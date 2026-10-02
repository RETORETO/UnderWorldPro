// Script/Data/Cards/CouncilRestriction.cs
using Godot;
using Godot.Collections;
using System.Collections.Generic;
using Script.Enums;

namespace Script.Data.Cards
{
    /// <summary>
    /// 【秘社构筑限制】一条构筑限制的声明式表达，支持两种模式：
    ///
    /// **① 数量要求模式（旧，`UseWhitelist = false`，保持原样不动）**
    ///   「需包含 ≥N 张满足筛选条件的牌」——`FilterByXxx`（AND）＋ `Comparison` ＋ `RequiredCount`。
    ///   现有 MSJC001~008 的 `.tres` 走这条路径，语义与实现**完全未变**。
    ///
    /// **② 白名单模式（新，`UseWhitelist = true`，用户 2026-09 口径）**
    ///   「只能包含…以及其他派系的…」/「非X且非中立的牌不能超过 N 张」：
    ///     · `AllowedColors`＝派系白名单（**"中立"＝`CardColor.Brown`**，用户确认）；
    ///     · 一张牌**合法** ⟺ `该牌 Colors 中至少有一个颜色 ∈ AllowedColors`（用户 2026-09 口径：
    ///       "只要含白名单颜色就合法"——**不是**要求全部颜色都在白名单里）；
    ///       或 `命中 ExceptionRules 中任一条`（OR）；
    ///     · 上述都不满足的牌＝**违规牌**，其总数必须 `&lt;= ExceptionMaxCount`：
    ///       010/011/012/014/016/017 的 `ExceptionMaxCount = 0`（一个都不许）；
    ///       013 计时者 / 015 隐士 的 `ExceptionMaxCount = 12`（"非【红】且非【中立】…不能超过 12 张"）。
    ///
    /// **`GetActualCount` 在两种模式下的含义**（用户 2026-09 口径）：
    ///   · 数量要求模式 → 满足筛选条件的**张数**；
    ///   · 白名单模式   → **违规张数**（供 UI 直接显示"当前 7 张 / 上限 12 张"）。
    /// </summary>
    [GlobalClass]
    public partial class CouncilRestriction : Resource
    {
        // ==================== ① 数量要求模式（原有字段，语义未变） ====================

        [Export] public bool FilterByColor { get; set; }
        [Export] public CardColor TargetColor { get; set; } = CardColor.Brown;

        [Export] public bool FilterByCategory { get; set; }
        [Export] public CardCategory TargetCategoryFlag { get; set; }

        [Export] public bool FilterBySubCategory { get; set; }
        [Export] public string TargetSubCategory { get; set; }

        [Export] public bool FilterByOrganization { get; set; }
        [Export] public string TargetOrganization { get; set; }

        [Export] public bool FilterByStrength { get; set; }
        [Export] public int MinStrength { get; set; } = 0;

        [Export] public bool FilterByInvestigation { get; set; }
        [Export] public int MinInvestigation { get; set; } = 0;

        [Export] public bool FilterByDomain { get; set; }
        [Export] public MagicDomain TargetDomain { get; set; } = MagicDomain.Mind;

        [Export] public ComparisonType Comparison { get; set; } = ComparisonType.GreaterOrEqual;
        [Export] public int RequiredCount { get; set; }

        // ==================== ② 白名单模式（新增，用户 2026-09 口径） ====================

        /// <summary>启用"只能包含…"白名单模式。`false`（默认）＝ 完全走①的旧逻辑（向后兼容）。</summary>
        [Export] public bool UseWhitelist { get; set; }

        /// <summary>派系颜色白名单。**"中立"＝<see cref="CardColor.Brown"/>**（用户 2026-09 确认；
        /// `PlayerCardData.Colors` 的注释原文即"默认棕色（中立）"）。</summary>
        [Export] public Array<CardColor> AllowedColors { get; set; } = new Array<CardColor>();

