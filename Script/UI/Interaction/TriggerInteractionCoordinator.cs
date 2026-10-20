using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Game.Entities;
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
    public partial class TriggerInteractionCoordinator : Node, ITriggerView, ITriggerBatchView
    {
        // ---- 由 Arena 注入的依赖 ----
        private TriggerOrderWindow _triggerOrderWindow;
        private TargetSelectionWindow _targetSelectionWindow;
        private PackedScene _triggerEntryScene;
        private List<AreaSlot> _areaSlots = new List<AreaSlot>();

        /// <summary>场上卡命中测试委托（指向 Arena.PickAreaCardAt）。</summary>
        private Func<Vector2, CardUI> _pickAreaCardAt;
        /// <summary>【选卡/找卡 UI 优化·批次 1】**选目标专用**的候选命中委托：
        /// 输入（全局坐标, 本次候选 UID 列表）→ 命中的 UID / null。指向 `CardDragController.PickCandidateCardUidAt`。
        /// 与 `_pickAreaCardAt`（拖拽落点语义）分开，避免"为了选目标而扩拖拽命中范围"。</summary>
        private Func<Vector2, IReadOnlyList<string>, string> _pickCandidateCardUidAt;
        /// <summary>模态锁（指向 Arena 的 LockModal/UnlockModal，内部已通知 Controller 维护原子锁）。</summary>
        private Action _lockTriggerOrderModal;
        private Action _unlockTriggerOrderModal;
        /// <summary>选目标窗口模态锁（独立类型 TargetSelection，与触发排序 TriggerOrder 区分，避免 HashSet 去重冲突）。</summary>
        private Action _lockTargetSelectionModal;
        private Action _unlockTargetSelectionModal;

        // ---- 【选卡/找卡 UI 优化·批次 2】选择窗（cat-2）依赖 ----
        /// <summary>打开卡面网格选择窗（指向 `Arena.OpenCardPickWindow`；返回 null ＝ 不可用，调用方回退提示窗）。</summary>
        private Func<CardPickRequest, CardPickWindow> _openCardPickWindow;
        /// <summary>UID → 卡实例（分类与窗口内容都用它；指向 `CardManager.GetInstance`）。</summary>
        private Func<string, CardInstance> _resolveCard;
        /// <summary>玩家 id → 显示名（窗口区域标签用；指向 Arena 的注入实现）。</summary>
        private Func<int, string> _playerNameOf;
        /// <summary>地区 UID → 地区编号（窗口区域标签用）。</summary>
        private Func<string, int> _areaIndexOf;
        /// <summary>当前这次取目标是否走"选择窗模式"（含 cat-2）。</summary>
        private bool _pickWindowMode;
        /// <summary>当前打开的选择窗实例（场上点击 → `SelectByUid` 用）。</summary>
        private CardPickWindow _activePickWindow;

        /// <summary>
        /// 【选卡/找卡 UI 优化·批次 2】注入"选择窗模式"的依赖（与 `Configure` 分开，
        /// 避免改动既有装配签名影响其他调用方/测试）。
        /// </summary>
        public void ConfigureCardPick(
            Func<CardPickRequest, CardPickWindow> openCardPickWindow,
            Func<string, CardInstance> resolveCard,
            Func<int, string> playerNameOf,
            Func<string, int> areaIndexOf)
        {
            _openCardPickWindow = openCardPickWindow;
            _resolveCard = resolveCard;
            _playerNameOf = playerNameOf;
            _areaIndexOf = areaIndexOf;
        }

        /// <summary>本次候选是否必须走选择窗（以 `chooserId` 为"自己"判定 cat-1/cat-2）。</summary>
        private bool MustUsePickWindow(IEnumerable<string> uids, int chooserId)
        {
            if (_openCardPickWindow == null || uids == null) return false;
            return CardPickChannel.ClassifyUids(uids, _resolveCard, chooserId) == CardPickChannelKind.Window;
        }

        /// <summary>把 UID 列表解成卡实例（保持原顺序；解析不到的跳过）。</summary>
        private List<CardInstance> ResolveCards(IEnumerable<string> uids)
        {
            var list = new List<CardInstance>();
            if (uids == null || _resolveCard == null) return list;
            foreach (var uid in uids)
            {
                var inst = _resolveCard(uid);
                if (inst != null) list.Add(inst);
            }
            return list;
        }

        /// <summary>
        /// 【批次 2·4.5】**打开"选择窗模式"的窗口**并挂好一次性回调。
        /// · 确认 ⇒ 收尾（解锁/清高亮）后把 UID 交回调用方；
        /// · 取消 ⇒ `AllowCancel` 为假时窗口根本不显示取消按钮，故此处只在允许时接线。
        /// 返回 false ⇒ 窗口不可用（调用方回退提示窗，绝不静默中断）。
        /// </summary>
        private bool OpenPickWindowMode(string title, string hint, IEnumerable<string> uids, int chooserId,
            bool allowCancel, string cancelLabel,
            Action<string> onConfirmed, Action onCancelled)
        {
            if (_openCardPickWindow == null) return false;
            var cards = ResolveCards(uids);
            if (cards.Count == 0) return false;

            var win = _openCardPickWindow.Invoke(new CardPickRequest
            {
                Title = title,
                Hint = hint,
                Cards = cards,
                ChooserId = chooserId,
                PlayerNameOf = _playerNameOf,
                AreaIndexOf = _areaIndexOf,
                AllowCancel = allowCancel,
                CancelLabel = string.IsNullOrEmpty(cancelLabel) ? "取消" : cancelLabel,
                OnPicked = inst => onConfirmed?.Invoke(inst?.UID),
                OnCancelled = () => onCancelled?.Invoke(),
            });
            if (win == null) return false;

            _activePickWindow = win;
            _pickWindowMode = true;
            return true;
        }

        /// <summary>收尾时静默关闭选择窗（不回调），避免"场上右键取消"后再交付一次 OnCancelled。</summary>
        private void ClosePickWindowSilently()
        {
            if (_activePickWindow != null && GodotObject.IsInstanceValid(_activePickWindow))
                _activePickWindow.CloseSilently();
            _activePickWindow = null;
            _pickWindowMode = false;
        }

        /// <summary>装配：注入窗口/场景引用、地区槽位、命中测试与模态锁委托。</summary>
        public void Configure(
            TriggerOrderWindow triggerOrderWindow,
            TargetSelectionWindow targetSelectionWindow,
            PackedScene triggerEntryScene,
            List<AreaSlot> areaSlots,
            Func<Vector2, CardUI> pickAreaCardAt,
            Func<Vector2, IReadOnlyList<string>, string> pickCandidateCardUidAt,
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
            _pickCandidateCardUidAt = pickCandidateCardUidAt;
            _lockTriggerOrderModal = lockTriggerOrderModal;
            _unlockTriggerOrderModal = unlockTriggerOrderModal;
            _lockTargetSelectionModal = lockTargetSelectionModal;
            _unlockTargetSelectionModal = unlockTargetSelectionModal;
        }

        // ============================================================
        // ITriggerView 实现
        // ============================================================

        // ---- 【2026-10·触发窗口改造】批量入堆叠（ITriggerBatchView）状态 ----
        /// <summary>「全部入堆叠」处理器（由 TurnManager 注入；见 <see cref="SetBatchEnterHandler"/>）。</summary>
        private Action<List<StackEffectInstance>, Action<bool>> _batchEnterHandler;
        /// <summary>批量预选目标进行中：期间选目标窗带「取消」出口，且排序窗由窗口自身冻结（协调器不再改禁用态）。</summary>
        private bool _batchSelectMode;

        /// <summary>【ITriggerBatchView】注入「全部入堆叠」处理器。</summary>
        public void SetBatchEnterHandler(Action<List<StackEffectInstance>, Action<bool>> handler)
            => _batchEnterHandler = handler;

        /// <summary>打开触发排序窗口。ownerPlayerId 为排序归属玩家；triggers 为已过滤（含合法目标）的触发实例。</summary>
        public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
            Action<List<StackEffectInstance>> onEnterStack, Action onDone)
        {
            if (_triggerOrderWindow == null || _triggerEntryScene == null) return;
            _batchSelectMode = false;

            var entries = new List<TriggerEntryUI>();
            foreach (var t in triggers)
            {
                var entry = _triggerEntryScene.Instantiate<TriggerEntryUI>();
                var sourceTemplate = t.Source?.PlayerTemplate;
                // 强制触发（EffectData.Forced=true，如 JC068 阿格斯IV准备维护）标记为强制，
                // 供排序窗口「放弃剩余效果」置灰（新规范下仅此路径读取 Forced 作为 IsMandatory）。
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

            // 【2026-10·触发窗口改造】「全部入堆叠」：整份顺序交给 TurnManager 做"先批量预选目标 → 再统一入堆叠"。
            _triggerOrderWindow.OnEnterAllStack = (orderedObjs) =>
            {
                var ordered = new List<StackEffectInstance>();
                foreach (var o in orderedObjs)
                    if (o is StackEffectInstance ti) ordered.Add(ti);
                if (_batchEnterHandler == null)
                {
                    GD.PrintErr("[TriggerCoordinator] ⚠ 未装配批量入堆叠处理器 → 「全部入堆叠」忽略（窗口保持原样）");
                    _triggerOrderWindow.NotifyBatchFinished(false);
                    return;
                }
                _batchSelectMode = true;   // 预选阶段：选目标窗带「取消」出口
                _batchEnterHandler(ordered, pushedAny =>
                {
                    _batchSelectMode = false;
                    _triggerOrderWindow.NotifyBatchFinished(pushedAny);
                });
            };

            _triggerOrderWindow.OnDone = () =>
            {
                _unlockTriggerOrderModal?.Invoke();   // 本次排序结束，解冻出牌/Pass（窗口为复用单例，非 QueueFree）
                _triggerOrderWindow.Visible = false;
                onDone?.Invoke();
            };

            _lockTriggerOrderModal?.Invoke();
            // 先记录"本轮待处理触发总数"再开窗（仅诊断；自动关窗口径＝窗口列表为空，见 TriggerOrderWindow）。
            _triggerOrderWindow.SetRemainingTriggerCount(triggers?.Count ?? 0);
            _triggerOrderWindow.Open(ownerPlayerId, entries);
        }

        /// <summary>
        /// 触发实际处理完后由 TurnManager 调用：从排序窗口列表移除**已了结**的条目。
        /// `consumed` ＝ 已入堆叠 / 无合法目标而跳过 / 模式或支付被否定 的触发；
        /// **玩家在取目标时取消的触发不在其中**（＝不入堆叠，但保留在窗口内，玩家可重新处理）。
        /// 取目标触发在玩家选完目标后才触发本方法，故条目保留到选目标完成。
        /// 移除后由窗口自身判定"列表空了 → 自动关窗"（口径单一，收敛在 TriggerOrderWindow）。
        /// </summary>
        public void RemoveOrderEntries(List<StackEffectInstance> consumed)
            => _triggerOrderWindow?.RemoveEntries(consumed);

        /// <summary>
        /// 请求玩家为触发效果选择合法目标。打开选目标提示窗口，记录合法目标集合；
        /// 玩家点选合法卡/地区后回调 onPicked(targetUid) 并关闭窗口。窗口为模态独占。
        /// </summary>
        public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids, Action<string> onPicked)
        {
            if (_targetSelectionWindow == null) return;
            // 【2026-09 修复·用户实测"多弹了一个选择框"】已有一次选目标在等玩家 ⇒ 本次是重复请求，忽略。
            if (_targetSelectionActive)
            {
                GD.Print($"[TriggerCoordinator] ⛔ 已有选目标窗在等玩家 → 忽略重复请求（触发={trigger?.DisplayName}）");
                return;
            }
            _targetSelectionActive = true;
            _onTargetPicked = onPicked;   // 关键：记录调用方回调，选完目标后据此推入堆叠（否则回调链中断，触发永不入栈）
            GD.Print($"[TriggerCoordinator] RequestTargetSelection: 触发={trigger?.DisplayName}, 目标数={validTargetUids.Count}, 是地区目标={trigger?.IsAreaTarget == true}");
            _validTargetUids = new HashSet<string>(validTargetUids);
            _validAreaUids.Clear();
            _targetIsArea = trigger?.IsAreaTarget == true;

            // 【选卡/找卡 UI 优化·批次 2】按"逐卡实例"分类：含 cat-2（牌库/墓地/资产/结附·封印于卡…）
            // ⇒ **选择窗模式**（场上仍高亮，但点击只置选中、确认才提交）；全 cat-1 ⇒ 维持提示窗模式。
            // chooser ＝ 该触发效果的控制者（`StackEffectInstance.ControllerId`），不新增接口参数
            //（`ITriggerView.RequestTargetSelection` 被约 40 个测试桩实现，签名必须稳定）。
            int chooserId = trigger?.ControllerId ?? 0;
            bool pickWindow = !_targetIsArea && MustUsePickWindow(_validTargetUids, chooserId);
            _pickWindowMode = false;   // 防御：上一次选择若异常收尾，别把"窗模式"带进本次单选

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

            // 【2026-10·提示文字】地区选择窗的提示**由效果数据给出**（`EffectData.Hint`，如机动的
            // "将移动至该地区"）；未声明时用中性文案。原先硬编码移动提示 ⇒ 非移动类效果会显示错误提示。
            string effectHint = trigger?.EffectDataRef?.Hint;
            string hint = validTargetUids.Count == 0
                ? "（无合法目标）"
                : (_targetIsArea
                    ? (string.IsNullOrEmpty(effectHint)
                        ? "请点击一个高亮的地区。"
                        : $"请点击一个高亮的地区（{effectHint}）。")
                    : "请在场上选择合法目标卡（点击场上卡完成选择）。");
            // 【批次2】选择窗模式下改由 `CardPickWindow` 承载（见下方向统一开启），此处不弹提示窗。
            if (!pickWindow)
                OpenTargetSelection(trigger.DisplayName, hint, extraLabel: null,
                    allowCancel: true, cancelLabel: "取消（不入堆叠）");

            // 选目标期间禁用触发排序窗口的操作按钮。
            // 【2026-10】批量预选阶段不改禁用态：排序窗口已由自身 _batchInProgress 冻结，
            // 此处若置 false/true 会在批量中途把窗口解冻（批量结束由 NotifyBatchFinished 统一恢复）。
            if (!_batchSelectMode) _triggerOrderWindow?.SetButtonsEnabled(false);

            // 选目标模态独占：加锁，禁止期间激活/出牌其他卡（原子锁，修复"选目标时还能激活"）
            _lockTargetSelectionModal?.Invoke();

            // 选中/取消共用的收尾（解锁 → 清高亮 → 关窗 → 恢复排序窗按钮），保证"回调唯一性"。
            void FinishTargetSelection()
            {
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
                ClosePickWindowSilently();
                _targetSelectionWindow.Visible = false;
                _targetSelectionWindow.OnCancelled = null;
                _targetSelectionWindow.OnTargetPicked = null;   // 一次性：防止收尾后重复投递（重复解锁/重复入堆叠）
                // 选目标结束，恢复按钮状态（批量预选期间不改，由 NotifyBatchFinished 统一恢复）
                if (!_batchSelectMode) _triggerOrderWindow?.SetButtonsEnabled(true);
            }

            // 注册一次性回调：选完关闭窗口并通知调用方
            _targetSelectionWindow.OnTargetPicked = (uid) =>
            {
                GD.Print($"[TriggerCoordinator] OnTargetPicked 收到回调 UID={uid}，准备转发给调用方 _onTargetPicked（挂接={_onTargetPicked != null}）");
                var cb = _onTargetPicked;
                _onTargetPicked = null;
                FinishTargetSelection();
                cb?.Invoke(uid);
            };

            // 【2026-10·触发窗口改造】取消出口：玩家在选目标窗点「取消」⇒ 该效果**不入堆叠**，
            // 但保留在触发排序窗口（可重新双击/全部入堆叠处理）。以**空 UID** 通知调用方。
            _targetSelectionWindow.OnCancelled = () =>
            {
                var cb = _onTargetPicked;
                _onTargetPicked = null;
                GD.Print($"[TriggerCoordinator] 选目标被取消（{trigger?.DisplayName}）→ 该效果不入堆叠，保留在排序窗口");
                FinishTargetSelection();
                cb?.Invoke(null);
            };

            // 【批次2】选择窗模式：开启卡面网格窗（确认＝提交；取消＝不入堆叠、留在排序窗）
            if (pickWindow)
            {
                bool opened = OpenPickWindowMode(
                    trigger?.DisplayName, hint, validTargetUids, chooserId,
                    allowCancel: true, cancelLabel: "取消（不入堆叠）",
                    onConfirmed: uid =>
                    {
                        var cb = _onTargetPicked;
                        _onTargetPicked = null;
                        FinishTargetSelection();
                        cb?.Invoke(uid);
                    },
                    onCancelled: () =>
                    {
                        var cb = _onTargetPicked;
                        _onTargetPicked = null;
                        GD.Print($"[TriggerCoordinator] 选择窗被取消（{trigger?.DisplayName}）→ 该效果不入堆叠，保留在排序窗口");
                        FinishTargetSelection();
                        cb?.Invoke(null);
                    });
                if (!opened)
                {
                    // 窗口不可用 ⇒ 回退提示窗模式（绝不静默中断选择）
                    GD.PrintErr("[TriggerCoordinator] 选择窗不可用 → 回退提示窗模式");
                    OpenTargetSelection(trigger.DisplayName, hint, extraLabel: null,
                        allowCancel: true, cancelLabel: "取消（不入堆叠）");
                }
            }

            if (validTargetUids.Count == 0)
                GD.Print($"[TriggerCoordinator] 触发 {trigger.DisplayName} 无合法目标，选目标窗口不展示（已过滤）");
        }

        /// <summary>
        /// 泛用选目标入口（激活效果/通用分支用，不依赖 StackEffectInstance）。
        /// 复用同一套 TargetSelectionWindow 与卡高光总线，激活效果②（牺牲并横置目标角色）即走此路径。
        /// 【2026-09·K15 多目标场上点选】`extraLabel` + `onExtra` 给出该槽的**出口按钮**：
        ///  · 可留空的槽 → "不选（留空该槽）"；
        ///  · 必选槽 → "取消（不打出）"。
        /// 目的：多目标卡的"场上卡槽"也走**高亮 + 点击**（用户口径："选择查看的暗藏者应该选择场上的
        /// 高亮暗藏者，而不是弹出选择框"），同时保留退出通道，玩家不会卡在点不动的提示窗上。
        /// 返回 false ＝ **选目标窗不可用**（如编辑器未挂场景）→ 调用方必须回退到自己的列表窗，
        /// 否则该次取目标会静默中断（挂死在"等一个永不到来的回调"上）。
        ///
        /// 【2026-10 用户口径·JC108】**卡目标（非地区目标）默认不再弹提示窗**：玩家口径
        /// "JC108 以场上的卡为目标弹出了选择窗，需要**直接选择场上的高亮卡**"。
        /// 卡目标的"选中"本来就发生在**场上高亮卡**上（点击即完成选择），再叠一个居中的提示窗
        /// 反而挡住战场、让玩家以为要在窗里选。故 `hintWindow=false` 时：
        ///  · 只广播高亮（`NotifyTargetSelectionChanged`），**不开窗**；
        ///  · 若给了 `extraLabel`（如"取消（不发动）"），把该出口挂到窗上（窗不可见也不影响
        ///    场上点击与取消都能走完收尾）；未给则本次选择没有取消出口（＝必选一个目标）。
        /// 地区目标（`isArea=true`）必须保留窗口：地区槽位的高亮是几何提示，窗口承载"点哪个高亮地区"。
        /// </summary>
        public bool RequestActivateTargetSelection(string effectName, bool isArea, List<string> validTargetUids,
            Action<string> onPicked, string extraLabel = null, Action onExtra = null, string hint = null,
            int chooserPlayerId = 0, bool hintWindow = true)
        {
            if (_targetSelectionWindow == null)
            {
                GD.PrintErr($"[TriggerCoordinator] ⚠ 选目标窗不可用 → 无法为「{effectName}」开窗（调用方应回退列表窗）");
                return false;
            }
            // 【2026-09 修复·用户实测"多弹了一个选择框"】同上：已有一次选目标在等玩家 ⇒ 忽略重复请求。
            if (_targetSelectionActive)
            {
                GD.Print($"[TriggerCoordinator] ⛔ 已有选目标/多选窗在等玩家 → 忽略重复请求（{effectName}）");
                return false;
            }
            _targetSelectionActive = true;
            _onTargetPicked = onPicked;
            GD.Print($"[TriggerCoordinator] RequestActivateTargetSelection: 效果={effectName}, 目标数={validTargetUids?.Count ?? 0}, 是地区目标={isArea}");
            _validTargetUids = new HashSet<string>(validTargetUids ?? new List<string>());
            _validAreaUids.Clear();
            _targetIsArea = isArea;

            // 【选卡/找卡 UI 优化·批次 2】选择窗模式判定（逐卡实例；地区目标不参与）：
            // chooserPlayerId 由调用方传入（激活/取牌都是"某个玩家在做选择"，手牌归属据此判定）。
            // 取不到（0）时手牌按 cat-2 安全降级（多开一个窗，但绝不会"高亮了却点不到"）。
            bool pickWindow = !isArea && MustUsePickWindow(_validTargetUids, chooserPlayerId);
            _pickWindowMode = false;   // 防御：同上，先归零再由 `OpenPickWindowMode` 置真

            CardManager.Instance.NotifyTargetSelectionChanged(null);

            // 【2026-10 用户口径·JC108】卡目标是否**开提示窗**：地区目标必须开（窗口承载"点哪个高亮
            // 地区"的指引）；卡目标由调用方决定（激活效果取卡目标默认 `hintWindow=true` 保持不变，
            // 显式传 false ⇒ "直接选择场上的高亮卡"，不再被居中提示窗挡住战场）。
            bool showWindow = !pickWindow && (_targetIsArea || hintWindow);

            if (_targetIsArea && showWindow)
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

            // 【2026-10·JC092】提示语由**调用方按数据给出**（`TargetSlot.Hint`，卡文口径）；
            // 未给出时用**中性**文案——不再硬编码"将移动至该地区"（那是移动类效果的说法，
            // 用在"把墓地角色牌置于该地区"等效果上就是错误提示）。
            string resolvedHint = _validTargetUids.Count == 0
                ? "（无合法目标）"
                : (!_targetIsArea
                    ? "请在场上选择合法目标卡（点击场上卡完成选择）。"
                    : (string.IsNullOrEmpty(hint)
                        ? "请点击一个高亮的地区。"
                        : $"请点击一个高亮的地区（{hint}）。"));
            if (!showWindow)
            {
                // 卡目标不开窗（用户口径：直接点场上高亮卡）。出口（如"取消（不发动）"）由下方
                // `OnExtra` 保持可用；没给取消出口就是"必选一个目标"。
                GD.Print($"[TriggerCoordinator] 卡目标不弹提示窗（用户口径：直接点场上高亮卡）；" +
                         $"合法目标 {_validTargetUids.Count} 个，本槽出口={(string.IsNullOrEmpty(extraLabel) ? "无（必选）" : extraLabel)}");
            }
            else
            {
                OpenTargetSelection(effectName, resolvedHint, extraLabel);
            }

            _triggerOrderWindow?.SetButtonsEnabled(false);

            // 选目标模态独占：加锁，禁止期间激活/出牌其他卡（原子锁，修复"选目标时还能激活"）
            _lockTargetSelectionModal?.Invoke();

            // 选中/出口共用同一份收尾（解锁 + 清高亮 + 关闭窗口 + 恢复排序窗按钮）
            void FinishSelection()
            {
                _unlockTargetSelectionModal?.Invoke();   // 先解锁（选目标已结束），再继续后续流程（入栈会自行 EnterAtomic）
                _targetSelectionActive = false;
                ClosePickWindowSilently();
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
                _targetSelectionWindow.OnExtra = null;
                _targetSelectionWindow.OnTargetPicked = null;   // 一次性：防止收尾后重复投递再次触发收尾（重复解锁）
                _cancelActivateTargetSelection = null;
                _triggerOrderWindow?.SetButtonsEnabled(true);
            }

            // 【2026-10·JC108】取消出口的统一投递（窗口按钮 / 场上右键共用；**幂等**：`FinishSelection`
            // 先把 `_targetSelectionActive` 置假，重复投递直接忽略）。仅在调用方给了出口
            // （`onExtra != null`）时才结束本次选择——没给出口就是"必选一个目标"，此刻右键不生效
            // （与窗口上没有取消按钮一致，避免"关掉窗却仍在等目标"而挂死流程）。
            void InvokeExtraExit()
            {
                if (!_targetSelectionActive) return;
                if (onExtra == null)
                {
                    GD.Print($"[TriggerCoordinator] 本次取目标无出口（必选）→ 忽略退出请求（{effectName}）");
                    return;
                }
                FinishSelection();
                _onTargetPicked = null;
                onExtra.Invoke();
            }

            _targetSelectionWindow.OnTargetPicked = (uid) =>
            {
                // 幂等：窗口把回调挂在自身字段上（`PickFromArena` 不清理），同一帧/重复投递可能进来第二次。
                // 收尾只允许做一次，否则会重复 `UnlockModal` 把外层的原子锁一并掀掉。
                if (!_targetSelectionActive) return;
                GD.Print($"[TriggerCoordinator] 激活选目标 OnTargetPicked UID={uid}");
                // 【2026-10 修复·JC041 第二次弃牌不落地】**先取走再回调**：
                // 回调链里常会**立刻发起下一次选择**（如 JC041「双方各弃一张」：玩家1 弃完 → 玩家2 立刻弹窗）。
                // 旧写法把 `_onTargetPicked = null` 放在回调**之后**，于是内层刚装上的新回调被这行清空 ⇒
                // 第二次点选会打印 `OnTargetPicked` 却**没有任何人接收**（日志实测：点完牌不弃牌）。
                var cb = _onTargetPicked;
                _onTargetPicked = null;
                FinishSelection();
                cb?.Invoke(uid);
            };
            // 【2026-09·K15】该槽的出口按钮（留空该槽 / 取消不打出）：只是"离开本槽"的通道，不选目标
            _targetSelectionWindow.OnExtra = () =>
            {
                GD.Print($"[TriggerCoordinator] 本槽出口（{extraLabel}）：{effectName}");
                InvokeExtraExit();
            };
            // 供"卡目标不弹窗"路径使用：窗口不可见时，场上**右键**＝取消（用户口径：手牌行动等取目标可取消）。
            _cancelActivateTargetSelection = InvokeExtraExit;

            // 【批次2】选择窗模式：开卡面网格窗（确认＝提交目标；取消＝离开本槽，走调用方给的出口）
            if (pickWindow)
            {
                bool opened = OpenPickWindowMode(
                    effectName, resolvedHint, validTargetUids, chooserPlayerId,
                    allowCancel: onExtra != null,   // 没给出口 ⇒ 必选一个目标（窗口不显示取消按钮）
                    cancelLabel: string.IsNullOrEmpty(extraLabel) ? "取消" : extraLabel,
                    onConfirmed: uid =>
                    {
                        var cb = _onTargetPicked;
                        _onTargetPicked = null;
                        FinishSelection();
                        cb?.Invoke(uid);
                    },
                    onCancelled: () =>
                    {
                        _onTargetPicked = null;
                        FinishSelection();
                        onExtra?.Invoke();   // 取消＝离开本槽（与提示窗「取消」按钮同一出口）
                    });
                if (!opened)
                {
                    GD.PrintErr("[TriggerCoordinator] 选择窗不可用 → 回退提示窗模式");
                    _pickWindowMode = false;
                    OpenTargetSelection(effectName, resolvedHint, extraLabel);
                }
            }

            if (_validTargetUids.Count == 0)
                GD.Print($"[TriggerCoordinator] 激活效果 {effectName} 无合法目标，选目标窗口不展示（已过滤）");
            return true;
        }

        /// <summary>
        /// 【2026-09 用户口径 JC062/JC021】**"场上高亮 + 一个窗（左键选择、右键取消）+ 确认/取消"**取目标。
        ///
        /// 用户口径原文（JC062）："JC062 需要在选择目标的时候就弹出弹窗，场上高亮+弹窗提示选择目标，
        /// 弹窗可以确认/取消…配合场上高亮卡选择，**左键选择右键取消**，如果已经选择两个目标，
        /// **第三个目标不能点击**，点确认直接加入堆叠。（**选择 0 个目标也可以加入堆栈**）"；
        /// 用户给出的窗面：`请选择目标暗藏者：已选择（0/2）` / `左键选择右键取消` / `确认` `取消`。
        /// （JC021）窗面：`请选择目标角色（0/2）和目标地区（0/1）`＋"每个玩家场上的角色只能选择一个，
        /// 如果没有选择地区，或者一个角色都没有选择，**不能点确认**"。
        ///
        /// 与 `RequestActivateTargetSelection`（单选、点选即关窗）的区别：本模式**窗口保持打开**，
        /// 玩家用**左键加选、右键取消**，最后按"确认/取消"。已选清单与"能否确认"由本协调器按调用方
        /// 回调（`titleOf`/`canConfirm`/`canAddCard`）实时重算。
        /// `candidatesOf(state)` 每次选择变化后重新调用 ⇒ 支持"选了地区之后该地区内的角色不再可选"
        /// （JC021 的"禁止原地移动"），并把已失效的旧选择自动剔除。
        /// 返回 false ＝ 选目标窗不可用（编辑器未挂场景）→ 调用方须回退到自己的列表窗。
        /// </summary>
        public bool RequestFieldMultiPick(string hint,
            System.Func<FieldPickState, FieldCandidates> candidatesOf,
            int maxCards, int maxAreas,
            System.Func<FieldPickState, string> titleOf,
            System.Func<FieldPickState, bool> canConfirm,
            System.Func<FieldPickState, string, bool> canAddCard,
            System.Action<List<string>, List<string>> onConfirmed,
            System.Action onCancelled,
            bool allowCancel = true, string confirmLabel = null, string windowTitle = null)
        {
            if (_targetSelectionWindow == null)
            {
                GD.PrintErr("[TriggerCoordinator] ⚠ 选目标窗不可用 → 无法开多选窗（调用方应回退列表窗）");
                return false;
            }
            // 【2026-09 修复·用户实测"多弹了一个选择框"】已有一次选择窗在等玩家 ⇒ 忽略重复请求。
            if (_targetSelectionActive)
            {
                GD.Print("[TriggerCoordinator] ⛔ 已有选目标/多选窗在等玩家 → 忽略重复的多选请求");
                return false;
            }
            _targetSelectionActive = true;
            _multiPickActive = true;
            _multiCandidatesOf = candidatesOf;
            _multiMaxCards = System.Math.Max(0, maxCards);
            _multiMaxAreas = System.Math.Max(0, maxAreas);
            _multiTitleOf = titleOf;
            _multiCanConfirm = canConfirm;
            _multiCanAddCard = canAddCard;
            _multiHint = string.IsNullOrEmpty(hint) ? "左键选择右键取消" : hint;
            _multiPick.Cards.Clear();
            _multiPick.Areas.Clear();
            _onMultiConfirmed = onConfirmed;
            _onMultiCancelled = onCancelled;

            _targetSelectionWindow.OnTargetPicked = null;   // 多选模式：点击走 HandleTargetSelectionClick
            _targetSelectionWindow.OnExtra = null;
            // 【2026-10-20·强制多选】`allowCancel:false`（手牌上限弃牌）⇒ 不给取消回调：
            // 既没有取消按钮，标题栏 X 也被 `TargetSelectionWindow` 忽略（`_closable=false`）。
            _targetSelectionWindow.OnCancelled = allowCancel
                ? (System.Action)(() =>
                {
                    GD.Print("[TriggerCoordinator] 多选取消（不发动）");
                    var cb = _onMultiCancelled;
                    CleanupTargetSelection();
                    cb?.Invoke();
                })
                : null;
            _targetSelectionWindow.OnMultiConfirmed = () =>
            {
                if (_multiCanConfirm != null && !_multiCanConfirm(_multiPick))
                {
                    GD.Print("[TriggerCoordinator] 多选：当前不满足确认条件 → 忽略确认键");
                    return;
                }
                var cards = new List<string>(_multiPick.Cards);
                var areas = new List<string>(_multiPick.Areas);
                var cb = _onMultiConfirmed;
                GD.Print($"[TriggerCoordinator] 多选确认：卡 {cards.Count} 张、地区 {areas.Count} 个");
                CleanupTargetSelection();
                cb?.Invoke(cards, areas);
            };
            _targetSelectionWindow.SetBlocking(false);
            _targetSelectionWindow.OpenMultiPick(_multiHint, cancelLabel: null, showCancel: allowCancel,
                confirmLabel: confirmLabel, windowTitle: windowTitle);
            _triggerOrderWindow?.SetButtonsEnabled(false);
            _lockTargetSelectionModal?.Invoke();
            RefreshMultiPick();   // 首次渲染：标题/提示/可选高亮/确认可用性
            return true;
        }

        /// <summary>多选：按当前状态重算候选与高亮（失效的旧选择会被剔除），并刷新窗面与"确认"可用性。</summary>
        private void RefreshMultiPick()
        {
            var cand = _multiCandidatesOf?.Invoke(_multiPick) ?? new FieldCandidates();
            // 选择已不再合法（如"选了地区之后，该地区内的角色不能再被移动"）→ 自动剔除
            _multiPick.Cards.RemoveAll(u => !cand.Cards.Contains(u));
            _multiPick.Areas.RemoveAll(u => !cand.Areas.Contains(u));

            _validTargetUids = new HashSet<string>(cand.Cards);
            _validAreaUids = new HashSet<string>(cand.Areas);

            // 【2026-09 用户口径】高亮＝**可点候选 ∪ 已选中**：已选中的卡/地区要**继续高亮**，
            // 否则选中后它不再是候选、高亮被撤掉，玩家会以为"没有高亮 / 选择丢了"。
            var highlightCards = new List<string>(_validTargetUids);
            foreach (var u in _multiPick.Cards) if (!highlightCards.Contains(u)) highlightCards.Add(u);
            CardManager.Instance.NotifyTargetSelectionChanged(null);
            if (highlightCards.Count > 0)
                CardManager.Instance.NotifyTargetSelectionChanged(highlightCards);

            foreach (var slot in _areaSlots)
            {
                bool candidate = _validAreaUids.Contains(slot.AreaUID);
                bool selected = _multiPick.Areas.Contains(slot.AreaUID);
                slot.IsTargetSelectable = candidate;
                slot.SetHighlight(candidate || selected);   // 已选中的地区保持"亮着"（不再是候选也不熄灭）
                slot.SetSelected(selected);                 // 另用绿色叠加强调"已选中"
                slot.OnAreaClicked = candidate ? OnAreaSlotPicked : null;
                // 右键取消：**候选与已选中**都要能取消（否则选中地区后再也点不掉 → 用户实测"无法右键取消地区选择"）
                slot.OnAreaRightClicked = (candidate || selected) ? OnAreaSlotRightClicked : null;
            }

            string title = _multiTitleOf?.Invoke(_multiPick) ?? "请选择目标";
            bool canOk = _multiCanConfirm == null || _multiCanConfirm(_multiPick);
            _targetSelectionWindow?.SetMultiPick(hint: _multiHint, title: title, confirmEnabled: canOk);
            GD.Print($"[TriggerCoordinator] 多选刷新：{title}｜卡 {_multiPick.Cards.Count}/{_multiMaxCards}、地区 {_multiPick.Areas.Count}/{_multiMaxAreas}，可选卡 {_validTargetUids.Count}、可选地区 {_validAreaUids.Count}，可确认={canOk}");
        }

        /// <summary>多选：左键点场上高亮卡＝**加选**（到上限后"第三个目标不能点击"）；右键＝取消选择。</summary>
        private void MultiPickCard(string uid, bool rightClick)
        {
            if (string.IsNullOrEmpty(uid)) return;
            if (rightClick)
            {
                if (_multiPick.Cards.Remove(uid)) RefreshMultiPick();
                return;
            }
            if (_multiPick.Cards.Contains(uid)) return;                      // 已选：左键不重复加（取消请用右键）
            if (_multiMaxCards <= 0 || _multiPick.Cards.Count >= _multiMaxCards)
            {
                GD.Print($"[TriggerCoordinator] 多选卡已满（{_multiMaxCards} 张）→ 忽略左键点击 {uid}");
                return;
            }
            if (_multiCanAddCard != null && !_multiCanAddCard(_multiPick, uid))
            {
                GD.Print($"[TriggerCoordinator] 多选：该卡不满足附加条件（如『每个玩家场上的角色只能选一个』）→ 忽略 {uid}");
                return;
            }
            _multiPick.Cards.Add(uid);
            RefreshMultiPick();
        }

        /// <summary>多选：左键点高亮地区＝**选择/替换**（至多 `_multiMaxAreas` 个）；右键＝取消选择。</summary>
        private void MultiPickArea(string areaUid, bool rightClick)
        {
            if (string.IsNullOrEmpty(areaUid)) return;
            if (rightClick)
            {
                if (_multiPick.Areas.Remove(areaUid)) RefreshMultiPick();
                return;
            }
            if (_multiPick.Areas.Contains(areaUid)) return;
            if (_multiMaxAreas <= 0) return;
            if (_multiPick.Areas.Count >= _multiMaxAreas) _multiPick.Areas.Clear();   // 上限即"一个地区"⇒ 直接换选
            _multiPick.Areas.Add(areaUid);
            RefreshMultiPick();
        }

        /// <summary>选目标收尾（单选/多选共用）：解锁 + 清高亮 + 关窗 + 恢复排序窗按钮。</summary>
        private void CleanupTargetSelection()
        {
            _unlockTargetSelectionModal?.Invoke();
            _targetSelectionActive = false;
            _multiPickActive = false;
            _validTargetUids.Clear();
            _validAreaUids.Clear();
            _onTargetPicked = null;
            _onMultiConfirmed = null;
            _onMultiCancelled = null;
            _multiCandidatesOf = null;
            _multiCanConfirm = null;
            _multiCanAddCard = null;
            _multiTitleOf = null;
            _multiPick.Cards.Clear();
            _multiPick.Areas.Clear();
            CardManager.Instance.NotifyTargetSelectionChanged(null);
            foreach (var slot in _areaSlots)
            {
                slot.SetHighlight(false);
                slot.SetSelected(false);
                slot.IsTargetSelectable = false;
                slot.OnAreaClicked = null;
                slot.OnAreaRightClicked = null;
            }
            if (_targetSelectionWindow != null)
            {
                _targetSelectionWindow.Visible = false;
                _targetSelectionWindow.OnExtra = null;
                _targetSelectionWindow.OnCancelled = null;
                _targetSelectionWindow.OnMultiConfirmed = null;
            }
            _triggerOrderWindow?.SetButtonsEnabled(true);
        }

        // ---- 多选模式状态（JC062/JC021）----
        private bool _multiPickActive;
        private int _multiMaxCards = 2;
        private int _multiMaxAreas;
        private string _multiHint = "左键选择右键取消";
        private System.Func<FieldPickState, FieldCandidates> _multiCandidatesOf;
        private System.Func<FieldPickState, string> _multiTitleOf;
        private System.Func<FieldPickState, bool> _multiCanConfirm;
        private System.Func<FieldPickState, string, bool> _multiCanAddCard;
        private System.Action<List<string>, List<string>> _onMultiConfirmed;
        private Action _onMultiCancelled;
        private readonly FieldPickState _multiPick = new FieldPickState();

        // ============================================================
        // 选目标状态机（原 Arena 私有字段与方法，迁移至此）
        // ============================================================

        private bool _targetSelectionActive;
        /// <summary>选目标交互是否进行中（供 Arena 输入路由判断是否拦截点击）。</summary>
        public bool IsTargetSelectionActive => _targetSelectionActive;

        /// <summary>
        /// 【2026-10-20 修复·恢复步骤手牌上限弃牌】该坐标是否命中「本次候选」里的某张卡（含手牌）。
        ///
        /// 为什么需要这个探测：`Arena._Input` 里"手牌拖拽起手"分支排在"选目标点击转发"之前，
        /// 弃牌提示窗打开时点手牌会被当成**起手拖牌/激活拖拽**整条吞掉（`AcceptEvent` + return），
        /// 协调器的 `MultiPickCard` 永远收不到 ⇒ 玩家看到的是"点手牌没选中，提示窗反而被搞没了"。
        /// 故在拖拽分支之前先用本探测判断"这一下该交给选目标流程"，命中才拦截。
        /// </summary>
        public bool HitsCandidateAt(Vector2 globalPos)
        {
            if (!_targetSelectionActive) return false;
            var uids = CurrentPickUids();
            if (uids == null || uids.Count == 0) return false;
            return !string.IsNullOrEmpty(_pickCandidateCardUidAt?.Invoke(globalPos, uids));
        }
        private bool _targetIsArea;          // true=目标为地区槽位（机动）；false=目标为场上卡
        private HashSet<string> _validTargetUids = new HashSet<string>();
        private HashSet<string> _validAreaUids = new HashSet<string>();
        private Action<string> _onTargetPicked;

        /// <summary>【2026-10·JC108】"本次取目标"的**取消出口**（仅当调用方给了出口时非空）。
        /// 用途：卡目标不弹提示窗时，窗口上的"取消"按钮看不到 → 由**场上右键**触发同一个出口回调
        /// （用户口径：手牌行动等取目标要能取消）。由 `RequestActivateTargetSelection` 在其局部
        /// `FinishSelection` 就绪后赋值，收尾时清空（幂等由 `_targetSelectionActive` 保证）。</summary>
        private Action _cancelActivateTargetSelection;

        /// <summary>打开选目标提示窗口。effectName/hint 用于展示；选择由 Arena 转发场上卡点击。
        /// 【2026-09·K15】`extraLabel` 非空时，窗口自带的备用按钮成为该槽的出口（留空该槽 / 取消不打出）。
        /// 【2026-10·触发窗口改造】`allowCancel=true` 时窗口额外显示「取消」按钮（回调 `OnCancelled`），
        /// 供"触发入堆叠取目标"使用；默认 false ＝ 既有激活等路径行为不变。</summary>
        public void OpenTargetSelection(string effectName, string hint, string extraLabel = null,
            bool allowCancel = false, string cancelLabel = null)
        {
            if (_targetSelectionWindow == null) return;
            // 选目标需点击穿透到下层（场上卡 / 高亮地区槽位），故关闭全屏遮罩的点击拦截
            _targetSelectionWindow.SetBlocking(false);
            _targetSelectionWindow.Open(effectName, hint, extraLabel, allowCancel, cancelLabel);
        }

        /// <summary>AreaSlot._GuiInput 直接回传的点击（机动取地区目标 / 多选地区）。仅当 UID 合法时生效。</summary>
        private void OnAreaSlotPicked(string areaUid)
        {
            if (!_targetSelectionActive) return;
            if (!_validAreaUids.Contains(areaUid))
            {
                GD.Print($"[TriggerCoordinator] OnAreaSlotPicked 忽略非法地区 UID={areaUid}（合法集={string.Join(",", _validAreaUids)}）");
                return;
            }
            // 【2026-09·JC021 多选】地区也走"左键选择/右键取消"
            if (_multiPickActive) { MultiPickArea(areaUid, rightClick: false); return; }
            GD.Print($"[TriggerCoordinator] OnAreaSlotPicked 命中合法地区 UID={areaUid}，转发选目标窗口");
            _targetSelectionWindow.PickFromArena(areaUid);
        }

        /// <summary>【2026-09·JC021 多选】地区槽位右键＝取消该地区的选择。</summary>
        private void OnAreaSlotRightClicked(string areaUid)
        {
            if (!_targetSelectionActive || !_multiPickActive) return;
            if (!_validAreaUids.Contains(areaUid)) return;
            MultiPickArea(areaUid, rightClick: true);
        }

        /// <summary>选目标窗口激活时，检测玩家点击是否为合法目标（场上卡或可选地区槽位）；是则选中并回调。
        /// 【2026-09·JC062/JC021 多选模式】`rightClick` 区分"左键选择 / 右键取消"。</summary>
        public void HandleTargetSelectionClick(Vector2 globalPos, bool rightClick = false)
        {
            // 【2026-10·JC108】**卡目标不弹提示窗**（激活取卡目标）时的取消手势：场上**右键**＝取消。
            // 位置放在多选分支之前、且仅当本次取目标确实有出口时才生效——多选模式自身用"右键取消单张"，
            // 不能被这里整体取消吞掉。
            if (rightClick && !_multiPickActive && !_targetIsArea && _cancelActivateTargetSelection != null)
            {
                GD.Print("[TriggerCoordinator] 场上右键 → 取目标取消出口（卡目标不弹窗时的取消手势）");
                _cancelActivateTargetSelection.Invoke();
                return;
            }

            // 【2026-09·多选模式】窗口保持打开，左键加选、右键取消
            if (_multiPickActive)
            {
                // **顺序关键：先判"点在卡上"，再判地区**——地区槽的矩形**包含**渲染在其上的卡，
                // 若先判地区，玩家"点角色选角色"会被误判成"选了地区"（用户实测 JC021：
                // "第一个选择了角色，会显示地区（1/1）识别错误"）。
                // 【选卡/找卡 UI 优化·批次 1】命中源换成"候选 ∩ 有场上 UI"的专用命中
                // （候选 ∪ 已选：已选中的卡要能被右键取消）。
                var multiUid = _pickCandidateCardUidAt?.Invoke(globalPos, CurrentPickUids());
                if (!string.IsNullOrEmpty(multiUid))
                {
                    MultiPickCard(multiUid, rightClick);
                    return;
                }
                foreach (var slot in _areaSlots)
                {
                    if (!slot.ContainsGlobalPoint(globalPos)) continue;
                    if (_validAreaUids.Contains(slot.AreaUID) || _multiPick.Areas.Contains(slot.AreaUID))
                    {
                        MultiPickArea(slot.AreaUID, rightClick);
                        return;
                    }
                }
                return;
            }

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

            // 默认：点击合法场上卡。
            // 【选卡/找卡 UI 优化·批次 1】命中改为"在**本次候选列表**里找有场上 UI 且被点中的卡"：
            // 候选里新增了"封印于地区 / 结附于地区的附属 / 自己手牌 / 秘社"这些此前**点不动**的形态，
            // 命中范围与高亮范围同源（都＝候选 ∩ 有场上 UI）⇒ 不会"高亮了却点不到"。
            var hitUid = _pickCandidateCardUidAt?.Invoke(globalPos, CurrentPickUids());
            if (string.IsNullOrEmpty(hitUid)) return;
            if (!_validTargetUids.Contains(hitUid)) return;

            // 【批次2·4.5】选择窗模式：点场上高亮的卡 ＝ **把窗内对应条目置为选中**（不提交、不关窗，
            // 仍须在选择窗点「确认」）。提示窗模式仍是"点中即提交"。
            if (_pickWindowMode)
            {
                if (_activePickWindow != null && GodotObject.IsInstanceValid(_activePickWindow))
                    _activePickWindow.SelectByUid(hitUid);
                return;
            }
            _targetSelectionWindow.PickFromArena(hitUid);
        }

        /// <summary>
        /// 【选卡/找卡 UI 优化·批次 1】**本次可点候选 UID 列表**：
        /// 单选＝合法目标集合；多选＝合法目标 **∪** 已选中（已选中的卡也要能被点/右键取消，
        /// 否则"选中后就不是候选"会让它再也点不到）。
        /// </summary>
        private List<string> CurrentPickUids()
        {
            var list = new List<string>(_validTargetUids);
            if (_multiPickActive)
                foreach (var u in _multiPick.Cards)
                    if (!list.Contains(u)) list.Add(u);
            return list;
        }
    }
}

