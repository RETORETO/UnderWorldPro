using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using System.Linq;

namespace Script.Manager
{
    /// <summary>
    /// EffectEngine —— 新效果系统的统一结算引擎（单套系统，旧系统已删除迁移）。
    ///
    /// 职责：
    /// 1. 统一解析并结算 EffectData（B/C/F 三组共用结构）。
    /// 2. 实现动作原语执行器（ActionVerb）：**绝大多数原语已落地**（Tap/Untap/Latent/Uncover/Destroy/Bounce/Move/
    ///    Sacrifice/Gain/Attach/Seal/Curb/Search/Reveal/Shuffle/Pay/Discard/PrepareUpkeep/DealDamage/PreventDamage/
    ///    PlaceToken/RemoveToken/PlaceCounter/Draw/DrawFromBottom/Predict/PutToArea/PayOptional/MoveToZone/Inspect/
    ///    GainControl/DestroyOnTarget/SkipAreaCombat/ReplaceIcon/RevealTopAndTakeIfCostIs/SacrificeSelfOptional/
    ///    TransformForm/RevealSealedAndQueueFreePlay/PayOrSacrificeSelf/BribeOrSacrifice/SacrificeChoice…）。
    ///    **仍未实现的只有 6 个**：`GenerateToken`/`Investigate`/`Strength`/`Power`（各自显式 PrintErr＋break，见 `ExecuteVerb`）
    ///    与 `Win`/`Lose`（仅打 TODO）；**当前无任何卡声明这 6 个动词**（休眠项，不是现行缺陷）。
    /// 3. 持续效果注册表（ContinuousRegistry）：按 ZoneScope 注册/注销（手牌期靠 HasInherentKeyword 双层补判）。
    /// 4. 触发收集：从卡实例的 EffectDataList 收集进场/现身触发，供 TurnManager/TriggerRegistry 桥接。
    /// 5. 激活能力入口：角色/附属/**秘社**激活（`ActivateAbility`，含代价/目标地区/玩家目标等入参）。
    /// 6. 时效修正栈（P2）："直到回合结束"类修正于 ExpireTurnEndEffects 自动还原。
    ///
    /// 触发驱动：反应/机动触发由监听总线（GameEventBus + TriggerRegistry）收集；
    /// 进场/现身触发由本类 CollectTriggers 收集，统一经 TurnManager.RunTriggerPhase 入栈。
    /// </summary>
    public class EffectEngine
    {
        /// <summary>
        /// 【MVC 收口】单例访问器（等价于 DI 容器的 RegisterSingleton）。
        /// 实例由 Composition Root（GameInitializer）在装配时创建，场景释放时实例随之释放，
        /// 8 个对局状态集合（原 static 字段）改为实例字段后，跨局不再残留。
        /// </summary>
        public static EffectEngine Instance { get; private set; }

        public EffectEngine()
        {
            Instance = this;
        }

        // 抓牌等需要回合管理器参与的全局操作，由 GameInitializer 注入
        public TurnManager TurnManagerRef { get; set; }

        // ==================== 持续效果注册表 ====================
        // 记录每张卡**每条效果**实际生效（已授予）的持续效果：键 = 卡 UID + "#" + 效果 Id
        //（同一张卡有多条条件持续效果时必须分别登记，否则"后一条前提不满足"的还原会误撤前一条，见 JC084）。
        private readonly System.Collections.Generic.Dictionary<string, ContinuousGrantRecord> _continuousRegistry
            = new System.Collections.Generic.Dictionary<string, ContinuousGrantRecord>();

        /// <summary>
        /// 一条持续授予的登记记录：**授予落到哪张卡**（`TargetUid`；"受此结附的角色获得X"类效果落到**宿主**，
        /// 其余落到来源卡自身）+ 实际授予的授予项（注销时按此精确还原）。
        /// </summary>
        public sealed class ContinuousGrantRecord
        {
            public string TargetUid;
            public System.Collections.Generic.List<EffectGrant> Grants = new System.Collections.Generic.List<EffectGrant>();
        }
        // 已生效持续效果的卡引用集合（与 _continuousRegistry 并行，供 ReevaluatePrereqs 遍历全集，无需 UID 反查）。
        private readonly System.Collections.Generic.HashSet<CardInstance> _activePrereqCards
            = new System.Collections.Generic.HashSet<CardInstance>();
        // 持续光环（D19-③）注册表：源卡 UID → (受影响他者 UID → 该源对该他者实际授予的 EffectGrant 列表)。
        // 用于精确还原：源卡离场/被封印、或他者离场/夺控时，仅撤销本源授予的部分，不影响其他源。
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>> _auraGrants
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>>();

        // ==================== 管理器引用（供动词执行体访问模型） ====================
        // 由 GameInitializer 注入；用于 Move 等动词真正改写模型（跨地区移动）。
        public CardManager CardManagerRef { get; set; }

        /// <summary>
        /// 【批次 2·多对局隔离】本效果引擎所属对局的 CardManager（**单一取值入口**）。
        /// 生产路径由 `GameInitializer` 注入 `CardManagerRef`（每局一套实例）→ 一个服务端进程内多场对局互不串场；
        /// 仅在"未注入"的旧路径/单测里退回进程级 `CardManager.Instance`（与改造前语义一致）。
        /// 说明：这正是设计方案 §十一 列出的"11 处 `CardManager.Instance`"的收口点——所有动词执行体一律走本属性。
        /// </summary>
        private CardManager CM => CardManagerRef ?? CardManager.Instance;
        // 持续光环（D19-③）需读取区域卡集合，由 GameInitializer 注入 ZoneManager 引用。
        public ZoneManager ZoneManagerRef { get; set; }

        // ==================== D-1 堆叠熔断查询 ====================
        // 由 TurnManager 注入 StackManager.IsCircuitBroken。
        // 为 true 时禁止激活新效果，防止效果递归失控。未注入时默认不熔断（测试环境）。
        public System.Func<bool> IsStackCircuitBroken { get; set; }

        // ==================== D-UI 模型脏标记广播 ====================
        // 当结算/激活改动了卡牌模型可见状态（横置等）时通知表现层刷新。
        //【MVC 收口】不再用 static 委托由 Arena 注入；改为 UiNotificationBus（实例单例）广播，
        // Controller 只 NotifyVisualDirty()，View 订阅后自行刷新（依赖倒置）。

        // ==================== 准备步骤维护询问（JC068 阿格斯IV） ====================
        // 效果A"支付1，否则横置"的玩家二选一。由 View（Arena）实现 IGameUIHooks 接口，
        // 在启动装配时经 Composition Root 注入本引擎（依赖倒置：Controller 依赖接口，View 实现接口）。
        // UI 只负责弹窗 + 收集选择，把 pay=true/false 回传给 ResolvePrepareUpkeep，不直接操作 Model。
        public Script.Interfaces.IGameUIHooks UIHooks { get; set; }

        // ==================== C/S 化 P0-B 输入网关 ====================
        // 注入后，结算在"需玩家决策"处（当前仅 PrepareUpkeep）经网关发起异步请求-响应，
        // 而非同步弹窗阻塞。未注入（null）时所有 *Async 入口退化为同步行为，与现有逻辑完全一致。
        // 服务端 Headless 场景注入 NetworkInputGateway（未来）/ AutoInputGateway（测试）；
        // 单机场景保持 null（沿用 UIHooks 本地弹窗），实现完全向后兼容。
        public Script.Net.IInputGateway InputGateway { get; set; }

        /// <summary>查询「准备维护」支付是否可行：控制者有足够可用费用支付 payAmount。</summary>
        public bool CanPayPrepareUpkeep(CardInstance source, int payAmount)
        {
            if (source == null) return false;
            var pm = TurnManagerRef?.PlayerManagerRef;
            return pm != null && pm.GetAvailableMana(source.ControllerId) >= payAmount;
        }

        /// <summary>
        /// 【MVC 收口】执行「准备维护」的业务决策（JC068 阿格斯IV 效果A「支付 N，否则横置」）。
        /// pay=true → 支付 payAmount 费用，保持来源未横置；pay=false → 横置来源卡（发 TappedChanged 解除对手派遣封锁）。
        /// 无 UI（测试）与 UI 弹窗回调统一经此落地，消除业务逻辑重复，且把「支付/横置」的模型写回收归 Controller。
        /// </summary>
        public void ResolvePrepareUpkeep(CardInstance source, int payAmount, bool pay)
        {
            if (source == null) return;
            if (pay)
            {
                var pm = TurnManagerRef?.PlayerManagerRef;
                if (pm != null)
                {
                    pm.SpendMana(source.ControllerId, payAmount);
                    GD.Print($"[EffectEngine] 准备维护：玩家{source.ControllerId} 支付 {payAmount}（阿格斯IV保持未横置）");
                }
            }
            else
            {
                source.SetTapped(true);   // 横置阿格斯IV（发 TappedChanged → 解除对手派遣封锁）
                GD.Print($"[EffectEngine] 准备维护：阿格斯IV 被横置，玩家{source.ControllerId} 的对手恢复秘密派遣权");
            }
        }

        // ==================== P2 时效修正栈 ====================
        // 记录"直到回合结束"等时效类修正，回合结束（ExpireTurnEndEffects）时自动还原。
        private sealed class TimedMod
        {
            public string TargetUid;     // 受影响卡 UID（卡级修正）；玩家级修正时为空
            public int PlayerId = -1;    // 玩家级修正（如费用-1/**控制权归还到谁**）时有效
            public string Kind;          // Keyword / Icon / Attribute / Defense / CostMod / Control
            public string Value = "";
            public int Amount = 1;
            public int ExpireTurn;       // 到期回合（≤该回合结束时还原）
            public string FilterText = ""; // 玩家级费用修正的匹配过滤（如"帷幕守望"），空=全匹配
            /// <summary>【P3】费用修正作用的**付费通道**：`"打出"`（缺省）/`"现身"`（暗藏者付费翻面）。</summary>
            public string CostScope = "打出";
            public bool ConsumeOnce;     // 一次性：打出/翻面符合条件牌一次后消费移除（JC012"下一张"语义）
            /// <summary>【P3】"**每回合可再生的一次性**"（JC026「每个回合中你所打出的第一张装备牌费用-1」）：
            /// 消费后**不**移除，而是记 `ConsumedTurn` 本回合失效；下回合自动恢复（登记表即时效期）。
            /// 时效终结＝来源卡离场（`UnregisterContinuous` 时按 `SourceUid` 清理）。</summary>
            public bool Renewable;
            /// <summary>可再生的费用修正的**来源卡 UID**（来源离场即失效）。</summary>
            public string SourceUid;
            /// <summary>本回合已被消费的回合号（-1＝未消费；仅 `Renewable` 用）。</summary>
            public int ConsumedTurn = -1;
        }
        private readonly System.Collections.Generic.List<TimedMod> _timedModStack
            = new System.Collections.Generic.List<TimedMod>();

        // ==================== 动作原语执行器 ====================

        /// <summary>
        /// 统一结算入口：执行一条 EffectData 的动作序列。
        /// source = 效果来源卡实例；target = 已选中的目标卡实例（可能为 null）。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target)
            => ResolveEffect(data, source, target, "");

        /// <summary>
        /// 带"目标地区"的重载：用于机动类效果（如 JC014 公路骑士）在对抗前将来源卡移动到另一地区。
        /// targetAreaId 非空时优先作为移动目的地（ManeuverTrigger 的目标类型为 TargetKind.Area）。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId)
        {
            // 复用 5 参重载；触发/激活路径默认 winnerId=-1（仅"X对抗获胜"类触发需要传入，见 WinnerIsSelf 判定）。
            ResolveEffect(data, source, target, targetAreaId, -1);
        }

        /// <summary>
        /// 带"对抗获胜方玩家 ID"的重载：用于"X对抗获胜"类触发（威名等）。
        /// winnerId = 对应 PlayerId（如 OnCombatWon 的获胜方），供条件 WinnerIsSelf 判定
        /// "本卡控制者是否为该对抗的获胜方"。非对抗类时机传 -1（默认），谓词走原判定。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId, int winnerId)
            => ResolveEffect(data, source, target, targetAreaId, winnerId, -1);

        /// <summary>
        /// 带"目标入栈版本号"的重载（D16 丢失目标新方案）：targetVersionId 为堆叠条目入栈时记录的
        /// 目标卡 VersionId；结算时与当前目标 VersionId 比对，不一致即判丢失（见 ValidateTargetAtResolve）。
        /// 不传（默认 -1）表示调用方未提供版本号，回退为"实例存在即有效"（兼容旧路径）。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId, int winnerId, int targetVersionId)
            => ResolveEffect(data, source, target, targetAreaId, winnerId, targetVersionId, -1);

        /// <summary>
        /// 【K1】带"本次事件数量"的完整重载：`eventAmount` 供动态值表达式 `事件数量` 求值
        /// （由触发条目透传，见 `StackEntry.EventAmount`；非事件路径传 -1）。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId,
            int winnerId, int targetVersionId, int eventAmount)
            => ResolveEffect(data, source, target, targetAreaId, winnerId, targetVersionId, eventAmount, null);

        /// <summary>
        /// 【K15·多目标】完整重载：`choices` = 逐槽选择（`EffectData.TargetSlots` 的取值）。
        /// 非空且效果声明了多目标槽位时：先按槽复检合法性（失败→整条不结算），
        /// 再对**每个被选中的卡目标**各执行一次动作序列（作用地区＝地区槽的值）；地区槽的值作为 `targetAreaId`。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId,
            int winnerId, int targetVersionId, int eventAmount,
            System.Collections.Generic.IList<TargetChoice> choices)
            => ResolveEffect(data, source, target, targetAreaId, winnerId, targetVersionId, eventAmount, choices, -1);

        /// <summary>
        /// 【K16·模式分支】带"选定模式"的完整重载：`modeIndex` ≥ 0 且效果声明了 `Modes` 时，
        /// 本次结算**只按该模式**的目标声明做校验、只执行该模式的动作序列（用户 2026-09 口径：
        /// 模式在打出时选定、付费之前，入堆叠后不可更改）。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId,
            int winnerId, int targetVersionId, int eventAmount,
            System.Collections.Generic.IList<TargetChoice> choices, int modeIndex)
            => ResolveEffect(data, source, target, targetAreaId, winnerId, targetVersionId, eventAmount, choices,
                modeIndex, -1);

        /// <summary>
        /// 【K24·玩家目标】最终重载：`targetPlayerId` ＝ 本次结算的**目标玩家号**（-1＝无玩家目标）。
        /// 口径（用户 2026-09）：允许指定任意对手（甚至自己），目标在**入堆叠时锁定**；
        /// 动作参数里写 `"目标玩家"`（如 `Shuffle{"目标玩家"}`、`Inspect{"目标玩家牌库顶"}`）即指向它。
        /// </summary>
        public void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId,
            int winnerId, int targetVersionId, int eventAmount,
            System.Collections.Generic.IList<TargetChoice> choices, int modeIndex, int targetPlayerId)
        {
            if (data == null) return;
            _currentEffectData = data;   // 【P3·K4】供动词读取本效果的声明（如 `FormChange`）
            // 【K24】本次结算的目标玩家（动作里 `"目标玩家"` 的解析来源）：必须在动作序列执行前设定。
            _currentTargetPlayerId = targetPlayerId;

            // 【K1·"玩家选择"型动态值】前置取值：本效果声明 `ValueExpr="玩家选择"` 且本次尚无玩家给定的数字
            // → **先弹数值输入窗**（用户口径 A36：复用既有数值输入窗），把"待续结算"上下文存下并挂起；
            // 回调里写回选择值后**重入本方法**，此时动态值可求值、动作按 `$X` 正常展开。
            // 取消/无 UI → 该效果**不发动**（fail-closed，绝不替玩家猜一个数字）。
            if (IsPlayerChoiceValue(data) && _playerChosenValue < 0)
            {
                if (BeginPlayerValuePrompt(data, source, target, targetAreaId, winnerId, targetVersionId,
                        eventAmount, choices, modeIndex, targetPlayerId))
                    return;   // 已挂起（等玩家输入）
                GD.Print($"[EffectEngine] ⏹ 玩家选择型取值无 UI 可用：{data.EffectId} → 不发动");
                return;
            }
            // 【K16】按选定模式取"本次实际生效的动作序列"（无模式分支时＝`data.Actions`）；
            // 目标声明由 `ValidateTargetAtResolve(..., modeIndex)` 内部同样按模式解析。
            var effActions = data.ActionsFor(modeIndex);
            if (data.HasModes)
                GD.Print($"[EffectEngine] 【K16 模式分支】{data.EffectId} 执行模式 {modeIndex + 1}/{data.Modes.Count}（{data.ModeAt(modeIndex)?.Label}）");
            GD.Print($"[EffectEngine] 结算效果 {data.EffectId} (Timing={data.Timing})");

            // 【K1】动态值 X 求值：为 0（或求值失败）→ **该效果不算发动**（不执行动作，直接返回）。
            var valueState = EvaluateDynamicValue(data, source, target, targetAreaId, eventAmount, out int dynValue, out string dynReason);
            if (valueState == DynamicValueState.Blocked)
            {
                GD.Print($"[EffectEngine] ⏹ 动态值 X 不可用：{data.EffectId} → 不发动（{dynReason}）");
                return;
            }

            // D16 雏形：结算前目标丢失校验（双保险，与 TurnManager.ResolveEntryTargetIsLost 互补）。
            // 事务卡（OnSorceryCast）目标丢失 → 整张不结算、卡进墓地；触发/激活来源卡在场，仅跳过本条结算。
            var tv = ValidateTargetAtResolve(data, source, target, targetAreaId, targetVersionId);
            if (tv == TargetValidation.Lost)
            {
                if (data.Timing == EffectTiming.OnSorceryCast && source != null)
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 事务卡 {source.PlayerTemplate?.CardName} 目标丢失 → 不结算，卡进墓地");
                    CM?.MoveCardToGraveyard(source);
                }
                else
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 触发/激活效果 {data.EffectId} 目标丢失 → 跳过本条结算");
                }
                return;
            }

            // 条件不满足时走 ElseBranch
            // 【P3·门禁 + 分支】`Predicate`＝触发/结算门禁；`BranchPredicate`（可空）＝门禁满足后的分支选择。
            // 两者任一不满足都走 ElseBranch（JC031："另一个本方角色死亡"为门禁，"死亡的是吸血鬼角色"为分支）。
            if (data.Condition != null && data.Condition.Predicate != "Always")
            {
                // winnerId 透传给条件判定（供 WinnerIsSelf 使用）；targetAreaId 透传（供 WinnerInSameArea 使用）。
                // 【K3·死亡类·A10】条件判定的**上下文卡**：优先用"触发上下文卡"（＝引发本次触发的卡，
                // 如死亡触发里刚死亡的角色）——触发条件的语义对象是它，不是玩家选定的目标；
                // 非触发路径（激活/打出）无上下文卡 → 维持既有口径（回落 `target`）。
                var condCtx = TriggerContextCard ?? target;
                bool met = EvaluateCondition(data.Condition.Predicate, source, target, condCtx, winnerId, targetAreaId, data.Condition.Param);
                if (met && !string.IsNullOrEmpty(data.Condition.BranchPredicate))
                {
                    met = EvaluateCondition(data.Condition.BranchPredicate, source, target, condCtx, winnerId, targetAreaId, data.Condition.Param);
                    if (!met) GD.Print($"[EffectEngine] 门禁满足但分支条件不满足「{data.Condition.BranchPredicate}」，走 ElseBranch");
                }
                if (!met)
                {
                    GD.Print($"[EffectEngine] 条件不满足「{data.Condition.Predicate}」，走 ElseBranch");
                    ExecuteActions(data.Condition.ElseBranch, source, target, data.Duration, targetAreaId);
                    return;
                }
            }

            GD.Print($"[EffectEngine] 执行动作序列: EffectId={data.EffectId}, Actions数量={effActions?.Count ?? -1}");
            _actionChainAborted = false;   // 【P1】新动作序列开始：清链式中止标志
            _currentValue = dynValue;      // 【K1】本次结算的动态值 X（供动作参数 `$X` 替换）
            _hasCurrentValue = valueState == DynamicValueState.Ok;

            // 【P3】一次性待触发承诺（卡文"…；在下一个准备步骤开始时…"）：在**动作序列执行之后**登记，
            // 因此"既做一件事、又留下承诺"的能力（JC024：先潜伏、再在下一个准备步骤免费现身）两者都成立；
            // 只有承诺而不做事的能力（JC082）则 Actions 为空、效果等价于纯登记。
            // 【K15·多目标】逐槽复检 → 对每个被选中的卡目标各执行一次动作序列（地区槽值＝作用地区）
            if (MultiTargetSlots.IsMultiTarget(data))
            {
                string bad = MultiTargetSlots.Validate(data, choices, source?.ControllerId ?? -1,
                    ZoneManagerRef, CM, TurnManagerRef?.PlayerManagerRef, source, TurnManagerRef?.AreaManagerRef);
                if (bad != null)
                {
                    GD.Print($"[EffectEngine] ⛔ 多目标选择不合法：{bad} → 本条效果不结算");
                    return;
                }
                string area = MultiTargetSlots.ChosenAreaUid(data, choices);
                if (string.IsNullOrEmpty(area)) area = targetAreaId;
                ExecuteMultiTargetActions(effActions, data, source,
                    MultiTargetSlots.ChosenCardUids(data, choices), area, data.Duration,
                    MultiTargetSlots.ChosenPlayerIds(data, choices));
                RegisterPendingFor(data, source);
                return;
            }
            // 【K2】群体动作：对"地区内符合条件的所有卡"逐张执行同一动作序列（一次结算内全部完成）
            if (MassTargetPool.IsMass(data))
            {
                ExecuteMassActions(effActions, data, source, targetAreaId, durationText: data.Duration);
                RegisterPendingFor(data, source);
                return;
            }
            ExecuteActions(effActions, source, target, data.Duration, targetAreaId);
            RegisterPendingFor(data, source);
        }

        /// <summary>【P3】若本效果声明了一次性承诺（`EffectData.Pending`），在动作序列执行后登记它。</summary>
        private void RegisterPendingFor(EffectData data, CardInstance source)
        {
            if (data?.Pending != null) RegisterPending(source, data.Pending);
        }

        // ==================== 【K1】动态数值 X ====================
        /// <summary>动态值求值状态。</summary>
        public enum DynamicValueState
        {
            NotApplicable,   // 该效果没有声明动态值（既有卡）
            Ok,              // 求值成功（>0）
            Blocked,         // X=0 或求值失败 → **该效果不算发动**（用户 2026-09 口径）
        }

        /// <summary>本次结算的动态值（`$X` 替换用）与是否有值。</summary>
        private int _currentValue;
        private bool _hasCurrentValue;

        // ==================== 【K1】"玩家选择"型取值（JC083「选择一个数字」） ====================
        /// <summary>玩家已选择的数字（-1＝尚未选择）。取值窗回调后写入，并在**该次结算内消费一次**。</summary>
        private int _playerChosenValue = -1;

        /// <summary>待续结算上下文（等数值输入窗答复；回调里重入 `ResolveEffect`）。</summary>
        private sealed class PendingResolve
        {
            public EffectData Data;
            public CardInstance Source;
            public CardInstance Target;
            public string TargetAreaId;
            public int WinnerId;
            public int TargetVersionId;
            public int EventAmount;
            public System.Collections.Generic.IList<TargetChoice> Choices;
            public int ModeIndex;
            public int TargetPlayerId;
        }

        private PendingResolve _pendingResolve;

        /// <summary>该效果是否声明了"玩家选择"型动态值。</summary>
        private static bool IsPlayerChoiceValue(EffectData data)
            => data != null && !string.IsNullOrWhiteSpace(data.ValueExpr)
               && data.ValueExpr.Trim() == "玩家选择";

        /// <summary>【JC055②】该效果是否声明了"代价数量"型动态值（X＝本次激活已支付的标志代价数量）。</summary>
        private static bool IsCostPaidValue(EffectData data)
            => data != null && !string.IsNullOrWhiteSpace(data.ValueExpr)
               && (data.ValueExpr.Trim() == "代价数量" || data.ValueExpr.Trim() == "已付代价数量");

        /// <summary>【JC055②】本次激活已支付的"标志代价数量"（X）→ 入堆叠时随 `StackEntry.EventAmount`
        /// 下发，结算时作为 `事件数量` 供 `ValueExpr="代价数量"` 读取（-1＝无）。</summary>
        private int _pendingPaidCounterX = -1;

        /// <summary>当前是否已持有玩家选择的数字（测试/取证用）。</summary>
        public bool HasPlayerChosenValue => _playerChosenValue >= 0;

        /// <summary>
        /// 【K1】弹出数值输入窗并挂起结算：范围＝`data.ValueMin..ValueMax`（缺省 1..9；JC083＝0..20，A31）。
        /// 返回 true＝已挂起（等回调）；false＝无 UI 可用（调用方按"不发动"处理）。
        /// 回调：选择值写回 `_playerChosenValue` → 重入 `ResolveEffect` 完成结算；**取消 → 不发动**。
        /// </summary>
        private bool BeginPlayerValuePrompt(EffectData data, CardInstance source, CardInstance target,
            string targetAreaId, int winnerId, int targetVersionId, int eventAmount,
            System.Collections.Generic.IList<TargetChoice> choices, int modeIndex, int targetPlayerId)
        {
            int chooser = source?.ControllerId ?? -1;
            int min = System.Math.Max(0, data.ValueMin);
            int max = System.Math.Max(min, data.ValueMax);
            string prompt = $"{source?.PlayerTemplate?.CardName}：请选择一个数字（{min}～{max}）";
            // ① 联机（服务端无 View）：经网关下发数值输入请求；回包后写回选择值并**重入结算**
            if (UIHooks == null && InputGateway != null)
            {
                if (chooser < 0) return false;
                _pendingResolve = new PendingResolve
                {
                    Data = data, Source = source, Target = target, TargetAreaId = targetAreaId,
                    WinnerId = winnerId, TargetVersionId = targetVersionId, EventAmount = eventAmount,
                    Choices = choices, ModeIndex = modeIndex, TargetPlayerId = targetPlayerId,
                };
                _ = AskPlayerValueViaGatewayAsync(chooser, prompt, min, max);
                return true;
            }
            if (UIHooks == null) return false;
            if (chooser < 0) return false;
            _pendingResolve = new PendingResolve
            {
                Data = data, Source = source, Target = target, TargetAreaId = targetAreaId,
                WinnerId = winnerId, TargetVersionId = targetVersionId, EventAmount = eventAmount,
                Choices = choices, ModeIndex = modeIndex, TargetPlayerId = targetPlayerId,
            };
            UIHooks.PromptNumberInput(chooser, prompt, min, max, min, "确认", "取消", chosen =>
            {
                var p = _pendingResolve;
                _pendingResolve = null;
                if (p == null) return;
                if (chosen < 0)
                {
                    GD.Print($"[EffectEngine] 玩家{chooser} 取消数字选择 → {p.Data.EffectId} 不发动");
                    return;
                }
                GD.Print($"[EffectEngine] 玩家{chooser} 选择数字 {chosen}（{p.Data.EffectId}）");
                _playerChosenValue = chosen;
                ResolveEffect(p.Data, p.Source, p.Target, p.TargetAreaId, p.WinnerId, p.TargetVersionId,
                    p.EventAmount, p.Choices, p.ModeIndex, p.TargetPlayerId);
            });
            return true;
        }

        /// <summary>【A55】"选择一个数字"（JC083）的联机落地：`Kind.NumberInput` 回包 → 写回选择值 → 重入结算。
        /// 与单机同一口径：**取消/超时/越界 → 该效果不发动**（fail-closed，绝不替玩家猜）。</summary>
        private async System.Threading.Tasks.Task AskPlayerValueViaGatewayAsync(int chooser, string prompt, int min, int max)
        {
            Script.Net.ChoiceResult res = null;
            try
            {
                res = await InputGateway.RequestChoice(chooser, new Script.Net.ChoiceRequest
                {
                    PlayerId = chooser,
                    RequestKind = Script.Net.ChoiceRequest.Kind.NumberInput,
                    Prompt = prompt,
                    ForceChoice = true,
                    NumberMin = min,
                    NumberMax = max,
                    NumberDefault = min,
                });
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[EffectEngine] ⚠ 联机数字选择请求异常：{ex.GetType().Name}: {ex.Message} → 不发动");
            }
            var p = _pendingResolve;
            _pendingResolve = null;
            if (p == null) return;
            bool cancelled = res == null || res.Cancelled || res.SelectedAmounts == null || res.SelectedAmounts.Count == 0;
            int chosen = cancelled ? -1 : res.SelectedAmounts[0];
            if (chosen < min || chosen > max)
            {
                GD.Print($"[EffectEngine] 玩家{chooser} 取消/非法数字选择（{chosen}）→ {p.Data.EffectId} 不发动");
                return;
            }
            GD.Print($"[EffectEngine] 玩家{chooser} 选择数字 {chosen}（{p.Data.EffectId}，联机）");
            _playerChosenValue = chosen;
            ResolveEffect(p.Data, p.Source, p.Target, p.TargetAreaId, p.WinnerId, p.TargetVersionId,
                p.EventAmount, p.Choices, p.ModeIndex, p.TargetPlayerId);
        }

        /// <summary>
        /// 【K24】本次结算的**目标玩家号**（-1＝无玩家目标）。动作参数里写 `"目标玩家"` 时指向它
        /// （见 `ResolvePlayerSide`、`Inspect` 的 `目标玩家牌库顶/手牌` 写法）。
        /// </summary>
        private int _currentTargetPlayerId = -1;

        /// <summary>【P3·K4】**本次结算的 EffectData**（动词执行期可读）——供 `TransformForm` 取 `FormChange` 形态声明
        /// （动词签名只有 action/source/target，没有 data）。生命周期与 `TriggerContextCard` 同款：
        /// 每次结算入口覆盖一次，**不在结算末尾清空**（K11 挂起续算的动作链仍要能读到）。
        /// </summary>
        private EffectData _currentEffectData;

        /// <summary>【K24】本次结算的目标玩家号（只读；供 View/测试取证）。</summary>
        public int CurrentTargetPlayerId => _currentTargetPlayerId;

        /// <summary>【K3·死亡类·A10】**触发上下文卡**——"引发本次触发的卡"（如死亡触发里**刚死亡的角色**）。
        /// 由 `TurnManager` 在结算 `ResolveKind.Trigger` 条目**之前**下发（取自
        /// `StackEffectInstance.TargetCardUid` ＝ `TriggerContext.ContextCard`；激活类入堆叠没有该值 → null）。
        /// 用途：动作主体 `"触发卡"` / `"死亡角色"` 的解析（JC105"将该角色封印在目标角色上"＝把刚死亡的
        /// 角色从墓地取出、封印到玩家选定的宿主上）。
        /// 生命周期：每次触发结算前覆盖一次（激活条目 → 覆盖为 null）；**不在结算结束清空**——K11"可选支付"
        /// 会让动作链挂起后异步续算，过早清空会让续算段解析不到 `"触发卡"`。</summary>
        public CardInstance TriggerContextCard { get; set; }

        /// <summary>把动作"主体"写法解析成**触发上下文卡**：`"触发卡"` / `"死亡角色"` / `"死亡的角色"`。其它写法 → null。</summary>
        private CardInstance ResolveTriggerContextSubject(string subject)
        {
            switch (subject)
            {
                case "触发卡":
                case "死亡角色":
                case "死亡的角色":
                    return TriggerContextCard;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 【K1】按 `EffectData.ValueExpr` 求值动态 X。规则（用户 2026-09 口径）：
        ///  · 未声明 `ValueExpr` → `NotApplicable`；
        ///  · 求值为 **0** → `Blocked`（不算发动）；表达式未登记 / 依赖未实现（如"玩家选择"）→ `Blocked`（fail-closed）。
        /// </summary>
        public DynamicValueState EvaluateDynamicValue(EffectData data, CardInstance source, CardInstance target,
            string targetAreaId, int eventAmount, out int value, out string reason)
        {
            value = 0;
            reason = null;
            if (data == null || string.IsNullOrWhiteSpace(data.ValueExpr)) return DynamicValueState.NotApplicable;

            // 【K1】"玩家选择"型：X 是**玩家给出的数字**（JC083 的"猜测值"），不是"数量"——
            // 声明 `ValueMin=0` 时 **X=0 合法且效果照常结算**（不套用"X=0 ⇒ 不发动"，否则猜不出
            // "印刷费用为 0 的牌"）；未取值 → Blocked（fail-closed）。
            if (IsPlayerChoiceValue(data))
            {
                if (_playerChosenValue < 0)
                {
                    reason = "『玩家选择』尚未取值";
                    return DynamicValueState.Blocked;
                }
                value = _playerChosenValue;
                _playerChosenValue = -1;   // 每次结算只消费一次，避免串到后续效果
                return DynamicValueState.Ok;
            }

            var ctx = new EffectValueResolver.ValueContext
            {
                Source = source,
                Target = target,
                TargetAreaId = targetAreaId,
                EventAmount = eventAmount,
                PlayerChosenValue = _playerChosenValue,
                HasPlayerChosenValue = _playerChosenValue >= 0,
                ZoneManager = ZoneManagerRef,
                CardManager = CM,
                PlayerManager = TurnManagerRef?.PlayerManagerRef,
                AreaManager = TurnManagerRef?.AreaManagerRef,
            };
            var st = EffectValueResolver.Evaluate(data.ValueExpr, in ctx, out value, out reason);
            if (st == EffectValueResolver.ValueEvalResult.NoExpr) return DynamicValueState.NotApplicable;
            if (st == EffectValueResolver.ValueEvalResult.Fail)
            {
                reason = $"取值表达式求值失败：{reason}";
                return DynamicValueState.Blocked;
            }
            if (value <= 0)
            {
                reason = $"X={value}（X=0 时该效果不算发动）";
                return DynamicValueState.Blocked;
            }
            return DynamicValueState.Ok;
        }

        /// <summary>
        /// 动作参数里的 `$X` 替换（返回**副本**，不改动卡脚本里的模板数据）。
        /// 【实现细节·避免 RefCounted 抖动】复用同一个暂存对象 `_scratchAction`：
        /// `EffectAction` 是 RefCounted，每次动词执行都 new 一个会显著增加 GC 压力——
        /// 而 Godot 的 RefCounted 终结器存在已知竞态（`gchandle.is_released()` → Fatal），
        /// 高频分配会让全量测试偶发原生崩溃。故只替换 Args 内容并复用实例。
        /// </summary>
        private readonly EffectAction _scratchAction = new EffectAction();

        private EffectAction SubstituteValueArgs(EffectAction a)
        {
            if (a?.Args == null) return a;
            bool need = false;
            foreach (var s in a.Args)
                if (s != null && s.Contains(EffectValueResolver.Placeholder, System.StringComparison.Ordinal)) { need = true; break; }
            if (!need) return a;

            int val = _hasCurrentValue ? _currentValue : 0;
            _scratchAction.Verb = a.Verb;
            _scratchAction.Args.Clear();
            foreach (var s in a.Args)
                _scratchAction.Args.Add(s == null ? null : s.Replace(EffectValueResolver.Placeholder, val.ToString(), System.StringComparison.Ordinal));
            return _scratchAction;
        }

        /// <summary>
        /// 【K2】群体动作执行体：取集合（`MassTargetPool`）→ 对每张卡执行一次动作序列。
        /// 语义要点（用户 2026-09 口径）：
        ///  · **一次结算内全部执行完毕**，不中途插入其他效果窗口（期间处于堆叠结算的原子段内，其它玩家无法行动）；
        ///  · 各目标死亡产生的死亡触发按既有触发管线收集（多条触发后续由玩家排序）；
        ///  · 集合为空 → 不执行任何动作（不报错）。
        /// </summary>
        private void ExecuteMassActions(Array<EffectAction> actions, EffectData data, CardInstance source,
            string targetAreaId, string durationText)
        {
            var mass = MassTargetPool.Collect(data, source, targetAreaId, ZoneManagerRef, CM, TurnManagerRef?.PlayerManagerRef);
            GD.Print($"[EffectEngine] 【K2 群体】{data.EffectId}：地区={(string.IsNullOrEmpty(targetAreaId) ? source?.ZoneDetailId : targetAreaId)} " +
                     $"→ 集合 {mass.Count} 张（{MassTargetPool.Describe(mass)}）");
            if (mass.Count == 0) return;

            foreach (var t in mass)
            {
                if (_actionChainAborted) break;   // 链式中止（如 Pay 失败）时停止后续目标
                _actionChainAborted = false;      // 每个目标独立结算动作序列（避免上一目标的中止影响下一目标）
                ExecuteActions(actions, source, t, durationText, targetAreaId);
            }
            _actionChainAborted = false;
        }

        /// <summary>
        /// 【K15·多目标】执行体：对"逐槽选中的每个卡目标"各执行一次动作序列，
        /// 作用地区统一为地区槽的值（`targetAreaId`）。
        /// 语义与 K2 群体一致：**一次结算内全部执行完毕**（堆叠结算期间处于原子段，其它玩家无法行动）；
        /// 各目标产生的触发按既有管线在本次结算后收集。
        /// `cardUids` 为空（全部槽留空）时仍执行一次（`target` 为 null）——用于"只依赖地区槽"的效果。
        /// </summary>
        private void ExecuteMultiTargetActions(Godot.Collections.Array<EffectAction> actions, EffectData data,
            CardInstance source, System.Collections.Generic.List<string> cardUids, string targetAreaId, string durationText,
            System.Collections.Generic.List<int> playerIds = null)
        {
            int players = playerIds?.Count ?? 0;
            if ((cardUids == null || cardUids.Count == 0) && players == 0)
            {
                GD.Print($"[EffectEngine] 【K15 多目标】{data.EffectId}：无卡目标（仅地区槽 {targetAreaId}）→ 执行一次动作序列");
                ExecuteActions(actions, source, null, durationText, targetAreaId);
                return;
            }
            // 【K24】先逐"目标玩家"各执行一次（玩家槽），再逐"卡目标"执行：两条通路共用同一动作序列，
            // 差异只在"本次的目标是谁"（玩家经 `_currentTargetPlayerId`、卡经 `target`）。
            foreach (int pid in playerIds ?? new System.Collections.Generic.List<int>())
            {
                if (_actionChainAborted) break;
                _actionChainAborted = false;
                _currentTargetPlayerId = pid;
                GD.Print($"[EffectEngine] 【K24 玩家目标】{data.EffectId}：地区={targetAreaId}，目标玩家=玩家{pid} → 执行动作序列");
                ExecuteActions(actions, source, null, durationText, targetAreaId);
            }
            if (cardUids != null && cardUids.Count > 0)
                GD.Print($"[EffectEngine] 【K15 多目标】{data.EffectId}：地区={targetAreaId}，卡目标 {cardUids.Count} 个 → 逐个执行动作序列");
            foreach (var uid in cardUids ?? new System.Collections.Generic.List<string>())
            {
                if (_actionChainAborted) break;
                var t = CM?.GetInstance(uid);
                if (t == null) { GD.Print($"[EffectEngine] ⚠ 多目标 {uid} 实例不存在，跳过"); continue; }
                _actionChainAborted = false;   // 每个目标独立结算动作序列
                ExecuteActions(actions, source, t, durationText, targetAreaId);
            }
            _actionChainAborted = false;
        }

        // ==================== D16 目标丢失校验（雏形） ====================
        /// <summary>
        /// 结算时目标校验结果（D16 雏形）。
        ///  - Ok：目标合法或本效果不需要目标，可正常结算。
        ///  - Lost：需要目标但目标已丢失，按规则"不结算，卡直接进墓地"。
        ///  - NoTarget：本效果声明不需指定对象（NeedsSelection=false），跳过校验。
        /// </summary>
        public enum TargetValidation { Ok, Lost, NoTarget }

        /// <summary>
        /// D16 雏形：校验一条 EffectData 在结算时的目标是否仍然合法存在。
        /// source = 效果来源卡；target = 已选中的目标卡实例（可为 null）。
        /// 规则（用户确认）：若效果/卡需要指定目标，但结算时目标丢失，则不结算，卡直接进墓地。
        /// 本方法只做"判定"，不执行进墓地——由调用方按卡类别决定是否把来源卡送墓地（事务卡适用，触发/激活不适用）。
        /// </summary>
        public TargetValidation ValidateTargetAtResolve(EffectData data, CardInstance source, CardInstance target,
            string targetAreaUid = "", int targetVersionId = -1, int modeIndex = -1)
        {
            if (data?.Target == null && !data.HasModes) return TargetValidation.Ok;
            // 【K16】有模式分支时按**选定模式**的目标声明校验（另一模式不参与）
            var t = data.TargetFor(modeIndex);
            if (t.Kind == TargetKind.None || !t.NeedsSelection) return TargetValidation.NoTarget;

            // 地区目标：本层不判（地区的存在性/丢失由 AreaManager 与 TurnManager.ResolveEntryTargetIsLost 收口）。
            // ⚠️ 2026-09 修正：原判据是"targetAreaUid 非空即视为地区目标"，但触发类条目也会携带**事件地区**
            // （进场触发=进入的地区、死亡触发=死亡时所处地区、赢取触发=被赢取地区），于是"卡目标 + 带事件地区"
            // 的效果（如 JC002 横置本地区目标角色）会被误判为地区目标而**整段跳过卡目标校验**。
            // 改为按效果声明的目标大类判定：只有真正声明 `Kind=Area` 的目标才走地区路径。
            if (t.Kind == TargetKind.Area)
                return TargetValidation.Ok;

            // 【K24】玩家目标：目标不是卡实例（不存在 `target` 卡），其合法性由 `PlayerTargetPool` 在
            // **入堆叠前**判定并锁定（`StackEntry.TargetPlayerId`）；玩家不会"离场/翻面"→ 本层恒 Ok。
            // 若不在此放行，玩家目标效果会被误判为"目标已丢失 → 不结算"。
            if (PlayerTargetPool.IsPlayerSpec(t))
                return TargetValidation.Ok;

            if (target == null)
            {
                GD.Print($"[EffectEngine] ⚑ [D16] 效果 {data.EffectId} 需要目标但目标已丢失 → 不结算");
                return TargetValidation.Lost;
            }

            // 卡目标（D16 新方案）：状态版本号比对——入栈时记录的版本号与当前不一致即判丢失。
            // 版本号由移动/翻面/控制者变化/子类别授予撤销等中枢的 BumpVersion 驱动，取代逐属性广播。
            // targetVersionId<0 表示调用方未提供入栈版本号（兼容旧路径），回退为"实例存在即有效"。
            // 注（2026-08-11）：争夺区→争夺区夺控换边【不刷版本号】，故版本号比对在此场景下失效，
            // 必须由下方属性二次复检（TargetRules）兜底"敌方/友方、子类别、费用"等约束。
            if (targetVersionId >= 0 && target.VersionId != targetVersionId)
            {
                GD.Print($"[EffectEngine] ⚑ [D16] 效果 {data.EffectId} 目标状态版本号变化（入栈 {targetVersionId} → 现 {target.VersionId}）→ 不结算");
                return TargetValidation.Lost;
            }

            // 属性二次复检（D16 需求点 3，2026-08-11 实装）：
            // 优先用激活目标规则 data.Target 复检（与 IsLegalActivateTarget 取目标阶段同源）；
            // 仅当激活无 Target 配置（data.Target==null）时，才回退到打出规则 TargetRules 精筛。
            // 解决：JC012 等"激活目标≠打出目标"的卡，被打出规则 TargetRules（结附秘社的附属）误判目标丢失（2026-08-11 修复）。
            // 语义：版本号比对覆盖移动/翻面；本复检覆盖控制者换边（版本号不变）类变化。两条缺一不可。
            // ⚠ 2026-08-16：Restrictions（Enemy/子类别/费用）精筛复检不再在此重复——
            //   激活/触发/事务效果均先经 TurnManager.IsTargetLost → TargetValidity.Check 做完整复检（含 CheckRestrictions），
            //   此处的 data.Target 与之同源，故此处只保留大类复检，避免冗余。
            if (source != null && target != null)
            {
                var actTarget = t;   // 【K16】按选定模式的目标声明复检
                if (actTarget != null)
                {
                    // 激活目标规则复检：大类 + 暗藏者回退（与 IsLegalActivateTarget 方案 A 粗筛同源）
                    // 【K14】`KindAlt` 非 None 时与 `Kind` 构成 OR（"目标附属或资产"）
                    var kind = CardManager.MapInstanceToTargetKind(target);
                    bool stillLegal = (kind == actTarget.Kind) ||
                        (actTarget.KindAlt != TargetKind.None && kind == actTarget.KindAlt) ||
                        (actTarget.IncludeCovert && kind == TargetKind.Covert);
                    if (!stillLegal)
                    {
                        GD.Print($"[EffectEngine] ⚑ [D16] 效果 {data.EffectId} 目标属性复检不合法（激活目标 {actTarget.Kind} 不符 {kind}）→ 不结算");
                        return TargetValidation.Lost;
                    }
                }
                else
                {
                    // 无激活目标配置：回退到打出规则 TargetRules 精筛（保留 JC015 类语义）
                    var srcTmpl = source.PlayerTemplate as PlayerCardData;
                    if (srcTmpl?.TargetRules != null && srcTmpl.TargetRules.Count > 0)
                    {
                        int srcCtrl = source.ControllerId;   // 实时来源控制者（与取目标阶段同一基准）
                        bool stillLegal = false;
                        foreach (var rule in srcTmpl.TargetRules)
                        {
                            if (rule != null && CardManager.MatchesRule(rule, target, srcCtrl, TurnManagerRef?.PlayerManagerRef)) { stillLegal = true; break; }
                        }
                        if (!stillLegal)
                        {
                            GD.Print($"[EffectEngine] ⚑ [D16] 效果 {data.EffectId} 目标属性复检不合法（TargetRules 不匹配，如夺控换边）→ 不结算");
                            return TargetValidation.Lost;
                        }
                    }
                }
            }

            return TargetValidation.Ok;
        }

        /// <summary>执行动作序列。durationText 传入以驱动时效修正栈（P2）；targetAreaId 供机动等指向地区的动词使用。
        ///
        /// 【P1·链式中止】`Pay` 动词支付失败时置位 `_actionChainAborted`，本循环立即停止，
        /// 从而实现卡文"若你如此做，则…"的语义（支付不成立 → 后续动作不结算）。
        /// 每次动作序列开始时重置该标志（见各 ResolveEffect 入口）。
        ///
        /// 【K11·挂起续算】`PayOptional` 需要**弹窗等玩家输入**（单机回调式），故本方法支持"从中断处继续"：
        /// 命中等输入时把"剩余动作 + 上下文"记入 `_pendingChain`，回调里调 `ContinuePendingChain()` 续跑。</summary>
        private void ExecuteActions(Array<EffectAction> actions, CardInstance source, CardInstance target, string durationText = "", string targetAreaId = "")
            => ExecuteActionsFrom(actions, 0, source, target, durationText, targetAreaId);

        private void ExecuteActionsFrom(Array<EffectAction> actions, int startIndex, CardInstance source, CardInstance target,
            string durationText = "", string targetAreaId = "")
        {
            if (actions == null) return;
            // 【2026-09 修复·秘社批次实测暴露】`_actionChainAborted` 是**实例级**标志，而它的"复位点"原先只在
            // `ResolveEffect` 的动作序列入口（第 314 行附近）——**`ExecuteActions` 还会被"代价动作"、
            // 挂起续算、多目标逐卡执行等路径直接调用**，于是上一次中止的残留会让**后续完全无关的效果动作
            // 被整段跳过**（实测：磨牌/移动到牌库底的动词"日志都没有"，因为链式标记还是 true）。
            // 此处按"每次进入动作序列即复位"收口（与 `ResolveEffect` 入口的复位同义且更靠内，重复置 false 无害）。
            _actionChainAborted = false;
            for (int i = startIndex; i < actions.Count; i++)
            {
                var raw = actions[i];
                if (_actionChainAborted)
                {
                    GD.Print($"[EffectEngine] ⏹ 动作链已中止（前置付款未成立），跳过后续 {raw?.Verb}");
                    continue;
                }
                var a = SubstituteValueArgs(raw);   // 【K1】`$X` → 本次动态值
                ExecuteVerb(a, source, target, durationText, targetAreaId);
                // 【K11】该动词请求了玩家输入（如 PayOptional）→ 挂起剩余动作，等回调续算
                if (_pendingChain != null)
                {
                    _pendingChain.NextIndex = i + 1;
                    _pendingChain.Source = source;
                    _pendingChain.Target = target;
                    _pendingChain.Duration = durationText;
                    _pendingChain.AreaId = targetAreaId;
                    _pendingChain.Actions = actions;
                    return;
                }
            }
        }

        /// <summary>【K11】挂起中的动作链（等待玩家在数值输入框里答复）。</summary>
        private sealed class PendingChain
        {
            public Array<EffectAction> Actions;
            public int NextIndex;
            public CardInstance Source;
            public CardInstance Target;
            public string Duration;
            public string AreaId;
        }

        private PendingChain _pendingChain;

        /// <summary>【K11】请求玩家输入（如"是否支付 N"）：登记挂起链，回调中调 `ContinuePendingChain()` 续算。
        /// 【A55】不覆盖"**已记录续算点**"的挂起链：嵌套场景（在玩家回包的回调里又发起一次决策）下，
        /// 覆盖会把外层尚未执行的动作整段丢掉。</summary>
        private void SuspendChainForInput()
        {
            if (_pendingChain != null && _pendingChain.Actions != null) return;
            _pendingChain = new PendingChain();
        }

        /// <summary>【K11】玩家输入完成 → 从他中断处继续执行剩余动作（含"支付失败/不支付 → 后续整段跳过"的语义）。
        /// 【A55】续算跑完后若链上**不再等待输入**，则做一次"结算收尾"（服务端没有 Arena 帮忙收尾：
        /// 同步段末尾的 地区赢取检查 / 解原子锁 / 免费打出队列 都在 `TurnManager.ResolveStackTop` 里，
        /// 而挂起时那条同步段已提前 return 了）。</summary>
        public void ContinuePendingChain()
        {
            var chain = _pendingChain;
            _pendingChain = null;
            if (chain?.Actions == null) return;   // 无续算点（同步回调场景）：不做任何事（含不触发收尾）
            ExecuteActionsFrom(chain.Actions, chain.NextIndex, chain.Source, chain.Target, chain.Duration, chain.AreaId);
            // 链上仍有等待（续算中又发起了一次决策）→ 等它的回调，不在此收尾
            if (_pendingChain == null) TurnManagerRef?.OnEngineChainSettled();
        }

        /// <summary>【P1】动作链中止标志：由 `Pay` 支付失败置位；每个动作序列开始时重置。</summary>
        private bool _actionChainAborted;

        // ==================== C/S 化 P0-B：可挂起续算重载 ====================
        // 以下 *Async 入口与上方同步入口语义一致，唯一区别：当 InputGateway 已注入时，
        // 结算在"需要玩家决策"的点（当前仅 PrepareUpkeep）await 网关请求-响应，而非同步弹窗阻塞。
        // 未注入 InputGateway 时，*Async 与同步版行为完全一致（向后兼容，现有调用/测试零改动）。
        // 规则层只依赖 IInputGateway 接口，绝不 await Godot 节点——满足服务端 Headless 要求。

        /// <summary>可挂起续算版 ResolveEffect（详见类顶 P0-B 说明）。</summary>
        public async System.Threading.Tasks.Task ResolveEffectAsync(EffectData data, CardInstance source, CardInstance target)
            => await ResolveEffectAsync(data, source, target, "");

        public async System.Threading.Tasks.Task ResolveEffectAsync(EffectData data, CardInstance source, CardInstance target, string targetAreaId)
            => await ResolveEffectAsync(data, source, target, targetAreaId, -1);

        public async System.Threading.Tasks.Task ResolveEffectAsync(EffectData data, CardInstance source, CardInstance target, string targetAreaId, int winnerId)
            => await ResolveEffectAsync(data, source, target, targetAreaId, winnerId, -1);

        public async System.Threading.Tasks.Task ResolveEffectAsync(EffectData data, CardInstance source, CardInstance target, string targetAreaId, int winnerId, int targetVersionId)
        {
            if (data == null) return;
            GD.Print($"[EffectEngine] 结算效果(Async) {data.EffectId} (Timing={data.Timing})");

            // 【K1】动态值 X 求值：为 0（或求值失败）→ 该效果不算发动
            var valueStateAsync = EvaluateDynamicValue(data, source, target, targetAreaId, -1, out int dynValueAsync, out string dynReasonAsync);
            if (valueStateAsync == DynamicValueState.Blocked)
            {
                GD.Print($"[EffectEngine] ⏹ 动态值 X 不可用：{data.EffectId} → 不发动（{dynReasonAsync}）");
                return;
            }

            var tv = ValidateTargetAtResolve(data, source, target, targetAreaId, targetVersionId);
            if (tv == TargetValidation.Lost)
            {
                if (data.Timing == EffectTiming.OnSorceryCast && source != null)
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 事务卡 {source.PlayerTemplate?.CardName} 目标丢失 → 不结算，卡进墓地");
                    CM?.MoveCardToGraveyard(source);
                }
                else
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 触发/激活效果 {data.EffectId} 目标丢失 → 跳过本条结算");
                }
                return;
            }

            if (data.Condition != null && data.Condition.Predicate != "Always")
            {
                // 【K3·死亡类·A10】同同步路径：上下文卡优先用"触发上下文卡"（见同步路径注释）。
                var condCtx = TriggerContextCard ?? target;
                bool met = EvaluateCondition(data.Condition.Predicate, source, target, condCtx, winnerId, targetAreaId, data.Condition.Param);
                // 【P3·门禁 + 分支】同同步路径：BranchPredicate 不满足也走 ElseBranch（JC031）
                if (met && !string.IsNullOrEmpty(data.Condition.BranchPredicate))
                {
                    met = EvaluateCondition(data.Condition.BranchPredicate, source, target, condCtx, winnerId, targetAreaId, data.Condition.Param);
                    if (!met) GD.Print($"[EffectEngine] 门禁满足但分支条件不满足「{data.Condition.BranchPredicate}」，走 ElseBranch");
                }
                if (!met)
                {
                    GD.Print($"[EffectEngine] 条件不满足「{data.Condition.Predicate}」，走 ElseBranch");
                    await ExecuteActionsAsync(data.Condition.ElseBranch, source, target, data.Duration, targetAreaId);
                    return;
                }
            }

            _actionChainAborted = false;   // 【P1】新动作序列开始：清链式中止标志
            _currentValue = dynValueAsync; // 【K1】本次结算的动态值 X（供动作参数 `$X` 替换）
            _hasCurrentValue = valueStateAsync == DynamicValueState.Ok;

            // 【P3】一次性待触发承诺（异步路径同办）：动作序列执行**之后**登记（见同步路径说明）。
            // 【K2】群体动作（异步路径同办）：一次结算内对集合每张卡各执行一次动作序列
            if (MassTargetPool.IsMass(data))
            {
                var mass = MassTargetPool.Collect(data, source, targetAreaId, ZoneManagerRef, CM, TurnManagerRef?.PlayerManagerRef);
                GD.Print($"[EffectEngine] 【K2 群体·Async】{data.EffectId} → 集合 {mass.Count} 张（{MassTargetPool.Describe(mass)}）");
                foreach (var t in mass)
                {
                    if (_actionChainAborted) break;
                    _actionChainAborted = false;
                    await ExecuteActionsAsync(data.Actions, source, t, data.Duration, targetAreaId);
                }
                _actionChainAborted = false;
                RegisterPendingFor(data, source);
                return;
            }
            await ExecuteActionsAsync(data.Actions, source, target, data.Duration, targetAreaId);
            RegisterPendingFor(data, source);
        }

        private async System.Threading.Tasks.Task ExecuteActionsAsync(Array<EffectAction> actions, CardInstance source, CardInstance target, string durationText = "", string targetAreaId = "")
        {
            if (actions == null) return;
            foreach (var raw in actions)
            {
                if (_actionChainAborted)
                {
                    GD.Print($"[EffectEngine] ⏹ 动作链已中止（前置付款未成立），跳过后续 {raw?.Verb}");
                    continue;
                }
                var a = SubstituteValueArgs(raw);   // 【K1】`$X` → 本次动态值
                await ExecuteVerbAsync(a, source, target, durationText, targetAreaId);
            }
        }

        /// <summary>
        /// 可挂起版单动词执行：除 PrepareUpkeep 在 InputGateway 注入时走 await 请求-响应外，
        /// 其余动词直接复用同步 ExecuteVerb（纯模型写入，无需等待玩家）。
        /// </summary>
        private async System.Threading.Tasks.Task ExecuteVerbAsync(EffectAction action, CardInstance source, CardInstance target, string durationText = "", string targetAreaId = "")
        {
            // 【P3·H】检视（联机）：把被检视卡的面**只下发给检视者本人**（网关按座位寻址），await 确认后继续。
            // 语义：检视不改变任何状态，故即使超时/未确认也照常继续后续结算。
            if (action.Verb == ActionVerb.Inspect && InputGateway != null)
            {
                var cards = ResolveInspectTargets(action, source, target);
                int inspector = source?.ControllerId ?? -1;
                if (cards.Count == 0 || inspector < 0)
                {
                    GD.Print("[EffectEngine] ⚠ Inspect（联机）跳过：对象/检视者无效");
                    return;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = inspector,
                    RequestKind = Script.Net.ChoiceRequest.Kind.InspectCards,
                    SourceUid = source?.UID,
                    Prompt = cards.Count == 1
                        ? $"检视：{cards[0].PlayerTemplate?.CardName ?? cards[0].UID}"
                        : $"检视 {cards.Count} 张牌",
                };
                foreach (var c in cards)
                {
                    req.OptionUids.Add(c.UID);
                    req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));
                }
                var res = await InputGateway.RequestChoice(inspector, req);
                if (res == null || res.Cancelled)
                    GD.Print($"[EffectEngine] Inspect（联机）：玩家{inspector} 未确认/超时（不影响后续结算）");
                foreach (var c in cards) InspectCardPrivately(c, inspector);
                return;
            }

            // 【K11】可选支付（联机）：经输入网关下发**数值输入框**请求，await 玩家输入的 N 后续算。
            if (action.Verb == ActionVerb.PayOptional && InputGateway != null)
            {
                int defAmtA = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var d0a)) ? d0a : 1;
                int minAmtA = (action.Args != null && action.Args.Count > 1 && int.TryParse(action.Args[1], out var d1a)) ? d1a : 0;
                int maxAmtA = (action.Args != null && action.Args.Count > 2 && int.TryParse(action.Args[2], out var d2a)) ? d2a : defAmtA;
                int payerA = source?.ControllerId ?? -1;
                var pmA = TurnManagerRef?.PlayerManagerRef;
                if (payerA < 0 || pmA == null)
                {
                    GD.Print("[EffectEngine] ⚠ PayOptional（联机）跳过：payer/PlayerManager 缺失 → 视为未支付");
                    _actionChainAborted = true;
                    return;
                }
                int availA = pmA.GetAvailableMana(payerA);
                int hardMaxA = System.Math.Min(maxAmtA, availA);
                int hardMinA = System.Math.Max(0, System.Math.Min(minAmtA, hardMaxA));
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = payerA,
                    RequestKind = Script.Net.ChoiceRequest.Kind.NumberInput,
                    SourceUid = source?.UID,
                    Prompt = $"{source?.PlayerTemplate?.CardName}：是否支付？可支付 0～{hardMaxA}（当前可用 {availA}）",
                    ForceChoice = true,
                    NumberMin = hardMinA,
                    NumberMax = hardMaxA,
                    NumberDefault = System.Math.Clamp(defAmtA, hardMinA, hardMaxA),
                };
                req.OptionLabels.Add("支付");
                req.OptionLabels.Add("不支付");
                var res = await InputGateway.RequestChoice(payerA, req);
                int chosenA = (res != null && !res.Cancelled && res.SelectedAmounts != null && res.SelectedAmounts.Count > 0)
                    ? res.SelectedAmounts[0] : 0;
                chosenA = System.Math.Clamp(chosenA, 0, hardMaxA);
                if (chosenA <= 0)
                {
                    GD.Print($"[EffectEngine] 玩家{payerA} 未支付/取消 → 中止后续动作（PayOptional 联机）");
                    _actionChainAborted = true;
                    return;
                }
                bool okA = pmA.SpendMana(payerA, chosenA);
                GD.Print($"[EffectEngine] {(okA ? "✓" : "✗")} 玩家{payerA} 可选支付 {chosenA}（PayOptional 联机）");
                if (!okA) _actionChainAborted = true;
                return;
            }

            if (action.Verb == ActionVerb.PrepareUpkeep && InputGateway != null)
            {
                // C/S 挂起点：经输入网关向控制者请求"支付/横置"二选一，await 回包后续算。
                int payAmount = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedPay)) ? parsedPay : 1;
                var paySource = source;
                var result = await InputGateway.RequestChoice(
                    source?.ControllerId ?? 0,
                    new Script.Net.ChoiceRequest
                    {
                        RequestKind = Script.Net.ChoiceRequest.Kind.PayOrTap,
                        SourceUid = source?.UID,
                        PayAmount = payAmount,
                        Prompt = $"准备维护：支付{payAmount} 或 横置",
                        // 单机语义：阿格斯IV 准备维护为强制触发，必须二选一（Arena.PromptPayOrTap 置 ForceChoice=true）。
                        ForceChoice = true,
                        // 服务端权威：费用是否够支付（客户端仅据此置灰"支付"选项，对齐单机 PromptPayOrTap 的置灰，不重算规则）。
                        PayAllowed = CanPayPrepareUpkeep(paySource, payAmount),
                    });
                // 拿到选择后续算（与同步版 ResolvePrepareUpkeep 落地逻辑一致）
                ResolvePrepareUpkeep(source, payAmount, pay: result.Pay);
                return;
            }

            // 【P2】弃牌（联机）：经网关向"选择者"请求从手牌中选一张弃掉（SelectHandCard）。
            if (action.Verb == ActionVerb.Discard && InputGateway != null)
            {
                int count = 1;
                if (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var dc)) count = dc;
                if (count <= 0) count = 1;
                string who = (action.Args != null && action.Args.Count > 1) ? action.Args[1] : "自己";
                string how = (action.Args != null && action.Args.Count > 2) ? action.Args[2] : "自选";
                foreach (var pid in ResolvePlayerSide(source, who))
                {
                    if (how == "随机")
                    {
                        TurnManagerRef?.DiscardRandom(pid, count, count);
                        continue;
                    }
                    int chooser = (how == "由施术者指定") ? (source?.ControllerId ?? pid) : pid;
                    await DiscardViaGatewayAsync(pid, chooser, count, count);
                }
                return;
            }
            // 【P2】封印（被封印者取自手牌）：经网关让控制者从自己手牌中选一张（SelectHandCard）。
            if (action.Verb == ActionVerb.Seal && InputGateway != null && ArgAt(action, 0, "自身") == "手牌")
            {
                int chooser = source?.ControllerId ?? -1;
                var sealHost = ArgAt(action, 1, "目标") == "自身" ? source : target;
                if (chooser < 0 || sealHost == null)
                {
                    GD.Print($"[EffectEngine] ⚠ Seal 中止：chooser={chooser}, host={(sealHost == null ? "null" : sealHost.UID)}");
                    return;
                }
                await SealFromHandViaGatewayAsync(action, chooser, sealHost, HasArg(action, "背面朝上"));
                return;
            }

            // 【P2·联机断链修复·JC119】"展示封印在守秘人上的所有牌 → 你可以打出其中一张（视作快速、不需付费）"：
            // 原先只有单机 `UIHooks` 分支，联机（服务端无 View）落到"兜底＝不打出"⇒ 该卡在联机侧**永远不生效**。
            // 现补齐网关分支：**先公开展示全部** → 向该玩家下发 `SelectHandCard`（可取消＝不打）→
            // 忠诚预检 → 登记免费打出（后续由 `TurnManager` 在原子锁解除后按正常打出流程执行）。
            if (action.Verb == ActionVerb.RevealSealedAndQueueFreePlay && InputGateway != null)
            {
                await RevealSealedAndQueueFreePlayViaGatewayAsync(source);
                return;
            }

            // 【P2】检索：经网关向控制者下发 SelectDeckCard（候选含展示引用，牌库对双方不可见）。
            if (action.Verb == ActionVerb.Search && InputGateway != null)
            {
                await SearchViaGatewayAsync(action, source, target, targetAreaId);
                return;
            }

            // 【秘社批次】`UnsealToArea`（联机）：候选含封印区/墓地中的牌 —— **没有场上 CardUI**，
            // 故与服务端权威候选一致地下发 `SelectZoneCard`（含卡面引用），await 回包后落地。
            if (action.Verb == ActionVerb.UnsealToArea && InputGateway != null)
            {
                await UnsealToAreaViaGatewayAsync(action, source, targetAreaId);
                return;
            }

            // 【秘社批次·MSJC015①】`PlaceHandCardToArea`（联机）：候选是**该玩家自己的手牌** ⇒
            // 走既有 `SelectHandCard`（手牌对本人可见，无需额外授权），await 回包后落地。
            if (action.Verb == ActionVerb.PlaceHandCardToArea && InputGateway != null)
            {
                await PlaceHandCardToAreaViaGatewayAsync(action, source, targetAreaId);
                return;
            }

            // 其余动词：复用同步执行体（无需等待玩家）
            ExecuteVerb(action, source, target, durationText, targetAreaId);
        }

        // ==================== 【A55】联机决策：动作链"挂起-续算"统一收口 ====================
        // 背景（2026-09 实测缺陷）：生产链路是 TurnManager.ResolveStackTop（同步）→ ResolveEntry →
        // **同步** `ResolveEffect`，而网关分支只写在 `ExecuteVerbAsync`/`*ViaGatewayAsync` 那套
        // "可挂起续算"实现里 —— 全项目调用 `ResolveEffectAsync` 的只有测试 ⇒ 服务端（无 UIHooks）
        // 下所有"要玩家选择"的效果都会静默走"无 UI 确定性兜底"（自动取第一张/自动弃/不支付）。
        // 修法：在**同步**执行体里，遇到"有网关且该动词需要玩家选择"时，把该动词交给既有异步执行体，
        // 并用动作链挂起-续算（`SuspendChainForInput` / `ContinuePendingChain`，服务端不阻塞线程）。

        /// <summary>当前是否有"等待玩家输入"的挂起动作链（服务端则等客户端回包）。
        /// 供 `TurnManager` 在等待期间**维持原子锁**（服务端没有 View，无人 `NotifyModalOpened`）。</summary>
        public bool HasPendingInput => _pendingChain != null;

        /// <summary>
        /// 把一段"需要玩家决策"的异步网关流程接到动作链上（挂起-续算）：
        ///  · 流程**尚未完成**（真实网络等待）→ 挂起动作链，回包落地后 `ContinuePendingChain()` 续算；
        ///  · 流程**已同步完成**（测试/自动网关/回包即刻到达）→ 清掉挂起登记，调用方继续执行剩余动作；
        ///  · 无网关可用 → 返回 false（调用方走 UI 分支或确定性兜底）。
        /// </summary>
        private bool BeginGatewayFlow(System.Func<System.Threading.Tasks.Task> flow)
        {
            if (InputGateway == null || flow == null) return false;
            SuspendChainForInput();   // 先登记"要挂起"：ExecuteActionsFrom 据此记录续算点
            System.Threading.Tasks.Task t;
            try
            {
                t = flow();
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[EffectEngine] ⚠ 联机决策流程异常：{ex.GetType().Name}: {ex.Message} → 就地继续后续动作");
                _pendingChain = null;
                return true;
            }
            if (t == null || t.IsCompleted)
            {
                // 同步完成（含异常/取消）：无需挂起，剩余动作照常执行
                _pendingChain = null;
                return true;
            }
            // 【A55】真正等待客户端回包：服务端没有 View 帮忙 `NotifyModalOpened`，故由引擎**显式持住原子锁**，
            // 否则等待窗口内对手仍可行动；回包续算后的"结算收尾"（`ContinuePendingChain` → `OnEngineChainSettled`）
            // 会按常规把锁放掉。
            TurnManagerRef?.EnterAtomic();
            GD.Print($"[EffectEngine] 🌐 联机决策请求已下发，动作链挂起等待回包（保持原子锁）");
            _ = AwaitGatewayFlowAsync(t);
            return true;   // 已挂起：ExecuteActionsFrom 记录续算点并返回
        }

        private async System.Threading.Tasks.Task AwaitGatewayFlowAsync(System.Threading.Tasks.Task t)
        {
            try { await t; }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[EffectEngine] ⚠ 联机决策流程异常（续算前）：{ex.GetType().Name}: {ex.Message}");
            }
            ContinuePendingChain();   // 内部完成"结算收尾"（见 ContinuePendingChain）
        }

        /// <summary>
        /// 【A55】"0/1 二选一"（支付/牺牲、可选牺牲自身、移除标志/改为牺牲）的**联机落地**：
        /// 与单机 `PromptNumberInput(0,1,default,…)` 同语义——下发 `Kind.NumberInput`（min=0/max=1，
        /// `OptionLabels` 为两个按钮文案），回包 >0 ＝ 选"是"，否则 ＝ 选"否"（取消/超时也归"否"）。
        /// </summary>
        private async System.Threading.Tasks.Task AskZeroOrOneViaGatewayAsync(int chooser, string prompt,
            string yesLabel, string noLabel, System.Action<bool> onDecided)
        {
            Script.Net.ChoiceResult res = null;
            try
            {
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = chooser,
                    RequestKind = Script.Net.ChoiceRequest.Kind.NumberInput,
                    Prompt = prompt,
                    ForceChoice = true,
                    NumberMin = 0,
                    NumberMax = 1,
                    NumberDefault = 0,
                };
                req.OptionLabels.Add(yesLabel);
                req.OptionLabels.Add(noLabel);
                res = await InputGateway.RequestChoice(chooser, req);
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[EffectEngine] ⚠ 联机 0/1 决策请求异常：{ex.GetType().Name}: {ex.Message} → 按『否』处理");
            }
            int chosen = (res != null && !res.Cancelled && res.SelectedAmounts != null && res.SelectedAmounts.Count > 0)
                ? res.SelectedAmounts[0] : 0;
            onDecided?.Invoke(chosen > 0);
        }

        /// <summary>该动词在"有网关"时是否必须走异步执行体（＝ `ExecuteVerbAsync` 里有网关分支的动词）。
        /// 条件与那些分支自身的守卫保持一致，避免把纯模型写入的动词也绕一圈异步。</summary>
        private bool VerbNeedsGateway(EffectAction a)
        {
            if (InputGateway == null || a == null) return false;
            return a.Verb switch
            {
                ActionVerb.Inspect => true,                          // 检视：把卡面只下发给检视者本人
                ActionVerb.PayOptional => true,                      // K11 可选支付：数值输入框
                ActionVerb.PrepareUpkeep => true,                    // JC068 支付/横置二选一
                ActionVerb.Discard => true,                          // 弃牌（自选走选牌请求；随机在异步分支内自行处理）
                ActionVerb.Seal => ArgAt(a, 0, "自身") == "手牌",     // "从手牌中选一张封印"
                ActionVerb.RevealSealedAndQueueFreePlay => true,     // JC119② 挑一张免费打出
                ActionVerb.Search => true,                           // 检索取牌（含 JC051 选宿主）
                ActionVerb.UnsealToArea => true,                     // 【秘社批次】从封印区/墓地挑一张进场（同"选牌"请求）
                ActionVerb.PlaceHandCardToArea => true,              // 【秘社批次·MSJC015①】从手牌挑一张置于目标地区
                _ => false,
            };
        }

        /// <summary>
        /// 【秘社批次·MSJC015①】把**手牌中符合条件的一张牌**放进"本次结算的目标地区"。
        ///
        /// Args 约定（见 `ActionVerb.PlaceHandCardToArea`）：
        ///  · `Args[0]` 筛选（`DeckSearchFilter` 语法，空＝不限）；
        ///  · `Args[1]` 朝向：`"暗藏"`（缺省）/ `"正面"`；
        ///  · `Args[2]` 目的地：`"目标地区"`（缺省，取 `targetAreaId`）；
        ///  · `Args[3]` 附加：含 `"展示"` ⇒ 先 `RevealCardPublicly` 公开展示。
        ///
        /// 选择路径与其它"选牌"动词一致：单机 `PromptPickCard`、联机 `Kind.SelectHandCard`（异步分支）、
        /// 无 UI → 确定性兜底取首候选。候选为空 ⇒ 本动作不生效（`MarkActionIneffective`）。
        /// </summary>
        private void ResolvePlaceHandCardToArea(EffectAction action, CardInstance source, string targetAreaId)
        {
            if (source == null || TurnManagerRef == null)
            { MarkActionIneffective("PlaceHandCardToArea", "来源/TurnManager 缺失"); return; }
            string areaUid = ArgAt(action, 2, "目标地区") == "目标地区" ? (targetAreaId ?? "") : "";
            if (string.IsNullOrEmpty(areaUid))
            { MarkActionIneffective("PlaceHandCardToArea", "推不出目标地区"); return; }

            var hand = TurnManagerRef.GetHandOf(source.ControllerId)
                       ?? new System.Collections.Generic.List<CardInstance>();
            var candidates = FilterHandByArg(hand, ArgAt(action, 0, ""));
            if (candidates.Count == 0)
            {
                GD.Print($"[EffectEngine] ⚠ PlaceHandCardToArea：手牌中没有符合「{ArgAt(action, 0, "")}」的牌 → 本动作不生效");
                MarkActionIneffective("PlaceHandCardToArea", "手牌无合格候选");
                return;
            }

            if (UIHooks != null)
            {
                SuspendChainForInput();
                UIHooks.PromptPickCard(source.ControllerId, "从手中选择一张牌（展示后置于目标地区）", candidates,
                    allowCancel: false,
                    picked => { LandPlaceHandCardToArea(action, source, picked, areaUid); ContinuePendingChain(); });
                return;
            }
            GD.Print($"[EffectEngine] ⚠ PlaceHandCardToArea 无 UI：确定性兜底取首候选（{candidates[0].PlayerTemplate?.CardName}）");
            LandPlaceHandCardToArea(action, source, candidates[0], areaUid);
        }

        /// <summary>把手牌候选按筛选串过滤（空串＝不限）。</summary>
        private static System.Collections.Generic.List<CardInstance> FilterHandByArg(
            System.Collections.Generic.List<CardInstance> hand, string filter)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            if (hand == null) return result;
            if (string.IsNullOrWhiteSpace(filter)) { result.AddRange(hand); return result; }
            foreach (var c in hand)
                if (DeckSearchFilter.Matches(filter, c)) result.Add(c);
            return result;
        }

        /// <summary>落地：展示（可选）→ 设朝向 → 进场。</summary>
        private void LandPlaceHandCardToArea(EffectAction action, CardInstance source, CardInstance picked, string areaUid)
        {
            if (picked == null) { MarkActionIneffective("PlaceHandCardToArea", "选中的牌为空"); return; }
            if (HasArg(action, "展示")) RevealCardPublicly(picked, source.ControllerId);
            bool asCovert = ArgAt(action, 1, "暗藏") == "暗藏";
            picked.SetCovert(asCovert);              // 先翻面再进场（与"派遣暗藏者"、检索"暗藏进场"同序）
            CM?.MoveCardToArea(picked, areaUid);
            GD.Print($"[EffectEngine] PlaceHandCardToArea：{picked.PlayerTemplate?.CardName} 从手牌" +
                     $"{(asCovert ? "以暗藏者状态" : "正面")}置于地区 {areaUid}");
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>
        /// 【秘社批次·MSJC014②】找"具有锁定标志（或锁定标记）的卡"**当前**所在地区。
        /// 卡库对同一概念两种写法并存（与 `TargetRestriction.HasCounter` / 静止规则同款），故两种都认。
        /// 确定性：按地区槽顺序 → 地区内区域列表顺序取**第一个**命中的卡；一个都没有 → null。
        /// </summary>
        private string ResolveLockedCardArea()
        {
            var zm = ZoneManagerRef;
            if (zm == null) return null;
            foreach (var areaUid in zm.GetAllAreaSlotUids() ?? new System.Collections.Generic.List<string>())
            {
                foreach (var c in zm.GetZoneCards(CardZone.AreaZone, 0, areaUid)
                             ?? new System.Collections.Generic.List<CardInstance>())
                {
                    if (c?.Counters == null) continue;
                    bool locked = (c.Counters.TryGetValue("锁定标志", out int a) && a > 0)
                               || (c.Counters.TryGetValue("锁定标记", out int b) && b > 0);
                    if (locked) return areaUid;
                }
            }
            return null;
        }

        /// <summary>
        /// 【秘社批次】执行单个动作原语。**未实现的原语 = `GenerateToken`/`Investigate`/`Strength`/`Power`（各自显式报错）
        /// 与 `Win`/`Lose`（仅打 TODO）**——除这 6 个外其余动词均有执行体（详见类顶职责 2）。</summary>
        private void ExecuteVerb(EffectAction action, CardInstance source, CardInstance target, string durationText = "", string targetAreaId = "")
        {
            // 【A55】联机（服务端 = 无 UI、有网关）：需要玩家选择的动词走网关 + 动作链挂起-续算。
            // 判定顺序与既有口径一致：**有 UI 时优先本机窗口**（单机/客户端表现层），只有无 UI 才走网关。
            if (UIHooks == null && VerbNeedsGateway(action)
                && BeginGatewayFlow(() => ExecuteVerbAsync(action, source, target, durationText, targetAreaId)))
                return;

            switch (action.Verb)
            {
                case ActionVerb.Tap:
                {
                    // 【P3·K16 触发版】横置对象：`Args[0]` 可选主体（缺省"目标"）。
                    // 支持 `"结附宿主"`（`AttachedTo`）与 `"封印宿主"`（`SealedBy`）——卡文
                    // "当本卡被封印在角色上时，横置**该角色**"（JC110）里"该角色"＝封印宿主。
                    var tapObj = ResolveHostSubject(ArgAt(action, 0, "目标"), source) ?? target;
                    if (tapObj != null) { tapObj.SetTapped(true); GD.Print($"[EffectEngine] 横置 {tapObj.PlayerTemplate?.CardName}"); Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty(); }
                    else GD.Print("[EffectEngine] Tap 目标为空，跳过");
                    break;
                }

                case ActionVerb.Untap:
                {
                    // 同 Tap：`Args[0]` 可选主体（缺省"目标"），支持 `"结附宿主"` / `"封印宿主"`。
                    var untapObj = ResolveHostSubject(ArgAt(action, 0, "目标"), source) ?? target;
                    if (untapObj != null) { untapObj.SetTapped(false); GD.Print($"[EffectEngine] 重置 {untapObj.PlayerTemplate?.CardName}"); Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty(); }
                    else GD.Print("[EffectEngine] Untap 目标为空，跳过");
                    break;
                }

                case ActionVerb.Destroy:
                    if (target != null)
                    {
                        // 【P1】改走 DestroyInstance：效果"消灭"按规则属于"死亡"，需广播 OnDeath/OnCardDied
                        // 与触发总线（此前直接 MoveCardToGraveyard，导致"每当一个角色死亡时…"类效果收不到）。
                        CM?.DestroyInstance(target);
                        GD.Print($"[EffectEngine] 消灭 {target.PlayerTemplate?.CardName}");
                    }
                    else GD.Print("[EffectEngine] Destroy 目标为空，跳过");
                    break;

                // 【P1】潜伏：翻面为暗藏者（"潜伏（将该角色翻面为暗藏者）"）。
                // 经 SetCovert 收口（含 BumpVersion：翻面即判目标丢失，符合规则③）。
                case ActionVerb.Latent:
                {
                    var latentTarget = target ?? source;
                    if (latentTarget == null)
                    { MarkActionIneffective("Latent", "目标为空"); break; }
                    // 【触发语义·护栏】潜伏是"场上卡翻面"：来源/目标已不在场上则跳过
                    // （典型：死亡触发的效果在墓地结算，来源卡已进墓地）。
                    if (!IsOnField(latentTarget))
                    { MarkActionIneffective("Latent", $"{latentTarget.PlayerTemplate?.CardName} 不在场上（{latentTarget.CurrentZone}）"); break; }
                    if (latentTarget.IsCovert)
                    { MarkActionIneffective("Latent", $"{latentTarget.PlayerTemplate?.CardName} 已经是暗藏者"); break; }
                    latentTarget.SetCovert(true);
                    GD.Print($"[EffectEngine] 潜伏：{latentTarget.PlayerTemplate?.CardName} 翻面为暗藏者");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【P1】现身：令暗藏者翻面揭示。经 CardManager.RevealCard 收口（复用进场+现身触发链路）。
                case ActionVerb.Uncover:
                {
                    var uncoverTarget = target ?? source;
                    if (uncoverTarget == null) { GD.Print("[EffectEngine] Uncover 目标为空，跳过"); break; }
                    if (!IsOnField(uncoverTarget)) { GD.Print($"[EffectEngine] Uncover 跳过：{uncoverTarget.PlayerTemplate?.CardName} 不在场上（{uncoverTarget.CurrentZone}）"); break; }
                    if (!uncoverTarget.IsCovert) { GD.Print($"[EffectEngine] Uncover 跳过：{uncoverTarget.PlayerTemplate?.CardName} 非暗藏"); break; }
                    CM?.RevealCard(uncoverTarget);
                    GD.Print($"[EffectEngine] 现身：{uncoverTarget.PlayerTemplate?.CardName} 翻面揭示");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                case ActionVerb.Bounce:
                    if (target != null)
                    {
                        CM?.ReturnCardToHand(target);
                        GD.Print($"[EffectEngine] 移回 {target.PlayerTemplate?.CardName} 到拥有者手牌");
                    }
                    break;

                case ActionVerb.Move:
                {
                    // 【K15】Args[0] = 对象：`"自身"`（缺省，= source，机动语义）/ `"目标"`（= target；
                    // 多目标效果里逐个目标各执行一次，如 JC021「将这些角色移动到目标地区」）。
                    bool moveTheTarget = action.Args != null && action.Args.Count > 0 && action.Args[0] == "目标";
                    var mover = moveTheTarget ? target : source;
                    if (mover == null)
                    {
                        GD.Print("[EffectEngine] ⚠ Move 跳过：被移动对象为空");
                        break;
                    }
                    // 【触发语义·护栏】移动的对象若已不在场上（如"死亡触发"的效果在墓地结算、
                    // 或触发窗口期间来源被消灭），移动会把墓地/手牌里的卡搬进争夺区 —— 直接跳过。
                    if (!IsOnField(mover))
                    {
                        GD.Print($"[EffectEngine] ⚠ Move 跳过：{(moveTheTarget ? "目标" : "来源")} {mover.PlayerTemplate?.CardName} 已不在场上（{mover.CurrentZone}）");
                        break;
                    }
                    // 【P3·K21】`Args[1]` = **目的地写法**（缺省＝`targetAreaId`，即机动/多目标地区槽语义）：
                    //   `"目标所在地区"`（JC069 警犬「移动到目标具有锁定标志的角色/暗藏者**所在的地区**」）/
                    //   `"来源所在地区"`（回到自身所在地区）。取值按被移动卡/目标卡的**当前**所在地实时解析。
                    string destArg = (action.Args != null && action.Args.Count > 1) ? action.Args[1] : "";
                    string dest = destArg switch
                    {
                        "" => targetAreaId,
                        "目标所在地区" => target?.ZoneDetailId ?? "",
                        "来源所在地区" => source?.ZoneDetailId ?? "",
                        // 【秘社批次·MSJC014②】"移动到**具有锁定标志的角色或暗藏者**所在的地区"：
                        // 目的地＝场上带"锁定标志/锁定标记"的卡（不限敌我、角色或暗藏者）**当前**所在地区；
                        // 取"哪一个"用确定性规则（地区槽顺序 → 地区内区域列表顺序的第一个），无 UI/联机两端同解。
                        "锁定者所在地区" => ResolveLockedCardArea() ?? "",
                        _ => null,
                    };
                    if (dest == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ Move 跳过：目的地写法「{destArg}」未登记（支持 目标所在地区/来源所在地区/锁定者所在地区，或省略取目标地区）");
                        break;
                    }
                    if (!string.IsNullOrEmpty(dest))
                    {
                        // 机动：移动到指定地区（JC014 公路骑士在对抗前触发，targetAreaId 由 ManeuverTrigger 窗口推导"另一地区"；
                        // JC021：targetAreaId = 玩家选定的目标地区；JC069：目的地＝目标卡所在地区）
                        var cm = CM;
                        if (cm != null)
                        {
                            cm.MoveCardToArea(mover, dest);
                            GD.Print($"[EffectEngine] 机动：{mover.PlayerTemplate?.CardName} 移动到地区 {dest}");
                        }
                        else
                        {
                            GD.Print($"[EffectEngine] 机动（{mover.PlayerTemplate?.CardName} → 地区 {dest}）— CardManager 未注入，仅记录");
                        }
                    }
                    break;
                }

                case ActionVerb.Draw:
                    // D34 修正：触发结算的"操作玩家"应为控制者（ControllerId），而非拥有者（OwnerId）。
                    // 夺控场景（Controller=P1, Owner=P2）下原逻辑抓到 P2 牌库，P1 无感；改为 ControllerId 后由控制者抽牌。
                    if (source != null && source.ControllerId >= 0)
                    {
                        // 抓牌：从控制者牌库顶加入手牌（XG01 等响应触发）
                        // 数量取自 action.Args[0]，缺省为 1
                        int amt = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedAmt)) ? parsedAmt : 1;
                        if (TurnManagerRef != null)
                        {
                            TurnManagerRef.DrawCardsForPlayer(source.ControllerId, amt);
                            GD.Print($"[EffectEngine] 抓牌：玩家{source.ControllerId} 抓 {amt} 张");
                        }
                        else
                        {
                            GD.Print($"[EffectEngine] ⚠ 抓牌失败：TurnManagerRef 未注入（玩家{source.ControllerId} ×{amt}）");
                        }
                    }
                    else
                    {
                        GD.Print($"[EffectEngine] ⚠ Draw 跳过：source={(source == null ? "null" : source.PlayerTemplate?.CardName)}, OwnerId={(source?.OwnerId)}");
                    }
                    break;

                // 【P3·K22·多玩家/对手侧】"由指定玩家**自己选择**牺牲"（口径 A11/A12）：
                // Args：[种类(角色/附属/资产/暗藏者), 谁(每位玩家/目标玩家/自己/对手), 地区(任意/本地区/目标地区)]。
                //  · **每位玩家**：按**先手优先**顺序逐个询问（先选者先记录，后选者可见——UI 侧高亮由 View 负责），
                //    全部选定后**一起牺牲**（原子锁期间不分死亡先后）；**无候选者跳过、什么都不付出**（A11 补充）；
                //  · **目标玩家/对手**：只问该玩家（K24 玩家目标在入堆叠时锁定，见 `_currentTargetPlayerId`）；
                //  · 决策路径：单机 `IGameUIHooks.PromptPickCard(该玩家 id, …)`；无 UI → 确定性兜底取第一个候选；
                //  · 牺牲＝死亡（`ResolveDeath(Sacrifice)`），**照常触发死亡时机**。
                // 【P3·K22/A12】JC128 替罪羊："除非目标玩家选择并移除 N 个他所拥有的势力标志，否则该玩家牺牲一个角色"。
                //  · **标志不足 N → 必须牺牲**（A12：不询问）；
                //  · 标志足够 → 询问目标玩家：**确认＝移除 N 个标志**（复用 `CurbRule` 的"由他按地区分配"）；**取消＝走牺牲路线**；
                //  · **没有角色时也可以选牺牲路线 → 此时什么都不付出**（`SacrificeChoice` 无候选即跳过）。
                case ActionVerb.BribeOrSacrifice:
                {
                    int needMarks = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var bribeN)) ? bribeN : 1;
                    string kind = ArgAt(action, 1, "角色");
                    int targetPlayer = _currentTargetPlayerId > 0 ? _currentTargetPlayerId
                        : (source != null ? (source.ControllerId == 1 ? 2 : 1) : -1);
                    var am = TurnManagerRef?.AreaManagerRef;
                    if (targetPlayer <= 0 || am == null)
                    {
                        GD.Print("[EffectEngine] ⚠ BribeOrSacrifice 跳过：无法确定目标玩家/地区管理器缺失");
                        break;
                    }
                    int owned = CurbRule.TotalMarks(am, targetPlayer);
                    bool mayPay = owned >= needMarks;                 // A12：不足 → 必须牺牲，不再询问
                    // 落地（单机回调 / 联机回包共用；**必须在玩家答复之后**才分叉，见下方缺陷说明）
                    System.Action<bool> landBribe = pay =>
                    {
                        if (pay)
                        {
                            CurbRule.Resolve(this, source, needMarks, "对手");
                            GD.Print($"[EffectEngine] BribeOrSacrifice：玩家{targetPlayer} 选择移除 {needMarks} 个势力标志（而非牺牲）");
                            return;
                        }
                        GD.Print($"[EffectEngine] BribeOrSacrifice：玩家{targetPlayer} 走牺牲路线（无合法候选则什么都不付出）");
                        ResolveSacrificeChoice(new EffectAction
                        {
                            Verb = ActionVerb.SacrificeChoice,
                            Args = new Array<string> { kind, "目标玩家", "任意" },
                        }, source, targetAreaId);
                    };
                    if (!mayPay)
                    {
                        landBribe(false);
                        break;
                    }
                    string bribePrompt = $"是否移除 {needMarks} 个你所拥有的势力标志？（取消则牺牲一个{KindName(kind)}）";
                    // ① 联机：经网关下发 0/1 二选一
                    if (UIHooks == null && BeginGatewayFlow(() => AskZeroOrOneViaGatewayAsync(targetPlayer, bribePrompt, "移除标志", "改为牺牲",
                            pay => landBribe(pay))))
                        break;
                    // ② 单机：数值输入窗（0/1）
                    //    ⚠ 原实现**未挂起动作链**：窗口是回调式异步的，代码却在同一帧按 `payChosen=false`
                    //    直接走了牺牲路线 ⇒ 玩家在窗口里的选择被丢弃（现有单测用同步桩，长期掩盖）。
                    if (UIHooks != null)
                    {
                        SuspendChainForInput();
                        UIHooks.PromptNumberInput(targetPlayer, bribePrompt, needMarks, needMarks, needMarks,
                            "移除标志", "改为牺牲",
                            chosen => { landBribe(chosen >= needMarks); ContinuePendingChain(); });
                        break;   // 已挂起：ExecuteActionsFrom 记录续算点
                    }
                    // ③ 无 UI / 无网关：确定性兜底＝能付则付（与既有同类兜底口径一致）
                    GD.Print($"[EffectEngine] BribeOrSacrifice：无 UI/网关 → 玩家{targetPlayer} 确定性兜底选择支付标志");
                    landBribe(true);
                    break;
                }

                case ActionVerb.SacrificeChoice:
                {
                    ResolveSacrificeChoice(action, source, targetAreaId);
                    break;
                }

                // 【P3·K19】获得临时费用（JC115「快速行动，横置：你获得1」）：Args[0]＝数量（缺省 1）。
                // 口径（用户 2026-09 §7.4b A26）：所有费用都是**无色费用**；临时费用**优先花费**、
                // **任意步骤结束时清除**（清空点＝`TurnManager.EnterPhase` 的每次阶段流转）。
                case ActionVerb.GrantTemporaryMana:
                {
                    int tempAmt = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedTemp)) ? parsedTemp : 1;
                    var pmTemp = TurnManagerRef?.PlayerManagerRef;
                    if (pmTemp != null && source != null && source.ControllerId > 0)
                    {
                        pmTemp.GrantTemporaryMana(source.ControllerId, tempAmt);
                        GD.Print($"[EffectEngine] 临时费用：玩家{source.ControllerId} 获得 {tempAmt}（当前 {pmTemp.GetTemporaryMana(source.ControllerId)}）");
                        Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    }
                    else
                    {
                        GD.Print($"[EffectEngine] ⚠ GrantTemporaryMana 跳过：PlayerManager/来源缺失（source={(source == null ? "null" : source.PlayerTemplate?.CardName)}）");
                    }
                    break;
                }

                case ActionVerb.DrawFromBottom:
                {
                    // 从牌库底抽牌（秘社激活效果）。数量取自 action.Args[0]，缺省为 1。
                    // 【P3·非抓牌式取牌】Args 含 `"不计抓牌"` → **不广播 OnDraw**（JC049 深渊细语：
                    // 卡文"将你牌库底的两张牌置于手中"，FAQ 明确"并不算抓牌"）。
                    if (source != null && source.ControllerId >= 0)
                    {
                        int amt = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedAmt2)) ? parsedAmt2 : 1;
                        bool countAsDraw = !HasArg(action, "不计抓牌");
                        if (TurnManagerRef != null)
                        {
                            TurnManagerRef.DrawFromBottomForPlayer(source.ControllerId, amt, countAsDraw);
                            GD.Print($"[EffectEngine] 从牌库底{(countAsDraw ? "抽牌" : "取牌（不计抓牌）")}：" +
                                     $"玩家{source.ControllerId} 取 {amt} 张");
                        }
                        else
                        {
                            GD.Print($"[EffectEngine] ⚠ 从底抽牌失败：TurnManagerRef 未注入（玩家{source.ControllerId} ×{amt}）");
                        }
                    }
                    else
                    {
                        GD.Print($"[EffectEngine] ⚠ DrawFromBottom 跳过：source={(source == null ? "null" : source.PlayerTemplate?.CardName)}, OwnerId={(source?.OwnerId)}");
                    }
                    break;
                }

                case ActionVerb.Gain:
                    // 获得属性/图标/关键字：+1防御、［战斗属性］、费用减少等。
                    // 带时效（durationText 含"回合结束"）的修正登记到 P2 时效修正栈，回合结束自动还原。
                    // 【P3】群体主体（Args[2]＝`所有本方角色`/`所有其他本方角色`）：对**全场**本方角色逐个授予
                    // （JC024「所有其他本方角色获得【机动】直到回合结束」）；逐个走同一条 `ApplyGain`
                    // （把主体参数改写为"目标"并把该卡作为 target 传入，时效项因此**逐卡**登记）。
                    if (IsMassGainSubject(action))
                    {
                        var subjects = CollectFriendlyCharacters(source, excludeSource: ArgAt(action, 2, "") == "所有其他本方角色");
                        if (subjects.Count == 0) GD.Print($"[EffectEngine] Gain（群体主体）无可授予对象（来源控制者={(source?.ControllerId ?? -1)}）");
                        foreach (var subj in subjects)
                        {
                            var perArgs = new Array<string>(action.Args);
                            perArgs[2] = "目标";                      // 复用既有主体解析（target ?? subject）
                            ApplyGain(source, subj, perArgs, durationText);
                        }
                        break;
                    }
                    ApplyGain(source, target, action.Args, durationText);
                    break;

                case ActionVerb.Lose:
                    // 【秘社批次·MSJC010②】"目标角色**获得或失去**灵体能力直到回合结束"：
                    // `Lose` 与 `Gain` 是同一条属性/关键字通道的两个方向（`ApplyGain` 内部对负数走 `Revoke`），
                    // 故实现＝把参数数量取负后交给**同一实现**，避免出现第二条"失去"逻辑。
                    {
                        var loseArgs = new Array<string>();
                        if (action.Args != null)
                            foreach (var a in action.Args) loseArgs.Add(a);
                        // Args = [属性名, 数量?]：数量缺省 1；显式给出时取负，未给出时补 "-1"。
                        if (loseArgs.Count >= 2 && int.TryParse(loseArgs[1], out int loseAmt))
                            loseArgs[1] = (-System.Math.Abs(loseAmt)).ToString();
                        else if (loseArgs.Count >= 2)
                            loseArgs[1] = "-1";
                        else
                            loseArgs.Add("-1");
                        ApplyGain(source, target, loseArgs, durationText);
                        GD.Print($"[EffectEngine] 失去：{string.Join(",", loseArgs)}（经 ApplyGain 的负数通道 → Revoke）");
                    }
                    break;

                case ActionVerb.Sacrifice:
                    // 【触发语义】牺牲**也是死亡**（死亡=行为：伤害致死/消灭/牺牲三种），
                    // 故统一走 CardManager.ResolveDeath：先广播死亡触发，再按去向离场。
                    if (source != null) { CM?.ResolveDeath(source, CardManager.DeathCause.Sacrifice); GD.Print($"[EffectEngine] 牺牲 {source.PlayerTemplate?.CardName}"); }
                    break;

                // 【P2】封印：把"被封印者"封到"宿主"上（唯一出口 TurnManager.SealCard，广播 OnSealed）。
                // Args 约定见 EffectAction 注释：[被封印者来源(自身/目标/牌库顶/手牌), 宿主(目标/自身), 附加(背面朝上)]。
                // 选择路径：被封印者取自手牌时有 UI → IGameUIHooks.PromptPickCard；无 UI → 确定性兜底（取第一张）。
                // 联机走 ExecuteVerbAsync 的网关分支（SelectHandCard 请求）。
                case ActionVerb.Seal:
                    ResolveSeal(action, source, target);
                    break;

                // 【P4】遏制X：由被遏制者（缺省=施术者对手）选择并移除他所拥有的 X 个势力标志。
                // Args 约定：[X（缺省1）, 被遏制者（缺省"对手"/"自己"）]。
                // 语义（用户规则）：X ≥ 该玩家标记总数 → 自动全清（不弹窗）；X < 总数 → 被遏制者按地区分配，
                // 每地区上限＝该地区该玩家的标记数。三条决策路径见 `CurbRule`。
                case ActionVerb.Curb:
                {
                    int curbX = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var cx)) ? cx : 1;
                    string curbWho = (action.Args != null && action.Args.Count > 1) ? action.Args[1] : "对手";
                    CurbRule.Resolve(this, source, curbX, curbWho);
                    break;
                }

                // 【P2】检索：从牌库（或牌库顶 N 张）中按筛选取一张，按去向放置，然后洗牌（口径见 ResolveSearch）。
                // Args 约定见 EffectAction 注释：[筛选, 去向, 附加, 范围]。联机走网关分支（SelectDeckCard）。
                case ActionVerb.Search:
                    ResolveSearch(action, source, target, targetAreaId);
                    break;

                // 【P2】展示：把一张牌**公开**给双方看（卡文"展示该牌"）。Args[0] = 对象：`目标`（缺省）/`自身`。
                // 与"检视"（只看自己的/对手的）区分：展示是公开信息 → 经 UiNotificationBus.CardRevealed 广播，
                // 由 View 呈现；无订阅者（测试/headless）时至少留公开日志，保证行为可观测。
                case ActionVerb.Reveal:
                {
                    var shown = ArgAt(action, 0, "目标") == "自身" ? source : (target ?? source);
                    RevealCardPublicly(shown, source?.ControllerId ?? shown?.ControllerId ?? -1);
                    break;
                }

                // 【P2】洗牌：Args[0] = 自己（缺省）/对手/双方。走 DeckManager 注入的可复现随机源。
                case ActionVerb.Shuffle:
                {
                    string who2 = (action.Args != null && action.Args.Count > 0) ? action.Args[0] : "自己";
                    foreach (var pid in ResolvePlayerSide(source, who2))
                        TurnManagerRef?.ShuffleDeckOf(pid);
                    break;
                }

                case ActionVerb.PlaceToken:
                    // 放置指示物（JC035：在结附地区放置一个本方势力标志；JC018 威名：在本地区放置一个本方势力标志）。
                    // Args[0]=指示物类型（"势力标志"等，仅记录/兜底），Args[1]=数量（缺省 1），
                    // Args[2]=可选去重标签（"威名" → 启用 D2 单次战斗维度去重）。
                    // 放置地区取 source 当前所属地区（结附地区的附属：source.ZoneDetailId 即结附地区）。
                    if (source != null && source.ControllerId >= 0)
                    {
                        string areaUid = source.ZoneDetailId ?? targetAreaId ?? "";
                        int amt = (action.Args.Count > 1 && int.TryParse(action.Args[1], out var p)) ? p : 1;
                        if (!string.IsNullOrEmpty(areaUid))
                        {
                            // D2 威名去重：同一控制者在同一地区（单次战斗对抗内）只放置一个势力标志。
                            bool isWeiMing = (action.Args.Count > 2 && action.Args[2] == "威名");
                            string dedupKey = _combatWonKey(areaUid, source.ControllerId);
                            if (isWeiMing && _combatWonMarkPlaced.ContainsKey(dedupKey))
                            {
                                GD.Print($"[EffectEngine] 放置势力标志（威名）去重跳过：地区 {areaUid} 玩家{source.ControllerId} 本次战斗已放置");
                                break;
                            }
                            if (isWeiMing) _combatWonMarkPlaced[dedupKey] = true;
                            TurnManagerRef?.AreaManagerRef?.AddPowerMark(areaUid, source.ControllerId, amt);
                            GD.Print($"[EffectEngine] 放置势力标志{(isWeiMing ? "（威名）" : "")}：地区 {areaUid} 玩家{source.ControllerId} ×{amt}（{(action.Args.Count > 0 ? action.Args[0] : "")}）");
                        }
                        else GD.Print($"[EffectEngine] ⚠ PlaceToken 跳过：无法确定放置地区（source={source.PlayerTemplate?.CardName}, ZoneDetailId={source.ZoneDetailId}）");
                    }
                    else GD.Print("[EffectEngine] ⚠ PlaceToken 跳过：source 为空或控制者无效");
                    break;

                case ActionVerb.Pay:
                {
                    // 【P1】支付（无色费用）：Args[0]=金额（缺省 1），为 source 的控制者支付。
                    // 语义：支付**失败**（费用不足）时置链式中止标志 → 同一动作序列的后续动作不再结算，
                    // 对应卡文"你可以支付N，若你如此做，则…"（如 JC061/JC100/JC105）。
                    // ⚠️ P1 限制：当前不做"是否支付"的玩家询问（自动能付则付）；带询问的版本见后续批次。
                    int payAmt = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var pv)) ? pv : 1;
                    int payer = source?.ControllerId ?? -1;
                    var pm = TurnManagerRef?.PlayerManagerRef;
                    if (payer < 0 || pm == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ Pay 跳过：payer={payer}, pm={(pm == null ? "null" : "ok")}（视为支付失败，中止后续动作）");
                        _actionChainAborted = true;
                        break;
                    }
                    if (payAmt <= 0) { GD.Print("[EffectEngine] Pay 金额<=0，视为已支付"); break; }
                    bool paid = pm.SpendMana(payer, payAmt);
                    GD.Print($"[EffectEngine] {(paid ? "✓" : "✗")} 玩家{payer} 支付 {payAmt} 费用（Pay 动词，{(paid ? "继续后续动作" : "中止后续动作")}）");
                    if (!paid) _actionChainAborted = true;
                    break;
                }

                // 【P2】弃牌：手牌 → 墓地（唯一出口 TurnManager.DiscardCards，逐张广播 OnDiscard）。
                // Args 约定见 EffectAction 注释：[张数, 弃牌者(自己/对手/双方), 选择方式(自选/随机/由施术者指定)]。
                // 选择路径：有 UI → IGameUIHooks.PromptDiscard（对手引用回调）；无 UI → 确定性兜底（弃第一张，供测试/headless）。
                // 联机走 ExecuteVerbAsync 的网关分支（SelectHandCard 请求）。
                case ActionVerb.Discard:
                {
                    int count = 1;
                    if (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var dc)) count = dc;
                    if (count <= 0) count = 1;
                    string who = (action.Args != null && action.Args.Count > 1) ? action.Args[1] : "自己";
                    string how = (action.Args != null && action.Args.Count > 2) ? action.Args[2] : "自选";
                    // 本次弃牌动作的"总张数"（OnDiscard 的 ExtraInt，供"遏制X（X=此次弃牌数量）"读取）
                    int batchSize = count;
                    if (how == "随机")
                    {
                        foreach (var pid in ResolvePlayerSide(source, who))
                            TurnManagerRef?.DiscardRandom(pid, count, batchSize);
                        break;
                    }
                    if (TurnManagerRef == null) { GD.Print("[EffectEngine] ⚠ Discard 跳过：TurnManagerRef 未注入"); break; }
                    foreach (var pid in ResolvePlayerSide(source, who))
                    {
                        int chooser = (how == "由施术者指定") ? (source?.ControllerId ?? pid) : pid;
                        PromptOrFallbackDiscard(pid, chooser, count, batchSize);
                    }
                    break;
                }

                // 【P3】在"卡上"放置标志/计数器（如"时间标志""锁定标志""哲人石标志"）。
                // Args 约定见 EffectAction 注释：[标志名, 数量, 对象(自身/目标)]。
                // 与 PlaceToken 的区别：PlaceToken 放的是**地区势力标记**（AreaManager），本动词放的是**卡上的标志**（CardManager.AddCounter）。
                case ActionVerb.PlaceCounter:
                {
                    string marker = ArgAt(action, 0, "");
                    if (string.IsNullOrEmpty(marker)) { GD.Print("[EffectEngine] ⚠ PlaceCounter 跳过：未指定标志名"); break; }
                    int cnt = 1;
                    if (action.Args != null && action.Args.Count > 1 && int.TryParse(action.Args[1], out var pc)) cnt = pc;
                    if (cnt <= 0) { GD.Print("[EffectEngine] PlaceCounter 数量<=0，跳过"); break; }
                    var subject = ArgAt(action, 2, "自身") == "目标" ? (target ?? source) : source;
                    if (subject == null) { GD.Print("[EffectEngine] ⚠ PlaceCounter 跳过：对象为空"); break; }
                    CM?.AddCounter(subject, marker, cnt);
                    int now = subject.Counters.TryGetValue(marker, out var cv) ? cv : 0;
                    GD.Print($"[EffectEngine] 放置标志：{subject.PlayerTemplate?.CardName} +{cnt} 个「{marker}」（当前 {now}）");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                case ActionVerb.RemoveToken:
                {
                    // 【P3·A】移除**地区势力标志**（与 PlaceToken 对称；卡上标志的移除见 PlaceCounter/RemoveCounter 语义）。
                    // Args：[指示物类型, 数量("全部" 缺省 / "N"), 归属("对手" 缺省 / "自己"), 地区("本地区" 缺省 / "目标地区")]
                    // 语义（用户 2026-09 口径）：**直接移除（不由对手分配）**，且**只作用于指定地区**
                    // （区别于 `Curb`＝X<总数时由被遏制者按地区分配、范围是全场）。
                    string scope = ArgAt(action, 3, "本地区");
                    string areaUid = scope == "目标地区"
                        ? (targetAreaId ?? "")
                        : (source?.ZoneDetailId ?? targetAreaId ?? "");
                    if (source == null || source.ControllerId < 0 || string.IsNullOrEmpty(areaUid))
                    {
                        GD.Print($"[EffectEngine] ⚠ RemoveToken 跳过：无法确定地区（source={source?.PlayerTemplate?.CardName}, zone={source?.ZoneDetailId}, targetArea={targetAreaId}）");
                        break;
                    }
                    int who = ArgAt(action, 2, "对手") == "自己"
                        ? source.ControllerId
                        : (source.ControllerId == 1 ? 2 : 1);
                    var areaMgr = TurnManagerRef?.AreaManagerRef;
                    if (areaMgr == null) { GD.Print("[EffectEngine] ⚠ RemoveToken 跳过：AreaManager 未注入"); break; }
                    int current = areaMgr.GetPowerMark(areaUid, who);
                    int amount = current;   // 缺省＝该地区该玩家的全部
                    string amtArg = ArgAt(action, 1, "全部");
                    if (amtArg != "全部" && int.TryParse(amtArg, out var n))
                        amount = System.Math.Min(System.Math.Max(0, n), current);
                    if (amount <= 0)
                    {
                        GD.Print($"[EffectEngine] RemoveToken：地区 {areaUid} 玩家{who} 无标志可移除（当前 {current}）");
                        break;
                    }
                    int removed = areaMgr.RemovePowerMark(areaUid, who, amount);
                    GD.Print($"[EffectEngine] 移除势力标志：地区 {areaUid} 玩家{who} ×{removed}（{ArgAt(action, 0, "势力标志")}，范围={scope}）");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                case ActionVerb.PutToArea:
                {
                    // 【P3·C】把**目标牌**放进"本次结算的目标地区"（JC092 死灵召唤 / JC133 操控死尸：
                    // 把**你墓地中的角色牌**以暗藏者状态 / 正面状态置于所选地区）。
                    // Args：[朝向("正面" 缺省 / "暗藏"), 附加子类别(可选，逗号分隔，如 "不死生物")]
                    // 语义：
                    //  · 目标地区＝`targetAreaId`（多目标模式下＝地区槽的值）；推不出地区则跳过；
                    //  · **暗藏**：先 `SetCovert(true)` 再移动（与"派遣暗藏者"、检索`暗藏进场`同序）；
                    //  · 移动走 `CardManager.MoveCardToArea` → 照常广播**进场触发**（EnterTrigger）；
                    //  · 附加子类别：对置入的卡 `Grant("SubCategory", 名)`（"额外具有"＝**叠加**，不替换原类别）。
                    var mover = target;
                    if (mover == null)
                    {
                        GD.Print("[EffectEngine] ⚠ PutToArea 跳过：目标牌为空");
                        break;
                    }
                    string areaUid = targetAreaId ?? "";
                    if (string.IsNullOrEmpty(areaUid))
                    {
                        GD.Print("[EffectEngine] ⚠ PutToArea 跳过：推不出目标地区");
                        break;
                    }
                    bool asCovert = ArgAt(action, 0, "正面") == "暗藏";
                    if (asCovert) mover.SetCovert(true);   // 先翻面再进场
                    CM?.MoveCardToArea(mover, areaUid);
                    string extraSubs = ArgAt(action, 1, "");
                    if (!string.IsNullOrEmpty(extraSubs))
                    {
                        foreach (var sub in extraSubs.Split(','))
                        {
                            string s = sub?.Trim();
                            if (string.IsNullOrEmpty(s)) continue;
                            mover.Grant("SubCategory", s, 1);
                            GD.Print($"[EffectEngine] PutToArea 额外授予子类别：{mover.PlayerTemplate?.CardName} += 「{s}」");
                        }
                    }
                    GD.Print($"[EffectEngine] PutToArea：{mover.PlayerTemplate?.CardName} {(asCovert ? "以暗藏者状态" : "正面")}进入地区 {areaUid}");
                    break;
                }

                // 【K11】可选支付：**弹数值输入框**问玩家"是否支付 N"（用户 2026-09 口径：输入的数字即为 N）。
                // Args：[默认金额(缺省1), 最小(缺省0), 最大(缺省=默认金额)]。
                // 语义：支付 → 扣 N 点无色费用并继续后续动作；**不支付/N=0/费用不足 → 置链式中止**（"若你如此做…"整段跳过）。
                case ActionVerb.PayOptional:
                {
                    int defAmt = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var d0)) ? d0 : 1;
                    int minAmt = (action.Args != null && action.Args.Count > 1 && int.TryParse(action.Args[1], out var d1)) ? d1 : 0;
                    int maxAmt = (action.Args != null && action.Args.Count > 2 && int.TryParse(action.Args[2], out var d2)) ? d2 : defAmt;
                    int payer2 = source?.ControllerId ?? -1;
                    var pm2 = TurnManagerRef?.PlayerManagerRef;
                    if (payer2 < 0 || pm2 == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ PayOptional 跳过：payer={payer2}, pm={(pm2 == null ? "null" : "ok")}（视为未支付，中止后续动作）");
                        _actionChainAborted = true;
                        break;
                    }
                    int avail = pm2.GetAvailableMana(payer2);
                    int hardMax = System.Math.Min(maxAmt, avail);      // 不可支付超过可用费用
                    int hardMin = System.Math.Max(0, System.Math.Min(minAmt, hardMax));
                    string prompt = $"{source?.PlayerTemplate?.CardName}：是否支付？可支付 0～{hardMax}（当前可用 {avail}）";

                    if (UIHooks != null)
                    {
                        // 挂起当前动作链：回调里扣费并续算剩余动作（不支付 → 置中止标志，后续整段跳过）
                        SuspendChainForInput();
                        UIHooks.PromptNumberInput(payer2, prompt, hardMin, hardMax, System.Math.Clamp(defAmt, hardMin, hardMax),
                            "支付", "不支付", chosen =>
                            {
                                if (chosen <= 0)
                                {
                                    GD.Print($"[EffectEngine] 玩家{payer2} 选择不支付 → 中止后续动作（PayOptional）");
                                    _actionChainAborted = true;
                                }
                                else
                                {
                                    bool ok2 = pm2.SpendMana(payer2, chosen);
                                    GD.Print($"[EffectEngine] {(ok2 ? "✓" : "✗")} 玩家{payer2} 可选支付 {chosen}（PayOptional）");
                                    if (!ok2) _actionChainAborted = true;
                                }
                                ContinuePendingChain();
                            });
                        break;   // 已挂起：ExecuteActionsFrom 会因 _pendingChain 非空而停止并记录续算点
                    }
                    // 无 UI（测试/headless）：确定性兜底＝不支付（绝不替玩家花钱），中止后续动作
                    GD.Print("[EffectEngine] ⚠ PayOptional 无 UI：确定性兜底＝不支付（中止后续动作）");
                    _actionChainAborted = true;
                    break;
                }

                // 【K1·JC083】展示来源控制者的**牌库顶一张牌**，按"印刷费用是否等于玩家所选数字"分流：
                // 相同 → 置入其**手牌**（FAQ/口径 A31：**不算抓牌** → 不广播 `OnDraw`）；
                // 不相同 → 置入其**牌库底**。牌库为空 → 无事发生。
                // Args[0]＝期望的印刷费用（通常写 `$X`，由"玩家选择"型动态值代入）。
                case ActionVerb.RevealTopAndTakeIfCostIs:
                {
                    int want = int.TryParse(ArgAt(action, 0, "0"), out var wantCost) ? wantCost : 0;
                    int owner = source?.ControllerId ?? -1;
                    var deck = TurnManagerRef?.GetDeckOf(owner);
                    if (deck == null || deck.Count == 0)
                    {
                        GD.Print($"[EffectEngine] RevealTopAndTakeIfCostIs：玩家{owner} 牌库为空，无事发生");
                        break;
                    }
                    var topCard = deck[0];   // 牌库约定：索引 0 = 牌库顶
                    if (topCard == null) break;
                    int topCost = topCard.PlayerTemplate?.Cost ?? -1;
                    RevealCardPublicly(topCard, owner);   // "展示牌库顶的一张牌"＝公开展示（不改变规则状态）
                    if (topCost == want)
                    {
                        CM?.MoveCardToHand(topCard);
                        GD.Print($"[EffectEngine] 🔎 牌库顶「{topCard.PlayerTemplate?.CardName}」印刷费用 {topCost} ＝ 所选 {want} → 置入手牌（**不算抓牌**，不广播 OnDraw）");
                    }
                    else
                    {
                        TurnManagerRef?.ReturnCardToDeck(owner, topCard.UID, toTop: false);
                        GD.Print($"[EffectEngine] 🔎 牌库顶「{topCard.PlayerTemplate?.CardName}」印刷费用 {topCost} ≠ 所选 {want} → 置入牌库底");
                    }
                    break;
                }

                // 【JC114】"**你可以从中选择一张，并牺牲好奇的黑客，若你如此做**，该玩家弃掉你所选择的牌"：
                // **可选的牺牲自身**——与 `PayOptional` 同一交互原语（数值输入窗 0/1，按钮"牺牲/不牺牲"）：
                //  · 选择牺牲 → `ResolveDeath(Sacrifice)`（**牺牲＝死亡，照常触发死亡时机**）→ 动作链**继续**
                //    （后续动作以"已捕获的来源/控制者"结算，如 JC015/JC096 的牺牲代价同款语义）；
                //  · 不牺牲 / 无 UI → 置 `_actionChainAborted` → **后续动作整段跳过**（"若你如此做"）。
                case ActionVerb.SacrificeSelfOptional:
                {
                    if (source == null || CM == null)
                    {
                        GD.Print("[EffectEngine] ⚠ SacrificeSelfOptional 跳过：来源或 CardManager 缺失（视为不牺牲）");
                        _actionChainAborted = true;
                        break;
                    }
                    int sacChooser = source.ControllerId;
                    string sacPrompt = $"{source.PlayerTemplate?.CardName}：是否牺牲它？（牺牲后才弃掉你指定的那张牌）";
                    // 落地（单机回调 / 联机回包共用同一份，避免两条路径口径分叉）
                    System.Action<bool> landSac = sacrifice =>
                    {
                        if (!sacrifice)
                        {
                            GD.Print($"[EffectEngine] 玩家{sacChooser} 选择**不牺牲** → 中止后续动作（SacrificeSelfOptional）");
                            _actionChainAborted = true;
                        }
                        else
                        {
                            GD.Print($"[EffectEngine] ✝ 可选牺牲：{source.PlayerTemplate?.CardName} 被牺牲（发动者选择）");
                            CM.ResolveDeath(source, CardManager.DeathCause.Sacrifice);
                            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                        }
                    };
                    // ① 联机：经网关下发 0/1 二选一（与服务端同语义）
                    if (UIHooks == null && BeginGatewayFlow(() => AskZeroOrOneViaGatewayAsync(sacChooser, sacPrompt, "牺牲", "不牺牲", landSac)))
                        break;
                    // ② 单机：数值输入窗（0/1）
                    if (UIHooks != null)
                    {
                        SuspendChainForInput();
                        UIHooks.PromptNumberInput(sacChooser, sacPrompt, 0, 1, 0, "牺牲", "不牺牲",
                            chosen => { landSac(chosen > 0); ContinuePendingChain(); });
                        break;   // 已挂起：ExecuteActionsFrom 记录续算点
                    }
                    // ③ 无 UI / 无网关：确定性兜底＝**不牺牲**（绝不替玩家牺牲）
                    GD.Print("[EffectEngine] ⚠ SacrificeSelfOptional 无 UI/网关：确定性兜底＝**不牺牲**");
                    landSac(false);
                    break;
                }

                // 【P3·K4·JC027/JC028】**实例级形态转换**：把本效果声明的 `FormChange` 施加到对象卡上
                //（**只改实例**：类别覆盖 / 卡名覆盖 / 印刷效果失活 + 形态效果表 / 印刷图标与防御清零 / 追加子类别）。
                // Args：[对象（`自身` 缺省 / `目标`）, 期限（`常驻` 缺省 / `直到回合结束`）]。
                // 期限含"回合结束"时登记时效项（`TimedMod{Kind="Form"}`），到期由 `ExpireTurnEndEffects` 撤销形态。
                case ActionVerb.TransformForm:
                {
                    var spec = _currentEffectData?.FormChange;
                    if (spec == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ TransformForm 跳过：{_currentEffectData?.EffectId} 未声明 FormChange");
                        break;
                    }
                    var subject = ArgAt(action, 0, "自身") == "目标" ? (target ?? source) : source;
                    if (subject == null) { GD.Print("[EffectEngine] ⚠ TransformForm 跳过：对象为空"); break; }
                    string term = ArgAt(action, 1, "常驻");
                    // 【顺序关键】先按**印刷效果表**注销旧的持续/静止登记（否则"原有的效果"会残留生效），
                    // 再施加形态覆盖，最后按**形态效果表**重新注册（JC028"损失了原有的效果"）。
                    bool willSuppress = spec.SuppressPrintedEffects;
                    if (willSuppress) UnregisterPersistent(subject);
                    subject.ApplyFormOverride(
                        ToCardCategory(spec.Category),
                        spec.Name,
                        spec.SuppressPrintedEffects,
                        spec.ZeroPrintedStatistics,
                        spec.Effects);
                    GD.Print($"[EffectEngine] 🧬 形态转换：{subject.PlayerTemplate?.CardName} → "
                           + $"类别={subject.EffectiveCategoryFlag} 名={subject.EffectiveCardName}"
                           + $"（{(spec.SuppressPrintedEffects ? "印刷效果失活" : "保留印刷效果")}，期限={term}）");
                    // 追加子类别（JC027「地精」）：走既有子类别授予通道
                    foreach (var sub in spec.AddSubCategories ?? new Array<string>())
                    {
                        if (string.IsNullOrWhiteSpace(sub)) continue;
                        subject.Grant("SubCategory", sub.Trim(), 1);
                    }
                    if (willSuppress)
                    {
                        subject.ResetToBaseValues(false);
                        if (subject.CurrentZone == CardZone.AreaZone || subject.CurrentZone == CardZone.AttachedZone)
                            RegisterPersistent(subject);
                    }
                    if (term.Contains("回合结束"))
                        RegisterTimedMod(new TimedMod
                        {
                            TargetUid = subject.UID,
                            Kind = "Form",
                            Value = string.Join(",", spec.AddSubCategories ?? new Array<string>()),
                            ExpireTurn = CurrentTurn,
                        });
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【P3·K21·JC064 戒严】"准备步骤**强制**触发：**支付1，否则牺牲戒严**"（用户口径 A19：
                // **由玩家选择**支付或牺牲）——与 `PayOptional`/`SacrificeSelfOptional` 同一交互原语：
                //  · **可支付**（费用 ≥ N）→ 弹 0/1 窗口（确认＝"支付 N" / 取消＝"牺牲"）：
                //    选择支付 → `SpendMana(N)` 后动作链继续；选择牺牲 → `ResolveDeath(Sacrifice)`；
                //  · **不可支付**（费用不足）→ **不询问、直接牺牲**（卡文"否则"的必然结果）；
                //  · 无 UI → 确定性兜底＝**能付就付**（不替玩家放弃已能支付的代价）。
                case ActionVerb.PayOrSacrificeSelf:
                {
                    int amount = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var amt3))
                        ? System.Math.Max(0, amt3) : 1;
                    if (source == null || CM == null) { GD.Print("[EffectEngine] ⚠ PayOrSacrificeSelf 跳过：来源缺失"); break; }
                    int payer3 = source.ControllerId;
                    var pm3 = TurnManagerRef?.PlayerManagerRef;
                    bool canPay = amount == 0 || (pm3 != null && pm3.GetAvailableMana(payer3) >= amount);

                    if (!canPay)
                    {
                        GD.Print($"[EffectEngine] ✝ 可用费用不足 {amount} → 直接牺牲 {source.PlayerTemplate?.CardName}（PayOrSacrificeSelf）");
                        CM.ResolveDeath(source, CardManager.DeathCause.Sacrifice);
                        Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                        break;
                    }
                    string posPrompt = $"{source.PlayerTemplate?.CardName}：支付 {amount} 还是牺牲它？（当前可用 {pm3?.GetAvailableMana(payer3) ?? 0}）";
                    // 落地（单机回调 / 联机回包共用）
                    System.Action<bool> landPay = pay =>
                    {
                        if (!pay)
                        {
                            GD.Print($"[EffectEngine] ✝ 玩家{payer3} 选择不支付 → 牺牲 {source.PlayerTemplate?.CardName}");
                            CM.ResolveDeath(source, CardManager.DeathCause.Sacrifice);
                        }
                        else
                        {
                            bool ok3 = pm3 == null || amount == 0 || pm3.SpendMana(payer3, amount);
                            GD.Print($"[EffectEngine] {(ok3 ? "✓" : "✗")} 玩家{payer3} 支付 {amount}（PayOrSacrificeSelf）");
                            if (!ok3) CM.ResolveDeath(source, CardManager.DeathCause.Sacrifice);   // 支付失败 → 兜底牺牲
                        }
                        Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    };
                    // ① 联机：经网关下发 0/1 二选一
                    if (UIHooks == null && BeginGatewayFlow(() => AskZeroOrOneViaGatewayAsync(payer3, posPrompt, $"支付 {amount}", "牺牲", landPay)))
                        break;
                    // ② 单机：数值输入窗（0/1）
                    if (UIHooks != null)
                    {
                        SuspendChainForInput();
                        UIHooks.PromptNumberInput(payer3, posPrompt, 0, 1, 1, $"支付 {amount}", "牺牲",
                            chosen => { landPay(chosen > 0); ContinuePendingChain(); });
                        break;   // 已挂起
                    }
                    // ③ 无 UI / 无网关：确定性兜底＝**能付就付**（不替玩家放弃已能支付的代价）
                    GD.Print($"[EffectEngine] ⚠ PayOrSacrificeSelf 无 UI/网关：确定性兜底＝支付 {amount}（不必牺牲）");
                    landPay(true);
                    break;
                }

                // 【JC119 守秘人】"死亡触发：**展示封印在守秘人上的所有牌**，你可以**打出其中一张**，
                // 视作它具有【快速】且不需要支付其费用（但仍需满足忠诚需求）。将不打出的牌移回其拥有者手上。"
                // 用户 2026-09 口径（本轮答复）：
                //  · 死亡触发在**原子锁之后**结算；此时**封印牌已按规则回到拥有者手牌**（`ZoneManager.DetachSealedCards`
                //    置 `ReturnedFromSealHostUid` 标记）→ **直接从手牌**取那批牌（"刚刚由于封印效果回到手牌的牌"）；
                //  · 顺序＝**先公开展示全部** → **由你选一张**（可取消＝不打）→ **走正常打出流程**
                //    （不在结算期嵌套打出：登记到 `TurnManager` 的免费打出队列，原子锁解除后按
                //    `PlayCard(freePlay:true, asIfQuick:true)` 执行＝**免费 + 视作快速 + 忠诚照常校验**）；
                //  · "将不打出的牌移回其拥有者手上"＝**天然成立**（它们本就在手牌）。
                case ActionVerb.RevealSealedAndQueueFreePlay:
                {
                    if (source == null || CM == null || TurnManagerRef == null) { GD.Print("[EffectEngine] ⚠ RevealSealedAndQueueFreePlay 跳过：依赖缺失"); break; }
                    int owner = source.ControllerId;
                    var hand = ZoneManagerRef?.GetZoneCards(CardZone.Hand, owner)
                        ?? new System.Collections.Generic.List<CardInstance>();
                    var candidates = new System.Collections.Generic.List<CardInstance>();
                    foreach (var c in hand)
                    {
                        if (c == null || c.IsDying) continue;
                        if (!string.Equals(c.ReturnedFromSealHostUid ?? "", source.UID, System.StringComparison.Ordinal)) continue;
                        if (TurnManagerRef.HasQueuedFreePlay(c.UID)) continue;      // 已登记过（防重复）
                        candidates.Add(c);
                    }
                    if (candidates.Count == 0)
                    {
                        GD.Print($"[EffectEngine] RevealSealedAndQueueFreePlay：{source.PlayerTemplate?.CardName} 没有『刚由封印回手』的牌 → 无事发生");
                        break;
                    }
                    // ① 先**公开展示全部**（卡文"展示封印在守秘人上的所有牌"）
                    foreach (var c in candidates) RevealCardPublicly(c, owner);
                    // ② 由控制者选一张（可取消＝不打）；无 UI → 兜底不打出（绝不替玩家出牌）
                    if (UIHooks == null)
                    {
                        GD.Print("[EffectEngine] ⚠ RevealSealedAndQueueFreePlay 无 UI：兜底＝不打出（牌留在手牌）");
                        break;
                    }
                    SuspendChainForInput();
                    UIHooks.PromptPickCard(owner,
                        $"{source.PlayerTemplate?.CardName}：可免费打出其中一张（视作快速；不需付费，仍需忠诚）",
                        candidates, allowCancel: true, picked =>
                        {
                            if (picked == null)
                            {
                                GD.Print($"[EffectEngine] 玩家{owner} 选择不打出封印牌 → 全部留在手牌");
                            }
                            else if (picked.PlayerTemplate == null || !TurnManagerRef.PlayerManagerRef.CanPayLoyalty(owner, picked.PlayerTemplate))
                            {
                                GD.PrintErr($"[EffectEngine] ⛔ 免费打出被拒：{picked.PlayerTemplate?.CardName} 不满足忠诚需求 → 留在手牌");
                            }
                            else
                            {
                                TurnManagerRef.EnqueueFreePlay(owner, picked.UID);
                            }
                            ContinuePendingChain();
                        });
                    break;   // 已挂起
                }

                // 【P3·G】把某张牌移到指定区域（"成为一个资产 / 移回手上"等去处转换）。
                // Args：[对象(自身/目标/牌库顶), 目的区(资产区/争夺区/手牌/牌库底), 朝向(正面/暗藏)]。
                case ActionVerb.MoveToZone:
                {
                    string subject = ArgAt(action, 0, "自身");
                    string dest = ArgAt(action, 1, "资产区");
                    bool asCovert = ArgAt(action, 2, "正面") == "暗藏";

                    CardInstance card = null;
                    int ownerForZone = source?.ControllerId ?? -1;
                    if (subject == "目标")
                    {
                        card = target;
                    }
                    else if (subject == "牌库顶")
                    {
                        // 来源控制者牌库顶一张（牌库约定：索引 0 = 牌库顶）
                        if (ownerForZone < 0 || TurnManagerRef == null)
                        {
                            GD.Print("[EffectEngine] ⚠ MoveToZone 跳过：牌库顶取牌需要有效的来源控制者与 TurnManager");
                            break;
                        }
                        var deck = TurnManagerRef.GetDeckOf(ownerForZone);
                        if (deck == null || deck.Count == 0)
                        {
                            GD.Print($"[EffectEngine] MoveToZone：玩家{ownerForZone} 牌库为空，无事发生");
                            break;
                        }
                        card = deck[0];
                        TurnManagerRef.RemoveCardFromDeckOf(card.OwnerId, card);   // 从牌库列表移出（随后由下方移动收口）
                    }
                    else
                    {
                        card = source;
                    }
                    if (card == null) { GD.Print("[EffectEngine] ⚠ MoveToZone 跳过：对象为空"); break; }

                    if (asCovert) card.SetCovert(true);
                    else card.SetCovert(false);   // "正面"（暗藏只存在于场上；对资产区无副作用）

                    switch (dest)
                    {
                        case "资产区":
                            ZoneManagerRef?.MoveToAsset(card, source?.ControllerId ?? card.ControllerId);
                            GD.Print($"[EffectEngine] MoveToZone：{card.PlayerTemplate?.CardName} → 资产区（玩家{source?.ControllerId ?? card.ControllerId}）");
                            break;
                        case "争夺区":
                        {
                            string area = !string.IsNullOrEmpty(targetAreaId) ? targetAreaId : (source?.ZoneDetailId ?? "");
                            if (string.IsNullOrEmpty(area)) { GD.Print("[EffectEngine] ⚠ MoveToZone：推不出争夺区落点，跳过"); break; }
                            CM?.MoveCardToArea(card, area);
                            // 【JC124】`Args[3]="拥有者"`：回场后**由拥有者操控**（卡文"并由其拥有者操控"）——
                            // 走夺控统一出口 `ZoneManager.ChangeController`（OwnerId 不变、控制者改为拥有者），
                            // 用于"该角色此前被夺控（如 JC036 初拥）后死亡再回场"的场合。
                            if (ArgAt(action, 3, "") == "拥有者" && card.ControllerId != card.OwnerId)
                            {
                                ZoneManagerRef?.ChangeController(card, card.OwnerId);
                                GD.Print($"[EffectEngine] MoveToZone：{card.PlayerTemplate?.CardName} 回场后交回**拥有者**（玩家{card.OwnerId}）操控");
                            }
                            GD.Print($"[EffectEngine] MoveToZone：{card.PlayerTemplate?.CardName} → 争夺区 {area}");
                            break;
                        }
                        case "手牌":
                            CM?.MoveCardToHand(card);
                            GD.Print($"[EffectEngine] MoveToZone：{card.PlayerTemplate?.CardName} → 拥有者手牌");
                            break;
                        case "牌库底":
                            // 【秘社批次·MSJC010②③】`Args[3]` = 牌库归属：`"拥有者"`（缺省，保持既有语义）/
                            // 缺省取 `card.OwnerId`；显式写 `"自己"` 时取**来源控制者**的牌库
                            // （卡文"置于**你的**牌库底"——封印牌的拥有者未必是秘社控制者，必须能分流）。
                            {
                                int deckOwner = card.OwnerId;
                                if (ArgAt(action, 3, "拥有者") == "自己" && source != null && source.ControllerId > 0)
                                    deckOwner = source.ControllerId;
                                TurnManagerRef?.ReturnCardToDeck(card, deckOwner, toTop: false);
                                GD.Print($"[EffectEngine] MoveToZone：{card.PlayerTemplate?.CardName} → 玩家{deckOwner} 牌库底");
                            }
                            break;
                        default:
                            GD.Print($"[EffectEngine] ⚠ MoveToZone：未登记的目的区「{dest}」→ 跳过");
                            break;
                    }
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【秘社批次】把**目标牌**（封印在秘社上 / 在你墓地中的角色牌）**正面进场**到本次结算的目标地区。
                // 与既有 `MoveToZone` 的区别：来源是**封印区/墓地**，且**要先解除封印关系**才移动
                // （否则卡会同时"在封印区"与"在争夺区"）。独占自 MSJC010③ / MSJC016②。
                case ActionVerb.UnsealToArea:
                    ResolveUnsealToArea(action, source, target, targetAreaId);
                    break;

                // 【秘社批次·MSJC015①】从**手牌**选一张符合条件的牌，展示后以指定朝向放进目标地区。
                case ActionVerb.PlaceHandCardToArea:
                    ResolvePlaceHandCardToArea(action, source, targetAreaId);
                    break;

                // 【秘社批次·MSJC013②】消灭"所有其上有 ≥N 个某标志的角色和附属"，然后按**实际被消灭数**抓牌。
                case ActionVerb.DestroyWithMinCounters:
                {
                    string marker = ArgAt(action, 0, "时间标志");
                    int needCounters = (action.Args != null && action.Args.Count > 1
                                        && int.TryParse(action.Args[1], out var nc)) ? nc : 3;
                    var zmRef = ZoneManagerRef;
                    if (zmRef == null)
                    { GD.Print("[EffectEngine] ⚠ DestroyWithMinCounters 跳过：ZoneManager 缺失"); break; }

                    // 先快照集合：消灭会改写区域桶，边遍历边删会漏/错。
                    var victims = new System.Collections.Generic.List<CardInstance>();
                    foreach (var c in zmRef.GetZoneCards(CardZone.AreaZone, 0, null)
                                 ?? new System.Collections.Generic.List<CardInstance>())
                    {
                        if (c == null || c.IsDying) continue;
                        int have = 0;
                        c.Counters?.TryGetValue(marker, out have);
                        if (have >= needCounters) victims.Add(c);
                    }
                    int destroyed = 0;
                    foreach (var v in victims)
                    {
                        if (v.IsDying) continue;
                        CM?.DestroyInstance(v);          // 消灭＝走死亡统一出口（照常触发死亡时机）
                        destroyed++;
                    }
                    GD.Print($"[EffectEngine] 💀 DestroyWithMinCounters：{marker}≥{needCounters} 的卡 {victims.Count} 张，实际消灭 {destroyed} 张");
                    // "然后**每有一个**…因此法被消灭，就抓一张牌"（用户口径：按实际消灭数）
                    if (destroyed > 0 && source != null && source.ControllerId > 0)
                    {
                        TurnManagerRef?.DrawCardsForPlayer(source.ControllerId, destroyed);
                        GD.Print($"[EffectEngine] 🃏 因本法被消灭 {destroyed} 张 → 玩家{source.ControllerId} 抓 {destroyed} 张");
                    }
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【秘社批次·MSJC016①】把**你牌库顶的 N 张牌**置于你的墓地（不是"消灭"，不触发死亡时机）。
                case ActionVerb.MillTopToGraveyard:
                {
                    int n = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var mn)) ? mn : 1;
                    int who = source?.ControllerId ?? -1;
                    if (who < 0 || TurnManagerRef == null)
                    { GD.Print("[EffectEngine] ⚠ MillTopToGraveyard 跳过：来源控制者/TurnManager 缺失"); break; }
                    int moved = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var deck = TurnManagerRef.GetDeckOf(who);
                        if (deck == null || deck.Count == 0)
                        { GD.Print($"[EffectEngine] MillTopToGraveyard：玩家{who} 牌库已空，停止（已磨 {moved} 张）"); break; }
                        var card = deck[0];                                    // 索引 0 = 牌库顶
                        TurnManagerRef.RemoveCardFromDeckOf(who, card);         // 先从牌库列表移出
                        ZoneManagerRef?.MoveToGraveyard(card, who);            // 再入墓地（唯一收口）
                        moved++;
                    }
                    GD.Print($"[EffectEngine] ⚫ 磨牌：玩家{who} 牌库顶 {moved} 张 → 墓地（不触发死亡）");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【秘社批次·MSJC017②】把**某张牌**置于你的**牌库顶**（Args[0]＝目标（缺省）/自身）。
                case ActionVerb.PutCardOnDeckTop:
                {
                    var subject = ArgAt(action, 0, "目标") == "自身" ? source : (target ?? source);
                    if (subject == null) { GD.Print("[EffectEngine] ⚠ PutCardOnDeckTop 跳过：对象为空"); break; }
                    if (subject.CurrentZone == CardZone.Deck)
                    { GD.Print($"[EffectEngine] PutCardOnDeckTop 跳过：{subject.PlayerTemplate?.CardName} 已在牌库"); break; }
                    TurnManagerRef?.ReturnCardToDeck(subject, subject.OwnerId, toTop: true);
                    GD.Print($"[EffectEngine] ⬆ 置牌库顶：{subject.PlayerTemplate?.CardName} → 玩家{subject.OwnerId} 牌库顶");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【P3】获得控制权（JC129「行动：你获得目标敌方角色的控制权**直到回合结束**」）。
                // Args[0]＝对象（`目标` 缺省 / `自身`）；Args[1]＝期限（`直到回合结束` 缺省 / `常驻`）。
                // 语义：只改**控制者**（`ControllerId`，OwnerId 不变，与引擎内各处"夺控"口径一致）；
                // 走 `ZoneManager.ChangeController` 统一出口（版本号/光环/前提取舍/UI 一并刷新）；
                // 期限为"直到回合结束"时登记时效项，回合结束由 `ExpireTurnEndEffects` **归还**给原控制者。
                case ActionVerb.GainControl:
                {
                    var subject = (action.Args != null && action.Args.Count > 0 && action.Args[0] == "自身")
                        ? source : (target ?? source);
                    int newController = source?.ControllerId ?? -1;
                    if (subject == null || newController <= 0)
                    { GD.Print("[EffectEngine] ⚠ GainControl 跳过：对象或来源控制者无效"); break; }
                    if (subject.ControllerId == newController)
                    { GD.Print($"[EffectEngine] GainControl 跳过：{subject.PlayerTemplate?.CardName} 已由玩家{newController} 控制"); break; }
                    string term = (action.Args != null && action.Args.Count > 1) ? action.Args[1] : "直到回合结束";
                    int original = subject.ControllerId;
                    ZoneManagerRef?.ChangeController(subject, newController);
                    GD.Print($"[EffectEngine] 👑 获得控制权：{subject.PlayerTemplate?.CardName} 由玩家{original} → 玩家{newController}（{term}）");
                    if (term.Contains("回合结束"))
                        RegisterTimedMod(new TimedMod
                        {
                            TargetUid = subject.UID,
                            Kind = "Control",
                            PlayerId = original,          // 到期归还给**原控制者**
                            ExpireTurn = CurrentTurn,
                        });
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【P3·K2 扩展】消灭"**目标卡上**"的整组卡：卡文"消灭该角色或附属上**封印的所有牌**"（JC013）／
                // "消灭目标角色上的**所有敌方附属**"（JC017）——即集合＝宿主卡的附属/封印牌（K2 群体此前只支持
                // "地区内群体"，无法表达"某张卡上的集合"）。
                // Args[0]＝集合来源：`附属`（缺省，宿主卡上的附属）/`封印牌`（宿主卡上被封印的牌）；
                // Args[1]＝归属筛选：`任意`（缺省）/`本方`/`对手`（基准＝**来源卡控制者**）。
                // 对象＝target（宿主卡）。集合为空 → 什么都不做（**不**置链式中止：卡文"消灭…。该角色获得屏障"
                // 的后续动作不以消灭成功为前提）。
                case ActionVerb.DestroyOnTarget:
                {
                    var host = target ?? source;
                    if (host == null) { GD.Print("[EffectEngine] ⚠ DestroyOnTarget 跳过：宿主为空"); break; }
                    string setKind = ArgAt(action, 0, "附属");
                    string side = ArgAt(action, 1, "任意");
                    var pool = new System.Collections.Generic.List<CardInstance>();
                    if (setKind == "封印牌")
                    {
                        if (host.SealedCards != null) pool.AddRange(host.SealedCards);
                    }
                    else if (setKind == "附属")
                    {
                        if (host.AttachedCards != null) pool.AddRange(host.AttachedCards);
                    }
                    else
                    {
                        GD.PushWarning($"[EffectEngine] DestroyOnTarget 集合来源「{setKind}」未登记（支持 附属/封印牌）→ 跳过");
                        break;
                    }
                    int me = source?.ControllerId ?? -1;
                    var hits = new System.Collections.Generic.List<CardInstance>();
                    foreach (var c in pool)
                    {
                        if (c == null || c.IsDying) continue;
                        if (side == "本方" && c.ControllerId != me) continue;
                        if (side == "对手" && c.ControllerId == me) continue;
                        hits.Add(c);
                    }
                    GD.Print($"[EffectEngine] 消灭宿主上的{setKind}（{side}）：{host.PlayerTemplate?.CardName} 上共 {hits.Count} 张");
                    // 先快照再逐张消灭：离场时 ZoneManager 的 detach 逻辑会改写宿主的 AttachedCards/SealedCards。
                    foreach (var c in hits)
                    {
                        GD.Print($"[EffectEngine] 消灭（宿主上的{setKind}）：{c.PlayerTemplate?.CardName}");
                        CM?.DestroyInstance(c);
                    }
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【P3·H】检视（私密查看）：不改状态，只把该卡的面私下呈现给**来源控制者**。
                // 与 `Reveal`（公开展示，双方可见）相对；单机走总线通知，联机走 `Kind.InspectCards` 请求。
                // 【K24】对象还支持"目标玩家"侧的 `目标玩家牌库顶` / `目标玩家手牌`。
                case ActionVerb.Inspect:
                {
                    int inspector = source?.ControllerId ?? -1;
                    if (inspector < 0) { GD.Print("[EffectEngine] ⚠ Inspect 跳过：检视者（来源控制者）未知"); break; }
                    foreach (var c in ResolveInspectTargets(action, source, target))
                        InspectCardPrivately(c, inspector);
                    break;
                }

                // 【P3·K3 第一批】防止伤害（JC078 光能盾：「本回合中，为目标角色防止接下来将受到的所有伤害」）。
                // · `Args[0]` = 对象：`"目标"`（缺省，= target）/`"自身"`（= source）；与 `Inspect` 同口径——
                //   对象＝目标而当前无目标 → **跳过**（不回退成来源卡，不做"意外护住来源卡"的事）。
                // · 期限＝**本回合**（卡文"本回合中"）：挂 `TimedMod{Kind="DamageShield"}`，回合结束随栈出栈失效。
                // · 消费点＝`CardManager.DamagePreventer`（装配层与本包"灵体"防止**并联**）：
                //   被防止 → 不写伤害 → 不判死 → **不触发死亡**（死亡触发的前提是死亡真的发生）。
                case ActionVerb.PreventDamage:
                {
                    string wardArg = ArgAt(action, 0, "目标");
                    CardInstance ward = wardArg == "自身" ? source : target;
                    if (ward == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ PreventDamage 跳过：对象='{wardArg}' 但当前无目标（不回退成来源卡）");
                        break;
                    }
                    RegisterDamageShield(ward);
                    break;
                }

                case ActionVerb.SkipAreaCombat:
                {
                    // 【P3·K18】"本回合中该地区略过对抗阶段"（JC118 停战协议）：对**本次结算的目标地区**登记时效。
                    // Args[0] = 对抗名清单（`,` 分隔；缺省/空 → 三步骤全部略过）；
                    // 落点地区＝`targetAreaId`（打出时选定的目标地区），缺省回退来源所在地区（并告警）。
                    var names = new System.Collections.Generic.List<string>();
                    string spec = ArgAt(action, 0, "");
                    foreach (var part in (spec ?? "").Split(','))
                        if (!string.IsNullOrWhiteSpace(part)) names.Add(part.Trim());
                    string skipArea = !string.IsNullOrEmpty(targetAreaId) ? targetAreaId : (source?.ZoneDetailId ?? "");
                    if (string.IsNullOrEmpty(skipArea))
                        GD.Print($"[EffectEngine] ⚠ SkipAreaCombat：既无目标地区也无来源地区，跳过");
                    else
                        RegisterAreaCombatSkipThisTurn(skipArea, names);
                    break;
                }

                case ActionVerb.ReplaceIcon:
                {
                    // 【P3·K20】**图标替换＝属性替换**（用户 2026-09 口径）：把某卡上"指定能力图标"的**有效贡献**
                    // 全部换成另一种图标对应的属性；整套替换在**效果内部一次做完**（原子锁期间不会插入其它触发）。
                    // Args[0] = 来源图标清单（`,` 分隔，如 `"黑星,白星"`）；Args[1] = 目标图标（如 `"黑刀"`）；
                    // Args[2] = 对象（`"目标"` 缺省 / `"自身"` / `"结附宿主"`）。
                    // 计数＝该属性的**当前有效贡献**（如 势力＝`CurrentPower`、先手势力＝授予的『先手势力』），
                    // 故替换后该卡**不再提供任何来源图标的属性**；`Duration` 含"回合结束"时登记时效项（到期精确还原）。
                    CardInstance rSubject = ArgAt(action, 2, "目标") switch
                    {
                        "自身" => source,
                        "结附宿主" => source?.AttachedTo,
                        _ => target ?? source,
                    };
                    string dstIcon = ArgAt(action, 1, "");
                    string dstAttr = IconToAttribute(dstIcon);
                    if (rSubject == null || string.IsNullOrEmpty(dstAttr))
                    {
                        GD.Print($"[EffectEngine] ⚠ ReplaceIcon 跳过：对象为空或目标图标「{dstIcon}」未登记");
                        break;
                    }
                    bool rTimed = durationText != null && durationText.Contains("回合结束");
                    int replacedTotal = 0;
                    foreach (var srcIconRaw in (ArgAt(action, 0, "") ?? "").Split(','))
                    {
                        string srcIcon = srcIconRaw.Trim();
                        if (string.IsNullOrEmpty(srcIcon)) continue;
                        string srcAttr = IconToAttribute(srcIcon);
                        if (string.IsNullOrEmpty(srcAttr)) { GD.Print($"[EffectEngine] ⚠ ReplaceIcon：来源图标「{srcIcon}」未登记 → 跳过"); continue; }
                        int n = srcAttr == "势力" ? rSubject.CurrentPower : rSubject.GetAttribute(srcAttr);
                        if (srcAttr == "先手势力") n = rSubject.GetAttribute("先手势力");
                        if (n <= 0) continue;
                        rSubject.Grant("Attribute", srcAttr, -n);            // 抹掉来源图标
                        rSubject.Grant("Attribute", dstAttr, n);             // 换成目标图标
                        replacedTotal += n;
                        if (rTimed)
                            RegisterTimedMod(new TimedMod
                            {
                                TargetUid = rSubject.UID,
                                Kind = "IconReplace",
                                Value = srcAttr + "|" + dstAttr,             // 来源属性|目标属性
                                Amount = n,
                                ExpireTurn = NowTurn,
                            });
                        GD.Print($"[EffectEngine] ⇄ 图标替换：{rSubject.PlayerTemplate?.CardName} 的 [{srcIcon}]×{n} → [{dstIcon}]" +
                                 (rTimed ? "（直到回合结束）" : ""));
                    }
                    if (replacedTotal == 0)
                        GD.Print($"[EffectEngine] ⚠ ReplaceIcon：{rSubject.PlayerTemplate?.CardName} 上没有可替换的[{ArgAt(action, 0, "")}]图标");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }

                // 【P2·修复】以下四个动词**尚未实现**：原先是"空 case 标签"直接**落到下方 `Attach` 的执行体**，
                // 一旦有卡声明它们就会**静默把来源卡结附到目标身上**（错误行为，且没有任何报错）。
                // 现改为各自独立的"显式未实现"分支：打错误日志、不做任何模型改动（fail-closed）。
                // 若要启用某个动词，请实现其执行体并把该 label 从本组移除（勿再堆叠在别的 case 之上）。
                case ActionVerb.GenerateToken:
                    GD.PrintErr($"[EffectEngine] ⛔ GenerateToken 尚未实现（{string.Join(",", action.Args)}）→ 本次不做任何改动");
                    break;
                case ActionVerb.Investigate:
                    GD.PrintErr($"[EffectEngine] ⛔ Investigate（调查对抗）尚未实现 → 本次不做任何改动");
                    break;
                case ActionVerb.Strength:
                    GD.PrintErr($"[EffectEngine] ⛔ Strength（战斗对抗）尚未实现 → 本次不做任何改动");
                    break;
                case ActionVerb.Power:
                    GD.PrintErr($"[EffectEngine] ⛔ Power（势力对抗）尚未实现 → 本次不做任何改动");
                    break;
                // 【P3·K12】把**来源卡自己**结附到宿主上（JC121 空鸣御守「进场触发：将空鸣御守结附于位于本地区的
                // 目标本方角色」——"附属进场时自行选定结附目标"这条死角）。
                // Args：[宿主（缺省"目标"；也支持"自身"＝把目标结附到来源上）]。
                // 落点与"检索结附"（`AttachSearchedCard`）同款：`IsAttached=true` / `AttachedTo=宿主` /
                // 宿主 `AttachedCards` 收录 / `ZoneManager.MoveToAttachedZone(卡, 宿主所在地区, 控制者)`
                // （宿主是秘社时地区写空串——秘社实例无 `ZoneDetailId`）。
                case ActionVerb.Attach:
                {
                    string hostSide = ArgAt(action, 0, "目标");
                    var attachCard = source;
                    CardInstance attachHost = hostSide == "自身" ? source : target;
                    if (attachCard == null || attachHost == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ Attach 跳过：被结附者/宿主为空（Args[0]={hostSide}）");
                        break;
                    }
                    if (attachCard == attachHost)
                    {
                        GD.Print("[EffectEngine] ⚠ Attach 跳过：不能结附到自身");
                        break;
                    }
                    bool councilHost = attachHost.CurrentZone == CardZone.CouncilZone;
                    attachCard.IsAttached = true;
                    attachCard.AttachedTo = attachHost;
                    attachHost.AttachedCards ??= new System.Collections.Generic.List<CardInstance>();
                    if (!attachHost.AttachedCards.Contains(attachCard)) attachHost.AttachedCards.Add(attachCard);
                    string attachArea = councilHost ? "" : (attachHost.ZoneDetailId ?? "");
                    ZoneManagerRef?.MoveToAttachedZone(attachCard, attachArea, attachCard.ControllerId >= 1 ? attachCard.ControllerId : 1);
                    GD.Print($"[EffectEngine] 结附：{attachCard.PlayerTemplate?.CardName} → 宿主 {attachHost.PlayerTemplate?.CardName}（地区={attachArea}）");
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    break;
                }
                case ActionVerb.Win:
                    GD.Print($"[EffectEngine] {action.Verb} TODO：执行器待实现");
                    break;

                // 准备步骤维护（JC068 阿格斯IV）：支付 N 费用，否则横置来源卡。
                // Args[0] 为所需费用（缺省 1）。经 UIHooks.PromptPayOrTap 发起"支付/横置"二选一弹窗
                //（异步：支付/横置实际动作在弹窗 OnChosen 回调内执行）。
                // 无 UI（测试）时按"能付则付"同步处理，保证单元测试可达。
                case ActionVerb.PrepareUpkeep:
                {
                    if (source == null)
                    {
                        GD.Print("[EffectEngine] PrepareUpkeep source 为空，跳过");
                        break;
                    }
                    int payAmount = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedPay)) ? parsedPay : 1;
                    if (UIHooks != null)
                    {
                        // 异步弹窗：发起选择，支付/横置动作由 UI 回调经 ResolvePrepareUpkeep 落地
                        UIHooks.PromptPayOrTap(source, payAmount);
                    }
                    else
                    {
                        // 无 UI（测试）：能付则付，否则横置（与 UI 分支复用同一业务决策）
                        ResolvePrepareUpkeep(source, payAmount, pay: CanPayPrepareUpkeep(source, payAmount));
                    }
                    break;
                }

                // 【P4】预测X：检视牌库顶 X 张，任意数量置顶/底（Args: [X]）。
                // 复用调查对抗的取/放通道与分配窗口；三条路径（单机窗口 / 联机 Prediction 请求 / 无 UI 兜底）见 `PredictionRule`。
                case ActionVerb.Predict:
                {
                    int predictX = (action.Args != null && action.Args.Count > 0 && int.TryParse(action.Args[0], out var px)) ? px : 1;
                    PredictionRule.Resolve(this, source, predictX);
                    break;
                }

                // 伤害（DealDamage）：造成 N 点伤害。数值取自 action.Args[0]（缺省 1），
                // 由 KeywordEffects.Strike(amount) 等写入，支持"袭击1/袭击2"配置。
                // 实际承伤经 CardManager.DealDamageToInstance 收口（含暗藏者免疫、死亡判定）。
                case ActionVerb.DealDamage:
                    if (target != null)
                    {
                        int dmg = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedDmg)) ? parsedDmg : 1;
                        // 【P3·K13】`Args[1]`（可选）＝ `"不可防止"`：本次伤害跳过"防止"（JC019 镇魔枪授予的能力）
                        bool unpreventable = action.Args.Count > 1 && ArgAt(action, 1, "") == "不可防止";
                        CM?.DealDamageToInstance(target, dmg, isTrauma: false, allowRedirect: true, unpreventable: unpreventable);
                        GD.Print($"[EffectEngine] 造成 {dmg} 点伤害 → {target.PlayerTemplate?.CardName}" +
                                 (unpreventable ? "（不可防止）" : ""));
                    }
                    else GD.Print("[EffectEngine] DealDamage 目标为空，跳过");
                    break;
            }
        }

        // ==================== P2 时效修正栈 ====================

        /// <summary>当前回合号（由 TurnManager 在结算时通过 CurrentTurn 提供，供时效登记）。</summary>
        public int CurrentTurn { get; set; } = 1;

        /// <summary>
        /// 【P2】弃牌：解析"玩家侧"参数（自己 / 对手 / 双方）→ 玩家 id 列表。
        /// 以 source 的**控制者**为基准（夺控场景下以控制者为准，与 D28/D29 一致）。
        /// 弃牌（Discard）与洗牌（Shuffle）共用同一套方位解析。
        /// </summary>
        private System.Collections.Generic.List<int> ResolvePlayerSide(CardInstance source, string who)
        {
            var list = new System.Collections.Generic.List<int>();
            int self = source?.ControllerId ?? -1;
            if (self < 0) return list;
            switch (who)
            {
                // 【K24】"目标玩家"：本次结算锁定的目标玩家（入堆叠时选定）。
                // 未指定（-1）时**不落任何玩家** → 调用方对空列表即"什么都不做"（fail-closed：绝不替玩家乱动别人的牌）。
                case "目标玩家":
                    if (_currentTargetPlayerId > 0) list.Add(_currentTargetPlayerId);
                    break;
                case "对手":
                    list.Add(self == 1 ? 2 : 1);
                    break;
                case "双方":
                    list.Add(self);
                    list.Add(self == 1 ? 2 : 1);
                    break;
                default:                     // "自己"（缺省）
                    list.Add(self);
                    break;
            }
            return list;
        }

        /// <summary>
        /// 【P2】弃牌（一次**动作** = 一批）：优先经 UI（`IGameUIHooks.PromptDiscard`，由 View 选牌并回调），
        /// 无 UI（测试/headless）时**确定性兜底**：弃手牌第一张并打日志。
        /// 张数 &gt; 1 时递归提示剩余张数（每张一次选择），**但先把本批选中的牌收集齐，最后一次性调用
        /// `TurnManager.DiscardCards`——因此 `OnDiscard` 每批**只广播一次**（用户 2026-09 口径，与 `OnDraw` 同口径）。
        /// </summary>
        private void PromptOrFallbackDiscard(int discarderId, int chooserPlayerId, int remaining, int batchSize)
        {
            if (remaining <= 0 || TurnManagerRef == null) return;
            var acc = new System.Collections.Generic.List<CardInstance>();
            CollectDiscardPicks(discarderId, chooserPlayerId, remaining, acc,
                () =>
                {
                    if (acc.Count > 0) TurnManagerRef.DiscardCards(discarderId, acc, batchSize);
                });
        }

        /// <summary>
        /// 逐张收集本批要弃的牌（回调式递归）；全部选完后调用 `onAllPicked` 做**唯一一次**弃牌落地。
        /// 候选里排除"本批已选中但尚未真正弃掉"的牌（此时它们仍在手牌）。
        /// </summary>
        private void CollectDiscardPicks(int discarderId, int chooserPlayerId, int remaining,
            System.Collections.Generic.List<CardInstance> acc, System.Action onAllPicked)
        {
            if (remaining <= 0 || TurnManagerRef == null)
            {
                onAllPicked?.Invoke();
                return;
            }
            var candidates = TurnManagerRef.GetHandOf(discarderId);
            candidates.RemoveAll(c => c == null || acc.Contains(c));
            if (candidates.Count == 0)
            {
                GD.Print($"[EffectEngine] Discard 停止：玩家{discarderId} 手牌为空（或已在本批选中）");
                onAllPicked?.Invoke();
                return;
            }
            if (UIHooks != null)
            {
                UIHooks.PromptDiscard(discarderId, chooserPlayerId, candidates, picked =>
                {
                    if (picked != null) acc.Add(picked);
                    CollectDiscardPicks(discarderId, chooserPlayerId, remaining - 1, acc, onAllPicked);
                });
                return;
            }
            GD.Print($"[EffectEngine] ⚠ Discard 无 UI：确定性兜底弃第一张（{candidates[0].PlayerTemplate?.CardName}）");
            acc.Add(candidates[0]);
            CollectDiscardPicks(discarderId, chooserPlayerId, remaining - 1, acc, onAllPicked);
        }

        /// <summary>
        /// 【P2·联机】经输入网关逐张请求手牌选择（`SelectHandCard`），**收集齐本批后一次性弃掉**
        /// （因此 `OnDiscard` 每批只广播一次，X＝本批总张数；用户 2026-09 口径）。
        /// 请求发给**选择者**（一般=弃牌者本人；JC114 = 施术者），候选 = 弃牌者的手牌（含展示引用，
        /// 因此"由施术者从对手手牌中指定"天然成立——服务端只把候选项发给选择者本人）。
        /// </summary>
        private async System.Threading.Tasks.Task DiscardViaGatewayAsync(int discarderId, int chooserPlayerId,
            int count, int batchSize)
        {
            if (TurnManagerRef == null || InputGateway == null) return;
            var pickedAll = new System.Collections.Generic.List<CardInstance>();
            for (int i = 0; i < count; i++)
            {
                var candidates = TurnManagerRef.GetHandOf(discarderId);
                candidates.RemoveAll(c => c == null || pickedAll.Contains(c));
                if (candidates.Count == 0)
                {
                    GD.Print($"[EffectEngine] Discard 停止：玩家{discarderId} 手牌为空（或已在本批选中）");
                    break;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = chooserPlayerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCard,
                    Prompt = chooserPlayerId == discarderId
                        ? "请选择要弃掉的手牌"
                        : $"请从玩家{discarderId} 的手牌中指定一张弃掉",
                    ForceChoice = true,
                };
                foreach (var c in candidates)
                {
                    req.OptionUids.Add(c.UID);
                    // 展示引用：客户端据此渲染卡面（选择者本人可见，无需额外授权字段）
                    var refr = global::ZoneManager.ProjectCard(c, CardZone.Hand, discarderId);
                    if (refr != null) req.OptionCardRefs.Add(refr);
                    // 【P2·联机可读性】显示名随请求下发（对手手牌对客户端是隐藏占位 → 只能靠服务端给名字）
                    req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
                }
                var result = await InputGateway.RequestChoice(chooserPlayerId, req);
                if (result == null || result.Cancelled || string.IsNullOrEmpty(result.SelectedUid))
                {
                    GD.Print($"[EffectEngine] Discard 中止：选择者未回包（requestId={req.RequestId}）");
                    break;
                }
                var picked = CM?.GetInstance(result.SelectedUid);
                if (picked == null) { GD.Print("[EffectEngine] ⚠ Discard 回调卡不存在，中止"); break; }
                // 【P2·联机权威复检】回包必须命中"服务端本次下发的候选"（＝该弃牌者手牌中、本批尚未选中的牌）。
                // 原先只判"实例存在"，客户端可回包任意 UID（如场上/对手手牌）把它当弃牌结果弃掉。
                if (!candidates.Contains(picked))
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 弃牌回包 {result.SelectedUid} 不在本次合法候选中 → 中止本批（fail-closed）");
                    break;
                }
                pickedAll.Add(picked);
            }
            // 本批一次性弃掉（OnDiscard 只广播一次，ExtraInt = 本批张数）
            if (pickedAll.Count > 0) TurnManagerRef.DiscardCards(discarderId, pickedAll, batchSize);
        }

        // ==================== 【P2】封印（Seal） ====================

        /// <summary>
        /// 【P2·联机】封印"取手牌"经网关请求 `SelectHandCard`：候选 = 控制者（= 选择者）自己的手牌，
        /// 含展示引用（对他自己可见，无需额外授权）。回包后交 `SealAndReport` 落地。
        /// </summary>
        private async System.Threading.Tasks.Task SealFromHandViaGatewayAsync(EffectAction action, int chooserPlayerId,
            CardInstance host, bool faceDown)
        {
            if (TurnManagerRef == null || InputGateway == null) return;
            var hand = TurnManagerRef.GetHandOf(chooserPlayerId);
            if (hand.Count == 0) { GD.Print($"[EffectEngine] Seal 停止：玩家{chooserPlayerId} 手牌为空"); return; }
            // 【秘社批次·MSJC010①】候选按 `Args[3]` 筛选（与 `ResolveSeal` / 发动门禁同一实现，不会分叉）。
            hand = FilterSealHandCandidates(action, hand);
            if (hand.Count == 0) { GD.Print($"[EffectEngine] Seal 停止：玩家{chooserPlayerId} 手牌中没有合格候选"); return; }

            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = chooserPlayerId,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCard,
                SourceUid = host.UID,
                Prompt = $"选择一张手牌封印到 {host.PlayerTemplate?.CardName}",
                ForceChoice = true,
            };
            foreach (var c in hand)
            {
                req.OptionUids.Add(c.UID);
                var refr = global::ZoneManager.ProjectCard(c, CardZone.Hand, chooserPlayerId);
                if (refr != null) req.OptionCardRefs.Add(refr);
                // 【P2·联机可读性】显示名随请求下发（封印自己的手牌；客户端占位卡无模板 → 靠服务端给名字）
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
            }
            var result = await InputGateway.RequestChoice(chooserPlayerId, req);
            if (result == null || result.Cancelled || string.IsNullOrEmpty(result.SelectedUid))
            {
                GD.Print($"[EffectEngine] Seal 中止：选择者未回包（requestId={req.RequestId}）");
                return;
            }
            var picked = CM?.GetInstance(result.SelectedUid);
            if (picked == null) { GD.Print($"[EffectEngine] ⚠ Seal 回包卡不存在：{result.SelectedUid}"); return; }
            // 【P2·联机权威复检】回包必须是"服务端下发的候选之一"（＝封印者此刻的手牌）。
            if (!hand.Contains(picked))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 封印回包 {result.SelectedUid} 不在本次合法候选中 → 拒绝（fail-closed）");
                return;
            }
            SealAndReport(picked, host, faceDown);
        }


        /// <summary>
        /// 【P2】封印落地：解析"被封印者 → 宿主"两个方位，交给唯一出口 `TurnManager.SealCard`
        /// （建立双向引用 + 进 SealedZone + 广播 OnSealed）。
        ///
        /// Args 约定（见 `EffectAction` 注释）：
        ///  · `Args[0]` 被封印者来源：`"自身"`（缺省，= source）/ `"目标"` / `"牌库顶"` / `"手牌"`（由控制者选一张）；
        ///  · `Args[1]` 宿主：`"目标"`（缺省）/ `"自身"`；
        ///  · `Args[2]` 附加：含 `"背面朝上"` → 信息隔离（JC119，对手不能检视）。
        ///
        /// 为什么"被封印者取手牌"要询问：JC119 是"你可以从手牌中封印一张"——具体封哪张由玩家定；
        /// 有 UI → `IGameUIHooks.PromptPickCard`（异步回调续算）；无 UI（测试/headless）→ 确定性兜底取第一张。
        /// 联机：走 `ExecuteVerbAsync` 的 `SelectHandCard` 网关分支（见 `SealFromHandViaGatewayAsync`），不走本方法。
        /// </summary>
        private void ResolveSeal(EffectAction action, CardInstance source, CardInstance target)
        {
            if (TurnManagerRef == null) { GD.Print("[EffectEngine] ⚠ Seal 跳过：TurnManagerRef 未注入"); return; }

            string subjectSide = ArgAt(action, 0, "自身");
            string hostSide = ArgAt(action, 1, "目标");
            bool faceDown = HasArg(action, "背面朝上");

            CardInstance host = hostSide == "自身" ? source : target;
            if (host == null)
            {
                GD.Print($"[EffectEngine] ⚠ Seal 跳过：宿主为空（Args[1]={hostSide}，且无 target）");
                return;
            }

            switch (subjectSide)
            {
                case "目标":
                    SealAndReport(target, host, faceDown);
                    break;

                // 【K3·死亡类·A10】把**本次触发的上下文卡**（＝刚死亡的角色）封印到宿主上：
                // JC105"当另一个本方灵体角色死亡时…将该角色封印在目标角色上，而非置入墓地"——
                // 走触发路线（用户 A10）：角色**已进墓地**，此处由 `SealCard` → `MoveToSealedZone`
                // （内部 `RemoveFromAllZones`）把它从墓地取出并置入宿主的封印区。
                case "触发卡":
                case "死亡角色":
                case "死亡的角色":
                {
                    var ctx = ResolveTriggerContextSubject(subjectSide);
                    if (ctx == null)
                    {
                        GD.Print($"[EffectEngine] ⚠ Seal 跳过：`{subjectSide}` 为空（非触发路径或触发无上下文卡）");
                        break;
                    }
                    SealAndReport(ctx, host, faceDown);
                    break;
                }

                case "牌库顶":
                {
                    int owner = source?.ControllerId ?? -1;
                    var top = TurnManagerRef.PeekDeckOf(owner, 1);
                    if (top.Count == 0) { GD.Print($"[EffectEngine] Seal 跳过：玩家{owner} 牌库为空"); break; }
                    var card = top[0];
                    // 牌库列表与区域桶是两套结构：必须先从牌库移除，否则该卡会同时"在牌库里"与"在封印区"。
                    TurnManagerRef.RemoveCardFromDeckOf(owner, card);
                    SealAndReport(card, host, faceDown);
                    break;
                }

                case "手牌":
                {
                    int chooser = source?.ControllerId ?? -1;
                    var hand = TurnManagerRef.GetHandOf(chooser);
                    if (hand.Count == 0) { GD.Print($"[EffectEngine] Seal 跳过：玩家{chooser} 手牌为空"); break; }
                    // 【秘社批次·MSJC010①】`Args[3]` = 手牌筛选（`DeckSearchFilter` 语法，空＝不限）：
                    // 卡文"从你的手中选择一张**【星】领域的牌**"——候选必须是符合条件的牌，
                    // 不能把整手牌都给玩家选（否则可以封印一张非【星】领域的牌）。
                    var eligible = new System.Collections.Generic.List<CardInstance>();
                    foreach (var c in hand) eligible.Add(c);
                    eligible = FilterSealHandCandidates(action, eligible);
                    if (eligible.Count == 0)
                    {
                        GD.Print($"[EffectEngine] Seal 跳过：玩家{chooser} 手牌中没有符合筛选「{ArgAt(action, 3, "")}」的牌");
                        MarkActionIneffective("Seal", "手牌无合格候选");
                        break;
                    }
                    if (UIHooks != null)
                    {
                        UIHooks.PromptPickCard(chooser, $"选择一张手牌封印（{ArgAt(action, 3, "任意")}）", eligible,
                            allowCancel: false, picked => SealAndReport(picked, host, faceDown));
                        break;
                    }
                    GD.Print($"[EffectEngine] ⚠ Seal 无 UI：确定性兜底封印手牌第一张合格牌（{eligible[0].PlayerTemplate?.CardName}）");
                    SealAndReport(eligible[0], host, faceDown);
                    break;
                }

                default:   // "自身"
                    SealAndReport(source, host, faceDown);
                    break;
            }
        }

        /// <summary>
        /// 【秘社批次】`Seal{手牌}` 的候选筛选：`Args[3]` 非空时按 `DeckSearchFilter` 语法过滤手牌
        /// （MSJC010①"从你的手中选择一张【星】领域的牌"＝ `领域:星空`）。空/未给 ⇒ 原样返回（不限）。
        /// 供 `ResolveSeal`、`SealFromHandViaGatewayAsync` 与 `ActivateEligibility` 的发动门禁**共用**，
        /// 保证"UI 能选的"与"可发动的"永远一致。
        /// </summary>
        private static System.Collections.Generic.List<CardInstance> FilterSealHandCandidates(
            EffectAction action, System.Collections.Generic.List<CardInstance> hand)
        {
            string filter = ArgAt(action, 3, "");
            if (string.IsNullOrWhiteSpace(filter)) return hand;
            var result = new System.Collections.Generic.List<CardInstance>();
            foreach (var c in hand)
                if (DeckSearchFilter.Matches(filter, c)) result.Add(c);
            return result;
        }

        /// <summary>
        /// 【秘社批次】该"封印手牌"动作此刻是否有合格候选（`ActivateEligibility` 的发动门禁用）。
        /// 与 `ResolveSeal` 的候选筛选**同一实现**，不会出现"能点但点了没反应"。
        /// </summary>
        public static bool HasSealHandCandidate(EffectData data, CardInstance source, TurnManager tm)
        {
            if (data?.Actions == null || source == null || tm == null) return true;
            foreach (var a in data.Actions)
            {
                if (a?.Verb != ActionVerb.Seal || ArgAt(a, 0, "自身") != "手牌") continue;
                var hand = tm.GetHandOf(source.ControllerId);
                if (hand == null || hand.Count == 0) return false;
                if (FilterSealHandCandidates(a, hand).Count == 0) return false;
            }
            return true;
        }

        /// <summary>执行封印并统一打日志（被封印者 == 宿主 时拒绝，避免自封死循环/无意义状态）。</summary>
        private void SealAndReport(CardInstance sealing, CardInstance host, bool faceDown)
        {
            if (sealing == null || host == null)
            { MarkActionIneffective("Seal", "被封印者或宿主为空"); return; }
            if (sealing == host)
            { MarkActionIneffective("Seal", $"{sealing.PlayerTemplate?.CardName} 不能封印在自己身上"); return; }
            bool ok = TurnManagerRef?.SealCard(sealing, host, faceDown) ?? false;
            GD.Print($"[EffectEngine] 封印{(ok ? "成功" : "失败")}：{sealing.PlayerTemplate?.CardName} → {host.PlayerTemplate?.CardName}（背面={faceDown}）");
            // 【P3·动作成果】封印失败 → 后续"然后…"动作整段跳过（JC009 FAQ：未能成功封印则遏制不结算）。
            if (!ok) MarkActionIneffective("Seal", $"封印 {sealing.PlayerTemplate?.CardName} 未落地");
        }

        /// <summary>
        /// 【P3·"若你如此做"动作成果】某动作**未真正生效** → 置链式中止标志：同一动作序列的**后续动作整段跳过**。
        /// 与既有"支付失败即中止"（`Pay`/`PayOptional`）共用同一个 `_actionChainAborted` 标志（同一语义：
        /// 前置动作没做成，"若你如此做"的收益就不结算）。覆盖：
        ///  · JC009「将目标角色封印…**然后**遏制1」——FAQ：未能成功封印则遏制无法结算；
        ///  · JC087「令本地区目标本方角色潜伏，**若你如此做**，在该地区放置势力标志」。
        /// 注：只在**同步**动作路径上能真正阻断后续动作；若该动作自身是"经 UI 回调完成"的
        /// （如 `Seal{手牌}` 等玩家选牌），回调发生时链后续动作可能已执行——此时该标志只作记录。
        /// </summary>
        private void MarkActionIneffective(string verb, string reason)
        {
            GD.Print($"[EffectEngine] ⏹ {verb} 未生效（{reason}）→ 后续动作整段跳过（『若你如此做』不成立）");
            _actionChainAborted = true;
        }

        /// <summary>取动作参数（越界/空串回落到默认值）。</summary>
        private static string ArgAt(EffectAction action, int index, string fallback)            => (action?.Args != null && action.Args.Count > index && !string.IsNullOrWhiteSpace(action.Args[index]))
                ? action.Args[index].Trim()
                : fallback;

        /// <summary>【P3·K16 触发版】把动作的"主体"写法解析成宿主卡：`"结附宿主"` ＝ `AttachedTo`（结附），
        /// `"封印宿主"` ＝ `SealedBy`（封印，`TurnManager.SealCard` 写入）。其它写法（含缺省 `"目标"`）→ null
        /// （调用方回落到既有的 `target` 口径）。卡文"该角色"＝本卡所在宿主用这两种写法表达（JC110）。</summary>
        private static CardInstance ResolveHostSubject(string subject, CardInstance source)
        {
            if (source == null) return null;
            switch (subject)
            {
                case "结附宿主": return source.AttachedTo;
                case "封印宿主": return source.SealedBy;
                default: return null;
            }
        }

        /// <summary>某参数是否出现在 Args 任一项中（用于"背面朝上"这类附加标记）。</summary>
        private static bool HasArg(EffectAction action, string needle)
        {
            if (action?.Args == null) return false;
            foreach (var a in action.Args)
                if (!string.IsNullOrEmpty(a) && a.Contains(needle, System.StringComparison.Ordinal)) return true;
            return false;
        }

        // ==================== 【P2·联机】JC119 展示封印牌 → 免费打出一张 ====================

        /// <summary>
        /// 【P2·联机断链修复·JC119】守秘人②：**经网关**执行"展示封印牌 → 由控制者选一张免费打出"。
        /// 与单机分支（`ExecuteVerb` 的同名 case）**同一口径**：
        ///  · 候选＝控制者手牌中"**刚由本卡封印回手**"（`ReturnedFromSealHostUid == source.UID`）且尚未登记过的牌；
        ///  · **先公开展示全部**（卡文"展示封印在守秘人上的所有牌"）；
        ///  · 下发 `SelectHandCard`（`ForceChoice=false` ⇒ 允许放弃＝一张都不打）；
        ///  · 回包**权威复检**（必须命中候选）→ **忠诚预检**（卡文"但仍需满足忠诚需求"）→
        ///    `TurnManager.EnqueueFreePlay`（真正的打出在原子锁解除后由 `ProcessQueuedFreePlays` 走正常打出流程）；
        ///  · 取消/超时/不满足忠诚 → **不打**（牌留在手牌，"移回拥有者手上"天然成立）。
        /// 原先只有单机 `UIHooks` 分支 ⇒ 联机落到"兜底＝不打出"，该卡在联机侧**永远不生效**。
        /// </summary>
        private async System.Threading.Tasks.Task RevealSealedAndQueueFreePlayViaGatewayAsync(CardInstance source)
        {
            if (source == null || CM == null || TurnManagerRef == null || InputGateway == null) return;
            int owner = source.ControllerId;
            var candidates = CollectSealedReturnedCards(source, owner);
            if (candidates.Count == 0)
            {
                GD.Print($"[EffectEngine] RevealSealedAndQueueFreePlay（联机）：{source.PlayerTemplate?.CardName} 没有『刚由封印回手』的牌 → 无事发生");
                return;
            }
            foreach (var c in candidates) RevealCardPublicly(c, owner);   // ① 先公开展示全部

            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = owner,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCard,
                SourceUid = source.UID,
                Prompt = $"{source.PlayerTemplate?.CardName}：可免费打出其中一张（视作快速；不需付费，仍需忠诚）",
                ForceChoice = false,   // 卡文"你可以…"：允许放弃（一张都不打）
            };
            foreach (var c in candidates)
            {
                req.OptionUids.Add(c.UID);
                var refr = global::ZoneManager.ProjectCard(c, CardZone.Hand, owner);
                if (refr != null) req.OptionCardRefs.Add(refr);
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
            }

            var result = await InputGateway.RequestChoice(owner, req);
            if (result == null || result.Cancelled || string.IsNullOrEmpty(result.SelectedUid))
            {
                GD.Print($"[EffectEngine] 玩家{owner} 选择不打出封印牌 → 全部留在手牌");
                return;
            }
            var picked = CM.GetInstance(result.SelectedUid);
            if (picked == null || !candidates.Contains(picked))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 免费打出回包 {result.SelectedUid} 不在候选内 → 拒绝（fail-closed）");
                return;
            }
            var pm = TurnManagerRef.PlayerManagerRef;
            if (picked.PlayerTemplate == null || pm == null || !pm.CanPayLoyalty(owner, picked.PlayerTemplate))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 免费打出被拒：{picked.PlayerTemplate?.CardName} 不满足忠诚需求 → 留在手牌");
                return;
            }
            TurnManagerRef.EnqueueFreePlay(owner, picked.UID);
        }

        /// <summary>【JC119·共用】"刚由 <paramref name="source"/> 封印回手、且尚未登记免费打出"的手牌集合
        /// （单机/联机两条路径共用，避免口径分叉）。</summary>
        private System.Collections.Generic.List<CardInstance> CollectSealedReturnedCards(CardInstance source, int owner)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            if (source == null || ZoneManagerRef == null || TurnManagerRef == null) return result;
            foreach (var c in ZoneManagerRef.GetZoneCards(CardZone.Hand, owner))
            {
                if (c == null || c.IsDying) continue;
                if (!string.Equals(c.ReturnedFromSealHostUid ?? "", source.UID, System.StringComparison.Ordinal)) continue;
                if (TurnManagerRef.HasQueuedFreePlay(c.UID)) continue;   // 已登记过（防重复）
                result.Add(c);
            }
            return result;
        }

        // ==================== 【P2】检索（Search） ====================

        /// <summary>
        /// 【P2·联机】检索取牌经网关请求 `SelectDeckCard`：候选 = 通过筛选的牌（含展示引用，
        /// 因为牌库对双方都不可见，客户端必须靠 `OptionCardRefs` 才能渲染候选卡面）。
        ///
        /// **洗牌语义（用户拍板 2026-09）**：
        ///  · 取到牌 → 洗牌；
        ///  · **没找到**（确实执行了检索，但无匹配）→ 洗牌（卡文"然后洗牌"对"没找到"同样成立）。
        ///
        /// **【A53·2026-09 用户拍板】检索一律强制，不可取消**（8 张 JC 检索卡卡文均无"你可以寻找"）：
        /// `ForceChoice = true` → 客户端不提供"取消"；若回包仍是取消/非法（异常客户端），则**不能靠取消规避
        /// 强制检索** → 按"无有效回包"的确定性兜底口径取**第一张匹配**（fail-safe），随后照常落地 + 洗牌。
        /// （原"玩家放弃 = 检索未发生 → 不洗牌"的口径随 A53 作废。）
        ///
        /// 去向『结附』且无显式目标时（JC051），还要再问一次"结附到哪个宿主"——候选 ≥ 2 时经网关 `SelectTarget`，
        /// 见 `AttachSearchedCardByOwnRulesViaGatewayAsync`。
        /// </summary>
        /// <summary>
        /// 【秘社批次·联机】`UnsealToArea` 经网关请求 `SelectZoneCard`：候选＝服务端用**同一实现**
        /// （`CollectUnsealCandidates`）算出的"封印区/墓地中符合条件的牌"。
        /// 回包必须命中本次下发的候选（fail-closed 复检）；超时/非法/未回包 → **确定性兜底取首候选**
        /// （与单机无 UI 口径一致，避免整条动作链卡死导致堆叠不结算）。
        /// </summary>
        private async System.Threading.Tasks.Task UnsealToAreaViaGatewayAsync(EffectAction action, CardInstance source,
            string targetAreaId)
        {
            if (source == null || InputGateway == null) return;
            string areaUid = targetAreaId ?? "";
            if (string.IsNullOrEmpty(areaUid))
            { GD.Print("[EffectEngine] ⚠ UnsealToArea（联机）跳过：推不出目标地区"); return; }

            var candidates = CollectUnsealCandidates(action, source);
            if (candidates.Count == 0)
            { GD.Print("[EffectEngine] ⚠ UnsealToArea（联机）：无合法候选 → 本动作不生效"); return; }

            int chooser = source.ControllerId;
            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = chooser,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectZoneCard,
                SourceUid = source.UID,
                Prompt = "选择一张放置进场的牌",
                ForceChoice = true,          // 强制：卡文是"令一张…进场"，没有"可以不放"
            };
            foreach (var c in candidates)
            {
                req.OptionUids.Add(c.UID);
                var refr = global::ZoneManager.ProjectCard(c, c.CurrentZone, chooser);
                if (refr != null) req.OptionCardRefs.Add(refr);
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
            }

            var result = await InputGateway.RequestChoice(chooser, req);
            CardInstance picked = null;
            if (result != null && !result.Cancelled && !string.IsNullOrEmpty(result.SelectedUid))
            {
                picked = candidates.Find(c => string.Equals(c.UID, result.SelectedUid, System.StringComparison.Ordinal));
                if (picked == null)
                    GD.PrintErr($"[EffectEngine] ⛔ UnsealToArea 回包 {result.SelectedUid} 不在本次合法候选中 → 拒绝（fail-closed）");
            }
            picked ??= candidates[0];   // 超时/取消/非法 → 确定性兜底（与单机无 UI 同款）
            LandUnsealToArea(action, source, picked, areaUid);
        }

        /// <summary>
        /// 【秘社批次·联机】`PlaceHandCardToArea` 经网关请求 `SelectHandCard`：候选＝该玩家手牌中符合筛选的牌。
        /// 回包必须命中本次下发的候选（fail-closed）；超时/非法 → **确定性兜底取首候选**（与单机无 UI 同款）。
        /// </summary>
        private async System.Threading.Tasks.Task PlaceHandCardToAreaViaGatewayAsync(EffectAction action,
            CardInstance source, string targetAreaId)
        {
            if (source == null || InputGateway == null || TurnManagerRef == null) return;
            string areaUid = ArgAt(action, 2, "目标地区") == "目标地区" ? (targetAreaId ?? "") : "";
            if (string.IsNullOrEmpty(areaUid))
            { GD.Print("[EffectEngine] ⚠ PlaceHandCardToArea（联机）跳过：推不出目标地区"); return; }

            int chooser = source.ControllerId;
            var hand = TurnManagerRef.GetHandOf(chooser) ?? new System.Collections.Generic.List<CardInstance>();
            var candidates = FilterHandByArg(hand, ArgAt(action, 0, ""));
            if (candidates.Count == 0)
            { GD.Print("[EffectEngine] ⚠ PlaceHandCardToArea（联机）：手牌无合格候选 → 本动作不生效"); return; }

            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = chooser,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCard,
                SourceUid = source.UID,
                Prompt = "从手中选择一张牌（展示后置于目标地区）",
                ForceChoice = true,
            };
            foreach (var c in candidates)
            {
                req.OptionUids.Add(c.UID);
                var refr = global::ZoneManager.ProjectCard(c, CardZone.Hand, chooser);
                if (refr != null) req.OptionCardRefs.Add(refr);
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
            }

            var result = await InputGateway.RequestChoice(chooser, req);
            CardInstance picked = null;
            if (result != null && !result.Cancelled && !string.IsNullOrEmpty(result.SelectedUid))
            {
                picked = candidates.Find(c => string.Equals(c.UID, result.SelectedUid, System.StringComparison.Ordinal));
                if (picked == null)
                    GD.PrintErr($"[EffectEngine] ⛔ PlaceHandCardToArea 回包 {result.SelectedUid} 不在本次合法候选中 → 拒绝（fail-closed）");
            }
            picked ??= candidates[0];
            LandPlaceHandCardToArea(action, source, picked, areaUid);
        }

        private async System.Threading.Tasks.Task SearchViaGatewayAsync(EffectAction action, CardInstance source,
            CardInstance target, string targetAreaId)
        {            if (TurnManagerRef == null || InputGateway == null) return;
            int searcher = source?.ControllerId ?? -1;
            if (searcher < 0) { GD.Print("[EffectEngine] ⚠ Search 跳过：来源控制者无效"); return; }

            string dest = ArgAt(action, 1, "手牌");
            var matches = CollectSearchMatches(action, searcher);
            if (matches.Count == 0)
            {
                GD.Print("[EffectEngine] 🔍 检索：未找到符合条件的牌 → 仍然洗牌");
                TurnManagerRef.ShuffleDeckOf(searcher);
                return;
            }

            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = searcher,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectDeckCard,
                SourceUid = source?.UID,
                Prompt = $"从牌库中寻找：{DescribeSearchFilter(ArgAt(action, 0, ""))}",
                ForceChoice = true,            // 【A53】强制检索：客户端不提供"取消"
            };
            foreach (var c in matches)
            {
                req.OptionUids.Add(c.UID);
                var refr = global::ZoneManager.ProjectCard(c, CardZone.Deck, searcher);
                if (refr != null) req.OptionCardRefs.Add(refr);
                // 【P2·联机可读性】候选显示名随请求下发：牌库对客户端是**隐藏占位**（无模板/无卡面），
                // 客户端只能拿到原始 UID ⇒ 玩家"看不到卡名却必须选"。显示名由服务端给出（单一真相源）。
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
            }

            var result = await InputGateway.RequestChoice(searcher, req);
            CardInstance picked = null;
            if (result != null && !result.Cancelled && !string.IsNullOrEmpty(result.SelectedUid))
                picked = CM?.GetInstance(result.SelectedUid);
            // 【P2·联机权威复检】回包必须命中"**服务端本次下发的候选**"（＝在检索者牌库中 ∧ 通过筛选 ∧ 未越界）。
            // 原先只判"实例存在"，客户端可回包任意 UID（如对手手牌/墓地里的牌）把它当检索结果取走；
            // 与 K10/K15/遏制/预测的复检同一口径。**A53**：强制检索下"取消/非法"不再等于"不取牌"，
            // 而是 fail-safe 兜底取第一张匹配（否则异常客户端可以规避强制检索）。
            if (picked == null || !matches.Contains(picked))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 检索回包『{(result?.Cancelled == true ? "取消" : result?.SelectedUid)}』" +
                            "不是本次合法候选 → 按强制检索确定性兜底取第一张匹配（fail-safe）");
                picked = matches[0];
            }
            TurnManagerRef.RemoveCardFromDeckOf(picked.OwnerId, picked);
            string extra = ArgAt(action, 2, "");
            if (dest == "结附" && extra != "地区" && target == null)
                await AttachSearchedCardByOwnRulesViaGatewayAsync(picked, source, targetAreaId, searcher);
            else
                ApplySearchDestination(picked, dest, extra, source, target, targetAreaId);
            // 卡文"展示该牌后置于你的手中"：`附加` 含"展示"时公开展示（JC007/JC025；联机路径）
            if (HasArg(action, "展示")) RevealCardPublicly(picked, searcher);
            TurnManagerRef.ShuffleDeckOf(searcher);   // 取到牌 → 洗牌
        }

        /// <summary>
        /// 【P2】检索落地（单机/无网关路径）：按"范围"取候选池 → 筛出匹配 → 询问玩家取一张
        /// → 按去向放置（JC051 的『结附』去向还会再问一次"结附到哪个宿主"）→ 洗牌。
        /// 洗牌口径见 `SearchViaGatewayAsync` 的说明（**取到牌 / 没找到 → 洗牌**）。
        ///
        /// 三条路径与弃牌完全对称：
        ///  · 有 UI（`IGameUIHooks`）→ 弹窗选牌，回调内续算（View 不写 Model）；
        ///  · 无 UI（测试/headless）→ 确定性兜底取第一张匹配；
        ///  · 联机 → `ExecuteVerbAsync` 在进入本方法前已改走 `SearchViaGatewayAsync`。
        ///
        /// 【A53·2026-09 用户拍板】检索**一律强制、不可取消**（8 张 JC 检索卡的卡文都没有"你可以寻找"）：
        /// `allowCancel: false`。原先"玩家可以放弃取牌"的口径作废——联机侧同步改为强制
        /// （见 `SearchViaGatewayAsync` 的 `ForceChoice = true` 与回包兜底）。
        /// </summary>
        private void ResolveSearch(EffectAction action, CardInstance source, CardInstance target, string targetAreaId)
        {
            if (TurnManagerRef == null) { GD.Print("[EffectEngine] ⚠ Search 跳过：TurnManagerRef 未注入"); return; }
            int searcher = source?.ControllerId ?? -1;
            if (searcher < 0) { GD.Print("[EffectEngine] ⚠ Search 跳过：来源控制者无效"); return; }

            string filter = ArgAt(action, 0, "");
            string dest = ArgAt(action, 1, "手牌");
            string extra = ArgAt(action, 2, "");

            var matches = CollectSearchMatches(action, searcher);
            if (matches.Count == 0)
            {
                // 检索确实执行了，只是没有匹配 → 按卡文"然后洗牌"洗牌
                GD.Print("[EffectEngine] 🔍 检索：未找到符合条件的牌 → 仍然洗牌");
                TurnManagerRef.ShuffleDeckOf(searcher);
                return;
            }

            System.Action<CardInstance> takeAndShuffle = picked =>
            {
                if (picked == null)
                {
                    // 强制检索（allowCancel=false）下不应出现；防御性兜底＝按"没找到"处理（卡文"然后洗牌"仍成立）
                    GD.PrintErr("[EffectEngine] ⚠ 检索回调收到 null（强制检索本不应发生）→ 按『没找到』处理：洗牌");
                    TurnManagerRef.ShuffleDeckOf(searcher);
                    return;
                }
                TurnManagerRef.RemoveCardFromDeckOf(picked.OwnerId, picked);
                // 去向**落地完成之后**才揭示 + 洗牌：JC051 的『结附』去向可能还要弹一次"选宿主"窗，
                // 此时本回调继续挂在那个窗的 onPicked 上（`ApplySearchDestination` 的 onDone 续算）。
                System.Action finish = () =>
                {
                    // 卡文"展示该牌后置于你的手中"：`附加` 含"展示"时公开展示（JC007/JC025）
                    if (HasArg(action, "展示")) RevealCardPublicly(picked, searcher);
                    TurnManagerRef.ShuffleDeckOf(searcher);
                };
                ApplySearchDestination(picked, dest, extra, source, target, targetAreaId, finish);
            };

            if (UIHooks != null)
            {
                // 【A53】强制检索：`allowCancel: false`（窗口不给"取消"，必须选一张）
                UIHooks.PromptPickCard(searcher, $"从牌库中寻找：{DescribeSearchFilter(filter)}", matches,
                    allowCancel: false, takeAndShuffle);
                return;
            }
            GD.Print($"[EffectEngine] ⚠ Search 无 UI：确定性兜底取第一张匹配（{matches[0].PlayerTemplate?.CardName}）");
            takeAndShuffle(matches[0]);
        }

        /// <summary>按 `Args[3]` 范围（`牌库` / `牌库顶:N`）与 `Args[0]` 筛选，收集本次检索的候选池。</summary>
        private System.Collections.Generic.List<CardInstance> CollectSearchMatches(EffectAction action, int searcher)
        {
            string filter = ArgAt(action, 0, "");
            string scope = ArgAt(action, 3, "牌库");

            System.Collections.Generic.List<CardInstance> pool;
            if (scope.StartsWith("牌库顶:", System.StringComparison.Ordinal)
                && int.TryParse(scope.Substring("牌库顶:".Length).Trim(), out int n) && n > 0)
            {
                pool = TurnManagerRef.PeekDeckOf(searcher, n);   // 只看顶上 N 张（JC032：牌库顶 6 张）
            }
            else
            {
                pool = TurnManagerRef.GetDeckOf(searcher);       // 整库
            }

            var matches = new System.Collections.Generic.List<CardInstance>();
            foreach (var c in pool)
                if (DeckSearchFilter.Matches(filter, c)) matches.Add(c);
            GD.Print($"[EffectEngine] 🔍 检索：玩家{searcher} 范围={scope} 筛选='{filter}' → 候选 {matches.Count}/{pool.Count}");
            return matches;
        }

        /// <summary>
        /// 把检索到的牌按"去向"放置。去向取自 `Args[1]`：
        ///  · `手牌`（缺省）：加入拥有者手牌（**注意：检索不是"抓牌"**，故不广播 OnDraw，见文档「检索≠抓牌」）；
        ///  · `进场`：置于来源所在地区（`source.ZoneDetailId ?? targetAreaId`）；
        ///  · `暗藏进场`：同"进场"，但以暗藏者状态（背面）进入——先 SetCovert(true) 再移动，与"派遣暗藏者"同序；
        ///  · `结附`：结附到 `target`（`Args[2]="地区"` 则结附到本地区，不绑定宿主卡）；
        ///    既无显式 target 又非"地区"时 → 宿主由**玩家**从"被检索卡自身 TargetRules 的合法宿主"中选
        ///    （见 `AttachSearchedCardByOwnRules`，≥2 个候选才弹窗）；
        ///  · `封印`：封到宿主（`Args[2]="自身"` → source，缺省 `目标` → target）。
        /// 去向无法成立时（如"进场"但推不出地区）回落到"加入手牌"，保证牌不会卡在牌库外无归属状态。
        ///
        /// <paramref name="onDone"/>：整个去向**落地完成**后的续算回调（调用方在其中揭示 / 洗牌）。
        /// 『结附·选宿主』会挂起等待玩家选择，因此该续算可能**不是**同步发生的——调用方不得假设
        /// `ApplySearchDestination` 返回时去向已落地。
        /// </summary>
        private void ApplySearchDestination(CardInstance card, string dest, string extra,
            CardInstance source, CardInstance target, string targetAreaId, System.Action onDone = null)
        {
            if (card == null) { onDone?.Invoke(); return; }
            switch (dest)
            {
                case "进场":
                case "暗藏进场":
                {
                    string areaUid = ResolveSearchArea(source, targetAreaId);
                    if (string.IsNullOrEmpty(areaUid))
                    {
                        GD.Print("[EffectEngine] ⚠ 检索去向『进场』推不出地区 → 回落加入手牌");
                        CM?.MoveCardToHand(card);
                        break;
                    }
                    if (dest == "暗藏进场") card.SetCovert(true);   // 先翻面再进场（与 TurnManager 派遣暗藏者同序）
                    CM?.MoveCardToArea(card, areaUid);
                    GD.Print($"[EffectEngine] 检索：{card.PlayerTemplate?.CardName} {(dest == "暗藏进场" ? "暗藏" : "正面")}进场到地区 {areaUid}");
                    break;
                }

                case "结附":
                {
                    // 宿主解析（**A53·2026-09 用户拍板修订**，详见 `EffectAction` 的 `Search` 注释）：
                    //  · `附加="地区"` → 显式结附「来源所在地区」（原语义保留）；
                    //  · 本次结算**已有显式 target**（效果声明了目标且玩家已选）→ 结附该 target（原语义保留）；
                    //  · 否则 → 候选＝被检索卡自身 TargetRules 的**合法宿主**，由**玩家**从中选一个（JC051 口径）；
                    //    无合法宿主 → 按"无合法目标"处理（不结附；牌放回拥有者牌库，随后照常洗牌）。
                    if (extra == "地区")
                    {
                        AttachSearchedCard(card, null, source, targetAreaId);
                        break;
                    }
                    if (target != null)
                    {
                        AttachSearchedCard(card, target, source, targetAreaId);
                        break;
                    }
                    AttachSearchedCardByOwnRules(card, source, targetAreaId, onDone);
                    return;   // 续算由 onPicked 回调触发
                }

                case "封印":
                {
                    var host = extra == "自身" ? source : target;
                    if (host == null) { GD.Print("[EffectEngine] ⚠ 检索去向『封印』宿主为空 → 回落加入手牌"); CM?.MoveCardToHand(card); break; }
                    SealAndReport(card, host, extra.Contains("背面朝上", System.StringComparison.Ordinal));
                    break;
                }

                default:   // 手牌
                    CM?.MoveCardToHand(card);
                    GD.Print($"[EffectEngine] 检索：{card.PlayerTemplate?.CardName} 加入玩家{card.OwnerId} 手牌（检索不是抓牌，不广播 OnDraw）");
                    break;
            }
            onDone?.Invoke();
        }

        /// <summary>检索"进场/结附地区"落点：优先来源所在地区，其次本次结算的目标地区。</summary>
        private static string ResolveSearchArea(CardInstance source, string targetAreaId)
        {
            // 【P3·K15】本次结算**显式给定的目标地区优先**（JC033①"以暗藏者状态置于**目标地区**"）；
            // 未给定时回退"来源所在地区"（既有语义不变）。
            if (!string.IsNullOrEmpty(targetAreaId)) return targetAreaId;
            return source?.ZoneDetailId ?? "";
        }

        /// <summary>
        /// 【P2】把检索到的附属卡结附到场上的 `host`（`host == null` 时结附到本地区）。
        /// 与 `TurnManager.ResolveEntry` 的附属结算同序（双向引用 → MoveToAttachedZone → 注册持续效果），
        /// 差别只在于：目标由本效果给定（JC051 的"结附到合理目标"= 施术者选，命中其自身目标规则），
        /// 而非由堆叠条目的打出目标给定。地区落点由 `ResolveSearchArea` 推出。
        /// </summary>
        private void AttachSearchedCard(CardInstance card, CardInstance host, CardInstance source, string targetAreaId)
        {
            if (card == null) return;

            if (host != null && host.CurrentZone == CardZone.CouncilZone)
            {
                // 结附秘社：秘社实例无 ZoneDetailId，不能走 MoveToArea（会落入空地区 UID 而脱离管理）
                card.IsAttached = true;
                card.AttachedTo = host;
                host.AttachedCards.Add(card);
                ZoneManagerRef?.MoveToAttachedZone(card, "", card.ControllerId >= 1 ? card.ControllerId : 1);
                GD.Print($"[EffectEngine] 检索：{card.PlayerTemplate?.CardName} 结附于秘社 {host.UID}");
                return;
            }

            if (host != null)
            {
                if (!IsOnField(host)) { GD.Print($"[EffectEngine] ⚠ 检索去向『结附』目标不在场上 → 回落加入手牌"); CM?.MoveCardToHand(card); return; }
                card.IsAttached = true;
                card.AttachedTo = host;
                host.AttachedCards.Add(card);
                ZoneManagerRef?.MoveToAttachedZone(card, host.ZoneDetailId ?? "",
                    card.ControllerId >= 1 ? card.ControllerId : 1);
                GD.Print($"[EffectEngine] 检索：{card.PlayerTemplate?.CardName} 结附于 {host.PlayerTemplate?.CardName}（地区 {host.ZoneDetailId}）");
                RegisterContinuous(card);
                return;
            }

            // 结附地区：无宿主（AttachedTo=null），落点为本地区
            string areaUid = ResolveSearchArea(source, targetAreaId);
            if (string.IsNullOrEmpty(areaUid)) { GD.Print("[EffectEngine] ⚠ 检索去向『结附（地区）』推不出地区 → 回落加入手牌"); CM?.MoveCardToHand(card); return; }
            card.IsAttached = true;
            card.AttachedTo = null;
            card.ZoneDetailId = areaUid;
            ZoneManagerRef?.MoveToAttachedZone(card, areaUid, card.ControllerId >= 1 ? card.ControllerId : 1);
            GD.Print($"[EffectEngine] 检索：{card.PlayerTemplate?.CardName} 结附于地区 {areaUid}");
            RegisterContinuous(card);
        }

        /// <summary>把筛选串转成给玩家看的提示文案（空串 = 任意牌）。</summary>
        private static string DescribeSearchFilter(string filter)
            => string.IsNullOrWhiteSpace(filter) ? "任意牌" : filter;

        /// <summary>
        /// 【P4·JC051／A53】检索去向『结附』的**宿主选择**：宿主候选由**被检索卡自身的 TargetRules** 决定
        /// ——"检索到的牌能否结附于某宿主"与"该牌从手牌打出时能否选它为目标"共用同一实现（`CardManager.MatchesRule`），
        /// 因此不会出现"随便选一个宿主"或"卡文写了结附秘社却结到别处"。
        ///
        /// 【A53·2026-09 用户拍板修订】候选有多个时**必须由玩家选**（原"引擎取确定性第一个"的口径作废）：
        ///  · 单机有 UI 且候选 ≥ 2 → `IGameUIHooks.PromptPickCard`（**不可取消**：卡文"结附于合理的目标"是强制的）；
        ///  · 单机无 UI（headless/测试）→ 确定性兜底取第一个（与其它"无 UI 兜底"同款纪律）；
        ///  · 联机 → `AttachSearchedCardByOwnRulesViaGatewayAsync` 经网关 `SelectTarget` 让检索者选。
        ///  · 候选 = 1 时**不弹窗**（没有可决策空间）→ 直接结附那一个。
        ///
        /// 解析顺序：
        ///  1. `匹配到卡类合法宿主` → 玩家从中选（1 个则直接结附）；
        ///     （如检索到 JC012『结附于本方秘社』→ 结附本方秘社）
        ///  2. 无卡类宿主、但被检索卡声明了 `Kind=Area` 规则（结附于地区）→ 结附「来源所在地区」；
        ///  3. 都没有 → **按"无合法目标"处理**：不结附、不进场的牌**放回拥有者牌库**
        ///     （牌库不因一次失败的结附少一张牌；调用方随后仍按卡文"然后洗牌"洗牌）。
        /// </summary>
        /// <param name="onDone">宿主选定并结附完成后的续算（调用方在其中揭示/洗牌）；选宿主时会挂起等待。</param>
        private void AttachSearchedCardByOwnRules(CardInstance card, CardInstance source, string targetAreaId,
            System.Action onDone = null)
        {
            if (card == null) { onDone?.Invoke(); return; }
            string areaUid = ResolveSearchArea(source, targetAreaId);
            var hosts = CollectOwnRuleHosts(card, out bool areaRuleDeclared);

            if (hosts.Count > 1 && UIHooks != null && card.ControllerId >= 1)
            {
                GD.Print($"[EffectEngine] 检索：宿主候选 {hosts.Count} 个 → 由玩家{card.ControllerId} 选择结附目标（不可取消）");
                UIHooks.PromptPickCard(card.ControllerId,
                    $"为「{card.PlayerTemplate?.CardName ?? card.UID}」选择结附的目标",
                    hosts, allowCancel: false,
                    picked =>
                    {
                        CardInstance host = hosts[0];
                        if (picked != null && hosts.Contains(picked)) host = picked;
                        else
                            GD.PrintErr($"[EffectEngine] ⛔ 结附宿主回包非法 → 确定性兜底取第一个合法宿主（{host.UID}）");
                        AttachSearchedCard(card, host, source, targetAreaId);
                        onDone?.Invoke();
                    });
                return;
            }

            if (hosts.Count > 0)
            {
                var host = hosts[0];
                GD.Print($"[EffectEngine] 检索：宿主候选 {hosts.Count} 个 → 直接结附 {host.PlayerTemplate?.CardName ?? host.CouncilTemplate?.CardName ?? host.UID}" +
                         (hosts.Count == 1 ? "（唯一合法宿主，无可决策空间）" : "（无 UI：确定性兜底取第一个）"));
                AttachSearchedCard(card, host, source, targetAreaId);
                onDone?.Invoke();
                return;
            }

            if (areaRuleDeclared && !string.IsNullOrEmpty(areaUid))
            {
                GD.Print($"[EffectEngine] 检索：被检索卡目标规则为『结附于地区』→ 结附地区 {areaUid}");
                AttachSearchedCard(card, null, source, targetAreaId);
                onDone?.Invoke();
                return;
            }

            GD.Print($"[EffectEngine] ⚠ 检索去向『结附』：{card.PlayerTemplate?.CardName} 无合法宿主" +
                     "（其 TargetRules 在当前场上无匹配对象）→ 按『无合法目标』处理（不结附；牌回拥有者牌库）");
            ReturnSearchedCardToDeck(card);
            onDone?.Invoke();
        }

        /// <summary>
        /// 【P4·JC051／A53·联机】检索去向『结附』的宿主选择（网关版，与单机 `AttachSearchedCardByOwnRules`
        /// **同一候选集、同一强制口径**，差别只在"谁来问"）：
        /// 候选 ≥ 2 → 经网关 `SelectTarget` 让检索者选；回包不在候选内/取消 → **确定性兜底取第一个合法宿主**
        /// （fail-safe：强制结附不能被非法回包"取消掉"）。候选 = 1 → 直接结附；无候选 → 地区规则 / 回牌库。
        /// </summary>
        private async System.Threading.Tasks.Task AttachSearchedCardByOwnRulesViaGatewayAsync(CardInstance card,
            CardInstance source, string targetAreaId, int searcher)
        {
            if (card == null || InputGateway == null) return;
            string areaUid = ResolveSearchArea(source, targetAreaId);
            var hosts = CollectOwnRuleHosts(card, out bool areaRuleDeclared);

            if (hosts.Count > 1)
            {
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = searcher,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
                    SourceUid = source?.UID,
                    Prompt = $"为「{card.PlayerTemplate?.CardName ?? card.UID}」选择结附的目标",
                    ForceChoice = true,          // 强制结附：客户端不提供"取消"
                };
                foreach (var h in hosts)
                {
                    req.OptionUids.Add(h.UID);
                    req.OptionLabels.Add(h.PlayerTemplate?.CardName ?? h.CouncilTemplate?.CardName ?? h.UID);
                }
                var result = await InputGateway.RequestChoice(searcher, req);
                CardInstance chosen = null;
                if (result != null && !result.Cancelled && !string.IsNullOrEmpty(result.SelectedUid))
                    chosen = hosts.Find(h => string.Equals(h.UID, result.SelectedUid, System.StringComparison.Ordinal));
                if (chosen == null)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 结附宿主回包『{result?.SelectedUid}』不在本次合法候选中（或取消）" +
                                " → 确定性兜底取第一个合法宿主（fail-safe：强制结附不可被规避）");
                    chosen = hosts[0];
                }
                AttachSearchedCard(card, chosen, source, targetAreaId);
                return;
            }

            if (hosts.Count > 0)
            {
                GD.Print($"[EffectEngine] 检索：宿主候选 {hosts.Count} 个 → 直接结附 {hosts[0].PlayerTemplate?.CardName ?? hosts[0].CouncilTemplate?.CardName ?? hosts[0].UID}（唯一合法宿主）");
                AttachSearchedCard(card, hosts[0], source, targetAreaId);
                return;
            }

            if (areaRuleDeclared && !string.IsNullOrEmpty(areaUid))
            {
                GD.Print($"[EffectEngine] 检索：被检索卡目标规则为『结附于地区』→ 结附地区 {areaUid}");
                AttachSearchedCard(card, null, source, targetAreaId);
                return;
            }

            GD.Print($"[EffectEngine] ⚠ 检索去向『结附』：{card.PlayerTemplate?.CardName} 无合法宿主 → 按『无合法目标』处理（不结附；牌回拥有者牌库）");
            ReturnSearchedCardToDeck(card);
        }

        /// <summary>【P4·JC051】检索去向『结附』无合法目标时的收口：牌**放回拥有者牌库**（牌库不因此少牌）。</summary>
        private void ReturnSearchedCardToDeck(CardInstance card)
        {
            if (card == null) return;
            if (ZoneManagerRef != null) ZoneManagerRef.MoveToDeckBottom(card, card.OwnerId, false);
            else CM?.MoveCardToHand(card);
        }

        /// <summary>
        /// 枚举"被检索卡自身 TargetRules 的合法宿主"（确定性顺序：秘社 P1→P2 → 争夺区卡 → 附属卡）。
        /// 判定复用打出阶段同一实现（`CardManager.MatchesRule` = 粗筛大类 + `TargetValidity.CheckRestrictions`）。
        /// `areaRuleDeclared` 输出：该卡是否声明了 `Kind=Area`（结附于地区）的目标规则。
        /// </summary>
        private System.Collections.Generic.List<CardInstance> CollectOwnRuleHosts(CardInstance card, out bool areaRuleDeclared)
        {
            areaRuleDeclared = false;
            var hosts = new System.Collections.Generic.List<CardInstance>();
            var tmpl = card?.PlayerTemplate;
            var rules = tmpl?.TargetRules;
            var zm = ZoneManagerRef;
            if (tmpl == null || rules == null || rules.Count == 0 || zm == null)
            {
                GD.Print($"[EffectEngine] ⚠ 自动选宿主：{card?.PlayerTemplate?.CardName} 未声明 TargetRules" +
                         $"（或 ZoneManager 未注入）→ 视为无合法宿主");
                return hosts;
            }
            foreach (var r in rules)
                if (r != null && r.Kind == TargetKind.Area) areaRuleDeclared = true;

            var candidates = new System.Collections.Generic.List<CardInstance>();
            for (int p = 1; p <= 2; p++) candidates.AddRange(zm.GetZoneCards(CardZone.CouncilZone, p));
            candidates.AddRange(zm.GetZoneCards(CardZone.AreaZone, 0));
            candidates.AddRange(zm.GetZoneCards(CardZone.AttachedZone, 0));

            int srcController = card.ControllerId;   // 敌我判定基准 = 被检索牌的当前控制者（= 检索方）
            var pm = TurnManagerRef?.PlayerManagerRef;
            foreach (var cand in candidates)
            {
                if (cand == null || cand == card) continue;
                foreach (var r in rules)
                {
                    if (r == null || r.Kind == TargetKind.Area || r.Kind == TargetKind.None) continue;
                    if (CardManager.MatchesRule(r, cand, srcController, pm)) { hosts.Add(cand); break; }
                }
            }
            return hosts;
        }

        /// <summary>
        /// 【P2】公开展示一张牌（卡文"展示该牌"）：日志 + `UiNotificationBus.CardRevealed` 广播（View 呈现给双方）。
        /// 语义边界：展示**不改变任何规则状态**（不进公开区、不刷版本号），这也是它与"检视"、"暗藏者现身"的区别。
        /// </summary>
        private void RevealCardPublicly(CardInstance card, int byPlayerId)
        {
            if (card == null) { GD.Print("[EffectEngine] ⚠ Reveal 跳过：对象为空"); return; }
            GD.Print($"[EffectEngine] 🔎 公开展示：玩家{byPlayerId} 展示「{card.PlayerTemplate?.CardName ?? card.UID}」");
            Script.Manager.UiNotificationBus.Instance?.NotifyCardRevealed(card, byPlayerId);
        }

        /// <summary>
        /// 【P3·H／K24】解析一次 `Inspect` 动作的**被检视卡集合**（同步与联机两条路径共用，语义只有一处）。
        /// 对象写法：
        ///  · `自身` → 来源卡；
        ///  · `目标`（缺省）→ 当前目标卡；**无目标返回空集**（不回退成来源卡：多目标效果在一个卡槽都没选时
        ///    动作序列仍会跑一遍，回退会把来源卡自己检视出去）；
        ///  · `目标玩家牌库顶` → 【K24】目标玩家（本次结算锁定）牌库顶的一张（**只读不取出**，牌库不变）；
        ///  · `目标玩家手牌` → 【K24】目标玩家的全部手牌（JC057 类"检视目标玩家的手牌"）。
        /// 未登记的写法 → 空集 + 告警（fail-closed，绝不误检视别的卡）。
        /// </summary>
        private System.Collections.Generic.List<CardInstance> ResolveInspectTargets(EffectAction action,
            CardInstance source, CardInstance target)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            string obj = ArgAt(action, 0, "目标");
            switch (obj)
            {
                case "自身":
                    if (source != null) result.Add(source);
                    break;
                case "目标":
                    if (target != null) result.Add(target);
                    else GD.Print("[EffectEngine] ⚠ Inspect 跳过：无目标（对象=目标 且当前没有目标）");
                    break;
                case "目标玩家牌库顶":
                {
                    int pid = _currentTargetPlayerId;
                    if (pid <= 0) { GD.Print("[EffectEngine] ⚠ Inspect 跳过：未指定目标玩家"); break; }
                    var top = TurnManagerRef?.PeekDeckOf(pid, 1);
                    if (top == null || top.Count == 0)
                    { GD.Print($"[EffectEngine] ⚠ Inspect 跳过：玩家{pid} 牌库为空"); break; }
                    result.Add(top[0]);
                    break;
                }
                case "目标玩家手牌":
                {
                    int pid = _currentTargetPlayerId;
                    if (pid <= 0) { GD.Print("[EffectEngine] ⚠ Inspect 跳过：未指定目标玩家"); break; }
                    var hand = TurnManagerRef?.GetHandOf(pid);
                    if (hand != null) foreach (var c in hand) if (c != null) result.Add(c);
                    break;
                }
                default:
                    GD.PushWarning($"[EffectEngine] ⚠ Inspect 对象写法「{obj}」未登记 → 不检视任何卡（fail-closed）");
                    break;
            }
            return result;
        }

        /// <summary>
        /// 【P3·H】检视（**私密**查看）：仅把该卡的面呈现给 `inspectorPlayerId` 本人，不改变任何对局状态
        /// （§规则：检视不是展示、也不是翻面）。单机经 `UiNotificationBus.NotifyCardInspected`（View 私下呈现），
        /// 联机经 `ChoiceRequest.Kind.InspectCards`（只把卡面下发给检视者，见 `ExecuteVerbAsync` 分支）。
        /// </summary>
        private void InspectCardPrivately(CardInstance card, int inspectorPlayerId)
        {
            if (card == null) return;
            GD.Print($"[EffectEngine] 👁 检视（私密）：玩家{inspectorPlayerId} 检视「{card.PlayerTemplate?.CardName ?? card.UID}」" +
                     $"（控制者=P{card.ControllerId}，区域={card.CurrentZone}，暗藏={card.IsCovert}）");
            Script.Manager.UiNotificationBus.Instance?.NotifyCardInspected(card, inspectorPlayerId);
        }

        /// <summary>
        /// 【触发语义】"卡是否在场上"（争夺区/附属区）：供"只在场上才有意义"的动词做护栏。        /// 存在意义：触发只看条件是否满足过、不看来源此刻场景，于是"死亡触发的效果"是在**墓地**里结算的；
        /// 这类效果若含 Move/Latent/Uncover 等"场上动作"，不加护栏就会把墓地里的卡搬回场上/翻面。
        /// </summary>
        private static bool IsOnField(CardInstance card)
            => card != null && (card.CurrentZone == CardZone.AreaZone || card.CurrentZone == CardZone.AttachedZone);

        /// <summary>
        /// Gain 动词落地（P2）：解析 Args 并实际修改目标卡状态。
        /// Args 约定：["防御","1"] / ["黑刀","1"] / ["费用减少","1","过滤串","一次性"] 等。
        /// durationText 含"回合结束"则登记时效修正，回合结束自动还原。
        ///
        /// 【P1】两处增强：
        ///  1) **能力图标名 → 属性**映射（黑刀=战斗、白眼=先手调查、黑眼=调查、白刀=先手战斗、黑星=势力、白星=先手势力）：
        ///     此前除"战斗属性"等旧词外，所有图标名都走 default 降级成 Keyword 授予，**不产生任何数值效果**；
        ///  2) **支持负数**：amt &lt; 0 时改走 `Revoke`（如 JC089「-1 防御」类减益），不再把负数写进授予字典。
        /// </summary>
        private void ApplyGain(CardInstance source, CardInstance target, Array<string> args, string durationText)
        {
            // 受影响主体：优先 target（属性增益给目标角色），费用修正等无 target 时用 source 的拥有者
            CardInstance subject = target ?? source;
            // 【K11】显式主体（Args[2]）：`"目标"` / `"自身"` / `"封印宿主"`（＝source.SealedBy，如 JC100"该角色"）
            string subjectArg = (args != null && args.Count > 2) ? args[2] : null;
            if (!string.IsNullOrEmpty(subjectArg))
            {
                subject = subjectArg switch
                {
                    "目标" => target ?? subject,
                    "自身" => source ?? subject,
                    "封印宿主" => source?.SealedBy ?? subject,
                    // 【P3·K13】"受此结附的角色"＝本卡（附属）的**结附宿主**（JC093 怨灵咒刃：宿主获得黑刀/黑星）
                    "结附宿主" => source?.AttachedTo ?? subject,
                    _ => subject,
                };
            }
            if (subject == null) { GD.Print("[EffectEngine] Gain 主体为空，跳过"); return; }
            if (args == null || args.Count < 1) return;
            string what = args[0];
            int amt = args.Count >= 2 && int.TryParse(args[1], out var a) ? a : 1;
            bool timed = durationText.Contains("回合结束");

            // 【P1】能力图标名 → 实例属性名的单一映射表（黑=常规 / 白=先手；刀=战力 / 眼=调查 / 星=势力）
            string attrName = IconToAttribute(what);

            switch (what)
            {
                case "防御":
                    // 防御属性：直接改 CurrentDefense（已有 HasDefense 语义），时效项记录还原量
                    if (subject.CurrentDefense < 0) subject.CurrentDefense = 0; // 原本无防御则临时获得
                    subject.CurrentDefense += amt;
                    // 下限钳制：防御不得低于"无防御"哨兵 -1（避免出现 -2 之类非法值）
                    if (subject.CurrentDefense < -1) subject.CurrentDefense = -1;
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 防御 {amt:+#;-#;0}（当前 {subject.CurrentDefense}）");
                    if (timed) RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Defense", Value = "防御", Amount = amt, ExpireTurn = CurrentTurn });
                    break;

                case "屏障":
                    // 【P3】屏障（规则文本：不能被敌方卡牌或能力指定为目标）。
                    // 经 CardInstance.SetGuard 收口（会发状态变化事件，供目标丢失总线/UI 刷新）。
                    // amt<0 = 解除屏障；时效项（"直到回合结束"）在 ExpireTurnEndEffects 里置回 false。
                    subject.SetGuard(amt >= 0);
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} {(amt >= 0 ? "获得" : "失去")}屏障");
                    if (timed && amt >= 0)
                        RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Guard", Value = "屏障", Amount = 1, ExpireTurn = CurrentTurn });
                    break;

                case "战斗属性":
                case "先手属性":
                case "调查属性":
                case "心灵属性":
                case "力量属性":
                    // 旧词（历史遗留）：保持"关键字"语义不动，避免影响既有卡
                    if (amt < 0) subject.Revoke("Keyword", what, -amt);
                    else subject.Grant("Keyword", what, amt);
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 关键字 [{what}]×{amt}");
                    if (timed) RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Keyword", Value = what, Amount = amt, ExpireTurn = CurrentTurn });
                    break;

                case "费用减少":
                    // 玩家级费用修正（P2）：减到打出费用上，由 TurnManager 经 GetCostModification 读取。
                    // D36 修正：归属玩家以控制者（ControllerId）为准而非拥有者（OwnerId）——
                    // 夺控场景（Owner≠Controller）下，折扣应挂到当前控制者身上，与 D29 激活付费语义一致。
                    // 控制者取自 source（激活的附属）或 target；args[2] 可选，作为过滤串（卡名/Organization，如"帷幕守望"）。
                    // args[3] 可选 = "一次性"：实现"下一张"语义（打出/翻面符合条件牌一次后消费移除）；否则整回合全减免。
                    // 注（2026-08-11）：已实装 JC012 复用此接口，consumeOnce 经 args[3] 传入。
                    int pid = (source != null ? source.ControllerId : (target != null ? target.ControllerId : -1));
                    string filter = args.Count >= 3 ? args[2] : "";
                    bool consumeOnce = args.Count >= 4 && args[3] == "一次性";
                    // 【JC055②】args[4]（可选）＝该减免作用的**付费通道**：缺省 `"打出"`；
                    // 写 `"打出,现身"` 表示**两个通道都能享受**（卡文"你打出的下一张灾难牌"＋用户口径：
                    // 暗藏者付费翻面（现身）同样可享受该减免）。
                    string gainScope = args.Count >= 5 && !string.IsNullOrWhiteSpace(args[4]) ? args[4].Trim() : "打出";
                    GD.Print($"[EffectEngine] Gain 费用减少 转交玩家{pid} 费用修正栈（{amt}，filter='{filter}'，{(consumeOnce ? "一次性" : "整回合")}，通道={gainScope}，timed={timed}）");
                    if (timed && pid >= 0)
                        ApplyCostModification(pid, amt, CurrentTurn, filter, consumeOnce,
                            renewable: false, sourceUid: null, costScope: gainScope);
                    else GD.Print($"[EffectEngine] ⚠ 费用减少未登记：timed={timed}, pid={pid}（需 Duration 含'回合结束'且控制者有效）");
                    break;

                case var _ when attrName != null:
                    // 【P1】能力图标 → Attribute（真正改数值的路径；负数走 Revoke）
                    // 【P3·K4 修正】**两条通道都写**：图标**存在性**（`Icon`，供 `HasIcon`/展示/"有图标"类判定）
                    // ＋ **数值**（`Attribute`，如黑刀=+1 战力）。此前只写 Attribute → 卡面"获得［黑刀］"
                    // 在 UI 与 `HasIcon` 判定上落空（JC116 曾踩到同一坑，JC027 形态授予这次同样踩到）。
                    if (amt < 0)
                    {
                        subject.Revoke("Attribute", attrName, -amt);
                        subject.Revoke("Icon", what, -amt);
                    }
                    else
                    {
                        subject.Grant("Attribute", attrName, amt);
                        subject.Grant("Icon", what, amt);
                    }
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 获得图标[{what}]×{amt}（属性 '{attrName}'，图标+属性双通道）");
                    if (timed)
                    {
                        RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Attribute", Value = attrName, Amount = amt, ExpireTurn = CurrentTurn });
                        RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Icon", Value = what, Amount = amt, ExpireTurn = CurrentTurn });
                    }
                    break;

                default:
                    // 其它属性/图标：尝试作为关键字授予
                    if (amt < 0) subject.Revoke("Keyword", what, -amt);
                    else subject.Grant("Keyword", what, amt);
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 授予 [{what}]×{amt}");
                    if (timed) RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Keyword", Value = what, Amount = amt, ExpireTurn = CurrentTurn });
                    break;
            }
        }

        /// <summary>
        /// 【P3】`Gain` 的群体主体写法（Args[2]）：`所有本方角色` / `所有其他本方角色`（"其他"＝排除来源卡自身）。
        /// </summary>
        private static bool IsMassGainSubject(EffectAction action)
        {
            string s = ArgAt(action, 2, "");
            return s == "所有本方角色" || s == "所有其他本方角色";
        }

        /// <summary>
        /// 【P3】取"全场"由某控制者掌控的**角色**卡（供群体主体类动词使用；跨全部争夺区，不是"本地区"）。
        /// 暗藏者（背面）也算角色卡（`MapInstanceToTargetKind` 会映射为 Covert，故此处按**卡类别**判定）。
        /// </summary>
        private System.Collections.Generic.List<CardInstance> CollectFriendlyCharacters(CardInstance source, bool excludeSource)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            int me = source?.ControllerId ?? -1;
            if (me <= 0) return result;
            var all = CM?.GetAreaCards();
            if (all == null) return result;
            foreach (var c in all)
            {
                if (c == null || c.IsDying) continue;
                if (c.ControllerId != me) continue;
                if (c.CurrentZone != CardZone.AreaZone) continue;
                if (c.PlayerTemplate == null || (c.PlayerTemplate.CategoryFlag & CardCategory.Character) == 0) continue;
                if (excludeSource && c == source) continue;
                result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// 【P3】持续授予**自愈**：`ResetToBaseState`（换区/现身复位）会重建派生数值（防御/战斗/调查…），
        /// 于是"运行期授予"折进数值的那部分会被清掉——图标/关键字（集合型）仍在，但 `Attribute 防御 -1`
        /// 这类**数值型**授予会丢失（如 JC089「宿主 -1 防御」在附属落位后立即被复位覆盖）。
        /// 本方法按 `_continuousRegistry` 逐条校验 `IsGrantPresent`，缺失者补授
        /// （与光环的 `ApplyAuraGrantsTo` 同一思路，幂等）。
        /// </summary>
        /// <param name="playerId">只处理"授予目标属于该玩家"的记录（&lt;=0＝不筛选，供测试/全量调用）。</param>
        public int ReapplyMissingContinuousGrants(int playerId)
        {
            int healed = 0;
            foreach (var kv in _continuousRegistry)
            {
                var rec = kv.Value;
                if (rec == null || string.IsNullOrEmpty(rec.TargetUid)) continue;
                var t = CM?.GetInstance(rec.TargetUid);
                if (t == null) continue;
                if (playerId > 0 && t.ControllerId != playerId) continue;
                foreach (var g in rec.Grants)
                {
                    if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                    if (IsGrantPresent(t, g)) continue;
                    t.Grant(g.Kind, g.Value, g.Amount);
                    healed++;
                    GD.Print($"[EffectEngine] ♻ 持续授予自愈：{t.PlayerTemplate?.CardName} ← 「{g.Kind}」:「{g.Value}」({g.Amount})");
                }
            }
            return healed;
        }

        /// <summary>
        /// 【P1】能力图标名 → 实例属性名的唯一映射（两端/各卡共用；未识别返回 null 表示非图标名）。
        /// 约定：黑=常规能力、白=先手能力；刀=战力、眼=调查、星=势力。
        /// 例：黑刀=战斗、白眼=先手调查、黑眼=调查、白刀=先手战斗、黑星=势力、白星=先手势力。
        /// 同时接受"属性名直写"（如"战斗"/"先手调查"），便于卡脚本两种写法都可用。
        /// </summary>
        public static string IconToAttribute(string iconOrAttr)
        {
            switch (iconOrAttr)
            {
                case "黑刀": case "战斗": return "战斗";
                case "白眼": case "先手调查": return "先手调查";
                case "黑眼": case "调查": return "调查";
                case "白刀": case "先手战斗": return "先手战斗";
                case "黑星": case "势力": return "势力";
                case "白星": case "先手势力": return "先手势力";
                default: return null;
            }
        }

        /// <summary>【P3·图标镜像】把"被封印在**本卡**上的**角色牌**"的**印刷能力图标**汇总成一批属性授予（JC066 封魔瓶瑟琳娜）。
        /// 图标→属性沿用 `IconToAttribute` 的既有映射：黑刀=战斗 / 黑眼=调查 / 黑星=势力 / 白刀=先手战斗 / 白眼=先手调查 / 白星=先手势力。
        /// 口径：① 只镜像**角色牌**（`CategoryFlag` 含 Character；非角色封印牌不提供能力图标）；
        ///      ② 只读**印刷**图标（封印牌已不在场，其运行期授予不参与镜像）；
        ///      ③ **不含魔法领域图标**（领域 ≠ 能力图标，卡文只写"能力栏内的能力图标"）；
        ///      ④ 同属性多来源**累加**为一条授予（`Grant` 本身按值累加，撤销时按记录精确还原）。</summary>
        private static System.Collections.Generic.List<EffectGrant> BuildMirroredIconGrants(CardInstance card)
        {
            var result = new System.Collections.Generic.List<EffectGrant>();
            if (card?.SealedCards == null) return result;
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var sealedCard in card.SealedCards)
            {
                var t = sealedCard?.PlayerTemplate;
                if (t == null) continue;
                if ((t.CategoryFlag & CardCategory.Character) == 0) continue;   // ① 只镜像角色牌
                AddMirroredIcon(counts, "战斗", t.Strength);
                AddMirroredIcon(counts, "调查", t.Investigation);
                AddMirroredIcon(counts, "势力", t.Power);
                AddMirroredIcon(counts, "先手战斗", t.FirstStrikeStrength);
                AddMirroredIcon(counts, "先手调查", t.FirstStrikeInvestigation);
                AddMirroredIcon(counts, "先手势力", t.FirstStrikePower);
            }
            // 确定性顺序（按固定属性名表输出，避免字典遍历顺序影响日志/测试）
            foreach (var attr in new[] { "调查", "战斗", "势力", "先手调查", "先手战斗", "先手势力" })
                if (counts.TryGetValue(attr, out int n) && n > 0)
                    result.Add(new EffectGrant { Kind = "Attribute", Value = attr, Amount = n });
            return result;
        }

        private static void AddMirroredIcon(System.Collections.Generic.Dictionary<string, int> counts, string attr, int amount)
        {
            if (amount <= 0) return;
            counts.TryGetValue(attr, out int cur);
            counts[attr] = cur + amount;
        }

        /// <summary>【P3·K3】挂"伤害护盾"：本回合内对该卡的**所有**伤害被防止（JC078 光能盾）。
        /// 幂等（同卡同回合只登记一条——护盾本身没有"层数/剩余点数"语义，重复结算不叠加）；
        /// 期限＝本回合，回合结束由 `ExpireTurnEndEffects` 出栈失效（护盾即登记项本身，无卡面状态需还原）。</summary>
        public void RegisterDamageShield(CardInstance card)
        {
            if (card == null) return;
            if (IsDamagePrevented(card))
            {
                GD.Print($"[EffectEngine] ✓ 伤害护盾已生效（{card.PlayerTemplate?.CardName}，本回合不重复登记）");
                return;
            }
            RegisterTimedMod(new TimedMod
            {
                TargetUid = card.UID,
                Kind = "DamageShield",
                Value = "全部伤害",
                Amount = 1,
                ExpireTurn = NowTurn,
            });
            GD.Print($"[EffectEngine] ✓ {card.PlayerTemplate?.CardName} 获得伤害护盾（本回合内所有伤害被防止）");
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>【P3·K3】该卡当前是否处于"伤害被全部防止"的护盾下。
        /// 消费点＝`CardManager.DamagePreventer`（装配层调用）；判定用卡 UID（实例换区不换 UID）。</summary>
        public bool IsDamagePrevented(CardInstance card)
        {
            if (card == null) return false;
            foreach (var m in _timedModStack)
                if (m.Kind == "DamageShield" && string.Equals(m.TargetUid, card.UID, System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>卡级时效修正登记（P2）。</summary>
        private void RegisterTimedMod(TimedMod m) => _timedModStack.Add(m);

        /// <summary>
        /// 回合结束还原（P2 核心）：撤销所有 ExpireTurn ≤ turn 的时效修正。
        /// 卡级（防御/关键字）调用 CardInstance.Revoke / 还原 CurrentDefense；玩家级费用修正直接出栈。
        /// 由 TurnManager.OnTurnEnded 订阅调用。
        /// </summary>
        public void ExpireTurnEndEffects(int turn)
        {
            for (int i = _timedModStack.Count - 1; i >= 0; i--)
            {
                var m = _timedModStack[i];
                if (m.ExpireTurn > turn) continue;

                if (m.Kind == "CostMod")
                {
                    // 【P3】"每回合可再生的一次性"：**不**按回合到期出栈（登记表即时效期），
                    // 仅当来源卡已不在场（被注销/离场）时才清理。
                    if (m.Renewable)
                    {
                        var src = string.IsNullOrEmpty(m.SourceUid) ? null : CM?.GetInstance(m.SourceUid);
                        if (src != null) continue;   // 来源仍在场 → 保留（下回合自动恢复）
                        GD.Print($"[EffectEngine] ⌛ 时效清理：可再生费用修正（来源已离场，filter='{m.FilterText}'）");
                    }
                    else
                    {
                        GD.Print($"[EffectEngine] ⌛ 时效还原：玩家{m.PlayerId} 费用修正 +{m.Amount} 到期");
                    }
                    Script.Manager.UiNotificationBus.Instance?.NotifyCostModChanged();
                }
                else if (m.Kind == "Control")
                {
                    // 【P3】控制权归还（JC129"直到回合结束"）：把卡交回**原控制者**
                    // （卡可能已离场/被夺控多次 → 找不到实例或已不属本次夺取者时跳过）。
                    var ctrl = CM?.GetInstance(m.TargetUid);
                    if (ctrl != null && m.PlayerId > 0)
                    {
                        ZoneManagerRef?.ChangeController(ctrl, m.PlayerId);
                        GD.Print($"[EffectEngine] ⌛ 时效还原：{ctrl.PlayerTemplate?.CardName} 控制权归还玩家{m.PlayerId}");
                        Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    }
                    else if (ctrl != null)
                        GD.Print($"[EffectEngine] ⌛ 时效还原：控制权归还跳过（原控制者未记录，玩家{m.PlayerId}）");
                }
                else if (m.Kind == "DamageShield")
                {
                    // 【P3·K3】伤害护盾到期：护盾**就是**这条登记项，出栈即失效（卡面无状态需还原）。
                    GD.Print($"[EffectEngine] ⌛ 时效还原：伤害护盾到期（{CM?.GetInstance(m.TargetUid)?.PlayerTemplate?.CardName}）");
                }
                else if (m.Kind == "AreaCombatSkip")
                {
                    // 【P3·K18】"本回合该地区略过对抗"到期（JC118）：登记项即时效载体，出栈即失效（地区无卡面状态）。
                    GD.Print($"[EffectEngine] ⌛ 时效还原：地区 {m.TargetUid} 的『略过对抗[{m.Value}]』到期（本回合结束）");
                }
                else if (m.Kind == "IconReplace")
                {
                    // 【P3·K20】图标替换到期（JC106）：`Value = 来源属性|目标属性` → 把来源属性还回去、把目标属性收回。
                    var ic = CM?.GetInstance(m.TargetUid);
                    var parts = (m.Value ?? "").Split('|');
                    if (ic != null && parts.Length == 2)
                    {
                        ic.Grant("Attribute", parts[1], -m.Amount);   // 收回换来的图标
                        ic.Grant("Attribute", parts[0], m.Amount);    // 还原原图标
                        GD.Print($"[EffectEngine] ⌛ 时效还原：{ic.PlayerTemplate?.CardName} 的图标替换 [{parts[0]}→{parts[1]}]×{m.Amount} 到期");
                        Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    }
                }
                else if (m.Kind == "Form")
                {
                    // 【P3·K4】**实例级形态**到期（JC027"成为一个地精角色…直到回合结束"）：撤销形态覆盖
                    // （类别/卡名/印刷效果失活/清零的图标与防御）＋移除此期间追加的子类别；
                    // 若形态期间印刷效果被失活，则按印刷效果表重新注册持续效果。
                    var fc = CM?.GetInstance(m.TargetUid);
                    if (fc != null)
                    {
                        // 【顺序】先按形态效果表注销（形态效果随之失效），再撤形态，最后按印刷效果表重注册。
                        if (fc.IsPrintedEffectsSuppressed) UnregisterPersistent(fc);
                        foreach (var sub in (m.Value ?? "").Split(','))
                            if (!string.IsNullOrWhiteSpace(sub)) fc.Revoke("SubCategory", sub.Trim(), 1);
                        fc.ClearFormOverride();
                        fc.ResetToBaseValues(false);
                        if (fc.CurrentZone == CardZone.AreaZone || fc.CurrentZone == CardZone.AttachedZone)
                            RegisterPersistent(fc);
                        GD.Print($"[EffectEngine] ⌛ 时效还原：{fc.PlayerTemplate?.CardName} 的实例级形态到期（回合结束）");
                        Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                    }
                }
                else
                {
                    var c = CM?.GetInstance(m.TargetUid);
                    if (c != null)
                    {
                        if (m.Kind == "Defense")
                        {
                            c.CurrentDefense -= m.Amount;
                            if (c.CurrentDefense < 0) c.CurrentDefense = -1; // 回到"无防御"
                            GD.Print($"[EffectEngine] ⌛ 时效还原：{c.PlayerTemplate?.CardName} 防御 -{m.Amount}（当前 {c.CurrentDefense}）");
                        }
                        else if (m.Kind == "Guard")
                        {
                            // 【P3】屏障时效还原（JC017「本回合中该角色获得屏障」）
                            c.SetGuard(false);
                            GD.Print($"[EffectEngine] ⌛ 时效还原：{c.PlayerTemplate?.CardName} 失去屏障");
                        }
                        else
                        {
                            c.Revoke(m.Kind, m.Value, m.Amount);
                            GD.Print($"[EffectEngine] ⌛ 时效还原：{c.PlayerTemplate?.CardName} 撤销 [{m.Value}]");
                        }
                    }
                }
                _timedModStack.RemoveAt(i);
            }
            // 修复：所有到期项出栈完成后再统一刷新一次，避免末次 RefreshCostReduction 读到期中态（mod 偏大）被定格。
            // 循环内 CostModChanged 在 RemoveAt 之前触发，最后一项出栈前读到的仍是 mod=未出栈值；
            // 出栈后栈已空却无再次刷新，导致回合结束费用绿标残留。此处兜底刷一次终值。
            Script.Manager.UiNotificationBus.Instance?.NotifyCostModChanged();
            // 【P3】同一"回合结束"时刻：作废到期的**一次性承诺**（`Pending.Expire="本回合结束"`）。
            ExpirePendingEffects(turn);
        }

        /// <summary>【P3】当前回合号（以 `TurnManager.TurnCount` 为权威，缺注入时回退 `CurrentTurn`）。</summary>
        private int NowTurn => TurnManagerRef?.TurnCount ?? CurrentTurn;

        // ==================== 玩家级费用修正栈（P2，JC012 等） ====================
        //【MVC 收口】费用修正变化通知改由 UiNotificationBus.CostModChanged 广播，本类不再持有 static 事件。

        /// <summary>登记玩家级费用修正（如"下一张帷幕守望牌费用-1"）。
        /// filterText 为空=全匹配；否则卡名包含该串 **或** 卡 Organization 等于该串才生效（2026-08-11 扩展 Organization 维度）。
        /// consumeOnce=true 表示"下一张"语义：打出/翻面符合条件牌一次后由 ConsumeCostModification 移除（回合结束未用则随 ExpireTurnEndEffects 出栈流失）。</summary>
        public void ApplyCostModification(int playerId, int amount, int expireTurn, string filterText, bool consumeOnce = false)
            => ApplyCostModification(playerId, amount, expireTurn, filterText, consumeOnce, renewable: false, sourceUid: null, costScope: "打出");

        /// <summary>
        /// 【P3】完整重载：`renewable = true` 表示"**每回合可再生的一次性**"费用修正
        /// （JC026 洛特尼科夫「每个回合中你所打出的第一张装备牌，其所需费用减少1」）——
        /// 与普通"一次性"的区别：消费后**不移除**，而是本回合内失效，**下回合自动恢复**；
        /// 时效终结由"来源卡是否仍在场"决定（`UnregisterContinuous` 按 `sourceUid` 清理）。
        /// `costScope` ＝ 该减免作用的**付费通道**（`"打出"` 缺省 / `"现身"`＝暗藏者付费翻面，JC033）。
        /// </summary>
        public void ApplyCostModification(int playerId, int amount, int expireTurn, string filterText,
            bool consumeOnce, bool renewable, string sourceUid, string costScope = "打出")
        {
            _timedModStack.Add(new TimedMod
            {
                PlayerId = playerId, Kind = "CostMod", Amount = amount, ExpireTurn = expireTurn,
                FilterText = filterText ?? "", ConsumeOnce = consumeOnce,
                Renewable = renewable, SourceUid = sourceUid,
                CostScope = string.IsNullOrEmpty(costScope) ? "打出" : costScope,
            });
            GD.Print($"[EffectEngine] ✓ 玩家{playerId} 费用修正 +{amount}（filter='{filterText}'，回合{expireTurn}到期，" +
                     $"{(renewable ? "**每回合可再生的一次性**" : consumeOnce ? "一次性" : "整回合")}）");
            Script.Manager.UiNotificationBus.Instance?.NotifyCostModChanged();
        }

        /// <summary>消费并移除玩家的一次性费用修正项（打出/翻面符合条件牌后调用，实现"下一张"语义）。
        /// 仅移除匹配的且 ConsumeOnce=true 的项；非一次性项（整回合全减免）不受影响。
        /// 【P1】匹配改用 `CostModFilter`（与服务端/客户端查询同一份实现），支持"组织:/子类别:/域:/有域图标"语法。</summary>
        public void ConsumeCostModification(int playerId, string filterText)
            => ConsumeCostModification(playerId, null, filterText);

        /// <summary>【P1】按实例消费：能正确匹配子类别/领域类过滤串。</summary>
        public void ConsumeCostModification(int playerId, CardInstance card, string fallbackFilterText,
            string costScope = "打出")
        {
            string scope = string.IsNullOrEmpty(costScope) ? "打出" : costScope;
            for (int i = _timedModStack.Count - 1; i >= 0; i--)
            {
                var m = _timedModStack[i];
                if (m.Kind != "CostMod" || m.PlayerId != playerId || !m.ConsumeOnce) continue;
                // 【P3】付费通道隔离（打出费 / 现身费各查各的）+ 【JC055②】组合通道（"打出,现身" 或 "全部"）
                if (!ScopeMatchesChannel(m.CostScope, scope)) continue;
                bool hit = string.IsNullOrEmpty(m.FilterText)
                           || m.FilterText == (fallbackFilterText ?? "")
                           || (card != null && CostModFilter.Matches(m.FilterText, card, card.PlayerTemplate?.CardName, card.PlayerTemplate?.Organization));
                if (hit)
                {
                    // 【P3】"每回合可再生的一次性"：不删除，仅标记**本回合已用**（下回合自动恢复）。
                    if (m.Renewable)
                    {
                        m.ConsumedTurn = NowTurn;
                        GD.Print($"[EffectEngine] ⌛ 消费『每回合可再生』费用修正：玩家{playerId} 的 '{m.FilterText}' 本回合已用（回合{NowTurn}，下回合恢复）");
                    }
                    else
                    {
                        GD.Print($"[EffectEngine] ⌛ 消费一次性费用修正：玩家{playerId} 的 '{m.FilterText}' 已使用，移除");
                        _timedModStack.RemoveAt(i);
                    }
                    Script.Manager.UiNotificationBus.Instance?.NotifyCostModChanged();
                }
            }
        }

        /// <summary>查询玩家当前对某张即将打出的卡的费用减免额。
        /// 【P1】匹配规则集中在 `CostModFilter`（与服务端/客户端共用），支持旧语义（卡名含/组织等）
        /// 与扩展语义（组织:/子类别:/域:/有域图标）。</summary>
        public int GetCostModification(int playerId, string cardName, string organization = "", string costScope = "打出")
            => GetCostModification(playerId, null, cardName, organization, costScope);

        /// <summary>【P1】带实例的费用减免查询：允许"子类别/魔法领域图标"类过滤串正确求值
        /// （实例携带运行时授予的子类别与领域）。服务端打出流程应优先使用本重载。</summary>
        public int GetCostModification(int playerId, CardInstance card, string cardName, string organization = "",
            string costScope = "打出")
        {
            int total = 0;
            string scope = string.IsNullOrEmpty(costScope) ? "打出" : costScope;
            foreach (var m in _timedModStack)
            {
                if (m.Kind != "CostMod" || m.PlayerId != playerId) continue;
                // 【P3】付费通道隔离：打出费 / 现身费两条通道各查各的（JC033 只减"付费翻面"的费用）
                // 【JC055②】组合通道：登记 "打出,现身"（或 "全部"）时两个通道都能享受该减免。
                if (!ScopeMatchesChannel(m.CostScope, scope)) continue;
                // 【P3】可再生的费用修正：本回合已消费 → 本回合内不生效（下回合自动恢复）
                if (m.Renewable && m.ConsumedTurn == NowTurn) continue;
                if (CostModFilter.Matches(m.FilterText, card, cardName, organization))
                    total += m.Amount;
            }
            return total;
        }

        /// <summary>【JC055②】付费通道匹配：登记值可为单个通道（`"打出"` / `"现身"`）、
        /// 逗号分隔的**组合**（`"打出,现身"`，表示两个通道都能享受）或通配 `"全部"`。</summary>
        private static bool ScopeMatchesChannel(string registered, string query)
        {
            string reg = string.IsNullOrEmpty(registered) ? "打出" : registered;
            string q = string.IsNullOrEmpty(query) ? "打出" : query;
            if (string.Equals(reg, q, System.StringComparison.Ordinal)) return true;
            if (string.Equals(reg, "全部", System.StringComparison.Ordinal)) return true;
            if (reg.IndexOf(',') < 0) return false;
            foreach (var seg in reg.Split(','))
                if (string.Equals(seg.Trim(), q, System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>导出当前生效中的玩家级费用修正（只读，供快照下行给客户端做减费展示）。
        /// 语义与 GetCostModification 的匹配规则一致，但导出的是「条目」而非某张卡的合计值。</summary>
        public System.Collections.Generic.List<Script.Net.CostModSnap> GetActiveCostMods()
        {
            var list = new System.Collections.Generic.List<Script.Net.CostModSnap>();
            foreach (var m in _timedModStack)
            {
                if (m.Kind != "CostMod") continue;
                list.Add(new Script.Net.CostModSnap
                {
                    PlayerId = m.PlayerId,
                    Amount = m.Amount,
                    FilterText = m.FilterText,
                    ConsumeOnce = m.ConsumeOnce,
                    ExpireTurn = m.ExpireTurn,
                });
            }
            return list;
        }

        // ==================== D2 威名去重（战斗对抗获胜放置势力标志） ====================
        // 维度：areaUid + 控制者，保证"同一控制者多个威名在同一地区只放置一个势力标志"。
        // 单次战斗对抗只 Emit 一次 OnCombatWon（见 TriggerEmitPoints.OnCombatWon）；
        // 每次 Emit 前由 ResetCombatWonDedup 清空，结算 PlaceToken 时按维度查重。
        private readonly System.Collections.Generic.Dictionary<string, bool> _combatWonMarkPlaced
            = new System.Collections.Generic.Dictionary<string, bool>();
        private string _combatWonKey(string areaUid, int controller)
            => $"{areaUid}|{controller}";

        /// <summary>清空威名放置去重表（每次战斗对抗 Emit 前调用，保证单次战斗维度内去重）。</summary>
        public void ResetCombatWonDedup() => _combatWonMarkPlaced.Clear();

        // ==================== 对局级全量重置（B2：单场景常驻 + 显式 Reset） ====================
        // 一局结束、重开下一局前调用（由 GameInitializer.ResetMatch 编排，且必须先于 CardManager.Reset 调用）。
        // 作用：清空本引擎全部静态对局状态注册表 + 复位 CurrentTurn。
        // 注意：仅清空"对局状态数据"，绝不清空模板级注册表（CardEffectFactory）与常驻注入引用
        // （TurnManagerRef/CardManagerRef/ZoneManagerRef/IsStackCircuitBroken/UIHooks）。
        // 采用"直接清空"方案：B2 下第二局会重建全部 CardInstance，旧卡对象整体被抛弃，无需先 Revoke。
        public void ResetAll()
        {
            _continuousRegistry.Clear();
            _activePrereqCards.Clear();
            _auraGrants.Clear();
            _timedModStack.Clear();
            _combatWonMarkPlaced.Clear();
            _pendingPrereqCards.Clear();
            _blockDeployCovertPlayers.Clear();
            _areaCombatSkips.Clear();
            _covertPowerExcludedBy.Clear();
            _noUntapWithSealedCardsBy.Clear();
            _extraPowerContestBy.Clear();
            _damageRedirects.Clear();
            _controlGains.Clear();   // 【P3·K4】持续夺控表（JC036）
            _nonOrgEntryBlockers.Clear();   // 【P3·K21】JC064 戒严：非指定组织不可进入
            _iconSuppressors.Clear();
            _iconSuppressionApplied.Clear();
            _domainSuppressors.Clear();
            _domainsSuppressedCards.Clear();
            _textBlankSuppressors.Clear();
            _textBlankedCards.Clear();
            _grantedAbilities.Clear();
            _pendingEffects.Clear();
            CurrentTurn = 1;
            GD.Print("[EffectEngine] ResetAll: 21 个静态注册表已清空, CurrentTurn=1");
        }

        // ==================== 【P3】一次性待触发承诺（PendingEffect） ====================
        /// <summary>
        /// 一条已登记的承诺：`Source`＝登记它的卡（如 JC082 徽记），`Spec`＝承诺内容，
        /// `ControllerId`＝承诺归属玩家（该玩家在指定时机出现时兑现），`ExpireTurn`＝到期回合（-1＝不过期）。
        /// </summary>
        public sealed class PendingRecord
        {
            public CardInstance Source;
            public PendingEffectSpec Spec;
            public int ControllerId;
            public int ExpireTurn = -1;
        }

        private readonly System.Collections.Generic.List<PendingRecord> _pendingEffects
            = new System.Collections.Generic.List<PendingRecord>();

        /// <summary>当前已登记的承诺数（测试/取证用）。</summary>
        public int PendingEffectCount => _pendingEffects.Count;

        /// <summary>
        /// 【P3】登记一份承诺（由 `ResolveEffect` 在效果声明了 `EffectData.Pending` 时调用）。
        /// 同一张卡同一时机**重复登记**时只保留一条（避免连开两次能力叠出两份承诺）。
        /// </summary>
        public void RegisterPending(CardInstance source, PendingEffectSpec spec)
        {
            if (source == null || spec == null) return;
            foreach (var r in _pendingEffects)
                if (r.Source == source && r.Spec.Timing == spec.Timing) { r.ExpireTurn = CurrentTurn; return; }
            _pendingEffects.Add(new PendingRecord
            {
                Source = source,
                Spec = spec,
                ControllerId = source.ControllerId,
                ExpireTurn = spec.Expire != null && spec.Expire.Contains("回合结束") ? CurrentTurn : -1,
            });
            GD.Print($"[EffectEngine] 📜 登记一次性承诺：{source.PlayerTemplate?.CardName} 在「{spec.Timing}」时机兑现一次" +
                     $"（到期：{spec.Expire}，{(spec.Optional ? "可选" : "强制")}）");
        }

        /// <summary>
        /// 【P3】取"本次时机出现时可以兑现"的承诺（供 `TriggerRegistry` 收集阶段调用）。
        /// 匹配条件：时机一致 ∧（**归属玩家 == 本次事件相关玩家**）。
        /// **无归属玩家的时机**（准备步骤/结束步骤＝双方共有的步骤，`TriggeringPlayerId = -1`）：
        /// 视为"所有在该时机登记的承诺都在此刻兑现"（每个时机事件兑现一次，与"谁发起"无关）。
        /// 其它时机若拿不到归属玩家 → 不兑现（fail-closed）。
        /// </summary>
        public System.Collections.Generic.List<PendingRecord> CollectPending(EffectTiming timing, int contextPlayerId)
        {
            var result = new System.Collections.Generic.List<PendingRecord>();
            bool playerAgnostic = timing is EffectTiming.OnTurnStarted or EffectTiming.OnTurnEnded;
            if (contextPlayerId < 0 && !playerAgnostic) return result;
            foreach (var r in _pendingEffects)
                if (r.Spec.Timing == timing && (playerAgnostic || r.ControllerId == contextPlayerId))
                    result.Add(r);
            return result;
        }

        /// <summary>【P3】兑现（移除）一条承诺。时机一旦出现即**消费**（"下一次"只兑现一次）。</summary>
        public void ConsumePending(PendingRecord record)
        {
            if (record == null) return;
            _pendingEffects.Remove(record);
            GD.Print($"[EffectEngine] 📜 承诺已兑现/消费：{record.Source?.PlayerTemplate?.CardName}（剩余 {_pendingEffects.Count} 条）");
        }

        /// <summary>【P3】到期作废：`Expire="本回合结束"` 的承诺在其到期回合结束时移除（由 `ExpireTurnEndEffects` 调用）。</summary>
        public void ExpirePendingEffects(int turn)
        {
            for (int i = _pendingEffects.Count - 1; i >= 0; i--)
            {
                var r = _pendingEffects[i];
                if (r.ExpireTurn < 0 || r.ExpireTurn > turn) continue;
                _pendingEffects.RemoveAt(i);
                GD.Print($"[EffectEngine] ⌛ 承诺到期作废：{r.Source?.PlayerTemplate?.CardName}（「{r.Spec.Timing}」未出现）");
            }
        }

        // ==================== 条件判定（最小） ====================
        /// <summary>
        /// 【P3】取"本卡的结附宿主"的大类（无宿主 → `None`）。供 `HostIsCouncil`/`HostIsCharacter` 谓词
        /// 与 `PrereqKind.HostKind` 共用同一判定口径（`CardManager.MapInstanceToTargetKind` 按区域映射）。
        /// </summary>
        private static TargetKind HostKindOf(CardInstance card)
        {
            var host = card?.AttachedTo;
            return host == null ? TargetKind.None : CardManager.MapInstanceToTargetKind(host);
        }

        public bool EvaluateCondition(string predicate, CardInstance source, CardInstance target, CardInstance contextCard = null, int triggeringPlayerId = -1, string triggeringAreaId = null, string param = null)
        {
            // 最小实现：支持几种已明确谓词；其余默认视为满足，避免卡死
            switch (predicate)
            {
                case "已横置":
                    return target != null && target.IsTapped;
                // 【A57·秘社 MSJC001】"若你具有【先手标志】"：先手标志由 `TurnManager.FirstPlayerId` 派生
                // （`PlayerManager.SetFirstPlayer` 写入 `Player.IsFirstPlayer`；见该字段注释"先手标志不参与恢复、
                // 由 TurnManager.FirstPlayerId 派生"）。判定基准＝**来源控制者是否为本回合先手**。
                case "具有先手标志":
                case "先手标志":
                {
                    int owner = (source ?? target)?.ControllerId ?? -1;
                    var pmFirst = TurnManagerRef?.PlayerManagerRef;
                    bool has = owner > 0 && pmFirst != null && pmFirst.IsFirstPlayer(owner);
                    GD.Print($"[EffectEngine] 条件「具有先手标志」：玩家{owner} → {(has ? "满足" : "不满足")}");
                    return has;
                }
                // 【P3】前提类谓词：本卡（附属）的**结附宿主**属于指定大类（无宿主 → 不满足）。
                // 用于 JC082 索亚之书：「·收藏：结附于本方秘社 快速行动2…」——只有结附在秘社上时才允许发动该能力。
                case "HostIsCouncil":
                    return HostKindOf(source ?? target) == TargetKind.Council;
                case "HostIsCharacter":
                    return HostKindOf(source ?? target) == TargetKind.Character;
                case "成功封印":
                {
                    // 【P2·修复】"封印成功"的真实判定：**本卡（来源）此刻封印着事件主体卡**。
                    // 原先恒 `true`（"封印系统未实现"的旧注释已过期：`TurnManager.SealCard` / `CardInstance.SealedBy` /
                    // `SealedCards` / `CardZone.SealedZone` 均已落地）——恒真会让依赖它的"若你如此做…"链
                    // 在**封印失败**时照样往下走。
                    var sealedCard = contextCard ?? target;
                    if (sealedCard == null) { GD.Print("[EffectEngine] 条件「成功封印」：无被封印卡上下文 → 不满足"); return false; }
                    bool sealing = ReferenceEquals(sealedCard.SealedBy, source)
                                   && sealedCard.CurrentZone == CardZone.SealedZone;
                    GD.Print($"[EffectEngine] 条件「成功封印」：{sealedCard.PlayerTemplate?.CardName} " +
                             $"封印宿主={sealedCard.SealedBy?.PlayerTemplate?.CardName ?? "无"} 区域={sealedCard.CurrentZone} → {(sealing ? "满足" : "不满足")}");
                    return sealing;
                }
                case "心灵领域忠诚≥2（资产区心心）":
                {
                    // 【P2·修复】真实读**本卡控制者**资产区的【心】（心灵领域）忠诚点数（`PlayerManager.GetDomainLoyalty`，
                    // 与 `PrereqKind.DomainLoyalty` 同一数据源）；原先恒 `true`（"待接入"的旧注释已过期）。
                    var pm2 = TurnManagerRef?.PlayerManagerRef;
                    if (source == null || pm2 == null)
                    {
                        GD.Print("[EffectEngine] 条件「心灵领域忠诚≥2」：来源/PlayerManager 缺失 → 不满足（fail-closed）");
                        return false;
                    }
                    var loy = pm2.GetDomainLoyalty(source.ControllerId);
                    loy.TryGetValue(MagicDomain.Mind, out int mind);
                    GD.Print($"[EffectEngine] 条件「心灵领域忠诚≥2」：玩家{source.ControllerId} 的【心】={mind} → {(mind >= 2 ? "满足" : "不满足")}");
                    return mind >= 2;
                }
                case "PlayedSorcery":
                    // 反应触发条件：刚被打出的卡是事务卡（法术类），且当前实际子类型含"法术"。
                    // 子类型判定用 HasSubCategory（合并静态模板 + 运行时授予）；打出阶段无运行时授予，等价于静态 SubCategories 含"法术"。
                    if (contextCard == null) return false;
                    var ct = contextCard.PlayerTemplate;
                    bool isSorcery = ct != null && (ct.CategoryFlag & CardCategory.Sorcery) != 0;
                    bool hasSpellSub = contextCard.HasSubCategory("法术");
                    if (!isSorcery) GD.Print($"[EffectEngine] 条件「PlayedSorcery」不满足：刚打出的 {ct?.CardName} 类型={ct?.CategoryFlag} 非事务");
                    if (!hasSpellSub) GD.Print($"[EffectEngine] 条件「PlayedSorcery」不满足：刚打出的 {ct?.CardName} 子类型={string.Join("/", contextCard.CurrentSubCategories)} 不含'法术'");
                    return isSorcery && hasSpellSub;
                case "OpponentHandLe3":
                    // JC035 准备阶段触发条件：本卡控制者的对手手牌数 ≤ 3。
                    // 以 source.ControllerId 的对方为"对手"，使双方各自的 JC035 各自判定。
                    if (source == null) return false;
                    int oppId = source.ControllerId == 1 ? 2 : (source.ControllerId == 2 ? 1 : -1);
                    if (oppId < 0) { GD.Print($"[EffectEngine] 条件「OpponentHandLe3」无法满足：source 控制者异常 {source.ControllerId}"); return false; }
                    int oppHand = CM != null ? CM.GetHandCardsByOwner(oppId).Count : 0;
                    GD.Print($"[EffectEngine] 条件「OpponentHandLe3」：对手玩家{oppId} 手牌数={oppHand} → {(oppHand <= 3 ? "满足" : "不满足")}");
                    return oppHand <= 3;
                case "WinnerIsSelf":
                    // "X对抗获胜"类触发通用条件：本卡控制者 == 对抗获胜方（winnerId）。
                    // 由 TriggerRegistry 收集 OnCombatWon/OnInvestigationWon/OnPowerWon 时，
                    // 经 StackEffectInstance.WinnerId → StackEntry.WinnerId → ResolveEffect(winnerId) 透传；
                    // 非对抗路径（winnerId<0）或无来源卡时无法判定，视为不满足。
                    // 命名规范：对应原始能力名 Strength/Investigation/Power（势力）三种对抗，本谓词对三者通用。
                    if (source == null) return false;
                    if (triggeringPlayerId < 0)
                    {
                        GD.Print("[EffectEngine] 条件「WinnerIsSelf」无可判定获胜方（非触发路径），不满足");
                        return false;
                    }
                    bool winnerIsSelf = (source.ControllerId == triggeringPlayerId);
                    GD.Print($"[EffectEngine] 条件「WinnerIsSelf」：来源控制者=P{source.ControllerId} 对抗胜者=P{triggeringPlayerId} → {(winnerIsSelf ? "满足" : "不满足")}");
                    return winnerIsSelf;
                case "WinnerInSameArea":
                    // "X对抗获胜"类触发（威名等）的地区限制条件（Y 方案）：
                    // 本卡所在地区（ZoneDetailId）== 该对抗获胜发生的地区（triggeringAreaId）。
                    // 由 TriggerRegistry 收集时从 ctx.TargetAreaId 透传；
                    // 非对抗路径（triggeringAreaId 空）或无来源卡时无法判定，视为不满足。
                    // 与 WinnerIsSelf 组合可表达"我方在自己所在地区获胜"才触发。
                    if (source == null) return false;
                    if (string.IsNullOrEmpty(triggeringAreaId))
                    {
                        GD.Print("[EffectEngine] 条件「WinnerInSameArea」无可判定地区（非触发路径），不满足");
                        return false;
                    }
                    string selfArea = source.ZoneDetailId ?? "";
                    bool sameArea = !string.IsNullOrEmpty(selfArea) && selfArea == triggeringAreaId;
                    GD.Print($"[EffectEngine] 条件「WinnerInSameArea」：来源地区={selfArea} 获胜地区={triggeringAreaId} → {(sameArea ? "满足" : "不满足")}");
                    return sameArea;
                // 【K1】"每当你…"类触发的通用谓词：事件的引发者 == 本效果来源卡的控制者。
                // 用于 JC080「每当你抓牌时…」（OnDraw 对所有玩家的抓牌广播，只有本方抓牌才应触发）。
                case "TriggerBySelf":
                {
                    if (source == null) return false;
                    if (triggeringPlayerId < 0)
                    {
                        GD.Print("[EffectEngine] 条件「TriggerBySelf」无引发玩家（非触发路径），不满足");
                        return false;
                    }
                    bool bySelf = triggeringPlayerId == source.ControllerId;
                    GD.Print($"[EffectEngine] 条件「TriggerBySelf」：引发者=P{triggeringPlayerId} 来源控制者=P{source.ControllerId} → {(bySelf ? "满足" : "不满足")}");
                    return bySelf;
                }
                case "WinnerIsSelfInSameArea":
                    // 复合条件（Y 方案，威名实际使用的谓词）：同时满足
                    //   (1) WinnerIsSelf：本卡控制者 == 对抗获胜方；
                    //   (2) WinnerInSameArea：本卡所在地区 == 对抗获胜地区。
                    // 仅用 WinnerInSameArea 会让"敌方在我方威名卡所在地区获胜"也误触发，故需复合。
                    if (source == null) return false;
                    bool winnerSelf = (source.ControllerId == triggeringPlayerId);
                    bool winnerArea = !string.IsNullOrEmpty(triggeringAreaId) && (source.ZoneDetailId ?? "") == triggeringAreaId;
                    bool both = winnerSelf && winnerArea;
                    GD.Print($"[EffectEngine] 条件「WinnerIsSelfInSameArea」：控制者=P{source.ControllerId} 胜者=P{triggeringPlayerId} 来源地区={source.ZoneDetailId} 获胜地区={triggeringAreaId} → {(both ? "满足" : "不满足")}");
                    return both;
                // ==================== 【P1】死亡触发类谓词 ====================
                // 配合 P1 接线的 OnCardDied 使用：死亡事件把"死亡的卡"放进 ContextCard/SourceCard，
                // 由 TriggerRegistry 传入本方法的 contextCard（= ctx.EffectiveContextCard）。
                // 语义：只要"有角色死亡"即成立（不含被消灭者自身即为本效果来源卡的情况）。
                case "DiedAnyCharacter":
                {
                    if (contextCard == null || contextCard == source) return false;
                    bool isChar = (contextCard.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0;
                    GD.Print($"[EffectEngine] 条件「DiedAnyCharacter」：死亡卡={contextCard.PlayerTemplate?.CardName} 是否角色={isChar}");
                    return isChar;
                }
                // 语义：死亡的是"本方角色"（控制者与本效果来源卡控制者相同）且非来源卡自身。
                case "DiedAllyCharacter":
                {
                    if (source == null || contextCard == null || contextCard == source) return false;
                    bool allyChar = (contextCard.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0
                                    && contextCard.ControllerId == source.ControllerId;
                    GD.Print($"[EffectEngine] 条件「DiedAllyCharacter」：死亡卡={contextCard.PlayerTemplate?.CardName} " +
                             $"控制者=P{contextCard.ControllerId} 来源控制者=P{source.ControllerId} → {(allyChar ? "满足" : "不满足")}");
                    return allyChar;
                }
                // 【P3·K5】"本方赢得该次战斗对抗 **且** 本地区有本方指定子类别的角色"（JC034「当你赢得一次战斗对抗时，
                // 若有**本方吸血鬼角色**参与了这次战斗对抗，你可以令目标对手弃一张手牌」）。
                // 判定＝A14 的近似口径（用户 2026-09）：**位于战斗地区且属于获胜方**——
                //   ① 本卡控制者 ＝ 对抗获胜方（`triggeringPlayerId`）；
                //   ② 战斗地区内存在**同控制者**、带 `param` 子类别（缺省不限）、**在争夺区**的角色。
                case "WinnerIsSelfWithAllySubCategoryInCombatArea":
                {
                    if (source == null || CM == null) return false;
                    if (string.IsNullOrEmpty(triggeringAreaId)) return false;
                    if (source.ControllerId != triggeringPlayerId)
                    {
                        GD.Print($"[EffectEngine] 条件「WinnerIsSelfWithAllySubCategoryInCombatArea」：本方未获胜（P{source.ControllerId} vs 胜者 P{triggeringPlayerId}）→ 不满足");
                        return false;
                    }
                    string wantSub = (param ?? "").Trim();
                    foreach (var c in CM.GetAllInstances())
                    {
                        if (c == null || c.IsDying || c == source) continue;
                        if (c.ControllerId != source.ControllerId) continue;                  // 只数"本方"
                        if (c.CurrentZone != CardZone.AreaZone) continue;                     // 场上角色
                        if (!string.Equals(c.ZoneDetailId ?? "", triggeringAreaId, System.StringComparison.Ordinal)) continue;   // 位于战斗地区
                        if ((c.PlayerTemplate?.CategoryFlag & CardCategory.Character) == 0) continue;
                        if (!string.IsNullOrEmpty(wantSub) && !c.HasSubCategory(wantSub)) continue;
                        GD.Print($"[EffectEngine] 条件「WinnerIsSelfWithAllySubCategoryInCombatArea({wantSub})」：找到 {c.PlayerTemplate?.CardName} 位于战斗地区 → 满足");
                        return true;
                    }
                    GD.Print($"[EffectEngine] 条件「WinnerIsSelfWithAllySubCategoryInCombatArea({wantSub})」：战斗地区内无符合条件的本方角色 → 不满足");
                    return false;
                }

                // 【P3·K5】参数化谓词：**指定地区内的卡上标志总数 ≥ N**（JC127「然后若该地区有三个或更多时间标志」）。
                // `param` 形式：`"标志名,数量"`（如 `"时间标志,3"`；兼容 `"时间标志"`＝至少 1 个）。
                // 地区＝`triggeringAreaId`（打出/激活类效果在结算时以"目标地区"下发，见 `ResolveEffect` 的 targetAreaId）。
                // 统计口径：**该地区内所有卡**（争夺区角色 ＋ 结附该地区的附属，即 `ZoneDetailId == 地区`）上的该标志之和。
                case "AreaMarkerAtLeast":                {
                    // 地区来源：优先本次结算的"目标地区"，否则回落到**来源卡所在地区**
                    // （如 JC048 诅咒沙漏结附于地区 → 来源卡 ZoneDetailId 即该地区；准备步骤触发不带地区上下文）。
                    string areaUid = !string.IsNullOrEmpty(triggeringAreaId) ? triggeringAreaId : (source?.ZoneDetailId ?? "");
                    if (string.IsNullOrEmpty(areaUid) || CM == null)
                    {
                        GD.Print("[EffectEngine] 条件「AreaMarkerAtLeast」：无目标地区且来源无地区 → 不满足");
                        return false;
                    }
                    string markerName = "时间标志";
                    int need = 1;
                    if (!string.IsNullOrWhiteSpace(param))
                    {
                        var parts = param.Split(',');
                        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])) markerName = parts[0].Trim();
                        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int parsedNeed)) need = parsedNeed;
                    }
                    // 统计走统一实现（与动态值 `计数:地区内卡上标志:…` 同源，避免两处口径分叉）
                    int total = Script.Manager.EffectValueResolver.CountMarkersInArea(areaUid, markerName, CM);
                    bool ok = total >= need;
                    GD.Print($"[EffectEngine] 条件「AreaMarkerAtLeast({markerName} ≥ {need})」：地区 {areaUid} 实际 {total} → {(ok ? "满足" : "不满足")}");
                    return ok;
                }

                // 【秘社批次·MSJC015】"若你的**手牌数量大于或等于**四张"：
                // `param` ＝ 门槛（缺省 4）。判定基准＝**来源控制者**的手牌数（秘社能力由控制者发动）。
                case "HandAtLeast":
                {
                    int needHand = 4;
                    if (!string.IsNullOrWhiteSpace(param) && int.TryParse(param.Trim(), out int parsedHand)) needHand = parsedHand;
                    int owner = (source ?? target)?.ControllerId ?? -1;
                    if (owner <= 0 || CM == null)
                    {
                        GD.Print("[EffectEngine] 条件「HandAtLeast」：来源控制者缺失 → 不满足（fail-closed）");
                        return false;
                    }
                    int have = CM.GetHandCardsByOwner(owner).Count;
                    bool ok = have >= needHand;
                    GD.Print($"[EffectEngine] 条件「HandAtLeast({needHand})」：玩家{owner} 手牌 {have} 张 → {(ok ? "满足" : "不满足")}");
                    return ok;
                }

                // 语义：死亡的卡是**本方灵体角色**（JC105"当另一个本方灵体角色死亡时"）。
                // 判据与 `DiedAllyCharacter` 同源（角色 + 同控制者 + **排除来源自身**），外加关键词「灵体」
                // （与光环筛选同一口径：运行期关键字 ∪ 卡面固有）。
                case "DiedAllySpiritCharacter":
                {
                    if (source == null || contextCard == null || contextCard == source) return false;
                    bool isChar = (contextCard.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0;
                    bool ally = contextCard.ControllerId == source.ControllerId;
                    bool spirit = contextCard.HasKeyword("灵体")
                                  || (contextCard.PlayerTemplate?.HasInherentKeyword("灵体") ?? false);
                    bool ok = isChar && ally && spirit;
                    GD.Print($"[EffectEngine] 条件「DiedAllySpiritCharacter」：死亡卡={contextCard.PlayerTemplate?.CardName} " +
                             $"是否角色={isChar} 同控制者={ally}（P{contextCard.ControllerId}/P{source.ControllerId}）" +
                             $" 是否灵体={spirit} → {(ok ? "满足" : "不满足")}");
                    return ok;
                }
                // 语义：死亡的卡是**吸血鬼角色**——判据＝"当前实际子类别"含「吸血鬼」
                // （与既有 `TargetRestriction.CurrentSubCategory` + Param="吸血鬼" 同一口径：
                //  模板固有子类别 ∪ 运行期授予的子类别，如 JC030 授予的吸血鬼）。
                // 用途：JC031 唤灵师的分支条件（"若死亡的是吸血鬼角色，则改为随机弃牌"）。
                case "DiedVampireCharacter":
                {
                    if (contextCard == null) return false;
                    bool isChar = (contextCard.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0;
                    bool vampire = isChar && contextCard.HasSubCategory("吸血鬼");
                    GD.Print($"[EffectEngine] 条件「DiedVampireCharacter」：死亡卡={contextCard.PlayerTemplate?.CardName} " +
                             $"是否角色={isChar} 是否吸血鬼={vampire} → {(vampire ? "满足" : "不满足")}");
                    return vampire;
                }
                // 【JC124】语义：**刚死亡的那张卡在死亡瞬间具有指定"卡上标志/指示物"**
                // （卡文"当**具有哲人石标志的**角色死亡时"）。`Param` ＝标志名（如 `哲人石标志`）。
                // 判据读**死亡瞬间的计数器快照**（`CardInstance.CountersAtDeath`，由 `CardManager.ResolveDeath`
                // 在广播死亡事件**之前**抓取）——因为死亡触发的**结算**晚于"进墓地"，而进墓地会清空计数器；
                // 无快照时回落到当前计数器（同步路径/未走死亡流程的手工结算场景）。
                case "DiedWithCounter":
                {
                    var died = contextCard ?? target;
                    if (died == null)
                    {
                        GD.Print("[EffectEngine] 条件「DiedWithCounter」：无死亡上下文卡 → 不满足");
                        return false;
                    }
                    bool ok = died.HadCounterAtDeath(param);
                    GD.Print($"[EffectEngine] 条件「DiedWithCounter({param})」：死亡卡={died.PlayerTemplate?.CardName} " +
                             $"→ {(ok ? "满足" : "不满足")}");
                    return ok;
                }
                // ==================== 【P3】回合内事件记忆位谓词 ====================
                // 语义："本回合中，**敌方**势力标志曾被移除"（JC053 鸣钟教派徽记的激活前提）。
                // 记忆位由 `AreaManager.RemovePowerMark` 在**真实移除 >0 个**时置位（遏制/移除皆算），
                // 在**回合开始的重置步骤**清空（"本回合"粒度）。
                // 敌我基准＝来源卡控制者（与其它条件谓词一致）。
                case "EnemyPowerMarkRemovedThisTurn":
                {
                    if (source == null) { GD.Print("[EffectEngine] 条件「EnemyPowerMarkRemovedThisTurn」无来源卡，不满足"); return false; }
                    var am = TurnManagerRef?.AreaManagerRef;
                    if (am == null) { GD.Print("[EffectEngine] 条件「EnemyPowerMarkRemovedThisTurn」无 AreaManager，不满足"); return false; }
                    bool met = false;
                    foreach (int pid in PlayerTargetPool.AllPlayers(TurnManagerRef?.PlayerManagerRef))
                    {
                        if (pid == source.ControllerId) continue;
                        if (am.HasPowerMarkRemovedThisTurn(pid)) { met = true; break; }
                    }
                    GD.Print($"[EffectEngine] 条件「EnemyPowerMarkRemovedThisTurn」：来源控制者=P{source.ControllerId} → {(met ? "满足" : "不满足")}");
                    return met;
                }
                // ==================== 【P2】弃牌类谓词 ====================
                // 语义：弃牌者是"敌方玩家"（≠ 本效果来源卡的控制者）。用于 JC040「每当敌方玩家弃牌时…」。
                // 弃牌者经 TriggerContext.TriggeringPlayerId 透传（= TurnManager.DiscardCards 的 discarderId）。
                case "DiscardByEnemy":
                {
                    if (source == null) return false;
                    if (triggeringPlayerId < 0)
                    {
                        GD.Print("[EffectEngine] 条件「DiscardByEnemy」无弃牌者信息（非弃牌路径），不满足");
                        return false;
                    }
                    bool byEnemy = triggeringPlayerId != source.ControllerId;
                    GD.Print($"[EffectEngine] 条件「DiscardByEnemy」：弃牌者=P{triggeringPlayerId} 来源控制者=P{source.ControllerId} → {(byEnemy ? "满足" : "不满足")}");
                    return byEnemy;
                }
                // ==================== 【P3】声望（势力对抗结束） ====================
                // 语义（用户规则文本）：每个势力对抗结束后，由该地区中"拥有较多声望角色的一方"触发。
                // 判定：本卡控制者在该地区的**声望角色数** 严格多于 对手在该地区的声望角色数 → 满足。
                // 口径：声望角色不要求未横置、不要求参与该次对抗（只要在该地区）；双方相等/均为 0 → 不满足。
                // 地区 = 本次势力对抗所在的地区（TriggerContext.TargetAreaId → triggeringAreaId）。
                case "RenownMajority":
                {
                    if (source == null) return false;
                    if (string.IsNullOrEmpty(triggeringAreaId))
                    {
                        GD.Print("[EffectEngine] 条件「RenownMajority」无对抗地区（非势力对抗路径），不满足");
                        return false;
                    }
                    int mine = RenownRule.CountRenownCharacters(ZoneManagerRef, triggeringAreaId, source.ControllerId);
                    int opp = RenownRule.CountRenownCharacters(ZoneManagerRef, triggeringAreaId, source.ControllerId == 1 ? 2 : 1);
                    bool majority = mine > 0 && mine > opp;
                    GD.Print($"[EffectEngine] 条件「RenownMajority」：地区{triggeringAreaId} 我方声望角色={mine}，对手={opp} → {(majority ? "满足" : "不满足")}");
                    return majority;
                }
                default:
                    GD.Print($"[EffectEngine] 未知条件「{predicate}」暂默认满足");
                    return true;
            }
        }

        // ==================== 持续效果注册表 ====================

        /// <summary>
        /// 注册一张卡的所有持续效果（卡进入相关区域时调用，P1 实现）。
        // ==================== 持久效果监听集（D19-②：带前提的持续效果登记，前提变化时重判） ====================
        // 记录"因前提未满足而未生效"的卡引用，待相关区域（资产区/忠诚）变化时由 ReevaluatePrereqs 重算。
        private readonly System.Collections.Generic.HashSet<CardInstance> _pendingPrereqCards
            = new System.Collections.Generic.HashSet<CardInstance>();

        // ==================== 全局封锁秘密派遣表（JC068 阿格斯IV） ====================
        // 记录"被禁止秘密派遣行动的玩家ID"（即未横置阿格斯IV控制者的对手）。
        // 由带 BlockDeployCovert 且前提（自身未横置）满足的持续效果在 RegisterPersistent / ReevaluatePrereqs 时登记；
        // 前提丧失或离场时移除。TurnManager.DeployCovert / CanDeployCovert 经 IsDeployCovertBlocked 实时查询。
        private readonly System.Collections.Generic.HashSet<int> _blockDeployCovertPlayers
            = new System.Collections.Generic.HashSet<int>();

        /// <summary>某玩家当前是否被"未横置阿格斯IV"封锁而禁止秘密派遣行动（JC068）。</summary>
        public bool IsDeployCovertBlocked(int playerId)
            => _blockDeployCovertPlayers.Contains(playerId);

        // ==================== 地区级"略过对抗"表（JC098 陷入疯狂的人群，K18 第一批） ====================
        // 记录"哪些卡声明了『本卡所在地区略过某类对抗』"。查询时**按卡当前所在地**判定（不做地区 UID 快照），
        // 因此卡在争夺区之间移动时无需重新登记即可自动跟随；卡离场/前提丧失时注销。
        private sealed class AreaCombatSkip
        {
            public string CardUid;
            public readonly System.Collections.Generic.HashSet<string> CombatNames
                = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        }
        private readonly System.Collections.Generic.List<AreaCombatSkip> _areaCombatSkips
            = new System.Collections.Generic.List<AreaCombatSkip>();

        /// <summary>【P3·K18】该地区是否**略过**指定对抗（`combatName` = `"调查"`/`"战斗"`/`"势力"`）。
        /// 两个来源**并联**：
        ///  ① 常驻静止规则（JC098 类：来源卡在争夺区期间由其所在地区持续生效）；
        ///  ② **本回合时效登记**（JC118 停战协议：打出时对"目标地区"登记，回合结束自动失效）。
        /// 消费点：`TurnManager.ResolveCurrentAreaCombat`（跳过不结算对抗，但仍给优先权窗口）。</summary>
        public bool AreaSkipsCombat(string areaUid, string combatName)
        {
            if (string.IsNullOrEmpty(areaUid) || string.IsNullOrEmpty(combatName)) return false;
            // ② 本回合时效：TargetUid 存**地区 UID**，Value 存"调查,战斗,势力"清单
            foreach (var m in _timedModStack)
            {
                if (m.Kind != "AreaCombatSkip") continue;
                if (!string.Equals(m.TargetUid, areaUid, System.StringComparison.Ordinal)) continue;
                foreach (var name in (m.Value ?? "").Split(','))
                    if (string.Equals(name.Trim(), combatName, System.StringComparison.Ordinal)) return true;
            }
            // ① 常驻静止规则
            foreach (var s in _areaCombatSkips)
            {
                if (!s.CombatNames.Contains(combatName)) continue;
                var src = CM?.GetInstance(s.CardUid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone) continue;          // 只在争夺区生效
                if (!string.Equals(src.ZoneDetailId, areaUid, System.StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>【P3·K18】登记"**本回合**该地区略过指定对抗"（JC118）：`combatNames` 空 → 三步骤全部略过。
        /// 时效载体＝`TimedMod{Kind="AreaCombatSkip", TargetUid=地区UID}`，回合结束由 `ExpireTurnEndEffects` 出栈；
        /// 同地区同清单重复登记幂等（不叠加）。</summary>
        public void RegisterAreaCombatSkipThisTurn(string areaUid, System.Collections.Generic.List<string> combatNames)
        {
            if (string.IsNullOrEmpty(areaUid)) { GD.Print("[EffectEngine] ⚠ SkipAreaCombat 跳过：未指定地区"); return; }
            var names = (combatNames == null || combatNames.Count == 0)
                ? new System.Collections.Generic.List<string> { "调查", "战斗", "势力" }
                : combatNames;
            string value = string.Join(",", names);
            foreach (var m in _timedModStack)
                if (m.Kind == "AreaCombatSkip"
                    && string.Equals(m.TargetUid, areaUid, System.StringComparison.Ordinal)
                    && string.Equals(m.Value ?? "", value, System.StringComparison.Ordinal))
                {
                    GD.Print($"[EffectEngine] ✓ 地区略过对抗（本回合）已登记：{areaUid} [{value}]（不重复）");
                    return;
                }
            RegisterTimedMod(new TimedMod
            {
                TargetUid = areaUid,          // 地区 UID（不是卡 UID）
                Kind = "AreaCombatSkip",
                Value = value,
                Amount = 1,
                ExpireTurn = NowTurn,
            });
            GD.Print($"[EffectEngine] ✓ 地区略过对抗（本回合）：{areaUid} 略过[{value}]");
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>登记/注销一张卡的"地区略过对抗"静止规则（`active=false` 时按卡 UID 清除）。</summary>
        private void SetAreaCombatSkip(CardInstance card, System.Collections.Generic.List<string> names, bool active)
        {
            if (card == null) return;
            for (int i = _areaCombatSkips.Count - 1; i >= 0; i--)
                if (string.Equals(_areaCombatSkips[i].CardUid, card.UID, System.StringComparison.Ordinal))
                    _areaCombatSkips.RemoveAt(i);
            if (!active || names == null || names.Count == 0) return;
            var rec = new AreaCombatSkip { CardUid = card.UID };
            foreach (var n in names)
                if (!string.IsNullOrWhiteSpace(n)) rec.CombatNames.Add(n.Trim());
            _areaCombatSkips.Add(rec);
            GD.Print($"[EffectEngine] ⊘ 地区略过对抗登记：{card.PlayerTemplate?.CardName} → 略过[{string.Join(",", rec.CombatNames)}]（实时按所在地区判定）");
        }

        /// <summary>该持续效果是否携带"地区略过对抗"静止标记；返回其对抗名清单（无则 null）。</summary>
        private static System.Collections.Generic.List<string> GetAreaCombatSkips(EffectData e)
        {
            if (e?.StaticEffects == null) return null;
            System.Collections.Generic.List<string> names = null;
            foreach (var s in e.StaticEffects)
            {
                if (s == null || s.Kind != StaticKind.SkipAreaCombat) continue;
                names ??= new System.Collections.Generic.List<string>();
                foreach (var part in (s.Param ?? "").Split(','))
                    if (!string.IsNullOrWhiteSpace(part)) names.Add(part.Trim());
            }
            return names;
        }

        // ==================== 地区级"敌方暗藏者不能参与势力对抗"表（JC023 范·弥尔顿） ====================
        // 记录"哪些卡声明了『本卡所在地区的敌方暗藏者不参与势力对抗』"。查询按卡**当前**所在地实时判定，
        // 与 `_areaCombatSkips` 同一套生命周期（前提满足登记 / 离场、前提丧失、卡失效时注销）。
        private readonly System.Collections.Generic.List<string> _covertPowerExcludedBy
            = new System.Collections.Generic.List<string>();

        /// <summary>【P3·K3】**地区赢取时的去处替代**查询（JC079 圣婴）：返回该卡在该次赢取中应去的区域名
        /// （`"计分区"`），无改判则返回空串（走原路径＝送拥有者牌库底）。
        /// 生效条件＝效果声明的谓词在"赢取地区"上下文下成立（缺省 `WinnerIsSelfInSameArea`），
        /// 判定与触发路径共用 `EvaluateCondition`（单一谓词实现）。</summary>
        public string ResolveWinAreaDestination(CardInstance card, string areaUid, int winnerId)
        {
            if (card == null) return "";
            foreach (var e in EffectsOf(card))
            {
                if (e == null || string.IsNullOrEmpty(e.WinAreaDestination)) continue;
                string predicate = string.IsNullOrEmpty(e.Condition?.Predicate)
                    ? "WinnerIsSelfInSameArea"
                    : e.Condition.Predicate;
                if (EvaluateCondition(predicate, card, card, card, winnerId, areaUid))
                {
                    GD.Print($"[EffectEngine] ⇄ 赢取地区去处替代：{card.PlayerTemplate?.CardName} → {e.WinAreaDestination}" +
                             $"（地区 {areaUid}，赢取者 P{winnerId}）");
                    return e.WinAreaDestination;
                }
            }
            return "";
        }

        /// <summary>【P3·K22】"牺牲一个角色"作代价的**候选**（口径见 `SacrificeCostSpec`）。
        /// 候选＝**发动者自己控制**、在争夺区正面（除非 `AllowCovert` 明确允许暗藏者）、未在死亡流程中、
        /// 且满足子类别白名单 / 本地区限定 / 排除来源自身 / 已横置要求的角色。
        /// 顺序确定性：先本方争夺区卡的场上顺序（与其它候选枚举一致）。</summary>
        public System.Collections.Generic.List<CardInstance> GetSacrificeCandidates(CardInstance source, SacrificeCostSpec spec)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            if (source == null || spec == null) return result;
            var zm = ZoneManagerRef;
            if (zm == null) return result;
            var all = zm.GetZoneCards(CardZone.AreaZone, source.ControllerId)
                ?? new System.Collections.Generic.List<CardInstance>();
            foreach (var c in all)
            {
                if (IsLegalSacrifice(source, spec, c)) result.Add(c);
            }
            return result;
        }

        /// <summary>【P3·K22】单卡是否可作为"牺牲一个角色"代价（与候选侧**同一实现**，权威侧复检同源）。</summary>
        public bool IsLegalSacrifice(CardInstance source, SacrificeCostSpec spec, CardInstance candidate)
        {
            if (source == null || spec == null || candidate == null) return false;
            if (candidate.IsDying) return false;
            if (candidate.CurrentZone != CardZone.AreaZone) return false;          // 代价来源＝争夺区
            if (candidate.ControllerId != source.ControllerId) return false;       // 只能牺牲自己的角色
            if (spec.ExcludeSource && ReferenceEquals(candidate, source)) return false;
            if (spec.LocalZone && candidate.ZoneDetailId != source.ZoneDetailId) return false;
            // 暗藏者：口径＝除非卡文明确写"暗藏者"，否则不能牺牲（暗藏者本就不是"角色"的正面状态）
            if (candidate.IsCovert && !spec.AllowCovert) return false;
            // 角色判定：模板类别含角色（暗藏者也可能是角色牌，故上面已单独按 AllowCovert 把关）
            var tmpl = candidate.PlayerTemplate;
            if (tmpl == null || (tmpl.CategoryFlag & CardCategory.Character) == 0) return false;
            if (spec.RequireTapped && !candidate.IsTapped) return false;
            // 子类别白名单（空＝不限）：打印子类别 ∪ 运行期获得的子类别，命中其一即可
            if (spec.SubCategories != null && spec.SubCategories.Count > 0)
            {
                bool hit = false;
                foreach (var sub in spec.SubCategories)
                {
                    if (string.IsNullOrWhiteSpace(sub)) continue;
                    if (candidate.HasSubCategory(sub.Trim())) { hit = true; break; }
                }
                if (!hit) return false;
            }
            return true;
        }

        /// <summary>【P3·K4】该卡**模板上印刷**了指定静止种类（不看运行期登记）。
        /// 用途：像"不能移动"（JC121）这类**前缀印刷规则**——它在卡片**未结附**（位于争夺区）时就该生效，
        /// 但附属卡的持久效果注册有 Z1 早退（"附属不在附属区 → 跳过持久效果注册"），
        /// 运行期旗标在该状态下拿不到，故此类规则按**模板印刷**判定。</summary>
        public bool HasPrintedStaticKind(CardInstance card, StaticKind kind)
        {
            var list = card?.PlayerTemplate?.EffectDataList;
            if (list == null) return false;
            foreach (var e in list)
            {
                if (e?.StaticEffects == null) continue;
                foreach (var s in e.StaticEffects)
                    if (s != null && s.Kind == kind) return true;
            }
            return false;
        }

        /// <summary>【P3·K22】"由指定玩家自己选择牺牲"（多玩家/对手侧；用户口径 A11/A12）：
        ///  · **每位玩家**＝按**先手优先**顺序逐个询问（先选者先记录、后选者可见；View 侧高亮由 View 负责），
        ///    全部选定后**一起牺牲**（同一批次，不分死亡先后）；
        ///  · **目标玩家/对手**＝只询问该玩家（K24 玩家目标，`_currentTargetPlayerId`）；
        ///  · **无合法候选的玩家跳过、什么都不付出**；牺牲＝死亡 → 照常广播死亡时机。
        /// 候选：种类（角色＝场上正面角色 / 暗藏者＝场上背面暗藏者 / 附属＝该玩家场上附属 / 资产＝其资产区卡），
        /// 地区限定（`本地区`＝来源卡所在地区；`目标地区`＝本次结算目标地区）。</summary>
        private void ResolveSacrificeChoice(EffectAction action, CardInstance source, string targetAreaId)
        {
            if (CM == null) { GD.Print("[EffectEngine] ⚠ SacrificeChoice 跳过：CardManager 未注入"); return; }
            string kind = ArgAt(action, 0, "角色");
            string who = ArgAt(action, 1, "每位玩家");
            string areaScope = ArgAt(action, 2, "任意");
            string areaUid = areaScope == "目标地区" ? (targetAreaId ?? "") : (source?.ZoneDetailId ?? "");

            // 需要做选择的玩家（顺序：先手优先 → 后手；单玩家情形只有该玩家）
            var choosers = new System.Collections.Generic.List<int>();
            if (who == "每位玩家")
            {
                int first = TurnManagerRef?.FirstPlayerId ?? 1;
                choosers.Add(first);
                choosers.Add(first == 1 ? 2 : 1);
            }
            else if (who == "自己") { if (source != null) choosers.Add(source.ControllerId); }
            else
            {
                int target = _currentTargetPlayerId > 0 ? _currentTargetPlayerId
                    : (source != null ? (source.ControllerId == 1 ? 2 : 1) : -1);
                if (target > 0) choosers.Add(target);
            }

            var picked = new System.Collections.Generic.List<CardInstance>();
            // 【P0·异步窗口修复】选卡窗是**回调式异步**窗口（`Arena.PromptPickCard` 打开窗口后立即返回，
            // 玩家点击时才回调）。原实现在发起询问后**同一帧内**就执行 `chosen ??= candidates[0]` 并立刻
            // `ResolveDeath` ⇒ 单机下选择窗刚弹出、第一候选就已经被牺牲，玩家的点击完全无效
            // （现有单测用**同步桩**，因此长期掩盖了该缺陷）。
            // 修法：改为"逐位收集 + 全部收齐后才落地牺牲"的回调递归；若第一次询问确实是异步的
            // （回调未同步触发），则**挂起动作链** `SuspendChainForInput()`，在最终回调里
            // `ContinuePendingChain()` 续算后续动作（与 PayOptional/封印/检索同一套机制）。
            var state = new SacrificePickState();
            CollectSacrificePicks(choosers, 0, kind, areaScope, areaUid, picked, state, () =>
            {
                if (picked.Count == 0) { GD.Print("[EffectEngine] SacrificeChoice：无人可选 → 无效果"); }
                else
                {
                    // 全部选定后**一起牺牲**（原子锁期间不区分先后）
                    foreach (var card in picked)
                        CM.ResolveDeath(card, CardManager.DeathCause.Sacrifice);
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                }
                if (state.ChainSuspended && !state.ResumeByGateway) ContinuePendingChain();   // 只在此处续算一次（见上方说明）
            });
        }

        /// <summary>【P0】牺牲选择收集期的状态（引用持有，供闭包读写）。</summary>
        private sealed class SacrificePickState
        {
            public bool ChainSuspended;
            /// <summary>【A55】本次收集走的是联机网关流程 ⇒ 续算由 `BeginGatewayFlow` 统一负责，
            /// `onAllDone` 不得再自行 `ContinuePendingChain()`（否则会重复续算/丢链）。</summary>
            public bool ResumeByGateway;
        }

        /// <summary>
        /// 【P0】逐位收集"由该玩家自己选择牺牲的对象"（回调递归）：
        ///  · 有 UI → `PromptPickCard`（不可取消）；**回调同步触发**（测试桩/无窗口兜底）→ 在回调内继续递归；
        ///    **回调异步**（真实窗口）→ 挂起动作链并返回，等玩家点击后从回调继续；
        ///  · 无 UI（headless/测试）→ 确定性兜底取第一个候选；
        ///  · 无合法候选的玩家直接跳过（什么都不付出，口径 A11）；
        ///  · 全部收齐后调用 `onAllDone`（落地牺牲 + 续算动作链）。
        /// </summary>
        private void CollectSacrificePicks(System.Collections.Generic.List<int> choosers, int index,
            string kind, string areaScope, string areaUid,
            System.Collections.Generic.List<CardInstance> picked, SacrificePickState state,
            System.Action onAllDone)
        {
            for (int i = index; i < choosers.Count; i++)
            {
                int playerId = choosers[i];
                var candidates = CollectSacrificeChoiceCandidates(playerId, kind, areaScope, areaUid);
                if (candidates.Count == 0)
                {
                    GD.Print($"[EffectEngine] SacrificeChoice：玩家{playerId} 无合法候选 → 跳过（什么都不付出）");
                    continue;
                }
                if (UIHooks != null)
                {
                    bool firedSynchronously = false;
                    UIHooks.PromptPickCard(playerId,
                        $"{KindName(kind)}：请选择你要牺牲的{KindName(kind)}（{candidates.Count} 个候选）",
                        candidates, allowCancel: false,
                        p =>
                        {
                            firedSynchronously = true;
                            var chosen = p ?? candidates[0];
                            picked.Add(chosen);
                            GD.Print($"[EffectEngine] SacrificeChoice：玩家{playerId} 选定牺牲 {chosen.PlayerTemplate?.CardName}");
                            // 继续收集下一位（可能再开一个窗口）；全部收齐后 onAllDone 落地
                            CollectSacrificePicks(choosers, i + 1, kind, areaScope, areaUid, picked, state, onAllDone);
                        });
                    // 同步回调（桩/兜底）：上面的递归已把剩下的人全部处理完（含 onAllDone）→ 直接返回
                    if (firedSynchronously) return;
                    // 异步回调（真实窗口）：挂起动作链，等玩家点击；后续由回调链续跑
                    if (!state.ChainSuspended)
                    {
                        GD.Print($"[EffectEngine] SacrificeChoice：等待玩家{playerId} 在窗口中选择（动作链挂起）");
                        SuspendChainForInput();
                        state.ChainSuspended = true;
                    }
                    return;
                }
                // 【A55】联机：逐位向该玩家下发 `SelectTarget`（顺序与单机一致：先手优先），全部收齐后 onAllDone。
                // 挂起-续算由 `BeginGatewayFlow` 统一负责 ⇒ 告知 onAllDone 不要再自行续算。
                if (BeginGatewayFlow(() => CollectSacrificePicksViaGatewayAsync(
                        choosers, i, kind, areaScope, areaUid, picked, state, onAllDone)))
                {
                    state.ResumeByGateway = true;
                    return;
                }
                var fallback = candidates[0];   // 无 UI/网关（headless/测试）：确定性兜底
                picked.Add(fallback);
                GD.Print($"[EffectEngine] SacrificeChoice：无 UI/网关 → 玩家{playerId} 确定性兜底选择 {fallback.PlayerTemplate?.CardName}");
            }
            onAllDone();
        }

        /// <summary>
        /// 【A55】联机的逐位牺牲选择：从第 <paramref name="startIndex"/> 位开始，对每位"有合法候选"的玩家
        /// 依次下发 `Kind.SelectTarget`（不可取消），回包**权威复检**（必须命中本次下发的候选），
        /// 非法/取消/超时 → 确定性兜底取第一个候选（与单机无 UI 同口径）。全部收齐后调用 `onAllDone()`。
        /// </summary>
        private async System.Threading.Tasks.Task CollectSacrificePicksViaGatewayAsync(
            System.Collections.Generic.List<int> choosers, int startIndex, string kind, string areaScope, string areaUid,
            System.Collections.Generic.List<CardInstance> picked, SacrificePickState state, System.Action onAllDone)
        {
            for (int i = startIndex; i < choosers.Count; i++)
            {
                int playerId = choosers[i];
                var candidates = CollectSacrificeChoiceCandidates(playerId, kind, areaScope, areaUid);
                if (candidates.Count == 0)
                {
                    GD.Print($"[EffectEngine] SacrificeChoice（联机）：玩家{playerId} 无合法候选 → 跳过（什么都不付出）");
                    continue;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
                    Prompt = $"{KindName(kind)}：请选择你要牺牲的{KindName(kind)}（{candidates.Count} 个候选）",
                    ForceChoice = true,
                };
                foreach (var c in candidates)
                {
                    req.OptionUids.Add(c.UID);
                    req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.CouncilTemplate?.CardName ?? c.UID);
                }
                Script.Net.ChoiceResult res = null;
                try { res = await InputGateway.RequestChoice(playerId, req); }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"[EffectEngine] ⚠ SacrificeChoice 联机请求异常：{ex.GetType().Name}: {ex.Message}（按确定性兜底）");
                }
                CardInstance chosen = null;
                if (res != null && !res.Cancelled && !string.IsNullOrEmpty(res.SelectedUid))
                    chosen = candidates.Find(c => string.Equals(c.UID, res.SelectedUid, System.StringComparison.Ordinal));
                if (chosen == null)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ SacrificeChoice 回包『{res?.SelectedUid}』不在本次合法候选中（或取消）" +
                                " → 确定性兜底取第一个候选");
                    chosen = candidates[0];
                }
                picked.Add(chosen);
                GD.Print($"[EffectEngine] SacrificeChoice（联机）：玩家{playerId} 选定牺牲 {chosen.PlayerTemplate?.CardName}");
            }
            onAllDone();
        }

        private static string KindName(string kind) => kind switch
        {
            "附属" => "附属",
            "资产" => "资产",
            "暗藏者" => "暗藏者",
            _ => "角色",
        };

        private System.Collections.Generic.List<CardInstance> CollectSacrificeChoiceCandidates(
            int playerId, string kind, string areaScope, string areaUid)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            if (CM == null || playerId <= 0) return result;
            bool limitArea = areaScope != "任意";
            foreach (var c in CM.GetAllInstances())
            {
                if (c == null || c.IsDying) continue;
                if (c.ControllerId != playerId) continue;
                bool ok = kind switch
                {
                    "资产" => c.CurrentZone == CardZone.AssetZone,
                    "附属" => c.CurrentZone == CardZone.AttachedZone || (c.CurrentZone == CardZone.AreaZone && c.IsAttached),
                    "暗藏者" => c.CurrentZone == CardZone.AreaZone && c.IsCovert,
                    _ => c.CurrentZone == CardZone.AreaZone && !c.IsCovert
                         && (c.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0,
                };
                if (!ok) continue;
                if (limitArea && !string.Equals(c.ZoneDetailId ?? "", areaUid ?? "", System.StringComparison.Ordinal)) continue;
                result.Add(c);
            }
            return result;
        }

        /// <summary>【P3·K22】"自本方卡牌上移除 N 个（时间）标志"作**发动代价**的**候选**（JC044）。
        /// 候选＝**该发动者控制**、**在场上**（争夺区 ∪ 附属区）、带指定标志（白名单任一写法，合计 ≥ `Amount`）的卡；
        /// 顺序确定性＝`GetAllInstances()` 顺序（与其它候选枚举一致）。见 `CounterCostSpec`。</summary>
        public System.Collections.Generic.List<CardInstance> GetCounterCostCandidates(CardInstance source, CounterCostSpec spec)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            if (source == null || spec == null || CM == null) return result;
            // 【JC055②】"移除 X 个"（X 由玩家选择）→ 候选＝带该标志 ≥1 的卡（上限另由总量约束）
            int need = spec.AmountByPlayerChoice ? 1 : (spec.Amount > 0 ? spec.Amount : 1);
            foreach (var c in CM.GetAllInstances())
            {
                if (c == null || c.IsDying) continue;
                if (c.ControllerId != source.ControllerId) continue;                 // 只数"本方卡牌"
                if (c.CurrentZone != CardZone.AreaZone && c.CurrentZone != CardZone.AttachedZone) continue;   // 场上
                if (CountMarkers(c, spec.Markers) < need) continue;
                result.Add(c);
            }
            return result;
        }

        /// <summary>【P3·K4】`FormCategory` → 实例类别位（`None`＝不改类别）。</summary>
        private static CardCategory ToCardCategory(FormCategory form) => form switch
        {
            FormCategory.Character => CardCategory.Character,
            FormCategory.Attachment => CardCategory.Attachment,
            FormCategory.CharacterOrAttachment => CardCategory.Character | CardCategory.Attachment,
            _ => CardCategory.None,
        };

        /// <summary>
        /// 【P3·K4】**该卡当前实际使用的效果表**：实例形态声明了"损失原有效果"（JC028「鲜血」）时用**形态效果表**，
        /// 否则用模板印刷效果表。所有"按卡取效果"的地方都应经此方法（注册/可发动/触发收集/注销），
        /// 否则形态卡会继续使用印刷效果（"损失了原有的效果"落不了地）。
        /// </summary>
        public static System.Collections.Generic.List<EffectData> EffectsOf(CardInstance card)
            => card?.EffectiveEffects ?? new System.Collections.Generic.List<EffectData>();

        /// <summary>该卡上"白名单里任一写法"的标志总数（用于代价候选判定与支付）。</summary>
        private static int CountMarkers(CardInstance card, Array<string> markers)
        {
            if (card == null || markers == null) return 0;
            int total = 0;
            foreach (var name in markers)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (card.Counters != null && card.Counters.TryGetValue(name.Trim(), out int n) && n > 0) total += n;
            }
            return total;
        }

        /// <summary>【JC055②】该卡上可作代价的标志数（View 显示"可移除 k"用；与支付实现同源）。</summary>
        public int GetMarkerCountOnCard(CardInstance card, CounterCostSpec spec)
            => CountMarkers(card, spec?.Markers);

        /// <summary>【JC055②】本方场上该标志的**总数**（＝玩家可选择的数量上限）。</summary>
        public int GetCounterCostAvailableTotal(CardInstance source, CounterCostSpec spec)
        {
            int total = 0;
            if (source == null || spec == null) return 0;
            foreach (var c in GetCounterCostCandidates(source, spec)) total += CountMarkers(c, spec.Markers);
            return total;
        }

        /// <summary>
        /// 【P3·K5·JC055②】支付"**移除 X 个标志**"代价（X 由玩家逐卡分配；用户口径 A30）：
        /// `allocation` ＝ 卡 UID → 从该卡移除的数量；校验（fail-closed，任一不过即**拒绝且无副作用**）：
        ///  · 每张卡都必须是**合法候选**（本方场上、带该标志）、数量 ≥ 0 且 ≤ 该卡实际持有量；
        ///  · 总和 **≥ 1**（"不允许为 0"＝至少移除一个）、≤ 本方总数；
        ///  · 未提供/空分配 → 视为不支付 → 拒绝（本能力必须以"移除至少 1 个"发动）。
        /// 成功时返回 true 并给出 `paidX`（＝总和，供 `ValueExpr="代价数量"` 读取）。
        /// </summary>
        public bool PayCounterCostAllocation(CardInstance source, EffectData data,
            System.Collections.Generic.Dictionary<string, int> allocation, out int paidX)
        {
            paidX = 0;
            var spec = data?.CounterCost;
            if (spec == null) return true;                 // 无此代价
            if (source == null || CM == null) return false;
            var candidates = GetCounterCostCandidates(source, spec);
            if (candidates.Count == 0)
            {
                GD.Print($"[EffectEngine] ⛔ 标志代价无候选（{data.EffectId}：本方场上无带该标志的卡）→ 拒绝发动");
                return false;
            }
            if (allocation == null || allocation.Count == 0)
            {
                GD.Print($"[EffectEngine] ⛔ 标志代价未分配（{data.EffectId}：X 至少为 1，不允许为 0）→ 拒绝发动");
                return false;
            }
            // 先全部复检（不产生任何副作用），再统一下扣
            var planned = new System.Collections.Generic.List<(CardInstance card, string marker, int count)>();
            int total = 0;
            foreach (var kv in allocation)
            {
                if (kv.Value <= 0) continue;
                var card = candidates.Find(c => string.Equals(c.UID, kv.Key, System.StringComparison.Ordinal));
                if (card == null)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 标志代价候选 {kv.Key} 不合法（{data.EffectId}）→ 拒绝发动（fail-closed）");
                    return false;
                }
                // 按白名单顺序找"卡上实际持有的写法"，逐点分配（与单卡支付同款语义）
                int remaining = kv.Value;
                foreach (var name in spec.Markers)
                {
                    if (remaining <= 0) break;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string marker = name.Trim();
                    if (card.Counters == null || !card.Counters.TryGetValue(marker, out int have) || have <= 0) continue;
                    int use = System.Math.Min(have, remaining);
                    planned.Add((card, marker, use));
                    remaining -= use;
                }
                if (remaining > 0)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 标志代价分配超出 {card.PlayerTemplate?.CardName} 实际持有量（{data.EffectId}）→ 拒绝发动（fail-closed）");
                    return false;
                }
                total += kv.Value;
            }
            if (total <= 0)
            {
                GD.Print($"[EffectEngine] ⛔ 标志代价分配总和为 0（{data.EffectId}）→ 拒绝发动");
                return false;
            }
            foreach (var (card, marker, count) in planned)
            {
                CM.RemoveCounter(card, marker, count);
                GD.Print($"[EffectEngine] ⊖ 标志代价：{card.PlayerTemplate?.CardName} 移除 {count} 个「{marker}」（发动者 P{source.ControllerId} 选择）");
            }
            paidX = total;
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
            return true;
        }

        /// <summary>【P3·K5】支付"移除 N 个（时间）标志"代价：选定卡（`chosenUid` 为空 → 确定性兜底取第一个合法候选）
        /// → 复检（fail-closed）→ 从该卡上移除标志（按白名单里**卡上实际持有的那个写法**扣）。
        /// 返回 false ＝代价无法支付（调用方应拒绝本次发动，且**不产生任何副作用**）。</summary>
        private bool PayCounterCost(CardInstance source, EffectData data, string chosenUid, out CardInstance paid)
        {
            paid = null;
            var spec = data?.CounterCost;
            if (spec == null) return true;                    // 无此代价
            if (source == null || CM == null) return false;
            int need = spec.Amount > 0 ? spec.Amount : 1;
            var candidates = GetCounterCostCandidates(source, spec);
            if (candidates.Count == 0)
            {
                GD.Print($"[EffectEngine] ⛔ 标志代价无候选（{data.EffectId}：本方场上无带该标志的卡）→ 拒绝发动");
                return false;
            }
            CardInstance pick = null;
            if (!string.IsNullOrEmpty(chosenUid))
            {
                pick = candidates.Find(c => string.Equals(c.UID, chosenUid, System.StringComparison.Ordinal));
                if (pick == null)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 指定的标志代价候选 {chosenUid} 不合法（{data.EffectId}）→ 拒绝发动（fail-closed）");
                    return false;
                }
            }
            else
            {
                pick = candidates[0];   // 无 UI/未指定：确定性兜底（与其它"无 UI 兜底"同款纪律）
                GD.Print($"[EffectEngine] ⚠ 未指定标志代价候选 → 确定性兜底取第一个合法候选（{pick.PlayerTemplate?.CardName}）");
            }

            // 扣标志：按白名单顺序找"卡上实际持有的写法"逐点移除
            int remaining = need;
            foreach (var name in spec.Markers)
            {
                if (remaining <= 0) break;
                if (string.IsNullOrWhiteSpace(name)) continue;
                string marker = name.Trim();
                if (pick.Counters == null || !pick.Counters.TryGetValue(marker, out int have) || have <= 0) continue;
                int use = System.Math.Min(have, remaining);
                CM.RemoveCounter(pick, marker, use);
                remaining -= use;
                GD.Print($"[EffectEngine] ⊖ 标志代价：{pick.PlayerTemplate?.CardName} 移除 {use} 个「{marker}」（发动者 P{source.ControllerId} 选择）");
            }
            if (remaining > 0)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 标志代价扣减不足（{data.EffectId}：仍缺 {remaining}）→ 拒绝发动（fail-closed）");
                return false;
            }
            paid = pick;
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
            return true;
        }

        /// <summary>【P3·K22】支付"牺牲一个角色"代价：选定卡（`chosenUid` 为空 → 确定性兜底取第一个合法候选）
        /// → 复检（fail-closed）→ `ResolveDeath(Sacrifice)`（**牺牲＝死亡，正常触发死亡时机**）。
        /// 返回 false ＝代价无法支付（调用方应拒绝本次发动，且**不产生任何副作用**）。</summary>
        private bool PaySacrificeCost(CardInstance source, EffectData data, string chosenUid, out CardInstance sacrificed)
        {
            sacrificed = null;
            var spec = data?.SacrificeCost;
            if (spec == null) return true;                    // 无此代价
            if (source == null || CM == null) return false;
            var candidates = GetSacrificeCandidates(source, spec);
            if (candidates.Count == 0)
            {
                GD.Print($"[EffectEngine] ⛔ 牺牲代价无候选（{data.EffectId}）→ 拒绝发动");
                return false;
            }
            CardInstance pick = null;
            if (!string.IsNullOrEmpty(chosenUid))
            {
                pick = candidates.Find(c => string.Equals(c.UID, chosenUid, System.StringComparison.Ordinal));
                if (pick == null)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 指定的牺牲候选 {chosenUid} 不合法（{data.EffectId}）→ 拒绝发动（fail-closed）");
                    return false;
                }
            }
            else
            {
                pick = candidates[0];   // 无 UI/未指定：确定性兜底（与其它"无 UI 兜底"同款纪律）
                GD.Print($"[EffectEngine] ⚠ 未指定牺牲候选 → 确定性兜底取第一个合法候选（{pick.PlayerTemplate?.CardName}）");
            }
            sacrificed = pick;
            GD.Print($"[EffectEngine] ✝ 牺牲代价：{pick.PlayerTemplate?.CardName} 因 {data.EffectId} 被牺牲（发动者 P{source.ControllerId} 选择）");
            CM.ResolveDeath(pick, CardManager.DeathCause.Sacrifice);   // 【触发语义】牺牲＝死亡 → 死亡时机照常触发
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
            return true;
        }

        // ==================== 【秘社批次·"牌作代价" CardCostSpec】 ====================

        /// <summary>
        /// 【秘社批次】"**从某区域取 N 张牌作为代价**"的**候选**（与牺牲/标志代价同款：候选侧与权威侧共用同一实现）。
        /// 口径见 <see cref="CardCostSpec"/>。确定性顺序＝来源区域列表顺序，便于双端同解与测试断言。
        /// </summary>
        public System.Collections.Generic.List<CardInstance> GetCardCostCandidates(CardInstance source, CardCostSpec spec)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            if (source == null || spec == null) return result;
            var zm = ZoneManagerRef;
            if (zm == null) return result;

            switch ((spec.FromZone ?? "").Trim())
            {
                case "封印区":
                {
                    // 宿主＝来源卡自身（秘社）时取它的封印列表；否则取该控制者场内所有卡的封印牌。
                    if (spec.HostSelf)
                    {
                        if (source.SealedCards == null) break;
                        foreach (var c in source.SealedCards)
                            if (IsLegalCardCost(source, spec, c)) result.Add(c);
                    }
                    else
                    {
                        foreach (var host in zm.GetZoneCards(CardZone.AreaZone, source.ControllerId)
                                     ?? new System.Collections.Generic.List<CardInstance>())
                        {
                            if (host?.SealedCards == null) continue;
                            foreach (var c in host.SealedCards)
                                if (IsLegalCardCost(source, spec, c)) result.Add(c);
                        }
                    }
                    break;
                }
                case "墓地":
                {
                    foreach (var c in zm.GetZoneCards(CardZone.Graveyard, source.ControllerId)
                                 ?? new System.Collections.Generic.List<CardInstance>())
                        if (IsLegalCardCost(source, spec, c)) result.Add(c);
                    break;
                }
                case "手牌":
                {
                    foreach (var c in zm.GetZoneCards(CardZone.Hand, source.ControllerId)
                                 ?? new System.Collections.Generic.List<CardInstance>())
                        if (IsLegalCardCost(source, spec, c)) result.Add(c);
                    break;
                }
                case "场上争夺区":
                {
                    foreach (var c in zm.GetZoneCards(CardZone.AreaZone, source.ControllerId)
                                 ?? new System.Collections.Generic.List<CardInstance>())
                        if (IsLegalCardCost(source, spec, c)) result.Add(c);
                    break;
                }
                default:
                    GD.PrintErr($"[EffectEngine] ⛔ CardCost 未登记的取牌区域「{spec.FromZone}」→ 无候选（fail-closed）");
                    break;
            }
            return result;
        }

        /// <summary>【秘社批次】单张牌是否可作为"牌作代价"（与候选侧**同一实现**，权威侧复检同源）。</summary>
        public bool IsLegalCardCost(CardInstance source, CardCostSpec spec, CardInstance candidate)
        {
            if (source == null || spec == null || candidate == null) return false;
            if (candidate.IsDying) return false;
            // 归属由**区域**保证（`GetCardCostCandidates` 按来源控制者所属区域收候选）：
            //  · 封印区 → 来源卡自己的 `SealedCards`；
            //  · 墓地/手牌/争夺区 → 该控制者的区域桶。
            // ⚠ 不要在这里再比对 `ControllerId`：被封印的牌**控制者仍是其拥有者**（封印不改变控制权），
            //   若按控制者过滤，"封印在自己秘社上、但属于对手的牌"会被误判为非法（实测踩到）。
            if (spec.UntappedOnly && candidate.IsTapped) return false;
            if (!string.IsNullOrWhiteSpace(spec.Filter) && !DeckSearchFilter.Matches(spec.Filter, candidate))
                return false;
            // 【区域一致性】候选必须确实在 `FromZone` 声明的区域里——否则"从墓地取"会把场上/手牌的牌也算进来。
            if (!IsInCardCostZone(spec.FromZone, source, candidate)) return false;
            return true;
        }

        /// <summary>候选是否位于 `CardCostSpec.FromZone` 声明的区域（含"封印区：宿主自身"的收窄）。</summary>
        private static bool IsInCardCostZone(string fromZone, CardInstance source, CardInstance candidate)
        {
            switch ((fromZone ?? "").Trim())
            {
                case "墓地": return candidate.CurrentZone == CardZone.Graveyard;
                case "手牌": return candidate.CurrentZone == CardZone.Hand;
                case "场上争夺区": return candidate.CurrentZone == CardZone.AreaZone;
                case "封印区":
                    return candidate.CurrentZone == CardZone.SealedZone
                           && candidate.SealedBy != null && ReferenceEquals(candidate.SealedBy, source);
                default: return false;
            }
        }

        /// <summary>
        /// 【秘社批次】支付"牌作代价"：选 N 张（`chosenUids` ＝ UI 选择的 UID 列表；空 ⇒ 确定性兜底取前 N 张），
        /// 逐张执行 `spec.CostAction`（缺省不做事）。**全部在入堆叠之前完成**。
        /// `Mandatory = true`（缺省）时候选不足 N 张 ⇒ 拒绝发动（返回 false，且**不做任何副作用**）。
        /// </summary>
        private bool PayCardCost(CardInstance source, EffectData data, System.Collections.Generic.List<string> chosenUids)
        {
            var spec = data?.CardCost;
            if (spec == null) return true;                       // 无此代价
            if (source == null) return false;

            int need = spec.Count > 0 ? spec.Count : 1;
            var candidates = GetCardCostCandidates(source, spec);

            var picked = new System.Collections.Generic.List<CardInstance>();
            if (chosenUids != null && chosenUids.Count > 0)
            {
                foreach (var uid in chosenUids)
                {
                    var c = candidates.Find(x => string.Equals(x.UID, uid, System.StringComparison.Ordinal));
                    if (c == null)
                    {
                        GD.PrintErr($"[EffectEngine] ⛔ 指定的牌作代价候选 {uid} 不合法（{data.EffectId}）→ 拒绝发动（fail-closed）");
                        return false;
                    }
                    if (!picked.Contains(c)) picked.Add(c);
                }
            }
            else
            {
                // 无 UI/未指定：确定性兜底取前 N 张（与其它"无 UI 兜底"同款纪律）
                int take = System.Math.Min(need, candidates.Count);
                for (int i = 0; i < take; i++) picked.Add(candidates[i]);
                if (take > 0)
                    GD.Print($"[EffectEngine] ⚠ 未指定牌作代价候选 → 确定性兜底取前 {take} 张（{data.EffectId}）");
            }

            if (picked.Count < need)
            {
                if (spec.Mandatory)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 牌作代价候选不足：需 {need} 张，仅有 {picked.Count} 张 → 拒绝发动 {data.EffectId}");
                    return false;
                }
                GD.Print($"[EffectEngine] ⚠ 牌作代价候选不足（{picked.Count}/{need}）但非强制 → 能取多少取多少");
            }

            foreach (var c in picked)
            {
                if (spec.CostAction == null) continue;
                GD.Print($"[EffectEngine] 🎴 牌作代价：对 {c.PlayerTemplate?.CardName ?? c.UID} 执行 {spec.CostAction.Verb}");
                ExecuteVerb(spec.CostAction, source, c);
            }
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
            return true;
        }

        // ==================== 【秘社批次·UnsealToArea：从封印区/墓地放置进场】 ====================

        /// <summary>
        /// 【秘社批次】把一张牌从**封印区**或**墓地**放进"本次结算的目标地区"。
        ///
        /// Args 约定（见 `ActionVerb.UnsealToArea`）：
        ///  · `Args[0]` 来源区：`"封印区"`（缺省）/ `"墓地"`；
        ///  · `Args[1]` 朝向：`"正面"`（缺省）/ `"暗藏"`；
        ///  · `Args[2]` 宿主：`"自身"`（缺省，＝来源卡上的封印牌）/ `"任意"`（不分宿主）；
        ///  · `Args[3]` 筛选（`DeckSearchFilter` 语法，空＝不限）；
        ///  · `Args[4]` 归属：`"由我操控"` ⇒ 进场后控制者改写为来源控制者（MSJC010③）。
        ///
        /// 选择路径（与其它"选牌"动词一致，**单一实现**）：
        ///  · 单机 → `IGameUIHooks.PromptPickCard`（回调式，挂起动作链；无取消）；
        ///  · 联机 → 走 `ExecuteVerbAsync` 的 `UnsealToAreaViaGatewayAsync`（本方法只在无网关时被调用）；
        ///  · 无 UI / 无网关 → **确定性兜底取第一个候选**（测试/headless）。
        /// </summary>
        private void ResolveUnsealToArea(EffectAction action, CardInstance source, CardInstance target, string targetAreaId)
        {
            if (source == null) { MarkActionIneffective("UnsealToArea", "来源为空"); return; }
            string areaUid = targetAreaId ?? "";
            if (string.IsNullOrEmpty(areaUid))
            { MarkActionIneffective("UnsealToArea", "推不出目标地区"); return; }

            var candidates = CollectUnsealCandidates(action, source);
            if (candidates.Count == 0)
            {
                GD.Print("[EffectEngine] ⚠ UnsealToArea：没有合法候选 → 本动作不生效");
                MarkActionIneffective("UnsealToArea", "无合法候选");
                return;
            }

            if (UIHooks != null)
            {
                SuspendChainForInput();
                UIHooks.PromptPickCard(source.ControllerId, "选择一张牌放置进场", candidates, allowCancel: false,
                    picked => { LandUnsealToArea(action, source, picked, areaUid); ContinuePendingChain(); });
                return;
            }
            GD.Print($"[EffectEngine] ⚠ UnsealToArea 无 UI：确定性兜底取第一个候选（{candidates[0].PlayerTemplate?.CardName}）");
            LandUnsealToArea(action, source, candidates[0], areaUid);
        }

        /// <summary>收集 `UnsealToArea` 的候选（来源区 + 宿主范围 + 筛选）。确定性顺序＝区域列表顺序。</summary>
        private System.Collections.Generic.List<CardInstance> CollectUnsealCandidates(EffectAction action, CardInstance source)
        {
            var result = new System.Collections.Generic.List<CardInstance>();
            var zm = ZoneManagerRef;
            if (zm == null || source == null) return result;

            string fromZone = ArgAt(action, 0, "封印区");
            string hostScope = ArgAt(action, 2, "自身");
            string filter = ArgAt(action, 3, "");

            System.Collections.Generic.List<CardInstance> pool = new();
            if (fromZone == "墓地")
            {
                // ⚠ 来源区判定必须**先于**宿主范围判定：墓地的牌与"宿主自身"无关
                //   （早期版本把"宿主自身"分支放在前面，导致"从墓地取"永远返回空池）。
                pool = zm.GetZoneCards(CardZone.Graveyard, source.ControllerId)
                       ?? new System.Collections.Generic.List<CardInstance>();
            }
            else if (hostScope == "自身")
            {
                // 只取"封印在来源卡（秘社）上"的牌——与 `CardCostSpec` 的 `HostSelf` 同口径。
                if (source.SealedCards != null) pool.AddRange(source.SealedCards);
            }
            else
            {
                // 任意宿主：该控制者争夺区内所有卡上的封印牌 + 封印于地区（SealedBy==null 但 ZoneDetailId 非空）的牌
                foreach (var host in zm.GetZoneCards(CardZone.AreaZone, source.ControllerId)
                             ?? new System.Collections.Generic.List<CardInstance>())
                    if (host?.SealedCards != null) pool.AddRange(host.SealedCards);
                pool.AddRange(zm.GetZoneCards(CardZone.SealedZone, source.ControllerId)
                              ?? new System.Collections.Generic.List<CardInstance>());
            }

            foreach (var c in pool)
            {
                if (c == null || c.IsDying) continue;
                if (!string.IsNullOrWhiteSpace(filter) && !DeckSearchFilter.Matches(filter, c)) continue;
                result.Add(c);
            }
            GD.Print($"[EffectEngine] UnsealToArea 候选：来源区={fromZone} 宿主={hostScope} 筛选='{filter}' → {result.Count} 张");
            return result;
        }

        /// <summary>把选定的一张牌从封印区/墓地放进目标地区（先解封印关系，再移动，最后按"归属"改写控制者）。</summary>
        private void LandUnsealToArea(EffectAction action, CardInstance source, CardInstance picked, string areaUid)
        {
            if (picked == null) { MarkActionIneffective("UnsealToArea", "选中的牌为空"); return; }

            // ① 解除封印关系（**必须在移动之前**：`ZoneManager.RemoveFromAllZones` 依赖 SealedBy 摘除，
            //    否则卡会同时留在宿主的 SealedCards 里，形成"既在封印区又在争夺区"的双重归属）。
            if (picked.SealedBy != null)
            {
                picked.SealedBy.SealedCards?.Remove(picked);
                picked.SealedBy = null;
            }
            picked.IsSeal = false;
            picked.IsSealedFaceDown = false;

            // ② 朝向（缺省正面）：暗藏只对进场有意义，与"派遣暗藏者"同序（先翻面再进场）。
            bool asCovert = ArgAt(action, 1, "正面") == "暗藏";
            picked.SetCovert(asCovert);

            // ③ 进场（照常广播进场触发）。
            CM?.MoveCardToArea(picked, areaUid);

            // ④ 归属："由我操控" ⇒ 控制者改写为来源控制者（MSJC010③"且由你操控"）。
            if (ArgAt(action, 4, "") == "由我操控" && picked.ControllerId != source.ControllerId)
                ZoneManagerRef?.ChangeController(picked, source.ControllerId);

            GD.Print($"[EffectEngine] UnsealToArea：{picked.PlayerTemplate?.CardName} {(asCovert ? "以暗藏者状态" : "正面")}" +
                     $"进场到地区 {areaUid}（控制者=P{picked.ControllerId}）");
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        // ==================== 【秘社批次·静态子类别授予 CouncilStaticGrant】 ====================

        /// <summary>已施加的"秘社静态子类别授予"记录：秘社 UID → 被授予卡 UID → 授予的子类别。</summary>
        private readonly System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.Dictionary<string, string>> _councilStaticGrants
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>();

        /// <summary>该卡当前是否是秘社、且在场（秘社区）。</summary>
        private static bool IsCouncilOnField(CardInstance card)
            => card != null && card.CouncilTemplate != null && card.CurrentZone == CardZone.CouncilZone;

        /// <summary>
        /// 【秘社批次】重算**全部秘社的静态子类别授予**（MSJC012 爱弗罗德家族"所有印刷费用≤2 的本方人类角色
        /// 额外具有奴仆类别"）。与光环同款纪律：**幂等**、差量补授/撤销、随任意移动重算。
        ///
        /// 消费点：装配层 `GameInitializer` 的 `ZoneManager.OnPostMove` 钩子（联机服务端/headless 同样生效）。
        /// 授予落点＝`CardInstance.CurrentSubCategories`（**叠加**，印刷类别不变；离场 `ResetToBaseState` 会被清，
        /// 故本方法在每次移动后重算即可自愈）。
        /// </summary>
        public void ReevaluateCouncilStaticGrants()
        {
            var zm = ZoneManagerRef;
            if (zm == null || CM == null) return;

            foreach (var pid in new[] { 1, 2 })
            {
                // 1) 该玩家的**每一张**秘社实例（秘社区理论上只有 1 张，但换秘社/重开局的短暂状态下
                //    可能同时存在多张；早期实现只取第一张 ⇒ 若第一张是别的秘社，本秘社的静态授予会被整块跳过，
                //    实测表现为"新加的秘社静态规则完全不生效"）。
                var councils = zm.GetZoneCards(CardZone.CouncilZone, pid)
                               ?? new System.Collections.Generic.List<CardInstance>();
                foreach (var council in councils)
                {
                    if (council?.CouncilTemplate == null) continue;
                    ReevaluateCouncilStaticGrantsFor(council);
                }
            }
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>重算**单张**秘社的静态子类别授予（逐秘社调用；`ReevaluateCouncilStaticGrants` 对所有秘社遍历）。</summary>
        private void ReevaluateCouncilStaticGrantsFor(CardInstance council)
        {
            var zm = ZoneManagerRef;
            if (zm == null || CM == null || council?.CouncilTemplate == null) return;
            int pid = council.ControllerId;

            {
                var rules = new System.Collections.Generic.List<CouncilStaticGrant>();
                foreach (var e in council.CouncilTemplate.StaticEffectList ?? new Godot.Collections.Array<EffectData>())
                    if (e?.CouncilStatic != null) rules.Add(e.CouncilStatic);

                if (!_councilStaticGrants.TryGetValue(council.UID, out var grantedTo))
                {
                    grantedTo = new System.Collections.Generic.Dictionary<string, string>();
                    _councilStaticGrants[council.UID] = grantedTo;
                }

                // 3) 撤销失效者（来源不在秘社区 / 卡已离场 / 不再符合筛选）
                foreach (var tUid in new System.Collections.Generic.List<string>(grantedTo.Keys))
                {
                    var t = CM.GetInstance(tUid);
                    var rule = MatchCouncilStaticRule(rules, t);
                    bool stillValid = IsCouncilOnField(council) && t != null
                        && t.CurrentZone == CardZone.AreaZone && rule != null;
                    if (stillValid) continue;
                    if (t != null && t.HasSubCategory(grantedTo[tUid]))
                        t.Revoke("SubCategory", grantedTo[tUid], 1);
                    GD.Print($"[EffectEngine] ⟳ 秘社静态授予撤销：{council.CouncilTemplate?.CardName} → {t?.PlayerTemplate?.CardName ?? tUid}");
                    grantedTo.Remove(tUid);
                }

                // 4) 补授（含自愈：换区会清运行期授予，登记表与实况不一致时要补回）
                if (!IsCouncilOnField(council)) return;
                foreach (var t in zm.GetZoneCards(CardZone.AreaZone, pid) ?? new System.Collections.Generic.List<CardInstance>())
                {
                    if (t == null) continue;
                    var rule = MatchCouncilStaticRule(rules, t);
                    if (rule == null) continue;
                    if (grantedTo.ContainsKey(t.UID) && t.HasSubCategory(rule.GrantSubCategory)) continue;
                    t.Grant("SubCategory", rule.GrantSubCategory, 1);
                    grantedTo[t.UID] = rule.GrantSubCategory;
                    GD.Print($"[EffectEngine] ✦ 秘社静态授予：{council.CouncilTemplate?.CardName} → " +
                             $"{t.PlayerTemplate?.CardName} += 子类别「{rule.GrantSubCategory}」");
                }
            }
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>该卡命中哪一条秘社静态授予规则（都不命中 → null）。各 `FilterByXxx` 之间为 AND。</summary>
        private static CouncilStaticGrant MatchCouncilStaticRule(System.Collections.Generic.List<CouncilStaticGrant> rules, CardInstance card)
        {
            if (rules == null || card == null || card.PlayerTemplate == null) return null;
            if (string.IsNullOrEmpty(card.PlayerTemplate.UID)) return null;
            foreach (var r in rules)
            {
                if (r == null || string.IsNullOrEmpty(r.GrantSubCategory)) continue;
                var tmpl = card.PlayerTemplate;
                if (r.FilterByCategory && (tmpl.CategoryFlag & r.TargetCategoryFlag) == 0) continue;
                if (r.FilterByMaxCost && tmpl.Cost > r.MaxCost) continue;
                if (r.FilterBySubCategories)
                {
                    bool hit = false;
                    if (r.TargetSubCategories != null)
                        foreach (var s in r.TargetSubCategories)
                            if (card.HasSubCategory(s) || card.HasStaticSubCategory(s)) { hit = true; break; }
                    if (!hit) continue;
                }
                if (r.LocalZoneOnly) continue;   // 当前无卡使用；如需启用请连 LocalZone 判定一起实现（fail-closed）
                return r;
            }
            return null;
        }

        /// <summary>【P3·K3】**伤害改由某卡承受**（JC077 庇佑圣灵 / JC121 空鸣御守，持续效果）：        /// 返回**应承受本次伤害的卡**（null＝不重定向，伤害照原样落在 `victim` 上）。
        /// 判定：遍历在册的重定向来源卡（在场、非死亡流程），按其 `DamageRedirect.Victim` 匹配 `victim`：
        ///  · `FriendlyNonSpiritLocal`：victim 与本卡**同控制者**、与本卡**同地区**、**非灵体**、是角色 → 本卡承受；
        ///  · `AttachedHost`：victim 正是本卡的**结附宿主** → 本卡承受。
        /// 口径：重定向的承受方＝**本卡自身**；承受方自身若有"防止"（灵体/护盾）照常在落地时生效。
        /// </summary>
        public CardInstance ResolveDamageRedirect(CardInstance victim)
        {
            if (victim == null || _damageRedirects.Count == 0) return null;
            foreach (var uid in _damageRedirects)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (ReferenceEquals(src, victim)) continue;                 // 不给自己重定向（防自环）

                var spec = FindDamageRedirectSpec(src);
                if (spec == null || spec.Victim == DamageRedirectVictim.None) continue;

                if (spec.Victim == DamageRedirectVictim.AttachedHost)
                {
                    if (src.CurrentZone != CardZone.AttachedZone) continue;  // 须仍在结附状态
                    if (!ReferenceEquals(src.AttachedTo, victim)) continue;
                    return src;
                }

                if (spec.Victim == DamageRedirectVictim.FriendlyNonSpiritLocal)
                {
                    if (src.CurrentZone != CardZone.AreaZone) continue;      // 须在争夺区（"位于本地区"）
                    if (victim.ControllerId != src.ControllerId) continue;   // "本方角色"
                    if (victim.ZoneDetailId != src.ZoneDetailId) continue;   // "位于本地区"
                    if (DamageRules.IsSpirit(victim)) continue;              // "非灵体"
                    var vt = victim.PlayerTemplate;
                    if (vt == null || (vt.CategoryFlag & CardCategory.Character) == 0) continue;   // 角色
                    return src;
                }
            }
            return null;
        }

        /// <summary>取某卡声明的伤害重定向声明（扫描其模板的持续效果）。</summary>
        private static DamageRedirectSpec FindDamageRedirectSpec(CardInstance card)
        {
            if (card == null) return null;
            foreach (var e in EffectsOf(card))
                if (e?.DamageRedirect != null && e.DamageRedirect.Victim != DamageRedirectVictim.None)
                    return e.DamageRedirect;
            return null;
        }

        /// <summary>登记/注销"伤害改由本卡承受"（`active=false` 时按卡 UID 清除）。</summary>
        private void SetDamageRedirect(CardInstance card, bool active)
        {
            if (card == null) return;
            _damageRedirects.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (!active) return;
            _damageRedirects.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 伤害重定向登记：{card.PlayerTemplate?.CardName}（本卡承受符合条件的伤害）");
        }

        // ==================== "伤害改由本卡承受"表（JC077 / JC121） ====================
        private readonly System.Collections.Generic.List<string> _damageRedirects
            = new System.Collections.Generic.List<string>();

        // ==================== 持续夺控表（JC036 初拥） ====================
        /// <summary>
        /// 一条已生效的**持续夺控**记录：来源卡（初拥）→ 被夺控的宿主卡。
        /// `AppliedControllerId` 记录**我们写进去的那一份控制者**，归还前比对，
        /// 以防期间该卡被别的夺控改过（那时不越权改写他人结果）。
        /// </summary>
        public sealed class ControlGainRecord
        {
            public string SourceUid;
            public string HostUid;
            public int AppliedControllerId;
        }

        private readonly System.Collections.Generic.List<ControlGainRecord> _controlGains
            = new System.Collections.Generic.List<ControlGainRecord>();

        /// <summary>防重入：夺控会驱动 `ChangeController` → `FireMoved` → 各类持续重算，重算期不得再次进入夺控。</summary>
        private bool _controlGainBusy;

        /// <summary>当前在册的持续夺控条数（测试/取证用）。</summary>
        public int ControlGainCount => _controlGains.Count;

        /// <summary>取某卡声明的持续夺控声明（扫描其模板的持续效果）。</summary>
        private static ControlGainSpec FindControlGainSpec(CardInstance card)
        {
            if (card == null) return null;
            foreach (var e in EffectsOf(card))
                if (e?.ControlGain != null && e.ControlGain.Victim != ControlGainVictim.None)
                    return e.ControlGain;
            return null;
        }

        /// <summary>
        /// 【P3·K4】登记/注销**持续夺控**（JC036 初拥「持续：你获得受此结附角色的控制权」）。
        ///  · `active=true`：解析受害者（`AttachedHost`＝本卡所结附的宿主；无宿主 → 不夺控）→
        ///    经 `ZoneManager.ChangeController` 统一出口把宿主交给**本卡控制者**（只改 `ControllerId`、
        ///    `OwnerId` 不变、**卡不移动地区**——"受控角色所在地区不变"）；
        ///    **不刷版本号**（`bumpVersion:false`）：与既有口径"争夺区内的夺控换边不刷新版本号"一致，
        ///    D16 目标丢失改由结算时的 `TargetValidity.CheckRestrictions`（按入栈来源控制者快照）承担；
        ///    并登记一条记录；
        ///  · `active=false`（来源离场/解除结附/前提丧失/卡失效）：按记录**归还给拥有者**
        ///    （卡面 FAQ 原文"该角色的操控权交还给其拥有者"），幂等。
        /// 用户 2026-09 口径（A16）：**不另设"版本号"判据**——版本号变化一般伴随初拥离场（持续效果自然注销），
        /// 被初拥角色离场时作为附属的初拥也一同进墓地，两条路径都落在本方法的注销分支上。
        /// </summary>
        private void SetControlGain(CardInstance card, bool active)
        {
            if (card == null) return;
            if (!active) { ReleaseControlGainsFor(card.UID); return; }
            if (_controlGainBusy) return;                                // 重入保护（见字段说明）
            var spec = FindControlGainSpec(card);
            if (spec == null) return;
            var host = spec.Victim == ControlGainVictim.AttachedHost ? card.AttachedTo : null;
            if (host == null)
            {
                GD.Print($"[EffectEngine] ⚠ 持续夺控 {card.PlayerTemplate?.CardName}：无结附宿主 → 不夺控");
                return;
            }
            foreach (var r in _controlGains)
                if (string.Equals(r.SourceUid, card.UID, System.StringComparison.Ordinal)) return;   // 已登记（幂等）
            int newController = card.ControllerId;
            if (newController <= 0) return;
            var rec = new ControlGainRecord { SourceUid = card.UID, HostUid = host.UID, AppliedControllerId = newController };
            _controlGains.Add(rec);
            if (host.ControllerId == newController)
            {
                GD.Print($"[EffectEngine] 👑 持续夺控登记：{host.PlayerTemplate?.CardName} 已在玩家{newController} 手中（{card.PlayerTemplate?.CardName}）");
                return;
            }
            _controlGainBusy = true;
            try { ZoneManagerRef?.ChangeController(host, newController, bumpVersion: false); }
            finally { _controlGainBusy = false; }
            GD.Print($"[EffectEngine] 👑 持续夺控：{host.PlayerTemplate?.CardName} → 玩家{newController}"
                   + $"（来源 {card.PlayerTemplate?.CardName}，所在地区不变）");
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>归还并移除**某来源卡**的全部持续夺控（幂等；`ClearAllStaticRulesFor`/离场注销共用）。</summary>
        private void ReleaseControlGainsFor(string sourceUid)
        {
            if (string.IsNullOrEmpty(sourceUid)) return;
            for (int i = _controlGains.Count - 1; i >= 0; i--)
            {
                var rec = _controlGains[i];
                if (!string.Equals(rec.SourceUid, sourceUid, System.StringComparison.Ordinal)) continue;
                _controlGains.RemoveAt(i);
                if (_controlGainBusy) continue;
                var host = CM?.GetInstance(rec.HostUid);
                if (host == null) continue;
                // 只归还"仍是我们写进去的那一份"：期间若被别的夺控改过，不越权改写
                if (host.ControllerId != rec.AppliedControllerId)
                {
                    GD.Print($"[EffectEngine] 👑 持续夺控归还跳过：{host.PlayerTemplate?.CardName} 现由玩家{host.ControllerId} 控制（非本效果施加）");
                    continue;
                }
                if (host.ControllerId == host.OwnerId) continue;         // 已是拥有者 → 无需动作
                _controlGainBusy = true;
                try { ZoneManagerRef?.ChangeController(host, host.OwnerId, bumpVersion: false); }
                finally { _controlGainBusy = false; }
                GD.Print($"[EffectEngine] 👑 持续夺控结束：{host.PlayerTemplate?.CardName} 控制权归还拥有者 玩家{host.OwnerId}");
                Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
            }
        }

        // ==================== 持续型"失去图标"表（JC123 饮魂者朱迪） ====================
        private readonly System.Collections.Generic.List<string> _iconSuppressors
            = new System.Collections.Generic.List<string>();
        /// <summary>卡 UID → 上一次**已施加**的抑制量（`Attribute` 授予记录）：重算时先按它精确还原。</summary>
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>
            _iconSuppressionApplied
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>();

        // ==================== 持续型"失去魔法领域图标"表（JC099 / JC101） ====================
        private readonly System.Collections.Generic.List<string> _domainSuppressors
            = new System.Collections.Generic.List<string>();
        /// <summary>已被本次规则抑制领域的卡 UID（重算时先全部还原，再按局面重新施加）。</summary>
        private readonly System.Collections.Generic.List<string> _domainsSuppressedCards
            = new System.Collections.Generic.List<string>();

        /// <summary>【P3·K20】持续型"失去魔法领域图标"的**统一重算**：先全部还原，再收集在册来源的规格并施加。
        /// 幂等；触发点＝来源增减 / 目标卡的封印变化、进出场（均经 `ReevaluatePrereqs`）等。</summary>
        public void ReapplySuppressions()
        {
            // 1) 全部还原（领域图标 + 文本空白）。文本空白还原＝**先摘标记**、再重新注册其效果——
            //    否则 `RegisterPersistent` 会被 `IsTextBlank` 门禁挡回去，效果永远回不来。
            foreach (var uid in _domainsSuppressedCards)
                CM?.GetInstance(uid)?.RestoreDomains();
            _domainsSuppressedCards.Clear();
            var prevBlanked = new System.Collections.Generic.List<string>(_textBlankedCards);
            _textBlankedCards.Clear();
            foreach (var uid in prevBlanked)
            {
                var tb = CM?.GetInstance(uid);
                if (tb != null) RegisterPersistent(tb);     // 解除屏蔽 → 效果恢复（重新注册持续效果）
            }
            if (_domainSuppressors.Count == 0 && _textBlankSuppressors.Count == 0) return;

            // 2) 收集在册来源的规格（**在场**即可：角色在争夺区、附属在附属区；死亡流程中的跳过）
            //    注：各 victim 的**区域语义**在下面 3)/4) 的匹配分支里分别把关
            //    （`EnemyCharacterWithSealedCards` 要求来源在争夺区；`AttachedHost` 要求来源在附属区）。
            var specs = new System.Collections.Generic.List<DomainSuppressionSpec>();
            foreach (var uid in _domainSuppressors)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone && src.CurrentZone != CardZone.AttachedZone) continue;
                var spec = FindDomainSuppressionSpec(src);
                if (spec != null) specs.Add(spec);
            }
            if (specs.Count == 0) return;

            // 3) 逐张场上卡匹配（含结附宿主：附属卡自身不在争夺区，故单独处理 AttachedHost）
            var field = ZoneManagerRef?.GetZoneCards(CardZone.AreaZone, 0)
                ?? new System.Collections.Generic.List<CardInstance>();
            foreach (var srcUid in _domainSuppressors)
            {
                var src = CM?.GetInstance(srcUid);
                if (src == null || src.IsDying) continue;
                var spec = FindDomainSuppressionSpec(src);
                if (spec == null) continue;
                if (spec.Victim == DomainSuppressionVictim.AttachedHost)
                {
                    var host = src.CurrentZone == CardZone.AttachedZone ? src.AttachedTo : null;
                    ApplyDomainSuppression(host);
                    continue;
                }
                foreach (var c in field)
                {
                    if (c == null || c.IsDying) continue;
                    if (spec.Victim != DomainSuppressionVictim.EnemyCharacterWithSealedCards) continue;
                    if (c.ControllerId == src.ControllerId) continue;                       // "敌方"
                    if (c.SealedCards == null || c.SealedCards.Count == 0) continue;        // "其上有牌封印"
                    var ct = c.PlayerTemplate;
                    if (ct == null || (ct.CategoryFlag & CardCategory.Character) == 0) continue;   // "角色"
                    ApplyDomainSuppression(c);
                }
            }

            // 4)【P3·K20】"印刷文本框视作空白"（JC101）：对象与领域抑制**同一 victim**，故复用同一匹配，
            //    命中即 `UnregisterPersistent`（该卡的持续效果全部停止＝**失去效果**）并记录，供下次还原时重注册。
            foreach (var srcUid in _textBlankSuppressors)
            {
                var src = CM?.GetInstance(srcUid);
                if (src == null || src.IsDying) continue;
                var blankSpec = FindTextBlankSpec(src);
                if (blankSpec == null || blankSpec.DomainSuppression == null) continue;
                if (blankSpec.DomainSuppression.Victim == DomainSuppressionVictim.AttachedHost)
                {
                    var host = src.CurrentZone == CardZone.AttachedZone ? src.AttachedTo : null;
                    ApplyTextBlank(host);
                    continue;
                }
                foreach (var c in field)
                {
                    if (c == null || c.IsDying) continue;
                    if (blankSpec.DomainSuppression.Victim != DomainSuppressionVictim.EnemyCharacterWithSealedCards) continue;
                    if (c.ControllerId == src.ControllerId) continue;
                    if (c.SealedCards == null || c.SealedCards.Count == 0) continue;
                    var ct = c.PlayerTemplate;
                    if (ct == null || (ct.CategoryFlag & CardCategory.Character) == 0) continue;
                    ApplyTextBlank(c);
                }
            }
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>【P3·K20】对一张卡施加"文本空白"＝**失去效果**：注销其全部持久效果（持续/静止/触发不再生效）
        /// 并记入屏蔽集合（还原时由 `ReapplySuppressions` 重新注册）。
        /// 注：**已在堆叠上的条目照常结算**（用户口径"不影响已经发动的效果"）。</summary>
        private void ApplyTextBlank(CardInstance card)
        {
            if (card == null || _textBlankedCards.Contains(card.UID)) return;
            _textBlankedCards.Add(card.UID);
            UnregisterPersistent(card);
            GD.Print($"[EffectEngine] ⊘ 文本视作空白：{card.PlayerTemplate?.CardName} 失去效果（已注册的持续效果停止）");
        }

        /// <summary>取某卡"文本空白"声明所在的效果（用于取它的 `DomainSuppression.Victim` 作匹配口径）。</summary>
        private static EffectData FindTextBlankSpec(CardInstance card)
        {
            if (card == null) return null;
            foreach (var e in EffectsOf(card))
                if (e != null && e.TextBlank) return e;
            return null;
        }

        private void ApplyDomainSuppression(CardInstance card)
        {
            if (card == null || card.AreDomainsSuppressed) return;
            card.SuppressAllDomains();
            if (!_domainsSuppressedCards.Contains(card.UID)) _domainsSuppressedCards.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 持续失去领域图标：{card.PlayerTemplate?.CardName} 的魔法领域图标被抑制");
        }

        /// <summary>取某卡声明的"失去魔法领域图标"规格（扫描其模板的持续效果）。</summary>
        private static DomainSuppressionSpec FindDomainSuppressionSpec(CardInstance card)
        {
            if (card == null) return null;
            foreach (var e in EffectsOf(card))
                if (e?.DomainSuppression != null && e.DomainSuppression.Victim != DomainSuppressionVictim.None)
                    return e.DomainSuppression;
            return null;
        }

        /// <summary>登记/注销"失去领域图标"来源（`active=false` 时按卡 UID 清除并**重算**以还原）。</summary>
        private void SetDomainSuppressor(CardInstance card, bool active)
        {
            if (card == null) return;
            _domainSuppressors.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (active) _domainSuppressors.Add(card.UID);
        }

        // ==================== "印刷文本框视作空白"表（JC101 直视深渊） ====================
        private readonly System.Collections.Generic.List<string> _textBlankSuppressors
            = new System.Collections.Generic.List<string>();
        /// <summary>当前被"文本空白"屏蔽（＝失去效果）的卡 UID。</summary>
        private readonly System.Collections.Generic.List<string> _textBlankedCards
            = new System.Collections.Generic.List<string>();

        /// <summary>【P3·K20】该卡是否处于"印刷文本框视作空白"状态（＝**失去效果**）。
        /// 口径（用户 2026-09）：只切断"未来"——不再注册持续效果、不再可发动、不再参与触发收集；
        /// **已经发动的效果不受影响**（故本查询**不**用于结算路径）。</summary>
        public bool IsTextBlank(CardInstance card)
            => card != null && _textBlankedCards.Contains(card.UID);

        /// <summary>登记/注销"文本空白"来源（`active=false` 时按卡 UID 清除并重算以恢复）。</summary>
        private void SetTextBlankSuppressor(CardInstance card, bool active)
        {
            if (card == null) return;
            _textBlankSuppressors.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (active) _textBlankSuppressors.Add(card.UID);
        }

        // ==================== "运行期授予的激活能力"表（JC019 弥尔顿的镇魔枪） ====================
        // 键＝宿主卡 UID；值＝(授予键, 能力数据)。授予键＝来源卡 UID + "#" + 效果 Id，便于精确收回。
        private readonly System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.Dictionary<string, EffectData>> _grantedAbilities
            = new System.Collections.Generic.Dictionary<string,
                System.Collections.Generic.Dictionary<string, EffectData>>();

        /// <summary>【P3·K15】激活能力"目标地区"的**合法候选**（确定性顺序＝地区编号序）：
        /// `AreaTargetExcludesCardArea` 时排除**卡目标当前所在地区**（JC117 FAQ"不能原地移动"）。
        /// 单一实现：权威门禁（`ActivateAbility` 的 2.7 步）与 View/候选侧共用。</summary>
        public System.Collections.Generic.List<string> GetLegalAreaTargets(CardInstance source, EffectData data,
            CardInstance cardTarget)
        {
            var result = new System.Collections.Generic.List<string>();
            if (data == null || !data.NeedsAreaTarget) return result;
            // 地区来源＝`AreaManager` 的已注册地区（与对抗阶段遍历用同一份，顺序＝地区编号序）
            var all = TurnManagerRef?.AreaManagerRef?.GetOrderedAreaUids()
                ?? new System.Collections.Generic.List<string>();
            foreach (var a in all)
            {
                if (string.IsNullOrEmpty(a)) continue;
                if (data.AreaTargetExcludesCardArea && cardTarget != null
                    && string.Equals(a, cardTarget.ZoneDetailId, System.StringComparison.Ordinal)) continue;
                result.Add(a);
            }
            return result;
        }

        /// <summary>【P3·K13】该卡当前**运行期获得的**激活能力（供候选枚举与 View 的"多能力选择窗口"合并）。
        /// 顺序确定：按登记先后（＝来源卡进场顺序）。</summary>
        public System.Collections.Generic.List<EffectData> GetGrantedAbilities(CardInstance card)
        {
            var result = new System.Collections.Generic.List<EffectData>();
            if (card == null) return result;
            if (_grantedAbilities.TryGetValue(card.UID, out var map))
                foreach (var kv in map) result.Add(kv.Value);
            return result;
        }

        /// <summary>【P3·K13】**统一重算**运行期授予的激活能力：该能力的来源是"带 `GrantedAbility` 的**持续效果**"
        /// （授予落点＝`GrantToHost` 的宿主，否则来源自身）。重算＝先清空、再按当前局面重建，
        /// 与其它统一重算同款（幂等）；触发点＝来源登记/注销、`ReevaluatePrereqs`。</summary>
        public void ReapplyGrantedAbilities()
        {
            _grantedAbilities.Clear();
            var field = ZoneManagerRef?.GetZoneCards(CardZone.AreaZone, 0)
                ?? new System.Collections.Generic.List<CardInstance>();
            var attached = ZoneManagerRef?.GetZoneCards(CardZone.AttachedZone, 0)
                ?? new System.Collections.Generic.List<CardInstance>();
            var sources = new System.Collections.Generic.List<CardInstance>(field);
            sources.AddRange(attached);
            foreach (var src in sources)
            {
                if (src == null || src.IsDying) continue;
                if (src.PlayerTemplate is null) continue;
                if (IsTextBlank(src)) continue;                       // 文本视作空白 → 失去效果（不再授予能力）
                foreach (var e in EffectsOf(src))
                {
                    if (e?.GrantedAbility == null) continue;
                    if (!IsEffectActiveInZone(e, src)) continue;
                    if (!EvaluatePrereq(src, e)) continue;            // 前提（如"结附于角色"）不满足则不授予
                    var target = ResolveGrantTarget(src, e);
                    if (target == null) continue;
                    if (!_grantedAbilities.TryGetValue(target.UID, out var map))
                    {
                        map = new System.Collections.Generic.Dictionary<string, EffectData>();
                        _grantedAbilities[target.UID] = map;
                    }
                    map[PrereqKey(src.UID, e.EffectId)] = e.GrantedAbility;
                    GD.Print($"[EffectEngine] ⊞ 运行期授予激活能力：{target.PlayerTemplate?.CardName} ← " +
                             $"{src.PlayerTemplate?.CardName}（{e.GrantedAbility.EffectId}）");
                }
            }
        }

        /// <summary>【P3·K20】持续型"失去图标"的**统一重算**：先按记录精确还原上一次抑制，再按当前局面重算。
        /// 幂等（可反复调用）；属性值在此期间变化也能被正确覆盖。触发点见 <see cref="EffectData.IconSuppression"/>。</summary>
        public void ReapplyIconSuppressions()
        {
            // 1) 还原上一次的抑制（记录的是**施加量 n**，施加时用的是 `Grant(attr, -n)` → 还原用 `Grant(attr, +n)`）
            foreach (var kv in _iconSuppressionApplied)
            {
                var prev = CM?.GetInstance(kv.Key);
                if (prev == null) continue;
                foreach (var g in kv.Value) prev.Grant("Attribute", g.Value, g.Amount);
            }
            _iconSuppressionApplied.Clear();
            if (_iconSuppressors.Count == 0) return;

            // 2) 收集在册来源的规格（来源须**在场**＝争夺区、非死亡流程）
            var specs = new System.Collections.Generic.List<IconSuppressionSpec>();
            foreach (var uid in _iconSuppressors)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone) continue;
                var spec = FindIconSuppressionSpec(src);
                if (spec != null) specs.Add(spec);
            }
            if (specs.Count == 0) return;

            // 3) 逐张场上卡：命中任一规格 → 抹掉其图标对应的**当前有效贡献**并记录下来（供下次还原）
            var field = ZoneManagerRef?.GetZoneCards(CardZone.AreaZone, 0)
                ?? new System.Collections.Generic.List<CardInstance>();
            foreach (var c in field)
            {
                if (c == null || c.IsDying) continue;
                IconSuppressionSpec hit = null;
                foreach (var s in specs) if (MatchesIconSuppression(s, c)) { hit = s; break; }
                if (hit == null) continue;
                var applied = new System.Collections.Generic.List<EffectGrant>();
                foreach (var icon in hit.Icons)
                {
                    string attr = IconToAttribute(icon);
                    if (string.IsNullOrEmpty(attr)) continue;
                    int n = attr == "势力" ? c.CurrentPower : c.GetAttribute(attr);
                    if (n <= 0) continue;
                    c.Grant("Attribute", attr, -n);
                    applied.Add(new EffectGrant { Kind = "Attribute", Value = attr, Amount = n });
                }
                if (applied.Count > 0)
                {
                    _iconSuppressionApplied[c.UID] = applied;
                    GD.Print($"[EffectEngine] ⊘ 持续失去图标：{c.PlayerTemplate?.CardName} 失去 [{string.Join(",", hit.Icons)}]");
                }
            }
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        /// <summary>该卡是否满足"持续失去图标"的对象条件（`RequireCounters` 任一命中；空＝不限）。</summary>
        private static bool MatchesIconSuppression(IconSuppressionSpec spec, CardInstance card)
        {
            if (spec == null || card == null) return false;
            if (spec.RequireCounters == null || spec.RequireCounters.Count == 0) return true;
            foreach (var name in spec.RequireCounters)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (card.Counters.TryGetValue(name.Trim(), out int n) && n > 0) return true;
            }
            return false;
        }

        /// <summary>取某卡声明的"持续失去图标"规格（扫描其模板的持续效果）。</summary>
        private static IconSuppressionSpec FindIconSuppressionSpec(CardInstance card)
        {
            if (card == null) return null;
            foreach (var e in EffectsOf(card))
                if (e?.IconSuppression != null && e.IconSuppression.Icons != null && e.IconSuppression.Icons.Count > 0)
                    return e.IconSuppression;
            return null;
        }

        /// <summary>登记/注销"持续失去图标"来源（`active=false` 时按卡 UID 清除并**重算**以还原）。</summary>
        private void SetIconSuppressor(CardInstance card, bool active)
        {
            if (card == null) return;
            _iconSuppressors.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (active) _iconSuppressors.Add(card.UID);
        }

        /// <summary>【P3·K3】该地区内 **`cardControllerId` 控制**的暗藏者是否被静止规则排除出势力对抗（JC023）。
        /// 口径：地区内存在任一登记来源卡、且其控制者 ≠ `cardControllerId`（＝"敌方暗藏者"）。</summary>
        public bool ExcludesCovertPower(string areaUid, int cardControllerId)
        {
            if (string.IsNullOrEmpty(areaUid) || cardControllerId < 0) return false;
            foreach (var uid in _covertPowerExcludedBy)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone) continue;              // 只在争夺区生效
                if (!string.Equals(src.ZoneDetailId, areaUid, System.StringComparison.Ordinal)) continue;
                if (src.ControllerId == cardControllerId) continue;              // 本方暗藏者不受影响
                return true;
            }
            return false;
        }

        /// <summary>登记/注销"敌方暗藏者不参与势力对抗"（`active=false` 时按卡 UID 清除）。</summary>
        private void SetCovertPowerExclusion(CardInstance card, bool active)
        {
            if (card == null) return;
            _covertPowerExcludedBy.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (!active) return;
            _covertPowerExcludedBy.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 地区静止规则登记：{card.PlayerTemplate?.CardName} → 本地区敌方暗藏者不参与势力对抗");
        }

        /// <summary>该持续效果是否携带"敌方暗藏者不能参与势力对抗"静止标记。</summary>
        private static bool HasCovertPowerExclusion(EffectData e)
        {
            if (e?.StaticEffects == null) return false;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.ExcludeCovertPower) return true;
            return false;
        }

        // ==================== "其上有牌封印的敌方角色不能被重置"表（JC108 乔治·帕佐，织梦人） ====================
        // 卡文无"本地区"限定 → **不按地区匹配**（来源在场即全局生效）。查询按来源卡的**实时**状态判定。
        private readonly System.Collections.Generic.List<string> _noUntapWithSealedCardsBy
            = new System.Collections.Generic.List<string>();

        /// <summary>【P3·K29】该卡在本回合的重置步骤中是否**不能被重置**（JC108）：
        /// 需满足 ① 本卡是**角色**（卡文"敌方**角色**"）② 其上有牌封印（`SealedCards` 非空）
        /// ③ 场上存在登记来源卡（在场且非死亡流程中）且其控制者 **≠** 本卡控制者（＝"敌方"）。</summary>
        public bool IsUntapBlocked(CardInstance card)
        {
            if (card == null || !card.IsTapped) return false;
            if (card.SealedCards == null || card.SealedCards.Count == 0) return false;
            var tmpl = card.PlayerTemplate;
            if (tmpl == null || (tmpl.CategoryFlag & CardCategory.Character) == 0) return false;   // 只保护"角色"
            foreach (var uid in _noUntapWithSealedCardsBy)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone) continue;          // 来源须"在场"（争夺区）
                if (src.ControllerId == card.ControllerId) continue;         // 只锁"敌方"角色
                return true;
            }
            return false;
        }

        /// <summary>登记/注销"其上有牌封印的敌方角色不能被重置"（`active=false` 时按卡 UID 清除）。</summary>
        private void SetNoUntapWithSealedCards(CardInstance card, bool active)
        {
            if (card == null) return;
            _noUntapWithSealedCardsBy.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (!active) return;
            _noUntapWithSealedCardsBy.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 静止规则登记：{card.PlayerTemplate?.CardName} → 其上有牌封印的敌方角色重置步骤不能被重置");
        }

        /// <summary>该持续效果是否携带"其上有牌封印的敌方角色不能被重置"静止标记。</summary>
        private static bool HasNoUntapWithSealedCards(EffectData e)
        {
            if (e?.StaticEffects == null) return false;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.NoUntapWithSealedCards) return true;
            return false;
        }

        // ==================== 地区级"额外进行一次势力对抗"表（JC067 约翰·F·纳特里） ====================
        // 与 `_areaCombatSkips` 同一套生命周期；查询按卡**当前**所在争夺区实时匹配地区。
        private readonly System.Collections.Generic.List<string> _extraPowerContestBy
            = new System.Collections.Generic.List<string>();

        /// <summary>【P3·K18】该地区在对抗阶段是否要**额外进行一次势力对抗**（JC067）：
        /// 地区内存在登记来源卡（在争夺区、非死亡流程）即成立。每回合每地区至多追加一次由调用方去重。</summary>
        public bool AreaHasExtraPowerContest(string areaUid)
        {
            if (string.IsNullOrEmpty(areaUid)) return false;
            foreach (var uid in _extraPowerContestBy)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone) continue;                  // 只在争夺区生效
                if (string.Equals(src.ZoneDetailId, areaUid, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>登记/注销"本地区额外进行一次势力对抗"（`active=false` 时按卡 UID 清除）。</summary>
        private void SetExtraPowerContest(CardInstance card, bool active)
        {
            if (card == null) return;
            _extraPowerContestBy.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (!active) return;
            _extraPowerContestBy.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 地区静止规则登记：{card.PlayerTemplate?.CardName} → 本地区额外进行一次势力对抗");
        }

        /// <summary>该持续效果是否携带"本地区额外进行一次势力对抗"静止标记。</summary>
        private static bool HasExtraPowerContest(EffectData e)
        {
            if (e?.StaticEffects == null) return false;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.ExtraAreaPowerContest) return true;
            return false;
        }

        // ==================== 地区级"非某组织角色不可进入"表（JC064 戒严） ====================
        // 卡文「所有非【国家机构】的角色牌不可正面打出在本地区，也不能移动进入本地区」；
        // 来源＝"结附于该地区"的附属（也可扩展为位于该地区的卡）。查询按来源**当前**所在/所结附地区实时匹配。
        private readonly System.Collections.Generic.List<string> _nonOrgEntryBlockers
            = new System.Collections.Generic.List<string>();

        /// <summary>登记/注销"本地区非某组织角色不可进入"（`active=false` 时按卡 UID 清除）。</summary>
        private void SetNonOrgEntryBlock(CardInstance card, bool active)
        {
            if (card == null) return;
            _nonOrgEntryBlockers.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (!active) return;
            _nonOrgEntryBlockers.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 地区静止规则登记：{card.PlayerTemplate?.CardName} → 非指定组织角色不可进入本地区");
        }

        /// <summary>取该持续效果声明的"允许进入的组织名"（未声明返回 null）。</summary>
        private static string GetNonOrgEntryAllowedOrg(EffectData e)
        {
            if (e?.StaticEffects == null) return null;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.BlockNonOrgEntryInArea)
                    return s.Param ?? "";
            return null;
        }

        /// <summary>该持续效果是否携带"非某组织角色不可进入本地区"静止标记。</summary>
        private static bool HasNonOrgEntryBlock(EffectData e) => GetNonOrgEntryAllowedOrg(e) != null;

        /// <summary>
        /// 【P3·K21·JC064】某卡**能否进入**指定地区：返回 true＝被"戒严"类静止规则**拦下**。
        /// 判定：区内存在登记来源（结附于该地区 或 位于该地区，且非死亡）→ 该卡的**组织名**与
        /// 规则声明的**允许组织**不同 → 拦下（卡文"所有非【国家机构】的角色牌…"）。
        /// 注：调用方决定适用范围（仅**角色牌**、且仅"**移动进入**"；秘密派遣与"放置进场"放行——见 `StaticKind` 注释）。
        /// </summary>
        public bool IsEntryBlockedByOrgRule(CardInstance card, string areaUid)
        {
            if (card == null || string.IsNullOrEmpty(areaUid) || _nonOrgEntryBlockers.Count == 0) return false;
            string org = card.PlayerTemplate?.Organization ?? "";
            foreach (var uid in _nonOrgEntryBlockers)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (!IsSourceGoverningArea(src, areaUid)) continue;
                foreach (var e in EffectsOf(src))
                {
                    string allowed = GetNonOrgEntryAllowedOrg(e);
                    if (allowed == null) continue;
                    if (string.IsNullOrEmpty(allowed)) continue;                       // 未写组织名 → 不拦（fail-open 防拼错静默生效）
                    if (!string.Equals(org, allowed, System.StringComparison.Ordinal))
                    {
                        GD.Print($"[EffectEngine] ⛔ 戒严类静止规则：{card.PlayerTemplate?.CardName}（组织='{org}'）"
                               + $"不可进入地区 {areaUid}（需 '{allowed}'，来源 {src.PlayerTemplate?.CardName}）");
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>该来源卡是否"管辖"指定地区：结附于该地区（无宿主的附属）或位于该地区（争夺区卡）。</summary>
        private static bool IsSourceGoverningArea(CardInstance src, string areaUid)
        {
            if (src == null) return false;
            if (src.CurrentZone == CardZone.AreaZone)
                return string.Equals(src.ZoneDetailId ?? "", areaUid, System.StringComparison.Ordinal);
            if (src.CurrentZone == CardZone.AttachedZone && src.AttachedTo == null)
                return string.Equals(src.ZoneDetailId ?? "", areaUid, System.StringComparison.Ordinal);   // 结附于地区
            return false;
        }

        // ==================== "带锁定标记的暗藏者不能现身"表（JC122 哈克·亚当斯） ====================
        // 卡文无"本地区"限定 → **不按地区匹配**（来源在场即全局生效）。
        // 用户 2026-09 口径：只封"现身"这一个动作，且只对**带锁定标记的暗藏者**生效。
        private readonly System.Collections.Generic.List<string> _noRevealWithLockCounterBy
            = new System.Collections.Generic.List<string>();

        /// <summary>【P3·K21】该卡此刻**是否可以现身**（JC122）：
        /// 需满足 ① 本卡是**暗藏者**（正面角色本就没有"现身"动作）② 卡上带**锁定标记/锁定标志**
        /// ③ 场上存在登记来源卡（在场且未在死亡流程中）。三条同时成立 → 禁止现身。</summary>
        public bool IsRevealBlocked(CardInstance card)
        {
            if (card == null || !card.IsCovert) return false;
            // 卡上标志判定口径与 `TargetRestriction.HasCounter` / `IconSuppression.RequireCounters` 一致：
            // `card.Counters[标志名] ≥ 1`（卡库对同一概念有"锁定标记"/"锁定标志"两种写法，两种都认）。
            bool locked = (card.Counters.TryGetValue("锁定标记", out int a) && a > 0)
                       || (card.Counters.TryGetValue("锁定标志", out int b) && b > 0);
            if (!locked) return false;
            foreach (var uid in _noRevealWithLockCounterBy)
            {
                var src = CM?.GetInstance(uid);
                if (src == null || src.IsDying) continue;
                if (src.CurrentZone != CardZone.AreaZone) continue;   // 来源须"在场"（争夺区）
                return true;
            }
            return false;
        }

        /// <summary>登记/注销"带锁定标记的暗藏者不能现身"（`active=false` 时按卡 UID 清除）。</summary>
        private void SetNoRevealWithLockCounter(CardInstance card, bool active)
        {
            if (card == null) return;
            _noRevealWithLockCounterBy.RemoveAll(u => string.Equals(u, card.UID, System.StringComparison.Ordinal));
            if (!active) return;
            _noRevealWithLockCounterBy.Add(card.UID);
            GD.Print($"[EffectEngine] ⊘ 静止规则登记：{card.PlayerTemplate?.CardName} → 带锁定标记的暗藏者不能现身");
        }

        /// <summary>该持续效果是否携带"带锁定标记的暗藏者不能现身"静止标记。</summary>
        private static bool HasNoRevealWithLockCounter(EffectData e)
        {
            if (e?.StaticEffects == null) return false;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.NoRevealWithLockCounter) return true;
            return false;
        }

        /// <summary>【P3】**统一入口**：按效果声明登记/注销全部"静止规则"登记表
        /// （地区级：略过对抗 / 敌方暗藏者不参与势力对抗 / 额外势力对抗；全局：封印角色不可被重置）。
        /// `active=false` 时按卡 UID 清除其全部登记（幂等）。新增静止种类只需在此加一行。</summary>
        private void SetStaticRuleRegistrations(CardInstance card, EffectData e, bool active)
        {
            if (card == null) return;
            var skipCombats = GetAreaCombatSkips(e);
            if (skipCombats != null) SetAreaCombatSkip(card, active ? skipCombats : null, active);
            if (HasCovertPowerExclusion(e)) SetCovertPowerExclusion(card, active);
            if (HasNoUntapWithSealedCards(e)) SetNoUntapWithSealedCards(card, active);
            if (HasNoRevealWithLockCounter(e)) SetNoRevealWithLockCounter(card, active);   // 【P3·K21】JC122
            if (HasExtraPowerContest(e)) SetExtraPowerContest(card, active);
            if (HasNonOrgEntryBlock(e)) SetNonOrgEntryBlock(card, active);   // 【P3·K21】JC064 戒严
            // 【P3·K3】伤害改由本卡承受（JC077/JC121，持续效果）
            if (e.DamageRedirect != null && e.DamageRedirect.Victim != DamageRedirectVictim.None)
                SetDamageRedirect(card, active);
            // 【P3·K4】持续夺控（JC036 初拥）：登记时夺控宿主、注销时归还拥有者
            if (e.ControlGain != null && e.ControlGain.Victim != ControlGainVictim.None)
                SetControlGain(card, active);
            // 【P3·K20】持续型"失去图标"（JC123）：登记/注销来源后**立即重算**（先还原再施加，幂等）
            if (e.IconSuppression != null && e.IconSuppression.Icons != null && e.IconSuppression.Icons.Count > 0)
            {
                SetIconSuppressor(card, active);
                ReapplyIconSuppressions();
            }
            // 【P3·K20】持续型"失去魔法领域图标"（JC099/JC101）：登记/注销来源后立即重算
            if (e.DomainSuppression != null && e.DomainSuppression.Victim != DomainSuppressionVictim.None)
            {
                SetDomainSuppressor(card, active);
                if (e.TextBlank) SetTextBlankSuppressor(card, active);
                ReapplySuppressions();
            }
        }

        /// <summary>该持续效果是否携带**任一**需登记的静止规则（用于决定是否把来源卡纳入重判全集）。</summary>
        private static bool HasAnyStaticRule(EffectData e)
            => GetAreaCombatSkips(e) != null || HasCovertPowerExclusion(e)
            || HasNoUntapWithSealedCards(e) || HasExtraPowerContest(e)
            || HasNoRevealWithLockCounter(e)          // 【P3·K21】JC122：带锁定标记的暗藏者不能现身
            || HasNonOrgEntryBlock(e)                 // 【P3·K21】JC064：非指定组织角色不可进入本地区
            || (e?.DamageRedirect != null && e.DamageRedirect.Victim != DamageRedirectVictim.None)
            || (e?.ControlGain != null && e.ControlGain.Victim != ControlGainVictim.None)   // 【P3·K4】JC036 持续夺控
            || (e?.IconSuppression != null && e.IconSuppression.Icons != null && e.IconSuppression.Icons.Count > 0)
            || (e?.DomainSuppression != null && e.DomainSuppression.Victim != DomainSuppressionVictim.None);

        /// <summary>按卡 UID 清除该卡的**全部**静止规则登记（卡失效/无模板/全局重置时用）。</summary>
        private void ClearAllStaticRulesFor(string cardUid)
        {
            if (string.IsNullOrEmpty(cardUid)) return;
            for (int i = _areaCombatSkips.Count - 1; i >= 0; i--)
                if (string.Equals(_areaCombatSkips[i].CardUid, cardUid, System.StringComparison.Ordinal))
                    _areaCombatSkips.RemoveAt(i);
            _covertPowerExcludedBy.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal));
            _noUntapWithSealedCardsBy.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal));
            _extraPowerContestBy.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal));
            _nonOrgEntryBlockers.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal));   // 【P3·K21】JC064
            _noRevealWithLockCounterBy.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal));
            _damageRedirects.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal));
            ReleaseControlGainsFor(cardUid);   // 【P3·K4】持续夺控一并归还并清除（JC036）
            bool wasSuppressor = _iconSuppressors.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal)) > 0;
            if (wasSuppressor) ReapplyIconSuppressions();
            bool wasDomainSuppressor = _domainSuppressors.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal)) > 0;
            bool wasTextBlankSuppressor = _textBlankSuppressors.RemoveAll(u => string.Equals(u, cardUid, System.StringComparison.Ordinal)) > 0;
            if (wasDomainSuppressor || wasTextBlankSuppressor) ReapplySuppressions();
        }

        /// <summary>
        /// 注册一张卡的【持久效果】（D19 统一入口，替代原 RegisterContinuous）：
        ///  - Static（静止/禁止，如"不能被装备结附"）→ 按 Scope 置 CardInstance.StaticFlags（默认争夺区）。
        ///  - Continuous（持续，如"公开"/条件"白眼"）→ 逐条 ContinuousGrants 调 CardInstance.Grant；
        ///    带结构化 PrereqSpec 的，先 EvaluatePrereq：满足才授予，不满足登记进 _pendingPrereqCards 监听。
        /// 前提变化（资产区/忠诚变动）由 ReevaluatePrereqs 触发开关（见 D19-②）。
        ///
        /// 手牌期（如"公开"拦截派遣暗藏）仍由 HasPublicKeyword 双层补判保证，不依赖本方法。
        /// </summary>
        public void RegisterPersistent(CardInstance card)
        {
            if (card?.PlayerTemplate is not PlayerCardData tmpl) return;
            // 【P3·K20】"印刷文本框视作空白"（JC101）→ 该卡**失去效果**：不注册任何持续/静止效果
            // （用户口径："不影响已经发动的效果"——只是不再生效/不再产生新效果）。
            if (IsTextBlank(card)) return;
            // Z1 严格规则：附属卡仅 AttachedZone 生效。非 AttachedZone 的附属卡（如 AreaZone/资产/手牌/墓地）
            // 不注册持久效果，其持续/静止效果不生效。角色卡仍按原逻辑（进 AreaZone/AttachedZone 时注册）。
            // 【方案C 后续改动方向】此整卡判定将移除，改为内层 foreach 按每个效果的 Scope（IsEffectActiveInZone）
            // 做效果粒度判定——允许"显式声明 Scope 的效果"在其他区生效（如墓地/手牌效果）。
            // 当前 Z1 严格规则仍由本整卡快速返回兜底，行为不变。
            bool isAttachment = (tmpl.CategoryFlag & CardCategory.Attachment) != 0;
            if (isAttachment && card.CurrentZone != CardZone.AttachedZone)
            {
                GD.Print($"[EffectEngine][Z1] 附属卡 {tmpl.CardName}(UID={card.UID}) 不在附属区(Zone={card.CurrentZone})，跳过持久效果注册");
                return;
            }
            // 【用户口径·2026-09】"**暗藏者（背面朝上）的持续/静止效果也不生效**"——与既有的
            // 触发（`TriggerCovertMode` 默认 FaceUp）、激活（`ActivateEligibility` 硬门禁 G0 拒暗藏）、
            // 光环（本文件 `ReevaluateAuras` 里 `if (src.IsCovert) srcOnField = false`）**对齐**：
            // 背面朝上的卡**不提供任何持续/静止规则**（既不授予图标/属性/关键字，也不登记地区级静止规则、
            // 不封锁秘密派遣……）。现身（SetCovert(false)）时由装配层钩子重新注册即恢复；
            // 潜伏（SetCovert(true)）时注销（见 `GameInitializer` 的 CovertChanged 钩子）。
            // 说明：这样"卡面印刷规则"类查询（如 JC081 的结附白名单、JC057 的检视授权）也一并失效——
            // 那两处读的是印刷规则，故各自额外做了 `IsCovert` 判定（同口径，两端同解）。
            if (card.IsCovert)
            {
                GD.Print($"[EffectEngine] ◑ {tmpl.CardName}(UID={card.UID}) 处于**暗藏**状态 → 跳过持续/静止效果注册（背面朝上的卡不提供持续/静止规则）");
                return;
            }
            if (HasAnyContinuousFor(card.UID)) return; // 防重复注册（键＝卡 UID+效果 Id，用前缀判定）
            bool anyApplied = false;

            foreach (var e in EffectsOf(card))
            {
                if (e.Timing == EffectTiming.Static)
                {
                    // 静止/禁止效果：按生效区判定（默认争夺区/结附区；非对应区不生效）
                    // 【方案C】改为统一 IsEffectActiveInZone（效果粒度，尊重 e.Scope 声明）。
                    // 原旧逻辑：`if (e.Scope != ZoneScope.AreaZone) continue;` 已在下方收敛为 IsEffectActiveInZone，
                    // 未来移除时勿漏（Static 效果也须过生效区判定）。
                    if (!IsEffectActiveInZone(e, card)) continue;
                    foreach (var s in e.StaticEffects ?? new Array<StaticEffect>())
                    {
                        if (s == null) continue;
                        card.AddStaticFlag(s.Kind);
                        GD.Print($"[EffectEngine] ⊘ 静止效果 {card.PlayerTemplate?.CardName} ← {s.Kind}（{e.EffectId}）");
                    }
                }
                else if (e.Timing == EffectTiming.Continuous)
                {
                    // 【方案C】生效区判定：持续效果也须在生效区（当前整卡 Z1 已保证，此处为效果粒度兜底，
                    // 未来允许显式声明 Scope 的效果在其他区生效时，靠此判定 + 移除整卡 return）。
                    if (!IsEffectActiveInZone(e, card)) continue;
                    // 持续效果：前提判定
                    bool prereqMet = EvaluatePrereq(card, e);
                    if (!prereqMet)
                    {
                        _pendingPrereqCards.Add(card);
                        GD.Print($"[EffectEngine] ◷ 持续效果 {e.EffectId} 前提未满足，登记监听（{e.ContinuousEffect}）");
                        continue;
                    }
                    // 全局封锁秘密派遣（JC068）：带 BlockDeployCovert 且前提（未横置）满足 → 登记封锁对手派遣。
                    if (HasBlockDeployCovert(e))
                    {
                        SetBlockDeployCovert(card, active: true);
                        _activePrereqCards.Add(card); // 登记到已生效集合，供横置/竖置时 ReevaluatePrereqs 重判
                    }
                    // 【P3】静止规则登记（统一入口）：地区级"略过对抗"（JC098）/"敌方暗藏者不参与势力对抗"（JC023）/
                    // "额外进行一次势力对抗"（JC067）＋全局"封印角色不可被重置"（JC108）。
                    // 登记表按卡 UID 收口；查询一律按卡**当前**所在地/状态实时判定 → 移动换区自动跟随。
                    if (HasAnyStaticRule(e))
                    {
                        SetStaticRuleRegistrations(card, e, active: true);
                        _activePrereqCards.Add(card);   // 前提丧失/离场时经 ReevaluatePrereqs / UnregisterPersistent 注销
                    }
                    // 持续光环（D19-③）：条件群体光环（如 JC060），"他者+过滤"语义。
                    // 源卡自身不授予——ApplyAura 内已排除源卡并仅对符合条件的他者授予；
                    // 源卡若自带被排除的领域（如 JC060 自带心灵）本就不在影响范围内。
                    if (e.IsAura)
                    {
                        ApplyAura(card, e);
                        GD.Print($"[EffectEngine] 注册光环效果 {e.EffectId} @ {e.Scope}：{e.ContinuousEffect}（源卡自身不授予）");
                        continue;
                    }
                    // 【P3】持续性的**玩家级费用修正**（JC026 每回合第一张装备牌 -1 费 / JC033 现身费 -1）
                    if (e.ContinuousCostMod != null)
                    {
                        var cm = e.ContinuousCostMod;
                        bool renewable = cm.PerTurn;
                        string scope = string.IsNullOrEmpty(cm.CostScope) ? "打出" : cm.CostScope;
                        // 幂等：同一来源（卡 UID + 通道 + 过滤串）只登记一条——
                        // 该修正不进 `_continuousRegistry`，故 `HasAnyContinuousFor` 的去重护栏看不到它，
                        // 必须在此显式判重（否则重复 `RegisterPersistent` 会叠加成 -2）。
                        bool already = false;
                        foreach (var m in _timedModStack)
                            if (m.Kind == "CostMod" && string.Equals(m.SourceUid, card.UID, System.StringComparison.Ordinal)
                                && string.Equals(m.CostScope, scope, System.StringComparison.Ordinal)
                                && string.Equals(m.FilterText ?? "", cm.Filter ?? "", System.StringComparison.Ordinal))
                            { already = true; break; }
                        if (!already)
                            ApplyCostModification(card.ControllerId, cm.Amount, CurrentTurn, cm.Filter,
                                consumeOnce: renewable, renewable: renewable, sourceUid: card.UID,
                                costScope: scope);
                        if (!_activePrereqCards.Contains(card)) _activePrereqCards.Add(card);   // 来源离场时走注销清理
                        GD.Print($"[EffectEngine] ✓ 持续费用修正{(already ? "(已登记)" : "生效")}：玩家{card.ControllerId} {cm.Amount}" +
                                 $"（filter='{cm.Filter}'，通道={scope}，{(renewable ? "每回合可再生的一次性" : "整回合")}）");
                    }
                    // 【P3·图标镜像】`PrereqSpec.MirrorSealedIcons` 时不看卡脚本静态 Grants，
                    // 改为按"被封印在本卡上的角色牌"的印刷能力图标现算一批授予（JC066 封魔瓶瑟琳娜）。
                    bool mirrorIcons = e.PrereqSpec?.MirrorSealedIcons == true;
                    if (e.ContinuousGrants != null || mirrorIcons)
                    {                        // 【P3·F】缩放倍数：ScaleExpr（计数表达式，如"每操控一个暗藏者"）或 ScaleByLoyalty（领域忠诚）
                        int mult = GetPrereqScale(card, e.PrereqSpec);
                        // 【P3·D】授予对象：`GrantToHost`（"受此结附的角色获得X"）→ 宿主；否则来源卡自身
                        var grantTarget = ResolveGrantTarget(card, e);
                        if (grantTarget == null)
                        {
                            GD.Print($"[EffectEngine] ⚠ 持续效果 {e.EffectId} 声明授予宿主，但来源卡无宿主 → 不授予");
                            continue;
                        }
                        // 【修复·P3·B】逐效果分别登记（键＝卡 UID+效果 Id）：同卡多条条件持续效果互不干扰
                        var applied = new System.Collections.Generic.List<EffectGrant>();
                        var grantList = mirrorIcons
                            ? (System.Collections.Generic.IEnumerable<EffectGrant>)BuildMirroredIconGrants(card)
                            : e.ContinuousGrants;
                        foreach (var g in grantList)
                        {
                            if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                            int realAmount = g.Amount * mult;
                            grantTarget.Grant(g.Kind, g.Value, realAmount);
                            // 记录实际授予量（深拷贝），撤销时按 realAmount 精确还原（多源叠加计数要求）
                            applied.Add(new EffectGrant { Kind = g.Kind, Value = g.Value, Amount = realAmount });
                            GD.Print($"[EffectEngine] ✓ 持续授予 {grantTarget.PlayerTemplate?.CardName} ← {g.Kind}:{g.Value} (×{realAmount}{(mult != 1 ? $" = {g.Amount}×{mult}" : "")})");
                        }
                        if (mirrorIcons)
                        {
                            GD.Print($"[EffectEngine] 🪞 图标镜像 {card.PlayerTemplate?.CardName}：按 {card.SealedCards?.Count ?? 0} 张封印牌汇总 {applied.Count} 项能力图标");
                            // 【关键】镜像量随"封印牌集合"变化：即使当前 0 张（＝0 项授予）也必须进入重判全集，
                            // 否则 `applied.Count == 0` 会让本卡既不进 `_activePrereqCards` 也不进 `_pendingPrereqCards`
                            // → 之后封印落地时的 `ReevaluatePrereqs` 根本不会遍历到它，镜像永远补不上。
                            _activePrereqCards.Add(card);
                        }
                        if (applied.Count > 0)
                        {
                            _continuousRegistry[PrereqKey(card.UID, e.EffectId)] = new ContinuousGrantRecord
                            {
                                TargetUid = grantTarget.UID,
                                Grants = applied,
                            };
                            anyApplied = true;
                        }
                    }
                    GD.Print($"[EffectEngine] 注册持续效果 {e.EffectId} @ {e.Scope}：{e.ContinuousEffect}");
                }
            }

            if (anyApplied)
                _activePrereqCards.Add(card); // 登记到已生效集合，供 ReevaluatePrereqs 遍历
            // 【P3·K20】新进场的卡可能**本身带锁定标志**（应被在册的"持续失去图标"抑制）→ 重算一次
            if (_iconSuppressors.Count > 0) ReapplyIconSuppressions();
        }

        /// <summary>持续前提登记键＝**卡 UID + 效果 Id**（同卡多条条件持续效果必须分别登记/还原）。</summary>
        private static string PrereqKey(string cardUid, string effectId) => cardUid + "#" + effectId;

        /// <summary>
        /// 【P3·D】持续授予的**落点**：`EffectData.GrantToHost=true`（卡文"受此结附的角色获得X"）→ 宿主卡；
        /// 其余 → 来源卡自身。无宿主时返回 null（调用方按"不授予"处理，用户 2026-09 口径）。
        /// </summary>
        private static CardInstance ResolveGrantTarget(CardInstance card, EffectData e)
        {
            if (e?.GrantToHost != true) return card;
            return card?.AttachedTo;
        }

        /// <summary>按登记记录精确还原一条持续授予（落点由记录里的 `TargetUid` 决定，不依赖当时的结附关系）。</summary>
        private void RevokeContinuousRecord(ContinuousGrantRecord rec, CardInstance sourceCard)
        {
            if (rec == null) return;
            var target = string.IsNullOrEmpty(rec.TargetUid)
                ? sourceCard
                : (CardManagerRef?.GetInstance(rec.TargetUid) ?? sourceCard);
            if (target == null) return;
            foreach (var g in rec.Grants) target.Revoke(g.Kind, g.Value, g.Amount);
        }

        /// <summary>该卡是否有任何已登记的持续授予（前缀匹配）。</summary>
        private bool HasAnyContinuousFor(string cardUid)
        {
            if (string.IsNullOrEmpty(cardUid)) return false;
            string prefix = cardUid + "#";
            foreach (var k in _continuousRegistry.Keys)
                if (k.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>移除该卡的全部持续登记（不做还原；还原场景请显式 Revoke）。</summary>
        private void RemoveAllContinuousFor(string cardUid)
        {
            if (string.IsNullOrEmpty(cardUid)) return;            string prefix = cardUid + "#";
            var keys = new System.Collections.Generic.List<string>();
            foreach (var k in _continuousRegistry.Keys)
                if (k.StartsWith(prefix, System.StringComparison.Ordinal)) keys.Add(k);
            foreach (var k in keys) _continuousRegistry.Remove(k);
            // 【P3】全部静止规则登记（地区级 + 全局）按卡 UID 一并注销（该卡已不可用/无模板）
            ClearAllStaticRulesFor(cardUid);
        }

        /// <summary>
        /// 【P3·F】该持续效果是否声明了"结构化前提/缩放"（`PrereqSpec.Kind` 有效 **或** 声明了 `ScaleExpr`）。
        /// </summary>
        private static bool HasPrereqSpec(EffectData e)
            => e?.PrereqSpec != null
            && (e.PrereqSpec.Kind != PrereqKind.None || !string.IsNullOrWhiteSpace(e.PrereqSpec.ScaleExpr)
                || e.PrereqSpec.MirrorSealedIcons);

        /// <summary>
        /// 【P3·F】持续效果的"前提/缩放倍数"：`ScaleExpr`（计数表达式，如"每操控一个暗藏者"）优先，
        /// 否则 `ScaleByLoyalty`（领域忠诚点数），再否则 1。
        /// 返回的 N 同时用于：① 前提是否成立（`ScaleExpr`/`ScaleByLoyalty` 时 N&gt;0 即成立）；② 授予量 ×N。
        /// </summary>
        private int GetPrereqScale(CardInstance card, ContinuousPrereqSpec spec)
        {
            if (spec == null) return 1;
            if (!string.IsNullOrWhiteSpace(spec.ScaleExpr))
            {
                var ctx = new EffectValueResolver.ValueContext
                {
                    Source = card,
                    Target = null,
                    TargetAreaId = card?.ZoneDetailId,
                    EventAmount = -1,
                    ZoneManager = ZoneManagerRef,
                    CardManager = CM,
                    PlayerManager = TurnManagerRef?.PlayerManagerRef,
                    AreaManager = TurnManagerRef?.AreaManagerRef,
                };
                var st = EffectValueResolver.Evaluate(spec.ScaleExpr, in ctx, out int v, out string why);
                if (st != EffectValueResolver.ValueEvalResult.Ok)
                {
                    GD.PushWarning($"[EffectEngine] 持续效果缩放表达式求值失败（{spec.ScaleExpr}）：{why} → 按 0 处理");
                    return 0;
                }
                return v;
            }
            return spec.ScaleByLoyalty ? GetPrereqLoyaltyValue(card, spec) : 1;
        }

        /// <summary>读取某卡控制者在指定区域（默认资产区）的某领域忠诚点数 N（线性计量用）。</summary>
        private int GetPrereqLoyaltyValue(CardInstance card, ContinuousPrereqSpec spec)
        {
            var pm = TurnManagerRef?.PlayerManagerRef;
            if (pm == null)
            {
                // 【P0·缺依赖要吵出来】原实现静默 return 0：前提被判"不满足"，持续效果（白眼/黑刀/光环）整块不生效，
                // 且日志上看不出原因（历史上探针即因此把白眼判成未生效）。这里显式告警。
                GD.PushWarning($"[EffectEngine] 持续效果前提判定需要本局 PlayerManager（经 TurnManagerRef 取），" +
                    $"但未注入 → 忠诚按 0 处理，效果 {card?.PlayerTemplate?.CardName} 的前提将判为不满足");
                return 0;
            }
            var loyalty = pm.GetDomainLoyalty(card.ControllerId);
            loyalty.TryGetValue(spec.Domain, out int v);
            return v;
        }

        /// <summary>卡类型默认生效区（方案C 声明式 Scope 的默认推导）。
        /// 未声明 Scope（Specified）的效果回退到此处：附属→AttachTarget（严格=仅 AttachedZone）；角色→AreaZone（仅 AreaZone）。
        /// 未来扩展（墓地/手牌生效等）在此加判断，或让具体效果声明 Scope 覆盖。</summary>
        /// <summary>纯函数：卡类型默认生效区（**秘社→秘社区**，附属→AttachTarget，其余玩家卡→AreaZone）。
        /// 静态化目的：客户端（无 EffectEngine 实例）复用同一判定，避免复制规则分叉。</summary>
        public static ZoneScope GetDefaultScope(CardInstance card)
        {
            // 【P0·修复】秘社卡：`CouncilCardData` 不是 `PlayerCardData` ⇒ `PlayerTemplate == null`。
            // 原先直接返回 `Specified`，而 `ScopeMatchesZone(Specified, …)` 恒 false ⇒ 秘社激活在
            // `ActivateAbility` 的生效区门禁处被**无条件拒绝**（"能点、点完没反应"）。
            if (card?.CouncilTemplate is CouncilCardData) return ZoneScope.CouncilZone;
            if (card?.PlayerTemplate is not PlayerCardData tmpl) return ZoneScope.Specified;
            bool isAttachment = (tmpl.CategoryFlag & CardCategory.Attachment) != 0;
            return isAttachment ? ZoneScope.AttachTarget : ZoneScope.AreaZone;
        }

        /// <summary>某效果在其来源卡当前所在区是否生效（方案C 统一判定，替代三处散落的"卡类型+CurrentZone"）。
        /// 判定基准：效果声明的 Scope；未声明（Specified）→ 卡类型默认区。
        /// 接入点：RegisterPersistent（持续/静止）、TriggerRegistry（触发）、ActivateEligibility（激活）。
        /// 注意：AttachTarget 采用严格规则=仅 AttachedZone（Z1/Z4，AreaZone 附属暂不生效）。
        /// 未来加"某区生效"只需在 switch 增分支，勿在调用处重复写区判断（避免遗漏）。</summary>
        /// <summary>纯函数：某效果在其来源卡当前所在区是否生效（静态化，客户端亦可直接调用）。</summary>
        /// <param name="zoneOverride">【触发语义】"触发条件满足时"来源卡所在的区域（null=只按实时 CurrentZone 判定）。
        /// 用于死亡触发（卡已在墓地才有窗口）/进场即死（进场触发窗口打开时卡已在墓地）等场景——
        /// 触发只看条件是否满足过，不看来源此刻的所在区。仅由 TriggerRegistry 传入，其余调用方传 null。
        /// **判定为"实时区域 或 触发时区域"任一命中**（OR），这样两种写法都成立：
        ///  · 角色卡不声明 Scope（默认生效区=争夺区）→ 实时在墓地不匹配，但触发时在争夺区 → 命中；
        ///  · 卡显式声明 `Scope=Graveyard`（"在墓地触发"）→ 实时在墓地 → 命中。
        /// 两者取并集，避免"卡片作者必须猜对声明方式"的坑。</param>
        public static bool IsEffectActiveInZone(EffectData e, CardInstance card, CardZone? zoneOverride = null,
            string detailOverride = null)
        {
            if (card == null || e == null) return false;
            ZoneScope scope = (e.Scope == ZoneScope.Specified) ? GetDefaultScope(card) : e.Scope;
            if (ScopeMatchesZone(scope, card.CurrentZone)) return true;                       // 实时区域
            if (zoneOverride.HasValue && ScopeMatchesZone(scope, zoneOverride.Value)) return true; // 触发时区域
            return false;
        }

        /// <summary>某生效区声明是否匹配给定区域（`Specified` 视为不匹配，由调用方先解析默认区）。</summary>
        private static bool ScopeMatchesZone(ZoneScope scope, CardZone zone)
        {
            switch (scope)
            {
                case ZoneScope.AreaZone:     return zone == CardZone.AreaZone;
                case ZoneScope.AttachTarget: return zone == CardZone.AttachedZone; // 严格版（Z1/Z4）
                case ZoneScope.HandZone:     return zone == CardZone.Hand;
                case ZoneScope.AssetZone:    return zone == CardZone.AssetZone;
                case ZoneScope.Graveyard:    return zone == CardZone.Graveyard;
                case ZoneScope.DeckZone:     return zone == CardZone.Deck;
                // 【P3·K16 触发版】封印区："被封印在一个角色上时…"类自身触发（JC110/JC100）。
                // 此前**无此分支** → 该类效果的来源卡此刻在封印区、默认区（角色=争夺区）不匹配，
                // 于是"被封印时"触发在生产路径上**永远收集不到**（只有手工结算效果的单测能过）。
                case ZoneScope.SealedZone:   return zone == CardZone.SealedZone;
                // 【P0·修复】秘社卡默认生效区（`GetDefaultScope` 对 `CouncilCardData` 返回本值）
                case ZoneScope.CouncilZone:  return zone == CardZone.CouncilZone;
                default:                     return false;
            }
        }

        /// <summary>结构化前提判定（D19-②）。PrereqSpec 优先；无则回退旧文本（仅"常驻"生效）。
        /// 注意：ScaleByLoyalty=true 时视为"线性计量"——N&gt;0 即满足，授予量由调用方按 N 缩放。</summary>
        public bool EvaluatePrereq(CardInstance card, EffectData e)
        {
            // 【P3·F】有结构化前提（Kind 有效 **或** 仅声明了 ScaleExpr）→ 走结构化判定；
            // 否则回退旧的"持续前提"文本（仅"常驻"生效）。
            if (HasPrereqSpec(e))
            {
                var spec = e.PrereqSpec;
                // 【P3·图标镜像】前提＝**常驻**（授予量由"封印牌集合"决定，不是"前提是否满足"）
                if (spec.MirrorSealedIcons) return true;
                // 【P3·F】按计数缩放（ScaleExpr）：N>0 即前提成立，授予量由调用方按 N 缩放
                if (!string.IsNullOrWhiteSpace(spec.ScaleExpr))
                    return GetPrereqScale(card, spec) > 0;
                if (spec.Kind == PrereqKind.DomainLoyalty)
                {
                    // 查询控制者在该区域（默认资产区）的某领域忠诚
                    int n = GetPrereqLoyaltyValue(card, spec);
                    if (spec.ScaleByLoyalty) return n > 0;          // 线性计量：有 1 点即满足
                    return n >= spec.Amount;                         // 布尔开关：≥阈值才满足
                }
                if (spec.Kind == PrereqKind.SelfUntapped)
                {
                    // 前提：来源卡自身未横置（JC068 阿格斯IV"只要未横置"）
                    return !card.IsTapped;
                }
                if (spec.Kind == PrereqKind.HostKind)
                {
                    // 【P3·G 扩展】前提：本卡的**结附宿主**属于指定大类（`Param` = `"角色"`/`"秘社"`）。
                    // 用于 JC082 索亚之书「选择一项：·借阅：结附于目标角色（宿主获得白眼）/·收藏：结附于本方秘社」——
                    // 两种形态的效果按**宿主种类**区分（打出时选的结附目标即"选择的一项"）。
                    // 无宿主（未结附）→ 不满足。
                    string host = spec.Param?.Trim() ?? "";
                    var h = card.AttachedTo;
                    if (h == null) return false;
                    var hostKind = CardManager.MapInstanceToTargetKind(h);
                    bool met = host == "角色" ? hostKind == TargetKind.Character
                        : host == "秘社" ? hostKind == TargetKind.Council
                        : false;
                    if (host == "" || (host != "角色" && host != "秘社"))
                    {
                        GD.PushWarning($"[EffectEngine] 持续前提 HostKind 的 Param「{host}」未登记（支持 角色/秘社）→ 判为不满足");
                        return false;
                    }
                    return met;
                }
                if (spec.Kind == PrereqKind.NoEnemyCharacterInArea)
                {
                    // 【P3·B】前提：来源卡所在地区**没有敌方角色**（JC016 执行部精锐）。
                    // "角色"＝争夺区正面角色（`TargetKind.Character`）；暗藏者不算角色。
                    string area = card.ZoneDetailId;
                    if (string.IsNullOrEmpty(area)) return false;
                    var zm = ZoneManagerRef;
                    if (zm == null)
                    {
                        GD.PushWarning("[EffectEngine] 持续前提 NoEnemyCharacterInArea 需要 ZoneManager，但未注入 → 判为不满足");
                        return false;
                    }
                    foreach (var c in zm.GetZoneCards(CardZone.AreaZone, 0, area))
                    {
                        if (c == null || c.IsDying) continue;
                        if (c.ControllerId == card.ControllerId) continue;      // 只关心敌方
                        if (CardManager.MapInstanceToTargetKind(c) == TargetKind.Character) return false;
                    }
                    return true;
                }
                if (spec.Kind == PrereqKind.AreaMarkOwner)
                {
                    // 【P3·B】前提：来源卡所在地区有**指定归属**的势力标志 ≥ Amount（缺省 1）（JC084 街头混混）。
                    string area = card.ZoneDetailId;
                    if (string.IsNullOrEmpty(area)) return false;
                    var am = TurnManagerRef?.AreaManagerRef;
                    if (am == null)
                    {
                        GD.PushWarning("[EffectEngine] 持续前提 AreaMarkOwner 需要 AreaManager，但未注入 → 判为不满足");
                        return false;
                    }
                    int who = (spec.Param == "对手")
                        ? (card.ControllerId == 1 ? 2 : 1)
                        : card.ControllerId;
                    int need = spec.Amount > 0 ? spec.Amount : 1;
                    return am.GetPowerMark(area, who) >= need;
                }
                return false;
            }
            // 兼容旧文本前提
            return string.IsNullOrEmpty(e.ContinuousPrereq) || e.ContinuousPrereq == "常驻";
        }

        /// <summary>判断某持续效果是否携带"全局封锁秘密派遣"（BlockDeployCovert）静止标记（JC068）。
        /// 效果B 用 Continuous+PrereqSpec(SelfUntapped) 表达动态前提，StaticEffects 承载封锁意图。</summary>
        private bool HasBlockDeployCovert(EffectData e)
        {
            if (e?.StaticEffects == null) return false;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.BlockDeployCovert) return true;
            return false;
        }

        /// <summary>按来源卡登记/撤销"封锁其控制者的对手秘密派遣"（JC068 阿格斯IV）。
        /// active=true：前提（未横置）满足，把 controlPlayer 的对手记入封锁表；active=false：移除。
        /// 规则：阿格斯IV的控制者为 X，则 X 的对手（另一玩家）被封锁。</summary>
        private void SetBlockDeployCovert(CardInstance card, bool active)
        {
            if (card == null) return;
            int opponent = card.ControllerId == 1 ? 2 : 1;
            if (active)
            {
                _blockDeployCovertPlayers.Add(opponent);
                GD.Print($"[EffectEngine] ⛔ 阿格斯IV未横置：封锁玩家{opponent} 秘密派遣行动");
            }
            else
            {
                _blockDeployCovertPlayers.Remove(opponent);
                GD.Print($"[EffectEngine] ✓ 阿格斯IV已横置/离场：解除对玩家{opponent} 的秘密派遣封锁");
            }
        }

        /// <summary>前提变化重判（D19-②）：资产区/忠诚变动或控制者变化（跨区 MOVE）后调用，
        /// 对「已生效」与「待定」的持续效果卡全集用当前控制者重新判定，开关授予。
        /// 注意：必须包含 _continuousRegistry 中的卡，否则已生效的卡在控制者变更/资产减少后不会被撤销。</summary>
        public void ReevaluatePrereqs(int playerId)
        {
            // 全集 = 已生效(activePrereqCards) + 待定(pending)。去重后逐张用当前 ControllerId 重判。
            var allCards = new System.Collections.Generic.HashSet<CardInstance>(_pendingPrereqCards);
            foreach (var c in _activePrereqCards) allCards.Add(c);
            if (allCards.Count == 0) return;

            foreach (var card in allCards.ToList())
            {
                if (card == null || card.ControllerId != playerId) continue;
                var tmpl = card.PlayerTemplate as PlayerCardData;
                if (tmpl == null) { _pendingPrereqCards.Remove(card); RemoveAllContinuousFor(card.UID); continue; }

                bool anyNowMet = false;
                foreach (var e in EffectsOf(card))
                {
                    if (e.Timing != EffectTiming.Continuous) continue;
                    if (!HasPrereqSpec(e)) continue;
                    if (EvaluatePrereq(card, e))
                    {
                        anyNowMet = true;
                        // 【P3·F】缩放倍数：即使已生效也要按最新 N 重算（N 增减 → 授予量增减）
                        int mult = GetPrereqScale(card, e.PrereqSpec);
                        // 【修复·P3·B】登记键＝**卡 UID + 效果 Id**（原来是"仅卡 UID"）：
                        // 同一张卡有多条"条件持续效果"时（如 JC084 白星/白眼两条），按卡登记的旧写法会让
                        // 后一条"前提不满足"的还原逻辑把前一条刚授予的图标一起撤掉。
                        string key = PrereqKey(card.UID, e.EffectId);
                        bool alreadyActive = _continuousRegistry.ContainsKey(key);
                        // 【P3·图标镜像】镜像量随"封印牌集合"变化 → 即使 mult==1 也必须撤销重算
                        bool mirrorIcons = e.PrereqSpec?.MirrorSealedIcons == true;
                        if (alreadyActive && mult == 1 && !mirrorIcons) continue; // 非缩放且已生效：维持

                        // 已生效的线性计量效果：先按旧量撤销，再按新量授予，避免 CurrentStrength 等重复叠加
                        if (alreadyActive)
                        {
                            if (_continuousRegistry.TryGetValue(key, out var oldApplied))
                                RevokeContinuousRecord(oldApplied, card);
                            _continuousRegistry.Remove(key);
                        }

                        // 【P3·D】授予落点：`GrantToHost` → 宿主卡；否则来源卡自身
                        var grantTarget = ResolveGrantTarget(card, e);
                        if (grantTarget == null)
                        {
                            GD.Print($"[EffectEngine] ⚠ 持续效果 {e.EffectId} 声明授予宿主，但来源卡无宿主 → 不授予");
                            continue;
                        }
                        var applied = new System.Collections.Generic.List<EffectGrant>();
                        // 【P3·图标镜像】授予源改写：按"被封印在本卡上的角色牌"现算能力图标（JC066）
                        var grantList = mirrorIcons
                            ? (System.Collections.Generic.IEnumerable<EffectGrant>)BuildMirroredIconGrants(card)
                            : (System.Collections.Generic.IEnumerable<EffectGrant>)(e.ContinuousGrants ?? new Array<EffectGrant>());
                        foreach (var g in grantList)
                        {
                            if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                            int realAmount = g.Amount * mult;
                            grantTarget.Grant(g.Kind, g.Value, realAmount);
                            applied.Add(new EffectGrant { Kind = g.Kind, Value = g.Value, Amount = realAmount });
                        }
                        if (applied.Count > 0)
                        {
                            _continuousRegistry[key] = new ContinuousGrantRecord
                            {
                                TargetUid = grantTarget.UID,
                                Grants = applied,
                            };
                            _activePrereqCards.Add(card); // 登记到已生效集合，供 ReevaluatePrereqs 遍历
                        }
                        else if (mirrorIcons)
                            _activePrereqCards.Add(card); // 镜像 0 项授予时也要留在重判全集（见 RegisterPersistent 同款说明）
                        GD.Print($"[EffectEngine] ⟳ 前提达成{(alreadyActive ? "(按N重算)" : "")}，持续效果 {e.EffectId} 现已生效 (×{mult})");
                        // 全局封锁秘密派遣（JC068）：前提（未横置）满足 → 登记封锁对手派遣（Set 幂等，重复调用无害）
                        if (HasBlockDeployCovert(e))
                            SetBlockDeployCovert(card, active: true);
                        // 【P3】静止规则（JC098 略过对抗 / JC023 暗藏者不参与势力对抗 / JC067 额外势力对抗 /
                        //       JC108 封印角色不可被重置）：前提满足 → 统一登记（幂等）
                        if (HasAnyStaticRule(e))
                            SetStaticRuleRegistrations(card, e, active: true);
                    }
                    else
                    {
                        // 只还原**本条效果**的授予（键含 EffectId），不动同卡其它条件持续效果
                        string key = PrereqKey(card.UID, e.EffectId);
                        if (_continuousRegistry.TryGetValue(key, out var appliedRec))
                        {
                            RevokeContinuousRecord(appliedRec, card);
                            _continuousRegistry.Remove(key);
                            GD.Print($"[EffectEngine] ⟳ 前提丧失，持续效果 {e.EffectId} 已还原");
                        }
                        // 全局封锁秘密派遣（JC068）：前提（已横置）丧失 → 解除封锁（Set 幂等）
                        if (HasBlockDeployCovert(e))
                            SetBlockDeployCovert(card, active: false);
                        // 【P3】静止规则：前提丧失 → 统一注销
                        if (HasAnyStaticRule(e))
                            SetStaticRuleRegistrations(card, e, active: false);
                    }
                }
                // 已满足则移出待定集合；仍不满足则留在待定集合等待后续资产变动
                if (anyNowMet) _pendingPrereqCards.Remove(card);
                else _pendingPrereqCards.Add(card);
            }
            // 持续光环（D19-③）重算：区域变动（角色进场/离场/夺控）后同步刷新所有光环的受影响集合
            ReevaluateAuras(playerId);
            // 【P3·K20】"失去魔法领域图标"的受影响集合也随局面变化重算（封印落地/解除、横置、进出场都经本方法）
            ReapplySuppressions();
            // 【P3·K13】运行期授予的激活能力同款重算（来源进出场/前提变化 → 能力随即增减）
            ReapplyGrantedAbilities();
        }

        /// <summary>注销一张卡的持久效果（卡离场/被封印/被消灭时调用，精确还原 + 清除静止标记）。</summary>
        public void UnregisterPersistent(CardInstance card)
        {
            if (card == null) return;
            _pendingPrereqCards.Remove(card);
            _activePrereqCards.Remove(card);
            // 【修复·P3·B】按"卡 UID + 效果 Id"逐条还原本卡的全部持续授予
            string prefix = card.UID + "#";
            var keys = new System.Collections.Generic.List<string>();
            foreach (var k in _continuousRegistry.Keys)
                if (k.StartsWith(prefix, System.StringComparison.Ordinal)) keys.Add(k);
            int revokedCount = 0;
            foreach (var k in keys)
            {
                RevokeContinuousRecord(_continuousRegistry[k], card);
                revokedCount += _continuousRegistry[k].Grants.Count;
                _continuousRegistry.Remove(k);
            }
            if (revokedCount > 0)
                GD.Print($"[EffectEngine] 注销持续效果 UID={card.UID}（已还原 {revokedCount} 项授予）");
            SetAreaCombatSkip(card, null, active: false);   // 【P3·K18】离场即注销"地区略过对抗"登记
            ClearAllStaticRulesFor(card.UID);               // 【P3】其余静止规则登记一并按卡 UID 注销
            // 【P3】**持续费用修正**（JC026 每回合首张装备 -1 / JC033 现身费 -1）：时效终结＝来源卡离场
            // → 按 SourceUid 清理（这类修正不进 _continuousRegistry，故需单独收口）。
            // 可再生（每回合重置）与"整回合"两种都要清；只有一次性费用修正（无 SourceUid）允许游离存在。
            for (int i = _timedModStack.Count - 1; i >= 0; i--)
            {
                var m = _timedModStack[i];
                if (m.Kind != "CostMod") continue;
                if (string.IsNullOrEmpty(m.SourceUid)) continue;
                if (!string.Equals(m.SourceUid, card.UID, System.StringComparison.Ordinal)) continue;
                _timedModStack.RemoveAt(i);
                GD.Print($"[EffectEngine] ⌛ 持续费用修正随来源离场清理（玩家{m.PlayerId}，通道={m.CostScope}，filter='{m.FilterText}'）");
                Script.Manager.UiNotificationBus.Instance?.NotifyCostModChanged();
            }
            // 持续光环（D19-③）还原：源卡离场时，撤销它对其他角色的所有授予
            if (_auraGrants.TryGetValue(card.UID, out var grantedTo))
            {
                // 先在本区域找；找不到则经 CardManager 全局按 UID 找回（夺控后目标易主仍要还原）
                var areaCards = ZoneManagerRef?.GetZoneCards(CardZone.AreaZone, card.ControllerId)
                    ?? new System.Collections.Generic.List<CardInstance>();
                foreach (var kv in grantedTo)
                {
                    var t = areaCards.FirstOrDefault(c => c.UID == kv.Key);
                    if (t == null) t = CardManagerRef?.GetInstance(kv.Key);
                    if (t != null)
                        foreach (var g in kv.Value)
                            t.Revoke(g.Kind, g.Value, g.Amount);
                }
                _auraGrants.Remove(card.UID);
            }
            // 全局封锁秘密派遣（JC068）：阿格斯IV离场/被消灭时解除对对手的派遣封锁
            // 【P3·K18】地区级"略过对抗"（JC098）：离场即注销（查询按实时所在地判定，故注销＝彻底失效）
            if (card.PlayerTemplate is PlayerCardData tmpl2)
            {
                foreach (var e in tmpl2.EffectDataList)
                {
                    if (e.Timing == EffectTiming.Continuous && HasBlockDeployCovert(e))
                        SetBlockDeployCovert(card, active: false);
                    if (e.Timing == EffectTiming.Continuous && HasAnyStaticRule(e))
                        SetStaticRuleRegistrations(card, e, active: false);
                }
            }
            // 清除静止标记（D19-⑦）
            if (card.PlayerTemplate is PlayerCardData tmpl)
            {
                foreach (var e in EffectsOf(card))
                {
                    if (e.Timing == EffectTiming.Static)
                        foreach (var s in e.StaticEffects ?? new Array<StaticEffect>())
                            if (s != null) card.RemoveStaticFlag(s.Kind);
                }
            }
        }

        /// <summary>持续光环（D19-③）应用：对源卡控制者争夺区内"符合条件"的其他角色授予 e.ContinuousGrants。
        /// 条件：target != 源卡 且 target 当前实例 MagicDomains 不含 e.AuraFilterExcludeDomain（如 JC060 排除已有心灵领域者）。
        /// 受影响的(源→他者)授予记录进 _auraGrants，供精确还原。</summary>
        /// <summary>
        /// 【D19-③ + P3·E】光环目标是否合格（**三处共用**：首次注册 `ApplyAura`、重算时的撤销判定与补授判定）。
        /// 全部满足才授予：
        ///  · 不是源卡自身（"其他"）；
        ///  · 目标在场上且**正面朝上**（暗藏者不受光环）；
        ///  · 领域筛选（沿用既有 JC060 口径）：`AuraRequiresNoDomain=true` → 目标必须没有任何魔法领域；
        ///    否则排除已含 `AuraFilterExcludeDomain` 者；
        ///  · 【P3·E】**组织筛选**：`AuraFilterOrganization` 非空 → 目标模板 `Organization` 须与其相同；
        ///  · 【P3·E】**关键词筛选**：`AuraFilterKeyword` 非空 → 目标须具有该关键字（运行期授予 ∪ 卡面固有）；
        ///  · 【P3·E】**仅本地区**：`AuraLocalZoneOnly=true` → 目标须与来源卡在同一地区。
        /// </summary>
        private static bool IsAuraTargetEligible(EffectData auraEffect, CardInstance src, CardInstance t)
        {
            if (auraEffect == null || t == null || src == null) return false;
            if (ReferenceEquals(t, src)) return false;
            if (t.IsCovert) return false;
            bool excludedByDomain = auraEffect.AuraRequiresNoDomain
                ? t.MagicDomains.Count > 0
                : t.MagicDomains.Contains(auraEffect.AuraFilterExcludeDomain);
            if (excludedByDomain) return false;
            if (!string.IsNullOrEmpty(auraEffect.AuraFilterOrganization))
            {
                string org = (t.PlayerTemplate as PlayerCardData)?.Organization ?? "";
                if (!string.Equals(org, auraEffect.AuraFilterOrganization, System.StringComparison.Ordinal))
                    return false;
            }
            if (!string.IsNullOrEmpty(auraEffect.AuraFilterKeyword))
            {
                var tmpl = t.PlayerTemplate as PlayerCardData;
                bool hasKw = t.HasKeyword(auraEffect.AuraFilterKeyword)
                    || (tmpl?.HasInherentKeyword(auraEffect.AuraFilterKeyword) ?? false);
                if (!hasKw) return false;
            }
            if (auraEffect.AuraLocalZoneOnly)
            {
                if (string.IsNullOrEmpty(src.ZoneDetailId)) return false;
                if (!string.Equals(t.ZoneDetailId ?? "", src.ZoneDetailId, System.StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 【D19-③】持续光环注册（源卡进场时）：把光环授予施加到"该控制者争夺区内符合条件的其他角色"。
        /// 过滤条件统一在 <see cref="IsAuraTargetEligible"/>（含 P3·E 的组织/关键词/仅本地区筛选）。
        /// </summary>
        private void ApplyAura(CardInstance source, EffectData e)
        {
            if (source == null || e?.ContinuousGrants == null) return;
            var zm = ZoneManagerRef;
            if (zm == null) return;
            var targets = zm.GetZoneCards(CardZone.AreaZone, source.ControllerId);
            if (!_auraGrants.ContainsKey(source.UID))
                _auraGrants[source.UID] = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>();
            var grantedTo = _auraGrants[source.UID];
            int count = 0;
            foreach (var t in targets)
            {
                if (t == null || t == source) continue;
                // 过滤条件统一在 `IsAuraTargetEligible`（领域筛选 + P3·E 组织/关键词/仅本地区）
                if (!IsAuraTargetEligible(e, source, t))
                {
                    GD.Print($"[光环LOG] 跳过(不符合光环条件) {t.PlayerTemplate?.CardName} 领域数={t.MagicDomains.Count} " +
                             $"组织={(t.PlayerTemplate as PlayerCardData)?.Organization} 地区={t.ZoneDetailId} AuraRequiresNoDomain={e.AuraRequiresNoDomain}");
                    continue;
                }
                ApplyAuraGrantsTo(source, e, t, grantedTo);   // 单一实现（首次授予 / 自愈补授；内部负责建登记项）
                count++;
            }
        }

        /// <summary>
        /// 【自愈辅助】某条光环授予当前是否**确实还在**目标卡上（用于发现"换区清空"造成的登记表/实况不一致）。
        /// </summary>
        private static bool IsGrantPresent(CardInstance t, EffectGrant g)
        {
            if (t == null || g == null || string.IsNullOrEmpty(g.Value)) return true;
            switch (g.Kind)
            {
                case "Keyword": return t.HasKeyword(g.Value);
                case "Icon": return t.HasIcon(g.Value);
                case "Attribute":
                {
                    // 【P3】**符号感知**：负值授予（如 JC089「获得-1防御」）在属性表里记成负计数，
                    // 若仍按 `> 0` 判定"已授予"，自愈路径每次重算都会再授一次 → 防御被反复扣减。
                    int cur = t.GetAttribute(g.Value);
                    return g.Amount < 0 ? cur < 0 : cur > 0;
                }
                case "SubCategory": return t.HasSubCategory(g.Value);
                // 【P3·K4】"被改写掉的子类别"（JC036 初拥）：生效＝该类别当前**不在**实例集合里；
                // 若被 `ResetToBaseState`（离场复位）恢复了，则视为缺失 → 自愈路径重新抹掉。
                case "SubCategoryRemove": return !t.HasSubCategory(g.Value);
                case "MagicDomain":
                    return System.Enum.TryParse<MagicDomain>(g.Value, out var d) && t.MagicDomains.Contains(d);
                default: return true;   // 未知种类不介入（保持旧行为）
            }
        }

        /// <summary>
        /// 【D19-③ 单一实现】把光环授予施加到**一个**目标身上（首次注册 = 全量授予；已登记 = 逐条校验并自愈补授）。
        /// 幂等：重复调用不会重复授予，也不会把"已授予但被清空"的目标漏掉
        /// （清空来源：`CardInstance.ResetToBaseState()`＝换区/现身复位会清掉运行期授予）。
        /// </summary>
        private void ApplyAuraGrantsTo(CardInstance source, EffectData auraEffect, CardInstance t,
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>> grantedTo)
        {
            if (t == null || auraEffect == null || grantedTo == null) return;
            if (!grantedTo.ContainsKey(t.UID))
            {
                grantedTo[t.UID] = new System.Collections.Generic.List<EffectGrant>();
                foreach (var g in auraEffect.ContinuousGrants ?? new Array<EffectGrant>())
                {
                    if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                    t.Grant(g.Kind, g.Value, g.Amount);
                    grantedTo[t.UID].Add(g);
                    GD.Print($"[EffectEngine] ✦ 光环补授 {source?.PlayerTemplate?.CardName} → {t.PlayerTemplate?.CardName} ← 「{g.Kind}」:「{g.Value}」(amt={g.Amount}) uid={t.UID}");
                }
                return;
            }
            // 已登记：校验授予是否仍在卡上（被复位清空过 → 自愈重授）
            foreach (var g in grantedTo[t.UID])
            {
                if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                if (IsGrantPresent(t, g)) continue;
                t.Grant(g.Kind, g.Value, g.Amount);
                GD.Print($"[EffectEngine] ♻ 光环自愈补授（复位清空后恢复）{source?.PlayerTemplate?.CardName} → {t.PlayerTemplate?.CardName} ← 「{g.Kind}」:「{g.Value}」");
            }
        }

        /// <summary>持续光环重算（D19-③）：某玩家区域变动（角色进场/离场/夺控）后调用，对所有含光环的源卡重算其受影响集合。
        /// 差量：新符合条件者补授；已不符合者（离场/夺控/已获领域）撤销本源授予。挂在 ZoneManager.OnPostMove / 夺控后。</summary>
        public void ReevaluateAuras(int playerId)
        {
            var zm = ZoneManagerRef;
            if (zm == null) return;
            GD.Print($"[光环LOG] ReevaluateAuras(p={playerId}) 光环源记录数={_auraGrants.Keys.Count}");
            foreach (var srcUid in _auraGrants.Keys.ToList())
            {
                // 全局按 UID 找回源卡（夺控后可能已属对手，也能找到）
                var src = CardManagerRef?.GetInstance(srcUid);
                if (src == null) continue;
                var tmpl = src.PlayerTemplate as PlayerCardData;
                if (tmpl == null) continue;
                // 找该源卡的光环 EffectData（IsAura == true 的持续效果）
                EffectData auraEffect = null;
                foreach (var ef in tmpl.EffectDataList)
                    if (ef.Timing == EffectTiming.Continuous && ef.IsAura) { auraEffect = ef; break; }
                if (auraEffect == null) continue;

                var grantedTo = _auraGrants[srcUid];
                // 关键不变量：某授予以"源卡当前控制者 src.ControllerId 的区域"为有效基准，与本次传入 playerId 无关。
                // 这样无论夺控后调用的是旧控制者还是新控制者的 ReevaluateAuras，结果都一致：
                //   目标 t 仍在 src.ControllerId 区域 + 符合过滤 → 有效；否则撤销（跨玩家找回 t 实例执行 Revoke）。
                int srcController = src.ControllerId;
                var srcAreaCards = zm.GetZoneCards(CardZone.AreaZone, srcController) ?? new System.Collections.Generic.List<CardInstance>();
                bool srcOnField = srcAreaCards.Any(c => c.UID == srcUid); // 源卡是否还在场（未离场）
                // 限制：源卡必须正面朝上（未暗藏）光环才生效；背面朝上（暗藏）则整光环注销。
                if (src.IsCovert) srcOnField = false;

                // 1) 撤销已失效者（A：跨玩家按 UID 找回实例再撤销，避免反复夺控攻击力叠加）
                foreach (var tUid in grantedTo.Keys.ToList())
                {
                    var t = srcAreaCards.FirstOrDefault(c => c.UID == tUid);
                    if (t == null) t = CardManagerRef?.GetInstance(tUid); // 跨控制者找回（夺控给对手仍要撤回本源授予）
                    // 失效条件：源卡离场 / 目标离场 / 目标不再符合光环条件（领域/组织/关键词/仅本地区/暗藏）/ 目标不在源卡当前控制者区域
                    bool invalid = !srcOnField || t == null || !IsAuraTargetEligible(auraEffect, src, t)
                        || !srcAreaCards.Any(c => c.UID == tUid);
                    if (invalid)
                    {
                        if (t != null)
                            foreach (var g in grantedTo[tUid])
                                t.Revoke(g.Kind, g.Value, g.Amount);
                        GD.Print($"[光环LOG] ⟳ 光环撤销 {src.PlayerTemplate?.CardName}(属p={srcController}) → {(t != null ? t.PlayerTemplate?.CardName : "NULL(已离场/不在源卡区域)")} UID={tUid} tNull={t == null}");
                        grantedTo.Remove(tUid);
                    }
                }
                // 2) 补授：源卡在场时，对 src.ControllerId 区域内符合条件、尚未授予的他者补授。
                //    【自愈】卡"换区"时 `CardInstance` 会清空运行期授予（离场还原），但光环登记表仍记着"已授予"——
                //    登记表与卡上实况不一致时会把该目标误判为"已授过"而永不重授（表现：光环授予在"暗藏→现身/移动"后丢失）。
                //    因此这里对**已登记目标**也逐条校验授予是否仍在，缺失则重新授予（幂等，不重复记账）。
                if (srcOnField)
                {
                    foreach (var t in srcAreaCards)
                    {
                        if (t == null || t == src) continue;
                        if (!IsAuraTargetEligible(auraEffect, src, t)) continue;
                        ApplyAuraGrantsTo(src, auraEffect, t, grantedTo);   // 单一实现（首次授予 / 自愈补授）
                    }
                }
            }
            // 光环补授/撤销已改写 MagicDomains/攻击力/调查力等数据，经总线通知表现层刷新
            // StatsLabel 与卡面动态标签（夺控场景下 OnPostMove 的 SyncDerivedUI 早于本调用，故需此处二次收口）。
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }

        // 兼容别名（旧调用点 RegisterContinuous / UnregisterContinuous 仍可用）
        public void RegisterContinuous(CardInstance card) => RegisterPersistent(card);
        public void UnregisterContinuous(CardInstance card) => UnregisterPersistent(card);
        /// <summary>查询某卡当前是否有已注册的持续效果（调试/规则用）。</summary>
        public bool HasContinuous(CardInstance card)
            => card != null && HasAnyContinuousFor(card.UID);

        /// <summary>
        /// 【JC081】取该卡声明的"**可结附子类别白名单**"（`StaticKind.OnlyAttachWithSubCategories.Param`，
        /// 本卡＝`装备,祝福`）。**未声明该规则** → 返回 null（调用方不施加任何限制）。
        /// 读的是**卡面印刷静止规则**（两端都持有卡脚本数据，故客户端也能判定，不依赖 EffectEngine 实例）。
        /// </summary>
        public static string GetAttachSubCategoryWhitelist(CardInstance target)
        {
            if (target == null) return null;
            // 【用户口径·2026-09】背面朝上的卡（暗藏者）**不提供**持续/静止规则 ⇒ 其"只受装备与祝福结附"
            // 也随之失效（与 `RegisterPersistent` 的暗藏门禁同口径；本判定读印刷规则，故必须在此单独判一次）。
            if (target.IsCovert) return null;
            foreach (var e in target.EffectiveEffects)
            {
                if (e?.StaticEffects == null) continue;
                foreach (var s in e.StaticEffects)
                    if (s != null && s.Kind == StaticKind.OnlyAttachWithSubCategories) return s.Param ?? "";
            }
            return null;
        }

        /// <summary>
        /// 【JC081】"**除装备与祝福外不受附属**"判定：`attaching`（要结附上去的附属卡）的**当前子类别**
        /// 是否命中白名单之一。白名单为空 → **放行**（fail-open：防"Param 拼错导致整卡被封死"）。
        /// </summary>
        public static bool PassesAttachWhitelist(CardInstance attaching, string whitelistParam)
        {
            if (attaching == null) return false;
            if (string.IsNullOrWhiteSpace(whitelistParam)) return true;
            bool anyValidEntry = false;
            foreach (var raw in whitelistParam.Split(','))
            {
                string sub = raw.Trim();
                if (sub.Length == 0) continue;
                anyValidEntry = true;
                if (attaching.CurrentSubCategories.Contains(sub)) return true;
            }
            // 整串只有分隔符/空白（如 `","` / `" , "`）→ 视作"未给出有效白名单"，同样 fail-open，
            // 与 `Param=""` 同一口径：拼错的 Param 不应把整张卡封死（宁可放宽，也不静默锁死规则）。
            return !anyValidEntry;
        }

        /// <summary>查询某卡是否带某静止/禁止标记（D19-⑤ 消费入口，如 CannotBeAttached）。</summary>
        public bool HasStaticFlag(CardInstance card, StaticKind flag)
            => card != null && card.HasStaticFlag(flag);

        // ==================== 触发收集（供 TurnManager 桥接） ====================

        /// <summary>
        /// 从卡实例的 EffectDataList 收集指定时机的触发效果（进场/现身）。
        /// 返回 EffectData 列表，由调用方（TurnManager 桥接或通过 GameEventBus 总线）决定是否入堆叠。
        /// </summary>
        public System.Collections.Generic.List<EffectData> CollectTriggers(CardInstance card, EffectTiming timing)
        {
            var result = new System.Collections.Generic.List<EffectData>();
            if (card == null) return result;
            foreach (var e in EffectsOf(card))
            {
                if (e.Timing == timing) result.Add(e);
            }
            if (result.Count > 0)
                GD.Print($"[EffectEngine] 收集到 {result.Count} 个 {timing} 触发（卡 {card.PlayerTemplate?.CardName}）");
            return result;
        }

        /// <summary>
        /// 角色/附属激活能力入堆叠（D9 激活框架接驳点）：将一条激活型 EffectData 包装为 Trigger 类
        /// StackEntry 入堆叠，统一走 ResolveEntry 的 Trigger 分支执行（含 D16 目标丢失校验）。
        /// </summary>
        public void PushActivationToStack(StackManager sm, CardInstance card, EffectData ability, CardInstance target = null,
            string sourceZoneDetailSnapshot = null, int sourceControllerSnapshot = -1, int targetPlayerId = -1,
            string targetAreaId = null, int eventAmount = -1)
        {
            if (sm == null || card == null || ability == null) return;
            var entry = new StackEntry(card.OwnerId, $"[激活]{ability.EffectId}")
            {
                Kind = ResolveKind.Trigger,
                IsCard = false,
                // 【K23/A57·双重计数修复】标记"激活类条目"：次数上限已在发动阶段（本方法调用者
                // `ActivateAbility`）登记过，结算分支据此**不再二次检查/二次登记**，
                // 否则首次结算会被自己刚登记的计数判成"已达上限"而跳过。
                FromActivation = true,
                ControllerId = card.ControllerId,   // 箭头按控制者着色（D31）
                SourceCardUid = card.UID,
                TargetCardUid = target?.UID ?? "",
                TargetPlayerId = targetPlayerId,     // 【K24】目标玩家（入堆叠时锁定）
                TargetAreaId = targetAreaId ?? "",   // 【K15】目标地区（入堆叠时锁定；结算时作为 targetAreaId 下发）
                // 【JC055②】激活代价已支付的数量（如"移除 X 个时间标志"的 X）→ 结算时作为 `事件数量` 下发，
                // 供 `ValueExpr="代价数量"` 读取（与触发事件的"事件数量"共用同一条透传通路）。
                EventAmount = eventAmount,
                EffectDataEffect = ability,
                EffectText = ability.EffectId,
                // 参照 TurnManager.PushTriggerToStack：填充 TriggerInstance，
                // 使 StackEntryUI 能经 entry.StackEffect.Source.PlayerTemplate 取到来源卡，渲染卡图。
                StackEffect = new Script.Data.Effects.StackEffectInstance
                {
                    Source = card,
                    OwnerPlayerId = card.OwnerId,
                    EffectDataRef = ability,
                },
            };
            // 箭头：激活指向目标卡（JC003 类需目标效果，堆叠内绘制指向箭头）。
            // 与 TurnManager.PushTriggerToStack / PushToStack 出牌类一致，填充 entry.Targets，
            // 否则 Arena.RefreshStackArrows 因 Targets 空列表而不画箭头（D30）。
            if (target != null)
            {
                entry.Targets.Add(new Script.Game.Entities.StackTargetRef
                {
                    Kind = Script.Game.Entities.StackTargetRef.RefKind.Card,
                    CardUid = target.UID,
                });
            }
            // 【K15】目标地区同样登记箭头引用（指向地区槽位；结算时经 `StackEntry.TargetAreaId` 下发）
            if (!string.IsNullOrEmpty(targetAreaId))
            {
                entry.Targets.Add(new Script.Game.Entities.StackTargetRef
                {
                    Kind = Script.Game.Entities.StackTargetRef.RefKind.Area,
                    AreaUid = targetAreaId,
                    AreaSlotId = TurnManagerRef?.AreaManagerRef?.GetSlotIndex(targetAreaId) ?? 0,
                });
            }
            // 【K24】目标玩家同样登记箭头引用（指向玩家头像；RefKind.Player 早已存在于 StackTargetRef）。
            if (targetPlayerId > 0)
            {
                entry.Targets.Add(new Script.Game.Entities.StackTargetRef
                {
                    Kind = Script.Game.Entities.StackTargetRef.RefKind.Player,
                    PlayerId = targetPlayerId,
                });
            }
            // 补记来源（发动地）快照：激活效果的本地区（IsLocalZone）结算复检依赖它，
            // 与 TurnManager.CaptureTargetSnapshot（触发/出牌入栈）一致。此前激活入栈漏记
            // （HasSourceZoneSnapshot=false）→ 结算时 snapshot=null → TargetValidity.Check 规则⑥
            // 本地区判定跳过 → 目标移出本地区不判丢失（JC020 效果A/B 复现，见 D46）。
            // 优先用 ActivateAbility 传入的"横置/牺牲前"宿主地区快照（来源可能已离场，如 JC020 效果B 先牺牲自身，
            // 此时 card.ZoneDetailId 已非宿主地区）；未传时回退 card 当前值（触发/出牌等来源在场场景）。
            if (sourceZoneDetailSnapshot != null || sourceControllerSnapshot >= 0)
            {
                entry.SourceZoneDetailSnapshot = sourceZoneDetailSnapshot;
                entry.SourceControllerSnapshot = sourceControllerSnapshot;
                entry.HasSourceZoneSnapshot = true;
            }
            else if (card != null)
            {
                entry.SourceZoneDetailSnapshot = card.ZoneDetailId;
                entry.SourceControllerSnapshot = card.ControllerId;
                entry.HasSourceZoneSnapshot = true;
            }
            sm.Push(entry);
            GD.Print($"[EffectEngine] ✓ 激活能力入堆叠: {ability.EffectId}（来源 {card.PlayerTemplate?.CardName}）");
        }

        // ==================== 事务卡便捷入口 ====================

        /// <summary>事务卡打出结算：执行卡上所有 OnSorceryCast 效果（F组）。
        /// `targetAreaUid`：【K2】打出时选定的**目标地区**（如"选择一个地区"的事务卡）——透传到效果结算，
        /// 供群体动作（`MassMode.AllInArea`）确定作用地区；缺省空串＝沿用旧行为（无地区）。
        /// `targetCardUids`：【K15】多目标卡**逐槽选定的卡目标（有序）**——效果若声明了 `TargetSlots`，
        /// 则恢复逐槽选择并逐个目标执行动作。</summary>
        public void ResolveSorcery(CardInstance sorcery, string targetUid, int targetVersionId = -1,
            string targetAreaUid = "", System.Collections.Generic.IList<string> targetCardUids = null, int modeIndex = -1)
        {
            if (sorcery?.PlayerTemplate is not PlayerCardData tmpl) return;
            var spells = new System.Collections.Generic.List<EffectData>();
            foreach (var e in EffectsOf(sorcery))
            {
                if (e.Timing == EffectTiming.OnSorceryCast) spells.Add(e);
            }
            if (spells.Count == 0) return;
            CardInstance target = string.IsNullOrEmpty(targetUid) ? null : CM?.GetInstance(targetUid);

            // D16 新方案：结算前校验，任一需目标效果的目标已丢失（版本号不一致）→ 整张事务卡不结算并直接进墓地（用户铁律）
            foreach (var spell in spells)
            {
                if (ValidateTargetAtResolve(spell, sorcery, target, targetAreaUid, targetVersionId, modeIndex) == TargetValidation.Lost)
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 事务卡 {tmpl.CardName} 目标丢失 → 整张不结算，卡进墓地");
                    CM?.MoveCardToGraveyard(sorcery);
                    return;
                }
            }

            foreach (var spell in spells)
            {
                // 【K15】多目标效果：把"地区槽 + 有序卡目标"恢复成逐槽选择后结算
                var choices = MultiTargetSlots.IsMultiTarget(spell)
                    ? MultiTargetSlots.Reconstruct(spell, targetAreaUid, targetCardUids)
                    : null;
                // 【K16】模式分支：把入堆叠时选定的模式传下去（结算只执行该模式）
                ResolveEffect(spell, sorcery, target, targetAreaUid, -1, targetVersionId, -1, choices, modeIndex);
            }
        }

        // ==================== 激活能力入口 ====================

        /// <summary>
        /// 角色/附属激活能力执行入口（D9 激活框架未实现，这里提供执行体）。
        /// 调用方需先校验 ActivateCondition/Cost（激活许可），校验通过再调本方法。
        /// </summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target)
            => ActivateAbility(source, data, target, -1);

        /// <summary>
        /// 【K24】激活能力入口（带**目标玩家**）：`targetPlayerId` ＞ 0 时本次效果的"目标玩家"＝该玩家
        /// （动作里 `"目标玩家"` 的解析来源）。玩家目标与卡目标互不排斥：`Target` 声明 `Kind=Player` 时用本参数，
        /// 声明卡/地区目标时用 `target`。
        /// </summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId)
            => ActivateAbility(source, data, target, targetPlayerId, null);

        /// <summary>【P3·K22】激活入口（带**牺牲代价的选定候选**）：`sacrificeUid` ＝发动者选择的被牺牲角色 UID
        /// （空＝无此代价，或走确定性兜底）。代价在**入堆叠之前**支付；不合法 → 整次发动被拒且无副作用。</summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
            string sacrificeUid)
            => ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, null);

        /// <summary>【P3·K15】激活入口（带**目标地区**）：`data.NeedsAreaTarget` 为 true 的效果需要它；
        /// 空值 → 走确定性兜底（第一个合法地区）。目标地区在**入堆叠时锁定**（`StackEntry.TargetAreaId`）。</summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
            string sacrificeUid, string targetAreaId)
            => ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, targetAreaId, null);

        /// <summary>【P3·K5】激活入口（带**标志代价的选定候选**）：`counterCostUid` ＝发动者选择的"移除标志"的那张卡
        /// UID（空＝无此代价，或走确定性兜底）。与牺牲代价同款：**入堆叠前支付**、不合法/无候选 → 整次发动被拒且无副作用。</summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
            string sacrificeUid, string targetAreaId, string counterCostUid)
            => ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, targetAreaId, counterCostUid, null);

        /// <summary>【JC055②】兼容入口（带**标志代价的逐卡分配**，无"牌作代价"）：
        /// `counterCostAllocation` ＝ 卡 UID → 移除数量。等价于新入口的 `cardCostUids = null`。</summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
            string sacrificeUid, string targetAreaId, string counterCostUid,
            System.Collections.Generic.Dictionary<string, int> counterCostAllocation)
            => ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, targetAreaId, counterCostUid,
                counterCostAllocation, null);

        /// <summary>
        /// 【JC055②】激活入口（带**标志代价的逐卡分配**）：`counterCostAllocation` ＝ 卡 UID → 移除数量
        /// （"自本方卡牌上移除 **X** 个时间标志"；X 由玩家选择、**至少 1**）。
        /// 与 `counterCostUid`（单卡固定数量）互斥；本参数非空时按分配支付，并把**支付的数量**作为
        /// 本次结算的 X（`ValueExpr="代价数量"`）。
        /// </summary>
        /// <summary>
        /// 【秘社批次】激活入口（带**"牌作代价"的选定候选**）：`cardCostUids` ＝ 发动者选择的、要作为代价取走的
        /// 牌 UID 列表（空＝无此代价，或未选择 ⇒ 走确定性兜底）。与牺牲/标志代价同款：**入堆叠前支付**、
        /// 候选不足/指定非法 → 整次发动被拒且**无副作用**。详见 <see cref="CardCostSpec"/>。
        /// </summary>
        public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
            string sacrificeUid, string targetAreaId, string counterCostUid,
            System.Collections.Generic.Dictionary<string, int> counterCostAllocation,
            System.Collections.Generic.List<string> cardCostUids)
        {
            if (data.Timing is not (EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated or EffectTiming.ManeuverTrigger or EffectTiming.CouncilActivated))
            {
                GD.PrintErr($"[EffectEngine] ActivateAbility 收到非激活类 Timing={data.Timing}");
                return;
            }
            // P2：同步当前回合号，供时效费用修正栈登记正确的到期回合（expireTurn）。
            // 与 PlayCard/AmbushReveal/ResolveEntry/DoCleanup 一致；激活结算路径此前漏设，
            // 导致 CurrentTurn 残留脏值 → expireTurn 偏大 → 费用修正项跨回合不清除（UI 残留绿色、真实费用被错误减免）。
            if (TurnManagerRef != null) CurrentTurn = TurnManagerRef.TurnCount;
            _pendingPaidCounterX = -1;   // 【JC055②】每次激活重置"已付标志代价数量"
            // D-1 熔断：堆叠累计压入达上限时禁止激活新效果
            if (IsStackCircuitBroken != null && IsStackCircuitBroken())
            {
                GD.PrintErr($"⛔ [EffectEngine] 堆叠已熔断，拒绝激活能力 {data.EffectId}");
                return;
            }

            // ===== D9 激活许可校验 =====
            // 秘社卡 PlayerTemplate==null，改用 CouncilTemplate 作为展示模板（来源卡本身有效即可）
            if (source == null || (source.PlayerTemplate is not PlayerCardData && source.CouncilTemplate is not CouncilCardData))
            {
                GD.PrintErr("[EffectEngine] ActivateAbility 来源卡无效");
                return;
            }
            // 【P3·K20】"印刷文本框视作空白"（JC101）→ 来源卡**失去效果**，其能力一律不可发动（权威门禁）
            if (IsTextBlank(source))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 来源卡文本视作空白（失去效果），拒绝激活 {data.EffectId}");
                return;
            }
            BaseCardData tmpl = source.PlayerTemplate as PlayerCardData;
            if (tmpl == null) tmpl = source.CouncilTemplate as CouncilCardData;

            // 0.8)【K27·来源生效区门禁】激活类效果声明的**生效区**必须与来源卡当前所在区一致
            //   （默认：角色=争夺区、附属=附属区；显式 `Scope = ZoneScope.HandZone` 的"**手牌行动**"
            //    允许在手牌中发动 → JC110）。这同时堵住"来源不在生效区也能激活"的漏洞
            //   （此前只靠候选/UI 约束，如墓地里发动的角色能力不会被拒）。
            if (!IsEffectActiveInZone(data, source))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 来源生效区不符：{tmpl?.CardName} 在 {source.CurrentZone}，" +
                            $"能力 {data.EffectId} 的生效区 {data.Scope} → 拒绝激活");
                return;
            }

            // 0) 规则层原子锁（核心需求 7.3）：原子操作（出牌/结算/触发排序/选目标窗口）执行期间
            // 冻结响应权，任何激活能力（含快速行动 JC003 类）均不可在原子期间发动；与 Pass/PlayCard 一致。
            if (TurnManagerRef != null && TurnManagerRef.IsAtomicLocked)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 原子操作进行中，拒绝激活 {data.EffectId}");
                return;
            }

            // 0.5)【K1】动态值 X：激活类效果没有事件数量上下文（事件数量来源只对触发类有意义）；
            //   若该效果声明了 `计数:…` 且求值为 0（或表达式未登记）→ **不可发动**（用户口径：X=0 不算发动）。
            //   【JC055②】例外：`代价数量` 的 X 来自**本次激活已支付的代价**（入堆叠前才产生）→ 不做前置求值判定。
            if (!string.IsNullOrWhiteSpace(data.ValueExpr) && !IsCostPaidValue(data))
            {
                var vst = EvaluateDynamicValue(data, source, target, null, -1, out _, out string vReason);
                if (vst == DynamicValueState.Blocked)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 动态值 X 不可用，拒绝激活 {data.EffectId}：{vReason}");
                    return;
                }
            }

            // 1) 优先权门禁：当前控制者拥有优先权（快速效果本身即“拥有优先权即可发动”）
            // 控制权语义（核心需求 / D28）：夺控场景下以 ControllerId 归属，而非 OwnerId。
            if (TurnManagerRef != null && TurnManagerRef.PriorityPlayerId != source.ControllerId)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 无优先权，拒绝激活 {data.EffectId}（Priority={TurnManagerRef.PriorityPlayerId}, Controller={source.ControllerId}）");
                return;
            }

            // 1.5)【P3·K21·JC041】发动**来源前提**（权威门禁，与 `ActivateEligibility` 候选侧**同一实现**）：
            //   `RequireSourceCovert`（只能作为暗藏者时发动）＋ `RequireSourceAreaSubCategory`（如"水域"地区）。
            if (!ActivateEligibility.MeetsSourcePrereqs(source, data, TurnManagerRef?.AreaManagerRef, out string srcReason))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 拒绝激活 {data.EffectId}：{srcReason}");
                return;
            }

            // 1.5) 窗口门禁（D9）：区分"快速行动"与"行动"            //   - 快速行动（ActivateWindow 含"快速"，如 JC003）：拥有优先权即可发动，堆叠开放时作为响应插队。            //   - 行动（ActivateWindow 含"行动"）：仅限【该玩家自己的行动步骤 + 堆叠为空 + 有行动权】。
            //     先手玩家→ActionFirst，后手玩家→ActionSecond（复用 CanEnterBasicActionMask 的精确判定）。
            //   - 【K26】阶段排除（ExcludedPhases）：声明了"不可发动的阶段"的效果在对应阶段一律拒绝
            //     （服务端权威门禁；与 ActivateEligibility 的高亮/候选判定共用 PhaseGateRule）。
            if (PhaseGateRule.IsExcluded(data, TurnManagerRef))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 阶段排除：{data.EffectId} 不能在「{PhaseGateRule.DescribeExclusion(data)}」发动" +
                            $"（{PhaseGateRule.DescribeState(TurnManagerRef)}）");
                return;
            }
            if (data.ActivateWindow.Contains("行动"))
            {
                var tm = TurnManagerRef;
                // 控制权语义（核心需求 / D29）：行动窗口归属以控制者为准，而非拥有者。
                bool canAct = tm != null && tm.CanEnterBasicActionMask(source.ControllerId);
                if (!canAct)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 行动窗口未满足，拒绝激活 {data.EffectId}（玩家{source.OwnerId} 非自己行动步骤/堆叠非空/无行动权）");
                    return;
                }
            }

            // 2) 激活条件（发动许可）：若需"横置本角色"作为代价，则本角色必须处于未横置态方可发动
            //    （已横置的角色不能再次发动；发动后再横置自身，见第 3 步）
            // 2.0)【K23】次数上限：每张卡·每回合·每能力（`EffectData.MaxUsesPerTurn`，0=不限次）。
            //   权威门禁放在代价（横置/牺牲/付费）之前，避免"已达上限却已付出代价"。
            if (source.IsAbilityLimitReached(data.EffectId, data.MaxUsesPerTurn))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 次数上限：{data.EffectId} 本回合已发动 {source.GetAbilityUsesThisTurn(data.EffectId)} 次" +
                            $"（上限 {data.MaxUsesPerTurn}）→ 拒绝激活");
                return;
            }
            // 2.0b)【A57】每局游戏次数上限（`EffectData.MaxUsesPerGame`，0=不限次；如秘社 MSJC001 行动4
            //   "该能力每局游戏只能发动一次"）。同样放在代价之前，避免"已达上限却已付出代价"。
            if (source.IsGameAbilityLimitReached(data.EffectId, data.MaxUsesPerGame))
            {
                GD.PrintErr($"[EffectEngine] ⛔ 每局次数上限：{data.EffectId} 本局已发动 {source.GetAbilityUsesThisGame(data.EffectId)} 次" +
                            $"（上限 {data.MaxUsesPerGame}）→ 拒绝激活");
                return;
            }
            if (!string.IsNullOrEmpty(data.ActivateCondition) &&
                data.ActivateCondition.Contains("横置本角色") &&
                source.IsTapped)
            {
                GD.PrintErr($"[EffectEngine] ⛔ {tmpl.CardName} 已横置，不能作为条件发动 {data.EffectId}");
                return;
            }

            // 2.2)【P3】激活类效果的**结算条件前置判定**：`EffectCondition.Predicate` 若声明了非 Always 的条件，
            //   则在**付费/横置/牺牲之前**判定——条件不满足直接拒绝发动（否则会"付了代价却什么都没发生"，
            //   与 2.5 的目标合法性同一条纪律）。用于 JC053「本回合中，若有敌方势力标志曾被移除，你可以抓一张牌」
            //   这类"以回合内既成事实为发动前提"的能力。
            if (data.Condition != null && !string.IsNullOrEmpty(data.Condition.Predicate)
                && data.Condition.Predicate != "Always")
            {
                bool preMet = EvaluateCondition(data.Condition.Predicate, source, target, target, -1, null);
                if (!preMet)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 发动条件不满足「{data.Condition.Predicate}」→ 拒绝激活 {data.EffectId}（代价未付出）");
                    return;
                }
            }

            // 2.7)【K15】"目标地区"前置门禁：声明了 `NeedsAreaTarget` 的激活能力必须能定出合法地区
            //   （同样先于付费/横置）。指定地区不合法 → 拒绝；未指定 → **确定性兜底取第一个合法地区**。
            if (data.NeedsAreaTarget)
            {
                var areas = GetLegalAreaTargets(source, data, target);
                if (areas.Count == 0)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 无合法目标地区 → 拒绝激活 {data.EffectId}（代价未付出）");
                    return;
                }
                if (string.IsNullOrEmpty(targetAreaId))
                {
                    targetAreaId = areas[0];
                    GD.Print($"[EffectEngine] ⚠ 未指定目标地区 → 确定性兜底第一个合法地区（{targetAreaId}）");
                }
                else if (!areas.Contains(targetAreaId))
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 指定的目标地区 {targetAreaId} 不合法 → 拒绝激活 {data.EffectId}（代价未付出）");
                    return;
                }
                GD.Print($"[EffectEngine] ↳ 激活目标地区：{targetAreaId}（入堆叠时锁定）");
            }

            // 2.6)【K22】"牺牲一个角色"代价的**前置门禁**（同样必须在付费/横置之前）：
            //   无合法候选 → 直接拒绝（否则会白付费用/白横置自身）；指定候选不合法 → 同样拒绝（fail-closed）。
            if (data.SacrificeCost != null)
            {
                var cand = GetSacrificeCandidates(source, data.SacrificeCost);
                if (cand.Count == 0)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 牺牲代价无合法候选 → 拒绝激活 {data.EffectId}（代价未付出）");
                    return;
                }
                if (!string.IsNullOrEmpty(sacrificeUid)
                    && cand.Find(c => string.Equals(c.UID, sacrificeUid, System.StringComparison.Ordinal)) == null)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 指定的牺牲候选 {sacrificeUid} 不合法 → 拒绝激活 {data.EffectId}（代价未付出）");
                    return;
                }
            }

            // 2.7)【秘社批次】"牌作代价"的前置门禁（同样必须在付费/横置之前）：
            //   候选不足 N 张（强制时）/ 指定候选不合法 → 直接拒绝（否则会白付费用/白横置自身）。
            if (data.CardCost != null)
            {
                int need = data.CardCost.Count > 0 ? data.CardCost.Count : 1;
                var cand = GetCardCostCandidates(source, data.CardCost);
                if (cand.Count < need && data.CardCost.Mandatory)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 牌作代价候选不足（需 {need}，仅 {cand.Count}）→ 拒绝激活 {data.EffectId}（代价未付出）");
                    return;
                }
                if (cardCostUids != null)
                {
                    foreach (var uid in cardCostUids)
                    {
                        if (cand.Find(c => string.Equals(c.UID, uid, System.StringComparison.Ordinal)) == null)
                        {
                            GD.PrintErr($"[EffectEngine] ⛔ 指定的牌作代价候选 {uid} 不合法 → 拒绝激活 {data.EffectId}（代价未付出）");
                            return;
                        }
                    }
                }
            }

            // 2.5) 目标合法性校验（若需选目标）——**必须在付费/横置/牺牲之前**：
            //   否则"目标不合法"的发动会先扣费、先把自身横置/牺牲掉，再被拒绝（代价白付）。
            // 【K24】玩家目标：由 `targetPlayerId` 表达（不再要求 `target` 非空），并按同一判定复检合法性。
            if (PlayerTargetPool.IsPlayerSpec(data.Target))
            {
                if (targetPlayerId <= 0)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 激活 {data.EffectId} 缺少目标玩家");
                    return;
                }
                if (!PlayerTargetPool.Matches(data.Target, targetPlayerId, source.ControllerId,
                        TurnManagerRef?.PlayerManagerRef, out string pReason))
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 激活 {data.EffectId} 目标玩家不合法：{pReason}");
                    return;
                }
                _currentTargetPlayerId = targetPlayerId;
            }
            else if (data.Target != null && data.Target.NeedsSelection && target == null)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 激活 {data.EffectId} 缺少目标");
                return;
            }
            else if (target != null && data.Target != null && data.Target.NeedsSelection)
            {
                // 【补齐·目标精筛】激活路径原先只校验"有没有给目标"，**不校验精筛条件**（
                // `Restrictions` 如 `CurrentSubCategory=吸血鬼`）→ 非法目标也会照付代价（用户 2026-09 口径：
                // "场上没有符合条件的角色时这个效果不能发动"，JC028）。现按**与打出/结算同一实现**
                // `TargetValidity.Check` 复检；不合法 → 拒绝发动且**代价未付出**。
                var tv2 = TargetValidity.Check(source, target, data.Target, null, TurnManagerRef?.PlayerManagerRef);
                if (!tv2.valid)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 激活 {data.EffectId} 目标不合法"
                             + $"（{target.PlayerTemplate?.CardName}：{tv2.reason}）→ 拒绝发动（代价未付出）");
                    return;
                }
            }

            // 3) 费用校验与扣除（控制权语义 / D29：由控制者付费，与 CanActivate 高亮判定对齐）
            int cost = data.Cost;
            var pm = TurnManagerRef?.PlayerManagerRef;
            if (cost > 0 && pm != null)
            {
                if (pm.GetAvailableMana(source.ControllerId) < cost)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 费用不足，拒绝激活 {data.EffectId}（需 {cost}，控制者{source.ControllerId}）");
                    return;
                }
                pm.SpendMana(source.ControllerId, cost);
                Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();   // 费用支付（横置资产卡）已改模型，刷新 UI
            }

            // 4.5) 捕获来源（发动地）快照：必须在"横置/牺牲"代价前取 source.ZoneDetailId。
            //      否则 JC020 效果B 先牺牲自身入墓地（第 3.5 步），ZoneDetailId 已非宿主地区，
            //      本地区(IsLocalZone)结算判定基准会丢失 → 目标移动不判丢失。
            //      source.ZoneDetailId = 宿主角色所在地区（附属 MoveToAttachedZone 时设置）。
            string srcZoneSnap = source?.ZoneDetailId ?? "";
            int srcCtrlSnap = source?.ControllerId ?? 0;

            // 3) 激活条件：横置本角色（付出代价）
            if (!string.IsNullOrEmpty(data.ActivateCondition) &&
                data.ActivateCondition.Contains("横置本角色"))
            {
                source.SetTapped(true);    // SetTapped 发 TappedChanged → 持续效果重判（阿格斯封锁即时开关）
                GD.Print($"[EffectEngine] ↻ {tmpl.CardName} 因激活横置");
                Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();   // 本角色横置已改模型，刷新 UI
            }

            // 3.5) 激活条件：牺牲本角色（付出代价，离场类）
            //   文档 §3.3："牺牲本角色"作为激活代价，在【入堆叠之前】处理（非效果动作）。
            //   先横置自身（3 步），再牺牲自身入墓地；牺牲后 source 仍被 PushActivationToStack 引用，
            //   结算 Destroy(target) 基于已捕获的 target 实例，不受影响。
            //   已横置不能发动的门禁见第 2 步；此处执行实际牺牲。
            if (!string.IsNullOrEmpty(data.ActivateCondition) &&
                data.ActivateCondition.Contains("牺牲本角色"))
            {
                if (source != null && CM != null)
                {
                    GD.Print($"[EffectEngine] ✝ {tmpl.CardName} 因激活牺牲自身（入堆叠前代价）");
                    // 【触发语义】牺牲＝死亡（行为），走统一死亡入口：死亡触发在此刻成立（卡仍在场上）。
                    CM.ResolveDeath(source, CardManager.DeathCause.Sacrifice);
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                }
                else
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 牺牲本角色失败（source 为空或 CardManager 缺失），拒绝激活 {data.EffectId}");
                    return;
                }
            }

            // 3.6)【K22】激活代价：牺牲一个角色（"牺牲一个角色"/"牺牲本地区的另一个角色"等，用户 2026-09 口径）            //    · 由**发动者自己选择**（`sacrificeUid`＝UI 选择结果；空 → 确定性兜底第一个合法候选）；
            //    · **除非卡文明确写"暗藏者"，否则不能牺牲暗藏者**；候选必须先经 `IsLegalSacrifice` 复检；
            //    · 牺牲＝死亡 → `ResolveDeath(Sacrifice)`：**死亡时机照常触发**；
            //    · 代价无法支付 → 拒绝发动（此时尚未入堆叠，也未消耗其它代价之外的状态）。
            if (data.SacrificeCost != null)
            {
                if (!PaySacrificeCost(source, data, sacrificeUid, out _)) return;
            }

            // 3.7)【K5】激活代价：**自本方卡牌上移除 N 个（时间）标志**（JC044；用户 2026-09 口径 A17/A27）：
            //    · "移除一个时间标志"就是**发动代价**；由**发动者自主选择**移除哪张卡上的标志
            //      （`counterCostUid`＝UI 选择结果；空 → 确定性兜底第一个合法候选）；
            //    · 同样在**入堆叠之前**支付；无合法候选 / 指定候选非法 → 拒绝发动且无副作用。
            if (data.CounterCost != null)
            {
                // 【JC055②】"移除 X 个"（X 由玩家逐卡分配）→ 按分配支付，并记下 X 供结算读取；
                // 否则走既有的"单卡固定数量"支付（JC044②）。
                if (data.CounterCost.AmountByPlayerChoice)
                {
                    if (!PayCounterCostAllocation(source, data, counterCostAllocation, out int paidX)) return;
                    _pendingPaidCounterX = paidX;
                }
                else if (!PayCounterCost(source, data, counterCostUid, out _)) return;
            }

            // 3.8)【秘社批次】激活代价：**从某区域取 N 张牌**（CardCostSpec）——同样在**入堆叠之前**支付。
            //   MSJC010②③（封印区的牌→牌库底）/ MSJC016②（墓地七张→封印在颂亡者上）/ MSJC012① ②（横置三个本方角色）。
            if (data.CardCost != null)
            {
                if (!PayCardCost(source, data, cardCostUids)) return;
            }

            // 4) 目标合法性校验：已在**第 2.5 步**完成（付费 / 横置 / 牺牲代价之前）——此处不重复。

            // ===== 入堆叠（由堆叠统一结算，含优先权让过） =====
            // 【K23】登记本次发动（计数在"发动被接受"的这一刻 +1，与"结算是否成功"无关）。
            source.MarkAbilityUsedThisTurn(data.EffectId);
            // 【A57】同一时刻登记"本局"计数（每局游戏次数上限，如 MSJC001 行动4）
            source.MarkAbilityUsedThisGame(data.EffectId);
            var sm = TurnManagerRef?.StackManagerRef;
            if (sm != null)
            {
                PushActivationToStack(sm, source, data, target, srcZoneSnap, srcCtrlSnap, targetPlayerId, targetAreaId,
                    eventAmount: _pendingPaidCounterX);
            }
            else
            {
                // 无堆叠管理器时回退直接结算（降级路径，不推荐）
                ResolveEffect(data, source, target);
            }
        }
    }
}