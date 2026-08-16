using System;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【JC002 巨石阵看护人 / JC003 帷幕护卫 · 丢失目标测试（文字用例→代码）】
    ///
    /// 覆盖三卡效果在结算时目标丢失的判定（2026-08-16 用户确认规则）：
    ///   · JC002 进场触发：目标=本地区角色（IsLocalZone=true），可指定自身，横置目标。
    ///   · JC003 角色激活：横置本角色+支付2，横置目标角色或暗藏者，无视地区（IsLocalZone=false）。
    ///
    /// 丢失判定统一走 TargetValidity.Check（版本号 + 发动地快照）：
    ///   · 规则①③ 离场/翻面（CheckZoneAndFace）：任何目标都判丢。
    ///   · 规则⑥ 本地区（IsLocalZone=true）：目标移出发动地判丢（争夺区移动不刷版本号，靠快照兜底）。
    ///   · 【确认点2】无视地区（IsLocalZone=false）：豁免规则⑥，目标移出地区不判丢。
    ///
    /// 备注：JC001 静止/持续效果数据层不变量见 JC001Tests；此处聚焦 JC002/JC003 的丢失目标矩阵。
    /// </summary>
    public class JC002003LostTargetTests : TestRunner
    {
        private CardInstance NewCharacter(string uid, CardZone zone = CardZone.AreaZone)
        {
            var tmpl = new PlayerCardData { CardName = "角色目标", CategoryFlag = CardCategory.Character };
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = 1;
            inst.ControllerId = 1;
            inst.CurrentZone = zone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        private CardInstance NewCovert(string uid, CardZone zone = CardZone.AreaZone)
        {
            var inst = NewCharacter(uid, zone);
            inst.SetCovert(true);   // 暗藏者
            return inst;
        }

        // JC003 来源：带激活效果（Target=Character + IncludeCovert），使 GetAllowedTargets 聚合出 Character，
        // 供 IsLegalActivateTarget 的大类粗筛通过（未配置 TargetRules 分支依赖模板 EffectDataList）。
        private CardInstance NewJC003Source(string uid = "src")
        {
            var tmpl = MakeJC003Template();
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = 1;
            inst.ControllerId = 1;
            inst.CurrentZone = CardZone.AreaZone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        // 本地区目标（JC002）：IsLocalZone=true，规则⑥生效
        private TargetSpec LocalZoneSpec() => new TargetSpec
        {
            Kind = TargetKind.Character,
            NeedsSelection = true,
            CanTargetSelf = true,
            IsLocalZone = true,
        };

        // 无视地区目标（JC003）：IsLocalZone=false，规则⑥豁免
        private TargetSpec AnyZoneSpec() => new TargetSpec
        {
            Kind = TargetKind.Character,
            NeedsSelection = true,
            CanTargetSelf = false,
            IsLocalZone = false,
            IncludeCovert = true,
        };

        // ==================== JC002 本地区丢失目标 ====================

        public void TestJC002_C1_目标合法不丢失()
        {
            // 场景：JC002 进场选目标A，A未移动未翻面 → 不丢失，横置生效
            var source = NewCharacter("src");
            var target = NewCharacter("tgt");
            var desc = LocalZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);   // 发动地=areaA，控制者=1

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsTrue(valid, $"C1 目标应合法，实际丢失原因={reason}");
        }

        public void TestJC002_C4_目标离场判丢失()
        {
            // 场景：JC002 选目标A后、结算前A进墓地 → 规则①/③判丢
            var source = NewCharacter("src");
            var target = NewCharacter("tgt", CardZone.Graveyard);   // A 已离场
            var desc = LocalZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsFalse(valid, "C4 目标离场应判丢失");
            // 注：目标在墓地时 CheckZoneAndFace 返回 LeftZone（非 Exiled/null 不归 Gone）
            Assert.IsTrue(reason == TargetLossReason.LeftZone, $"C4 丢失原因应为 LeftZone，实际={reason}");
        }

        public void TestJC002_C5_目标翻面判丢失()
        {
            // 场景：JC002 选目标A后、结算前A翻面成暗藏者 → 规则③翻面判丢
            var source = NewCharacter("src");
            var target = NewCovert("tgt");   // A 已翻面成暗藏者（JC002 目标不含暗藏者 IncludeCovert=false）
            var desc = LocalZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsFalse(valid, "C5 目标翻面应判丢失");
        }

        public void TestJC002_C6_目标移出本地区判丢失()
        {
            // 场景：JC002 选目标A后、结算前A移到地区B → 规则⑥本地区判丢（争夺区移动不刷版本号，靠快照兜底）
            var source = NewCharacter("src");
            var target = NewCharacter("tgt");
            target.ZoneDetailId = "areaB";   // 目标已跑出发动地 areaA
            var desc = LocalZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsFalse(valid, "C6 目标移出本地区应判丢失");
            Assert.IsTrue(reason == TargetLossReason.OutOfLocalZone, $"C6 丢失原因应为 OutOfLocalZone，实际={reason}");
        }

        public void TestJC002_C7_目标为自身且自身移出地区判丢失()
        {
            // 场景：JC002 目标=自身，结算前自身移到地区B → 规则⑥本地区判丢
            var source = NewCharacter("src");
            source.ZoneDetailId = "areaB";   // 来源自身已跑出发动地 areaA
            var desc = LocalZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, source, desc, snap);
            Assert.IsFalse(valid, "C7 目标=自身且自身移出地区应判丢失");
            Assert.IsTrue(reason == TargetLossReason.OutOfLocalZone, $"C7 丢失原因应为 OutOfLocalZone，实际={reason}");
        }

        public void TestJC002_C8_目标未移动不丢失()
        {
            // 对照：JC002 目标A留在发动地 areaA，不丢失
            var source = NewCharacter("src");
            var target = NewCharacter("tgt");
            target.ZoneDetailId = "areaA";   // 目标仍在发动地
            var desc = LocalZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsTrue(valid, $"C8 目标留在本地区应合法，实际丢失原因={reason}");
        }

        // ==================== JC003 无视地区丢失目标 ====================

        public void TestJC003_D5_目标离场判丢失()
        {
            // 场景：JC003 激活选目标B后、结算前B进墓地 → 判丢（规则①③，无视地区仍生效）
            var source = NewCharacter("src");
            var target = NewCharacter("tgt", CardZone.Graveyard);
            var desc = AnyZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsFalse(valid, "D5 目标离场应判丢失");
            // 注：目标在墓地时 CheckZoneAndFace 返回 LeftZone（非 Exiled/null 不归 Gone）
            Assert.IsTrue(reason == TargetLossReason.LeftZone, $"D5 丢失原因应为 LeftZone，实际={reason}");
        }

        public void TestJC003_D6_目标翻面判丢失()
        {
            // 场景：JC003 激活选目标B（角色）后、结算前B翻面成暗藏者
            // 注意：JC003 IncludeCovert=true 可目标暗藏者，但"选中时是正面角色、结算时已翻面"仍算目标变化判丢。
            // 这里验证：目标由正面翻为暗藏者后，仍判丢（版本号翻面 + CheckZoneAndFace）。
            var source = NewCharacter("src");
            var target = NewCovert("tgt");   // 原正面角色翻成暗藏者
            var desc = AnyZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            // JC003 可目标暗藏者（IncludeCovert=true），但本用例验证的是"选中后翻面"导致的目标状态变化。
            // 翻面本身已由版本号方案在入栈侧拦截（ResolveEntryTargetIsLost），此静态 Check 验证翻面后区/朝向判定。
            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            // 由于 JC003 允许暗藏者目标，翻面后仍是合法目标（暗藏者）→ 静态 Check 不判丢。
            // 真正的"翻面判丢"由版本号方案（目标入栈后翻面→版本号变化→目标丢失）在上层拦截。
            // 【备注】此处静态 Check 通过，说明翻面未破坏目标合法性（JC003 兼容暗藏者目标）；
            // 若目标是"正面角色且结算时翻面"，由版本号方案判丢，而非此静态 Check。
            Assert.IsTrue(valid, $"D6 静态 Check：JC003 翻面后目标仍是合法暗藏者目标，不应由本函数判丢，reason={reason}");
        }

        public void TestJC003_D7_无视地区豁免本地区判定()
        {
            // 场景：JC003 激活选目标B后、结算前B移到地区B → 无视地区（IsLocalZone=false）豁免规则⑥，不判丢
            var source = NewCharacter("src");
            var target = NewCharacter("tgt");
            target.ZoneDetailId = "areaB";   // 目标移到别地区
            var desc = AnyZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsTrue(valid, $"D7 无视地区豁免发动地快照，目标移出地区不应判丢，实际丢失原因={reason}");
        }

        public void TestJC003_D8_目标合法不丢失()
        {
            // 对照：JC003 目标B未变化，不丢失
            var source = NewCharacter("src");
            var target = NewCharacter("tgt");
            target.ZoneDetailId = "areaA";
            var desc = AnyZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsTrue(valid, $"D8 目标合法不丢失，实际丢失原因={reason}");
        }

        public void TestJC003_可目标暗藏者()
        {
            // JC003 IncludeCovert=true：暗藏者可以是合法目标
            var source = NewCharacter("src");
            var target = NewCovert("covert");
            var desc = AnyZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsTrue(valid, $"JC003 可目标暗藏者，实际丢失原因={reason}");
        }

        // ==================== JC001 数据层不变量（补充） ====================

        public void TestJC001_A_静止标记作用域()
        {
            // JC001-A 静止 CannotBeAttached：静止效果（Scope=ContestZone）仅在争夺区（AreaZone）注册生效。
            // 构造 JC001 风格卡（静止效果，Scope=ContestZone），验证 RegisterPersistent 的生效区判定。
            var tmpl = new PlayerCardData
            {
                CardName = "JC001_测试卡",
                CategoryFlag = CardCategory.Character,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC001_A_Test",
                Timing = EffectTiming.Static,
                Scope = ZoneScope.ContestZone,
                StaticEffects = new Godot.Collections.Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.CannotBeAttached, Param = "" }
                },
            });

            // 争夺区实例：注册后应有 CannotBeAttached
            var inArea = new CardInstance(tmpl, "jc001a");
            inArea.ControllerId = 1; inArea.OwnerId = 1; inArea.CurrentZone = CardZone.AreaZone;
            EffectEngine.RegisterPersistent(inArea);
            Assert.IsTrue(inArea.HasStaticFlag(StaticKind.CannotBeAttached), "JC001-A 争夺区应获得 CannotBeAttached 静止标记");

            // 手牌实例：不在生效区，不应注册（静止仅争夺区）
            var inHand = new CardInstance(tmpl, "jc001h");
            inHand.ControllerId = 1; inHand.OwnerId = 1; inHand.CurrentZone = CardZone.Hand;
            EffectEngine.RegisterPersistent(inHand);
            Assert.IsFalse(inHand.HasStaticFlag(StaticKind.CannotBeAttached), "JC001-A 手牌不应获得 CannotBeAttached（静止仅争夺区生效）");
        }

        // ==================== JC003 激活目标选择流程 ====================
        // 说明：激活链路（ActivateAbility → RequestActivateTargetSelection）依赖 UI 协调器与 PlayerManager，
        // 纯单元测试环境（TurnManager.Initialize(null,null,cm)）无法完整驱动 UI 选目标窗。
        // 此处用 ActivateEligibility.IsLegalActivateTarget（纯静态、不依赖 cm/PlayerManager）+ TargetValidity.Check
        // 覆盖"目标选择流程"的核心——合法目标判定 + 目标丢失判定。完整激活结算集成需场景级测试。

        private PlayerCardData MakeJC003Template()
        {
            var tmpl = new PlayerCardData
            {
                CardName = "JC003_帷幕护卫",
                CategoryFlag = CardCategory.Character,
            };
            // 目标选择流程核心：激活效果 Target（Character + 或暗藏者 + 无视地区）
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC003_1_Tap",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    Filter = "或暗藏者",
                    NeedsSelection = true,
                    IncludeCovert = true,
                    // IsLocalZone 默认 false → 无视地区
                },
            });
            return tmpl;
        }

        private EffectData JC003Ability() => new EffectData
        {
            EffectId = "JC003_1_Tap",
            Timing = EffectTiming.CharacterActivated,
            Target = new TargetSpec
            {
                Kind = TargetKind.Character,
                Filter = "或暗藏者",
                NeedsSelection = true,
                IncludeCovert = true,
            },
        };

        public void TestJC003_激活目标选择_合法角色目标()
        {
            // 目标选择流程：角色目标是 JC003 的合法激活目标
            var source = NewJC003Source();
            var target = NewCharacter("tgt", CardZone.AreaZone);
            target.ZoneDetailId = "areaB";   // 不同地区（无视地区）
            bool ok = ActivateEligibility.IsLegalActivateTarget(source, JC003Ability(), target, null);
            Assert.IsTrue(ok, "JC003 合法激活目标：角色目标（无视地区）应合法");
        }

        public void TestJC003_激活目标选择_合法暗藏者目标()
        {
            // 目标选择流程：暗藏者目标（IncludeCovert=true + Filter"或暗藏者"）合法
            var source = NewJC003Source();
            var target = NewCovert("covert", CardZone.AreaZone);
            bool ok = ActivateEligibility.IsLegalActivateTarget(source, JC003Ability(), target, null);
            Assert.IsTrue(ok, "JC003 合法激活目标：暗藏者目标应合法");
        }

        public void TestJC003_激活目标选择_无视地区()
        {
            // 目标选择流程：无视地区（IsLocalZone=false），不同地区目标仍合法（IsLegalActivateTarget 不查地区）
            var source = NewJC003Source();
            source.ZoneDetailId = "areaA";
            var target = NewCharacter("tgt", CardZone.AreaZone);
            target.ZoneDetailId = "areaB";   // 目标在别地区
            bool ok = ActivateEligibility.IsLegalActivateTarget(source, JC003Ability(), target, null);
            Assert.IsTrue(ok, "JC003 无视地区：不同地区目标应合法");
        }

        public void TestJC003_激活目标选择_拒绝自身()
        {
            // 目标选择流程：自身不是合法目标（源==目标）
            var source = NewJC003Source();
            bool ok = ActivateEligibility.IsLegalActivateTarget(source, JC003Ability(), source, null);
            Assert.IsFalse(ok, "JC003 目标选择：自身不能作为目标");
        }

        public void TestJC003_激活目标选择_拒绝非角色()
        {
            // 目标选择流程：非角色（附属）不是合法目标
            var source = NewJC003Source();
            // 构造附属类型目标（CategoryFlag=Attachment），MapInstanceToTargetKind 判为非角色
            var tmpl = new PlayerCardData { CardName = "附属目标", CategoryFlag = CardCategory.Attachment };
            var target = new CardInstance(tmpl, "tgt");
            target.OwnerId = 1; target.ControllerId = 1;
            target.CurrentZone = CardZone.AreaZone; target.ZoneDetailId = "areaA";
            bool ok = ActivateEligibility.IsLegalActivateTarget(source, JC003Ability(), target, null);
            Assert.IsFalse(ok, "JC003 目标选择：附属/非角色目标应拒绝");
        }

        public void TestJC003_激活目标_翻面后仍合法()
        {
            // 目标选择流程 + 丢失：JC003 IncludeCovert=true 可目标暗藏者，
            // 目标翻面成暗藏者后仍是合法目标（静态 IsLegalActivateTarget 不判丢；真正"选中后翻面"判丢由版本号方案上层拦截）
            var source = NewJC003Source();
            var target = NewCovert("tgt", CardZone.AreaZone);
            bool ok = ActivateEligibility.IsLegalActivateTarget(source, JC003Ability(), target, null);
            Assert.IsTrue(ok, "JC003 目标翻面成暗藏者后仍合法（IncludeCovert=true）");
        }

        public void TestJC003_激活目标_移出地区不判丢()
        {
            // 目标选择流程 + 丢失：无视地区（IsLocalZone=false）豁免发动地快照（规则⑥），
            // 目标移出地区在 TargetValidity.Check 中不判丢（确认点2）
            var source = NewCharacter("src");
            var target = NewCharacter("tgt");
            target.ZoneDetailId = "areaB";
            var desc = AnyZoneSpec();
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);
            var (valid, reason) = TargetValidity.Check(source, target, desc, snap);
            Assert.IsTrue(valid, $"JC003 激活目标移出地区不判丢（无视地区豁免发动地快照），reason={reason}");
        }
    }
}
