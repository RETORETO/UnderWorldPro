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

    /// <summary>效果时机/触发源。区分"持续常驻（B组）"与"进场后引发的结算（C/F组）"。</summary>
    public enum EffectTiming
    {
        Continuous,            // 持续效果（B组）：入注册表，按位置实时增删，不入堆叠
        Static,                // 静止/禁止效果（B组衍生，D19-⑤）：常驻规则约束（如不能被装备结附），置位 StaticFlags，不入堆叠、不授出能力
        EnterTrigger,          // 进场触发（C组）
        UncoverTrigger,        // 现身触发（C组）
        CharacterActivated,    // 角色激活能力（C组）
        AttachmentActivated,   // 附属激活能力（C组）
        ManeuverTrigger,       // 机动触发（C组，玩家可选，非强制）
        SpellCast,             // 事务卡打出结算（F组）：打出即结算，完即入墓地
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
        /// 【待接】触点：EffectEngine.Draw（抽牌动作原语）执行后调用 GameEventBus.Emit(OnDraw, ctx)。
        /// 区别于 OnCardPlayed（打牌）：抓牌是被动作，打牌是主动打出。</summary>
        OnDraw,

        /// <summary>触发：当卡牌被弃置（从手牌进入墓地/弃牌堆）时广播。对应 GameEvent.OnDiscard。
        /// 覆盖卡文："手牌触发""当你弃牌时"（全卡"手牌触发" ~5 对样例）。典型如弃牌时触发效果、弃牌视为某种代价结算。
        /// 【待接】触点：手牌弃牌结算处（HandManager/EffectEngine.Discard）调用 GameEventBus.Emit(OnDiscard, ctx)。
        /// 区别于 OnCardDied（场上死亡）：此处特指手牌区弃置。</summary>
        OnDiscard,

        /// <summary>触发：当指示物（Token/Marker）被放置到某卡/区域时广播。对应 GameEvent.OnMarkerPlaced。
        /// 覆盖卡文："当指示物被放置""放置触发"（全卡"指示物" ~61 处、"放置" ~286 处）。典型如每放置 1 指示物触发结算。
        /// 【待接】触点：指示物系统放置入口（E4-#1 放置原语）调用 GameEventBus.Emit(OnMarkerPlaced, ctx)。
        /// 需注意：高频放置场景必须配合 D-1 熔断/自指循环复核，避免"放置触发→再放置"死循环。</summary>
        OnMarkerPlaced,

        /// <summary>触发：当一张卡被封印（Seal）时广播。对应 GameEvent.OnSealed。
        /// 覆盖卡文："当卡牌被封印""封印触发"（全卡"封印" ~149 处，极高）。典型如被封印时触发、封印作为条件。
        /// 【待接】触点：封印系统（D15）施加封印处调用 GameEventBus.Emit(OnSealed, ctx)。
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

        /// <summary>触发：战斗对抗（Battle 类型）获胜时广播。对应 GameEvent.CombatWon。
        /// 触发语义：CombatManager.ResolveWinner 结算战斗对抗分出胜者后，经 TriggerEmitPoints.CombatWon 广播；
        /// 由威名类效果（JC018 等）响应，条件 WinnerIsSelf 判定"本卡控制者是否为对抗获胜方"。
        /// 放置逻辑参考 JC035 的 PlaceToken 势力标记落点（ZoneDetailId）。
        /// 【后续拓展·B 预留】调查对抗获胜 → OnInvestigationWon；势力对抗(Power)获胜 → OnFactionWon；
        /// 二者复用同一 WinnerIsSelf 条件与 TriggerEffectInstance.WinnerId 透传链路，仅需新增枚举 + EventToTiming 映射 + TriggerEmitPoints.Emit 方法。</summary>
        OnCombatWon,

        /// <summary>议会（秘社）激活能力（C组衍生）。对应玩家双击秘社 UI 弹出激活窗口、选择某激活选项后发动。
        /// 与 CharacterActivated/AttachmentActivated 并列，但来源是 CouncilZone 中的秘社实例；非快速行动、需门槛判定、进堆叠。
        /// 触点：CouncilCardUI 双击 → PlayerActionService 激活入口（门槛判定 + 进堆叠 + TargetRules 选目标）。
        /// 注意：秘社卡可含多个激活选项（ActivationOptions），每个选项视为一条独立的 CouncilActivated 效果流。</summary>
        CouncilActivated,
    }

    /// <summary>打出时机（仅事务卡/快速行动用，描述"何时允许打出"）。</summary>
    public enum PlayTiming
    {
        MainPhase,     // 主阶段/专属步骤打出
        Response,      // 堆叠开放时作为响应插队打出（快速行动默认）
        NonResponsive, // 卡文注明"不能以响应打出"
    }

    /// <summary>效果生效位置（ZoneScope，声明式生效区）。
    /// 方案C（声明式 Scope）默认推导见 EffectEngine.GetDefaultScope/IsEffectActiveInZone。
    /// 每个 EffectData 可声明其生效区；Specified=未声明，回退到卡类型默认区。
    /// 未来扩展（如"墓地/卡组生效"）在此枚举补值并在 GetDefaultScope/IsEffectActiveInZone 加分支即可。</summary>
    public enum ZoneScope
    {
        ContestZone,   // 争夺区（角色默认生效位置）
        HandZone,      // 手牌区（如"公开"关键字仅约束手牌派遣暗藏）
        AttachTarget,  // 结附于合法目标时（附属默认；严格规则下=仅 AttachedZone）
        AssetZone,     // 资产区
        Graveyard,     // 墓地（未来扩展：墓地生效效果）
        DeckZone,      // 卡组（未来扩展：卡组生效效果）
        Specified,     // 由卡文指定区域（未声明→回退卡类型默认）
    }

    /// <summary>静止/禁止效果种类（D19-⑤ 打通：让"不能被装备结附"等静止效果可被系统识别与校验）。
    /// 与持续关键字授予（公开）不同——静止效果是规则约束，常驻、不授出能力，挂在 CardInstance.StaticFlags。
    /// 采用通用枚举而非单布尔，以便未来"不能被锁定/不能成为目标/不能被横置"等零成本扩展。</summary>
    public enum StaticKind
    {
        CannotBeAttached,   // 不能被装备/附属结附（如 JC001 通灵猫头鹰）
        BlockDeployCovert,  // 全局封锁：当本卡未横置时，控制者的对手不能进行秘密派遣行动（JC068 阿格斯IV）
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
    }

    /// <summary>持续效果前提种类。</summary>
    public enum PrereqKind
    {
        None,
        DomainLoyalty,   // 控制者在某区域的某领域忠诚≥ Amount
        SelfUntapped,    // 来源卡自身未横置（JC068：只要阿格斯IV未横置）
    }

    // ==================== 数据节点 ====================

    /// <summary>目标选择描述（D16 目标校验层使用）。
    /// 继承 RefCounted：见 EffectAction 说明（引用计数，避免 GC 误回收）。</summary>
    [GlobalClass]
    public partial class TargetSpec : RefCounted, Script.Data.Effects.ITargetDesc
    {
        /// <summary>目标种类（复用 TargetKind）。</summary>
        [Export] public TargetKind Kind { get; set; } = TargetKind.None;

        /// <summary>目标过滤条件文本（如"已横置""具魔法领域图标""结附于角色"）。</summary>
        [Export] public string Filter { get; set; } = "";

        /// <summary>是否需要玩家选择目标。JC011（横置所有敌方角色）等不需取对象时为 false。</summary>
        [Export] public bool NeedsSelection { get; set; } = true;

        /// <summary>是否允许以来源自身（施法者）为目标。默认 false（触发效果一般不能打自己）。
        /// "袭击"类关键词（KeywordEffects.Strike）设为 true：巨型蝙蝠现身袭击可指定自身为目标。
        /// 注意：GetLocalZoneTargets 在收集候选时据此决定是否排除 t.Source。</summary>
        [Export] public bool CanTargetSelf { get; set; } = false;

        /// <summary>目标丢失处置模式（2026-08-07）。默认 Counter=丢失即反击/不结算；
        /// 预留 Optional 给未来多目标（丢几个都行）。单目标卡/效果保持默认即原行为。</summary>
        [Export] public TargetLossMode LossMode { get; set; } = TargetLossMode.Counter;

        /// <summary>是否限定"本地区"：目标须与来源同 AREASLOT（规则⑥）。默认 false。</summary>
        [Export] public bool IsLocalZone { get; set; } = false;

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
    }

    /// <summary>单条持续授予（B组专用，持续效果"赋予什么"的结构化表达）。
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
        [Export] public string ActivateCondition { get; set; } = ""; // 发动许可（横置本角色+快速行动2…）
        [Export] public int Cost { get; set; } = 0;                 // 费用数字（快速行动N 中的 N）
        [Export] public bool Forced { get; set; } = false;          // 是否强制（不可跳过）
        [Export] public CovertTriggerMode TriggerCovertMode { get; set; } = CovertTriggerMode.FaceUp; // 触发对暗藏状态的要求（默认仅正面朝上可触发）

        // ---- 持续专用（B组） ----
        // 默认 Specified：由 IsEffectActiveInZone 走 GetDefaultScope（附属→AttachTarget，角色→ContestZone），
        // 实现"附属默认在附属区生效、角色默认在争夺区生效"。此前默认 ContestZone 会让未声明 Scope 的
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

        // ---- 静止/禁止专用（D19-⑤） ----
        [Export] public Array<StaticEffect> StaticEffects { get; set; } = new Array<StaticEffect>(); // 静止/禁止效果（不能被装备结附等）

        // ---- 结算要素 ----
        [Export] public TargetSpec Target { get; set; } = new TargetSpec();
        [Export] public EffectCondition Condition { get; set; } = new EffectCondition();
        [Export] public Array<EffectAction> Actions { get; set; } = new Array<EffectAction>();
        [Export] public string Duration { get; set; } = "瞬时"; // 常驻/瞬时/直到回合结束

        // ---- 备注 ----
        [Export] public string FaqNote { get; set; } = "";
    }
}
