using System;
using System.Collections.Generic;
using Godot;
using Script.Data.Cards;

namespace Script.UI
{
    /// <summary>联机触发排序窗口内的一个条目（由 ArenaOl 依据服务端请求构造，窗口只负责展示）。</summary>
    public class OnlineTriggerEntry
    {
        /// <summary>条目身份（用于"逐次入堆叠"多轮请求之间匹配同一触发，通常是来源卡 UID，回退显示名）。</summary>
        public string OptionKey;
        /// <summary>条目名（触发名，单机为 StackEffectInstance.DisplayName）。</summary>
        public string Name;
        /// <summary>效果文本（单机为 StackEffectInstance.DisplayText）。</summary>
        public string EffectText;
        /// <summary>是否强制（强制触发不可拖入放弃区，且窗口不可整体放弃）。</summary>
        public bool Mandatory;
        /// <summary>来源卡模板（用于显示卡图；可为 null）。</summary>
        public PlayerCardData SourceTemplate;
    }

    /// <summary>
    /// 【联机专用·触发效果排序窗口】对齐单机 `TriggerOrderWindow` 的**视觉与操作**：
    ///  - 入堆叠区（左→右＝先→后入栈）/ 放弃区（仅非强制可拖入，可拖回）；
    ///  - 两个按钮：第一张进入堆叠 / 完成排序并放弃下方效果（**仅入堆叠区为空时可点，不看放弃区**＝放弃剩余）；
    ///  - **自动关闭**：只要"排序框与放弃框皆空"即静默关闭窗口（仅 Hide、不回包）。
    ///    前提（既定规则）：无合法目标的效果不入排序窗、入堆叠不可取消 ⇒"客户端两框全空"等价于"服务端剩余为空"。
    ///    收到同 ctx 的下一轮请求＝排序未结束（刷新窗口，绝不关窗）；若窗口已被静默关闭而服务端仍下发请求，
    ///    `ArenaOl` 会重新弹出（`OpenOrUpdate` 内 `Popup`）；
    ///  - 直接复用共享的纯 View 组件 `TriggerEntryUI`（条目）与 `TriggerOrderRoot`（排布与拖拽），**不改这两个类**。
    ///
    /// 与单机的差异（仅交互落点，不涉规则）：
    ///  - 单机"第一张进入堆叠"本地直接入栈；联机把"本次入堆叠的条目下标顺序"回包给服务端，由服务端入栈；
    ///  - 逐次入堆叠会产生多轮请求-响应（`OrderContextId` 相同）→ 本窗口**保持打开**并在新请求到达时刷新条目，
    ///    被入堆叠的条目随即消失（服务端的"剩余集合"里已不含它们）。
    ///
    /// 纪律：本类为纯 View，不引用任何 Manager、不写 Model；提交只回调调用方（ArenaOl → ChoiceResult 上行）。
    /// </summary>
    [GlobalClass]
    public partial class OnlineTriggerOrderWindow : Window
    {
        [Export] public Control OrderZoneContainer;
        [Export] public Control DiscardZoneContainer;
        [Export] public Button FirstEnterButton;
        [Export] public Button DoneButton;
        [Export] public Label TitleLabel;
        [Export] public Label HintLabel;
        [Export] public Control DragHandle;

        /// <summary>提交"本次入堆叠的条目下标顺序"（下标指向本次请求的条目列表）；窗口不关闭，等服务端下一轮。</summary>
        public Action<List<int>> OnSubmitIndices;
        /// <summary>点"完成排序并放弃下方效果"（＝放弃剩余，仅入堆叠区为空时可点）时回调；服务端据此结束本 owner 的排序。</summary>
        public Action OnDone;
        /// <summary>本轮提交是否为「第一张进入堆叠」（仅用于日志/追踪；自动关闭已改由双框全空判定驱动）。</summary>
        public bool LastSubmitWasFirstEnter { get; private set; }

        /// <summary>当前排序窗口归属玩家（先手=1 / 后手=2）。</summary>
        public int OwnerPlayerId { get; private set; }

        private TriggerOrderRoot _root;
        private PackedScene _entryScene;
        private string _contextId;
        private readonly List<OnlineTriggerEntry> _entries = new List<OnlineTriggerEntry>();

