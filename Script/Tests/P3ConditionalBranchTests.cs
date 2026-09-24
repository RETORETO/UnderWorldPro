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
    /// 【K17·条件式动作分支】按条件二选一执行（`EffectCondition.Predicate` + `ElseBranch`）：
    /// **条件成立 → `Actions`；不成立 → `ElseBranch`**（单一判定实现 `EffectEngine.ResolveEffect`）。
    /// 覆盖卡：**JC004 力场法师**（进场触发：横置本地区目标角色；若该角色已横置，则改为移回其拥有者手上）。
    /// </summary>
    public class P3ConditionalBranchTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 97 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

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

        private static EffectData Jc004Effect(PlayerCardData tmpl)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == "JC004_1_TapOrBounceLocalCharacter") return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestK17_JC004结构()
        {
            var tmpl = Load("JC004_lichangfashi");
            Assert.IsNotNull(tmpl, "JC004 模板应可加载");
            var e = Jc004Effect(tmpl);
            Assert.IsNotNull(e, "JC004 应有进场触发效果");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "时机＝进场触发");
            Assert.AreEqual("已横置", e.Condition.Predicate, "条件谓词＝已横置（成立即走 Actions）");
            Assert.AreEqual(1, e.Actions.Count, "条件成立时单动作");
            Assert.AreEqual(ActionVerb.Bounce, e.Actions[0].Verb, "已横置 → 移回拥有者手牌");
            Assert.IsNotNull(e.Condition.ElseBranch, "必须声明 ElseBranch");
            Assert.AreEqual(1, e.Condition.ElseBranch.Count, "条件不成立时单动作");
            Assert.AreEqual(ActionVerb.Tap, e.Condition.ElseBranch[0].Verb, "未横置 → 横置该角色");
            Assert.AreEqual(TargetKind.Character, e.Target.Kind, "目标＝角色");
            Assert.IsTrue(e.Target.IsLocalZone, "目标限定本地区");
        }

        // ==================== ② 行为：未横置 → 横置；已横置 → 移回手牌 ====================

        /// <summary>未横置的目标角色：走 ElseBranch → 被横置（不变量：仍在场上）。</summary>
        public void TestK17_条件不成立则横置()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_v1"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            Assert.IsFalse(victim.IsTapped, "前置：目标未横置");

            var e = Jc004Effect(mage.PlayerTemplate);
            EffectEngine.Instance.ResolveEffect(e, mage, victim, area);

            Assert.IsTrue(victim.IsTapped, "条件（已横置）不成立 → 走 ElseBranch：横置目标");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "横置不移场：目标仍在争夺区");
        }

        /// <summary>已横置的目标角色：条件成立 → 走 Actions → 移回其拥有者手牌。</summary>
        public void TestK17_条件成立则移回手牌()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_v2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            victim.SetTapped(true);
            int handBefore = game.TurnManager.GetHandOf(2).Count;

            var e = Jc004Effect(mage.PlayerTemplate);
            EffectEngine.Instance.ResolveEffect(e, mage, victim, area);

            Assert.AreEqual(CardZone.Hand, victim.CurrentZone, "条件（已横置）成立 → 走 Actions：移回**拥有者**（P2）手牌");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(2).Count, "P2 手牌 +1");
            Assert.AreEqual(2, victim.OwnerId, "回到拥有者（不是控制者）手上");
        }

        /// <summary>条件分支**不做半条效果**：已横置时只走"移回手牌"这一支——同地区其它角色不受影响（没有被横置）。</summary>
        public void TestK17_已横置时只走一支()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var mage = game.CardManager.CreateCardInstance(Load("JC004_lichangfashi"), 1);
            game.ZoneManager.MoveToArea(mage, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_k17_v3"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            victim.SetTapped(true);
            var bystander = game.CardManager.CreateCardInstance(Probe("TS_k17_v4"), 2);
            game.ZoneManager.MoveToArea(bystander, area, 2);

            var e = Jc004Effect(mage.PlayerTemplate);
            EffectEngine.Instance.ResolveEffect(e, mage, victim, area);

            Assert.AreEqual(CardZone.Hand, victim.CurrentZone, "已横置 → 只执行移回手牌这一支");
            Assert.AreEqual(CardZone.AreaZone, bystander.CurrentZone, "旁观角色未被移动");
            Assert.IsFalse(bystander.IsTapped, "旁观角色未被横置（证明只执行了一支、没有落进 ElseBranch）");
        }
    }
}
