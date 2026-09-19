using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>房间设置（仅房主在创建房间时决定；本批只有初始地区数）。</summary>
    public class RoomSettings
    {
        /// <summary>初始地区数量（服务端建局时使用；默认 3，与现有 `SetupInitialAreas(3)` 一致）。</summary>
        public int InitialAreaCount = 3;
    }

    /// <summary>一个座位的状态（服务端权威）。</summary>
    public class RoomSeatState
    {
        /// <summary>座位号（1 或 2；对局内即 `playerId`）。</summary>
        public int Seat;

        /// <summary>玩家昵称（原样，允许重名）。</summary>
        public string PlayerName;

        /// <summary>房间内显示名：`昵称#座位`（D9：允许重名，靠座位号区分）。</summary>
        public string DisplayName;

        /// <summary>是否已准备（服务端校验卡组通过后才为 true）。</summary>
        public bool Ready;

        /// <summary>该座位的连接是否仍然在线（掉线后为 false）。</summary>
        public bool Connected;

        // ---- 卡组信息：按 D7，**对手座位这些字段一律不可见** ----
        /// <summary>是否已提交卡组（仅本人可见）。</summary>
        public bool HasDeck;
        /// <summary>卡组名（仅本人可见）。</summary>
        public string DeckName;
        /// <summary>卡组卡牌数（仅本人可见）。</summary>
        public int DeckCardCount;
        /// <summary>卡组校验是否通过（**对双方可见**：对手只看到"违规"这一状态，看不到原因）。</summary>
        public bool DeckValid;
        /// <summary>卡组违规原因（**仅本人可见**，可能含卡牌信息）。</summary>
        public string DeckError;

        /// <summary>本座位是否已请求"再来一局"（双方都点才执行，见 D10）。</summary>
        public bool RematchRequested;
    }

    /// <summary>对局结果摘要（仅 `Finished` 阶段）。</summary>
    public class MatchSummary
    {
        /// <summary>获胜座位（1/2；0 表示无胜者，如服务端中止）。</summary>
        public int WinnerSeat;
        /// <summary>结果原因文案（如"对手掉线判负""占领地区达到阈值"）。</summary>
        public string Reason;
    }

    /// <summary>
    /// 【大厅/房间 · 批次 1】房间状态（服务端权威；信封类型 `room_state`，**全量推送**）。
    ///
    /// 为什么全量推送而不是增量事件：与"快照即真相"一致、客户端无增量一致性负担、重发天然幂等。
    ///
    /// 信息可见性（D7）：房间状态**必须按查看者裁剪**后再下发，见 <see cref="ForViewer"/>——
    /// 对手的卡组名/张数/违规原因一律不下发（不是"客户端不显示"）。
    /// </summary>
    public class RoomState
    {
        public string RoomId;
        public string RoomName;
        public RoomPhase Phase;
        /// <summary>房主座位（建房者为 seat1；房主离开则转移）。</summary>
        public int HostSeat;
        public RoomSettings Settings = new RoomSettings();
        /// <summary>两个座位（seat1、seat2；空座位也会出现，`PlayerName` 为空）。</summary>
        public List<RoomSeatState> Slots = new List<RoomSeatState>();
        /// <summary>对局结果（仅 `Finished`）。</summary>
        public MatchSummary MatchSummary;
        /// <summary>本状态是为哪个座位裁剪的（客户端据此判断"哪个是我"）。</summary>
        public int ViewerSeat;

        /// <summary>取某座位。</summary>
        public RoomSeatState SeatState(int seat)
        {
            foreach (var s in Slots) if (s != null && s.Seat == seat) return s;
            return null;
        }

        /// <summary>
        /// 按查看者裁剪（服务端下发前调用）：**对手座位的卡组信息不可见**（D7）。
        /// 保留可见的：座位号、昵称、显示名、准备状态、在线状态、是否违规（仅状态不含原因）、重开请求。
        /// </summary>
        public RoomState ForViewer(int viewerSeat)
        {
            var copy = new RoomState
            {
                RoomId = RoomId,
                RoomName = RoomName,
                Phase = Phase,
                HostSeat = HostSeat,
                ViewerSeat = viewerSeat,
                Settings = Settings == null ? null : new RoomSettings { InitialAreaCount = Settings.InitialAreaCount },
                MatchSummary = MatchSummary == null ? null : new MatchSummary { WinnerSeat = MatchSummary.WinnerSeat, Reason = MatchSummary.Reason },
                Slots = new List<RoomSeatState>(),
            };
            foreach (var s in Slots ?? new List<RoomSeatState>())
            {
                if (s == null) continue;
                bool isSelf = s.Seat == viewerSeat;
                copy.Slots.Add(new RoomSeatState
                {
                    Seat = s.Seat,
                    PlayerName = s.PlayerName,
                    DisplayName = s.DisplayName,
                    Ready = s.Ready,
                    Connected = s.Connected,
                    RematchRequested = s.RematchRequested,
                    // 卡组：本人可见全部；对手只保留"是否违规"这一状态
                    HasDeck = isSelf && s.HasDeck,
                    DeckName = isSelf ? s.DeckName : null,
                    DeckCardCount = isSelf ? s.DeckCardCount : 0,
                    DeckValid = s.DeckValid,
                    DeckError = isSelf ? s.DeckError : null,
                });
            }
            return copy;
        }

        /// <summary>构造显示名（D9：`昵称#座位`；昵称为空时用"空位"）。</summary>
        public static string MakeDisplayName(string playerName, int seat)
            => string.IsNullOrWhiteSpace(playerName) ? $"空位#{seat}" : $"{playerName}#{seat}";
    }
}
