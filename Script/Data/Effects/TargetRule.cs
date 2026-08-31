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
        // —— 以下为目标动态属性筛选类（候选合法性用；结算前"属性变化"丢失已由版本号方案统一覆盖，无需快照比对）——
        // 注意：2026-08-11 起，原 ControllerChangedToEnemy/Friendly、SubCategoryGained/Lost 等"结算前变化"类枚举已删除——
        // 其语义（控制者/子类别变化即判丢失）现由 D16 版本号方案统一覆盖：任何控制者/子类别变化都会 BumpVersion ，
        // 结算时比对入栈 TargetVersionId 不一致即判丢失，方向性记忆不再单独维护（无需目标相关总线）。
        CombatCapable,      // 目标【当前拥有战力】(即 Strength 属性，可参与战斗对抗)。
                            // 用途示例："目标带战力的角色"。候选合法性 + 结算前丢失均可复用。
                            // 【逻辑暂缓未实现】：需在 CardManager.MatchesRule 加此 case，
                            // 读 CardInstance 的战力字段（参考 CostAtLeast 写法，读 PlayerTemplate.Strength/CurrentStrength），
                            // 满足"有战力"才通过；结算前若失去战力（版本号方案会判丢失）无需额外处理。
        AttachedToCharacterIfNoMind, // 条件式：资产区【无】【心】(心灵领域忠诚≥1) 时，目标须结附于角色；
                            // 有【心】时此限制不生效（放行任意附属）。用于 JC005 裂解术：
                            // "消灭目标结附于角色的附属；若资产区有【心】则改为消灭目标附属"。
                            // 仅在【取目标(打出)阶段】评估（结算时不复检，见 TargetValidity.Check 规则）。
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
