using Godot;
using Script.Manager;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【正式代码·真跨进程】命令行驱动的联机进程入口节点。
    ///
    /// 挂到某个根场景（或用 Godot 启动参数指定），在 _Ready 解析用户命令行参数并启动对应一侧：
    ///   --server [port]        → 启动服务端进程（权威规则 + ENet 监听，不建客户端投影）
    ///   --client [port] [pid]  → 启动客户端进程（独立 CardManager 从模板重建 + ENet 连接 + 消费快照，
    ///                             不建服务端规则；pid 为本进程代表的玩家编号 1/2，默认 1）
    ///
    /// 自动演示（便于多进程观察）：
    ///   服务端：检测到玩家1连接后自动 PushSnapshot() 推初始快照；玩家2连入后再补推一次。
    ///   客户端：连接后每 10 秒自动上行一个抽牌意图（以本进程的玩家编号身份），触发服务端结算 + 快照下行。
    ///
    /// 双进程验证方式（三个终端/窗口，需先 Build Project）：
    ///   # 服务端进程
    ///   godot --path d:/MYPROJECT/UWPro res://Scenes/Net/NetEntry.tscn -- --server 18950
    ///   # 客户端A（代表玩家1）
    ///   godot --path d:/MYPROJECT/UWPro res://Scenes/Net/NetEntry.tscn -- --client 18950 1
    ///   # 客户端B（代表玩家2）
    ///   godot --path d:/MYPROJECT/UWPro res://Scenes/Net/NetEntry.tscn -- --client 18950 2
    ///
    /// 说明：GodotEnetTransport 按连接顺序把第一个客户端映射为玩家1、第二个为玩家2，
    /// 服务端 IntentHub 会强校验 intent.PlayerId 与连接映射一致，故各客户端只能操作自己的玩家。
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

        public override void _Ready()
        {
            var args = OS.GetCmdlineUserArgs();
            ushort port = 18950;
            int clientPlayerId = 1;   // --client 的第三个参数：本客户端进程代表的玩家编号（1 或 2）
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--server") { _isServer = true; if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = (ushort)p; }
                else if (args[i] == "--client")
                {
                    _isClient = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = (ushort)p;
                    if (i + 2 < args.Length && int.TryParse(args[i + 2], out var pid)) clientPlayerId = pid;
                }
            }

            if (!_isServer && !_isClient) return; // 无联机参数，本节点空转（不干扰正常单机流程）

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
            }
            else if (_isClient)
            {
                var clientCard = new NetClientCardManager();
                _launcher.StartClientOnly(clientCard, port, null, clientPlayerId);
                // 挂载联机战场（快照驱动 UI，布局与单机 Arena 一致），显示客户端投影 + 提供操作
                var arenaOlScene = GD.Load<PackedScene>("res://Scenes/Net/ArenaOl.tscn");
                var arenaOl = arenaOlScene.Instantiate<Script.UI.ArenaOl>();
                arenaOl.Name = "ArenaOl";
                AddChild(arenaOl);
                arenaOl.AttachLauncher(_launcher, clientPlayerId);
                GD.Print($"[NetMatchEntry] 客户端进程就绪（玩家{clientPlayerId} + 端口 {port}）。等待服务端快照...");
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
