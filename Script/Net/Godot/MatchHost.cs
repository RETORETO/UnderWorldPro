using System;
using System.Collections.Generic;
using Godot;
using Script.Manager;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 批次 2】单场对局宿主（服务端侧）：把"一场对局"所需的全部服务端装配收进一个对象，
    /// 使**一个服务端进程可以同时托管多场对局**而互不干扰（每场一套 `GameInitializer` + 网关 + 座位↔会话绑定）。
    ///
    /// 职责（只做"托管"，不含房间规则）：
    ///  1. 装配：PM 调试命令依赖、`IntentHub`+`GodotIntentRouter`、`NetworkInputGateway`（按"座位→会话"下发决策请求），
    ///     并把网关注入 `TurnManager`/`CombatManager`；
    ///  2. 座位绑定：`BindSeat(seat, sessionId)` / `UnbindSession`（**绑定策略由上层决定**——本类不按连接顺序自己分配，
    ///     房间层（批次 3）按房间座位绑定；当前单场装配器 `NetworkMatchLauncher` 按连接顺序绑定）；
    ///  3. 消息路由：`HandleMessage(sessionId, envelopeType, payload)` 处理 `intent` / `choice_res`（含越权校验）；
    ///  4. 快照下行：`PushSnapshot()` 按座位逐玩家过滤（`SnapshotFilter.ForPlayer`）后定点下发；
    ///  5. 结束信号：订阅 `PlayerManager.OnGameOver` → `IsOver`/`WinnerSeat`（供房间层广播结果，批次 5 使用）。
    ///
    /// 多对局隔离的依赖（批次 2 已完成）：规则层不再依赖进程级单例——
    ///   · `EffectEngine` 取值改走 `CardManagerRef`（`EffectEngine.CM`）；
    ///   · 目标精筛的 PlayerManager 由 `CardManager.PlayerManagerRef` / `TurnManager` 显式传入 `TargetValidity`；
    ///   · 卡组可由 `GameInitializer.P1DeckOverride/P2DeckOverride` 显式指定，服务端不再读 UI 单例。
    ///   （`EffectEngine.Instance`/`CardManager.Instance`/`TurnManager.Instance` 仅作未注入时的兜底，生产路径不依赖。）
    /// </summary>
    public class MatchHost : IRoomMatchHost
    {
        /// <summary>本场对局标识（房间 id；单场装配器可用 "match_1"）。</summary>
        public string MatchId { get; }

        /// <summary>本场对局的服务端权威组合根（**本场独有**的一套 Manager）。</summary>
        public GameInitializer Game { get; }

        /// <summary>服务端自省/测试：每次下发后给出完整快照（未过滤）。</summary>
        public Action<GameSnapshot> OnServerSnapshot;

        private readonly INetTransport _transport;
        private IntentHub _hub;
        private NetworkInputGateway _gateway;

        // 座位(1/2) ↔ 会话（绑定策略由上层决定）
        private readonly Dictionary<int, string> _seatToSession = new Dictionary<int, string>();
        private readonly Dictionary<string, int> _sessionToSeat = new Dictionary<string, int>();

        private bool _serving;
        private Action _onAsyncSettlementCompletedHandler;

        /// <summary>
        /// 某座位判负（房间层在"对局中掉线/离桌"时调用）。
        /// **当前语义（D5）**：直接判该方认输结束对局（`PlayerManager.Concede(loserId)` → `OnGameOver`）。
        /// **Phase D 演进**：本方法内部改为"启动 30s 宽限计时 → 超时判负 / 重连成功取消计时"，调用方与协议不变。
        /// </summary>
        public void ForfeitSeat(int seat, string reason)
        {
            if (seat != 1 && seat != 2) return;
            GD.Print($"[MatchHost] {MatchId} 座位 {seat} 判负（{reason}）");
            Game?.PlayerManager?.Concede(seat);
        }

        /// <summary>本场是否已结束（服务端权威）。</summary>
        public bool IsOver => Game?.PlayerManager?.IsGameOver == true;

        /// <summary>获胜座位（1/2；0=未结束或无胜者）。</summary>
        public int WinnerSeat { get; private set; }

        /// <summary>结束回调（座位号；房间层据此置 Finished 并广播结果）。</summary>
        public Action<int> OnGameOver { get; set; }

        public MatchHost(string matchId, GameInitializer game, INetTransport transport)
        {
            MatchId = string.IsNullOrEmpty(matchId) ? "match" : matchId;
            Game = game ?? throw new ArgumentNullException(nameof(game));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>决策网关（测试/房间层观测用）。</summary>
        public NetworkInputGateway Gateway => _gateway;

        // ==================== 装配 ====================

        /// <summary>
        /// 接管本场对局的网络侧（幂等）。调用前 `Game` 应已完成 `InitializeAll()`（是否 `StartMatch()` 由上层决定）。
        /// </summary>
        public void StartServing()
        {
            if (_serving) return;
            _serving = true;

            var pm = Game.PMCommandManager;
            if (pm != null) ConfigureServerPMCommands(pm);

            var router = new GodotIntentRouter(Game.PlayerActionService, Game.CardManager, pm);
            _hub = new IntentHub(router);
            // 决策请求下行：座位 → 会话（由本场绑定表解析；未绑定的座位直接判空，网关会按取消返回避免死等）
            _gateway = new NetworkInputGateway(_transport, seat => SessionOfSeat(seat));

            Game.TurnManager.InputGateway = _gateway;
            Game.CombatManager.InputGateway = _gateway;

            // 异步决策续算完成后主动广播快照（避免客户端完成选择后 UI 无反馈）
            _onAsyncSettlementCompletedHandler = () => PushSnapshot();
            Game.TurnManager.OnAsyncSettlementCompleted += _onAsyncSettlementCompletedHandler;

            // 结束信号（房间层据此宣布结果；批次 5 会在此广播 RoomState(Finished)）
            if (Game.PlayerManager != null)
                Game.PlayerManager.OnGameOver += OnPlayerManagerGameOver;

            GD.Print($"[MatchHost] {MatchId} 已接管网络侧（座位绑定数 {_seatToSession.Count}）");
        }

        /// <summary>释放本场托管（房间销毁/重开时调用）：退订并解除网关注入，避免事件残留造成跨局串扰。</summary>
        public void StopServing()
        {
            if (!_serving) return;
            _serving = false;

            if (_onAsyncSettlementCompletedHandler != null && Game?.TurnManager != null)
                Game.TurnManager.OnAsyncSettlementCompleted -= _onAsyncSettlementCompletedHandler;
            _onAsyncSettlementCompletedHandler = null;

            if (Game?.PlayerManager != null)
                Game.PlayerManager.OnGameOver -= OnPlayerManagerGameOver;

            if (Game?.TurnManager != null) Game.TurnManager.InputGateway = null;
            if (Game?.CombatManager != null) Game.CombatManager.InputGateway = null;

            _seatToSession.Clear();
            _sessionToSeat.Clear();
            GD.Print($"[MatchHost] {MatchId} 已停止托管");
        }

        private void OnPlayerManagerGameOver(int winnerId)
        {
            WinnerSeat = winnerId;
            GD.Print($"[MatchHost] {MatchId} 对局结束：获胜座位 {winnerId}");
            OnGameOver?.Invoke(winnerId);
        }

        // ==================== 座位绑定 ====================

        /// <summary>把座位绑定到会话（重复绑定同座位会覆盖，旧会话解绑）。</summary>
        public void BindSeat(int seat, string sessionId)
        {
            if (seat != 1 && seat != 2) throw new ArgumentOutOfRangeException(nameof(seat), "座位只能是 1 或 2");
            if (string.IsNullOrEmpty(sessionId)) return;

            if (_seatToSession.TryGetValue(seat, out var old) && old != sessionId) _sessionToSeat.Remove(old);
            _seatToSession[seat] = sessionId;
            _sessionToSeat[sessionId] = seat;
            GD.Print($"[MatchHost] {MatchId} 绑定：会话 {sessionId} → 座位 {seat}");
        }

        /// <summary>按会话解绑（掉线/离开）。</summary>
        public void UnbindSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (!_sessionToSeat.TryGetValue(sessionId, out var seat)) return;
            _sessionToSeat.Remove(sessionId);
            _seatToSession.Remove(seat);
            GD.Print($"[MatchHost] {MatchId} 解绑：会话 {sessionId}（座位 {seat}）");
        }

        /// <summary>座位 → 会话（未绑定返回 null）。</summary>
        public string SessionOfSeat(int seat) => _seatToSession.TryGetValue(seat, out var s) ? s : null;

        /// <summary>会话 → 座位（未绑定返回 0）。</summary>
        public int SeatOfSession(string sessionId)
            => !string.IsNullOrEmpty(sessionId) && _sessionToSeat.TryGetValue(sessionId, out var seat) ? seat : 0;

        /// <summary>座位是否已绑定且连接在线。</summary>
        public bool IsSeatConnected(int seat)
        {
            string sid = SessionOfSeat(seat);
            return !string.IsNullOrEmpty(sid) && _transport.IsSessionConnected(sid);
        }

        // ==================== 消息路由 ====================

        /// <summary>
        /// 处理一条属于本场的对局通道消息。返回 true 表示已处理（上层不应再回"不在对局中"）。
        /// 未绑定座位的会话（越权/旁观）一律拒绝并回执。
        /// </summary>
        public bool HandleMessage(string sessionId, string envelopeType, string payload)
        {
            // 【M3】终局后短路：已结束的对局不再接受任何改动（否则终局结果可能被后续消息覆盖，
            // 或再次触发 OnGameOver 造成"已结束的对局又广播一次结果"）。返回 true = 已受理（静默忽略）。
            if (IsOver)
            {
                GD.Print($"[MatchHost] {MatchId} 已结束，忽略来自会话 {sessionId} 的 {envelopeType} 消息");
                return true;
            }

            // 【M4】决策回包必须由**该请求对应的座位**提交（此前只按可预测的 RequestId 匹配 → 对手可代答）
            int seatOfSender = SeatOfSession(sessionId);
            if (envelopeType == NetEnvelope.Types.ChoiceResult)
            {
                if (seatOfSender == 0)
                {
                    GD.PrintErr($"[MatchHost] {MatchId} 拒绝未绑定会话 {sessionId} 的决策回包");
                    return true;
                }
                return _gateway != null && _gateway.TryHandleResult(payload, seatOfSender);
            }

            if (envelopeType != NetEnvelope.Types.Intent) return false;

            int seat = seatOfSender;
            if (seat == 0)
            {
                // 越权：该会话未绑定本场座位（不应能对本场发意图）
                var denied = IntentSerializer.SerializeResult(new IntentResult
                {
                    IntentId = null, Success = false,
                    Error = $"越权：会话 {sessionId} 未绑定本场（{MatchId}）的任何座位",
                });
                _transport.SendToSession(sessionId, NetEnvelope.Wrap(NetEnvelope.Types.IntentResult, denied));
                GD.PrintErr($"[MatchHost] {MatchId} 拒绝未绑定会话 {sessionId} 的意图");
                return true;
            }

            var resultJson = _hub.HandleIntent(sessionId, seat, payload);
            _transport.SendToSession(sessionId, NetEnvelope.Wrap(NetEnvelope.Types.IntentResult, resultJson));

            // 权威结算后广播最新整局快照（各玩家按可见性过滤）
            PushSnapshot();
            return true;
        }

        /// <summary>把一条握手层已解包的决策回包交给网关（上层也可直接调 `HandleMessage`）。</summary>
        public bool TryHandleChoiceResult(string payload) => _gateway != null && _gateway.TryHandleResult(payload);

        // ==================== 快照下行 ====================

        /// <summary>向本场两个座位逐玩家过滤后下发当前快照；无座位绑定时只回调服务端自省。</summary>
        public void PushSnapshot()
        {
            var snap = Game?.BuildSnapshot();
            if (snap == null)
            {
                GD.Print("[MatchHost] BuildSnapshot 返回 null（未下行）");
                return;
            }
            PushSnapshot(snap);
        }

        /// <summary>用给定快照下发（避免重复 Build；房间/测试可复用）。</summary>
        public void PushSnapshot(GameSnapshot snap)
        {
            if (snap == null) return;

            int handCount = 0;
            foreach (var z in snap.Zones) if (z.Zone == Script.Enums.CardZone.Hand) handCount++;
            GD.Print($"[MatchHost] {MatchId} 下行快照 总卡数={snap.Zones.Count} 手牌数={handCount}（按玩家过滤）");

            for (int seat = 1; seat <= 2; seat++)
            {
                string sid = SessionOfSeat(seat);
                if (string.IsNullOrEmpty(sid) || !_transport.IsSessionConnected(sid)) continue;
                var view = SnapshotFilter.ForPlayer(snap, seat);
                _transport.SendToSession(sid, SnapshotSerializer.SnapPrefix + SnapshotSerializer.Serialize(view));
            }

            OnServerSnapshot?.Invoke(snap);
        }

        /// <summary>只向某座位下发（该座位掉线/未绑定时静默跳过）。</summary>
        public void PushSnapshotToSeat(int seat)
        {
            string sid = SessionOfSeat(seat);
            if (string.IsNullOrEmpty(sid) || !_transport.IsSessionConnected(sid)) return;
            var snap = Game?.BuildSnapshot();
            if (snap == null) return;
            var view = SnapshotFilter.ForPlayer(snap, seat);
            _transport.SendToSession(sid, SnapshotSerializer.SnapPrefix + SnapshotSerializer.Serialize(view));
            OnServerSnapshot?.Invoke(snap);
        }

        // ==================== PM 调试命令（服务端无 Arena 场景时用） ====================

        private void ConfigureServerPMCommands(PMCommandManager pm)
        {
            pm.Initialize(
                Game.CardManager,
                Game.DeckManager,
                () => Game.AreaManager?.GetOrderedAreaUids() ?? new List<string>(),
                (pid, count) => Game.PlayerActionService?.DrawCards(pid, count),
                Game.ZoneManager,
                onZoneChanged: null,
                refreshCardUI: null,
                getFaceUp: null,
                setFaceUp: null,
                openCombatWindow: null,
                areaManager: Game.AreaManager,
                drawAndAddNewArea: () => DrawAndAddAreaForDebug(),
                stackManager: Game.StackManager);
        }

        /// <summary>PM 命令 addarea 的纯逻辑实现：从世界牌库抽 1 张地区卡注册为新区（不建 UI），作用于**本场**。</summary>
        private bool DrawAndAddAreaForDebug()
        {
            var svc = Game?.PlayerActionService;
            if (svc == null || Game.AreaManager == null) return false;
            var initial = svc.DrawInitialAreas(1);
            if (initial == null || initial.Count == 0) return false;
            var area = initial[0];
            int index = Game.AreaManager.GetOrderedAreaUids().Count + 1;
            svc.RegisterArea(area.UID, index, area);
            return true;
        }
    }
}
