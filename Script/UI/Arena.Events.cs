using Godot;
using Script.Game.Entities;
using Script.Manager;
using Script.UI;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Arena 分部（M-09 结构拆分）：逻辑层事件订阅 + 全部事件处理器 + 计分区显示刷新。
///
/// 本文件仅为物理分文件，未做任何类提取：所有成员仍属 Arena，访问级别、调用关系、
/// 运行时行为与拆分前完全一致。原位置为 Arena.cs 行 642-677（订阅）、806-1018（处理器）、
/// 1065-1076（游戏结束）。
///
/// 这些处理器每个都跨 3-5 个 UI 子系统联动（地区槽位 / 计分区 / 手牌 / 世界牌库 / 统计），
/// 若提取为独立类需把 _areaSlots、_cardUIMap、_handCardUIs、各容器全部开成 internal，
/// 属净负债，故只做分文件不做提取。
/// </summary>
public partial class Arena : Control
{
    // ============================================================
    // 订阅逻辑层事件（核心：表现层仅通过事件获知状态变化）
    // ============================================================
    private void SubscribeToManagerEvents()
    {
        // TurnManager 事件
        _turnManager.OnPhaseChanged += OnPhaseChangedHandler;
        _turnManager.OnPriorityChanged += OnPriorityChangedHandler;
        _turnManager.OnTurnStarted += OnTurnStartedHandler;
        _turnManager.OnTurnEnded += OnTurnEndedHandler;
        _turnManager.OnCardDrawn += OnCardDrawnFromTurn;
        _turnManager.OnCardPlayed += OnCardPlayedHandler;
        _turnManager.OnAssetsChanged += OnAssetsChangedHandler;
        // 破平消耗费用（横置资产）后刷新资产表现（MVC：逻辑层事件 → UI 刷新）
        if (_initializer?.CombatManager != null)
            _initializer.CombatManager.OnAssetsChanged += OnAssetsChangedHandler;

        // CardManager 事件
        _cardManager.OnCardDied += OnCardDiedHandler;
        // 选目标高光总线：进入/退出选目标时刷新可选目标卡的黄色高光（MVC：纯读集合重绘）
        _cardManager.OnTargetSelectionChanged += OnTargetSelectionChangedHandler;

        // AreaManager 事件
        _areaManager.OnAreaWon += OnAreaWonHandler;
        _areaManager.OnAreaReplaced += OnAreaReplacedHandler;
        _areaManager.OnAreaRemoved += OnAreaRemovedHandler;
        _areaManager.OnMarksChanged += OnMarksChangedHandler;
        _areaManager.OnWorldDeckChanged += OnWorldDeckChangedHandler;
        _areaManager.OnAreaWonResolved += OnAreaWonResolvedHandler;

        // 效果引擎：结算/激活改动了卡牌横置等可见状态时通知表现层刷新
        // （复用资产变化处理器：同步资产区+争夺区横置视觉并重排卡牌；
        //  同时刷新激活高光，使"发动后横置"的卡立即取消可发动高光/拖拽箭头）
        //【MVC 收口】OnVisualDirty 已迁至 UiNotificationBus 实例单例，此处订阅其事件。
        Script.Manager.UiNotificationBus.Instance.OnVisualDirty += OnVisualDirtyHandler;

        // 翻面（暗藏↔公开）影响持续光环生效范围：源卡背面→整光环注销；目标卡背面→仅撤销该目标。
        // 经 CardManager 实例事件捕获 CovertChanged，重算光环并刷新 UI。
        _cardManager.OnCardStateChanged += OnCardStateChangedHandler;
    }

    /// <summary>OnVisualDirty 具名处理器（匿名 lambda 无法用 -= 反订阅，须具名化以支持 _ExitTree 清理）。</summary>
    private void OnVisualDirtyHandler()
    {
        OnAssetsChangedHandler();
        RefreshActivateHighlights();
        // 持续光环补授/撤销（夺控/入场/离场）会改写 MagicDomains、攻击力、调查力等数据，
        // 这些字段同时驱动地区能力统计 StatsLabel 与卡面动态标签，必须随 OnVisualDirty 一并刷新
        // （OnPostMove 的 SyncDerivedUI 早于 ReevaluateAuras 执行，故光环改写后需此处二次收口）。
        SyncDerivedUI();
    }

