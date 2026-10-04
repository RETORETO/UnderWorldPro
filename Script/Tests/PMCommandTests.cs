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

        /// <summary>
        /// 【用户约定】"输入卡牌编号时不支持多张"：`draw 1 2 ZZ995`（玩家1、数量2、编号）必须报错，
        /// 一张都不取；需要按编号取牌时数量只能写 1。
        /// </summary>
        public void TestPMCommand_draw定向取牌_编号不支持多张()
        {
            var game = NewGame();
            var a = PutInDeck(game, Probe("ZZ995_探针A"));
            var b = PutInDeck(game, Probe("ZZ995_探针B"));
            var c = PutInDeck(game, Probe("ZZ995_探针C"));
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            bool ok = game.PMCommandManager.TryExecuteCommand("draw 1 2 ZZ995", out string error);

            Assert.IsFalse(ok, "编号取牌 + 数量 2 → 必须报错");
            Assert.IsTrue(error != null && error.Contains("不支持多张"), $"错误应说明不支持多张，实际: {error}");
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "一张都不取");
            int stillInDeck = 0;
            foreach (var card in new[] { a, b, c }) if (InDeck(game, card, 1)) stillInDeck++;
            Assert.AreEqual(3, stillInDeck, "3 张仍全部留在卡组");
        }

        /// <summary>
        /// 【2026-10 修订】编号取牌的**玩家位**写法：`draw 1 ZZ994`（玩家在前）与
        /// `draw 1 1 ZZ994`（显式数量 1）；旧的"省略玩家"不再可用（会被当成非法玩家参数并报错）。
        /// </summary>
        public void TestPMCommand_draw定向取牌_玩家位写法()
        {
            var game = NewGame();
            var p1Card = PutInDeck(game, Probe("ZZC01_探针P1"), 1);
            var p2Card = PutInDeck(game, Probe("ZZC01_探针P2"), 2);

            Run(game, "draw 1 ZZC01");        // 玩家1 取 1 张
            Assert.IsTrue(game.TurnManager.GetHandOf(1).Contains(p1Card), "玩家参数在第一位＝P1");
            Assert.IsFalse(game.TurnManager.GetHandOf(2).Contains(p2Card), "未指定 P2 时不取 P2 的牌");

            Run(game, "draw 2 ZZC01");        // 玩家2 取 1 张（用户报的 `DRAW 2 JC015` 形态）
            Assert.IsTrue(game.TurnManager.GetHandOf(2).Contains(p2Card), "玩家参数 2 ⇒ 从 P2 卡组取并进 P2 手牌");

            // 省略玩家的旧简写必须报错（不再静默按 P1）
            bool ok = game.PMCommandManager.TryExecuteCommand("draw ZZC01", out string error);
            Assert.IsFalse(ok, "缺玩家参数的 `draw <编号>` 必须报错");
            Assert.IsTrue(error != null && error.Contains("玩家"), $"错误应提示玩家参数，实际: {error}");
        }

        /// <summary>
        /// 【2026-10 修订】`draw 1 5` / `draw 2 3`：**玩家参数第一位**、第二参是数量 → 该玩家从牌库顶抽 N 张。
        /// 旧日志形态（`draw 1 5` / `draw 2 5`）逐字兼容。
        /// </summary>
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

            Run(game, "draw 1 2");   // 玩家1 抽 2 张
            Assert.AreEqual(1, calledPlayer, "第一个参数＝玩家号");
            Assert.AreEqual(2, calledCount, "第二个参数＝抽牌数量");

            Run(game, "draw 2 3");   // 玩家2 抽 3 张
            Assert.AreEqual(2, calledPlayer, "第一个参数＝玩家号（P2）");
            Assert.AreEqual(3, calledCount, "第二个参数＝抽牌数量");
        }

        // ==================== ①-b 【2026-10】玩家参数语义（用户实测缺陷） ====================

        /// <summary>把一个探针卡放进指定玩家手牌（用于"卡组里没有该编号"的分布提示断言）。</summary>
        private static CardInstance PutInHand(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            game.ZoneManager.MoveToHand(card, ownerId);
            return card;
        }

        /// <summary>
        /// 【用户实测缺陷回归】`draw 3 ZZ990`：第一位是**玩家参数**，只能是 1|2 → 必须报错，
        /// 且**双方手牌都不能变**（旧实现会把「3」当数量、静默按 P1 取牌/抽牌 —— 就是用户看到的现象）。
        /// </summary>
        public void TestPMCommand_draw玩家参数非法则不动作()
        {
            var game = NewGame();
            var p1 = PutInDeck(game, Probe("ZZ990_探针P1"), 1);
            var p2 = PutInDeck(game, Probe("ZZ990_探针P2"), 2);
            int hand1 = game.TurnManager.GetHandOf(1).Count;
            int hand2 = game.TurnManager.GetHandOf(2).Count;

            bool ok = game.PMCommandManager.TryExecuteCommand("draw 3 ZZ990", out string error);

            Assert.IsFalse(ok, "玩家参数 3 非法 → 命令必须失败");
            Assert.IsTrue(!string.IsNullOrEmpty(error), "必须给出可读错误（供联机回执）");
            Assert.AreEqual(hand1, game.TurnManager.GetHandOf(1).Count, "P1 手牌不能变（禁止静默按 P1）");
            Assert.AreEqual(hand2, game.TurnManager.GetHandOf(2).Count, "P2 手牌不能变");
            Assert.IsTrue(InDeck(game, p1, 1) && InDeck(game, p2, 2), "双方卡组都不动");
        }

        /// <summary>`draw 1 ZZ991` → 从 **P1** 卡组取该编号进 **P1** 手牌，P2 完全不受影响。</summary>
        public void TestPMCommand_draw定向取牌_进指定玩家手牌_P1()
        {
            var game = NewGame();
            var p1Card = PutInDeck(game, Probe("ZZ991_探针"), 1);
            var p2Card = PutInDeck(game, Probe("ZZ991_探针"), 2);

            Run(game, "draw 1 ZZ991");

            Assert.IsTrue(game.TurnManager.GetHandOf(1).Contains(p1Card), "P1 的 ZZ991 进 P1 手牌");
            Assert.IsFalse(game.TurnManager.GetHandOf(2).Contains(p2Card), "P2 的 ZZ991 一动不动");
        }

        /// <summary>
        /// 【用户报的原始命令形态】`draw 2 ZZ992` → 玩家参数＝2 ⇒ 从 **P2** 卡组取该编号进 **P2** 手牌。
        /// 旧实现把「2」当数量、缺省玩家 P1 ⇒ 卡进 P1 手牌（用户实测 `DRAW 2 JC015` 的根因）。
        /// </summary>
        public void TestPMCommand_draw定向取牌_进指定玩家手牌_P2()
        {
            var game = NewGame();
            var p1Card = PutInDeck(game, Probe("ZZ992_探针"), 1);
            var p2Card = PutInDeck(game, Probe("ZZ992_探针"), 2);

            Run(game, "draw 2 ZZ992");

            Assert.IsTrue(game.TurnManager.GetHandOf(2).Contains(p2Card), "P2 的 ZZ992 进 P2 手牌");
            Assert.IsFalse(game.TurnManager.GetHandOf(1).Contains(p1Card), "P1 的 ZZ992 不能被取走（核心回归点）");
            Assert.IsTrue(InDeck(game, p1Card, 1), "P1 卡组保持原样");
        }

        /// <summary>`draw 1 1 ZZ993`：显式数量＝1 → 允许；编号取牌一次只取 1 张。</summary>
        public void TestPMCommand_draw定向取牌_显式数量1()
        {
            var game = NewGame();
            var a = PutInDeck(game, Probe("ZZ993_探针A"), 2);
            var b = PutInDeck(game, Probe("ZZ993_探针B"), 2);

            Run(game, "draw 2 1 ZZ993");

            int inHand = 0;
            if (game.TurnManager.GetHandOf(2).Contains(a)) inHand++;
            if (game.TurnManager.GetHandOf(2).Contains(b)) inHand++;
            Assert.AreEqual(1, inHand, "编号取牌一次只取 1 张（不支持多张）");
        }

        /// <summary>`draw 1 2 ZZ994`：显式数量 ≠ 1 → 必须报错（用户约定：输入卡牌编号时不支持多张）。</summary>
        public void TestPMCommand_draw定向取牌_数量大于1报错()
        {
            var game = NewGame();
            PutInDeck(game, Probe("ZZ994_探针A"), 1);
            PutInDeck(game, Probe("ZZ994_探针B"), 1);
            int hand1 = game.TurnManager.GetHandOf(1).Count;

            bool ok = game.PMCommandManager.TryExecuteCommand("draw 1 2 ZZ994", out string error);

            Assert.IsFalse(ok, "编号取牌 + 数量 2 → 必须失败");
            Assert.IsTrue(error != null && error.Contains("不支持多张"), $"错误应说明不支持多张，实际: {error}");
            Assert.AreEqual(hand1, game.TurnManager.GetHandOf(1).Count, "一张都不取");
        }

        /// <summary>
        /// `draw 2 ZZ995` 而该编号**只在 P1 卡组**：按"玩家参数＝搜索域"口径，P2 卡组没有 → 报错，
        /// 并且错误信息要提示该编号在 P1 侧有几张（便于排查）。
        /// </summary>
        public void TestPMCommand_draw定向取牌_只搜参数玩家的卡组()
        {
            var game = NewGame();
            PutInDeck(game, Probe("ZZD01_探针"), 1);
            int hand1 = game.TurnManager.GetHandOf(1).Count;
            int hand2 = game.TurnManager.GetHandOf(2).Count;

            bool ok = game.PMCommandManager.TryExecuteCommand("draw 2 ZZD01", out string error);

            Assert.IsFalse(ok, "P2 卡组没有该编号 → 失败");
            Assert.IsTrue(error != null && error.Contains("P1"), $"错误应提示 P1 侧有该编号，实际: {error}");
            Assert.AreEqual(hand1, game.TurnManager.GetHandOf(1).Count, "P1 手牌不变");
            Assert.AreEqual(hand2, game.TurnManager.GetHandOf(2).Count, "P2 手牌不变");
        }

        /// <summary>编号只剩在**手牌**时，`draw` 必须失败并把分布写进错误（不再一句"什么都不做"）。</summary>
        public void TestPMCommand_draw定向取牌_仅在区域时错误含分布()
        {
            var game = NewGame();
            PutInHand(game, Probe("ZZE01_探针"), 1);
            int hand1 = game.TurnManager.GetHandOf(1).Count;

            bool ok = game.PMCommandManager.TryExecuteCommand("draw 1 ZZE01", out string error);

            Assert.IsFalse(ok, "卡组里没有该编号 → 失败");
            Assert.IsTrue(error != null && error.Contains("其它区域"), $"错误应含区域分布，实际: {error}");
            Assert.AreEqual(hand1, game.TurnManager.GetHandOf(1).Count, "手牌不变");
        }

        /// <summary>`draw 1 10` 这类"数量很大而卡组不足/编号不存在"的旧写法不回归（仍是牌库顶抽）。</summary>
        public void TestPMCommand_draw旧语法_数量形态不回归为取牌()
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

            Run(game, "draw 1 10");

            Assert.AreEqual(1, calledPlayer, "必须走「牌库顶抽牌」回调（不是定向取牌）");
            Assert.AreEqual(10, calledCount, "数量＝10");
        }

        // ==================== ①-d 【2026-10】对抗命令：单机走正规结算链（含获胜触发） ====================

        /// <summary>对抗窗口桩：记录收到的请求并立即"关闭窗口"（模拟 View 处理完毕）。</summary>
        private sealed class WindowStub
        {
            private readonly GameInitializer _game;
            public int Calls;
            public CombatManager.CombatType LastType;
            public int LastWinner, LastDiff, LastAreaNo;

            public WindowStub(GameInitializer game)
            {
                _game = game;
                _game.CombatManager.OnCombatWindowRequired += OnWindow;
            }

            private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
            {
                Calls++;
                LastType = type;
                LastWinner = winner;
                LastDiff = diff;
                LastAreaNo = areaNo;
                onResolved?.Invoke();
            }

            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
        }

        /// <summary>把一张探针角色放到指定地区（供对抗窗口/获胜触发的上下文）。</summary>
        private static CardInstance PutInArea(GameInitializer game, string code, int ownerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(Probe(code), ownerId);
            game.ZoneManager.MoveToArea(card, areaUid, ownerId);
            return card;
        }

        /// <summary>
        /// `investigation 1 1 1`（单机）：经 CombatManager.ResolveCombatFromDebug → ResolveWinner
        /// ⇒ 打开本机调查窗口，并在窗口关闭后**广播 OnInvestigationWon 获胜触发**
        /// （旧 PM 命令直调窗口协调器、绕过 ResolveWinner ⇒ 该触发完全不会广播）。
        /// 判据＝场上带 OnInvestigationWon 效果的探针在窗口关闭后入堆叠。
        /// </summary>
        public void TestPMCommand_investigation_单机走正规结算链并广播获胜()
        {
            var game = NewGame(1);
            var areaUid = game.AreaManager.GetOrderedAreaUids()[0];

            // 场上放一张"调查对抗获胜 → 入堆叠"的探针（仅验证广播是否发生）
            var tmpl = Probe("TS_pm_invwon");
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_pm_invwon_e",
                Timing = EffectTiming.OnInvestigationWon,
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            });
            tmpl._effectsInjected = true;
            var unit = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(unit, areaUid, 1);

            var stub = new WindowStub(game);
            try
            {
                Run(game, "investigation 1 1 1");
            }
            finally
            {
                stub.Detach();
            }

            Assert.AreEqual(1, stub.Calls, "必须打开一次本机调查对抗窗口");
            Assert.AreEqual(CombatManager.CombatType.Investigation, stub.LastType, "窗口类型＝调查对抗");
            Assert.AreEqual(1, stub.LastWinner, "胜者＝P1");
            Assert.AreEqual(1, stub.LastDiff, "获胜点数 X=1");
            Assert.AreEqual(1, stub.LastAreaNo, "地区号＝1");

            StackEntry found = null;
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId == "TS_pm_invwon_e") { found = e; break; }
            Assert.IsNotNull(found, "窗口关闭后必须广播 OnInvestigationWon（旧实现直调窗口 ⇒ 该触发不会发生）");
        }

        /// <summary>
        /// `battle 1 1 1`（单机）：打开战斗窗口；`IsDamageAssignmentOpen` 在**窗口打开期间**为 true、
        /// 关闭后复位（K26 口径；旧 PM 直调窗口时会一直为 false）。
        /// </summary>
        public void TestPMCommand_battle_单机窗口期间置位分配伤害阶段()
        {
            var game = NewGame(1);
            var areaUid = game.AreaManager.GetOrderedAreaUids()[0];
            PutInArea(game, "TS_pm_loser", 2, areaUid);   // 败方（P2）在场角色，保证有可分配对象

            bool openDuringWindow = false;
            int calls = 0;
            bool sawStrength = false;
            void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
            {
                calls++;
                sawStrength = type == CombatManager.CombatType.Strength;
                openDuringWindow = game.CombatManager.IsDamageAssignmentOpen;
                onResolved?.Invoke();
            }
            game.CombatManager.OnCombatWindowRequired += OnWindow;
            try
            {
                Run(game, "battle 1 1 1");
            }
            finally
            {
                game.CombatManager.OnCombatWindowRequired -= OnWindow;
            }

            Assert.AreEqual(1, calls, "必须打开一次本机战斗对抗窗口");
            Assert.IsTrue(sawStrength, "窗口类型＝战斗对抗");
            Assert.IsTrue(openDuringWindow, "窗口打开期间 IsDamageAssignmentOpen 必须为 true");
            Assert.IsFalse(game.CombatManager.IsDamageAssignmentOpen, "窗口关闭后必须复位");
        }

        /// <summary>
        /// `power 1 1 2`：瞬时结算（加势力标记、不弹窗），且**不截断**后续批量命令
        /// （旧实现把 power 列入 BlockingCommands，后面的命令会被静默丢弃）。
        /// </summary>
        public void TestPMCommand_power_瞬时结算且不截断批量命令()
        {
            var game = NewGame(1);
            var areaUid = game.AreaManager.GetOrderedAreaUids()[0];
            var before = game.AreaManager.GetPowerMark(areaUid, 1);

            int executed = game.PMCommandManager.ExecuteCommandBatch("power 1 1 2\ndraw 1 TSK01");

            var after = game.AreaManager.GetPowerMark(areaUid, 1);
            Assert.IsTrue(after > before, $"势力标记应增加（{before} → {after}）");
            Assert.AreEqual(2, executed, "power 之后的命令不能被截断（旧实现只执行 1 条）");
        }

        /// <summary>
        /// `investigation 3 1 1`：玩家参数非法 → 命令失败（不再静默按 P1 开窗）。
        /// </summary>
        public void TestPMCommand_对抗命令_玩家参数非法则失败()
        {
            var game = NewGame(1);
            var stub = new WindowStub(game);
            try
            {
                bool ok = game.PMCommandManager.TryExecuteCommand("investigation 3 1 1", out string error);
                Assert.IsFalse(ok, "玩家参数 3 非法 → 失败");
                Assert.IsTrue(error != null && error.Contains("玩家"), $"错误应提示玩家参数，实际: {error}");
            }
            finally
            {
                stub.Detach();
            }
            Assert.AreEqual(0, stub.Calls, "不得打开任何对抗窗口");
        }

        // ==================== ①-c 【2026-10】move / setassetfromdeck 的玩家参数语义 ====================

        /// <summary>`move 2 ZZ997 hand`：从 **P2** 卡组找编号 → 进 **P2** 手牌。</summary>
        public void TestPMCommand_move_编号与玩家参数()
        {
            var game = NewGame();
            var p1Card = PutInDeck(game, Probe("ZZ997_探针"), 1);
            var p2Card = PutInDeck(game, Probe("ZZ997_探针"), 2);

            Run(game, "move 2 ZZ997 hand");

            Assert.IsTrue(game.TurnManager.GetHandOf(2).Contains(p2Card), "P2 卡组的 ZZ997 → P2 手牌");
            Assert.IsTrue(InDeck(game, p1Card, 1), "P1 卡组的同编号卡不受影响");
        }

        /// <summary>`move 1 inst_xxx grave`：**实例 UID** 写法仍然可用（兼容既有用法）。</summary>
        public void TestPMCommand_move_实例UID兼容()
        {
            var game = NewGame();
            var card = PutInDeck(game, Probe("ZZ998_探针"), 1);

            Run(game, $"move 1 {card.UID} grave");

            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "按实例 UID 也能移动到墓地");
            Assert.IsFalse(InDeck(game, card, 1), "已离开卡组");
        }

        /// <summary>
        /// `move 1 ZZ999 1 99`：地区号越界（当前 2 个地区）→ 明确失败且卡仍在卡组，
        /// 不允许把 "99" 静默吞成别的参数继续执行。
        /// </summary>
        public void TestPMCommand_move_地区号越界报错()
        {
            var game = NewGame(2);
            var card = PutInDeck(game, Probe("ZZ999_探针"), 1);

            bool ok = game.PMCommandManager.TryExecuteCommand("move 1 ZZ999 1 99", out string error);

            Assert.IsFalse(ok, "地区号 99 越界 → 失败");
            Assert.IsTrue(!string.IsNullOrEmpty(error), "必须给出可读错误");
            Assert.IsTrue(InDeck(game, card, 1), "卡仍在卡组，未被移动");
            Assert.IsFalse(card.CurrentZone == CardZone.AreaZone, "没有被移到任何地区");
        }

        /// <summary>`setassetfromdeck 2 3`：从 **P2** 卡组顶取 3 张进 **P2** 资产区。</summary>
        public void TestPMCommand_setassetfromdeck_参数玩家()
        {
            var game = NewGame();
            for (int i = 0; i < 3; i++) PutInDeck(game, Probe($"ZZA9{i}_探针"), 2);

            Run(game, "setassetfromdeck 2 3");

            var p2Assets = game.ZoneManager.GetZoneCards(CardZone.AssetZone, 2);
            Assert.IsTrue(p2Assets.Count >= 3, $"P2 资产区应至少有 3 张，实际 {p2Assets.Count}");
            var p1Assets = game.ZoneManager.GetZoneCards(CardZone.AssetZone, 1);
            Assert.AreEqual(0, p1Assets.Count, "P1 资产区不受影响");
        }

        /// <summary>`setassetfromdeck 1 ZZ991`：按编号从 P1 卡组取 1 张进 P1 资产区。</summary>
        public void TestPMCommand_setassetfromdeck_按编号()
        {
            var game = NewGame();
            var card = PutInDeck(game, Probe("ZZB01_探针"), 1);

            Run(game, "setassetfromdeck 1 ZZB01");

            Assert.AreEqual(CardZone.AssetZone, card.CurrentZone, "该编号的卡进了资产区");
            Assert.AreEqual(1, card.ControllerId, "归属 P1");
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
