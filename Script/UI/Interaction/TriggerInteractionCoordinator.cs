using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.UI;
using Script.Interfaces;
using System;
using System.Collections.Generic;

namespace Script.UI.Interaction
{
    /// <summary>
    /// 【MVC 修复 M-08】触发效果交互协调器。
    /// 原触发排序 / 选目标交互逻辑散落在 Arena（约 1464–1672 行），使 Arena 同时承担
    /// "渲染编排根" 与 "触发交互协调器" 双重职责，正是 M-02（开局编排被 View 蚕食）、
    /// M-04（规则判定泄漏到 View）的根因来源之一。
    ///
    /// 本类显式实现 ITriggerView（此前由 Arena 隐式实现并 this 注入 TurnManager），
    /// 把触发排序窗口、选目标窗口、地区高亮、模态锁协同等全部收归此类。
    /// Arena 不再实现 ITriggerView，仅持有一个本类实例并将其引用注入 TurnManager，
    /// 依赖方向仍是：UI → 接口 ← Controller，Controller 不直引任何具体 UI 类型。
    ///
    /// 设计约束（零行为变更）：
    /// - 窗口/场景引用与 _areaSlots 由 Arena 在装配时注入；
    /// - 模态锁（LockModal/UnlockModal）与场上卡命中测试（PickAreaCardAt）属 Arena 根职责，
    ///   通过委托暴露给本类，避免把 Arena 的私有状态搬过来造成重复真相。
    /// </summary>
    public partial class TriggerInteractionCoordinator : Node, ITriggerView
    {
        // ---- 由 Arena 注入的依赖 ----
        private TriggerOrderWindow _triggerOrderWindow;
        private TargetSelectionWindow _targetSelectionWindow;
        private PackedScene _triggerEntryScene;
        private List<AreaSlot> _areaSlots = new List<AreaSlot>();

        /// <summary>场上卡命中测试委托（指向 Arena.PickAreaCardAt）。</summary>
        private Func<Vector2, CardUI> _pickAreaCardAt;
        /// <summary>模态锁（指向 Arena 的 LockModal/UnlockModal，内部已通知 Controller 维护原子锁）。</summary>
        private Action _lockTriggerOrderModal;
        private Action _unlockTriggerOrderModal;
        /// <summary>选目标窗口模态锁（独立类型 TargetSelection，与触发排序 TriggerOrder 区分，避免 HashSet 去重冲突）。</summary>
        private Action _lockTargetSelectionModal;
        private Action _unlockTargetSelectionModal;

        /// <summary>装配：注入窗口/场景引用、地区槽位、命中测试与模态锁委托。</summary>
        public void Configure(
            TriggerOrderWindow triggerOrderWindow,
            TargetSelectionWindow targetSelectionWindow,
            PackedScene triggerEntryScene,
            List<AreaSlot> areaSlots,
            Func<Vector2, CardUI> pickAreaCardAt,
            Action lockTriggerOrderModal,
            Action unlockTriggerOrderModal,
            Action lockTargetSelectionModal,
            Action unlockTargetSelectionModal)
        {
            _triggerOrderWindow = triggerOrderWindow;
            _targetSelectionWindow = targetSelectionWindow;
            _triggerEntryScene = triggerEntryScene;
            _areaSlots = areaSlots ?? new List<AreaSlot>();
            _pickAreaCardAt = pickAreaCardAt;
            _lockTriggerOrderModal = lockTriggerOrderModal;
            _unlockTriggerOrderModal = unlockTriggerOrderModal;
            _lockTargetSelectionModal = lockTargetSelectionModal;
            _unlockTargetSelectionModal = unlockTargetSelectionModal;
        }

        // ============================================================
        // ITriggerView 实现
        // ============================================================

