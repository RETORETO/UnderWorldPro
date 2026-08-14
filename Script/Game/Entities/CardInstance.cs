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
        public void BumpVersion() => VersionId++;

        // ---------- 所属与控制 ----------
        public int OwnerId { get; set; }
        public int ControllerId { get; set; }

        // ---------- 区域信息 ----------
        public CardZone CurrentZone { get; set; } = CardZone.Deck;
        public string ZoneDetailId { get; set; } // 如地区ID、附属目标UID等

        // ---------- 动态能力值（当前实际值） ----------
        public int CurrentInvestigation { get; set; }
        public int CurrentCombat { get; set; }
        public int CurrentPower { get; set; }
        public int CurrentDefense { get; set; }

        // ---------- 魔法领域（实例层，JC060 光环可运行时授予） ----------
        // 初始化时从模板静态 MagicDomains 拷贝；运行时授予（如 JC060 给无领域角色加"心"）经 GrantMagicDomain 追加。
        // 规则/UI 判断"是否具有某领域图标"统一读此集合（而非仅模板），确保光环授予后对其他效果可见。
        public List<MagicDomain> MagicDomains { get; } = new List<MagicDomain>();
        public bool HasMagicDomain(MagicDomain d) => MagicDomains.Contains(d);

        // ---------- 先手能力值（当前实际值，含持续效果加成） ----------
        // 白眼=先手调查能力：持续效果满足前提时，每"白眼"给 FirstStrikeInvestigation +1（对应卡文"白眼白眼"叠加）。
        public int CurrentFirstStrikeInvestigation { get; set; }
        public int CurrentFirstStrikeCombat { get; set; }

        // ---------- 静止/禁止标记（D19-⑤） ----------
        // 与持续关键字授予(_granted*)分离：静止效果是规则约束（不能被装备结附等），常驻、不授出能力。
        private HashSet<StaticKind> _staticFlags = new HashSet<StaticKind>();
        public IReadOnlyCollection<StaticKind> StaticFlags => _staticFlags;
        public bool HasStaticFlag(StaticKind flag) => _staticFlags.Contains(flag);
        public void AddStaticFlag(StaticKind flag) => _staticFlags.Add(flag);
        public void RemoveStaticFlag(StaticKind flag) => _staticFlags.Remove(flag);

        // ---------- 状态标记 ----------
        public bool IsTapped { get; set; }
        public bool IsSealed { get; set; }
        public bool IsAttached { get; set; }
        public bool IsSeal { get; set; }   // 本卡是否为"封印卡"（封印了别的对象），镜像 IsAttached
        public bool HasGuard { get; set; }
        public bool IsCovert { get; set; }        // 暗藏状态(背面朝上,场上暗藏者)
        public bool ExtraCostPaid { get; set; }   // 额外费用是否已支付(结算时玩家选择)

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

        // ---------- 持续/修正效果动态授予（由 EffectEngine 持续注册表增删，离场/封印时还原） ----------
        // 与基础能力值(Current*)分离：此处记录"由效果赋予的"关键字/图标/标志属性，便于精确还原。
        private HashSet<string> _grantedKeywords = new HashSet<string>();   // 关键字：公开、先手、调查…
        private HashSet<string> _grantedIcons = new HashSet<string>();      // 图标：领域图标（心心/白眼/黑刀…）
        private Dictionary<string, int> _grantedAttributes = new Dictionary<string, int>(); // 标志属性增益计数
        private HashSet<MagicDomain> _grantedDomains = new HashSet<MagicDomain>(); // 运行时授予的魔法领域（JC060 光环等），对称 _grantedAttributes

        public IReadOnlyCollection<string> GrantedKeywords => _grantedKeywords;
        public IReadOnlyCollection<string> GrantedIcons => _grantedIcons;
        public IReadOnlyDictionary<string, int> GrantedAttributes => _grantedAttributes;

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
                    // 黑刀=战斗：每"黑刀"(Attribute="战斗")给基础战斗+1（JC060 光环，对称调查做法；实际结算经 AreaStatsCalculator 读 CurrentCombat）
                    else if (value == "战斗") CurrentCombat += amount;
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
                    // 运行时子类型（如 JC030 授予"吸血鬼"）：写入 CurrentSubCategories（当前实际子类型集合）
                    CurrentSubCategories.Add(value);
                    BumpVersion();   // 子类别变化影响目标合法性 → 版本号失效
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
                    if (_grantedAttributes.ContainsKey(value))
                    {
                        _grantedAttributes[value] -= amount;
                        if (_grantedAttributes[value] <= 0) _grantedAttributes.Remove(value);
                    }
                    // 白眼=先手调查：撤销时回退先手调查
                    if (value == "先手调查") CurrentFirstStrikeInvestigation -= amount;
                    // 黑眼=调查：撤销时回退基础调查
                    else if (value == "调查") CurrentInvestigation -= amount;
                    // 黑刀=战斗：撤销时回退基础战斗
                    else if (value == "战斗") CurrentCombat -= amount;
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
                    // 撤销时回退：仅当该子类型非模板固有（避免误删静态子类型）才从 CurrentSubCategories 移除
                    if (!HasStaticSubCategory(value)) CurrentSubCategories.Remove(value);
                    BumpVersion();   // 子类别撤销影响目标合法性 → 版本号失效
                    break;
            }
        }

        // ---------- 伤害/创伤 ----------
        private int _damage = 0;
        public int Damage
        {
            get => _damage;
            private set => _damage = Math.Max(0, value);
        }
        private int _trauma = 0;
        public int Trauma
        {
            get => _trauma;
            private set => _trauma = Math.Max(0, value);
        }

        // ---------- 关联关系 ----------
        public CardInstance AttachedTo { get; set; }
        public List<CardInstance> AttachedCards { get; set; } = new List<CardInstance>();
        public CardInstance SealedBy { get; set; }
        public List<CardInstance> SealedCards { get; set; } = new List<CardInstance>();

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
                CurrentCombat = PlayerTemplate.Combat;
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
            CurrentInvestigation = PlayerTemplate.GetTotalInvestigation(isFirstPlayer);
            CurrentCombat = PlayerTemplate.GetTotalCombat(isFirstPlayer);
            CurrentPower = PlayerTemplate.GetTotalPower(isFirstPlayer);
            CurrentDefense = PlayerTemplate.Defense >= 0 ? PlayerTemplate.Defense : -1;
            // 黑眼=调查：基础调查 + 当前已授予的"调查"属性（不受先手限制，保证重算后持续效果不丢失）
            CurrentInvestigation += GetAttribute("调查");
            // 黑刀=战斗：基础战斗 + 当前已授予的"战斗"属性（JC060 光环 +1 战斗，对称调查做法，保证重算后不丢失）
            CurrentCombat += GetAttribute("战斗");
            // 先手值：基础 FirstStrike + 当前已授予的"先手调查"属性（白眼叠加），保证重算后持续效果不丢失
            int whiteEyes = GetAttribute("先手调查");
            CurrentFirstStrikeInvestigation = PlayerTemplate.FirstStrikeInvestigation + whiteEyes;
            CurrentFirstStrikeCombat = PlayerTemplate.FirstStrikeCombat;
            // 实例魔法领域：模板固有（过滤-1哨兵）+ 运行时授予（_grantedDomains，JC060 光环等）两部分。
            // 对称 JC001 白眼机制：重算时从"模板固有 + 授予集合"直接重建，不依赖反推，避免每回合先手重算冲刷光环授予的域。
            MagicDomains.Clear();
            if (PlayerTemplate.MagicDomains != null)
                foreach (var d in PlayerTemplate.MagicDomains)
                    if ((int)d != -1) MagicDomains.Add(d); // B1：-1哨兵归一化为空列表（无领域）
            foreach (var d in _grantedDomains) // 运行时授予：来自光环，自然不含-1，叠加回最终列表
                if (!MagicDomains.Contains(d)) MagicDomains.Add(d);
        }

        public bool HasDefense => PlayerTemplate != null && PlayerTemplate.Defense >= 0;

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
        // 注：原"被封印(IsSealed)的卡不可加指示物"规则已上移至 Controller 层
        //     （CardManager.AddCounter），本类只负责字典增删。
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
            IsTapped = false;
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
                CurrentCombat = PlayerTemplate.Combat;
                CurrentPower = PlayerTemplate.Power;
                CurrentDefense = PlayerTemplate.Defense >= 0 ? PlayerTemplate.Defense : -1;
                CurrentFirstStrikeInvestigation = PlayerTemplate.FirstStrikeInvestigation;
                CurrentFirstStrikeCombat = PlayerTemplate.FirstStrikeCombat;
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
