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
                        if (Script.Manager.EffectEngine.Instance != null
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
        public static bool CanActivate(CardInstance card, TurnManager tm, PlayerManager pm, CardManager cm,
            bool ignoreAtomicLock = false)
        {
            if (card?.PlayerTemplate is not PlayerCardData tmpl)
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
                if (Script.Manager.EffectEngine.Instance != null
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
                if (e.Target != null && e.Target.NeedsSelection && !HasLegalTarget(card, e, cm, pm)) continue;
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
                    return true;
                }
            }
            return false;
        }

        /// <summary>判定激活能力 source 以 target 为目标是否合法（拖拽落点校验复用）。</summary>
        public static bool IsLegalActivateTarget(CardInstance source, EffectData ability, CardInstance target, CardManager cm,
            PlayerManager pm = null)
        {
            if (source == null || ability == null || target == null || source == target) return false;
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
            var all = ability.Target != null && ability.Target.IsLocalZone
                ? cm.GetAreaInstancesBySlot(source.ZoneDetailId)
                : cm.GetAreaCards();
            if (all == null) return false;
            foreach (var c in all)
            {
                if (c == source) continue;
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
        private static bool ConditionAllowsActivation(EffectData e, CardInstance card)
        {
            if (e?.Condition == null || string.IsNullOrEmpty(e.Condition.Predicate)) return true;
            if (e.Condition.Predicate == "Always") return true;
            return EffectEngine.Instance.EvaluateCondition(e.Condition.Predicate, card, null, null, -1, null);
        }
    }
}