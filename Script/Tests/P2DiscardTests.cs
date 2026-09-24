using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【P2·弃牌原语】回归：四种选择方式（自选 / 随机 / 被指定者自选 / 由施术者指定）+ `OnDiscard` 接线 + 联机网关路径。
    ///
    /// 规则口径（用户确认）：
    ///  ① "令目标玩家弃一张手牌" = **被指定者自己选**；
    ///  ② 随机弃牌复用项目的**可复现随机源**；
    ///  ③ **允许**把对手手牌的临时视图**只下发给施术者本人**（JC114）；
    ///  ④ 弃牌 = 手牌 → 墓地（**不是死亡**：不触发死亡触发）。
    /// </summary>
    public class P2DiscardTests : TestRunner
    {
        /// <summary>记录请求并自动"选第一张"的网关（用于联机路径断言）。</summary>
        private sealed class AutoPickGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public System.Func<ChoiceRequest, string> Pick;
            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                string uid = Pick?.Invoke(req);
                if (string.IsNullOrEmpty(uid) && req?.OptionUids != null && req.OptionUids.Count > 0)
                    uid = req.OptionUids[0];
                return Task.FromResult(new ChoiceResult
                {
                    RequestId = req?.RequestId,
                    SelectedUid = uid,
                    Cancelled = false,
                });
            }
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 31 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Probe(string uid, EffectData effect = null,
            CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = "探针_" + uid, CategoryFlag = category };
            if (effect != null) tmpl.EffectDataList.Add(effect);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData DiscardEffect(string id, string count, string who, string how)
        {
            var e = new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };
            e.Actions.Add(new EffectAction
            {
                Verb = ActionVerb.Discard,
                Args = new Array<string> { count, who, how },
            });
            return e;
        }

        private static bool WaitUntil(System.Func<bool> cond, int timeoutMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                System.Threading.Thread.Sleep(5);
            }
            return cond();
        }

        private static bool StackHas(GameInitializer game, string sourceUid)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.SourceCardUid == sourceUid) return true;
            return false;
        }

        // ==================== ① 自选（无 UI → 确定性兜底） ====================

        public void TestP2Discard_自选_兜底弃第一张并广播弃牌时机()
        {
            var game = NewGame();
            var gw = new AutoPickGateway();
            game.SetInputGateway(gw);   // 走生产装配路径（同时注入引擎与回合管理器）

            // 场上旁观卡（控制者=玩家1）：监听"敌方玩家弃牌"（JC040 型）
            var watcher = Probe("TS_discard_watch", new EffectData
            {
                EffectId = "TS_discard_watch_e",
                Timing = EffectTiming.OnDiscard,
                Condition = new EffectCondition { Predicate = "DiscardByEnemy" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
            });
            var watcherInst = game.CardManager.CreateCardInstance(watcher, 1);
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            game.ZoneManager.MoveToArea(watcherInst, area, 1);

            // 源卡（事务，控制者=玩家1）→ "对手"= 玩家2 弃牌
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_discard_src", DiscardEffect("TS_disc_src_e", "1", "对手", "自选")), 1);
            int p2Before = game.TurnManager.GetHandOf(2).Count;
            Assert.IsTrue(p2Before > 0, "前置：玩家2 有手牌");

            EffectEngine.Instance.ResolveEffectAsync(src.PlayerTemplate.EffectDataList[0], src, null).Wait(5000);

            Assert.AreEqual(p2Before - 1, game.TurnManager.GetHandOf(2).Count, "被指定者应弃掉 1 张手牌");
            Assert.IsTrue(WaitUntil(() => StackHas(game, watcherInst.UID)),
                "弃牌应广播 OnDiscard，且『敌方玩家弃牌』成立时旁观卡被收集");

            // 对照：本方（玩家1）自己弃牌 → 旁观卡（控制者=玩家1）的 DiscardByEnemy 不成立
            int entriesBefore = game.StackManager.Entries.Count;
            int p1Hand = game.TurnManager.GetHandOf(1).Count;
            Assert.IsTrue(p1Hand > 0, "前置：玩家1 有手牌");
            game.TurnManager.DiscardCards(1, new[] { game.TurnManager.GetHandOf(1)[0] }, 1);
            System.Threading.Thread.Sleep(150);
            Assert.AreEqual(entriesBefore, game.StackManager.Entries.Count,
                "本方玩家自己弃牌不应触发『每当敌方玩家弃牌时』");
        }

        // ==================== ② 对手弃牌（被指定者自选） ====================

        public void TestP2Discard_对手弃牌_由被指定者自选()
        {
            var game = NewGame();
            var gw = new AutoPickGateway
            {
                // 断言：请求发给**被指定者**（玩家2），候选是他的手牌
                Pick = req =>
                {
                    Assert.AreEqual(2, req.PlayerId, "『令目标玩家弃一张手牌』的选牌请求应发给被指定者本人");
                    return req.OptionUids.Count > 0 ? req.OptionUids[1 % req.OptionUids.Count] : null;
                },
            };
            game.SetInputGateway(gw);   // 生产装配路径：同时注入引擎与回合管理器

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_discard_opp", DiscardEffect("TS_disc_opp_e", "1", "对手", "自选")), 1);
            int p2Before = game.TurnManager.GetHandOf(2).Count;
            Assert.IsTrue(p2Before > 0, "前置：玩家2 有手牌");

            EffectEngine.Instance.ResolveEffectAsync(src.PlayerTemplate.EffectDataList[0], src, null).Wait(5000);

            Assert.AreEqual(1, gw.Requests.Count, "应恰好发起一次选牌请求");
            Assert.AreEqual(ChoiceRequest.Kind.SelectHandCard, gw.Requests[0].RequestKind, "请求种类应为 SelectHandCard");
            Assert.AreEqual(p2Before - 1, game.TurnManager.GetHandOf(2).Count, "被指定者应弃掉 1 张手牌");
        }

        // ==================== ③ 随机弃牌（可复现随机源） ====================

        public void TestP2Discard_随机弃牌_走可复现随机源()
        {
            var game = NewGame();
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_discard_rand", DiscardEffect("TS_disc_rand_e", "2", "自己", "随机")), 1);
            int before = game.TurnManager.GetHandOf(1).Count;
            Assert.IsTrue(before >= 2, "前置：玩家1 手牌 ≥ 2");

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(before - 2, game.TurnManager.GetHandOf(1).Count, "随机弃牌应弃掉 2 张手牌");
            Assert.IsNull(game.TurnManager.InputGateway, "随机弃牌不需要任何玩家决策（不应发起请求）");
        }

        // ==================== ④ 由施术者指定（JC114：临时下发对手手牌） ====================

        public void TestP2Discard_由施术者指定_候选只发给施术者()
        {
            var game = NewGame();
            var gw = new AutoPickGateway();
            game.SetInputGateway(gw);   // 生产装配路径：同时注入引擎与回合管理器

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_discard_pick", DiscardEffect("TS_disc_pick_e", "1", "对手", "由施术者指定")), 1);
            src.ControllerId = 1;
            int p2Before = game.TurnManager.GetHandOf(2).Count;

            EffectEngine.Instance.ResolveEffectAsync(src.PlayerTemplate.EffectDataList[0], src, null).Wait(5000);

            Assert.AreEqual(1, gw.Requests.Count, "应发起一次选牌请求");
            var req = gw.Requests[0];
            Assert.AreEqual(1, req.PlayerId, "JC114：请求应发给**施术者**（玩家1），由其从对手手牌中指定");
            Assert.AreEqual(p2Before, req.OptionUids.Count, "候选 = 对手（玩家2）的全部手牌");
            Assert.AreEqual(p2Before, req.OptionCardRefs.Count, "候选需带展示引用（临时下发给施术者本人）");
            Assert.AreEqual(p2Before - 1, game.TurnManager.GetHandOf(2).Count, "被指定的牌应由玩家2 弃掉");
        }

        // ==================== ⑤ 弃牌不是死亡 ====================

        public void TestP2Discard_弃牌不触发死亡()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AutoPickGateway();

            // 被弃的牌自身带死亡触发 → 弃牌（手牌→墓地）不应触发死亡
            var dieProbe = Probe("TS_discard_nodeath", new EffectData
            {
                EffectId = "TS_discard_nodeath_e",
                Timing = EffectTiming.OnCardDied,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
            });
            var card = game.CardManager.CreateCardInstance(dieProbe, 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.DiscardCards(1, new[] { card }, 1);
            System.Threading.Thread.Sleep(120);

            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "前置：牌已弃入墓地");
            Assert.IsFalse(StackHas(game, card.UID),
                "弃牌（手牌→墓地）不是死亡 → 即便该牌带了死亡触发也不应触发");
        }

        // ==================== ⑥ 【粒度】一次弃牌动作只广播一次 OnDiscard（用户 2026-09 口径） ====================

        /// <summary>一次弃 3 张 → 只收集 1 条触发，且事件数量＝3（不是逐张各触发一次）。</summary>
        public void TestP2Discard_一批只广播一次并带总张数()
        {
            var game = NewGame();
            game.SetInputGateway(new AutoPickGateway());
            string area = game.AreaManager.GetOrderedAreaUids()[0];

            // 旁观卡（控制者=玩家1）：每当敌方玩家弃牌时触发
            var watcher = game.CardManager.CreateCardInstance(Probe("TS_discard_batch_watch", new EffectData
            {
                EffectId = "TS_discard_batch_watch_e",
                Timing = EffectTiming.OnDiscard,
                Condition = new EffectCondition { Predicate = "DiscardByEnemy" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Forced = false,
            }), 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            // 源卡（事务，控制者=玩家1）→ 令对手（玩家2）弃 3 张（自选）
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_discard_batch_src", DiscardEffect("TS_disc_batch_e", "3", "对手", "自选")), 1);
            src.ControllerId = 1;
            int p2Before = game.TurnManager.GetHandOf(2).Count;
            Assert.IsTrue(p2Before >= 3, "前置：玩家2 手牌 ≥ 3");

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);
            System.Threading.Thread.Sleep(120);

            Assert.AreEqual(p2Before - 3, game.TurnManager.GetHandOf(2).Count, "一次动作应弃掉 3 张");
            Assert.AreEqual(1, game.StackManager.Entries.Count,
                "一次弃牌动作＝一批 → 只广播一次 OnDiscard（不是逐张各触发一次）");
            Assert.AreEqual(3, game.StackManager.Entries[0].EventAmount,
                "本批总张数（ExtraInt=3）应透传，供『X＝此次弃牌的数量』取数");
        }

        /// <summary>端到端：JC040 安普莎在对方一次弃 3 张时**只触发一次**、遏制 3（不是 3 次×3＝9）。</summary>
        public void TestP2Discard_JC040_一次弃3张只遏制3()
        {
            var game = NewGame();
            game.SetInputGateway(new AutoPickGateway());
            string area = game.AreaManager.GetOrderedAreaUids()[0];

            var anpusa = game.CardManager.CreateCardInstance(LoadJc("JC040_anpushamingfudeshinv"), 1);
            game.ZoneManager.MoveToArea(anpusa, area, 1);
            game.AreaManager.AddPowerMark(area, 2, 5);
            Assert.AreEqual(5, game.AreaManager.GetPowerMark(area, 2), "前置：对手 5 个势力标志");

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_discard_jc040_src", DiscardEffect("TS_disc_jc040_e", "3", "对手", "自选")), 1);
            src.ControllerId = 1;
            int p2Before = game.TurnManager.GetHandOf(2).Count;
            Assert.IsTrue(p2Before >= 3, "前置：玩家2 手牌 ≥ 3");

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);
            System.Threading.Thread.Sleep(120);

            Assert.AreEqual(1, game.StackManager.Entries.Count, "JC040 只应收集 1 条触发");
            ResolveStackOnce(game);

            Assert.AreEqual(2, game.AreaManager.GetPowerMark(area, 2),
                "X＝此次弃牌数量 3 → 遏制 3（5→2）；若按旧的逐张广播会变成 3 次×3＝9");
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        private static PlayerCardData LoadJc(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>("res://Cards/Data/PlayerCards/JC/" + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }
    }
}
