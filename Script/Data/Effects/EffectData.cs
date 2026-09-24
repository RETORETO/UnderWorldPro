using Godot;
using Godot.Collections;
using Script.Enums;

namespace Script.Data.Effects
{
    /// <summary>
    /// 通用效果数据结构（EffectData）。
    /// 设计目标：统一承载"持续效果 / 角色·附属触发与激活效果 / 事务卡打出结算"三类数据，
    /// 作为效果引擎的纯数据层（schema）。各动作原语的执行器（EffectActionResolver）后续逐个补全，
    /// 未实现的动作在结构中仅是枚举/字符串值，不影响本数据结构定稿。
    ///
    /// 数据来源：基于 Card.xml 全量聚类（见《效果类型清单.md》）与 JC001~JC013 人工转写样例。
    /// </summary>

    // ==================== 枚举定义 ====================

    /// <summary>触发对来源卡暗藏（背面朝上）状态的要求（核心需求：暗藏潜伏机制）。
    ///  FaceUp    （默认）：仅当来源卡正面朝上（非暗藏）时可触发——常规触发，暗藏时跳过；
    ///  CovertOnly：仅当来源卡背面朝上（暗藏/IsCovert=true）时可触发——"暗藏专属触发"，正面朝上反而不触发；
    ///  Either    ：无论正面/暗藏均可触发（极少数全状态生效的效果）。
    /// </summary>
    public enum CovertTriggerMode { FaceUp, CovertOnly, Either }

    /// <summary>
    /// 效果时机/触发源。一个枚举承载四类语义（历史演进的自然结果），按性质分组如下：
    ///
    /// 【B组·持续/静止】不入堆叠、不授出/结算，注册表常驻：
    ///   Continuous（持续效果）、Static（静止/禁止规则约束）。
    ///
    /// 【C组·激活能力】玩家主动发动，需费用/优先权/门槛，发动后进堆叠结算：
    ///   CharacterActivated（角色激活）、AttachmentActivated（附属激活）、CouncilActivated（秘社激活）。
    ///
    /// 【C组·触发时机】事件被动触发，经 GameEventBus 广播 → TriggerRegistry 收集 → 入堆叠：
    ///   EnterTrigger（进场）、UncoverTrigger（现身）、ManeuverTrigger（机动）、
    ///   以及下方所有 OnXxx 总线扩展时机（OnCardPlayed/OnCardDied/OnAreaWon/.../OnCombatWon）。
    ///
    /// 【F组·打出结算】事务卡打出即结算，完即入墓地：
    ///   OnSorceryCast（事务打出）。当前走 ResolveSorcery 专属路径，未接总线（见该条 TODO）。
    ///
    /// 注：本枚举值顺序一经发布须保持稳定（新增值追加末尾），详见下方"总线扩展时机"注释块。
    /// </summary>
    public enum EffectTiming
    {
        Continuous,            // 持续效果（B组）：入注册表，按位置实时增删，不入堆叠
        Static,                // 静止/禁止效果（B组衍生，D19-⑤）：常驻规则约束（如不能被装备结附），置位 StaticFlags，不入堆叠、不授出能力
        EnterTrigger,          // 进场触发（C组）
        UncoverTrigger,        // 现身触发（C组）
        CharacterActivated,    // 角色激活能力（C组）
        AttachmentActivated,   // 附属激活能力（C组）
        ManeuverTrigger,       // 机动触发（C组，玩家可选，非强制）
        OnSorceryCast,         // 事务卡打出结算（F组）：打出即结算，完即入墓地。
                               // TODO（命名/架构）：本时机当前走 ResolveSorcery 专属路径，未接入 GameEventBus 总线触发——
                               // 若需"反应类时机（如 OnCardPlayed）能响应事务打出"，须后续接入总线（见 EffectEngine.ResolveSorcery）。
        OnCardPlayed,          // 反应触发：当任意卡被打出时，旁观者卡触发（如 XG01 打出事务牌时抓牌）
        OnTurnEnded,           // 回合结束触发：结束步骤（EndCleanup）广播，此时堆叠仍可结算

        // ============================================================
        // 下方为"监听总线扩展时机"——全部追加自 2026-08-05 总线补全批次。
        // 约束：新增时机一律追加在末尾，禁止在中间插入。
        // 原因：即使 EffectData 不序列化进 .tres，EffectTiming 仍可能以整数形式出现在其他资源、
        //      保存数据、网络同步或调试日志中；保持顺序稳定可避免潜在不一致。
        // 每个时机都对应 GameEvent 中同名/同义事件，由 TriggerRegistry.EventToTiming 登记映射。
        // 触点 Emit 现状见各条注释："【已接】"= 业务点已调用 GameEventBus.Emit；
        //                         "【待接】"= 枚举与映射已就绪，待对应业务点补 Emit（不影响编译与现有流程）。
        // ============================================================

        /// <summary>反应触发：当任意卡牌死亡（进入墓地）时，旁观者卡触发。对应 GameEvent.OnCardDied。
        /// 覆盖卡文："死亡触发"（全卡 ~20 对简繁样例）。典型如死亡时打 X 伤害、死亡时抽牌、死亡时放置指示物。
        /// 【待接】触点：CardManager.HandleInstanceDeath 处死亡判定后调用 GameEventBus.Emit(OnCardDied, ctx)。
        /// 注意：与 OnCardLeft 区分——OnCardDied 仅指进墓地的死亡，OnCardLeft 含回手/封印移除等离场。</summary>
        OnCardDied,

        /// <summary>触发：当某地区（对抗区域）被一方赢取时广播。对应 GameEvent.OnAreaWon。
        /// 覆盖卡文："赢取触发"（全卡 ~29 对样例）。典型如赢取地区时抓牌、赢取时放置指示物、赢取时造成创伤。
        /// 【待接】触点：AreaManager.ResolveAreaWon 结算完成后调用 GameEventBus.Emit(OnAreaWon, ctx)。
        /// 可顺带清掉核心需求 2.2 的 TODO 欠账（该处已预留扩展位）。</summary>
        OnAreaWon,

        /// <summary>触发：当回合开始时广播（准备步骤/开始步骤）。对应 GameEvent.OnTurnStarted。
        /// 覆盖卡文："准备步骤强制触发"（全卡 ~39 对样例）、"开始步骤"。典型如回合开始抓牌、回合开始放置指示物。
        /// 【待接】触点：TurnManager.NextTurn 推进到新回合的"开始"节点后调用 GameEventBus.Emit(OnTurnStarted, ctx)。
        /// 与 OnTurnEnded 成对，构成回合生命周期两端的监听点。</summary>
        OnTurnStarted,

        /// <summary>反应触发：当本角色/玩家抓牌（抽牌）时，旁观者卡触发。对应 GameEvent.OnDraw。
        /// 覆盖卡文："当你抓牌时""抓牌触发"（全卡"抓牌" ~18 处）。典型如抓牌时额外放置指示物、抓牌上限相关。
        /// 【待接】触点：EffectEngine.Instance.Draw（抽牌动作原语）执行后调用 GameEventBus.Emit(OnDraw, ctx)。
        /// 区别于 OnCardPlayed（打牌）：抓牌是被动作，打牌是主动打出。</summary>
        OnDraw,

        /// <summary>触发：当卡牌被弃置（从手牌进入墓地/弃牌堆）时广播。对应 GameEvent.OnDiscard。
        /// 覆盖卡文："手牌触发""当你弃牌时"（全卡"手牌触发" ~5 对样例）。典型如弃牌时触发效果、弃牌视为某种代价结算。
        /// 【待接】触点：手牌弃牌结算处（HandManager/EffectEngine.Instance.Discard）调用 GameEventBus.Emit(OnDiscard, ctx)。
        /// 区别于 OnCardDied（场上死亡）：此处特指手牌区弃置。</summary>
        OnDiscard,

        /// <summary>触发：当指示物（Token/Marker）被放置到某卡/区域时广播。对应 GameEvent.OnMarkerPlaced。
        /// 覆盖卡文："当指示物被放置""放置触发"（全卡"指示物" ~61 处、"放置" ~286 处）。典型如每放置 1 指示物触发结算。
        /// 【待接】触点：指示物系统放置入口（E4-#1 放置原语）调用 GameEventBus.Emit(OnMarkerPlaced, ctx)。
        /// 需注意：高频放置场景必须配合 D-1 熔断/自指循环复核，避免"放置触发→再放置"死循环。</summary>
        OnMarkerPlaced,

