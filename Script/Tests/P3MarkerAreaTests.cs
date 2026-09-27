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
    /// 本批两张"纯机制、无需新确认"的卡：
    ///  · **JC055 安妮斯朵拉①**（准备步骤触发：在目标角色或附属上放置一个时间标志）——
    ///    全用既有原语：`OnTurnStarted`（＝准备步骤）+ 或种类目标（角色/附属）+ `PlaceCounter{时间标志,1,目标}`；
    ///  · **JC127 终局**（行动：选择目标地区 → 移除该地区所有势力标志；然后若该地区有 ≥3 个时间标志，
    ///    则消灭该地区上的所有角色）——`RemoveToken{"势力标志","全部",对手/自己,"目标地区"}` 两条
    ///    ＋ 新谓词 **`AreaMarkerAtLeast`**（`Param = "时间标志,3"`）＋ 群体 `Destroy`（`MassMode.AllInArea`）。
    /// </summary>
    public class P3MarkerAreaTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 127 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 5,
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

        // ==================== ① JC055 结构 + 放置时间标志 ====================

        public void TestMarker_JC055结构()
        {
            var tmpl = Load("JC055_annisiduola");
            Assert.IsNotNull(tmpl, "JC055 模板应可加载");
            var e = FindEffect(tmpl, "JC055_A_PreparePlaceTimeMarker");
            Assert.IsNotNull(e, "JC055 应有『准备步骤 → 放时间标志』效果");
            Assert.AreEqual(EffectTiming.OnTurnStarted, e.Timing, "时机＝准备步骤（OnTurnStarted）");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.AreEqual(TargetKind.Attachment, e.Target.KindAlt, "或种类＝附属（『目标角色或附属』）");
            Assert.AreEqual(ActionVerb.PlaceCounter, e.Actions[0].Verb, "动作＝放卡上标志");
            Assert.AreEqual("时间标志", e.Actions[0].Args[0], "标志名＝时间标志");
            Assert.AreEqual("目标", e.Actions[0].Args[2], "对象＝目标");
        }

        public void TestMarker_JC055在目标上放置时间标志()
        {
            var game = NewGame();
            var spell = game.CardManager.CreateCardInstance(Load("JC055_annisiduola"), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            var target = OnField(game, Probe("TS_mk_victim"), 2, game.AreaManager.GetOrderedAreaUids()[0]);

            var e = FindEffect(spell.PlayerTemplate, "JC055_A_PreparePlaceTimeMarker");
            EffectEngine.Instance.ResolveEffect(e, spell, target, "");

            Assert.IsTrue(target.Counters.TryGetValue("时间标志", out int n) && n == 1,
                "目标角色获得 1 个时间标志");
        }

        // ==================== ② JC127 结构 ====================

        public void TestMarker_JC127结构()
        {
            var tmpl = Load("JC127_zhongju");
            Assert.IsNotNull(tmpl, "JC127 模板应可加载");
            Assert.AreEqual(TargetKind.Area, tmpl.TargetRules[0].Kind, "打出目标＝地区");

            var clear = FindEffect(tmpl, "JC127_A_ClearPowerMarksInArea");
            Assert.IsNotNull(clear, "JC127 应有『清除地区所有势力标志』效果");
            Assert.AreEqual(2, clear.Actions.Count, "两条 RemoveToken：对手的全部 + 自己的全部");
            Assert.AreEqual(ActionVerb.RemoveToken, clear.Actions[0].Verb, "动词＝移除地区标志");
            Assert.AreEqual("全部", clear.Actions[0].Args[1], "数量＝全部");
            Assert.AreEqual("对手", clear.Actions[0].Args[2], "第 1 条＝对手");
            Assert.AreEqual("自己", clear.Actions[1].Args[2], "第 2 条＝自己");
            Assert.AreEqual("目标地区", clear.Actions[0].Args[3], "范围＝目标地区");

            var destroy = FindEffect(tmpl, "JC127_B_DestroyAllIfMarkers");
            Assert.IsNotNull(destroy, "JC127 应有『若地区时间标志 ≥3 则消灭该地区所有角色』效果");
            Assert.AreEqual("AreaMarkerAtLeast", destroy.Condition.Predicate, "条件＝地区标志数 ≥ N（新谓词）");
            Assert.AreEqual("时间标志,3", destroy.Condition.Param, "参数＝时间标志,3");
            Assert.AreEqual(MassMode.AllInArea, destroy.Target.MassMode, "群体＝目标地区内全体");
            Assert.AreEqual(ActionVerb.Destroy, destroy.Actions[0].Verb, "动作＝消灭");
        }

        // ==================== ③ 新谓词：地区内卡上标志合计 ≥ N ====================

        public void TestMarker_地区标志数谓词()
        {
            var game = NewGame();
            var source = OnField(game, Load("JC127_zhongju"), 1, game.AreaManager.GetOrderedAreaUids()[0]);
            var areas = game.AreaManager.GetOrderedAreaUids();
            var a1 = OnField(game, Probe("TS_mk_a1"), 1, areas[0]);
            var a2 = OnField(game, Probe("TS_mk_a2"), 2, areas[0]);
            var other = OnField(game, Probe("TS_mk_other"), 1, areas[1]);

            game.CardManager.AddCounter(a1, "时间标志", 2);
            game.CardManager.AddCounter(a2, "时间标志", 1);
            game.CardManager.AddCounter(other, "时间标志", 3);   // 别的地区，不计入

            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("AreaMarkerAtLeast", source, source, source, -1, areas[0], "时间标志,3"),
                "该地区合计 3 个 → 满足 ≥3");
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("AreaMarkerAtLeast", source, source, source, -1, areas[0], "时间标志,2"),
                "≥2 也满足");
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("AreaMarkerAtLeast", source, source, source, -1, areas[0], "时间标志,4"),
                "≥4 不满足（该地区只有 3 个）");
            // 【回落口径】未给地区上下文时，谓词回落到**来源卡所在地区**（JC048 诅咒沙漏就靠这条）
            Assert.IsTrue(EffectEngine.Instance.EvaluateCondition("AreaMarkerAtLeast", source, source, source, -1, "", "时间标志,3"),
                "未给地区上下文 → 回落到来源卡所在地区（该地区合计 3）");
            var noArea = game.CardManager.CreateCardInstance(Probe("TS_mk_noarea"), 1);   // 仍在手牌/无地区
            Assert.IsFalse(EffectEngine.Instance.EvaluateCondition("AreaMarkerAtLeast", noArea, noArea, noArea, -1, "", "时间标志,1"),
                "来源卡也没有地区 → 不满足");
        }

        // ==================== ④ 端到端：清空地区势力标志 + 条件满足则消灭该地区所有角色 ====================

        public void TestMarker_JC127清除标志与条件消灭()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var spell = game.CardManager.CreateCardInstance(Load("JC127_zhongju"), 1);
            game.ZoneManager.MoveToHand(spell, 1);

            var mine = OnField(game, Probe("TS_mk_mine"), 1, area);
            var enemy = OnField(game, Probe("TS_mk_enemy"), 2, area);
            game.AreaManager.AddPowerMark(area, 1, 2);
            game.AreaManager.AddPowerMark(area, 2, 1);
            game.CardManager.AddCounter(mine, "时间标志", 1);
            game.CardManager.AddCounter(enemy, "时间标志", 2);   // 合计 3 → 满足

            var clear = FindEffect(spell.PlayerTemplate, "JC127_A_ClearPowerMarksInArea");
            EffectEngine.Instance.ResolveEffect(clear, spell, null, area);
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(area, 1), "本方势力标志被清空");
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(area, 2), "对手势力标志被清空");

            var destroy = FindEffect(spell.PlayerTemplate, "JC127_B_DestroyAllIfMarkers");
            EffectEngine.Instance.ResolveEffect(destroy, spell, null, area);
            Assert.AreEqual(CardZone.Graveyard, mine.CurrentZone, "条件满足 → 该地区本方角色被消灭");
            Assert.AreEqual(CardZone.Graveyard, enemy.CurrentZone, "条件满足 → 该地区敌方角色被消灭");
        }

        // ==================== ⑤ 条件不满足 → 不消灭 ====================

        public void TestMarker_JC127条件不满足则不消灭()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var spell = game.CardManager.CreateCardInstance(Load("JC127_zhongju"), 1);
            game.ZoneManager.MoveToHand(spell, 1);

            var mine = OnField(game, Probe("TS_mk_mine2"), 1, area);
            game.CardManager.AddCounter(mine, "时间标志", 2);   // 只有 2 个 → 不满足 ≥3

            var destroy = FindEffect(spell.PlayerTemplate, "JC127_B_DestroyAllIfMarkers");
            EffectEngine.Instance.ResolveEffect(destroy, spell, null, area);

            Assert.AreEqual(CardZone.AreaZone, mine.CurrentZone, "只有 2 个时间标志 → 不消灭角色");
        }
    }
}