        /// <summary>"不在白名单内、也没命中例外"的牌**允许的张数上限**。
        /// `0`（默认）＝ 一个都不许（010/011/012/014/016/017）；`12` ＝ 013/015。</summary>
        [Export] public int ExceptionMaxCount { get; set; }

        /// <summary>例外清单，**OR** 语义：命中任一条即视为合法（"以及其他派系的…"）。</summary>
        [Export] public Array<CouncilExceptionRule> ExceptionRules { get; set; } = new Array<CouncilExceptionRule>();

        public enum ComparisonType
        {
            GreaterOrEqual,
            LessOrEqual,
            Equal
        }

        // ==================== 判定 ====================

        public bool IsSatisfiedBy(System.Collections.Generic.IEnumerable<PlayerCardData> deck)
        {
            // 白名单模式：判据是"**违规张数 ≤ ExceptionMaxCount**"（与 `Comparison`/`RequiredCount` 无关）。
            // 旧的"数量要求"比较关系只在 `UseWhitelist == false` 下生效（向后兼容）。
            if (UseWhitelist) return CountViolations(deck) <= ExceptionMaxCount;

            return GetActualCount(deck) is var count && Comparison switch
            {
                ComparisonType.GreaterOrEqual => count >= RequiredCount,
                ComparisonType.LessOrEqual => count <= RequiredCount,
                ComparisonType.Equal => count == RequiredCount,
                _ => false
            };
        }

        /// <summary>
        /// 统计套牌中的数量（供校验明细展示用）。含义随模式而变（用户 2026-09 口径）：
        ///  · 数量要求模式 → 满足本限制全部筛选条件的**张数**；
        ///  · 白名单模式   → **违规张数**（既不在 `AllowedColors` 内、又没命中任何例外）。
        /// </summary>
        public int GetActualCount(System.Collections.Generic.IEnumerable<PlayerCardData> deck)
        {
            if (UseWhitelist) return CountViolations(deck);

            int count = 0;
            if (deck == null) return 0;
            foreach (var card in deck)
            {
                if (card == null) continue;
                if (MatchesFilter(card)) count++;
            }
            return count;
        }

        /// <summary>旧（数量要求）模式的筛选判定：各 `FilterByXxx` 之间为 AND。公开给 `CouncilExceptionRule` 复用。</summary>
        public bool MatchesFilter(PlayerCardData card)
        {
            if (card == null) return false;
            if (FilterByColor && !card.Colors.Contains(TargetColor)) return false;
            if (FilterByCategory && !card.CategoryFlag.HasFlag(TargetCategoryFlag)) return false;
            if (FilterBySubCategory && !HasSubCategory(card, TargetSubCategory)) return false;
            if (FilterByOrganization && card.Organization != TargetOrganization) return false;
            if (FilterByStrength && card.Strength < MinStrength) return false;
            if (FilterByInvestigation && card.Investigation < MinInvestigation) return false;
            if (FilterByDomain && !HasMagicDomain(card, TargetDomain)) return false;
            return true;
        }

        /// <summary>白名单模式下：牌是否**合法**（含白名单颜色 **或** 命中任一条例外）。</summary>
        public bool IsAllowedInWhitelist(PlayerCardData card)
        {
            if (card == null) return false;
            // 用户 2026-09 口径："只要含白名单颜色就合法"（任一命中即可，不要求全部颜色都在白名单里）。
            if (AllowedColors != null && card.Colors != null)
                foreach (var c in card.Colors)
                    if (AllowedColors.Contains(c)) return true;

            if (ExceptionRules != null)
                foreach (var r in ExceptionRules)
                    if (r != null && MatchesException(r, card)) return true;
            return false;
        }

