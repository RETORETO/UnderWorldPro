using Godot;
using Script.Manager;
using Script.UI;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【联机演示节点·即插即用】把 NetworkMatchLauncher 接入 Godot 场景，用于手动验证"服务端权威 + 客户端投影"回环。
    ///
    /// 用法（不侵入 Arena 正式逻辑）：
    ///   1. 在 Arena.tscn 中把本节点作为 Arena 的子节点加入（或运行时 AddChild）；
    ///   2. 默认开启 AutoRun：进对局后自动 StartDemo→Draw×AutoDrawCount→Pass，并把日志打到输出窗口，
    ///      你只需运行 Arena 即可在编辑器底部"输出"面板看到 [NetworkMatchLauncher] 日志观察闭环。
    ///   3. 也可关闭 AutoRun，再在场景中右键 NetMatchDemo（远程面板调用方法）手动 StartDemo/DrawDemo/PassDemo/StopDemo。
    ///   4. 客户端投影状态可通过 launcher.ClientZoneManager 在调试器查看，断言其与服务端一致即验证闭环。
    ///
    /// 说明：本节点是演示/验证用途，正式联机入口由 NetworkMatchLauncher 承担；本节点仅负责"挂场景 + 触发"。
    /// 客户端投影状态可通过 launcher.ClientZoneManager 在调试器查看，断言其与服务端一致即验证闭环。
    /// </summary>
    [GlobalClass]
    public partial class NetMatchDemo : Node
    {
        /// <summary>进对局后自动跑 StartDemo→Draw×N→Pass→Stop，并把日志打印到编辑器输出窗口。</summary>
        [Export] public bool AutoRun { get; set; } = true;
        /// <summary>AutoRun 触发延迟（秒），用于等待 Arena 起手完毕。</summary>
        [Export] public int AutoStartDelaySec { get; set; } = 2;
        /// <summary>AutoRun 中客户端上行抽牌意图的次数。</summary>
        [Export] public int AutoDrawCount { get; set; } = 2;
        /// <summary>AutoRun 用 ENet 监听端口（默认 18920）。</summary>
        [Export] public ushort Port { get; set; } = 18920;

        private GameInitializer _gi;
        private NetworkMatchLauncher _launcher;
        private double _autoTimer = 0;
        private int _autoStep = 0;
        private int _autoDrawsDone = 0;

        public override void _Ready()
        {
            // 与 Arena 同路径查找 GameInitializer（Composition Root，兄弟节点）
            ResolveGameInitializer();
        }

        private void ResolveGameInitializer()
        {
            if (_gi != null) return;
            var parent = GetParent();
            // 用 Godot 相对路径 "../GameInitializer"（NetMatchDemo 与 GameInitializer 是 Arena 下的兄弟）
            var viaPath = GetNodeOrNull<GameInitializer>("../GameInitializer");
            var viaParent = parent?.GetNodeOrNull<GameInitializer>("GameInitializer");
            var viaScene = GetTree().CurrentScene?.GetNodeOrNull<GameInitializer>("GameInitializer");
            _gi = viaPath ?? viaParent ?? viaScene;

            if (_gi == null)
            {
                // ===== 诊断日志：列出父级所有子节点，定位 GameInitializer 真实名称/类型 =====
                GD.PushWarning($"[NetMatchDemo] 未找到 GameInitializer。" +
                    $"本节点路径={GetPath()} 父级={parent?.Name ?? "null"}({parent?.GetType().Name ?? "?"})");
                if (parent != null)
                {
                    foreach (var child in parent.GetChildren())
                        GD.PushWarning($"  - 子节点: 名={child.Name} 类型={child.GetType().Name}");
                }
                GD.PushWarning($"  查找结果: viaPath={(viaPath != null ? "命中" : "null")} viaParent={(viaParent != null ? "命中" : "null")} viaScene={(viaScene != null ? "命中" : "null")}");
            }
        }

        public override void _Process(double delta)
        {
            if (!AutoRun) return;
            _autoTimer += delta;
            if (_autoStep == 0 && _autoTimer >= AutoStartDelaySec)
            {
                // 阶段 0：自动启动 launcher。启动失败（launcher 未建）则直接结束，不再反复重试
                if (_launcher == null)
                {
                    StartDemo(Port);
                    _autoTimer = 0;
                    _autoStep = _launcher != null ? 1 : 2; // 失败则结束（_autoStep=2），避免每帧重试刷屏
                    return;
                }
                _autoStep = 1;
            }
            else if (_autoStep == 1)
            {
                if (_autoDrawsDone < AutoDrawCount)
                {
                    DrawDemo();
                    _autoDrawsDone++;
                    _autoTimer = 0;
                }
                else if (_autoTimer >= 1.0)
                {
                    PassDemo();
                    _autoTimer = 0;
                    _autoStep = 2;
                }
            }
        }

        /// <summary>启动本地回环联机对局（Server 权威 + Client 投影）。
        /// useLoopback=true 走内存回环（稳定，无需 ENet 握手），适合验证投影闭环；
        /// useLoopback=false 走真实 ENet（需等待连接建立，Godot 运行环境）。</summary>

        public void StartDemo(ushort port = 18920, bool useLoopback = true)
        {
            // _Ready 可能早于 Arena 装配 GameInitializer，故此处补一次查找（含场景树递归兜底）
            if (_gi == null)
                _gi = GetNodeOrNull<GameInitializer>("../GameInitializer")
                      ?? GetParent()?.GetNodeOrNull<GameInitializer>("GameInitializer")
                      ?? GetTree().CurrentScene?.GetNodeOrNull<GameInitializer>("GameInitializer")
                      ?? GetTree().Root.FindChild("GameInitializer", true, false) as GameInitializer;
            if (_gi == null) { GD.PushError("[NetMatchDemo] GameInitializer 未就绪，无法启动（父级=" + (GetParent()?.Name ?? "null") + "）"); return; }
            // 若 Arena 尚未起手（直接远程调用本方法），先确保对局初始化
            if (_gi.ZoneManager == null || _gi.TurnManager == null)
            {
                _gi.InitializeAll();
                _gi.StartMatch();
            }
            _launcher = new NetworkMatchLauncher();
            AddChild(_launcher); // launcher 自带 _Process 驱动两端 Poll
            if (useLoopback)
            {
                var loop = new Script.Net.LoopbackTransport();
                _launcher.StartLocalLoopback(_gi, port, loop, loop);
            }
            else
            {
                _launcher.StartLocalLoopback(_gi, port);
            }
            // 联机客户端渲染统一由 ArenaOl（快照驱动·全量重建）负责；NetMatchDemo 仅作早期意图通道演示。
            GD.Print($"[NetMatchDemo] 联机演示已启动（useLoopback={useLoopback}）远程调用 DrawDemo() 触发客户端意图");
        }

        /// <summary>客户端上行一条抽牌意图（验证意图→权威结算→快照下行→投影重建闭环）。</summary>

        public void DrawDemo() => _launcher?.SendIntent(PlayerIntent.Kind.DrawCards, "1");

        /// <summary>客户端上行一条让过意图。</summary>

        public void PassDemo() => _launcher?.SendIntent(PlayerIntent.Kind.Pass);


        public void StopDemo()
        {
            _launcher?.Stop();
            _launcher?.QueueFree();
            _launcher = null;
        }
    }
}
