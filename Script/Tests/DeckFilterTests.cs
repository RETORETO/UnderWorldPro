using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.IO;
using Script.Net;
using Script.UI;

namespace Script.Tests
{
    /// <summary>
    /// 【卡池结构化检索 · 2026-10-17】DeckFilterModel 匹配语义 + 索引一致性回归。
    ///
    /// 锁定用户拍板的四条语义，防止后续改动悄悄改行为：
    ///   ① 7 属性 = ≥（Defense == -1 的卡天然被任何非负阈值排除）；
    ///   ② 忠诚 = AND 组合且点数 ≥ 输入值；
    ///   ③ 类别 = 位掩码**按位包含**（选"角色"要能命中 `角色|附属` 复合卡）；
    ///   ④ 魔法领域 = 集合包含（空列表 = 无领域）。
    /// 另加两条真实索引一致性用例（条数、以及若干条件的确切命中数），
    /// 索引缺失时会明确失败并提示重跑 CardIndexRunner（构建期必生成、且已入仓库）。
    /// </summary>
    public class DeckFilterTests : TestRunner
    {
        // ==================== 语义单元用例（合成数据，不依赖文件） ====================

        public void TestCategoryBitmaskContains()
        {
            var plain = Entry(cat: (int)CardCategory.Character);                                  // 1 角色
            var combo = Entry(cat: (int)CardCategory.Character | (int)CardCategory.Attachment);   // 5 角色|附属
            var sorcery = Entry(cat: (int)CardCategory.Sorcery);                                  // 2 事务

            var mChar = new DeckFilterModel { CategoryMask = (int)CardCategory.Character };
            var mAtt = new DeckFilterModel { CategoryMask = (int)CardCategory.Attachment };
            var mSor = new DeckFilterModel { CategoryMask = (int)CardCategory.Sorcery };
            Prep(mChar, mAtt, mSor);

            Assert.IsTrue(mChar.Matches(plain), "纯角色应命中『角色』");
            Assert.IsTrue(mChar.Matches(combo), "角色|附属 复合卡也应命中『角色』（按位包含）");
            Assert.IsFalse(mChar.Matches(sorcery), "事务卡不应命中『角色』");
            Assert.IsTrue(mAtt.Matches(combo), "角色|附属 复合卡应命中『附属』");
            Assert.IsFalse(mAtt.Matches(plain), "纯角色不应命中『附属』");
            Assert.IsTrue(mSor.Matches(sorcery), "事务卡应命中『事务』");
        }

        public void TestAttributeGreaterOrEqual()
        {
            var e = Entry(inv: 3, str: 2, pow: 1, def: -1, fsInv: 0, fsStr: 0, fsPow: 0);

            var ge3 = new DeckFilterModel(); ge3.MinAttr[DeckFilterModel.AttrInvestigation] = 3;
            var ge2 = new DeckFilterModel(); ge2.MinAttr[DeckFilterModel.AttrInvestigation] = 2;
            var ge4 = new DeckFilterModel(); ge4.MinAttr[DeckFilterModel.AttrInvestigation] = 4;
            Prep(ge3, ge2, ge4);

            Assert.IsTrue(ge3.Matches(e), "调查=3 应满足 ≥3");
            Assert.IsTrue(ge2.Matches(e), "调查=3 应满足 ≥2");
            Assert.IsFalse(ge4.Matches(e), "调查=3 不应满足 ≥4");

            // 防御 -1（无防御）必须被任何非负阈值排除
            var def0 = new DeckFilterModel(); def0.MinAttr[DeckFilterModel.AttrDefense] = 0;
            Prep(def0);
            Assert.IsFalse(def0.Matches(e), "Defense=-1（无防御）不应满足 ≥0");
        }

