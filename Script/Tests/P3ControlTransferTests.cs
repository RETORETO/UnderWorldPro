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
    /// 【P3·控制权转移 + 到期归还（`ActionVerb.GainControl` / `ZoneManager.ChangeController`）→ JC129 操控心智】
    /// 卡文：行动：你获得目标敌方角色的控制权直到回合结束。
    ///
    /// 语义：
    ///  · 只改**控制者**（`ControllerId`），`OwnerId` 不变（与引擎内各处"夺控"口径一致：
    ///    归属/窗口/付费/激活都以控制者为准）；
    ///  · 经 `ZoneManager.ChangeController` 走**同一条**内部夺控出口（版本号 / 光环重算 / 前提取舍 / UI 刷新）；
    ///  · "直到回合结束"＝登记时效项（`TimedMod{Kind="Control", PlayerId=原控制者}`），
    ///    回合结束时由 `ExpireTurnEndEffects` **归还**给原控制者（卡已离场则安全跳过）。
    /// </summary>
    public class P3ControlTransferTests : TestRunner
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

        // ==================== ① 结构 ====================

        public void TestControl_JC129结构()
        {
            var tmpl = Load("JC129_caokongxinzhi");
            Assert.IsNotNull(tmpl, "JC129 模板应可加载");
            var e = FindEffect(tmpl, "JC129_1_GainControlUntilEndOfTurn");
            Assert.IsNotNull(e, "JC129 应有『获得目标敌方角色控制权』效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "事务卡结算时机");
            Assert.AreEqual(PlayTiming.MainPhase, e.PlayTiming, "卡文『行动：』→ 行动阶段打出");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.Enemy), "目标限定**敌方**角色");
            Assert.AreEqual(ActionVerb.GainControl, e.Actions[0].Verb, "动作＝获得控制权");
            Assert.AreEqual("直到回合结束", e.Actions[0].Args[1], "期限＝直到回合结束");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝直到回合结束");
        }

        // ==================== ② 变更控制者：只改 ControllerId，OwnerId 不变 ====================

        public void TestControl_只改控制者不改拥有者()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var enemy = game.CardManager.CreateCardInstance(Probe("TS_ct_enemy"), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);
            Assert.AreEqual(2, enemy.ControllerId, "前置：控制者＝P2");
            Assert.AreEqual(2, enemy.OwnerId, "前置：拥有者＝P2");

            game.ZoneManager.ChangeController(enemy, 1);

            Assert.AreEqual(1, enemy.ControllerId, "控制者已变更为 P1");
            Assert.AreEqual(2, enemy.OwnerId, "**拥有者不变**（夺控不改归属）");
        }

        // ==================== ③ 端到端：事务卡结算 → 获得控制权；回合结束 → 归还 ====================

        public void TestControl_获得控制权并在回合结束归还()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var enemy = game.CardManager.CreateCardInstance(Probe("TS_ct_enemy2"), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);
            var sorcery = game.CardManager.CreateCardInstance(Load("JC129_caokongxinzhi"), 1);

            var e = FindEffect(sorcery.PlayerTemplate, "JC129_1_GainControlUntilEndOfTurn");
            EffectEngine.Instance.ResolveEffect(e, sorcery, enemy, area);

            Assert.AreEqual(1, enemy.ControllerId, "结算后由**施术者 P1** 控制");
            Assert.AreEqual(2, enemy.OwnerId, "拥有者仍是 P2");
            Assert.AreEqual(CardZone.AreaZone, enemy.CurrentZone, "控制权变更不移动卡");

            // 回合结束：归还
            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.AreEqual(2, enemy.ControllerId, "回合结束 → 控制权**归还**原控制者 P2");
        }

        /// <summary>常驻期限（非"直到回合结束"）→ 不登记归还。</summary>
        public void TestControl_常驻期限不归还()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var enemy = game.CardManager.CreateCardInstance(Probe("TS_ct_enemy3"), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);

            var data = new EffectData
            {
                EffectId = "TS_ct_permanent",
                Timing = EffectTiming.OnSorceryCast,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.GainControl, Args = new Array<string> { "目标", "常驻" } },
                },
                Duration = "常驻",
            };
            var src = game.CardManager.CreateCardInstance(Probe("TS_ct_src"), 1);
            EffectEngine.Instance.ResolveEffect(data, src, enemy, area);
            Assert.AreEqual(1, enemy.ControllerId, "已夺取控制权");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.AreEqual(1, enemy.ControllerId, "常驻 → 回合结束**不**归还");
        }

        /// <summary>卡在归还前已离场 → 安全跳过（不报错、不误改）。</summary>
        public void TestControl_卡离场后归还安全跳过()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var enemy = game.CardManager.CreateCardInstance(Probe("TS_ct_enemy4"), 2);
            game.ZoneManager.MoveToArea(enemy, area, 2);
            var sorcery = game.CardManager.CreateCardInstance(Load("JC129_caokongxinzhi"), 1);

            var e = FindEffect(sorcery.PlayerTemplate, "JC129_1_GainControlUntilEndOfTurn");
            EffectEngine.Instance.ResolveEffect(e, sorcery, enemy, area);
            Assert.AreEqual(1, enemy.ControllerId, "前置：已夺取");

            game.CardManager.ResolveDeath(enemy, CardManager.DeathCause.Destroy);
            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);   // 不应抛异常

            Assert.AreEqual(CardZone.Graveyard, enemy.CurrentZone, "卡仍在墓地");
        }

        /// <summary>已是本方控制 → 不重复夺取（幂等提示，避免时效项错记原控制者）。</summary>
        public void TestControl_已是本方控制则跳过()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var own = game.CardManager.CreateCardInstance(Probe("TS_ct_own"), 1);
            game.ZoneManager.MoveToArea(own, area, 1);
            var sorcery = game.CardManager.CreateCardInstance(Load("JC129_caokongxinzhi"), 1);

            var e = FindEffect(sorcery.PlayerTemplate, "JC129_1_GainControlUntilEndOfTurn");
            EffectEngine.Instance.ResolveEffect(e, sorcery, own, area);
            Assert.AreEqual(1, own.ControllerId, "仍由 P1 控制");

            // 结束后不应把它"归还"给 P2
            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.AreEqual(1, own.ControllerId, "不会因跳过而错记归还对象");
        }
    }
}
