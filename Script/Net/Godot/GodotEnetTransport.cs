using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【C/S 化 P3·模块A / 大厅化·批次 1】基于 Godot ENet 的真实联机传输实现（INetTransport）。
    ///
    /// 设计要点：
    /// 1. 仅依赖 Godot 的底层 MultiplayerPeer 包 API（ENetMultiplayerPeer + Poll/GetPacket/PutPacket），
    ///    不挂入 SceneTree.Multiplayer 信号树，保持 transport 自包含、可在 Headless 服务端进程运行。
    /// 2. 消息模型：每条消息是一条 UTF-8 文本（JSON，由各 Serializer 生成），经 ENet 可靠通道传输。
    /// 3. **会话寻址（批次 1 起）**：服务端为每条连接分配一个会话 id（`s1`、`s2`…），
    ///    维护 `peerId ↔ sessionId` 双向映射；`SendToSession` 按会话定向下发。
    ///    （旧的"连接按顺序 = 玩家1/2"假设已废除：座位由房间层把 Seat↔Session 绑定，见大厅设计方案 §五。）
    /// 4. 客户端：只有一条到服务端的连接，用 `SendToServer`；自身会话 id 由服务端在 `Hello` 应答中回传。
    ///    客户端收到下行消息时上报的 sessionId 恒为 <see cref="SelfSessionId"/>（占位，不含寻址含义）。
    /// 5. 驱动：ENet 需主动 Poll，由场景中的节点每帧调用 `Poll()`。
    ///
    /// 用法：
    ///   服务端：var t = new GodotEnetTransport(EnetRole.Server){ Port = 18950, MaxClients = 16 };
    ///           t.OnSessionMessage((sid,msg)=>...); t.Start();
    ///   客户端：var t = new GodotEnetTransport(EnetRole.Client){ Port = 18950, ServerAddress = "127.0.0.1" };
    ///           t.OnSessionMessage((sid,msg)=>...); t.Start();
    ///
    /// 纪律：本文件位于 Net/Godot/ 子目录，是 Net/ 根目录中唯一允许引入 Godot 依赖的传输实现，
    /// 不破坏 Net/ 根目录的纯托管性（Headless 服务端仍可用 LoopbackTransport 做测试）。
    /// </summary>
    public class GodotEnetTransport : INetTransport
    {
        private readonly EnetRole _role;
        private readonly ENetMultiplayerPeer _peer = new ENetMultiplayerPeer();
        private readonly Encoding _utf8 = new UTF8Encoding(false);

        private Action<string, string> _sessionMessageHandler;
        private Action<string> _sessionConnectedHandler;
        private Action<string> _sessionDisconnectedHandler;

        private bool _running;
        /// <summary>客户端：是否曾观测到"连接中/已连接"（用于区分"刚创建尚未就绪"与"连接确实失败"）。</summary>
        private bool _linkWasUp;
        /// <summary>客户端：连接是否已判定失败（Poll 轮询已因此停止）。供上层给出一次性提示，避免日志刷屏。</summary>
        public bool LinkFailed { get; private set; }

        // 服务端：ENet peer id(long) ↔ 会话 id(string)
        private readonly Dictionary<long, string> _peerToSession = new Dictionary<long, string>();
        private readonly Dictionary<string, long> _sessionToPeer = new Dictionary<string, long>();
        private int _nextSessionSeq;

        /// <summary>客户端收到的下行消息所使用的会话 id 占位（客户端不需要寻址；真实会话 id 见 Hello 应答）。</summary>
        public const string SelfSessionId = "self";

        // —— 配置属性（供 INetTransport.Start() 无参启动读取）——
        /// <summary>监听/连接端口。</summary>
        public ushort Port { get; set; } = 8910;
        /// <summary>服务端最大客户端数（仅服务端用）。大厅要容纳"多人等待 + 场内多人"，故默认 16。</summary>
        public int MaxClients { get; set; } = 16;
        /// <summary>服务端地址（仅客户端用）。</summary>
        public string ServerAddress { get; set; } = "127.0.0.1";

        public GodotEnetTransport(EnetRole role)
        {
            _role = role;
        }

        public void Start()
        {
            if (_running) return;
            Error err;
            if (_role == EnetRole.Server)
            {
                err = _peer.CreateServer(Port, MaxClients);
                if (err != Error.Ok)
                    throw new InvalidOperationException($"[GodotEnetTransport] CreateServer failed: {err}");
                _peer.PeerConnected += OnPeerConnected;
                _peer.PeerDisconnected += OnPeerDisconnected;
            }
            else
            {
                err = _peer.CreateClient(ServerAddress, Port);
                if (err != Error.Ok)
                    throw new InvalidOperationException($"[GodotEnetTransport] CreateClient failed: {err}");
            }
            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_role == EnetRole.Server)
            {
                _peer.PeerConnected -= OnPeerConnected;
                _peer.PeerDisconnected -= OnPeerDisconnected;
            }
            _peer.Close();
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
                GD.PrintErr("[GodotEnetTransport] SendToSession 仅服务端可用（客户端请用 SendToServer）");
                return;
            }
            if (string.IsNullOrEmpty(sessionId)) return;
            if (!_sessionToPeer.TryGetValue(sessionId, out long enetPeerId)) return;   // 会话不存在：静默忽略
            _peer.SetTargetPeer((int)enetPeerId);
            _peer.PutPacket(_utf8.GetBytes(message));
        }

        public void SendToServer(string message)
        {
            if (!_running) return;
            if (_role != EnetRole.Client)
            {
                GD.PrintErr("[GodotEnetTransport] SendToServer 仅客户端可用（服务端请用 SendToSession）");
                return;
            }
            // 客户端：发往 server。ENet 客户端只连一个 peer（server），保持默认 target（0）即可。
            // 防御：握手未完成时直接返回，避免 PutPacket 触发 ERR_UNCONFIGURED 崩溃。
            if (_peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
            {
                GD.PrintErr("[GodotEnetTransport] SendToServer 被忽略：客户端尚未连接服务器（connection_status != CONNECTED）");
                return;
            }
            _peer.PutPacket(_utf8.GetBytes(message));
        }

        // ==================== 订阅 ====================

        public void OnSessionMessage(Action<string, string> handler) => _sessionMessageHandler = handler;
        public void OnSessionConnected(Action<string> handler) => _sessionConnectedHandler = handler;
        public void OnSessionDisconnected(Action<string> handler) => _sessionDisconnectedHandler = handler;

        // ==================== 轮询 ====================

        /// <summary>
        /// 由 Godot 节点的 _Process 每帧调用，驱动 ENet 轮询并分发入站消息。
        /// 信号（PeerConnected/PeerDisconnected）在 Poll 内同步触发，先于收包执行。
        ///
        /// **顺序很重要**：先 `_peer.Poll()` + 收包，**再**判定"链路是否已断"。
        /// 服务端停止/断开前会先下发收尾消息（"房间已关闭"等），若在判定处直接 return，
        /// 这一帧刚到的数据就永远读不出来 —— 客户端会看到连接无声消失（H3 的闭环正是靠这条消息）。
        /// 判定为断开后 `_running = false`，后续帧不再 Poll，因此不会出现"每帧刷引擎错误"。
        /// </summary>
        public void Poll()
        {
            if (!_running) return;

            _peer.Poll();
            DispatchAvailablePackets();

            // 客户端链路已彻底断开（ENet 判定连接失败 / 对端关闭）时，peer 已不活跃：
            // 此处判定一次、给出**可行动**的诊断后停止轮询。
            // 说明：用"曾观测到 Connecting/Connected"作闩锁，避免刚创建时状态尚未就绪就误判为失败。
            if (_role == EnetRole.Client)
            {
                var status = _peer.GetConnectionStatus();
                if (status != MultiplayerPeer.ConnectionStatus.Disconnected) _linkWasUp = true;
                else if (_linkWasUp)
                {
                    _running = false;
                    LinkFailed = true;
                    GD.PrintErr($"[GodotEnetTransport] 连接服务端 {ServerAddress}:{Port} 失败（ENet 状态=Disconnected），已停止轮询。"
                        + "请依次确认：① 客户端填写的服务端地址是否为**服务端电脑的局域网 IPv4**（不是 127.0.0.1）；"
                        + "② 服务端进程是否已在运行；"
                        + "③ 服务端电脑防火墙是否放行该 UDP 端口（netsh advfirewall firewall add rule name=\"UWPro Server\" dir=in action=allow protocol=UDP localport=<端口>）；"
                        + "④ 两台电脑是否在同一网段（互相 ping 通）。");
                }
            }
        }

        /// <summary>把 ENet 队列中已到达的数据包全部分发出去（服务端按 peer 查会话，客户端用占位会话 id）。</summary>
        private void DispatchAvailablePackets()
        {
            while (_peer.GetAvailablePacketCount() > 0)
            {
                // 注意顺序：Godot 4.7 的 ENetMultiplayerPeer.GetPacket() 会消费队列中的包，
                // 若先 GetPacket 再 GetPacketPeer，后者会在空队列上报错并返回默认 peer(1)。
                // 必须先取 peer 再取数据。
                long from = _peer.GetPacketPeer();
                byte[] bytes = _peer.GetPacket();
                string msg = _utf8.GetString(bytes);
                if (_role == EnetRole.Server)
                {
                    if (!_peerToSession.TryGetValue(from, out var sessionId)) continue;   // 未知来源，丢弃
                    _sessionMessageHandler?.Invoke(sessionId, msg);
                }
                else
                {
                    // 客户端：下行消息无来源寻址需求，用占位会话 id（真实会话 id 由 Hello 应答回传）
                    _sessionMessageHandler?.Invoke(SelfSessionId, msg);
                }
            }
        }

        // ==================== 查询 ====================

        /// <summary>服务端：当前已连入的会话 id 列表。</summary>
        public IReadOnlyCollection<string> ConnectedSessionIds => _sessionToPeer.Keys;

        /// <summary>服务端：该会话是否仍连接着。客户端角色下恒为 false（客户端没有"会话池"概念）。</summary>
        public bool IsSessionConnected(string sessionId)
            => !string.IsNullOrEmpty(sessionId) && _sessionToPeer.ContainsKey(sessionId);

        public int ConnectedSessionCount => _sessionToPeer.Count;

        /// <summary>
        /// 服务端主动断开某会话。
        /// **优雅断开**（`force: false`）：ENet 会先把该 peer 已排队的下行数据（如"你已被移出房间""房间已关闭"）发完
        /// 再发断开包；强制断开会让这些通知静默丢失，客户端只看到连接消失。
        /// 会话映射**立即**移除（服务端视角即时生效），后续 `OnPeerDisconnected` 不会重复回调。
        /// </summary>
        public void DisconnectSession(string sessionId, string reason)
        {
            if (_role != EnetRole.Server) return;
            if (string.IsNullOrEmpty(sessionId)) return;
            if (!_sessionToPeer.TryGetValue(sessionId, out long peerId)) return;
            GD.Print($"[GodotEnetTransport] 主动断开会话 {sessionId}（peer {peerId}）：{reason}");
            _sessionToPeer.Remove(sessionId);
            _peerToSession.Remove(peerId);
            _peer.DisconnectPeer((int)peerId, false);
        }

        /// <summary>客户端角色：是否已连上服务端（ENet 客户端底层连接状态）。服务端角色下不可用。</summary>
        public bool IsClientConnected()
            => _role == EnetRole.Client && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

        // ==================== 会话分配 ====================

        private void OnPeerConnected(long id)
        {
            if (_role != EnetRole.Server) return;
            if (_peerToSession.ContainsKey(id)) return;
            string sessionId = "s" + (++_nextSessionSeq);
            _peerToSession[id] = sessionId;
            _sessionToPeer[sessionId] = id;
            GD.Print($"[GodotEnetTransport] Peer {id} connected → session {sessionId}");
            _sessionConnectedHandler?.Invoke(sessionId);
        }

        private void OnPeerDisconnected(long id)
        {
            if (_role != EnetRole.Server) return;
            if (!_peerToSession.TryGetValue(id, out var sessionId)) return;
            _peerToSession.Remove(id);
            _sessionToPeer.Remove(sessionId);
            GD.Print($"[GodotEnetTransport] Peer {id} (session {sessionId}) disconnected");
            _sessionDisconnectedHandler?.Invoke(sessionId);
        }
    }

    /// <summary>GodotEnetTransport 的角色：服务端权威 or 客户端。</summary>
    public enum EnetRole
    {
        Server,
        Client
    }
}
