using Godot;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// TriggerOrderWindow(Window) 内的根 Control，负责触发条目的排布与拖拽计算。
    /// 与 InvestigationWindow 的结构一致：拖拽排序的鼠标接管由 TriggerOrderWindow(Window) 的
    /// _Input（全局输入）统一驱动，本类只暴露 Pick/Move/Drop 方法，用 GetViewport().GetMousePosition()
    /// 传入的全局坐标做命中与落点计算（与 InvestigationWindow._Input 完全对称）。
    /// </summary>
    [GlobalClass]
    public partial class TriggerOrderRoot : Control
    {
        [Export] public Control OrderZoneContainer;
        [Export] public Control DiscardZoneContainer;
        [Export] public Control DragHandle;

        private List<TriggerEntryUI> _entries = new();

        /// <summary>条目在 OrderZone / DiscardZone 间移动后触发，供 TriggerOrderWindow 刷新按钮状态
        /// （如：有卡离开 OrderZone 后是否需要允许"完成排序/放弃剩余触发"）。</summary>
        public System.Action OnOrderZoneChanged;

        // 整体拖动窗口用的状态
        private bool _dragging = false;
        private Vector2 _dragOffset;

        // 条目拖拽状态
        private TriggerEntryUI _dragEntry;
        private Vector2 _entryDragOffset;
        private Control _dragFromZone;

        // 拖拽冻结标志：选目标窗口打开期间由 TriggerOrderWindow.SetButtonsEnabled 联动置 true，
        // 与三个按钮禁用同步，禁止拾取/移动条目以及整体拖动窗口（修复：选目标期间仍可拖拽条目的问题）。
        private bool _dragFrozen;

        public override void _Ready()
        {
            if (DragHandle != null)
            {
                DragHandle.GuiInput += OnDragHandleGuiInput;
                DragHandle.MouseFilter = Control.MouseFilterEnum.Stop;
            }
            // 让 Panel 与两个 Zone 不消费鼠标事件；条目拖拽改由 TriggerOrderWindow._Input（全局输入）驱动，
            // 因此本 RootControl 不再需要 _GuiInput，保持默认（tscn 中 mouse_filter=Pass）即可。
            var panel = GetNodeOrNull<Control>("Panel");
            if (panel != null) panel.MouseFilter = MouseFilterEnum.Pass;
            if (OrderZoneContainer != null) OrderZoneContainer.MouseFilter = MouseFilterEnum.Pass;
            if (DiscardZoneContainer != null) DiscardZoneContainer.MouseFilter = MouseFilterEnum.Pass;
        }

        /// <summary>由 TriggerOrderWindow.Open 调用，注入待排序条目。</summary>
        public void Setup(List<TriggerEntryUI> entries)
        {
            _entries = entries;
            ClearContainer(OrderZoneContainer);
            ClearContainer(DiscardZoneContainer);
            foreach (var e in entries)
            {
                e.Set("layout_mode", 0);
                e.Set("size_flags_horizontal", 0);
                e.CustomMinimumSize = new Vector2(110, 150);
                e.MouseFilter = MouseFilterEnum.Pass;
                e.CurrentZone = 1;
                OrderZoneContainer.AddChild(e);
            }
            ArrangeOrderZone();
        }

        // ===== 整体窗口拖动（DragHandle）=====
        private void OnDragHandleGuiInput(InputEvent @event)
        {
            if (_dragFrozen) return;   // 选目标期间冻结：禁止整体拖动窗口
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _dragging = true; _dragOffset = GetGlobalMousePosition() - GlobalPosition; }
                else _dragging = false;
            }
            else if (@event is InputEventMouseMotion mm && _dragging)
            {
                GlobalPosition = GetGlobalMousePosition() - _dragOffset;
            }
        }

        // ===== 条目拖拽（由 TriggerOrderWindow._Input 统一驱动，与 InvestigationWindow 对称）=====
        public bool IsEntryDragging => _dragEntry != null;

        /// <summary>由 TriggerOrderWindow.SetButtonsEnabled 联动调用：选目标窗口打开期间冻结所有拖拽。
        /// 冻结时若正在拖拽则立即归位，避免残留半拖状态。</summary>
        public void SetDragFrozen(bool frozen)
        {
            _dragFrozen = frozen;
            if (frozen && _dragEntry != null)
            {
                // 立即结束当前拖拽并归位
                _dragEntry.ZIndex = 0;
                _dragEntry = null;
                _dragFromZone = null;
                ArrangeOrderZone();
            }
        }

        /// <summary>尝试在全局坐标 globalMouse 处拾取一个条目并开始拖拽。返回是否命中。</summary>
        public bool BeginEntryDrag(Vector2 globalMouse)
        {
            if (_dragFrozen) return false;  // 选目标期间冻结：禁止拾取新条目
            if (_dragging) return false; // 整体窗口拖动进行中，不抢条目拖拽
            var hit = PickEntryAt(globalMouse);
            if (hit == null) return false;
            _dragEntry = hit;
            _dragFromZone = hit.GetParent() as Control;
            _entryDragOffset = globalMouse - _dragEntry.GlobalPosition;
            _dragEntry.ZIndex = 100;
            GD.Print($"[触发排序] 开始拖拽条目 Name={_dragEntry.NameLabel?.Text} from={(_dragFromZone == OrderZoneContainer ? "Order" : "Discard")}");
            return true;
        }

        public void MoveEntryDrag(Vector2 globalMouse)
        {
            if (_dragFrozen || _dragEntry == null) return;
            _dragEntry.GlobalPosition = globalMouse - _entryDragOffset;
        }

        public void EndEntryDrag(Vector2 globalMouse)
        {
            if (_dragEntry == null) return;
            DropEntryAt(globalMouse);
            _dragEntry = null;
            _dragFromZone = null;
        }

        private TriggerEntryUI PickEntryAt(Vector2 globalMouse)
        {
            TriggerEntryUI best = null;
            int bestZ = int.MinValue;
            foreach (var zone in new[] { OrderZoneContainer, DiscardZoneContainer })
            {
                if (zone == null) continue;
                foreach (var c in zone.GetChildren())
                {
                    if (c is TriggerEntryUI t && t.Visible)
                    {
                        var r = new Rect2(t.GlobalPosition, t.Size);
                        if (r.HasPoint(globalMouse) && t.ZIndex >= bestZ) { best = t; bestZ = t.ZIndex; }
                    }
                }
            }
            GD.Print($"[触发排序] PickEntryAt 命中={(best != null ? best.NameLabel?.Text : "null")} pos={globalMouse}");
            return best;
        }

        private void DropEntryAt(Vector2 globalMouse)
        {
            GD.Print($"[触发排序] 松开拖拽 落点={globalMouse}");
            var orderRect = OrderZoneContainer.GetGlobalRect();
            var discardRect = DiscardZoneContainer.GetGlobalRect();
            GD.Print($"[触发排序] OrderRect={orderRect} DiscardRect={discardRect}");
            GD.Print($"[触发排序] OrderSize={OrderZoneContainer.Size} DiscardSize={DiscardZoneContainer.Size}");

            Control targetZone = null;
            bool inOrder = orderRect.HasPoint(globalMouse);
            bool inDiscard = discardRect.HasPoint(globalMouse);
            if (inDiscard) targetZone = DiscardZoneContainer;        // 优先放弃区，避免与堆叠区重叠时误判
            else if (inOrder) targetZone = OrderZoneContainer;
            else
            {
                // 都不命中：选鼠标中心更近的 zone（更稳健，避免布局偏差导致无法归位/丢弃）
                float dOrder = globalMouse.DistanceTo(orderRect.GetCenter());
                float dDiscard = globalMouse.DistanceTo(discardRect.GetCenter());
                targetZone = dDiscard < dOrder ? DiscardZoneContainer : OrderZoneContainer;
                GD.Print($"[触发排序] 落点未命中任何zone，按中心距离选择 nearest={(targetZone == DiscardZoneContainer ? "Discard" : "Order")}");
            }

            if (targetZone == DiscardZoneContainer && _dragEntry.IsMandatory)
            {
                GD.Print($"[DIAG TriggerOrderRoot] 强制触发不可放入放弃区，归位");
                ArrangeOrderZone();
                return;
            }

            if (_dragEntry.GetParent() != targetZone)
            {
                _dragEntry.GetParent()?.RemoveChild(_dragEntry);
                targetZone.AddChild(_dragEntry);
            }

            int insertIndex = GetInsertIndex(targetZone, globalMouse);
            int currentIndex = -1;
            var children = targetZone.GetChildren();
            for (int k = 0; k < children.Count; k++)
            {
                if (children[k] == _dragEntry) { currentIndex = k; break; }
            }
            if (currentIndex >= 0 && currentIndex != insertIndex)
                targetZone.MoveChild(_dragEntry, insertIndex);

            _dragEntry.CurrentZone = (targetZone == OrderZoneContainer) ? 1 : 2;
            GD.Print($"[触发排序] 落点结果 zone={(targetZone == OrderZoneContainer ? "Order" : "Discard")} IsMandatory={_dragEntry.IsMandatory}");
            ArrangeOrderZone();
            OnOrderZoneChanged?.Invoke();
        }

        private int GetInsertIndex(Control zone, Vector2 globalMouse)
        {
            float localX = globalMouse.X - zone.GetGlobalRect().Position.X;
            const float cw = 110f, gap = 14f;
            int idx = Mathf.FloorToInt(localX / (cw + gap));
            idx = Mathf.Clamp(idx, 0, Mathf.Max(0, zone.GetChildCount() - 1));
            return idx;
        }

        /// <summary>手动锚点排布各 Zone 内条目（父是普通 Control，不自动布局）。</summary>
        public void ArrangeOrderZone()
        {
            const float cw = 110f, ch = 150f, gap = 14f;
            foreach (var zone in new[] { OrderZoneContainer, DiscardZoneContainer })
            {
                var items = new List<TriggerEntryUI>();
                foreach (var c in zone.GetChildren())
                    if (c is TriggerEntryUI t) items.Add(t);
                int n = items.Count;
                float containerW = zone.Size.X;
                float containerH = zone.Size.Y;
                float totalW = n * cw + (n - 1) * gap;
                float startX = Mathf.Max(0, (containerW - totalW) / 2f);
                float y = Mathf.Max(0, (containerH - ch) / 2f);
                for (int i = 0; i < n; i++)
                {
                    var it = items[i];
                    it.AnchorLeft = 0; it.AnchorTop = 0; it.AnchorRight = 0; it.AnchorBottom = 0;
                    it.OffsetLeft = startX + i * (cw + gap);
                    it.OffsetTop = y;
                    it.OffsetRight = it.OffsetLeft + cw;
                    it.OffsetBottom = it.OffsetTop + ch;
                    it.MouseFilter = MouseFilterEnum.Pass;
                }
            }
        }

        private void ClearContainer(Node container)
        {
            if (container == null) return;
            foreach (var c in container.GetChildren())
                c.QueueFree();
        }
    }
}
