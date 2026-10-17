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

        // —— 【增量 A】BOT 模式（大厅流程冒烟） ——
        private bool _isBot;
        private string _botRole = "host";
        private string _botDeckPath;
        private bool _startLobby;
        private string _lobbyNick;
        private ClientSession _botSession;
        private NetBotClient _bot;

        // —— 【容量压测 · 2026-10-17】进程内批量 BOT（`--load`） ——
        private LoadBotRunner _loadRunner;
        private int _loadPairs;
        private int _loadIntervalMs = 3000;
        private int _loadDurationSeconds;
        private string _loadDeckPath;
        private double _loadElapsed;

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
                    if (i + 3 < args.Length && !args[i + 3].StartsWith("--", System.StringComparison.Ordinal)) serverHost = args[i + 3];
                }
                else if (args[i] == "--host")
                {
                    // 单独写法：--host 192.168.1.10（与 --client 的第四个参数等价，后者优先）
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", System.StringComparison.Ordinal)) serverHost = args[i + 1];
                }
                else if (args[i] == "--bot")
                {
                    // 【增量 A】脚本化客户端（开发/冒烟用）：--bot <port> <host|join> [serverIp] [deckPath]
                    // 配合 `NetServer.tscn` 可在**一台机器**上真跑"完整流程"（建房→加入→准备→开局→对局→重开）。
                    _isBot = true;
                    _isClient = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var bp)) port = (ushort)bp;
                    if (i + 2 < args.Length && !args[i + 2].StartsWith("--", System.StringComparison.Ordinal)) _botRole = args[i + 2];
                    if (i + 3 < args.Length && !args[i + 3].StartsWith("--", System.StringComparison.Ordinal)) serverHost = args[i + 3];
                    if (i + 4 < args.Length && !args[i + 4].StartsWith("--", System.StringComparison.Ordinal)) _botDeckPath = args[i + 4];
                }
                else if (args[i] == "--load")
                {
                    // 【容量压测 · 2026-10-17】进程内拉起 N 对 BOT（每对 = 1 host + 1 join），造 N 间并发房。
                    //   --load <pairs> [serverIp] [port] [--load-interval <ms>] [--load-duration <sec>]
                    // 与 `--bot` 的区别：--bot 是"一个进程一个 BOT"（冒烟用）；--load 是"一个进程 N 对"（压测用，
                    // 避免 N×2 个 Godot 进程把被测的 CPU/内存曲线污染掉）。
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var lpairs)) _loadPairs = lpairs;
                    if (i + 2 < args.Length && !args[i + 2].StartsWith("--", System.StringComparison.Ordinal)) serverHost = args[i + 2];
                    if (i + 3 < args.Length && int.TryParse(args[i + 3], out var lport)) port = (ushort)lport;
                }
                else if (args[i] == "--load-interval")
                {
                    // 对局内「让过」间隔（毫秒）。默认 3000；压测时可设 200~500 提高负载。
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var li) && li > 0) _loadIntervalMs = li;
                }
                else if (args[i] == "--load-duration")
                {
                    // 跑满多少秒后打印终态摘要并退出（0 = 不自动退出，靠外部杀掉）。
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var ld) && ld > 0) _loadDurationSeconds = ld;
                }
                else if (args[i] == "--load-deck")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", System.StringComparison.Ordinal)) _loadDeckPath = args[i + 1];
                }
                else if (args[i] == "--lobby")
                {
                    // 【增量 B】直接进入联机大厅页（跳过主菜单）并自动连接：
                    //   --lobby [serverIp] [port] [nick] [--smoke-host|--smoke-join]
                    // 只影响联机入口；单机流程（主菜单/单机大厅）不受影响。
                    _startLobby = true;
                    Script.UI.Online.OnlineClientContext.AutoConnect = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", System.StringComparison.Ordinal)) serverHost = args[i + 1];
                    if (i + 2 < args.Length && int.TryParse(args[i + 2], out var lp)) port = (ushort)lp;
                    if (i + 3 < args.Length && !args[i + 3].StartsWith("--", System.StringComparison.Ordinal)) _lobbyNick = args[i + 3];
                }
                else if (args[i] == "--smoke-host")
                {
                    Script.UI.Online.OnlineClientContext.SmokeMode = true;
                    Script.UI.Online.OnlineClientContext.SmokeIsHost = true;
                }
                else if (args[i] == "--smoke-join")
                {
                    Script.UI.Online.OnlineClientContext.SmokeMode = true;
                    Script.UI.Online.OnlineClientContext.SmokeIsHost = false;
                }
            }

            // ==================== 联机大厅页模式（增量 B：带 UI 的大厅入口） ====================
            if (_startLobby)
            {
                Script.UI.Online.OnlineClientContext.LastHost = serverHost;
                Script.UI.Online.OnlineClientContext.LastPort = port;
                if (!string.IsNullOrWhiteSpace(_lobbyNick)) Script.UI.Online.OnlineClientContext.LastName = _lobbyNick;
                GD.Print($"[NetMatchEntry] 进入联机大厅页（默认服务端 {serverHost}:{port}）");
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
                return;
            }

            // ==================== 批量压测模式（进程内 N 对 BOT） ====================
            // 必须在 `if (!_isServer && !_isClient) return;` 之前：`--load` 既不是服务端也不是单个客户端。
            if (_loadPairs > 0)
            {
                _serverHost = serverHost;
                _port = port;
                _loadRunner = new LoadBotRunner(serverHost, port, _loadPairs, _loadIntervalMs / 1000.0, _loadDeckPath);
                _loadRunner.Start();
                GD.Print($"[NetMatchEntry] 批量压测模式已启动（{_loadPairs} 对 → {serverHost}:{port}）");
                return;
            }

            if (!_isServer && !_isClient) return; // 无联机参数，本节点空转（不干扰正常单机流程）

            _serverHost = serverHost;
            _port = port;

            // ==================== BOT 模式（增量 A：大厅流程冒烟，不挂战场 UI） ====================
            if (_isBot)
            {
                _botSession = ClientSession.Connect(serverHost, port, _botRole == "host" ? "房主BOT" : "玩家BOT");
                _bot = new NetBotClient(_botSession, _botRole == "host", _botDeckPath);
                GD.Print($"[NetMatchEntry] BOT 客户端已启动（角色 {_botRole}，服务端 {serverHost}:{port}）");
                return;
            }

            _launcher = new NetworkMatchLauncher();
            AddChild(_launcher); // launcher 自带 _Process 驱动 ENet Poll

            // ==================== 服务端（薄适配：与 NetServer.tscn 走同一实现） ====================
            // 历史：早期 `--server` 用 NetworkMatchLauncher 起"单场服务端"（无大厅）。批次 6 起统一为
            // **大厅服务端**（LobbyServerHost）——避免两条服务端形态并存；单场启动器仅保留给测试/回环。
            if (_isServer)
            {
                var host = new LobbyServerHost { Name = "LobbyServerHost" };
                AddChild(host);
                if (!host.Start(Script.Net.Lobby.ServerConfig.FromArgs(args)))
                {
                    GD.PrintErr("[NetMatchEntry] 服务端启动失败，进程退出");
                    GetTree().Quit(1);
                    return;
                }
                GD.Print("[NetMatchEntry] --server 已映射到大厅服务端（与 NetServer.tscn 同一实现）");
                return;
            }

            // ==================== 旧式客户端（薄适配：映射到大厅流程） ====================
            // 历史：`--client <port> <pid> [ip]` 直接挂单场战场、等待服务端推快照。批次 6 起统一为
            // "进大厅页（自动连接）"；座位由服务端在房间内分配，故 pid 参数不再需要（保留兼容、仅打印提示）。
            if (_isClient)
            {
                GD.Print($"[NetMatchEntry] --client 已映射到大厅客户端（忽略 pid={clientPlayerId}，座位由房间分配）");
                Script.UI.Online.OnlineClientContext.AutoConnect = true;
                Script.UI.Online.OnlineClientContext.LastHost = serverHost;
                Script.UI.Online.OnlineClientContext.LastPort = port;
                Script.UI.Online.OnlineClientContext.LastName = $"玩家{clientPlayerId}";
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
                return;
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
            // 批量压测模式：驱动 N 对 BOT；到点打印终态摘要并退出（便于脚本化跑实验）
            if (_loadRunner != null)
            {
                _loadRunner.Poll(delta);
                _loadElapsed += delta;
                if (_loadDurationSeconds > 0 && _loadElapsed >= _loadDurationSeconds)
                {
                    _loadRunner.DumpSummary();
                    // 【2026-10-17】退出前先释放会话与客户端投影，否则进程退出时报 ObjectDB 泄漏
                    //（每个会话的 ClientMatchView 会 new 9 个不入树的 Manager Node）。
                    _loadRunner.Stop();
                    GD.Print($"[NetMatchEntry] 压测时长已到（{_loadDurationSeconds}s），退出");
                    GetTree().Quit(0);
                }
                return;
            }

            // BOT 模式：只驱动 ClientSession（无战场 UI、无本地规则）
            if (_isBot)
            {
                _bot?.Poll(delta);
                return;
            }

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

        /// <summary>
        /// 【2026-10-17】退出兜底：释放本节点**自己创建**的 BOT 会话与压测负载。
        ///
        /// 为什么需要：`--load` 到点走的是 `_loadRunner.Stop()`（正常路径），但如果进程被 Ctrl+C /
        /// 场景切换等方式提前结束，会话与其 `ClientMatchView`（9 个不入树的 Manager Node）就没人释放，
        /// 退出时会出现 `ObjectDB instances were leaked`。
        ///
        /// 纪律：只处理 `--bot` / `--load` 的私有会话；**不要**碰 `OnlineClientContext` 里的共享会话
        /// （那是 `--lobby` / 图形客户端的，生命周期归 `OnlineClientContext` 管）。
        /// </summary>
        public override void _ExitTree()
        {
            try { _loadRunner?.Stop(); } catch { /* 退出期忽略 */ }
            _loadRunner = null;

            try { _botSession?.Dispose(); } catch { /* 退出期忽略 */ }
            _botSession = null;
            _bot = null;

            base._ExitTree();
        }
    }
}
