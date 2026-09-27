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
    /// 【P3·K1】"**玩家选择**"型动态值 → **JC083 睿智的斯芬克司**（用户口径 **A31** ＋ **A36**）：
    ///  · 准备步骤触发：**选择一个数字**（范围 **0～20**）→ 公开展示你牌库顶一张 →
    ///    印刷费用**相同** → 置入**手牌**（**不算抓牌**：不广播 `OnDraw`）；**不同** → 置入**牌库底**；
    ///  · 取值交互＝**复用既有数值输入窗**（`IGameUIHooks.PromptNumberInput`）：结算**之前**弹窗并挂起，
    ///    回调里写回选择值 → 重入结算；**取消 / 无 UI → 该效果不发动**（fail-closed，绝不替玩家猜数字）；
    ///  · **X=0 合法**：此处的 X 是"猜测值"而非"数量"，故不套用"X=0 ⇒ 效果不发动"
    ///    （否则猜不到"印刷费用为 0 的牌"）；对照 JC055② 的"至少移除 1 个"。
    /// </summary>
    public class P4ChooseNumberTests : TestRunner
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

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 833 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        /// <summary>把游戏的触发系统接到"记录总线"上，供断言 `OnDraw` 是否广播（与 `P3DrawFromBottomTests` 同款）。</summary>
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

        private static PlayerCardData Probe(string uid, int cost)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = CardCategory.Sorcery,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>把一张"牌库顶"探针放进某玩家的牌库顶（牌库约定：索引 0＝牌库顶）。</summary>
        private static CardInstance DeckTop(GameInitializer game, int playerId, string uid, int cost)
        {
            var card = game.CardManager.CreateCardInstance(Probe(uid, cost), playerId);
            game.ZoneManager.MoveToDeckTop(card, playerId);
            return card;
        }

        /// <summary>数值输入框桩：记录范围并按脚本回包（-1＝取消）。</summary>
        private sealed class NumberStub : Script.Interfaces.IGameUIHooks
        {
            public int Answer = -1;
            public int Calls;
            public int LastMin = -99, LastMax = -99;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked) { }
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen) { }

            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                Calls++;
                LastMin = min; LastMax = max;
                onChosen?.Invoke(Answer);
            }
        }

        /// <summary>就位：把 JC083 放到场上并返回它的效果数据（来源卡＝该角色）。</summary>
        private static (CardInstance card, EffectData effect) Ready(GameInitializer game, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(Load("JC083_ruizhidesifenkesi"), playerId);
            game.ZoneManager.MoveToArea(card, FirstArea(game), playerId);
            return (card, FindEffect(card.PlayerTemplate, "JC083_A_GuessTopDeckCost"));
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        // ==================== ① 结构 ====================

        public void TestChooseNumber_JC083结构()
        {
            var tmpl = Load("JC083_ruizhidesifenkesi");
            var e = FindEffect(tmpl, "JC083_A_GuessTopDeckCost");
            Assert.IsNotNull(e, "JC083 应有『选择数字 → 展示牌库顶』效果");
            Assert.AreEqual(EffectTiming.OnTurnStarted, e.Timing, "时机＝准备步骤");
            Assert.AreEqual("玩家选择", e.ValueExpr, "取值表达式＝玩家选择（K1 首次落地）");
            Assert.AreEqual(0, e.ValueMin, "A31：数字范围下限 0");
            Assert.AreEqual(20, e.ValueMax, "A31：数字范围上限 20");
            Assert.AreEqual(ActionVerb.RevealTopAndTakeIfCostIs, e.Actions[0].Verb, "动作＝展示库顶并按费用分流");
            Assert.AreEqual("$X", e.Actions[0].Args[0], "期望费用＝玩家所选数字（$X 占位符）");
        }

        // ==================== ② 费用相同 → 置入手牌且**不算抓牌** ====================

        public void TestChooseNumber_费用相同入手不算抓牌()
        {
            var game = NewGame();
            var rec = WireRecordingBus(game);
            var (card, effect) = Ready(game, 1);
            var top = DeckTop(game, 1, "TS_cn_top3", cost: 3);

            var stub = new NumberStub { Answer = 3 };
            game.EffectEngine.UIHooks = stub;

            int onDrawBefore = rec.Count(GameEvent.OnDraw);
            game.EffectEngine.ResolveEffect(effect, card, null, "", -1, -1, -1);

            Assert.AreEqual(1, stub.Calls, "弹了一次数值输入窗");
            Assert.AreEqual(0, stub.LastMin, "窗口下限＝0（A31）");
            Assert.AreEqual(20, stub.LastMax, "窗口上限＝20（A31）");
            Assert.AreEqual(CardZone.Hand, top.CurrentZone, "费用相同 → 库顶牌**置入手牌**");
            Assert.AreEqual(onDrawBefore, rec.Count(GameEvent.OnDraw), "**不算抓牌**：不广播 OnDraw");
        }

        // ==================== ③ 费用不同 → 置入牌库底 ====================

        public void TestChooseNumber_费用不同置入牌库底()
        {
            var game = NewGame();
            var (card, effect) = Ready(game, 1);
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;
            var top = DeckTop(game, 1, "TS_cn_top4", cost: 4);      // 成为牌库顶（索引 0）
            int deckAfter = game.TurnManager.GetDeckOf(1).Count;

            var stub = new NumberStub { Answer = 7 };               // 与库顶费用 4 不同
            game.EffectEngine.UIHooks = stub;

            game.EffectEngine.ResolveEffect(effect, card, null, "", -1, -1, -1);

            var deck = game.TurnManager.GetDeckOf(1);
            Assert.AreEqual(1, stub.Calls, "弹窗被调用");
            Assert.AreEqual(deckAfter, deck.Count, "牌仍留在牌库（没有入手）");
            Assert.AreEqual(CardZone.Deck, top.CurrentZone, "费用不同 → 该牌留在牌库");
            Assert.AreEqual(top.UID, deck[deck.Count - 1].UID, "且被**置于牌库底**（库约定：索引 0＝顶）");
            Assert.AreEqual(deckBefore + 1, deck.Count, "牌库总数不变（前置探针仍在）");
        }

        // ==================== ④ 选择 0 是合法猜测（不适用"X=0 不发动"） ====================

        public void TestChooseNumber_选择0合法且照常结算()
        {
            var game = NewGame();
            var (card, effect) = Ready(game, 1);
            var top = DeckTop(game, 1, "TS_cn_top0", cost: 0);

            var stub = new NumberStub { Answer = 0 };
            game.EffectEngine.UIHooks = stub;

            game.EffectEngine.ResolveEffect(effect, card, null, "", -1, -1, -1);

            Assert.AreEqual(1, stub.Calls, "弹窗被调用");
            Assert.AreEqual(CardZone.Hand, top.CurrentZone,
                "X=0 是**合法猜测**（该卡的 X 是猜测值而非数量）→ 费用为 0 的库顶牌入手");
        }

        // ==================== ⑤ 取消 → 不发动 ====================

        public void TestChooseNumber_取消则不发动()
        {
            var game = NewGame();
            var (card, effect) = Ready(game, 1);
            var top = DeckTop(game, 1, "TS_cn_top1", cost: 1);

            var stub = new NumberStub { Answer = -1 };   // 取消
            game.EffectEngine.UIHooks = stub;
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;

            game.EffectEngine.ResolveEffect(effect, card, null, "", -1, -1, -1);

            Assert.AreEqual(1, stub.Calls, "弹窗被调用");
            Assert.AreEqual(CardZone.Deck, top.CurrentZone, "取消 → 效果不发动，牌库顶不变");
            Assert.AreEqual(handBefore, game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count, "没有牌进入手牌");
        }

        // ==================== ⑥ 无 UI → 不发动（fail-closed） ====================

        public void TestChooseNumber_无UI则不发动()
        {
            var game = NewGame();
            var (card, effect) = Ready(game, 1);
            var top = DeckTop(game, 1, "TS_cn_top9", cost: 9);
            game.EffectEngine.UIHooks = null;   // 无 UI（headless 兜底）

            game.EffectEngine.ResolveEffect(effect, card, null, "", -1, -1, -1);

            Assert.AreEqual(CardZone.Deck, top.CurrentZone, "无 UI → 不发动，牌库顶不变");
            Assert.IsFalse(game.EffectEngine.HasPlayerChosenValue, "未残留玩家选择值");
        }

        // ==================== ⑦ 空牌库 → 无事发生（不崩） ====================

        public void TestChooseNumber_空牌库无事发生()
        {
            var game = NewGame();
            var (card, effect) = Ready(game, 1);
            // 清空该玩家牌库
            foreach (var c in new List<CardInstance>(game.ZoneManager.GetZoneCards(CardZone.Deck, 1)))
                game.ZoneManager.MoveToGraveyard(c, 1);
            Assert.AreEqual(0, game.ZoneManager.GetZoneCards(CardZone.Deck, 1).Count, "前置：牌库为空");

            var stub = new NumberStub { Answer = 2 };
            game.EffectEngine.UIHooks = stub;
            game.EffectEngine.ResolveEffect(effect, card, null, "", -1, -1, -1);   // 不抛异常即可

            Assert.AreEqual(1, stub.Calls, "仍先问数字");
        }
    }
}