    /// <summary>OnCardStateChanged 具名处理器（匿名 lambda 无法用 -= 反订阅，须具名化以支持 _ExitTree 清理）。</summary>
    private void OnCardStateChangedHandler(CardInstance card, Script.Enums.CardChangeKind kind)
    {
        if (card == null) return;
        if (kind == Script.Enums.CardChangeKind.CovertChanged)
        {
            Script.Manager.EffectEngine.Instance.ReevaluateAuras(card.ControllerId);
        }
        // 横置/重置：JC068 阿格斯IV"未横置封锁"依赖此即时开关（横置→解除封锁，竖置→恢复封锁）
        else if (kind == Script.Enums.CardChangeKind.TappedChanged)
        {
            Script.Manager.EffectEngine.Instance.ReevaluatePrereqs(card.ControllerId);
        }
    }

    // ============================================================
    // TurnManager 事件处理器
    // ============================================================
    private void OnPhaseChangedHandler(TurnManager.TurnPhase phase)
    {
        GD.Print($"[Arena.UI] 阶段变更: {phase}");
        // 离开行动阶段 → 基础行动蒙版失效，强制退出（回到普通可出牌状态）
        if (_activeMask != MaskMode.None && !_turnManager.IsActionPhase())
            ExitMask();
        UpdateTurnUI();
    }

    private void OnPriorityChangedHandler(int playerId)
    {
        GD.Print($"[Arena.UI] 行动权变更: 玩家{playerId}");
        // 玩家1失去行动权（点让过 / 行动权转移给对手）→ 关闭蒙版。
        // 之后若对手让过或结算后行动权回到玩家1，仍可在自己行动步骤内重新点按钮打开蒙版。
        if (_activeMask != MaskMode.None && playerId != 1)
            ExitMask();
        // 先刷新核心行动权/阶段文字（直接读 _turnManager.PriorityPlayerId），
        // 保证即使后续 SyncDerivedUI 因悬挂节点抛异常，UI 文字也与 LOG 保持一致。
        UpdateTurnUI();
        // 派生 UI 刷新：用 IsInstanceValid 过滤已释放节点，避免 ObjectDisposedException 中断本 handler。
        SyncDerivedUISafe();
    }

    private void OnTurnStartedHandler(int turnCount)
    {
        GD.Print($"[Arena.UI] 回合开始: {turnCount}");
        // 回合开始：基础行动次数重置已上收 TurnManager（发 OnTurnStarted 前完成），View 不再反向驱动；
        // 这里仅退出蒙版（次数用尽后下一回合恢复，蒙版显示状态由 UI 管理）。
        ExitMask();

        SyncDerivedUI();   // 先后手可能已切换，统一刷新派生 UI（争夺区先手加成 + 地区统计）

        UpdateTurnUI();
        UpdateActionButtons();
    }

    private void OnTurnEndedHandler(int turnCount)
    {
        GD.Print($"[Arena.UI] 回合结束: {turnCount}");
    }

    private void OnCardDrawnFromTurn(int playerId, CardInstance instance)
    {
        // 数据层移动（TurnManager.DrawCardsForPlayer → CardManager.MoveCardToHand → ZoneManager.MoveToHand）
        // 已通过 OnCardMoved 事件同步触发 SyncCardUIToZone，完成了 CardUI 从牌库到手牌容器的挂载、
        // 列表加入与翻面。此处只做 SyncCardUIToZone 未覆盖的增量（拖拽订阅 + 允许拖拽），
        // 避免重复 AddChild / 重复加列表导致 TakeCard 找不到 UI（已被 Sync 提前从牌库列表移除）。
        CardUI cardUI = FindCardUIByUID(instance.UID);
        if (cardUI == null) return;
        cardUI.CardDragEnded -= _onCardDragEndedForward;   // 避免重复订阅
        cardUI.CardDragEnded += _onCardDragEndedForward;
        cardUI.CovertRevealRequested -= _onCovertRevealForward;  // 避免重复订阅
        cardUI.CovertRevealRequested += _onCovertRevealForward;
        cardUI.AttachmentDoubleClicked -= _onAttachmentDoubleClickedForward;  // 避免重复订阅
        cardUI.AttachmentDoubleClicked += _onAttachmentDoubleClickedForward;
        cardUI.CanDrag = true;   // 进入手牌后允许拖拽
        // 问题B：抽到的牌进入手牌时，按当前费用修正栈刷新减费显示（如本回合已激活 JC012①，"下一张帷幕守望牌费用-1"应让新抽到的牌变绿）。
        // RefreshCostReduction 仅读 Model（GetCostModification）+ 改自身 Label，符合 MVC 总线规范。
        cardUI.RefreshCostReduction();
        ArrangeAllCards();
        UpdateDeckCount();
    }

