using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Script.Game.Entities
{
    public class CardInstance
    {
        // ---------- 静态数据 ----------
        public BaseCardData Template { get; private set; }
        public PlayerCardData PlayerTemplate => Template as PlayerCardData;
        public AreaCardData AreaTemplate => Template as AreaCardData;
        public CouncilCardData CouncilTemplate => Template as CouncilCardData;

        // ---------- 唯一标识 ----------
        public string UID { get; private set; }

        // ---------- 目标有效性版本号（D16 丢失目标新方案） ----------
        // 每当卡的"会改变其作为目标合法性"的状态发生变化（区域移动 / 翻面 / 控制者变化 /
        // 子类别授予或撤销等）时自增。堆叠条目入栈时记录此值；结算时若目标实例仍存在但
        // VersionId 与入栈时不同，即判目标丢失（无需再为每个属性效果单独广播）。
        // 与 UID 互补：UID=身份（整局不变，定位"哪张卡"）；VersionId=状态版本（判"是否还是入栈时那张合法目标"）。
        public int VersionId { get; private set; } = 0;
        /// <summary>自增状态版本号。**附带效果**：按用户 2026-09 口径，版本号变动时该卡的
        /// "每回合只能发动一次/两次"限制**重新可用**（`_abilityUsesThisTurn` 一并清空）。</summary>
        public void BumpVersion()
        {
            VersionId++;
            ResetAbilityUsesThisTurn();   // 【K23】版本号变动 → 使用次数限制重置（用户口径）
        }
        // C/S 化·第一批：快照重建时按权威态恢复版本号（覆盖自增，保证双端一致）
        internal void SyncVersion(int v) => VersionId = v;
        // C/S 化·第一批：快照重建时按权威态恢复 UID（极端路径：实例晚于快照建立）
        internal void SetUid(string uid) => UID = uid;

        // ---------- 所属与控制 ----------
        public int OwnerId { get; set; }
        public int ControllerId { get; set; }

        // ---------- 区域信息 ----------
        public CardZone CurrentZone { get; set; } = CardZone.Deck;
        public string ZoneDetailId { get; set; } // 如地区ID、附属目标UID等

        // ---------- 动态能力值（当前实际值） ----------
        public int CurrentInvestigation { get; set; }
        public int CurrentStrength { get; set; }
        public int CurrentPower { get; set; }
        public int CurrentDefense { get; set; }

        // ---------- 魔法领域（实例层，JC060 光环可运行时授予） ----------
        // 初始化时从模板静态 MagicDomains 拷贝；运行时授予（如 JC060 给无领域角色加"心"）经 GrantMagicDomain 追加。
        // 规则/UI 判断"是否具有某领域图标"统一读此集合（而非仅模板），确保光环授予后对其他效果可见。
        public List<MagicDomain> MagicDomains { get; } = new List<MagicDomain>();
        public bool HasMagicDomain(MagicDomain d) => MagicDomains.Contains(d);

        /// <summary>
        /// 【P1】是否"具有魔法领域图标"（供 `TargetRestriction.HasMagicDomainIcon` 等复用，两端同源）。
        /// 判定顺序：实例当前领域（已含运行时授予）非空 → 真；否则回退模板印刷领域，并忽略 `-1` 哨兵（= 印刷"无领域"）。
        /// 之所以回退模板：`MagicDomains` 仅由 `ResetToBaseValues` 从模板拷入，未经过该步的实例（如尚在手牌）会是空表。
        /// </summary>
        public bool HasAnyMagicDomainIcon
        {
            get
            {
                if (MagicDomains.Count > 0) return true;
                var tmplDomains = PlayerTemplate?.MagicDomains;
                if (tmplDomains == null) return false;
                foreach (var d in tmplDomains)
                    if ((int)d != -1) return true;
                return false;
            }
        }

        // ---------- 先手能力值（当前实际值，含持续效果加成） ----------
        // 白眼=先手调查能力：持续效果满足前提时，每"白眼"给 FirstStrikeInvestigation +1（对应卡文"白眼白眼"叠加）。
        public int CurrentFirstStrikeInvestigation { get; set; }
        public int CurrentFirstStrikeStrength { get; set; }

        // ---------- 静止/禁止标记（D19-⑤） ----------
        // 与持续关键字授予(_granted*)分离：静止效果是规则约束（不能被装备结附等），常驻、不授出能力。
        private HashSet<StaticKind> _staticFlags = new HashSet<StaticKind>();
        public IReadOnlyCollection<StaticKind> StaticFlags => _staticFlags;
        public bool HasStaticFlag(StaticKind flag) => _staticFlags.Contains(flag);
        public void AddStaticFlag(StaticKind flag) => _staticFlags.Add(flag);
        public void RemoveStaticFlag(StaticKind flag) => _staticFlags.Remove(flag);

        // ---------- 状态标记 ----------
        public bool IsTapped { get; set; }

        /// <summary>
        /// 【触发语义】"正在死亡"标记：`CardManager.ResolveDeath` 在**广播死亡事件期间**置位、离场完成后清除。
        /// 用途：死亡触发窗口是在卡还留在场上时打开的（触发条件"卡在场上"天然成立），此时它**已不再是合法目标**，
        /// 故候选收集（`TurnManager.GetLocalZoneTargets` / `LegalityProjection`）需按本标记排除"正在死的卡"。
        /// 常态为 false，不参与快照投影。
        /// </summary>
        public bool IsDying { get; set; }
        public bool IsSealed { get; set; }
        public bool IsAttached { get; set; }
        public bool IsSeal { get; set; }   // 本卡是否为"封印卡"（封印了别的对象），镜像 IsAttached

        /// <summary>
        /// 【P2·封印信息隔离】本封印卡是否为"背面朝上"封印（JC119：**你的对手不能检视该牌**）。
        /// 仅对封印卡（<see cref="IsSeal"/>==true）有意义：为 true 时，快照投影对**非控制者**隐藏其牌面
        /// （`ZoneCardRef.IsSealedFaceDown` → `SnapshotFilter`/客户端只画卡背），控制者本人始终可见。
        /// 与 <see cref="IsCovert"/> 区分：暗藏是场上正/背面状态、影响对抗与承伤；本标记纯信息隔离，不影响任何规则判定。
        /// </summary>
        public bool IsSealedFaceDown { get; set; }
        public bool HasGuard { get; set; }
        public bool IsCovert { get; set; }        // 暗藏状态(背面朝上,场上暗藏者)
        public bool ExtraCostPaid { get; set; }   // 额外费用是否已支付(结算时玩家选择)

        // ---------- C/S 化·Phase B：信息隔离占位 ----------
        /// <summary>
        /// 是否为"隐藏信息占位实例"（对手手牌/牌库等不可见区域）：只承载"存在与数量"，
        /// `Template` 为 null（无名称/费用/效果），UI 只画卡背，不参与任何规则与交互。
        /// 仅由快照投影路径设置（`ZoneCardRef.IsHidden`），单机恒为 false。
        /// </summary>
        public bool IsHidden { get; set; }

        // ---------- C/S 化·第一批：快照补全字段 ----------
        // 这两项为卡的回合级状态，原仅存在于运行时（无持久属性），快照需携带以免重连后
        // 进场时机判定（EnterTrigger 过滤）与能力使用限制失真。
        public bool EnteredThisTurn { get; set; }
        public bool AbilityUsedThisTurn { get; set; }

        // ---------- 【K23】每回合"每张卡每能力"使用次数（"该能力每回合只能发动一次/两次"） ----------
        // 用户 2026-09 口径：计数基准 = **每张卡 · 每回合 · 每能力**（按 EffectId 分别计数）；
        // 且**该卡版本号变动时，限制重新可用**（见 BumpVersion）。
        // 回合重置：`TurnManager.DoReset`（重置步骤）→ `CardManager.ResetAllAbilityUsesThisTurn()`。
        // 声明：`EffectData.MaxUsesPerTurn`（0 = 不限次）。
        private readonly Dictionary<string, int> _abilityUsesThisTurn = new Dictionary<string, int>();

        /// <summary>本回合某能力（EffectId）已发动次数；未发动过返回 0。</summary>
        public int GetAbilityUsesThisTurn(string effectId)
            => (!string.IsNullOrEmpty(effectId) && _abilityUsesThisTurn.TryGetValue(effectId, out var n)) ? n : 0;

        /// <summary>登记一次发动（计数 +1）。调用点＝"发动被接受"的那一刻（激活=入堆叠前；触发=入堆叠结算前）。</summary>
        public void MarkAbilityUsedThisTurn(string effectId)
        {
            if (string.IsNullOrEmpty(effectId)) return;
            _abilityUsesThisTurn[effectId] = GetAbilityUsesThisTurn(effectId) + 1;
        }

        /// <summary>该能力本回合是否已达上限（max &lt;= 0 = 不限次）。</summary>
        public bool IsAbilityLimitReached(string effectId, int maxPerTurn)
            => maxPerTurn > 0 && GetAbilityUsesThisTurn(effectId) >= maxPerTurn;

        /// <summary>清空本卡的本回合能力计数（回合重置 / 版本号变动时调用）。</summary>
        public void ResetAbilityUsesThisTurn() => _abilityUsesThisTurn.Clear();

        /// <summary>快照投影用：本回合已使用的能力与次数（只读）。</summary>
        public IReadOnlyDictionary<string, int> AbilityUsesThisTurn => _abilityUsesThisTurn;

        /// <summary>快照恢复（客户端投影）：以权威态覆盖本卡的本回合能力计数。</summary>
        public void SetAbilityUsesThisTurn(IEnumerable<KeyValuePair<string, int>> pairs)
        {
            _abilityUsesThisTurn.Clear();
            if (pairs == null) return;
            foreach (var kv in pairs)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) _abilityUsesThisTurn[kv.Key] = kv.Value;
        }

        // ---------- 状态变更中枢（Model 自身事件，供 Controller 订阅转发） ----------
        // 【MVC 修复】原实现直接调用 CardManager.Notify* 静态总线，形成 Model→Controller 反向依赖。
        // 现改为 Model 声明自身事件（OnStateChanged），由 CardManager.CreateCardInstance 工厂订阅并转发到全局总线，
        // 解除 Model 对 Controller 的编译期耦合（依赖倒置）。TurnManager/Arena 对 CardManager 总线的订阅不变。
        // 注（2026-08-11）：原 OnSubCategoriesChanged 事件已删除——子类别/领域变化判丢失改由 D16 版本号方案覆盖，
        // 不再需要 Model→总线事件；此处仅保留 OnStateChanged 供通用状态变更（含 UI 刷新）使用。
        /// <summary>状态变更事件（暗藏/屏障/移动等），签名与 CardManager.OnCardStateChanged 一致。</summary>
        public event Action<CardInstance, CardChangeKind> OnStateChanged;

        /// <summary>
        /// 设置暗藏状态(正面↔背面)。任何 flip 动作都必须经此方法。
        /// 规则③【翻面即丢失】（2026-08-11 收口）：此处调用 BumpVersion() 使目标版本号失效——
        /// 正→反与反→正双向均丢，且不论效果是否声明 IncludeCovert（能对暗藏者生效的效果也一样丢）。
        /// 原先该规则依赖 TurnManager 的 CovertChanged 总线即时置位，总线移除后改由版本号承载，
        /// 结算时 IsTargetLost 比对 TargetVersionId 不一致即判丢失。
        /// OnStateChanged 仍照常发出，但仅供 UI 刷新（Arena 箭头重绘）使用。
        /// </summary>
        public void SetCovert(bool covert)
        {
            if (IsCovert == covert) return;
            IsCovert = covert;
            BumpVersion();   // 规则③：翻面使目标版本号失效（结算时判丢失）
            OnStateChanged?.Invoke(this, CardChangeKind.CovertChanged);
        }

        /// <summary>
        /// 设置屏障状态。任何屏障授予/移除都必须经此方法，以便经 OnStateChanged 发出
        /// GuardChanged（规则④⑦）。注：屏障机制当前未接入运行时赋值，此方法预留，待屏障逻辑落地后自动生效。
        /// </summary>
        public void SetGuard(bool guard)
        {
            if (HasGuard == guard) return;
            HasGuard = guard;
            OnStateChanged?.Invoke(this, CardChangeKind.GuardChanged);
        }

        /// <summary>设置横置/重置状态（IsTapped 布尔变化）。任何横置/竖置动作都应经此方法，
        /// 以发出 TappedChanged 事件驱动持续效果重判（JC068 阿格斯IV"未横置封锁"即时开关）。</summary>
        public void SetTapped(bool tapped)
        {
            if (IsTapped == tapped) return;
            IsTapped = tapped;
            OnStateChanged?.Invoke(this, CardChangeKind.TappedChanged);
        }

        // ---------- 计数器 ----------
        private Dictionary<string, int> _counters = new Dictionary<string, int>();
        public IReadOnlyDictionary<string, int> Counters => _counters;

        /// <summary>
        /// 【JC124·死亡瞬间的计数器快照】死亡触发在"卡仍在场上"时广播，但**结算发生在卡已进墓地之后**，
        /// 而进墓地会 `ResetToBaseState()` 清空计数器 —— 于是"**具有哲人石标志的角色**死亡时…"这类
        /// 以"标志"为条件的死亡触发，在结算阶段复检条件时标志已经消失（条件必然不成立）。
        /// 故由 `CardManager.ResolveDeath` 在**广播死亡事件之前**抓取一份快照；谓词 `DiedWithCounter` 读它。
        /// （快照在**每一次**死亡时重写，因此不会因上一次死亡而残留误判。）
        /// </summary>
        private Dictionary<string, int> _countersAtDeath = new Dictionary<string, int>();

        /// <summary>【JC124】死亡瞬间的计数器快照（只读；未死亡过为空）。</summary>
        public IReadOnlyDictionary<string, int> CountersAtDeath => _countersAtDeath;

        /// <summary>【JC124】在**广播死亡事件之前**抓取计数器快照（由 `CardManager.ResolveDeath` 调用）。</summary>
        public void SnapshotCountersAtDeath()
        {
            _countersAtDeath.Clear();
            foreach (var kv in _counters) _countersAtDeath[kv.Key] = kv.Value;
        }

        /// <summary>【JC124】死亡瞬间是否具有某计数器（快照优先；无快照则看当前计数器）。</summary>
        public bool HadCounterAtDeath(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            if (_countersAtDeath.Count > 0)
                return _countersAtDeath.TryGetValue(type, out var n) && n > 0;
            return _counters.TryGetValue(type, out var cur) && cur > 0;
        }

        // ---------- 持续/修正效果动态授予（由 EffectEngine 持续注册表增删，离场/封印时还原） ----------
        // 与基础能力值(Current*)分离：此处记录"由效果赋予的"关键字/图标/标志属性，便于精确还原。
        private HashSet<string> _grantedKeywords = new HashSet<string>();   // 关键字：公开、先手、调查…
        private HashSet<string> _grantedIcons = new HashSet<string>();      // 图标：领域图标（心心/白眼/黑刀…）
        private Dictionary<string, int> _grantedAttributes = new Dictionary<string, int>(); // 标志属性增益计数
        private HashSet<MagicDomain> _grantedDomains = new HashSet<MagicDomain>(); // 运行时授予的魔法领域（JC060 光环等），对称 _grantedAttributes
        /// <summary>【P3·K4】被"改写掉"的子类别（按来源计数）：JC036 初拥把受结附角色的**人类**类别改为**吸血鬼**。
        /// 只影响**当前实际**集合（`CurrentSubCategories`）；`HasStaticSubCategory`（印刷类别）读模板数据，**不受影响**
        /// （用户 2026-09 口径："印刷类别为人类"指模型数据；卡片实例失去该类别不改变模型数据）。</summary>
        private Dictionary<string, int> _suppressedSubCategories = new Dictionary<string, int>();

        public IReadOnlyCollection<string> GrantedKeywords => _grantedKeywords;
        public IReadOnlyCollection<string> GrantedIcons => _grantedIcons;
        public IReadOnlyDictionary<string, int> GrantedAttributes => _grantedAttributes;
        /// <summary>【P3·K4】当前被"改写掉"的子类别（快照投影用，客户端据此重建同一份当前子类别集合）。</summary>
        public IEnumerable<string> SuppressedSubCategories => _suppressedSubCategories.Keys;

        // ==================== 【P3·K4】实例级"形态"（JC027 / JC028） ====================
        // 用户 2026-09 口径 **A32**：形态转换**只改实例**，**没有任何效果会改变卡的印刷类别**
        //（故不需要"印刷类别改写 CategoryOverride"）。本组字段即"实例级形态覆盖"：
        //  · 类别覆盖（附属↔角色，可组合）→ 影响**目标合法性**与"是不是角色"类判定；
        //  · 卡名覆盖（JC028「鲜血」）→ 影响**卡名判定**（`TargetRestriction.CardName` 等）；
        //  · 印刷效果失活（JC028"损失了原有的效果"）→ 该卡改用**形态效果表**（`FormEffects`）；
        //  · 印刷图标/防御清零（JC028"不具有能力图标和防御力"）。
        private CardCategory _categoryOverride = CardCategory.None;
        private string _nameOverride;
        private bool _printedEffectsSuppressed;
        private bool _zeroPrintedStatistics;
        private List<EffectData> _formEffects;

        /// <summary>实例级类别覆盖（`None`＝未覆盖，用模板印刷类别）。</summary>
        public CardCategory CategoryFlagOverride => _categoryOverride;
        /// <summary>实例级卡名覆盖（空＝用模板卡名）。</summary>
        public string CardNameOverride => _nameOverride ?? "";
        /// <summary>印刷效果是否已失活（形态生效且该形态声明"损失原有效果"）。</summary>
        public bool IsPrintedEffectsSuppressed => _printedEffectsSuppressed;
        /// <summary>形态的**有效类别**：有覆盖用覆盖，否则用模板印刷类别。</summary>
        public CardCategory EffectiveCategoryFlag
            => _categoryOverride != CardCategory.None
                ? _categoryOverride
                : (PlayerTemplate?.CategoryFlag ?? CardCategory.None);
        /// <summary>形态的**有效卡名**：有覆盖用覆盖，否则用模板卡名。</summary>
        public string EffectiveCardName
            => string.IsNullOrEmpty(_nameOverride) ? (PlayerTemplate?.CardName ?? "") : _nameOverride;
        /// <summary>形态效果表（`IsPrintedEffectsSuppressed` 时取代模板效果表；空＝无效果）。</summary>
        public List<EffectData> FormEffects => _formEffects;
        /// <summary>是否存在实例级形态覆盖（用于投影/判定短路）。</summary>
        public bool HasFormOverride
            => _categoryOverride != CardCategory.None || !string.IsNullOrEmpty(_nameOverride)
               || _printedEffectsSuppressed;

        /// <summary>
        /// 【P3·K4】施加**实例级形态**（JC027 附属→地精角色 / JC028 角色→"鲜血"附属）。
        /// `zeroPrintedStatistics`：清空实例的印刷能力值与防御（"不具有能力图标和防御力"）——
        /// 由 `ResetToBaseValues` 共同尊重，避免任何一次属性重算把图标/防御"复活"。
        /// </summary>
        public void ApplyFormOverride(CardCategory category, string name, bool suppressPrintedEffects,
            bool zeroPrintedStatistics, IEnumerable<EffectData> formEffects)
        {
            _categoryOverride = category;
            _nameOverride = string.IsNullOrEmpty(name) ? null : name;
            _printedEffectsSuppressed = suppressPrintedEffects;
            _zeroPrintedStatistics = zeroPrintedStatistics;
            _formEffects = formEffects == null ? null : new List<EffectData>(formEffects);
            if (_zeroPrintedStatistics)
            {
                CurrentInvestigation = 0;
                CurrentStrength = 0;
                CurrentFirstStrikeInvestigation = 0;
                CurrentFirstStrikeStrength = 0;
                CurrentDefense = -1;      // "-1"＝无防御（与模板哨兵一致）
            }
        }

        /// <summary>清除实例级形态（到期/离场复位）：恢复模板类别/卡名/效果表，并按模板重建能力值。</summary>
        public void ClearFormOverride()
        {
            if (!HasFormOverride && _formEffects == null) return;
            _categoryOverride = CardCategory.None;
            _nameOverride = null;
            _printedEffectsSuppressed = false;
            _formEffects = null;
            if (_zeroPrintedStatistics)
            {
                _zeroPrintedStatistics = false;
                ResetToBaseValues(false);   // 恢复印刷能力值/防御（先手加成的重算由调用方负责）
            }
        }

        /// <summary>该卡当前应使用的**效果表**：形态失活印刷效果时用形态效果表，否则用模板效果表。</summary>
        public List<EffectData> EffectiveEffects
        {
            get
            {
                if (_printedEffectsSuppressed) return _formEffects ?? new List<EffectData>();
                var list = new List<EffectData>();
                var tmpl = PlayerTemplate as PlayerCardData;
                if (tmpl?.EffectDataList != null)
                    foreach (var e in tmpl.EffectDataList) list.Add(e);
                return list;
            }
        }

        /// <summary>
        /// 当前实际子类型集合（可变，实例唯一真相源）。
        ///  - 实例化时从模板静态 SubCategories 拷贝初始值；
        ///  - 后续持续效果（如 JC030 授予"吸血鬼"）追加进入；
        ///  - 离场/封印时重置回静态拷贝。
        /// 规则/UI 判断是否"当前具有某子类型"统一读此集合。
        /// </summary>
        public HashSet<string> CurrentSubCategories { get; } = new HashSet<string>();

        public bool HasKeyword(string kw) => _grantedKeywords.Contains(kw);
        public bool HasIcon(string icon) => _grantedIcons.Contains(icon);
        public int GetAttribute(string attr) => _grantedAttributes.TryGetValue(attr, out var v) ? v : 0;

        /// <summary>当前实际是否具有某子类型（含静态 + 运行时授予）。规则/UI 统一入口。</summary>
        public bool HasSubCategory(string sub) => CurrentSubCategories.Contains(sub);

        /// <summary>仅判断模板【静态】子类型（不含运行时授予）。用于需要区分"固有身份"的场景。</summary>
        public bool HasStaticSubCategory(string sub) => PlayerTemplate?.SubCategories.Contains(sub) ?? false;

        /// <summary>
        /// 是否具有【公开】关键字（不可用于派遣暗藏行动）。组合两层来源：
        ///  - 运行时授予（持续效果 RegisterContinuous 已在场区注册）；
        ///  - 模板固有声明（手牌期 RegisterContinuous 未调用，从 PlayerTemplate.HasInherentKeyword 补判）。
        /// UI 与规则层统一使用此属性，避免手牌期漏判。
        /// </summary>
        public bool HasPublicKeyword
            => HasKeyword("公开") || (PlayerTemplate?.HasInherentKeyword("公开") ?? false);

        /// <summary>持续效果注册：授予关键字/图标/属性（+1 计数）。</summary>
        public void Grant(string kind, string value, int amount = 1)
        {
            if (amount == 0) return;
            switch (kind)
            {
                case "Keyword": _grantedKeywords.Add(value); break;
                case "Icon": _grantedIcons.Add(value); break;
                case "Attribute":
                    if (!_grantedAttributes.ContainsKey(value)) _grantedAttributes[value] = 0;
                    _grantedAttributes[value] += amount;
                    if (_grantedAttributes[value] == 0) _grantedAttributes.Remove(value);

                    // 白眼=先手调查：每"白眼"(Attribute="先手调查")给先手调查+1（卡文"白眼白眼"叠加）
                    if (value == "先手调查") CurrentFirstStrikeInvestigation += amount;
                    // 黑眼=调查：每"黑眼"(Attribute="调查")给基础调查+1（JC030 等，不受先手限制）
                    else if (value == "调查") CurrentInvestigation += amount;
                    // 黑刀=战力：每"黑刀"(Attribute="战斗")给基础战力+1（JC060 光环，对称调查做法；实际结算经 AreaStatsCalculator 读 CurrentStrength）
                    else if (value == "战斗") CurrentStrength += amount;
                    // 【P1】白刀=先手战力：与"先手调查"同构——记入 CurrentFirstStrikeStrength，
                    // 由 AreaStatsCalculator 在"本卡控制者恰为先手"时叠加（不折进 CurrentStrength，避免非先手时误加）。
                    else if (value == "先手战斗") CurrentFirstStrikeStrength += amount;
                    // 【P1】黑星=势力：折进 CurrentPower（与"调查"折进 CurrentInvestigation 对称）。
                    else if (value == "势力") CurrentPower += amount;
                    // 【P1】白星=先手势力：无对应实例字段（模板只有 FirstStrikePower），由 AreaStatsCalculator 实时读取。
                    // 注：以上 6 个是"能力图标 ↔ 属性"的完整映射——黑刀/白眼/黑眼/白刀/黑星/白星。
                    // 【P3·E】防御力：光环类"获得+N防御力"（JC059）经此调整 `CurrentDefense`，
                    // 语义与 `EffectEngine` 的 `Gain{"防御"}` 完全一致（原本无防御则先归 0，下限钳到 -1）。
                    else if (value == "防御")
                    {
                        if (CurrentDefense < 0) CurrentDefense = 0;
                        CurrentDefense += amount;
                        if (CurrentDefense < -1) CurrentDefense = -1;
                    }
                    break;
                case "MagicDomain":
                    // JC060 光环：给被影响角色实例层追加魔法领域（如"心"=心灵领域），供其他效果/UI 识别
                    if (Enum.TryParse<MagicDomain>(value, out var d) && !_grantedDomains.Contains(d))
                    {
                        _grantedDomains.Add(d);
                        if (!MagicDomains.Contains(d)) MagicDomains.Add(d);
                    }
                    break;
                case "SubCategory":
                    // 运行时子类型（如 JC030 授予"吸血鬼"）：写入 CurrentSubCategories（当前实际子类型集合）。
                    // 不 BumpVersion：子类别合法性由 TargetValidity.Check 结算时实时复检（如 NotStaticSubCategory/StaticSubCategory/CurrentSubCategory），
                    // 不再依赖版本号（原 Bump 为旧"总线通知"遗留，总线已移除）。见 2026-08-15 JC030 修复。
                    CurrentSubCategories.Add(value);
                    break;
                case "SubCategoryRemove":
                    // 【P3·K4】"类别改写"的**移除侧**（JC036 初拥"将该角色的人类类别改为吸血鬼类别"）：
                    // 与 `SubCategory` 的"追加"相反——把该子类别从**当前实际**集合里抹掉（即使它是**模板固有**的，
                    // 这正是"改写"与"额外具有"的区别），并按来源**计数**登记，撤销时计数归零才还原。
                    // 注：`HasStaticSubCategory`（印刷类别）**不受影响**（读模板数据；用户 2026-09 口径）。
                    if (!_suppressedSubCategories.ContainsKey(value)) _suppressedSubCategories[value] = 0;
                    _suppressedSubCategories[value] += amount;
                    if (_suppressedSubCategories[value] > 0) CurrentSubCategories.Remove(value);
                    else _suppressedSubCategories.Remove(value);
                    break;
            }
        }

        /// <summary>持续效果注销：撤销授予（amount 为负向计数，用于多源叠加时精确还原）。</summary>
        public void Revoke(string kind, string value, int amount = 1)
        {
            if (amount == 0) return;
            switch (kind)
            {
                case "Keyword": _grantedKeywords.Remove(value); break;
                case "Icon": _grantedIcons.Remove(value); break;
                case "Attribute":
                    // 【幂等护栏】只有"确实登记过该属性授予"时才回退派生数值：
                    // 否则重复撤销（如光环撤销 + 自愈/换区清空后的重算）会把 `CurrentDefense` 等数值扣穿
                    // （表现为"防御力变成 0/负值"）。Grant/Revoke 必须成对，且多源叠加时按 amount 精确还原。
                    if (!_grantedAttributes.ContainsKey(value)) break;
                    _grantedAttributes[value] -= amount;
                    if (_grantedAttributes[value] <= 0) _grantedAttributes.Remove(value);

                    // 白眼=先手调查：撤销时回退先手调查
                    if (value == "先手调查") CurrentFirstStrikeInvestigation -= amount;
                    // 黑眼=调查：撤销时回退基础调查
                    else if (value == "调查") CurrentInvestigation -= amount;
                    // 黑刀=战力：撤销时回退基础战力
                    else if (value == "战斗") CurrentStrength -= amount;
                    // 【P1】白刀=先手战力：撤销时回退（与 Grant 对称）
                    else if (value == "先手战斗") CurrentFirstStrikeStrength -= amount;
                    // 【P1】黑星=势力：撤销时回退基础势力（与 Grant 对称）
                    else if (value == "势力") CurrentPower -= amount;
                    // 【P3·E】防御力：撤销时回退（与 Grant 对称；防御不得低于 -1 哨兵）
                    else if (value == "防御")
                    {
                        CurrentDefense -= amount;
                        if (CurrentDefense < -1) CurrentDefense = -1;
                    }
                    break;
                case "MagicDomain":
                    // JC060 光环撤销：仅移除"运行时授予"的域（来自 _grantedDomains），模板固有域不动
                    if (Enum.TryParse<MagicDomain>(value, out var d) && _grantedDomains.Contains(d))
                    {
                        _grantedDomains.Remove(d);
                        // 仅当该域不是模板固有（即纯运行时授予）才从 MagicDomains 移除，避免误删静态域
                        if (PlayerTemplate == null || PlayerTemplate.MagicDomains == null || !PlayerTemplate.MagicDomains.Contains(d))
                            MagicDomains.Remove(d);
                    }
                    break;
                case "SubCategory":
                    // 撤销时回退：仅当该子类型非模板固有（避免误删静态子类型）才从 CurrentSubCategories 移除。
                    // 不 BumpVersion：同 Grant("SubCategory")，子类别合法性由结算复检覆盖，不依赖版本号（2026-08-15 JC030 修复）。
                    if (!HasStaticSubCategory(value)) CurrentSubCategories.Remove(value);
                    break;
                case "SubCategoryRemove":
                    // 【P3·K4】复原"被改写掉的子类别"：计数归零才还原；模板固有者补回当前集合
                    // （运行时授予的那一份由它自己的 `SubCategory` 授予/撤销负责，此处不越权）。
                    // 条目不存在＝已被 `ResetToBaseState`（离场复位）连同集合一起清掉 → 无需还原（幂等）。
                    if (!_suppressedSubCategories.ContainsKey(value)) break;
                    _suppressedSubCategories[value] -= amount;
                    if (_suppressedSubCategories[value] <= 0)
                    {
                        _suppressedSubCategories.Remove(value);
                        if (HasStaticSubCategory(value)) CurrentSubCategories.Add(value);
                    }
                    break;
            }
        }

        /// <summary>
        /// 【P1·C/S 投影回填】按快照恢复"运行时状态"（静止/禁止标记、当前子类别、运行时授予、先手能力值）。
        ///
        /// 存在意义：真跨进程客户端**不运行 EffectEngine**（没有持续效果注册表），若这些状态不入快照，
        /// 客户端上任何读取它们的判定/展示都会与服务端不一致。典型三例：
        ///  · JC001-A/JC030-B 的 CannotBeAttached 静止标记缺失 → 客户端以为可以装备结附，服务端拒绝；
        ///  · JC030-C 授予的"吸血鬼"子类别缺失 → 卡面子类别/基于子类别的判定两端不同；
        ///  · JC001 白眼 / JC030 黑眼 / JC060 光环的授予属性缺失 → 客户端地区统计（AreaStatsCalculator）
        ///    与 CardUI 白眼标签少算数值。
        ///
        /// 关键纪律：这里【直接覆盖】各集合，**不走 Grant()/Revoke()**——Grant 对"调查/战斗/先手调查"
        /// 会就地叠加 Current*，而 Current*（CurrentInvestigation / CurrentStrength / CurrentFirstStrike*）
        /// 已随快照独立投影到位，再走 Grant 就二次叠加了。
        /// </summary>
        public void RestoreProjectedState(
            IEnumerable<StaticKind> staticFlags,
            IEnumerable<string> currentSubCategories,
            IEnumerable<string> removedSubCategories,
            IEnumerable<string> grantedKeywords,
            IEnumerable<string> grantedIcons,
            IDictionary<string, int> grantedAttributes,
            int currentFirstStrikeInvestigation,
            int currentFirstStrikeStrength)
        {
            _staticFlags.Clear();
            if (staticFlags != null)
                foreach (var f in staticFlags) _staticFlags.Add(f);

            // 【P3·K4】"被改写掉的子类别"（JC036 初拥）必须先于并集重建登记：
            // 服务端"印刷类别"仍在（模板数据不变），故客户端重建时**不能**只靠"模板固有"就把它加回来。
            _suppressedSubCategories.Clear();
            if (removedSubCategories != null)
                foreach (var s in removedSubCategories)
                    if (!string.IsNullOrEmpty(s)) _suppressedSubCategories[s] = 1;

            // 当前子类别 = （模板固有 ∪ 快照运行时值）− 被改写掉的（并集而非覆盖：模板固有项在服务端恒成立，
            // 漏掉只会造成假阴性；被"改写"掉的则必须减掉，否则客户端会多出服务端已失去的类别）
            CurrentSubCategories.Clear();
            if (PlayerTemplate?.SubCategories != null)
                foreach (var s in PlayerTemplate.SubCategories)
                    if (!_suppressedSubCategories.ContainsKey(s)) CurrentSubCategories.Add(s);
            if (currentSubCategories != null)
                foreach (var s in currentSubCategories)
                    if (!string.IsNullOrEmpty(s) && !_suppressedSubCategories.ContainsKey(s)) CurrentSubCategories.Add(s);

            _grantedKeywords.Clear();
            if (grantedKeywords != null)
                foreach (var k in grantedKeywords)
                    if (!string.IsNullOrEmpty(k)) _grantedKeywords.Add(k);
            _grantedIcons.Clear();
            if (grantedIcons != null)
                foreach (var i in grantedIcons)
                    if (!string.IsNullOrEmpty(i)) _grantedIcons.Add(i);
            _grantedAttributes.Clear();
            if (grantedAttributes != null)
                foreach (var kv in grantedAttributes)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != 0) _grantedAttributes[kv.Key] = kv.Value;

            CurrentFirstStrikeInvestigation = currentFirstStrikeInvestigation;
            CurrentFirstStrikeStrength = currentFirstStrikeStrength;
        }

        // ---------- 伤害/创伤 ----------
        private int _damage = 0;
        public int Damage
        {
            get => _damage;
            internal set => _damage = Math.Max(0, value);
        }
        private int _trauma = 0;
        public int Trauma
        {
            get => _trauma;
            internal set => _trauma = Math.Max(0, value);
        }

        // ---------- 关联关系 ----------
        public CardInstance AttachedTo { get; set; }
        public List<CardInstance> AttachedCards { get; set; } = new List<CardInstance>();
        public CardInstance SealedBy { get; set; }
        public List<CardInstance> SealedCards { get; set; } = new List<CardInstance>();

        /// <summary>
        /// 【JC119 守秘人】本卡**因宿主离场而从封印回手**时记录**封印它的那张宿主卡 UID**
        /// （"刚刚由于封印效果回到手牌的牌"）——死亡触发的"展示并免费打出其中一张"据此识别候选。
        /// 生命周期：宿主离场连带回手时置位；该卡**离开手牌**（打出/弃掉/移动）时清除。
        /// </summary>
        public string ReturnedFromSealHostUid { get; set; }

        // ---------- 死亡事件（纯数据事件字段，由 Controller 在规则判定后触发，Model 不自触发）----------
        // 注：使用普通委托字段（非 event）以便 Controller 层在死亡判定后显式触发；
        //     订阅方仍通过 += 订阅，但触发权收口于 Controller。
        public Action<CardInstance> OnDeath;

        // ============================================================
        // 构造函数
        // ============================================================
        public CardInstance(BaseCardData template, string customUid = null)
        {
            Template = template;
            UID = customUid ?? Guid.NewGuid().ToString();

            // 默认值：无防御
            CurrentDefense = -1;

            // 只有 PlayerTemplate 不为空时才从模板读取能力值
            if (PlayerTemplate != null)
            {
                CurrentInvestigation = PlayerTemplate.Investigation;
                CurrentStrength = PlayerTemplate.Strength;
                CurrentPower = PlayerTemplate.Power;
                CurrentDefense = PlayerTemplate.Defense >= 0 ? PlayerTemplate.Defense : -1;
                // 初始化当前子类型集合：拷贝模板静态 SubCategories（后续持续效果可追加运行时子类型）
                foreach (var s in PlayerTemplate.SubCategories) CurrentSubCategories.Add(s);
            }
        }

        // ============================================================
        // 能力值计算
        // ============================================================
        public void ResetToBaseValues(bool isFirstPlayer)
        {
            if (PlayerTemplate == null) return;
            // 【P3·K4】形态声明"不具有能力图标和防御力"（JC028「鲜血」）→ 复位也保持清零
            if (_zeroPrintedStatistics)
            {
                CurrentInvestigation = 0;
                CurrentStrength = 0;
                CurrentPower = 0;
                CurrentFirstStrikeInvestigation = 0;
                CurrentFirstStrikeStrength = 0;
                CurrentDefense = -1;
                return;
            }
            CurrentInvestigation = PlayerTemplate.GetTotalInvestigation(isFirstPlayer);
            CurrentStrength = PlayerTemplate.GetTotalStrength(isFirstPlayer);
            CurrentPower = PlayerTemplate.GetTotalPower(isFirstPlayer);
            CurrentDefense = PlayerTemplate.Defense >= 0 ? PlayerTemplate.Defense : -1;
            // 黑眼=调查：基础调查 + 当前已授予的"调查"属性（不受先手限制，保证重算后持续效果不丢失）
            CurrentInvestigation += GetAttribute("调查");
            // 黑刀=战力：基础战力 + 当前已授予的"战斗"属性（JC060 光环 +1 战力，对称调查做法，保证重算后不丢失）
            CurrentStrength += GetAttribute("战斗");
            // 先手值：基础 FirstStrike + 当前已授予的"先手调查"属性（白眼叠加），保证重算后持续效果不丢失
            int whiteEyes = GetAttribute("先手调查");
            CurrentFirstStrikeInvestigation = PlayerTemplate.FirstStrikeInvestigation + whiteEyes;
            CurrentFirstStrikeStrength = PlayerTemplate.FirstStrikeStrength;
            // 【P3·E】防御力：基础防御 + 当前已授予的"防御"属性（光环"获得+N防御力"，如 JC059）。
            // ⚠ 必须与其它属性同款重算，否则"属性重算（RefreshFirstStrikeStats）→ 光环撤销"会多减一次，
            // 表现为防御力被扣穿（如 3 → 1）。
            // 【P3·K13 修正】判据必须是 `!= 0`：**负值授予**（如 JC089「受此结附的角色 -1 防御」）
            // 在旧写法 `> 0` 下会被跳过 → 复位后减益凭空消失。
            int grantedDefense = GetAttribute("防御");
            if (grantedDefense != 0)
            {
                if (CurrentDefense < 0) CurrentDefense = 0;
                CurrentDefense += grantedDefense;
                if (CurrentDefense < -1) CurrentDefense = -1;
            }
            // 【P1】白刀/黑星：先手战力与势力同样按"基础 + 授予"重建（保持与 调查/战斗 对称）
            CurrentFirstStrikeStrength += GetAttribute("先手战斗");
            CurrentPower += GetAttribute("势力");
            // 实例魔法领域：模板固有（过滤-1哨兵）+ 运行时授予（_grantedDomains，JC060 光环等）两部分。
            // 对称 JC001 白眼机制：重算时从"模板固有 + 授予集合"直接重建，不依赖反推，避免每回合先手重算冲刷光环授予的域。
            // 【P3·K20】领域抑制期间（"失去所有魔法领域图标"）→ `RebuildDomains` 会跳过重填（否则重算会把图标复活）。
            RebuildDomains();
        }

        public bool HasDefense => PlayerTemplate != null && PlayerTemplate.Defense >= 0;

        /// <summary>【P3·K20】**魔法领域图标被抑制**（"失去所有魔法领域图标"：JC099 / JC101）：
        /// 抑制期间实例的 `MagicDomains` 为空，且 `ResetToBaseValues` 重建时也会**跳过**领域重填
        /// （否则任何一次属性重算都会把图标"复活"）。`RestoreDomains()` 精确恢复。</summary>
        public bool AreDomainsSuppressed { get; private set; }

        public void SuppressAllDomains()
        {
            if (AreDomainsSuppressed) return;
            AreDomainsSuppressed = true;
            MagicDomains.Clear();
        }

        public void RestoreDomains()
        {
            if (!AreDomainsSuppressed) return;
            AreDomainsSuppressed = false;
            RebuildDomains();
        }

        /// <summary>按"模板固有（去 -1 哨兵）+ 运行期授予"重建领域集合；抑制期间不填。</summary>
        private void RebuildDomains()
        {
            MagicDomains.Clear();
            if (AreDomainsSuppressed) return;
            if (PlayerTemplate?.MagicDomains != null)
                foreach (var d in PlayerTemplate.MagicDomains)
                    if ((int)d != -1) MagicDomains.Add(d);
            foreach (var d in _grantedDomains)
                if (!MagicDomains.Contains(d)) MagicDomains.Add(d);
        }

        // ============================================================
        // 伤害/创伤管理（纯数据写入，不包含规则判定与死亡流程）
        // 注：原 HasDefense 守卫、死亡判定(CheckDeath/IsDead) 与 OnDeath 触发
        //     已上移至 Controller 层（CardManager.DealDamageToInstance / HandleDeath），
        //     本类仅负责把数值写入 Damage/Trauma 字段。
        // ============================================================
        public void TakeDamage(int amount)
        {
            if (amount == 0) return;
            if (amount > 0)
            {
                Damage += amount;
            }
            else
            {
                // 负值：减少伤害
                Damage = Math.Max(0, Damage + amount); // amount 是负数
            }
        }

        public void TakeTrauma(int amount)
        {
            if (amount <= 0) return;
            Trauma += amount;
        }

        public void ResetDamage()
        {
            Damage = 0;
        }

        public void ResetTrauma()
        {
            Trauma = 0;
        }

        // ============================================================
        // 死亡判定（纯查询，无副作用；由 Controller 调用以驱动死亡流程）
        // ============================================================
        public bool IsDead()
        {
            if (!HasDefense) return false;
            return Damage + Trauma >= CurrentDefense;
        }

        // ============================================================
        // 计数器管理（纯数据写入，不含规则判定）
        // 本类只负责字典增删。
        // ============================================================
        public void AddCounter(string type, int amount = 1)
        {
            if (!_counters.ContainsKey(type)) _counters[type] = 0;
            _counters[type] += amount;
        }

        public void RemoveCounter(string type, int amount = 1)
        {
            if (!_counters.ContainsKey(type)) return;
            _counters[type] = Math.Max(0, _counters[type] - amount);
            if (_counters[type] == 0) _counters.Remove(type);
        }

        /// <summary>移除卡上所有指示物</summary>
        public void ClearCounters()
        {
            _counters.Clear();
        }

        // ============================================================
        // 常规重置(回合重置步骤调用)
        // ============================================================
        /// <summary>
        /// 常规重置(回合重置步骤调用):解除横置。
        /// 规则：所有暗藏者在不受其他效果影响时均可被常规重置解除横置（与正常角色一致）；
        /// 仅被特定效果标记为"不可重置"的卡才跳过。本方法为纯状态清零，不含该判定，
        /// 判定由调用方(回合重置逻辑)在需要时负责。
        /// </summary>
        public void Untap()
        {
            // 统一走 SetTapped 事件链，避免绕过 TappedChanged 导致持续效果（JC068 阿格斯封锁）不同步。
            // 注意：当前无调用者；回合重置实际走 CardManager.UntapAll（亦经 SetTapped）。
            SetTapped(false);
        }

        // ============================================================
        // 状态清理（进入手牌/卡组时调用）
        // ============================================================
        public void ResetToBaseState()
        {
            // 清除伤害和创伤
            Damage = 0;
            Trauma = 0;

            // 清除状态
            IsTapped = false;
            IsSealed = false;
            IsAttached = false;
            IsSeal = false;   // 重置时清除封印卡标记
            IsSealedFaceDown = false;   // 【P2】离场/回手即不再是封印卡，背面标记一并清除
            HasGuard = false;
            SetCovert(false);       // 暗藏只存在于场上,离开时解除(经 SetCovert 发 CovertChanged 事件)
            ExtraCostPaid = false;  // 重新进入手牌/卡组时重置额外费用支付态

            // 清除计数器
            _counters.Clear();

            // 清除持续/修正效果动态授予（离场时还原）
            _grantedKeywords.Clear();
            _grantedIcons.Clear();
            _grantedAttributes.Clear();
            _grantedDomains.Clear();
            // 【P3·K4】"被改写掉的子类别"同属运行期状态：离场一并清零（下方按模板重建当前集合）
            _suppressedSubCategories.Clear();
            // 【P3·K4】实例级形态（JC027/JC028）同属运行期状态：离场复位即还原为印刷形态
            _categoryOverride = CardCategory.None;
            _nameOverride = null;
            _printedEffectsSuppressed = false;
            _zeroPrintedStatistics = false;
            _formEffects = null;

            // 重置当前子类型集合：清空后恢复为模板静态 SubCategories（移除运行时授予的如"吸血鬼"）
            CurrentSubCategories.Clear();
            if (PlayerTemplate != null)
                foreach (var s in PlayerTemplate.SubCategories) CurrentSubCategories.Add(s);

            // 清除静止/禁止标记（D19-⑦：离场/封印时清位，防止状态泄漏）
            _staticFlags.Clear();

            // 清除临时能力值（恢复为基础值，先手加成由调用者决定）
            if (PlayerTemplate != null)
            {
                CurrentInvestigation = PlayerTemplate.Investigation;
                CurrentStrength = PlayerTemplate.Strength;
                CurrentPower = PlayerTemplate.Power;
                CurrentDefense = PlayerTemplate.Defense >= 0 ? PlayerTemplate.Defense : -1;
                CurrentFirstStrikeInvestigation = PlayerTemplate.FirstStrikeInvestigation;
                CurrentFirstStrikeStrength = PlayerTemplate.FirstStrikeStrength;
            }

            // 注意：结附关系(AttachedCards/AttachedTo)与封印关系(SealedCards/SealedBy)的清除
            // 在此【不再处理】——其单一真相源是 ZoneManager 的 detach 逻辑（离开区域时按规则处置），
            // 本方法只负责卡自身状态字段的清零，避免与 ZoneManager 重复管理关系造成漂移。
            // 若未来出现"不经由 ZoneManager 移动"的特殊入口，需在此补兜底，但目前所有离场均走 ZoneManager。

            // 重置区域（由调用者设置）
            // CurrentZone 和 ZoneDetailId 由调用者设置
        }
    }
}