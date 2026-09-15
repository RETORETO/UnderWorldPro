using Godot;
using System.Collections.Generic;
using Script.Data.Cards;
using Script.Enums;
using Script.Game;
using Script.Interfaces;
using Script.IO;

/// <summary>
/// GameInitializer — 负责创建和注入所有 Manager 依赖
/// Arena UI 通过此类获取已初始化的 Manager 引用，避免初始化逻辑散落在 UI 层
/// </summary>
[GlobalClass]
public partial class GameInitializer : Node
{
    /// <summary>装配级诊断日志开关（降噪）。默认 false：测试/联机只保留必要日志，避免刷屏；
    /// 调试装配流程时置 true 可看到逐条 ✅ 明细。不影响任何逻辑。</summary>
    public static bool VerboseSetup = false;

    // ============================================================
    // Manager 引用（只读，初始化后不可变）
    // ============================================================
    public CardManager CardManager { get; private set; }
    public PlayerManager PlayerManager { get; private set; }
    public DeckManager DeckManager { get; private set; }
    public TurnManager TurnManager { get; private set; }
    public AreaManager AreaManager { get; private set; }
    public WorldDeckManager WorldDeckManager { get; private set; }
    public ZoneManager ZoneManager { get; private set; }
    public CombatManager CombatManager { get; private set; }
    public StackManager StackManager { get; private set; }

    /// <summary>PM 调试命令管理器：由本 Composition Root 负责创建与生命周期，
    /// UI 回调注入由 Arena 在获取到引用后完成（回调本质是 View→逻辑的刷新通知）。</summary>
    public PMCommandManager PMCommandManager { get; private set; }

    /// <summary>玩家意图统一通道（命令总线雏形）：收口所有玩家主动意图，供 View 调用，
    /// 便于以后 C/S 化时在此层拦截并下发服务器。</summary>
    public PlayerActionService PlayerActionService { get; private set; }

    /// <summary>
    /// 本局唯一随机源。所有需要随机的 Manager 均从此处注入，
    /// 禁止各自 new Random()，否则牌序无法复现也无法同步。
    /// </summary>
    public IRandomSource RandomSource { get; private set; }

    /// <summary>
    /// 调试用固定种子：设为非 null 时每次运行牌序完全一致，便于复现依赖执行顺序的 bug。
    /// 正式运行请保持 null（自动生成随机种子）。
    /// </summary>
    [Export] public int DebugSeed = -1;

    /// <summary>本局加载的卡组（含卡组与秘社），供 SetupCouncils 读取秘社。由 InitializeAll 加载并缓存。</summary>
    private DeckList _loadedDeck;

    /// <summary>本局 EffectEngine 实例（静态单例治理：显式注入替代 EffectEngine.Instance）。</summary>
    private Script.Manager.EffectEngine _effectEngine;

    /// <summary>持续效果联动处理器（下沉到装配层，替代单机 Arena.Events 的 View 订阅，使联机服务端同样触发）。
    /// 具名字段便于每次 InitializeAll 先反订阅再订阅，避免重复累加。</summary>
    private System.Action<Script.Game.Entities.CardInstance, CardChangeKind> _cardStateContinuityHandler;

