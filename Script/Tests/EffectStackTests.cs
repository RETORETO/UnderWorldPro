using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Game.Entities;

namespace Script.Tests
{
    /// <summary>P1 — E6 效果入堆叠（逻辑正确性：进栈与字段携带）。</summary>
    public partial class EffectStackTests : TestRunner
    {
        private partial class CharCard : PlayerCardData
        {
            public CharCard()
            {
                CardName = "测试角色";
                CategoryFlag = CardCategory.Character;
                EffectDataList = new Array<EffectData>
                {
                    new EffectData
                    {
                        EffectId = "ET1",
                        Timing = EffectTiming.EnterTrigger,
                        Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                        Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Tap } },
                        Duration = "瞬时",
                    },
                };
            }
        }

        private CardInstance NewInstance(PlayerCardData tmpl, string uid = "u1")
        {
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = 0;
            inst.ControllerId = 0;
            return inst;
        }

        public void TestStackEntryCarriesEffectData()
        {
            var sm = new StackManager();
            sm.Clear();
            var data = new EffectData { EffectId = "X", Timing = EffectTiming.EnterTrigger };
            var entry = new StackEntry(0, "测试效果") { Kind = ResolveKind.Trigger, SourceCardUid = "u1", EffectDataEffect = data };
            Assert.IsNotNull(entry.EffectDataEffect, "StackEntry.EffectDataEffect 应可承载 EffectData");
            Assert.AreEqual("X", entry.EffectDataEffect.EffectId);
            sm.Push(entry);
            Assert.IsFalse(sm.IsEmpty, "压入后堆叠不应为空");
            Assert.AreEqual(1, sm.Entries.Count);
        }

        public void TestPushActivationToStackConstructsEntry()
        {
            var sm = new StackManager();
            sm.Clear();
            var card = NewInstance(new CharCard());
            var ability = new EffectData { EffectId = "ACT1", Timing = EffectTiming.CharacterActivated };
            EffectEngine.PushActivationToStack(sm, card, ability, null);
            Assert.IsFalse(sm.IsEmpty, "激活能力应压入 1 条");
            Assert.AreEqual("ACT1", sm.Top.EffectDataEffect.EffectId);
        }

        public void TestNewTriggerBypassesD4WindowRedline()
        {
            var redline = new List<string>();
            Assert.KnownRedline(true, redline,
                "旧版 PushTriggersToStack 已删除（D36）：触发统一经 TriggerRegistry.RunTriggerPhase 排序窗口入栈，不再有绕过排序窗口的直接入栈路径");
            Assert.KnownRedline(true, redline, "PushActivationToStack 当前未被 ActivateAbility 调用（D9 激活接入留待）");
        }

        /// <summary>D16 统一收口（2026-08-11）：经 StackManager.Push 入栈时，无论走触发/出牌/激活哪条路径，
        /// 只要填了 TargetCardUid 且 StackManager 已注入 CardManager，就自动登记目标版本号到 TargetVersionId，
        /// 供结算时 D16 比对（目标翻面/移动/夺控 → 版本号不一致即判丢失）。此前激活能力入栈漏记本字段。</summary>
        public void TestPushAutoRecordsTargetVersionId()
        {
            var cm = new CardManager();
            cm._Ready();
            var sm = new StackManager();
            sm.Clear();
            sm.SetCardManager(cm);                       // 注入 CardManager（运行时等价）

            // 用 CardManager 工厂创建，使目标实例注册到 cm（GetInstance 可查），模拟运行时
            var target = cm.CreateCardInstance(new CharCard(), 0);
            int v0 = target.VersionId;                   // 入栈前版本号
            var src = cm.CreateCardInstance(new CharCard(), 0);

            // 激活能力（PushActivationToStack）此前绕过 CaptureTargetSnapshot，是漏记重灾区
            var ability = new EffectData { EffectId = "ACT1", Timing = EffectTiming.CharacterActivated };
            EffectEngine.PushActivationToStack(sm, src, ability, target);

            Assert.IsFalse(sm.IsEmpty, "激活能力应压入 1 条");
            var entry = sm.Top;
            Assert.AreEqual(target.UID, entry.TargetCardUid, "目标 UID 应已记录");
            Assert.AreEqual(v0, entry.TargetVersionId, "Push 应自动登记入栈时目标版本号（D16 统一收口）");
        }

        /// <summary>D16 属性复检（2026-08-16 起 Enemy/子类别/费用下沉到 TargetValidity.Check 单一真相源）：
        /// 目标被夺控换边（敌方→己方）且争夺区→争夺区移动不刷版本号时，由 Check 的 Enemy Restrictions 拦截——
        /// JC015 消灭"敌方"角色，目标变己方后应判丢失。版本号机制无法覆盖"夺控换边不刷版本号"的场景由属性复检兜底。</summary>
        public void TestResolveRejectsTargetAfterControlTransfer()
        {
            var cm = new CardManager();
            cm._Ready();
            // 来源卡（模拟 JC015 驱魔人）：控制者=玩家1（己方），位于争夺区
            var src = cm.CreateCardInstance(new PlayerCardData { CardName = "驱魔人", CategoryFlag = CardCategory.Character }, 1);
            src.CurrentZone = CardZone.AreaZone;
            src.ZoneDetailId = "areaA";
            // 目标：初始敌方（玩家2控制），位于争夺区
            var tgt = cm.CreateCardInstance(new PlayerCardData { CardName = "血族长老", CategoryFlag = CardCategory.Character }, 2);
            tgt.CurrentZone = CardZone.AreaZone;
            tgt.ZoneDetailId = "areaA";

            // 激活目标规则：Character + Enemy 限制（单一真相源，经 CheckRestrictions 复检）
            var desc = new TargetSpec
            {
                Kind = TargetKind.Character,
                NeedsSelection = true,
                Restrictions = new Godot.Collections.Array<TargetRestriction> { TargetRestriction.Enemy },
            };
            // 来源控制者快照=1（入栈时），Enemy 以此为准，来源事后被夺控不改变敌我关系
            var snap = new TargetValidity.TargetSnapshot("areaA", 1);

            // 入栈时（目标敌方=玩家2 vs 来源=玩家1）：Enemy 通过 → 合法
            var before = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsTrue(before.valid, $"夺控前目标敌方，应合法（reason={before.reason}）");

            // 争夺区→争夺区夺控换边：控制者 2→1，版本号不刷（模拟 FireMoved bump=False），变为己方
            tgt.ControllerId = 1;
            var after = TargetValidity.Check(src, tgt, desc, snap);
            Assert.IsFalse(after.valid, "夺控换边成己方后，Enemy 属性复检应判丢失");
        }
    }
}
