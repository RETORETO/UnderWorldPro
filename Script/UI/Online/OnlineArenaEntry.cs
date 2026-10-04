using Godot;
using Script.Net.Godot;
using Script.Net.Lobby;
using Script.UI;

namespace Script.UI.Online
{
    /// <summary>
    /// 【大厅/房间 UI · 增量 B】**联机战场页**：把既有的 `ArenaOl` 战场挂到"大厅会话"上。
    ///
    /// 与旧的单场路径（`NetEntry --client` → `NetworkMatchLauncher`）区别：
    ///   · 本页的连接来自 `OnlineClientContext`（大厅同一条连接）；
    ///   · 战场数据仍走同一套 `ClientMatchView` 投影与 `IMatchClientView` 接口（增量 B 抽出），
    ///     故 `ArenaOl` 的类型与交互逻辑**一行未改**。
    /// 对局结束后：先在**战场页**展示获胜者（`MatchResultDisplaySeconds` 秒，由服务端决定时长），
    /// 服务端随后自动复用原房间并广播 `Waiting`，本页再切回房间页——玩家无需点任何按钮。
    /// </summary>
    public partial class OnlineArenaEntry : Node
    {
        private ClientSession _session;
        private ArenaOl _arena;

        public override void _Ready()
        {
            _session = OnlineClientContext.Take();
            if (_session == null)
            {
                GD.PrintErr("[OnlineArenaEntry] 没有活动会话，返回大厅");
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
                return;
            }

            var scene = GD.Load<PackedScene>("res://Scenes/Net/ArenaOl.tscn");
            _arena = scene?.Instantiate<ArenaOl>();
            if (_arena == null)
            {
                GD.PrintErr("[OnlineArenaEntry] ArenaOl.tscn 加载失败");
                return;
            }
            AddChild(_arena);
            _arena.AttachSession(_session, _session.MySeat);

            _session.OnRoomState += OnRoomState;
            _session.OnDisconnected += OnDisconnected;
            GD.Print($"[OnlineArenaEntry] 战场已挂载（座位 {_session.MySeat}，连接=大厅同一会话）");
        }

        public override void _Process(double delta)
        {
            _session?.Poll();

            // 冒烟模式：进入战场若干秒后自动认输（等同玩家点「投降」），用于验证"结束→战场页展示结果→自动回房间页"闭环
            if (!OnlineClientContext.SmokeMode || _smokeConceded || _session == null) return;
            _smokeTimer += delta;
            if (_smokeTimer < 8.0) return;
            _smokeConceded = true;
            GD.Print("[OnlineArenaEntry][冒烟] 自动认输（验证对局结束→回房间页）");
            _session.SendMatchIntent(Script.Net.PlayerIntent.Kind.Concede);
        }
        private double _smokeTimer;
        private bool _smokeConceded;

        private void OnRoomState(RoomState state)
        {
            if (state == null) return;
            // 房间被销毁/服务端停止：直接回大厅（房间页拿不到座位，去了也只会再跳一次）
            if (state.Phase == RoomPhase.Closed)
            {
                GD.Print("[OnlineArenaEntry] 房间已关闭，返回大厅");
                OnlineClientContext.Disconnect();
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
                return;
            }
            // 对局结束（Finished）→ **停在战场页**展示获胜者，等服务端展示结束后复用房间再切走。
            if (state.Phase == RoomPhase.Finished)
            {
                int winner = state.MatchSummary?.WinnerSeat ?? 0;
                GD.Print($"[OnlineArenaEntry] 对局结束：获胜座位 {winner}，战场页展示结果（稍后自动回到房间）");
                _arena?.ShowMatchResult(winner);
                return;
            }
            // 房间回到可玩阶段（服务端已自动复用原房间）→ 回房间页（房间页不再显示结果，直接可重新准备）
            if (state.Phase == RoomPhase.Waiting || state.Phase == RoomPhase.Ready)
            {
                GD.Print($"[OnlineArenaEntry] 房间已回到 {state.Phase}，返回房间页");
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.RoomOnline);
            }
        }

        /// <summary>【H3】与服务端断开：释放会话并回大厅提示（对局由服务端按 D5 判负结束）。</summary>
        private void OnDisconnected()
        {
            GD.PrintErr("[OnlineArenaEntry] 与服务端断开连接，返回大厅");
            OnlineClientContext.Disconnect();
            GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
        }

        public override void _ExitTree()
        {
            if (_session != null)
            {
                _session.OnRoomState -= OnRoomState;
                _session.OnDisconnected -= OnDisconnected;
            }
            base._ExitTree();
        }
    }
}
