using System.Collections.Generic;
using System.IO;
using Godot;
using Script.Data.Cards;
using Script.Net;

namespace Script.Tools
{
    /// <summary>
    /// 【卡组编辑器检索索引】生成器：把 `res://Cards/Data/PlayerCards` 下的全部 `.tres`
    /// 压缩成一份"只含可过滤字段 + res 路径"的 JSON 索引。
    ///
    /// 为什么要有它（而不是让编辑器运行期扫 .tres）：
    ///   Web（wasm）上同步 `ResourceLoader.Load` 698 次会明显卡顿，
    ///   而检索只需要静态字段 —— 于是把这些字段在**构建期**抽出来，运行时只读 1 个 JSON。
    ///   详见计划 `Script/Net/DeckIndexData.cs` 的类头说明。
    ///
    /// 生成方式（headless，见 <see cref="CardIndexRunner"/>）：
    ///   godot --headless --path . --script res://Script/Tools/CardIndexRunner.cs
    /// 产物：res://Cards/card_index.json（建议提交进仓库；打包脚本每次导出前会强制重生成）
    /// </summary>
    public static class CardIndexBuilder
    {
        /// <summary>卡池源目录（仅玩家卡；秘社/地区卡不进卡池）。</summary>
        public const string PlayerCardsDir = "res://Cards/Data/PlayerCards";

        /// <summary>默认产物路径。</summary>
        public const string DefaultOutputPath = "res://Cards/card_index.json";

        /// <summary>只构建、不落盘（供单测/校验用）。</summary>
        public static DeckIndexRoot Build()
        {
            var root = new DeckIndexRoot();
            var paths = Script.IO.ResourceDirScan.TresRecursive(PlayerCardsDir);
            // 【Web 安全】无参 Sort() 走文化敏感比较器（ICU），必须显式序数。
            paths.Sort(System.StringComparer.Ordinal);

            foreach (var path in paths)
            {
                var p = ResourceLoader.Load<PlayerCardData>(path);
                // UID 为空的资源（含被误放进目录的非卡资源）一律跳过，与 CardTresToJson 同口径
                if (p == null || string.IsNullOrEmpty(p.UID)) continue;
                root.Cards.Add(ToEntry(p, path));
            }

            root.EntryCount = root.Cards.Count;
            // 【Web 安全】用 Godot 的时间 API 产出字符串，绕开 .NET 的 DateTime 格式化（会触发 ICU）。
            root.GeneratedAt = Time.GetDatetimeStringFromSystem(false, true);
            return root;
        }

        /// <summary>构建并写出 JSON；返回写出后的绝对路径。</summary>
        public static string BuildAndWrite(string outputPath = DefaultOutputPath)
        {
            var root = Build();
            var json = System.Text.Json.JsonSerializer.Serialize(root,
                new System.Text.Json.JsonSerializerOptions
                {
                    // POCO 用 public 字段承载，必须 IncludeFields 才不丢；不缩进以压小体积
                    IncludeFields = true,
                    WriteIndented = false,
                    // 运行时派生字段（*Fold）在构建期为 null，跳过以免 698 条各写一堆 null 键
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });

            // res:// 在导出后只读，但生成发生在 headless/编辑器（源工程）阶段，可写
            string abs = ProjectSettings.GlobalizePath(outputPath);
            File.WriteAllText(abs, json);

            GD.Print("[CardIndexBuilder] 扫描目录 " + PlayerCardsDir);
            GD.Print("[CardIndexBuilder] 条目数=" + Script.IO.WebSafeText.Int(root.EntryCount)
                   + " 产物=" + abs
                   + " 大小KB=" + Script.IO.WebSafeText.Int((int)(new FileInfo(abs).Length / 1024)));
            return abs;
        }

        // ============================================================
        // .tres → 索引条目
        // ============================================================
        private static DeckSearchIndexEntry ToEntry(PlayerCardData p, string resPath)
        {
            var e = new DeckSearchIndexEntry
            {
                ResPath = resPath,
                FileStem = GetFileStem(resPath),
                UID = p.UID,
                CardName = p.CardName,
                SubCardName = p.SubCardName,
                UniqueField = p.UniqueField,
                Cost = p.Cost,
                CategoryFlagValue = (int)p.CategoryFlag,
                Organization = p.Organization,
                Investigation = p.Investigation,
                Strength = p.Strength,
                Power = p.Power,
                FirstStrikeInvestigation = p.FirstStrikeInvestigation,
                FirstStrikeStrength = p.FirstStrikeStrength,
                FirstStrikePower = p.FirstStrikePower,
                Defense = p.Defense,
            };

            var subs = p.SubCategories;
            if (subs != null)
                foreach (var s in subs)
                    if (!string.IsNullOrEmpty(s)) e.SubCategories.Add(s);

            var colors = p.Colors;
            if (colors != null)
                foreach (var c in colors) e.Colors.Add((int)c);

            // 魔法领域：剔除 -1（"无领域"哨兵）；空列表即"无领域"
            var domains = p.MagicDomains;
            if (domains != null)
                foreach (var d in domains)
                {
                    int dv = (int)d;
                    if (dv >= 0 && dv <= 4) e.MagicDomains.Add(dv);
                }

            var cl = p.ColorLoyaltyRequirements;
            if (cl != null)
                foreach (var r in cl)
                    if (r != null) e.ColorLoyalties.Add(new LoyaltyEntry { Value = (int)r.Color, Amount = r.Amount });

            var dl = p.DomainLoyaltyRequirements;
            if (dl != null)
                foreach (var r in dl)
                    if (r != null) e.DomainLoyalties.Add(new LoyaltyEntry { Value = (int)r.Domain, Amount = r.Amount });

            return e;
        }

        /// <summary>
        /// 取 res 路径的文件名（去目录、去 ".tres"）。手写解析而不是 Path.GetFileNameWithoutExtension，
        /// 免得任何平台差异影响构建期产物。
        /// </summary>
        private static string GetFileStem(string resPath)
        {
            if (string.IsNullOrEmpty(resPath)) return "";
            int slash = resPath.LastIndexOf('/');
            string file = slash >= 0 ? resPath.Substring(slash + 1) : resPath;
            if (file.EndsWith(".tres", System.StringComparison.Ordinal))
                file = file.Substring(0, file.Length - 5);
            return file;
        }
    }
}
