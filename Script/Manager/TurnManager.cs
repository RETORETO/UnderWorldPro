using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Interfaces;
using Script.Game.Entities;

/// <summary>
/// TurnManager - 回合流程管理器（单例）
/// 职责：管理回合流程、阶段切换、行动权分配、出牌验证
/// UI 层通过订阅事件来更新显示，不直接控制回合逻辑
/// </summary>
public partial class TurnManager : Node
{
    // ============================================================
    // 单例
    // ============================================================
    public static TurnManager Instance { get; private set; }

    /// <summary>阶段/步骤级诊断日志开关（降噪）。默认 false：测试/联机只保留必要日志；规则调试时置 true 可看阶段明细。不影响逻辑。</summary>
    public static bool VerboseSteps = false;

    // ============================================================
    // 阶段枚举
    // ============================================================
    public enum TurnPhase
    {
        StartReset,         // 重置步骤
        StartPrepare,       // 准备步骤
        StartDraw,          // 抓牌步骤
        ActionFirst,        // 先手行动
        ActionSecond,       // 后手行动
        PreCombat,          // 对抗前优先权窗口（进入调查对抗前，双方玩家让过才开始）
        CombatInvestigation,// 调查对抗
        CombatStrength,     // 战斗对抗（以战力 Strength 比对的对抗）
        CombatPower,        // 势力对抗
        EndCleanup,         // 结束步骤
        EndRecover,         // 恢复步骤
        EndPassTurn,        // 移交先手
        Mulligan            // 【第0回合】开局调度步骤：发完起手手牌后、第一回合重置步骤前（联机）
    }

    // ============================================================
    // 公开属性
    // ============================================================
    public int TurnCount { get; internal set; } = 0;
    public TurnPhase CurrentPhase { get; internal set; } = TurnPhase.StartReset;
    /// <summary>先手玩家（1/2）。**0 = 尚未产生**：第0回合（调度步骤）期间双方未分先后手。</summary>
    public int FirstPlayerId { get; internal set; } = 1;
    public int PriorityPlayerId { get; internal set; } = 0;

    /// <summary>是否已产生先手玩家（第0回合调度期间为 false）。</summary>
    public bool HasFirstPlayer => FirstPlayerId == 1 || FirstPlayerId == 2;
    /// <summary>
    /// 先手行 UI 文案（View 只读）：未产生时显示"先手：未产生"，否则"先手：P1/P2"。
    /// 单机/联机两处回合状态标签共用本属性，避免文案分叉。
    /// </summary>
    public string FirstPlayerDisplayText => HasFirstPlayer ? $"先手：P{FirstPlayerId}" : "先手：未产生";

    // ============================================================
    // 事件系统（供 UI 订阅）
    // ============================================================
    public event Action<TurnPhase> OnPhaseChanged;
    public event Action<int> OnPriorityChanged;
    public event Action<int> OnTurnStarted;
    public event Action<int> OnTurnEnded;
    /// <summary>当双方玩家各抽一张牌后触发，参数为 (playerId, cardInstance)</summary>
    public event Action<int, CardInstance> OnCardDrawn;
    /// <summary>出牌成功，参数为 (playerId, cardUid, targetUid)</summary>
    public event Action<int, string, string> OnCardPlayed;
    /// <summary>资产横置/解除横置后触发，供 UI 刷新横置表现</summary>
    public event Action OnAssetsChanged;
    /// <summary>先手加成重算完成后触发，供 UI 统一刷新先手角标（避免在渲染过程中反向驱动 Controller 重算）</summary>
    public event Action OnFirstStrikeRecalculated;

    /// <summary>C/S 化：网络化异步决策结算（触发排序/选目标等）全部完成后触发。
    /// 服务端 NetworkMatchLauncher 订阅后主动广播快照，客户端才能及时看到续算结果。</summary>
    public event Action OnAsyncSettlementCompleted;

    /// <summary>供外部（如 Arena 蒙版行动）触发资产变化刷新；封装 event 的 Invoke。</summary>
    public void NotifyAssetsChanged() => OnAssetsChanged?.Invoke();

    // ============================================================
    // Manager 引用
    // ============================================================
    private DeckManager _deckManager;
    private PlayerManager _playerManager;
    private CardManager _cardManager;
    private ZoneManager _zoneManager;
    private CombatManager _combatManager;
    private StackManager _stackManager;
    private AreaManager _areaManager;
    private ITriggerView _view;

    // ==================== C/S 化 P0-B 输入网关 ====================
    // 注入后，触发排序窗口（RunTriggerPhaseAsync）经网关向各所有者发起异步排序请求，
    // 而非依赖本地 _view 弹窗（解决 D17 联机窗口无输入隔离）。
    // 未注入（null）时所有 *Async 入口退化为同步行为，与现有逻辑完全一致。
    public Script.Net.IInputGateway InputGateway { get; set; }
    /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
    public Script.Manager.EffectEngine EffectEngineRef { get; set; }

    /// <summary>未显式注入时回退静态 Instance（向后兼容）。</summary>
    private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;
    //【MVC 收口】触发系统三实例（由 Composition Root 装配注入，取代原 static class）
    private TriggerRegistry _triggerRegistry;
    private TriggerEmitPoints _triggerEmitPoints;
    private GameEventBus _gameEventBus;

    // D9 激活框架：暴露给 EffectEngine 等静态入口访问 manager 引用
    public PlayerManager PlayerManagerRef => _playerManager;
    public AreaManager AreaManagerRef => _areaManager;
    public StackManager StackManagerRef => _stackManager;
    public CardManager CardManagerRef => _cardManager;

    // ============================================================
    // 规则层原子锁（核心需求 7.3）：原子操作（出牌/结算/对抗/触发排序窗口/选目标窗口）
    // 执行期间不给响应权。锁的权威在 Controller（TurnManager），由 View（Arena）在打开/关闭
    // 模态窗口时通过 EnterAtomic/ExitAtomic 通知维护；Pass/PlayCard 入口据此拒绝，确保即便
    // 经 PM 命令或程序入口也无法在原子操作期间破坏响应权（与 View 层冻结双重保险，呼应偏离 D18）。
    // ============================================================
    private bool _atomicLock;
    public bool IsAtomicLocked => _atomicLock;
    public void EnterAtomic() => _atomicLock = true;
    public void ExitAtomic()
    {
        // 仅当没有任何模态窗口仍打开、且没有"决策持锁"未释放时才解锁
        if (_openModalCount <= 0 && _decisionHoldDepth <= 0) _atomicLock = false;
    }
    private int _openModalCount;   // 由 Arena 在 LockModal/UnlockModal 时维护

    // ------------------------------------------------------------
    // 【2026-09·原子操作期间的玩家选择】计数式"决策持锁"（用户口径）：
    //   一个原子操作期间若需要玩家选择，**在玩家选择完成之前必须阻塞进程**——
    //   此期间只有持续效果可以影响游戏，不得有新的行动/触发效果进入堆叠。
    //   该持锁可嵌套（多层窗口/多处等待），用计数而非布尔，且**绝不因某个模态窗
    //   关闭而误放**（旧实现里 `NotifyModalClosed` 会直接解锁，把外层持锁一并掀掉）。
    //   持有者：① 单机 View 的每个选择窗（Arena.LockChoiceWindow）；
    //           ② 引擎/规则类等待玩家回包的联机决策（EffectEngine.BeginGatewayFlow、
    //              领袖/道途/遏制/预测规则、JC119 免费打出取目标、恢复步骤弃牌循环）。
    // ------------------------------------------------------------
    private int _decisionHoldDepth;
    public int DecisionHoldDepth => _decisionHoldDepth;

    /// <summary>【2026-09】进入"等待玩家决策"的原子持锁段（可嵌套，必须与 <see cref="EndDecisionHold"/> 成对）。</summary>
    public void BeginDecisionHold()
    {
        _decisionHoldDepth++;
        _atomicLock = true;
    }

    /// <summary>【2026-09】退出"等待玩家决策"的原子持锁段；计数归零且无模态窗时才真正解锁。</summary>
    public void EndDecisionHold()
    {
        if (_decisionHoldDepth > 0) _decisionHoldDepth--;
        if (_decisionHoldDepth <= 0 && _openModalCount <= 0) _atomicLock = false;
    }

    /// <summary>
    /// 【2026-09】联机"等待玩家回包"期间的统一持锁入口：把整段异步决策包在决策持锁内
    /// （服务端没有 View 帮忙 <see cref="NotifyModalOpened"/>）。与单机窗口持锁语义一致。
    /// </summary>
    public async System.Threading.Tasks.Task<T> HoldAtomicDuringDecisionAsync<T>(System.Func<System.Threading.Tasks.Task<T>> flow)
    {
        BeginDecisionHold();
        try { return await flow(); }
        finally { EndDecisionHold(); }
    }

    public void NotifyModalOpened() { _openModalCount++; EnterAtomic(); }
    public void NotifyModalClosed()
    {
        _openModalCount = Math.Max(0, _openModalCount - 1);
        if (_openModalCount <= 0 && _decisionHoldDepth <= 0) _atomicLock = false;
    }

    /// <summary>同步原子段结束时调用：若没有任何模态窗口仍打开、也没有决策持锁，则原子操作已完整结束，解除规则层锁。</summary>
    public void ExitAtomicWhenIdle()
    {
        if (_openModalCount <= 0 && _decisionHoldDepth <= 0) _atomicLock = false;
    }

    /// <summary>
    /// 【A55】效果层"挂起-续算"跑完一轮后的**结算收尾**（与 `ResolveStackTop` 同步段末尾对称）：
    /// 挂起时 `ResolveStackTop` 已提前 return ⇒ 地区赢取检查 / 解原子锁 / 免费打出队列 需要在这里补做，
    /// 否则服务端会一直握着原子锁（死锁）且漏掉地区赢取与 JC119 的免费打出队列。
    /// 由 `EffectEngine.ContinuePendingChain()` 在"链上不再等待输入"时调用。
    /// </summary>
    public void OnEngineChainSettled()
    {
        // 结算后若堆叠清空，立即检查是否有基础地区满足赢取条件（与 ResolveStackTop 同款）
        if (_stackManager != null && _stackManager.IsEmpty && _areaManager != null)
            _areaManager.TryStartAreaWin(_stackManager);

        ExitAtomicWhenIdle();

        // 【JC119】效果请求的免费打出：正常打出流程（入堆叠/优先级窗口照常）
        ProcessQueuedFreePlays();

        // 【A55·联机 UI 刷新】本次"挂起-续算"已全部落地 ⇒ 通知服务端**立即下发快照**。
        // 缺陷实测（服务器日志 18:25）：JC007 检索经网关选牌后，"逆转异界之门 加入玩家1 手牌"发生在
        // 客户端回包的**异步续算**里，而 `MatchHost` 只在"意图流程结束"时推快照 ⇒ 客户端要等到
        // 下一次上行（让过）才看得到新手牌（"卡片加入手牌，玩家让过后才能看到手牌"）。
        NotifyResolutionSettled();
    }

    /// <summary>
    /// 【A55】"一次结算（含异步续算）已落地"的公开通知口：联机由 `MatchHost` 订阅
    /// `OnAsyncSettlementCompleted` 后**立即下发快照**，保证服务端异步落地的状态变化（检索入手 /
    /// 弃牌 / 封印 / 牺牲 / 可选支付…）在客户端即时可见。
    /// 同步结算路径由 `ResolveStackTop` 末尾经 `OnAsyncSettlementCompleted` 之外的上行流程覆盖，
    /// 引擎侧挂起-续算与规则类（领袖/道途/遏制/预测）的异步流程统一调本方法。
    /// </summary>
    public void NotifyResolutionSettled() => OnAsyncSettlementCompleted?.Invoke();

    // ============================================================
    // 内部状态
    // ============================================================
    private int _passCount = 0;

    /// <summary>
    /// 【显式让过状态机】本响应/步骤窗口内**已让过的玩家**集合（不再用"翻转 1/2"猜对手）。
    /// 推进条件严格等价于「天生优先玩家 A 让过 + 另一方 B 让过」：
    ///  · A 让过 → 行动权交给 B；
    ///  · B 让过 → 双方都已让过 → 直接推进（结算栈顶 / 推进步骤）。
    /// 与 `_passCount` 同步维护（后者仅供日志/旧口径读取）。
    /// </summary>
    private readonly System.Collections.Generic.HashSet<int> _passedPlayers
        = new System.Collections.Generic.HashSet<int>();

    /// <summary>复位"让过窗口"：新步骤开始 / 有东西入堆叠 / 堆叠结算后，双方的让过状态一并清空。</summary>
    private void ResetPassWindow()
    {
        _passCount = 0;
        _passedPlayers.Clear();
    }

    /// <summary>另一位玩家（2 人局）：1→2，2→1。让过后行动权**显式**交给另一方，不再盲目翻转。</summary>
    public static int OtherPlayerId(int playerId) => playerId == 1 ? 2 : 1;

    // ============================================================
    // 【规则简化】免让过步骤的自动推进
    // ------------------------------------------------------------
    // 重置步骤 / 恢复步骤 / 移交先手不再等待"双方连续让过"：
    //   · 重置、恢复：各自 `DoXxx` 末尾调用 `TryAutoAdvance()`——**仅当堆叠为空**时直接推进
    //     （堆叠非空＝有触发待响应，仍走既有让过窗口，保持"响应窗口隔离在阶段外"的语义）；
    //   · 移交先手：原本就直接 `NextTurn()`，不经让过。
    // 递归保护：`DoPassTurn → NextTurn → EnterPhase(StartReset) → DoReset → TryAutoAdvance` 会形成
    // "重置→准备"的自动链（重置免让过后必然继续推进），再加用户效果触发可能出现更长的连续自动推进；
    // 用 `_autoAdvanceDepth` 计数兜底，超过阈值即告警并停止自动推进（退回让过窗口），防止递归过深/死循环。
    // ============================================================
    private int _autoAdvanceDepth = 0;
    private const int MaxAutoAdvanceDepth = 8;

    /// <summary>
    /// 【规则简化】免让过步骤的推进入口：**堆叠为空**时立即 `AdvancePhase()`，否则保持既有让过窗口不动。
    /// 仅在 `IsAutoAdvanceStep` 覆盖的步骤（重置 / 恢复）末尾调用。
    /// </summary>
    private void TryAutoAdvance()
    {
        if (!IsAutoAdvanceStep(CurrentPhase)) return;

        int stackCount = _stackManager?.Entries.Count ?? 0;
        if (stackCount > 0)
        {
            // 堆叠非空：有触发/效果待结算 → 仍需在优先权窗口内让过（本步骤不再自动推进）
            if (VerboseSteps)
                GD.Print($"⏭ [TurnManager] 【{CurrentPhaseDisplayName}】免让过步骤但堆叠非空（{stackCount} 项），转为等待双方让过");
            return;
        }

        // 恢复步骤：叠空即进入"手牌上限检查点"——若超上限则启动弃牌循环（门控，阻止提前离开）
        if (CurrentPhase == TurnPhase.EndRecover && !RecoverCanLeave())
        {
            RunRecoverHandLimitAsync();
            return;
        }

        if (_autoAdvanceDepth >= MaxAutoAdvanceDepth)
        {
            GD.PrintErr($"⚠ [TurnManager] 免让过自动推进深度达上限（{MaxAutoAdvanceDepth}），停止自动推进，退回让过窗口（阶段={CurrentPhase}）");
            return;
        }

        _autoAdvanceDepth++;
        try
        {
            GD.Print($"⏭ [TurnManager] 【{CurrentPhaseDisplayName}】免让过步骤：结算完毕且堆叠为空 → 直接推进下一阶段");
            AdvancePhase();
        }
        finally
        {
            _autoAdvanceDepth--;
        }
    }

    /// <summary>对抗阶段的地区游标：本回合参与对抗的地区列表快照（按编号从小到大）。
    /// 进入 ActionSecond → CombatInvestigation 时建立，避免结算过程中地区增删导致索引错乱。</summary>
    private System.Collections.Generic.List<string> _combatAreaUids = new System.Collections.Generic.List<string>();
    /// <summary>当前正在结算对抗的地区索引（0-based，对应 _combatAreaUids）。</summary>
    private int _combatAreaIndex = 0;

    /// <summary>【P3·K18】本回合**已经为哪些地区追加过**"额外进行一次势力对抗"（JC067）——
    /// 卡文"额外进行**一次**"＝每地区每回合至多追加一次；重置步骤（`DoReset`）清空。</summary>
    private readonly System.Collections.Generic.HashSet<string> _extraPowerDoneAreas
        = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

    /// <summary>当前对抗地区 UID（无地区时为空串）。</summary>
    public string CurrentCombatAreaUid =>
        (_combatAreaIndex >= 0 && _combatAreaIndex < _combatAreaUids.Count) ? _combatAreaUids[_combatAreaIndex] : "";
    /// <summary>当前对抗地区序号（1-based，无地区时为 0），供 UI 显示"1地区调查步骤"。
    /// 取 AreaManager 注册的<b>真实槽位编号</b>（与 Arena UI 一致），避免游标序号错位。</summary>
    public int CurrentCombatAreaNo
    {
        get
        {
            // 【联机投影】客户端 TurnManager 没有权威的地区游标（_combatAreaUids / _areaManager），
            // 只能靠服务端快照下行的序号（ApplySnapshot 写入）；服务端本字段恒 0，行为不变。
            if (_projectedCombatAreaNo > 0) return _projectedCombatAreaNo;
            string uid = CurrentCombatAreaUid;
            if (uid == "") return 0;
            int realNo = _areaManager != null ? _areaManager.GetSlotIndex(uid) : 0;
            return realNo > 0 ? realNo : _combatAreaIndex + 1;
        }
    }

    /// <summary>【联机投影】快照下行的对抗地区序号（仅客户端写入；服务端恒 0）。</summary>
    private int _projectedCombatAreaNo = 0;

    // ============================================================
    // 生命周期
    // ============================================================
    public override void _Ready()
    {
        Instance = this;
        //【MVC 收口】TurnManagerRef 注入已收归 GameInitializer（effectEngine.TurnManagerRef = TurnManager），
        // 此处不再重复注入（避免依赖 EffectEngine.Instance 已创建的时序脆弱性）。
        // P2：回合结束时还原所有"直到回合结束"的时效修正（持续/修正栈）
        OnTurnEnded += (turn) => EE?.ExpireTurnEndEffects(turn);
        GD.Print("✅ TurnManager 单例初始化完成");
    }

    /// <summary>
    /// 注入依赖的 Manager 引用（在 GameInitializer 中调用）
    /// </summary>
    public void Initialize(DeckManager deckManager, PlayerManager playerManager, CardManager cardManager)
    {
        _deckManager = deckManager;
        _playerManager = playerManager;
        _cardManager = cardManager;

        // 注（2026-08-11·目标丢失总线清零）：原 CardManager.OnCardStateChanged 订阅
        // （OnCardStateChangedHandler，含翻面③与移动⑥两条即时置位分支）已删除。
        // 目标丢失判定全部下沉到【结算时】的 IsTargetLost 三段链：
        //   ① 实例不存在/离场 → ② 版本号比对（覆盖离场/翻面/子类别等一切 BumpVersion 场景）
        //   → ③ TargetValidity.Check（本地区发动地快照、Friendly/Enemy 等静态条件复检）。
        // 至此不再有任何"目标丢失"相关总线；剩余总线（OnPostMove / OnCardStateChanged）纯供 UI 刷新。

        //【MVC 收口】触发系统三实例的委托注入移至 SetTriggerSystem（由 GameInitializer 在
        // 创建 TriggerRegistry/TriggerEmitPoints 后调用），此处不再有 static 注入。
    }

    /// <summary>
    ///【MVC 收口】注入触发系统三实例（由 GameInitializer 装配后调用）。
    /// TriggerRegistry 负责收集匹配，RunTriggerPhase（既有排序窗口/入栈/让过）仍由本类驱动；
    /// TriggerEmitPoints 供本类业务点（现身/进场/回合开始/机动/回合结束）广播触发事件。
    /// 新增触发时机时，只需在 GameEvent 枚举 + TriggerRegistry.EventToTiming 增加映射，
    /// 并在对应业务点经 _triggerEmitPoints / _gameEventBus.Emit(...)。
    /// </summary>
    public void SetTriggerSystem(TriggerRegistry registry, TriggerEmitPoints emitPoints, GameEventBus bus)
    {
        _triggerRegistry = registry;
        _triggerEmitPoints = emitPoints;
        _gameEventBus = bus;

        registry.GetAllInstances = () => _cardManager?.GetAllInstances() ?? new System.Collections.Generic.List<CardInstance>();
        // 联机（InputGateway 注入）走网络化异步流程；单机/测试仍走原同步流程，行为不变。
        registry.RunTriggerPhase = (triggers) => RunTriggerPhaseDispatch(triggers, onAllCompleted: () =>
        {
            GD.Print($"[Trigger] 触发阶段结束，进入正常响应让过流程（总线驱动）");
        });
        // 进场/现身触发统一走 RunTriggerPhase 排序窗口（先收集→玩家排序窗口→确认→入栈）；
        // 需选目标的触发在 RunTriggerPhase→PushOrderedTriggers→EnsureTargetThenPush 中弹选（含跨争夺区候选），
        // 不再走"直接入栈"快捷路径。原 PushTriggerForCard 委托已废弃，保留字段兼容。
        // D-1 熔断查询：堆叠累计压入达上限后，TriggerRegistry 不再收集任何触发、EffectEngine 拒绝激活
        registry.IsCircuitBroken = () => _stackManager != null && _stackManager.IsCircuitBroken;
        EE.IsStackCircuitBroken = () => _stackManager != null && _stackManager.IsCircuitBroken;
    }

