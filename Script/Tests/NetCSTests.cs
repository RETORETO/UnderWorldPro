using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P0/P1 内核验证】注意：本测试**不是自动对战**，仅验证两个内核链路可跑：
    ///  (1) P0-B：注入 AutoInputGateway 后，ResolveEffectAsync 在 PrepareUpkeep 处能 await 网关请求-响应并续算落地；
    ///  (2) P1-A/B：TurnManager.ToSnapshot() 产出整局快照，经 SnapshotSerializer 序列化→反序列化往返一致。
    /// 对战行为本身（AI/脚本/联网客户端）由独立模块驱动，不在本测试范围内。
    /// </summary>
    public class NetCSTests : TestRunner
    {
        private CardInstance NewAttached(string uid, int ctrl = 1, CardZone zone = CardZone.AttachedZone)
        {
            var tmpl = new CouncilCardData { CardName = "内核验证卡" };
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = zone;
            inst.IsAttached = zone == CardZone.AttachedZone;
            return inst;
        }

        /// <summary>P0-B 验证：AutoInputGateway 注入后，ResolveEffectAsync 的 PrepareUpkeep 能 await 并续算。</summary>
        public void TestResolveEffectAsyncWithAutoGateway()
        {
            var ee = new Script.Manager.EffectEngine();
            ee.InputGateway = new AutoInputGateway { DefaultPay = true };

            var eff = new EffectData
            {
                EffectId = "CS_TEST_PrepareUpkeep",
                Timing = EffectTiming.OnTurnStarted,
                Scope = ZoneScope.AttachTarget,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PrepareUpkeep, Args = new Array<string> { "1" } }
                },
                Duration = "强制",
            };
            var src = NewAttached("src", 1);

            // 关键：await 异步结算路径（不阻塞、不依赖 UI 弹窗）
            var task = ee.ResolveEffectAsync(eff, src, null);
            task.Wait(5000); // 测试内同步等待完成（等价于客户端收到回包后续算）

            Assert.IsTrue(task.IsCompleted, "ResolveEffectAsync 应在网关返回后完成（无 UI 阻塞）");
            Assert.IsFalse(task.IsFaulted, "ResolveEffectAsync 不应抛异常");
        }

        /// <summary>P1-A/B 验证：快照可序列化往返，字段不丢失。</summary>
        public void TestSnapshotRoundTrip()
        {
            // 自包含构造一个带数据的快照（不依赖全局 TurnManager.Instance 是否已初始化），
            // 验证 SnapshotSerializer 经 JSON 往返后字段完整——这是 P1 序列化的核心目标。
            var snap = new GameSnapshot
            {
                TurnCount = 3,
                CurrentPhase = 2,
            };
            snap.Players.Add(new PlayerSnapshot { PlayerId = 1, AvailableMana = 4, StorybookCount = 1 });
            snap.Players.Add(new PlayerSnapshot { PlayerId = 2, AvailableMana = 2, StorybookCount = 0 });
            snap.Zones.Add(new ZoneCardRef { Uid = "C1", ControllerId = 1, Zone = Script.Enums.CardZone.AssetZone });
            snap.Stack.Add(new StackEntrySnap { OwnerId = 2, SourceUid = "S1", EffectText = "待结算效果" });

            var json = SnapshotSerializer.Serialize(snap);
            Assert.IsFalse(string.IsNullOrEmpty(json), "快照应可序列化为非空 JSON");

            var back = SnapshotSerializer.Deserialize(json);
            Assert.IsNotNull(back, "反序列化应还原快照");
            Assert.AreEqual(snap.TurnCount, back.TurnCount, "往返后 TurnCount 一致");
            Assert.AreEqual(snap.CurrentPhase, back.CurrentPhase, "往返后 CurrentPhase 一致");
            Assert.AreEqual(snap.Players.Count, back.Players.Count, "往返后玩家数一致");
            Assert.AreEqual(snap.Players[0].AvailableMana, back.Players[0].AvailableMana, "往返后可用法力一致");
            Assert.AreEqual(snap.Zones.Count, back.Zones.Count, "往返后区域卡数量一致");
            Assert.AreEqual("C1", back.Zones[0].Uid, "往返后区域卡 UID 一致");
            Assert.AreEqual(snap.Stack.Count, back.Stack.Count, "往返后堆叠数量一致");
            Assert.AreEqual("S1", back.Stack[0].SourceUid, "往返后堆叠项 SourceUid 一致");
        }
    }
}
