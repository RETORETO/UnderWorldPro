using System;
using Godot;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 批次 6 收尾】**大厅服务端宿主**：把"服务端进程要装配的一切"收成一个可复用节点，
    /// 供两个入口共用，避免两份服务端装配代码：
    ///   · `Scenes/Net/NetServer.tscn`（`NetServerEntry`）——正式 headless 服务端入口（推荐）；
    ///   · `Scenes/Net/NetEntry.tscn --server`（`NetMatchEntry`）——开发/兼容入口（薄适配，内部用本类）。
    ///
    /// 装配内容：ENet 服务端传输 → `ServerMatchFactory`（每房间一套对局）→ `LobbyServer`（房间状态机 + 对局通道路由）
    /// → `ServerHost`（会话/协议/幂等底座）。每帧 `Poll()` 驱动 ENet 与房间超时清理。
    /// </summary>
    public partial class LobbyServerHost : Node
    {
        private INetTransport _transport;
        private ServerHost _host;
        private LobbyServer _lobby;
        private ServerMatchFactory _matchFactory;

        public ServerHost Host => _host;
        public LobbyServer Lobby => _lobby;
        public ServerConfig Config { get; private set; }
        /// <summary>是否已成功启动（端口占用等失败时为 false）。</summary>
        public bool Started { get; private set; }

        /// <summary>启动服务端。返回是否成功（失败会打印可读原因，不抛异常，便于入口给出友好提示）。</summary>
        public bool Start(ServerConfig config)
        {
            if (Started) return true;
            Config = config ?? new ServerConfig();

            // 【Web 联机】按配置选传输：浏览器客户端只能连 WebSocket 服务端（见 ServerConfig.UseWebSocket）。
            if (Config.UseWebSocket)
            {
                _transport = new GodotWebSocketTransport(EnetRole.Server) { Port = Config.Port };
            }
            else
            {
                _transport = new GodotEnetTransport(EnetRole.Server)
                {
                    Port = Config.Port,
                    MaxClients = Config.MaxConnections,
                };
            }
            try
            {
                _transport.Start();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[LobbyServerHost] 监听失败（端口 {Config.Port} 可能被占用）：{ex.Message}");
                GD.PrintErr("[LobbyServerHost] 请换端口（--port 19000）或先关闭占用该端口的进程。");
                return false;
            }

            _matchFactory = new ServerMatchFactory(this, _transport);
            _lobby = new LobbyServer(Config, new GameDeckValidator(), room => _matchFactory.Create(room))
            {
                LogHook = msg => GD.Print(msg),
            };
            _lobby.OnRoomClosed = room => _matchFactory.Destroy(room);
            // H1：建局失败回退时也要释放半成品对局装配（房间没关，但这一局的节点必须回收）
            _lobby.OnMatchAssemblyReleased = room => _matchFactory.Destroy(room);

            _host = new ServerHost(_transport, Config, _lobby, _lobby) { LogHook = msg => GD.Print(msg) };
            _lobby.Host = _host;
            _host.Start();

            Started = true;
            GD.Print($"[LobbyServerHost] 大厅服务端就绪：传输={(Config.UseWebSocket ? "WebSocket(浏览器可连)" : "ENet(桌面)")}"
                + $"，端口 {Config.Port}，最大连接 {Config.MaxConnections}，最大房间 {Config.MaxRooms}"
                + $"，昵称上限 {Config.NameMaxLen}，协议版本 {Config.ProtocolVersion}");
            PrintClientHint(Config);
            return true;
        }

        public override void _Process(double delta)
        {
            if (!Started) return;
            _host?.Poll();
            _lobby?.Tick(delta);
        }

        public override void _ExitTree()
        {
            if (Started) GD.Print("[LobbyServerHost] 停止服务端");
            try { _host?.Stop(); } catch { /* 退出期忽略 */ }
            Started = false;
            base._ExitTree();
        }

        /// <summary>打印"客户端怎么连"的提示（含本机候选 IPv4），减少手工翻文档。</summary>
        private static void PrintClientHint(ServerConfig config)
        {
            GD.Print("[LobbyServerHost] 客户端启动方式：");
            GD.Print("[LobbyServerHost]   A) 图形客户端：主菜单 →「联机对战」→ 填地址端口昵称 → 连接");
            GD.Print("[LobbyServerHost]   B) 命令行：<godot_mono_console.exe> --path <工程> res://Scenes/Net/NetEntry.tscn -- --lobby <本机IP> " + config.Port + " 昵称");
            GD.Print("[LobbyServerHost]   本机候选 IPv4：");
            foreach (var addr in IP.GetLocalAddresses())
            {
                if (addr == "127.0.0.1" || addr.Contains(':')) continue;   // 只列 IPv4
                GD.Print($"[LobbyServerHost]     {addr}:{config.Port}（Windows 防火墙需允许 UDP 入站）");
            }
        }
    }
}