        /// <summary>触发：当一张卡被封印（Seal）时广播。对应 GameEvent.OnSealed。
        /// 覆盖卡文："当卡牌被封印""封印触发"（全卡"封印" ~149 处，极高）。典型如被封印时触发、封印作为条件。
        /// 【已接线·P2】触点：封印唯一出口 `TurnManager.SealCard`（经 `ActionVerb.Seal` 与检索去向`封印`调用）。
        /// 上下文：ContextCard=被封印的卡，TriggeringPlayerId=封印者，ExtraString=宿主卡 UID（空=封印于地区）。
        /// 与 OnCardLeft 部分重叠（封印常导致卡离场回手），但语义独立：封印是状态，离场是位置变化。</summary>
        OnSealed,

        /// <summary>触发：当一张卡在场上移动（区域/位置变更）时广播。对应 GameEvent.OnMove。
        /// 覆盖卡文："当一个敌方角色或暗藏者移动时""移动触发"（全卡"移动" ~45 处）。典型如移动时触发结算。
        /// 【待接】触点：移动系统（机动/位置变更原语）完成后调用 GameEventBus.Emit(OnMove, ctx)。</summary>
        OnMove,

        /// <summary>反应触发：当一张卡/效果被"终止"（Counter/反制）时广播。对应 GameEvent.OnCounter。
        /// 覆盖卡文："终止之""当此卡被终止时"（快速行动终止堆叠目标高频）。典型如被终止时触发反击、终止响应。
        /// 【待接】触点：终止/反制动作原语（E6-TODO：Counter 等）结算处调用 GameEventBus.Emit(OnCounter, ctx)。
        /// 关联 T9（反应箭头）与 D-2（取消/替代能力）：终止是反应链核心节点。</summary>
        OnCounter,

        /// <summary>触发：当一张卡离开战场/区域（回手、封印移除、被移出游戏等非死亡离场）时广播。对应 GameEvent.OnCardLeft。
        /// 覆盖卡文："当离开战场时""离场触发"（全卡"离场" ~17 处，独立"离场触发"仅 1 处）。典型如离场时触发结算。
        /// 【待接】触点：离场判定处（区别于 OnCardDied 的死亡进墓地）调用 GameEventBus.Emit(OnCardLeft, ctx)。
        /// 与 OnCardDied 区分：死亡=进墓地；离场=任意原因离开战场（含死亡，但此处仅指"非死亡离场"以保持互斥清晰）。</summary>
        OnCardLeft,

        /// <summary>触发：战斗对抗（Strength 类型）获胜时广播。对应 GameEvent.OnCombatWon。
        /// 触发语义：CombatManager.ResolveWinner 结算战斗对抗分出胜者后，经 TriggerEmitPoints.OnCombatWon 广播；
        /// 由威名类效果（JC018 等）响应，条件 WinnerIsSelf 判定"本卡控制者是否为对抗获胜方"。
        /// 放置逻辑参考 JC035 的 PlaceToken 势力标记落点（ZoneDetailId）。
        /// 【后续拓展·B 预留】调查对抗获胜 → OnInvestigationWon；势力对抗(Power)获胜 → OnPowerWon；
        /// 二者复用同一 WinnerIsSelf 条件与 StackEffectInstance.WinnerId 透传链路，仅需新增枚举 + EventToTiming 映射 + TriggerEmitPoints.Emit 方法。</summary>
        OnCombatWon,

        /// <summary>秘社激活能力（C组衍生）。对应玩家双击秘社 UI 弹出激活窗口、选择某激活选项后发动。
        /// 与 CharacterActivated/AttachmentActivated 并列，但来源是 CouncilZone 中的秘社实例；非快速行动、需门槛判定、进堆叠。
        /// 触点：CouncilCardUI 双击 → PlayerActionService 激活入口（门槛判定 + 进堆叠 + TargetRules 选目标）。
        /// 注意：秘社卡可含多个激活选项（ActivationOptions），每个选项视为一条独立的 CouncilActivated 效果流。</summary>
        CouncilActivated,

        /// <summary>【P3】触发：**一次势力对抗结束时**广播（含平局、含无人获得标记的情况）。对应 GameEvent.OnPowerCombatEnded。
        /// 与 `OnCombatWon` 的区别：后者只在"分出胜者的战斗对抗"后发；本时机在势力对抗结束时无条件发。
        /// 供【声望】（`KeywordEffects.Renown()`）响应：条件谓词 `RenownMajority` 比较该地区双方"声望角色"数量，
        /// 由较多的一方在该地区放置一个势力标志（多个声望角色也只放一个）。</summary>
        OnPowerCombatEnded,

        /// <summary>【P3】触发：**调查对抗（Investigation）获胜**时广播。对应 `GameEvent.OnInvestigationWon`。
        /// 与 `OnCombatWon` 并列（后者只覆盖战斗对抗 Strength）；**平局/0:0 不广播**（无胜者语义）。
        /// 供 JC072「当你赢得**其所参与**的调查对抗时…」类效果响应：条件 `WinnerIsSelfInSameArea`
        /// 同时表达"本卡控制者获胜"与"本卡位于该地区"（＝其参与了这次对抗）。</summary>
        OnInvestigationWon,

        /// <summary>【P3】触发：**本卡翻面为暗藏者**（"被潜伏"）时广播。对应 `GameEvent.OnLatent`。
        /// 触点：装配层订阅 `CardManager.OnCardStateChanged` 的 `CovertChanged`，仅在 `IsCovert == true` 时 Emit
        /// （暗藏→正面由既有的 `UncoverTrigger` 负责，两者互不重叠）。
        /// 语义：**自身**时机——只有被潜伏的那张卡自己的 `OnLatent` 效果会被收集（与进场/现身同款）。
        /// 覆盖 JC054「潜伏强制触发：对所有位于本地区的其他角色造成 1 点伤害」（群体伤害＝`MassMode.AllInArea`）。</summary>
        OnLatent,
    }

    /// <summary>打出时机（仅事务卡/快速行动用，描述"何时允许打出"）。</summary>
    public enum PlayTiming
    {
        MainPhase,     // 主阶段/专属步骤打出
        Response,      // 堆叠开放时作为响应插队打出（快速行动默认）
        NonResponsive, // 卡文注明"不能以响应打出"
    }

    /// <summary>效果生效位置（ZoneScope，声明式生效区）。
    /// 方案C（声明式 Scope）默认推导见 EffectEngine.Instance.GetDefaultScope/IsEffectActiveInZone。
    /// 每个 EffectData 可声明其生效区；Specified=未声明，回退到卡类型默认区。
    /// 未来扩展（如"墓地/卡组生效"）在此枚举补值并在 GetDefaultScope/IsEffectActiveInZone 加分支即可。</summary>
    public enum ZoneScope
    {
        AreaZone,      // 争夺区（角色默认生效位置）
        HandZone,      // 手牌区（如"公开"关键字仅约束手牌派遣暗藏）
        AttachTarget,  // 结附于合法目标时（附属默认；严格规则下=仅 AttachedZone）
        AssetZone,     // 资产区
        Graveyard,     // 墓地（未来扩展：墓地生效效果）
        DeckZone,      // 卡组（未来扩展：卡组生效效果）
        SealedZone,    // 【P3·K16 触发版】封印区：`TurnManager.SealCard` 先把被封印卡移入封印区再广播 OnSealed，
                       // 故"被封印在一个角色上时…"类自身触发的**触发时来源区＝封印区**（JC110/JC100）。
        Specified,     // 由卡文指定区域（未声明→回退卡类型默认）
    }

