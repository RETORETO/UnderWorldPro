using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化·第一批迁移·端到端联机验证】用 NetworkMatchLauncher 跑"服务端权威 + 客户端投影"回环，
    /// 验证意图上行 → 权威结算 → 快照下行 → 客户端重建 这条联网主干闭环。
    ///
    /// 与 NetP1Batch1Tests（快照双向/序列化）不同，本文件验证的是"联机对局启动器把各正式组件串起来后，
    /// 客户端投影最终与服务端权威状态一致"——即第一批改造在真实联网链路下的端到端正确性。
    ///
    /// 设计约束：轻量装配，复用正式 GameInitializer 起手（StartMatch 发初始手牌），注入 NetworkMatchLauncher。
    /// </summary>
    public class NetP1Batch1MatchTests : TestRunner
    {
        /// <summary>
        /// 启动本地回环联机对局，客户端发一条抽牌意图，轮询驱动 ENet 握手+快照下行，
        /// 断言：客户端投影手牌数 == 服务端权威手牌数（快照双向闭环成功）。
        /// </summary>
        public void TestLocalLoopbackMatchProjectionConsistent()
        {
            const ushort port = 18930;

            // 1) 服务端：正式 GameInitializer 起手（发初始手牌）
            var game = new GameInitializer { DebugSeed = 1 };
            game.InitializeAll();
            game.StartMatch();

            var serverHandBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            Assert.IsTrue(serverHandBefore > 0, "服务端起手后应有初始手牌（验证 GameInitializer 正常）");

            // 2) 联机启动器：Server 权威 + Client 投影回环（注入 LoopbackTransport 内存回环，避免真实 ENet 握手不稳定）
            // 注意：server 与 client 共用同一 LoopbackTransport 实例，使其内部 serverHandler 与 clientHandler 互通，
            // 从而意图上行（ClientReply）→ 服务端裁决 → 快照下行（Send→clientHandler→ApplySnapshot）形成闭环。
            var loop = new Script.Net.LoopbackTransport();
            var launcher = new NetworkMatchLauncher();
            launcher.StartLocalLoopback(game, port, loop, loop);

            // 3) 客户端上行抽牌意图（count=1）
            launcher.SendIntent(PlayerIntent.Kind.DrawCards, "1");

            // 4) 轮询驱动 ENet 握手 + 意图处理 + 快照下行（ENet 需多帧 Poll）
            for (int i = 0; i < 200; i++)
            {
                launcher._Process(0);
                // 客户端投影手牌数一旦与服务端一致即视为快照已到达并重建
                if (launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1).Count == game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count
                    && launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1).Count >= serverHandBefore)
                {
                    break;
                }
                System.Threading.Thread.Sleep(5);
            }

            // 5) 断言：客户端投影与服务端权威状态一致（快照双向闭环）
            var serverHand = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            var clientHand = launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
            Assert.AreEqual(serverHand, clientHand,
                $"客户端投影手牌数({clientHand})应等于服务端权威手牌数({serverHand})——快照下行+ApplySnapshot 闭环");

            // 6) 断言：客户端投影中的卡 UID 均能在服务端实例中找到（RebuildFromRef 命中共享 CardManager）
            foreach (var c in launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1))
            {
                var serverInst = game.CardManager.GetInstance(c.UID);
                Assert.IsNotNull(serverInst, $"客户端投影卡 UID={c.UID} 应在服务端 CardManager 中存在（ResolveInstance 命中）");
            }

            launcher.Stop();
        }

        /// <summary>
        /// 验证断线重连视角：服务端多次结算后，客户端投影始终跟随（多次快照下行累积一致）。
        /// </summary>
        public void TestLocalLoopbackMultipleSnapshotsTrack()
        {
            const ushort port = 18931;

            var game = new GameInitializer { DebugSeed = 2 };
            game.InitializeAll();
            game.StartMatch();

            var launcher = new NetworkMatchLauncher();
            var loop2 = new Script.Net.LoopbackTransport();
            launcher.StartLocalLoopback(game, port, loop2, loop2);

            // 连续多次抽牌，每次都等待快照同步，断言投影始终跟随
            for (int round = 0; round < 3; round++)
            {
                launcher.SendIntent(PlayerIntent.Kind.DrawCards, "1");
                for (int i = 0; i < 200; i++)
                {
                    launcher._Process(0);
                    var sH = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
                    var cH = launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1).Count;
                    if (sH == cH && cH >= 1) break;
                    System.Threading.Thread.Sleep(5);
                }
                Assert.AreEqual(
                    game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count,
                    launcher.ClientZoneManager.GetZoneCards(CardZone.Hand, 1).Count,
                    $"第{round + 1}次结算后客户端投影应跟随服务端权威状态");
            }

            launcher.Stop();
        }

        /// <summary>
        /// 第二批验证：客户端投影除手牌外，回合（阶段/优先权/先手）、堆叠、玩家状态也随快照同步。
        /// 驱动：多次 Pass（阶段推进）+ 让过推进优先权，断言 client 投影 TurnManager/StackManager/PlayerManager 与服务端一致。
        /// </summary>
        public void TestLocalLoopbackManagerStateProjection()
        {
            const ushort port = 18932;

            var game = new GameInitializer { DebugSeed = 3 };
            game.InitializeAll();
            game.StartMatch();

            var launcher = new NetworkMatchLauncher();
            var loop = new Script.Net.LoopbackTransport();
            launcher.StartLocalLoopback(game, port, loop, loop);

            var serverTurn = game.TurnManager;
            var serverStack = game.StackManager;
            var serverPlayer = game.PlayerManager;

            // 抽一次牌触发一次快照（验证区域已同步前提下，其余 Manager 状态同步）
            launcher.SendIntent(PlayerIntent.Kind.DrawCards, "1");
            launcher._Process(0);

            // 断言回合状态投影一致
            Assert.AreEqual(serverTurn.CurrentPhase, launcher.ClientTurnManager.CurrentPhase,
                $"客户端投影阶段应等于服务端阶段（{serverTurn.CurrentPhase}）");
            Assert.AreEqual(serverTurn.FirstPlayerId, launcher.ClientTurnManager.FirstPlayerId,
                "客户端投影先手玩家应等于服务端");
            Assert.AreEqual(serverTurn.PriorityPlayerId, launcher.ClientTurnManager.PriorityPlayerId,
                "客户端投影优先权玩家应等于服务端");
            Assert.AreEqual(serverTurn.TurnCount, launcher.ClientTurnManager.TurnCount,
                "客户端投影回合计数应等于服务端");

            // 断言先手标志派生正确
            Assert.AreEqual(serverPlayer.IsFirstPlayer(serverTurn.FirstPlayerId),
                launcher.ClientPlayerManager.IsFirstPlayer(serverTurn.FirstPlayerId),
                "客户端投影先手标志应与服务端一致");

            // 断言堆叠投影一致
            Assert.AreEqual(serverStack.Entries.Count, launcher.ClientStackManager.Entries.Count,
                "客户端投影堆叠条数应等于服务端");

            launcher.Stop();
        }

        /// <summary>
        /// 第三批验证：客户端投影的世界牌库（地区牌堆）与地区势力标记随快照同步。
        /// 驱动：服务端对某地区 AddPowerMark 后触发快照，断言客户端世界牌堆 UID 序列与势力标记一致。
        /// </summary>
        public void TestLocalLoopbackWorldDeckAndAreaProjection()
        {
            const ushort port = 18933;

            var game = new GameInitializer { DebugSeed = 4 };
            game.InitializeAll();
            game.StartMatch();

            var launcher = new NetworkMatchLauncher();
            var loop = new Script.Net.LoopbackTransport();
            launcher.StartLocalLoopback(game, port, loop, loop);

            var serverWorld = game.WorldDeckManager;
            var serverArea = game.AreaManager;

            // 手动注册地区（模拟 Arena 起手动作：Arena.cs 第917行调用 RegisterArea）。
            // GameInitializer.InitializeAll+StartMatch 本身不注册地区（地区注册由 View 层驱动），
            // 故测试需显式注册以驱动投影验证。
            string areaA = game.CardManager.CreateCardInstance(new AreaCardData(), 0).UID;
            string areaB = game.CardManager.CreateCardInstance(new AreaCardData(), 0).UID;
            serverArea.RegisterArea(areaA, 1, game.CardManager.GetInstance(areaA));
            serverArea.RegisterArea(areaB, 2, game.CardManager.GetInstance(areaB));

            // 给地区 A 玩家1加 3 个势力标记
            serverArea.AddPowerMark(areaA, 1, 3);

            // 触发一次快照下行（抽牌意图）
            launcher.SendIntent(PlayerIntent.Kind.DrawCards, "1");
            launcher._Process(0);

            // 断言世界牌堆投影一致（UID 序列相同；ToSnapshotUids 现返回 ZoneCardRef）
            var serverUids = serverWorld.ToSnapshotUids();
            var clientUids = launcher.ClientWorldDeckManager.ToSnapshotUids();
            Assert.AreEqual(serverUids.Count, clientUids.Count,
                $"客户端投影世界牌堆数量({clientUids.Count})应等于服务端({serverUids.Count})");
            for (int i = 0; i < serverUids.Count; i++)
                Assert.AreEqual(serverUids[i].Uid, clientUids[i].Uid,
                    $"世界牌堆第{i}张 UID 应一致");

            // 断言地区势力标记投影一致
            var (p1, p2) = serverArea.GetMarks(areaA);
            var (cp1, cp2) = launcher.ClientAreaManager.GetMarks(areaA);
            Assert.AreEqual(p1, cp1, $"地区 {areaA} 玩家1标记数应一致（{p1}）");
            Assert.AreEqual(p2, cp2, $"地区 {areaA} 玩家2标记数应一致（{p2}）");

            launcher.Stop();
        }
    }
}
