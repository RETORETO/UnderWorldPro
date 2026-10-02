using Godot;
using System.Collections.Generic;
using Script.Enums;

namespace Script.Data.Effects
{
    /// <summary>
    /// 目标限定条件枚举（精筛级别，可无限扩展）。
    /// 与 TargetKind（粗筛大类）配合使用：一条 TargetRule = 一个大类(TargetKind) + 0..N 个限定条件(Restrictions)。
    /// evaluator（CardManager.MatchesRule）对每条 rule 先粗筛大类，再要求所有 Restrictions 都满足(AND)。
    /// 新增目标种类时：只需在此加枚举值 + MatchesRule 加一个 case，无需改动任何单卡逻辑（数据驱动）。
    /// </summary>
    public enum TargetRestriction
    {
        None,               // 无附加条件（仅按大类）
        Friendly,           // 目标控制者 == 来源方（打出者/激活者）
        Enemy,              // 目标控制者 != 来源方
        StaticSubCategory,  // 目标【静态模板】SubCategories 含 Param（"印刷类别"）
        CurrentSubCategory, // 目标【当前实际】SubCategories 含 Param（含运行时授予）
        CardName,           // 目标【静态】CardName == Param
        SubCardName,        // 目标【静态】SubCardName == Param（子卡名，如"圣甲虫产业"）
        LocalZone,          // 目标须位于【来源所在的本地区】(AREASLOT 相同)；来源/目标任一移动即丢失(规则⑥)
        // —— 以下为静态属性筛选类（候选合法性 + 结算前丢失均可复用，无需快照比对）——
        NotStaticSubCategory, // 目标【静态模板】SubCategories 不含 Param（"非某类别"，如"非人类"）：含该子类别即不通过
        CostAtLeast,        // 目标【静态印刷费用】PlayerTemplate.Cost >= int.Parse(Param)（如"3"）：低于门槛即不通过
        // ---- 【P1】新增精筛条件（均为静态/当前属性条件，故取目标阶段与结算复检都评估）----
        CostAtMost,         // 目标【静态印刷费用】<= Param 门槛（如"2"）。Param 可单独写，也可按 '|' 分段
        Tapped,             // 目标当前【已横置】（如 JC010「消灭目标已横置的角色」）
        HasMagicDomainIcon, // 目标【具有魔法领域图标】（实例 MagicDomains 非空，回退模板；-1 哨兵=无领域，如 JC006）
        Organization,       // 目标【静态】Organization == Param（阵营/组织筛选，如"王座会""国家机构""鸣钟教派"）
        HasKeyword,         // 目标当前具有 Param 关键字（运行时授予 ∪ 模板固有，语义同 HasPublicKeyword）
        // —— 以下为目标动态属性筛选类（候选合法性用；结算前"属性变化"丢失已由版本号方案统一覆盖，无需快照比对）——
        // 注意：2026-08-11 起，原 ControllerChangedToEnemy/Friendly、SubCategoryGained/Lost 等"结算前变化"类枚举已删除——
        // 其语义（控制者/子类别变化即判丢失）现由 D16 版本号方案统一覆盖：任何控制者/子类别变化都会 BumpVersion ，
        // 结算时比对入栈 TargetVersionId 不一致即判丢失，方向性记忆不再单独维护（无需目标相关总线）。
        CombatCapable,      // 目标【当前拥有战力】(即 Strength 属性，可参与战斗对抗)。
                            // 用途示例："目标带战力的角色"。候选合法性 + 结算前丢失均可复用。
                            // 【逻辑暂缓未实现】：需在 CardManager.MatchesRule 加此 case，
                            // 读 CardInstance 的战力字段（参考 CostAtLeast 写法，读 PlayerTemplate.Strength/CurrentStrength），
                            // 满足"有战力"才通过；结算前若失去战力（版本号方案会判丢失）无需额外处理。
        AttachedToCharacterIfNoMind, // 条件式：资产区【无】【心】(心灵领域忠诚≥1) 时，目标须结附于角色；                            // 有【心】时此限制不生效（放行任意附属）。用于 JC005 裂解术：
                            // "消灭目标结附于角色的附属；若资产区有【心】则改为消灭目标附属"。
                            // 仅在【取目标(打出)阶段】评估（结算时不复检，见 TargetValidity.Check 规则）。
        /// <summary>
        /// 【P3】目标须是**结附于秘社**的附属（`AttachedTo` 是秘社实例）。
        /// 用途：JC043 纵火狂「消灭目标**结附于秘社**或**本地区**上的附属」——与 `TargetSpec.SpecAlt` 组合表达 OR
        /// （另一条用 `LocalZone`＝结附于本地区）。
        /// </summary>
        HostIsCouncil,
        /// <summary>
        /// 【P3】目标须是**直接结附于地区**的附属（无宿主：`AttachedTo == null`，挂在地区容器上）。
        /// 与 `LocalZone` 组合即"结附于**本地区**的附属"；与 `HostIsCouncil` 一起用于 JC043 的两条 OR。
        /// 注：结附于**角色**的附属其 `ZoneDetailId` 也是宿主所在地区，故单靠 `LocalZone` 无法区分
        /// "结附于地区"与"结附于该地区的角色"——必须另有本限制。
        /// </summary>
        AttachedToArea,
        /// <summary>
        /// 【P3·K21】**目标须带有指定卡上标志**（`Param` = 标志名，如 `"锁定标志"`）——判定 `卡.Counters[标志名] ≥ 1`。
        /// 用途：JC069 警犬「移动到一个具有**锁定标志**的角色/暗藏者所在的地区」；
        /// JC122/JC123/JC124 的"具有锁定标志/哲人石标志的角色"同款筛选亦可复用。
        /// 口径：只支持"至少一个"（数量门槛形式尚未登记）；**运行时标志**（`AddCounter`/`RemoveCounter` 即时生效），
        /// 与 `TargetValidity.CheckRestrictions` 单一真相源共用 → 取目标与结算复检同一判定。
        /// 注：卡上标志的增减**不**触发 `BumpVersion`，故"结算前标志被移除"由结算复检（同一 `CheckRestrictions`）兜住。
        /// </summary>
        HasCounter,
        /// <summary>
        /// 【秘社批次·MSJC011②】目标须**带有指定能力图标**（`Param` = 图标名，当前只登记 `"黑刀"`）。
        /// **用户 2026-09 口径**："能力图标即为『调查/战力/势力 ＋ 三个先手值』的统称，调查/战力/势力能力都来源于
        /// 能力图标 ⇒ **可以按照战力 &gt; 0 判断（注意不要计算先手战力）**"。
        /// 判定：`Param == "黑刀"` ⇒ `目标当前战力 &gt; 0`（读 `CurrentStrength`，含光环/授予带来的战力，**不含先手战力**）；
        /// 未登记的图标名 → **fail-closed 不通过并打日志**（防拼错静默放行）。
        /// </summary>
        HasAbilityIcon,
        /// <summary>
        /// 【P3·K4】**目标【当前实际】SubCategories 不含 Param**（"非某类别"的**实例口径**）。
        /// 与 `NotStaticSubCategory` 的区别＝**读当前集合还是印刷模板**：
        /// 用户 2026-09 口径（JC036 初拥"将该角色的人类类别改为吸血鬼类别"）——
        /// 「**印刷类别**为人类」读**模型数据**（`StaticSubCategory`）；卡文只写"人类 / 非人类"的
        /// 一律读**实例当前类别**（`CurrentSubCategory` / 本枚举），故被初拥改写的角色对
        /// "非人类"类效果**合法**、对"人类角色"类效果**不再合法**。
        /// 用途：JC015 驱魔人「消灭目标非人类且印刷费用≥3的敌方角色」/ JC019 弥尔顿的镇魔枪「对位于本地区的目标非人类角色…」。
        /// 与 `CostAtLeast` 共用 `Param` 时按 '|' 分段（第 0 段＝本限制的子类别名）。
        /// </summary>
        NotCurrentSubCategory,
        // 未来扩展位（按需添加 case）：Tapped, Untapped, HasKeyword, HasAttribute, LoyaltyColor, Domain, StrengthAtLeast...
    }

