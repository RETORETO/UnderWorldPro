using Godot;
using System.Collections.Generic;
using System.IO;
using Script.IO;
using Script.UI;

public partial class Room : Control
{
    private Label _roomIdLabel;
    private Label _playerNameLabel1, _playerNameLabel2;
    private Label _readyStatus1, _readyStatus2;
    private Button _readyBtn, _startBtn, _exitBtn;
    private OptionButton _deckOption;

    private bool _isReady = false;

    // 扫描到的卡组 JSON 路径列表（与下拉项一一对应）
    private List<string> _deckPaths = new List<string>();

    private const string DecksDir = "res://Decks";

    public override void _Ready()
    {
        GD.Print("【房间】场景加载完成");

        _roomIdLabel = GetNodeOrNull<Label>("RoomIdLabel");
        _playerNameLabel1 = GetNodeOrNull<Label>("PlayerListContainer/PlayerSlot1/PlayerNameLabel1");
        // 注意：Room.tscn 中 PlayerSlot2 下的两个 Label 实际也叫 PlayerNameLabel1 / ReadyStatus1
        _playerNameLabel2 = GetNodeOrNull<Label>("PlayerListContainer/PlayerSlot2/PlayerNameLabel1");
        _readyStatus1 = GetNodeOrNull<Label>("PlayerListContainer/PlayerSlot1/ReadyStatus1");
        _readyStatus2 = GetNodeOrNull<Label>("PlayerListContainer/PlayerSlot2/ReadyStatus1");
        _readyBtn = GetNodeOrNull<Button>("PlayerListContainer/Button");
        _startBtn = GetNodeOrNull<Button>("PlayerListContainer/Button2");
        _exitBtn  = GetNodeOrNull<Button>("PlayerListContainer/Button3");
        _deckOption = GetNodeOrNull<OptionButton>("PlayerListContainer/DeckOptionButton");

        if (_readyBtn != null) _readyBtn.Pressed += OnReadyPressed;
        if (_startBtn != null) _startBtn.Pressed += OnStartPressed;
        if (_exitBtn != null) _exitBtn.Pressed  += OnExitPressed;
        if (_deckOption != null) _deckOption.ItemSelected += OnDeckSelected;

        var gm = GameManager.Instance;
        // 兜底：若 Room 被直接运行（未经过 Lobby.InitRoom），自动初始化一个单人房间，避免索引越界
        if (gm.RoomPlayers == null || gm.RoomPlayers.Count == 0)
        {
            GD.Print("【房间】未检测到房间数据，自动初始化兜底房间");
            gm.InitRoom("debug-room", gm.PlayerName ?? "玩家1");
        }

        GD.Print($"【房间】房间号：{gm.RoomId}");
        GD.Print($"【房间】玩家列表：{string.Join(", ", gm.RoomPlayers)}");
        GD.Print($"【房间】准备状态：{string.Join(", ", gm.RoomReady)}");

        RefreshDeckDropdown();
        RefreshRoomUI();
        ApplyTheme();   // 【2026-10-19 UI 美化】统一到暗色金主题
    }

    /// <summary>应用统一主题：深色背景 + 场景底图 + 金色标题 + 按钮统一样式（见 Script/UI/UITheme.cs）。</summary>
    private void ApplyTheme()
    {
        var bg = UITheme.BackgroundRect();
        AddChild(bg);
        MoveChild(bg, 0);

        // 【2026-10-20 背景】场景图压在深色底之上、所有面板之下（等比铺满裁切，压暗 0.72——本页以面板为主）。
        var cover = BackdropHelper.AttachCover(this, "res://Cards/Image/桌面8.jpg", 0.72f);
        if (cover != null) MoveChild(cover, 1);

        if (_roomIdLabel != null) UITheme.ApplyTitle(_roomIdLabel, 34);
        if (_playerNameLabel1 != null) UITheme.ApplyBody(_playerNameLabel1);
        if (_playerNameLabel2 != null) UITheme.ApplyBody(_playerNameLabel2);
        if (_readyBtn != null) UITheme.ApplyPrimaryButton(_readyBtn);
        if (_startBtn != null) UITheme.ApplyPrimaryButton(_startBtn);
        if (_exitBtn != null) UITheme.ApplySecondaryButton(_exitBtn);
        if (_deckOption != null) UITheme.ApplySecondaryButton(_deckOption);   // OptionButton 继承 Button，复用按钮样式
    }

