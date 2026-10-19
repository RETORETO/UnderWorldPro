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

    /// <summary>
    /// 【P1·视角归一·2026-10-19】座位是否镜像（本地座位 = 2）。
    ///
    /// 联机镜像视图下，座位 1 的卡画在 `Player2Area`（上）、座位 2 的卡画在 `Player1Area`（下），
    /// 而 `PlayerXArea` 里的 `StatsLabel`（调查/战斗/势力）与地区卡的「势力标记」文案是**按半边固定**的
    /// ⇒ 不跟着换就会把"自己的数字"显示在对手那半边（玩家 2 视角独有）。
    /// 单机 `Arena` 恒为 false（本地恒为座位 1，画面不镜像）。
    /// </summary>
    public bool MirrorSeats
    {
        get => _mirrorSeats;
        set
        {
            _mirrorSeats = value;
            // 争夺区的红/蓝归属色依赖本标记 ⇒ 任何时刻被改写都要立刻重刷（不依赖调用方在 _Ready 前设好）。
            ApplyAreaBackdrop();
        }
    }
    private bool _mirrorSeats;

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

        // 【2026-10-20】地区背景：每个地区一张独立场景图，上下半区取同一张图的不同裁切窗口。
        ApplyAreaBackdrop();

        // 颜色由场景文件中的 modulate/self_modulate 控制，不在此覆盖
    }

    /// <summary>
    /// 【2026-10-20 区域背景】给本槽位的上下半区铺地区场景图。
    ///
    /// 修正的历史问题：`AreaSlot.tscn` 里 `Player1Area/Background`、`Player2Area/Background` 用的是
    /// `堆叠1.jpg`（90×365 的竖条），却被**默认 SCALE 拉伸**铺满 480×200 的横条 ⇒ 横向放大 5.3 倍、纵向压缩
    /// 1.8 倍，人物被拉成一张"饼"。现改为：场景图 + `AtlasTexture` 取景窗口 + `KeepAspectCovered`
    /// （等比铺满、溢出裁掉），全程零拉伸。
    ///
    /// 取景窗口 = 原图 888×370，恰好等于半区 480×200 的 2.4 比例；上下半区窗口横向错开，
    /// 使"本方/对方"两侧画面不同（否则上下两半一模一样，玩家分不清谁是谁）。
    /// </summary>
    private void ApplyAreaBackdrop()
    {
        if (_player1Area == null || _player2Area == null) return;

        string path = ArenaBackdrop.AreaImage(_areaIndex);

        var p2Bg = _player2Area.GetNodeOrNull<TextureRect>("Background");   // 上半 = 对手半边
        var p1Bg = _player1Area.GetNodeOrNull<TextureRect>("Background");   // 下半 = 本方半边

        bool topOk = BackdropHelper.ApplyCoverWindow(p2Bg, path, ArenaBackdrop.TopWindow, ArenaBackdrop.TopScrim);
        bool bottomOk = BackdropHelper.ApplyCoverWindow(p1Bg, path, ArenaBackdrop.BottomWindow, ArenaBackdrop.BottomScrim);

        // 座位归属：`Player1Area` **恒为「本方」半边**（联机镜像视图下本方 = 座位 2）。
        // 故红/蓝必须按「座位」着色，而不是按节点名 —— 否则玩家 2 视角下红蓝会反。
        ApplySeatFrame(p1Bg, bottomOk, MirrorSeats ? 2 : 1);
        ApplySeatFrame(p2Bg, topOk, MirrorSeats ? 1 : 2);
    }

    /// <summary>座位 1 的红 / 座位 2 的蓝（争夺区归属色）。</summary>
    private static readonly Color Seat1Color = new Color(0.80f, 0.28f, 0.26f, 1f);
    private static readonly Color Seat2Color = new Color(0.26f, 0.52f, 0.82f, 1f);

    /// <summary>
    /// 给某个争夺区半区套「座位色边框 + 淡色罩」，让"这一半归谁"一眼可辨。
    ///
    /// 为什么不放在 `PlayerXArea` 下、而要挂进已有的 `Background` 节点里：
    /// `ClearCombatZones()` 每次清场只保留名为 `Background` / `StatsLabel` 的子节点，
    /// 挂在外层会被当场删掉（表现为"打着打着红蓝边框消失"）。
    /// </summary>
    private static void ApplySeatFrame(TextureRect bg, bool hasImage, int seat)
    {
        if (bg == null || !GodotObject.IsInstanceValid(bg)) return;
        Color c = seat == 1 ? Seat1Color : Seat2Color;

        // 没有可用场景图 ⇒ 用接近纯色的座位底色兜底（同时仍保留边框范围）。
        if (!hasImage)
        {
            BackdropHelper.EnsureFill(bg, new Color(c.R * 0.26f, c.G * 0.26f, c.B * 0.28f, 0.98f), "__SeatFill");
        }
        else
        {
            // 有图时把兜底色藏起来，免得重复叠一层色块
            var staleFill = bg.GetNodeOrNull<ColorRect>("__SeatFill");
            if (staleFill != null && GodotObject.IsInstanceValid(staleFill)) staleFill.Visible = false;
        }

        // 淡色罩：0.14 足够读出色相，又不至于把卡面压灰。
        var wash = bg.GetNodeOrNull<ColorRect>("__SeatWash");
        if (wash == null || !GodotObject.IsInstanceValid(wash))
        {
            wash = new ColorRect { Name = "__SeatWash", MouseFilter = MouseFilterEnum.Ignore };
            wash.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            bg.AddChild(wash);
        }
        wash.Color = new Color(c.R, c.G, c.B, 0.14f);

        // 边框：用「透明底 + 纯描边」的 Panel，范围一眼可见（2px）。
        var frame = bg.GetNodeOrNull<Panel>("__SeatFrame");
        if (frame == null || !GodotObject.IsInstanceValid(frame))
        {
            frame = new Panel { Name = "__SeatFrame", MouseFilter = MouseFilterEnum.Ignore };
            frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            bg.AddChild(frame);
        }
        var sb = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0f), BorderColor = c };
        sb.SetBorderWidthAll(2);
        sb.SetCornerRadiusAll(6);
        frame.AddThemeStyleboxOverride("panel", sb);
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
        ApplyAreaBackdrop();   // 槽位序号已知 ⇒ 换成该地区对应的场景图
        UpdateMarks(0, 0);
        ClearCombatZones();
        RefreshStatsLabels();
    }

    public void UpdateLayout()
    {
        if (_cardContainer == null || _player1Area == null || _player2Area == null) return;

        // 与 Scenes/UI/AreaSlot.tscn 保持一致:
        // Player2Area 在上、CardContainer(AreaCardUI) 在中间居中、Player1Area 在下
        // 【P2·2026-10-18】槽位 400×580 → 480×540：3 张 140 宽角色卡 + 2×8 间距 = 436 ≤ 480（不再重叠）。
        // 【2026-10-19】地区卡 140×100 → 196×140（保持 1.4:1 卡图比例，见 `AreaCardUI.CARD_WIDTH/HEIGHT`）；
        // 纵向 540 = 200 + 140(地区卡) + 200，上下两侧各 200 仍容纳 196 高的角色卡。
        // ⚠️ 地区卡尺寸与玩家卡**不同源**（横向卡 vs 竖卡），故用 `AreaCardUI` 的常量，不得混用 `CardLayoutHelper`。
        float totalW = Size.X > 0 ? Size.X : 480;
        float totalH = Size.Y > 0 ? Size.Y : 540;
        const float cardWidth = AreaCardUI.CARD_WIDTH;
        const float cardHeight = AreaCardUI.CARD_HEIGHT;
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
        _candidateHighlight = on;
        ApplyAreaHighlightVisual();
    }

    // ============================================================
    // 【选卡/找卡 UI 优化·批次 1 / 2026-10-16 修正】地区槽位高亮的两态视觉
    // ------------------------------------------------------------
    // 修复"地区高亮完全不可见"：根节点是**不自绘**的纯 `Control`，而 `SelfModulate` **不传播给子节点**
    // ⇒ 原先那句赋值实际什么都看不见（真正可见的 `Background` 是 `Player1Area/Player2Area` 下的**孙子节点**）。
    // 现统一为两条真正可见的表达，且**两态用不同颜色**（用户口径：已选中要有"区别于原有高亮"的其它高亮）：
    //   ① **可选**：地区卡（`AreaCardUI`）画**黄框** + 两侧玩家区 `Background` 淡黄（表达"整格地区可选"）；
    //   ② **已选中**：地区卡画**青色粗框** + 两侧背景淡青（与黄框明显区分）。
    // 注意：**不再用根节点 `SelfModulate`/`Modulate` 上色**——前者不传播（看不见），后者会与另一态互相覆盖。
    // ============================================================
    private bool _candidateHighlight;
    private bool _chosenHighlight;

    private void ApplyAreaHighlightVisual()
    {
        _areaCardUI?.SetSelectableHighlight(_candidateHighlight);
        _areaCardUI?.SetChosenHighlight(_chosenHighlight);
        SetSideBackgroundTint(_chosenHighlight
            ? AreaChosenTint
            : (_candidateHighlight ? AreaHighlightTint : Colors.White));
    }

    private CanvasItem _player1Bg;
    private CanvasItem _player2Bg;
    private bool _sideBackgroundResolved;
    private static readonly Color AreaHighlightTint = new Color(1f, 0.95f, 0.6f);
    private static readonly Color AreaChosenTint = new Color(0.75f, 0.95f, 1f);

    private void SetSideBackgroundTint(Color c)
    {
        ResolveSideBackgrounds();
        if (_player1Bg != null && GodotObject.IsInstanceValid(_player1Bg)) _player1Bg.Modulate = c;
        if (_player2Bg != null && GodotObject.IsInstanceValid(_player2Bg)) _player2Bg.Modulate = c;
    }

    private void ResolveSideBackgrounds()
    {
        if (_sideBackgroundResolved) return;
        // `_Ready` 尚未执行时 `_player1Area/_player2Area` 仍为空 ⇒ 本次跳过，下次再解析。
        if (_player1Area == null && _player2Area == null) return;
        _sideBackgroundResolved = true;
        _player1Bg = _player1Area?.GetNodeOrNull<CanvasItem>("Background");
        _player2Bg = _player2Area?.GetNodeOrNull<CanvasItem>("Background");
    }

    /// <summary>
    /// 【2026-09 用户口径 JC021】**"已选中"态**：与"可选"高亮（黄框 + 淡黄）区分开，这里用
    /// **青色粗框 + 淡青侧栏**。于是"已被选中的地区"仍然看得见（否则选中后它不再是候选、
    /// 高亮被撤掉，玩家会以为"地区没有高亮"）。
    /// </summary>
    public void SetSelected(bool on)
    {
        _chosenHighlight = on;
        ApplyAreaHighlightVisual();
    }

    /// <summary>由 Arena 在选目标（地区槽位）激活时设置本槽位是否为可选目标。</summary>
    public bool IsTargetSelectable { get; set; }

    /// <summary>【2026-09·JC021 多选】地区槽位**右键**回调（"左键选择、右键取消"的取消侧）。</summary>
    public System.Action<string> OnAreaRightClicked;

    public override void _GuiInput(InputEvent @event)
    {
        // 仅当本槽位是"机动取目标"的可选目标时，点击才回传地区 UID。
        // 选目标窗口为 Control 叠加层（非独占模态 Window），不吞掉下层 _GuiInput，故此处可靠触发。
        if (!IsTargetSelectable) return;
        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                OnAreaClicked?.Invoke(_areaUID);
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                // 【2026-09·JC021】右键＝取消对该地区的选择（与卡的"左键选择右键取消"同口径）
                OnAreaRightClicked?.Invoke(_areaUID);
                GetViewport().SetInputAsHandled();
            }
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
        // 【P1】镜像视图下 Player1Area = 本方、Player2Area = 对手 ⇒ 值与文案一起换（避免"P1: 3 P2: 1"里 P1 其实是自己）
        if (MirrorSeats)
        {
            _player1Marks = p2;
            _player2Marks = p1;
            if (_areaCardUI != null) _areaCardUI.UpdateMarks(p2, p1, localFirst: true);
            return;
        }
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

    public void SetAreaIndex(int index)
    {
        _areaIndex = index;
        ApplyAreaBackdrop();   // 序号变了 ⇒ 场景图跟着换
    }

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

        // 【P1】镜像视图（本地=座位2）：Player1Area 里画的是座位2 的卡 ⇒ 数字也必须取座位2 的
        if (MirrorSeats)
        {
            ApplyStats(_player1StatsLabel, p2);
            ApplyStats(_player2StatsLabel, p1);
        }
        else
        {
            ApplyStats(_player1StatsLabel, p1);
            ApplyStats(_player2StatsLabel, p2);
        }
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
