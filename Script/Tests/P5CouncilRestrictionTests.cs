using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using ComparisonType = Script.Data.Cards.CouncilRestriction.ComparisonType;

namespace Script.Tests
{
    /// <summary>
    /// 【秘社构筑限制】按用户 2026-09 口径实现并锁定：
    ///  1. `CouncilRestriction.Describe()` 生成可读限制文案（供"卡组违规"提示与 UI 展示）；
    ///  2. `CouncilRestriction.GetActualCount()` / `IsSatisfiedBy()` 按筛选条件统计与判定；
    ///  3. `CouncilCardData.ValidateDeckDetailed()` 返回**逐条**未满足限制的可读明细；
    ///  4. 线上资源 `MSJC001_weimushouwang`（帷幕守望）确实携带"需包含 25 张或更多黄色牌"限制，
    ///     且该限制能正确通过 / 拒绝相应卡组。
    /// </summary>
    public class P5CouncilRestrictionTests : TestRunner
    {
        private const string Msjc001Path = "res://Cards/Data/CouncilCards/JC/MSJC001_weimushouwang.tres";
        private const string YellowColorName = "黄色";

        // ==================== ① Describe 文案 ====================

        public void TestDescribe_需包含25张黄色牌()
        {
            var r = new CouncilRestriction
            {
                FilterByColor = true,
                TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual,
                RequiredCount = 25,
            };

            Assert.AreEqual("需包含 25 张或更多黄色牌", r.Describe(),
                "Describe 文案应为『需包含 25 张或更多黄色牌』");
        }

        public void TestDescribe_带实际数量()
        {
            var r = new CouncilRestriction
            {
                FilterByColor = true,
                TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual,
                RequiredCount = 25,
            };

            Assert.AreEqual("需包含 25 张或更多黄色牌（当前 18 张）", r.Describe(18),
                "传入实际数量时应附加『（当前 N 张）』");
        }

        public void TestDescribe_其他比较关系()
        {
            Assert.AreEqual("需包含 10 张或更少蓝色牌",
                new CouncilRestriction { FilterByColor = true, TargetColor = CardColor.Blue, Comparison = ComparisonType.LessOrEqual, RequiredCount = 10 }.Describe(),
                "LessOrEqual 文案");
            Assert.AreEqual("需恰好包含 3 张绿色牌",
                new CouncilRestriction { FilterByColor = true, TargetColor = CardColor.Green, Comparison = ComparisonType.Equal, RequiredCount = 3 }.Describe(),
                "Equal 文案");
        }

        // ==================== ② 计数与判定 ====================

        public void TestGetActualCount_按颜色统计()
        {
            var r = new CouncilRestriction
            {
                FilterByColor = true,
                TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual,
                RequiredCount = 25,
            };

            var deck = new List<PlayerCardData>();
            for (int i = 0; i < 25; i++) deck.Add(Card(CardColor.Yellow));
            for (int i = 0; i < 10; i++) deck.Add(Card(CardColor.Brown));

            Assert.AreEqual(25, r.GetActualCount(deck), "应只统计黄色牌数量");
            Assert.IsTrue(r.IsSatisfiedBy(deck), "25 张黄牌应恰好满足『需 ≥25 张黄牌』");
        }

        public void TestIsSatisfiedBy_边界_24与25张()
        {
            var r = new CouncilRestriction
            {
                FilterByColor = true,
                TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual,
                RequiredCount = 25,
            };

            var deck24 = new List<PlayerCardData>();
            for (int i = 0; i < 24; i++) deck24.Add(Card(CardColor.Yellow));
            Assert.IsFalse(r.IsSatisfiedBy(deck24), "24 张黄牌不满足（＜25）");

            deck24.Add(Card(CardColor.Yellow));
            Assert.IsTrue(r.IsSatisfiedBy(deck24), "25 张黄牌应满足（边界包含）");
        }

        public void TestIsSatisfiedBy_空卡组不满足()
        {
            var r = new CouncilRestriction
            {
                FilterByColor = true,
                TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual,
                RequiredCount = 25,
            };
            Assert.IsFalse(r.IsSatisfiedBy(new List<PlayerCardData>()), "空卡组应不满足");
            Assert.AreEqual(0, r.GetActualCount(null), "null 卡组统计为 0（不抛异常）");
        }

        // ==================== ③ ValidateDeckDetailed ====================

