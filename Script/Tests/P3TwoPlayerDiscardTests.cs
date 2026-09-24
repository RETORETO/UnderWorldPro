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
    /// 【P3·"每位玩家各弃一张"的多玩家弃牌编排 + JC041 禁婆②】
    /// 卡文：现身强制触发：每位玩家各弃一张手牌。
    ///
    /// 交付：
    ///  · `Discard{1, "双方", "自选"}` 全链路可跑：`ResolvePlayerSide("双方")` → 双方各**依次**询问
    ///    （`IGameUIHooks.PromptDiscard(discarderId, chooserPlayerId = 自己)`），各自自选一张后**分批**落地
    ///    （每批一次 `TurnManager.DiscardCards` → `OnDiscard` 每批**只广播一次**，用户 2026-09 口径）；
    ///  · JC041：`UncoverTrigger` + `Forced = true`（**强制**触发，不需要目标）。
    /// </summary>
    public class P3TwoPlayerDiscardTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
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

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
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

        /// <summary>触发排序窗口桩：全部按默认顺序入堆叠。</summary>
        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>弃牌 UI 桩：记录每位玩家的询问次数/候选数，并选第一张。</summary>
        private sealed class DiscardRecorder : Script.Interfaces.IGameUIHooks
        {
            public readonly List<int> Discarders = new List<int>();
            public readonly List<int> Choosers = new List<int>();
            public readonly List<int> CandidateCounts = new List<int>();

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked)
            {
                Discarders.Add(discarderId);
                Choosers.Add(chooserPlayerId);
                CandidateCounts.Add(candidates?.Count ?? 0);
                onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        // ==================== ① 结构 ====================

        public void TestTwoPlayerDiscard_JC041结构()
        {
            var tmpl = Load("JC041_jinpo");
            Assert.IsNotNull(tmpl, "JC041 模板应可加载");
            var e = FindEffect(tmpl, "JC041_1_EachPlayerDiscardsOne");
            Assert.IsNotNull(e, "JC041 应有『现身强制触发：每位玩家各弃一张手牌』");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e.Timing, "时机＝现身触发");
            Assert.IsTrue(e.Forced, "『强制触发』→ Forced=true");
            Assert.AreEqual(ActionVerb.Discard, e.Actions[0].Verb, "动作＝弃牌");
            Assert.AreEqual("1", e.Actions[0].Args[0], "各弃 1 张");
            Assert.AreEqual("双方", e.Actions[0].Args[1], "弃牌者＝双方");
            Assert.AreEqual("自选", e.Actions[0].Args[2], "各自自选");
            Assert.IsFalse(e.Target.NeedsSelection, "不需要选目标");
        }

        // ==================== ② 端到端：现身 → 双方各弃一张 ====================

        public void TestTwoPlayerDiscard_现身触发双方各弃一张()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var jinpo = game.CardManager.CreateCardInstance(Load("JC041_jinpo"), 1);
            game.ZoneManager.MoveToArea(jinpo, area, 1);
            jinpo.SetCovert(true);

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var rec = new DiscardRecorder();
            EffectEngine.Instance.UIHooks = rec;

            int hand1Before = game.TurnManager.GetHandOf(1).Count;
            int hand2Before = game.TurnManager.GetHandOf(2).Count;

            game.CardManager.RevealCard(jinpo);   // 现身 → UncoverTrigger

            Assert.IsFalse(game.StackManager.IsEmpty, "强制触发应入堆叠");
            ResolveStackOnce(game);

            Assert.AreEqual(2, rec.Discarders.Count, "应分别询问**两位玩家**各一次");
            Assert.IsTrue(rec.Discarders.Contains(1) && rec.Discarders.Contains(2), "两位玩家都被问到");
            foreach (int chooser in rec.Choosers)
                Assert.IsTrue(chooser == 1 || chooser == 2, "自选 → 选择者就是弃牌者本人");
            Assert.AreEqual(hand1Before - 1, game.TurnManager.GetHandOf(1).Count, "P1 弃 1 张");
            Assert.AreEqual(hand2Before - 1, game.TurnManager.GetHandOf(2).Count, "P2 弃 1 张");
            Assert.IsFalse(jinpo.IsCovert, "现身已生效");
        }

        /// <summary>无 UI（headless）→ 双方各按确定性兜底弃第一张（仍各弃一张）。</summary>
        public void TestTwoPlayerDiscard_无UI兜底也各弃一张()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var jinpo = game.CardManager.CreateCardInstance(Load("JC041_jinpo"), 1);
            game.ZoneManager.MoveToArea(jinpo, area, 1);
            jinpo.SetCovert(true);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            EffectEngine.Instance.UIHooks = null;

            int hand1Before = game.TurnManager.GetHandOf(1).Count;
            int hand2Before = game.TurnManager.GetHandOf(2).Count;

            game.CardManager.RevealCard(jinpo);
            ResolveStackOnce(game);

            Assert.AreEqual(hand1Before - 1, game.TurnManager.GetHandOf(1).Count, "P1 弃 1 张（兜底第一张）");
            Assert.AreEqual(hand2Before - 1, game.TurnManager.GetHandOf(2).Count, "P2 弃 1 张（兜底第一张）");
        }
    }
}
