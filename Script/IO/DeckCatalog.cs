using System.Collections.Generic;
using Godot;

namespace Script.IO
{
    /// <summary>
    /// 【大厅/房间 UI · 增量 C】**卡组目录清单**：枚举 `res://Decks/` 下的卡组文件，供房间页下拉框使用。
    ///
    /// 说明：
    ///  · 卡组文件＝该目录下的 `*.json`（与 `DeckListLoader` 读取格式一致）；
    ///  · 返回**按文件名排序**的全路径列表（`res://Decks/xxx.json`），显示名取不含扩展名的文件名；
    ///  · 目录打不开（极少见：导出构建未包含该目录）时返回空列表，调用方回退到 <see cref="DefaultDeckPath"/>。
    ///  · **导出注意**：若将来做导出构建，需确保导出过滤包含 `Decks/*.json`，否则 `DirAccess` 列不到文件。
    /// </summary>
    public static class DeckCatalog
    {
        /// <summary>卡组目录。</summary>
        public const string DeckDir = "res://Decks";

        /// <summary>兜底卡组（目录为空/不可读时使用；与单机默认一致）。</summary>
        public const string DefaultDeckPath = DeckDir + "/default_deck.json";

        /// <summary>枚举目录下的卡组全路径（按文件名升序）。目录不可读时返回空列表。</summary>
        public static List<string> ListDeckPaths()
        {
            var list = new List<string>();
            var dir = DirAccess.Open(DeckDir);
            if (dir == null)
            {
                GD.PrintErr($"[DeckCatalog] 无法打开卡组目录：{DeckDir}（导出构建请把 Decks/*.json 加入导出过滤）");
                return list;
            }

            dir.ListDirBegin();
            string name = dir.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (!dir.CurrentIsDir() && name.EndsWith(".json"))
                    list.Add($"{DeckDir}/{name}");
                name = dir.GetNext();
            }
            dir.ListDirEnd();

            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            GD.Print($"[DeckCatalog] 卡组目录共 {list.Count} 个卡组：{string.Join("、", list.ConvertAll(DisplayName))}");
            return list;
        }

        /// <summary>列表为空时给出可用路径：优先默认卡组，其次目录里的第一个，最后仍是默认路径。</summary>
        public static string ResolveInitialDeck(IReadOnlyList<string> available, string preferred)
        {
            if (!string.IsNullOrEmpty(preferred) && FileAccess.FileExists(preferred)) return preferred;
            if (available != null && available.Count > 0)
            {
                foreach (var p in available) if (System.String.Equals(p, DefaultDeckPath, System.StringComparison.OrdinalIgnoreCase)) return p;
                return available[0];
            }
            return DefaultDeckPath;
        }

        /// <summary>显示名＝不含扩展名的文件名（下拉框展示用）。</summary>
        public static string DisplayName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "（无）";
            int slash = path.LastIndexOf('/');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }
    }
}
