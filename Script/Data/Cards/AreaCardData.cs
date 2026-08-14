using Godot;
using Godot.Collections;
using Script.Enums;

namespace Script.Data.Cards
{
    [GlobalClass]
    public partial class AreaCardData : BaseCardData
    {
        [Export] public int RequiredPower { get; set; }
        [Export] public string WinTriggerEffect { get; set; }

        // 地区类别，固定为 Land（但保留导出以便未来扩展）
        [Export] public CardCategory Category { get; set; } = CardCategory.Land;
        // 地区子类别（中文，可多个）
        [Export] public Array<string> SubCategories { get; set; } = new Array<string>();
        //卡图路径（可在 Inspector 中手动设置，或由转换脚本生成）
        [Export] public string CardImagePath { get; set; } = "";
    }
}