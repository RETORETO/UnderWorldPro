using Godot;
using System.Collections.Generic;

namespace Script.IO
{
/// <summary>
/// 卡组 JSON 读写器（纯工具，无节点依赖）。
/// 卡组文件结构：
/// {
///   "deckName": "默认对局卡组",
///   "cards":    [ { "templatePath": "res://.../JC001_xxx.tres", "copies": 3 }, ... ],
///   "councils": [ { "templatePath": "res://.../MSJC001_xxx.tres", "copies": 1 } ]
/// }
/// 使用 Godot.FileAccess 读写 res:// 路径（System.IO 无法直接访问资源路径）。
/// </summary>
public static class DeckListLoader
{
    /// <summary>秘社缺失时的兜底默认秘社（与旧 GameInitializer.DefaultCouncilPath 一致）。</summary>
    private const string FallbackCouncilPath = "res://Cards/Data/CouncilCards/JC/MSJC009_mishe.tres";

    /// <summary>
    /// 读取卡组 JSON。councils 为空时自动补一条默认秘社，保证对局始终有秘社可用。
    /// 文件不存在或解析失败时返回 null（调用方应决定兜底策略）。
    /// </summary>
    public static DeckList Load(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            GD.PushError($"[DeckListLoader] 卡组文件不存在: {path}");
            return null;
        }

        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            GD.PushError($"[DeckListLoader] 无法打开卡组文件: {path}");
            return null;
        }

        string text = file.GetAsText();
        var json = Json.ParseString(text);
        if (json.VariantType == Variant.Type.Nil)
        {
            GD.PushError($"[DeckListLoader] 卡组 JSON 解析失败: {path}");
            return null;
        }

        return BuildFromRoot(json.As<Godot.Collections.Dictionary>(), path);
    }

    /// <summary>
    /// 【联机·服务端校验用】从 **JSON 文本**解析卡组（服务端不一定持有客户端本地的卡组文件）。
    /// 语义与 <see cref="Load"/> 完全一致（含"无秘社则补默认秘社"兜底）；解析失败返回 null。
    /// </summary>
    public static DeckList LoadFromText(string jsonText, string sourceLabel = "(inline)")
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            GD.PushError($"[DeckListLoader] 卡组 JSON 为空: {sourceLabel}");
            return null;
        }
        var json = Json.ParseString(jsonText);
        if (json.VariantType != Variant.Type.Nil && json.VariantType == Variant.Type.Dictionary)
            return BuildFromRoot(json.As<Godot.Collections.Dictionary>(), sourceLabel);
        GD.PushError($"[DeckListLoader] 卡组 JSON 解析失败: {sourceLabel}");
        return null;
    }

    /// <summary>把已解析的根字典装配为 `DeckList`（`Load` 与 `LoadFromText` 共用，避免两份解析逻辑）。</summary>
    private static DeckList BuildFromRoot(Godot.Collections.Dictionary root, string sourceLabel)
    {
        var deck = new DeckList();
        if (root.ContainsKey("deckName")) deck.DeckName = root["deckName"].AsString();

        if (root.ContainsKey("cards") && root["cards"].VariantType == Variant.Type.Array)
        {
            foreach (var item in root["cards"].As<Godot.Collections.Array>())
            {
                var e = ParseEntry(item);
                if (e != null) deck.Cards.Add(e);
            }
        }

        if (root.ContainsKey("councils") && root["councils"].VariantType == Variant.Type.Array)
        {
            foreach (var item in root["councils"].As<Godot.Collections.Array>())
            {
                var e = ParseEntry(item);
                if (e != null) deck.Councils.Add(e);
            }
        }

        // 兜底：无秘社则补默认，保证 SetupCouncils 有数据
        if (deck.Councils.Count == 0)
        {
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = FallbackCouncilPath, Copies = 1 });
        }
        _ = sourceLabel;
        return deck;
    }

    /// <summary>
    /// 把卡组序列化为 JSON **文本**（不落盘）。
    /// 用途：**联机客户端把卡组内联提交给服务端**（服务端不一定持有客户端本机的卡组文件）。
    /// 与 <see cref="Save"/> 使用同一字段名，保证服务端 `LoadFromText` 能解析。
    /// </summary>
    public static string ToJsonText(DeckList deck)
    {
        if (deck == null) return null;
        var root = new Godot.Collections.Dictionary
        {
            ["deckName"] = deck.DeckName,
            ["cards"] = ToArray(deck.Cards),
            ["councils"] = ToArray(deck.Councils),
        };
        return Json.Stringify(root);
    }

    /// <summary>将卡组序列化写回 JSON 文件（覆盖式）。</summary>
    public static void Save(DeckList deck, string path)
    {
        var root = new Godot.Collections.Dictionary
        {
            ["deckName"] = deck.DeckName,
            ["cards"] = ToArray(deck.Cards),
            ["councils"] = ToArray(deck.Councils),
        };

        string text = Json.Stringify(root);
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushError($"[DeckListLoader] 无法写入卡组文件: {path}");
            return;
        }
        file.StoreString(text);
        GD.Print($"[DeckListLoader] 卡组已保存: {path}（卡 {deck.TotalCardCount} 张 / 秘社 {deck.TotalCouncilCount} 张）");
    }

    // ---- 内部辅助 ----

    private static DeckList.DeckEntry ParseEntry(Variant item)
    {
        if (item.VariantType != Variant.Type.Dictionary) return null;
        var d = item.As<Godot.Collections.Dictionary>();
        var entry = new DeckList.DeckEntry();
        if (d.ContainsKey("templatePath")) entry.TemplatePath = d["templatePath"].AsString();
        if (d.ContainsKey("copies")) entry.Copies = (int)d["copies"].As<long>();
        if (string.IsNullOrEmpty(entry.TemplatePath)) return null;
        if (entry.Copies < 1) entry.Copies = 1;
        return entry;
    }

    private static Godot.Collections.Array ToArray(List<DeckList.DeckEntry> entries)
    {
        var arr = new Godot.Collections.Array();
        foreach (var e in entries)
        {
            arr.Add(new Godot.Collections.Dictionary
            {
                ["templatePath"] = e.TemplatePath,
                ["copies"] = e.Copies,
            });
        }
        return arr;
    }
}

}
