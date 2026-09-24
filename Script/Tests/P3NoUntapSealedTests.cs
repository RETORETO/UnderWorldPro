using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K29 重置步骤钩子 + K21 按"其上有牌封印"筛选 → JC108 乔治·帕佐，织梦人②】
    /// 卡文：持续：只要乔治·帕佐，织梦人在场，所有其上有牌封印的敌方角色于重置步骤中不能被重置。
    ///
    /// 交付：`StaticKind.NoUntapWithSealedCards`（与 `Timing = Continuous` 同一 `EffectData`，前提常驻）
    ///  · 生命周期与 `SkipAreaCombat`/`ExcludeCovertPower` 同款（前提满足、离场、前提丧失、
    ///    `RemoveAllContinuousFor`、`ResetAll` 五处收口）；**全局生效**（卡文无"本地区"限定）；
    ///  · 判定 `EffectEngine.IsUntapBlocked(card)`：① 本卡是**角色** ② 其上**有牌封印** ③ 场上存在登记来源
    ///    （在场、非死亡流程）且其控制者 ≠ 本卡控制者（＝"敌方"）；
    ///  · 消费点 `CardManager.UntapAll`（重置步骤**唯一**解横置出口）→ 命中者不解除横置，其余照常。
    /// </summary>
    public class P3NoUntapSealedTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 701 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = category, Defense = 3,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>把一张探针卡封印到宿主上（走 `SealCard` 同一收口）。</summary>
        private static void SealOnto(GameInitializer game, CardInstance host, string uid, int ownerId)
        {
            var sealedCard = game.CardManager.CreateCardInstance(Probe(uid, CardCategory.Attachment), ownerId);
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

        public void TestNoUntap_JC108结构()
        {
            var tmpl = Load("JC108_qiaozhipazuozhimengren");
            Assert.IsNotNull(tmpl, "JC108 模板应可加载");
            var e = FindEffect(tmpl, "JC108_2_NoUntapSealedEnemyCharacters");
            Assert.IsNotNull(e, "JC108 应有『封印角色不能被重置』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果");
            Assert.IsNotNull(e.StaticEffects, "声明了静止规则");
            Assert.AreEqual(StaticKind.NoUntapWithSealedCards, e.StaticEffects[0].Kind, "静止种类＝封印角色不可被重置");
        }

        // ==================== ② 判定：敌方 + 有封印 + 角色 才锁 ====================

        public void TestNoUntap_判定条件()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, areas[0]);   // 来源在场（P1）
            var enemySealed = OnField(game, Probe("TS_nu_enemy_sealed"), 2, areas[0]);
            var enemyPlain = OnField(game, Probe("TS_nu_enemy_plain"), 2, areas[0]);
            var ownSealed = OnField(game, Probe("TS_nu_own_sealed"), 1, areas[0]);
            SealOnto(game, enemySealed, "TS_nu_seal_a", 1);
            SealOnto(game, ownSealed, "TS_nu_seal_b", 1);
            foreach (var c in new[] { enemySealed, enemyPlain, ownSealed }) c.SetTapped(true);

            Assert.IsTrue(EffectEngine.Instance.IsUntapBlocked(enemySealed), "敌方 + 有封印 → 被锁");
            Assert.IsFalse(EffectEngine.Instance.IsUntapBlocked(enemyPlain), "无封印 → 不锁");
            Assert.IsFalse(EffectEngine.Instance.IsUntapBlocked(ownSealed), "本方角色 → 不锁（只锁『敌方』）");
        }

        // ==================== ③ 非角色不锁（卡文只写"角色"） ====================

        public void TestNoUntap_附属卡不锁()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, areas[0]);
            var enemyAttachment = OnField(game, Probe("TS_nu_enemy_att", CardCategory.Attachment), 2, areas[0]);
            SealOnto(game, enemyAttachment, "TS_nu_seal_c", 1);
            enemyAttachment.SetTapped(true);

            Assert.IsFalse(EffectEngine.Instance.IsUntapBlocked(enemyAttachment),
                "附属卡不是『角色』 → 不锁（判定按模板类别含 Character）");
        }

        // ==================== ④ 来源离场 → 立即失效 ====================

        public void TestNoUntap_来源离场即失效()
        {
            var game = NewGame();
            var areas = Areas(game);
            var van = OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, areas[0]);
            var enemySealed = OnField(game, Probe("TS_nu_d"), 2, areas[0]);
            SealOnto(game, enemySealed, "TS_nu_seal_d", 1);
            enemySealed.SetTapped(true);
            Assert.IsTrue(EffectEngine.Instance.IsUntapBlocked(enemySealed), "前置：被锁");

            game.ZoneManager.MoveToHand(van, 1);   // 来源离场

            Assert.IsFalse(EffectEngine.Instance.IsUntapBlocked(enemySealed), "来源离场 → 不再锁");
        }

        // ==================== ⑤ 端到端：重置步骤真的不解横置（对照照常重置） ====================

        public void TestNoUntap_端到端重置步骤()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, areas[0]);
            var enemySealed = OnField(game, Probe("TS_nu_e2e_locked"), 2, areas[0]);
            var enemyPlain = OnField(game, Probe("TS_nu_e2e_free"), 2, areas[0]);
            SealOnto(game, enemySealed, "TS_nu_seal_e", 1);
            enemySealed.SetTapped(true);
            enemyPlain.SetTapped(true);

            game.CardManager.UntapAll();   // 重置步骤

            Assert.IsTrue(enemySealed.IsTapped, "有封印的敌方角色：重置步骤后仍横置（不能被重置）");
            Assert.IsFalse(enemyPlain.IsTapped, "对照：同地区敌方无封印角色照常被重置");
        }

        // ==================== ⑥ 封印被移除后，下次重置步骤照常重置 ====================

        public void TestNoUntap_封印移除后恢复()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, areas[0]);
            var victim = OnField(game, Probe("TS_nu_f"), 2, areas[0]);
            SealOnto(game, victim, "TS_nu_seal_f", 1);
            victim.SetTapped(true);

            game.CardManager.UntapAll();
            Assert.IsTrue(victim.IsTapped, "前置：有封印 → 未重置");

            // 封印解除（封印牌回拥有者手牌）
            foreach (var seal in new List<CardInstance>(victim.SealedCards))
                game.ZoneManager.MoveToHand(seal, seal.OwnerId);

            game.CardManager.UntapAll();
            Assert.IsFalse(victim.IsTapped, "封印被移除 → 重置步骤照常解除横置（实时判定，无需重登记）");
        }
    }
}