        /// <summary>白名单模式下：统计**违规张数**（不合法＝既不在白名单、又没命中例外）。</summary>
        public int CountViolations(System.Collections.Generic.IEnumerable<PlayerCardData> deck)
        {
            int count = 0;
            if (deck == null) return 0;
            foreach (var card in deck)
            {
                if (card == null) continue;
                if (!IsAllowedInWhitelist(card)) count++;
            }
            return count;
        }

        // ==================== 例外条目判定（单一实现） ====================

        /// <summary>一条例外是否命中该牌。各 `FilterByXxx` 之间为 AND；`FilterBySubCategories` / `TargetDomains` 内部为 OR。</summary>
        public static bool MatchesException(CouncilExceptionRule r, PlayerCardData card)
        {
            if (r == null || card == null) return false;

            if (r.FilterByCategory && !card.CategoryFlag.HasFlag(r.TargetCategoryFlag)) return false;

            if (r.FilterBySubCategories)
            {
                bool hit = false;
                if (r.TargetSubCategories != null)
                    foreach (var s in r.TargetSubCategories)
                        if (HasSubCategory(card, s)) { hit = true; break; }
                if (!hit) return false;
            }

            if (r.FilterByDomain)
            {
                bool hit = false;
                if (r.TargetDomains != null)
                    foreach (var d in r.TargetDomains)
                        if (HasMagicDomain(card, d)) { hit = true; break; }
                if (!hit) return false;
            }

            // "不具有魔法领域图标"：与 FilterByDomain 同时为 true ⇒ 永远不命中（fail-closed）。
            if (r.RequireNoDomain && (r.FilterByDomain || !HasNoMagicDomain(card))) return false;

            if (r.FilterByKeyword && !HasKeyword(card, r.TargetKeyword)) return false;

            if (r.FilterByAbilityIcon && !MatchesAbilityIcon(r.TargetAbilityIcon, card)) return false;

            return true;
        }

        /// <summary>
        /// 【能力图标】用户 2026-09 口径："能力图标即为『调查/战力/势力 ＋ 三个先手值』的统称，
        /// 调查/战力/势力能力都来源于能力图标 ⇒ **可以按照战力&gt;0 判断（注意不要计算先手战力）**"。
        /// 故这里只认 **［黑刀］**（＝战力图标）：命中 ⟺ `Strength &gt; 0`，**明示不含 `FirstStrikeStrength`**。
        /// 未登记（拼错）的图标名 → 不命中并打日志（fail-closed，避免静默放行）。
        /// </summary>
        public static bool MatchesAbilityIcon(string icon, PlayerCardData card)
        {
            if (card == null) return false;
            switch ((icon ?? "").Trim())
            {
                case "黑刀":
                    return card.Strength > 0;      // ⚠ 不含 FirstStrikeStrength（先手战力）——用户口径
                default:
                    GD.Print($"[CouncilRestriction] ⚠ 未登记的能力图标筛选「{icon}」→ 视为不命中（fail-closed）");
                    return false;
            }
        }

        // ==================== 字段判定辅助（把哨兵值等细节收在一处） ====================

