using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Game.Entities;
using Script.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using Script.Enums;
using Script.Manager;
using Script.Interfaces;

public partial class Arena : Control, Script.Interfaces.IGameUIHooks
{
    // ============================================================
    // 区域引用
    // ============================================================
    [Export] private Control _scoreZone;
    private Control _handContainerRef;
    private Control _handContainerRef2;
    private Label _scoreLabel;
    private Control _areasContainer;
    private Label _phaseStatusLabel;
    private Label _resultLabel;   // 胜负结算文字（居中覆盖），初始隐藏

    // 按钮引用
    private Button _endTurnButton;
    private Button _passButton;
    private Button _opponentPassButton;
    private Button _concedeButton;   // 投降按钮（ActionPanel 子节点），按下直接判本地玩家失败

    // ============================================================
    // 卡牌 UI 列表
    // ============================================================
    // 【MVC 修复 M-08】以下 UI 集合/状态改为 internal，供 CardDragController（同程序集子控制器）访问，
    // 无须经大量委托转发，避免行为漂移；Arena 仍是对外唯一的 View 根。
    internal List<AreaSlot> AreaSlots => _areaSlots;
    internal List<CardUI> HandCardUIs => _handCardUIs;
    internal List<CardUI> HandCardUIs2 => _handCardUIs2;
    internal List<CardUI> AreaCardUIs => _areaCardUIs;
    /// <summary>附属子区(AttachedZone)中"已结附"的附属卡 UI（含结附秘社的附属），供激活拖拽命中检测使用。</summary>
    internal IEnumerable<CardUI> AttachedCardUIs
        => _cardUIMap.Values.Where(ui => ui != null && ui.Instance != null
            && ui.Instance.CurrentZone == CardZone.AttachedZone && ui.Instance.IsAttached);
    internal Control AssetContainer1 => _assetContainer1;
    internal MaskMode ActiveMask => _activeMask;
    internal bool IsDragInteractionLocked => IsInteractionLocked;
    internal int? PendingHandInsertIndex { get => _pendingHandInsertIndex; set => _pendingHandInsertIndex = value; }
    internal void ExitMaskInternal() => ExitMask();
    internal void ArrangeAllCardsInternal() => ArrangeAllCards();
    internal void UpdateTurnUIInternal() => UpdateTurnUI();
    internal void RefreshMaskUIInternal() => RefreshMaskUI();

    private List<AreaSlot> _areaSlots = new List<AreaSlot>();

    // 【MVC 修复 M-08】触发交互协调器（实现 ITriggerView），承载触发排序/选目标交互，Arena 不再直接实现该接口。
    private Script.UI.Interaction.TriggerInteractionCoordinator _triggerCoordinator;
    // 【MVC 修复 M-08】模态对抗窗口协调器，承载战斗/调查窗口生命周期与事件路由。
    private Script.UI.Interaction.ModalWindowCoordinator _modalCoordinator;
    // 【MVC 修复 M-08】手牌拖拽控制器，承载拖拽起手/放手路由与出牌/蒙版落点判定。
    private Script.UI.Interaction.CardDragController _cardDragController;
    // 拖拽结束事件转发到控制器（CardUI 的 CardDragEnded 事件签名 (CardUI, bool)）
    private CardUI.CardDragEndedEventHandler _onCardDragEndedForward;
        // 暗藏者现身请求转发（CardUI 的 CovertRevealRequested 事件签名 (CardUI)）
        private CardUI.CovertRevealRequestedEventHandler _onCovertRevealForward;
        // 附属卡双击激活请求转发（CardUI 的 AttachmentDoubleClicked 事件签名 (CardUI)，与秘社双击一致）
        private CardUI.AttachmentDoubleClickedEventHandler _onAttachmentDoubleClickedForward;
        /// <summary>【2026-10·JC085】墓地行动双击请求转发（CardUI.GraveyardPlayRequested → 选择地区 → 从墓地打出）。</summary>
        private CardUI.GraveyardPlayRequestedEventHandler _onGraveyardPlayRequestedForward;
    private List<CardUI> _handCardUIs = new List<CardUI>();
    private List<CardUI> _handCardUIs2 = new List<CardUI>();
    private List<CardUI> _areaCardUIs = new List<CardUI>();
    // 附属于地区 / 封印于地区的卡（AttachedTo==null 且 ZoneDetailId 非空）：不在 _areaCardUIs 角色列表中，
    // 由 ArrangeAreaAttachmentsAndSeals 与角色卡一起在地区内统一均匀平铺。
    private List<CardUI> _areaAttachSealUIs = new List<CardUI>();

    // 拖拽退回手牌时，按放手 X 坐标插入到序列中接近的位置（而非固定末尾）。
    // 非 null 时，SyncCardUIToZone 的 Hand 分支会按此索引 Insert；否则走原 Add（末尾）。
    private int? _pendingHandInsertIndex = null;

    // ============================================================
    // 模块引用
    // ============================================================
    [Export] private DeckZone _deckZone;
    [Export] private GraveyardZone _graveyardZone;
    private DeckZone _deckZone2;
    private GraveyardZone _graveyardZone2;
    private WorldDeckZone _worldDeckZone;

    // ============================================================
    // 预制体
    // ============================================================
    [Export] private PackedScene _cardUIScene;
    [Export] private PackedScene _areaSlotScene;
    [Export] private PackedScene _areaCardUIScene;
    [Export] private PackedScene _councilUIScene;   // 议会（秘社）UI 场景（CouncilCardUI.tscn）

    // 议会（秘社）容器 UI 引用（Arena.tscn 已预留 CouncilContainer 节点，阶段 C：渲染秘社实例）
    private Control _councilContainer1;
    private Control _councilContainer2;

    // 对抗窗口预制体
    [Export] private PackedScene _investigationWindowScene;
    [Export] private PackedScene _strengthWindowScene;
    [Export] private PackedScene _tieBreakWindowScene;   // 先手破平确认窗口（2026-08-05）
    // 当前拖拽中的手牌卡状态已迁移至 CardDragController（MVC 修复 M-08），Arena 不再持有。

    // 堆叠区预制体
    [Export] private PackedScene _stackEntryScene;
    [Export] private PackedScene _stackArrowScene;

    // D9 激活框架：暴露箭头预制体与图层（internal 供同程序集的 CardDragController 复用）
    internal PackedScene StackArrowScene => _stackArrowScene;
    internal Control ArrowLayer => _arrowLayer;

    // 布局引擎相关常量已迁移至 Arena.Layout.cs（CARD_WIDTH / CARD_HEIGHT / HAND_SPACING / ZINDEX_CARD_BASE）

    // ============================================================
    // Manager 引用（由 GameInitializer 提供）
    // ============================================================
    private GameInitializer _initializer;
    private CardManager _cardManager;
    private PlayerManager _playerManager;
    private DeckManager _deckManager;
    private TurnManager _turnManager;
    //【MVC 修复-问题3】玩家意图统一通道：View 所有写操作经此服务表达，不再直接点名各 Manager 命令方法
    private PlayerActionService _actionService;
    private AreaManager _areaManager;
    private WorldDeckManager _worldDeckManager;
    private StackManager _stackManager;

    // 堆叠区 UI 节点
    private Control _stackContainer;
    private Control _arrowLayer;
    private Dictionary<StackEntry, StackEntryUI> _stackEntryUIMap = new Dictionary<StackEntry, StackEntryUI>();
    private Dictionary<StackEntry, List<StackArrow>> _stackArrows = new Dictionary<StackEntry, List<StackArrow>>();

    // 触发排序 / 选目标窗口（最新需求：触发效果入堆叠系统 UI 骨架）
    private PackedScene _triggerOrderScene;
    private PackedScene _targetSelectionScene;
    private PackedScene _triggerEntryScene;
    private PackedScene _councilActivateWindowScene;
    private PackedScene _activationChoiceWindowScene;
    private TriggerOrderWindow _triggerOrderWindow;
    private TargetSelectionWindow _targetSelectionWindow;
    /// <summary>【鼠标悬浮预览】整张卡图的悬浮浮层（见 `CardHoverPreview` / `CardPreviewBus`）。</summary>
    private CardHoverPreview _hoverPreview;

    // ============================================================
    // 基础行动蒙版（派遣暗藏 / 建立资产）
    // 蒙版 = "可拖拽态"提示：激活后仅允许对应卡牌拖拽/行动，成功后退出蒙版。
    // 点击按钮为纯 UI 切换（进入/退出/互斥切换），不消耗次数。
    // ============================================================
    // 【MVC 修复 M-08】MaskMode 枚举已提取至 Script.UI.MaskMode（共享类型），
    // Arena 处于同命名空间 Script.UI，可直接引用。

    /// <summary>当前激活的蒙版（None 表示普通可出牌状态）</summary>
    private MaskMode _activeMask = MaskMode.None;

    // ============================================================
    // 模态交互锁（Atomicity / 7.3）：调查对抗 / 战斗对抗 / 触发排序窗口打开期间，
    // 冻结一切非本窗口输入（出牌、Pass），避免原子操作执行期间被打断。
    // 注意：窗口 Exclusive 模态只拦截下层 Control 的 _GuiInput，而拖拽出牌是 CardUI 回调、
    // Pass 是按钮回调，均绕过 _GuiInput，故必须在 Arena 逻辑层显式拦截。
    // ============================================================
    // 【MVC 修复 M-08】ModalWindow 枚举已提取至 Script.UI.ModalWindow（共享类型），
    // Arena 处于同命名空间 Script.UI，可直接引用。
    // 【2026-09】由 HashSet 改为**按类型计数**：同一类型多窗重叠（JC081 多选、多目标逐槽、
    // 多玩家逐位牺牲）时不再互相抵消，避免"内层窗关闭把外层持锁掀掉"。
    private readonly System.Collections.Generic.Dictionary<ModalWindow, int> _openModalCounts = new();
    private bool IsInteractionLocked
    {
        get
        {
            foreach (var kv in _openModalCounts) if (kv.Value > 0) return true;
            return false;
        }
    }
    private void LockModal(ModalWindow m)
    {
        _openModalCounts.TryGetValue(m, out int n);
        _openModalCounts[m] = n + 1;
        // 通知 Controller 维护规则层原子锁（核心需求 7.3）：模态窗口打开期间不给响应权
        _turnManager?.NotifyModalOpened();
        // 原子锁置位后立即刷新激活高亮：清除窗口打开前（锁外）残留的 CanActivate=true 缓存，
        // 使 JC003 等快速行动能力在触发排序/选目标窗口内不再显示可发动高亮（呼应 D25/D26，修复原子残差）。
        RefreshActivateHighlights();
    }
    private void UnlockModal(ModalWindow m)
    {
        _openModalCounts.TryGetValue(m, out int n);
        if (n > 0) _openModalCounts[m] = n - 1;
        else _openModalCounts[m] = 0;
        _turnManager?.NotifyModalClosed();
        // 原子锁解除后刷新激活高亮：恢复窗口关闭后的合法激活高亮。
        RefreshActivateHighlights();
    }

    // ============================================================
    // 【2026-09·原子操作期间的玩家选择】通用"玩家决策窗"持锁守卫
    // ------------------------------------------------------------
    // 用户口径：一个原子操作期间如果需要玩家选择，**玩家选择完成之前阻塞进程**；此期间只有
    // 持续效果可以影响游戏，不得有新的行动/触发效果进入堆叠。因此单机的每个选择窗都必须与
    // 规则层原子锁联动——此前只有"触发排序/选目标/对抗"三类窗口联动，选卡/数值/模式/多目标/
    // 额外费用/激活代价/准备维护等窗口都不联动 ⇒ 结算中途还能再出牌/再激活/让过（架构级缺陷）。
    //
    // 配对纪律（按**窗口实例**成对，可嵌套）：
    //   · 开窗 → LockChoiceWindow(win)（持锁 + 监听 TreeExiting 兜底）；
    //   · 玩家选定 → 交付回调**之前** UnlockChoiceWindow(win)（先解锁再续算，否则回调里
    //     PlayCard/ActivateAbility 会被入站原子锁拒绝——与 TriggerInteractionCoordinator 同款）；
    //   · 取消/点窗外关闭（不交付选择）→ 由 TreeExiting 兜底释放。
    // ============================================================
    private readonly System.Collections.Generic.HashSet<ulong> _lockedChoiceWindows = new();

    private void LockChoiceWindow(Window w)
    {
        if (w == null) return;
        if (!_lockedChoiceWindows.Add(w.GetInstanceId())) return;
        LockModal(ModalWindow.ChoiceBox);
        // 无选择即关闭（取消/点窗外）时兜底释放，杜绝持锁泄漏
        w.TreeExiting += () => UnlockChoiceWindow(w);
    }

    private void UnlockChoiceWindow(Window w)
    {
        if (w == null) return;
        if (!_lockedChoiceWindows.Remove(w.GetInstanceId())) return;
        UnlockModal(ModalWindow.ChoiceBox);
    }

    /// <summary>
    /// 【2026-09】把选择窗的条目回调统一包成"**先解锁、再交付选择**"（决策持锁配对纪律，见上方注释）。
    /// 必须在 `win.PopupCentered()` 之前调用；`ActivationChoiceWindow` 在按下时读取 `item.OnChosen`，
    /// 故此处就地替换条目回调即可覆盖**所有**调用方（含 JC081 多选、多目标逐槽、激活代价分配等循环窗）。
    /// </summary>
    private void BindChoiceUnlock(Window win, System.Collections.Generic.List<ActivationChoiceItem> items)
    {
        if (win == null || items == null) return;
        foreach (var it in items)
        {
            if (it == null) continue;
            var orig = it.OnChosen;
            it.OnChosen = () => { UnlockChoiceWindow(win); orig?.Invoke(); };
        }
    }

    // 按钮引用
    private Button _deployCovertButton;
    private Button _establishAssetButton;

    // 蒙版提示 Label（动态创建到 Player1Area）
    private Label _maskHintLabel;

    // ============================================================
    // PM命令
    // ============================================================
    private PMCommandManager _pmCommandManager;
    private ZoneManager _zoneManager;
    private TextEdit _commandInput;

    // 逻辑→UI 同步
    private Dictionary<string, CardUI> _cardUIMap = new Dictionary<string, CardUI>();
    // 议会（秘社）UI 单独管理（与玩家卡 _cardUIMap 解耦，避免类型冲突）
    private Dictionary<string, CouncilCardUI> _councilCardUIs = new Dictionary<string, CouncilCardUI>();
    private Control _scoreContainer1, _scoreContainer2;
    private Control _assetContainer1, _assetContainer2;
    private Label _assetLabel1, _assetLabel2;

