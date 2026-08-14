using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.UI;
using System;
using System.Collections.Generic;

public partial class AreaSlot : Control
{
    private Control _cardContainer;
    private Control _player1Area;
    private Control _player2Area;
    private Label _player1StatsLabel;
    private Label _player2StatsLabel;

    private AreaCardUI _areaCardUI;
    private AreaCardData _data;          // 缓存 = _instance.AreaTemplate，对外 Data 属性返回（兼容现有引用）
    private CardInstance _instance;       // 彻底方案B：地区卡实例（赢取时由 AreaManager MoveToScore 进分数区）
    private int _areaIndex;
    private bool _isTaskArea;
    private string _areaUID;              // 槽位身份 ID = CardInstance.UID（彻底方案B：用实例 UID 作为地区槽位 ID）

    // 统计依赖（由 Arena 注入，避免 AreaSlot 反向持有全局 Manager）
    private ZoneManager _zoneManager;
    private PlayerManager _playerManager;

    // 取目标效果（机动）目标选择支持：由 Arena 注入，点击槽位时回调地区 UID。
    // ActionService 仅做逻辑层地区注册，Arena 通过此回调接收点击。
    public Action<string> OnAreaClicked;

    private int _player1Marks = 0;
    private int _player2Marks = 0;

    public int AreaIndex => _areaIndex;
    public AreaCardData Data => _data;
    public bool IsTaskArea => _isTaskArea;
    public string AreaUID => _areaUID;
    public Control Player1Area => _player1Area;
    public Control Player2Area => _player2Area;

    /// <summary>注入统计所需的逻辑层依赖。必须在 Setup 之前调用。</summary>
    public void SetStatsDependencies(ZoneManager zoneManager, PlayerManager playerManager)
    {
        _zoneManager = zoneManager;
        _playerManager = playerManager;
    }

    public override void _Ready()
    {
        _cardContainer = GetNode<Control>("CardContainer");
        _player1Area = GetNode<Control>("Player1Area");
        _player2Area = GetNode<Control>("Player2Area");
        _player1StatsLabel = _player1Area?.GetNodeOrNull<Label>("StatsLabel");
        _player2StatsLabel = _player2Area?.GetNodeOrNull<Label>("StatsLabel");
        if (_cardContainer == null) GD.PrintErr("AreaSlot: CardContainer not found!");

        GD.Print($"AreaSlot _Ready: CardContainer={_cardContainer != null}, P1={_player1Area != null}, P2={_player2Area != null}");

        if (_cardContainer == null || _player1Area == null || _player2Area == null)
        {
            GD.PrintErr("AreaSlot 子节点获取失败，请检查场景树名称！");
            return;
        }

        SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _cardContainer.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _player1Area.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _player2Area.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);

