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
    /// 驱动：本类继承 Node，由场景挂入后 _Process 每帧 Poll 两端 transport（ENet 需主动 Poll）。
    /// 这是正式联机入口（非验证桩）；ServerCore 纯托管侧另有其启动器，与本类平行。
    /// </summary>
    public class NetworkMatchLauncher : Node
    {
        private const string SNAP_PREFIX = "SNAP:";

        // —— 服务端侧（权威） ——
        private INetTransport _serverTransport;
        private IntentHub _hub;
        private NetworkInputGateway _gateway;
        private GameInitializer _serverGame;

        // —— 客户端侧（投影） ——
        private INetTransport _clientTransport;
        private LoopbackTransport _loopbackClient; // 仅当注入 LoopbackTransport 时非 null
        private IntentClient _client;
        private ZoneManager _clientZone;      // 独立投影区域桶
        private CardManager _clientCard;     // 同进程下与服务端共享实例（注入 GameInitializer 的 CardManager）
        private DeckManager _clientDeck;     // 独立投影牌库（ApplySnapshot 牌库分支用）
        private PlayerManager _clientPlayer; // 独立投影玩家（先手/游戏结束标记）
        private StackManager _clientStack;   // 独立投影堆叠
        private TurnManager _clientTurn;     // 独立投影回合（阶段/优先权/回合计数）
        private WorldDeckManager _clientWorld; // 第三批：独立投影世界牌库（地区牌堆）
        private AreaManager _clientArea;      // 第三批：独立投影地区（注册表 + 势力标记）

        /// <summary>服务端经此回调对外广播快照（如 Arena 想接 UI 渲染可订阅；最小验证可留空）。</summary>
        public Action<Script.Net.GameSnapshot> OnServerSnapshot;

        /// <summary>客户端收到快照后回调（最小验证可用来断言投影一致性）。</summary>
        public Action<Script.Net.GameSnapshot> OnClientSnapshot;

        /// <summary>客户端收到服务端下发的决策请求（ChoiceRequest）时回调。ArenaOl 订阅后弹出决策面板；
        /// 玩家选择经 SendChoiceResult 上行回包。无订阅方时 OnClientMessage 自动回取消兜底。</summary>
        public event Action<Script.Net.ChoiceRequest> OnClientChoiceRequest;

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
        /// </summary>
        public void StartServerOnly(GameInitializer serverGame, ushort port = 18920,
            INetTransport serverTransport = null)
        {
            if (serverGame == null) throw new ArgumentNullException(nameof(serverGame));
            _serverGame = serverGame;

            var pm = serverGame.PMCommandManager;
            if (pm != null)
                ConfigureServerPMCommands(serverGame, pm);
            var router = new GodotIntentRouter(serverGame.PlayerActionService, serverGame.CardManager, pm);
            _hub = new IntentHub(router);

            _serverTransport = serverTransport ?? new GodotEnetTransport(EnetRole.Server) { Port = port };
            // 异步决策网关：服务端 await 客户端 ChoiceResult。传输层为单委托，网关不自行订阅，
            // 由统一分发方 OnServerMessage 显式转发 ChoiceResult（TryHandleResult）。
            _gateway = new NetworkInputGateway(_serverTransport);
            // 注入规则层：注入后，*Async 决策入口（如触发排序窗口）改经网关向目标玩家下发请求并 await 回包；
            // 未注入时完全退化为同步，现有行为不变。
            serverGame.TurnManager.InputGateway = _gateway;
            // 联机对抗（调查/战斗/破平）也走同一决策网关，替代单机的事件窗口。
            serverGame.CombatManager.InputGateway = _gateway;
            // 异步决策续算（触发排序/选目标等）完成后，主动广播最新快照，
            // 避免客户端完成选择后 UI 无反馈（否则要等下一次意图才刷新）。
            serverGame.TurnManager.OnAsyncSettlementCompleted += () => PushSnapshot();
            _serverTransport.OnMessageReceived(OnServerMessage);
            _serverTransport.Start();
            GD.Print($"[NetworkMatchLauncher] 服务端进程已启动（权威规则 + 端口 {port}，InputGateway 已注入）");
        }

        /// <summary>服务端无 Arena 场景时，为 PMCommandManager 注入纯逻辑依赖（UI 回调置空），
        /// 使服务端可经 DebugCommand 意图执行 PM 调试命令验证核心功能。</summary>
        private void ConfigureServerPMCommands(GameInitializer serverGame, PMCommandManager pm)
        {
            pm.Initialize(
                serverGame.CardManager,
                serverGame.DeckManager,
                () => serverGame.AreaManager?.GetOrderedAreaUids() ?? new System.Collections.Generic.List<string>(),
                (pid, count) => serverGame.PlayerActionService?.DrawCards(pid, count),
                serverGame.ZoneManager,
                onZoneChanged: null,
                refreshCardUI: null,
                getFaceUp: null,
                setFaceUp: null,
                openCombatWindow: null,
                areaManager: serverGame.AreaManager,
                drawAndAddNewArea: () => DrawAndAddAreaForDebug(serverGame),
                stackManager: serverGame.StackManager);
        }

        /// <summary>PM 命令 addarea 的纯逻辑实现：从世界牌库抽 1 张地区卡注册为新区（不建 UI）。</summary>
        private static bool DrawAndAddAreaForDebug(GameInitializer serverGame)
        {
            if (serverGame?.PlayerActionService == null || serverGame.AreaManager == null) return false;
            var initial = serverGame.PlayerActionService.DrawInitialAreas(1);
            if (initial == null || initial.Count == 0) return false;
            var area = initial[0];
            int index = serverGame.AreaManager.GetOrderedAreaUids().Count + 1;
            serverGame.PlayerActionService.RegisterArea(area.UID, index, area);
            return true;
        }


        /// <summary>
        /// 集中装配客户端投影 Manager（Zone/Deck/Player/Stack/Turn/World/Area/Card）。
        /// 客户端仅做快照投影，不驱动规则；投影装配知识集中于此，避免散落在 StartClientOnly 中。
        /// </summary>
        private void AssembleClientProjection(CardManager clientCard)
        {
            _clientCard = clientCard;

            _clientDeck = new DeckManager();
            _clientDeck.Initialize(_clientCard, new Script.Game.SeededRandomSource(1));
            _clientZone = new ZoneManager();
            _clientZone.Initialize(_clientCard, _clientDeck);
            _clientCard.SetZoneManager(_clientZone);

            // 独立投影 PlayerManager / StackManager / TurnManager（仅状态投影，不驱动规则）
            _clientPlayer = new PlayerManager();
            _clientPlayer.SetZoneManager(_clientZone);
            _clientStack = new StackManager();
            _clientStack.SetCardManager(_clientCard);
            _clientTurn = new TurnManager();
            _clientTurn.Initialize(_clientDeck, _clientPlayer, _clientCard);
            _clientTurn.SetZoneManager(_clientZone);
            _clientTurn.SetStackManager(_clientStack);

            // 世界牌库/地区的地区卡（DQ）用【独立 CardManager】重建，与玩家卡（_clientCard）彻底隔离，
            // 避免地区卡实例与玩家卡实例在共享 _allInstances 里串扰。
            var worldCard = new NetClientCardManager();
            _clientWorld = new WorldDeckManager();
            _clientWorld.SetCardManager(worldCard);
            _clientArea = new AreaManager();
            _clientArea.Initialize(_clientWorld, _clientZone, _clientStack);
            _clientArea.SetCardManager(worldCard);
        }

        /// <summary>
        /// 【真跨进程·客户端进程】仅装配客户端投影侧：独立 CardManager（跨进程由子类从模板重建）+ 各投影 Manager + ENet 连接 + 消费快照。
        /// 不建任何服务端规则。供独立 Client 进程启动（配合命令行为 --client）。
        /// </summary>
        public void StartClientOnly(CardManager clientCard, ushort port = 18920,
            INetTransport clientTransport = null, int playerId = 1)
        {
            AssembleClientProjection(clientCard);

            _clientTransport = clientTransport ?? new GodotEnetTransport(EnetRole.Client)
            {
                Port = port,
                ServerAddress = "127.0.0.1",
                LocalPlayerId = playerId,
            };
            _client = new IntentClient(_clientTransport, playerId);
            // 传输无关注册：LoopbackTransport 用 RegisterClient 收服务端下行，其余用 OnMessageReceived
            _loopbackClient = _clientTransport as LoopbackTransport;
            if (_loopbackClient != null)
                _loopbackClient.RegisterClient(playerId, m => OnClientMessage(playerId, m));
            else
                _clientTransport.OnMessageReceived(OnClientMessage);
            _clientTransport.Start();
            GD.Print($"[NetworkMatchLauncher] 客户端进程已启动（玩家{playerId} + 独立投影 + 端口 {port}）");
        }

        /// <summary>客户端上行一条意图（如 Pass / DrawCards / PlayCard）。返回的 IntentId 可用于匹配下行结果。</summary>
        public string SendIntent(PlayerIntent.Kind kind, params string[] args)
            => SendIntentAs(1, kind, args);

        /// <summary>
        /// 以指定玩家编号上行一条意图（Loopback 回环：演示模式驱动玩家1/玩家2 让过推进回合）。
        /// 真实跨进程时各客户端进程固定自己的 LocalPlayerId；此处供单进程双轨演示用。
        /// </summary>
        public string SendIntentAs(int playerId, PlayerIntent.Kind kind, params string[] args)
        {
            if (_loopbackClient != null)
            {
                var intent = PlayerIntent.Create(playerId, kind, args);
                _loopbackClient.ClientReply(playerId, Script.Net.IntentSerializer.SerializeIntent(intent));
                return intent.IntentId;
            }
            return _client?.SendIntent(kind, args);
        }

        /// <summary>客户端上行一条决策回包（ChoiceResult），对应服务端 NetworkInputGateway 挂起的请求。</summary>
        public void SendChoiceResult(Script.Net.ChoiceResult result)
        {
            if (result == null || _clientTransport == null) return;
            // Loopback：直接经回环上送服务端分发方；其余传输经 Send 上行。
            if (_loopbackClient != null)
            {
                _loopbackClient.ClientReply(_client?.LocalPlayerId ?? 1, Script.Net.ChoiceSerializer.SerializeResult(result));
                return;
            }
            _clientTransport.Send(_client?.LocalPlayerId ?? 1, Script.Net.ChoiceSerializer.SerializeResult(result));
        }

        // —— 服务端收包：意图 → 路由 → 回执 → 广播快照 ——
        private void OnServerMessage(int playerId, string message)
        {
            // 1) 优先尝试交给决策网关：若是 ChoiceResult（匹配到挂起请求）则完成对应 await 并返回
            if (_gateway != null && _gateway.TryHandleResult(message)) return;

            // 2) 否则按意图处理（快照为服务端下行，不会到此处）
            var resultJson = _hub.HandleIntent(playerId, message);
            _serverTransport.Send(playerId, resultJson);

            // 权威结算后，向所有已连客户端广播最新整局快照（投影重建）
            PushSnapshot();
        }

        /// <summary>
        /// 服务端主动向所有已连客户端广播当前整局快照（投影重建）。
        /// 供客户端连接后推送初始快照，以及双进程验证手动触发。
        /// </summary>
        public void PushSnapshot()
        {
            var snap = _serverGame?.BuildSnapshot();
            if (snap != null)
            {
                int handCount = 0;
                foreach (var z in snap.Zones) if (z.Zone == Script.Enums.CardZone.Hand) handCount++;
                GD.Print($"[NetworkMatchLauncher][SVR] 下行快照 总卡数={snap.Zones.Count} 手牌数={handCount}");
                var snapJson = SNAP_PREFIX + Script.Net.SnapshotSerializer.Serialize(snap);
                BroadcastSnapshot(snapJson);
                OnServerSnapshot?.Invoke(snap);
            }
            else
            {
                GD.Print($"[NetworkMatchLauncher][SVR] BuildSnapshot 返回 null（未下行）");
            }
        }

        // —— 客户端收包：ChoiceRequest 响应 + IntentResult 解析 + SNAP 投影重建 ——
        private void OnClientMessage(int playerId, string message)
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

            // 服务端经 NetworkInputGateway 下发的决策请求（ChoiceRequest）：客户端必须回复 ChoiceResult，
            // 否则服务端 await 会 30s 超时挂起。已接入决策面板时派发事件由 ArenaOl 弹出；
            // 无订阅方（纯投影客户端）保留自动取消兜底，保证服务端权威结算不阻塞。
            var req = Script.Net.ChoiceSerializer.DeserializeRequest(message);
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

            // 否则为 IntentResult（客户端可对结果做断言/日志，此处仅解析不强制处理）
            var _ = Script.Net.IntentClient.ParseResult(message);
        }

        /// <summary>客户端投影聚合应用整局快照：区域 → 世界牌库 → 玩家 → 堆叠 → 回合 → 地区（顺序：先恢复依赖基础）。</summary>
        private void ApplyClientSnapshot(Script.Net.GameSnapshot snap)
        {
            var before = _clientZone.GetZoneCards(Script.Enums.CardZone.Hand, 1).Count;
            // 1) 区域桶（卡的位置真相）
            _clientZone.ApplySnapshot(snap.Zones);
            var after = _clientZone.GetZoneCards(Script.Enums.CardZone.Hand, 1).Count;
            // 2) 世界牌库（地区牌堆：命中共享实例或从模板重建）
            _clientWorld.ApplySnapshotUids(snap.WorldDeckCards);
            // 3) 玩家（游戏结束标记；先手由回合层派生）
            _clientPlayer.ApplySnapshot(snap.Players);
            // 4) 堆叠（按 SourceUid 解析卡片型条目）
            _clientStack.ApplySnapshot(snap.Stack);
            // 5) 回合（阶段/优先权/回合计数；先手标志据此派生）
            _clientTurn.ApplySnapshot(snap);
            _clientPlayer.SetFirstPlayer(_clientTurn.FirstPlayerId);
            // 6) 地区（注册表从快照 AreaCards 重建 + 势力标记从快照恢复）
            _clientArea.ApplySnapshot(snap.AreaCards, snap.AreaMarks);
            GD.Print($"[NetworkMatchLauncher][CLI] 应用快照 手牌 {before}→{after} 总卡数={snap.Zones.Count} " +
                     $"堆叠={_clientStack.Entries.Count} 世界牌堆={_clientWorld.Count} 地区={_clientArea.GetOrderedAreaUids().Count} 地区标记={snap.AreaMarks.Count} " +
                     $"阶段={_clientTurn.CurrentPhase} 优先权={_clientTurn.PriorityPlayerId}");
        }

        /// <summary>向所有已连客户端广播整局快照（投影重建）。兼容 Loopback（视为常连）与 ENet（按 peer 映射）。</summary>
        private void BroadcastSnapshot(string snapJson)
        {
            foreach (var pid in new[] { 1, 2 })
            {
                if (_serverTransport.IsPlayerConnected(pid))
                    _serverTransport.Send(pid, snapJson);
            }
        }

        /// <summary>客户端投影的 ZoneManager（供断言/调试读取投影状态）。</summary>
        public ZoneManager ClientZoneManager => _clientZone;
        /// <summary>客户端投影的 DeckManager（独立投影牌库；牌库数据在此，不在 ZoneManager.GetZoneCards(Deck) 里）。</summary>
        public DeckManager ClientDeckManager => _clientDeck;
        /// <summary>客户端投影的 TurnManager（第二批：阶段/优先权/回合计数投影）。</summary>
        public TurnManager ClientTurnManager => _clientTurn;
        /// <summary>客户端投影的 StackManager（第二批：堆叠投影）。</summary>
        public StackManager ClientStackManager => _clientStack;
        /// <summary>客户端投影的 PlayerManager（第二批：先手/游戏结束投影）。</summary>
        public PlayerManager ClientPlayerManager => _clientPlayer;
        /// <summary>客户端投影的 WorldDeckManager（第三批：地区牌堆投影）。</summary>
        public WorldDeckManager ClientWorldDeckManager => _clientWorld;
        /// <summary>客户端投影的 AreaManager（第三批：地区注册表/势力标记投影）。</summary>
        public AreaManager ClientAreaManager => _clientArea;

        /// <summary>每帧驱动 ENet 轮询（由挂载场景的 _Process 调用）。</summary>
        public override void _Process(double delta)
        {
            _serverTransport?.Poll();
            _clientTransport?.Poll();
        }

        /// <summary>服务端视角：指定玩家编号是否已连入（双进程入口检测客户端连接用）。</summary>
        public bool ServerPlayerConnected(int playerId)
            => _serverTransport != null && _serverTransport.IsPlayerConnected(playerId);

        /// <summary>客户端视角：是否已连上服务端（双进程入口检测连接状态用）。</summary>
        public bool ClientConnected()
        {
            if (_clientTransport == null) return false;
            // Loopback 回环视为常连
            if (_loopbackClient != null) return true;
            // 真实 ENet 客户端：以底层连接状态判定（IsPlayerConnected 在客户端角色下恒 false，不可用）
            if (_clientTransport is GodotEnetTransport enet)
                return enet.IsClientConnected();
            return _clientTransport.IsPlayerConnected(1);
        }

        public void Stop()
        {
            _serverTransport?.Stop();
            _clientTransport?.Stop();
            GD.Print("[NetworkMatchLauncher] 本地回环联机已停止");
        }
    }
}
