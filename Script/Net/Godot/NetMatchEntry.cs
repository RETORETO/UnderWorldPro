using Godot;
using Script.Manager;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【正式代码·真跨进程】命令行驱动的联机进程入口节点。
    ///
    /// 挂到某个根场景（或用 Godot 启动参数指定），在 _Ready 解析用户命令行参数并启动对应一侧：
    ///   --server [port]                 → 启动服务端进程（权威规则 + ENet 监听，不建客户端投影）
    ///   --client [port] [pid] [host]    → 启动客户端进程（独立 CardManager 从模板重建 + ENet 连接 + 消费快照，
    ///                                     不建服务端规则；pid 为本进程代表的玩家编号 1/2，默认 1；
    ///                                     host 为服务端地址，默认 127.0.0.1＝本机）
    ///   --host [ip]                     → 等价写法：单独指定服务端地址（也可只写这一项）
    ///
    /// 局域网对战（服务端在 A 机、客户端在 B 机）：
    ///   A 机：godot --path <项目> res://Scenes/Net/NetEntry.tscn -- --server 18950
    ///   B 机：godot --path <项目> res://Scenes/Net/NetEntry.tscn -- --client 18950 1 <A机IP>
    ///         godot --path <项目> res://Scenes/Net/NetEntry.tscn -- --client 18950 2 <A机IP>
    ///   注意：服务端按**连接先后**分配玩家编号（第一个连入=玩家1），故必须先启动玩家1 的客户端；
    ///         IntentHub 会强校验 intent.PlayerId 与连接映射一致（不符即拒绝）。
    ///
    /// 自动演示（便于多进程观察）：
    ///   服务端：检测到玩家1连接后自动 PushSnapshot() 推初始快照；玩家2连入后再补推一次。
    ///   客户端：连接后自动抽牌演示已废弃（见 _Process 注释）。
    ///
    /// 说明：服务端 ENet 监听绑定全部网卡（局域网可达）；首次运行 Windows 会弹防火墙授权，需允许（UDP）。
    ///
    /// 服务端：挂本节点后，需先在场景中存在/装配好 GameInitializer（对局组合根），由 _Ready 查找并 StartMatch。
    /// 客户端：无需 GameInitializer（不跑规则），只用快照重建投影。
    /// </summary>
    [GlobalClass]
    public partial class NetMatchEntry : Node
    {
        private NetworkMatchLauncher _launcher;
        private bool _isServer = false;
        private bool _isClient = false;
        private bool _initialSnapshotPushed = false;
        private bool _p2SnapshotPushed = false;
        private double _clientAutoActTimer = 0;
        /// <summary>客户端：服务端地址/端口（排障日志用）。</summary>
        private string _serverHost = "127.0.0.1";
        private ushort _port = 18950;
        /// <summary>客户端：是否已连上服务端（只打印一次"已连接"）。</summary>
        private bool _clientLinkUp;
        private double _clientLinkNotifyTimer;
        /// <summary>客户端：链路失败已提示过一次（避免日志刷屏）。</summary>
        private bool _clientLinkFailedNotified;

        public override void _Ready()
        {
            var args = OS.GetCmdlineUserArgs();
            ushort port = 18950;
            int clientPlayerId = 1;   // --client 的第三个参数：本客户端进程代表的玩家编号（1 或 2）
            string serverHost = "127.0.0.1";   // 服务端地址：本机默认回环，局域网对战填服务端电脑 IP
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--server") { _isServer = true; if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = (ushort)p; }
                else if (args[i] == "--client")
                {
                    _isClient = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = (ushort)p;
                    if (i + 2 < args.Length && int.TryParse(args[i + 2], out var pid)) clientPlayerId = pid;
                    // 第四个参数（可选）：服务端 IP，支持 "--client 18950 1 192.168.1.10"
                    if (i + 3 < args.Length && !args[i + 3].StartsWith("--")) serverHost = args[i + 3];
                }
                else if (args[i] == "--host")
                {
                    // 单独写法：--host 192.168.1.10（与 --client 的第四个参数等价，后者优先）
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) serverHost = args[i + 1];
                }
            }

            if (!_isServer && !_isClient) return; // 无联机参数，本节点空转（不干扰正常单机流程）

            _serverHost = serverHost;
            _port = port;

            _launcher = new NetworkMatchLauncher();
            AddChild(_launcher); // launcher 自带 _Process 驱动 ENet Poll

            if (_isServer)
            {
                // 服务端：查找场景中装配好的 GameInitializer（对局组合根），必要时初始化 + 起手
                var gi = GetNodeOrNull<GameInitializer>("GameInitializer")
                         ?? GetParent()?.GetNodeOrNull<GameInitializer>("GameInitializer");
                if (gi == null)
                {
                    GD.PushError("[NetMatchEntry] --server 需要场景中存在 GameInitializer 节点");
                    return;
                }
                if (gi.ZoneManager == null || gi.TurnManager == null)
                {
                    gi.InitializeAll();
                    gi.StartMatch();
                    SetupInitialAreas(gi); // 命令行场景无 Arena，手动摆 3 个初始地区（纯逻辑，供地区投影验证）
                }
                _launcher.StartServerOnly(gi, port);
                GD.Print($"[NetMatchEntry] 服务端进程就绪（端口 {port}，含 {gi.AreaManager?.GetOrderedAreaUids().Count} 个地区）。等待客户端连接...");
                PrintLocalAddresses(port);
            }
            else if (_isClient)
            {
                var clientCard = new NetClientCardManager();
                _launcher.StartClientOnly(clientCard, port, null, clientPlayerId, serverHost);
                // 挂载联机战场（快照驱动 UI，布局与单机 Arena 一致），显示客户端投影 + 提供操作
                var arenaOlScene = GD.Load<PackedScene>("res://Scenes/Net/ArenaOl.tscn");
                var arenaOl = arenaOlScene.Instantiate<Script.UI.ArenaOl>();
                arenaOl.Name = "ArenaOl";
                AddChild(arenaOl);
                arenaOl.AttachLauncher(_launcher, clientPlayerId);
                GD.Print($"[NetMatchEntry] 客户端进程就绪（玩家{clientPlayerId} + 服务端 {serverHost} 端口 {port}）。等待服务端快照...");
            }
        }

        /// <summary>服务端启动后打印本机可用的局域网 IPv4（供另一台电脑的 --client 参数直接使用）。</summary>
        private static void PrintLocalAddresses(ushort port)
        {
            GD.Print("[NetMatchEntry] 局域网对战：请让客户端使用下面任一 IPv4 地址作为服务端地址（--client <port> <pid> <ip>）");
            foreach (var addr in IP.GetLocalAddresses())
            {
                if (addr == "127.0.0.1") continue;
                GD.Print($"[NetMatchEntry]   候选服务端地址：{addr}  端口 {port}（Windows 防火墙需允许 UDP 入站）");
            }
        }

        /// <summary>摆放初始地区（纯逻辑，不碰 UI）。复用 PlayerActionService.SetupInitialAreas，与单机 Arena 同一入口。</summary>
        private void SetupInitialAreas(GameInitializer gi)
        {
            var svc = gi.PlayerActionService;
            if (svc == null) return;
            var initial = svc.SetupInitialAreas(3);
            GD.Print($"[NetMatchEntry] 已摆放 {initial.Count} 个初始地区（供地区投影验证）");
        }

        public override void _Process(double delta)
        {
            if (_launcher == null) return;

            // 显式驱动 ENet Poll（launcher 已被 AddChild，引擎也会调；显式调用更保险）
            _launcher._Process(delta);

            if (_isServer)
            {
                // 服务端：玩家1连入后推送初始快照；玩家2（第二个客户端）连入后再推一次，
                // 保证后到的玩家2 能立即看到当前整局状态。此后随玩家意图自动广播全量快照。
                if (!_initialSnapshotPushed && _launcher.ServerPlayerConnected(1))
                {
                    _launcher.PushSnapshot();
                    _initialSnapshotPushed = true;
                    GD.Print("[NetMatchEntry] 玩家1已连接，已推送初始快照。");
                }
                else if (_initialSnapshotPushed && !_p2SnapshotPushed && _launcher.ServerPlayerConnected(2))
                {
                    _launcher.PushSnapshot();
                    _p2SnapshotPushed = true;
                    GD.Print("[NetMatchEntry] 玩家2已连接，已补推一次快照（玩家2 可看到当前局面）。");
                }
            }
            else if (_isClient)
            {
                // 连接状态可见化（局域网排障必需）：连上就打一次"已连接"，未连上每 5 秒提示一次，
                // 避免"点了没反应"却看不出是链路没通还是服务端没启动。
                if (!_clientLinkUp && _launcher.ClientConnected())
                {
                    _clientLinkUp = true;
                    GD.Print($"[NetMatchEntry] 已连接服务端 {_serverHost}:{_port}，等待初始快照...");
                }
                else if (!_clientLinkUp && _launcher.ClientLinkFailed())
                {
                    // 链路已判定失败（ENet 已给出一键排障提示）→ 只提示一次并停掉周期提醒，避免日志刷屏
                    if (!_clientLinkFailedNotified)
                    {
                        _clientLinkFailedNotified = true;
                        GD.PrintErr("[NetMatchEntry] 与服务器的连接已断开/未能建立。请修正地址或端口后**重启本客户端进程**重试。");
                    }
                }
                else if (!_clientLinkUp)
                {
                    _clientLinkNotifyTimer += delta;
                    if (_clientLinkNotifyTimer >= 5.0)
                    {
                        _clientLinkNotifyTimer = 0;
                        GD.Print($"[NetMatchEntry] 仍未连上服务端 {_serverHost}:{_port}……"
                            + "请确认：服务端进程已启动、IP 为服务端电脑的局域网地址、服务端防火墙已放行该 UDP 端口。");
                    }
                }

                // ============================================================
                // 【已废弃】客户端自动抽牌演示逻辑（每 10 秒上行一个 DrawCards 意图）。
                // 作用：早期验证「服务端结算 + 快照下行 + 客户端投影重建」链路是否闭环。
                // 已验证完成（P1/P2 手牌能按顺序增长）。此逻辑仅用于演示，不应作为正式玩法。
                // ⚠ 后续不可复用：正式对战的操作必须由玩家显式发出（出牌/让过/派遣等），
                //   任何"定时自动抽牌"都不属于真实玩法，禁止重新启用。
                // ============================================================
                // if (_launcher.ClientConnected())
                // {
                //     _clientAutoActTimer += delta;
                //     if (_clientAutoActTimer >= 10.0)
                //     {
                //         _launcher.SendIntent(PlayerIntent.Kind.DrawCards, "1");
                //         GD.Print("[NetMatchEntry] 客户端已自动上行抽牌意图（DrawCards）");
                //         _clientAutoActTimer = 0;
                //     }
                // }
                _ = delta; // 保留参数占位，避免未使用告警
            }
        }
    }
}