        public void TestMagicDomainFilter()
        {
            var star = Entry(domains: new List<int> { (int)MagicDomain.Star });
            var none = Entry(domains: new List<int>());

            var fStar = new DeckFilterModel { DomainFilter = (int)MagicDomain.Star };
            var fMind = new DeckFilterModel { DomainFilter = (int)MagicDomain.Mind };
            var fNone = new DeckFilterModel { DomainFilter = DeckFilterModel.DomainNone };
            var fAny = new DeckFilterModel { DomainFilter = DeckFilterModel.DomainAny };
            Prep(fStar, fMind, fNone, fAny);

            Assert.IsTrue(fStar.Matches(star), "星空卡应命中『星空』");
            Assert.IsFalse(fMind.Matches(star), "星空卡不应命中『心灵』");
            Assert.IsTrue(fNone.Matches(none), "无领域卡应命中『无领域』");
            Assert.IsFalse(fStar.Matches(none), "无领域卡不应命中『星空』");
            Assert.IsTrue(fAny.Matches(none), "『不限』应命中任何卡");
            Assert.IsTrue(fAny.Matches(star), "『不限』应命中任何卡");
        }

        public void TestLoyaltyAndSemantics()
        {
            var e = Entry(
                colorLoyalties: new List<LoyaltyEntry>
                {
                    new LoyaltyEntry { Value = (int)CardColor.Yellow, Amount = 2 },
                    new LoyaltyEntry { Value = (int)CardColor.Blue, Amount = 1 },
                },
                domainLoyalties: new List<LoyaltyEntry>
                {
                    new LoyaltyEntry { Value = (int)MagicDomain.Star, Amount = 1 },
                });

            var y2 = Cond(true, (int)CardColor.Yellow, 2);
            var y1 = Cond(true, (int)CardColor.Yellow, 1);
            var y3 = Cond(true, (int)CardColor.Yellow, 3);
            var b1 = Cond(true, (int)CardColor.Blue, 1);
            var mind1 = Cond(true, (int)CardColor.Brown, 1);   // 卡没有棕忠诚
            var domeStar1 = Cond(false, (int)MagicDomain.Star, 1);
            var domeNone = Cond(false, (int)MagicDomain.Blood, 1);

            var mSingle = new DeckFilterModel(); mSingle.Loyalties.Add(y2);
            var mGe = new DeckFilterModel(); mGe.Loyalties.Add(y1);
            var mTooHigh = new DeckFilterModel(); mTooHigh.Loyalties.Add(y3);
            var mAbsent = new DeckFilterModel(); mAbsent.Loyalties.Add(mind1);
            var mAnd = new DeckFilterModel(); mAnd.Loyalties.Add(y1); mAnd.Loyalties.Add(b1);
            var mAndFail = new DeckFilterModel(); mAndFail.Loyalties.Add(y1); mAndFail.Loyalties.Add(domeNone);
            var mDomain = new DeckFilterModel(); mDomain.Loyalties.Add(domeStar1);
            Prep(mSingle, mGe, mTooHigh, mAbsent, mAnd, mAndFail, mDomain);

            Assert.IsTrue(mSingle.Matches(e), "黄忠诚 2 应满足 黄≥2");
            Assert.IsTrue(mGe.Matches(e), "黄忠诚 2 应满足 黄≥1（≥ 语义）");
            Assert.IsFalse(mTooHigh.Matches(e), "黄忠诚 2 不应满足 黄≥3");
            Assert.IsFalse(mAbsent.Matches(e), "卡没有棕忠诚 → 不应命中");
            Assert.IsTrue(mAnd.Matches(e), "黄≥1 且 蓝≥1 → 应命中（AND）");
            Assert.IsFalse(mAndFail.Matches(e), "黄≥1 且 鲜血≥1 → 卡无鲜血忠诚，不应命中");
            Assert.IsTrue(mDomain.Matches(e), "领域忠诚 星空≥1 → 应命中");
        }

