using System;
using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】大厅业务实现：**服务端权威**的房间列表与房间状态机。
    ///
    /// 同时实现两个接口：
    ///   · `ILobbyHandler`：处理握手层交来的 `lobby_req`（已通过 Hello/版本/幂等校验）；
    ///   · `IMatchRouter`：把对局通道消息（`intent`/`choice_res`）按"会话 → 房间 → 该房对局宿主"路由。
    ///
    /// 权威与校验（不信任客户端）：
    ///   · 权限：只有房主能开始/踢人；越权一律拒绝并回具体错误码；
    ///   · 阶段：`Starting`/`InMatch` 阶段拒绝一切房间操作（避免开局竞态）；
    ///   · 卡组（D6）：点「准备」时校验（不通过则准备不生效）；点「开始」时**复检**（不通过则不开始并取消该方准备）；
    ///     **已准备后卡组锁定**（再发 `SelectDeck` 一律 `DeckLocked` 拒绝，需先取消准备）；
    ///   · 可见性（D7）：对手卡组信息由 `RoomState.ForViewer` 在下发前裁掉（本类只负责把状态交给 `ServerHost`）。
    ///
    /// 与业务无关的连接/协议/幂等由 `ServerHost` 负责；对局规则由 `IRoomMatchHost`（Godot 侧 `MatchHost`）负责。
    /// </summary>
    public class LobbyServer : ILobbyHandler, IMatchRouter, ILobbyShutdown
    {
        private readonly RoomRegistry _rooms;
        private readonly IDeckValidator _deckValidator;
        private readonly Func<RoomSession, IRoomMatchHost> _matchFactory;
        private readonly ServerConfig _config;

        /// <summary>会话 → 房间号（本类的成员权威表）。</summary>
        private readonly Dictionary<string, string> _sessionRoom = new Dictionary<string, string>();

        /// <summary>会话目录（由 `ServerHost` 注入：查昵称/在线状态）。</summary>
        public ServerHost Host { get; set; }

        /// <summary>日志钩子（默认 Console）。</summary>
        public Action<string> LogHook { get; set; }

        /// <summary>房间被销毁时回调（服务端入口可用于释放对局节点等）。</summary>
        public Action<RoomSession> OnRoomClosed;

        /// <summary>
        /// 【H1】建局失败回退时回调：请服务端入口释放"半成品对局装配"（`ServerMatchFactory.Destroy`）。
        /// 与 `OnRoomClosed` 分开命名：房间**没有**关闭，只是这一局的装配被丢弃。
        /// </summary>
        public Action<RoomSession> OnMatchAssemblyReleased;

        /// <summary>卡在 `Starting` 的兜底回退时限（毫秒）：超过则视为建局异常残留，退回 `Waiting`。</summary>
        public const long StartingGraceMs = 15000;

        public LobbyServer(ServerConfig config, IDeckValidator deckValidator,
            Func<RoomSession, IRoomMatchHost> matchFactory)
        {
            _config = config ?? new ServerConfig();
            _deckValidator = deckValidator ?? new PermissiveDeckValidator();
            _matchFactory = matchFactory;
            _rooms = new RoomRegistry(_config.MaxRooms);
        }

        public int RoomCount => _rooms.Count;
        public RoomSession GetRoom(string roomId) => _rooms.Get(roomId);
        public string RoomOfSession(string sessionId)
            => !string.IsNullOrEmpty(sessionId) && _sessionRoom.TryGetValue(sessionId, out var rid) ? rid : null;

        // ====================================================================
        // ILobbyHandler
        // ====================================================================

        public void Handle(string sessionId, LobbyRequest req)
        {
            if (req == null) return;
            var room = CurrentRoom(sessionId);
            switch (req.Kind)
            {
                case LobbyRequestKind.ListRooms: HandleListRooms(sessionId, req); break;
                case LobbyRequestKind.CreateRoom: HandleCreateRoom(sessionId, req); break;
                case LobbyRequestKind.JoinRoom: HandleJoinRoom(sessionId, req); break;
                case LobbyRequestKind.LeaveRoom: HandleLeaveRoom(sessionId, req); break;
                case LobbyRequestKind.SetReady: HandleSetReady(sessionId, req, room); break;
                case LobbyRequestKind.SelectDeck: HandleSelectDeck(sessionId, req, room); break;
                case LobbyRequestKind.StartMatch: HandleStartMatch(sessionId, req, room); break;
                case LobbyRequestKind.Kick: HandleKick(sessionId, req, room); break;
                case LobbyRequestKind.Ping:
                    {
                        var res = LobbyResponse.Success(req.RequestId);
                        res.SessionId = sessionId;
                        Host?.Respond(sessionId, res);
                        break;
                    }
                default:
                    Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.UnknownRequest);
                    break;
            }
        }

        /// <summary>会话断开（含主动退出/踢人后的连接关闭）：按"离开房间"处理。</summary>
        public void OnSessionLeft(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            var room = CurrentRoom(sessionId);
            if (room == null) return;
            LeaveRoomInternal(room, sessionId, "连接断开", notifySelf: false);
        }

        // ====================================================================
        // IMatchRouter（对局通道）
        // ====================================================================

        public bool TryRoute(string sessionId, string envelopeType, string payload)
        {
            var room = CurrentRoom(sessionId);
            var match = room?.Match;
            if (match == null) return false;
            // M3：**只有 InMatch 才对局通道开放**。终局后仍放行会让已结束的对局继续改状态甚至二次触发结束信号，
            // 使客户端看到的结果与房间广播不一致（下一局由服务端在结果展示结束后自动复用房间驱动，不需要 Finished 通道）。
            if (room.Phase != RoomPhase.InMatch) return false;
            return match.HandleMessage(sessionId, envelopeType, payload);
        }

        // ====================================================================
        // ILobbyShutdown（服务端停止）
        // ====================================================================

        /// <summary>
        /// 【H3】服务端停止前关闭全部房间：`CloseRoom` 会给在座客户端下发 `RoomState(Phase=Closed)`，
        /// 客户端据此回大厅并提示，而不是看到"连接无声消失 + 页面卡住"。幂等（房间已关闭时为空操作）。
        /// </summary>
        public void OnServerStopping(string reason)
        {
            var all = new List<RoomSession>(_rooms.All);
            if (all.Count > 0) Log($"[Lobby] 服务端停止：关闭全部房间（{all.Count} 个）");
            foreach (var room in all) CloseRoom(room, reason ?? "服务端已停止");
        }

        // ====================================================================
        // 大厅：列表 / 创建 / 加入
        // ====================================================================

        private void HandleListRooms(string sessionId, LobbyRequest req)
        {
            var res = LobbyResponse.Success(req.RequestId);
            res.RoomList = _rooms.List();
            res.SessionId = sessionId;
            Host?.Respond(sessionId, res);
        }

        private void HandleCreateRoom(string sessionId, LobbyRequest req)
        {
            if (CurrentRoom(sessionId) != null)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.AlreadyInRoom);
                return;
            }
            if (_rooms.IsFull)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "服务器房间已满，请稍后再试。");
                return;
            }
            string hostName = PlayerName(sessionId);
            var room = _rooms.Create($"{hostName} 的房间", Now(), req.RoomSettings);
            if (room == null)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "服务器房间已满。");
                return;
            }
            var seat = room.Seat(1);
            seat.SessionId = sessionId;
            seat.PlayerName = hostName;
            room.HostSeat = 1;
            room.RefreshLobbyPhase();
            _sessionRoom[sessionId] = room.RoomId;
            Log($"[Lobby] {sessionId}（{seat.PlayerName}）创建房间 {room.RoomId}");

            var res = LobbyResponse.Success(req.RequestId);
            res.SessionId = sessionId;
            res.RoomState = room.ToState(IsConnected).ForViewer(1);
            Host?.Respond(sessionId, res);
            Broadcast(room);
        }

        private void HandleJoinRoom(string sessionId, LobbyRequest req)
        {
            if (CurrentRoom(sessionId) != null)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.AlreadyInRoom);
                return;
            }
            var room = _rooms.Get(req.RoomId);
            if (room == null)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.RoomNotFound);
                return;
            }
            if (!room.IsLobbyPhase)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "该房间正在对局中，无法加入。");
                return;
            }
            int seatNo = room.FreeSeat();
            if (seatNo == 0)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.RoomFull);
                return;
            }
            var seat = room.Seat(seatNo);
            seat.SessionId = sessionId;
            seat.PlayerName = PlayerName(sessionId);
            room.RefreshLobbyPhase();
            room.LastActiveTick = Now();
            _sessionRoom[sessionId] = room.RoomId;
            Log($"[Lobby] {sessionId}（{seat.PlayerName}）加入房间 {room.RoomId} 座位 {seatNo}");

            var res = LobbyResponse.Success(req.RequestId);
            res.SessionId = sessionId;
            res.RoomState = room.ToState(IsConnected).ForViewer(seatNo);
            Host?.Respond(sessionId, res);
            Broadcast(room);
        }

        /// <summary>主动离开房间（协议请求）。</summary>
        private void HandleLeaveRoom(string sessionId, LobbyRequest req)
        {
            var room = CurrentRoom(sessionId);
            if (room == null)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom);
                return;
            }
            var res = LobbyResponse.Success(req.RequestId);
            res.SessionId = sessionId;
            Host?.Respond(sessionId, res);
            LeaveRoomInternal(room, sessionId, "玩家离开", notifySelf: false);
        }

        // ====================================================================
        // 房间：准备 / 卡组
        // ====================================================================

        private void HandleSetReady(string sessionId, LobbyRequest req, RoomSession room)
        {
            if (room == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }
            if (!room.IsLobbyPhase)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "当前阶段无法改变准备状态。");
                return;
            }
            var seat = room.SeatOfSession(sessionId);
            if (seat == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }

            if (req.Ready && !seat.DeckValid)
            {
                // D6：违规卡组点准备 → 报"卡组违规"，准备不生效
                string why = string.IsNullOrEmpty(seat.DeckError) ? "请先选择合法卡组。" : seat.DeckError;
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.InvalidDeck, $"卡组违规：{why}");
                Broadcast(room);
                return;
            }

            seat.Ready = req.Ready;
            room.RefreshLobbyPhase();
            room.LastActiveTick = Now();
            Log($"[Lobby] 房间 {room.RoomId} 座位 {seat.Seat}（{seat.PlayerName}）{(req.Ready ? "已准备" : "取消准备")} → 阶段 {room.Phase}");

            var res = LobbyResponse.Success(req.RequestId);
            res.SessionId = sessionId;
            res.RoomState = room.ToState(IsConnected).ForViewer(seat.Seat);
            Host?.Respond(sessionId, res);
            Broadcast(room);
        }

        private void HandleSelectDeck(string sessionId, LobbyRequest req, RoomSession room)
        {
            if (room == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }
            if (!room.IsLobbyPhase)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "当前阶段无法更换卡组。");
                return;
            }
            var seat = room.SeatOfSession(sessionId);
            if (seat == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }

            // 卡组锁定：已准备就不再接受更换（客户端下拉框已禁用，这里是**权威拒绝**，改包也绕不过）
            if (seat.Ready)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.DeckLocked,
                    "已准备：卡组已锁定，请先取消准备再更换卡组。");
                return;
            }

            bool ok = _deckValidator.Validate(req.DeckJson, req.DeckId, out var deck, out var deckName, out var count, out var err);
            seat.DeckValid = ok;
            if (ok)
            {
                seat.Deck = deck;
                seat.DeckName = string.IsNullOrWhiteSpace(req.DeckName) ? deckName : req.DeckName.Trim();
                seat.DeckCardCount = count;
                seat.DeckError = null;
                Log($"[Lobby] 房间 {room.RoomId} 座位 {seat.Seat} 卡组通过校验：{seat.DeckName}（{count} 张）");
            }
            else
            {
                seat.Deck = null;
                seat.DeckName = null;
                seat.DeckCardCount = 0;
                seat.DeckError = err ?? "卡组不合法";
                // 卡组失效则自动取消准备（否则会出现"已准备但卡组违规"的中间态）
                seat.Ready = false;
                Log($"[Lobby] 房间 {room.RoomId} 座位 {seat.Seat} 卡组违规：{seat.DeckError}");
            }
            room.RefreshLobbyPhase();
            room.LastActiveTick = Now();

            var res = ok ? LobbyResponse.Success(req.RequestId)
                         : LobbyResponse.Fail(req.RequestId, LobbyErrorCode.InvalidDeck, $"卡组违规：{seat.DeckError}");
            res.SessionId = sessionId;
            res.RoomState = room.ToState(IsConnected).ForViewer(seat.Seat);
            Host?.Respond(sessionId, res);
            Broadcast(room);
        }

        // ====================================================================
        // 房间：开始 / 踢人
        // ====================================================================

        private void HandleStartMatch(string sessionId, LobbyRequest req, RoomSession room)
        {
            if (room == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }
            var seat = room.SeatOfSession(sessionId);
            if (seat == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }
            if (seat.Seat != room.HostSeat)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotHost, "只有房主可以开始对局。");
                return;
            }
            if (!room.IsLobbyPhase)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "当前阶段无法开始对局。");
                return;
            }
            if (room.OccupiedCount < 2)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch, "需要两名玩家才能开始。");
                return;
            }

            // D6：开局时**复检**双方卡组；任一方非法 → 不开始，并取消该方准备
            for (int s = 1; s <= 2; s++)
            {
                var st = room.Seat(s);
                if (st.DeckValid && st.Deck != null) continue;
                st.Ready = false;
                st.DeckValid = false;
                if (string.IsNullOrEmpty(st.DeckError)) st.DeckError = "未提交合法卡组";
                room.RefreshLobbyPhase();
                Log($"[Lobby] 房间 {room.RoomId} 开始被拒：座位 {s} 卡组违规（已取消其准备）");
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.InvalidDeck,
                    $"座位 {s} 卡组违规，已取消其准备：{st.DeckError}");
                Broadcast(room);
                return;
            }

            if (_matchFactory == null)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.Internal, "服务端未装配对局工厂。");
                return;
            }

            room.Phase = RoomPhase.Starting;
            room.StartingAtTick = Now();
            Broadcast(room);

            var res = LobbyResponse.Success(req.RequestId);
            res.SessionId = sessionId;
            Host?.Respond(sessionId, res);

            StartMatchInternal(room);
        }

        /// <summary>
        /// 建局（**时序固定**，见 PLAN §12.2）：创建对局宿主 → 绑座（1/2）→ StartServing → 推初始快照 → 进入 InMatch。
        /// 绑座必须先于 StartServing：网关的"座位→会话"解析在建局期即可能被调用（首个决策请求）。
        ///
        /// 【H1 修复】`_matchFactory`、`BindSeat`、`StartServing`、`PushSnapshot` **任一环节抛异常都整段回退**：
        /// 停止已装配的宿主 → 释放半成品装配 → 退回 `Waiting` + 取消双方准备 + 通知双方。
        /// 不这样做的话房间会永久停在 `Starting`（`Tick` 只回收空房与 `Finished`），既占满 `MaxRooms`，又让双方 UI 全灰。
        /// </summary>
        private void StartMatchInternal(RoomSession room)
        {
            IRoomMatchHost match = null;
            string failure = null;
            try
            {
                match = _matchFactory(room);
                if (match != null)
                {
                    room.Match = match;
                    match.OnGameOver = winner => OnMatchGameOver(room, winner);

                    match.BindSeat(1, room.Seat(1).SessionId);
                    match.BindSeat(2, room.Seat(2).SessionId);
                    match.StartServing();
                    match.PushSnapshot();
                }
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                Log($"[Lobby] 房间 {room.RoomId} 建局异常：{failure}");
            }

            if (match == null || failure != null)
            {
                AbortStart(room, failure ?? "服务端未装配对局工厂。");
                return;
            }

            room.Phase = RoomPhase.InMatch;
            room.Summary = null;          // 上一局的结果横幅不带到新一局（M5 允许 Waiting 阶段保留 Summary）
            room.LastActiveTick = Now();
            Log($"[Lobby] 房间 {room.RoomId} 对局开始（座位1={room.Seat(1).PlayerName} / 座位2={room.Seat(2).PlayerName}）");
            Broadcast(room);

            // 【第0回合·调度步骤】广播之后再进第 0 回合：先把阶段置为 Mulligan 并下发一份快照，
            // 客户端据此弹调度框；调度选择**由客户端主动上报**（不做服务端主动下发，避免请求早于订阅而丢失），
            // 双方上报齐了自动结算并开始第 1 回合。
            try
            {
                room.Match?.StartMulligan();
            }
            catch (Exception ex)
            {
                Log($"[Lobby] 房间 {room.RoomId} 进入第0回合调度步骤异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>【H1】建局失败回退：常见于对局装配抛异常。绝不把房间留在 `Starting`。</summary>
        private void AbortStart(RoomSession room, string reason)
        {
            if (room.Match != null)
            {
                try { room.Match.StopServing(); }
                catch (Exception ex) { Log($"[Lobby] 建局回退时 StopServing 异常：{ex.Message}"); }
                room.Match = null;
            }
            room.Phase = RoomPhase.Waiting;
            room.Seat(1).Ready = false;
            room.Seat(2).Ready = false;
            room.LastActiveTick = Now();

            try { OnMatchAssemblyReleased?.Invoke(room); }
            catch (Exception ex) { Log($"[Lobby] 释放半成品对局装配异常：{ex.Message}"); }

            Log($"[Lobby] 房间 {room.RoomId} 建局失败，已退回等待阶段（{reason}）");
            Broadcast(room);
            NoticeRoom(room, LobbyErrorCode.Internal, $"建局失败，已退回准备阶段：{reason}");
        }

        private void OnMatchGameOver(RoomSession room, int winnerSeat)
        {
            if (room.Phase == RoomPhase.Closed) return;
            room.Summary = new MatchSummary
            {
                WinnerSeat = winnerSeat,
                Reason = winnerSeat == 1 || winnerSeat == 2 ? $"座位 {winnerSeat} 获胜" : "对局结束",
            };
            room.Phase = RoomPhase.Finished;
            room.FinishedAtTick = Now();
            Log($"[Lobby] 房间 {room.RoomId} 对局结束：获胜座位 {winnerSeat}" +
                $"（结果展示 {_config.MatchResultDisplaySeconds}s 后自动回到本房间）");
            Broadcast(room);
            // M5：若是"对手掉线/退出"导致只剩一人，房间不能停在 Finished（重开永远等不到对方）
            RecoverIfOpponentGone(room, "对手已离开（掉线或退出）");
        }

        /// <summary>
        /// 【M5】对局结束（或结束后有人离座）且房里只剩一人时，把房间退回 `Waiting`：
        ///   · 否则房间以"1 人 / Finished"的形态卡住 —— 它在大厅列表里长时间不可加入，留下的一方也开不了下一局；
        ///   · `Summary` 清空：结果只在**战场页**展示，房间页不再显示任何结果横幅。
        /// </summary>
        private void RecoverIfOpponentGone(RoomSession room, string reason)
        {
            if (room == null || room.Phase != RoomPhase.Finished) return;
            if (room.OccupiedCount >= 2) return;

            room.Phase = RoomPhase.Waiting;
            room.Summary = null;
            for (int s = 1; s <= 2; s++) room.Seat(s).Ready = false;
            room.LastActiveTick = Now();
            Log($"[Lobby] 房间 {room.RoomId} 只剩 {room.OccupiedCount} 人，退回等待阶段（{reason}）");
            Broadcast(room);
            NoticeRoom(room, LobbyErrorCode.Disconnected, $"{reason}，房间已回到等待状态（可等新玩家加入，或点「退出房间」）。");
        }

        private void HandleKick(string sessionId, LobbyRequest req, RoomSession room)
        {
            if (room == null) { Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotInRoom); return; }
            var seat = room.SeatOfSession(sessionId);
            if (seat == null || seat.Seat != room.HostSeat)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.NotHost, "只有房主可以踢人。");
                return;
            }
            var target = room.Seat(req.TargetSeat);
            if (target == null || !target.Occupied || req.TargetSeat == room.HostSeat)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.InvalidSeat, "目标座位无效或为房主自己。");
                return;
            }
            // 【H2 修复】阶段守卫：`Kick` 原先只校验"我是房主"，对局中踢对手会走 `LeaveRoomInternal` 的
            // InMatch 分支 → `ForfeitSeat(对手)` → 房主凭空获胜（可被主动利用）。对局中一律拒绝，等结束后再踢。
            if (!room.IsLobbyPhase && room.Phase != RoomPhase.Finished)
            {
                Host?.RespondFail(Host?.GetSession(sessionId), req, LobbyErrorCode.PhaseMismatch,
                    "对局进行中不能踢人（请等本局结束）。");
                return;
            }

            string kickedSession = target.SessionId;
            var res = LobbyResponse.Success(req.RequestId);
            res.SessionId = sessionId;
            Host?.Respond(sessionId, res);

            Host?.Notice(kickedSession, LobbyErrorCode.Disconnected, "你已被房主移出房间。", room.RoomId);
            LeaveRoomInternal(room, kickedSession, "被房主移出", notifySelf: false, forceClear: true);
        }

        /// <summary>
        /// 【结果展示结束 → 自动复用房间】关闭本局宿主并回到"同一房间 / 双方未准备"：
        ///   · 房间号、座位、房主、卡组全部保留（与旧"再来一局"一致），房主再点「开始对局」即可开下一局；
        ///   · `Summary` 清空（结果只在战场页展示，房间页不再显示结果横幅）；
        ///   · 由 `Tick` 在 `Finished` 停留超过 `MatchResultDisplaySeconds` 后驱动，**无需任何玩家操作**。
        /// </summary>
        private void ReuseRoomAfterMatch(RoomSession room)
        {
            if (room == null || room.Phase != RoomPhase.Finished) return;

            if (room.Match != null)
            {
                try { room.Match.StopServing(); } catch (Exception ex) { Log($"[Lobby] StopServing 异常：{ex.Message}"); }
                room.Match = null;
            }
            // 释放对局装配（与 AbortStart 一致），避免对局场景/节点残留
            try { OnMatchAssemblyReleased?.Invoke(room); }
            catch (Exception ex) { Log($"[Lobby] 释放对局装配异常：{ex.Message}"); }

            room.Summary = null;
            room.Phase = RoomPhase.Waiting;
            for (int s = 1; s <= 2; s++) room.Seat(s).Ready = false;
            room.LastActiveTick = Now();
            Log($"[Lobby] 房间 {room.RoomId} 结果展示结束：本局已关闭，自动回到本房间（双方未准备，卡组保留）");
            Broadcast(room);
        }

        // ====================================================================
        // 离开 / 销毁 / 超时
        // ====================================================================

        /// <summary>
        /// 离开处理（主动离开、掉线、被踢共用）：
        ///   · 对局中离开 → 按 D5 直接判该方认负（经 `ForfeitSeat`），房间转 `Finished`；
        ///   · 释放座位；房主离开则转移；房间无人则销毁。
        /// </summary>
        private void LeaveRoomInternal(RoomSession room, string sessionId, string reason, bool notifySelf, bool forceClear = false)
        {
            if (room == null || string.IsNullOrEmpty(sessionId)) return;
            var seat = room.SeatOfSession(sessionId);
            _sessionRoom.Remove(sessionId);

            if (seat == null)
            {
                // 会话已不在座位（可能刚刚已被清）——仍需在无人时销毁房间
                if (room.OccupiedCount == 0) CloseRoom(room, reason);
                return;
            }
            int seatNo = seat.Seat;

            if (room.Phase == RoomPhase.InMatch && room.Match != null)
            {
                Log($"[Lobby] 房间 {room.RoomId} 座位 {seatNo} 在对局中离开（{reason}）→ 判该方认负（D5）");
                try { room.Match.ForfeitSeat(seatNo, reason); }
                catch (Exception ex) { Log($"[Lobby] ForfeitSeat 异常：{ex.Message}"); }
                // 对局结束会经 OnMatchGameOver 置 Finished 并广播；此处继续走座位释放
            }
            else if (room.Match != null)
            {
                try { room.Match.UnbindSession(sessionId); } catch { /* 已结束的宿主允许失败 */ }
            }

            // 座位立即释放（座位号不回退：另一名玩家的座位号保持不变）。
            // forceClear 形参保留：Phase D 重连需要区分"暂时保留座位"与"彻底释放"，届时在此分支化。
            _ = forceClear;
            room.ClearSeat(seatNo);
            bool hostChanged = room.TransferHostIfNeeded();
            room.LastActiveTick = Now();
            if (room.IsLobbyPhase) room.RefreshLobbyPhase();
            Log($"[Lobby] 房间 {room.RoomId} 座位 {seatNo} 已释放（{reason}）{(hostChanged ? $"，房主转移至座位 {room.HostSeat}" : "")}");

            if (notifySelf) Host?.Notice(sessionId, LobbyErrorCode.Disconnected, reason, room.RoomId);

            if (room.OccupiedCount == 0)
            {
                CloseRoom(room, "房间无人");
                return;
            }
            // M5：结束后有人离开 → 房间退回等待（否则留下的一方重开永远等不到对方）
            RecoverIfOpponentGone(room, "对手已离开");
            Broadcast(room);
        }

        /// <summary>
        /// 关闭房间（销毁并从注册表移除）。
        /// 【M1 修复】先把 `Phase = Closed` 的**房间状态下发给在座客户端**（客户端据此回大厅并提示），再清座位、
        /// 移出注册表。不能走 `Broadcast`：它对 `Closed` 会提前返回，导致客户端侧 `Closed` 分支成了永远收不到的死代码，
        /// 玩家在"房间超时销毁/服务端停止/被踢空"时只能自己点「退出房间」。
        /// </summary>
        private void CloseRoom(RoomSession room, string reason)
        {
            if (room == null || room.Phase == RoomPhase.Closed) return;
            if (room.Match != null)
            {
                try { room.Match.StopServing(); } catch { }
                room.Match = null;
            }

            room.Phase = RoomPhase.Closed;
            int notified = 0;
            for (int s = 1; s <= 2; s++)
            {
                var seat = room.Seat(s);
                if (!seat.Occupied || !IsConnected(seat.SessionId)) continue;
                // 房间状态（Phase=Closed）先于任何后续状态变化发出，保证客户端看到的是"房间已关闭"
                Host?.SendRoomState(seat.SessionId, s, room.ToState(IsConnected));
                notified++;
            }
            if (notified > 0) Log($"[Lobby] 房间 {room.RoomId} 已向 {notified} 个在座会话下发 Closed 状态（客户端据此回大厅）");

            for (int s = 1; s <= 2; s++)
            {
                if (room.Seat(s).SessionId != null) _sessionRoom.Remove(room.Seat(s).SessionId);
                room.ClearSeat(s);
            }
            _rooms.Remove(room.RoomId);
            Log($"[Lobby] 房间 {room.RoomId} 已关闭（{reason}），当前房间数 {_rooms.Count}");
            OnRoomClosed?.Invoke(room);
        }

        /// <summary>
        /// 每帧驱动（服务端入口调用）：
        ///   · 【结果展示】`Finished` 超过 `MatchResultDisplaySeconds` → 自动复用房间回到等待阶段（无需玩家操作）；
        ///   · 结束后超过 `FinishedRoomTtlSeconds` 无人操作 → 销毁；长时间无人在座的房间（异常情况兜底）→ 销毁；
        ///   · 【H1 兜底】卡在 `Starting` 超过 `StartingGraceMs` 的房间 → 回退 `Waiting`；
        ///   · 【M7】空闲超过 `SessionIdleTimeoutSeconds` 且**不在任何房间**的会话 → 回收连接槽位。
        /// </summary>
        public void Tick(double delta)
        {
            if (delta <= 0) return;
            _idleAccum += delta;
            if (_idleAccum < 1.0) return;    // 每秒检查一次即可
            _idleAccum = 0;

            long now = Now();
            // ① 结果展示到点 → 自动复用房间（先于"超时销毁"，保证房间回到等待阶段而不是被销毁）
            SweepFinishedRooms(now);

            var toClose = new List<RoomSession>();
            foreach (var room in _rooms.All)
            {
                if (room.OccupiedCount == 0) { toClose.Add(room); continue; }
                if (room.Phase == RoomPhase.Finished &&
                    now - room.FinishedAtTick > _config.FinishedRoomTtlSeconds * 1000L)
                    toClose.Add(room);
            }
            foreach (var room in toClose) CloseRoom(room, "超时");

            SweepStartingRooms(now);
            SweepIdleSessions(now);
        }
        private double _idleAccum;

        /// <summary>
        /// 【结果展示 → 自动回房】`Finished` 停留超过 `MatchResultDisplaySeconds`（战场页展示获胜者的时长）后，
        /// 自动关闭本局并复用原房间回到等待阶段；期间无需玩家点击任何按钮。
        /// </summary>
        private void SweepFinishedRooms(long now)
        {
            int secs = _config.MatchResultDisplaySeconds;
            long limit = (secs > 0 ? secs : 0) * 1000L;
            List<RoomSession> done = null;
            foreach (var room in _rooms.All)
            {
                if (room.Phase != RoomPhase.Finished) continue;
                if (now - room.FinishedAtTick <= limit) continue;
                (done ??= new List<RoomSession>()).Add(room);
            }
            if (done == null) return;
            foreach (var room in done) ReuseRoomAfterMatch(room);
        }

        /// <summary>【H1 兜底】`Starting` 停留超时的房间退不回 `InMatch` 也不能永远占位（异常残留的唯一出口）。</summary>
        private void SweepStartingRooms(long now)
        {
            List<RoomSession> stuck = null;
            foreach (var room in _rooms.All)
            {
                if (room.Phase != RoomPhase.Starting) continue;
                if (now - room.StartingAtTick <= StartingGraceMs) continue;
                (stuck ??= new List<RoomSession>()).Add(room);
            }
            if (stuck == null) return;
            foreach (var room in stuck) AbortStart(room, $"建局超时（>{StartingGraceMs / 1000}s 未进入对局）");
        }

        /// <summary>
        /// 【M7】回收空闲连接（`ServerConfig.SessionIdleTimeoutSeconds`，≤0 表示关闭该机制）。
        /// **只回收不在任何房间的会话**：房间内/对局中的玩家"长时间不发消息"是完全正常的（思考、看牌），
        /// 误踢代价远大于多占一个连接槽位；房间自身的清理由 `FinishedRoomTtlSeconds` 与空房规则负责。
        /// </summary>
        private void SweepIdleSessions(long now)
        {
            int limit = _config.SessionIdleTimeoutSeconds;
            if (limit <= 0 || Host == null) return;

            foreach (var sid in new List<string>(Host.SessionIds))
            {
                var info = Host.GetSession(sid);
                if (info == null) continue;
                if (RoomOfSession(sid) != null) continue;                 // 在房里：不回收
                if (now - info.LastMessageTick <= limit * 1000L) continue;
                Log($"[Lobby] 会话 {sid}（{info.PlayerName ?? "未登录"}）空闲超过 {limit}s，回收连接");
                Host.Notice(sid, LobbyErrorCode.Disconnected, "长时间无操作，连接已断开。");
                Host.Disconnect(sid, "idle timeout");
            }
        }

        // ====================================================================
        // 工具
        // ====================================================================

        private RoomSession CurrentRoom(string sessionId)
        {
            string rid = RoomOfSession(sessionId);
            var room = _rooms.Get(rid);
            if (room == null && rid != null) _sessionRoom.Remove(sessionId);   // 表与注册表自愈
            return room;
        }

        private string PlayerName(string sessionId)
            => Host?.GetSession(sessionId)?.PlayerName ?? "玩家";

        private bool IsConnected(string sessionId) => Host?.IsConnected(sessionId) ?? false;

        private static long Now() => Environment.TickCount64;

        /// <summary>向房间内所有在座且已连接的会话广播房间状态（按查看者裁剪，D7）。</summary>
        public void Broadcast(RoomSession room)
        {
            if (room == null || room.Phase == RoomPhase.Closed) return;
            for (int s = 1; s <= 2; s++)
            {
                var seat = room.Seat(s);
                if (!seat.Occupied) continue;
                if (!IsConnected(seat.SessionId)) continue;
                Host?.SendRoomState(seat.SessionId, s, room.ToState(IsConnected));
            }
        }

        private void NoticeRoom(RoomSession room, LobbyErrorCode code, string text)
        {
            for (int s = 1; s <= 2; s++)
            {
                var seat = room.Seat(s);
                if (!seat.Occupied) continue;
                // 【2026-10-17】与 Broadcast / CloseRoom 的在线判断对齐：
                // 原来只判 Occupied，批量断开时会继续向已关闭的 peer 发包（原生 ERROR + 堆栈）。
                if (!IsConnected(seat.SessionId)) continue;
                Host?.Notice(seat.SessionId, code, text, room.RoomId);
            }
        }

        private void Log(string msg) { if (LogHook != null) LogHook(msg); else Console.WriteLine(msg); }
    }
}
