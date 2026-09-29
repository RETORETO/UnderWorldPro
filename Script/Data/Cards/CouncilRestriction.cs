// Script/Data/Cards/CouncilRestriction.cs
using Godot;
using Script.Enums;

namespace Script.Data.Cards
{
    [GlobalClass]
    public partial class CouncilRestriction : Resource
    {
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

        public enum ComparisonType
        {
            GreaterOrEqual,
            LessOrEqual,
            Equal
        }

        public bool IsSatisfiedBy(System.Collections.Generic.IEnumerable<PlayerCardData> deck)
        {
            return GetActualCount(deck) is var count && Comparison switch
            {
                ComparisonType.GreaterOrEqual => count >= RequiredCount,
                ComparisonType.LessOrEqual => count <= RequiredCount,
                ComparisonType.Equal => count == RequiredCount,
                _ => false
            };
        }

        /// <summary>统计套牌中满足本限制全部筛选条件的卡牌数量（供校验明细展示用）。</summary>
        public int GetActualCount(System.Collections.Generic.IEnumerable<PlayerCardData> deck)
        {
            int count = 0;
            if (deck == null) return 0;
            foreach (var card in deck)
            {
                if (card == null) continue;
                bool match = true;
                if (FilterByColor && !card.Colors.Contains(TargetColor)) match = false;
                if (FilterByCategory && !card.CategoryFlag.HasFlag(TargetCategoryFlag)) match = false;
                if (FilterBySubCategory && !card.SubCategories.Contains(TargetSubCategory)) match = false;
                if (FilterByOrganization && card.Organization != TargetOrganization) match = false;
                if (FilterByStrength && card.Strength < MinStrength) match = false;
                if (FilterByInvestigation && card.Investigation < MinInvestigation) match = false;
                if (FilterByDomain && !card.MagicDomains.Contains(TargetDomain)) match = false;
                if (match) count++;
            }
            return count;
        }

        /// <summary>
        /// 生成可读的构筑限制文案（供"卡组违规"提示与 UI 展示）。
        /// 例："需包含 25 张或更多黄色牌"（满足时要求）；传入 actualCount 时附加"（当前 N 张）"。
        /// </summary>
        public string Describe(int actualCount = -1)
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
            var filters = new System.Collections.Generic.List<string>();
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

        /// <summary>CardColor 枚举 → 中文颜色名。</summary>
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