    // ============================================================
    // _Ready
    // ============================================================
    public override void _Ready()
    {
        GD.Print("【Arena】对战场景加载开始");

        // ---- 1. 获取场景节点 ----
        _handContainerRef = GetNode<Control>("Player1Area/HandContainer");
        _scoreLabel = GetNode<Label>("Player1Area/ScoreZone/ScoreLabel");
        if (_scoreLabel != null) _scoreLabel.Text = "";
        _handContainerRef2 = GetNode<Control>("Player2Area/HandContainer");
        _deckZone2 = GetNode<DeckZone>("Player2Area/DeckZone");
        _graveyardZone2 = GetNode<GraveyardZone>("Player2Area/GraveyardZone");
        _scoreContainer1 = GetNode<Control>("Player1Area/ScoreZone/CardContainer");
        _scoreContainer2 = GetNode<Control>("Player2Area/ScoreZone/CardContainer");
        _assetContainer1 = GetNode<Control>("Player1Area/AssetContainer");
        _assetContainer2 = GetNode<Control>("Player2Area/AssetContainer");
        _assetLabel1 = GetNode<Label>("Player1Area/AssetContainer/AssetLabel");
        _assetLabel2 = GetNode<Label>("Player2Area/AssetContainer/AssetLabel");
        _councilContainer1 = GetNode<Control>("Player1Area/CouncilContainer");
        _councilContainer2 = GetNode<Control>("Player2Area/CouncilContainer");
        _areasContainer = GetNode<Control>("CenterArea/AreasContainer");
        _phaseStatusLabel = GetNode<Label>("ActionPanel/PhaseLabel");
        // 胜负结算文字 LABEL（居中覆盖全场，初始隐藏；游戏结束时由 OnGameOver 显示 3 秒后回主界面）
        _resultLabel = GetNode<Label>("CenterArea/ResultLabel");
        if (_resultLabel != null) _resultLabel.Visible = false;

        // ---- 1.5 获取堆叠区节点 ----
        _stackContainer = GetNode<Control>("StackPanel/StackContainer");
        _arrowLayer = GetNode<Control>("ArrowLayer");
        // 箭头层抬高：地区槽位/手牌等会设置正 ZIndex，ArrowLayer 必须高于它们才能不被遮挡；
        // 具体层号由 Script.UI.Layers 统一分配（压住所有卡与附属，但低于卡面悬浮预览与命令框）。
        _arrowLayer.ZIndex = Layers.Arrows;
        _arrowLayer.ZAsRelative = false;   // 绝对 ZIndex，不受父节点影响

        // ---- 2. 配置 ActionPanel ----
        var actionPanel = GetNode<Panel>("ActionPanel");
        if (actionPanel != null) actionPanel.MouseFilter = MouseFilterEnum.Stop;

        // ---- 3. 绑定按钮 ----
        _passButton = GetNode<Button>("ActionPanel/PassButton");
        _passButton.Pressed += OnPlayerPass;
        _opponentPassButton = GetNode<Button>("ActionPanel/OpponentPassButton");
        _opponentPassButton.Pressed += OnOpponentPass;
        _endTurnButton = GetNode<Button>("ActionPanel/EndTurnButton");
        _endTurnButton.Pressed += OnEndTurnPressed;
        // 投降按钮：本地玩家(1)直接判负（对手胜），结算逻辑在 PlayerManager.Concede，UI 仅触发意图
        _concedeButton = GetNodeOrNull<Button>("ActionPanel/ConcedeButton");
        if (_concedeButton != null) _concedeButton.Pressed += OnConcedePressed;

        // ---- 3.5 绑定基础行动按钮（派遣暗藏 / 建立资产）----
        _deployCovertButton = GetNodeOrNull<Button>("Player1Area/DeployCovertButton");
        _establishAssetButton = GetNodeOrNull<Button>("Player1Area/EstablishAssetButton");
        if (_deployCovertButton != null) _deployCovertButton.Pressed += OnDeployCovertButtonPressed;
        if (_establishAssetButton != null) _establishAssetButton.Pressed += OnEstablishAssetButtonPressed;

        // 动态创建蒙版提示 Label（显示在玩家1手牌区上方）
        _maskHintLabel = new Label();
        _maskHintLabel.Name = "MaskHintLabel";
        _maskHintLabel.Visible = false;
        _maskHintLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        _maskHintLabel.Position = new Vector2(240, 10);
        _handContainerRef?.GetParent()?.AddChild(_maskHintLabel);

        // ---- 4. 加载预制体 ----
        if (_cardUIScene == null)
            _cardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
        if (_councilUIScene == null)
            _councilUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CouncilCardUI.tscn");
        if (_areaSlotScene == null)
            _areaSlotScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaSlot.tscn");
        if (_areaCardUIScene == null)
            _areaCardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaCardUI.tscn");
        if (_investigationWindowScene == null)
            _investigationWindowScene = ResourceLoader.Load<PackedScene>("res://Scenes/CombatResolution/InvestigationWindow.tscn");
        if (_strengthWindowScene == null)
            _strengthWindowScene = ResourceLoader.Load<PackedScene>("res://Scenes/CombatResolution/StrengthWindow.tscn");
        if (_tieBreakWindowScene == null)
            _tieBreakWindowScene = ResourceLoader.Load<PackedScene>("res://Scenes/CombatResolution/TieBreakWindow.tscn");
        if (_stackEntryScene == null)
            _stackEntryScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/StackEntryUI.tscn");
        if (_stackArrowScene == null)
            _stackArrowScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/StackArrow.tscn");
        if (_triggerOrderScene == null)
            _triggerOrderScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/TriggerOrderWindow.tscn");
        if (_targetSelectionScene == null)
            _targetSelectionScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/TargetSelectionWindow.tscn");
        if (_triggerEntryScene == null)
            _triggerEntryScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/TriggerEntryUI.tscn");
        if (_councilActivateWindowScene == null)
            _councilActivateWindowScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CouncilActivateWindow.tscn");
        if (_activationChoiceWindowScene == null)
            _activationChoiceWindowScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/ActivationChoiceWindow.tscn");

        // ---- 4.1 实例化触发排序 / 选目标窗口（默认隐藏，供 D4 触发收集逻辑调用）----
        if (_triggerOrderWindow == null && _triggerOrderScene != null)
        {
            _triggerOrderWindow = _triggerOrderScene.Instantiate<TriggerOrderWindow>();
            _triggerOrderWindow.Visible = false;
            // 层级（ZIndex/TopLevel）由 TriggerOrderWindow._Ready 自行设置，这里不再覆盖
            AddChild(_triggerOrderWindow);
        }

        // ---- 4.2【鼠标悬浮预览】整张卡图的悬浮浮层（用户 2026-09 口径）----
        // 单机战场是**本地同桌**视图（双方手牌/场上都在同一屏），故查看者固定为本机人类座位（玩家1）：
        //  · 自己的卡（含手牌与暗藏者）→ 弹；
        //  · 公开卡（场上正面/附属/秘社/资产/墓地/地区）→ 弹；
        //  · **对手手牌 / 对手暗藏者** → 不弹，**除非**查看者有一张**未横置**且**与该暗藏者同地区**的
        //    JC057 类卡（"随时检视本地区任一暗藏者"，判定＝`CovertInspectRule` 单一真相源）。
        _hoverPreview = new CardHoverPreview { Name = "CardHoverPreview", ZIndex = Layers.CardHoverPreview };
        AddChild(_hoverPreview);
        CardPreviewBus.Attach(card => CovertInspectRule.CanViewerSeeCardFace(card, 1, _cardManager),
            _hoverPreview, viewerPlayerId: 1);
        if (_targetSelectionWindow == null && _targetSelectionScene != null)
        {
            _targetSelectionWindow = _targetSelectionScene.Instantiate<TargetSelectionWindow>();
            _targetSelectionWindow.Visible = false;
            // 根节点已改为 Window，层级由 Window 自身管理，不再需要 TopLevel/ZIndex。
            AddChild(_targetSelectionWindow);
        }

        // ---- 5. 初始化所有 Manager（委托给 GameInitializer） ----
        //【MVC 修复】装配器（GameInitializer）的职责由场景声明（Composition Root），
        //此处仅从场景树获取已实例化的节点，不再由 View 自行 new + AddChild。
        _initializer = GetNodeOrNull<GameInitializer>("GameInitializer");
        if (_initializer == null)
        {
            //【MVC 修复 M-03】禁止 View 自行 new 装配器兜底：装配器缺失属场景配置错误，
            // 必须暴露而非由表现层偷偷补齐，否则 Composition Root 唯一性被破坏。
            GD.PushError("Arena: 场景中未找到 GameInitializer 节点，请在 Arena.tscn 中声明该节点。");
            return;
        }
        _initializer.InitializeAll();
        _zoneManager = _initializer.ZoneManager;
        //【MVC 收口】ZoneManagerRef / CardManagerRef 已由 GameInitializer（Composition Root）注入，
        // View 不再装配 Controller 依赖。JC068「准备维护」的 UI 询问改由本 Arena 实现 IGameUIHooks 接口，
        // 经 RegisterUIHooks(this) 注入（依赖倒置：Controller 依赖接口，View 实现接口），
        // 弹窗只负责「展示二选一 + 收集选择」，业务动作（支付/横置）统一回交 Controller（ResolvePrepareUpkeep）。
        _initializer.RegisterUIHooks(this);
        _zoneManager.OnCardMoved += SyncCardUIToZone;
        //【MVC 收口】派生 UI 聚合刷新（资产区领域忠诚/地区能力统计/分数/横置视觉/持续效果态）
        // 统一经 ZoneManager.OnPostMove 事件驱动，所有 move 路径（move/PM 命令/结算）自动刷新，
        // 无需调用方逐处手动调 SyncDerivedUI，杜绝"一个一个 UI 刷新漏调"的问题。
        _zoneManager.OnPostMove += OnPostMoveSyncDerivedUI;
        // 持续光环（D19-③）：区域变动后重算所有光环受影响集合（入场/离场/夺控），与 ReevaluatePrereqs 内调用双保险
        _zoneManager.OnPostMove += OnPostMoveReevaluateAuras;
        //【MVC 收口-堆叠箭头】箭头刷新改由事件驱动，去掉原 _Process 每帧轮询全量重绘：
        //  - 目标卡移动（FireMoved→OnPostMove）驱动箭头实时跟随；
        //  - 目标状态变更（CardManager.OnCardStateChanged，含翻面/离场/屏障）驱动重绘，
        //    丢失目标的条目因 entry.TargetWasLost 跳过其箭头（方案 B：与结算丢失走同一条总线）。
        _zoneManager.OnPostMove += OnPostMoveRedrawArrows;
        //【MVC 收口-P0】牌库顺序重组由 ZoneManager 唯一决定，Arena 订阅后仅按序纯重绘 DeckZone
        _zoneManager.OnDeckReordered += OnDeckReorderedHandler;

        _cardManager = _initializer.CardManager;
        //【MVC 收口-堆叠箭头】目标状态变更（翻面/离场/屏障）驱动箭头重绘；
        // 丢失目标的条目因 entry.TargetWasLost 跳过其箭头（方案 B：与结算丢失走同一条总线）。
        // 匿名 lambda 无法用 -= 反订阅，故具名化以支持 _ExitTree 清理。
        _cardManager.OnCardStateChanged += OnCardStateChangedRedrawArrows;
        _playerManager = _initializer.PlayerManager;
        // 订阅游戏结束事件：胜利/失败瞬间由 Manager 广播（不经过堆叠），UI 仅展示 LABEL 并延时回主界面
        if (_playerManager != null) _playerManager.OnGameOver += OnGameOverHandler;
        _deckManager = _initializer.DeckManager;
        _turnManager = _initializer.TurnManager;
        //【MVC 收口-P2】先手加成由 TurnManager 在卡移动后统一重算，Arena 订阅事件仅刷新先手角标，
        // 不再在 SyncCardUIToZone 渲染过程中反向驱动 Controller 重算
        _turnManager.OnFirstStrikeRecalculated += RefreshFirstStrikeFlags;
        // D9 激活框架：优先权变化时刷新“可发动”高亮边框
        _turnManager.OnPriorityChanged += RefreshActivateHighlights;
        // 2026-08-11：费用修正栈变化（JC012 登记/消费/到期）时刷新手牌减费提示
        //【MVC 收口】CostModChanged 已迁至 UiNotificationBus 实例单例。
        Script.Manager.UiNotificationBus.Instance.CostModChanged += RefreshHandCostReductions;
        // 阶段 C：Arena 加载时主动同步双方议会（秘社）UI。
        // 注意：SetupCouncils 在 InitializeAll 阶段（Arena 未加载）已把秘社移入 CouncilZone，
        // 事件早于本订阅触发，故此处主动重放一次，而非依赖 OnCardMoved 事件。
        SyncCouncilsUI();
        //【MVC 修复-问题3】所有玩家意图经统一通道表达
        _actionService = _initializer.PlayerActionService;
        _areaManager = _initializer.AreaManager;
        // 触发效果系统（新系统）：【MVC 修复 M-08】触发交互逻辑收归 TriggerInteractionCoordinator
        // （实现 ITriggerView），Arena 不再直接实现该接口；此处 new 出协调器、注入窗口/场景/地区槽位/
        // 命中测试/模态锁依赖，再把协调器实例注入 TurnManager。TurnManager 仍只依赖 ITriggerView，
        // 不直接引用 Arena 或其他具体 UI 类型，Controller 无 UI 引用红线保持不变。
        if (_turnManager != null)
        {
            _triggerCoordinator = new Script.UI.Interaction.TriggerInteractionCoordinator();
            AddChild(_triggerCoordinator);
            _triggerCoordinator.Configure(
                _triggerOrderWindow, _targetSelectionWindow, _triggerEntryScene,
                _areaSlots,
                (pos) => _cardDragController?.PickAreaCardAt(pos),
                () => LockModal(ModalWindow.TriggerOrder),
                () => UnlockModal(ModalWindow.TriggerOrder),
                () => LockModal(ModalWindow.TargetSelection),
                () => UnlockModal(ModalWindow.TargetSelection));
            _turnManager.SetTriggerView(_triggerCoordinator);
            GD.Print("  ✅ TriggerInteractionCoordinator（ITriggerView）已注入 TurnManager");
        }
        else
        {
            GD.PrintErr("❌ 触发效果注入失败：_turnManager 为 null");
        }
        _worldDeckManager = _initializer.WorldDeckManager;
        _stackManager = _initializer.StackManager;
        _stackManager.OnStackChanged += RefreshStackUI;

        // 模态对抗窗口协调器（MVC 修复 M-08）：承接战斗/调查窗口生命周期与事件路由。
        // 直接订阅 CombatManager.OnCombatWindowRequired，替代 Arena 此前手动订阅转发；
        // 注入窗口预制体、Controller 引用、命中委托与模态锁委托，并把战斗结算后的 UI 刷新钩子接回 Arena。
        _modalCoordinator = new Script.UI.Interaction.ModalWindowCoordinator();
        AddChild(_modalCoordinator);
        _modalCoordinator.Configure(
            _investigationWindowScene, _strengthWindowScene, _tieBreakWindowScene, _areaSlots,
            _initializer.CombatManager, _cardManager, _actionService,
            FindCardUIByUID,
            LockModal, UnlockModal);
        _modalCoordinator.SetStrengthResolvedCallback(() =>
        {
            RefreshDamageDisplay();
            RefreshArenaStats();
        });
        _modalCoordinator.BindToCombatManager(_initializer.CombatManager);

        // 手牌拖拽控制器（MVC 修复 M-08）：承接拖拽起手/放手路由与出牌/蒙版落点判定。
        // 通过 Arena 暴露的 internal 访问器读取 UI 状态，并持有 Controller 引用表达写意图。
        _cardDragController = new Script.UI.Interaction.CardDragController();
        AddChild(_cardDragController);
        _cardDragController.Configure(this, _actionService, _cardManager, _turnManager, _modalCoordinator);
        _onCardDragEndedForward = (c, d) => _cardDragController.OnCardDragEnded(c, d);
        _onCovertRevealForward = (c) =>
        {
            // 双击暗藏者：由当前有行动权的玩家发动（门禁在 TurnManager 内校验 controller==playerId）
            if (c?.Instance == null) return;
            // 【P3·K21·JC041·A29】暗藏者状态下若**位于水域地区**（或该卡另有"暗藏者专用激活能力"）：
            // **双击不直接现身**，而是弹出**选择框**（"正常现身" / "激活暗藏者效果"，**可取消**）。
            // 判据＝`ActivateEligibility` 里**当前可发动**、且声明 `RequireSourceCovert` 的能力（候选侧与权威同源：
            // 地区子类别前提也在其中判定），因此非水域 / 非暗藏者时不会出现该选择框。
            var covertAbilities = new System.Collections.Generic.List<Script.Data.Effects.EffectData>();
            if (c.Instance.IsCovert && _turnManager != null && _playerManager != null && _cardManager != null)
            {
                var eligible = Script.Manager.ActivateEligibility.GetEligibleAbilities(
                    c.Instance.ControllerId, _turnManager, _playerManager, _cardManager);
                if (eligible != null)
                    foreach (var (abCard, ab) in eligible)
                        if (abCard == c.Instance && ab != null && ab.RequireSourceCovert) covertAbilities.Add(ab);
            }
            if (covertAbilities.Count > 0)
            {
                var inst = c.Instance;
                var items = new System.Collections.Generic.List<ActivationChoiceItem>
                {
                    new ActivationChoiceItem
                    {
                        Label = "正常现身",
                        OnChosen = () => RevealCovertWithExtraCost(_turnManager.PriorityPlayerId, inst),
                    },
                };
                foreach (var ab in covertAbilities)
                {
                    var captured = ab;
                    items.Add(new ActivationChoiceItem
                    {
                        Label = "激活暗藏者效果：" + GetActivationAbilityLabel(inst, captured),
                        OnChosen = () => BeginActivate(inst, captured, null, -1),
                    });
                }
                items.Add(new ActivationChoiceItem { Label = "取消", OnChosen = () => { } });
                OpenChoiceWindow($"{inst.PlayerTemplate?.CardName}：选择行动", items, force: true);
                return;
            }
            RevealCovertWithExtraCost(_turnManager.PriorityPlayerId, c.Instance);
        };
        // 双击请求激活：与秘社双击一致，单击不弹窗（JC012 交互修正）。
        // 【2026-09 修复·JC019/JC024】可双击激活的来源**不止附属**：
        //  · 附属（IsAttached）：模板的 AttachmentActivated/CharacterActivated；
        //  · **争夺区正面角色**：其激活能力可能来自①**运行期授予**（JC019 弥尔顿的镇魔枪给宿主的
        //    "快速行动，横置：造成 1 点不可伤害"）②**无目标的自指激活**（JC024 董事局"快速行动2：将自身潜伏"）
        //    ——这两类都无法用"拖到目标上"表达（宿主/自身没有可拖的目标，或根本没有目标），
        //    旧代码在此 `!IsAttached → return` 把它们全挡掉了（表现为"宿主高亮但点击/拖拽都无反馈"）。
        //  · 「手牌行动」卡（JC110）由 `CanDrag` 之前的双击分支放行（同款）。
        _onAttachmentDoubleClickedForward = (c) =>
        {
            if (c?.Instance == null) return;
            var inst = c.Instance;
            // 【2026-09 诊断】原先本转发器**多处静默 return**（双击毫无反应且零日志，用户实测难定位）——
            // 现每一条提前返回都留下可读日志（含"为什么不能发动"，见 `DiagnoseActivateBlock`）。
            if (!ActivateEligibility.IsDoubleClickActivationSource(inst))
            {
                GD.Print($"[Arena] 双击忽略：{inst.PlayerTemplate?.CardName} 不是『双击激活』来源" +
                         $"（当前区={inst.CurrentZone} 附属={inst.IsAttached} 暗藏={inst.IsCovert}）");
                return;
            }
            // 能力判定＝**模板 ∪ 运行期授予**（与候选枚举/门禁同一入口）；无任何可激活能力则忽略双击。
            if (ActivateEligibility.GetAllActivateAbilities(inst).Count == 0)
            {
                GD.Print($"[Arena] 双击忽略：{inst.PlayerTemplate?.CardName} 没有任何激活能力（模板∪运行期授予）");
                return;
            }
            GD.Print($"[Arena] 双击激活请求：{inst.PlayerTemplate?.CardName}（能力 {ActivateEligibility.GetAllActivateAbilities(inst).Count} 条）");
            OpenAttachmentActivateChoice(c);
        };
        // 【2026-10 用户口径·JC085】墓地行动：墓地中的"墓地行动"卡双击 → **弹选择地区窗** → 从墓地正面打出
        // （卡文"你可以从墓地中正面打出复生行尸，就如同其在你的手上一样"；费用/忠诚/行动步骤照常由 Controller 校验）。
        // 触发条件（墓地区 + `PlayableFromGraveyard`）已在 `CardUI.OnGuiInput` 侧过滤，这里再做一次权威校验。
        _onGraveyardPlayRequestedForward = (c) => OnGraveyardPlayRequested(c);

        // ---- 6. 订阅逻辑层事件 ----
        SubscribeToManagerEvents();

        // ---- 7. 初始化 UI 组件 ----
        InitializeWorldDeckZone();
        InitializeAreas();
        InitializeDeckAndGraveyard();
        BuildCardUIMap();
        RefreshAssetLabels();   // 初始化时先渲染资产区标签
        InitializePMCommands();

        // ---- 8. 开局 ----
        //【MVC 修复 M-02】开局编排（发起始手牌、起局）由 GameInitializer（Composition Root）负责，
        // View 只在自身 UI 与事件订阅全部就绪后触发一次，不再感知发牌张数等规则参数。
        GameManager.Instance?.GoToState(GameManager.GameState.Arena);   // 同步应用级状态（已是 Arena 场景，内部跳过重载）
        _initializer.StartMatch();
        ArrangeAllCards();   // 开局发牌完成后统一排布一次（纯 UI）
        RefreshActivateHighlights();   // D9 激活框架：初始高亮（若开局即拥有优先权且满足条件）
        GD.Print("【Arena】对战场景加载完成");
    }

    /// <summary>2026-08-11：费用修正栈变化（JC012 登记/消费/到期）时，刷新双方手牌的减费提示。
    /// 遍历 HandCardUIs / HandCardUIs2，调 CardUI.RefreshCostReduction 重算并显示减后费用。</summary>
    private void RefreshHandCostReductions()
    {
        foreach (var c in HandCardUIs) c?.RefreshCostReduction();
        foreach (var c in HandCardUIs2) c?.RefreshCostReduction();
    }

        // D9 激活框架：激活合法目标缓存（源卡 UID -> 合法目标卡 UID 集合）。
        // 【MVC 收口-问题A】合法目标由 Model 在 RefreshActivateHighlights 时一次性预计算，
        // 拖拽控制器只读取缓存，不再自行持有 CardManager 实时重算规则，View 与规则解耦。
        private readonly Dictionary<string, HashSet<string>> _activateLegalTargetCache = new();
        /// <summary>查询某源卡对某目标卡是否为当前合法激活目标（读预计算缓存）。</summary>
        public bool IsLegalActivateTargetCached(CardUI actingCard, CardUI targetCard)
        {
            if (actingCard == null || targetCard == null || actingCard == targetCard) return false;
            if (actingCard.Instance == null || targetCard.Instance == null) return false;
            // 规则层原子锁（核心需求 7.3 / D26）：原子操作进行中冻结响应权，落点合法性查询亦须拒绝。
            if (_turnManager != null && _turnManager.IsAtomicLocked) return false;
            return _activateLegalTargetCache.TryGetValue(actingCard.Instance.UID, out var set)
                   && set.Contains(targetCard.Instance.UID);
        }

        /// <summary>该源卡当前是否有**可拖拽的合法目标**（缓存存在且非空）。
        /// 【2026-09】拖拽起手据此决定是否吞掉左键：无可拖目标时**不吞**，让双击信号送达卡 UI。</summary>
        public bool HasCachedActivateTarget(CardUI actingCard)
            => actingCard?.Instance != null
               && _activateLegalTargetCache.TryGetValue(actingCard.Instance.UID, out var set)
               && set.Count > 0;

        // ============================================================
        // 堆叠区 UI 同步
        // ============================================================
        private void RefreshStackUI()
    {
        // 堆叠由空变非空（如对手出牌入堆叠）→ 基础行动蒙版的限制条件失效，强制退出蒙版
        if (_activeMask != MaskMode.None && _stackManager != null && !_stackManager.IsEmpty)
            ExitMask();

        if (_stackManager == null || _stackContainer == null) return;

        // 收集当前应存在的条目
        var current = _stackManager.Entries;

        // 移除已不存在的条目 UI 及其箭头
        var toRemove = new List<StackEntry>();
        foreach (var kv in _stackEntryUIMap)
        {
            if (!current.Contains(kv.Key))
            {
                kv.Value.QueueFree();
                toRemove.Add(kv.Key);
            }
        }
        foreach (var key in toRemove)
        {
            _stackEntryUIMap.Remove(key);
            if (_stackArrows.TryGetValue(key, out var arrows))
            {
                foreach (var a in arrows) a.QueueFree();
                _stackArrows.Remove(key);
            }
        }

        // 新增缺失的条目 UI（保持堆叠顺序：末尾为顶，HBoxContainer 中末尾显示在右侧）
        for (int i = 0; i < current.Count; i++)
        {
            var entry = current[i];
            if (!_stackEntryUIMap.ContainsKey(entry))
            {
                var inst = _stackEntryScene.Instantiate<StackEntryUI>();
                // 必须先 AddChild 触发 _Ready（初始化子节点引用），再调用 Setup 赋值/挂卡，
                // 否则 Setup 内访问 _effectPanel 等字段时仍为 null 导致 NullReferenceException。
                _stackContainer.AddChild(inst);
                // 卡片型复用真卡 UI：从手牌列表取对应 CardUI（入堆叠时已脱离手牌容器）
                CardUI reuse = null;
                if (entry.IsCard && !string.IsNullOrEmpty(entry.SourceCardUid))
                    reuse = FindCardUIByUID(entry.SourceCardUid);
                inst.Setup(entry, reuse);

                // 赢取地区条目：复制并显示一张该地区卡的卡面（复用 AreaCardUI.tscn）
                if (entry.Kind == ResolveKind.AreaWon && !string.IsNullOrEmpty(entry.WonAreaUid))
                {
                    var slot = _areaSlots.FirstOrDefault(s => s.AreaUID == entry.WonAreaUid);
                    if (slot != null)
                        inst.AttachAreaCard(slot.Data, _areaCardUIScene);
                }

                _stackEntryUIMap[entry] = inst;
            }
        }

        RedrawAllArrows();
    }

