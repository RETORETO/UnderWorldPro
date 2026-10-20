using Godot;
using Script.IO;
using Script.Net.Godot;
using Script.UI;
using Script.UI.Online;

public partial class MainMenu : Control
{
    private Button _startButton;
    private Button _deckButton;
    private Button _quitButton;
    private Button _onlineButton;   // 【增量 B】联机对战（大厅页）
    private Button _hostButton;     // 【增量 D】建立主机（本进程内嵌服务端 + 自动建房）

    public override void _Ready()
    {
        // 【建立主机·增量 D】"回到首页 = 关闭本机主机"的唯一收口：
        // 首页既是本流程的唯一入口，也是"不再联机"的终点（覆盖大厅页「返回」、房间页离开后返回、退出前回首页等路径）。
        // 不放进 GameManager，避免 Manager → Net/Godot 的跨层依赖。
        if (LocalHostServer.IsRunning)
        {
            LocalHostServer.StopIfRunning();
            OnlineClientContext.Disconnect();
            GD.Print("【主菜单】返回首页：已关闭本机主机（端口已释放）");
        }

        // 获取按钮引用（通过路径或 @Export 绑定）
        _startButton = GetNode<Button>("ButtonContainer/StartButton");
        _deckButton = GetNode<Button>("ButtonContainer/DeckButton");
        _quitButton = GetNode<Button>("ButtonContainer/QuitButton");
        // 联机入口：场景里可能尚未加该按钮（旧场景），故用 GetNodeOrNull 容错
        _onlineButton = GetNodeOrNull<Button>("ButtonContainer/OnlineButton");
        // 建立主机入口：同样容错（旧场景/未加节点的场合不报错）
        _hostButton = GetNodeOrNull<Button>("ButtonContainer/HostButton");

        // 绑定按钮点击事件
        _startButton.Pressed += OnStartPressed;
        _deckButton.Pressed += OnDeckPressed;
        _quitButton.Pressed += OnQuitPressed;
        if (_onlineButton != null) _onlineButton.Pressed += OnOnlinePressed;
        if (_hostButton != null) _hostButton.Pressed += OnHostPressed;

        // 同步 GameManager 状态（已是 MainMenu 场景，GoToState 内部会跳过重载）
        if (GameManager.Instance != null)
            GameManager.Instance.GoToState(GameManager.GameState.MainMenu);

        // 【性能排查入口】惰性解析命令行开关（Web 上是 index.html 的 GODOT_CONFIG.args）。
        // 诊断阶段打的 eA/e0~e3/eB 等标记已删除（结论见 Script/IO/WebSafeText.cs 与 DevFlags.cs）。
        WebPerfParams.Ensure();
        WebPerfParams.DumpBanner();

        if (!string.IsNullOrEmpty(WebPerfParams.AutoEnter))
            ScheduleAutoEnter();

        // 【2026-10-19 UI 美化】统一到卡组编辑界面的暗色金主题。
        ApplyTheme();
    }