        /// <summary>打开触发排序窗口。ownerPlayerId 为排序归属玩家；triggers 为已过滤（含合法目标）的触发实例。</summary>
        public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
            Action<List<StackEffectInstance>> onEnterStack, Action onDone)
        {
            if (_triggerOrderWindow == null || _triggerEntryScene == null) return;

            var entries = new List<TriggerEntryUI>();
            foreach (var t in triggers)
            {
                var entry = _triggerEntryScene.Instantiate<TriggerEntryUI>();
                var sourceTemplate = t.Source?.PlayerTemplate;
                // 强制触发（EffectData.Forced=true，如 JC068 阿格斯IV准备维护）标记为强制，
                // 供 TriggerOrderRoot 阻止拖入放弃区（新规范下仅此路径读取 Forced 作为 IsMandatory）。
                bool isMandatory = t.EffectDataRef?.Forced == true;
                entry.Setup(t.DisplayName, t.DisplayText, isMandatory, t, sourceTemplate);
                entries.Add(entry);
            }

            _triggerOrderWindow.OnEnterStack = (orderedObjs) =>
            {
                var ordered = new List<StackEffectInstance>();
                foreach (var o in orderedObjs)
                    if (o is StackEffectInstance ti) ordered.Add(ti);
                onEnterStack?.Invoke(ordered);
            };
            _triggerOrderWindow.OnDone = () =>
            {
                _unlockTriggerOrderModal?.Invoke();   // 本次排序结束，解冻出牌/Pass（窗口为复用单例，非 QueueFree）
                _triggerOrderWindow.Visible = false;
                onDone?.Invoke();
            };

            _lockTriggerOrderModal?.Invoke();
            _triggerOrderWindow.Open(ownerPlayerId, entries);
        }

        /// <summary>触发实际入栈后由 TurnManager 调用：从 OrderZone 移除已入栈条目。
        /// 取目标触发在玩家选完目标后才触发本方法，故条目保留到选目标完成（修复"卡提前从 OrderZone 消失"）。</summary>
        public void RemoveOrderEntries(List<StackEffectInstance> pushed)
        {
            _triggerOrderWindow?.RemoveEntries(pushed);
        }

