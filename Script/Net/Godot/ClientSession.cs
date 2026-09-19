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
    ///  1. 建立到服务端的 ENet 连接，并在连上后自动发送 `Hello`（携带协议版本与昵称）；
    ///  2. 上行大厅/房间请求（`Send(LobbyRequest)`，自动补 `RequestId`）；
    ///  3. 下行分发：`LobbyResponse` / `RoomState` / `RoomList` / `ServerNotice` 各自事件；
    ///     对局通道消息（`intent_result`/`choice_req`/`SNAP:`）经 `OnMatchEnvelope` 透传（批次 5 由战场视图订阅）；
    ///  4. 维护自身身份：`MySessionId`（Hello 应答回填）与 `MySeat`（RoomState 回填，0=不在房间）。
    ///
    /// 纪律：本类只做"连接 + 分发"，不含任何房间规则；房间规则在服务端（`LobbyServer`，批次 3）。
    /// </summary>
    public class ClientSession : IMatchClientView
    {
        private readonly INetTransport _transport;
        private readonly string _playerName;

        private bool _helloSent;
        private int _requestSeq;
        /// <summary>Hello 请求的 RequestId（用于把"Hello 已应答"与其它应答区分开）。</summary>
        private string _helloRequestId;
        /// <summary>断开事件是否已通知过（幂等：`Poll` 检测与显式 `Disconnect()` 只会通知一次）。</summary>
        private bool _disconnectNotified;

        /// <summary>
        /// 对局视图（投影 + 快照应用 + 决策请求分发）。**懒创建**：首次收到对局消息时建立，
        /// 投影 CardManager 用 `NetClientCardManager`（跨进程从模板重建卡实例）。
        /// 大厅流程与旧的单场 `NetworkMatchLauncher` 复用同一实现（`ClientMatchView`），避免两套投影逻辑分叉。
        /// </summary>
        private ClientMatchView _match;

        /// <summary>客户端对局视图（懒创建；未进入对局前为 null）。</summary>
        public ClientMatchView MatchView
        {
            get
            {
                if (_match == null)
                {
                    _match = new ClientMatchView(new NetClientCardManager(), MySeat);
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
        public bool IsConnected => _transport is GodotEnetTransport enet ? enet.IsClientConnected() : true;

        /// <summary>连接是否已判定失败（不再轮询）。</summary>
        public bool LinkFailed => _transport is GodotEnetTransport enet && enet.LinkFailed;

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

        /// <summary>便捷构造：内部创建 ENet 客户端传输并启动。</summary>
        public static ClientSession Connect(string host, ushort port, string playerName)
        {
            var transport = new GodotEnetTransport(EnetRole.Client)
            {
                Port = port,
                ServerAddress = string.IsNullOrEmpty(host) ? "127.0.0.1" : host,
            };
            var session = new ClientSession(transport, playerName);
            session._transport.Start();
            GD.Print($"[ClientSession] 正在连接服务端 {transport.ServerAddress}:{port}（昵称 {playerName}）");
            return session;
        }

        /// <summary>每帧驱动（握手完成会自动发送 Hello）。</summary>
        public void Poll()
        {
            _transport.Poll();

            // 【H3】断链检测：客户端侧的 ENet 在"连不上/服务端退出/被踢断"时只会把状态置为 Disconnected（传输层
            // 已据此停止轮询），此前**不发任何事件** → 在线页面会永久停在原地空转。这里判定一次并通知上层。
            // 用 `_helloSent && !IsConnected` 兜住"连过又被对端关掉"的情形；`LinkFailed` 兜住"根本没连上"的情形。
            if (LinkFailed || (_helloSent && !IsConnected)) NotifyDisconnected();

            if (_helloSent) return;
            if (!IsConnected) return;
            _helloSent = true;
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

        /// <summary>上行一个大厅/房间请求（自动补 `RequestId`）。</summary>
        public string Send(LobbyRequest req)
        {
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
            var intent = PlayerIntent.Create(MySeat, kind, args);
            _transport.SendToServer(NetEnvelope.Wrap(NetEnvelope.Types.Intent, IntentSerializer.SerializeIntent(intent)));
            return intent.IntentId;
        }

        /// <summary>上行一条决策回包（对局通道，对应服务端挂起的 `ChoiceRequest`）。</summary>
        public void SendChoiceResult(ChoiceResult result)
        {
            if (result == null) return;
            _transport.SendToServer(NetEnvelope.Wrap(NetEnvelope.Types.ChoiceResult, ChoiceSerializer.SerializeResult(result)));
        }

        public void Disconnect()
        {
            _transport.Stop();
            NotifyDisconnected();
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
            if (string.IsNullOrEmpty(message)) return;

            // 对局通道（快照/决策请求/意图回执）：交给 ClientMatchView 做投影与分发（与单场路径同一实现），
            // 同时保留 OnMatchEnvelope 透传（既有订阅者/冒烟工具用）。
            bool isMatch = message.StartsWith(SnapshotSerializer.SnapPrefix, StringComparison.Ordinal);
            if (!isMatch && NetEnvelope.TryUnwrap(message, out var mt, out _))
                isMatch = mt == NetEnvelope.Types.IntentResult || mt == NetEnvelope.Types.ChoiceRequest;

            if (isMatch)
            {
                // 对局消息到达时座位已由 RoomState 确定；此处同步一次，保证决策请求的定向校验正确
                if (_match != null) _match.SetLocalSeat(MySeat);
                MatchView.HandleMessage(message);
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
                    MatchView.HandleMessage(message);
                    OnMatchEnvelope?.Invoke(type, payload);
                    break;
                default:
                    GD.PrintErr($"[ClientSession] 未知信封类型：{type}");
                    break;
            }
        }

        // ==================== IMatchClientView（委托给 ClientMatchView；供 ArenaOl 挂载） ====================

        int IMatchClientView.LocalSeat => MySeat;
        GameSnapshot IMatchClientView.LastSnapshot => MatchView.LastSnapshot;
        ZoneManager IMatchClientView.ClientZoneManager => MatchView.ClientZoneManager;
        CardManager IMatchClientView.ClientCardManager => MatchView.ClientCardManager;
        PlayerManager IMatchClientView.ClientPlayerManager => MatchView.ClientPlayerManager;
        TurnManager IMatchClientView.ClientTurnManager => MatchView.ClientTurnManager;
        StackManager IMatchClientView.ClientStackManager => MatchView.ClientStackManager;
        WorldDeckManager IMatchClientView.ClientWorldDeckManager => MatchView.ClientWorldDeckManager;
        AreaManager IMatchClientView.ClientAreaManager => MatchView.ClientAreaManager;
        DeckManager IMatchClientView.ClientDeckManager => MatchView.ClientDeckManager;
        IReadOnlyList<CostModSnap> IMatchClientView.ClientCostMods => MatchView.ClientCostMods;
        IReadOnlyList<StackEntrySnap> IMatchClientView.ClientStackSnapshot => MatchView.ClientStackSnapshot;
        IReadOnlyList<Script.Game.Entities.CardInstance> IMatchClientView.ClientInvestigationCards => MatchView.ClientInvestigationCards;
        event Action<GameSnapshot> IMatchClientView.OnClientSnapshot
        {
            add => MatchView.OnClientSnapshot += value;
            remove => MatchView.OnClientSnapshot -= value;
        }
        event Action<ChoiceRequest> IMatchClientView.OnClientChoiceRequest
        {
            add => MatchView.OnClientChoiceRequest += value;
            remove => MatchView.OnClientChoiceRequest -= value;
        }
        event Action<IntentResult> IMatchClientView.OnClientIntentRejected
        {
            add => MatchView.OnClientIntentRejected += value;
            remove => MatchView.OnClientIntentRejected -= value;
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
