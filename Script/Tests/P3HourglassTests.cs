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
    /// 【P3·K5 + 2026-09 用户口径 A95】JC048 诅咒沙漏（附属，结附于目标地区）：
    /// 卡文全部在**一条效果**里按顺序处理 —— `Target.MassSequential = true`：
    ///  ① `PlaceCounter{时间标志,1,自身}`（普通动作 ⇒ 只执行一次）；
    ///  ② `DealDamage{"$X"}` + `MassInArea`（动作级群体 ⇒ 地区内每个角色各一次），
    ///     `ValueExpr = "计数:地区内卡上标志:时间标志:本地区"` + `ValueAtResolve`（结算时求值 ⇒ 含刚放的）；
    ///  ③ `Sacrifice` + `RequirePredicate = AreaMarkerAtLeast(时间标志,3)`（动作级"然后若…"条件）。
    ///
    /// 回归要点（用户实测的两个症状）：
    ///  · "每次放置都要造成伤害"（原先 X 在**收集**阶段求值 ⇒ 没标志时整条被跳过，只有牺牲那回合才伤害）；
    ///  · 三个动作的**先后关系**（放标志 → 打伤害 → 牺牲）由动作声明顺序保证。
    /// </summary>
    public class P3HourglassTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";
        private const string MainId = "JC048_1_PreparePlaceDamageSacrifice";

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

        // ==================== ① 结构：一条效果、三个动作、顺序原语 ====================

        public void TestHourglass_结构()
        {
            var tmpl = Load("JC048_zuzhoushalou");
            Assert.AreEqual(1, tmpl.EffectDataList.Count, "【A95】卡文是一条效果里的三个动作，不得拆成多条");
            var e = FindEffect(tmpl, MainId);
            Assert.IsNotNull(e, "JC048 唯一效果应存在");
            Assert.AreEqual(EffectTiming.OnTurnStarted, e.Timing, "时机＝准备步骤");
            Assert.IsTrue(e.Forced, "卡文『强制触发』");
            Assert.IsTrue(e.ValueAtResolve, "X 在结算时求值（含刚放下的标志）");
            Assert.AreEqual("计数:地区内卡上标志:时间标志:本地区", e.ValueExpr, "X＝该地区内所有卡上的时间标志之和");
            Assert.AreEqual(MassMode.AllInArea, e.Target.MassMode, "群体＝该地区内所有角色");
            Assert.IsTrue(e.Target.MassSequential, "【A95】动作按声明顺序执行（动作级群体/条件生效）");
            Assert.IsTrue(e.Target.CanTargetSelf, "不分敌我");
            Assert.AreEqual(3, e.Actions.Count, "共三个动作");

            // ① 放标志：普通动作（不声明 MassInArea）⇒ 只执行一次
            Assert.AreEqual(ActionVerb.PlaceCounter, e.Actions[0].Verb, "动作①＝放标志");
            Assert.AreEqual("时间标志", e.Actions[0].Args[0], "标志名");
            Assert.AreEqual("自身", e.Actions[0].Args[2], "放在诅咒沙漏自身");
            Assert.IsFalse(e.Actions[0].MassInArea, "①放标志只执行一次（若逐目标会放多次）");

            // ② 群体伤害：动作级群体
            Assert.AreEqual(ActionVerb.DealDamage, e.Actions[1].Verb, "动作②＝造成伤害");
            Assert.AreEqual("$X", e.Actions[1].Args[0], "伤害量＝动态值 X");
            Assert.IsTrue(e.Actions[1].MassInArea, "②对该地区每个角色各执行一次");

            // ③ 条件牺牲：动作级"然后若…"
            Assert.AreEqual(ActionVerb.Sacrifice, e.Actions[2].Verb, "动作③＝牺牲自身");
            Assert.AreEqual("AreaMarkerAtLeast", e.Actions[2].RequirePredicate, "③条件＝地区标志数");
            Assert.AreEqual("时间标志,3", e.Actions[2].RequireParam, "③参数＝时间标志,3");
        }

        // ==================== ② 取值写法：地区内卡上标志之和 ====================

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

            var e = FindEffect(hourglass.PlayerTemplate, MainId);
            var st = EffectEngine.Instance.EvaluateDynamicValue(e, hourglass, null, "", -1, out int x, out string reason);
            Assert.IsTrue(st == EffectEngine.DynamicValueState.Ok, $"动态值应可求值（{st} / {reason}）");
            Assert.AreEqual(4, x, $"本地区合计 1+2+1＝4（{reason}）");
        }

        // ==================== ③ 端到端：一条效果内"放 → 伤"顺序（用户实测症状） ====================

        public void TestHourglass_一条效果按顺序放标志并造成伤害()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var hourglass = AttachToArea(game, area, 1);
            var ally = OnField(game, Probe("TS_hg_ally"), 1, area);
            var enemy = OnField(game, Probe("TS_hg_enemy"), 2, area);

            // 结算前该地区**没有**时间标志：靠动作①刚放下的 1 个 ⇒ 每个角色应受 1 点伤害
            // （旧实现里②的 X 在收集阶段求值＝0 ⇒ 整条被跳过 ⇒ 实测"只有牺牲那回合才造成伤害"）
            var e = FindEffect(hourglass.PlayerTemplate, MainId);
            EffectEngine.Instance.ResolveEffect(e, hourglass, null, area);

            Assert.IsTrue(hourglass.Counters.TryGetValue("时间标志", out int n) && n == 1,
                "动作①已放 1 个时间标志");
            Assert.AreEqual(1, ally.Damage, "动作②紧随其后：本方角色受 1 点伤害（含刚放的标志）");
            Assert.AreEqual(1, enemy.Damage, "敌方角色同样受 1 点伤害（不分敌我）");
            Assert.AreEqual(CardZone.AttachedZone, hourglass.CurrentZone, "只有 1 个标志 → 动作③跳过，不牺牲");
        }

        // ==================== ④ ≥3 → 牺牲；且伤害按动作顺序累计 ====================

        public void TestHourglass_三个标志则牺牲自身()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var hourglass = AttachToArea(game, area, 1);
            var c = OnField(game, Probe("TS_hg_c3"), 1, area);
            game.CardManager.AddCounter(hourglass, "时间标志", 2);
            game.CardManager.AddCounter(c, "时间标志", 1);      // 结算前合计 3；动作① 放下后 4

            var e = FindEffect(hourglass.PlayerTemplate, MainId);
            EffectEngine.Instance.ResolveEffect(e, hourglass, null, area);

            Assert.AreEqual(4, c.Damage, "伤害在动作②、于放标志之后计算 ⇒ 4 点（3+1）");
            Assert.AreEqual(CardZone.Graveyard, hourglass.CurrentZone, "≥3 → 动作③牺牲自身（进墓地）");
        }

        public void TestHourglass_放完仍不足三个则不牺牲()
        {
            var game = NewGame();
            string area = game.AreaManager.GetOrderedAreaUids()[0];
            var hourglass = AttachToArea(game, area, 1);
            game.CardManager.AddCounter(hourglass, "时间标志", 1);   // 动作① 放下后 = 2 < 3

            var e = FindEffect(hourglass.PlayerTemplate, MainId);
            EffectEngine.Instance.ResolveEffect(e, hourglass, null, area);

            Assert.AreEqual(CardZone.AttachedZone, hourglass.CurrentZone, "放完只有 2 个 → 不牺牲，仍结附在地区上");
        }
    }
}
