using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Game.Entities;

namespace Script.Tests
{
    /// <summary>
    /// 目标丢失规则单元测试（2026-08-07，接驳 TargetValidity.Check）。
    /// 覆盖需求 ①②③④⑤⑥⑦⑧⑨ 及基础语义。
    /// 注：测试直接构造 CardInstance 并设 Zone/IsCovert/HasGuard/ZoneDetailId，
    /// 不依赖 ZoneManager，聚焦 checker 逻辑本身。
    /// 已知边界（灵活目标阶段再处理，不在本测试范围）：
    ///  - "角色或暗藏者二选一"目标在数据层目前是两条 TargetRule，单目标取首条为基准；
    ///    本测试分别覆盖单一 Character / 单一 Covert 的翻面丢失。
    ///  - 规则④⑤"离开原附属/封印区"当前实现为"脱离附属/封印状态即丢"；
    ///    "换宿主但仍附属"的边界暂不覆盖（无原宿主记录机制）。
    /// </summary>
    public partial class TargetLossTests : TestRunner
    {
        private CardInstance NewInstance(string uid, CardZone zone, bool isCovert = false, bool hasGuard = false, string zoneDetail = "area-1")
        {
            var tmpl = new PlayerCardData { CardName = "T-" + uid };
            var inst = new CardInstance(tmpl, uid);
            inst.CurrentZone = zone;
            inst.IsCovert = isCovert;
            inst.HasGuard = hasGuard;
            inst.ZoneDetailId = zoneDetail;
            return inst;
        }

        // ---------- ① 角色离场/翻面 ----------
        public void TestCharacterLostWhenNull()
        {
            var src = NewInstance("src", CardZone.AreaZone, false, false, "area-1");
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, null, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Gone, reason);
        }