        // 颜色由场景文件中的 modulate/self_modulate 控制，不在此覆盖
    }

    /// <summary>彻底方案B：由地区卡实例驱动槽位显示。AreaUID 取实例 UID（槽位身份=实例身份）。</summary>
    public void Setup(CardInstance instance, int index, bool isTask, PackedScene areaCardUIScene)
    {
        _instance = instance;
        _data = instance?.AreaTemplate;
        _areaIndex = index;
        _isTaskArea = isTask;
        _areaUID = instance?.UID ?? Guid.NewGuid().ToString();

        if (_cardContainer == null) return;

        ClearCardContainer();
        if (areaCardUIScene != null)
        {
            _areaCardUI = areaCardUIScene.Instantiate<AreaCardUI>();
            if (_areaCardUI != null)
            {
                _cardContainer.AddChild(_areaCardUI);
                _areaCardUI.Setup(_data);      // 传入地区卡数据（名称/势力/卡图等）
                _areaCardUI.SetFace(true);    // 地区卡放入 AREASLOT 时正面朝上
            }
        }
        UpdateLayout();
        UpdateMarks(0, 0);
        ClearCombatZones();
        RefreshStatsLabels();
    }

    public void UpdateLayout()
    {
        if (_cardContainer == null || _player1Area == null || _player2Area == null) return;

        // 与 Scenes/UI/AreaSlot.tscn 保持一致:
        // Player2Area 在上、CardContainer(AreaCardUI) 在中间居中、Player1Area 在下
        float totalW = Size.X > 0 ? Size.X : 400;
        float totalH = Size.Y > 0 ? Size.Y : 580;
        const float cardWidth = 140;
        const float cardHeight = 100;
        float sideH = (totalH - cardHeight) / 2;

        _player2Area.Size = new Vector2(totalW, sideH);
        _player2Area.Position = new Vector2(0, 0);

        _cardContainer.Size = new Vector2(cardWidth, cardHeight);
        _cardContainer.Position = new Vector2((totalW - cardWidth) / 2, sideH);

        _player1Area.Size = new Vector2(totalW, sideH);
        _player1Area.Position = new Vector2(0, sideH + cardHeight);

        if (_areaCardUI != null)
        {
            _areaCardUI.Size = _cardContainer.Size;
            _areaCardUI.Position = Vector2.Zero;
        }
    }

    /// <summary>命中检测：给定全局坐标是否落在本槽位整体范围内（用于"选择地区槽位为目标"的点击判定）。</summary>
    public bool ContainsGlobalPoint(Vector2 globalPos)
    {
        return GetGlobalRect().HasPoint(globalPos);
    }

    /// <summary>设置目标高亮（机动等"选择地区槽位"触发效果的可选目标视觉提示）。</summary>
    public void SetHighlight(bool on)
    {
        // ColorLoyaltyRequirement/调制色由场景控制；此处用自调制叠加黄色高亮，避免覆盖原配色。
        SelfModulate = on ? new Color(1.2f, 1.2f, 0.4f) : Colors.White;
    }

    /// <summary>由 Arena 在选目标（地区槽位）激活时设置本槽位是否为可选目标。</summary>
    public bool IsTargetSelectable { get; set; }

    public override void _GuiInput(InputEvent @event)
    {
        // 仅当本槽位是"机动取目标"的可选目标时，点击才回传地区 UID。
        // 选目标窗口为 Control 叠加层（非独占模态 Window），不吞掉下层 _GuiInput，故此处可靠触发。
        if (!IsTargetSelectable) return;
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            OnAreaClicked?.Invoke(_areaUID);
            GetViewport().SetInputAsHandled();
        }
    }

    public bool CanDropCard(CardUI cardUI, Vector2 globalPos)
    {
        if (_player1Area != null && _player1Area.GetGlobalRect().HasPoint(globalPos))
            return true;
        if (_player2Area != null && _player2Area.GetGlobalRect().HasPoint(globalPos))
            return true;
        return false;
    }

    private void ClearCardContainer()
    {
        // 只移除动态内容(AreaCardUI),保留场景里静态的 Background 背景
        foreach (var child in _cardContainer.GetChildren())
            if (child.Name != "Background") child.QueueFree();
        _areaCardUI = null;
    }

    public void ClearCombatZones()
    {
        // 只移除动态内容(打出的卡牌),保留场景里静态的 Background 与 StatsLabel
        foreach (var child in _player1Area.GetChildren())
            if (child.Name != "Background" && child.Name != "StatsLabel") child.QueueFree();
        foreach (var child in _player2Area.GetChildren())
            if (child.Name != "Background" && child.Name != "StatsLabel") child.QueueFree();
        RefreshStatsLabels();
    }

    public void UpdateMarks(int p1, int p2)
    {
        _player1Marks = p1;
        _player2Marks = p2;
        if (_areaCardUI != null)
            _areaCardUI.UpdateMarks(p1, p2);
    }

    public AreaCardUI GetAreaCardUI() => _areaCardUI;

    public void SetAreaCardUI(AreaCardUI newUI)
    {
        ClearCardContainer();
        if (newUI != null)
        {
            _cardContainer.AddChild(newUI);
            _areaCardUI = newUI;
            _areaCardUI.SetFace(true);
            _areaCardUI.MouseFilter = MouseFilterEnum.Stop;
            foreach (var child in _areaCardUI.GetChildren())
                if (child is Control c) c.MouseFilter = MouseFilterEnum.Ignore;
            UpdateLayout();
        }
        RefreshStatsLabels();
    }

    public void ResetToNewArea(CardInstance newInstance, AreaCardUI newUI)
    {
        SetAreaCardUI(newUI);
        _instance = newInstance;
        _data = newInstance?.AreaTemplate;
        _isTaskArea = false;
        _areaUID = newInstance?.UID ?? Guid.NewGuid().ToString();
        if (_areaCardUI != null)
        {
            _areaCardUI.Setup(_data);
            _areaCardUI.SetFace(true);
        }
        UpdateMarks(0, 0);
        ClearCombatZones();
        RefreshStatsLabels();
    }

    /// <summary>
    /// 仅更新地区数据，不替换 AreaCardUI（由 AreaManager 在赢取替换时调用）。
    /// 彻底方案B：AreaUID 取新实例 UID（槽位身份=实例身份）。
    /// </summary>
    public void ResetToNewAreaData(CardInstance newInstance)
    {
        _instance = newInstance;
        _data = newInstance?.AreaTemplate;
        _isTaskArea = false;
        _areaUID = newInstance?.UID ?? Guid.NewGuid().ToString();
        if (_areaCardUI != null)
        {
            _areaCardUI.Setup(_data);
            _areaCardUI.SetFace(true);
        }
        UpdateMarks(0, 0);
        ClearCombatZones();
        RefreshStatsLabels();
    }

    public void SetAreaIndex(int index) => _areaIndex = index;

    public Control GetTargetContainer(Vector2 globalPos)
    {
        if (_player1Area != null && _player1Area.GetGlobalRect().HasPoint(globalPos))
            return _player1Area;
        if (_player2Area != null && _player2Area.GetGlobalRect().HasPoint(globalPos))
            return _player2Area;
        return null;
    }

    public void DebugPrint()
    {
        GD.Print($"--- AreaSlot Debug (Size={Size}) ---");
        if (_cardContainer != null)
            GD.Print($"  CardContainer: Pos={_cardContainer.Position}, Size={_cardContainer.Size}, GlobalPos={_cardContainer.GlobalPosition}");
        if (_player1Area != null)
            GD.Print($"  Player1Area: Pos={_player1Area.Position}, Size={_player1Area.Size}, GlobalPos={_player1Area.GlobalPosition}");
        if (_player2Area != null)
            GD.Print($"  Player2Area: Pos={_player2Area.Position}, Size={_player2Area.Size}, GlobalPos={_player2Area.GlobalPosition}");
        GD.Print("--- End ---");
    }

    // ============================================================
    // 地区内能力统计（调查/战斗/势力）
    // 规则同 CombatManager：
    //   - 仅统计未横置的角色/附属/暗藏者
    //   - 暗藏者：调查=0、战斗=0、势力=1（+先手势力加成），横置则归零
    //   - 先手加成：卡牌操控者=当前先手玩家时，加算对应先手能力
    // 任一玩家在该区无任何上述卡时，对应 Label 隐藏
    // ============================================================

    /// <summary>
    /// 刷新两个玩家的地区能力统计 Label。
    /// 在以下时机由 Arena 调用：卡进入/离开地区、地区内卡横置变化、先手玩家切换。
    /// </summary>
    public void RefreshStatsLabels()
    {
        if (_zoneManager == null || _playerManager == null)
            return;   // 依赖未注入时静默跳过（Setup 早期或测试场景）

        // 统计规则统一委托 AreaStatsCalculator（与 CombatManager 结算共用同一份逻辑，避免规则漂移）
        var p1 = AreaStatsCalculator.GetAreaStats(_zoneManager, _playerManager, _areaUID, 1);
        var p2 = AreaStatsCalculator.GetAreaStats(_zoneManager, _playerManager, _areaUID, 2);

        ApplyStats(_player1StatsLabel, p1);
        ApplyStats(_player2StatsLabel, p2);
    }

    private static void ApplyStats(Label label, (int inv, int bat, int pow) s)
    {
        if (label == null || !GodotObject.IsInstanceValid(label)) return;
        // 三项均为 0 → 该地区该玩家没有任何参与卡，隐藏 Label
        if (s.inv == 0 && s.bat == 0 && s.pow == 0)
        {
            label.Visible = false;
            label.Text = "";
            return;
        }
        label.Visible = true;
        label.Text = $"调查：{s.inv}，战斗：{s.bat}，势力：{s.pow}";
    }
}
