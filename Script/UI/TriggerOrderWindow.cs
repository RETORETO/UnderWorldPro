using Godot;
using System.Collections.Generic;
using Script.Data.Cards;

namespace Script.UI
{
    /// <summary>
    /// 触发效果排序窗口（独立 Window 弹出，模态独占）。
    /// 规则：
    ///  - 窗口弹出期间冻结所有操作。
    ///  - 入堆叠区域（OrderZoneContainer）：玩家可把触发拖入/拖出；强制触发不能放入放弃区。
    ///  - 放弃区域（DiscardZoneContainer）：仅非强制触发可放入；关窗前可拖回入堆叠区。
    ///  - 全部进入堆叠：按当前 OrderZone 顺序把全部触发压入堆叠（逐个选目标）。
    ///  - 第一张进入堆叠：仅 OrderZone 最左侧一个触发压入堆叠（如需目标则弹选目标窗口）。
    ///  - 确认（Done）：仅当 OrderZone 为空时可点击，表示放弃剩余触发，关闭窗口推进。
    ///
    /// 拖拽排序与鼠标接管由子节点 TriggerOrderRoot(Control) 负责（Window 非 Control，无 _GuiInput）。
    /// </summary>
    public partial class TriggerOrderWindow : Window
    {
        [Export] public Control OrderZoneContainer;
        [Export] public Control DiscardZoneContainer;
        [Export] public Button AllEnterButton;
        [Export] public Button FirstEnterButton;
        [Export] public Button DoneButton;
        [Export] public Label TitleLabel;
        [Export] public Label HintLabel;
        [Export] public Control DragHandle;

        /// <summary>当前排序窗口归属玩家（先手=1 / 后手=2）。</summary>
        public int OwnerPlayerId { get; private set; }

        /// <summary>入堆叠回调：参数为当前要入堆叠的触发数据列表（按顺序）。不关闭窗口。</summary>
        public System.Action<List<object>> OnEnterStack;
        /// <summary>完成/确认回调：OrderZone 为空时关闭窗口后触发，用于推进下一步。</summary>
        public System.Action OnDone;

        private TriggerOrderRoot _root;

        public override void _Ready()
        {
            if (AllEnterButton != null) AllEnterButton.Pressed += OnAllEnterPressed;
            if (FirstEnterButton != null) FirstEnterButton.Pressed += OnFirstEnterPressed;
            if (DoneButton != null) DoneButton.Pressed += OnDonePressed;
            _root = GetNode<TriggerOrderRoot>("RootControl");
            _root.OnOrderZoneChanged += RefreshButtonStates;
        }

        // 与 InvestigationWindow 完全对称：用 Window 自身的 _Input（全局输入）驱动条目拖拽，
        // 不受子 Control 的 mouse_filter 限制。仅命中条目或正在拖拽时才 SetInputAsHandled，避免吞掉其它输入。
        public override void _Input(InputEvent @event)
        {
            if (!Visible) return;
            var globalMouse = GetViewport().GetMousePosition();
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (_root != null && _root.BeginEntryDrag(globalMouse)) GetViewport().SetInputAsHandled();
                }
                else if (_root != null && _root.IsEntryDragging)
                {
                    _root.EndEntryDrag(globalMouse);
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (@event is InputEventMouseMotion && _root != null && _root.IsEntryDragging)
            {
                _root.MoveEntryDrag(globalMouse);
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>打开排序窗口：注入归属玩家与待排序触发条目。</summary>
        public void Open(int ownerPlayerId, IEnumerable<TriggerEntryUI> entries)
        {
            OwnerPlayerId = ownerPlayerId;
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vp = GetViewport().GetVisibleRect().Size;
            Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)(vp.Y / 2) + 150);
            Popup(); // Window 显示并定位
            var list = new List<TriggerEntryUI>(entries);
            _root?.Setup(list);
            SetButtonsEnabled(true);
        }

