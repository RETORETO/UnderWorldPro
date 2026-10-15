using Godot;
using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Script.UI
{
    /// <summary>
    /// 触发效果排序窗口（独立 Window 弹出，模态独占）。
    /// 【2026-10·触发窗口改造】由"双区拖拽（入堆叠区 + 放弃区）"改为**单一效果列表 + 三个入口**：
    ///  - **效果列表区**：统一展示本窗口内全部"待排序"（尚未入堆叠）的触发效果，左→右即入堆叠先后；
    ///  - **双击**某条目 → 该效果入堆叠（需选目标时进入既有选目标流程；选目标被取消则该效果留在列表）；
    ///  - **全部入堆叠**：按列表顺序先逐条**预选目标**，任一取消则整体中止（一条都不入堆叠）；全部成功再统一入堆叠；
    ///  - **放弃剩余效果**：放弃列表内全部剩余触发并关窗（**列表内存在强制效果时置灰不可点**）；
    ///  - **自动关闭**：列表空了（全部处理完）自动关窗并推进流程。
    ///
    /// 纪律：本窗口只负责"展示 + 收集玩家意图"，入堆叠/选目标/强制判定/放弃规则全部由
    /// TurnManager 的既有链路执行（OnEnterStack / OnEnterAllStack / OnDone 三个回调）。
    /// 拖拽排序与鼠标接管由子节点 TriggerOrderRoot(Control) 负责（Window 非 Control，无 _GuiInput）。
    /// </summary>
    public partial class TriggerOrderWindow : Window
    {
        [Export] public Control OrderZoneContainer;
        /// <summary>放弃区容器。【2026-10】单机新排序窗口不再使用（可为 null）；保留导出仅为兼容联机/旧场景。</summary>
        [Export] public Control DiscardZoneContainer;
        /// <summary>「全部入堆叠」按钮（新增）。</summary>
        [Export] public Button AllEnterButton;
        /// <summary>「放弃剩余效果」按钮。</summary>
        [Export] public Button DoneButton;
        [Export] public Label TitleLabel;
        [Export] public Label HintLabel;
        [Export] public Control DragHandle;

        /// <summary>当前排序窗口归属玩家（先手=1 / 后手=2）。</summary>
        public int OwnerPlayerId { get; private set; }

        /// <summary>单条入堆叠回调（双击）：参数为要入堆叠的触发数据列表（本窗口恒为 1 条）。不关闭窗口。</summary>
        public System.Action<List<object>> OnEnterStack;
        /// <summary>【2026-10】「全部入堆叠」回调：参数为**按列表左→右顺序**的全部待处理触发数据。</summary>
        public System.Action<List<object>> OnEnterAllStack;
        /// <summary>完成/确认回调：关窗后触发，用于推进到下一个所有者窗口。</summary>
        public System.Action OnDone;

        private TriggerOrderRoot _root;

        /// <summary>本窗口是否已弹出（用于"只在开窗后才允许自动关窗"的兜底）。</summary>
        public bool IsOpen => Visible;

        /// <summary>本次排序尚未入堆叠的触发总数（由 Arena/TurnManager 注入，**仅作诊断日志**）。
        /// 【2026-10】自动关窗口径已简化为"列表为空"，本计数不再作为门槛（保留以免改动既有调用点）。</summary>
        private int _remainingTriggerCount = -1;

        /// <summary>选目标等外部流程要求的整体禁用（由协调器 SetButtonsEnabled 驱动）。</summary>
        private bool _externalEnabled = true;
        /// <summary>单条入堆叠在途（双击后到引擎回填 RemoveEntries 之间）。</summary>
        private bool _busy;
        /// <summary>「全部入堆叠」批量在途（预选 + 入堆叠期间，禁止一切重入）。</summary>
        private bool _batchInProgress;
        /// <summary>本窗口已 Open 过（未 Close）。</summary>
        private bool _opened;
        /// <summary>本次会话已关窗（防止 OnDone 被重复交付）。</summary>
        private bool _closed;

        public override void _Ready()
        {
            if (AllEnterButton != null) AllEnterButton.Pressed += OnAllEnterPressed;
            if (DoneButton != null) DoneButton.Pressed += OnDonePressed;
            _root = GetNode<TriggerOrderRoot>("RootControl");
            _root.OnOrderZoneChanged += RefreshButtonStates;
        }

        /// <summary>注入"本次排序仍未处理的触发总数"（仅诊断用）。在窗口打开**前**调用只记录数值。</summary>
        public void SetRemainingTriggerCount(int count)
        {
            _remainingTriggerCount = count;
            GD.Print($"[TriggerOrderWindow] 引擎剩余触发计数 = {count}（诊断）");
        }

        /// <summary>由 TurnManager/协调器在推进时刷新剩余计数（诊断）。</summary>
        public void UpdateRemainingTriggerCount(int count) => SetRemainingTriggerCount(count);

        // ============================================================
        // 入口：打开 / 关闭
        // ============================================================

        /// <summary>打开排序窗口：注入归属玩家与待排序触发条目。</summary>
        public void Open(int ownerPlayerId, IEnumerable<TriggerEntryUI> entries)
        {
            OwnerPlayerId = ownerPlayerId;
            _busy = false;
            _batchInProgress = false;
            _opened = true;
            _closed = false;
            _externalEnabled = true;

            // 水平、垂直双向居中
            var vp = GetViewport().GetVisibleRect().Size;
            Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)((vp.Y - Size.Y) / 2));
            Popup(); // Window 显示并定位
            var list = new List<TriggerEntryUI>(entries);
            _root?.Setup(list);
            ApplyButtonStates();
            TryAutoCloseIfEmpty();
        }

        /// <summary>关闭窗口并推进（OnDone 每会话只交付一次）。</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _opened = false;
            _busy = false;
            _batchInProgress = false;
            Hide(); // Window 隐藏
            OnDone?.Invoke();
        }

        // ============================================================
        // 列表内容查询
        // ============================================================

        /// <summary>当前效果列表内条目（按顺序），供外部查询。</summary>
        public Godot.Collections.Array<Godot.Node> GetOrderZoneChildren()
            => OrderZoneContainer != null ? OrderZoneContainer.GetChildren() : new Godot.Collections.Array<Godot.Node>();

        /// <summary>放弃区条目（单机新窗口恒为空；保留以兼容联机/旧调用点）。</summary>
        public Godot.Collections.Array<Godot.Node> GetDiscardZoneChildren()
            => DiscardZoneContainer != null ? DiscardZoneContainer.GetChildren() : new Godot.Collections.Array<Godot.Node>();

        private int EntryCount => OrderZoneContainer?.GetChildCount() ?? 0;

        /// <summary>列表内是否存在强制效果（强制触发不可放弃 ⇒ 「放弃剩余效果」置灰）。</summary>
        private bool AnyMandatory()
        {
            if (OrderZoneContainer == null) return false;
            foreach (var c in OrderZoneContainer.GetChildren())
                if (c is TriggerEntryUI t && t.IsMandatory) return true;
            return false;
        }

        /// <summary>按列表左→右顺序取出待处理触发的原始数据（StackEffectInstance），供批量入堆叠。</summary>
        private List<StackEffectInstance> CurrentOrderedStackEffects()
        {
            var list = new List<StackEffectInstance>();
            if (OrderZoneContainer == null) return list;
            foreach (var c in OrderZoneContainer.GetChildren())
                if (c is TriggerEntryUI t && t.TriggerData is StackEffectInstance si) list.Add(si);
            return list;
        }

        // ============================================================
        // 按钮 / 拖拽可用性（单一收口）
        // ============================================================

        /// <summary>
        /// 控制整体可用状态（选目标期间由协调器调用 disabled=false 之外的语义保持兼容：
        /// 参数 enabled=false ⇒ 整体禁用；true ⇒ 恢复为"按列表内容自动判定"）。
        /// </summary>
        public void SetButtonsEnabled(bool enabled)
        {
            _externalEnabled = enabled;
            ApplyButtonStates();
        }

        /// <summary>按当前状态重算两个按钮与拖拽冻结（唯一收口，避免散落的 Disabled 赋值）。
        /// · 全部入堆叠：可操作 且 列表非空；
        /// · 放弃剩余效果：可操作 且 列表非空 且 **无强制效果**。</summary>
        private void ApplyButtonStates()
        {
            bool interactive = _externalEnabled && !_busy && !_batchInProgress;
            bool hasEntries = EntryCount > 0;
            if (AllEnterButton != null) AllEnterButton.Disabled = !interactive || !hasEntries;
            if (DoneButton != null) DoneButton.Disabled = !interactive || !hasEntries || AnyMandatory();
            _root?.SetDragFrozen(!interactive);
        }

        private void RefreshButtonStates() => ApplyButtonStates();

        // ============================================================
        // 输入：双击入堆叠 / 拖拽排序
        // ============================================================

        // 与 InvestigationWindow 完全对称：用 Window 自身的 _Input（全局输入）驱动条目交互，
        // 不受子 Control 的 mouse_filter 限制。仅命中条目或正在拖拽时才 SetInputAsHandled，避免吞掉其它输入。
        public override void _Input(InputEvent @event)
        {
            if (!Visible) return;
            bool interactive = _externalEnabled && !_busy && !_batchInProgress;
            var globalMouse = GetViewport().GetMousePosition();
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (!interactive) return;
                    // 【2026-10】双击条目 ＝ 该效果入堆叠（先于拖拽拾取判定，避免第二次按下被当成新拖拽）。
                    if (mb.DoubleClick)
                    {
                        var hit = _root?.PickEntryAt(globalMouse);
                        if (hit != null)
                        {
                            OnEntryDoubleClicked(hit);
                            GetViewport().SetInputAsHandled();
                            return;
                        }
                    }
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

        /// <summary>双击条目：把该效果交给既有单条入堆叠链路（需选目标则由引擎弹原选目标窗）。
        /// 条目**不在此处移除**——由引擎在"实际入堆叠/取消"回填 RemoveEntries 时统一处理，
        /// 因此"选目标被取消"时条目会自然留在列表内。</summary>
        private void OnEntryDoubleClicked(TriggerEntryUI entry)
        {
            if (entry == null || _busy || _batchInProgress) return;
            if (!(entry.TriggerData is StackEffectInstance)) return;
            _busy = true;
            ApplyButtonStates();
            GD.Print($"[TriggerOrderWindow] 双击入堆叠: {entry.NameLabel?.Text}");
            OnEnterStack?.Invoke(new List<object> { entry.TriggerData });
        }

        // ============================================================
        // 「全部入堆叠」
        // ============================================================

        private void OnAllEnterPressed()
        {
            if (_batchInProgress || _busy) return;      // 重入防护
            if (EntryCount == 0) return;
            var ordered = CurrentOrderedStackEffects();
            if (ordered.Count == 0) return;
            if (OnEnterAllStack == null)
            {
                GD.PrintErr("[TriggerOrderWindow] 未装配「全部入堆叠」处理器 → 忽略本次点击");
                return;
            }
            _batchInProgress = true;
            ApplyButtonStates();                        // 冻结按钮与拖拽（批量期间不让改顺序）
            GD.Print($"[TriggerOrderWindow] 全部入堆叠：{ordered.Count} 条（先按序预选目标）");
            OnEnterAllStack(new List<object>(ordered));
        }

        /// <summary>由协调器在批量流程结束后调用（**必须**恰好一次）。
        /// pushedAny=false（预选被取消 ⇒ 整体中止）⇒ 列表保持原样、恢复可用；
        /// pushedAny=true ⇒ 引擎已回填 RemoveEntries，此处补一次"空列表自动关窗"判定。</summary>
        public void NotifyBatchFinished(bool pushedAny)
        {
            _batchInProgress = false;
            ApplyButtonStates();
            if (pushedAny) TryAutoCloseIfEmpty();
        }

        // ============================================================
        // 「放弃剩余效果」
        // ============================================================

        private void OnDonePressed()
        {
            if (_batchInProgress || _busy) return;
            if (EntryCount == 0) return;                 // 空列表本应已自动关窗
            if (AnyMandatory())
            {
                GD.Print("[TriggerOrderWindow] 放弃剩余效果被忽略：列表内存在强制效果");
                return;
            }
            GD.Print("[TriggerOrderWindow] 放弃剩余效果（放弃列表内全部未处理触发）");
            Close();                                     // → Hide + OnDone（推进到下一个所有者窗口）
        }

        // ============================================================
        // 入堆叠回填 / 自动关窗
        // ============================================================

        /// <summary>
        /// 触发实际处理完后由 TurnManager/协调器调用：从列表移除**已了结**的条目。
        /// · consumed 内含已入堆叠、以及"无合法目标而跳过 / 模式或支付被否定"的触发；
        /// · **玩家取消取目标**的触发不在 consumed 中 ⇒ 保留在列表内（可重新处理）。
        /// 取目标的触发在玩家选完目标后才回填，故条目会保留到选目标完成。
        /// </summary>
        public void RemoveEntries(List<StackEffectInstance> consumed)
        {
            _busy = false;                               // 单条入堆叠在途结束（含"被取消"的情形）
            if (consumed != null && consumed.Count > 0)
            {
                foreach (var t in consumed)
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
            }
            _root?.ArrangeOrderZone();
            ApplyButtonStates();
            TryAutoCloseIfEmpty();
        }

        /// <summary>列表为空 ⇒ 自动关窗并推进（唯一自动关闭口径）。
        /// 门槛：本窗已 Open、且当前不在批量/单条流程中（避免把"过程态"误判为"已无剩余"）。</summary>
        private void TryAutoCloseIfEmpty()
        {
            if (!_opened || _closed) return;
            if (_batchInProgress || _busy) return;
            if (EntryCount > 0) return;
            GD.Print("[TriggerOrderWindow] 列表已无待排序效果 → 自动关闭排序窗口");
            Close();
        }
    }
}