    /// <summary>静止/禁止效果种类（D19-⑤ 打通：让"不能被装备结附"等静止效果可被系统识别与校验）。
    /// 与持续关键字授予（公开）不同——静止效果是规则约束，常驻、不授出能力，挂在 CardInstance.StaticFlags。
    /// 采用通用枚举而非单布尔，以便未来"不能被锁定/不能成为目标/不能被横置"等零成本扩展。</summary>
    public enum StaticKind
    {
        CannotBeAttached,   // 不能被装备/附属结附（如 JC001 通灵猫头鹰）
        BlockDeployCovert,  // 全局封锁：当本卡未横置时，控制者的对手不能进行秘密派遣行动（JC068 阿格斯IV）
        /// <summary>
        /// 【P3·K18】来源卡**所在地区略过指定对抗**（JC098「持续：本地区略过调查对抗步骤」）。
        /// `StaticEffect.Param` ＝对抗名，可 `,` 组合：`"调查"` / `"战斗"` / `"势力"`（缺省空＝"调查"）。
        /// 生命周期：挂 `EffectTiming.Continuous` 的同一 `EffectData` 上 → 前提满足时由 `RegisterPersistent` 登记，
        /// 离场/前提丧失时注销；消费点＝`TurnManager.ResolveCurrentAreaCombat` 经 `EffectEngine.AreaSkipsCombat`
        /// 实时判定（按卡**当前**所在争夺区匹配地区，故移动换区自动跟随）。
        /// </summary>
        SkipAreaCombat,
        /// <summary>
        /// 【P3·K21】来源卡**所在地区内的"敌方"暗藏者不能参与势力对抗**（JC023 范·弥尔顿
        /// 「持续：所有本地区的敌方暗藏者不能参与势力对抗」）。
        /// 语义："敌方"＝**非本卡控制者**（不对称：只抹掉对手的暗藏者势力贡献，本方暗藏者照常贡献）。
        /// 生命周期与 `SkipAreaCombat` 同款（`Timing = Continuous` 同一 `EffectData`，前提满足登记 / 离场注销）；
        /// 消费点＝`AreaStatsCalculator`（对抗结算与 UI 展示共用的单一真相源），
        /// 判定按卡**当前**所在地实时匹配地区（`EffectEngine.ExcludesCovertPower`）。
        /// </summary>
        ExcludeCovertPower,
        /// <summary>
        /// 【P3·K29/K21】来源卡**在场期间**，**敌方**（＝非本卡控制者）**其上有牌封印的角色**在重置步骤中
        /// **不能被重置**（JC108 乔治·帕佐，织梦人「持续：只要…在场，所有其上有牌封印的敌方角色于重置步骤中不能被重置」）。
        /// 生命周期与 `SkipAreaCombat`/`ExcludeCovertPower` 同款（`Timing = Continuous` 同一 `EffectData`，
        /// 前提满足登记 / 离场、前提丧失、卡失效时注销）；**不按地区匹配**（卡文无"本地区"限定 → 全局）。
        /// 消费点＝`CardManager.UntapAll`（重置步骤的唯一解横置出口），判定
        /// `EffectEngine.IsUntapBlocked(card)`。
        /// </summary>
        NoUntapWithSealedCards,
        /// <summary>
        /// 【P3·K18】来源卡**所在地区**在对抗阶段中**额外进行一次势力对抗**（JC067 约翰·F·纳特里
        /// 「持续：本地区在对抗阶段时额外进行一次势力对抗」）。
        /// 生命周期与 `SkipAreaCombat` 同款（`Timing = Continuous` 同一 `EffectData`，前提满足登记 / 离场注销；
        /// 按卡**当前**所在争夺区实时匹配地区）；消费点＝`TurnManager.AdvancePhase` 的 `CombatPower` 分支：
        /// 当前地区本回合还没跑过追加的那次 → 原地再进一次 `CombatPower`（同地区同类型），跑完再推进地区游标。
        /// **每地区每回合至多追加一次**（`TurnManager` 用"本回合已追加地区集合"去重，重置步骤清空）。
        /// </summary>
        ExtraAreaPowerContest,
        /// <summary>
        /// 【P3·K21】来源卡**在场期间**，场上**带"锁定标记/锁定标志"的暗藏者不能【现身】**
        /// （JC122 哈克·亚当斯「持续：只要哈克·亚当斯在场，所有具有锁定标记的角色或暗藏者不能翻面」）。
        /// **用户 2026-09 口径**：只需限制**现身**行为即可（无需封禁"潜伏/翻面为暗藏者"方向），
        /// 且只对**带锁定标记的暗藏者**生效（正面角色没有"现身"动作，天然不受影响）。
        /// `Param` ＝标志名，可 `,` 组合（缺省＝`"锁定标记,锁定标志"`，卡库两种写法并存）。
        /// 生命周期与 `NoUntapWithSealedCards` 同款（`Timing = Continuous`，前提满足登记 / 离场注销）；
        /// 消费点＝`TurnManager.RevealCovert`（现身的唯一玩家入口），判定 `EffectEngine.IsRevealBlocked(card)`。
        /// </summary>
        NoRevealWithLockCounter,
        /// <summary>
        /// 【P3·K25】来源卡在场上时，**同一控制者至多操控 1 张「道途」**（JC034 心兽之道
        /// 「你只能操控至多一个道途」）。声明方式：`Timing = Static` + 本标记（经 `RegisterPersistent`
        /// 的静止分支落到 `CardInstance.StaticFlags`）；**只声明在印了该规则的卡上**——
        /// 未印该规则的「道途」卡不受限制。
        /// 消费点＝`PathRule`（新建，与 `LeaderRule` 同款）：进场/移动后（`ZoneManager.OnPostMove`）若该控制者
        /// 场上「道途」数 &gt; 1 且其中任一携带本标记 → **立刻由该控制者选择一个牺牲**（不入堆叠、不可被响应，
        /// 牺牲＝死亡并广播死亡触发）；单机走 `IGameUIHooks.PromptPickCard`、联机走 `Kind.SelectTarget`、
        /// 无 UI/网关走确定性兜底（优先牺牲最新进场者）。
        /// **用户 2026-09 口径**：与【领袖】同一套处理（存在多个道途时**玩家选择一个牺牲**）。
        /// </summary>
        UniquePathAtMostOne,
        // CannotBeTarget,  // 未来：不能成为目标
        // CannotBeTapped,  // 未来：不能被横置
    }

    /// <summary>单条静止/禁止效果（D19-⑤）。挂在 EffectData.StaticEffects，由 RegisterPersistent 置位到 CardInstance.StaticFlags。</summary>
    [GlobalClass]
    public partial class StaticEffect : RefCounted
    {
        [Export] public StaticKind Kind { get; set; } = StaticKind.CannotBeAttached;
        /// <summary>可选参数（如限定某种附属类型）；默认空=对所有适用。</summary>
        [Export] public string Param { get; set; } = "";
    }

    /// <summary>持续效果前提的结构化表达（D19-② 打通：替代原文本 ContinuousPrereq 占位）。
    /// 用于"若你的资产区具有【心心】(心灵领域忠诚≥2)"这类条件持续效果，避免解析中文文本。</summary>
    [GlobalClass]
    public partial class ContinuousPrereqSpec : RefCounted
    {
        /// <summary>前提种类。目前支持：DomainLoyalty（某领域忠诚≥阈值）。</summary>
        [Export] public PrereqKind Kind { get; set; } = PrereqKind.None;
        /// <summary>领域（仅 DomainLoyalty 用）。</summary>
        [Export] public MagicDomain Domain { get; set; } = MagicDomain.Mind;
        /// <summary>所需最低点数（DomainLoyalty 用）。</summary>
        [Export] public int Amount { get; set; } = 0;
        /// <summary>统计区域（DomainLoyalty 用，默认资产区）。</summary>
        [Export] public CardZone Zone { get; set; } = CardZone.AssetZone;
        /// <summary>是否按忠诚点数线性缩放授予量（D19 扩展，JC018 用）。
        /// false（默认）= 布尔开关：忠诚≥Amount 才授予，授予量取 ContinuousGrants.Amount。
        /// true = 线性计量：授予量 = ContinuousGrants.Amount × 当前领域忠诚点数 N（N=0 视为未满足）。
        /// 例：JC018「每有一个【心】获得一个黑刀」= DomainLoyalty(Mind) + ScaleByLoyalty=true + Attribute=战斗。</summary>
        [Export] public bool ScaleByLoyalty { get; set; } = false;

        /// <summary>【P3·B】前提的附加参数（如 `AreaMarkOwner` 的归属：`"自己"`/`"对手"`；空＝自己）。</summary>
        [Export] public string Param { get; set; } = "";

        /// <summary>
        /// 【P3·F】**按计数缩放的持续授予**：取值表达式（复用 `EffectValueResolver` 的语法），
        /// 求出的 N 既是授予倍数（每条 `ContinuousGrant.Amount × N`），也是"前提是否满足"的判据（N&gt;0 才算前提成立）。
        /// 例：JC037 `计数:区域:争夺区:暗藏者:自己`（每操控一个暗藏者 → 一个黑眼）；
        ///     JC045 `计数:卡上标志:时间标志:自身`；JC109 `计数:封印牌:自身`。
        /// 与 `ScaleByLoyalty` 的区别：后者固定按"领域忠诚"缩放，本字段按任意已登记表达式缩放。
        /// </summary>
        [Export] public string ScaleExpr { get; set; } = "";

