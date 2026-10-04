using Godot;
using Script.IO;
using Script.Net.Godot;
using Script.Net.Lobby;

namespace Script.UI.Online
{
    /// <summary>
    /// 【大厅/房间 UI · 增量 B】**联机房间页**（在线专用；单机 `Room.tscn` 保持原样不动）。
    ///
    /// 显示：房间号、两个座位（`昵称#座位` / 准备状态 / 卡组校验状态）、自己的卡组与校验结果；
    /// 房主额外有「开始对局」「踢出对手」。
    /// 对局结果只在**战场页**展示（结束后服务端自动复用本房间回到等待阶段），本页不再显示结果与「再来一局」。
    /// 规则全部由服务端裁决（本页只发请求 + 显示），对手卡组信息按 D7 根本收不到。
    /// </summary>
    public partial class RoomOl : Control
    {
        private Label _roomLabel, _seat1Label, _seat2Label, _statusLabel, _deckStatusLabel;
        /// <summary>【增量 D】"本机已建立主机"的局域网地址提示（只在房主本机托管服务端时显示）。</summary>
        private Label _hostHintLabel;
        /// <summary>卡组下拉框：列出 `res://Decks/` 下所有卡组文件名；选择后即提交并记忆。</summary>
        private OptionButton _deckOption;
        private Button _deckButton, _readyButton, _startButton, _kickButton, _leaveButton;

        /// <summary>下拉框中「显示名 → 卡组全路径」的映射（下标与 OptionButton 项一一对应）。</summary>
        private readonly System.Collections.Generic.List<string> _deckPaths = new System.Collections.Generic.List<string>();
        /// <summary>当前选中的卡组路径（未选中为空）。</summary>
        private string _selectedDeckPath = "";

        /// <summary>最后一次**成功提交**给服务端的下拉框下标（准备后锁定卡组时用它把 UI 选回来）。-1 = 尚未提交过。</summary>
        private int _committedIndex = -1;
        /// <summary>程序化改动下拉框选中项时抑制 `ItemSelected`（否则"回滚选中项"会被当成玩家又选了一次）。</summary>
        private bool _suppressDeckSignal;

        private ClientSession _session;
        private RoomState _room;

        public override void _Ready()
        {
            _roomLabel = GetNodeOrNull<Label>("Panel/RoomLabel");
            _seat1Label = GetNodeOrNull<Label>("Panel/Seat1Label");
            _seat2Label = GetNodeOrNull<Label>("Panel/Seat2Label");
            _statusLabel = GetNodeOrNull<Label>("Panel/StatusLabel");
            _deckStatusLabel = GetNodeOrNull<Label>("Panel/DeckStatusLabel");
            _hostHintLabel = GetNodeOrNull<Label>("Panel/HostHintLabel");
            _deckOption = GetNodeOrNull<OptionButton>("Panel/DeckOption");
            _deckButton = GetNodeOrNull<Button>("Panel/DeckButton");
            _readyButton = GetNodeOrNull<Button>("Panel/ReadyButton");
            _startButton = GetNodeOrNull<Button>("Panel/StartButton");
            _kickButton = GetNodeOrNull<Button>("Panel/KickButton");
            _leaveButton = GetNodeOrNull<Button>("Panel/LeaveButton");

            if (_deckButton != null) _deckButton.Pressed += () => SubmitDeck(_selectedDeckPath, out _);   // 重新提交（同一卡组不会触发 item_selected，故保留显式按钮）
            if (_readyButton != null) _readyButton.Pressed += ToggleReady;
            if (_startButton != null) _startButton.Pressed += () => _session?.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
            if (_kickButton != null) _kickButton.Pressed += KickOpponent;
            if (_leaveButton != null) _leaveButton.Pressed += LeaveRoom;

            BuildDeckDropdown();

            _session = OnlineClientContext.Take();
            if (_session == null)
            {
                SetStatus("没有活动连接，请先在大厅连接服务端。");
                return;
            }
            _session.OnRoomState += OnRoomState;
            _session.OnLobbyResponse += OnLobbyResponse;
            _session.OnNotice += OnNotice;
            _session.OnDisconnected += OnDisconnected;
            if (_session.CurrentRoom != null) OnRoomState(_session.CurrentRoom);

            // 进房即**自动提交**当前选中的卡组（上次使用 / 目录第一个）：
            // 否则玩家只"看到"下拉框选中项但服务端还不知道（且点同一个选项不会再触发 item_selected）。
            // 若我此刻已是"已准备"（例如从战场页回到房间页），卡组处于锁定态，跳过（服务端也会拒绝）。
            if (IsDeckLocked()) GD.Print("[RoomOl] 我已是「已准备」：卡组锁定，跳过自动提交");
            else SubmitDeck(_selectedDeckPath, out _);
        }

