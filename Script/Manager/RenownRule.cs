using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【P3·词条规则】声望（Renown）——用户 2026-09 规则文本：
    ///  · 类型：触发效果；
    ///  · 效果：**每个势力对抗结束后**，由该地区中**拥有较多声望角色的一方**触发，给该地区添加一个势力标志；
    ///  · **多个声望角色在场也只能添加一个**势力标志（去重与威名同款）；
    ///  · **不依赖势力对抗获胜**（平局、甚至败方，只要声望角色更多就触发）。
    ///
    /// 实现口径（已与用户确认的默认值）：
    ///  · "声望角色" = 该地区内、由该玩家控制、且是**角色**的卡，其声望来自运行期授予或卡面固有声明；
    ///  · 不要求未横置，也不要求参与该次势力对抗；
    ///  · 比较为**严格多于**（相等或双方均为 0 → 不触发）。
    ///
    /// 消费点：`CombatManager` 的势力对抗结束分支（单机 + 联机两条路径同源）调用 `ApplyIfMajority`。
    /// 卡级触发形式另见 `KeywordEffects.Renown()` + 条件谓词 `RenownMajority`（两者共用本类的计数实现，不会分叉）。
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
                if (c.HasKeyword("声望") || tmpl.HasInherentKeyword("声望")) n++;
            }
            return n;
        }

        /// <summary>
        /// 势力对抗结束后执行声望规则：若某一方的声望角色**严格多于**对手，则在该地区给它 +1 势力标志。
        /// 返回实际放置者（0 = 无人满足）。
        /// </summary>
        public static int ApplyIfMajority(ZoneManager zm, AreaManager am, string areaUid)
        {
            if (zm == null || am == null || string.IsNullOrEmpty(areaUid)) return 0;
            int p1 = CountRenownCharacters(zm, areaUid, 1);
            int p2 = CountRenownCharacters(zm, areaUid, 2);
            if (p1 <= 0 && p2 <= 0)
            {
                Godot.GD.Print($"[声望] 地区{areaUid}：双方均无声望角色 → 不放置");
                return 0;
            }
            if (p1 == p2)
            {
                Godot.GD.Print($"[声望] 地区{areaUid}：双方声望角色相等（{p1}:{p2}）→ 不放置");
                return 0;
            }
            int majority = p1 > p2 ? 1 : 2;
            am.AddPowerMark(areaUid, majority, 1);   // 多个声望角色也只放 1 个（规则文本）
            Godot.GD.Print($"[声望] 地区{areaUid}：玩家{majority} 声望角色更多（{p1}:{p2}）→ 放置 1 个势力标志");
            return majority;
        }
    }
}
