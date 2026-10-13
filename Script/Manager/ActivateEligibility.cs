using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Game.Entities;
using System.Collections.Generic;
using Script.Enums;

namespace Script.Manager
{
    /// <summary>
    /// D9 激活框架：可发动性判定服务。
    /// 判定一张场上卡当前是否可被玩家发动角色激活能力，条件：
    ///  1) 卡面含 CharacterActivated（或 AttachmentActivated）效果；
    ///  2) 当前玩家拥有优先权（TurnManager.PriorityPlayerId == 卡控制者）；
    ///  3) 费用足够（PlayerManager.GetAvailableMana >= Cost）；
    ///  4) 若有需选目标的效果，则场上存在至少一个合法目标（角色/暗藏者等）。
    /// 调用方可据此给卡牌加"可发动高亮边框"，或在拖拽落点校验时复用 IsLegalActivateTarget。
    /// </summary>
    public static class ActivateEligibility
    {
        /// <summary>返回某玩家当前所有可发动的激活能力（含来源卡与对应 EffectData）。</summary>
        public static List<(CardInstance Source, EffectData Ability)> GetEligibleAbilities(int playerId, TurnManager tm, PlayerManager pm, CardManager cm)
        {
            var result = new List<(CardInstance, EffectData)>();
            if (tm == null || pm == null || cm == null) return result;

            // 门禁 2：当前必须拥有优先权
            if (tm.PriorityPlayerId != playerId) return result;

            // 遍历场上卡，按"当前控制者"过滤（核心需求 / D28）：夺控场景下以 ControllerId 归属，
            // 而非 OwnerId，确保控制者才能发动其控制的卡。
            // 【K27】"**手牌行动**"：显式声明 `Scope = ZoneScope.HandZone` 的激活类效果可在**手牌**中发动
            // （JC110「手牌行动4」）→ 候选扫描把该玩家的手牌也纳入（效果粒度生效区判定会筛掉其余手牌效果）。
            var field = cm.GetAreaCards() ?? new List<CardInstance>();
            var scan = new List<CardInstance>(field);
            var handCards = cm.ZoneManagerRef?.GetZoneCards(CardZone.Hand, playerId);
            if (handCards != null)
                foreach (var h in handCards)
                    if (h != null && h.ControllerId == playerId && !scan.Contains(h)) scan.Add(h);
            int mana = pm.GetAvailableMana(playerId);

            foreach (var card in scan)
            {
                if (card?.ControllerId != playerId) continue;
                // 【P3·K20】"文本视作空白"的卡失去效果 → 不提供任何候选能力（JC101）
                if (Script.Manager.EffectEngine.Instance?.IsTextBlank(card) == true) continue;
                if (card?.PlayerTemplate is PlayerCardData tmpl)
                {
                    // 【P3·K13】候选＝卡面固有激活能力 ∪ **运行期授予的**激活能力（JC019 镇魔枪授予宿主的那条）
                    foreach (var e in GetAllActivateAbilities(card))
                    {
                        if (e.Timing is not (EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated)) continue;

                        // 【K26】阶段排除：该效果声明了"不可发动的阶段"且当前正处于该阶段 → 本效果不可发动
                        if (PhaseGateRule.IsExcluded(e, tm)) continue;

                        // 【K23】次数上限：该能力本回合已用满（每张卡·每回合·每能力）→ 不可发动
                        if (card.IsAbilityLimitReached(e.EffectId, e.MaxUsesPerTurn)) continue;
                        if (card.IsGameAbilityLimitReached(e.EffectId, e.MaxUsesPerGame)) continue;   // 【A57】每局游戏次数上限（不随回合/离场重置）

                        // 【K1】动态值 X 求值为 0（或表达式未登记）→ 该效果不算发动 → 不可发动
                        // 【2026-09 修复·JC055②】例外：`代价数量` 的 X 来自**本次激活已支付的代价**
                        // （逐卡分配标志、入堆叠前才产生）⇒ 候选侧**不能**按"X 不可用"拦，
                        // 否则该能力**永远不亮、永远不可发动**（用户实测："JC055 可以激活行动效果时 UI 没有高亮"）。
                        if (!Script.Manager.EffectEngine.IsCostPaidValue(e)
                            && Script.Manager.EffectEngine.Instance != null
                            && Script.Manager.EffectEngine.Instance.EvaluateDynamicValue(e, card, null, null, -1, out _, out _)
                               == Script.Manager.EffectEngine.DynamicValueState.Blocked) continue;

                        // 【P3·K21·JC041】来源前提（暗藏者限定 / 地区子类别）
                        if (!MeetsSourcePrereqs(card, e, AreaManagerRef, out _)) continue;

                        // 门禁 3：费用足够
                        if (e.Cost > mana) continue;

                        // 门禁 4：需选目标时，场上须有合法目标
                        if (e.Target != null && e.Target.NeedsSelection)
                        {
                            if (!HasLegalTarget(card, e, cm, pm)) continue;
                        }

                        // 【P3·K22】牺牲代价：没有可牺牲的角色 → 不可发动（与 EffectEngine 的"代价前置门禁"同解）
                        if (e.SacrificeCost != null && GetSacrificeCandidates(card, e).Count == 0) continue;
                // 【P3·K5】标志代价（JC044）：本方场上没有带指定标志的卡 → 该能力不可发动（与牺牲代价同款门禁）
                if (e.CounterCost != null && GetCounterCostCandidates(card, e).Count == 0) continue;
                        // 【P3·K15】需要"目标地区"的激活能力：没有可选地区 → 不可发动；
                        // 声明"不能原地移动"（FAQ）时**至少要存在另一个地区**（卡目标尚未选定，只能做必要条件判断）
                        if (e.NeedsAreaTarget)
                        {
                            int areaCount = GetAreaTargetCandidates(card, e).Count;
                            if (areaCount == 0) continue;
                            if (e.AreaTargetExcludesCardArea && areaCount < 2) continue;
                        }

                        // 【P3】门禁 5：激活类效果声明的结算条件（EffectCondition.Predicate）须满足
                        if (!ConditionAllowsActivation(e, card)) continue;

                        result.Add((card, e));
                    }
                }
                else if (card?.CouncilTemplate is CouncilCardData councilTmpl)
                {
                    // 秘社卡：遍历 ActivationOptions 各选项的 EffectDataList
                    foreach (var opt in councilTmpl.ActivationOptions)
                    {
                        if (opt?.EffectDataList == null) continue;
                        foreach (var e in opt.EffectDataList)
                        {
                            if (e.Timing is not EffectTiming.CouncilActivated) continue;
                            // 【K26】阶段排除（秘社激活同办）
                            if (PhaseGateRule.IsExcluded(e, tm)) continue;
                            // 【K23】次数上限（秘社激活同办）
                            if (card.IsAbilityLimitReached(e.EffectId, e.MaxUsesPerTurn)) continue;
                            if (card.IsGameAbilityLimitReached(e.EffectId, e.MaxUsesPerGame)) continue;   // 【A57】每局游戏次数上限（不随回合/离场重置）
                            if (e.Cost > mana) continue;
                            if (e.Target != null && e.Target.NeedsSelection)
                            {
                                if (!HasLegalTarget(card, e, cm, pm)) continue;
                            }
                            // 【P3】门禁 5：结算条件须满足
                            if (!ConditionAllowsActivation(e, card)) continue;
                            result.Add((card, e));
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>判定某张场上卡能否发动其激活能力（用于单卡高亮）。
        /// <param name="pm">本对局的 PlayerManager（多对局隔离 / 客户端投影）：需选目标的效果经它求值条件式限制，缺失会失真。</param>
        /// <param name="ignoreAtomicLock">【仅投影生成用】忽略"原子操作进行中"这一时序门禁：
        /// 服务端生成客户端视图（快照）时可能正处于原子段内，若照实返回 false，客户端会整块失去"可发动"高亮。
        /// 时序门禁由服务端在意图入口（ActivateAbility）强制，不依赖本查询。</param>
        /// <summary>
        /// 【2026-09·诊断】"此刻为什么不能发动"的**可读原因**（**仅供日志/UI 提示，不参与任何规则判定**）。
        ///
        /// 背景（用户实测）："JC042/JC044 偶尔出现无法双击唤醒效果，**卡有高亮，而且双击完全没有程序 LOG**"。
        /// 根因是单机双击链路 `Arena.OpenAttachmentActivateChoice` 里 `CanActivate` 不通过时**静默 return**
        /// —— 而高亮是**上一次刷新**（优先权变化/视觉脏标记）算好的；点击那一刻若门禁变为不满足
        /// （原子操作进行中 / 优先权转移 / 费用或次数不够 / 已横置 / 代价候选耗尽 / 阶段窗口已过…），
        /// 就表现为"高亮着却怎么点都没反应、也不打日志"。本方法按 `CanActivate` 的门禁顺序粗查一遍，
        /// 返回第一条不满足的说明（全部通过返回 `null`），供调用方**打日志 + 刷新高亮**（让 UI 与规则自洽）。
        /// 注：与 `CanActivate` 的细分门禁相比本方法更粗（只用于解释），**规则判定仍以 `CanActivate` 为准**。
        /// </summary>
        public static string DiagnoseActivateBlock(CardInstance card, TurnManager tm, PlayerManager pm, CardManager cm)
        {
            if (card == null) return "卡实例为空";
            if (card.IsCovert) return "暗藏/背面朝上，不可作为激活来源";
            if (Script.Manager.EffectEngine.Instance?.IsTextBlank(card) == true) return "印刷文本视作空白（失去效果）";
            var playerId = card.ControllerId;
            if (tm != null && tm.PriorityPlayerId != playerId)
                return $"当前没有优先权（优先权在 P{tm.PriorityPlayerId}，本卡控制者 P{playerId}）";
            if (tm != null && tm.IsAtomicLocked) return "原子操作进行中（结算/决策未闭合，规则层原子锁）";
            if (CanActivate(card, tm, pm, cm)) return null;
            return "该卡当前没有任何可发动的激活能力（费用/阶段窗口/每回合次数/发动代价/目标不满足）";
        }

        /// <summary>
        /// 【2026-10 用户口径·JC093】该激活能力是否**因次数上限用尽**而不可发动
        /// （`MaxUsesPerTurn`／`MaxUsesPerGame`；0＝不限次）——**候选/高亮/逐项置灰/直发判定共用同一份**。
        /// 用途：单机 `Arena.OpenAttachmentActivateChoice` 与联机 `ArenaOl.IsActivationEffectDisabled`
        /// 据此把用尽的能力**置灰**；"全部用尽"时双击**不开面板、无反馈**
        /// （用户口径："使用两次效果后双击不需要反馈"）。
        /// </summary>
        public static bool IsAbilityUsesExhausted(CardInstance card, EffectData e)
            => card != null && e != null
               && (card.IsAbilityLimitReached(e.EffectId, e.MaxUsesPerTurn)
                   || card.IsGameAbilityLimitReached(e.EffectId, e.MaxUsesPerGame));

        /// <summary>一组激活能力是否**全部**已因次数上限用尽（UI 据此静默忽略双击，不开面板）。</summary>
        public static bool AllAbilitiesUsesExhausted(CardInstance card, List<EffectData> abilities)
        {
            if (card == null || abilities == null || abilities.Count == 0) return false;
            foreach (var e in abilities)
                if (!IsAbilityUsesExhausted(card, e)) return false;
            return true;
        }

        public static bool CanActivate(CardInstance card, TurnManager tm, PlayerManager pm, CardManager cm,
            bool ignoreAtomicLock = false)
        {            if (card?.PlayerTemplate is not PlayerCardData tmpl)
            {
                // 秘社卡：PlayerTemplate==null，改用 CouncilTemplate 的 ActivationOptions 判定
                if (card?.CouncilTemplate is CouncilCardData councilTmpl)
                    return CanActivateCouncil(card, councilTmpl, tm, pm, cm, ignoreAtomicLock);
                return false;
            }
            // 通用门禁 G0（默认生效，所有激活卡适用）：暗藏/背面朝上的卡不可作为激活来源。
            // 规则：仅正面朝上可激活；暗藏者（IsCovert）隐藏信息，不可发动任何激活能力。
            // 修复 JC003 BUG：原仅遍历 AreaZone 未判 IsCovert，导致背面朝上亦高亮可激活。
            if (card.IsCovert) return false;
            // 【P3·K20】"印刷文本框视作空白"（JC101）→ 该卡**失去效果**：不可发动任何激活能力。
            if (Script.Manager.EffectEngine.Instance?.IsTextBlank(card) == true) return false;
            // 【方案C】生效区门禁收敛到内层 foreach：对每个激活效果用 IsEffectActiveInZone 做效果粒度判定
            // （默认：附属=AttachedZone，角色=AreaZone；显式声明 Scope 的效果按声明）。
            // 旧整卡判定（isAtt + CurrentZone 一刀切 return false）已移除，勿再在别处重复写区判断，
            // 避免"显式声明其他区（如墓地/手牌）的激活效果"被整卡误拦。
            // 控制权语义（核心需求 / D28）：激活能力由"当前控制者"发动，而非拥有者。
            // 夺控场景下 OwnerId 不变而 ControllerId 改变，若用 OwnerId 会导致：
            // ① 真正控制者因 OwnerId != 优先权被拒（自己的卡不能发动）；② 非控制者因 OwnerId == 优先权反能发动他人卡。
            var playerId = card.ControllerId;
            if (tm?.PriorityPlayerId != playerId) return false;
            // 规则层原子锁（核心需求 7.3）：原子操作（出牌/结算/触发排序/选目标窗口）执行期间
            // 冻结响应权，任何激活能力（含快速行动 JC003 类）均不可在原子期间发动，与 Pass/PlayCard 一致。
            if (tm != null && tm.IsAtomicLocked && !ignoreAtomicLock) return false;
            int mana = pm?.GetAvailableMana(playerId) ?? 0;

            // 【P3·K13】门禁同样覆盖**运行期授予的**激活能力（否则"授予的能力可发动性"与候选枚举会不一致）
            foreach (var e in GetAllActivateAbilities(card))
            {
                if (e.Timing is not (EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated)) continue;
                // 方案C：激活效果生效区判定（效果粒度）。默认：附属=AttachedZone，角色=AreaZone；
                // 显式声明 Scope 的效果按声明（如未来墓地/手牌激活）。不在此区则跳过该效果。
                // 静态化调用：客户端（无 EffectEngine 实例）也能复用同一生效区判定（行为与原先一致）。
                if (!EffectEngine.IsEffectActiveInZone(e, card)) continue;
                // 【K26】阶段排除：当前阶段被该效果排除 → 不可发动（与 `EffectEngine.ActivateAbility` 权威门禁同解）
                if (PhaseGateRule.IsExcluded(e, tm)) continue;
                // 【K23】次数上限：该能力本回合已用满 → 不可发动
                if (card.IsAbilityLimitReached(e.EffectId, e.MaxUsesPerTurn)) continue;
                if (card.IsGameAbilityLimitReached(e.EffectId, e.MaxUsesPerGame)) continue;   // 【A57】每局游戏次数上限（不随回合/离场重置）
                // 【K1】动态值 X 不可用（0 或求值失败）→ 不可发动
                // 【2026-09 修复·JC055②】`代价数量` 例外（X 在入堆叠前才由支付产生）→ 不拦（见上文同款说明）。
                if (!Script.Manager.EffectEngine.IsCostPaidValue(e)
                    && Script.Manager.EffectEngine.Instance != null
                    && Script.Manager.EffectEngine.Instance.EvaluateDynamicValue(e, card, null, null, -1, out _, out _)
                       == Script.Manager.EffectEngine.DynamicValueState.Blocked) continue;
                // 【P3·K21·JC041】来源前提（暗藏者限定 / 地区子类别）
                if (!MeetsSourcePrereqs(card, e, AreaManagerRef, out _)) continue;
                if (e.Cost > mana) continue;
                // 窗口门禁（D9）：行动类激活仅限【自己行动步骤 + 堆叠为空 + 有行动权】
                // （先手→ActionFirst，后手→ActionSecond，复用 CanEnterBasicActionMask）。
                // 快速行动（含"快速"）仅需优先权（已由方法首行 PriorityPlayerId 校验）。
                if (e.ActivateWindow.Contains("行动"))
                {
                    if (tm == null || !tm.CanEnterBasicActionMask(playerId)) continue;
                }
                // 发动条件要求"横置本角色"作为代价时，本角色须处于未横置态方可发动；已横置则跳过
                if (!string.IsNullOrEmpty(e.ActivateCondition) &&
                    e.ActivateCondition.Contains("横置本角色") &&
                    card.IsTapped) continue;
                // 【P3·K22】牺牲代价：没有可牺牲的角色 → 不可发动（与 EffectEngine 的"代价前置门禁"同解，UI 据此置灰）
                if (e.SacrificeCost != null && GetSacrificeCandidates(card, e).Count == 0) continue;
                // 【P3·K5】标志代价（JC044）：本方场上没有带指定标志的卡 → 该能力不可发动（与牺牲代价同款门禁）
                if (e.CounterCost != null && GetCounterCostCandidates(card, e).Count == 0) continue;
                // 【P3·K15】需要"目标地区"的激活能力：没有可选地区 → 不可发动；
                // 声明"不能原地移动"时**至少要存在另一个地区**（卡目标尚未选定 → 必要条件判断）
                if (e.NeedsAreaTarget)
                {
                    int areaCount = GetAreaTargetCandidates(card, e).Count;
                    if (areaCount == 0) continue;
                    if (e.AreaTargetExcludesCardArea && areaCount < 2) continue;
                }
                // 【P3】激活类效果声明的**结算条件**（`EffectCondition.Predicate`）同样是发动前提：
                // 不满足 → 不可发动（与 `EffectEngine.ActivateAbility` 的付费前门禁同源，UI 据此置灰）。
                if (!ConditionAllowsActivation(e, card)) continue;
                // 【2026-09·A57 同款防呆】"无目标能力"（`Kind == None`）不参与"是否有合法目标"门禁：
                // `EffectData.Target` 默认值是非 null 的 TargetSpec，若忘了清空会被误判成"需要选目标"⇒永不可发动。
                if (e.Target != null && e.Target.Kind != TargetKind.None && e.Target.NeedsSelection
                    && !HasLegalTarget(card, e, cm, pm)) continue;
                return true;
            }
            return false;
        }

        /// <summary>【P3·K15】某激活能力"目标地区"的**合法候选**（未声明 NeedsAreaTarget → 空表）。
        /// 未指定卡目标时（＝候选枚举阶段）只按"地区存在"给候选；选定卡目标后再按
        /// AreaTargetExcludesCardArea（JC117"不能原地移动"）过滤。</summary>
        public static List<string> GetAreaTargetCandidates(CardInstance source, EffectData ability,
            CardInstance cardTarget = null)
            => Script.Manager.EffectEngine.Instance?.GetLegalAreaTargets(source, ability, cardTarget)
               ?? new List<string>();

        /// <summary>【P3·K22】某激活能力"牺牲一个角色"代价的**合法候选**（无此代价 → 空表）。
        /// 单一实现委托 `EffectEngine.GetSacrificeCandidates`（与权威侧复检同源，不会分叉）。</summary>
        /// <summary>【P3·K5】标志代价的候选（供 View 弹选择窗）：单一实现委托
        /// `EffectEngine.GetCounterCostCandidates`（与权威侧复检同源，不会分叉）。</summary>
        public static List<CardInstance> GetCounterCostCandidates(CardInstance source, EffectData ability)
        {
            if (ability?.CounterCost == null || source == null) return new List<CardInstance>();
            return Script.Manager.EffectEngine.Instance?.GetCounterCostCandidates(source, ability.CounterCost)
                   ?? new List<CardInstance>();
        }

        public static List<CardInstance> GetSacrificeCandidates(CardInstance source, EffectData ability)
        {
            if (ability?.SacrificeCost == null || source == null) return new List<CardInstance>();
            return Script.Manager.EffectEngine.Instance?.GetSacrificeCandidates(source, ability.SacrificeCost)
                ?? new List<CardInstance>();
        }

        /// <summary>
        /// 【P3·K21·JC041】**发动来源前提**（候选侧与权威侧**共用同一实现**，避免两处口径分叉）：
        ///  · `RequireSourceCovert`：来源卡必须处于**暗藏者**状态（用户口径 A29 追问答复："这个效果只有作为暗藏者时可以激活"）；
        ///  · `RequireSourceAreaSubCategory`：来源卡**所在地区**须具有该**地区子类别**（本卡＝`水域`）。
        /// 无法判定（AreaManager 未注入/来源不在地区）→ 不满足（fail-closed：宁可不可发动，也不放行越权发动）。
        /// </summary>
        public static bool MeetsSourcePrereqs(CardInstance source, EffectData ability,
            AreaManager areaManager, out string reason)
        {
            reason = null;
            if (source == null || ability == null) { reason = "来源或效果为空"; return false; }
            if (ability.RequireSourceCovert && !source.IsCovert)
            {
                reason = "该能力只能作为**暗藏者**时发动";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(ability.RequireSourceAreaSubCategory))
            {
                string area = source.ZoneDetailId;
                if (string.IsNullOrEmpty(area)) { reason = "来源卡不在任何地区上"; return false; }
                if (areaManager == null) { reason = "AreaManager 未注入，无法判定地区子类别"; return false; }
                if (!areaManager.AreaHasSubCategory(area, ability.RequireSourceAreaSubCategory))
                {
                    reason = $"该能力只能在『{ability.RequireSourceAreaSubCategory}』地区发动（当前地区 {area}）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>本次对局的 AreaManager（候选侧统一从 `EffectEngine.Instance` 取，与权威侧同源）。</summary>
        private static AreaManager AreaManagerRef
            => Script.Manager.EffectEngine.Instance?.TurnManagerRef?.AreaManagerRef;
        /// <summary>【P3·K13】某卡当前可发动的激活能力＝**卡面固有**（`EffectDataList`）∪ **运行期授予**
        /// （JC019 镇魔枪授予宿主的能力）。View 的"多激活效果选择窗口"与候选枚举共用本方法
        /// ——用户 2026-09 口径：宿主原本就有激活效果时，**在既有选择窗口里额外增列 1 项**。</summary>
        public static List<EffectData> GetAllActivateAbilities(CardInstance card)
        {
            var result = new List<EffectData>();
            if (card == null) return result;
            if (card.PlayerTemplate is PlayerCardData tmpl && tmpl.EffectDataList != null)
                foreach (var e in tmpl.EffectDataList)
                    if (e != null && e.Timing is EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated)
                        result.Add(e);
            var granted = Script.Manager.EffectEngine.Instance?.GetGrantedAbilities(card);
            if (granted != null) result.AddRange(granted);
            return result;
        }

        /// <summary>
        /// 【2026-09 修复·JC019/JC024】**"双击请求激活"的来源判定**（View 与测试共用的单一实现）：
        ///  · 附属（`IsAttached`）：JC012 类；
        ///  · **争夺区正面角色**（非暗藏）：其能力可能来自①运行期授予（JC019 给宿主）②无目标自指激活
        ///    （JC024"快速行动2：将自身潜伏"）——这两类都无法用"拖到目标上"表达，只能靠双击进入；
        ///  · 手牌中的"手牌行动"卡（声明 `Scope=HandZone` 的激活能力，如 JC110）。
        /// 注：本判定只说"这一类来源允许双击进入激活流程"，**是否真的可发动**仍由 `CanActivate` 把关。
        /// </summary>
        public static bool IsDoubleClickActivationSource(CardInstance card)
        {
            if (card == null) return false;
            if (card.IsAttached) return true;
            if (card.CurrentZone == CardZone.AreaZone) return !card.IsCovert;
            if (card.CurrentZone == CardZone.Hand && card.PlayerTemplate is PlayerCardData t)
            {
                if (t.EffectDataList == null) return false;
                foreach (var e in t.EffectDataList)
                    if (e != null && e.Scope == ZoneScope.HandZone
                        && e.Timing is EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated) return true;
                return false;
            }
            return false;
        }

        /// <summary>
        /// 【2026-09】"**首条**激活能力"的统一取法：`GetAllActivateAbilities`（模板 ∪ **运行期授予**）的第一条。
        /// 单机 `Arena.RefreshActivateHighlights`/`CardDragController.GetActivatingAbility`、
        /// 联机 `ArenaOl.GetFirstOnlineActivateAbility` 都必须用本方法——此前各自只读模板 `EffectDataList`，
        /// 于是 JC019 授予宿主的激活能力在**拖拽路径**上取不到（箭头里 ability=null ⇒ 松手"无合法目标"取消）。
        /// </summary>
        public static EffectData GetFirstActivateAbility(CardInstance card)
        {
            var all = GetAllActivateAbilities(card);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// 【2026-09·用户实测】"**能否用拖拽表达**这次激活"：恰好 **1 条**能力 ＋ 需要选**场上卡**目标。
        ///  · 多条能力（JC024 自带 ＋ JC019 授予 = 2 条）⇒ 必须走**双击效果菜单**；
        ///  · 无目标/自指能力（JC024"快速行动2：将自身潜伏"）⇒ 必须**直接发动**（双击）；
        ///  · 玩家目标（JC111）/地区目标（JC117）⇒ 拖到卡上无法表达；
        /// 以上一律不进入拖拽（否则 UI 会吞掉左键 ⇒ 双击被拖拽覆盖，效果根本用不了）。
        /// `ability` 输出＝可用于拖拽的那条能力（不可拖拽时为 null）。
        /// </summary>
        public static bool CanDragActivate(CardInstance card, out EffectData ability)
        {
            ability = null;
            var all = GetAllActivateAbilities(card);
            if (all.Count != 1) return false;
            var e = all[0];
            // 【2026-10 用户口径·JC108】声明"双击激活"的能力**不进拖拽**（否则左键被全局拖拽吞掉，
            // `CardUI` 收不到双击信号，双击激活链路根本不可达；见 `EffectData.ActivateByDoubleClick`）。
            if (e?.ActivateByDoubleClick == true) return false;
            var spec = e?.Target;
            if (spec == null || !spec.NeedsSelection) return false;      // 无目标 ⇒ 双击直接发动
            if (spec.Kind == TargetKind.None) return false;
            if (PlayerTargetPool.IsPlayerSpec(spec)) return false;       // 玩家目标不走卡拖拽
            if (spec.Kind == TargetKind.Area) return false;              // 地区目标走选地区窗
            ability = e;
            return true;
        }

        /// <summary>秘社激活门禁：遍历 ActivationOptions 各选项的 EffectDataList，任一可发动即返回 true。</summary>
        private static bool CanActivateCouncil(CardInstance card, CouncilCardData councilTmpl, TurnManager tm,
            PlayerManager pm, CardManager cm, bool ignoreAtomicLock = false)
        {
            // 通用门禁 G0（默认生效，所有激活卡适用）：暗藏/背面朝上的卡不可作为激活来源。
            if (card.IsCovert) return false;
            var playerId = card.ControllerId;
            if (tm?.PriorityPlayerId != playerId) return false;
            if (tm != null && tm.IsAtomicLocked && !ignoreAtomicLock) return false;
            int mana = pm?.GetAvailableMana(playerId) ?? 0;

            foreach (var opt in councilTmpl.ActivationOptions)
            {
                if (opt?.EffectDataList == null) continue;
                foreach (var e in opt.EffectDataList)
                {
                    if (e.Timing is not EffectTiming.CouncilActivated) continue;
                    // 【K26】阶段排除（秘社激活同办）
                    if (PhaseGateRule.IsExcluded(e, tm)) continue;
                    // 【K23】次数上限（秘社激活同办）
                    if (card.IsAbilityLimitReached(e.EffectId, e.MaxUsesPerTurn)) continue;
                    if (card.IsGameAbilityLimitReached(e.EffectId, e.MaxUsesPerGame)) continue;   // 【A57】每局游戏次数上限（不随回合/离场重置）
                    if (e.Cost > mana) continue;
                    if (e.ActivateWindow.Contains("行动"))
                    {
                        if (tm == null || !tm.CanEnterBasicActionMask(playerId)) continue;
                    }
                    if (!string.IsNullOrEmpty(e.ActivateCondition) &&
                        e.ActivateCondition.Contains("横置本角色") &&
                        card.IsTapped) continue;
                    // 【A57·防呆】"无目标能力"必须显式 `Target = null`；但 `EffectData.Target` 的**默认值是非 null 的
                    // TargetSpec**，忘了清空时这里会误判成"需要选目标" ⇒ 该秘社能力**永远不可激活**（做 MSJC 批次时踩到）。
                    // 故补一条：`Kind == None` 的目标声明**不可能有可选目标**，不参与本门禁（fail-open，且不会放过任何真需求）。
                    if (e.Target != null && e.Target.Kind != TargetKind.None && e.Target.NeedsSelection
                        && !HasLegalTarget(card, e, cm, pm)) continue;
                    // 【秘社批次】"牌作代价"门禁（与 `EffectEngine.ActivateAbility` 的权威门禁同源实现）：
                    // 候选不足 N 张（强制）→ 不可发动，UI 据此置灰。
                    if (e.CardCost != null)
                    {
                        int need = e.CardCost.Count > 0 ? e.CardCost.Count : 1;
                        var cand = Script.Manager.EffectEngine.Instance
                            ?.GetCardCostCandidates(card, e.CardCost)
                            ?? new System.Collections.Generic.List<Script.Game.Entities.CardInstance>();
                        if (cand.Count < need && e.CardCost.Mandatory) continue;
                    }
                    // 【秘社批次】"从手牌封印一张符合条件的牌"（MSJC010①）门禁：手牌中没有合格候选 → 不可发动。
                    if (!Script.Manager.EffectEngine.HasSealHandCandidate(e, card, tm)) continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>判定激活能力 source 以 target 为目标是否合法（拖拽落点校验复用）。</summary>
        public static bool IsLegalActivateTarget(CardInstance source, EffectData ability, CardInstance target, CardManager cm,
            PlayerManager pm = null)
        {
            if (source == null || ability == null || target == null) return false;
            // 【2026-09 修复·A62（与 JC004 同类）】声明"目标含自身"（`Target.CanTargetSelf`）的激活能力，
            // **来源卡本身也可以是合法目标**；此前无条件 `source == target → false` ⇒ 当本地区唯一合法对象
            // 就是自己时（如 JC003 帷幕护卫、JC019 授予宿主的能力），该能力恒判"无合法目标" → 高亮/候选/
            // 双击都被拦（表现为"能力永远不可发动"）。候选枚举侧（`TurnManager.GetTargetUidsForSpec`）
            // 一直是按 `CanTargetSelf` 放行的，此处必须与之一致。
            if (source == target && ability.Target?.CanTargetSelf != true) return false;
            if (ability.Target == null || !ability.Target.NeedsSelection) return false;
            // 本地区限定（TargetSpec.IsLocalZone）：目标须与来源卡同地区。统一基准=source.ZoneDetailId，
            // 对三种情况均成立：角色激活=角色所在地区；结附地区的附属=结附的地区；
            // 结附角色的附属=宿主角色所在地区（结附时 MoveToAttachedZone 已把 ZoneDetailId 记为宿主地区）。
            // 此处前置统一拦截，使上方 TargetRules 分支与下方 allowedB 分支都受本地区约束。
            if (ability.Target.IsLocalZone && target.ZoneDetailId != source.ZoneDetailId) return false;
            var tmpl = source.PlayerTemplate as PlayerCardData;
            if (tmpl == null) return false;

            // 方案 A：若卡牌配置了 TargetRules，先用激活目标大类（ability.Target.Kind）筛掉明显不符者，
            // 再对 Kind 与激活目标一致的 rule 走 MatchesRule 精筛（保证"打出/激活同源"约束仍生效）。
            // 修复：原逻辑用 rule.Kind（打出目标规则）直接与激活目标比较，导致激活目标≠打出目标种类
            // （如 JC012 打出目标为"结附秘社"、激活②目标为"场上角色"）时永远不匹配、显示无合法目标。
            if (tmpl.TargetRules != null && tmpl.TargetRules.Count > 0)
            {
                int sourceController = source.ControllerId;
                var actKind = ability.Target != null ? ability.Target.Kind : TargetKind.None;
                // 1) 激活目标大类粗筛：目标种类必须与 ability.Target.Kind 一致（无 Target 时回退到 GetAllowedTargets）
                var allowed = tmpl.GetAllowedTargets();
                var kind = CardManager.MapInstanceToTargetKind(target);
                bool kindOk = actKind != TargetKind.None
                    ? (kind == actKind)
                    : (allowed.Count > 0 && allowed.Contains(kind));
                if (!kindOk) return false;
                // 2) 暗藏者回退（激活 Target.Filter 声明"或暗藏者"时放宽）
                if (kind == TargetKind.Covert && ability.Target != null &&
                    !string.IsNullOrEmpty(ability.Target.Filter) && ability.Target.Filter.Contains("暗藏者"))
                    return true;
                // 3) 对 Kind 与激活目标一致的 rule 精筛；若无任何 rule 的 Kind 与激活目标一致，则大类已过即合法
                bool hasMatchingRule = false;
                foreach (var rule in tmpl.TargetRules)
                {
                    if (rule == null || rule.Kind != actKind) continue;
                    hasMatchingRule = true;
                    if (CardManager.MatchesRule(rule, target, sourceController, pm)) return true;
                }
                return !hasMatchingRule; // 无匹配 rule 时退化为纯大类粗筛（JC012 激活②走此路径）
            }

            // 未配置 TargetRules 时：直接以激活能力自身声明的目标规则粗筛，不再依赖 GetAllowedTargets()。
            // GetAllowedTargets() 是"打出"语义（已把激活/触发能力目标排除），用于激活目标会判空（JC003 角色卡）。
            var actTargetB = ability.Target;
            if (actTargetB == null || actTargetB.Kind == TargetKind.None) return false;
            var kindB = CardManager.MapInstanceToTargetKind(target);
            // 大类匹配：目标种类 == 声明种类；或 IncludeCovert 放宽暗藏者（暗藏者被映射为 TargetKind.Covert，
            // 而非 Character，故声明 Kind=Character 的 JC003 需靠 IncludeCovert 覆盖暗藏者，符合"无视地区"规则）。
            bool kindOkB = (kindB == actTargetB.Kind) ||
                (actTargetB.IncludeCovert && kindB == TargetKind.Covert);
            if (!kindOkB) return false;
            // 【P3·K21】**精筛条件必须同样生效**（修缺陷）：此前本分支只做大类粗筛 →
            // 激活能力自己声明的 `Target.Restrictions` 被完全忽略（如 JC069「目标须带锁定标志」会对任意角色放行）。
            // 与打出目标、结算复检共用 `TargetValidity.CheckRestrictions`（单一真相源：能选中的目标与结算时合法的目标一致）。
            return TargetValidity.CheckRestrictions(actTargetB.Restrictions, actTargetB.Param, target,
                source.ControllerId, pm);
        }

        public static bool HasLegalTarget(CardInstance source, EffectData ability, CardManager cm,
            PlayerManager pm = null)
        {
            if (cm == null) return false;
            // 本地区限定：仅枚举来源卡所在地区的卡（基准=source.ZoneDetailId，含暗藏者与角色）；
            // 否则 GetAreaCards 会遍历所有地区，本地区效果会把其他地区的目标也算合法。
            // pm（多对局隔离）：由调用方传入本局 PlayerManager；null 时链路退回进程级静态兜底。
            pm ??= cm.PlayerManagerRef;
            // 【K24】玩家目标：候选＝"任意/本方/对手"限制下合法的玩家（不扫场上的卡）。
            if (PlayerTargetPool.IsPlayerSpec(ability.Target))
            {
                if (ability.Target.Kind != TargetKind.Player && ability.Target.KindAlt != TargetKind.Player) return false;
                return PlayerTargetPool.Collect(ability.Target, source.ControllerId, pm).Count > 0;
            }
            // 【K14】非争夺区目标（墓地/手牌/资产/附属/秘社/封印）：候选来自对应区域而非争夺区（`TargetZonePool` 单一实现）。
            if (TargetZonePool.IsZonePileSpec(ability.Target))
            {
                var pile = TargetZonePool.Collect(ability.Target, source.ControllerId,
                    cm.ZoneManagerRef, pm, source.ZoneDetailId);
                return pile.Count > 0;
            }
            // 【2026-09·JC027】形态角色经 `ZoneManager.MoveFormZone` 迁入争夺区，本身就在 `_area` 里
            // ⇒ 这里用常规的争夺区成员集合即可（不需要候选侧补丁）。
            var all = ability.Target != null && ability.Target.IsLocalZone
                ? cm.GetAreaInstancesBySlot(source.ZoneDetailId)
                : cm.GetAreaCards();
            if (all == null) return false;
            foreach (var c in all)
            {
                // 【2026-09·A62】"目标含自身"的声明不再跳过来源卡（与候选枚举同口径）
                if (c == source && ability.Target?.CanTargetSelf != true) continue;
                if (IsLegalActivateTarget(source, ability, c, cm, pm)) return true;
            }
            return false;
        }

        /// <summary>
        /// 【P3】激活类效果的**结算条件**（`EffectCondition.Predicate`）是否允许发动。
        /// 语义与 `EffectEngine.ActivateAbility` 的"付费前门禁"完全一致（同一谓词、同一判定入口）：
        /// 条件为空/`Always` → 放行；否则按谓词求值（如 JC053 的 `EnemyPowerMarkRemovedThisTurn`）。
        /// UI 的高亮/候选据此置灰，避免"点了却因条件不满足被拒"。
        /// </summary>
        /// <summary>激活类效果声明的**结算条件**（`EffectCondition.Predicate`，非 `Always`）是否满足；
        /// 不满足 ⇒ 该能力不是合法发动项。**同时供候选侧门禁与 UI 置灰判定使用**（单一实现，避免两套口径）。
        /// 【2026-09 修复·JC053】UI 的"多能力选择窗"此前只判"需横置已横置 / 需目标无候选"，
        /// 漏了本前置条件 ⇒ 用户实测"快速行动①抽牌效果**不满足前置**时没有置灰"。</summary>
        public static bool ConditionAllowsActivation(EffectData e, CardInstance card)
        {
            if (e?.Condition == null || string.IsNullOrEmpty(e.Condition.Predicate)) return true;
            if (e.Condition.Predicate == "Always") return true;
            // 【2026-09 修复·**回归**】纯客户端**没有 `EffectEngine.Instance`**（联机客户端的规则判定一律来自
            // 服务端投影，见 A78）⇒ 这里必须判空。原先直接 `.Instance.EvaluateCondition(...)`：本方法被
            // UI 置灰判定调用（`ArenaOl.IsActivationEffectDisabled`）⇒ 客户端双击时抛 NullReferenceException，
            // **整个双击处理器中断**，表现为"卡有高亮、双击毫无反应、控制台只有 .err 里一条 NRE"
            // （用户实测 JC053；日志已定位到本行）。
            // 客户端不能本地求值 ⇒ 返回 true（**不本地置灰**），由"服务端投影清单里有没有这条能力"决定置灰。
            var ee = EffectEngine.Instance;
            if (ee == null) return true;
            return ee.EvaluateCondition(e.Condition.Predicate, card, null, null, -1, null);
        }
    }
}