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
    /// 【P3·"每回合可再生的一次性"费用修正 → JC026 洛特尼科夫①】
    /// 卡文：持续：每个回合中你所打出的第一张装备牌，其所需费用减少1。
    ///
    /// 交付：`EffectData.ContinuousCostMod`（`ContinuousCostModSpec{Amount, Filter, PerTurn}`）
    ///  · 注册期间为**来源控制者**挂一条玩家级费用修正（限定条件走既有 `CostModFilter`，支持 `子类别:`）；
    ///  · **每回合可再生的一次性**（`TimedMod.Renewable`）：消费后本回合失效（记 `ConsumedTurn`），
    ///    **下回合自动恢复**（登记项即时效载体，无需重挂）；时效终结＝来源卡离场。
    ///  · 对照：普通"一次性"（`ConsumeOnce`，如 JC012）消费后**移除**，不会下回合再生。
    /// </summary>
    public class P3RenewableCostModTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

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

        /// <summary>装备牌探针（子类别含"装备"）。</summary>
        private static PlayerCardData EquipmentProbe(string uid, int cost = 3)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "装备_" + uid, Cost = cost,
                CategoryFlag = CardCategory.Attachment, Defense = 1,
            };
            tmpl.SubCategories.Add("装备");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Attachment, int cost = 0)
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
                var tmpl = Probe($"TS_rc_asset_{playerId}_{i}", CardCategory.Attachment);
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

        /// <summary>把 JC026 放到场上（触发持续效果注册）。</summary>
        private static CardInstance PlaceLottnikov(GameInitializer game, string area)
        {
            var card = game.CardManager.CreateCardInstance(Load("JC026_luotenikefu"), 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            return card;
        }

        // ==================== ① 结构 ====================

        public void TestRenewableCost_JC026结构()
        {
            var tmpl = Load("JC026_luotenikefu");
            Assert.IsNotNull(tmpl, "JC026 模板应可加载");
            var e = FindEffect(tmpl, "JC026_1_FirstEquipmentCostMinusOnePerTurn");
            Assert.IsNotNull(e, "JC026 应有『每回合第一张装备牌费用 -1』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsNotNull(e.ContinuousCostMod, "声明了持续性玩家级费用修正");
            Assert.AreEqual(1, e.ContinuousCostMod.Amount, "减 1 费（约定：正值＝减费）");
            Assert.AreEqual("子类别:装备", e.ContinuousCostMod.Filter, "限定『装备牌』");
            Assert.IsTrue(e.ContinuousCostMod.PerTurn, "每回合可再生的一次性");
        }

        // ==================== ② 注册即可减免（仅装备牌） ====================

        public void TestRenewableCost_注册后装备牌减费()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var lott = PlaceLottnikov(game, area);

            // 仅按卡名查询：`子类别:` 过滤无法命中（无实例可判子类别）→ 0；带实例查询才命中。
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, "装备牌A", ""),
                "仅按卡名查询 → 子类别过滤无法命中（0）");

            var equip = game.CardManager.CreateCardInstance(EquipmentProbe("TS_rc_eq"), 1);
            var nonEquip = game.CardManager.CreateCardInstance(Probe("TS_rc_plain", CardCategory.Attachment, 3), 1);
            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, equip, equip.PlayerTemplate?.CardName, ""),
                "**装备牌** → 减 1（约定：正值＝减费，实际费用 = 印刷费用 - mod）");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, nonEquip, nonEquip.PlayerTemplate?.CardName, ""),
                "非装备牌 → 不减（子类别过滤）");
            Assert.IsNotNull(lott, "来源卡在场");
        }

        // ==================== ③ 每回合可再生：用掉后本回合失效、下回合恢复 ====================

        public void TestRenewableCost_用掉后下回合自动恢复()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            PlaceLottnikov(game, area);
            var equip = game.CardManager.CreateCardInstance(EquipmentProbe("TS_rc_eq2"), 1);
            string name = equip.PlayerTemplate?.CardName;

            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, equip, name, ""), "前置：减 1 生效");

            // 打出一张装备牌 → 消费（本回合失效）
            EffectEngine.Instance.ConsumeCostModification(1, equip, "");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, equip, name, ""),
                "**本回合**已用掉 → 不再减免");

            // 进入下一回合（TurnCount 前进）→ 自动恢复
            game.TurnManager.NextTurn();
            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, equip, name, ""),
                "**下一回合自动恢复**（可再生，无需重挂）");

            // 再用一次 → 又失效（"每回合只减一次"）
            EffectEngine.Instance.ConsumeCostModification(1, equip, "");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, equip, name, ""), "第二次用掉后本回合再次失效");
        }

        // ==================== ④ 对照：普通"一次性"消费后不再恢复 ====================

        public void TestRenewableCost_普通一次性不恢复()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var equip = game.CardManager.CreateCardInstance(EquipmentProbe("TS_rc_eq3"), 1);
            string name = equip.PlayerTemplate?.CardName;

            EffectEngine.Instance.ApplyCostModification(1, 1, game.TurnManager.TurnCount, "子类别:装备", consumeOnce: true);
            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, equip, name, ""), "前置：一次性减费生效");

            EffectEngine.Instance.ConsumeCostModification(1, equip, "");
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, equip, name, ""), "消费后失效");

            game.TurnManager.NextTurn();
            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, equip, name, ""),
                "**普通一次性不会**下回合恢复（与 JC026 的可再生语义区分）");
        }

        // ==================== ⑤ 时效终结：来源卡离场后修正消失 ====================

        public void TestRenewableCost_来源离场后失效()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var lott = PlaceLottnikov(game, area);
            var equip = game.CardManager.CreateCardInstance(EquipmentProbe("TS_rc_eq4"), 1);
            string name = equip.PlayerTemplate?.CardName;

            Assert.AreEqual(1, EffectEngine.Instance.GetCostModification(1, equip, name, ""), "前置：生效");

            game.CardManager.ResolveDeath(lott, CardManager.DeathCause.Destroy);   // 离场 → UnregisterContinuous

            Assert.AreEqual(0, EffectEngine.Instance.GetCostModification(1, equip, name, ""),
                "来源离场 → 可再生费用修正随之清理");
        }

        // ==================== ⑥ 端到端：打出装备牌实际少付 1 费 ====================

        public void TestRenewableCost_打出装备牌实际减费()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 6, (CardColor)3);      // 提供无色费用
            PlaceLottnikov(game, area);
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var equip = game.CardManager.CreateCardInstance(EquipmentProbe("TS_rc_eq5", 3), 1);
            game.ZoneManager.MoveToHand(equip, 1);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsTrue(game.TurnManager.PlayCard(1, equip.UID), "装备牌可打出");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1),
                "印刷费用 3 → 实际只付 2（每回合第一张装备牌 -1）");
        }
    }
}
