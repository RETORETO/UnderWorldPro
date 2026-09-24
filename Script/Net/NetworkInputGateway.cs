using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2 / 批次 1 会话化】网络输入网关：服务端权威模式下，把"需玩家决策"变为"经网络下发请求→等回包续算"。
    ///
    /// 与 P0 的 LocalInputGateway（本地弹窗）/ AutoInputGateway（测试预设）互补：
    /// - 本网关把 ChoiceRequest 序列化为 JSON，经 INetTransport **按会话**下发（座位由注入的 `seatToSession` 解析）；
    /// - 由传输统一分发方（`ServerHost` / `NetworkMatchLauncher`）把上行的 ChoiceResult 交给 `TryHandleResult`，
    ///   按 `RequestId` 完成对应的 TaskCompletionSource；
    /// - EffectEngine/TurnManager 的 *Async 路径 await 此结果，实现"挂起→等网络→续算"。
    ///
    /// 这是 MTGA 类"服务端 await 客户端响应"状态机的核心闭环。
    /// </summary>
    public class NetworkInputGateway : IInputGateway
    {
        private readonly INetTransport _transport;
        /// <summary>
        /// 座位（=对局内 playerId 1/2）→ 会话 id 的解析器：对局层把 Seat↔Session 绑定后注入。
        /// 默认按**回环约定** `"p" + seat`（`LoopbackTransport` 测试用）；真实传输必须显式传入（见 `NetworkMatchLauncher`）。
        /// </summary>
        private readonly Func<int, string> _seatToSession;
        /// <summary>挂起中的请求：完成源 + 该请求应回答的座位（M4：防止对手代答/取消本方决策）。</summary>
        private sealed class Pending
        {
            public TaskCompletionSource<ChoiceResult> Tcs;
            /// <summary>请求下发给哪个座位（= 对局内 playerId）。</summary>
            public int Seat;
        }

        // 挂起的请求：key = 请求唯一 id，value = 完成源。收到回包按 CorrelationId 匹配。
        // 真实网络下 OnClientMessage 可能在传输回调线程触发，故用 lock 保护字典（P3 并发安全）。
        private readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>();
        private readonly object _lock = new object();
        private int _seq;

        public NetworkInputGateway(INetTransport transport, Func<int, string> seatToSession = null)
        {
            _transport = transport;
            _seatToSession = seatToSession ?? (seat => "p" + seat);
            // 注意：不在此处订阅会话消息 —— 传输层为单委托（见 INetTransport 实现），
            // 若在此订阅会被后续同传输的其它订阅者覆盖。改由传输统一分发方（`ServerHost` 或 `NetworkMatchLauncher`）
            // 显式调用 TryHandleResult 转发 ChoiceResult，避免订阅被覆盖导致网关永远等不到回包。
        }

        /// <summary>
        /// 供传输统一分发方（服务端 OnServerMessage）把客户端上行的消息转发给网关：
        /// 若消息是 ChoiceResult（correlation 匹配到挂起请求）则完成对应等待并返回 true；否则返回 false。
        /// 兼容两种输入：信封格式（`V2:choice_res|...`）与旧裸 JSON（未包裹）。
        ///
        /// 【M4】`seat` = 提交回包的会话所绑定的座位（0/false 表示调用方无法提供或不做校验，保持旧行为）：
        /// 与请求下发目标座位不一致时**不完成等待**（并保留挂起状态等真正的玩家回答），只记一条拒绝日志。
        /// </summary>
        public bool TryHandleResult(string json, int seat = 0)
        {
            var payload = NetEnvelope.UnwrapPayload(json);   // 信封则解包，否则原样
            var result = ChoiceSerializer.DeserializeResult(payload);
            if (result == null || string.IsNullOrEmpty(result.RequestId)) return false;

            Pending pending = null;
            lock (_lock)
            {
                if (_pending.TryGetValue(result.RequestId, out pending))
                {
                    if (seat != 0 && pending.Seat != 0 && pending.Seat != seat)
                    {
                        GDLogErr($"[NetworkInputGateway] 拒绝越权决策回包：请求 {result.RequestId} 属于座位 {pending.Seat}，"
                            + $"却由座位 {seat} 提交（该请求继续等待合法玩家回答）");
                        return true;
                    }
                    _pending.Remove(result.RequestId);
                }
            }
            pending?.Tcs.TrySetResult(result);
            return pending != null;
        }

        /// <summary>
        /// 【Phase B】各类决策请求的默认超时（秒）：需玩家思考/多点选的窗口给更长时间，简单确认类较短。
        /// </summary>
        public static int TimeoutSecondsFor(ChoiceRequest.Kind kind)
        {
            switch (kind)
            {
                case ChoiceRequest.Kind.SelectTriggerOrder: return 60;
                case ChoiceRequest.Kind.SelectTarget: return 60;
                case ChoiceRequest.Kind.SelectHandCard: return 60;   // 【P2】手牌选择（弃牌等）
                case ChoiceRequest.Kind.CombatInvestigation: return 60;
                case ChoiceRequest.Kind.CombatStrength: return 60;
                case ChoiceRequest.Kind.CurbMarks: return 60;   // 【P4】遏制X 的按地区分配
                case ChoiceRequest.Kind.Prediction: return 60;  // 【P4】预测X 的顶/底分配
                case ChoiceRequest.Kind.SelectZoneCard: return 60;   // 【K14】墓地/手牌选卡
                case ChoiceRequest.Kind.MultiTargetSlot: return 60;  // 【K15】多目标逐槽选择
                case ChoiceRequest.Kind.ConfirmTargets: return 30;   // 【K15】多目标确认面板（纯确认，短超时）
                case ChoiceRequest.Kind.SelectEffectMode: return 45;  // 【K16】模式分支"选择一项"
                case ChoiceRequest.Kind.NumberInput: return 60;       // 【K11】数值输入框（支付 N / 选数字）
                case ChoiceRequest.Kind.InspectCards: return 30;     // 【P3·H】检视（私密查看，纯确认）
                case ChoiceRequest.Kind.SelectPlayer: return 45;     // 【K24】玩家目标（选项少，短于选卡）
                case ChoiceRequest.Kind.ForceChoice: return 45;
                case ChoiceRequest.Kind.PayOrTap: return 45;
                case ChoiceRequest.Kind.TieBreak: return 30;
                case ChoiceRequest.Kind.Concede: return 30;
                default: return DefaultTimeoutSeconds;
            }
        }

        /// <summary>默认超时（秒），用于未覆盖或非法返回值的回退。</summary>
        public const int DefaultTimeoutSeconds = 30;

        /// <summary>
        /// 可覆盖的超时提供者（返回秒），用于配置化/测试；返回 &lt;= 0 时回退默认值。
        /// 未设置时使用 <see cref="TimeoutSecondsFor"/> 的按 Kind 映射。
        /// </summary>
        public Func<ChoiceRequest, int> TimeoutProvider { get; set; }

        public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
        {
            var id = $"req_{++_seq}";
            req.RequestId = id;
            req.PlayerId = playerId;

            var tcs = new TaskCompletionSource<ChoiceResult>();
            lock (_lock) _pending[id] = new Pending { Tcs = tcs, Seat = playerId };

            // 下发请求（Phase B：带类型信封 v2；批次 1：按"座位 → 会话"寻址）
            var json = NetEnvelope.Wrap(NetEnvelope.Types.ChoiceRequest, ChoiceSerializer.Serialize(req));
            string sessionId = _seatToSession(playerId);
            if (string.IsNullOrEmpty(sessionId))
            {
                lock (_lock) _pending.Remove(id);
                GDLogErr($"[NetworkInputGateway] 座位 {playerId} 无对应会话（可能已掉线），请求 {id} 直接按取消处理");
                return System.Threading.Tasks.Task.FromResult(new ChoiceResult { RequestId = id, Cancelled = true });
            }
            _transport.SendToSession(sessionId, json);

            // 超时保护：按 Kind 配置化（不再一律 30s）
            int seconds = TimeoutProvider?.Invoke(req) ?? TimeoutSecondsFor(req.RequestKind);
            if (seconds <= 0) seconds = DefaultTimeoutSeconds;
            var timer = new System.Timers.Timer(seconds * 1000.0) { AutoReset = false };
            timer.Elapsed += (s, e) =>
            {
                Pending t = null;
                lock (_lock) { if (_pending.TryGetValue(id, out t)) _pending.Remove(id); }
                t?.Tcs.TrySetResult(new ChoiceResult { RequestId = id, Cancelled = true });
            };
            timer.Start();

            return tcs.Task;
        }

        private static void GDLogErr(string msg) => Console.Error.WriteLine(msg);
    }
}
