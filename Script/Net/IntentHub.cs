using System;
using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块C】意图指令通道 Hub —— 玩家写操作的"网络化"收口网关。
    ///
    /// 收口前置条件 ⑥（意图 100% 收口可序列化）的剩余两项：
    ///   (a) UID 分配：原 CardManager 本地 _nextUID++ 自增，双 Client 各自建卡会撞 UID。
    ///       现"建卡"经意图上行到服务端权威执行，新建实例的 UID 由服务端 _nextUID 分配，
    ///       经 IntentResult.AssignedUids 下发给客户端用于本地投影（见 CardManager.LastCreatedUid）。
    ///   (b) PMCommandManager 直触：原调试器/战斗窗口直接 new PlayerActionService().DealDamage(...)，
    ///       绕过任何序列化。现统一经由 IntentHub 路由（服务端侧 Hub 就是 PlayerActionService 的唯一网络入口），
    ///       PMCommandManager 等本地调用方（单机模式）可直接调 PlayerActionService；联网模式则必须走意图上行。
    ///
    /// 职责边界：
    ///   - 本类【纯托管】，只依赖 Script.Net（DTO/序列化）和 Script.Manager.PlayerActionService / CardManager。
    ///     它不依赖 Godot，可被 Headless 服务端承载。但注意它引用了 Script.Manager（业务服务），
    ///     这符合"Net/ 根可引用业务服务，但业务服务不得反向依赖传输"的单向依赖纪律。
    ///   - 身份校验：服务端 HandleIntent 强制校验 intent.PlayerId 与传输层上报的 playerId 一致，
    ///     防止客户端伪造"代其他玩家操作"。真正的玩家↔连接映射由 INetTransport 实现（如 GodotEnetTransport）保证。
    ///
    /// 与 NetworkInputGateway 配合：
    ///   - NetworkInputGateway 管"服务端→客户端"的 ChoiceRequest 下行 + ChoiceResult 上行（决策阻塞）；
    ///   - IntentHub 管"客户端→服务端"的 PlayerIntent 上行 + IntentResult 下行（主动操作裁决）。
    ///   两者组成 MTGA 类服务端权威双向环。
    /// </summary>
    public class IntentHub
    {
        private readonly IIntentRouter _router;

        /// <summary>服务端模式构造：绑定意图路由（Godot 侧桥接 PlayerActionService/CardManager，或纯托管 Server 规则内核）。</summary>
        public IntentHub(IIntentRouter router)
        {
            _router = router;
        }

        /// <summary>
        /// 服务端：处理一条收到的意图（json）。
        /// playerId 由传输层上报（权威），必须与 intent.PlayerId 一致；否则判越权拒绝。
        /// 返回序列化后的 IntentResult，供传输层下行给对应玩家。
        /// </summary>
        public string HandleIntent(int reportedPlayerId, string json)
        {
            PlayerIntent intent;
            try
            {
                intent = IntentSerializer.DeserializeIntent(json);
            }
            catch (Exception ex)
            {
                return IntentSerializer.SerializeResult(new IntentResult
                {
                    IntentId = null,
                    Success = false,
                    Error = $"意图反序列化失败: {ex.Message}",
                });
            }

            // 身份校验：防止客户端越权代操作其他玩家
            if (intent.PlayerId != reportedPlayerId)
            {
                return IntentSerializer.SerializeResult(new IntentResult
                {
                    IntentId = intent.IntentId,
                    Success = false,
                    Error = $"越权：意图声明 PlayerId={intent.PlayerId} 与连接映射 {reportedPlayerId} 不符",
                });
            }

            var result = new IntentResult { IntentId = intent.IntentId, Success = true };
            try
            {
                Route(intent, result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return IntentSerializer.SerializeResult(result);
        }

        /// <summary>按意图种类路由到本地 PlayerActionService 执行（服务端权威裁决）。</summary>
        private void Route(PlayerIntent intent, IntentResult result)
        {
            var a = intent.Args;
            switch (intent.IntentKind)
            {
                case PlayerIntent.Kind.Pass:
                    _router.Pass(intent.PlayerId);
                    break;

                case PlayerIntent.Kind.PlayCard:
                    // Args: cardUid[, targetUid][, targetCardUid]
                    _router.PlayCard(intent.PlayerId, At(a, 0), At(a, 1), At(a, 2));
                    break;

                case PlayerIntent.Kind.DeployCovert:
                    // Args: cardUid, areaUid —— 暗藏会经 TurnManager 内部建实例，收集新 UID
                    MarkUidWatch();
                    _router.DeployCovert(intent.PlayerId, At(a, 0), At(a, 1));
                    CollectNewUid(result);
                    break;

                case PlayerIntent.Kind.RevealCovert:
                    _router.RevealCovert(intent.PlayerId, At(a, 0));
                    break;

                case PlayerIntent.Kind.EstablishAsset:
                    MarkUidWatch();
                    _router.EstablishAsset(intent.PlayerId, At(a, 0));
                    CollectNewUid(result);
                    break;

                case PlayerIntent.Kind.ReturnCardToDeck:
                    _router.ReturnCardToDeck(intent.PlayerId, At(a, 0),
                        a.Count > 1 && At(a, 1) == "1");
                    break;

                case PlayerIntent.Kind.ReturnCardToHand:
                    _router.ReturnCardToHand(intent.PlayerId, At(a, 0));
                    break;

                case PlayerIntent.Kind.DrawCards:
                    _router.DrawCards(intent.PlayerId, int.Parse(At(a, 0) ?? "1"));
                    break;

                case PlayerIntent.Kind.DealDamage:
                    // Args: targetUid, amount[, isTrauma=1/0]（实例解析由路由实现方完成）
                    _router.DealDamage(At(a, 0), int.Parse(At(a, 1) ?? "0"),
                        a.Count > 2 && At(a, 2) == "1");
                    break;

                case PlayerIntent.Kind.DealAreaDamage:
                    // Args: areaUid, amount[, isTrauma=1/0]
                    _router.DealAreaDamage(At(a, 0), int.Parse(At(a, 1) ?? "0"),
                        a.Count > 2 && At(a, 2) == "1");
                    break;

                case PlayerIntent.Kind.Concede:
                    _router.Concede(intent.PlayerId);
                    break;

                case PlayerIntent.Kind.ActivateAbility:
                    // Args: sourceUid, effectId, targetUid(可空)（实例/效果解析由路由实现方完成）
                    _router.ActivateAbility(At(a, 0), At(a, 1), At(a, 2));
                    break;

                case PlayerIntent.Kind.ResetActionCharges:
                    _router.ResetActionCharges(intent.PlayerId);
                    break;

                case PlayerIntent.Kind.DebugCommand:
                    // Args: 整行 PM 命令（客户端已作为单个参数上行，含空格）
                    _router.DebugCommand(intent.PlayerId, At(a, 0) ?? "");
                    break;

                default:
                    throw new InvalidOperationException($"未支持的意图种类: {intent.IntentKind}");
            }
        }

        /// <summary>若路由实现支持 UID 观察窗，则标记开始（仅 IUIDWatch 实现方响应）。</summary>
        private void MarkUidWatch()
        {
            if (_router is IUIDWatch w) w.MarkCreateWatch();
        }

        /// <summary>收集服务端在"建卡观察窗"内新建的实例 UID，回填到结果（解决 C/S UID 撞车）。</summary>
        private void CollectNewUid(IntentResult result)
        {
            if (_router is IUIDWatch w)
            {
                var uid = w.ConsumeCreatedUid();
                if (!string.IsNullOrEmpty(uid)) result.AssignedUids.Add(uid);
            }
        }

        private static string At(List<string> args, int i) => i < args.Count ? args[i] : null;
    }

    /// <summary>
    /// 【C/S 化 P3·模块C】客户端侧意图发送器：把本地意图经传输层上行到服务端。
    /// 持有 INetTransport 与本地玩家编号；SendIntent 返回 correlation id 供匹配下行 IntentResult。
    /// </summary>
    public class IntentClient
    {
        private readonly INetTransport _transport;
        public int LocalPlayerId { get; }

        public IntentClient(INetTransport transport, int localPlayerId)
        {
            _transport = transport;
            LocalPlayerId = localPlayerId;
        }

        /// <summary>上行一条意图。返回 IntentId（客户端保存用于匹配 IntentResult）。</summary>
        public string SendIntent(PlayerIntent.Kind kind, params string[] args)
        {
            var intent = PlayerIntent.Create(LocalPlayerId, kind, args);
            _transport.Send(LocalPlayerId, IntentSerializer.SerializeIntent(intent));
            return intent.IntentId;
        }

        /// <summary>客户端收到服务端下行的 IntentResult（json）后解析。</summary>
        public static IntentResult ParseResult(string json) => IntentSerializer.DeserializeResult(json);
    }
}
