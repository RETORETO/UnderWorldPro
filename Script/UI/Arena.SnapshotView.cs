using Script.Net;

/// <summary>
/// 【C/S 化 P3·模块B】Arena 的快照渲染接入（Arena 分部文件）。
///
/// 职责：让 Arena 具备"消费 GameSnapshot 而非本地 Model 直读"的可选渲染路径。
/// - 单机模式：默认不启用（_snapshotDriver 为 null），仍走 ZoneManager 事件总线 → SyncCardUIToZone
///   的本地 Model 直读刷新，行为完全不变。
/// - 网络模式：调用 EnableSnapshotRender() 启用后，服务端下发的快照经 ApplySnapshot() 进入
///   SnapshotRenderDriver，Diff 后路由到本分部实现的 ISnapshotView，把增量指令映射到 Arena
///   现有的布局/刷新方法（ArrangeAllCards / RefreshStackUI / SyncDerivedUI / UpdateTurnUI 等）。
///
/// 注意：这是"客户端消费快照"契约层的地基。当前 Arena 底层卡片仍以 CardInstance 承载，故本
/// 分部把指令保守映射为"重新排列 + 派生状态刷新"，不破坏现有单机渲染；真正的"UI 纯数据化
/// （CardUI 改由 ZoneCardRef 挂载、不依赖 CardInstance）"留待网络客户端实际落地时推进。
/// </summary>
public partial class Arena
{
    /// <summary>启用快照渲染模式（网络模式）。默认单机不启用。</summary>
    public void EnableSnapshotRender()
    {
        if (_snapshotDriver == null)
            _snapshotDriver = new SnapshotRenderDriver(this);
    }

    /// <summary>客户端收到服务端下发的整局快照后调用：增量刷新 UI（首包走全量）。</summary>
    public void ApplySnapshot(GameSnapshot snap)
    {
        if (_snapshotDriver == null)
            EnableSnapshotRender();
        _snapshotDriver.ApplySnapshot(snap);
    }

    // ==================== ISnapshotView 实现 ====================
    // 保守策略：不破坏现有单机渲染，指令映射到现有布局/刷新方法。

    public void AddCard(CardRenderCommand cmd)
    {
        // 新卡由快照触发：若尚无对应本地实例则跳过（交由后续快照一致性刷新），有则重新排列。
        ArrangeAllCards();
    }

    public void RemoveCard(string uid)
    {
        ArrangeAllCards();
    }

    public void MoveCard(CardRenderCommand cmd)
    {
        ArrangeAllCards();
        RefreshArenaStats();
    }

    public void UpdateCard(CardRenderCommand cmd)
    {
        var ui = FindCardUIByUID(cmd.Uid);
        if (ui?.Instance != null)
        {
            // 快照属性 → 应用到本地实例（C/S 下实例由快照重建后同构，此路径仅做状态同步）
            if (cmd.IsTapped != ui.Instance.IsTapped) ui.SetTapped(cmd.IsTapped);
            if (cmd.IsCovert != ui.Instance.IsCovert) ui.Instance.SetCovert(cmd.IsCovert);
            ui.RefreshStats();
        }
        SyncDerivedUI();
    }

    public void StackPush(StackRenderCommand cmd)
    {
        RefreshStackUI();
    }

    public void StackPop()
    {
        RefreshStackUI();
    }

    public void UpdatePlayer(PlayerRenderCommand cmd)
    {
        RefreshArenaStats();
        SyncDerivedUI();
    }

    public void UpdatePhase(PhaseRenderCommand cmd)
    {
        UpdateTurnUI();
        RefreshActivateHighlights();
    }
}