namespace Script.UI.Interaction
{
    /// <summary>
    /// 【2026-09 用户口径 JC062/JC021】"场上高亮多选取目标"的**当前选择状态**：
    /// `Cards` ＝ 已选场上卡（有序）、`Areas` ＝ 已选地区。
    /// 由 `TriggerInteractionCoordinator` 持有，并在每次选择变化后交给调用方回调
    /// （`titleOf` 拼"请选择…（k/N）"、`canConfirm` 决定"确认"是否可点、`canAddCard` 施加附加限制）。
    /// </summary>
    public sealed class FieldPickState
    {
        /// <summary>已选场上卡 UID（有序）。</summary>
        public readonly System.Collections.Generic.List<string> Cards
            = new System.Collections.Generic.List<string>();
        /// <summary>已选地区 UID（JC021 用）。</summary>
        public readonly System.Collections.Generic.List<string> Areas
            = new System.Collections.Generic.List<string>();
    }

    /// <summary>
    /// 【2026-09】某次选择状态下的**可点候选**（每次变化后由调用方重算）：
    /// JC021 选了地区之后，该地区内的角色不能再被移动（禁止原地移动）⇒ 卡候选随之收缩，
    /// 协调器会把已经不合法的旧选择剔除。
    /// </summary>
    public sealed class FieldCandidates
    {
        public System.Collections.Generic.List<string> Cards = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<string> Areas = new System.Collections.Generic.List<string>();
    }
}