        /// <summary>
        /// 【P3·图标镜像】持续授予的**来源改写**：本效果不取 `ContinuousGrants`（卡脚本静态声明），
        /// 而是**镜像"被封印在本卡上的角色牌"能力栏内的所有能力图标**（JC066 封魔瓶瑟琳娜）。
        /// 语义：逐张读取封印在本卡上的**角色牌**的**印刷能力图标**（调查/战斗/势力 + 先手三项），
        /// 按既有图标→属性映射（`EffectEngine.IconToAttribute`）汇总后**额外授予本卡**（与自身图标叠加）。
        /// 授予量随封印牌集合变化 → 封印落地（`TurnManager.SealCard`）/ 任意换区（`ZoneManager.FireMoved`）
        /// 触发的 `ReevaluatePrereqs` 会**撤销重算**（不依赖 `ScaleExpr` 的 N&gt;0 门槛，前提恒为常驻）。
        /// </summary>
        [Export] public bool MirrorSealedIcons { get; set; } = false;
    }

    /// <summary>
    /// 【P3·K22】"**牺牲一个角色**"作为**发动/打出的代价**（用户 2026-09 口径）：
    ///  · **由效果发动者自己选择**被牺牲的角色（UI 弹选择窗；无 UI/headless 走确定性兜底＝第一个合法候选）；
    ///  · **除非卡文明确写了"暗藏者"，否则不能牺牲暗藏者**（`AllowCovert`）；
    ///  · **牺牲＝死亡**：走 `CardManager.ResolveDeath(cause: Sacrifice)` → **正常触发死亡时机**；
    ///  · 代价在**入堆叠之前**支付（与"横置本角色/牺牲本角色"同一代价阶段）。
    /// 判定单一实现＝`EffectEngine.GetSacrificeCandidates` / `IsLegalSacrifice`，候选侧与权威侧共用（不会分叉）。
    /// </summary>
    [GlobalClass]
    public partial class SacrificeCostSpec : RefCounted
    {
        /// <summary>子类别白名单（空＝不限）；命中"打印或运行期获得的子类别之一"即可（如 JC038「吸血鬼或奴仆角色」）。</summary>
        [Export] public Array<string> SubCategories { get; set; } = new Array<string>();
        /// <summary>候选限定在**来源卡所在地区**（JC094「牺牲**本地区**的另一个角色」）。</summary>
        [Export] public bool LocalZone { get; set; } = false;
        /// <summary>排除来源卡自身（卡文写"**另一个**角色"时置 true）。</summary>
        [Export] public bool ExcludeSource { get; set; } = false;
        /// <summary>是否允许牺牲**暗藏者**（口径：除非卡文明确写"暗藏者"，否则 false）。</summary>
        [Export] public bool AllowCovert { get; set; } = false;
        /// <summary>是否要求候选**已横置**（预留：如"牺牲一个已横置的角色"）；默认 false＝不限。</summary>
        [Export] public bool RequireTapped { get; set; } = false;
    }

    /// <summary>
    /// 【P3·K3】持续型"**伤害改由本卡承受**"的受保护对象（**用户 2026-09 口径：JC077/JC121 这类卡是持续效果**，
    /// 不是入堆叠的触发、也不是"替代式效应"框架）。
    /// </summary>
    public enum DamageRedirectVictim
    {
        None,                     // 未声明（不启用）
        /// <summary>本卡所在地区内、**本方**（与本卡同控制者）、**非灵体**的角色（JC077 庇佑圣灵
        /// 「每当一个位于本地区，且非灵体的本方角色将受到伤害时，该伤害改为对庇佑圣灵造成」）。</summary>
        FriendlyNonSpiritLocal,
        /// <summary>本卡**所结附的宿主**（JC121 空鸣御守「每当**受此结附的角色**将受到伤害时，
        /// 该伤害改为对空鸣御守造成」）。</summary>
        AttachedHost,
    }

    /// <summary>【P3·K3】持续型伤害重定向声明（见 <see cref="DamageRedirectVictim"/>）。</summary>
    [GlobalClass]
    public partial class DamageRedirectSpec : RefCounted
    {
        /// <summary>受保护对象；`None` ＝ 不启用（fail-closed：不认识就不生效）。</summary>
        [Export] public DamageRedirectVictim Victim { get; set; } = DamageRedirectVictim.None;
    }

    /// <summary>
    /// 【P3·K20】**持续型"失去图标"**声明（JC123 饮魂者朱迪「只要…在场，所有具有锁定标记的角色或暗藏者
    /// 失去所有［黑星］和［白星］能力图标」）。与一次性 `ReplaceIcon` 的区别：这是**在场期间持续抑制**、
    /// 来源离场/标志消失即恢复，且受影响集合会随场上变化重算。
    /// </summary>
    [GlobalClass]
    public partial class IconSuppressionSpec : RefCounted
    {
        /// <summary>要"失去"的**能力图标**清单（如 `黑星`/`白星`）：逐条经 `IconToAttribute` 映射到属性，
        /// 抑制量＝该属性的**当前有效贡献**（`势力` 取 `CurrentPower`；`先手势力` 取授予的『先手势力』）。</summary>
        [Export] public Array<string> Icons { get; set; } = new Array<string>();
        /// <summary>受影响对象须带有**其中任一**卡上标志（空＝不限对象）。
        /// 注：卡库里"锁定标记 / 锁定标志"是同一概念的两种写法 → 两种都登记，二者等效。</summary>
        [Export] public Array<string> RequireCounters { get; set; } = new Array<string>();
    }

    /// <summary>【P3·K20】持续型"**失去魔法领域图标**"的受影响对象（JC099 / JC101）。</summary>
    public enum DomainSuppressionVictim
    {
        None,                             // 未声明（不启用）
        /// <summary>**敌方**（与本卡控制者不同）、**其上有牌封印**的**角色**（JC099 徘徊的鬼魂
        /// 「所有其上有牌封印的敌方角色失去魔法领域图标」）。</summary>
        EnemyCharacterWithSealedCards,
        /// <summary>本卡**所结附的宿主**（JC101 直视深渊「受此结附的角色…失去所有魔法领域图标」）。</summary>
        AttachedHost,
    }

    /// <summary>【P3·K20】持续型"失去魔法领域图标"声明。</summary>
    [GlobalClass]
    public partial class DomainSuppressionSpec : RefCounted
    {
        [Export] public DomainSuppressionVictim Victim { get; set; } = DomainSuppressionVictim.None;
    }

    /// <summary>持续效果前提种类。</summary>
    public enum PrereqKind
    {
        None,
        DomainLoyalty,   // 控制者在某区域的某领域忠诚≥ Amount
        SelfUntapped,    // 来源卡自身未横置（JC068：只要阿格斯IV未横置）
        /// <summary>【P3·B】来源卡**所在地区没有敌方角色**（JC016 执行部精锐；暗藏者不算"角色"）。</summary>
        NoEnemyCharacterInArea,
        /// <summary>【P3·B】来源卡**所在地区有指定归属的势力标志**（JC084 街头混混；`Param`=自己/对手，`Amount`=至少几个，缺省 1）。</summary>
        AreaMarkOwner,
        /// <summary>
        /// 【P3·G 扩展】本卡的**结附宿主**属于指定大类（`Param` = `"角色"` / `"秘社"`；无宿主 → 不满足）。
        /// 用途：JC082 索亚之书「选择一项：·借阅＝结附于目标角色（宿主获得［白眼］）／·收藏＝结附于本方秘社」
        /// —— 两种形态的效果按**宿主种类**区分（打出时选的结附目标即"选择的一项"）。
        /// </summary>
        HostKind,
    }

    /// <summary>目标选择的规模模式（【K2】群体/批量动作）。</summary>
    public enum MassMode
    {
        /// <summary>单目标（缺省，`TargetSpec` 的既有语义不变）。</summary>
        Single,
        /// <summary>
        /// 群体：作用于"某地区内符合条件的所有卡"，**不需要玩家选单个目标**。
        /// 地区 = 本次结算的目标地区（`targetAreaId`，如打出时选的地区/触发事件所在地），为空时＝来源所在地区；
        /// 集合条件 = `TargetSpec` 的 `Kind` / `IncludeCovert` / `Filter`（检索筛选语法）/ `Restrictions`。
        /// 执行方式：一次结算内对集合每张卡各执行一次动作（**不做"批量期间挂起触发"**，见 `MassTargetPool`）。
        /// </summary>
        AllInArea,
    }

    // ==================== 数据节点 ====================

