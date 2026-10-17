using System.Collections.Generic;
using Script.UI;

namespace Script.Tests
{
    /// <summary>
    /// 【卡组编辑器 · 卡组板布局 · 2026-10-17】`DeckBoardLayout` 的算法回归。
    ///
    /// 锁定用户 2026-10-17 口述的规则，防止后续改动悄悄改行为：
    ///   ① 排序两把键（费用降序 / 颜色升序）＋编号兜底；
    ///   ② 同名组上限 50 张，超出另起一组；
    ///   ③ 组是原子单位：非空列装不下 ⇒ 整组移到下一列、**不压缩**；
    ///   ④ 空列且原始高度超列高 ⇒ 压缩 offset 铺满一列（offset 只压不张，可为 0）；
    ///   ⑤ 列高不得小于卡高（入口钳制）。
    /// 全部为合成数据，不依赖任何资源文件。
    /// </summary>
    public class DeckBoardLayoutTests : TestRunner
    {
        private const float CardW = 100f;
        private const float CardH = 140f;
        private const float ColW = 110f;
        private const float Offset0 = 28f;
        private const float Gap = 8f;
        private const float Eps = 0.01f;

        private static DeckBoardLayout.DeckBoardParams P(float columnH = 600f, DeckBoardLayout.DeckSortMode mode = DeckBoardLayout.DeckSortMode.CostFirst)
            => new DeckBoardLayout.DeckBoardParams
            {
                CardW = CardW,
                CardH = CardH,
                ColumnW = ColW,
                ColumnH = columnH,
                Offset0 = Offset0,
                GroupGap = Gap,
                MaxPerGroup = 50,
                Mode = mode,
            };

        private static DeckBoardLayout.DeckBoardCard Card(string path, int cost, int color, int count, string uid = null)
            => new DeckBoardLayout.DeckBoardCard
            {
                TemplatePath = path,
                CardName = path,
                Uid = uid ?? path,
                Cost = cost,
                ColorKey = color,
                Count = count,
            };

        private static void Near(float expected, float actual, string msg)
        {
            if (System.Math.Abs(expected - actual) > Eps)
                throw new AssertFailedException($"{msg}：期望 {expected:0.###} 实际 {actual:0.###}");
        }

        // ==================== ① 排序 ====================

