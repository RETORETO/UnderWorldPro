using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【P3·K4】附属与暗藏者的**通用规则**：宿主翻面为暗藏者 ⇒ 其上「结附于角色」的附属被**消灭**。
    ///
    /// 规则来源：JC036 初拥 FAQ 第二条——「当被初拥结附的角色因其他卡牌能力而翻面为暗藏者后，
    /// 初拥会被消灭，该暗藏者的操控权交还给其拥有者」。
    /// **用户 2026-09 口径**：这条做成**通用规则**（不是 JC036 的个别处理）——任何「结附于角色」的附属，
    /// 其宿主翻面为暗藏者时一律被消灭（同期受影响：JC073 苏族捕梦网 / JC089 毒血诅咒 / JC116 柯尔特巨蟒左轮 …）。
    ///
    /// 判据（刻意收窄，避免"自毁"）：
    ///  · 只用「宿主**此刻是暗藏者**」这一条，**不重判结附目标规则本身**——
    ///    否则 JC036 把宿主的人类类别改写为吸血鬼后，"结附于印刷类别为人类的角色"会立刻不成立、初拥当场自毁；
    ///  · 只处理 `AttachedTo` 指向该宿主的**附属**（结附于地区/秘社的附属不受影响：秘社不会翻面）。
    ///
    /// 消费点：卡"翻面为暗藏者"之后（`CardChangeKind.CovertChanged` 且 `IsCovert == true`），
    /// 由装配层 `GameInitializer` 订阅 `CardManager.OnCardStateChanged` 后调用（与 `OnLatent` 广播同一处）。
    /// 落地方式＝`CardManager.DestroyInstance`（**消灭＝死亡**，照常广播死亡触发、走墓地）。
    /// </summary>
    public static class AttachmentRule
    {
        /// <summary>
        /// 【翻面消费点】一张卡**翻面为暗藏者**后调用：消灭其上结附的全部附属。
        /// 幂等：附属被消灭时会从宿主 `AttachedCards` 摘除，重复调用不会重复处理。
        /// </summary>
        public static int EnforceOnCovertChange(CardInstance host, CardManager cm)
        {
            if (host == null || !host.IsCovert) return 0;
            if (host.AttachedCards == null || host.AttachedCards.Count == 0) return 0;
            var manager = cm ?? CardManager.Instance;
            if (manager == null) return 0;

            // 复制一份再遍历：消灭走 `MoveToGraveyard` → `RemoveFromAllZones` 会改写宿主的 AttachedCards。
            var attached = new List<CardInstance>(host.AttachedCards);
            int destroyed = 0;
            foreach (var att in attached)
            {
                if (att == null || att.IsDying) continue;
                if (att.CurrentZone != CardZone.AttachedZone) continue;             // 须仍在结附状态
                if (!ReferenceEquals(att.AttachedTo, host)) continue;               // 须确实结附在本宿主上
                GD.Print($"[附属规则] 宿主 {host.PlayerTemplate?.CardName}(UID={host.UID}) 翻面为暗藏者 → 消灭其上的附属 "
                       + $"{att.PlayerTemplate?.CardName}(UID={att.UID})");
                manager.DestroyInstance(att);
                destroyed++;
            }
            if (destroyed > 0)
                GD.Print($"[附属规则] 共消灭 {destroyed} 张附属（宿主翻面为暗藏者；用户 2026-09 口径＝通用规则）");
            return destroyed;
        }
    }
}