    /// <summary>
    /// 记录目标入栈快照，供结算时比对目标是否丢失。在目标锁定入栈后调用（PushTriggerToStack / PlayCard）。
    /// 记录三项：
    ///  · TargetVersionId —— 核心比对依据，结算时不一致即判丢失（覆盖离场/翻面/夺控/子类别变更等）；
    ///  · SourceZoneDetailSnapshot —— 规则⑥"本地区"的发动地基准（争夺区内移动不刷版本号，靠它兜住）；
    ///  · SourceControllerSnapshot —— Friendly/Enemy 复检的敌我基准（来源事后被夺控不改变入栈时的敌我关系）。
    /// 注（2026-08-11）：原目标侧的控制者/子类别快照已删除，其语义由版本号方案统一覆盖。
    /// </summary>
    private void CaptureTargetSnapshot(Script.Game.Entities.StackEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.TargetCardUid)) return;
        var target = _cardManager?.GetInstance(entry.TargetCardUid);
        if (target == null) return;
        entry.HasTargetSnapshot = true;
        // 注（2026-08-11）：TargetVersionId 已由 StackManager.Push 统一收口登记（D16），此处不再重复赋值，
        // 避免双重登记；本方法仅保留来源快照（规则⑥本地区 / Friendly-Enemy 复检基准）。
        // 记录来源(自身)快照：来源卡可能为另一张（JC030 来源≠自身）或同卡（CanTargetSelf）。
        var src = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
        if (src != null)
        {
            // 【触发语义】"本地区"基准优先取**触发时所在地区**（触发来源卡的那个 StackEffectInstance 快照）：
            // 死亡触发（卡已进墓地）与"进场即死"的进场触发，此后 src.ZoneDetailId 已为 null，
            // 只有触发时地区才是规则⑥要的那个"发动地"。
            string triggerArea = entry.StackEffect?.SourceAreaAtTrigger;
            entry.SourceZoneDetailSnapshot = !string.IsNullOrEmpty(triggerArea) ? triggerArea : src.ZoneDetailId;
            entry.SourceControllerSnapshot = src.ControllerId;
            entry.HasSourceZoneSnapshot = true;
        }
        GD.Print($"🎯 [目标锁定] 条目 {entry.SourceCardUid ?? entry.SourceCard?.UID} 取目标卡 {entry.TargetCardUid}" +
                 $"（名称={target.PlayerTemplate?.CardName}, 当前区={target.ZoneDetailId}, 控制者={target.ControllerId}），" +
                 $"记录入栈快照：版本号={entry.TargetVersionId}，来源地区={entry.SourceZoneDetailSnapshot}，来源控制者={entry.SourceControllerSnapshot}");
    }

    /// <summary>注入 Arena 引用（用于打开触发排序窗口 / 选目标窗口）。</summary>
    public void SetTriggerView(ITriggerView view) => _view = view;

    // ==================== 【P2】弃牌（手牌 → 墓地） ====================
    /// <summary>随机源（随机弃牌用）。由 GameInitializer 注入，禁止在流程内 new Random()。</summary>
    private Script.Game.IRandomSource _rng;
    public void SetRandomSource(Script.Game.IRandomSource rng) => _rng = rng;

    /// <summary>UI 侧每次弃牌一张后的事件（可选，供表现层刷新手牌）。</summary>
    public event System.Action<int, CardInstance> OnCardDiscarded;

    /// <summary>取某玩家当前手牌（副本，供"选一张弃掉"的候选构建）。</summary>
    public List<CardInstance> GetHandOf(int playerId)
        => _cardManager != null ? new List<CardInstance>(_cardManager.GetHandCardsByOwner(playerId)) : new List<CardInstance>();

    /// <summary>
    /// 【P2·弃牌唯一出口】把给定手牌弃入墓地，并广播 `OnDiscard`。
    /// 规则：手牌 → 墓地；只有"弃牌"走本方法（回手/回牌库/进墓地结算都不是弃牌）。
    ///
    /// 【OnDiscard 粒度·用户 2026-09 口径】**一次调用 = 一批，只广播一次**（与 `OnDraw` 同口径）：
    /// 本批全部弃完后广播一次，`ExtraInt` = **本次弃牌动作的总张数**
    /// （供 JC040「每当敌方玩家弃牌时，遏制X，X＝此次弃牌的数量」按批取数——不是逐张各触发一次）。
    /// </summary>
    /// <param name="discarderId">弃牌者（`OnDiscard` 的引发玩家 = 他，供"每当敌方玩家弃牌时"判定）。</param>
    /// <param name="cards">要弃的牌（非手牌/空引用自动跳过）。</param>
    /// <param name="batchSize">本次弃牌动作的总张数（`OnDiscard` 的 ExtraInt）；批内实际弃掉的张数不足时按实际张数广播。</param>
    /// <returns>实际弃掉的张数。</returns>
    public int DiscardCards(int discarderId, System.Collections.Generic.IEnumerable<CardInstance> cards, int batchSize = 1)
    {
        int discarded = 0;
        if (cards == null) return 0;
        CardInstance firstDiscarded = null;
        foreach (var card in cards)
        {
            if (card == null) continue;
            if (card.CurrentZone != CardZone.Hand)
            {
                GD.Print($"[TurnManager] 弃牌跳过 {card.PlayerTemplate?.CardName}：不在手牌（{card.CurrentZone}）");
                continue;
            }
            _cardManager.MoveCardToGraveyard(card);      // 手牌 → 墓地（唯一去向；弃牌不是死亡）
            OnCardDiscarded?.Invoke(discarderId, card);
            firstDiscarded ??= card;
            GD.Print($"[TurnManager] 玩家{discarderId} 弃牌：{card.PlayerTemplate?.CardName}（本次共 {batchSize} 张）");
            discarded++;
        }
        // 【OnDiscard 粒度】一次弃牌动作只广播一次；条件满足时该牌还在手牌 → 区域快照按"手牌"记录。
        if (discarded > 0 && firstDiscarded != null)
            _triggerEmitPoints?.OnDiscard(firstDiscarded, discarderId,
                batchSize > 0 ? System.Math.Min(batchSize, discarded) : discarded);
        // 【UI 刷新】弃牌是"模型已变、请刷新"的可见变更：显式广播 UI 通知总线。
        // 单机 Arena 订阅 OnVisualDirty → OnAssetsChangedHandler + ArrangeAllCards（与出牌/激活等同一收口通道），
        // 保证手牌/墓地 UI 与数据同步；不再依赖"ZoneManager.OnCardMoved→SyncCardUIToZone"这一单一路径，
        // 避免在恢复步骤等异步流程中因时序问题导致界面滞后。
        if (discarded > 0)
            Script.Manager.UiNotificationBus.Instance?.NotifyVisualDirty();
        return discarded;
    }

    /// <summary>【P2】随机弃牌（张数 = count 与手牌数的较小值），走注入的可复现随机源。</summary>
    public int DiscardRandom(int discarderId, int count, int batchSizeOverride = -1)
    {
        var hand = GetHandOf(discarderId);
        int total = System.Math.Min(System.Math.Max(0, count), hand.Count);
        if (total <= 0) return 0;
        var picked = new List<CardInstance>();
        for (int i = 0; i < total; i++)
        {
            int idx = _rng != null ? _rng.Next(hand.Count) : 0;
            picked.Add(hand[idx]);
            hand.RemoveAt(idx);
        }
        return DiscardCards(discarderId, picked, batchSizeOverride > 0 ? batchSizeOverride : total);
    }

    // ==================== 【P2】封印（Seal） ====================
    /// <summary>
    /// 【P2·封印唯一出口】把 `sealing` 封印到 `host` 上（沿用 PM 调试命令确立的既有机制）：
    ///  ① 双向引用：`sealing.SealedBy = host`、`host.SealedCards.Add(sealing)`；
    ///  ② 标记：`sealing.IsSeal = true`（本卡是封印卡）、`host.IsSealed = true`（本卡被封印）；
    ///  ③ 区域：封印卡移入 `SealedZone`（不进争夺区桶），`ZoneDetailId` = 宿主所在地区；
    ///  ④ 触发：广播 `OnSealed`；
    ///  ⑤ 连带：宿主离场时封印卡回 OWNER 手牌（`ZoneManager.DetachSealedCards` 已实装）。
    /// </summary>
    /// <param name="faceDown">是否"背面朝上"封印（JC119：你的对手不能检视该牌）。会进入快照投影做信息隔离。</param>
    /// <returns>是否成功。</returns>
    public bool SealCard(CardInstance sealing, CardInstance host, bool faceDown = false)
    {
        if (sealing == null || host == null || sealing == host) return false;
        if (sealing.SealedBy != null) { GD.Print($"[TurnManager] 封印失败：{sealing.UID} 已被封印"); return false; }

        sealing.SealedBy = host;
        sealing.IsSeal = true;
        sealing.IsSealed = false;             // IsSealed 标在【宿主】上
        sealing.IsSealedFaceDown = faceDown;  // 信息隔离：背面朝上的封印牌对对手不可见
        if (host.SealedCards == null) host.SealedCards = new List<CardInstance>();
        host.SealedCards.Add(sealing);
        host.IsSealed = true;

        string areaUid = host.ZoneDetailId ?? "";
        _zoneManager?.MoveToSealedZone(sealing, areaUid, sealing.ControllerId >= 1 ? sealing.ControllerId : 1);
        // 【触发语义】封印时机：条件满足时封印卡仍在原处（此处按"封印动作发生"广播，入堆叠后不再看区）
        _triggerEmitPoints?.OnSealed(sealing, host, sealing.ControllerId, areaUid);
        // 【P3·F】封印落地 → 重判"按计数缩放的持续授予"（JC109「每有一张牌被封印在其上 → 一个黑刀」）
        EE?.ReevaluatePrereqs(host.ControllerId);
        GD.Print($"[TurnManager] 🔒 封印：{sealing.PlayerTemplate?.CardName} → {host.PlayerTemplate?.CardName}（地区={areaUid}，背面={faceDown}）");
        return true;
    }

    // ==================== 【P2】洗牌（Shuffle） ====================
    /// <summary>【P2】洗某玩家的牌库（走 DeckManager 注入的可复现随机源，联机双端一致 / 重连可复原）。</summary>
    public void ShuffleDeckOf(int playerId)
    {
        if (_deckManager == null) return;
        if (playerId == 1) _deckManager.ShufflePlayer1();
        else _deckManager.ShufflePlayer2();
        GD.Print($"[TurnManager] 🔀 玩家{playerId} 牌库已洗牌（剩余 {_deckManager.GetPlayer1DeckCount()} / {_deckManager.GetPlayer2DeckCount()}）");
    }

    /// <summary>【P2】取某玩家牌库（只读副本，供检索筛选用）。</summary>
    public List<CardInstance> GetDeckOf(int playerId)
        => _deckManager == null
            ? new List<CardInstance>()
            : (playerId == 1 ? _deckManager.GetPlayer1DeckData() : _deckManager.GetPlayer2DeckData());

    /// <summary>【P2】取牌库顶 N 张（只读，不取出；供"自牌库顶 X 张中寻找"）。</summary>
    public List<CardInstance> PeekDeckOf(int playerId, int count)
        => _deckManager == null
            ? new List<CardInstance>()
            : (playerId == 1 ? _deckManager.PeekPlayer1(count) : _deckManager.PeekPlayer2(count));

    /// <summary>【P2】把一张牌从其所在玩家的牌库中移出（检索取牌/牌库顶封印用），随后由调用方决定去向。
    /// 说明：`DeckManager.RemoveFromDeck` 按 `OwnerId` 定位牌库列表，故此处传入的 playerId 仅用于**校验**
    /// （发现"卡不在该玩家牌库"时打警告，避免检索把别处的卡误当牌库牌移出）。</summary>
    public void RemoveCardFromDeckOf(int playerId, CardInstance card)
    {
        if (_deckManager == null || card == null) return;
        if (!GetDeckOf(playerId).Contains(card))
        {
            GD.Print($"[TurnManager] ⚠ RemoveCardFromDeckOf：{card.PlayerTemplate?.CardName} 不在玩家{playerId} 牌库中（OwnerId={card.OwnerId}），按 OwnerId 移除");
        }
        _deckManager.RemoveFromDeck(card);   // 从牌库数据层移除（不改变 CurrentZone/区域桶，交由去向方法处理）
    }

    /// <summary>注入对抗结算管理器（由 GameInitializer 调用）</summary>
    public void SetCombatManager(CombatManager combatManager)
    {
        _combatManager = combatManager;
    }

    /// <summary>
    /// 【K26】"分配战斗伤害阶段"是否正在进行（＝战斗对抗伤害分配窗口打开期间，既有单机也有联机路径）。
    /// 由 `CombatManager.IsDamageAssignmentOpen` 提供；`PhaseGateRule` 与各门禁读此属性，
    /// 从而把"不能在该阶段使用"收窄到**窗口打开的那一小段**（用户 2026-09 口径）。
    /// </summary>
    public bool IsDamageAssignmentOpen => _combatManager != null && _combatManager.IsDamageAssignmentOpen;

    /// <summary>注入堆叠管理器（由 GameInitializer 调用），用于打出卡入栈与响应窗口结算</summary>
    public void SetStackManager(StackManager stackManager)
    {
        _stackManager = stackManager;
    }

    /// <summary>注入地区管理器（由 GameInitializer 调用），用于赢取地区检查与结算</summary>
    public void SetAreaManager(AreaManager areaManager)
    {
        _areaManager = areaManager;
    }

    /// <summary>注入区域管理器（由 GameInitializer 调用），用于基础行动收口时驱动 ZoneManager 移动</summary>
    public void SetZoneManager(ZoneManager zoneManager)
    {
        _zoneManager = zoneManager;
        // 卡牌移动后先手加成可能变化，由 TurnManager 主动重算并通知 UI，消除 View 反向驱动 Controller
        _zoneManager.OnCardMoved += (_, _, _, _) => RefreshFirstStrikeStats();
        // 胜利判定收口（不经由堆叠，即时结算）：任何卡进入分数区即检测总分是否达阈值。
        // 分数区增减均经 OnPostMove 统一出口，覆盖所有移动路径（打牌/PM命令/结算/地区赢取）。
        _zoneManager.OnPostMove += OnCardMovedForVictoryCheck;
    }

    /// <summary>分数区变动后的即时胜利判定：卡进入分数区 → CheckGameOver（命中即 TriggerGameOver，丢弃未结算堆叠）。</summary>
    private void OnCardMovedForVictoryCheck(CardInstance card, CardZone zone, int playerId, string detailId)
    {
        if (card == null || zone != CardZone.ScoreZone) return;
        if (_playerManager == null || _playerManager.IsGameOver) return;
        _playerManager.CheckGameOver();   // 内部命中胜者会立即 TriggerGameOver（清堆叠+广播）
    }

    // ============================================================
    // 核心方法：Reset / StartGame / NextTurn
    // ============================================================

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 调用：将全部对局状态复位到"未开局"初始值。
    /// 纯复位、不触发任何事件；真正开局由后续 GameInitializer.StartMatch → StartGame 完成。
    /// </summary>
    public void Reset()
    {
        TurnCount = 0;
        CurrentPhase = TurnPhase.StartReset;
        FirstPlayerId = 1;
        PriorityPlayerId = 0;
        ResetPassWindow();
        _combatAreaUids.Clear();
        _combatAreaIndex = 0;
        _projectedCombatAreaNo = 0;     // 【联机投影】未开局无对抗地区
        _extraPowerDoneAreas.Clear();   // 【P3·K18】新回合：追加势力对抗的去重集合复位
        _atomicLock = false;
        _openModalCount = 0;
        _decisionHoldDepth = 0;   // 【2026-09】决策持锁随换局/复位一并归零（杜绝跨局泄漏）
        GD.Print("[TurnManager] Reset: 回合状态已复位到未开局");
    }

    public void StartGame()
    {
        TurnCount = 1;
        // 先手兜底：正常流程里先手已在"第0回合调度完成后"随机产生（或单机 Reset 后默认为 P1），
        // 此处只在"未产生"（如跳过调度直接开局的异常路径）时补一次随机，避免把 0 带进第一回合。
        if (!HasFirstPlayer) FirstPlayerId = RollFirstPlayer();
        ResetPassWindow();
        _combatAreaUids.Clear();
        _combatAreaIndex = 0;
        _extraPowerDoneAreas.Clear();   // 【P3·K18】新回合：追加势力对抗的去重集合复位
        _playerManager?.SetFirstPlayer(FirstPlayerId);
        // MVC：回合级规则（重置基础行动次数/破平特权）归属 Controller（TurnManager），
        // 在广播 OnTurnStarted 前完成，避免 View 收到事件后再反向驱动 Model。
        _playerManager?.ResetActionCharges(1);
        _playerManager?.ResetActionCharges(2);
        GD.Print($"=== 游戏开始，回合 {TurnCount}，先手玩家: {FirstPlayerId} ===");
        OnTurnStarted?.Invoke(TurnCount);
        EnterPhase(TurnPhase.StartReset);
    }

    public void NextTurn()
    {
        TurnCount++;
        FirstPlayerId = FirstPlayerId == 1 ? 2 : 1;
        ResetPassWindow();
        _combatAreaUids.Clear();
        _combatAreaIndex = 0;
        _extraPowerDoneAreas.Clear();   // 【P3·K18】新回合：追加势力对抗的去重集合复位
        _playerManager?.SetFirstPlayer(FirstPlayerId);
        // MVC：回合级规则（重置基础行动次数/破平特权）归属 Controller（TurnManager），
        // 在广播 OnTurnStarted 前完成，避免 View 收到事件后再反向驱动 Model。
        _playerManager?.ResetActionCharges(1);
        _playerManager?.ResetActionCharges(2);
        GD.Print($"=== 开始回合 {TurnCount}，先手玩家: {FirstPlayerId} ===");
        OnTurnStarted?.Invoke(TurnCount);
        EnterPhase(TurnPhase.StartReset);
    }

    // ============================================================
    // 【开局·调度步骤 = 第 0 回合】（联机）
    // ------------------------------------------------------------
    // 定位（用户 2026-10 口径 · 最终版）：调度是**回合流程的一个真实步骤**——
    //   起手手牌已发 → **TurnPhase.Mulligan（第 0 回合，TurnCount=0）** → 双方上报选择 → StartGame(TurnCount=1, Reset)。
    // 【为什么不主动下发决策请求】客户端收到 `room.Phase=InMatch` 后才 `GoToStateDeferred` **延迟一帧**切到战场页，
    // 服务端若在该广播后立刻 `RequestChoice`，请求会落在"客户端尚未订阅决策请求"的空档里而被兜底取消
    // （历史现象：不弹调度框、直接跳到第一回合）。
    // 因此第 0 回合改为**客户端驱动（上行）**：客户端本地弹调度框，玩家点"确认调度"时**主动上报**选择，
    // 上报那一刻客户端必然已就绪 ⇒ 无竞态，也不需要就绪信号 / 重发 / 超时兜底。
    //   注：发起手牌本身不需要此机制——快照可随时补帧（`LastSnapshot`），故第 1 回合的启动时机原样不动。
    // 结算口径：双方都上报后，把**高亮（所选）手牌**放到牌库底，再从**牌库顶**抽回等量张，最后洗牌。
    // 说明：仅联机（InputGateway 注入）启用；单机由 StartMatch 直接 StartGame，不走本步骤。
    // 【先手产生时机（用户 2026-10 口径）】调度期间**不产生先手玩家**：
    //   - 进入第 0 回合即把 `FirstPlayerId` 置 0（`HasFirstPlayer=false`），UI 显示"先手: 未产生"；
    //   - 双方上报齐 → 结算置底/顶抽/洗牌 → **仍在第 0 回合（TurnCount=0）**随机产生先手玩家；
    //   - 之后才离开第 0 回合并 `StartGame()`，先手结果随 `OnAsyncSettlementCompleted` 快照下发。
    // ============================================================

    /// <summary>是否处于第 0 回合（开局调度步骤）：供 UI 显示"调度步骤" / 冻结常规交互。</summary>
    public bool IsMulliganPhase => CurrentPhase == TurnPhase.Mulligan;

    /// <summary>调度步骤完成事件（供服务端广播快照、UI 收起提示框）。</summary>
    public event Action OnMulliganCompleted;

    /// <summary>已上报调度选择的玩家（1/2）；双方齐了才统一结算。</summary>
    private readonly System.Collections.Generic.HashSet<int> _mulliganReported
        = new System.Collections.Generic.HashSet<int>();
    /// <summary>各玩家上报的"要换掉"的手牌 UID（已权威复检）。</summary>
    private readonly System.Collections.Generic.Dictionary<int, List<string>> _mulliganPicks
        = new System.Collections.Generic.Dictionary<int, List<string>>();

    /// <summary>
    /// 【先手产生】随机掷出先手玩家（1/2）。随机源沿用注入的 `_rng`（可复现，禁止 new Random()）；
    /// 未注入随机源（部分测试/裸实例）时回退 P1，保持既有确定性。
    /// </summary>
    private int RollFirstPlayer() => _rng != null ? (_rng.Next(2) == 0 ? 1 : 2) : 1;

    /// <summary>把当前先手写入 PlayerManager 的"先手标志"（0 = 未产生，双方均无先手加成）。</summary>
    private void SyncFirstPlayerFlag() => _playerManager?.SetFirstPlayer(FirstPlayerId);

    /// <summary>
    /// 【第 0 回合·调度步骤】联机入口（客户端驱动）：先把阶段置为第 0 回合并下发一份快照，
    /// 之后**只等客户端主动上报**（见 `ReportMulligan`），双方齐了才结算并 `StartGame()` 进入第一回合。
    /// 幂等保护：重复调用（重连 / 多处触发）若已在第 0 回合则直接返回。
    /// </summary>
    public void BeginMulligan()
    {
        if (CurrentPhase == TurnPhase.Mulligan)
        {
            GD.Print("[TurnManager] 第0回合·调度步骤已在进行中，忽略重复调用");
            return;
        }
        if (InputGateway == null || _cardManager == null)
        {
            GD.Print("[TurnManager] 调度步骤跳过（无 InputGateway / CardManager）：直接开始第一回合");
            StartGame();
            return;
        }

        // 进入第 0 回合：仅此一步，**不涉及优先权转换**（无人持有行动权）
        TurnCount = 0;
        CurrentPhase = TurnPhase.Mulligan;
        PriorityPlayerId = 0;
        // 【先手未产生】第 0 回合调度期间不分先后手：UI 显示"先手: 未产生"，
        // 先手玩家在双方上报完成后（仍在第 0 回合）随机产生。
        FirstPlayerId = 0;
        SyncFirstPlayerFlag();
        ResetPassWindow();
        _mulliganReported.Clear();
        _mulliganPicks.Clear();
        OnPhaseChanged?.Invoke(CurrentPhase);
        GD.Print("=== [TurnManager] 第0回合·调度步骤开始（未产生先手玩家，等待双方主动上报调度选择）===");
        OnAsyncSettlementCompleted?.Invoke();   // 服务端据此下发一份「第0回合」快照，客户端据此弹调度框
    }

    /// <summary>
    /// 【客户端驱动】接收某玩家的调度上报：`pickedUids` 为**要换掉**的手牌 UID（空 = 不调度）。
    /// 服务端按该玩家**当前真实手牌**权威复检（不在手牌里的 UID 一律丢弃）。
    /// 双方都上报后自动结算 + 进入第一回合；重复上报（重连重发）幂等忽略。
    /// 结算口径：各自把选中的牌放回**牌库底**，再抽等量牌，最后洗牌。
    /// </summary>
    public void ReportMulligan(int playerId, List<string> pickedUids)
    {
        if (CurrentPhase != TurnPhase.Mulligan || playerId != 1 && playerId != 2) return;
        if (!_mulliganReported.Add(playerId)) return;   // 幂等：已上报过则忽略

        var hand = GetHandOf(playerId);
        var allowed = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var c in hand) if (c != null) allowed.Add(c.UID);

        var picked = new List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        if (pickedUids != null)
            foreach (var uid in pickedUids)
            {
                if (string.IsNullOrEmpty(uid) || !allowed.Contains(uid)) continue;   // 权威复检
                if (!seen.Add(uid)) continue;
                picked.Add(uid);
            }

        _mulliganPicks[playerId] = picked;
        GD.Print($"[TurnManager] 玩家{playerId} 调度上报 {picked.Count} 张（手牌共 {hand.Count} 张），"
            + $"已上报 {_mulliganReported.Count}/2");

        if (_mulliganReported.Count < 2) return;   // 等另一位

        // 双方齐 → 统一结算（先取出全部 UID 再动牌库，避免边遍历边改动）
        foreach (var kv in _mulliganPicks) ApplyMulligan(kv.Key, kv.Value);

        // 【先手产生】双方完成调度后、**依然在第 0 回合（TurnCount=0）**随机产生先手玩家。
        // 必须早于 `OnAsyncSettlementCompleted`（服务端据此下发快照），否则客户端首份快照里先手仍是"未产生"。
        FirstPlayerId = RollFirstPlayer();
        SyncFirstPlayerFlag();
        GD.Print($"🎲 [TurnManager] 第0回合：双方调度完成，随机产生先手玩家 → P{FirstPlayerId}");

        CurrentPhase = TurnPhase.StartReset;   // 离开第 0 回合（StartGame 会置 TurnCount=1 并进入重置步骤）
        GD.Print("=== [TurnManager] 调度步骤完成，开始第一回合 ===");
        OnMulliganCompleted?.Invoke();
        OnAsyncSettlementCompleted?.Invoke();
        StartGame();
    }

    /// <summary>
    /// 结算某玩家的调度：所选手牌 → 牌库底；再从牌库顶抽等量张；最后洗牌。
    /// 逐张经 `CardManager` 收口，保证手牌列表 / CurrentZone / 区域桶一致。
    /// </summary>
    private void ApplyMulligan(int playerId, List<string> pickedUids)
    {
        if (pickedUids == null || pickedUids.Count == 0)
        {
            GD.Print($"[TurnManager] 玩家{playerId} 未调度（0 张）");
            return;
        }

        var picked = new List<CardInstance>();
        foreach (var uid in pickedUids)
        {
            var inst = _cardManager.GetInstance(uid);
            if (inst == null) continue;
            if (inst.CurrentZone != CardZone.Hand) continue;   // 必须在手牌
            if (inst.OwnerId != playerId) continue;            // 必须是自己的手牌
            picked.Add(inst);
        }
        if (picked.Count == 0) return;

        // ① 所选手牌 → 牌库底（逐张经 CardManager 收口）
        foreach (var c in picked)
            _cardManager.MoveCardToDeckBottom(c, playerId);

        // ② 从牌库顶抽等量张（此时牌库顶＝未被换掉的原牌库顶）
        for (int i = 0; i < picked.Count; i++)
            DrawCardsForPlayer(playerId, 1);

        // ③ 洗牌
        ShuffleDeckOf(playerId);

        GD.Print($"[TurnManager] 玩家{playerId} 调度结算完成：{picked.Count} 张置底 → 顶抽 {picked.Count} 张 → 洗牌");
    }

    // ============================================================
    // 玩家操作：Pass / PlayCard
    // ============================================================

    /// <summary>
    /// 玩家让过。
    /// 【显式优先权状态机（用户 2026-09 口径）】
    ///  · 每个步骤存在一位**天生优先玩家** A（＝`GetPriorityPlayer()`：先手；ActionSecond 内为后手）；
    ///  · 步骤开始 / 有东西入堆叠 / 堆叠结算后，行动权一律归 A，并复位让过窗口；
    ///  · A 让过 → 行动权**显式**交给另一方 B（不再盲目翻转 1/2）；
    ///  · B 让过 → 双方都已让过 → 直接推进（堆叠非空：结算栈顶；堆叠为空：推进下一步骤）。
    ///  · 第 0 回合（调度步骤）不涉及优先权转换：`PriorityPlayerId` 为 0，双方都无法让过。
    /// </summary>
    public void Pass(int playerId)
    {
        // 规则层原子锁（核心需求 7.3）：原子操作（含触发排序/选目标窗口）期间不给响应权
        if (_atomicLock)
        {
            GD.Print($"⛔ [TurnManager] 原子操作进行中（{_atomicLock}），拒绝玩家{playerId}让过");
            return;
        }

        // 堆叠非空时：响应窗口独立于阶段，任何阶段都允许当前拥有行动权的玩家让过
        if (_stackManager != null && !_stackManager.IsEmpty)
        {
            if (PriorityPlayerId != playerId)
            {
                GD.Print($"❌ [TurnManager] 当前行动权在玩家{PriorityPlayerId}，玩家{playerId}不能让过");
                return;
            }

            _passedPlayers.Add(playerId);
            _passCount = _passedPlayers.Count;
            GD.Print($"🟢 [TurnManager] 玩家{playerId} 让过 (本窗口已让过: {string.Join("/", _passedPlayers)}) [堆叠非空，阶段={CurrentPhase}]");

            if (_passedPlayers.Count >= 2)
            {
                ResetPassWindow();
                ResolveStackTop();   // 结算顶部条目（含结构化执行）

                // 注：对抗三步骤（调查/战斗/势力）现在各自独立成阶段，结算完毕后统一进入
                // "分配优先权 → 等待双方连续让过 → 推进" 流程，因此不再需要 pending 强制推进分支。

                // 堆叠结算后：行动权归"当前步骤优先获得行动权的玩家"（与谁打出无关）
                PriorityPlayerId = GetPriorityPlayer();
                GD.Print($"⏳ [TurnManager] 堆叠结算后行动权给优先玩家{PriorityPlayerId}");
                OnPriorityChanged?.Invoke(PriorityPlayerId);
            }
            else
            {
                // 让过者让出行动权 → 交给**另一位**玩家（A 让过 → B 获得行动权）
                PriorityPlayerId = OtherPlayerId(playerId);
                GD.Print($"⏳ [TurnManager] 行动权转移给玩家{PriorityPlayerId}（堆叠非空，待响应）");
                OnPriorityChanged?.Invoke(PriorityPlayerId);
            }
            return;
        }

        // 堆叠为空时：任何阶段都允许当前拥有优先权的玩家让过（规则 8：任何步骤开始当前玩家获得优先权）
        if (PriorityPlayerId != playerId)
        {
            GD.Print($"❌ [TurnManager] 当前行动权在玩家{PriorityPlayerId}，玩家{playerId}不能让过");
            return;
        }

        _passedPlayers.Add(playerId);
        _passCount = _passedPlayers.Count;
        GD.Print($"🟢 [TurnManager] 玩家{playerId} 让过 (本窗口已让过: {string.Join("/", _passedPlayers)}) [阶段={CurrentPhase}]");

        // 堆叠为空：双方都让过则推进阶段
        if (_passedPlayers.Count >= 2)
        {
            ResetPassWindow();
            // 注：不再对外广播"玩家0"作为阶段切换过渡态。原因：OnPriorityChanged 的订阅者（Arena UI）
            // 为同步回调，若先 Invoke(0) 再 AdvancePhase，订阅者会在 CurrentPhase 仍为旧阶段、
            // PriorityPlayerId=0 的窗口内同步尝试 Pass/行动，触发"行动权在玩家0"误报。
            // 直接 AdvancePhase → EnterPhase → DoXxx 内部以有效玩家(1/2) 复位并发出 OnPriorityChanged，
            // UI 不经历玩家0瞬间，彻底避免下游误触发。
            // 恢复步骤：双方让过且堆叠空 → 先过"手牌上限检查点"再推进
            if (CurrentPhase == TurnPhase.EndRecover && !RecoverCanLeave())
            {
                RunRecoverHandLimitAsync();
                return;
            }
            GD.Print("⏩ [TurnManager] 双方都让过，推进阶段");
            AdvancePhase();
        }
        else
        {
            // 让过者让出行动权 → 交给**另一位**玩家（A 让过 → B 获得行动权）
            PriorityPlayerId = OtherPlayerId(playerId);
            GD.Print($"⏳ [TurnManager] 行动权转移给玩家{PriorityPlayerId}");
            OnPriorityChanged?.Invoke(PriorityPlayerId);
        }
    }

    /// <summary>
    /// 结算堆叠顶部条目：弹出并依据结构化 ResolveKind 执行实际效果。
    /// 预留终止逻辑：若条目被"终止"（如反制），则由 OnResolve 失败分支直接送墓地（当前仅正常路径）。
    /// </summary>
    private void ResolveStackTop()
    {
        if (_stackManager == null || _stackManager.IsEmpty) return;
        EnterAtomic();   // 结算为原子操作；若随后触发窗口打开，Arena 的 NotifyModalOpened 维持锁
        var entry = _stackManager.ResolveTop();
        if (entry == null) { ExitAtomicWhenIdle(); return; }

        // 优先执行可选钩子（预留接口）
        if (entry.OnResolve != null)
        {
            entry.OnResolve.Invoke();
            return;
        }

        ResolveEntry(entry);

        // 结算后若堆叠清空，立即检查是否有基础地区满足赢取条件（规则：堆叠为空时触发）
        if (_stackManager != null && _stackManager.IsEmpty && _areaManager != null)
            _areaManager.TryStartAreaWin(_stackManager);

        // 同步段结束：若无模态窗口保持打开，则原子操作已完整结束，解除规则层锁
        ExitAtomicWhenIdle();

        // 【JC119】效果请求的**免费打出**（"视作快速、不需付费"）在**本次结算完全结束、原子锁解除之后**
        // 才走**正常打出流程**（入堆叠/优先级窗口照常）——避免在结算期内嵌套打出。
        ProcessQueuedFreePlays();
    }

    /// <summary>
    /// 【JC119】免费打出队列：效果（守秘人的死亡触发"展示其上封印的牌并打出其中一张"）在结算期只能**登记**；
    /// 由 `ResolveStackTop` 在**原子锁解除后**逐条走 `PlayCard(freePlay:true, asIfQuick:true)`（＝正常打出流程）。
    /// 每次只处理一条（打出后入堆叠，其结算结束时再继续）。
    /// </summary>
    private readonly System.Collections.Generic.List<(int PlayerId, string CardUid)> _queuedFreePlays
        = new System.Collections.Generic.List<(int, string)>();

    /// <summary>【JC119】该卡是否已登记"免费打出"（防重复登记）。</summary>
    public bool HasQueuedFreePlay(string cardUid)
    {
        foreach (var (_, uid) in _queuedFreePlays)
            if (string.Equals(uid, cardUid, System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>【JC119】登记一次"免费打出"（由效果在结算期调用；实际打出在原子锁解除后）。</summary>
    public void EnqueueFreePlay(int playerId, string cardUid)
    {
        if (playerId <= 0 || string.IsNullOrEmpty(cardUid)) return;
        _queuedFreePlays.Add((playerId, cardUid));
        GD.Print($"[TurnManager] 🆓 登记免费打出：玩家{playerId} 的 {cardUid}（原子锁解除后按正常打出流程执行）");
    }

    /// <summary>处理免费打出队列（每次处理队首一条；打出与否都会出队，避免死循环）。
    /// 由 `ResolveStackTop` 在原子锁解除后调用；**亦为测试/调试的驱动入口**（队列本身只在结算期登记，无法从外部触发）。</summary>
    public void ProcessQueuedFreePlays()
    {
        while (_queuedFreePlays.Count > 0)
        {
            var (pid, uid) = _queuedFreePlays[0];
            _queuedFreePlays.RemoveAt(0);
            var inst = _cardManager?.GetInstance(uid);
            if (inst == null) { GD.Print($"[TurnManager] ⚠ 免费打出跳过：找不到卡 {uid}"); continue; }
            if (inst.CurrentZone != CardZone.Hand)
            {
                GD.Print($"[TurnManager] ⚠ 免费打出跳过：{inst.PlayerTemplate?.CardName} 已不在手牌（{inst.CurrentZone}）");
                continue;
            }
            // 【JC119·目标选择窗】需要选目标的牌：这类牌的打出**不是玩家拖拽发起的**，没有"拖到目标上"的交互，
            // 故由本方法弹一次目标选择窗（`IGameUIHooks.PromptPlayTarget`）取目标后再走正常打出流程。
            if (inst.PlayerTemplate?.GetRequiresTarget() == true)
            {
                BeginFreePlayTargetChoice(pid, uid, inst);
                return;   // 选择是异步的（等待玩家点选/取消）；取消或选定后在回调里继续处理队列
            }
            PlayCard(pid, uid, null, null, null, -1, null, null, freePlay: true, asIfQuick: true);
            return;   // 单条打出即可（打出后入堆叠，其结算结束时再进入本方法继续处理）
        }
    }

    /// <summary>
    /// 【JC119·K28·目标选择窗】为"效果免费打出、但需要选目标"的牌取目标，然后按正常打出流程打出。
    ///
    /// 决策路径（与引擎既有口径完全一致）：
    ///  · **单机**（`EE.UIHooks != null`）→ `IGameUIHooks.PromptPlayTarget` 弹**目标选择窗**
    ///    （卡目标＝场上高亮 + 点击；地区目标＝地区槽高亮 + 点击；实现见 `Arena.PromptPlayTarget`）；
    ///  · **联机**（`EE.InputGateway != null`，服务端无 View）→ 经 `RequestFreePlayTargetAsync` 下发
    ///    `ChoiceRequest.Kind.SelectTarget` 回环（回包权威复检；取消/超时 → 留在手牌），
    ///    **等待回包期间持原子锁**（`HoldAtomicDuringDecisionAsync`，见 §7.3.1）；
    ///  · **两者皆无**（纯 headless 规则内核/测试）→ **确定性兜底取第一个候选**（与触发路径"无 UI 取首候选"同口径）；
    ///  · **无任何候选** → 留在手牌并告警（fail-closed，绝不静默代打）。
    ///
    /// 候选枚举：卡目标复用 `GetLegalPlayTargetUids`（与 `LegalityProjection` 同一候选池 + 同一判定函数
    /// `CardManager.IsLegalPlayTarget`，不新增第二套判定）；地区目标＝卡声明的可打出目标含 `Area` 时的全部地区
    /// （与 `PlayCard` 对地区目标自身的权威校验"地区存在"同口径）。
    /// </summary>
    private void BeginFreePlayTargetChoice(int playerId, string cardUid, CardInstance inst)
    {
        var tmpl = inst.PlayerTemplate;
        string cardName = tmpl?.CardName ?? cardUid;

        var cardCands = GetLegalPlayTargetUids(inst);
        bool areaAllowed = tmpl?.GetAllowedTargets()?.Contains(TargetKind.Area) == true;
        bool isArea = cardCands.Count == 0 && areaAllowed;
        var cands = isArea ? GetOrderedAreaUidsForPlay() : cardCands;

        if (cands.Count == 0)
        {
            GD.PrintErr($"[TurnManager] ⚠ 免费打出跳过：{cardName} 需要选目标但**无任何合法目标** → 留在手牌");
            ProcessQueuedFreePlays();   // 继续处理队列里的下一条
            return;
        }

        // 成功打出后不再继续处理队列：新条目已入堆叠，其结算结束时（ResolveStackTop 末尾）会再次进入本方法。
        System.Action<string> finish = pickedUid =>
        {
            bool played = false;
            if (!string.IsNullOrEmpty(pickedUid))
            {
                string targetUid = isArea ? pickedUid : null;
                string targetCardUid = isArea ? null : pickedUid;
                played = PlayCard(playerId, cardUid, targetUid, targetCardUid, null, -1, null, null,
                    freePlay: true, asIfQuick: true);
                if (!played)
                    GD.PrintErr($"[TurnManager] ⚠ 免费打出失败（目标已失效或被门禁拒绝）：{cardName} → 留在手牌");
            }
            else
            {
                GD.Print($"[TurnManager] 免费打出：玩家{playerId} 未选择目标（取消）→ {cardName} 留在手牌");
            }
            if (!played) ProcessQueuedFreePlays();   // 没打成 → 继续处理队列里的下一条
        };

        if (EE?.InputGateway != null)
        {
            // 【P2·联机断链修复·JC119】联机：把"免费打出的目标"**回环给该玩家**（`SelectTarget`，
            // 卡目标/地区目标同一请求类型——地区 uid 也可以作为候选下发；客户端走既有选目标窗口）。
            // 回包**权威复检**必须命中服务端下发的候选；取消/超时 → 不打（留在手牌）。
            string prompt = $"请为「{cardName}」选择{(isArea ? "目标地区" : "目标")}（由效果免费打出）";
            GD.Print($"[TurnManager] 🆓 免费打出需选目标（联机）：{cardName}（候选 {cands.Count} 个，isArea={isArea}）→ 经网关请求");
            _ = RequestFreePlayTargetAsync(playerId, cardName, isArea, cands, prompt, cardUid, finish);
            return;
        }
        if (EE?.UIHooks != null)
        {
            string prompt = $"请为「{cardName}」选择{(isArea ? "目标地区" : "目标")}（由效果免费打出）";
            GD.Print($"[TurnManager] 🆓 免费打出需选目标：{cardName}（候选 {cands.Count} 个，isArea={isArea}）→ 弹目标选择窗");
            EE.UIHooks.PromptPlayTarget(playerId, prompt, isArea, cands, finish);
            return;
        }
        // 无 UI（headless 测试 / 纯规则内核）：确定性兜底取第一个候选（与触发路径同一兜底口径）
        GD.Print($"[TurnManager] 🆓 免费打出需选目标：{cardName} → 无 UI，确定性兜底取首个候选 {cands[0]}");
        finish(cands[0]);
    }

    /// <summary>
    /// 【P2·联机】激活的"选择类"入参**由服务端驱动收集**（与单机 View 侧那几套窗 **1:1**）：
    /// 顺序与单机 `Arena.BeginActivate` 完全一致 —— **目标地区 → 牺牲代价 → 标志代价 → 目标玩家**，
    /// 每步候选都由**服务端**用与权威侧同源的规则函数算出（`EffectEngine.GetLegalAreaTargets` /
    /// `GetSacrificeCandidates` / `GetCounterCostCandidates` / `PlayerTargetPool.Collect`），随
    /// `ChoiceRequest` 下发；回包一律**权威复检**（必须命中候选），取消/超时/非法 → **整次激活不发动**
    /// （不付任何代价）。收集完毕后在**释放原子锁之后**调 `EE.ActivateAbility`（其自身会 EnterAtomic）。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> ActivateAbilityWithChoicesAsync(int playerId, string sourceUid,
        string effectId, string targetUid, int targetPlayerId)
    {
        if (InputGateway == null || EE == null || _cardManager == null) return false;
        var source = _cardManager.GetInstance(sourceUid);
        var ability = FindActivationAbilityFor(source, effectId);
        if (source == null || ability == null) return false;
        var target = string.IsNullOrEmpty(targetUid) ? null : _cardManager.GetInstance(targetUid);

        string sacrificeUid = null;
        string targetAreaId = null;
        string counterCostUid = null;
        System.Collections.Generic.Dictionary<string, int> allocation = null;
        int stackCountBefore = _stackManager?.Entries?.Count ?? -1;   // 用于判定"本次激活是否真的入堆叠"

        EnterAtomic();
        try
        {
            // ---------- ① 目标地区（单机 `Arena.BeginActivateAreaSelection`）----------
            if (ability.NeedsAreaTarget)
            {
                var areas = EE.GetLegalAreaTargets(source, ability, target);
                if (areas.Count == 0)
                {
                    GD.PrintErr($"[K15·联机] {ability.EffectId} 无合法目标地区 → 不发动");
                    return false;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
                    SourceUid = source.UID,
                    Prompt = $"请为「{source.PlayerTemplate?.CardName ?? "激活效果"}」选择目标地区",
                    ForceChoice = true,
                    OptionUids = areas,
                };
                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled || string.IsNullOrEmpty(res.SelectedUid) || !areas.Contains(res.SelectedUid))
                {
                    GD.Print($"[K15·联机] {ability.EffectId} 未选定/非法目标地区 → 不发动（fail-closed）");
                    return false;
                }
                targetAreaId = res.SelectedUid;
            }

            // ---------- ② 牺牲代价（单机 `Arena.BeginActivateWithSacrifice`：按钮列表选一个）----------
            if (ability.SacrificeCost != null)
            {
                var cands = EE.GetSacrificeCandidates(source, ability.SacrificeCost)
                            ?? new System.Collections.Generic.List<CardInstance>();
                if (cands.Count == 0)
                {
                    GD.PrintErr($"[K22·联机] {ability.EffectId} 牺牲代价无候选 → 不发动");
                    return false;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectActivationCost,
                    SourceUid = source.UID,
                    Prompt = "选择要牺牲的角色",
                    ForceChoice = true,
                    Amount = 1,   // 语义：从候选里选 1 张（**单张模式**；逐卡分配模式以 `OptionMaxAmounts` 非空区分）
                };
                foreach (var c in cands)
                {
                    if (c == null) continue;
                    req.OptionUids.Add(c.UID);
                    req.OptionLabels.Add($"牺牲 {c.PlayerTemplate?.CardName ?? c.UID}");
                }
                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled || string.IsNullOrEmpty(res.SelectedUid)
                    || !req.OptionUids.Contains(res.SelectedUid))
                {
                    GD.Print($"[K22·联机] {ability.EffectId} 未选定/非法牺牲候选 → 不发动（fail-closed）");
                    return false;
                }
                sacrificeUid = res.SelectedUid;
            }

            // ---------- ③ 标志代价（单机 `BeginActivateWithCounterCostChoice` / `...Allocation`）----------
            if (ability.CounterCost != null)
            {
                var cands = EE.GetCounterCostCandidates(source, ability.CounterCost)
                            ?? new System.Collections.Generic.List<CardInstance>();
                if (cands.Count == 0)
                {
                    GD.PrintErr($"[K5·联机] {ability.EffectId} 标志代价无候选 → 不发动");
                    return false;
                }
                bool byChoice = ability.CounterCost.AmountByPlayerChoice;
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectActivationCost,
                    SourceUid = source.UID,
                    Prompt = byChoice ? "选择要移除标志的卡（每张可多次点击累加，至少 1 个）" : "选择要移除标志的卡",
                    ForceChoice = true,
                    Amount = byChoice ? 1 : ability.CounterCost.Amount,   // 逐卡分配时仅作"至少 1"提示（模式以 OptionMaxAmounts 非空区分）
                };
                foreach (var c in cands)
                {
                    if (c == null) continue;
                    int have = EE.GetMarkerCountOnCard(c, ability.CounterCost);
                    req.OptionUids.Add(c.UID);
                    req.OptionMaxAmounts.Add(have);   // 逐卡分配模式：客户端据此显示"已选 X / 共 N"
                    req.OptionLabels.Add(byChoice
                        ? $"{c.PlayerTemplate?.CardName ?? c.UID}：移除 1 个（共 {have}）"
                        : $"从 {c.PlayerTemplate?.CardName ?? c.UID} 上移除 {ability.CounterCost.Amount} 个标志");
                }
                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled)
                {
                    GD.Print($"[K5·联机] {ability.EffectId} 未选定标志代价 → 不发动");
                    return false;
                }
                if (byChoice)
                {
                    var alloc = new System.Collections.Generic.Dictionary<string, int>();
                    int total = 0;
                    if (res.SelectedUids != null && res.SelectedAmounts != null)
                    {
                        for (int i = 0; i < res.SelectedUids.Count && i < res.SelectedAmounts.Count; i++)
                        {
                            string uid = res.SelectedUids[i];
                            int amt = res.SelectedAmounts[i];
                            if (string.IsNullOrEmpty(uid) || amt <= 0) continue;
                            if (!req.OptionUids.Contains(uid)) { GD.PrintErr($"[K5·联机] 回包卡 {uid} 不在候选内，忽略该项"); continue; }
                            var card = _cardManager.GetInstance(uid);
                            int have = card == null ? 0 : EE.GetMarkerCountOnCard(card, ability.CounterCost);
                            amt = System.Math.Min(amt, have);            // 权威钳制：不得超过该卡现有标志数
                            if (amt <= 0) continue;
                            alloc[uid] = amt;
                            total += amt;
                        }
                    }
                    if (total <= 0)
                    {
                        GD.Print($"[K5·联机] {ability.EffectId} 分配为空（至少需 1 个标志）→ 不发动");
                        return false;
                    }
                    allocation = alloc;
                }
                else
                {
                    if (string.IsNullOrEmpty(res.SelectedUid) || !req.OptionUids.Contains(res.SelectedUid))
                    {
                        GD.Print($"[K5·联机] {ability.EffectId} 未选定/非法标志候选 → 不发动（fail-closed）");
                        return false;
                    }
                    counterCostUid = res.SelectedUid;
                }
            }

            // ---------- ④ 目标玩家（单机 `Arena.BeginActivateWithPlayerTarget`）----------
            if (Script.Manager.PlayerTargetPool.IsPlayerSpec(ability.Target))
            {
                var cands = Script.Manager.PlayerTargetPool.Collect(ability.Target, source.ControllerId, PlayerManagerRef);
                if (cands.Count == 0)
                {
                    GD.PrintErr($"[K24·联机] {ability.EffectId} 玩家目标无候选 → 不发动");
                    return false;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectPlayer,
                    SourceUid = source.UID,
                    Prompt = $"请为 {ability.EffectId} 选择目标玩家",
                    ForceChoice = true,
                    OptionPlayerIds = new System.Collections.Generic.List<int>(cands),
                };
                foreach (int pid in cands)
                    req.OptionLabels.Add(Script.Manager.PlayerTargetPool.Describe(PlayerManagerRef, pid));
                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled || !cands.Contains(res.SelectedPlayerId))
                {
                    GD.Print($"[K24·联机] {ability.EffectId} 未选定/非法目标玩家 → 不发动（fail-closed）");
                    return false;
                }
                targetPlayerId = res.SelectedPlayerId;
            }
        }
        finally
        {
            // 与 K15/K16 同款：选择期间持原子锁，但**必须在 ActivateAbility 之前释放**（其自身会 EnterAtomic）
            ExitAtomicWhenIdle();
        }

        EE.ActivateAbility(source, ability, target, targetPlayerId, sacrificeUid, targetAreaId, counterCostUid, allocation);
        // 成功＝确实入堆叠（`ActivateAbility` 的门禁/代价校验失败时不入栈）——据此如实回传是否发动，
        // 避免"服务端已拒绝但调用方以为成功"。
        bool pushed = _stackManager != null && !_stackManager.IsEmpty && stackCountBefore >= 0
                      && _stackManager.Entries.Count > stackCountBefore;
        GD.Print($"[联机激活] {ability.EffectId} 选择类入参已收集：目标地区={targetAreaId ?? "无"} 牺牲={sacrificeUid ?? "无"} " +
                 $"标志卡={counterCostUid ?? (allocation != null ? $"分配 {allocation.Count} 张" : "无")} 目标玩家={targetPlayerId} → 入堆叠={pushed}");
        OnAsyncSettlementCompleted?.Invoke();
        return pushed;
    }

    /// <summary>按 EffectId 在实例的印刷效果表里找激活类效果（角色/附属/秘社；找不到 → null）。</summary>
    private static Script.Data.Effects.EffectData FindActivationAbilityFor(CardInstance inst, string effectId)
    {
        if (inst == null || string.IsNullOrEmpty(effectId)) return null;
        if (inst.PlayerTemplate?.EffectDataList != null)
        {
            foreach (var e in inst.PlayerTemplate.EffectDataList)
                if (e != null && e.EffectId == effectId
                    && (e.Timing == EffectTiming.CharacterActivated || e.Timing == EffectTiming.AttachmentActivated))
                    return e;
        }
        if (inst.CouncilTemplate?.ActivationOptions != null)
        {
            foreach (var opt in inst.CouncilTemplate.ActivationOptions)
            {
                if (opt?.EffectDataList == null) continue;
                foreach (var e in opt.EffectDataList)
                    if (e != null && e.EffectId == effectId && e.Timing == EffectTiming.CouncilActivated) return e;
            }
        }
        return null;
    }

    /// <summary>
    /// 【P2·联机】JC119 免费打出的**目标请求回环**：向该玩家下发 `SelectTarget`（候选＝服务端权威枚举的
    /// 卡目标或地区目标），回包命中候选才继续（否则按取消处理）；随后调 `finish` 完成"按正常打出流程打出"。
    /// </summary>
    private async System.Threading.Tasks.Task RequestFreePlayTargetAsync(int playerId, string cardName, bool isArea,
        List<string> candidates, string prompt, string cardUid, System.Action<string> finish)
    {
        var gateway = EE?.InputGateway;
        if (gateway == null) { finish(null); return; }

        var req = new Script.Net.ChoiceRequest
        {
            PlayerId = playerId,
            RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
            SourceUid = cardUid,
            Prompt = prompt,
            ForceChoice = true,
            OptionUids = new List<string>(candidates),
        };
        if (!isArea)
        {
            foreach (var uid in candidates)
            {
                var inst = _cardManager?.GetInstance(uid);
                if (inst == null) continue;
                req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(inst, inst.CurrentZone, inst.OwnerId));
                req.OptionLabels.Add(inst.PlayerTemplate?.CardName ?? uid);
            }
        }

        // 【2026-09·原子操作期间的玩家选择】等待玩家选目标回包期间持原子锁（服务端无 View）
        var res = await HoldAtomicDuringDecisionAsync(() => gateway.RequestChoice(playerId, req));
        if (res == null || res.Cancelled || string.IsNullOrEmpty(res.SelectedUid))
        {
            GD.Print($"[TurnManager] 免费打出（联机）：玩家{playerId} 未选目标/超时 → {cardName} 留在手牌");
            finish(null);
            return;
        }
        if (!candidates.Contains(res.SelectedUid))
        {
            GD.PrintErr($"[TurnManager] ⛔ 免费打出目标回包 {res.SelectedUid} 不在候选内 → 拒绝（fail-closed）");
            finish(null);
            return;
        }
        finish(res.SelectedUid);
    }

    /// <summary>
    /// 【JC119】"效果免费打出的牌"的**合法卡目标**枚举——候选池与判定函数**复用** `LegalityProjection`
    /// 的口径（争夺区 + 附属区 + 秘社 + 双方资产区 + 双方墓地；判定＝`CardManager.IsLegalPlayTarget`），
    /// 只多一条"排除来源自身"（来源正在被打出，不能以自己为目标）。
    /// </summary>
    public List<string> GetLegalPlayTargetUids(CardInstance handCard)
    {
        var result = new List<string>();
        if (handCard == null || _cardManager == null || _zoneManager == null) return result;
        var pool = new List<CardInstance>();
        pool.AddRange(_cardManager.GetAreaCards() ?? new List<CardInstance>());
        pool.AddRange(_zoneManager.GetZoneCards(CardZone.AttachedZone, 0));
        pool.AddRange(_zoneManager.GetZoneCards(CardZone.CouncilZone, 0));
        pool.AddRange(_zoneManager.GetZoneCards(CardZone.AssetZone, 1));
        pool.AddRange(_zoneManager.GetZoneCards(CardZone.AssetZone, 2));
        pool.AddRange(_zoneManager.GetZoneCards(CardZone.Graveyard, 1));
        pool.AddRange(_zoneManager.GetZoneCards(CardZone.Graveyard, 2));
        foreach (var t in pool)
        {
            if (t == null || t == handCard || t.IsDying) continue;
            if (_cardManager.IsLegalPlayTarget(handCard, t)) result.Add(t.UID);
        }
        return result;
    }

    /// <summary>【JC119】免费打出"地区目标"的候选＝全部地区 UID（打出的地区目标权威校验＝地区存在，同一口径）。</summary>
    private List<string> GetOrderedAreaUidsForPlay()
        => _areaManager?.GetOrderedAreaUids() ?? new List<string>();

    /// <summary>
    /// 原子操作后检查并运行触发效果收集流程（D4 触发规则接驳点）。
    /// 若当前无可触发效果则直接返回；否则按"先手排序窗口 → 后手排序窗口 → 优先玩家得权"推进。
    /// 注：此处统一采用先手优先排序（后手步骤内反转规则后续再调，预留 GetPriorityPlayer 扩展）。
    /// <summary>
    /// 新效果系统：扫描场上所有卡实例的 EffectDataList，收集指定"反应触发"时机（如 OnCardPlayed）
    /// 的效果，条件满足则直接入堆叠（与进场/激活触发同套结算规则，由响应窗口让过结算）。
    /// contextCard = 触发上下文（如本次打出的卡），用于条件判定（如 PlayedSorcery）。
    /// </summary>
    /// <summary>
    /// 触发效果排序流程：按触发所有者分组，每个所有者各自打开一个排序窗口处理自己拥有的触发；
    /// 全部所有者的窗口处理完毕后，优先权归优先玩家。
    /// 窗口内点击"全部/第一张进入堆叠"会立即把对应触发入栈；点击"确认"关闭窗口并进入下一个所有者的窗口。
    /// 这样避免同一批触发被所有玩家重复处理（例如银钥探求者只应被其控制者处理一次）。
    /// </summary>
    /// <summary>
    /// 触发决策的公共分组规则（单机同步窗口与联机异步网关共用，单一真相源）：
    /// 按 ControllerId 分组，再按"先手优先"排定各所有者的处理顺序（D32 修正：窗口归属以控制者为准）。
    /// </summary>
    private (System.Collections.Generic.Dictionary<int, List<Script.Data.Effects.StackEffectInstance>> byOwner, List<int> owners)
        BuildTriggerOwnerGroups(List<Script.Data.Effects.StackEffectInstance> allTriggers)
    {
        var byOwner = new System.Collections.Generic.Dictionary<int, List<Script.Data.Effects.StackEffectInstance>>();
        if (allTriggers != null)
        {
            foreach (var t in allTriggers)
            {
                int owner = t.ControllerId;
                if (!byOwner.ContainsKey(owner)) byOwner[owner] = new List<Script.Data.Effects.StackEffectInstance>();
                byOwner[owner].Add(t);
            }
        }

        var owners = new List<int>(byOwner.Keys);
        int firstPlayer = FirstPlayerId;
        owners.Sort((a, b) =>
        {
            if (a == firstPlayer) return -1;
            if (b == firstPlayer) return 1;
            return a.CompareTo(b);
        });
        return (byOwner, owners);
    }

    private void RunTriggerPhase(List<Script.Data.Effects.StackEffectInstance> allTriggers, System.Action onAllCompleted = null)
    {
        // 1~2. 按控制者分组 + 先手优先排序：与联机异步流程共用同一份分组/排序规则（单一真相源）。
        var (byOwner, owners) = BuildTriggerOwnerGroups(allTriggers);

        // 3. 顺序逐个打开各所有者的窗口；当前窗口 确认(onDone) 后处理下一个。
        System.Action processNext = null;
        int cursor = 0;
        processNext = () =>
        {
            if (cursor >= owners.Count)
            {
                // 所有者的窗口都处理完毕，优先权归优先玩家
                PriorityPlayerId = GetPriorityPlayer();
                ResetPassWindow();
                GD.Print($"⏳ [Trigger] 触发阶段结束，优先权给优先玩家{PriorityPlayerId}");
                OnPriorityChanged?.Invoke(PriorityPlayerId);
                onAllCompleted?.Invoke();   // 全部所有者窗口结束后回调（如对抗前触发需立即结算）
                return;
            }
            int owner = owners[cursor];
            cursor++;
            // 【健壮性】未注入触发排序视图（headless/测试/异常装配）时**不再 NRE**：
            // 按收集顺序直接入栈（＝"不排序"的确定性降级），并照常推进到下一所有者窗口。
            // 此前这里会抛 NullReferenceException，把整个结算流程打断（本轮实测踩到）。
            if (_view == null)
            {
                GD.PrintErr($"[Trigger] ⚠ 未注入触发排序视图 → 玩家{owner} 的 {byOwner[owner].Count} 条触发按收集顺序直接入栈（降级）");
                PushOrderedTriggers(byOwner[owner], 0, processNext);
                return;
            }
            GD.Print($"[Trigger] 打开玩家{owner} 的触发排序窗口（触发数={byOwner[owner].Count}）");
            _view.OpenTriggerOrderForPlayer(owner, byOwner[owner],
                // 触发实际入栈（含选目标完成）后，从 OrderZone 移除对应条目
                onEnterStack: ordered => PushOrderedTriggers(ordered, 0, () => _view.RemoveOrderEntries(ordered)),
                onDone: processNext);
        };
        processNext();
    }

    // ==================== C/S 化 P0-B：触发排序窗口异步版 ====================
    /// <summary>
    /// 可挂起续算版 RunTriggerPhase。语义与同步版一致：按 ControllerId 分组→先手优先排序→逐所有者窗口。
    /// 区别：当 InputGateway 已注入时，每个所有者的"排序窗口"改为经网关向该玩家发起
    /// SelectTriggerOrder 请求并 await 回包，而非本地 _view 弹窗；其余（入栈/选目标/让过）逻辑复用同步路径。
    /// 未注入网关时直接委托同步版，行为完全不变（向后兼容现有单机/测试）。
    /// 这样既修复了 D17 多玩家编排的"窗口无输入隔离"问题（联机时由网关确保仅 owner 可操作），
    /// 又无需改动现有 _view 弹窗链路。
    /// </summary>
    /// <summary>
    /// 触发流程统一入口：单机（未注入 InputGateway）走原同步排序窗口；
    /// 联机（已注入 InputGateway）走网络化异步流程。由 SetTriggerSystem 绑定到 TriggerRegistry.RunTriggerPhase。
    /// </summary>
    private void RunTriggerPhaseDispatch(List<Script.Data.Effects.StackEffectInstance> allTriggers, System.Action onAllCompleted = null)
    {
        // 恢复步骤·双方都超上限：延迟收集弃牌触发，双方都弃完后再统一开窗（决策：defer）
        if (_deferRecoverTriggers)
        {
            _deferredRecoverTriggers.AddRange(allTriggers);
            return;
        }
        // 恢复步骤内触发的效应：标记挂起，阻止在窗口结束前提前离开恢复步骤
        bool hold = (CurrentPhase == TurnPhase.EndRecover);
        if (hold) _recoverTriggerHold = true;
        System.Action completed = () =>
        {
            if (hold) _recoverTriggerHold = false;
            onAllCompleted?.Invoke();
            // 【恢复步骤·再驱动】触发窗口结束后，若仍在恢复步骤且手牌未清（弃牌循环先前因"窗口挂起"跳出）：
            // 重新驱动手牌上限循环，避免"触发结算完却没人再推进恢复步骤"的死角（先手/后手弃牌触发的效应）。
            if (hold && CurrentPhase == TurnPhase.EndRecover && !RecoverCanLeave()
                && (_stackManager?.Entries.Count ?? 0) == 0)
            {
                RunRecoverHandLimitAsync();
            }
        };
        if (InputGateway != null)
        {
            _ = RunTriggerPhaseAsync(allTriggers, () =>
            {
                completed();
                OnAsyncSettlementCompleted?.Invoke();
            });
            return;
        }
        RunTriggerPhase(allTriggers, completed);
    }

    /// <summary>联机触发排序窗口的上下文序号（用于生成 OrderContextId，客户端据此复用同一排序窗口）。</summary>
    private int _triggerOrderContextSeq;

    private async System.Threading.Tasks.Task RunTriggerPhaseAsync(List<Script.Data.Effects.StackEffectInstance> allTriggers, System.Action onAllCompleted = null)
    {
        if (InputGateway == null)
        {
            RunTriggerPhase(allTriggers, onAllCompleted);
            return;
        }
        // 联机触发排序/选目标为原子操作：期间拒绝让过/出牌，与单机模态窗口 EnterAtomic 语义一致。
        EnterAtomic();
        try
        {
            var (byOwner, owners) = BuildTriggerOwnerGroups(allTriggers);

            for (int i = 0; i < owners.Count; i++)
            {
                int owner = owners[i];   // owner = 触发效果来源卡的 ControllerId（BuildTriggerOwnerGroups 已按 ControllerId 分组）
                // 排序窗口上下文 id：同一 owner 的整个排序过程（含"逐次入堆叠"的多轮请求）共用一个 id，
                // 客户端据此复用同一窗口、不反复关闭重开（对齐单机排序窗口保持打开的手感）。
                string contextId = $"trig_{owner}_{TurnCount}_{++_triggerOrderContextSeq}";
                var remaining = new List<Script.Data.Effects.StackEffectInstance>(byOwner[owner]);
                GD.Print($"[Trigger] 向玩家{owner}（controller）发起触发排序请求（Async，触发数={remaining.Count}，ctx={contextId}）");

                int guard = 0;
                while (remaining.Count > 0)
                {
                    // 防呆：客户端若持续回"不可推进"的结果（如一直取消但仍有强制触发），最多重问 32 轮，
                    // 之后按默认顺序全部入栈，避免死循环卡住整局。
                    if (++guard > 32)
                    {
                        GD.PrintErr($"[Trigger] 玩家{owner} 排序请求重试超过上限，剩余 {remaining.Count} 个按默认顺序入栈");
                        foreach (var t in remaining) await EnsureTargetThenPushAsync(t);
                        break;
                    }
                    // 任一触发为强制（EffectData.Forced）时，该窗口不可整体放弃（等价单机 Done 按钮禁用）。
                    bool force = remaining.Exists(t => t.EffectDataRef != null && t.EffectDataRef.Forced);
                    var req = new Script.Net.ChoiceRequest
                    {
                        PlayerId = owner,
                        RequestKind = Script.Net.ChoiceRequest.Kind.SelectTriggerOrder,
                        OptionUids = remaining.ConvertAll(t => t.Source?.UID ?? t.DisplayName),
                        OptionForced = remaining.ConvertAll(t => t.EffectDataRef != null && t.EffectDataRef.Forced),
                        OptionLabels = remaining.ConvertAll(t => t.DisplayName ?? ""),
                        OptionTexts = remaining.ConvertAll(t => t.DisplayText ?? ""),
                        Prompt = $"玩家{owner} 排序触发（{remaining.Count} 个）",
                        ForceChoice = force,
                        OrderContextId = contextId,
                    };

                    var result = await InputGateway.RequestChoice(owner, req);

                    // 放弃剩余：仅当剩余中没有强制触发时才生效（有强制触发 → 重新请求，对齐单机按钮禁用语义）。
                    if (result != null && result.Cancelled)
                    {
                        if (force)
                        {
                            GD.Print($"[Trigger] 玩家{owner} 请求放弃但仍有强制触发，重新请求排序");
                            continue;
                        }
                        GD.Print($"[Trigger] 玩家{owner} 放弃剩余触发（{remaining.Count} 个），跳过入栈");
                        break;
                    }

                    // 本次要入堆叠的条目：按 SelectedIndices 的顺序（下标指向本次请求的 OptionUids）。
                    // 兼容：未回下标（旧客户端）或空列表 → 全部按默认顺序入栈（与旧行为一致，不会卡死）。
                    var ordered = new List<Script.Data.Effects.StackEffectInstance>();
                    var indices = result?.SelectedIndices;
                    if (indices == null || indices.Count == 0)
                    {
                        GD.Print($"[Trigger] 玩家{owner} 未指定顺序，剩余 {remaining.Count} 个按默认顺序入栈");
                        ordered.AddRange(remaining);
                    }
                    else
                    {
                        var seen = new HashSet<int>();
                        foreach (var idx in indices)
                        {
                            if (idx < 0 || idx >= remaining.Count)
                            {
                                GD.PrintErr($"[Trigger] 忽略越界下标 {idx}（剩余 {remaining.Count} 个）");
                                continue;
                            }
                            if (!seen.Add(idx))
                            {
                                GD.PrintErr($"[Trigger] 忽略重复下标 {idx}");
                                continue;
                            }
                            ordered.Add(remaining[idx]);
                        }
                        if (ordered.Count == 0)
                        {
                            // 全是非法下标：不推进（下一轮 guard 保护），避免空转死循环。
                            GD.PrintErr($"[Trigger] 玩家{owner} 回包下标全部非法，本轮不推进");
                            continue;
                        }
                    }

                    // 按玩家指定顺序逐个入堆叠（需选目标的先经网关选目标），随后从剩余中移除。
                    foreach (var t in ordered)
                        await EnsureTargetThenPushAsync(t);
                    foreach (var t in ordered)
                        remaining.Remove(t);
                }
            }

            PriorityPlayerId = GetPriorityPlayer();
            ResetPassWindow();
            GD.Print($"⏳ [Trigger] 触发阶段结束（Async），优先权给优先玩家{PriorityPlayerId}");
            OnPriorityChanged?.Invoke(PriorityPlayerId);
            onAllCompleted?.Invoke();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[Trigger] 网络化触发流程异常: {ex.GetType().Name}: {ex.Message}");
            onAllCompleted?.Invoke();
        }
        finally
        {
            ExitAtomicWhenIdle();
        }
    }

    /// <summary>
    /// 【K16·模式分支·触发版·联机】触发类"选择一项"的**入堆叠前**模式选择（经 `InputGateway` 下发
    /// `ChoiceRequest.Kind.SelectEffectMode`，复用卡牌模式分支的同一请求类型与客户端面板）。
    /// 与单机口径一致：**入堆叠时选定、入堆叠后不可更改**（§7.4b A5）；取消/超时/非法序号 → 该触发**不入堆叠**。
    /// 返回 false 表示"本触发被放弃"。
    /// </summary>
    private async System.Threading.Tasks.Task<bool> EnsureTriggerModeAsync(Script.Data.Effects.StackEffectInstance t)
    {
        var data = t?.EffectDataRef;
        if (data?.Modes == null || data.Modes.Count == 0 || t.ModeIndex >= 0) return true;
        if (InputGateway == null)
        {
            t.ModeIndex = 0;
            GD.PrintErr($"[Trigger] ⚠ 无网关 → {t.DisplayName} 的『选择一项』按确定性兜底取模式 1/{data.Modes.Count}：{data.Modes[0]?.Label}");
            return true;
        }
        int chooser = t.Source?.ControllerId ?? t.OwnerPlayerId;
        var req = new Script.Net.ChoiceRequest
        {
            PlayerId = chooser,
            RequestKind = Script.Net.ChoiceRequest.Kind.SelectEffectMode,
            SourceUid = t.Source?.UID,
            Prompt = $"{t.DisplayName}：选择一项",
            ForceChoice = true,
            Amount = data.Modes.Count,
        };
        for (int i = 0; i < data.Modes.Count; i++)
            req.OptionLabels.Add(data.ModeAt(i)?.Label ?? $"选项{i + 1}");
        var res = await InputGateway.RequestChoice(chooser, req);
        if (res == null || res.Cancelled)
        {
            GD.Print($"[Trigger] 【K16·触发版·联机】玩家{chooser} 取消/超时未选模式 → 该触发不入堆叠（{t.DisplayName}）");
            return false;
        }
        int chosen = (res.SelectedIndices != null && res.SelectedIndices.Count > 0) ? res.SelectedIndices[0] : -1;
        if (data.ModeAt(chosen) == null)
        {
            GD.PrintErr($"[Trigger] 【K16·触发版·联机】回包模式序号非法（{chosen}）→ 该触发不入堆叠");
            return false;
        }
        t.ModeIndex = chosen;
        GD.Print($"[Trigger] 【K16·触发版·联机】玩家{chooser} 选定模式 {chosen + 1}/{data.Modes.Count}：{data.ModeAt(chosen)?.Label}");
        return true;
    }

    /// <summary>网络化触发入栈：逐个 await 选模式/选目标（若需），随后入栈。</summary>
    private async System.Threading.Tasks.Task EnsureTargetThenPushAsync(Script.Data.Effects.StackEffectInstance t)
    {
        // 【K16·触发版】模式选择先于目标选择（模式可能决定目标声明）；放弃 → 不入堆叠。
        if (!await EnsureTriggerModeAsync(t)) return;
        if (NeedSelectAnyTarget(t) && string.IsNullOrEmpty(t.SelectedTargetUid))
        {
            // 【K24·玩家目标】触发路径的玩家目标：经网关下发独立选择请求（`Kind.SelectPlayer`）。
            var pSpec = t.EffectDataRef?.Target;
            if (PlayerTargetPool.IsPlayerSpec(pSpec))
            {
                int chooser = t.Source?.ControllerId ?? t.ControllerId;
                var pCandidates = PlayerTargetPool.Collect(pSpec, chooser, PlayerManagerRef);
                if (pCandidates.Count == 0)
                {
                    GD.Print($"[Trigger] {t.DisplayName} 无合法目标玩家，跳过入栈");
                    return;
                }
                var pReq = new Script.Net.ChoiceRequest
                {
                    PlayerId = chooser,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectPlayer,
                    SourceUid = t.Source?.UID,
                    Prompt = $"请为 {t.DisplayName} 选择目标玩家",
                    OptionPlayerIds = new List<int>(pCandidates),
                    ForceChoice = true,   // 对齐卡目标：选目标不可取消，必须选一个
                };
                var pRes = await InputGateway.RequestChoice(chooser, pReq);
                if (pRes == null || pRes.Cancelled || pRes.SelectedPlayerId <= 0)
                {
                    GD.Print($"[Trigger] {t.DisplayName} 玩家取消/未选目标玩家，跳过入栈");
                    return;
                }
                t.SelectedPlayerId = pRes.SelectedPlayerId;
                PushTriggerToStack(t);
                return;
            }

            var valid = GetValidTargetUidsForTrigger(t);
            if (valid.Count == 0)
            {
                GD.Print($"[Trigger] {t.DisplayName} 无合法目标，跳过入栈");
                return;
            }

            // 推送目标：效果来源卡的 ControllerId（明确以 controller 为准，而非 Owner/触发者）。
            int controller = t.Source?.ControllerId ?? t.ControllerId;
            // 【K14】墓地/手牌目标：这些卡没有场上 CardUI，"场上点选"窗口点不到 →
            // 改用 `SelectZoneCard`（客户端走通用决策面板的按钮式选卡；候选随 OptionCardRefs 下发以便渲染卡名）。
            var spec = t.EffectDataRef?.Target;
            bool zonePile = TargetZonePool.IsZonePileSpec(spec);
            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = controller,
                RequestKind = zonePile
                    ? Script.Net.ChoiceRequest.Kind.SelectZoneCard
                    : Script.Net.ChoiceRequest.Kind.SelectTarget,
                SourceUid = t.Source?.UID,
                OptionUids = valid,
                Prompt = zonePile
                    ? $"请选择{TargetZonePool.DescribeZone(spec.Kind)}中的目标牌"
                    : $"请为 {t.DisplayName} 选择目标",
                ForceChoice = true,   // 对齐单机 TargetSelectionWindow：选目标不可取消/点窗外关闭，必须选一个
            };
            if (zonePile)
            {
                foreach (var uid in valid)
                {
                    var inst = _cardManager?.GetInstance(uid);
                    if (inst != null)
                        req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(inst, inst.CurrentZone, inst.OwnerId));
                    // 【P2·联机可读性】显示名随请求下发（墓地/手牌等区域牌堆在客户端是隐藏占位）
                    req.OptionLabels.Add(inst?.PlayerTemplate?.CardName ?? uid);
                }
            }
            var result = await InputGateway.RequestChoice(controller, req);
            if (result == null || result.Cancelled || string.IsNullOrEmpty(result.SelectedUid))
            {
                GD.Print($"[Trigger] {t.DisplayName} 玩家取消/未选目标，跳过入栈");
                return;
            }
            // 【P2·联机权威复检】回包必须是"服务端本次下发的候选之一"（`valid` 由 `GetValidTargetUidsForTrigger`
            // 权威计算）。原先直接采信回包 UID ⇒ 客户端可指定任意卡作为触发目标（越权）。
            if (!valid.Contains(result.SelectedUid))
            {
                GD.PrintErr($"[Trigger] ⛔ {t.DisplayName} 目标回包 {result.SelectedUid} 不在候选内 → 该触发不入栈（fail-closed）");
                return;
            }
            t.SelectedTargetUid = result.SelectedUid;
            PushTriggerToStack(t);
            return;
        }

        PushTriggerToStack(t);
    }

    /// <summary>计算"需要选目标"触发的合法候选 UID（同步/异步选目标共用，单一真相源）。</summary>
    private List<string> GetValidTargetUidsForTrigger(Script.Data.Effects.StackEffectInstance t)
    {
        if (t == null) return new List<string>();
        if (t.IsAreaTarget && t.Source != null)
            return GetOtherCombatAreaUids(t.Source.ZoneDetailId);
        if (t.Source != null)
            return GetLocalZoneTargets(t);
        return new List<string>();
    }

    /// <summary>按列表顺序逐个触发：需目标且未选的先弹选目标窗口，选完再入栈；处理完所有后调用 onAllDone。</summary>
    private void PushOrderedTriggers(List<Script.Data.Effects.StackEffectInstance> list, int index, System.Action onAllDone)
    {
        if (list == null || index >= list.Count)
        {
            onAllDone?.Invoke();
            return;
        }
        var t = list[index];
        // 模式选择（若有）先于目标选择；**取消 → 该触发不入堆叠**，但流程继续到下一个触发。
        EnsureTriggerModeThenPush(t,
            onReady: () => EnsureTargetThenPush(t, () => PushOrderedTriggers(list, index + 1, onAllDone)),
            onSkip: () => PushOrderedTriggers(list, index + 1, onAllDone));
    }

    /// <summary>
    /// 【K16·模式分支·触发版】触发类"选择一项"（`EffectData.Modes`）的**入堆叠前**模式选择（单机）。
    /// 口径（用户 2026-09，§7.4b A5）：**入堆叠时选定**（与 K16 卡牌模式"打出时选定、入堆叠不可更改"、
    /// K15 激活"目标地区入堆叠时锁定"同一口径），**不是结算时**才问。
    /// · 效果未声明 `Modes` / 已选定（`ModeIndex >= 0`）→ 直接续算，行为完全不变；
    /// · 有 UI（`IGameUIHooks.PromptEffectMode`）→ 开选择窗，回包写入 `t.ModeIndex`；
    /// · **无 UI（headless/测试/异常装配）→ 确定性兜底第一个模式**（与"卡目标缺省取第一个候选"同口径），
    ///   并打警告日志（不静默：日志可追溯）。
    /// </summary>
    /// <param name="onReady">模式已选定（或本效果无模式）→ 续算（进入取目标/入堆叠）。</param>
    /// <param name="onSkip">玩家取消选择 → **不入堆叠**，但整体流程继续（调 onSkip 推进下一个触发）。</param>
    private void EnsureTriggerModeThenPush(Script.Data.Effects.StackEffectInstance t,
        System.Action onReady, System.Action onSkip)
    {
        var data = t?.EffectDataRef;
        if (data?.Modes == null || data.Modes.Count == 0 || t.ModeIndex >= 0)
        {
            onReady?.Invoke();
            return;
        }
        var hooks = EE?.UIHooks;
        if (hooks == null)
        {
            t.ModeIndex = 0;
            GD.PrintErr($"[Trigger] ⚠ 无 UI → {t.DisplayName}（玩家{t.Source?.ControllerId ?? t.OwnerPlayerId}）" +
                        $"的『选择一项』按确定性兜底取模式 1/{data.Modes.Count}：{data.Modes[0]?.Label}");
            onReady?.Invoke();
            return;
        }
        int chooser = t.Source?.ControllerId ?? t.OwnerPlayerId;
        GD.Print($"[Trigger] 【K16·触发版】向玩家{chooser} 询问『选择一项』（{data.EffectId}，共 {data.Modes.Count} 项）");
        hooks.PromptEffectMode(t.Source, data, idx =>
        {
            if (idx < 0 || idx >= data.Modes.Count)
            {
                // 取消/非法：与"无合法目标"同一结果——该触发**不入堆叠**，绝不静默代选。
                GD.Print($"[Trigger] {t.DisplayName} 玩家取消/未选模式 → 该触发不入堆叠");
                onSkip?.Invoke();
                return;
            }
            t.ModeIndex = idx;
            GD.Print($"[Trigger] 【K16·触发版】{t.DisplayName} 选定模式 {idx + 1}/{data.Modes.Count}：{data.Modes[idx]?.Label}");
            onReady?.Invoke();
        });
    }

    /// <summary>
    /// 【K24·玩家目标】触发路径的玩家目标选择（单机）：候选＝`PlayerTargetPool.Collect`
    /// （"任意/本方/对手"限制），经 `IGameUIHooks.PromptPlayerTarget` 独立窗口收集。
    /// **无 UI 时确定性兜底**取第一个候选（与卡目标既有口径一致，供测试/headless）；
    /// 取消 / 无候选 → 该触发**不入堆叠**（绝不静默代选）。
    /// </summary>
    private void EnsurePlayerTargetThenPush(Script.Data.Effects.StackEffectInstance t, System.Action onPushed)
    {
        var spec = t.EffectDataRef?.Target;
        int chooser = t.Source?.ControllerId ?? t.ControllerId;
        var candidates = PlayerTargetPool.Collect(spec, chooser, PlayerManagerRef);
        if (candidates.Count == 0)
        {
            GD.Print($"[Trigger] {t.DisplayName} 无合法目标玩家，跳过入栈");
            onPushed?.Invoke();
            return;
        }
        var hooks = EE?.UIHooks;
        if (hooks != null)
        {
            hooks.PromptPlayerTarget(chooser, $"请为 {t.DisplayName} 选择目标玩家", candidates, picked =>
            {
                if (picked <= 0)
                {
                    GD.Print($"[Trigger] {t.DisplayName} 玩家取消/未选目标玩家，跳过入栈");
                    onPushed?.Invoke();
                    return;
                }
                // 【P2·联机权威复检】回包的玩家号必须是"服务端下发的候选"之一（`PlayerTargetPool` 权威枚举）。
                if (!candidates.Contains(picked))
                {
                    GD.PrintErr($"[Trigger] ⛔ {t.DisplayName} 目标玩家回包 P{picked} 不在候选内 → 该触发不入栈（fail-closed）");
                    onPushed?.Invoke();
                    return;
                }
                t.SelectedPlayerId = picked;
                PushTriggerToStack(t);
                onPushed?.Invoke();
            });
            return;
        }
        GD.Print($"[Trigger] {t.DisplayName} 需选目标玩家但无 UI → 取第一个候选（确定性兜底：玩家{candidates[0]}）");
        t.SelectedPlayerId = candidates[0];
        PushTriggerToStack(t);
        onPushed?.Invoke();
    }

    /// <summary>确保触发已选定目标后压入堆叠。需目标且未选时，先通过 Arena 弹选目标窗口收集目标。</summary>
    private void EnsureTargetThenPush(Script.Data.Effects.StackEffectInstance t, System.Action onPushed)
    {
        if (NeedSelectAnyTarget(t) && string.IsNullOrEmpty(t.SelectedTargetUid))
        {
            // 【K24·玩家目标】以**玩家**为目标的触发：候选不是卡 UID，必须在入堆叠前经"独立选择窗口"
            // 就地选定（目标与卡目标同口径＝入堆叠时锁定）。此处**先于**卡目标流程判定。
            var pSpec = t.EffectDataRef?.Target;
            if (PlayerTargetPool.IsPlayerSpec(pSpec))
            {
                EnsurePlayerTargetThenPush(t, onPushed);
                return;
            }

            List<string> valid = GetValidTargetUidsForTrigger(t);
            if (valid.Count == 0)
            {
                GD.Print($"[Trigger] {t.DisplayName} 无合法目标，跳过入栈");
                onPushed?.Invoke();
                return;
            }

            // 【K14】墓地/手牌目标：这些卡**没有场上的 CardUI**，"点选场上卡"的窗口点不到，
            // 故改走通用选卡窗（`IGameUIHooks.PromptPickCard`：按钮式列出候选卡名，任何区域的卡都能选）。
            var spec = t.EffectDataRef?.Target;
            if (TargetZonePool.IsZonePileSpec(spec))
            {
                var hooks = EE?.UIHooks;
                if (hooks != null)
                {
                    var candidates = new List<CardInstance>();
                    foreach (var uid in valid)
                    {
                        var inst = _cardManager?.GetInstance(uid);
                        if (inst != null) candidates.Add(inst);
                    }
                    hooks.PromptPickCard(t.Source?.ControllerId ?? 0,
                        $"请选择{TargetZonePool.DescribeZone(spec.Kind)}中的目标牌",
                        candidates, allowCancel: false,
                        picked =>
                        {
                            if (picked == null) { onPushed?.Invoke(); return; }   // 无选择 → 不结算（与"无合法目标"同结果）
                            t.SelectedTargetUid = picked.UID;
                            PushTriggerToStack(t);
                            onPushed?.Invoke();
                        });
                    return;
                }
                GD.Print($"[Trigger] {t.DisplayName} 区域牌堆目标（{spec.Kind}）但无 UI → 取第一个候选（确定性兜底）");
                t.SelectedTargetUid = valid[0];
                PushTriggerToStack(t);
                onPushed?.Invoke();
                return;
            }

            if (_view != null)
            {
                // 新系统地区目标（机动 ManeuverTrigger）：直接取"所有其他争夺区"作为候选。
                // 旧 TriggerEffectCollector 已随旧系统迁移删除，普通反应触发（如 XG01 抽牌）不进此分支
                // （其 RequiresTarget 为 false，见 StackEffectInstance）。
                // 候选计算与异步选目标共用 GetValidTargetUidsForTrigger（单一真相源）。
                _view.RequestTargetSelection(t, valid, (uid) =>
                {
                    t.SelectedTargetUid = uid;
                    PushTriggerToStack(t);
                    onPushed?.Invoke();
                });
                return;
            }

            // 无视图：确定性兜底取第一个候选
            GD.Print($"[Trigger] {t.DisplayName} 需选目标但无视图 → 取第一个候选（确定性兜底）");
            t.SelectedTargetUid = valid[0];
            PushTriggerToStack(t);
            onPushed?.Invoke();
        }
        else
        {
            PushTriggerToStack(t);
            onPushed?.Invoke();
        }
    }

    /// <summary>将单个触发效果作为 Trigger 类堆叠条目压入堆叠。</summary>
    private void PushTriggerToStack(Script.Data.Effects.StackEffectInstance t)
    {
        if (_stackManager == null) return;
        var entry = new StackEntry(t.OwnerPlayerId, t.DisplayText)
        {
            Kind = ResolveKind.Trigger,
            ControllerId = t.Source?.ControllerId ?? t.OwnerPlayerId,   // 箭头按控制者着色（D31）
            SourceCardUid = t.Source?.UID ?? "",
            TargetCardUid = t.TargetCardUid ?? "",   // 反应触发上下文（刚打出的卡），结算时用于条件判定
            WinnerId = t.WinnerId,                   // "X对抗获胜"类触发：对抗获胜方玩家 ID，供 ResolveEffect→WinnerIsSelf 判定
            // 【K1】本次事件携带的数量（如"本次弃牌的总张数"）：供动态值表达式 `事件数量` 求值
            EventAmount = t.EventAmount,
            TargetAreaId = t.TargetAreaId,           // "X对抗获胜"类触发：对抗获胜地区 UID，供 ResolveEffect→WinnerInSameArea 判定（Y 方案）
            ModeIndex = t.ModeIndex,                 // 【K16·触发版】"选择一项"所选模式（入堆叠时已选定，-1＝无模式）
            StackEffect = t,
        };

        // 【K24·玩家目标】触发的目标玩家写入结算负载（-1＝无），并登记指向玩家头像的箭头引用。
        if (t.SelectedPlayerId > 0)
        {
            entry.TargetPlayerId = t.SelectedPlayerId;
            entry.Targets.Add(new Script.Game.Entities.StackTargetRef
            {
                Kind = Script.Game.Entities.StackTargetRef.RefKind.Player,
                PlayerId = t.SelectedPlayerId,
            });
            GD.Print($"[Trigger] 入栈: {t.DisplayName}, 目标玩家=玩家{t.SelectedPlayerId}，已登记箭头目标");
        }

        // 新系统（EffectEngine）触发：若承载的是 EffectData，写入 EffectDataEffect，
        // 由 ResolveStackTop 的 Trigger 分支优先走新系统结算（EffectEngine.Instance.ResolveEffect）。
        if (t.EffectDataRef != null)
        {
            entry.EffectDataEffect = t.EffectDataRef;
            if (string.IsNullOrEmpty(entry.SourceCardUid) && t.Source != null)
                entry.SourceCardUid = t.Source.UID;
            // 结算前校验来源卡版本号未变（RequireSourceVersionUnchanged）：入栈时记录来源卡 VersionId，
            // 结算时比对，来源卡离场/翻面/进手牌/进墓地等触发 BumpVersion 则跳过结算。
            if (t.EffectDataRef.RequireSourceVersionUnchanged && t.Source != null)
            {
                entry.SourceVersionId = t.Source.VersionId;
                GD.Print($"[Trigger] 入栈: {t.EffectDataRef.EffectId} 记录来源版本号={entry.SourceVersionId}（来源={t.Source.PlayerTemplate?.CardName}）");
            }
            GD.Print($"[Trigger] 入栈(新系统 EffectData): {t.EffectDataRef.EffectId}, 来源={t.Source?.PlayerTemplate?.CardName}");
        }

        // 取目标效果：把选定目标写入 entry.Targets，堆叠区据此绘制"箭尾从堆叠条目→目标"的箭头；
        // 同时按目标类型写回 entry 的结算字段（TargetCardUid / TargetAreaId）。
        //  - 机动（JC014）等取地区目标（IsAreaTarget==true）：SelectedTargetUid 为地区槽位，
        //    箭尾指向目标地区 UI，并写 entry.TargetAreaId 供 Move 动词取落点；
        //  - 进场触发选卡目标（如 JC002，IsAreaTarget==false 但 NeedsSelection==true）：SelectedTargetUid
        //    为"目标卡 UID"，必须写回 entry.TargetCardUid，否则 ResolveEffect 取到 null 目标（选目标失效）。
        if (!string.IsNullOrEmpty(t.SelectedTargetUid))
        {
            if (t.IsAreaTarget)
            {
                entry.Targets.Add(new Script.Game.Entities.StackTargetRef
                {
                    Kind = Script.Game.Entities.StackTargetRef.RefKind.Area,
                    AreaUid = t.SelectedTargetUid,
                    AreaSlotId = ParseAreaSlotId(t.SelectedTargetUid),
                });
                GD.Print($"[Trigger] 入栈: {t.DisplayName}, 目标地区={t.SelectedTargetUid}，已登记箭头目标");
            }
            else
            {
                // 卡目标（JC002 类进场触发）：写回 TargetCardUid 供结算，并登记卡箭头
                entry.TargetCardUid = t.SelectedTargetUid;
                entry.Targets.Add(new Script.Game.Entities.StackTargetRef
                {
                    Kind = Script.Game.Entities.StackTargetRef.RefKind.Card,
                    CardUid = t.SelectedTargetUid,
                });
                GD.Print($"[Trigger] 入栈: {t.DisplayName}, 目标卡={t.SelectedTargetUid}，已登记箭头目标（进场触发选卡）");
                GD.Print($"🎯 [取目标] 触发 {t.DisplayName} 取目标卡 {t.SelectedTargetUid}");
            }
        }
        else if (!string.IsNullOrEmpty(t.TargetCardUid))
        {
            entry.Targets.Add(new Script.Game.Entities.StackTargetRef
            {
                Kind = Script.Game.Entities.StackTargetRef.RefKind.Card,
                CardUid = t.TargetCardUid,
            });
            GD.Print($"[Trigger] 入栈: {t.DisplayName}, 目标卡={t.TargetCardUid}，已登记箭头目标（反应触发上下文）");
        }

        // 新系统机动（ManeuverTrigger）：选定的目标地区同时写入 entry.TargetAreaId，
        // 供 ResolveStackTop 的 Trigger 分支走 EffectEngine.Instance.ResolveEffect(data, src, tgt, targetAreaId) 时
        // 把落脚地区传给 Move 动词（否则 targetAreaId 为空，机动无目的地）。
        // 仅当该触发为取地区目标时写入（旧系统 TargetType==AreaSlot 或新系统 IsAreaTarget）。
        if (t.NeedsAreaTarget && !string.IsNullOrEmpty(t.SelectedTargetUid))
        {
            entry.TargetAreaId = t.SelectedTargetUid;
            GD.Print($"[Trigger] 入栈: 写入 TargetAreaId={t.SelectedTargetUid}（机动落点）");
        }
        else
        {
            GD.Print($"[Trigger] 入栈: {t.DisplayName}（无目标箭头）");
        }

        // 【触发时所在地区】把"触发时所在地区"（死亡＝死亡时所处地区 / 进场＝进入的地区 / 赢取＝被赢取地区）
        // 写入条目：结算时作为 `ResolveEffect(targetAreaId)` 下发 —— 供 `MoveToZone{…,"争夺区"}`（JC124
        // "从墓地移回**场上**（它死亡时所在地区）"）与"本地区"判定使用，避免拿已离场来源的 null ZoneDetailId 当基准。
        // 仅在条目尚无目标地区时补写（机动等显式地区目标不受影响）。
        if (string.IsNullOrEmpty(entry.TargetAreaId) && !string.IsNullOrEmpty(t.SourceAreaAtTrigger))
        {
            entry.TargetAreaId = t.SourceAreaAtTrigger;
            GD.Print($"[Trigger] 入栈: 写入**触发时所在地区**={t.SourceAreaAtTrigger}（供结算取『本地区』/回场落点）");
        }

        // 记录目标入栈快照（控制者+子类别），供"结算前变化"类丢失判定比对待入栈时状态（过程记忆）
        CaptureTargetSnapshot(entry);

        _stackManager.Push(entry);
    }

    /// <summary>
    /// 判断触发效果是否需要"弹窗选目标"。
    ///  - 机动（JC014）：IsAreaTarget==true && EffectData.Target.NeedsSelection==true（取地区目标）。
    ///  - 进场/现身触发选卡目标（JC002）：IsAreaTarget==false 但 EffectData.Target.NeedsSelection==true。
    /// 不能用 StackEffectInstance.RequiresTarget 判断卡目标（其受 IsAreaTarget 约束恒 false）。
    /// </summary>
    private static bool NeedSelectAnyTarget(Script.Data.Effects.StackEffectInstance t)
    {
        if (t == null) return false;
        if (t.IsAreaTarget) return t.RequiresTarget; // 地区目标：沿用原判定
        return t.EffectDataRef?.Target?.NeedsSelection == true; // 卡目标（进场触发选卡）
    }

    /// <summary>
    /// 取"本地区（同一 AREASLOT 内的全部争夺区，含来源所在区与另一争夺区）"内、
    /// 满足触发目标 Kind 的合法候选卡 UID 列表。
    /// 用于 JC002 类进场/现身触发选"本地区目标角色"——可跨同一 AREASLOT 的两个争夺区选目标。
    /// 候选 = 来源卡所在 SLOT 下所有争夺区的实例，按 EffectData.Target.Kind 过滤，排除来源自身。
    /// </summary>
    private List<string> GetLocalZoneTargets(Script.Data.Effects.StackEffectInstance t)
    {
        var spec = t.EffectDataRef?.Target;
        string srcArea = !string.IsNullOrEmpty(t.SourceAreaAtTrigger) ? t.SourceAreaAtTrigger : (t.Source?.ZoneDetailId ?? "");
        return GetTargetUidsForSpec(spec, t.Source, srcArea);
    }

    /// <summary>
    /// 【K16】按目标声明取合法候选（**触发取目标**与**模式分支打出取目标**共用，单一实现）：
    ///  · 非争夺区目标（墓地/手牌/资产/附属/秘社/封印）→ `TargetZonePool` 按 spec 枚举；
    ///  · 其余 → 来源卡所在 SLOT 下所有争夺区实例，按 `spec.Kind` 过滤，排除来源自身（除非 `CanTargetSelf`）。
    /// </summary>
    private List<string> GetTargetUidsForSpec(TargetSpec spec, CardInstance source, string sourceAreaOverride = null,
        bool allAreasIfNotLocal = false)
    {
        var result = new List<string>();
        if (source == null || _cardManager == null || _areaManager == null) return result;
        var kind = spec?.Kind ?? TargetKind.None;
        // 【K14】非争夺区目标（墓地/手牌/资产/附属/秘社/封印）：不走"本地区争夺区"枚举，
        // 改由 `TargetZonePool` 按 TargetSpec 枚举（含 Friendly/Enemy、Filter 文本筛选，以及 `KindAlt` 的 OR；
        // `IsLocalZone` 时按事件地区过滤附属等有地区归属的卡）。丢失判定复用既有版本号逻辑。
        if (TargetZonePool.IsZonePileSpec(spec))
        {
            int srcCtrl0 = source.ControllerId;
            string srcArea0 = !string.IsNullOrEmpty(sourceAreaOverride) ? sourceAreaOverride : (source.ZoneDetailId ?? "");
            result = TargetZonePool.CollectUids(spec, srcCtrl0, _zoneManager, _cardManager.PlayerManagerRef, srcArea0);
            GD.Print($"[取目标] K14 非争夺区目标（{spec.Kind}/{spec.KindAlt}）：来源控制者=P{srcCtrl0} → 候选 {result.Count} 张");
            return result;
        }
        // 2) 收集争夺区 UID：
        //   · 触发路径（默认）：来源卡所在 SLOT 下的所有争夺区（"本地区"语义，含同一 SLOT 的另一争夺区）；
        //   · 【K16】打出路径：来源卡在**手牌**（无地区），且该模式目标未声明 `IsLocalZone` → 取**全场**争夺区
        //     （如 JC063「将目标角色潜伏」没有"本地区"限制）。
        string sourceArea = !string.IsNullOrEmpty(sourceAreaOverride)
            ? sourceAreaOverride
            : (source.ZoneDetailId ?? "");
        var areaUids = new List<string>();
        // 【P3】`spec.AllAreas` = 显式"不限定本地区"（触发路径也生效；来源无地区时必需，如结附于秘社的附属）。
        bool allAreas = (spec?.AllAreas ?? false) || (allAreasIfNotLocal && !(spec?.IsLocalZone ?? false));
        if (_zoneManager != null)
        {
            foreach (var uid in _zoneManager.GetAllAreaSlotUids())
            {
                if (allAreas || _areaManager.GetSlotIndex(uid) == _areaManager.GetSlotIndex(sourceArea))
                    areaUids.Add(uid);
            }
        }
        else
        {
            areaUids.Add(sourceArea);
        }
        // 3) 合并各争夺区实例，按 Kind 过滤；默认排除来源自身，除非该效果显式允许以自身为目标（如袭击可打自己）
        bool canTargetSelf = spec?.CanTargetSelf ?? false;
        foreach (var uid in areaUids)
        {
            foreach (var inst in _cardManager.GetAreaInstancesBySlot(uid))
            {
                if (inst == source && !canTargetSelf) continue;
                // 【触发语义】"正在死亡"的卡已不再是合法目标（死亡触发窗口在它离场前打开，它仍在区内）
                if (inst.IsDying) continue;
                if (CardManager.MapInstanceToTargetKind(inst) == kind) result.Add(inst.UID);
            }
        }
        return result;
    }

    /// <summary>
    /// 结算前目标校验（D16 目标丢失规则接驳点，A+B 统一收口）：
    /// 若卡/效果需要指定目标，但结算时目标已丢失（不存在 / 地区被移除），则"不结算、卡直接进墓地"。
    /// 按堆叠条目类别判定所需目标：
    ///  - PlayToArea（角色卡）：必须校验落点地区仍存在，否则进场失败进墓地（覆盖"地区丢失导致角色进场失败"）。
    ///  - EventToGraveyard（事务卡）：若声明取目标，则目标卡/目标地区必须仍存在。
    ///  - Attachment（附属）：结附目标必须仍存在（结附于卡则目标卡存在；结附地区则地区存在）。
    /// 返回 true 表示"目标已丢失，已送墓地，调用方应直接 return 跳过结算"。
    /// 该埋点即为后续"丢失目标反击"规则的唯一挂接处，三类卡天然兼容。
    /// </summary>
    private bool ResolveEntryTargetIsLost(StackEntry entry)
    {
        if (entry == null) return false;
        // 丢失判定前 LOG：打印条目类型、目标卡/地区、入栈目标版本号、目标当前版本号、过程早判标记，便于对照后续是否真丢失
        var _preTarget = (!string.IsNullOrEmpty(entry.TargetCardUid)) ? _cardManager?.GetInstance(entry.TargetCardUid) : null;
        GD.Print($"🔍 [丢失判定-前] 条目类型={entry.Kind}，来源={entry.SourceCardUid ?? entry.SourceCard?.UID}，" +
                 $"目标卡={entry.TargetCardUid ?? "∅"}，目标地区={entry.TargetAreaId ?? "∅"}，" +
                 $"入栈版本号={entry.TargetVersionId}，目标当前版本号={_preTarget?.VersionId ?? -1}，" +
                 $"过程早判TargetWasLost={entry.TargetWasLost}");
        switch (entry.Kind)
        {
            case ResolveKind.PlayToArea:
            {
                // 角色卡：落点地区丢失 → 进场失败
                string area = entry.TargetAreaId ?? "";
                if (!string.IsNullOrEmpty(area) && (_areaManager == null || _areaManager.GetSlotIndex(area) == 0))
                {
                    GD.Print($"⚑ [D16] 角色卡落点地区 {area} 已丢失，进场失败 → 卡进墓地");
                    var c = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
                    if (c != null) _cardManager?.MoveCardToGraveyard(c);
                    return true;
                }
                return false;
            }
            case ResolveKind.EventToGraveyard:
            {
                // 【K15·多目标】逐目标版本号比对（与 `TargetCardUids` 同序）：
                // **只跳过"丢失的那一个目标"，其余照常结算**（用户 2026-09 口径）——
                // 因此这里不返回"整条不结算"，而是把已丢失的目标从条目里剔除（保留顺序与版本号对齐）后继续。
                if (entry.TargetCardUids != null && entry.TargetCardUids.Count > 0)
                    PruneLostMultiTargets(entry);
                // 事务卡：声明取目标且目标卡丢失 → 不结算进墓地
                if (!string.IsNullOrEmpty(entry.TargetCardUid))
                {
                    if (entry.TargetWasLost || IsTargetLost(entry, entry.TargetCardUid))
                    {
                        GD.Print($"⚑ [D16] 事务卡目标 {entry.TargetCardUid} 已丢失，不结算 → 卡进墓地");
                        var c = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
                        if (c != null) _cardManager?.MoveCardToGraveyard(c);
                        return true;
                    }
                }
                // 事务卡指向地区但地区丢失 → 不结算进墓地
                if (!string.IsNullOrEmpty(entry.TargetAreaId) && (_areaManager == null || _areaManager.GetSlotIndex(entry.TargetAreaId) == 0))
                {
                    GD.Print($"⚑ [D16] 事务卡目标地区 {entry.TargetAreaId} 已丢失，不结算 → 卡进墓地");
                    var c = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
                    if (c != null) _cardManager?.MoveCardToGraveyard(c);
                    return true;
                }
                return false;
            }
            case ResolveKind.Attachment:
            {
                // 附属：结附于卡则目标卡须存在；结附地区则地区须存在
                if (!string.IsNullOrEmpty(entry.TargetCardUid))
                {
                    if (entry.TargetWasLost || IsTargetLost(entry, entry.TargetCardUid))
                    {
                        GD.Print($"⚑ [D16] 附属结附目标卡 {entry.TargetCardUid} 已丢失，不结算 → 卡进墓地");
                        var c = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
                        if (c != null) _cardManager?.MoveCardToGraveyard(c);
                        return true;
                    }
                }
                else if (!string.IsNullOrEmpty(entry.TargetAreaId) && (_areaManager == null || _areaManager.GetSlotIndex(entry.TargetAreaId) == 0))
                {
                    GD.Print($"⚑ [D16] 附属结附目标地区 {entry.TargetAreaId} 已丢失，不结算 → 卡进墓地");
                    var c = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
                    if (c != null) _cardManager?.MoveCardToGraveyard(c);
                    return true;
                }
                return false;
            }
            case ResolveKind.Trigger:
            {
                // 反应触发/激活（效果型，IsCard=false）：声明取卡目标则目标卡丢失 → 仅不结算，
                // 效果来源卡【不进墓地】（规则：效果丢失目标只跳过结算，卡本身留在场上）。
                //
                // 【修复·2026-09】本判定**只在"该效果确实需要指定目标"时成立**（`Target.NeedsSelection == true`）。
                // 依据：`EffectEngine.ValidateTargetAtResolve` 对 `Kind=None || NeedsSelection==false` 的效果
                // 直接返回 `NoTarget`（跳过校验）——同一个"是否需要目标"的判据必须两处一致。
                // 此前只看"条目是否带 TargetCardUid"，而触发条目的 TargetCardUid 是**触发上下文卡**
                // （死亡触发=刚死亡的卡、反应触发=刚被使用的卡），这些卡本身会因区域移动而 BumpVersion，
                // 于是**死亡触发等"带上下文卡"的触发在结算时被整条丢弃**（`OnCardDied` 类效果永不结算）。
                bool triggerNeedsTarget = entry.EffectDataEffect?.Target?.NeedsSelection == true;
                // 【K24】玩家目标：目标不是卡实例（`TargetCardUid` 只是触发上下文卡），无"卡目标丢失"语义。
                if (PlayerTargetPool.IsPlayerSpec(entry.EffectDataEffect?.Target)) return false;
                if (triggerNeedsTarget && !string.IsNullOrEmpty(entry.TargetCardUid))
                {
                    if (entry.TargetWasLost || IsTargetLost(entry, entry.TargetCardUid))
                    {
                        GD.Print($"⚑ [D16] 反应触发目标卡 {entry.TargetCardUid} 已丢失，仅跳过结算（效果不进墓地）");
                        return true;
                    }
                }
                return false;
            }
            default:
                GD.Print($"🔍 [丢失判定-后] 条目类型={entry.Kind}，目标卡={entry.TargetCardUid ?? "∅"}：未丢失，正常结算");
                return false;   // 其余类型（赢取地区等）无"指定目标丢失"语义
        }
    }

    /// <summary>
    /// 【K15·多目标】结算前**剔除已丢失的目标**（用户 2026-09 口径：**只跳过丢失的那一个，其余照常结算**）。
    /// 逐项按 `TargetVersionIds` 同序比对版本号（实例不存在或版本号变化＝丢失），
    /// 就地移除该项（`TargetCardUids` / `TargetVersionIds` 同步）并打日志；
    /// 全部丢失时列表清空 → 效果仍结算但无卡目标（`Move` 等动作自然跳过）。
    /// 注：**地区槽丢失**（地区不存在）仍按既有单目标 D16 口径处理（整条不结算、卡进墓地）。
    /// </summary>
    private void PruneLostMultiTargets(StackEntry entry)
    {
        for (int i = entry.TargetCardUids.Count - 1; i >= 0; i--)
        {
            string uid = entry.TargetCardUids[i];
            if (string.IsNullOrEmpty(uid)) continue;
            var card = _cardManager?.GetInstance(uid);
            int ver = (entry.TargetVersionIds != null && i < entry.TargetVersionIds.Count) ? entry.TargetVersionIds[i] : -1;
            bool lost = card == null || (ver >= 0 && card.VersionId != ver);
            if (!lost) continue;
            GD.Print($"⚑ [D16] 多目标第 {i + 1} 个（{uid}）已丢失（{(card == null ? "实例不存在" : $"版本 {ver}→{card.VersionId}")}）→ **只跳过该目标**，其余照常结算");
            entry.TargetCardUids.RemoveAt(i);
            if (entry.TargetVersionIds != null && i < entry.TargetVersionIds.Count) entry.TargetVersionIds.RemoveAt(i);
        }
    }

    /// <summary>
    /// 【K15·多目标】逐目标丢失判定（供需要"是否仍有目标可用"的场景复用）：`TargetCardUids` 的每一项都要实例存在，
    /// 且（若登记了）版本号与 `TargetVersionIds` 同序一致。**任一目标丢失即返回 true**。
    /// 注意：结算路径已改为"只跳过丢失目标"（见 `PruneLostMultiTargets`），本方法只作查询用。
    /// </summary>
    private bool IsMultiTargetLost(StackEntry entry)
    {
        if (entry.TargetCardUids == null || entry.TargetCardUids.Count == 0) return false;
        for (int i = 0; i < entry.TargetCardUids.Count; i++)
        {
            string uid = entry.TargetCardUids[i];
            if (string.IsNullOrEmpty(uid)) continue;
            var card = _cardManager?.GetInstance(uid);
            if (card == null)
            {
                GD.Print($"⚑ [D16] 多目标第 {i + 1} 个（{uid}）实例已不存在 → 判丢失");
                entry.TargetWasLost = true;
                return true;
            }
            int ver = (entry.TargetVersionIds != null && i < entry.TargetVersionIds.Count) ? entry.TargetVersionIds[i] : -1;
            if (ver >= 0 && card.VersionId != ver)
            {
                GD.Print($"⚑ [D16] 多目标第 {i + 1} 个（{uid}）版本号变化（入栈 {ver} → 现 {card.VersionId}）→ 判丢失");
                entry.TargetWasLost = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 目标丢失判定（结算时唯一判定点，2026-08-11 定稿）。目标丢失相关总线已全部移除，
    /// 所有判定在此按【三段链】完成，判定为丢失时顺手置 entry.TargetWasLost 供 UI 箭头读取：
    ///  ① 实例不存在 → 丢失（兜底）。
    ///  ② 版本号比对：入栈后目标经历离场/翻面/夺控（跨区）/子类别变更等都会 BumpVersion，不一致即丢失。
    ///     例外：争夺区→争夺区移动（含跨地区、含夺控换边）【不刷版本号】，故不因此判丢，
    ///     其"本地区/敌我"语义交由第③段按入栈快照复检。
    ///  ③ TargetValidity.Check：大类区域+朝向、规则⑥本地区（发动地快照）、Friendly/Enemy 等
    ///     Restrictions 静态复检（与取目标阶段共用 CheckRestrictions，单一真相源）。
    ///  ④ RecheckTargetProperties：属性复检扩展点（保留）。
    /// </summary>
    private bool IsTargetLost(StackEntry entry, string targetUid)
    {
        if (string.IsNullOrEmpty(targetUid)) return true;
        var target = _cardManager?.GetInstance(targetUid);
        if (target == null) return true; // 实例已不存在，直接判丢失（兜底）

        // ② 状态版本号比对（D16 核心）：入栈后目标经历任何离场/翻面/子类别变更都会 BumpVersion，
        // 版本号不一致即判"目标已不是入栈时那张合法目标" → 丢失。只比是否相等，不关心变化具体内容。
        if (entry.TargetVersionId >= 0 && target.VersionId != entry.TargetVersionId)
        {
            GD.Print($"⚑ [D16] 目标 {targetUid} 状态版本号变化（入栈 {entry.TargetVersionId} → 现 {target.VersionId}），判丢失");
            entry.TargetWasLost = true;
            return true;
        }

        // ③ 结算复检（总线移除后的主判定）：版本号一致，但仍需按入栈快照复核那些"不刷版本号"的条件——
        // 规则⑥本地区（争夺区内移动不刷版本号）与 Friendly/Enemy（夺控换边不刷版本号）等。
            var desc = GetPrimaryTargetDesc(entry);
            if (desc != null)
            {
                var source = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
                var snapshot = entry.HasSourceZoneSnapshot
                    ? new Script.Manager.TargetValidity.TargetSnapshot(
                        entry.SourceZoneDetailSnapshot, entry.SourceControllerSnapshot)
                    : (Script.Manager.TargetValidity.TargetSnapshot?)null;
                var (valid, reason) = TargetValidity.Check(source, target, desc, snapshot, _playerManager);
                if (!valid)
            {
                GD.Print($"⚑ [D16] 目标 {targetUid} 结算复检不合法（{reason}）：发动地快照={entry.SourceZoneDetailSnapshot}，" +
                         $"目标当前地区={target.ZoneDetailId}，来源控制者快照={entry.SourceControllerSnapshot}，目标控制者={target.ControllerId} → 判丢失");
                entry.TargetWasLost = true;
                return true;
            }
        }

        // ④ 属性二次复检（D16 需求点 3）：版本号一致（卡未被移动/翻面），但效果声明的目标属性已不满足 → 判丢失。
        TargetLossReason propReason = RecheckTargetProperties(entry, target);
        if (propReason != TargetLossReason.None)
        {
            GD.Print($"⚑ [D16] 目标 {targetUid} 属性条件不满足（{propReason}），按丢失处理");
            entry.TargetWasLost = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 属性二次复检（D16 需求点 3）：结算时再次确认目标满足效果声明的属性条件。
    /// 条件声明在 EffectData 上（如 RequireSubCategory / RequireMinStrength 等），由效果设计在 cs/tres 配置。
    /// 当前 EffectData 尚未内置属性条件字段，此处保留扩展点：待结构落地后读取 entry 上各 EffectData
    /// 的目标属性条件并比对 target，不满足返回对应 TargetLossReason（按丢失处理）；满足或未声明返回 None。
    /// 示例：
    ///   foreach (var ed in entryEffectDataList)
    ///       if (ed.RequireSubCategory != null && !target.CurrentSubCategories.Contains(ed.RequireSubCategory))
    ///           return TargetLossReason.Illegal;
    /// </summary>
    private TargetLossReason RecheckTargetProperties(StackEntry entry, CardInstance target)
    {
        return TargetLossReason.None;
    }

    /// <summary>
    /// 取得堆叠条目的主目标描述（ITargetDesc）。
    /// 玩家卡：首条非空 TargetRule（实现 ITargetDesc）。
    /// 事务卡：EffectDataList 中首个 Kind != None 的 Target（实现 ITargetDesc）。
    /// 取不到返回 null（调用方回退旧逻辑）。
    /// </summary>
    private ITargetDesc GetPrimaryTargetDesc(StackEntry entry)
    {
        // 【K16·模式分支】入堆叠时选定了模式 → 用**该模式**的目标声明做 D16 复检（另一模式不参与）
        if (entry.ModeIndex >= 0)
        {
            var modeTarget = entry.EffectDataEffect?.TargetFor(entry.ModeIndex);
            if (modeTarget != null && modeTarget.Kind != TargetKind.None) return modeTarget;
            var srcForMode = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
            if (srcForMode?.PlayerTemplate is PlayerCardData modeTmpl)
            {
                var modeEffect = MultiTargetSlots.FindModeEffect(modeTmpl, EffectTiming.OnSorceryCast);
                var mt = modeEffect?.TargetFor(entry.ModeIndex);
                if (mt != null && mt.Kind != TargetKind.None) return mt;
            }
        }

        // 反应触发（Trigger）承载的是 EffectData，目标描述直接取 entry.EffectDataEffect.Target
        // （优先于从 PlayerCardData 列表推断，因为合击/进场触发等的 Target 写在 EffectData 上）。
        if (entry.EffectDataEffect?.Target != null && entry.EffectDataEffect.Target.Kind != TargetKind.None)
            return entry.EffectDataEffect.Target;

        var src = entry.SourceCard ?? _cardManager?.GetInstance(entry.SourceCardUid);
        if (src?.PlayerTemplate is PlayerCardData pcd)
        {
            // 事务卡（EventToGraveyard）：走效果系统 Target
            if (entry.Kind == ResolveKind.EventToGraveyard && pcd.EffectDataList != null)
            {
                foreach (var e in pcd.EffectDataList)
                    if (e?.Target != null && e.Target.Kind != TargetKind.None)
                        return e.Target;
            }
            // 玩家卡（角色/附属）：首条 TargetRule
            if (pcd.TargetRules != null)
            {
                foreach (var r in pcd.TargetRules)
                    if (r != null && r.Kind != TargetKind.None)
                        return r;
            }
        }
        return null;
    }

    /// <summary>
    /// 按结构化结算数据执行堆叠条目。当前实现：PlayToArea（角色卡放地区）。
    /// 其余类型（EventToGraveyard / Attachment / NewArea / Trigger / AmbushReveal）预留分支。
    /// </summary>
    private void ResolveEntry(StackEntry entry)
    {
        // A+B 统一收口：结算前先做目标丢失校验（D16）。目标丢失则此处已送墓地并 return。
        if (ResolveEntryTargetIsLost(entry)) return;

        // P2：同步当前回合号，供时效修正栈登记到期回合
        EE.CurrentTurn = TurnCount;

        // 【K3·死亡类·A10】触发上下文卡默认清空：只有 `ResolveKind.Trigger` 分支会按条目写入
        // （激活条目写入 null）；事务/附属/进场等结算路径一律视为"无触发上下文"，
        // 避免动作主体 `"触发卡"` 与条件判定读到上一次触发的残留上下文。
        EE.TriggerContextCard = null;

        switch (entry.Kind)
        {
            case ResolveKind.PlayToArea:
            {
                var card = entry.SourceCard ?? (_cardManager?.GetInstance(entry.SourceCardUid));
                if (card == null) { GD.PrintErr($"❌ [Resolve] 找不到源卡 {entry.SourceCardUid}"); return; }
                string area = entry.TargetAreaId ?? "";
                GD.Print($"[Resolve] 角色卡 {card.PlayerTemplate?.CardName} 结算 → 放置地区 {area}");
                _cardManager?.MoveCardToArea(card, area);
                // 进场触发已下沉至 ZoneManager.MoveToArea（D37）：非场地区→争夺区自动 Emit EnterTrigger，
                // 此处不再重复广播。
                // 新效果系统桥接：注册持续效果
                EE.RegisterContinuous(card);
                break;
            }
            case ResolveKind.EventToGraveyard:
            {
                var card = entry.SourceCard ?? (_cardManager?.GetInstance(entry.SourceCardUid));
                if (card == null) return;
                GD.Print($"[Resolve] 事务卡 {card.PlayerTemplate?.CardName} 结算 → 置入墓地");
                _cardManager?.MoveCardToGraveyard(card);
                // 新效果系统桥接（P0）：执行事务卡 F 组 EffectData（打出即结算）
                // 【K2】把"打出时选定的目标地区"一并透传：群体动作（MassMode.AllInArea）据此确定作用地区
                // 【K15】把"逐槽选定的卡目标（有序）"一并透传：多目标效果据此逐目标执行动作
                EE.ResolveSorcery(card, entry.TargetCardUid, entry.TargetVersionId, entry.TargetAreaId ?? "",
                    entry.TargetCardUids, entry.ModeIndex);
                break;
            }
            case ResolveKind.Attachment:
            {
                var card = entry.SourceCard ?? (_cardManager?.GetInstance(entry.SourceCardUid));
                if (card == null) { GD.PrintErr($"❌ [Resolve] 找不到附属源卡 {entry.SourceCardUid}"); return; }

                string targetCardUid = entry.TargetCardUid;
                string targetAreaId = entry.TargetAreaId;

                if (!string.IsNullOrEmpty(targetCardUid))
                {
                    // 结附于场上某张卡：建立双向结附关系，附属随目标所在地区
                    var target = _cardManager?.GetInstance(targetCardUid);
                    if (target == null) { GD.PrintErr($"❌ [Resolve] 附属目标卡不存在 {targetCardUid}，附属进墓地"); _cardManager?.MoveCardToGraveyard(card); return; }

                    // 结附于秘社（CouncilZone 实例）：与"结附于卡"同属双向绑定模式，
                    // 但秘社实例无 ZoneDetailId（不在争夺区），不能走 MoveCardToArea（会落入空地区 UID 而脱离管理）。
                    // 改为进 CouncilZone（MoveToCouncil），并保留 AttachedTo 绑定：
                    // 既让 UI 经 ArrangeAllAttachments 挂在秘社实例上排布，也让秘社离场时
                    // 由 DetachAttachmentsToGraveyard / ClearAttachments 自动连带附属进墓地（绑定到秘社实例语义）。
                    if (target.CurrentZone == CardZone.CouncilZone)
                    {
                        card.IsAttached = true;
                        card.AttachedTo = target;
                        target.AttachedCards.Add(card);
                        // 走与"结附于卡"完全一致的 AttachedZone 路径（双向绑定已建立）：
                        // 附属进 AttachedZone（不占争夺区、不被战力统计误算），ZoneDetailId 留空。
                        // UI 经 SyncCardUIToZone 的 AttachedZone 分支按 AttachedTo（秘社实例）挂到秘社容器，
                        // 并由 ArrangeAllAttachments 的秘社分支排布；秘社离场时由
                        // DetachAttachmentsToGraveyard / ClearAttachments 自动连带附属进墓地（绑定到秘社实例语义）。
                        _zoneManager?.MoveToAttachedZone(card, "", card.ControllerId == 0 ? 1 : card.ControllerId);
                        GD.Print($"[Resolve] 附属 {card.PlayerTemplate?.CardName} 结附于秘社 {target.CouncilTemplate?.CardName ?? target.UID}（玩家{card.ControllerId} 秘社区）");
                        break;
                    }

                    // D19-⑤：静止效果"不能被装备结附"校验（兜底）——仅当打出的是【装备】类附属卡
                    // （子类型含"装备"，含运行时授予）且目标带 CannotBeAttached 时拒绝结附。
                    // 注：正常打出阶段已由 CardManager.IsLegalPlayTarget 拦截，此处为结算兜底，
                    // 避免绕过 UI 直接入栈或逻辑漏洞导致非法结附成功。
                    if (card.CurrentSubCategories.Contains("装备")
                        && EE.HasStaticFlag(target, StaticKind.CannotBeAttached))
                    {
                        GD.Print($"❌ [Resolve] 目标 {target.PlayerTemplate?.CardName} 不能被装备结附，装备 {card.PlayerTemplate?.CardName} 进墓地");
                        _cardManager?.MoveCardToGraveyard(card);
                        return;
                    }
                    // 【JC081】"除装备与祝福外不受附属"（结算兜底，与打出阶段共用同一判定实现）
                    {
                        string whitelist = EffectEngine.GetAttachSubCategoryWhitelist(target);
                        if (whitelist != null && !EffectEngine.PassesAttachWhitelist(card, whitelist))
                        {
                            GD.Print($"❌ [Resolve] 目标 {target.PlayerTemplate?.CardName} 只受『{whitelist}』类附属结附，"
                                   + $"{card.PlayerTemplate?.CardName} 进墓地");
                            _cardManager?.MoveCardToGraveyard(card);
                            return;
                        }
                    }
                    card.IsAttached = true;
                    card.AttachedTo = target;
                    target.AttachedCards.Add(card);
                    // 附属进入 AttachedZone（非 AreaZone）：Z1 生效区判定需要 CurrentZone=AttachedZone，
                    // 否则结附角色的附属效果（RegisterPersistent/TriggerRegistry/CanActivate）不会生效。
                    // ZoneDetailId 记宿主角色所在地区，供"本地区（IsLocalZone）"判定使用。
                    // （此前误用 MoveCardToArea→AreaZone，导致结附角色的附属效果全部失效。）
                    _zoneManager?.MoveToAttachedZone(card, target.ZoneDetailId ?? "", card.ControllerId == 0 ? 1 : card.ControllerId);
                    GD.Print($"[Resolve] 附属 {card.PlayerTemplate?.CardName} 结附于 {target.PlayerTemplate?.CardName}（地区 {target.ZoneDetailId}）");
                }
                else if (!string.IsNullOrEmpty(targetAreaId))
                {
                    // 结附于地区：作为该地区独立卡处理（UI 与角色一致排在 PlayerXArea）。
                    // 必须走 MoveToAttachedZone（CurrentZone=AttachedZone），而非 MoveCardToArea（CurrentZone=AreaZone）。
                    // 原因：结附地区的附属无宿主(AttachedTo==null)，赢取地区时无法由宿主 MoveToDeckBottom 连带，
                    // 而 ResolveAreaWon 第2步专门遍历 AttachedZone 且 AttachedTo==null 的卡回库底。
                    // 若误入 AreaZone，则会被"IsAttached 跳过"且 AttachedZone 查不到 → 卡脱离区域管理而消失。
                    card.IsAttached = true;
                    card.AttachedTo = null;   // 结附地区不绑定具体卡
                    card.ZoneDetailId = targetAreaId;
                    _zoneManager?.MoveToAttachedZone(card, targetAreaId, card.ControllerId == 0 ? 1 : card.ControllerId);
                    GD.Print($"[Resolve] 附属 {card.PlayerTemplate?.CardName} 结附于地区 {targetAreaId}");
                }
                else
                {
                    GD.PrintErr($"❌ [Resolve] 附属 {card.PlayerTemplate?.CardName} 无合法目标，进墓地");
                    _cardManager?.MoveCardToGraveyard(card);
                }
                // 进场触发已下沉至 ZoneManager.MoveToArea（D37）：非场地区→争夺区自动 Emit EnterTrigger，
                // 此处不再重复广播。
                // 新效果系统桥接：注册持续效果
                EE.RegisterContinuous(card);
                break;
            }
            case ResolveKind.NewArea:
                GD.Print("[Resolve] NewArea 结算（预留：任务/地区卡未实现）");
                break;
            case ResolveKind.Trigger:
            {
                // 新效果系统触发（EffectData）：优先按其执行（实现"效果入堆叠"后由本分支结算）
                if (entry.EffectDataEffect != null)
                {
                    var src = string.IsNullOrEmpty(entry.SourceCardUid) ? null : _cardManager?.GetInstance(entry.SourceCardUid);
                    var tgt = string.IsNullOrEmpty(entry.TargetCardUid) ? null : _cardManager?.GetInstance(entry.TargetCardUid);
                    // 结算前校验来源卡版本号未变（RequireSourceVersionUnchanged）：来源卡版本号已变（离场/翻面/进手牌/进墓地等）
                    // 则跳过结算。争夺区→争夺区移动不 BumpVersion，故不影响。
                    if (entry.EffectDataEffect.RequireSourceVersionUnchanged && entry.SourceVersionId >= 0
                        && (src == null || src.VersionId != entry.SourceVersionId))
                    {
                        GD.Print($"[Resolve] 跳过结算（来源卡版本号已变）: {entry.EffectDataEffect.EffectId}, 入栈版本号={entry.SourceVersionId}, 来源当前版本号={src?.VersionId ?? -1}, src是否为null={(src == null)}");
                        break;
                    }
                    GD.Print($"[Resolve] 触发/激活效果（EffectData）结算: {entry.EffectDataEffect.EffectId}, 来源={src?.PlayerTemplate?.CardName}, 来源UID={entry.SourceCardUid}, src是否为null={(src == null)}, src.OwnerId={src?.OwnerId}, 目标={tgt?.PlayerTemplate?.CardName}");
                    // 【K23】触发类能力的次数上限（每张卡·每回合·每能力）：在**结算前**检查并登记，
                    // 使"同一批次收集到多条同源触发（如同时死亡）"也最多只结算到上限。
                    //
                    // ⚠【K23/A57·双重计数修复】**激活类条目（entry.FromActivation）必须跳过这一段**：
                    // 激活能力的计数已在"发动被接受"那一刻由 `EffectEngine.ActivateAbility` 登记
                    // （语义＝发动被接受即计数，与结算成功与否无关）。若这里再查一次，
                    // `MaxUsesPerTurn/MaxUsesPerGame = 1` 的能力**首次结算就会被自己刚登记的 1 次判成"已达上限"而整条跳过**
                    // （实测：MSJC001 行动4 首发动即被跳过；JC031/JC032/JC069/JC093 同样受影响）。
                    if (!entry.FromActivation)
                    {
                        if (src != null && src.IsAbilityLimitReached(entry.EffectDataEffect.EffectId, entry.EffectDataEffect.MaxUsesPerTurn))
                        {
                            GD.Print($"[Resolve] ⏹ 次数上限：{entry.EffectDataEffect.EffectId} 本回合已发动 " +
                                     $"{src.GetAbilityUsesThisTurn(entry.EffectDataEffect.EffectId)} 次（上限 {entry.EffectDataEffect.MaxUsesPerTurn}）→ 跳过本次结算");
                            break;
                        }
                        // 【A57】每局游戏次数上限（如 MSJC001 行动4"每局游戏只能发动一次"）：与每回合上限**同点先检查**
                        // （必须在登记之前，否则"已达上限"仍会把本回合计数 +1）
                        if (src != null && src.IsGameAbilityLimitReached(entry.EffectDataEffect.EffectId, entry.EffectDataEffect.MaxUsesPerGame))
                        {
                            GD.Print($"[Resolve] ⏹ 每局次数上限：{entry.EffectDataEffect.EffectId} 本局已发动 " +
                                     $"{src.GetAbilityUsesThisGame(entry.EffectDataEffect.EffectId)} 次（上限 {entry.EffectDataEffect.MaxUsesPerGame}）→ 跳过本次结算");
                            break;
                        }
                        if (src != null) src.MarkAbilityUsedThisTurn(entry.EffectDataEffect.EffectId);
                        if (src != null) src.MarkAbilityUsedThisGame(entry.EffectDataEffect.EffectId);
                    }
                    // entry.WinnerId：对抗获胜方玩家 ID（威名等"X对抗获胜"触发由 TriggerRegistry 收集时经
                    // StackEffectInstance.WinnerId → PushTriggerToStack 写入），透传给 ResolveEffect 供 WinnerIsSelf 判定。
                    // 【K24】entry.TargetPlayerId：入堆叠时锁定的目标玩家号（激活类玩家目标效果用）。
                    // 【K3·死亡类·A10】把"引发本次触发的卡"（＝`TriggerContext.ContextCard`，如刚死亡的角色）
                    // 下发给引擎：动作主体 `"触发卡"` / `"死亡角色"` 据此解析（JC105 把死亡角色封印到目标角色上）。
                    // 来源：`StackEffectInstance.TargetCardUid`（收集时写入 ContextCard.UID）。注意
                    // `entry.TargetCardUid` 在有玩家选定目标时会被覆盖为目标卡，故必须从 `StackEffect` 取。
                    EE.TriggerContextCard = string.IsNullOrEmpty(entry.StackEffect?.TargetCardUid)
                        ? null
                        : _cardManager?.GetInstance(entry.StackEffect.TargetCardUid);
                    EE.ResolveEffect(entry.EffectDataEffect, src, tgt, entry.TargetAreaId ?? "", entry.WinnerId, entry.TargetVersionId, entry.EventAmount, null, entry.ModeIndex, entry.TargetPlayerId);
                    break;
                }

                var trig = entry.StackEffect;
                GD.Print($"[Resolve] 触发效果结算分支（兜底）: {entry.EffectText}, Source={trig?.Source?.PlayerTemplate?.CardName}");
                // 旧系统 ICardTriggerBehavior.Resolve 路径已随旧系统迁移删除；
                // 所有触发现由 EffectDataEffect 分支经 EffectEngine.Instance.ResolveEffect 结算（见上文）。
                // 此处仅作兜底日志，不再有脚本结算分支。
                break;
            }
            case ResolveKind.AmbushReveal:
            {
                var card = entry.SourceCard ?? (_cardManager?.GetInstance(entry.SourceCardUid));
                if (card == null) { GD.PrintErr($"❌ [Resolve] 找不到现身源卡 {entry.SourceCardUid}"); return; }
                string areaId = entry.TargetAreaId ?? card.ZoneDetailId ?? "";
                // 回地区：fromZone=Stack != AreaZone → ZoneManager.MoveToArea 自动触发 EnterTrigger（D37，回地区进场为正常情况）
                _cardManager?.MoveCardToArea(card, areaId);
                // 额外广播现身触发信号（UncoverTrigger），满足核心需求 6.3「视作角色/附属」的现身时机
                _triggerEmitPoints?.UncoverTrigger(card, card.ControllerId, areaId);
                GD.Print($"[Resolve] 暗藏者 {card.PlayerTemplate?.CardName} 现身结算 → 回地区 {areaId}（EnterTrigger + UncoverTrigger）");
                break;
            }
            case ResolveKind.AreaWon:
            {
                if (_areaManager != null)
                    _areaManager.ResolveAreaWon(entry.WonAreaUid, entry.WinnerId);
                else
                    GD.PrintErr("❌ [Resolve] AreaWon 但 AreaManager 未注入");
                break;
            }
            default:
                GD.Print($"[Resolve] 未处理的结算类型 {entry.Kind}");
                break;
        }
    }

    /// <summary>
    /// 玩家打出卡牌。完整流程：阶段检查→行动权校验→能量校验→扣费→移动卡牌到目标区域
    /// 返回 true 表示出牌成功，UI 层据此决定是否移动 UI 元素。
    /// targetUid：目标地区 UID（角色落点 / 附属结附地区 / 事务指向地区时使用）。
    /// targetCardUid：目标卡 UID（附属结附于卡 / 事务指向场上卡时使用，可为空）。
    ///
    /// 【K15·多目标】`multiChoices`：声明了 `EffectData.TargetSlots` 的卡（如 JC021「选择目标本方角色和/或目标敌方角色，
    /// 移动到目标地区」）逐槽选择的结果。非空时以它为准（`targetUid`/`targetCardUid` 由它派生），
    /// 并在**付费之前**完成合法性校验（必选槽 / 至少 `MinChosenSlots` 个 / 禁止原地移动…）——不合法则整次打出失败。
    /// </summary>
    /// <summary>
    /// 【P3】卡是否具有某关键字：运行期授予（`HasKeyword`）∪ 卡面固有声明（`HasInherentKeyword`）。
    /// 与 `CardInstance.HasPublicKeyword` 同款双层判定——印在卡面上的词条走模板固有，
    /// 由效果授予的词条走运行时集合；效果失效时判定自然转 false。
    /// 消费点：隐匿（打出门禁）、非资产（建立资产门禁）。
    /// </summary>
    private static bool HasCardKeyword(CardInstance card, PlayerCardData tmpl, string keyword)
        => card != null && (card.HasKeyword(keyword) || (tmpl?.HasInherentKeyword(keyword) ?? false));

    /// <summary>
    /// 【K15·多目标·联机】服务端驱动的多目标打出流程（用户 2026-09 口径）：
    /// **逐槽下发 `MultiTargetSlot` 请求 → 最后下发 `ConfirmTargets` 确认 → 确认后才 `PlayCard` 入堆叠**。
    ///  · 每槽候选由服务端（`MultiTargetSlots.CollectSlot`）权威计算后随请求下发（含展示引用）；
    ///  · 回包 `SelectedUid` 为空串＝该槽留空（可留空的槽）；`Cancelled=true`＝取消整次打出；
    ///  · 组合不合法（必选槽未选 / 少于 `MinChosenSlots` / 禁止原地移动…）→ 整次打出失败（不扣费）；
    ///  · 确认面板被取消/超时 → 整次打出失败（不扣费、卡留手牌）；
    ///  · 期间持有原子锁（与单机模态窗口语义一致），并在完成后触发
    ///    `OnAsyncSettlementCompleted`（联机由 `MatchHost` 据此下发最新快照）。
    /// 返回 true 表示"已完成一次多目标打出"（成功入堆叠）；false＝被玩家取消或校验失败。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> PlayMultiTargetCardAsync(int playerId, string cardUid)
    {
        var instance = _cardManager?.GetInstance(cardUid);
        var tmpl = instance?.PlayerTemplate;
        var effect = MultiTargetSlots.FindMultiTargetEffect(tmpl, EffectTiming.OnSorceryCast);
        if (effect == null || InputGateway == null)
            return PlayCard(playerId, cardUid);   // 非多目标卡 / 无网关：退回既有路径

        var choices = new System.Collections.Generic.List<TargetChoice>();
        for (int i = 0; i < effect.TargetSlots.Count; i++) choices.Add(TargetChoice.Empty);
        string contextId = $"mt_{playerId}_{TurnCount}_{++_multiTargetContextSeq}";

        EnterAtomic();
        try
        {
            for (int i = 0; i < effect.TargetSlots.Count; i++)
            {
                var cand = MultiTargetSlots.CollectSlot(effect, i, playerId, choices, _zoneManager,
                    _cardManager, _playerManager, instance, _areaManager);
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.MultiTargetSlot,
                    SourceUid = instance?.UID,
                    SlotIndex = i,
                    SlotCount = effect.TargetSlots.Count,
                    SlotOptional = cand.Optional,
                    SlotLabel = cand.Label,
                    Prompt = $"请选择{cand.Label}（第 {i + 1}/{effect.TargetSlots.Count} 个目标槽）",
                    ForceChoice = !cand.Optional,   // 必选槽：客户端不提供"留空"（但仍可整次取消）
                    OrderContextId = contextId,
                    OptionUids = cand.IsArea ? cand.AreaUids : UidList(cand.Cards),
                    OptionLabels = cand.IsArea ? AreaLabels(cand.AreaUids) : CardLabels(cand.Cards),
                };
                if (!cand.IsArea)
                    foreach (var c in cand.Cards)
                        if (c != null) req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));

                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled)
                {
                    GD.Print($"[K15 多目标·联机] 玩家{playerId} 取消/超时（槽 {i}），整次打出中止：{tmpl?.CardName}");
                    return false;
                }
                if (string.IsNullOrEmpty(res.SelectedUid))
                {
                    if (!cand.Optional)
                    {
                        GD.Print($"[K15 多目标·联机] 『{cand.Label}』是必选槽但回包为空 → 中止");
                        return false;
                    }
                    choices[i] = TargetChoice.Empty;
                    continue;
                }
                choices[i] = cand.IsArea
                    ? TargetChoice.Area(res.SelectedUid)
                    : TargetChoice.Card(res.SelectedUid);
            }

            string bad = MultiTargetSlots.Validate(effect, choices, playerId, _zoneManager, _cardManager,
                _playerManager, instance, _areaManager);
            if (bad != null)
            {
                GD.Print($"[K15 多目标·联机] 组合不合法：{bad} → 整次打出中止");
                return false;
            }

            // 确认面板：把逐槽结果下发，玩家确认后才真的入堆叠
            var confirm = new Script.Net.ChoiceRequest
            {
                PlayerId = playerId,
                RequestKind = Script.Net.ChoiceRequest.Kind.ConfirmTargets,
                SourceUid = instance?.UID,
                Prompt = "确认目标后加入堆叠",
                ForceChoice = true,
                OrderContextId = contextId,
                OptionLabels = SlotSummary(effect, choices),
                OptionUids = MultiTargetSlots.ChosenCardUids(effect, choices),
            };
            foreach (var uid in confirm.OptionUids)
            {
                var c = _cardManager?.GetInstance(uid);
                if (c != null) confirm.OptionCardRefs.Add(global::ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));
            }
            var cres = await InputGateway.RequestChoice(playerId, confirm);
            if (cres == null || cres.Cancelled || !cres.Confirmed)
            {
                GD.Print($"[K15 多目标·联机] 玩家{playerId} 未确认（取消/超时）→ 不打出 {tmpl?.CardName}");
                return false;
            }
        }
        finally
        {
            // 【要点】选目标期间持原子锁（对齐单机模态窗口语义），但**必须在调用 `PlayCard` 之前释放**：
            // `PlayCard` 自己会 `EnterAtomic`，且开头就拒"原子操作进行中"的调用。
            ExitAtomicWhenIdle();
        }

        bool ok = PlayCard(playerId, cardUid, null, null, choices);
        GD.Print($"[K15 多目标·联机] 确认通过 → PlayCard={ok}（{MultiTargetSlots.Describe(effect, choices, _cardManager)}）");
        // 联机：本流程异步于意图处理返回，完成后主动通知服务端下发最新快照
        OnAsyncSettlementCompleted?.Invoke();
        return ok;
    }

    /// <summary>【K15】多目标选择上下文序号（客户端据此复用同一面板）。</summary>
    private int _multiTargetContextSeq;

    /// <summary>
    /// 【K10·额外费用·联机】服务端驱动的"带额外费用选择"的打出流程（用户口径 **A23/A33**）：
    /// 两类需要**玩家本人选择**的额外费用在联机侧原先无路可走（客户端只发裸 `PlayCard` 意图，
    /// 额外费用参数无处携带），本方法把选择**回环给该玩家**后再续算：
    ///  · **JC049「额外费用：牺牲一个角色」**：候选由服务端权威枚举（`GetExtraCostSacrificeCandidates`）→
    ///    下发 **`ChoiceRequest.Kind.SelectTarget`**（A33 指定**复用**该请求类型，非新窗口类型；场上卡高亮点选）→
    ///    回包 `SelectedUid` 必须命中候选（服务端**权威复检**，不在候选内 → 整次中止）；
    ///  · **JC081「额外费用：展示任意数量的手牌」**：候选＝打出者当前手牌 → 下发
    ///    **`Kind.SelectHandCards`**（**多选**，`SelectedUids` 有序、**可空**＝不展示）→
    ///    `Confirmed=true` 才继续；`Cancelled=true` ＝取消整次打出（A23：取消＝不打出、卡不入堆叠、不扣费）。
    ///
    /// 期间持原子锁（与单机模态窗口语义一致），**必须在 `PlayCard` 之前释放**（`PlayCard` 自己会 `EnterAtomic`）。
    /// 无合法牺牲对象 ⇒ **该卡不能打出**（A21）；完成后触发 `OnAsyncSettlementCompleted`（联机据此下发快照）。
    /// 返回 true＝已完成一次打出（成功入堆叠）；false＝被取消/校验失败/无合法候选。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> PlayWithExtraCostAsync(int playerId, string cardUid,
        string targetUid, string targetCardUid)
    {
        var instance = _cardManager?.GetInstance(cardUid);
        var tmpl = instance?.PlayerTemplate;
        if (tmpl == null || tmpl.ExtraCost == null || InputGateway == null)
            return PlayCard(playerId, cardUid, targetUid, targetCardUid);   // 无额外费用 / 无网关：退回既有路径

        string extraSacrificeUid = null;
        var extraRevealUids = new System.Collections.Generic.List<string>();
        bool cancelled = false;

        EnterAtomic();
        try
        {
            // ---------- ① JC049：牺牲一个角色（必须支付；无合法对象 ⇒ 不能打出）----------
            if (NeedsExtraCostPlayerChoice(tmpl))
            {
                var cands = GetExtraCostSacrificeCandidates(instance);
                if (cands.Count == 0)
                {
                    GD.Print($"❌ [K10 额外费用·联机] {tmpl.CardName} 无合法牺牲对象 → 不能打出（A21）");
                    return false;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
                    SourceUid = instance.UID,
                    Prompt = $"额外费用：请选择要为「{tmpl.CardName}」牺牲的角色",
                    ForceChoice = true,   // 必须支付：客户端不提供"跳过"（仍可整次取消）
                    OptionUids = UidList(cands),
                    OptionLabels = CardLabels(cands),
                };
                foreach (var c in cands)
                    if (c != null) req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));

                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled)
                {
                    GD.Print($"[K10 额外费用·联机] 玩家{playerId} 取消/超时（选择牺牲对象）→ 不打出 {tmpl.CardName}");
                    cancelled = true;
                }
                else if (string.IsNullOrEmpty(res.SelectedUid) || cands.Find(c => c != null && c.UID == res.SelectedUid) == null)
                {
                    // 服务端权威复检：回包必须命中服务端给出的候选（防越权/失效选择）
                    GD.PrintErr($"[K10 额外费用·联机] 回包 {res.SelectedUid} 不在合法候选内 → 整次打出中止（fail-closed）");
                    cancelled = true;
                }
                else
                {
                    extraSacrificeUid = res.SelectedUid;
                    GD.Print($"[K10 额外费用·联机] 玩家{playerId} 选择牺牲 {extraSacrificeUid}");
                }
            }

            // ---------- ② JC081：展示任意数量的手牌（可不展示＝全额支付；取消＝不打出）----------
            if (!cancelled && NeedsExtraCostRevealChoice(tmpl))
            {
                var cands = GetExtraCostRevealCandidates(playerId, instance);
                if (cands.Count > 0)
                {
                    var req = new Script.Net.ChoiceRequest
                    {
                        PlayerId = playerId,
                        RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCards,
                        SourceUid = instance.UID,
                        Prompt = $"额外费用：选择要为「{tmpl.CardName}」展示给对手的手牌",
                        ForceChoice = false,   // 允许"一张都不展示"（＝无减免，照样能打出）
                        NumberMax = cands.Count,
                        OptionUids = UidList(cands),
                    };
                    foreach (var c in cands)
                    {
                        if (c == null) continue;
                        req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));
                        // 显示名带"合格 -1 / 不合格"标记（**判定由服务端给出**，客户端不重算规则）
                        req.OptionLabels.Add((c.PlayerTemplate?.CardName ?? c.UID)
                            + (IsQualifiedRevealCard(tmpl, c) ? "（合格 -1）" : "（不合格）"));
                    }

                    var res = await InputGateway.RequestChoice(playerId, req);
                    if (res == null || res.Cancelled)
                    {
                        GD.Print($"[K10 额外费用·联机] 玩家{playerId} 取消/超时（展示手牌）→ 不打出 {tmpl.CardName}");
                        cancelled = true;
                    }
                    else
                    {
                        // 服务端权威复检：只接受候选内、且不重复的 UID（不合法项直接丢弃，不因此中止整次打出）
                        var legal = new System.Collections.Generic.HashSet<string>(UidList(cands));
                        if (res.SelectedUids != null)
                            foreach (var uid in res.SelectedUids)
                            {
                                if (string.IsNullOrEmpty(uid) || !legal.Contains(uid) || extraRevealUids.Contains(uid)) continue;
                                extraRevealUids.Add(uid);
                            }
                        GD.Print($"[K10 额外费用·联机] 玩家{playerId} 展示 {extraRevealUids.Count} 张（候选 {cands.Count} 张）");
                    }
                }
                else
                {
                    GD.Print($"[K10 额外费用·联机] {tmpl.CardName} 无手牌可展示 → 按不展示处理（全额支付）");
                }
            }
        }
        finally
        {
            ExitAtomicWhenIdle();
        }

        if (cancelled)
        {
            OnAsyncSettlementCompleted?.Invoke();   // 联机：取消也要下发一次最新快照（恢复 UI 可操作状态）
            return false;
        }

        bool ok = PlayCard(playerId, cardUid, targetUid, targetCardUid, null, -1,
            extraSacrificeUid, extraRevealUids.Count > 0 ? extraRevealUids : null);
        GD.Print($"[K10 额外费用·联机] 选择完成 → PlayCard={ok}（牺牲={extraSacrificeUid ?? "无"}，展示={extraRevealUids.Count} 张）");
        OnAsyncSettlementCompleted?.Invoke();
        return ok;
    }

    /// <summary>【K15】卡候选 → UID 列表。</summary>
    private static System.Collections.Generic.List<string> UidList(System.Collections.Generic.List<CardInstance> cards)
    {
        var list = new System.Collections.Generic.List<string>();
        if (cards != null) foreach (var c in cards) if (c != null) list.Add(c.UID);
        return list;
    }

    /// <summary>【K15】卡候选 → 显示名（与 UID 同序）。</summary>
    private static System.Collections.Generic.List<string> CardLabels(System.Collections.Generic.List<CardInstance> cards)
    {
        var list = new System.Collections.Generic.List<string>();
        if (cards != null) foreach (var c in cards) list.Add(c?.PlayerTemplate?.CardName ?? c?.UID ?? "?");
        return list;
    }

    /// <summary>【K15】地区候选 → 显示名（与 UID 同序）："地区N"。</summary>
    private System.Collections.Generic.List<string> AreaLabels(System.Collections.Generic.List<string> areaUids)
    {
        var list = new System.Collections.Generic.List<string>();
        if (areaUids != null)
            foreach (var uid in areaUids)
                list.Add($"地区{_areaManager?.GetSlotIndex(uid) ?? 0}");
        return list;
    }

    /// <summary>【K15】逐槽摘要（确认面板显示用，与槽序一致）。</summary>
    private System.Collections.Generic.List<string> SlotSummary(EffectData data, System.Collections.Generic.List<TargetChoice> choices)
        => new System.Collections.Generic.List<string> { MultiTargetSlots.Describe(data, choices, _cardManager) };

    /// <summary>
    /// 【K16·模式分支·单机】"选择一项"类卡牌的打出入口：**先让玩家选定模式（付费之前）** → 选该模式的目标
    /// → 再走入 `PlayCard(..., modeIndex)`。
    /// 口径（用户 2026-09，与 JC063 的 FAQ 一致）：模式在**打出并付费时**选定，入堆叠后**不可更改**。
    /// 无 UI（测试/headless）时确定性兜底：不选模式直接尝试打出 → 会被 `PlayCard` 的模式校验拒绝（绝不静默代选）。
    /// </summary>
    public void BeginModeCardPlay(int playerId, string cardUid)
    {
        var instance = _cardManager?.GetInstance(cardUid);
        var tmpl = instance?.PlayerTemplate;
        var effect = MultiTargetSlots.FindModeEffect(tmpl, EffectTiming.OnSorceryCast);
        if (effect == null)
        {
            PlayCard(playerId, cardUid);   // 非模式卡：原路径
            return;
        }
        var hooks = EE?.UIHooks;
        if (hooks == null)
        {
            GD.Print($"[TurnManager] ⚠ 模式卡 {tmpl?.CardName} 无 UI：确定性兜底直接尝试打出（缺模式选择将被拒绝）");
            PlayCard(playerId, cardUid);
            return;
        }
        hooks.PromptEffectMode(instance, effect, chosenIndex =>
        {
            if (chosenIndex < 0 || chosenIndex >= effect.Modes.Count)
            {
                GD.Print($"[TurnManager] 玩家{playerId} 取消了 {tmpl?.CardName} 的模式选择（不打出，卡留手牌）");
                return;
            }
            var spec = effect.ModeAt(chosenIndex)?.Target;
            bool needTarget = spec != null && spec.Kind != TargetKind.None && spec.NeedsSelection;
            if (!needTarget)
            {
                PlayCard(playerId, cardUid, null, null, null, chosenIndex);
                return;
            }
            // 该模式的目标：走既有单机选目标通道（候选＝该模式目标声明的合法候选）
            var valid = GetTargetUidsForSpec(spec, instance, null, allAreasIfNotLocal: true);
            if (valid.Count == 0)
            {
                GD.Print($"[TurnManager] 模式 {chosenIndex + 1} 无合法目标 → 不打出 {tmpl?.CardName}");
                return;
            }
            if (_view == null)
            {
                GD.Print($"[TurnManager] ⚠ 模式卡 {tmpl?.CardName} 无 View：无法取目标，不打出");
                return;
            }
            // 单机选目标窗口需要一个"触发实例"承载展示信息（模式分支不是触发，故构造仅用于展示的临时实例）
            var modeDisplay = new Script.Data.Effects.StackEffectInstance
            {
                Source = instance,
                OwnerPlayerId = playerId,
                ControllerId = instance?.ControllerId ?? playerId,
                EffectDataRef = effect,
                IsAreaTarget = spec.Kind == TargetKind.Area,
            };
            _view.RequestTargetSelection(modeDisplay, valid, uid =>
            {
                if (string.IsNullOrEmpty(uid))
                {
                    GD.Print($"[TurnManager] 玩家{playerId} 未选目标 → 不打出 {tmpl?.CardName}");
                    return;
                }
                var targetInst = _cardManager?.GetInstance(uid);
                bool isArea = targetInst == null && (_areaManager?.GetSlotIndex(uid) ?? 0) > 0;
                if (isArea) PlayCard(playerId, cardUid, uid, null, null, chosenIndex);
                else PlayCard(playerId, cardUid, null, uid, null, chosenIndex);
            });
        });
    }

    /// <summary>
    /// 【K16·模式分支·联机】服务端驱动：① 下发 `Kind.SelectEffectMode` 让玩家选定模式 →
    /// ② 若该模式需要目标，按该模式的目标声明下发 `Kind.SelectTarget`/`SelectZoneCard` →
    /// ③ `PlayCard(..., modeIndex)`（付费/入堆叠都在选定之后）。
    /// 取消/超时 → 整次打出中止（不扣费、卡留手牌）；完成后经 `OnAsyncSettlementCompleted` 让服务端下发快照。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> PlayModeCardAsync(int playerId, string cardUid)
    {
        var instance = _cardManager?.GetInstance(cardUid);
        var tmpl = instance?.PlayerTemplate;
        var effect = MultiTargetSlots.FindModeEffect(tmpl, EffectTiming.OnSorceryCast);
        if (effect == null || InputGateway == null)
            return PlayCard(playerId, cardUid);   // 非模式卡/无网关：退回既有路径

        int chosen = -1;
        string targetUid = null;
        EnterAtomic();
        try
        {
            var modeReq = new Script.Net.ChoiceRequest
            {
                PlayerId = playerId,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectEffectMode,
                SourceUid = instance?.UID,
                Prompt = $"{tmpl?.CardName}：选择一项",
                ForceChoice = true,
                Amount = effect.Modes.Count,
            };
            for (int i = 0; i < effect.Modes.Count; i++)
                modeReq.OptionLabels.Add(effect.ModeAt(i)?.Label ?? $"选项{i + 1}");
            var mres = await InputGateway.RequestChoice(playerId, modeReq);
            if (mres == null || mres.Cancelled)
            {
                GD.Print($"[K16 模式·联机] 玩家{playerId} 取消/超时未选模式 → 不打出 {tmpl?.CardName}");
                return false;
            }
            chosen = (mres.SelectedIndices != null && mres.SelectedIndices.Count > 0) ? mres.SelectedIndices[0] : -1;
            if (effect.ModeAt(chosen) == null)
            {
                GD.Print($"[K16 模式·联机] 回包模式序号非法（{chosen}）→ 不打出");
                return false;
            }
            GD.Print($"[K16 模式·联机] 玩家{playerId} 选定模式 {chosen + 1}：{effect.ModeAt(chosen).Label}");

            var spec = effect.ModeAt(chosen).Target;
            if (spec != null && spec.Kind != TargetKind.None && spec.NeedsSelection)
            {
                var valid = GetTargetUidsForSpec(spec, instance, null, allAreasIfNotLocal: true);
                if (valid.Count == 0)
                {
                    GD.Print($"[K16 模式·联机] 模式 {chosen + 1} 无合法目标 → 不打出");
                    return false;
                }
                bool zonePile = TargetZonePool.IsZonePileSpec(spec);
                var treq = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = zonePile
                        ? Script.Net.ChoiceRequest.Kind.SelectZoneCard
                        : Script.Net.ChoiceRequest.Kind.SelectTarget,
                    SourceUid = instance?.UID,
                    OptionUids = valid,
                    Prompt = zonePile
                        ? $"请选择{TargetZonePool.DescribeZone(spec.Kind)}中的目标牌"
                        : $"请为 {tmpl?.CardName} 选择目标",
                    ForceChoice = true,
                };
                if (zonePile)
                {
                    foreach (var uid in valid)
                    {
                        var inst = _cardManager?.GetInstance(uid);
                        if (inst != null) treq.OptionCardRefs.Add(global::ZoneManager.ProjectCard(inst, inst.CurrentZone, inst.OwnerId));
                    }
                }
                var tres = await InputGateway.RequestChoice(playerId, treq);
                if (tres == null || tres.Cancelled || string.IsNullOrEmpty(tres.SelectedUid))
                {
                    GD.Print($"[K16 模式·联机] 未选目标（取消/超时）→ 不打出 {tmpl?.CardName}");
                    return false;
                }
                targetUid = tres.SelectedUid;
            }
        }
        finally
        {
            // 与多目标一致：选目标期间持原子锁，**调 PlayCard 前必须释放**（PlayCard 自身会 EnterAtomic）
            ExitAtomicWhenIdle();
        }

        bool isArea = string.IsNullOrEmpty(targetUid) || _cardManager?.GetInstance(targetUid) == null;
        bool ok = isArea
            ? PlayCard(playerId, cardUid, targetUid, null, null, chosen)
            : PlayCard(playerId, cardUid, null, targetUid, null, chosen);
        GD.Print($"[K16 模式·联机] 选定模式 {chosen + 1} 后 PlayCard={ok}");
        OnAsyncSettlementCompleted?.Invoke();
        return ok;
    }

    /// <summary>
    /// 【K15·多目标】单机"多目标事务卡"打出入口：逐槽取目标 → **确认面板** → 确认后才走入 `PlayCard`
    /// （用户 2026-09 口径：选完目标要在 UI 面板确认，确认后才把效果加入堆叠）。
    /// 非多目标卡直接走原 `PlayCard` 路径，行为不变。
    /// 无 UI（测试/headless）时确定性兜底：不带选择直接尝试打出——会被 `PlayCard` 的多目标校验拒绝（绝不静默跳目标）。
    /// </summary>
    public void BeginMultiTargetPlay(int playerId, string cardUid)
    {
        var instance = _cardManager?.GetInstance(cardUid);
        var tmpl = instance?.PlayerTemplate;
        var effect = MultiTargetSlots.FindMultiTargetEffect(tmpl, EffectTiming.OnSorceryCast);
        if (effect == null)
        {
            PlayCard(playerId, cardUid);   // 非多目标卡：原路径（目标由 View 点选后另行传入）
            return;
        }
        var hooks = EE?.UIHooks;
        if (hooks == null)
        {
            GD.Print($"[TurnManager] ⚠ 多目标卡 {tmpl?.CardName} 无 UI：确定性兜底直接尝试打出（缺少逐槽选择将被拒绝）");
            PlayCard(playerId, cardUid, null, null, null);
            return;
        }
        var choices = new System.Collections.Generic.List<TargetChoice>();
        for (int i = 0; i < effect.TargetSlots.Count; i++) choices.Add(TargetChoice.Empty);
        GD.Print($"[TurnManager] 【K15 多目标】开始逐槽取目标：{tmpl?.CardName}（{effect.TargetSlots.Count} 个槽）");
        hooks.PromptMultiTargets(instance, effect,
            (slotIndex, current) => MultiTargetSlots.CollectSlot(effect, slotIndex, playerId, current,
                _zoneManager, _cardManager, _playerManager, instance, _areaManager),
            final =>
            {
                if (final == null)
                {
                    GD.Print($"[TurnManager] 玩家{playerId} 取消了 {tmpl?.CardName} 的多目标选择（不打出，卡留手牌）");
                    return;
                }
                GD.Print($"[TurnManager] 【K15 多目标】确认面板通过：{MultiTargetSlots.Describe(effect, final, _cardManager)}");
                PlayCard(playerId, cardUid, null, null, final);
            });
    }

    /// <summary>
    /// 【K10·额外费用】该卡打出前需要**玩家做选择**（目前只有"牺牲一个角色"这一类，JC049）：
    /// View 据此决定"先弹选择窗、再把选择交给 `PlayCard`"。其余额外费用（JC135 的 X 由目标费用决定、
    /// JC081 的可选展示）不需要这个前置选择。
    /// </summary>
    public bool NeedsExtraCostPlayerChoice(PlayerCardData tmpl)
        => tmpl?.ExtraCost != null && tmpl.ExtraCost.Kind == ExtraCostKind.SacrificeCharacter;

    /// <summary>
    /// 【K10·额外费用】该卡是否有"**展示手牌减费**"这一类可选额外费用（JC081）：
    /// View 据此弹"手牌多选 → 确认/取消"窗（取消＝不打出、不扣任何费用）。
    /// </summary>
    public bool NeedsExtraCostRevealChoice(PlayerCardData tmpl)
        => tmpl?.ExtraCost != null && tmpl.ExtraCost.Kind == ExtraCostKind.RevealHandCards;

    /// <summary>【K10·额外费用】展示手牌的候选（＝打出者当前手牌，按手牌顺序）。
    /// **排除 `source`（正在打出的这张牌本身）**：卡文"展示任意数量的**手牌**"指手牌中**其它**的牌——
    /// 正在打出的牌此刻虽然还在手牌区（付费发生在入堆叠之前），但它已经"被展示/被打出"，
    /// 不能拿自己给自己减费（否则每打一张 JC081 都白减 1：JC081 自身组织就是【圣贤】）。</summary>
    public System.Collections.Generic.List<CardInstance> GetExtraCostRevealCandidates(int playerId,
        CardInstance source = null)
    {
        var all = _zoneManager?.GetZoneCards(CardZone.Hand, playerId)
                  ?? new System.Collections.Generic.List<CardInstance>();
        if (source == null) return all;
        var list = new System.Collections.Generic.List<CardInstance>();
        foreach (var c in all)
            if (c != null && !ReferenceEquals(c, source)) list.Add(c);
        return list;
    }

    /// <summary>【K10·额外费用】某张手牌是否算"合格展示牌"（命中 `RevealFilter`，如 JC081 的
    /// `组织:圣贤|域:神圣`）——与付费阶段**同一实现** `CostModFilter.Matches`，View 展示减免数用它。</summary>
    public bool IsQualifiedRevealCard(PlayerCardData tmpl, CardInstance handCard)
    {
        if (tmpl?.ExtraCost == null || handCard == null) return false;
        return CostModFilter.Matches(tmpl.ExtraCost.RevealFilter, handCard,
            handCard.PlayerTemplate?.CardName, handCard.PlayerTemplate?.Organization);
    }

    /// <summary>
    /// 【K10·额外费用】"牺牲一个角色"作**打出费用**的候选（＝本方场上正面角色；口径与 P3·K22 牺牲代价
    /// **同一实现** `EffectEngine.GetSacrificeCandidates`，不会分叉）。空集 ⇒ 该卡**不能打出**（A21）。
    /// </summary>
    public System.Collections.Generic.List<CardInstance> GetExtraCostSacrificeCandidates(CardInstance card)
    {
        var empty = new System.Collections.Generic.List<CardInstance>();
        var tmpl = card?.PlayerTemplate;
        if (!NeedsExtraCostPlayerChoice(tmpl) || EE == null) return empty;
        var spec = tmpl.ExtraCost.Sacrifice ?? new SacrificeCostSpec();
        return EE.GetSacrificeCandidates(card, spec) ?? empty;
    }

    /// <summary>
    /// 【K10·额外费用·统一实现】**打出**与**暗藏者现身**共用的额外费用解析：
    /// 把卡声明的 `PlayerCardData.ExtraCost` 解析成四个量（牺牲对象 / 展示的手牌 / 额外费用 / 一次性减免），
    /// 并在解析期做完整校验（fail-closed）：
    ///  · `SacrificeCharacter`（JC049，**必须支付**）：候选＝本方场上正面角色；指定 UID 必须命中候选，
    ///    未指定 → 确定性兜底取第一个（与"牺牲代价"同款纪律）；**无候选 → 失败**；
    ///  · `PayTargetCost`（JC135，**必须支付**）：X＝目标印刷费用；**无目标 → 失败**（故"现身"路径下
    ///    该形态无法满足 ⇒ 拒绝现身，绝不放行未付额外费用的现身）；
    ///  · `RevealHandCards`（JC081，**可选**）：展示的牌必须在**该玩家手牌**内且**不含本卡自身**（防自我减费）；
    ///    命中 `RevealFilter` 的张数＝一次性减免；未提供且 `Mandatory` → 失败。
    /// 返回 false 时 `failReason` 给出原因（调用方自行打日志/拒绝本次动作）。
    /// </summary>
    private bool TryResolveExtraCost(PlayerCardData tmpl, CardInstance instance, int playerId, string targetCardUid,
        string extraSacrificeUid, System.Collections.Generic.IList<string> extraRevealUids,
        out CardInstance extraSacrifice, out System.Collections.Generic.List<CardInstance> extraRevealCards,
        out int extraMana, out int extraDiscount, out string failReason)
    {
        extraSacrifice = null;
        extraRevealCards = new System.Collections.Generic.List<CardInstance>();
        extraMana = 0;
        extraDiscount = 0;
        failReason = null;
        var extraSpec = tmpl?.ExtraCost;
        if (extraSpec == null || extraSpec.Kind == ExtraCostKind.None) return true;

        switch (extraSpec.Kind)
        {
            case ExtraCostKind.SacrificeCharacter:
            {
                var sacSpec = extraSpec.Sacrifice ?? new SacrificeCostSpec();
                var candidates = EE.GetSacrificeCandidates(instance, sacSpec);
                if (candidates.Count == 0)
                {
                    failReason = "无合法牺牲对象（该额外费用必须支付）";
                    return false;
                }
                if (!string.IsNullOrEmpty(extraSacrificeUid))
                {
                    extraSacrifice = candidates.Find(c => string.Equals(c.UID, extraSacrificeUid, System.StringComparison.Ordinal));
                    if (extraSacrifice == null)
                    {
                        failReason = $"指定的牺牲候选 {extraSacrificeUid} 不合法";
                        return false;
                    }
                }
                else
                {
                    extraSacrifice = candidates[0];
                    GD.Print($"[TurnManager] ⚠ 【K10 额外费用】未指定牺牲候选 → 确定性兜底取第一个合法候选（{extraSacrifice.PlayerTemplate?.CardName}）");
                }
                return true;
            }
            case ExtraCostKind.PayTargetCost:
            {
                var payTarget = string.IsNullOrEmpty(targetCardUid) ? null : _cardManager?.GetInstance(targetCardUid);
                if (payTarget == null)
                {
                    failReason = "需先选定目标（X＝目标印刷费用），当前无目标";
                    return false;
                }
                extraMana = Math.Max(0, payTarget.PlayerTemplate?.Cost ?? 0);
                GD.Print($"[TurnManager] 【K10 额外费用】{tmpl.CardName}：X＝目标 {payTarget.PlayerTemplate?.CardName} 的印刷费用 {extraMana}");
                return true;
            }
            case ExtraCostKind.RevealHandCards:
            {
                if (extraRevealUids != null && extraRevealUids.Count > 0)
                {
                    int qualified = 0;
                    foreach (var uid in extraRevealUids)
                    {
                        var c = _cardManager?.GetInstance(uid);
                        if (c == null || c.CurrentZone != CardZone.Hand || c.ControllerId != playerId)
                        {
                            failReason = $"展示候选 {uid} 不在你的手牌";
                            return false;
                        }
                        // 正在打出的本卡自身不计入"展示的手牌"（防自我减费，与候选枚举同一口径）
                        if (ReferenceEquals(c, instance)) continue;
                        extraRevealCards.Add(c);
                        if (CostModFilter.Matches(extraSpec.RevealFilter, c, c.PlayerTemplate?.CardName, c.PlayerTemplate?.Organization))
                            qualified++;
                    }
                    extraDiscount = qualified;
                    GD.Print($"[TurnManager] 【K10 额外费用】{tmpl.CardName}：展示 {extraRevealCards.Count} 张，其中合格 {qualified} 张 → 减免 {extraDiscount}");
                    return true;
                }
                if (extraSpec.Mandatory)
                {
                    failReason = "需展示任意数量手牌（未提供）";
                    return false;
                }
                return true;   // 可选：不展示＝全额支付
            }
            default:
                failReason = $"未登记的额外费用种类「{extraSpec.Kind}」";
                return false;
        }
    }
    public bool PlayCard(int playerId, string cardUid, string targetUid = null, string targetCardUid = null,
        System.Collections.Generic.IList<TargetChoice> multiChoices = null, int modeIndex = -1,
        string extraSacrificeUid = null, System.Collections.Generic.IList<string> extraRevealUids = null,
        bool freePlay = false, bool asIfQuick = false)
    {
        // 游戏已结束：拒绝一切新出牌（胜利/失败已结算，未结算堆叠已丢弃）
        if (_playerManager != null && _playerManager.IsGameOver)
        {
            GD.Print($"⛔ [TurnManager] 游戏已结束，拒绝玩家{playerId}打出卡牌 UID={cardUid}");
            return false;
        }

        // D-1 熔断：堆叠累计压入达上限时禁止打出新卡（置于 EnterAtomic 之前，避免占用原子锁后返回）
        if (_stackManager != null && _stackManager.IsCircuitBroken)
        {
            GD.PrintErr($"⛔ [TurnManager] 堆叠已熔断（累计 {_stackManager.SequenceCount}/{StackManager.MaxStackSequence}），拒绝玩家{playerId}打出卡牌 UID={cardUid}");
            return false;
        }

        // 规则层原子锁（核心需求 7.3）：原子操作进行中（如其它出牌/结算的触发窗口未关闭）拒绝新出牌
        if (_atomicLock)
        {
            GD.Print($"⛔ [TurnManager] 原子操作进行中，拒绝玩家{playerId}打出卡牌 UID={cardUid}");
            return false;
        }
        // 2. 行动权校验
        if (PriorityPlayerId != playerId)
        {
            GD.Print($"❌ [TurnManager] 当前行动权在玩家{PriorityPlayerId}，玩家{playerId}不能出牌");
            return false;
        }

        // 3. 查找卡牌实例（前移：门禁与后续校验共用）
        var instance = _cardManager?.GetInstance(cardUid);
        if (instance == null)
        {
            GD.Print($"❌ [TurnManager] 未找到卡牌实例: {cardUid}");
            return false;
        }

        var tmpl = instance.PlayerTemplate;

        // 3.5)【K27·来源门禁】打出的来源区校验——**服务端权威**，不再只靠候选投影/UI 约束：
        //   默认只允许"从**手牌**打出"；声明了 `PlayableFromGraveyard`（卡文"**墓地行动**"）的卡
        //   额外放行"从**墓地**正面打出"（JC085 复生行尸）。其余来源（牌库/堆叠/场上/封印区/资产区）一律拒绝，
        //   避免联机下越权把任意位置的卡打出来。
        if (instance.CurrentZone != CardZone.Hand)
        {
            bool graveyardPlay = instance.CurrentZone == CardZone.Graveyard
                                 && tmpl != null && tmpl.PlayableFromGraveyard;
            if (!graveyardPlay)
            {
                GD.Print($"⛔ [TurnManager] 来源区不合法：{tmpl?.CardName ?? cardUid} 在 {instance.CurrentZone}（只允许从手牌打出）");
                return false;
            }
            GD.Print($"[TurnManager] 🪦 墓地行动：{tmpl.CardName} 从墓地正面打出（仍按手牌同款规则：付费/忠诚/行动步骤）");
        }

        // 3.x 快速打出门禁（D9 卡级）
        // 所有档均要求"有优先权"（步骤2已校验 PriorityPlayerId == playerId）。
        // 档位划分：
        //  A档（有门禁）：先手限 ActionFirst / 后手限 ActionSecond，且堆叠必须空
        //                —— 角色/附属恒为 A档；事务卡默认亦为 A档。
        //  B档（限行动阶段）：ActionFirst/ActionSecond 任意、有优先权即可，不需堆叠空
        //                —— 事务卡 LimitToActionPhaseOnly=true 时进入。
        //  C档（取消门禁）：任意有优先权时（含响应窗口、堆叠非空）均可打出
        //                —— 事务卡含 PlayTiming.Response 效果时进入；角色/附属永不进入。
        bool isSorceryCard = (tmpl.CategoryFlag & CardCategory.Sorcery) != 0;
        // 【K28·JC119】"**视作具有【快速】且不需要支付其费用**"（由效果授予的免费打出）：
        //  · `asIfQuick` → **跳过阶段门禁**（不受行动阶段限制；"快速"＝不受行动步骤约束）；
        //  · `freePlay` → **费用置 0**（跳过付费，含额外费用），但**忠诚照常校验**（卡文"但仍需满足忠诚需求"）。
        if (asIfQuick)
        {
            GD.Print($"[TurnManager] ⚡ 视作【快速】打出：{tmpl.CardName}（由效果授予，跳过行动阶段门禁）");
        }
        else if (!isSorceryCard)
        {
            // 角色/附属 → A档：复用基础行动门禁（堆叠空 + 自有优先权 + 自己专属行动步骤）
            if (!CanEnterBasicActionMask(playerId))
            {
                return false;
            }
        }
        else if (tmpl.CanPlayAsResponse())
        {
            // 事务卡含 Response 效果 → C档：仅优先权已校验，放行（响应窗口/堆叠非空均可）
        }
        else if (tmpl.LimitToActionPhaseOnly)
        {
            // 事务卡 B档：限行动阶段、不需堆叠空
            if (CurrentPhase != TurnPhase.ActionFirst && CurrentPhase != TurnPhase.ActionSecond)
            {
                return false;
            }
        }
        else
        {
            // 事务卡默认 A档：复用基础行动门禁
            if (!CanEnterBasicActionMask(playerId))
            {
                return false;
            }
        }

        // 3.x【K26】阶段排除：卡上任一效果声明了"不可打出的阶段"且当前正处于该状态 → 该卡不可打出。
        // 例：JC008 灵能激发（快速行动）"不能在分配战斗伤害阶段使用"（＝伤害分配窗口打开的那一小段）。
        if (PhaseGateRule.IsCardPlayExcluded(tmpl, this))
        {
            GD.Print($"❌ [TurnManager] {tmpl.CardName} 不能在「{PhaseGateRule.DescribeCardExclusion(tmpl, this)}」打出（效果声明了阶段排除）");
            return false;
        }

        // 4. 不可从手牌正常打出的卡（费用为空 = -1，只能特殊方式进场）
        if (tmpl.Cost < 0)
        {
            GD.Print($"❌ [TurnManager] 该卡无法从手牌正常打出（Cost={tmpl.Cost}）");
            return false;
        }

        // 4.1 【P3·隐匿】"该角色不能从手牌付费正面打出到争夺区"（用户规则文本）。
        // 仅限制这一种打出方式：秘密派遣（暗藏者背面进场）、效果直接放置等均不受限制。
        // 判定：运行期授予的关键字 ∪ 卡面固有声明（与"公开"同款双层判定）；只对**角色卡**生效（卡文写的是"该角色"）。
        if ((tmpl.CategoryFlag & CardCategory.Character) != 0 && HasCardKeyword(instance, tmpl, "隐匿"))
        {
            GD.Print($"❌ [TurnManager] {tmpl.CardName} 具有【隐匿】，不能从手牌付费正面打出到争夺区（只能以秘密派遣等其它方式进场）");
            return false;
        }

        // 4.2【P3·K21·JC064 戒严】"**所有非【国家机构】的角色牌不可正面打出在本地区**"：
        // 只限制"**正面付费打出到该地区**"这一种进场方式（FAQ：秘密派遣与"其他方式放置进场"不受限——
        // 这两条路径不经本方法）。落点地区＝本次打出的目标地区（`targetUid`）。
        if ((tmpl.CategoryFlag & CardCategory.Character) != 0 && !string.IsNullOrEmpty(targetUid)
            && EE?.IsEntryBlockedByOrgRule(instance, targetUid) == true)
        {
            GD.Print($"❌ [TurnManager] {tmpl.CardName} 受『戒严』类静止规则限制：非指定组织角色不可正面打出在该地区");
            return false;
        }

        // 【2026-09·原子操作期间的玩家选择】"**打出卡牌**"整段都是原子操作（核心需求 7.1/7.3）：
        // 从原子序的**第一步（确认忠诚）**起持锁，覆盖 选目标/额外费用/付费/入堆叠，
        // 确保打出过程中（含任何"要玩家选择"的前置窗）都不给响应权、也不会有新的行动/触发效果入堆叠。
        // 注意：**入站门禁**（游戏结束/熔断/原子锁拒绝/优先权/来源区/时机/阶段/费用可打出/隐匿/戒严）
        // 必须留在 EnterAtomic **之前**——否则会被自己的锁拒绝（自锁）。
        // 用 try/finally 收口，保证本段内所有早退分支都释放锁。
        EnterAtomic();
        try
        {

        // 5. 忠诚校验（6.1）：颜色 + 领域需求必须同时满足（允许冗余）
        if (!_playerManager.CanPayLoyalty(playerId, tmpl))
        {
            GD.Print($"❌ [TurnManager] 忠诚不足，无法打出 {tmpl.CardName}");
            return false;
        }

        // 5.x【K16·模式分支】声明了 `Modes` 的卡（卡文"选择一项"）：**必须在付费之前选定模式**，
        // 且所选模式的序号必须合法（用户 2026-09 口径：模式在打出时选定，入堆叠后不可更改）。
        EffectMode chosenMode = null;
        {
            var modeEffect = MultiTargetSlots.FindModeEffect(tmpl, isSorceryCard ? EffectTiming.OnSorceryCast : (EffectTiming?)null);
            if (modeEffect != null)
            {
                chosenMode = modeEffect.ModeAt(modeIndex);
                if (chosenMode == null)
                {
                    GD.Print($"❌ [TurnManager] {tmpl.CardName} 需要先选择一项模式（modeIndex={modeIndex} 非法），拒绝打出");
                    return false;
                }
                GD.Print($"[TurnManager] 【K16 模式分支】{tmpl.CardName} 选定模式 {modeIndex + 1}/{modeEffect.Modes.Count}：{chosenMode.Label}");
            }
        }

        // 5.y【K15·多目标】声明了多目标槽位的卡：先校验逐槽选择是否合法，再进入付费。
        // 口径（用户 2026-09）：至多 2 个目标（本方角色 / 敌方角色，各自可留空），**至少选 1 个**；
        // 目标地区由玩家选，**已经在该目的地的角色不出现在候选里**（禁止原地移动）。
        // 校验失败 → 整次打出失败（不扣费、不进堆叠），把"非法组合"挡在付费之前。
        EffectData multiEffect = null;
        System.Collections.Generic.List<string> multiCardUids = null;
        if (isSorceryCard)
        {
            multiEffect = MultiTargetSlots.FindMultiTargetEffect(tmpl, EffectTiming.OnSorceryCast);
            if (multiEffect != null)
            {
                if (multiChoices == null)
                {
                    GD.Print($"❌ [TurnManager] {tmpl.CardName} 需要多目标选择（未提供逐槽选择），拒绝打出");
                    return false;
                }
                string reason = MultiTargetSlots.Validate(multiEffect, multiChoices, playerId,
                    _zoneManager, _cardManager, _playerManager, instance, _areaManager);
                if (reason != null)
                {
                    GD.Print($"❌ [TurnManager] {tmpl.CardName} 多目标不合法：{reason} → 拒绝打出");
                    return false;
                }
                multiCardUids = MultiTargetSlots.ChosenCardUids(multiEffect, multiChoices);
                targetUid = MultiTargetSlots.ChosenAreaUid(multiEffect, multiChoices);
                targetCardUid = (multiCardUids.Count > 0) ? multiCardUids[0] : null;
                GD.Print($"[TurnManager] 【K15 多目标】{tmpl.CardName}：{MultiTargetSlots.Describe(multiEffect, multiChoices, _cardManager)}");
            }
        }

        // 5.z【K10·额外费用】"**这张牌打出费用的一部分**"（用户 2026-09 口径 A21–A24）：
        //   · 时机：**付费阶段**支付、**卡入堆叠之前**付清；**不在堆叠里结算、不可被响应**（类比万智牌的额外费用）；
        //   · 三种形态：① `SacrificeCharacter`（JC049：**必须支付**，**无合法牺牲对象则整张不能打出**，
        //     牺牲者由**打出者自己选择**）；② `PayTargetCost`（JC135：X＝**先选定的目标**的印刷费用，
        //     0 费目标 → **不支付**；合法目标费用均大于可用费用 → 不能打出）；
        //     ③ `RevealHandCards`（JC081：**可选**、一次性减免，不展示＝全额支付）。
        //   · 选择输入：单机由 Arena 预选后随参数传入（`extraSacrificeUid`/`extraRevealUids`）；
        //     未指定时牺牲走**确定性兜底**（第一个合法候选，与既有"牺牲代价"同款纪律）、展示视为"不展示"。
        CardInstance extraSacrifice = null;
        var extraRevealCards = new System.Collections.Generic.List<CardInstance>();
        int extraMana = 0;         // JC135：额外支付的无色费用（＝目标印刷费用）
        int extraDiscount = 0;     // JC081：因展示而减掉的费用（一次性，只作用于本张）
        // 【K10·统一实现】额外费用解析抽成 TryResolveExtraCost：**打出**（PlayCard）与**暗藏者现身**
        // （RevealCovert）共用同一份校验/兜底/减免口径（用户口径：现身无特殊说明时同样需校验额外费用）。
        if (!TryResolveExtraCost(tmpl, instance, playerId, targetCardUid, extraSacrificeUid, extraRevealUids,
                out extraSacrifice, out extraRevealCards, out extraMana, out extraDiscount, out string extraFail))
        {
            GD.PrintErr($"❌ [TurnManager] 【K10 额外费用】{tmpl.CardName}：{extraFail} → 拒绝打出");
            return false;
        }


    // 6. 费用支付（6.2）：横置资产区卡牌产生无色费用
        // 【2026-09】原子锁已在"确认忠诚"之前置位（见上方 try 段），此处不再重复 EnterAtomic；
        // 原有的"从实际开始改变游戏状态起加锁"口径已被用户口径取代：**打出卡的原子操作包含
        // 确认忠诚 / 选目标 / 额外费用 / 付费 全部前置步骤**。
        // P2：先减玩家级时效费用修正（如 JC012 的"下一张帷幕守望牌费用-1"）
        // 【P1】改传实例：使"子类别:/域:/有域图标"类过滤串（如 JC042/JC112）能正确命中。
        EE.CurrentTurn = TurnCount;
        // 【K10】额外费用并入本次费用：总费用 ＝（基础费用 − 时效减免 − 展示减免）+ 额外支付的 X
        // 【K28·JC119】免费打出：费用与额外费用一律为 0（**忠诚已在上方校验、照常生效**）
        int cost = freePlay
            ? 0
            : tmpl.Cost - EE.GetCostModification(playerId, instance, tmpl.CardName, tmpl.Organization) - extraDiscount;
        if (cost < 0) cost = 0;
        int totalCost = freePlay ? 0 : cost + extraMana;
        if (freePlay) GD.Print($"[TurnManager] 🆓 免费打出：{tmpl.CardName}（不需支付费用，忠诚已校验）");

        // 【K10】额外费用①（牺牲）：**牺牲＝死亡**（照常广播死亡时机），在**入堆叠之前**、原子锁内支付。
        // 放在费用校验之后：确保"费用不足"这类失败**不会白牺牲**（校验段已保证 totalCost 可支付）。
        if (totalCost > 0 && _playerManager.GetAvailableMana(playerId) < totalCost)
        {
            GD.Print($"❌ [TurnManager] 可用费用不足！需要 {totalCost}（含额外费用 {extraMana}），当前 {_playerManager.GetAvailableMana(playerId)}");
            ExitAtomicWhenIdle();   // 修复：解锁，避免原子锁卡死
            return false;
        }
        // 【K10】额外费用②（展示手牌）：给**对手**公开展示（`UiNotificationBus.CardRevealed`），
        // 一次性减免已在 totalCost 里扣掉；展示不改变任何规则状态。
        foreach (var rev in extraRevealCards)
        {
            GD.Print($"[TurnManager] 🔎 【K10 额外费用】玩家{playerId} 展示 {rev.PlayerTemplate?.CardName}");
            Script.Manager.UiNotificationBus.Instance?.NotifyCardRevealed(rev, playerId);
        }
        if (extraSacrifice != null)
        {
            GD.Print($"[TurnManager] ✝ 【K10 额外费用】{tmpl.CardName}：牺牲 {extraSacrifice.PlayerTemplate?.CardName}（打出者选择，不可被响应）");
            _cardManager?.ResolveDeath(extraSacrifice, CardManager.DeathCause.Sacrifice);
        }

        if (totalCost > 0)
        {
            if (!_playerManager.SpendMana(playerId, totalCost))
            {
                GD.Print($"❌ [TurnManager] 费用支付失败");
                ExitAtomicWhenIdle();
                return false;
            }
            GD.Print($"[TurnManager] 玩家{playerId} 支付 {totalCost} 费用（含额外费用 {extraMana}），剩余可用 {_playerManager.GetAvailableMana(playerId)}");
            // 一次性费用修正（"下一张"语义）：本张已成功支付并使用减免，消费移除该修正项（整回合项不移除）
            EE.ConsumeCostModification(playerId, instance, tmpl.Organization);
        }

        // 资产横置状态已变化，通知 UI 刷新
        OnAssetsChanged?.Invoke();

        // 7. 入堆叠（费用/执行分离）：按卡类别构造结构化结算数据，等待响应窗口结算
        if (_stackManager != null)
        {
            // 【JC119】成功打出即清除"刚由封印回手"标记（该标记只用于识别"本卡死亡时刚回手的那批封印牌"）
            instance.ReturnedFromSealHostUid = null;
            var entry = new StackEntry(playerId, instance);
            entry.ControllerId = instance.ControllerId;   // 箭头按控制者着色（D31）
            entry.TargetAreaId = targetUid ?? "";
            entry.TargetCardUid = targetCardUid ?? "";
            entry.Targets.Clear();
            // 【K16·模式分支】把"打出时选定的模式"写入条目：入堆叠后不可更改，结算只执行该模式
            if (chosenMode != null) entry.ModeIndex = modeIndex;
            // 【K15·多目标】把逐槽选择写进条目（**按卡槽对齐**，留空槽保留空串，逐目标版本号由 StackManager.Push 登记）
            if (multiEffect != null)
            {
                entry.TargetCardUids = MultiTargetSlots.SlotAlignedCardUids(multiEffect, multiChoices);
                if (multiCardUids.Count > 0) entry.TargetCardUid = multiCardUids[0];
            }
            if (!string.IsNullOrEmpty(targetCardUid))
                GD.Print($"🎯 [取目标] 出牌 {instance.PlayerTemplate?.CardName}({cardUid}) 取目标卡 {targetCardUid}");
            else if (!string.IsNullOrEmpty(targetUid))
                GD.Print($"🎯 [取目标] 出牌 {instance.PlayerTemplate?.CardName}({cardUid}) 取目标地区 {targetUid}");

            bool isAttachment = (tmpl.CategoryFlag & CardCategory.Attachment) != 0;
            bool isSorcery = (tmpl.CategoryFlag & CardCategory.Sorcery) != 0;

            // A+B：打出阶段接入通用目标拦截系统（统一收口，三类卡都走）。
            // 事务/附属的"卡目标"选择交由此系统裁决（IsLegalPlayTarget 按 AllowedTargets 粗筛），
            // 不再由效果系统自行实现。角色卡落点地区也应校验地区合法存在（提前拦截"地区丢失"）。
            // 【K15】多目标卡不走这里：其逐槽合法性已在**付费之前**由 `MultiTargetSlots.Validate` 完成
            // （比卡级 AllowedTargets 粗筛更严，且含"至少 N 个槽/禁止原地移动"等跨槽约束）。
            if (multiEffect != null)
            {
                GD.Print($"[TurnManager] 【K15 多目标】{tmpl.CardName} 目标已按槽校验通过，跳过卡级目标粗筛");
            }
            else if (!string.IsNullOrEmpty(targetCardUid))
            {
                // 指向场上某张卡：交给通用目标拦截系统裁决
                var tgtInst = _cardManager.GetInstance(targetCardUid);
                if (tgtInst == null || !_cardManager.IsLegalPlayTarget(instance, tgtInst))
                {
                    GD.Print($"❌ [TurnManager] 通用目标拦截系统：卡 {tmpl.CardName} 的卡目标不合法（{targetCardUid}），拒绝入堆叠");
                    ExitAtomicWhenIdle();
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(targetUid))
            {
                // 仅指向地区（角色落点 / 附属结附地区 / 事务指向地区）：校验地区仍存在
                if (_areaManager == null || _areaManager.GetSlotIndex(targetUid) == 0)
                {
                    GD.Print($"❌ [TurnManager] 通用目标拦截系统：目标地区 {targetUid} 不存在，拒绝入堆叠");
                    ExitAtomicWhenIdle();
                    return false;
                }
            }

            if (isAttachment)
            {
                // 附属：结算时结附于目标（地区或卡）。targetCardUid 优先（结附于卡），
                // 否则 targetUid 视为结附地区。
                entry.Kind = ResolveKind.Attachment;
                if (!string.IsNullOrEmpty(entry.TargetCardUid))
                    entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Card, CardUid = entry.TargetCardUid });
                else if (!string.IsNullOrEmpty(entry.TargetAreaId))
                    entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Area, AreaSlotId = ParseAreaSlotId(entry.TargetAreaId), AreaUid = entry.TargetAreaId });
                GD.Print($"[TurnManager] 附属 {tmpl.CardName} 进入堆叠（结附目标={(entry.TargetCardUid ?? entry.TargetAreaId)}），等待结算");
            }
            else if (isSorcery)
            {
                // 事务：结算后置入墓地。若含目标则记录（初期仅占位，不执行效果）。
                entry.Kind = ResolveKind.EventToGraveyard;
                if (multiEffect != null)
                {
                    // 【K15 多目标】箭头引用：地区槽 + 每个卡目标各一条
                    if (!string.IsNullOrEmpty(entry.TargetAreaId))
                        entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Area, AreaSlotId = ParseAreaSlotId(entry.TargetAreaId), AreaUid = entry.TargetAreaId });
                    foreach (var uid in entry.TargetCardUids)
                        if (!string.IsNullOrEmpty(uid))
                            entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Card, CardUid = uid });
                }
                else if (!string.IsNullOrEmpty(entry.TargetCardUid))
                    entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Card, CardUid = entry.TargetCardUid });
                else if (!string.IsNullOrEmpty(entry.TargetAreaId))
                    entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Area, AreaSlotId = ParseAreaSlotId(entry.TargetAreaId), AreaUid = entry.TargetAreaId });
                GD.Print($"[TurnManager] 事务 {tmpl.CardName} 进入堆叠（{(entry.Targets.Count > 0 ? "带目标" : "无目标")}），等待结算");
            }
            else
            {
                // 默认/角色：结算时放置目标地区己方争夺区
                entry.Kind = ResolveKind.PlayToArea;
                if (!string.IsNullOrEmpty(entry.TargetAreaId))
                    entry.Targets.Add(new StackTargetRef { Kind = StackTargetRef.RefKind.Area, AreaSlotId = ParseAreaSlotId(entry.TargetAreaId), AreaUid = entry.TargetAreaId });
                GD.Print($"[TurnManager] 卡牌 {tmpl.CardName} 进入堆叠，等待响应窗口结算");
            }

            // 记录目标入栈快照（控制者+子类别），供"结算前变化"类丢失判定比对待入栈时状态（过程记忆）
            CaptureTargetSnapshot(entry);

            _stackManager.Push(entry);
        }
        else
        {
            // 兜底（StackManager 未注入）：直接移动，避免卡死
            GD.PrintErr("[TurnManager] StackManager 未注入，回退为直接移动");
            _cardManager?.MoveCardToArea(instance, targetUid ?? "");
        }

        // 8. 复位让过窗口（打出动作重置响应窗口：天生优先玩家重新取得行动权，需重新 A 让过 + B 让过）
        ResetPassWindow();

        // 9. 优先权分配（统一口径见 AssignPriorityAfterPlay）
        AssignPriorityAfterPlay(playerId, "出牌入堆叠");

        GD.Print($"[TurnManager] ✅ 玩家{playerId} 成功打出卡牌 UID={cardUid}, 目标={(targetUid ?? "无")}");
        OnCardPlayed?.Invoke(playerId, cardUid, targetUid);

        // 打出卡牌后：通过监听总线广播"卡被打出"事件，由 TriggerRegistry 统一收集反应触发（如 XG01 打出事务牌时抓牌）
        // 走新效果系统（EffectEngine）入堆叠，与进场/激活触发同套结算规则
        // 反应事件：被打出的卡既是引发者(SourceCard)也是被检查对象(ContextCard)；TriggeringPlayerId 供敌我向条件判定
        _gameEventBus?.Emit(Script.Data.Effects.GameEvent.OnCardPlayed,
            new Script.Data.Effects.TriggerContext
            {
                ContextCard = instance,
                SourceCard = instance,
                TriggeringPlayerId = playerId,
            });

        // 同步段结束：若无模态窗口（触发排序/选目标）保持打开，则原子操作已完整结束，解除规则层锁
        ExitAtomicWhenIdle();
        return true;
        }
        finally
        {
            // 【2026-09】"打出卡牌"原子段结束（触发排序/选目标窗仍打开时由模态计数维持锁；
            // 本段内早退分支已各自调用过 ExitAtomicWhenIdle，此处再调为幂等兜底，杜绝锁泄漏）
            ExitAtomicWhenIdle();
        }
    }

    // ============================================================
    // 暗藏者现身（快速能力，AmbushReveal）
    // ============================================================
    /// <summary>
    /// 暗藏者现身（Reveal）：玩家有行动权时双击己方争夺区内背面朝上的暗藏者发动。
    /// 规则要点（核心需求 6.3 / 你确认的设计）：
    ///   1) 视为"打出卡牌"行为，需满足该卡正常打出的所有条件（忠诚需求 + 原始费用）；
    ///      **额外费用同样校验**（用户 2026-09 口径：无特殊说明时现身也要校验，JC 卡包内全部需要）——
    ///      与 `PlayCard` 共用 `TryResolveExtraCost`：`RevealHandCards`（JC081）展示手牌可**减免本次现身费用**；
    ///      必须支付类若在现身路径无法满足（如 `PayTargetCost` 需要目标）则**拒绝现身**（fail-closed）。
    ///      选择输入由 View 预选后经 `extraSacrificeUid`/`extraRevealUids` 传入（联机经网关回环）。
    ///   2) 不改变所在地区：现身地区默认 = 原本暗藏的地区（不弹地区选择）。
    ///   3) 进堆叠：与打出卡牌走相同入栈路径（响应窗口让过结算），箭头指向原地区。
    ///      进堆叠时即翻面（IsCovert=false），堆叠区内保持正面，结算回地区仍是正面。
    ///   4) 回地区触发进场触发（EnterTrigger）是正常情况；同时额外广播现身触发（UncoverTrigger）。
    ///   5) 门禁 = 仅"有行动权(PriorityPlayerId==playerId)"，不要求行动阶段、不要求堆叠空（快速能力）。
    ///   6) 费用/忠诚/付费均按控制者(ControllerId)归属（与激活/机动一致，D28~D36）；
    ///      并校验发动者即控制者，防止第三方误触发。
    ///   7) 直接尝试发动，不满足则提示失败（不弹确认）。
    /// </summary>
    public bool RevealCovert(int playerId, string cardUid,
        string extraSacrificeUid = null, System.Collections.Generic.IList<string> extraRevealUids = null)
    {
        // 游戏已结束：拒绝一切快速能力发动
        if (_playerManager != null && _playerManager.IsGameOver) return false;

        // D-1 熔断：堆叠累计压入达上限时禁止发动
        if (_stackManager != null && _stackManager.IsCircuitBroken)
        {
            GD.Print($"❌ [TurnManager] 堆叠已达熔断上限，拒绝暗藏者现身");
            return false;
        }

        // 原子锁：原子操作执行期间（触发排序/选目标窗口）拒绝插队（D18/D24 三重保险之一）
        if (IsAtomicLocked)
        {
            GD.Print($"❌ [TurnManager] 原子操作进行中，拒绝暗藏者现身");
            return false;
        }

        // 门禁：仅校验行动权（快速能力，不要求行动阶段/堆叠空）
        // 注意：必须用 PriorityPlayerId（当前实际持有优先权者），而非 GetPriorityPlayer()（阶段天生优先玩家）。
        // 响应窗口中 PriorityPlayerId 会随让过翻转，先手玩家在 ActionSecond 也可能持有优先权，
        // 用 GetPriorityPlayer() 会错误拒绝先手玩家的现身（见 D9 优先权校验统一）。
        if (PriorityPlayerId != playerId)
        {
            return false;
        }

        var instance = _cardManager?.GetInstance(cardUid);
        if (instance == null)
        {
            GD.Print($"❌ [TurnManager] 找不到卡 {cardUid}，拒绝暗藏者现身");
            return false;
        }

        // 必须是争夺区内背面朝上的暗藏者
        if (!instance.IsCovert || instance.CurrentZone != CardZone.AreaZone)
        {
            GD.Print($"❌ [TurnManager] 卡 {instance.PlayerTemplate?.CardName} 非争夺区内暗藏者，拒绝现身");
            return false;
        }

        // 发动者必须即控制者（防第三方误触发）
        int controller = instance.ControllerId;
        if (controller != playerId)
        {
            GD.Print($"❌ [TurnManager] 玩家{playerId} 非暗藏者控制者(={controller})，拒绝现身");
            return false;
        }

        // 【P3·K21】静止规则：场上存在"带锁定标记的暗藏者不能现身"的来源（JC122 哈克·亚当斯）→ 拒绝现身。
        if (EE?.IsRevealBlocked(instance) == true)
        {
            GD.Print($"❌ [TurnManager] {instance.PlayerTemplate?.CardName} 带锁定标记且场上存在禁止翻面的来源（JC122 类）→ 拒绝现身");
            return false;
        }

        var tmpl = instance.PlayerTemplate;
        if (tmpl == null || tmpl.Cost < 0)
        {
            GD.Print($"❌ [TurnManager] 暗藏者模板异常/不可正常打出，拒绝现身");
            return false;
        }

        // 忠诚需求校验（按控制者归属，D28/D29）
        if (!_playerManager.CanPayLoyalty(controller, tmpl))
        {
            GD.Print($"❌ [TurnManager] 玩家{controller} 忠诚需求不满足，拒绝暗藏者现身");
            return false;
        }

        // 进入原子操作：从费用支付起加锁
        EnterAtomic();

        // 【K10·额外费用】现身**同样要校验额外费用**（用户口径：无特殊说明时都需要校验；JC 卡包内全部需要）。
        // 与 `PlayCard` 共用 `TryResolveExtraCost`（同一份校验/兜底/减免口径，不会分叉）。
        //  · JC081（RevealHandCards，可选）：展示的手牌可减免**本次现身费用**（一次性）；
        //  · 其它形态（如 JC049/JC135 的必须支付类）若在现身路径无法满足（无目标等）→ **拒绝现身**（fail-closed）。
        if (!TryResolveExtraCost(tmpl, instance, controller, targetCardUid: null,
                extraSacrificeUid, extraRevealUids,
                out CardInstance extraSacrifice, out var extraRevealCards,
                out int extraMana, out int extraDiscount, out string extraFail))
        {
            GD.PrintErr($"❌ [TurnManager] 【K10 额外费用】{tmpl.CardName} 现身：{extraFail} → 拒绝现身（fail-closed）");
            ExitAtomicWhenIdle();
            return false;
        }

        // 费用校验与支付（按控制者，原始 Cost − 时效减免 − 展示减免 + 额外支付的 X）
        EE.CurrentTurn = TurnCount;
        // 【P3】现身费用：用**实例重载**（子类别类过滤可用）并走**"现身"付费通道**
        // （JC033「所有本方暗藏者，付费翻面时其所需费用减少1」）。
        int cost = tmpl.Cost - EE.GetCostModification(controller, instance, tmpl.CardName, tmpl.Organization, "现身")
                   - extraDiscount;
        if (cost < 0) cost = 0;
        int totalCost = cost + extraMana;
        // 【K10】先"展示手牌"（给对手公开；不改变规则状态）→ 再"牺牲"（＝死亡，照常广播死亡时机）
        // → 最后付费；与 `PlayCard` 的先后口径一致（都发生在**入堆叠之前**、原子锁内）。
        foreach (var rev in extraRevealCards)
        {
            GD.Print($"[TurnManager] 🔎 【K10 额外费用】玩家{controller} 现身时展示 {rev.PlayerTemplate?.CardName}");
            Script.Manager.UiNotificationBus.Instance?.NotifyCardRevealed(rev, controller);
        }
        if (extraSacrifice != null)
        {
            GD.Print($"[TurnManager] ✝ 【K10 额外费用】{tmpl.CardName} 现身：牺牲 {extraSacrifice.PlayerTemplate?.CardName}（控制者选择，不可被响应）");
            _cardManager?.ResolveDeath(extraSacrifice, CardManager.DeathCause.Sacrifice);
        }
        if (totalCost > 0)
        {
            if (_playerManager.GetAvailableMana(controller) < totalCost)
            {
                GD.Print($"❌ [TurnManager] 暗藏者现身费用不足！需要 {totalCost}（含额外费用 {extraMana}），当前 {_playerManager.GetAvailableMana(controller)}");
                ExitAtomicWhenIdle();
                return false;
            }
            if (!_playerManager.SpendMana(controller, totalCost))
            {
                GD.Print($"❌ [TurnManager] 暗藏者现身费用支付失败");
                ExitAtomicWhenIdle();
                return false;
            }
            GD.Print($"[TurnManager] 玩家{controller} 支付暗藏者现身费用 {totalCost}（含额外费用 {extraMana}），剩余可用 {_playerManager.GetAvailableMana(controller)}");
            // 一次性费用修正（"下一张"语义）：本张已成功支付并使用减免，消费移除该修正项
            EE.ConsumeCostModification(controller, instance, tmpl.Organization, "现身");
            // 资产卡因付费被横置，经事件总线通知 UI 刷新横置表现（与打牌/派遣暗藏一致，MVC：逻辑层只发事件）。
            // 注：OnPostMove→SyncDerivedUI 只刷标签不刷横置旋转，横置视觉仅由 OnAssetsChangedHandler 驱动。
            OnAssetsChanged?.Invoke();
        }

        // 进堆叠即翻面（Model 层同步移入堆叠区，结算回地区时 fromZone=Stack≠AreaZone 才会触发 EnterTrigger）
        string originArea = instance.ZoneDetailId ?? "";
        instance.SetCovert(false);   // 经 SetCovert 发 CovertChanged（规则③：翻面触发目标丢失总线扫描）
        // D-修复：必须将卡真正移入堆叠区（CurrentZone=Stack），否则结算 MoveCardToArea 时
        // fromZone==AreaZone 会跳过进场触发（EnterTrigger）。先前漏调 MoveCardToStack 导致暗藏者现身不触发进场触发。
        _cardManager?.MoveCardToStack(instance);

        // 入堆叠：构造 AmbushReveal 结算条目，箭头指向原本暗藏地区
        if (_stackManager != null)
        {
            var entry = new StackEntry(playerId, instance);
            entry.ControllerId = controller;                 // 箭头按控制者着色（D31）
            entry.Kind = ResolveKind.AmbushReveal;
            entry.TargetAreaId = originArea;                 // 结算回地区落点
            entry.Targets.Clear();
            if (!string.IsNullOrEmpty(originArea))
                entry.Targets.Add(new StackTargetRef
                {
                    Kind = StackTargetRef.RefKind.Area,
                    AreaSlotId = ParseAreaSlotId(originArea),
                    AreaUid = originArea
                });
            GD.Print($"[TurnManager] 暗藏者 {tmpl.CardName} 现身进入堆叠（指向地区 {originArea}），等待响应窗口结算");
            _stackManager.Push(entry);
        }
        else
        {
            // 兜底（StackManager 未注入）：直接翻面回地区，避免卡死
            GD.PrintErr("[TurnManager] StackManager 未注入，暗藏者现身回退为直接翻面");
            _triggerEmitPoints?.EnterTrigger(instance, controller, originArea, instance.AttachedTo);
            _triggerEmitPoints?.UncoverTrigger(instance, controller, originArea);
        }

        // 复位让过窗口 + 优先权分配（与打出卡牌同一口径）
        ResetPassWindow();
        AssignPriorityAfterPlay(playerId, "暗藏者现身入堆叠");

        GD.Print($"[TurnManager] ✅ 玩家{playerId} 成功发动暗藏者现身 UID={cardUid}, 地区={originArea}");

        // 同步段结束：若无模态窗口保持打开，解除规则层锁
        ExitAtomicWhenIdle();
        return true;
    }

    /// <summary>把地区槽位 id（如 "AreaSlot_1" 或 "1"）解析为槽位编号，供箭头定位使用。</summary>
    private int ParseAreaSlotId(string areaDetailId)
    {
        if (string.IsNullOrEmpty(areaDetailId)) return 0;
        // 兼容 "AreaSlot_3" 与纯数字 "3"
        var parts = areaDetailId.Split('_');
        string num = parts.Length > 1 ? parts[^1] : areaDetailId;
        return int.TryParse(num, out var id) ? id : 0;
    }

    /// <summary>
    /// 【K10·联机】**现身**时的额外费用选择由服务端驱动收集（与 `PlayWithExtraCostAsync` 同款机制）：
    ///  · `RevealHandCards`（JC081，可选）→ 下发 `SelectHandCards`（候选＝控制者手牌**除本卡自身**，
    ///    `ForceChoice=false` ⇒ 允许"一张都不展示"＝全额支付）；
    ///  · `SacrificeCharacter`（必须支付）→ 下发 `SelectTarget`（候选＝`GetSacrificeCandidates`）；
    ///  · `PayTargetCost` → 现身路径无目标交互 ⇒ **直接拒绝现身**（fail-closed，绝不放行未付额外费用的现身）。
    /// 收集完毕后在**释放原子锁之后**调 `RevealCovert(...)`（其内部再走同一份 `TryResolveExtraCost` 校验）。
    /// </summary>
    public async System.Threading.Tasks.Task<bool> RevealCovertWithExtraCostAsync(int playerId, string cardUid)
    {
        var inst = _cardManager?.GetInstance(cardUid);
        var tmpl = inst?.PlayerTemplate;
        var spec = tmpl?.ExtraCost;
        if (inst == null || spec == null || spec.Kind == ExtraCostKind.None || InputGateway == null)
            return RevealCovert(playerId, cardUid);

        if (spec.Kind == ExtraCostKind.PayTargetCost)
        {
            GD.PrintErr($"[K10 额外费用·联机] {tmpl.CardName} 的额外费用需先选定目标，**现身路径无目标交互** → 拒绝现身（fail-closed）");
            return false;
        }

        string sacrificeUid = null;
        var revealUids = new System.Collections.Generic.List<string>();

        EnterAtomic();
        try
        {
            if (spec.Kind == ExtraCostKind.SacrificeCharacter)
            {
                var cands = EE.GetSacrificeCandidates(inst, spec.Sacrifice ?? new SacrificeCostSpec())
                            ?? new System.Collections.Generic.List<CardInstance>();
                if (cands.Count == 0)
                {
                    GD.PrintErr($"[K10 额外费用·联机] {tmpl.CardName} 现身：无合法牺牲对象 → 拒绝现身");
                    return false;
                }
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
                    SourceUid = inst.UID,
                    Prompt = $"额外费用：请选择要为「{tmpl.CardName}」现身而牺牲的角色",
                    ForceChoice = true,
                };
                foreach (var c in cands)
                {
                    if (c == null) continue;
                    req.OptionUids.Add(c.UID);
                    req.OptionLabels.Add($"牺牲 {c.PlayerTemplate?.CardName ?? c.UID}");
                }
                var res = await InputGateway.RequestChoice(playerId, req);
                if (res == null || res.Cancelled || string.IsNullOrEmpty(res.SelectedUid)
                    || !req.OptionUids.Contains(res.SelectedUid))
                {
                    GD.Print($"[K10 额外费用·联机] {tmpl.CardName} 现身：未选定/非法牺牲候选 → 拒绝现身");
                    return false;
                }
                sacrificeUid = res.SelectedUid;
            }
            else if (spec.Kind == ExtraCostKind.RevealHandCards)
            {
                var req = new Script.Net.ChoiceRequest
                {
                    PlayerId = playerId,
                    RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCards,
                    SourceUid = inst.UID,
                    Prompt = $"额外费用：选择要为「{tmpl.CardName}」现身而展示给对手的手牌",
                    ForceChoice = false,   // 可不展示（＝全额支付）
                };
                foreach (var c in GetHandOf(playerId))
                {
                    if (c == null || ReferenceEquals(c, inst)) continue;   // 候选不含本卡自身（防自我减费）
                    req.OptionUids.Add(c.UID);
                    var refr = global::ZoneManager.ProjectCard(c, CardZone.Hand, playerId);
                    if (refr != null) req.OptionCardRefs.Add(refr);
                    req.OptionLabels.Add((c.PlayerTemplate?.CardName ?? c.UID)
                        + (IsQualifiedRevealCard(tmpl, c) ? "（合格 -1）" : "（不合格）"));
                }
                if (req.OptionUids.Count > 0)
                {
                    var res = await InputGateway.RequestChoice(playerId, req);
                    if (res == null || res.Cancelled)
                    {
                        GD.Print($"[K10 额外费用·联机] {tmpl.CardName} 现身：取消展示手牌 → 拒绝现身");
                        return false;
                    }
                    if (res.SelectedUids != null)
                        foreach (var uid in res.SelectedUids)
                            if (req.OptionUids.Contains(uid) && !revealUids.Contains(uid)) revealUids.Add(uid);
                }
            }
        }
        finally
        {
            ExitAtomicWhenIdle();
        }

        bool ok = RevealCovert(playerId, cardUid, sacrificeUid, revealUids.Count > 0 ? revealUids : null);
        OnAsyncSettlementCompleted?.Invoke();
        return ok;
    }

    /// <summary>
    /// 抽牌（数据层唯一收口）：经 DeckManager 抽卡 → CardManager 移动入手牌列表 → 触发 OnCardDrawn 通知 UI。
    /// UI 层不应直接调用 DeckManager.Draw；初始发牌、PM 命令 draw 均经此方法进行。
    /// **OnDraw 粒度（2026-09 用户口径）**：一次调用＝**一批**，本批抓完后只广播一次 `OnDraw` 并带本批张数。
    /// </summary>
    public void DrawCardsForPlayer(int playerId, int count)
    {
        if (_deckManager == null || _cardManager == null) return;

        // 【秘社批次·MSJC017 梦魇族群】"持续：你从牌库底抓牌，而非牌库顶"——
        // 本秘社在场时该玩家的**所有抓牌**统一改向。这是**唯一收口**处的重定向（初始发牌、阶段抓牌、
        // 效果抓牌都经本方法），故不需要在每张卡的抓牌动作里各写一遍。
        if (CouncilDrawsFromBottom(playerId))
        {
            GD.Print($"[TurnManager] 🔻 玩家{playerId} 秘社为梦魇族群 ⇒ 本次抓牌改从**牌库底**取（{count} 张）");
            DrawFromBottomForPlayer(playerId, count);
            return;
        }

        CardInstance firstDrawn = null;
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            CardInstance card = playerId == 1 ? _deckManager.DrawPlayer1() : _deckManager.DrawPlayer2();
            if (card == null)
            {
                // 牌库耗尽：抽牌者（playerId）无法抽牌 → 该玩家立即失败（对手胜）。
                // 即使抽牌动作由对方效果触发，也只看"谁无法抽牌"判负（规则：需抽牌但无牌可抽时失败）。
                if (_playerManager != null && !_playerManager.IsGameOver)
                    _playerManager.TriggerGameOver(playerId == 1 ? 2 : 1);
                break;    // 立即结束抽牌，不再继续（已抓到的牌仍算一批，照常广播）
            }
            _cardManager.MoveCardToHand(card);   // 委托 ZoneManager 设置 CurrentZone=Hand 并维护列表
            OnCardDrawn?.Invoke(playerId, card); // UI 层据此把对应 CardUI 从牌库取出
            firstDrawn ??= card;
            drawn++;
        }
        // 【OnDraw 粒度·用户 2026-09 口径】一次调用＝一批：本批全部抓完后**只广播一次** `OnDraw`，
        // 并把本批张数作为 `ExtraInt` 带下去（供 JC080「X＝此次抓牌的数量」按批取数）。
        if (drawn > 0) _triggerEmitPoints?.OnDraw(firstDrawn, playerId, drawn);
    }

    /// <summary>
    /// 【秘社批次·MSJC017】该玩家的秘社是否声明了"从牌库底抓牌"（`CouncilCardData.DrawFromBottom`）。
    /// 读秘社区实例的模板；秘社缺失/未声明 ⇒ false（保持既有牌库顶口径）。
    /// </summary>
    public bool CouncilDrawsFromBottom(int playerId)
    {
        if (_zoneManager == null || playerId < 1) return false;
        var councils = _zoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
        if (councils == null) return false;
        foreach (var c in councils)
            if (c?.CouncilTemplate != null && c.CouncilTemplate.DrawFromBottom) return true;
        return false;
    }

    /// <summary>
    /// 从牌库底抽牌（秘社激活效果用）。空库时抽牌者判负，与 DrawCardsForPlayer 同规则。
    /// </summary>
    /// <param name="countAsDraw">是否**算作"抓牌"**（缺省 true＝既有口径）。
    /// 【P3·K3 附：非抓牌式取牌】`false` 用于卡文/FAQ 明写"**并不算抓牌**"的效果（JC049 深渊细语
    /// "将你牌库底的两张牌置于手中"）：
    ///  · **不广播 `OnDraw`**（抓牌时机不触发，"每当你抓牌时…"类效果不响应）；
    ///  · **空库不判负**——判负规则绑定"抓牌"这一行为，本路径只是把牌拿走，取到多少算多少；
    ///  · `OnCardDrawn` **照常通知**（那是 View 把 CardUI 从牌库移出的**界面**通知，与触发时机无关）。</param>
    public void DrawFromBottomForPlayer(int playerId, int count, bool countAsDraw = true)
    {
        if (_deckManager == null || _cardManager == null) return;
        CardInstance firstDrawn = null;
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            CardInstance card = playerId == 1 ? _deckManager.DrawFromBottomPlayer1() : _deckManager.DrawFromBottomPlayer2();
            if (card == null)
            {
                // 非抓牌式取牌：取到多少算多少，不因空库判负（见 countAsDraw 说明）。
                if (countAsDraw && _playerManager != null && !_playerManager.IsGameOver)
                    _playerManager.TriggerGameOver(playerId == 1 ? 2 : 1);
                break;
            }
            _cardManager.MoveCardToHand(card);
            OnCardDrawn?.Invoke(playerId, card);   // 界面通知：把 CardUI 从牌库取出（与触发时机无关）
            firstDrawn ??= card;
            drawn++;
        }
        // 【OnDraw 粒度】与 DrawCardsForPlayer 一致：一次调用＝一批，只广播一次并带本批张数
        // （从牌库底抽牌同样属于"抓牌"，JC049 等经 DrawFromBottom）；
        // 【非抓牌式取牌】`countAsDraw=false` → **不广播**（卡文/FAQ 明写"并不算抓牌"）。
        if (drawn > 0 && countAsDraw) _triggerEmitPoints?.OnDraw(firstDrawn, playerId, drawn);
    }

    /// <summary>
    /// 获取玩家当前可用无色费用（未横置资产卡数量）
    /// </summary>
    public int GetAvailableMana(int playerId)
    {
        return _playerManager?.GetAvailableMana(playerId) ?? 0;
    }

    // ============================================================
    // 基础行动收口（原散落在 Arena 的规则判定/费用扣费/区域移动统一上收）
    // 规则 5.1 派遣暗藏者：仅角色卡、费用 1、落己方争夺区、背面进场、消耗 1 次
    // 规则 5.2 建立资产：落资产区（或地区，容错）、消耗 1 次、不耗费用
    // 返回 true 表示行动成功；UI 层据此决定是否刷新翻面/退出蒙版，失败则退回手牌
    // ============================================================

    /// <summary>
    ///【MVC 修复 M-04】派遣暗藏的「卡牌资格」规则查询（单一真相源）。
    /// 仅判定与卡牌自身相关的资格：必须是角色卡，且不得具有【公开】关键字。
    /// 不含阶段/行动权/次数等情境校验——那些仍由 <see cref="DeployCovert"/> 在执行时把关。
    ///
    /// 存在意义：UI 需要在「拖拽起手」这一刻就知道某张牌能不能拖，属于规则的前置查询。
    /// 若让 View 自行用 CategoryFlag/HasPublicKeyword 推导，同一条规则就会有两份实现并各自漂移。
    /// 因此这里暴露查询、View 只调不问，规则改动只需改本方法与 DeployCovert 共用的判据。
    /// </summary>
    /// <summary>玩家当前是否被"未横置阿格斯IV"封锁而禁止秘密派遣（JC068）。</summary>
    public bool CanPlayerDeployCovert(int playerId)
    {
        // 阿格斯IV全局封锁：未横置的阿格斯IV封锁其控制者的对手
        return !(EE?.IsDeployCovertBlocked(playerId) ?? false);
    }

    public bool CanDeployCovert(CardInstance instance)
    {
        // 判定体抽取到 DeployCovertEligibility（单一真相源），联机客户端（快照投影、无 TurnManager 实例）
        // 复用同一静态函数做"拖起资格"预检；此处保持原签名与语义不变（单机行为零变化）。
        // 【P1·同一判定含封锁】全局封锁（JC068）不再是"只在服务端动作里判"的旁路：由本方法把
        // 本局权威封锁态作为入参喂给同一函数，客户端则喂快照投影态（PlayerManager.IsDeployCovertBlockedProjected），
        // 两端从此共用一份判定。
        return DeployCovertEligibility.IsEligible(instance, !CanPlayerDeployCovert(instance?.ControllerId ?? -1));
    }

    /// <summary>
    /// 派遣暗藏者（基础行动）。完整流程：阶段/行动权校验 → 类别校验 → 费用校验/扣费 →
    /// 标记 IsCovert → 经 ZoneManager 移动至己方争夺区 → 扣减次数。
    /// areaUid 为目标地区 UID（必须是该玩家的己方争夺区）。
    /// 注意：移动后 ZoneManager.FireMoved 会触发 UI 翻为正面，暗藏者背面表现由 UI 层在返回 true 后自行 SetFace(false)。
    /// </summary>
    public bool DeployCovert(int playerId, string cardUid, string areaUid)
    {
        // 1. 阶段校验（基础行动仅行动阶段可用）
        if (!IsActionPhase())
        {
            GD.Print($"❌ [TurnManager] 当前阶段 {CurrentPhase} 不允许派遣暗藏");
            return false;
        }
        // 2. 行动权校验
        if (PriorityPlayerId != playerId)
        {
            GD.Print($"❌ [TurnManager] 当前行动权在玩家{PriorityPlayerId}，玩家{playerId}不能派遣暗藏");
            return false;
        }
        // 2.3 全局封锁校验：玩家被"未横置阿格斯IV"封锁时禁止秘密派遣（JC068）
        if (EE.IsDeployCovertBlocked(playerId))
        {
            GD.Print($"❌ [TurnManager] 玩家{playerId} 派遣暗藏被拒：对方未横置阿格斯IV封锁秘密派遣行动");
            return false;
        }
        // 2.5 规则校验：具有【公开】关键字的卡不可用于派遣暗藏行动
        var instance = _cardManager?.GetInstance(cardUid);
        if (instance == null)
        {
            GD.Print($"❌ [TurnManager] 派遣暗藏失败：找不到卡牌 {cardUid}");
            return false;
        }
        if (instance.HasPublicKeyword)
        {
            GD.Print($"❌ [TurnManager] 玩家{playerId} 派遣暗藏被拒：{instance.PlayerTemplate?.CardName} 具有【公开】关键字，不可进行暗藏行动");
            return false;
        }
        // 3. 次数校验
        if (!_playerManager.HasDeployCovertCharge(playerId))
        {
            GD.Print($"❌ [TurnManager] 玩家{playerId} 派遣暗藏次数已用尽");
            return false;
        }
        // 5. 类别校验：仅角色卡可派遣
        bool isChar = (instance.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0;
        if (!isChar)
        {
            GD.Print($"❌ [TurnManager] 派遣暗藏：仅角色卡可派遣（{instance.PlayerTemplate?.CardName}）");
            return false;
        }
        // 6. 落点校验：必须指定己方争夺区
        if (string.IsNullOrEmpty(areaUid))
        {
            GD.Print("❌ [TurnManager] 派遣暗藏：需指定己方争夺区");
            return false;
        }
        // 7. 费用校验（规则3：费用不足不消耗次数，保留蒙版重试）
        const int cost = 1;
        if (_playerManager.GetAvailableMana(playerId) < cost)
        {
            GD.Print($"❌ [TurnManager] 派遣暗藏失败：费用不足（需 {cost}），卡牌退回手牌");
            return false;
        }
        if (!_playerManager.SpendMana(playerId, cost))
        {
            GD.Print("❌ [TurnManager] 派遣暗藏失败：费用支付失败，卡牌退回手牌");
            return false;
        }

        // 8. 以暗藏者（背面朝上）形式放入己方争夺区
        instance.SetCovert(true);   // 经状态中枢发 CovertChanged，供目标丢失总线捕获(规则③)
        _zoneManager.MoveToArea(instance, areaUid, playerId);

        // 9. 资产横置状态变化，通知 UI 刷新
        OnAssetsChanged?.Invoke();

        // 10. 扣减次数
        _playerManager.ConsumeDeployCovert(playerId);

        GD.Print($"[TurnManager] ✅ 玩家{playerId} 派遣暗藏成功：{instance.PlayerTemplate?.CardName} → 地区 {areaUid}（背面）");
        return true;
    }

    /// <summary>
    /// 建立资产（基础行动）。完整流程：阶段/行动权校验 → 次数校验 →
    /// 经 ZoneManager 移动至资产区 → 扣减次数。（不耗费用）
    /// </summary>
    public bool EstablishAsset(int playerId, string cardUid)
    {
        // 1. 阶段校验
        if (!IsActionPhase())
        {
            GD.Print($"❌ [TurnManager] 当前阶段 {CurrentPhase} 不允许建立资产");
            return false;
        }
        // 2. 行动权校验
        if (PriorityPlayerId != playerId)
        {
            GD.Print($"❌ [TurnManager] 当前行动权在玩家{PriorityPlayerId}，玩家{playerId}不能建立资产");
            return false;
        }
        // 3. 次数校验
        if (!_playerManager.HasEstablishAssetCharge(playerId))
        {
            GD.Print($"❌ [TurnManager] 玩家{playerId} 建立资产次数已用尽");
            return false;
        }
        // 4. 查找卡牌实例
        var instance = _cardManager?.GetInstance(cardUid);
        if (instance == null)
        {
            GD.Print($"❌ [TurnManager] 未找到卡牌实例: {cardUid}");
            return false;
        }

        // 4.1 【P3·非资产】规则文本："拥有非资产效果的卡不能被用于【建立资产】基础行动"。
        // 例外：可以通过其他效果变为资产（形态转换路径不经过本门禁）。
        if (HasCardKeyword(instance, instance.PlayerTemplate, "非资产"))
        {
            GD.Print($"❌ [TurnManager] {instance.PlayerTemplate?.CardName} 具有【非资产】，不能用于建立资产基础行动");
            return false;
        }

        // 5. 进入资产区
        _zoneManager.MoveToAsset(instance, playerId);

        // 6. 扣减次数
        _playerManager.ConsumeEstablishAsset(playerId);

        // 7. 资产区忠诚变动 → 重判监听中的条件持续效果（D19-②，如 JC001「心心」→白眼）
        EE?.ReevaluatePrereqs(playerId);

        GD.Print($"[TurnManager] ✅ 玩家{playerId} 建立资产成功：{instance.PlayerTemplate?.CardName}");
        return true;
    }

    /// <summary>
    /// 【秘社批次】把**一张已有的卡实例**送回指定玩家的牌库（顶/底）。
    /// 与下方按 UID 的调试入口并存：效果动词（`MoveToZone{牌库底}` / `PutCardOnDeckTop`）手里已经有实例，
    /// 再经 `GetInstance(uid)` 绕一圈是**多余且脆弱**的（实例未必在 `CardManager` 的索引里，
    /// 查不到就会静默什么都不做——实测：MSJC010②③"把封印牌置于牌库底"变成"卡留在封印区、日志卡名为空"）。
    /// 故此处提供"以实例为准"的正式入口，`CardManager.GetInstance` 查不到时**不再阻断**。
    /// </summary>
    public void ReturnCardToDeck(CardInstance card, int playerId, bool toTop = false)
    {
        if (card == null) { GD.PrintErr("[TurnManager] ❌ ReturnCardToDeck: 卡实例为空"); return; }
        _zoneManager?.MoveToDeckBottom(card, playerId, toTop);
        GD.Print($"[TurnManager] ↩ {card.PlayerTemplate?.CardName}（{card.UID}）→ 玩家{playerId} 牌库{(toTop ? "顶" : "底")}");
    }

    /// <summary>
    /// 调试/测试辅助：将指定手牌卡退回牌库（不消耗行动权、不校验阶段）。
    /// 经 ZoneManager 统一移动，避免 UI 越权直调 ZoneManager.MoveToDeckBottom。
    /// 正常玩法不应调用；仅 PM 调试命令 / 测试场景使用。
    /// </summary>
    public void ReturnCardToDeck(int playerId, string cardUid, bool toTop = false)
    {
        var instance = _cardManager?.GetInstance(cardUid);
        if (instance == null)
        {
            GD.Print($"❌ [TurnManager] ReturnCardToDeck: 未找到卡牌实例 {cardUid}");
            return;
        }
        _zoneManager?.MoveToDeckBottom(instance, playerId, toTop);
        GD.Print($"[TurnManager] 调试：玩家{playerId} 卡牌 {instance.PlayerTemplate?.CardName} 退回牌库 (toTop={toTop})");
    }

    /// <summary>
    /// 判断给定阶段是否为行动阶段
    /// </summary>
    public bool IsActionPhase()
    {
        return CurrentPhase == TurnPhase.ActionFirst || CurrentPhase == TurnPhase.ActionSecond;
    }

    /// <summary>
    /// 计算"优先获得行动权的玩家"（永远存在一个优先玩家）：
    /// - 先手玩家(FirstPlayerId)在除后手行动步骤(ActionSecond)外的所有步骤优先；
    /// - 后手行动步骤(ActionSecond)内，优先玩家让位给后手玩家。
    /// 即：P1 先手时 ActionSecond → P2 优先，其余 → P1 优先；
    ///     P1 后手时 ActionSecond → P1 优先，其余 → P2 优先。
    /// 优先权分配（出牌入堆叠 / 结算后）均归此玩家，与谁打出无关。
    /// </summary>
    public int GetPriorityPlayer()
    {
        // 【第0回合】调度步骤不涉及优先权转换：无人持有行动权（PriorityPlayerId = 0）
        if (CurrentPhase == TurnPhase.Mulligan) return 0;
        // ActionSecond 内优先玩家为"后手"（即非先手方）
        if (CurrentPhase == TurnPhase.ActionSecond)
            return FirstPlayerId == 1 ? 2 : 1;
        // 其余所有步骤（含 StartPrepare/StartDraw/ActionFirst/Combat*/End* 等）均先手玩家优先
        return FirstPlayerId;
    }

    /// <summary>
    /// 【MVC 收口】按当前先后手刷新【争夺区 + 手牌区】所有卡牌的显示用能力值（含先手加成）。
    /// 原逻辑散落在 Arena.ApplyFirstStrike / Arena.RefreshAllHandStats（View 直接写 Model 的
    /// CurrentXxx 字段，违反 MVC），现上收至 Controller：规则只属于 Controller，
    /// View 只读取 CardInstance.CurrentXxx 并刷新自身。
    /// 调用时机：先后手切换（OnPriorityChanged/OnTurnStarted）、卡移动、手牌变化时。
    /// </summary>
    public void RefreshFirstStrikeStats()
    {
        if (_zoneManager == null) return;
        foreach (var inst in _zoneManager.GetZoneCards(CardZone.AreaZone, 0))
        {
            bool isFirst = FirstPlayerId == inst.ControllerId;
            inst.ResetToBaseValues(isFirstPlayer: isFirst);
        }
        // 【P1】附属区一并重算：附属同样参与地区能力统计（AreaStatsCalculator.IsStatParticipant 含附属），
        // 原实现只刷争夺区/手牌 → 附属的印刷先手值（如 JC035 FSI=1、JC120 FSP=1、JC121 FSS=2）从未生效，
        // 且 AreaStatsCalculator 改读实例 Current*（P1）后必须保证附属的 Current* 也被正确重算。
        foreach (var inst in _zoneManager.GetZoneCards(CardZone.AttachedZone, 0))
        {
            bool isFirst = FirstPlayerId == inst.ControllerId;
            inst.ResetToBaseValues(isFirstPlayer: isFirst);
        }
        // 手牌区同样受先手加成影响（先手玩家手牌能力值更高），一并重算，消除 View 写 Model
        for (int p = 1; p <= 2; p++)
        {
            bool isFirst = FirstPlayerId == p;
            foreach (var inst in _zoneManager.GetZoneCards(CardZone.Hand, p))
                inst.ResetToBaseValues(isFirstPlayer: isFirst);
        }
        // 通知 UI 先手加成已重算，由其统一刷新先手角标（View 不再在渲染时反向驱动 Controller）
        OnFirstStrikeRecalculated?.Invoke();
    }

    /// <summary>返回当前所有在场卡实例（争夺区 + 手牌 + 分数区），供 UI 统一刷新先手角标等展示。</summary>
    public IEnumerable<CardInstance> GetAllCardsInPlay()
    {
        if (_zoneManager == null) yield break;
        foreach (var z in new[] { CardZone.AreaZone, CardZone.Hand, CardZone.ScoreZone })
        {
            for (int p = 1; p <= 2; p++)
            {
                foreach (var inst in _zoneManager.GetZoneCards(z, p))
                    if (inst != null) yield return inst;
            }
        }
    }

    /// <summary>
    /// 【MVC 收口】基础行动蒙版准入的规则门禁（原 Arena.CanEnterActionMask 在 View 中重复判断）。
    /// 规则：①堆叠必须为空（基础行动不可在响应窗口/堆叠有内容时进行）；
    ///       ②当前步骤优先获得行动权的玩家必须是该 playerId；
    ///       ③仅限"自己的行动步骤"（先手→ActionFirst，后手→ActionSecond）。
    /// UI 只调用本方法决定"是否显示蒙版按钮"，不再自行判规则。
    /// 注：此门禁针对"基础行动（暗藏/资产）"，与 PlayCard（允许堆叠非空时入栈）语义不同，二者不可混用。
    /// </summary>
    public bool CanEnterBasicActionMask(int playerId)
    {
        // 游戏已结束：拒绝一切基础行动（暗藏/资产）
        if (_playerManager != null && _playerManager.IsGameOver) return false;

        // ① 堆叠必须为空
        if (_stackManager != null && !_stackManager.IsEmpty) return false;
        // ② 当前实际持有优先权者必须是该玩家（用 PriorityPlayerId，与 PlayCard/RevealCovert 统一；
        //    步骤归属判断见 ③，仍由 GetPriorityPlayer() 派生 FirstPlayerId，二者分工不同不可混用）
        if (PriorityPlayerId != playerId) return false;
        // ③ 仅限自己的行动步骤
        bool inMyActionStep = (FirstPlayerId == playerId)
            ? CurrentPhase == TurnPhase.ActionFirst
            : CurrentPhase == TurnPhase.ActionSecond;
        return inMyActionStep;
    }

    // ============================================================
    // 阶段流转（自动推进）
    // ============================================================

    /// <summary>
    /// 进入指定阶段（内部阶段流转统一入口；亦供测试/联机快照恢复直接设置阶段）。
    /// </summary>
    public void EnterPhase(TurnPhase phase)
    {
        // 【P3·K19】临时费用（JC115）：**任意步骤结束时消失**——实现上＝每次阶段流转（＝上一步骤结束）时清空双方临时费用。
        // 口径见 §7.4b A26（六个步骤：先手/后手/预对抗/调查对抗/战斗对抗/势力对抗）。
        PlayerManagerRef?.ClearAllTemporaryMana();

        CurrentPhase = phase;
        if (VerboseSteps) GD.Print($"--- [TurnManager] 进入阶段: {phase} ---");
        OnPhaseChanged?.Invoke(phase);

        switch (phase)
        {
            case TurnPhase.StartReset:  DoReset(); break;
            case TurnPhase.StartPrepare: DoPrepare(); break;
            case TurnPhase.StartDraw:   DoDraw(); break;
            case TurnPhase.ActionFirst: DoActionFirst(); break;
            case TurnPhase.ActionSecond: DoActionSecond(); break;
            case TurnPhase.PreCombat: DoPreCombat(); break;
            case TurnPhase.CombatInvestigation: DoCombatInvestigation(); break;
            case TurnPhase.CombatStrength: DoCombatStrength(); break;
            case TurnPhase.CombatPower: DoCombatPower(); break;
            case TurnPhase.EndCleanup:  DoCleanup(); break;
            case TurnPhase.EndRecover:  DoRecover(); break;
            case TurnPhase.EndPassTurn: DoPassTurn(); break;
        }
    }

    private void AdvancePhase()
    {
        TurnPhase nextPhase;
        switch (CurrentPhase)
        {
            // 第0回合（调度）→ 第1回合重置步骤（兜底：正常路径由 BeginMulliganAsync 直接 StartGame）
            case TurnPhase.Mulligan:     nextPhase = TurnPhase.StartReset; break;
            case TurnPhase.StartReset:   nextPhase = TurnPhase.StartPrepare; break;
            case TurnPhase.StartPrepare: nextPhase = TurnPhase.StartDraw; break;
            case TurnPhase.StartDraw:    nextPhase = TurnPhase.ActionFirst; break;
            case TurnPhase.ActionFirst:  nextPhase = TurnPhase.ActionSecond; break;

            // 进入对抗阶段：建立地区游标快照，先进入"对抗前优先权窗口"等待双方让过，再开始地区1的调查对抗；无地区则整个对抗阶段跳过
            case TurnPhase.ActionSecond:
                BeginCombatPhase();
                nextPhase = _combatAreaUids.Count > 0 ? TurnPhase.PreCombat : TurnPhase.EndCleanup;
                break;

            // 对抗前优先权窗口：双方玩家连续让过后才真正开始（首个/新地区的）调查对抗
            case TurnPhase.PreCombat:
                nextPhase = TurnPhase.CombatInvestigation;
                break;

            // 同一地区内：调查 → 战斗 → 势力（每一种对抗之间都已经过一次双方连续让过）
            case TurnPhase.CombatInvestigation: nextPhase = TurnPhase.CombatStrength; break;
            case TurnPhase.CombatStrength:      nextPhase = TurnPhase.CombatPower; break;

            // 本地区三种对抗结算完毕：推进地区游标，若还有下一地区则直接进入其调查对抗
            // （对抗前优先权窗口 PreCombat 整个回合仅在 ActionSecond 之后进行一次，后续地区不再重复 PreCombat）
            case TurnPhase.CombatPower:
                // 【P3·K18·第二批】"本地区在对抗阶段时额外进行一次势力对抗"（JC067 约翰·F·纳特里）：
                // 当前地区本回合还没跑过追加的那次 → **原地再进一次 CombatPower**（同地区、同对抗类型），
                // 跑完（再过一次双方让过）才继续推进地区游标。每回合每地区至多追加一次（见 `_extraPowerDoneAreas`）。
                {
                    string areaForExtra = CurrentCombatAreaUid;
                    if (EE?.AreaHasExtraPowerContest(areaForExtra) == true
                        && !string.IsNullOrEmpty(areaForExtra)
                        && !_extraPowerDoneAreas.Contains(areaForExtra))
                    {
                        _extraPowerDoneAreas.Add(areaForExtra);
                        if (VerboseSteps)
                            GD.Print($"🔰 [TurnManager] 地区{( _areaManager != null ? _areaManager.GetSlotIndex(areaForExtra) : 0 )} 触发『额外进行一次势力对抗』（JC067）");
                        nextPhase = TurnPhase.CombatPower;   // 原地重入：EnterPhase → DoCombatPower → 再结算一次势力对抗
                        break;
                    }
                }
                _combatAreaIndex++;
                if (_combatAreaIndex < _combatAreaUids.Count)
                {
                    GD.Print($"🔰 [TurnManager] 地区{_combatAreaIndex} 三种对抗结算完毕，直接进入地区{_combatAreaIndex + 1} 调查对抗（不再重复 PreCombat）");
                    nextPhase = TurnPhase.CombatInvestigation;
                }
                else
                {
                    GD.Print("✅ [TurnManager] 所有地区三种对抗结算完毕");
                    nextPhase = TurnPhase.EndCleanup;
                }
                break;

            case TurnPhase.EndCleanup: nextPhase = TurnPhase.EndRecover; break;
            case TurnPhase.EndRecover: nextPhase = TurnPhase.EndPassTurn; break;
            default: nextPhase = CurrentPhase; break;
        }

        GD.Print($"⏩ [TurnManager] 阶段推进: {CurrentPhase} -> {nextPhase}");
        EnterPhase(nextPhase);
    }

    /// <summary>
    /// 进入对抗阶段前建立地区游标快照（按地区编号从小到大）。
    /// 结算过程中地区可能被赢取移除，用快照保证"本回合参与对抗的地区集合"稳定。
    /// </summary>
    private void BeginCombatPhase()
    {
        _combatAreaUids = _combatManager?.GetOrderedAreaUids() ?? new System.Collections.Generic.List<string>();
        _combatAreaIndex = 0;
        GD.Print($"🔰 [TurnManager] 对抗阶段开始，共 {_combatAreaUids.Count} 个地区，依次结算 调查→战斗→势力");
    }

    // ============================================================
    // 各阶段具体逻辑
    // ============================================================

    /// <summary>
    /// 【统一优先权分配·打出/现身后】打出卡牌或暗藏者现身**入堆叠后**的优先权归属（单一出口）。
    ///
    /// 规则（用户 2026-09 口径 · 修复"后手使用效果/打出卡后优先权交换错误"）：
    ///  · 每个步骤存在一位**天生优先玩家**（先手；ActionSecond 内为后手）。响应窗口内应**先由天生优先
    ///    玩家取得优先权**——即打出后不能无条件把优先权"拉回"天生优先玩家，否则当**非优先方**（如先手
    ///    在 ActionSecond 期间）打出时，天生优先玩家本应失去"先手响应"机会却被错误地直接赋予优先权。
    ///  · 因此：若**打出者本身就是天生优先玩家**，优先权保持在其手中（＝他先获得响应权，符合"天生优先"）；
    ///    若**打出者是另一方**（非天生优先玩家），优先权应交给**天生优先玩家**（＝对方），
    ///    从而"谁天生优先谁先响应"的语义在响应窗口内成立。
    ///  · `_passCount` 已由调用方清零：响应窗口的让过节拍从"打出后"重新开始（两次让过才结算栈顶）。
    ///
    /// 说明：本方法收口原 `PlayCard`（出牌入堆叠）与 `RevealCovert`（暗藏者现身）两处重复赋值，
    /// 避免两套口径漂移；与 `BeginStepPriority`（步骤入口复位）语义区分，互不覆盖。
    /// </summary>
    /// <param name="playerId">打出/现身者。</param>
    /// <param name="reason">日志用描述（如"出牌入堆叠"）。</param>
    private void AssignPriorityAfterPlay(int playerId, string reason)
    {
        int naturalPriority = GetPriorityPlayer();
        // 打法A：打出者即天生优先玩家 → 保持其优先权（他先响应）；
        // 打法B：打出者为另一方 → 归还天生优先玩家（对方先响应）。
        PriorityPlayerId = naturalPriority;
        GD.Print($"⏳ [TurnManager] 玩家{playerId} {reason}，优先权归天生优先玩家{PriorityPlayerId}"
            + $"（打出者{(playerId == naturalPriority ? "＝天生优先，保持" : "≠天生优先，交还对方")}）");
        OnPriorityChanged?.Invoke(PriorityPlayerId);
    }

    /// <summary>
    /// 【修复 A+B+C】步骤开始时分配优先权。
    /// 铁律（阶段推进的唯一条件）：仅当堆叠为空时，双方连续让过（Pass 空分支 _passCount>=2）才会
    /// AdvancePhase 推进阶段；堆叠非空时玩家让过一律走 Pass 非空分支只结算栈顶，绝不推进阶段
    /// （赢地区等响应窗口天然隔离在阶段外）——这一铁律由 Pass 的两个分支实现，与本法无关。
    ///
    /// 本法只负责"步骤入口的行动权/计数初始化"，规则是：
    /// ① PriorityPlayerId 始终 = GetPriorityPlayer()（由当前阶段 + FirstPlayerId 派生）。
    ///    这是跨阶段切换的正确语义：无论上一个阶段结束时行动权停在谁手里（如空分支计数到 2 直接
    ///    AdvancePhase 未翻转），新阶段必须从"本阶段优先玩家"重新开始，绝不能沿用残留值。
    ///    否则 JC035 路径下（StartPrepare 空分支两次让过推进时 PriorityPlayerId 停在 P1，
    ///    紧接着 StartDraw 抽牌又入栈使栈非空）→ BeginStepPriority 误沿用 P1，导致 StartDraw 错给 P1。
    /// ② _passCount 仅在堆叠空时清零；堆叠非空时不在此清零，交给 Pass 非空分支（413 行）掌管，
    ///    避免打断响应窗口的让过节拍（两次让过被误计为阶段推进让过）。
    /// </summary>
    private void BeginStepPriority(string stepName)
    {
        // ① 行动权始终按"本阶段优先玩家"复位（跨阶段切换无条件，与栈是否为空无关）
        PriorityPlayerId = GetPriorityPlayer();

        int stackCount = _stackManager?.Entries.Count ?? 0;
        if (stackCount == 0)
        {
            // 堆叠空：本步骤节拍重新开始
            ResetPassWindow();
        }
        else
        {
            // 堆叠非空：保留非空分支的 _passCount 节拍（不在此清零），仅复位行动权显示。
            if (VerboseSteps) GD.Print($"⚠️ 【{stepName}】堆叠非空（{stackCount} 项），行动权复位为玩家{PriorityPlayerId}，计数交 Pass 非空分支掌管");
        }
        if (VerboseSteps) GD.Print($"【{stepName}】行动权: 玩家{PriorityPlayerId}");
        // 广播当前行动权，确保 UI 与规则层一致
        OnPriorityChanged?.Invoke(PriorityPlayerId);
    }

    private void DoReset()
    {
        if (VerboseSteps) GD.Print("【重置步骤】所有卡牌 IsTapped = false（含资产，费用恢复）");
        _cardManager?.UntapAll();
        // 【K23】每回合一次/两次类限制在**重置步骤**清空（每张卡·每回合·每能力）。
        // 说明：按用户口径，版本号变动也会另行清空该卡的计数（见 `CardInstance.BumpVersion`）。
        _cardManager?.ResetAllAbilityUsesThisTurn();
        // 【P3】"本回合"粒度的**事件记忆位**同样在重置步骤清空：本回合是否有过"某玩家的势力标志被移除"
        // （JC053 鸣钟教派徽记的激活前提"本回合中，若有敌方势力标志曾被移除"）。
        AreaManagerRef?.ResetPowerMarkRemovedThisTurn();
        OnAssetsChanged?.Invoke();
        // 步骤开始：当前优先玩家获得优先权（免让过：若堆叠为空则立即推进，不等让过）
        BeginStepPriority("重置步骤");
        TryAutoAdvance();
    }

    private void DoPrepare()
    {
        if (VerboseSteps) GD.Print("【准备步骤】广播 OnTurnStarted（双方准备阶段触发点）");
        // 准备阶段为双方共有步骤：不归属单一回合玩家（-1），由每张卡的 EffectData 按各自控制者判定"对手"。
        _triggerEmitPoints?.OnTurnStarted();
        // 【秘社批次】MSJC016 颂亡者「触发：**在第一个回合的准备步骤开始时**，将你牌库顶的三张牌置于你的墓地」：
        // 一次性时机（只第一回合），且必须发生在**发完起手之后**（否则磨掉的是未发完的牌库顶，
        // 张数会与"起手已发完"的正式局不一致）——故不能用 `GameInitializer.ApplyCouncilOpeningEffects`
        // （那时还没发牌），放在这里按 `TurnCount == 1` 一次性执行。
        if (TurnCount == 1)
        {
            foreach (var pid in new[] { 1, 2 })
            {
                if (_zoneManager == null) break;
                foreach (var council in _zoneManager.GetZoneCards(CardZone.CouncilZone, pid)
                             ?? new System.Collections.Generic.List<CardInstance>())
                {
                    var list = council?.CouncilTemplate?.TurnStartEffectList;
                    if (list == null) continue;
                    foreach (var e in list)
                    {
                        if (e == null) continue;
                        GD.Print($"[TurnManager] 秘社首个准备步骤触发：{council.CouncilTemplate.CardName} → {e.EffectId}");
                        EE?.ResolveEffect(e, council, null);
                    }
                }
            }
        }
        // 步骤开始：当前优先玩家获得优先权，等待双方连续让过推进
        // 修复 A：OnTurnStarted 可能让 JC035 等触发同步入栈，若堆叠非空则保留堆叠结算流程设定的行动权，
        // 不再无条件覆盖（否则触发链搅乱 PriorityPlayerId，后续让过全部报错）。
        BeginStepPriority("准备步骤");
    }

    private void DoDraw()
    {
        if (VerboseSteps) GD.Print("【抓牌步骤】双方各抽1张");
        // 复用 DrawCardsForPlayer，确保抽牌经 CardManager.MoveCardToHand → ZoneManager 维护手牌列表与 CurrentZone，
        // 避免直接调 DeckManager.DrawPlayerX 导致手牌数据列表与实际不一致。
        try
        {
            DrawCardsForPlayer(1, 1);
            DrawCardsForPlayer(2, 1);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"【抓牌步骤】抽牌过程异常: {ex.GetType().Name}: {ex.Message}");
        }
        // 步骤开始：当前优先玩家获得优先权，等待双方连续让过推进
        BeginStepPriority("抓牌步骤");
    }

    private void DoActionFirst()
    {
        BeginStepPriority("先手行动");   // ActionFirst 内先手玩家优先
    }

    private void DoActionSecond()
    {
        BeginStepPriority("后手行动");   // ActionSecond 内后手玩家优先
    }

    /// <summary>
    /// 对抗前优先权窗口（规则对接待补 D14 的"对抗前让过"）：整个回合仅在 ActionSecond 之后进行一次，
    /// 当前优先玩家获得行动权，双方玩家连续让过（_passCount>=2）后经 AdvancePhase 推进到 CombatInvestigation
    /// 才真正开始地区1的调查对抗。后续地区（地区2、地区3...）不再重复 PreCombat（BUG 修复：原每地区一次共三次）。
    /// 机动触发（ManeuverTrigger，如 JC014）仅在此次 PreCombat 内收集一次，由 GetOtherCombatAreaUids 提供全部地区作为候选目标。
    /// </summary>
    private void DoPreCombat()
    {
        if (VerboseSteps) GD.Print("【对抗前优先权窗口】进入对抗阶段前（整回合仅一次）：优先玩家获得行动权，双方让过才开始首个地区调查对抗");
        BeginStepPriority("对抗前优先权窗口");

        // 对抗前触发窗口（标准触发流程）：通过 CollectManeuverTriggers 走
        // 新系统 EffectDataList(ManeuverTrigger) 收集 -> RunTriggerPhase -> 选目标地区 -> PushTriggerToStack，
        // 再回到优先权窗口等待结算。JC014/JC015 公路骑士/驱魔人等"机动触发"在此以 ManeuverTrigger 挂入堆叠。
        if (VerboseSteps) GD.Print("【对抗前优先权窗口】广播 ManeuverTrigger 事件，由 TriggerRegistry 收集机动触发（ManeuverTrigger）并入堆叠，等待结算");
        // 窗口类系统事件：无单一引发玩家（TriggeringPlayerId 保持 NoPlayer=-1）；
        // TargetAreaId 带上当前对抗地区，供机动触发推导"其他可移动地区"
        _gameEventBus?.Emit(Script.Data.Effects.GameEvent.ManeuverTrigger,
            new Script.Data.Effects.TriggerContext { TargetAreaId = CurrentCombatAreaUid });
    }

    /// <summary>
    /// <summary>
    /// 供触发收集器/机动目标选择调用：返回与 currentAreaUid 不同的**所有**对抗地区列表（"其他可移动地区"）。
    /// 机动是取目标效果，玩家需从其中选择一处。仅剩一个地区时返回空列表（无可移动目标，触发应被过滤）。
    /// 取"全部已注册地区"（_combatAreaUids），而非仅"当前有卡在场的地区"，否则空地区无法作为移动目标。
    /// </summary>
    public List<string> GetOtherCombatAreaUids(string currentAreaUid)
    {
        var areas = (_combatAreaUids != null && _combatAreaUids.Count > 0)
            ? _combatAreaUids
            : (_zoneManager != null ? _zoneManager.GetAllAreaSlotUids() : null);
        var result = new List<string>();
        if (areas == null) return result;
        foreach (var a in areas)
        {
            if (!string.IsNullOrEmpty(a) && a != currentAreaUid) result.Add(a);
        }
        return result;
    }

    private void DoCombatInvestigation()
        => ResolveCurrentAreaCombat(CombatManager.CombatType.Investigation, "调查步骤");

    private void DoCombatStrength()
        => ResolveCurrentAreaCombat(CombatManager.CombatType.Strength, "战斗步骤");

    private void DoCombatPower()
        => ResolveCurrentAreaCombat(CombatManager.CombatType.Power, "势力步骤");

    /// <summary>【P3·K18】指定地区在指定对抗上是否被"略过对抗"静止规则跳过（JC098「本地区略过调查对抗步骤」）。
    /// 对抗名映射：Investigation→"调查" / Strength→"战斗" / Power→"势力"（与 `StaticEffect.Param` 同一套写法）。
    /// 单一判定入口：结算路径（`ResolveCurrentAreaCombat`）与 UI/候选侧共用，避免两处口径漂移。</summary>
    public bool IsAreaCombatSkipped(CombatManager.CombatType type, string areaUid)
        => EE?.AreaSkipsCombat(areaUid, CombatRuleName(type)) == true;

    /// <summary>对抗类型 → 卡文/静止规则里的对抗名（"调查"/"战斗"/"势力"）。</summary>
    public static string CombatRuleName(CombatManager.CombatType type) => type switch
    {
        CombatManager.CombatType.Investigation => "调查",
        CombatManager.CombatType.Strength => "战斗",
        CombatManager.CombatType.Power => "势力",
        _ => "",
    };

    /// <summary>
    /// 阶段/步骤 → 中文显示名（单机 Arena 与联机 ArenaOl **共用唯一口径**，避免两处 switch 漂移）。
    /// 12 个步骤全部中文；对抗三步骤带"{N}地区"前缀（如"1地区调查步骤"，areaNo &lt;= 0 时不带前缀）。
    /// </summary>
    public static string GetPhaseDisplayName(TurnPhase phase, int areaNo = 0)
        => phase switch
        {
            TurnPhase.StartReset           => "重置步骤",
            TurnPhase.StartPrepare         => "准备步骤",
            TurnPhase.StartDraw            => "抓牌步骤",
            TurnPhase.ActionFirst          => "先手行动步骤",
            TurnPhase.ActionSecond         => "后手行动步骤",
            TurnPhase.PreCombat            => "机动轮",
            TurnPhase.CombatInvestigation  => areaNo > 0 ? $"{areaNo}地区调查步骤" : "调查对抗步骤",
            TurnPhase.CombatStrength       => areaNo > 0 ? $"{areaNo}地区战斗步骤" : "战斗对抗步骤",
            TurnPhase.CombatPower          => areaNo > 0 ? $"{areaNo}地区势力步骤" : "势力对抗步骤",
            TurnPhase.EndCleanup           => "结束步骤",
            TurnPhase.EndRecover           => "恢复步骤",
            TurnPhase.EndPassTurn          => "移交先手",
            TurnPhase.Mulligan             => "调度步骤",
            _                              => phase.ToString()
        };

    /// <summary>
    /// 【规则简化】免让过步骤：这些步骤结算完自身效果后**直接推进**到下一步骤，
    /// 不等待双方连续让过（重置步骤 / 恢复步骤 / 移交先手）。
    /// 移交先手（EndPassTurn）本就直接 NextTurn，无需让过；重置/恢复由 `DoReset`/`DoRecover` 末尾自动推进。
    /// </summary>
    public static bool IsAutoAdvanceStep(TurnPhase phase)
        => phase == TurnPhase.StartReset
        || phase == TurnPhase.EndRecover
        || phase == TurnPhase.EndPassTurn;

    /// <summary>供 UI 直接取"当前步骤中文名"（内部带上当前对抗地区序号）。</summary>
    public string CurrentPhaseDisplayName =>
        GetPhaseDisplayName(CurrentPhase, IsCombatPhase(CurrentPhase) ? CurrentCombatAreaNo : 0);

    /// <summary>
    /// 回合状态标签四行文案（单机 Arena 与联机 ArenaOl **共用唯一口径**）：
    /// ① 第n回合　② 先手：P1/P2（未产生时"先手：未产生"）　③ 步骤中文名（含对抗地区）
    /// ④ 行动权：P1/P2（无人持有时"行动权：无"）。View 只做 `Label.Text = turn.TurnStatusText`。
    /// </summary>
    public string TurnStatusText =>
        $"第{TurnCount}回合\n{FirstPlayerDisplayText}\n{CurrentPhaseDisplayName}\n"
        + (PriorityPlayerId > 0 ? $"行动权：P{PriorityPlayerId}" : "行动权：无");

    /// <summary>是否为按地区逐个结算的对抗步骤（需要带地区序号显示）。</summary>
    public static bool IsCombatPhase(TurnPhase phase)
        => phase == TurnPhase.CombatInvestigation
        || phase == TurnPhase.CombatStrength
        || phase == TurnPhase.CombatPower;

    /// <summary>
    /// 结算"当前地区游标所指地区"的指定对抗，结算完毕后分配优先权、重置让过计数，
    /// 等待双方连续让过才由 AdvancePhase 推进到本地区的下一种对抗（或下一地区 / 结束阶段）。
    /// 若结算过程中有条目入栈（如地区赢取），同样在该优先权窗口内让过结算。
    /// </summary>
    private void ResolveCurrentAreaCombat(CombatManager.CombatType type, string stepName)
    {
        string areaUid = CurrentCombatAreaUid;
        // 地区编号以 AreaManager 注册的"真实槽位编号"为准（与 Arena UI 一致），而非游标索引，
        // 避免地区被替换/前移后游标序号与显示编号错位导致串地区结算。
        int areaNo = _areaManager != null ? _areaManager.GetSlotIndex(areaUid) : (_combatAreaIndex + 1);

        if (_combatManager == null || string.IsNullOrEmpty(areaUid))
        {
            // 无对抗管理器 / 无地区：仍然给出让过窗口，保持"每步骤都有优先权"的一致性
            GrantPriorityAfterCombatStep(stepName, areaNo);
            return;
        }

        if (VerboseSteps) GD.Print($"【{stepName}】结算地区{areaNo}（{areaUid}）的{stepName.Replace("步骤", "对抗")}");

        // 【P3·K18】地区级"略过对抗"（JC098 陷入疯狂的人群「本地区略过调查对抗步骤」）：
        // 该地区在本步骤**不结算对抗**（不产生胜负、不加势力标志、不开分配窗口/伤害分配），
        // 但仍给出让过窗口 —— 与"无对抗管理器 / 无地区"的降级路径同款，保持"每步骤都有优先权"的一致性。
        if (IsAreaCombatSkipped(type, areaUid))
        {
            GD.Print($"【{stepName}】地区{areaNo}（{areaUid}）被『略过对抗』静止规则跳过：不结算对抗，仅给优先权窗口");
            GrantPriorityAfterCombatStep(stepName, areaNo);
            return;
        }

        // 联机服务端：对抗决策经 CombatManager 网络化异步结算（与单机事件窗口路径并列）。
        if (InputGateway != null && _combatManager.InputGateway != null)
        {
            _ = ResolveCurrentAreaCombatAsync(type, stepName, areaUid, areaNo);
            return;
        }

        _combatManager.ResolveAreaCombat(type, areaUid, areaNo,
            () => GrantPriorityAfterCombatStep(stepName, areaNo));
    }

    /// <summary>联机服务端对抗异步结算：await 网关回收决策后，再走统一的让过收尾。</summary>
    private async System.Threading.Tasks.Task ResolveCurrentAreaCombatAsync(
        CombatManager.CombatType type, string stepName, string areaUid, int areaNo)
    {
        // 对抗窗口为原子操作：期间拒绝让过/出牌，与单机对抗窗口 EnterAtomic 语义一致。
        EnterAtomic();
        try
        {
            await _combatManager.ResolveAreaCombatAsync(type, areaUid, areaNo);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[TurnManager] 联机对抗结算异常: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ExitAtomicWhenIdle();
        }
        GrantPriorityAfterCombatStep(stepName, areaNo);
        OnAsyncSettlementCompleted?.Invoke();
    }

    /// <summary>
    /// 单次对抗（某地区的调查/战斗/势力）结算完毕后的收尾：分配优先权并等待双方连续让过。
    /// </summary>
    private void GrantPriorityAfterCombatStep(string stepName, int areaNo)
    {
        PriorityPlayerId = GetPriorityPlayer();
        ResetPassWindow();
        int stackCount = _stackManager?.Entries.Count ?? 0;
        if (VerboseSteps) GD.Print($"【{stepName}】地区{areaNo} 对抗结算完毕（堆叠 {stackCount} 项），行动权: 玩家{PriorityPlayerId}，等待双方连续让过");

        // 三种对抗（调查/战斗/势力）共用收尾点：与 ResolveStackTop 收尾点对称，
        // 同步结算堆叠清空后统一检查"是否有地区满足赢取条件"。
        // 修复：原先仅 ResolveStackTop（入栈路径，如 JC035 准备阶段）会触发 TryStartAreaWin，
        // 而正常势力对抗在 CombatPower 阶段内同步结算、不入栈，导致赢取地区永远不被触发。
        if (stackCount == 0 && _areaManager != null && _stackManager != null)
        {
            _areaManager.TryStartAreaWin(_stackManager);
        }

        OnPriorityChanged?.Invoke(PriorityPlayerId);
    }

    private void DoCleanup()
    {
        if (VerboseSteps) GD.Print("【结束步骤】广播 OnTurnEnded 事件，收集回合结束触发（OnTurnEnded）并入堆叠");

        // 在结束步骤（而非 DoPassTurn）广播：此处后续还有优先权窗口可供双方响应、堆叠可正常结算；
        // DoPassTurn 紧接着 NextTurn()，此时入栈的触发将没有结算机会。
        // 系统级回合事件：无单一引发玩家（NoPlayer=-1）；ExtraInt 带回合号供条件判定使用。
        EE.CurrentTurn = TurnCount;
        _gameEventBus?.Emit(Script.Data.Effects.GameEvent.OnTurnEnded,
            new Script.Data.Effects.TriggerContext { ExtraInt = TurnCount });

        // 步骤开始：当前优先玩家获得优先权，等待双方连续让过推进
        BeginStepPriority("结束步骤");
    }

    // ============================================================
    // 恢复步骤：手牌上限检查点 + 弃牌循环
    // ============================================================
    /// <summary>手牌上限（可配置常量，默认 7）。超过此数的玩家在恢复步骤上限检查点必须弃到恰好该数。</summary>
    public int HandLimit = 7;

    // 恢复步骤内运行态
    private bool _recoverTriggerHold;    // 恢复步骤内触发窗口挂起：阻止提前离开恢复步骤
    private bool _recoverDiscardRunning; // 防止异步重入
    private bool _deferRecoverTriggers;  // 双方都超上限时：延迟收集弃牌触发，双方都弃完再统一开窗
    private System.Collections.Generic.List<Script.Data.Effects.StackEffectInstance> _deferredRecoverTriggers
        = new System.Collections.Generic.List<Script.Data.Effects.StackEffectInstance>();

    /// <summary>
    /// 恢复步骤能否离开（推进到下一阶段）？
    /// 仅当处于恢复步骤、双方手牌都 ≤ 手牌上限、且没有任何挂起的触发窗口时才允许离开。
    /// </summary>
    private bool RecoverCanLeave()
    {
        if (CurrentPhase != TurnPhase.EndRecover) return true;
        if (_recoverTriggerHold) return false;
        int h1 = GetHandOf(1).Count;
        int h2 = GetHandOf(2).Count;
        return h1 <= HandLimit && h2 <= HandLimit;
    }

    private void BeginDeferRecoverTriggers()
    {
        _deferRecoverTriggers = true;
        _deferredRecoverTriggers.Clear();
    }

    private void EndDeferRecoverTriggersAndFlush()
    {
        _deferRecoverTriggers = false;
        if (_deferredRecoverTriggers.Count == 0) return;
        var batch = _deferredRecoverTriggers;
        _deferredRecoverTriggers = new System.Collections.Generic.List<Script.Data.Effects.StackEffectInstance>();
        RunTriggerPhaseDispatch(batch);
    }

    /// <summary>
    /// 恢复步骤手牌上限循环（核心循环）：
    /// 每当堆叠清空（或恢复步骤首次进入）检查双方手牌；任一玩家超过上限则弃掉多余张数，
    /// 弃牌触发的效应进入堆叠，结算后再次检查，直到双方都 ≤ 上限且堆叠为空。
    /// 仅作用于恢复步骤内部（决策：scope＝仅恢复步骤内）。
    /// </summary>
    private async void RunRecoverHandLimitAsync()
    {
        if (_recoverDiscardRunning) return;
        _recoverDiscardRunning = true;
        try
        {
            while (!RecoverCanLeave())
            {
                // 有触发待结算或触发窗口挂起：交给既有让过/结算流程驱动，不在此重入
                if ((_stackManager?.Entries.Count ?? 0) > 0 || _recoverTriggerHold)
                    break;

                int fp = FirstPlayerId;
                int sp = (fp == 1) ? 2 : 1;
                int overFp = System.Math.Max(0, GetHandOf(fp).Count - HandLimit);
                int overSp = System.Math.Max(0, GetHandOf(sp).Count - HandLimit);
                bool both = overFp > 0 && overSp > 0;

                if (both) BeginDeferRecoverTriggers();
                try
                {
                    if (overFp > 0) await DiscardToHandLimitAsync(fp, fp, overFp);
                    if (overSp > 0) await DiscardToHandLimitAsync(sp, sp, overSp);
                }
                catch (System.Exception ex)
                {
                    // 防御：单次弃牌流程内任何异常都不允许中断"恢复步骤推进"（否则弃牌完成却不离开恢复步骤）。
                    GD.PrintErr($"⚠ [TurnManager] 恢复步骤弃牌流程异常（继续尝试推进）：{ex.Message}");
                }
                finally { if (both) EndDeferRecoverTriggersAndFlush(); }

                // 弃牌后若又有触发入堆叠/窗口挂起：跳出，交由既有流程驱动；否则回到循环顶部重新检查手牌
            }

            // 【推进判定】正常情况下双方都 ≤ 上限且堆叠为空 → 推进。
            // 兜底：若因"堆叠非空/触发窗口挂起"跳出循环，则由既有让过/结算流程（Pass / RunTriggerPhase 完成回调）
            // 再次驱动本循环或推进；此处仅在确实可离开时才调用 AdvancePhase。
            if (RecoverCanLeave() && (_stackManager?.Entries.Count ?? 0) == 0 && !_recoverTriggerHold)
            {
                AdvancePhase();
                // 【联机·立即下发快照】恢复步骤推进（EndRecover→EndPassTurn→下一回合）后主动通知服务端下发快照，
                // 否则客户端阶段标签/战场会停留在"恢复步骤"（联机 UI 完全由快照驱动）。
                NotifyResolutionSettled();
            }
        }
        finally
        {
            _recoverDiscardRunning = false;
        }
    }

    /// <summary>
    /// 让某玩家恰好弃掉 requiredCount 张手牌（手牌上限弃牌）。
    /// 联机：复用既有「一次选择 N 张」多选框（ChoiceRequest.Kind.SelectHandCards，ForceChoice＋NumberMin=NumberMax=requiredCount）。
    /// 单机：复用既有选牌窗逐张确认，强制收集恰好 requiredCount 张。
    /// </summary>
    private async System.Threading.Tasks.Task DiscardToHandLimitAsync(int discarderId, int chooserPlayerId, int requiredCount)
    {
        var hand = GetHandOf(discarderId);
        if (hand.Count <= HandLimit) return;
        // 【2026-09·原子操作期间的玩家选择】恢复步骤「手牌上限弃牌」是**强制玩家决策**：
        // 玩家选完之前必须阻塞进程——期间只有持续效果可以影响游戏，不得有新的行动/触发效果进入堆叠
        // （单机窗口等待与 `WaitUIRefreshAsync` 帧等待都在本持锁段内）。用 try/finally 保证异常也释放。
        BeginDecisionHold();
        try
        {
            await DiscardToHandLimitCoreAsync(discarderId, chooserPlayerId, requiredCount, hand);
        }
        finally
        {
            EndDecisionHold();
        }
    }

    /// <summary>`DiscardToHandLimitAsync` 的实体（决策持锁由外层包住，便于 try/finally 配对释放）。</summary>
    private async System.Threading.Tasks.Task DiscardToHandLimitCoreAsync(int discarderId, int chooserPlayerId,
        int requiredCount, System.Collections.Generic.List<CardInstance> candidates)
    {
        if (InputGateway != null)
        {
            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = discarderId,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectHandCards,
                Prompt = $"恢复步骤·手牌上限：你必须恰好弃掉 {requiredCount} 张手牌",
                ForceChoice = true,
                NumberMin = requiredCount,
                NumberMax = requiredCount,
            };
            foreach (var c in candidates)
            {
                if (c == null) continue;
                req.OptionUids.Add(c.UID);
                req.OptionLabels.Add(c.PlayerTemplate?.CardName ?? c.UID);
                req.OptionCardRefs.Add(global::ZoneManager.ProjectCard(c, CardZone.Hand, discarderId));
            }
            var result = await InputGateway.RequestChoice(discarderId, req);
            if (result != null && result.SelectedUids.Count > 0)
            {
                var toDiscard = candidates.FindAll(c => c != null && result.SelectedUids.Contains(c.UID));
                if (toDiscard.Count > requiredCount) toDiscard = toDiscard.GetRange(0, requiredCount);
                if (toDiscard.Count > 0)
                {
                    DiscardCards(discarderId, toDiscard, toDiscard.Count);
                    // 【联机·立即下发快照】弃牌改了 Model（手牌→墓地），必须主动通知服务端下发快照，
                    // 否则客户端 UI（阶段标签/手牌/双方墓地）会一直停留在旧状态（＝"回合已推进但界面还在恢复步骤"）。
                    // 这一步同时让"后手弹窗前能看到先手弃牌结果"成立。
                    NotifyResolutionSettled();
                    // 【顺序保证】弃牌已改 Model：等 UI 真正刷新（联机由客户端乐观回显 + 快照收敛呈现）后再继续。
                    await WaitUIRefreshAsync();
                }
            }
        }
        else
        {
            var remaining = new System.Collections.Generic.List<CardInstance>(candidates);
            var picked = new System.Collections.Generic.List<CardInstance>();
            for (int i = 0; i < requiredCount && remaining.Count > 0; i++)
            {
                CardInstance chosen = null;
                if (EE?.UIHooks != null)
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<CardInstance>();
                    EE.UIHooks.PromptDiscard(discarderId, chooserPlayerId, remaining, (c) => tcs.TrySetResult(c));
                    chosen = await tcs.Task;
                }
                if (chosen == null) chosen = remaining[0]; // 无 UI 或取消：强制取第一张（手牌上限为强制弃牌）
                picked.Add(chosen);
                remaining.Remove(chosen);
            }
            if (picked.Count > 0)
            {
                DiscardCards(discarderId, picked, picked.Count);
                // 【统一通知】弃牌已改 Model：单机无订阅者（无副作用），联机由 MatchHost 据此下发快照。
                NotifyResolutionSettled();
                // 【顺序保证】单机：弃牌已改 Model，等一帧把墓地/手牌真正渲染出来，后手弹窗前即可见。
                await WaitUIRefreshAsync();
            }
        }
    }

    /// <summary>
    /// 等 UI 完成一次刷新（View 的帧同步点）。无 UI（headless/测试）时为已完成任务，不引入等待。
    /// 收口所有"改完 Model 需让表现层追上一帧"的调用，避免散落各处直接摸 UI。
    /// </summary>
    private System.Threading.Tasks.Task WaitUIRefreshAsync()
        => EE?.UIHooks != null ? EE.UIHooks.WaitForUIRefresh() : System.Threading.Tasks.Task.CompletedTask;

    private void DoRecover()
    {
        if (VerboseSteps) GD.Print("【恢复步骤】");
        _cardManager?.ResetAllDamage();                          // 1) 移除所有角色上的伤害
        EffectEngine.Instance?.ExpireTurnEndEffects(TurnCount);  // 2) 所有注明"直到回合结束"的效果在此时结束
        BeginStepPriority("恢复步骤");
        TryAutoAdvance();                                        // 3) 上限检查点（首次）经门控接入弃牌循环
    }

    private void DoPassTurn()
    {
        GD.Print($"=== [TurnManager] 回合 {TurnCount} 结束 ===");
        OnTurnEnded?.Invoke(TurnCount);
        NextTurn();
    }

    // ==================== C/S 化 P1-A：回合状态快照导出 ====================
    /// <summary>导出整局状态快照（聚合 ZoneManager + StackManager + 本类回合字段）。</summary>
    public Script.Net.GameSnapshot ToSnapshot()
    {
        var snap = new Script.Net.GameSnapshot
        {
            TurnCount = TurnCount,
            FirstPlayerId = FirstPlayerId,
            PriorityPlayerId = PriorityPlayerId,
            CurrentPhase = (int)CurrentPhase,
            // 【联机】对抗步骤的地区序号随快照下行（客户端没有权威地区游标，否则联机只显示"调查对抗步骤"）。
            CombatAreaNo = IsCombatPhase(CurrentPhase) ? CurrentCombatAreaNo : 0,
            Zones = _zoneManager?.ToSnapshot() ?? new System.Collections.Generic.List<Script.Net.ZoneCardRef>(),
            Stack = StackManagerRef?.ToSnapshot() ?? new System.Collections.Generic.List<Script.Net.StackEntrySnap>(),
        };
        // 玩家快照（可用法力等运行时状态）
        var pm = PlayerManagerRef;
        if (pm != null)
        {
            for (int pid = 1; pid <= 2; pid++)
            {
                snap.Players.Add(new Script.Net.PlayerSnapshot
                {
                    PlayerId = pid,
                    AvailableMana = pm.GetAvailableMana(pid),
                    IsGameOver = pm.IsGameOver,
                    ScoreTotal = pm.GetScoreTotal(pid),
                    // 【P1】全局封锁秘密派遣（JC068-B）：权威在 EffectEngine 的封锁表，客户端没有该引擎，
                    // 必须随快照下行，否则客户端派遣门禁与服务端不一致。
                    DeployCovertBlocked = EE?.IsDeployCovertBlocked(pid) ?? false,
                });
            }
        }
        return snap;
    }

    // ==================== C/S 化·第二批：客户端投影回合状态恢复 ====================
    /// <summary>
    /// 从服务端整局快照恢复客户端投影的回合运行时状态（仅联网投影/重连调用，对单机零影响）。
    /// 恢复：回合计数 / 阶段 / 先手玩家 / 优先权玩家。
    /// 客户端投影的 TurnManager 只做状态投影，不驱动规则；堆叠由 StackManager.ApplySnapshot 单独恢复。
    /// </summary>
    public void ApplySnapshot(Script.Net.GameSnapshot snap)
    {
        if (snap == null) return;
        TurnCount = snap.TurnCount;
        CurrentPhase = (TurnPhase)snap.CurrentPhase;
        FirstPlayerId = snap.FirstPlayerId;
        PriorityPlayerId = snap.PriorityPlayerId;
        _projectedCombatAreaNo = snap.CombatAreaNo;   // 对抗地区序号（联机显示"1地区调查步骤"用）
    }
}
