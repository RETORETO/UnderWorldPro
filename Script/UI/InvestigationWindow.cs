using Godot;
using Script.Game.Entities;
using Script.UI;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 调查对抗窗口（InvestigationWindow）。
/// 调查对抗胜利后由 Arena 调用 Open(playerId, x, areaUid) 弹出。
/// 从胜者牌库顶取出 X 张卡，**初始全部置于 TopDropZone（牌库顶）**；
/// 玩家在左右两个区域（左＝牌库顶 / 右＝牌库底）之间拖拽分配。
/// Confirm 时按各区内"左→右"顺序放回牌库（最左＝该组最靠牌库顶），并让胜者抽 1 张。
/// </summary>
public partial class InvestigationWindow : Window
{
    // ---------- 场景节点 ----------
    private Label _titleLabel;
    private Control _topDropZone;
    private Control _topCardContainer;
    private Label _topCountLabel;
    private Label _topZoneHeaderLabel;
    private Control _bottomDropZone;
    private Control _bottomCardContainer;
    private Label _bottomCountLabel;
    private Label _bottomZoneHeaderLabel;
    private Label _progressLabel;
    private Label _hintLabel;
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

    /// <summary>【2026-10】两槽模型：左框＝牌库顶、右框＝牌库底。
    /// 原"资产池（Pool）"第三槽已取消——打开时所有卡直接落在 Top，玩家仅在两框之间拖拽。</summary>
    private enum DropZone { Top, Bottom }

    /// <summary>当前对抗所属的地区序号（1-based），用于窗口标题显示。</summary>
    private int _areaNo;

    /// <summary>【2026-10·JC104】本窗当前套用的**可复用文案**（调查对抗 / 预测X…）；日志前缀随它走。</summary>
    private AllocationWindowTexts _texts = AllocationWindowTexts.Investigation(0);

    public override void _Ready()
    {
        _titleLabel = GetNode<Label>("RootControl/TitleLabel");
        _topDropZone = GetNode<Control>("RootControl/TopDropZone");
        _topCardContainer = GetNode<Control>("RootControl/TopDropZone/CardContainer");
        _topCountLabel = GetNode<Label>("RootControl/TopDropZone/CountLabel");
        _topZoneHeaderLabel = GetNodeOrNull<Label>("RootControl/TopDropZone/ZoneHeaderLabel");
        _bottomDropZone = GetNode<Control>("RootControl/BottomDropZone");
        _bottomCardContainer = GetNode<Control>("RootControl/BottomDropZone/CardContainer");
        _bottomCountLabel = GetNode<Label>("RootControl/BottomDropZone/CountLabel");
        _bottomZoneHeaderLabel = GetNodeOrNull<Label>("RootControl/BottomDropZone/ZoneHeaderLabel");
        _progressLabel = GetNode<Label>("RootControl/ProgressLabel");
        _hintLabel = GetNodeOrNull<Label>("RootControl/HintLabel");
        _clearButton = GetNode<Button>("RootControl/ClearButton");
        _confirmButton = GetNode<Button>("RootControl/ConfirmButton");

        _clearButton.Pressed += OnClearPressed;
        _confirmButton.Pressed += OnConfirmPressed;

        _cardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
    }

    /// <summary>
    /// 【2026-10·JC104】套用"同一份分配机制、按场景换文案"的**可复用文案**（`AllocationWindowTexts`）：
    /// 窗口标题栏、窗内标题、两区标题、提示行一次性写入 ⇒ 预测类效果看到的是"预测X"而不是"调查对抗"
    /// （用户口径："显示上不能是调查对抗 UI"）。未给出的字段保持场景原值。
    /// </summary>
    public void ApplyTexts(AllocationWindowTexts texts)
    {
        if (texts == null) return;
        _texts = texts;
        if (!string.IsNullOrEmpty(texts.WindowTitle)) Title = texts.WindowTitle;   // 操作系统窗口标题栏
        if (_titleLabel != null && !string.IsNullOrEmpty(texts.Title)) _titleLabel.Text = texts.Title;
        if (_topZoneHeaderLabel != null && !string.IsNullOrEmpty(texts.TopZoneHeader))
            _topZoneHeaderLabel.Text = texts.TopZoneHeader;
        if (_bottomZoneHeaderLabel != null && !string.IsNullOrEmpty(texts.BottomZoneHeader))
            _bottomZoneHeaderLabel.Text = texts.BottomZoneHeader;
        if (_hintLabel != null && !string.IsNullOrEmpty(texts.Hint)) _hintLabel.Text = texts.Hint;
    }

