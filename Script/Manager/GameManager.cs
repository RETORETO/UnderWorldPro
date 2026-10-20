// GameManager.cs
using Godot;
using System.Collections.Generic;
using Script.IO;

public partial class GameManager : Node
{
    public static GameManager Instance { get; private set; }  // 添加这一行

    /// <summary>
    /// 应用级场景状态。`LobbyOnline`/`RoomOnline`/`OnlineArena` 为**联机专用**（增量 B 追加，不动单机语义）：
    /// 大厅页 → 房间页 → 联机战场页。
    /// 【2026-10-19 用户口径】单机不再有独立大厅：`MainMenu → Room`（单人直接进房间），故移除 `Lobby`。
    /// </summary>
    public enum GameState { MainMenu, Room, Arena, DeckBuilder, LobbyOnline, RoomOnline, OnlineArena }
    public GameState CurrentState { get; private set; }

    // 状态 → 场景路径映射（唯一真相源，避免散落在各 View）
    private static readonly Dictionary<GameState, string> ScenePaths = new()
    {
        { GameState.MainMenu,    "res://Scenes/MainMenu.tscn" },
        { GameState.Room,        "res://Scenes/Room.tscn" },
        { GameState.Arena,       "res://Scenes/Arena/Arena.tscn" },
        { GameState.DeckBuilder,  "res://Scenes/DeckBuilder.tscn" },
        // 联机专用场景（新建；单机场景未被改动）
        { GameState.LobbyOnline, "res://Scenes/Net/LobbyOl.tscn" },
        { GameState.RoomOnline,  "res://Scenes/Net/RoomOl.tscn" },
        { GameState.OnlineArena, "res://Scenes/Net/OnlineArena.tscn" },
    };

    public override void _Ready()
    {
        Instance = this;   // 赋值

        // 【2026-10-20 注意】**不要**在这里把 `user://online_client.cfg` 的"上次卡组"灌进
        // SelectedDeckPath / UseSelectedDeck。实测那样做会改变"单机开局默认卡组"的来源
        //（由硬编码卡组变成玩家上次选的 JSON 卡组），一次回归就跑出 51 条与本需求无关的
        // 门禁/触发类失败（TestP3_隐匿_*、TestP2Discard_*、TestCouncilActivation_* 等）。
        // 卡组默认值的解析放在**界面层**（DeckBuilder / Room），见 Script.IO.DeckSelection。

        // 启动时仅同步枚举，不主动切场景（场景树根已是 MainMenu）
        CurrentState = GameState.MainMenu;
        GD.Print($"游戏状态初始化: {CurrentState}");
    }

    /// <summary>
    /// 应用级状态切换：更新枚举并统一切换场景。
    /// 各 UI 只调用本方法请求流转，不再自行 ChangeSceneToFile（问题 3 修复）。
    /// </summary>
    public void GoToState(GameState newState)
    {
        CurrentState = newState;
        GD.Print($"游戏状态切换: {newState}");

        if (!ScenePaths.TryGetValue(newState, out string path) || string.IsNullOrEmpty(path))
        {
            GD.Print($"[GameManager] 状态 {newState} 暂无对应场景，仅更新枚举");
            return;
        }
        // 避免重复切到当前已加载的场景（如启动时已是 MainMenu）
        if (GetTree().CurrentScene?.SceneFilePath == path)
        {
            GD.Print($"[GameManager] 已在场景 {path}，跳过重载");
            return;
        }
        GetTree().ChangeSceneToFile(path);
    }

    /// <summary>
    /// 【增量 B】延迟切换场景：**在网络回调 / 节点处理 / 场景加载过程中请求切场景时必须用它**。
    /// 原因：`ChangeSceneToFile` 会在当前帧的节点处理期间立即增删子节点，Godot 会报
    /// "Parent node is busy adding/removing children" 并可能抛 `ObjectDisposedException`。
    /// 实现：把 defer 挂在 **SceneTree** 上（比挂在 Node 上更晚、更安全），并先行更新状态枚举。
    /// </summary>
    public void GoToStateDeferred(GameState newState)
    {
        CurrentState = newState;
        if (!ScenePaths.TryGetValue(newState, out string path) || string.IsNullOrEmpty(path))
        {
            GD.Print($"[GameManager] 状态 {newState} 暂无对应场景，仅更新枚举");
            return;
        }
        if (GetTree().CurrentScene?.SceneFilePath == path) return;   // 已在该场景：不重复切换
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, path);
    }

    /*
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && key.Keycode == (Key)96)
        {
            string command = "draw 1"; // 模拟
            ExecuteCommand(command);
        }
    }
    */
    public void ExecuteCommand(string command)
    {
        GD.Print($"执行PM命令: {command}");
        // 后续实现
    }

    public string PlayerName { get; set; } = "玩家";
    public string RoomId { get; set; } = "";
    public List<string> RoomPlayers { get; set; } = new List<string>(); // 玩家名字列表
    public List<bool> RoomReady { get; set; } = new List<bool>();       // 对应准备状态

    // 方便重置房间
    public void InitRoom(string roomId, string hostName)
    {
        RoomId = roomId;
        RoomPlayers = new List<string> { hostName, "AI玩家" };
        RoomReady = new List<bool> { false, true }; // AI默认准备
    }

    // ---- 卡组选择（房间→对局链路传递）----
    /// <summary>当前选中的卡组 JSON 路径（默认用内置默认卡组）。</summary>
    public string SelectedDeckPath { get; set; } = "res://Decks/default_deck.json";

    /// <summary>当前选中的卡组内存态（DeckBuilder 编辑/保存后写入，开局直接喂给 DeckManager，
    /// 避免重复读盘；为 null 时由 GameInitializer 按需从 SelectedDeckPath 加载）。</summary>
    public DeckList SelectedDeck { get; set; } = null;

    /// <summary>房间界面是否勾选使用所选 JSON 卡组带入 P1。false=则不生效，P1 也用硬编码卡组。</summary>
    public bool UseSelectedDeck { get; set; } = false;

    /// <summary>从 MainMenu 进入 DeckBuilder 时要编辑的卡组路径；为空表示**载入"上次使用的卡组"**
    /// （与房间页默认卡组同一口径，见 Script.IO.DeckSelection.ResolveDefault）。</summary>
    public string EditingDeckPath { get; set; } = null;
}