    /// <summary>目标选择描述（D16 目标校验层使用）。
    /// 继承 RefCounted：见 EffectAction 说明（引用计数，避免 GC 误回收）。</summary>
    [GlobalClass]
    public partial class TargetSpec : RefCounted, Script.Data.Effects.ITargetDesc
    {
        /// <summary>目标种类（复用 TargetKind）。</summary>
        [Export] public TargetKind Kind { get; set; } = TargetKind.None;

        /// <summary>
        /// 【K14·或种类】第二个可接受的大类（`None` = 无）。与 <see cref="Kind"/> 构成 **OR**（单目标二选一），
        /// 用于卡文"目标**附属或资产**"这类表达（JC107）；`Restrictions`/`Filter` 等条件对两个大类同样适用。
        /// 取候选时两个大类的候选池合并；结算复检接受任一。
        /// </summary>
        [Export] public TargetKind KindAlt { get; set; } = TargetKind.None;

        /// <summary>目标过滤条件文本（如"已横置""具魔法领域图标""结附于角色"）。</summary>
        [Export] public string Filter { get; set; } = "";

        /// <summary>是否需要玩家选择目标。JC011（横置所有敌方角色）等不需取对象时为 false。</summary>
        [Export] public bool NeedsSelection { get; set; } = true;

        /// <summary>是否允许以来源自身（施法者）为目标。默认 false（触发效果一般不能打自己）。
        /// "袭击"类关键词（KeywordEffects.Strike）设为 true：巨型蝙蝠现身袭击可指定自身为目标。
        /// 注意：GetLocalZoneTargets 在收集候选时据此决定是否排除 t.Source。</summary>
        [Export] public bool CanTargetSelf { get; set; } = false;

        /// <summary>
        /// 【P3·复合目标限制】第二条**完整**目标声明，与本体构成 **OR**（卡文"目标**……或……**"且两侧条件不同时用，
        /// 如 JC043「消灭目标**结附于秘社**或**本地区**上的附属」——两者都是附属，但一靠宿主种类、
        /// 一靠"本地区"，无法用 `KindAlt`（只表达大类 OR）或 `Restrictions`（AND）表达）。
        /// 语义：候选池＝两条各自收集的并集（去重，本体优先）；合法性/结算复检＝任一条通过即通过。
        /// 为空（缺省）时行为与既有实现完全一致。
        /// </summary>
        [Export] public TargetSpec SpecAlt { get; set; } = null;

        /// <summary>是否限定"本地区"：目标须与来源同 AREASLOT（规则⑥）。默认 false。</summary>
        [Export] public bool IsLocalZone { get; set; } = false;

        /// <summary>
        /// 【P3】目标候选**跨全部地区**（显式 opt-in）：触发路径默认只取"来源所在 SLOT"内的候选
        /// （本地区语义），来源**没有地区**（如结附于秘社的附属、秘社自身）或卡文本身未限定本地区时，
        /// 用本标志显式打开"全场争夺区"候选。
        /// 用途：JC009 远东六号科研所（结附于本方秘社）「进场触发：将目标角色封印在本卡上，然后遏制1」——
        /// 卡文/FAQ 只要求"选择一个角色"，不限定地区。
        /// </summary>
        [Export] public bool AllAreas { get; set; } = false;

        /// <summary>是否同时接受"暗藏者"作为合法目标（与 Kind=Character 组合表达"角色或暗藏者"）。
        /// 设 true 时 Character 校验允许 IsCovert 任一状态（角色/暗藏者均合法）。
        /// 注（2026-08-11）：翻面（规则③）改由**版本号**覆盖——SetCovert 触发 BumpVersion，
        /// 结算时 IsTargetLost 比对 TargetVersionId 不一致即判丢失（正→反、反→正双向皆丢）。
        /// 本标志只放宽"入栈/结算时"的静态朝向合法性，不影响翻面丢失。</summary>
        [Export] public bool IncludeCovert { get; set; } = false;

        /// <summary>精筛条件列表（全部满足才通过）。含 LocalZone 等数据驱动限定，由 TargetValidity.Check 统一校验。
        /// 注（2026-08-11）：原 ControllerChangedToEnemy/Friendly、SubCategoryGained/Lost 等"结算前变化"类枚举已删除，
        /// 其语义改由 D16 版本号方案统一覆盖（控制者/子类别变化触发 BumpVersion，结算比对 TargetVersionId 判丢失），
        /// 不再经 Restrictions 声明方向性记忆。</summary>
        [Export] public Godot.Collections.Array<TargetRestriction> Restrictions { get; set; }
            = new Godot.Collections.Array<TargetRestriction>();

        /// <summary>条件参数（子类型名/卡名/数值等，配合带参 Restrictions 使用，如 CostAtLeast 的门槛值）。</summary>
        [Export] public string Param { get; set; } = "";

        /// <summary>
        /// 【K2】群体规模模式（缺省 `Single`＝既有单目标语义）。
        /// 设 `AllInArea` 时本效果作用于"地区内符合条件的所有卡"（见 `MassMode` 与 `MassTargetPool`）：
        /// 动作序列会对集合内每张卡各执行一次，**在一次结算内全部完成**；`NeedsSelection` 应为 false。
        /// </summary>
        [Export] public MassMode MassMode { get; set; } = MassMode.Single;
    }

    /// <summary>
    /// 【K15·多目标槽位】一个"目标槽"：除了"该槽接受什么目标"（复用 `TargetSpec` 的全部字段）外，
    /// 额外表达"这个槽是否可以留空"。
    /// 语义（用户 2026-09 口径）：卡文"选择目标本方角色**和/或**目标敌方角色"＝两个槽（本方角色 / 敌方角色），
    /// **各自可留空**（`Optional=true`），但整条效果**至少要有 `MinChosenSlots` 个槽被填**
    /// （JC021＝1：一个都不选就不算合法打出）。
    /// </summary>
    [GlobalClass]
    public partial class TargetSlot : RefCounted
    {
        /// <summary>该槽接受的目标描述（`Kind` / `KindAlt` / `Filter` / `Restrictions` / `IsLocalZone`…）。</summary>
        [Export] public TargetSpec Spec { get; set; } = new TargetSpec();

        /// <summary>该槽是否可留空（true＝玩家可以不选这个槽；false＝必须选一个合法目标）。</summary>
        [Export] public bool Optional { get; set; } = true;

        /// <summary>选择窗口里的槽位提示（如"本方角色""敌方角色""目标地区"）。</summary>
        [Export] public string Label { get; set; } = "";

        /// <summary>
        /// 【K15·槽间依赖】本槽候选是否**排除"位于本效果已选中的『地区槽』内的卡"**
        /// （用户 2026-09 口径：JC021 的 FAQ 禁止原地移动 → 已经在该目的地的角色**不出现在候选里**）。
        /// 因此取目标顺序上，"地区槽"应先于标记了本标志的卡槽被选择。
        /// </summary>
        [Export] public bool ExcludeInChosenArea { get; set; } = false;
    }

    /// <summary>
    /// 【K16·模式分支】一个"模式"（卡文"选择一项：·… ·…"）：自带目标声明与动作序列。
    /// 口径（用户 2026-09）：模式**在打出时选定、付费之前**，选定后**入堆叠即不可更改**；
    /// 结算时只执行所选模式的目标/动作（另一模式完全不参与）。
    /// </summary>
    [GlobalClass]
    public partial class EffectMode : RefCounted
    {
        /// <summary>模式名（选择窗口的按钮文本，如"将目标角色潜伏"）。</summary>
        [Export] public string Label { get; set; } = "";

        /// <summary>该模式的目标声明（可为 `Kind=None`＝该模式不需要目标）。</summary>
        [Export] public TargetSpec Target { get; set; } = new TargetSpec();

        /// <summary>该模式的动作序列。</summary>
        [Export] public Array<EffectAction> Actions { get; set; } = new Array<EffectAction>();
    }

    /// <summary>
    /// 【P3】持续性**玩家级费用修正**的声明（见 `EffectData.ContinuousCostMod`）。
    /// 卡文形态："持续：每个回合中你所打出的第一张**装备牌**，其所需费用减少1"（JC026）。
    /// </summary>
    [GlobalClass]
    public partial class ContinuousCostModSpec : RefCounted
    {
        /// <summary>修正值：**正值＝减费**（与 `Gain{"费用减少",N}` 同一约定：`实际费用 = 印刷费用 - mod`），如 1。</summary>
        [Export] public int Amount { get; set; } = 1;

