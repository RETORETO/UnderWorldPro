using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2】网络输入网关：服务端权威模式下，把"需玩家决策"变为"经网络下发请求→等回包续算"。
    ///
    /// 与 P0 的 LocalInputGateway（本地弹窗）/ AutoInputGateway（测试预设）互补：
    /// - 本网关把 ChoiceRequest 序列化为 JSON，经 INetTransport 下发指定玩家；
    /// - 注册 OnMessageReceived 回调，收到该玩家的 ChoiceResult JSON 后完成对应的 TaskCompletionSource；
    /// - EffectEngine/TurnManager 的 *Async 路径 await 此结果，实现"挂起→等网络→续算"。
    ///
    /// 这是 MTGA 类"服务端 await 客户端响应"状态机的核心闭环。
    /// </summary>
    public class NetworkInputGateway : IInputGateway
    {
        private readonly INetTransport _transport;
        // 挂起的请求：key = 请求唯一 id，value = 完成源。收到回包按 CorrelationId 匹配。
        // 真实网络下 OnClientMessage 可能在传输回调线程触发，故用 lock 保护字典（P3 并发安全）。
        private readonly Dictionary<string, TaskCompletionSource<ChoiceResult>> _pending =
            new Dictionary<string, TaskCompletionSource<ChoiceResult>>();
        private readonly object _lock = new object();
        private int _seq;

        public NetworkInputGateway(INetTransport transport)
        {
            _transport = transport;
            // 注意：不在此处订阅 OnMessageReceived —— 传输层为单委托（见 INetTransport 实现），
            // 若在此订阅会被后续同传输的其它订阅者覆盖。改由传输统一分发方（NetworkMatchLauncher.OnServerMessage）
            // 显式调用 TryHandleResult 转发 ChoiceResult，避免订阅被覆盖导致网关永远等不到回包。
        }

        /// <summary>
        /// 供传输统一分发方（服务端 OnServerMessage）把客户端上行的消息转发给网关：
        /// 若消息是 ChoiceResult（correlation 匹配到挂起请求）则完成对应等待并返回 true；否则返回 false。
        /// 兼容两种输入：信封格式（`V2:choice_res|...`）与旧裸 JSON（未包裹）。
        /// </summary>
        public bool TryHandleResult(string json)
        {
            var payload = NetEnvelope.UnwrapPayload(json);   // 信封则解包，否则原样
            var result = ChoiceSerializer.DeserializeResult(payload);
            if (result == null || string.IsNullOrEmpty(result.RequestId)) return false;
            TaskCompletionSource<ChoiceResult> tcs = null;
            lock (_lock) { if (_pending.TryGetValue(result.RequestId, out tcs)) _pending.Remove(result.RequestId); }
            tcs?.TrySetResult(result);
            return tcs != null;
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
                case ChoiceRequest.Kind.CombatInvestigation: return 60;
                case ChoiceRequest.Kind.CombatStrength: return 60;
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
            lock (_lock) _pending[id] = tcs;

            // 下发请求（Phase B：带类型信封 v2）
            var json = NetEnvelope.Wrap(NetEnvelope.Types.ChoiceRequest, ChoiceSerializer.Serialize(req));
            _transport.Send(playerId, json);

            // 超时保护：按 Kind 配置化（不再一律 30s）
            int seconds = TimeoutProvider?.Invoke(req) ?? TimeoutSecondsFor(req.RequestKind);
            if (seconds <= 0) seconds = DefaultTimeoutSeconds;
            var timer = new System.Timers.Timer(seconds * 1000.0) { AutoReset = false };
            timer.Elapsed += (s, e) =>
            {
                TaskCompletionSource<ChoiceResult> t = null;
                lock (_lock) { if (_pending.TryGetValue(id, out t)) _pending.Remove(id); }
                t?.TrySetResult(new ChoiceResult { RequestId = id, Cancelled = true });
            };
            timer.Start();

            return tcs.Task;
        }
    }
}
