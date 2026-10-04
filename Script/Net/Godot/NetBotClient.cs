using System;
using Godot;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】**脚本化客户端（BOT）**：用于在一台机器上真跑"完整流程"。
    ///
    /// 用法（配合 `NetServer` 三进程）：
    ///   # 服务端
    ///   godot --headless --path &lt;工程&gt; res://Scenes/Net/NetServer.tscn -- --port 18950
    ///   # 房主 BOT（创建房间）
    ///   godot --headless --path &lt;工程&gt; res://Scenes/Net/NetEntry.tscn -- --bot 18950 host 127.0.0.1
    ///   # 加入者 BOT（加入房间）
    ///   godot --headless --path &lt;工程&gt; res://Scenes/Net/NetEntry.tscn -- --bot 18950 join 127.0.0.1
    ///
    /// 行为：Hello → ListRooms →（host）CreateRoom /（join）JoinRoom → SelectDeck → Ready →
    ///       （host，两人就绪后）StartMatch → 收到对局快照后每 3 秒让过一次 → 结束时（可选）请求重开。
    /// 纪律：本类只用于**开发/冒烟**，不参与正式玩法；所有操作都走真实协议（不直接改服务端状态）。
    /// </summary>
    public class NetBotClient
    {
        private readonly ClientSession _session;
        private readonly bool _isHost;
        private readonly string _deckPath;

        private bool _helloed, _listed, _deckSent, _readySent, _startSent;
        private double _timer;
        private RoomState _room;
        private string _roomId;

        /// <summary>收到对局快照次数（外部可观察流程是否推进到对局阶段）。</summary>
        public int SnapshotCount { get; private set; }
        /// <summary>阶段日志（便于人工观察）。</summary>
        public string LastStage { get; private set; } = "connecting";

        public NetBotClient(ClientSession session, bool isHost, string deckPath)
        {
            _session = session;
            _isHost = isHost;
            _deckPath = string.IsNullOrEmpty(deckPath) ? "res://Decks/default_deck.json" : deckPath;

            _session.OnLobbyResponse += OnResponse;
            _session.OnRoomState += OnRoomState;
            _session.OnRoomList += OnRoomList;
            _session.OnConnected += OnConnected;
            _session.OnNotice += n => GD.Print($"[BOT {(_isHost ? "host" : "join")}] 通知：{n.Text}");
            _session.OnMatchEnvelope += OnMatchEnvelope;
        }

        public void Poll(double delta)
        {
            _session.Poll();
            if (_session.LinkFailed) return;

            // 对局内"让过"计时必须在下面的节流 return 之前累计（否则被 0.5s 节流吃掉时间）
            if (_room != null && _room.Phase == RoomPhase.InMatch) { SendPassIfDue(delta); return; }

            _timer += delta;
            if (_timer < 0.5) return;
            _timer = 0;

            if (!_helloed) return;                 // Hello 由 ClientSession.Connect 自动发送
            if (!_listed) { ListRooms(); return; }
            if (_roomId == null) return;           // 等建房/加入完成
            if (!_deckSent) { SendDeck(); return; }
            if (!_readySent && _room != null && _room.Phase != RoomPhase.InMatch) { SetReady(); return; }
            if (_isHost && !_startSent && _room != null && _room.SeatsReadyForStart()) { StartMatch(); return; }
        }

        public void OnConnected()
        {
            _helloed = true;
            LastStage = "hello-sent";
            GD.Print($"[BOT {(_isHost ? "host" : "join")}] 已连接，等待大厅应答…");
        }

        private void ListRooms()
        {
            _listed = true;
            LastStage = "list-rooms";
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.ListRooms });
        }

        private void SendDeck()
        {
            _deckSent = true;
            LastStage = "select-deck";
            var deck = Script.IO.DeckListLoader.Load(_deckPath);
            if (deck == null)
            {
                GD.PrintErr($"[BOT] 卡组加载失败：{_deckPath}，改为用空 JSON（预期会被服务端判违规，用于验证校验链路）");
                _session.Send(new LobbyRequest { Kind = LobbyRequestKind.SelectDeck, DeckJson = "{}" });
                return;
            }
            // 以**内联 JSON** 提交（服务端不一定持有本机卡组文件）
            _session.Send(new LobbyRequest
            {
                Kind = LobbyRequestKind.SelectDeck,
                DeckName = deck.DeckName,
                DeckJson = Script.IO.DeckListLoader.ToJsonText(deck),
            });
        }

        private void SetReady()
        {
            _readySent = true;
            LastStage = "ready";
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
        }

        private void StartMatch()
        {
            _startSent = true;
            LastStage = "start-match";
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
        }

        private void SendPassIfDue(double delta)
        {
            _passTimer += delta;
            if (_passTimer < 3.0) return;
            _passTimer = 0;
            _passCount++;
            LastStage = $"in-match(pass #{_passCount})";
            // 用既有意图通道让过（对局内身份=座位，由服务端校验）
            _session.SendMatchIntent(Script.Net.PlayerIntent.Kind.Pass);
            GD.Print($"[BOT {(_isHost ? "host" : "join")}] 对局中：让过（第 {_passCount} 次）");
        }
        private double _passTimer;
        private int _passCount;

        // ==================== 下行事件 ====================

        private void OnResponse(LobbyResponse res)
        {
            if (!res.Ok)
            {
                GD.PrintErr($"[BOT {(_isHost ? "host" : "join")}] 请求失败 {res.ErrorCode}：{res.ErrorText}");
                if (res.ErrorCode == LobbyErrorCode.RoomNotFound) _roomId = null;   // 重试列表
                return;
            }
            // 房间列表随 ListRooms 的**应答**下发（`LobbyResponse.RoomList`）；
            // 独立的 `room_list` 推送为后续"列表主动刷新"预留，故此处两条路径共用同一处理。
            if (res.RoomList != null) { HandleRoomList(res.RoomList); return; }
            if (res.RoomState != null)
            {
                _roomId = res.RoomState.RoomId;
                _room = res.RoomState;
            }
        }

        private void OnRoomState(RoomState state)
        {
            _room = state;
            _roomId = state.RoomId;
            LastStage = $"room-{state.Phase}";
            GD.Print($"[BOT {(_isHost ? "host" : "join")}] 房间 {state.RoomId} 阶段={state.Phase} "
                + $"座位1={state.SeatState(1)?.DisplayName}({Ready(state.SeatState(1))}) "
                + $"座位2={state.SeatState(2)?.DisplayName}({Ready(state.SeatState(2))})");
        }

        private static string Ready(RoomSeatState s) => s == null ? "-" : (s.Ready ? "已准备" : "未准备");

        private void OnRoomList(RoomListSnapshot list) => HandleRoomList(list);

        private void HandleRoomList(RoomListSnapshot list)
        {
            GD.Print($"[BOT {(_isHost ? "host" : "join")}] 房间列表：{list.Rooms.Count} 间（上限 {list.MaxRooms}）");
            if (_isHost)
            {
                _session.Send(new LobbyRequest { Kind = LobbyRequestKind.CreateRoom, RoomSettings = new RoomSettings { InitialAreaCount = 3 } });
            }
            else
            {
                if (list.Rooms.Count == 0)
                {
                    // 房主还没建好：稍后重试列表
                    _listed = false;
                    return;
                }
                _session.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = list.Rooms[0].RoomId });
            }
        }

        private void OnMatchEnvelope(string type, string payload)
        {
            if (type == "snap")
            {
                SnapshotCount++;
                if (SnapshotCount == 1) GD.Print($"[BOT {(_isHost ? "host" : "join")}] 已收到初始对局快照（进入对局）");
            }
            else if (type == NetEnvelope.Types.ChoiceRequest)
            {
                // 冒烟用：请决策一律回取消，避免卡住（真实玩家由 UI 决策）
                var req = ChoiceSerializer.DeserializeRequest(payload);
                GD.Print($"[BOT] 收到决策请求 {req?.RequestKind}，自动取消（冒烟）");
                _session.SendChoiceResult(new ChoiceResult { RequestId = req?.RequestId, Cancelled = true });
            }
        }
    }

    /// <summary>BOT 用的小工具（放在同文件，避免污染正式 API）。</summary>
    public static class BotRoomStateExtensions
    {
        /// <summary>两人在座且都已准备（≡ 服务端 `RoomPhase.Ready`）。</summary>
        public static bool SeatsReadyForStart(this RoomState state)
            => state != null && state.Phase == RoomPhase.Ready;
    }
}
