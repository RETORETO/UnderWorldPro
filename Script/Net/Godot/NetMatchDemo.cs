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
        /// <summary>是否把客户端收到的快照差异驱动到 Arena UI（快照差异渲染入口，联机演示用）。</summary>
        [Export] public bool DriveArenaBySnapshot { get; set; } = true;
        /// <summary>【TEMPTEST】是否开启"持续让过+抽牌"自动循环（验收后移除）。</summary>
        [Export] public bool AutoLoopPassDraw { get; set; } = false;
        /// <summary>【TEMPTEST】AutoLoopPassDraw 的动作间隔（秒）。</summary>
        [Export] public int LoopIntervalSec { get; set; } = 2;

        private GameInitializer _gi;
        private NetworkMatchLauncher _launcher;
        private double _autoTimer = 0;
        private int _autoStep = 0;
        private int _autoDrawsDone = 0;
        // 【TEMPTEST】持续让过+抽牌循环的状态（验收后随方法一并移除）
        private double _loopTimer = 0;
        private int _loopStep = 0; // 0=P1让过 1=P2让过 2=抽牌（推进回合节拍）
        private bool _loopStarted = false; // 启动只尝试一次，避免 _gi 未就绪时每帧重试刷屏

        public override void _Ready()
        {
            // AutoLoopPassDraw 接管 _Process 时禁用 AutoRun，避免双启动冲突
            if (AutoLoopPassDraw) AutoRun = false;
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
            // ===== 【TEMPTEST】持续让过+抽牌循环（验收后随 AutoLoopPassDraw 一并移除） =====
            if (AutoLoopPassDraw)
            {
                if (_launcher == null)
                {
                    if (!_loopStarted) { _loopStarted = true; StartDemo(Port); } // 启动只尝试一次，避免反复报错
                    _loopTimer = 0;
                    return;
                }
                _loopTimer += delta;
                if (_loopTimer >= LoopIntervalSec)
                {
                    _loopTimer = 0;
                    // 让过回合需玩家1/玩家2 连续让过才推进阶段到 StartDraw（抽牌步骤）。
                    // 序列：P1让过 → P2让过 →（阶段推进）→ 每推进一次抽 1 张验证手牌增长。
                    switch (_loopStep)
                    {
                        case 0: _launcher.SendIntentAs(1, PlayerIntent.Kind.Pass); _loopStep = 1; GD.Print("[NetMatchDemo][TEMPTEST] P1 让过"); break;
                        case 1: _launcher.SendIntentAs(2, PlayerIntent.Kind.Pass); _loopStep = 2; GD.Print("[NetMatchDemo][TEMPTEST] P2 让过（阶段应推进）"); break;
                        default:
                            _launcher.SendIntentAs(1, PlayerIntent.Kind.DrawCards, "1");
                            _loopStep = 0; // 循环回起点
                            GD.Print("[NetMatchDemo][TEMPTEST] P1 抽牌（验证手牌增长）");
                            break;
                    }
                }
                return; // 循环模式接管 _Process，不走下方 AutoRun
            }

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
            // 快照差异驱动 Arena UI：客户端收到服务端快照时，经 SnapshotDiff→RenderCommand 增量驱动 Arena 刷新。
            // 单机模式 Arena 仍走 ZoneManager 事件总线；此处为联机演示叠加一层"快照差异渲染"入口。
            if (DriveArenaBySnapshot)
            {
                var arena = GetParent() as Arena ?? GetParent()?.GetParent() as Arena;
                if (arena == null)
                    GD.PushWarning("[NetMatchDemo] 未找到 Arena，快照差异渲染未接入");
                else
                {
                    _launcher.OnClientSnapshot += snap =>
                    {
                        arena.ApplySnapshot(snap); // SnapshotRenderDriver.Diff → ISnapshotView → Arena 现有刷新
                        GD.Print($"[NetMatchDemo] 快照差异已驱动 Arena（第 {snap.TurnCount} 回合，总卡数={snap.Zones.Count}）");
                    };
                    GD.Print("[NetMatchDemo] 已接入快照差异渲染：OnClientSnapshot → Arena.ApplySnapshot");
                }
            }
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