        public void TestValidateDeckDetailed_返回逐条未满足明细()
        {
            var council = new CouncilCardData { UID = "TS_cr_council", CardName = "测试秘社" };
            council.Restrictions.Add(new CouncilRestriction
            {
                FilterByColor = true, TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual, RequiredCount = 25,
            });
            council.Restrictions.Add(new CouncilRestriction
            {
                FilterByColor = true, TargetColor = CardColor.Green,
                Comparison = ComparisonType.GreaterOrEqual, RequiredCount = 10,
            });

            var deck = new List<PlayerCardData>();
            for (int i = 0; i < 20; i++) deck.Add(Card(CardColor.Yellow));   // 黄 20（缺 25）→ 不合格
            for (int i = 0; i < 12; i++) deck.Add(Card(CardColor.Green));    // 绿 12（≥10）→ 合格

            var failures = council.ValidateDeckDetailed(deck);
            Assert.AreEqual(1, failures.Count, "只有黄牌限制不合格，应返回 1 条明细");
            Assert.IsTrue(failures[0].Contains(YellowColorName) && failures[0].Contains("25"),
                $"明细应说明『黄色牌』与『25』，实际：{failures[0]}");
            Assert.IsTrue(failures[0].Contains("20"), $"明细应附带实际数量 20，实际：{failures[0]}");

            Assert.IsFalse(council.ValidateDeck(deck), "ValidateDeck 应返回 false");
        }

        public void TestValidateDeckDetailed_全部满足返回空()
        {
            var council = new CouncilCardData { UID = "TS_cr_council2", CardName = "测试秘社" };
            council.Restrictions.Add(new CouncilRestriction
            {
                FilterByColor = true, TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual, RequiredCount = 25,
            });

            var deck = new List<PlayerCardData>();
            for (int i = 0; i < 30; i++) deck.Add(Card(CardColor.Yellow));

            Assert.AreEqual(0, council.ValidateDeckDetailed(deck).Count, "全部满足应返回空明细");
            Assert.IsTrue(council.ValidateDeck(deck), "ValidateDeck 应返回 true");
        }

        public void TestHasRestrictions()
        {
            var noR = new CouncilCardData { UID = "TS_cr_none", CardName = "无限制秘社" };
            Assert.IsFalse(noR.HasRestrictions, "无限制秘社 HasRestrictions=false");

            var withR = new CouncilCardData { UID = "TS_cr_has", CardName = "有限制秘社" };
            withR.Restrictions.Add(new CouncilRestriction
            {
                FilterByColor = true, TargetColor = CardColor.Yellow,
                Comparison = ComparisonType.GreaterOrEqual, RequiredCount = 25,
            });
            Assert.IsTrue(withR.HasRestrictions, "有限制秘社 HasRestrictions=true");
        }

        // ==================== ④ 线上资源 MSJC001 ====================

        public void TestMsjc001_资源携带25张黄色牌限制()
        {
            var council = ResourceLoader.Load<CouncilCardData>(Msjc001Path);
            Assert.IsTrue(council != null, $"应能加载秘社资源：{Msjc001Path}");
            Assert.IsTrue(council.HasRestrictions,
                "MSJC001_weimushouwang 应携带构筑限制（需 25 张黄色牌）");
            Assert.AreEqual(1, council.Restrictions.Count, "当前应恰好 1 条限制");

            var r = council.Restrictions[0];
            Assert.IsTrue(r.FilterByColor, "限制应基于颜色筛选");
            Assert.AreEqual(CardColor.Yellow, r.TargetColor, "目标颜色应为黄色");
            Assert.AreEqual(ComparisonType.GreaterOrEqual, r.Comparison, "比较关系应为 GreaterOrEqual");
            Assert.AreEqual(25, r.RequiredCount, "要求数量应为 25");
            Assert.IsTrue(r.Describe().Contains("25") && r.Describe().Contains(YellowColorName),
                $"限制文案应含『25』与『黄色』，实际：{r.Describe()}");
        }

        public void TestMsjc001_卡组合规判定()
        {
            var council = ResourceLoader.Load<CouncilCardData>(Msjc001Path);
            Assert.IsTrue(council != null, "应能加载 MSJC001");

            // 合规：25 黄
            var okDeck = new List<PlayerCardData>();
            for (int i = 0; i < 25; i++) okDeck.Add(Card(CardColor.Yellow));
            Assert.IsTrue(council.ValidateDeck(okDeck), "25 张黄牌应满足 MSJC001 构筑限制");

            // 违规：24 黄
            var badDeck = new List<PlayerCardData>();
            for (int i = 0; i < 24; i++) badDeck.Add(Card(CardColor.Yellow));
            Assert.IsFalse(council.ValidateDeck(badDeck), "24 张黄牌应不满足 MSJC001 构筑限制");

            var failures = council.ValidateDeckDetailed(badDeck);
            Assert.AreEqual(1, failures.Count, "应给出 1 条不合规明细");
            Assert.IsTrue(failures[0].Contains(YellowColorName), $"明细应说明颜色，实际：{failures[0]}");
        }

        // ==================== 辅助 ====================

        private static PlayerCardData Card(CardColor color)
        {
            var c = new PlayerCardData { UID = "TS_cr_card", CardName = "探针卡", Defense = 1 };
            c.Colors.Add(color);
            return c;
        }
    }
}
