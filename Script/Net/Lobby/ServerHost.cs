using System;
using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】服务端会话底座：连接 = 会话（SessionId），承载协议分发与底座级校验。
    ///
    /// 职责边界（**与业务无关**，故本类不含任何房间规则）：
    ///  1. 会话表：连接建立即分配会话（会话 id 由传输层给出），断开即回收并通知业务；
    ///  2. 信封分发：`lobby_req` → 底座校验 → `ILobbyHandler`；`intent`/`choice_res` → `IMatchRouter`；其它 → 通知；
    ///  3. 底座级校验：必须先 Hello、重复 Hello 拒绝、协议版本比对（不匹配则通知并断开）、昵称合法性；
    ///  4. **幂等**：按 `RequestId` 去重（重复请求回放缓存应答，绝不重复执行）；
    ///  5. 连接数上限：超限直接通知 + 断开；
    ///  6. 下行通道：`Send` / `Respond` / `Notice` / `Disconnect`（业务实现只经这些方法回包，不直接摸传输层）。
    ///
    /// 纯托管（不依赖 Godot），headless 服务端可用；测试可用 `LoopbackTransport` 直接驱动。
    /// </summary>
    public class ServerHost
    {
        /// <summary>一个会话的底座状态（业务状态如房间/座位由 `ILobbyHandler` 自己维护）。</summary>
        public sealed class SessionInfo
        {
            public string SessionId;
            public string PlayerName;
            public bool Helloed;
            public long ConnectedAtTick;
            public long LastMessageTick;
            /// <summary>
            /// 【幽灵连接·2026-10-20】该会话是否主动发过心跳（`LobbyRequestKind.Ping`）。
            /// **能力协商**：旧版客户端不发心跳，服务端不能拿"无上行"当"已掉线"去踢它（会误伤正常玩家）。
            /// 故"在房会话的超时回收"只对 `HeartbeatSeen` 的会话生效 —— 服务端先升级是安全的。
            /// </summary>
            public bool HeartbeatSeen;
            /// <summary>已处理过的 RequestId → 缓存应答 JSON（用于幂等回放）。</summary>
            public readonly Dictionary<string, string> RecentResponses = new Dictionary<string, string>();
            /// <summary>已受理但尚未应答的 RequestId（防并发重复执行）。</summary>
            public readonly HashSet<string> InFlightRequests = new HashSet<string>();
        }

        private const int MaxCachedResponsesPerSession = 32;

        private readonly INetTransport _transport;
        private readonly ServerConfig _config;
        private readonly ILobbyHandler _lobby;
        private readonly IMatchRouter _matchRouter;

        private readonly Dictionary<string, SessionInfo> _sessions = new Dictionary<string, SessionInfo>();

        /// <summary>下行消息回调（测试可注入，用于观察发出的报文而不依赖传输实现）。null 时用传输层。</summary>
        public Action<string, string> OutboundHook { get; set; }

        /// <summary>日志钩子（默认 Console；Godot 侧可替换为 GD.Print）。</summary>
        public Action<string> LogHook { get; set; }

        public ServerConfig Config => _config;
        public int SessionCount => _sessions.Count;

        public ServerHost(INetTransport transport, ServerConfig config,
            ILobbyHandler lobby, IMatchRouter matchRouter = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _config = config ?? new ServerConfig();
            _lobby = lobby;
            _matchRouter = matchRouter ?? new NoMatchRouter();

            _transport.OnSessionConnected(OnSessionConnected);
            _transport.OnSessionDisconnected(OnSessionDisconnected);
            _transport.OnSessionMessage(OnSessionMessage);
        }

        /// <summary>启动监听（由外层进程入口调用）。</summary>
        public void Start()
        {
            _transport.Start();
            Log($"[ServerHost] 服务端会话底座已启动（端口 {_config.Port}，最大连接 {_config.MaxConnections}，最大房间 {_config.MaxRooms}）");
        }

        /// <summary>每帧驱动传输轮询。</summary>
        public void Poll() => _transport.Poll();

        /// <summary>
        /// 停止监听。【H3 修复】先让业务（`ILobbyShutdown`）关闭全部房间并把 `Closed` 状态下发给在座客户端，
        /// **再**停传输——顺序反了客户端只会看到"连接无声消失"而页面卡住。
        /// </summary>
        public void Stop()
        {
            try { (_lobby as ILobbyShutdown)?.OnServerStopping("服务端已停止"); }
            catch (Exception ex) { LogErr($"[ServerHost] 服务端停止清理异常：{ex.GetType().Name}: {ex.Message}"); }
            FlushOutbound(rounds: 8, sleepMs: 20);   // 给客户端 ~160ms 的窗口收完收尾消息，再真正关闭
            _transport.Stop();
        }

        /// <summary>
        /// 把**已排入发送队列**的下行消息真正发出去。
        /// 为什么必须有它：ENet 的强制断开 / `Close()` 不会替我们发送排队中的数据，直接断开会让刚发出的
        /// "房间已关闭""该房间已满""你已被移出房间"等通知静默丢失，客户端只能看到连接消失（H3/M1/M7 的闭环都靠它）。
        /// </summary>
        public void FlushOutbound(int rounds = 3, int sleepMs = 10)
        {
            if (OutboundHook != null) return;   // 测试注入的同步下行钩子：无需冲刷
            try
            {
                for (int i = 0; i < rounds; i++)
                {
                    _transport.Poll();
                    if (sleepMs > 0) System.Threading.Thread.Sleep(sleepMs);
                }
            }
            catch (Exception ex) { LogErr($"[ServerHost] 冲刷发送队列异常：{ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>会话查询（测试/业务用；返回 null 表示未知会话）。</summary>
        public SessionInfo GetSession(string sessionId)
            => string.IsNullOrEmpty(sessionId) ? null : (_sessions.TryGetValue(sessionId, out var s) ? s : null);

        /// <summary>当前全部会话 id（快照语义由调用方自行复制；M7 空闲回收用）。</summary>
        public IEnumerable<string> SessionIds => _sessions.Keys;

        public bool IsConnected(string sessionId) => _transport.IsSessionConnected(sessionId);

        // ==================== 连接生命周期 ====================

        private void OnSessionConnected(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (_sessions.ContainsKey(sessionId)) return;

            if (_sessions.Count >= _config.MaxConnections)
            {
                Log($"[ServerHost] 拒绝会话 {sessionId}：连接数已达上限 {_config.MaxConnections}");
                Notice(sessionId, LobbyErrorCode.ServerFull, "服务器已满，请稍后再试。");
                Disconnect(sessionId, "server full");
                return;
            }

            _sessions[sessionId] = new SessionInfo
            {
                SessionId = sessionId,
                ConnectedAtTick = Environment.TickCount64,
                LastMessageTick = Environment.TickCount64,
            };
            Log($"[ServerHost] 会话建立 {sessionId}（当前会话数 {_sessions.Count}）");
        }

        private void OnSessionDisconnected(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (!_sessions.Remove(sessionId)) return;
            Log($"[ServerHost] 会话断开 {sessionId}（当前会话数 {_sessions.Count}）");
            try { _lobby?.OnSessionLeft(sessionId); }
            catch (Exception ex) { LogErr($"[ServerHost] OnSessionLeft 异常：{ex.GetType().Name}: {ex.Message}"); }
        }

        // ==================== 上行分发 ====================

        private void OnSessionMessage(string sessionId, string message)
        {
            var session = GetSession(sessionId);
            if (session == null) return;   // 未知会话（可能刚被断开）：静默丢弃
            session.LastMessageTick = Environment.TickCount64;

            if (!NetEnvelope.TryUnwrap(message, out var type, out var payload))
            {
                // 非信封（旧格式）：底座不猜测形状，直接提示（对局通道由批次 5 的匹配器接管信封外消息的情况不存在）
                Notice(sessionId, LobbyErrorCode.UnknownRequest, "无法识别的消息格式（缺少信封类型）。");
                return;
            }

            switch (type)
            {
                case NetEnvelope.Types.LobbyRequest:
                    HandleLobbyRequest(session, payload);
                    break;

                case NetEnvelope.Types.Intent:
                case NetEnvelope.Types.ChoiceResult:
                    if (!(_matchRouter?.TryRoute(sessionId, type, payload) ?? false))
                        Notice(sessionId, LobbyErrorCode.NotInMatch, "当前不在对局中，操作被忽略。");
                    break;

                default:
                    Notice(sessionId, LobbyErrorCode.UnknownRequest, $"无法识别的消息类型：{type}");
                    break;
            }
        }

        /// <summary>大厅/房间请求：底座校验（Hello / 版本 / 幂等）后交业务处理。</summary>
        private void HandleLobbyRequest(SessionInfo session, string payload)
        {
            LobbyRequest req;
            try { req = LobbySerializer.DeserializeRequest(payload); }
            catch (Exception ex)
            {
                LogErr($"[ServerHost] LobbyRequest 解析失败：{ex.Message}");
                Notice(session.SessionId, LobbyErrorCode.UnknownRequest, "请求内容无法解析。");
                return;
            }
            if (req == null)
            {
                Notice(session.SessionId, LobbyErrorCode.UnknownRequest, "空请求。");
                return;
            }

            // 【幽灵连接·2026-10-20】收到心跳 ⇒ 对端进程仍在运行。只做标记，不进幂等流程、不改任何业务状态。
            if (req.Kind == LobbyRequestKind.Ping) session.HeartbeatSeen = true;

            // ---- 幂等：重复 RequestId 一律不重复执行 ----
            if (!string.IsNullOrEmpty(req.RequestId))
            {
                if (session.RecentResponses.TryGetValue(req.RequestId, out var cached))
                {
                    Log($"[ServerHost] 幂等：会话 {session.SessionId} 重复请求 {req.RequestId}，回放缓存应答");
                    if (!string.IsNullOrEmpty(cached)) SendRaw(session.SessionId, cached);
                    return;
                }
                if (!session.InFlightRequests.Add(req.RequestId))
                {
                    Log($"[ServerHost] 幂等：会话 {session.SessionId} 的请求 {req.RequestId} 正在处理中，忽略重复");
                    return;
                }
            }

            // ---- Hello 门禁 ----
            if (req.Kind == LobbyRequestKind.Hello)
            {
                if (session.Helloed)
                {
                    RespondFail(session, req, LobbyErrorCode.AlreadyHelloed);
                    return;
                }
                if (req.ProtocolVersion != _config.ProtocolVersion)
                {
                    LogErr($"[ServerHost] 会话 {session.SessionId} 协议版本不匹配：客户端 {req.ProtocolVersion} ≠ 服务端 {_config.ProtocolVersion}");
                    Notice(session.SessionId, LobbyErrorCode.VersionMismatch,
                        $"客户端协议版本 {req.ProtocolVersion} 与服务端 {_config.ProtocolVersion} 不一致，请更新客户端。");
                    RespondFail(session, req, LobbyErrorCode.VersionMismatch);
                    Disconnect(session.SessionId, "protocol version mismatch");
                    return;
                }
                string name = (req.PlayerName ?? "").Trim();
                if (name.Length == 0 || name.Length > _config.NameMaxLen)
                {
                    RespondFail(session, req, LobbyErrorCode.NameInvalid,
                        $"昵称不合法：长度需在 1–{_config.NameMaxLen} 字符之间。");
                    return;
                }
                session.PlayerName = name;
                session.Helloed = true;
                var res = LobbyResponse.Success(req.RequestId);
                res.SessionId = session.SessionId;
                Log($"[ServerHost] 会话 {session.SessionId} 登录大厅：{name}");
                Respond(session, res);
                return;
            }

            if (!session.Helloed)
            {
                RespondFail(session, req, LobbyErrorCode.NotHelloed);
                return;
            }

            // ---- 交业务（批次 3 的 LobbyServer）----
            if (_lobby == null)
            {
                RespondFail(session, req, LobbyErrorCode.Internal, "服务端未装配大厅业务。");
                return;
            }
            try { _lobby.Handle(session.SessionId, req); }
            catch (Exception ex)
            {
                LogErr($"[ServerHost] LobbyHandler.Handle 异常：{ex.GetType().Name}: {ex.Message}");
                RespondFail(session, req, LobbyErrorCode.Internal);
            }
        }

        // ==================== 下行 ====================

        /// <summary>向会话发送一条已带信封的原始报文。</summary>
        public void SendRaw(string sessionId, string envelopedMessage)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(envelopedMessage)) return;
            // 测试注入的下行钩子必须排在在线判断**之前**：它可能替换掉传输层（回环/替身），
            // 用真实传输的在线状态去过滤会误杀测试消息。
            if (OutboundHook != null) { OutboundHook(sessionId, envelopedMessage); return; }
            // 【纵深防御 · 2026-10-17】所有大厅/房间下行都经过这里。传输层已按 ready_state 预检，
            // 这里再挡一道，任何绕过 LobbyServer 的直发也不会打到已关闭对端。
            if (!_transport.IsSessionConnected(sessionId)) return;
            _transport.SendToSession(sessionId, envelopedMessage);
        }

        /// <summary>按类型向会话下发一条消息（自动打信封）。</summary>
        public void Send(string sessionId, string envelopeType, string payloadJson)
            => SendRaw(sessionId, NetEnvelope.Wrap(envelopeType, payloadJson));

        /// <summary>下发房间状态（**按查看者裁剪后**再发；裁剪是服务端职责，见 `RoomState.ForViewer`）。</summary>
        public void SendRoomState(string sessionId, int viewerSeat, RoomState state)
            => Send(sessionId, NetEnvelope.Types.RoomState, LobbySerializer.Serialize(state?.ForViewer(viewerSeat)));

        /// <summary>下发房间列表。</summary>
        public void SendRoomList(string sessionId, RoomListSnapshot list)
            => Send(sessionId, NetEnvelope.Types.RoomList, LobbySerializer.Serialize(list));

        /// <summary>下发一条服务端通知（非应答类）。</summary>
        public void Notice(string sessionId, LobbyErrorCode code, string text = null, string roomId = null)
            => Send(sessionId, NetEnvelope.Types.ServerNotice, LobbySerializer.Serialize(ServerNotice.Of(code, text, roomId)));

        /// <summary>应答一个请求（业务实现调用；自动打信封并写入幂等缓存）。</summary>
        public void Respond(SessionInfo session, LobbyResponse response)
        {
            if (session == null) return;
            string json = LobbySerializer.Serialize(response);
            if (!string.IsNullOrEmpty(response?.RequestId)) CacheResponse(session, response.RequestId, json);
            Send(session.SessionId, NetEnvelope.Types.LobbyResponse, json);
        }

        /// <summary>按会话 id 应答（业务实现通常只持有 sessionId）。</summary>
        public void Respond(string sessionId, LobbyResponse response) => Respond(GetSession(sessionId), response);

        /// <summary>失败应答的便捷写法。</summary>
        public void RespondFail(SessionInfo session, LobbyRequest req, LobbyErrorCode code, string text = null)
            => Respond(session, LobbyResponse.Fail(req?.RequestId, code, text));

        /// <summary>主动断开某会话（踢人 / 服务器满 / 协议不匹配 / 空闲回收）。</summary>
        public void Disconnect(string sessionId, string reason)
        {
            Log($"[ServerHost] 断开会话 {sessionId}（{reason}）");
            FlushOutbound();   // 先让刚下发的通知真正发出去（强制断开不会替我们发排队中的数据）
            _transport.DisconnectSession(sessionId, reason);
            // 回环/测试传输可能不会回调断开事件，这里兜底回收，保证业务一定能收到 OnSessionLeft
            if (_sessions.ContainsKey(sessionId)) OnSessionDisconnected(sessionId);
        }

        // ==================== 私有工具 ====================

        private void CacheResponse(SessionInfo session, string requestId, string json)
        {
            session.InFlightRequests.Remove(requestId);
            if (session.RecentResponses.Count >= MaxCachedResponsesPerSession)
            {
                // 简易 LRU：清掉最早写入的一批（数量上限很小，直接清空一半即可，避免引入链表）
                var keys = new List<string>(session.RecentResponses.Keys);
                for (int i = 0; i < keys.Count / 2; i++) session.RecentResponses.Remove(keys[i]);
            }
            session.RecentResponses[requestId] = json;
        }

        private void Log(string msg) { if (LogHook != null) LogHook(msg); else Console.WriteLine(msg); }
        private void LogErr(string msg) { if (LogHook != null) LogHook(msg); else Console.Error.WriteLine(msg); }
    }
}