    // ============================================================
    // 初始化入口（由 Arena._Ready 调用）
    // ============================================================
    public void InitializeAll()
    {
        GD.Print("=== GameInitializer: 开始初始化所有 Manager ===");

        // 0. 注册全部卡效果（必须在任何卡加载/实例化之前，供 CardEffectFactory.EnsureEffects 查表）。
        //    按扩展包迭代时，可改为只注册当前系列（如 Cards.Effects.PlayerCards.JC.JCEffects.RegisterAll()）。
        Cards.Effects.AllEffects.RegisterAll();
        if (VerboseSetup) GD.Print("  ✅ 已注册全部卡效果构建器");

        // 0.5 随机源（必须最先创建：DeckManager / WorldDeckManager 洗牌都依赖它）
        //    DebugSeed >= 0 时使用固定种子，牌序可复现；否则自动生成。
        RandomSource = new SeededRandomSource(DebugSeed >= 0 ? DebugSeed : (int?)null);
        if (VerboseSetup) GD.Print($"  🎲 RandomSource 种子 = {((SeededRandomSource)RandomSource).Seed}" +
                 (DebugSeed >= 0 ? "（固定种子，牌序可复现）" : "（自动生成，记录此值可复现本局）"));

        // 0.8【MVC 收口】触发系统三实例（原 static class → 实例，统一由本 Composition Root 装配）。
        //   依赖顺序：GameEventBus → TriggerEmitPoints(bus) → TriggerRegistry(bus)。
        //   TriggerRegistry 构造时自动订阅 bus（SetDispatch(HandleEvent)）。
        var eventBus = new Script.Manager.GameEventBus();
        var triggerEmitPoints = new Script.Manager.TriggerEmitPoints(eventBus);
        var triggerRegistry = new Script.Manager.TriggerRegistry(eventBus);
        if (VerboseSetup) GD.Print("  ✅ 触发系统三实例（GameEventBus / TriggerEmitPoints / TriggerRegistry）");

        // 0.9【MVC 收口】EffectEngine 实例（原 static class → 实例 + Instance 单例）。
        // 由本 Composition Root 创建；8 个对局状态集合（持续注册表/光环/时效修正/封锁等）
        // 从 static 字段改为实例字段后，实例随场景释放即清空，根治"跨局封锁残留"类问题。
        var effectEngine = new Script.Manager.EffectEngine();
        _effectEngine = effectEngine;
        if (VerboseSetup) GD.Print("  ✅ EffectEngine（实例化，状态随实例生命周期）");

        // 触发系统在 EffectEngine 之前创建：此处补齐注入 EffectEngine 引用（静态单例治理）。
        triggerRegistry.EffectEngineRef = effectEngine;
        triggerEmitPoints.EffectEngineRef = effectEngine;

        // 1. CardManager（卡牌实例的创建、移动、查询）
        CardManager = new CardManager();
        AddChild(CardManager);
        CardManager.SetTriggerEmitPoints(triggerEmitPoints);   //【MVC 收口】注入触发广播封装层
        CardManager.EffectEngineRef = effectEngine;            // 静态单例治理：显式注入 EffectEngine
        // 持续效果联动下沉到装配层（原在单机 Arena.Events 的 View 订阅）：横置/翻面 → 重判持续前提/光环。
        // 使联机服务端（无 Arena）同样触发，修复 JC068 阿格斯IV"TAP 后对手仍不能暗藏"。
        _cardStateContinuityHandler = (card, kind) =>
        {
            if (card == null) return;
            if (kind == CardChangeKind.TappedChanged) effectEngine.ReevaluatePrereqs(card.ControllerId);
            else if (kind == CardChangeKind.CovertChanged) effectEngine.ReevaluateAuras(card.ControllerId);
        };
        CardManager.OnCardStateChanged -= _cardStateContinuityHandler;
        CardManager.OnCardStateChanged += _cardStateContinuityHandler;
        if (VerboseSetup) GD.Print("  ✅ CardManager");

        // 2. PlayerManager（玩家状态：能量、先手标志）
        PlayerManager = new PlayerManager();
        AddChild(PlayerManager);
        // 注入到 TargetValidity（静态目标校验链），供条件式限制（如 AttachedToCharacterIfNoMind）
        // 在取目标阶段评估"资产区是否具【心】"（心灵领域忠诚）。
        Script.Manager.TargetValidity.PlayerManagerRef = PlayerManager;
        if (VerboseSetup) GD.Print("  ✅ PlayerManager");

        // 3. DeckManager（卡组管理：洗牌、抽牌）
        DeckManager = new DeckManager();
        DeckManager.Initialize(CardManager, RandomSource);
        AddChild(DeckManager);
        if (VerboseSetup) GD.Print("  ✅ DeckManager");

        // 3.5 ZoneManager（统一区域移动，单一真相源）
        ZoneManager = new ZoneManager();
        AddChild(ZoneManager);
        ZoneManager.Initialize(CardManager, DeckManager);
        ZoneManager.SetTriggerEmitPoints(triggerEmitPoints);   //【MVC 收口】注入触发广播封装层
        ZoneManager.EffectEngineRef = effectEngine;            // 静态单例治理：显式注入 EffectEngine
        CardManager.SetZoneManager(ZoneManager);
        PlayerManager.SetZoneManager(ZoneManager);   // 忠诚/费用来自资产区
        if (VerboseSetup) GD.Print("  ✅ ZoneManager");

        // 4. TurnManager（回合流程、阶段切换）
        TurnManager = new TurnManager();
        AddChild(TurnManager);
        TurnManager.Initialize(DeckManager, PlayerManager, CardManager);
        TurnManager.SetZoneManager(ZoneManager);   // 基础行动收口需驱动 ZoneManager 移动
        TurnManager.EffectEngineRef = effectEngine;   // 静态单例治理：必须在 SetTriggerSystem 之前注入（其内部 EE.IsStackCircuitBroken）
        ZoneManager.TurnManagerRef = TurnManager;     // 静态单例治理：替代 ZoneManager 内 TurnManager.Instance
        TurnManager.SetTriggerSystem(triggerRegistry, triggerEmitPoints, eventBus);   //【MVC 收口】注入触发系统三实例
        if (VerboseSetup) GD.Print("  ✅ TurnManager");


        // 5. WorldDeckManager（世界牌库：地区卡牌堆）
        // 注意：仅在此时创建对象并持有引用；地区卡实例化的 Initialize 延迟到秘社(SetupCouncils)之后调用，
        // 以满足"玩家卡 → 秘社 → 地区"的实例化顺序（地区卡 CreateCardInstance 延后）。
        WorldDeckManager = new WorldDeckManager();
        AddChild(WorldDeckManager);
        if (VerboseSetup) GD.Print($"  ✅ WorldDeckManager (对象已创建，地区卡实例化延后至秘社之后)");

        // 6. AreaManager（地区生命周期管理）—— 需在 StackManager 之后（赢取检查依赖堆叠）
        // 6.6 StackManager（堆叠区：LIFO，响应窗口驱动）先创建
        StackManager = new StackManager();
        AddChild(StackManager);
        StackManager.SetCardManager(CardManager);     // 注入：D16 统一收口，Push 时自动登记目标版本号
        TurnManager.SetStackManager(StackManager);   // 注入：打出卡/让过结算依赖堆叠
        PlayerManager.SetStackManager(StackManager);  // 注入：游戏结束即时结算时清空未结算堆叠
        if (VerboseSetup) GD.Print("  ✅ StackManager");

        AreaManager = new AreaManager();
        AddChild(AreaManager);
        AreaManager.Initialize(WorldDeckManager, ZoneManager, StackManager);
        TurnManager.SetAreaManager(AreaManager);     // 注入：赢取地区检查/结算依赖
        if (VerboseSetup) GD.Print("  ✅ AreaManager");

        // 6.7 PlayerActionService（玩家意图统一通道）
        // 依赖 TurnManager / CardManager / ZoneManager / AreaManager / PlayerManager，故须在 AreaManager 之后创建
        PlayerActionService = new PlayerActionService(TurnManager, CardManager, ZoneManager, AreaManager, PlayerManager);
        PlayerActionService.EffectEngineRef = effectEngine;   // 静态单例治理：显式注入 EffectEngine
        if (VerboseSetup) GD.Print("  ✅ PlayerActionService");

        // 6.5 CombatManager（三种对抗计算与势力标记产出，依赖 ZoneManager + AreaManager）
        CombatManager = new CombatManager();
        AddChild(CombatManager);
        CombatManager.Initialize(CardManager, PlayerManager, ZoneManager, AreaManager);
        CombatManager.SetTriggerEmitPoints(triggerEmitPoints);   //【MVC 收口】注入触发广播封装层
        CombatManager.ActionService = PlayerActionService;       // 联机对抗网络化路径需要（调查取牌/放回/抽牌）
        TurnManager.SetCombatManager(CombatManager);
        if (VerboseSetup) GD.Print("  ✅ CombatManager");

        // 7. 加载卡组（依赖 CardManager + DeckManager）+ 初始化双方秘社
        ReloadDecks();
        SetupCouncils();

        // 7.6 实例化地区卡牌堆（依赖 WorldDeckManager 对象 + CardManager）
        // 顺序要求：玩家卡(InitTestDecks) → 秘社(SetupCouncils) → 地区(此处)，故延迟至此。
        IWorldDeckProvider provider = new LocalWorldDeckProvider();
        WorldDeckManager.Initialize(provider, RandomSource, CardManager);
        if (VerboseSetup) GD.Print($"  ✅ WorldDeckManager (共 {WorldDeckManager.Count} 张地区卡)");

        // 8. PM 调试命令管理器：创建并纳入本节点生命周期（UI 回调由 Arena 注入）
        PMCommandManager = new PMCommandManager();
        AddChild(PMCommandManager);
        PMCommandManager.EffectEngineRef = effectEngine;   // 静态单例治理：显式注入 EffectEngine
        if (VerboseSetup) GD.Print("  ✅ PMCommandManager");

        // 8.5【MVC 收口】EffectEngine 的管理器引用注入收归本 Composition Root，不再由 View（Arena）装配。
        // 持续光环（D19-③）依赖 ZoneManager 读取区域卡集合；撤销/补授需跨控制者按 UID 找回实例（夺控场景）
        // 必须注入 CardManagerRef（此前漏注入会致 JC060 对他者授予/夺控撤销静默失效）。
        effectEngine.ZoneManagerRef = ZoneManager;
        effectEngine.CardManagerRef = CardManager;
        effectEngine.TurnManagerRef = TurnManager;   // 抓牌/费用等需回合管理器参与的全局操作

        // 8.6【MVC 收口】UI 通知总线（实例单例）：承载 Controller 的"状态已变请刷新"广播
        // （OnVisualDirty / CostModChanged），取代 EffectEngine 的 static 委托。须早于任何 OnVisualDirty 触发。
        new Script.Manager.UiNotificationBus();
        if (VerboseSetup) GD.Print("  ✅ UiNotificationBus");

        GD.Print("=== GameInitializer: 所有 Manager 初始化完成 ===");
    }

