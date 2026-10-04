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
            // 导出后 .tres 被转成二进制资源，目录列表给出的是 "xxx.tres.remap"；编辑器下是 "xxx.tres"。
            // 只认 ".tres" 会导致导出后地区卡扫描为 0（`WorldDeckManager` 加载 0 张地区卡）。
            string resName = null;
            if (!dir.CurrentIsDir())
            {
                if (fileName.EndsWith(".tres"))
                    resName = fileName;
                else if (fileName.EndsWith(".tres.remap"))
                    resName = fileName.Substring(0, fileName.Length - ".remap".Length);
            }

            if (resName != null)
            {
                string fullPath = $"{AREA_CARD_PATH}{resName}";
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