        // 上一轮条目的落区状态（用于"逐次入堆叠"后把仍被玩家留在放弃区的条目放回放弃区）
        private sealed class Placement { public string Key; public bool Discard; public bool Consumed; }
        private readonly List<Placement> _prevPlacement = new List<Placement>();

        // 提交后等服务端下一轮请求期间冻结按钮（带超时兜底，避免服务端异常时窗口卡死）
        private bool _waitingForServer;
        private double _waitSeconds;
        // 等待服务端"下一条消息"（含下一轮排序请求、选目标请求、快照等）的宽限时长。
        // 必须显著大于"服务端两次消息之间"的正常间隔：选目标路径下，玩家可能长时间停留在选目标窗口，
        // 此期间本窗口处于 _waitingForServer 但**不应**被判定为"排序已结束"。
        // 超时后仅恢复按钮可用（不自动关窗）——窗口不能永久冻结，但关窗统一交给"双框全空"判定。
        private const double WaitTimeoutSeconds = 3.0;

        public override void _Ready()
        {
            if (FirstEnterButton != null) FirstEnterButton.Pressed += OnFirstEnterPressed;
            if (DoneButton != null) DoneButton.Pressed += OnDonePressed;
            _root = GetNodeOrNull<TriggerOrderRoot>("RootControl");
            if (_root != null) _root.OnOrderZoneChanged += RefreshButtonStates;
            _entryScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/TriggerEntryUI.tscn");
        }

        public override void _Process(double delta)
        {
            if (!_waitingForServer) return;
            _waitSeconds += delta;
            if (_waitSeconds < WaitTimeoutSeconds) return;
            // 超时兜底：服务端长时间未再下发任何消息（异常/丢包），窗口不能永久冻结。
            // 注意：**不自动关窗**——「第一张进入堆叠」的触发若需选目标，玩家可能在选目标窗口停留很久
            // （选目标上限 60s），此时服务端只是"在等你选目标"，并非排序已结束；若此处关窗会误弃剩余触发。
            // 关窗统一由"双框全空"判定（OpenOrUpdate 收到下一轮请求会复位本计时）。此处仅恢复按钮可用。
            _waitingForServer = false;
            GD.PrintErr("[OnlineTriggerOrderWindow] 等待服务端消息超时，恢复按钮可用（不自动关窗）");
            SetButtonsEnabled(true);
        }

        /// <summary>
        /// 双框全空 → 静默关闭（仅隐藏窗口，**不回包**）。
        /// 判定依据：客户端 "排序框 + 放弃框" 子节点数均为 0。
        /// 正确性前提（项目既定规则，已确认）：① 场上无合法目标的效果不会进入排序窗；② 进入堆叠后不可取消。
        /// 故"客户端两框全空"严格等价于"服务端本轮剩余待排序触发为空" → 排序自然结束，直接关窗收尾。
        ///
        /// 与手动「完成排序」的区别：手动路径走 <see cref="OnDonePressed"/> → `OnDone?.Invoke()` 回包 Cancelled=true；
        /// 本静默路径**只 Hide**，因为每张触发提交时已各自回包过入堆叠顺序，剩余本就为空，无需再发放弃语义。
        /// 若服务端（异常情况下）仍下发同 ctx 的下一轮请求，`ArenaOl` 会重新弹出本窗口（`OpenOrUpdate` 内部 Popup）。
        /// </summary>
        private void SilentCloseIfBothZonesEmpty()
        {
            if (!Visible) return;
            int orderCount = OrderZoneContainer?.GetChildCount() ?? 0;
            int discardCount = DiscardZoneContainer?.GetChildCount() ?? 0;
            if (orderCount > 0 || discardCount > 0) return;
            GD.Print("[OnlineTriggerOrderWindow] 排序框与放弃框皆空 → 静默关闭排序窗口（不回包）");
            _waitingForServer = false;
            _waitSeconds = 0;
            LastSubmitWasFirstEnter = false;
            Hide();
        }

        /// <summary>结束本次排序（回包路径）：先关闭再回调调用方（`ArenaOl` 据 `OnDone` 回包 Cancelled 并释放窗口）。
        /// 仅用于"服务端确认排序结束"，**不**用于静默自动关闭。</summary>
        private void RaiseDone()
        {
            _waitingForServer = false;
            LastSubmitWasFirstEnter = false;
            _waitSeconds = 0;
            Hide();
            OnDone?.Invoke();
        }