    // ============================================================
    // 卡组加载（供 InitializeAll 装配与 ResetMatch 重开复用）
    // ============================================================

    /// <summary>
    /// 加载双方卡组并缓存秘社来源（_loadedDeck）。
    /// 规则：P2 始终使用硬编码测试卡组；P1 仅在房间勾选「使用卡组JSON」时，
    /// 用 SelectedDeck（优先）或 SelectedDeckPath 指向的 JSON 初始化，否则也用硬编码卡组。
    /// InitTestDecks / InitFromDeckList 均为幂等重建（内部 Clear 后填充+洗牌），可安全重复调用。
    /// </summary>
    private void ReloadDecks()
    {
        DeckManager.InitTestDecks(playerId: 2); // P2 硬编码

        var gm = GameManager.Instance;
        if (gm != null && gm.UseSelectedDeck)
        {
            _loadedDeck = gm.SelectedDeck ?? DeckListLoader.Load(gm.SelectedDeckPath);
            if (_loadedDeck != null && _loadedDeck.Cards.Count > 0)
            {
                DeckManager.InitFromDeckList(_loadedDeck, playerId: 1); // P1 用 JSON
                GD.Print($"【GameInitializer】P1 使用卡组 JSON: {gm.SelectedDeckPath}");
            }
            else
            {
                GD.PrintErr("[GameInitializer] 卡组 JSON 无效，P1 回退到硬编码卡组");
                DeckManager.InitTestDecks(playerId: 1);
            }
        }
        else
        {
            DeckManager.InitTestDecks(playerId: 1); // P1 硬编码（未勾选）
        }
    }

