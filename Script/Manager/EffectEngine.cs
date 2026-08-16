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
    /// 2. 实现动作原语执行器（ActionVerb）：已落地 Tap/Untap/Destroy/Bounce/Move/Draw/Gain/Sacrifice，
    ///    其余原语为占位（打印 TODO），不影响数据结构定稿与流程跑通。
    /// 3. 持续效果注册表（ContinuousRegistry）：按 ZoneScope 注册/注销（手牌期靠 HasInherentKeyword 双层补判）。
    /// 4. 触发收集：从卡实例的 EffectDataList 收集进场/现身触发，供 TurnManager/TriggerRegistry 桥接。
    /// 5. 激活能力入口：角色/附属激活（D9 激活框架未实现，这里提供执行体）。
    /// 6. 时效修正栈（P2）："直到回合结束"类修正于 ExpireTurnEndEffects 自动还原。
    ///
    /// 触发驱动：反应/机动触发由监听总线（GameEventBus + TriggerRegistry）收集；
    /// 进场/现身触发由本类 CollectTriggers 收集，统一经 TurnManager.RunTriggerPhase 入栈。
    /// </summary>
    public static class EffectEngine
    {
        // 抓牌等需要回合管理器参与的全局操作，由 TurnManager 初始化时注入
        public static TurnManager TurnManagerRef { get; set; }

        // ==================== 持续效果注册表 ====================
        // 记录每张卡实际生效（已授予）的持续效果条目，注销时精确还原。
        // value = 该卡所有"已实际生效"的 EffectGrant 扁平列表（含来源 EffectId，便于调试）。
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>> _continuousRegistry
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>();
        // 已生效持续效果的卡引用集合（与 _continuousRegistry 并行，供 ReevaluatePrereqs 遍历全集，无需 UID 反查）。
        private static readonly System.Collections.Generic.HashSet<CardInstance> _activePrereqCards
            = new System.Collections.Generic.HashSet<CardInstance>();
        // 持续光环（D19-③）注册表：源卡 UID → (受影响他者 UID → 该源对该他者实际授予的 EffectGrant 列表)。
        // 用于精确还原：源卡离场/被封印、或他者离场/夺控时，仅撤销本源授予的部分，不影响其他源。
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>> _auraGrants
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<EffectGrant>>>();

        // ==================== 管理器引用（供动词执行体访问模型） ====================
        // 由 TurnManager 初始化时注入；用于 Move 等动词真正改写模型（跨地区移动）。
        public static CardManager CardManagerRef { get; set; }
        // 持续光环（D19-③）需读取区域卡集合，由 Arena 初始化时注入 ZoneManager 引用。
        public static ZoneManager ZoneManagerRef { get; set; }

        // ==================== D-1 堆叠熔断查询 ====================
        // 由 TurnManager 初始化时注入 StackManager.IsCircuitBroken。
        // 为 true 时禁止激活新效果，防止效果递归失控。未注入时默认不熔断（测试环境）。
        public static System.Func<bool> IsStackCircuitBroken { get; set; }

        // ==================== D-UI 模型脏标记广播 ====================
        // 当结算/激活改动了卡牌模型可见状态（横置等）时通知表现层刷新。
        // 由 Arena 初始化时注入（回调其内部的资产区/争夺区横置态刷新）。
        public static System.Action OnVisualDirty { get; set; }

        // ==================== 准备步骤维护询问（JC068 阿格斯IV） ====================
        // 效果A"支付1，否则横置"的玩家二选一。由 Arena 注入：弹出选择窗，并把"支付/横置"的
        // 实际执行放进弹窗回调（异步）。本引擎弹窗是异步的，故委托只负责"发起并登记动作"，不等待返回。
        // 签名：(source, payAmount)。UI 实现内部弹窗，两选项 OnChosen 分别执行 SpendMana 或 SetTapped。
        public static System.Action<CardInstance, int> PromptPayOrTap { get; set; }

        // ==================== P2 时效修正栈 ====================
        // 记录"直到回合结束"等时效类修正，回合结束（ExpireTurnEndEffects）时自动还原。
        private sealed class TimedMod
        {
            public string TargetUid;     // 受影响卡 UID（卡级修正）；玩家级修正时为空
            public int PlayerId = -1;    // 玩家级修正（如费用-1）时有效
            public string Kind;          // Keyword / Icon / Attribute / Defense / CostMod
            public string Value = "";
            public int Amount = 1;
            public int ExpireTurn;       // 到期回合（≤该回合结束时还原）
            public string FilterText = ""; // 玩家级费用修正的匹配过滤（如"帷幕守望"），空=全匹配
            public bool ConsumeOnce;     // 一次性：打出/翻面符合条件牌一次后消费移除（JC012"下一张"语义）
        }
        private static readonly System.Collections.Generic.List<TimedMod> _timedModStack
            = new System.Collections.Generic.List<TimedMod>();

        // ==================== 动作原语执行器 ====================

        /// <summary>
        /// 统一结算入口：执行一条 EffectData 的动作序列。
        /// source = 效果来源卡实例；target = 已选中的目标卡实例（可能为 null）。
        /// </summary>
        public static void ResolveEffect(EffectData data, CardInstance source, CardInstance target)
            => ResolveEffect(data, source, target, "");

        /// <summary>
        /// 带"目标地区"的重载：用于机动类效果（如 JC014 公路骑士）在对抗前将来源卡移动到另一地区。
        /// targetAreaId 非空时优先作为移动目的地（ManeuverTrigger 的目标类型为 TargetKind.Area）。
        /// </summary>
        public static void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId)
        {
            // 复用 5 参重载；触发/激活路径默认 winnerId=-1（仅"X对抗获胜"类触发需要传入，见 WinnerIsSelf 判定）。
            ResolveEffect(data, source, target, targetAreaId, -1);
        }

        /// <summary>
        /// 带"对抗获胜方玩家 ID"的重载：用于"X对抗获胜"类触发（威名等）。
        /// winnerId = 对应 PlayerId（如 CombatWon 的获胜方），供条件 WinnerIsSelf 判定
        /// "本卡控制者是否为该对抗的获胜方"。非对抗类时机传 -1（默认），谓词走原判定。
        /// </summary>
        public static void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId, int winnerId)
            => ResolveEffect(data, source, target, targetAreaId, winnerId, -1);

        /// <summary>
        /// 带"目标入栈版本号"的重载（D16 丢失目标新方案）：targetVersionId 为堆叠条目入栈时记录的
        /// 目标卡 VersionId；结算时与当前目标 VersionId 比对，不一致即判丢失（见 ValidateTargetAtResolve）。
        /// 不传（默认 -1）表示调用方未提供版本号，回退为"实例存在即有效"（兼容旧路径）。
        /// </summary>
        public static void ResolveEffect(EffectData data, CardInstance source, CardInstance target, string targetAreaId, int winnerId, int targetVersionId)
        {
            if (data == null) return;
            GD.Print($"[EffectEngine] 结算效果 {data.EffectId} (Timing={data.Timing})");

            // D16 雏形：结算前目标丢失校验（双保险，与 TurnManager.ResolveEntryTargetIsLost 互补）。
            // 事务卡（SpellCast）目标丢失 → 整张不结算、卡进墓地；触发/激活来源卡在场，仅跳过本条结算。
            var tv = ValidateTargetAtResolve(data, source, target, targetAreaId, targetVersionId);
            if (tv == TargetValidation.Lost)
            {
                if (data.Timing == EffectTiming.SpellCast && source != null)
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 事务卡 {source.PlayerTemplate?.CardName} 目标丢失 → 不结算，卡进墓地");
                    CardManager.Instance?.MoveCardToGraveyard(source);
                }
                else
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 触发/激活效果 {data.EffectId} 目标丢失 → 跳过本条结算");
                }
                return;
            }

            // 条件不满足时走 ElseBranch
            if (data.Condition != null && data.Condition.Predicate != "Always")
            {
                // winnerId 透传给条件判定（供 WinnerIsSelf 使用）；targetAreaId 透传（供 WinnerInSameArea 使用）
                bool met = EvaluateCondition(data.Condition.Predicate, source, target, target, winnerId, targetAreaId);
                if (!met)
                {
                    GD.Print($"[EffectEngine] 条件不满足「{data.Condition.Predicate}」，走 ElseBranch");
                    ExecuteActions(data.Condition.ElseBranch, source, target, data.Duration, targetAreaId);
                    return;
                }
            }

            GD.Print($"[EffectEngine] 执行动作序列: EffectId={data.EffectId}, Actions数量={data.Actions?.Count ?? -1}");
            ExecuteActions(data.Actions, source, target, data.Duration, targetAreaId);
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
        public static TargetValidation ValidateTargetAtResolve(EffectData data, CardInstance source, CardInstance target, string targetAreaUid = "", int targetVersionId = -1)
        {
            if (data?.Target == null) return TargetValidation.Ok;
            var t = data.Target;
            if (t.Kind == TargetKind.None || !t.NeedsSelection) return TargetValidation.NoTarget;

            // 取卡目标：已选中实例为空 → 丢失
            if (!string.IsNullOrEmpty(targetAreaUid))
            {
                // 指向地区：地区丢失判据交由外部（AreaManager），这里仅在本层以"target==null 且声明取卡"为丢失
                // 地区类目标丢失由 TurnManager.ResolveEntryTargetIsLost 收口，此处返回 Ok 避免双重处理
                return TargetValidation.Ok;
            }

            if (target == null)
            {
                GD.Print($"[EffectEngine] ⚑ [D16] 效果 {data.EffectId} 需要目标但目标已丢失 → 不结算");
                return TargetValidation.Lost;
            }

            // ⚑ [2026-08-15 诊断] 结算入口打印目标当前版本/控制者/区域，配合 [DIAG][Stack.Push] 对比入栈与结算差异
            GD.Print($"[DIAG][Resolve] 效果 {data.EffectId} 结算目标={target.PlayerTemplate?.CardName ?? "?"} 当前版本={target.VersionId} 入栈版本={targetVersionId} ctrl={target.ControllerId} zone={target.CurrentZone} kind={CardManager.MapInstanceToTargetKind(target)}");

            // 卡目标（D16 新方案）：状态版本号比对——入栈时记录的版本号与当前不一致即判丢失。
            // 版本号由移动/翻面/控制者变化/子类别授予撤销等中枢的 BumpVersion 驱动，取代逐属性广播。
            // targetVersionId<0 表示调用方未提供入栈版本号（兼容旧路径），回退为"实例存在即有效"。
            // 注（2026-08-11）：争夺区→争夺区夺控换边【不刷版本号】，故版本号比对在此场景下失效，
            // 必须由下方属性二次复检（TargetRules）兜底"敌方/友方、子类别、费用"等约束。
            if (targetVersionId >= 0 && target.VersionId != targetVersionId)
            {
                GD.Print($"[EffectEngine] ⚑ [D16] 效果 {data.EffectId} 目标状态版本号变化（入栈 {targetVersionId} → 现 {target.VersionId}）→ 不结算  [DIAG target={target.PlayerTemplate?.CardName ?? "?"} ctrl={target.ControllerId} zone={target.CurrentZone}]");
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
                var actTarget = data.Target;
                if (actTarget != null)
                {
                    // 激活目标规则复检：大类 + 暗藏者回退（与 IsLegalActivateTarget 方案 A 粗筛同源）
                    var kind = CardManager.MapInstanceToTargetKind(target);
                    bool stillLegal = (kind == actTarget.Kind) ||
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
                            if (rule != null && CardManager.MatchesRule(rule, target, srcCtrl)) { stillLegal = true; break; }
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

        /// <summary>执行动作序列。durationText 传入以驱动时效修正栈（P2）；targetAreaId 供机动等指向地区的动词使用。</summary>
        private static void ExecuteActions(Array<EffectAction> actions, CardInstance source, CardInstance target, string durationText = "", string targetAreaId = "")
        {
            if (actions == null) return;
            foreach (var a in actions)
            {
                ExecuteVerb(a, source, target, durationText, targetAreaId);
            }
        }

        /// <summary>执行单个动作原语。未实现者打印 TODO 占位。</summary>
        private static void ExecuteVerb(EffectAction action, CardInstance source, CardInstance target, string durationText = "", string targetAreaId = "")
        {
            switch (action.Verb)
            {
                case ActionVerb.Tap:
                    if (target != null) { target.SetTapped(true); GD.Print($"[EffectEngine] 横置 {target.PlayerTemplate?.CardName}"); OnVisualDirty?.Invoke(); }
                    else GD.Print("[EffectEngine] Tap 目标为空，跳过");
                    break;

                case ActionVerb.Untap:
                    if (target != null) { target.SetTapped(false); GD.Print($"[EffectEngine] 重置 {target.PlayerTemplate?.CardName}"); OnVisualDirty?.Invoke(); }
                    break;

                case ActionVerb.Destroy:
                    if (target != null)
                    {
                        CardManager.Instance?.MoveCardToGraveyard(target);
                        GD.Print($"[EffectEngine] 消灭 {target.PlayerTemplate?.CardName}");
                    }
                    else GD.Print("[EffectEngine] Destroy 目标为空，跳过");
                    break;

                case ActionVerb.Bounce:
                    if (target != null)
                    {
                        CardManager.Instance?.ReturnCardToHand(target);
                        GD.Print($"[EffectEngine] 移回 {target.PlayerTemplate?.CardName} 到拥有者手牌");
                    }
                    break;

                case ActionVerb.Move:
                    if (!string.IsNullOrEmpty(targetAreaId) && source != null)
                    {
                        // 机动：移动到指定地区（JC014 公路骑士在对抗前触发，targetAreaId 由 PreCombat 窗口推导"另一地区"）
                        var cm = CardManagerRef ?? CardManager.Instance;
                        if (cm != null)
                        {
                            cm.MoveCardToArea(source, targetAreaId);
                            GD.Print($"[EffectEngine] 机动：{source.PlayerTemplate?.CardName} 移动到地区 {targetAreaId}");
                        }
                        else
                        {
                            GD.Print($"[EffectEngine] 机动（{source?.PlayerTemplate?.CardName} → 地区 {targetAreaId}）— CardManager 未注入，仅记录");
                        }
                    }
                    break;

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

                case ActionVerb.DrawFromBottom:
                    // 从牌库底抽牌（秘社激活效果）。数量取自 action.Args[0]，缺省为 1。
                    if (source != null && source.ControllerId >= 0)
                    {
                        int amt = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedAmt2)) ? parsedAmt2 : 1;
                        if (TurnManagerRef != null)
                        {
                            TurnManagerRef.DrawFromBottomForPlayer(source.ControllerId, amt);
                            GD.Print($"[EffectEngine] 从牌库底抽牌：玩家{source.ControllerId} 抽 {amt} 张");
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

                case ActionVerb.Gain:
                    // 获得属性/图标/关键字：+1防御、［战斗属性］、费用减少等。
                    // 带时效（durationText 含"回合结束"）的修正登记到 P2 时效修正栈，回合结束自动还原。
                    ApplyGain(source, target, action.Args, durationText);
                    break;

                case ActionVerb.Lose:
                    GD.Print($"[EffectEngine] Lose TODO: {string.Join(",", action.Args)}");
                    break;

                case ActionVerb.Sacrifice:
                    if (source != null) { CardManager.Instance?.MoveCardToGraveyard(source); GD.Print($"[EffectEngine] 牺牲 {source.PlayerTemplate?.CardName}"); }
                    break;

                case ActionVerb.Seal:
                    GD.Print("[EffectEngine] Seal TODO：封印系统 D15 未实现");
                    break;

                case ActionVerb.Curb:
                    GD.Print($"[EffectEngine] Curb TODO: 遏制{string.Join("", action.Args)}（势力标记系统待实现）");
                    break;

                case ActionVerb.Search:
                    GD.Print($"[EffectEngine] Search TODO: {string.Join(",", action.Args)}（搜牌接口待实现）");
                    break;

                case ActionVerb.Reveal:
                    GD.Print("[EffectEngine] Reveal TODO");
                    break;

                case ActionVerb.Shuffle:
                    GD.Print("[EffectEngine] Shuffle TODO（牌库洗牌待实现）");
                    break;

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
                case ActionVerb.PreventDamage:
                case ActionVerb.RemoveToken:
                case ActionVerb.GenerateToken:
                case ActionVerb.Investigate:
                case ActionVerb.Fight:
                case ActionVerb.Power:
                case ActionVerb.Attach:
                case ActionVerb.Win:
                    GD.Print($"[EffectEngine] {action.Verb} TODO：执行器待实现");
                    break;

                // 准备步骤维护（JC068 阿格斯IV）：支付 N 费用，否则横置来源卡。
                // Args[0] 为所需费用（缺省 1）。经 PromptPayOrTap 发起"支付/横置"二选一弹窗
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
                    if (PromptPayOrTap != null)
                    {
                        // 异步弹窗：发起选择，支付/横置动作交给回调
                        PromptPayOrTap(source, payAmount);
                    }
                    else
                    {
                        // 无 UI（测试）：能付则付，否则横置
                        var pm = TurnManagerRef?.PlayerManagerRef;
                        if (pm != null && pm.GetAvailableMana(source.ControllerId) >= payAmount)
                        {
                            pm.SpendMana(source.ControllerId, payAmount);
                            GD.Print($"[EffectEngine] 准备维护：玩家{source.ControllerId} 支付 {payAmount}（阿格斯IV保持未横置）");
                        }
                        else
                        {
                            source.SetTapped(true);   // 横置阿格斯IV（发 TappedChanged → 解除对手派遣封锁）
                            GD.Print($"[EffectEngine] 准备维护：阿格斯IV 被横置，玩家{source.ControllerId} 的对手恢复秘密派遣权");
                        }
                        OnVisualDirty?.Invoke();
                    }
                    break;
                }

                // 伤害（DealDamage）：造成 N 点伤害。数值取自 action.Args[0]（缺省 1），
                // 由 KeywordEffects.Strike(amount) 等写入，支持"袭击1/袭击2"配置。
                // 实际承伤经 CardManager.DealDamageToInstance 收口（含暗藏者免疫、死亡判定）。
                case ActionVerb.DealDamage:
                    if (target != null)
                    {
                        int dmg = (action.Args.Count > 0 && int.TryParse(action.Args[0], out var parsedDmg)) ? parsedDmg : 1;
                        CardManager.Instance?.DealDamageToInstance(target, dmg);
                        GD.Print($"[EffectEngine] 造成 {dmg} 点伤害 → {target.PlayerTemplate?.CardName}");
                    }
                    else GD.Print("[EffectEngine] DealDamage 目标为空，跳过");
                    break;
            }
        }

        // ==================== P2 时效修正栈 ====================

        /// <summary>当前回合号（由 TurnManager 在结算时通过 CurrentTurn 提供，供时效登记）。</summary>
        public static int CurrentTurn { get; set; } = 1;

        /// <summary>
        /// Gain 动词落地（P2）：解析 Args 并实际修改目标卡状态。
        /// Args 约定：["防御","1"] / ["战斗属性",""] / ["费用减少","1"] 等。
        /// durationText 含"回合结束"则登记时效修正，回合结束自动还原。
        /// </summary>
        private static void ApplyGain(CardInstance source, CardInstance target, Array<string> args, string durationText)
        {
            // 受影响主体：优先 target（属性增益给目标角色），费用修正等无 target 时用 source 的拥有者
            CardInstance subject = target ?? source;
            if (subject == null) { GD.Print("[EffectEngine] Gain 主体为空，跳过"); return; }
            if (args == null || args.Count < 1) return;
            string what = args[0];
            int amt = args.Count >= 2 && int.TryParse(args[1], out var a) ? a : 1;
            bool timed = durationText.Contains("回合结束");

            switch (what)
            {
                case "防御":
                    // 防御属性：直接改 CurrentDefense（已有 HasDefense 语义），时效项记录还原量
                    if (subject.CurrentDefense < 0) subject.CurrentDefense = 0; // 原本无防御则临时获得
                    subject.CurrentDefense += amt;
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 防御 +{amt}（当前 {subject.CurrentDefense}）");
                    if (timed) RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Defense", Value = "防御", Amount = amt, ExpireTurn = CurrentTurn });
                    break;

                case "战斗属性":
                case "先手属性":
                case "调查属性":
                case "心灵属性":
                case "力量属性":
                    // 标志属性关键字：复用 P1 动态授予容器，时效项回合结束还原
                    subject.Grant("Keyword", what, 1);
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 获得关键字 [{what}]");
                    if (timed) RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Keyword", Value = what, Amount = 1, ExpireTurn = CurrentTurn });
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
                    GD.Print($"[EffectEngine] Gain 费用减少 转交玩家{pid} 费用修正栈（{amt}，filter='{filter}'，{(consumeOnce ? "一次性" : "整回合")}，timed={timed}）");
                    if (timed && pid >= 0) ApplyCostModification(pid, amt, CurrentTurn, filter, consumeOnce);
                    else GD.Print($"[EffectEngine] ⚠ 费用减少未登记：timed={timed}, pid={pid}（需 Duration 含'回合结束'且控制者有效）");
                    break;

                default:
                    // 其它属性/图标：尝试作为关键字授予
                    subject.Grant("Keyword", what, amt);
                    GD.Print($"[EffectEngine] ✓ {subject.PlayerTemplate?.CardName} 授予 [{what}]×{amt}");
                    if (timed) RegisterTimedMod(new TimedMod { TargetUid = subject.UID, Kind = "Keyword", Value = what, Amount = amt, ExpireTurn = CurrentTurn });
                    break;
            }
        }

        /// <summary>卡级时效修正登记（P2）。</summary>
        private static void RegisterTimedMod(TimedMod m) => _timedModStack.Add(m);

        /// <summary>
        /// 回合结束还原（P2 核心）：撤销所有 ExpireTurn ≤ turn 的时效修正。
        /// 卡级（防御/关键字）调用 CardInstance.Revoke / 还原 CurrentDefense；玩家级费用修正直接出栈。
        /// 由 TurnManager.OnTurnEnded 订阅调用。
        /// </summary>
        public static void ExpireTurnEndEffects(int turn)
        {
            for (int i = _timedModStack.Count - 1; i >= 0; i--)
            {
                var m = _timedModStack[i];
                if (m.ExpireTurn > turn) continue;

                if (m.Kind == "CostMod")
                {
                    GD.Print($"[EffectEngine] ⌛ 时效还原：玩家{m.PlayerId} 费用修正 +{m.Amount} 到期");
                    CostModChanged?.Invoke();
                }
                else
                {
                    var c = CardManager.Instance?.GetInstance(m.TargetUid);
                    if (c != null)
                    {
                        if (m.Kind == "Defense")
                        {
                            c.CurrentDefense -= m.Amount;
                            if (c.CurrentDefense < 0) c.CurrentDefense = -1; // 回到"无防御"
                            GD.Print($"[EffectEngine] ⌛ 时效还原：{c.PlayerTemplate?.CardName} 防御 -{m.Amount}（当前 {c.CurrentDefense}）");
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
            CostModChanged?.Invoke();
        }

        // ==================== 玩家级费用修正栈（P2，JC012 等） ====================

        /// <summary>费用修正栈变化事件（登记/消费/到期），供 UI 刷新手牌减费提示（CardUI.RefreshCostReduction）。</summary>
        public static event System.Action CostModChanged;

        /// <summary>登记玩家级费用修正（如"下一张帷幕守望牌费用-1"）。
        /// filterText 为空=全匹配；否则卡名包含该串 **或** 卡 Organization 等于该串才生效（2026-08-11 扩展 Organization 维度）。
        /// consumeOnce=true 表示"下一张"语义：打出/翻面符合条件牌一次后由 ConsumeCostModification 移除（回合结束未用则随 ExpireTurnEndEffects 出栈流失）。</summary>
        public static void ApplyCostModification(int playerId, int amount, int expireTurn, string filterText, bool consumeOnce = false)
        {
                    _timedModStack.Add(new TimedMod { PlayerId = playerId, Kind = "CostMod", Amount = amount, ExpireTurn = expireTurn, FilterText = filterText ?? "", ConsumeOnce = consumeOnce });
            GD.Print($"[EffectEngine] ✓ 玩家{playerId} 费用修正 +{amount}（filter='{filterText}'，回合{expireTurn}到期，{(consumeOnce ? "一次性" : "整回合")}）");
            CostModChanged?.Invoke();
        }

        /// <summary>消费并移除玩家的一次性费用修正项（打出/翻面符合条件牌后调用，实现"下一张"语义）。
        /// 仅移除匹配 filterText 且 ConsumeOnce=true 的项；非一次性项（整回合全减免）不受影响。</summary>
        public static void ConsumeCostModification(int playerId, string filterText)
        {
            for (int i = _timedModStack.Count - 1; i >= 0; i--)
            {
                var m = _timedModStack[i];
                if (m.Kind != "CostMod" || m.PlayerId != playerId || !m.ConsumeOnce) continue;
                if (string.IsNullOrEmpty(m.FilterText) || m.FilterText == (filterText ?? ""))
                {
                    GD.Print($"[EffectEngine] ⌛ 消费一次性费用修正：玩家{playerId} 的 '{m.FilterText}' 已使用，移除");
                    _timedModStack.RemoveAt(i);
                    CostModChanged?.Invoke();
                }
            }
        }

        /// <summary>查询玩家当前对某张即将打出的卡的费用减免额。
        /// 匹配：filterText 为空=全匹配；否则 cardName 含 filterText **或** organization == filterText（2026-08-11 扩展 Organization 维度）。</summary>
        public static int GetCostModification(int playerId, string cardName, string organization = "")
        {
            int total = 0;
            foreach (var m in _timedModStack)
            {
                if (m.Kind != "CostMod" || m.PlayerId != playerId) continue;
                if (string.IsNullOrEmpty(m.FilterText)
                    || (cardName != null && cardName.Contains(m.FilterText))
                    || (!string.IsNullOrEmpty(organization) && organization == m.FilterText))
                    total += m.Amount;
            }
            return total;
        }

        // ==================== D2 威名去重（战斗对抗获胜放置势力标志） ====================
        // 维度：areaUid + 控制者，保证"同一控制者多个威名在同一地区只放置一个势力标志"。
        // 单次战斗对抗只 Emit 一次 CombatWon（见 TriggerEmitPoints.CombatWon）；
        // 每次 Emit 前由 ResetCombatWonDedup 清空，结算 PlaceToken 时按维度查重。
        private static readonly System.Collections.Generic.Dictionary<string, bool> _combatWonMarkPlaced
            = new System.Collections.Generic.Dictionary<string, bool>();
        private static string _combatWonKey(string areaUid, int controller)
            => $"{areaUid}|{controller}";

        /// <summary>清空威名放置去重表（每次战斗对抗 Emit 前调用，保证单次战斗维度内去重）。</summary>
        public static void ResetCombatWonDedup() => _combatWonMarkPlaced.Clear();

        // ==================== 对局级全量重置（B2：单场景常驻 + 显式 Reset） ====================
        // 一局结束、重开下一局前调用（由 GameInitializer.ResetMatch 编排，且必须先于 CardManager.Reset 调用）。
        // 作用：清空本引擎全部静态对局状态注册表 + 复位 CurrentTurn。
        // 注意：仅清空"对局状态数据"，绝不清空模板级注册表（CardEffectFactory）与常驻注入引用/委托
        // （TurnManagerRef/CardManagerRef/ZoneManagerRef/IsStackCircuitBroken/OnVisualDirty/PromptPayOrTap/CostModChanged）。
        // 采用"直接清空"方案：B2 下第二局会重建全部 CardInstance，旧卡对象整体被抛弃，无需先 Revoke。
        public static void ResetAll()
        {
            _continuousRegistry.Clear();
            _activePrereqCards.Clear();
            _auraGrants.Clear();
            _timedModStack.Clear();
            _combatWonMarkPlaced.Clear();
            _pendingPrereqCards.Clear();
            _blockDeployCovertPlayers.Clear();
            CurrentTurn = 1;
            GD.Print("[EffectEngine] ResetAll: 7 个静态注册表已清空, CurrentTurn=1");
        }

        // ==================== 条件判定（最小） ====================
        public static bool EvaluateCondition(string predicate, CardInstance source, CardInstance target, CardInstance contextCard = null, int triggeringPlayerId = -1, string triggeringAreaId = null)
        {
            // 最小实现：支持几种已明确谓词；其余默认视为满足，避免卡死
            switch (predicate)
            {
                case "已横置":
                    return target != null && target.IsTapped;
                case "成功封印":
                    // 封印系统未实现，暂视为满足（后续接 D15 后改为真实判定）
                    return true;
                case "心灵领域忠诚≥2（资产区心心）":
                    // 资产区忠诚统计：PlayerManager 已有 GetDomainLoyalty，待接入
                    GD.Print("[EffectEngine] 条件「心灵领域忠诚≥2」暂默认满足（资产区忠诚统计待接入）");
                    return true;
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
                    int oppHand = CardManager.Instance != null ? CardManager.Instance.GetHandCardsByOwner(oppId).Count : 0;
                    GD.Print($"[EffectEngine] 条件「OpponentHandLe3」：对手玩家{oppId} 手牌数={oppHand} → {(oppHand <= 3 ? "满足" : "不满足")}");
                    return oppHand <= 3;
                case "WinnerIsSelf":
                    // "X对抗获胜"类触发通用条件：本卡控制者 == 对抗获胜方（winnerId）。
                    // 由 TriggerRegistry 收集 OnCombatWon/OnInvestigationWon/OnFactionWon 时，
                    // 经 TriggerEffectInstance.WinnerId → StackEntry.WinnerId → ResolveEffect(winnerId) 透传；
                    // 非对抗路径（winnerId<0）或无来源卡时无法判定，视为不满足。
                    // 命名规范：对应原始能力名 Combat/Investigation/Power（势力）三种对抗，本谓词对三者通用。
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
        private static readonly System.Collections.Generic.HashSet<CardInstance> _pendingPrereqCards
            = new System.Collections.Generic.HashSet<CardInstance>();

        // ==================== 全局封锁秘密派遣表（JC068 阿格斯IV） ====================
        // 记录"被禁止秘密派遣行动的玩家ID"（即未横置阿格斯IV控制者的对手）。
        // 由带 BlockDeployCovert 且前提（自身未横置）满足的持续效果在 RegisterPersistent / ReevaluatePrereqs 时登记；
        // 前提丧失或离场时移除。TurnManager.DeployCovert / CanDeployCovert 经 IsDeployCovertBlocked 实时查询。
        private static readonly System.Collections.Generic.HashSet<int> _blockDeployCovertPlayers
            = new System.Collections.Generic.HashSet<int>();

        /// <summary>某玩家当前是否被"未横置阿格斯IV"封锁而禁止秘密派遣行动（JC068）。</summary>
        public static bool IsDeployCovertBlocked(int playerId)
            => _blockDeployCovertPlayers.Contains(playerId);

        /// <summary>
        /// 注册一张卡的【持久效果】（D19 统一入口，替代原 RegisterContinuous）：
        ///  - Static（静止/禁止，如"不能被装备结附"）→ 按 Scope 置 CardInstance.StaticFlags（默认争夺区）。
        ///  - Continuous（持续，如"公开"/条件"白眼"）→ 逐条 ContinuousGrants 调 CardInstance.Grant；
        ///    带结构化 PrereqSpec 的，先 EvaluatePrereq：满足才授予，不满足登记进 _pendingPrereqCards 监听。
        /// 前提变化（资产区/忠诚变动）由 ReevaluatePrereqs 触发开关（见 D19-②）。
        ///
        /// 手牌期（如"公开"拦截派遣暗藏）仍由 HasPublicKeyword 双层补判保证，不依赖本方法。
        /// </summary>
        public static void RegisterPersistent(CardInstance card)
        {
            if (card?.PlayerTemplate is not PlayerCardData tmpl) return;
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
            GD.Print($"[JC001DEBUG] RegisterPersistent 进入: {tmpl.CardName} (UID={card.UID}, 效果数={tmpl.EffectDataList.Count})");
            if (_continuousRegistry.ContainsKey(card.UID)) return; // 防重复注册
            var applied = new System.Collections.Generic.List<EffectGrant>();

            foreach (var e in tmpl.EffectDataList)
            {
                if (e.Timing == EffectTiming.Static)
                {
                    // 静止/禁止效果：按生效区判定（默认争夺区/结附区；非对应区不生效）
                    // 【方案C】改为统一 IsEffectActiveInZone（效果粒度，尊重 e.Scope 声明）。
                    // 原旧逻辑：`if (e.Scope != ZoneScope.ContestZone) continue;` 已在下方收敛为 IsEffectActiveInZone，
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
                        // [DIAG] 追踪阿格斯封锁注册次数与实例，排查多实例（UI 只见一张但逻辑多个）
                        GD.Print($"[DIAG 阿格斯IV] RegisterPersistent 效果B: UID={card.UID} Zone={card.CurrentZone} ctrl={card.ControllerId} 宿主={(card.AttachedTo?.UID ?? "∅")} 前提满足={prereqMet}");
                        SetBlockDeployCovert(card, active: true);
                        _activePrereqCards.Add(card); // 登记到已生效集合，供横置/竖置时 ReevaluatePrereqs 重判
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
                    if (e.ContinuousGrants != null)
                    {
                        // 线性计量（ScaleByLoyalty）：授予量 = g.Amount × 领域忠诚点数 N
                        int mult = (e.PrereqSpec != null && e.PrereqSpec.ScaleByLoyalty)
                            ? GetPrereqLoyaltyValue(card, e.PrereqSpec) : 1;
                        foreach (var g in e.ContinuousGrants)
                        {
                            if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                            int realAmount = g.Amount * mult;
                            card.Grant(g.Kind, g.Value, realAmount);
                            // 记录实际授予量（深拷贝），撤销时按 realAmount 精确还原（多源叠加计数要求）
                            applied.Add(new EffectGrant { Kind = g.Kind, Value = g.Value, Amount = realAmount });
                            GD.Print($"[EffectEngine] ✓ 持续授予 {card.PlayerTemplate?.CardName} ← {g.Kind}:{g.Value} (×{realAmount}{(mult != 1 ? $" = {g.Amount}×{mult}" : "")})");
                        }
                    }
                    GD.Print($"[EffectEngine] 注册持续效果 {e.EffectId} @ {e.Scope}：{e.ContinuousEffect}");
                }
            }

            if (applied.Count > 0)
            {
                _continuousRegistry[card.UID] = applied;
                _activePrereqCards.Add(card); // 登记到已生效集合，供 ReevaluatePrereqs 遍历
            }
        }

        /// <summary>读取某卡控制者在指定区域（默认资产区）的某领域忠诚点数 N（线性计量用）。</summary>
        private static int GetPrereqLoyaltyValue(CardInstance card, ContinuousPrereqSpec spec)
        {
            var pm = TurnManagerRef?.PlayerManagerRef;
            if (pm == null) return 0;
            var loyalty = pm.GetDomainLoyalty(card.ControllerId);
            loyalty.TryGetValue(spec.Domain, out int v);
            return v;
        }

        /// <summary>卡类型默认生效区（方案C 声明式 Scope 的默认推导）。
        /// 未声明 Scope（Specified）的效果回退到此处：附属→AttachTarget（严格=仅 AttachedZone）；角色→ContestZone（仅 AreaZone）。
        /// 未来扩展（墓地/手牌生效等）在此加判断，或让具体效果声明 Scope 覆盖。</summary>
        public static ZoneScope GetDefaultScope(CardInstance card)
        {
            if (card?.PlayerTemplate is not PlayerCardData tmpl) return ZoneScope.Specified;
            bool isAttachment = (tmpl.CategoryFlag & CardCategory.Attachment) != 0;
            return isAttachment ? ZoneScope.AttachTarget : ZoneScope.ContestZone;
        }

        /// <summary>某效果在其来源卡当前所在区是否生效（方案C 统一判定，替代三处散落的"卡类型+CurrentZone"）。
        /// 判定基准：效果声明的 Scope；未声明（Specified）→ 卡类型默认区。
        /// 接入点：RegisterPersistent（持续/静止）、TriggerRegistry（触发）、ActivateEligibility（激活）。
        /// 注意：AttachTarget 采用严格规则=仅 AttachedZone（Z1/Z4，AreaZone 附属暂不生效）。
        /// 未来加"某区生效"只需在 switch 增分支，勿在调用处重复写区判断（避免遗漏）。</summary>
        public static bool IsEffectActiveInZone(EffectData e, CardInstance card)
        {
            if (card == null || e == null) return false;
            ZoneScope scope = (e.Scope == ZoneScope.Specified) ? GetDefaultScope(card) : e.Scope;
            switch (scope)
            {
                case ZoneScope.ContestZone:  return card.CurrentZone == CardZone.AreaZone;
                case ZoneScope.AttachTarget: return card.CurrentZone == CardZone.AttachedZone; // 严格版（Z1/Z4）
                case ZoneScope.HandZone:     return card.CurrentZone == CardZone.Hand;
                case ZoneScope.AssetZone:    return card.CurrentZone == CardZone.AssetZone;
                case ZoneScope.Graveyard:    return card.CurrentZone == CardZone.Graveyard; // 未来
                case ZoneScope.DeckZone:     return card.CurrentZone == CardZone.Deck;      // 未来
                default:                     return false;
            }
        }

        /// <summary>结构化前提判定（D19-②）。PrereqSpec 优先；无则回退旧文本（仅"常驻"生效）。
        /// 注意：ScaleByLoyalty=true 时视为"线性计量"——N&gt;0 即满足，授予量由调用方按 N 缩放。</summary>
        public static bool EvaluatePrereq(CardInstance card, EffectData e)
        {
            if (e.PrereqSpec != null && e.PrereqSpec.Kind != PrereqKind.None)
            {
                var spec = e.PrereqSpec;
                if (spec.Kind == PrereqKind.DomainLoyalty)
                {
                    // 查询控制者在该区域（默认资产区）的某领域忠诚
                    int n = GetPrereqLoyaltyValue(card, spec);
                    GD.Print($"[JC001DEBUG] EvaluatePrereq 领域忠诚: 卡={card.PlayerTemplate?.CardName} 控制者={card.ControllerId} 领域={spec.Domain} 当前={n} 需≥{spec.Amount} → {(n >= spec.Amount ? "满足" : "不满足")}");
                    if (spec.ScaleByLoyalty) return n > 0;          // 线性计量：有 1 点即满足
                    return n >= spec.Amount;                         // 布尔开关：≥阈值才满足
                }
                if (spec.Kind == PrereqKind.SelfUntapped)
                {
                    // 前提：来源卡自身未横置（JC068 阿格斯IV"只要未横置"）
                    return !card.IsTapped;
                }
                return false;
            }
            // 兼容旧文本前提
            return string.IsNullOrEmpty(e.ContinuousPrereq) || e.ContinuousPrereq == "常驻";
        }

        /// <summary>判断某持续效果是否携带"全局封锁秘密派遣"（BlockDeployCovert）静止标记（JC068）。
        /// 效果B 用 Continuous+PrereqSpec(SelfUntapped) 表达动态前提，StaticEffects 承载封锁意图。</summary>
        private static bool HasBlockDeployCovert(EffectData e)
        {
            if (e?.StaticEffects == null) return false;
            foreach (var s in e.StaticEffects)
                if (s != null && s.Kind == StaticKind.BlockDeployCovert) return true;
            return false;
        }

        /// <summary>按来源卡登记/撤销"封锁其控制者的对手秘密派遣"（JC068 阿格斯IV）。
        /// active=true：前提（未横置）满足，把 controlPlayer 的对手记入封锁表；active=false：移除。
        /// 规则：阿格斯IV的控制者为 X，则 X 的对手（另一玩家）被封锁。</summary>
        private static void SetBlockDeployCovert(CardInstance card, bool active)
        {
            if (card == null) return;
            int opponent = card.ControllerId == 1 ? 2 : 1;
            // [DIAG] 打印阿格斯IV完整实例信息：UID、Zone、控制者、拥有者、宿主(AttachedTo)、是否横置
            // 用于排查 UI 只见一张但逻辑存在多个实例的问题（同一阿格斯不应出现两个 UID 的封锁登记）。
            GD.Print($"[DIAG 阿格斯IV] 封锁开关 active={active} 卡={card.PlayerTemplate?.CardName}(UID={card.UID}) Zone={card.CurrentZone} ctrl={card.ControllerId} owner={card.OwnerId} 宿主AttachedTo={(card.AttachedTo?.PlayerTemplate?.CardName ?? "∅")}(UID={(card.AttachedTo?.UID ?? "∅")}) IsTapped={card.IsTapped} → 封锁玩家{opponent}");
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
        public static void ReevaluatePrereqs(int playerId)
        {
            // 全集 = 已生效(activePrereqCards) + 待定(pending)。去重后逐张用当前 ControllerId 重判。
            var allCards = new System.Collections.Generic.HashSet<CardInstance>(_pendingPrereqCards);
            foreach (var c in _activePrereqCards) allCards.Add(c);
            if (allCards.Count == 0) return;

            foreach (var card in allCards.ToList())
            {
                if (card == null || card.ControllerId != playerId) continue;
                var tmpl = card.PlayerTemplate as PlayerCardData;
                if (tmpl == null) { _pendingPrereqCards.Remove(card); _continuousRegistry.Remove(card.UID); continue; }

                bool anyNowMet = false;
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e.Timing != EffectTiming.Continuous) continue;
                    if (e.PrereqSpec == null || e.PrereqSpec.Kind == PrereqKind.None) continue;
                    if (EvaluatePrereq(card, e))
                    {
                        anyNowMet = true;
                        // 线性计量（ScaleByLoyalty）即使已生效也要按最新 N 重算（N 增减→黑刀增减）
                        int mult = (e.PrereqSpec != null && e.PrereqSpec.ScaleByLoyalty)
                            ? GetPrereqLoyaltyValue(card, e.PrereqSpec) : 1;
                        bool alreadyActive = _continuousRegistry.ContainsKey(card.UID);
                        if (alreadyActive && mult == 1) continue; // 非缩放且已生效：维持

                        // 已生效的线性计量卡：先按旧量撤销，再按新量授予，避免 CurrentCombat 等重复叠加
                        if (alreadyActive)
                        {
                            if (_continuousRegistry.TryGetValue(card.UID, out var oldApplied))
                                foreach (var g in oldApplied) card.Revoke(g.Kind, g.Value, g.Amount);
                            _continuousRegistry.Remove(card.UID);
                        }

                        var applied = new System.Collections.Generic.List<EffectGrant>();
                        foreach (var g in e.ContinuousGrants ?? new Array<EffectGrant>())
                        {
                            if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                            int realAmount = g.Amount * mult;
                            card.Grant(g.Kind, g.Value, realAmount);
                            applied.Add(new EffectGrant { Kind = g.Kind, Value = g.Value, Amount = realAmount });
                        }
                        if (applied.Count > 0)
                        {
                            _continuousRegistry[card.UID] = applied;
                            _activePrereqCards.Add(card); // 登记到已生效集合，供 ReevaluatePrereqs 遍历
                        }
                        GD.Print($"[EffectEngine] ⟳ 前提达成{(alreadyActive ? "(按N重算)" : "")}，持续效果 {e.EffectId} 现已生效 (×{mult})");
                        // 全局封锁秘密派遣（JC068）：前提（未横置）满足 → 登记封锁对手派遣（Set 幂等，重复调用无害）
                        if (HasBlockDeployCovert(e))
                            SetBlockDeployCovert(card, active: true);
                    }
                    else
                    {
                        if (_continuousRegistry.TryGetValue(card.UID, out var applied))
                        {
                            foreach (var g in applied) card.Revoke(g.Kind, g.Value, g.Amount);
                            _continuousRegistry.Remove(card.UID);
                            GD.Print($"[EffectEngine] ⟳ 前提丧失，持续效果 {e.EffectId} 已还原");
                        }
                        // 全局封锁秘密派遣（JC068）：前提（已横置）丧失 → 解除封锁（Set 幂等）
                        if (HasBlockDeployCovert(e))
                            SetBlockDeployCovert(card, active: false);
                    }
                }
                // 已满足则移出待定集合；仍不满足则留在待定集合等待后续资产变动
                if (anyNowMet) _pendingPrereqCards.Remove(card);
                else _pendingPrereqCards.Add(card);
            }
            // 持续光环（D19-③）重算：区域变动（角色进场/离场/夺控）后同步刷新所有光环的受影响集合
            ReevaluateAuras(playerId);
        }

        /// <summary>注销一张卡的持久效果（卡离场/被封印/被消灭时调用，精确还原 + 清除静止标记）。</summary>
        public static void UnregisterPersistent(CardInstance card)
        {
            if (card == null) return;
            _pendingPrereqCards.Remove(card);
            _activePrereqCards.Remove(card);
            if (_continuousRegistry.TryGetValue(card.UID, out var applied))
            {
                foreach (var g in applied)
                    card.Revoke(g.Kind, g.Value, g.Amount);
                _continuousRegistry.Remove(card.UID);
                GD.Print($"[EffectEngine] 注销持续效果 UID={card.UID}（已还原 {applied.Count} 项授予）");
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
            if (card.PlayerTemplate is PlayerCardData tmpl2)
            {
                foreach (var e in tmpl2.EffectDataList)
                {
                    if (e.Timing == EffectTiming.Continuous && HasBlockDeployCovert(e))
                    {
                        SetBlockDeployCovert(card, active: false);
                        break;
                    }
                }
            }
            // 清除静止标记（D19-⑦）
            if (card.PlayerTemplate is PlayerCardData tmpl)
            {
                foreach (var e in tmpl.EffectDataList)
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
        private static void ApplyAura(CardInstance source, EffectData e)
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
                // B2 过滤语义：
                //  - AuraRequiresNoDomain=true：目标须"完全没有魔法领域"（实例 MagicDomains 为空列表）才受影响，
                //    用于 JC060——有鲜血领域等也不必受影响；
                //  - 否则沿用 AuraFilterExcludeDomain：排除已含该单域者。
                bool excluded = e.AuraRequiresNoDomain
                    ? (t.MagicDomains.Count > 0)
                    : t.MagicDomains.Contains(e.AuraFilterExcludeDomain);
                if (excluded) { GD.Print($"[光环LOG] 跳过(已有领域/排除域) {t.PlayerTemplate?.CardName} 领域数={t.MagicDomains.Count} AuraRequiresNoDomain={e.AuraRequiresNoDomain}"); continue; }
                if (!grantedTo.ContainsKey(t.UID))
                    grantedTo[t.UID] = new System.Collections.Generic.List<EffectGrant>();
                foreach (var g in e.ContinuousGrants)
                {
                    if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                    t.Grant(g.Kind, g.Value, g.Amount);
                    grantedTo[t.UID].Add(g);
                    count++;
                }
            }
        }

        /// <summary>持续光环重算（D19-③）：某玩家区域变动（角色进场/离场/夺控）后调用，对所有含光环的源卡重算其受影响集合。
        /// 差量：新符合条件者补授；已不符合者（离场/夺控/已获领域）撤销本源授予。挂在 ZoneManager.OnPostMove / 夺控后。</summary>
        public static void ReevaluateAuras(int playerId)
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
                    bool excludedByDomain = auraEffect.AuraRequiresNoDomain
                        ? (t != null && t.MagicDomains.Count > 0)
                        : (t != null && t.MagicDomains.Contains(auraEffect.AuraFilterExcludeDomain));
                    // 失效条件：目标已离场 / 不在源卡当前控制者区域（被夺到别处）/ 源卡离场 / 目标已获领域 / 目标暗藏(背面)
                    bool invalid = !srcOnField || t == null || t == src || excludedByDomain || (t != null && t.IsCovert)
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
                // 2) 补授：源卡在场时，对 src.ControllerId 区域内符合条件、尚未授予的他者补授
                if (srcOnField)
                {
                    foreach (var t in srcAreaCards)
                    {
                        if (t == null || t == src) continue;
                        if (t.IsCovert) continue; // 目标暗藏(背面)不授予光环
                        bool excludedByDomain = auraEffect.AuraRequiresNoDomain
                            ? (t.MagicDomains.Count > 0)
                            : t.MagicDomains.Contains(auraEffect.AuraFilterExcludeDomain);
                        if (excludedByDomain) continue;
                        if (grantedTo.ContainsKey(t.UID)) continue; // 已授过
                        grantedTo[t.UID] = new System.Collections.Generic.List<EffectGrant>();
                        foreach (var g in auraEffect.ContinuousGrants ?? new Array<EffectGrant>())
                        {
                            if (g == null || string.IsNullOrEmpty(g.Value)) continue;
                            t.Grant(g.Kind, g.Value, g.Amount);
                            grantedTo[t.UID].Add(g);
                            GD.Print($"[EffectEngine] ✦ 光环补授 {src.PlayerTemplate?.CardName} → {t.PlayerTemplate?.CardName} ← {g.Kind}:{g.Value}");
                        }
                    }
                }
            }
            // 光环补授/撤销已改写 MagicDomains/攻击力/调查力等数据，经总线通知表现层刷新
            // StatsLabel 与卡面动态标签（夺控场景下 OnPostMove 的 SyncDerivedUI 早于本调用，故需此处二次收口）。
            OnVisualDirty?.Invoke();
        }

        // 兼容别名（旧调用点 RegisterContinuous / UnregisterContinuous 仍可用）
        public static void RegisterContinuous(CardInstance card) => RegisterPersistent(card);
        public static void UnregisterContinuous(CardInstance card) => UnregisterPersistent(card);

        /// <summary>查询某卡当前是否有已注册的持续效果（调试/规则用）。</summary>
        public static bool HasContinuous(CardInstance card)
            => card != null && _continuousRegistry.ContainsKey(card.UID);

        /// <summary>查询某卡是否带某静止/禁止标记（D19-⑤ 消费入口，如 CannotBeAttached）。</summary>
        public static bool HasStaticFlag(CardInstance card, StaticKind flag)
            => card != null && card.HasStaticFlag(flag);

        // ==================== 触发收集（供 TurnManager 桥接） ====================

        /// <summary>
        /// 从卡实例的 EffectDataList 收集指定时机的触发效果（进场/现身）。
        /// 返回 EffectData 列表，由调用方（TurnManager 桥接或通过 GameEventBus 总线）决定是否入堆叠。
        /// </summary>
        public static System.Collections.Generic.List<EffectData> CollectTriggers(CardInstance card, EffectTiming timing)
        {
            var result = new System.Collections.Generic.List<EffectData>();
            if (card?.PlayerTemplate is not PlayerCardData tmpl) return result;
            foreach (var e in tmpl.EffectDataList)
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
        public static void PushActivationToStack(StackManager sm, CardInstance card, EffectData ability, CardInstance target = null,
            string sourceZoneDetailSnapshot = null, int sourceControllerSnapshot = -1)
        {
            if (sm == null || card == null || ability == null) return;
            var entry = new StackEntry(card.OwnerId, $"[激活]{ability.EffectId}")
            {
                Kind = ResolveKind.Trigger,
                IsCard = false,
                ControllerId = card.ControllerId,   // 箭头按控制者着色（D31）
                SourceCardUid = card.UID,
                TargetCardUid = target?.UID ?? "",
                EffectDataEffect = ability,
                EffectText = ability.EffectId,
                // 参照 TurnManager.PushTriggerToStack：填充 TriggerInstance，
                // 使 StackEntryUI 能经 entry.TriggerInstance.Source.PlayerTemplate 取到来源卡，渲染卡图。
                TriggerInstance = new Script.Data.Effects.TriggerEffectInstance
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
                    Kind = Script.Game.Entities.StackTargetRef.TargetKind.Card,
                    CardUid = target.UID,
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

        /// <summary>事务卡打出结算：执行卡上所有 SpellCast 效果（F组）。</summary>
        public static void ResolveSorcery(CardInstance sorcery, string targetUid, int targetVersionId = -1)
        {
            if (sorcery?.PlayerTemplate is not PlayerCardData tmpl) return;
            var spells = new System.Collections.Generic.List<EffectData>();
            foreach (var e in tmpl.EffectDataList)
            {
                if (e.Timing == EffectTiming.SpellCast) spells.Add(e);
            }
            if (spells.Count == 0) return;
            CardInstance target = string.IsNullOrEmpty(targetUid) ? null : CardManager.Instance?.GetInstance(targetUid);

            // D16 新方案：结算前校验，任一需目标效果的目标已丢失（版本号不一致）→ 整张事务卡不结算并直接进墓地（用户铁律）
            foreach (var spell in spells)
            {
                if (ValidateTargetAtResolve(spell, sorcery, target, "", targetVersionId) == TargetValidation.Lost)
                {
                    GD.Print($"[EffectEngine] ⚑ [D16] 事务卡 {tmpl.CardName} 目标丢失 → 整张不结算，卡进墓地");
                    CardManager.Instance?.MoveCardToGraveyard(sorcery);
                    return;
                }
            }

            foreach (var spell in spells)
                ResolveEffect(spell, sorcery, target, "", -1, targetVersionId);
        }

        // ==================== 激活能力入口 ====================

        /// <summary>
        /// 角色/附属激活能力执行入口（D9 激活框架未实现，这里提供执行体）。
        /// 调用方需先校验 ActivateCondition/Cost（激活许可），校验通过再调本方法。
        /// </summary>
        public static void ActivateAbility(CardInstance source, EffectData data, CardInstance target)
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
            BaseCardData tmpl = source.PlayerTemplate as PlayerCardData;
            if (tmpl == null) tmpl = source.CouncilTemplate as CouncilCardData;

            // 0) 规则层原子锁（核心需求 7.3）：原子操作（出牌/结算/触发排序/选目标窗口）执行期间
            // 冻结响应权，任何激活能力（含快速行动 JC003 类）均不可在原子期间发动；与 Pass/PlayCard 一致。
            if (TurnManager.Instance != null && TurnManager.Instance.IsAtomicLocked)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 原子操作进行中，拒绝激活 {data.EffectId}");
                return;
            }

            // 1) 优先权门禁：当前控制者拥有优先权（快速效果本身即“拥有优先权即可发动”）
            // 控制权语义（核心需求 / D28）：夺控场景下以 ControllerId 归属，而非 OwnerId。
            if (TurnManager.Instance != null && TurnManager.Instance.PriorityPlayerId != source.ControllerId)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 无优先权，拒绝激活 {data.EffectId}（Priority={TurnManager.Instance.PriorityPlayerId}, Controller={source.ControllerId}）");
                return;
            }

            // 1.5) 窗口门禁（D9）：区分"快速行动"与"行动"
            //   - 快速行动（ActivateWindow 含"快速"，如 JC003）：拥有优先权即可发动，堆叠开放时作为响应插队。
            //   - 行动（ActivateWindow 含"行动"）：仅限【该玩家自己的行动步骤 + 堆叠为空 + 有行动权】。
            //     先手玩家→ActionFirst，后手玩家→ActionSecond（复用 CanEnterBasicActionMask 的精确判定）。
            if (data.ActivateWindow.Contains("行动"))
            {
                var tm = TurnManager.Instance;
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
            if (!string.IsNullOrEmpty(data.ActivateCondition) &&
                data.ActivateCondition.Contains("横置本角色") &&
                source.IsTapped)
            {
                GD.PrintErr($"[EffectEngine] ⛔ {tmpl.CardName} 已横置，不能作为条件发动 {data.EffectId}");
                return;
            }

            // 3) 费用校验与扣除（控制权语义 / D29：由控制者付费，与 CanActivate 高亮判定对齐）
            int cost = data.Cost;
            var pm = TurnManager.Instance?.PlayerManagerRef;
            if (cost > 0 && pm != null)
            {
                if (pm.GetAvailableMana(source.ControllerId) < cost)
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 费用不足，拒绝激活 {data.EffectId}（需 {cost}，控制者{source.ControllerId}）");
                    return;
                }
                pm.SpendMana(source.ControllerId, cost);
                OnVisualDirty?.Invoke();   // 费用支付（横置资产卡）已改模型，刷新 UI
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
                OnVisualDirty?.Invoke();   // 本角色横置已改模型，刷新 UI
            }

            // 3.5) 激活条件：牺牲本角色（付出代价，离场类）
            //   文档 §3.3："牺牲本角色"作为激活代价，在【入堆叠之前】处理（非效果动作）。
            //   先横置自身（3 步），再牺牲自身入墓地；牺牲后 source 仍被 PushActivationToStack 引用，
            //   结算 Destroy(target) 基于已捕获的 target 实例，不受影响。
            //   已横置不能发动的门禁见第 2 步；此处执行实际牺牲。
            if (!string.IsNullOrEmpty(data.ActivateCondition) &&
                data.ActivateCondition.Contains("牺牲本角色"))
            {
                if (source != null && CardManager.Instance != null)
                {
                    GD.Print($"[EffectEngine] ✝ {tmpl.CardName} 因激活牺牲自身（入堆叠前代价）");
                    CardManager.Instance.MoveCardToGraveyard(source);
                    OnVisualDirty?.Invoke();
                }
                else
                {
                    GD.PrintErr($"[EffectEngine] ⛔ 牺牲本角色失败（source 为空或 CardManager 缺失），拒绝激活 {data.EffectId}");
                    return;
                }
            }

            // 4) 目标合法性校验（若需选目标）
            if (data.Target != null && data.Target.NeedsSelection && target == null)
            {
                GD.PrintErr($"[EffectEngine] ⛔ 激活 {data.EffectId} 缺少目标");
                return;
            }

            // ===== 入堆叠（由堆叠统一结算，含优先权让过） =====
            var sm = TurnManager.Instance?.StackManagerRef;
            if (sm != null)
            {
                PushActivationToStack(sm, source, data, target, srcZoneSnap, srcCtrlSnap);
            }
            else
            {
                // 无堆叠管理器时回退直接结算（降级路径，不推荐）
                ResolveEffect(data, source, target);
            }
        }
    }
}
