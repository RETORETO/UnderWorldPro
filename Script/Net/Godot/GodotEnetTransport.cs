using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【C/S 化 P3·模块A】基于 Godot ENet 的真实联机传输实现（INetTransport）。
    ///
    /// 设计要点：
    /// 1. 仅依赖 Godot 的底层 MultiplayerPeer 包 API（ENetMultiplayerPeer + Poll/GetPacket/PutPacket），
    ///    不挂入 SceneTree.Multiplayer 信号树，保持 transport 自包含、可在 Headless 服务端进程运行。
    /// 2. 消息模型：每条消息是一条 UTF-8 文本（JSON，由 ChoiceSerializer / SnapshotSerializer 生成），
    ///    经 ENet 可靠通道传输。这与 P2 的 LoopbackTransport 契约完全一致（Send/OnMessageReceived/Start/Stop）。
    /// 3. 玩家编号映射（仅服务端）：ENet 分配的 peer id（long）按连接顺序映射为游戏内玩家编号 1/2。
    ///    第一个连入 → 玩家1，第二个 → 玩家2；断开即从映射移除（P3·模块E 处理重连重映射）。
    /// 4. 客户端：本地玩家编号由上层在连接时协定（LocalPlayerId 属性），Send 始终发往 server（peer 1）。
    /// 5. 驱动：ENet 需主动 Poll。由场景中的 EnetPollDriverNode（或任意 Node 的 _Process）每帧调用 Poll()。
    ///
    /// 用法：
    ///   服务端：var t = new GodotEnetTransport(EnetRole.Server){ Port = 8910 };
    ///           t.OnMessageReceived((pid,msg)=>...); t.Start();  // 挂 EnetPollDriverNode 驱动 Poll
    ///   客户端：var t = new GodotEnetTransport(EnetRole.Client){ Port = 8910, ServerAddress = "127.0.0.1", LocalPlayerId = 1 };
    ///           t.OnMessageReceived((pid,msg)=>...); t.Start();
    ///
    /// 纪律：本文件位于 Net/Godot/ 子目录，是 Net/ 根目录中唯一允许引入 Godot 依赖的传输实现，
    /// 不破坏 Net/ 根目录的纯托管性（Headless 服务端仍可用 LoopbackTransport）。
    /// </summary>
    public class GodotEnetTransport : INetTransport
    {
        private readonly EnetRole _role;
        private readonly ENetMultiplayerPeer _peer = new ENetMultiplayerPeer();
        private readonly Encoding _utf8 = new UTF8Encoding(false);
        private Action<int, string> _serverHandler;
        private bool _running;

        // 服务端：ENet peer id(long) ↔ 游戏内玩家编号(int 1/2)
        private readonly Dictionary<long, int> _peerToPlayer = new Dictionary<long, int>();
        private readonly Dictionary<int, long> _playerToPeer = new Dictionary<int, long>();
        private int _nextPlayerId = 1;

        // —— 配置属性（供 INetTransport.Start() 无参启动读取）——
        /// <summary>监听/连接端口。</summary>
        public ushort Port { get; set; } = 8910;
        /// <summary>服务端最大客户端数（仅服务端用）。</summary>
        public int MaxClients { get; set; } = 2;
        /// <summary>服务端地址（仅客户端用）。</summary>
        public string ServerAddress { get; set; } = "127.0.0.1";
        /// <summary>客户端本地玩家编号（由上层连接协定制）。</summary>
        public int LocalPlayerId { get; set; } = 1;

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
            _peerToPlayer.Clear();
            _playerToPeer.Clear();
            _nextPlayerId = 1;
        }

        public void Send(int playerId, string message)
        {
            if (!_running) return;
            var bytes = _utf8.GetBytes(message);
            if (_role == EnetRole.Server)
            {
                if (_playerToPeer.TryGetValue(playerId, out long enetPeerId))
                {
                    _peer.SetTargetPeer((int)enetPeerId);
                    _peer.PutPacket(bytes);
                }
            }
            else
            {
                // 客户端：发往 server。ENet 客户端只连一个 peer（server），
                // 保持默认 target（0 = 发往唯一连接的 peer）即可，显式 SetTargetPeer 反而易指错 peer。
                // 防御：握手未完成（连接状态非 Connected）时直接返回，避免 PutPacket 触发 ERR_UNCONFIGURED 崩溃。
                if (_peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
                {
                    GD.PrintErr($"[GodotEnetTransport] Send 被忽略：客户端尚未连接服务器（connection_status != CONNECTED）");
                    return;
                }
                _peer.PutPacket(bytes);
            }
        }

        public void OnMessageReceived(Action<int, string> handler)
        {
            _serverHandler = handler;
        }

        /// <summary>
        /// 由 Godot 节点的 _Process 每帧调用，驱动 ENet 轮询并分发入站消息。
        /// 信号（PeerConnected/PeerDisconnected）在 Poll 内同步触发，先于收包执行。
        /// </summary>
        public void Poll()
        {
            if (!_running) return;
            _peer.Poll();

            while (_peer.GetAvailablePacketCount() > 0)
            {
                // 注意顺序：Godot 4.7 的 ENetMultiplayerPeer.GetPacket() 会消费队列中的包，
                // 若先 GetPacket 再 GetPacketPeer，后者会在空队列上报错并返回默认 peer(1)，
                // 导致服务端无法用真实 peer id 命中玩家映射而丢弃消息。必须先取 peer 再取数据。
                long from = _peer.GetPacketPeer();
                byte[] bytes = _peer.GetPacket();
                string msg = _utf8.GetString(bytes);
                int playerId;
                if (_role == EnetRole.Server)
                {
                    if (!_peerToPlayer.TryGetValue(from, out playerId))
                        continue; // 未知来源，丢弃
                }
                else
                {
                    playerId = LocalPlayerId;
                }
                _serverHandler?.Invoke(playerId, msg);
            }
        }

        /// <summary>服务端：当前已连入并分配了玩家编号的 ENet peer id 列表（调试/编排用）。</summary>
        public IReadOnlyCollection<long> ConnectedPeerIds => _peerToPlayer.Keys;

        /// <summary>服务端：查询某游戏内玩家编号是否已连入。</summary>
        public bool IsPlayerConnected(int playerId) => _playerToPeer.ContainsKey(playerId);

        /// <summary>客户端角色：是否已连上服务端（ENet 客户端底层连接状态）。服务端角色下不可用。</summary>
        public bool IsClientConnected()
            => _role == EnetRole.Client && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

        private void OnPeerConnected(long id)
        {
            if (_role != EnetRole.Server) return;
            if (_peerToPlayer.ContainsKey(id)) return;
            int pid = _nextPlayerId++;
            _peerToPlayer[id] = pid;
            _playerToPeer[pid] = id;
            GD.Print($"[GodotEnetTransport] Peer {id} connected → assigned player {pid}");
        }

        private void OnPeerDisconnected(long id)
        {
            if (_role != EnetRole.Server) return;
            if (_peerToPlayer.TryGetValue(id, out int pid))
            {
                _peerToPlayer.Remove(id);
                _playerToPeer.Remove(pid);
                GD.Print($"[GodotEnetTransport] Peer {id} (player {pid}) disconnected");
            }
        }
    }

    /// <summary>GodotEnetTransport 的角色：服务端权威 or 客户端。</summary>
    public enum EnetRole
    {
        Server,
        Client
    }
}