    // ============================================================
    // 秘社初始化（阶段 B：实例化并开局注入 CouncilZone）
    // ============================================================

    /// <summary>默认秘社资源路径（仅当卡组 JSON 未携带任何秘社时的兜底）。</summary>
    private const string DefaultCouncilPath = "res://Cards/Data/CouncilCards/JC/MSJC001_weimushouwang.tres";

    /// <summary>
    /// 从已加载卡组（_loadedDeck.Councils）实例化双方秘社并移入各自 CouncilZone。
    /// 单机默认：每个秘社模板双方各创建一张。councils 为空时由 DeckListLoader 兜底补默认秘社，
    /// 若仍无则本方法直接返回（不阻塞开局）。
    /// </summary>
    private void SetupCouncils()
    {
        var councils = _loadedDeck?.Councils;
        if (councils == null || councils.Count == 0)
        {
            GD.PrintErr($"❌ 卡组未携带秘社，回退默认秘社: {DefaultCouncilPath}");
            councils = new List<DeckList.DeckEntry>
            {
                new DeckList.DeckEntry { TemplatePath = DefaultCouncilPath, Copies = 1 },
            };
        }

        foreach (var entry in councils)
        {
            var councilData = ResourceLoader.Load<CouncilCardData>(entry.TemplatePath);
            if (councilData == null)
            {
                GD.PrintErr($"❌ 加载秘社失败: {entry.TemplatePath}");
                continue;
            }
            for (int playerId = 1; playerId <= 2; playerId++)
            {
                var inst = CardManager.CreateCardInstance(councilData, playerId);
                ZoneManager.MoveToCouncil(inst, playerId);
                if (VerboseSetup) GD.Print($"  ✅ 玩家{playerId} 秘社就位: {councilData.CardName}");
            }
        }
    }