    // ============================================================
    // 事件订阅生命周期（2026-08-16 修复：投降/重开跨局崩溃的根治）
    // 所有逻辑层/静态事件订阅改为具名方法，并在 _ExitTree 统一反订阅，
    // 防止"旧 Arena 已释放但回调仍挂在静态/单例事件上"导致 ObjectDisposedException。
    // 这是 MTGA 等卡牌的"订阅/反订阅成对"行业铁律。
    // ============================================================

    private void OnPostMoveSyncDerivedUI(CardInstance card, CardZone zone, int playerId, string detailId) => SyncDerivedUI();

    private void OnPostMoveReevaluateAuras(CardInstance card, CardZone zone, int playerId, string detailId)
        => Script.Manager.EffectEngine.Instance.ReevaluateAuras(playerId);

    private void OnPostMoveRedrawArrows(CardInstance card, CardZone zone, int playerId, string detailId) => RedrawAllArrows();

    private void OnCardStateChangedRedrawArrows(CardInstance card, Script.Enums.CardChangeKind kind) => RedrawAllArrows();

    /// <summary>
    /// 【MVC 收口】实现 IGameUIHooks 接口：JC068 阿格斯IV「准备维护」的 UI 询问。
    /// Controller（EffectEngine）经注入的接口发起，View 只负责「展示二选一 + 收集选择」，
    /// 业务动作（支付/横置）统一回交 Controller（ResolvePrepareUpkeep），View 不写 Model。
    /// </summary>
    public void PromptPayOrTap(CardInstance source, int payAmount)
    {
        if (source == null) return;
        bool canPay = EffectEngine.Instance.CanPayPrepareUpkeep(source, payAmount);
        var win = _activationChoiceWindowScene?.Instantiate<ActivationChoiceWindow>();
        if (win != null)
        {
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            // 支付选项：费用不足时置灰（此时只能选横置；阿格斯IV已未横置故横置总是可选）
            items.Add(new ActivationChoiceItem
            {
                Label = $"支付{payAmount}（横置1张资产区牌）",
                Disabled = !canPay,
                OnChosen = () =>
                {
                    EffectEngine.Instance.ResolvePrepareUpkeep(source, payAmount, pay: true);
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                },
            });
            items.Add(new ActivationChoiceItem
            {
                Label = "横置阿格斯IV",
                OnChosen = () =>
                {
                    EffectEngine.Instance.ResolvePrepareUpkeep(source, payAmount, pay: false);
                    Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
                },
            });
            // 强制选择：阿格斯准备维护为强制触发，玩家必须二选一（不能取消/点窗外关闭）
            win.ForceChoice = true;
            win.Init(items);
            AddChild(win);
            // 【2026-09】决策窗持锁：选择完成前冻结响应权（只能靠持续效果影响游戏）
            LockChoiceWindow(win);
            BindChoiceUnlock(win, items);
            win.PopupCentered();
        }
        else
        {
            // 无窗口兜底：交由 Controller 决策（能付则付，否则横置）
            EffectEngine.Instance.ResolvePrepareUpkeep(source, payAmount, pay: canPay);
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        }
    }

    /// <summary>
    /// 【JC119·K28】目标选择窗：为"**由效果免费打出的牌**"取目标（这类牌的打出不是玩家拖拽发起的，
    /// 没有"拖到目标上"的交互）。**完全复用**激活效果的目标选择通道
    /// （`TriggerInteractionCoordinator.RequestActivateTargetSelection`：卡目标＝场上高亮 + 点击；
    /// 地区目标＝地区槽高亮 + 点击；同一份 `TargetSelectionWindow`、同一份卡高光总线），
    /// 不新增第二套选目标 UI。View 只收集选择 → 经回调交回 Controller（`TurnManager` 随后走正常打出流程）。
    /// 窗口/协调器缺失（编辑器未挂场景等）→ 回调 null（该牌留在手牌，绝不静默代打）。
    /// </summary>
    public void PromptPlayTarget(int chooserPlayerId, string prompt, bool isArea,
        System.Collections.Generic.List<string> candidateUids, System.Action<string> onPicked)
    {
        if (_triggerCoordinator == null || _targetSelectionWindow == null)
        {
            GD.PrintErr("[Arena] PromptPlayTarget：目标选择窗不可用（协调器/窗口缺失）→ 按取消处理（不打出该牌）");
            onPicked?.Invoke(null);
            return;
        }
        GD.Print($"[Arena] 🎯 PromptPlayTarget：玩家{chooserPlayerId} 为「{prompt}」选目标（候选 {candidateUids?.Count ?? 0} 个，isArea={isArea}）");
        _triggerCoordinator.RequestActivateTargetSelection(prompt, isArea, candidateUids, onPicked);
    }

    /// <summary>
    /// 【P2】弃牌 UI：让 `chooserPlayerId` 从候选手牌中选一张（候选属于 `discarderId`）。
    /// 一般情形 chooser == discarder（自己弃自己的牌）；JC114 情形 chooser = 施术者（从对手手牌中指定）。
    /// View 只负责"展示候选手牌 + 收集选择"，实际弃牌由 Controller 经回调执行（View 不写 Model）。
    /// </summary>
    public void PromptDiscard(int discarderId, int chooserPlayerId, System.Collections.Generic.List<CardInstance> candidates,
        System.Action<CardInstance> onPicked)
    {
        // 弃牌 = 强制版通用选卡（必须选一张，不允许取消）。窗口细节统一收口在 PromptPickCard（单一实现）。
        PromptPickCard(chooserPlayerId,
            chooserPlayerId == discarderId ? "请选择要弃掉的手牌" : $"请从玩家{discarderId} 的手牌中指定一张弃掉",
            candidates, allowCancel: false,
            picked => { if (picked != null) onPicked?.Invoke(picked); });
    }

