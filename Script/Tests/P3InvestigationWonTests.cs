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
    /// 【P3·调查对抗获胜时机（OnInvestigationWon）+ JC072 博学的贤者】
    /// 卡文：公开 触发：当你赢得**其所参与**的调查对抗时，博学的贤者获得声望直到回合结束。
    ///
    /// 交付的机制：
    ///  · 新触发时机 `GameEvent.OnInvestigationWon` → `EffectTiming.OnInvestigationWon`（原先只在注释里"预留"），
    ///    由 `CombatManager` 的**单机与联机**两条调查对抗结算路径在**调查窗口关闭之后**广播
    ///    （与战斗对抗 `OnCombatWon` 同一条顺序纪律）；**平局 / 0:0 无胜者 → 不广播**；
    ///  · 与 `OnCombatWon`（只覆盖战斗对抗）**互不串线**（本套件用"对照探针"证明）；
    ///  · 条件 `WinnerIsSelfInSameArea` ＝ "本卡控制者获胜 ∧ 本卡就在该对抗地区" → 卡文的"**其所参与**"；
    ///  · 效果 `Gain{声望,1,自身}` + `Duration="直到回合结束"`（时效到期自动失去）。
    /// </summary>
    public class P3InvestigationWonTests : TestRunner
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

        /// <summary>探针卡（可选调查/战力值），用于构造对抗胜负。</summary>
        private static PlayerCardData Probe(string uid, int investigation = 0, int strength = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
                Investigation = investigation, Strength = strength,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>对照探针：挂一条"战斗对抗获胜 → 授予黑刀1"的效果（用于证明 OnCombatWon 链路本身工作）。</summary>
        private static PlayerCardData CombatWonProbe(string uid)
        {
            var tmpl = Probe(uid);
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_iw_oncombatwon",
                Timing = EffectTiming.OnCombatWon,
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑刀", "1", "自身" } },
                },
                Duration = "瞬时",
            });
            return tmpl;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>触发排序窗口桩：全部按默认顺序入堆叠。</summary>
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

        /// <summary>对抗窗口桩：立即"关闭窗口"（模拟 View 处理完毕），用于验证"窗口关闭后才广播"。</summary>
        private sealed class InstantCombatWindow
        {
            private readonly GameInitializer _game;
            public int Calls;

            public InstantCombatWindow(GameInitializer game)
            {
                _game = game;
                _game.CombatManager.OnCombatWindowRequired += OnWindow;
            }

            private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
            {
                Calls++;
                onResolved?.Invoke();
            }

            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
        }

        /// <summary>联机网关桩：对调查分配与触发排序都回"全选"。</summary>
        private sealed class CombatGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.CombatInvestigation
                    || req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                }
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        /// <summary>双方连续让过一次 → 结算堆叠顶。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        private static StackEntry FindEntry(GameInitializer game, string effectId)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestInvestigationWon_JC072结构()
        {
            var tmpl = Load("JC072_boxuedexianzhe");
            Assert.IsNotNull(tmpl, "JC072 模板应可加载");
            Assert.IsNotNull(FindEffect(tmpl, "JC072_A_Public"), "应有『公开』关键词");

            var e = FindEffect(tmpl, "JC072_1_RenownOnInvestigationWon");
            Assert.IsNotNull(e, "JC072 应有『赢得调查对抗 → 获得声望』触发");
            Assert.AreEqual(EffectTiming.OnInvestigationWon, e.Timing, "时机＝调查对抗获胜（新时机）");
            Assert.AreEqual("WinnerIsSelfInSameArea", e.Condition.Predicate,
                "条件＝本方获胜 ∧ 本卡在该地区（＝『其所参与』）");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[0].Verb, "动作＝获得");
            Assert.AreEqual("声望", e.Actions[0].Args[0], "获得的是【声望】");
            Assert.AreEqual("自身", e.Actions[0].Args[2], "主体＝自身（该角色获得）");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝直到回合结束");
            Assert.IsFalse(e.Forced, "非强制（卡文『当…时』可由玩家在排序窗放弃）");
        }

        // ==================== ② 端到端：赢得调查对抗 → 获得声望（直到回合结束） ====================

        public void TestInvestigationWon_获胜则获得声望()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            // P1：JC072（调查 2）；P2：调查 0 的探针 → P1 以 2:0 赢下调查对抗
            var sage = game.CardManager.CreateCardInstance(Load("JC072_boxuedexianzhe"), 1);
            game.ZoneManager.MoveToArea(sage, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_iw_foe"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var win = new InstantCombatWindow(game);
            try
            {
                Assert.IsFalse(sage.HasKeyword("声望"), "前置：尚未获得声望");

                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);

                Assert.AreEqual(1, win.Calls, "调查对抗窗口被打开过一次");
                Assert.IsNotNull(FindEntry(game, "JC072_1_RenownOnInvestigationWon"),
                    "获胜后应把『获得声望』触发入堆叠");

                ResolveStackOnce(game);
                Assert.IsTrue(sage.HasKeyword("声望"), "结算后该角色获得【声望】");

                // 时效：回合结束还原
                EffectEngine.Instance.ExpireTurnEndEffects(game.TurnManager.TurnCount);
                Assert.IsFalse(sage.HasKeyword("声望"), "『直到回合结束』→ 时效到期后失去声望");
            }
            finally
            {
                win.Detach();
            }
        }

        // ==================== ③ 条件：必须"其所参与"——输掉对抗 / 不在该地区 / 平局 都不触发 ====================

        public void TestInvestigationWon_输掉对抗则不触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var sage = game.CardManager.CreateCardInstance(Load("JC072_boxuedexianzhe"), 1);
            game.ZoneManager.MoveToArea(sage, area, 1);
            var strong = game.CardManager.CreateCardInstance(Probe("TS_iw_strong", 3), 2);   // P2 赢（2:3）
            game.ZoneManager.MoveToArea(strong, area, 2);

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);

                Assert.IsTrue(game.StackManager.IsEmpty, "本方输掉调查对抗 → 不入堆叠");
                Assert.IsFalse(sage.HasKeyword("声望"), "未获得声望");
            }
            finally
            {
                win.Detach();
            }
        }

        public void TestInvestigationWon_不在对抗地区则不触发()
        {
            var game = NewGame();
            var areas = Areas(game);

            // JC072 在地区1；对抗发生在地区2（P1 在地区2 有调查 3 的探针 → P1 赢）
            var sage = game.CardManager.CreateCardInstance(Load("JC072_boxuedexianzhe"), 1);
            game.ZoneManager.MoveToArea(sage, areas[0], 1);
            var helper = game.CardManager.CreateCardInstance(Probe("TS_iw_helper", 3), 1);
            game.ZoneManager.MoveToArea(helper, areas[1], 1);

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, areas[1], 2, null);

                Assert.IsTrue(game.StackManager.IsEmpty, "获胜地区不含本卡 → 不触发（『其所参与』不成立）");
                Assert.IsFalse(sage.HasKeyword("声望"), "未获得声望");
            }
            finally
            {
                win.Detach();
            }
        }

        /// <summary>平局且先手不可破平 → 无胜者 → 不广播、不触发。</summary>
        public void TestInvestigationWon_平局不触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var sage = game.CardManager.CreateCardInstance(Load("JC072_boxuedexianzhe"), 1);
            game.ZoneManager.MoveToArea(sage, area, 1);
            var tie = game.CardManager.CreateCardInstance(Probe("TS_iw_tie", 2), 2);   // P1=2, P2=2
            game.ZoneManager.MoveToArea(tie, area, 2);
            game.PlayerManager.ConsumeTieBreak(game.TurnManager.FirstPlayerId);          // 先手不可破平

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var win = new InstantCombatWindow(game);
            try
            {
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);

                Assert.IsTrue(game.StackManager.IsEmpty, "平局无胜者 → 不触发");
                Assert.IsFalse(sage.HasKeyword("声望"), "未获得声望");
            }
            finally
            {
                win.Detach();
            }
        }

        // ==================== ④ 两种对抗互不串线（含对照探针） ====================

        /// <summary>
        /// 战斗对抗获胜**只**触发 OnCombatWon 类效果，不会触发调查类；同一场上放两条探针，
        /// 对照探针（OnCombatWon）必须被收集 → 证明本次确实广播了事件，只是时机不同。
        /// </summary>
        public void TestInvestigationWon_战斗对抗获胜不触发调查时机()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            // P1 战力 3 的探针 + 对照探针；P2 战力 0 → P1 赢下战斗对抗
            var fighter = game.CardManager.CreateCardInstance(Probe("TS_iw_fighter", 0, 3), 1);
            game.ZoneManager.MoveToArea(fighter, area, 1);
            var control = game.CardManager.CreateCardInstance(CombatWonProbe("TS_iw_control"), 1);
            game.ZoneManager.MoveToArea(control, area, 1);
            var sage = game.CardManager.CreateCardInstance(Load("JC072_boxuedexianzhe"), 1);
            // JC072 调查 2 / 战力 0：放在同地区会改变调查对抗值，但对战力无影响
            game.ZoneManager.MoveToArea(sage, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_iw_foe2"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var win = new InstantCombatWindow(game);
            try
            {
                // 战斗对抗：P1 战力 3 vs P2 战力 0 → P1 获胜，伤害分配窗口由桩立即关闭
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);

                Assert.IsNotNull(FindEntry(game, "TS_iw_oncombatwon"),
                    "对照：战斗对抗获胜确实广播 → OnCombatWon 类效果被收集");
                Assert.IsNull(FindEntry(game, "JC072_1_RenownOnInvestigationWon"),
                    "战斗对抗获胜**不**触发调查对抗类效果");
                ResolveStackOnce(game);
                Assert.IsFalse(sage.HasKeyword("声望"), "未获得声望");
            }
            finally
            {
                win.Detach();
            }
        }

        // ==================== ⑤ 联机路径同样广播（ResolveAreaCombatAsync） ====================

        public void TestInvestigationWon_联机路径也触发()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var sage = game.CardManager.CreateCardInstance(Load("JC072_boxuedexianzhe"), 1);
            game.ZoneManager.MoveToArea(sage, area, 1);
            var foe = game.CardManager.CreateCardInstance(Probe("TS_iw_foe3"), 2);
            game.ZoneManager.MoveToArea(foe, area, 2);

            var gw = new CombatGateway();
            game.SetInputGateway(gw);
            // 对抗窗口的网络化路径由**对局宿主**注入（生产：MatchHost 装配；测试在此对齐同一装配）
            game.CombatManager.InputGateway = gw;

            game.CombatManager.ResolveAreaCombatAsync(CombatManager.CombatType.Investigation, area, 1)
                .GetAwaiter().GetResult();

            bool sawInvestigation = false;
            foreach (var r in gw.Requests)
                if (r.RequestKind == Script.Net.ChoiceRequest.Kind.CombatInvestigation) sawInvestigation = true;
            Assert.IsTrue(sawInvestigation, "联机：应下发调查分配请求");
            Assert.IsNotNull(FindEntry(game, "JC072_1_RenownOnInvestigationWon"),
                "联机路径在分配完成后同样广播（触发已入堆叠）");
        }
    }
}
