using Godot;
using Script.Game.Entities;
using Script.UI;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 调查对抗窗口（InvestigationWindow）。
/// 调查对抗胜利后由 Arena 调用 Open(playerId, x, areaUid) 弹出。
/// 从胜者牌库顶取出 X 张卡放入 CardPool，玩家拖拽分配到 TopDropZone（牌库顶）或
/// BottomDropZone（牌库底）。Confirm 时按"从右到左"顺序放回牌库并让胜者抽 1 张。
/// </summary>
public partial class InvestigationWindow : Window
{
    // ---------- 场景节点 ----------
    private Label _titleLabel;
    private Control _topDropZone;
    private Control _topCardContainer;
    private Label _topCountLabel;
    private Control _bottomDropZone;
    private Control _bottomCardContainer;
    private Label _bottomCountLabel;
    private Label _progressLabel;
    private Control _cardPool;
    private Button _clearButton;
    private Button _confirmButton;

    // ---------- 依赖 ----------
    // View 不直接写数据层；所有数据/UI 牌库同步由回调 onReturnConfirmed 委托给 Controller 完成
    private Action<int, int> _onDraw;   // 胜者抽牌回调 (playerId, count)

    /// <summary>
    /// 确认时回调：把分配结果（纯数据）交回 Controller 统一处理（数据层放回 + UI 牌库同步 + 抽牌）。
    /// 参数顺序：胜者、牌库顶卡(实例)、牌库底卡(实例)。
    /// 牌库 UI 的最终顺序由 ZoneManager.OnDeckReordered 事件驱动 DeckZone 重建，View 不再回传 UI。
    /// </summary>
    private System.Action<int, List<CardInstance>, List<CardInstance>> _onReturnConfirmed;

    // ---------- 状态 ----------
    private int _winnerPlayer;
    private int _totalCount;

    /// <summary>确认（分配完成）后回调，由 Arena 注入，用于通知 CombatManager 本区结算完毕。</summary>
    public System.Action OnResolved { get; set; }
    private readonly List<CardInstance> _cardInstances = new List<CardInstance>();
    private readonly Dictionary<CardInstance, CardUI> _uiMap = new Dictionary<CardInstance, CardUI>();
    private readonly Dictionary<CardInstance, DropZone> _placement = new Dictionary<CardInstance, DropZone>();
    private PackedScene _cardUIScene;

    private enum DropZone { Pool, Top, Bottom }

    /// <summary>当前对抗所属的地区序号（1-based），用于窗口标题显示。</summary>
    private int _areaNo;

    public override void _Ready()
    {
        _titleLabel = GetNode<Label>("RootControl/TitleLabel");
        _topDropZone = GetNode<Control>("RootControl/TopDropZone");
        _topCardContainer = GetNode<Control>("RootControl/TopDropZone/CardContainer");
        _topCountLabel = GetNode<Label>("RootControl/TopDropZone/CountLabel");
        _bottomDropZone = GetNode<Control>("RootControl/BottomDropZone");
        _bottomCardContainer = GetNode<Control>("RootControl/BottomDropZone/CardContainer");
        _bottomCountLabel = GetNode<Label>("RootControl/BottomDropZone/CountLabel");
        _progressLabel = GetNode<Label>("RootControl/ProgressLabel");
        _cardPool = GetNode<Control>("RootControl/CardPool");
        _clearButton = GetNode<Button>("RootControl/ClearButton");
        _confirmButton = GetNode<Button>("RootControl/ConfirmButton");

        _clearButton.Pressed += OnClearPressed;
        _confirmButton.Pressed += OnConfirmPressed;

        _cardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
    }

    /// <summary>初始化依赖并打开窗口。
    /// cards 为从胜者牌库顶取出的 X 张实例（已脱离数据层牌库）；
    /// onReturnConfirmed 在确认时回调（仅回传纯数据：牌库顶/底实例列表），由 Controller 统一完成数据层放回 + UI 牌库同步 + 抽牌；
    /// onDraw 为胜者抽牌回调。</summary>
    public void Configure(int winnerPlayer, int areaNo, List<CardInstance> cards,
                          System.Action<int, List<CardInstance>, List<CardInstance>> onReturnConfirmed,
                          Action<int, int> onDraw)
    {
        _winnerPlayer = winnerPlayer;
        _areaNo = areaNo;
        if (_titleLabel != null) _titleLabel.Text = $"{areaNo}地区调查对抗获胜";
        _onReturnConfirmed = onReturnConfirmed;
        _onDraw = onDraw;
        _cardInstances.Clear();
        _uiMap.Clear();
        _placement.Clear();

        for (int i = 0; i < cards.Count; i++)
        {
            var inst = cards[i];
            if (inst == null) continue;
            _cardInstances.Add(inst);

            // 窗口内部自行实例化 CardUI 进行展示；最终牌库 UI 顺序由 ZoneManager 事件驱动 DeckZone 重建，
            // 故此处无需与 DeckZone 共享 CardUI 实例。
            CardUI ui = _cardUIScene.Instantiate<CardUI>();
            ui.CanDrag = false;            // 禁用自带拖拽，由本窗口接管
            ui.FreezeSmoothMove = true;   // 禁用平滑归位，位置由本窗口直接控制
            ui.GetParent()?.RemoveChild(ui);
            _uiMap[inst] = ui;
            _placement[inst] = DropZone.Pool;
            _cardPool.AddChild(ui);       // 先入树
            ui.Setup(inst);               // 入树后再 Setup + 翻正面，避免被 _Ready 覆盖
            ui.SetFace(true);
        }

        _totalCount = _cardInstances.Count;
        RefreshLabels();
        GD.Print($"【调查窗口】已打开：胜者P{_winnerPlayer}，共 {_totalCount} 张待分配");
    }