        /// <summary>
        /// 请求玩家为触发效果选择合法目标。打开选目标提示窗口，记录合法目标集合；
        /// 玩家点选合法卡/地区后回调 onPicked(targetUid) 并关闭窗口。窗口为模态独占。
        /// </summary>
        public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids, Action<string> onPicked)
        {
            if (_targetSelectionWindow == null) return;
            _targetSelectionActive = true;
            _onTargetPicked = onPicked;   // 关键：记录调用方回调，选完目标后据此推入堆叠（否则回调链中断，触发永不入栈）
            GD.Print($"[TriggerCoordinator] RequestTargetSelection: 触发={trigger?.DisplayName}, 目标数={validTargetUids.Count}, 是地区目标={trigger?.IsAreaTarget == true}");
            _validTargetUids = new HashSet<string>(validTargetUids);
            _validAreaUids.Clear();
            _targetIsArea = trigger?.IsAreaTarget == true;

            // 进入选目标前先清残留高光（窗口打开瞬间即取消旧高光），避免上一次交互遗留。
            CardManager.Instance.NotifyTargetSelectionChanged(null);

            // 机动：高亮所有合法目标地区槽位（区别于"选场上卡"）。
            if (_targetIsArea)
            {
                foreach (var uid in validTargetUids)
                    _validAreaUids.Add(uid);
                foreach (var slot in _areaSlots)
                {
                    bool selectable = _validAreaUids.Contains(slot.AreaUID);
                    slot.IsTargetSelectable = selectable;
                    slot.SetHighlight(selectable);
                    if (selectable) slot.OnAreaClicked = OnAreaSlotPicked;
                }
            }
            else
            {
                // 卡目标：经总线广播合法目标集合，由 Arena 订阅后给对应 CardUI 上黄色高光（MVC 红线：Controller 不直接调 View）。
                CardManager.Instance.NotifyTargetSelectionChanged(validTargetUids);
            }

            string hint = validTargetUids.Count == 0
                ? "（无合法目标）"
                : (_targetIsArea
                    ? "请点击一个高亮的地区（将移动至该地区）。"
                    : "请在场上选择合法目标卡（点击场上卡完成选择）。");
            OpenTargetSelection(trigger.DisplayName, hint);

            // 选目标期间禁用触发排序窗口的三个操作按钮
            _triggerOrderWindow?.SetButtonsEnabled(false);

            // 选目标模态独占：加锁，禁止期间激活/出牌其他卡（原子锁，修复"选目标时还能激活"）
            _lockTargetSelectionModal?.Invoke();

            // 注册一次性回调：选完关闭窗口并通知调用方
            _targetSelectionWindow.OnTargetPicked = (uid) =>
            {
                GD.Print($"[TriggerCoordinator] OnTargetPicked 收到回调 UID={uid}，准备转发给调用方 _onTargetPicked（挂接={_onTargetPicked != null}）");
                _unlockTargetSelectionModal?.Invoke();   // 先解锁（选目标已结束），再继续后续流程（入栈会自行 EnterAtomic）
                _targetSelectionActive = false;
                // 选目标窗口关闭：统一经总线取消高光（卡片目标 + 地区槽位）。
                CardManager.Instance.NotifyTargetSelectionChanged(null);
                _validTargetUids.Clear();
                _validAreaUids.Clear();
                // 清除可能的地区高亮与可选状态
                foreach (var slot in _areaSlots)
                {
                    slot.SetHighlight(false);
                    slot.IsTargetSelectable = false;
                    slot.OnAreaClicked = null;
                }
                _targetSelectionWindow.Visible = false;
                // 选目标结束，恢复按钮状态
                _triggerOrderWindow?.SetButtonsEnabled(true);
                _onTargetPicked?.Invoke(uid);
                _onTargetPicked = null;
            };

            if (validTargetUids.Count == 0)
                GD.Print($"[TriggerCoordinator] 触发 {trigger.DisplayName} 无合法目标，选目标窗口不展示（已过滤）");
        }

        /// <summary>
        /// 泛用选目标入口（激活效果/通用分支用，不依赖 StackEffectInstance）。
        /// 复用同一套 TargetSelectionWindow 与卡高光总线，激活效果②（牺牲并横置目标角色）即走此路径。
        /// </summary>
        public void RequestActivateTargetSelection(string effectName, bool isArea, List<string> validTargetUids, Action<string> onPicked)
        {
            if (_targetSelectionWindow == null) return;
            _targetSelectionActive = true;
            _onTargetPicked = onPicked;
            GD.Print($"[TriggerCoordinator] RequestActivateTargetSelection: 效果={effectName}, 目标数={validTargetUids?.Count ?? 0}, 是地区目标={isArea}");
            _validTargetUids = new HashSet<string>(validTargetUids ?? new List<string>());
            _validAreaUids.Clear();
            _targetIsArea = isArea;

            CardManager.Instance.NotifyTargetSelectionChanged(null);

            if (_targetIsArea)
            {
                foreach (var uid in _validTargetUids)
                    _validAreaUids.Add(uid);
                foreach (var slot in _areaSlots)
                {
                    bool selectable = _validAreaUids.Contains(slot.AreaUID);
                    slot.IsTargetSelectable = selectable;
                    slot.SetHighlight(selectable);
                    if (selectable) slot.OnAreaClicked = OnAreaSlotPicked;
                }
            }
            else
            {
                CardManager.Instance.NotifyTargetSelectionChanged(new List<string>(_validTargetUids));
            }

            string hint = _validTargetUids.Count == 0
                ? "（无合法目标）"
                : (_targetIsArea
                    ? "请点击一个高亮的地区。"
                    : "请在场上选择合法目标卡（点击场上卡完成选择）。");
            OpenTargetSelection(effectName, hint);

            _triggerOrderWindow?.SetButtonsEnabled(false);

            // 选目标模态独占：加锁，禁止期间激活/出牌其他卡（原子锁，修复"选目标时还能激活"）
            _lockTargetSelectionModal?.Invoke();

            _targetSelectionWindow.OnTargetPicked = (uid) =>
            {
                GD.Print($"[TriggerCoordinator] 激活选目标 OnTargetPicked UID={uid}");
                _unlockTargetSelectionModal?.Invoke();   // 先解锁（选目标已结束），再继续后续流程（入栈会自行 EnterAtomic）
                _targetSelectionActive = false;
                CardManager.Instance.NotifyTargetSelectionChanged(null);
                _validTargetUids.Clear();
                _validAreaUids.Clear();
                foreach (var slot in _areaSlots)
                {
                    slot.SetHighlight(false);
                    slot.IsTargetSelectable = false;
                    slot.OnAreaClicked = null;
                }
                _targetSelectionWindow.Visible = false;
                _triggerOrderWindow?.SetButtonsEnabled(true);
                _onTargetPicked?.Invoke(uid);
                _onTargetPicked = null;
            };

            if (_validTargetUids.Count == 0)
                GD.Print($"[TriggerCoordinator] 激活效果 {effectName} 无合法目标，选目标窗口不展示（已过滤）");
        }

        // ============================================================
        // 选目标状态机（原 Arena 私有字段与方法，迁移至此）
        // ============================================================

        private bool _targetSelectionActive;
        /// <summary>选目标交互是否进行中（供 Arena 输入路由判断是否拦截点击）。</summary>
        public bool IsTargetSelectionActive => _targetSelectionActive;
        private bool _targetIsArea;          // true=目标为地区槽位（机动）；false=目标为场上卡
        private HashSet<string> _validTargetUids = new HashSet<string>();
        private HashSet<string> _validAreaUids = new HashSet<string>();
        private Action<string> _onTargetPicked;

        /// <summary>打开选目标提示窗口。effectName/hint 用于展示；选择由 Arena 转发场上卡点击。</summary>
        public void OpenTargetSelection(string effectName, string hint)
        {
            if (_targetSelectionWindow == null) return;
            // 选目标需点击穿透到下层（场上卡 / 高亮地区槽位），故关闭全屏遮罩的点击拦截
            _targetSelectionWindow.SetBlocking(false);
            _targetSelectionWindow.Open(effectName, hint);
        }

        /// <summary>AreaSlot._GuiInput 直接回传的点击（机动取地区目标）。仅当选目标激活且 UID 合法时生效。</summary>
        private void OnAreaSlotPicked(string areaUid)
        {
            if (!_targetSelectionActive) return;
            if (!_validAreaUids.Contains(areaUid))
            {
                GD.Print($"[TriggerCoordinator] OnAreaSlotPicked 忽略非法地区 UID={areaUid}（合法集={string.Join(",", _validAreaUids)}）");
                return;
            }
            GD.Print($"[TriggerCoordinator] OnAreaSlotPicked 命中合法地区 UID={areaUid}，转发选目标窗口");
            _targetSelectionWindow.PickFromArena(areaUid);
        }

        /// <summary>选目标窗口激活时，检测玩家点击是否为合法目标（场上卡或可选地区槽位）；是则选中并回调。</summary>
        public void HandleTargetSelectionClick(Vector2 globalPos)
        {
            // 机动：点击高亮的合法地区槽位即完成选择
            if (_targetIsArea)
            {
                foreach (var slot in _areaSlots)
                {
                    if (_validAreaUids.Contains(slot.AreaUID) && slot.ContainsGlobalPoint(globalPos))
                    {
                        GD.Print($"[TriggerCoordinator] HandleTargetSelectionClick 命中高亮地区 UID={slot.AreaUID}，提交选目标");
                        _targetSelectionWindow.PickFromArena(slot.AreaUID);
                        return;
                    }
                }
                GD.Print($"[TriggerCoordinator] HandleTargetSelectionClick（地区模式）未命中合法地区，合法集={string.Join(",", _validAreaUids)} 点击={globalPos}");
                return;
            }

            // 默认：点击合法场上卡
            var hit = _pickAreaCardAt?.Invoke(globalPos);
            if (hit == null || hit.Instance == null) return;
            string uid = hit.Instance.UID;
            if (_validTargetUids.Contains(uid))
                _targetSelectionWindow.PickFromArena(uid);
        }
    }
}
