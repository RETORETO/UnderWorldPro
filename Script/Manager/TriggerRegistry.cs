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
    /// → 生成 TriggerEffectInstance → 交回 TurnManager.RunTriggerPhase（既有排序窗口/入栈/让过流程）。
    /// 规则（与原 Collect 方法完全一致）：
    ///  1. 仅 CurrentZone==AreaZone 的卡可作触发源；
    ///  2. 触发按来源卡控制者(ControllerId)归属；
    ///  3. 收集后走 RunTriggerPhase 标准流程。
    ///
    /// 上下文取用约定（见 TriggerContext 注释）：
    ///  - 条件判定用 ctx.EffectiveContextCard（ContextCard 缺省回落 SourceCard）；
    ///  - 入栈目标/箭头只用 ctx.ContextCard（不回落，避免误指向引发者自身）；
    ///  - ctx.TriggeringPlayerId 供敌我向条件使用（-1 表示系统事件，如 PreCombat）。
    /// </summary>
    public static class TriggerRegistry
    {
        // ---- 由 TurnManager 初始化时注入（避免静态类直接依赖单例，方便测试） ----
        public static Func<List<CardInstance>> GetAllInstances { get; set; }
        public static Action<List<TriggerEffectInstance>> RunTriggerPhase { get; set; }

        /// <summary>
        /// D-1 熔断查询委托（由 TurnManager 注入 StackManager.IsCircuitBroken）。
        /// 为 true 时本次事件不收集任何触发，阻断"效果互相触发"的递归链。
        /// 未注入时默认不熔断（测试环境可不接堆叠）。
        /// </summary>
        public static Func<bool> IsCircuitBroken { get; set; }

        /// <summary>
        /// 进场/现身触发的入栈委托（无排序窗口，直接入堆叠）。
        /// 参数：(来源卡, 时机, 上下文卡UID)。
        /// </summary>
        public static System.Action<CardInstance, EffectTiming, CardInstance> PushTriggerForCard { get; set; }

        /// <summary>走排序窗口（RunTriggerPhase）的时机集合。</summary>
        /// 注意：进场触发(EnterPlay)/现身触发(Uncover) 现已统一走 RunTriggerPhase 排序窗口
        /// （需求：触发效果收集后先经玩家排序窗口确认，再依次入堆叠，而非直接入栈）；
        /// 直接入栈时机集合（无排序窗口，经 PushTriggerForCard 委托）。
        /// 注意：当前设计下进场/现身触发不再走此快捷路径——TurnManager 已废弃 PushTriggerForCard
        /// （见 TurnManager 注释），进场/现身触发统一经 RunTriggerPhase（含选目标 EnsureTargetThenPush，
        /// 如 JC002 选本地区角色），详见 D39 修正说明。此集合保留为空以兼容潜在专用时机。
        private static readonly HashSet<GameEvent> DirectPushEvents = new()
        {
        };

        /// <summary>
        /// GameEvent ↔ EffectTiming 映射。新增触发时机时，在此追加一行即可
        /// （前提：对应的 EffectTiming 与 GameEvent 值都已存在）。
        /// 每行右侧注释标明该时机的触发语义，便于后续维护者一目了然"这个映射对应什么触发"。
        /// 注意：映射登记 ≠ 已触发。业务点必须调用 GameEventBus.Emit(对应事件) 才会真正广播；
        ///      未 Emit 的事件即使在此登记，也不会有任何卡响应（属"结构就绪、触点待接"状态）。
        /// </summary>
        private static readonly Dictionary<GameEvent, EffectTiming> EventToTiming = new()
        {
            // —— 已启用且业务点已 Emit 的时机 ——
            { GameEvent.OnCardPlayed,  EffectTiming.OnCardPlayed },   // 反应：任意卡被打出时（如 XG01 打出事务牌抓牌）
            { GameEvent.PreCombat,     EffectTiming.ManeuverTrigger },// 机动：对抗前优先权窗口（玩家可选，非强制）
            { GameEvent.EnterPlay,     EffectTiming.EnterTrigger },   // 进场：卡进入争夺区/场上（直接入栈，无排序窗口）
            { GameEvent.Uncover,       EffectTiming.UncoverTrigger }, // 现身：暗藏→公开（D37+：触点由 CardManager.RevealCard Emit，与 EnterPlay 一并广播）
            { GameEvent.OnTurnEnded,   EffectTiming.OnTurnEnded },    // 回合结束：结束步骤广播（DoCleanup 已 Emit）

            // —— 2026-08-05 补全批次：枚举与映射已就绪，触点 Emit 见各 EffectTiming 注释【待接】 ——
            { GameEvent.OnCardDied,    EffectTiming.OnCardDied },     // 反应：卡进墓地死亡时（触点 CardManager.HandleInstanceDeath）
            { GameEvent.OnAreaWon,     EffectTiming.OnAreaWon },      // 触发：地区被赢取时（触点 AreaManager.ResolveAreaWon）
            { GameEvent.OnTurnStarted, EffectTiming.OnTurnStarted },  // 触发：回合开始/准备步骤（触点 TurnManager.NextTurn）
            { GameEvent.OnDraw,        EffectTiming.OnDraw },         // 反应：抓牌动作完成时（触点 EffectEngine.Draw）
            { GameEvent.OnDiscard,     EffectTiming.OnDiscard },      // 触发：手牌弃置时（触点 HandManager/EffectEngine.Discard）
            { GameEvent.OnMarkerPlaced,EffectTiming.OnMarkerPlaced }, // 触发：指示物放置时（触点 E4-#1 放置原语；高频须配 D-1 熔断）
            { GameEvent.OnSealed,      EffectTiming.OnSealed },       // 触发：卡被封印时（触点 D15 封印系统）
            { GameEvent.OnMove,        EffectTiming.OnMove },         // 触发：场上移动/位置变更时（触点移动原语）
            { GameEvent.OnCounter,     EffectTiming.OnCounter },     // 反应：卡/效果被终止(Counter)时（触点 E6-TODO 反制原语；关联 T9/D-2）
            { GameEvent.OnCardLeft,    EffectTiming.OnCardLeft },    // 触发：非死亡离场时（回手/封印移除等；触点离场判定）
            { GameEvent.CombatWon,     EffectTiming.OnCombatWon },   // 触发：战斗对抗获胜时（触点 CombatManager.ResolveWinner → TriggerEmitPoints.CombatWon；条件 CombatWinnerIsSelf）
        };

        /// <summary>
        /// 总线入口：由 GameEventBus.Dispatch 在每次事件广播时调用。统一负责"事件→时机→收集触发"的匹配。
        /// 处理流程：
        ///  1. 若未注入 GetAllInstances/RunTriggerPhase（测试/未初始化）则跳过；
        ///  2. 查 EventToTiming 字典：找不到映射的事件直接跳过（保证"未登记即不触发"，不会误伤）；
        ///  3. D-1 熔断检查：堆叠累计压入达上限（IsCircuitBroken()==true）则本事件不收集任何触发，阻断效果递归；
        ///  4. 遍历场上 AreaZone 卡，按 ctx.EffectiveContextCard 做条件判定，命中即生成 TriggerEffectInstance；
        ///  5. 反应/机动类时机走 RunTriggerPhase 排序窗口；进场/现身类在 DirectPushEvents 中直接入栈。
        /// 注意：本方法只"收集并派发触发"，不负责 Emit 事件本身；事件的 Emit 由各业务点（见 EffectTiming 注释【待接】）完成。
        /// </summary>
        public static void HandleEvent(GameEvent evt, TriggerContext ctx)
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

            var collected = new List<TriggerEffectInstance>();
            int scanned = 0, skippedZone = 0, skippedCond = 0;
            bool isManeuver = (timing == EffectTiming.ManeuverTrigger);

            // 条件判定用的"被检查对象"：ContextCard 缺省时回落到 SourceCard
            // （如 EnterPlay 无结附目标时，仍应让谓词拿到刚进场的那张卡）
            var conditionCard = ctx.EffectiveContextCard;

            GD.Print($"[Trigger] 事件 {evt} 上下文: {ctx}");

            foreach (var card in GetAllInstances())
            {
                if (card == null) continue;
                var tmpl = card.PlayerTemplate;
                if (tmpl == null) continue;

                // 规则1：触发源区判定——交由内层 foreach 对每个效果用 IsEffectActiveInZone 做效果粒度判定
                // （方案C：附属卡默认仅 AttachedZone；角色卡仅 AreaZone；显式声明 Scope 的效果按声明）。
                // 【方案C 后续改动方向】旧整卡判定（isAttachZone/allowAttach/isAttachment + 整卡 return）
                // 已收敛为下方 foreach 内的 IsEffectActiveInZone(data, card) 效果粒度判定；
                // 进场/现身例外（规则1.6）仍在 foreach 内单独保留（仅触发来源卡自身），勿在此重复写区判断。

                var effectDataList = tmpl.EffectDataList ?? new Godot.Collections.Array<Script.Data.Effects.EffectData>();
                foreach (var data in effectDataList)
                {
                    if (data == null) continue;
                    if (data.Timing != timing) continue;
                    // 方案C：触发效果生效区判定（效果粒度）。默认：附属=AttachedZone，角色=AreaZone；
                    // 显式声明 Scope 的效果按声明（如未来墓地/手牌触发）。不在此区则跳过该效果。
                    if (!Script.Manager.EffectEngine.IsEffectActiveInZone(data, card))
                    {
                        skippedZone++;
                        continue;
                    }

                    // 规则1.6（D40）：进场触发(EnterPlay)/现身触发(Uncover) 属"自身进场/现身"语义，
                    // 仅当触发源卡 == 事件来源卡时才收集——避免任意卡进场/现身误触发他人身上的进场/现身类效果
                    // （如：公路骑士现身误触发巨石阵看护人的 JC002）。反应类时机（OnCardPlayed/PlayedSorcery 等）
                    // 不在此限，仍对所有场上卡全局扫描。
                    if ((evt == GameEvent.EnterPlay || evt == GameEvent.Uncover) && card != ctx.SourceCard)
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
                    if (!EffectEngine.EvaluateCondition(data.Condition?.Predicate, card, conditionCard, conditionCard, ctx.TriggeringPlayerId, ctx.TargetAreaId))
                    {
                        skippedCond++;
                        GD.Print($"[Trigger] 触发条件不满足（EffectId={data.EffectId}, 来源={tmpl.CardName}, 事件={evt}），跳过");
                        continue;
                    }

                    // 规则2：归属来源卡控制者（D32 修正：ControllerId 为窗口分组依据）
                    var inst = new TriggerEffectInstance
                    {
                        UID = Guid.NewGuid().ToString("N"),
                        Source = card,
                        OwnerPlayerId = card.ControllerId,
                        ControllerId = card.ControllerId,
                        EffectDataRef = data,
                        TargetCardUid = ctx.ContextCard?.UID ?? "",
                        HasValidTarget = true,
                        IsAreaTarget = isManeuver,   // 仅机动取目标地区
                        // 对抗获胜方玩家 ID 透传（"X对抗获胜"类触发：威名等）。
                        // 经 PushTriggerToStack 落入 StackEntry.WinnerId，最终 ResolveEffect(winnerId) 供 WinnerIsSelf 判定。
                        WinnerId = ctx.TriggeringPlayerId,
                        // 对抗获胜地区 UID 透传（Y 方案：威名等需要在收集阶段即按地区过滤的触发）。
                        // 经 PushTriggerToStack 落入 StackEntry.TargetAreaId，最终供 WinnerInSameArea 判定。
                        TargetAreaId = ctx.TargetAreaId,
                    };
                    collected.Add(inst);
                    GD.Print($"[Trigger] 触发收集: EffectId={data.EffectId}, 来源={tmpl.CardName}, 控制者=P{card.ControllerId}, 事件={evt}");
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