        /// <summary>限定条件（`CostModFilter` 语法，如 `子类别:装备` / `组织:王座会`；空＝全匹配）。</summary>
        [Export] public string Filter { get; set; } = "";

        /// <summary>
        /// 该减免作用于哪条**付费通道**：
        ///  · `"打出"`（缺省）＝ 打出手牌卡的费用（`PlayCard` / 秘密派遣）；
        ///  · `"现身"`＝ **暗藏者付费翻面（现身）**的费用（`TurnManager.RevealCovert`；JC033「所有本方暗藏者，
        ///    付费翻面时其所需费用减少1」）。
        /// 两条通道各自查询、互不串台。
        /// </summary>
        [Export] public string CostScope { get; set; } = "打出";

        /// <summary>
        /// `true` ＝"**每回合可再生的一次性**"：本回合用掉后失效、**下回合自动恢复**（直到来源卡离场）；
        /// `false` ＝整回合持续生效（不限张数）。
        /// </summary>
        [Export] public bool PerTurn { get; set; } = true;
    }

    /// <summary>
    /// 【P3·一次性待触发承诺（"本回合中，当你…下一次…时，你可以…"）】
    /// 一个"承诺"：声明"在指定的**触发时机**首次出现时，兑现一组动作"，并可在**到期点**（现支持"本回合结束"）
    /// 自动作废。用于 JC082 索亚之书「快速行动2，横置：**本回合中**，当你赢得**下一次**调查对抗时，
    /// 你可以抓一张牌」——效果本身不立即做事，而是**登记**这份承诺。
    ///
    /// 语义（与卡文一一对应）：
    ///  · 登记：`EffectData.Pending` 非空的激活/触发效果**结算时**由 `EffectEngine` 登记到承诺表；
    ///  · 兑现：相应时机首次出现且**归属该玩家**时，承诺被**兑现一次**（走正常触发管线：入堆叠→排序→让过→结算），
    ///    并立即从承诺表移除（"**下一次**"＝只兑现一次，与玩家最终是否选择执行无关）；
    ///  · 到期：`Expire="本回合结束"` → 回合结束时（`ExpireTurnEndEffects`）作废未兑现的承诺；
    ///  · 可选性：`Optional=true`（卡文"你可以"）→ 兑现时作为**非强制**触发（玩家可在排序窗放弃）。
    /// </summary>
    [GlobalClass]
    public partial class PendingEffectSpec : RefCounted
    {
        /// <summary>兑现时机（哪个触发时机首次出现时兑现）。</summary>
        [Export] public EffectTiming Timing { get; set; } = EffectTiming.OnInvestigationWon;

        /// <summary>到期点。目前只支持 `"本回合结束"`（空＝不过期，需显式兑现）。</summary>
        [Export] public string Expire { get; set; } = "本回合结束";

        /// <summary>是否可选（卡文"你可以"→ true：兑现时作为非强制触发，玩家可放弃）。</summary>
        [Export] public bool Optional { get; set; } = true;

        /// <summary>兑现时执行的动作序列。</summary>
        [Export] public Array<EffectAction> Actions { get; set; } = new Array<EffectAction>();
    }

    /// <summary>
    /// 单条持续授予（B组专用，持续效果"赋予什么"的结构化表达）。
    /// 继承 RefCounted：见 EffectAction 说明（引用计数，避免 GC 误回收）。</summary>
    [GlobalClass]
    public partial class EffectGrant : RefCounted
    {
        /// <summary>授予种类：Keyword（关键字，如"公开""先手""调查"）/ Icon（图标，如"心心""白眼""黑刀"）/ Attribute（标志属性计数，如"战斗属性"）。</summary>
        [Export] public string Kind { get; set; } = "Keyword";
        /// <summary>授予的值（关键字名/图标名/属性名）。</summary>
        [Export] public string Value { get; set; } = "";
        /// <summary>数量（主要用于 Attribute 叠加计数，默认 1）。</summary>
        [Export] public int Amount { get; set; } = 1;
    }

    /// <summary>
    /// 通用效果节点（B/C/F 三组共用）。
    /// 字段按用途分组：
    ///  - 调度元信息：EffectId / Timing / PlayTiming / ActivateWindow / ActivateCondition / Cost / Forced
    ///  - 持续专用：   Scope / ContinuousPrereq / ContinuousEffect
    ///  - 结算要素：   Target / Condition / Actions / Duration
    ///  - 备注：       FaqNote
    ///
    /// 继承 RefCounted：本项目的所有具体效果均由卡片 C# 脚本在构造函数中构建并挂入
    /// EffectDataList，不序列化进 .tres（.tres 仅保存牌面基础信息），故无需 Resource 派生；
    /// 但 RefCounted 提供引用计数，避免 GodotObject 在 GC 时被误回收导致 EffectDataList 元素失效。
    /// </summary>
    [GlobalClass]
    public partial class EffectData : RefCounted
    {
        // ---- 调度元信息 ----
        [Export] public string EffectId { get; set; }
        [Export] public EffectTiming Timing { get; set; }
        [Export] public PlayTiming PlayTiming { get; set; } = PlayTiming.MainPhase; // 仅事务卡用
        [Export] public string ActivateWindow { get; set; } = "";   // 快速行动/行动/触发（角色·附属激活）
        /// <summary>
        /// 【K26·阶段排除】本效果**不可发动 / 不可（作为该卡效果）打出**的阶段名（多个用 `,` 分隔，空=无排除）。
        /// 已登记的阶段名（与用户规则文本一致）：
        ///  · `"分配战斗伤害阶段"` = `TurnPhase.CombatStrength`（战斗对抗结算中由败方分配伤害的那一步）。
        /// 事务卡用法：卡上任一 `OnSorceryCast` 效果声明排除 → 该卡在该阶段不可打出（`TurnManager.PlayCard` 门禁）。
        /// 激活能力用法：该激活效果在该阶段不可发动（`ActivateEligibility` 高亮/候选 + `EffectEngine.ActivateAbility` 权威门禁）。
        /// 单一判定实现见 `PhaseGateRule`；未登记的阶段名按"不排除"处理并告警（防拼错静默失效）。
        /// </summary>
        [Export] public string ExcludedPhases { get; set; } = "";
        [Export] public string ActivateCondition { get; set; } = ""; // 发动许可（横置本角色+快速行动2…）
        [Export] public int Cost { get; set; } = 0;                 // 费用数字（快速行动N 中的 N）
        /// <summary>
        /// 【K23·次数上限】本能力**每回合最多发动次数**（0 = 不限次；卡文"该能力每回合只能发动一次/两次"）。
        /// 用户 2026-09 口径：
        ///  · 计数基准 = **每张卡 · 每回合 · 每能力**（按 `EffectId` 分别计数，见 `CardInstance.GetAbilityUsesThisTurn`）；
        ///  · **该卡版本号变动 ⇒ 限制重新可用**（`CardInstance.BumpVersion` 会清空该卡的计数）；
        ///  · 回合重置发生在**重置步骤**（`TurnManager.DoReset` → `CardManager.ResetAllAbilityUsesThisTurn`）。
        /// 消费点（两处，避免重复计数）：激活 = `ActivateEligibility` 门禁 + `EffectEngine.ActivateAbility` 登记；
        /// 触发 = `TurnManager.ResolveEntry` 的 Trigger 分支（结算前检查并登记）。
        /// </summary>
        [Export] public int MaxUsesPerTurn { get; set; } = 0;
        [Export] public bool Forced { get; set; } = false;          // 是否强制（不可跳过）
        /// <summary>
        /// 【K1·动态数值 X】本效果的动态值表达式（空 = 无动态值）。动作参数里用占位符 `$X` 引用（见 `EffectAction.Args`）。
        /// 已登记写法见 `EffectValueResolver`（`事件数量` / `计数:区域:…` / `计数:卡上标志:…` / `计数:地区标志:…` / `玩家选择`）。
        /// **X 求值为 0 时该效果不算发动**（用户 2026-09 口径）：触发不收集、激活不可发动、结算直接跳过；
        /// 表达式未登记或依赖未实现（如"玩家选择"缺数值输入 UI）同样按"不发动"处理（fail-closed）。
        /// </summary>
        [Export] public string ValueExpr { get; set; } = "";
        [Export] public CovertTriggerMode TriggerCovertMode { get; set; } = CovertTriggerMode.FaceUp; // 触发对暗藏状态的要求（默认仅正面朝上可触发）
        /// <summary>结算前校验【来源卡版本号】是否未变（默认 false）。设 true 时，入栈时记录来源卡 VersionId，
        /// 结算时若来源卡 VersionId 已变（离场/翻面/进手牌/进墓地等触发 BumpVersion），则跳过结算。
        /// 争夺区→争夺区移动不 BumpVersion，故不影响此类效果结算。机动（Maneuver）等依赖来源卡身份稳定的效果设 true。</summary>
        [Export] public bool RequireSourceVersionUnchanged { get; set; } = false;

