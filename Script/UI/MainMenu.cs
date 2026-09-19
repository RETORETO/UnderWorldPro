using Godot;

public partial class MainMenu : Control
{
    private Button _startButton;
    private Button _deckButton;
    private Button _quitButton;
    private Button _onlineButton;   // 【增量 B】联机对战（大厅页）

    public override void _Ready()
    {
        // 获取按钮引用（通过路径或 @Export 绑定）
        _startButton = GetNode<Button>("ButtonContainer/StartButton");
        _deckButton = GetNode<Button>("ButtonContainer/DeckButton");
        _quitButton = GetNode<Button>("ButtonContainer/QuitButton");
        // 联机入口：场景里可能尚未加该按钮（旧场景），故用 GetNodeOrNull 容错
        _onlineButton = GetNodeOrNull<Button>("ButtonContainer/OnlineButton");

        // 绑定按钮点击事件
        _startButton.Pressed += OnStartPressed;
        _deckButton.Pressed += OnDeckPressed;
        _quitButton.Pressed += OnQuitPressed;
        if (_onlineButton != null) _onlineButton.Pressed += OnOnlinePressed;

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