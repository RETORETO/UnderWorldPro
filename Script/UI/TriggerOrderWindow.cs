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
    ///  - 第一张进入堆叠：仅 OrderZone 最左侧一个触发压入堆叠（如需目标则弹选目标窗口）；
    ///    入堆叠完成后仅当**排序框与放弃框都为空**（且引擎剩余计数归零）才自动关窗推进；任一框还有内容则保持打开。
    ///  - 确认（Done，"完成排序并放弃下方效果"）：**仅当排序框（OrderZone）为空时可点击，不看放弃框**，
    ///    表示放弃剩余触发（含放弃框内内容），关闭窗口推进。
    ///
    /// 拖拽排序与鼠标接管由子节点 TriggerOrderRoot(Control) 负责（Window 非 Control，无 _GuiInput）。
    /// </summary>
    public partial class TriggerOrderWindow : Window
    {
        [Export] public Control OrderZoneContainer;
        [Export] public Control DiscardZoneContainer;
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

        /// <summary>本次排序尚未入堆叠的触发总数（由 Arena 注入，仅含"仍需玩家处理"的触发）。
        /// 「第一张进入堆叠」后若本值归零（＝最后一张触发已入栈，无剩余效果需要加入堆叠），自动关窗推进。</summary>
        private int _remainingTriggerCount = -1;

        public override void _Ready()
        {
            if (FirstEnterButton != null) FirstEnterButton.Pressed += OnFirstEnterPressed;
            if (DoneButton != null) DoneButton.Pressed += OnDonePressed;
            _root = GetNode<TriggerOrderRoot>("RootControl");
            _root.OnOrderZoneChanged += RefreshButtonStates;
        }

        /// <summary>
        /// 本窗口已打开（Visible）后，由 Arena 同步"本次排序仍未处理的触发总数"。
        /// 在窗口打开**前**调用只记录数值、不判定自动关闭（避免开窗瞬间就被当成"已无剩余"而误关）。
        /// </summary>
        public void SetRemainingTriggerCount(int count)
        {
            _remainingTriggerCount = count;
            if (Visible) TryAutoCloseIfNothingRemaining();
        }

        /// <summary>本窗口是否已弹出（Arena 据此决定 SetRemainingTriggerCount 是记录还是即时判定）。</summary>
        public bool IsOpen => Visible;
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
            _firedFirstEnter = false;
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vp = GetViewport().GetVisibleRect().Size;
            Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)(vp.Y / 2) + 150);
            Popup(); // Window 显示并定位
            var list = new List<TriggerEntryUI>(entries);
            _root?.Setup(list);
            SetButtonsEnabled(true);
            // 开窗后立即按已注入的剩余数做一次兜底判定（本例中首次入栈已由 onEnterStack 判定，这里只覆盖异常装配）。
            TryAutoCloseIfNothingRemaining();
        }

        public void Close()
        {
            Hide(); // Window 隐藏
            OnDone?.Invoke();
        }

        /// <summary>当前 OrderZone 内条目（按顺序），供外部查询。</summary>
        public Godot.Collections.Array<Godot.Node> GetOrderZoneChildren()
            => OrderZoneContainer != null ? OrderZoneContainer.GetChildren() : new Godot.Collections.Array<Godot.Node>();

        /// <summary>当前 DiscardZone 内条目，供外部/自动关闭判定查询（与 <see cref="GetOrderZoneChildren"/> 对称）。</summary>
        public Godot.Collections.Array<Godot.Node> GetDiscardZoneChildren()
            => DiscardZoneContainer != null ? DiscardZoneContainer.GetChildren() : new Godot.Collections.Array<Godot.Node>();

        /// <summary>
        /// 控制两个操作按钮的可用状态（选目标期间可整体禁用）。
        /// 「完成排序并放弃下方效果」启用口径（用户 2026-10）：**仅排序框为空**时可点，**不看放弃框**
        /// （放弃框非空时点它＝放弃下方内容并结束）。「第一张进入堆叠」启用口径：排序框非空时可点。
        /// </summary>
        public void SetButtonsEnabled(bool enabled)
        {
            if (FirstEnterButton != null) FirstEnterButton.Disabled = !enabled || (OrderZoneContainer?.GetChildCount() ?? 0) == 0;
            if (DoneButton != null) DoneButton.Disabled = !enabled || (OrderZoneContainer?.GetChildCount() ?? 0) > 0;
            // 与两个按钮同步：选目标窗口打开（enabled=false）时冻结条目与窗口拖拽，关闭时恢复
            if (_root != null) _root.SetDragFrozen(!enabled);
        }

        private void RefreshButtonStates()
        {
            SetButtonsEnabled(true);
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
            _firedFirstEnter = true;
            var single = new List<object> { first.TriggerData };
            GD.Print($"[TriggerOrderWindow] 第一张进入堆叠: {first.NameLabel?.Text}");
            // 节点移除延后到触发实际入栈之后（含选目标）。
            OnEnterStack?.Invoke(single);
        }

        /// <summary>本次窗口是否点过「第一张进入堆叠」（自动关闭仅在该路径生效）。</summary>
        private bool _firedFirstEnter;

        /// <summary>
        /// 「第一张进入堆叠」路径的自动关闭判定：本次点击的触发已实际入栈（Arena 在选目标完成后回填剩余数），
        /// 且**排序框与放弃框都为空** → 自动关窗并推进流程（无需再点"完成排序"）。
        /// 门槛（三者同时满足）：点过第一张进入堆叠、引擎注入的剩余计数归零、且两框（OrderZone/DiscardZone）均无条目。
        /// 只在该路径生效：窗口首次打开即"全程无待处理触发"的兜底场景不适用（此时无"进入堆叠"动作）。
        /// </summary>
        private void TryAutoCloseIfNothingRemaining()
        {
            if (!_firedFirstEnter) return;
            if (_remainingTriggerCount != 0) return;
            if ((OrderZoneContainer?.GetChildCount() ?? 0) > 0 || (DiscardZoneContainer?.GetChildCount() ?? 0) > 0)
            {
                GD.Print("[TriggerOrderWindow] 第一张入堆叠后仍有剩余条目（排序框或放弃框非空）→ 暂不关闭排序窗口");
                return;
            }
            GD.Print("[TriggerOrderWindow] 第一张入堆叠后已无剩余触发（双框皆空）→ 自动关闭排序窗口");
            _firedFirstEnter = false;
            Close();
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

        /// <summary>
        /// 由 Arena 在「本次排序继续推进」时调用（onEnterStack 回调里）：刷新本窗口记录的剩余触发总数，
        /// 覆盖"入栈后 OrderZone 非空但整轮剩余数已变"的情形，并在"第一张入堆叠后已无剩余"时自动关窗。
        /// </summary>
        public void UpdateRemainingTriggerCount(int count) => SetRemainingTriggerCount(count);

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
