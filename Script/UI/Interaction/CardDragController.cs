using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.UI;
using System.Collections.Generic;
using System.Linq;

namespace Script.UI.Interaction
{
    /// <summary>
    /// 【MVC 修复 M-08】手牌拖拽控制器。
    /// 原拖拽逻辑散落在 Arena（约 874–1102 行的 OnCardDragEnded / ReturnCardToHand / HandleMaskDrop，
    /// 以及 2146–2175 的 _Input 拖拽路由、2225–2303 的 PickHandCardAt / IsDraggableUnderMask /
    /// IsLegalCardTarget / PickAreaCardAt / UpdateDragTargetHighlights / ClearDragTargetHighlights），
    /// 使 Arena 同时承担"渲染编排根"与"拖拽控制器"双重职责（M-02/M-04 的根因之一）。
    ///
    /// 本类统一负责：
    /// - 全局输入中的手牌拖拽起手/放手路由；
    /// - 拖拽落点的出牌/蒙版行动判定（规则委托 Controller，仅做 UI 层几何与意图表达）；
    /// - 拖拽高亮与退回手牌的插入位置计算。
    ///
    /// 设计：通过 Arena 暴露的 internal 访问器读取手牌集合/地区槽位/蒙版状态等 UI 状态，
    /// 并持有 Controller 引用（PlayerActionService / CardManager / TurnManager）表达写意图。
    /// 所有规则判定（CanDeployCovert / IsLegalPlayTarget）仍上收 Controller，View 只调不问。
    /// </summary>
    public partial class CardDragController : Node
    {
        private Arena _arena;
        private PlayerActionService _actionService;
        private CardManager _cardManager;
        private TurnManager _turnManager;
        private ModalWindowCoordinator _modalCoordinator;

        private CardUI _draggingHandCard;

        /// <summary>
        /// 【2026-09 修复·用户实测"多弹了一个选择框"】已经处理过"拖拽结束"的那张卡 UI：
        /// `CardDragEnded` 可能被重复投递（多份订阅 / `CardUI._GuiInput` 与 Arena 全局输入两条路径），
        /// 于是同一次拖拽会走多遍完整流程、每遍各弹一个选择框（实测 JC062 两框、JC063 三框）。
        /// 本字段在 `TryBeginDragAt`（新拖拽开始）时清空，故"同一张卡再拖一次"不受影响。
        /// </summary>
        private CardUI _lastHandledDragEndUi;
        private CardUI _lastDragHoverTarget;

        // ===== D9 激活框架：场上卡激活拖拽状态 =====
        private CardUI _activatingCard;
        private StackArrow _activateArrow;
        private readonly List<CardUI> _activateHighlighted = new();
        /// <summary>【2026-10 用户口径·JC108】本次激活拖拽期间，鼠标**离开过来源卡**（＝确实拖动了）。
        /// 用途：来源卡自身也是合法目标（`Target.CanTargetSelf`，如 JC108"封印在目标角色上"含自身）时，
        /// "拖出去再松开在本卡身上"＝**指定自身为目标**；若从未离开来源卡（＝原地单击），
        /// 松手仍是"取消"（保持"单击场上卡不误发动效果"的既有行为）。</summary>
        private bool _activateLeftSource;
        public bool IsActivating => _activatingCard != null;

        public void Configure(Arena arena, PlayerActionService actionService, CardManager cardManager, TurnManager turnManager, ModalWindowCoordinator modalCoordinator)
        {
            _arena = arena;
            _actionService = actionService;
            _cardManager = cardManager;
            _turnManager = turnManager;
            _modalCoordinator = modalCoordinator;
        }

        // ============================================================
        // 全局输入路由：手牌拖拽起手 / 放手
        // ============================================================

        /// <summary>尝试在全局坐标点选命中的手牌卡并开始拖拽；返回是否成功起手。</summary>
        public bool TryBeginDragAt(Vector2 globalPos)
        {
            // 【选卡/找卡 UI 优化·批次 1】选目标期间**不允许起手拖拽**：
            // 选目标路由（`Arena._Input` 里排在后面的分支）会处理"点候选手牌＝选中它"，
            // 若此处先把左键吃掉，点自己的候选手牌会**先被拖拽吞掉**、永远选不中（用户实测口径）。
            if (_arena != null && _arena.IsTargetSelectionActive) return false;

            // 战斗对抗窗口打开期间不抢手牌拖拽（由 ModalWindowCoordinator 处理伤害分配点击）
            if (_draggingHandCard == null && (_modalCoordinator?.ActiveStrengthWindow == null))
            {
                CardUI hit = PickHandCardAt(globalPos);
                if (hit != null && hit.CanDrag)
                {
                    _draggingHandCard = hit;
                    _lastHandledDragEndUi = null;   // 【2026-09】新的拖拽开始 ⇒ 允许本次拖拽结束时被处理一次
                    hit.BeginDrag();
                    return true;
                }
            }
            return false;
        }

        /// <summary>结束当前拖拽（鼠标释放），交由 OnCardDragEnded 处理出牌/归位。</summary>
        public void EndDrag()
        {
            if (_draggingHandCard == null) return;
            var card = _draggingHandCard;
            _draggingHandCard = null;
            card.EndDrag();
        }

        public bool IsDragging => _draggingHandCard != null;
        /// <summary>是否处于"普通出牌拖拽中"（非蒙版），用于 Arena 实时高亮合法目标卡。</summary>
        public bool IsDraggingUnderNoneMask => IsDragging && _arena.ActiveMask == MaskMode.None;

        // ============================================================
        // 拖拽结束：出牌 / 蒙版行动 / 归位
        // ============================================================

