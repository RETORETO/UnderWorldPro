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

            // 回填：优先用命令行 `--lobby` 指定的值（OnlineClientContext 已置位），否则用本地偏好。
            // 【建立主机·增量 D】HostAutoFlow 必须在回填**之前**消费：房主流程没有 `--lobby`，靠它判定"用内存里的地址/昵称回填"。
            Script.IO.OnlineClientPrefs.EnsureLoaded();
            bool hostFlow = OnlineClientContext.ConsumeHostAutoFlow();
            _hostFlow = hostFlow;
            bool fromCli = OnlineClientContext.AutoConnect || hostFlow;
            if (_hostInput != null) _hostInput.Text = fromCli ? OnlineClientContext.LastHost : Script.IO.OnlineClientPrefs.LastHost;
            if (_portInput != null) _portInput.Text = (fromCli ? OnlineClientContext.LastPort : Script.IO.OnlineClientPrefs.LastPort).ToString();
            if (_nameInput != null) _nameInput.Text = fromCli ? OnlineClientContext.LastName : Script.IO.OnlineClientPrefs.LastName;

            if (_connectBtn != null) _connectBtn.Pressed += OnConnectPressed;
            if (_refreshBtn != null) _refreshBtn.Pressed += () => RequestRoomList();
            if (_createBtn != null) _createBtn.Pressed += OnCreateRoomPressed;
            if (_backBtn != null) _backBtn.Pressed += OnBackPressed;

            ApplyTheme();   // 【2026-10-20 UI 美化】统一到卡组编辑界面的暗色金主题

            SetRoomActionsEnabled(false);
            if (Script.Net.Godot.GodotWebSocketTransport.IsWeb)
            {
                // 【传输层统一 · 2026-10-14】浏览器与桌面都走 WebSocket，所以服务端不再需要任何特殊开关；
                // 这段提示直接写在状态栏上，免得用户对着"正在连接…"猜原因。
                SetStatus(OnlineClientContext.HasSession
                    ? "已连接（Web 版走 WebSocket）"
                    : "Web 版联机：请填「服务端地址:端口」后点「连接」（服务端需已启动，桌面版点「建立主机」或跑 NetServer 均可）");
            }
            else
            {
                SetStatus(OnlineClientContext.HasSession ? "已连接（可刷新房间列表）" : "未连接：请填写服务端地址后点「连接」");
            }
            if (OnlineClientContext.HasSession)
            {
                AttachSession(OnlineClientContext.Take());
                // 【H4】Hello 是**会话级**一次性动作，而本页是新实例：若会话早已登录成功（最常见的"打完一局回大厅"），
                // 必须按会话上的事实恢复按钮并拉列表；否则刷新/建房会永久灰、状态条永远停在"正在登录大厅…"。
                if (_session != null && _session.IsHelloAcked)
                {
                    _helloAcked = true;
                    SetRoomActionsEnabled(true);
                    if (TryHostAutoCreateRoom()) return;   // 主机流程：已登录 → 直接建房（一次性）
                    SetStatus($"已登录大厅（会话 {_session.MySessionId}），正在获取房间列表…");
                    RequestRoomList();
                }
            }
            else if (hostFlow)
            {
                // 【建立主机·增量 D】本进程已起内嵌服务端，自动连它（地址/端口/昵称已由上一步回填到输入框）
                GD.Print("[LobbyOl] 建立主机：自动连接本机内嵌服务端");
                OnConnectPressed();
            }
            else if (OnlineClientContext.AutoConnect)
            {
                GD.Print("[LobbyOl] --lobby 自动连接模式：使用回填地址直接连接");
                OnConnectPressed();
            }
        }

        /// <summary>
        /// 【2026-10-20 UI 美化】应用统一主题（见 `Script/UI/UITheme.cs`，真源取自卡组编辑界面）。
        /// 只动表现层：改颜色/样式，不碰任何节点名（`Panel/ConnectButton` 等路径被 `LobbyFlowTests` 断言依赖）。
        /// </summary>
        private void ApplyTheme()
        {
            if (GetNodeOrNull<ColorRect>("Background") is ColorRect bg) bg.Color = UITheme.Background;
            if (GetNodeOrNull<Panel>("Panel") is Panel panel) UITheme.ApplyPanel(panel);

            // 【2026-10-20 背景】场景图压在深色底之上、面板之下（等比铺满裁切，压暗 0.72）。
            var cover = BackdropHelper.AttachCover(this, "res://Cards/Image/桌面3.jpg", 0.72f);
            if (cover != null) MoveChild(cover, 1);

            if (GetNodeOrNull<Label>("Panel/TitleLabel") is Label title) UITheme.ApplyTitle(title, 34);
            foreach (string path in new[]
            {
                "Panel/HostLabel", "Panel/PortLabel", "Panel/NameLabel", "Panel/RoomListTitle",
            })
            {
                if (GetNodeOrNull<Label>(path) is Label lb) UITheme.ApplyBody(lb);
            }
            if (_statusLabel != null) UITheme.ApplyBody(_statusLabel);

            if (_hostInput != null) UITheme.ApplyLineEdit(_hostInput);
            if (_portInput != null) UITheme.ApplyLineEdit(_portInput);
            if (_nameInput != null) UITheme.ApplyLineEdit(_nameInput);

            UITheme.ApplyPrimaryButton(_connectBtn);
            UITheme.ApplyPrimaryButton(_createBtn);
            UITheme.ApplySecondaryButton(_refreshBtn);
            UITheme.ApplySecondaryButton(_backBtn);
        }

        /// <summary>冒烟用：进入大厅后自动建房/加入（与按钮走同一处理函数）。</summary>
        private double _smokeTimer;
        private bool _smokeJoined;

        // ==================== 连接 ====================

        private void OnConnectPressed()
        {
            // 【联机排查埋点 k0~k5】逐句夹住：最后打印出来的那一条，就是崩点所在。
            GD.Print("[LobbyOl] k0 OnConnectPressed 进入");
            if (_session != null)   // 已连接：断开后重连（允许改地址）
            {
                DetachSession();
                OnlineClientContext.Disconnect();
                SetStatus("已断开，正在使用新地址重连…");
            }
            GD.Print("[LobbyOl] k1 旧会话处理完成");

            // 【Web 安全】这一段的每次"值 -> 字符串 / 字符串 -> 数字"都必须绕开全球化：
            //   `string.IsNullOrWhiteSpace` / `Trim()` / `ushort.TryParse` 都可能经 NumberFormatInfo 或
            //   Unicode 类别表落到 ICU 上（Web 上直接 abort）。见 Script/IO/WebSafeText.cs 类头。
            string host = Script.IO.WebSafeText.IsNullOrWhiteSpaceAscii(_hostInput?.Text)
                ? Script.IO.OnlineDefaults.Host : Script.IO.WebSafeText.TrimAscii(_hostInput.Text);
            GD.Print("[LobbyOl] k2 host 解析完成: " + host);

            GD.Print("[LobbyOl] k3 即将解析 port");
            if (!Script.IO.WebSafeText.TryParseUshort(Script.IO.WebSafeText.TrimAscii(_portInput?.Text), out var port))
                port = Script.IO.OnlineDefaults.Port;
            GD.Print("[LobbyOl] k3b port=" + Script.IO.WebSafeText.Int(port));

            string name = Script.IO.WebSafeText.IsNullOrWhiteSpaceAscii(_nameInput?.Text)
                ? "玩家" : Script.IO.WebSafeText.TrimAscii(_nameInput.Text);
            GD.Print("[LobbyOl] k4 name 解析完成");

            OnlineClientContext.LastHost = host;
            OnlineClientContext.LastPort = port;
            OnlineClientContext.LastName = name;
            GD.Print("[LobbyOl] k5 即将 SaveConnection");
            Script.IO.OnlineClientPrefs.SaveConnection(host, port, name);   // 持久化：下次自动回填
            GD.Print("[LobbyOl] k5b SaveConnection 完成");

            // 【联机排查埋点】n1/n2 夹住 Connect：n1 之后若进程没了，说明崩在 Connect 内部（CreateClient 一类的原生调用）。
            GD.Print("[LobbyOl] n1 即将 Connect host=" + host + " port=" + Script.IO.WebSafeText.Int(port) + " name=" + name);
            _session = ClientSession.Connect(host, port, name);
            GD.Print("[LobbyOl] n2 Connect 已返回");
            OnlineClientContext.SetSession(_session);
            AttachSession(_session);

            // 【硬保险】客户端传输在本平台不可用（历史上是"浏览器上请求 ENet 服务端"）。
            // 现在两端都走 WebSocket，正常不会走到这里；保留作兜底，避免状态栏永远停在"正在连接…"。
            if (_session.UnsupportedOnWeb)
            {
                GD.PrintErr("[LobbyOl] 当前平台的传输不可用，已中止本次连接");
                SetStatus("当前平台无法建立该网络连接（浏览器不支持监听端口）。"
                        + "需联机请连接一台已启动的服务端（桌面版「建立主机」或 NetServer）。");
                return;
            }

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
                // 【2026-10-20】房间行改成 PanelContainer（卡片底）包 HBoxContainer，这里两种都认。
                Node rowNode = child is HBoxContainer h ? h : (child as PanelContainer)?.GetChildOrNull<HBoxContainer>(0);
                if (rowNode == null) continue;
                foreach (var sub in rowNode.GetChildren())
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
            // 【建立主机·增量 D】房主与**本进程内嵌服务端**断开时，顺手把服务端关掉：
            // 否则会留下"绑着端口却无人使用"的僵尸服务端，且首页的「建立主机」看起来仍在运行（实际谁也没连上）。
            if (LocalHostServer.IsRunning)
            {
                LocalHostServer.StopIfRunning();
                OnlineClientContext.HostAutoFlow = false;
                SetStatus("本机主机已停止：无法连接自身内嵌服务端（请重新点「建立主机」）。");
                return;
            }
            SetStatus("与服务端断开连接：请确认服务端仍在运行，然后点「连接」重连。");
        }

        /// <summary>
        /// 【建立主机·增量 D】主机自动流程的一次性"建房"动作（Hello 就绪后调用）。
        /// 消费即清零：房主「离开房间」回到本页时不会被再次拖进房间。
        /// </summary>
        private bool TryHostAutoCreateRoom()
        {
            if (!_hostFlow) return false;
            _hostFlow = false;
            SetStatus("主机模式：正在创建房间…");
            OnCreateRoomPressed();
            return true;
        }

        /// <summary>本页是否处于"建立主机"自动流程中（从 `_Ready` 消费 `HostAutoFlow` 得到，一次性）。</summary>
        private bool _hostFlow;

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
                if (TryHostAutoCreateRoom()) return;   // 主机流程：登录完成 → 自动建房（一次性）
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
                UITheme.ApplyBody(empty);
                _roomListBox.AddChild(empty);
                SetStatus($"房间列表已刷新：0 / {list.MaxRooms} 间");
                return;
            }

            foreach (var room in list.Rooms)
            {
                // 【2026-10-20 UI 美化】每间房做成一张「深底 + 灰蓝描边」的卡片行，与卡组编辑界面的行风格一致。
                var card = new PanelContainer();
                UITheme.ApplyPanel(card);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                card.AddChild(row);

                var label = new Label
                {
                    Text = $"{room.RoomName}    {room.PlayerCount}/{room.MaxPlayers}    房主：{room.HostDisplayName}    阶段：{room.Phase}",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.AddThemeColorOverride("font_color", UITheme.Gold);
                row.AddChild(label);

                var join = new Button { Text = "加入", Disabled = room.PlayerCount >= room.MaxPlayers || room.Phase != RoomPhase.Waiting };
                UITheme.ApplySecondaryButton(join);
                string captured = room.RoomId;
                join.Pressed += () => _session?.Send(new LobbyRequest { Kind = LobbyRequestKind.JoinRoom, RoomId = captured });
                row.AddChild(join);
                _joinButtons.Add(join);

                _roomListBox.AddChild(card);
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