    /// <summary>
    /// 【UI 立即刷新·帧同步点】实现 <see cref="IGameUIHooks.WaitForUIRefresh"/>：
    /// 先把待刷新的容器立即重排一次（数据层已同步、UI 树已更新，立刻重排保证"本帧即刻可见"），
    /// 再等待一个 <c>ProcessFrame</c> 让 Godot 真正完成一次绘制，然后交还控制权。
    /// 用于恢复步骤"先手弃牌 → UI 立即刷新 → 后手可见 → 后手弃牌"的严格顺序：
    /// Controller 在弃牌（Model 已改）后 await 本方法，回来后后手才弹窗，届时墓地已可见先手弃的牌。
    /// </summary>
    public async System.Threading.Tasks.Task WaitForUIRefresh()
    {
        // 1) 立即重排（同步）：把手牌/墓地/场上的数据变化立刻反映到节点位置，避免"等下一帧才归位"。
        // 防御：本方法在 Controller 的异步续算里被 await，**任何异常都不允许外泄**——
        // 否则 async void 的调用链（RunRecoverHandLimitAsync）会因未捕获异常静默中断，
        // 表现为"弃牌完成但恢复步骤不推进"。故这里吞掉一切刷新异常（刷新失败不影响规则推进）。
        try { SyncDerivedUI(); }
        catch (System.Exception ex) { GD.PrintErr($"[Arena] WaitForUIRefresh 刷新异常（忽略，不影响推进）：{ex.Message}"); }

        // 2) 等待一个渲染帧：保证 Godot 完成本次绘制，后手弹窗前画面已呈现先手的弃牌结果。
        // 防御：节点已被释放（换局/重开）或不在树内时**不等待**——否则 ToSignal 永不完成，调用方永久挂起。
        try
        {
            if (GodotObject.IsInstanceValid(this) && IsInsideTree())
            {
                var tree = GetTree();
                if (tree != null) await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        catch (System.Exception ex) { GD.PrintErr($"[Arena] WaitForUIRefresh 等待帧异常（忽略，不影响推进）：{ex.Message}"); }
    }

    /// <summary>
    /// 【P2】通用选卡 UI：让 `chooserPlayerId` 从候选卡中选一张（封印取手牌 / 检索取牌 / 弃牌共用）。
    /// View 只负责"展示候选 + 收集选择"，实际动作由 Controller 经回调执行（View 不写 Model）。
    /// </summary>
    public void PromptPickCard(int chooserPlayerId, string prompt, System.Collections.Generic.List<CardInstance> candidates,
        bool allowCancel, System.Action<CardInstance> onPicked)
    {
        if (candidates == null || candidates.Count == 0)
        {
            // 无候选：视为"不选"（检索"没找到"的唯一合法结果；弃牌侧调用方已保证候选非空）
            GD.Print($"[Arena] PromptPickCard 无候选（{prompt}）→ 回调 null");
            onPicked?.Invoke(null);
            return;
        }
        var win = _activationChoiceWindowScene?.Instantiate<ActivationChoiceWindow>();
        if (win == null)
        {
            // 无窗口兜底：由 Controller 侧的确定性兜底继续（不在此处写 Model）
            GD.Print("[Arena] PromptPickCard 无可用窗口，交由 Controller 兜底");
            onPicked?.Invoke(allowCancel ? null : candidates[0]);
            return;
        }

        var items = new System.Collections.Generic.List<ActivationChoiceItem>();
        foreach (var c in candidates)
        {
            if (c == null) continue;
            var captured = c;
            items.Add(new ActivationChoiceItem
            {
                // 【2026-09】秘社实例没有 PlayerTemplate（秘社走 CouncilTemplate）——缺这层兜底时
                // 候选列表会显示原始 UID（如 "inst_115"）。JC051 的"选择结附宿主"候选里就可能有秘社。
                Label = captured.PlayerTemplate?.CardName ?? captured.CouncilTemplate?.CardName ?? captured.UID,
                OnChosen = () => onPicked?.Invoke(captured),
            });
        }
        // allowCancel=false：必须选一张才能继续（点窗外/取消均不允许）
        win.ForceChoice = !allowCancel;
        win.CustomTitle = string.IsNullOrEmpty(prompt) ? null : prompt;
        win.Init(items);
        AddChild(win);
        if (allowCancel && win.OnCancelled == null)
            win.OnCancelled = () => { UnlockChoiceWindow(win); onPicked?.Invoke(null); };   // 取消＝不选（回调唯一性，见 ActivationChoiceWindow.OnCancelled）
        LockChoiceWindow(win);
        BindChoiceUnlock(win, items);
        win.PopupCentered();
    }

    /// <summary>
    /// 【P4·遏制X】UI：让**被遏制者**从场上各地区中选择一处移除 1 个本方势力标志。
    /// 语义（用户规则）：`X &lt; 该玩家标记总数` 时由被遏制者按地区分配，**每地区上限＝该地区标记数**；
    /// 因此 Controller（`CurbRule`）**逐次**询问（每次 1 个），本方法只负责"展示候选 + 收集选择"。
    /// X ≥ 总数时不会走到这里（自动全清、不弹窗）。
    /// View 只回传选中的地区 UID，实际移除由 Controller 在回调内落地（View 不写 Model）。
    /// </summary>
    public void PromptCurbMarks(int curbedPlayerId, int remaining,
        System.Collections.Generic.List<CurbMarkOption> options, System.Action<string> onPickedAreaUid)
    {
        if (options == null || options.Count == 0)
        {
            onPickedAreaUid?.Invoke(null);
            return;
        }
        var win = _activationChoiceWindowScene?.Instantiate<ActivationChoiceWindow>();
        if (win == null)
        {
            // 无窗口兜底：交回 Controller 走确定性分配
            GD.Print("[Arena] PromptCurbMarks 无可用窗口，交由 Controller 兜底");
            onPickedAreaUid?.Invoke(null);
            return;
        }

        var items = new System.Collections.Generic.List<ActivationChoiceItem>();
        foreach (var o in options)
        {
            if (o == null) continue;
            var captured = o;
            items.Add(new ActivationChoiceItem
            {
                Label = $"地区{captured.SlotIndex}（可移除 {captured.Removable}）",
                OnChosen = () => onPickedAreaUid?.Invoke(captured.AreaUid),
            });
        }
        // 强制选择：遏制分配给被遏制者本人，必须选一处（不取消/点窗外不关闭）
        win.ForceChoice = true;
        win.CustomTitle = $"遏制{remaining}：请选择要移除本方势力标志的地区";
        win.Init(items);
        AddChild(win);
        LockChoiceWindow(win);
        BindChoiceUnlock(win, items);
        win.PopupCentered();
    }

    /// <summary>
    /// 【P4·预测X】UI：把"牌库顶 X 张"交给预测者分配置顶/置底。
    /// 复用口径（用户指定）：**直接复用调查对抗分配窗口**（`InvestigationWindow`：拖到"牌库顶/牌库底"两区，
    /// 同一区内左右顺序即置回顺序）；差别只有：标题改为"预测X"、**不抽牌**、牌由 Controller 取出/置回。
    /// View 只负责展示与收集分配，置回由 Controller 经回调统一落地（View 不写 Model）。
    /// 无窗口时回调 (null, cards) → Controller 走确定性兜底（原序全部留在牌库顶）。
    /// </summary>
    public void PromptPrediction(int predictorId, int count, System.Collections.Generic.List<CardInstance> cards,
        System.Action<System.Collections.Generic.List<CardInstance>, System.Collections.Generic.List<CardInstance>> onConfirmed)
    {
        if (cards == null || cards.Count == 0) { onConfirmed?.Invoke(null, cards); return; }
        var win = _investigationWindowScene?.Instantiate<InvestigationWindow>();
        if (win == null)
        {
            GD.Print("[Arena] PromptPrediction 无可用窗口，交由 Controller 兜底");
            onConfirmed?.Invoke(null, cards);
            return;
        }
        AddChild(win);
        // 【2026-09】预测窗也是"玩家决策窗"：等待期间持原子锁（选完先解锁再交回 Controller）
        LockChoiceWindow(win);
        var vpSize = GetViewport().GetVisibleRect().Size;
        win.Position = new Vector2I((int)((vpSize.X - win.Size.X) / 2), (int)(vpSize.Y / 2) + 150);
        win.Popup();
        win.Configure(predictorId, 0, cards,
            (pid, toTop, toBottom) => { UnlockChoiceWindow(win); onConfirmed?.Invoke(toTop, toBottom); },
            (pid, n) => { /* 预测不是抽牌：不执行任何抽牌 */ },
            $"预测{count}：将任意数量置于牌库顶（其余置于牌库底）");
        // 未确认即关闭（点窗外/关闭按钮）→ 按"原序全部留在牌库顶"兜底交付一次（回调唯一性）
        win.CloseRequested += () => { UnlockChoiceWindow(win); onConfirmed?.Invoke(null, cards); };
    }

    /// <summary>
    /// 【K15·多目标】UI：**逐槽取目标 → 确认面板 → 确认后交回 Controller**（用户 2026-09 口径）。
    ///  · 每处理一个槽都**重新**向 Controller 索取候选（`candidatesForSlot`）——地区槽先选，
    ///    卡槽候选随之排除"已在目标地区内的角色"（FAQ 禁止原地移动）；
    ///  · 可留空的槽提供"不选（留空）"项；任何时候都能"取消（不打出）"→ `onConfirmed(null)`；
    ///  · 全部槽处理完 → **确认面板**列出已选目标：`确认（加入堆叠）` / `取消（不打出）`；
    ///  · View 只收集选择；合法性复检、付费、入堆叠一律由 Controller（`TurnManager.PlayCard`）执行。
    /// </summary>
    public void PromptMultiTargets(CardInstance source, Script.Data.Effects.EffectData data,
        System.Func<int, System.Collections.Generic.List<TargetChoice>, SlotCandidates> candidatesForSlot,
        System.Action<System.Collections.Generic.List<TargetChoice>> onConfirmed)
    {
        if (data?.TargetSlots == null || data.TargetSlots.Count == 0)
        {
            onConfirmed?.Invoke(null);
            return;
        }
        var choices = new System.Collections.Generic.List<TargetChoice>();
        for (int i = 0; i < data.TargetSlots.Count; i++) choices.Add(TargetChoice.Empty);
        int slotCount = data.TargetSlots.Count;

        // 【2026-09 用户口径 JC062/JC021】**一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消**：
        // 形状判定见 `MultiTargetSlots.ClassifyShape`（全槽＝地区槽或"场上看得见的卡槽"，且至少一个卡槽）：
        //  · JC062（两个"目标暗藏者"槽，至少 0 个）：`请选择目标暗藏者：已选择（0/2）`＋`左键选择右键取消`；
        //  · JC021（"目标地区"必选 ＋"本方/敌方角色"）：`请选择目标角色（0/2）和目标地区（0/1）`，
        //    每个玩家场上的角色只能选一个，"没有地区或没有角色时不能确认"。
        // 其余形状（含区域牌堆槽，如 JC092/JC133 的墓地角色牌）仍走既有逐槽流程。
        if (MultiTargetSlots.ClassifyShape(data) == MultiTargetSlots.MultiSlotShape.FieldMultiPick
            && TryPromptFieldMultiPick(source, data, candidatesForSlot, onConfirmed))
            return;

        AskSlot(0);

        void AskSlot(int index)
        {
            if (index >= slotCount) { Confirm(); return; }
            var cand = candidatesForSlot?.Invoke(index, choices);
            string label = (cand != null && !string.IsNullOrEmpty(cand.Label))
                ? cand.Label : MultiTargetSlots.SlotLabel(data, index);

            // 【2026-09 用户口径·A86】"场上看得见的卡一律用场上高亮选，不用列表窗"：
            // 该槽的候选若在场上可见（争夺区角色/暗藏者/附属/秘社…或地区槽），改走"**高亮 + 点击**"，
            // **不再弹按钮列表窗**（用户实测 JC062："选择查看的暗藏者应该选择场上的高亮暗藏者，
            // 而不是弹出选择框"）。候选为空 / 玩家槽 / 区域牌堆（墓地/手牌/牌库…）仍走按钮窗
            // ——那里必须保留"留空 / 取消（不打出）"出口，否则玩家会卡住（见 `ShouldPickOnField`）。
            // 选目标窗不可用（编辑器未挂场景等）→ 返回 false，就地回退按钮窗（绝不静默卡住流程）。
            if (MultiTargetSlots.ShouldPickOnField(cand) && AskSlotByFieldClick(index, cand, label)) return;

            AskSlotByListWindow(index, cand, label);
        }

        /// <summary>【K15】单槽的**候选按钮列表窗**（区域牌堆/玩家槽/无候选时的唯一可行形态：
        /// 那里有"留空 / 取消（不打出）"出口）。</summary>
        void AskSlotByListWindow(int index, SlotCandidates cand, string label)
        {
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            if (cand != null && cand.IsArea)
            {
                foreach (var uid in cand.AreaUids)
                {
                    var captured = uid;
                    items.Add(new ActivationChoiceItem
                    {
                        Label = $"地区{_areaManager?.GetSlotIndex(captured) ?? 0}",
                        OnChosen = () => { choices[index] = TargetChoice.Area(captured); AskSlot(index + 1); },
                    });
                }
            }
            else if (cand != null)
            {
                foreach (var c in cand.Cards)
                {
                    if (c == null) continue;
                    var captured = c;
                    items.Add(new ActivationChoiceItem
                    {
                        Label = captured.PlayerTemplate?.CardName ?? captured.UID,
                        OnChosen = () => { choices[index] = TargetChoice.Card(captured.UID); AskSlot(index + 1); },
                    });
                }
            }
            // 可留空的槽：给出"不选"出口（卡文"和/或"）
            if (cand == null || cand.Optional)
            {
                items.Add(new ActivationChoiceItem
                {
                    Label = "不选（留空）",
                    OnChosen = () => { choices[index] = TargetChoice.Empty; AskSlot(index + 1); },
                });
            }
            items.Add(new ActivationChoiceItem
            {
                Label = "取消（不打出）",
                OnChosen = () => onConfirmed?.Invoke(null),
            });
            OpenChoiceWindow($"选择{label}（第 {index + 1}/{slotCount} 个目标槽）", items, force: true);
        }

        /// <summary>
        /// 【2026-09·K15 多目标·场上点选】单槽的"**场上高亮 + 点击**"取目标（用户口径 A86）：
        /// 复用既有选目标通道（`TriggerInteractionCoordinator.RequestActivateTargetSelection`：
        /// 卡目标＝场上卡高亮 + 点击；地区目标＝地区槽高亮 + 点击），**不弹候选列表窗**。
        /// 窗口自带的备用按钮＝该槽的出口：可留空 → "不选（留空该槽）"；必选 → "取消（不打出）"。
        /// 返回 false ＝ 选目标窗不可用（调用方回退按钮列表窗）。
        /// </summary>
        bool AskSlotByFieldClick(int index, SlotCandidates cand, string label)
        {
            var uids = new System.Collections.Generic.List<string>();
            if (cand.IsArea)
            {
                uids.AddRange(cand.AreaUids);
            }
            else
            {
                foreach (var c in cand.Cards) if (c != null) uids.Add(c.UID);
            }
            string prompt = $"{source?.PlayerTemplate?.CardName}：{label}（第 {index + 1}/{slotCount} 槽）";
            string extraLabel = cand.Optional ? "不选（留空该槽）" : "取消（不打出）";
            return _triggerCoordinator.RequestActivateTargetSelection(prompt, cand.IsArea, uids,
                uid =>
                {
                    choices[index] = cand.IsArea ? TargetChoice.Area(uid) : TargetChoice.Card(uid);
                    AskSlot(index + 1);
                },
                extraLabel,
                () =>
                {
                    if (cand.Optional)
                    {
                        choices[index] = TargetChoice.Empty;   // 留空本槽，继续下一槽
                        AskSlot(index + 1);
                    }
                    else
                    {
                        onConfirmed?.Invoke(null);             // 必选槽取消 → 整次不打出
                    }
                },
                cand.Hint);   // 【2026-10·JC092】该槽的动作提示（卡文口径；空＝中性兜底）
        }

        void Confirm()
        {
            var items = new System.Collections.Generic.List<ActivationChoiceItem>
            {
                new ActivationChoiceItem
                {
                    Label = "确认（加入堆叠）",
                    OnChosen = () => onConfirmed?.Invoke(choices),
                },
                new ActivationChoiceItem
                {
                    Label = "取消（不打出）",
                    OnChosen = () => onConfirmed?.Invoke(null),
                },
            };
            OpenChoiceWindow($"确认目标：{MultiTargetSlots.Describe(data, choices, _cardManager)}", items, force: true);
        }
    }

    /// <summary>
    /// 【2026-09 用户口径 JC062/JC021】**"一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消"**取目标。
    ///
    /// 分组文案/上限下限/候选重算/回包映射**全部委托** `MultiTargetSlots.FieldPickPlan`（单一真相源，
    /// 与服务端联机聚合请求 `ChoiceRequest.Kind.FieldMultiPick` 同源）；本方法只做"把计划接到选目标窗上"。
    /// 用户给出的两个窗面（由计划里的 `Title` 逐字生成）：
    /// <code>
    /// 请选择目标暗藏者：已选择（0/2）           ← JC062（两个"目标暗藏者"槽，至少 0 个）
    /// 左键选择右键取消
    /// 确认   取消
    ///
    /// 请选择目标角色（0/2）和目标地区（0/1）    ← JC021（地区必选 ＋"本方/敌方角色"）
    /// 左键选择右键取消
    /// 确认   取消
    /// </code>
    /// 返回 false ＝ 该效果不是 `FieldMultiPick` 形状 / 选目标窗不可用 → 调用方回退既有逐槽流程。
    /// </summary>
    private bool TryPromptFieldMultiPick(CardInstance source, Script.Data.Effects.EffectData data,
        System.Func<int, System.Collections.Generic.List<TargetChoice>, SlotCandidates> candidatesForSlot,
        System.Action<System.Collections.Generic.List<TargetChoice>> onConfirmed)
    {
        if (_triggerCoordinator == null) return false;
        var plan = MultiTargetSlots.FieldPickPlan.Build(data, source, source?.ControllerId ?? -1,
            _zoneManager, _cardManager, _playerManager, _areaManager);
        if (plan == null) return false;

        Script.UI.Interaction.FieldCandidates CandidatesOf(Script.UI.Interaction.FieldPickState st)
        {
            var res = new Script.UI.Interaction.FieldCandidates();
            res.Cards.AddRange(plan.OpenCardCandidates(st.Cards, st.Areas));
            res.Areas.AddRange(plan.OpenAreaCandidates(st.Cards, st.Areas));
            return res;
        }

        return _triggerCoordinator.RequestFieldMultiPick(
            "左键选择右键取消", CandidatesOf, plan.MaxCards, plan.MaxAreas,
            st => plan.Title(st.Cards, st.Areas),
            st => plan.CanConfirm(st.Cards, st.Areas),
            (st, uid) => plan.CanAddCard(st.Cards, st.Areas, uid),
            (pickedCards, pickedAreas) => onConfirmed?.Invoke(plan.MapPicks(pickedCards, pickedAreas)),
            () => onConfirmed?.Invoke(null));
    }

    /// <summary>
    /// 【2026-09·JC062/JC057 用户口径】**检视结果窗**：把本次结算中被私下检视到的**全部**牌面
    /// **列在同一个窗**里（`InspectWindow`：卡面 + **只有一个『确认』按钮**，点击即关闭）。
    /// 用户口径（原文）："…结算的时候弹出一个弹窗，里面列出所有看到的信息，这个框的按钮只有一个确认，
    /// 点击后关闭弹窗"＋"JC057 的查看方式和 JC062 一致"＋"等点确认后再继续结算（抓牌排在确认之后）"。
    /// View 只展示与收集一次确认；**续算（含 JC062 的抓牌）由 Controller 经回调执行**（View 不写 Model）。
    /// </summary>
    public void PromptInspectCards(int viewerPlayerId, string title, System.Collections.Generic.List<CardInstance> cards,
        System.Action onConfirmed)
    {
        var win = new InspectWindow();
        // 【配对纪律】先解锁、再交回 Controller 续算（与 NumberInputWindow 同款；关闭窗口亦走同一回调）
        win.OnConfirmed = () => { UnlockChoiceWindow(win); onConfirmed?.Invoke(); };
        // 【2026-09 修复·用户实测"卡面显示成卡背"】**必须先入树、再 Init**：Init 里实例化 CardUI 时会
        // `Setup()+SetFace(true)`，若窗口还没入树，`CardUI._Ready()` 里的 `SetFace(false)` 会后跑把正面盖掉。
        AddChild(win);
        win.Init(title, cards);
        LockChoiceWindow(win);
        win.PopupCentered();
    }

    /// <summary>
    /// 【K11·触发版】UI：触发入堆叠前的"可选固定费用"——**是否为 N 付费**。
    /// 只有"**支付 N**"/"**不支付**"两个按钮（**不出现数值输入框**；用户实测报障："支付弹窗只需要是否两个选项，
    /// 不需要输入支付的费用"）；放弃整条触发请用**触发排序窗**，故本窗不提供"取消"。
    /// View 只收集选择，扣费/入堆叠由 Controller（`TurnManager.EnsureOptionalPayThenPush`）执行（View 不写 Model）。
    /// </summary>
    public void PromptOptionalPay(int chooserPlayerId, string prompt, int amount, System.Action<bool> onChosen)
    {
        var items = new System.Collections.Generic.List<ActivationChoiceItem>
        {
            new ActivationChoiceItem { Label = $"支付 {amount}", OnChosen = () => onChosen?.Invoke(true) },
            new ActivationChoiceItem { Label = "不支付", OnChosen = () => onChosen?.Invoke(false) },
        };
        OpenChoiceWindow(string.IsNullOrEmpty(prompt) ? $"是否支付 {amount}？" : prompt, items, force: true);
    }

    /// <summary>
    /// 【2026-09 用户实测·JC064】"支付 N，否则牺牲本卡"＝**两个按钮**（"支付 N" / "牺牲它"），
    /// **不出现数值输入框**（用户口径："JC064 支付费用的触发弹窗不需要输入框，参考 JC061"）。
    /// View 只收集选择，扣费/牺牲由 Controller（`EffectEngine` 的 `PayOrSacrificeSelf`）执行。
    /// </summary>
    public void PromptPayOrSacrifice(int chooserPlayerId, string sourceName, int amount, System.Action<bool> onChosen)
    {
        var items = new System.Collections.Generic.List<ActivationChoiceItem>
        {
            new ActivationChoiceItem { Label = $"支付 {amount}", OnChosen = () => onChosen?.Invoke(true) },
            new ActivationChoiceItem { Label = "牺牲它", OnChosen = () => onChosen?.Invoke(false) },
        };
        OpenChoiceWindow($"{sourceName}：支付 {amount}，否则牺牲它", items, force: true);
    }

    /// <summary>
    /// 【2026-09 整体复查·修复·JC128】通用**二选一确认窗**（两个按钮、无输入框）：`yesLabel` / `noLabel`
    /// 由调用方给出（如"移除标志"/"改为牺牲"），与联机 `AskZeroOrOneViaGatewayAsync` 同语义。
    /// 用户口径（JC061/JC064 同类）："这类是/否选择**不需要输入框**"。
    /// </summary>
    public void PromptTwoChoice(int chooserPlayerId, string prompt, string yesLabel, string noLabel,
        System.Action<bool> onChosen)
    {
        var items = new System.Collections.Generic.List<ActivationChoiceItem>
        {
            new ActivationChoiceItem { Label = yesLabel, OnChosen = () => onChosen?.Invoke(true) },
            new ActivationChoiceItem { Label = noLabel, OnChosen = () => onChosen?.Invoke(false) },
        };
        OpenChoiceWindow(string.IsNullOrEmpty(prompt) ? "请选择" : prompt, items, force: true);
    }

    /// <summary>
    /// 【K11·可选支付 / 数值输入】UI：弹出**数值输入框**（输入 N → 确认/取消）。
    /// 口径（用户 2026-09）：输入的数字即为 N；取消/不支付 → 回调 -1。
    /// View 只收集数字；扣费与后续动作由 Controller 执行（View 不写 Model）。
    /// </summary>
    public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
        string confirmLabel, string cancelLabel, System.Action<int> onChosen)
    {
        var win = new NumberInputWindow();
        // 【2026-09 修复·同 InspectWindow 的两个坑】**先入树再 Init**（否则 `Init` 里建的控件会被
        // `_Ready` 覆盖）+ **窗口显式定尺寸**（`Window` 默认 100×100 且不按内容撑开，`PopupCentered()`
        // 只按当前 `Size`）——此前这个数值输入窗同样是 100×100。
        AddChild(win);
        win.Init(prompt, min, max, defaultValue, confirmLabel, cancelLabel);
        // 【2026-09】先解锁、再交回 Controller（配对纪律；取消亦会回调 -1，见 NumberInputWindow.Cancel）
            win.OnConfirmed = value =>
            {
                UnlockChoiceWindow(win);
                onChosen?.Invoke(value);
                // 【BUG 修复·JC083 用户实测"输入数字点确定后没立刻刷新 UI"】回调里会**同步重入结算**
                // （如 JC083：立刻把牌库顶的牌判给手牌/牌库底）⇒ 必须在这里立刻重排/刷新 View，
                // 否则界面停留在结算前的状态，要等下一次自然刷新才更新。
                ArrangeAllCardsInternal();
                UpdateTurnUIInternal();
            };
        LockChoiceWindow(win);
        win.PopupCentered();
    }

    /// <summary>
    /// 【K16·模式分支】UI：把"选择一项"列成按钮让玩家选定（**付费之前**）。
    /// View 只收集选择（回传下标；取消 = -1），付费/入堆叠由 Controller 执行（View 不写 Model）。
    /// </summary>
    public void PromptEffectMode(CardInstance source, Script.Data.Effects.EffectData data, System.Action<int> onChosen)
        => PromptEffectModeWithAvailability(source, data, null, onChosen);

    /// <summary>
    /// 【2026-09 用户口径 JC063】"选择一项" + **各模式可用性**：`availability[i] == false` ⇒ 该模式
    /// **当前没有合法目标**（可用性由 Controller 用与取目标**同一份枚举**算好）⇒ 按钮**置灰**
    /// （`ActivationChoiceItem.Disabled`，点击不触发 OnChosen）。用户口径："JC063 两个效果如果场上完全
    /// 没有合法目标，置灰（注意两个效果要求的合法目标不一致）"。
    /// </summary>
    public void PromptEffectModeWithAvailability(CardInstance source, Script.Data.Effects.EffectData data,
        System.Collections.Generic.List<bool> availability, System.Action<int> onChosen)
    {
        if (data?.Modes == null || data.Modes.Count == 0) { onChosen?.Invoke(-1); return; }
        var items = new System.Collections.Generic.List<ActivationChoiceItem>();
        int unavailable = 0;
        for (int i = 0; i < data.Modes.Count; i++)
        {
            int captured = i;
            bool enabled = availability == null || captured >= availability.Count || availability[captured];
            if (!enabled) unavailable++;
            items.Add(new ActivationChoiceItem
            {
                Label = (data.Modes[i]?.Label ?? $"选项{captured + 1}") + (enabled ? "" : "（场上没有合法目标）"),
                Disabled = !enabled,
                OnChosen = () => onChosen?.Invoke(captured),
            });
        }
        items.Add(new ActivationChoiceItem { Label = "取消（不打出）", OnChosen = () => onChosen?.Invoke(-1) });
        GD.Print($"[Arena] 【K16 模式分支】{source?.PlayerTemplate?.CardName}：{data.Modes.Count} 个模式，其中 {unavailable} 个因『场上没有合法目标』置灰");
        OpenChoiceWindow($"{source?.PlayerTemplate?.CardName}：选择一项", items, force: true);
    }

    /// <summary>【K15】用通用选择窗展示一组按钮项（多目标逐槽选择 / 确认面板共用）。</summary>
    private void OpenChoiceWindow(string title, System.Collections.Generic.List<ActivationChoiceItem> items, bool force)
    {
        var win = _activationChoiceWindowScene?.Instantiate<ActivationChoiceWindow>();
        if (win == null)
        {
            GD.Print($"[Arena] OpenChoiceWindow 无可用窗口（{title}）");
            return;
        }
        win.ForceChoice = force;
        win.CustomTitle = title;
        win.Init(items);
        AddChild(win);
        // 【2026-09】通用决策窗持锁（覆盖：多目标逐槽/确认面板、模式选择、激活代价分配、额外费用多选、
        // 暗藏者行动选择、玩家目标…）。当前所有调用方均为 force=true（必须选一项才关闭），
        // 故不会出现"无回调关闭"；仍由 LockChoiceWindow 的 TreeExiting 兜底防泄漏。
        LockChoiceWindow(win);
        BindChoiceUnlock(win, items);
        win.PopupCentered();
    }

    /// <summary>退出场景时反订阅全部逻辑层/静态事件，避免残留回调访问已释放节点。</summary>
    public override void _ExitTree()
    {
        UnsubscribeFromManagerEvents();
        // 【鼠标悬浮预览】反注册总线（避免旧 Arena 的浮层引用残留到下一局/联机视图）
        CardPreviewBus.Detach(_hoverPreview);
        base._ExitTree();
    }

    /// <summary>
    /// 反注册 SubscribeToManagerEvents 与 _Ready 中订阅的所有逻辑层/静态事件（成对反订阅）。
    /// 其中 _zoneManager/_turnManager/_cardManager 等逻辑层对象为全局单例，跨局复用，必须反订阅，
    /// 否则旧 Arena 实例的回调会残留在其上，重开第二局时被触发而访问已释放的 UI 节点。
    /// </summary>
    private void UnsubscribeFromManagerEvents()
    {
        GD.Print($"[Arena._ExitTree] 反订阅逻辑层事件（Arena 实例 {GetInstanceId()}）");

        if (_turnManager != null)
        {
            _turnManager.OnPhaseChanged -= OnPhaseChangedHandler;
            _turnManager.OnPriorityChanged -= OnPriorityChangedHandler;
            _turnManager.OnTurnStarted -= OnTurnStartedHandler;
            _turnManager.OnTurnEnded -= OnTurnEndedHandler;
            _turnManager.OnCardDrawn -= OnCardDrawnFromTurn;
            _turnManager.OnCardPlayed -= OnCardPlayedHandler;
            _turnManager.OnAssetsChanged -= OnAssetsChangedHandler;
            _turnManager.OnFirstStrikeRecalculated -= RefreshFirstStrikeFlags;
            _turnManager.OnPriorityChanged -= RefreshActivateHighlights;
        }
        if (_initializer?.CombatManager != null)
            _initializer.CombatManager.OnAssetsChanged -= OnAssetsChangedHandler;
        if (_cardManager != null)
        {
            _cardManager.OnCardDied -= OnCardDiedHandler;
            _cardManager.OnTargetSelectionChanged -= OnTargetSelectionChangedHandler;
            _cardManager.OnCardStateChanged -= OnCardStateChangedHandler;
            _cardManager.OnCardStateChanged -= OnCardStateChangedRedrawArrows;
        }
        if (_areaManager != null)
        {
            _areaManager.OnAreaWon -= OnAreaWonHandler;
            _areaManager.OnAreaReplaced -= OnAreaReplacedHandler;
            _areaManager.OnAreaRemoved -= OnAreaRemovedHandler;
            _areaManager.OnMarksChanged -= OnMarksChangedHandler;
            _areaManager.OnWorldDeckChanged -= OnWorldDeckChangedHandler;
            _areaManager.OnAreaWonResolved -= OnAreaWonResolvedHandler;
        }
        //【MVC 收口】UI 通知总线反订阅（OnVisualDirty/CostModChanged 已从 EffectEngine 迁移至此）
        Script.Manager.UiNotificationBus.Instance.OnVisualDirty -= OnVisualDirtyHandler;
        Script.Manager.UiNotificationBus.Instance.CostModChanged -= RefreshHandCostReductions;
        if (_zoneManager != null)
        {
            _zoneManager.OnCardMoved -= SyncCardUIToZone;
            _zoneManager.OnPostMove -= OnPostMoveSyncDerivedUI;
            _zoneManager.OnPostMove -= OnPostMoveReevaluateAuras;
            _zoneManager.OnPostMove -= OnPostMoveRedrawArrows;
            _zoneManager.OnDeckReordered -= OnDeckReorderedHandler;
        }
        if (_playerManager != null)
            _playerManager.OnGameOver -= OnGameOverHandler;
        if (_stackManager != null)
            _stackManager.OnStackChanged -= RefreshStackUI;
        //【MVC 收口】IGameUIHooks 接口由 RegisterUIHooks 注入，Arena 销毁后由下一局重新注册覆盖；
        // 不再需要手动置空 static 委托。
    }

    /// <summary>
    /// 【事件驱动】重建堆叠条目 → 目标的箭头实例（增删）。何时增删由逻辑事件决定（OnStackChanged /
    /// OnPostMove / OnCardStateChanged 等），不在每帧做。端点的实时跟随由 UpdateArrowEndpoints 负责。
    /// </summary>
    private void RedrawAllArrows()
    {
        if (_arrowLayer == null) return;

        foreach (var kv in _stackEntryUIMap)
        {
            var entry = kv.Key;
            var entryUI = kv.Value;

            // 清理旧箭头
            if (_stackArrows.TryGetValue(entry, out var oldArrows))
            {
                foreach (var a in oldArrows) a.QueueFree();
                oldArrows.Clear();
            }
            else
            {
                oldArrows = new List<StackArrow>();
                _stackArrows[entry] = oldArrows;
            }

            // 目标已丢失：取消本条目所有 StackArrow，不取消其他条目箭头。
            // 说明（2026-08-11）：目标丢失总线已移除，TargetWasLost 改由结算时 IsTargetLost 置位，
            // 因此这里补一道【只读】版本号预判——目标版本号已变（离场/翻面等）时立即隐藏箭头，
            // 让 UI 在移动/翻面瞬间就有反馈，而不必等到结算。纯只读、不置位、无副作用，
            // 最终是否真丢失一律以结算时 IsTargetLost 的判定为准。
            bool uiLost = entry.TargetWasLost;
            if (!uiLost && entry.TargetVersionId >= 0 && !string.IsNullOrEmpty(entry.TargetCardUid))
            {
                var tgt = _cardManager?.GetInstance(entry.TargetCardUid);
                uiLost = tgt == null || tgt.VersionId != entry.TargetVersionId;
            }
            if (uiLost)
                continue;
            if (entry.Targets == null) continue;
            // 箭头按控制者（ControllerId）着色（核心需求 / D31）：夺控场景下控制者颜色，而非拥有者颜色。
            Color col = entry.ControllerId == 1 ? Colors.OrangeRed : Colors.CornflowerBlue;

            foreach (var target in entry.Targets)
            {
                // 创建箭头实例（不在此设端点，端点交由 UpdateArrowEndpoints 每帧跟随）
                var arrow = _stackArrowScene.Instantiate<StackArrow>();
                arrow.ZIndex = Layers.Arrows;
                arrow.ZAsRelative = false;   // 绝对 ZIndex
                _arrowLayer.AddChild(arrow);
                arrow.SetColor(col);
                oldArrows.Add(arrow);
            }
        }

        // 实例增删完成后立即做一次端点跟随，避免本帧内箭头无端点（闪空）
        UpdateArrowEndpoints();
    }

    /// <summary>
    /// 【每帧跟随】更新已存在箭头的起点/终点，使箭头贴紧卡实体/效果条目实时坐标。
    /// 起点 = 堆叠条目 UI 全局中心（随 VBox 布局/平滑移动实时变化）；终点 = 目标卡/地区/玩家实时中心。
    /// 仅更新端点并 QueueRedraw，不增删箭头实例（增删归 RedrawAllArrows 事件驱动）。
    /// 这是 MTGA 式「表现层每帧 reconcile」——事件驱动决定「有哪些箭头」，每帧跟随决定「端点画在哪」。
    /// </summary>
    private void UpdateArrowEndpoints()
    {
        if (_arrowLayer == null) return;

        foreach (var kv in _stackArrows)
        {
            var entry = kv.Key;
            var arrows = kv.Value;
            if (arrows == null || arrows.Count == 0) continue;
            if (!_stackEntryUIMap.TryGetValue(entry, out var entryUI)) continue;
            if (entry.Targets == null) continue;

            // 起点 = 条目 UI 全局中心（每帧实时读，覆盖 VBox 布局延迟 / 未来平滑移动）
            Vector2 from = entryUI.GetGlobalCenter();

            // 箭头与 entry.Targets 按创建顺序一一对应（RedrawAllArrows 内按 Targets 顺序 Add）
            int n = Mathf.Min(arrows.Count, entry.Targets.Count);
            for (int i = 0; i < n; i++)
            {
                Vector2? to = ResolveTargetCenter(entry.Targets[i]);
                if (to == null) continue;
                arrows[i].SetPoints(from, to.Value);
            }
        }
    }

    /// <summary>根据目标引用定位目标节点中心点（全局坐标）。找不到返回 null。</summary>
    private Vector2? ResolveTargetCenter(StackTargetRef target)
    {
        switch (target.Kind)
        {
            case StackTargetRef.RefKind.Card:
                if (_cardUIMap.TryGetValue(target.CardUid, out var cardUI))
                {
                    // 使用旋转后视觉包围盒中心，兼容横置卡（90° 旋转后视觉中心已偏离逻辑中心）
                    return cardUI.GetVisualGlobalRect().GetCenter();
                }
                return null;
            case StackTargetRef.RefKind.Area:
                // 优先用 AreaUid 精确定位（AreaSlot 节点命名 AreaSlot_<编号>，编号由 AreaUID 映射）
                Control slotNode = null;
                if (!string.IsNullOrEmpty(target.AreaUid))
                {
                    int no = GetAreaNo(target.AreaUid);   // 由地区 UID → 1-based 编号
                    if (no > 0)
                        slotNode = _areasContainer.GetNodeOrNull<Control>($"AreaSlot_{no}");
                }
                // 回退：直接用解析出的 AreaSlotId
                if (slotNode == null && target.AreaSlotId > 0)
                    slotNode = _areasContainer.GetNodeOrNull<Control>($"AreaSlot_{target.AreaSlotId}");
                if (slotNode != null) return slotNode.GlobalPosition + slotNode.GetRect().Size / 2f;
                return null;
            case StackTargetRef.RefKind.Player:
                // 玩家区中心（用对应 PlayerArea 的全局中心近似）
                var pArea = GetNodeOrNull<Control>(target.PlayerId == 1 ? "Player1Area" : "Player2Area");
                if (pArea != null) return pArea.GlobalPosition + pArea.GetRect().Size / 2f;
                return null;
            default:
                return null;
        }
    }

    public override void _Process(double delta)
    {
        // 拖拽手牌时实时高亮合法目标卡（黄色蒙版：仅标识"打出附属卡可结附目标"）
        // 基础行动蒙版（建立资产/派遣暗藏）拖拽期间不显示该高亮，避免场上角色卡被错误打上黄色蒙版
        // 【MVC 修复 M-08】拖拽状态与高亮逻辑已迁移至 CardDragController，此处经控制器转发。
        if (_cardDragController != null && _cardDragController.IsDraggingUnderNoneMask)
            _cardDragController.UpdateDragTargetHighlights();

        // D9 激活框架：激活拖拽中实时更新箭头与目标高亮
        if (_cardDragController != null && _cardDragController.IsActivating)
            _cardDragController.UpdateActivateDrag(GetGlobalMousePosition());

        // 堆叠箭头：事件驱动负责「增删箭头实例」（RedrawAllArrows），每帧跟随负责「更新端点」（UpdateArrowEndpoints）。
        // 堆叠非空即每帧更新端点，使箭头贴紧条目/目标实时坐标（覆盖 VBox 布局延迟，并为未来平滑移动铺路）。
        // 空堆叠/无箭头时零开销（UpdateArrowEndpoints 内部空循环直接返回）。
        if (_stackManager != null && !_stackManager.IsEmpty && _arrowLayer != null)
            UpdateArrowEndpoints();
    }

    // [诊断] 用于确认输入事件分发链路：打印鼠标按下时 Arena 是否收到、以及当前鼠标下命中的卡牌
    private void OnArenaGuiInputDiagnostic(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            Vector2 mp = mb.GlobalPosition;
            GD.Print($"[Arena.Diag] 鼠标按下 @({mp.X:F0},{mp.Y:F0})");
            // 检测鼠标位置是否落在任意手牌卡的可视包围盒内（使用 CardUI 的 GetVisualGlobalRect）
            int hitHand = 0;
            foreach (var c in _handCardUIs)
            {
                if (c != null && c.GetVisualGlobalRect().HasPoint(mp)) hitHand++;
            }
            foreach (var c in _handCardUIs2)
            {
                if (c != null && c.GetVisualGlobalRect().HasPoint(mp)) hitHand++;
            }
            GD.Print($"[Arena.Diag] 手牌中命中 {hitHand} 张");
        }
    }