        public void OnCardDragEnded(CardUI cardUI, bool dropped)
        {
            // ============================================================
            // 【2026-09 修复·用户实测"多弹出了一个选择框"（JC062 两个、JC063 三个）】
            // 同一次鼠标释放可能被**送达多次**（`CardUI.CardDragEnded` 信号 + Arena 全局输入路径等），
            // 于是本方法被重复调用。重复调用的**致命点**在原顺序：
            //   ① 第一次调用开了选择窗并把卡从手牌移出（进入选择流程）；
            //   ② 第二次调用先撞上"模态冻结"分支 → `ReturnCardToHand` ⇒ **把卡塞回手牌**；
            //   ③ 于是再来一次调用又能通过"在手牌"判定 → **再走一遍完整流程** ⇒ 每遍各弹一个选择框。
            // 修法：把"**卡已不在手牌 ⇒ 一律忽略**"提到最前（且**不再归位**），重复投递被就地吞掉。
            // 注意：这**不影响**正常的"模态打开期间禁止拖拽出牌"——那时卡仍在手牌，会照常走下面的冻结分支。
            // ============================================================
            bool stillInHand = cardUI != null
                               && (_arena.HandCardUIs.Contains(cardUI) || _arena.HandCardUIs2.Contains(cardUI));
            if (!stillInHand)
            {
                GD.Print($"[DragController.DragEnd] 卡已不在手牌（重复的拖拽结束投递）→ 忽略：" +
                         $"{cardUI?.Instance?.PlayerTemplate?.CardName}");
                return;
            }
            // 同一张卡 UI 的**同一次拖拽**只处理一次：`CardDragEnded` 可能被重复投递（多份订阅/全局输入路径），
            // 标记在 `TryBeginDragAt`（新的拖拽开始）时清除，故不影响"同一张卡再拖一次"。
            if (ReferenceEquals(_lastHandledDragEndUi, cardUI))
            {
                GD.Print($"[DragController.DragEnd] 同一次拖拽已处理过 → 忽略重复投递：" +
                         $"{cardUI.Instance?.PlayerTemplate?.CardName}");
                return;
            }
            _lastHandledDragEndUi = cardUI;

            // 模态窗口（调查/战斗/触发排序）打开期间冻结出牌，避免原子操作执行期间被打断（核心需求 7.3）
            if (_arena.IsDragInteractionLocked)
            {
                GD.Print($"[DragController.DragEnd] 模态窗口打开中，出牌被冻结，卡牌归位: {cardUI.Instance?.PlayerTemplate?.CardName}");
                ReturnCardToHand(cardUI);
                return;
            }

            var instance = cardUI.Instance;
            if (instance == null) return;

            // 注：原先这里还有一次"非手牌卡 → 忽略"的判定；现已提到方法最前（见上方"重复投递"注释），
            // 此处不再重复判定（避免两处守卫口径漂移）。
            bool inHand1 = _arena.HandCardUIs.Contains(cardUI);
            bool inHand2 = _arena.HandCardUIs2.Contains(cardUI);

            int playerId = inHand2 ? 2 : 1;
            List<CardUI> hand = playerId == 1 ? _arena.HandCardUIs : _arena.HandCardUIs2;

            // 蒙版激活时走专属行动分支（不走普通出牌 PlayCard）
            if (_arena.ActiveMask != MaskMode.None)
            {
                HandleMaskDrop(cardUI, playerId, hand);
                return;
            }

            Vector2 mousePos = _arena.GetGlobalMousePosition();

            // 取消判定：拖回自己手牌区 = 取消打出，归位手牌
            var handContainer = _arena.GetHandContainer(playerId);
            if (handContainer != null && handContainer.GetGlobalRect().HasPoint(mousePos))
            {
                GD.Print($"[DragController.DragEnd] 拖回手牌区，取消打出并归位: {instance.PlayerTemplate?.CardName}");
                ClearDragTargetHighlights();
                ReturnCardToHand(cardUI);
                return;
            }

            // 【2026-10 用户口径·JC110】手牌中的"**手牌行动**"卡（`Scope = ZoneScope.HandZone` 的激活能力，
            // 如 JC110 闲人的绿地"手牌行动4：将自身封印在目标角色上"）**拖到该能力的合法目标上＝发动该能力**
            // （付费 → 入堆叠 → 结算封印），与"双击 → 取目标"同一出口（`EffectEngine.ActivateAbility`）。
            // 位置：在普通落点判定**之前**尝试，但**只在真的发动了**才返回 true（`TryHandActivateDrop` 的约定）
            // —— 手牌行动卡**同时是可正常打出的角色卡**（JC110 自身 3 费），拖到己方争夺区的普通打出
            // 必须继续走下面的 `PlayCard` 流程（用户实测·JC110①："依然无法付费正面打出"就是这里
            // 被"落点非法→归位手牌"提前吞掉的）。
            if (TryHandActivateDrop(cardUI, playerId)) return;

            // 方案A：先命中检测场上卡（附属/事务可选目标）
            string targetCardUid = "";
            string targetAreaName = "";
            CardUI hitCard = PickAreaCardAt(mousePos);
            if (hitCard != null && IsLegalCardTarget(cardUI, hitCard))
            {
                targetCardUid = hitCard.Instance?.UID ?? "";
            }
            else
            {
                //【打出/效果目标分离】角色卡打出落点=己方争夺区（slot 中属于 playerId 的那块），
                // 与附属/事务卡「选结附/结算目标」是两套独立逻辑，不能共用 GetAllowedTargets 判定。
                bool isCharacter = cardUI.Instance?.PlayerTemplate is PlayerCardData pcd
                    && (pcd.CategoryFlag & CardCategory.Character) != 0;

                if (isCharacter)
                {
                    // 角色卡：仅拖到"己方争夺区"有效（playerId==1→Player1Area，playerId==2→Player2Area），
                    // 拖到敌方争夺区/空白处 targetAreaName 保持空，交由下方校验退回手牌。
                    foreach (var slot in _arena.AreaSlots)
                    {
                        var ownArea = playerId == 2 ? slot.Player2Area : slot.Player1Area;
                        if (ownArea != null && ownArea.GetGlobalRect().HasPoint(mousePos))
                        {
                            targetAreaName = slot.AreaUID;
                            break;
                        }
                    }
                }
                else
                {
                    // 附属/事务：命中结算，先判争夺区槽位（结附地区），再判秘社区（结附秘社）。
                    // 此前仅检测 AreaSlots，导致结附秘社卡放下时永远取不到 targetCardUid 而报"未检测到合法目标"。
                    // 地区落点仅当 dragging 卡允许结附地区（GetAllowedTargets 含 Area）时接受；否则不放 targetAreaName，
                    // 避免"只能结附角色的附属"（如 JC020）被拖到地区而错误结附地区（D48）。
                    bool allowsArea = _cardManager != null
                        && cardUI.Instance?.PlayerTemplate is PlayerCardData pcd2
                        && pcd2.GetAllowedTargets().Contains(TargetKind.Area);
                    foreach (var slot in _arena.AreaSlots)
                    {
                        var container = slot.GetTargetContainer(mousePos);
                        if (container != null)
                        {
                            if (allowsArea)
                            {
                                targetAreaName = slot.AreaUID;
                            }
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(targetAreaName))
                    {
                        var councilContainer = _arena.GetCouncilContainer(playerId);
                        if (councilContainer != null && councilContainer.GetGlobalRect().HasPoint(mousePos))
                        {
                            var councilInst = _arena.GetCouncilInstance(playerId);
                            if (councilInst != null && _cardManager != null
                                && _cardManager.IsLegalPlayTarget(cardUI.Instance, councilInst))
                            {
                                targetCardUid = councilInst.UID;
                                GD.Print($"[DragController.DragEnd] 命中玩家{playerId}秘社区，秘社实例={councilInst.CouncilTemplate?.CardName} 作为结附目标");
                            }
                            else
                            {
                                GD.Print($"[DragController.DragEnd] 命中秘社区但非合法目标（IsLegalPlayTarget=false 或无秘社实例）");
                            }
                        }
                    }
                }
            }

            bool requiresTarget = instance.PlayerTemplate?.GetRequiresTarget() ?? false;
            bool isCharacterCard = instance.PlayerTemplate is PlayerCardData pcdC
                && (pcdC.CategoryFlag & CardCategory.Character) != 0;
            bool isSorceryCard = instance.PlayerTemplate is PlayerCardData pcdS
                && (pcdS.CategoryFlag & CardCategory.Sorcery) != 0;

            // 角色卡：必须落在己方争夺区，否则退回手牌（拖到敌方争夺区/空白处均为非法落点）
            if (isCharacterCard && string.IsNullOrEmpty(targetAreaName))
            {
                GD.Print("❌ 角色卡需落在己方争夺区，退回手牌");
                ClearDragTargetHighlights();
                ReturnCardToHand(cardUI);
                return;
            }

            // 事务卡（需选目标）：不再要求精确落在目标上，改为拖出后弹窗选目标（复用 JC012 激活弹窗模式）。
            // 若仍拖到了合法目标卡上（targetCardUid 非空），则沿用原有"落点即目标"快速路径。
            //
            // 【2026-09 修复·用户实测 JC063】**"模式分支"与"多目标槽位"的事务卡必须走专用选择流程，
            // 不能在落在目标上时走"落点即目标"快速路径**——那条路径直接 `PlayCard`（不带 modeIndex /
            // 逐槽选择），于是：
            //  · JC063「选择一项」：落点即目标 ⇒ `PlayCard(modeIndex=-1)` 被拒（"需要先选择一项模式"），
            //    或即使能打出也不是按所选模式的目标类别（用户口径："选择一项以后才需要选择目标，
            //    其中一个只能以角色为目标（潜伏）、另一个只能以暗藏者为目标（移回手牌）"）；
            //  · JC021/JC092/JC133 等 `TargetSlots` 卡：同样被拒（"需要多目标选择（未提供逐槽选择）"）。
            // 故对这两类卡，无论落点是什么，一律交给 `BeginSorceryTargetSelection`（它内部按
            // 模式 → 该模式目标 / 逐槽取目标 → 确认面板 的正确顺序处理）。
            bool needsSelectionFlow = isSorceryCard
                && (Script.Manager.MultiTargetSlots.FindModeEffect(
                        instance.PlayerTemplate, Script.Data.Effects.EffectTiming.OnSorceryCast) != null
                    || Script.Manager.MultiTargetSlots.FindMultiTargetEffect(
                        instance.PlayerTemplate, Script.Data.Effects.EffectTiming.OnSorceryCast) != null);
            if (needsSelectionFlow
                || (isSorceryCard && requiresTarget
                    && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaName)))
            {
                // 【诊断】把"为什么走专用流程"留在日志里：模式卡/多目标卡必须**先选模式/逐槽**再取目标，
                // 不允许"落点即目标"（否则会被规则层拒：需要先选择一项模式 / 需要多目标选择）。
                if (needsSelectionFlow)
                    GD.Print($"[DragController.DragEnd] 『{instance.PlayerTemplate?.CardName}』声明了模式/多目标槽位 " +
                             $"⇒ 一律走专用选择流程（先选模式/逐槽，再取目标）；本次落点 targetCard={targetCardUid} targetArea={targetAreaName}");
                ClearDragTargetHighlights();
                _arena.BeginSorceryTargetSelection(instance, cardUI, playerId, hand);
                return;
            }

            // 【JC082·用户口径 2026-10-16 修正】"选择一项"的**附属卡**（`PlayerCardData.PlayChoice`，如 JC082 索亚之书）：
            //  · **未落在任何目标上**（拖出后松在空白/非目标处）⇒ 走"选择一项"窗口（与 JC063 同款，最后所选的项决定结附目标）；
            //  · **已落在合法目标上**（结附于角色＝借阅 / 结附于本方秘社＝收藏）⇒ **不拦截**，继续走下面的正常落点流程：
            //    `PlayCard(..., targetCardUid)` 会按目标类别**反推所选项**（`TurnManager.PlayCard` 的 JC082 分支），
            //    从而"拖到秘社（或角色）上直接付费打出并结附"。
            // 用户口径："**打出必须拖附属卡到秘社上**"。此前这里**无条件拦截**（连拖到秘社上也弹选择窗）⇒ 正是
            // 用户实测"附属卡直接打出并结附于秘社"的来源；现按**联机孪生入口**补齐同一守卫
            // （`ArenaOl`：`tmpl.PlayChoice && target 为空` 才上行裸意图，否则带目标上行、由服务端反推所选项）。
            if (instance.PlayerTemplate is PlayerCardData pcdChoice && pcdChoice.PlayChoice
                && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaName))
            {
                GD.Print($"[DragController.DragEnd] {pcdChoice.CardName} 声明了『选择一项』（PlayChoice）且未落点目标 ⇒ 走 JC063 同款选择流程");
                ClearDragTargetHighlights();
                _actionService.BeginModeCardPlay(playerId, instance.UID);
                return;
            }

