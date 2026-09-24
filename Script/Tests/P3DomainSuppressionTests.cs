using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K20：持续型"失去**魔法领域图标**" → JC099 徘徊的鬼魂③】
    /// 卡文：持续：只要徘徊的幽灵在场且未横置，所有其上有牌封印的敌方角色失去魔法领域图标。
    ///
    /// 交付：`EffectData.DomainSuppression`（`DomainSuppressionSpec.Victim`）+ `PrereqKind.SelfUntapped` 前提；
    ///  · 领域抑制走 `CardInstance.SuppressAllDomains/RestoreDomains`（`AreDomainsSuppressed` 会被
    ///    `ResetToBaseValues` 尊重 → 任何属性重算都不会把图标"复活"）；
    ///  · 统一重算 `EffectEngine.ReapplySuppressions()`（先全部还原、再按局面施加，幂等），
    ///    触发点：来源登记/注销（含横置/竖置经 `ReevaluatePrereqs`）、封印落地/解除、卡的进出场。
  /// </summary>
    public class P3DomainSuppressionTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 1213 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character,
            MagicDomain? domain = MagicDomain.Mind)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = category, Defense = 5,
            };
            tmpl.SubCategories.Add("人类");
            if (domain.HasValue) tmpl.MagicDomains.Add(domain.Value);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static void SealOnto(GameInitializer game, CardInstance host, string uid, int ownerId)
        {
            var sealedCard = game.CardManager.CreateCardInstance(Probe(uid, CardCategory.Attachment, null), ownerId);
            game.TurnManager.SealCard(sealedCard, host, faceDown: false);
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

        public void TestDomain_JC099结构()
        {
            var tmpl = Load("JC099_paihuaideguihun");
            Assert.IsNotNull(tmpl, "JC099 模板应可加载");
            var e = FindEffect(tmpl, "JC099_3_SealedEnemyLoseDomains");
            Assert.IsNotNull(e, "JC099 应有『有封印的敌方角色失去领域图标』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsNotNull(e.DomainSuppression, "声明了领域图标抑制");
            Assert.AreEqual(DomainSuppressionVictim.EnemyCharacterWithSealedCards, e.DomainSuppression.Victim, "对象＝有封印的敌方角色");
            Assert.AreEqual(PrereqKind.SelfUntapped, e.PrereqSpec.Kind, "前提＝自身未横置（卡文『只要…未横置』）");
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ② 敌方 + 有封印 → 失去领域图标 ====================

        public void TestDomain_敌方有封印者失去领域()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC099_paihuaideguihun"), 1, areas[0]);
            var enemy = OnField(game, Probe("TS_ds_enemy"), 2, areas[0]);
            Assert.AreEqual(1, enemy.MagicDomains.Count, "前置：敌方角色本有 1 个魔法领域图标");

            SealOnto(game, enemy, "TS_ds_seal", 1);   // 封印落地 → 自动重判

            Assert.AreEqual(0, enemy.MagicDomains.Count, "其上有牌封印的敌方角色 → 失去全部领域图标");
        }

        // ==================== ③ 本方有封印者 / 敌方无封印者 都不受影响 ====================

        public void TestDomain_只针对敌方且有封印者()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC099_paihuaideguihun"), 1, areas[0]);
            var ownSealed = OnField(game, Probe("TS_ds_own"), 1, areas[0]);
            var enemyPlain = OnField(game, Probe("TS_ds_plain"), 2, areas[0]);
            SealOnto(game, ownSealed, "TS_ds_seal2", 1);

            Assert.AreEqual(1, ownSealed.MagicDomains.Count, "本方角色不受影响（卡文『敌方』）");
            Assert.AreEqual(1, enemyPlain.MagicDomains.Count, "无封印的敌方角色不受影响（卡文『其上有牌封印』）");
        }

        // ==================== ④ 来源横置 → 立即失效（SelfUntapped 前提） ====================

        public void TestDomain_来源横置后失效()
        {
            var game = NewGame();
            var areas = Areas(game);
            var ghost = OnField(game, Load("JC099_paihuaideguihun"), 1, areas[0]);
            var enemy = OnField(game, Probe("TS_ds_tap"), 2, areas[0]);
            SealOnto(game, enemy, "TS_ds_seal3", 1);
            Assert.AreEqual(0, enemy.MagicDomains.Count, "前置：被抑制");

            ghost.SetTapped(true);
            EffectEngine.Instance.ReevaluatePrereqs(ghost.ControllerId);   // 横置 → 门槛失效（生产路径由 TappedChanged 触发）

            Assert.AreEqual(1, enemy.MagicDomains.Count, "来源横置 → 领域图标恢复（『只要…未横置』）");

            ghost.SetTapped(false);
            EffectEngine.Instance.ReevaluatePrereqs(ghost.ControllerId);

            Assert.AreEqual(0, enemy.MagicDomains.Count, "来源竖置 → 再次抑制");
        }

        // ==================== ⑤ 封印被解除 → 恢复；来源离场 → 恢复 ====================

        public void TestDomain_封印解除与来源离场后恢复()
        {
            var game = NewGame();
            var areas = Areas(game);
            var ghost = OnField(game, Load("JC099_paihuaideguihun"), 1, areas[0]);
            var enemy = OnField(game, Probe("TS_ds_recover"), 2, areas[0]);
            SealOnto(game, enemy, "TS_ds_seal4", 1);
            Assert.AreEqual(0, enemy.MagicDomains.Count, "前置：被抑制");

            foreach (var s in new List<CardInstance>(enemy.SealedCards))
                game.ZoneManager.MoveToHand(s, s.OwnerId);   // 解封（走换区 → 重判）

            Assert.AreEqual(1, enemy.MagicDomains.Count, "封印被解除 → 领域图标恢复");

            SealOnto(game, enemy, "TS_ds_seal5", 1);
            Assert.AreEqual(0, enemy.MagicDomains.Count, "重新封印 → 再次抑制");

            game.ZoneManager.MoveToHand(ghost, 1);   // 来源离场 → 注销并重算
            Assert.AreEqual(1, enemy.MagicDomains.Count, "来源离场 → 领域图标恢复");
        }

        // ==================== ⑥ 抑制期间属性重算不会让图标"复活" ====================

        public void TestDomain_属性重算不复活()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC099_paihuaideguihun"), 1, areas[0]);
            var enemy = OnField(game, Probe("TS_ds_refresh"), 2, areas[0]);
            SealOnto(game, enemy, "TS_ds_seal6", 1);
            Assert.AreEqual(0, enemy.MagicDomains.Count, "前置：被抑制");

            enemy.ResetToBaseValues(game.PlayerManager.IsFirstPlayer(enemy.ControllerId));   // 属性重算

            Assert.AreEqual(0, enemy.MagicDomains.Count, "属性重算后仍为 0（抑制标志被重算逻辑尊重）");
        }
    }
}