        // ---- 持续专用（B组） ----
        // 默认 Specified：由 IsEffectActiveInZone 走 GetDefaultScope（附属→AttachTarget，角色→AreaZone），
        // 实现"附属默认在附属区生效、角色默认在争夺区生效"。此前默认 AreaZone 会让未声明 Scope 的
        // 附属卡激活效果被误当角色区判定而失效（D46）。显式声明 Scope 的效果按声明。
        [Export] public ZoneScope Scope { get; set; } = ZoneScope.Specified;
        [Export] public string ContinuousPrereq { get; set; } = "常驻"; // 持续前提（常驻/心灵领域忠诚≥2…），兼容旧文本
        [Export] public ContinuousPrereqSpec PrereqSpec { get; set; } = null; // 结构化前提（D19-②，优先于文本）
        [Export] public string ContinuousEffect { get; set; } = "";      // 持续效果文本（获得图标/关键字…）
        [Export] public Array<EffectGrant> ContinuousGrants { get; set; } = new Array<EffectGrant>(); // 持续授予结构化（关键字/图标/属性）

        // ---- 持续光环（D19-③ 打通：影响他者） ----
        /// <summary>光环型持续：源卡对自身授予 ContinuousGrants 的同时，对其控制者争夺区内其他符合条件的角色也授予同样的 Grants。
        /// AuraFilterExcludeDomain != None 时启用光环：被影响目标 = 同控制者、争夺区内、当前实例 MagicDomains 不含该域的角色。
        /// 例如 JC060 设 MagicDomain.Mind（"心"=心灵领域）：所有未印刷魔法领域图标的本方角色获得心图标+黑刀+心灵领域。
        /// 源卡自身若已含该域则被自动排除（不会给自己重复授予）。</summary>
        [Export] public MagicDomain AuraFilterExcludeDomain { get; set; } = MagicDomain.Mind;
        /// <summary>是否启用光环型持续（D19-③）：true 时源卡对自身授予 ContinuousGrants 的同时，
        /// 对其控制者争夺区内其他符合条件的角色也授予同样的 Grants（见 AuraFilterExcludeDomain）。</summary>
        [Export] public bool IsAura { get; set; } = false;
        /// <summary>光环过滤模式（B2，2026-08-09 修正）：true 时要求目标"完全没有魔法领域"（实例 MagicDomains 为空列表）才受光环影响；
        /// 用于 JC060——"所有未印刷魔法领域图标的本方角色"，也包括有鲜血领域但无心灵领域的角色都不应受影响。
        /// 与 AuraFilterExcludeDomain 互斥：本标志优先（设 true 时忽略 AuraFilterExcludeDomain 的单域排除语义）。</summary>
        [Export] public bool AuraRequiresNoDomain { get; set; } = false;

        // ---- 【P3·E】光环筛选扩展（组织 / 关键词 / 仅本地区） ----
        /// <summary>
        /// 【P3·E】光环的**组织筛选**（空＝不限）：非空时只影响 `Organization` 与来源卡模板相同的卡。
        /// 用于 JC038「所有其他本方【王座会】角色获得袭击1」、JC051「所有其他本方鸣钟教派角色获得白星」。
        /// </summary>
        [Export] public string AuraFilterOrganization { get; set; } = "";

        /// <summary>
        /// 【P3·K3·**去处替代**】本卡所在地区被赢取时，把本卡的**去处**从"送拥有者牌库底"改判为该字段值
        /// （JC079 圣婴：「触发：当你赢取本地区时，将圣婴置入你的计分区，而非置入其拥有者的牌库底」→ `"计分区"`）。
        /// · 生效条件＝本效果的 `Condition.Predicate` 在"赢取地区"上下文下成立
        ///   （缺省写法 `WinnerIsSelfInSameArea`＝"**你**赢取**本地区**时"）；判定复用 `EffectEngine.EvaluateCondition`
        ///   （与触发路径同一谓词实现），上下文＝(本卡／赢取者／被赢取地区)。
        /// · 消费点＝`AreaManager.ResolveAreaWon` 的"送牌库底"扫描（与『撤回』同一实现位，**就地改判去处**）——
        ///   与代码既有判断一致（该处注释已注明"改变本地区卡去向"类效果属 K3 替代效果，而非入堆叠的触发：
        ///   送库底是同步跑完的，而入堆叠触发异步结算，按触发写会"改判时卡已回库"）。
        /// · 目前仅登记 `"计分区"`；其余写法忽略（不改判，走原路径）。
        /// </summary>
        [Export] public string WinAreaDestination { get; set; } = "";

        /// <summary>
        /// 【P3·K22】**牺牲一个角色作为发动/打出代价**（用户 2026-09 口径，见 `SacrificeCostSpec`）：
        /// 非空时该激活能力在发动前必须牺牲一个由**发动者自己选择**的角色（候选/合法性见
        /// `EffectEngine.GetSacrificeCandidates`）；未指定候选时走确定性兜底（第一个合法候选）。
        /// </summary>
        [Export] public SacrificeCostSpec SacrificeCost { get; set; } = null;

        /// <summary>
        /// 【P3·K3】**持续型伤害重定向**：本卡在场期间，把符合条件的目标"将要受到的伤害"**改为由本卡承受**
        /// （JC077 庇佑圣灵 / JC121 空鸣御守；用户 2026-09 口径：这两张卡是**持续效果**）。
        /// 消费点＝`CardManager.DealDamageToInstance`（伤害唯一收口）经 `EffectEngine.ResolveDamageRedirect`
        /// 查询；被重定向后**不再二次重定向**（防乒乓），但承受方自身的"防止"（灵体/护盾）照常生效。
        /// </summary>
        [Export] public DamageRedirectSpec DamageRedirect { get; set; } = null;

        /// <summary>
        /// 【P3·K20】**持续型"失去图标"**：本卡在场期间，符合条件的卡失去指定能力图标（JC123）。
        /// 应用/还原由 `EffectEngine.ReapplyIconSuppressions` **统一重算**（先精确还原、再按当前局面施加），
        /// 触发点：在册来源增减 / 受影响卡增减标志（`AddCounter`/`RemoveCounter`）/ 进出场 / `ResetAll`。
        /// </summary>
        [Export] public IconSuppressionSpec IconSuppression { get; set; } = null;

        /// <summary>
        /// 【P3·K20】**持续型"失去魔法领域图标"**：本卡在场期间，符合条件的卡失去**全部**魔法领域图标
        /// （JC099 / JC101）。由 `EffectEngine.ReapplySuppressions` **统一重算**（先精确还原、再按当前局面施加）；
        /// 领域图标走 `CardInstance.MagicDomains`，抑制期间属性重算也会跳过重填（`AreDomainsSuppressed`）。
        /// </summary>
        [Export] public DomainSuppressionSpec DomainSuppression { get; set; } = null;

        /// <summary>
        /// 【P3·K20】**"印刷文本框视作空白"**（JC101 直视深渊）：本卡在场（结附）期间，符合条件的卡
        /// **失去效果**——即其**印刷效果不再生效**：不再注册持续效果、不再可发动、不再参与触发收集；
        /// 已在堆叠上的条目**照常结算**（用户口径："不影响**已经发动**的效果"）。
        /// 与 `DomainSuppression` 同一套统一重算（`EffectEngine.ReapplySuppressions`），故解除即恢复。
        /// 受影响对象复用 `DomainSuppression.Victim`（同一效果里两者一起声明，对象一致）。
        /// </summary>
        [Export] public bool TextBlank { get; set; } = false;

