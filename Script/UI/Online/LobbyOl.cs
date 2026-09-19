using System.Collections.Generic;
using Godot;
using Script.Net.Godot;
using Script.Net.Lobby;

namespace Script.UI.Online
{
    /// <summary>
    /// 【大厅/房间 UI · 增量 B】**联机大厅页**（在线专用；单机 `Lobby.tscn` 保持原样不动）。
    ///
    /// 职责：填服务端地址/端口/昵称 → 连接 → 拉取房间列表 → 创建或加入房间 → 切到房间页。
    /// 数据全部来自服务端（`ClientSession` 事件）；本页不判断房主/准备等规则。
    /// </summary>
    public partial class LobbyOl : Control
    {
        private LineEdit _hostInput, _portInput, _nameInput;
        private Button _connectBtn, _refreshBtn, _createBtn, _backBtn;
        private Label _statusLabel;
        private VBoxContainer _roomListBox;

        private ClientSession _session;
        private readonly List<Button> _joinButtons = new List<Button>();

        public override void _Ready()
        {
            _hostInput = GetNodeOrNull<LineEdit>("Panel/HostInput");
            _portInput = GetNodeOrNull<LineEdit>("Panel/PortInput");
            _nameInput = GetNodeOrNull<LineEdit>("Panel/NameInput");
            _connectBtn = GetNodeOrNull<Button>("Panel/ConnectButton");
            _refreshBtn = GetNodeOrNull<Button>("Panel/RefreshButton");
            _createBtn = GetNodeOrNull<Button>("Panel/CreateRoomButton");
            _backBtn = GetNodeOrNull<Button>("Panel/BackButton");
            _statusLabel = GetNodeOrNull<Label>("Panel/StatusLabel");
            _roomListBox = GetNodeOrNull<VBoxContainer>("Panel/Scroll/RoomList");

            // 回填：优先用命令行 `--lobby` 指定的值（OnlineClientContext 已置位），否则用本地偏好
            Script.IO.OnlineClientPrefs.EnsureLoaded();
            bool fromCli = OnlineClientContext.AutoConnect;
            if (_hostInput != null) _hostInput.Text = fromCli ? OnlineClientContext.LastHost : Script.IO.OnlineClientPrefs.LastHost;
            if (_portInput != null) _portInput.Text = (fromCli ? OnlineClientContext.LastPort : Script.IO.OnlineClientPrefs.LastPort).ToString();
            if (_nameInput != null) _nameInput.Text = fromCli ? OnlineClientContext.LastName : Script.IO.OnlineClientPrefs.LastName;

            if (_connectBtn != null) _connectBtn.Pressed += OnConnectPressed;
            if (_refreshBtn != null) _refreshBtn.Pressed += () => RequestRoomList();
            if (_createBtn != null) _createBtn.Pressed += OnCreateRoomPressed;
            if (_backBtn != null) _backBtn.Pressed += OnBackPressed;

            SetRoomActionsEnabled(false);
            SetStatus(OnlineClientContext.HasSession ? "已连接（可刷新房间列表）" : "未连接：请填写服务端地址后点「连接」");
            if (OnlineClientContext.HasSession)
            {
                AttachSession(OnlineClientContext.Take());
                // 【H4】Hello 是**会话级**一次性动作，而本页是新实例：若会话早已登录成功（最常见的"打完一局回大厅"），
                // 必须按会话上的事实恢复按钮并拉列表；否则刷新/建房会永久灰、状态条永远停在"正在登录大厅…"。
                if (_session != null && _session.IsHelloAcked)
                {
                    _helloAcked = true;
                    SetRoomActionsEnabled(true);
                    SetStatus($"已登录大厅（会话 {_session.MySessionId}），正在获取房间列表…");
                    RequestRoomList();
                }
            }
            else if (OnlineClientContext.AutoConnect)
            {
                GD.Print("[LobbyOl] --lobby 自动连接模式：使用回填地址直接连接");
                OnConnectPressed();
            }
        }

        /// <summary>冒烟用：进入大厅后自动建房/加入（与按钮走同一处理函数）。</summary>
        private double _smokeTimer;
        private bool _smokeJoined;

        // ==================== 连接 ====================

