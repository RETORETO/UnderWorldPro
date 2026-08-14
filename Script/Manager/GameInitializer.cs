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
public partial class GameInitializer : Node
{
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

    // ============================================================
    // 初始化入口（由 Arena._Ready 调用）
    // ============================================================
    public void InitializeAll()
    {
        GD.Print("=== GameInitializer: 开始初始化所有 Manager ===");

        // 0. 注册全部卡效果（必须在任何卡加载/实例化之前，供 CardEffectFactory.EnsureEffects 查表）。
        //    按扩展包迭代时，可改为只注册当前系列（如 Cards.Effects.PlayerCards.JC.JCEffects.RegisterAll()）。
        Cards.Effects.AllEffects.RegisterAll();
        GD.Print("  ✅ 已注册全部卡效果构建器");

        // 0.5 随机源（必须最先创建：DeckManager / WorldDeckManager 洗牌都依赖它）
        //    DebugSeed >= 0 时使用固定种子，牌序可复现；否则自动生成。
        RandomSource = new SeededRandomSource(DebugSeed >= 0 ? DebugSeed : (int?)null);
        GD.Print($"  🎲 RandomSource 种子 = {((SeededRandomSource)RandomSource).Seed}" +
                 (DebugSeed >= 0 ? "（固定种子，牌序可复现）" : "（自动生成，记录此值可复现本局）"));

        // 1. CardManager（卡牌实例的创建、移动、查询）
        CardManager = new CardManager();
        AddChild(CardManager);
        GD.Print("  ✅ CardManager");

        // 2. PlayerManager（玩家状态：能量、先手标志）
        PlayerManager = new PlayerManager();
        AddChild(PlayerManager);
        GD.Print("  ✅ PlayerManager");

        // 3. DeckManager（卡组管理：洗牌、抽牌）
        DeckManager = new DeckManager();
        DeckManager.Initialize(CardManager, RandomSource);
        AddChild(DeckManager);
        GD.Print("  ✅ DeckManager");

        // 3.5 ZoneManager（统一区域移动，单一真相源）
        ZoneManager = new ZoneManager();
        AddChild(ZoneManager);
        ZoneManager.Initialize(CardManager, DeckManager);
        CardManager.SetZoneManager(ZoneManager);
        PlayerManager.SetZoneManager(ZoneManager);   // 忠诚/费用来自资产区
        GD.Print("  ✅ ZoneManager");

        // 4. TurnManager（回合流程、阶段切换）
        TurnManager = new TurnManager();
        AddChild(TurnManager);
        TurnManager.Initialize(DeckManager, PlayerManager, CardManager);
        TurnManager.SetZoneManager(ZoneManager);   // 基础行动收口需驱动 ZoneManager 移动
        GD.Print("  ✅ TurnManager");


        // 5. WorldDeckManager（世界牌库：地区卡牌堆）
        // 注意：仅在此时创建对象并持有引用；地区卡实例化的 Initialize 延迟到秘社(SetupCouncils)之后调用，
        // 以满足"玩家卡 → 秘社 → 地区"的实例化顺序（地区卡 CreateCardInstance 延后）。
        WorldDeckManager = new WorldDeckManager();
        AddChild(WorldDeckManager);
        GD.Print($"  ✅ WorldDeckManager (对象已创建，地区卡实例化延后至秘社之后)");

        // 6. AreaManager（地区生命周期管理）—— 需在 StackManager 之后（赢取检查依赖堆叠）
        // 6.6 StackManager（堆叠区：LIFO，响应窗口驱动）先创建
        StackManager = new StackManager();
        AddChild(StackManager);
        StackManager.SetCardManager(CardManager);     // 注入：D16 统一收口，Push 时自动登记目标版本号
        TurnManager.SetStackManager(StackManager);   // 注入：打出卡/让过结算依赖堆叠
        PlayerManager.SetStackManager(StackManager);  // 注入：游戏结束即时结算时清空未结算堆叠
        GD.Print("  ✅ StackManager");

        AreaManager = new AreaManager();
        AddChild(AreaManager);
        AreaManager.Initialize(WorldDeckManager, ZoneManager, StackManager);
        TurnManager.SetAreaManager(AreaManager);     // 注入：赢取地区检查/结算依赖
        GD.Print("  ✅ AreaManager");

        // 6.7 PlayerActionService（玩家意图统一通道）
        // 依赖 TurnManager / CardManager / ZoneManager / AreaManager / PlayerManager，故须在 AreaManager 之后创建
        PlayerActionService = new PlayerActionService(TurnManager, CardManager, ZoneManager, AreaManager, PlayerManager);
        GD.Print("  ✅ PlayerActionService");

        // 6.5 CombatManager（三种对抗计算与势力标记产出，依赖 ZoneManager + AreaManager）
        CombatManager = new CombatManager();
        AddChild(CombatManager);
        CombatManager.Initialize(CardManager, PlayerManager, ZoneManager, AreaManager);
        TurnManager.SetCombatManager(CombatManager);
        GD.Print("  ✅ CombatManager");

        // 7. 加载卡组（依赖 CardManager + DeckManager）
        //    规则：P2 始终使用硬编码测试卡组；P1 仅在房间勾选「使用卡组JSON」时，
        //    用 SelectedDeck（优先）或 SelectedDeckPath 指向的 JSON 初始化，否则也用硬编码卡组。
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

        // 7.5 初始化双方秘社（依赖 CardManager + ZoneManager，从卡组 JSON 的 councils 读取）
        SetupCouncils();

        // 7.6 实例化地区卡牌堆（依赖 WorldDeckManager 对象 + CardManager）
        // 顺序要求：玩家卡(InitTestDecks) → 秘社(SetupCouncils) → 地区(此处)，故延迟至此。
        IWorldDeckProvider provider = new LocalWorldDeckProvider();
        WorldDeckManager.Initialize(provider, RandomSource, CardManager);
        GD.Print($"  ✅ WorldDeckManager (共 {WorldDeckManager.Count} 张地区卡)");

        // 8. PM 调试命令管理器：创建并纳入本节点生命周期（UI 回调由 Arena 注入）
        PMCommandManager = new PMCommandManager();
        AddChild(PMCommandManager);
        GD.Print("  ✅ PMCommandManager");

        GD.Print("=== GameInitializer: 所有 Manager 初始化完成 ===");
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
                GD.Print($"  ✅ 玩家{playerId} 秘社就位: {councilData.CardName}");
            }
        }
    }

    // ============================================================
    // 开局编排（Composition Root 职责，禁止由 View 命令式驱动）
    // ============================================================

    /// <summary>每位玩家的起始手牌数。规则参数属逻辑层，不应由 UI 传入。</summary>
    public const int InitialHandSize = 6;

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
