using Godot;
using Godot.Collections;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Data.Cards
{
    /// <summary>
    /// 玩家卡牌数据类（PlayerCardData）
    /// 继承自基础卡牌数据（BaseCardData），用于定义玩家可使用的卡牌的所有静态属性。
    /// </summary>
    [GlobalClass]
    public partial class PlayerCardData : BaseCardData
    {
        // ==================== 基本标识与费用 ====================

        /// <summary>子卡名称（SubCardName）—— 用于表示同一主卡下的变体或子类型</summary>
        [Export] public string SubCardName { get; set; }

        /// <summary>唯一标识字段（UniqueField）—— 卡牌在数据库或系统中的唯一键（如UID或特定代码）</summary>
        [Export] public string UniqueField { get; set; }

        /// <summary>打出费用（Cost），-1 表示该卡不可直接打出（例如需要特殊条件）</summary>
        [Export] public int Cost { get; set; } = -1;

        // ==================== 忠诚度需求（颜色与领域） ====================

        /// <summary>颜色忠诚度需求列表（ColorLoyaltyRequirements）—— 需要拥有的各颜色忠诚度点数</summary>
        [Export] public Array<ColorLoyaltyRequirement> ColorLoyaltyRequirements { get; set; } = new Array<ColorLoyaltyRequirement>();

        /// <summary>领域忠诚度需求列表（DomainLoyaltyRequirements）—— 需要拥有的各魔法领域忠诚度点数</summary>
        [Export] public Array<DomainLoyaltyRequirement> DomainLoyaltyRequirements { get; set; } = new Array<DomainLoyaltyRequirement>();

        // ==================== 分类与颜色/领域归属 ====================

        /// <summary>卡牌主类别标志（CategoryFlag）—— 使用位掩码表示卡牌类型（角色事务附属任务）</summary>
        [Export] public CardCategory CategoryFlag { get; set; }

        /// <summary>子类别列表（SubCategories）—— 用于更细粒度的分类（如"人类"、"士兵"等）</summary>
        [Export] public Array<string> SubCategories { get; set; } = new Array<string>();

        /// <summary>卡牌所属颜色列表（Colors）—— 默认棕色（中立），可包含多种颜色</summary>
        [Export] public Array<CardColor> Colors { get; set; } = new Array<CardColor> { CardColor.Brown };

        /// <summary>卡牌所属魔法领域列表（MagicDomains）—— 如神圣，心灵等</summary>
        [Export] public Array<MagicDomain> MagicDomains { get; set; } = new Array<MagicDomain>();

        /// <summary>组织/阵营（Organization）—— 卡牌所属的势力或团体</summary>
        [Export] public string Organization { get; set; }

        // ==================== 战斗与调查属性 ====================

        /// <summary>基础调查值（Investigation）—— 用于调查类行动的能力值</summary>
        [Export] public int Investigation { get; set; }

        /// <summary>基础战斗值（Combat）—— 用于战斗类行动的能力值</summary>
        [Export] public int Combat { get; set; }

        /// <summary>基础力量值（Power）—— 代表影响力</summary>
        [Export] public int Power { get; set; }

        /// <summary>先攻调查加成（FirstStrikeInvestigation）—— 在先攻时额外增加的调查值</summary>
        [Export] public int FirstStrikeInvestigation { get; set; }

        /// <summary>先攻战斗加成（FirstStrikeCombat）—— 在先攻时额外增加的战斗值</summary>
        [Export] public int FirstStrikeCombat { get; set; }

        /// <summary>先攻力量加成（FirstStrikePower）—— 在先攻时额外增加的影响力</summary>
        [Export] public int FirstStrikePower { get; set; }

        /// <summary>防御值（Defense），-1 表示无防御能力（即不能阻挡或承受伤害）</summary>
        [Export] public int Defense { get; set; } = -1;

        // ==================== 额外费用与效果 ====================

        /// <summary>额外费用描述（ExtraCostDescription）—— 打出此卡可能需要支付额外代价的文字说明</summary>
        [Export] public string ExtraCostDescription { get; set; }

        /// <summary>额外费用是否强制（IsExtraCostMandatory）—— true 表示必须支付额外费用才能打出</summary>
        [Export] public bool IsExtraCostMandatory { get; set; }

        /// <summary>效果列表（Effects）—— 卡牌产生的效果ID或描述字符串列表</summary>
        [Export] public Array<string> Effects { get; set; } = new Array<string>();

        /// <summary>通用效果数据列表（EffectDataList）—— 新效果系统的统一结构化载体，
        /// 覆盖持续效果（B组）/ 角色·附属触发与激活（C组）/ 事务卡打出结算（F组）。
        /// 由卡片效果（旧：C# 构造函数；新：CardEffectFactory.EnsureEffects）填充，
        /// 不序列化进 .tres（EffectData 是 RefCounted 而非 Resource），
        /// 因此不使用 [Export]，避免 Godot 导出数组要求元素可序列化而导致加载异常。</summary>
        public Array<Script.Data.Effects.EffectData> EffectDataList { get; set; }
            = new Array<Script.Data.Effects.EffectData>();

        /// <summary>效果注入标记（内部）：由 CardEffectFactory.EnsureEffects 置位，
        /// 保证共享 .tres 模板只填充一次效果，避免重复 Clear/Add 污染。</summary>
        internal bool _effectsInjected = false;

        /// <summary>快速打出门禁档位配置（D9 卡级）：
        ///  - false（默认）：事务卡默认取 A档（有门禁，先手限ActionFirst/后手限ActionSecond，堆叠必须空）；
        ///  - true           ：事务卡取 B档（限行动阶段不分先后手，不需堆叠空，可在响应窗口打出）。
        /// 角色/附属永不读此字段，恒为 A档（有门禁）。
        /// 含 PlayTiming.Response 效果的事务卡直接取 C档（取消门禁，任意有优先权时打出），忽略本开关。</summary>
        [Export] public bool LimitToActionPhaseOnly { get; set; } = false;

        /// <summary>事务卡是否含"可响应打出"效果（PlayTiming.Response）。
        /// 仅事务卡有意义；命中则返回 true（C档：取消门禁，可响应窗口打出）。
        /// 角色/附属永不调用此方法进入 C档。</summary>
        public bool CanPlayAsResponse()
        {
            if (EffectDataList == null) return false;
            foreach (var e in EffectDataList)
                if (e != null && e.PlayTiming == PlayTiming.Response) return true;
            return false;
        }

        /// <summary>
        /// Godot 反序列化后 / C# 构造函数链执行后，确保 EffectDataList 始终非空。
        /// 某些子类化资源的加载路径会先设 null 再回填字段，防御性兜底避免运行时 NRE。
        /// </summary>
        public PlayerCardData()
        {
            EffectDataList ??= new Array<Script.Data.Effects.EffectData>();
        }

        /// <summary>
        /// 查询该卡模板（固有文本能力）是否声明了某关键字——不依赖运行时持续效果注册时机。
        /// 用于"公开"等手牌期即应生效的固有约束（持续效果 RegisterContinuous 仅进场调用，
        /// 手牌期 HasKeyword 为 false，故此处直接从 EffectDataList 的 Continuous 授予中判定）。
        /// </summary>
        public bool HasInherentKeyword(string kw)
        {
            if (EffectDataList == null || string.IsNullOrEmpty(kw)) return false;
            foreach (var e in EffectDataList)
            {
                if (e == null || e.Timing != Script.Data.Effects.EffectTiming.Continuous) continue;
                if (e.ContinuousGrants == null) continue;
                foreach (var g in e.ContinuousGrants)
                {
                    if (g != null && g.Kind == "Keyword" && g.Value == kw) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 语义化封装：该卡模板是否声明了"公开"关键字（固有文本能力）。
        /// 直接代理 HasInherentKeyword("公开") —— 从效果数据结构层判定，而非从牌面文本判断。
        /// 用途：手牌派遣暗藏校验时判断"这张卡有没有公开效果"（效果失效/授予被移除时自然为 false）。
        /// 注意：本方法基于模板（卡定义）的固有持续授予；运行期若需判断"当前实例此刻是否实际处于公开约束"，
        /// 应查询持续效果注册表（ContinuousRegistry）对该实例的实际授予，而非本模板方法。
        /// </summary>
        public bool HasPublicKeyword()
        {
            return HasInherentKeyword("公开");
        }

        /// <summary>关联卡牌唯一标识（RelatedCardUID）—— 用于指向另一张卡牌（转变卡等）</summary>
        [Export] public string RelatedCardUID { get; set; }

        // ==================== 打出目标元数据（初期粗筛，未来由效果精确化） ====================

        /// <summary>打出时可选择的目标种类集合（旧粗筛字段，保留兼容）。空=按 CategoryFlag 类别默认推导。</summary>
        [Export]
        public Godot.Collections.Array<TargetKind> AllowedTargets { get; set; } = new Godot.Collections.Array<TargetKind>();

        /// <summary>打出时是否必须选择目标。Default=按类别默认（附属=Required、事务按是否含目标、角色=None）。</summary>
        [Export]
        public TargetRequirement TargetRequirement { get; set; } = TargetRequirement.Default;

        // ==================== 数据驱动目标规则（方案 A 主入口） ====================
        // TargetRules：声明式目标规则列表，tres Inspector 可直接配置，cs 也可构造。
        // 每条规则 = 一个粗筛大类(TargetKind) + 0..N 个精筛条件(TargetRestriction, AND)。
        // 合法目标 = 满足【任一】规则即可（OR）。evaluator 见 CardManager.IsLegalPlayTarget / MatchesRule。
        //
        // ───── 已确认的全部目标种类 → TargetRule 配置对照（后续新增只需加枚举+case，不改单卡）─────
        // ① 本方秘社（出现最多）      : TargetRule{ Kind=Council,   Restrictions=[Friendly] }
        // ② 目标角色                  : TargetRule{ Kind=Character }
        // ③ 目标本方角色              : TargetRule{ Kind=Character, Restrictions=[Friendly] }
        // ④ 目标敌方角色              : TargetRule{ Kind=Character, Restrictions=[Enemy] }
        // ⑤ 目标地区                  : TargetRule{ Kind=Area }   （地区非卡实例，走 TurnManager.PlayCard 的 Area 路径，
        //                                                         若需"城市地区"用下方 ⑥；城市子类别读 AreaCardData.SubCategories）
        // ⑥ 目标城市地区              : TargetRule{ Kind=Area,      Restrictions=[CurrentSubCategory], Param="城市" }
        // ⑦ 目标非核心角色            : TargetRule{ Kind=Character }（暂等同所有角色，无需额外条件）
        // ⑧ 目标本方暗藏者            : TargetRule{ Kind=Covert,    Restrictions=[Friendly] }
        // ⑨ 目标人类角色              : TargetRule{ Kind=Character, Restrictions=[CurrentSubCategory], Param="人类" }
        // ⑩ 目标人类或吸血鬼角色      : 两条规则 OR：
        //        TargetRule{ Kind=Character, Restrictions=[CurrentSubCategory], Param="人类" }
        //      + TargetRule{ Kind=Character, Restrictions=[CurrentSubCategory], Param="吸血鬼" }
        // ⑪ 目标吸血鬼角色            : TargetRule{ Kind=Character, Restrictions=[CurrentSubCategory], Param="吸血鬼" }
        // ⑫ 目标本方人类角色          : TargetRule{ Kind=Character, Restrictions=[Friendly, CurrentSubCategory], Param="人类" }
        // ⑬ 目标印刷类别为人类的角色  : TargetRule{ Kind=Character, Restrictions=[StaticSubCategory],  Param="人类" }
        // ⑭ 目标副名为"圣甲虫产业"的本方附属 : 两条规则 AND 于同一 rule：
        //        TargetRule{ Kind=Attachment, Restrictions=[Friendly, SubCardName], Param="圣甲虫产业" }
        // ⑮ （扩展位）目标名为某卡的附属 : TargetRule{ Kind=Attachment, Restrictions=[CardName], Param="卡名" }
        // ⑯ 目标非人类且印刷费用≥3的敌方角色（JC015 驱魔人）: 一条规则 AND 三个限制，
        //    NotStaticSubCategory 与 CostAtLeast 共用 rule.Param，按 '|' 分段（第0段=子类别，第1段=费用门槛）：
        //        TargetRule{ Kind=Character,
        //                    Restrictions=[Enemy, NotStaticSubCategory, CostAtLeast],
        //                    Param="人类|3" }   // NotStaticSubCategory→"人类"；CostAtLeast→"3"
        //    （注：JC015 自身 SubCategories=["人类","僧侣"]，但来源是牺牲者非目标，不影响；目标须为非人类敌方且费用≥3）
        //
        // 注：Friendly/Enemy 的"本方"以打出者/激活者 ControllerId 为准（见 MatchesRule 取 sourceController）。
        [Export]
        public Godot.Collections.Array<Script.Data.Effects.TargetRule> TargetRules { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.TargetRule>();

        // ==================== 逻辑方法 ====================

        /// <summary>
        /// 取得该卡打出时允许的目标【大类】集合（供需要大类列表的调用方，如 UI 高亮）。
        /// 优先级：TargetRules(方案A) > 单卡 AllowedTargets 覆盖 > EffectData.Target 聚合 > 类别默认。
        /// 精确合法性仍由 CardManager.IsLegalPlayTarget 按 TargetRules 逐条 MatchesRule 裁决。
        /// </summary>
        public System.Collections.Generic.List<TargetKind> GetAllowedTargets()
        {
            // 0. 方案 A：从 TargetRules 聚合大类（去重）
            if (TargetRules != null && TargetRules.Count > 0)
            {
                var fromRules = new System.Collections.Generic.List<TargetKind>();
                foreach (var r in TargetRules)
                    if (r != null && r.Kind != TargetKind.None && !fromRules.Contains(r.Kind))
                        fromRules.Add(r.Kind);
                if (fromRules.Count > 0) return fromRules;
            }

            // 1. 单卡显式覆盖优先
            if (AllowedTargets != null && AllowedTargets.Count > 0)
                return new System.Collections.Generic.List<TargetKind>(AllowedTargets);

            // 2. 从 EffectDataList 聚合所有 Target.Kind（事务卡 F 组 / 角色·附属激活 C 组统一从此读取）
            var fromEffects = new System.Collections.Generic.List<TargetKind>();
            if (EffectDataList != null)
            {
                foreach (var e in EffectDataList)
                {
                    var t = e?.Target;
                    if (t == null || t.Kind == TargetKind.None) continue;
                    if (!fromEffects.Contains(t.Kind)) fromEffects.Add(t.Kind);
                }
            }
            if (fromEffects.Count > 0) return fromEffects;

            // 3. 类别默认推导
            var list = new System.Collections.Generic.List<TargetKind>();
            if ((CategoryFlag & CardCategory.Attachment) != 0)
            {
                list.Add(TargetKind.Character);
                list.Add(TargetKind.Attachment);
                list.Add(TargetKind.Council);
                list.Add(TargetKind.Area);
                list.Add(TargetKind.Covert);
            }
            // 角色/事务默认无 AllowedTargets（角色落点=地区，事务默认无目标）
            return list;
        }

        /// <summary>
        /// 该卡打出时是否需要选择目标。
        /// 职责划分：
        ///  - 玩家卡（角色/附属）为单目标，合法性由 TargetRules 判定；配置了任一 Kind != None 即需选目标。
        ///  - 事务卡可能需多目标，其目标合法性改由效果系统（EffectDataList 中 Target）判定，不再读 TargetRules。
        /// 优先级：单卡 TargetRequirement 覆盖 > EffectData.Target.NeedsSelection 聚合（事务卡等统一走此）> 类别默认。
        /// </summary>
        public bool GetRequiresTarget()
        {
            switch (TargetRequirement)
            {
                case TargetRequirement.Required: return true;
                case TargetRequirement.None: return false;
                default:
                    // 附属必须选目标
                    if ((CategoryFlag & CardCategory.Attachment) != 0) return true;

                    // 玩家卡方案 A 主入口：配置了任一 TargetRule(Kind != None) 即需选单目标。
                    // 注意：事务卡不在此分支判定，改由下方 EffectDataList 聚合（效果系统统一裁决）。
                    if (TargetRules != null)
                    {
                        foreach (var r in TargetRules)
                            if (r != null && r.Kind != TargetKind.None)
                                return true;
                    }

                    // 从 EffectDataList 聚合：任一效果 Target.NeedsSelection 为 true 即需要目标
                    if (EffectDataList != null)
                    {
                        bool anyNeeds = false;
                        bool anyTarget = false;
                        foreach (var e in EffectDataList)
                        {
                            var t = e?.Target;
                            if (t == null || t.Kind == TargetKind.None) continue;
                            anyTarget = true;
                            if (t.NeedsSelection) anyNeeds = true;
                        }
                        if (anyTarget) return anyNeeds;   // 有目标声明则按 NeedsSelection 决定
                    }

                    // 兜底：有允许目标才需选（默认无目标则不打出时选择）；角色无需选（地区即落点）
                    return GetAllowedTargets().Count > 0;
            }
        }

        /// <summary>
        /// 验证当前可用的颜色和领域忠诚度是否满足卡牌需求
        /// </summary>
        /// <param name="availableColors">可用颜色及点数字典</param>
        /// <param name="availableDomains">可用领域及点数字典</param>
        /// <returns>满足所有需求返回 true，否则 false</returns>
        public bool ValidateLoyalty(
            System.Collections.Generic.Dictionary<CardColor, int> availableColors,
            System.Collections.Generic.Dictionary<MagicDomain, int> availableDomains)
        {
            // 检查所有颜色忠诚度需求
            foreach (var req in ColorLoyaltyRequirements)
                if (!availableColors.TryGetValue(req.Color, out int c) || c < req.Amount)
                    return false;
            // 检查所有领域忠诚度需求
            foreach (var req in DomainLoyaltyRequirements)
                if (!availableDomains.TryGetValue(req.Domain, out int d) || d < req.Amount)
                    return false;
            return true;
        }

        /// <summary>获取总调查值（基础 + 先攻加成，若为先攻）</summary>
        public int GetTotalInvestigation(bool isFirst) => Investigation + (isFirst ? FirstStrikeInvestigation : 0);

        /// <summary>获取总战斗值（基础 + 先攻加成，若为先攻）</summary>
        public int GetTotalCombat(bool isFirst) => Combat + (isFirst ? FirstStrikeCombat : 0);

        /// <summary>获取总力量值（基础 + 先攻加成，若为先攻）</summary>
        public int GetTotalPower(bool isFirst) => Power + (isFirst ? FirstStrikePower : 0);

        /// <summary>是否具有防御能力（Defense >= 0）</summary>
        public bool HasDefense => Defense >= 0;
    }
}