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
    /// 【P3·K5】"自本方卡牌上移除一个（时间）标志"作**发动代价** → JC044 灾难使者②。
    /// 口径（用户 2026-09，§7.4b A17/A27）：
    ///  · "移除一个时间标志"**就是发动代价**（不当作效果步骤）；
    ///  · 由**发动者自主选择**移除哪张卡上的标志（UI 选择窗；无 UI → 确定性兜底第一个合法候选）；
    ///  · 代价在**入堆叠之前**支付；**无合法候选 → 能力不可发动、无副作用**。
    /// </summary>
    public class P3CounterCostTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 44 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 3,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, game.AreaManager.GetOrderedAreaUids()[0], playerId);
            return card;
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked) => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        // ==================== ① 结构 ====================

        public void TestCounterCost_JC044结构()
        {
            var tmpl = Load("JC044_zainanshizhe");
            var e = FindEffect(tmpl, "JC044_B_RemoveTimeMarkerToCurb");
            Assert.IsNotNull(e, "JC044 应有『移除时间标志 → 遏制1』能力");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "时机＝角色激活");
            Assert.AreEqual("行动", e.ActivateWindow, "卡文『行动』");
            Assert.IsNotNull(e.CounterCost, "以 CounterCost 表达发动代价");
            Assert.AreEqual(1, e.CounterCost.Amount, "移除 1 个");
            Assert.IsTrue(e.CounterCost.Markers.Contains("时间标志"), "标志名白名单含『时间标志』");
            Assert.IsTrue(e.CounterCost.Markers.Contains("时间标记"), "兼容『时间标记』写法");
            Assert.AreEqual(ActionVerb.Curb, e.Actions[0].Verb, "效果＝遏制");
            Assert.AreEqual("1", e.Actions[0].Args[0], "遏制 1");
        }

        // ==================== ② 候选：只数本方场上带标志的卡 ====================

        public void TestCounterCost_候选只数本方场上带标志的卡()
        {
            var game = NewGame();
            var source = OnField(game, Load("JC044_zainanshizhe"), 1);
            var ownMarked = OnField(game, Probe("TS_cc_own_marked"), 1);
            var ownPlain = OnField(game, Probe("TS_cc_own_plain"), 1);
            var enemyMarked = OnField(game, Probe("TS_cc_enemy_marked"), 2);
            game.CardManager.AddCounter(ownMarked, "时间标志");
            game.CardManager.AddCounter(enemyMarked, "时间标志");

            var e = FindEffect(source.PlayerTemplate, "JC044_B_RemoveTimeMarkerToCurb");
            var cands = EffectEngine.Instance.GetCounterCostCandidates(source, e.CounterCost);

            Assert.AreEqual(1, cands.Count, "候选只有 1 张（本方场上带标志的卡）");
            Assert.AreEqual(ownMarked.UID, cands[0].UID, "正是本方那张带标志的卡");
            Assert.IsFalse(cands.Contains(ownPlain), "不带标志的本方卡不算候选");
            Assert.IsFalse(cands.Contains(enemyMarked), "敌方带标志的卡不算候选（卡文『本方卡牌』）");
        }

        // ==================== ③ 无候选 → 能力不可发动 ====================

        public void TestCounterCost_无候选则不可发动()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            var source = OnField(game, Load("JC044_zainanshizhe"), 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var e = FindEffect(source.PlayerTemplate, "JC044_B_RemoveTimeMarkerToCurb");
            Assert.IsFalse(Script.Manager.ActivateEligibility.CanActivate(source, game.TurnManager,
                game.PlayerManager, game.CardManager, false), "无带标志的卡 → 该能力不可发动（门禁）");

            EffectEngine.Instance.ActivateAbility(source, e, null);
            Assert.AreEqual(0, game.StackManager.Entries.Count, "拒绝发动 → 不入堆叠");
        }

        // ==================== ④ 有候选 → 代价先付（标志被移除）、再入堆叠 ====================

        public void TestCounterCost_支付后入堆叠()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            var source = OnField(game, Load("JC044_zainanshizhe"), 1);
            var marked = OnField(game, Probe("TS_cc_marked2"), 1);
            game.CardManager.AddCounter(marked, "时间标志", 2);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var e = FindEffect(source.PlayerTemplate, "JC044_B_RemoveTimeMarkerToCurb");
            EffectEngine.Instance.ActivateAbility(source, e, null, -1, null, null, marked.UID);

            Assert.AreEqual(1, marked.Counters["时间标志"], "代价已支付：移除 1 个时间标志（原 2 → 1）");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "入堆叠");
        }

        // ==================== ⑤ 指定非法候选 → 拒绝发动且无副作用 ====================

        public void TestCounterCost_指定非法候选则拒绝()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            var source = OnField(game, Load("JC044_zainanshizhe"), 1);
            var marked = OnField(game, Probe("TS_cc_marked3"), 1);
            var plain = OnField(game, Probe("TS_cc_plain3"), 1);
            game.CardManager.AddCounter(marked, "时间标志", 1);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var e = FindEffect(source.PlayerTemplate, "JC044_B_RemoveTimeMarkerToCurb");
            EffectEngine.Instance.ActivateAbility(source, e, null, -1, null, null, plain.UID);   // 指定不带标志的卡

            Assert.AreEqual(1, marked.Counters["时间标志"], "非法指定 → 未扣任何标志（fail-closed）");
            Assert.AreEqual(0, game.StackManager.Entries.Count, "拒绝发动 → 不入堆叠");
        }

        // ==================== ⑥ 「时间标记」另一种写法同样可作代价 ====================

        public void TestCounterCost_兼容时间标记写法()
        {
            var game = NewGame();
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
            var source = OnField(game, Load("JC044_zainanshizhe"), 1);
            var marked = OnField(game, Probe("TS_cc_marked4"), 1);
            game.CardManager.AddCounter(marked, "时间标记", 1);   // 卡库另一种写法
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var e = FindEffect(source.PlayerTemplate, "JC044_B_RemoveTimeMarkerToCurb");
            EffectEngine.Instance.ActivateAbility(source, e, null, -1, null, null, marked.UID);

            Assert.AreEqual(0, marked.Counters.ContainsKey("时间标记") ? marked.Counters["时间标记"] : 0,
                "『时间标记』写法同样被识别并扣除");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "入堆叠");
        }
    }
}
