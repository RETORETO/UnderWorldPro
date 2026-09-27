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
        private CardUI _lastDragHoverTarget;

        // ===== D9 激活框架：场上卡激活拖拽状态 =====
        private CardUI _activatingCard;
        private StackArrow _activateArrow;
        private readonly List<CardUI> _activateHighlighted = new();
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
            // 战斗对抗窗口打开期间不抢手牌拖拽（由 ModalWindowCoordinator 处理伤害分配点击）
            if (_draggingHandCard == null && (_modalCoordinator?.ActiveStrengthWindow == null))
            {
                CardUI hit = PickHandCardAt(globalPos);
                if (hit != null && hit.CanDrag)
                {
                    _draggingHandCard = hit;
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
            // 模态窗口（调查/战斗/触发排序）打开期间冻结出牌，避免原子操作执行期间被打断（核心需求 7.3）
            if (_arena.IsDragInteractionLocked)
            {
                GD.Print($"[DragController.DragEnd] 模态窗口打开中，出牌被冻结，卡牌归位: {cardUI.Instance?.PlayerTemplate?.CardName}");
                ReturnCardToHand(cardUI);
                return;
            }

            var instance = cardUI.Instance;
            if (instance == null) return;

            bool inHand1 = _arena.HandCardUIs.Contains(cardUI);
            bool inHand2 = _arena.HandCardUIs2.Contains(cardUI);
            if (!inHand1 && !inHand2)
            {
                GD.Print($"[DragController.DragEnd] 非手牌卡({instance.PlayerTemplate?.CardName})的拖拽被忽略，归位原位");
                return;
            }

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
            if (isSorceryCard && requiresTarget
                && string.IsNullOrEmpty(targetCardUid) && string.IsNullOrEmpty(targetAreaName))
            {
                ClearDragTargetHighlights();
                _arena.BeginSorceryTargetSelection(instance, cardUI, playerId, hand);
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

        /// <summary>判断 dragging 这张手牌是否能以 target 这张场上卡为合法目标（委托 CardManager 规则判定）。</summary>
        public bool IsLegalCardTarget(CardUI dragging, CardUI target)
        {
            if (dragging == null || target == null || dragging == target) return false;
            return _cardManager != null && _cardManager.IsLegalPlayTarget(dragging.Instance, target.Instance);
        }

        /// <summary>按全局坐标命中检测场上卡（争夺区角色 + 附属区附属），返回最上层命中卡。</summary>
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
            return null;
        }

        /// <summary>拖拽中：对合法目标卡做半透明高亮（使用当前拖拽卡）。</summary>
        public void UpdateDragTargetHighlights()
        {
            if (_draggingHandCard == null) return;
            var dragging = _draggingHandCard;
            var mp = _arena.GetGlobalMousePosition();
            CardUI hovered = PickAreaCardAt(mp);
            bool hoveredLegal = IsLegalCardTarget(dragging, hovered);
            foreach (var c in _arena.AreaCardUIs)
            {
                if (c == null) continue;
                bool legal = IsLegalCardTarget(dragging, c);
                c.SetTargetHighlight(legal);
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

            // 角色卡（非附属）：沿用拖拽手势激活，绘制箭头并高亮合法目标。
            _activatingCard = hit;
            var arrow = _arena.StackArrowScene?.Instantiate<StackArrow>();
            if (arrow != null)
            {
                arrow.ZIndex = 4095;
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

        /// <summary>激活拖拽结束：命中合法目标则发动能力并进入堆叠；否则取消归位。</summary>
        public void EndActivateDrag(Vector2 globalPos)
        {
            if (!IsActivating || _activatingCard == null) return;
            var source = _activatingCard;
            var ability = GetActivatingAbility(source);
            var target = PickAreaCardAt(globalPos);

            ClearActivateTargetHighlights();
            if (_activateArrow != null) { _activateArrow.QueueFree(); _activateArrow = null; }

            _activatingCard = null;
            // 源卡本体不随拖拽移动（仅画箭头），无需归位

            if (ability != null && target != null && target != source &&
                _arena.IsLegalActivateTargetCached(source, target))
            {
                _actionService.ActivateAbility(source.Instance, ability, target.Instance);
            }
            else
            {
                GD.Print("[DragController] 激活拖拽取消（无合法目标）");
            }
        }

        public void ClearActivateTargetHighlights()
        {
            foreach (var c in _activateHighlighted)
                c?.SetTargetHighlight(false);
            _activateHighlighted.Clear();
            _lastDragHoverTarget = null;
        }

        /// <summary>从可发动的场上卡上取其第一个角色激活能力（用于发动）。</summary>
        private EffectData GetActivatingAbility(CardUI card)
        {
            if (card?.Instance?.PlayerTemplate is not PlayerCardData tmpl) return null;
            foreach (var e in tmpl.EffectDataList)
            {
                if (e.Timing is EffectTiming.CharacterActivated or EffectTiming.AttachmentActivated) return e;
            }
            return null;
        }
    }
}