        public void Close()
        {
            Hide(); // Window 隐藏
            OnDone?.Invoke();
        }

        /// <summary>当前 OrderZone 内条目（按顺序），供外部查询。</summary>
        public Godot.Collections.Array<Godot.Node> GetOrderZoneChildren()
            => OrderZoneContainer != null ? OrderZoneContainer.GetChildren() : new Godot.Collections.Array<Godot.Node>();

        /// <summary>控制三个操作按钮的可用状态（选目标期间可整体禁用）。</summary>
        public void SetButtonsEnabled(bool enabled)
        {
            if (AllEnterButton != null) AllEnterButton.Disabled = !enabled || (OrderZoneContainer?.GetChildCount() ?? 0) == 0;
            if (FirstEnterButton != null) FirstEnterButton.Disabled = !enabled || (OrderZoneContainer?.GetChildCount() ?? 0) == 0;
            if (DoneButton != null) DoneButton.Disabled = !enabled || (OrderZoneContainer?.GetChildCount() ?? 0) > 0;
            // 与三个按钮同步：选目标窗口打开（enabled=false）时冻结条目与窗口拖拽，关闭时恢复
            if (_root != null) _root.SetDragFrozen(!enabled);
        }

        private void RefreshButtonStates()
        {
            SetButtonsEnabled(true);
        }

        private List<object> CurrentOrder()
        {
            var list = new List<object>();
            foreach (var c in OrderZoneContainer.GetChildren())
                if (c is TriggerEntryUI t) list.Add(t.TriggerData);
            return list;
        }

        private void OnAllEnterPressed()
        {
            var ordered = CurrentOrder();
            if (ordered.Count == 0) return;
            GD.Print($"[TriggerOrderWindow] 全部进入堆叠: {ordered.Count} 个");
            // 节点移除延后到触发实际入栈之后（见 EnsureTargetThenPush 的 onAllDone → Arena.RemoveOrderEntries），
            // 否则取目标触发在玩家选目标前就会从 OrderZone 消失。
            OnEnterStack?.Invoke(ordered);
        }

        private void OnFirstEnterPressed()
        {
            if (OrderZoneContainer.GetChildCount() == 0) return;
            TriggerEntryUI first = null;
            foreach (var c in OrderZoneContainer.GetChildren())
            {
                if (c is TriggerEntryUI t) { first = t; break; }
            }
            if (first == null) return;
            var single = new List<object> { first.TriggerData };
            GD.Print($"[TriggerOrderWindow] 第一张进入堆叠: {first.NameLabel?.Text}");
            // 节点移除延后到触发实际入栈之后（含选目标）。
            OnEnterStack?.Invoke(single);
        }

        /// <summary>
        /// 触发实际入栈后由 Arena 调用：从 OrderZone 移除已入栈条目。
        /// 取目标触发在玩家选完目标后才调用本方法，故条目会保留到选目标完成。
        /// </summary>
        public void RemoveEntries(System.Collections.Generic.List<Script.Data.Effects.StackEffectInstance> pushed)
        {
            if (pushed == null || pushed.Count == 0)
            {
                _root?.ArrangeOrderZone();
                RefreshButtonStates();
                return;
            }
            foreach (var t in pushed)
            {
                foreach (var c in OrderZoneContainer.GetChildren())
                {
                    if (c is TriggerEntryUI ui && ui.TriggerData == t)
                    {
                        OrderZoneContainer.RemoveChild(ui);
                        ui.QueueFree();
                        break;
                    }
                }
            }
            _root?.ArrangeOrderZone();
            RefreshButtonStates();
        }

        private void OnDonePressed()
        {
            if (OrderZoneContainer.GetChildCount() > 0)
            {
                GD.Print("[TriggerOrderWindow] 完成排序被忽略：OrderZone 仍非空");
                return;
            }
            GD.Print("[TriggerOrderWindow] 完成排序（放弃剩余触发）");
            Close();
        }
    }
}
