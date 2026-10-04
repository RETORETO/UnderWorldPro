using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.IO;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【秘社归属·2026-10 修复】开局"给双方设置秘社"的归属与时序回归：
    ///  1. 每位玩家拿到**自己卡组**携带的秘社（此前双方共用 P1 卡组的秘社 ⇒ P2 拿到错误秘社）；
    ///  2. 起手数各按自己秘社的 `StartingHandSize`（此前 P2 沿用 P1 秘社的起手数）；
    ///  3. 无专属卡组的一方回退**默认秘社 MSJC009**，不镜像对手的秘社；
    ///  4. 重开一局不串场（不再残留上一局的卡组/秘社）；
    ///  5. 秘社开局效果（MSJC015 的【隐秘】标志）结算在**最终保留**的秘社实例上
    ///     （旧顺序先结算、再被发牌环节重建秘社清空 ⇒ 效果产物丢失）。
    /// </summary>
    public class CouncilAssignTests : TestRunner
    {
        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";
        private const string DefaultCouncilUid = "MSJC009_mishe";   // 默认兜底秘社

        // ==================== ① 双方各拿自己卡组的秘社 ====================

        public void TestCouncilAssign_双方各拿自己卡组的秘社()
        {
            var game = NewGame();
            game.P1DeckOverride = BuildDeck(councilUid: "TS_ca_c1", councilName: "P1秘社", handSize: 7);
            game.P2DeckOverride = BuildDeck(councilUid: "TS_ca_c2", councilName: "P2秘社", handSize: 9);

            game.StartMatch();

            var c1 = CouncilOf(game, 1);
            var c2 = CouncilOf(game, 2);

            Assert.AreEqual("TS_ca_c1", c1.CouncilTemplate.UID, "玩家1 应拿到自己卡组携带的秘社（TS_ca_c1）");
            Assert.AreEqual("TS_ca_c2", c2.CouncilTemplate.UID, "玩家2 应拿到**自己卡组**携带的秘社（TS_ca_c2），而非 P1 的秘社");
            Assert.AreEqual(1, game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1).Count, "玩家1 秘社区恰 1 张");
            Assert.AreEqual(1, game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 2).Count, "玩家2 秘社区恰 1 张");
        }

        /// <summary>起手数应各按自己秘社的 `StartingHandSize`（7 / 9），不再双双读 P1 秘社。</summary>
        public void TestCouncilAssign_起手数各按自己秘社()
        {
            var game = NewGame();
            game.P1DeckOverride = BuildDeck(councilUid: "TS_ca_h1", councilName: "P1秘社", handSize: 7);
            game.P2DeckOverride = BuildDeck(councilUid: "TS_ca_h2", councilName: "P2秘社", handSize: 9);

            game.StartMatch();

            Assert.AreEqual(7, game.CardManager.GetHandCardsByOwner(1).Count, "玩家1 起手数＝自己秘社的 7");
            Assert.AreEqual(9, game.CardManager.GetHandCardsByOwner(2).Count, "玩家2 起手数＝自己秘社的 9（不是 P1 的 7）");
        }

        // ==================== ② 无专属卡组方 → 默认秘社（不镜像对手） ====================

        /// <summary>P2 未指定卡组（走硬编码卡组）→ 用默认秘社，不得沿用 P1 卡组的秘社。</summary>
        public void TestCouncilAssign_P2无卡组时回退默认秘社而非镜像P1()
        {
            var game = NewGame();
            game.P1DeckOverride = BuildDeck(councilUid: "TS_ca_m1", councilName: "P1秘社", handSize: 7);
            game.P2DeckOverride = null;

            game.StartMatch();

            Assert.AreEqual("TS_ca_m1", CouncilOf(game, 1).CouncilTemplate.UID, "玩家1 用自己卡组的秘社");
            Assert.AreEqual(DefaultCouncilUid, CouncilOf(game, 2).CouncilTemplate.UID,
                "玩家2 无专属卡组 ⇒ 回退默认秘社 MSJC009（不镜像 P1 的秘社）");
        }

        /// <summary>双方均无专属卡组 → 双方都用默认秘社。</summary>
        public void TestCouncilAssign_双方均无卡组时用默认秘社()
        {
            var game = NewGame();   // 未设 P1/P2DeckOverride → 走硬编码卡组

            game.StartMatch();

            foreach (int p in new[] { 1, 2 })
            {
                Assert.AreEqual(DefaultCouncilUid, CouncilOf(game, p).CouncilTemplate.UID,
                    $"玩家{p} 无专属卡组 ⇒ 使用默认秘社 MSJC009");
            }
        }

        // ==================== ③ 重开一局不串场 ====================

        /// <summary>
        /// 【修复·残留】旧实现的秘社来源字段在"硬编码卡组"分支未被重置，重开一局会残留上一局的卡组秘社。
        /// </summary>
        public void TestCouncilAssign_重开后不再残留上一局的秘社()
        {
            var game = NewGame();
            game.P1DeckOverride = BuildDeck(councilUid: "TS_ca_r1", councilName: "旧局秘社", handSize: 7);
            game.StartMatch();
            Assert.AreEqual("TS_ca_r1", CouncilOf(game, 1).CouncilTemplate.UID, "前置：第一局 P1 用自己卡组的秘社");

            // 第二局：不再指定任何卡组（走硬编码）
            game.P1DeckOverride = null;
            game.P2DeckOverride = null;
            game.ResetMatch();

            foreach (int p in new[] { 1, 2 })
            {
                Assert.AreEqual(DefaultCouncilUid, CouncilOf(game, p).CouncilTemplate.UID,
                    $"重开后玩家{p} 应回退默认秘社，不得残留上一局卡组的秘社");
            }
        }

        // ==================== ④ 秘社开局效果落在最终实例上 ====================

        /// <summary>
        /// 【修复·时序】MSJC015「在每局游戏开始时，在本方秘社上放置一个【隐秘】标志」。
        /// 旧顺序是先结算开局效果、再于发牌环节重建秘社 ⇒ 标志打在被丢弃的旧实例上。
        /// </summary>
        public void TestCouncilAssign_开局效果打在最终秘社实例上()
        {
            var game = NewGame();
            game.P1DeckOverride = BuildDeckWithRealCouncil("MSJC015_yinshi");
            game.P2DeckOverride = BuildDeck(councilUid: "TS_ca_o2", councilName: "P2秘社", handSize: 6);

            game.StartMatch();   // 内部：发牌（重放秘社）→ 秘社开局效果

            var councils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1);
            Assert.AreEqual(1, councils.Count, "玩家1 秘社区恰 1 张（MSJC015）");
            var council = councils[0];
            Assert.AreEqual("MSJC015_yinshi", council.CouncilTemplate.UID, "P1 秘社＝自己卡组携带的 MSJC015");
            Assert.IsTrue(council.Counters.TryGetValue("隐秘", out int n) && n == 1,
                "开局效果应打在**最终保留**的秘社实例上（秘社上有 1 个【隐秘】标志）");
        }

        // ==================== 辅助 ====================

        private static CardInstance CouncilOf(GameInitializer game, int playerId)
        {
            var councils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
            Assert.AreEqual(1, councils.Count, $"前置：玩家{playerId} 秘社区恰 1 张秘社");
            Assert.IsNotNull(councils[0].CouncilTemplate, $"前置：玩家{playerId} 的秘社应带 CouncilTemplate");
            return councils[0];
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 20261004 };
            game.InitializeAll();
            return game;
        }

        private static DeckList BuildDeck(string councilUid, string councilName, int handSize)
        {
            var deck = new DeckList { DeckName = "秘社归属测试卡组_" + councilUid };
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = ProbeTemplatePath, Copies = 30 });
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = WriteProbeCouncil(councilUid, councilName, handSize), Copies = 1 });
            return deck;
        }

        private static DeckList BuildDeckWithRealCouncil(string councilFile)
        {
            var deck = new DeckList { DeckName = "真实秘社卡组_" + councilFile };
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = ProbeTemplatePath, Copies = 30 });
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = CouncilDir + councilFile + ".tres", Copies = 1 });
            return deck;
        }

        // ---- 临时真实资源（写到 user://，便于 ResourceLoader 加载）----

        private static string _probeTemplatePath;
        private static string ProbeTemplatePath => _probeTemplatePath ??= WriteProbeTemplate();

        private static string WriteProbeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "TS_ca_probe_tmpl", CardName = "秘社归属探针卡", Cost = 1, Defense = 1,
                CategoryFlag = CardCategory.Sorcery,
            };
            string path = "user://TS_ca_probe_tmpl.tres";
            ResourceSaver.Save(tmpl, path);
            return path;
        }

        private static string WriteProbeCouncil(string uid, string name, int handSize)
        {
            var council = new CouncilCardData
            {
                UID = uid, CardName = name, StartingHandSize = handSize,
            };
            string path = $"user://TS_ca_council_{uid}.tres";
            ResourceSaver.Save(council, path);
            return path;
        }
    }
}