        // ==================== 卡组下拉框 ====================

        /// <summary>
        /// 构建卡组下拉框：列出 `res://Decks/` 下全部卡组**文件名**（不含扩展名），
        /// 并按"上次使用的卡组 → 目录里第一个 → 默认卡组"的顺序自动选中；选中即自动提交（进房即就绪到可准备状态）。
        /// `OptionButton` 天生"点选项即收起下拉框"，符合交互要求。
        /// </summary>
        private void BuildDeckDropdown()
        {
            OnlineClientPrefs.EnsureLoaded();
            var paths = DeckCatalog.ListDeckPaths();
            if (paths.Count == 0) paths.Add(DeckCatalog.DefaultDeckPath);   // 目录不可读时至少保留默认项

            _deckPaths.Clear();
            _deckPaths.AddRange(paths);

            if (_deckOption == null) return;
            _deckOption.Clear();
            foreach (var p in paths) _deckOption.AddItem(DeckCatalog.DisplayName(p));
            _deckOption.ItemSelected += OnDeckItemSelected;

            // 自动选择：偏好里的上次卡组优先；否则目录第一个（通常即 default_deck.json，按名称排序）
            _selectedDeckPath = DeckCatalog.ResolveInitialDeck(paths, OnlineClientPrefs.LastDeckPath);
            int index = paths.FindIndex(p => string.Equals(p, _selectedDeckPath, System.StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = 0;
            _deckOption.Selected = index;
            _selectedDeckPath = paths[index];
            GD.Print($"[RoomOl] 卡组下拉框已就绪：{paths.Count} 个，当前选中「{DeckCatalog.DisplayName(_selectedDeckPath)}」"
                + $"（来源：{(string.IsNullOrEmpty(OnlineClientPrefs.LastDeckPath) ? "目录默认" : "上次使用")}）");
        }

        /// <summary>下拉框选择变化：切换卡组 → 立即提交给服务端校验 → 记忆为"上次使用的卡组"。
        /// 已准备时卡组锁定：不接受切换，并把选中项回滚到已提交的那个。</summary>
        private void OnDeckItemSelected(long index)
        {
            if (_suppressDeckSignal) return;
            if (index < 0 || index >= _deckPaths.Count) return;
            if (IsDeckLocked())
            {
                RestoreCommittedSelection();
                SetStatus("已准备：卡组已锁定，如需更换请先点「取消准备」。");
                return;
            }
            _selectedDeckPath = _deckPaths[(int)index];
            GD.Print($"[RoomOl] 已选择卡组：{_selectedDeckPath}");
            SubmitDeck(_selectedDeckPath, out _);
        }

        // ==================== 卡组锁定（准备后不可更换） ====================

        /// <summary>
        /// 我的座位是否已准备（= 卡组锁定）。
        /// UI 侧第一道闸（下拉框禁用 + 事件回滚）；服务端在 `LobbyServer.HandleSelectDeck` 有对应的权威拒绝，
        /// 因此改客户端也绕不过去。
        /// </summary>
        private bool IsDeckLocked() => _room?.SeatState(_room.ViewerSeat)?.Ready == true;

        /// <summary>按服务端状态同步卡组下拉框的锁定态（准备 → 禁用且不可展开；取消准备 → 恢复可换）。</summary>
        private void ApplyDeckLock(bool locked)
        {
            if (_deckOption != null)
            {
                _deckOption.Disabled = locked;
                _deckOption.TooltipText = locked
                    ? "已准备：卡组已锁定，取消准备后可更换"
                    : "选择要使用的卡组（选中即提交给服务端校验）";
            }
            if (_deckButton != null) _deckButton.Disabled = locked;
            if (locked) RestoreCommittedSelection();
        }

        /// <summary>把下拉框选中项恢复为"最后一次成功提交的卡组"（锁定后回滚尚未生效的改动）。</summary>
        private void RestoreCommittedSelection()
        {
            if (_deckOption == null || _committedIndex < 0 || _committedIndex >= _deckPaths.Count) return;
            if (_deckOption.Selected == _committedIndex) return;
            _suppressDeckSignal = true;
            _deckOption.Selected = _committedIndex;
            _suppressDeckSignal = false;
            _selectedDeckPath = _deckPaths[_committedIndex];
            GD.Print($"[RoomOl] 卡组已锁定：选中项回滚为「{DeckCatalog.DisplayName(_selectedDeckPath)}」");
        }

        /// <summary>提交卡组给服务端（内联 JSON，服务端做基础校验）；persist=true 时记忆为上次使用的卡组。
        /// **已准备时直接拒绝**（与 `LobbyServer` 的权威校验一致），避免"看到失败提示但服务端其实已换卡组"。</summary>
        private bool SubmitDeck(string path, out string error, bool persist = true)
        {
            error = null;
            if (IsDeckLocked())
            {
                error = "已准备：卡组已锁定，请先取消准备再更换卡组。";
                RestoreCommittedSelection();
                SetStatus(error);
                return false;
            }
            if (_session == null || string.IsNullOrEmpty(path)) { error = "未选择卡组"; return false; }
            var deck = DeckListLoader.Load(path);
            if (deck == null)
            {
                error = $"卡组文件无法读取：{path}";
                SetStatus(error);
                return false;
            }
            _session.Send(new LobbyRequest
            {
                Kind = LobbyRequestKind.SelectDeck,
                DeckName = deck.DeckName,
                DeckJson = DeckListLoader.ToJsonText(deck),
            });
            _committedIndex = _deckOption != null ? _deckOption.Selected : _committedIndex;
            if (persist) OnlineClientPrefs.SaveDeck(path);
            SetStatus($"已提交卡组「{DeckCatalog.DisplayName(path)}」（{deck.TotalCardCount} 张），等待服务端校验…");
            return true;
        }

        /// <summary>便捷重载（下拉框/按钮共用；persist 由调用方决定）。</summary>
        private void SubmitDeck() => SubmitDeck(_selectedDeckPath, out _);

        /// <summary>下拉框项下标（供诊断）。</summary>
        public int SelectedDeckIndex => _deckOption?.Selected ?? -1;

        public override void _Process(double delta)
        {
            _session?.Poll();

            // 冒烟模式：进房后自动"提交卡组 → 准备 →（房主）开始"，全部走与按钮相同的处理函数
            if (!OnlineClientContext.SmokeMode || _session == null || _room == null) return;
            _smokeTimer += delta;
            if (_smokeTimer < 1.5) return;
            _smokeTimer = 0;

            var mine = _room.SeatState(_room.ViewerSeat);
            if (mine == null) return;

            // 结束后无需任何操作：服务端会自动复用本房间回到等待阶段（房间页不再有"再来一局"）
            if (!mine.HasDeck) { GD.Print("[RoomOl][冒烟] 自动提交卡组"); SubmitDeck(); return; }
            if (!mine.Ready) { GD.Print("[RoomOl][冒烟] 自动准备"); ToggleReady(); return; }
            if (_room.HostSeat == _room.ViewerSeat && _room.Phase == RoomPhase.Ready)
            {
                GD.Print("[RoomOl][冒烟] 自动开始对局");
                _session.Send(new LobbyRequest { Kind = LobbyRequestKind.StartMatch });
            }
        }
        private double _smokeTimer;

        // ==================== 服务端状态 ====================

        private void OnRoomState(RoomState state)
        {
            if (state == null) return;
            _room = state;

            // 开局：切到战场页（由 OnlineArenaEntry 负责挂 ArenaOl 并 attach 本会话）
            if (state.Phase == RoomPhase.InMatch)
            {
                GD.Print("[RoomOl] 对局开始，切换到战场页");
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.OnlineArena);
                return;
            }
            if (state.Phase == RoomPhase.Closed || state.ViewerSeat == 0)
            {
                SetStatus("房间已关闭，返回大厅");
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
                return;
            }

            int me = state.ViewerSeat;
            var mine = state.SeatState(me);
            var opp = state.SeatState(me == 1 ? 2 : 1);

            if (_roomLabel != null) _roomLabel.Text = $"房间 {state.RoomId}（{state.RoomName}）  阶段：{state.Phase}  我是座位 {me}{(state.HostSeat == me ? "（房主）" : "")}";
            if (_seat1Label != null) _seat1Label.Text = SeatLine(state, 1, state.HostSeat);
            if (_seat2Label != null) _seat2Label.Text = SeatLine(state, 2, state.HostSeat);

            RefreshHostHint();

            if (_readyButton != null)
            {
                _readyButton.Text = mine?.Ready == true ? "取消准备" : "准备";
                _readyButton.Disabled = state.Phase == RoomPhase.Starting;
            }
            // 卡组锁定：已准备 → 下拉框不可打开、不能重新提交；取消准备/新一局回到未准备 → 自动解锁
            ApplyDeckLock(mine?.Ready == true);
            if (_startButton != null) _startButton.Visible = state.HostSeat == me;
            if (_startButton != null) _startButton.Disabled = state.Phase != RoomPhase.Ready;
            if (_kickButton != null) _kickButton.Visible = state.HostSeat == me;
            if (_kickButton != null) _kickButton.Disabled = opp == null || string.IsNullOrEmpty(opp.PlayerName);

            if (_deckStatusLabel != null)
            {
                // D7：只能看到自己的卡组；对手只显示"是否违规"状态
                string deckText = mine == null ? "（无座位）"
                    : mine.HasDeck
                        ? $"我的卡组：{mine.DeckName}（{mine.DeckCardCount} 张）{(mine.DeckValid ? "校验通过" : "校验未通过")}"
                        : "我的卡组：未提交";
                if (!string.IsNullOrEmpty(mine?.DeckError)) deckText += $"　违规原因：{mine.DeckError}";
                if (mine?.Ready == true) deckText += "　🔒 已准备：卡组已锁定（取消准备后可更换）";
                _deckStatusLabel.Text = deckText;
            }
        }

        /// <summary>
        /// 【建立主机·增量 D】刷新"本机地址"提示：只有**本进程托管了内嵌服务端**时才显示，
        /// 并把对手要填的「局域网 IPv4:端口」与防火墙提醒写清楚。
        /// 纯只读展示，不参与任何规则判定（红线：表现层只读）。
        /// </summary>
        private void RefreshHostHint()
        {
            if (_hostHintLabel == null) return;
            if (!LocalHostServer.IsRunning)
            {
                _hostHintLabel.Visible = false;
                return;
            }
            ushort port = LocalHostServer.Port;
            _hostHintLabel.Visible = true;
            _hostHintLabel.Text = $"本机已建立主机：请让对手在「联机对战」里连接 {LocalHostServer.AddressHint(port)}\n"
                + $"（若对手连不上：本机 Windows 防火墙需放行 UDP {port} 入站）";
        }

        private static string SeatLine(RoomState state, int seat, int hostSeat)
        {
            var s = state.SeatState(seat);
            if (s == null || string.IsNullOrEmpty(s.PlayerName))
                return $"座位 {seat}：空位";
            string deck = s.HasDeck ? $"卡组 {s.DeckName}（{s.DeckCardCount}）" : "卡组 —";
            return $"座位 {seat}：{s.DisplayName}{(seat == hostSeat ? "（房主）" : "")}　{(s.Ready ? "已准备" : "未准备")}"
                 + $"　{deck}{(s.DeckValid ? "" : "　⚠卡组违规")}　{(s.Connected ? "" : "（已离线）")}";
        }

        private void OnLobbyResponse(LobbyResponse res)
        {
            if (!res.Ok) { SetStatus($"请求失败（{res.ErrorCode}）：{res.ErrorText}"); return; }
            SetStatus("请求已受理");
        }

        // ==================== 按钮 ====================

        /// <summary>服务端主动通知（被踢、房间关闭等）。</summary>
        private void OnNotice(Script.Net.Lobby.ServerNotice notice)
        {
            SetStatus($"服务端通知：{notice?.Text}");
            if (notice != null && notice.Code == LobbyErrorCode.Disconnected)
                GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
        }

        /// <summary>【H3】与服务端断开：释放会话并回大厅（大厅页会提示"请点连接重连"）。</summary>
        private void OnDisconnected()
        {
            GD.PrintErr("[RoomOl] 与服务端断开连接，返回大厅");
            OnlineClientContext.Disconnect();
            GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
        }

        private void ToggleReady()
        {
            if (_session == null || _room == null) return;
            bool now = _room.SeatState(_room.ViewerSeat)?.Ready == true;
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.SetReady, Ready = !now });
        }

        private void KickOpponent()
        {
            if (_session == null || _room == null) return;
            int opp = _room.ViewerSeat == 1 ? 2 : 1;
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.Kick, TargetSeat = opp });
            SetStatus("已请求踢出对手");
        }

        private void LeaveRoom()
        {
            if (_session == null) return;
            _session.Send(new LobbyRequest { Kind = LobbyRequestKind.LeaveRoom });
            GameManager.Instance?.GoToStateDeferred(GameManager.GameState.LobbyOnline);
        }

        public override void _ExitTree()
        {
            if (_session != null)
            {
                _session.OnRoomState -= OnRoomState;
                _session.OnLobbyResponse -= OnLobbyResponse;
                // M6：`OnNotice` 原先只订阅不反订阅 —— 房间页切走后回调仍指向已释放节点
                // （访问已释放 Label 报错），且其内部的"回大厅"逻辑会把当前页（可能是战场页）强行切走。
                _session.OnNotice -= OnNotice;
                _session.OnDisconnected -= OnDisconnected;
            }
            base._ExitTree();
        }

        private void SetStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.Text = text;
            GD.Print($"[RoomOl] {text}");
        }
    }
}
