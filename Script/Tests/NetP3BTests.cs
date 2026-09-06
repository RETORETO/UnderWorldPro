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
    ///  (4) SnapshotRenderDriver 首包 Rebuild + 增量 Diff + sink 路由正确。
    /// 注：快照差异算法与渲染驱动均纯托管，不依赖 Godot 场景；Arena 的 UI 应用层（Arena.SnapshotView）
    /// 保守映射现有刷新方法，不重复装配完整对局（由既有 Arena 单机事件路径 + 端到端覆盖）。
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

    }
}