    // ============================================================
    // 拖拽（窗口接管）
    // ============================================================
    private CardUI _dragging;
    private Vector2 _dragOffset;
    private Vector2 _dragOrigin;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            // 在任意一张卡上按下 → 开始拖拽（无论它在 Pool 还是 DropZone）
            var card = PickCardAt(mb.GlobalPosition);
            if (card != null)
            {
                _dragging = card;
                _dragOffset = mb.GlobalPosition - card.GlobalPosition;
                _dragOrigin = card.GlobalPosition;
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event is InputEventMouseButton mb2 && !mb2.Pressed && mb2.ButtonIndex == MouseButton.Left)
        {
            if (_dragging != null)
            {
                DropCardToZone(_dragging, mb2.GlobalPosition);
                _dragging = null;
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event is InputEventMouseMotion motion && _dragging != null)
        {
            _dragging.GlobalPosition = motion.GlobalPosition - _dragOffset;
            _dragging.TargetPosition = _dragging.GlobalPosition;   // 同步目标位置，防止 CardUI._Process 平滑归位
            GetViewport().SetInputAsHandled();
        }
    }

    private CardUI PickCardAt(Vector2 globalPos)
    {
        // 倒序遍历，优先命中上层卡
        foreach (var ui in _uiMap.Values)
        {
            if (ui.GetVisualGlobalRect().HasPoint(globalPos))
                return ui;
        }
        return null;
    }

