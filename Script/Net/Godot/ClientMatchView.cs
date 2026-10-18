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
    public class ClientMatchView : IMatchClientView, IDisposable
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

        /// <summary>已释放（幂等 + 防释放后继续处理消息）。</summary>
        private bool _disposed;
        /// <summary>是否接管构造参数传入的 `ClientCardManager` 的所有权（决定 Dispose 时是否 Free 它）。</summary>
        private readonly bool _ownsCardManager;
        /// <summary>
        /// 世界牌库/地区卡专用的独立 CardManager。
        /// 【2026-10-17】它原来是 `Assemble` 里的局部变量 `worldCard`，导致这一个 Node 根本无处释放；
        /// 提升为字段后才能在 <see cref="Dispose"/> 里 Free 掉。
        /// </summary>
        private NetClientCardManager _worldCardManager;

        /// <param name="ownsCardManager">
        /// 本视图是否**接管** <paramref name="clientCard"/> 的所有权（Dispose 时是否把它 Free 掉）。
        /// 【必须由调用方显式声明 · 2026-10-17】
        ///   · `ClientSession` 传的是它自己 `new` 的 `NetClientCardManager` → 传 true；
        ///   · `NetworkMatchLauncher` 单场路径传的是 `serverGame.CardManager`（已在场景树内、
        ///     `CardManager.Instance` 正指向它）→ 传 false。
        /// 后者若被 Free，服务端/单机对局会直接崩（访问已释放节点）。
        /// </param>
        public ClientMatchView(CardManager clientCard, int localSeat, bool ownsCardManager = false)
        {
            LocalSeat = localSeat;
            _ownsCardManager = ownsCardManager;
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
            // 【P0·双端同一判定】把本局投影 PlayerManager 注入客户端 CardManager：
            // 目标精筛里的"条件式限制"（如 JC005 的 AttachedToCharacterIfNoMind 需读资产区【心】）
            // 经 `CardManager.PlayerManagerRef` → `TargetValidity.CheckRestrictions` 求值；
            // 未注入时该链路静默退回进程级 `TargetValidity.PlayerManagerRef`（客户端进程恒为 null）→
            // 表现为"有【心】却只能选结附角色的附属"，即客户端本地提前判断与服务端不一致。
            ClientCardManager.PlayerManagerRef = ClientPlayerManager;
            ClientStackManager = new StackManager();
            ClientStackManager.SetCardManager(ClientCardManager);
            ClientTurnManager = new TurnManager();
            ClientTurnManager.Initialize(ClientDeckManager, ClientPlayerManager, ClientCardManager);
            ClientTurnManager.SetZoneManager(ClientZoneManager);
            ClientTurnManager.SetStackManager(ClientStackManager);

            // 世界牌库/地区的地区卡（DQ）用【独立 CardManager】重建，与玩家卡彻底隔离，
            // 避免地区卡实例与玩家卡实例在共享 _allInstances 里串扰。
            // 【2026-10-17】存字段而非局部变量：它是 9 个 Node 之一，必须能被 Dispose 释放。
            _worldCardManager = new NetClientCardManager();
            ClientWorldDeckManager = new WorldDeckManager();
            ClientWorldDeckManager.SetCardManager(_worldCardManager);
            ClientAreaManager = new AreaManager();
            ClientAreaManager.Initialize(ClientWorldDeckManager, ClientZoneManager, ClientStackManager);
            ClientAreaManager.SetCardManager(_worldCardManager);
        }

        // ==================== 释放 ====================

        /// <summary>
        /// 释放本视图创建的投影 Manager。
        ///
        /// 【为什么必须显式释放 · 2026-10-17】`Assemble` 里这 9 个 Manager 都是 `new` 出来的 Node，
        /// 既不 AddChild 到场景树、也没有任何地方 Free 它们 → **每个 `ClientMatchView` 泄漏 9 个 ObjectDB 实例**。
        /// 压测 `--load 8` 时 16 个会话 × 9 = 144，正是退出时
        /// `WARNING: 144 ObjectDB instances were leaked at exit` 的来源。
        ///
        /// 纪律：
        ///  · 未入树的节点用 `Free()`（确定性）。`QueueFree()` 要等帧末，退出路径可能没有下一帧；
        ///  · **只释放自己创建的**：`ClientCardManager` 是构造参数，可能是外部在树内的 `CardManager`
        ///    （`NetworkMatchLauncher` 单场路径），只有 `ownsCardManager == true` 时才释放；
        ///  · 这些 Manager 从未入树 ⇒ `_Ready()` 从未执行 ⇒ 不会写 `CardManager.Instance`/`TurnManager.Instance`，
        ///    因此释放它们**不会**留下悬空静态单例，无需清理 Instance。
        ///  · 调用前应确保界面已解绑（`ArenaOl.DetachLauncher`），否则 UI 缓存的 Manager 引用会变成悬空。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 先摘回调，避免释放过程中还有事件打到残留订阅者。
            OnClientSnapshot = null;
            OnClientChoiceRequest = null;
            OnClientIntentRejected = null;
            _intentSender = null;
            _choiceSender = null;

            // 先释放"依赖方"，再释放被依赖者（都只是 C# 引用，顺序不影响正确性，按直觉来）。
            FreeNode(ClientAreaManager);
            FreeNode(ClientWorldDeckManager);
            FreeNode(_worldCardManager);
            FreeNode(ClientTurnManager);
            FreeNode(ClientStackManager);
            FreeNode(ClientPlayerManager);
            FreeNode(ClientZoneManager);
            FreeNode(ClientDeckManager);
            if (_ownsCardManager) FreeNode(ClientCardManager);

            ClientAreaManager = null;
            ClientWorldDeckManager = null;
            _worldCardManager = null;
            ClientTurnManager = null;
            ClientStackManager = null;
            ClientPlayerManager = null;
            ClientZoneManager = null;
            ClientDeckManager = null;
            ClientCardManager = null;

            _costMods = new List<CostModSnap>();
            _stackSnaps = new List<StackEntrySnap>();
            _investigationCards = new List<CardInstance>();
            LastSnapshot = null;
        }

        /// <summary>未入树节点的确定性释放（`IsInstanceValid` 兜底，避免二次 Free）。</summary>
        private static void FreeNode(Node n)
        {
            if (n != null && GodotObject.IsInstanceValid(n)) n.Free();
        }

        // ==================== 消息分发 ====================

        /// <summary>
        /// 处理一条服务端下行报文（原始文本：`SNAP:` 快照 / `V2:` 信封 / 裸 JSON 兼容）。
        /// 旧单场路径与大厅路径都调本方法，保证分发逻辑只有一份。
        /// </summary>
        public void HandleMessage(string message)
        {
            if (_disposed || string.IsNullOrEmpty(message)) return;

            if (message.StartsWith(SnapshotSerializer.SnapPrefix, StringComparison.Ordinal))
            {
                var snap = SnapshotSerializer.Deserialize(message.Substring(SnapshotSerializer.SnapPrefix.Length));
                // 【大包修复 · 2026-10-14】快照反序列化失败**必须出声**：此前是静默 return，
                // 曾让"快照没到/解析失败"表现为"进战场后一张牌都不显示"且日志毫无线索。
                if (snap == null) GD.PrintErr($"[ClientMatchView] 快照反序列化失败（报文长度 {message.Length}），本帧忽略");
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
            if (_disposed || snap == null) return;
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
            if (_disposed) return null;
            if (_intentSender == null)
            {
                GD.PrintErr("[ClientMatchView] 未绑定意图上行通道，SendIntent 被忽略");
                return null;
            }
            return _intentSender(kind, args);
        }

        public void SendChoiceResult(ChoiceResult result)
        {
            if (_disposed) return;
            _choiceSender?.Invoke(result);
        }
    }
}
