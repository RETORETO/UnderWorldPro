using Godot;
using Script.Enums;

namespace Script.Data.Cards
{
    [GlobalClass]
    public partial class ColorLoyaltyRequirement : Resource
    {
        [Export] public CardColor Color { get; set; }
        [Export] public int Amount { get; set; }
    }
}