            if (requiresTarget && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaName))
            {
                GD.Print("❌ 该卡需要目标，但未检测到合法目标（场上卡/地区）");
                ClearDragTargetHighlights();
                ReturnCardToHand(cardUI);
                return;
            }
            if (!requiresTarget && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaName))
            {
                GD.Print("[DragController.DragEnd] 无目标卡，直接进堆叠");
            }

            // 【K10·额外费用】该卡打出前需要**玩家做选择**（JC049「牺牲一个角色」）：
            // 先弹选择窗（取消＝不打出、不扣任何费用），选完由 Arena 带选择入堆叠并完成手牌/UI 整理；
            // 无合法牺牲对象 ⇒ 该卡**不能打出**（A21），Arena 会直接退回手牌。
            if (_actionService.NeedsExtraCostPlayerChoice(instance.PlayerTemplate))
            {
                ClearDragTargetHighlights();
                _arena.BeginPlayWithExtraCostChoice(instance, cardUI, playerId, hand, targetAreaName, targetCardUid);
                return;
            }

            // 【K10·额外费用】"展示任意数量手牌减费"（JC081）：手牌多选 → 确认/取消（取消＝不打出、不扣费）。
            if (_actionService.NeedsExtraCostRevealChoice(instance.PlayerTemplate))
            {
                ClearDragTargetHighlights();
                _arena.BeginPlayWithRevealExtraCostChoice(instance, cardUI, playerId, hand, targetAreaName, targetCardUid);
                return;
            }

            bool success = _actionService.PlayCard(playerId, instance.UID, targetAreaName, targetCardUid);
            ClearDragTargetHighlights();

            if (!success)
            {
                ReturnCardToHand(cardUI);
                return;
            }

            hand.Remove(cardUI);
            if (_arena.AreaCardUIs.Contains(cardUI)) _arena.AreaCardUIs.Remove(cardUI);

            cardUI.CanDrag = false;   // 进入堆叠后不可拖拽
            _arena.ArrangeAllCardsInternal();
            _arena.UpdateTurnUIInternal();
        }

        // ============================================================
        // 退回手牌（拖拽取消 / 非法落点）
        // ============================================================

        private void ReturnCardToHand(CardUI cardUI)
        {
            if (_arena.AreaCardUIs.Contains(cardUI))
                _arena.AreaCardUIs.Remove(cardUI);
            var hand = _arena.HandCardUIs2.Contains(cardUI) ? _arena.HandCardUIs2 : _arena.HandCardUIs;

            float dragCenterX = cardUI.GetVisualGlobalRect().GetCenter().X;
            hand.Remove(cardUI);
            int insertAt = hand.Count;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i] != null && hand[i].GetVisualGlobalRect().GetCenter().X > dragCenterX)
                {
                    insertAt = i;
                    break;
                }
            }
            _arena.PendingHandInsertIndex = insertAt;   // 交给 SyncCardUIToZone 的 Hand 分支生效
            GD.Print($"[DragController.Return] 拖拽退回手牌，插入索引={insertAt} (放手X={dragCenterX:F0})");

            cardUI.CanDrag = true;
            var instance = cardUI.Instance;
            if (instance != null)
            {
                _actionService.ReturnCardToHand(1, instance.UID);
            }
            cardUI.SetFace(true);
            _arena.ArrangeAllCardsInternal();
        }

        // ============================================================
        // 蒙版拖放处理（派遣暗藏 / 建立资产）
        // ============================================================

        private void HandleMaskDrop(CardUI cardUI, int playerId, List<CardUI> hand)
        {
            var instance = cardUI.Instance;
            if (instance == null) return;

            Vector2 mousePos = _arena.GetGlobalMousePosition();

            Control hitAreaContainer = null;
            bool hitIsPlayer1Area = false;
            foreach (var slot in _arena.AreaSlots)
            {
                var p1 = slot.Player1Area;
                var p2 = slot.Player2Area;
                if (p1 != null && p1.GetGlobalRect().HasPoint(mousePos))
                {
                    hitAreaContainer = p1; hitIsPlayer1Area = true; break;
                }
                if (p2 != null && p2.GetGlobalRect().HasPoint(mousePos))
                {
                    hitAreaContainer = p2; hitIsPlayer1Area = false; break;
                }
            }

            if (_arena.ActiveMask == MaskMode.DeployCovert)
            {
                if (hitAreaContainer == null || !hitIsPlayer1Area)
                {
                    GD.Print("❌ 派遣暗藏：需拖到己方争夺区，退回手牌");
                    ReturnCardToHand(cardUI);
                    return;
                }
                string areaUid = (_arena.AreaSlots.FirstOrDefault(s => s.Player1Area == hitAreaContainer))?.AreaUID ?? "";

                if (_actionService.DeployCovert(playerId, instance.UID, areaUid))
                {
                    _arena.FindCardUIByUID(instance.UID)?.SetFace(false);
                    _arena.ExitMaskInternal();
                    _arena.UpdateTurnUIInternal();
                }
                else
                {
                    ReturnCardToHand(cardUI);
                }
            }
            else if (_arena.ActiveMask == MaskMode.EstablishAsset)
            {
                bool droppedOnAsset = _arena.AssetContainer1 != null && _arena.AssetContainer1.GetGlobalRect().HasPoint(mousePos);
                if (hitAreaContainer == null && !droppedOnAsset)
                {
                    GD.Print("❌ 建立资产：需拖到资产区或地区，退回手牌");
                    ReturnCardToHand(cardUI);
                    return;
                }

                if (_actionService.EstablishAsset(playerId, instance.UID))
                {
                    _arena.ExitMaskInternal();
                    _arena.UpdateTurnUIInternal();
                }
                else
                {
                    ReturnCardToHand(cardUI);
                }
            }
        }

        // ============================================================
        // 手牌命中 / 可拖拽判定 / 落点目标判定
        // ============================================================

        /// <summary>按全局坐标查找命中的手牌卡（玩家1/玩家2手牌均参与）。蒙版激活时按当前蒙版过滤可拖拽卡。</summary>
        public CardUI PickHandCardAt(Vector2 globalPos)
        {
            for (int i = _arena.HandCardUIs.Count - 1; i >= 0; i--)
            {
                var c = _arena.HandCardUIs[i];
                if (c != null && c.GetVisualGlobalRect().HasPoint(globalPos) && IsDraggableUnderMask(c)) return c;
            }
            for (int i = _arena.HandCardUIs2.Count - 1; i >= 0; i--)
            {
                var c = _arena.HandCardUIs2[i];
                if (c != null && c.GetVisualGlobalRect().HasPoint(globalPos) && IsDraggableUnderMask(c)) return c;
            }
            return null;
        }

        /// <summary>判断某张手牌在当前蒙版下是否允许被拖起。规则判定委托 Controller。</summary>
        private bool IsDraggableUnderMask(CardUI cardUI)
        {
            if (cardUI == null || !cardUI.CanDrag) return false;
            switch (_arena.ActiveMask)
            {
                case MaskMode.DeployCovert:
                    return _turnManager?.CanDeployCovert(cardUI.Instance) ?? false;
                case MaskMode.EstablishAsset:
                    return true;
                default:
                    return true;
            }
        }

        // ============================================================
        // 【2026-10·JC110】手牌行动：拖到合法目标上＝发动
        // ============================================================

        /// <summary>
        /// 【2026-10 用户口径·JC110】手牌中的"**手牌行动**"卡拖到**合法目标**上松手 ⇒ **发动该激活能力**
        /// （付费 → 入堆叠 → 结算；与"双击 → 取目标窗"同一条 Controller 出口 `ActivateAbility`）。
        /// 用户原文："无法通过拖拽正面付费打出"。
        ///
        /// 返回 true ＝ **本次拖拽真的按"发动"处理了**（已入堆叠），调用方应直接返回、不得再走打出流程；
        /// 返回 false ＝ 不是手牌行动卡 / 当前不可发动 / 落点不是该能力的合法目标
        /// ⇒ **交回普通打出流程**（关键：手牌行动卡往往**同时是可正常打出的牌**——JC110 自身就是 3 费角色卡，
        /// 拖到己方争夺区必须照常付费打出，不能被这里提前吞成"归位手牌"）。
        /// 规则判定（门禁 + 落点合法性）全部委托 `ActivateEligibility`（View 不写 Model、也不复刻规则）。
        /// </summary>
        private bool TryHandActivateDrop(CardUI cardUI, int playerId)
        {
            var instance = cardUI?.Instance;
            if (instance == null) return false;
            if (instance.CurrentZone != CardZone.Hand) return false;   // 只有手牌中的"手牌行动"走此路
            var ability = Script.Manager.ActivateEligibility.GetHandScopedActivateAbility(instance);
            if (ability == null) return false;

            var hit = PickAreaCardAt(_arena.GetGlobalMousePosition());
            if (!Script.Manager.ActivateEligibility.CanHandActivateOn(
                    instance, hit?.Instance, _turnManager, _arena.PlayerManagerRef, _cardManager))
            {
                // 落点不是该能力的合法目标 ⇒ **不发动、也不归位**，交回普通打出流程
                // （普通落点可能是己方争夺区＝正常付费打出；由下方既有分支决定打出/退回手牌）。
                GD.Print($"[DragController.DragEnd] 手牌行动落点不是激活目标（落点=" +
                         $"{hit?.Instance?.PlayerTemplate?.CardName ?? "空"}）→ 交回普通打出流程");
                return false;
            }

            GD.Print($"[DragController.DragEnd] 手牌行动拖拽发动：{instance.PlayerTemplate?.CardName} " +
                     $"→ 目标 {hit.Instance.PlayerTemplate?.CardName}（{ability.EffectId}，费用 {ability.Cost}）");
            ClearDragTargetHighlights();
            // 发动入口与双击链路同一出口（Controller 内部做权威复检/付费/入堆叠）
            _actionService.ActivateAbility(instance, ability, hit.Instance);
            _arena.RefreshActivateHighlights();
            return true;
        }

        /// <summary>判断 dragging 这张手牌是否能以 target 这张场上卡为合法目标（委托 CardManager 规则判定）。</summary>
        public bool IsLegalCardTarget(CardUI dragging, CardUI target)
        {
            if (dragging == null || target == null || dragging == target) return false;
            return _cardManager != null && _cardManager.IsLegalPlayTarget(dragging.Instance, target.Instance);
        }

        /// <summary>按全局坐标命中检测场上卡（争夺区角色 + 附属区附属 + **资产区资产**），返回最上层命中卡。
        /// 【2026-10 用户口径·JC107】资产卡（`AssetZone`）此前**不在命中范围内**：选目标时它被
        /// `_cardUIMap` 正常高亮，但点它取不到命中 ⇒ "高亮却选中无反馈"（用户实测）。
        /// 命中优先级保持"争夺区角色 → 附属 → 资产"，与既有落点语义一致（角色压附属、附属压资产区不影响布局）。</summary>
        public CardUI PickAreaCardAt(Vector2 globalPos)
        {
            for (int i = _arena.AreaCardUIs.Count - 1; i >= 0; i--)
            {
                var c = _arena.AreaCardUIs[i];
                if (c != null && c.GetVisualGlobalRect().HasPoint(globalPos)) return c;
            }
            // 扩展：命中附属卡（AttachedZone），使选目标/激活拖拽能点中附属
            foreach (var c in _arena.AttachedCardUIs)
            {
                if (c != null && c.GetVisualGlobalRect().HasPoint(globalPos)) return c;
            }
            // 扩展：命中资产卡（AssetZone），使"消灭目标附属或资产"类选目标能点中资产（JC107）
            foreach (var c in _arena.AssetCardUIs)
            {
                if (c != null && GodotObject.IsInstanceValid(c) && c.GetVisualGlobalRect().HasPoint(globalPos)) return c;
            }
            return null;
        }

        // ============================================================
        // 【选卡/找卡 UI 优化·批次 1】只服务"选目标"的候选命中
        // ------------------------------------------------------------
        // 与 `PickAreaCardAt` 的关键区别：后者是**拖拽落点/蒙版/战斗分配**的命中语义（只扫
        // 争夺区角色 + 附属 + 资产），**不得**扩展（会顺带改变拖拽行为）。
        // 本方法只做一件事：**在本次候选列表里找"此刻真的有场上 UI 且被点中"的那张卡**。
        //   · 命中范围 ＝ 候选列表 ∩ 有场上 UI（与高亮范围同源 ⇒ 不存在"高亮却点不到"）；
        //   · 用**既有**的 `Arena.FindCardUIByUID`（已覆盖手牌/资产/计分/争夺区/地区槽含附属封印/
        //     秘社容器含结附秘社的附属/牌库/墓地）与 `FindCouncilUIByUID`（秘社卡本身）取 UI，
        //     **不需要新增任何 UI 集合**；
        //   · 取 `GetVisualGlobalRect()`（旋转后的 AABB）判定，矩形随卡尺寸/上浮自动跟随。
        // ============================================================

        // 候选重叠时的取胜优先级（"视觉在上"取胜）：自己手牌 → 附属/封印 → 争夺区角色 → 资产 → 秘社
        private const int RANK_HAND = 0;
        private const int RANK_ATTACH_SEAL = 1;
        private const int RANK_AREA_CARD = 2;
        private const int RANK_ASSET = 3;
        private const int RANK_COUNCIL = 4;
        private const int RANK_OTHER = 5;

        private static int CandidateRank(CardInstance inst)
        {
            if (inst == null) return RANK_OTHER;
            switch (inst.CurrentZone)
            {
                case CardZone.Hand: return RANK_HAND;
                case CardZone.AttachedZone:
                case CardZone.SealedZone: return RANK_ATTACH_SEAL;
                case CardZone.AreaZone: return RANK_AREA_CARD;
                case CardZone.AssetZone: return RANK_ASSET;
                case CardZone.CouncilZone: return RANK_COUNCIL;
                default: return RANK_OTHER;
            }
        }

        /// <summary>
        /// 在**候选列表**里按全局坐标命中一张卡，返回其 UID；都不命中返回 null。
        /// 【选卡/找卡 UI 优化·批次 1】只服务选目标，**不改** `PickAreaCardAt`。
        /// </summary>
        public string PickCandidateCardUidAt(Vector2 globalPos, IReadOnlyList<string> candidateUids)
        {
            if (_arena == null || candidateUids == null || candidateUids.Count == 0) return null;
            string best = null;
            int bestRank = int.MaxValue;
            for (int i = 0; i < candidateUids.Count; i++)
            {
                string uid = candidateUids[i];
                if (string.IsNullOrEmpty(uid)) continue;
                if (!CandidateUIHasPoint(uid, globalPos, out int rank)) continue;
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = uid;
                }
            }
            return best;
        }

        /// <summary>该 UID 此刻是否有可见场上 UI 命中该点；命中时经 <paramref name="rank"/> 回传优先级。</summary>
        private bool CandidateUIHasPoint(string uid, Vector2 pos, out int rank)
        {
            rank = int.MaxValue;
            var ui = _arena.FindCardUIByUID(uid);
            if (ui != null && GodotObject.IsInstanceValid(ui) && ui.Visible
                && ui.GetVisualGlobalRect().HasPoint(pos))
            {
                rank = CandidateRank(ui.Instance);
                return true;
            }
            var council = _arena.FindCouncilUIByUID(uid);
            if (council != null && GodotObject.IsInstanceValid(council) && council.Visible
                && council.GetVisualGlobalRect().HasPoint(pos))
            {
                rank = RANK_COUNCIL;
                return true;
            }
            return false;
        }

        /// <summary>拖拽中：对合法目标卡做半透明高亮（使用当前拖拽卡）。</summary>
        public void UpdateDragTargetHighlights()
        {
            if (_draggingHandCard == null) return;
            var dragging = _draggingHandCard;
            var mp = _arena.GetGlobalMousePosition();
            CardUI hovered = PickAreaCardAt(mp);
            // 【2026-10·JC110】手牌中的"手牌行动"卡：拖拽期间的**合法落点**是"该激活能力的目标"
            // （不是打出目标）⇒ 高亮按激活目标算，否则玩家看不到"可以放在哪个角色上"。
            bool handActivate = Script.Manager.ActivateEligibility.GetHandScopedActivateAbility(dragging.Instance) != null;
            bool hoveredLegal = handActivate
                ? Script.Manager.ActivateEligibility.CanHandActivateOn(
                    dragging.Instance, hovered?.Instance, _turnManager, _arena.PlayerManagerRef, _cardManager)
                : IsLegalCardTarget(dragging, hovered);
            foreach (var c in _arena.AreaCardUIs)
            {
                if (c == null) continue;
                bool legal = handActivate
                    ? Script.Manager.ActivateEligibility.CanHandActivateOn(
                        dragging.Instance, c.Instance, _turnManager, _arena.PlayerManagerRef, _cardManager)
                    : IsLegalCardTarget(dragging, c);
                c.SetTargetHighlight(legal);
            }
            // 【2026-10·JC107】资产卡同为合法落点（"消灭目标附属或资产"）⇒ 拖拽期间一并高亮，
            // 否则玩家看不到"资产卡可以放"；命中范围与高亮范围保持一致（同一份 IsLegalCardTarget）。
            foreach (var c in _arena.AssetCardUIs)
            {
                if (c == null || !GodotObject.IsInstanceValid(c)) continue;
                c.SetTargetHighlight(IsLegalCardTarget(dragging, c));
            }
            _lastDragHoverTarget = hoveredLegal ? hovered : null;

            // 结附秘社卡：拖拽中高亮本方秘社（若其为合法目标）。
            // 秘社 UI 不在 AreaCardUIs 中，需单独处理。
            int dragPlayer = _arena.HandCardUIs2.Contains(dragging) ? 2 : 1;
            var councilInst = _arena.GetCouncilInstance(dragPlayer);
            bool councilLegal = councilInst != null && _cardManager != null
                && _cardManager.IsLegalPlayTarget(dragging.Instance, councilInst);
            _arena.SetCouncilHighlight(dragPlayer, councilLegal);
        }

        /// <summary>清除所有拖拽高亮。</summary>
        public void ClearDragTargetHighlights()
        {
            foreach (var c in _arena.AreaCardUIs)
                c?.SetTargetHighlight(false);
            foreach (var c in _arena.AssetCardUIs)
                if (c != null && GodotObject.IsInstanceValid(c)) c.SetTargetHighlight(false);
            _arena.ClearCouncilHighlights();
            _lastDragHoverTarget = null;
        }

        // ============================================================
        // D9 激活框架：场上卡激活拖拽（复用出牌拖拽手势）
        // ============================================================

        /// <summary>尝试在全局坐标点选命中“可发动”的场上卡并开始激活拖拽；返回是否成功起手。</summary>
        public bool TryBeginActivateDragAt(Vector2 globalPos)
        {
        if (IsActivating) return false;
        // 规则层原子锁（核心需求 7.3 / D26）：原子操作（触发排序/选目标窗口等）进行中冻结响应权，
        // 即便 UI 高亮缓存残留可发动状态，起手亦须就地复核原子锁，杜绝在窗口内发动 JC003 类快速能力。
        if (_turnManager != null && _turnManager.IsAtomicLocked) return false;
        // 仅当存在可发动卡时，才允许激活拖拽起手（避免与出牌拖拽争夺左键）
        CardUI hit = null;
            for (int i = _arena.AreaCardUIs.Count - 1; i >= 0; i--)
            {
                var c = _arena.AreaCardUIs[i];
                if (c != null && c.CanActivate && c.GetVisualGlobalRect().HasPoint(globalPos))
                {
                    hit = c; break;
                }
            }
            // 结附秘社/角色/地区的附属卡在 AttachedZone，不在 AreaCardUIs，需单独命中检测。
            if (hit == null)
            {
                foreach (var c in _arena.AttachedCardUIs)
                {
                    if (c != null && c.CanActivate && c.GetVisualGlobalRect().HasPoint(globalPos))
                    {
                        hit = c; break;
                    }
                }
            }
            if (hit == null) return false;

            // 附属卡不再走单击拖拽弹窗：激活统一改为双击触发（与秘社双击一致，JC012 交互修正）。
            // 单击命中附属卡走此分支直接返回（不拖拽、不弹窗），避免单击即弹窗。
            if (hit.Instance.IsAttached)
                return false;

            // 【2026-09·用户实测修复】拖拽**只能表达**"恰好 1 条能力 ＋ 需要选场上卡目标"的激活：
            //  · 多条能力（JC024 自带 + JC019 授予 = 2 条）⇒ 必须走**双击效果菜单**（弹选择窗）；
            //  · 无目标/自指能力（JC024"快速行动2：将自身潜伏"）⇒ 必须**双击直接发动**；
            //  · 玩家目标（JC111）/地区目标（JC117）⇒ 拖到卡上无法表达。
            // 以上情况一律 `return false`：**不吞左键**。此前无条件起手 ⇒ `Arena._Input` 会 `AcceptEvent()`，
            // 双击信号根本到不了 CardUI（用户实测："JC024 UI 上依然是拖拽 UI，无法正常使用效果"、
            // "结附给 JC024 后有两个激活效果，但双击没弹出选择效果界面（双击被拖拽覆盖）"）。
            if (!ActivateEligibility.CanDragActivate(hit.Instance, out _)) return false;
            // 没有可拖的合法目标（如只有自身可选的 A62 场景）也不吞左键，交给双击路径。
            if (!_arena.HasCachedActivateTarget(hit)) return false;

            // 角色卡（非附属）：沿用拖拽手势激活，绘制箭头并高亮合法目标。
            _activatingCard = hit;
            _activateLeftSource = false;   // 【JC108】新一次拖拽：重置"是否离开过来源卡"
            var arrow = _arena.StackArrowScene?.Instantiate<StackArrow>();
            if (arrow != null)
            {
                arrow.ZIndex = Layers.Arrows;
                arrow.ZAsRelative = false;
                _arena.ArrowLayer?.AddChild(arrow);
                _activateArrow = arrow;
            }
            // 初始高亮合法目标（拖拽中 UpdateActivateDrag 会增量刷新）
            foreach (var c in _arena.AreaCardUIs)
            {
                if (c == null) continue;
                if (_arena.IsLegalActivateTargetCached(hit, c))
                {
                    c.SetTargetHighlight(true);
                    _activateHighlighted.Add(c);
                }
            }
            return true;
        }

        /// <summary>激活拖拽中：更新箭头与合法目标高亮。
        /// 【MVC 收口-问题A】合法性查询改为读取 Arena 预计算的缓存（_activateLegalTargetCache），
        /// 不再实时调用 ActivateEligibility 规则，View 与规则解耦。</summary>
        public void UpdateActivateDrag(Vector2 globalPos)
        {
            if (!IsActivating || _activatingCard == null) return;
            var from = _activatingCard.GetVisualGlobalRect().GetCenter();
            _activateArrow.SetPoints(from, globalPos);

            // 【2026-10·JC108】记录"鼠标确实离开过来源卡"：用于区分
            // ①"拖出去再松回本卡身上"（＝指定自身为目标）与 ②"原地单击"（＝取消，不误发动）。
            if (!_activateLeftSource && !_activatingCard.GetVisualGlobalRect().HasPoint(globalPos))
                _activateLeftSource = true;

            var hovered = PickAreaCardAt(globalPos);
            bool legal = _arena.IsLegalActivateTargetCached(_activatingCard, hovered);
            foreach (var c in _arena.AreaCardUIs)
            {
                if (c == null) continue;
                bool hl = _arena.IsLegalActivateTargetCached(_activatingCard, c);
                c.SetTargetHighlight(hl);
                if (hl) _activateHighlighted.Add(c);
            }
            _lastDragHoverTarget = legal ? hovered : null;
        }

        /// <summary>激活拖拽结束：命中合法目标则发动能力并进入堆叠；否则取消归位。
        /// 【2026-10 用户口径·JC108】落点是**来源卡自身**时不再一律判取消：该能力声明"目标含自身"
        /// （`Target.CanTargetSelf`，由 `IsLegalActivateTargetCached` 把关）且本次确实拖动过
        /// （`_activateLeftSource`）⇒ 以**自身为目标**发动（用户："拖拽后在这张卡自己身上松开
        /// 会识别为取消效果"）；原地单击（从未离开来源卡）仍按取消处理。</summary>
        public void EndActivateDrag(Vector2 globalPos)
        {
            if (!IsActivating || _activatingCard == null) return;
            var source = _activatingCard;
            var ability = GetActivatingAbility(source);
            var target = PickAreaCardAt(globalPos);
            bool dragged = _activateLeftSource;
            bool selfTarget = target != null && target == source && dragged;

            ClearActivateTargetHighlights();
            if (_activateArrow != null) { _activateArrow.QueueFree(); _activateArrow = null; }

            _activatingCard = null;
            _activateLeftSource = false;
            // 源卡本体不随拖拽移动（仅画箭头），无需归位

            if (ability != null && target != null && (target != source || selfTarget)
                && _arena.IsLegalActivateTargetCached(source, target))
            {
                _actionService.ActivateAbility(source.Instance, ability, target.Instance);
            }
            else
            {
                GD.Print($"[DragController] 激活拖拽取消（无合法目标；落点={target?.Instance?.PlayerTemplate?.CardName ?? "空"}，"
                         + $"来源自身={target == source}，拖动过={dragged}）");
            }
        }

        public void ClearActivateTargetHighlights()
        {
            foreach (var c in _activateHighlighted)
                c?.SetTargetHighlight(false);
            _activateHighlighted.Clear();
            _lastDragHoverTarget = null;
        }

        /// <summary>从可发动的场上卡上取其"可用于拖拽的"激活能力。
        /// 【2026-09】改为 `ActivateEligibility.GetFirstActivateAbility`（模板 ∪ **运行期授予**）——
        /// 此前只读模板 ⇒ JC019 授予宿主的激活能力在拖拽路径上取不到（松手判"无合法目标"而取消）。
        /// 能走到这里的前提已由 `TryBeginActivateDragAt` 的 `CanDragActivate` 保证（单条 ＋ 需选卡目标）。</summary>
        private EffectData GetActivatingAbility(CardUI card)
            => ActivateEligibility.GetFirstActivateAbility(card?.Instance);
    }
}
