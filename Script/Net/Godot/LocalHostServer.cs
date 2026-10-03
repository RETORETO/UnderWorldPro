using System;
using System.Collections.Generic;
using Godot;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 增量 D】**本进程内嵌大厅服务端**（"建立主机"的服务端侧）。
    ///
    /// 定位（决策 D12）：房主点首页「建立主机」时，服务端就在**本进程内**装配，不拉起子进程：
    ///   · 开发运行（mono 版 Godot + --path）与导出后的 EXE **同一套代码路径**，无需定位外部可执行文件、无需传 res:// 场景参数；
    ///   · 无子进程残留、无孤儿端口、无需为子进程隔离 `user://` 目录（多进程共用用户目录会崩，见 `start_online_3.ps1` 说明）；
    ///   · 退出进程即释放端口。
    /// 与 D1 的关系：D1"服务端 = 独立 headless 进程"仍适用于**专用服务器/多机部署**（`NetServer.tscn`、`NetMatchEntry --server`、
    /// `启动大厅服务端.bat` 全部不变）；本类只承载"房主在自己机器上开一局"，两者并存。
    ///
    /// 复用而非复制：服务端装配仍由既有 `LobbyServerHost`（与 headless 服务端同一个类）完成；本类只负责
    /// "挂到场景树 / 选端口 / 关掉它"。同进程内"真实 ENet 服务端 + 真实客户端会话"已被 `LobbyFlowTests` 证明可行。
    ///
    /// 纪律：本文件位于 `Net/Godot/`（Godot 桥接层），不污染 `Net/` 根目录的纯托管性。
    /// </summary>
    public static class LocalHostServer
    {
        /// <summary>默认起始端口（与 `ServerConfig.Port` 的 D8 默认值一致）。</summary>
        public const ushort DefaultBasePort = 18950;
        /// <summary>端口尝试次数：18950→18959，占用则顺延（见 D13）。</summary>
        public const int DefaultAttempts = 10;

        /// <summary>内嵌服务端节点名（便于在远程场景树/日志里辨认；也保证同层节点名唯一）。</summary>
        public const string EmbeddedNodeName = "EmbeddedLobbyServer";

        private static LobbyServerHost _host;

        /// <summary>本进程是否正在托管服务端。</summary>
        public static bool IsRunning => _host != null && GodotObject.IsInstanceValid(_host) && _host.Started;

        /// <summary>当前监听的端口（未托管时为 0）。</summary>
        public static ushort Port { get; private set; }

        /// <summary>内嵌服务端节点（诊断/测试用；`NetMatchEntry` 已有"显式调 `_Process`"的先例）。未托管时为 null。</summary>
        public static LobbyServerHost Host => _host;

        /// <summary>
        /// 启动内嵌服务端（幂等：已在运行则直接成功）。
        /// 在 <paramref name="basePort"/> 起逐个端口尝试绑定，首个成功者生效；全部失败则返回 false 并给出可读原因。
        /// </summary>
        public static bool Start(out string error, ushort basePort = DefaultBasePort, int attempts = DefaultAttempts)
        {
            error = null;
            if (IsRunning) return true;                       // 幂等：重复点「建立主机」不会重复占端口

            var parent = ResolveParent();
            if (parent == null)
            {
                error = "场景树不可用，无法挂载内嵌服务端";
                GD.PrintErr($"[LocalHostServer] 建立主机失败：{error}");
                return false;
            }

            int tries = attempts > 0 ? attempts : 1;
            for (int i = 0; i < tries; i++)
            {
                int candidate = basePort + i;
                if (candidate > 65535) break;

                // 每轮用**新节点**试：LobbyServerHost.Start 内部已把"端口占用"转成"返回 false + 打印原因"，不抛异常。
                var host = new LobbyServerHost { Name = EmbeddedNodeName };
                parent.AddChild(host);
                if (!host.Start(new ServerConfig { Port = (ushort)candidate }))
                {
                    DetachAndFree(host);                      // 失败端口：立刻摘除，避免留下半装配节点
                    continue;
                }

                _host = host;
                Port = (ushort)candidate;
                GD.Print($"[LocalHostServer] 本机主机已就绪：端口 {Port}；对手可连接 {AddressHint(Port)}");
                return true;
            }

            error = $"端口 {basePort}–{basePort + tries - 1} 都被占用或无法监听（请关闭占用该端口的程序后重试）";
            GD.PrintErr($"[LocalHostServer] 建立主机失败：{error}");
            return false;
        }

        /// <summary>关闭内嵌服务端（未托管时为无操作）。关闭后端口**同步**释放，可立刻再次 `Start`。</summary>
        public static void Stop()
        {
            var host = _host;
            _host = null;
            Port = 0;
            if (host == null) return;
            if (!GodotObject.IsInstanceValid(host)) return;
            DetachAndFree(host);
            GD.Print("[LocalHostServer] 本机主机已关闭（端口已释放）");
        }

        /// <summary>仅在托管时关闭（供"回到首页"等收口点使用，避免各处重复判空）。</summary>
        public static void StopIfRunning()
        {
            if (_host != null || Port != 0) Stop();
        }

        /// <summary>本机候选局域网 IPv4（排除回环、IPv6 与链路本地 169.254.*）；与 `LobbyServerHost.PrintClientHint` 同口径。</summary>
        public static List<string> LanIPv4()
        {
            var list = new List<string>();
            foreach (var addr in IP.GetLocalAddresses())
            {
                if (string.IsNullOrEmpty(addr)) continue;
                if (addr == "127.0.0.1" || addr.Contains(':')) continue;   // 只取 IPv4
                if (addr.StartsWith("169.254.")) continue;                 // 链路本地：对手用不上
                list.Add(addr);
            }
            return list;
        }

        /// <summary>给对手看的连接地址提示（多网卡时列前 3 个，避免提示过长）。</summary>
        public static string AddressHint(ushort port)
        {
            var lan = LanIPv4();
            if (lan.Count == 0) return $"（未发现局域网 IPv4，请用 ipconfig 查看本机地址）:{port}";
            int n = Math.Min(lan.Count, 3);
            var parts = new List<string>();
            for (int i = 0; i < n; i++) parts.Add($"{lan[i]}:{port}");
            return string.Join(" 或 ", parts);
        }

        // ==================== 内部 ====================

        /// <summary>
        /// 内嵌服务端的宿主：优先挂在 `GameManager`（autoload，随进程存活、不随切场景被释放；且其子树每帧会被引擎驱动
        /// `LobbyServerHost._Process`，从而轮询 ENet 与房间超时）；极端情况下退回场景树根。
        /// </summary>
        private static Node ResolveParent()
        {
            if (GameManager.Instance != null && GodotObject.IsInstanceValid(GameManager.Instance)) return GameManager.Instance;
            return (Engine.GetMainLoop() as SceneTree)?.Root;
        }

        /// <summary>
        /// 从树上摘除并**立即**释放（不用 `QueueFree`）。
        /// 为什么必须立即：`LobbyServerHost._ExitTree` → `ServerHost.Stop()` → ENet peer 关闭才同步释放 UDP 端口；
        /// `QueueFree` 要等帧末，会让"关掉后马上再建立主机"落到下一个端口，也让单测无法确定性地验证端口复用。
        /// 先 `RemoveChild` 再 `Free` 与 `ServerMatchFactory.FreeGameNode` 的"未在 owner 下 → Free"同构（已脱离树，无 busy-parent 风险）。
        /// </summary>
        private static void DetachAndFree(LobbyServerHost host)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;
            var parent = host.GetParent();
            if (parent != null) parent.RemoveChild(host);
            host.Free();
        }
    }
}
