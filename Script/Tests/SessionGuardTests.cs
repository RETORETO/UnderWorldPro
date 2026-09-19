using System.Collections.Generic;
using Script.Net;
using Script.Net.Lobby;

namespace Script.Tests
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】底座守卫验证（"脏输入/越权/重复"一律由底座拦住）：
    ///  未 Hello 即请求 / 重复 Hello / 版本不匹配 / 昵称非法 / 未知类型 / 非信封 /
    ///  **请求幂等（重复 RequestId 不重复执行）** / 非对局会话发对局消息被拒。
    /// </summary>
    public class SessionGuardTests : TestRunner
    {
        private sealed class CountingLobby : ILobbyHandler
        {
            public int HandleCount;
            public readonly List<LobbyErrorCode> Notified = new List<LobbyErrorCode>();
            public ServerHost Host;
            public LobbyRequest LastRequest;

            public void Handle(string sessionId, LobbyRequest request)
            {
                HandleCount++;
                LastRequest = request;
                Host?.Respond(sessionId, LobbyResponse.Success(request.RequestId));
            }

            public void OnSessionLeft(string sessionId) { }
        }

        /// <summary>桩对局路由：可选"接管"对局消息，用于验证被接管时不再发 NotInMatch 通知。</summary>
        private sealed class StubMatchRouter : IMatchRouter
        {
            public bool Accept;
            public string LastType;
            public bool TryRoute(string sessionId, string envelopeType, string payload)
            {
                LastType = envelopeType;
                return Accept;
            }
        }

        private sealed class Harness
        {
            public LoopbackTransport Loop;
            public ServerHost Host;
            public CountingLobby Lobby;
            public StubMatchRouter Router;
            public readonly List<string> DownA = new List<string>();

            public void Send(string json) => Loop.ClientReply("s1", json);
            public void SendEnveloped(string type, string payload) => Send(NetEnvelope.Wrap(type, payload));
            public string LastDown => DownA.Count == 0 ? null : DownA[DownA.Count - 1];
        }

        private static Harness NewHarness(ServerConfig cfg = null, IMatchRouter router = null)
        {
            var h = new Harness
            {
                Loop = new LoopbackTransport(),
                Lobby = new CountingLobby(),
                Router = router as StubMatchRouter,
            };
            h.Host = new ServerHost(h.Loop, cfg ?? new ServerConfig(), h.Lobby, router);
            h.Lobby.Host = h.Host;
            h.Host.LogHook = _ => { };
            h.Loop.Start();
            h.Loop.RegisterSession("s1", m => h.DownA.Add(m));
            return h;
        }

        private static string HelloJson(string requestId, int version = LobbyProtocol.ProtocolVersion, string name = "玩家甲")
            => LobbySerializer.Serialize(new LobbyRequest
            {
                RequestId = requestId, Kind = LobbyRequestKind.Hello,
                ProtocolVersion = version, PlayerName = name,
            });

        private static string ReqJson(string requestId, LobbyRequestKind kind)
            => LobbySerializer.Serialize(new LobbyRequest { RequestId = requestId, Kind = kind });

        public void TestRequestBeforeHelloRejected()
        {
            var h = NewHarness();
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, ReqJson("c1", LobbyRequestKind.ListRooms));

            Assert.AreEqual(0, h.Lobby.HandleCount, "未 Hello 的请求不应进入业务处理");
            Assert.IsTrue(h.LastDown.Contains(NetEnvelope.Types.LobbyResponse), "应回一个 lobby_res");
            Assert.IsTrue(h.LastDown.Contains("NotHelloed"), "错误码应为 NotHelloed");
        }

        public void TestDuplicateHelloRejected()
        {
            var h = NewHarness();
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("c1"));
            int downAfterFirst = h.DownA.Count;
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("c2"));

            Assert.IsTrue(h.DownA.Count > downAfterFirst, "重复 Hello 应有应答");
            Assert.IsTrue(h.LastDown.Contains("AlreadyHelloed"), "重复 Hello 应被拒（AlreadyHelloed）");
        }

        public void TestVersionMismatchRejectsAndDisconnects()
        {
            var h = NewHarness();
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("c1", version: LobbyProtocol.ProtocolVersion + 1));

            // 客户端应同时收到：① 请求的失败应答（correlation 明确）② server_notice（说明即将断开）
            bool sawResponse = h.DownA.Exists(m =>
                m.Contains(NetEnvelope.Types.LobbyResponse) && m.Contains("VersionMismatch"));
            bool sawNotice = h.DownA.Exists(m =>
                m.Contains(NetEnvelope.Types.ServerNotice) && m.Contains("VersionMismatch"));
            Assert.IsTrue(sawResponse, "版本不匹配应有一个 VersionMismatch 失败应答");
            Assert.IsTrue(sawNotice, "版本不匹配应有一条 server_notice（告知客户端即将断开）");
            Assert.AreEqual(0, h.Host.SessionCount, "版本不匹配的会话应被断开（会话表移除）");
            Assert.IsFalse(h.Host.IsConnected("s1"), "断开后会话不再处于已连接");
        }

        public void TestInvalidNameRejected()
        {
            var h = NewHarness(new ServerConfig { NameMaxLen = 4 });
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("c1", name: "这个昵称太长了"));
            Assert.IsTrue(h.LastDown.Contains("NameInvalid"), "超长昵称应被拒（NameInvalid）");

            var h2 = NewHarness(new ServerConfig { NameMaxLen = 4 });
            h2.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("c1", name: "   "));
            Assert.IsTrue(h2.LastDown.Contains("NameInvalid"), "空白昵称应被拒（NameInvalid）");
        }

        public void TestDuplicateRequestIdIsIdempotent()
        {
            var h = NewHarness();
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("hello1"));
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, ReqJson("dup1", LobbyRequestKind.ListRooms));
            int downsAfterFirst = h.DownA.Count;

            // 同一 RequestId 再发一次：不得重复执行，且应回放缓存应答
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, ReqJson("dup1", LobbyRequestKind.ListRooms));

            Assert.AreEqual(1, h.Lobby.HandleCount, "重复 RequestId 只应执行一次（幂等）");
            Assert.AreEqual(downsAfterFirst + 1, h.DownA.Count, "重复请求应回放一条缓存应答");
            Assert.IsTrue(h.LastDown.Contains("dup1"), "回放的应答应是同一 RequestId");

            // 不同 RequestId 正常执行
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, ReqJson("dup2", LobbyRequestKind.ListRooms));
            Assert.AreEqual(2, h.Lobby.HandleCount, "不同 RequestId 应正常执行");
        }

        public void TestUnknownEnvelopeTypeAndFormat()
        {
            var h = NewHarness();
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("h1"));

            // 未知类型
            h.SendEnveloped("something_else", "{}");
            Assert.IsTrue(h.LastDown.Contains(NetEnvelope.Types.ServerNotice), "未知类型应先给通知");
            Assert.IsTrue(h.LastDown.Contains("UnknownRequest"), "通知码应为 UnknownRequest");

            // 非信封（旧格式/脏数据）
            h.Send("{\"kind\":1}");
            Assert.IsTrue(h.LastDown.Contains("UnknownRequest"), "非信封消息应被判为无法识别");
            Assert.AreEqual(0, h.Lobby.HandleCount, "脏数据不应进入业务处理");
        }

        public void TestMatchChannelRejectedWithoutMatch()
        {
            var router = new StubMatchRouter { Accept = false };
            var h = NewHarness(router: router);
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("h1"));

            // 非对局会话发 intent → 被拒（NotInMatch 通知），且路由被尝试过
            h.SendEnveloped(NetEnvelope.Types.Intent, "{\"intentId\":\"i1\",\"playerId\":1}");
            Assert.AreEqual(NetEnvelope.Types.Intent, router.LastType, "底座应把 intent 交给对局路由");
            Assert.IsTrue(h.LastDown.Contains(NetEnvelope.Types.ServerNotice), "未接管时应给通知");
            Assert.IsTrue(h.LastDown.Contains("NotInMatch"), "通知码应为 NotInMatch");

            // choice_res 同样按对局通道处理
            h.SendEnveloped(NetEnvelope.Types.ChoiceResult, "{\"requestId\":\"r1\"}");
            Assert.AreEqual(NetEnvelope.Types.ChoiceResult, router.LastType, "底座应把 choice_res 交给对局路由");
            Assert.IsTrue(h.LastDown.Contains("NotInMatch"), "choice_res 未接管时同样提示不在对局中");

            // 被路由接管时不发通知
            var accepting = new StubMatchRouter { Accept = true };
            var h2 = NewHarness(router: accepting);
            h2.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("h1"));
            int downs = h2.DownA.Count;
            h2.SendEnveloped(NetEnvelope.Types.Intent, "{\"intentId\":\"i2\",\"playerId\":1}");
            Assert.AreEqual(downs, h2.DownA.Count, "被对局路由接管的消息不应再被底座回通知");
        }

        public void TestRequestIdCacheBounded()
        {
            // 幂等缓存有上限：连发大量不同请求不应无界增长/抛异常
            var h = NewHarness();
            h.SendEnveloped(NetEnvelope.Types.LobbyRequest, HelloJson("h1"));
            for (int i = 0; i < 200; i++)
                h.SendEnveloped(NetEnvelope.Types.LobbyRequest, ReqJson("bulk" + i, LobbyRequestKind.ListRooms));

            Assert.AreEqual(200, h.Lobby.HandleCount, "200 个不同请求应各执行一次");
            Assert.IsTrue(h.Host.GetSession("s1").RecentResponses.Count <= 32, "幂等缓存应有上限（≤32）");
        }
    }
}
