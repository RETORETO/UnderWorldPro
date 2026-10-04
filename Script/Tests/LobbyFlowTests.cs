using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Script.IO;
using Script.Net;
using Script.Net.Godot;
using Script.Net.Lobby;

namespace Script.Tests
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】**完整流程自检**（不是单元测试）：同进程内起真实 ENet 服务端 + 两个真实客户端会话，
    /// 走完 `连接 → Hello → 房间列表 → 建房 → 加入 → 提交卡组 → 准备 → 开始 → 对局（收快照）→ 结束 → 自动回同一房间`。
    ///
    /// 目的与定位（用户已明确"大厅/房间不写单测"）：本套件只验证**流程能走通且双方视角一致**，
    /// 不断言内部实现细节；真正的交付验收是"一台机器三进程冒烟"（`NetServer + 2×NetEntry --bot`）。
    /// </summary>
    public class LobbyFlowTests : TestRunner
    {
        private const ushort Port = 18995;   // 与默认 18950 错开，避免与本机其它测试/实例冲突

        // ==================== 测试脚手架 ====================

        private sealed class Rig : IDisposable
        {
            public INetTransport Transport;
            public ServerHost Host;
            public LobbyServer Lobby;
            public ServerMatchFactory Factory;
            public ClientSession Host1;
            public ClientSession Guest2;
            public ClientSession Guest3;

            public readonly List<RoomState> StateH1 = new List<RoomState>();
            public readonly List<RoomState> StateH2 = new List<RoomState>();
            public readonly List<LobbyResponse> ResH1 = new List<LobbyResponse>();
            public readonly List<LobbyResponse> ResH2 = new List<LobbyResponse>();
            public readonly List<string> NoticesH1 = new List<string>();
            public readonly List<string> NoticesH2 = new List<string>();
            public int SnapshotsH1, SnapshotsH2;

            public RoomState Last1 => StateH1.Count == 0 ? null : StateH1[StateH1.Count - 1];
            public RoomState Last2 => StateH2.Count == 0 ? null : StateH2[StateH2.Count - 1];

            public void Dispose()
            {
                Host1?.Disconnect();
                Guest2?.Disconnect();
                Guest3?.Disconnect();
                Host?.Stop();
            }
        }

        private static Rig StartRig(Func<RoomSession, IRoomMatchHost> factory = null, ServerConfig config = null)
        {
            var rig = new Rig();
            var cfg = config ?? new ServerConfig { Port = Port, MaxConnections = 16, MaxRooms = 4, Verbose = false };
            cfg.Port = Port;   // 每个 rig 必须用自己的端口，避免并行/残留实例抢端口
            rig.Transport = new GodotEnetTransport(EnetRole.Server) { Port = Port, MaxClients = 16 };
            rig.Factory = new ServerMatchFactory(null, rig.Transport);
            rig.Lobby = new LobbyServer(cfg, new GameDeckValidator(), factory ?? (room => rig.Factory.Create(room)))
            {
                LogHook = _ => { },   // 静音：流程自检只需结果
            };
            rig.Lobby.OnRoomClosed = room => rig.Factory.Destroy(room);
            rig.Lobby.OnMatchAssemblyReleased = room => rig.Factory.Destroy(room);
            rig.Host = new ServerHost(rig.Transport, cfg, rig.Lobby, rig.Lobby) { LogHook = _ => { } };
            rig.Lobby.Host = rig.Host;
            rig.Host.Start();

            rig.Host1 = ClientSession.Connect("127.0.0.1", Port, "房主甲");
            rig.Host1.OnLobbyResponse += r => rig.ResH1.Add(r);
            rig.Host1.OnRoomState += s => rig.StateH1.Add(s);
            rig.Host1.OnNotice += n => rig.NoticesH1.Add(n.Text);
            rig.Host1.OnMatchEnvelope += (t, _) => { if (t == "snap") rig.SnapshotsH1++; };

            rig.Guest2 = ClientSession.Connect("127.0.0.1", Port, "玩家乙");
            rig.Guest2.OnLobbyResponse += r => rig.ResH2.Add(r);
            rig.Guest2.OnRoomState += s => rig.StateH2.Add(s);
            rig.Guest2.OnMatchEnvelope += (t, _) => { if (t == "snap") rig.SnapshotsH2++; };
            rig.Guest2.OnNotice += n => rig.NoticesH2.Add(n.Text);

            // 等两条连接完成握手
            Pump(rig, 4000, () => rig.Host1.IsConnected && rig.Guest2.IsConnected);
            return rig;
        }

        /// <summary>驱动服务端与客户端轮询，直到条件满足或超时。</summary>
        private static bool Pump(Rig rig, int timeoutMs, Func<bool> until)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                rig.Host.Poll();
                rig.Lobby.Tick(0.05);
                rig.Host1?.Poll();
                rig.Guest2?.Poll();
                rig.Guest3?.Poll();
                if (until != null && until()) return true;
                Thread.Sleep(5);
            }
            return until == null || until();
        }

        private static string DeckJson()
        {
            var deck = DeckListLoader.Load("res://Decks/default_deck.json");
            if (deck == null) return null;
            return DeckListLoader.ToJsonText(deck);
        }

        // ==================== 流程 1：完整正向流程 ====================

        public void TestFullLobbyFlow()
        {
            using var rig = StartRig();
            Assert.IsTrue(rig.Host1.IsConnected && rig.Guest2.IsConnected, "两个客户端应连上服务端");
            Pump(rig, 3000, () => rig.Host1.MySessionId != null && rig.Guest2.MySessionId != null);
            Assert.IsNotNull(rig.Host1.MySessionId, "Hello 应答应回填自身会话 id（房主）");
            Assert.IsNotNull(rig.Guest2.MySessionId, "Hello 应答应回填自身会话 id（加入者）");
            Assert.IsTrue(rig.Host1.MySessionId != rig.Guest2.MySessionId, "两个客户端应是不同会话");

            // ① 房间列表（空）
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.ListRooms });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Exists(r => r.RoomList != null)), "应收到房间列表应答");
            var list0 = rig.ResH1.Find(r => r.RoomList != null).RoomList;
            Assert.AreEqual(0, list0.Rooms.Count, "初始应无房间");
            Assert.AreEqual(4, list0.MaxRooms, "房间上限应来自服务端配置");

            // ② 建房（房主 → 座位1，房主身份）
            rig.Host1.Send(new LobbyRequest
            {
                Kind = LobbyRequestKind.CreateRoom,
                RoomSettings = new RoomSettings { InitialAreaCount = 3 },
            });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1 != null), "房主应收到房间状态");
            string roomId = rig.Last1.RoomId;
            Assert.IsNotNull(roomId, "房间号应由服务端分配");
            Assert.AreEqual(1, rig.Last1.ViewerSeat, "房主视角应为座位1");
            Assert.AreEqual(RoomPhase.Waiting, rig.Last1.Phase, "仅一名玩家时应为 Waiting");
            Assert.IsTrue(rig.Last1.SeatState(1).Connected, "座位1应显示在线");

            // ③ 列表能看到该房间
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.ListRooms });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH2.Exists(r => r.RoomList != null)), "加入者应收到房间列表");
            var list1 = rig.ResH2.Find(r => r.RoomList != null).RoomList;
            Assert.AreEqual(1, list1.Rooms.Count, "列表应包含刚创建的房间");
            Assert.AreEqual(1, list1.Rooms[0].PlayerCount, "列表应显示 1 人");

            // ④ 加入（座位2）
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = roomId });
            Assert.IsTrue(Pump(rig, 3000,
                () => rig.Last2 != null && rig.Last2.SeatState(2)?.PlayerName != null), "加入者应进入座位2");
            Assert.AreEqual(2, rig.Last2.ViewerSeat, "加入者视角应为座位2");
            Assert.AreEqual("玩家乙#2", rig.Last2.SeatState(2).DisplayName, "显示名应为 昵称#座位（D9）");
            Assert.AreEqual("房主甲#1", rig.Last1.SeatState(1).DisplayName, "房主侧同样显示 昵称#座位");

            // ⑤ 双方提交卡组（服务端校验，内联 JSON）
            string deckJson = DeckJson();
            Assert.IsNotNull(deckJson, "测试卡组应能加载（res://Decks/default_deck.json）");
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckName = "房主卡组", DeckJson = deckJson });
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckName = "客人卡组", DeckJson = deckJson });
            Assert.IsTrue(Pump(rig, 4000,
                () => rig.Last1?.SeatState(1)?.DeckValid == true && rig.Last2?.SeatState(2)?.DeckValid == true),
                "双方卡组都应通过服务端校验");

            // D7：对手卡组信息不可见
            var oppView = rig.Last1.SeatState(2);
            Assert.IsFalse(oppView.HasDeck, "D7：对手 HasDeck 不可见");
            Assert.IsNull(oppView.DeckName, "D7：对手卡组名不可见");
            Assert.AreEqual(0, oppView.DeckCardCount, "D7：对手卡组张数不可见");

            // ⑥ 双方准备 → 阶段 Ready
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1?.Phase == RoomPhase.Ready), "双方准备后阶段应为 Ready");

            // ⑥b 卡组锁定：已准备后再提交卡组应被权威拒绝（DeckLocked），卡组与准备状态都不变
            int resN = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckName = "偷换的卡组", DeckJson = deckJson });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Count > resN && !rig.ResH1[rig.ResH1.Count - 1].Ok),
                "已准备后更换卡组应被拒");
            Assert.AreEqual(LobbyErrorCode.DeckLocked, rig.ResH1[rig.ResH1.Count - 1].ErrorCode, "错误码应为 DeckLocked");
            Assert.AreEqual("房主卡组", rig.Last1.SeatState(1).DeckName, "被拒后卡组名不应变化");
            Assert.IsTrue(rig.Last1.SeatState(1).Ready, "被拒后应保持已准备");

            // ⑥c 取消准备 → 锁定解除，可以正常更换卡组；再准备回到 Ready 继续后续步骤
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = false });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1?.SeatState(1)?.Ready == false), "应能取消准备（锁定解除）");
            resN = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckName = "房主卡组2", DeckJson = deckJson });
            Assert.IsTrue(Pump(rig, 4000, () => rig.ResH1.Count > resN && rig.ResH1[rig.ResH1.Count - 1].Ok
                && rig.Last1?.SeatState(1)?.DeckName == "房主卡组2"), "取消准备后应能更换卡组");
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1?.Phase == RoomPhase.Ready), "重新准备后阶段应回到 Ready");

            // ⑦ 房主开始 → Starting/InMatch，双方收到对局快照
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
            Assert.IsTrue(Pump(rig, 8000,
                () => rig.Last1?.Phase == RoomPhase.InMatch && rig.SnapshotsH1 > 0 && rig.SnapshotsH2 > 0),
                $"应进入对局且双方收到快照（phase={rig.Last1?.Phase}, snap1={rig.SnapshotsH1}, snap2={rig.SnapshotsH2}）");

            // ⑧ 对局中房间操作应被拒（阶段守卫）
            int resCount = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = false });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Count > resCount && !rig.ResH1[rig.ResH1.Count - 1].Ok),
                "对局中改变准备状态应被拒（PhaseMismatch）");
            Assert.AreEqual(LobbyErrorCode.PhaseMismatch, rig.ResH1[rig.ResH1.Count - 1].ErrorCode, "错误码应为 PhaseMismatch");

            // ⑨ 结束对局（用服务端权威入口判负，避免依赖完整对局时长）→ Finished + 结果广播
            var room = rig.Lobby.GetRoom(roomId);
            Assert.IsNotNull(room?.Match, "房间应持有对局宿主");
            room.Match.ForfeitSeat(1, "流程自检：模拟座位1离场判负");
            Assert.IsTrue(Pump(rig, 5000, () => rig.Last2?.Phase == RoomPhase.Finished),
                $"判负后房间应为 Finished（实际 {rig.Last2?.Phase}）");
            Assert.AreEqual(2, rig.Last2.MatchSummary?.WinnerSeat, "结果应广播给双方（获胜座位 2）");

            // ⑩ 结果展示结束后**自动**回同一房间（无需任何"再来一局"操作）：
            //    双方未准备、卡组保留、房间号与座位不变、旧对局已关闭、不再保留结果。
            Assert.IsTrue(Pump(rig, 8000,
                () => rig.Last1?.Phase == RoomPhase.Waiting
                      && rig.Last1.SeatState(1).Ready == false
                      && rig.Last1.SeatState(2).Ready == false),
                $"结果展示结束后应自动回到 Waiting 且双方未准备（实际 {rig.Last1?.Phase}）");
            Assert.IsNull(rig.Lobby.GetRoom(roomId)?.Match, "自动回房后旧对局应已关闭");
            Assert.IsTrue(rig.Last1.SeatState(1).DeckValid, "自动回房后卡组应保留（仍有效）");
            Assert.AreEqual(roomId, rig.Last1.RoomId, "应复用原房间（房间号不变）");
            Assert.IsNull(rig.Last1.MatchSummary, "结果只在战场页展示，回到房间后不再保留结果");

            // ⑪ 离开：两人都离开后房间销毁
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.LeaveRoom });
            Pump(rig, 2000, () => false);
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.LeaveRoom });
            Assert.IsTrue(Pump(rig, 4000, () => rig.Lobby.RoomCount == 0), "两人离开后房间应被销毁");
        }

        // ==================== 流程 2：联机 UI 场景可加载（冒烟） ====================

        /// <summary>
        /// 联机 UI 场景冒烟检查（不是单测）：三个在线场景必须能加载且关键节点存在，
        /// 否则玩家会等到"点联机对战后白屏/报错"才发现。
        /// </summary>
        public void TestOnlineUiScenesLoad()
        {
            var cases = new (string path, string requiredNode)[]
            {
                ("res://Scenes/Net/LobbyOl.tscn", "Panel/ConnectButton"),
                ("res://Scenes/Net/RoomOl.tscn", "Panel/ReadyButton"),
                ("res://Scenes/Net/OnlineArena.tscn", null),
            };
            foreach (var (path, requiredNode) in cases)
            {
                var scene = ResourceLoader.Load<PackedScene>(path);
                Assert.IsNotNull(scene, $"{path} 应可加载（解析无错误）");
                var node = scene.Instantiate();
                Assert.IsNotNull(node, $"{path} 应可实例化");
                if (requiredNode != null)
                    Assert.IsNotNull(node.GetNodeOrNull(requiredNode), $"{path} 应包含节点 {requiredNode}");
                node.Free();
            }
        }

        // ==================== 流程 3：权限与边界 ====================

        public void TestRoomPermissionFlow()
        {
            using var rig = StartRig();
            Pump(rig, 3000, () => rig.Host1.MySessionId != null && rig.Guest2.MySessionId != null);

            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.CreateRoom });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1 != null), "房主应建房成功");
            string roomId = rig.Last1.RoomId;

            // 不在房间时的房间操作应被拒
            int n0 = rig.ResH2.Count;
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH2.Count > n0 && !rig.ResH2[rig.ResH2.Count - 1].Ok), "未进房准备应被拒");
            Assert.AreEqual(LobbyErrorCode.NotInRoom, rig.ResH2[rig.ResH2.Count - 1].ErrorCode, "错误码应为 NotInRoom");

            // 加入一个不存在的房间
            n0 = rig.ResH2.Count;
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = "r999" });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH2.Count > n0 && rig.ResH2[rig.ResH2.Count - 1].ErrorCode == LobbyErrorCode.RoomNotFound),
                "加入不存在的房间应报 RoomNotFound");

            // 非房主不能开始
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = roomId });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last2?.SeatState(2)?.PlayerName != null), "加入者应就座");

            n0 = rig.ResH2.Count;
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH2.Count > n0 && rig.ResH2[rig.ResH2.Count - 1].ErrorCode == LobbyErrorCode.NotHost),
                "非房主开始应报 NotHost");

            // 房主也不能"踢自己"
            n0 = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.Kick, TargetSeat = 1 });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Count > n0 && rig.ResH1[rig.ResH1.Count - 1].ErrorCode == LobbyErrorCode.InvalidSeat),
                "踢自己应报 InvalidSeat");

            // 未提交卡组就准备 → 被判卡组违规，准备不生效
            n0 = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Count > n0 && rig.ResH1[rig.ResH1.Count - 1].ErrorCode == LobbyErrorCode.InvalidDeck),
                "未提交卡组就准备应报 InvalidDeck");
            Assert.IsTrue(rig.Last1?.Phase != RoomPhase.Ready, "准备不生效，阶段不应为 Ready");

            // 非法卡组（伪造路径）→ 选卡组被拒，DeckValid 保持 false
            n0 = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest
            {
                Kind = LobbyRequestKind.SelectDeck,
                DeckJson = "{\"deckName\":\"坏卡组\",\"cards\":[{\"templatePath\":\"res://NoSuchCard.tres\",\"copies\":1}]}",
            });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Count > n0 && rig.ResH1[rig.ResH1.Count - 1].ErrorCode == LobbyErrorCode.InvalidDeck),
                "不存在的卡牌模板应判卡组违规");
            Assert.IsFalse(rig.Last1.SeatState(1).DeckValid, "违规后 DeckValid 应为 false");

            // 房间满：第三个客户端加入应被拒
            rig.Guest3 = ClientSession.Connect("127.0.0.1", Port, "第三人");
            bool joined3 = Pump(rig, 4000, () => rig.Guest3.MySessionId != null);
            Assert.IsTrue(joined3, "第三个客户端应能连上（服务器允许更多连接）");
            var res3 = new List<LobbyResponse>();
            rig.Guest3.OnLobbyResponse += r => res3.Add(r);
            rig.Guest3.Send(new LobbyRequest { Kind = LobbyRequestKind.ListRooms });
            Assert.IsTrue(Pump(rig, 3000, () => res3.Exists(r => r.RoomList != null)), "第三人应能拿到房间列表");
            rig.Guest3.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = roomId });
            Assert.IsTrue(Pump(rig, 3000, () => res3.Exists(r => !r.Ok && r.ErrorCode == LobbyErrorCode.RoomFull)),
                "满房加入应报 RoomFull");

            // 房主踢人 → 被踢者收到通知、座位释放、房主仍为座位1
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.Kick, TargetSeat = 2 });
            Assert.IsTrue(Pump(rig, 4000,
                () => rig.NoticesH2.Count > 0 && rig.Last1?.SeatState(2)?.PlayerName == null),
                "踢人后应通知被踢者并释放座位2");
            Assert.AreEqual(1, rig.Last1.HostSeat, "房主应仍为座位1");

            // 房主离开 → 房间空 → 销毁
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.LeaveRoom });
            Assert.IsTrue(Pump(rig, 4000, () => rig.Lobby.RoomCount == 0), "无人后房间应销毁");
        }

        // ==================== 流程 4：阶段守卫 / 异常回退 / 关闭通知（H1·H2·M1·M5） ====================

        /// <summary>共用前置：建房 → 加入 → 双方提交卡组 → 双方准备 → 房主开始 → 进入 InMatch。返回房间号。</summary>
        private static void PrepareMatch(Rig rig, out string roomId, bool expectInMatch = true)
        {
            string deckJson = DeckJson();
            Assert.IsNotNull(deckJson, "测试卡组应能加载");
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.CreateRoom });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1 != null), "房主应建房成功");
            roomId = rig.Last1.RoomId;

            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = roomId });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last2?.SeatState(2)?.PlayerName != null), "加入者应就座");
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckName = "房主卡组", DeckJson = deckJson });
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckName = "客人卡组", DeckJson = deckJson });
            Assert.IsTrue(Pump(rig, 4000,
                () => rig.Last1?.SeatState(1)?.DeckValid == true && rig.Last2?.SeatState(2)?.DeckValid == true),
                "双方卡组都应通过校验");
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            rig.Guest2.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1?.Phase == RoomPhase.Ready), "双方准备后应为 Ready");
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
            if (expectInMatch)
                Assert.IsTrue(Pump(rig, 8000, () => rig.Last1?.Phase == RoomPhase.InMatch), "房主开始后应进入 InMatch");
        }

        /// <summary>
        /// 【H2】对局中踢人必须被拒（否则房主踢掉对手 = 凭空获胜）；
        /// 【M5】对局中对手掉线（走真实 `OnSessionLeft` 链路）→ 判负 → 房间退回 `Waiting` 而不是卡在 `Finished`；
        /// 【M1】服务端停止 → 在座客户端收到 `RoomState(Closed)`。
        /// </summary>
        public void TestMatchGuardsAndRecovery()
        {
            using var rig = StartRig();
            Pump(rig, 3000, () => rig.Host1.MySessionId != null && rig.Guest2.MySessionId != null);
            PrepareMatch(rig, out string roomId);

            // H2：对局进行中踢人 → PhaseMismatch，且对局不受影响
            int n0 = rig.ResH1.Count;
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.Kick, TargetSeat = 2 });
            Assert.IsTrue(Pump(rig, 3000, () => rig.ResH1.Count > n0), "踢人请求应收到应答");
            Assert.AreEqual(LobbyErrorCode.PhaseMismatch, rig.ResH1[rig.ResH1.Count - 1].ErrorCode,
                "对局中踢人应报 PhaseMismatch");
            Assert.AreEqual(RoomPhase.InMatch, rig.Last1?.Phase, "对局不应因踢人被结束");
            Assert.IsNotNull(rig.Last1.SeatState(2).PlayerName, "对手应仍在座");
            Assert.IsNotNull(rig.Lobby.GetRoom(roomId)?.Match, "对局宿主不应被拆掉");

            // M5：座位2 掉线（真实链路：Lightning → LeaveRoomInternal → ForfeitSeat → 释放座位）
            rig.Lobby.OnSessionLeft(rig.Guest2.MySessionId);
            Assert.IsTrue(Pump(rig, 6000, () => rig.Last1?.Phase == RoomPhase.Waiting),
                $"对手掉线后房间应退回 Waiting（实际 {rig.Last1?.Phase}）");
            Assert.IsNull(rig.Last1.SeatState(2).PlayerName, "座位2 应已释放");
            Assert.IsNotNull(rig.Lobby.GetRoom(roomId), "房主还在，房间不应销毁");
            // 结果只在**战场页**展示：回到房间后不再保留结果
            Assert.IsNull(rig.Last1.MatchSummary, "结果只在战场页展示，回到房间后不再保留 Summary");

            // M1：服务端停止要关闭全部房间，并把 `Closed` 房间状态下发给在座客户端。
            // 拆成两条断言的原因：`ServerHost.Stop()` 会在关房后紧接着关传输，而本套件是同进程回环测试 ——
            // 客户端在这次调用期间没有机会轮询，"最后一条消息"是否收到取决于传输尾部行为，不适合作为判据；
            // 因此（a）验证 Stop 经 `ILobbyShutdown` 关掉了全部房间，（b）单独验证关房时客户端确实能收到 Closed。
            int n1 = rig.StateH1.Count;
            rig.Lobby.OnServerStopping("流程自检：服务端已停止");
            Assert.IsTrue(Pump(rig, 4000,
                () => rig.StateH1.Count > n1 && rig.StateH1[rig.StateH1.Count - 1].Phase == RoomPhase.Closed),
                "关房时应把 Closed 房间状态下发给在座客户端（客户端据此回大厅）");
            Assert.AreEqual(0, rig.Lobby.RoomCount, "关房后不应残留房间");

            // 重新建一个房间，验证完整 Stop() 的接线：经 ILobbyShutdown 关闭全部房间
            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.CreateRoom });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Lobby.RoomCount == 1), "房主应能再建一个房间");
            rig.Host.Stop();
            Assert.AreEqual(0, rig.Lobby.RoomCount, "ServerHost.Stop() 应关闭全部房间（经 ILobbyShutdown）");
        }

        /// <summary>【H1】建局中途抛异常 → 房间退回 `Waiting`（绝不卡在 `Starting`）、取消双方准备、不残留对局宿主。</summary>
        public void TestStartAbortRollsBack()
        {
            using var rig = StartRig(_ => new ThrowingMatchHost());
            Pump(rig, 3000, () => rig.Host1.MySessionId != null && rig.Guest2.MySessionId != null);
            PrepareMatch(rig, out string roomId, expectInMatch: false);

            Assert.IsTrue(Pump(rig, 6000, () => rig.NoticesH1.Exists(t => t != null && t.Contains("建局失败"))),
                "建局失败应通知玩家（可读原因）");
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1?.Phase == RoomPhase.Waiting),
                $"建局失败后应退回 Waiting（实际 {rig.Last1?.Phase}）");
            Assert.IsNull(rig.Lobby.GetRoom(roomId)?.Match, "失败后不应残留对局宿主");
            Assert.IsFalse(rig.Last1.SeatState(1).Ready, "失败后应取消准备");
            Assert.IsFalse(rig.Last1.SeatState(2).Ready, "失败后应取消双方准备");
            Assert.IsTrue(rig.Last1.SeatState(1).DeckValid, "卡组应保留（无需重选）");
        }

        /// <summary>【M4】决策回包必须由该请求对应的座位提交（对手代答/取消应被拒且不影响等待中的请求）。</summary>
        public void TestChoiceResultSeatGuard()
        {
            var transport = new LoopbackTransport();
            transport.Start();
            transport.RegisterSession("p1", _ => { });
            transport.RegisterSession("p2", _ => { });
            var gateway = new NetworkInputGateway(transport, seat => "p" + seat);

            var req = new ChoiceRequest { RequestKind = ChoiceRequest.Kind.SelectTarget };
            var task = gateway.RequestChoice(1, req);
            Assert.IsNotNull(req.RequestId, "网关应回填 RequestId（用于关联回包）");

            string json = NetEnvelope.Wrap(NetEnvelope.Types.ChoiceResult,
                ChoiceSerializer.SerializeResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = true }));

            Assert.IsTrue(gateway.TryHandleResult(json, 2), "越权回包应被受理但拒绝执行（不向上层报未处理）");
            Assert.IsFalse(task.IsCompleted, "座位2 提交的决策回包不应完成座位1 的等待");
            Assert.IsTrue(gateway.TryHandleResult(json, 1), "座位1 的合法回包应被处理");
            Assert.IsTrue(task.IsCompleted, "座位1 的合法回包应完成等待");
        }

        /// <summary>【M7】空闲会话回收：只回收"不在任何房间"的空闲连接，房间内的玩家不受影响。</summary>
        public void TestIdleSessionRecycle()
        {
            var cfg = new ServerConfig { Port = Port, MaxConnections = 16, MaxRooms = 4, SessionIdleTimeoutSeconds = 1 };
            using var rig = StartRig(null, cfg);
            Pump(rig, 4000, () => rig.Host1.MySessionId != null && rig.Guest2.MySessionId != null);

            rig.Host1.Send(new LobbyRequest { Kind = LobbyRequestKind.CreateRoom });
            Assert.IsTrue(Pump(rig, 3000, () => rig.Last1 != null), "房主应建房成功（此后房主在房间里）");

            Thread.Sleep(1300);   // 双方都沉默超过 1s
            // 判据放在**服务端效果**上：回收是"断开连接"，而断开前那条通知在回环测试里同样受传输尾部影响
            // （客户端在 Tick 内没有轮询机会），因此这里断言"不在房间的空闲会话被回收、房间内的不受影响"。
            Assert.IsTrue(Pump(rig, 6000, () => !rig.Host.IsConnected(rig.Guest2.MySessionId)),
                "不在房间里的空闲会话应被回收（断开）");
            Assert.IsTrue(rig.Host.IsConnected(rig.Host1.MySessionId), "房间内的会话不应被空闲回收");
            Assert.IsNotNull(rig.Lobby.GetRoom(rig.Last1.RoomId), "房主所在房间应仍在");
        }

        /// <summary>H1 用：`StartServing` 必抛异常的假对局宿主（模拟"建局装配中途失败"）。</summary>
        private sealed class ThrowingMatchHost : IRoomMatchHost
        {
            public void StartServing() => throw new InvalidOperationException("流程自检：模拟建局装配异常");
            public void StopServing() { }
            // 本桩在 StartServing 即抛异常，房间层会走 AbortStart 回退，永远不会走到第0回合调度
            public void StartMulligan() => throw new InvalidOperationException("流程自检：模拟进入调度异常");
            public void BindSeat(int seat, string sessionId) { }
            public void UnbindSession(string sessionId) { }
            public int SeatOfSession(string sessionId) => 0;
            public string SessionOfSeat(int seat) => null;
            public bool HandleMessage(string sessionId, string envelopeType, string payload) => false;
            public void PushSnapshot() { }
            public void ForfeitSeat(int seat, string reason) { }
            public bool IsOver => false;
            public int WinnerSeat => 0;
            public Action<int> OnGameOver { get; set; }
        }
    }
}
