using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·K18 第二批：地区级"额外进行一次势力对抗" → JC067 约翰·F·纳特里②】
    /// 卡文：领袖 公开 声望 持续：本地区在对抗阶段时额外进行一次势力对抗。
    ///
    /// 交付：`StaticKind.ExtraAreaPowerContest`（与 `Timing = Continuous` 同一 `EffectData`，前提常驻；
    /// 与 K18 的 `SkipAreaCombat` 同款生命周期与"按卡当前所在地实时匹配地区"判定）；
    ///  · 消费点＝`TurnManager.AdvancePhase` 的 `CombatPower` 分支：当前地区本回合未追加过 →
    ///    **原地重入** `CombatPower`（同地区、同对抗类型再结算一次），跑完再推进地区游标；
    ///  · **每地区每回合至多追加一次**（`_extraPowerDoneAreas` 去重；`Reset`/`StartGame`/`NextTurn` 清空）；
    ///  · 追加的那次是**正常势力对抗**（照常放势力标志；声望等既有规则同办）。
    /// </summary>
    public class P3ExtraPowerContestTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 769 };
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

        private static PlayerCardData Probe(string uid, int power = 1)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 5, Power = power,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>带【声望】的角色探针（用于把声望多数"打平" —— 双方各有 1 个声望角色时声望规则不放置，
        /// 这样势力标志读数就只反映势力对抗本身的产出）。</summary>
        private static PlayerCardData ProbeWithRenown(string uid, int power)
        {
            var tmpl = Probe(uid, power);
            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = uid + "_Renown";
            tmpl.EffectDataList.Add(renown);
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

        /// <summary>双方连续让过 → 推进阶段（与既有套件同款推进方式）。</summary>
        private static void PassTwice(GameInitializer game)
        {
            for (int i = 0; i < 2; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static void AdvanceTo(GameInitializer game, global::TurnManager.TurnPhase target, int maxPasses = 120)
        {
            for (int i = 0; i < maxPasses && game.TurnManager.CurrentPhase != target; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
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

        public void TestExtraPower_JC067结构()
        {
            var tmpl = Load("JC067_yuehanFnateli");
            Assert.IsNotNull(tmpl, "JC067 模板应可加载");
            var e = FindEffect(tmpl, "JC067_D_ExtraPowerContest");
            Assert.IsNotNull(e, "JC067 应有『本地区额外进行一次势力对抗』效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果（前提常驻）");
            Assert.IsNotNull(e.StaticEffects, "声明了静止规则");
            Assert.AreEqual(StaticKind.ExtraAreaPowerContest, e.StaticEffects[0].Kind, "静止种类＝额外势力对抗");
        }

        // ==================== ② 判定：本卡所在地区成立、他区不成立；离场即失效 ====================

        public void TestExtraPower_判定与离场()
        {
            var game = NewGame();
            var areas = Areas(game);
            var john = OnField(game, Load("JC067_yuehanFnateli"), 1, areas[0]);

            Assert.IsTrue(EffectEngine.Instance.AreaHasExtraPowerContest(areas[0]), "本卡所在地区 → 追加势力对抗");
            Assert.IsFalse(EffectEngine.Instance.AreaHasExtraPowerContest(areas[1]), "其它地区 → 不追加");

            game.ZoneManager.MoveToArea(john, areas[1], 1);   // 换区
            Assert.IsFalse(EffectEngine.Instance.AreaHasExtraPowerContest(areas[0]), "原地区不再追加（按当前所在地实时判定）");
            Assert.IsTrue(EffectEngine.Instance.AreaHasExtraPowerContest(areas[1]), "新地区开始追加");

            game.ZoneManager.MoveToHand(john, 1);             // 离场
            Assert.IsFalse(EffectEngine.Instance.AreaHasExtraPowerContest(areas[1]), "离场 → 登记注销");
        }

        // ==================== ③ 端到端：该地区的势力步骤真的多结算一次 ====================
        // 观测点：势力对抗会给**获胜方**按其获胜点数放势力标志（`AreaManager.GetPowerMark`）。
        // 用"P2 获胜、每次差值 1"来读数（P1 侧有 JC067 的【声望】会额外 +1，会干扰读数且叠加到
        // `RequiredPower=4` 会触发"赢取地区"把标记清零）：
        //   对照组（无 JC067）：势力步骤只跑一次 → P2 得 1 个标志；
        //   实验组（有 JC067）：势力步骤跑两次 → P2 得 2 个标志。
        public void TestExtraPower_端到端势力步骤跑两次()
        {
            // 对照组：P1 战力 3、P2 战力 4 → P2 每次获胜差值 1
            var control = NewGame();
            var cAreas = Areas(control);
            OnField(control, Probe("TS_ep_c_p1", power: 3), 1, cAreas[0]);
            OnField(control, Probe("TS_ep_c_p2", power: 4), 2, cAreas[0]);
            Assert.AreEqual(3, AreaStatsCalculator.SumPlayerValue(control.ZoneManager, control.PlayerManager,
                cAreas[0], 1, CombatManager.CombatType.Power), "对照：P1 势力 3");
            Assert.AreEqual(4, AreaStatsCalculator.SumPlayerValue(control.ZoneManager, control.PlayerManager,
                cAreas[0], 2, CombatManager.CombatType.Power), "对照：P2 势力 4");
            AdvanceTo(control, global::TurnManager.TurnPhase.CombatPower);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, control.TurnManager.CurrentPhase,
                "对照：推进到势力步骤");
            int controlMark = control.AreaManager.GetPowerMark(cAreas[0], 2);
            Assert.AreEqual(1, controlMark, "对照：只跑一次势力对抗 → P2 得 1 个势力标志");

            // 实验组：P1 场上就是 JC067（势力 3、自带【声望】），故给 P2 的探针也加【声望】把声望多数打平
            // （否则声望规则会给 P1 放标记、与 P2 新增量互相抵扣，读数变成 0 就测不出追加的那次）。
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC067_yuehanFnateli"), 1, areas[0]);
            OnField(game, ProbeWithRenown("TS_ep_p2", power: 4), 2, areas[0]);
            Assert.AreEqual(3, AreaStatsCalculator.SumPlayerValue(game.ZoneManager, game.PlayerManager,
                areas[0], 1, CombatManager.CombatType.Power), "实验：P1 势力 3（JC067）");
            Assert.AreEqual(4, AreaStatsCalculator.SumPlayerValue(game.ZoneManager, game.PlayerManager,
                areas[0], 2, CombatManager.CombatType.Power), "实验：P2 势力 4");
            AdvanceTo(game, global::TurnManager.TurnPhase.CombatPower);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, game.TurnManager.CurrentPhase,
                "实验：推进到势力步骤");
            int firstMark = game.AreaManager.GetPowerMark(areas[0], 2);
            Assert.AreEqual(controlMark, firstMark, "实验：第一次势力对抗的产出与对照相同");

            // 再让过两轮 → 触发"额外一次势力对抗"并原地重入 CombatPower
            PassTwice(game);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, game.TurnManager.CurrentPhase,
                "追加的势力对抗：原地重入同一阶段（而不是推进地区游标）");
            int extraMark = game.AreaManager.GetPowerMark(areas[0], 2);

            Assert.AreEqual(firstMark * 2, extraMark,
                $"追加的势力对抗确实多结算了一次（第一次 {firstMark} → 追加后 {extraMark} 个势力标志；" +
                $"对照单次 {controlMark}）");
        }

        // ==================== ④ 每地区每回合只追加一次（再让过不再重入） ====================

        public void TestExtraPower_每回合只追加一次()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC067_yuehanFnateli"), 1, areas[0]);
            OnField(game, Probe("TS_ep_once_p1", power: 3), 1, areas[0]);
            OnField(game, Probe("TS_ep_once_p2", power: 1), 2, areas[0]);
            AdvanceTo(game, global::TurnManager.TurnPhase.CombatPower);

            PassTwice(game);   // 第 1 次追加
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, game.TurnManager.CurrentPhase,
                "第 1 次追加：原地重入");
            int afterFirst = game.AreaManager.GetPowerMark(areas[0], 2);

            PassTwice(game);   // 已追加过 → 应推进（进入下一地区的调查步骤或结束阶段）
            Assert.IsFalse(game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.CombatPower,
                "第 2 次让过不再重入势力步骤（每地区每回合只追加一次）");
            Assert.AreEqual(afterFirst, game.AreaManager.GetPowerMark(areas[0], 2),
                "势力标志数不再增长（确认没有多跑第三次）");
        }
    }
}