    /// <summary>
    /// 目标描述统一接口（目标系统·丢失规则，2026-08-07）。
    /// 让玩家卡 TargetRule 与效果系统 TargetSpec 共用同一套丢失校验链（TargetValidity），
    /// 无需关心数据来自哪套系统。checker 只需此最小信息。
    /// </summary>
    public interface ITargetDesc
    {
        TargetKind Kind { get; }
        /// <summary>【K14·或种类】第二个可接受的大类（`None` = 无）。卡文"目标附属**或**资产"这类**单目标二选一**用它表达：
        /// 与 `Kind` 构成 OR（其余限制条件对两个大类同样适用）。打出规则的多条 `TargetRule` 是另一条 OR 通路。</summary>
        TargetKind KindAlt { get; }
        bool NeedsSelection { get; }
        /// <summary>精筛条件列表（全部满足才通过）。空=仅按大类。校验链据此推导 IsLocalZone 等静态/位置类判定。</summary>
        Godot.Collections.Array<TargetRestriction> Restrictions { get; }
        /// <summary>条件参数（子类型名/卡名/数值等，配合带参 Restrictions 使用，如 CostAtLeast 的门槛值）。</summary>
        string Param { get; }
        /// <summary>是否限定"本地区"：目标须与来源同 AREASLOT（规则⑥）。由 Restrictions.Contains(LocalZone) 推导。</summary>
        bool IsLocalZone { get; }
        /// <summary>是否同时接受"暗藏者"作为合法目标（与 Kind=Character 组合表达"角色或暗藏者"）。
        /// 设 true 时 Character 校验允许 IsCovert 任一状态（角色/暗藏者均合法）。翻面仍按规则③丢失。</summary>
        bool IncludeCovert { get; }
    }

