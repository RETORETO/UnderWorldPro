using Godot;

/// <summary>
/// Web 性能/诊断参数 —— 从 `index.html` 的 `GODOT_CONFIG.args` 读运行时开关。
///
/// 【为什么做成运行时开关】
/// 改一次开关不用重新导出（导出一次约 1~2 分钟，浏览器实跑约 1 分钟）。
/// 解析只在最早的自加载（<see cref="WebFontFallback"/>）与 <see cref="MainMenu"/> 里各调一次
/// （`Ensure()` 幂等），之后只读。
///
/// 【2026-10-12 更正】本类曾把 `OS.GetCmdlineArgs()` 判为"Web 上会 abort"并被清空成纯常量。
/// 逐句埋点后已证实**该调用完全正常**（实测打印 "项数=0"、Parse 跑完）；
/// 当时真正的崩点是 `String.StartsWith(string)` 与**负整数/浮点格式化**触发的 .NET 全球化路径
/// （见 <see cref="Script.IO.WebSafeText"/> 的类头说明）。所以本类恢复读运行时开关。
///
/// 【安全兜底】解析全部包在 try/catch 里，任何异常都退化为编译期默认值（<see cref="DevFlags"/>），
/// 因此引擎接口若变化，最多只是"开关不生效"，不会影响游戏本身。
/// </summary>
public static class WebPerfParams
{
    private static bool _parsed;
    private static string[] _args = System.Array.Empty<string>();

    // —— 编译期默认值（DevFlags）：没有命令行开关时用它们 ——
    private static bool _enabled = DevFlags.VerbosePerf;
    private static bool _frameProbe = DevFlags.FrameProbe;
    private static bool _skipTextures;
    private static bool _forceSync;
    private static bool _progressiveTextures = true;
    private static bool _noWordWrap;
    private static bool _skipPoolCards;
    private static int _pageSizeOverride = -1;
    private static string _autoEnter = DevFlags.AutoEnterDeck ? "deck" : "";
    /// <summary>自动进入前的等待秒数（单位：秒，默认 1.2s = 72 帧）。</summary>
    private static float _autoDelay = 1.2f;
    /// <summary>【联机排查】在 Web 上也照常调用 ENet（复现"点联机即卡死"的旧路径，对照用）。</summary>
    private static bool _netRaw;
    /// <summary>【联机】桌面端也强制用 WebSocket 传输（默认桌面走 ENet）。</summary>
    private static bool _webSocket;
    /// <summary>【联机排查】连上大厅后自动走一遍"建房 → 进房间"（等价于原有 `--smoke` 冒烟模式）。</summary>
    private static bool _smoke;
    /// <summary>【联机排查】冒烟时**加入**首个房间而不是自己建房（`--wp-join`）。</summary>
    private static bool _smokeJoin;
    /// <summary>【联机排查】`--wp-auto=online` 时要连的服务端地址/端口/昵称。</summary>
    private static string _netHost = "127.0.0.1";
    private static int _netPort = 18950;
    private static string _netName = "";

    /// <summary>
    /// 【必须在任何读取之前调用一次】解析 index.html 的 GODOT_CONFIG.args。
    /// 目前在最早上树的自加载里调一次（避免更早的阶段读引擎接口），MainMenu 里再调一次（幂等）。
    /// </summary>
    public static void Ensure()
    {
        if (_parsed) return;
        _parsed = true;

        try
        {
            string[] a = OS.GetCmdlineArgs();
            if (a != null) { _args = a; Parse(); }
        }
        catch (System.Exception e)
        {
            // 退化为编译期默认值即可，不影响游戏
            GD.PrintErr("[WebPerf] 读取命令行开关失败（已退化为编译期默认值）: " + e.Message);
        }
    }

