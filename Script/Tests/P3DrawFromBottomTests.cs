using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·非抓牌式取牌】"将你牌库底的两张牌**置于手中**"（JC049 深渊细语②）：
    /// 卡文写"置于手中"、FAQ 明确"**并不算抓牌**"→ 该取牌路径必须**不广播 `OnDraw`**
    /// （"每当你抓牌时…"类效果不响应），且**空库不判负**（判负规则绑定"抓牌"行为）。
    ///  · 数据表达：`DrawFromBottom{数量, "不计抓牌"}`（`HasArg` 标记）；
    ///  · 消费点：`EffectEngine` 的 `DrawFromBottom` 分支 → `TurnManager.DrawFromBottomForPlayer(p, n, countAsDraw:false)`；
    ///  · 对照：不带该标记（含秘社激活的既有调用）→ 照旧广播 `OnDraw`（一次调用＝一批）。
    /// </summary>
    public class P3DrawFromBottomTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>记录总线下发的所有事件（纯观察，不接触发收集）。</summary>
        private sealed class RecordingBus
        {
            public readonly List<GameEvent> Events = new List<GameEvent>();
            public GameEventBus Bus { get; }
            public RecordingBus()
            {
                Bus = new GameEventBus();
                Bus.SetDispatch((e, c) => Events.Add(e));
            }
            public int Count(GameEvent e)
            {
                int n = 0;
                foreach (var x in Events) if (x == e) n++;
                return n;
            }
        }

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 49 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        /// <summary>把游戏的触发系统接到"记录总线"上，供断言 `OnDraw` 是否广播。
        /// ⚠ `TriggerRegistry` 构造时会用 `SetDispatch(HandleEvent)` 占用总线分发，
        /// 故**构造之后再**包一层"先记录、再转发给注册表"的分发器（既观察事件、又保持触发收集照常）。</summary>
        private static RecordingBus WireRecordingBus(GameInitializer game)
        {
            var rec = new RecordingBus();
            var registry = new TriggerRegistry(rec.Bus);
            rec.Bus.SetDispatch((e, c) =>
            {
                rec.Events.Add(e);
                registry.HandleEvent(e, c);
            });
            var emit = new TriggerEmitPoints(rec.Bus);
            game.TurnManager.SetTriggerSystem(registry, emit, rec.Bus);
            return rec;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestDrawFromBottom_JC049结构()
        {
            var tmpl = Load("JC049_shenyuanxiyu");
            Assert.IsNotNull(tmpl, "JC049 模板应可加载");
            var e = FindEffect(tmpl, "JC049_A_TakeBottomTwoToHand");
            Assert.IsNotNull(e, "JC049 应有『牌库底两张置入手牌』效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "事务卡：打出即结算");
            Assert.AreEqual(ActionVerb.DrawFromBottom, e.Actions[0].Verb, "动词＝从牌库底取牌");
            Assert.AreEqual("2", e.Actions[0].Args[0], "数量＝2");
            Assert.AreEqual("不计抓牌", e.Actions[0].Args[1], "标记＝不计抓牌（FAQ：并不算抓牌）");
        }

        // ==================== ② 效果结算：牌库底两张进手牌、且**不广播 OnDraw** ====================

        public void TestDrawFromBottom_取牌不算抓牌_不广播OnDraw()
        {
            var game = NewGame();
            var rec = WireRecordingBus(game);

            var card = game.CardManager.CreateCardInstance(Load("JC049_shenyuanxiyu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            Assert.IsTrue(deckBefore >= 2, "前置：牌库至少两张");
            var bottom = game.TurnManager.GetDeckOf(1)[deckBefore - 1];   // 约定：索引 0＝牌库顶
            int drawsBefore = rec.Count(GameEvent.OnDraw);

            var e = FindEffect(card.PlayerTemplate, "JC049_A_TakeBottomTwoToHand");
            EffectEngine.Instance.ResolveEffect(e, card, null, "");

            Assert.AreEqual(deckBefore - 2, game.TurnManager.GetDeckOf(1).Count, "牌库底两张已取出");
            Assert.AreEqual(handBefore + 2, game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count, "两张进入手牌");
            Assert.AreEqual(CardZone.Hand, bottom.CurrentZone, "牌库底那张（最底）确实进了手牌");
            Assert.AreEqual(drawsBefore, rec.Count(GameEvent.OnDraw),
                "**不算抓牌** → 不广播 OnDraw（『每当你抓牌时…』类效果不响应）");
        }

        // ==================== ③ 对照：既有"从牌库底抽牌"仍属抓牌（广播一次/批） ====================

        public void TestDrawFromBottom_默认仍算抓牌_广播OnDraw()
        {
            var game = NewGame();
            var rec = WireRecordingBus(game);

            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            int drawsBefore = rec.Count(GameEvent.OnDraw);

            game.TurnManager.DrawFromBottomForPlayer(1, 2);   // 既有调用口径（countAsDraw 缺省 true）

            Assert.AreEqual(deckBefore - 2, game.TurnManager.GetDeckOf(1).Count, "取出两张");
            Assert.AreEqual(drawsBefore + 1, rec.Count(GameEvent.OnDraw),
                "既有口径仍是『抓牌』：一次调用＝一批 → 只广播一次 OnDraw");
        }

        // ==================== ④ 取牌标记与数量相互独立（数量缺省 1） ====================

        public void TestDrawFromBottom_数量缺省为1()
        {
            var game = NewGame();
            WireRecordingBus(game);

            var tmpl = new PlayerCardData
            {
                UID = "TS_dfb_probe", CardName = "探针_dfb", Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            var probe = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(probe, game.AreaManager.GetOrderedAreaUids()[0], 1);

            var eff = new EffectData
            {
                EffectId = "TS_dfb_one",
                Timing = EffectTiming.OnSorceryCast,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DrawFromBottom, Args = new Array<string> { "不计抓牌" } },
                },
            };

            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            EffectEngine.Instance.ResolveEffect(eff, probe, null, "");

            Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count, "数量缺省＝1（标记不占数量位）");
            Assert.AreEqual(handBefore + 1, game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count, "取到 1 张");
        }
    }
}
