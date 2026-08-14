using System.Collections.Generic;

namespace Script.IO
{
/// <summary>
/// 卡组数据模型（内存态），可序列化为 JSON 存盘/读取。
/// 同时包含玩家卡组（cards）与所选秘社（councils），两者均用 (模板资源路径, 份数) 表达。
/// </summary>
public class DeckList
{
    /// <summary>卡组显示名（存盘用，便于识别）。</summary>
    public string DeckName = "未命名卡组";

    /// <summary>玩家卡组：(模板资源路径, 份数)。路径形如 res://Cards/Data/PlayerCards/JC/JC001_xxx.tres。</summary>
    public List<DeckEntry> Cards = new List<DeckEntry>();

    /// <summary>所选秘社：(模板资源路径, 份数)。路径形如 res://Cards/Data/CouncilCards/JC/MSJC001_xxx.tres。</summary>
    public List<DeckEntry> Councils = new List<DeckEntry>();

    /// <summary>卡组总张数（玩家卡份数之和，不含秘社）。</summary>
    public int TotalCardCount
    {
        get
        {
            int sum = 0;
            foreach (var c in Cards) sum += c.Copies;
            return sum;
        }
    }

    /// <summary>秘社总张数（份数之和）。</summary>
    public int TotalCouncilCount
    {
        get
        {
            int sum = 0;
            foreach (var c in Councils) sum += c.Copies;
            return sum;
        }
    }

    /// <summary>序列化用单项：模板路径 + 份数。</summary>
    public class DeckEntry
    {
        public string TemplatePath = "";
        public int Copies = 1;
    }
}

}
