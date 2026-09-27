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
    /// 【JC114 好奇的黑客】「行动1，横置：**目标玩家展示其手牌**。你可以从中选择一张，并**牺牲好奇的黑客**，
    /// **若你如此做**，该玩家弃掉你所选择的牌。」（用户口径 **A13** ＋ 交互答复）
    ///  · 激活：行动窗口 + 费用 1 + 横置自身（代价入堆叠前支付）；目标＝**玩家**（K24，入堆叠锁定）；
    ///  · 动作链：① `Inspect{"目标玩家手牌"}`（**私密**查看，只给施术者）→ ② `SacrificeSelfOptional`
    ///    （0/1 窗口；**牺牲 → 死亡触发照常 + 继续**；**不牺牲/取消/无 UI → 后续整段跳过**）→
    ///    ③ `Discard{"1","目标玩家","由施术者指定"}`（由施术者从其手牌中指定）。
    /// </summary>
    public class P4HackerTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 1140 };
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

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static PlayerCardData Probe(string uid, CardCategory category, int cost = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 5 : -1,
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

        /// <summary>给玩家放 n 张无色资产（供费用）。</summary>
        private static void GiveMana(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var asset = game.CardManager.CreateCardInstance(
                    Probe($"TS_hk_asset_{playerId}_{i}", CardCategory.Attachment), playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static CardInstance Hacker(GameInitializer game, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(Load("JC114_haoqideheike"), playerId);
            game.ZoneManager.MoveToArea(card, FirstArea(game), playerId);
            return card;
        }

        /// <summary>UI 桩：回答"是否牺牲"（0/1）并记录检视/弃牌选择。</summary>
        private sealed class HooksStub : Script.Interfaces.IGameUIHooks
        {
            public int SacrificeAnswer = 1;           // 1＝牺牲
            public int NumberCalls;
            public CardInstance DiscardPick;          // 施术者指定的那张牌（空＝桩内取第一张）
            public int DiscardChooser = -2;           // 记录收到的"选择者"
            public int DiscardDiscarder = -2;         // 记录收到的"弃牌者"

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
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
                System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(0);

            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked)
            {
                DiscardDiscarder = discarderId;
                DiscardChooser = chooserPlayerId;
                var pick = DiscardPick ?? (candidates != null && candidates.Count > 0 ? candidates[0] : null);
                onPicked?.Invoke(pick);
            }

            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                NumberCalls++;
                onChosen?.Invoke(SacrificeAnswer);
            }
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

        // ==================== ① 结构 ====================

        public void TestHacker_结构()
        {
            var tmpl = Load("JC114_haoqideheike");
            var e = FindEffect(tmpl, "JC114_1_RevealHandOptionalSacrificeDiscard");
            Assert.IsNotNull(e, "JC114 应有『查看手牌 → 可选牺牲 → 指定弃牌』效果");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活能力");
            Assert.AreEqual("行动", e.ActivateWindow, "卡文『行动』");
            Assert.AreEqual(1, e.Cost, "行动 1");
            Assert.AreEqual("横置本角色", e.ActivateCondition, "横置自身作代价");
            Assert.AreEqual(TargetKind.Player, e.Target.Kind, "目标＝玩家（K24）");
            Assert.AreEqual(3, e.Actions.Count, "三个动作：检视手牌 → 可选牺牲 → 指定弃牌");
            Assert.AreEqual(ActionVerb.Inspect, e.Actions[0].Verb, "① 查看目标玩家手牌");
            Assert.AreEqual("目标玩家手牌", e.Actions[0].Args[0], "对象＝目标玩家手牌");
            Assert.AreEqual(ActionVerb.SacrificeSelfOptional, e.Actions[1].Verb, "② 可选的牺牲自身");
            Assert.AreEqual(ActionVerb.Discard, e.Actions[2].Verb, "③ 弃牌");
            Assert.AreEqual("目标玩家", e.Actions[2].Args[1], "弃牌者＝目标玩家");
            Assert.AreEqual("由施术者指定", e.Actions[2].Args[2], "由**施术者**指定弃哪张");
        }

        // ==================== ② 选择牺牲 → 由施术者指定其一张手牌弃掉 ====================

        public void TestHacker_牺牲后指定弃牌()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            var hacker = Hacker(game, 1);
            // 对手手牌里放一张可识别的牌
            var victimCard = game.CardManager.CreateCardInstance(Probe("TS_hk_victim", CardCategory.Sorcery), 2);
            game.ZoneManager.MoveToHand(victimCard, 2);
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 2).Count;

            var ability = FindEffect(hacker.PlayerTemplate, "JC114_1_RevealHandOptionalSacrificeDiscard");
            var stub = new HooksStub { SacrificeAnswer = 1, DiscardPick = victimCard };
            game.EffectEngine.UIHooks = stub;
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            game.EffectEngine.ActivateAbility(hacker, ability, null, 2);   // 目标玩家＝2
            Assert.IsFalse(game.StackManager.IsEmpty, "已入堆叠");
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Graveyard, hacker.CurrentZone, "选择牺牲 → 好奇的黑客进墓地（牺牲＝死亡）");
            Assert.AreEqual(2, stub.DiscardDiscarder, "弃牌者＝目标玩家");
            Assert.AreEqual(1, stub.DiscardChooser, "**选择者＝施术者**（由施术者指定）");
            Assert.AreEqual(CardZone.Graveyard, victimCard.CurrentZone, "被指定的那张牌被弃掉");
            Assert.AreEqual(handBefore - 1, game.ZoneManager.GetZoneCards(CardZone.Hand, 2).Count, "对手手牌 -1");
            Assert.AreEqual(1, stub.NumberCalls, "询问了一次『是否牺牲』");
        }

        // ==================== ③ 不牺牲 → 什么都不发生（后续整段跳过） ====================

        public void TestHacker_不牺牲则什么都不发生()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            var hacker = Hacker(game, 1);
            var victimCard = game.CardManager.CreateCardInstance(Probe("TS_hk_victim2", CardCategory.Sorcery), 2);
            game.ZoneManager.MoveToHand(victimCard, 2);
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 2).Count;

            var ability = FindEffect(hacker.PlayerTemplate, "JC114_1_RevealHandOptionalSacrificeDiscard");
            var stub = new HooksStub { SacrificeAnswer = 0 };   // 不牺牲/取消
            game.EffectEngine.UIHooks = stub;
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            game.EffectEngine.ActivateAbility(hacker, ability, null, 2);
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AreaZone, hacker.CurrentZone, "不牺牲 → 好奇的黑客仍在场上");
            Assert.AreEqual(CardZone.Hand, victimCard.CurrentZone, "对手没有弃牌");
            Assert.AreEqual(handBefore, game.ZoneManager.GetZoneCards(CardZone.Hand, 2).Count, "手牌数不变");
            Assert.AreEqual(-2, stub.DiscardChooser, "**弃牌动作整段跳过**（没有请求过指定弃牌）");
        }

        // ==================== ④ 无 UI → 不牺牲（fail-closed，绝不替玩家牺牲） ====================

        public void TestHacker_无UI则不牺牲()
        {
            var game = NewGame();
            GiveMana(game, 1, 4);
            var hacker = Hacker(game, 1);
            var victimCard = game.CardManager.CreateCardInstance(Probe("TS_hk_victim3", CardCategory.Sorcery), 2);
            game.ZoneManager.MoveToHand(victimCard, 2);

            var ability = FindEffect(hacker.PlayerTemplate, "JC114_1_RevealHandOptionalSacrificeDiscard");
            game.EffectEngine.UIHooks = null;
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            game.EffectEngine.ActivateAbility(hacker, ability, null, 2);
            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AreaZone, hacker.CurrentZone, "无 UI → 兜底不牺牲");
            Assert.AreEqual(CardZone.Hand, victimCard.CurrentZone, "对手手牌不变");
        }
    }
}
