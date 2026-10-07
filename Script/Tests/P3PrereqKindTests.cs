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
    /// 【P3·B】持续前提（`PrereqKind`）扩展，按用户 2026-09 口径实现并锁定：
    ///  · `NoEnemyCharacterInArea`＝所在地区**没有敌方角色**（正面角色；暗藏者不算角色）→ JC016 执行部精锐（白眼/黑星）；
    ///  · `AreaMarkOwner`＝所在地区有**指定归属**的势力标志（`Param`＝自己/对手，`Amount` 缺省 1）→ JC084 街头混混（白星/白眼）。
    /// 前提随**敌方角色进出该地区**（`ZoneManager.FireMoved` 对双方都重算）与**势力标志增减**
    /// （装配层订阅 `AreaManager.OnMarksChanged`）实时开关。
    /// </summary>
    public class P3PrereqKindTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 19 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
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

        /// <summary>注册一张在场卡的持续效果（模拟"结算进场后登记持续效果"的生产路径）。</summary>
        private static void Register(GameInitializer game, CardInstance card)
            => EffectEngine.Instance.RegisterPersistent(card);

        // ==================== ① JC016：所在地区没有敌方角色 ====================

        public void TestPrereq_JC016结构()
        {
            var tmpl = Load("JC016_zhixingbujingrui");
            Assert.IsNotNull(tmpl, "JC016 模板应可加载");
            var e = FindEffect(tmpl, "JC016_C_NoEnemyInAreaIcons");
            Assert.IsNotNull(e, "JC016 应有『所在地区没有敌方角色』的持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "时机＝持续");
            Assert.IsNotNull(e.PrereqSpec, "必须声明结构化前提");
            Assert.AreEqual(PrereqKind.NoEnemyCharacterInArea, e.PrereqSpec.Kind, "前提＝所在地区没有敌方角色");
            // 【2026-09 修复】此前只授予 `Icon`（图标存在性）⇒ 卡面显示有图标但**数值没加**（先手调查/先手势力恒 0）。
            // 现两条通道都写：`Attribute`（白眼=先手调查 / 黑星=先手势力，真正改数值）+ `Icon`（图标存在性）。
            Assert.AreEqual(4, e.ContinuousGrants.Count, "授予：白眼/黑星 的 属性 + 图标 两条通道（共 4 条）");
            bool hasAttr = false;
            foreach (var g in e.ContinuousGrants)
                if (g.Kind == "Attribute" && g.Value == "先手调查" && g.Amount == 1) hasAttr = true;
            Assert.IsTrue(hasAttr, "必须授予属性『先手调查』（白眼＝先手调查+1，否则数值不生效）");
            bool hasPower = false;
            foreach (var g in e.ContinuousGrants)
                if (g.Kind == "Attribute" && g.Value == "先手势力" && g.Amount == 1) hasPower = true;
            Assert.IsTrue(hasPower, "必须授予属性『先手势力』（黑星＝先手势力+1）");
        }

        /// <summary>
        /// 【2026-09 修复·用户实测】JC016 的前提满足时，**数值**必须真的加上：
        /// 白眼＝先手调查 +1、黑星＝先手势力 +1（此前只写 `Icon` 通道 ⇒ `HasIcon` 为真但 `GetAttribute` 为 0）。
        /// </summary>
        public void TestPrereq_JC016获得图标同时数值生效()
        {
            var game = NewGame();
            var elite = game.CardManager.CreateCardInstance(Load("JC016_zhixingbujingrui"), 1);
            game.ZoneManager.MoveToArea(elite, Areas(game)[0], 1);
            Register(game, elite);
            EffectEngine.Instance.ReevaluatePrereqs(1);

            Assert.IsTrue(elite.HasIcon("白眼"), "前提满足 → 图标［白眼］存在");
            Assert.IsTrue(elite.HasIcon("黑星"), "前提满足 → 图标［黑星］存在");
            Assert.AreEqual(1, elite.GetAttribute("先手调查"), "白眼＝先手调查 +1（数值生效）");
            Assert.AreEqual(1, elite.GetAttribute("先手势力"), "黑星＝先手势力 +1（数值生效）");
            Assert.AreEqual(elite.PlayerTemplate.FirstStrikeInvestigation + 1, elite.CurrentFirstStrikeInvestigation,
                "实例先手调查 = 模板先手调查 + 授予（＋重算也不会丢）");
        }

        /// <summary>无敌方角色 → 获得白眼/黑星；敌方角色进入 → 收回；敌方角色离开 → 重新获得。</summary>
        public void TestPrereq_无敌方角色时才获得图标()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];

            var elite = game.CardManager.CreateCardInstance(Load("JC016_zhixingbujingrui"), 1);
            game.ZoneManager.MoveToArea(elite, a1, 1);
            Register(game, elite);

            EffectEngine.Instance.ReevaluatePrereqs(1);
            Assert.IsTrue(elite.HasIcon("白眼"), "所在地区无敌方角色 → 获得白眼");
            Assert.IsTrue(elite.HasIcon("黑星"), "所在地区无敌方角色 → 获得黑星");

            // 敌方角色进入同一地区（生产路径：MoveToArea → FireMoved → 重判双方）
            var foe = game.CardManager.CreateCardInstance(Probe("TS_pk_foe"), 2);
            game.ZoneManager.MoveToArea(foe, a1, 2);
            Assert.IsFalse(elite.HasIcon("白眼"), "敌方角色进入 → 前提丧失，白眼被收回");
            Assert.IsFalse(elite.HasIcon("黑星"), "敌方角色进入 → 黑星被收回");

            // 敌方角色移到另一个地区 → 前提恢复
            game.ZoneManager.MoveToArea(foe, a2, 2);
            Assert.IsTrue(elite.HasIcon("白眼"), "敌方角色离开 → 前提恢复，白眼再次获得");
            Assert.IsTrue(elite.HasIcon("黑星"), "黑星再次获得");
        }

        /// <summary>暗藏者不算"角色"：敌方**暗藏者**在同一地区不破坏前提。</summary>
        public void TestPrereq_敌方暗藏者不算角色()
        {
            var game = NewGame();
            string a1 = Areas(game)[0];
            var elite = game.CardManager.CreateCardInstance(Load("JC016_zhixingbujingrui"), 1);
            game.ZoneManager.MoveToArea(elite, a1, 1);
            Register(game, elite);

            var covert = game.CardManager.CreateCardInstance(Probe("TS_pk_covert"), 2);
            game.ZoneManager.MoveToArea(covert, a1, 2);
            covert.SetCovert(true);   // 翻成暗藏者

            EffectEngine.Instance.ReevaluatePrereqs(1);
            Assert.IsTrue(elite.HasIcon("白眼"), "敌方暗藏者不是『角色』→ 前提仍成立");
        }

        // ==================== ② JC084：所在地区有本方/敌方势力标志 ====================

        public void TestPrereq_JC084结构()
        {
            var tmpl = Load("JC084_jietouhunhun");
            Assert.IsNotNull(tmpl, "JC084 模板应可加载");

            var own = FindEffect(tmpl, "JC084_A_OwnMarkWhiteStar");
            Assert.IsNotNull(own, "应有『有本方势力标志 → 白星』");
            Assert.AreEqual(PrereqKind.AreaMarkOwner, own.PrereqSpec.Kind, "前提＝所在地区有势力标志");
            Assert.AreEqual("自己", own.PrereqSpec.Param, "归属＝自己");
            Assert.AreEqual(2, own.ContinuousGrants.Count, "授予白星（Icon + Attribute 两条通道）");

            var foe = FindEffect(tmpl, "JC084_B_EnemyMarkWhiteEye");
            Assert.IsNotNull(foe, "应有『有敌方势力标志 → 白眼』");
            Assert.AreEqual("对手", foe.PrereqSpec.Param, "归属＝对手");
            Assert.AreEqual(2, foe.ContinuousGrants.Count, "授予白眼（Icon + Attribute）");
        }

        /// <summary>本方标志 → 白星；敌方标志 → 白眼；标志被移除 → 对应图标收回。</summary>
        public void TestPrereq_势力标志驱动图标()
        {
            var game = NewGame();
            string a1 = Areas(game)[0];
            var punk = game.CardManager.CreateCardInstance(Load("JC084_jietouhunhun"), 1);
            game.ZoneManager.MoveToArea(punk, a1, 1);
            Register(game, punk);

            EffectEngine.Instance.ReevaluatePrereqs(1);
            Assert.IsFalse(punk.HasIcon("白星"), "前置：本地区无本方标志 → 没有白星");
            Assert.IsFalse(punk.HasIcon("白眼"), "前置：本地区无敌方标志 → 没有白眼");

            // 本方放标志（装配层订阅 OnMarksChanged → 自动重判）
            game.AreaManager.AddPowerMark(a1, 1, 1);
            Assert.IsTrue(punk.HasIcon("白星"), "本地区出现本方势力标志 → 获得白星");

            // 敌方放标志：净差逻辑会先抵扣本方 1 个 → 本方标志归零、敌方 0
            game.AreaManager.AddPowerMark(a1, 2, 2);
            Assert.AreEqual(0, game.AreaManager.GetPowerMark(a1, 1), "净差：敌方 2 个先抵扣本方 1 个");
            Assert.AreEqual(1, game.AreaManager.GetPowerMark(a1, 2), "敌方净剩 1 个 → 满足『有敌方标志』");
            Assert.IsTrue(punk.HasIcon("白眼"), "本地区出现敌方势力标志 → 获得白眼");
            Assert.IsFalse(punk.HasIcon("白星"), "本方标志已被抵扣 → 白星收回");

            // 移除敌方标志 → 白眼收回
            game.AreaManager.RemovePowerMark(a1, 2, 1);
            Assert.IsFalse(punk.HasIcon("白眼"), "敌方标志被移除 → 白眼收回");
        }
    }
}
