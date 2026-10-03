using Godot;
using Script.IO;
using Script.Net.Godot;
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
    }

    private void OnStartPressed()
    {
        GD.Print("点击开始游戏，切换到大厅");
        // 统一经 GameManager（应用级 Controller）流转场景（问题 3 修复）
        GameManager.Instance.GoToState(GameManager.GameState.Lobby);
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

        OnlineClientPrefs.EnsureLoaded();
        OnlineClientContext.HostAutoFlow = true;      // 一次性：进大厅页后自动连接并建房（消费即清零）
        OnlineClientContext.LastHost = "127.0.0.1";   // 本机客户端连自己起的内嵌服务端
        OnlineClientContext.LastPort = LocalHostServer.Port;
        OnlineClientContext.LastName = string.IsNullOrWhiteSpace(OnlineClientPrefs.LastName) ? "主机玩家" : OnlineClientPrefs.LastName;
        GD.Print($"【主菜单】已建立主机：端口 {LocalHostServer.Port}；对手可连接 {LocalHostServer.AddressHint(LocalHostServer.Port)}"
            + "（若对手连不上：Windows 防火墙需放行该 UDP 端口入站）");
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