    // ============================================================
    // 订阅逻辑层事件（核心：表现层仅通过事件获知状态变化）
    // ============================================================
    // 【M-09 结构拆分】SubscribeToManagerEvents 及全部事件处理器已迁至 Arena.Events.cs
    // （仍为本类的 partial 成员，行为不变）。

    // ============================================================
    // 初始化 UI 子模块
    // ============================================================
    private void InitializeWorldDeckZone()
    {
        _worldDeckZone = GetNode<WorldDeckZone>("CenterArea/WorldDeckZone");
        if (_worldDeckZone != null)
        {
            _worldDeckZone.Visible = true;
            // 位置与尺寸由场景文件(Arena.tscn)定义:左侧 offset_left=50, offset_top=250, 尺寸 140x100
            // 不在此处用代码覆盖,避免面板被挪到右侧或撑大

            _worldDeckZone.Initialize(_worldDeckManager?.Count ?? 0, _areaCardUIScene);
        }
        else
        {
            GD.PrintErr("[Arena] ❌ WorldDeckZone 节点未找到！路径: CenterArea/WorldDeckZone");
        }
    }

    /// <summary>给 AreaSlot 注入统计所需的逻辑层依赖（ZoneManager/PlayerManager），再 Setup</summary>
    private void ConfigureSlot(AreaSlot slot, CardInstance instance, int index)
    {
        slot.SetStatsDependencies(_zoneManager, _playerManager);
        slot.Setup(instance, index, false, _areaCardUIScene);
    }

    /// <summary>刷新所有地区 slot 的能力统计 Label（进出地区/横置/先手切换时调用）</summary>
    private void RefreshAllAreaStatsLabels()
    {
        foreach (var slot in _areaSlots)
            slot?.RefreshStatsLabels();
    }

    private void InitializeAreas()
    {
        // 抽取+注册初始地区已收口到 PlayerActionService.SetupInitialAreas（与联机服务端 NetMatchEntry 共用）。
        List<CardInstance> initialAreas = _actionService.SetupInitialAreas(3);
        _areaSlots.Clear();
        foreach (Node child in _areasContainer.GetChildren())
            child.QueueFree();

        for (int i = 0; i < initialAreas.Count; i++)
        {
            var slot = _areaSlotScene.Instantiate<AreaSlot>();
            _areasContainer.AddChild(slot);
            slot.Name = $"AreaSlot_{i + 1}";
            ConfigureSlot(slot, initialAreas[i], i + 1);
            _areaSlots.Add(slot);
        }
        // AreaManager 已改为纯逻辑层，不再持有 UI 引用；标记/替换均通过事件通知本类刷新
        ArrangeAreas();
        _worldDeckZone?.RefreshDisplay(_worldDeckManager?.Count ?? 0);
    }

    private void InitializeDeckAndGraveyard()
    {
        if (_deckZone != null)
            _deckZone.Initialize(_deckManager.GetPlayer1DeckData(), _cardUIScene, 1);
        if (_graveyardZone != null)
            _graveyardZone.Initialize(_cardUIScene);
        if (_deckZone2 != null)
            _deckZone2.Initialize(_deckManager.GetPlayer2DeckData(), _cardUIScene, 2);
        if (_graveyardZone2 != null)
            _graveyardZone2.Initialize(_cardUIScene);
    }

    private void InitializePMCommands()
    {
        //【MVC 修复】PMCommandManager 的生命周期由 GameInitializer（Composition Root）统一管理，
        //此处仅获取其引用并注入 UI 回调，不再由 View 自行 new + AddChild。
        _pmCommandManager = _initializer.PMCommandManager;
        if (_pmCommandManager == null)
        {
            //【MVC 修复 M-03】禁止 View 自建：PMCommandManager 生命周期归 Composition Root 独占。
            GD.PushError("Arena: GameInitializer 未创建 PMCommandManager，PM 调试命令不可用。");
            return;
        }
        // 方案②：向 PM 注入"实时查询地区 UID 列表"的 Func，每次命令执行时按槽位顺序取当前 UID，
        // 避免地区赢取/替换后旧快照过期（PM 命令落到失效槽位）。不传递 UI 实例/列表类型，符合 MVC 边界。
        _pmCommandManager.Initialize(
            _cardManager, _deckManager,
            () => _areaSlots.Select(s => s.AreaUID).ToList(),
            (int playerId, int count) => _actionService.DrawCards(playerId, count),
            _zoneManager,
            // 【2026-10】`onZoneChanged` 是所有 PM 命令的"UI 收尾"出口：除整场重排与派生统计外，
            // 还需刷新卡面**伤害/创伤数字** —— 否则 `damage`/`trauma`/`kill`/`heal` 这类只改模型的命令
            // 在界面上毫无变化（用户实测"kill/damage 无法使用"）。
            () => { ArrangeAllCards(); RefreshDamageDisplay(); RefreshArenaStats(); },
            (string uid) =>   // 仅刷新指定卡的 UI（横置/数值同步），不返回 UI 实例
            {
                var ui = FindCardUIByUID(uid);
                var inst = _cardManager.GetInstance(uid);
                if (ui != null && inst != null)
                {
                    ui.SetTapped(inst.IsTapped);
                    ui.RefreshStats();
                }
            },
            (string uid) => FindCardUIByUID(uid)?.IsFaceUp ?? false,   // 读取翻面状态
            (string uid, bool face) => FindCardUIByUID(uid)?.SetFace(face), // 设置翻面状态
            (CombatManager.CombatType type, int winner, int x, int areaNo) =>
            {
                // 【2026-10 修复】不再直调窗口协调器（那会绕过 CombatManager.ResolveWinner，
                // 导致 PM `investigation` 不广播 OnInvestigationWon 获胜触发、也不置 IsDamageAssignmentOpen）。
                // 现统一经 CombatManager.ResolveCombatFromDebug → 单机走 ResolveWinner → 事件链弹本机窗口，
                // 与"正规对抗结算"完全同一条链路。联机侧同一条命令由 MatchHost 侧直接调该方法（无本窗口回调）。
                _initializer.CombatManager.ResolveCombatFromDebug(type, areaNo, winner, x);
            },
            _areaManager,
            () => DrawAndAddNewArea(),
            _stackManager
        );

        _commandInput = GetNodeOrNull<TextEdit>("CommandInput");
        if (_commandInput != null)
        {
            _commandInput.Visible = false;
            _commandInput.ZIndex = CommandInputZIndex;   // 拉到最上层：否则秘社/卡牌会压在命令框上方
            // 多行输入：Enter 换行，Shift+Enter 执行，` / ~ 开关（均在 _GuiInput 中拦截）
            _commandInput.GuiInput += OnCommandInputGuiInput;
        }
        else
        {
            GD.PrintErr("❌ Arena: 未找到 CommandInput(TextEdit) 节点");
        }
    }

    // ============================================================
    // 事件处理器（TurnManager / CardManager / AreaManager / PlayerManager）
    // ============================================================
    // 【M-09 结构拆分】全部 On*Handler 及计分区刷新方法已迁至 Arena.Events.cs
    // （仍为本类的 partial 成员，行为不变）。

    // ============================================================
    // 拖拽处理（玩家卡）— 仅负责 UI，逻辑验证全部委托给 TurnManager
    // ============================================================
    // 拖拽处理（玩家卡）已迁移至 CardDragController（MVC 修复 M-08）。
    // OnCardDragEnded / ReturnCardToHand 不再由 Arena 直接实现，
    // 经 _onCardDragEndedForward 转发至 _cardDragController.OnCardDragEnded。



    // 蒙版拖放处理（派遣暗藏 / 建立资产）已迁移至 CardDragController.HandleMaskDrop（MVC 修复 M-08）。

    // ============================================================
    // 按钮事件 → 经 PlayerActionService 表达意图
    // ============================================================
    public void OnPlayerPass()
    {
        // 模态窗口打开期间冻结 Pass（原子操作执行期间不给响应权，核心需求 7.3）
        if (IsInteractionLocked) return;
        _actionService.Pass(1);
        UpdateTurnUI();
    }

    public void OnOpponentPass()
    {
        if (IsInteractionLocked) return;
        _actionService.Pass(2);
        UpdateTurnUI();
    }

    public void OnEndTurnPressed()
    {
        GD.Print("⚠️ 使用调试功能强制结束回合 [未实现]");
    }

    /// <summary>投降按钮：本地玩家(1)表达投降意图，经 PlayerActionService 收口判负（UI 不写规则）。</summary>
    public void OnConcedePressed()
    {
        if (_playerManager == null || _playerManager.IsGameOver) return;
        _actionService.Concede(1);
    }

    // 【M-09 结构拆分】OnGameOverHandler 已迁至 Arena.Events.cs（仍为本类的 partial 成员）。

    // ============================================================
    // 基础行动蒙版 / 回合状态栏
    // ============================================================
    // 【M-09 结构拆分】蒙版（ToggleMask/EnterMask/ExitMask/RefreshMaskUI/RefreshHandMask/
    // ApplyMaskToOneCard/UpdateMaskHint/UpdateActionButtons）与 UpdateTurnUI/UpdateDeckCount
    // 已迁至 Arena.Mask.cs（仍为本类的 partial 成员，行为不变）。

    // ============================================================
    // 刷新争夺区能力值
    // ============================================================
    /// <summary>
    /// 根据当前先后手刷新【争夺区内】所有卡牌的 StatsContainer 显示（手牌/资产区不参与对抗，不刷新）。
    /// 先手加成计算已上收至 Controller（TurnManager.RefreshFirstStrikeStats），本方法只负责把
    /// Controller 算好的 CardInstance.CurrentXxx 同步到 UI（View 只读，不再写 Model）。
    /// </summary>
    private void RefreshArenaStats()
    {
        if (_turnManager == null) return;
        // 规则收口：由 Controller 统一重算争夺区内所有卡的先手加成显示值
        _actionService.RefreshFirstStrikeStats();
        if (_zoneManager == null) return;
        foreach (var inst in _zoneManager.GetZoneCards(CardZone.AreaZone, 0))
        {
            FindCardUIByUID(inst.UID)?.RefreshStats();
        }
    }

    /// <summary>
    /// 附属结附在地区内角色上时刷新该角色及其附属的对抗属性显示。
    /// 结附在角色上的附属会为当前地区提供对抗属性，在此统一重算
    /// 角色 + 其所有附属的显示能力值（实例 CurrentXxx 与 UI StatsContainer）。
    /// </summary>
    private void RefreshAttachedStats(CardInstance character)
    {
        if (character == null) return;
        // 重算角色本体与其附属的先攻/对抗属性显示（RefreshFirstStrikeStats 为 TurnManager 既有方法）。
        // 卡牌实例本身的 UI 刷新由 UiNotificationBus 总线处理，此处不重复调用实例方法。
        _turnManager?.RefreshFirstStrikeStats();
        foreach (var attached in character.AttachedCards ?? new System.Collections.Generic.List<CardInstance>())
        {
            _turnManager?.RefreshFirstStrikeStats();
        }
    }

    // 布局引擎（ArrangeAllCards / ArrangeAllAttachments / ArrangeAllSeals / ArrangeCouncilCards /
    // FindCardUIByUID 及全套 Arrange* / Layout / Place 方法）已迁移至 Arena.Layout.cs

    // ============================================================
    // 逻辑→UI 同步（订阅 ZoneManager.OnCardMoved）
    // ============================================================
    private void BuildCardUIMap()
    {
        _cardUIMap.Clear();
        if (_deckZone != null)
            foreach (var c in _deckZone.GetCards())
                if (c?.Instance != null) _cardUIMap[c.Instance.UID] = c;
        if (_deckZone2 != null)
            foreach (var c in _deckZone2.GetCards())
                if (c?.Instance != null) _cardUIMap[c.Instance.UID] = c;

        // 统一订阅横置变化：任一张卡横置/解除横置都会刷新所有地区能力统计 Label
        foreach (var cardUI in _cardUIMap.Values)
        {
            if (cardUI == null) continue;
            cardUI.TappedChanged -= OnAnyCardTappedChanged;
            cardUI.TappedChanged += OnAnyCardTappedChanged;
        }
    }

    /// <summary>任一卡牌横置状态变化 → 重排场上布局并刷新所有派生 UI。
    /// 经由统一绑定层 SyncDerivedUI，避免与移动/回合切换的刷新契约分叉（问题 4）。</summary>
    private void OnAnyCardTappedChanged(CardUI cardUI, bool tapped)
    {
        SyncDerivedUI();
    }

    // 玩家分流辅助方法、Sync 系列、Arrange 系列、Layout/Place 方法
    // 已全部迁移至 Arena.Layout.cs（partial 实现，无需在主文件保留声明）

    // ============================================================
    // 触发排序 / 选目标交互（MVC 修复 M-08）
    // 原逻辑已迁移至 TriggerInteractionCoordinator（实现 ITriggerView），
    // Arena 仅在输入路由中转发选目标点击到协调器。
    // ============================================================