    private static void Parse()
    {
        foreach (string raw in _args)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            // 【必须用序数比较】单参数 StartsWith 在 Web 上会 abort（见 WebSafeText）。
            if (!raw.StartsWith("--wp-", System.StringComparison.Ordinal)) continue;

            string s = raw.Substring(5);            // 去掉 "--wp-"
            string key = s, val = "";
            int eq = s.IndexOf('=');
            if (eq >= 0) { key = s.Substring(0, eq); val = s.Substring(eq + 1); }

            switch (Script.IO.WebSafeText.ToLowerAscii(key))
            {
                case "perf":     _enabled = true; break;
                case "noperf":   _enabled = false; break;
                case "probe":    _frameProbe = true; break;
                case "noprobe":  _frameProbe = false; break;
                case "notex":    _skipTextures = true; break;   // E2：完全不加载卡图
                case "sync":     _forceSync = true; break;      // 回到旧的同步加载路径
                case "prog":     _progressiveTextures = true; break;
                case "nowrap":   _noWordWrap = true; break;     // 禁用智能换行
                case "nocards":  _skipPoolCards = true; break;  // 连卡牌面板都不建
                case "pagesize": _pageSizeOverride = ParseInt(val, -1); break;
                case "delayms":  _autoDelay = ParseInt(val, 1200) / 1000f; break;  // 自动进入的延迟（毫秒）
                case "auto":     _autoEnter = val; break;       // 目标场景：deck/lobby/room/host/online
                // —— 联机排查（2026-10-13）——
                case "netraw":   _netRaw = true; break;         // Web 上也照常调 ENet（复现旧崩溃）
                case "ws":       _webSocket = true; break;      // 桌面也用 WebSocket 传输
                case "smoke":    _smoke = true; break;          // 连上大厅后自动建房进房间
                case "join":     _smokeJoin = true; break;      // 冒烟时改为「加入首个房间」（=点「加入」按钮）
                case "host":     _netHost = val; break;         // 连接目标地址
                case "port":     _netPort = ParseInt(val, 18950); break;
                case "name":     _netName = val; break;
                default:
                    GD.PushWarning("[WebPerf] 未知开关，已忽略: " + raw);
                    break;
            }
        }
    }

    private static int ParseInt(string s, int def)
    {
        // 纯手工解析，避免任何全球化/格式解析路径
        if (string.IsNullOrEmpty(s)) return def;
        int v = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') return def;
            v = v * 10 + (c - '0');
        }
        return v;
    }

    // ================= 对外只读属性 =================
    /// <summary>是否输出 [PERF] 计时日志。</summary>
    public static bool Enabled => _enabled;
    /// <summary>是否启用首帧探针。⚠️ 见 <see cref="DevFlags.FrameProbe"/> 的警告。</summary>
    public static bool FrameProbe => _frameProbe;
    /// <summary>每页渲染多少张（-1 = 用代码里的默认值）。</summary>
    public static int PageSizeOverride => _pageSizeOverride;
    /// <summary>E2：完全不加载纹理，用来量"摘掉纹理后还剩多少耗时"。</summary>
    public static bool SkipTextures => _skipTextures;
    /// <summary>是否分帧渐进填图（默认 true）。</summary>
    public static bool ProgressiveTextures => _progressiveTextures;
    /// <summary>强制同步加载（对照用）。</summary>
    public static bool ForceSync => _forceSync;
    /// <summary>用 Arbitrary（按字符换行）替代 WordSmart（按词智能换行）。</summary>
    public static bool NoWordWrap => _noWordWrap;
    /// <summary>连卡牌面板都不创建。</summary>
    public static bool SkipPoolCards => _skipPoolCards;
    /// <summary>自动进入的目标场景名（"deck"/"lobby"/…；空 = 不自动进入）。</summary>
    public static string AutoEnter => _autoEnter;
    /// <summary>自动进入前的等待秒数（默认 1.2s；`--wp-delayms=N` 可改）。</summary>
    public static float AutoDelay => _autoDelay;
    /// <summary>Web 上也照常调用 ENet（`--wp-netraw`；仅用于复现旧崩溃做对照）。</summary>
    public static bool NetRaw => _netRaw;
    /// <summary>桌面端也强制用 WebSocket 传输（`--wp-ws`）；Web 端恒为 true（不受此开关影响）。</summary>
    public static bool WebSocket => _webSocket;
    /// <summary>连上大厅后自动建房进房间（`--wp-smoke`；仅用于无点击实测）。</summary>
    public static bool Smoke => _smoke;
    /// <summary>【联机排查】冒烟时加入首个房间（`--wp-join`），而不是自己建房。</summary>
    public static bool SmokeJoin => _smokeJoin;
    /// <summary>`--wp-auto=online` 的目标地址 / 端口 / 昵称。</summary>
    public static string NetHost => _netHost;
    public static int NetPort => _netPort;
    public static string NetName => _netName;
    /// <summary>已解析到的命令行项数（诊断用）。</summary>
    public static int ArgCount => _args.Length;
    /// <summary>已解析（诊断用）。</summary>
    public static bool Ok => _parsed;

    /// <summary>
    /// 把生效的开关打一行出来，便于对照日志。
    ///
    /// 【2026-10-12 定版写法】逐 token 二分已把当时的崩点钉死在「**格式化负整数**」上
    /// （正数 1200 正常、负数 -1 立刻 Aborted）。
    /// 所以这里所有数字一律走 <see cref="Script.IO.WebSafeText.Int"/>，永远不碰 NumberFormatInfo；
    /// 布尔也不做插值，直接三目成 "1"/"0"，把「值 -> 字符串」的所有可能文化路径全部掐掉。
    /// </summary>
    public static void DumpBanner()
    {
        GD.Print("[WebPerf] 开关 enabled=" + (_enabled ? "1" : "0")
               + " probe=" + (_frameProbe ? "1" : "0")
               + " auto='" + (_autoEnter ?? "") + "'"
               + " delayms=" + Script.IO.WebSafeText.Int((int)(_autoDelay * 1000f))
               + " notex=" + (_skipTextures ? "1" : "0")
               + " nowrap=" + (_noWordWrap ? "1" : "0")
               + " nocards=" + (_skipPoolCards ? "1" : "0")
               + " sync=" + (_forceSync ? "1" : "0")
               + " prog=" + (_progressiveTextures ? "1" : "0")
               + " pagesize=" + Script.IO.WebSafeText.Int(_pageSizeOverride)
               + " netraw=" + (_netRaw ? "1" : "0")
               + " ws=" + (_webSocket ? "1" : "0")
               + " smoke=" + (_smoke ? "1" : "0")
               + " join=" + (_smokeJoin ? "1" : "0")
               + " argc=" + Script.IO.WebSafeText.Int(_args.Length));
    }

    public static void Log(string msg) { if (_enabled) GD.Print("[PERF] " + msg); }
    public static void Summary(string msg) { if (_enabled) GD.Print("[PERF] " + msg); }
}