        /// <summary>
        /// 【P3·K13】**运行期授予的激活能力**（JC019 弥尔顿的镇魔枪：「受此结附的角色…**并获得『快速行动，横置：
        /// 此角色对位于本地区的目标非人类角色造成1点伤害，该伤害不能被防止』的能力**」）。
        /// 语义：本持续效果的**授予落点**（`GrantToHost` → 宿主；否则来源自身）**获得一条激活能力**：
        ///  · 该能力以**获得它的那张卡**为来源（故 `ActivateCondition="横置本角色"` 横置的是**宿主**，
        ///    与 JC019 的 FAQ 一致——横置结附的角色而非武器本身）；
        ///  · 候选/高亮/权威门禁与卡面固有激活能力**同一套**（`ActivateEligibility` / `ActivateAbility`）；
        ///  · 用户 2026-09 口径：宿主原本就有激活效果时，**在既有的"多激活效果选择窗口"里额外增列 1 项**
        ///    （`Arena.OpenAttachmentActivateChoice` 的候选已合并运行期授予项）；
        ///  · 来源卡离场/前提丧失 → `ReapplyGrantedAbilities` 统一重算收回（能力随之消失）。
        /// </summary>
        [Export] public EffectData GrantedAbility { get; set; } = null;

        /// <summary>
        /// 【P3·K15 扩展】**激活能力额外需要一个"目标地区"**（JC117 红眼航班「将目标本方角色移动到**目标地区**」/
        /// JC033 家族传承①「以暗藏者状态置于**目标地区**」）：`Target` 仍声明**卡目标**（角色）；
        /// 本字段为 true 时，发动流程还需由玩家选定一个**地区**，作为本次结算的 `targetAreaId`
        /// （入堆叠时锁定，随 `StackEntry.TargetAreaId` 下发 → `ResolveEffect` 的 `targetAreaId` 参数）。
        /// 未指定时引擎走**确定性兜底**（取第一个合法地区）。
        /// </summary>
        [Export] public bool NeedsAreaTarget { get; set; } = false;

        /// <summary>【P3·K15 扩展】目标地区**必须不同于卡目标所在地区**（JC117 FAQ："不能原地移动"）。</summary>
        [Export] public bool AreaTargetExcludesCardArea { get; set; } = false;

        /// <summary>
        /// 【P3·E】光环的**关键词筛选**（空＝不限）：非空时只影响具有该关键字（运行期授予 ∪ 卡面固有）的卡。
        /// 用于 JC105「所有其他本方灵体角色获得白星」。
        /// </summary>
        [Export] public string AuraFilterKeyword { get; set; } = "";

        /// <summary>
        /// 【P3·E】是否**只影响本地区**（与来源卡同一地区）的卡：true 时用于 JC059
        /// 「**位于本地区**的所有其他本方角色获得+1防御力」；缺省 false＝来源控制者的**全部**争夺区角色
        /// （"所有其他本方角色"，如 JC038/JC051/JC105）。
        /// </summary>
        [Export] public bool AuraLocalZoneOnly { get; set; } = false;

        // ---- 静止/禁止专用（D19-⑤） ----
        [Export] public Array<StaticEffect> StaticEffects { get; set; } = new Array<StaticEffect>(); // 静止/禁止效果（不能被装备结附等）

        // ---- 结算要素 ----
        [Export] public TargetSpec Target { get; set; } = new TargetSpec();

        /// <summary>
        /// 【P3·D】持续授予的**落点**：`true` ＝ 授予**结附的宿主**（卡文"受此结附的角色获得X"，JC073/JC116）；
        /// 缺省 `false` ＝ 授予来源卡自身。宿主不存在（附属未结附于卡，如结附秘社/地区）时**不授予**（用户 2026-09 口径）。
        /// </summary>
        [Export] public bool GrantToHost { get; set; } = false;

        /// <summary>
        /// 【P3】持续性的**玩家级费用修正**（卡文"**每个回合中**你所打出的**第一张**某类牌，其所需费用减少1"）：
        /// 非空时，本持续效果在注册期间为**来源控制者**挂一条费用修正（全局限定条件见 `Filter`）。
        /// `PerTurn=true` ＝"**每回合可再生的一次性**"：消费后本回合失效、**下回合自动恢复**，
        /// 时效终结＝来源卡离场。覆盖 JC026 洛特尼科夫。见 <see cref="ContinuousCostModSpec"/>。
        /// </summary>
        [Export] public ContinuousCostModSpec ContinuousCostMod { get; set; } = null;

        /// <summary>
        /// 【P3】一次性**待触发承诺**（卡文"本回合中，当你…**下一次**…时，你可以…"）：
        /// 非空时，本效果**结算时不执行普通动作**，而是把这份承诺登记到 `EffectEngine` 的承诺表；
        /// 对应时机首次出现且归属本卡控制者时兑现一次（走正常触发管线），并在到期点（本回合结束）自动作废。
        /// 见 <see cref="PendingEffectSpec"/>。覆盖 JC082 索亚之书②。
        /// </summary>
        [Export] public PendingEffectSpec Pending { get; set; } = null;

        // ---- 【K15】多目标槽位（替代单个 Target 的"多槽位"模式；为空＝沿用上面的单目标语义） ----
        /// <summary>
        /// 【K15·多目标】多槽位目标声明（`TargetSlot`：每个槽＝一个目标描述 + 是否可留空）。
        /// 非空即启用"多目标模式"：取目标时**逐槽**收集（每槽最多 1 个目标，可留空的槽允许不选），
        /// 全部选完后进入**确认面板**，确认通过才入堆叠（用户 2026-09 口径）。
        /// `Kind = Area` 的槽表示"目标地区"，其选中值落到 `StackEntry.TargetAreaId`；
        /// 卡类槽的选中值落到 `StackEntry.TargetCardUids`（有序，与槽序一致）。
        /// </summary>
        [Export] public Array<TargetSlot> TargetSlots { get; set; } = new Array<TargetSlot>();

        /// <summary>
        /// 【K15】多目标模式下**至少要填几个槽**（默认 0＝可以一个都不选；JC021＝1）。
        /// 不满足时：打出阶段直接判"不合法"（不能打出），结算阶段按"目标丢失/不成立"处理。
        /// </summary>
        [Export] public int MinChosenSlots { get; set; } = 0;

        [Export] public EffectCondition Condition { get; set; } = new EffectCondition();
        [Export] public Array<EffectAction> Actions { get; set; } = new Array<EffectAction>();
        [Export] public string Duration { get; set; } = "瞬时"; // 常驻/瞬时/直到回合结束

        // ---- 【K16】模式分支（"选择一项"；为空＝无模式分支，沿用上面的 Target/Actions） ----
        /// <summary>
        /// 【K16·模式分支】本效果的可选模式（卡文"选择一项：·… ·…"）。非空时：
        /// **模式在打出时选定（付费之前）**，选定后入堆叠**不可更改**；结算只执行所选模式的目标/动作。
        /// 见 `StackEntry.ModeIndex`、`TurnManager.BeginModeCardPlay` / `PlayModeCardAsync`、
        /// `IGameUIHooks.PromptEffectMode` / `ChoiceRequest.Kind.SelectEffectMode`。
        /// </summary>
        [Export] public Array<EffectMode> Modes { get; set; } = new Array<EffectMode>();

        /// <summary>是否有模式分支。</summary>
        public bool HasModes => Modes != null && Modes.Count > 0;

        // ---- 【K13】"袭击X"标记（供触发收集时把多个袭击来源**合并叠加**为一个触发） ----
        /// <summary>
        /// 【K13】本效果是否是"袭击X"关键词产生的效果（由 `KeywordEffects.Strike(amount)` 置位）。
        /// 收集阶段据此把**卡面自带袭击 + 运行期授予的袭击**合并为**一个**触发（数值叠加，用户 2026-09 口径：
        /// "已有袭击1 被授予袭击1 → 变为袭击2"）。
        /// </summary>
        [Export] public bool IsStrike { get; set; } = false;

        /// <summary>【K13】袭击的数值（`IsStrike=true` 时有效；＝该来源提供的袭击点数）。</summary>
        [Export] public int StrikeAmount { get; set; } = 0;

        /// <summary>取第 i 个模式（越界/无模式返回 null）。</summary>
        public EffectMode ModeAt(int index)
            => HasModes && index >= 0 && index < Modes.Count ? Modes[index] : null;

        /// <summary>本次结算（按模式）实际生效的目标声明：有模式分支且指定了模式 → 该模式的目标；否则 `Target`。</summary>
        public TargetSpec TargetFor(int modeIndex)
            => ModeAt(modeIndex)?.Target ?? Target;

        /// <summary>本次结算（按模式）实际生效的动作序列：有模式分支且指定了模式 → 该模式的动作；否则 `Actions`。</summary>
        public Array<EffectAction> ActionsFor(int modeIndex)
            => ModeAt(modeIndex)?.Actions ?? Actions;

        // ---- 备注 ----
        [Export] public string FaqNote { get; set; } = "";
    }
}
