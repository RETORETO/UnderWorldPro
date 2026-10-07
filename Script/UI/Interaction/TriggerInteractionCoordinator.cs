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
                // 记录本轮入栈的触发：Arena 处理完（含选目标）后回填"仍未处理"的触发总数，
                // 供「第一张进入堆叠」判断"排序窗口内已无剩余卡牌/效果需加入堆叠"→ 自动关窗。
                _pendingEnterStack = ordered;
                onEnterStack?.Invoke(ordered);
            };
            _triggerOrderWindow.OnDone = () =>
            {
                _unlockTriggerOrderModal?.Invoke();   // 本次排序结束，解冻出牌/Pass（窗口为复用单例，非 QueueFree）
                _triggerOrderWindow.Visible = false;
                onDone?.Invoke();
            };

            _lockTriggerOrderModal?.Invoke();
            // 先记录"本轮待处理触发总数"再开窗：Open 之后的兜底判定才不会被开窗瞬间的默认值误关。
            _pendingEnterStack = null;
            _triggerOrderWindow.SetRemainingTriggerCount(triggers?.Count ?? 0);
            _triggerOrderWindow.Open(ownerPlayerId, entries);
        }

        /// <summary>本轮「进入堆叠」的触发列表（OnEnterStack 记录）；用于把剩余数回填给排序窗口。</summary>
        private List<StackEffectInstance> _pendingEnterStack;

        /// <summary>触发实际入栈后由 TurnManager 调用：从 OrderZone 移除已入栈条目。
        /// 取目标触发在玩家选完目标后才触发本方法，故条目保留到选目标完成（修复"卡提前从 OrderZone 消失"）。
        /// 移除后回填"仍未处理"的触发总数：归零时若本次来自「第一张进入堆叠」，排序窗口自动关闭并推进。</summary>
        public void RemoveOrderEntries(List<StackEffectInstance> pushed)
        {
            _triggerOrderWindow?.RemoveEntries(pushed);
            // 剩余节点数＝本次点击时窗口内的条目（尚未从 OrderZone 移除）－本次已入栈数；
            // 与引擎侧"剩余触发"同解（取消选目标的触发也已从 OrderZone 移除）。
            // 注意：本计数**不含放弃框（DiscardZone）内的条目**——放弃框内容由窗口自身在自动关窗判定时检查
            // （TriggerOrderWindow.TryAutoCloseIfNothingRemaining 要求排序框与放弃框皆空才关窗）。
            int remainingUi = Math.Max(0, (_triggerOrderWindow?.GetOrderZoneChildren().Count ?? 0) - (pushed?.Count ?? 0));
            int pending = _pendingEnterStack?.Count ?? 0;
            _pendingEnterStack = null;
            _triggerOrderWindow?.UpdateRemainingTriggerCount(pending + remainingUi);
        }

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
        /// 【2026-09·K15 多目标场上点选】`extraLabel` + `onExtra` 给出该槽的**出口按钮**：
        ///  · 可留空的槽 → "不选（留空该槽）"；
        ///  · 必选槽 → "取消（不打出）"。
        /// 目的：多目标卡的"场上卡槽"也走**高亮 + 点击**（用户口径："选择查看的暗藏者应该选择场上的
        /// 高亮暗藏者，而不是弹出选择框"），同时保留退出通道，玩家不会卡在点不动的提示窗上。
        /// 返回 false ＝ **选目标窗不可用**（如编辑器未挂场景）→ 调用方必须回退到自己的列表窗，
        /// 否则该次取目标会静默中断（挂死在"等一个永不到来的回调"上）。
        /// </summary>
        public bool RequestActivateTargetSelection(string effectName, bool isArea, List<string> validTargetUids,
            Action<string> onPicked, string extraLabel = null, Action onExtra = null)
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
            OpenTargetSelection(effectName, hint, extraLabel);

            _triggerOrderWindow?.SetButtonsEnabled(false);

            // 选目标模态独占：加锁，禁止期间激活/出牌其他卡（原子锁，修复"选目标时还能激活"）
            _lockTargetSelectionModal?.Invoke();

            // 选中/出口共用同一份收尾（解锁 + 清高亮 + 关闭窗口 + 恢复排序窗按钮）
            void FinishSelection()
            {
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
                _targetSelectionWindow.OnExtra = null;
                _triggerOrderWindow?.SetButtonsEnabled(true);
            }

            _targetSelectionWindow.OnTargetPicked = (uid) =>
            {
                GD.Print($"[TriggerCoordinator] 激活选目标 OnTargetPicked UID={uid}");
                FinishSelection();
                _onTargetPicked?.Invoke(uid);
                _onTargetPicked = null;
            };
            // 【2026-09·K15】该槽的出口按钮（留空该槽 / 取消不打出）：只是"离开本槽"的通道，不选目标
            _targetSelectionWindow.OnExtra = () =>
            {
                GD.Print($"[TriggerCoordinator] 本槽出口（{extraLabel}）：{effectName}");
                FinishSelection();
                _onTargetPicked = null;
                onExtra?.Invoke();
            };

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
            System.Action onCancelled)
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
            _targetSelectionWindow.OnCancelled = () =>
            {
                GD.Print("[TriggerCoordinator] 多选取消（不发动）");
                var cb = _onMultiCancelled;
                CleanupTargetSelection();
                cb?.Invoke();
            };
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
            _targetSelectionWindow.OpenMultiPick(_multiHint);
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
        private bool _targetIsArea;          // true=目标为地区槽位（机动）；false=目标为场上卡
        private HashSet<string> _validTargetUids = new HashSet<string>();
        private HashSet<string> _validAreaUids = new HashSet<string>();
        private Action<string> _onTargetPicked;

        /// <summary>打开选目标提示窗口。effectName/hint 用于展示；选择由 Arena 转发场上卡点击。
        /// 【2026-09·K15】`extraLabel` 非空时，窗口自带的备用按钮成为该槽的出口（留空该槽 / 取消不打出）。</summary>
        public void OpenTargetSelection(string effectName, string hint, string extraLabel = null)
        {
            if (_targetSelectionWindow == null) return;
            // 选目标需点击穿透到下层（场上卡 / 高亮地区槽位），故关闭全屏遮罩的点击拦截
            _targetSelectionWindow.SetBlocking(false);
            _targetSelectionWindow.Open(effectName, hint, extraLabel);
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
            // 【2026-09·多选模式】窗口保持打开，左键加选、右键取消
            if (_multiPickActive)
            {
                // **顺序关键：先判"点在卡上"，再判地区**——地区槽的矩形**包含**渲染在其上的卡，
                // 若先判地区，玩家"点角色选角色"会被误判成"选了地区"（用户实测 JC021：
                // "第一个选择了角色，会显示地区（1/1）识别错误"）。
                var multiHit = _pickAreaCardAt?.Invoke(globalPos);
                if (multiHit?.Instance != null
                    && (_validTargetUids.Contains(multiHit.Instance.UID) || rightClick))
                {
                    MultiPickCard(multiHit.Instance.UID, rightClick);
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

            // 默认：点击合法场上卡
            var hit = _pickAreaCardAt?.Invoke(globalPos);
            if (hit == null || hit.Instance == null) return;
            string uid = hit.Instance.UID;
            if (_validTargetUids.Contains(uid))
                _targetSelectionWindow.PickFromArena(uid);
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
