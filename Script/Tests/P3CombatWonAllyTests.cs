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
    /// 【用户 2026-09 口径 A14】"参与了该次战斗对抗"＝**近似「位于战斗地区且属于获胜方」** → JC034 心兽之道③
    /// （"持续：当你赢得一次战斗对抗时，若有**本方吸血鬼角色**参与了这次战斗对抗，你可以令**目标对手**弃一张手牌"）。
    ///  · 新谓词 **`WinnerIsSelfWithAllySubCategoryInCombatArea`**（`Param`＝子类别名，如"吸血鬼"）：
    ///    ① 本卡控制者 ＝ 对抗获胜方（`triggeringPlayerId`）② 战斗地区内存在**同控制者**、带该子类别的**场上角色**；
    ///  · 目标＝**玩家目标**（`Kind=Player` + `Restrictions=Enemy`，K24：入堆叠时锁定）；
    ///  · 弃牌＝`Discard{"1","目标玩家","自选"}`（由该对手自己选）。
    /// </summary>
    public class P3CombatWonAllyTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private sealed class RecordingBus
        {
            public readonly List<GameEvent> Events = new List<GameEvent>();
            public GameEventBus Bus { get; }
            public RecordingBus()
            {
                Bus = new GameEventBus();
                Bus.SetDispatch((e, c) => Events.Add(e));
            }
        }

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 34 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static TriggerEmitPoints WireEmitPoints(GameInitializer game, RecordingBus rec)
        {
            var registry = new TriggerRegistry(rec.Bus);
            rec.Bus.SetDispatch((e, c) =>
            {
                rec.Events.Add(e);
                registry.HandleEvent(e, c);
            });
            var emit = new TriggerEmitPoints(rec.Bus);
            game.TurnManager.SetTriggerSystem(registry, emit, rec.Bus);
            return emit;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static PlayerCardData Probe(string uid, string subCategory = null,
            CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 3,
            };
            if (subCategory != null) tmpl.SubCategories.Add(subCategory);
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

        // ==================== ① 结构 ====================

        public void TestCombatWonAlly_JC034结构()
        {
            var tmpl = Load("JC034_xinshouzhidao");
            var e = FindEffect(tmpl, "JC034_C_CombatWonDiscardIfAllyVampire");
            Assert.IsNotNull(e, "JC034 应有『赢得战斗对抗且本方吸血鬼参与 → 目标对手弃牌』效果");
            Assert.AreEqual(EffectTiming.OnCombatWon, e.Timing, "时机＝战斗对抗获胜");
            Assert.AreEqual("WinnerIsSelfWithAllySubCategoryInCombatArea", e.Condition.Predicate, "条件＝新复合谓词");
            Assert.AreEqual("吸血鬼", e.Condition.Param, "参数＝吸血鬼");
            Assert.IsFalse(e.Forced, "卡文『你可以』→ 非强制");
            Assert.AreEqual(TargetKind.Player, e.Target.Kind, "目标＝玩家");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.Enemy), "目标限定＝对手");
            Assert.AreEqual(ActionVerb.Discard, e.Actions[0].Verb, "动作＝弃牌");
            Assert.AreEqual("目标玩家", e.Actions[0].Args[1], "弃牌者＝目标玩家");
            Assert.AreEqual("自选", e.Actions[0].Args[2], "由该对手自己选择弃哪张");
        }

        // ==================== ② 谓词边界 ====================

        public void TestCombatWonAlly_谓词边界()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();

            // 场景：A 地区（战斗地区）有本方吸血鬼角色；B 地区有本方普通角色
            var lord = OnField(game, Load("JC034_xinshouzhidao"), 1, areas[0]);
            var vampire = OnField(game, Probe("TS_cwa_vampire", "吸血鬼"), 1, areas[0]);
            var plain = OnField(game, Probe("TS_cwa_plain"), 1, areas[1]);

            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("WinnerIsSelfWithAllySubCategoryInCombatArea",
                lord, lord, lord, 1, areas[0], "吸血鬼"), "本方获胜 + 本地区有本方吸血鬼 → 满足");

            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("WinnerIsSelfWithAllySubCategoryInCombatArea",
                lord, lord, lord, 2, areas[0], "吸血鬼"), "敌方获胜 → 不满足");

            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("WinnerIsSelfWithAllySubCategoryInCombatArea",
                lord, lord, lord, 1, areas[1], "吸血鬼"), "打赢的是别的地区（该地区只有普通角色）→ 不满足");

            // 把吸血鬼移出战斗地区 → 不再满足
            game.ZoneManager.MoveToArea(vampire, areas[1], 1);
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("WinnerIsSelfWithAllySubCategoryInCombatArea",
                lord, lord, lord, 1, areas[0], "吸血鬼"), "吸血鬼不在战斗地区 → 不满足");
            Assert.IsTrue(plain.CurrentZone == CardZone.AreaZone, "前置：普通角色仍在场");
        }

        // ==================== ③ 端到端：获胜 → 目标对手弃一张 ====================

        public void TestCombatWonAlly_目标对手弃一张()
        {
            var game = NewGame();
            var rec = new RecordingBus();
            var emit = WireEmitPoints(game, rec);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var areas = game.AreaManager.GetOrderedAreaUids();

            // JC034 是**附属**（结附于本方秘社 → AttachedZone），其效果生效区默认＝结附区
            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1);
            Assert.IsTrue(council != null && council.Count > 0, "前置：P1 有秘社实例");
            var lord = game.CardManager.CreateCardInstance(Load("JC034_xinshouzhidao"), 1);
            lord.IsAttached = true;
            lord.AttachedTo = council[0];
            council[0].AttachedCards.Add(lord);
            game.ZoneManager.MoveToAttachedZone(lord, "", 1);
            OnField(game, Probe("TS_cwa_vampire2", "吸血鬼"), 1, areas[0]);

            // 对手手牌：两张（弃 1 张后剩 1 张）
            var h1 = game.CardManager.CreateCardInstance(Probe("TS_cwa_hand1"), 2);
            var h2 = game.CardManager.CreateCardInstance(Probe("TS_cwa_hand2"), 2);
            game.ZoneManager.MoveToHand(h1, 2);
            game.ZoneManager.MoveToHand(h2, 2);
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 2).Count;

            emit.OnCombatWon(1, areas[0]);   // 生产触点：本方赢下该地区的战斗对抗

            Assert.AreEqual(1, game.StackManager.Entries.Count, "触发已入堆叠（玩家目标在入堆叠时锁定）");
            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(handBefore - 1, game.ZoneManager.GetZoneCards(CardZone.Hand, 2).Count,
                "目标对手弃掉 1 张手牌");
        }
    }
}
