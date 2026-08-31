using System.Collections.Generic;
using Godot;
using Script.Game;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P3·模块E】断线重连验证（前置条件 ⑤ 收口）：
    ///  (1) DeterministicRandom 可复原：同种子连续消耗 N 次后，Restore(seed, N) 精确回到中间态，后续输出与不重连一致；
    ///  (2) RandomSourceHub 多子流：步数隔离 + Snapshot/Restore 后各流输出一致；
    ///  (3) ReconnectManager：服务端 BuildPayload 注入随机状态到快照，客户端 ApplyPayload 复原渲染 + 读出挂起输入。
    /// 注：全部为纯托管逻辑，不依赖 Godot 场景；真实传输层接入由后续联机端到端覆盖。
    /// </summary>
    public class NetP3ETests : TestRunner
    {
        /// <summary>(1) DeterministicRandom 可复原到任意中间态。</summary>
        public void TestDeterministicRandomRestore()
        {
            // 连续消耗：记录每步输出
            var a = new DeterministicRandom(12345);
            var outputs = new List<int>();
            for (int i = 0; i < 100; i++) outputs.Add(a.Next(1000));
            Assert.AreEqual(100, a.Step, "连续消耗 100 次后 Step 应为 100");

            // 复原到中间态（第 60 步）
            var b = new DeterministicRandom(12345);
            b.Restore(12345, 60);
            Assert.AreEqual(60, b.Step, "Restore 后 Step 应为 60");

            // 复原后从第 60 步继续，应与 a 从第 60 步起的输出一致
            for (int i = 60; i < 100; i++)
                Assert.AreEqual(outputs[i], b.Next(1000), $"第 {i} 步复原后输出应一致");
        }

        /// <summary>(1b) Shuffle 也计入步数且可复原。</summary>
        public void TestDeterministicRandomShuffleRestore()
        {
            var seed = 777;
            var items = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            var r1 = new DeterministicRandom(seed);
            var shuffled = new List<int>(items);
            r1.Shuffle(shuffled);
            long stepsAfterShuffle = r1.Step;
            Assert.IsTrue(stepsAfterShuffle > 0, "Shuffle 应消耗随机步数");

            // 复原后重新洗，步数一致且洗牌结果一致
            var r2 = new DeterministicRandom(seed);
            var shuffled2 = new List<int>(items);
            r2.Shuffle(shuffled2);
            Assert.AreEqual(stepsAfterShuffle, r2.Step, "重洗步数应一致");
            for (int i = 0; i < shuffled.Count; i++)
                Assert.AreEqual(shuffled[i], shuffled2[i], $"洗牌第 {i} 位应一致（同种子可复现）");
        }

        /// <summary>(2) RandomSourceHub 多子流快照/复原。</summary>
        public void TestRandomSourceHubSnapshotRestore()
        {
            var hub = new RandomSourceHub(42);
            var shuffleStream = hub.GetStream("shuffle");
            var uidStream = hub.GetStream("uid");

            // 消耗各流
            shuffleStream.Next(10);
            shuffleStream.Next(10);
            uidStream.Next(100);
            long shuffleSteps = shuffleStream.Step;
            long uidSteps = uidStream.Step;

            // 导出快照
            var snap = hub.Snapshot();
            Assert.AreEqual(2, snap.StreamSteps.Count, "应有 2 个子流");
            Assert.AreEqual(shuffleSteps, snap.StreamSteps["shuffle"], "shuffle 流步数应记录");
            Assert.AreEqual(uidSteps, snap.StreamSteps["uid"], "uid 流步数应记录");

            // 复原到新 Hub，各流应从快照步数继续（输出一致）
            var hub2 = new RandomSourceHub(999); // 不同种子，但 Restore 会用快照种子覆盖
            hub2.Restore(snap);
            var s2 = hub2.GetStream("shuffle");
            var u2 = hub2.GetStream("uid");
            Assert.AreEqual(shuffleSteps, s2.Step, "复原后 shuffle Step 应一致");
            Assert.AreEqual(uidSteps, u2.Step, "复原后 uid Step 应一致");

            // 复原后继续输出应与原 hub 继续输出一致
            var hub1 = hub; // 原 hub 继续
            for (int i = 0; i < 5; i++)
                Assert.AreEqual(hub1.GetStream("shuffle").Next(100), s2.Next(100), $"shuffle 第 {i} 步应一致");
        }

        /// <summary>(3) ReconnectManager 服务端载荷 + 客户端应用（含 PendingInput）。</summary>
        public void TestReconnectManagerPayload()
        {
            // 服务端：装配随机 Hub + 快照提供委托
            var serverHub = new RandomSourceHub(2026);
            var serverShuffle = serverHub.GetStream("shuffle");
            for (int i = 0; i < 50; i++) serverShuffle.Next(100); // 服务端已消耗 50 步

            GameSnapshot captured = null;
            ReconnectManager server = new ReconnectManager(
                () =>
                {
                    captured = new GameSnapshot
                    {
                        TurnCount = 7,
                        PriorityPlayerId = 2,
                        PendingInput = new ChoiceRequest { RequestId = "req_9", PlayerId = 2, Prompt = "请选择目标" },
                    };
                    return captured;
                },
                snap => { }, // 服务端侧无需渲染
                serverHub);

            // 服务端构建重连载荷（应注入随机状态）
            var payload = server.BuildPayload(reconnectPlayerId: 2, hasGame: true);
            Assert.IsTrue(payload.HasGame, "应仍有对局");
            Assert.IsNotNull(payload.Random, "载荷应含随机快照");
            Assert.AreEqual(50, payload.Random.StreamSteps["shuffle"], "随机快照应含服务端已消耗步数");
            Assert.AreEqual("req_9", payload.Snapshot.PendingInput.RequestId, "载荷快照应含挂起输入");

            // 客户端：应用载荷 → 复原渲染 + 读出挂起输入
            GameSnapshot applied = null;
            var clientHub = new RandomSourceHub(0);
            ReconnectManager client = new ReconnectManager(
                () => null,
                snap => applied = snap);
            var pending = client.ApplyPayload(payload);
            Assert.IsNotNull(applied, "客户端应应用快照");
            Assert.AreEqual(7, applied.TurnCount, "客户端快照回合数应一致");
            Assert.IsNotNull(pending, "应读出挂起输入");
            Assert.AreEqual("req_9", pending.RequestId, "挂起输入 RequestId 应一致");

            // 客户端随机复原：与原服务端 Hub 后续输出对齐
            ReconnectManager.RestoreClientRng(clientHub, payload);
            var clientStream = clientHub.GetStream("shuffle");
            var serverStream = serverHub.GetStream("shuffle");
            for (int i = 0; i < 5; i++)
                Assert.AreEqual(serverStream.Next(100), clientStream.Next(100), $"重连后第 {i} 步随机应对齐");
        }
    }
}
