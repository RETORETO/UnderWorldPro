using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using System;

namespace Script.Manager
{
    /// <summary>
    /// 地区能力统计的单一真相源（纯计算，无 Godot 节点依赖）。
    /// 规则：某玩家在本地区（AreaZone）的【未横置】角色/附属/暗藏者对应能力分别求和。
    ///   - 横置(IsTapped)的卡不提供任何能力（含先手加成），但仍可被战斗对抗分配伤害。
    ///   - 暗藏者(IsCovert)：调查=0、战斗=0、势力=1（+先手势力加成）；被横置同样归零。
    ///   - 先手加成：若当前先手玩家是该卡操控者(ControllerId)，加算对应先手能力。
    ///   - 仅角色(Character) / 附属(Attachment) / 暗藏者 参与统计（暗藏者即使模板类别不含这两类也参与）。
    ///
    /// 该规则同时被 CombatManager（对抗结算）与 AreaSlot（UI 常驻标签显示）复用，
    /// 避免"显示用统计"与"结算用统计"各写一份导致规则漂移。
    /// </summary>
    public static class AreaStatsCalculator
    {
        /// <summary>单卡在某对抗类型下的有效能力值（已含横置排除外的先手加成与暗藏者规则）。</summary>
        public static int ValueOf(CardInstance card, CombatManager.CombatType type, PlayerManager playerManager)
        {
            if (card == null) return 0;
            bool isFirst = playerManager != null && playerManager.IsFirstPlayer(card.ControllerId);

            if (card.IsCovert)
            {
                // 暗藏者：调查=0、战斗=0、势力=1（+先手势力）
                if (type == CombatManager.CombatType.Investigation) return 0;
                if (type == CombatManager.CombatType.Strength) return 0;
                var t = card.PlayerTemplate;
                int firstPower = (t != null && isFirst) ? t.FirstStrikePower : 0;
                return 1 + firstPower;
            }

            var tmpl = card.PlayerTemplate;
            if (tmpl == null) return 0;

            return type switch
            {
                // 调查能力加成来源（二者不同属性，互不影响）：
                //  - 基础调查值(card.CurrentInvestigation) 已含黑眼=调查（由 CardInstance.Grant 同步，
                //    且 ResetToBaseValues 重算时含 GetAttribute("调查")，不在此重复叠加）；
                //  - 白眼=先手调查：仅当结算者为先手玩家时叠加（每白眼+1，卡文"白眼白眼"叠加）。
                CombatManager.CombatType.Investigation => card.CurrentInvestigation
                    + (isFirst ? card.GetAttribute("先手调查") : 0),
                // 战斗结算读实例当前战力值（含 JC060 光环授予的"战斗"属性 +1），而非仅模板 GetTotalStrength
                // 【P1】白刀=先手战斗：与"白眼=先手调查"同构——仅先手方叠加（白刀不折进 CurrentStrength，避免非先手时误加）。
                CombatManager.CombatType.Strength => card.CurrentStrength
                    + (isFirst ? card.GetAttribute("先手战斗") : 0),
                // 【P1】势力改用实例值：原实现只读模板 GetTotalPower(isFirst)，导致"黑星=势力"（实例授予 CurrentPower）
                // 与"白星=先手势力"（实时授予属性）完全不参与对抗结算。改读实例后与调查/战力口径统一。
                // 【2026-09·JC051】卡面（`CardUI`）按**本行同一口径**补算先手势力，保证"卡面看到的 = 对抗用的"。
                CombatManager.CombatType.Power => card.CurrentPower
                    + (isFirst ? card.GetAttribute("先手势力") : 0),
                _ => 0
            };
        }

        /// <summary>判断卡牌是否为参与地区能力统计的身份（角色/附属/暗藏者）。</summary>
        public static bool IsStatParticipant(CardInstance card)
        {
            if (card == null) return false;
            // 暗藏者即使模板类别不含角色/附属，也参与（按规则暗藏者提供势力）
            if (card.IsCovert) return true;
            var tmpl = card.PlayerTemplate;
            if (tmpl == null && card.EffectiveCategoryFlag == CardCategory.None) return false;
            // 【P3·K4】用**有效类别**（含实例级形态覆盖）：JC027"成为一个地精角色"后按角色参与统计
            var f = card.EffectiveCategoryFlag;
            // 角色(Character) 或 附属(Attachment)
            return (f & CardCategory.Character) != 0 || (f & CardCategory.Attachment) != 0;
        }

        /// <summary>【P3·K21】该卡本次是否被"地区级静止规则"排除出统计（JC023 范·弥尔顿
        /// 「所有本地区的**敌方**暗藏者不能参与势力对抗」）。
        /// 只影响**暗藏者**：正面角色/附属不受影响；"敌方"＝非静止规则来源卡的控制者（不对称，本方暗藏者照常参与）。
        /// 判定入口＝`EffectEngine.ExcludesCovertPower`（按卡**当前**所在地实时匹配地区）。
        /// 因暗藏者的调查/战斗本为 0，实际效果＝其势力贡献（1+先手势力）归零。</summary>
        private static bool IsExcludedByAreaStaticRule(CardInstance card, string areaUid)
            => card.IsCovert
            && EffectEngine.Instance?.ExcludesCovertPower(areaUid, card.ControllerId) == true;

        /// <summary>
        /// 计算某玩家在本地区（指定 slotUid）的三项能力之和（调查/战斗/势力），含先手加成与暗藏者规则。
        /// 返回 (inv, bat, pow)。横置卡不计入；非参与身份（如地区卡、资产卡、议会卡）不计入。
        /// </summary>
        public static (int inv, int bat, int pow) GetAreaStats(
            ZoneManager zoneManager, PlayerManager playerManager, string slotUid, int playerId)
        {
            int inv = 0, bat = 0, pow = 0;
            if (zoneManager == null) return (0, 0, 0);

            var cards = zoneManager.GetAllCardsInAreaIncludingAttached(slotUid);
            foreach (var card in cards)
            {
                if (card.ControllerId != playerId) continue;
                if (card.IsTapped) continue;                 // 横置不提供任何能力
                if (!IsStatParticipant(card)) continue;      // 仅角色/附属/暗藏者参与
                if (IsExcludedByAreaStaticRule(card, slotUid)) continue;   // 【P3·K21】敌方暗藏者被静止规则排除

                inv += ValueOf(card, CombatManager.CombatType.Investigation, playerManager);
                bat += ValueOf(card, CombatManager.CombatType.Strength, playerManager);
                pow += ValueOf(card, CombatManager.CombatType.Power, playerManager);
            }
            return (inv, bat, pow);
        }

        /// <summary>
        /// 计算某玩家在本地区某单一对抗类型的能力总和（供 CombatManager 结算调用）。
        /// 与 GetAreaStats 共用同一份规则，仅聚焦单类型。
        /// </summary>
        public static int SumPlayerValue(
            ZoneManager zoneManager, PlayerManager playerManager, string areaUid, int playerId,
            CombatManager.CombatType type)
        {
            if (zoneManager == null) return 0;
            int total = 0;
            foreach (var card in zoneManager.GetAllCardsInAreaIncludingAttached(areaUid))
            {
                if (card.ControllerId != playerId) continue;
                if (card.IsTapped) continue;            // 横置不提供任何能力
                if (IsExcludedByAreaStaticRule(card, areaUid)) continue;   // 【P3·K21】与 GetAreaStats 同一规则
                total += ValueOf(card, type, playerManager);
            }
            return total;
        }
    }
}
