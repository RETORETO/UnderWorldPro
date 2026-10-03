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
    /// 【2026-09·PM 调试命令新增/修复】
    ///  ① `draw &lt;数量&gt; &lt;卡牌编号&gt; [玩家]` —— **定向取牌**：从该玩家（缺省 P1）**卡组**取指定编号的牌
    ///     加入其手牌。编号＝UID 第一个下划线之前的部分、不区分大小写（如 `JC001`）；没有 → 什么都不做；
    ///     有多张 → 随机一张；数量＝最多取几张。**旧的 `draw &lt;玩家&gt; [数量]` 语法保持兼容**。
    ///  ② `jumpto &lt;步骤名&gt;[地区号]` —— **直接跳转步骤**，中间经过的步骤**一律不结算**（其触发/效果都不结算），
    ///     目标步骤执行正常的进入逻辑（照常可结算、后续流程照常推进）；对抗类步骤带地区号：
    ///     `jumpto CombatInvestigation1` ＝跳到 1 地区调查对抗。
    ///  另：本批同时修复"重置步骤不竖置秘社卡"（`CardManager.UntapAll` 补 `CouncilZone`，见
    ///  `P3NoUntapSealedTests.TestNoUntap_重置步骤竖置秘社`）。
    /// </summary>
    public class PMCommandTests : TestRunner
    {
        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 20260922 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 3 : -1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>把一张探针卡放进指定玩家的卡组（索引 0＝牌库顶）。</summary>
        private static CardInstance PutInDeck(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            (ownerId == 1 ? game.DeckManager.GetDeckPlayer1() : game.DeckManager.GetDeckPlayer2()).Insert(0, card);
            return card;
        }

        private static bool InDeck(GameInitializer game, CardInstance card, int ownerId = 1)
            => (ownerId == 1 ? game.DeckManager.GetDeckPlayer1() : game.DeckManager.GetDeckPlayer2()).Contains(card);

        private void Run(GameInitializer game, string cmd) => game.PMCommandManager.ExecuteCommand(cmd);

        // ==================== ① draw <数量> <卡牌编号> ====================
        // 注：默认卡组里**本来就含真实的 JC 卡**（如 JC001 通灵猫头鹰），因此探针用例统一用
        // 卡组里不可能出现的编号（ZZ9xx），把"命中/多张/随机/数量"这些自变量隔离出来。

        public void TestPMCommand_draw定向取牌_命中一张()
        {
            var game = NewGame();
            var card = PutInDeck(game, Probe("ZZ998_探针A"));
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            Run(game, "draw 1 ZZ998");

            Assert.IsTrue(game.TurnManager.GetHandOf(1).Contains(card), "ZZ998 应已从卡组加入 P1 手牌");
            Assert.IsFalse(InDeck(game, card), "该卡应已离开卡组");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "P1 手牌 +1");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "实例所在区＝手牌");
        }

        /// <summary>真实卡组路径：`draw 1 JC001` 应取到一张编号为 JC001 的牌（具体是哪张不固定）。</summary>
        public void TestPMCommand_draw定向取牌_真实卡组命中编号()
        {
            var game = NewGame();
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            var before = new List<CardInstance>(game.TurnManager.GetHandOf(1));

            Run(game, "draw 1 JC001");

            var after = game.TurnManager.GetHandOf(1);
            Assert.AreEqual(handBefore + 1, after.Count, "P1 手牌 +1（卡组里确有 JC001）");
            CardInstance added = null;
            foreach (var c in after) if (!before.Contains(c)) added = c;
            Assert.IsNotNull(added, "应能识别出本次加入手牌的那张");
            string code = added.PlayerTemplate?.UID ?? "";
            int cut = code.IndexOf('_');
            Assert.AreEqual("JC001", cut > 0 ? code.Substring(0, cut) : code,
                "加入的必须是编号 JC001 的牌（下划线前的部分＝JC001）");
        }

        public void TestPMCommand_draw定向取牌_编号不区分大小写()
        {
            var game = NewGame();
            var card = PutInDeck(game, Probe("ZZ997_探针B"));

            Run(game, "draw 1 zz997");   // 小写输入

            Assert.IsTrue(game.TurnManager.GetHandOf(1).Contains(card), "编号比较不区分大小写（zz997 → ZZ997）");
        }

        public void TestPMCommand_draw定向取牌_没有则什么都不做()
        {
            var game = NewGame();
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            Run(game, "draw 1 ZZ999");

            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "卡组里没有该编号 → 手牌不变");
        }

        public void TestPMCommand_draw定向取牌_多张随机取一张()
        {
            var game = NewGame();
            var a = PutInDeck(game, Probe("ZZ996_探针A"));
            var b = PutInDeck(game, Probe("ZZ996_探针B"));
            var c = PutInDeck(game, Probe("ZZ996_探针C"));
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            Run(game, "draw 1 ZZ996");

            int inHand = 0, inDeck = 0;
            foreach (var card in new[] { a, b, c })
            {
                if (game.TurnManager.GetHandOf(1).Contains(card)) inHand++;
                if (InDeck(game, card)) inDeck++;
            }
            Assert.AreEqual(1, inHand, "多张同编号 → 只取 1 张");
            Assert.AreEqual(2, inDeck, "其余仍留在卡组");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "P1 手牌 +1");
        }

        public void TestPMCommand_draw定向取牌_数量2取两张()
        {
            var game = NewGame();
            var a = PutInDeck(game, Probe("ZZ995_探针A"));
            var b = PutInDeck(game, Probe("ZZ995_探针B"));
            var c = PutInDeck(game, Probe("ZZ995_探针C"));

            Run(game, "draw 2 ZZ995");

            int inHand = 0;
            foreach (var card in new[] { a, b, c }) if (game.TurnManager.GetHandOf(1).Contains(card)) inHand++;
            Assert.AreEqual(2, inHand, "数量 2 → 取 2 张（第 3 张留在卡组）");
        }

        public void TestPMCommand_draw定向取牌_简写与P2()
        {
            var game = NewGame();
            var p1Card = PutInDeck(game, Probe("ZZ994_探针P1"), 1);
            var p2Card = PutInDeck(game, Probe("ZZ994_探针P2"), 2);

            Run(game, "draw ZZ994");          // 简写：省略数量＝1，缺省 P1
            Assert.IsTrue(game.TurnManager.GetHandOf(1).Contains(p1Card), "简写取 1 张（缺省 P1）");
            Assert.IsFalse(game.TurnManager.GetHandOf(2).Contains(p2Card), "未指定玩家时不取 P2 的牌");

            Run(game, "draw 1 ZZ994 2");      // 显式指定玩家 2
            Assert.IsTrue(game.TurnManager.GetHandOf(2).Contains(p2Card), "第 4 个参数＝玩家号（P2）");
        }

        /// <summary>旧语法 `draw &lt;玩家&gt; [数量]` 保持兼容（走 UI 注入的抽牌回调）。</summary>
        public void TestPMCommand_draw旧语法兼容()
        {
            var game = NewGame();
            int calledPlayer = -1, calledCount = -1;
            game.PMCommandManager.Initialize(
                game.CardManager, game.DeckManager,
                () => game.AreaManager.GetOrderedAreaUids(),
                (pid, count) => { calledPlayer = pid; calledCount = count; },
                game.ZoneManager,
                areaManager: game.AreaManager,
                stackManager: game.StackManager);

            Run(game, "draw 1 2");   // 两个参数都是数字 → 旧语义：玩家1 抽 2 张
            Assert.AreEqual(1, calledPlayer, "旧语法：第一个参数＝玩家号");
            Assert.AreEqual(2, calledCount, "旧语法：第二个参数＝抽牌数量");
        }

        // ==================== ② jumpto <步骤名>[地区号] ====================

        public void TestPMCommand_jumpto_跳到行动步骤()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.EnterPhase(global::TurnManager.TurnPhase.StartPrepare);   // 先到准备步骤

            Run(game, "jumpto ActionFirst");

            Assert.AreEqual(global::TurnManager.TurnPhase.ActionFirst, tm.CurrentPhase, "应跳到先手行动步骤");
            Assert.AreEqual(tm.GetPriorityPlayer(), tm.PriorityPlayerId, "目标步骤照常分配行动权（天生优先玩家）");
        }

        public void TestPMCommand_jumpto_跳到指定地区对抗()
        {
            var game = NewGame(2);   // 2 个地区
            var tm = game.TurnManager;
            Assert.AreEqual(2, game.AreaManager.GetOrderedAreaUids().Count, "前置：2 个地区");

            Run(game, "jumpto CombatInvestigation1");
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatInvestigation, tm.CurrentPhase, "应跳到调查对抗步骤");
            Assert.AreEqual(1, tm.CurrentCombatAreaNo, "地区游标＝1 地区");

            Run(game, "jumpto CombatStrength2");
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatStrength, tm.CurrentPhase, "应跳到战斗对抗步骤");
            Assert.AreEqual(2, tm.CurrentCombatAreaNo, "地区游标＝2 地区（CombatStrength2）");

            Run(game, "jumpto CombatPower1");
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, tm.CurrentPhase, "应跳到势力对抗步骤");
            Assert.AreEqual(1, tm.CurrentCombatAreaNo, "地区游标＝1 地区");
        }

        /// <summary>中间经过的步骤**不结算**：`PreCombat` 会收集"机动触发"，直接跳到调查对抗则不应有任何条目入堆叠。</summary>
        public void TestPMCommand_jumpto_中间步骤不结算()
        {
            var game = NewGame(2);
            var tm = game.TurnManager;
            string area = game.AreaManager.GetOrderedAreaUids()[0];

            // 场上放一张"机动触发"的卡（若结算 PreCombat 就会被收集入堆叠）
            var tmpl = Probe("TS_pm_maneuver");
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_pm_maneuver_e",
                Timing = EffectTiming.ManeuverTrigger,
                Target = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                Actions = new Array<EffectAction>(),
            });
            tmpl._effectsInjected = true;
            var unit = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(unit, area, 1);

            tm.FirstPlayerId = 1;
            tm.EnterPhase(global::TurnManager.TurnPhase.ActionSecond);
            Assert.IsTrue(game.StackManager.IsEmpty, "前置：堆叠为空");

            Run(game, "jumpto CombatInvestigation1");

            Assert.IsTrue(game.StackManager.IsEmpty,
                "跳转不结算中间步骤 ⇒ PreCombat 的机动触发没有被收集（堆叠仍为空）");
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatInvestigation, tm.CurrentPhase, "已到目标步骤");
        }

        /// <summary>`jumpto EndRecover`：目标步骤**照常结算**（手牌上限弃牌），且后续游戏正常推进。</summary>
        public void TestPMCommand_jumpto_EndRecover正常结算并继续()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.EnterPhase(global::TurnManager.TurnPhase.ActionFirst);

            // 手牌超上限 2 张（重置步骤之外的正常结算路径见 P3AtomicDecisionLockTests）
            while (tm.GetHandOf(1).Count <= tm.HandLimit + 1)
            {
                var c = game.CardManager.CreateCardInstance(Probe($"TS_pm_hand{tm.GetHandOf(1).Count}"), 1);
                game.ZoneManager.MoveToHand(c, 1);
            }
            int turnBefore = tm.TurnCount;

            Run(game, "jumpto EndRecover");

            Assert.IsTrue(tm.GetHandOf(1).Count <= tm.HandLimit,
                "恢复步骤照常结算手牌上限（多余手牌已弃掉）");
            Assert.IsTrue(tm.TurnCount > turnBefore || tm.CurrentPhase != global::TurnManager.TurnPhase.EndRecover,
                "恢复步骤结算完毕 → 后续流程照常推进（换回合/进入下一阶段）");
        }

        public void TestPMCommand_jumpto_非法步骤名不改变状态()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.EnterPhase(global::TurnManager.TurnPhase.ActionFirst);
            var before = tm.CurrentPhase;

            Run(game, "jumpto NoSuchStep");
            Assert.AreEqual(before, tm.CurrentPhase, "非法步骤名 → 状态不变");

            Run(game, "jumpto CombatInvestigation9");   // 地区号越界
            Assert.AreEqual(before, tm.CurrentPhase, "地区号越界 → 状态不变");
        }
    }
}