        // 与单机 TriggerOrderWindow 完全对称：用 Window 自身的 _Input（全局输入）驱动条目拖拽。
        public override void _Input(InputEvent @event)
        {
            if (!Visible || _root == null) return;
            var globalMouse = GetViewport().GetMousePosition();
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (_root.BeginEntryDrag(globalMouse)) GetViewport().SetInputAsHandled();
                }
                else if (_root.IsEntryDragging)
                {
                    _root.EndEntryDrag(globalMouse);
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (@event is InputEventMouseMotion && _root.IsEntryDragging)
            {
                _root.MoveEntryDrag(globalMouse);
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>
        /// 打开或刷新排序窗口。contextId 相同＝同一 owner 的同一轮排序（逐次入堆叠的多轮请求）→ 复用本窗口并保留
        /// 放弃区落区；contextId 不同＝新的排序轮次 → 清空上一轮落区记忆。
        /// </summary>
        public void OpenOrUpdate(string contextId, int ownerPlayerId, List<OnlineTriggerEntry> entries)
        {
            if (!string.Equals(_contextId, contextId, StringComparison.Ordinal))
            {
                _contextId = contextId;
                _prevPlacement.Clear();
            }
            OwnerPlayerId = ownerPlayerId;

            _entries.Clear();
            if (entries != null) _entries.AddRange(entries);

            RebuildEntries();

            if (!Visible)
            {
                // 【2026-10-20】居中唯一实现（原先取自身视口 ⇒ 落在屏幕左上角）
                WindowCenter.PopupCentered(this);
            }

            _waitingForServer = false;
            _waitSeconds = 0;
            LastSubmitWasFirstEnter = false;   // 新一轮请求到达 → 由本轮提交重新置位
            SetButtonsEnabled(true);
            // 边界兜底：服务端下发的条目列表为空（理论上不会，但防御上游异常）→ 双框全空 → 静默关闭。
            SilentCloseIfBothZonesEmpty();
        }

        /// <summary>按当前条目列表重建条目节点，并恢复上一轮的放弃区落区。</summary>
        private void RebuildEntries()
        {
            if (_root == null || _entryScene == null) return;

            var uis = new List<TriggerEntryUI>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e == null) continue;
                var ui = _entryScene.Instantiate<TriggerEntryUI>();
                // TriggerData 只作为"下标载体"（TriggerOrderRoot 不读取它），回包时据此得到服务端 OptionUids 的下标。
                ui.Setup(e.Name ?? "触发", e.EffectText ?? "", e.Mandatory, i, e.SourceTemplate);
                uis.Add(ui);
            }

            // 先全部放入入堆叠区（TriggerOrderRoot.Setup 的既定行为），再按上一轮落区把放弃区条目搬回去
            _root.Setup(uis);

            var discardUis = new List<TriggerEntryUI>();
            foreach (var ui in uis)
            {
                int idx = ui.TriggerData is int v ? v : -1;
                if (idx < 0 || idx >= _entries.Count) continue;
                if (!_entries[idx].Mandatory && WasDiscarded(_entries[idx].OptionKey)) discardUis.Add(ui);
            }
            foreach (var ui in discardUis)
            {
                if (ui.GetParent() == DiscardZoneContainer) continue;
                ui.GetParent()?.RemoveChild(ui);
                DiscardZoneContainer?.AddChild(ui);
                ui.CurrentZone = 2;
            }
            _root.ArrangeOrderZone();
            RecordPlacement();
        }

        /// <summary>上一轮是否把该条目（按 OptionKey 一对一消费匹配）留在了放弃区。</summary>
        private bool WasDiscarded(string key)
        {
            foreach (var p in _prevPlacement)
            {
                if (p.Consumed || p.Key != key) continue;
                p.Consumed = true;
                return p.Discard;
            }
            return false;
        }

        /// <summary>记录本轮条目的落区状态，供下一轮恢复。</summary>
        private void RecordPlacement()
        {
            _prevPlacement.Clear();
            foreach (var ui in EnumerateAllEntries())
            {
                int idx = ui.TriggerData is int v ? v : -1;
                string key = (idx >= 0 && idx < _entries.Count) ? _entries[idx].OptionKey : null;
                _prevPlacement.Add(new Placement
                {
                    Key = key,
                    Discard = ui.CurrentZone == 2,
                    Consumed = false,
                });
            }
        }