    // ============================================================
    // 公开方法
    // ============================================================
    public void AddNewArea(CardInstance areaInstance)
    {
        if (areaInstance == null) { GD.PrintErr("❌ AddNewArea: areaInstance 为空"); return; }
        if (_areaSlotScene == null) { GD.PrintErr("❌ AddNewArea: _areaSlotScene 未加载"); return; }

        int newIndex = _areaSlots.Count + 1;
        var slot = _areaSlotScene.Instantiate<AreaSlot>();

        // 必须先 AddChild 让 _Ready 执行（绑定 CardContainer 等子节点），再 Setup 填入地区卡
        _areasContainer.AddChild(slot);
        slot.Name = $"AreaSlot_{newIndex}";
        ConfigureSlot(slot, areaInstance, newIndex);

        _areaSlots.Add(slot);
        _actionService.RegisterArea(slot.AreaUID, newIndex, areaInstance);   // 同步逻辑注册表（任务地区额外槽位）
        ArrangeAreas();
        _worldDeckZone?.RefreshDisplay(_worldDeckManager?.Count ?? 0);
    }

    /// <summary>
    /// 从世界牌库抽出一张新的地区卡，生成新的 AreaSlot 并填入 CardContainer，
    /// 随后重新排列所有 AreaSlot（放不下则重叠，右侧压左侧）。
    /// </summary>
    /// <returns>成功返回 true</returns>
    public bool DrawAndAddNewArea()
    {
        if (_areaManager == null) { GD.PrintErr("❌ AreaManager 未初始化"); return false; }

        CardInstance data = _actionService.DrawNewArea();
        if (data == null) return false;

        AddNewArea(data);
        GD.Print($"✅ 新增地区：{data.AreaTemplate?.CardName}（当前共 {_areaSlots.Count} 个地区，世界牌库剩余 {_areaManager.WorldDeckCount} 张）");
        return true;
    }

    public void ClearHand()
    {
        foreach (var card in _handCardUIs) card.QueueFree();
        _handCardUIs.Clear();
        _zoneManager?.ClearHand(1);
        ArrangeAllCards();
    }

    public void ReturnCardToDeck(CardUI cardUI, bool toTop = false)
    {
        int playerId = _handCardUIs2.Contains(cardUI) ? 2 : 1;
        _handCardUIs.Remove(cardUI);
        _handCardUIs2.Remove(cardUI);
        _areaCardUIs.Remove(cardUI);

        var instance = cardUI.Instance;
        if (instance != null)
        {
            // 委托 PlayerActionService 收口：经 ZoneManager 统一移动（避免 UI 越权直调 ZoneManager.MoveToDeckBottom）
            _actionService.ReturnCardToDeck(playerId, instance.UID, toTop);
            // 数据移动经 FireMoved(Deck) 触发 SyncCardUIToZone→ReturnCardUI 完成 UI 同步；
            // 此处再显式调 ReturnCardUI 冗余，但因 MoveToDeckBottom 内部已 FireMoved，
            // 故本行实际走的是与 Sync 一致的【单张通道】。若日后 MoveToDeckBottom 改为不 FireMoved，需同步确认此处。
            GetDeckZone(playerId)?.ReturnCardUI(cardUI, toTop);
            ArrangeAllCards();
            UpdateDeckCount();
        }
    }

    // ============================================================
    // 对抗窗口（调查/战斗）
    // ============================================================
    // 对抗窗口生命周期（MVC 修复 M-08）
    // 事件路由与窗口实例化逻辑已迁移至 ModalWindowCoordinator，
    // Arena 仅保留：OnDeckReorderedHandler（牌库按序重绘）、RefreshFirstStrikeFlags（先手加成数值刷新）、
    // PickStrengthCardAt（命中测试，经协调器读取当前战斗窗口）、GetAreaNo。

    /// <summary>OnDeckReordered 处理器：按 ZoneManager 编排后的有序牌库列表，重建对应 DeckZone 的 CardUI 顺序（纯重绘）。</summary>
    private void OnDeckReorderedHandler(int playerId, List<CardInstance> orderedDeck)
    {
        var deckZone = playerId == 1 ? _deckZone : _deckZone2;
        deckZone?.RebuildFromOrdered(orderedDeck);
    }

    /// <summary>【MVC 收口-P2】先手加成重算完成后（TurnManager 触发），统一刷新所有在场卡 UI 的能力数值。
    /// 先手加成已体现在 CardInstance.CurrentXxx（由 Controller 算好），View 仅通过 RefreshStats 重绘数值，
    /// 不再在渲染过程中反向驱动 Controller 重算。</summary>
    private void RefreshFirstStrikeFlags()
    {
        if (_zoneManager == null) return;
            foreach (var inst in _zoneManager.GetZoneCards(CardZone.AreaZone, 0))
            {
                var ui = FindCardUIByUID(inst.UID);
                if (ui != null && GodotObject.IsInstanceValid(ui)) ui.RefreshStats();
            }
        // GD.Print("[Arena.UI] 先手加成能力数值已按 Controller 重算结果统一刷新");
    }

    /// <summary>D9 激活框架：刷新所有场上卡的“可发动”高亮边框。
    /// 仅当该卡控制者当前拥有优先权、费用足够、且有合法目标时高亮。
    /// 订阅 TurnManager.OnPriorityChanged 触发；初始化及每次优先权转移后调用。</summary>
        private void RefreshActivateHighlights(int playerId = 0)
        {
            if (_turnManager == null || _playerManager == null || _cardManager == null || _zoneManager == null) return;
            _activateLegalTargetCache.Clear();
            // 可发动遍历范围：争夺区(AreaZone) 卡 + 附属/封印子区(AttachedZone) 的附属卡。
            // AttachedZone 中的封印卡(IsSeal)无激活 timing，CanActivate 自然为 false，不会被高亮。
            // 结附秘社的附属卡也落在 AttachedZone，此前漏遍历导致双击无高亮、无法激活（本次修复）。
            var activatableCards = new List<CardInstance>(_zoneManager.GetZoneCards(CardZone.AreaZone, 0));
            activatableCards.AddRange(_zoneManager.GetZoneCards(CardZone.AttachedZone, 0));
            foreach (var inst in activatableCards)
            {
                var ui = FindCardUIByUID(inst.UID);
                // 过滤已释放（QueueFree 后 IsInstanceValid=false）的卡 UI：赢地区移入分数区等路径可能
                // 释放节点但 _cardUIMap 中仍保留引用，访问其 Label 会抛 ObjectDisposedException，中断本 handler。
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                bool can = ActivateEligibility.CanActivate(inst, _turnManager, _playerManager, _cardManager);
                ui.SetActivatableHighlight(can);
                // 【MVC 收口-问题A】仅对"**可用拖拽表达**的激活"预计算合法目标集合，供拖拽控制器只读查询。
                // 【2026-09】能力取法改为 `GetFirstActivateAbility`（模板 ∪ 运行期授予，JC019 宿主可用），
                // 且只缓存 `CanDragActivate` 成立者（多能力/无目标/玩家/地区目标 ⇒ 不进拖拽，交给双击）。
                if (can && ActivateEligibility.CanDragActivate(inst, out var ability))
                {
                    bool canSelf = ability.Target?.CanTargetSelf == true;
                    var set = new HashSet<string>();
                    foreach (var t in _zoneManager.GetZoneCards(CardZone.AreaZone, 0))
                    {
                        if (t == inst && !canSelf) continue;   // A62：声明"目标含自身"时来源卡也是合法目标
                        if (ActivateEligibility.IsLegalActivateTarget(inst, ability, t, _cardManager))
                            set.Add(t.UID);
                    }
                    if (set.Count > 0) _activateLegalTargetCache[inst.UID] = set;
                }
            }
            // 【2026-10 用户口径·JC085】**墓地行动**也要有"可行动"高亮（用户："墓地中的卡有行动效果可以激活时
            // 要有高亮"）：判据＝`TurnManager.CanPlayFromGraveyardNow`（与双击入口**同一实现**），
            // 双击 ⇒ 弹激活效果窗 ⇒ 选地区 ⇒ 正面打出。
            foreach (var inst in _zoneManager.GetZoneCards(CardZone.Graveyard, 0))
            {
                if (inst?.PlayerTemplate == null || !inst.PlayerTemplate.PlayableFromGraveyard) continue;
                var ui = FindCardUIByUID(inst.UID);
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                ui.SetActivatableHighlight(_turnManager.CanPlayFromGraveyardNow(inst, out _));
            }
        }