    /// <summary>出牌成功回调 — 仅用于 UI 更新（逻辑已在 TurnManager 中完成）</summary>
    private void OnCardPlayedHandler(int playerId, string cardUid, string targetUid)
    {
        GD.Print($"[Arena.UI] 出牌成功: P{playerId} {cardUid} -> {targetUid}");
        UpdateTurnUI();
    }

    // ============================================================
    // CardManager 事件处理器
    // ============================================================
    private void OnCardDiedHandler(CardInstance instance)
    {
        // 死亡卡的 UI 归位（从场上移除 + 挂墓地）已由 ZoneManager.OnCardMoved → SyncCardUIToZone 统一驱动，
        // 本 handler 不再重复操作 UI 列表/墓地表，仅做死亡日志与统计刷新，
        // 避免依赖"Sync 必须先于 OnCardDied 运行"的隐式顺序（此前为脆弱的空操作）。
        GD.Print($"[Arena.UI] 卡牌死亡事件: {instance.PlayerTemplate?.CardName} (归位由 SyncCardUIToZone 负责)");
        RefreshArenaStats();
    }

    /// <summary>选目标高光总线处理器：读合法目标 UID 集合，重绘全场卡的黄色可选高光。
    /// uids 为 null/空 → 清除所有高光；否则仅高亮集合内卡。纯读 Model 重绘，不改数据。</summary>
    private void OnTargetSelectionChangedHandler(List<string> uids)
    {
        HashSet<string> set = (uids == null || uids.Count == 0) ? null : new HashSet<string>(uids);
        foreach (var kv in _cardUIMap)
            kv.Value.SetSelectableHighlight(set != null && set.Contains(kv.Key));
    }

    // ============================================================
    // AreaManager 事件处理器
    // ============================================================
    private void OnAreaWonHandler(string areaUid, bool isTaskArea)
    {
        GD.Print($"[Arena.UI] 地区赢取事件: {areaUid} (任务地区={isTaskArea})");
        var slot = _areaSlots.FirstOrDefault(s => s.AreaUID == areaUid);
        if (slot == null) return;

        // 将地区卡(AreaCardUI)从 AreaSlot 的 CardContainer 取出，移入玩家1计分区 CardContainer
        AreaCardUI ui = slot.GetAreaCardUI();
        if (ui != null)
        {
            var cardContainer = slot.GetNode<Control>("CardContainer");
            cardContainer?.RemoveChild(ui);
            _scoreContainer1?.AddChild(ui);
            ui.SetFace(true);   // 正面朝上（计分卡面展示）
            ui.MouseFilter = MouseFilterEnum.Ignore;
            // 彻底方案B：分数累计由逻辑层 MoveToScore(实例) 完成，GetScoreTotal 实时计算，
            // Arena 不再调用 AddWonAreaScore（ScoreZone 已含真实地区实例）。

            GD.Print($"[Arena.Score] 地区卡「{slot.Data?.CardName ?? "未知"}」从 AreaSlot_{slot.AreaIndex}({slot.AreaUID}) 移入 Player1 分数区");
            RefreshScoreLabel();  // 计分区新增 → 刷新分数显示
            LogScoreZoneCards(1);
        }

        // 清空槽位地区卡引用，避免替换地区时 ResetToNewAreaData 对分数区里的原卡调用 Setup(newData)
        slot.SetAreaCardUI(null);

        if (isTaskArea)
        {
            // 任务地区赢取后的 UI 清理由 OnAreaRemovedHandler 处理
        }
        else
        {
            // 普通地区：刷新世界牌库显示（替换数据由 OnAreaReplacedHandler 处理）
            _worldDeckZone?.RefreshDisplay(_worldDeckManager?.Count ?? 0);
        }
    }

    private void OnAreaReplacedHandler(string areaUid, CardInstance newInstance)
    {
        GD.Print($"[Arena.UI] 地区替换事件: {areaUid} → {newInstance?.AreaTemplate?.CardName}");
        var slot = _areaSlots.FirstOrDefault(s => s.AreaUID == areaUid);
        if (slot == null) return;

        // 原地区卡 UI 已移入分数区，槽位现在是空的；需要新建一张地区卡 UI 放入槽位
        if (_areaCardUIScene != null && newInstance != null)
        {
            var newUI = _areaCardUIScene.Instantiate<AreaCardUI>();
            newUI.Setup(newInstance.AreaTemplate);
            newUI.SetFace(true);
            slot.SetAreaCardUI(newUI);
        }

        slot.ResetToNewAreaData(newInstance);   // slot.AreaUID 已更新为新实例 UID
        // 同步逻辑注册表：用新实例 UID 作为 key，与 AreaManager.ResolveAreaWon 保持一致
        // （避免 _areas key 与槽位 AreaUID 错位导致标记/对抗落到失效 UID）
        _actionService.RegisterArea(newInstance.UID, slot.AreaIndex, newInstance);
    }