        private void OnConnectPressed()
        {
            if (_session != null)   // 已连接：断开后重连（允许改地址）
            {
                DetachSession();
                OnlineClientContext.Disconnect();
                SetStatus("已断开，正在使用新地址重连…");
            }

            string host = string.IsNullOrWhiteSpace(_hostInput?.Text) ? "127.0.0.1" : _hostInput.Text.Trim();
            if (!ushort.TryParse(_portInput?.Text?.Trim(), out var port)) port = 18950;
            string name = string.IsNullOrWhiteSpace(_nameInput?.Text) ? "玩家" : _nameInput.Text.Trim();

            OnlineClientContext.LastHost = host;
            OnlineClientContext.LastPort = port;
            OnlineClientContext.LastName = name;
            Script.IO.OnlineClientPrefs.SaveConnection(host, port, name);   // 持久化：下次自动回填

            _session = ClientSession.Connect(host, port, name);
            OnlineClientContext.SetSession(_session);
            AttachSession(_session);
            SetStatus($"正在连接 {host}:{port} …");
        }

        private void AttachSession(ClientSession session)
        {
            if (session == null) return;
            _session = session;
            _session.OnConnected += OnConnected;
            _session.OnDisconnected += OnDisconnected;
            _session.OnNotice += OnNotice;
            _session.OnLobbyResponse += OnLobbyResponse;
            _session.OnRoomState += OnRoomState;
            GD.Print("[LobbyOl] 已绑定会话事件");
        }

        private void DetachSession()
        {
            if (_session == null) return;
            _session.OnConnected -= OnConnected;
            _session.OnDisconnected -= OnDisconnected;
            _session.OnNotice -= OnNotice;
            _session.OnLobbyResponse -= OnLobbyResponse;
            _session.OnRoomState -= OnRoomState;
            _session = null;
        }

        /// <summary>
        /// 离开大厅页时**必须反订阅**：会话是进程级长生命周期对象，而本页节点会被切场景释放；
        /// 若不反订阅，后续服务端事件会打到已释放节点上（报 ObjectDisposedException）。
        /// </summary>
        public override void _ExitTree()
        {
            DetachSession();
            base._ExitTree();
        }

        public override void _Process(double delta)
        {
            _session?.Poll();

            // 冒烟模式：连接后自动建房/加入（等同用户点按钮）
            if (!OnlineClientContext.SmokeMode || _session == null || _smokeJoined) return;
            _smokeTimer += delta;
            if (_smokeTimer < 1.5) return;
            _smokeTimer = 0;
            if (!_session.IsConnected) return;
            _smokeJoined = true;
            if (OnlineClientContext.SmokeIsHost) { GD.Print("[LobbyOl][冒烟] 自动创建房间"); OnCreateRoomPressed(); }
            else if (TryJoinFirstRoom()) GD.Print("[LobbyOl][冒烟] 自动加入首个房间");
        }

        /// <summary>冒烟/便捷用：加入列表里的第一个可加入房间（与「加入」按钮同一请求）。</summary>
        private bool TryJoinFirstRoom()
        {
            if (_roomListBox == null || _session == null) return false;
            foreach (var child in _roomListBox.GetChildren())
            {
                if (child is not HBoxContainer row) continue;
                foreach (var sub in row.GetChildren())
                {
                    if (sub is not Button btn || btn.Text != "加入" || btn.Disabled) continue;
                    btn.EmitSignal(BaseButton.SignalName.Pressed);
                    return true;
                }
            }
            _smokeJoined = false;   // 还没房间：下轮再试
            return false;
        }

        private void OnNotice(Script.Net.Lobby.ServerNotice notice)
        {
            SetStatus($"服务端通知：{notice?.Text}");
            // 被踢/房间关闭：回大厅
            if (notice != null && notice.Code == Script.Net.Lobby.LobbyErrorCode.Disconnected)
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
        }

        private void OnConnected()
        {
            // 注意：`ClientSession.OnConnected` 在**发送 Hello 之前**触发，此时服务端尚未登记本会话，
            // 立刻发 ListRooms 会被判 NotHelloed。故房间列表等 **Hello 的应答**到达后再拉（见 OnLobbyResponse）。
            SetStatus("已连接服务端，正在登录大厅…");
        }

