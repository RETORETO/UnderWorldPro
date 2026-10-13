using System;
using System.Threading;
using Godot;
using Script.Net.Godot;
using Script.Net.Lobby;

namespace Script.Tests
{
    /// <summary>
    /// 【建立主机 · 增量 D】**内嵌服务端自检**（不是完整流程测试）：
    ///   ① 本进程内能起服务端 → 客户端连自己 → 自动建房（建房者=座位1=房主）；
    ///   ② 起始端口被占用时能顺延到下一个端口；
    ///   ③ `Stop()` 后端口**同步**释放（可立刻复用同一端口），且 `Start` 幂等；
    ///   ④ 局域网地址提示的基本形状（避免把回环/IPv6 当可用地址发给对手）。
    ///
    /// 定位：只验证"能起、能连、能建房、能关"这条新增链路的确定性部分，不重复 `LobbyFlowTests` 的完整对局流程断言；
    /// 真正的交付验收仍是"两台机器真机冒烟"（见 `联机大厅房间与开局流程.md` §八）。
    /// </summary>
    public class LocalHostTests : TestRunner
    {
        /// <summary>测试端口基数：与默认 18950 及 `LobbyFlowTests` 的 18995 错开，避免本机残留实例抢端口。</summary>
        private const ushort BasePort = 18997;

        // ==================== 脚手架 ====================

        /// <summary>
        /// 驱动内嵌服务端与客户端会话，直到条件成立或超时。
        /// 单测里没有引擎帧（整段测试在同一个 `_Ready` 内同步跑完），故显式调用服务端节点的 `_Process`
        /// ——与 `NetMatchEntry._Process` 显式驱动 launcher 的做法同源，也和 `LobbyFlowTests.Pump` 手写 Poll 一致。
        /// </summary>
        private static bool Pump(ClientSession session, int timeoutMs, Func<bool> until)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                LocalHostServer.Host?._Process(0.05);
                session?.Poll();
                if (until != null && until()) return true;
                Thread.Sleep(5);
            }
            return until == null || until();
        }

        /// <summary>测试用卡组（复用既有默认卡组；服务端在「建房」阶段不需要卡组，卡组只在准备/开局时校验）。</summary>
        private static LobbyRequest CreateRoomRequest() => new LobbyRequest
        {
            Kind = LobbyRequestKind.CreateRoom,
            RoomSettings = new RoomSettings { InitialAreaCount = 3 },
        };

        // ==================== 用例 ====================

        /// <summary>本进程内嵌服务端 + 本进程客户端连自己 + 自动建房（= 首页「建立主机」的可执行等价物）。</summary>
        public void TestEmbeddedHostHostsRoom()
        {
            ClientSession session = null;
            try
            {
                Assert.IsTrue(LocalHostServer.Start(out var error, BasePort), $"内嵌服务端应启动成功：{error}");
                Assert.IsTrue(LocalHostServer.IsRunning, "启动后 IsRunning 应为 true");
                Assert.AreEqual(BasePort, LocalHostServer.Port, "应监听期望的起始端口");

                session = ClientSession.Connect("127.0.0.1", LocalHostServer.Port, "主机甲");
                Assert.IsTrue(Pump(session, 5000, () => session.IsHelloAcked), "客户端应完成 Hello（本进程内连自己）");

                session.Send(CreateRoomRequest());

                RoomState room = null;
                Assert.IsTrue(Pump(session, 5000, () => (room = session.CurrentRoom) != null && room.ViewerSeat != 0),
                    "建房后应收到 RoomState");
                Assert.AreEqual(1, room.ViewerSeat, "建房者应占座位 1");
                Assert.AreEqual(1, room.HostSeat, "建房者应为房主");
                Assert.AreEqual(RoomPhase.Waiting, room.Phase, "新建房间应为 Waiting 阶段");
            }
            finally
            {
                session?.Disconnect();
                LocalHostServer.Stop();
            }
        }

        /// <summary>起始端口被占用 → 顺延到下一个端口（对应 D13 的 18950→18959 策略）。</summary>
        public void TestEmbeddedHostPortFallback()
        {
            ushort busy = (ushort)(BasePort + 1);
            // 内嵌服务端现在用 WebSocket(TCP) 监听，所以"占端口"也必须占 TCP 端口才能复现冲突。
            var occupied = new GodotWebSocketTransport(EnetRole.Server) { Port = busy };
            try
            {
                occupied.Start();
                Assert.IsTrue(LocalHostServer.Start(out var error, busy), $"起始端口被占用时仍应起得来（顺延）：{error}");
                Assert.AreEqual((ushort)(busy + 1), LocalHostServer.Port, "应顺延到下一个端口");
            }
            finally
            {
                LocalHostServer.Stop();
                occupied.Stop();
            }
        }

        /// <summary>`Stop()` 必须同步释放端口：关闭后能立刻在同一端口再次启动（否则"关掉再建立主机"会莫名换端口）。</summary>
        public void TestEmbeddedHostStopReleasesPort()
        {
            try
            {
                Assert.IsTrue(LocalHostServer.Start(out var e1, BasePort), $"首次启动应成功：{e1}");
                Assert.AreEqual(BasePort, LocalHostServer.Port, "首次应监听起始端口");

                LocalHostServer.Stop();
                Assert.IsFalse(LocalHostServer.IsRunning, "Stop 后 IsRunning 应为 false");
                Assert.AreEqual((ushort)0, LocalHostServer.Port, "Stop 后 Port 应清零");

                Assert.IsTrue(LocalHostServer.Start(out var e2, BasePort), $"关闭后应能立刻复用同一端口：{e2}");
                Assert.AreEqual(BasePort, LocalHostServer.Port, "端口应已同步释放");
            }
            finally
            {
                LocalHostServer.Stop();
            }
        }

        /// <summary>`Start` 幂等：重复点「建立主机」不换端口、不重复占用。</summary>
        public void TestEmbeddedHostStartIsIdempotent()
        {
            try
            {
                Assert.IsTrue(LocalHostServer.Start(out var e1, BasePort), $"首次启动应成功：{e1}");
                ushort first = LocalHostServer.Port;
                Assert.IsTrue(LocalHostServer.Start(out var e2, BasePort), $"重复启动应幂等成功：{e2}");
                Assert.AreEqual(first, LocalHostServer.Port, "重复启动不应换端口");
            }
            finally
            {
                LocalHostServer.Stop();
            }
        }

        /// <summary>地址提示形状：以「:端口」结尾；只列 IPv4 且排除回环（对手拿着它要能直连）。</summary>
        public void TestLanAddressHintFormat()
        {
            string hint = LocalHostServer.AddressHint(18950);
            Assert.IsTrue(hint.EndsWith(":18950", StringComparison.Ordinal), $"地址提示应以「:端口」结尾，实际：{hint}");
            foreach (var ip in LocalHostServer.LanIPv4())
            {
                Assert.IsFalse(ip == "127.0.0.1", "提示里不应出现回环地址");
                Assert.IsFalse(ip.Contains(':'), "提示里不应出现 IPv6 地址");
            }
        }
    }
}
