// Script/Data/Cards/CouncilExceptionRule.cs
using Godot;
using Godot.Collections;
using Script.Enums;

namespace Script.Data.Cards
{
    /// <summary>
    /// 【秘社构筑限制·例外条目】<see cref="CouncilRestriction"/> 白名单模式（`UseWhitelist = true`）下，
    /// "只能包含…**以及其他派系的…**"里那个"以及"的部分。
    ///
    /// 语义：本条目描述一类**虽然不在派系白名单里、但仍允许放进牌组**的牌。
    /// 多条 `ExceptionRules` 之间是 **OR**（命中任一条即豁免）。
    ///
    /// 判定全部是**正向 AND**（同一条目内各 `FilterByXxx` 必须同时满足），
    /// 与 `CouncilRestriction` 旧有的筛选口径保持同一套词汇与同一实现（见 `CouncilRestriction.MatchesFilter`）。
    ///
    /// 覆盖（用户 2026-09 口径）：
    ///  · MSJC010 貘组：`FilterByDomain = true, TargetDomain = Star`（其他派系【星】领域的牌）；
    ///  · MSJC011 S．P．T执行部：`类别=角色` + `子类别=人类` + `战力&gt;0`（带［黑刀］人类角色）；
    ///  · MSJC012 爱弗罗德家族：`类别=角色` + `子类别={吸血鬼,奴仆}`（OR）；
    ///  · MSJC014 哨兵：`RequireNoDomain = true`（不具有魔法领域图标的牌）；
    ///  · MSJC016 颂亡者：`FilterByDomain = true, TargetDomain = Death`（其他派系【死】领域的牌）；
    ///  · MSJC017 梦魇族群：`关键字=灵体 ∧ 类别=角色` **或** `子类别=梦境`（两条 OR）。
    /// </summary>
    [GlobalClass]
    public partial class CouncilExceptionRule : Resource
    {
        /// <summary>按卡类别筛选（`TargetCategoryFlag` 位掩码任一命中即算命中）。</summary>
        [Export] public bool FilterByCategory { get; set; }
        [Export] public CardCategory TargetCategoryFlag { get; set; }

        /// <summary>按子类别筛选——**任一命中**即算命中（OR 语义，用于"吸血鬼**或**奴仆角色牌"）。
        /// 复数版本与 `CouncilRestriction.TargetSubCategory`（单数）用途相同，新数据一律用本字段。</summary>
        [Export] public bool FilterBySubCategories { get; set; }
        [Export] public Array<string> TargetSubCategories { get; set; } = new Array<string>();

        /// <summary>按魔法领域筛选（任一命中即算命中）。</summary>
        [Export] public bool FilterByDomain { get; set; }
        [Export] public Array<MagicDomain> TargetDomains { get; set; } = new Array<MagicDomain>();

        /// <summary>
        /// 【"**不具有**魔法领域图标"】置 true ⇒ 本条目要求该牌**没有任何魔法领域**才命中（MSJC014 哨兵）。
        /// 与 `FilterByDomain` 互斥语义（一个要求有、一个要求没有）；两者同时为 true 时**永远不命中**（fail-closed）。
        /// 注：无领域卡在 `.tres` 里存的是哨兵值 `[-1]`，`CouncilRestriction.HasNoMagicDomain` 会把哨兵值也算"无领域"。
        /// </summary>
        [Export] public bool RequireNoDomain { get; set; }

        /// <summary>按关键字筛选（运行时授予 ∪ 模板固有声明，与 `DeckSearchFilter` 的 `关键字:` 同源判定）。</summary>
        [Export] public bool FilterByKeyword { get; set; }
        [Export] public string TargetKeyword { get; set; } = "";

        /// <summary>
        /// 按**能力图标**筛选——**用户 2026-09 口径**：
        /// "能力图标即为『调查/战力/势力 ＋ 三个先手值』的统称……**可以按照战力&gt;0 判断（注意不要计算先手战力）**"。
        /// 故本字段取值只认 **`黑刀`**：命中条件＝`PlayerCardData.Strength &gt; 0`（**不含** `FirstStrikeStrength`）。
        /// ⚠ 不要在这里引入 `Card.xml` 的 `&lt;pt&gt;` 解析或图标查表——数据层里根本没有图标标签（只有数值），
        /// 且 `Card.xml` 只覆盖 216/698 张；按用户口径直接用战力数值判定，是**唯一**与数据一致的做法。
        /// </summary>
        [Export] public bool FilterByAbilityIcon { get; set; }
        [Export] public string TargetAbilityIcon { get; set; } = "";
    }
}