    // ============================================================
    // 开局编排（Composition Root 职责，禁止由 View 命令式驱动）
    // ============================================================

    /// <summary>每位玩家的起始手牌数。规则参数属逻辑层，不应由 UI 传入。</summary>
    public const int InitialHandSize = 6;

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset，供"再来一局"场景内重开调用）。
    /// 前置条件：InitializeAll() 已完成（Manager 实例常驻，本方法不重建对象，仅清状态复用）。
    /// 顺序要求：
    ///   1. 必须先 EffectEngine.Instance.ResetAll() 清静态效果注册表，早于 CardManager.Reset()，
    ///      避免新卡 UID（_nextUID 归 1）与旧注册表残留 key 冲突、旧 CardInstance 强引用泄漏。
    ///   2. 清各 Manager 对局状态 → 重建卡组/秘社/世界牌库 → 发牌起局。
    /// 本方法不改变当前"游戏结束切主界面"流程，仅提供能力；弹窗（再来一局）接入后由 UI 调用。
    /// </summary>
    public void ResetMatch()
    {
        GD.Print("=== GameInitializer: 对局重置 (ResetMatch) ===");
        // 1. 先清效果注册表（最关键）
        _effectEngine?.ResetAll();
        // 2. 清各 Manager 对局状态（不重建对象，复用常驻实例）
        CardManager.Reset();
        PlayerManager.Reset();
        ZoneManager.Reset();
        AreaManager.Reset();
        WorldDeckManager.Reset();
        StackManager.ClearAll();
        TurnManager.Reset();
        // 2.5【MVC 收口】清空 UI 通知总线订阅（旧 Arena 节点重建会重新订阅，避免残留叠加）
        Script.Manager.UiNotificationBus.Instance?.Clear();
        // 3. 重建玩家卡组 + 世界牌库 + 秘社（ZoneManager.Reset 已清 CouncilZone，需重放秘社）
        ReloadDecks();
        SetupCouncils();
        // 4. 发牌起局
        StartMatch();
        GD.Print("=== GameInitializer: 对局重置完成，已重新开局 ===");
    }

    /// <summary>
    /// 【MVC 修复 M-02】开局编排：发起始手牌并起局。
    /// 原先由 Arena._Ready 命令式调用 DrawCards/StartGame，属 View 承担 Orchestrator 职责；
    /// 现收归本 Composition Root，View 只在 UI 就绪后调用本方法一次，不再感知开局步骤与规则参数。
    /// 调用前提：InitializeAll() 已完成，且 UI 已订阅完各 Manager 事件（否则发牌事件会漏接）。
    /// </summary>
    public void StartMatch()
    {
        if (PlayerActionService == null)
        {
            GD.PushError("GameInitializer.StartMatch: 尚未 InitializeAll，无法开局。");
            return;
        }

        GD.Print("=== GameInitializer: 开局编排 ===");
        // 起手数：玩家携带秘社时覆盖默认 InitialHandSize（读 CouncilTemplate.StartingHandSize）
        int hand1 = GetCouncilHandSize(1);
        int hand2 = GetCouncilHandSize(2);
        PlayerActionService.DrawCards(1, hand1);
        PlayerActionService.DrawCards(2, hand2);
        PlayerActionService.StartGame();
    }

