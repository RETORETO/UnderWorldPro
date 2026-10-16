using Script.Enums;
using Script.Game.Entities;
using System;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>候选列表的"呈现通道"判定结果。</summary>
    public enum CardPickChannelKind
    {
        /// <summary>候选全部落在 cat-1 范围内 ⇒ 走**提示窗**（场上高亮 + 左键点选即提交）。</summary>
        FieldClick = 0,
        /// <summary>候选里含任意一张 cat-2 的卡 ⇒ 整个列表走**选择窗**（卡面网格 + 区域标签 + 确认）。</summary>
        Window = 1,
    }

    /// <summary>
    /// 【选卡/找卡 UI 优化·批次 1】候选卡的 cat-1 / cat-2 分类器（**纯静态、双端可共用**）。
    ///
    /// 判定原则（方案 3.0–3.2）：
    /// - **逐卡实例、运行时判定**——不按效果声明、不按 `TargetKind` 静态映射；
    /// - 判定数据全部来自 `CardInstance` 现有字段（`CurrentZone` / `IsAttached` / `AttachedTo` /
    ///   `IsSeal` / `SealedBy` / `OwnerId`），**不新增任何字段**；
    /// - 只决定"要不要多开一个选择窗"，**不改变候选集合**（View 不增不减、不改合法性）。
    ///
    /// 归类（方案 3.1/3.2）：
    /// - **cat-1**：争夺区角色/暗藏者｜结附于地区的附属（`AttachedTo == null`）｜封印于地区的封印
    ///   （`SealedBy == null`）｜自己手牌（`OwnerId == chooserId`）｜秘社；
    /// - **cat-2**：结附/封印**于卡**的附属封印｜**资产（无条件）**｜牌库｜墓地｜对手手牌｜放逐区｜
    ///   计分区｜堆叠｜任何拿不到可见场上 UI 的卡。
    /// </summary>
    public static class CardPickChannel
    {
        /// <summary>单卡是否为 cat-1（可走场上高亮 + 左键点选）。</summary>
        /// <param name="inst">候选卡实例。</param>
        /// <param name="chooserId">本次做选择的玩家 id（决定"自己手牌"的归属）。</param>
        public static bool IsCat1(CardInstance inst, int chooserId)
        {
            if (inst == null) return false;
            switch (inst.CurrentZone)
            {
                case CardZone.AreaZone:
                    return true;   // 争夺区角色 / 暗藏者
                case CardZone.CouncilZone:
                    return true;   // 秘社（打出目标＝本方秘社的 11 张卡依赖它）
                case CardZone.Hand:
                    return inst.OwnerId == chooserId;   // 只认"自己手牌"；对手手牌 ⇒ cat-2
                case CardZone.AttachedZone:
                    // 结附于地区（host == null）⇒ cat-1；结附于角色/秘社/附属 ⇒ cat-2
                    return inst.IsAttached && inst.AttachedTo == null;
                case CardZone.SealedZone:
                    // 封印于地区（SealedBy == null）⇒ cat-1；封印于卡 ⇒ cat-2
                    return inst.IsSeal && inst.SealedBy == null;
                default:
                    // AssetZone（资产**无条件** cat-2）/ Deck / Graveyard / ScoreZone / Stack / Exiled
                    return false;
            }
        }

        /// <summary>按候选列表汇总：含任意一张 cat-2 ⇒ <see cref="CardPickChannelKind.Window"/>。</summary>
        public static CardPickChannelKind Classify(IEnumerable<CardInstance> candidates, int chooserId)
        {
            if (candidates != null)
            {
                foreach (var c in candidates)
                    if (!IsCat1(c, chooserId)) return CardPickChannelKind.Window;
            }
            // 全 cat-1（含空列表）：无窗可开，照旧走场上点选
            return CardPickChannelKind.FieldClick;
        }

        /// <summary>按候选 UID 列表汇总（联机侧 / 只有 UID 场景用）。</summary>
        public static CardPickChannelKind ClassifyUids(IEnumerable<string> uids, Func<string, CardInstance> resolve, int chooserId)
        {
            if (uids != null)
            {
                foreach (var uid in uids)
                {
                    var inst = resolve?.Invoke(uid);
                    if (!IsCat1(inst, chooserId)) return CardPickChannelKind.Window;
                }
            }
            return CardPickChannelKind.FieldClick;
        }
    }
}
