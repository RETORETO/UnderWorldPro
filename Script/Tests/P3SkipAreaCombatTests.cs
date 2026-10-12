using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K18 第一批：地区级"略过对抗" → JC098 陷入疯狂的人群②】
    /// 卡文：持续：本地区略过调查对抗步骤。
    ///
    /// 交付：`StaticKind.SkipAreaCombat`（`StaticEffect.Param` = 对抗名：`调查`/`战斗`/`势力`，可 `,` 组合）
    ///  · 挂在与 `Timing = Continuous` 同一条 `EffectData` 上 → 前提（常驻）满足时 `RegisterPersistent` 登记；
    ///  · 离场（`UnregisterPersistent`）/ 前提丧失（`ReevaluatePrereqs`）/ 卡失效（`RemoveAllContinuousFor`）时注销；
    ///  · **判定按卡当前所在地实时匹配地区**（`EffectEngine.AreaSkipsCombat`）→ 争夺区之间移动自动跟随；
    ///  · 消费点＝`TurnManager.NextCombatStepOfArea`（由 `AdvancePhase` 调用）：被跳过的步骤**在流程里直接不存在**
    ///    （不进入该阶段、不分配行动权、不开让过窗口、不开对抗窗口）——用户 2026-10 口径：
    ///    "跳过调查对抗步骤，不是调查对抗这件事，相当于流程里直接没有这个步骤"。
    /// </summary>
    public class P3SkipAreaCombatTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 337 };
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

        /// <summary>角色探针（可带调查图标 = 白眼/黑眼通道的印刷值）。</summary>
        private static PlayerCardData Probe(string uid, int investigation = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 2, Investigation = investigation,
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

        /// <summary>把当前阶段推进到目标阶段（双方连续让过 → AdvancePhase）；堆叠非空时先结算堆叠。</summary>
        private static void AdvanceTo(GameInitializer game, global::TurnManager.TurnPhase target, int maxPasses = 80)
        {
            for (int i = 0; i < maxPasses && game.TurnManager.CurrentPhase != target; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>订阅"对抗窗口请求"并计数（调查对抗开窗＝该地区本步骤真的结算了对抗）。</summary>
        private sealed class WindowCounter
        {
            public int Investigation;
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
                if (type == CombatManager.CombatType.Investigation) Investigation++;
                onResolved?.Invoke();   // 无 UI：立即关窗，流程继续
            }

            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
        }

        // ==================== ① 结构 ====================

        public void TestSkip_JC098结构()
        {
            var tmpl = Load("JC098_xianrufengkuangderenqun");
            Assert.IsNotNull(tmpl, "JC098 模板应可加载");
            var e = FindEffect(tmpl, "JC098_A_SkipInvestigationStep");
            Assert.IsNotNull(e, "JC098 应有『本地区略过调查对抗步骤』效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果（前提常驻）");
            Assert.IsNotNull(e.StaticEffects, "声明了静止规则");
            Assert.AreEqual(1, e.StaticEffects.Count, "一条静止规则");
            Assert.AreEqual(StaticKind.SkipAreaCombat, e.StaticEffects[0].Kind, "静止种类＝略过对抗");
            Assert.AreEqual("调查", e.StaticEffects[0].Param, "略过的是『调查』对抗步骤");
        }

        // ==================== ② 登记后：本地区略过（且只略过调查） ====================

        public void TestSkip_登记后本地区略过调查()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);

            Assert.IsTrue(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "本卡所在地区 → 调查对抗被略过");
            Assert.IsFalse(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Strength, areas[0]),
                "只略过『调查』：战斗对抗不受影响");
            Assert.IsFalse(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Power, areas[0]),
                "只略过『调查』：势力对抗不受影响");
            Assert.IsFalse(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[1]),
                "其它地区不受影响");
        }

        // ==================== ③ 离场即失效 ====================

        public void TestSkip_离场即失效()
        {
            var game = NewGame();
            var areas = Areas(game);
            var card = OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);
            Assert.IsTrue(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "前置：登记生效");

            game.ZoneManager.MoveToHand(card, 1);   // 离场 → UnregisterPersistent

            Assert.IsFalse(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "卡离场 → 略过登记注销");
        }

        // ==================== ④ 换区跟随（判定按当前所在地，不做地区快照） ====================

        public void TestSkip_换区后跟随新地区()
        {
            var game = NewGame();
            var areas = Areas(game);
            var card = OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);

            game.ZoneManager.MoveToArea(card, areas[1], 1);   // 争夺区 A → 争夺区 B

            Assert.IsFalse(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[0]),
                "原地区不再略过");
            Assert.IsTrue(game.TurnManager.IsAreaCombatSkipped(CombatManager.CombatType.Investigation, areas[1]),
                "新地区开始略过（按卡当前所在地实时判定）");
        }

        // ==================== ⑤ 端到端：调查步骤在流程里直接不存在（对照：不卡则照常结算） ====================

        public void TestSkip_端到端调查步骤直接跳过()
        {
            // 对照组：无 JC098 → 调查步骤照常打开对抗窗口
            var control = NewGame();
            var controlAreas = Areas(control);
            OnField(control, Probe("TS_skip_p1", investigation: 3), 1, controlAreas[0]);
            OnField(control, Probe("TS_skip_p2", investigation: 0), 2, controlAreas[0]);
            var controlCounter = new WindowCounter(control);
            AdvanceTo(control, global::TurnManager.TurnPhase.CombatInvestigation);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatInvestigation, control.TurnManager.CurrentPhase,
                "对照：应推进到调查对抗阶段");
            Assert.AreEqual(1, controlCounter.Investigation, "对照：调查对抗真的结算了（打开过对抗窗口）");
            controlCounter.Detach();

            // 实验组①：JC098 在**1 地区** → 机动轮后**直接进入 1 地区战斗步骤**（调查步骤不进入）
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);
            OnField(game, Probe("TS_skip_p1b", investigation: 3), 1, areas[0]);
            OnField(game, Probe("TS_skip_p2b", investigation: 0), 2, areas[0]);
            var counter = new WindowCounter(game);
            AdvanceTo(game, global::TurnManager.TurnPhase.CombatStrength);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatStrength, game.TurnManager.CurrentPhase,
                "JC098 在 1 地区 ⇒ 机动轮后直接进入 1 地区战斗步骤（流程里没有调查步骤）");
            Assert.AreEqual(1, game.TurnManager.CurrentCombatAreaNo, "仍在 1 地区");
            Assert.AreEqual(0, counter.Investigation, "调查步骤不在流程里 ⇒ 调查对抗窗口从未打开");
            counter.Detach();

            // 实验组②：JC098 在**2 地区** → 1 地区势力步骤后**直接进入 2 地区战斗步骤**（2 地区调查步骤不进入）
            var game2 = NewGame();
            var areas2 = Areas(game2);
            OnField(game2, Load("JC098_xianrufengkuangderenqun"), 1, areas2[1]);
            OnField(game2, Probe("TS_skip_p1c", investigation: 3), 1, areas2[0]);
            OnField(game2, Probe("TS_skip_p2c", investigation: 0), 2, areas2[0]);
            var counter2 = new WindowCounter(game2);
            AdvanceTo(game2, global::TurnManager.TurnPhase.CombatPower);
            Assert.AreEqual(1, game2.TurnManager.CurrentCombatAreaNo, "前置：1 地区势力步骤（1 地区未被略过）");
            Assert.AreEqual(1, counter2.Investigation, "1 地区调查步骤照常结算（略过按地区各自生效）");
            AdvanceTo(game2, global::TurnManager.TurnPhase.CombatStrength);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatStrength, game2.TurnManager.CurrentPhase,
                "JC098 在 2 地区 ⇒ 1 地区势力步骤后直接进入 2 地区战斗步骤");
            Assert.AreEqual(2, game2.TurnManager.CurrentCombatAreaNo, "已推进到 2 地区");
            Assert.AreEqual(1, counter2.Investigation, "2 地区调查步骤同样在流程里不存在（调查窗口数不再增加）");
            counter2.Detach();
        }
    }
}