        public void TestTextAndUniqueFilters()
        {
            var e = Entry(
                name: "力场法师",
                subName: "变体甲",
                subCats: new List<string> { "人类", "法师" },
                unique: "帷幕守望");

            Assert.IsTrue(new DeckFilterModel { NameQuery = "法师" }.PrepAndMatches(e), "卡名子串应命中");
            Assert.IsFalse(new DeckFilterModel { NameQuery = "猫头鹰" }.PrepAndMatches(e), "卡名不匹配不应命中");
            Assert.IsTrue(new DeckFilterModel { SubCardNameQuery = "变体" }.PrepAndMatches(e), "副卡名子串应命中");
            Assert.IsTrue(new DeckFilterModel { SubCategoryQuery = "法师" }.PrepAndMatches(e), "子类别任一命中即可");
            Assert.IsFalse(new DeckFilterModel { SubCategoryQuery = "吸血鬼" }.PrepAndMatches(e), "不在子类别里不应命中");
            Assert.IsTrue(new DeckFilterModel { UniqueOnly = true }.PrepAndMatches(e), "有 UniqueField → 独有卡应命中");

            var notUnique = Entry(name: "某普通卡");
            Assert.IsFalse(new DeckFilterModel { UniqueOnly = true }.PrepAndMatches(notUnique),
                "UniqueField 为空 → 独有卡筛选不应命中");
        }

        public void TestEmptyConditionsAreNoOp()
        {
            var m = new DeckFilterModel();
            m.PrepareForSearch();
            Assert.IsFalse(m.HasStructured, "全部条件为空时 HasStructured 应为 false");
            Assert.IsTrue(m.Matches(Entry()), "无条件应命中任意卡");
            Assert.IsTrue(m.MatchesUid(Entry()), "编号查询为空应视为命中（保持原行为）");
        }

        public void TestUidMatchesFileStem()
        {
            var e = Entry(stem: "jc001_tonglingmaotouying");
            var m = new DeckFilterModel { UidQuery = "jc001" };
            m.PrepareForSearch();
            Assert.IsTrue(m.MatchesUid(e), "编号子串应命中小写化的文件名");
            var m2 = new DeckFilterModel { UidQuery = "  JC  " };
            m2.PrepareForSearch();
            Assert.IsTrue(m2.MatchesUid(e), "编号查询应裁剪空白并 ASCII 折叠后匹配");
            var m3 = new DeckFilterModel { UidQuery = "bq" };
            m3.PrepareForSearch();
            Assert.IsFalse(m3.MatchesUid(e), "不匹配的编号不应命中");
        }

        // ==================== 真实索引一致性（需 Cards/card_index.json） ====================

        public void TestRealIndexLoadsAndCounts()
        {
            Assert.IsTrue(CardSearchIndex.EnsureLoaded(),
                "卡池索引未加载成功：请确认 Cards/card_index.json 存在（重跑 res://Script/Tools/CardIndexRunner.cs 生成）");
            Assert.AreEqual(698, CardSearchIndex.Count, "索引条目数应为 698（玩家卡）");

            // 抽查 JC004 力场法师：黄忠诚 2、类别=角色、有心灵领域
            DeckSearchIndexEntry jc004 = null;
            foreach (var c in CardSearchIndex.Cards)
                if (c.UID == "JC004_lichangfashi") { jc004 = c; break; }
            Assert.IsNotNull(jc004, "索引里应能找到 JC004_lichangfashi");
            Assert.AreEqual((int)CardCategory.Character, jc004.CategoryFlagValue, "JC004 类别应为角色");
            Assert.IsTrue(jc004.ColorLoyalties != null && jc004.ColorLoyalties.Count == 1, "JC004 应恰有 1 条颜色忠诚");
            Assert.AreEqual((int)CardColor.Yellow, jc004.ColorLoyalties[0].Value, "JC004 颜色忠诚应为黄");
            Assert.AreEqual(2, jc004.ColorLoyalties[0].Amount, "JC004 黄忠诚点数应为 2");
        }