        private IEnumerable<TriggerEntryUI> EnumerateAllEntries()
        {
            foreach (var zone in new[] { OrderZoneContainer, DiscardZoneContainer })
            {
                if (zone == null) continue;
                foreach (var c in zone.GetChildren())
                    if (c is TriggerEntryUI t) yield return t;
            }
        }

        /// <summary>当前入堆叠区条目（按左→右顺序）对应的请求下标。</summary>
        private List<int> CurrentOrderIndices()
        {
            var list = new List<int>();
            if (OrderZoneContainer == null) return list;
            foreach (var c in OrderZoneContainer.GetChildren())
                if (c is TriggerEntryUI t && t.TriggerData is int idx) list.Add(idx);
            return list;
        }

        /// <summary>
        /// 刷新按钮状态并做"双框全空"自动关闭判定。
        /// 绑定到 `TriggerOrderRoot.OnOrderZoneChanged`（条目在排序框/放弃框间移动后）；同时对外暴露，
        /// 便于测试与调用方在条目集合变更后主动驱动一次刷新。
        /// </summary>
        public void RefreshButtonStates()
        {
            SetButtonsEnabled(true);
            // 拖拽在排序框/放弃框之间移动条目后：若两框皆空 → 静默关闭（覆盖"提交后残留空窗"等边界）。
            SilentCloseIfBothZonesEmpty();
        }

        /// <summary>
        /// 控制两个按钮的可用状态（入堆叠区为空时"完成排序"可点；否则可入堆叠）。
        /// 「完成排序」启用口径与单机一致：**仅排序框（入堆叠区）为空时可点，不看放弃区**。
        /// </summary>
        public void SetButtonsEnabled(bool enabled)
        {
            int orderCount = OrderZoneContainer?.GetChildCount() ?? 0;
            if (FirstEnterButton != null) FirstEnterButton.Disabled = !enabled || orderCount == 0;
            // 对齐单机：仅当入堆叠区为空时可"完成排序"（＝放弃剩余；强制触发不可能在放弃区，故此时必无强制待入栈）
            if (DoneButton != null) DoneButton.Disabled = !enabled || orderCount > 0;
            if (_root != null) _root.SetDragFrozen(!enabled);
        }

        private void OnFirstEnterPressed()
        {
            var indices = CurrentOrderIndices();
            if (indices.Count == 0) return;
            GD.Print("[OnlineTriggerOrderWindow] 第一张进入堆叠");
            Submit(new List<int> { indices[0] }, isFirstEnter: true);
        }

        private void OnDonePressed()
        {
            if ((OrderZoneContainer?.GetChildCount() ?? 0) > 0) return;   // 双保险：入堆叠区非空不可完成
            GD.Print("[OnlineTriggerOrderWindow] 完成排序并放弃下方效果");
            LastSubmitWasFirstEnter = false;
            Hide();
            OnDone?.Invoke();
        }

        /// <summary>提交本次入堆叠的下标顺序：冻结按钮、移除已提交条目、回调调用方（等服务端下一轮）。</summary>
        private void Submit(List<int> indices, bool isFirstEnter = false)
        {
            if (indices == null || indices.Count == 0) return;
            LastSubmitWasFirstEnter = isFirstEnter;
            var submitted = new HashSet<int>(indices);
            foreach (var ui in new List<TriggerEntryUI>(EnumerateAllEntries()))
            {
                if (ui.TriggerData is int idx && submitted.Contains(idx))
                {
                    ui.GetParent()?.RemoveChild(ui);
                    ui.QueueFree();
                }
            }
            _root?.ArrangeOrderZone();
            RecordPlacement();

            _waitingForServer = true;
            _waitSeconds = 0;
            SetButtonsEnabled(false);
            OnSubmitIndices?.Invoke(indices);

            // 提交并移除条目后：若排序框与放弃框都已为空（＝本轮全部处理完）→ 静默关闭排序窗口（不回包）。
            // 服务端若仍有剩余（异常兜底）会下发同 ctx 下一轮请求 → ArenaOl 重新弹出并刷新。
            SilentCloseIfBothZonesEmpty();
        }

        /// <summary>关闭窗口（新的排序轮次由 ArenaOl 决定：不同 contextId 时销毁重建）。</summary>
        public void CloseAndFree()
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            Hide();
            QueueFree();
        }
    }
}
