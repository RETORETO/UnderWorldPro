using System;
using System.Collections.Generic;
using Godot;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 增量 B】"客户端对局视图"统一接口：战场表现层（`ArenaOl`）与决策窗口只依赖本接口，
    /// 从而**同一套 UI 既能挂旧单场启动器，也能挂大厅会话**（`ClientSession`），避免两条并行代码路径分叉。
    ///
    /// 实现者：
    ///  · `NetworkMatchLauncher`（单场装配器 / 开发 CLI 路径）；
    ///  · `ClientSession`（大厅流程：一条连接贯穿大厅与对局）。
    /// 两者内部都复用同一个 `ClientMatchView`（投影装配 + 快照应用 + 消息分发）。
    /// </summary>
    public interface IMatchClientView
    {
        /// <summary>本客户端的座位号（对局内=playerId）。</summary>
        int LocalSeat { get; }

        ZoneManager ClientZoneManager { get; }
        CardManager ClientCardManager { get; }
        PlayerManager ClientPlayerManager { get; }
        TurnManager ClientTurnManager { get; }
        StackManager ClientStackManager { get; }
        WorldDeckManager ClientWorldDeckManager { get; }
        AreaManager ClientAreaManager { get; }
        DeckManager ClientDeckManager { get; }

        /// <summary>玩家级费用修正投影（仅展示）。</summary>
        IReadOnlyList<CostModSnap> ClientCostMods { get; }
        /// <summary>最近一次堆叠快照（与投影 Entries 同序；UI 取来源模板 UID 等）。</summary>
        IReadOnlyList<StackEntrySnap> ClientStackSnapshot { get; }
        /// <summary>待分配调查卡投影（仅决策者本人非空）。</summary>
        IReadOnlyList<CardInstance> ClientInvestigationCards { get; }

        /// <summary>
        /// 最近一次已应用的整局快照（null=尚未收到）。
        /// **用途：晚订阅者补帧**——大厅流程里初始快照在"房间页"阶段就到达（服务端建局即推送），
        /// 而战场页要到之后才挂载；若只依赖事件订阅，战场会漏掉首帧从而**一张卡都不显示**。
        /// 故战场挂载后应主动用本属性补一次（见 `ArenaOl.AttachMatchClient`）。
        /// </summary>
        GameSnapshot LastSnapshot { get; }

        /// <summary>收到整局快照（投影已应用完毕）。</summary>
        event Action<GameSnapshot> OnClientSnapshot;
        /// <summary>收到服务端决策请求（需 UI 回复 `SendChoiceResult`；无订阅方时自动回取消兜底）。</summary>
        event Action<ChoiceRequest> OnClientChoiceRequest;
        /// <summary>意图被服务端拒绝（用于可见提示）。</summary>
        event Action<IntentResult> OnClientIntentRejected;

        /// <summary>上行一条对局意图（身份＝本客户端座位）。</summary>
        string SendIntentAs(int seat, PlayerIntent.Kind kind, params string[] args);
        /// <summary>上行一条决策回包。</summary>
        void SendChoiceResult(ChoiceResult result);
    }

    /// <summary>
    /// 【大厅/房间 · 增量 B】客户端对局投影（**从 `NetworkMatchLauncher` 抽出的客户端半边**）：
    /// 装配一套"只读投影 Manager"，把服务端快照应用到投影，并在收到决策请求/回执时派发事件。
    ///
    /// 纪律：本类**不驱动规则**（不结算、不判定胜负），只做"快照 → 投影状态"的恢复与消息分发。
    /// 上行（意图/回包）由外部注入的两个委托完成：旧单场路径经回环/ENet 直发，大厅路径经 `ClientSession`。
    /// </summary>
    public class ClientMatchView : IMatchClientView
    {
        public int LocalSeat { get; private set; }

        public ZoneManager ClientZoneManager { get; private set; }
        public CardManager ClientCardManager { get; private set; }
        public PlayerManager ClientPlayerManager { get; private set; }
        public TurnManager ClientTurnManager { get; private set; }
        public StackManager ClientStackManager { get; private set; }
        public WorldDeckManager ClientWorldDeckManager { get; private set; }
        public AreaManager ClientAreaManager { get; private set; }
        public DeckManager ClientDeckManager { get; private set; }

        public IReadOnlyList<CostModSnap> ClientCostMods => _costMods;
        public IReadOnlyList<StackEntrySnap> ClientStackSnapshot => _stackSnaps;
        public IReadOnlyList<CardInstance> ClientInvestigationCards => _investigationCards;

        /// <summary>最近一次已应用的整局快照（晚订阅者补帧用，见接口注释）。</summary>
        public GameSnapshot LastSnapshot { get; private set; }

        public event Action<GameSnapshot> OnClientSnapshot;
        public event Action<ChoiceRequest> OnClientChoiceRequest;
        public event Action<IntentResult> OnClientIntentRejected;

        private List<CostModSnap> _costMods = new List<CostModSnap>();
        private List<StackEntrySnap> _stackSnaps = new List<StackEntrySnap>();
        private List<CardInstance> _investigationCards = new List<CardInstance>();

        private Func<PlayerIntent.Kind, string[], string> _intentSender;
        private Action<ChoiceResult> _choiceSender;

        public ClientMatchView(CardManager clientCard, int localSeat)
        {
            LocalSeat = localSeat;
            Assemble(clientCard);
        }

        /// <summary>座位号在"先建房后开局"的流程中由服务端确认（`RoomState.ViewerSeat`），故允许后置设置。</summary>
        public void SetLocalSeat(int seat) => LocalSeat = seat;

        /// <summary>注入上行通道（意图 / 决策回包）。未注入时意图上行会打印错误并返回 null。</summary>
        public void BindSender(Func<PlayerIntent.Kind, string[], string> intentSender, Action<ChoiceResult> choiceSender)
        {
            _intentSender = intentSender;
            _choiceSender = choiceSender;
        }

        // ==================== 投影装配 ====================

        private void Assemble(CardManager clientCard)
        {
            ClientCardManager = clientCard;

            ClientDeckManager = new DeckManager();
            ClientDeckManager.Initialize(ClientCardManager, new Script.Game.SeededRandomSource(1));
            ClientZoneManager = new ZoneManager();
            ClientZoneManager.Initialize(ClientCardManager, ClientDeckManager);
            ClientCardManager.SetZoneManager(ClientZoneManager);

            // 独立投影 PlayerManager / StackManager / TurnManager（仅状态投影，不驱动规则）
            ClientPlayerManager = new PlayerManager();
            ClientPlayerManager.SetZoneManager(ClientZoneManager);
            ClientStackManager = new StackManager();
            ClientStackManager.SetCardManager(ClientCardManager);
            ClientTurnManager = new TurnManager();
            ClientTurnManager.Initialize(ClientDeckManager, ClientPlayerManager, ClientCardManager);
            ClientTurnManager.SetZoneManager(ClientZoneManager);
            ClientTurnManager.SetStackManager(ClientStackManager);

            // 世界牌库/地区的地区卡（DQ）用【独立 CardManager】重建，与玩家卡彻底隔离，
            // 避免地区卡实例与玩家卡实例在共享 _allInstances 里串扰。
            var worldCard = new NetClientCardManager();
            ClientWorldDeckManager = new WorldDeckManager();
            ClientWorldDeckManager.SetCardManager(worldCard);
            ClientAreaManager = new AreaManager();
            ClientAreaManager.Initialize(ClientWorldDeckManager, ClientZoneManager, ClientStackManager);
            ClientAreaManager.SetCardManager(worldCard);
        }

        // ==================== 消息分发 ====================

        /// <summary>
        /// 处理一条服务端下行报文（原始文本：`SNAP:` 快照 / `V2:` 信封 / 裸 JSON 兼容）。
        /// 旧单场路径与大厅路径都调本方法，保证分发逻辑只有一份。
        /// </summary>
        public void HandleMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (message.StartsWith(SnapshotSerializer.SnapPrefix, StringComparison.Ordinal))
            {
                var snap = SnapshotSerializer.Deserialize(message.Substring(SnapshotSerializer.SnapPrefix.Length));
                if (snap != null) ApplySnapshot(snap);
                return;
            }

            string envType = NetEnvelope.UnwrapType(message);
            string payload = NetEnvelope.UnwrapPayload(message);

            // 决策请求：必须回包，否则服务端按 Kind 超时挂起；有订阅方时交 UI，无订阅方自动取消兜底
            var req = (envType == null || envType == NetEnvelope.Types.ChoiceRequest)
                ? ChoiceSerializer.DeserializeRequest(payload)
                : null;
            if (req != null && !string.IsNullOrEmpty(req.RequestId))
            {
                if (req.PlayerId != LocalSeat)
                {
                    GD.Print($"[ClientMatchView] 忽略发给座位{req.PlayerId}的决策请求（本地座位 {LocalSeat}）");
                    return;
                }
                if (OnClientChoiceRequest != null) { OnClientChoiceRequest.Invoke(req); return; }
                SendChoiceResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = true });
                GD.Print($"[ClientMatchView] 无决策 UI 订阅，自动响应决策请求 {req.RequestKind}({req.RequestId})：取消");
                return;
            }

            // 意图回执：失败时通知 UI（避免"点了没反应"）
            var intentResult = IntentClient.ParseResult(payload);
            if (intentResult != null && !intentResult.Success)
            {
                GD.PrintErr($"[ClientMatchView] 意图被拒绝：{intentResult.Error}");
                OnClientIntentRejected?.Invoke(intentResult);
            }
        }

        /// <summary>应用整局快照到投影（顺序：区域 → 世界牌库 → 玩家 → 堆叠 → 回合 → 地区）。</summary>
        public void ApplySnapshot(GameSnapshot snap)
        {
            if (snap == null) return;
            int before = ClientZoneManager.GetZoneCards(Script.Enums.CardZone.Hand, LocalSeat).Count;
            ClientZoneManager.ApplySnapshot(snap.Zones);
            int after = ClientZoneManager.GetZoneCards(Script.Enums.CardZone.Hand, LocalSeat).Count;
            ClientWorldDeckManager.ApplySnapshotUids(snap.WorldDeckCards);
            ClientPlayerManager.ApplySnapshot(snap.Players);
            ClientStackManager.ApplySnapshot(snap.Stack);
            ClientTurnManager.ApplySnapshot(snap);
            ClientPlayerManager.SetFirstPlayer(ClientTurnManager.FirstPlayerId);
            ClientAreaManager.ApplySnapshot(snap.AreaCards, snap.AreaMarks);

            _costMods = snap.CostMods ?? new List<CostModSnap>();
            _stackSnaps = snap.Stack ?? new List<StackEntrySnap>();

            _investigationCards = new List<CardInstance>();
            if (snap.InvestigationCards != null && ClientCardManager != null)
            {
                foreach (var r in snap.InvestigationCards)
                {
                    var inst = ClientCardManager.RebuildFromRef(r);
                    if (inst != null) _investigationCards.Add(inst);
                }
            }

            GD.Print($"[ClientMatchView] 应用快照 手牌 {before}→{after} 总卡数={snap.Zones.Count} " +
                     $"堆叠={ClientStackManager.Entries.Count} 世界牌堆={ClientWorldDeckManager.Count} 地区={ClientAreaManager.GetOrderedAreaUids().Count} " +
                     $"地区标记={snap.AreaMarks.Count} 阶段={ClientTurnManager.CurrentPhase} 优先权={ClientTurnManager.PriorityPlayerId}");
            LastSnapshot = snap;   // 供晚订阅者（战场页）补帧
            OnClientSnapshot?.Invoke(snap);
        }

        // ==================== 上行 ====================

        public string SendIntentAs(int seat, PlayerIntent.Kind kind, params string[] args)
        {
            if (_intentSender == null)
            {
                GD.PrintErr("[ClientMatchView] 未绑定意图上行通道，SendIntent 被忽略");
                return null;
            }
            return _intentSender(kind, args);
        }

        public void SendChoiceResult(ChoiceResult result) => _choiceSender?.Invoke(result);
    }
}
