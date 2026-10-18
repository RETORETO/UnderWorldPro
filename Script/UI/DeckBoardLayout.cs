using System;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 【卡组编辑器 · 卡组板布局算法 · 2026-10-17】纯逻辑层：**不引用 Godot**，可在 headless 测试里直接跑。
    ///
    /// 输入 = 卡组里"每个卡种（模板路径）→ 份数 + 排序键"，输出 = 每个"同名组"落在第几列、纵向起点、
    /// 堆叠错位步长（可能是压缩后的小值甚至负值）。UI 层（`DeckBuilder`）只负责按结果摆节点。
    ///
    /// 规则来源（用户 2026-10-17 口述，逐条对应见各方法注释）：
    ///   ① 卡组框尺寸固定、卡片尺寸固定；等宽纵列，列高 = 卡组框可视高；
    ///   ② 同名卡合并为组、组内沿 Y 向下错位堆叠（越靠下层级越高、最下方卡完整显示）；
    ///   ③ 异名组互不重叠；
    ///   ④ 组是**原子单位**：装不下就整组移到下一列；只有当前列没有别的卡名时才压缩 offset；
    ///   ⑤ 同名组上限 50 张，超出部分另起一组。
    ///
    /// 纪律：排序比较只用整数比较 + `StringComparer.Ordinal`。
    /// **禁止 culture-sensitive 比较**（`string.CompareTo` / 默认 `List.Sort()` / ICU 相关 API）——
    /// 本项目的 Web（wasm）运行时没有 ICU，走到那条路径会直接 abort（见 `DeckBuilder.ScanPool` 注释）。
    /// </summary>
    public static class DeckBoardLayout
    {
        /// <summary>排序优先级（对应界面下拉框的两项）。</summary>
        public enum DeckSortMode
        {
            /// <summary>「高费 ＞ 颜色」：费用降序为主键，颜色升序为次键。</summary>
            CostFirst = 0,
            /// <summary>「颜色 ＞ 高费」：颜色升序为主键，费用降序为次键。</summary>
            ColorFirst = 1,
        }

        /// <summary>一个"卡种"＝卡组字典里的一条（模板路径 + 份数 + 排序键）。</summary>
        public class DeckBoardCard
        {
            /// <summary>模板路径（= `_workingCards` 的 key），兼作分组键。</summary>
            public string TemplatePath = "";
            public string CardName = "";
            public string Uid = "";
            /// <summary>费用；`&lt; 0`（本项目用 -1）＝不可直接打出。</summary>
            public int Cost = -1;
            /// <summary>颜色键 = `CardColor` 枚举值（多色取最小者）；非法值已由调用方过滤。</summary>
            public int ColorKey = 0;
            /// <summary>份数（≥1）。</summary>
            public int Count = 1;
        }

        /// <summary>布局参数（全部由 UI 传入；默认值仅供测试/兜底）。</summary>
        public class DeckBoardParams
        {
            public float CardW = 100f;
            public float CardH = 140f;
            /// <summary>列宽（含列间空隙）。</summary>
            public float ColumnW = 110f;
            /// <summary>列高 = 卡组框可视高度（运行期量取）。</summary>
            public float ColumnH = 600f;
            /// <summary>初始堆叠错位步长（= 上方卡露出的高度）。</summary>
            public float Offset0 = 28f;
            /// <summary>异名组之间的纵向空隙。</summary>
            public float GroupGap = 8f;
            /// <summary>同名组张数上限（超出切分）。</summary>
            public int MaxPerGroup = 50;
            public DeckSortMode Mode = DeckSortMode.CostFirst;
        }

        /// <summary>一个"同名组"（≤ `MaxPerGroup` 张）及其布局结果。</summary>
        public class DeckBoardGroup
        {
            public string TemplatePath = "";
            public string CardName = "";
            public string Uid = "";
            public int Cost = -1;
            public int ColorKey = 0;
            public int Count = 1;

            // ---- 布局结果（由 `Compute` 填写）----
            /// <summary>列号（0 起）。</summary>
            public int Column;
            /// <summary>组顶相对卡组板内容顶的 Y。</summary>
            public float StartY;
            /// <summary>组内相邻两张卡的错位步长（压缩后可能小于 `Offset0`，也允许为负）。</summary>
            public float Offset;
            /// <summary>组高 = `CardH + (Count-1) * Offset`。</summary>
            public float GroupHeight;
        }

        /// <summary>布局结果。</summary>
        public class DeckBoardResult
        {
            public int ColumnCount;
            /// <summary>卡组板内容宽度 = `ColumnCount * ColumnW`。</summary>
            public float ContentWidth;
            /// <summary>卡组板内容高度 = 列高。</summary>
            public float ContentHeight;
            /// <summary>组列表，**顺序即填充/绘制顺序**（同名组内也按此顺序升序添加 ⇒ 越靠下层级越高）。</summary>
            public List<DeckBoardGroup> Groups = new List<DeckBoardGroup>();
        }

        // ============================================================
        // 主入口
        // ============================================================
        /// <summary>
        /// 排序 → 分组（含 50 张切分）→ 换列装箱（含压缩）。结果可直接用于摆节点。
        /// </summary>
        public static DeckBoardResult Compute(IEnumerable<DeckBoardCard> cards, DeckBoardParams p)
        {
            p ??= new DeckBoardParams();

            var sorted = new List<DeckBoardCard>();
            if (cards != null)
                foreach (var c in cards)
                    if (c != null && c.Count > 0) sorted.Add(c);

            // 显式传比较器：无参 `List<T>.Sort()` 走文化敏感比较器（ICU），Web 上会 abort。
            sorted.Sort((a, b) => Compare(a, b, p.Mode));

            var result = new DeckBoardResult();
            result.Groups = BuildGroups(sorted, p.MaxPerGroup);
            Pack(result.Groups, p, result);
            return result;
        }

        // ============================================================
        // ① 排序（需求 6）
        // ============================================================
        /// <summary>
        /// 两把排序键 + 一个兜底键：
        ///   · 主/次键由 <paramref name="mode"/> 决定（费用降序 / 颜色升序，谁先谁后）；
        ///   · 主次键完全相同时按**卡片编号升序**（`StringComparer.Ordinal`，保证跨平台一致且不碰 ICU）。
        /// 费用 `&lt; 0`（不可直接打出）视作"最低"，降序时排在所有正常费用**之后**。
        /// </summary>
        public static int Compare(DeckBoardCard a, DeckBoardCard b, DeckSortMode mode)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;      // null 排后（理论上不会出现）
            if (b == null) return -1;

            int c;
            if (mode == DeckSortMode.ColorFirst)
            {
                c = a.ColorKey.CompareTo(b.ColorKey);       // 颜色升序（主键）
                if (c != 0) return c;
                c = CompareCostDesc(a, b);                  // 费用降序（次键）
                if (c != 0) return c;
            }
            else
            {
                c = CompareCostDesc(a, b);
                if (c != 0) return c;
                c = a.ColorKey.CompareTo(b.ColorKey);       // 颜色升序（次键）
                if (c != 0) return c;
            }

            // 第三键：卡片编号升序。必须显式 Ordinal（Web 无 ICU）。
            return string.CompareOrdinal(a.Uid ?? "", b.Uid ?? "");
        }

        /// <summary>费用降序：高费在前；`&lt; 0`（不可直接打出）排最后。</summary>
        private static int CompareCostDesc(DeckBoardCard a, DeckBoardCard b)
        {
            int ca = a.Cost < 0 ? int.MinValue : a.Cost;
            int cb = b.Cost < 0 ? int.MinValue : b.Cost;
            return cb.CompareTo(ca);
        }

        // ============================================================
        // ② 分组 + 50 张切分（需求 2）
        // ============================================================
        /// <summary>
        /// 把已排序的卡种展开成"同名组"：
        ///   · **相邻同键合并**（当前数据里每个模板路径唯一，合并是恒等操作；保留合并逻辑是为了将来
        ///     若改成按卡名分组时行为仍然正确）；
        ///   · 合并后张数超过 <paramref name="maxPerGroup"/> 时按顺序切成多组（50/50/…/余数）。
        /// 分组键取**模板路径**而非卡名：同路径必然同卡同名，而异画/异版本若被合并，
        /// 会把"点一下加一张"的增删语义与存档份数搅乱。
        /// </summary>
        public static List<DeckBoardGroup> BuildGroups(List<DeckBoardCard> sorted, int maxPerGroup)
        {
            var groups = new List<DeckBoardGroup>();
            if (sorted == null || sorted.Count == 0) return groups;
            if (maxPerGroup <= 0) maxPerGroup = 50;

            int i = 0;
            while (i < sorted.Count)
            {
                var head = sorted[i];
                int total = 0;
                int j = i;
                while (j < sorted.Count && SameKey(sorted[j], head))
                {
                    total += sorted[j].Count;
                    j++;
                }

                int left = total;
                while (left > 0)
                {
                    int take = left > maxPerGroup ? maxPerGroup : left;
                    groups.Add(new DeckBoardGroup
                    {
                        TemplatePath = head.TemplatePath,
                        CardName = head.CardName,
                        Uid = head.Uid,
                        Cost = head.Cost,
                        ColorKey = head.ColorKey,
                        Count = take,
                    });
                    left -= take;
                }

                i = j;
            }
            return groups;
        }

        private static bool SameKey(DeckBoardCard a, DeckBoardCard b)
            => string.Equals(a.TemplatePath ?? "", b.TemplatePath ?? "", StringComparison.Ordinal);

        // ============================================================
        // ③ 换列装箱（需求 1、3、4）
        // ============================================================
        /// <summary>
        /// 逐组填充列：
        ///   · 列内已用高度 + 组间空隙 + 组原始高度 &gt; 列高 ⇒ **整组**移到下一列（绝不拆组、绝不压缩）；
        ///   · 落进空列时若原始高度仍超列高 ⇒ 压缩本组 `offset` 铺满一列
        ///     （`offset' = (列高 - 卡高) / (张数 - 1)`，可无限缩小，允许卡名重叠）；
        ///   · 非空列一律用初始 offset，即"列里已有别的卡名时不压缩"。
        /// 说明：需求里"当前列只有这组同名卡"的压缩情形，在"上一组已铺满该列"时必然退化为空列分支
        /// （压缩后 `GroupHeight == 列高` ⇒ 该列已满 ⇒ 下一组落入新空列），故无需单独分支。
        /// </summary>
        private static void Pack(List<DeckBoardGroup> groups, DeckBoardParams p, DeckBoardResult result)
        {
            float cardH = p.CardH > 0f ? p.CardH : 1f;
            float colW = p.ColumnW > 0f ? p.ColumnW : 1f;
            float colH = p.ColumnH > cardH ? p.ColumnH : cardH;    // 钳制：列高不得小于卡高
            float offset0 = p.Offset0 > 0f ? p.Offset0 : 0f;
            float gap = p.GroupGap > 0f ? p.GroupGap : 0f;

            var used = new List<float> { 0f };                     // 每列已用高度
            int col = 0;

            foreach (var g in groups)
            {
                int n = g.Count > 0 ? g.Count : 1;
                float rawH = cardH + (n - 1) * offset0;            // 组原始高度

                while (used.Count <= col) used.Add(0f);

                // 非空列放不下 ⇒ 整组原子移到下一列（不压缩）
                if (used[col] > 0f && used[col] + gap + rawH > colH)
                {
                    col++;
                    while (used.Count <= col) used.Add(0f);
                }

                if (used[col] <= 0f)
                {
                    // 空列：放得下就用初始 offset，放不下就压缩
                    g.Column = col;
                    g.StartY = 0f;
                    if (rawH <= colH)
                        g.Offset = n > 1 ? offset0 : 0f;
                    else
                        g.Offset = n > 1 ? (colH - cardH) / (n - 1) : 0f;

                    g.GroupHeight = cardH + (n - 1) * g.Offset;
                    used[col] = g.GroupHeight > 0f ? g.GroupHeight : 0f;
                }
                else
                {
                    // 非空列（列内已有别的组）：照常追加，绝不压缩
                    g.Column = col;
                    g.StartY = used[col] + gap;
                    g.Offset = n > 1 ? offset0 : 0f;
                    g.GroupHeight = rawH;
                    used[col] = g.StartY + g.GroupHeight;
                }
            }

            result.ColumnCount = col + 1;
            result.ContentWidth = (col + 1) * colW;
            result.ContentHeight = colH;
        }

        // ============================================================
        // 工具：颜色键（多色取枚举值最小者）
        // ============================================================
        /// <summary>
        /// 从一组颜色枚举值取**排序键**：取合法值中的最小者（与 `CardColor` 的定义顺序一致：
        /// 棕 0 / 黄 1 / 白 2 / 绿 3 / 灰 4 / 紫 5 / 蓝 6 / 黑 7 / 红 8）。
        /// 无合法值时回落 `fallback`（默认 0 = 棕/中立）。
        /// 之所以用"最小值"而不是数组首色：数据里颜色数组顺序不保证，取最小才有确定性。
        /// </summary>
        public static int ColorKeyOf(IEnumerable<int> colors, int fallback = 0, int definedMax = 8)
        {
            bool any = false;
            int best = fallback;
            if (colors != null)
            {
                foreach (var c in colors)
                {
                    if (c < 0 || c > definedMax) continue;     // 过滤哨兵/脏值
                    if (!any || c < best) { best = c; any = true; }
                }
            }
            return any ? best : fallback;
        }
    }
}