    /// <summary>扫描 res://Decks/*.json 填充下拉框；首个选项固定为「默认卡组（硬编码）」。</summary>
    private void RefreshDeckDropdown()
    {
        _deckOption.Clear();
        _deckPaths.Clear();

        // 索引 0 为「不使用 JSON（用硬编码卡组）」选项
        _deckOption.AddItem("默认卡组（硬编码）");
        _deckPaths.Add("");

        // 扫描目录下所有 .json
        using var dir = DirAccess.Open(DecksDir);
        if (dir != null)
        {
            dir.ListDirBegin();
            string fileName = dir.GetNext();
            while (!string.IsNullOrEmpty(fileName))
            {
                if (!dir.CurrentIsDir() && fileName.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = DecksDir + "/" + fileName;
                    _deckOption.AddItem(fileName);
                    _deckPaths.Add(fullPath);
                }
                fileName = dir.GetNext();
            }
            dir.ListDirEnd();
        }

        // 恢复选中项：与卡组编辑页 / 联机房间**同一口径**（偏好里的上次卡组 > default_deck.json > 目录第一个）。
        // 用 ResolveForUi 而不是直接读 GameManager：本场已明确选过就用本场的（含"硬编码"的空值），
        // 还没选过才去读持久化偏好 —— 这样重启后房间也能恢复"上次使用的卡组"。
        string preferred = Script.IO.DeckSelection.ResolveForUi();
        int selectIndex = 0;
        if (!Script.IO.WebSafeText.IsNullOrWhiteSpaceAscii(preferred))
        {
            selectIndex = _deckPaths.FindIndex(p => Script.IO.WebSafeText.EqualsIgnoreCaseAscii(p, preferred));
            if (selectIndex < 0) selectIndex = 0;
        }
        _deckOption.Select(selectIndex);
        GameManager.Instance.UseSelectedDeck = selectIndex > 0;
        GameManager.Instance.SelectedDeckPath = selectIndex > 0 ? _deckPaths[selectIndex] : "";
    }

    private void OnDeckSelected(long index)
    {
        int i = (int)index;
        if (i <= 0 || i >= _deckPaths.Count)
        {
            // 「默认卡组（硬编码）」：不带入 JSON（只同步内存，不覆盖已记住的卡组文件）
            Script.IO.DeckSelection.Remember("");
            GD.Print("【房间】选用：默认硬编码卡组");
        }
        else
        {
            // 选卡组 = 变更"默认卡组"（写盘 + 同步本场），卡组编辑页下次进入即显示它
            Script.IO.DeckSelection.Remember(_deckPaths[i]);
            GD.Print($"【房间】选用卡组 JSON: {_deckPaths[i]}");
        }
    }

    private void RefreshRoomUI()
    {
        var gm = GameManager.Instance;
        if (_roomIdLabel != null) _roomIdLabel.Text = $"房间号：{gm.RoomId}";

        if (_playerNameLabel1 != null) _playerNameLabel1.Text = gm.RoomPlayers.Count > 0 ? gm.RoomPlayers[0] : "玩家1";
        if (_readyStatus1 != null)
        {
            bool r1 = gm.RoomReady.Count > 0 && gm.RoomReady[0];
            _readyStatus1.Text = r1 ? "已准备" : "未准备";
            _readyStatus1.AddThemeColorOverride("font_color", r1 ? UITheme.Success : UITheme.Text);
        }

        if (gm.RoomPlayers.Count > 1)
        {
            if (_playerNameLabel2 != null) _playerNameLabel2.Text = gm.RoomPlayers[1];
            if (_readyStatus2 != null)
            {
                bool r2 = gm.RoomReady.Count > 1 && gm.RoomReady[1];
                _readyStatus2.Text = r2 ? "已准备" : "未准备";
                _readyStatus2.AddThemeColorOverride("font_color", r2 ? UITheme.Success : UITheme.Text);
            }
        }
        else
        {
            if (_playerNameLabel2 != null) _playerNameLabel2.Text = "AI玩家";
            if (_readyStatus2 != null)
            {
                _readyStatus2.Text = "已准备";
                _readyStatus2.AddThemeColorOverride("font_color", UITheme.Success);
            }
        }

        _isReady = gm.RoomReady.Count > 0 && gm.RoomReady[0];
        if (_readyBtn != null) _readyBtn.Text = _isReady ? "取消准备" : "准备";

        bool allReady = gm.RoomReady.Count >= 2 && gm.RoomReady[0] && gm.RoomReady[1];
        if (_startBtn != null) _startBtn.Disabled = !allReady;

        GD.Print($"【房间】UI刷新 - 玩家1准备：{_isReady}，玩家2准备：{(gm.RoomReady.Count > 1 ? gm.RoomReady[1].ToString() : "无")}，开始按钮启用：{allReady}");
    }

    private void OnReadyPressed()
    {
        var gm = GameManager.Instance;
        if (gm.RoomReady.Count == 0) gm.RoomReady.Add(false);
        gm.RoomReady[0] = !gm.RoomReady[0];
        _isReady = gm.RoomReady[0];
        GD.Print($"【房间】玩家准备状态切换：{(_isReady ? "已准备" : "取消准备")}");
        RefreshRoomUI();
    }

    private void OnStartPressed()
    {
        GD.Print("【房间】点击开始游戏，切换到对战场景");
        GameManager.Instance.GoToState(GameManager.GameState.Arena);
    }

    private void OnExitPressed()
    {
        GD.Print("【房间】退出房间，返回主菜单");
        GameManager.Instance.RoomPlayers.Clear();
        GameManager.Instance.RoomReady.Clear();
        GameManager.Instance.GoToState(GameManager.GameState.MainMenu);
    }
}
