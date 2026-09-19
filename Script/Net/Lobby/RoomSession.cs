using System;
using System.Collections.Generic;
using Script.IO;

namespace Script.Net.Lobby
{
    /// <summary>房间内的一个座位（纯数据；`Connected` 由 `LobbyServer` 结合会话目录填充）。</summary>
    public class RoomSeat
    {
        public int Seat;
        public string SessionId;
        public string PlayerName;

        /// <summary>是否已准备（服务端校验卡组通过后才为 true）。</summary>
        public bool Ready;

        /// <summary>本座位是否已请求"再来一局"（双方都请求才执行，见 D10）。</summary>
        public bool RematchRequested;

        // ---- 卡组（仅本人可见，D7）----
        public DeckList Deck;
        public string DeckName;
        public int DeckCardCount;
        public bool DeckValid;
        public string DeckError;

        public bool Occupied => !string.IsNullOrEmpty(SessionId);
    }

    /// <summary>
    /// 【大厅/房间 · 增量 A】房间：状态机 + 数据（服务端权威）。
    ///
    /// 阶段迁移（与设计方案 §六 一致）：
    ///   Waiting ⇄ Ready（两人在座且都准备 → Ready；有人取消准备/离座 → Waiting）
    ///   Ready → Starting（房主开始，服务端复检卡组）→ InMatch → Finished
    ///   Finished →（双方都请求重开）→ Waiting（复用原房间，见 D10）
    ///   任意阶段 → Closed（空房/超时/踢空/服务端停止）
    ///
    /// 本类**不含协议与广播**（那是 `LobbyServer` 的职责），也不含对局规则（那是 `IRoomMatchHost`）。
    /// </summary>
    public class RoomSession
    {
        public string RoomId;
        public string RoomName;
        public RoomPhase Phase = RoomPhase.Waiting;
        public int HostSeat = 1;
        public RoomSettings Settings = new RoomSettings();

        /// <summary>两个固定座位（索引与座位号一致：Seats[1]、Seats[2]）。</summary>
        public readonly RoomSeat[] Seats = { null, new RoomSeat { Seat = 1 }, new RoomSeat { Seat = 2 } };

        /// <summary>当前对局宿主（`InMatch`/`Finished` 期间非空）。</summary>
        public IRoomMatchHost Match;

        /// <summary>对局结果（仅 `Finished`）。</summary>
        public MatchSummary Summary;

        public long CreatedAtTick;
        public long LastActiveTick;
        public long FinishedAtTick;
        /// <summary>进入 `Starting` 的时刻（H1 兜底：`LobbyServer.Tick` 用它检测"卡在 Starting"的房间并回退）。</summary>
        public long StartingAtTick;

        public RoomSeat Seat(int seat) => (seat == 1 || seat == 2) ? Seats[seat] : null;

        /// <summary>取某会话所在座位（不在本房返回 null）。</summary>
        public RoomSeat SeatOfSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            for (int s = 1; s <= 2; s++)
                if (Seats[s].SessionId == sessionId) return Seats[s];
            return null;
        }

        /// <summary>在座人数（含掉线但未清理的座位）。</summary>
        public int OccupiedCount
        {
            get
            {
                int n = 0;
                for (int s = 1; s <= 2; s++) if (Seats[s].Occupied) n++;
                return n;
            }
        }

        /// <summary>是否两人都在座且都准备（→ 阶段应为 Ready）。</summary>
        public bool BothReady => Seats[1].Occupied && Seats[2].Occupied && Seats[1].Ready && Seats[2].Ready;

        /// <summary>是否处于"可自由改座位/准备/卡组"的阶段。</summary>
        public bool IsLobbyPhase => Phase == RoomPhase.Waiting || Phase == RoomPhase.Ready;

        /// <summary>取一个空座位号（无空位返回 0）。</summary>
        public int FreeSeat()
        {
            for (int s = 1; s <= 2; s++) if (!Seats[s].Occupied) return s;
            return 0;
        }

        /// <summary>按当前座位/准备情况刷新 `Waiting`/`Ready`（仅在这两个阶段间切换）。</summary>
        public void RefreshLobbyPhase()
        {
            if (!IsLobbyPhase) return;
            Phase = BothReady ? RoomPhase.Ready : RoomPhase.Waiting;
        }

        /// <summary>清空某座位（离座/被踢）。</summary>
        public void ClearSeat(int seat)
        {
            var s = Seat(seat);
            if (s == null) return;
            s.SessionId = null;
            s.PlayerName = null;
            s.Ready = false;
            s.RematchRequested = false;
            s.Deck = null;
            s.DeckName = null;
            s.DeckCardCount = 0;
            s.DeckValid = false;
            s.DeckError = null;
        }

        /// <summary>房主转移：把房主给另一个在座座位（无人在座返回 false）。</summary>
        public bool TransferHostIfNeeded()
        {
            if (Seat(HostSeat)?.Occupied == true) return false;
            for (int s = 1; s <= 2; s++)
                if (Seats[s].Occupied) { HostSeat = s; return true; }
            return false;
        }

        /// <summary>构造发给某查看者的房间状态（服务端权威 → 由 `ServerHost.SendRoomState` 再裁剪）。</summary>
        public RoomState ToState(Func<string, bool> isConnected)
        {
            var state = new RoomState
            {
                RoomId = RoomId,
                RoomName = RoomName,
                Phase = Phase,
                HostSeat = HostSeat,
                Settings = new RoomSettings { InitialAreaCount = Settings.InitialAreaCount },
                MatchSummary = Summary == null ? null : new MatchSummary { WinnerSeat = Summary.WinnerSeat, Reason = Summary.Reason },
                Slots = new List<RoomSeatState>(),
            };
            for (int s = 1; s <= 2; s++)
            {
                var seat = Seats[s];
                state.Slots.Add(new RoomSeatState
                {
                    Seat = s,
                    PlayerName = seat.PlayerName,
                    DisplayName = RoomState.MakeDisplayName(seat.PlayerName, s),
                    Ready = seat.Ready,
                    Connected = seat.Occupied && (isConnected == null || isConnected(seat.SessionId)),
                    RematchRequested = seat.RematchRequested,
                    HasDeck = seat.Deck != null,
                    DeckName = seat.DeckName,
                    DeckCardCount = seat.DeckCardCount,
                    DeckValid = seat.DeckValid,
                    DeckError = seat.DeckError,
                });
            }
            return state;
        }

        /// <summary>大厅列表摘要。</summary>
        public RoomBrief ToBrief()
        {
            var host = Seat(HostSeat);
            return new RoomBrief
            {
                RoomId = RoomId,
                RoomName = RoomName,
                Phase = Phase,
                PlayerCount = OccupiedCount,
                MaxPlayers = 2,
                HostDisplayName = RoomState.MakeDisplayName(host?.PlayerName, HostSeat),
            };
        }
    }
}
