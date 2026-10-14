using System.Collections.Generic;
using System.Threading;
using Godot;
using Script.Net;
using Script.Net.Lobby;

namespace Script.Tests
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】会话路由验证：
    ///  1) 会话建立/断开 → 底座会话表正确增减，并通知业务；
    ///  2) `SendToSession` **定向投递**：两个会话互不串（回环）；
    ///  3) 连接数上限：超限会话被拒（通知 ServerFull）；
    ///  4) 真实 WebSocket 本机回环：服务端为两条连接分配**不同会话 id**，定向消息各达各的。
    ///
    /// 【传输层统一 · 2026-10-15】第 4 条从 ENet 迁到 WebSocket：ENet 已不在联机主链路上
    /// （`ClientSession.Connect` 恒用 WebSocket），而这条用例是**唯一**在真实 socket 上验证
    /// "多 peer → 多会话 → 定向投递不串"的用例；继续用 ENet 等于主链路没有真实传输覆盖。
    /// 尤其 `GodotWebSocketTransport.DispatchAvailablePackets` 有个 WS 特有的硬约束
    /// （必须先 `GetPacketPeer()` 再 `GetPacket()`，因为前者取的是**队列头部**包的来源），
    /// 一旦写反就会出现"消息投给了上一个 peer"的串会话 bug —— 只有真实双连接才能暴露。
    /// ENet 传输自身的真实收发仍由 `NetGodotTests` / `NetP1Batch1Tests` 覆盖，不丢。
    /// </summary>
    public class SessionRoutingTests : TestRunner
    {
        /// <summary>记录收到的下行报文 + 被通知的会话，供断言。</summary>
        private sealed class StubLobby : ILobbyHandler
        {
            public readonly List<string> Left = new List<string>();
            public readonly List<(string sessionId, LobbyRequestKind kind)> Handled = new List<(string, LobbyRequestKind)>();
            public ServerHost Host;

            public void Handle(string sessionId, LobbyRequest request)
            {
                Handled.Add((sessionId, request.Kind));
                // 统一回一个成功应答（并回显房间状态为 null）
                Host?.Respond(sessionId, LobbyResponse.Success(request.RequestId));
            }

            public void OnSessionLeft(string sessionId) => Left.Add(sessionId);
        }

        private static ServerHost NewHost(INetTransport transport, ServerConfig cfg, StubLobby lobby)
        {
            var host = new ServerHost(transport, cfg, lobby);
            lobby.Host = host;
            host.LogHook = _ => { };   // 静音日志，避免测试输出淹没
            return host;
        }

        public void TestSessionLifecycleAndTargetedSend()
        {
            var loop = new LoopbackTransport();
            var lobby = new StubLobby();
            var host = NewHost(loop, new ServerConfig(), lobby);
            loop.Start();

            loop.RegisterSession("s1", _ => { });
            loop.RegisterSession("s2", _ => { });
            Assert.AreEqual(2, host.SessionCount, "两个会话都应在会话表中");
            Assert.IsTrue(host.IsConnected("s1") && host.IsConnected("s2"), "两会话均应处于已连接");

            // 定向投递：只应命中目标会话
            var got = new Dictionary<string, List<string>> { ["s1"] = new List<string>(), ["s2"] = new List<string>() };
            loop.RegisterSession("s1", m => got["s1"].Add(m));
            loop.RegisterSession("s2", m => got["s2"].Add(m));
            host.Send("s1", NetEnvelope.Types.ServerNotice, "{\"text\":\"hello s1\"}");
            Assert.AreEqual(1, got["s1"].Count, "会话 s1 应收到定向消息");
            Assert.AreEqual(0, got["s2"].Count, "会话 s2 不应收到发给 s1 的消息");

            // 断开 → 会话表移除 + 通知业务
            loop.UnregisterSession("s1");
            Assert.AreEqual(1, host.SessionCount, "断开后会话表应只剩 1");
            Assert.IsFalse(host.IsConnected("s1"), "断开后 IsConnected 应为 false");
            Assert.AreEqual(1, lobby.Left.Count, "断开应通知业务 OnSessionLeft");
            Assert.AreEqual("s1", lobby.Left[0], "通知的会话 id 应正确");

            // 未知会话：静默丢弃（不抛异常）
            host.Send("s_unknown", NetEnvelope.Types.ServerNotice, "{}");
        }

        public void TestConnectionLimitRejectsExcessSession()
        {
            var loop = new LoopbackTransport();
            var lobby = new StubLobby();
            var cfg = new ServerConfig { MaxConnections = 2 };
            var host = NewHost(loop, cfg, lobby);
            loop.Start();

            var notices = new List<string>();
            loop.RegisterSession("s1", _ => { });
            loop.RegisterSession("s2", _ => { });
            loop.RegisterSession("s3", m => notices.Add(m));   // 第 3 个会话：超限

            Assert.AreEqual(2, host.SessionCount, "超过上限的会话不应进入会话表");
            Assert.AreEqual(1, notices.Count, "超限会话应收到一条 ServerFull 通知");
            Assert.IsTrue(notices[0].Contains(NetEnvelope.Types.ServerNotice), "通知应打 server_notice 信封");
            Assert.IsTrue(notices[0].Contains("ServerFull") || notices[0].Contains("已满"), "通知内容应说明服务器已满");
        }

        public void TestSessionMessageRoutingToLobbyHandler()
        {
            var loop = new LoopbackTransport();
            var lobby = new StubLobby();
            var host = NewHost(loop, new ServerConfig(), lobby);
            loop.Start();

            var responses = new List<string>();
            loop.RegisterSession("s1", m => responses.Add(m));

            // Hello → 底座处理（不进入业务）
            loop.ClientReply("s1", NetEnvelope.Wrap(NetEnvelope.Types.LobbyRequest,
                LobbySerializer.Serialize(new LobbyRequest
                {
                    RequestId = "c1", Kind = LobbyRequestKind.Hello,
                    ProtocolVersion = LobbyProtocol.ProtocolVersion, PlayerName = "玩家甲",
                })));
            Assert.AreEqual(0, lobby.Handled.Count, "Hello 由底座处理，不应进入业务处理器");
            Assert.AreEqual(1, responses.Count, "Hello 应有一个应答");
            Assert.IsTrue(responses[0].Contains(NetEnvelope.Types.LobbyResponse), "应答应打 lobby_res 信封");
            Assert.IsTrue(responses[0].Contains("c1"), "应答应回显 RequestId");
            Assert.IsFalse(string.IsNullOrEmpty(responses[0]), "应答应有内容");
            Assert.IsTrue(host.GetSession("s1").Helloed, "Hello 后会话应标记为已登录");
            Assert.AreEqual("玩家甲", host.GetSession("s1").PlayerName, "Hello 应记录昵称");

            // Hello 之后的业务请求 → 进入业务处理器
            loop.ClientReply("s1", NetEnvelope.Wrap(NetEnvelope.Types.LobbyRequest,
                LobbySerializer.Serialize(new LobbyRequest { RequestId = "c2", Kind = LobbyRequestKind.ListRooms })));
            Assert.AreEqual(1, lobby.Handled.Count, "Hello 之后的请求应进入业务处理器");
            Assert.AreEqual(LobbyRequestKind.ListRooms, lobby.Handled[0].kind, "业务应收到正确的请求种类");
        }

        /// <summary>真实 WebSocket：两条连接 → 两个会话 id；定向消息各达各的，互不串。</summary>
        public void TestWebSocketTwoSessionsTargetedDelivery()
        {
            const int port = 18912;
            var server = new global::Script.Net.Godot.GodotWebSocketTransport(global::Script.Net.Godot.EnetRole.Server)
            {
                Port = (ushort)port,
            };
            var clientA = new global::Script.Net.Godot.GodotWebSocketTransport(global::Script.Net.Godot.EnetRole.Client)
            {
                Port = (ushort)port, ServerAddress = "127.0.0.1",
            };
            var clientB = new global::Script.Net.Godot.GodotWebSocketTransport(global::Script.Net.Godot.EnetRole.Client)
            {
                Port = (ushort)port, ServerAddress = "127.0.0.1",
            };

            var sessions = new List<string>();
            server.OnSessionConnected(sid => sessions.Add(sid));

            string gotA = null, gotB = null;
            clientA.OnSessionMessage((_, m) => gotA = m);
            clientB.OnSessionMessage((_, m) => gotB = m);

            // 服务端收到任一客户端上行后，向**该会话**回一条带会话 id 的消息（定向验证）
            server.OnSessionMessage((sid, msg) => server.SendToSession(sid, "ECHO:" + sid));

            server.Start();
            clientA.Start();
            Thread.Sleep(50);
            clientB.Start();

            for (int i = 0; i < 600; i++)
            {
                server.Poll();
                clientA.Poll();
                clientB.Poll();
                if (clientA.IsClientConnected() && gotA == null) clientA.SendToServer("PING");
                if (clientB.IsClientConnected() && gotB == null) clientB.SendToServer("PING");
                if (gotA != null && gotB != null && sessions.Count >= 2) break;
                Thread.Sleep(10);
            }

            Assert.AreEqual(2, sessions.Count, "服务端应为两条连接分配两个会话");
            Assert.IsTrue(sessions[0] != sessions[1], "两个会话 id 必须互不相同");
            Assert.IsTrue(gotA != null && gotA.StartsWith("ECHO:"), $"客户端 A 应收到定向回包，实际={gotA ?? "null"}");
            Assert.IsTrue(gotB != null && gotB.StartsWith("ECHO:"), $"客户端 B 应收到定向回包，实际={gotB ?? "null"}");
            Assert.IsTrue(gotA != gotB, "两个客户端收到的定向消息不应相同（证明未串会话）");
            Assert.AreEqual(2, server.ConnectedSessionCount, "服务端应有两个活跃会话");

            server.Stop();
            clientA.Stop();
            clientB.Stop();
        }
    }
}
