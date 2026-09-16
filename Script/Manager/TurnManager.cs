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
        EndPassTurn         // 移交先手
    }

    // ============================================================
    // 公开属性
    // ============================================================
    public int TurnCount { get; internal set; } = 0;
    public TurnPhase CurrentPhase { get; internal set; } = TurnPhase.StartReset;
    public int FirstPlayerId { get; internal set; } = 1;
    public int PriorityPlayerId { get; internal set; } = 0;

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
        // 仅当没有任何模态窗口仍打开时才解锁；Arena 会在最后一个窗口关闭时调用
        if (_openModalCount <= 0) _atomicLock = false;
    }
    private int _openModalCount;   // 由 Arena 在 LockModal/UnlockModal 时维护
    public void NotifyModalOpened() { _openModalCount++; EnterAtomic(); }
    public void NotifyModalClosed()
    {
        _openModalCount = Math.Max(0, _openModalCount - 1);
        if (_openModalCount == 0) _atomicLock = false;
    }

    /// <summary>同步原子段结束时调用：若没有任何模态窗口仍打开，则原子操作已完整结束，解除规则层锁。</summary>
    public void ExitAtomicWhenIdle()
    {
        if (_openModalCount <= 0) _atomicLock = false;
    }

    // ============================================================
    // 内部状态
    // ============================================================
    private int _passCount = 0;

    /// <summary>对抗阶段的地区游标：本回合参与对抗的地区列表快照（按编号从小到大）。
    /// 进入 ActionSecond → CombatInvestigation 时建立，避免结算过程中地区增删导致索引错乱。</summary>
    private System.Collections.Generic.List<string> _combatAreaUids = new System.Collections.Generic.List<string>();
    /// <summary>当前正在结算对抗的地区索引（0-based，对应 _combatAreaUids）。</summary>
    private int _combatAreaIndex = 0;

    /// <summary>当前对抗地区 UID（无地区时为空串）。</summary>
    public string CurrentCombatAreaUid =>
        (_combatAreaIndex >= 0 && _combatAreaIndex < _combatAreaUids.Count) ? _combatAreaUids[_combatAreaIndex] : "";
    /// <summary>当前对抗地区序号（1-based，无地区时为 0），供 UI 显示"地区N 调查对抗"。
    /// 取 AreaManager 注册的<b>真实槽位编号</b>（与 Arena UI 一致），避免游标序号错位。</summary>
    public int CurrentCombatAreaNo
    {
        get
        {
            string uid = CurrentCombatAreaUid;
            if (uid == "") return 0;
            int realNo = _areaManager != null ? _areaManager.GetSlotIndex(uid) : 0;
            return realNo > 0 ? realNo : _combatAreaIndex + 1;
        }
    }

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
            entry.SourceZoneDetailSnapshot = src.ZoneDetailId;
            entry.SourceControllerSnapshot = src.ControllerId;
            entry.HasSourceZoneSnapshot = true;
        }
        GD.Print($"🎯 [目标锁定] 条目 {entry.SourceCardUid ?? entry.SourceCard?.UID} 取目标卡 {entry.TargetCardUid}" +
                 $"（名称={target.PlayerTemplate?.CardName}, 当前区={target.ZoneDetailId}, 控制者={target.ControllerId}），" +
                 $"记录入栈快照：版本号={entry.TargetVersionId}，来源地区={entry.SourceZoneDetailSnapshot}，来源控制者={entry.SourceControllerSnapshot}");
    }

    /// <summary>注入 Arena 引用（用于打开触发排序窗口 / 选目标窗口）。</summary>
    public void SetTriggerView(ITriggerView view) => _view = view;

    /// <summary>注入对抗结算管理器（由 GameInitializer 调用）</summary>
    public void SetCombatManager(CombatManager combatManager)
    {
        _combatManager = combatManager;
    }

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
        _passCount = 0;
        _combatAreaUids.Clear();
        _combatAreaIndex = 0;
        _atomicLock = false;
        _openModalCount = 0;
        GD.Print("[TurnManager] Reset: 回合状态已复位到未开局");
    }

    public void StartGame()
    {
        TurnCount = 1;
        FirstPlayerId = 1;
        _passCount = 0;
        _combatAreaUids.Clear();
        _combatAreaIndex = 0;
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
        _passCount = 0;
        _combatAreaUids.Clear();
        _combatAreaIndex = 0;
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
    // 玩家操作：Pass / PlayCard
    // ============================================================

    /// <summary>
    /// 玩家让过。仅在行动阶段有效。双方连续让过则推进到下一阶段。
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

            _passCount++;
            GD.Print($"🟢 [TurnManager] 玩家{playerId} 让过 (连续让过计数: {_passCount}) [堆叠非空，阶段={CurrentPhase}]");

            if (_passCount >= 2)
            {
                _passCount = 0;
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
                PriorityPlayerId = PriorityPlayerId == 1 ? 2 : 1;
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

        _passCount++;
        GD.Print($"🟢 [TurnManager] 玩家{playerId} 让过 (连续让过计数: {_passCount}) [阶段={CurrentPhase}]");

        // 堆叠为空：双方连续让过则推进阶段
        if (_passCount >= 2)
        {
            _passCount = 0;
            // 注：不再对外广播"玩家0"作为阶段切换过渡态。原因：OnPriorityChanged 的订阅者（Arena UI）
            // 为同步回调，若先 Invoke(0) 再 AdvancePhase，订阅者会在 CurrentPhase 仍为旧阶段、
            // PriorityPlayerId=0 的窗口内同步尝试 Pass/行动，触发"行动权在玩家0"误报。
            // 直接 AdvancePhase → EnterPhase → DoXxx 内部以有效玩家(1/2) 复位并发出 OnPriorityChanged，
            // UI 不经历玩家0瞬间，彻底避免下游误触发。
            GD.Print("⏩ [TurnManager] 双方连续让过，推进阶段");
            AdvancePhase();
        }
        else
        {
            PriorityPlayerId = PriorityPlayerId == 1 ? 2 : 1;
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
    }

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
                _passCount = 0;
                GD.Print($"⏳ [Trigger] 触发阶段结束，优先权给优先玩家{PriorityPlayerId}");
                OnPriorityChanged?.Invoke(PriorityPlayerId);
                onAllCompleted?.Invoke();   // 全部所有者窗口结束后回调（如对抗前触发需立即结算）
                return;
            }
            int owner = owners[cursor];
            cursor++;
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
        if (InputGateway != null)
        {
            _ = RunTriggerPhaseAsync(allTriggers, () =>
            {
                onAllCompleted?.Invoke();
                OnAsyncSettlementCompleted?.Invoke();
            });
            return;
        }
        RunTriggerPhase(allTriggers, onAllCompleted);
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
            _passCount = 0;
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

    /// <summary>网络化触发入栈：逐个 await 选目标（若需），随后入栈。</summary>
    private async System.Threading.Tasks.Task EnsureTargetThenPushAsync(Script.Data.Effects.StackEffectInstance t)
    {
        if (NeedSelectAnyTarget(t) && string.IsNullOrEmpty(t.SelectedTargetUid))
        {
            var valid = GetValidTargetUidsForTrigger(t);
            if (valid.Count == 0)
            {
                GD.Print($"[Trigger] {t.DisplayName} 无合法目标，跳过入栈");
                return;
            }

            // 推送目标：效果来源卡的 ControllerId（明确以 controller 为准，而非 Owner/触发者）。
            int controller = t.Source?.ControllerId ?? t.ControllerId;
            var req = new Script.Net.ChoiceRequest
            {
                PlayerId = controller,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectTarget,
                SourceUid = t.Source?.UID,
                OptionUids = valid,
                Prompt = $"请为 {t.DisplayName} 选择目标",
                ForceChoice = true,   // 对齐单机 TargetSelectionWindow：选目标不可取消/点窗外关闭，必须选一个
            };
            var result = await InputGateway.RequestChoice(controller, req);
            if (result == null || result.Cancelled || string.IsNullOrEmpty(result.SelectedUid))
            {
                GD.Print($"[Trigger] {t.DisplayName} 玩家取消/未选目标，跳过入栈");
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
        EnsureTargetThenPush(t, () => PushOrderedTriggers(list, index + 1, onAllDone));
    }

    /// <summary>确保触发已选定目标后压入堆叠。需目标且未选时，先通过 Arena 弹选目标窗口收集目标。</summary>
    private void EnsureTargetThenPush(Script.Data.Effects.StackEffectInstance t, System.Action onPushed)
    {
        if (NeedSelectAnyTarget(t) && string.IsNullOrEmpty(t.SelectedTargetUid) && _view != null)
        {
            // 新系统地区目标（机动 ManeuverTrigger）：直接取"所有其他争夺区"作为候选。
            // 旧 TriggerEffectCollector 已随旧系统迁移删除，普通反应触发（如 XG01 抽牌）不进此分支
            // （其 RequiresTarget 为 false，见 StackEffectInstance）。
            // 候选计算与异步选目标共用 GetValidTargetUidsForTrigger（单一真相源）。
            List<string> valid = GetValidTargetUidsForTrigger(t);
            if (valid.Count == 0)
            {
                GD.Print($"[Trigger] {t.DisplayName} 无合法目标，跳过入栈");
                onPushed?.Invoke();
                return;
            }
            _view.RequestTargetSelection(t, valid, (uid) =>
            {
                t.SelectedTargetUid = uid;
                PushTriggerToStack(t);
                onPushed?.Invoke();
            });
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
            TargetAreaId = t.TargetAreaId,           // "X对抗获胜"类触发：对抗获胜地区 UID，供 ResolveEffect→WinnerInSameArea 判定（Y 方案）
            StackEffect = t,
        };

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
        var result = new List<string>();
        if (t.Source == null || _cardManager == null || _areaManager == null) return result;
        var kind = t.EffectDataRef?.Target?.Kind ?? TargetKind.None;
        // 1) 取来源卡所在争夺区对应的 SLOT 编号
        int slotIndex = _areaManager.GetSlotIndex(t.Source.ZoneDetailId);
        // 2) 收集同一 SLOT 下所有争夺区 UID（含来源区与"另一争夺区"）
        var areaUids = new List<string>();
        if (_zoneManager != null)
        {
            foreach (var uid in _zoneManager.GetAllAreaSlotUids())
            {
                if (_areaManager.GetSlotIndex(uid) == slotIndex) areaUids.Add(uid);
            }
        }
        else
        {
            areaUids.Add(t.Source.ZoneDetailId);
        }
        // 3) 合并各争夺区实例，按 Kind 过滤；默认排除来源自身，除非该效果显式允许以自身为目标（如袭击可打自己）
        bool canTargetSelf = t.EffectDataRef?.Target?.CanTargetSelf ?? false;
        foreach (var uid in areaUids)
        {
            foreach (var inst in _cardManager.GetAreaInstancesBySlot(uid))
            {
                if (inst == t.Source && !canTargetSelf) continue;
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
                if (!string.IsNullOrEmpty(entry.TargetCardUid))
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
                var (valid, reason) = TargetValidity.Check(source, target, desc, snapshot);
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
                EE.ResolveSorcery(card, entry.TargetCardUid, entry.TargetVersionId);
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
                    // entry.WinnerId：对抗获胜方玩家 ID（威名等"X对抗获胜"触发由 TriggerRegistry 收集时经
                    // StackEffectInstance.WinnerId → PushTriggerToStack 写入），透传给 ResolveEffect 供 WinnerIsSelf 判定。
                    EE.ResolveEffect(entry.EffectDataEffect, src, tgt, entry.TargetAreaId ?? "", entry.WinnerId, entry.TargetVersionId);
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
    /// </summary>
    public bool PlayCard(int playerId, string cardUid, string targetUid = null, string targetCardUid = null)
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
        if (!isSorceryCard)
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

        // 4. 不可从手牌正常打出的卡（费用为空 = -1，只能特殊方式进场）
        if (tmpl.Cost < 0)
        {
            GD.Print($"❌ [TurnManager] 该卡无法从手牌正常打出（Cost={tmpl.Cost}）");
            return false;
        }

        // 5. 忠诚校验（6.1）：颜色 + 领域需求必须同时满足（允许冗余）
        if (!_playerManager.CanPayLoyalty(playerId, tmpl))
        {
            GD.Print($"❌ [TurnManager] 忠诚不足，无法打出 {tmpl.CardName}");
            return false;
        }

        // 6. 费用支付（6.2）：横置资产区卡牌产生无色费用
        // 进入原子操作：从实际开始改变游戏状态（费用支付/入堆叠）起加锁；
        // 之前的阶段/行动权/费用校验等失败均不占用原子锁（修复：StartReset 拖拽误锁导致双方无法让过）。
        EnterAtomic();   // 标记本次打出为原子操作；若随后触发窗口打开，Arena 的 NotifyModalOpened 会维持锁
        // P2：先减玩家级时效费用修正（如 JC012 的"下一张帷幕守望牌费用-1"）
        EE.CurrentTurn = TurnCount;
        int cost = tmpl.Cost - EE.GetCostModification(playerId, tmpl.CardName, tmpl.Organization);
        if (cost < 0) cost = 0;
        if (cost > 0)
        {
            if (_playerManager.GetAvailableMana(playerId) < cost)
            {
                GD.Print($"❌ [TurnManager] 可用费用不足！需要 {cost}，当前 {_playerManager.GetAvailableMana(playerId)}");
                ExitAtomicWhenIdle();   // 修复：解锁，避免原子锁卡死
                return false;
            }
            if (!_playerManager.SpendMana(playerId, cost))
            {
                GD.Print($"❌ [TurnManager] 费用支付失败");
                ExitAtomicWhenIdle();
                return false;
            }
            GD.Print($"[TurnManager] 玩家{playerId} 支付 {cost} 费用，剩余可用 {_playerManager.GetAvailableMana(playerId)}");
            // 一次性费用修正（"下一张"语义）：本张已成功支付并使用减免，消费移除该修正项（整回合项不移除）
            EE.ConsumeCostModification(playerId, tmpl.Organization);
        }

        // 资产横置状态已变化，通知 UI 刷新
        OnAssetsChanged?.Invoke();

        // 7. 入堆叠（费用/执行分离）：按卡类别构造结构化结算数据，等待响应窗口结算
        if (_stackManager != null)
        {
            var entry = new StackEntry(playerId, instance);
            entry.ControllerId = instance.ControllerId;   // 箭头按控制者着色（D31）
            entry.TargetAreaId = targetUid ?? "";
            entry.TargetCardUid = targetCardUid ?? "";
            entry.Targets.Clear();
            if (!string.IsNullOrEmpty(targetCardUid))
                GD.Print($"🎯 [取目标] 出牌 {instance.PlayerTemplate?.CardName}({cardUid}) 取目标卡 {targetCardUid}");
            else if (!string.IsNullOrEmpty(targetUid))
                GD.Print($"🎯 [取目标] 出牌 {instance.PlayerTemplate?.CardName}({cardUid}) 取目标地区 {targetUid}");

            bool isAttachment = (tmpl.CategoryFlag & CardCategory.Attachment) != 0;
            bool isSorcery = (tmpl.CategoryFlag & CardCategory.Sorcery) != 0;

            // A+B：打出阶段接入通用目标拦截系统（统一收口，三类卡都走）。
            // 事务/附属的"卡目标"选择交由此系统裁决（IsLegalPlayTarget 按 AllowedTargets 粗筛），
            // 不再由效果系统自行实现。角色卡落点地区也应校验地区合法存在（提前拦截"地区丢失"）。
            if (!string.IsNullOrEmpty(targetCardUid))
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
                if (!string.IsNullOrEmpty(entry.TargetCardUid))
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

        // 8. 重置让过计数（打出动作重置响应窗口）
        _passCount = 0;

        // 9. 卡牌进入堆叠后，优先权归"当前步骤优先获得行动权的玩家"（与谁打出无关）。
        //    规则：存在一个永远优先的玩家（先手，ActionSecond 内为后手）；只有该玩家让过，
        //    另一人才可能获得行动权。因此出牌后优先权不转移给对手。
        PriorityPlayerId = GetPriorityPlayer();
        GD.Print($"⏳ [TurnManager] 玩家{playerId} 出牌入堆叠，优先权归优先玩家{PriorityPlayerId}（响应窗口）");
        OnPriorityChanged?.Invoke(PriorityPlayerId);

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

    // ============================================================
    // 暗藏者现身（快速能力，AmbushReveal）
    // ============================================================
    /// <summary>
    /// 暗藏者现身（Reveal）：玩家有行动权时双击己方争夺区内背面朝上的暗藏者发动。
    /// 规则要点（核心需求 6.3 / 你确认的设计）：
    ///   1) 视为"打出卡牌"行为，需满足该卡正常打出的所有条件（忠诚需求 + 原始费用）；
    ///      本期不接额外费用（与打出牌系统一致，ExtraCost 暂未实现）。
    ///   2) 不改变所在地区：现身地区默认 = 原本暗藏的地区（不弹地区选择）。
    ///   3) 进堆叠：与打出卡牌走相同入栈路径（响应窗口让过结算），箭头指向原地区。
    ///      进堆叠时即翻面（IsCovert=false），堆叠区内保持正面，结算回地区仍是正面。
    ///   4) 回地区触发进场触发（EnterTrigger）是正常情况；同时额外广播现身触发（UncoverTrigger）。
    ///   5) 门禁 = 仅"有行动权(PriorityPlayerId==playerId)"，不要求行动阶段、不要求堆叠空（快速能力）。
    ///   6) 费用/忠诚/付费均按控制者(ControllerId)归属（与激活/机动一致，D28~D36）；
    ///      并校验发动者即控制者，防止第三方误触发。
    ///   7) 直接尝试发动，不满足则提示失败（不弹确认）。
    /// </summary>
    public bool RevealCovert(int playerId, string cardUid)
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

        // 费用校验与支付（按控制者，原始 Cost；额外费用本期不接）
        EE.CurrentTurn = TurnCount;
        int cost = tmpl.Cost - EE.GetCostModification(controller, tmpl.CardName, tmpl.Organization);
        if (cost < 0) cost = 0;
        if (cost > 0)
        {
            if (_playerManager.GetAvailableMana(controller) < cost)
            {
                GD.Print($"❌ [TurnManager] 暗藏者现身费用不足！需要 {cost}，当前 {_playerManager.GetAvailableMana(controller)}");
                ExitAtomicWhenIdle();
                return false;
            }
            if (!_playerManager.SpendMana(controller, cost))
            {
                GD.Print($"❌ [TurnManager] 暗藏者现身费用支付失败");
                ExitAtomicWhenIdle();
                return false;
            }
            GD.Print($"[TurnManager] 玩家{controller} 支付暗藏者现身费用 {cost}，剩余可用 {_playerManager.GetAvailableMana(controller)}");
            // 一次性费用修正（"下一张"语义）：本张已成功支付并使用减免，消费移除该修正项
            EE.ConsumeCostModification(controller, tmpl.Organization);
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

        // 重置让过计数 + 优先权归优先玩家（与打出卡牌一致）
        _passCount = 0;
        PriorityPlayerId = GetPriorityPlayer();
        GD.Print($"⏳ [TurnManager] 暗藏者现身入堆叠，优先权归优先玩家{PriorityPlayerId}（响应窗口）");
        OnPriorityChanged?.Invoke(PriorityPlayerId);

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
    /// 抽牌（数据层唯一收口）：经 DeckManager 抽卡 → CardManager 移动入手牌列表 → 触发 OnCardDrawn 通知 UI。
    /// UI 层不应直接调用 DeckManager.Draw；初始发牌、PM 命令 draw 均经此方法进行。
    /// </summary>
    public void DrawCardsForPlayer(int playerId, int count)
    {
        if (_deckManager == null || _cardManager == null) return;
        for (int i = 0; i < count; i++)
        {
            CardInstance card = playerId == 1 ? _deckManager.DrawPlayer1() : _deckManager.DrawPlayer2();
            if (card == null)
            {
                // 牌库耗尽：抽牌者（playerId）无法抽牌 → 该玩家立即失败（对手胜）。
                // 即使抽牌动作由对方效果触发，也只看"谁无法抽牌"判负（规则：需抽牌但无牌可抽时失败）。
                if (_playerManager != null && !_playerManager.IsGameOver)
                    _playerManager.TriggerGameOver(playerId == 1 ? 2 : 1);
                return;   // 立即结束抽牌，不再继续
            }
            _cardManager.MoveCardToHand(card);   // 委托 ZoneManager 设置 CurrentZone=Hand 并维护列表
            OnCardDrawn?.Invoke(playerId, card); // UI 层据此把对应 CardUI 从牌库取出
        }
    }

    /// <summary>
    /// 从牌库底抽牌（秘社激活效果用）。空库时抽牌者判负，与 DrawCardsForPlayer 同规则。
    /// </summary>
    public void DrawFromBottomForPlayer(int playerId, int count)
    {
        if (_deckManager == null || _cardManager == null) return;
        for (int i = 0; i < count; i++)
        {
            CardInstance card = playerId == 1 ? _deckManager.DrawFromBottomPlayer1() : _deckManager.DrawFromBottomPlayer2();
            if (card == null)
            {
                if (_playerManager != null && !_playerManager.IsGameOver)
                    _playerManager.TriggerGameOver(playerId == 1 ? 2 : 1);
                return;
            }
            _cardManager.MoveCardToHand(card);
            OnCardDrawn?.Invoke(playerId, card);
        }
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
        return !EE.IsDeployCovertBlocked(playerId);
    }

    public bool CanDeployCovert(CardInstance instance)
    {
        // 判定体抽取到 DeployCovertEligibility（单一真相源），联机客户端（快照投影、无 TurnManager 实例）
        // 复用同一静态函数做"拖起资格"预检；此处保持原签名与语义不变（单机行为零变化）。
        return DeployCovertEligibility.IsEligible(instance);
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

    private void EnterPhase(TurnPhase phase)
    {
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
            _passCount = 0;
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
        OnAssetsChanged?.Invoke();
        // 步骤开始：当前优先玩家获得优先权，等待双方连续让过推进
        BeginStepPriority("重置步骤");
    }

    private void DoPrepare()
    {
        if (VerboseSteps) GD.Print("【准备步骤】广播 OnTurnStarted（双方准备阶段触发点）");
        // 准备阶段为双方共有步骤：不归属单一回合玩家（-1），由每张卡的 EffectData 按各自控制者判定"对手"。
        _triggerEmitPoints?.OnTurnStarted();
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
        _passCount = 0;
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

    private void DoRecover()
    {
        if (VerboseSteps) GD.Print("【恢复步骤】清除所有伤害标记");
        _cardManager?.ResetAllDamage();
        // 步骤开始：当前优先玩家获得优先权，等待双方连续让过推进
        BeginStepPriority("恢复步骤");
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
    }
}