        /// <summary>D9：取某卡的第一个角色/附属激活能力（**模板 ∪ 运行期授予**，与拖拽控制器同源）。</summary>
        private static EffectData GetFirstActivateAbility(CardInstance inst)
        {
            if (inst?.PlayerTemplate is PlayerCardData)
                return ActivateEligibility.GetFirstActivateAbility(inst);
            if (inst?.CouncilTemplate is CouncilCardData councilTmpl)
            {
                foreach (var opt in councilTmpl.ActivationOptions)
                {
                    if (opt?.EffectDataList == null) continue;
                    foreach (var e in opt.EffectDataList)
                    {
                        if (e.Timing is EffectTiming.CouncilActivated) return e;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 议会（秘社）卡双击激活处理。
        /// 由 CouncilCardUI.CouncilDoubleClicked 信号触发（订阅见 SyncCouncilsUI / SyncCouncilCardUI）。
        /// 行为：当前控制者需拥有优先权且议会可激活时，激活其第一个激活能力（无目标直接发动）。
        /// 注：本方法在 Arena.Layout.cs 拆分迁移时随 SyncCouncilsUI 区域一并整理，
        /// 原双击弹窗/选目标交互如需增强可在此扩展（如复用 ModalWindowCoordinator）。
        /// </summary>
        private void OnCouncilDoubleClicked(CouncilCardUI cardUI)
        {
            if (cardUI?.Instance == null) return;
            var inst = cardUI.Instance;
            if (inst.IsTapped)
            {
                GD.PrintErr($"[Arena] 已横置，无法使用秘社效果: {inst.CouncilTemplate?.CardName}");
                return;
            }
            if (_turnManager == null || _playerManager == null || _cardManager == null) return;
            bool canAct = ActivateEligibility.CanActivate(inst, _turnManager, _playerManager, _cardManager);
            if (!canAct)
            {
                return;
            }
            var councilTmpl = inst.CouncilTemplate;
            if (councilTmpl == null || councilTmpl.ActivationOptions.Count == 0)
            {
                // 无选项：走默认单一激活
                var ability = GetFirstActivateAbility(inst);
                if (ability == null)
                {
                    return;
                }
                BeginActivate(inst, ability, null, -1);
                RefreshActivateHighlights();
                return;
            }

            // 多选项：弹出选择窗口，取消则不发动
            if (_councilActivateWindowScene == null)
            {
                GD.PrintErr("[Arena] 秘社激活弹窗场景未加载");
                return;
            }
            var win = _councilActivateWindowScene.Instantiate<CouncilActivateWindow>();
            AddChild(win);
            // 【2026-09】秘社"宣布使用效果"的选择窗同样属决策等待 → 持锁（取消不发动时由 TreeExiting 兜底释放）
            LockChoiceWindow(win);
            win.Init(councilTmpl.ActivationOptions, (opt) =>
            {
                UnlockChoiceWindow(win);
                if (opt?.EffectDataList == null || opt.EffectDataList.Count == 0) return;
                // 取第一个 EffectData 发动（本秘社两选项均单一 EffectData、无目标）
                BeginActivate(inst, opt.EffectDataList[0], null, -1);
                RefreshActivateHighlights();
            });
        }

        /// <summary>
        /// 【2026-10 用户口径·JC085】**墓地行动**：墓地中声明 `PlayerCardData.PlayableFromGraveyard` 的卡
        /// 双击 → **唤出技能/激活选择窗**（用户口径："只有一个激活效果也要弹出窗口"）→ 选该项后再弹
        /// **选择地区窗** → 从墓地正面打出（走生产同款 `_actionService.PlayCard`）。
        /// 口径（卡文）："你可以从墓地中正面打出复生行尸，就如同其在你的手上一样（仍需要支付其费用，且需满足其忠诚需求）"
        /// ⇒ 本方法只负责"选项 + 选地区 + 提交意图"，费用/忠诚/入堆叠由 Controller 校验（View 不写 Model）。
        /// 门禁＝`TurnManager.CanPlayFromGraveyardNow`（与高亮同一实现）。
        /// </summary>
        private void OnGraveyardPlayRequested(CardUI cardUI)
        {
            var inst = cardUI?.Instance;
            if (inst == null) return;
            var tmpl = inst.PlayerTemplate;
            if (tmpl == null || !tmpl.PlayableFromGraveyard)
            {
                GD.Print($"[Arena] 墓地行动忽略：{tmpl?.CardName} 未声明『墓地行动』");
                return;
            }
            if (!_turnManager.CanPlayFromGraveyardNow(inst, out string why))
            {
                GD.Print($"[Arena] 墓地行动忽略：{tmpl.CardName} → {why}");
                RefreshActivateHighlights();
                return;
            }

            // 第一级：**技能/激活选择窗**（即使只有一条也弹，对齐"双击弹出选择效果的面板"口径）
            var win = _activationChoiceWindowScene?.Instantiate<ActivationChoiceWindow>();
            if (win == null)
            {
                GD.PrintErr("[Arena] 墓地行动：激活选择窗不可用（编辑器未挂场景）→ 直接进入选地区");
                OpenGraveyardAreaChoice(inst);
                return;
            }
            var items = new List<ActivationChoiceItem>
            {
                new ActivationChoiceItem
                {
                    Label = $"墓地行动：从墓地正面打出「{tmpl.CardName}」（费用 {Mathf.Max(0, tmpl.Cost)}）",
                    OnChosen = () => OpenGraveyardAreaChoice(inst),
                },
                new ActivationChoiceItem { Label = "取消", OnChosen = () => { } },
            };
            win.Init(items);
            AddChild(win);
            win.PopupCentered();
            GD.Print($"[Arena] 墓地行动：{tmpl.CardName} 双击 → 弹技能选择窗（1 条）");
        }

        /// <summary>【2026-10·JC085】墓地行动第二级：**选择地区窗** → `PlayCard{目标地区}`（正面打出）。</summary>
        private void OpenGraveyardAreaChoice(CardInstance inst)
        {
            var tmpl = inst.PlayerTemplate;
            var areas = _areaManager?.GetOrderedAreaUids() ?? new List<string>();
            if (areas.Count == 0)
            {
                GD.PrintErr($"[Arena] 墓地行动：当前没有可放置的地区（{tmpl?.CardName}）");
                return;
            }
            _triggerCoordinator.RequestActivateTargetSelection(
                $"{tmpl?.CardName}（墓地行动）",
                true,                       // isArea：走地区槽位高亮 + 点击
                areas,
                uid =>
                {
                    if (string.IsNullOrEmpty(uid)) return;
                    bool ok = _actionService.PlayCard(inst.ControllerId, inst.UID, uid, null);
                    GD.Print($"[Arena] 墓地行动：{tmpl?.CardName} → 地区 {uid} " +
                             (ok ? "打出成功（已入堆叠）" : "被拒（见 Controller 日志：费用/忠诚/阶段）"));
                    RefreshActivateHighlights();
                },
                "取消（不打出）");
        }

        /// <summary>
        /// 激活能力选择框（附属 / **争夺区正面角色**）：列出所有可激活的 AttachmentActivated/CharacterActivated
        /// 效果供玩家选其一发动（多条＝弹窗，单条＝直接发动）。
        /// 【2026-09 修复·JC019】候选改为 `ActivateEligibility.GetAllActivateAbilities`＝**模板 ∪ 运行期授予**：
        /// 此前只读 `PlayerTemplate.EffectDataList` ⇒ JC019 授予宿主的激活能力（模板里没有）永远列不出来，
        /// 宿主高亮却双击无反应。授予项由该入口**追加在模板项之后**（宿主原有激活效果时窗口自动多出这一条）。
        /// </summary>
        public void OpenAttachmentActivateChoice(CardUI cardUI)
        {
            if (cardUI == null || cardUI.Instance == null) return;
            var inst = cardUI.Instance;
            if (!ActivateEligibility.CanActivate(inst, _turnManager, _playerManager, _cardManager))
            {
                // 【2026-09 修复·用户实测"卡有高亮，双击完全没有 LOG"】原先此处**静默 return**：
                // 高亮是上一次刷新算的，点击那一刻门禁可能已不满足（原子锁/优先权/费用/次数/代价/阶段）——
                // 表现为"高亮着却怎么点都没反应、也不打日志"。现打可读原因日志，并**立即刷新高亮**
                // 让 UI 与规则自洽（该卡会当即取消高亮，玩家一眼看出"此刻不能发动"）。
                string why = ActivateEligibility.DiagnoseActivateBlock(inst, _turnManager, _playerManager, _cardManager);
                GD.PrintErr($"[Arena] ⛔ 双击激活被拒：{inst.PlayerTemplate?.CardName} → {why}");
                RefreshActivateHighlights();
                return;
            }

            var abilities = new List<EffectData>();
            foreach (var e in ActivateEligibility.GetAllActivateAbilities(inst))
            {
                if (e != null && e.Timing is EffectTiming.AttachmentActivated or EffectTiming.CharacterActivated)
                    abilities.Add(e);
            }
            if (abilities.Count == 0)
            {
                GD.PrintErr($"[Arena] ⛔ 双击激活被拒：{inst.PlayerTemplate?.CardName} 无 CharacterActivated/AttachmentActivated 能力");
                return;
            }
            // 【2026-10 用户口径·JC093】次数上限**全部用尽**（如"每回合至多发动两次"已用两次）⇒
            // 双击**不开面板、无反馈**（用户："使用两次效果后双击不需要反馈"）；部分用尽时仍开面板，
            // 把用尽项**置灰**展示（对齐 JC053"一项可选、一项置灰"口径）。
            if (ActivateEligibility.AllAbilitiesUsesExhausted(inst, abilities))
            {
                GD.Print($"[Arena] ⏹ 双击忽略：{inst.PlayerTemplate?.CardName} 的激活能力本回合次数已全部用尽");
                return;
            }

            // 单选项：直接发动，不走弹窗（与原本拖拽激活一致，避免多余点击）。
            // 若该效果"以横置本角色为代价"且本卡已横置（无法执行横置代价），或需要目标但无合法目标，则不发动。
            if (abilities.Count == 1)
            {
                var only = abilities[0];
                bool needsTapOnly = !string.IsNullOrEmpty(only.ActivateCondition)
                                    && only.ActivateCondition.Contains("横置本角色");
                bool noTargetOnly = only.Target is { NeedsSelection: true }
                                    && !ActivateEligibility.HasLegalTarget(inst, only, _cardManager);
                if (noTargetOnly || (needsTapOnly && inst.IsTapped))
                {
                    GD.PrintErr($"[Arena] ⛔ 双击激活被拒：{inst.PlayerTemplate?.CardName} → " +
                                (noTargetOnly ? "该能力需要目标但当前无合法目标" : "该能力需横置自身但已横置"));
                    return;
                }
                if (only.Target is { NeedsSelection: true })
                {
                    BeginActivateWithTarget(inst, only);
                }
                else
                {
                    BeginActivate(inst, only, null, -1);
                    RefreshActivateHighlights();
                }
                return;
            }

            // 多选项：弹泛用选择窗
            var win = _activationChoiceWindowScene?.Instantiate<ActivationChoiceWindow>();
            if (win == null) return;
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            for (int i = 0; i < abilities.Count; i++)
            {
                var ability = abilities[i];
                int idx = i;
                string label = $"{i + 1}. {GetActivationAbilityLabel(inst, ability)}";
                // 需要指定目标的效果：预判断场上是否存在合法目标；无目标则置灰禁用，避免无目标也能发动。
                // "以横置本角色为代价"的效果：若本卡已横置（无法执行横置代价）也置灰（如 JC020 效果A）。
                bool needsTap = !string.IsNullOrEmpty(ability.ActivateCondition)
                                && ability.ActivateCondition.Contains("横置本角色");
                // 【2026-10·JC093】次数上限用尽的能力在面板里**置灰**（与候选/权威门禁同源）。
                bool exhausted = ActivateEligibility.IsAbilityUsesExhausted(inst, ability);
                bool disabled = (ability.Target is { NeedsSelection: true }
                                 && !ActivateEligibility.HasLegalTarget(inst, ability, _cardManager))
                                || (needsTap && inst.IsTapped)
                                || exhausted;
                if (disabled) label += exhausted ? "（本回合次数已用尽）"
                    : (needsTap && inst.IsTapped ? "（已横置，无法横置自身）" : "（无合法目标）");
                items.Add(new ActivationChoiceItem
                {
                    Label = label,
                    Disabled = disabled,
                    OnChosen = () =>
                    {
                        if (items[idx].Disabled) return;
                        var ab = abilities[idx];
                        if (ab.Target is { NeedsSelection: true })
                            BeginActivateWithTarget(inst, ab);
                        else
                        {
                            BeginActivate(inst, ab, null, -1);
                            RefreshActivateHighlights();
                        }
                    }
                });
            }
            win.Init(items);
            AddChild(win);
            win.PopupCentered();
        }

        /// <summary>
        /// 【K22】"**牺牲一个角色**"作代价：先弹选择窗让**发动者自己选**被牺牲的角色，选完再发动
        /// （把选定 UID 一并交给 Controller 复检/支付代价）。候选＝`ActivateEligibility.GetSacrificeCandidates`
        /// （与权威复检同源：本方争夺区、非暗藏者除非卡文允许、子类别/本地区/排除自身等筛选）。
        /// </summary>
        private void BeginActivateWithSacrifice(CardInstance inst, EffectData ability, CardInstance target, int targetPlayerId, string areaUid = null)
        {
            var candidates = Script.Manager.ActivateEligibility.GetSacrificeCandidates(inst, ability);
            if (candidates.Count == 0)
            {
                GD.PrintErr($"[Arena] 牺牲代价无候选：{ability.EffectId}");
                return;
            }
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            foreach (var c in candidates)
            {
                var picked = c;
                items.Add(new ActivationChoiceItem
                {
                    Label = $"牺牲 {picked.PlayerTemplate?.CardName}",
                    OnChosen = () =>
                    {
                        _actionService.ActivateAbility(inst, ability, target, targetPlayerId, picked.UID, areaUid);
                        RefreshActivateHighlights();
                    }
                });
            }
            items.Add(new ActivationChoiceItem { Label = "取消（不发动）", OnChosen = () => { } });
            OpenChoiceWindow("选择要牺牲的角色", items, force: true);
        }

        /// <summary>
        /// 【P3·K22】发动激活能力的**统一入口**（View 侧）：带"牺牲一个角色"代价的效果先走选择窗
        /// （`BeginActivateWithSacrifice`，由**发动者自己选**）；其余直接透传 Controller。
        /// 所有激活分派路径（双击/选框/拖拽落点）都经此处，避免漏接线导致"有代价的能力按兜底候选发动"。
        /// </summary>
        private void BeginActivate(CardInstance inst, EffectData ability, CardInstance target, int targetPlayerId)
        {
            if (inst == null || ability == null) return;
            // 【K15】需要"目标地区"的激活能力：先（若还没选卡目标就先选卡目标）→ 再弹**地区选择窗** →
            // 最后把 (卡目标, 目标地区) 一并交给 Controller。用户口径与既有"入堆叠时锁定目标"一致。
            if (ability.NeedsAreaTarget)
            {
                BeginActivateAreaSelection(inst, ability, target, targetPlayerId);
                return;
            }
            if (ability.SacrificeCost != null)
            {
                BeginActivateWithSacrifice(inst, ability, target, targetPlayerId);
                return;
            }
            // 【K5】标志代价（"自本方卡牌上移除…标志"）：X 由玩家选择 → 先收集代价，再发动。
            //  · JC055② `AmountByPlayerChoice`：**逐卡分配**（点一张卡记录用 1 点 → 确认；A30，总和 ≥ 1）；
            //  · JC044② 固定数量：从候选里**选一张卡**（原本无 UI、引擎直接兜底取第一张 → 现补齐）。
            if (ability.CounterCost != null)
            {
                if (ability.CounterCost.AmountByPlayerChoice)
                    BeginActivateWithCounterCostAllocation(inst, ability, target, targetPlayerId);
                else
                    BeginActivateWithCounterCostChoice(inst, ability, target, targetPlayerId);
                return;
            }
            _actionService.ActivateAbility(inst, ability, target, targetPlayerId);
            RefreshActivateHighlights();
        }

        /// <summary>【K5·JC044②】固定数量的标志代价：从候选里选一张卡（该卡上标志数 ≥ 数量）。</summary>
        private void BeginActivateWithCounterCostChoice(CardInstance inst, EffectData ability,
            CardInstance target, int targetPlayerId)
        {
            var candidates = _actionService.GetCounterCostChoiceCandidates(inst, ability);
            if (candidates.Count == 0) { GD.PrintErr($"[Arena] 标志代价无候选：{ability.EffectId}"); return; }
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            foreach (var c in candidates)
            {
                var picked = c;
                items.Add(new ActivationChoiceItem
                {
                    Label = $"从 {picked.PlayerTemplate?.CardName} 上移除 {ability.CounterCost.Amount} 个标志",
                    OnChosen = () =>
                    {
                        _actionService.ActivateAbility(inst, ability, target, targetPlayerId, null, null, picked.UID);
                        RefreshActivateHighlights();
                    },
                });
            }
            items.Add(new ActivationChoiceItem { Label = "取消（不发动）", OnChosen = () => { } });
            OpenChoiceWindow("选择要移除标志的卡", items, force: true);
        }

        /// <summary>
        /// 【K5·JC055②】"移除 **X** 个标志"代价的**逐卡分配**窗（用户口径 A30：点一次某张卡即记录用 1 点，
        /// 确认后移除对应标志；**X ≥ 1**）。实现＝复用通用选择窗做"逐卡增点 + 重置 + 确认/取消"循环。
        /// </summary>
        private void BeginActivateWithCounterCostAllocation(CardInstance inst, EffectData ability,
            CardInstance target, int targetPlayerId)
        {
            var candidates = _actionService.GetCounterCostChoiceCandidates(inst, ability);
            if (candidates.Count == 0) { GD.PrintErr($"[Arena] 标志代价无候选：{ability.EffectId}"); return; }
            var allocation = new System.Collections.Generic.Dictionary<string, int>();
            ShowCounterAllocationStep(inst, ability, target, targetPlayerId, candidates, allocation);
        }

        private void ShowCounterAllocationStep(CardInstance inst, EffectData ability, CardInstance target,
            int targetPlayerId, System.Collections.Generic.List<CardInstance> candidates,
            System.Collections.Generic.Dictionary<string, int> allocation)
        {
            int total = 0;
            foreach (var kv in allocation) total += kv.Value;

            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            foreach (var c in candidates)
            {
                var card = c;
                if (card == null) continue;
                int have = _actionService.GetMarkerCountOnCard(card, ability);
                allocation.TryGetValue(card.UID, out int used);
                items.Add(new ActivationChoiceItem
                {
                    Label = $"{card.PlayerTemplate?.CardName}：移除 1 个（已选 {used} / 共 {have}）",
                    OnChosen = () =>
                    {
                        if (used < have) allocation[card.UID] = used + 1;
                        ShowCounterAllocationStep(inst, ability, target, targetPlayerId, candidates, allocation);
                    },
                });
            }
            if (total >= 1)
            {
                items.Add(new ActivationChoiceItem
                {
                    Label = $"确认（共移除 {total} 个 → 本回合下一张灾难牌费用 -{total}）",
                    OnChosen = () =>
                    {
                        _actionService.ActivateAbility(inst, ability, target, targetPlayerId, null, null, allocation);
                        RefreshActivateHighlights();
                    },
                });
            }
            else
            {
                items.Add(new ActivationChoiceItem { Label = "（请至少选择 1 个标志才能确认）", OnChosen = () => { } });
            }
            if (total > 0)
                items.Add(new ActivationChoiceItem
                {
                    Label = "重置选择",
                    OnChosen = () =>
                    {
                        allocation.Clear();
                        ShowCounterAllocationStep(inst, ability, target, targetPlayerId, candidates, allocation);
                    },
                });
            items.Add(new ActivationChoiceItem { Label = "取消（不发动）", OnChosen = () => { } });
            OpenChoiceWindow($"移除时间标志（已选 {total}，至少 1）", items, force: true);
        }

        /// <summary>【K15】为需要"目标地区"的激活能力弹地区选择窗（候选＝`ActivateEligibility.GetAreaTargetCandidates`，
        /// 已按 `AreaTargetExcludesCardArea` 排除卡目标当前所在地＝JC117"不能原地移动"）。</summary>
        private void BeginActivateAreaSelection(CardInstance inst, EffectData ability, CardInstance cardTarget, int targetPlayerId)
        {
            var areas = Script.Manager.ActivateEligibility.GetAreaTargetCandidates(inst, ability, cardTarget);
            if (areas.Count == 0)
            {
                GD.PrintErr($"[Arena] 无合法目标地区：{ability.EffectId}");
                return;
            }
            _triggerCoordinator.RequestActivateTargetSelection(
                inst.PlayerTemplate?.CardName ?? "激活效果",
                true,                                   // isArea：走地区槽位选择
                areas,
                uid =>
                {
                    if (string.IsNullOrEmpty(uid)) return;
                    if (ability.SacrificeCost != null)
                        BeginActivateWithSacrifice(inst, ability, cardTarget, targetPlayerId, uid);   // 代价选择仍在 Controller 侧完成（目标地区一并带走）
                    else
                        _actionService.ActivateAbility(inst, ability, cardTarget, targetPlayerId, null, uid);
                    RefreshActivateHighlights();
                });
        }

        /// <summary>生成单条激活效果在选框中的简短描述（优先 FaqNote，回退 EffectId）。</summary>
        private static string GetActivationAbilityLabel(CardInstance inst, EffectData ability)
        {
            if (!string.IsNullOrEmpty(ability.FaqNote)) return ability.FaqNote;
            if (!string.IsNullOrEmpty(ability.EffectId)) return ability.EffectId;
            return "激活效果";
        }

        /// <summary>
        /// 【K24】激活效果"取目标"的统一分派：**玩家目标**走独立选择窗口（`PromptPlayerTarget`），
        /// 其余（角色/附属/地区/非争夺区牌堆）沿用既有选目标窗。
        /// </summary>
        private void BeginActivateWithTarget(CardInstance inst, EffectData ability)
        {
            if (Script.Manager.PlayerTargetPool.IsPlayerSpec(ability.Target))
                BeginActivateWithPlayerTarget(inst, ability);
            else
                BeginAttachmentActivateWithTarget(inst, ability);
        }

        /// <summary>
        /// 【K24·玩家目标】独立选择窗口：把候选玩家列成按钮后回传玩家号（取消 = -1）。
        /// 玩家不是场上的卡节点（无法点选/拖拽表达），故必须有独立窗口（用户 2026-09 口径）；
        /// View 只收集选择，合法性复检/付费/入堆叠由 Controller（`ActivateAbility` / 触发入栈）执行（View 不写 Model）。
        /// 同时是 `IGameUIHooks.PromptPlayerTarget` 的实现（触发路径在入堆叠前经此取值）。
        /// </summary>
        public void PromptPlayerTarget(int chooserPlayerId, string prompt,
            System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
        {
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            foreach (int pid in candidates)
            {
                int captured = pid;
                items.Add(new ActivationChoiceItem
                {
                    Label = Script.Manager.PlayerTargetPool.Describe(_playerManager, captured),
                    OnChosen = () => onChosen?.Invoke(captured),
                });
            }
            items.Add(new ActivationChoiceItem { Label = "取消（不发动）", OnChosen = () => onChosen?.Invoke(-1) });
            OpenChoiceWindow(string.IsNullOrEmpty(prompt) ? "选择目标玩家" : prompt, items, force: true);
        }

        /// <summary>
        /// 【K10·额外费用】打出前需要**玩家做选择**的额外费用（JC049「额外费用：牺牲一个角色」）：
        /// 弹选择窗（**由打出者自己选**牺牲谁；取消＝不打出、**不扣任何费用**）→ 选完带选择调用 `PlayCard`
        /// （额外费用在**付费阶段、入堆叠之前**付清，**不可被响应**）→ 完成手牌/UI 整理。
        /// **无合法牺牲对象 ⇒ 该卡不能打出**（用户口径 A21），直接退回手牌。
        /// </summary>
        internal void BeginPlayWithExtraCostChoice(CardInstance card, CardUI cardUI, int playerId,
            List<CardUI> hand, string targetAreaName, string targetCardUid)
        {
            if (card == null) return;
            var candidates = _actionService.GetExtraCostSacrificeCandidates(card);
            if (candidates.Count == 0)
            {
                GD.Print($"❌ [Arena] 【K10 额外费用】{card.PlayerTemplate?.CardName} 无合法牺牲对象 → 不能打出，退回手牌");
                _actionService.ReturnCardToHand(playerId, card.UID);
                ArrangeAllCardsInternal();
                return;
            }
            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            foreach (var c in candidates)
            {
                var picked = c;
                items.Add(new ActivationChoiceItem
                {
                    Label = $"牺牲 {picked.PlayerTemplate?.CardName}（额外费用）",
                    OnChosen = () => FinishPlayWithExtraCost(card, cardUI, playerId, hand,
                        targetAreaName, targetCardUid, picked.UID),
                });
            }
            items.Add(new ActivationChoiceItem { Label = "取消（不打出）", OnChosen = () => { } });
            OpenChoiceWindow("额外费用：选择要牺牲的角色", items, force: true);
        }

        /// <summary>
        /// 【K10·额外费用·JC081】"**展示任意数量手牌**减费"的打出前交互（用户口径 A22/A23）：
        /// 手牌**多选**（点一次选入/再点取消，窗内逐张列出当前选择）→ **确认**＝给对手展示所有已选牌 →
        /// 按合格张数减免 → 入堆叠；**取消**＝不打出、卡不入堆叠、**不扣任何费用**。
        /// 交互实现说明：复用既有 `ActivationChoiceWindow` 做"逐张增删 + 确认/取消"的循环（不新造窗口）。
        /// </summary>
        internal void BeginPlayWithRevealExtraCostChoice(CardInstance card, CardUI cardUI, int playerId,
            List<CardUI> hand, string targetAreaName, string targetCardUid)
        {
            if (card == null) return;
            var candidates = _actionService.GetExtraCostRevealCandidates(playerId, card);
            var selected = new System.Collections.Generic.List<CardInstance>();
            ShowRevealChoiceStep(card, cardUI, playerId, hand, targetAreaName, targetCardUid, candidates, selected);
        }

        /// <summary>【K10·JC081】展示手牌选择的一步（可反复调用：每次点击后重开窗口刷新选择态）。
        /// 【统一实现】完成回调参数化：**打出**（`FinishPlayWithRevealExtraCost`）与**暗藏者现身**
        /// （`RevealCovertWithExtraCost`）共用同一套选择 UI（用户口径：现身同样要校验额外费用）。</summary>
        private void ShowRevealChoiceStep(CardInstance card, CardUI cardUI, int playerId, List<CardUI> hand,
            string targetAreaName, string targetCardUid,
            System.Collections.Generic.List<CardInstance> candidates,
            System.Collections.Generic.List<CardInstance> selected)
            => ShowRevealChoiceStepGeneric(card, candidates, selected,
                sel => FinishPlayWithRevealExtraCost(card, cardUI, playerId, hand, targetAreaName, targetCardUid, sel),
                cancelLabel: "取消（不打出）");

        /// <summary>【K10·统一实现】展示手牌多选窗（逐张增删 + 确认/取消）：`onConfirm(selected)` 由调用方落地。</summary>
        private void ShowRevealChoiceStepGeneric(CardInstance card,
            System.Collections.Generic.List<CardInstance> candidates,
            System.Collections.Generic.List<CardInstance> selected,
            System.Action<System.Collections.Generic.List<CardInstance>> onConfirm, string cancelLabel)
        {
            var tmpl = card.PlayerTemplate;
            int qualified = 0;
            foreach (var s in selected)
                if (_actionService.IsQualifiedRevealCard(tmpl, s)) qualified++;

            var items = new System.Collections.Generic.List<ActivationChoiceItem>();
            foreach (var c in candidates)
            {
                var captured = c;
                if (captured == null) continue;
                bool on = selected.Contains(captured);
                string name = captured.PlayerTemplate?.CardName ?? captured.UID;
                string mark = _actionService.IsQualifiedRevealCard(tmpl, captured) ? "（合格 -1）" : "（不合格）";
                items.Add(new ActivationChoiceItem
                {
                    Label = (on ? "✔ 已选：展示 " : "展示 ") + name + mark,
                    OnChosen = () =>
                    {
                        if (on) selected.Remove(captured); else selected.Add(captured);
                        ShowRevealChoiceStepGeneric(card, candidates, selected, onConfirm, cancelLabel);
                    },
                });
            }
            items.Add(new ActivationChoiceItem
            {
                Label = $"确认（展示 {selected.Count} 张，其中合格 {qualified} 张 → 费用 -{qualified}）",
                OnChosen = () => onConfirm?.Invoke(selected),
            });
            items.Add(new ActivationChoiceItem { Label = cancelLabel, OnChosen = () => { } });
            OpenChoiceWindow($"额外费用：选择要展示给对手的手牌（当前减免 {qualified}）", items, force: true);
        }

        /// <summary>
        /// 【K10·现身】单机：暗藏者现身前的额外费用选择（与打出 **1:1** 同款窗口；用户口径：现身同样要校验额外费用）：
        ///  · `RevealHandCards`（JC081，可选）→ 手牌多选窗（确认＝展示并减免；取消＝不现身）；
        ///  · `SacrificeCharacter`（必须支付）→ 牺牲候选选择窗；
        ///  · `PayTargetCost` / 无候选 → **不现身**（fail-closed；权威侧同样会拒绝）；
        ///  · 无额外费用 → 直接现身。
        /// </summary>
        private void RevealCovertWithExtraCost(int playerId, CardInstance inst)
        {
            if (inst == null) return;
            var tmpl = inst.PlayerTemplate;
            var spec = tmpl?.ExtraCost;
            if (spec == null || spec.Kind == ExtraCostKind.None)
            {
                _actionService.RevealCovert(playerId, inst.UID);
                return;
            }
            System.Action<System.Collections.Generic.List<CardInstance>> revealFinish = sel =>
            {
                var uids = new System.Collections.Generic.List<string>();
                foreach (var s in sel) if (s != null) uids.Add(s.UID);
                _actionService.RevealCovert(playerId, inst.UID, null, uids);
                RefreshActivateHighlights();
            };

            switch (spec.Kind)
            {
                case ExtraCostKind.RevealHandCards:
                {
                    var candidates = _actionService.GetExtraCostRevealCandidates(playerId, inst);
                    if (candidates == null || candidates.Count == 0)
                    {
                        // 无可展示手牌：该形态为可选 ⇒ 直接全额现身（mandatory 时权威侧会拒绝）
                        _actionService.RevealCovert(playerId, inst.UID);
                        return;
                    }
                    ShowRevealChoiceStepGeneric(inst, candidates,
                        new System.Collections.Generic.List<CardInstance>(), revealFinish, cancelLabel: "取消（不现身）");
                    return;
                }
                case ExtraCostKind.SacrificeCharacter:
                {
                    var sacSpec = spec.Sacrifice ?? new SacrificeCostSpec();
                    var cands = EffectEngine.Instance?.GetSacrificeCandidates(inst, sacSpec)
                                ?? new System.Collections.Generic.List<CardInstance>();
                    if (cands.Count == 0)
                    {
                        GD.PrintErr($"[Arena] 【K10 额外费用】{tmpl.CardName} 现身：无合法牺牲对象 → 不能现身");
                        return;
                    }
                    var items = new System.Collections.Generic.List<ActivationChoiceItem>();
                    foreach (var c in cands)
                    {
                        var picked = c;
                        items.Add(new ActivationChoiceItem
                        {
                            Label = $"牺牲 {picked.PlayerTemplate?.CardName}（现身额外费用）",
                            OnChosen = () =>
                            {
                                _actionService.RevealCovert(playerId, inst.UID, picked.UID);
                                RefreshActivateHighlights();
                            },
                        });
                    }
                    items.Add(new ActivationChoiceItem { Label = "取消（不现身）", OnChosen = () => { } });
                    OpenChoiceWindow("额外费用：选择要牺牲的角色（现身）", items, force: true);
                    return;
                }
                default:
                    GD.PrintErr($"[Arena] 【K10 额外费用】{tmpl.CardName} 的额外费用（{spec.Kind}）在现身路径无法满足 → 不能现身");
                    return;
            }
        }

        /// <summary>【K10·JC081】带"已选展示手牌"完成打出 + 手牌/UI 整理。</summary>
        private void FinishPlayWithRevealExtraCost(CardInstance card, CardUI cardUI, int playerId, List<CardUI> hand,
            string targetAreaName, string targetCardUid, System.Collections.Generic.List<CardInstance> selected)
        {
            var uids = new System.Collections.Generic.List<string>();
            foreach (var s in selected)
                if (s != null) uids.Add(s.UID);

            bool success = _actionService.PlayCard(playerId, card.UID, targetAreaName, targetCardUid, null, uids);
            if (!success)
            {
                _actionService.ReturnCardToHand(playerId, card.UID);
                ArrangeAllCardsInternal();
                return;
            }
            hand.Remove(cardUI);
            if (_areaCardUIs.Contains(cardUI)) _areaCardUIs.Remove(cardUI);
            cardUI.CanDrag = false;
            ArrangeAllCardsInternal();
            UpdateTurnUIInternal();
        }

        /// <summary>【K10】带"已选定的额外费用牺牲者"完成打出 + 手牌/UI 整理（与拖拽路径的收尾一致）。</summary>
        private void FinishPlayWithExtraCost(CardInstance card, CardUI cardUI, int playerId, List<CardUI> hand,
            string targetAreaName, string targetCardUid, string sacrificeUid)
        {
            bool success = _actionService.PlayCard(playerId, card.UID, targetAreaName, targetCardUid, sacrificeUid);
            if (!success)
            {
                _actionService.ReturnCardToHand(playerId, card.UID);
                ArrangeAllCardsInternal();
                return;
            }
            hand.Remove(cardUI);
            if (_areaCardUIs.Contains(cardUI)) _areaCardUIs.Remove(cardUI);
            cardUI.CanDrag = false;   // 进入堆叠后不可拖拽
            ArrangeAllCardsInternal();
            UpdateTurnUIInternal();
        }

        /// <summary>为"以玩家为目标"的激活效果弹独立选择窗（如 JC111 混沌蝴蝶，来源多为角色卡）。</summary>
        private void BeginActivateWithPlayerTarget(CardInstance inst, EffectData ability)
        {
            var candidates = Script.Manager.PlayerTargetPool.Collect(ability.Target, inst.ControllerId, _playerManager);
            if (candidates.Count == 0)
            {
                GD.PrintErr($"[Arena] 目标玩家无候选：{ability.EffectId}");
                return;
            }
            string name = inst.PlayerTemplate?.CardName ?? "激活效果";
            PromptPlayerTarget(inst.ControllerId, $"{name}：选择目标玩家", candidates, pid =>
            {
                if (pid <= 0) return;
                BeginActivate(inst, ability, null, pid);
                RefreshActivateHighlights();
            });
        }

        /// <summary>为附属激活效果（有目标，如 JC012 ②横置角色）弹出选目标窗。选完后发动并牺牲来源。</summary>
        private void BeginAttachmentActivateWithTarget(CardInstance inst, EffectData ability)
        {
            var set = new HashSet<string>();
            // 【2026-09 修复】候选＝争夺区成员（形态角色经 `MoveFormZone` 已迁入争夺区 ⇒ 天然在内），
            // 并按 `Target.CanTargetSelf` 决定是否把**来源卡自身**纳入候选（A62：JC003/JC019"目标角色"）。
            bool canSelf = ability.Target?.CanTargetSelf == true;
            foreach (var t in _cardManager.GetAreaCards())
            {
                if (t == inst && !canSelf) continue;
                if (ActivateEligibility.IsLegalActivateTarget(inst, ability, t, _cardManager))
                    set.Add(t.UID);
            }
            // 仅角色（AreaZone 中的正面角色；暗藏者由 IncludeCovert=false 已在 IsLegalActivateTarget 内排除）
            var validUids = new System.Collections.Generic.List<string>(set);
            _triggerCoordinator.RequestActivateTargetSelection(
                inst.PlayerTemplate?.CardName ?? "激活效果",
                ability.Target is { Kind: TargetKind.Area },
                validUids,
                (uid) =>
                {
                    var target = _cardManager.GetInstance(uid);
                    BeginActivate(inst, ability, target, -1);
                    RefreshActivateHighlights();
                });
        }

    /// <summary>
    /// 事务卡（需选目标）拖出后的弹窗选目标入口（复用 JC012 激活弹窗模式）。
    /// 收集场上合法目标（争夺区角色 + 附属区附属 + 秘社）；完全无合法目标则退回手牌；
    /// 否则弹窗让玩家选目标，选完回调 PlayCard 入栈。
    /// 与 BeginAttachmentActivateWithTarget 并列，均属"交互层收集目标 → 弹窗 → Controller 收口"。
    /// </summary>
    internal void BeginSorceryTargetSelection(CardInstance sorcery, CardUI cardUI, int playerId, List<CardUI> hand)
    {
        if (_cardManager == null || _zoneManager == null || _triggerCoordinator == null) return;

        // 【2026-09 修复·用户实测"多弹出了一个原本流程中固有的选择框"】**一次只允许一个选择流程**：
        // 已有模态选择窗开着（模式窗/多选窗/选目标窗…）⇒ 本次是对同一次拖拽的**重复投递**，直接忽略。
        // （根因见 `CardDragController.OnCardDragEnded` 顶部注释：重复投递曾在"模态冻结"分支把卡塞回手牌，
        //  导致后续重复投递又能走完整流程，于是 JC062 弹 2 个框、JC063 弹 3 个框。）
        if (IsDragInteractionLocked)
        {
            GD.Print($"[Arena] ⛔ 已有选择窗/模态开着 → 忽略对『{sorcery.PlayerTemplate?.CardName}』的重复取目标请求（防重复弹窗）");
            return;
        }

        // 弹窗前预校验费用（与 TurnManager.PlayCard 一致，含时效费用修正），不足则直接退回手牌、不弹选目标窗。
        // 【P1】传实例：与 TurnManager.PlayCard 同一重载，保证"子类别:/域:/有域图标"类过滤串两端一致。
        int cost = sorcery.PlayerTemplate.Cost
            - Script.Manager.EffectEngine.Instance.GetCostModification(
                playerId, sorcery, sorcery.PlayerTemplate.CardName, sorcery.PlayerTemplate.Organization);
        if (cost < 0) cost = 0;
        if (cost > 0 && _playerManager.GetAvailableMana(playerId) < cost)
        {
            GD.Print($"❌ 事务卡 {sorcery.PlayerTemplate?.CardName} 费用不足（需{cost}），不弹选目标窗，退回手牌");
            _actionService.ReturnCardToHand(playerId, sorcery.UID);
            ArrangeAllCardsInternal();
            return;
        }

        // 【K16·模式分支】声明了 `Modes` 的卡（卡文"选择一项"）：先选模式（付费前）→ 取该模式目标 → 入堆叠。
        var modeEffect = Script.Manager.MultiTargetSlots.FindModeEffect(
            sorcery.PlayerTemplate, Script.Data.Effects.EffectTiming.OnSorceryCast);
        if (modeEffect != null)
        {
            GD.Print($"[Arena] 【K16 模式分支】{sorcery.PlayerTemplate?.CardName}：进入选模式 + 取目标流程");
            _actionService.BeginModeCardPlay(playerId, sorcery.UID);
            hand.Remove(cardUI);
            if (_areaCardUIs.Contains(cardUI)) _areaCardUIs.Remove(cardUI);
            cardUI.CanDrag = false;
            ArrangeAllCardsInternal();
            UpdateTurnUIInternal();
            return;
        }

        // 【K15·多目标】声明了 `EffectData.TargetSlots` 的事务卡（如 JC021）：
        // 走"逐槽取目标 → 确认面板 → 确认后才入堆叠"的专用流程（Controller 收口 `BeginMultiTargetPlay`）。
        var multiEffect = Script.Manager.MultiTargetSlots.FindMultiTargetEffect(
            sorcery.PlayerTemplate, Script.Data.Effects.EffectTiming.OnSorceryCast);
        if (multiEffect != null)
        {
            GD.Print($"[Arena] 【K15 多目标】{sorcery.PlayerTemplate?.CardName}：进入逐槽取目标 + 确认面板流程");
            _actionService.BeginMultiTargetPlay(playerId, sorcery.UID);
            // 成功与否由 Controller 经确认面板回调决定；此处只负责刷新手牌/堆叠显示
            hand.Remove(cardUI);
            if (_areaCardUIs.Contains(cardUI)) _areaCardUIs.Remove(cardUI);
            cardUI.CanDrag = false;
            ArrangeAllCardsInternal();
            UpdateTurnUIInternal();
            return;
        }

        var validUids = new System.Collections.Generic.List<string>();
        // 候选：争夺区角色 + 附属区附属 + 秘社 + 资产区 + 墓地（覆盖事务卡可能的目标种类；与 LegalityProjection 同源）
        var candidates = new List<CardInstance>(_zoneManager.GetZoneCards(CardZone.AreaZone, 0));
        candidates.AddRange(_zoneManager.GetZoneCards(CardZone.AttachedZone, 0));
        candidates.AddRange(_zoneManager.GetZoneCards(CardZone.CouncilZone, 0));
        candidates.AddRange(_zoneManager.GetZoneCards(CardZone.AssetZone, 1));
        candidates.AddRange(_zoneManager.GetZoneCards(CardZone.AssetZone, 2));
        candidates.AddRange(_zoneManager.GetZoneCards(CardZone.Graveyard, 1));
        candidates.AddRange(_zoneManager.GetZoneCards(CardZone.Graveyard, 2));

        foreach (var t in candidates)
        {
            if (t == null || t == sorcery) continue;
            if (_cardManager.IsLegalPlayTarget(sorcery, t))
                validUids.Add(t.UID);
        }

        // 场上完全无合法目标 → 拒绝打出，退回手牌
        if (validUids.Count == 0)
        {
            GD.Print($"❌ 事务卡 {sorcery.PlayerTemplate?.CardName} 无合法目标，退回手牌");
            _actionService.ReturnCardToHand(playerId, sorcery.UID);
            ArrangeAllCardsInternal();
            return;
        }

        // 弹窗选目标；选完回调入栈（PlayCard 一次性完成费用支付 + 目标校验 + 入栈）
        _triggerCoordinator.RequestActivateTargetSelection(
            sorcery.PlayerTemplate?.CardName ?? "事务",
            isArea: false,
            validUids,
            (uid) =>
            {
                bool success = _actionService.PlayCard(playerId, sorcery.UID, null, uid);
                if (!success)
                {
                    _actionService.ReturnCardToHand(playerId, sorcery.UID);
                    ArrangeAllCardsInternal();
                    return;
                }
                hand.Remove(cardUI);
                if (_areaCardUIs.Contains(cardUI)) _areaCardUIs.Remove(cardUI);
                cardUI.CanDrag = false;   // 进入堆叠后不可拖拽
                ArrangeAllCardsInternal();
                UpdateTurnUIInternal();
            });
    }

    /// <summary>将地区 UID 转为 1-based 地区序号（供 CombatManager 调用窗口使用）。</summary>
    public int GetAreaNo(string areaUid)
    {
        for (int i = 0; i < _areaSlots.Count; i++)
            if (_areaSlots[i].AreaUID == areaUid) return i + 1;
        return -1;
    }

    /// <summary>命中测试：取出点击位置下的合法战斗承伤目标。
    /// 【MVC 修复 M-04】原先在此内联复现 ControllerId/CurrentZone/ZoneDetailId/IsCovert 四项判据，
    /// 与 CombatManager.IsLegalStrengthTarget 重复且漏掉了 HasDefense（两份规则已实际漂移）；
    /// 现统一委托 Controller 判定，View 只负责几何命中测试。</summary>
    private CardUI PickStrengthCardAt(Vector2 globalPos)
    {
        // 【MVC 修复 M-08】当前战斗窗口改由 ModalWindowCoordinator 持有，此处经协调器读取。
        var strengthWindow = _modalCoordinator?.ActiveStrengthWindow;
        if (strengthWindow == null) return null;
        var combatManager = _initializer?.CombatManager;
        if (combatManager == null) return null;
        int loser = strengthWindow.WinnerPlayer == 1 ? 2 : 1;
        string areaUid = strengthWindow.AreaUid;
        foreach (var card in _areaCardUIs)
        {
            // 合法性判定上收 Controller（单一真相源），此处仅做 UI 层几何命中
            if (!combatManager.IsLegalStrengthTarget(card.Instance, loser, areaUid)) continue;
            if (card.GetVisualGlobalRect().HasPoint(globalPos)) return card;
        }
        return null;
    }

    // ============================================================
    // 测试/PM命令
    // ============================================================
    /// <summary>测试按钮：对指定地区全体造成伤害/创伤。
    /// 【MVC 修复 M-05】原先在此取出 Model 集合并逐个循环发伤害，属 View 遍历 Model 执行领域操作；
    /// 现改为表达单一意图 DealAreaDamage，遍历与结算由 Controller 负责，View 只负责刷新显示。</summary>
    public void OnTestDamageButton(int areaIdx, int amount, bool isTrauma)
    {
        if (areaIdx < 0 || areaIdx >= _areaSlots.Count) return;
        _actionService.DealAreaDamage(_areaSlots[areaIdx].AreaUID, amount, isTrauma);
        RefreshDamageDisplay();
    }

    /// <summary>刷新全场卡面的伤害/创伤数字。
    /// 【2026-10】改为 `internal`：PM 命令的 `onZoneChanged` 回调需要它 —— 伤害/治疗类命令只改模型，
    /// 若不刷新伤害数字，玩家在界面上看到的就是"命令没用"（用户实测）。</summary>
    internal void RefreshDamageDisplay()
    {
        foreach (var cardUI in _areaCardUIs)
            cardUI.RefreshDamageDisplay();
    }



    /// <summary>
    /// 多行命令框输入拦截：
    ///   Enter        → 换行（TextEdit 默认行为，不拦截）
    ///   Shift+Enter  → 执行全部命令
    ///   ` / ~        → 关闭命令框并**吞掉该次按键**（命令框不接受 `~` 字符输入）
    /// </summary>
    private void OnCommandInputGuiInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

        // 反引号/波浪线键：不插入字符，直接关闭命令框（与 _Input 的开关行为一致）
        if (IsCommandToggleKey(key))
        {
            ToggleCommandInput();
            _commandInput?.AcceptEvent();
            return;
        }

        if (key.Keycode != Key.Enter && key.Keycode != Key.KpEnter) return;
        if (!key.ShiftPressed) return;   // 无 Shift 时交给 TextEdit 换行

        OnCommandSubmitted();
        _commandInput?.AcceptEvent();
    }

    /// <summary>
    /// 命令框开关键判定：物理键位为反引号/波浪线键（`Keycode == 96`，即 `` ` `` / `~` 同键），
    /// 或本次按键产生的字符就是 `~`（非美式键盘布局下的兜底）。
    /// </summary>
    private static bool IsCommandToggleKey(InputEventKey key)
        => (int)key.Keycode == 96 || key.Unicode == '~';

    /// <summary>
    /// 切换 PM 命令输入框可见性（`~` / `` ` `` 键；单机与联机行为一致）。
    /// 打开时清空内容、聚焦并置光标到开头；关闭时释放焦点。面板层级由 `CommandInputZIndex` 保证在最上层。
    /// </summary>
    private void ToggleCommandInput()
    {
        if (_commandInput == null) return;
        bool show = !_commandInput.Visible;
        _commandInput.Visible = show;
        _commandInput.ZIndex = CommandInputZIndex;   // 始终高于卡牌/秘社/悬浮预览
        _commandInput.Text = "";
        if (show)
        {
            _commandInput.GrabFocus();
            _commandInput.SetCaretLine(0);
            _commandInput.SetCaretColumn(0);
        }
        else
        {
            _commandInput.ReleaseFocus();
        }
    }

    /// <summary>PM 命令输入框的面板层级：战场内最高层（高于卡牌、附属、箭头与卡面悬浮预览）。
    /// 层号统一由 Script.UI.Layers 分配（Godot z_index 上限 4096，旧值 4100 会被截断）。</summary>
    private const int CommandInputZIndex = Layers.CommandInput;

    private void OnCommandSubmitted()
    {
        if (_commandInput == null) return;

        string text = _commandInput.Text;
        ExecutePMCommandBatch(text);

        // 发送后保持输入框可见，方便连续输入；清空内容并重新聚焦
        _commandInput.Text = "";
        _commandInput.GrabFocus();
    }

    /// <summary>执行单条 PM 命令</summary>
    public void ExecutePMCommand(string cmd)
    {
        _pmCommandManager?.ExecuteCommand(cmd);
    }

    /// <summary>执行多行 PM 命令（从上往下，遇对抗命令后中止）</summary>
    public void ExecutePMCommandBatch(string text)
    {
        _pmCommandManager?.ExecuteCommandBatch(text);
    }

    public override void _Input(InputEvent @event)
    {
        // ---- 手牌拖拽（全局输入驱动，绕开 Godot 命中分发失效问题）----
        // 【MVC 修复 M-08】拖拽起手/放手路由已收归 CardDragController，此处仅转发。
        if (@event is InputEventMouseButton dragDown && dragDown.Pressed && dragDown.ButtonIndex == MouseButton.Left)
        {
            // [DEBUG] 诊断双击暗藏者无反应：记录左键按下，看 DoubleClick 是否被标记、是否被 Try 拦截
            // D9 激活框架：激活拖拽起手优先于出牌拖拽（命中可发动的场上卡才进入激活模式）
            if (_cardDragController.IsActivating || _cardDragController.TryBeginActivateDragAt(dragDown.GlobalPosition))
            {
                AcceptEvent();
                return;
            }
            if (_cardDragController.TryBeginDragAt(dragDown.GlobalPosition))
            {
                AcceptEvent();
                return;
            }
        }
        else if (@event is InputEventMouseButton dragUp && !dragUp.Pressed && dragUp.ButtonIndex == MouseButton.Left)
        {
            if (_cardDragController.IsActivating)
            {
                _cardDragController.EndActivateDrag(dragUp.GlobalPosition);
                AcceptEvent();
                return;
            }
            if (_cardDragController.IsDragging)
            {
                _cardDragController.EndDrag();
                AcceptEvent();
                return;
            }
        }

        // 战斗对抗窗口打开时，路由主战场卡点击分配伤害（左键+1 / 右键-1）
        // 【MVC 修复 M-08】当前战斗窗口改由 ModalWindowCoordinator 持有，此处经协调器读取。
        if (_modalCoordinator != null && _modalCoordinator.ActiveStrengthWindow != null && @event is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right)
            {
                var card = _cardDragController.PickAreaCardAt(mb.GlobalPosition);
                if (card != null)
                {
                    _modalCoordinator.ActiveStrengthWindow.TryAllocate(card, mb.ButtonIndex == MouseButton.Right ? 2 : 1);
                    AcceptEvent();
                    return;
                }
            }
        }

        // 触发效果选目标窗口激活时，路由场上卡点击（最新需求：选目标提示窗口）
        // 【MVC 修复 M-08】选目标状态与命中逻辑已迁移至 TriggerInteractionCoordinator，此处转发。
        // 【2026-09 用户口径 JC062/JC021】多选窗："**左键选择、右键取消**" ⇒ 右键也要转发（`rightClick:true`）。
        if (_triggerCoordinator != null && _triggerCoordinator.IsTargetSelectionActive
            && @event is InputEventMouseButton tsMb && tsMb.Pressed
            && (tsMb.ButtonIndex == MouseButton.Left || tsMb.ButtonIndex == MouseButton.Right))
        {
            _triggerCoordinator.HandleTargetSelectionClick(tsMb.GlobalPosition,
                rightClick: tsMb.ButtonIndex == MouseButton.Right);
            AcceptEvent();
            return;
        }

        // `~` / `` ` `` 键切换 PM 命令输入框可见性（同一个键：按一下打开、再按一下关闭；
        // 打开时该键被吞掉，因此命令框里不会出现 `~` 字符）
        if (@event is InputEventKey key && key.Pressed && !key.Echo && IsCommandToggleKey(key))
        {
            ToggleCommandInput();
            AcceptEvent();
        }
    }

    /// <summary>
    /// 手牌命中 / 可拖拽判定 / 落点目标判定 / 拖拽高亮 已全部迁移至 CardDragController（MVC 修复 M-08）。
    /// Arena 经 _cardDragController（PickAreaCardAt / UpdateDragTargetHighlights / IsDraggingUnderNoneMask）访问。
    /// </summary>
}
