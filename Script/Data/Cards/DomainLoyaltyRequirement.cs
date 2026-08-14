using Godot;
using Script.Enums;

namespace Script.Data.Cards
{
    [GlobalClass]
    public partial class DomainLoyaltyRequirement : Resource
    {
        [Export] public MagicDomain Domain { get; set; }
        [Export] public int Amount { get; set; }
    }
}