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
    /// 【OnDraw 粒度（按批）+ JC080 凯拉·歌迪亚教授】按用户 2026-09 口径实现并锁定：
    ///  · **一次抓牌动作 = 一批**：`TurnManager.DrawCardsForPlayer` / `DrawFromBottomForPlayer` 的**一次调用**
    ///    在一批全部抓完后**只广播一次** `OnDraw`，并把本批张数写进 `TriggerContext.ExtraInt`
    ///    （→ `StackEffectInstance.EventAmount` → `StackEntry.EventAmount` → `ResolveEffect(eventAmount)`）；
    ///  · 一张都没抓到（牌库耗尽判负）→ **不广播**；
    ///  · JC080：`OnDraw` + 条件 `TriggerBySelf`（只有"你"抓牌才触发）+ `ValueExpr="事件数量"` +
    ///    `PlaceToken{势力标志, $X}`（落点＝来源卡所在地区＝"本地区"，放置归来源控制者＝"本方"）+ `Forced=false`（"你可以"）。
    /// </summary>
    public class P3DrawBatchTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 73 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>旁观探针：OnDraw 触发（可选、无动作），用于观测"广播了几次、带了多少张"。</summary>
        private static PlayerCardData DrawWatcher(string uid, string predicate = "TriggerBySelf")
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = uid + "_e",
                Timing = EffectTiming.OnDraw,
                Condition = new EffectCondition { Predicate = predicate },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } },
                },
                Forced = false,
            });
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>轮询等待（触发收集可能经由异步输入网关）。</summary>
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

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        // ==================== ① OnDraw 按批一次 + 带本批张数 ====================

        /// <summary>抓 2 张 → 只收集 1 条触发（不是 2 条），且事件数量＝2。</summary>
        public void TestOnDraw_一批只广播一次并带张数()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var watcher = game.CardManager.CreateCardInstance(DrawWatcher("TS_draw_watch"), 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            game.TurnManager.DrawCardsForPlayer(1, 2);

            Assert.IsTrue(WaitUntil(() => game.StackManager.Entries.Count >= 1), "抓牌应广播 OnDraw 并被收集");
            System.Threading.Thread.Sleep(80);   // 给"可能多收集的那一条"一点时间暴露出来
            Assert.AreEqual(1, game.StackManager.Entries.Count,
                "一次抓 2 张＝一批 → 只广播一次 OnDraw（不是逐张各触发一次）");
            Assert.AreEqual(2, game.StackManager.Entries[0].EventAmount,
                "本批张数（ExtraInt=2）应透传到堆叠条目，供『X＝此次抓牌的数量』取数");

            // 再抓 1 张＝新的一批 → 再收集 1 条，事件数量＝1
            ResolveStackOnce(game);
            game.TurnManager.DrawCardsForPlayer(1, 1);
            Assert.IsTrue(WaitUntil(() => game.StackManager.Entries.Count >= 1), "新的一批抓牌应再广播一次");
            Assert.AreEqual(1, game.StackManager.Entries[0].EventAmount, "本批张数＝1");
        }

        /// <summary>从牌库底抓牌走同一粒度口径（一次调用＝一批）。</summary>
        public void TestOnDraw_牌库底抓牌同样按批()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var watcher = game.CardManager.CreateCardInstance(DrawWatcher("TS_draw_watch_bottom"), 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            game.TurnManager.DrawFromBottomForPlayer(1, 2);

            Assert.IsTrue(WaitUntil(() => game.StackManager.Entries.Count >= 1), "从牌库底抓牌应广播 OnDraw");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "从牌库底抓 2 张也应只广播一次");
            Assert.AreEqual(2, game.StackManager.Entries[0].EventAmount, "本批张数＝2");
        }

        /// <summary>一批都没抓到（牌库耗尽判负）→ 不广播。</summary>
        public void TestOnDraw_牌库耗尽不广播()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var watcher = game.CardManager.CreateCardInstance(DrawWatcher("TS_draw_watch_empty"), 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            // 先把牌库抽空（数量足够抽干；抽干后不会再有广播）
            int guard = 0;
            while (game.TurnManager.GetDeckOf(2).Count > 0 && guard++ < 200)
                game.TurnManager.DrawCardsForPlayer(2, 1);
            Assert.AreEqual(0, game.TurnManager.GetDeckOf(2).Count, "前置：玩家2 牌库已抽空");

            int before = game.StackManager.Entries.Count;
            int handBefore = game.TurnManager.GetHandOf(2).Count;
            game.TurnManager.DrawCardsForPlayer(2, 2);   // 无牌可抽 → 玩家2 判负

            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(2).Count, "无牌可抽 → 没有牌进入手牌");
            Assert.AreEqual(before, game.StackManager.Entries.Count, "一张都没抓到 → 不广播 OnDraw");
        }

        // ==================== ② JC080 结构 ====================

        public void TestJC080_结构()
        {
            var tmpl = Load("JC080_kailagediyajiaoshou");
            Assert.IsNotNull(tmpl, "JC080 模板应可加载");

            var e = FindEffect(tmpl, "JC080_1_PlaceMarksOnDraw");
            Assert.IsNotNull(e, "JC080 应有『每当你抓牌时放置势力标志』效果");
            Assert.AreEqual(EffectTiming.OnDraw, e.Timing, "触发时机＝抓牌");
            Assert.AreEqual("TriggerBySelf", e.Condition.Predicate, "条件＝你（来源控制者）抓牌才触发");
            Assert.AreEqual("事件数量", e.ValueExpr, "X＝此次抓牌的数量");
            Assert.IsFalse(e.Forced, "卡文『你可以』→ 可选触发（非强制）");
            Assert.AreEqual(1, e.Actions.Count, "单动作");
            Assert.AreEqual(ActionVerb.PlaceToken, e.Actions[0].Verb, "动作为放置势力标志");
            Assert.AreEqual("势力标志", e.Actions[0].Args[0], "指示物类型＝势力标志");
            Assert.AreEqual("$X", e.Actions[0].Args[1], "数量用占位符 $X");
        }

        // ==================== ③ JC080 端到端 ====================

        /// <summary>本方抓 2 张 → 在本地区放置 2 个本方势力标志（触发收集 → 堆叠 → 结算的完整链路）。</summary>
        public void TestJC080_抓牌按批放置本方标志()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var kaila = game.CardManager.CreateCardInstance(Load("JC080_kailagediyajiaoshou"), 1);
            game.ZoneManager.MoveToArea(kaila, area, 1);

            int mineBefore = game.AreaManager.GetPowerMark(area, 1);
            int foeBefore = game.AreaManager.GetPowerMark(area, 2);

            game.TurnManager.DrawCardsForPlayer(1, 2);
            Assert.IsTrue(WaitUntil(() => game.StackManager.Entries.Count >= 1), "抓牌触发应被收集");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "抓牌触发应被收集（1 条）");
            ResolveStackOnce(game);

            Assert.AreEqual(mineBefore + 2, game.AreaManager.GetPowerMark(area, 1),
                "X＝此次抓牌数量 2 → 本地区本方势力标志 +2");
            Assert.AreEqual(foeBefore, game.AreaManager.GetPowerMark(area, 2), "不影响对手标志");
        }

        /// <summary>抓 1 张 → X=1；对手抓牌不触发（`TriggerBySelf`）。</summary>
        public void TestJC080_抓一张与本方判定()
        {
            var game = NewGame();
            game.SetInputGateway(new EnterAllGateway());
            string area = FirstArea(game);
            var kaila = game.CardManager.CreateCardInstance(Load("JC080_kailagediyajiaoshou"), 1);
            game.ZoneManager.MoveToArea(kaila, area, 1);

            int mineBefore = game.AreaManager.GetPowerMark(area, 1);

            // 对照：**对手**抓牌 → JC080 是"每当你抓牌时"，不应触发
            game.TurnManager.DrawCardsForPlayer(2, 2);
            System.Threading.Thread.Sleep(120);
            Assert.AreEqual(0, game.StackManager.Entries.Count, "对手抓牌不应触发凯拉（TriggerBySelf）");

            // 本方抓 1 张 → X=1
            game.TurnManager.DrawCardsForPlayer(1, 1);
            Assert.IsTrue(WaitUntil(() => game.StackManager.Entries.Count >= 1), "本方抓牌应触发");
            ResolveStackOnce(game);

            Assert.AreEqual(mineBefore + 1, game.AreaManager.GetPowerMark(area, 1), "X=1 → 本地区本方标志 +1");
        }

        /// <summary>触发排序窗口网关：全部入堆叠（与 P1/P3/P4 测试同款）。</summary>
        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }
    }
}