        public void TestRealIndexFilterCounts()
        {
            Assert.IsTrue(CardSearchIndex.EnsureLoaded(), "卡池索引未加载成功（见上一条用例说明）");
            var cards = CardSearchIndex.Cards;

            // 类别位掩码"包含"的关键证据：角色总数 = 纯角色 369 + 角色|附属 复合 4 = 373
            Assert.AreEqual(373, CountMatches(cards, new DeckFilterModel { CategoryMask = (int)CardCategory.Character }),
                "『角色』应命中 373 张（含 4 张角色|附属 复合卡）");
            Assert.AreEqual(143, CountMatches(cards, new DeckFilterModel { CategoryMask = (int)CardCategory.Attachment }),
                "『附属』应命中 143 张（含 4 张复合卡）");
            Assert.AreEqual(339, CountMatches(cards, new DeckFilterModel { DomainFilter = DeckFilterModel.DomainNone }),
                "『无领域』应为 339 张");
            Assert.AreEqual(92, CountMatches(cards, new DeckFilterModel { DomainFilter = (int)MagicDomain.Star }),
                "『星空』应为 92 张");
            Assert.AreEqual(31, CountMatches(cards, new DeckFilterModel { UniqueOnly = true }),
                "仅独有卡应为 31 张");
            Assert.AreEqual(214, CountMatches(cards, new DeckFilterModel { SubCategoryQuery = "人类" }),
                "子类别含『人类』应为 214 张");
        }

        /// <summary>编号搜索行为不变：含 jc 应为 135 张（等同改造前的文件名子串过滤）。</summary>
        public void TestRealIndexUidCount()
        {
            Assert.IsTrue(CardSearchIndex.EnsureLoaded(), "卡池索引未加载成功");
            var m = new DeckFilterModel { UidQuery = "jc" };
            m.PrepareForSearch();
            int n = 0;
            foreach (var c in CardSearchIndex.Cards)
                if (m.MatchesUid(c)) n++;
            Assert.AreEqual(135, n, "编号含 jc 应为 135 张（原按文件名搜索行为不变）");
        }

        // ==================== 辅助 ====================

        private static int CountMatches(List<DeckSearchIndexEntry> cards, DeckFilterModel m)
        {
            m.PrepareForSearch();
            int n = 0;
            foreach (var c in cards)
                if (m.MatchesUid(c) && m.Matches(c)) n++;
            return n;
        }

        private static void Prep(params DeckFilterModel[] models)
        {
            foreach (var m in models) m.PrepareForSearch();
        }

        private static LoyaltyCond Cond(bool isColor, int value, int amount)
            => new LoyaltyCond { IsColor = isColor, Value = value, Amount = amount };

        /// <summary>构造一条已折叠（模拟 CardSearchIndex.Prepare 之后）的索引条目。</summary>
        private static DeckSearchIndexEntry Entry(
            string stem = "test_stem",
            string name = "",
            string subName = "",
            string unique = "",
            int cat = 0,
            List<string> subCats = null,
            List<int> domains = null,
            List<LoyaltyEntry> colorLoyalties = null,
            List<LoyaltyEntry> domainLoyalties = null,
            int inv = 0, int str = 0, int pow = 0, int def = -1,
            int fsInv = 0, int fsStr = 0, int fsPow = 0)
        {
            var e = new DeckSearchIndexEntry
            {
                FileStem = stem,
                CardName = name,
                SubCardName = subName,
                UniqueField = unique,
                CategoryFlagValue = cat,
                MagicDomains = domains ?? new List<int>(),
                ColorLoyalties = colorLoyalties ?? new List<LoyaltyEntry>(),
                DomainLoyalties = domainLoyalties ?? new List<LoyaltyEntry>(),
                Investigation = inv,
                Strength = str,
                Power = pow,
                Defense = def,
                FirstStrikeInvestigation = fsInv,
                FirstStrikeStrength = fsStr,
                FirstStrikePower = fsPow,
                SubCategories = subCats ?? new List<string>(),
                // 折叠字段与 CardSearchIndex.Prepare 保持一致
                FileStemFold = WebSafeText.ToLowerAscii(stem),
                NameFold = WebSafeText.ToLowerAscii(name ?? ""),
                SubCardNameFold = WebSafeText.ToLowerAscii(subName ?? ""),
                SubCategoriesFold = new List<string>(),
            };
            if (subCats != null)
                foreach (var s in subCats) e.SubCategoriesFold.Add(WebSafeText.ToLowerAscii(s));
            return e;
        }
    }

    /// <summary>测试小工具：一行内完成 PrepareForSearch + Matches。</summary>
    internal static class DeckFilterModelTestExt
    {
        public static bool PrepAndMatches(this DeckFilterModel m, DeckSearchIndexEntry e)
        {
            m.PrepareForSearch();
            return m.Matches(e);
        }
    }
}
