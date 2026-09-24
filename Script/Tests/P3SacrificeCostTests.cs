using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K22 牺牲代价第一批 → JC094 威达的汀巴巴迪】
    /// 卡文：行动，牺牲本地区的另一个角色：消灭目标角色或暗藏者。
    ///
    /// 口径（用户 2026-09）：
    ///  · 牺牲由**效果发动者自己选择**（UI 选择窗；无 UI/未指定 → 确定性兜底第一个合法候选）；
    ///  · **除非卡文明确写"暗藏者"，否则不能牺牲暗藏者**；
    ///  · **牺牲＝死亡** → 走 `ResolveDeath(Sacrifice)`，**死亡时机照常触发**；
    ///  · 代价在**入堆叠之前**支付；无候选 / 指定候选不合法 → 拒绝发动且**无副作用**。
    /// </summary>
    public class P3SacrificeCostTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 823 };
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

        private static PlayerCardData Probe(string uid, int defense = 3, CardCategory category = CardCategory.Character,
            int investigation = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = category, Defense = defense, Investigation = investigation,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area,
            bool covert = false)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            if (covert) card.SetCovert(true);
            return card;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>让 P1 拿到优先权/行动权，便于发动"行动"类激活能力。</summary>
        private static void PrepareActionWindow(GameInitializer game)
        {
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
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

        public void TestSacrifice_JC094结构()
        {
            var tmpl = Load("JC094_weidadetingbabadi");
            Assert.IsNotNull(tmpl, "JC094 模板应可加载");
            var e = FindEffect(tmpl, "JC094_1_SacrificeAllyDestroyTarget");
            Assert.IsNotNull(e, "JC094 应有『牺牲本地区另一个角色 → 消灭目标』能力");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活能力");
            Assert.AreEqual("行动", e.ActivateWindow, "行动窗口");
            Assert.IsNotNull(e.SacrificeCost, "声明了牺牲代价");
            Assert.IsTrue(e.SacrificeCost.LocalZone, "代价限『本地区』");
            Assert.IsTrue(e.SacrificeCost.ExcludeSource, "代价排除自身（『另一个角色』）");
            Assert.IsFalse(e.SacrificeCost.AllowCovert, "不可牺牲暗藏者（卡文未写暗藏者）");
            Assert.IsTrue(e.Target.IncludeCovert, "目标可为暗藏者");
            Assert.AreEqual(ActionVerb.Destroy, e.Actions[0].Verb, "动作为消灭");
        }

        // ==================== ② 候选过滤：本方 + 本地区 + 非自身 + 非暗藏者 ====================

        public void TestSacrifice_候选过滤()
        {
            var game = NewGame();
            var areas = Areas(game);
            var wd = OnField(game, Load("JC094_weidadetingbabadi"), 1, areas[0]);
            var sameAreaAlly = OnField(game, Probe("TS_sc_ally"), 1, areas[0]);
            var otherAreaAlly = OnField(game, Probe("TS_sc_other_area"), 1, areas[1]);
            var covertAlly = OnField(game, Probe("TS_sc_covert"), 1, areas[0], covert: true);
            var enemyChar = OnField(game, Probe("TS_sc_enemy"), 2, areas[0]);
            var ability = FindEffect(wd.PlayerTemplate, "JC094_1_SacrificeAllyDestroyTarget");

            var candidates = ActivateEligibility.GetSacrificeCandidates(wd, ability);
            Assert.IsTrue(candidates.Contains(sameAreaAlly), "本方同地区角色 → 合法候选");
            Assert.IsFalse(candidates.Contains(wd), "来源自身不合法（『另一个角色』）");
            Assert.IsFalse(candidates.Contains(otherAreaAlly), "其它地区不合法（『本地区』）");
            Assert.IsFalse(candidates.Contains(covertAlly), "暗藏者不合法（卡文未写暗藏者）");
            Assert.IsFalse(candidates.Contains(enemyChar), "敌方角色不合法（代价只能用自己的）");
            Assert.AreEqual(1, candidates.Count, "恰好 1 个合法候选");
        }

        // ==================== ③ 无候选 → 不可发动 ====================

        public void TestSacrifice_无候选不可发动()
        {
            var game = NewGame();
            var areas = Areas(game);
            var wd = OnField(game, Load("JC094_weidadetingbabadi"), 1, areas[0]);   // 本地区只有它自己
            var victim = OnField(game, Probe("TS_sc_victim"), 2, areas[0]);         // 敌方目标（不可牺牲）
            PrepareActionWindow(game);
            var ability = FindEffect(wd.PlayerTemplate, "JC094_1_SacrificeAllyDestroyTarget");

            Assert.IsFalse(ActivateEligibility.CanActivate(wd, game.TurnManager, game.PlayerManager, game.CardManager),
                "没有可牺牲的角色 → 该能力不可发动（UI 置灰）");

            EffectEngine.Instance.ActivateAbility(wd, ability, victim);
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "被拒绝的发动不产生任何效果（目标未被消灭）");
        }

        // ==================== ④ 端到端：牺牲代价 + 消灭目标 ====================

        public void TestSacrifice_端到端牺牲并消灭目标()
        {
            var game = NewGame();
            var areas = Areas(game);
            var wd = OnField(game, Load("JC094_weidadetingbabadi"), 1, areas[0]);
            var ally = OnField(game, Probe("TS_sc_ally2"), 1, areas[0]);
            var victim = OnField(game, Probe("TS_sc_victim2"), 2, areas[0]);
            PrepareActionWindow(game);
            var ability = FindEffect(wd.PlayerTemplate, "JC094_1_SacrificeAllyDestroyTarget");

            EffectEngine.Instance.ActivateAbility(wd, ability, victim, -1, ally.UID);

            // 代价在**入堆叠之前**已支付（牺牲者立刻进墓地）；主效果此时仍在堆叠上、尚未结算。
            Assert.AreEqual(CardZone.Graveyard, ally.CurrentZone, "代价先付：被牺牲的角色立刻进墓地（牺牲＝死亡）");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "主效果尚未结算（仍在堆叠上）→ 目标还在场上");

            // 双方连续让过 → 堆叠结算 → 消灭生效
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "结算后：目标被消灭");
            Assert.AreEqual(CardZone.Graveyard, ally.CurrentZone, "结算后：被牺牲者仍在墓地");
        }

        // ==================== ⑤ 牺牲触发死亡时机（CardManager.OnCardDied 广播） ====================

        public void TestSacrifice_牺牲触发死亡时机()
        {
            var game = NewGame();
            var areas = Areas(game);
            var wd = OnField(game, Load("JC094_weidadetingbabadi"), 1, areas[0]);
            var ally = OnField(game, Probe("TS_sc_died"), 1, areas[0]);
            var victim = OnField(game, Probe("TS_sc_died_victim"), 2, areas[0]);
            PrepareActionWindow(game);
            var ability = FindEffect(wd.PlayerTemplate, "JC094_1_SacrificeAllyDestroyTarget");

            var diedUids = new List<string>();
            game.CardManager.OnCardDied += c => diedUids.Add(c.UID);

            EffectEngine.Instance.ActivateAbility(wd, ability, victim, -1, ally.UID);

            Assert.IsTrue(diedUids.Contains(ally.UID), "牺牲触发死亡时机（OnCardDied 广播了被牺牲者）");
        }

        // ==================== ⑥ 指定非法候选 → 拒绝且无副作用（fail-closed） ====================

        public void TestSacrifice_指定非法候选被拒()
        {
            var game = NewGame();
            var areas = Areas(game);
            var wd = OnField(game, Load("JC094_weidadetingbabadi"), 1, areas[0]);
            var ally = OnField(game, Probe("TS_sc_legal"), 1, areas[0]);
            var covertAlly = OnField(game, Probe("TS_sc_illegal"), 1, areas[0], covert: true);
            var victim = OnField(game, Probe("TS_sc_victim3"), 2, areas[0]);
            PrepareActionWindow(game);
            var ability = FindEffect(wd.PlayerTemplate, "JC094_1_SacrificeAllyDestroyTarget");

            EffectEngine.Instance.ActivateAbility(wd, ability, victim, -1, covertAlly.UID);   // 暗藏者不可作代价

            Assert.IsTrue(covertAlly.IsCovert, "非法候选未被牺牲（暗藏者仍在场且未翻面）");
            Assert.AreEqual(CardZone.AreaZone, ally.CurrentZone, "也未改牺牲其它合法候选（代价未支付）");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "目标未被消灭（整次发动被拒）");
        }

        // ==================== ⑦ 未指定候选 → 确定性兜底（无 UI 路径） ====================

        public void TestSacrifice_未指定时候选兜底()
        {
            var game = NewGame();
            var areas = Areas(game);
            var wd = OnField(game, Load("JC094_weidadetingbabadi"), 1, areas[0]);
            var ally = OnField(game, Probe("TS_sc_first"), 1, areas[0]);
            var victim = OnField(game, Probe("TS_sc_victim4"), 2, areas[0]);
            PrepareActionWindow(game);
            var ability = FindEffect(wd.PlayerTemplate, "JC094_1_SacrificeAllyDestroyTarget");

            EffectEngine.Instance.ActivateAbility(wd, ability, victim);   // 不给 sacrificeUid → 兜底第一个合法候选

            Assert.AreEqual(CardZone.Graveyard, ally.CurrentZone, "兜底取第一个合法候选并牺牲它");
        }
    }
}
