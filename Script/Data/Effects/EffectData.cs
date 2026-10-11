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
        /// 供【声望】（`KeywordEffects.Renown()`）响应：条件谓词 `RenownMajorityInSameArea` 比较该地区双方"声望角色"数量，
        /// 由较多的一方在该地区放置一个势力标志（多个声望角色也只放 1 枚，广播窗口去重）。</summary>
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
        CouncilZone,   // 【P0·修复】秘社区的**秘社卡默认生效区**：`CouncilCardData` 的实例 `PlayerTemplate == null`
                       // （模板类型是 `CouncilCardData` 而非 `PlayerCardData`），`GetDefaultScope` 原先落到
                       // `Specified` → `ScopeMatchesZone` 的 `default: false` ⇒ **秘社激活被权威门禁无条件拒绝**
                       // （`EffectEngine.ActivateAbility` 的生效区校验），表现为"秘社能点、点完没反应"。
                       // 现在：秘社卡默认生效区＝本值，`ScopeMatchesZone` 命中 `CardZone.CouncilZone`。
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
        /// <summary>
        /// 【P3·K4】来源卡**未结附于角色**时**不能移动**（JC121 空鸣御守的「不能移动」前缀）。
        /// **用户 2026-09 口径（§7.4b A18/A19）**：
        ///  · 这是**独立效果行**，**只有未结附于角色时才生效**；结附于角色时本卡随角色一起移动（不适用本条）；
        ///  · "移动"**涵盖机动 / 效果移动 / 置换**（实现上收口在 `CardManager.MoveCardToArea` 唯一入口）。
        /// 声明方式：`Timing = Static` + 本标记 + `Scope = AreaZone`
        /// （未结附时本卡位于争夺区，区域化注册天然只在该状态成立——结附后进入附属区即自动注销）。
        /// 消费点＝`CardManager.MoveCardToArea`（`EffectEngine.HasStaticFlag(card, …)` 判定 + `AttachedTo == null` 复核）。
        /// </summary>
        CannotMoveWhileUnattached,
        /// <summary>
        /// 【P3·K21·JC064 戒严】来源卡**所在/所结附的地区**：**非指定组织的角色牌不可正面打出在该地区，
        /// 也不能移动进入该地区**（卡文「所有非【国家机构】的角色牌不可正面打出在本地区，也不能移动进入本地区」）。
        /// `StaticEffect.Param` ＝组织名（本卡＝`国家机构`）。
        /// **用户 2026-09 口径（§7.4b A19）**："不能移动进入"**涵盖机动 / 效果移动 / 置换**；
        /// 卡文 FAQ 明确**允许**的两条路径不受本规则限制：① **秘密派遣**（背面进场，走 `ZoneManager.MoveToArea`）；
        /// ② **其他方式放置进场**（如"操控死尸"从墓地放进场）——实现上以"**该卡此刻是否已在场上**"区分
        /// "**移动进入**"（在场卡的位置变更 → 拦）与"**放置进场**"（从墓地/手牌/牌库进场 → 放行）。
        /// 消费点：`TurnManager.PlayCard`（正面打出到该地区）＋ `CardManager.MoveCardToArea`（移动唯一收口）
        /// 经 `EffectEngine.IsEntryBlockedByOrgRule(card, areaUid)` 判定。
        /// </summary>
        BlockNonOrgEntryInArea,
        /// <summary>
        /// 【JC081 亚洛尔·前缀】"**除装备与祝福外不受附属**"：本卡**只能被指定子类别的附属结附**。
        /// `StaticEffect.Param` ＝**子类别白名单**（`,` 分隔，本卡＝`装备,祝福`）——结附它的附属必须命中其中之一，
        /// 否则**打出阶段即拦截**（`CardManager.IsLegalPlayTarget`）、结算阶段再兜底
        /// （`TurnManager` 的结附分支）。与 `CannotBeAttached`（JC001："任何装备都不能结附"）同类实现，
        /// 只是把"一律禁止"放宽成"白名单允许"；未写白名单（Param 空）→ 放行（fail-open，防拼错静默封死）。
        /// 判定单一实现＝`EffectEngine.GetAttachSubCategoryWhitelist` / `PassesAttachWhitelist`（两端共用，不依赖引擎实例）。
        /// </summary>
        OnlyAttachWithSubCategories,
        /// <summary>
        /// 【JC057 监控员·持续】"**只要监控员未横置，你可以随时检视本地区的任一暗藏者**"：
        /// 来源卡在场上且**未横置**时，其控制者获得一条**持续可见性授权**——可以查看
        /// **与来源卡同一地区**的**暗藏者**（含对手的）的卡面。
        /// 声明方式：`Timing = Static` ＋ `Scope = AreaZone` ＋ `PrereqSpec{Kind=SelfUntapped}`
        /// （横置即失效、重置即恢复：前提由持续注册表实时重判）。
        /// 消费点＝**UI 悬浮预览的可见性判定**（`Script/Manager/CovertInspectRule.CanViewerSeeCardFace`）——
        /// 这是一条**不经过效果结算的查看通道**（"随时检视"），故判定实现为**静态纯函数**
        /// （读卡面印刷规则 + 实例态），客户端/服务端同解、不依赖 EffectEngine 实例。
        /// </summary>
        AllowInspectLocalCovert,
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

    /// <summary>【P3·K5】"**自本方卡牌上移除一个（时间）标志**"作**发动代价**的声明（JC044 灾难使者
    /// 「行动，自本方卡牌上移除一个时间标志：遏制1」）。
    /// 口径（用户 2026-09，§7.4b A17/A27）：
    ///  · "移除一个时间标志"**就是发动代价**（不当作效果步骤）；
    ///  · 由**发动者自主选择**移除**哪张卡**上的标志（有 UI → 选择窗口；无 UI → 确定性兜底取第一个合法候选）；
    ///  · 代价在**入堆叠之前**支付（与"横置本角色/牺牲本角色"同一代价阶段）；**无合法候选 → 拒绝发动、无副作用**。
    /// 候选口径：**该发动者控制**、**在场上**（争夺区 ∪ 附属区）、带指定标志（合计 ≥ `Amount`）的卡。
    /// 判定单一实现＝`EffectEngine.GetCounterCostCandidates`（候选侧与权威侧共用，不会分叉）。
    /// </summary>
    [GlobalClass]
    public partial class CounterCostSpec : RefCounted
    {
        /// <summary>标志名白名单（卡库对同一概念存在多种写法，如 `"时间标志"` / `"时间标记"`）；命中任一即算。</summary>
        [Export] public Array<string> Markers { get; set; } = new Array<string>();

        /// <summary>需要移除的数量（缺省 1）。`AmountByPlayerChoice=true` 时本字段被忽略（按玩家选择的数量）。</summary>
        [Export] public int Amount { get; set; } = 1;

        /// <summary>
        /// 【P3·K5·JC055②】**移除数量由玩家选择**（"自本方卡牌上移除 **X** 个时间标志"）：
        /// 候选＝本方场上带该标志（≥1）的卡；**X ≥ 1**（用户口径 A30 补充：**不允许为 0**＝至少移除一个），
        /// 上限＝本方场上该标志总数；交互＝"点一张卡即记录用 1 点 → 确认后移除"（A30）。
        /// 支付在**入堆叠之前**；无候选 / 分配总和为 0 / 指定卡不合法 → 拒绝发动且无副作用。
        /// 支付的数量作为本次结算的 X（`ValueExpr="代价数量"` 读取，见 `EffectValueResolver`）。
        /// </summary>
        [Export] public bool AmountByPlayerChoice { get; set; } = false;
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
    /// 【P3·K10】**额外费用**的种类（**用户 2026-09 口径 A21–A24 / A32 批**，见 `ExtraCostSpec`）。
    /// 额外费用是**打出这张牌本身的费用之一**：在"打出付费阶段"支付、**卡入堆叠之前**付清、
    /// **不在堆叠里结算、不可被响应**（用户口径，类比万智牌的额外费用）。
    /// </summary>
    public enum ExtraCostKind
    {
        None,                // 未声明（不启用）
        /// <summary>JC049 深渊细语「额外费用：牺牲一个角色」——**必须支付**；**无合法牺牲对象则不能打出**。
        /// 牺牲者由**打出者自己选择**（A1：未写"暗藏者"→ 不可牺牲暗藏者；牺牲＝死亡，照常触发死亡时机）。</summary>
        SacrificeCharacter,
        /// <summary>JC081 亚洛尔「额外费用：展示任意数量手牌，每展示一张【圣贤】牌或具【圣】图标的牌，费用 -1」
        /// ——**可以不展示**（＝没有减免，全额支付照样能打出）；取消＝不打出、卡不入堆叠、**不扣任何费用**；
        /// 减免**一次性**（只作用于这一张）。</summary>
        RevealHandCards,
        /// <summary>JC135 余震「额外费用：支付 X 费」——X ＝**先选定的目标**的**印刷费用**
        /// （用户口径：先选目标再定 X；选 0 费附属则**不支付额外费用**、直接结算后续效果；
        /// 若合法目标的费用均大于可用费用则该卡不能打出）。</summary>
        PayTargetCost,
    }

    /// <summary>
    /// 【P3·K10】**额外费用声明**（卡级，挂在 `PlayerCardData.ExtraCost` 上）。
    /// 与 `EffectData.SacrificeCost`（**发动代价**）的区别：本项是**打出这张牌的费用**，
    /// 由 `TurnManager.PlayCard` 在付费阶段处理（不是效果结算的一部分）。
    /// </summary>
    [GlobalClass]
    public partial class ExtraCostSpec : RefCounted
    {
        /// <summary>额外费用种类；`None` ＝ 不启用。</summary>
        [Export] public ExtraCostKind Kind { get; set; } = ExtraCostKind.None;

        /// <summary>是否**必须**支付：JC049＝true（不满足则整张**不能打出**）；
        /// JC081＝false（不展示＝不减免，仍可全额打出）。</summary>
        [Export] public bool Mandatory { get; set; } = true;

        /// <summary>`RevealHandCards`：**合格牌**的筛选串（`CostModFilter` 语法）——每展示一张合格牌费用 -1。
        /// 例（JC081）：`组织:圣贤|域:神圣`（【圣贤】牌＝组织:圣贤；【圣】图标＝神圣领域图标）。</summary>
        [Export] public string RevealFilter { get; set; } = "";

        /// <summary>`SacrificeCharacter`：牺牲代价细则（候选＝本方场上正面角色；口径与 P3·K22 同一实现
        /// `EffectEngine.GetSacrificeCandidates`）。空＝按缺省（不可暗藏者、不限地区、可含自身）。</summary>
        [Export] public SacrificeCostSpec Sacrifice { get; set; } = null;
    }

    /// <summary>
    /// 【秘社批次·"牌作代价"】**从某区域取 N 张牌作为发动/打出的代价**（用户 2026-09 口径）。
    ///
    /// 与既有两类代价并列、同一实现风格（代价在**入堆叠之前**支付；候选枚举与门禁共用同一实现，UI 不会与服务端分叉）：
    ///   · `SacrificeCostSpec`（K22，牺牲一个**角色**＝死亡）；
    ///   · `CounterCostSpec`（K5，移除卡上的**标志**）；
    ///   · **本类**（把**牌本身**从某区域取走并做一件事）。
    ///
    /// 覆盖：
    ///  · MSJC010 貘组②③："**将一张/两张封印在貘组上的牌置于你的牌库底**"（`FromZone=SealedZone`，
    ///    `HostSelf=true`，`CostAction=MoveToZone{牌库底}`）；
    ///  · MSJC016 颂亡者②："**将你墓地中的七张牌封印在颂亡者上**"（`FromZone=Graveyard`，`HostSelf=true`，
    ///    `CostAction=Seal{自身,自身}`）；
    ///  · MSJC012 爱弗罗德家族①②："**横置三个本方奴仆/吸血鬼角色**"（`FromZone=BoardArea`，
    ///    `Filter="子类别:奴仆"`，`CostAction=Tap`，`UntappedOnly=true`）。
    /// </summary>
    [GlobalClass]
    public partial class CardCostSpec : RefCounted
    {
        /// <summary>取牌区域：`"封印区"`（缺省）/ `"墓地"` / `"手牌"` / `"场上争夺区"`。</summary>
        [Export] public string FromZone { get; set; } = "封印区";

        /// <summary>取牌来源宿主：`true`（缺省）＝**本效果来源卡**（秘社自身）；
        /// `false` ＝ 来源控制者的任意/全部合法卡（如"你墓地中的…"）。</summary>
        [Export] public bool HostSelf { get; set; } = true;

        /// <summary>张数（缺省 1）。"七张"＝7；"两张"＝2；"三个"＝3。</summary>
        [Export] public int Count { get; set; } = 1;

        /// <summary>筛选串（**复用 `DeckSearchFilter` 语法**，`;`=AND、`,`=OR）：
        /// 例 `"子类别:奴仆"` / `"关键字:灵体;类别:角色"` / `"独有"`。空串＝不限。</summary>
        [Export] public string Filter { get; set; } = "";

        /// <summary>是否**必须支付**（缺省 true）。true＝候选不足 N 张时不能发动；
        /// false＝能取多少取多少（当前仅有 true 的用户口径，保留字段以备后续卡）。</summary>
        [Export] public bool Mandatory { get; set; } = true;

        /// <summary>候选是否限定**未横置**（缺省 false）。用于"横置三个本方角色"（不能重复横置同一张）。</summary>
        [Export] public bool UntappedOnly { get; set; } = false;

        /// <summary>
        /// 对每张选中的牌执行的**代价动作**（普通 `EffectAction`，复用既有动词，禁止新造特例）。
        /// **约定：动作里的"对象"一律写 `"目标"`** —— `PayCardCost` 会把"选中的那张牌"作为 `target` 传入
        /// （写 `"自身"` 会解析成**来源卡**，即"把秘社自己塞进牌库"这种灾难性误动作，实测踩到）。
        ///  · `ActionVerb.Tap{目标}` → 横置（MSJC012 横置三个奴仆/吸血鬼角色）；
        ///  · `ActionVerb.MoveToZone{目标, 牌库底, 正面, 自己}` → 置于**来源控制者**的牌库底（MSJC010②③）；
        ///  · `ActionVerb.Seal{自身, 自身}` → 把选中的牌封印在来源卡上（MSJC016②，"自身"在此处指被封印者＝target）。
        /// 为空 ⇒ 只"取走"不做动作（当前无卡使用）。
        /// </summary>
        [Export] public EffectAction CostAction { get; set; } = null;
    }

    /// <summary>
    /// 【秘社批次·静态规则】**秘社在场期间**对符合条件的卡施加一条静止授予（用户 2026-09 口径）。
    ///
    /// 为什么单独建类、不复用光环（`EffectData.IsAura`）：光环的注册与重算入口
    /// （`EffectEngine.ApplyAura` / `ReevaluateAuras`）读的是 `PlayerCardData.EffectDataList`，
    /// 而秘社模板是 `CouncilCardData`（`PlayerTemplate == null`）⇒ 光环机制**结构上覆盖不到秘社**。
    /// 故按 `LeaderRule`/`PathRule` 同款做法建独立规则类，挂在装配层的 `ZoneManager.OnPostMove` 上重算。
    ///
    /// 覆盖：**MSJC012 爱弗罗德家族**"所有印刷费用小于或等于 2 的本方人类角色额外具有奴仆类别"
    /// （`FilterByMaxCost=2` + `FilterBySubCategories={人类}` + `FilterByCategory=角色` → 授予子类别 `奴仆`）。
    /// **来源卡自身不是角色、不入争夺区**，故天然不会授予自己（无需 `ExcludeSource`）。
    /// </summary>
    [GlobalClass]
    public partial class CouncilStaticGrant : Resource
    {
        /// <summary>按卡类别筛选（角色/附属/事务…）。</summary>
        [Export] public bool FilterByCategory { get; set; }
        [Export] public CardCategory TargetCategoryFlag { get; set; }

        /// <summary>按子类别筛选（任一命中即算命中）。</summary>
        [Export] public bool FilterBySubCategories { get; set; }
        [Export] public Array<string> TargetSubCategories { get; set; } = new Array<string>();

        /// <summary>按**印刷费用上限**筛选（"印刷费用小于或等于 2"＝`FilterByMaxCost=true, MaxCost=2`）。</summary>
        [Export] public bool FilterByMaxCost { get; set; }
        [Export] public int MaxCost { get; set; }

        /// <summary>是否**只影响本地区**（缺省 false＝本控制者全部争夺区）。</summary>
        [Export] public bool LocalZoneOnly { get; set; }

        /// <summary>授予的**子类别**（进 `CardInstance.CurrentSubCategories`，**叠加**不替换；印刷类别不变）。</summary>
        [Export] public string GrantSubCategory { get; set; } = "";
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
    /// 【P3·K4】**持续夺控**的受影响对象（JC036 初拥「持续：**你获得受此结附角色的控制权**」）。
    /// 与一次性 `ActionVerb.GainControl`（JC129「直到回合结束」）的区别：本项是**持续效果**——
    /// 来源卡在场且结附期间一直生效，来源离场/解除结附即**归还**。
    /// </summary>
    public enum ControlGainVictim
    {
        None,          // 未声明（不启用；fail-closed：不认识就不生效）
        /// <summary>本卡**所结附的宿主**（JC036：把受结附角色的控制权交给本卡控制者）。</summary>
        AttachedHost,
    }

    /// <summary>【P3·K4】持续控权声明（见 <see cref="ControlGainVictim"/>）。</summary>
    [GlobalClass]
    public partial class ControlGainSpec : RefCounted
    {
        /// <summary>受影响对象；`None` ＝ 不启用。</summary>
        [Export] public ControlGainVictim Victim { get; set; } = ControlGainVictim.None;
    }

    /// <summary>
    /// 【P3·K4】**实例级形态**的目标类别（JC027 花园地精像"成为一个地精角色"、JC028 鲜血奴仆"成为一个…附属"）。
    /// 用户口径 **A32**：形态**只改实例**（`CardCategory` 覆盖），**没有任何效果改变卡的印刷类别**。
    /// </summary>
    public enum FormCategory
    {
        None,                     // 不改类别
        Character,                // 实例类别＝角色（会参与"目标角色"类判定与角色统计）
        Attachment,               // 实例类别＝附属（JC028：**不保留角色类别**）
        CharacterOrAttachment,    // 实例类别＝角色/附属组合（JC027："它仍然是附属"）
    }

    /// <summary>
    /// 【P3·K4】**实例级形态转换声明**（挂在执行转换的那条效果上，由 `ActionVerb.TransformForm` 施加）。
    /// 覆盖项各自可选：类别覆盖 / 卡名覆盖 / 印刷效果失活（改用 `Effects`）/ 印刷图标与防御清零 / 追加子类别。
    /// </summary>
    [GlobalClass]
    public partial class FormChangeSpec : RefCounted
    {
        /// <summary>实例类别覆盖（`None`＝不改；JC027＝`CharacterOrAttachment`，JC028＝`Attachment`）。</summary>
        [Export] public FormCategory Category { get; set; } = FormCategory.None;

        /// <summary>实例卡名覆盖（JC028＝「鲜血」；空＝不改名）。用户口径：目前不影响显示，但**影响卡名判定**。</summary>
        [Export] public string Name { get; set; } = "";

        /// <summary>"**损失了原有的效果**"（JC028）：该卡不再使用模板印刷效果，改用 `Effects` 作为其效果表。</summary>
        [Export] public bool SuppressPrintedEffects { get; set; } = false;

        /// <summary>"**不具有能力图标和防御力**"（JC028）：实例的印刷能力值（含先手值）与防御清零，
        /// 且**属性重算也不会复活**（`ResetToBaseValues` 共同尊重）。</summary>
        [Export] public bool ZeroPrintedStatistics { get; set; } = false;

        /// <summary>形态生效期间**追加**的子类别（JC027＝「地精」）；撤销形态时移除。</summary>
        [Export] public Array<string> AddSubCategories { get; set; } = new Array<string>();

        /// <summary>形态自己的效果表（`SuppressPrintedEffects=true` 时取代模板效果表）。
        /// JC028：「受此结附的角色获得1防御力 和［黑刀］」＝一条 `GrantToHost` 的持续效果。</summary>
        [Export] public Array<EffectData> Effects { get; set; } = new Array<EffectData>();

        /// <summary>
        /// 【2026-09·JC027 用户口径】**形态自带的授予**（能力图标/属性/关键字/子类别）——**幂等**：
        /// 重复施加同一形态**不会叠加**（卡文"成为一个地精角色，且具有［白眼］［黑刀］［黑星］和 1 防御力
        /// 直到回合结束"是一个**状态**，不是可叠加的增益）⇒ 再次发动仍是 1 白眼/1 黑刀/1 势力/1 防御。
        /// 与 `Actions` 里直接写 `Gain` 的区别：那些是"每次结算都累加"的一次性授予，不适用于形态自带属性。
        /// 引擎侧由 `EffectEngine.ApplyFormGrants` 记账（先撤销上一次形态授予，再授予新的），形态到期一并撤销。
        /// </summary>
        [Export] public Array<EffectGrant> Grants { get; set; } = new Array<EffectGrant>();
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
        /// 【2026-09·JC052 用户口径】群体/集合动作里"排除来源卡自身"的**版本敏感**写法：
        /// 卡文的"消灭所有本地区的**其他**角色和暗藏者"里的"其他"＝**与收集该效果时同一张卡**。
        /// 若来源卡在此期间**版本号已变**（如进场触发 A 收集后本卡翻面／重新现身），
        /// 现存的这张"自己"对老效果而言**已不是同一张卡** ⇒ 应当**纳入集合（可被消灭）**。
        ///
        /// 语义（`MassTargetPool.Collect` 消费）：
        ///  · `CanTargetSelf = true` → 本标志无意义（本就不排除自身）；
        ///  · `VersionSkipsSelf = false`（缺省）→ 一律排除来源自身（既有行为，绝大多数"其他角色"效果）；
        ///  · `VersionSkipsSelf = true` → **仅当**"来源卡版本号 == 收集该效果时的版本号"才排除；
        ///    版本号未知（如打出/激活路径未记录）时保守排除（与缺省一致）。
        /// 用途：JC052 终焉的诗寇蒂「进场触发：消灭所有本地区的其他角色和暗藏者」。
        /// </summary>
        [Export] public bool VersionSkipsSelf { get; set; } = false;

        /// <summary>
        /// 【2026-09·JC048 用户口径】**"一条效果、动作按顺序执行"模式**：
        /// `true` 时 `ResolveEffect` **不**按"整条动作序列对每个群体目标各跑一遍"执行，而是
        /// **动作按声明顺序**依次执行：普通动作只执行一次；声明了 `EffectAction.MassInArea` 的动作
        /// 对该群体集合各执行一次；声明了 `EffectAction.RequirePredicate` 的动作在条件不满足时跳过。
        /// 缺省 `false` ＝维持既有群体语义（JC050/JC052/JC011 等不变）。
        /// </summary>
        [Export] public bool MassSequential { get; set; } = false;

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

        /// <summary>
        /// 【秘社批次·卡上标志门槛】目标（或群体集合成员）**其上某标志的数量须 ≥ N**。
        /// 写法 `<标志名>:<N>`（如 MSJC013 计时者②「消灭**所有其上有三个或更多时间标志**的角色和附属」
        /// ＝ `"时间标志:3"`）；空串＝不限。
        /// 消费点：`MassTargetPool.MatchesOne`（群体集合）与 `CardManager.GetLocalZoneTargets` / `HasLegalTarget`
        /// （单目标候选）；单一解析实现在 `MassTargetPool.MeetsMinCounters`。
        /// </summary>
        [Export] public string MinCounters { get; set; } = "";
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
        /// 【2026-10 用户口径·JC092】选择窗口里的**动作提示**（一句话说明"选中该槽后会怎样"，
        /// 如"将你墓地中的角色牌以暗藏者状态置于该地区"）。
        /// 用途：场上点选/地区选择的提示语**由数据给出、不再由 UI 猜**（原先硬编码"将移动至该地区"
        /// ⇒ JC092 的"选择地区"槽显示成了移动提示，与卡文不符）。
        /// 空＝UI 用中性兜底文案（"请点击一个高亮的地区。"）。
        /// </summary>
        [Export] public string Hint { get; set; } = "";

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
        /// <summary>
        /// 【2026-09·JC117】**只允许在指定阶段发动**（多个用 `,` 分隔，空=不限制）——与 `ExcludedPhases` 互补的白名单。
        /// 已登记的阶段名（`PhaseGateRule` 统一判定）：
        ///  · `"行动阶段"` = `TurnPhase.ActionFirst | ActionSecond`（先手/后手行动步骤）；
        ///  · `"对抗阶段"` = `PreCombat | CombatInvestigation | CombatStrength | CombatPower`。
        /// **只作用于"发动/激活"**（`ActivateEligibility` 高亮/候选 + `EffectEngine.ActivateAbility` 权威门禁）；
        /// **不作用于卡牌打出**——打出侧的"只能在行动阶段打出"用 `PlayerCardData.LimitToActionPhaseOnly`
        /// （两者不要混用，避免同一句卡文有两份实现）。
        /// 未登记的阶段名按"不满足"处理（⇒ 该效果不可发动）并告警一次（防拼错静默放开）。
        /// </summary>
        [Export] public string OnlyPhases { get; set; } = "";
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
        /// <summary>
        /// 【A57·次数上限·每局游戏】本能力**每局游戏最多发动次数**（0 = 不限次；
        /// 卡文"该能力**每局游戏只能发动一次**"，如秘社 MSJC001『帷幕守望』行动4）。
        /// 与 K23 `MaxUsesPerTurn` 平行，差别只在**重置时机**：
        ///  · 计数基准 = **每张卡 · 每局 · 每能力**（按 `EffectId` 分别计数，见 `CardInstance.GetAbilityUsesThisGame`）；
        ///  · **不随回合重置**（重置步骤只清 `MaxUsesPerTurn` 的计数）；
        ///  · **不因离场/换区/回手/版本号变动清零**（`ResetToBaseState`/`BumpVersion` 只清回合计数）——
        ///    "每局一次"是整局维度的承诺，不能靠换区刷回来；
        ///  · 只有**重开一局**才恢复（实例按局重建；另在开局处显式 `CardManager.ResetAllAbilityUsesThisGame()` 兜底）。
        /// 消费点与 K23 完全一致（激活 = `ActivateEligibility` 门禁 + `EffectEngine.ActivateAbility` 登记；
        /// 触发 = `TurnManager.ResolveEntry` 的 Trigger 分支），避免出现第二条判定。
        /// </summary>
        [Export] public int MaxUsesPerGame { get; set; } = 0;
        [Export] public bool Forced { get; set; } = false;          // 是否强制（不可跳过）
        /// <summary>
        /// 【K1·动态数值 X】本效果的动态值表达式（空 = 无动态值）。动作参数里用占位符 `$X` 引用（见 `EffectAction.Args`）。
        /// 已登记写法见 `EffectValueResolver`（`事件数量` / `计数:区域:…` / `计数:卡上标志:…` / `计数:地区标志:…` / `玩家选择`）。
        /// **X 求值为 0 时该效果不算发动**（用户 2026-09 口径）：触发不收集、激活不可发动、结算直接跳过；
        /// 表达式未登记或依赖未实现（如"玩家选择"缺数值输入 UI）同样按"不发动"处理（fail-closed）。
        /// </summary>
        [Export] public string ValueExpr { get; set; } = "";

        /// <summary>
        /// 【2026-09·JC048 用户口径】"X 在**结算时**求值，不要用收集时的值卡'是否发动'"：
        /// `false`（缺省）→ 维持既有口径（K1/A39：`X=0` 的**数量型**效果**不算发动**，收集阶段即跳过）；
        /// `true` → **跳过收集阶段的 X=0 门禁**，X 留到结算时求值（结算时若仍为 0，则自然什么也不做）。
        ///
        /// 用途：JC048 诅咒沙漏②「该地区每有一个时间标志就对地区内所有角色造成 1 点伤害」——
        /// 它与①「在诅咒沙漏上放一个时间标志」是**两条同时间的效果**：收集发生在**放标志之前**，
        /// 于是首个准备步骤收集时 X=0 被整条跳过 ⇒ 表现成"**只有牺牲那一回合才造成伤害**"
        /// （用户实测："JC048 效果给自己放置标记，每次放置都要造成伤害，不是只有 JC048 牺牲的时候造成伤害"）。
        /// </summary>
        [Export] public bool ValueAtResolve { get; set; } = false;
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
        /// <summary>
        /// 【2026-09 修复·用户实测 JC059】`AuraFilterExcludeDomain` 的"**未声明**"哨兵
        /// （＝不做领域筛选）。取 -1 是沿用全仓"印刷无魔法领域"的同一约定（见 `CardInstance.RebuildDomains`
        /// 跳过 `-1`）。
        ///
        /// 为什么需要它：该字段**原先默认 `MagicDomain.Mind`**，而枚举里 **Mind == 0**（**没有 `None`**），
        /// 于是"没写领域子句"与"显式排除心灵领域"无法区分 ⇒ **每一张光环卡都静默排除了心灵领域角色**。
        /// 用户实测：JC059（"位于本地区的所有其他本方角色 +1 防御力"）对 JC066 不生效——JC066 的
        /// `MagicDomains=[0]`＝心灵领域，被这条**隐式**排除挡掉了。全仓只有 JC060 显式写过该字段，
        /// 而它同时设了优先级更高的 `AuraRequiresNoDomain=true`（"未印刷任何魔法领域"），
        /// 故"排除某领域"这条分支实际**无人消费** ⇒ 改为**显式声明才生效**。
        /// </summary>
        public const int AuraNoDomainFilter = -1;

        /// <summary>光环型持续：源卡对自身授予 ContinuousGrants 的同时，对其控制者争夺区内其他符合条件的角色也授予同样的 Grants。
        /// 【2026-09 修复】领域排除改为**显式声明才生效**（见 `AuraNoDomainFilter`）：未声明＝不筛领域；
        /// 显式声明某领域＝排除"实例 MagicDomains 已含该域"的角色（判定见 `EffectEngine.IsAuraTargetEligible`）。
        /// 例：JC060 用 `AuraRequiresNoDomain=true`（"所有**未印刷**魔法领域图标的本方角色"）——后者优先级更高。</summary>
        [Export] public MagicDomain AuraFilterExcludeDomain { get; set; } = (MagicDomain)AuraNoDomainFilter;
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

        /// <summary>【P3·K5】激活代价：自本方卡牌上移除 N 个（时间）标志（JC044；见 `CounterCostSpec`）。</summary>
        [Export] public CounterCostSpec CounterCost { get; set; } = null;

        /// <summary>
        /// 【秘社批次】激活代价：**从某区域取 N 张牌**（并对其执行一个代价动作）——见 `CardCostSpec`。
        /// 用户 2026-09 口径。覆盖 MSJC010②③（封印区的牌→牌库底）/ MSJC016②（墓地七张→封印在颂亡者上）/
        /// MSJC012① ②（横置三个本方奴仆·吸血鬼角色）。
        /// </summary>
        [Export] public CardCostSpec CardCost { get; set; } = null;

        /// <summary>
        /// 【K11·触发版（2026-09 用户口径 JC061）】**触发类效果的"可选固定费用"**（0＝未声明）。
        ///
        /// 语义：该效果**入堆叠之前**问一次"**是否支付 N**"（**只有是/否两个选项，不需要输入金额**）：
        ///  · 是 → 扣 N 点费用（`PlayerManager.SpendMana`）后照常入堆叠、照常结算；
        ///  · 否 → **该触发不入堆叠**（整条效果不结算，也不扣费）。
        ///
        /// 与 `SacrificeCost` / `CounterCost` / `CardCost`（激活能力的**发动代价**）同类：都是
        /// "**入堆叠之前付清、不在堆叠里结算、不可被响应**"；区别只是本项**可选且金额固定**（那三项是
        /// "候选选择"类代价）。放弃整条效果由更上层的**触发排序窗**负责（`EffectData.Forced`），
        /// 本费用窗**不提供"取消"**。
        ///
        /// 消费点（单一收口）：`TurnManager.EnsureOptionalPayThenPush` / `...Async` —— 两个方法都
        /// **紧邻 `PushTriggerToStack` 调用**，即"**先选完目标、再问支付**"：联机选目标阶段取消时不会白扣费。
        /// 费用不足 → **不弹窗**、直接按"不支付"处理（该触发不入堆叠；绝不替玩家花钱）。
        /// 单机 UI ＝ `IGameUIHooks.PromptOptionalPay`（无实现时 fail-closed＝不支付）；
        /// 联机 ＝ `ChoiceRequest.Kind.ForceChoice` 两按钮列表（天然不带"取消"按钮）。
        /// </summary>
        [Export] public int OptionalPayCost { get; set; } = 0;

        /// <summary>
        /// 【2026-10·JC082 用户口径】**入堆叠前的"是/否"确认**（非支付型"你可以…"）：
        /// 非空 ⇒ 触发在入堆叠前先问来源控制者（两个按钮、**不出现输入框**；文案＝本字符串）；
        /// **选"否"/取消 ⇒ 该触发不入堆叠**（不产生任何堆叠条目、不结算）。
        ///
        /// 与 `OptionalPayCost` 的分工：后者问的是"是否**支付 N**"（要花钱）；本字段问的是
        /// 纯意愿（如 JC082 收藏"本回合中，当你赢得下一次调查对抗时，你可以抓一张牌"⇒"是否抓一张牌？"）。
        /// 两者同时声明时：先问支付（付不起直接放弃），再问本确认。
        /// 单机＝`IGameUIHooks.PromptTwoChoice`；联机＝`ChoiceRequest.Kind.ForceChoice`（是/否两按钮）。
        /// 无 UI/无网关 ⇒ 按"否"处理（fail-closed，绝不替玩家做选择）。
        /// </summary>
        [Export] public string ConfirmPrompt { get; set; } = "";

        /// <summary>
        /// 【秘社批次】**秘社在场期间的静态子类别授予**（用户 2026-09 口径，见 `CouncilStaticGrant`）：
        /// 非空时按该规则对本控制者争夺区内符合条件的卡额外授予子类别（MSJC012"费用≤2 的本方人类获得奴仆类别"）。
        /// 由装配层 `GameInitializer` 的 `OnPostMove` 钩子调用 `EffectEngine.ReevaluateCouncilStaticGrants` 重算。
        /// </summary>
        [Export] public CouncilStaticGrant CouncilStatic { get; set; } = null;

        /// <summary>
        /// 【P3·K1】`ValueExpr="玩家选择"` 时**玩家可输入的数字范围**（含端点；缺省 1～9）。
        /// 用途：JC083「选择一个数字」（用户口径 **A31**：范围 **0～20**）。
        /// 语义要点：`ValueMin=0` 时 **X=0 是合法选择**（该卡文里 X 是"猜测值"而非"数量"，
        /// 故不适用 K1 的"X=0 ⇒ 效果不发动"那条规则——否则无法猜"印刷费用为 0"）。
        /// </summary>
        [Export] public int ValueMin { get; set; } = 1;

        /// <summary>【P3·K1】`ValueExpr="玩家选择"` 的数字上限（含；缺省 9）。</summary>
        [Export] public int ValueMax { get; set; } = 9;

        /// <summary>
        /// 【P3·K3】**持续型伤害重定向**：本卡在场期间，把符合条件的目标"将要受到的伤害"**改为由本卡承受**
        /// （JC077 庇佑圣灵 / JC121 空鸣御守；用户 2026-09 口径：这两张卡是**持续效果**）。
        /// 消费点＝`CardManager.DealDamageToInstance`（伤害唯一收口）经 `EffectEngine.ResolveDamageRedirect`
        /// 查询；被重定向后**不再二次重定向**（防乒乓），但承受方自身的"防止"（灵体/护盾）照常生效。
        /// </summary>
        [Export] public DamageRedirectSpec DamageRedirect { get; set; } = null;

        /// <summary>
        /// 【P3·K4】**持续夺控**（JC036 初拥「持续：你获得受此结附角色的控制权，将该角色的人类类别改为
        /// 吸血鬼类别」）：本卡在场且结附期间，**宿主**的控制者变为本卡控制者（`ZoneManager.ChangeController`
        /// 统一出口，只改 `ControllerId`、拥有者不变、卡不移动地区）。
        /// 生命周期：登记时夺控、注销（来源离场/解除结附/前提丧失）时**归还给拥有者**（卡面 FAQ 原文
        /// "该角色的操控权交还给其拥有者"）。
        /// 用户 2026-09 口径（A16）：**没有写"直到回合结束"的持续效果就持续尽可能久**；
        /// 版本号变化一般伴随初拥离场、被初拥角色离场会让附属的初拥一同进墓地，故**不另设版本号判据**，
        /// 一律由持续效果自身的登记/注销生命周期承担。
        /// </summary>
        [Export] public ControlGainSpec ControlGain { get; set; } = null;

        /// <summary>
        /// 【P3·K4】**实例级形态转换**（JC027 花园地精像"成为一个地精角色" + 白眼/黑刀/黑星/1防御直到回合结束；
        /// JC028 鲜血奴仆"成为一个名为**鲜血**的附属并结附于目标吸血鬼角色"）。
        /// 由 `ActionVerb.TransformForm` 施加到 `CardInstance`（**只改实例**，见 `FormChangeSpec` / 口径 A32）；
        /// 期限含"回合结束"时登记时效项，到期由 `ExpireTurnEndEffects` 撤销形态并还原。
        /// </summary>
        [Export] public FormChangeSpec FormChange { get; set; } = null;

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
        /// 【2026-10·提示文字】**效果级**的选择窗提示（一句话说明"选中后会怎样"，作为括号补充展示）。
        /// 与 `TargetSlot.Hint` 的分工：多目标槽卡（JC092/JC133/JC021）用**槽级**提示；
        /// 无槽位的"取地区目标"流程（如机动 `Maneuver()`、JC117 移动）用**效果级**提示。
        /// 二者皆空 ⇒ UI 用中性兜底文案（"请点击一个高亮的地区。"）。
        /// 消费点＝`TriggerInteractionCoordinator`（单机触发/激活取目标窗）＋ `ChoiceRequest.SlotHint`（联机）。
        /// </summary>
        [Export] public string Hint { get; set; } = "";

        /// <summary>
        /// 【P3·K21·JC041】**发动前提：来源卡必须处于"暗藏者"状态（背面朝上）**。
        /// 卡文「**快速暗藏者行动2**：令禁婆由暗藏者状态现身。你只能在其位于水域地区时发动此能力。」
        /// 用户口径（A29 追问答复）：这是**暗藏者专用**的激活能力——"**区别于其他效果大多正面在场才能生效，
        /// 这个效果只有作为暗藏者时可以激活**"。消费点＝`ActivateEligibility`（候选/可发动）＋
        /// `EffectEngine.ActivateAbility`（权威门禁）。
        /// </summary>
        [Export] public bool RequireSourceCovert { get; set; } = false;

        /// <summary>
        /// 【P3·K21·JC041】**发动前提：来源卡所在地区须具有指定地区子类别**（空＝不限；本卡＝`水域`）。
        /// 判定经 `AreaManager.AreaHasSubCategory`（读地区卡模板 `AreaCardData.SubCategories`）。
        /// </summary>
        [Export] public string RequireSourceAreaSubCategory { get; set; } = "";

        /// <summary>
        /// 【2026-10 用户口径·威名/声望】**触发前提：来源卡必须【未横置】**（横置的角色不提供其词条）。
        /// 本卡文依据：声望卡文"若你比对手操控更多**未横置**的声望角色"（JC070/JC011/JC067/JC076/JC080/JC095），
        /// 以及项目既有口径"横置的卡不提供任何能力"（`AreaStatsCalculator`）。
        /// 消费点＝`TriggerRegistry` 收集阶段（横置 ⇒ 该条**不收集**，不进触发排序窗；与暗藏模式门禁同层）。
        /// 由 `KeywordEffects.WeiMing()` / `Renown()` 声明（两词条同办）。
        /// </summary>
        [Export] public bool RequireSourceUntapped { get; set; } = false;

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

        // ---- 【JC124·构筑级全局触发】（"只要这张牌**一开始加入玩家卡组**就能触发"） ----
        /// <summary>
        /// 【JC124】本效果是**构筑级（卡组级）全局触发**：**只要该卡在开局时位于某玩家的卡组内**，
        /// 该玩家就登记一次本效果，此后**无论这张牌本身身处何处（牌库/手牌/场上/墓地）都照样生效**
        /// （卡文"该能力在尼古拉斯·勒梅离场后仍然生效"）。
        ///
        /// 与普通触发的两点区别（也是本字段存在的理由）：
        ///  · **收集方式**：不走"扫描场上卡实例"的常规触发收集（否则本卡在场时会与构筑级登记**重复收集**），
        ///    改由 `Script/Manager/DeckLevelTriggerRule` 在事件发生时按"已登记玩家"合成触发
        ///    （登记点＝`DeckManager` 装载卡组时；**同一玩家加入多张同名牌也只登记一次**）；
        ///  · **来源卡**：堆叠条目的来源＝该玩家卡组里的那张实例（因此**堆叠上显示的是本卡的卡图**）。
        /// </summary>
        [Export] public bool DeckLevelRegistration { get; set; } = false;

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
