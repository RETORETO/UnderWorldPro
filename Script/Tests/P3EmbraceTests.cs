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
    /// 【P3·K4】JC036 初拥 —— **持续夺控 + 子类别改写**（用户 2026-09 §7.4b A16 口径）。
    ///
    /// 交付的机制：
    ///  · **持续夺控** `EffectData.ControlGain`（`Victim = AttachedHost`）：登记时经
    ///    `ZoneManager.ChangeController` 把宿主交给本卡控制者（只改 controller、物主不变、**卡不移动地区**、
    ///    **不刷版本号**）；注销（来源离场/解附/卡失效）时**归还给拥有者**（卡面 FAQ 原文）；
    ///  · **子类别改写** `ContinuousGrants{ Kind="SubCategoryRemove", Value="人类" }` + `{ Kind="SubCategory", Value="吸血鬼" }`
    ///    （落点＝宿主）：把**模板固有**的"人类"从**实例当前类别**里抹掉、追加"吸血鬼"，撤销时计数归零还原；
    ///    **印刷类别（模型数据）不变** → "印刷类别为人类"类判定照旧成立、"人类角色"类判定不再成立；
    ///  · **通用规则** `AttachmentRule`：宿主翻面为暗藏者 ⇒ 其上「结附于角色」的附属被消灭
    ///    （JC036 FAQ 第二条；用户口径＝做通用规则，不只对 JC036 生效）。
    ///
    /// 口径（A16）："没有写持续到回合结束的效果都持续尽可能久"——**不设版本号判据**：
    /// 初拥离场/被消灭时持续效果自然注销，被初拥角色离场时作为附属的初拥也一同进墓地，两条路径都落在注销分支。
    /// </summary>
    public class P3EmbraceTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 1)
        {
            var game = new GameInitializer { DebugSeed = 936 };
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

        /// <summary>人类角色探针（印刷子类别含"人类"）。</summary>
        private static PlayerCardData HumanProbe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 3,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>普通附属探针（结附于角色）。</summary>
        private static PlayerCardData AttachmentProbe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针附属_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Attachment, Defense = -1,
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
            game.EffectEngine.RegisterPersistent(attachment);
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>造一个"P2 的人类角色"，并由 P1 的初拥结附上去；返回 (host, chuyong)。</summary>
        private static (CardInstance host, CardInstance chuyong) Embrace(GameInitializer game, string hostUid = "TS_emb_host")
        {
            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(HumanProbe(hostUid), 2);   // 物主/控制者＝P2
            game.ZoneManager.MoveToArea(host, area, 2);
            var chuyong = game.CardManager.CreateCardInstance(Load("JC036_chuyong"), 1);   // P1 的初拥
            Attach(game, host, chuyong);
            return (host, chuyong);
        }

        // ==================== ① 结构 ====================

        public void TestEmbrace_结构()
        {
            var jc036 = Load("JC036_chuyong");
            Assert.IsNotNull(jc036, "JC036 卡数据可加载");
            Assert.IsNotNull(jc036.TargetRules, "有打出目标规则");
            Assert.AreEqual(TargetKind.Character, jc036.TargetRules[0].Kind, "结附于角色");
            Assert.IsTrue(jc036.TargetRules[0].Restrictions.Contains(TargetRestriction.StaticSubCategory),
                "打的目标＝**印刷类别**（模型数据）为人类的角色（卡文原文）");
            Assert.AreEqual("人类", jc036.TargetRules[0].Param, "印刷类别＝人类");

            var e = FindEffect(jc036, "JC036_A_EmbraceHost");
            Assert.IsNotNull(e, "有『获得控制权 + 类别改写』持续效果");
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "持续效果（不是触发）");
            Assert.AreEqual(ZoneScope.AttachTarget, e.Scope, "结附于角色时生效");
            Assert.IsNotNull(e.ControlGain, "声明了持续夺控");
            Assert.AreEqual(ControlGainVictim.AttachedHost, e.ControlGain.Victim, "夺控对象＝结附宿主");
            Assert.IsTrue(e.GrantToHost, "类别改写的落点＝宿主");
            Assert.AreEqual(2, e.ContinuousGrants.Count, "两条授予：移除人类 + 追加吸血鬼");
            Assert.AreEqual("SubCategoryRemove", e.ContinuousGrants[0].Kind, "第一条＝移除子类别");
            Assert.AreEqual("人类", e.ContinuousGrants[0].Value, "移除『人类』");
            Assert.AreEqual("SubCategory", e.ContinuousGrants[1].Kind, "第二条＝追加子类别");
            Assert.AreEqual("吸血鬼", e.ContinuousGrants[1].Value, "追加『吸血鬼』");
        }

        // ==================== ② 夺控 + 类别改写（端到端） ====================

        public void TestEmbrace_夺控并改写类别()
        {
            var game = NewGame();
            var (host, chuyong) = Embrace(game);

            Assert.AreEqual(2, host.OwnerId, "前置：宿主物主＝P2");
            Assert.AreEqual(1, host.ControllerId, "P1 的初拥结附后，**控制权归 P1**");
            Assert.AreEqual(2, host.OwnerId, "物主不变（只是控制者换了）");
            Assert.AreEqual(FirstArea(game), host.ZoneDetailId, "**受控角色所在地区不变**");
            Assert.AreEqual(CardZone.AreaZone, host.CurrentZone, "仍在争夺区（不因夺控易区）");
            Assert.AreEqual(1, game.EffectEngine.ControlGainCount, "登记了一条持续夺控");

            Assert.IsTrue(host.HasSubCategory("吸血鬼"), "类别改写：获得『吸血鬼』");
            Assert.IsFalse(host.HasSubCategory("人类"), "类别改写：失去『人类』（实例当前类别）");
            Assert.IsTrue(host.HasStaticSubCategory("人类"), "**印刷类别（模型数据）不变**：模板仍含『人类』");
            Assert.IsTrue(chuyong.AttachedTo == host, "结附关系保持");
        }

        // ==================== ③ 初拥离场 ⇒ 控制权归还拥有者 + 类别还原 ====================

        public void TestEmbrace_初拥离场归还控制权并还原类别()
        {
            var game = NewGame();
            var (host, chuyong) = Embrace(game);
            Assert.AreEqual(1, host.ControllerId, "前置：已被 P1 夺控");

            game.CardManager.DestroyInstance(chuyong);   // 初拥被消灭（离开场上）

            Assert.AreEqual(CardZone.Graveyard, chuyong.CurrentZone, "初拥进墓地");
            Assert.AreEqual(2, host.ControllerId, "控制权**归还拥有者**（FAQ 原文：交还给其拥有者）");
            Assert.AreEqual(0, game.EffectEngine.ControlGainCount, "夺控登记已注销");
            Assert.IsTrue(host.HasSubCategory("人类"), "类别改写撤销：『人类』回来");
            Assert.IsFalse(host.HasSubCategory("吸血鬼"), "类别改写撤销：『吸血鬼』消失");
        }

        // ==================== ④ 宿主翻面为暗藏者 ⇒ 初拥被消灭（FAQ 第二条） ====================

        public void TestEmbrace_宿主翻面为暗藏者则初拥被消灭()
        {
            var game = NewGame();
            var (host, chuyong) = Embrace(game);
            Assert.AreEqual(1, host.ControllerId, "前置：已被 P1 夺控");

            host.SetCovert(true);   // 因其他卡牌能力翻面为暗藏者

            Assert.AreEqual(CardZone.Graveyard, chuyong.CurrentZone,
                "FAQ 第二条：被初拥结附的角色翻面为暗藏者 → **初拥被消灭**（通用规则 AttachmentRule）");
            Assert.AreEqual(2, host.ControllerId, "该暗藏者的操控权交还给其拥有者");
            Assert.IsFalse(host.HasSubCategory("吸血鬼"), "改写一并还原");
            Assert.AreEqual(0, game.EffectEngine.ControlGainCount, "夺控登记已注销");
        }

        // ==================== ⑤ 通用规则：对其它「结附于角色」的附属同样生效 ====================

        public void TestEmbrace_通用规则影响其它附属且不误伤地区附属()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(HumanProbe("TS_emb_host2"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);

            // A：结附于**角色**的附属 → 宿主翻面时被消灭
            var onHost = game.CardManager.CreateCardInstance(AttachmentProbe("TS_emb_att_card"), 1);
            Attach(game, host, onHost);

            // B：结附于**地区**的附属（无宿主）→ 不受影响
            var onArea = game.CardManager.CreateCardInstance(AttachmentProbe("TS_emb_att_area"), 1);
            game.ZoneManager.MoveToAttachedZone(onArea, area, 1);
            Assert.IsNull(onArea.AttachedTo, "前置：结附于地区（无宿主）");

            host.SetCovert(true);

            Assert.AreEqual(CardZone.Graveyard, onHost.CurrentZone, "结附于角色的附属被消灭（通用规则，不只 JC036）");
            Assert.AreEqual(CardZone.AttachedZone, onArea.CurrentZone, "结附于地区的附属不受影响");
        }

        // ==================== ⑥ "人类角色"读实例 vs "印刷类别为人类"读模型 ====================

        public void TestEmbrace_人类判定按实例而印刷判定按模型()
        {
            var game = NewGame();
            var (host, _) = Embrace(game);
            var jc036 = Load("JC036_chuyong");
            var jc091 = Load("JC091_mousha");
            var e091 = FindEffect(jc091, "JC091_1_DestroyHuman");

            // JC036 自身的结附限制写"**印刷类别**为人类" → 改写后**仍然成立**
            var (printRuleOk, _) = TargetValidity.Check(host, host, jc036.TargetRules[0], null, game.PlayerManager);
            Assert.IsTrue(printRuleOk, "『印刷类别为人类』读模型数据 → 改写后仍成立（故初拥不自毁）");

            // JC091 写的是"人类角色" → 读实例当前类别 → 被改写的吸血鬼**不再是合法目标**
            var (humanRuleOk, reason) = TargetValidity.Check(host, host, e091.Target, null, game.PlayerManager);
            Assert.IsFalse(humanRuleOk, "JC091『目标人类角色』对改写后的角色**不再合法**（用户 2026-09 口径）");
            Assert.AreEqual(TargetLossReason.Illegal, reason, "判为不合法（而非丢失）");

            // 对照：未被改写的人类角色仍然合法
            var plain = game.CardManager.CreateCardInstance(HumanProbe("TS_emb_plain"), 1);
            game.ZoneManager.MoveToArea(plain, FirstArea(game), 1);
            var (plainOk, _) = TargetValidity.Check(host, plain, e091.Target, null, game.PlayerManager);
            Assert.IsTrue(plainOk, "对照：普通人类角色仍可被 JC091 指定");
            // JC015/JC019 的"非人类"同口径（实例当前类别）→ 改写后的吸血鬼**合法**
            var jc015 = Load("JC015_qumoren");
            Assert.IsTrue(jc015.TargetRules[0].Restrictions.Contains(TargetRestriction.NotCurrentSubCategory),
                "JC015『非人类』改为实例口径（NotCurrentSubCategory）");
            var jc019 = Load("JC019_mierdundezhenmoqiang");
            Assert.IsTrue(FindEffect(jc019, "JC019_1_GrantBlackBladeAndAbility").GrantedAbility.Target
                    .Restrictions.Contains(TargetRestriction.NotCurrentSubCategory),
                "JC019 授予能力的『非人类』同样改为实例口径");

            var notHumanOnly = new Array<TargetRestriction> { TargetRestriction.NotCurrentSubCategory };
            var plainHuman = game.CardManager.CreateCardInstance(HumanProbe("TS_emb_plain2"), 1);
            game.ZoneManager.MoveToArea(plainHuman, FirstArea(game), 1);
            Assert.IsTrue(TargetValidity.CheckRestrictions(notHumanOnly, "人类", host, 2, game.PlayerManager),
                "被改写的角色满足『非人类』（实例当前类别已无人类）");
            Assert.IsFalse(TargetValidity.CheckRestrictions(notHumanOnly, "人类", plainHuman, 2, game.PlayerManager),
                "对照：普通人类角色不满足『非人类』");
        }

        // ==================== ⑦ 快照投影：被改写掉的类别必须下行 ====================

        public void TestEmbrace_快照投影含被改写类别()
        {
            var game = NewGame();
            var (host, _) = Embrace(game);

            var r = ZoneManager.ProjectCard(host, host.CurrentZone, host.ControllerId);
            Assert.IsTrue(r.RemovedSubCategories.Contains("人类"), "投影：被改写掉的『人类』随快照下行");
            Assert.IsTrue(r.SubCategories.Contains("吸血鬼"), "投影：当前类别含『吸血鬼』");
            Assert.IsFalse(r.SubCategories.Contains("人类"), "投影：当前类别不含『人类』");
            Assert.AreEqual(1, r.ControllerId, "投影：控制者＝P1（夺控随快照下行）");

            // 客户端重建：模板固有 ∪ 快照 − 被改写掉的 → 与服务端同解
            var client = new CardInstance(host.PlayerTemplate, "client_emb_host");
            client.RestoreProjectedState(new List<StaticKind>(), r.SubCategories, r.RemovedSubCategories,
                r.GrantedKeywords, r.GrantedIcons, r.GrantedAttributes, 0, 0);
            Assert.IsFalse(client.HasSubCategory("人类"), "客户端：不再具有『人类』（否则两端判定分叉）");
            Assert.IsTrue(client.HasSubCategory("吸血鬼"), "客户端：具有『吸血鬼』");
            Assert.IsTrue(client.HasStaticSubCategory("人类"), "客户端：印刷类别仍为『人类』（模型数据）");
        }

        // ==================== ⑧ 无宿主不夺控 ====================

        public void TestEmbrace_无宿主不夺控()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var chuyong = game.CardManager.CreateCardInstance(Load("JC036_chuyong"), 1);
            game.ZoneManager.MoveToAttachedZone(chuyong, area, 1);   // 未结附于角色（无宿主）
            game.EffectEngine.RegisterPersistent(chuyong);

            Assert.IsNull(chuyong.AttachedTo, "前置：确实没有宿主");
            Assert.AreEqual(0, game.EffectEngine.ControlGainCount, "无宿主 → 不夺控（fail-closed）");
        }
    }
}
