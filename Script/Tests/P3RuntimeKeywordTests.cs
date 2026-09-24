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
    /// 【P3·K13：运行期授予的**触发类关键词**参与触发收集 + `Gain` 群体主体】
    ///  → JC024 S．P．T董事局①（进场：所有其他本方角色获得【机动】直到回合结束）
    ///  → JC089 毒血诅咒（宿主 -1 防御 + 白刀/黑刀 + 【威名】）
    ///
    /// 机制：
    ///  · `KeywordEffects.RuntimeTriggerTemplates`：关键词 → 触发形态模板（机动→`ManeuverTrigger`、威名→`OnCombatWon`）；
    ///    `TriggerRegistry` 收集阶段对本卡**运行期授予**的这类关键词合成触发（卡面固有者跳过，避免重复）；
    ///  · `Gain` 的群体主体 `Args[2]="所有本方角色"/"所有其他本方角色"`：对全场本方角色逐个授予（逐个登记时效项）；
    ///  · `IsGrantPresent` **符号感知**：负值授予（-1 防御）不会被自愈路径反复补授。
    /// </summary>
    public class P3RuntimeKeywordTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
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

        private static PlayerCardData Probe(string uid, int strength = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 2, Strength = strength,
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

        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? Areas(game)[0], attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        private sealed class InstantCombatWindow
        {
            private readonly GameInitializer _game;
            public InstantCombatWindow(GameInitializer game)
            {
                _game = game;
                _game.CombatManager.OnCombatWindowRequired += OnWindow;
            }
            private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
                => onResolved?.Invoke();
            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static StackEntry FindEntryPrefix(GameInitializer game, string prefix)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId != null && e.EffectDataEffect.EffectId.StartsWith(prefix)) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestRuntimeKeyword_JC024结构()
        {
            var tmpl = Load("JC024_SPTdongshiju");
            var e = FindEffect(tmpl, "JC024_1_ManeuverToOtherAllies");
            Assert.IsNotNull(e, "JC024 应有『所有其他本方角色获得机动』进场触发");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[0].Verb, "动作＝获得（授予）");
            Assert.AreEqual("机动", e.Actions[0].Args[0], "授予【机动】");
            Assert.AreEqual("所有其他本方角色", e.Actions[0].Args[2], "群体主体＝所有其他本方角色");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝直到回合结束");
        }

        public void TestRuntimeKeyword_JC089结构()
        {
            var tmpl = Load("JC089_duxuezuzhou");
            Assert.IsNotNull(tmpl, "JC089 模板应可加载");
            var e = FindEffect(tmpl, "JC089_A_CursedHostGrants");
            Assert.IsNotNull(e, "JC089 应有『宿主获得减益与威名』持续效果");
            Assert.IsTrue(e.GrantToHost, "授予落点＝结附宿主");
            Assert.AreEqual(4, e.ContinuousGrants.Count, "四条授予");
            Assert.AreEqual(-1, e.ContinuousGrants[0].Amount, "-1 防御（负值授予）");
            Assert.AreEqual("防御", e.ContinuousGrants[0].Value, "授予对象＝防御");
            Assert.IsTrue(e.ContinuousGrants[2].Value == "黑刀" || e.ContinuousGrants[2].Value == "白刀", "含图标授予");
            Assert.AreEqual("威名", e.ContinuousGrants[3].Value, "含【威名】关键字授予");
        }

        // ==================== ② 群体授予：机动授予"所有其他本方角色" ====================

        public void TestRuntimeKeyword_群体授予机动()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var spt = game.CardManager.CreateCardInstance(Load("JC024_SPTdongshiju"), 1);
            game.ZoneManager.MoveToArea(spt, area, 1);
            var ally1 = game.CardManager.CreateCardInstance(Probe("TS_rk_ally1"), 1);
            game.ZoneManager.MoveToArea(ally1, area, 1);
            var ally2 = game.CardManager.CreateCardInstance(Probe("TS_rk_ally2"), 1);
            game.ZoneManager.MoveToArea(ally2, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_rk_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            var e = FindEffect(spt.PlayerTemplate, "JC024_1_ManeuverToOtherAllies");
            EffectEngine.Instance.ResolveEffect(e, spt, null, area);

            Assert.IsTrue(ally1.HasKeyword("机动"), "本方角色 1 获得【机动】");
            Assert.IsTrue(ally2.HasKeyword("机动"), "本方角色 2 获得【机动】");
            Assert.IsFalse(spt.HasKeyword("机动"), "**『其他』**→ 自身不获得");
            Assert.IsFalse(foe.HasKeyword("机动"), "对手角色不获得");

            EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
            Assert.IsFalse(ally1.HasKeyword("机动"), "回合结束 → 授予撤销（时效项逐卡登记）");
            Assert.IsFalse(ally2.HasKeyword("机动"), "回合结束 → 授予撤销");
        }

        // ==================== ③ 运行期授予的【机动】参与触发收集 ====================

        /// <summary>被授予【机动】的角色在"对抗前优先权窗口"被收集为机动触发（合成 EffectData）。</summary>
        public void TestRuntimeKeyword_授予的机动参与触发收集()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var unit = game.CardManager.CreateCardInstance(Probe("TS_rk_mv"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_rk_mv_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            unit.Grant("Keyword", "机动", 1);          // 运行期授予
            Assert.IsTrue(unit.HasKeyword("机动"), "前置：已授予机动");

            AdvanceToPhase(game, global::TurnManager.TurnPhase.PreCombat);

            var entry = FindEntryPrefix(game, "Keyword_机动");
            Assert.IsNotNull(entry, "运行期授予的【机动】应被收集为触发并入堆叠");
            Assert.AreEqual(unit.UID, entry.SourceCardUid, "来源＝被授予的角色");
        }

        /// <summary>未授予【机动】的角色不会被收集（对照）。</summary>
        public void TestRuntimeKeyword_未授予则不收集()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var unit = game.CardManager.CreateCardInstance(Probe("TS_rk_nomv"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_rk_nomv_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            AdvanceToPhase(game, global::TurnManager.TurnPhase.PreCombat);

            Assert.IsNull(FindEntryPrefix(game, "Keyword_机动"), "未授予 → 不收集");
        }

        // ==================== ④ JC089：宿主减益 + 运行期授予的【威名】真的会触发 ====================

        public void TestRuntimeKeyword_JC089宿主减益与威名触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var host = game.CardManager.CreateCardInstance(Probe("TS_rk_host", 3), 1);   // 战力 3 → 赢战斗对抗
            game.ZoneManager.MoveToArea(host, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_rk_host_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            int defBefore = host.CurrentDefense;
            var curse = game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1);
            Attach(game, host, curse);

            Assert.AreEqual(defBefore - 1, host.CurrentDefense, "宿主 -1 防御");
            Assert.IsTrue(host.HasIcon("白刀"), "宿主获得［白刀］");
            Assert.IsTrue(host.HasIcon("黑刀"), "宿主获得［黑刀］");
            Assert.IsTrue(host.HasKeyword("威名"), "宿主获得【威名】关键字（运行期授予）");

            // 反复重算光环（自愈路径）：负值授予不应被反复补授导致防御继续下降
            EffectEngine.Instance.ReevaluateAuras(1);
            EffectEngine.Instance.ReevaluateAuras(1);
            Assert.AreEqual(defBefore - 1, host.CurrentDefense, "多次重算后防御仍为 -1（符号感知的已授予判定）");

            int marksBefore = game.AreaManager.GetPowerMark(area, 1);
            var win = new InstantCombatWindow(game);
            try
            {
                // 战斗对抗：P1 战力 3 vs P2 战力 0 → P1 获胜 → 运行期授予的【威名】应触发
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);

                var entry = FindEntryPrefix(game, "Keyword_威名");
                Assert.IsNotNull(entry, "**运行期授予的【威名】被收集为触发**（战斗对抗获胜）");
                ResolveStackOnce(game);
                Assert.AreEqual(marksBefore + 1, game.AreaManager.GetPowerMark(area, 1),
                    "结算后在本地区放置了 1 个本方势力标志");
            }
            finally
            {
                win.Detach();
            }
        }

        /// <summary>附属离场 → 授予逐条撤销（防御回退、威名与图标移除）。</summary>
        public void TestRuntimeKeyword_JC089离场撤销()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var host = game.CardManager.CreateCardInstance(Probe("TS_rk_host2"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            int defBefore = host.CurrentDefense;

            var curse = game.CardManager.CreateCardInstance(Load("JC089_duxuezuzhou"), 1);
            Attach(game, host, curse);
            Assert.AreEqual(defBefore - 1, host.CurrentDefense, "前置：-1 防御");

            EffectEngine.Instance.UnregisterPersistent(curse);

            Assert.AreEqual(defBefore, host.CurrentDefense, "附属注销 → 防御回退");
            Assert.IsFalse(host.HasKeyword("威名"), "【威名】被撤销");
            Assert.IsFalse(host.HasIcon("黑刀"), "［黑刀］被撤销");
        }

        private static void AdvanceToPhase(GameInitializer game, global::TurnManager.TurnPhase phase)
        {
            for (int i = 0; i < 40 && game.TurnManager.CurrentPhase != phase; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }
    }
}
