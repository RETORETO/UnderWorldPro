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
    /// 【P3·D】持续授予的落点＝**结附宿主**（`EffectData.GrantToHost`），按用户 2026-09 口径：
    ///  · 卡文"受此结附的角色获得X" → 授予**宿主**（结附的角色），**不是**附属自己；
    ///  · 附属未结附于卡（无宿主）→ **不授予**；
    ///  · 附属离场/被消灭 → 从**宿主**精确撤销（登记记录记住落点 UID）。
    /// 覆盖卡：**JC116 柯尔特巨蟒左轮**（宿主获得黑刀）、**JC073 苏族捕梦网**（宿主获得白眼与屏障）。
    /// </summary>
    public class P3GrantToHostTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 89 };
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

        /// <summary>把附属结附到宿主（模拟生产路径的结附落地 + 持续效果登记）。</summary>
        private static void Attach(GameInitializer game, CardInstance host, CardInstance attachment)
        {
            attachment.IsAttached = true;
            attachment.AttachedTo = host;
            host.AttachedCards.Add(attachment);
            game.ZoneManager.MoveToAttachedZone(attachment, host.ZoneDetailId ?? FirstArea(game), attachment.ControllerId);
            EffectEngine.Instance.RegisterPersistent(attachment);
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestGrantToHost_结构()
        {
            var revolver = Load("JC116_keertejumangzuolun");
            var e116 = FindEffect(revolver, "JC116_B_GrantBlackBladeToHost");
            Assert.IsNotNull(e116, "JC116 应有『宿主获得黑刀』持续效果");
            Assert.IsTrue(e116.GrantToHost, "授予落点＝宿主");
            Assert.AreEqual("Icon", e116.ContinuousGrants[0].Kind, "授予图标");
            Assert.AreEqual("黑刀", e116.ContinuousGrants[0].Value, "黑刀");

            var dream = Load("JC073_suzubumengwang");
            var e73 = FindEffect(dream, "JC073_A_GrantWhiteEyeAndBarrierToHost");
            Assert.IsNotNull(e73, "JC073 应有『宿主获得白眼与屏障』持续效果");
            Assert.IsTrue(e73.GrantToHost, "授予落点＝宿主");
            Assert.AreEqual(3, e73.ContinuousGrants.Count, "白眼（Icon）+ 先手调查（Attribute）+ 屏障");
        }

        // ==================== ② JC116：宿主获得黑刀，附属自身不获得 ====================

        public void TestGrantToHost_宿主获得黑刀()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_gth_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var revolver = game.CardManager.CreateCardInstance(Load("JC116_keertejumangzuolun"), 1);

            Attach(game, host, revolver);

            Assert.IsTrue(host.HasIcon("黑刀"), "**宿主**获得黑刀（附属于宿主的作用）");
            Assert.IsFalse(revolver.HasIcon("黑刀"), "附属自身不获得黑刀（授予落点＝宿主）");

            // 附属离场 → 从宿主精确撤销
            EffectEngine.Instance.UnregisterPersistent(revolver);
            Assert.IsFalse(host.HasIcon("黑刀"), "附属注销 → 宿主身上的黑刀被撤销");
        }

        // ==================== ③ JC073：宿主获得白眼与屏障（屏障生效于目标合法性） ====================

        public void TestGrantToHost_宿主获得白眼与屏障()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_gth_host2"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var dream = game.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);

            Assert.IsFalse(TargetValidity.IsBarrierBlocking(host, 2), "前置：宿主还没有屏障（敌方可以指定）");

            Attach(game, host, dream);

            Assert.IsTrue(host.HasIcon("白眼"), "宿主获得白眼");
            Assert.IsTrue(host.HasKeyword("屏障"), "宿主获得屏障（关键字）");
            Assert.IsTrue(TargetValidity.IsBarrierBlocking(host, 2), "屏障生效：**敌方**来源不能把宿主指定为目标");
            Assert.IsFalse(TargetValidity.IsBarrierBlocking(host, 1), "屏障只挡敌方：己方来源不受限制");
        }

        // ==================== ④ 无宿主不授予 ====================

        public void TestGrantToHost_无宿主不授予()
        {
            var game = NewGame();
            string area = FirstArea(game);

            // 未结附于卡：直接把附属放到附属区（AttachedTo 为空）
            var revolver = game.CardManager.CreateCardInstance(Load("JC116_keertejumangzuolun"), 1);
            game.ZoneManager.MoveToAttachedZone(revolver, area, 1);
            EffectEngine.Instance.RegisterPersistent(revolver);

            Assert.IsFalse(revolver.HasIcon("黑刀"), "无宿主 → 不授予（也不授予附属自身）");
            Assert.IsNull(revolver.AttachedTo, "前置：确实没有宿主");
        }
    }
}
