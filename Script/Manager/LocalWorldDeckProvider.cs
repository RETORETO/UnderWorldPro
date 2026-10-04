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

        // 统一走导出安全的目录扫描：导出包内 .tres 会变成 "xxx.tres.remap"，
        // 且素材在 .pck 内、磁盘上没有目录（详见 Script/IO/ResourceDirScan.cs 注释）。
        var paths = Script.IO.ResourceDirScan.TresInDir(AREA_CARD_PATH);
        if (paths.Count == 0)
            GD.PrintErr($"【LocalWorldDeckProvider】未扫描到任何地区卡: {AREA_CARD_PATH}（编辑器与导出包内都应为 res://Cards/Data/AreaCards）");

        foreach (var fullPath in paths)
        {
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

        return areas;
    }
}