    private void DropCardToZone(CardUI card, Vector2 globalPos)
    {
        if (!_uiMap.ContainsValue(card)) return;
        var inst = card.Instance;
        DropZone zone = DropZone.Pool;

        if (_topCardContainer.GetGlobalRect().HasPoint(globalPos) ||
            _topDropZone.GetGlobalRect().HasPoint(globalPos))
            zone = DropZone.Top;
        else if (_bottomCardContainer.GetGlobalRect().HasPoint(globalPos) ||
                 _bottomDropZone.GetGlobalRect().HasPoint(globalPos))
            zone = DropZone.Bottom;

        _placement[inst] = zone;

        Control target = zone == DropZone.Top ? _topCardContainer
                        : zone == DropZone.Bottom ? _bottomCardContainer
                        : _cardPool;

        // 按放手 X 坐标在目标池的【现有卡序列（排除自身）】中找最接近的插入点，
        // 用 MoveChild 对齐节点树顺序，从而真正调整该池内卡的顺序（影响最终返回牌库顺序）。
        int insertIdx = 0;
        var siblings = target.GetChildren().OfType<CardUI>().Where(c => c != card).ToList();
        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].GetVisualGlobalRect().GetCenter().X > globalPos.X)
            {
                insertIdx = i;
                break;
            }
            insertIdx = i + 1;
        }

        card.GetParent()?.RemoveChild(card);
        target.AddChild(card);
        target.MoveChild(card, insertIdx);

        GD.Print($"[调查窗口] 卡落至 {zone}，插入索引={insertIdx}");

        ArrangeDropZone(_topCardContainer);
        ArrangeDropZone(_bottomCardContainer);
        RefreshLabels();
    }

    private void ArrangeDropZone(Control container)
    {
        // CardPool 是 HBoxContainer，自动布局会忽略手动 Position，故跳过（交给 HBox 默认均匀排列）。
        // 仅对普通 Control 容器（Top/Bottom 的 CardContainer）应用"放得下居中、放不下重叠"逻辑。
        if (container is HBoxContainer) return;

        const float cw = 100f, ch = 140f, gap = 8f;
        // 收集容器内卡（按节点树顺序 = 左→右用户拖拽顺序）
        var cards = container.GetChildren().OfType<CardUI>().ToList();
        int n = cards.Count;
        if (n == 0) return;

        float containerWidth = container.Size.X;
        if (containerWidth <= 0) containerWidth = 1152;
        float y = (container.Size.Y - ch) / 2;

        if (n == 1)
        {
            var only = cards[0];
            only.AnchorLeft = only.AnchorRight = only.AnchorTop = only.AnchorBottom = 0;
            only.CustomMinimumSize = new Vector2(cw, ch);
            only.Size = new Vector2(cw, ch);
            only.Position = new Vector2((containerWidth - cw) / 2f, y);
            only.ZIndex = 0;
            only.TargetPosition = only.GlobalPosition;
            return;
        }

        float idealTotal = n * cw + (n - 1) * gap;
        float startX;
        float step;
        if (idealTotal <= containerWidth)
        {
            // 放得下：整体居中，不重叠
            startX = (containerWidth - idealTotal) / 2f;
            step = cw + gap;
        }
        else
        {
            // 放不下：压缩步长铺满容器宽度（允许重叠），绝不超出容器
            startX = 0;
            step = (containerWidth - cw) / (n - 1);
        }

        for (int i = 0; i < n; i++)
        {
            var card = cards[i];
            card.AnchorLeft = card.AnchorRight = card.AnchorTop = card.AnchorBottom = 0;
            card.CustomMinimumSize = new Vector2(cw, ch);
            card.Size = new Vector2(cw, ch);
            card.Position = new Vector2(startX + i * step, y);
            card.ZIndex = i;   // 右卡(i 大)压在左卡之上，与资产池一致
            card.TargetPosition = card.GlobalPosition;   // 防止 CardUI._Process 拉回
        }
    }

    // ============================================================
    // 标签刷新
    // ============================================================
    private void RefreshLabels()
    {
        int top = 0, bottom = 0;
        foreach (var kv in _placement)
        {
            if (kv.Value == DropZone.Top) top++;
            else if (kv.Value == DropZone.Bottom) bottom++;
        }
        int placed = top + bottom;
        if (_topCountLabel != null) _topCountLabel.Text = $"{top} 张";
        if (_bottomCountLabel != null) _bottomCountLabel.Text = $"{bottom} 张";
        if (_progressLabel != null) _progressLabel.Text = $"已放置：{placed} / {_totalCount} 张";
        if (_confirmButton != null) _confirmButton.Disabled = placed < _totalCount;
    }

    // ============================================================
    // 按钮
    // ============================================================
    private void OnClearPressed()
    {
        foreach (var inst in _cardInstances)
        {
            _placement[inst] = DropZone.Pool;
            var ui = _uiMap[inst];
            ui.GetParent()?.RemoveChild(ui);
            _cardPool.AddChild(ui);
        }
        ArrangeDropZone(_topCardContainer);
        ArrangeDropZone(_bottomCardContainer);
        RefreshLabels();
        GD.Print("【调查窗口】已清除，所有卡回到 CardPool");
    }

    private bool _confirmed = false;

    private void OnConfirmPressed()
    {
        if (_confirmed) return;   // 防止重复触发导致多抽牌
        _confirmed = true;
        if (_confirmButton != null) _confirmButton.Disabled = true;

        // 放回顺序：TopDropZone（从左到右）-> BottomDropZone（从左到右）
        // 即先放左侧的卡，再放右侧的卡（与拖拽排序表现一致）
        var topCards = CardsInContainerOrdered(_topCardContainer);    // 左→右
        var bottomCards = CardsInContainerOrdered(_bottomCardContainer);

        var toTop = new List<CardInstance>(topCards);
        var toBottom = new List<CardInstance>(bottomCards);

        GD.Print($"【调查窗口】确认：放回牌库顶 {toTop.Count} 张、牌库底 {toBottom.Count} 张");

        // 仅回传纯数据分配结果，牌库最终顺序与 UI 由 Controller（ZoneManager + DeckZone）统一重建
        _onReturnConfirmed?.Invoke(_winnerPlayer, toTop, toBottom);

        // 胜者抽 1 张
        _onDraw?.Invoke(_winnerPlayer, 1);

        // 通知外部（CombatManager）本区结算完成，可推进下一个地区
        OnResolved?.Invoke();

        // 关闭窗口
        QueueFree();
    }

    private List<CardInstance> CardsInContainerOrdered(Control container)
    {
        var list = new List<CardInstance>();
        foreach (var c in container.GetChildren())
        {
            if (c is CardUI card && card.Instance != null)
                list.Add(card.Instance);
        }
        // 按 X 坐标排序（左→右）
        list.Sort((a, b) =>
        {
            float ax = _uiMap[a].Position.X;
            float bx = _uiMap[b].Position.X;
            return ax.CompareTo(bx);
        });
        return list;
    }
}