    /// <summary>初始化依赖并打开窗口。
    /// cards 为从胜者牌库顶取出的 X 张实例（已脱离数据层牌库）；
    /// onReturnConfirmed 在确认时回调（仅回传纯数据：牌库顶/底实例列表），由 Controller 统一完成数据层放回 + UI 牌库同步 + 抽牌；
    /// onDraw 为胜者抽牌回调。
    /// texts：【2026-10·JC104】**可复用文案**（`AllocationWindowTexts`）——调查对抗传
    /// `AllocationWindowTexts.Investigation(areaNo)`（缺省即此），预测X 传 `AllocationWindowTexts.Prediction(x)`；
    /// 两者的"拖拽/排序/确认"机制**完全共用**，只有文案不同（用户口径：显示上不能是调查对抗 UI）。
    /// 传 null 时按 `areaNo` 走调查对抗文案（既有调用点行为不变）。</summary>
    public void Configure(int winnerPlayer, int areaNo, List<CardInstance> cards,
                          System.Action<int, List<CardInstance>, List<CardInstance>> onReturnConfirmed,
                          Action<int, int> onDraw, AllocationWindowTexts texts = null)
    {
        _winnerPlayer = winnerPlayer;
        _areaNo = areaNo;
        ApplyTexts(texts ?? AllocationWindowTexts.Investigation(areaNo));
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
            _placement[inst] = DropZone.Top;      // 【2026-10】初始全部落在"牌库顶"框（不再有暂存池）
            _topCardContainer.AddChild(ui);       // 先入树
            ui.Setup(inst);                       // 入树后再 Setup + 翻正面，避免被 _Ready 覆盖
            ui.SetFace(true);
        }

        _totalCount = _cardInstances.Count;
        // 【2026-10】Top 容器不再是 HBoxContainer（无自动排布）⇒ 必须手动摆位一次，
        // 否则所有卡会叠在容器 (0,0)，且 PickCardAt 命中错乱。
        ArrangeDropZone(_topCardContainer);
        RefreshLabels();
        GD.Print($"【{_texts.LogTag}】已打开：胜者P{_winnerPlayer}，共 {_totalCount} 张待分配");
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
            // 在任意一张卡上按下 → 开始拖拽（无论在牌库顶框还是牌库底框）
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
        // 【2026-10】按 ZIndex 取**最上层**的命中卡：ArrangeDropZone 中 ZIndex = 区内左→右序号，
        // 即"右卡压在左卡之上"。原实现按 Dictionary 枚举序返回"首个命中"，与视觉层次无关，
        // 卡重叠时会抓到被压在下面的那张（拖错卡）。
        CardUI best = null;
        foreach (var ui in _uiMap.Values)
        {
            if (!ui.GetVisualGlobalRect().HasPoint(globalPos)) continue;
            if (best == null || ui.ZIndex > best.ZIndex) best = ui;
        }
        return best;
    }

    private void DropCardToZone(CardUI card, Vector2 globalPos)
    {
        if (!_uiMap.ContainsValue(card)) return;
        var inst = card.Instance;

        bool overTop = _topCardContainer.GetGlobalRect().HasPoint(globalPos) ||
                       _topDropZone.GetGlobalRect().HasPoint(globalPos);
        bool overBottom = _bottomCardContainer.GetGlobalRect().HasPoint(globalPos) ||
                          _bottomDropZone.GetGlobalRect().HasPoint(globalPos);

        if (!overTop && !overBottom)
        {
            // 【2026-10】两框之外松手：视为取消本次拖拽 → 被拖卡吸回原槽位，不改 _placement。
            // （原实现在此落回"资产池"，该槽已取消；改为原位复位，避免卡凭空消失或错位。）
            ArrangeDropZone(_topCardContainer);
            ArrangeDropZone(_bottomCardContainer);
            return;
        }

        DropZone zone = overTop ? DropZone.Top : DropZone.Bottom;
        _placement[inst] = zone;

        Control target = zone == DropZone.Top ? _topCardContainer : _bottomCardContainer;

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

        GD.Print($"[{_texts.LogTag}] 卡落至 {zone}，插入索引={insertIdx}");

        ArrangeDropZone(_topCardContainer);
        ArrangeDropZone(_bottomCardContainer);
        RefreshLabels();
    }

    private void ArrangeDropZone(Control container)
    {
        // 【2026-10】窗口内已无 HBoxContainer（CardPool 槽取消），两区均为普通 Control 容器，
        // 统一应用"放得下居中、放不下重叠"逻辑。
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
        if (_topCountLabel != null) _topCountLabel.Text = $"{top} 张";
        if (_bottomCountLabel != null) _bottomCountLabel.Text = $"{bottom} 张";
        // 【2026-10】两槽模型下每张卡**恒在** Top 或 Bottom，"未放置"状态不复存在 ⇒
        // 进度行改为顶/底分布信息，"确认"按钮恒可用（确认后 OnConfirmPressed 会置灰防连点）。
        if (_progressLabel != null) _progressLabel.Text = $"牌库顶 {top} 张 / 牌库底 {bottom} 张";
        if (_confirmButton != null) _confirmButton.Disabled = false;
    }

    // ============================================================
    // 按钮
    // ============================================================
    private void OnClearPressed()
    {
        // 【2026-10】暂存池取消后，"清除选择"语义＝把所有卡收回"牌库顶"框（回到打开时的初始状态）。
        foreach (var inst in _cardInstances)
        {
            _placement[inst] = DropZone.Top;
            var ui = _uiMap[inst];
            ui.GetParent()?.RemoveChild(ui);
            _topCardContainer.AddChild(ui);
        }
        ArrangeDropZone(_topCardContainer);
        ArrangeDropZone(_bottomCardContainer);
        RefreshLabels();
        GD.Print($"【{_texts.LogTag}】已清除，所有卡回到牌库顶框");
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

        GD.Print($"【{_texts.LogTag}】确认：放回牌库顶 {toTop.Count} 张、牌库底 {toBottom.Count} 张");

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
