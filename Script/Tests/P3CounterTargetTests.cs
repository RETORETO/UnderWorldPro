using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K21：目标须带指定"卡上标志" + "移动到目标所在地区" → JC069 警犬②（快速行动2）】
    /// 卡文：快速行动2：将警犬移动到目标具有锁定标志的角色或暗藏者所在的地区，警犬获得［黑眼］直到回合结束。
    ///      该能力每回合只能发动一次。
    ///
    /// 交付：
    ///  · 新限制 **`TargetRestriction.HasCounter`**（`Param`＝标志名，判定 `卡.Counters[标志名] ≥ 1`；
    ///    Param 空 → fail-closed 不通过）；判定挂在 `TargetValidity.CheckRestrictions`（取目标与结算复检**同一真相源**）；
    ///  · `ActionVerb.Move` 新**目的地写法** `Args[1]`：`"目标所在地区"` / `"来源所在地区"`（缺省＝`targetAreaId` 机动语义），
    ///    按目标卡**当前**所在地实时解析；
    ///  · 时效授予 `Gain{"黑眼",1}` + `Duration="直到回合结束"`；`Cost=2` / `ActivateWindow="快速"` / `MaxUsesPerTurn=1`。
    /// </summary>
    public class P3CounterTargetTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 577 };
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

        private static PlayerCardData Probe(string uid, int defense = 3)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = defense,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area, bool covert = false)
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

        public void TestCounterTarget_JC069结构()
        {
            var tmpl = Load("JC069_jingquan");
            Assert.IsNotNull(tmpl, "JC069 模板应可加载");
            var e = FindEffect(tmpl, "JC069_2_MoveToLockedThenGainDarkEye");
            Assert.IsNotNull(e, "JC069 应有『快速行动2』能力");
            Assert.AreEqual(EffectTiming.CharacterActivated, e.Timing, "角色激活能力");
            Assert.AreEqual("快速", e.ActivateWindow, "快速行动");
            Assert.AreEqual(2, e.Cost, "卡文『行动2』");
            Assert.AreEqual(1, e.MaxUsesPerTurn, "每回合只能发动一次");
            Assert.AreEqual(TargetRestriction.HasCounter, e.Target.Restrictions[0], "目标须带锁定标志");
            Assert.AreEqual("锁定标志", e.Target.Param, "筛选的标志名＝锁定标志");
            Assert.IsTrue(e.Target.IncludeCovert, "目标可为暗藏者");
            Assert.AreEqual(ActionVerb.Move, e.Actions[0].Verb, "第一条动作＝移动");
            Assert.AreEqual("目标所在地区", e.Actions[0].Args[1], "目的地＝目标所在地区");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[1].Verb, "第二条动作＝获得黑眼");
            Assert.AreEqual("黑眼", e.Actions[1].Args[0], "获得的是黑眼");
            Assert.AreEqual("直到回合结束", e.Duration, "黑眼只到回合结束");
        }

        // ==================== ② 候选过滤：只有带标志的卡才是合法目标 ====================

        public void TestCounterTarget_只有带标志者是合法目标()
        {
            var game = NewGame();
            var areas = Areas(game);
            var dog = OnField(game, Load("JC069_jingquan"), 1, areas[0]);
            var locked = OnField(game, Probe("TS_ct_locked"), 2, areas[0]);
            var lockedCovert = OnField(game, Probe("TS_ct_locked_covert"), 2, areas[0], covert: true);
            var plain = OnField(game, Probe("TS_ct_plain"), 2, areas[0]);
            var ability = FindEffect(dog.PlayerTemplate, "JC069_2_MoveToLockedThenGainDarkEye");

            game.CardManager.AddCounter(locked, "锁定标志");
            game.CardManager.AddCounter(lockedCovert, "锁定标志");

            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(dog, ability, locked, null), "带锁定标志的角色合法");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(dog, ability, lockedCovert, null), "带锁定标志的暗藏者合法");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(dog, ability, plain, null), "无锁定标志者不合法");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(dog, ability, dog, null), "自身无标志 → 不合法");
        }

        // ==================== ③ 无合法目标 → 该能力不可发动 ====================

        public void TestCounterTarget_无标志者时不可发动()
        {
            var game = NewGame();
            var areas = Areas(game);
            var dog = OnField(game, Load("JC069_jingquan"), 1, areas[0]);
            OnField(game, Probe("TS_ct_none"), 2, areas[0]);
            var ability = FindEffect(dog.PlayerTemplate, "JC069_2_MoveToLockedThenGainDarkEye");

            Assert.IsFalse(ActivateEligibility.HasLegalTarget(dog, ability, game.CardManager, game.PlayerManager),
                "场上没有带锁定标志者 → 无合法目标 → 不可发动");
        }

        // ==================== ④ 端到端：警犬移动到目标所在地区并获得黑眼 ====================

        public void TestCounterTarget_移动到目标所在地区并获黑眼()
        {
            var game = NewGame();
            var areas = Areas(game);
            var dog = OnField(game, Load("JC069_jingquan"), 1, areas[0]);
            var locked = OnField(game, Probe("TS_ct_move"), 2, areas[1]);
            game.CardManager.AddCounter(locked, "锁定标志");
            var ability = FindEffect(dog.PlayerTemplate, "JC069_2_MoveToLockedThenGainDarkEye");

            Assert.AreEqual(areas[0], dog.ZoneDetailId, "前置：警犬在地区1");

            EffectEngine.Instance.ResolveEffect(ability, dog, locked);

            Assert.AreEqual(areas[1], dog.ZoneDetailId, "结算后：警犬移动到目标所在地区（地区2）");
            Assert.AreEqual(1, dog.GetAttribute("调查"), "获得黑眼×1（＝调查 +1）");
        }

        // ==================== ⑤ 黑眼是时效项：回合结束撤销 ====================

        public void TestCounterTarget_黑眼回合结束撤销()
        {
            var game = NewGame();
            var areas = Areas(game);
            var dog = OnField(game, Load("JC069_jingquan"), 1, areas[0]);
            var locked = OnField(game, Probe("TS_ct_time"), 2, areas[1]);
            game.CardManager.AddCounter(locked, "锁定标志");
            var ability = FindEffect(dog.PlayerTemplate, "JC069_2_MoveToLockedThenGainDarkEye");

            EffectEngine.Instance.ResolveEffect(ability, dog, locked);
            Assert.AreEqual(1, dog.GetAttribute("调查"), "前置：黑眼生效");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.AreEqual(0, dog.GetAttribute("调查"), "回合结束 → 黑眼失效（时效项）");
        }

        // ==================== ⑥ 标志被移除后不再是合法目标（同一真相源复检） ====================

        public void TestCounterTarget_标志移除后不再合法()
        {
            var game = NewGame();
            var areas = Areas(game);
            var dog = OnField(game, Load("JC069_jingquan"), 1, areas[0]);
            var locked = OnField(game, Probe("TS_ct_remove"), 2, areas[0]);
            var ability = FindEffect(dog.PlayerTemplate, "JC069_2_MoveToLockedThenGainDarkEye");

            game.CardManager.AddCounter(locked, "锁定标志");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(dog, ability, locked, null), "前置：有标志 → 合法");

            game.CardManager.RemoveCounter(locked, "锁定标志");

            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(dog, ability, locked, null),
                "标志被移除 → 不再是合法目标（候选与结算复检共用同一判定）");
        }

        // ==================== ⑦ 缺 Param（标志名）时 fail-closed ====================

        public void TestCounterTarget_缺标志名时不通过()
        {
            var game = NewGame();
            var areas = Areas(game);
            var victim = OnField(game, Probe("TS_ct_noparam"), 2, areas[0]);
            game.CardManager.AddCounter(victim, "锁定标志");

            bool ok = TargetValidity.CheckRestrictions(
                new Godot.Collections.Array<TargetRestriction> { TargetRestriction.HasCounter },
                "",   // Param 缺失
                victim, 1, game.PlayerManager);

            Assert.IsFalse(ok, "HasCounter 未给出标志名 → fail-closed 不通过（防拼错静默放行）");
        }
    }
}
