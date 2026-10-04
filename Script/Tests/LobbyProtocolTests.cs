using System.Collections.Generic;
using Script.Net;
using Script.Net.Lobby;

namespace Script.Tests
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】协议层验证：
    ///  1) 五类大厅数据（LobbyRequest/LobbyResponse/RoomState/RoomList/ServerNotice）经 JSON 往返不丢字段；
    ///  2) **信息可见性红线（D7）**：`RoomState.ForViewer` 必须把对手座位的卡组信息裁掉；
    ///  3) 信封新类型可 wrap/unwrap，旧格式（非信封）仍按原样返回（向后兼容）；
    ///  4) 服务端配置默认值与命令行解析（D8）。
    /// </summary>
    public class LobbyProtocolTests : TestRunner
    {
        public void TestLobbyRequestRoundTrip()
        {
            var req = new LobbyRequest
            {
                RequestId = "c1",
                Kind = LobbyRequestKind.JoinRoom,
                ProtocolVersion = LobbyProtocol.ProtocolVersion,
                PlayerName = "我太帅了",
                RoomId = "room_7",
                Ready = true,
                DeckId = "deck_jc014",
                DeckJson = "{\"cards\":[]}",
                DeckName = "JC014 测试卡组",
                TargetSeat = 2,
                ClientTimeMs = 123456789,
                RoomSettings = new RoomSettings { InitialAreaCount = 4 },
            };

            var back = LobbySerializer.DeserializeRequest(LobbySerializer.Serialize(req));
            Assert.IsNotNull(back, "LobbyRequest 应能往返");
            Assert.AreEqual("c1", back.RequestId, "RequestId 往返");
            Assert.AreEqual(LobbyRequestKind.JoinRoom, back.Kind, "Kind 往返");
            Assert.AreEqual("我太帅了", back.PlayerName, "昵称往返（含中文）");
            Assert.AreEqual("room_7", back.RoomId, "RoomId 往返");
            Assert.IsTrue(back.Ready, "Ready 往返");
            Assert.AreEqual("deck_jc014", back.DeckId, "DeckId 往返");
            Assert.AreEqual("JC014 测试卡组", back.DeckName, "DeckName 往返");
            Assert.AreEqual(2, back.TargetSeat, "TargetSeat 往返");
            Assert.AreEqual(123456789L, back.ClientTimeMs, "ClientTimeMs 往返");
            Assert.IsNotNull(back.RoomSettings, "RoomSettings 往返");
            Assert.AreEqual(4, back.RoomSettings.InitialAreaCount, "InitialAreaCount 往返");
        }

        public void TestLobbyResponseAndNoticeRoundTrip()
        {
            var res = LobbyResponse.Fail("c9", LobbyErrorCode.RoomFull);
            res.SessionId = "s2";
            var back = LobbySerializer.DeserializeResponse(LobbySerializer.Serialize(res));
            Assert.IsNotNull(back, "LobbyResponse 应能往返");
            Assert.IsFalse(back.Ok, "Ok=false 往返");
            Assert.AreEqual(LobbyErrorCode.RoomFull, back.ErrorCode, "ErrorCode 往返");
            Assert.IsFalse(string.IsNullOrEmpty(back.ErrorText), "未给具体原因时应填默认文案");
            Assert.AreEqual("s2", back.SessionId, "SessionId 往返（客户端据此获知自身会话）");

            var notice = ServerNotice.Of(LobbyErrorCode.NotHost, null, "room_1");
            var nBack = LobbySerializer.DeserializeNotice(LobbySerializer.Serialize(notice));
            Assert.IsNotNull(nBack, "ServerNotice 应能往返");
            Assert.AreEqual(LobbyErrorCode.NotHost, nBack.Code, "通知错误码往返");
            Assert.AreEqual("room_1", nBack.RoomId, "通知房间号往返");
            Assert.IsFalse(string.IsNullOrEmpty(nBack.Text), "通知文案应回退默认值");
        }

        /// <summary>D7：对手座位的卡组信息必须不可见；准备/在线/违规状态仍可见。</summary>
        public void TestRoomStateForViewerHidesOpponentDeck()
        {
            var state = BuildTwoSeatState();

            var viewFor1 = state.ForViewer(1);
            Assert.AreEqual(1, viewFor1.ViewerSeat, "裁剪后应记录查看者座位");

            var me = viewFor1.SeatState(1);
            var opp = viewFor1.SeatState(2);
            Assert.IsNotNull(me, "应有自己座位");
            Assert.IsNotNull(opp, "应有对手座位");

            // 自己：卡组信息可见
            Assert.IsTrue(me.HasDeck, "自己的 HasDeck 可见");
            Assert.AreEqual("我的卡组", me.DeckName, "自己的卡组名可见");
            Assert.AreEqual(30, me.DeckCardCount, "自己的卡组张数可见");
            Assert.IsTrue(me.DeckValid, "自己的校验结果可见");
            Assert.IsNull(me.DeckError, "自己无违规时原因为空");

            // 对手：卡组信息必须不可见（D7）
            Assert.IsFalse(opp.HasDeck, "对手的 HasDeck 必须不可见");
            Assert.IsNull(opp.DeckName, "对手的卡组名必须不可见");
            Assert.AreEqual(0, opp.DeckCardCount, "对手的卡组张数必须不可见");
            Assert.IsNull(opp.DeckError, "对手的违规原因必须不可见（可能含卡牌信息）");
            Assert.IsTrue(opp.Ready, "对手的准备状态可见");
            Assert.IsTrue(opp.Connected, "对手的在线状态可见");
            // 对手"是否违规"作为状态可见（界面显示"卡组违规"，但不给原因）
            Assert.IsFalse(opp.DeckValid, "对手是否违规可见（仅状态）");

            // 原始对象不被裁剪污染（裁剪必须是副本）
            Assert.IsTrue(state.SeatState(2).HasDeck, "原始 RoomState 不应被 ForViewer 就地修改");
            Assert.AreEqual("对手卡组", state.SeatState(2).DeckName, "原始对手卡组名应保留在服务端侧");

            // 显示名（D9）：昵称#座位
            Assert.AreEqual("我#1", state.SeatState(1).DisplayName, "显示名应为 昵称#座位");
            Assert.AreEqual("对手#2", state.SeatState(2).DisplayName, "对手显示名同样带座位号");
            Assert.AreEqual("空位#2", RoomState.MakeDisplayName("", 2), "空昵称座位显示为 空位#座位");
        }

        public void TestRoomListRoundTrip()
        {
            var list = new RoomListSnapshot { MaxRooms = 8 };
            list.Rooms.Add(new RoomBrief { RoomId = "room_1", RoomName = "测试房间", Phase = RoomPhase.Waiting, PlayerCount = 1, HostDisplayName = "我#1" });
            var back = LobbySerializer.DeserializeRoomList(LobbySerializer.Serialize(list));
            Assert.IsNotNull(back, "RoomListSnapshot 应能往返");
            Assert.AreEqual(1, back.Rooms.Count, "房间数往返");
            Assert.AreEqual("room_1", back.Rooms[0].RoomId, "房间 id 往返");
            Assert.AreEqual(2, back.Rooms[0].MaxPlayers, "最大人数默认 2");
            Assert.AreEqual(8, back.MaxRooms, "MaxRooms 往返");
        }

        public void TestEnvelopeNewTypesAndLegacyCompat()
        {
            string wrapped = NetEnvelope.Wrap(NetEnvelope.Types.LobbyRequest, "{\"kind\":0}");
            Assert.AreEqual(NetEnvelope.Types.LobbyRequest, NetEnvelope.UnwrapType(wrapped), "新类型 lobby_req 应可识别");
            Assert.IsTrue(NetEnvelope.TryUnwrap(wrapped, out var t, out var p), "新类型应可解包");
            Assert.AreEqual(NetEnvelope.Types.LobbyRequest, t, "解包类型正确");
            Assert.AreEqual("{\"kind\":0}", p, "载荷应原样取出");

            foreach (var type in new[]
            {
                NetEnvelope.Types.LobbyResponse, NetEnvelope.Types.RoomState,
                NetEnvelope.Types.RoomList, NetEnvelope.Types.ServerNotice,
            })
            {
                Assert.AreEqual(type, NetEnvelope.UnwrapType(NetEnvelope.Wrap(type, "{}")), $"{type} 应可识别");
            }

            // 旧格式（非信封）仍按原样返回 → 旧客户端/旧消息不被打断
            Assert.IsFalse(NetEnvelope.TryUnwrap("{\"requestId\":\"x\"}", out _, out var legacyPayload), "非信封应判为旧格式");
            Assert.AreEqual("{\"requestId\":\"x\"}", legacyPayload, "旧格式载荷应原样返回");

            // 载荷内含 '|' 不应被错误切分
            string withPipe = NetEnvelope.Wrap(NetEnvelope.Types.RoomState, "{\"a\":\"x|y\"}");
            Assert.AreEqual("{\"a\":\"x|y\"}", NetEnvelope.UnwrapPayload(withPipe), "载荷中的 | 不应影响解析");
        }

        public void TestServerConfigDefaultsAndArgs()
        {
            var def = new ServerConfig();
            Assert.AreEqual((ushort)18950, def.Port, "默认端口 18950");
            Assert.AreEqual(16, def.MaxConnections, "默认最大连接 16");
            Assert.AreEqual(8, def.MaxRooms, "默认最大房间 8");
            Assert.AreEqual(12, def.NameMaxLen, "默认昵称上限 12");
            Assert.AreEqual(600, def.FinishedRoomTtlSeconds, "默认结束房保留 600s");
            Assert.AreEqual(LobbyProtocol.ProtocolVersion, def.ProtocolVersion, "默认协议版本与常量一致");

            var cfg = ServerConfig.FromArgs(new[]
            {
                "--path", "res://Scenes/Net/NetServer.tscn", "--port", "19000",
                "--max-rooms", "3", "--max-conns", "5", "--name-max", "8",
                "--finished-ttl", "60", "--verbose",
            });
            Assert.AreEqual((ushort)19000, cfg.Port, "--port 生效");
            Assert.AreEqual(3, cfg.MaxRooms, "--max-rooms 生效");
            Assert.AreEqual(5, cfg.MaxConnections, "--max-conns 生效");
            Assert.AreEqual(8, cfg.NameMaxLen, "--name-max 生效");
            Assert.AreEqual(60, cfg.FinishedRoomTtlSeconds, "--finished-ttl 生效");
            Assert.IsTrue(cfg.Verbose, "--verbose 生效");

            // 无法识别的参数与非法值不应破坏默认值
            var cfg2 = ServerConfig.FromArgs(new[] { "--bogus", "x", "--port", "not-a-number" });
            Assert.AreEqual((ushort)18950, cfg2.Port, "非法端口值应保持默认，不抛异常");
        }

        private static RoomState BuildTwoSeatState()
        {
            var state = new RoomState
            {
                RoomId = "room_1",
                RoomName = "测试房间",
                Phase = RoomPhase.Ready,
                HostSeat = 1,
                Settings = new RoomSettings { InitialAreaCount = 3 },
                Slots = new List<RoomSeatState>
                {
                    new RoomSeatState
                    {
                        Seat = 1, PlayerName = "我", DisplayName = RoomState.MakeDisplayName("我", 1),
                        Ready = true, Connected = true,
                        HasDeck = true, DeckName = "我的卡组", DeckCardCount = 30, DeckValid = true, DeckError = null,
                    },
                    new RoomSeatState
                    {
                        Seat = 2, PlayerName = "对手", DisplayName = RoomState.MakeDisplayName("对手", 2),
                        Ready = true, Connected = true,
                        HasDeck = true, DeckName = "对手卡组", DeckCardCount = 40, DeckValid = false, DeckError = "卡牌 JC999 不存在",
                    },
                },
            };
            return state;
        }
    }
}
