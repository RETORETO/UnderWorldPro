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
            var field = cm.GetAreaCards();
            if (field == null) return result;

            int mana = pm.GetAvailableMana(playerId);

            foreach (var card in field)
            {
                if (card?.ControllerId != playerId) continue;
                if (card?.PlayerTemplate is PlayerCardData tmpl)
                {
                    foreach (var e in tmpl.EffectDataList)
                    {
                        if (e.Timing is not (EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated)) continue;

                        // 门禁 3：费用足够
                        if (e.Cost > mana) continue;

                        // 门禁 4：需选目标时，场上须有合法目标
                        if (e.Target != null && e.Target.NeedsSelection)
                        {
                            if (!HasLegalTarget(card, e, cm)) continue;
                        }

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
                            if (e.Cost > mana) continue;
                            if (e.Target != null && e.Target.NeedsSelection)
                            {
                                if (!HasLegalTarget(card, e, cm)) continue;
                            }
                            result.Add((card, e));
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>判定某张场上卡能否发动其激活能力（用于单卡高亮）。</summary>
        public static bool CanActivate(CardInstance card, TurnManager tm, PlayerManager pm, CardManager cm)
        {
            if (card?.PlayerTemplate is not PlayerCardData tmpl)
            {
                // 秘社卡：PlayerTemplate==null，改用 CouncilTemplate 的 ActivationOptions 判定
                if (card?.CouncilTemplate is CouncilCardData councilTmpl)
                    return CanActivateCouncil(card, councilTmpl, tm, pm, cm);
                return false;
            }
            // 通用门禁 G0（默认生效，所有激活卡适用）：暗藏/背面朝上的卡不可作为激活来源。
            // 规则：仅正面朝上可激活；暗藏者（IsCovert）隐藏信息，不可发动任何激活能力。
            // 修复 JC003 BUG：原仅遍历 AreaZone 未判 IsCovert，导致背面朝上亦高亮可激活。
            if (card.IsCovert) return false;
            // 控制权语义（核心需求 / D28）：激活能力由"当前控制者"发动，而非拥有者。
            // 夺控场景下 OwnerId 不变而 ControllerId 改变，若用 OwnerId 会导致：
            // ① 真正控制者因 OwnerId != 优先权被拒（自己的卡不能发动）；② 非控制者因 OwnerId == 优先权反能发动他人卡。
            var playerId = card.ControllerId;
            if (tm?.PriorityPlayerId != playerId) return false;
            // 规则层原子锁（核心需求 7.3）：原子操作（出牌/结算/触发排序/选目标窗口）执行期间
            // 冻结响应权，任何激活能力（含快速行动 JC003 类）均不可在原子期间发动，与 Pass/PlayCard 一致。
            if (tm != null && tm.IsAtomicLocked) return false;
            int mana = pm?.GetAvailableMana(playerId) ?? 0;

            foreach (var e in tmpl.EffectDataList)
            {
                if (e.Timing is not (EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated)) continue;
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
                if (e.Target != null && e.Target.NeedsSelection && !HasLegalTarget(card, e, cm)) continue;
                return true;
            }
            return false;
        }

        /// <summary>秘社激活门禁：遍历 ActivationOptions 各选项的 EffectDataList，任一可发动即返回 true。</summary>
        private static bool CanActivateCouncil(CardInstance card, CouncilCardData councilTmpl, TurnManager tm, PlayerManager pm, CardManager cm)
        {
            // 通用门禁 G0（默认生效，所有激活卡适用）：暗藏/背面朝上的卡不可作为激活来源。
            if (card.IsCovert) return false;
            var playerId = card.ControllerId;
            if (tm?.PriorityPlayerId != playerId) return false;
            if (tm != null && tm.IsAtomicLocked) return false;
            int mana = pm?.GetAvailableMana(playerId) ?? 0;

            foreach (var opt in councilTmpl.ActivationOptions)
            {
                if (opt?.EffectDataList == null) continue;
                foreach (var e in opt.EffectDataList)
                {
                    if (e.Timing is not EffectTiming.CouncilActivated) continue;
                    if (e.Cost > mana) continue;
                    if (e.ActivateWindow.Contains("行动"))
                    {
                        if (tm == null || !tm.CanEnterBasicActionMask(playerId)) continue;
                    }
                    if (!string.IsNullOrEmpty(e.ActivateCondition) &&
                        e.ActivateCondition.Contains("横置本角色") &&
                        card.IsTapped) continue;
                    if (e.Target != null && e.Target.NeedsSelection && !HasLegalTarget(card, e, cm)) continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>判定激活能力 source 以 target 为目标是否合法（拖拽落点校验复用）。</summary>
        public static bool IsLegalActivateTarget(CardInstance source, EffectData ability, CardInstance target, CardManager cm)
        {
            if (source == null || ability == null || target == null || source == target) return false;
            if (ability.Target == null || !ability.Target.NeedsSelection) return false;
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
                    if (CardManager.MatchesRule(rule, target, sourceController)) return true;
                }
                return !hasMatchingRule; // 无匹配 rule 时退化为纯大类粗筛（JC012 激活②走此路径）
            }

            // 未配置 TargetRules 时：按大类粗筛（GetAllowedTargets 聚合 EffectDataList 的 Target.Kind）+ 暗藏者 Filter 回退。
            // 与上方 TargetRules 分支同为受支持的正式路径，二者按卡牌配置择一，无优先/新旧之分。
            var allowedB = tmpl.GetAllowedTargets();
            if (allowedB.Count == 0) return false;
            var kindB = CardManager.MapInstanceToTargetKind(target);
            if (allowedB.Contains(kindB)) return true;
            // 回退：激活效果的 Target.Filter 显式声明"或暗藏者"时，暗藏者（TargetKind.Covert）亦合法
            if (ability.Target != null &&
                !string.IsNullOrEmpty(ability.Target.Filter) &&
                ability.Target.Filter.Contains("暗藏者") &&
                kindB == TargetKind.Covert)
            {
                return true;
            }
            return false;
        }

        public static bool HasLegalTarget(CardInstance source, EffectData ability, CardManager cm)
        {
            if (cm == null) return false;
            var all = cm.GetAreaCards();
            if (all == null) return false;
            foreach (var c in all)
            {
                if (c == source) continue;
                if (IsLegalActivateTarget(source, ability, c, cm)) return true;
            }
            return false;
        }
    }
}