    private void OnMarksChangedHandler(string areaUid, int p1, int p2)
    {
        var slot = _areaSlots.FirstOrDefault(s => s.AreaUID == areaUid);
        slot?.UpdateMarks(p1, p2);
    }

    private void OnAreaRemovedHandler(int index)
    {
        GD.Print($"[Arena.UI] 地区移除事件: 索引 {index}");
        if (index < 0 || index >= _areaSlots.Count) return;

        var slot = _areaSlots[index];
        _actionService.UnregisterArea(slot.AreaUID);   // 同步逻辑注册表
        _actionService.ShiftSlotIndicesFrom(index + 1); // 后续槽位编号前移
        _areaSlots.RemoveAt(index);
        slot.QueueFree();

        // 重新编号后续地区
        for (int i = index; i < _areaSlots.Count; i++)
            _areaSlots[i].SetAreaIndex(i + 1);

        ArrangeAreas();
    }

    private void OnWorldDeckChangedHandler()
    {
        _worldDeckZone?.RefreshDisplay(_worldDeckManager?.Count ?? 0);
    }

    /// <summary>赢取地区结算完成后刷新分数显示（总分由 PlayerManager 实时计算分数区内所有卡 Score 之和）</summary>
    private void OnAreaWonResolvedHandler(string areaUid, int winnerId, int scoreGained)
    {
        GD.Print($"[Arena.UI] 地区赢取结算完成: {areaUid} 赢取者P{winnerId} 获得{scoreGained}分");
        RefreshScoreDisplay();
    }

    // ============================================================
    // 计分区显示刷新（由上方地区赢取处理器驱动）
    // ============================================================
    /// <summary>刷新分数显示：写入 ScoreLabel（"分数：X"，X=玩家1计分区总分），并输出日志。</summary>
    private void RefreshScoreDisplay()
    {
        RefreshScoreLabel();
    }

    /// <summary>刷新 Player1Area/ScoreZone/ScoreLabel 显示玩家1计分区总分（"分数：X"）。
    /// 总分 = 计分区内所有玩家卡 Score 之和 + 赢取地区累计分数，均由 PlayerManager 统一计算（GetScoreTotal 已含两部分）。
    /// 在计分区有卡加入/离开时调用，确保分数实时更新。</summary>
    private void RefreshScoreLabel()
    {
        if (_playerManager == null || _scoreLabel == null) return;
        int s1 = _playerManager.GetScoreTotal(1);
        _scoreLabel.Text = $"分数：{s1}";
        // GD.Print($"[Arena.UI] 刷新分数 Label：分数：{s1}");
    }

    /// <summary>输出某玩家分数区内所有卡的卡名（含玩家卡与地区卡），用于调试。</summary>
    private void LogScoreZoneCards(int playerId)
    {
        var container = GetScoreContainer(playerId);
        if (container == null) return;
        var names = new System.Collections.Generic.List<string>();
        foreach (var child in container.GetChildren())
        {
            if (child is CardUI cardUI && cardUI.Instance?.PlayerTemplate != null)
                names.Add(cardUI.Instance.PlayerTemplate.CardName);
            else if (child is AreaCardUI areaUI && areaUI.Data != null)
                names.Add(areaUI.Data.CardName);
        }
        GD.Print($"[Arena.Score] 玩家{playerId}分数区当前有 {names.Count} 张卡: {string.Join(", ", names)}");
    }

    // ============================================================
    // PlayerManager 事件处理器
    // ============================================================
    /// <summary>
    /// 游戏结束事件处理器（订阅 PlayerManager.OnGameOver）：
    /// 显示居中胜负 LABEL，3 秒后切回主界面。判定规则在 Manager，UI 只做展示 + 场景切换。
    /// </summary>
    private async void OnGameOverHandler(int winnerId)
    {
        if (_resultLabel != null)
        {
            _resultLabel.Text = winnerId == 1 ? "玩家1 胜利！" : "玩家2 胜利！";
            _resultLabel.Visible = true;
        }
        GD.Print($"🏁 [Arena] 游戏结束，获胜=玩家{winnerId}，3 秒后返回主界面");
        await ToSignal(GetTree().CreateTimer(3.0f), "timeout");
        // 退出对战窗口、回到主界面（GameManager 统一状态机，MVC 合规：UI 仅触发切场景意图）
        GameManager.Instance?.GoToState(GameManager.GameState.MainMenu);
    }
}