    /// <summary>应用统一主题：深色底 + 场景背景图 + 左侧皮革侧栏 + 金色标题 + 按钮统一样式。</summary>
    private void ApplyTheme()
    {
        // 【2026-10-20 背景】三层背景放进独立容器并压到 index 0，保证一定在标题/按钮之下：
        //   ① 纯深色底（图片加载失败时的兜底）  ② 全屏场景图（等比铺满裁切）  ③ 左侧皮革竖版侧栏。
        var backdrop = new Control
        {
            Name = "__MenuBackdrop",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);
        MoveChild(backdrop, 0);

        var baseRect = UITheme.BackgroundRect();
        backdrop.AddChild(baseRect);

        // 场景里预留的空 Background 节点（还带 custom_maximum_size=250×250）一律停用，避免和上面的背景层打架。
        if (GetNodeOrNull<TextureRect>("Background") is TextureRect legacyBg) legacyBg.Visible = false;

        // ② 全屏场景图：桌面4（用户 2026-10-20 指定换封面），压暗 0.58 保证标题与按钮可读
        //    （不追求"看清图"，只做氛围）。
        // 【2026-10-20 排查】用户反馈"首页背景没有替换成桌面4"：这里显式打一条日志，
        // 开一次游戏就能在日志里确认**到底加载的是哪张图**（若日志仍是旧内容 ⇒ 跑的是旧 DLL）。
        GD.Print("[MainMenu] 首页封面背景 = res://Cards/Image/桌面4.jpg（压暗 0.58）");
        var cover = BackdropHelper.AttachCover(backdrop, "res://Cards/Image/桌面4.jpg", 0.58f);
        if (cover == null)
            GD.PrintErr("[MainMenu] ⚠ 首页封面图加载失败：res://Cards/Image/桌面4.jpg（只剩纯色底，需检查导入缓存）");

        // ③ 左侧皮革侧栏：`牌手信息.jpg` 是 300×900（0.333），铺 360×1080 时缩放系数恰为 1.2，
        //    纵向正好铺满、横向零裁切 ⇒ 底部「隐秘世界」压印 LOGO 完整可见。
        var spine = new TextureRect
        {
            Name = "__MenuSpine",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        backdrop.AddChild(spine);
        BackdropHelper.MakeCover(spine, BackdropHelper.LeatherPanel, 0.22f);
        spine.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        spine.OffsetRight = 360f;
        spine.OffsetLeft = 0f;

        var title = GetNodeOrNull<Label>("Title");
        if (title != null)
        {
            title.Text = "UWPro";
            title.HorizontalAlignment = HorizontalAlignment.Center;
            UITheme.ApplyTitle(title, 56);
            // 金色标题压在图片上，需要描边才不糊
            title.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
            title.AddThemeConstantOverride("outline_size", 8);
        }

        // 【2026-10-20 用户口径】首页按钮尺寸必须完全统一：之前只有「单机测试模式 / 编辑卡组 / 退出游戏」
        // 在场景里写了 custom_minimum_size=(0,50)，而「联机对战 / 建立主机」没写 ⇒ 后两个矮一截。
        // 这里对所有按钮统一给同一尺寸，不再依赖 .tscn 里各自的写法。
        const float btnHeight = 52f;
        foreach (Button btn in new[] { _startButton, _onlineButton, _hostButton, _deckButton, _quitButton })
        {
            if (btn == null) continue;
            btn.CustomMinimumSize = new Vector2(0f, btnHeight);
            btn.CustomMaximumSize = new Vector2(-1f, btnHeight);
        }

        if (_startButton != null) UITheme.ApplyPrimaryButton(_startButton);
        if (_onlineButton != null) UITheme.ApplyPrimaryButton(_onlineButton);
        if (_hostButton != null) UITheme.ApplyPrimaryButton(_hostButton);
        if (_deckButton != null) UITheme.ApplyPrimaryButton(_deckButton);
        if (_quitButton != null) UITheme.ApplySecondaryButton(_quitButton);
    }

    /// <summary>
    /// 【性能排查】自动进入卡组编辑用的剩余帧数；-1 = 未启用。
    ///
    /// 为什么按帧计数而不是 `GetTree().CreateTimer()`：
    /// 本工程此前**从未调用过** `CreateTimer` / `ToSignal`（那些写法只出现在玩法代码里、
    /// 未必真的执行过），属于"未经验证"的接口。已被验证的是**每个 Node 都会走的基础回调**
    /// （`_Ready`/`_Process`），所以这里只用最基础的机制，避免再引入新的崩点。
    /// </summary>
    private int _autoEnterFramesLeft = -1;

    /// <summary>【诊断】自动进入的目标状态（由 `--wp-auto=` 解析而来）。</summary>
    private GameManager.GameState _autoTargetState = GameManager.GameState.DeckBuilder;

    /// <summary>
    /// 【联机排查·2026-10-13】`--wp-auto=` 的非场景动作：
    ///   · `host`   = 等价于点首页「建立主机」（走 `OnHostPressed`，含起内嵌服务端 + 自动建房）；
    ///   · `online` = 进联机大厅并**自动连接**（地址/端口/昵称来自 `--wp-host/--wp-port/--wp-name`）。
    /// 空串表示走普通的"切到某个场景"路径（见 <see cref="TryParseAutoTarget"/>）。
    /// </summary>
    private string _autoAction = "";

    /// <summary>
    /// 【诊断】把 `--wp-auto=` 的取值映射到 GameState。
    /// 只用 `StringComparison.OrdinalIgnoreCase`（不走 ICU），未知目标返回 false。
    /// 支持任意场景是为了做「换一个场景是否也崩」的决定性对照：
    /// 若连最简单的 Lobby 也在首帧 abort，说明崩点在「场景切换」本身而不是 DeckBuilder 的内容。
    /// </summary>
    private static bool TryParseAutoTarget(string s, out GameManager.GameState st)
    {
        st = GameManager.GameState.DeckBuilder;
        if (string.Equals(s, "deck", System.StringComparison.OrdinalIgnoreCase)
         || string.Equals(s, "deckbuilder", System.StringComparison.OrdinalIgnoreCase)) { st = GameManager.GameState.DeckBuilder; return true; }
        if (string.Equals(s, "lobby", System.StringComparison.OrdinalIgnoreCase)) { st = GameManager.GameState.Room; return true; }   // 单机无大厅，直接进房间
        if (string.Equals(s, "lobbyonline", System.StringComparison.OrdinalIgnoreCase)) { st = GameManager.GameState.LobbyOnline; return true; }
        if (string.Equals(s, "room", System.StringComparison.OrdinalIgnoreCase)) { st = GameManager.GameState.Room; return true; }
        if (string.Equals(s, "roomonline", System.StringComparison.OrdinalIgnoreCase)) { st = GameManager.GameState.RoomOnline; return true; }
        if (string.Equals(s, "mainmenu", System.StringComparison.OrdinalIgnoreCase)) { st = GameManager.GameState.MainMenu; return true; }
        return false;
    }

    public override void _Process(double delta)
    {
        if (_autoEnterFramesLeft < 0) return;
        if (_autoEnterFramesLeft-- > 0) return;
        _autoEnterFramesLeft = -1;

        if (!IsInsideTree()) return;                    // 期间场景已切换就放弃
        if (GameManager.Instance == null)
        {
            GD.PrintErr("[WebPerf] 自动进入失败：GameManager 未就绪");
            return;
        }

        // 【联机排查】非场景动作优先：`--wp-auto=host` / `--wp-auto=online`
        if (_autoAction == "host")
        {
            GD.Print("[WebPerf] 自动动作：建立主机（等价于点首页「建立主机」）");
            OnHostPressed();
            return;
        }
        if (_autoAction == "online")
        {
            string host = string.IsNullOrEmpty(WebPerfParams.NetHost) ? Script.IO.OnlineDefaults.Host : WebPerfParams.NetHost;
            string name = string.IsNullOrEmpty(WebPerfParams.NetName) ? "玩家" : WebPerfParams.NetName;
            GD.Print($"[WebPerf] 自动动作：联机大厅自动连接 host={host} port={WebPerfParams.NetPort}");
            OnlineClientContext.AutoConnect = true;     // 进大厅页后自动点「连接」
            OnlineClientContext.SmokeMode = WebPerfParams.Smoke;   // 可选：连上后自动建房进房间
            OnlineClientContext.SmokeIsHost = !WebPerfParams.SmokeJoin;   // --wp-join：改为加入首个房间（走「加入」按钮那条路）
            OnlineClientContext.LastHost = host;
            OnlineClientContext.LastPort = (ushort)WebPerfParams.NetPort;
            OnlineClientContext.LastName = name;
            GameManager.Instance.GoToState(GameManager.GameState.LobbyOnline);
            return;
        }

        GD.Print("[WebPerf] 自动进入：" + _autoTargetState.ToString());
        if (_autoTargetState == GameManager.GameState.DeckBuilder)
            GameManager.Instance.EditingDeckPath = null; // 新建卡组，与点「编辑卡组」一致
        GameManager.Instance.GoToState(_autoTargetState);
    }

    /// <summary>
    /// 【诊断】由运行时开关 `--wp-auto=deck` 触发自动进入，省掉无头浏览器里模拟点击的坐标问题。
    /// 这里只做登记，真正的进入在 <see cref="_Process"/> 里按帧倒计时执行。
    ///
    /// 为什么按帧计数而不是 `GetTree().CreateTimer()`：
    /// 本工程此前**从未调用过** `CreateTimer` / `ToSignal`（那些写法只出现在玩法代码里、
    /// 未必真的执行过），属于"未经验证"的接口。已被验证的是**每个 Node 都会走的基础回调**
    /// （`_Ready`/`_Process`），所以这里只用最基础的机制，避免再引入新的崩点。
    /// </summary>
    private void ScheduleAutoEnter()
    {
        // 【为什么不用 ToLowerInvariant】
        // 曾在这里用 `WebPerfParams.AutoEnter.ToLowerInvariant()`，Web 上崩在它上面
        //（会走 .NET 全球化/ICU 路径，见 WebSafeText 的类头说明）。
        // 改用纯序数比较 StringComparison.OrdinalIgnoreCase（不需要 ICU 数据）。
        string target = WebPerfParams.AutoEnter;

        // 【联机排查】非场景动作：与点按钮走**同一个**处理函数，保证实验测的就是真实路径。
        if (string.Equals(target, "host", System.StringComparison.OrdinalIgnoreCase))
        {
            _autoAction = "host";
            int f0 = (int)(WebPerfParams.AutoDelay * 60f);
            _autoEnterFramesLeft = f0 < 1 ? 1 : f0;
            return;
        }
        if (string.Equals(target, "online", System.StringComparison.OrdinalIgnoreCase))
        {
            _autoAction = "online";
            int f1 = (int)(WebPerfParams.AutoDelay * 60f);
            _autoEnterFramesLeft = f1 < 1 ? 1 : f1;
            return;
        }

        bool ok = TryParseAutoTarget(target, out _autoTargetState);
        if (!ok)
        {
            GD.PrintErr($"[WebPerf] 未知的 auto 目标：{target}（支持 deck/lobby/lobbyonline/room/roomonline/mainmenu/host/online）");
            return;
        }

        int frames = (int)(WebPerfParams.AutoDelay * 60f);
        if (frames < 1) frames = 1;
        _autoEnterFramesLeft = frames;
    }

    private void OnStartPressed()
    {
        GD.Print("点击单机测试模式，直接进入房间");
        // 【2026-10-19 用户口径】单机不再经过大厅：直接初始化单人房间并进入房间。
        // Room._Ready 本身也有兜底（RoomPlayers 为空时自动 InitRoom），这里显式建一个干净房间。
        var gm = GameManager.Instance;
        if (gm == null) return;
        string roomId = System.DateTime.Now.Ticks.ToString();
        gm.InitRoom(roomId, string.IsNullOrEmpty(gm.PlayerName) ? "玩家" : gm.PlayerName);
        gm.GoToState(GameManager.GameState.Room);
    }

    /// <summary>【增量 B】进入联机大厅页（在线专用场景；单机「开始游戏」流程不受影响）。</summary>
    private void OnOnlinePressed()
    {
        GD.Print("【主菜单】进入联机大厅");
        GameManager.Instance.GoToState(GameManager.GameState.LobbyOnline);
    }

    /// <summary>
    /// 【增量 D】建立主机：**本进程内**起内嵌大厅服务端，再让本进程的客户端连自己并**自动创建一个房间**，
    /// 从而"单一程序实例同时承担服务端与客户端"；对手在另一台机器用「联机对战」填本机局域网 IPv4 即可加入（见 D12/D13/D14）。
    /// </summary>
    private void OnHostPressed()
    {
        if (GameManager.Instance == null)
        {
            GD.PrintErr("【主菜单】建立主机失败：GameManager 未就绪");
            return;
        }

        // 防御：确保从"干净的一条连接"开始（正常情况下回首页时已断开）
        if (OnlineClientContext.HasSession) OnlineClientContext.Disconnect();

        if (!LocalHostServer.Start(out var error))
        {
            GD.PrintErr($"【主菜单】建立主机失败：{error}");
            ShowTempMessage($"建立主机失败：{error}");
            return;
        }
        GD.Print("[主菜单] n2 LocalHostServer.Start 成功");

        OnlineClientPrefs.EnsureLoaded();
        OnlineClientContext.HostAutoFlow = true;      // 一次性：进大厅页后自动连接并建房（消费即清零）
        OnlineClientContext.LastHost = "127.0.0.1";   // 本机客户端连自己起的内嵌服务端
        OnlineClientContext.LastPort = LocalHostServer.Port;
        OnlineClientContext.LastName = Script.IO.WebSafeText.IsNullOrWhiteSpaceAscii(OnlineClientPrefs.LastName) ? "主机玩家" : OnlineClientPrefs.LastName;
        GD.Print($"【主菜单】已建立主机：端口 {LocalHostServer.Port}；对手可连接 {LocalHostServer.AddressHint(LocalHostServer.Port)}"
            + "（对手在「联机对战」填上面的地址即可；若连不上：Windows 防火墙需放行该 TCP 端口入站）");
        GameManager.Instance.GoToState(GameManager.GameState.LobbyOnline);
    }

    private void OnDeckPressed()
    {
        GD.Print("【主菜单】进入卡组编辑");
        GameManager.Instance.EditingDeckPath = null; // 新建卡组
        GameManager.Instance.GoToState(GameManager.GameState.DeckBuilder);
    }

    private void OnQuitPressed()
    {
        GD.Print("退出游戏");
        GetTree().Quit();
    }

    // 临时提示方法（可用于未完成功能）
    private void ShowTempMessage(string message)
    {
        // 简单实现：创建一个 Label 显示几秒后消失
        var label = new Label
        {
            Text = message,
            Position = new Vector2(100, 100),
            Size = new Vector2(300, 50)
        };
        AddChild(label);
        // 使用 Timer 自动移除
        var timer = new Timer();
        timer.WaitTime = 2.0f;
        timer.OneShot = true;
        timer.Timeout += () =>
        {
            label.QueueFree();
            timer.QueueFree();
        };
        AddChild(timer);
        timer.Start();
    }
}