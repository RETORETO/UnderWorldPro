using System;
using System.Collections.Generic;
using Godot;
using Script.Manager;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】客户端会话：**一条连接贯穿大厅与对局**，按信封类型分发到上层。
    ///
    /// 职责：
    ///  1. 建立到服务端的连接（**统一 WebSocket**，见 `Connect` 的说明），并在连上后自动发送 `Hello`（携带协议版本与昵称）；
    ///  2. 上行大厅/房间请求（`Send(LobbyRequest)`，自动补 `RequestId`）；
    ///  3. 下行分发：`LobbyResponse` / `RoomState` / `RoomList` / `ServerNotice` 各自事件；
    ///     对局通道消息（`intent_result`/`choice_req`/`SNAP:`）经 `OnMatchEnvelope` 透传（批次 5 由战场视图订阅）；
    ///  4. 维护自身身份：`MySessionId`（Hello 应答回填）与 `MySeat`（RoomState 回填，0=不在房间）。
    ///
    /// 纪律：本类只做"连接 + 分发"，不含任何房间规则；房间规则在服务端（`LobbyServer`，批次 3）。
    /// </summary>
    public class ClientSession : IMatchClientView, IDisposable
    {
        private readonly INetTransport _transport;
        private readonly string _playerName;

        private bool _helloSent;
        private int _requestSeq;
        /// <summary>Hello 请求的 RequestId（用于把"Hello 已应答"与其它应答区分开）。</summary>
        private string _helloRequestId;
        /// <summary>断开事件是否已通知过（幂等：`Poll` 检测与显式 `Disconnect()` 只会通知一次）。</summary>
        private bool _disconnectNotified;
        /// <summary>已释放（幂等 + 阻止释放后再懒创建对局视图）。</summary>
        private bool _disposed;

        /// <summary>
        /// 【幽灵连接·2026-10-20】应用层心跳间隔（毫秒）。
        ///
        /// **它不代表"思考超时"**：桌游一回合想几分钟很正常，服务端只要每 15s 收到这样一个几十字节的
        /// `Ping`，就会刷新该会话的 `LastMessageTick`，永不判掉线。玩家感知不到任何东西。
        /// 只有**进程被杀 / 断网 / 休眠**导致心跳彻底停止时，服务端才会在阈值（默认 5 分钟起）后释放座位。
        ///
        /// 为什么不用 WebSocket 原生 ping：原生 pong 由底层库自动回应，业务层拿不到"对端是否还活着"的结论；
        /// 而 TCP 半开时内核要十几分钟才报错（`tcp_retries2`），远慢于桌游需要的回收速度。
        /// </summary>
        public const int HeartbeatIntervalMs = 15000;

        /// <summary>上次发心跳的时刻（`System.Environment.TickCount64`）。Hello 发出时初始化，避免连上就补发一条。</summary>
        private long _lastHeartbeatTick;

        /// <summary>最近一条心跳的 RequestId：其应答在分发处被吞掉，业务层（大厅页/BOT/测试）永远看不到。</summary>
        private string _heartbeatRequestId;

        /// <summary>
        /// 对局视图（投影 + 快照应用 + 决策请求分发）。**懒创建**：首次收到对局消息时建立，
        /// 投影 CardManager 用 `NetClientCardManager`（跨进程从模板重建卡实例）。
        /// 大厅流程与旧的单场 `NetworkMatchLauncher` 复用同一实现（`ClientMatchView`），避免两套投影逻辑分叉。
        /// </summary>
        private ClientMatchView _match;

        /// <summary>
        /// 客户端对局视图（懒创建；未进入对局前为 null）。
        /// 【2026-10-17】**`Dispose()` 之后恒返回 null**：否则释放后再被访问会重新 lazy 创建一个新视图，
        /// 又泄漏 9 个 Node（正是要修的问题）。所有调用方必须按可空处理。
        /// </summary>
        public ClientMatchView MatchView
        {
            get
            {
                if (_disposed) return null;
                if (_match == null)
                {
                    // ownsCardManager: true —— 这个 NetClientCardManager 由本会话创建，Dispose 时一并释放。
                    _match = new ClientMatchView(new NetClientCardManager(), MySeat, ownsCardManager: true);
                    _match.BindSender((kind, args) => SendMatchIntent(kind, args), result => SendChoiceResult(result));
                    GD.Print($"[ClientSession] 已建立客户端对局视图（座位 {MySeat}）");
                }
                return _match;
            }
        }

        /// <summary>服务端分配的会话 id（Hello 应答回填；未登录前为空）。</summary>
        public string MySessionId { get; private set; }

        /// <summary>
        /// 大厅登录（Hello）是否已成功应答。
        /// **会话级**状态：Hello 每条连接只发一次，因此页面级缓存（"本页是否已登录"）在页面重建后必须从这里恢复，
        /// 否则"从房间页返回大厅"的刷新/建房按钮会永久禁用（H4 修复的正是这一点）。
        /// </summary>
        public bool IsHelloAcked { get; private set; }

        /// <summary>本客户端在房间内的座位（1/2；0=尚未进入房间）。由服务端 `RoomState.ViewerSeat` 回填。</summary>
        public int MySeat { get; private set; }

        /// <summary>最近一次收到的房间状态（按查看者裁剪后的版本）。</summary>
        public RoomState CurrentRoom { get; private set; }

        /// <summary>是否已连上服务端。</summary>
        public bool IsConnected => _transport switch
        {
            GodotEnetTransport enet => enet.IsClientConnected(),
            GodotWebSocketTransport ws => ws.IsClientConnected(),
            _ => true,
        };

        /// <summary>连接是否已判定失败（不再轮询）。</summary>
        public bool LinkFailed =>
            (_transport as GodotEnetTransport)?.LinkFailed == true
            || (_transport as GodotWebSocketTransport)?.LinkFailed == true;

        /// <summary>
        /// 【2026-10-13】本会话因"当前平台不支持所选传输"而根本没有建立连接。
        /// 供 UI 立刻给出可读提示，而不是显示"正在连接…"永远转圈。
        /// </summary>
        public bool UnsupportedOnWeb =>
            (_transport as GodotEnetTransport)?.UnsupportedOnWeb == true
            || (_transport as GodotWebSocketTransport)?.UnsupportedOnWeb == true;

        /// <summary>本会话使用的传输中文名（诊断/日志用）。</summary>
        public string TransportName => _transport switch
        {
            GodotEnetTransport => "ENet(UDP)",
            GodotWebSocketTransport => "WebSocket(TCP)",
            _ => "其它",
        };

        // ==================== 事件 ====================
        /// <summary>已连上服务端（可开始登录大厅）。</summary>
        public event Action OnConnected;
        /// <summary>与服务端断开（含连接失败）。</summary>
        public event Action OnDisconnected;
        /// <summary>大厅/房间请求的应答。</summary>
        public event Action<LobbyResponse> OnLobbyResponse;
        /// <summary>房间状态（全量、已按查看者裁剪）。</summary>
        public event Action<RoomState> OnRoomState;
        /// <summary>房间列表。</summary>
        public event Action<RoomListSnapshot> OnRoomList;
        /// <summary>服务端主动通知（被踢、服务器停止、请求被拒等）。</summary>
        public event Action<ServerNotice> OnNotice;
        /// <summary>
        /// 对局通道消息透传：type ∈ {`intent_result`,`choice_req`,`snap`}，payload 为信封内 JSON（`snap` 为快照 JSON）。
        /// 供战场视图（批次 5 的 `ArenaOl`）订阅；大厅阶段不会触发。
        /// </summary>
        public event Action<string, string> OnMatchEnvelope;

        public ClientSession(INetTransport transport, string playerName)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _playerName = playerName ?? "";
            _transport.OnSessionMessage(OnMessage);
        }

        /// <summary>
        /// 便捷构造：内部创建传输并启动。
        ///
        /// 【传输层统一 · 2026-10-14】**桌面与 Web 一律走 WebSocket（TCP）**，不再按平台分叉。
        /// 为什么把桌面也统一过来：
        ///   ① 浏览器只能 WebSocket，统一后"桌面与 Web 在同一房间对战"不需要任何开关或双监听；
        ///   ② 服务端也因此可以是**桌面玩家点「建立主机」开出来的 WebSocket 监听**，Web 玩家能直接连进来；
        ///   ③ 两端同一条链路，出问题只需排查一种日志。
        /// `GodotEnetTransport` 实现仍保留（回环/历史测试用），但**不在联机主链路上**。
        /// </summary>
        public static ClientSession Connect(string host, ushort port, string playerName)
        {
            string target = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            var transport = new GodotWebSocketTransport(EnetRole.Client)
            {
                Port = port,
                ServerAddress = target,
                Url = GodotWebSocketTransport.BuildUrl(target, port),
            };

            var session = new ClientSession(transport, playerName);
            session._transport.Start();
            GD.Print($"[ClientSession] 正在用 {session.TransportName} 连接服务端 {target}:{port}（昵称 {playerName}）");
            return session;
        }

        /// <summary>每帧驱动（握手完成会自动发送 Hello）。</summary>
        public void Poll()
        {
            if (_disposed) return;
            _transport.Poll();

            // 【H3】断链检测：客户端侧的 ENet 在"连不上/服务端退出/被踢断"时只会把状态置为 Disconnected（传输层
            // 已据此停止轮询），此前**不发任何事件** → 在线页面会永久停在原地空转。这里判定一次并通知上层。
            // 用 `_helloSent && !IsConnected` 兜住"连过又被对端关掉"的情形；`LinkFailed` 兜住"根本没连上"的情形。
            if (LinkFailed || (_helloSent && !IsConnected)) NotifyDisconnected();

            if (_helloSent)
            {
                // 【幽灵连接·2026-10-20】已登录后每 15s 发一次心跳。放在这里而不是各页面里，
                // 是为了让大厅页 / 房间页 / 战场页三个驱动点（都只是调 `Poll()`）自动都带心跳。
                if (IsConnected) PumpHeartbeat();
                return;
            }
            if (!IsConnected) return;
            _helloSent = true;
            _lastHeartbeatTick = System.Environment.TickCount64;
            OnConnected?.Invoke();
            var hello = new LobbyRequest
            {
                Kind = LobbyRequestKind.Hello,
                ProtocolVersion = LobbyProtocol.ProtocolVersion,
                PlayerName = _playerName,
            };
            _helloRequestId = Send(hello);
            GD.Print($"[ClientSession] 已连接，已发送 Hello（协议版本 {LobbyProtocol.ProtocolVersion}）");
        }

        /// <summary>
        /// 【幽灵连接·2026-10-20】到点发一条 `LobbyRequest{Ping}`。
        ///
        /// 服务端 `LobbyServer.Handle` 的 `Ping` 分支只回一条空应答（不做任何状态变更），
        /// 但 `ServerHost.OnSessionMessage` 会因此刷新 `LastMessageTick` —— 这就是服务端判定
        /// "这条连接还活着"的唯一依据。旧版本客户端不发心跳，服务端因此**不会**对"在房会话"做超时回收
        /// （见 `LobbyServer.SweepIdleSessions` 的 `HeartbeatSeen` 守卫），所以服务端先升级也不会误踢旧客户端。
        /// </summary>
        private void PumpHeartbeat()
        {
            long now = System.Environment.TickCount64;
            if (now - _lastHeartbeatTick < HeartbeatIntervalMs) return;
            _lastHeartbeatTick = now;
            _heartbeatRequestId = Send(new LobbyRequest { Kind = LobbyRequestKind.Ping });
        }

        /// <summary>上行一个大厅/房间请求（自动补 `RequestId`）。</summary>
        public string Send(LobbyRequest req)
        {
            if (_disposed) return null;
            if (req == null) return null;
            if (string.IsNullOrEmpty(req.RequestId)) req.RequestId = $"c{++_requestSeq}";
            _transport.SendToServer(NetEnvelope.Wrap(NetEnvelope.Types.LobbyRequest, LobbySerializer.Serialize(req)));
            return req.RequestId;
        }

        /// <summary>
        /// 上行一条对局意图（对局通道）。`kind` 与参数同 `PlayerIntent`；
        /// 身份用本会话在房间内的座位（由 `RoomState.ViewerSeat` 回填；未进房则为 0，服务端会判越权）。
        /// </summary>
        public string SendMatchIntent(PlayerIntent.Kind kind, params string[] args)
        {
            if (_disposed) return null;
            var intent = PlayerIntent.Create(MySeat, kind, args);
            _transport.SendToServer(NetEnvelope.Wrap(NetEnvelope.Types.Intent, IntentSerializer.SerializeIntent(intent)));
            return intent.IntentId;
        }

        /// <summary>上行一条决策回包（对局通道，对应服务端挂起的 `ChoiceRequest`）。</summary>
        public void SendChoiceResult(ChoiceResult result)
        {
            if (_disposed) return;
            if (result == null) return;
            _transport.SendToServer(NetEnvelope.Wrap(NetEnvelope.Types.ChoiceResult, ChoiceSerializer.SerializeResult(result)));
        }

        public void Disconnect()
        {
            if (_disposed) return;
            StopAndNotify();
        }

        /// <summary>停链 + 通知（`Disconnect` 与 `Dispose` 共用）。</summary>
        private void StopAndNotify()
        {
            try { _transport.Stop(); } catch { /* 已停止则忽略 */ }
            NotifyDisconnected();
        }

        /// <summary>
        /// 释放会话：停链 → 通知 UI → 退订传输回调 → 释放客户端对局视图（9 个 Node）。
        ///
        /// 【2026-10-17】原 `Disconnect()` 只停链、不释放 `_match` → 每个会话泄漏 9 个 ObjectDB 实例
        /// （压测 `--load 8` 的 16 会话 = 144，即退出时那条 leak 警告的来源）。
        ///
        /// 顺序说明：
        ///  1) **先置 `_disposed`**：`NotifyDisconnected` 可能让 UI 回调 `OnlineClientContext.Disconnect()`，
        ///     提前置位可让重入变成 no-op，避免二次释放；
        ///  2) 先 `StopAndNotify` 再释放视图：UI 在 `OnDisconnected` 里仍可读会话级数据（`CurrentRoom` 等，不碰 Manager）；
        ///  3) 释放后 `_match = null` —— getter 有 `_disposed` 保护，不会再重建。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopAndNotify();

            // 退订传输回调（`OnSessionMessage` 是单槽 setter，置 null 不会误删别人的订阅）。
            try { _transport.OnSessionMessage(null); } catch { /* 忽略 */ }

            try { _match?.Dispose(); } catch { /* 忽略 */ }
            _match = null;

            OnConnected = null;
            OnDisconnected = null;
            OnLobbyResponse = null;
            OnRoomState = null;
            OnRoomList = null;
            OnNotice = null;
            OnMatchEnvelope = null;
        }

        /// <summary>通知"已断开"（幂等；`Poll` 的链路检测与显式 `Disconnect()` 共用）。</summary>
        private void NotifyDisconnected()
        {
            if (_disconnectNotified) return;
            _disconnectNotified = true;
            GD.Print("[ClientSession] 与服务端的连接已断开（连接失败 / 被服务端关闭 / 主动断开）");
            OnDisconnected?.Invoke();
        }

        // ==================== 下行分发 ====================

        private void OnMessage(string _, string message)
        {
            if (_disposed || string.IsNullOrEmpty(message)) return;

            // 对局通道（快照/决策请求/意图回执）：交给 ClientMatchView 做投影与分发（与单场路径同一实现），
            // 同时保留 OnMatchEnvelope 透传（既有订阅者/冒烟工具用）。
            bool isMatch = message.StartsWith(SnapshotSerializer.SnapPrefix, StringComparison.Ordinal);
            if (!isMatch && NetEnvelope.TryUnwrap(message, out var mt, out _))
                isMatch = mt == NetEnvelope.Types.IntentResult || mt == NetEnvelope.Types.ChoiceRequest;

            if (isMatch)
            {
                // 对局消息到达时座位已由 RoomState 确定；此处同步一次，保证决策请求的定向校验正确
                if (_match != null) _match.SetLocalSeat(MySeat);
                MatchView?.HandleMessage(message);
                if (message.StartsWith(SnapshotSerializer.SnapPrefix, StringComparison.Ordinal))
                    OnMatchEnvelope?.Invoke("snap", message.Substring(SnapshotSerializer.SnapPrefix.Length));
                return;
            }

            if (!NetEnvelope.TryUnwrap(message, out var type, out var payload))
            {
                GD.PrintErr($"[ClientSession] 收到非信封消息，无法分发：{message.Substring(0, Math.Min(60, message.Length))}…");
                return;
            }

            switch (type)
            {
                case NetEnvelope.Types.LobbyResponse:
                    {
                        var res = LobbySerializer.DeserializeResponse(payload);
                        if (res == null) break;
                        if (!string.IsNullOrEmpty(res.SessionId)) MySessionId = res.SessionId;
                        // Hello 应答即"已登录大厅"（会话级事实）；只认 Hello 那一条的 RequestId，避免误判
                        if (!IsHelloAcked && res.Ok && !string.IsNullOrEmpty(res.SessionId)
                            && (string.IsNullOrEmpty(_helloRequestId) || res.RequestId == _helloRequestId))
                            IsHelloAcked = true;
                        // 心跳应答：只用于刷新服务端的活跃时间，对业务层**完全不可见**。
                        // 若上抛，"大厅页"会把它当成一次成功的空操作（无害但噪声），
                        // 而 "BOT 的连续失败计数" 会被它无条件清零（`NetBotClient.OnResponse` 的成功分支）。
                        if (!string.IsNullOrEmpty(_heartbeatRequestId) && res.RequestId == _heartbeatRequestId) break;
                        OnLobbyResponse?.Invoke(res);
                        break;
                    }
                case NetEnvelope.Types.RoomState:
                    {
                        var state = LobbySerializer.DeserializeRoomState(payload);
                        if (state == null) break;
                        CurrentRoom = state;
                        MySeat = state.ViewerSeat;
                        OnRoomState?.Invoke(state);
                        break;
                    }
                case NetEnvelope.Types.RoomList:
                    {
                        var list = LobbySerializer.DeserializeRoomList(payload);
                        if (list != null) OnRoomList?.Invoke(list);
                        break;
                    }
                case NetEnvelope.Types.ServerNotice:
                    {
                        var notice = LobbySerializer.DeserializeNotice(payload);
                        if (notice != null) OnNotice?.Invoke(notice);
                        break;
                    }
                case NetEnvelope.Types.IntentResult:
                case NetEnvelope.Types.ChoiceRequest:
                    // 已在上面按"对局通道"提前处理（交 ClientMatchView）；此分支不应到达，保留以防未来新增类型漏接线
                    MatchView?.HandleMessage(message);
                    OnMatchEnvelope?.Invoke(type, payload);
                    break;
                default:
                    GD.PrintErr($"[ClientSession] 未知信封类型：{type}");
                    break;
            }
        }

        // ==================== IMatchClientView（委托给 ClientMatchView；供 ArenaOl 挂载） ====================

        // 【2026-10-17】以下全部走 `MatchView?.`：`Dispose()` 之后 getter 恒为 null，
        // 这些成员必须能容忍 null（否则断线收尾时 UI 的最后一帧读取会 NRE）。
        int IMatchClientView.LocalSeat => MySeat;
        GameSnapshot IMatchClientView.LastSnapshot => MatchView?.LastSnapshot;
        ZoneManager IMatchClientView.ClientZoneManager => MatchView?.ClientZoneManager;
        CardManager IMatchClientView.ClientCardManager => MatchView?.ClientCardManager;
        PlayerManager IMatchClientView.ClientPlayerManager => MatchView?.ClientPlayerManager;
        TurnManager IMatchClientView.ClientTurnManager => MatchView?.ClientTurnManager;
        StackManager IMatchClientView.ClientStackManager => MatchView?.ClientStackManager;
        WorldDeckManager IMatchClientView.ClientWorldDeckManager => MatchView?.ClientWorldDeckManager;
        AreaManager IMatchClientView.ClientAreaManager => MatchView?.ClientAreaManager;
        DeckManager IMatchClientView.ClientDeckManager => MatchView?.ClientDeckManager;
        IReadOnlyList<CostModSnap> IMatchClientView.ClientCostMods => MatchView?.ClientCostMods;
        IReadOnlyList<StackEntrySnap> IMatchClientView.ClientStackSnapshot => MatchView?.ClientStackSnapshot;
        IReadOnlyList<Script.Game.Entities.CardInstance> IMatchClientView.ClientInvestigationCards => MatchView?.ClientInvestigationCards;
        // 事件订阅同样必须空安全：`add` 里若直接访问 `MatchView` 属性，Dispose 后（getter 返回 null）
        // 会立刻 NRE；而在 `_disposed` 之前访问又会触发懒创建。故先取局部再判空。
        event Action<GameSnapshot> IMatchClientView.OnClientSnapshot
        {
            add { var v = MatchView; if (v != null) v.OnClientSnapshot += value; }
            remove { var v = MatchView; if (v != null) v.OnClientSnapshot -= value; }
        }
        event Action<ChoiceRequest> IMatchClientView.OnClientChoiceRequest
        {
            add { var v = MatchView; if (v != null) v.OnClientChoiceRequest += value; }
            remove { var v = MatchView; if (v != null) v.OnClientChoiceRequest -= value; }
        }
        event Action<IntentResult> IMatchClientView.OnClientIntentRejected
        {
            add { var v = MatchView; if (v != null) v.OnClientIntentRejected += value; }
            remove { var v = MatchView; if (v != null) v.OnClientIntentRejected -= value; }
        }

        /// <summary>上行意图（接口签名带 seat 以兼容旧单场路径；本会话以自己的座位为准，忽略该参数）。</summary>
        string IMatchClientView.SendIntentAs(int seat, PlayerIntent.Kind kind, params string[] args)
        {
            if (seat != 0 && MySeat != 0 && seat != MySeat)
                GD.PrintErr($"[ClientSession] SendIntentAs 收到座位 {seat}，但本会话座位为 {MySeat}；以本会话座位为准");
            return SendMatchIntent(kind, args);
        }
        void IMatchClientView.SendChoiceResult(ChoiceResult result) => SendChoiceResult(result);

        /// <summary>快照前缀（唯一真相源见 `SnapshotSerializer.SnapPrefix`；对局通道，不经信封）。</summary>
        public const string SnapPrefix = SnapshotSerializer.SnapPrefix;
    }
}
