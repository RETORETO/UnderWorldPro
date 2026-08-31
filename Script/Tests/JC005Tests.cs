using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// JC005 裂解术 数据层测试（2026-08-24）。
    /// 覆盖核心规则：
    ///   · 打出（取目标）阶段判定资产区【心】：有【心】可消灭任意附属，无【心】仅消灭结附角色的附属；
    ///     结附秘社（宿主 PlayerTemplate 为 null）/结附地区（AttachedTo 为 null）在无【心】时均非法。
    ///   · 结算复检阶段（TargetValidity.Check）跳过 AttachedToCharacterIfNoMind，即结算不再复判【心】。
    ///   · 事务卡(PlayTiming.Response) 目标经 EffectData.Timing=OnSorceryCast 聚合，IsLegalPlayTarget 走此路径。
    /// 纯数据层测试，不依赖拖拽/弹窗/UI。
    /// </summary>
    public partial class JC005Tests : TestRunner
    {
        // ---------- 模板/实例构造辅助 ----------

        private PlayerCardData MakeSorceryTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC005_lijieshu",
                CardName = "裂解术",
                CategoryFlag = CardCategory.Sorcery,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC005_1_DestroyAttachment",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Cost = 1,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Attachment,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.AttachedToCharacterIfNoMind },
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Destroy } },
                Duration = "瞬时",
            });
            return tmpl;
        }

        /// <summary>带【心】领域的资产卡模板（放入资产区即可让 HasMindDomain 为真）。</summary>
        private PlayerCardData MakeMindAssetTemplate()
        {
            var t = new PlayerCardData { UID = "mind_asset", CardName = "心灵资产", CategoryFlag = CardCategory.Character };
            t.MagicDomains = new Array<MagicDomain> { MagicDomain.Mind };
            return t;
        }

        private PlayerCardData MakeCharacterTemplate() =>
            new PlayerCardData { UID = "host_char", CardName = "宿主角色", CategoryFlag = CardCategory.Character };

        /// <summary>秘社（CouncilCardData 作宿主，实例的 PlayerTemplate 为 null，贴合真实秘社附属场景）。</summary>
        private CouncilCardData MakeCouncilTemplate() =>
            new CouncilCardData { UID = "host_secret", CardName = "宿主秘社" };

        private PlayerCardData MakeAttachmentTemplate() =>
            new PlayerCardData { UID = "attach", CardName = "附属", CategoryFlag = CardCategory.Attachment };

        /// <summary>构造一张附属实例，指定其结附宿主（null = 结附地区）。</summary>
        private CardInstance NewAttachment(string uid, CardInstance host)
        {
            var inst = new CardInstance(MakeAttachmentTemplate(), uid)
            {
                OwnerId = 1,
                ControllerId = 1,
                CurrentZone = CardZone.AttachedZone,
                IsAttached = true,
                AttachedTo = host,
            };
            return inst;
        }

        /// <summary>装配 ZoneManager/CardManager/PlayerManager 并注入 TargetValidity.PlayerManagerRef；hasMind 时在资产区放一张【心】资产。</summary>
        private void InjectSetup(bool hasMind)
        {
            var zm = new ZoneManager();
            zm._Ready();
            var cm = new CardManager();
            cm._Ready();
            zm.Initialize(cm, null); // 注入 _cardManager，否则 FireMoved→NotifyCardStateChanged 空引用
            var pm = new PlayerManager();
            pm._Ready();
            pm.SetZoneManager(zm);
            if (hasMind)
            {
                var mind = new CardInstance(MakeMindAssetTemplate(), "mind_asset_inst")
                {
                    OwnerId = 1,
                    ControllerId = 1,
                    CurrentZone = CardZone.AssetZone,
                };
                zm.MoveToAsset(mind, 1);
            }
            TargetValidity.PlayerManagerRef = pm;
        }

        private TargetSpec SorceryTarget() => MakeSorceryTemplate().EffectDataList[0].Target;

        // ---------- 取目标阶段（打出判定【心】） ----------

        /// <summary>T1：有【心】时，可消灭任意附属（结附角色 / 结附秘社 / 结附地区）。</summary>
        public void TestJC005_T1_有心_可灭任意附属()
        {
            InjectSetup(true);
            var cm = new CardManager();
            cm._Ready();
            var sorcery = new CardInstance(MakeSorceryTemplate(), "JC005_inst")
            {
                OwnerId = 1,
                ControllerId = 1,
                CurrentZone = CardZone.Hand,
            };
            var onChar = NewAttachment("a1", new CardInstance(MakeCharacterTemplate(), "h1"));
            var onSecret = NewAttachment("a2", new CardInstance(MakeCouncilTemplate(), "h2"));
            var onArea = NewAttachment("a3", null);

            Assert.IsTrue(cm.IsLegalPlayTarget(sorcery, onChar), "有【心】应可消灭结附角色的附属");
            Assert.IsTrue(cm.IsLegalPlayTarget(sorcery, onSecret), "有【心】应可消灭结附秘社的附属");
            Assert.IsTrue(cm.IsLegalPlayTarget(sorcery, onArea), "有【心】应可消灭结附地区的附属");
        }

        /// <summary>T2：无【心】时，仅结附角色的附属合法；结附秘社 / 结附地区的附属非法。</summary>
        public void TestJC005_T2_无心_仅灭结附角色()
        {
            InjectSetup(false);
            var cm = new CardManager();
            cm._Ready();
            var sorcery = new CardInstance(MakeSorceryTemplate(), "JC005_inst")
            {
                OwnerId = 1,
                ControllerId = 1,
                CurrentZone = CardZone.Hand,
            };
            var onChar = NewAttachment("a1", new CardInstance(MakeCharacterTemplate(), "h1"));
            var onSecret = NewAttachment("a2", new CardInstance(MakeCouncilTemplate(), "h2"));
            var onArea = NewAttachment("a3", null);

            Assert.IsTrue(cm.IsLegalPlayTarget(sorcery, onChar), "无【心】应可消灭结附角色的附属");
            Assert.IsFalse(cm.IsLegalPlayTarget(sorcery, onSecret), "无【心】不可消灭结附秘社的附属（宿主 PlayerTemplate 为 null）");
            Assert.IsFalse(cm.IsLegalPlayTarget(sorcery, onArea), "无【心】不可消灭结附地区的附属（AttachedTo 为 null）");
        }

        /// <summary>T3：无【心】且目标无宿主（结附地区的附属）应判非法（边界：AttachedTo==null 不应误放行）。</summary>
        public void TestJC005_T3_无心_无宿主附属非法()
        {
            InjectSetup(false);
            var spec = SorceryTarget();
            var orphan = NewAttachment("a4", null);
            bool ok = TargetValidity.CheckRestrictions(
                new System.Collections.Generic.List<TargetRestriction> { TargetRestriction.AttachedToCharacterIfNoMind },
                string.Empty, orphan, 1);
            Assert.IsFalse(ok, "无【心】且无宿主(结附地区)附属应非法");
        }

        // ---------- 结算复检阶段 ----------

        /// <summary>T4：取目标阶段经 IsLegalPlayTarget 判【心】（无【心】结附秘社非法）；
        /// 结算阶段经 TargetValidity.Check（内部自动 ExcludePlayTimeOnlyRestrictions 跳过该限制），无【心】结附秘社也合法。</summary>
        public void TestJC005_T4_结算复检_跳过心判定()
        {
            InjectSetup(false); // 无【心】
            var spec = SorceryTarget();
            var cm = new CardManager();
            cm._Ready();
            var sorcery = new CardInstance(MakeSorceryTemplate(), "JC005_inst")
            {
                OwnerId = 1,
                ControllerId = 1,
                CurrentZone = CardZone.Hand,
            };
            var onSecret = NewAttachment("a2", new CardInstance(MakeCouncilTemplate(), "h2"));

            // 取目标阶段（打出判定）：无【心】→ 结附秘社非法
            Assert.IsFalse(cm.IsLegalPlayTarget(sorcery, onSecret), "取目标(打出)判定：无【心】结附秘社应非法");

            // 结算阶段：Check 内部 ExcludePlayTimeOnlyRestrictions 剔除该限制 → 应合法（结算不复判【心】）
            var resolve = TargetValidity.Check(sorcery, onSecret, spec, null);
            Assert.IsTrue(resolve.valid, "结算复检：跳过 AttachedToCharacterIfNoMind 后结附秘社应合法（结算不重新判心）");
        }

        // ---------- 模板结构断言 ----------

        /// <summary>T5：JC005 模板结构符合设计（事务卡 / 快速行动 / 目标为附属 / 含条件式限制 / 动作为消灭）。</summary>
        public void TestJC005_T5_模板结构()
        {
            var tmpl = MakeSorceryTemplate();
            Assert.AreEqual(CardCategory.Sorcery, tmpl.CategoryFlag, "JC005 为事务卡");
            var e = tmpl.EffectDataList[0];
            Assert.AreEqual(EffectTiming.OnSorceryCast, e.Timing, "打出即结算");
            Assert.AreEqual(PlayTiming.Response, e.PlayTiming, "快速行动（任意优先权）");
            Assert.AreEqual(TargetKind.Attachment, e.Target.Kind, "目标为附属");
            Assert.IsTrue(e.Target.Restrictions.Contains(TargetRestriction.AttachedToCharacterIfNoMind),
                "含条件式限制 AttachedToCharacterIfNoMind");
            Assert.AreEqual(ActionVerb.Destroy, e.Actions[0].Verb, "动作为消灭");
        }
    }
}
