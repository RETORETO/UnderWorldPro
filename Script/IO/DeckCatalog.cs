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
            // 【联机排查埋点 p1~p10，2026-10-13】Web 版进房间页时崩在本方法内（调用方 d1 之后、d2 之前），
            // 逐步打标记即可钉到具体那一句。定位完成后整段删除。
            GD.Print("[DeckCatalog] p1 即将 DirAccess.Open " + DeckDir);
            var dir = DirAccess.Open(DeckDir);
            GD.Print("[DeckCatalog] p2 Open 已返回, 是否为空=" + (dir == null ? "1" : "0"));
            if (dir == null)
            {
                GD.PrintErr($"[DeckCatalog] 无法打开卡组目录：{DeckDir}（导出构建请把 Decks/*.json 加入导出过滤）");
                return list;
            }

            GD.Print("[DeckCatalog] p3 即将 ListDirBegin");
            dir.ListDirBegin();
            GD.Print("[DeckCatalog] p4 ListDirBegin 完成");
            string name = dir.GetNext();
            int __guard = 0;
            while (!string.IsNullOrEmpty(name))
            {
                if (++__guard > 5000) { GD.PrintErr("[DeckCatalog] 目录项过多，提前中断"); break; }
                GD.Print("[DeckCatalog] p5 项='" + name + "'");
                bool isDir = dir.CurrentIsDir();
                GD.Print("[DeckCatalog] p5b CurrentIsDir=" + (isDir ? "1" : "0"));
                // 【2026-10-13】EndsWith 的 OrdinalIgnoreCase 在**比较区间含非 ASCII** 时同样会落到
                // OrdinalCasing -> ICU（见 WebSafeText 类头）。目录里存在中文名文件时这是真风险，
                // 统一改用只折叠 ASCII 的安全版本。
                if (!isDir && WebSafeText.EndsWithIgnoreCaseAscii(name, ".json"))
                    list.Add(DeckDir + "/" + name);
                name = dir.GetNext();
            }
            GD.Print("[DeckCatalog] p6 列举结束，条数=" + WebSafeText.Int(list.Count));
            dir.ListDirEnd();
            GD.Print("[DeckCatalog] p7 ListDirEnd 完成，即将 Sort");
            // 【Web 崩溃根因 · 2026-10-13】这里原来是 `list.Sort(StringComparer.OrdinalIgnoreCase)`，
            // 对含中文文件名的目录（Decks/ 下半数是中文名卡组）排序时实测 wasm abort。
            // 原因：OrdinalIgnoreCase 只对 ASCII 走算术快路径，遇到 >0x7F 的字符即转
            // OrdinalCasing.CompareStringIgnoreCaseNonAscii -> Interop.Globalization.ChangeCase -> ICU。
            // 改用只折叠 ASCII 的比较器（中文无大小写，结果与原行为一致）。
            WebSafeText.SortIgnoreCaseAscii(list);
            GD.Print("[DeckCatalog] p8 Sort 完成");
            GD.Print("[DeckCatalog] 卡组目录共 " + WebSafeText.Int(list.Count) + " 个卡组："
                + string.Join("、", list.ConvertAll(DisplayName)));
            GD.Print("[DeckCatalog] p10 完成");
            return list;
        }

        /// <summary>列表为空时给出可用路径：优先默认卡组，其次目录里的第一个，最后仍是默认路径。</summary>
        public static string ResolveInitialDeck(IReadOnlyList<string> available, string preferred)
        {
            if (!string.IsNullOrEmpty(preferred) && FileAccess.FileExists(preferred)) return preferred;
            if (available != null && available.Count > 0)
            {
                // 【2026-10-13】原为 string.Equals(..., OrdinalIgnoreCase)：两侧都是含中文的 res:// 路径，
                // 比较到第 13 个字符就会跌进 ICU 路径 -> Web 上 abort。改用 ASCII 折叠的安全比较。
                foreach (var p in available) if (WebSafeText.EqualsIgnoreCaseAscii(p, DefaultDeckPath)) return p;
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
