using System;
using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Script.UI
{
    /// <summary>
    /// 【联机战场节点·快照驱动】单机用 Arena（事件驱动），跨进程联机用 ArenaOl（快照驱动）。
    ///
    /// 布局完全复用 res://Scenes/Net/ArenaOl.tscn（从 Arena.tscn 复制，区域容器/按钮位置与单机一致），
    /// 仅驱动方式分离：
    ///  - 纯快照驱动：收到快照 → 从客户端投影（launcher.ClientZoneManager 等）取卡实例 →
    ///    重建卡 UI → 用 CardLayoutHelper 排版（与单机视觉一致）。
    ///  - 操作输入：让过/回合结束按钮 → launcher.SendIntentAs 上行意图，服务端权威结算。
    ///
    /// 与单机 Arena 完全隔离，不触碰其事件驱动逻辑。
    /// </summary>
    [GlobalClass]
    public partial class ArenaOl : Control
    {
        private IMatchClientView _matchClient;   // 客户端对局视图（单场启动器 or 大厅会话，两者同接口）
        private int _localPlayerId = 1;

        private Control _p1Hand;
        private Control _p2Hand;
        private Control _areasContainer;

        // 专用区域组件（与单机 Arena 一致）：牌库/墓地/世界牌库用各自的组件渲染，
        // 手牌用 CardUI 渲染。联机快照驱动同样复用这些组件，确保视觉与单机一致。
        private OnlineDeckZone _p1DeckZone;
        private OnlineDeckZone _p2DeckZone;
        private GraveyardZone _p1GraveZone;
        private GraveyardZone _p2GraveZone;
        private WorldDeckZone _worldDeckZone;

        private Label _phaseLabel;
        private Label _p1ScoreLabel;
        private Label _p2ScoreLabel;

        private PackedScene _cardUIScene;
        private PackedScene _areaCardUIScene;
        private PackedScene _areaSlotScene;
        private PackedScene _councilUIScene;   // 秘社卡 UI（CouncilCardUI.tscn）
        private PackedScene _stackEntryScene;  // 堆叠条目 UI（StackEntryUI.tscn）
        private List<AreaSlot> _areaSlots = new List<AreaSlot>();
        private readonly Dictionary<string, CardUI> _cardUIMap = new Dictionary<string, CardUI>();
        private readonly Dictionary<string, CouncilCardUI> _councilCardUIMap = new Dictionary<string, CouncilCardUI>();
        private Control _assetContainer1, _assetContainer2;   // 资产区（P1/P2）
        private Label _assetLabel1, _assetLabel2;             // 费用池标签（费用/颜色忠诚/领域忠诚，对标单机）
        private Control _scoreContainer1, _scoreContainer2;   // 分数区（P1/P2）
        private Control _councilContainer1, _councilContainer2; // 秘社区（P1/P2）
        private Control _stackContainer;      // 堆叠区容器
        private Label _resultLabel;           // 游戏结束结算文字（由服务端权威结果驱动）
        /// <summary>是否已展示本局结果（避免后续快照刷新把结算文字刷掉）。</summary>
        private bool _matchResultShown;
        private Button _concedeButton;        // 投降按钮
        private ChoicePromptPanel _choicePromptPanel;   // 联机决策面板（收到 ChoiceRequest 时弹出）
        private TextEdit _commandInput;       // PM 调试命令输入（Shift+Enter 上行 DebugCommand）

        // ==================== 联机激活交互（双击效果选择 + 场上高亮选目标） ====================
        // 效果选择/场上选目标进行期间为本地模态：拦截让过/出牌/重复双击等并发输入（服务端无待决请求，纯客户端 UI 阶段）。
        private bool _activationSelectionBusy;
        private OnlineActivationChoiceWindow _onlineChoiceWindow;   // 联机效果选择窗口（动态实例）
        private PackedScene _targetWindowScene;                     // 复用单机 TargetSelectionWindow 场景（只读，不改类）
        private TargetSelectionWindow _onlineTargetWindow;          // 场上点击高亮选目标提示窗（复用单机场景）
        // —— 场上选目标状态 ——
        private bool _activationTargetActive;
        private Script.Data.Effects.EffectData _activationTargetAbility;
        private Script.Game.Entities.CardInstance _activationTargetSource;
        private readonly System.Collections.Generic.HashSet<string> _activationLegalCardUids = new System.Collections.Generic.HashSet<string>();
        /// <summary>「事务卡需目标出牌」路径的待发卡 UID（非空表示当前选目标是为 PlayCard 服务）。</summary>
        private string _pendingPlayCardUid;
        /// <summary>「服务端选目标请求」的待回包请求（非空表示本轮选中的目标要回成 ChoiceResult，而非上行意图）。</summary>
        private Script.Net.ChoiceRequest _pendingChoiceTargetRequest;
        /// <summary>服务端选目标请求中的**地区目标**集合（机动类触发：高亮地区槽位、点击地区完成选择）。</summary>
        private readonly System.Collections.Generic.HashSet<string> _activationLegalAreaUids = new System.Collections.Generic.HashSet<string>();

        // —— 【2026-09 用户口径 JC062/JC021·联机】"一个窗多选"状态（`ChoiceRequest.Kind.FieldMultiPick`）——
        /// <summary>整窗多选进行中（服务端等一次聚合回包）。</summary>
        private bool _onlineMultiPickActive;
        /// <summary>本轮多选对应的服务端请求（回包用其 RequestId）。</summary>
        private Script.Net.ChoiceRequest _onlineMultiPickRequest;
        /// <summary>已选卡 UID（有序）。</summary>
        private readonly System.Collections.Generic.List<string> _onlineMultiPickCards = new System.Collections.Generic.List<string>();
        /// <summary>已选地区 UID（至多 1 个）。</summary>
        private readonly System.Collections.Generic.List<string> _onlineMultiPickAreas = new System.Collections.Generic.List<string>();
        /// <summary>服务端下发的卡候选（全量，不随选择变化）。</summary>
        private readonly System.Collections.Generic.List<string> _onlineMultiPickCardUids = new System.Collections.Generic.List<string>();
        /// <summary>服务端下发的地区候选（全量，不随选择变化）。</summary>
        private readonly System.Collections.Generic.List<string> _onlineMultiPickAreaUids = new System.Collections.Generic.List<string>();
        /// <summary>当前**可点**的卡候选（未选满时的并集，已选中的不再列出）。</summary>
        private readonly System.Collections.Generic.List<string> _onlineMultiPickCandCards = new System.Collections.Generic.List<string>();
        /// <summary>当前**可点**的地区候选。</summary>
        private readonly System.Collections.Generic.List<string> _onlineMultiPickCandAreas = new System.Collections.Generic.List<string>();
        /// <summary>「支付/横置」二选一窗口（JC068 准备维护；联机专用窗口，不本地结算）。</summary>
        private OnlinePayOrTapWindow _onlinePayOrTapWindow;
        /// <summary>「触发排序」窗口（联机专用；逐次入堆叠会多轮请求，窗口按 OrderContextId 复用）。</summary>
        private OnlineTriggerOrderWindow _onlineTriggerOrderWindow;
        /// <summary>当前排序窗口的上下文 id（服务端 OrderContextId；变化＝新一轮排序，需重建窗口）。</summary>
        private string _triggerOrderContextId;
        /// <summary>
        /// 本轮（当前 OrderContextId 下最新一轮）触发排序请求。
        /// 同一 ctx 会连发多轮（每次入堆叠一轮，各轮 RequestId 不同），回包必须用**最新一轮**的 RequestId，
        /// 否则新轮次的入堆叠/完成回包会挂到已完成的旧请求上而被服务端丢弃。
        /// </summary>
        private Script.Net.ChoiceRequest _onlineTriggerOrderCurrentRequest;
        /// <summary>联机专用排序窗口场景缓存。</summary>
        private PackedScene _triggerOrderWindowScene;

        // —— 角色卡「拖拽箭头激活」状态（复刻单机 CardDragController.TryBeginActivateDragAt/EndActivateDrag 手势） ——
        private Control _arrowLayer;                                  // 箭头层（动态创建：全屏、鼠标穿透、高层级）
        private PackedScene _stackArrowScene;                         // 复用单机 StackArrow 场景（只读，不改其类）
        private CardUI _activateDragSource;                           // 激活拖拽源卡 UI
        private Script.Data.Effects.EffectData _activateDragAbility;  // 源卡首个可激活能力（与单机 GetActivatingAbility 一致）
        private StackArrow _activateArrow;                            // 拖拽中的箭头
        private readonly List<CardUI> _activateHighlighted = new List<CardUI>();
        private readonly HashSet<string> _activateLegalUids = new HashSet<string>();

        // —— 服务端拒绝提示（G4）：联机提示窗 + 模态标记 ——
        private OnlineNoticeWindow _noticeWindow;
        private bool _noticeWindowOpen;

        // ==================== 堆叠区（对齐单机：卡面/效果文本/地区卡面/箭头） ====================
        private readonly Dictionary<StackEntryUI, Script.Game.Entities.StackEntry> _stackEntryUIMap
            = new Dictionary<StackEntryUI, Script.Game.Entities.StackEntry>();
        private readonly Dictionary<Script.Game.Entities.StackEntry, List<StackArrow>> _stackArrows
            = new Dictionary<Script.Game.Entities.StackEntry, List<StackArrow>>();

        // ==================== 快照应用队列（应用时机与表现层事件源） ====================
        // 到达即入队（同帧多份自动合并）；真正应用时机由 _Process 决定：
        //  - 本地交互/动画进行中可延后（有上限，避免长期不同步）；
        //  - 应用前用 SnapshotDiff 对比"上一份已应用快照"，把差异指令交给表现层（动画的触发源）。
        private readonly Script.Net.SnapshotApplicationQueue _snapshotQueue = new Script.Net.SnapshotApplicationQueue();
        private double _snapshotDeferTimer;
        private const double SnapshotMaxDeferSeconds = 1.5;

        /// <summary>是否打印每帧表现层差异摘要（调试用；默认关闭）。</summary>
        public bool VerbosePresentation { get; set; } = false;

        /// <summary>最近一次快照差异的表现层摘要（如 "add=1 move=2 stack=1"），供调试/观测。</summary>
        public string LastPresentationSummary { get; private set; } = "";

        // ==================== 联机拖拽（出牌/派遣暗藏/建立资产） ====================
        // 联机为快照驱动：拖拽结束不本地移动卡，只上行对应意图；服务端权威裁决后靠快照回显。
        // 蒙版模式：DeployCovert=派遣暗藏（需落到己方争夺区），EstablishAsset=建立资产（拖出即建立）。
        private enum MaskMode { None, DeployCovert, EstablishAsset }
        private MaskMode _maskMode = MaskMode.None;
        private Button _deployCovertBtn;   // 派遣暗藏按钮（缓存以统一刷新高亮）
        private Button _establishAssetBtn; // 建立资产按钮（缓存以统一刷新高亮）
        private Control _localHand;      // 本地玩家手牌容器（拖拽来源）
        private int _localHandOwner;     // 本地玩家号（与 _localPlayerId 一致，拖拽时校验）

        /// <summary>
        /// 争夺区（AreaZone 桶）实际渲染出的角色卡 UI，按容器登记。
        /// 【2026-10 修复】作为"争夺区统一平铺序列"的角色侧来源，替代原先
        /// "遍历容器子节点 + `!IsAttached && !IsSeal`" 的过滤写法 —— 该写法会把「形态角色」
        /// （SyncFormZone/MoveFormZone 迁入争夺区后仍保持 IsAttached=true，如 JC027"它仍然是附属"）
        /// 从序列里剔除，导致它停留旧位并与地区附属完全重叠、其它卡随之整体位移（横置卡错位）。
        /// 与单机 `_areaCardUIs`（争夺区 UI 全集、无过滤）同构。
        /// 每次 RebuildAreaZoneCards 开头清空重填。
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<Control, System.Collections.Generic.List<Control>>
            _areaRoleGroups = new System.Collections.Generic.Dictionary<Control, System.Collections.Generic.List<Control>>();

        public override void _Ready()
        {
            _cardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");

            _p1Hand = GetNodeOrNull<Control>("Player1Area/HandContainer");
            _p2Hand = GetNodeOrNull<Control>("Player2Area/HandContainer");
            _areasContainer = GetNodeOrNull<Control>("CenterArea/AreasContainer");
            _p1DeckZone = GetNodeOrNull<OnlineDeckZone>("Player1Area/DeckZone");
            _p2DeckZone = GetNodeOrNull<OnlineDeckZone>("Player2Area/DeckZone");
            _p1GraveZone = GetNodeOrNull<GraveyardZone>("Player1Area/GraveyardZone");
            _p2GraveZone = GetNodeOrNull<GraveyardZone>("Player2Area/GraveyardZone");
            _worldDeckZone = GetNodeOrNull<WorldDeckZone>("CenterArea/WorldDeckZone");
            _assetContainer1 = GetNodeOrNull<Control>("Player1Area/AssetContainer");
            _assetContainer2 = GetNodeOrNull<Control>("Player2Area/AssetContainer");
            _assetLabel1 = GetNodeOrNull<Label>("Player1Area/AssetContainer/AssetLabel");
            _assetLabel2 = GetNodeOrNull<Label>("Player2Area/AssetContainer/AssetLabel");
            _phaseLabel = GetNodeOrNull<Label>("ActionPanel/PhaseLabel");
            _p1ScoreLabel = GetNodeOrNull<Label>("Player1Area/ScoreZone/ScoreLabel");
            _p2ScoreLabel = GetNodeOrNull<Label>("Player2Area/ScoreZone/ScoreLabel");
            _scoreContainer1 = GetNodeOrNull<Control>("Player1Area/ScoreZone/CardContainer");
            _scoreContainer2 = GetNodeOrNull<Control>("Player2Area/ScoreZone/CardContainer");
            _councilContainer1 = GetNodeOrNull<Control>("Player1Area/CouncilContainer");
            _councilContainer2 = GetNodeOrNull<Control>("Player2Area/CouncilContainer");
            _stackContainer = GetNodeOrNull<Control>("StackPanel/StackContainer");
            _resultLabel = GetNodeOrNull<Label>("CenterArea/ResultLabel");
            _concedeButton = GetNodeOrNull<Button>("ActionPanel/ConcedeButton");

            if (_areaCardUIScene == null)
                _areaCardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaCardUI.tscn");
            if (_areaSlotScene == null)
                _areaSlotScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaSlot.tscn");
            if (_councilUIScene == null)
                _councilUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CouncilCardUI.tscn");
            if (_stackEntryScene == null)
                _stackEntryScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/StackEntryUI.tscn");
            if (_stackArrowScene == null)
                _stackArrowScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/StackArrow.tscn");

            // 箭头层（动态创建，避免改 ArenaOl.tscn）：全屏、鼠标穿透、高层级（对齐单机 Arena 的 ArrowLayer）
            _arrowLayer = new Control
            {
                Name = "ArrowLayer",
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = Layers.Arrows,   // 层号统一由 Script.UI.Layers 分配
                ZAsRelative = false,
            };
            _arrowLayer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(_arrowLayer);

            // 【鼠标悬浮预览】联机侧同样接管：查看者＝本机座位（`_localPlayerId`）。
            // 判定＝同一份 `CovertInspectRule` 单一真相源：自己的卡（含暗藏者）与公开卡可弹；
            // **对手手牌**在联机投影里本就是隐藏占位（`IsHidden`，无模板）→ 判定直接为假；
            // **对手暗藏者**需 JC057（未横置 + 同地区）授权 —— 但联机客户端**不持有隐藏卡的模板/卡图**
            // （服务端只下发占位），故即便授权成立也**没有卡面可显示**：该情形留作已知联机缺口（见清单 §7.9 K8）。
            _hoverPreview = new CardHoverPreview { Name = "CardHoverPreview", ZIndex = Layers.CardHoverPreview };
            AddChild(_hoverPreview);
            CardPreviewBus.Attach(card => Script.Manager.CovertInspectRule.CanViewerSeeCardFace(
                card, _localPlayerId, _matchClient?.ClientCardManager), _hoverPreview, viewerPlayerId: _localPlayerId);

            var passBtn = GetNodeOrNull<Button>("ActionPanel/PassButton");
            if (passBtn != null) passBtn.Pressed += () => DoPass(_localPlayerId);
            var endTurnBtn = GetNodeOrNull<Button>("ActionPanel/EndTurnButton");
            if (endTurnBtn != null) endTurnBtn.Pressed += () => DoEndTurn(_localPlayerId);

            // 基础行动：派遣暗藏 / 建立资产（联机→上行意图，服务端裁决）
            _deployCovertBtn = GetNodeOrNull<Button>("Player1Area/DeployCovertButton");
            if (_deployCovertBtn != null) _deployCovertBtn.Pressed += () => ToggleMask(MaskMode.DeployCovert);
            _establishAssetBtn = GetNodeOrNull<Button>("Player1Area/EstablishAssetButton");
            if (_establishAssetBtn != null) _establishAssetBtn.Pressed += () => ToggleMask(MaskMode.EstablishAsset);
            if (_concedeButton != null) _concedeButton.Pressed += () => DoConcede(_localPlayerId);

            // PM 调试命令输入框（联机客户端：上行 DebugCommand 意图，由服务端权威执行）
            _commandInput = GetNodeOrNull<TextEdit>("CommandInput");
            if (_commandInput != null)
            {
                _commandInput.Visible = false;
                _commandInput.ZIndex = CommandInputZIndex;   // 拉到最上层：否则秘社/卡牌会压在命令框上方
                _commandInput.GuiInput += OnCommandInputGuiInput;
            }
        }

        /// <summary>
        /// 挂载"客户端对局视图"（统一入口，增量 B）：
        ///   · 单场装配器路径传 `NetworkMatchLauncher`；
        ///   · 大厅路径传 `ClientSession`（一条连接贯穿大厅与对局）。
        /// 两者都实现 <see cref="IMatchClientView"/>，故战场表现层只有一条代码路径。
        /// </summary>
        public void AttachMatchClient(IMatchClientView client, int localPlayerId = 1)
        {
            _matchClient = client;
            _localPlayerId = localPlayerId;
            _snapshotQueue.Reset();   // 换局/新连接：清空基准快照与待应用队列
            if (_matchClient != null)
            {
                _matchClient.OnClientSnapshot += OnSnapshotReceived;
                _matchClient.OnClientChoiceRequest += OnClientChoiceRequest;
                _matchClient.OnClientIntentRejected += OnClientIntentRejected;

                // 【关键】晚订阅者补帧：大厅流程中初始快照在"房间页"阶段就已到达（服务端建局即推送），
                // 而本战场页是之后才挂载的——若只靠事件订阅会漏掉首帧，表现为"进入对战后一张卡都不显示"。
                if (_matchClient.LastSnapshot != null)
                {
                    GD.Print("[ArenaOl] 补帧：使用最近一次快照重建战场（晚挂载场景）");
                    OnSnapshotReceived(_matchClient.LastSnapshot);
                }
            }
        }

        /// <summary>兼容旧调用点（单场装配器）。</summary>
        public void AttachLauncher(NetworkMatchLauncher launcher, int localPlayerId = 1)
            => AttachMatchClient(launcher, localPlayerId);

        /// <summary>兼容旧调用点（大厅会话）。</summary>
        public void AttachSession(Script.Net.Godot.ClientSession session, int localPlayerId = 0)
            => AttachMatchClient(session, localPlayerId != 0 ? localPlayerId : session.MySeat);

        /// <summary>【对局结束 → 战场页展示获胜者】由服务端房间广播的权威结果驱动（对齐单机居中结算文字）。</summary>
        /// <param name="winnerSeat">获胜座位（1/2）；0 表示无胜者（服务端中止）。</param>
        public void ShowMatchResult(int winnerSeat)
        {
            _matchResultShown = true;
            if (_resultLabel == null) return;
            _resultLabel.Text = winnerSeat <= 0 ? "对局结束"
                : (winnerSeat == _localPlayerId ? "你获胜！" : "你落败！");
            _resultLabel.Visible = true;
            GD.Print($"[ArenaOl] 展示对局结果：{_resultLabel.Text}（获胜座位 {winnerSeat}）");
        }

        public void DetachLauncher()
        {
            if (_matchClient != null)
            {
                _matchClient.OnClientSnapshot -= OnSnapshotReceived;
                _matchClient.OnClientChoiceRequest -= OnClientChoiceRequest;
                _matchClient.OnClientIntentRejected -= OnClientIntentRejected;
            }
            _matchClient = null;
        }

        /// <summary>
        /// 离开战场页（切回房间页/大厅页）时**必须反订阅**：`ClientSession` 是进程级长寿命对象，
        /// 而本节点会被切场景释放；不反订阅会让后续快照/决策请求打到已释放节点（ObjectDisposedException）。
        /// </summary>
        public override void _ExitTree()
        {
            CardPreviewBus.Detach(_hoverPreview);   // 【鼠标悬浮预览】反注册（避免悬挂引用）
            DetachLauncher();
            base._ExitTree();
        }

        /// <summary>收到服务端快照：仅入队（同帧多份自动合并），由 <see cref="_Process"/> 在安全时机应用。</summary>
        private void OnSnapshotReceived(GameSnapshot snap)
        {
            if (snap == null) return;
            _snapshotQueue.Enqueue(snap);
            // 【触发排序窗口·状态同步】快照到达时，若排序窗口已不可见（可能已被"双框全空"静默关闭），
            // 清理本地引用，避免悬挂；排序窗口的自动关闭判定已在窗口内部由界面状态驱动
            // （OnlineTriggerOrderWindow.SilentCloseIfBothZonesEmpty），此处只做引用清理。
            PollOnlineTriggerOrderWindowClosed();
        }

        /// <summary>
        /// 清理已关闭/失效的触发排序窗口引用（排序窗口自身负责"双框全空即静默关闭"的判定）。
        /// </summary>
        private void PollOnlineTriggerOrderWindowClosed()
        {
            if (_onlineTriggerOrderWindow == null) return;
            if (!GodotObject.IsInstanceValid(_onlineTriggerOrderWindow) || !_onlineTriggerOrderWindow.Visible)
            {
                _onlineTriggerOrderWindow = null;
                _onlineTriggerOrderCurrentRequest = null;
                _activationSelectionBusy = false;
            }
        }

        /// <summary>【第0回合·调度】本地是否已上报过调度选择（防重复弹出 / 重复上报）。</summary>
        private bool _mulliganReportedLocal;

        /// <summary>
        /// 【第0回合·调度（客户端驱动）】每次应用快照后检查：若处于第 0 回合且尚未上报，
        /// 就**本地**弹出调度框（不再依赖服务端下发决策请求——那会早于本页挂载而丢失）。
        /// 玩家点"确认调度"时经 `PlayerIntent.MulliganReport` 主动上报，上报那一刻本页必然已就绪。
        /// </summary>
        private void EnsureMulliganPrompt()
        {
            var turn = _matchClient?.ClientTurnManager;
            if (turn == null) return;
            if (turn.CurrentPhase != global::TurnManager.TurnPhase.Mulligan) return;   // 仅第0回合
            if (_mulliganReportedLocal) return;                    // 已上报
            if (_choicePromptPanel != null && GodotObject.IsInstanceValid(_choicePromptPanel)
                && _choicePromptPanel.Visible) return;             // 已弹出

            int localSeat = _matchClient.LocalSeat;
            var hand = _matchClient.ClientZoneManager?.GetZoneCards(CardZone.Hand, localSeat);
            if (hand == null || hand.Count == 0) return;           // 手牌还没到（等下一份快照）

            // 组装一份**本地**请求（服务端不参与下发）：复用同一个 ChoicePromptPanel.BuildMulligan 界面
            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = localSeat,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectMulligan,
                Prompt = "开局调度：点选手牌以置入牌库底，并从牌库顶抽等量张后洗牌",
                ForceChoice = false,
                NumberMax = hand.Count,
            };
            foreach (var c in hand)
            {
                if (c == null) continue;
                req.OptionUids.Add(c.UID);
                req.OptionCardRefs.Add(ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
            }

            if (_choicePromptPanel == null || !GodotObject.IsInstanceValid(_choicePromptPanel))
            {
                _choicePromptPanel = new ChoicePromptPanel();
                AddChild(_choicePromptPanel);
            }
            _choicePromptPanel.Init(req, ResolveChoiceLabel, result =>
            {
                _mulliganReportedLocal = true;
                // 客户端驱动：走意图通道上报（服务端按真实手牌权威复检）
                var picked = (result != null && !result.Cancelled && result.SelectedUids != null)
                    ? result.SelectedUids
                    : new System.Collections.Generic.List<string>();
                _matchClient?.SendIntentAs(localSeat, Script.Net.PlayerIntent.Kind.MulliganReport, picked.ToArray());
                GD.Print($"[ArenaOl] 调度已上报：{picked.Count} 张");
            });
            var vpSize = GetViewport().GetVisibleRect().Size;
            _choicePromptPanel.Position = new Vector2I((int)((vpSize.X - _choicePromptPanel.Size.X) / 2), (int)(vpSize.Y / 2) + 120);
            _choicePromptPanel.Popup();
            GD.Print($"[ArenaOl] 第0回合·已弹出调度框（候选 {req.OptionUids.Count} 张手牌）");
        }

        /// <summary>服务端下发决策请求：弹出联机决策面板，玩家选择后回包。</summary>
        private void OnClientChoiceRequest(Script.Net.ChoiceRequest request)
        {
            if (request == null) return;

            // 【对齐单机】决策请求改用单机同款窗口（调查/战斗/破平/支付横置/触发排序/选目标），失败再退回通用决策面板
            if (TryOpenOnlineDecisionWindow(request)) return;

            // 【2026-09 修复·用户实测"JC021 发动、选择地区后，后续目标选择框没有弹出"】
            // 通用决策面板**每次请求都用新实例**：上一个请求完成时面板已 `QueueFree()`，但同一帧内（本地主机/低延迟）
            // 到达的**下一个**请求（多目标逐槽 / 确认面板）会看到"实例仍有效"而复用它 ⇒ 新窗口被随后的释放一起销毁，
            // 表现为"第一个框弹了、后面的框不弹"。这里显式释放旧面板再建新的。
            if (_choicePromptPanel != null && GodotObject.IsInstanceValid(_choicePromptPanel))
                _choicePromptPanel.QueueFree();
            _choicePromptPanel = new ChoicePromptPanel();
            AddChild(_choicePromptPanel);
            _choicePromptPanel.Init(request, ResolveChoiceLabel, result =>
            {
                // 【乐观回显·快照架构内】手牌上限弃牌（SelectHandCards 且 NumberMin==NumberMax==N）：
                // 玩家点确认那一刻先在本地把手牌 UI 摘掉、让"弃牌"立即可见，随后由服务端权威快照全量重建收敛。
                // 这是"客户端预测 + 服务端权威"的常规做法：不改变快照驱动架构，只提前呈现"几乎必然成立"的本地结果。
                TryOptimisticEchoHandLimitDiscard(request, result);
                if (_matchClient != null)
                    _matchClient.SendChoiceResult(result);
            });
            var vpSize = GetViewport().GetVisibleRect().Size;
            _choicePromptPanel.Position = new Vector2I((int)((vpSize.X - _choicePromptPanel.Size.X) / 2), (int)(vpSize.Y / 2) + 120);
            _choicePromptPanel.Popup();
        }

        /// <summary>
        /// 【联机·乐观回显】手牌上限弃牌确认瞬间，本地立即把所选卡的手牌 UI 移除（并同步监听到墓地区），
        /// 让"弃牌"在服务端快照回来之前就可见。仅作用于视觉，不写任何 Model；服务端权威快照到达后
        /// <see cref="ApplySnapshot"/> 的全量重建会覆盖本回显，二者最终一致（预测失败也会被纠正）。
        /// 判定口径：<see cref="Script.Net.ChoiceRequest.Kind.SelectHandCards"/> 且 NumberMin==NumberMax（恰好 N 张的强制弃牌）。
        /// </summary>
        private void TryOptimisticEchoHandLimitDiscard(Script.Net.ChoiceRequest request, Script.Net.ChoiceResult result)
        {
            if (request == null || result == null || result.Cancelled) return;
            if (request.RequestKind != Script.Net.ChoiceRequest.Kind.SelectHandCards) return;
            if (request.NumberMax <= 0 || request.NumberMin != request.NumberMax) return;   // 仅"恰好 N 张"的强制弃牌
            var selected = result.SelectedUids;
            if (selected == null || selected.Count == 0) return;

            // 仅当这些卡属于本地玩家手牌时才做本地回显（服务端不会让对手弃本方手牌）
            var localHand = _localPlayerId == 1 ? _p1Hand : _p2Hand;
            if (localHand == null) return;

            int echoed = 0;
            foreach (var uid in selected)
            {
                if (string.IsNullOrEmpty(uid)) continue;
                if (!_cardUIMap.TryGetValue(uid, out var ui) || ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                if (ui.GetParent() != localHand) continue;   // 不在本方手牌容器内，跳过（避免误摘）
                ui.GetParent()?.RemoveChild(ui);
                _cardUIMap.Remove(uid);
                ui.QueueFree();
                echoed++;
            }
            if (echoed > 0)
            {
                ArrangeHandOnly();
                GD.Print($"[ArenaOl] 乐观回显：手牌上限弃牌 {echoed} 张已本地移除（等待服务端快照收敛）");
            }
        }

        /// <summary>仅重排双方手牌（乐观回显后的即时整理；不触发全量重建）。</summary>
        private void ArrangeHandOnly()
        {
            ArrangeChildren(_p1Hand);
            ArrangeChildren(_p2Hand);
        }

        // ==================== 对抗窗口（对齐单机：调查/战斗/破平） ====================

        private PackedScene _strengthWindowScene;        // 联机专用 OnlineStrengthWindow.tscn（不改单机窗口）
        private PackedScene _investigationWindowScene;   // 复用单机 InvestigationWindow.tscn
        private PackedScene _tieBreakWindowScene;        // 复用单机 TieBreakWindow.tscn
        private OnlineStrengthWindow _onlineStrengthWindow;
        /// <summary>【鼠标悬浮预览】整张卡图的悬浮浮层（见 `CardHoverPreview` / `CardPreviewBus`）。</summary>
        private CardHoverPreview _hoverPreview;
        private InvestigationWindow _onlineInvestigationWindow;
        /// <summary>【2026-09·JC062/JC057】联机检视结果窗（与单机 `Arena.PromptInspectCards` 同款）。</summary>
        private InspectWindow _onlineInspectWindow;
        private bool _onlineCombatWindowOpen;

        /// <summary>
        /// 把服务端下发的决策请求转成**单机同款窗口**：
        ///  - CombatStrength → StrengthWindow（点击场上败方卡左键+1/右键-1，确认后回 ChoiceResult；不本地结算）
        ///  - CombatInvestigation → InvestigationWindow（拖拽把待分配调查卡分到牌库顶/底，确认后回 ChoiceResult）
        ///  - TieBreak → TieBreakWindow（确认/取消→ChoiceResult）
        ///  - PayOrTap → OnlinePayOrTapWindow（对齐单机 Arena.PromptPayOrTap：支付/横置二选一，强制、支付不足置灰）
        ///  - SelectTriggerOrder → OnlineTriggerOrderWindow（对齐单机 TriggerOrderWindow：拖拽排序 + 全部/第一张入堆叠 + 完成）
        ///  - SelectTarget → 场上选目标状态机（对齐单机 TargetSelectionWindow：高亮合法目标，点选完成）
        /// 返回 false 表示未处理（调用方退回通用决策面板）。
        /// </summary>
        private bool TryOpenOnlineDecisionWindow(Script.Net.ChoiceRequest req)
        {
            switch (req.RequestKind)
            {
                case Script.Net.ChoiceRequest.Kind.CombatStrength:
                    return OpenOnlineStrengthWindow(req);
                case Script.Net.ChoiceRequest.Kind.CombatInvestigation:
                    return OpenOnlineInvestigationWindow(req);
                case Script.Net.ChoiceRequest.Kind.Prediction:
                    return OpenOnlinePredictionWindow(req);
                case Script.Net.ChoiceRequest.Kind.TieBreak:
                    return OpenOnlineTieBreakWindow(req);
                case Script.Net.ChoiceRequest.Kind.PayOrTap:
                    return OpenOnlinePayOrTapWindow(req);
                case Script.Net.ChoiceRequest.Kind.SelectTriggerOrder:
                    return OpenOnlineTriggerOrderWindow(req);
                case Script.Net.ChoiceRequest.Kind.SelectTarget:
                    return OpenOnlineTargetSelectionWindow(req);
                case Script.Net.ChoiceRequest.Kind.SelectActivationCost:
                    // 【P2·联机】激活代价选择：与单机 `Arena.BeginActivateWithSacrifice` /
                    // `BeginActivateWithCounterCostChoice` / `BeginActivateWithCounterCostAllocation`
                    // **1:1 同款按钮窗**（同一 `ActivationChoiceItem` 列表、同样文案与"至少 1 个/重置/取消"口径）。
                    return OpenOnlineActivationCostWindow(req);
                case Script.Net.ChoiceRequest.Kind.MultiTargetSlot:
                    // 【2026-09·K15·A86】多目标逐槽：场上看得见的槽走"高亮 + 点击"（与单机同款），
                    // 其余（区域牌堆/玩家槽/无候选）返回 false 退回通用面板的按钮列表。
                    return OpenOnlineMultiTargetSlotWindow(req);
                case Script.Net.ChoiceRequest.Kind.ConfirmTargets:
                case Script.Net.ChoiceRequest.Kind.SelectEffectMode:
                case Script.Net.ChoiceRequest.Kind.NumberInput:
                    // 【K15/K16/K11】确认面板 / 模式分支"选择一项" / 数值输入框：
                    // 走通用决策面板的对应 Build* 分支（ChoicePromptPanel）。
                    return false;
                case Script.Net.ChoiceRequest.Kind.FieldMultiPick:
                    // 【2026-09 用户口径 JC062/JC021】整窗多选（服务端聚合下发）：与单机 1:1 同款
                    // "一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消"；窗口不可用时退回通用面板按钮列表。
                    return OpenOnlineFieldMultiPickWindow(req);
                case Script.Net.ChoiceRequest.Kind.InspectCards:
                    // 【2026-09·JC062/JC057 用户口径】检视结果：与单机 1:1 同款 `InspectWindow`
                    // （整批卡面列在一个窗、只有一个『确认』）；请求未带卡面引用时退回通用面板（卡名列示）。
                    return OpenOnlineInspectWindow(req);
                default:
                    return false;
            }
        }

        private T LoadWindowScene<T>(ref PackedScene cache, string path) where T : Node
        {
            if (cache == null) cache = ResourceLoader.Load<PackedScene>(path);
            return cache?.Instantiate<T>();
        }

        /// <summary>
        /// 【P2·联机】激活**代价选择窗**（与单机三套窗 1:1）：
        ///  · **固定/单张模式**（`Amount > 0` 或牺牲代价）：一张卡一个按钮（文案由服务端组装：
        ///    "牺牲 X" / "从 X 上移除 N 个标志"）＋"取消（不发动）" → 回包 `SelectedUid`；
        ///  · **逐卡分配模式**（`Amount <= 0`，JC055②"移除 X 个标志、X 由玩家选、至少 1"）：
        ///    **复刻单机 `ShowCounterAllocationStep`** —— 点一次某张卡即记录 1 点（显示"已选 X / 共 N"），
        ///    满额后该卡不可再加，"确认（共移除 N 个）"仅在总量 ≥ 1 时出现，另有"重置选择"与"取消（不发动）"
        ///    → 回包 `SelectedUids` + `SelectedAmounts`（与单机 `allocation` 字典同义）。
        /// 候选与上限**全部由服务端下发**（`OptionUids`/`OptionLabels`/`OptionMaxAmounts`/`Amount`），
        /// 客户端只收集、不重算规则。
        /// </summary>
        private bool OpenOnlineActivationCostWindow(Script.Net.ChoiceRequest req)
        {
            var uids = req.OptionUids ?? new List<string>();
            var labels = req.OptionLabels ?? new List<string>();
            var maxes = req.OptionMaxAmounts ?? new List<int>();
            // **逐卡分配模式**的判据＝服务端下发了"每张卡的现有标志数上限"（`OptionMaxAmounts` 非空）；
            // 其余（牺牲代价 / 固定数量标志代价）＝**单张模式**（一张卡一个按钮）。
            bool allocationMode = maxes.Count > 0;

            if (allocationMode)
            {
                var allocation = new Dictionary<string, int>();
                // 取消/关窗 → 回包 Cancelled（否则服务端要等超时）；同时复位本地模态
                System.Action cancel = () =>
                {
                    _activationSelectionBusy = false;
                    _matchClient?.SendChoiceResult(
                        new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = true });
                };
                System.Action reopen = null;
                reopen = () =>
                {
                    int total = 0;
                    foreach (var kv in allocation) total += kv.Value;
                    var items = new List<ActivationChoiceItem>();
                    for (int i = 0; i < uids.Count; i++)
                    {
                        string uid = uids[i];
                        int have = (i < maxes.Count) ? maxes[i] : 0;
                        allocation.TryGetValue(uid, out int used);
                        string name = (i < labels.Count && !string.IsNullOrEmpty(labels[i])) ? labels[i] : uid;
                        bool full = used >= have;
                        string capturedUid = uid;
                        items.Add(new ActivationChoiceItem
                        {
                            Label = $"{name}：移除 1 个（已选 {used} / 共 {have}）",
                            Disabled = full,
                            OnChosen = () =>
                            {
                                if (!full) allocation[capturedUid] = used + 1;
                                reopen();
                            },
                        });
                    }
                    if (total >= 1)
                    {
                        items.Add(new ActivationChoiceItem
                        {
                            Label = $"确认（共移除 {total} 个标志）",
                            OnChosen = () =>
                            {
                                var res = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                                foreach (var kv in allocation)
                                {
                                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                                    res.SelectedUids.Add(kv.Key);
                                    res.SelectedAmounts.Add(kv.Value);
                                }
                                _activationSelectionBusy = false;
                                _matchClient?.SendChoiceResult(res);
                                GD.Print($"[ArenaOl] 激活标志代价已回包：{res.SelectedUids.Count} 张卡、共 {total} 个标志");
                            },
                        });
                        items.Add(new ActivationChoiceItem
                        {
                            Label = "重置选择",
                            OnChosen = () => { allocation.Clear(); reopen(); },
                        });
                    }
                    else
                    {
                        items.Add(new ActivationChoiceItem
                        {
                            Label = "（请至少选择 1 个标志才能确认）",
                            Disabled = true,
                            OnChosen = () => { },
                        });
                    }
                    items.Add(new ActivationChoiceItem { Label = "取消（不发动）", OnChosen = cancel });
                    OpenOnlineChoiceWindow(items, cancel);
                };
                reopen();
                GD.Print($"[ArenaOl] 激活标志代价窗（逐卡分配）：候选 {uids.Count} 张");
                return true;
            }

            // 固定/单张模式：一张卡一个按钮（文案由服务端给出）
            System.Action cancelSingle = () =>
            {
                _activationSelectionBusy = false;
                _matchClient?.SendChoiceResult(
                    new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = true });
            };
            var single = new List<ActivationChoiceItem>();
            for (int i = 0; i < uids.Count; i++)
            {
                string uid = uids[i];
                if (string.IsNullOrEmpty(uid)) continue;
                string label = (i < labels.Count && !string.IsNullOrEmpty(labels[i])) ? labels[i] : uid;
                string capturedUid = uid;
                single.Add(new ActivationChoiceItem
                {
                    Label = label,
                    OnChosen = () =>
                    {
                        _activationSelectionBusy = false;
                        _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                        {
                            RequestId = req.RequestId,
                            Cancelled = false,
                            SelectedUid = capturedUid,
                        });
                    },
                });
            }
            single.Add(new ActivationChoiceItem { Label = "取消（不发动）", OnChosen = cancelSingle });
            OpenOnlineChoiceWindow(single, cancelSingle);
            GD.Print($"[ArenaOl] 激活代价窗（单选）：候选 {uids.Count} 张");
            return true;
        }

        /// <summary>战斗对抗：使用**联机专用** `OnlineStrengthWindow`（与单机视觉/操作一致，但不本地结算）；
        /// 合法承伤目标由服务端下发的 `OptionUids` 决定（客户端不复制规则）。</summary>
        private bool OpenOnlineStrengthWindow(Script.Net.ChoiceRequest req)
        {
            var win = LoadWindowScene<OnlineStrengthWindow>(ref _strengthWindowScene, "res://Scenes/Net/OnlineStrengthWindow.tscn");
            if (win == null) return false;

            var legal = new HashSet<string>(req.OptionUids ?? new List<string>());
            AddChild(win);
            win.IsLegalTarget = inst => inst != null && legal.Contains(inst.UID);   // 服务端下发的合法目标集合
            win.FindCardUI = inst => FindCardUiByInstance(inst);                      // 显示/清除分配数字
            win.OnConfirmed = allocations =>
            {
                var res = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                if (allocations != null)
                    foreach (var kv in allocations)
                    {
                        if (kv.Key == null || kv.Value <= 0) continue;
                        res.SelectedUids.Add(kv.Key.UID);
                        res.SelectedAmounts.Add(kv.Value);
                    }
                _matchClient?.SendChoiceResult(res);
                _onlineStrengthWindow = null;
                _onlineCombatWindowOpen = false;
                _activationSelectionBusy = false;
                GD.Print($"[ArenaOl] 战斗对抗分配已回包：{res.SelectedUids.Count} 个目标");
            };
            win.Configure(req.PlayerId, req.Amount, req.AreaUid, req.AreaNo);
            var vp = GetViewport().GetVisibleRect().Size;
            win.Position = new Vector2I((int)((vp.X - win.Size.X) / 2), (int)(vp.Y / 2) + 120);
            win.Popup();

            _onlineStrengthWindow = win;
            _onlineCombatWindowOpen = true;
            _activationSelectionBusy = true;   // 决策期间本地模态（并延后快照应用，避免重建打断窗口）
            GD.Print($"[ArenaOl] 战斗对抗窗口已打开（地区{req.AreaNo}，伤害 {req.Amount}，合法目标 {legal.Count}）");
            return true;
        }

        /// <summary>调查对抗：复用单机 InvestigationWindow；待分配卡由快照（仅决策者可见）投影提供。</summary>
        private bool OpenOnlineInvestigationWindow(Script.Net.ChoiceRequest req)
        {
            var win = LoadWindowScene<InvestigationWindow>(ref _investigationWindowScene, "res://Scenes/CombatResolution/InvestigationWindow.tscn");
            if (win == null) return false;

            var cards = new List<CardInstance>();
            // 【关键】决策等待期间服务端不会推快照 → 待分配卡必须随请求下发（OptionCardRefs）；
            // 客户端用投影 CardManager 按引用重建实例（含模板，可显示卡面）。
            var clientCardMgr = _matchClient?.ClientCardManager;
            if (clientCardMgr != null && req.OptionCardRefs != null)
            {
                foreach (var r in req.OptionCardRefs)
                {
                    if (r == null) continue;
                    var inst = clientCardMgr.RebuildFromRef(r);
                    if (inst != null) cards.Add(inst);
                }
            }
            // 兜底：若请求未带引用（旧服务端），退回用快照投影的待分配卡
            if (cards.Count == 0)
            {
                var projected = _matchClient?.ClientInvestigationCards;
                if (projected != null) foreach (var c in projected) if (c != null) cards.Add(c);
            }
            if (cards.Count == 0)
            {
                GD.PrintErr("[ArenaOl] 调查对抗窗口：未拿到待分配卡（请求缺 OptionCardRefs 且快照无投影），退回通用决策面板");
                win.QueueFree();
                return false;
            }

            AddChild(win);
            var vp = GetViewport().GetVisibleRect().Size;
            win.Position = new Vector2I((int)((vp.X - win.Size.X) / 2), (int)(vp.Y / 2) + 150);
            win.Popup();
            win.OnResolved = () => { _onlineInvestigationWindow = null; _onlineCombatWindowOpen = false; _activationSelectionBusy = false; };
            win.Configure(req.PlayerId, req.AreaNo, cards,
                (winner, toTop, toBottom) =>
                {
                    // 回包语义：SelectedUids = 放回牌库顶的顺序（未选中的由服务端放牌库底）
                    var res = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    if (toTop != null) foreach (var c in toTop) if (c != null) res.SelectedUids.Add(c.UID);
                    _matchClient?.SendChoiceResult(res);
                    _onlineInvestigationWindow = null;
                    _onlineCombatWindowOpen = false;
                    _activationSelectionBusy = false;
                    GD.Print($"[ArenaOl] 调查对抗放回已回包：牌库顶 {res.SelectedUids.Count} 张");
                },
                (pid, count) => { /* 联机：抽牌由服务端结算，客户端不执行 */ });

            _onlineInvestigationWindow = win;
            _onlineCombatWindowOpen = true;
            _activationSelectionBusy = true;
            GD.Print($"[ArenaOl] 调查对抗窗口已打开（地区{req.AreaNo}，待分配 {cards.Count} 张）");
            return true;
        }

        /// <summary>
        /// 【P4·预测X】复用单机调查对抗分配窗口（`InvestigationWindow`）：把牌库顶 X 张拖到"牌库顶/牌库底"两区。
        /// 与调查对抗的唯一差别：标题为"预测X"、**不抽牌**。
        /// 待分配的 X 张必须随请求下发（`OptionCardRefs`，决策等待期间服务端不推快照），否则退回通用决策面板。
        /// 回包语义与调查对抗一致：`SelectedUids` = 置于牌库顶的牌（有序），其余按原序置底（服务端权威结算）。
        /// </summary>
        private bool OpenOnlinePredictionWindow(Script.Net.ChoiceRequest req)
        {
            var win = LoadWindowScene<InvestigationWindow>(ref _investigationWindowScene, "res://Scenes/CombatResolution/InvestigationWindow.tscn");
            if (win == null) return false;

            var cards = new List<CardInstance>();
            var clientCardMgr = _matchClient?.ClientCardManager;
            if (clientCardMgr != null && req.OptionCardRefs != null)
            {
                foreach (var r in req.OptionCardRefs)
                {
                    if (r == null) continue;
                    var inst = clientCardMgr.RebuildFromRef(r);
                    if (inst != null) cards.Add(inst);
                }
            }
            if (cards.Count == 0)
            {
                GD.PrintErr("[ArenaOl] 预测窗口：未拿到待分配牌（请求缺 OptionCardRefs），退回通用决策面板");
                win.QueueFree();
                return false;
            }

            AddChild(win);
            var vp = GetViewport().GetVisibleRect().Size;
            win.Position = new Vector2I((int)((vp.X - win.Size.X) / 2), (int)(vp.Y / 2) + 150);
            win.Popup();
            win.OnResolved = () => { _onlineInvestigationWindow = null; _onlineCombatWindowOpen = false; _activationSelectionBusy = false; };
            win.Configure(req.PlayerId, 0, cards,
                (pid, toTop, toBottom) =>
                {
                    var res = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    if (toTop != null) foreach (var c in toTop) if (c != null) res.SelectedUids.Add(c.UID);
                    _matchClient?.SendChoiceResult(res);
                    _onlineInvestigationWindow = null;
                    _onlineCombatWindowOpen = false;
                    _activationSelectionBusy = false;
                    GD.Print($"[ArenaOl] 预测置顶已回包：牌库顶 {res.SelectedUids.Count} 张");
                },
                (pid, count) => { /* 预测不是抽牌：联机同样不抽牌 */ },
                $"预测{cards.Count}：将任意数量置于牌库顶（其余置于牌库底）");

            _onlineInvestigationWindow = win;
            _onlineCombatWindowOpen = true;
            _activationSelectionBusy = true;
            GD.Print($"[ArenaOl] 预测窗口已打开（待分配 {cards.Count} 张）");
            return true;
        }

        /// <summary>
        /// 【2026-09·JC062/JC057 用户口径】**联机检视结果窗**：与单机 `Arena.PromptInspectCards` **1:1 同款**
        /// （`InspectWindow`：整批卡面列在**一个窗**里 + **只有一个『确认』按钮**，点击即关闭）。
        /// 待展示的牌由服务端随请求下发（`OptionCardRefs`：决策等待期间服务端不推快照），客户端用投影
        /// CardManager（`RebuildFromRef`）重建实例后渲染**卡面**（与调查/预测窗同一手法）。
        /// 回包 `ChoiceResult{Cancelled=false}`——检视**不改变任何状态**，服务端只等一个确认
        /// （确认后继续结算；JC062 的"抓一张牌"排在确认之后，见 `EffectEngine._deferredSorcery`）。
        /// 请求未带卡面引用（极旧服务端/异常）→ 返回 false 退回通用决策面板（列卡名），不静默丢检视。
        /// </summary>
        private bool OpenOnlineInspectWindow(Script.Net.ChoiceRequest req)
        {
            var cards = new List<CardInstance>();
            var clientCardMgr = _matchClient?.ClientCardManager;
            if (clientCardMgr != null && req.OptionCardRefs != null)
            {
                foreach (var r in req.OptionCardRefs)
                {
                    if (r == null) continue;
                    var inst = clientCardMgr.RebuildFromRef(r);
                    if (inst != null) cards.Add(inst);
                }
            }
            if (cards.Count == 0)
            {
                GD.PrintErr("[ArenaOl] 检视窗口：未拿到被检视牌（请求缺 OptionCardRefs），退回通用决策面板");
                return false;
            }

            var win = new InspectWindow();
            win.OnConfirmed = () =>
            {
                _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    Cancelled = false,   // 只有一个『确认』：不存在"取消检视"
                });
                _onlineInspectWindow = null;
                _activationSelectionBusy = false;
                GD.Print("[ArenaOl] 检视确认已回包（服务端继续结算）");
            };
            // 【2026-09 修复·同单机】**先入树、再 Init**（否则 `CardUI._Ready()` 的 `SetFace(false)`
            // 会把 `Init` 里翻好的正面盖成卡背）；`Init` 里已显式设定窗口尺寸，故随后的居中取值正确。
            AddChild(win);
            win.Init(req.Prompt, cards);
            var vp = GetViewport().GetVisibleRect().Size;
            win.Position = new Vector2I((int)((vp.X - win.Size.X) / 2), (int)((vp.Y - win.Size.Y) / 2));
            win.Popup();

            _onlineInspectWindow = win;
            _activationSelectionBusy = true;   // 决策期间本地模态（并延后快照应用，避免重建打断窗口）
            GD.Print($"[ArenaOl] 检视窗口已打开（{cards.Count} 张，只有一个确认按钮）");
            return true;
        }

        /// <summary>先手破平：复用单机 TieBreakWindow（确认→Cancelled=false；取消→Cancelled=true）。</summary>
        private bool OpenOnlineTieBreakWindow(Script.Net.ChoiceRequest req)
        {
            var win = LoadWindowScene<TieBreakWindow>(ref _tieBreakWindowScene, "res://Scenes/CombatResolution/TieBreakWindow.tscn");
            if (win == null) return false;

            AddChild(win);
            win.Configure(req.AreaNo, (CombatManager.CombatType)req.CombatTypeValue, confirmed =>
            {
                _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    Cancelled = !confirmed,
                });
                _onlineCombatWindowOpen = false;
                _activationSelectionBusy = false;
                GD.Print($"[ArenaOl] 先手破平回包：{(confirmed ? "确认" : "取消")}");
            });
            var vp = GetViewport().GetVisibleRect().Size;
            win.Position = new Vector2I((int)((vp.X - win.Size.X) / 2), (int)(vp.Y / 2) + 120);
            win.Popup();

            _onlineCombatWindowOpen = true;
            _activationSelectionBusy = true;
            return true;
        }

        /// <summary>
        /// 支付/横置二选一（JC068 阿格斯IV「准备维护」）：对齐单机 `Arena.PromptPayOrTap` 的弹窗——
        /// 两个选项（支付 N / 横置来源卡）、费用不足时"支付"置灰、强制选择（无取消、点窗外不关闭）。
        /// 联机不做本地结算：选中后回 `ChoiceResult{Pay=...}`，由服务端 `ResolvePrepareUpkeep` 落地。
        /// </summary>
        private bool OpenOnlinePayOrTapWindow(Script.Net.ChoiceRequest req)
        {
            if (_onlinePayOrTapWindow != null && GodotObject.IsInstanceValid(_onlinePayOrTapWindow))
                _onlinePayOrTapWindow.QueueFree();

            var win = new OnlinePayOrTapWindow();
            AddChild(win);

            // 来源卡名（单机文案为"横置阿格斯IV"）：由请求的 SourceUid 在投影世界里解析
            string sourceName = string.IsNullOrEmpty(req.SourceUid) ? null : ResolveChoiceLabel(req.SourceUid);
            if (string.IsNullOrEmpty(sourceName) || sourceName == "（未命名）")
                sourceName = "来源卡";

            string payLabel = $"支付{req.PayAmount}（横置1张资产区牌）";
            string tapLabel = $"横置{sourceName}";
            win.Init(req.Prompt, payLabel, !req.PayAllowed, tapLabel, pay =>
            {
                _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    Pay = pay,
                    Cancelled = false,
                });
                _onlinePayOrTapWindow = null;
                _activationSelectionBusy = false;
                GD.Print($"[ArenaOl] 准备维护回包：{(pay ? $"支付{req.PayAmount}" : "横置")}");
            });

            var vp = GetViewport().GetVisibleRect().Size;
            win.Position = new Vector2I((int)((vp.X - win.Size.X) / 2), (int)(vp.Y / 2) + 120);
            win.Popup();

            _onlinePayOrTapWindow = win;
            _activationSelectionBusy = true;   // 决策期间本地模态（并延后快照应用，避免重建打断窗口）
            GD.Print($"[ArenaOl] 准备维护窗口已打开（支付{req.PayAmount}，可支付={req.PayAllowed}，来源={sourceName}）");
            return true;
        }

        /// <summary>
        /// 触发/效果选目标：对齐单机 `TriggerInteractionCoordinator.RequestTargetSelection` ——
        /// 复用联机既有"场上点击选目标"状态机（`TargetSelectionWindow` + 合法目标高亮），点选合法目标即回包；
        /// 单机该路径**不可取消**（必须点一个合法目标），故不提供取消按钮，仅保留服务端超时兜底。
        /// 目标可为**场上卡**（`OptionUids` 指向卡实例）或**地区**（机动类触发，指向地区 UID）。
        /// </summary>
        private bool OpenOnlineTargetSelectionWindow(Script.Net.ChoiceRequest req)
            => OpenOnlineFieldTargetSelection(req, extraLabel: null, onExtra: null);

        /// <summary>
        /// 【2026-09·K15 多目标·联机】**场上点选**取目标（含"出口按钮"）：
        /// 与单机 `Arena.PromptMultiTargets` 的 `AskSlotByFieldClick` 同款——
        /// 候选在场上看得见的槽（争夺区角色/暗藏者/附属/秘社/地区）走"**高亮 + 点击**"，
        /// **不弹候选列表窗**（用户口径："选择查看的暗藏者应该选择场上的高亮暗藏者，而不是弹出选择框"）。
        /// `extraLabel` 非空时把窗口备用按钮用作该槽的出口（可留空 → 回空 UID；必选 → 回 Cancelled）。
        /// </summary>
        private bool OpenOnlineFieldTargetSelection(Script.Net.ChoiceRequest req, string extraLabel,
            System.Action onExtra)
        {
            // 区分"卡目标"与"地区目标"：能解析为地区 UID 的走地区高亮（对齐单机 trigger.IsAreaTarget 分支）
            var areaUids = new List<string>();
            var cardUids = new List<string>();
            foreach (var uid in req.OptionUids ?? new List<string>())
            {
                if (string.IsNullOrEmpty(uid)) continue;
                if (IsKnownAreaUid(uid)) areaUids.Add(uid);
                else cardUids.Add(uid);
            }

            _activationSelectionBusy = true;
            _activationTargetActive = true;
            _activationTargetSource = null;      // 本路径不是"激活/出牌"，选中后回的是 ChoiceResult
            _activationTargetAbility = null;
            _pendingPlayCardUid = null;
            _pendingChoiceTargetRequest = req;   // 标记：本轮的"选中"要回成 ChoiceResult
            _activationLegalCardUids.Clear();
            foreach (var u in cardUids) _activationLegalCardUids.Add(u);
            _activationLegalAreaUids.Clear();
            foreach (var u in areaUids) _activationLegalAreaUids.Add(u);

            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (_activationLegalCardUids.Contains(ui.Instance.UID)) ui.SetTargetHighlight(true);
            }
            // 【2026-09】秘社不在 `_cardUIMap`（走 `_councilCardUIMap`），但同样可能是合法目标
            // （如 JC051「结附于本方秘社」的宿主候选）→ 必须一并高亮，否则联机端"看不到可点目标"。
            HighlightOnlineCouncilTargets(true);
            ApplyOnlineAreaHighlights();

            EnsureTargetWindow();
            if (_onlineTargetWindow == null)
            {
                _activationSelectionBusy = false;
                _activationTargetActive = false;
                _pendingChoiceTargetRequest = null;
                return false;
            }
            _onlineTargetWindow.OnTargetPicked = OnServerChoiceTargetPicked;
            // 【2026-09·K15】该槽的出口按钮（可留空 → 回空 UID；必选 → 回 Cancelled），
            // 与单机 `TargetSelectionWindow.OnExtra` 同款语义（窗口自带的备用按钮）。
            _onlineTargetWindow.OnExtra = onExtra == null ? null : () =>
            {
                ClearActivationTargetSelection();
                _pendingChoiceTargetRequest = null;
                onExtra();
            };
            _onlineTargetWindow.SetBlocking(false);   // 点击穿透到场上卡/地区
            string hint = cardUids.Count == 0 && areaUids.Count > 0
                ? "请点击一个高亮的地区（将移动至该地区）。"
                : "请在场上选择合法目标卡（点击场上卡或秘社完成选择）。";
            _onlineTargetWindow.Open(req.Prompt ?? "请选择目标", hint, extraLabel);
            GD.Print($"[ArenaOl] 触发选目标窗口已打开（卡目标 {cardUids.Count} 个，地区目标 {areaUids.Count} 个，出口={extraLabel ?? "无"}）");
            return true;
        }

        /// <summary>
        /// 【2026-09·K15 多目标·联机】逐槽 `MultiTargetSlot` 请求的落地：
        ///  · 该槽候选**在场上看得见**（服务端经 `ChoiceRequest.SlotFieldVisible` 下发同一份判定，
        ///    客户端不重算规则）→ 走"**场上高亮 + 点击**"（`OpenOnlineFieldTargetSelection`）；
        ///  · 否则（区域牌堆/玩家槽/无候选）→ 返回 false，退回通用决策面板的按钮列表
        ///    （那里有"留空该槽 / 取消（不打出）"出口）。
        /// 回包：选中 → `SelectedUid`；出口 → 可留空回**空 UID**、必选回 `Cancelled=true`（服务端权威复检）。
        /// </summary>
        private bool OpenOnlineMultiTargetSlotWindow(Script.Net.ChoiceRequest req)
        {
            if (!req.SlotFieldVisible) return false;
            bool optional = req.SlotOptional;
            string extraLabel = optional ? "不选（留空该槽）" : "取消（不打出）";
            var captured = req;
            return OpenOnlineFieldTargetSelection(req, extraLabel, () =>
            {
                _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                {
                    RequestId = captured.RequestId,
                    SelectedUid = "",                 // 空 UID ＝ 该槽留空（服务端按 SlotOptional 复检）
                    Cancelled = !optional,            // 必选槽的出口＝取消整次打出
                });
                _activationSelectionBusy = false;
                GD.Print($"[ArenaOl] 多目标槽出口回包：{(optional ? "留空该槽" : "取消整次打出")}");
            });
        }

        /// <summary>
        /// 【2026-09 用户口径 JC062/JC021·联机】**"一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消"**：
        /// 服务端把整组候选与上限/下限**一次性**下发（`Kind.FieldMultiPick`，与单机
        /// `MultiTargetSlots.FieldPickPlan` **同源**），本方法用与单机**同一个窗口**
        /// （`TargetSelectionWindow` 的多选模式）呈现，回包 `SelectedUids`（已选卡，有序）＋
        /// `SelectedAreaUids`（已选地区）。
        ///
        /// 客户端只做**展示层约束**（服务端已给上限/下限）：至多 N 张卡、每个玩家场上的角色至多 1 个、
        /// 地区至多 1 个且"必选时没选不能确认"、"选了地区后该地区内的角色不可选"（＝禁止原地移动）；
        /// **合法性由服务端 `MultiTargetSlots.Validate` 权威复检**（客户端不重算规则）。
        /// </summary>
        private bool OpenOnlineFieldMultiPickWindow(Script.Net.ChoiceRequest req)
        {
            EnsureTargetWindow();
            if (_onlineTargetWindow == null)
            {
                GD.PrintErr("[ArenaOl] FieldMultiPick：选目标窗不可用 → 退回通用决策面板（按钮式）");
                return false;
            }
            _onlineMultiPickRequest = req;
            _onlineMultiPickActive = true;
            _onlineMultiPickCards.Clear();
            _onlineMultiPickAreas.Clear();
            _onlineMultiPickCardUids.Clear();
            foreach (var u in req.OptionUids ?? new List<string>())
                if (!string.IsNullOrEmpty(u)) _onlineMultiPickCardUids.Add(u);
            _onlineMultiPickAreaUids.Clear();
            foreach (var u in req.OptionAreaUids ?? new List<string>())
                if (!string.IsNullOrEmpty(u)) _onlineMultiPickAreaUids.Add(u);

            _activationSelectionBusy = true;      // 决策期间本地模态（并延后快照应用，避免重建打断窗口）
            _activationTargetActive = false;      // 多选不是"点一下即完成"，用独立状态机
            _activationTargetSource = null;
            _activationTargetAbility = null;
            _pendingPlayCardUid = null;
            _pendingChoiceTargetRequest = req;

            _onlineTargetWindow.OnTargetPicked = null;
            _onlineTargetWindow.OnExtra = null;
            _onlineTargetWindow.OnCancelled = () =>
            {
                var captured = _onlineMultiPickRequest;
                ClearOnlineMultiPick();
                if (captured == null) return;
                _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                {
                    RequestId = captured.RequestId,
                    Cancelled = true,
                });
                GD.Print("[ArenaOl] 【一个窗多选】取消 → 回包 Cancelled（不打出、不扣费）");
            };
            _onlineTargetWindow.OnMultiConfirmed = () =>
            {
                var captured = _onlineMultiPickRequest;
                if (captured == null) return;
                if (!OnlineMultiPickCanConfirm())
                {
                    GD.Print("[ArenaOl] 【一个窗多选】当前不满足确认条件 → 忽略确认键");
                    return;
                }
                var res = new Script.Net.ChoiceResult { RequestId = captured.RequestId, Confirmed = true };
                res.SelectedUids.AddRange(_onlineMultiPickCards);
                res.SelectedAreaUids.AddRange(_onlineMultiPickAreas);
                ClearOnlineMultiPick();
                _matchClient?.SendChoiceResult(res);
                GD.Print($"[ArenaOl] 【一个窗多选】确认 → 回包：卡 {res.SelectedUids.Count} 张、地区 {res.SelectedAreaUids.Count} 个");
            };
            _onlineTargetWindow.SetBlocking(false);   // 点击穿透到场上卡/地区
            _onlineTargetWindow.OpenMultiPick("左键选择右键取消");
            RefreshOnlineMultiPick();
            return true;
        }

        /// <summary>多选：按当前选择重算"还能点谁 + 高亮 + 标题 + 确认可用性"（与单机 `RefreshMultiPick` 同口径）。</summary>
        private void RefreshOnlineMultiPick()
        {
            var req = _onlineMultiPickRequest;
            if (req == null || _onlineTargetWindow == null) return;

            // 【2026-09 用户口径 JC021】**已选地区后必须取消"位于该地区内的角色"的选择**（禁止原地移动）：
            // 用户口径："JC021选择地区后需要取消本地区内的角色选择"。单机由 `FieldPickPlan`（候选收缩 ⇒
            // 协调器剔除失效选择）天然满足；联机客户端没有引擎计划，故在此按"已选地区 + 卡的 ZoneDetailId"自行剔除。
            int pruned = _onlineMultiPickCards.RemoveAll(u => OnlineMultiPickIsInChosenArea(u));
            if (pruned > 0)
                GD.Print($"[ArenaOl] 【一个窗多选】选了地区后自动取消 {pruned} 个位于该地区内的角色选择（禁止原地移动）");

            // 卡候选：服务端候选 − 已选 −（已选地区时）该地区内的角色（禁止原地移动）
            _onlineMultiPickCandCards.Clear();
            if (_onlineMultiPickCards.Count < req.MultiMaxCards)
                foreach (var u in _onlineMultiPickCardUids)
                    if (!_onlineMultiPickCards.Contains(u) && !OnlineMultiPickIsInChosenArea(u))
                        _onlineMultiPickCandCards.Add(u);
            // 地区候选：地区槽未选（上限 1）时全部候选
            _onlineMultiPickCandAreas.Clear();
            if (req.MultiMaxAreas > 0 && _onlineMultiPickAreas.Count < req.MultiMaxAreas)
                _onlineMultiPickCandAreas.AddRange(_onlineMultiPickAreaUids);

            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                // 高亮＝**可点候选 ∪ 已选中**（选中的卡继续保持"亮着"，否则玩家会以为选择丢了）
                bool on = _onlineMultiPickCandCards.Contains(ui.Instance.UID)
                          || _onlineMultiPickCards.Contains(ui.Instance.UID);
                ui.SetTargetHighlight(on);
            }
            HighlightOnlineCouncilTargets(false);   // 本形状的候选只有争夺区卡与地区（不涉及秘社）
            _activationLegalCardUids.Clear();
            foreach (var u in _onlineMultiPickCandCards) _activationLegalCardUids.Add(u);
            _activationLegalAreaUids.Clear();
            foreach (var u in _onlineMultiPickCandAreas) _activationLegalAreaUids.Add(u);
            foreach (var u in _onlineMultiPickAreas) _activationLegalAreaUids.Add(u);   // 已选地区也要能右键取消
            ApplyOnlineAreaHighlights();
            // 多选里地区点击是"切换选择"，不是"点完即回包" ⇒ 覆盖上面的单击回包回调；
            // `SetSelected` 用**另一个通道**（`Modulate`）强调"已选中"，与"可选"黄色高亮区分。
            if (_areaSlots != null)
                foreach (var slot in _areaSlots)
                {
                    if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                    bool candidate = _onlineMultiPickCandAreas.Contains(slot.AreaUID);
                    bool selected = _onlineMultiPickAreas.Contains(slot.AreaUID);
                    slot.SetSelected(selected);
                    if (candidate || selected) slot.SetHighlight(true);
                    if (!candidate && !selected) continue;
                    slot.OnAreaClicked = candidate
                        ? new Action<string>(uid => ToggleOnlineMultiPickArea(uid, rightClick: false))
                        : null;
                    slot.OnAreaRightClicked = new Action<string>(uid => ToggleOnlineMultiPickArea(uid, rightClick: true));
                }

            string cardLabel = string.IsNullOrEmpty(req.MultiCardLabel) ? "目标角色" : req.MultiCardLabel;
            string areaLabel = string.IsNullOrEmpty(req.MultiAreaLabel) ? "目标地区" : req.MultiAreaLabel;
            string title = req.MultiMaxAreas > 0
                ? $"请选择{cardLabel}（{_onlineMultiPickCards.Count}/{req.MultiMaxCards}）和{areaLabel}（{_onlineMultiPickAreas.Count}/{req.MultiMaxAreas}）"
                : $"请选择{cardLabel}：已选择（{_onlineMultiPickCards.Count}/{req.MultiMaxCards}）";
            bool canOk = OnlineMultiPickCanConfirm();
            _onlineTargetWindow.SetMultiPick("左键选择右键取消", title, canOk);
            GD.Print($"[ArenaOl] 【一个窗多选】{title}｜可点卡 {_onlineMultiPickCandCards.Count}、可点地区 {_onlineMultiPickCandAreas.Count}，可确认={canOk}");
        }

        /// <summary>多选："确认"是否可用（至少 N 张卡；地区必选时必须已选地区）。</summary>
        private bool OnlineMultiPickCanConfirm()
        {
            var req = _onlineMultiPickRequest;
            if (req == null) return false;
            return _onlineMultiPickCards.Count >= req.MultiMinCards
                   && _onlineMultiPickCards.Count <= req.MultiMaxCards
                   && _onlineMultiPickAreas.Count <= System.Math.Max(0, req.MultiMaxAreas)
                   && (!req.MultiAreaRequired || _onlineMultiPickAreas.Count >= 1);
        }

        /// <summary>该卡是否位于"已选中的地区"内（FAQ：不能原地移动 ⇒ 选了地区后该地区内角色不可选）。</summary>
        private bool OnlineMultiPickIsInChosenArea(string cardUid)
        {
            if (_onlineMultiPickAreas.Count == 0 || string.IsNullOrEmpty(cardUid)) return false;
            var inst = _matchClient?.ClientCardManager?.GetInstance(cardUid);
            if (inst == null) return false;
            return _onlineMultiPickAreas.Contains(inst.ZoneDetailId);
        }

        /// <summary>多选：左键＝加选（受"至多 N 张"与"每个玩家场上的角色至多 1 个"约束）；右键＝取消该选择。</summary>
        private void ToggleOnlineMultiPickCard(string uid, bool rightClick)
        {
            if (string.IsNullOrEmpty(uid)) return;
            if (rightClick)
            {
                if (_onlineMultiPickCards.Remove(uid)) RefreshOnlineMultiPick();
                return;
            }
            if (!_onlineMultiPickCandCards.Contains(uid)) return;
            if (_onlineMultiPickRequest?.MultiSameControllerOnce == true)
            {
                var inst = _matchClient?.ClientCardManager?.GetInstance(uid);
                if (inst != null)
                    foreach (var picked in _onlineMultiPickCards)
                    {
                        var p = _matchClient?.ClientCardManager?.GetInstance(picked);
                        if (p != null && p.ControllerId == inst.ControllerId)
                        {
                            GD.Print($"[ArenaOl] 【一个窗多选】每个玩家场上的角色只能选一个 → 忽略 {uid}");
                            return;
                        }
                    }
            }
            _onlineMultiPickCards.Add(uid);
            RefreshOnlineMultiPick();
        }

        /// <summary>多选：左键点地区＝选择/换选（至多 1 个）；右键＝取消该地区（**已选中的也要能取消**）。</summary>
        private void ToggleOnlineMultiPickArea(string areaUid, bool rightClick)
        {
            if (string.IsNullOrEmpty(areaUid)) return;
            if (rightClick)
            {
                // 先处理取消，再判候选（否则"已选中后不再是候选"会让右键失效 —— 用户实测"无法右键取消地区选择"）
                if (_onlineMultiPickAreas.Remove(areaUid)) RefreshOnlineMultiPick();
                return;
            }
            if (!_onlineMultiPickCandAreas.Contains(areaUid)) return;
            var req = _onlineMultiPickRequest;
            if (req == null || req.MultiMaxAreas <= 0) return;
            if (_onlineMultiPickAreas.Count >= req.MultiMaxAreas) _onlineMultiPickAreas.Clear();   // 上限即"一个地区" ⇒ 直接换选
            _onlineMultiPickAreas.Add(areaUid);
            RefreshOnlineMultiPick();
        }

        /// <summary>多选收尾：清状态/清高亮/关窗/解除本地模态（回包前调用，避免复用时残留）。</summary>
        private void ClearOnlineMultiPick()
        {
            _onlineMultiPickActive = false;
            _onlineMultiPickRequest = null;
            _onlineMultiPickCards.Clear();
            _onlineMultiPickAreas.Clear();
            _onlineMultiPickCandCards.Clear();
            _onlineMultiPickCandAreas.Clear();
            _onlineMultiPickCardUids.Clear();
            _onlineMultiPickAreaUids.Clear();
            _pendingChoiceTargetRequest = null;
            ClearActivationTargetSelection();
        }

        /// <summary>服务端选目标回包：把选中 UID 封成 `ChoiceResult` 上行（对齐单机选目标不可取消）。</summary>
        private void OnServerChoiceTargetPicked(string targetUid)        {
            var req = _pendingChoiceTargetRequest;
            ClearActivationTargetSelection();
            _pendingChoiceTargetRequest = null;
            if (string.IsNullOrEmpty(targetUid) || req == null) return;
            _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
            {
                RequestId = req.RequestId,
                SelectedUid = targetUid,
                Cancelled = false,
            });
            GD.Print($"[ArenaOl] 触发选目标回包：{targetUid}");
        }

        /// <summary>
        /// 触发排序（对齐单机 `TriggerOrderWindow`）：拖拽决定入堆叠顺序 + 取消（放弃区）/
        /// 第一张进入堆叠 / 完成排序并放弃下方效果（＝放弃剩余）。联机专用窗口 `OnlineTriggerOrderWindow`（复用共享的
        /// `TriggerEntryUI`/`TriggerOrderRoot` 纯 View 组件，不改它们）。
        ///
        /// 逐次入堆叠会产生**多轮请求-响应**：同一 `OrderContextId` 复用同一窗口并刷新条目；上下文变化则重建窗口。
        /// **本轮请求的 OptionUids ＝ 服务端侧"当前全部剩余待排序触发"**，故收到同 ctx 请求即刷新窗口、
        /// 且证明排序未结束（绝不在本方法内关窗）。
        /// 回包：`SelectedIndices` = 本次入堆叠的条目下标顺序（服务端据此按序入栈）；"完成排序"= `Cancelled=true`。
        /// 窗口的自动关闭由窗口内部"双框全空"判定触发（静默关闭、不回包）；
        /// 若窗口已被静默关闭而服务端仍下发同 ctx 请求，本方法"窗口为空则新建"分支会**重新弹出**并刷新（防御兜底）。
        /// </summary>
        private bool OpenOnlineTriggerOrderWindow(Script.Net.ChoiceRequest req)
        {
            string ctx = req.OrderContextId ?? "";
            if (_onlineTriggerOrderWindow != null && GodotObject.IsInstanceValid(_onlineTriggerOrderWindow)
                && !string.Equals(_triggerOrderContextId, ctx, StringComparison.Ordinal))
            {
                _onlineTriggerOrderWindow.CloseAndFree();   // 新的排序轮次（如换 owner）→ 重建窗口
                _onlineTriggerOrderWindow = null;
                _onlineTriggerOrderCurrentRequest = null;
            }
            // 【关键·不可在此关窗】收到同 OrderContextId 的排序请求，恰恰**证明整轮排序尚未结束**
            // （服务端"逐次入堆叠"会对同一 owner 复用同一 ctx 连发多轮请求，每轮 OptionUids = 服务器侧
            //  **当前全部剩余待排序触发**）。故此处只允许走下方 OpenOrUpdate 刷新窗口，**绝不能**关窗——
            //  否则「第一张进入堆叠」后（尤其该触发需选目标、服务端 await 选目标后才移除并下发下一轮）
            //  会把"还有剩余触发的正常下一轮请求"误判为"排序已结束"而误关窗、误弃后续触发。
            // 【重开兜底】若窗口此前已被"双框全空"静默关闭（`_onlineTriggerOrderWindow == null`），
            // 本分支会走下方"窗口为空 → 新建 + OpenOrUpdate(Popup)"重新弹出并刷新剩余条目。
            if (_onlineTriggerOrderWindow == null || !GodotObject.IsInstanceValid(_onlineTriggerOrderWindow))
            {
                _triggerOrderContextId = ctx;
                _onlineTriggerOrderWindow = LoadWindowScene<OnlineTriggerOrderWindow>(
                    ref _triggerOrderWindowScene, "res://Scenes/Net/OnlineTriggerOrderWindow.tscn");
                if (_onlineTriggerOrderWindow == null)
                {
                    GD.PrintErr("[ArenaOl] OnlineTriggerOrderWindow.tscn 加载失败，退回通用决策面板");
                    return false;
                }
                AddChild(_onlineTriggerOrderWindow);
                // 回包必须用「当前这一轮」的 RequestId（同 ctx 会连发多轮请求，各轮 RequestId 不同；
                // 若沿用首轮捕获的 id，后续轮的入堆叠/完成回包会挂到已完成的旧请求上 → 服务端收不到，卡死）。
                _onlineTriggerOrderWindow.OnSubmitIndices = indices =>
                {
                    var current = _onlineTriggerOrderCurrentRequest;
                    if (current == null) { GD.PrintErr("[ArenaOl] 触发排序入堆叠回包：无当前请求，丢弃"); return; }
                    var res = new Script.Net.ChoiceResult
                    {
                        RequestId = current.RequestId,
                        Cancelled = false,
                    };
                    if (indices != null) res.SelectedIndices.AddRange(indices);
                    _matchClient?.SendChoiceResult(res);
                    GD.Print($"[ArenaOl] 触发排序入堆叠已回包：{res.SelectedIndices.Count} 个（顺序 {string.Join(",", res.SelectedIndices)}）");
                };
                _onlineTriggerOrderWindow.OnDone = () =>
                {
                    var current = _onlineTriggerOrderCurrentRequest;
                    if (current != null)
                    {
                        _matchClient?.SendChoiceResult(new Script.Net.ChoiceResult
                        {
                            RequestId = current.RequestId,
                            Cancelled = true,
                        });
                    }
                    GD.Print("[ArenaOl] 触发排序完成（放弃剩余）已回包");
                    // 对齐单机 Done 关闭窗口：本轮排序结束（若还有其它 owner，服务端会下发新的上下文 id 开新窗口）
                    if (_onlineTriggerOrderWindow != null && GodotObject.IsInstanceValid(_onlineTriggerOrderWindow))
                        _onlineTriggerOrderWindow.CloseAndFree();
                    _onlineTriggerOrderWindow = null;
                    _onlineTriggerOrderCurrentRequest = null;
                    _activationSelectionBusy = false;
                };
            }

            // 记录本轮请求：供 OnSubmitIndices / OnDone 回包使用正确的 RequestId。
            _onlineTriggerOrderCurrentRequest = req;
            _onlineTriggerOrderWindow.OpenOrUpdate(ctx, req.PlayerId, BuildOnlineTriggerEntries(req));
            _activationSelectionBusy = true;   // 决策期间本地模态（并延后快照应用，避免重建打断窗口）
            GD.Print($"[ArenaOl] 触发排序窗口已打开/刷新（ctx={ctx}，条目 {(req.OptionUids?.Count ?? 0)} 个）");
            return true;
        }

        /// <summary>把排序请求的候选转成窗口条目（名称/效果文本/强制标记来自请求，卡图由来源卡模板解析）。</summary>
        private List<OnlineTriggerEntry> BuildOnlineTriggerEntries(Script.Net.ChoiceRequest req)
        {
            var list = new List<OnlineTriggerEntry>();
            var uids = req.OptionUids ?? new List<string>();
            for (int i = 0; i < uids.Count; i++)
            {
                string uid = uids[i];
                if (string.IsNullOrEmpty(uid)) continue;
                string name = (req.OptionLabels != null && i < req.OptionLabels.Count) ? req.OptionLabels[i] : null;
                if (string.IsNullOrEmpty(name)) name = ResolveChoiceLabel(uid);
                string text = (req.OptionTexts != null && i < req.OptionTexts.Count) ? req.OptionTexts[i] : "";
                bool mandatory = req.OptionForced != null && i < req.OptionForced.Count && req.OptionForced[i];
                list.Add(new OnlineTriggerEntry
                {
                    OptionKey = uid,
                    Name = name,
                    EffectText = text,
                    Mandatory = mandatory,
                    SourceTemplate = ResolvePlayerTemplateByUid(uid),
                });
            }
            return list;
        }

        /// <summary>按 UID 取投影世界里的玩家卡模板（卡图显示用；取不到返回 null，条目只显示文字）。</summary>
        private Script.Data.Cards.PlayerCardData ResolvePlayerTemplateByUid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            var inst = _matchClient?.ClientCardManager?.GetInstance(uid) ?? FindInstanceInAllZones(uid);
            return inst?.PlayerTemplate as Script.Data.Cards.PlayerCardData;
        }

        /// <summary>该 UID 是否为本局已知的地区（用于把 SelectTarget 的候选分流成"地区目标"）。</summary>
        private bool IsKnownAreaUid(string uid)
        {
            if (string.IsNullOrEmpty(uid) || _areaSlots == null) return false;
            foreach (var slot in _areaSlots)
                if (slot != null && GodotObject.IsInstanceValid(slot) && slot.AreaUID == uid) return true;
            return false;
        }

        /// <summary>
        /// 高亮"可被选为目标的地区槽位"（对齐单机 `TriggerInteractionCoordinator.RequestTargetSelection` 的
        /// `_targetIsArea` 分支）：`SetHighlight(true)` + `IsTargetSelectable = true` + `OnAreaClicked` 回调；
        /// 点击由 `AreaSlot._GuiInput` 在 `IsTargetSelectable` 时触发（单机同款公开 API，只读复用不改其类）。
        /// </summary>
        private void ApplyOnlineAreaHighlights()
        {
            if (_areaSlots == null) return;
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                bool selectable = _activationLegalAreaUids.Contains(slot.AreaUID);
                slot.IsTargetSelectable = selectable;
                slot.SetHighlight(selectable);
                slot.OnAreaClicked = selectable ? new Action<string>(OnServerChoiceTargetPicked) : null;
            }
        }

        /// <summary>清除地区槽位的高亮/可选状态（选目标结束或取消时调用）。</summary>
        private void ClearOnlineAreaHighlights()
        {
            if (_areaSlots == null) return;
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                slot.IsTargetSelectable = false;
                slot.SetHighlight(false);
                slot.SetSelected(false);
                slot.OnAreaClicked = null;
                // 【2026-09·JC021】右键回调也要清（否则窗口复用/收尾后残留旧回调）
                slot.OnAreaRightClicked = null;
            }
        }

        /// <summary>【2026-09·JC051 用户口径】联机卡 UI 的**统一 Setup 收口**：先注入客户端投影
        /// `PlayerManager`（卡面势力走 `AreaStatsCalculator.ValueOf`，与地区合计逐卡同函数），再 `Setup`。
        /// 只为避免"某条创建路径漏注入"（本会话反复踩过同类漏口）。</summary>
        private CardUI SetupCardUI(CardUI ui, CardInstance inst)
        {
            ui?.SetStatsDependencies(_matchClient?.ClientPlayerManager);
            ui?.Setup(inst);
            return ui;
        }

        /// <summary>按实例找已渲染的 CardUI（清分配显示/命中判定用）。</summary>
        /// <summary>【2026-09 修复·A101】联机卡 UI 的**统一登记收口**：写入 `_cardUIMap` 的同时**幂等订阅**双击激活。
        /// 原先有 6 处 `_cardUIMap[inst.UID] = ui;`，但只有 3 处顺手订阅了 `AttachmentDoubleClicked`
        /// ⇒ 经其余路径建出的卡 UI **双击无任何接收者**（`EmitSignal` 静默丢弃、零日志）＝用户实测
        /// "JC053 有高亮、双击完全没有反馈"（与单机侧 A89 同一类缺陷：订阅必须挂"唯一收口"）。</summary>
        private void RegisterOnlineCardUI(CardInstance inst, CardUI ui)
        {
            if (inst == null || ui == null) return;
            _cardUIMap[inst.UID] = ui;
            ui.AttachmentDoubleClicked -= OnAttachmentDoubleClicked;   // 幂等：防重复订阅
            ui.AttachmentDoubleClicked += OnAttachmentDoubleClicked;
        }

        private CardUI FindCardUiByInstance(CardInstance inst)
        {
            if (inst == null) return null;
            return _cardUIMap.TryGetValue(inst.UID, out var ui) && ui != null && GodotObject.IsInstanceValid(ui) ? ui : null;
        }

        /// <summary>服务端拒绝意图（IntentResult.Success=false）：弹出可见提示（联机提示窗，点窗外不关闭）。
        /// 例如激活目标非法、缺优先权、费用不足等——避免玩家"点了没反应"。</summary>
        private void OnClientIntentRejected(Script.Net.IntentResult result)
        {
            if (result == null) return;
            if (_noticeWindow != null && GodotObject.IsInstanceValid(_noticeWindow)) return;   // 同屏只保留一个提示
            _noticeWindowOpen = true;
            _noticeWindow = new OnlineNoticeWindow();
            AddChild(_noticeWindow);
            _noticeWindow.OnClosed = () => { _noticeWindowOpen = false; };
            _noticeWindow.Init(string.IsNullOrEmpty(result.Error)
                ? "操作被服务端拒绝。"
                : $"操作被服务端拒绝：{result.Error}");
            _noticeWindow.PopupCentered();
        }

        /// <summary>把决策请求中的 UID 解析成可读文字（卡名/地区名/触发名）。</summary>
        private string ResolveChoiceLabel(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return "（未命名）";
            // 玩家卡（手牌/争夺区/附属等）
            var inst = _matchClient?.ClientZoneManager != null ? FindInstanceInAllZones(uid) : null;
            if (inst != null)
            {
                if (inst.PlayerTemplate != null) return inst.PlayerTemplate.CardName ?? uid;
                if (inst.AreaTemplate != null) return inst.AreaTemplate.CardName ?? uid;
                if (inst.CouncilTemplate != null) return inst.CouncilTemplate.CardName ?? uid;
            }
            // 地区实例（独立 CardManager 中的地区卡）
            var area = _matchClient?.ClientAreaManager?.GetOrderedAreaInstances()?.Find(a => a != null && a.UID == uid);
            if (area?.AreaTemplate != null) return area.AreaTemplate.CardName ?? uid;
            return uid;
        }

        /// <summary>在客户端投影 ZoneManager 的全部区域桶里按 UID 查卡实例。</summary>
        private Script.Game.Entities.CardInstance FindInstanceInAllZones(string uid)
        {
            var zone = _matchClient?.ClientZoneManager;
            if (zone == null) return null;
            foreach (var z in new[] { CardZone.Hand, CardZone.Graveyard, CardZone.AssetZone, CardZone.AreaZone, CardZone.AttachedZone, CardZone.SealedZone, CardZone.CouncilZone, CardZone.ScoreZone, CardZone.Stack })
            {
                foreach (var inst in zone.GetZoneCards(z, 0))
                    if (inst != null && inst.UID == uid) return inst;
                for (int p = 1; p <= 2; p++)
                    foreach (var inst in zone.GetZoneCards(z, p))
                        if (inst != null && inst.UID == uid) return inst;
            }
            return null;
        }

        // ==================== 快照渲染（全量重建） ====================
        private void ApplySnapshot(GameSnapshot snap)
        {
            if (_matchClient == null) return;
            ClearAllUI();

            var zone = _matchClient.ClientZoneManager;
            if (zone != null)
            {
                // 手牌/墓地/资产区：ZoneManager 投影区域桶（玩家卡，PlayerTemplate != null）
                RebuildInto(_p1Hand, zone.GetZoneCards(CardZone.Hand, 1));
                RebuildInto(_p2Hand, zone.GetZoneCards(CardZone.Hand, 2));
                RebuildGraveyards(zone.GetZoneCards(CardZone.Graveyard, 1), zone.GetZoneCards(CardZone.Graveyard, 2));
                RebuildAssets(zone.GetZoneCards(CardZone.AssetZone, 1), zone.GetZoneCards(CardZone.AssetZone, 2));
            }

            // 牌库：数据在独立投影 DeckManager（ZoneManager.GetZoneCards(Deck) 恒空），从 ClientDeckManager 取
            RebuildDecks();

            // 场上地区：数据在独立投影 AreaManager（不在 ZoneManager 的 AreaZone 桶），从 ClientAreaManager 取
            RebuildAreas();

            // 争夺区角色/暗藏卡：ZoneManager 的 AreaZone 桶，按地区 + 控制者归属到对应 AreaSlot 的争夺区
            if (zone != null)
                RebuildAreaZoneCards(zone.GetZoneCards(CardZone.AreaZone, 0));

            // P2：秘社区 / 附属封印区 / 分数区 / 堆叠 / 势力标记（与单机 Arena 对齐）
            RebuildCouncils();
            RebuildAttachmentsAndSeals();
            RebuildScoreZone(snap);
            RefreshStackUI();
            RefreshAreaMarks();

            RebuildWorldDeck();

            var turn = _matchClient.ClientTurnManager;
            if (_phaseLabel != null && turn != null)
            {
                // 四行文案（回合/先手/步骤/行动权）唯一口径在 TurnManager.TurnStatusText（与单机 Arena 同口径）；
                // 对抗步骤的地区前缀由服务端快照 CombatAreaNo 投影驱动（客户端无权威地区游标）。
                _phaseLabel.Text = turn.TurnStatusText;
            }

            // 分数显示：只显示胜利分数（费用池已移到 PlayerXArea/AssetContainer/AssetLabel）
            if (_p1ScoreLabel != null && snap.Players.Count > 0)
                _p1ScoreLabel.Text = $"P1 分数 {snap.Players[0].ScoreTotal}";
            if (_p2ScoreLabel != null && snap.Players.Count > 1)
                _p2ScoreLabel.Text = $"P2 分数 {snap.Players[1].ScoreTotal}";

            // 费用池（对标单机 Arena.RefreshAssetLabels）：费用：可用/总 + 颜色忠诚 + 领域忠诚
            RefreshOnlineAssetLabels(snap);

            // 游戏结束展示：由 `ShowMatchResult`（服务端房间广播的权威结果）驱动；
            // 快照里的 `IsGameOver` 是"对局已结束"的**全局**标志（双方都为 true），不能用来推断胜者。
            if (_resultLabel != null) _resultLabel.Visible = _matchResultShown;

            ArrangeAll();

            // 【2026-10 修复·横置卡偏移】ArrangeAll() 内的 ArrangeAreas() 会**再次写入槽位 Position/Size**，
            // 而卡片是槽位的子孙 ⇒ 全局坐标随之改变，排版瞬间写入的目标值即成为陈旧值
            // （联机独有：单机 ArrangeAreas 只在建/删区调用，排版路径不碰槽位）。
            // 因此在全部排版结束后统一自锁一次平滑目标，槽位后移、卡片旋转都不会被 _Process 拉偏。
            SyncSmoothTargets();

            // 诊断日志：战场重建完成（用于排查"进入对战后没有卡牌"这类问题——能从日志直接看到手牌/场上数量）
            GD.Print($"[ArenaOl] 战场已按快照重建：手牌 {_p1Hand?.GetChildCount() ?? 0}/{_p2Hand?.GetChildCount() ?? 0}"
                + $"，争夺区 {CountAllAreaCards()} 张，地区 {_areaSlots.Count} 个，堆叠 {_stackContainer?.GetChildCount() ?? 0}");
            LogTappedCardSample();

            // 快照即权威状态：每次全量重建后刷新「可发动」高亮（联机无本地优先权变更事件）
            RefreshActivateHighlights();

            // 【2026-09 修复·用户实测"地区没有正常高亮 / 选目标高亮消失"】**重建后必须补回"本地选目标"高亮**：
            // `ClearAllUI()` 会把所有卡/地区的高亮与"已选中"态一并清掉，而快照延迟策略有 1.5s 上限
            // （`SnapshotMaxDeferSeconds`）⇒ 玩家在选择窗里稍微多想一会儿，快照就会强制应用并把高亮抹掉
            // （表现为"地区没有正常高亮"，且点选目标的手感全丢）。故按当前本地交互状态**重新施加**一次。
            ReapplyLocalSelectionHighlights();

            // 【第0回合·调度】进入第 0 回合且尚未上报 → 本地弹出调度框（客户端驱动，见 EnsureMulliganPrompt）
            EnsureMulliganPrompt();
        }

        /// <summary>
        /// 【2026-09】快照全量重建后，按当前本地交互状态**补回**选目标高亮与"已选中"态：
        ///  · 整窗多选（JC062/JC021）→ `RefreshOnlineMultiPick()`（重算候选 + 已选中视觉 + 窗面计数）；
        ///  · 单选取目标（触发/事务卡）→ 重新高亮 `_activationLegalCardUids` 与地区槽。
        /// </summary>
        private void ReapplyLocalSelectionHighlights()
        {
            if (_onlineMultiPickActive)
            {
                RefreshOnlineMultiPick();
                return;
            }
            if (!_activationTargetActive) return;
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (_activationLegalCardUids.Contains(ui.Instance.UID)) ui.SetTargetHighlight(true);
            }
            HighlightOnlineCouncilTargets(true);
            ApplyOnlineAreaHighlights();
            GD.Print($"[ArenaOl] 快照重建后已补回选目标高亮（卡 {_activationLegalCardUids.Count}、地区 {_activationLegalAreaUids.Count}）");
        }

        /// <summary>
        /// 联机费用池（对标单机 `Arena.RefreshOneAssetLabel`）：写入 `PlayerXArea/AssetContainer/AssetLabel` 三行
        /// （费用：可用/总 + 颜色忠诚 + 领域忠诚）。
        /// - 可用费用取快照权威值 `PlayerSnapshot.AvailableMana`（含 K19 临时费用；客户端重算只数未横置资产，会少算）；
        /// - 总费用＝客户端投影资产区张数；
        /// - 忠诚由客户端投影 `PlayerManager`（已注入 `ClientZoneManager`）按资产区实时计算，口径与单机一致。
        /// </summary>
        private void RefreshOnlineAssetLabels(GameSnapshot snap)
        {
            var zone = _matchClient?.ClientZoneManager;
            var pm = _matchClient?.ClientPlayerManager;
            if (zone == null || pm == null) return;

            RefreshOneOnlineAssetLabel(_assetLabel1, 1, snap, zone, pm);
            RefreshOneOnlineAssetLabel(_assetLabel2, 2, snap, zone, pm);
        }

        private void RefreshOneOnlineAssetLabel(Label label, int playerId, GameSnapshot snap,
            ZoneManager zone, PlayerManager pm)
        {
            if (label == null) return;

            int available = pm.GetAvailableMana(playerId);
            if (snap?.Players != null)
            {
                var psnap = snap.Players.Find(p => p != null && p.PlayerId == playerId);
                if (psnap != null) available = psnap.AvailableMana;   // 快照权威值优先（含临时费用）
            }
            int total = zone.GetZoneCards(CardZone.AssetZone, playerId).Count;
            label.Text = Arena.FormatCostPoolText(available, total,
                pm.GetColorLoyalty(playerId), pm.GetDomainLoyalty(playerId));
        }

        /// <summary>争夺区已渲染卡数（诊断日志用）。</summary>
        private int CountAllAreaCards()
        {
            int n = 0;
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                n += slot.Player1Area?.GetChildCount() ?? 0;
                n += slot.Player2Area?.GetChildCount() ?? 0;
            }
            return n;
        }

        /// <summary>
        /// 联机「可发动」高亮：**优先读服务端下发的合法性投影**（`GameViewSnap.ActivatableUids`，
        /// 由服务端用 `ActivateEligibility.CanActivate` 算出），客户端不再自行拼输入判定；
        /// 无投影（旧服务端/测试路径）时退回调用**同一份规则函数**本地求值。
        /// 两条路都指向同一实现，配合 P0/P1 的输入补齐（本局 PlayerManager / 静止标记 / 运行时授予已投影），
        /// 客户端与服务端从此同解。
        /// </summary>
        private void RefreshActivateHighlights()
        {
            var zone = _matchClient?.ClientZoneManager;
            var cardMgr = _matchClient?.ClientCardManager;
            var turn = _matchClient?.ClientTurnManager;
            var player = _matchClient?.ClientPlayerManager;
            if (zone == null || cardMgr == null || turn == null || player == null) return;

            var instances = new List<CardInstance>(zone.GetZoneCards(CardZone.AreaZone, 0));
            instances.AddRange(zone.GetZoneCards(CardZone.AttachedZone, 0));
            foreach (var inst in instances)
            {
                if (inst == null || inst.IsHidden) continue;   // 占位实例（信息隔离）不参与可发动判定
                if (!_cardUIMap.TryGetValue(inst.UID, out var ui)) continue;
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                ui.SetActivatableHighlight(IsActivatableForClient(inst));
            }
        }

        /// <summary>服务端为本客户端下发的合法性投影（随最近一次快照；null=未下发）。</summary>
        private Script.Net.GameViewSnap ClientLegalityView => _matchClient?.LastSnapshot?.View;

        /// <summary>某张场上卡此刻是否"可发动"（投影优先，退回同一规则函数）。</summary>
        private bool IsActivatableForClient(CardInstance inst)
        {
            if (inst == null) return false;
            var view = ClientLegalityView;
            if (view != null) return view.ActivatableUids != null && view.ActivatableUids.Contains(inst.UID);
            return ActivateEligibility.CanActivate(inst, _matchClient?.ClientTurnManager,
                _matchClient?.ClientPlayerManager, _matchClient?.ClientCardManager);
        }

        /// <summary>某张卡作为打出目标是否合法（投影优先，退回同一规则函数）。</summary>
        private bool IsLegalPlayTargetForClient(CardInstance src, CardInstance tgt)
        {
            if (src == null || tgt == null) return false;
            var view = ClientLegalityView;
            var entry = view?.PlayTargets?.Find(x => x != null && x.SourceUid == src.UID);
            if (entry != null) return entry.TargetUids.Contains(tgt.UID);
            // 无投影 / 该来源不在服务端手牌投影中（异常竞态）→ 退回调用同一份规则函数本地求值
            return _matchClient?.ClientCardManager?.IsLegalPlayTarget(src, tgt) == true;
        }

        /// <summary>重建双方玩家牌库：用 DeckZone 组件显示卡背堆叠 + 数量（与单机一致）。
        /// 牌库数据在客户端投影 DeckManager（ZoneManager.GetZoneCards(Deck) 恒空）。</summary>
        private void RebuildDecks()
        {
            var deckMgr = _matchClient?.ClientDeckManager;
            if (deckMgr == null) return;
            var d1 = deckMgr.GetDeckPlayer1();
            var d2 = deckMgr.GetDeckPlayer2();
            // DeckZone.Initialize 只清内部列表不清已有子节点，快照全量重建前先清掉旧卡 UI，避免跨快照堆积
            ClearDeckChildren(_p1DeckZone);
            ClearDeckChildren(_p2DeckZone);
            _p1DeckZone?.Initialize(d1, _cardUIScene, 1);
            _p2DeckZone?.Initialize(d2, _cardUIScene, 2);
        }

        /// <summary>清空 DeckZone 节点下的旧 CardUI 子节点（保留 CountLabel/CardContainer 等静态子节点）。</summary>
        private void ClearDeckChildren(OnlineDeckZone deckZone)
        {
            if (deckZone == null) return;
            foreach (Node child in deckZone.GetChildren())
                if (child is CardUI c && GodotObject.IsInstanceValid(c)) c.QueueFree();
        }

        /// <summary>重建双方墓地：用 GraveyardZone 组件显示正面堆叠 + 数量（与单机一致）。</summary>
        private void RebuildGraveyards(System.Collections.Generic.List<CardInstance> grave1, System.Collections.Generic.List<CardInstance> grave2)
        {
            // 先 Clear() 释放上一快照的旧卡节点，再 Initialize 设场景引用，最后逐张 AddCard
            if (_p1GraveZone != null) { _p1GraveZone.Clear(); _p1GraveZone.Initialize(_cardUIScene); }
            if (_p2GraveZone != null) { _p2GraveZone.Clear(); _p2GraveZone.Initialize(_cardUIScene); }
            RebuildGraveyardInto(_p1GraveZone, grave1);
            RebuildGraveyardInto(_p2GraveZone, grave2);
        }

        private void RebuildGraveyardInto(GraveyardZone graveZone, System.Collections.Generic.List<CardInstance> instances)
        {
            if (graveZone == null || instances == null) return;
            foreach (var inst in instances)
            {
                if (inst == null || inst.PlayerTemplate == null || inst.IsHidden) continue;   // 墓地只放玩家卡（占位除外）
                var ui = _cardUIScene.Instantiate<CardUI>();
                SetupCardUI(ui, inst);
                graveZone.AddCard(ui);
            }
        }

        /// <summary>重建双方资产区：把资产卡显示到各自 AssetContainer（横排，费用左上、横置视觉）。
        /// 建立资产后，卡进 AssetZone 桶，由这里渲染，避免"手牌减了但资产区空"。</summary>
        private void RebuildAssets(System.Collections.Generic.List<CardInstance> asset1, System.Collections.Generic.List<CardInstance> asset2)
        {
            RebuildAssetsInto(_assetContainer1, asset1);
            RebuildAssetsInto(_assetContainer2, asset2);
        }

        private void RebuildAssetsInto(Control container, System.Collections.Generic.List<CardInstance> instances)
        {
            if (container == null) return;
            // 清空容器旧卡（立即移除+释放，避免延迟 QueueFree 残留）
            foreach (Node child in container.GetChildren())
                if (child is CardUI oc && GodotObject.IsInstanceValid(oc))
                {
                    container.RemoveChild(oc);
                    oc.QueueFree();
                }
            if (instances == null) return;

            // 收集本容器所有资产卡，统一交共享排版平铺（与单机 ArrangeContainerCards 一致）：
            // 容器宽 400px、卡宽 100、间距 20 → 超 3 张自动折叠重叠，右卡压左卡，不再横向溢出。
            var cards = new System.Collections.Generic.List<Control>();
            foreach (var inst in instances)
            {
                if (inst?.PlayerTemplate == null) continue;
                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                SetupCardUI(ui, inst);
                ui.SetExternalCostMod(ComputeClientCostMod(inst));
                ui.SetTapped(inst.IsTapped);   // 使用费用后横置资产卡：同步服务端 IsTapped
                ui.CanDrag = false;   // 资产区只读展示（联机下后续可扩展"横置/消耗"意图，暂不拖拽）
                RegisterOnlineCardUI(inst, ui);
                cards.Add(ui);
            }
            CardLayoutHelper.LayoutCardsEvenly(container, cards);
        }

        /// <summary>
        /// 重建争夺区角色/暗藏卡：把 AreaZone 桶的卡按所在地区(ZoneDetailId) + 控制者(ControllerId)
        /// 归属到对应 AreaSlot 的争夺区容器（P1→Player1Area，P2→Player2Area），
        /// 同步正背面(暗藏卡背面)与横置状态，并用共享排版均匀平铺。
        /// 解决"派遣暗藏/出牌到争夺区后 UI 无体现"。
        /// </summary>
        private void RebuildAreaZoneCards(System.Collections.Generic.List<CardInstance> areaZoneCards)
        {
            // 0) 清空争夺区角色 UI 登记（供 RebuildAttachmentsAndSeals 的统一平铺序列使用）
            _areaRoleGroups.Clear();

            // 1) 清空每个 AreaSlot 争夺区容器的旧角色卡 UI
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                ClearContainerCards(slot.Player1Area);
                ClearContainerCards(slot.Player2Area);
            }

            if (areaZoneCards == null) return;

            // 2) 按 (地区UID, 容器) 分组，方便每容器均匀平铺
            var groups = new Dictionary<Control, System.Collections.Generic.List<Control>>();
            foreach (var inst in areaZoneCards)
            {
                if (inst?.PlayerTemplate == null) continue;   // 只渲染玩家卡（角色/暗藏者）
                if (string.IsNullOrEmpty(inst.ZoneDetailId)) continue;
                var slot = _areaSlots.Find(s => s != null && s.AreaUID == inst.ZoneDetailId);
                if (slot == null) continue;
                var container = inst.ControllerId == 1 ? slot.Player1Area : slot.Player2Area;
                if (container == null) continue;

                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                SetupCardUI(ui, inst);
                ui.SetExternalCostMod(ComputeClientCostMod(inst));
                ui.SetFace(!inst.IsCovert);      // 暗藏卡(IsCovert)显示背面，普通角色正面
                ui.SetTapped(inst.IsTapped);     // 同步横置（使用费用/攻击后）
                ui.CanDrag = false;              // 争夺区只读展示（联机下后续再接入攻击/横置意图）
                RegisterOnlineCardUI(inst, ui);
                // 剩余交互：暗藏双击现身 / 附属双击激活（上行意图，服务端权威裁决）
                ui.CovertRevealRequested += OnCovertRevealRequested;

                if (!groups.TryGetValue(container, out var list)) { list = new System.Collections.Generic.List<Control>(); groups[container] = list; }
                list.Add(ui);
            }

            // 3) 各容器均匀平铺（含横置旋转视觉宽度）
            foreach (var kv in groups)
                CardLayoutHelper.LayoutCardsEvenly(kv.Key, kv.Value);

            // 4) 登记"争夺区角色 UI 全集"，供步骤 RebuildAttachmentsAndSeals 的统一平铺序列使用
            //    （含形态角色：CurrentZone==AreaZone 但 IsAttached==true，绝不能被过滤掉）
            foreach (var kv in groups)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                _areaRoleGroups[kv.Key] = kv.Value;
            }
        }

        /// <summary>清空容器下的 CardUI 子节点（立即移除+释放，避免延迟 QueueFree 残留）。</summary>
        private void ClearContainerCards(Control container)
        {
            if (container == null) return;
            foreach (Node child in container.GetChildren())
                if (child is CardUI oc && GodotObject.IsInstanceValid(oc))
                {
                    container.RemoveChild(oc);
                    oc.QueueFree();
                }
        }

        // ==================== P2：四区域 UI / 堆叠 / 势力标记 / 剩余交互 ====================

        /// <summary>重建双方秘社区（CouncilZone）：用 CouncilCardUI 显示秘社卡（正面、横置同步）。</summary>
        private void RebuildCouncils()
        {
            if (_councilUIScene == null) return;
            var zone = _matchClient?.ClientZoneManager;
            if (zone == null) return;

            foreach (var ui in _councilCardUIMap.Values)
                if (ui != null && GodotObject.IsInstanceValid(ui)) ui.QueueFree();
            _councilCardUIMap.Clear();

            for (int p = 1; p <= 2; p++)
            {
                var container = p == 1 ? _councilContainer1 : _councilContainer2;
                if (container == null) continue;
                foreach (Node child in container.GetChildren())
                    if (child is CouncilCardUI ccu)
                    {
                        container.RemoveChild(ccu);
                        ccu.QueueFree();
                    }

                var cards = new System.Collections.Generic.List<Control>();
                // 【2026-09】全量重建：先清空本玩家旧秘社 UI 的登记，避免"秘社重放/移除"后残留已释放节点的条目
                // （选目标命中与高亮都按 UID 查本表，残留条目会指向已 QueueFree 的节点）。
                {
                    var stale = new System.Collections.Generic.List<string>();
                    foreach (var kv in _councilCardUIMap)
                        if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value) || kv.Value.Instance?.ControllerId == p)
                            stale.Add(kv.Key);
                    foreach (var uid in stale)
                    {
                        if (_councilCardUIMap.TryGetValue(uid, out var old) && old != null && GodotObject.IsInstanceValid(old))
                            old.CouncilDoubleClicked -= OnCouncilDoubleClicked;
                        _councilCardUIMap.Remove(uid);
                    }
                }
                foreach (var inst in zone.GetZoneCards(CardZone.CouncilZone, p))
                {
                    if (inst == null || inst.CouncilTemplate == null) continue;
                    var ui = _councilUIScene.Instantiate<CouncilCardUI>();
                    container.AddChild(ui);
                    ui.Setup(inst);   // 秘社 UI 是另一种类型（CouncilCardUI），不参与"卡面势力"注入
                    ui.SetTapped(inst.IsTapped);
                    _councilCardUIMap[inst.UID] = ui;
                    ui.CouncilDoubleClicked += OnCouncilDoubleClicked;
                    cards.Add(ui);
                }
                // 与单机 Arena 一致：经共享排版设置位置/尺寸/枢轴/TargetPosition（全局），
                // 否则 CouncilCardUI._Process 会把卡平滑拉向"本地坐标被当作全局坐标"的屏幕左上角。
                CardLayoutHelper.LayoutCardsEvenly(container, cards);
            }
        }

        /// <summary>重建附属/封印区（AttachedZone / SealedZone）：挂到宿主卡同容器并相对宿主对齐。
        /// 全量重建语义：宿主卡已由 RebuildAreaZoneCards / RebuildCouncils 先行渲染，此处只补附属/封印卡。
        /// 注意：必须"先 AddChild 再 Setup"（Setup 在 AddChild 前会被 _Ready 的默认 SetFace(false) 覆盖成卡背）。</summary>
        private void RebuildAttachmentsAndSeals()
        {
            var zone = _matchClient?.ClientZoneManager;
            if (zone == null) return;

            var cards = new System.Collections.Generic.List<CardInstance>();
            var att = zone.GetZoneCards(CardZone.AttachedZone, 0);
            var seal = zone.GetZoneCards(CardZone.SealedZone, 0);
            if (att != null) cards.AddRange(att);
            if (seal != null) cards.AddRange(seal);

            // 附属于地区/封印于地区的卡：按容器分组，全部渲染后统一与容器内角色卡平铺（CardLayoutHelper 会按 IsTapped 旋转）。
            var areaGroup = new System.Collections.Generic.Dictionary<Control, System.Collections.Generic.List<CardUI>>();

            // 结附/封印于"卡"的附属：先只挂载，暂不定位。
            // 【2026-10 修复】原流程在循环中立即按宿主**当时**的 Position 定位，而宿主角色卡可能
            // 在下方 areaGroup 平铺时才被重新排布（地区附属 A 加入序列 ⇒ 角色 B 的 index/Position 变化），
            // 导致附属 C 停在宿主旧位置 ⇒ 相对宿主偏移一个平铺步长。
            // 对齐单机 ArrangeAllCards：先平铺（角色 + 地区附属），再基于宿主**最终** Position 相对定位。
            var pendingRelative = new System.Collections.Generic.List<(CardUI ui, Control hostUi, bool isSeal, int idx)>();

            foreach (var inst in cards)
            {
                if (inst == null || inst.PlayerTemplate == null) continue;
                bool isSeal = inst.IsSeal;

                // 【2026-10·对齐单机 Arena.Layout:610】宿主判定加 IsSeal 分支：
                // 单机写法 `IsAttached ? AttachedTo : (IsSeal ? SealedBy : null)`；
                // 联机旧写法 `IsAttached ? AttachedTo : SealedBy` 在非结附时会无条件取 SealedBy，
                // 一旦 AttachedTo 解析不到就退化成"地区附属"混入统一平铺行 —— 既挤开横置卡、又不再叠在宿主下。
                CardInstance host = inst.IsAttached ? inst.AttachedTo : (inst.IsSeal ? inst.SealedBy : null);
                if (host != null)
                {
                    // —— 结附/封印于卡（角色或秘社）：挂宿主同容器，相对宿主对齐 ——
                    Control hostUi = null;
                    if (host.CurrentZone == CardZone.CouncilZone)
                    {
                        if (_councilCardUIMap.TryGetValue(host.UID, out var cui) && cui != null) hostUi = cui;
                    }
                    else if (_cardUIMap.TryGetValue(host.UID, out var hui) && hui != null) hostUi = hui;

                    if (hostUi == null || !GodotObject.IsInstanceValid(hostUi)) continue;   // 宿主 UI 未渲染/已释放，跳过
                    // 【2026-10·对齐单机】同容器护栏：附属必须挂到"宿主 UI 所在容器"，
                    // 否则会继承别的容器的坐标系（甚至宿主的横置旋转）而错位（单机 4 处同款护栏）。
                    var hostContainer = hostUi.GetParent() as Control;
                    if (hostContainer == null || !GodotObject.IsInstanceValid(hostContainer)) continue;

                    // 先入树再 Setup（修复：Setup 在 AddChild 前会被 _Ready 覆盖成卡背）
                    var ui = _cardUIScene.Instantiate<CardUI>();
                    hostContainer.AddChild(ui);
                    SetupCardUI(ui, inst);
                    ui.SetExternalCostMod(ComputeClientCostMod(inst));
                    ui.SetTapped(inst.IsTapped);
                    ui.CanDrag = false;
                    RegisterOnlineCardUI(inst, ui);

                    // 相对宿主定位（含宿主对齐、横置旋转、多张下延/上延、ZIndex）
                    // 推迟到 areaGroup 平铺之后统一执行，避免用宿主尚未最终化的 Position 定位。
                    int idx = 0;
                    var hostList = isSeal ? host.SealedCards : host.AttachedCards;
                    if (hostList != null) { int k = hostList.IndexOf(inst); if (k >= 0) idx = k; }
                    pendingRelative.Add((ui, hostUi, isSeal, idx));
                }
                else
                {
                    // —— 附属于地区 / 封印于地区：挂到对应 AreaSlot 争夺区容器，随后统一平铺 ——
                    var slot = _areaSlots.Find(s => s != null && s.AreaUID == inst.ZoneDetailId);
                    var hostContainer = inst.ControllerId == 1 ? slot?.Player1Area : slot?.Player2Area;
                    if (hostContainer == null) continue;

                    var ui = _cardUIScene.Instantiate<CardUI>();
                    hostContainer.AddChild(ui);
                    SetupCardUI(ui, inst);
                    ui.SetExternalCostMod(ComputeClientCostMod(inst));
                    ui.SetTapped(inst.IsTapped);
                    ui.CanDrag = false;
                    RegisterOnlineCardUI(inst, ui);

                    if (!areaGroup.TryGetValue(hostContainer, out var list)) { list = new System.Collections.Generic.List<CardUI>(); areaGroup[hostContainer] = list; }
                    list.Add(ui);
                }
            }

            // 结附/封印于地区的卡：与容器内角色卡一起均匀平铺（对齐单机 ArrangeAreaAttachmentsAndSeals：
            // 角色 + 地区附属/封印合并成统一序列；结附角色的附属 C 挂在 AttachedZone，绝不在本序列内，
            // 只由下方 pendingRelative 循环相对宿主单独定位）。
            // 【2026-10 修复】角色侧来源改为 RebuildAreaZoneCards 登记的 `_areaRoleGroups`
            // （争夺区 UI **全集**，含形态角色这类 IsAttached==true 的卡），
            // 不再用"遍历 GetChildren() + `!IsAttached && !IsSeal` 过滤"——该过滤会让形态角色
            // 缺席统一平铺 ⇒ 停留旧位并与地区附属完全重叠（两张 JC027 重叠）、其余卡整体位移（横置卡错位）。
            // 合并与平铺走共享实现 CardLayoutHelper.LayoutAreaRow，与单机同源。
            var containers = new System.Collections.Generic.HashSet<Control>();
            foreach (var k in _areaRoleGroups.Keys) containers.Add(k);
            foreach (var k in areaGroup.Keys) containers.Add(k);
            foreach (var container in containers)
            {
                _areaRoleGroups.TryGetValue(container, out var roles);
                areaGroup.TryGetValue(container, out var areaAttachSeals);
                System.Collections.Generic.List<Control> attachSeals = null;
                if (areaAttachSeals != null)
                {
                    attachSeals = new System.Collections.Generic.List<Control>();
                    foreach (var a in areaAttachSeals)
                        if (a != null && GodotObject.IsInstanceValid(a)) attachSeals.Add(a);
                }
                CardLayoutHelper.LayoutAreaRow(container, roles, attachSeals);
            }

            // 诊断日志：争夺区统一平铺序列（用于排查"横置卡错位 / 形态角色与地区附属重叠"）
            // 形态角色数 = 争夺区内仍保留附属身份的卡（如 JC027 变角色后 IsAttached 仍为 true），
            // 它**必须**参与统一平铺；若该数为 0 且存在形态卡，说明序列口径又漂了。
            if (containers.Count > 0)
            {
                int formRoles = 0;
                foreach (var kv in _areaRoleGroups)
                    foreach (var c in kv.Value)
                        if (c is CardUI fc && fc.Instance != null
                            && fc.Instance.CurrentZone == CardZone.AreaZone && fc.Instance.IsAttached) formRoles++;
                GD.Print($"[ArenaOl] 争夺区统一平铺：容器 {containers.Count} 个，地区附属/封印 {areaGroup.Count} 组，形态角色 {formRoles} 张");
            }

            // 宿主位置已最终化（含地区附属平铺后的角色重排），再统一相对宿主定位附属/封印。
            foreach (var p in OrderHostFirst(pendingRelative))
                LayoutAttachmentRelative(p.ui, p.hostUi, p.isSeal, p.idx);
        }

        /// <summary>
        /// 稳定排序：把"宿主本身也是待定位附属"的嵌套附属排在其子级之前（宿主先定位）。
        /// 【2026-10 兜底】附属结附于附属时，若子级先定位会用到父级尚未更新的旧坐标 ⇒ 位置错乱。
        /// 采用"多轮稳定扫描"，无依赖环时保持原相对顺序，有环（异常数据）时按原序兜底输出，绝不丢项。
        /// </summary>
        private static System.Collections.Generic.List<(CardUI ui, Control hostUi, bool isSeal, int idx)> OrderHostFirst(
            System.Collections.Generic.List<(CardUI ui, Control hostUi, bool isSeal, int idx)> items)
        {
            var result = new System.Collections.Generic.List<(CardUI ui, Control hostUi, bool isSeal, int idx)>();
            if (items == null || items.Count == 0) return result;

            var pending = new System.Collections.Generic.List<(CardUI ui, Control hostUi, bool isSeal, int idx)>(items);
            var placed = new System.Collections.Generic.HashSet<CardUI>();
            var pendingHosts = new System.Collections.Generic.HashSet<CardUI>();
            foreach (var p in pending) pendingHosts.Add(p.ui);

            while (pending.Count > 0)
            {
                bool progressed = false;
                for (int i = 0; i < pending.Count;)
                {
                    var p = pending[i];
                    // 宿主不在"待定位集合"中 ⇒ 宿主位置已最终化（争夺区角色/秘社/已定位附属），可以定位
                    bool hostReady = !(p.hostUi is CardUI hc && pendingHosts.Contains(hc) && !placed.Contains(hc));
                    if (!hostReady) { i++; continue; }

                    result.Add(p);
                    placed.Add(p.ui);
                    pending.RemoveAt(i);
                    progressed = true;
                }
                if (!progressed) break;   // 依赖环：按当前顺序兜底，避免死循环
            }

            if (pending.Count > 0) result.AddRange(pending);   // 绝不丢项
            return result;
        }

        /// <summary>把附属/封印卡相对宿主（CardUI 或 CouncilCardUI）对齐定位（移植单机 ArrangeAllAttachments/Seals 算法）。
        /// seal=true 封印卡朝宿主上缘上延；false 附属朝宿主下缘下延。含横置旋转、Pivot、ZIndex 与平滑目标。</summary>
        private void LayoutAttachmentRelative(CardUI ui, Control hostUi, bool seal, int index)
        {
            // 附属/封印自身可能横置：同步旋转与 Pivot
            ui.PivotOffset = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT) / 2f;
            ui.RotationDegrees = ui.IsTapped ? 90f : 0f;

            // 宿主真实视觉中心与上/下缘（本地坐标，兼容宿主横置旋转）
            // 【2026-09 修复】原用 `PivotOffset.Rotated(角度)` 估算视觉中心 ⇒ 宿主横置时误差 (120,20)、
            // 附属被摆到宿主左上方；Godot 旋转**绕 PivotOffset**，旋转后视觉中心仍是 `Position + PivotOffset`（单机同修）。
            bool hostTapped = hostUi is CardUI hc ? hc.IsTapped : hostUi is CouncilCardUI cc && cc.IsTapped;
            float centerX = hostUi.Position.X + hostUi.PivotOffset.X;
            float centerY = hostUi.Position.Y + hostUi.PivotOffset.Y;
            float visHalfH = hostTapped ? CardLayoutHelper.CARD_WIDTH / 2f : CardLayoutHelper.CARD_HEIGHT / 2f;

            float topLeftX = centerX - CardLayoutHelper.CARD_WIDTH / 2f;
            float topLeftY;
            if (seal)
            {
                float topY = centerY - visHalfH;
                topLeftY = topY - CardLayoutHelper.CARD_HEIGHT + (index + 1) * (CardLayoutHelper.CARD_HEIGHT * 0.4f);
            }
            else
            {
                float bottomY = centerY + visHalfH;
                topLeftY = bottomY - CardLayoutHelper.CARD_HEIGHT + (index + 1) * (CardLayoutHelper.CARD_HEIGHT * 0.4f);
            }

            ui.Position = new Vector2(topLeftX, topLeftY);
            ui.TargetPosition = ui.GlobalPosition;
            // 宿主层最高，附属/封印依次递减（越后越远）。
            // 【对齐单机】单机附属 `targetUI.ZIndex-(i+1)`、封印 `hostUI.ZIndex-1-i`，两者都是"减 1 起步"；
            // 原联机 `-(seal?1:0)-index` 使附属第一张与宿主同层（叠在宿主之上），改为统一减 1 起步。
            ui.ZIndex = hostUi.ZIndex - 1 - index;
        }

        /// <summary>重建分数区（ScoreZone）：渲染赢取地区/计分卡，并从快照刷新双方胜利分数。</summary>
        private void RebuildScoreZone(GameSnapshot snap)
        {
            var zone = _matchClient?.ClientZoneManager;
            if (zone == null) return;

            for (int p = 1; p <= 2; p++)
            {
                var container = p == 1 ? _scoreContainer1 : _scoreContainer2;
                if (container == null) continue;
                foreach (Node child in container.GetChildren())
                    if (child is CardUI || child is AreaCardUI)
                    {
                        container.RemoveChild(child);
                        child.QueueFree();
                    }

                foreach (var inst in zone.GetZoneCards(CardZone.ScoreZone, p))
                {
                    if (inst == null) continue;
                    if (inst.PlayerTemplate != null)
                    {
                        var ui = _cardUIScene.Instantiate<CardUI>();
                        container.AddChild(ui);
                        SetupCardUI(ui, inst);
                        ui.SetExternalCostMod(ComputeClientCostMod(inst));
                        ui.SetFace(true);
                        ui.CanDrag = false;
                        RegisterOnlineCardUI(inst, ui);
                    }
                    else if (inst.AreaTemplate != null && _areaCardUIScene != null)
                    {
                        var ui = _areaCardUIScene.Instantiate<AreaCardUI>();
                        container.AddChild(ui);
                        ui.Setup(inst.AreaTemplate);
                        ui.SetFace(true);
                        ui.MouseFilter = MouseFilterEnum.Ignore;
                    }
                }
                CardLayoutHelper.LayoutCardsEvenly(container, GatherCardUIs(container));
            }
        }

        /// <summary>收集容器内所有 CardUI（供均匀排版）。</summary>
        private System.Collections.Generic.List<Control> GatherCardUIs(Control container)
        {
            var list = new System.Collections.Generic.List<Control>();
            if (container == null) return list;
            foreach (Node child in container.GetChildren())
                if (child is CardUI c && GodotObject.IsInstanceValid(c)) list.Add(c);
            return list;
        }

        /// <summary>重建堆叠区（StackPanel/StackContainer）：条目以效果文字展示（客户端投影不重建卡片型 Kind）。</summary>
        /// <summary>
        /// 重建堆叠区（对齐单机 Arena.RefreshStackUI）：
        ///  - 卡片型 → StackEntryUI 内显示真实卡面；
        ///  - 效果型 → 半透明遮罩 + 角标 + 文本，并按快照的 SourceTemplateUid 显示来源卡卡图；
        ///  - Kind=AreaWon → 额外挂一张地区卡面（复用 AreaCardUI）；
        ///  - 每条目按 Targets 绘制"条目 → 目标"箭头（颜色按控制者），每帧跟随端点。
        /// 联机为快照驱动：每次快照全量重建（同时清理旧箭头）。
        /// </summary>
        private void RefreshStackUI()
        {
            if (_stackContainer == null) return;
            foreach (Node child in _stackContainer.GetChildren())
                if (child is StackEntryUI old)
                {
                    _stackContainer.RemoveChild(old);
                    old.QueueFree();
                }
            _stackEntryUIMap.Clear();
            ClearStackArrows();

            var stack = _matchClient?.ClientStackManager;
            if (stack == null || _stackEntryScene == null) return;

            var snaps = _matchClient?.ClientStackSnapshot;   // 与 Entries 同序（ApplySnapshot 按序追加）
            var entries = stack.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                var snap = (snaps != null && i < snaps.Count) ? snaps[i] : null;

                var ui = _stackEntryScene.Instantiate<StackEntryUI>();
                _stackContainer.AddChild(ui);      // 先入树再 Setup（_Ready 内取子节点引用）
                ui.Setup(entry, null);

                // 效果型：客户端没有 StackEffect 实例，用快照的来源模板 UID 补来源卡图
                // 注：不改动单机 StackEntryUI —— 直接操作其场景内节点 "TriggerCardArt"。
                if (!entry.IsCard && snap != null && !string.IsNullOrEmpty(snap.SourceTemplateUid))
                {
                    var art = ui.GetNodeOrNull<TextureRect>("TriggerCardArt");
                    if (art != null)
                    {
                        var tex = ResourceLoader.Load<Texture2D>(ResolvePlayerCardImagePath(snap.SourceTemplateUid));
                        if (tex != null) { art.Texture = tex; art.Visible = true; }
                        else art.Visible = false;
                    }
                }

                // 赢取地区条目：复制并显示该地区卡的卡面（对齐单机）
                if (entry.Kind == ResolveKind.AreaWon && !string.IsNullOrEmpty(entry.WonAreaUid))
                {
                    var slot = _areaSlots.Find(s => s != null && s.AreaUID == entry.WonAreaUid);
                    if (slot?.Data != null) ui.AttachAreaCard(slot.Data, _areaCardUIScene);
                }

                _stackEntryUIMap[ui] = entry;
            }

            RedrawStackArrows();
        }

        /// <summary>按条目 Targets 创建箭头实例（颜色按控制者着色，与单机一致）。</summary>
        private void RedrawStackArrows()
        {
            if (_arrowLayer == null || _stackArrowScene == null) return;
            ClearStackArrows();
            foreach (var kv in _stackEntryUIMap)
            {
                var entry = kv.Value;              // 键=条目 UI，值=数据模型条目
                var list = entry.Targets;
                if (list == null || list.Count == 0) continue;
                Color col = entry.ControllerId == 1 ? Colors.OrangeRed : Colors.CornflowerBlue;
                var arrows = new List<StackArrow>();
                foreach (var _ in list)
                {
                    var arrow = _stackArrowScene.Instantiate<StackArrow>();
                    arrow.ZIndex = Layers.Arrows;
                    arrow.ZAsRelative = false;
                    _arrowLayer.AddChild(arrow);
                    arrow.SetColor(col);
                    arrows.Add(arrow);
                }
                _stackArrows[entry] = arrows;
            }
            UpdateStackArrowEndpoints();
        }

        /// <summary>清理堆叠箭头实例。</summary>
        private void ClearStackArrows()
        {
            foreach (var kv in _stackArrows)
                foreach (var a in kv.Value)
                    if (a != null && GodotObject.IsInstanceValid(a)) a.QueueFree();
            _stackArrows.Clear();
        }

        /// <summary>每帧跟随：起点=条目 UI 中心，终点=目标卡/地区/玩家区中心（对齐单机 UpdateArrowEndpoints）。</summary>
        private void UpdateStackArrowEndpoints()
        {
            if (_arrowLayer == null || _stackArrows.Count == 0) return;
            foreach (var kv in _stackArrows)
            {
                var entry = kv.Key;
                var arrows = kv.Value;
                if (arrows == null || arrows.Count == 0 || entry.Targets == null) continue;
                StackEntryUI entryUi = null;
                foreach (var m in _stackEntryUIMap)
                    if (ReferenceEquals(m.Value, entry)) { entryUi = m.Key; break; }
                if (entryUi == null || !GodotObject.IsInstanceValid(entryUi)) continue;

                Vector2 from = entryUi.GetGlobalCenter();
                int n = Mathf.Min(arrows.Count, entry.Targets.Count);
                for (int i = 0; i < n; i++)
                {
                    var to = ResolveStackTargetCenter(entry.Targets[i]);
                    if (to == null) continue;
                    arrows[i].SetPoints(from, to.Value);
                }
            }
        }

        /// <summary>定位堆叠目标节点中心（全局坐标）；找不到返回 null。</summary>
        private Vector2? ResolveStackTargetCenter(StackTargetRef target)
        {
            if (target == null) return null;
            switch (target.Kind)
            {
                case StackTargetRef.RefKind.Card:
                    if (!string.IsNullOrEmpty(target.CardUid)
                        && _cardUIMap.TryGetValue(target.CardUid, out var cardUi)
                        && cardUi != null && GodotObject.IsInstanceValid(cardUi))
                        return cardUi.GetVisualGlobalRect().GetCenter();
                    return null;
                case StackTargetRef.RefKind.Area:
                    AreaSlot slot = null;
                    if (!string.IsNullOrEmpty(target.AreaUid))
                        slot = _areaSlots.Find(s => s != null && s.AreaUID == target.AreaUid);
                    if (slot == null && target.AreaSlotId >= 1 && target.AreaSlotId <= _areaSlots.Count)
                        slot = _areaSlots[target.AreaSlotId - 1];
                    if (slot == null || !GodotObject.IsInstanceValid(slot)) return null;
                    return slot.GlobalPosition + slot.GetRect().Size / 2f;
                case StackTargetRef.RefKind.Player:
                    var pArea = GetNodeOrNull<Control>(target.PlayerId == 1 ? "Player1Area" : "Player2Area");
                    if (pArea != null) return pArea.GlobalPosition + pArea.GetRect().Size / 2f;
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>刷新各地区势力标记（从客户端投影 AreaManager 读取）。</summary>
        private void RefreshAreaMarks()
        {
            var areaMgr = _matchClient?.ClientAreaManager;
            if (areaMgr == null) return;
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                var m = areaMgr.GetMarks(slot.AreaUID);
                slot.UpdateMarks(m.p1, m.p2);
            }
        }

        // ==================== 剩余交互：投降 / 现身 / 激活（意图上行） ====================

        /// <summary>本地玩家投降。仅允许操作自己，越权忽略。</summary>
        public void DoConcede(int playerId)
        {
            if (_matchClient == null || !IsLocalPlayer(playerId)) return;
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.Concede);
            GD.Print($"[ArenaOl] 上行 Concede: 玩家{_localPlayerId}");
        }

        // ==================== PM 调试命令（联机服务端权威执行） ====================
        /// <summary>
        /// Shift+Enter 执行输入框内的 PM 命令（逐行上行 DebugCommand 意图，服务端权威执行）。
        /// 另：`~` / `` ` `` 键**先于**字符插入被拦截 —— 关闭命令框并吞掉该次按键（命令框不接受 `~` 字符）。
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
            if (!key.ShiftPressed) return;   // 无 Shift 交给 TextEdit 换行
            if (_commandInput == null) return;
            string text = _commandInput.Text;
            _commandInput.Text = "";
            SendPMBatch(text);
            _commandInput?.AcceptEvent();
        }

        /// <summary>把多行 PM 命令逐行作为 DebugCommand 意图上行（空行/# 注释跳过）。</summary>
        private void SendPMBatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _matchClient == null) return;
            foreach (var raw in text.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.DebugCommand, line);
                GD.Print($"[ArenaOl] 上行 DebugCommand: {line}");
            }
        }

        /// <summary>反引号 ` 切换 PM 命令输入框可见性（与单机 Arena 的按键一致，Key 96）。</summary>
        public override void _Input(InputEvent @event)
        {
            // 战斗对抗窗口：点击场上卡分配伤害（左键 +1 / 右键 -1），与单机 Arena._Input 的路由一致
            if (_onlineStrengthWindow != null && GodotObject.IsInstanceValid(_onlineStrengthWindow)
                && @event is InputEventMouseButton strMb && strMb.Pressed
                && (strMb.ButtonIndex == MouseButton.Left || strMb.ButtonIndex == MouseButton.Right))
            {
                var hitCard = PickFieldCardAt(strMb.GlobalPosition);
                if (hitCard != null)
                {
                    _onlineStrengthWindow.TryAllocate(hitCard, strMb.ButtonIndex == MouseButton.Right ? 2 : 1);
                    AcceptEvent();
                    return;
                }
            }

            // 【2026-09 用户口径 JC062/JC021】**整窗多选**：左键选中/右键取消（窗口保持打开，等"确认/取消"）。
            // 必须在下面的"单击即完成"分支之前接管（否则多选的第一次点击就会被当成"完成选择"回包）。
            if (_onlineMultiPickActive
                && @event is InputEventMouseButton mpMb && mpMb.Pressed
                && (mpMb.ButtonIndex == MouseButton.Left || mpMb.ButtonIndex == MouseButton.Right))
            {
                bool rightClick = mpMb.ButtonIndex == MouseButton.Right;
                // **顺序关键：先判"点在卡上"，再判地区**——地区槽矩形包含其上的卡，顺序反了会把
                // "点角色选角色"误判为"选了地区"（用户实测 JC021："第一个选择了角色，会显示地区（1/1）"）。
                var mpCard = PickFieldCardAt(mpMb.GlobalPosition);
                string mpCardUid = mpCard?.Instance?.UID;
                if (!string.IsNullOrEmpty(mpCardUid)
                    && (_onlineMultiPickCandCards.Contains(mpCardUid) || _onlineMultiPickCards.Contains(mpCardUid)
                        || rightClick))
                {
                    ToggleOnlineMultiPickCard(mpCardUid, rightClick);
                    AcceptEvent();
                    return;
                }
                string mpArea = PickLegalActivationAreaAt(mpMb.GlobalPosition);
                if (string.IsNullOrEmpty(mpArea)) mpArea = FindTargetAreaUidAt(mpMb.GlobalPosition);
                if (!string.IsNullOrEmpty(mpArea))
                    ToggleOnlineMultiPickArea(mpArea, rightClick);
                AcceptEvent();
                return;
            }

            // 场上点击高亮选目标：左键命中合法目标卡/地区 → 完成选择（经复用的 TargetSelectionWindow.PickFromArena）
            if (_activationTargetActive
                && @event is InputEventMouseButton tsMb && tsMb.Pressed && tsMb.ButtonIndex == MouseButton.Left)
            {
                // 命中合法目标卡则完成选择；未命中不取消（选目标无取消，须点合法目标），吞掉事件避免误拖拽其它卡。
                // 机动等"地区目标"（单机 trigger.IsAreaTarget 分支）同样在此接管：_Input 先于 Control._GuiInput，
                // 故必须在本分支判定地区槽位，否则 AreaSlot._GuiInput 收不到点击。
                string hit = PickLegalActivationTargetAt(tsMb.GlobalPosition);
                if (string.IsNullOrEmpty(hit)) hit = PickLegalActivationAreaAt(tsMb.GlobalPosition);
                if (!string.IsNullOrEmpty(hit) && _onlineTargetWindow != null && GodotObject.IsInstanceValid(_onlineTargetWindow))
                    _onlineTargetWindow.PickFromArena(hit);
                AcceptEvent();
                return;
            }

            // 激活拖拽进行中：吞掉左键；抬起即结算落点（复刻单机 EndActivateDrag）
            if (_activateDragSource != null
                && @event is InputEventMouseButton dragMb && dragMb.ButtonIndex == MouseButton.Left)
            {
                if (!dragMb.Pressed) EndOnlineActivateDrag(dragMb.GlobalPosition);
                AcceptEvent();
                return;
            }

            // 激活拖拽起手：优先于手牌拖拽；仅当命中己方争夺区「可发动」角色卡才成立
            if (@event is InputEventMouseButton activateDown && activateDown.Pressed && activateDown.ButtonIndex == MouseButton.Left
                && TryBeginOnlineActivateDragAt(activateDown.GlobalPosition))
            {
                AcceptEvent();
                return;
            }

            // `~` / `` ` `` 键切换 PM 命令输入框可见性（按一下打开、再按一下关闭；
            // 打开时该键被吞掉，因此命令框里不会出现 `~` 字符 —— 与单机 Arena 行为一致）
            if (@event is InputEventKey key && key.Pressed && !key.Echo && IsCommandToggleKey(key))
            {
                ToggleCommandInput();
                AcceptEvent();
            }
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
            _commandInput.ZIndex = CommandInputZIndex;
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

        /// <summary>场上选目标命中：返回点击位置下 ZIndex 最高的合法目标卡 UID（无则 null）。
        /// 候选同时覆盖玩家卡（`_cardUIMap`）与**秘社**（`_councilCardUIMap`）——两者共用同一 ZIndex 比较。</summary>
        private string PickLegalActivationTargetAt(Vector2 globalPos)
        {
            string best = null;
            int bestZ = int.MinValue;
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (!_activationLegalCardUids.Contains(ui.Instance.UID)) continue;
                if (ui.GetVisualGlobalRect().HasPoint(globalPos) && ui.ZIndex > bestZ)
                {
                    bestZ = ui.ZIndex;
                    best = ui.Instance.UID;
                }
            }
            foreach (var kv in _councilCardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (!_activationLegalCardUids.Contains(ui.Instance.UID)) continue;
                if (ui.GetVisualGlobalRect().HasPoint(globalPos) && ui.ZIndex > bestZ)
                {
                    bestZ = ui.ZIndex;
                    best = ui.Instance.UID;
                }
            }
            return best;
        }

        /// <summary>【2026-09】秘社目标高亮开关（与玩家卡 `SetTargetHighlight` 同款视觉）。
        /// 秘社在 `_councilCardUIMap` 里，`OpenOnlineTargetSelectionWindow` 的卡片循环覆盖不到它。</summary>
        private void HighlightOnlineCouncilTargets(bool on)
        {
            foreach (var kv in _councilCardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (_activationLegalCardUids.Contains(ui.Instance.UID)) ui.SetTargetHighlight(on);
            }
        }

        /// <summary>场上选目标命中（地区）：返回点击位置下的合法目标地区 UID（无则 null）。
        /// 对齐单机 `TriggerInteractionCoordinator._targetIsArea` 的"点高亮地区"分支（判定范围为整个槽位矩形）。</summary>
        private string PickLegalActivationAreaAt(Vector2 globalPos)
        {
            if (_activationLegalAreaUids.Count == 0 || _areaSlots == null) return null;
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                if (!_activationLegalAreaUids.Contains(slot.AreaUID)) continue;
                if (slot.ContainsGlobalPoint(globalPos)) return slot.AreaUID;
            }
            return null;
        }

        /// <summary>暗藏者双击现身：上行 RevealCovert 意图，服务端权威裁决。</summary>
        private void OnCovertRevealRequested(CardUI cardUI)
        {
            HealStaleLocalModalFlag();
            if (_activationSelectionBusy || _noticeWindowOpen) { GD.Print($"[ArenaOl] ⏸ 交互被本地模态吞掉（busy={_activationSelectionBusy} notice={_noticeWindowOpen}）→ 已提示并自愈检查"); return; }   // 本地模态：效果选择/选目标/提示窗期间禁止现身暗藏
            if (cardUI?.Instance == null || _matchClient == null) return;
            if (cardUI.Instance.ControllerId != _localPlayerId) return;   // 输入隔离：只能现身自己的暗藏
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.RevealCovert, cardUI.Instance.UID);
            GD.Print($"[ArenaOl] 上行 RevealCovert: {cardUI.Instance.PlayerTemplate?.CardName}");
        }

        /// <summary>
        /// 【2026-09 防御加固·用户实测"偶发：卡有高亮但双击无反应"】
        /// `_activationSelectionBusy` 是"本地模态"标志（决策期间禁止再起手/让过/结束回合）。
        /// 它由各窗口的回调复位；一旦某条异常路径（窗口被快照重建释放、回包丢失、取消链断裂…）
        /// 让它**残留为 true 而实际已无任何决策窗口存活**，该玩家的双击/激活就会被**永久静默吞掉**
        /// （用户实测：卡有高亮、双击没弹窗也没反应，且**偶发、难以复现**）。
        /// 故在双击入口做**自愈**：若标志为真但**没有任何存活的决策窗口**，判定为残留 → 复位后继续处理本次双击。
        /// 只在"确实没有任何窗口"时复位，因此不影响任何正常的模态保护。
        /// </summary>
        private bool HasLiveLocalDecisionWindow()
        {
            if (_activationTargetActive) return true;                       // 场上点选目标状态机
            if (_activateDragSource != null) return true;                   // 激活拖拽进行中
            if (_onlineChoiceWindow != null && GodotObject.IsInstanceValid(_onlineChoiceWindow)) return true;
            if (_onlineTargetWindow != null && GodotObject.IsInstanceValid(_onlineTargetWindow) && _onlineTargetWindow.Visible) return true;
            if (_onlinePayOrTapWindow != null && GodotObject.IsInstanceValid(_onlinePayOrTapWindow)) return true;
            if (_onlineInvestigationWindow != null && GodotObject.IsInstanceValid(_onlineInvestigationWindow)) return true;
            if (_noticeWindow != null && GodotObject.IsInstanceValid(_noticeWindow)) return true;
            if (_choicePromptPanel != null && GodotObject.IsInstanceValid(_choicePromptPanel)) return true;
            return _onlineCombatWindowOpen;
        }

        /// <summary>见 <see cref="HasLiveLocalDecisionWindow"/>：残留标志自愈。</summary>
        private void HealStaleLocalModalFlag()
        {
            // 提示窗标志同理：窗口已不存在（异常/被快照重建释放）却仍为 true → 复位
            if (_noticeWindowOpen && (_noticeWindow == null || !GodotObject.IsInstanceValid(_noticeWindow)))
            {
                GD.PrintErr("[ArenaOl] ⚠ 检测到「提示窗打开」标志残留（窗口已不存在）→ 已自愈复位");
                _noticeWindowOpen = false;
            }
            if (!_activationSelectionBusy) return;
            if (HasLiveLocalDecisionWindow()) return;
            GD.PrintErr("[ArenaOl] ⚠ 检测到「本地模态」标志残留（无任何存活的决策窗口）→ 已自愈复位（用户实测偶发双击无反应）");
            _activationSelectionBusy = false;
        }

        /// <summary>附属卡双击激活：有多个可激活效果则弹联机效果列表供选择，再按所选发动；单效果直接发动（与单机一致）。</summary>
        private void OnAttachmentDoubleClicked(CardUI cardUI)
        {
            // 【2026-09 防御加固】先自愈可能残留的"本地模态"标志（偶发双击无反应的兜底）
            HealStaleLocalModalFlag();
            HealStaleLocalModalFlag();
            if (_activationSelectionBusy || _noticeWindowOpen) { GD.Print($"[ArenaOl] ⏸ 交互被本地模态吞掉（busy={_activationSelectionBusy} notice={_noticeWindowOpen}）→ 已提示并自愈检查"); return; }   // 本地模态：防重复双击/提示窗期间
            if (cardUI?.Instance == null || _matchClient == null)
            {
                GD.Print("[ArenaOl] 双击激活忽略：卡实例或联机客户端未就绪");
                return;
            }
            if (cardUI.Instance.ControllerId != _localPlayerId)
            {
                GD.Print($"[ArenaOl] 双击激活忽略：{cardUI.Instance.PlayerTemplate?.CardName} " +
                         $"控制者={cardUI.Instance.ControllerId} ≠ 本方={_localPlayerId}");
                return;
            }
            var inst = cardUI.Instance;
            // 【2026-09 修复·JC019/JC024（与单机 Arena 同款）】候选＝**模板 ∪ 运行期授予**
            // （`GetAllActivateAbilities`）：JC019 授予宿主的激活能力不在宿主模板里，
            // 旧写法（只读 `EffectDataList`）会判成"无可激活能力"直接忽略双击。
            // 【2026-09 修复·纯客户端】纯客户端拿不到服务端的"运行期授予"表 ⇒ 再并入
            // 服务端投影的能力清单（`GameViewSnap.ActivationAbilities`），否则联机下宿主的
            // 授予能力**既不能拖拽也没有双击菜单**。
            var abilities = CollectOnlineActivateAbilities(inst);
            if (abilities.Count == 0)
            {
                GD.Print($"[ArenaOl] {inst.PlayerTemplate?.CardName} 无可激活能力，忽略双击");
                return;
            }
            // 【2026-09 用户口径·JC053】"**不能直接发激活，要打开面板后展示一项可选、一项置灰**"：
            // 只有 1 条能力时**也不得盲目直发**——若该条当前不可用（前置不满足/已横置/无合法目标），
            // 必须开面板把它**置灰**展示（否则就是"有高亮、点了没反应"，用户实测）。可用且仅 1 条时仍直发（省一次点击）。
            if (abilities.Count == 1 && !IsActivationEffectDisabled(inst, abilities[0]))
            {
                SendActivation(inst, abilities[0]);
                return;
            }
            GD.Print($"[ArenaOl] 打开激活面板：{inst.PlayerTemplate?.CardName} 能力 {abilities.Count} 条（含置灰项）");
            ShowOnlineEffectChoice(inst, abilities);
        }

        /// <summary>秘社双击激活：有多个激活选项则弹联机选项列表供选择，再按所选发动。
        /// 与单机 Arena.OnCouncilDoubleClicked 一致：每个选项取首个 EffectData。</summary>
        private void OnCouncilDoubleClicked(CouncilCardUI cardUI)
        {
            HealStaleLocalModalFlag();
            if (_activationSelectionBusy || _noticeWindowOpen) { GD.Print($"[ArenaOl] ⏸ 交互被本地模态吞掉（busy={_activationSelectionBusy} notice={_noticeWindowOpen}）→ 已提示并自愈检查"); return; }
            if (cardUI?.Instance == null || _matchClient == null) return;
            if (cardUI.Instance.ControllerId != _localPlayerId) return;
            var inst = cardUI.Instance;
            var councilTmpl = inst.CouncilTemplate;
            if (councilTmpl == null || councilTmpl.ActivationOptions == null || councilTmpl.ActivationOptions.Count == 0)
            {
                GD.Print($"[ArenaOl] 秘社 {councilTmpl?.CardName} 无激活选项，忽略双击");
                return;
            }
            var options = new List<(Script.Data.Cards.CouncilActivationOption opt, EffectData eff)>();
            foreach (var opt in councilTmpl.ActivationOptions)
            {
                if (opt?.EffectDataList == null || opt.EffectDataList.Count == 0) continue;
                options.Add((opt, opt.EffectDataList[0]));
            }
            if (options.Count == 0)
            {
                GD.Print($"[ArenaOl] 秘社 {councilTmpl.CardName} 无有效激活选项，忽略双击");
                return;
            }
            if (options.Count == 1) { SendActivation(inst, options[0].eff); return; }

            _activationSelectionBusy = true;
            var items = new List<ActivationChoiceItem>();
            for (int i = 0; i < options.Count; i++)
            {
                var (opt, eff) = options[i];
                bool disabled = IsActivationEffectDisabled(inst, eff);
                string optName = string.IsNullOrEmpty(opt.OptionName) ? GetActivationAbilityLabel(eff) : opt.OptionName;
                string label = (i + 1) + ". " + optName;
                if (disabled) label += EffectDisabledReason(inst, eff);
                EffectData captured = eff;
                items.Add(new ActivationChoiceItem
                {
                    Label = label,
                    Disabled = disabled,
                    OnChosen = () => { _activationSelectionBusy = false; SendActivation(inst, captured); }
                });
            }
            OpenOnlineChoiceWindow(items);
        }

        /// <summary>为附属/角色卡的多个可激活效果构建联机效果列表并弹出。</summary>
        private void ShowOnlineEffectChoice(CardInstance inst, List<EffectData> abilities)
        {
            _activationSelectionBusy = true;
            var items = new List<ActivationChoiceItem>();
            for (int i = 0; i < abilities.Count; i++)
            {
                var eff = abilities[i];
                bool disabled = IsActivationEffectDisabled(inst, eff);
                string label = (i + 1) + ". " + GetActivationAbilityLabel(eff);
                if (disabled) label += EffectDisabledReason(inst, eff);
                EffectData captured = eff;
                items.Add(new ActivationChoiceItem
                {
                    Label = label,
                    Disabled = disabled,
                    OnChosen = () => { _activationSelectionBusy = false; SendActivation(inst, captured); }
                });
            }
            OpenOnlineChoiceWindow(items);
        }

        /// <summary>实例化联机效果选择窗口并弹出（点窗外不关闭；取消回调复位本地选择态）。
        /// `onCancelled` 非空时用调用方回调（如"激活代价窗"需要在取消时**回包 Cancelled**，否则服务端要等到超时）。</summary>
        private void OpenOnlineChoiceWindow(List<ActivationChoiceItem> items, System.Action onCancelled = null)
        {
            if (_onlineChoiceWindow != null && GodotObject.IsInstanceValid(_onlineChoiceWindow))
                _onlineChoiceWindow.QueueFree();
            _onlineChoiceWindow = new OnlineActivationChoiceWindow();
            AddChild(_onlineChoiceWindow);
            _onlineChoiceWindow.OnCancelled = onCancelled ?? (() => { _activationSelectionBusy = false; });
            _onlineChoiceWindow.Init(items);
            _onlineChoiceWindow.PopupCentered();
            _activationSelectionBusy = true;   // 决策期间本地模态（并延后快照应用，避免重建打断窗口）
        }

        /// <summary>
        /// 【2026-09】某场上卡的"可激活能力"候选（联机）：**本地（模板 ∪ 本地可见的运行期授予）**
        /// ∪ **服务端投影**（`GameViewSnap.ActivationAbilities` 里本地看不到的条目，如 JC019 授予宿主的能力）。
        /// 投影条目用**代理 `EffectData`** 表达：只带 EffectId / 展示名 / 目标形态（真实限制与候选
        /// 一律取自服务端投影，见 `ComputeLegalCardTargetUids` 的投影优先分支）。
        /// </summary>
        private List<EffectData> CollectOnlineActivateAbilities(CardInstance inst)
        {
            var result = new List<EffectData>();
            if (inst == null) return result;
            foreach (var e in ActivateEligibility.GetAllActivateAbilities(inst))
                if (e != null && e.Timing is EffectTiming.AttachmentActivated or EffectTiming.CharacterActivated)
                    result.Add(e);

            var view = ClientLegalityView;
            if (view?.ActivationAbilities == null) return result;
            foreach (var a in view.ActivationAbilities)
            {
                if (a == null || a.SourceUid != inst.UID) continue;
                bool exists = false;
                foreach (var e in result)
                    if (e != null && e.EffectId == a.EffectId) { exists = true; break; }
                if (exists) continue;
                result.Add(new EffectData
                {
                    EffectId = a.EffectId,
                    Timing = EffectTiming.CharacterActivated,
                    FaqNote = a.Label ?? "",
                    Target = new TargetSpec
                    {
                        Kind = (TargetKind)a.TargetKindValue,
                        NeedsSelection = a.NeedsSelection,
                    },
                });
            }
            return result;
        }

        /// <summary>单条激活效果在列表中的简短描述（优先 FaqNote，回退 EffectId；与单机 GetActivationAbilityLabel 一致）。</summary>
        private static string GetActivationAbilityLabel(EffectData ability)
        {
            if (!string.IsNullOrEmpty(ability.FaqNote)) return ability.FaqNote;
            if (!string.IsNullOrEmpty(ability.EffectId)) return ability.EffectId;
            return "激活效果";
        }

        /// <summary>联机本地粗筛：某激活效果当前是否置灰不可发动（需横置但已横置 / 需目标但无合法目标）。</summary>
        private bool IsActivationEffectDisabled(CardInstance inst, EffectData e)
        {
            // 【2026-09·JC053 口径落地·A78】联机客户端的"可发动"以**服务端投影清单**为准：
            // 若服务端**为这张卡**给出了能力投影，而本能力**不在其中**（＝服务端判不可发动，如前置条件不满足）
            // ⇒ **置灰**。这既实现了"一项可选、一项置灰"，又不依赖客户端本地规则求值（客户端无 `EffectEngine.Instance`）。
            // ⚠ 只在"这张卡确实有投影条目"时启用本条，避免服务端不下发投影的普通卡被整体误置灰。
            var legality = ClientLegalityView;
            if (legality?.ActivationAbilities != null && !string.IsNullOrEmpty(e.EffectId))
            {
                bool hasProjectionForCard = false, projected = false;
                foreach (var a in legality.ActivationAbilities)
                {
                    if (a == null || a.SourceUid != inst.UID) continue;
                    hasProjectionForCard = true;
                    if (a.EffectId == e.EffectId) { projected = true; break; }
                }
                if (hasProjectionForCard && !projected) return true;
            }
            // 【2026-09 修复·JC053】结算条件（前置）不满足 → 置灰。与候选侧 `ActivateEligibility.CanActivate`
            // 的 `ConditionAllowsActivation` 同源（单一实现）。
            // ⚠ 纯客户端无 `EffectEngine.Instance`，该方法内部判空后返回 true（本地无法求值）⇒
            // 联机的主判据是上面那条"投影清单里有没有"。
            if (!Script.Manager.ActivateEligibility.ConditionAllowsActivation(e, inst)) return true;
            bool needsTap = !string.IsNullOrEmpty(e.ActivateCondition) && e.ActivateCondition.Contains("横置本角色");
            if (needsTap && inst.IsTapped) return true;
            if (e.Target != null && e.Target.NeedsSelection)
            {
                // 【P2·联机】地区目标 / 玩家目标：候选由**服务端**下发（地区槽位高亮 / 独立选人窗），
                // 客户端本地不判"有无合法目标"（判定权威在服务端，避免两套口径）。
                if (e.Target.Kind == TargetKind.Area) return false;
                if (Script.Manager.PlayerTargetPool.IsPlayerSpec(e.Target)) return false;
                if (ComputeLegalCardTargetUids(inst, e).Count == 0) return true;
            }
            return false;
        }

        /// <summary>置灰原因后缀（与单机 OpenAttachmentActivateChoice 文案一致）。</summary>
        private static string EffectDisabledReason(CardInstance inst, EffectData e)
        {
            // 【2026-09 修复·JC053】前置条件不满足的原因文案（与单机一致）
            if (!Script.Manager.ActivateEligibility.ConditionAllowsActivation(e, inst)) return "（前置条件不满足）";
            bool needsTap = !string.IsNullOrEmpty(e.ActivateCondition) && e.ActivateCondition.Contains("横置本角色");
            if (needsTap && inst.IsTapped) return "（已横置，无法横置自身）";
            return "（无合法目标）";
        }

        /// <summary>统一激活上行入口：**选择类入参（目标地区/代价/玩家目标）由服务端接管逐项询问**（与单机那几套窗 1:1），
        /// 客户端只在"需要**卡目标**"时本地走场上点选（与服务端同源的合法性投影）后带上 targetUid 上行。</summary>
        private void SendActivation(CardInstance inst, EffectData ability)
        {
            if (inst == null || ability == null || _matchClient == null) return;
            string name = inst.PlayerTemplate?.CardName ?? inst.CouncilTemplate?.CardName ?? "卡";

            // 【K24】以**玩家**为目标的激活：候选是玩家号（非场上卡节点），由服务端经 `SelectPlayer` 下发候选
            // ⇒ 客户端直接上行意图，服务端接管（本轮补齐联机激活路径）。
            if (Script.Manager.PlayerTargetPool.IsPlayerSpec(ability.Target))
            {
                _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, inst.UID, ability.EffectId, "");
                GD.Print($"[ArenaOl] 上行 ActivateAbility(玩家目标，服务端选人): {name} effect={ability.EffectId}");
                return;
            }

            if (ability.Target != null && ability.Target.NeedsSelection)
            {
                // 【K15】地区目标：**服务端接管**（下发 `SelectTarget`，客户端走与单机同款"地区槽位高亮 + 点击"窗）；
                // 原先此处直接 `return`（"本轮暂缓"）⇒ JC117 的联机激活完全不可用。
                if (ability.Target.Kind == TargetKind.Area)
                {
                    _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, inst.UID, ability.EffectId, "");
                    GD.Print($"[ArenaOl] 上行 ActivateAbility(地区目标，服务端接管选地区): {name} effect={ability.EffectId}");
                    return;
                }
                BeginActivationTargetSelection(inst, ability);
                return;
            }
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, inst.UID, ability.EffectId, "");
            GD.Print($"[ArenaOl] 上行 ActivateAbility: {name} effect={ability.EffectId}");
        }

        // ==================== 出牌拖拽：合法目标高亮（对齐单机 UpdateDragTargetHighlights） ====================

        private CardUI _handDragHighlightSource;
        private readonly List<CardUI> _handDragHighlighted = new List<CardUI>();

        /// <summary>
        /// 拖拽手牌期间高亮合法目标（对齐单机 `CardDragController.UpdateDragTargetHighlights`）：
        /// 仅在"无蒙版"时生效；合法目标**优先取服务端下发的合法性投影**（`GameViewSnap.PlayTargets`），
        /// 无投影时退回调用与服务端同一份规则函数（`CardManager.IsLegalPlayTarget`）本地求值。
        /// 每帧调用（拖拽是移动过程，无事件可依）。
        /// </summary>
        private void UpdateHandDragTargetHighlights()
        {
            if (_maskMode != MaskMode.None || _activationSelectionBusy || _noticeWindowOpen)
            {
                ClearHandDragTargetHighlights();
                return;
            }

            // 找到"本地玩家正在拖拽的手牌卡"
            CardUI dragging = null;
            var localHand = _localPlayerId == 1 ? _p1Hand : _p2Hand;
            if (localHand != null)
            {
                foreach (var kv in _cardUIMap)
                {
                    var ui = kv.Value;
                    if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                    if (!ui.IsDragging) continue;
                    if (ui.GetParent() != localHand) continue;
                    dragging = ui;
                    break;
                }
            }
            if (dragging?.Instance == null)
            {
                ClearHandDragTargetHighlights();
                return;
            }
            if (!ReferenceEquals(_handDragHighlightSource, dragging)) ClearHandDragTargetHighlights();
            _handDragHighlightSource = dragging;

            var cardMgr = _matchClient?.ClientCardManager;
            if (cardMgr == null) return;

            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (ReferenceEquals(ui, dragging)) continue;
                if (ui.Instance.IsHidden) continue;
                // 高亮范围与落点命中范围一致：仅争夺区/附属区（对齐单机 UpdateDragTargetHighlights 只遍历 AreaCardUIs）
                var zone = ui.Instance.CurrentZone;
                if (zone != CardZone.AreaZone && zone != CardZone.AttachedZone) continue;
                bool legal = IsLegalPlayTargetForClient(dragging.Instance, ui.Instance);
                bool was = _handDragHighlighted.Contains(ui);
                if (legal && !was) { ui.SetTargetHighlight(true); _handDragHighlighted.Add(ui); }
                else if (!legal && was) { ui.SetTargetHighlight(false); _handDragHighlighted.Remove(ui); }
            }

            // 本方秘社为合法目标时高亮（对齐单机 SetCouncilHighlight）
            var ownCouncilUi = (_localPlayerId == 1 ? _councilContainer1 : _councilContainer2) != null
                ? FindOwnCouncilUi()
                : null;
            if (ownCouncilUi?.Instance != null)
                ownCouncilUi.SetTargetHighlight(IsLegalPlayTargetForClient(dragging.Instance, ownCouncilUi.Instance));
        }

        /// <summary>清除拖拽期间的合法目标高亮。</summary>
        private void ClearHandDragTargetHighlights()
        {
            foreach (var ui in _handDragHighlighted)
                if (ui != null && GodotObject.IsInstanceValid(ui)) ui.SetTargetHighlight(false);
            _handDragHighlighted.Clear();
            _handDragHighlightSource = null;
            var ownCouncilUi = FindOwnCouncilUi();
            if (ownCouncilUi != null && GodotObject.IsInstanceValid(ownCouncilUi)) ownCouncilUi.SetTargetHighlight(false);
        }

        /// <summary>取本方秘社卡 UI（秘社 UI 不在 _cardUIMap 中）。</summary>
        private CouncilCardUI FindOwnCouncilUi()
        {
            foreach (var kv in _councilCardUIMap)
            {
                var ui = kv.Value;
                if (ui?.Instance == null || !GodotObject.IsInstanceValid(ui)) continue;
                if (ui.Instance.ControllerId == _localPlayerId) return ui;
            }
            return null;
        }

        /// <summary>计算某激活效果在客户端投影中的合法卡目标 UID 集合（卡目标；地区目标暂缓）。
        /// 【P2·单一判定】优先读服务端投影（`GameViewSnap.AbilityTargets`：服务端用
        /// `ActivateEligibility.IsLegalActivateTarget` 逐个候选算出）；无该条目时退回调用同一份规则函数
        /// 本地求值（输入已在 P0/P1 补齐：本局 PlayerManager + 静止标记 + 运行时授予均随快照到位）。</summary>
        private HashSet<string> ComputeLegalCardTargetUids(CardInstance source, EffectData ability)
        {
            var legal = new HashSet<string>();
            if (source == null || ability?.Target == null || !ability.Target.NeedsSelection) return legal;
            if (ability.Target.Kind == TargetKind.Area) return legal;

            var view = ClientLegalityView;
            if (view?.AbilityTargets != null)
            {
                var proj = view.AbilityTargets.Find(x => x != null && x.SourceUid == source.UID
                                                         && x.EffectId == ability.EffectId);
                if (proj != null)
                {
                    foreach (var u in proj.TargetUids) legal.Add(u);
                    return legal;
                }
            }

            var zone = _matchClient?.ClientZoneManager;
            var cardMgr = _matchClient?.ClientCardManager;
            var playerMgr = _matchClient?.ClientPlayerManager;
            if (zone == null || cardMgr == null) return legal;
            var cards = zone.GetZoneCards(CardZone.AreaZone, 0);
            if (cards == null) return legal;
            foreach (var c in cards)
            {
                if (c == null || c == source) continue;
                if (ability.Target.IsLocalZone && c.ZoneDetailId != source.ZoneDetailId) continue;
                if (ActivateEligibility.IsLegalActivateTarget(source, ability, c, cardMgr, playerMgr)) legal.Add(c.UID);
            }
            return legal;
        }

        /// <summary>进入"场上点击高亮"选目标：计算合法目标、高亮对应卡 UI、打开复用提示窗。</summary>
        private void BeginActivationTargetSelection(CardInstance source, EffectData ability)
        {
            var legal = ComputeLegalCardTargetUids(source, ability);
            if (legal.Count == 0)
            {
                GD.Print($"[ArenaOl] {source.PlayerTemplate?.CardName ?? source.CouncilTemplate?.CardName} 激活效果无合法目标，忽略：{ability.EffectId}");
                _activationSelectionBusy = false;
                return;
            }
            _activationSelectionBusy = true;
            _activationTargetActive = true;
            _activationTargetSource = source;
            _activationTargetAbility = ability;
            _activationLegalCardUids.Clear();
            foreach (var u in legal) _activationLegalCardUids.Add(u);

            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (_activationLegalCardUids.Contains(ui.Instance.UID)) ui.SetTargetHighlight(true);
            }

            EnsureTargetWindow();
            if (_onlineTargetWindow != null)
            {
                _onlineTargetWindow.OnTargetPicked = OnActivationTargetPicked;
                _onlineTargetWindow.SetBlocking(false);   // 点击穿透到场上卡
                _onlineTargetWindow.Open(source.PlayerTemplate?.CardName ?? "激活效果",
                    "请点击场上高亮的合法目标卡完成选择。");
            }
        }

        /// <summary>懒加载并实例化复用的单机 TargetSelectionWindow 场景（不改其类源码）。</summary>
        private void EnsureTargetWindow()
        {
            if (_onlineTargetWindow != null && GodotObject.IsInstanceValid(_onlineTargetWindow)) return;
            if (_targetWindowScene == null)
                _targetWindowScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/TargetSelectionWindow.tscn");
            if (_targetWindowScene == null)
            {
                GD.PrintErr("[ArenaOl] TargetSelectionWindow.tscn 加载失败");
                return;
            }
            _onlineTargetWindow = _targetWindowScene.Instantiate<TargetSelectionWindow>();
            AddChild(_onlineTargetWindow);
        }

        /// <summary>场上选中合法目标后：清高亮/关提示窗/复位状态；按当前待办动作上行意图。</summary>
        private void OnActivationTargetPicked(string targetUid)
        {
            var src = _activationTargetSource;
            var ab = _activationTargetAbility;
            var playUid = _pendingPlayCardUid;      // 事务卡出牌路径（甲1）
            ClearActivationTargetSelection();
            if (string.IsNullOrEmpty(targetUid) || _matchClient == null) return;

            // 路径一：事务卡（需目标出牌）——复刻单机 BeginSorceryTargetSelection 的"选完目标再出牌"
            if (!string.IsNullOrEmpty(playUid))
            {
                _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.PlayCard, playUid, "", targetUid);
                GD.Print($"[ArenaOl] 上行 PlayCard(事务卡+目标): {playUid} target={targetUid}");
                return;
            }

            // 路径二：激活能力（原有）
            if (src == null || ab == null) return;
            string name = src.PlayerTemplate?.CardName ?? src.CouncilTemplate?.CardName ?? "卡";
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, src.UID, ab.EffectId, targetUid);
            GD.Print($"[ArenaOl] 上行 ActivateAbility(+目标): {name} effect={ab.EffectId} target={targetUid}");
        }

        /// <summary>清理场上高亮、关闭提示窗、复位目标选择状态与本地模态锁。</summary>
        private void ClearActivationTargetSelection()
        {
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (_activationLegalCardUids.Contains(ui.Instance.UID)) ui.SetTargetHighlight(false);
            }
            HighlightOnlineCouncilTargets(false);   // 秘社高亮同款清理
            if (_onlineTargetWindow != null && GodotObject.IsInstanceValid(_onlineTargetWindow))
            {
                _onlineTargetWindow.Visible = false;
                _onlineTargetWindow.OnExtra = null;   // 【2026-09·K15】清掉"本槽出口"回调，避免窗口复用时误触发
            }
            ClearOnlineAreaHighlights();
            _activationTargetActive = false;
            _activationTargetAbility = null;
            _activationTargetSource = null;
            _pendingPlayCardUid = null;
            _activationLegalCardUids.Clear();
            _activationLegalAreaUids.Clear();
            _activationSelectionBusy = false;
        }

        // ==================== 事务卡（需目标出牌）场上选目标（对齐单机） ====================

        /// <summary>
        /// 事务卡需选目标时的联机入口（对齐单机 `Arena.BeginSorceryTargetSelection`）：
        /// 单机是"拖出后弹窗选目标"，联机此处复用既有的"场上高亮 + 复用单机 TargetSelectionWindow"基础设施，
        /// 选完目标再上行 PlayCard(cardUid, null, targetUid)；服务端仍做完整校验与结算。
        /// 若场上无合法目标 / 费用不足 → 不发意图（卡留在手牌，视觉上自行归位）。
        /// </summary>
        private void BeginOnlinePlayCardTargetSelection(CardInstance card)
        {
            if (card?.PlayerTemplate == null || _matchClient == null) return;

            // 费用预检（对齐单机：费用不足不弹窗、直接不发动）；用快照投影的费用修正计算
            int cost = Mathf.Max(0, card.PlayerTemplate.Cost - ComputeClientCostMod(card));
            var playerMgr = _matchClient.ClientPlayerManager;
            if (cost > 0 && playerMgr != null && playerMgr.GetAvailableMana(_localPlayerId) < cost)
            {
                GD.Print($"[ArenaOl] 事务卡 {card.PlayerTemplate.CardName} 费用不足（需{cost}），不弹选目标窗");
                return;
            }

            var clientCardMgr = _matchClient.ClientCardManager;
            var zone = _matchClient.ClientZoneManager;
            if (clientCardMgr == null || zone == null) return;

            // 【P2·单一判定】候选优先取服务端投影（`GameViewSnap.PlayTargets`：服务端已用
            // `CardManager.IsLegalPlayTarget` 对完整的候选池算过一遍）；
            // 无投影时退回本地按同一规则函数枚举候选（候选池与单机 Arena.BeginSorceryTargetSelection 一致）。
            var legal = new List<string>();
            var projEntry = ClientLegalityView?.PlayTargets?.Find(x => x != null && x.SourceUid == card.UID);
            if (projEntry != null)
            {
                legal.AddRange(projEntry.TargetUids);
            }
            else
            {
                // 候选：争夺区角色 + 附属区附属 + 秘社 + 资产区 + 墓地（覆盖事务卡可能的目标种类，与单机/LegalityProjection 一致）
                var candidates = new List<CardInstance>(zone.GetZoneCards(CardZone.AreaZone, 0));
                candidates.AddRange(zone.GetZoneCards(CardZone.AttachedZone, 0));
                candidates.AddRange(zone.GetZoneCards(CardZone.CouncilZone, 0));
                candidates.AddRange(zone.GetZoneCards(CardZone.AssetZone, 1));
                candidates.AddRange(zone.GetZoneCards(CardZone.AssetZone, 2));
                candidates.AddRange(zone.GetZoneCards(CardZone.Graveyard, 1));
                candidates.AddRange(zone.GetZoneCards(CardZone.Graveyard, 2));

                foreach (var t in candidates)
                {
                    if (t == null || t == card || t.IsHidden) continue;
                    if (clientCardMgr.IsLegalPlayTarget(card, t)) legal.Add(t.UID);
                }
            }
            if (legal.Count == 0)
            {
                GD.Print($"[ArenaOl] 事务卡 {card.PlayerTemplate.CardName} 无合法目标，不弹出选择");
                return;
            }

            _pendingPlayCardUid = card.UID;
            _activationSelectionBusy = true;
            _activationTargetActive = true;
            _activationLegalCardUids.Clear();
            foreach (var u in legal) _activationLegalCardUids.Add(u);

            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (_activationLegalCardUids.Contains(ui.Instance.UID)) ui.SetTargetHighlight(true);
            }

            EnsureTargetWindow();
            if (_onlineTargetWindow != null)
            {
                _onlineTargetWindow.OnTargetPicked = OnActivationTargetPicked;
                _onlineTargetWindow.SetBlocking(false);
                _onlineTargetWindow.Open(card.PlayerTemplate.CardName ?? "事务卡",
                    "请点击场上高亮的合法目标卡完成选择。");
            }
            GD.Print($"[ArenaOl] 事务卡选目标：{card.PlayerTemplate.CardName} 合法目标={legal.Count}");
        }

        // ==================== 角色卡「拖拽箭头激活」（复刻单机 CardDragController 手势） ====================

        /// <summary>每帧：① 泵送快照应用队列（合并/延后 + 差异路由） ② 维护激活拖拽的箭头与合法目标高亮。</summary>
        public override void _Process(double delta)
        {
            PumpSnapshotQueue(delta);
            UpdateStackArrowEndpoints();   // 堆叠箭头每帧跟随（端点实时更新）
            UpdateHandDragTargetHighlights();   // 拖拽手牌期间的合法目标高亮（对齐单机）

            if (_activateDragSource == null) return;
            // 防御：源卡 UI 若被快照重建销毁，立即取消拖拽（避免引用失效节点）
            if (!GodotObject.IsInstanceValid(_activateDragSource) || _activateDragSource.Instance == null)
            {
                CancelOnlineActivateDrag();
                return;
            }
            UpdateOnlineActivateDrag(GetGlobalMousePosition());
        }

        /// <summary>
        /// 快照应用时机控制（延迟掩盖基础）：
        ///  - 本地交互/动画进行中 → 延后应用（上限 <see cref="SnapshotMaxDeferSeconds"/>，超时强制应用避免长期不同步）；
        ///  - 应用前用 SnapshotDiff 对比上一份已应用快照 → 差异指令交 <see cref="RouteRenderCommands"/>（表现层事件源）。
        /// </summary>
        private void PumpSnapshotQueue(double delta)
        {
            bool localBusy = _activateDragSource != null
                             || _activationTargetActive
                             || _activationSelectionBusy
                             || _noticeWindowOpen;
            if (localBusy)
            {
                _snapshotDeferTimer += delta;
                _snapshotQueue.IsDeferred = _snapshotDeferTimer < SnapshotMaxDeferSeconds;
            }
            else
            {
                _snapshotDeferTimer = 0;
                _snapshotQueue.IsDeferred = false;
            }

            if (!_snapshotQueue.TryDequeue(out var prev, out var next)) return;

            ApplySnapshot(next);
            RouteRenderCommands(Script.Net.SnapshotDiff.Diff(prev, next));
            _snapshotQueue.MarkApplied(next);
        }

        /// <summary>
        /// 表现层事件源：把"前后快照差异"路由给表现层。
        /// 当前实现为最小形态——分类计数 + 摘要（供调试/观测），并作为后续动画系统的接入点：
        /// 将来在此把 AddCard/MoveCard/UpdateCard/StackPush|Pop/PhaseChange/PlayerUpdate 映射为动画即可，
        /// 无需改动快照链路与规则层。
        /// </summary>
        private void RouteRenderCommands(List<Script.Net.RenderCommand> cmds)
        {
            if (cmds == null || cmds.Count == 0) return;

            int add = 0, remove = 0, move = 0, update = 0, stack = 0, player = 0, phase = 0;
            foreach (var c in cmds)
            {
                if (c == null) continue;
                switch (c.Kind)
                {
                    case Script.Net.RenderCommand.CmdKind.AddCard: add++; break;
                    case Script.Net.RenderCommand.CmdKind.RemoveCard: remove++; break;
                    case Script.Net.RenderCommand.CmdKind.MoveCard: move++; break;
                    case Script.Net.RenderCommand.CmdKind.UpdateCard: update++; break;
                    case Script.Net.RenderCommand.CmdKind.StackPush:
                    case Script.Net.RenderCommand.CmdKind.StackPop: stack++; break;
                    case Script.Net.RenderCommand.CmdKind.PlayerUpdate: player++; break;
                    case Script.Net.RenderCommand.CmdKind.PhaseChange: phase++; break;
                }
            }

            LastPresentationSummary =
                $"add={add} remove={remove} move={move} update={update} stack={stack} player={player} phase={phase}";
            if (VerbosePresentation)
                GD.Print($"[ArenaOl][表现] 快照差异 {cmds.Count} 条：{LastPresentationSummary}");
        }

        /// <summary>尝试在点击处起手激活拖拽：命中己方争夺区「可发动」角色卡才成立。
        /// 与单机一致：只用该卡首个 CharacterActivated/AttachmentActivated 能力；必须落到合法目标才发动（本轮不做无目标增强）。</summary>
        private bool TryBeginOnlineActivateDragAt(Vector2 globalPos)
        {
            if (_activationSelectionBusy || _noticeWindowOpen || _activationTargetActive || _activateDragSource != null) return false;

            var hit = PickFieldCardAt(globalPos);
            if (hit?.Instance == null) return false;
            var inst = hit.Instance;
            if (inst.CurrentZone != CardZone.AreaZone) return false;
            if (inst.IsCovert || inst.IsAttached) return false;   // 暗藏现身 / 附属激活各有双击链路，不在此争抢
            if (inst.ControllerId != _localPlayerId) return false; // 输入隔离

            var ability = ResolveOnlineDragAbility(inst);
            if (ability == null) return false;

            // 与单机 CardDragController.TryBeginActivateDragAt 同源门禁：优先权 + 费用 + 生效区 + 行动窗口 + 有合法目标
            // 【P2】改为"服务端投影优先 + 同一规则函数兜底"，客户端不再自行拼输入
            var cardMgr = _matchClient?.ClientCardManager;
            var turn = _matchClient?.ClientTurnManager;
            var player = _matchClient?.ClientPlayerManager;
            if (cardMgr == null || turn == null || player == null) return false;
            if (!IsActivatableForClient(inst)) return false;

            _activateDragSource = hit;
            _activateDragAbility = ability;
            _activateLegalUids.Clear();
            foreach (var u in ComputeLegalCardTargetUids(inst, ability)) _activateLegalUids.Add(u);
            _activationSelectionBusy = true;   // 拖拽期间本地模态：禁止让过/结束回合/重复起手

            if (_arrowLayer != null && _stackArrowScene != null)
            {
                _activateArrow = _stackArrowScene.Instantiate<StackArrow>();
                _arrowLayer.AddChild(_activateArrow);
            }
            UpdateOnlineActivateDrag(globalPos);
            GD.Print($"[ArenaOl] 激活拖拽起手: {inst.PlayerTemplate?.CardName} 能力={ability.EffectId} 合法目标={_activateLegalUids.Count}");
            return true;
        }

        /// <summary>拖拽中：箭头从源卡视觉中心指向鼠标，并按合法目标集合刷新高亮。</summary>
        private void UpdateOnlineActivateDrag(Vector2 globalPos)
        {
            if (_activateDragSource == null) return;
            if (_activateArrow != null && GodotObject.IsInstanceValid(_activateArrow))
                _activateArrow.SetPoints(_activateDragSource.GetVisualGlobalRect().GetCenter(), globalPos);

            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                bool legal = _activateLegalUids.Contains(ui.Instance.UID);
                ui.SetTargetHighlight(legal);
                if (legal && !_activateHighlighted.Contains(ui)) _activateHighlighted.Add(ui);
            }
        }

        /// <summary>放手：落点为合法目标则上行 ActivateAbility(sourceUid, effectId, targetUid)；否则取消（与单机 EndActivateDrag 一致）。</summary>
        private void EndOnlineActivateDrag(Vector2 globalPos)
        {
            var src = _activateDragSource;
            var ab = _activateDragAbility;
            string targetUid = PickLegalTargetAt(globalPos);
            bool ok = src?.Instance != null && ab != null
                      && !string.IsNullOrEmpty(targetUid) && targetUid != src.Instance.UID;
            string srcUid = src?.Instance?.UID;
            string srcName = src?.Instance?.PlayerTemplate?.CardName;
            string effectId = ab?.EffectId;
            CancelOnlineActivateDrag();

            if (!ok || _matchClient == null)
            {
                GD.Print("[ArenaOl] 激活拖拽取消（落点非合法目标）");
                return;
            }
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, srcUid, effectId, targetUid);
            GD.Print($"[ArenaOl] 上行 ActivateAbility(拖拽): {srcName} effect={effectId} target={targetUid}");
        }

        /// <summary>清理激活拖拽：清高亮、释放箭头、复位状态与本地模态锁。</summary>
        private void CancelOnlineActivateDrag()
        {
            foreach (var ui in _activateHighlighted)
                if (ui != null && GodotObject.IsInstanceValid(ui)) ui.SetTargetHighlight(false);
            _activateHighlighted.Clear();
            if (_activateArrow != null && GodotObject.IsInstanceValid(_activateArrow)) _activateArrow.QueueFree();
            _activateArrow = null;
            _activateDragSource = null;
            _activateDragAbility = null;
            _activateLegalUids.Clear();
            _activationSelectionBusy = false;
        }

        /// <summary>源卡首个角色/附属激活能力（**模板 ∪ 运行期授予**，与单机 `CardDragController.GetActivatingAbility` 同源）。</summary>
        private static EffectData GetFirstOnlineActivateAbility(CardInstance inst)
            => ActivateEligibility.GetFirstActivateAbility(inst);

        /// <summary>
        /// 【2026-09 修复·JC019 宿主无法拖拽】解析"本次拖拽要发动的能力"：
        ///  ① 本地判定优先（单机/宿主进程：`GetAllActivateAbilities` 含运行期授予）——
        ///     与单机同款：**恰好 1 条能力 ＋ 需选场上卡目标**（多能力走双击菜单、无目标/自指走双击直接发动、
        ///     玩家/地区目标不走卡拖拽），否则返回 null（**不吞左键**）；
        ///  ② 本地判定不成立时，退回**服务端投影**（`GameViewSnap.ActivationAbilities`）：
        ///     纯客户端拿不到服务端的"运行期授予"表（JC019 授予宿主的能力）⇒ 本地能力数是 0，
        ///     此时必须按服务端下发的"可否拖拽 + EffectId"起手，否则该效果在联机下**永远无法拖拽**。
        ///     为目标判定安全，本条要求服务端**同时**下发了该能力的合法目标条目（`AbilityTargets`），
        ///     否则 fail-closed 返回 null（不自行放宽目标限制）。
        /// </summary>
        private EffectData ResolveOnlineDragAbility(CardInstance inst)
        {
            if (inst == null) return null;
            if (ActivateEligibility.CanDragActivate(inst, out var localAbility)) return localAbility;

            var view = ClientLegalityView;
            if (view?.ActivationAbilities == null) return null;
            string effectId = null;
            foreach (var a in view.ActivationAbilities)
            {
                if (a == null || a.SourceUid != inst.UID || !a.Draggable) continue;
                if (effectId != null) return null;   // 多条可拖拽能力（异常/未投影齐）→ 交给双击菜单
                effectId = a.EffectId;
            }
            if (string.IsNullOrEmpty(effectId)) return null;
            bool hasTargets = view.AbilityTargets != null
                && view.AbilityTargets.Exists(t => t != null && t.SourceUid == inst.UID && t.EffectId == effectId);
            if (!hasTargets) return null;   // 无服务端目标投影 → 不起手（不猜目标限制）

            // 代理能力数据：**只用 EffectId + "需选卡目标"**（真实限制与候选全部来自服务端投影，
            // 见 `ComputeLegalCardTargetUids` 的投影优先分支；上行意图只带 EffectId）。
            return new EffectData
            {
                EffectId = effectId,
                Timing = EffectTiming.CharacterActivated,
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
            };
        }

        /// <summary>
        /// 某张**本地手牌**在当前蒙版下是否允许被拖起（对齐单机 `CardDragController.IsDraggableUnderMask`）：
        ///  - 派遣暗藏：复用规则层同一静态函数 `DeployCovertEligibility.IsEligible`（非角色卡 / 带【公开】/ 被全局封锁 → 不可拖起）；
        ///  - 建立资产 / 无蒙版：均可拖起。
        /// 注：动作内的阶段/行动权/次数校验不在客户端复刻，非法操作仍由服务端拒绝并回执提示。
        /// 【P1】全局封锁秘密派遣（JC068-B）由快照投影态作为入参喂给同一函数：
        /// 原先客户端对此完全无感（能拖起、等服务端拒绝），现在与服务端同解。
        /// </summary>
        private bool IsDraggableUnderOnlineMask(CardInstance inst)
        {
            if (inst == null) return false;
            return _maskMode switch
            {
                MaskMode.DeployCovert => DeployCovertEligibility.IsEligible(inst,
                    _matchClient?.ClientPlayerManager?.IsDeployCovertBlockedProjected(_localPlayerId) == true),
                _ => true,
            };
        }

        /// <summary>按当前蒙版刷新"本地手牌是否可拖起" + 手牌灰/绿蒙版视觉（对齐单机 RefreshHandMask）。
        /// 进入/退出蒙版时调用；手牌每帧不会重建，故需主动刷新。</summary>
        private void ApplyOnlineMaskDragGate()
        {
            if (_localHand == null || !GodotObject.IsInstanceValid(_localHand)) return;
            foreach (var child in _localHand.GetChildren())
            {
                if (child is not CardUI ui || !GodotObject.IsInstanceValid(ui)) continue;
                if (ui.Instance == null) { ui.CanDrag = false; ui.SetMask(null); continue; }
                ui.CanDrag = !ui.Instance.IsHidden && IsDraggableUnderOnlineMask(ui.Instance);
                ApplyOnlineMaskToOneCard(ui);
            }
        }

        /// <summary>按当前蒙版为单张手牌设置灰/绿蒙版（对齐单机 Arena.Mask.ApplyMaskToOneCard）。</summary>
        private void ApplyOnlineMaskToOneCard(CardUI card)
        {
            if (card == null) return;
            var grey = new Color(0.01f, 0.01f, 0.01f, 0.90f);   // 灰色蒙版（派遣暗藏·角色卡，加深）
            var green = new Color(0.2f, 0.8f, 0.3f, 0.55f);      // 绿色蒙版（建立资产·所有手牌）

            switch (_maskMode)
            {
                case MaskMode.DeployCovert:
                    // 仅"可派遣暗藏"的卡打灰色蒙版（角色卡且无【公开】且未被全局封锁）；
                    // 事务/附属/公开卡不打蒙版（不可暗藏）。
                    bool canCovert = DeployCovertEligibility.IsEligible(card.Instance,
                        _matchClient?.ClientPlayerManager?.IsDeployCovertBlockedProjected(_localPlayerId) == true);
                    card.SetMask(canCovert ? grey : (Color?)null);
                    break;
                case MaskMode.EstablishAsset:
                    card.SetMask(green);
                    break;
                default:
                    card.SetMask(null);
                    break;
            }
        }

        /// <summary>命中"场上卡"目标（**仅争夺区/附属区**，不含手牌/牌库/墓地，跳过隐藏占位；
        /// 对齐单机 `CardDragController.PickAreaCardAt` 的命中范围：AreaCardUIs + AttachedCardUIs）。</summary>
        private CardUI PickFieldCardAt(Vector2 globalPos)
        {
            CardUI best = null;
            int bestZ = int.MinValue;
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui) || ui.Instance == null) continue;
                if (ui.Instance.IsHidden) continue;
                var zone = ui.Instance.CurrentZone;
                if (zone != CardZone.AreaZone && zone != CardZone.AttachedZone) continue;
                if (!ui.GetVisualGlobalRect().HasPoint(globalPos)) continue;
                if (ui.ZIndex >= bestZ) { bestZ = ui.ZIndex; best = ui; }
            }
            return best;
        }

        /// <summary>命中点击处的合法目标卡 UID（激活拖拽落点判定）。</summary>
        private string PickLegalTargetAt(Vector2 globalPos)
        {
            var hit = PickFieldCardAt(globalPos);
            if (hit?.Instance == null) return null;
            return _activateLegalUids.Contains(hit.Instance.UID) ? hit.Instance.UID : null;
        }

        /// <summary>客户端按快照投影的费用修正计算某张卡的减费额。
        /// 【P1·同一判定】匹配规则改调服务端同一份 `CostModFilter.Matches`（旧语义：卡名含/组织等；
        /// 扩展语义：组织:/子类别:/域:/有域图标），避免"客户端显示 -1、服务端按 -0 结算"的两套逻辑。
        /// 纯展示，不参与结算。</summary>
        private int ComputeClientCostMod(CardInstance inst)
        {
            var mods = _matchClient?.ClientCostMods;
            if (mods == null || mods.Count == 0 || inst?.PlayerTemplate == null || inst.IsHidden) return 0;
            var tmpl = inst.PlayerTemplate;
            int total = 0;
            foreach (var m in mods)
            {
                if (m == null || m.PlayerId != inst.ControllerId) continue;
                if (Script.Manager.CostModFilter.Matches(m.FilterText, inst, tmpl.CardName, tmpl.Organization))
                    total += m.Amount;
            }
            return total;
        }

        /// <summary>重建世界牌库（地区牌堆）：用 WorldDeckZone 组件显示卡背堆叠 + 数量。</summary>
        private void RebuildWorldDeck()
        {
            if (_worldDeckZone == null) return;
            var wm = _matchClient?.ClientWorldDeckManager;
            if (wm == null) return;
            _worldDeckZone.Initialize(wm.Count, _areaCardUIScene);
        }

        /// <summary>重建场上地区：用 AreaSlot 槽位组件显示地区卡正面（与单机一致）。
        /// 地区数据在客户端投影 AreaManager（GetOrderedAreaInstances 按槽位排序），不在 ZoneManager 的 AreaZone 桶。</summary>
        private void RebuildAreas()
        {
            var areaMgr = _matchClient?.ClientAreaManager;
            if (areaMgr == null) return;
            var areaInstances = areaMgr.GetOrderedAreaInstances();

            // 清空旧槽位
            foreach (var slot in _areaSlots)
                if (slot != null && GodotObject.IsInstanceValid(slot)) slot.QueueFree();
            _areaSlots.Clear();
            if (_areasContainer != null)
                foreach (Node child in _areasContainer.GetChildren())
                    child.QueueFree();

            if (areaInstances == null) return;

            for (int i = 0; i < areaInstances.Count; i++)
            {
                var inst = areaInstances[i];
                if (inst?.AreaTemplate == null) continue;
                var slot = _areaSlotScene.Instantiate<AreaSlot>();
                _areasContainer.AddChild(slot);
                slot.Name = $"AreaSlot_{i + 1}";
                // 【对齐单机】注入统计依赖（客户端投影的 Zone/Player Manager）→ 地区"调查/战斗/势力"统计可显示。
                // AreaStatsCalculator 只依赖这两个 Manager，且客户端 PlayerManager 已注入投影 ZoneManager
                // 并已由快照设置先手（SetFirstPlayer），故统计与单机同源同值。
                slot.SetStatsDependencies(_matchClient?.ClientZoneManager, _matchClient?.ClientPlayerManager);
                slot.Setup(inst, i + 1, false, _areaCardUIScene);
                _areaSlots.Add(slot);
            }
            ArrangeAreas();
        }

        /// <summary>地区槽位排版（复刻单机 Arena.ArrangeAreas：放得下居中，放不下重叠右压左）。</summary>
        private void ArrangeAreas()
        {
            if (_areaSlots.Count == 0 || _areasContainer == null) return;
            float containerWidth = _areasContainer.Size.X;
            if (containerWidth <= 0) containerWidth = 1920;

            int count = _areaSlots.Count;
            const float slotWidth = 400f;
            const float slotHeight = 580f;
            const float spacing = 30f;

            float startX, step;
            if (count == 1)
            {
                startX = System.Math.Max(0f, (containerWidth - slotWidth) / 2f);
                step = 0f;
            }
            else
            {
                float idealTotal = count * slotWidth + (count - 1) * spacing;
                if (idealTotal <= containerWidth)
                {
                    startX = (containerWidth - idealTotal) / 2f;
                    step = slotWidth + spacing;
                }
                else
                {
                    startX = 0f;
                    step = (containerWidth - slotWidth) / (count - 1f);
                    if (step < 0f) step = 0f;
                }
            }

            for (int i = 0; i < count; i++)
            {
                var slot = _areaSlots[i];
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                // 【2026-10·对齐单机 Arena.Layout.ArrangeAreas】单机在写完 Size 后还做两件事：
                //  ① anchors 归零 —— 防止场景里残留的 anchor 让 Size/Position 被布局系统二次修正；
                //  ② UpdateLayout() —— 按槽位当前 Size 重算 Player1Area/CardContainer/Player2Area 的
                //     Size 与 Position。当前 tscn 默认恰好是 400x580 所以数值等价，但一旦槽位尺寸变化
                //     （如未来窄屏压缩）不重算就会让子容器与视觉错位。
                slot.Position = new Vector2(startX + i * step, 0);
                slot.Size = new Vector2(slotWidth, slotHeight);
                slot.AnchorLeft = 0; slot.AnchorRight = 0;
                slot.AnchorTop = 0; slot.AnchorBottom = 0;
                slot.ZIndex = Layers.AreaSlotBase + i;   // 右侧地区压在左侧地区上方（基准层由 Layers 统一分配）
                slot.ZAsRelative = false;
                slot.UpdateLayout();
            }
        }

        private void RebuildInto(Control container, System.Collections.Generic.IEnumerable<CardInstance> instances)
        {
            if (container == null || instances == null) return;
            // 重建前先立即清空容器旧 CardUI 子节点（移除并释放），
            // 避免 QueueFree 延迟释放导致旧卡（如建立资产后被移走的卡）仍残留在容器里、被 ArrangeAll 排布显示。
            foreach (Node child in container.GetChildren())
                if (child is CardUI oc && GodotObject.IsInstanceValid(oc))
                {
                    container.RemoveChild(oc);
                    oc.QueueFree();
                }

            bool isLocalHand = (container == _p1Hand && _localPlayerId == 1) || (container == _p2Hand && _localPlayerId == 2);
            if (isLocalHand) { _localHand = container; _localHandOwner = _localPlayerId; }
            foreach (var inst in instances)
            {
                if (inst == null || _cardUIMap.ContainsKey(inst.UID)) continue;
                // 【Phase B 信息隔离】隐藏区占位（对手手牌）：只画卡背、不参与交互与减费显示。
                if (inst.IsHidden)
                {
                    var hiddenUi = _cardUIScene.Instantiate<CardUI>();
                    container.AddChild(hiddenUi);
                    hiddenUi.Setup(null);
                    hiddenUi.SetFace(false);
                    hiddenUi.CanDrag = false;
                    _cardUIMap[inst.UID] = hiddenUi;
                    continue;
                }
                // CardUI 只支持玩家卡（PlayerCardData）；地区卡/秘社卡 PlayerTemplate 为 null，跳过避免误报/背面
                if (inst.PlayerTemplate == null) continue;
                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                SetupCardUI(ui, inst);
                ui.SetExternalCostMod(ComputeClientCostMod(inst));   // 手牌减费提示（快照投影，仅展示）
                // 输入隔离：仅本地玩家的手牌可拖拽出牌/派遣/资产；对手手牌只读展示
                // 蒙版资格预检（对齐单机 CardDragController.IsDraggableUnderMask → TurnManager.CanDeployCovert）：
                // 派遣暗藏蒙版下，不具备资格的卡（非角色卡 / 带【公开】）不允许被拖起。
                ui.CanDrag = isLocalHand && IsDraggableUnderOnlineMask(inst);
                if (isLocalHand)
                    ui.CardDragEnded += OnLocalHandCardDragEnded;
                RegisterOnlineCardUI(inst, ui);
            }
        }

        private void ClearAllUI()
        {
            // 清除手牌/墓地/地区动态节点（牌库/世界牌库由对应组件 Initialize 时自建/自清）
            foreach (var kv in _cardUIMap)
                if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
            _cardUIMap.Clear();
            foreach (var kv in _councilCardUIMap)
                if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
            _councilCardUIMap.Clear();
            foreach (var slot in _areaSlots)
                if (slot != null && GodotObject.IsInstanceValid(slot)) slot.QueueFree();
            _areaSlots.Clear();
        }

        private void ArrangeAll()
        {
            ArrangeChildren(_p1Hand);
            ArrangeChildren(_p2Hand);
            ArrangeAreas();
        }

        private void ArrangeChildren(Control container)
        {
            if (container == null) return;
            var cards = new List<Control>();
            foreach (var child in container.GetChildren())
                if (child is CardUI c && GodotObject.IsInstanceValid(c)) cards.Add(c);
            CardLayoutHelper.LayoutCardsEvenly(container, cards);
        }

        /// <summary>
        /// 【2026-10】快照重建收尾：把所有卡 UI 的平滑目标（`TargetPosition`）同步到其**当前**全局位置。
        ///
        /// 为何需要这道统一兜底：
        ///  ① **旋转卡的枢轴项**：旋转 Control 的 transform origin = Position + pivot − R·pivot，
        ///     90° 时 GlobalPosition = Position + (120,20)。排版虽已用 CardLayoutHelper.SetSmoothTarget
        ///     （读实时 GlobalPosition）写对，但下面 ② 会让它立刻失效；
        ///  ② **联机独有的"排完卡之后再移槽位"**：ArrangeAll → ArrangeAreas 会再次写 `slot.Position/Size`，
        ///     卡片是槽位的子孙 ⇒ 全局坐标随之改变 ⇒ 排版瞬间写入的目标值变成**陈旧值**，
        ///     `CardUI._Process` 每帧 Lerp 会把整排卡拉回旧全局点（横置卡尤其明显）。
        /// 自锁后横置宿主与竖置附属都不再漂移，"宿主漂移、附属不动"的分离现象随之消失。
        ///
        /// 跳过正在拖拽的卡：其 `TargetPosition` 承担"拖拽取消后归位"的职责，不可覆盖。
        /// 仅每次快照重建后执行一次，O(n)，无每帧开销。
        /// </summary>
        private void SyncSmoothTargets()
        {
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                if (ui.IsDragging) continue;   // 拖拽中的卡由 CardUI.CancelDrag 负责归位
                ui.TargetPosition = ui.GlobalPosition;
            }
            foreach (var kv in _councilCardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                ui.TargetPosition = ui.GlobalPosition;
            }
        }

        /// <summary>
        /// 诊断：抽样式打印最多 2 张横置卡的坐标三元组（Position / GlobalPosition / TargetPosition）。
        /// 正常应恒有 `GlobalPosition == TargetPosition`，且二者 ≈ `Position + 父容器全局 + (120,20)`（横置枢轴项）。
        /// 若 `GlobalPosition != TargetPosition`，说明仍有环节在排版之后改写了其中之一 ⇒ 横置卡还会漂移。
        /// </summary>
        private void LogTappedCardSample()
        {
            int printed = 0;
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                if (!CardLayoutHelper.IsTapped(ui)) continue;
                GD.Print($"[ArenaOl] 横置卡采样 {ui.Instance?.UID}：Pos={ui.Position}，"
                    + $"Global={ui.GlobalPosition}，Target={ui.TargetPosition}，Rot={ui.RotationDegrees}");
                if (++printed >= 2) break;
            }
        }

        // ==================== 操作输入（意图上行，含输入隔离） ====================
        // 输入隔离：联机下每个客户端进程只代表一个玩家（_localPlayerId，由 AttachLauncher 传入）。
        //  1) 所有意图上行必须用本地玩家身份：DoPass/DoEndTurn 只允许 playerId == _localPlayerId。
        //  2) 权威隔离在服务端：IntentHub.HandleIntent 强校验 intent.PlayerId == 连接映射(reportedPlayerId)，
        //     本进程即使误传对方玩家号，服务端也会以"越权"拒绝（双重防线，客户端校验是防呆，服务端校验是权威）。
        /// <summary>是否为本地玩家（本进程可操作的对象）。</summary>
        public bool IsLocalPlayer(int playerId) => playerId == _localPlayerId;

        /// <summary>本地玩家让过。仅允许操作自己，越权忽略。</summary>
        public void DoPass(int playerId)
        {
            HealStaleLocalModalFlag();
            if (_activationSelectionBusy || _noticeWindowOpen) { GD.Print($"[ArenaOl] ⏸ 交互被本地模态吞掉（busy={_activationSelectionBusy} notice={_noticeWindowOpen}）→ 已提示并自愈检查"); return; }   // 本地模态：效果选择/选目标/提示窗期间禁止让过
            if (_matchClient == null || !IsLocalPlayer(playerId)) return;   // 输入隔离：只操作本地玩家
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.Pass);
        }

        /// <summary>本地玩家结束回合/让过推进阶段。仅允许操作自己，越权忽略。</summary>
        public void DoEndTurn(int playerId)
        {
            HealStaleLocalModalFlag();
            if (_activationSelectionBusy || _noticeWindowOpen) { GD.Print($"[ArenaOl] ⏸ 交互被本地模态吞掉（busy={_activationSelectionBusy} notice={_noticeWindowOpen}）→ 已提示并自愈检查"); return; }   // 本地模态：效果选择/选目标/提示窗期间禁止结束回合
            if (_matchClient == null || !IsLocalPlayer(playerId)) return;   // 输入隔离：只操作本地玩家
            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.Pass);
        }

        // ==================== 联机拖拽落点判定 + 意图上行 ====================

        /// <summary>切换派遣/资产蒙版模式。再次点击同按钮退出蒙版。</summary>
        private void ToggleMask(MaskMode mode)
        {
            _maskMode = (_maskMode == mode) ? MaskMode.None : mode;
            // 按钮高亮：**两个按钮一起刷**（对齐单机 UpdateActionButtons）。
            // 【2026-10 修复】原先只刷被点击的那个按钮 ⇒ 先点"派遣暗藏"再点"建立资产"时两按钮同时高亮。
            UpdateOnlineActionButtons();
            // 进入蒙版即清除残留的手牌拖拽目标高亮（对齐单机 EnterMask → ClearDragTargetHighlights），
            // 避免建立资产/派遣暗藏拖拽时旧黄色高亮残留。
            if (_maskMode != MaskMode.None) ClearHandDragTargetHighlights();
            ApplyOnlineMaskDragGate();   // 按新蒙版刷新手牌"可拖起"资格 + 灰/绿蒙版视觉（对齐单机 RefreshHandMask）
            GD.Print($"[ArenaOl] 蒙版模式 => {_maskMode}（{(_maskMode == MaskMode.DeployCovert ? "请拖手牌到己方争夺区" : _maskMode == MaskMode.EstablishAsset ? "请拖出手牌建立资产" : "无")}）");
        }

        /// <summary>按当前蒙版统一刷新"派遣暗藏/建立资产"两个按钮的高亮（对齐单机 `Arena.Mask.UpdateActionButtons`）。
        /// 联机无 charge 概念，只刷 Modulate：命中当前蒙版＝浅绿 (0.7,1,0.7)，否则复位白色。</summary>
        private void UpdateOnlineActionButtons()
        {
            var active = new Color(0.7f, 1f, 0.7f);
            if (_deployCovertBtn != null && GodotObject.IsInstanceValid(_deployCovertBtn))
                _deployCovertBtn.Modulate = (_maskMode == MaskMode.DeployCovert) ? active : Colors.White;
            if (_establishAssetBtn != null && GodotObject.IsInstanceValid(_establishAssetBtn))
                _establishAssetBtn.Modulate = (_maskMode == MaskMode.EstablishAsset) ? active : Colors.White;
        }

        /// <summary>退出蒙版（回到普通可出牌状态）并刷新手牌蒙版与可拖起资格（对齐单机 ExitMask → RefreshMaskUI）。</summary>
        private void ExitOnlineMask()
        {
            if (_maskMode == MaskMode.None) return;
            _maskMode = MaskMode.None;
            ApplyOnlineMaskDragGate();   // 清除手牌灰/绿蒙版 + 恢复普通可拖起
            UpdateOnlineActionButtons(); // 按钮高亮复位（落点取消/意图上行后同样要复位，避免残留）
        }

        /// <summary>
        /// 本地玩家手牌拖拽结束：**严格对齐单机 `CardDragController.OnCardDragEnded` 的落点语义**：
        ///  ① 拖回自己手牌区 → 取消（不发意图，卡自行归位）；
        ///  ② 蒙版模式（派遣暗藏/建立资产）→ 专属分支；
        ///  ③ 目标卡：**仅当为合法打出目标**才取（秘社同理）——判据见 `IsLegalPlayTargetForClient`
        ///     （服务端投影优先 + 同一规则函数兜底）；
        ///  ④ 地区落点：角色卡取"己方争夺区"；其它卡仅当其允许 Area 目标时才取（对齐单机 D48）；
        ///  ⑤ 角色卡未落在己方争夺区 → 不发意图（等价单机"退回手牌"）；
        ///  ⑥ 事务卡需目标且两者皆空 → 弹出选目标（对齐单机 `BeginSorceryTargetSelection`）；
        ///  ⑦ 其它需目标卡两者皆空 → 不发意图（等价单机退回手牌）。
        /// 注：CardUI.EndDrag 的第二个参数固定为 false（单机也不检查该参数），故按卡当前位置判定落点。
        /// </summary>
        private void OnLocalHandCardDragEnded(CardUI cardUI, bool dropped)
        {
            HealStaleLocalModalFlag();
            if (_activationSelectionBusy || _noticeWindowOpen) { GD.Print($"[ArenaOl] ⏸ 交互被本地模态吞掉（busy={_activationSelectionBusy} notice={_noticeWindowOpen}）→ 已提示并自愈检查"); return; }   // 本地模态：效果选择/选目标/提示窗/对抗窗口期间禁止拖拽出牌
            if (cardUI?.Instance == null) return;
            if (_matchClient == null || !IsLocalPlayer(_localHandOwner)) return;   // 输入隔离：只操作本地玩家
            var inst = cardUI.Instance;
            var tmpl = inst.PlayerTemplate;
            if (tmpl == null) return;
            var cardUid = inst.UID;
            // 拖拽结束瞬间卡仍位于放手位置（CardLayoutHelper 平滑移动目标在下次快照重建后才会更新）
            Vector2 dropPos = cardUI.GetVisualGlobalRect().GetCenter();

            // ① 拖回自己手牌区 = 取消打出（对齐单机）
            if (_localHand != null && _localHand.GetGlobalRect().HasPoint(dropPos))
            {
                GD.Print($"[ArenaOl] 拖回手牌区，取消出牌：{tmpl.CardName}");
                return;
            }

            // ② 蒙版模式（与单机 HandleMaskDrop 同义）
            if (_maskMode == MaskMode.DeployCovert)
            {
                var areaUid = FindOwnAreaUidAt(dropPos);
                if (string.IsNullOrEmpty(areaUid))
                {
                    GD.Print("[ArenaOl] 派遣暗藏需拖到己方争夺区（服务端将拒绝非法落点）");
                    ExitOnlineMask();
                    return;
                }
                _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.DeployCovert, cardUid, areaUid);
                ExitOnlineMask();
                GD.Print($"[ArenaOl] 上行 DeployCovert: {inst.PlayerTemplate?.CardName} → 地区 {areaUid}");
                return;
            }

            if (_maskMode == MaskMode.EstablishAsset)
            {
                // 对齐单机 HandleMaskDrop：建立资产需落到「己方资产区」或「任一争夺区」，否则取消（不发意图）
                var ownAsset = _localPlayerId == 1 ? _assetContainer1 : _assetContainer2;
                bool droppedOnAsset = ownAsset != null && GodotObject.IsInstanceValid(ownAsset)
                                      && ownAsset.GetGlobalRect().HasPoint(dropPos);
                bool droppedOnArea = !string.IsNullOrEmpty(FindTargetAreaUidAt(dropPos));
                if (!droppedOnAsset && !droppedOnArea)
                {
                    GD.Print("[ArenaOl] 建立资产：需拖到己方资产区或争夺区，本次不发意图");
                    ExitOnlineMask();
                    return;
                }
                _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.EstablishAsset, cardUid);
                ExitOnlineMask();
                GD.Print($"[ArenaOl] 上行 EstablishAsset: {inst.PlayerTemplate?.CardName}");
                return;
            }

            var clientCardMgr = _matchClient.ClientCardManager;
            bool isCharacter = (tmpl.CategoryFlag & CardCategory.Character) != 0;
            bool requiresTarget = tmpl.GetRequiresTarget();

            // ③ 目标卡：仅"合法打出目标"才算命中（对齐单机 IsLegalCardTarget；避免把非法落点直接发给服务端）
            // 【P2】合法性判据优先取服务端投影（IsLegalPlayTargetForClient 内部：投影优先 + 同一规则函数兜底）
            string targetCardUid = "";
            var hitUi = PickFieldCardAt(dropPos);   // 仅争夺区/附属区（不含手牌，对齐单机 PickAreaCardAt）
            if (hitUi?.Instance != null && clientCardMgr != null && IsLegalPlayTargetForClient(inst, hitUi.Instance))
                targetCardUid = hitUi.Instance.UID;
            // 秘社（CouncilCardUI 不在 _cardUIMap）：命中本方秘社区且为合法目标时才取（对齐单机）
            if (string.IsNullOrEmpty(targetCardUid) && clientCardMgr != null)
            {
                var councilContainer = _localPlayerId == 1 ? _councilContainer1 : _councilContainer2;
                if (councilContainer != null && GodotObject.IsInstanceValid(councilContainer)
                    && councilContainer.GetGlobalRect().HasPoint(dropPos))
                {
                    var councilCards = _matchClient.ClientZoneManager?.GetZoneCards(CardZone.CouncilZone, _localPlayerId);
                    var councilInst = councilCards?.Find(c => c != null);
                    if (councilInst != null && IsLegalPlayTargetForClient(inst, councilInst))
                        targetCardUid = councilInst.UID;
                }
            }

            // ④ 地区落点：角色卡=己方争夺区；其它卡仅当允许 Area 目标（对齐单机 D48）
            string targetAreaUid = "";
            if (string.IsNullOrEmpty(targetCardUid))
            {
                if (isCharacter) targetAreaUid = FindOwnAreaUidAt(dropPos);
                else if (tmpl.GetAllowedTargets().Contains(TargetKind.Area)) targetAreaUid = FindTargetAreaUidAt(dropPos);
            }

            // ⑤ 角色卡必须落在己方争夺区，否则不发意图（等价单机"退回手牌"）
            if (isCharacter && string.IsNullOrEmpty(targetAreaUid))
            {
                GD.Print($"[ArenaOl] 角色卡需落在己方争夺区，本次不发意图：{tmpl.CardName}");
                return;
            }

            // ⑥ 事务卡（需目标）：拖出后弹窗选目标（对齐单机 BeginSorceryTargetSelection）
            if (IsSorceryRequiringTarget(inst)
                && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaUid))
            {
                // 【2026-09 修复·用户实测 JC063（联机，用户报 3 个窗）】**声明了"选择一项"模式的事务卡必须
                // 先选模式、再按所选模式取目标**——不能先弹"通用选目标"框。原实现先走
                // `BeginOnlinePlayCardTargetSelection`（通用选目标框），玩家点完目标上行 `PlayCard` 后
                // 服务端才发现"需要先选择一项模式"→ 又下发模式请求 → 再下发该模式的目标请求，
                // 于是"通用框 → 模式框 → 目标框"三个窗（用户口径："第一个选择目标框是通用框，在这个效果里不需要"）。
                // 修法＝与单机 `CardDragController.DragEnd` 的 A110② 同款：模式卡直接上行"裸打出意图"，
                // 由服务端按 `SelectEffectMode` → 该模式目标 的正确顺序驱动（**最多 2 个窗**）。
                var modeEffect = Script.Manager.MultiTargetSlots.FindModeEffect(
                    tmpl, Script.Data.Effects.EffectTiming.OnSorceryCast);
                if (modeEffect != null)
                {
                    _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.PlayCard, cardUid, "", "");
                    GD.Print($"[ArenaOl] 【K16 模式分支】上行 PlayCard（无目标，由服务端先驱动『选择一项』再取该模式目标）: {tmpl.CardName}");
                    return;
                }
                // 【K15·多目标】声明了 TargetSlots 的卡：客户端**不本地取目标**，只发"裸打出意图"，
                // 由服务端一次性聚合下发"一个窗多选"请求（JC062/JC021 ⇒ `Kind.FieldMultiPick`；
                // JC092/JC133 等区域牌堆形状 ⇒ 仍逐槽 + 确认面板）。用户 2026-09 口径。
                if (IsMultiTargetCard(inst))
                {
                    _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.PlayCard, cardUid, "", "");
                    GD.Print($"[ArenaOl] 【K15 多目标】上行 PlayCard（无目标，由服务端驱动取目标）: {tmpl.CardName}");
                    return;
                }
                BeginOnlinePlayCardTargetSelection(inst);
                return;
            }

            // ⑦ 其它需目标卡未命中合法目标 → 不发意图（等价单机退回手牌）
            if (requiresTarget && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaUid))
            {
                GD.Print($"[ArenaOl] {tmpl.CardName} 需要目标但未命中合法目标，本次不发意图");
                return;
            }

            _matchClient.SendIntentAs(_localPlayerId, PlayerIntent.Kind.PlayCard, cardUid,
                targetAreaUid ?? "", targetCardUid ?? "");
            GD.Print($"[ArenaOl] 上行 PlayCard: {tmpl.CardName} targetArea={targetAreaUid ?? "无"} targetCard={targetCardUid ?? "无"}");
        }

        /// <summary>查找落点命中的地区槽位（任一侧争夺区容器内即算命中，用于结附地区类卡）。</summary>
        private string FindTargetAreaUidAt(Vector2 globalPos)
        {
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                bool hit = (slot.Player1Area != null && slot.Player1Area.GetGlobalRect().HasPoint(globalPos))
                           || (slot.Player2Area != null && slot.Player2Area.GetGlobalRect().HasPoint(globalPos));
                if (hit) return slot.AreaUID;
            }
            return null;
        }

        /// <summary>是否为"需要目标的事务卡"（与单机 CardDragController 的判定一致：Sorcery 类别 + GetRequiresTarget）。</summary>
        private static bool IsSorceryRequiringTarget(CardInstance inst)
        {
            if (inst?.PlayerTemplate is not PlayerCardData tmpl) return false;
            if ((tmpl.CategoryFlag & CardCategory.Sorcery) == 0) return false;
            return tmpl.GetRequiresTarget();
        }

        /// <summary>【K15】该卡是否声明了多目标槽位（由服务端驱动逐槽选择 + 确认面板）。</summary>
        private static bool IsMultiTargetCard(CardInstance inst)
            => inst?.PlayerTemplate is PlayerCardData tmpl
            && Script.Manager.MultiTargetSlots.FindMultiTargetEffect(tmpl, Script.Data.Effects.EffectTiming.OnSorceryCast) != null;

        /// <summary>按卡模板 UID 推断卡图目录（与 CardUI/StackEntryUI 的规则一致）。</summary>
        private static string ResolvePlayerCardImagePath(string uid)
        {
            int i = 0;
            while (i < uid.Length && char.IsLetter(uid[i])) i++;
            string folder = i > 0 ? uid.Substring(0, i) : "JC";
            return $"res://Cards/Image/PlayerCards/{folder}/{uid}.jpg";
        }

        /// <summary>查找落点命中的"己方争夺区"对应地区 UID（本地玩家 1→Player1Area，2→Player2Area）。</summary>
        private string FindOwnAreaUidAt(Vector2 globalPos)
        {
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                var ownArea = _localPlayerId == 1 ? slot.Player1Area : slot.Player2Area;
                if (ownArea != null && ownArea.GetGlobalRect().HasPoint(globalPos))
                    return slot.AreaUID;
            }
            return null;
        }
    }
}
