using System;
using System.Collections.Generic;
using Godot;
using Script.Data.Effects;
using Script.Manager;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 触发匹配器（监听总线模式）：取代原 TurnManager 中分散的
    /// CollectTriggeredEffects / CollectManeuverTriggers。
    /// 职责：在 GameEvent 到达时，遍历场上卡 → 匹配 event↔EffectTiming 映射 + 条件判定
    /// → 生成 StackEffectInstance → 交回 TurnManager.RunTriggerPhase（既有排序窗口/入栈/让过流程）。
    /// 规则（与原 Collect 方法完全一致）：
    ///  1. 仅 CurrentZone==AreaZone 的卡可作触发源；
    ///  2. 触发按来源卡控制者(ControllerId)归属；
    ///  3. 收集后走 RunTriggerPhase 标准流程。
    ///
    /// 上下文取用约定（见 TriggerContext 注释）：
    ///  - 条件判定用 ctx.EffectiveContextCard（ContextCard 缺省回落 SourceCard）；
    ///  - 入栈目标/箭头只用 ctx.ContextCard（不回落，避免误指向引发者自身）；
    ///  - ctx.TriggeringPlayerId 供敌我向条件使用（-1 表示系统事件，如 PreCombat）。
    /// 【MVC 收口】原 static class → 实例：GetAllInstances/RunTriggerPhase/IsCircuitBroken/
    /// PushTriggerForCard 改为实例属性，由 TurnManager 初始化时注入；实例随 Composition Root 创建/释放。
    /// </summary>
    public class TriggerRegistry
    {
        private readonly GameEventBus _bus;

        /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
        public Script.Manager.EffectEngine EffectEngineRef { get; set; }

        /// <summary>未显式注入时回退静态 Instance（向后兼容）。</summary>
        private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;

        public TriggerRegistry(GameEventBus bus)
        {
            _bus = bus;
            // 总线分发：GameEventBus.Emit → 本类 HandleEvent（构造时即完成订阅，无需外部 SetDispatch）
            _bus.SetDispatch(HandleEvent);
        }

        // ---- 由 TurnManager 初始化时注入（实例属性，避免静态类直接依赖单例，方便测试） ----
        public Func<List<CardInstance>> GetAllInstances { get; set; }
        public Action<List<StackEffectInstance>> RunTriggerPhase { get; set; }

        /// <summary>
        /// D-1 熔断查询委托（由 TurnManager 注入 StackManager.IsCircuitBroken）。
        /// 为 true 时本次事件不收集任何触发，阻断"效果互相触发"的递归链。
        /// 未注入时默认不熔断（测试环境可不接堆叠）。
        /// </summary>
        public Func<bool> IsCircuitBroken { get; set; }

        /// <summary>
        /// 进场/现身触发的入栈委托（无排序窗口，直接入堆叠）。
        /// 参数：(来源卡, 时机, 上下文卡UID)。
        /// </summary>
        public System.Action<CardInstance, EffectTiming, CardInstance> PushTriggerForCard { get; set; }

        /// <summary>
        /// 【JC124·构筑级全局触发】收集器（由装配层注入 `DeckLevelTriggerRule.Collect`）：
        /// 在**常规场上实例扫描之后**调用，把"只要在开局卡组里就生效"的触发（与来源卡当前身处何处无关）
        /// 合成出来并**并入同一批收集结果**（走完全相同的触发管线：排序窗口 → 入堆叠 → 让过 → 结算）。
        /// </summary>
        public Func<EffectTiming, TriggerContext, EffectEngine, List<StackEffectInstance>> DeckLevelCollector { get; set; }

        /// <summary>
        /// 【2026-09 修复·JC043】**"没有合法目标的效果不得加入触发排序窗口"**：
        /// 收集阶段的前置判定（由 `TurnManager` 注入，复用 `GetTargetUidsForSpec` / `PlayerTargetPool`
        /// 等**同一套候选枚举**，不新增第二套规则）。返回 `false` ⇒ 该触发**不收集**
        /// （于是不进排序窗、不入堆叠）。未注入时视为"恒有目标"（行为与既有实现一致）。
        ///
        /// 用户实测："JC043 效果场上没有合法目标也在触发排序窗口中显示了，没有合法目标不能加入触发排序窗口"。
        /// </summary>
        public Func<CardInstance, EffectData, TriggerContext, bool> HasLegalTargetsForTrigger { get; set; }

        /// <summary>走排序窗口（RunTriggerPhase）的时机集合。</summary>
        /// 注意：进场触发(EnterTrigger)/现身触发(UncoverTrigger) 现已统一走 RunTriggerPhase 排序窗口
        /// （需求：触发效果收集后先经玩家排序窗口确认，再依次入堆叠，而非直接入栈）；
        /// 直接入栈时机集合（无排序窗口，经 PushTriggerForCard 委托）。
        /// 注意：当前设计下进场/现身触发不再走此快捷路径——TurnManager 已废弃 PushTriggerForCard
        /// （见 TurnManager 注释），进场/现身触发统一经 RunTriggerPhase（含选目标 EnsureTargetThenPush，
        /// 如 JC002 选本地区角色），详见 D39 修正说明。此集合保留为空以兼容潜在专用时机。
        private readonly HashSet<GameEvent> DirectPushEvents = new()
        {
        };

        /// <summary>
        /// GameEvent ↔ EffectTiming 映射。新增触发时机时，在此追加一行即可
        /// （前提：对应的 EffectTiming 与 GameEvent 值都已存在）。
        /// 每行右侧注释标明该时机的触发语义，便于后续维护者一目了然"这个映射对应什么触发"。
        /// 注意：映射登记 ≠ 已触发。业务点必须调用 GameEventBus.Emit(对应事件) 才会真正广播；
        ///      未 Emit 的事件即使在此登记，也不会有任何卡响应（属"结构就绪、触点待接"状态）。
        /// </summary>
        private readonly Dictionary<GameEvent, EffectTiming> EventToTiming = new()
        {
            // —— 已启用且业务点已 Emit 的时机 ——
            { GameEvent.OnCardPlayed,  EffectTiming.OnCardPlayed },   // 反应：任意卡被打出时（如 XG01 打出事务牌抓牌）
            { GameEvent.ManeuverTrigger, EffectTiming.ManeuverTrigger },// 机动：对抗前优先权窗口（玩家可选，非强制）
            { GameEvent.EnterTrigger,    EffectTiming.EnterTrigger },   // 进场：卡进入争夺区/场上（直接入栈，无排序窗口）
            { GameEvent.UncoverTrigger,  EffectTiming.UncoverTrigger }, // 现身：暗藏→公开（D37+：触点由 CardManager.RevealCard Emit，与 EnterTrigger 一并广播）
            { GameEvent.OnTurnEnded,   EffectTiming.OnTurnEnded },    // 回合结束：结束步骤广播（DoCleanup 已 Emit）

            // —— 2026-08-05 补全批次：枚举与映射已就绪，触点 Emit 见各 EffectTiming 注释【待接】 ——
            { GameEvent.OnCardDied,    EffectTiming.OnCardDied },     // 反应：卡进墓地死亡时（触点 CardManager.HandleInstanceDeath）
            { GameEvent.OnAreaWon,     EffectTiming.OnAreaWon },      // 触发：地区被赢取时（触点 AreaManager.ResolveAreaWon）
            { GameEvent.OnTurnStarted, EffectTiming.OnTurnStarted },  // 触发：回合开始/准备步骤（触点 TurnManager.NextTurn）
            { GameEvent.OnDraw,        EffectTiming.OnDraw },         // 反应：抓牌动作完成时（触点 EffectEngine.Instance.Draw）
            { GameEvent.OnDiscard,     EffectTiming.OnDiscard },      // 触发：手牌弃置时（触点 HandManager/EffectEngine.Instance.Discard）
            { GameEvent.OnMarkerPlaced,EffectTiming.OnMarkerPlaced }, // 触发：指示物放置时（触点 E4-#1 放置原语；高频须配 D-1 熔断）
            { GameEvent.OnSealed,      EffectTiming.OnSealed },       // 触发：卡被封印时（触点 D15 封印系统）
            { GameEvent.OnMove,        EffectTiming.OnMove },         // 触发：场上移动/位置变更时（触点移动原语）
            { GameEvent.OnCounter,     EffectTiming.OnCounter },     // 反应：卡/效果被终止(Counter)时（触点 E6-TODO 反制原语；关联 T9/D-2）
            { GameEvent.OnCardLeft,    EffectTiming.OnCardLeft },    // 触发：非死亡离场时（回手/封印移除等；触点离场判定）
            { GameEvent.OnCombatWon,   EffectTiming.OnCombatWon },   // 触发：战斗对抗获胜时（触点 CombatManager.ResolveWinner → TriggerEmitPoints.OnCombatWon；条件 CombatWinnerIsSelf）
            { GameEvent.OnPowerCombatEnded, EffectTiming.OnPowerCombatEnded }, // 【P3】触发：势力对抗结束时（触点 CombatManager 势力分支 → TriggerEmitPoints.PowerCombatEnded；声望用）
            { GameEvent.OnInvestigationWon, EffectTiming.OnInvestigationWon }, // 【P3】触发：调查对抗获胜时（触点 CombatManager 调查分支 → TriggerEmitPoints.OnInvestigationWon；条件 WinnerIsSelfInSameArea）
            { GameEvent.OnLatent,      EffectTiming.OnLatent },        // 【P3】触发：本卡被潜伏/翻面为暗藏者时（触点装配层 CovertChanged → TriggerEmitPoints.OnLatent；自身语义）
        };

        /// <summary>
        /// 【P3·K13】把一个"关键词触发模板"克隆成**本条收集专用**的 `EffectData`（并带上可辨识的 EffectId）。
        /// 不能直接复用模板实例：模板是共享的（`KeywordEffects.RuntimeTriggerTemplates`），
        /// 而 EffectId 会参与 K23 次数登记与日志辨识，逐次克隆避免相互污染。
        /// </summary>
        private static Script.Data.Effects.EffectData CloneKeywordTemplate(Script.Data.Effects.EffectData src, string effectId)
            => new Script.Data.Effects.EffectData
            {
                EffectId = effectId,
                Timing = src.Timing,
                Forced = src.Forced,
                Condition = src.Condition,
                Target = src.Target,
                Actions = src.Actions,
                Duration = src.Duration,
                RequireSourceVersionUnchanged = src.RequireSourceVersionUnchanged,
                FaqNote = src.FaqNote,
            };

        /// <summary>
        /// 总线入口：由 GameEventBus.Dispatch 在每次事件广播时调用。统一负责"事件→时机→收集触发"的匹配。
        /// 处理流程：
        ///  1. 若未注入 GetAllInstances/RunTriggerPhase（测试/未初始化）则跳过；
        ///  2. 查 EventToTiming 字典：找不到映射的事件直接跳过（保证"未登记即不触发"，不会误伤）；
        ///  3. D-1 熔断检查：堆叠累计压入达上限（IsCircuitBroken()==true）则本事件不收集任何触发，阻断效果递归；
        ///  4. 遍历场上 AreaZone 卡，按 ctx.EffectiveContextCard 做条件判定，命中即生成 StackEffectInstance；
        ///  5. 反应/机动类时机走 RunTriggerPhase 排序窗口；进场/现身类在 DirectPushEvents 中直接入栈。
        /// 注意：本方法只"收集并派发触发"，不负责 Emit 事件本身；事件的 Emit 由各业务点（见 EffectTiming 注释【待接】）完成。
        /// </summary>
        public void HandleEvent(GameEvent evt, TriggerContext ctx)
        {
            if (GetAllInstances == null || RunTriggerPhase == null)
            {
                GD.Print($"[TriggerRegistry] ⚠ 未注入 GetAllInstances/RunTriggerPhase，事件 {evt} 跳过");
                return;
            }
            if (!EventToTiming.TryGetValue(evt, out var timing))
            {
                GD.Print($"[TriggerRegistry] ⚠ 事件 {evt} 暂无 EffectTiming 映射，跳过");
                return;
            }
            // D-1 熔断：堆叠累计压入达上限时不再收集任何触发，阻断效果递归链
            if (IsCircuitBroken != null && IsCircuitBroken())
            {
                GD.PrintErr($"⛔ [TriggerRegistry] 堆叠已熔断，事件 {evt} 不收集触发（防御性阻断，疑似效果递归）");
                return;
            }

            ctx ??= new TriggerContext();

            var collected = new List<StackEffectInstance>();
            int scanned = 0, skippedZone = 0, skippedCond = 0;
            bool isManeuver = (timing == EffectTiming.ManeuverTrigger);

            // 条件判定用的"被检查对象"：ContextCard 缺省时回落到 SourceCard
            // （如 EnterTrigger 无结附目标时，仍应让谓词拿到刚进场的那张卡）
            var conditionCard = ctx.EffectiveContextCard;

            GD.Print($"[Trigger] 事件 {evt} 上下文: {ctx}");

            foreach (var card in GetAllInstances())
            {
                if (card == null) continue;
                var tmpl = card.PlayerTemplate;
                if (tmpl == null) continue;
                // 【P3·K20】"印刷文本框视作空白"（JC101）：被屏蔽的卡**失去效果** → 其效果不参与触发收集。
                // 口径（用户 2026-09）：**不影响已经发动的效果**——已在堆叠上的条目照常结算（本处只管"收集"）。
                if (EE?.IsTextBlank(card) == true) continue;

                // 规则1：触发源区判定——交由内层 foreach 对每个效果用 IsEffectActiveInZone 做效果粒度判定
                // （方案C：附属卡默认仅 AttachedZone；角色卡仅 AreaZone；显式声明 Scope 的效果按声明）。
                // 【方案C 后续改动方向】旧整卡判定（isAttachZone/allowAttach/isAttachment + 整卡 return）
                // 已收敛为下方 foreach 内的 IsEffectActiveInZone(data, card) 效果粒度判定；
                // 进场/现身例外（规则1.6）仍在 foreach 内单独保留（仅触发来源卡自身），勿在此重复写区判断。

                var effectDataList = tmpl.EffectDataList ?? new Godot.Collections.Array<Script.Data.Effects.EffectData>();
                // 【触发语义·场景校正】触发只判"条件是否满足过"，不看来源此刻身在何处：
                // 当扫描到的正是"事件主体卡"（或事件本身无主体卡，如赢取地区）时，用上下文带来的
                // **触发时区域**替代实时 CurrentZone 做生效区判定。覆盖两类必须成立的场景：
                //   · 死亡触发：卡进入墓地之后才有窗口 → 角色卡（默认生效区=争夺区）不会被误杀；
                //   · 进场即死：进场触发窗口打开时卡已在墓地 → 同样照常收集。
                // 旁观者（反应类，如 XG001 看别人打出事务）不是事件主体，仍按实时区域判定。
                bool isEventSubject = (ctx.SourceCard == null || card == ctx.SourceCard);
                CardZone? zoneAtTrigger = isEventSubject ? ctx.SourceZoneAtTrigger : null;
                // 【K13】本卡的"袭击"汇总：卡面自带袭击（通过校验者）之和 + 运行期授予的袭击点数
                int strikeSum = 0;
                Script.Data.Effects.EffectData strikeTemplate = null;
                foreach (var data in effectDataList)
                {
                    if (data == null) continue;
                    if (data.Timing != timing) continue;
                    // 【JC124·构筑级全局触发】带该声明的效果**不走**"扫描场上实例"的常规收集：
                    // 它由 `DeckLevelTriggerRule` 按"已登记玩家"合成（来源＝卡组里的那张实例）。
                    // 若在此同时收集，那张牌在场时会**重复收集**（同一死亡角色被回场两次）。
                    if (data.DeckLevelRegistration) continue;
                    // 方案C：触发效果生效区判定（效果粒度）。默认：附属=AttachedZone，角色=AreaZone；
                    // 显式声明 Scope 的效果按声明（如未来墓地/手牌触发）。不在此区则跳过该效果。
                    if (EE == null || !EffectEngine.IsEffectActiveInZone(data, card, zoneAtTrigger))
                    {
                        skippedZone++;
                        continue;
                    }

                    // 规则1.6（D40）：进场触发(EnterTrigger)/现身触发(UncoverTrigger) 属"自身进场/现身"语义，
                    // 仅当触发源卡 == 事件来源卡时才收集——避免任意卡进场/现身误触发他人身上的进场/现身类效果
                    // （如：公路骑士现身误触发巨石阵看护人的 JC002）。反应类时机（OnCardPlayed/PlayedSorcery 等）
                    // 不在此限，仍对所有场上卡全局扫描。
                    // 【P3】"被潜伏/翻面时"(OnLatent) 同属**自身**语义（只有被潜伏的那张卡自己的效果成立）。
                    // 【P0·修复】"进入/移动到某个地区"(OnMove) 也同属**自身**语义：卡文都是
                    // 「当**此卡**进入一个地区时」（JC061/JC087），而 `OnMove` 是"任何卡位置变更都广播"的事件
                    // ⇒ 若不在此限，别人的角色移动/进场会误触发本卡的效果（实测：对手暗藏者进场即触发 JC061）。
                    // 若将来出现"当**其他**角色进入本地区时"这类**旁观**效果，需另加时机或谓词（不在此放开）。
                    if ((evt == GameEvent.EnterTrigger || evt == GameEvent.UncoverTrigger
                         || evt == GameEvent.OnLatent || evt == GameEvent.OnMove)
                        && card != ctx.SourceCard)
                    {
                        continue;
                    }

                    // 规则1.5：触发对来源卡暗藏（背面朝上）状态的要求（核心需求：暗藏潜伏机制 / D27）
                    //  FaceUp    ：暗藏(IsCovert)时跳过；
                    //  CovertOnly：非暗藏(正面)时跳过；
                    //  Either    ：不限。
                    bool isCovert = card.IsCovert;
                    if (data.TriggerCovertMode == CovertTriggerMode.FaceUp && isCovert)
                    {
                        GD.Print($"[Trigger] ⚠ {tmpl.CardName} 暗藏(背面)，跳过触发 {data.EffectId}（要求正面朝上）");
                        continue;
                    }
                    if (data.TriggerCovertMode == CovertTriggerMode.CovertOnly && !isCovert)
                    {
                        GD.Print($"[Trigger] ⚠ {tmpl.CardName} 正面朝上，跳过触发 {data.EffectId}（要求暗藏态）");
                        continue;
                    }

                    scanned++;

                    // 条件判定（反应触发用 ContextCard 作上下文，如 PlayedSorcery；
                    // ContextCard 为空时回落 SourceCard，见 TriggerContext.EffectiveContextCard）
                    // 威名触发(OnCombatWon)需传入 ctx.TriggeringPlayerId 供 WinnerIsSelf 判定（D1：Emit 时未填 SourceCard）。
                    // 同时传入 ctx.TargetAreaId 供 WinnerInSameArea 判定（Y 方案：获胜地区 == 本卡所在地区才入栈）。
                    if (EE == null || !EE.EvaluateCondition(data.Condition?.Predicate, card, conditionCard, conditionCard, ctx.TriggeringPlayerId, ctx.TargetAreaId, data.Condition?.Param))
                    {
                        skippedCond++;
                        GD.Print($"[Trigger] 触发条件不满足（EffectId={data.EffectId}, 来源={tmpl.CardName}, 事件={evt}），跳过");
                        continue;
                    }

                    // 【2026-09 修复·K23/A57·收集阶段次数门禁】触发类能力的次数上限（每回合/每局）必须
                    // **在收集阶段**就拦下：此前只在"结算前"检查 ⇒ 已达上限的触发仍会入堆叠
                    // （并弹触发排序窗 / 目标玩家窗），玩家看到的是"还能再发动一次"，只是结算时被跳过
                    // （用户实测 JC031 唤灵师："可以一回合内多次发动效果，虽然只有一次生效了，
                    //  但是需要后续直接不能发动"）。
                    // 判据与结算前的检查**同源**（`CardInstance.IsAbilityLimitReached` /
                    // `IsGameAbilityLimitReached`）；结算前那次保留为"同一批事件同时收集"的兜底。
                    if (data.MaxUsesPerTurn > 0 && card.IsAbilityLimitReached(data.EffectId, data.MaxUsesPerTurn))
                    {
                        skippedCond++;
                        GD.Print($"[Trigger] ⏹ 本回合次数已用尽（EffectId={data.EffectId}, 来源={tmpl.CardName}，" +
                                 $"已 {card.GetAbilityUsesThisTurn(data.EffectId)}/{data.MaxUsesPerTurn}）→ 不收集（不发动）");
                        continue;
                    }
                    if (data.MaxUsesPerGame > 0 && card.IsGameAbilityLimitReached(data.EffectId, data.MaxUsesPerGame))
                    {
                        skippedCond++;
                        GD.Print($"[Trigger] ⏹ 本局次数已用尽（EffectId={data.EffectId}, 来源={tmpl.CardName}，" +
                                 $"已 {card.GetAbilityUsesThisGame(data.EffectId)}/{data.MaxUsesPerGame}）→ 不收集（不发动）");
                        continue;
                    }

                    // 【2026-09·K26 审计补口】阶段排除（`ExcludedPhases`）：与**候选侧**（`ActivateEligibility`）
                    // 和**权威侧**（`EffectEngine.ActivateAbility`）同解 —— 处于被排除的阶段时**不收集**该触发。
                    // 此前只在激活/结算侧判，触发类效果虽然当前没有卡声明该字段（审计确认），但属**潜在缺口**
                    // （声明了却仍会进排序窗、只在结算时被跳过，与 A76/A82 同款"收集期该拦而没拦"）。
                    if (PhaseGateRule.IsExcluded(data, EE?.TurnManagerRef))
                    {
                        skippedCond++;
                        GD.Print($"[Trigger] ⏹ 阶段排除（EffectId={data.EffectId}, 来源={tmpl.CardName}, 事件={evt}）→ 不收集");
                        continue;
                    }

                    // 【2026-09 修复·JC043】"**没有合法目标的效果不得加入触发排序窗口**"（用户实测）：
                    // 需要选目标、但当前**一个合法候选都没有** ⇒ 不收集（用户看到的"排序窗里躺着一条
                    // 点了也没法选目标"的条目就此消失）。候选枚举复用 TurnManager 的同一套实现。
                    if (HasLegalTargetsForTrigger != null && !HasLegalTargetsForTrigger(card, data, ctx))
                    {
                        skippedCond++;
                        GD.Print($"[Trigger] ⏹ 无合法目标（EffectId={data.EffectId}, 来源={tmpl.CardName}, 事件={evt}）→ 不收集（不进排序窗）");
                        continue;
                    }

                    // 【K1】动态值 X 预判（用户口径：X=0 时该效果**不算发动**）：
                    // 在**收集阶段**就用事件上下文求值，为 0（或表达式未登记/依赖未实现）→ 不收集该触发。
                    // 【2026-09·JC048 用户口径】`ValueAtResolve=true` 时**跳过本门禁**：该效果的 X 依赖
                    // "同时间另一条效果先结算"的结果（JC048 先放时间标志、再按地区标志数造成伤害），
                    // 收集发生在放标志**之前** ⇒ 首个准备步骤 X=0 会把整条伤害跳过（实测表现为
                    // "只有牺牲那一回合才造成伤害"）。此类效果留到结算时求值（为 0 则自然不做任何事）。
                    if (EE != null && !string.IsNullOrWhiteSpace(data.ValueExpr) && !data.ValueAtResolve)
                    {
                        var st = EE.EvaluateDynamicValue(data, card, conditionCard, ctx.TargetAreaId,
                            ctx.ExtraInt, out _, out string dynReason);
                        if (st == EffectEngine.DynamicValueState.Blocked)
                        {
                            GD.Print($"[Trigger] ⏹ 动态值 X 不可用（EffectId={data.EffectId}, 来源={tmpl.CardName}）→ 不收集（不发动）：{dynReason}");
                            continue;
                        }
                    }

                    // 【K13·袭击数值叠加】"袭击X"不逐条收集：先把**通过校验**的袭击来源（卡面自带 ∪ 运行期授予）
                    // 合并为一个触发（总和＝各来源之和 + 运行期授予的袭击点数）——见本卡循环结束后的合成。
                    if (data.IsStrike)
                    {
                        strikeSum += data.StrikeAmount;
                        strikeTemplate ??= data;
                        GD.Print($"[Trigger] 袭击来源记入汇总：{tmpl.CardName} +{data.StrikeAmount}（当前合计 {strikeSum}）");
                        continue;
                    }

                    // 规则2：归属来源卡控制者（D32 修正：ControllerId 为窗口分组依据）
                    var inst = new StackEffectInstance
                    {
                        UID = Guid.NewGuid().ToString("N"),
                        Source = card,
                        OwnerPlayerId = card.ControllerId,
                        ControllerId = card.ControllerId,
                        EffectDataRef = data,
                        TargetCardUid = ctx.ContextCard?.UID ?? "",
                        HasValidTarget = true,
                        IsAreaTarget = isManeuver,   // 仅机动取目标地区
                        // 【K1】本次事件携带的数量（供"X＝此次弃牌的数量"等求值）
                        EventAmount = ctx.ExtraInt,
                        // 对抗获胜方玩家 ID 透传（"X对抗获胜"类触发：威名等）。
                        // 经 PushTriggerToStack 落入 StackEntry.WinnerId，最终 ResolveEffect(winnerId) 供 WinnerIsSelf 判定。
                        WinnerId = ctx.TriggeringPlayerId,
                        // 对抗获胜地区 UID 透传（Y 方案：威名等需要在收集阶段即按地区过滤的触发）。
                        // 经 PushTriggerToStack 落入 StackEntry.TargetAreaId，最终供 WinnerInSameArea 判定。
                        TargetAreaId = ctx.TargetAreaId,
                        // 【触发语义】"触发时所在地区"（死亡=死亡时所处地区 / 进场=进入的地区 / 赢取=被赢取地区）：
                        // 供"本地区"取候选与结算复检使用，避免用已离场来源的 null ZoneDetailId 当基准。
                        SourceAreaAtTrigger = isEventSubject ? ctx.SourceAreaAtTrigger : null,
                    };
                    collected.Add(inst);
                    GD.Print($"[Trigger] 触发收集: EffectId={data.EffectId}, 来源={tmpl.CardName}, 控制者=P{card.ControllerId}, 事件={evt}");
                }

                // 【K13·袭击合成】本卡的袭击来源（自带 ∪ 授予）汇总后**只收集一个**触发，伤害＝总和。
                // 用户 2026-09 口径：多个袭击同时存在时**数值叠加**（已有袭击1 被授予袭击1 → 变为袭击2）。
                // 仅在"现身触发"且该卡是事件主体时合成（现身袭击＝该角色自己现身）。
                if (timing == EffectTiming.UncoverTrigger && card == ctx.SourceCard)
                {
                    int grantedStrike = card.GetAttribute("袭击");   // 运行期授予（如 JC038 光环）
                    int totalStrike = strikeSum + grantedStrike;
                    if (totalStrike > 0)
                    {
                        var baseData = strikeTemplate ?? Script.Data.Effects.KeywordEffects.Strike(1);
                        var merged = Script.Data.Effects.KeywordEffects.StrikeWithAmount(baseData, totalStrike);
                        collected.Add(new StackEffectInstance
                        {
                            UID = Guid.NewGuid().ToString("N"),
                            Source = card,
                            OwnerPlayerId = card.ControllerId,
                            ControllerId = card.ControllerId,
                            EffectDataRef = merged,
                            TargetCardUid = ctx.ContextCard?.UID ?? "",
                            HasValidTarget = true,
                            IsAreaTarget = false,
                            EventAmount = ctx.ExtraInt,
                            WinnerId = ctx.TriggeringPlayerId,
                            TargetAreaId = ctx.TargetAreaId,
                            SourceAreaAtTrigger = ctx.SourceAreaAtTrigger,
                        });
                        GD.Print($"[Trigger] 【K13 袭击叠加】{tmpl.CardName}：自带 {strikeSum} + 授予 {grantedStrike} = **袭击{totalStrike}**（合并为一个触发）");
                    }
                }

                // 【P3·K13】运行期授予的**触发类关键词**参与触发收集（如 JC024 授予的【机动】、JC089 授予的【威名】）：
                // 卡面固有的关键词由上面的 `EffectDataList` 扫描收集；运行期授予的没有对应 EffectData，
                // 故在此**合成**该关键词的触发形态模板（`KeywordEffects.RuntimeTriggerTemplates`），
                // 并复用同一套判定（生效区 / 暗藏模式 / 条件）。卡面已固有该关键词时跳过（避免与上方扫描重复）。
                foreach (var kv in Script.Data.Effects.KeywordEffects.RuntimeTriggerTemplates())
                {
                    var template = kv.Value;
                    if (template == null || template.Timing != timing) continue;
                    if (!card.HasKeyword(kv.Key)) continue;                    // 未被授予（运行期关键字集合）
                    if (tmpl.HasInherentKeyword(kv.Key)) continue;             // 卡面固有 → 已由上方扫描收集
                    if (!EffectEngine.IsEffectActiveInZone(template, card, zoneAtTrigger)) { skippedZone++; continue; }
                    if (template.TriggerCovertMode == CovertTriggerMode.FaceUp && card.IsCovert) continue;
                    if (template.TriggerCovertMode == CovertTriggerMode.CovertOnly && !card.IsCovert) continue;
                    if (EE == null || !EE.EvaluateCondition(template.Condition?.Predicate, card, conditionCard,
                            conditionCard, ctx.TriggeringPlayerId, ctx.TargetAreaId))
                    {
                        skippedCond++;
                        continue;
                    }
                    var synth = CloneKeywordTemplate(template, $"Keyword_{kv.Key}");
                    collected.Add(new StackEffectInstance
                    {
                        UID = Guid.NewGuid().ToString("N"),
                        Source = card,
                        OwnerPlayerId = card.ControllerId,
                        ControllerId = card.ControllerId,
                        EffectDataRef = synth,
                        TargetCardUid = ctx.ContextCard?.UID ?? "",
                        HasValidTarget = true,
                        IsAreaTarget = (timing == EffectTiming.ManeuverTrigger),   // 机动＝取目标地区
                        EventAmount = ctx.ExtraInt,
                        WinnerId = ctx.TriggeringPlayerId,
                        TargetAreaId = ctx.TargetAreaId,
                        SourceAreaAtTrigger = isEventSubject ? ctx.SourceAreaAtTrigger : null,
                    });
                    GD.Print($"[Trigger] 【K13 关键词触发】{tmpl.CardName}：运行期授予的【{kv.Key}】按 `{timing}` 收集");
                }
            }

            // 【P3·一次性待触发承诺】把"本回合中，当你…下一次…时，你可以…"类承诺在**时机出现时**兑现一次：
            // 承诺由 `EffectEngine.RegisterPending` 登记（效果声明 `EffectData.Pending`），此处按
            // "时机一致 ∧ 归属玩家 == 本次事件相关玩家"取用，并**立即消费**（"下一次"只兑现一次；
            // 与玩家最终是否执行动作无关）。兑现出来的触发与普通触发**同一条管线**（排序窗 → 入堆叠 → 让过 → 结算）。
            var pendings = EE?.CollectPending(timing, ctx.TriggeringPlayerId);
            if (pendings != null)
            {
                foreach (var rec in pendings)
                {
                    var spec = rec.Spec;
                    if (spec?.Actions == null || spec.Actions.Count == 0) { EE.ConsumePending(rec); continue; }
                    var pendingData = new Script.Data.Effects.EffectData
                    {
                        EffectId = $"Pending_{timing}_{rec.Source?.UID}",
                        Timing = timing,
                        Condition = new Script.Data.Effects.EffectCondition { Predicate = "Always" },
                        Target = new Script.Data.Effects.TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                        Actions = spec.Actions,
                        Forced = !spec.Optional,          // 卡文"你可以" → 非强制（可在排序窗放弃）
                        Duration = "瞬时",
                        FaqNote = "一次性承诺兑现（本回合下一次…）",
                    };
                    collected.Add(new StackEffectInstance
                    {
                        UID = Guid.NewGuid().ToString("N"),
                        Source = rec.Source,
                        OwnerPlayerId = rec.ControllerId,
                        ControllerId = rec.ControllerId,
                        EffectDataRef = pendingData,
                        TargetCardUid = ctx.ContextCard?.UID ?? "",
                        HasValidTarget = false,
                        IsAreaTarget = false,
                        WinnerId = ctx.TriggeringPlayerId,
                        TargetAreaId = ctx.TargetAreaId,
                    });
                    EE.ConsumePending(rec);
                    GD.Print($"[Trigger] 📜 承诺兑现：{rec.Source?.PlayerTemplate?.CardName} 在「{timing}」时机（收集为触发）");
                }
            }

            // 【JC124·构筑级全局触发】"只要这张牌一开始加入玩家卡组就能触发"的触发（构筑级登记）：
            // 与普通触发共用同一条管线，故直接并入本批收集结果（判定复用 `EffectEngine.EvaluateCondition`）。
            if (DeckLevelCollector != null)
            {
                var deckLevel = DeckLevelCollector(timing, ctx, EE);
                if (deckLevel != null && deckLevel.Count > 0)
                {
                    collected.AddRange(deckLevel);
                    GD.Print($"[Trigger] 事件 {evt} 构筑级（卡组级）触发收集：{deckLevel.Count} 条");
                }
            }

            GD.Print($"[Trigger] 事件 {evt} 扫描完成: 扫描={scanned}, 区域过滤跳过={skippedZone}, 条件不满足={skippedCond}, 收集={collected.Count}");
            if (collected.Count == 0) return;

            // 进场/现身触发：直接入堆叠（无排序窗口，走 PushTriggerForCard 委托）
            if (DirectPushEvents.Contains(evt))
            {
                if (PushTriggerForCard == null)
                {
                    GD.Print($"[TriggerRegistry] ⚠ 未注入 PushTriggerForCard，进场/现身触发 {evt} 跳过");
                    return;
                }
                foreach (var inst in collected)
                {
                    // 传 ContextCard（可能为 null）而非 conditionCard：
                    // 入栈目标/箭头只应指向真实的"被作用对象"，不能因回落而误指向进场卡自身。
                    PushTriggerForCard(inst.Source, timing, ctx.ContextCard);
                }
                GD.Print($"[Trigger] {evt} 直接入堆叠完成（{collected.Count} 条）");
                return;
            }

            // 反应/机动触发：走标准排序窗口 → 入栈 → 双方让过
            RunTriggerPhase(collected);
        }
    }
}