        /// <summary>费用降序：高费在前；费用 -1（不可直接打出）垫底。</summary>
        public void TestSortCostFirst()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("c1", 5, 0, 1, "JC001"),
                Card("c2", 2, 0, 1, "JC002"),
                Card("c3", -1, 0, 1, "JC003"),
                Card("c4", 3, 0, 1, "JC004"),
            };
            var r = DeckBoardLayout.Compute(cards, P());
            Assert.AreEqual(4, r.Groups.Count, "组数");
            Assert.AreEqual("c1", r.Groups[0].TemplatePath, "费用 5 应在最前");
            Assert.AreEqual("c4", r.Groups[1].TemplatePath, "费用 3 第二");
            Assert.AreEqual("c2", r.Groups[2].TemplatePath, "费用 2 第三");
            Assert.AreEqual("c3", r.Groups[3].TemplatePath, "费用 -1（不可打出）应垫底");
        }

        /// <summary>颜色升序为主键、费用降序为次键。</summary>
        public void TestSortColorFirst()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("red", 1, 8, 1, "JC001"),      // 红
                Card("brown5", 5, 0, 1, "JC002"),   // 棕（中立）
                Card("brown9", 9, 0, 1, "JC003"),   // 棕（中立）
                Card("blue", 4, 6, 1, "JC004"),     // 蓝
            };
            var r = DeckBoardLayout.Compute(cards, P(mode: DeckBoardLayout.DeckSortMode.ColorFirst));
            Assert.AreEqual("brown9", r.Groups[0].TemplatePath, "棕内高费先（9）");
            Assert.AreEqual("brown5", r.Groups[1].TemplatePath, "棕内低费后（5）");
            Assert.AreEqual("blue", r.Groups[2].TemplatePath, "蓝(6) 在棕(0) 之后");
            Assert.AreEqual("red", r.Groups[3].TemplatePath, "红(8) 最后");
        }

        /// <summary>颜色与费用完全相同 ⇒ 按卡片编号升序（Ordinal，不依赖平台排序器）。</summary>
        public void TestSortUidTieBreak()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("b", 2, 1, 1, "JC002_z"),
                Card("a", 2, 1, 1, "JC001_a"),
                Card("c", 2, 1, 1, "JC003_c"),
            };
            var r = DeckBoardLayout.Compute(cards, P());
            Assert.AreEqual("a", r.Groups[0].TemplatePath, "JC001 应最先");
            Assert.AreEqual("b", r.Groups[1].TemplatePath, "JC002 第二");
            Assert.AreEqual("c", r.Groups[2].TemplatePath, "JC003 第三");
        }

        /// <summary>多色卡取枚举值最小者；空/脏值回落棕(0)。</summary>
        public void TestColorKeyOf()
        {
            Assert.AreEqual(2, DeckBoardLayout.ColorKeyOf(new List<int> { 6, 2 }), "多色取最小（白2 < 蓝6）");
            Assert.AreEqual(0, DeckBoardLayout.ColorKeyOf(new List<int>()), "空表回落棕");
            Assert.AreEqual(0, DeckBoardLayout.ColorKeyOf(new List<int> { -1 }), "-1 哨兵被过滤后回落棕");
            Assert.AreEqual(0, DeckBoardLayout.ColorKeyOf(new List<int> { 9 }), "越界值被过滤后回落棕");
            Assert.AreEqual(7, DeckBoardLayout.ColorKeyOf(new List<int> { 7, 9 }), "黑(7) 合法、9 被过滤");
        }

        // ==================== ② 分组 / 50 张切分 ====================

        /// <summary>同名组上限 50 张，超出部分另起一组（顺序保持）。</summary>
        public void TestGroupSplitAt50()
        {
            AssertSplit(51, new[] { 50, 1 });
            AssertSplit(50, new[] { 50 });
            AssertSplit(100, new[] { 50, 50 });
            AssertSplit(101, new[] { 50, 50, 1 });
        }

        private void AssertSplit(int copies, int[] expect)
        {
            var list = new List<DeckBoardLayout.DeckBoardCard> { Card("x", 1, 0, copies, "JC001") };
            var groups = DeckBoardLayout.BuildGroups(list, 50);
            Assert.AreEqual(expect.Length, groups.Count, $"{copies} 张应切成 {expect.Length} 组");
            for (int i = 0; i < expect.Length; i++)
            {
                Assert.AreEqual(expect[i], groups[i].Count, $"{copies} 张第 {i} 组的张数");
                Assert.AreEqual("x", groups[i].TemplatePath, "切分后仍指向同一模板");
            }
        }

        // ==================== ③ 换列 / 压缩 ====================

        /// <summary>放得下就同列追加，异名组之间留空隙、不重叠。</summary>
        public void TestPackNoWrap()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("a", 3, 0, 2, "JC001"),    // rawH = 140 + 28 = 168
                Card("b", 2, 0, 2, "JC002"),    // 168
                Card("c", 1, 0, 2, "JC003"),    // 168 → 168+8+168+8+168 = 520 ≤ 600
                Card("d", 0, 0, 2, "JC004"),    // 520+8+168 = 696 > 600 → 换列
            };
            var r = DeckBoardLayout.Compute(cards, P(600f));
            Assert.AreEqual(4, r.Groups.Count, "组数");
            Assert.AreEqual(0, r.Groups[0].Column, "a 第 0 列");
            Assert.AreEqual(0, r.Groups[1].Column, "b 第 0 列");
            Assert.AreEqual(0, r.Groups[2].Column, "c 第 0 列");
            Assert.AreEqual(1, r.Groups[3].Column, "d 应换到第 1 列");
            Assert.AreEqual(2, r.ColumnCount, "共 2 列");

            Near(0f, r.Groups[0].StartY, "a 从列顶开始");
            Near(168f + Gap, r.Groups[1].StartY, "b 紧接 a 下方并留空隙");
            Near(2 * (168f + Gap), r.Groups[2].StartY, "c 接在 b 下方");
            Near(0f, r.Groups[3].StartY, "换列后从新列列顶开始");

            // 异名分离：同列相邻组不重叠
            for (int i = 1; i < 3; i++)
            {
                float prevBottom = r.Groups[i - 1].StartY + r.Groups[i - 1].GroupHeight;
                Assert.IsTrue(r.Groups[i].StartY >= prevBottom + Gap - Eps,
                    $"第 {i} 组应对上一组下方留出 {Gap} 空隙");
            }
        }

        /// <summary>非空列装不下 ⇒ 整组原子移到下一列，**不压缩**。</summary>
        public void TestWrapGroupAtomicNotCompressed()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("a", 3, 0, 10, "JC001"),   // rawH = 140 + 9*28 = 392
                Card("b", 2, 0, 5, "JC002"),    // rawH = 140 + 4*28 = 252 → 392+8+252 = 652 > 600
            };
            var r = DeckBoardLayout.Compute(cards, P(600f));
            Assert.AreEqual(1, r.Groups[1].Column, "整组应移到第 1 列");
            Near(0f, r.Groups[1].StartY, "移到新列后从列顶开始");
            Near(Offset0, r.Groups[1].Offset, "非空列溢出时**不压缩**，保持初始 offset");
            Assert.AreEqual(5, r.Groups[1].Count, "整组不拆分");
            Near(252f, r.Groups[1].GroupHeight, "组高 = 卡高 + (张数-1)×offset");
        }

        /// <summary>空列且原始高度超列高 ⇒ 压缩 offset，使整组在一列内铺满。</summary>
        public void TestCompressInEmptyColumn()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard> { Card("a", 3, 0, 20, "JC001") };  // rawH = 672 > 600
            var r = DeckBoardLayout.Compute(cards, P(600f));
            var g = r.Groups[0];
            float expectOffset = (600f - CardH) / (20 - 1);
            Assert.AreEqual(0, g.Column, "压缩后仍在第 0 列");
            Near(0f, g.StartY, "压缩组从列顶开始");
            Near(expectOffset, g.Offset, "压缩 offset = (列高-卡高)/(张数-1)");
            Near(600f, g.GroupHeight, "压缩后组高 == 列高（铺满一列）");
            Assert.AreEqual(1, r.ColumnCount, "压缩后只占 1 列");
        }

        /// <summary>压缩不设下限：列高只比卡高一点点时 offset 会压到 0（卡名完全重叠）。</summary>
        public void TestCompressToZeroOffset()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard> { Card("a", 3, 0, 50, "JC001") };
            var r = DeckBoardLayout.Compute(cards, P(CardH));   // 列高 == 卡高
            var g = r.Groups[0];
            Near(0f, g.Offset, "列高 == 卡高时压缩 offset = 0");
            Near(CardH, g.GroupHeight, "组高 == 列高");
            Assert.IsTrue(g.Offset >= -Eps, "压缩只压不张（offset 不为负）");
        }

        /// <summary>单张组没有堆叠：offset = 0、组高 = 卡高。</summary>
        public void TestSingleCardGroup()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard> { Card("a", 3, 0, 1, "JC001") };
            var r = DeckBoardLayout.Compute(cards, P(600f));
            var g = r.Groups[0];
            Near(0f, g.Offset, "单张组 offset = 0");
            Near(CardH, g.GroupHeight, "单张组组高 = 卡高");
        }

        /// <summary>列高小于卡高时入口钳制到卡高（避免出现负 offset / 内容被裁）。</summary>
        public void TestColumnHeightClamped()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("a", 3, 0, 1, "JC001"),
                Card("b", 2, 0, 1, "JC002"),   // 140+8+140 = 288 > 100 → 但列高被钳到 140 ⇒ 换列
            };
            var r = DeckBoardLayout.Compute(cards, P(100f));   // 列高 100 < 卡高 140
            Near(CardH, r.ContentHeight, "列高被钳制到卡高");
            Assert.AreEqual(1, r.Groups[1].Column, "钳制后第 2 组仍放不下 ⇒ 换列");
        }

        /// <summary>内容宽度 / 列数汇总。</summary>
        public void TestContentWidth()
        {
            var cards = new List<DeckBoardLayout.DeckBoardCard>
            {
                Card("a", 3, 0, 1, "JC001"),   // 140
                Card("b", 2, 0, 1, "JC002"),   // 140 → 288
                Card("c", 1, 0, 1, "JC003"),   // 288+8+140 = 436 ≤ 600 → 第 0 列
                Card("d", 1, 0, 1, "JC004"),   // 436+8+140 = 584 ≤ 600 → 第 0 列
                Card("e", 1, 0, 1, "JC005"),   // 584+8+140 = 732 > 600 → 第 1 列
            };
            var r = DeckBoardLayout.Compute(cards, P(600f));
            Assert.AreEqual(2, r.ColumnCount, "共 2 列");
            Near(2 * ColW, r.ContentWidth, "内容宽 = 列数 × 列宽");
            Near(600f, r.ContentHeight, "内容高 = 列高");
        }

        /// <summary>空卡组：不崩、1 列、内容宽 = 1 × 列宽。</summary>
        public void TestEmptyDeck()
        {
            var r = DeckBoardLayout.Compute(new List<DeckBoardLayout.DeckBoardCard>(), P(600f));
            Assert.AreEqual(0, r.Groups.Count, "空卡组没有组");
            Assert.AreEqual(1, r.ColumnCount, "空卡组仍算 1 列");
            Near(ColW, r.ContentWidth, "空卡组内容宽 = 1 列");
        }
    }
}