    /// <summary>
    /// 【MVC 收口】View 就绪后注册 UI 交互钩子（依赖倒置：View 实现 IGameUIHooks 接口，供 Controller 调用）。
    /// 由 Arena 在 _Ready 里 InitializeAll() 完成后调用；Controller 依赖接口，而非 View 往 Controller 塞委托。
    /// </summary>
    public void RegisterUIHooks(Script.Interfaces.IGameUIHooks hooks)
    {
        _effectEngine.UIHooks = hooks;
        // C/S 化 P0-B：若已装配输入网关，则同步注入到引擎/回合管理器（向后兼容：未装配则保持 null）。
        if (_inputGateway != null)
        {
            _effectEngine.InputGateway = _inputGateway;
            TurnManager.InputGateway = _inputGateway;
        }
        if (VerboseSetup) GD.Print("  ✅ IGameUIHooks 已注册（Arena 实现注入）");
    }

    // ==================== C/S 化 P0-B 输入网关装配 ====================
    // 服务端权威模式下，在 RegisterUIHooks 之前调用本方法注入网关：
    // - 单机模式：不调用（_inputGateway 保持 null，引擎走本地 UIHooks 弹窗，行为不变）
    // - Headless 测试/服务端：注入 AutoInputGateway / NetworkInputGateway
    // 注入后，引擎在"需玩家决策"处（PrepareUpkeep / 触发排序）经网关异步请求-响应，而非同步弹窗。
    private Script.Net.IInputGateway _inputGateway;

    /// <summary>
    /// 设置全局输入网关。应在 RegisterUIHooks 之前调用以生效于本局装配。
    /// 传入 null 等同于回退单机模式（本地 UIHooks 弹窗）。
    /// </summary>
    public void SetInputGateway(Script.Net.IInputGateway gateway)
    {
        _inputGateway = gateway;
        _effectEngine.InputGateway = gateway;
        TurnManager.InputGateway = gateway;
        GD.Print(gateway != null
            ? "  ✅ IInputGateway 已注入（C/S 异步输入模式）"
            : "  ⚪ IInputGateway 已清除（回退单机弹窗模式）");
    }

    // ==================== C/S 化 P1-A：整局快照聚合入口 ====================
    /// <summary>
    /// 构建当前整局状态快照（纯数据，可序列化下发/重连）。
    /// 实际聚合逻辑在 TurnManager.ToSnapshot()（已汇总 ZoneManager + StackManager + 回合字段）。
    /// </summary>
    public Script.Net.GameSnapshot BuildSnapshot()
    {
        // 用本实例持有的 TurnManager（而非静态 TurnManager.Instance，后者在测试/联机启动器场景未被赋值）
        var snap = TurnManager?.ToSnapshot();
        if (snap == null) return null;
        // 第三批：补充世界牌库（地区牌堆）、地区卡注册表与势力标记
        snap.WorldDeckCards = WorldDeckManager?.ToSnapshotUids() ?? snap.WorldDeckCards;
        snap.AreaCards = AreaManager?.ToSnapshot() ?? snap.AreaCards;
        snap.AreaMarks = AreaManager?.ToSnapshotMarks() ?? snap.AreaMarks;
        // 玩家级费用修正（JC012① 等）：下行给客户端做减费展示（结算仍以服务端为准）
        snap.CostMods = _effectEngine?.GetActiveCostMods() ?? snap.CostMods;
        return snap;
    }

    /// <summary>查询玩家起手数：若有秘社则用其 StartingHandSize，否则回退默认 InitialHandSize</summary>
    private int GetCouncilHandSize(int playerId)
    {
        var councils = ZoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
        if (councils != null)
        {
            foreach (var c in councils)
            {
                if (c?.CouncilTemplate != null)
                    return c.CouncilTemplate.StartingHandSize;
            }
        }
        return InitialHandSize;
    }
}
