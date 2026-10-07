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

    // ============================================================
    // 【批次 2·多对局隔离】本局卡组显式指定（联机房间/MatchHost 用）
    // ============================================================
    // 背景：原 `ReloadDecks` 只从 UI 自动单例 `GameManager`（SelectedDeck/UseSelectedDeck）取 P1 卡组，
    // 那是单机时代的"房间内勾选卡组"约定；服务端（headless、可能同时跑多场对局）不能依赖进程级 UI 状态，
    // 否则多局之间会互相读到对方的卡组选择。
    // 约定：非 null 即视为"本局显式卡组"，优先于 UI 选择与硬编码卡组；由建房/开局流程（批次 5）按座位填入。
    /// <summary>本局 P1（座位1）卡组；null=未指定（退回 UI 选择/硬编码）。</summary>
    public DeckList P1DeckOverride { get; set; }
    /// <summary>本局 P2（座位2）卡组；null=未指定（退回硬编码测试卡组）。</summary>
    public DeckList P2DeckOverride { get; set; }

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

    /// <summary>本局加载的 P1 卡组（含卡组与秘社），供 SetupCouncils 按玩家读取秘社；null=该方无专属卡组。</summary>
    private DeckList _deckP1;
    /// <summary>本局加载的 P2 卡组（含卡组与秘社），供 SetupCouncils 按玩家读取秘社；null=该方无专属卡组。</summary>
    private DeckList _deckP2;

    // ============================================================
    // 【2026-10 修复·单人模式"看不到起始手牌 / PM draw 无 UI 变化"】卡组装载来源快照
    // ============================================================
    // 装载（`InitFromDeckList` / `InitTestDecks`）会 **new 出整批新 CardInstance**。而 View 是按
    // "装载完成那一刻"的那批实例建卡 UI 的：`Arena.InitializeDeckAndGraveyard()` 用它们建 `DeckZone` 的
    // CardUI，`Arena.BuildCardUIMap()` 把它们登记进 `_cardUIMap`（键＝实例 UID）。
    // 若在 View 绑定之后再装一次，逻辑层与 View 就分属两批实例：之后每一次"牌库→手牌"移动
    // （起始发牌 / PM `draw` / 回合抓牌）在 `Arena.SyncCardUIToZone` 里都按 UID 查不到卡 UI，
    // 于是**静默 return**（无任何日志）⇒ 手牌区永远空白、牌库 UI 永远不减。
    // 真实调用链正是如此：`Arena._Ready → InitializeAll()`（首次装载）→ 建 View
    // → `StartMatch() → DealOpeningHands()`（原实现无条件再装一次）。
    // 因此这里记录"上次装载的来源"，来源未变就**不重装**，保证实例集与 View 始终同源。

    /// <summary>是否已完成过一次卡组装载（首次必须装载）。</summary>
    private bool _decksLoaded;

    /// <summary>上次装载时 P1 的来源卡组（null＝硬编码测试卡组；非 null＝该 DeckList 引用）。</summary>
    private DeckList _loadedP1Deck;

    /// <summary>上次装载时 P1 走"单机 UI 所选 JSON"分支的路径（null＝未走该分支）。
    /// 来源标识用**路径**而非 `GameManager.SelectedDeck` 对象：玩家可在房间内改选卡组，
    /// 只有路径才是稳定的选择标识（`SelectedDeck` 仅作按需读盘缓存，本项目当前从不写入它）。</summary>
    private string _loadedP1UiPath;

    /// <summary>上次装载时 P2 的来源卡组（null＝硬编码测试卡组；非 null＝P2DeckOverride）。</summary>
    private DeckList _loadedP2Deck;

    /// <summary>P1 卡组来源分支（与 `ReloadDecks` 的优先级一一对应；仅用于"是否需要重装"判定与打日志）。</summary>
    private enum P1DeckSourceKind
    {
        Hardcoded,      // 硬编码测试卡组
        Override,       // P1DeckOverride
        UiSelectedJson, // 单机 UI 所选卡组 JSON（按路径）
        InheritP2,      // 未指定 P1，沿用 P2DeckOverride
    }

    /// <summary>
    /// 解析 P1 卡组来源（**不实例化**）。优先级与 `ReloadDecks` 完全一致：
    /// ① `P1DeckOverride`；② 单机 UI 所选 JSON（返回路径）；③ 沿用 `P2DeckOverride`；④ 硬编码。
    /// </summary>
    private P1DeckSourceKind ResolveP1DeckSource(out DeckList deck, out string uiPath)
    {
        deck = null;
        uiPath = null;

        if (P1DeckOverride != null)
        {
            deck = P1DeckOverride;
            return P1DeckSourceKind.Override;
        }

        var gm = GameManager.Instance;
        if (gm != null && gm.UseSelectedDeck)
        {
            uiPath = gm.SelectedDeckPath ?? "";
            return P1DeckSourceKind.UiSelectedJson;
        }

        if (P2DeckOverride != null)
        {
            deck = P2DeckOverride;
            return P1DeckSourceKind.InheritP2;
        }

        return P1DeckSourceKind.Hardcoded;
    }

    /// <summary>取指定玩家的本局卡组（即该玩家秘社的来源）；该方无专属卡组时返回 null，由调用方回退默认秘社。</summary>
    private DeckList GetDeckForPlayer(int playerId) => playerId == 1 ? _deckP1 : _deckP2;

    /// <summary>本局 EffectEngine 实例（静态单例治理：显式注入替代 EffectEngine.Instance）。</summary>
    private Script.Manager.EffectEngine _effectEngine;

    /// <summary>
    /// 【批次 2】本局 EffectEngine 只读访问器（**本局独有**，不指向进程级单例）。
    /// 供对局宿主/房间层与服务端侧逻辑取用（例如需要在本局上直接结算效果、或查询费用修正）。
    /// </summary>
    public Script.Manager.EffectEngine EffectEngine => _effectEngine;

    /// <summary>持续效果联动处理器（下沉到装配层，替代单机 Arena.Events 的 View 订阅，使联机服务端同样触发）。
    /// 具名字段便于每次 InitializeAll 先反订阅再订阅，避免重复累加。</summary>
    private System.Action<Script.Game.Entities.CardInstance, CardChangeKind> _cardStateContinuityHandler;

    /// <summary>【P3·B】势力标志变动 → 持续前提重判的订阅句柄（装配层持有，便于解绑/多局隔离）。</summary>
    private System.Action<string, int, int> _marksContinuityHandler;

    /// <summary>【K13】区域移动后 → 光环重算的订阅句柄（装配层持有，联机服务端与 headless 同样生效）。</summary>
    private System.Action<Script.Game.Entities.CardInstance, Script.Enums.CardZone, int, string> _postMoveAuraHandler;
        /// <summary>【2026-09·JC051】回合开始时的持续/光环重算（自愈补授）句柄：回合切换**不经移动** ⇒ 需独立触发点。</summary>
        
