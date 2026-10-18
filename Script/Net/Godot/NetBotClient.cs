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
    ///       （host，两人就绪后）StartMatch → 收到对局快照后每 N 秒让过一次 → 结束时（可选）请求重开。
    /// 纪律：本类只用于**开发/冒烟/压测**，不参与正式玩法；所有操作都走真实协议（不直接改服务端状态）。
    ///
    /// 【2026-10-17 · 批量压测】三处调整，目的是让"一个进程内跑 N 对 BOT"能真正造出 N 间并发房：
    ///   1. **join 不再固定加入 `Rooms[0]`** —— 改为挑"仍在等待且有空位"的房间。
    ///      原来固定第一间：多房间时第 3 个 join BOT 起必然失败，而失败码非 RoomNotFound 时又不重置 `_roomId`
    ///      → BOT 静默卡死，压测出来的"并发房间数"全是假的。
    ///   2. **让过间隔可配**（默认 3s，压测可设 0.2~0.5s 加负载）。
    ///   3. **可静默**（<see cref="Verbose"/>）—— 16 个 BOT 每收一次房间状态就打一行，日志 I/O 会把被测曲线污染掉。
    /// </summary>
    public class NetBotClient
    {
        private readonly ClientSession _session;
        private readonly bool _isHost;
        private readonly string _deckPath;
        /// <summary>对局内「让过」的间隔（秒）。默认 3 秒（冒烟节奏）；压测可调到 0.2~0.5 加负载。</summary>
        private readonly double _passIntervalSeconds;

        private bool _helloed, _listed;
        private double _timer;
        private RoomState _room;
        private string _roomId;

        // 【换房重试】按"房间 id"记进度而不是 bool：
        // 失败后 BOT 会清掉房间、重新挑选一间，此时必须能重新提交卡组/准备/开局。
        // 用 bool 的话，重试进的新房间永远提交不了卡组（`_deckSent` 还是 true）→ 卡在"未准备"。
        private string _deckSentRoom;
        private string _readySentRoom;
        private string _startSentRoom;

        /// <summary>连续失败次数（超过阈值就放弃重试并报错，避免永久错误打成死循环）。</summary>
        private int _failCount;
        private const int MaxRetryOnFailure = 12;
        /// <summary>已放弃（连续失败过多）。</summary>
        public bool GaveUp { get; private set; }

        /// <summary>
        /// 是否打印逐条流程日志。默认 true（保持 `--bot` 冒烟时的观察体验）；
        /// 批量压测时置 false —— 16 个 BOT 每收到一次房间状态就打印，会变成几十 MB 的日志 I/O，
        /// 直接把被测的 CPU/内存曲线污染掉。
        /// </summary>
        public bool Verbose { get; set; } = true;

        /// <summary>日志前缀标签（压测时带序号，便于在成百行日志里定位是哪一对 BOT）。</summary>
        public string Tag { get; set; }

        /// <summary>收到对局快照次数（外部可观察流程是否推进到对局阶段）。</summary>
        public int SnapshotCount { get; private set; }
        /// <summary>阶段日志（便于人工观察）。</summary>
        public string LastStage { get; private set; } = "connecting";

        /// <summary>当前所在房间 id（未进房为 null）。供压测工具统计"到底凑成了几间房"。</summary>
        public string RoomId => _roomId;
        /// <summary>当前房间状态（未进房为 null）。</summary>
        public RoomState Room => _room;
        /// <summary>是否已进入对局阶段。</summary>
        public bool InMatch => _room != null && _room.Phase == RoomPhase.InMatch;
        /// <summary>已发出的「让过」次数。</summary>
        public int PassCount => _passCount;
        /// <summary>本会话标识（服务端分配的会话 id；未登录前为 null）。</summary>
        public string SessionId => _session?.MySessionId;

        private string RoleTag => _isHost ? "host" : "join";
        private string Prefix => string.IsNullOrEmpty(Tag) ? $"BOT {RoleTag}" : $"BOT {RoleTag}#{Tag}";

        /// <summary>逐条流程日志（<see cref="Verbose"/> 关闭时静默）。</summary>
        private void Log(string msg) { if (Verbose) GD.Print(msg); }
        /// <summary>错误日志：无论 Verbose 都打印（失败不能被压测的静默模式吃掉）。</summary>
        private void LogErr(string msg) => GD.PrintErr(msg);

        public NetBotClient(ClientSession session, bool isHost, string deckPath, double passIntervalSeconds = 3.0)
        {
            _session = session;
            _isHost = isHost;
            _deckPath = string.IsNullOrEmpty(deckPath) ? "res://Decks/default_deck.json" : deckPath;
            _passIntervalSeconds = passIntervalSeconds > 0 ? passIntervalSeconds : 3.0;

            _session.OnLobbyResponse += OnResponse;
            _session.OnRoomState += OnRoomState;
            _session.OnRoomList += OnRoomList;
            _session.OnConnected += OnConnected;
            _session.OnNotice += n => Log($"[{Prefix}] 通知：{n.Text}");
            _session.OnMatchEnvelope += OnMatchEnvelope;
        }

        public void Poll(double delta)
        {
            if (GaveUp) return;
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
            if (_deckSentRoom != _roomId) { SendDeck(); return; }
            if (_readySentRoom != _roomId && _room != null && _room.Phase != RoomPhase.InMatch) { SetReady(); return; }
            if (_isHost && _startSentRoom != _roomId && _room != null && _room.SeatsReadyForStart()) { StartMatch(); return; }
        }

        public void OnConnected()
        {
            _helloed = true;
            LastStage = "hello-sent";
            Log($"[{Prefix}] 已连接，等待大厅应答…");
        }

        private void ListRooms()
        {
            _listed = true;
            LastStage = "list-rooms";
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.ListRooms });
        }

        private void SendDeck()
        {
            _deckSentRoom = _roomId;
            LastStage = "select-deck";
            var deck = Script.IO.DeckListLoader.Load(_deckPath);
            if (deck == null)
            {
                LogErr($"[{Prefix}] 卡组加载失败：{_deckPath}，改为用空 JSON（预期会被服务端判违规，用于验证校验链路）");
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
            _readySentRoom = _roomId;
            LastStage = "ready";
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = true });
        }

        private void StartMatch()
        {
            _startSentRoom = _roomId;
            LastStage = "start-match";
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
        }

        private void SendPassIfDue(double delta)
        {
            _passTimer += delta;
            if (_passTimer < _passIntervalSeconds) return;
            _passTimer = 0;
            _passCount++;
            LastStage = $"in-match(pass #{_passCount})";
            // 用既有意图通道让过（对局内身份=座位，由服务端校验）
            _session.SendMatchIntent(Script.Net.PlayerIntent.Kind.Pass);
            Log($"[{Prefix}] 对局中：让过（第 {_passCount} 次）");
        }
        private double _passTimer;
        private int _passCount;

        // ==================== 下行事件 ====================

        private void OnResponse(LobbyResponse res)
        {
            if (!res.Ok)
            {
                _failCount++;
                LogErr($"[{Prefix}] 请求失败 {res.ErrorCode}：{res.ErrorText}（连续第 {_failCount} 次）");

                if (_failCount > MaxRetryOnFailure)
                {
                    GaveUp = true;
                    LogErr($"[{Prefix}] 连续失败超过 {MaxRetryOnFailure} 次，放弃重试（请检查服务端日志与房间上限）");
                    return;
                }

                // 房间相关失败（房间不存在 / 已满 / 已开局）→ 清掉进房进度，重新拉列表挑另一间。
                // 不重置卡组/准备/开局：那三个是按房间 id 记的，换房后自然会重新提交。
                _roomId = null;
                _room = null;
                _listed = false;
                return;
            }

            _failCount = 0;

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
            Log($"[{Prefix}] 房间 {state.RoomId} 阶段={state.Phase} "
                + $"座位1={state.SeatState(1)?.DisplayName}({Ready(state.SeatState(1))}) "
                + $"座位2={state.SeatState(2)?.DisplayName}({Ready(state.SeatState(2))})");
        }

        private static string Ready(RoomSeatState s) => s == null ? "-" : (s.Ready ? "已准备" : "未准备");

        private void OnRoomList(RoomListSnapshot list) => HandleRoomList(list);

        private void HandleRoomList(RoomListSnapshot list)
        {
            Log($"[{Prefix}] 房间列表：{list.Rooms.Count} 间（上限 {list.MaxRooms}）");

            if (_isHost)
            {
                _session.Send(new LobbyRequest { Kind = LobbyRequestKind.CreateRoom, RoomSettings = new RoomSettings { InitialAreaCount = 3 } });
                return;
            }

            // 【批量压测修复】原来固定 `list.Rooms[0]`：N 对 BOT 时所有 join 都挤第一间房（每房只有 2 个座位），
            // 第 3 个起必然失败，于是"并发房间数"永远凑不到 N。
            // 现改为：挑第一个"仍在等待且有空位"的房间；都没有就等下一轮列表。
            RoomBrief target = null;
            for (int i = 0; i < list.Rooms.Count; i++)
            {
                var r = list.Rooms[i];
                if (r == null || string.IsNullOrEmpty(r.RoomId)) continue;
                if (r.Phase != RoomPhase.Waiting) continue;
                if (r.PlayerCount >= r.MaxPlayers) continue;
                target = r;
                break;
            }

            if (target == null)
            {
                // 房主还没建好，或所有房都满/已开局：稍后重新拉列表
                _listed = false;
                return;
            }

            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = target.RoomId });
        }

        private void OnMatchEnvelope(string type, string payload)
        {
            if (type == "snap")
            {
                SnapshotCount++;
                if (SnapshotCount == 1) Log($"[{Prefix}] 已收到初始对局快照（进入对局）");
            }
            else if (type == NetEnvelope.Types.ChoiceRequest)
            {
                // 冒烟用：请决策一律回取消，避免卡住（真实玩家由 UI 决策）
                var req = ChoiceSerializer.DeserializeRequest(payload);
                Log($"[{Prefix}] 收到决策请求 {req?.RequestKind}，自动取消（冒烟）");
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
