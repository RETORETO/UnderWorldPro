using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P3·模块B】客户端渲染切换·快照渲染管道验证：
    ///  (1) SnapshotDiff 空→快照 全量 Add；
    ///  (2) SnapshotDiff 同快照 无指令（幂等）；
    ///  (3) SnapshotDiff 卡移动/属性变化/堆叠/玩家/阶段 产出对应增量指令；
    ///  (4) SnapshotApplicationQueue：同帧多快照合并、延后窗口、与"上一份已应用快照"配对（表现层事件源）。
    /// 注：差异算法与快照应用队列均纯托管，不依赖 Godot 场景；ArenaOl 的表现层接入（差异指令路由）由联机冒烟覆盖。
    /// </summary>
    public class NetP3BTests : TestRunner
    {
        private static ZoneCardRef Card(string uid, CardZone zone, int owner = -1, int dmg = 0, bool tapped = false, bool covert = false)
            => new ZoneCardRef { Uid = uid, Zone = zone, ZoneOwnerId = owner, Damage = dmg, IsTapped = tapped, IsCovert = covert };

        /// <summary>(1) 空态 → 快照：全量 Add + 首次阶段信息（首包初始化语义）。</summary>
        public void TestSnapshotDiffInitialAllAdd()
        {
            var next = new GameSnapshot { Zones = new List<ZoneCardRef> { Card("a", CardZone.Hand, 1) } };
            var cmds = SnapshotDiff.Diff(null, next);

            // 首包从空态：应产出 1 条 AddCard（卡） + 1 条 PhaseChange（首次获知阶段）
            Assert.AreEqual(2, cmds.Count, "空→快照应产出 AddCard + PhaseChange 共 2 条");
            Assert.IsTrue(cmds.Exists(x => x.Kind == RenderCommand.CmdKind.AddCard), "应含 AddCard");
            Assert.IsTrue(cmds.Exists(x => x.Kind == RenderCommand.CmdKind.PhaseChange), "首包应含 PhaseChange(首次获知阶段)");
            var c = (CardRenderCommand)cmds.Find(x => x.Kind == RenderCommand.CmdKind.AddCard);
            Assert.AreEqual("a", c.Uid, "AddCard Uid 应一致");
            Assert.AreEqual(CardZone.Hand, c.Zone, "AddCard Zone 应一致");
        }

        /// <summary>(2) 相同快照：无指令（幂等）。</summary>
        public void TestSnapshotDiffIdenticalNoOp()
        {
            var snap = new GameSnapshot
            {
                TurnCount = 3,
                PriorityPlayerId = 1,
                CurrentPhase = (int)TurnManager.TurnPhase.ActionFirst,
                Zones = new List<ZoneCardRef> { Card("a", CardZone.AreaZone, 1, dmg: 2, tapped: true) },
                Players = new List<PlayerSnapshot> { new PlayerSnapshot { PlayerId = 1, AvailableMana = 5 } },
            };
            var cmds = SnapshotDiff.Diff(snap, snap);
            Assert.AreEqual(0, cmds.Count, "相同快照不应产出增量指令");
        }

        /// <summary>(3) 卡移动 + 属性变化 + 玩家 + 阶段变化。</summary>
        public void TestSnapshotDiffIncremental()
        {
            var prev = new GameSnapshot
            {
                TurnCount = 3,
                PriorityPlayerId = 1,
                CurrentPhase = (int)TurnManager.TurnPhase.ActionFirst,
                Zones = new List<ZoneCardRef>
                {
                    Card("a", CardZone.Hand, 1),
                    Card("b", CardZone.AreaZone, 1, dmg: 2),
                },
                Players = new List<PlayerSnapshot> { new PlayerSnapshot { PlayerId = 1, AvailableMana = 5 } },
                Stack = new List<StackEntrySnap>(),
            };
            var next = new GameSnapshot
            {
                TurnCount = 3,
                PriorityPlayerId = 2,           // 优先权变化
                CurrentPhase = (int)TurnManager.TurnPhase.ActionFirst,
                Zones = new List<ZoneCardRef>
                {
                    Card("a", CardZone.AreaZone, 1, dmg: 3, tapped: true),  // a 移动 + 属性变化
                    Card("c", CardZone.Hand, 1),                             // c 新增
                    // b 消失
                },
                Players = new List<PlayerSnapshot>
                {
                    new PlayerSnapshot { PlayerId = 1, AvailableMana = 3 },  // 法力 5→3
                },
                Stack = new List<StackEntrySnap> { new StackEntrySnap { OwnerId = 1, SourceUid = "a", EffectText = "E1" } },
            };

            var cmds = SnapshotDiff.Diff(prev, next);
            var kinds = new List<RenderCommand.CmdKind>();
            foreach (var c in cmds) kinds.Add(c.Kind);

            Assert.IsTrue(kinds.Contains(RenderCommand.CmdKind.MoveCard), "a 移动应产 MoveCard");
            Assert.IsTrue(kinds.Contains(RenderCommand.CmdKind.AddCard), "c 新增应产 AddCard");
            Assert.IsTrue(kinds.Contains(RenderCommand.CmdKind.RemoveCard), "b 消失应产 RemoveCard");
            Assert.IsTrue(kinds.Contains(RenderCommand.CmdKind.PlayerUpdate), "法力变化应产 PlayerUpdate");
            Assert.IsTrue(kinds.Contains(RenderCommand.CmdKind.PhaseChange), "优先权变化应产 PhaseChange");
            Assert.IsTrue(kinds.Contains(RenderCommand.CmdKind.StackPush), "堆叠新增应产 StackPush");

            // 卡 a 的 MoveCard 应携带新属性
            var moveA = cmds.Find(x => x is CardRenderCommand cc && cc.Kind == RenderCommand.CmdKind.MoveCard && cc.Uid == "a") as CardRenderCommand;
            Assert.IsTrue(moveA != null && moveA.Damage == 3 && moveA.IsTapped, "MoveCard 应携带新属性(Damage=3,Tap)");
        }

        /// <summary>(4a) 同一帧内多份快照：只保留最后一份（合并），且不与已应用快照重复应用。</summary>
        public void TestSnapshotQueueCoalescing()
        {
            var q = new SnapshotApplicationQueue();
            var s1 = new GameSnapshot { TurnCount = 1 };
            var s2 = new GameSnapshot { TurnCount = 2 };
            var s3 = new GameSnapshot { TurnCount = 3 };

            q.Enqueue(s1); q.Enqueue(s2); q.Enqueue(s3);
            Assert.AreEqual(3, q.EnqueueCount, "入队次数应记 3");
            Assert.AreEqual(2, q.CoalescedCount, "同帧 3 份应合并掉 2 份");
            Assert.IsTrue(q.HasPending, "应有待应用快照");

            Assert.IsTrue(q.TryDequeue(out var prev, out var next), "待应用快照应可出队");
            Assert.IsTrue(prev == null, "首次应用时 prev 应为 null（Diff 视作空初始态）");
            Assert.IsTrue(ReferenceEquals(next, s3), "应只应用最后一份快照（s3）");
            Assert.IsFalse(q.HasPending, "出队后队列应为空");
        }

        /// <summary>(4b) 与"上一份已应用快照"配对：出队给出 prev/next，供 SnapshotDiff 计算增量。</summary>
        public void TestSnapshotQueuePairing()
        {
            var q = new SnapshotApplicationQueue();
            var s1 = new GameSnapshot { TurnCount = 1, Zones = new List<ZoneCardRef> { Card("a", CardZone.Hand, 1) } };
            var s2 = new GameSnapshot { TurnCount = 2, Zones = new List<ZoneCardRef> { Card("a", CardZone.AreaZone, 1) } };

            q.Enqueue(s1);
            Assert.IsTrue(q.TryDequeue(out var p0, out var n1), "首包应出队");
            Assert.IsTrue(p0 == null, "首包 prev 为 null");
            q.MarkApplied(n1);

            q.Enqueue(s2);
            Assert.IsTrue(q.TryDequeue(out var p1, out var n2), "第二包应出队");
            Assert.IsTrue(ReferenceEquals(p1, s1), "第二包 prev 应为已应用的 s1");

            var cmds = SnapshotDiff.Diff(p1, n2);
            Assert.IsTrue(cmds.Exists(c => c.Kind == RenderCommand.CmdKind.MoveCard), "配对结果应能直接喂给 SnapshotDiff 产出 MoveCard");
        }

        /// <summary>(4c) 延后窗口：IsDeferred=true 时不应用但保留快照（动画/本地交互窗口期不丢快照）。</summary>
        public void TestSnapshotQueueDeferred()
        {
            var q = new SnapshotApplicationQueue();
            var s1 = new GameSnapshot { TurnCount = 1 };
            q.Enqueue(s1);
            q.IsDeferred = true;

            Assert.IsFalse(q.TryDequeue(out _, out _), "延后窗口内不应出队");
            Assert.IsTrue(q.HasPending, "延后期间快照应保留在队列中");

            q.IsDeferred = false;
            Assert.IsTrue(q.TryDequeue(out _, out var next), "解除延后应可出队");
            Assert.IsTrue(ReferenceEquals(next, s1), "解除延后应应用保留的那份快照");

            q.Reset();
            Assert.IsFalse(q.HasPending, "Reset 后应无待应用快照");
            Assert.IsTrue(q.Applied == null, "Reset 后基准快照应清空");
        }

    }
}
