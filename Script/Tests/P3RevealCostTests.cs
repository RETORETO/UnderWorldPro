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
    /// 【P3·费用修正的**付费通道隔离**（"打出" vs "现身"）→ JC033 家族传承①】
    /// 卡文：结附于本方秘社 持续：所有本方暗藏者，**付费翻面时**其所需费用减少1。
    ///
    /// 交付：`ContinuousCostModSpec.CostScope`（`"打出"` 缺省 / `"现身"`）——
    ///  · 打出费用（`PlayCard` / 秘密派遣）与**现身费用**（`TurnManager.RevealCovert`，暗藏者付费翻面）
    ///    是两条独立通道，各自查询各自的减免（互不串台）；
    ///  · 现身通道改用**实例重载**查询 + 消费（子类别类过滤可用）。
    /// </summary>
    public class P3RevealCostTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";
        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new AbandonTriggerView());
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character, int cost = 3)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_rv_asset_{playerId}_{i}", CardCategory.Attachment, 0);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static CardInstance MakeCouncil(GameInitializer game, int playerId)
        {
            var councilTmpl = Godot.ResourceLoader.Load<CouncilCardData>(CouncilDir + "MSJC001_weimushouwang.tres");
            var inst = game.CardManager.CreateCardInstance(councilTmpl, playerId);
            game.ZoneManager.MoveToCouncil(inst, playerId);
            return inst;
        }

        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        private sealed class AbandonTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onDone?.Invoke();

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        // ==================== ① 结构 ====================

        public void TestRevealCost_JC033结构()
        {
            var tmpl = Load("JC033_jiazuchuancheng");
            Assert.IsNotNull(tmpl, "JC033 模板应可加载");
            var e = FindEffect(tmpl, "JC033_A_RevealCostMinusOne");
            Assert.IsNotNull(e, "JC033 应有『付费翻面费用 -1』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsNotNull(e.ContinuousCostMod, "声明了持续性费用修正");
            Assert.AreEqual(1, e.ContinuousCostMod.Amount, "减 1 费（正值＝减费）");
            Assert.AreEqual("现身", e.ContinuousCostMod.CostScope, "**只作用于『现身』付费通道**");
            Assert.IsFalse(e.ContinuousCostMod.PerTurn, "整回合（不限张数）");
            Assert.AreEqual("", e.ContinuousCostMod.Filter, "全匹配（现身通道本身只服务本方暗藏者）");
        }

        // ==================== ② 两条付费通道互不串台 ====================

        public void TestRevealCost_通道互不串台()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var council = MakeCouncil(game, 1);
            var badge = game.CardManager.CreateCardInstance(Load("JC033_jiazuchuancheng"), 1);
            Attach(game, council, badge);

            var card = game.CardManager.CreateCardInstance(Probe("TS_rv_card", CardCategory.Character, 3), 1);

            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, card, card.PlayerTemplate.CardName, "", "现身"),
                "**现身**通道：减 1");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, card, card.PlayerTemplate.CardName, ""),
                "**打出**通道：不受影响（默认 scope）");
        }

        // ==================== ③ 端到端：暗藏者付费翻面少付 1 费 ====================

        public void TestRevealCost_付费翻面少付一费()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)6);

            var council = MakeCouncil(game, 1);
            var badge = game.CardManager.CreateCardInstance(Load("JC033_jiazuchuancheng"), 1);
            Attach(game, council, badge);

            // 本方暗藏者（已在争夺区背面朝上）
            var covert = game.CardManager.CreateCardInstance(Probe("TS_rv_covert", CardCategory.Character, 3), 1);
            game.ZoneManager.MoveToArea(covert, area, 1);
            covert.SetCovert(true);
            game.TurnManager.PriorityPlayerId = 1;

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(game.TurnManager.RevealCovert(1, covert.UID), "付费翻面应成功");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1),
                "印刷费用 3 → 实付 2（家族传承：付费翻面 -1）");
        }

        /// <summary>对照：没有 JC033 时按全额付费。</summary>
        public void TestRevealCost_无减费则全额()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 5, (CardColor)6);

            var covert = game.CardManager.CreateCardInstance(Probe("TS_rv_covert2", CardCategory.Character, 3), 1);
            game.ZoneManager.MoveToArea(covert, area, 1);
            covert.SetCovert(true);
            game.TurnManager.PriorityPlayerId = 1;

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(game.TurnManager.RevealCovert(1, covert.UID));
            Assert.AreEqual(manaBefore - 3, game.PlayerManager.GetAvailableMana(1), "无减速修正 → 实付印刷费用 3");
        }

        // ==================== ④ 来源离场 → 现身减费消失 ====================

        public void TestRevealCost_来源离场后失效()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var council = MakeCouncil(game, 1);
            var badge = game.CardManager.CreateCardInstance(Load("JC033_jiazuchuancheng"), 1);
            Attach(game, council, badge);
            var card = game.CardManager.CreateCardInstance(Probe("TS_rv_card2", CardCategory.Character, 3), 1);

            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, card, card.PlayerTemplate.CardName, "", "现身"), "前置：生效");

            EffectEngine.Instance.UnregisterPersistent(badge);   // 附属注销（离场）

            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, card, card.PlayerTemplate.CardName, "", "现身"),
                "来源注销 → 现身减费消失");
        }
    }
}