private System.Action<int> _turnStartAuraHandler;

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
        // 【JC124·构筑级（卡组级）全局触发】收集器注入：与常规场上扫描并轨（同一批收集 → 同一套触发管线）。
        // 语义＝"只要这张牌一开始加入玩家卡组就能触发、且离场后仍然生效"，登记见 `DeckLevelTriggerRule.RegisterFromDeck`。
        triggerRegistry.DeckLevelCollector = Script.Manager.DeckLevelTriggerRule.Collect;
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
            else if (kind == CardChangeKind.CovertChanged)
            {
                // 【用户口径·2026-09】"暗藏者的持续/静止效果也不生效"→ 翻面时**重算持久效果注册**：
                //  · 翻为暗藏者（潜伏）→ 注销（`UnregisterPersistent` 会清掉静止标记与地区级静止规则登记）；
                //  · 翻为正面（现身）→ 重新注册（`RegisterPersistent` 会重新授予/登记）。
                // 放在本分支**最前面**：先让规则集合与新朝向一致，再做后续的重判/触发广播
                // （否则 OnLatent/Uncover 类触发会读到旧规则集合）。
                if (card.CurrentZone == CardZone.AreaZone || card.CurrentZone == CardZone.AttachedZone)
                {
                    if (card.IsCovert) effectEngine.UnregisterPersistent(card);
                    else effectEngine.RegisterPersistent(card);
                }
                effectEngine.ReevaluateAuras(card.ControllerId);
                // 【P3·F】翻面也会改变"你操控的暗藏者数量"（JC037 每操控一个暗藏者 → 一个黑眼）→ 双方都重判
                effectEngine.ReevaluatePrereqs(card.ControllerId);
                effectEngine.ReevaluatePrereqs(card.ControllerId == 1 ? 2 : 1);
                // 【P3·"被潜伏/翻面时"】只在本卡**翻为暗藏者**时广播（反方向＝现身，走既有 UncoverTrigger）。
                if (card.IsCovert)
                    triggerEmitPoints.OnLatent(card, card.ControllerId, card.ZoneDetailId ?? "");
                // 【P3·K4】通用规则（用户 2026-09 口径，来源 JC036 初拥 FAQ 第二条）：
                // **宿主翻面为暗藏者 ⇒ 其上「结附于角色」的附属被消灭**（消灭＝死亡，照常走墓地与死亡触发）。
                // 放在本分支末尾：翻面自身的时机广播先完成，附属的死亡触发再进收集管线（同批由玩家排序）。
                if (card.IsCovert)
                    Script.Manager.AttachmentRule.EnforceOnCovertChange(card, CardManager);
            }
        };
        CardManager.OnCardStateChanged -= _cardStateContinuityHandler;
        CardManager.OnCardStateChanged += _cardStateContinuityHandler;
        if (VerboseSetup) GD.Print("  ✅ CardManager");

        // 2. PlayerManager（玩家状态：能量、先手标志）
        PlayerManager = new PlayerManager();
        AddChild(PlayerManager);
        // 注入到 TargetValidity（静态目标校验链），供条件式限制（如 AttachedToCharacterIfNoMind）
        // 在取目标阶段评估"资产区是否具【心】"（心灵领域忠诚）。
        // 【批次 2·多对局隔离】这是**进程级兜底**（一个进程跑多局时只会指向最后一局）；
        // 生产链路由 `CardManager.PlayerManagerRef`（下方注入）与 `TurnManager` 把自己的 PlayerManager
        // 显式传入 `TargetValidity.CheckRestrictions/Check`，本行仅为未注入路径与单测保留。
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
        // 【P3·灵体】伤害防止钩子：把"灵体"规则接到伤害唯一收口 `CardManager.DealDamageToInstance`。
        // 语义见 `DamageRules`（对手在本地区的魔法领域图标少于灵体控制者 → 防止该次伤害 → 不判死、不触发死亡）。
        // 【P3·K3】并联"伤害护盾"（JC078 光能盾：本回合内该角色受到的**所有**伤害被防止）——
        // 两条防止规则**并联**（任一成立即防止），都走同一个收口，故都不会产生死亡触发。
        CardManager.DamagePreventer = (target, amount) =>
            Script.Manager.DamageRules.IsSpiritPrevention(target, ZoneManager)
            || effectEngine?.IsDamagePrevented(target) == true;
        // 【P4·领袖】进场规则钩子：同控制者场上至多 1 个领袖；进场超限 → 立刻由该控制者选一个牺牲（不入堆叠）。
        // 挂在"进场触点"（MoveToArea 的非场地区→争夺区、且持续效果已注册之后），见 `LeaderRule`。
        ZoneManager.OnCardEnteredArea = card => Script.Manager.LeaderRule.EnforceOnEntry(card, CardManager, effectEngine);
        // 【P3·K25】「道途」唯一性（JC034「你只能操控至多一个道途」；用户 2026-09 口径＝与领袖同款、玩家选一个牺牲）：
        // 挂在**任意区域变更之后**（`OnPostMove`）——道途是"结附于本方秘社"的附属卡（落 `AttachedZone`），
        // 不经过 `OnCardEnteredArea`（那只覆盖争夺区进场），故用通用移动钩子覆盖进场/回场/移动等落点，见 `PathRule`。
        ZoneManager.OnPostMove += (movedCard, zone, playerId, detailId)
            => Script.Manager.PathRule.EnforceOnEntry(movedCard, CardManager, effectEngine);
        if (VerboseSetup) GD.Print("  ✅ ZoneManager（含灵体伤害防止钩子 / 领袖唯一性规则钩子 / 道途唯一性规则钩子）");

        // 4. TurnManager（回合流程、阶段切换）
        TurnManager = new TurnManager();
        AddChild(TurnManager);
        TurnManager.Initialize(DeckManager, PlayerManager, CardManager);
        TurnManager.SetZoneManager(ZoneManager);   // 基础行动收口需驱动 ZoneManager 移动
        TurnManager.EffectEngineRef = effectEngine;   // 静态单例治理：必须在 SetTriggerSystem 之前注入（其内部 EE.IsStackCircuitBroken）
        ZoneManager.TurnManagerRef = TurnManager;     // 静态单例治理：替代 ZoneManager 内 TurnManager.Instance
        TurnManager.SetTriggerSystem(triggerRegistry, triggerEmitPoints, eventBus);   //【MVC 收口】注入触发系统三实例
        TurnManager.SetRandomSource(RandomSource);   //【P2】随机弃牌等需要可复现随机源
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
        AreaManager.SetTriggerEmitPoints(triggerEmitPoints);   //【P1】OnAreaWon / OnMarkerPlaced 发点依赖
        // 【P3·B】势力标志增减 → 重判持续前提（JC084 街头混混"所在地区有本方/敌方势力标志 → 白星/白眼"）。
        // 与 CardManager 的 TappedChanged 同思路：把联动下沉到装配层，联机服务端（无 Arena）同样生效。
        _marksContinuityHandler = (areaUid, p1, p2) =>
        {
            effectEngine.ReevaluatePrereqs(1);
            effectEngine.ReevaluatePrereqs(2);
        };
        AreaManager.OnMarksChanged -= _marksContinuityHandler;
        AreaManager.OnMarksChanged += _marksContinuityHandler;
        // 【K13】区域移动后重算**光环**：授权会随"离场"被清空（如"暗藏→现身"时序），必须按当前场上重新判定。
        // 此前该重算只在 View（Arena.OnPostMove）里做 → **联机服务端与 headless 路径会漏**，
        // 导致 JC038 这类"光环授予袭击"在角色现身时刻丢失。下沉到装配层后两端一致（ReevaluateAuras 幂等）。
        _postMoveAuraHandler = (card, zone, playerId, detailId) =>
        {
            effectEngine.ReevaluateAuras(playerId);
            effectEngine.ReevaluateAuras(playerId == 1 ? 2 : 1);
            // 【P3】持续授予自愈：换区/落位会重建派生数值 → 数值型授予（如 JC089「宿主 -1 防御」）需按登记表补回。
            effectEngine.ReapplyMissingContinuousGrants(playerId);
            effectEngine.ReapplyMissingContinuousGrants(playerId == 1 ? 2 : 1);
            // 【P3·K13/K20】"运行期授予的激活能力"（JC019）与"持续抑制"（JC099/JC101/JC123）也须随卡的进出场重算。
            // 放在这里（＝**任何移动都会经过的统一后置刷新**）而不是只放在 `ReevaluatePrereqs`：
            // 后者在"该玩家没有带前提的持续效果"时会提前返回，会**漏掉**这次重算（本轮实测踩到）。
            effectEngine.ReapplyGrantedAbilities();
            effectEngine.ReapplySuppressions();
            // 【秘社批次】秘社的静态子类别授予（MSJC012"费用≤2 的本方人类获得奴仆类别"）也须随卡的进出场重算。
            // 与光环同款：秘社是 `CouncilCardData`（`PlayerTemplate == null`），光环机制结构上覆盖不到 ⇒ 独立规则。
            effectEngine.ReevaluateCouncilStaticGrants();
        };
        ZoneManager.OnPostMove -= _postMoveAuraHandler;
        ZoneManager.OnPostMove += _postMoveAuraHandler;

        // 【2026-09 用户实测·JC051】光环/持续授予在**回合切换**时也可能被还原掉，而"回合切换"**没有卡牌移动**
        // ⇒ 走不到上面的 `OnPostMove` 重算，自愈补授永不触发（实测："第一回合能正常显示，再过两回合
        // 先手交移回来了没有正常显示"）。这里在**回合开始**对双方各做一次同口径的**幂等**重算/自愈补授。
        _turnStartAuraHandler = _ =>
        {
            // 【2026-09 回归加固】回合切换不经移动 ⇒ 在这里补一次持续/光环重算。
            // ⚠ 只做与"授予补回"直接相关的两项（光环重算 + 数值授予补回）：原先连 `ReapplyGrantedAbilities`/
            // `ReapplySuppressions`/`ReevaluateCouncilStaticGrants` 一起调，风险面过大（用户实测：加了本钩子后
            // JC053 双击无反馈、JC051 连第一回合都不显示 ⇒ 疑似此处异常中断了回合流程）。
            // 且**一律 try/catch**：重算失败绝不允许打断"回合开始"这一关键流程（只记日志）。
            try
            {
                effectEngine.ReevaluateAuras(1);
                effectEngine.ReevaluateAuras(2);
                effectEngine.ReapplyMissingContinuousGrants(1);
                effectEngine.ReapplyMissingContinuousGrants(2);
                // 【2026-09·JC051】先手相关值必须在**回合开始**重算（否则先手标记停留在上一回合 ⇒ 后手回合把临时先手能力也算进去）
                TurnManager.RefreshFirstStrikeStats();
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[光环/持续] ⚠ 回合开始重算异常（已吞掉，不影响回合流程）：{ex.Message}");
            }
        };
        TurnManager.OnTurnStarted -= _turnStartAuraHandler;
        TurnManager.OnTurnStarted += _turnStartAuraHandler;
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
        PMCommandManager.TurnManagerRef = TurnManager;     // 【2026-09】jumpto 跳转步骤 / 定向取牌（draw <玩家> <编号>）用
        // 【2026-10】对抗命令（investigation/battle）结算入口：联机服务端（无 Arena 本机窗口）经它把
        // 对抗窗口通过决策协议下发给玩家；单机由 Arena 的窗口回调转调同一方法（正规结算链）。
        PMCommandManager.CombatManagerRef = CombatManager;
        // 【2026-10】地区列表兜底：View 侧 `_getAreaUids` 回调未注入时（headless 测试 / 纯服务端）
        // 用 AreaManager 取"按槽位顺序的地区 UID"，避免 investigation/battle/power 的地区校验恒失败。
        PMCommandManager.AreaManagerRef = AreaManager;
        // 【2026-09】模型侧依赖就地接线：使 PM 命令在无 View 场景（headless 测试 / 纯服务端）也可用；
        // View 的 UI 回调仍由 Arena/MatchHost 调 `Initialize` 注入（并覆盖此处）。
        PMCommandManager.InjectModelRefs(CardManager, DeckManager, ZoneManager, AreaManager);
        if (VerboseSetup) GD.Print("  ✅ PMCommandManager");

        // 8.5【MVC 收口】EffectEngine 的管理器引用注入收归本 Composition Root，不再由 View（Arena）装配。
        // 持续光环（D19-③）依赖 ZoneManager 读取区域卡集合；撤销/补授需跨控制者按 UID 找回实例（夺控场景）
        // 必须注入 CardManagerRef（此前漏注入会致 JC060 对他者授予/夺控撤销静默失效）。
        effectEngine.ZoneManagerRef = ZoneManager;
        effectEngine.CardManagerRef = CardManager;
        effectEngine.TurnManagerRef = TurnManager;   // 抓牌/费用等需回合管理器参与的全局操作

        // 8.5.1【批次 2·多对局隔离】CardManager 也持有本局的 PlayerManager：
        // 目标精筛链（CardManager.MatchesRule/MatchesSpec → TargetValidity.CheckRestrictions）需要它来评估
        // 【心】等条件式限制；不注入就会退回进程级 `TargetValidity.PlayerManagerRef`（多局并存时会串场）。
        CardManager.PlayerManagerRef = PlayerManager;

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
    /// 加载双方卡组并**按玩家**缓存秘社来源（`_deckP1`/`_deckP2`）。
    /// 优先级（自上而下）：
    ///   1. **本局显式指定的卡组**（`P1DeckOverride`/`P2DeckOverride`）——联机房间/`MatchHost` 用；
    ///   2. 单机 UI 选择（`GameManager.UseSelectedDeck` + `SelectedDeck/SelectedDeckPath`，仅 P1）；
    ///   3. 硬编码测试卡组（P1/P2）→ 记为"该方无专属卡组"，秘社回退默认秘社。
    /// InitTestDecks / InitFromDeckList 均为幂等重建（内部 Clear 后填充+洗牌），可安全重复调用。
    ///
    /// 【幂等·来源未变则不重装】见文件内 `_decksLoaded` 段注释：重装会换掉整批 CardInstance，
    /// 使已按旧实例建好的 View（`DeckZone` 的 CardUI + `Arena._cardUIMap`）**永久失联**
    /// ⇒ 起始手牌与 PM draw 全都没有 UI。故仅当来源真的变化时才重装：
    ///  · 单机路径：`InitializeAll` 已按房间所选卡组装载 → `StartMatch` 时来源未变 ⇒ 跳过（修复本次故障）；
    ///  · 测试/联机：在 `InitializeAll` **之后**才赋 `P1DeckOverride`/`P2DeckOverride`（或房间内改选了卡组路径）
    ///    ⇒ 来源变化 ⇒ 照常重装（既有契约不变）；
    ///  · 整局重开：`ResetMatch` 传 `force: true`（`CardManager.Reset` 已把 `_nextUID` 归 1，必须重建实例）。
    /// </summary>
    private void ReloadDecks(bool force = false)
    {
        var p1Kind = ResolveP1DeckSource(out var p1Deck, out var p1UiPath);
        var p2Deck = P2DeckOverride;

        if (!force && _decksLoaded
            && ReferenceEquals(_loadedP1Deck, p1Deck) && _loadedP1UiPath == p1UiPath
            && ReferenceEquals(_loadedP2Deck, p2Deck))
        {
            GD.Print("[GameInitializer] 卡组来源未变 ⇒ 跳过重复装载（保持逻辑实例集与 View 同源）");
            return;
        }

        // ① P2 卡组
        if (p2Deck != null)
        {
            DeckManager.InitFromDeckList(p2Deck, playerId: 2);
            _deckP2 = p2Deck;   // 秘社来源：P2 自己的卡组
            GD.Print("【GameInitializer】P2 使用本局显式指定卡组");
        }
        else
        {
            DeckManager.InitTestDecks(playerId: 2); // P2 硬编码
            _deckP2 = null;                         // 无专属卡组 → 秘社回退默认秘社
        }

        // ② P1 卡组（每个分支都必须显式赋值 _deckP1，避免沿用上一局/其它分支的残留）
        switch (p1Kind)
        {
            case P1DeckSourceKind.Override:
                DeckManager.InitFromDeckList(p1Deck, playerId: 1);
                _deckP1 = p1Deck;   // 秘社来源：P1 自己的卡组
                GD.Print("【GameInitializer】P1 使用本局显式指定卡组");
                break;

            case P1DeckSourceKind.UiSelectedJson:
            {
                var gm = GameManager.Instance;
                var deck = gm?.SelectedDeck ?? DeckListLoader.Load(p1UiPath);
                if (deck != null && deck.Cards.Count > 0)
                {
                    DeckManager.InitFromDeckList(deck, playerId: 1); // P1 用 JSON
                    _deckP1 = deck;
                    GD.Print($"【GameInitializer】P1 使用卡组 JSON: {p1UiPath}");
                }
                else
                {
                    GD.PrintErr("[GameInitializer] 卡组 JSON 无效，P1 回退到硬编码卡组");
                    DeckManager.InitTestDecks(playerId: 1);
                    _deckP1 = null;   // 回退硬编码 = 无专属卡组，不能沿用已加载但无效的 deck
                }
                break;
            }

            case P1DeckSourceKind.InheritP2:
                // 联机场景：P2 已显式指定 → P1 未指定时沿用 P2 卡组（避免服务端去读 UI 状态）。
                // 此时 P1 的牌确实来自该卡组，秘社也取该卡组自带值，保持"卡与秘社同源"。
                DeckManager.InitFromDeckList(p1Deck, playerId: 1);
                _deckP1 = p1Deck;
                GD.Print("【GameInitializer】P1 沿用 P2 的本局显式卡组（未单独指定）");
                break;

            default:
                DeckManager.InitTestDecks(playerId: 1); // P1 硬编码（未勾选）
                _deckP1 = null;                         // 无专属卡组 → 秘社回退默认秘社
                break;
        }

        // 记录本次生效来源，供下一次判定"是否需要重装"（见上方幂等说明）。
        _decksLoaded = true;
        _loadedP1Deck = p1Deck;
        _loadedP1UiPath = p1UiPath;
        _loadedP2Deck = p2Deck;
    }

    // ============================================================
    // 秘社初始化（阶段 B：实例化并开局注入 CouncilZone）
    // ============================================================

    /// <summary>默认秘社资源路径（仅当卡组 JSON 未携带任何秘社时的兜底）。</summary>
    private const string DefaultCouncilPath = "res://Cards/Data/CouncilCards/JC/MSJC009_mishe.tres";

    /// <summary>
    /// 按**各自**卡组实例化双方秘社并移入各自 CouncilZone。
    /// 秘社来源＝该玩家自己的卡组（`GetDeckForPlayer(pid).Councils`）；该方无专属卡组或卡组未携带秘社时，
    /// 回退默认秘社 `DefaultCouncilPath`（**不镜像对手的秘社**）。
    /// </summary>
    private void SetupCouncils()
    {
        // 【幂等】本方法会被调用多次（InitializeAll → DealOpeningHands 重放，ResetMatch 亦会重放），
        // 而 MoveToCouncil 只"加入"不移除 ⇒ 需先清空双方秘社区再重建，否则每位玩家 CouncilZone 会累积多张秘社：
        //   · `GetCouncilHandSize` 读到先建的旧卡 → 起手数回退默认 6（本局卡组秘社的 StartingHandSize 失效）；
        //   · "结附于本方秘社"类效果会出现重复候选（JC051 的宿主选择会多出一个候选）。
        // 清空时旧实例按移出游戏处理并广播 → View 摘除旧秘社 UI。
        for (int pid = 1; pid <= 2; pid++) ZoneManager.ClearCouncilZone(pid);

        for (int playerId = 1; playerId <= 2; playerId++)
        {
            var councils = ResolveCouncilEntries(playerId);
            foreach (var entry in councils)
            {
                var councilData = ResourceLoader.Load<CouncilCardData>(entry.TemplatePath);
                if (councilData == null)
                {
                    GD.PrintErr($"❌ 加载秘社失败: {entry.TemplatePath}");
                    continue;
                }
                int copies = entry.Copies > 0 ? entry.Copies : 1;
                for (int i = 0; i < copies; i++)
                {
                    var inst = CardManager.CreateCardInstance(councilData, playerId);
                    ZoneManager.MoveToCouncil(inst, playerId);
                }
                GD.Print($"  ✅ 玩家{playerId} 秘社就位: {councilData.CardName}");
            }
        }
    }

    /// <summary>
    /// 取指定玩家的秘社条目：来自**该玩家自己的卡组**；无卡组或卡组未携带秘社时回退默认秘社 1 张。
    /// 【修复·秘社归属】此前双方共用 P1 卡组的秘社（旧的 `_loadedDeck`），导致 P2 拿到 P1 卡组的秘社，
    /// 且 `GetCouncilHandSize(2)` 读到的是 P1 秘社的起始手牌数。
    /// </summary>
    private List<DeckList.DeckEntry> ResolveCouncilEntries(int playerId)
    {
        var deck = GetDeckForPlayer(playerId);
        var councils = deck?.Councils;
        if (councils != null && councils.Count > 0) return councils;

        if (deck == null)
            GD.Print($"ℹ️ 玩家{playerId} 无专属卡组（硬编码卡组/未提交卡组），使用默认秘社: {DefaultCouncilPath}");
        else
            GD.PrintErr($"❌ 玩家{playerId} 卡组未携带秘社，回退默认秘社: {DefaultCouncilPath}");

        return new List<DeckList.DeckEntry>
        {
            new DeckList.DeckEntry { TemplatePath = DefaultCouncilPath, Copies = 1 },
        };
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
        // 【强制重装】CardManager.Reset 已把 _nextUID 归 1 ⇒ 必须重建整批实例（否则新 UID 与旧注册表冲突），
        // 来源即使未变也不能跳过，故传 force: true。
        ReloadDecks(force: true);
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
        // 【A57】开局清空"每局游戏"能力使用次数（`EffectData.MaxUsesPerGame`，如秘社 MSJC001 行动4）：
        // 实例虽按局重建，这里显式兜底"重开一局复用实例"的情形——**回合重置不清它**。
        CardManager?.ResetAllAbilityUsesThisGame();
        // ① 先按各自秘社发起始手牌（DealOpeningHands 内部按本局卡组重放秘社，并据此取各自起手数）。
        DealOpeningHands();
        // ② 后结算秘社开局效果（"持续：在每局游戏开始时…"，如 MSJC015 在本方秘社上放置【隐秘】标志）。
        // 【修复·时序】旧顺序是先结算再发牌，而 DealOpeningHands 会重建秘社并清掉带标志的旧实例 ⇒ 效果产物丢失。
        // 现改为发牌之后结算：起手已抽完（与卡文"第一个回合的准备步骤开始时"口径一致），
        // 且效果打在**最终保留**的秘社实例上，仍早于第一回合的准备步骤。
        ApplyCouncilOpeningEffects();

        // 单机：直接开始第一回合（无联机调度步骤）。
        PlayerActionService.StartGame();
    }

    /// <summary>
    /// 【秘社批次】执行双方秘社声明的**开局效果**（`CouncilCardData.OpeningEffectList`）。
    /// 时机：秘社实例已就位（`SetupCouncils` 之后）、起手牌已发完（`DealOpeningHands` 之后）、
    /// 第一回合开始之前（早于第一回合的准备步骤）。
    /// 覆盖 MSJC015「在每局游戏开始时，在本方秘社上放置一个【隐秘】标志」与
    /// MSJC016「在第一个回合的准备步骤开始时，将你牌库顶的三张牌置于你的墓地」
    /// （后者在 .cs 里同时也在准备步骤登记触发，本处只负责"第一回合"这一次的即时生效，
    /// 避免开局流程与阶段机在"第一回合准备步骤"上的时序歧义）。
    /// </summary>
    public void ApplyCouncilOpeningEffects()
    {
        if (_effectEngine == null || ZoneManager == null || CardManager == null) return;
        for (int pid = 1; pid <= 2; pid++)
        {
            var councils = ZoneManager.GetZoneCards(CardZone.CouncilZone, pid);
            if (councils == null) continue;
            foreach (var council in councils)
            {
                var list = council?.CouncilTemplate?.OpeningEffectList;
                if (list == null || list.Count == 0) continue;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    GD.Print($"[GameInitializer] 秘社开局效果：{council.CouncilTemplate.CardName} → {e.EffectId}");
                    _effectEngine.ResolveEffect(e, council, null);
                }
            }
        }
    }

    /// <summary>
    /// 按**当前**卡组就绪秘社并发放起始手牌（不发第一回合）。
    ///
    /// 【为什么在发牌前重放秘社】`InitializeAll()`/`ResetMatch()` 里已跑过 `SetupCouncils()`，但联机路径是
    /// 「`InitializeAll()` → 设 `P1DeckOverride`（房间卡组） → 发牌」——秘社实例仍是**旧卡组**（默认秘社）的，
    /// 导致 `GetCouncilHandSize()` 永远读到默认 6 张，房间卡组自带的秘社起始手牌数完全不生效。
    /// 故此处按当前卡组**重放一次**秘社，保证"按秘社记录的起始手牌数发牌"。
    /// </summary>
    public void DealOpeningHands()
    {
        if (PlayerActionService == null)
        {
            GD.PushError("GameInitializer.DealOpeningHands: 尚未 InitializeAll，无法发牌。");
            return;
        }

        // 起手数来源＝秘社；重放卡组+秘社确保读到的是**本局最终卡组**携带的秘社（而非 InitializeAll 时的旧卡组）。
        // ReloadDecks 是幂等的：来源未变（单机：InitializeAll 已按房间所选卡组装载）⇒ 直接跳过，
        // **绝不换掉 View 已绑定的那批实例**；来源变化（测试/联机在 InitializeAll 之后才赋 override，
        // 或房间内改选了卡组路径）⇒ 照常重装。详见 ReloadDecks 的幂等说明。
        ReloadDecks();
        SetupCouncils();

        GD.Print("=== GameInitializer: 发放起始手牌（发完后再开始第一回合）===");
        int hand1 = GetCouncilHandSize(1);
        int hand2 = GetCouncilHandSize(2);
        PlayerActionService.DrawCards(1, hand1);
        PlayerActionService.DrawCards(2, hand2);
        GD.Print($"[GameInitializer] 起始手牌：座位1 = {hand1} 张 / 座位2 = {hand2} 张");
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
        // 【调查对抗·客户端 UI 对齐】把待分配的调查卡一并下发（仅决策者本人可见，由 SnapshotFilter 过滤）
        FillInvestigationCards(snap);
        return snap;
    }

    /// <summary>把 CombatManager 的待决调查卡投影为纯数据引用（供客户端复刻单机 InvestigationWindow）。</summary>
    private void FillInvestigationCards(Script.Net.GameSnapshot snap)
    {
        var cm = CombatManager;
        var pending = cm?.PendingInvestigationCards;
        snap.InvestigationOwnerPlayerId = (pending != null && pending.Count > 0) ? cm.PendingInvestigationOwner : 0;
        snap.InvestigationCards = new System.Collections.Generic.List<Script.Net.ZoneCardRef>();
        if (pending == null) return;
        foreach (var c in pending)
        {
            if (c == null) continue;
            // 走统一投影入口（ZoneManager.ProjectCard），避免"待决调查卡"另有一份字段不全的手写映射——
            // 单进程回环下 ApplySnapshot 会把引用套回同一批共享实例，字段不全的映射会反过来抹掉服务端状态。
            snap.InvestigationCards.Add(global::ZoneManager.ProjectCard(c, c.CurrentZone, c.OwnerId));
        }
    }

    /// <summary>
    /// 【Phase B·信息隔离】构建"发给某个玩家"的快照视图：隐藏信息（对手手牌/牌库、地区牌堆）替换为占位引用，
    /// 只保留数量与顺序；公开区（争夺区/附属/封印/墓地/资产/分数/秘社/堆叠/地区/势力标记/费用修正）原样下发。
    /// viewerPlayerId &lt;= 0 表示不过滤（返回完整快照，供服务端自省/测试）。
    ///
    /// 【P2·合法性投影】同时附带"服务端为该玩家算好的可发动/合法目标集合"（`LegalityProjection`），
    /// 使客户端只做渲染与成员判断，不再自行拼输入做提前判断（消灭两套判定的根源）。
    /// </summary>
    public Script.Net.GameSnapshot BuildSnapshotFor(int viewerPlayerId)
    {
        var full = BuildSnapshot();
        if (full == null) return null;
        var view = Script.Manager.LegalityProjection.Build(CardManager, PlayerManager, TurnManager, ZoneManager, viewerPlayerId);
        return Script.Net.SnapshotFilter.ForPlayer(full, viewerPlayerId, view);
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
