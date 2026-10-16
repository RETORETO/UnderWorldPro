using System;
using Script.Enums;
using Script.Game.Entities;

namespace Script.UI
{
    /// <summary>
    /// 【选卡/找卡 UI 优化·批次 1】选择窗里每张候选卡下方"所在区域"标签的唯一解析入口
    /// （**纯静态、纯展示**，见方案 5.2）。
    ///
    /// 目标：标签必须**明确到"哪一个玩家、哪一个地区"**，不出现"封印区""附属区"这种分不清归属的写法。
    ///
    /// **玩家名扩展点**：所有玩家称呼一律经 `playerNameOf` 委托取，组件内**禁止出现 "玩家1"/"玩家2" 字面量**。
    /// Arena 注入 `id => _playerManager.GetPlayer(id).Name`；将来把 `PlayerData.Name` 换成真实玩家名时，
    /// 本文件一行都不用改。
    /// </summary>
    public static class CardZoneLabel
    {
        /// <summary>
        /// 描述一张候选卡"所在区域"。
        /// </summary>
        /// <param name="inst">候选卡实例。</param>
        /// <param name="chooserId">本次做选择的玩家 id（其本人显示为"你"）。</param>
        /// <param name="playerNameOf">玩家 id → 玩家显示名（Arena 注入 `_playerManager.GetPlayer(id).Name`）。</param>
        /// <param name="areaIndexOf">地区 UID → 地区编号（1-based，Arena 注入 `_areaManager.GetSlotIndex`）。可空。</param>
        public static string Describe(CardInstance inst, int chooserId,
            Func<int, string> playerNameOf, Func<string, int> areaIndexOf = null)
        {
            if (inst == null) return "";

            string F(int id) => id == chooserId
                ? "你"
                : (playerNameOf?.Invoke(id) ?? $"玩家{id}");
            string AreaOf(CardInstance c) =>
                $"{F(c.ControllerId)}地区{areaIndexOf?.Invoke(c.ZoneDetailId) ?? 0}";

            switch (inst.CurrentZone)
            {
                case CardZone.AreaZone:
                    return $"{AreaOf(inst)}{(inst.IsCovert ? "（暗藏）" : "")}";

                case CardZone.AttachedZone:
                    // 结附于地区（host == null）⇒ 写"附属于{玩家}地区{N}"；绝不写"附属区"
                    if (inst.IsAttached && inst.AttachedTo != null)
                        return $"附属于{DescribeHost(inst.AttachedTo, chooserId, playerNameOf, areaIndexOf)}";
                    return $"附属于{AreaOf(inst)}";

                case CardZone.SealedZone:
                    if (inst.IsSeal && inst.SealedBy != null)
                        return $"封印于{DescribeHost(inst.SealedBy, chooserId, playerNameOf, areaIndexOf)}";
                    return $"封印于{AreaOf(inst)}";

                case CardZone.Hand:
                    // 自己手牌 ⇒ "你的手牌"；对手手牌 ⇒ "玩家N的手牌"（一律带玩家名）
                    return $"{F(inst.OwnerId)}的手牌";

                case CardZone.Deck:
                    return $"{F(inst.ControllerId)}的牌库";

                case CardZone.Graveyard:
                    return $"{F(inst.ControllerId)}的墓地";

                case CardZone.ScoreZone:
                    return $"{F(inst.ControllerId)}的计分区";

                case CardZone.AssetZone:
                    return $"{F(inst.ControllerId)}的资产区";

                case CardZone.CouncilZone:
                    return $"{F(inst.ControllerId)}的秘社";

                case CardZone.Stack:
                    return $"堆叠（{F(inst.ControllerId)}）";

                case CardZone.Exiled:
                    // 【已知限制】ZoneManager.GetZoneCards 没有 Exiled 分支 ⇒ 无玩家维度可查，
                    // 只能写"放逐区"。要区分双方放逐区需先补该分支（属改逻辑，本批不做）。
                    return "放逐区";

                default:
                    return inst.CurrentZone.ToString();
            }
        }

        /// <summary>宿主（附属/封印的承载者）的显示：秘社带玩家归属，其余走宿主卡名。</summary>
        private static string DescribeHost(CardInstance host, int chooserId,
            Func<int, string> playerNameOf, Func<string, int> areaIndexOf)
        {
            if (host == null) return "";
            if (host.CurrentZone == CardZone.CouncilZone)
            {
                string f = host.ControllerId == chooserId
                    ? "你"
                    : (playerNameOf?.Invoke(host.ControllerId) ?? $"玩家{host.ControllerId}");
                return $"{f}的秘社";
            }
            // 角色 / 暗藏者 / 附属：用宿主卡名（走 EffectiveCardName，兼容 JC028「鲜血」式改名覆盖）
            string name = host.EffectiveCardName;
            if (string.IsNullOrEmpty(name)) name = host.PlayerTemplate?.CardName ?? "";
            if (string.IsNullOrEmpty(name)) name = host.CouncilTemplate?.CardName ?? "";
            return $"「{name}」";
        }

        /// <summary>
        /// 描述"地区目标（地区卡本身）"：**不带玩家归属**（地区是共享世界牌、两侧共用）。
        /// 例：`地区3（荒废的遗迹）`。最易与"地区内卡"混淆，故必须与 <see cref="Describe"/> 分开。
        /// </summary>
        public static string DescribeArea(string areaUid, string areaName, Func<string, int> areaIndexOf)
        {
            int n = areaIndexOf?.Invoke(areaUid) ?? 0;
            return string.IsNullOrEmpty(areaName) ? $"地区{n}" : $"地区{n}（{areaName}）";
        }
    }
}
