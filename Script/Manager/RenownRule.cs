using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【P3·词条规则】声望（Renown）——用户 2026-09 规则文本：
    ///  · 类型：**触发效果**（不是立即结算）；
    ///  · 时机：**一个地区的所有对抗结束后**（含 JC067 追加的那次势力对抗之后；同一地区每回合只触发一次）；
    ///  · 效果：由该地区中**拥有较多声望角色的一方**触发，给该地区添加一个势力标志；
    ///  · **多个声望角色在场也只能添加一个**势力标志（去重与威名同款）；
    ///  · **不依赖势力对抗获胜**（平局、甚至败方，只要声望角色更多就触发）。
    ///
    /// 实现口径（已与用户确认的默认值）：
    ///  · "声望角色" = 该地区内、由该玩家控制、且是**角色**的卡，其声望来自运行期授予或卡面固有声明；
    ///  · 不要求未横置，也不要求参与该次势力对抗；
    ///  · 比较为**严格多于**（相等或双方均为 0 → 不触发）。
    ///
    /// 【2026-09 用户口径·改为触发】原先的"引擎级立即结算"（`CombatManager` 在势力对抗出口直接
    /// `ApplyIfMajority`）**已删除**：用户实测口径"声望是触发效果……应为势力对抗结算后**触发**声望，
    /// 参考威名（但不需要势力对抗获胜，只需要计算声望角色数量），并且不管势力对抗进行几次（JC067）
    /// 声望都只触发一次"。现由触发路径承担：
    ///  · 触发点＝`TurnManager` 在"该地区三种对抗全部结算完毕"时广播 `TriggerEmitPoints.PowerCombatEnded`
    ///    （自带"每地区每回合一次"幂等守卫）；
    ///  · 触发形态＝`KeywordEffects.Renown()`（`OnPowerCombatEnded` + 条件 `RenownMajority` +
    ///    `PlaceToken{势力标志,1,声望}`，`Forced=false` ⇒ 玩家可放弃），并登记进
    ///    `KeywordEffects.RuntimeTriggerTemplates()` ⇒ 任何**拥有【声望】关键字**的角色（卡面固有或运行期授予）
    ///    都会参与收集；
    ///  · 本类只保留**计数**实现（`CountRenownCharacters`），供条件谓词使用（单一真相源）。
    /// </summary>
    public static class RenownRule
    {
        /// <summary>统计某地区内某玩家的"声望角色"数量。</summary>
        public static int CountRenownCharacters(ZoneManager zm, string areaUid, int controllerId)
        {
            if (zm == null || string.IsNullOrEmpty(areaUid) || controllerId <= 0) return 0;
            int n = 0;
            foreach (var c in zm.GetZoneCards(CardZone.AreaZone, 0, areaUid))
            {
                if (c == null || c.ControllerId != controllerId) continue;
                var tmpl = c.PlayerTemplate;
                if (tmpl == null || (tmpl.CategoryFlag & CardCategory.Character) == 0) continue;
                // 【2026-09 用户口径·通用】"声望"是**持续关键字**：背面朝上的角色（暗藏者）**不提供**其词条
                // （＝不算"声望角色"），故走"当前有效"判定（运行期授予 ∪ 非暗藏时的卡面固有）。
                if (c.HasActiveKeyword("声望")) n++;
            }
            return n;
        }
    }
}