        public void TestCharacterLostWhenFlippedToCovert()
        {
            var src = NewInstance("src", CardZone.AreaZone, false, false, "area-1");
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: true);
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Covert, reason);
        }

        public void TestCharacterLostWhenLeftArea()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.Graveyard);
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.LeftZone, reason);
        }

        public void TestCharacterValidWhenFaceUpInArea()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: false);
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid); Assert.AreEqual(TargetLossReason.None, reason);
        }

        // ---------- ② 暗藏者离场/翻面 ----------
        public void TestCovertLostWhenFlippedToCharacter()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: false);
            var desc = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Uncover, reason);
        }

        public void TestCovertValidWhenCovertInArea()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: true);
            var desc = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid); Assert.AreEqual(TargetLossReason.None, reason);
        }

        // ---------- ③ 角色或暗藏者二选一：翻面仍丢（覆盖单一侧） ----------
        public void TestCharacterOrCovertFlippedStillLost()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: true);
            var descChar = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (v1, r1) = TargetValidity.Check(src, tgt, descChar);
            Assert.IsFalse(v1);
            var descCov = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true };
            var tgt2 = NewInstance("t2", CardZone.AreaZone, isCovert: false);
            var (v2, r2) = TargetValidity.Check(src, tgt2, descCov);
            Assert.IsFalse(v2);
        }

        // ---------- ④ 附属离开附属区 ----------
        public void TestAttachmentLostWhenLeftAttached()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.Graveyard);
            var desc = new TargetSpec { Kind = TargetKind.Attachment, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.LeftZone, reason);
        }

        public void TestAttachmentValidWhenInAttachedZone()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AttachedZone);
            var desc = new TargetSpec { Kind = TargetKind.Attachment, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid);
        }

        // ---------- ⑤ 封印离开封印区 ----------
        public void TestSealLostWhenLeftSealed()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.Graveyard);
            var desc = new TargetSpec { Kind = TargetKind.Seal, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.LeftZone, reason);
        }

        public void TestSealValidWhenInSealedZone()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.SealedZone);
            var desc = new TargetSpec { Kind = TargetKind.Seal, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid);
        }

        // ---------- ⑥ 本地区限定 = 发动地快照（2026-08-11 改版，2026-08-11 修正） ----------
        // 语义：以入栈时来源所在地区（snapshot.SourceZoneDetailId）为唯一基准，
        // 结算时只判断【目标】是否仍在发动地区；【来源(发动角色)】离开发动地不再判丢失
        // （效果发动后即与发动角色分离）。无快照则不判（兜底放行）。

        /// <summary>来源离开发动地区、但目标仍在发动地区 → 仍合法（效果与发动角色分离，不判来源地区）。</summary>
        public void TestLocalZoneValidWhenSourceLeftButTargetStays()
        {
            var src = NewInstance("src", CardZone.AreaZone, false, false, "area-2");   // 来源已移到 area-2（如回手牌）
            var tgt = NewInstance("t1", CardZone.AreaZone, false, false, "area-1");    // 目标仍在发动地 area-1
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IsLocalZone = true };
            var snap = new TargetValidity.TargetSnapshot("area-1", 0);                 // 发动地 = area-1
            var (valid, reason) = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsTrue(valid); Assert.AreEqual(TargetLossReason.None, reason);
        }

        /// <summary>目标离开发动地区 → 丢失（争夺区内移动不刷版本号，全靠本判定兜住）。</summary>
        public void TestLocalZoneLostWhenTargetLeftSnapshotArea()
        {
            var src = NewInstance("src", CardZone.AreaZone, false, false, "area-1");
            var tgt = NewInstance("t1", CardZone.AreaZone, false, false, "area-2");    // 目标跑到 area-2
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IsLocalZone = true };
            var snap = new TargetValidity.TargetSnapshot("area-1", 0);
            var (valid, reason) = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.OutOfLocalZone, reason);
        }

        /// <summary>来源与目标都仍在发动地区 → 合法。</summary>
        public void TestLocalZoneValidWhenBothStayInSnapshotArea()
        {
            var src = NewInstance("src", CardZone.AreaZone, false, false, "area-1");
            var tgt = NewInstance("t1", CardZone.AreaZone, false, false, "area-1");
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IsLocalZone = true };
            var snap = new TargetValidity.TargetSnapshot("area-1", 0);
            var (valid, reason) = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsTrue(valid);
        }

        /// <summary>目标=来源自身且自身换了地区 → 丢失（旧实现靠实时比对无法覆盖，必须用快照）。</summary>
        public void TestLocalZoneLostWhenSelfTargetChangedArea()
        {
            var self = NewInstance("self", CardZone.AreaZone, false, false, "area-2"); // 自身已移到 area-2
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IsLocalZone = true, CanTargetSelf = true };
            var snap = new TargetValidity.TargetSnapshot("area-1", 0);                 // 发动地 = area-1
            var (valid, reason) = TargetValidity.Check(self, self, desc, snap);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.OutOfLocalZone, reason);
        }

        // ---------- ⑦ 屏障：尚未实现，当前不判定 ----------
        /// <summary>屏障效果未实现，Check 暂不判 HasGuard（TargetValidity.Check 内留 TODO）。
        /// 待屏障落地后应改回断言 Illegal，并保持"屏障不刷版本号、仅结算时静态判定"。</summary>
        public void TestBarrierNotCheckedYet()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, hasGuard: true);
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid, "屏障未实现前不判定 HasGuard（实现后此断言应改为 Illegal）");
        }

        public void TestNoBarrierStillValid()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, hasGuard: false);
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid);
        }

        // ---------- Friendly / Enemy 结算复检（方案 B·入栈来源控制者快照） ----------

        /// <summary>Enemy 限制：目标被夺控换到来源同侧（夺控不刷版本号）→ 结算复检判非法。</summary>
        public void TestEnemyRestrictionLostWhenTargetBecameFriendly()
        {
            var src = NewInstance("src", CardZone.AreaZone); src.ControllerId = 0;
            var tgt = NewInstance("t1", CardZone.AreaZone);  tgt.ControllerId = 0;   // 已被夺控到我方
            var desc = new TargetSpec
            {
                Kind = TargetKind.Character, NeedsSelection = true,
                Restrictions = new Godot.Collections.Array<TargetRestriction> { TargetRestriction.Enemy },
            };
            var snap = new TargetValidity.TargetSnapshot(null, 0);                    // 入栈时来源控制者 = 0
            var (valid, reason) = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Illegal, reason);
        }

        /// <summary>Enemy 限制：目标仍在敌方 → 合法。</summary>
        public void TestEnemyRestrictionValidWhenStillEnemy()
        {
            var src = NewInstance("src", CardZone.AreaZone); src.ControllerId = 0;
            var tgt = NewInstance("t1", CardZone.AreaZone);  tgt.ControllerId = 1;
            var desc = new TargetSpec
            {
                Kind = TargetKind.Character, NeedsSelection = true,
                Restrictions = new Godot.Collections.Array<TargetRestriction> { TargetRestriction.Enemy },
            };
            var snap = new TargetValidity.TargetSnapshot(null, 0);
            var (valid, reason) = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsTrue(valid);
        }

        /// <summary>敌我基准取【入栈快照】而非来源当前值：来源自身事后被夺控，
        /// 不改变入栈时已确定的敌我关系（目标仍算敌方 → 合法）。</summary>
        public void TestEnemyRestrictionUsesSourceControllerSnapshot()
        {
            var src = NewInstance("src", CardZone.AreaZone); src.ControllerId = 1;   // 来源自己被夺控成了 1
            var tgt = NewInstance("t1", CardZone.AreaZone);  tgt.ControllerId = 1;   // 目标没动
            var desc = new TargetSpec
            {
                Kind = TargetKind.Character, NeedsSelection = true,
                Restrictions = new Godot.Collections.Array<TargetRestriction> { TargetRestriction.Enemy },
            };
            var snap = new TargetValidity.TargetSnapshot(null, 0);                    // 入栈时来源控制者 = 0
            var (valid, reason) = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsTrue(valid, "来源自身被夺控不应改变入栈时已锁定的敌我关系");
        }

        /// <summary>可对暗藏者生效的效果（IncludeCovert=true）：目标是暗藏者且未翻面 → 不应判丢失。
        /// 这是 CheckRestrictions 刻意不做大类粗筛的原因（MapInstanceToTargetKind 会把暗藏者映射为 Covert）。</summary>
        public void TestIncludeCovertTargetNotLostWhenStillCovert()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: true);
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, IncludeCovert = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid, "IncludeCovert=true 时暗藏者应仍是合法目标（翻面丢失由版本号覆盖）");
        }

        // ---------- ⑧ 墓地内卡为目标，离墓地 → 丢 ----------
        public void TestGraveyardTargetValidWhenInGraveyard()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.Graveyard, zoneDetail: "");
            var desc = new TargetSpec { Kind = TargetKind.Graveyard, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid);
        }

        public void TestGraveyardTargetLostWhenLeftGraveyard()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, zoneDetail: "area-1");
            var desc = new TargetSpec { Kind = TargetKind.Graveyard, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.LeftZone, reason);
        }

        // ---------- ⑨ 手牌卡为目标，离手牌 → 丢 ----------
        public void TestHandTargetValidWhenInHand()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.Hand, zoneDetail: "");
            var desc = new TargetSpec { Kind = TargetKind.Hand, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(valid);
        }

        public void TestHandTargetLostWhenLeftHand()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, zoneDetail: "area-1");
            var desc = new TargetSpec { Kind = TargetKind.Hand, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.LeftZone, reason);
        }

        // ---------- 玩家卡 TargetRule 经 ITargetDesc 同样可用 ----------
        public void TestPlayerCardTargetRuleViaInterface()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: true);
            var rule = TargetRule.Make(TargetKind.Character);
            var (valid, reason) = TargetValidity.Check(src, tgt, rule);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Covert, reason);
        }

        // ---------- 反应触发（Trigger）承载 EffectData 的目标丢失（对应 JC003 场景） ----------
        // 结算时 GetPrimaryTargetDesc 应直接取 entry.EffectDataEffect.Target（而非 PlayerCardData 列表）。
        // 这里直接以 EffectData.Target 作为 ITargetDesc 入参，验证翻面/离场两面均判丢失。
        public void TestTriggerEffectTargetLostWhenFlipped()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: true);   // 角色翻面成暗藏
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true }; // 模拟 EffectData.Target
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Covert, reason);
        }

        public void TestTriggerEffectTargetLostWhenUncover()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone, isCovert: false); // 暗藏者翻回正面
            var desc = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.Uncover, reason);
        }

        public void TestTriggerEffectTargetLostWhenLeftZone()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.Graveyard);   // 被 kill 后进墓地（规则③）
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            var (valid, reason) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(valid); Assert.AreEqual(TargetLossReason.LeftZone, reason);
        }

        // ============================================================
        // D16 版本号方案（2026-08-11）：取目标记录状态版本号，结算时比对不一致即判丢失。
        // 核心链路（CaptureTargetSnapshot→BumpVersion→IsTargetLost）的同程序集私有方法已由 TurnManager 内部实现，
        // 本组测试通过 CardInstance / StackEntry 的 public 字段（VersionId / TargetVersionId）与 TargetValidity.Check
        // 验证"取目标前后 + 丢失前后"的不变量，覆盖版本号比对的等价逻辑。
        // 注：private 方法 TurnManager.IsTargetLost / ResolveEntryTargetIsLost 的端到端验证见 TargetInterceptTests 红线（需集成）。
        // ============================================================

        /// <summary>取目标前：目标无任何变化时 VersionId 为初始值；取目标动作（CaptureTargetSnapshot）
        /// 将当前 VersionId 记入 StackEntry.TargetVersionId。</summary>
        public void TestCaptureSnapshotRecordsVersionBeforeAnyChange()
        {
            var tgt = NewInstance("t1", CardZone.AreaZone);
            int v0 = tgt.VersionId;                       // 取目标前：初始状态版本号
            Assert.AreEqual(0, v0, "新目标初始版本号应为 0");

            // 模拟取目标：CaptureTargetSnapshot 记录入栈版本号
            var entry = new StackEntry(1, tgt) { TargetCardUid = tgt.UID, TargetVersionId = tgt.VersionId };
            Assert.AreEqual(v0, entry.TargetVersionId, "取目标应记录当前版本号");
        }

        /// <summary>取目标后、目标状态变化：任何移动/翻面/控制者变化触发 BumpVersion → 版本号自增，
        /// 此时 entry.TargetVersionId（入栈快照）≠ 当前 VersionId → 等价"丢失"。</summary>
        public void TestTargetLostWhenVersionChangedAfterCapture()
        {
            var tgt = NewInstance("t1", CardZone.AreaZone);
            // 取目标：记录入栈版本号
            var entry = new StackEntry(1, tgt) { TargetCardUid = tgt.UID, TargetVersionId = tgt.VersionId };
            Assert.AreEqual(tgt.VersionId, entry.TargetVersionId, "取目标后版本号一致，未丢失");

            // 取目标后目标经历状态变化（如被移动/翻面/夺控/子类别变更，均经 BumpVersion）
            tgt.BumpVersion();
            bool lost = entry.TargetVersionId >= 0 && tgt.VersionId != entry.TargetVersionId;
            Assert.IsTrue(lost, "版本号变化应判丢失");
            Assert.IsFalse(entry.TargetVersionId == tgt.VersionId, "丢失后版本号应不一致");
        }

        /// <summary>取目标后、目标未变化：版本号一致 → 等价"未丢失"，可正常结算。</summary>
        public void TestTargetKeptWhenVersionUnchanged()
        {
            var tgt = NewInstance("t1", CardZone.AreaZone);
            var entry = new StackEntry(1, tgt) { TargetCardUid = tgt.UID, TargetVersionId = tgt.VersionId };
            // 取目标后无状态变化
            bool lost = entry.TargetVersionId >= 0 && tgt.VersionId != entry.TargetVersionId;
            Assert.IsFalse(lost, "版本号未变应判未丢失");
        }

        /// <summary>取目标前后合法性对比（public Check 路径）：取目标时合法；目标翻面为暗藏者后 Check 失败。</summary>
        public void TestCheckLegalBeforeAndLostAfterFlip()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone);   // 取目标时：明面角色，合法
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };

            var (validBefore, _) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(validBefore, "取目标时明面角色应合法");

            // 取目标后目标翻面为暗藏者（状态变化，等价 BumpVersion 触发）
            tgt.IsCovert = true;
            tgt.BumpVersion();
            var (validAfter, reasonAfter) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(validAfter, "翻面为暗藏者后应判丢失");
            Assert.AreEqual(TargetLossReason.Covert, reasonAfter);
        }

        /// <summary>取目标前后：目标从争夺区移动到墓地（规则③），Check 由合法变丢失（LeftZone）。</summary>
        public void TestCheckLegalBeforeAndLostAfterMoveToGraveyard()
        {
            var src = NewInstance("src", CardZone.AreaZone);
            var tgt = NewInstance("t1", CardZone.AreaZone);   // 取目标时：在场
            var desc = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };

            var (validBefore, _) = TargetValidity.Check(src, tgt, desc);
            Assert.IsTrue(validBefore, "取目标时在场应合法");

            tgt.CurrentZone = CardZone.Graveyard;            // 进墓地（状态变化）
            tgt.BumpVersion();
            var (validAfter, reasonAfter) = TargetValidity.Check(src, tgt, desc);
            Assert.IsFalse(validAfter, "进墓地后应判丢失");
            Assert.AreEqual(TargetLossReason.LeftZone, reasonAfter);
        }

        // 注（2026-08-11）：原⑩⑪"结算前控制者/子类别变化（过程记忆、快照比对）"测试段已删除——
        // 方向性记忆（ControllerChangedToEnemy/Friendly、SubCategoryGained/Lost）已废弃，
        // 相关判定改由 D16 版本号方案统一覆盖（任何控制者/子类别变化触发 BumpVersion，结算比对 TargetVersionId 不一致即丢）。
        // 若未来需恢复方向性记忆测试，应先恢复 TargetRestriction 枚举与 TargetValidity 快照分支。
    }
}