        /// <summary>【H3】与服务端断开（服务端退出/网络中断）：提示并禁用房间操作，允许点「连接」重连。</summary>
        private void OnDisconnected()
        {
            _helloAcked = false;
            SetRoomActionsEnabled(false);
            SetStatus("与服务端断开连接：请确认服务端仍在运行，然后点「连接」重连。");
        }

        /// <summary>登录大厅（Hello）是否已成功应答（据此才允许列表/建房等请求）。</summary>
        private bool _helloAcked;

        private void RequestRoomList()
        {
            if (_session == null)
            {
                SetStatus("未连接：请先点「连接」");
                return;
            }
            if (!_helloAcked)
            {
                SetStatus("正在登录大厅，稍后自动获取房间列表…");
                return;
            }
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.ListRooms });
        }

        // ==================== 服务端应答 ====================

        private void OnLobbyResponse(LobbyResponse res)
        {
            // Hello 应答：带 SessionId；收到即表示登录完成，此后才发其它请求
            if (res.Ok && !string.IsNullOrEmpty(res.SessionId) && !_helloAcked)
            {
                _helloAcked = true;
                SetRoomActionsEnabled(true);
                SetStatus($"已登录大厅（会话 {res.SessionId}），正在获取房间列表…");
                RequestRoomList();
                return;
            }

            if (!res.Ok)
            {
                SetStatus($"请求失败（{res.ErrorCode}）：{res.ErrorText}");
                return;
            }
            if (res.RoomList != null) { RebuildRoomList(res.RoomList); return; }
            // 建房/加入成功：进入房间页
            if (res.RoomState != null)
            {
                SetStatus($"已进入房间 {res.RoomState.RoomId}");
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.RoomOnline);
            }
        }

        private void OnRoomState(RoomState state)
        {
            // 房间状态到达即代表已在房内（无论是建房还是加入）
            if (state != null) GameManager.Instance?.GoToStateDeferred(GameManager.GameState.RoomOnline);
        }

        private void RebuildRoomList(RoomListSnapshot list)
        {
            if (_roomListBox == null) return;
            foreach (var child in _roomListBox.GetChildren())
            {
                _roomListBox.RemoveChild(child);
                child.QueueFree();
            }
            _joinButtons.Clear();

            if (list.Rooms.Count == 0)
            {
                var empty = new Label { Text = "（暂无可加入的房间，可点「创建房间」）" };
                _roomListBox.AddChild(empty);
                SetStatus($"房间列表已刷新：0 / {list.MaxRooms} 间");
                return;
            }

            foreach (var room in list.Rooms)
            {
                var row = new HBoxContainer();
                var label = new Label
                {
                    Text = $"{room.RoomName}    {room.PlayerCount}/{room.MaxPlayers}    房主：{room.HostDisplayName}    阶段：{room.Phase}",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                row.AddChild(label);

                var join = new Button { Text = "加入", Disabled = room.PlayerCount >= room.MaxPlayers || room.Phase != RoomPhase.Waiting };
                string captured = room.RoomId;
                join.Pressed += () => _session?.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = captured });
                row.AddChild(join);
                _joinButtons.Add(join);

                _roomListBox.AddChild(row);
            }
            SetStatus($"房间列表已刷新：{list.Rooms.Count} / {list.MaxRooms} 间");
        }

        // ==================== 按钮 ====================

        private void OnCreateRoomPressed()
        {
            if (_session == null) { SetStatus("未连接：请先点「连接」"); return; }
            _session.Send(new LobbyRequest
            {
                Kind = LobbyRequestKind.CreateRoom,
                RoomSettings = new RoomSettings { InitialAreaCount = 3 },
            });
            SetStatus("正在创建房间…");
        }

        private void OnBackPressed()
        {
            DetachSession();
            OnlineClientContext.Disconnect();
            GameManager.Instance?.GoToStateDeferred(GameManager.GameState.MainMenu);
        }

        private void SetRoomActionsEnabled(bool enabled)
        {
            if (_refreshBtn != null) _refreshBtn.Disabled = !enabled;
            if (_createBtn != null) _createBtn.Disabled = !enabled;
        }

        private void SetStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.Text = text;
            GD.Print($"[LobbyOl] {text}");
        }
    }
}
