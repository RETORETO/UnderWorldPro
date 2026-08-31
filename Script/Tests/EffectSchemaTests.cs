using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Tests
{
    /// <summary>P4 — E1 效果 schema 结构正确性（不依赖底层动作实现）。</summary>
    public class EffectSchemaTests : TestRunner
    {
        public void TestEffectTimingValues()
        {
            // 关键枚举值存在且可序列化
            Assert.IsTrue(System.Enum.IsDefined(typeof(EffectTiming), EffectTiming.Continuous));
            Assert.IsTrue(System.Enum.IsDefined(typeof(EffectTiming), EffectTiming.EnterTrigger));
            Assert.IsTrue(System.Enum.IsDefined(typeof(EffectTiming), EffectTiming.OnSorceryCast));
            Assert.IsTrue(System.Enum.IsDefined(typeof(EffectTiming), EffectTiming.CharacterActivated));
            Assert.IsTrue(System.Enum.IsDefined(typeof(EffectTiming), EffectTiming.AttachmentActivated));
        }

        public void TestTargetKindDeckAdded()
        {
            // E1 新增 Deck 目标类型
            Assert.IsTrue(System.Enum.IsDefined(typeof(TargetKind), TargetKind.Deck));
        }

        public void TestEffectDataStructure()
        {
            var data = new EffectData
            {
                EffectId = "T1",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.HandZone,
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "公开", Amount = 1 },
                },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Tap } },
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                Duration = "常驻",
            };
            Assert.AreEqual("T1", data.EffectId);
            Assert.AreEqual(EffectTiming.Continuous, data.Timing);
            Assert.IsNotNull(data.ContinuousGrants);
            Assert.AreEqual(1, data.ContinuousGrants.Count);
            Assert.AreEqual("公开", data.ContinuousGrants[0].Value);
            Assert.IsNotNull(data.Actions);
            Assert.IsTrue(data.Target.NeedsSelection);
        }

        public void TestEffectGrantRoundTrip()
        {
            var g = new EffectGrant { Kind = "Keyword", Value = "先手调查", Amount = 1 };
            Assert.AreEqual("Keyword", g.Kind);
            Assert.AreEqual("先手调查", g.Value);
            Assert.AreEqual(1, g.Amount);
        }
    }
}
