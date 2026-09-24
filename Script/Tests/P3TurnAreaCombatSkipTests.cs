using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K18 第二批·**本回合型**地区级"略过对抗" → JC118 停战协议】
    /// 卡文：只能在行动阶段打出。快速行动：选择目标地区，本回合中该地区略过对抗阶段。
    /// 口径（用户 2026-09）：**三步骤（调查/战斗/势力）全部略过**。
    ///
    /// 交付：`ActionVerb.SkipAreaCombat`（Args[0]＝对抗名清单，缺省＝三步全略过）→
    /// `EffectEngine.RegisterAreaCombatSkipThisTurn(地区, 清单)` 登记 `TimedMod{Kind="AreaCombatSkip"}`
    /// （**TargetUid 存地区 UID**、回合结束随时效栈出栈）；查询 `AreaSkipsCombat` 现为
    /// "常驻静止规则（JC098 类）∪ 本回合时效登记（JC118）"并联；消费点仍是
    /// `TurnManager.ResolveCurrentAreaCombat`（不结算对抗、仍给让过窗口）。
    /// </summary>
    public class P3TurnAreaCombatSkipTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 929 };
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

        private static PlayerCardData Probe(string uid, int investigation = 0, int power = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5,
                Investigation = investigation, Power = power,
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

        private static void AdvanceTo(GameInitializer game, global::TurnManager.TurnPhase target, int maxPasses = 120)
        {
            for (int i = 0; i < maxPasses && game.TurnManager.CurrentPhase != target; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>订阅"对抗窗口请求"并计数（开窗＝该步骤真的结算了对抗）。</summary>
        private sealed class WindowCounter
        {
            public int Total;
            private readonly GameInitializer _game;

            public WindowCounter(GameInitializer game)
            {
                _game = game;
                _game.CombatManager.OnCombatWindowRequired += OnWindow;
            }

            private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
            {
                Total++;
                onResolved?.Invoke();   // 无 UI：立即关窗，流程继续
            }

            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
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

        public void TestTurnSkip_JC118结构()
        {
            var tmpl = Load("JC118_tingzhanxieyi");
            Assert.IsNotNull(tmpl, "JC118 模板应可加载");
            var e = FindEffect(tmpl, "JC118_1_SkipAllCombatsInAreaThisTurn");
            Assert.IsNotNull(e, "JC118 应有『本回合该地区略过对抗阶段』效果");
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "事务卡打出即结算");
            Assert.AreEqual(TargetKind.Area, e.Target.Kind, "打出目标＝地区");
            Assert.IsTrue(e.Target.NeedsSelection, "需要选择目标地区");
            Assert.AreEqual(ActionVerb.SkipAreaCombat, e.Actions[0].Verb, "动词＝略过对抗");
            Assert.AreEqual("调查,战斗,势力", e.Actions[0].Args[0], "三步骤全部略过（用户口径）");
            Assert.IsTrue(tmpl.LimitToActionPhaseOnly, "前缀『只能在行动阶段打出』");
        }

        // ==================== ② 登记后：该地区三步骤全部略过，其它地区不受影响 ====================

        public void TestTurnSkip_登记后三步骤全略过()
        {
            var game = NewGame();
            var areas = Areas(game);

            EffectEngine.Instance.RegisterAreaCombatSkipThisTurn(areas[0], null);   // 空清单 → 三步全略过

            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "调查"), "调查步骤被略过");
            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "战斗"), "战斗步骤被略过");
            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "势力"), "势力步骤被略过");
            Assert.IsFalse(EffectEngine.Instance.AreaSkipsCombat(areas[1], "调查"), "其它地区不受影响");
        }

        // ==================== ③ 只略过指定对抗（清单写法） ====================

        public void TestTurnSkip_清单式只略过指定对抗()
        {
            var game = NewGame();
            var areas = Areas(game);

            EffectEngine.Instance.RegisterAreaCombatSkipThisTurn(areas[0], new List<string> { "调查" });

            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "调查"), "调查被略过");
            Assert.IsFalse(EffectEngine.Instance.AreaSkipsCombat(areas[0], "战斗"), "战斗不受影响（清单式）");
            Assert.IsFalse(EffectEngine.Instance.AreaSkipsCombat(areas[0], "势力"), "势力不受影响（清单式）");
        }

        // ==================== ④ 回合结束自动失效（"本回合中"） ====================

        public void TestTurnSkip_回合结束失效()
        {
            var game = NewGame();
            var areas = Areas(game);
            EffectEngine.Instance.RegisterAreaCombatSkipThisTurn(areas[0], null);
            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "调查"), "前置：本回合内生效");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);

            Assert.IsFalse(EffectEngine.Instance.AreaSkipsCombat(areas[0], "调查"), "回合结束 → 略过失效（下回合照常结算对抗）");
        }

        // ==================== ⑤ 端到端：打出后该地区三步骤都不结算（对照正常结算） ====================

        public void TestTurnSkip_端到端三步骤都不结算()
        {
            // 对照组：不打出停战协议 → 调查步骤会开对抗窗口
            var control = NewGame();
            var cAreas = Areas(control);
            OnField(control, Probe("TS_ts_c1", investigation: 3), 1, cAreas[0]);
            OnField(control, Probe("TS_ts_c2", investigation: 0), 2, cAreas[0]);
            var cCounter = new WindowCounter(control);
            AdvanceTo(control, global::TurnManager.TurnPhase.CombatInvestigation);
            Assert.AreEqual(1, cCounter.Total, "对照：调查步骤真的开了对抗窗口");
            cCounter.Detach();

            // 实验组：先结算一次 JC118（对目标地区登记本回合略过），再推进到对抗阶段
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Probe("TS_ts_p1", investigation: 3, power: 3), 1, areas[0]);
            OnField(game, Probe("TS_ts_p2", investigation: 0, power: 1), 2, areas[0]);
            var tmpl = Load("JC118_tingzhanxieyi");
            var spell = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToHand(spell, 1);
            EffectEngine.Instance.ResolveEffect(FindEffect(tmpl, "JC118_1_SkipAllCombatsInAreaThisTurn"),
                spell, null, areas[0]);
            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "战斗"), "前置：登记生效");

            var counter = new WindowCounter(game);
            AdvanceTo(game, global::TurnManager.TurnPhase.CombatInvestigation);
            Assert.AreEqual(0, counter.Total, "该地区调查步骤被略过（不开对抗窗口）");

            // 跳过调查后仍能推进到战斗/势力步骤，且这两步同样不结算
            AdvanceTo(game, global::TurnManager.TurnPhase.CombatStrength);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatStrength, game.TurnManager.CurrentPhase,
                "仍正常推进到战斗步骤（略过≠卡死）");
            Assert.AreEqual(0, counter.Total, "战斗步骤同样被略过");
            counter.Detach();
        }
    }
}
