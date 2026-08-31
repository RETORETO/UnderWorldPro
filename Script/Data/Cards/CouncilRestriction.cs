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
            int count = 0;
            foreach (var card in deck)
            {
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

            return Comparison switch
            {
                ComparisonType.GreaterOrEqual => count >= RequiredCount,
                ComparisonType.LessOrEqual => count <= RequiredCount,
                ComparisonType.Equal => count == RequiredCount,
                _ => false
            };
        }
    }
}