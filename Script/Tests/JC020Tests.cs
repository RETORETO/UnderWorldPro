using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// JC020 隐蔽的飞刀 数据层测试（2026-08-17）。
    /// 覆盖今天（D46-D50）所有易出 bug 的点：
    ///   · 打出目标仅结附正面角色（排除暗藏者/地区）
    ///   · 未声明 Scope 的附属默认在 AttachedZone 生效（D46）
    ///   · 两效果目标：A 仅本地区暗藏者、B 仅本地区正面角色（含本地区限定）
    ///   · 本地区判定：目标移出宿主地区判丢失（D50）
    ///   · 效果B 前置仅牺牲（无横置前提）；效果A 前置横置自身（方案A）
    ///   · 牺牲/离场清结附关系，可再次结附（D49）
    /// </summary>
    public partial class JC020Tests : TestRunner
    {
        /// <summary>构造 JC020 风格模板：打出结附正面角色；效果A(横置暗藏者)+效果B(牺牲打伤害)。</summary>
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC020_yinbidefeidao",
                CardName = "隐蔽的飞刀",
                CategoryFlag = CardCategory.Attachment,
            };
            // 打出目标：结附正面角色（最简，排除暗藏者与地区）
            tmpl.TargetRules = new Array<TargetRule> { TargetRule.Make(TargetKind.Character) };
            // 效果A：快速行动，横置自身 → 横置本地区目标暗藏者
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC020_1_TapCovert",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Covert,
                    NeedsSelection = true,
                    IsLocalZone = true,
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Tap } },
                Duration = "瞬时",
            });
            // 效果B：快速行动，牺牲自身 → 对本地区目标正面角色造成1点伤害（无横置前提）
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC020_2_DealDamage",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "牺牲本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IsLocalZone = true,
                    IncludeCovert = false,
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "1" } } },
                Duration = "瞬时",
            });
            return tmpl;
        }

        /// <summary>构造结附到 AttachedZone 的 JC020 实例（控制者=1，ZoneDetailId=宿主地区）。</summary>
        private CardInstance NewAttached(string uid, int ctrl = 1, string zoneDetailId = "areaA")
        {
            var inst = new CardInstance(MakeTemplate(), uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AttachedZone;
            inst.IsAttached = true;
            inst.ZoneDetailId = zoneDetailId;
            return inst;
        }

        private CardInstance NewCharacter(string uid, int ctrl = 1, string zoneDetailId = "areaA", bool covert = false)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = uid, CategoryFlag = CardCategory.Character };
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = zoneDetailId;
            if (covert) inst.SetCovert(true);
            return inst;
        }

        // ==================== 打出目标 ====================

        public void TestJC020_T1_打出目标仅结附正面角色()
        {
            var tmpl = MakeTemplate();
            var allowed = tmpl.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Character), $"打出目标应含 Character，实际={string.Join(",", allowed)}");
            Assert.IsFalse(allowed.Contains(TargetKind.Area), "打出目标不应含 Area（不能结附地区，D48）");

            var cm = new CardManager(); cm._Ready();
            var src = NewAttached("src", 1);
            var faceUp = NewCharacter("正面角色", 2, "areaA");
            Assert.IsTrue(cm.IsLegalPlayTarget(src, faceUp), "打出目标=正面角色应合法");
            var covert = NewCharacter("暗藏者", 2, "areaA", covert: true);
            Assert.IsFalse(cm.IsLegalPlayTarget(src, covert), "打出目标=暗藏者不应合法（仅正面角色）");
        }

        // ==================== 效果结构 ====================

        public void TestJC020_T2_两效果结构()
        {
            var effects = MakeTemplate().EffectDataList;
            var eA = effects[0];
            var eB = effects[1];

            Assert.AreEqual(EffectTiming.AttachmentActivated, eA.Timing, "效果A为附属激活");
            Assert.AreEqual(TargetKind.Covert, eA.Target.Kind, "效果A仅暗藏者");
            Assert.IsTrue(eA.Target.IsLocalZone, "效果A本地区限定");
            Assert.IsTrue(eA.ActivateCondition.Contains("横置本角色"), "效果A以横置自身为代价");

            Assert.AreEqual(TargetKind.Character, eB.Target.Kind, "效果B仅正面角色");
            Assert.IsFalse(eB.Target.IncludeCovert, "效果B不选暗藏者");
            Assert.IsTrue(eB.Target.IsLocalZone, "效果B本地区限定");
            Assert.IsTrue(eB.ActivateCondition.Contains("牺牲本角色"), "效果B以牺牲自身为代价");
            Assert.IsFalse(eB.ActivateCondition.Contains("横置本角色"), "效果B不应以横置为代价（方案A）");
            Assert.AreEqual(ActionVerb.DealDamage, eB.Actions[0].Verb, "效果B动作为造成1点伤害");
            Assert.AreEqual("1", eB.Actions[0].Args[0], "效果B伤害为1");
        }

        // ==================== 生效区（D46） ====================

        public void TestJC020_T3_未声明Scope附属AttachedZone生效()
        {
            var effects = MakeTemplate().EffectDataList;
            Assert.AreEqual(ZoneScope.Specified, effects[0].Scope, "效果A未显式声明 Scope（默认 Specified，D46 根治）");
            Assert.AreEqual(ZoneScope.Specified, effects[1].Scope, "效果B未显式声明 Scope");

            var attached = NewAttached("jc020_att", 1, "areaA");
            Assert.IsTrue(EffectEngine.IsEffectActiveInZone(effects[0], attached), "附属在 AttachedZone 效果A 应生效");
            Assert.IsTrue(EffectEngine.IsEffectActiveInZone(effects[1], attached), "附属在 AttachedZone 效果B 应生效");

            var area = NewAttached("jc020_area", 1, "areaA");
            area.CurrentZone = CardZone.AreaZone; area.IsAttached = false;
            Assert.IsFalse(EffectEngine.IsEffectActiveInZone(effects[0], area), "附属在 AreaZone（未结附）效果A 不应生效");
        }

        // ==================== 目标过滤（本地区 + 暗藏者/角色） ====================

        public void TestJC020_T4_效果A仅本地区暗藏者()
        {
            var source = NewAttached("src", 1, "areaA");
            var ability = MakeTemplate().EffectDataList[0];   // 效果A：Covert + IsLocalZone

            var localCovert = NewCharacter("本区暗藏者", 2, "areaA", covert: true);
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(source, ability, localCovert, null),
                "效果A 对本地区暗藏者应合法");

            var remoteCovert = NewCharacter("他区暗藏者", 2, "areaB", covert: true);
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, ability, remoteCovert, null),
                "效果A 对非本地区暗藏者应非法（本地区限定）");

            var faceUp = NewCharacter("正面角色", 2, "areaA");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, ability, faceUp, null),
                "效果A 对正面角色应非法（Kind=Covert）");
        }

        public void TestJC020_T5_效果B仅本地区正面角色()
        {
            var source = NewAttached("src", 1, "areaA");
            var ability = MakeTemplate().EffectDataList[1];   // 效果B：Character + IncludeCovert=false + IsLocalZone

            var localFaceUp = NewCharacter("本区正面", 2, "areaA");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(source, ability, localFaceUp, null),
                "效果B 对本地区正面角色应合法");

            var localCovert = NewCharacter("本区暗藏者", 2, "areaA", covert: true);
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, ability, localCovert, null),
                "效果B 对暗藏者应非法（IncludeCovert=false）");

            var remoteFaceUp = NewCharacter("他区正面", 2, "areaB");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(source, ability, remoteFaceUp, null),
                "效果B 对非本地区角色应非法（本地区限定）");
        }

        // ==================== 本地区丢失（D50） ====================

        public void TestJC020_T6_目标移出本地区判丢失()
        {
            var source = NewAttached("src", 1, "areaA");
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);   // 来源（宿主）地区=areaA

            // 效果B（正面角色目标）：目标移出本地区 → OutOfLocalZone
            var tgt = NewCharacter("目标", 2, "areaA");
            var before = TargetValidity.Check(source, tgt, MakeTemplate().EffectDataList[1].Target, snap);
            Assert.IsTrue(before.valid, $"效果B 目标留本地区应合法（reason={before.reason}）");
            tgt.ZoneDetailId = "areaB";
            var after = TargetValidity.Check(source, tgt, MakeTemplate().EffectDataList[1].Target, snap);
            Assert.IsFalse(after.valid, "效果B 目标移出本地区应判丢失（D50）");
            Assert.IsTrue(after.reason == TargetLossReason.OutOfLocalZone, $"丢失原因应为 OutOfLocalZone，实际={after.reason}");

            // 效果A（暗藏者目标）：同理本地区丢失
            var covert = NewCharacter("目标暗藏", 2, "areaA", covert: true);
            var beforeA = TargetValidity.Check(source, covert, MakeTemplate().EffectDataList[0].Target, snap);
            Assert.IsTrue(beforeA.valid, $"效果A 目标留本地区应合法（reason={beforeA.reason}）");
            covert.ZoneDetailId = "areaB";
            var afterA = TargetValidity.Check(source, covert, MakeTemplate().EffectDataList[0].Target, snap);
            Assert.IsFalse(afterA.valid, "效果A 目标移出本地区应判丢失（D50）");
        }

        // ==================== 前置条件（方案A） ====================

        public void TestJC020_T7_效果A以横置为前提_效果B仅牺牲()
        {
            var eA = MakeTemplate().EffectDataList[0];
            var eB = MakeTemplate().EffectDataList[1];
            // 效果A 需要横置自身（已横置时不能发动，UI 置灰）
            Assert.IsTrue(eA.ActivateCondition.Contains("横置本角色"), "效果A 以横置自身为代价");
            // 效果B 仅牺牲，无横置前提（已横置也能发动）
            Assert.IsTrue(eB.ActivateCondition.Contains("牺牲本角色"), "效果B 以牺牲自身为代价");
            Assert.IsFalse(eB.ActivateCondition.Contains("横置本角色"), "效果B 不因横置被拦截（方案A）");
        }

        // ==================== 牺牲/离场清结附关系（D49） ====================

        public void TestJC020_T8_牺牲离场清结附关系()
        {
            var cm = new CardManager(); cm._Ready();
            var zm = new ZoneManager(); zm.Initialize(cm, null);
            cm.SetZoneManager(zm);

            var host = NewCharacter("宿主", 1, "areaA");
            var knife = NewAttached("jc020", 1, "areaA");
            // 建立结附关系（双向）
            knife.AttachedTo = host;
            host.AttachedCards.Add(knife);
            Assert.IsTrue(host.AttachedCards.Contains(knife), "前置：宿主结附列表含飞刀");

            // 模拟 JC020 效果B 牺牲自身（进墓地）→ 应清结附关系
            zm.MoveToGraveyard(knife, 1);

            Assert.IsNull(knife.AttachedTo, "牺牲离场后飞刀 AttachedTo 应为 null（D49）");
            Assert.IsFalse(host.AttachedCards.Contains(knife), "牺牲离场后宿主 AttachedCards 不应含飞刀（D49）");
        }
    }
}
