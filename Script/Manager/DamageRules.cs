using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【P3·词条规则】伤害相关的规则收口（当前：灵体）。
    ///
    /// **灵体**（用户 2026-09 规则文本）：
    ///  类型：持续效果。
    ///  触发：当一个灵体角色被分配伤害（战斗或效果造成的伤害都在范围内）。
    ///  计算：计算灵体角色所在地区中所有角色/附属卡的**魔法领域图标数量**。
    ///  判定：如果灵体角色的**对手**在本地区的图标数量 **小于** 灵体角色的**控制者**在本地区的图标数量 → **防止伤害**。
    ///
    /// 实现口径（已与用户确认的默认值）：
    ///  · 计数**包含灵体角色自身**（它就是该地区的一张角色卡）；
    ///  · **包含暗藏者**（背面朝上的角色卡同样带领域图标）；
    ///  · **附属卡**按其 `ControllerId` 归属计入所在地区（附属的 ZoneDetailId = 所属地区）；
    ///  · "图标数量" = 各卡 `MagicDomains` 的元素个数之和（多领域卡算多个）；
    ///  · 比较为**严格小于**才防止（相等不防止）；
    ///  · 防止 = 不写伤害 → 不判死 → 不触发死亡（由 `CardManager.DealDamageToInstance` 的 `DamagePreventer` 钩子承担）。
    ///
    /// 消费点：`GameInitializer` 把 `IsSpiritPrevention` 接到 `CardManager.DamagePreventer`。
    /// </summary>
    public static class DamageRules
    {
        /// <summary>该卡是否为灵体（**当前有效**）：运行期授予的关键字 ∪ 卡面固有声明。
        /// 【2026-09 用户口径·通用】背面朝上的卡（暗藏者）不提供其效果 ⇒ `灵体` 休眠（走 `HasActiveKeyword`）。</summary>
        public static bool IsSpirit(CardInstance card) => card != null && card.HasActiveKeyword("灵体");

        /// <summary>
        /// 灵体伤害防止判定：true = 本次伤害被防止（不落伤害、不判死、不触发死亡）。
        /// </summary>
        public static bool IsSpiritPrevention(CardInstance target, ZoneManager zm)
        {
            if (target == null || zm == null) return false;
            if (!IsSpirit(target)) return false;

            string area = target.ZoneDetailId;
            if (string.IsNullOrEmpty(area)) return false;

            int mine = 0, theirs = 0;
            foreach (var c in zm.GetAllCardsInAreaIncludingAttached(area))
            {
                if (c == null) continue;
                int icons = c.MagicDomains?.Count ?? 0;
                if (icons <= 0) continue;
                if (c.ControllerId == target.ControllerId) mine += icons;
                else theirs += icons;
            }

            bool prevent = theirs < mine;
            Godot.GD.Print($"[灵体] {target.PlayerTemplate?.CardName} @地区{area}：本方(含自身)领域图标={mine}，" +
                           $"对手={theirs} → {(prevent ? "防止本次伤害" : "不防止（对手图标不少于本方）")}");
            return prevent;
        }
    }
}
