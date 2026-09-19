using System;
using Godot;
using Script.Manager;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【C/S 化·联网对局启动器】把已落地的正式联机组件串成一条"服务端权威 → 客户端投影"闭环。
    ///
    /// 组件复用（均为正式组件，非临时）：
    ///   - 传输层：GodotEnetTransport（ENet 真实链路，127.0.0.1 回环，可跨进程）；
    ///   - 意图路由：IntentHub + GodotIntentRouter（意图 → 真实 PlayerActionService / CardManager 规则）；
    ///   - 异步输入：NetworkInputGateway（服务端 await 客户端 ChoiceResult）；
    ///   - 快照：GameInitializer.BuildSnapshot() + SnapshotSerializer 下行 + ZoneManager.ApplySnapshot 客户端重建。
    ///
    /// 结构（单 Godot 进程内 Server + Client 回环，MTGA 类"房主即服务端"）：
    ///   - 服务端：持有 GameInitializer 的真实 Manager（ZoneManager/CardManager/...），权威执行规则；
    ///   - 客户端：持有【独立的投影 ZoneManager】+【与服务端共享的 CardManager 实例】。
    ///     共享 CardManager 是因为同进程内卡实例本就由服务端建好，ResolveInstance 可直接命中（避免重复建实例/UID 漂移）；
    ///     投影 ZoneManager 独立，则客户端 ApplySnapshot 只重建自己的区域桶，不污染服务端权威状态。
    ///     跨进程场景（未来）时，客户端 CardManager 也独立，届时需补"从模板建实例"路径（见 CardManager.RebuildFromRef 注释），
    ///     本启动器通过注入独立 CardManager 即可适配，无需改动主干。
    ///
    /// 消息协议（同一 transport 通道复用，用前缀区分）：
    ///   - 客户端上行：PlayerIntent JSON（IntentSerializer 已含 IncludeFields）；
    ///   - 服务端下行：IntentResult JSON（意图回执）/ "SNAP:" + SnapshotSerializer JSON（整局快照投影）。
    ///
    /// 【批次 1 会话化】传输层只认**会话 id**（服务端分配 `s1`/`s2`…）。本类作为"单场装配器"按连接顺序把会话绑到
    /// 座位 1/2（`_seatToSession`），对局层因此仍可按"座位（=playerId）"表达语义；房间模型下这层绑定由 `RoomSession` 接管
    /// （大厅/房间批次 3/5），届时本类退化为薄适配或由 `MatchHost` 取代（拆分不在本批做）。
    ///
    /// 驱动：本类继承 Node，由场景挂入后 _Process 每帧 Poll 两端 transport（ENet 需主动 Poll）。
    /// 这是正式联机入口（非验证桩）：服务端与客户端两侧都经本类装配，规则只在服务端执行。
    /// </summary>
    public class NetworkMatchLauncher : Node, IMatchClientView
    {
        private const string SNAP_PREFIX = Script.Net.SnapshotSerializer.SnapPrefix;

        // —— 服务端侧（权威） ——
        private INetTransport _serverTransport;
        private MatchHost _matchHost;        // 单场对局宿主（批次 2：服务端装配全部委托给它）
        private GameInitializer _serverGame;

        // —— 客户端侧（投影） ——
        private INetTransport _clientTransport;
        private LoopbackTransport _loopbackClient; // 仅当注入 LoopbackTransport 时非 null
        private IntentClient _client;
        // 【增量 B】客户端投影字段已迁入 `ClientMatchView`（`_matchView`）；下方访问器委托给它。
        // 【增量 B】玩家级费用修正/堆叠快照/待分配调查卡 已迁入 ClientMatchView（经访问器暴露）。

        /// <summary>服务端经此回调对外广播快照（如 Arena 想接 UI 渲染可订阅；最小验证可留空）。</summary>
        public Action<Script.Net.GameSnapshot> OnServerSnapshot;

        /// <summary>客户端收到快照后回调（最小验证可用来断言投影一致性）。</summary>
        public Action<Script.Net.GameSnapshot> OnClientSnapshot;

        /// <summary>客户端收到服务端下发的决策请求（ChoiceRequest）时回调。ArenaOl 订阅后弹出决策面板；
        /// 玩家选择经 SendChoiceResult 上行回包。无订阅方时 OnClientMessage 自动回取消兜底。</summary>
        public event Action<Script.Net.ChoiceRequest> OnClientChoiceRequest;

        /// <summary>客户端收到服务端「拒绝意图」（IntentResult.Success=false）时回调，供 ArenaOl 弹可见提示。</summary>
        public event Action<Script.Net.IntentResult> OnClientIntentRejected;

        // ==================== IMatchClientView（成员名与既有公开 API 一致） ====================
        // 说明：本类的 `OnClientSnapshot/OnClientChoiceRequest/OnClientIntentRejected` 与 `ClientXxx` 访问器
        // 早已是 ArenaOl/测试使用的公开 API，接口刻意采用同一命名 → 本类几乎无需适配层。
        // 唯一例外：`OnClientSnapshot` 是**委托字段**（调用方可直接赋值），而接口要求 event，
        // 故对其做显式接口实现（转发到同一字段），既有赋值写法保持不变。

        event Action<Script.Net.GameSnapshot> IMatchClientView.OnClientSnapshot
        {
            add => OnClientSnapshot += value;
            remove => OnClientSnapshot -= value;
        }

        // ==================== 座位 ↔ 会话 绑定（批次 1 适配层） ====================
        // 传输层只认会话（sessionId）；本类作为"单场装配器"，按连接顺序把会话绑到座位 1/2。
        // 【批次 2】座位表已下沉到 `MatchHost`（本类只决定"哪个新会话拿哪个座位"，绑定动作委托给 MatchHost）；
        // 房间模型下这层绑定策略由房间（RoomSession）决定，见《联机大厅与房间设计方案》§五。
        private int _nextSeat = 1;
        /// <summary>本进程（客户端角色）的座位号。</summary>
        private int _localSeat = 1;

        /// <summary>座位 → 会话（服务端角色；委托 `MatchHost`，未装配时返回 null）。</summary>
        private string SessionOfSeat(int seat) => _matchHost?.SessionOfSeat(seat);

        /// <summary>
        /// 启动本地回环联机对局。
        /// serverGame：已由 Arena 装配好的 GameInitializer（持有真实 Manager，并已 StartMatch 起手）。
        /// port：ENet 监听端口（客户端连 127.0.0.1）。仅在使用默认 GodotEnetTransport 时生效。
        ///
        /// 设计：传输层可注入（INetTransport 契约）。默认用 GodotEnetTransport（真实 ENet 联机）；
        /// 单进程端到端测试可注入 LoopbackTransport（内存回环，无需真实握手）以稳定验证投影闭环。
        ///
        /// clientCardManager：客户端投影的 CardManager。默认 null = 复用服务端 CardManager（单进程回环，
        /// 共享实例，ResolveInstance 直接命中）；传入独立实例（如跨进程场景的 ClientCardManager 子类）
        /// 则模拟"客户端不共享服务端实例、仅靠快照从模板重建"，验证跨进程重建路径。
        /// </summary>
        public void StartLocalLoopback(GameInitializer serverGame, ushort port = 18920,
            INetTransport serverTransport = null, INetTransport clientTransport = null,
            CardManager clientCardManager = null)
        {
            if (serverGame == null) throw new ArgumentNullException(nameof(serverGame));
            _serverGame = serverGame;

            // 服务端权威 + 客户端投影（单进程回环；clientCardManager 传入独立实例则模拟跨进程）
            StartServerOnly(serverGame, port, serverTransport);
            StartClientOnly(clientCardManager ?? serverGame.CardManager, port, clientTransport);

            GD.Print($"[NetworkMatchLauncher] 本地回环联机已启动（服务端权威 + 客户端投影）");
        }

        /// <summary>
        /// 【真跨进程·服务端进程】仅装配服务端权威侧：GameInitializer 真实规则 + ENet/传输监听 + IntentHub + 快照下行。
        /// 不建任何客户端投影。供独立 Server 进程启动（配合命令行为 --server）。
        ///
        /// 【批次 2】服务端侧装配**全部委托给 `MatchHost`**（单场对局宿主）：本类不再自己持有 hub/网关/座位表，
        /// 只负责"传输 + 按连接顺序绑座"这层单场适配。这样"一个服务端进程多场并发"只需创建多个 `MatchHost`，
        /// 不存在第二份对局装配实现（避免返工）。
        /// </summary>
        public void StartServerOnly(GameInitializer serverGame, ushort port = 18920,
            INetTransport serverTransport = null)
        {
            if (serverGame == null) throw new ArgumentNullException(nameof(serverGame));
            _serverGame = serverGame;

            _serverTransport = serverTransport ?? new GodotEnetTransport(EnetRole.Server) { Port = port, MaxClients = 16 };

            // 单场对局宿主（本类的服务端侧就是"一场"）：装配 hub/网关/PM 命令/结束订阅/快照下行
            _matchHost = new MatchHost("match_1", serverGame, _serverTransport);
            _matchHost.OnServerSnapshot = snap => OnServerSnapshot?.Invoke(snap);
            _matchHost.OnGameOver = winner => GD.Print($"[NetworkMatchLauncher][SVR] 对局结束，获胜座位 {winner}");
            _matchHost.StartServing();

            _serverTransport.OnSessionMessage(OnServerMessage);
            _serverTransport.OnSessionConnected(OnServerSessionConnected);
            _serverTransport.OnSessionDisconnected(OnServerSessionDisconnected);
            _serverTransport.Start();
            GD.Print($"[NetworkMatchLauncher] 服务端进程已启动（权威规则 + 端口 {port}，MatchHost 已接管；会话寻址）");
        }

        /// <summary>服务端：新会话接入 → 按连接顺序绑定座位（1/2）。超出 2 个的连接不绑定座位（无法参与对局）。</summary>
        private void OnServerSessionConnected(string sessionId)
        {
            if (_nextSeat > 2) return;                 // 单场装配器最多两名玩家（大厅模型由房间层管理，见设计方案）
            _matchHost?.BindSeat(_nextSeat++, sessionId);
        }

        /// <summary>服务端：会话断开 → 释放座位绑定（委托 `MatchHost`）。</summary>
        private void OnServerSessionDisconnected(string sessionId)
        {
            if (_matchHost?.SeatOfSession(sessionId) is not int seat || seat == 0) return;
            _matchHost.UnbindSession(sessionId);
            GD.Print($"[NetworkMatchLauncher][SVR] 会话 {sessionId}（座位 {seat}）断开，座位已释放");
            // 座位号不回退：避免"新连接的座位号与既存玩家的 UI/日志身份混淆"（房间模型下由房间重排座位）
        }

        /// <summary>
        /// 集中装配客户端投影 Manager（Zone/Deck/Player/Stack/Turn/World/Area/Card）。
        /// 客户端仅做快照投影，不驱动规则；投影装配知识集中于此，避免散落在 StartClientOnly 中。
        /// </summary>
        private void AssembleClientProjection(CardManager clientCard)
        {
            // 【增量 B】投影装配与消息分发已抽出为 `ClientMatchView`（与大厅路径共用同一份实现），
            // 本类只保留"单场装配器"的接线：把视图事件转发到既有公开事件，并把上行绑定到本类的发送路径。
            _matchView = new ClientMatchView(clientCard, _localSeat);
            _matchView.OnClientSnapshot += snap => OnClientSnapshot?.Invoke(snap);
            _matchView.OnClientChoiceRequest += req => OnClientChoiceRequest?.Invoke(req);
            _matchView.OnClientIntentRejected += res => OnClientIntentRejected?.Invoke(res);
            _matchView.BindSender(
                (kind, args) => SendIntentAs(_localSeat, kind, args),
                result => SendChoiceResult(result));
        }

        /// <summary>客户端对局视图（投影 + 分发 + 上行）；未启动客户端时为 null。</summary>
        private ClientMatchView _matchView;

        /// <summary>
        /// 【真跨进程·客户端进程】仅装配客户端投影侧：独立 CardManager（跨进程由子类从模板重建）+ 各投影 Manager + ENet 连接 + 消费快照。
        /// 不建任何服务端规则。供独立 Client 进程启动（配合命令行为 --client）。
        /// serverAddress：服务端地址（默认 127.0.0.1 本机；局域网对战填服务端电脑的 IP，见 NetMatchEntry 的 --host）。
        /// </summary>
        public void StartClientOnly(CardManager clientCard, ushort port = 18920,
            INetTransport clientTransport = null, int playerId = 1, string serverAddress = "127.0.0.1")
        {
            AssembleClientProjection(clientCard);
            _localSeat = playerId;

            _clientTransport = clientTransport ?? new GodotEnetTransport(EnetRole.Client)
            {
                Port = port,
                ServerAddress = string.IsNullOrEmpty(serverAddress) ? "127.0.0.1" : serverAddress,
            };
            _client = new IntentClient(_clientTransport, playerId);

            // 传输无关注册：LoopbackTransport 用会话注册收服务端下行，其余用 OnSessionMessage。
            // 回环场景下客户端会话名 = 服务端为该座位绑定的会话名（同进程共享映射）；未绑定时按 "p<seat>" 约定。
            _loopbackClient = _clientTransport as LoopbackTransport;
            if (_loopbackClient != null)
            {
                string sid = SessionOfSeat(playerId) ?? ("p" + playerId);
                _loopbackClient.RegisterSession(sid, m => OnClientMessage(playerId, m));
            }
            else
            {
                _clientTransport.OnSessionMessage((_, m) => OnClientMessage(playerId, m));
            }
            _clientTransport.Start();
            string addrLog = (_clientTransport as GodotEnetTransport)?.ServerAddress ?? "内存回环(Loopback)";
            GD.Print($"[NetworkMatchLauncher] 客户端进程已启动（座位{playerId} + 独立投影 + 服务端 {addrLog} 端口 {port}）");
        }

        /// <summary>客户端上行一条意图（如 Pass / DrawCards / PlayCard）。返回的 IntentId 可用于匹配下行结果。</summary>
        public string SendIntent(PlayerIntent.Kind kind, params string[] args)
            => SendIntentAs(_localSeat, kind, args);

        /// <summary>
        /// 以指定座位上行一条意图（回环/演示：单进程内驱动座位1/座位2 让过推进回合）。
        /// 真实跨进程时每个客户端进程固定自己的座位（`StartClientOnly` 的 playerId）。
        /// 寻址：回环用 `ClientReply(会话, …)`，真实客户端用 `SendToServer`（无需寻址）。
        /// </summary>
        public string SendIntentAs(int playerId, PlayerIntent.Kind kind, params string[] args)
        {
            if (_loopbackClient != null)
            {
                var intent = PlayerIntent.Create(playerId, kind, args);
                string sid = SessionOfSeat(playerId) ?? ("p" + playerId);
                _loopbackClient.ClientReply(sid,
                    NetEnvelope.Wrap(NetEnvelope.Types.Intent, Script.Net.IntentSerializer.SerializeIntent(intent)));
                return intent.IntentId;
            }
            return _client?.SendIntentAs(playerId, kind, args);
        }

        /// <summary>客户端上行一条决策回包（ChoiceResult，Phase B：带类型信封 v2），对应服务端挂起的请求。</summary>
        public void SendChoiceResult(Script.Net.ChoiceResult result)
        {
            if (result == null || _clientTransport == null) return;
            var payload = Script.Net.NetEnvelope.Wrap(
                Script.Net.NetEnvelope.Types.ChoiceResult,
                Script.Net.ChoiceSerializer.SerializeResult(result));
            // 回环：需显式给出"来源会话"；真实客户端：SendToServer（唯一对端）。
            if (_loopbackClient != null)
            {
                string sid = SessionOfSeat(_localSeat) ?? ("p" + _localSeat);
                _loopbackClient.ClientReply(sid, payload);
                return;
            }
            _clientTransport.SendToServer(payload);
        }

        // —— 服务端收包：意图/决策回包 → 交本场 MatchHost 路由 ——
        private void OnServerMessage(string sessionId, string message)
        {
            // Phase B·协议加固：优先按显式类型信封分发；非信封（旧客户端）回退到形状识别。
            string envType = Script.Net.NetEnvelope.UnwrapType(message);
            string payload = Script.Net.NetEnvelope.UnwrapPayload(message);

            // 1) 决策回包：交给网关匹配挂起请求（信封/裸 JSON 均可）
            if (envType == null || envType == Script.Net.NetEnvelope.Types.ChoiceResult)
            {
                if (_matchHost != null && _matchHost.TryHandleChoiceResult(payload)) return;
                if (envType == Script.Net.NetEnvelope.Types.ChoiceResult)
                {
                    GD.PrintErr("[NetworkMatchLauncher][SVR] 收到未匹配到挂起请求的 ChoiceResult，已忽略");
                    return;
                }
            }

            // 2) 对局通道（意图等）：全部交 MatchHost（含"未绑定会话→越权拒绝"与结算后快照广播）
            if (_matchHost != null && _matchHost.HandleMessage(sessionId, envType ?? Script.Net.NetEnvelope.Types.Intent, payload))
                return;

            GD.PrintErr($"[NetworkMatchLauncher][SVR] 无法路由的消息：会话 {sessionId}，类型 {envType ?? "（无信封）"}");
        }

        /// <summary>
        /// 服务端主动向所有已连客户端广播当前整局快照（投影重建）。
        /// 【Phase B 信息隔离】按玩家分别过滤后下发：每个玩家只看到自己的手牌/牌库内容，
        /// 对手手牌/牌库与地区牌堆只发占位引用（数量与顺序保留）。服务端侧保留完整真相（OnServerSnapshot 仍为完整快照）。
        /// 【批次 2】实际下发由 `MatchHost.PushSnapshot()` 承担（逐座位定点投递）。
        /// </summary>
        public void PushSnapshot()
        {
            if (_matchHost != null) { _matchHost.PushSnapshot(); return; }
            PushSnapshotLegacy();
        }

        /// <summary>未装配 MatchHost 时的兜底路径（旧调用顺序：先 PushSnapshot 后 StartServerOnly）。</summary>
        private void PushSnapshotLegacy()
        {
            var snap = _serverGame?.BuildSnapshot();
            if (snap == null)
            {
                GD.Print($"[NetworkMatchLauncher][SVR] BuildSnapshot 返回 null（未下行）");
                return;
            }

            int handCount = 0;
            foreach (var z in snap.Zones) if (z.Zone == Script.Enums.CardZone.Hand) handCount++;
            GD.Print($"[NetworkMatchLauncher][SVR] 下行快照 总卡数={snap.Zones.Count} 手牌数={handCount}（按玩家过滤）");

            foreach (var pid in new[] { 1, 2 })
            {
                string sid = SessionOfSeat(pid);
                if (string.IsNullOrEmpty(sid) || !_serverTransport.IsSessionConnected(sid)) continue;
                var view = Script.Net.SnapshotFilter.ForPlayer(snap, pid,
                    Script.Manager.LegalityProjection.Build(_serverGame?.CardManager, _serverGame?.PlayerManager,
                        _serverGame?.TurnManager, _serverGame?.ZoneManager, pid));
                var json = SNAP_PREFIX + Script.Net.SnapshotSerializer.Serialize(view);
                _serverTransport.SendToSession(sid, json);
            }

            OnServerSnapshot?.Invoke(snap);   // 服务端自省/测试：完整快照
        }

        // —— 客户端收包：SNAP 投影重建 + ChoiceRequest 响应 + IntentResult 解析 ——
        private void OnClientMessage(int playerId, string message)
        {
            // 【增量 B】分发逻辑统一在 ClientMatchView（快照应用 + 决策请求 + 意图回执）；
            // 本类只订阅其事件并转发到既有公开事件（保持既有测试与 ArenaOl 老路径不变）。
            _matchView?.HandleMessage(message);
        }

        /// <summary>（遗留）单场路径的旧分发实现已迁入 `ClientMatchView.HandleMessage`。</summary>
        private void OnClientMessageLegacy(int playerId, string message)
        {
            if (message.StartsWith(SNAP_PREFIX))
            {
                var snap = Script.Net.SnapshotSerializer.Deserialize(message.Substring(SNAP_PREFIX.Length));
                if (snap != null)
                {
                    ApplyClientSnapshot(snap);
                    OnClientSnapshot?.Invoke(snap);
                }
                return;
            }

            // Phase B·协议加固：优先按显式类型信封分发；非信封（旧服务端）回退形状识别。
            string envType = Script.Net.NetEnvelope.UnwrapType(message);
            string payload = Script.Net.NetEnvelope.UnwrapPayload(message);

            // 服务端经 NetworkInputGateway 下发的决策请求（ChoiceRequest）：客户端必须回复 ChoiceResult，
            // 否则服务端 await 会（按 Kind）超时挂起。已接入决策面板时派发事件由 ArenaOl 弹出；
            // 无订阅方（纯投影客户端）保留自动取消兜底，保证服务端权威结算不阻塞。
            var req = (envType == null || envType == Script.Net.NetEnvelope.Types.ChoiceRequest)
                ? Script.Net.ChoiceSerializer.DeserializeRequest(payload)
                : null;
            if (req != null && !string.IsNullOrEmpty(req.RequestId))
            {
                // 定向校验：仅响应发给本进程玩家的请求（服务端已定向下发，此处二次防呆）
                if (req.PlayerId != (_client?.LocalPlayerId ?? 1))
                {
                    GD.Print($"[NetworkMatchLauncher] 忽略发给玩家{req.PlayerId}的决策请求（本地玩家为{_client?.LocalPlayerId}）");
                    return;
                }
                if (OnClientChoiceRequest != null)
                {
                    OnClientChoiceRequest.Invoke(req);
                    return;
                }
                var result = new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    SelectedUid = null,
                    Pay = false,
                    Cancelled = true,   // 兜底：无决策 UI 订阅，自动取消
                };
                SendChoiceResult(result);
                GD.Print($"[NetworkMatchLauncher] 无决策 UI 订阅，自动响应决策请求 {req.RequestKind}({req.RequestId})：取消");
                return;
            }

            // 否则为 IntentResult：失败时记录日志并通知 UI（服务端拒绝需要可见反馈，避免"点了没反应"）
            var intentResult = Script.Net.IntentClient.ParseResult(payload);
            if (intentResult != null && !intentResult.Success)
            {
                GD.PrintErr($"[NetworkMatchLauncher][SVR] 意图被拒绝：{intentResult.Error}");
                OnClientIntentRejected?.Invoke(intentResult);
            }
        }

        /// <summary>
        /// 客户端投影聚合应用整局快照（委托 `ClientMatchView`）。
        /// 保留本方法名以便既有调用点/测试不变；实现只有一份（增量 B 抽出）。
        /// </summary>
        private void ApplyClientSnapshot(Script.Net.GameSnapshot snap) => _matchView?.ApplySnapshot(snap);

        // ==================== IMatchClientView（委托给 ClientMatchView） ====================
        // 旧单场路径（含全部既有测试）继续用这些访问器；新的大厅路径由 ClientSession 提供同名能力。

        public int LocalSeat => _localSeat;
        
        
        
        
        
        
        
        

        /// <summary>客户端投影的 ZoneManager（供断言/调试读取投影状态）。</summary>
        public ZoneManager ClientZoneManager => _matchView?.ClientZoneManager;
        /// <summary>客户端投影的 CardManager（联机客户端做本地可发动判定 / 规则复用查询时使用）。</summary>
        public CardManager ClientCardManager => _matchView?.ClientCardManager;
        /// <summary>客户端投影的玩家级费用修正（仅用于 UI 减费展示，不参与结算）。</summary>
        public System.Collections.Generic.IReadOnlyList<Script.Net.CostModSnap> ClientCostMods => _matchView?.ClientCostMods;
        /// <summary>客户端最近一次堆叠快照（与 ClientStackManager.Entries 同序；供 UI 取来源模板 UID 等展示字段）。</summary>
        public System.Collections.Generic.IReadOnlyList<Script.Net.StackEntrySnap> ClientStackSnapshot => _matchView?.ClientStackSnapshot;
        /// <summary>【调查对抗】当前待分配的调查卡投影（仅决策者本人非空；供客户端复刻单机调查窗口）。</summary>
        public System.Collections.Generic.IReadOnlyList<Script.Game.Entities.CardInstance> ClientInvestigationCards => _matchView?.ClientInvestigationCards;
        /// <summary>最近一次已应用快照（晚订阅者补帧用）。</summary>
        public GameSnapshot LastSnapshot => _matchView?.LastSnapshot;
        /// <summary>客户端投影的 DeckManager（独立投影牌库；牌库数据在此，不在 ZoneManager.GetZoneCards(Deck) 里）。</summary>
        public DeckManager ClientDeckManager => _matchView?.ClientDeckManager;
        /// <summary>客户端投影的 TurnManager（第二批：阶段/优先权/回合计数投影）。</summary>
        public TurnManager ClientTurnManager => _matchView?.ClientTurnManager;
        /// <summary>客户端投影的 StackManager（第二批：堆叠投影）。</summary>
        public StackManager ClientStackManager => _matchView?.ClientStackManager;
        /// <summary>客户端投影的 PlayerManager（第二批：先手/游戏结束投影）。</summary>
        public PlayerManager ClientPlayerManager => _matchView?.ClientPlayerManager;
        /// <summary>客户端投影的 WorldDeckManager（第三批：地区牌堆投影）。</summary>
        public WorldDeckManager ClientWorldDeckManager => _matchView?.ClientWorldDeckManager;
        /// <summary>客户端投影的 AreaManager（第三批：地区注册表/势力标记投影）。</summary>
        public AreaManager ClientAreaManager => _matchView?.ClientAreaManager;

        /// <summary>每帧驱动 ENet 轮询（由挂载场景的 _Process 调用）。</summary>
        public override void _Process(double delta)
        {
            _serverTransport?.Poll();
            _clientTransport?.Poll();
        }

        /// <summary>服务端视角：指定座位（玩家编号）是否已有会话连入（双进程入口检测客户端连接用）。</summary>
        public bool ServerPlayerConnected(int playerId)
        {
            string sid = SessionOfSeat(playerId);
            return !string.IsNullOrEmpty(sid) && _serverTransport != null && _serverTransport.IsSessionConnected(sid);
        }

        /// <summary>客户端视角：与服务端的链路是否已判定失败（不存在/超时；ENet 轮询已停止）。</summary>
        public bool ClientLinkFailed()
            => _clientTransport is GodotEnetTransport enet && enet.LinkFailed;

        /// <summary>客户端视角：是否已连上服务端（双进程入口检测连接状态用）。</summary>
        public bool ClientConnected()
        {
            if (_clientTransport == null) return false;
            // Loopback 回环视为常连
            if (_loopbackClient != null) return true;
            // 真实 ENet 客户端：以底层连接状态判定（服务端角色才有会话池）
            if (_clientTransport is GodotEnetTransport enet)
                return enet.IsClientConnected();
            return true;
        }

        public void Stop()
        {
            _serverTransport?.Stop();
            _clientTransport?.Stop();
            GD.Print("[NetworkMatchLauncher] 本地回环联机已停止");
        }
    }
}
