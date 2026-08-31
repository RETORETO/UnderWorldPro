using System.Threading;
using Godot;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P3·模块A 集成验证】真实 ENet 联机回环：
    /// 同进程内起一个服务端 transport + 一个客户端 transport，经 127.0.0.1 真实 socket 握手收发。
    /// 验证 GodotEnetTransport 的 StartServer/StartClient/Send/OnMessageReceived/Poll 端到端可用，
    /// 玩家编号(1) ↔ ENet peer id 映射正确。
    /// </summary>
    public class NetGodotTests : TestRunner
    {
        public void TestEnetRealTransportLoopback()
        {
            const int port = 18910;
            var server = new global::Script.Net.Godot.GodotEnetTransport(global::Script.Net.Godot.EnetRole.Server) { Port = (ushort)port };
            var client = new global::Script.Net.Godot.GodotEnetTransport(global::Script.Net.Godot.EnetRole.Client)
            {
                Port = (ushort)port,
                ServerAddress = "127.0.0.1",
                LocalPlayerId = 1
            };

            string serverGot = null;
            string clientGot = null;

            server.OnMessageReceived((pid, msg) =>
            {
                serverGot = msg;
                server.Send(pid, "PONG:" + pid); // 服务端回包
            });
            client.OnMessageReceived((pid, msg) => { clientGot = msg; });

            server.Start();
            client.Start();

            // 手动驱动 ENet 轮询（替代场景中的 EnetPollDriverNode），完成握手与收发。
            // 客户端在连接建立后持续发送 PING（幂等），规避单次发送的时序边缘情况。
            for (int i = 0; i < 500; i++)
            {
                server.Poll();
                client.Poll();
                if (server.IsPlayerConnected(1))
                {
                    client.Send(1, "PING");
                }
                if (serverGot != null && clientGot != null) break;
                Thread.Sleep(10);
            }

            Assert.IsTrue(serverGot == "PING", "服务端应经真实 ENet 收到客户端 PING");
            Assert.IsTrue(clientGot == "PONG:1", "客户端应经真实 ENet 收到服务端回包 PONG:1");

            server.Stop();
            client.Stop();
        }
    }
}
