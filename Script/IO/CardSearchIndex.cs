using System.Collections.Generic;
using Godot;
using Script.Net;

namespace Script.IO
{
    /// <summary>
    /// 【卡组编辑器检索索引】运行时加载器 + 缓存。
    ///
    /// 职责单一：把 `res://Cards/card_index.json`（构建期由 <see cref="Script.Tools.CardIndexBuilder"/> 生成）
    /// 读进内存，并对所有"需要子串匹配"的字段做**一次性 ASCII 折叠**，让每次搜索只做序数 Contains。
    ///
    /// 失败策略：**优雅降级**。索引缺失/损坏/反序列化异常一律返回 false，
    /// 由 DeckBuilder 回退到改造前的"按文件名子串"过滤（功能降级，但不崩、且有明确报错）。
    ///
    /// Web 安全：只走 FileAccess（不用 System.IO.File，它读不到 res://）、
    /// System.Text.Json（工程内网络序列化已在 Web 上验证）、WebSafeText.*（不碰 ICU）。
    /// </summary>
    public static class CardSearchIndex
    {
        public const string IndexPath = "res://Cards/card_index.json";

        private static List<DeckSearchIndexEntry> _cards;
        private static DeckIndexRoot _root;
        private static bool _tried;

        /// <summary>索引是否可用（已成功加载）。</summary>
        public static bool Available => _cards != null;

        /// <summary>索引条目数（不可用时为 0）。</summary>
        public static int Count => _cards != null ? _cards.Count : 0;

        /// <summary>已加载的条目列表（不可用时为 null）。</summary>
        public static List<DeckSearchIndexEntry> Cards => _cards;

        /// <summary>索引根（含 EntryCount / GeneratedAt，供过期比对）。</summary>
        public static DeckIndexRoot Root => _root;

        /// <summary>幂等加载。返回是否可用。</summary>
        public static bool EnsureLoaded()
        {
            if (_tried) return _cards != null;
            _tried = true;

            ulong t0 = Time.GetTicksMsec();
            try
            {
                // 【A/B 对照开关】--wp-index=off：故意跳过索引，用来证明"索引未引入回归"以及
                // 定量对比"有无索引"下的 _Ready 耗时与过滤耗时。
                if (WebPerfParams.IndexDisabled)
                {
                    GD.Print("[CardSearchIndex] --wp-index=off：跳过索引（走编号子串回退路径）");
                    return false;
                }

                if (!FileAccess.FileExists(IndexPath))
                {
                    GD.PrintErr("[CardSearchIndex] 索引不存在：" + IndexPath + " → 回退为编号搜索");
                    return false;
                }

                using var f = FileAccess.Open(IndexPath, FileAccess.ModeFlags.Read);
                if (f == null)
                {
                    GD.PrintErr("[CardSearchIndex] 打开索引失败：" + IndexPath
                                + " err=" + Script.IO.WebSafeText.Int((int)FileAccess.GetOpenError()));
                    return false;
                }

                string text = f.GetAsText();
                var root = System.Text.Json.JsonSerializer.Deserialize<DeckIndexRoot>(text,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        IncludeFields = true,                 // POCO 用 public 字段
                        PropertyNameCaseInsensitive = true,
                    });

                if (root == null || root.Cards == null)
                {
                    GD.PrintErr("[CardSearchIndex] 索引反序列化为空 → 回退为编号搜索");
                    return false;
                }

                foreach (var e in root.Cards) Prepare(e);
                _cards = root.Cards;
                _root = root;

                WebPerfParams.Summary("deck.index load=" + Script.IO.WebSafeText.Int((int)(Time.GetTicksMsec() - t0))
                    + "ms entries=" + Script.IO.WebSafeText.Int(_cards.Count));
                return true;
            }
            catch (System.Exception ex)
            {
                // Web 上托管异常跨原生边界会 abort，这里必须吞掉并降级
                GD.PrintErr("[CardSearchIndex] 加载索引异常 → 回退为编号搜索："
                            + ex.GetType().Name + " " + ex.Message);
                _cards = null;
                _root = null;
                return false;
            }
        }

        /// <summary>对单条索引做一次性预处理（折叠需子串匹配的字段）。</summary>
        private static void Prepare(DeckSearchIndexEntry e)
        {
            if (e == null) return;
            e.FileStemFold = Fold(e.FileStem);
            e.NameFold = Fold(e.CardName);
            e.SubCardNameFold = Fold(e.SubCardName);

            e.SubCategoriesFold = new List<string>();
            if (e.SubCategories != null)
                foreach (var s in e.SubCategories)
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    e.SubCategoriesFold.Add(Fold(s));
                }
        }

        /// <summary>空 → ""；否则 ASCII 折叠（中文无大小写，等价原样）。</summary>
        private static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Script.IO.WebSafeText.ToLowerAscii(s);
        }
    }
}