        /// <summary>子类别命中（大小写不敏感、容忍首尾空格；空串不命中）。</summary>
        public static bool HasSubCategory(PlayerCardData card, string sub)
        {
            if (card?.SubCategories == null || string.IsNullOrWhiteSpace(sub)) return false;
            string want = sub.Trim();
            foreach (var s in card.SubCategories)
                if (string.Equals((s ?? "").Trim(), want, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>魔法领域命中。**无领域卡在 `.tres` 里存的是哨兵值 `[-1]`** ⇒ 该值不算任何领域。</summary>
        public static bool HasMagicDomain(PlayerCardData card, MagicDomain domain)
        {
            if (card?.MagicDomains == null || (int)domain < 0) return false;
            foreach (var d in card.MagicDomains)
                if ((int)d == (int)domain) return true;
            return false;
        }

        /// <summary>"不具有魔法领域图标"＝ `MagicDomains` 为空 **或** 只含哨兵值负数（无领域卡的实际存储形态）。</summary>
        public static bool HasNoMagicDomain(PlayerCardData card)
        {
            if (card?.MagicDomains == null || card.MagicDomains.Count == 0) return true;
            foreach (var d in card.MagicDomains)
                if ((int)d >= 0) return false;
            return true;
        }

        /// <summary>关键字命中：与 `DeckSearchFilter` 的 `关键字:` 同源判定（模板固有声明通道）。
        /// 构筑期只看模板（`PlayerCardData`），故走 `HasInherentKeyword`。</summary>
        public static bool HasKeyword(PlayerCardData card, string kw)
        {
            if (card == null || string.IsNullOrWhiteSpace(kw)) return false;
            return card.HasInherentKeyword(kw.Trim());
        }

        // ==================== 文案 ====================

        /// <summary>
        /// 生成可读的构筑限制文案（供"卡组违规"提示与构筑界面展示）。
        /// · 数量要求模式：`需包含 25 张或更多黄色牌`（传入 actualCount 时附加"（当前 N 张）"）；
        /// · 白名单模式：`只能包含 黄色/中立 派系的牌，以及其他派系：领域为 星空 的牌（当前 2 张，上限 0 张）`
        ///   或 `非 红色 且非 中立 派系的牌不能超过 12 张（当前 7 张）`。
        /// </summary>
        public string Describe(int actualCount = -1)
        {
            return UseWhitelist ? DescribeWhitelist(actualCount) : DescribeRequiredCount(actualCount);
        }

        private string DescribeRequiredCount(int actualCount)
        {
            var sb = new System.Text.StringBuilder();

            // 数量关系文案：≥N / ≤N / 恰好 N
            string countText = Comparison switch
            {
                ComparisonType.GreaterOrEqual => $"需包含 {RequiredCount} 张或更多",
                ComparisonType.LessOrEqual => $"需包含 {RequiredCount} 张或更少",
                ComparisonType.Equal => $"需恰好包含 {RequiredCount} 张",
                _ => $"需包含 {RequiredCount} 张"
            };
            sb.Append(countText);

            // 筛选条件描述（多个条件为"同时满足"的与关系）
            var filters = new List<string>();
            if (FilterByColor) filters.Add($"{ColorName(TargetColor)}牌");
            if (FilterByCategory) filters.Add($"类别为 {TargetCategoryFlag} 的牌");
            if (FilterBySubCategory) filters.Add($"子类别为 {TargetSubCategory} 的牌");
            if (FilterByOrganization) filters.Add($"组织为 {TargetOrganization} 的牌");
            if (FilterByStrength) filters.Add($"战力 ≥ {MinStrength} 的牌");
            if (FilterByInvestigation) filters.Add($"调查 ≥ {MinInvestigation} 的牌");
            if (FilterByDomain) filters.Add($"领域为 {DomainName(TargetDomain)} 的牌");
            if (filters.Count > 0) sb.Append(string.Join("且", filters));

            if (actualCount >= 0) sb.Append($"（当前 {actualCount} 张）");
            return sb.ToString();
        }

        private string DescribeWhitelist(int actualCount)
        {
            var sb = new System.Text.StringBuilder();

            if (ExceptionMaxCount <= 0)
            {
                // "只能包含 A/B 派系的牌，以及其他派系的：<例外1> 或 <例外2>"
                sb.Append("只能包含 ");
                sb.Append(DescribeAllowedColors());
                sb.Append(" 派系的牌");
                var exc = new List<string>();
                if (ExceptionRules != null)
                    foreach (var r in ExceptionRules)
                        if (r != null) exc.Add(DescribeException(r));
                if (exc.Count > 0) sb.Append("，以及其他派系的：" + string.Join(" 或 ", exc));
                if (actualCount >= 0) sb.Append($"（当前 {actualCount} 张违规，上限 0 张）");
            }
            else
            {
                // "非 A 且非 中立 派系的牌不能超过 N 张"
                sb.Append("非 ");
                sb.Append(DescribeAllowedColors());
                sb.Append($" 且非 {ColorName(CardColor.Brown)} 派系的牌不能超过 {ExceptionMaxCount} 张");
                if (actualCount >= 0) sb.Append($"（当前 {actualCount} 张）");
            }
            return sb.ToString();
        }

        /// <summary>白名单颜色文案：`黄色/中立`——**棕色在白名单语境下显示为"中立"**（用户口径）。</summary>
        private string DescribeAllowedColors()
        {
            if (AllowedColors == null || AllowedColors.Count == 0) return "（未声明白名单）";
            var names = new List<string>();
            foreach (var c in AllowedColors) names.Add(WhitelistColorName(c));
            return string.Join("/", names);
        }

        /// <summary>白名单语境下的颜色名：**棕色 ⇒ 中立**（其余同 <see cref="ColorName"/>）。</summary>
        public static string WhitelistColorName(CardColor c) => c == CardColor.Brown ? "中立" : ColorName(c);

        private static string DescribeException(CouncilExceptionRule r)
        {
            var parts = new List<string>();
            if (r.FilterByCategory) parts.Add($"类别为 {CategoryName(r.TargetCategoryFlag)}");
            if (r.FilterBySubCategories && r.TargetSubCategories != null && r.TargetSubCategories.Count > 0)
                parts.Add($"子类别为 {string.Join(" 或 ", r.TargetSubCategories)}");
            if (r.FilterByDomain && r.TargetDomains != null && r.TargetDomains.Count > 0)
            {
                var ns = new List<string>();
                foreach (var d in r.TargetDomains) ns.Add(DomainName(d));
                parts.Add($"领域为 {string.Join(" 或 ", ns)}");
            }
            if (r.RequireNoDomain) parts.Add("不具有魔法领域图标");
            if (r.FilterByKeyword) parts.Add($"具有关键字 {r.TargetKeyword}");
            if (r.FilterByAbilityIcon) parts.Add($"具有［{r.TargetAbilityIcon}］能力图标（战力&gt;0）");
            return parts.Count == 0 ? "（未声明筛选条件的例外）" : string.Join(" 且 ", parts);
        }

        /// <summary>CardCategory 位掩码 → 中文类别名（取最匹配的单一类别名，供文案用）。</summary>
        public static string CategoryName(CardCategory c)
        {
            if ((c & CardCategory.Character) != 0) return "角色";
            if ((c & CardCategory.Attachment) != 0) return "附属";
            if ((c & CardCategory.Sorcery) != 0) return "事务";
            if ((c & CardCategory.Quest) != 0) return "任务";
            if ((c & CardCategory.Land) != 0) return "地区";
            if ((c & CardCategory.Token) != 0) return "指示物";
            return c.ToString();
        }

        /// <summary>CardColor 枚举 → 中文颜色名。（白名单语境请用 <see cref="WhitelistColorName"/>，那里棕色显示为"中立"。）</summary>
        public static string ColorName(CardColor c) => c switch
        {
            CardColor.Brown => "棕色",
            CardColor.Yellow => "黄色",
            CardColor.White => "白色",
            CardColor.Green => "绿色",
            CardColor.Gray => "灰色",
            CardColor.Purple => "紫色",
            CardColor.Blue => "蓝色",
            CardColor.Black => "黑色",
            CardColor.Red => "红色",
            _ => c.ToString()
        };

        /// <summary>MagicDomain 枚举 → 中文领域名。</summary>
        public static string DomainName(MagicDomain d) => d switch
        {
            MagicDomain.Mind => "心灵",
            MagicDomain.Divine => "神圣",
            MagicDomain.Star => "星空",
            MagicDomain.Death => "死亡",
            MagicDomain.Blood => "鲜血",
            _ => d.ToString()
        };
    }
}
