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
    /// 【P3·K5】JC048 诅咒沙漏（附属，结附于目标地区）：
    ///  · 准备步骤**强制**触发：① `PlaceCounter{时间标志,1,自身}` → 该地区**每有一个**时间标志，
    ///    就对位于该地区的**所有角色**造成 1 点伤害（群体 `MassMode.AllInArea` + `DealDamage{"$X"}`，
    ///    `ValueExpr = "计数:地区内卡上标志:时间标志:本地区"` —— **本批新增的取值写法**）；
    ///    ② 若该地区时间标志 **≥3** → **牺牲自身**（`AreaMarkerAtLeast`，地区回落到来源卡所在地区）。
    /// </summary>
    public class P3HourglassTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 48 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character, int defense = 6)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = defense,
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

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        /// <summary>把诅咒沙漏结附到地区上（附属区、ZoneDetailId＝该地区）。</summary>
        private static CardInstance AttachToArea(GameInitializer game, string areaUid, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(Load("JC048_zuzhoushalou"), playerId);
            game.ZoneManager.MoveToAttachedZone(card, areaUid, playerId);
            return card;
        }

        // ==================== ① 结构 ====================

        public void TestHourglass_结构()
        {
            var tmpl = Load("JC048_zuzhoushalou");
            var a = FindEffect(tmpl, "JC048_A_PlaceTimeMarkerOnSelf");
            Assert.IsNotNull(a, "JC048 应有『准备步骤：放标志』效果");
            Assert.AreEqual(EffectTiming.OnTurnStarted, a.Timing, "时机＝准备步骤");
            Assert.IsTrue(a.Forced, "卡文『强制触发』");
            Assert.AreEqual(ActionVerb.PlaceCounter, a.Actions[0].Verb, "动作＝放标志");
            Assert.AreEqual("时间标志", a.Actions[0].Args[0], "标志名");
            Assert.AreEqual("自身", a.Actions[0].Args[2], "放在诅咒沙漏自身");

            var b0 = FindEffect(tmpl, "JC048_B_AreaDamagePerMarker");
            Assert.IsNotNull(b0, "JC048 应有『按地区标志数群体伤害』效果");
            Assert.AreEqual("计数:地区内卡上标志:时间标志:本地区", b0.ValueExpr, "X＝该地区内所有卡上的时间标志之和");
            Assert.AreEqual(MassMode.AllInArea, b0.Target.MassMode, "群体＝该地区内所有角色");
            Assert.IsTrue(b0.Target.CanTargetSelf, "不分敌我");
            Assert.AreEqual(ActionVerb.DealDamage, b0.Actions[0].Verb, "动作＝造成伤害");
            Assert.AreEqual("$X", b0.Actions[0].Args[0], "伤害量＝动态值 X");

            var b = FindEffect(tmpl, "JC048_C_SacrificeSelfIfThreeMarkers");
            Assert.IsNotNull(b, "JC048 应有『若 ≥3 个时间标志则牺牲自身』效果");
            Assert.AreEqual("AreaMarkerAtLeast", b.Condition.Predicate, "条件＝地区标志数");
            Assert.AreEqual("时间标志,3", b.Condition.Param, "参数＝时间标志,3");
            Assert.AreEqual(ActionVerb.Sacrifice, b.Actions[0].Verb, "动作＝牺牲自身");
        }

        // ==================== ② 新取值写法：地区内卡上标志之和 ====================

        public void TestHourglass_地区内卡上标志计数()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            var hourglass = AttachToArea(game, areas[0], 1);
            var c1 = OnField(game, Probe("TS_hg_c1"), 1, areas[0]);
            var c2 = OnField(game, Probe("TS_hg_c2"), 2, areas[0]);
            var other = OnField(game, Probe("TS_hg_other"), 1, areas[1]);
            game.CardManager.AddCounter(hourglass, "时间标志", 1);
            game.CardManager.AddCounter(c1, "时间标志", 2);
            game.CardManager.AddCounter(c2, "时间标志", 1);
            game.CardManager.AddCounter(other, "时间标志", 5);   // 别的地区：不计入

            var e = FindEffect(hourglass.PlayerTemplate, "JC048_B_AreaDamagePerMarker");
            var st = EffectEngine.Instance.EvaluateDynamicValue(e, hourglass, null, "", -1, out int x, out string reason);
            Assert.IsTrue(st == EffectEngine.DynamicValueState.Ok, $"动态值应可求值（{st} / {reason}）");
            Assert.AreEqual(4, x, $"本地区合计 1+2+1＝4（{reason}）");
        }

        // ==================== ③ 端到端：结算 → 放标志 + 群体伤害 ====================

        public void TestHourglass_放标志并群体伤害()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var hourglass = AttachToArea(game, area, 1);
            var ally = OnField(game, Probe("TS_hg_ally"), 1, area);
            var enemy = OnField(game, Probe("TS_hg_enemy"), 2, area);
            game.CardManager.AddCounter(ally, "时间标志", 1);   // 结算前该地区已有 1 个 → 放完共 2 个 → 各受 2 点伤害

            // 准备步骤的两条效果按声明顺序结算：先放标志、再按（含新放的）标志数群体伤害
            var place = FindEffect(hourglass.PlayerTemplate, "JC048_A_PlaceTimeMarkerOnSelf");
            EffectEngine.Instance.ResolveEffect(place, hourglass, null, area);
            Assert.IsTrue(hourglass.Counters.TryGetValue("时间标志", out int n) && n == 1,
                "先在诅咒沙漏上放了 1 个时间标志");

            var dmg = FindEffect(hourglass.PlayerTemplate, "JC048_B_AreaDamagePerMarker");
            EffectEngine.Instance.ResolveEffect(dmg, hourglass, null, area);

            Assert.AreEqual(2, ally.Damage, "本方角色受到 2 点伤害（该地区共 2 个时间标志）");
            Assert.AreEqual(2, enemy.Damage, "敌方角色同样受到 2 点伤害（不分敌我）");
        }

        // ==================== ④ ≥3 个时间标志 → 牺牲自身 ====================

        public void TestHourglass_三个标志则牺牲自身()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var hourglass = AttachToArea(game, area, 1);
            var c = OnField(game, Probe("TS_hg_c3"), 1, area);
            game.CardManager.AddCounter(hourglass, "时间标志", 2);
            game.CardManager.AddCounter(c, "时间标志", 1);      // 合计 3

            var b = FindEffect(hourglass.PlayerTemplate, "JC048_C_SacrificeSelfIfThreeMarkers");
            EffectEngine.Instance.ResolveEffect(b, hourglass, null, "");   // 无地区上下文 → 回落到来源卡所在地区

            Assert.AreEqual(CardZone.Graveyard, hourglass.CurrentZone, "该地区有 3 个时间标志 → 诅咒沙漏被牺牲（进墓地）");
        }

        public void TestHourglass_两个标志不牺牲()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var hourglass = AttachToArea(game, area, 1);
            game.CardManager.AddCounter(hourglass, "时间标志", 2);   // 只有 2 个

            var b = FindEffect(hourglass.PlayerTemplate, "JC048_C_SacrificeSelfIfThreeMarkers");
            EffectEngine.Instance.ResolveEffect(b, hourglass, null, "");

            Assert.AreEqual(CardZone.AttachedZone, hourglass.CurrentZone, "只有 2 个 → 不牺牲，仍结附在地区上");
        }
    }
}
