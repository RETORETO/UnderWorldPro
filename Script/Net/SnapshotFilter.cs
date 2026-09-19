using System.Collections.Generic;
using Script.Enums;

namespace Script.Net
{
    /// <summary>
    /// 【Phase B·信息隔离】按"观看者玩家"过滤整局快照：隐藏信息（对手手牌、双方牌库、地区牌堆）只下发
    /// **占位引用**，客户端据此建立只承载数量的占位实例（只画卡背），从而：
    ///   - 阻断"客户端可见对手手牌/牌库/牌堆内容"的信息泄露；
    ///   - 保持既有投影链路的"数量/顺序"语义不变（占位引用与真实引用一一对应，桶内计数一致），
    ///     使 UI（手牌卡背数量、牌库剩余数、地区牌堆数量）无需额外字段即可正确显示。
    ///
    /// 设计要点：
    ///   - **纯函数**：不修改传入的快照（服务端侧仍持有完整真相，供自省/重连使用）；
    ///   - **viewerPlayerId &lt;= 0 表示"不过滤"**（服务端内部/测试/调试取完整快照）；
    ///   - 隐藏判定：`Hand`/`Deck` 区且 `ZoneOwnerId != viewer`；`WorldDeckCards`（地区牌堆）对所有人隐藏。
    ///   - 公开区（争夺区/附属/封印/墓地/资产/分数/秘社/堆叠/地区注册表/势力标记）原样下发。
    /// </summary>
    public static class SnapshotFilter
    {
        /// <summary>占位引用的 UID 前缀（不含任何真实卡牌身份信息）。</summary>
        public const string HiddenUidPrefix = "hidden_";

        /// <summary>该区域是否属于"按玩家隐藏"的区域（仅手牌：自己可见、对手不可见）。</summary>
        public static bool IsPlayerOwnedHiddenZone(CardZone zone)
            => zone == CardZone.Hand;

        /// <summary>某条引用对观看者是否应隐藏。</summary>
        public static bool IsHiddenFor(ZoneCardRef r, int viewerPlayerId)
        {
            if (r == null) return false;
            if (viewerPlayerId <= 0) return false;          // 不过滤
            if (r.IsHidden) return true;                     // 已是占位
            // 牌库：对**双方**都不可见（单机也只看卡背与数量，从不暴露牌序）
            if (r.Zone == CardZone.Deck) return true;
            // 手牌：自己的可见、对手的隐藏
            return IsPlayerOwnedHiddenZone(r.Zone) && r.ZoneOwnerId != viewerPlayerId;
        }

        /// <summary>构造一条占位引用：保留"区域/归属/顺序"语义，抹掉一切卡牌身份与运行时状态。</summary>
        public static ZoneCardRef MakeHiddenRef(ZoneCardRef src, int index)
        {
            // 占位 UID 稳定可复现（同一观看者/区域/序号 → 同一 UID），便于客户端跨快照复用同一占位实例。
            string owner = src != null ? src.ZoneOwnerId.ToString() : "-1";
            string zone = src != null ? ((int)src.Zone).ToString() : "0";
            return new ZoneCardRef
            {
                Uid = $"{HiddenUidPrefix}{zone}_{owner}_{index}",
                TemplateUid = null,
                Zone = src?.Zone ?? CardZone.Hand,
                ZoneOwnerId = src?.ZoneOwnerId ?? -1,
                ZoneDetailId = null,     // 隐藏区无地区/宿主语义（手牌/牌库/牌堆）
                OwnerId = src?.ZoneOwnerId ?? -1,
                ControllerId = src?.ZoneOwnerId ?? -1,
                IsHidden = true,
            };
        }

        /// <summary>
        /// 为指定观看者过滤快照：隐藏区替换为占位引用，其余字段按引用复用（不改动原快照）。
        /// </summary>
        /// <param name="view">【P2】服务端为该观看者算好的合法性投影（见 `LegalityProjection`）；
        /// null = 不附带（旧服务端/测试路径），客户端将退回调用同一份规则函数本地判定。</param>
        public static GameSnapshot ForPlayer(GameSnapshot full, int viewerPlayerId, GameViewSnap view = null)
        {
            if (full == null) return null;
            if (viewerPlayerId <= 0) return full;   // 不过滤（服务端自省/测试）

            var filtered = new GameSnapshot
            {
                TurnCount = full.TurnCount,
                FirstPlayerId = full.FirstPlayerId,
                PriorityPlayerId = full.PriorityPlayerId,
                CurrentPhase = full.CurrentPhase,
                Players = full.Players,
                Stack = full.Stack,
                AreaMarks = full.AreaMarks,
                AreaCards = full.AreaCards,
                PendingInput = full.PendingInput,
                Random = full.Random,
                CostMods = full.CostMods,
                Zones = FilterZoneList(full.Zones, viewerPlayerId),
                // 【P2·合法性投影】服务端算好的候选集合随本查看者的快照下发（客户端只读不算）
                View = view ?? full.View,
            };

            // 地区牌堆对双方都隐藏（只看数量）
            if (full.WorldDeckCards != null)
            {
                filtered.WorldDeckCards = new List<ZoneCardRef>();
                for (int i = 0; i < full.WorldDeckCards.Count; i++)
                    filtered.WorldDeckCards.Add(MakeHiddenRef(full.WorldDeckCards[i], i));
            }

            // 待分配的调查卡：只发给决策者本人
            FilterInvestigation(full, filtered, viewerPlayerId);

            return filtered;
        }

        /// <summary>待分配的调查卡只发给决策者本人（其他人收到空表，避免泄露对手牌库内容）。</summary>
        private static void FilterInvestigation(GameSnapshot full, GameSnapshot view, int viewerPlayerId)
        {
            if (full.InvestigationOwnerPlayerId == viewerPlayerId)
            {
                view.InvestigationCards = full.InvestigationCards;
                view.InvestigationOwnerPlayerId = full.InvestigationOwnerPlayerId;
            }
        }

        private static List<ZoneCardRef> FilterZoneList(List<ZoneCardRef> src, int viewerPlayerId)
        {
            var result = new List<ZoneCardRef>();
            if (src == null) return result;
            for (int i = 0; i < src.Count; i++)
            {
                var r = src[i];
                if (r == null) continue;
                result.Add(IsHiddenFor(r, viewerPlayerId) ? MakeHiddenRef(r, i) : r);
            }
            return result;
        }
    }
}
