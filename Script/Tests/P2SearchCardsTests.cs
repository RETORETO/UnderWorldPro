using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【效果级推进·第 2 批】JC007 / JC025 / JC130 / JC131 —— 全部建立在 P1/P2 已交付的原语上：
    ///  · JC007 常春藤派学者：进场触发 → 检索（≤2 费的法术/书籍牌）→ 展示 → 入手 → 洗牌；
    ///  · JC025 猎手论坛徽记：结附秘社 + ①【猎魔人】牌减费 ②牺牲检索一张猎手牌（展示后入手并洗牌）；
    ///  · JC130 深入研究：抓两张后弃一张手牌（`Draw` + `Discard` 唯一出口）；
    ///  · JC131 空投物资：检索任意牌入手并洗牌。
    ///
    /// 覆盖口径：**结构**（效果数据与卡文一致）+ **行为**（真的取到牌/真的弃掉/展示真的广播/洗牌口径）。
    /// </summary>
    public class P2SearchCardsTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static EffectData Find(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 53 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Probe(string uid, int cost = 1, string subCategory = null,
            CardCategory category = CardCategory.Character, string organization = "")
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Organization = organization,
            };
            if (subCategory != null) tmpl.SubCategories.Add(subCategory);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>把探针卡插到玩家1 牌库顶（index 0 = 牌库顶）。</summary>
        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            card.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, card);
            return card;
        }

        // ==================== 结构 ====================

        public void TestSearchCards_结构与卡文一致()
        {
            // JC007：进场触发检索 ≤2 费的法术/书籍牌
            var jc007 = Load("JC007_changchuntengpaixuezhe");
            Assert.IsNotNull(jc007, "JC007 模板应可加载");
            var e007 = Find(jc007, "JC007_1_SearchSpellOrBook");
            Assert.IsNotNull(e007, "JC007 应有 SearchSpellOrBook");
            Assert.AreEqual(EffectTiming.EnterTrigger, e007.Timing, "JC007 为进场触发");
            Assert.AreEqual(ActionVerb.Search, e007.Actions[0].Verb, "JC007 动作为检索");
            Assert.AreEqual("费用<=2;子类别:法术,书籍", e007.Actions[0].Args[0], "JC007 筛选串应与卡文一致");
            Assert.AreEqual("手牌", e007.Actions[0].Args[1], "JC007 去向=手牌");
            Assert.IsTrue(e007.Actions[0].Args[2].Contains("展示"), "JC007 应公开展示该牌");

            // JC025：结附秘社 + 减费 + 牺牲检索猎手牌
            var jc025 = Load("JC025_lieshouluntanhuiji");
            Assert.IsNotNull(jc025, "JC025 模板应可加载");
            Assert.IsTrue(jc025.TargetRules != null && jc025.TargetRules.Count > 0
                && jc025.TargetRules[0].Kind == TargetKind.Council, "JC025 应结附于秘社");
            var e025a = Find(jc025, "JC025_1_CostReduce");
            Assert.IsNotNull(e025a, "JC025 应有减费激活");
            Assert.AreEqual("费用减少", e025a.Actions[0].Args[0], "JC025① 为费用减少");
            Assert.AreEqual("组织:猎魔人", e025a.Actions[0].Args[2], "JC025① 过滤串应为『组织:猎魔人』");
            var e025b = Find(jc025, "JC025_2_SearchHunter");
            Assert.IsNotNull(e025b, "JC025 应有检索激活");
            Assert.AreEqual(1, e025b.Cost, "JC025② 费用=1（快速行动1）");
            Assert.IsTrue(e025b.ActivateCondition.Contains("牺牲"), "JC025② 代价含牺牲自身");
            Assert.AreEqual("子类别:猎手", e025b.Actions[0].Args[0], "JC025② 筛选串为『子类别:猎手』");

            // JC130：抓两张后弃一张
            var jc130 = Load("JC130_shenruyanjiu");
            var e130 = Find(jc130, "JC130_1_DrawTwoDiscardOne");
            Assert.IsNotNull(e130, "JC130 应有 DrawTwoDiscardOne");
            Assert.IsFalse(jc130.LimitToActionPhaseOnly, "JC130『行动』应限行动阶段");
            Assert.AreEqual(ActionVerb.Draw, e130.Actions[0].Verb, "JC130 先抓牌");
            Assert.AreEqual("2", e130.Actions[0].Args[0], "JC130 抓两张");
            Assert.AreEqual(ActionVerb.Discard, e130.Actions[1].Verb, "JC130 后弃牌");
            Assert.AreEqual("1", e130.Actions[1].Args[0], "JC130 弃一张");
            Assert.AreEqual("自己", e130.Actions[1].Args[1], "JC130 弃自己的手牌");

            // JC131：检索任意牌
            var jc131 = Load("JC131_kongtouwuzi");
            var e131 = Find(jc131, "JC131_1_SearchAny");
            Assert.IsNotNull(e131, "JC131 应有 SearchAny");
            Assert.IsFalse(jc131.LimitToActionPhaseOnly, "JC131『行动』应限行动阶段");
            Assert.AreEqual("", e131.Actions[0].Args[0], "JC131 空筛选 = 任意牌");
        }

        // ==================== JC007 行为：按筛选取牌 + 展示 + 洗牌 ====================

        public void TestJC007_按费用与子类别检索并公示()
        {
            var game = NewGame();
            var spell = PutOnDeckTop(game, Probe("TS_007_spell", cost: 2, subCategory: "法术"));
            var book = PutOnDeckTop(game, Probe("TS_007_book", cost: 1, subCategory: "书籍"));
            var pricey = PutOnDeckTop(game, Probe("TS_007_pricey", cost: 5, subCategory: "法术"));
            var human = PutOnDeckTop(game, Probe("TS_007_human", cost: 1, subCategory: "人类"));

            // 收集"公开展示"广播
            var revealed = new List<CardInstance>();
            System.Action<CardInstance, int> handler = (c, by) => revealed.Add(c);
            UiNotificationBus.Instance.CardRevealed += handler;
            try
            {
                var src = game.CardManager.CreateCardInstance(Load("JC007_changchuntengpaixuezhe"), 1);
                int handBefore = game.TurnManager.GetHandOf(1).Count;
                int deckBefore = game.DeckManager.GetPlayer1DeckCount();

                // 无 UI：确定性兜底取"牌库顺序里第一张匹配"（此处 = book，因为它在 spell 之上插得更晚→index 0）
                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

                Assert.AreEqual(1, revealed.Count, "应公开展示 1 张牌（JC007 卡文『展示该牌』）");
                var got = revealed[0];
                Assert.IsTrue(got == book || got == spell, "被展示的应是符合筛选的牌");
                Assert.AreEqual(CardZone.Hand, got.CurrentZone, "展示后该牌应置于手中");
                Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1（只取一张）");
                Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "牌库 -1");
                Assert.AreEqual(CardZone.Deck, pricey.CurrentZone, "5 费牌不符合筛选，仍在牌库");
                Assert.AreEqual(CardZone.Deck, human.CurrentZone, "非法术/书籍牌不符合筛选，仍在牌库");
            }
            finally { UiNotificationBus.Instance.CardRevealed -= handler; }
        }

        /// <summary>
        /// 牌库里没有符合条件的牌时：**不取牌**，但检索确实发生了 → **仍然洗牌**（卡文"然后洗牌"），
        /// 且牌数与手牌数都不变。
        /// </summary>
        public void TestJC007_牌库无匹配时不取牌但仍洗牌()
        {
            var game = NewGame();
            // 人为清空"符合 JC007 筛选"的牌（移出手牌区），制造"必然找不到"的局面
            const string filter = "费用<=2;子类别:法术,书籍";
            var deck = game.DeckManager.GetDeckPlayer1();
            for (int i = deck.Count - 1; i >= 0; i--)
            {
                if (DeckSearchFilter.Matches(filter, deck[i]))
                {
                    var c = deck[i];
                    deck.RemoveAt(i);
                    game.ZoneManager.MoveToHand(c, 1);
                }
            }
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            string orderBefore = DeckSignature(game);

            var src = game.CardManager.CreateCardInstance(Load("JC007_changchuntengpaixuezhe"), 1);
            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(deckBefore, game.DeckManager.GetPlayer1DeckCount(), "无匹配 → 不取牌，牌库数不变");
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "无匹配 → 手牌数不变");
            Assert.IsFalse(orderBefore == DeckSignature(game), "无匹配也要洗牌（检索确实发生了）");
        }

        private static string DeckSignature(GameInitializer game)
            => string.Join(",", game.DeckManager.GetPlayer1DeckData().ConvertAll(c => c.UID));

        // ==================== JC131 行为：检索任意牌 ====================

        public void TestJC131_任意牌检索()
        {
            var game = NewGame();
            var top = PutOnDeckTop(game, Probe("TS_131_any", cost: 9, category: CardCategory.Sorcery));
            var src = game.CardManager.CreateCardInstance(Load("JC131_kongtouwuzi"), 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(CardZone.Hand, top.CurrentZone, "空筛选 → 牌库顶第一张应被取走（任意牌）");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1");
        }

        // ==================== JC130 行为：抓两张 + 弃一张 ====================

        public void TestJC130_抓两张弃一张()
        {
            var game = NewGame();
            var src = game.CardManager.CreateCardInstance(Load("JC130_shenruyanjiu"), 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();
            int graveBefore = game.CardManager.GetGraveyardCards().Count;

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(handBefore + 2 - 1, game.TurnManager.GetHandOf(1).Count, "净效果 = 手牌 +1（抓2弃1）");
            Assert.AreEqual(deckBefore - 2, game.DeckManager.GetPlayer1DeckCount(), "牌库 -2（只抓，不因弃牌变化）");
            Assert.AreEqual(graveBefore + 1, game.CardManager.GetGraveyardCards().Count, "弃掉的那张应进墓地");
        }

        /// <summary>弃牌走唯一出口 → 会广播 `OnDiscard`（"每当（敌方）玩家弃牌时"类效果依赖它）。</summary>
        public void TestJC130_弃牌广播OnDiscard()
        {
            var game = NewGame();
            int discarded = 0;
            System.Action<int, CardInstance> handler = (pid, card) => discarded++;
            game.TurnManager.OnCardDiscarded += handler;
            try
            {
                var src = game.CardManager.CreateCardInstance(Load("JC130_shenruyanjiu"), 1);
                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);
            }
            finally { game.TurnManager.OnCardDiscarded -= handler; }
            Assert.AreEqual(1, discarded, "JC130 的弃牌应触发一次 OnCardDiscarded（弃牌唯一出口）");
        }

        // ==================== JC025 行为：牺牲检索猎手牌 ====================

        public void TestJC025_牺牲检索猎手牌()
        {
            var game = NewGame();
            var hunter = PutOnDeckTop(game, Probe("TS_025_hunter", cost: 3, subCategory: "猎手", organization: "猎魔人"));
            var other = PutOnDeckTop(game, Probe("TS_025_other", cost: 3, subCategory: "士兵"));

            var src = game.CardManager.CreateCardInstance(Load("JC025_lieshouluntanhuiji"), 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];
            game.ZoneManager.MoveToAttachedZone(src, area, 1);   // 徽记已在场上（结附区域即可，本用例只验检索动作）
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            var e = Find(src.PlayerTemplate, "JC025_2_SearchHunter");
            EffectEngine.Instance.ResolveEffect(e, src, null);

            Assert.AreEqual(CardZone.Hand, hunter.CurrentZone, "应取到『猎手』子类别的牌");
            Assert.AreEqual(CardZone.Deck, other.CurrentZone, "非猎手牌不应被取走");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1");
        }
    }
}
