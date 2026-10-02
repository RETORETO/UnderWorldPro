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

        // ==================== ⑤ 白名单模式（MSJC010~017，用户 2026-09 口径） ====================
        // 口径：① 一张牌合法 ⟺ 含白名单颜色（"只要含"，不要求全含）；
        //      ② 或命中任一条例外（OR）；
        //      ③ 都不满足＝违规，违规总数须 ≤ ExceptionMaxCount；
        //      ④ 白名单模式下 GetActualCount 返回**违规张数**；
        //      ⑤ 能力图标按"战力 > 0"判定，**不含先手战力**。

        public void TestWhitelist_含白名单颜色即合法()
        {
            var r = Whitelist(CardColor.Yellow, CardColor.Brown);
            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Yellow))), "黄色牌在白名单内 ⇒ 合法");
            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Brown))), "**棕色＝中立** ⇒ 合法");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Green))), "绿色牌不在白名单且无例外 ⇒ 违规");
            Assert.AreEqual(1, r.GetActualCount(Deck(Probe(CardColor.Green))), "白名单模式下统计的是**违规张数**");
            Assert.AreEqual(0, r.GetActualCount(Deck(Probe(CardColor.Yellow), Probe(CardColor.Brown))), "全合法 ⇒ 违规 0 张");
        }

        public void TestWhitelist_只要含白名单颜色就合法()
        {
            // 用户 2026-09 口径："只要含白名单颜色就合法"（不要求全部颜色都在白名单里）。
            var r = Whitelist(CardColor.Yellow);
            var dual = Probe(CardColor.Yellow);
            dual.Colors.Add(CardColor.Green);          // 黄＋绿双色
            Assert.IsTrue(r.IsSatisfiedBy(Deck(dual)), "含黄色即合法（即使同时含白名单外的绿色）");
        }

        public void TestWhitelist_空卡组与null安全()
        {
            var r = Whitelist(CardColor.Yellow);
            Assert.IsTrue(r.IsSatisfiedBy(new List<PlayerCardData>()), "空卡组违规 0 张 ⇒ 满足");
            Assert.AreEqual(0, r.GetActualCount(null), "null 卡组统计为 0（不抛异常）");
        }

        // ---- 例外：010 貘组（其他派系【星】领域的牌） ----

        public void TestWhitelist_例外010_星领域()
        {
            var r = Whitelist(CardColor.Yellow, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule
            { FilterByDomain = true, TargetDomains = new Godot.Collections.Array<MagicDomain> { MagicDomain.Star } });

            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Green, domains: new[] { MagicDomain.Star }))),
                "非黄派系但【星】领域 ⇒ 例外放行");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Green, domains: new[] { MagicDomain.Mind }))),
                "非黄派系且【心】领域 ⇒ 不在例外内");
            // 无领域卡（哨兵值 -1）不得被"领域例外"误放行
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Green))), "无领域卡不应命中【星】领域例外");
        }

        // ---- 例外：011 S．P．T执行部（带［黑刀］图标的人类角色牌） ----

        public void TestWhitelist_例外011_黑刀人类角色()
        {
            var r = Whitelist(CardColor.Green, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule
            {
                FilterByCategory = true, TargetCategoryFlag = CardCategory.Character,
                FilterBySubCategories = true,
                TargetSubCategories = new Godot.Collections.Array<string> { "人类" },
                FilterByAbilityIcon = true, TargetAbilityIcon = "黑刀",
            });

            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }, strength: 1))),
                "非绿派系 ＋ 人类角色 ＋ 战力>0（［黑刀］）⇒ 例外放行");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }, strength: 0))),
                "人类角色但战力=0（无［黑刀］）⇒ 不放行");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "法师" }, strength: 2))),
                "非人类 ⇒ 不放行（类别∧子类别∧图标 三者 AND）");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }, strength: 0,
                    category: CardCategory.Sorcery))),
                "非角色类别 ⇒ 不放行");
        }

        /// <summary>用户口径关键点：**只按战力判定，不含先手战力**（FirstStrikeStrength 不算［黑刀］）。</summary>
        public void TestWhitelist_例外011_不计先手战力()
        {
            var r = Whitelist(CardColor.Green);
            r.ExceptionRules.Add(new CouncilExceptionRule
            {
                FilterByCategory = true, TargetCategoryFlag = CardCategory.Character,
                FilterByAbilityIcon = true, TargetAbilityIcon = "黑刀",
            });

            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, strength: 0, firstStrikeStrength: 3))),
                "**只有先手战力、战力=0 ⇒ 不算［黑刀］**（用户 2026-09 口径：不要计算先手战力）");
            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, strength: 1, firstStrikeStrength: 0))),
                "战力>0 ⇒ 算［黑刀］");
        }

        public void TestWhitelist_未登记能力图标failClosed()
        {
            var r = Whitelist(CardColor.Green);
            r.ExceptionRules.Add(new CouncilExceptionRule
            { FilterByAbilityIcon = true, TargetAbilityIcon = "紫刀" });   // 拼错/未登记
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, strength: 5))),
                "未登记的能力图标名 ⇒ 不命中（fail-closed，不静默放行）");
        }

        // ---- 例外：012 爱弗罗德家族（吸血鬼或奴仆角色牌） ----

        public void TestWhitelist_例外012_子类别OR()
        {
            var r = Whitelist(CardColor.Blue, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule
            {
                FilterByCategory = true, TargetCategoryFlag = CardCategory.Character,
                FilterBySubCategories = true,
                TargetSubCategories = new Godot.Collections.Array<string> { "吸血鬼", "奴仆" },
            });

            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "吸血鬼" }))), "吸血鬼 ⇒ 放行");
            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "奴仆" }))), "奴仆 ⇒ 放行");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }))), "人类 ⇒ 不放行");
        }

        // ---- 例外：014 哨兵（不具有魔法领域图标的牌） ----

        public void TestWhitelist_例外014_无魔法领域()
        {
            var r = Whitelist(CardColor.Gray, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule { RequireNoDomain = true });

            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red))),
                "**无领域卡（哨兵值 -1）⇒ 命中『不具有魔法领域图标』例外**");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, domains: new[] { MagicDomain.Mind }))),
                "有【心】领域 ⇒ 不在例外内");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, domains: new[] { MagicDomain.Death }))),
                "有【死】领域 ⇒ 不在例外内");
        }

        // ---- 例外：016 颂亡者（其他派系【死】领域的牌） ----

        public void TestWhitelist_例外016_死领域()
        {
            var r = Whitelist(CardColor.Black, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule
            { FilterByDomain = true, TargetDomains = new Godot.Collections.Array<MagicDomain> { MagicDomain.Death } });

            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.White, domains: new[] { MagicDomain.Death }))), "【死】领域 ⇒ 放行");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.White, domains: new[] { MagicDomain.Star }))), "【星】领域 ⇒ 不放行");
        }

        // ---- 例外：017 梦魇族群（灵体角色 **或** 梦境牌） ----

        public void TestWhitelist_例外017_两条OR()
        {
            var r = Whitelist(CardColor.Purple, CardColor.Brown);
            // ① 关键字「灵体」∧ 类别「角色」
            r.ExceptionRules.Add(new CouncilExceptionRule
            {
                FilterByCategory = true, TargetCategoryFlag = CardCategory.Character,
                FilterByKeyword = true, TargetKeyword = "灵体",
            });
            // ② 子类别「梦境」
            r.ExceptionRules.Add(new CouncilExceptionRule
            {
                FilterBySubCategories = true,
                TargetSubCategories = new Godot.Collections.Array<string> { "梦境" },
            });

            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, inherentKeyword: "灵体"))), "灵体角色 ⇒ 例外①放行");
            Assert.IsTrue(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, subCategories: new[] { "梦境" }))), "梦境牌 ⇒ 例外②放行");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, strength: 1))), "既非灵体、又非梦境 ⇒ 违规");
            Assert.IsFalse(r.IsSatisfiedBy(Deck(Probe(CardColor.Red, category: CardCategory.Sorcery, inherentKeyword: "灵体"))),
                "非角色类别 ⇒ 例外①要求『灵体**角色**』，不放行");
        }

        // ---- 013/015：非X非中立 ≤ 12（ExceptionMaxCount = 12） ----

        public void TestWhitelist_额度12_边界()
        {
            var r = Whitelist(CardColor.Red, CardColor.Brown);
            r.ExceptionMaxCount = 12;

            var ok = new List<PlayerCardData>();
            for (int i = 0; i < 12; i++) ok.Add(Probe(CardColor.Green, uid: "TS_cr_wl12_" + i));
            Assert.AreEqual(12, r.GetActualCount(ok), "12 张违规 ⇒ 违规数 12");
            Assert.IsTrue(r.IsSatisfiedBy(ok), "**恰好 12 张 ⇒ 通过（边界包含）**");

            ok.Add(Probe(CardColor.Green, uid: "TS_cr_wl13"));
            Assert.AreEqual(13, r.GetActualCount(ok), "13 张违规 ⇒ 违规数 13");
            Assert.IsFalse(r.IsSatisfiedBy(ok), "**13 张 ⇒ 不通过**");
        }

        public void TestWhitelist_额度12_白名单与中立不计数()
        {
            var r = Whitelist(CardColor.Red, CardColor.Brown);
            r.ExceptionMaxCount = 12;
            var deck = new List<PlayerCardData>();
            for (int i = 0; i < 20; i++) deck.Add(Probe(CardColor.Red, uid: "TS_cr_r" + i));
            for (int i = 0; i < 20; i++) deck.Add(Probe(CardColor.Brown, uid: "TS_cr_n" + i));
            Assert.AreEqual(0, r.GetActualCount(deck), "红＋中立都不计入违规");
            Assert.IsTrue(r.IsSatisfiedBy(deck), "全是红/中立 ⇒ 满足");
        }

        /// <summary>013/015 与 010 的区别：额度>0 时不写 ExceptionRules，违规张数按额度收口。</summary>
        public void TestWhitelist_013式与010式语义差异()
        {
            var cap12 = Whitelist(CardColor.Red, CardColor.Brown);
            cap12.ExceptionMaxCount = 12;
            var zero = Whitelist(CardColor.Yellow, CardColor.Brown);
            zero.ExceptionMaxCount = 0;

            var deck = Deck(Probe(CardColor.Green));
            Assert.IsTrue(cap12.IsSatisfiedBy(deck), "013/015：1 张违规 ≤ 12 ⇒ 通过");
            Assert.IsFalse(zero.IsSatisfiedBy(deck), "010/011：1 张违规 > 0 ⇒ 不通过");
        }

        // ---- Describe 文案 ----

        public void TestWhitelist_Describe_只能包含句式()
        {
            var r = Whitelist(CardColor.Yellow, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule
            { FilterByDomain = true, TargetDomains = new Godot.Collections.Array<MagicDomain> { MagicDomain.Star } });
            string s = r.Describe(2);
            Assert.IsTrue(s.Contains("只能包含"), $"应含『只能包含』，实际：{s}");
            Assert.IsTrue(s.Contains("黄色") && s.Contains("中立"), $"白名单应显示 黄色/中立，实际：{s}");
            Assert.IsTrue(s.Contains("星空"), $"例外应显示领域名，实际：{s}");
            Assert.IsTrue(s.Contains("2"), $"应含当前违规张数，实际：{s}");
        }

        public void TestWhitelist_Describe_额度句式()
        {
            var r = Whitelist(CardColor.Red, CardColor.Brown);
            r.ExceptionMaxCount = 12;
            string s = r.Describe(7);
            Assert.IsTrue(s.Contains("不能超过 12 张"), $"应含额度文案，实际：{s}");
            Assert.IsTrue(s.Contains("非") && s.Contains("红色") && s.Contains("中立"), $"应含『非 红色 且非 中立』，实际：{s}");
            Assert.IsTrue(s.Contains("7"), $"应含当前张数，实际：{s}");
        }

        // ---- 与 ValidateDeckDetailed / 线上资源的联动 ----

        public void TestWhitelist_ValidateDeckDetailed返回明细()
        {
            var council = new CouncilCardData { UID = "TS_cr_wl", CardName = "白名单秘社" };
            var r = Whitelist(CardColor.Yellow, CardColor.Brown);
            r.ExceptionRules.Add(new CouncilExceptionRule
            { FilterByDomain = true, TargetDomains = new Godot.Collections.Array<MagicDomain> { MagicDomain.Star } });
            council.Restrictions.Add(r);

            var bad = Deck(Probe(CardColor.Yellow), Probe(CardColor.Green), Probe(CardColor.Blue));
            var failures = council.ValidateDeckDetailed(bad);
            Assert.AreEqual(1, failures.Count, "1 条限制未满足 ⇒ 1 条明细");
            Assert.IsTrue(failures[0].Contains("2"), $"明细应附带违规张数 2，实际：{failures[0]}");
            Assert.IsFalse(council.ValidateDeck(bad), "ValidateDeck 应返回 false");

            var good = Deck(Probe(CardColor.Yellow),
                Probe(CardColor.Green, domains: new[] { MagicDomain.Star }));
            Assert.AreEqual(0, council.ValidateDeckDetailed(good).Count, "例外牌使卡组合规 ⇒ 无明细");
        }

        // ==================== ⑥ 线上资源：010~017 的构筑限制结构 ====================

        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";

        private static CouncilCardData LoadCouncil(string file)
            => ResourceLoader.Load<CouncilCardData>(CouncilDir + file + ".tres");

        private static CouncilExceptionRule OnlyException(CouncilRestriction r)
        {
            Assert.IsNotNull(r, "限制应存在");
            Assert.AreEqual(1, r.ExceptionRules.Count, "应恰好 1 条例外");
            return r.ExceptionRules[0];
        }

        public void TestMsjc010_限制_白名单黄中立加星领域例外()
        {
            var c = LoadCouncil("MSJC010_mozu");
            Assert.IsNotNull(c, "应能加载 MSJC010");
            Assert.AreEqual(1, c.Restrictions.Count, "1 条限制");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.UseWhitelist, "应为白名单模式");
            Assert.AreEqual(0, r.ExceptionMaxCount, "010 一个违规都不许");
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Yellow) && r.AllowedColors.Contains(CardColor.Brown),
                "白名单＝黄＋中立（棕）");
            var e = OnlyException(r);
            Assert.IsTrue(e.FilterByDomain, "例外按领域筛选");
            Assert.IsTrue(e.TargetDomains.Contains(MagicDomain.Star), "例外＝【星】领域");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Yellow))), "黄牌合法");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Green, domains: new[] { MagicDomain.Star }))), "非黄但【星】领域合法");
            Assert.IsFalse(c.ValidateDeck(Deck(Probe(CardColor.Green, domains: new[] { MagicDomain.Mind }))), "非黄非星 ⇒ 违规");
        }

        public void TestMsjc011_限制_白名单绿中立加黑刀人类角色例外()
        {
            var c = LoadCouncil("MSJC011_SPTzhixingbu");
            Assert.IsNotNull(c, "应能加载 MSJC011");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.UseWhitelist, "白名单模式");
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Green) && r.AllowedColors.Contains(CardColor.Brown), "绿＋中立");
            var e = OnlyException(r);
            Assert.IsTrue(e.FilterByCategory && e.TargetCategoryFlag == CardCategory.Character, "例外要求角色类别");
            Assert.IsTrue(e.FilterBySubCategories && e.TargetSubCategories.Contains("人类"), "例外要求人类");
            Assert.IsTrue(e.FilterByAbilityIcon && e.TargetAbilityIcon == "黑刀", "例外要求［黑刀］图标");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }, strength: 1))),
                "红派系＋人类＋战力>0 ⇒ 合法");
            Assert.IsFalse(c.ValidateDeck(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }, strength: 0))),
                "人类但无战力（无［黑刀］）⇒ 违规");
        }

        public void TestMsjc012_限制_白名单蓝中立加吸血鬼奴仆例外()
        {
            var c = LoadCouncil("MSJC012_aifuluodejiazu");
            Assert.IsNotNull(c, "应能加载 MSJC012");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Blue) && r.AllowedColors.Contains(CardColor.Brown), "蓝＋中立");
            var e = OnlyException(r);
            Assert.IsTrue(e.FilterBySubCategories, "例外按子类别");
            Assert.IsTrue(e.TargetSubCategories.Contains("吸血鬼") && e.TargetSubCategories.Contains("奴仆"), "吸血鬼 OR 奴仆");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Red, subCategories: new[] { "吸血鬼" }))), "吸血鬼合法");
            Assert.IsFalse(c.ValidateDeck(Deck(Probe(CardColor.Red, subCategories: new[] { "人类" }))), "人类违规");
        }

        public void TestMsjc013_限制_非红非中立不超过12()
        {
            var c = LoadCouncil("MSJC013_jishizhe");
            Assert.IsNotNull(c, "应能加载 MSJC013");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.UseWhitelist, "白名单模式");
            Assert.AreEqual(12, r.ExceptionMaxCount, "上限 12");
            Assert.AreEqual(0, r.ExceptionRules.Count, "013 没有例外条目");
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Red) && r.AllowedColors.Contains(CardColor.Brown), "红＋中立");

            var deck = new List<PlayerCardData>();
            for (int i = 0; i < 12; i++) deck.Add(Probe(CardColor.Green, uid: "TS_msjc13_" + i));
            Assert.IsTrue(c.ValidateDeck(deck), "12 张违规 ⇒ 通过（边界）");
            deck.Add(Probe(CardColor.Green, uid: "TS_msjc13_12"));
            Assert.IsFalse(c.ValidateDeck(deck), "13 张违规 ⇒ 不通过");
        }

        public void TestMsjc014_限制_白名单灰中立加无领域例外()
        {
            var c = LoadCouncil("MSJC014_shaobing");
            Assert.IsNotNull(c, "应能加载 MSJC014");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Gray) && r.AllowedColors.Contains(CardColor.Brown), "灰＋中立");
            var e = OnlyException(r);
            Assert.IsTrue(e.RequireNoDomain, "例外＝不具有魔法领域图标");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Red))), "非灰但无领域 ⇒ 合法");
            Assert.IsFalse(c.ValidateDeck(Deck(Probe(CardColor.Red, domains: new[] { MagicDomain.Mind }))), "非灰且有领域 ⇒ 违规");
        }

        public void TestMsjc015_限制_非白非中立不超过12()
        {
            var c = LoadCouncil("MSJC015_yinshi");
            Assert.IsNotNull(c, "应能加载 MSJC015");
            var r = c.Restrictions[0];
            Assert.AreEqual(12, r.ExceptionMaxCount, "上限 12");
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.White) && r.AllowedColors.Contains(CardColor.Brown), "白＋中立");
        }

        public void TestMsjc016_限制_白名单黑中立加死领域例外()
        {
            var c = LoadCouncil("MSJC016_songwangzhe");
            Assert.IsNotNull(c, "应能加载 MSJC016");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Black) && r.AllowedColors.Contains(CardColor.Brown), "黑＋中立");
            var e = OnlyException(r);
            Assert.IsTrue(e.FilterByDomain && e.TargetDomains.Contains(MagicDomain.Death), "例外＝【死】领域");
        }

        public void TestMsjc017_限制_白名单紫中立加灵体角色或梦境例外()
        {
            var c = LoadCouncil("MSJC017_mengyanzuqun");
            Assert.IsNotNull(c, "应能加载 MSJC017");
            var r = c.Restrictions[0];
            Assert.IsTrue(r.AllowedColors.Contains(CardColor.Purple) && r.AllowedColors.Contains(CardColor.Brown), "紫＋中立");
            Assert.AreEqual(2, r.ExceptionRules.Count, "两条例外（灵体角色 / 梦境牌）");
            Assert.IsTrue(r.ExceptionRules[0].FilterByKeyword && r.ExceptionRules[0].TargetKeyword == "灵体", "例外①＝关键字灵体");
            Assert.IsTrue(r.ExceptionRules[1].FilterBySubCategories && r.ExceptionRules[1].TargetSubCategories.Contains("梦境"),
                "例外②＝子类别梦境");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Red, inherentKeyword: "灵体"))), "灵体角色合法（例外①）");
            Assert.IsTrue(c.ValidateDeck(Deck(Probe(CardColor.Red, subCategories: new[] { "梦境" }))), "梦境牌合法（例外②）");
            Assert.IsFalse(c.ValidateDeck(Deck(Probe(CardColor.Red, strength: 1))), "既非灵体又非梦境 ⇒ 违规");
        }

        /// <summary>010~017 的 `Describe` 都应是可读文案（非空、含实际张数），供大厅"卡组违规"提示使用。</summary>
        public void TestMsjc010_017_Describe均可读()
        {
            foreach (var f in new[]
            {
                "MSJC010_mozu", "MSJC011_SPTzhixingbu", "MSJC012_aifuluodejiazu", "MSJC013_jishizhe",
                "MSJC014_shaobing", "MSJC015_yinshi", "MSJC016_songwangzhe", "MSJC017_mengyanzuqun",
            })
            {
                var c = LoadCouncil(f);
                Assert.IsNotNull(c, $"{f} 应能加载");
                Assert.IsTrue(c.HasRestrictions, $"{f} 应声明构筑限制");
                string s = c.Restrictions[0].Describe(3);
                Assert.IsTrue(s.Length > 5, $"{f} 的限制文案过短：{s}");
                Assert.IsTrue(s.Contains("3"), $"{f} 的文案应含实际张数：{s}");
            }
        }

        // ==================== 辅助 ====================

        private static PlayerCardData Card(CardColor color)
        {
            var c = new PlayerCardData { UID = "TS_cr_card", CardName = "探针卡", Defense = 1 };
            c.Colors.Add(color);
            return c;
        }

        /// <summary>白名单模式测试用的探针卡（可指定颜色/类别/子类别/领域/战力/关键字）。</summary>
        private static PlayerCardData Probe(CardColor color,
            CardCategory category = CardCategory.Character,
            string[] subCategories = null,
            MagicDomain[] domains = null,
            int strength = 0,
            int firstStrikeStrength = 0,
            string inherentKeyword = null,
            string uid = "TS_cr_probe")
        {
            var c = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Defense = 1,
                CategoryFlag = category, Strength = strength, FirstStrikeStrength = firstStrikeStrength,
            };
            if (color != CardColor.Brown) c.Colors.Clear();   // 默认是棕色（中立），需要别的颜色时先清
            c.Colors.Add(color);
            foreach (var s in subCategories ?? System.Array.Empty<string>()) c.SubCategories.Add(s);
            // 无领域卡在 .tres 里就是 [-1] 哨兵值（实测），这里显式模拟该形态。
            if (domains == null || domains.Length == 0) c.MagicDomains.Add((MagicDomain)(-1));
            else foreach (var d in domains) c.MagicDomains.Add(d);
            // 关键字走"模板固有声明"通道（EffectDataList 的 Continuous 授予 "Keyword"）。
            if (inherentKeyword != null)
            {
                var e = new Script.Data.Effects.EffectData { Timing = Script.Data.Effects.EffectTiming.Continuous };
                e.ContinuousGrants.Add(new Script.Data.Effects.EffectGrant
                { Kind = "Keyword", Value = inherentKeyword, Amount = 1 });
                c.EffectDataList.Add(e);
            }
            c._effectsInjected = true;
            return c;
        }

        private static List<PlayerCardData> Deck(params PlayerCardData[] cards)
            => new List<PlayerCardData>(cards);

        private static CouncilRestriction Whitelist(params CardColor[] colors)
        {
            var r = new CouncilRestriction { UseWhitelist = true, ExceptionMaxCount = 0 };
            foreach (var c in colors) r.AllowedColors.Add(c);
            return r;
        }
    }
}
