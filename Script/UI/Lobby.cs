using Godot;

public partial class Lobby : Control
{
    private LineEdit _nameInput;
    private Button _createRoomBtn;
    private Button _backBtn;
    private Button _joinBtn; // 房间列表中的加入按钮

    public override void _Ready()
    {
        GD.Print("【Lobby】场景加载开始");

        // ---- 获取节点，每步检查 ----
        _nameInput = GetNode<LineEdit>("PlayerNameInput");
        if (_nameInput == null) GD.PrintErr("❌ Lobby: 未找到 PlayerNameInput");
        else GD.Print("✅ Lobby: PlayerNameInput 获取成功");

        _createRoomBtn = GetNode<Button>("RoomListContainer/CreateRoomButton");
        if (_createRoomBtn == null) GD.PrintErr("❌ Lobby: 未找到 CreateRoomButton");
        else GD.Print("✅ Lobby: CreateRoomButton 获取成功");

        _backBtn = GetNode<Button>("RoomListContainer/BackButton");
        if (_backBtn == null) GD.PrintErr("❌ Lobby: 未找到 BackButton");
        else GD.Print("✅ Lobby: BackButton 获取成功");

        _joinBtn = GetNode<Button>("RoomListContainer/RoomItem1/JoinButton1");
        if (_joinBtn == null) GD.PrintErr("❌ Lobby: 未找到 JoinButton");
        else GD.Print("✅ Lobby: JoinButton 获取成功");

        // ---- 加载已有玩家名 ----
        if (_nameInput != null && GameManager.Instance != null)
        {
            _nameInput.Text = GameManager.Instance.PlayerName;
            GD.Print($"📛 当前玩家名: {_nameInput.Text}");
            _nameInput.TextChanged += OnNameChanged;
        }

        // ---- 绑定按钮事件 ----
        if (_createRoomBtn != null) _createRoomBtn.Pressed += OnCreateRoom;
        if (_backBtn != null) _backBtn.Pressed += OnBack;
        if (_joinBtn != null) _joinBtn.Pressed += OnJoinRoom;

        GD.Print("【Lobby】初始化完成");
    }

    private void OnNameChanged(string newText)
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.PlayerName = newText;
            GD.Print($"📛 玩家名更新为: {newText}");
        }
    }

    private void OnCreateRoom()
    {
        GD.Print("【Lobby】点击创建房间");
        if (GameManager.Instance == null)
        {
            GD.PrintErr("❌ GameManager 未实例化");
            return;
        }

        string roomId = System.DateTime.Now.Ticks.ToString();
        GD.Print($"📌 生成房间ID: {roomId}");
        GameManager.Instance.InitRoom(roomId, GameManager.Instance.PlayerName);
        GD.Print($"📋 房间初始化完成，玩家列表: [{string.Join(", ", GameManager.Instance.RoomPlayers)}]");

        GD.Print("🔄 切换到房间场景...");
        GameManager.Instance.GoToState(GameManager.GameState.Room);
    }

    private void OnJoinRoom()
    {
        GD.Print("【Lobby】点击加入房间（模拟）");
        // 模拟加入，直接创建房间（实际可从列表选择）
        OnCreateRoom();
    }

    private void OnBack()
    {
        GD.Print("【Lobby】返回主菜单");
        GameManager.Instance.GoToState(GameManager.GameState.MainMenu);
    }
}