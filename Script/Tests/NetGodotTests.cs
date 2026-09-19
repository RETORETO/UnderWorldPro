using System.Threading;
using Godot;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P3·模块A 集成验证 / 批次 1 会话化】真实 ENet 联机回环：
    /// 同进程内起一个服务端 transport + 一个客户端 transport，经 127.0.0.1 真实 socket 握手收发。
    /// 验证 GodotEnetTransport 的 StartServer/StartClient/SendToSession/SendToServer/OnSessionMessage/Poll
    /// 端到端可用，且**服务端为连接分配的会话 id** 能正确用于定向回包。
    /// </summary>
    public class NetGodotTests : TestRunner
    {
        public void TestEnetRealTransportLoopback()
        {
            const int port = 18910;
            var server = new global::Script.Net.Godot.GodotEnetTransport(global::Script.Net.Godot.EnetRole.Server)
            {
                Port = (ushort)port,
                MaxClients = 16,
            };
            var client = new global::Script.Net.Godot.GodotEnetTransport(global::Script.Net.Godot.EnetRole.Client)
            {
                Port = (ushort)port,
                ServerAddress = "127.0.0.1",
            };

            string serverGot = null;
            string serverSawSession = null;
            string clientGot = null;

            server.OnSessionMessage((sessionId, msg) =>
            {
                serverGot = msg;
                serverSawSession = sessionId;
                server.SendToSession(sessionId, "PONG:" + sessionId);   // 服务端按会话定向回包
            });
            client.OnSessionMessage((_, msg) => { clientGot = msg; });

            server.Start();
            client.Start();

            // 手动驱动 ENet 轮询（替代场景中的 EnetPollDriverNode），完成握手与收发。
            // 客户端在连接建立后持续发送 PING（幂等），规避单次发送的时序边缘情况。
            for (int i = 0; i < 500; i++)
            {
                server.Poll();
                client.Poll();
                if (client.IsClientConnected())
                {
                    client.SendToServer("PING");
                }
                if (serverGot != null && clientGot != null) break;
                Thread.Sleep(10);
            }

            Assert.IsTrue(serverGot == "PING", "服务端应经真实 ENet 收到客户端 PING");
            Assert.IsTrue(!string.IsNullOrEmpty(serverSawSession) && serverSawSession.StartsWith("s"),
                $"服务端应为连接分配会话 id（形如 s1），实际={serverSawSession ?? "null"}");
            Assert.IsTrue(server.ConnectedSessionCount == 1, "服务端应有且仅有 1 个会话");
            Assert.IsTrue(server.IsSessionConnected(serverSawSession), "该会话应处于已连接状态");
            Assert.IsTrue(clientGot == "PONG:" + serverSawSession,
                $"客户端应经真实 ENet 收到服务端按会话定向的回包 PONG:{serverSawSession}，实际={clientGot}");

            server.Stop();
            client.Stop();
        }
    }
}
