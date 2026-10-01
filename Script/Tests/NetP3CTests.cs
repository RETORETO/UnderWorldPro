using System.Collections.Generic;
using Godot;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P3·模块C】意图指令通道验证：
    ///  (1) 序列化往返：PlayerIntent / IntentResult 经 JSON（IncludeFields）字段不丢（含枚举 Kind / Args / AssignedUids）；
    ///  (2) 服务端权威身份校验：IntentHub 必须拒绝"声明玩家编号 ≠ 连接映射编号"的越权意图（防代操作）；
    ///  (3) 通道闭环：经 LoopbackTransport，客户端 SendIntent 上行 → 服务端 HandleIntent 裁决 → IntentResult 下行。
    /// 注意：合法意图的"业务执行正确性"由各 Manager 既有单测 + 后续模块B 端到端覆盖，本测试不重复装配完整对局。
    /// </summary>
    public class NetP3CTests : TestRunner
    {
        /// <summary>(1) 序列化往返：所有 Kind 枚举值 + Args + AssignedUids 不丢。</summary>
        public void TestIntentSerializerRoundTrip()
        {
            var intent = PlayerIntent.Create(2, PlayerIntent.Kind.DeployCovert, "inst_5", "areaA");
            var json = IntentSerializer.SerializeIntent(intent);
            var back = IntentSerializer.DeserializeIntent(json);

            Assert.AreEqual(2, back.PlayerId, "PlayerId 往返应一致");
            Assert.AreEqual(PlayerIntent.Kind.DeployCovert, back.IntentKind, "IntentKind 枚举往返应一致（IncludeFields 关键）");
            Assert.IsTrue(back.Args.Count == 2 && back.Args[0] == "inst_5" && back.Args[1] == "areaA",
                "Args 列表往返应一致");
            Assert.IsFalse(string.IsNullOrEmpty(back.IntentId), "IntentId 应保留（correlation id）");

            var result = new IntentResult
            {
                IntentId = intent.IntentId,
                Success = true,
                AssignedUids = new List<string> { "inst_99", "inst_100" },
            };
            var rjson = IntentSerializer.SerializeResult(result);
            var rback = IntentSerializer.DeserializeResult(rjson);
            Assert.IsTrue(rback.Success, "Result.Success 应保留");
            Assert.AreEqual(2, rback.AssignedUids.Count, "AssignedUids 应保留（服务端 UID 下发）");
            Assert.AreEqual("inst_99", rback.AssignedUids[0], "AssignedUids[0] 应保留");
        }

        /// <summary>(2) 服务端权威身份校验：越权意图被拒（不触达业务服务）。</summary>
        public void TestIntentHubRejectsImpersonation()
        {
            // 桩路由：本用例越权路径不会触达业务逻辑
            var hub = new IntentHub(new StubIntentRouter());

            // 连接映射为会话 p1（座位 1），但意图声称自己是玩家 2 → 应判越权
            var intent = PlayerIntent.Create(2, PlayerIntent.Kind.Pass);
            var json = IntentSerializer.SerializeIntent(intent);
            var respJson = hub.HandleIntent("p1", 1, json);
            var resp = IntentSerializer.DeserializeResult(respJson);

            Assert.IsFalse(resp.Success, "越权意图应被拒绝");
            Assert.IsFalse(string.IsNullOrEmpty(resp.Error), "应给出越权原因");
            Assert.AreEqual(intent.IntentId, resp.IntentId, "IntentId 应原样带回（correlation）");
        }

        /// <summary>(3) 通道闭环：客户端经传输层上行意图，服务端收到并裁决（此处验证传输+反序列化+身份校验全链路）。</summary>
        public void TestIntentChannelLoopback()
        {
            // 批次 1：传输层会话寻址；本用例用"座位 1/2 ↔ 会话 p1/p2"的回环辅助。
            var loop = new PlayerIdLoopback();
            var transport = loop.Transport;
            transport.Start();

            // 服务端 IntentHub（桩路由；本用例验证"连接映射错配"被拒，不触达业务逻辑）
            var hub = new IntentHub(new StubIntentRouter());
            transport.OnSessionMessage((sessionId, msg) =>
            {
                // 服务端视角：该会话在房间里的座位是 2（第二个连入者），而意图声明 PlayerId=1 → 应判越权。
                // 服务端侧：Phase B 信封 → 先解包再裁决；回执同样打信封（与 NetworkMatchLauncher 一致）
                var resp = hub.HandleIntent(sessionId, 2, NetEnvelope.UnwrapPayload(msg));
                transport.SendToSession(sessionId, NetEnvelope.Wrap(NetEnvelope.Types.IntentResult, resp));
            });

            bool gotResult = false;
            IntentResult received = null;
            // 客户端侧：注册回包处理
            loop.RegisterClient(1, (serverMsg) =>
            {
                received = IntentClient.ParseResult(serverMsg);
                gotResult = true;
            });

            // 客户端发意图（本地座位 1，PlayCard）
            var client = new IntentClient(transport, 1);
            var id = client.SendIntent(PlayerIntent.Kind.PlayCard, "inst_7", "targetX");

            // LoopbackTransport 是同步投递，回包应已到达
            Assert.IsTrue(gotResult, "客户端应经回环收到服务端下行的 IntentResult");
            Assert.IsNotNull(received, "应解析出 IntentResult");
            Assert.AreEqual(id, received.IntentId, "下行结果应匹配上行 IntentId（correlation）");
            // 服务端用座位=2 裁决，意图声明 1 → 身份不符被拒（证明通道闭环 + 权威校验）
            Assert.IsFalse(received.Success, "连接映射与意图声明不符时应被服务端拒绝（通道闭环正确）");
        }

        /// <summary>(4) DebugCommand 意图：含空格整行命令作为单个参数路由，内容不丢。</summary>
        public void TestDebugCommandRouting()
        {
            var stub = new StubIntentRouter();
            var hub = new IntentHub(stub);

            var intent = PlayerIntent.Create(1, PlayerIntent.Kind.DebugCommand, "damage area 0 3");
            var json = IntentSerializer.SerializeIntent(intent);
            var respJson = hub.HandleIntent("p1", 1, json);
            var resp = IntentSerializer.DeserializeResult(respJson);

            Assert.IsTrue(resp.Success, "DebugCommand 应被路由为合法意图");
            Assert.AreEqual("damage area 0 3", stub.LastDebugCommand, "整行命令应作为单个参数完整透传");
        }
    }

    /// <summary>测试桩：实现 IIntentRouter，不触达任何业务逻辑（仅验证 IntentHub 反序列化/身份校验/路由分发）。</summary>
    internal class StubIntentRouter : IIntentRouter
    {
        public void Pass(int playerId) { }
        public void PlayCard(int playerId, string cardUid, string targetUid, string targetCardUid) { }
        public void DeployCovert(int playerId, string cardUid, string areaUid) { }
        public void RevealCovert(int playerId, string cardUid) { }
        public void EstablishAsset(int playerId, string cardUid) { }
        public void ReturnCardToDeck(int playerId, string cardUid, bool toTop) { }
        public void ReturnCardToHand(int playerId, string cardUid) { }
        public void DrawCards(int playerId, int count) { }
        public void DealDamage(string targetUid, int amount, bool isTrauma) { }
        public void DealAreaDamage(string areaUid, int amount, bool isTrauma) { }
        public void Concede(int playerId) { }
        public void ActivateAbility(string sourceUid, string effectId, string targetUid) { }
        public void ResetActionCharges(int playerId) { }
        public void MulliganReport(int playerId, System.Collections.Generic.IList<string> pickedUids) { }
        public string LastDebugCommand;
        public void DebugCommand(int playerId, string commandLine) { LastDebugCommand = commandLine; }
    }
}
