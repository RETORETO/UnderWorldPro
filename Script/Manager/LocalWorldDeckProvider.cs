// 放在 Script/Manager/LocalWorldDeckProvider.cs
using Godot;
using Script.Data.Cards;
using Script.Interfaces;
using System.Collections.Generic;

public class LocalWorldDeckProvider : IWorldDeckProvider
{
    private const string AREA_CARD_PATH = "res://Cards/Data/AreaCards/";

    public List<AreaCardData> LoadAllAreas()
    {
        List<AreaCardData> areas = new List<AreaCardData>();

        using var dir = DirAccess.Open(AREA_CARD_PATH);
        if (dir == null)
        {
            GD.PrintErr($"【LocalWorldDeckProvider】无法打开目录: {AREA_CARD_PATH}");
            return areas;
        }

        dir.ListDirBegin();
        string fileName = dir.GetNext();
        while (fileName != "")
        {
            if (!dir.CurrentIsDir() && fileName.EndsWith(".tres"))
            {
                string fullPath = $"{AREA_CARD_PATH}{fileName}";
                var areaData = ResourceLoader.Load<AreaCardData>(fullPath);
                if (areaData != null)
                {
                    areas.Add(areaData);
                }
                else
                {
                    GD.PrintErr($"【LocalWorldDeckProvider】加载失败: {fullPath}");
                }
            }
            fileName = dir.GetNext();
        }
        dir.ListDirEnd();

        return areas;
    }
}