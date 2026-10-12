using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【Web 联机 · 2026-10-13】基于 Godot WebSocket 的传输实现（INetTransport），**浏览器可用的那一套**。
    ///
    /// 为什么必须另起一套（而不是继续用 ENet）：
    ///   浏览器的安全沙箱**不允许网页打开原始 UDP/TCP socket**。Godot 的 `ENetMultiplayerPeer` 建在 UDP 上，
    ///   所以在 Web 导出里 `CreateServer` / `CreateClient` 都不可能成立（实测直接让 wasm abort，页面冻死）。
    ///   浏览器唯一能做的双向长连接是 **WebSocket**（`ws://` / `wss://`），因此 Web 客户端必须走本传输。
    ///
    /// 与 <see cref="GodotEnetTransport"/> 的关系：
    ///   两者都实现 `INetTransport`，**上层（`LobbyServer`/`ServerHost`/`ClientSession`/`MatchHost`）一行都不用改** ——
    ///   协议是文本信封（`V2:&lt;type&gt;|&lt;json&gt;`），与传输无关。本类基本是 ENet 版的"换 peer 类型"版：
    ///   `ENetMultiplayerPeer` → `WebSocketMultiplayerPeer`，`CreateServer(port,maxClients)` → `CreateServer(port, "*", null)`，
    ///   `CreateClient(addr,port)` → `CreateClient("ws://addr:port", null)`。
    ///   关键语义已核对一致：`GetPacketPeer()` 取的是"**队列头部**那条包的来源"，
    ///   因此与 ENet 一样**必须先 `GetPacketPeer()` 再 `GetPacket()`**（见 DispatchAvailablePackets 的注释）。
    ///
    /// 角色限制：
    ///   · **客户端（浏览器 / 桌面都行）**：`ws://<host>:<port>` 连到 WebSocket 服务端；
    ///   · **服务端**：只有桌面/专用服务器能监听（浏览器永远不能当服务端）。
    ///     在 Web 上请求服务端角色会被 <see cref="UnsupportedOnWeb"/> 挡下并给出可读原因，不再 abort。
    ///
    /// 与 ENet 并存的部署方式（端口号可以相同，因为一个是 UDP、一个是 TCP）：
    ///   · 桌面客户端：`NetServer.tscn -- --port 18950`（ENet/UDP）
    ///   · 浏览器客户端：`NetServer.tscn -- --ws --port 18950`（WebSocket/TCP）
    /// </summary>
    public class GodotWebSocketTransport : INetTransport
    {
        private readonly EnetRole _role;
        private WebSocketMultiplayerPeer _peer;
        private readonly Encoding _utf8 = new UTF8Encoding(false);

        private Action<string, string> _sessionMessageHandler;
        private Action<string> _sessionConnectedHandler;
        private Action<string> _sessionDisconnectedHandler;

        private bool _running;
        /// <summary>客户端：是否曾观测到"连接中/已连接"（用于区分"刚创建尚未就绪"与"连接确实失败"）。</summary>
        private bool _linkWasUp;
        /// <summary>客户端：连接是否已判定失败（Poll 轮询已因此停止）。</summary>
        public bool LinkFailed { get; private set; }
        /// <summary>本传输因"Web 平台限制"而未能启动（服务端角色在浏览器上）。</summary>
        public bool UnsupportedOnWeb { get; private set; }

        // 服务端：WS peer id(long) ↔ 会话 id(string)
        private readonly Dictionary<long, string> _peerToSession = new Dictionary<long, string>();
        private readonly Dictionary<string, long> _sessionToPeer = new Dictionary<string, long>();
        private int _nextSessionSeq;

        /// <summary>客户端收到的下行消息所用的会话 id 占位（客户端不需要寻址；真实会话 id 见 Hello 应答）。</summary>
        public const string SelfSessionId = "self";

        // —— 配置属性（供 INetTransport.Start() 无参启动读取）——
        /// <summary>监听端口（服务端角色）。</summary>
        public ushort Port { get; set; } = 18950;
        /// <summary>服务端地址（客户端角色；仅用于日志与拼 URL）。</summary>
        public string ServerAddress { get; set; } = "127.0.0.1";
        /// <summary>客户端完整 URL（留空则按 `ws://<see cref="ServerAddress"/>:<see cref="Port"/>` 拼）。</summary>
        public string Url { get; set; }

        /// <summary>当前平台是否是 Web 导出。</summary>
        public static bool IsWeb => OS.HasFeature("web");

        /// <summary>由 host/port 拼出 `ws://host:port`（host 里已带 scheme 时原样返回）。</summary>
        public static string BuildUrl(string host, ushort port)
        {
            if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
            if (host.StartsWith("ws://", StringComparison.Ordinal) || host.StartsWith("wss://", StringComparison.Ordinal))
                return host;
            return "ws://" + host + ":" + port;
        }

        /// <summary>懒创建（与服务端同款：Web 上若被拦下就不必真的造出 peer）。</summary>
        private WebSocketMultiplayerPeer Peer => _peer ??= new WebSocketMultiplayerPeer();

        public GodotWebSocketTransport(EnetRole role)
        {
            _role = role;
        }

        public void Start()
        {
            if (_running) return;

            // 【硬保险】浏览器不能监听端口 → 服务端角色在 Web 上永不成立。
            if (_role == EnetRole.Server && IsWeb)
            {
                UnsupportedOnWeb = true;
                GD.PrintErr("[GodotWebSocketTransport] 浏览器不允许网页监听端口，Web 版无法充当服务端。");
                return;
            }

            Error err;
            if (_role == EnetRole.Server)
            {
                // 绑到所有网卡（"*"），对手从局域网连进来；TLS 不启用（浏览器页面走 http，ws:// 即可）。
                GD.Print($"[WS] w1 即将 CreateServer port={Port}");
                err = Peer.CreateServer(Port, "*", null);
                GD.Print($"[WS] w2 CreateServer 已返回 err={err}");
                if (err != Error.Ok)
                    throw new InvalidOperationException($"[GodotWebSocketTransport] CreateServer failed: {err}");
                Peer.PeerConnected += OnPeerConnected;
                Peer.PeerDisconnected += OnPeerDisconnected;
            }
            else
            {
                string url = string.IsNullOrEmpty(Url) ? BuildUrl(ServerAddress, Port) : Url;
                Url = url;
                GD.Print($"[WS] w1 即将 CreateClient url={url}");
                err = Peer.CreateClient(url, null);
                GD.Print($"[WS] w2 CreateClient 已返回 err={err}");
                if (err != Error.Ok)
                    throw new InvalidOperationException($"[GodotWebSocketTransport] CreateClient failed: {err}");
            }
            _running = true;
            GD.Print("[WS] w3 传输已启动");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_role == EnetRole.Server)
            {
                Peer.PeerConnected -= OnPeerConnected;
                Peer.PeerDisconnected -= OnPeerDisconnected;
            }
            if (_peer != null) _peer.Close();
            _peerToSession.Clear();
            _sessionToPeer.Clear();
            _nextSessionSeq = 0;
        }

        // ==================== 发送 ====================

        public void SendToSession(string sessionId, string message)
        {
            if (!_running) return;
            if (_role != EnetRole.Server)
            {
                GD.PrintErr("[GodotWebSocketTransport] SendToSession 仅服务端可用（客户端请用 SendToServer）");
                return;
            }
            if (string.IsNullOrEmpty(sessionId)) return;
            if (!_sessionToPeer.TryGetValue(sessionId, out long wsPeerId)) return;   // 会话不存在：静默忽略
            Peer.SetTargetPeer((int)wsPeerId);
            Peer.PutPacket(_utf8.GetBytes(message));
        }

        public void SendToServer(string message)
        {
            if (!_running) return;
            if (_role != EnetRole.Client)
            {
                GD.PrintErr("[GodotWebSocketTransport] SendToServer 仅客户端可用（服务端请用 SendToSession）");
                return;
            }
            if (Peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
            {
                GD.PrintErr("[GodotWebSocketTransport] SendToServer 被忽略：客户端尚未连上服务端");
                return;
            }
            Peer.PutPacket(_utf8.GetBytes(message));
        }

        // ==================== 订阅 ====================

        public void OnSessionMessage(Action<string, string> handler) => _sessionMessageHandler = handler;
        public void OnSessionConnected(Action<string> handler) => _sessionConnectedHandler = handler;
        public void OnSessionDisconnected(Action<string> handler) => _sessionDisconnectedHandler = handler;

        // ==================== 轮询 ====================

        /// <summary>
        /// 由 Godot 节点的 _Process 每帧调用（Web 上由大厅页/房间页/战场页的 `_Process` 驱动）。
        /// 顺序与 ENet 版一致：先 Poll + 收包，**再**判定"链路是否已断"，
        /// 否则服务端停止前下发的收尾消息（"房间已关闭"）会被丢掉。
        /// </summary>
        public void Poll()
        {
            if (!_running) return;

            Peer.Poll();
            DispatchAvailablePackets();

            if (_role == EnetRole.Client)
            {
                var status = Peer.GetConnectionStatus();
                if (status != MultiplayerPeer.ConnectionStatus.Disconnected) _linkWasUp = true;
                else if (_linkWasUp)
                {
                    _running = false;
                    LinkFailed = true;
                    GD.PrintErr($"[GodotWebSocketTransport] 连接 {Url} 失败（状态=Disconnected），已停止轮询。"
                        + "请确认：① WebSocket 服务端已启动（`NetServer.tscn -- --ws --port <端口>`）；"
                        + "② 地址端口填对（浏览器需用**服务端机器的局域网 IPv4**，不能是 127.0.0.1 除非同机）；"
                        + "③ 页面若是 https 打开，则必须用 wss://（混合内容会被浏览器拦掉）。");
                }
            }
        }

        /// <summary>
        /// 把队列里已到达的数据包全部分发出去（服务端按 peer 查会话，客户端用占位会话 id）。
        ///
        /// 【顺序很重要】`GetPacketPeer()` 返回的是**队列头部**那条包的来源（Godot 源码：
        /// `return _incoming_packets.front()->get().source;`），所以必须先取来源、再 `GetPacket()` 消费。
        /// 与 ENet 版是同一条规矩（只是原因不同：ENet 是消费后来源被重置）。
        /// </summary>
        private void DispatchAvailablePackets()
        {
            while (Peer.GetAvailablePacketCount() > 0)
            {
                long from = Peer.GetPacketPeer();
                byte[] bytes = Peer.GetPacket();
                string msg = _utf8.GetString(bytes);
                if (_role == EnetRole.Server)
                {
                    if (!_peerToSession.TryGetValue(from, out var sessionId)) continue;   // 未知来源，丢弃
                    _sessionMessageHandler?.Invoke(sessionId, msg);
                }
                else
                {
                    _sessionMessageHandler?.Invoke(SelfSessionId, msg);
                }
            }
        }

        // ==================== 查询 ====================

        /// <summary>服务端：当前已连入的会话 id 列表。</summary>
        public IReadOnlyCollection<string> ConnectedSessionIds => _sessionToPeer.Keys;

        /// <summary>服务端：该会话是否仍连接着。</summary>
        public bool IsSessionConnected(string sessionId)
            => !string.IsNullOrEmpty(sessionId) && _sessionToPeer.ContainsKey(sessionId);

        public int ConnectedSessionCount => _sessionToPeer.Count;

        /// <summary>服务端主动断开某会话（优雅断开：先把它已排队的数据发完）。</summary>
        public void DisconnectSession(string sessionId, string reason)
        {
            if (_role != EnetRole.Server) return;
            if (string.IsNullOrEmpty(sessionId)) return;
            if (!_sessionToPeer.TryGetValue(sessionId, out long peerId)) return;
            GD.Print($"[GodotWebSocketTransport] 主动断开会话 {sessionId}（peer {peerId}）：{reason}");
            _sessionToPeer.Remove(sessionId);
            _peerToSession.Remove(peerId);
            Peer.DisconnectPeer((int)peerId, false);
        }

        /// <summary>客户端角色：是否已连上服务端。</summary>
        public bool IsClientConnected()
            => _running && _role == EnetRole.Client
            && Peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

        // ==================== 会话分配 ====================

        private void OnPeerConnected(long id)
        {
            if (_role != EnetRole.Server) return;
            if (_peerToSession.ContainsKey(id)) return;
            string sessionId = "w" + (++_nextSessionSeq);
            _peerToSession[id] = sessionId;
            _sessionToPeer[sessionId] = id;
            GD.Print($"[GodotWebSocketTransport] WS 客户端 {id} 已连接 → 会话 {sessionId}");
            _sessionConnectedHandler?.Invoke(sessionId);
        }

        private void OnPeerDisconnected(long id)
        {
            if (_role != EnetRole.Server) return;
            if (!_peerToSession.TryGetValue(id, out var sessionId)) return;
            _peerToSession.Remove(id);
            _sessionToPeer.Remove(sessionId);
            GD.Print($"[GodotWebSocketTransport] WS 客户端 {id}（会话 {sessionId}）已断开");
            _sessionDisconnectedHandler?.Invoke(sessionId);
        }
    }
}