    /// <summary>
    /// 一条目标规则（声明式，可在 tres Inspector 直接配置，也可在 cs 里构造）。
    /// 一条规则 = 一个粗筛大类(Kind) + 任意个精筛条件(Restrictions, AND 关系)。
    /// 卡牌的合法目标 = 满足【任一】TargetRule 即可（OR 关系，见 CardManager.IsLegalPlayTarget）。
    /// 实现 ITargetDesc：IsLocalZone 由 Restrictions 推导。
    /// </summary>
    [GlobalClass]
    public partial class TargetRule : Resource, ITargetDesc
    {
        /// <summary>粗筛大类（角色/附属/秘社/地区/暗藏者…）。</summary>
        [Export] public TargetKind Kind { get; set; } = TargetKind.None;

        /// <summary>精筛条件列表（全部满足才通过）。空=仅按大类。</summary>
        [Export] public Godot.Collections.Array<TargetRestriction> Restrictions { get; set; }
            = new Godot.Collections.Array<TargetRestriction>();

        /// <summary>条件参数（子类型名/卡名/子卡名等，配合 Restrictions 使用）。</summary>
        [Export] public string Param { get; set; } = "";

        /// <summary>是否同时接受"暗藏者"作为合法目标（与 Kind=Character 组合表达"角色或暗藏者"）。
        /// 设 true 时 Character 校验允许 IsCovert 任一状态（角色/暗藏者均合法）。翻面仍按规则③丢失。</summary>
        [Export] public bool IncludeCovert { get; set; } = false;

        // ---- ITargetDesc 实现（玩家卡单目标，固定语义）----
        bool ITargetDesc.NeedsSelection => Kind != TargetKind.None;
        TargetKind ITargetDesc.KindAlt => TargetKind.None;   // 打出规则以"多条 TargetRule"表达 OR，不使用 KindAlt
        bool ITargetDesc.IsLocalZone => Restrictions.Contains(TargetRestriction.LocalZone);
        bool ITargetDesc.IncludeCovert => IncludeCovert;

        /// <summary>便捷构造（cs 内生成规则用）。</summary>
        public static TargetRule Make(TargetKind kind, TargetRestriction restriction = TargetRestriction.None, string param = "")
        {
            var r = new TargetRule();
            r.Kind = kind;
            if (restriction != TargetRestriction.None)
                r.Restrictions.Add(restriction);
            r.Param = param;
            return r;
        }
    }
}
