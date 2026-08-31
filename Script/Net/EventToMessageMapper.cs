using System;
using Script.Enums;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块D】渲染事件 → 网络消息映射层（纯托管，不依赖 Godot / Manager / UI）。
    ///
    /// 前置条件 ⑧ 的收口：把 Manager 事件总线携带的渲染事件（卡移动/状态变更/阶段/优先权/胜负等）
    /// 映射为可序列化的 <see cref="RenderEvent"/> 消息，供传输层下发。这是"事件驱动渲染"与
    /// "网络消息"之间的桥梁：客户端把本地渲染事件转发为消息（上报），或服务端把权威状态变更
    /// 广播为消息（下行）。
    ///
    /// 职责边界（与 Net/ 纯净纪律一致）：
    ///   - 本类**只接收已抽取的纯参数**（int/string/enum 承载），不触碰 CardInstance 等 Godot 实体；
    ///   - 调用方（UI 层）在事件回调里从事件参数抽取纯数据，再调本类的 CreateXxx() 得到 RenderEvent；
    ///   - 构造注入 _emit 输出回调（Action&lt;RenderEvent&gt;），决定消息去向（序列化下发 / 本地日志 / 丢弃）。
    ///
    /// 典型接入（UI 层 / 服务端装配处）：
    ///   var mapper = new EventToMessageMapper(evt => transport.Send(target, EventSerializer.Serialize(evt)));
    ///   _zoneManager.OnCardMoved += (card, zone, pid, detail) => mapper.OnCardMoved(card.UID, zone, pid, detail);
    /// </summary>
    public class EventToMessageMapper
    {
        private readonly Action<RenderEvent> _emit;

        public EventToMessageMapper(Action<RenderEvent> emit) => _emit = emit ?? (_ => { });

        /// <summary>把一条已组装的渲染事件交给输出回调（统一出口）。</summary>
        public void Publish(RenderEvent evt) => _emit(evt);

        // ---------- 工厂：把纯参数组装为 RenderEvent（不触碰 Model） ----------

        public RenderEvent CreateCardMoved(string uid, CardZone zone, int zoneOwner, string detail)
            => new RenderEvent { Kind = RenderEvent.EventKind.CardMoved, Uid = uid, ToZone = (int)zone, ZoneOwnerId = zoneOwner, ZoneDetailId = detail };

        public RenderEvent CreateCardStateChanged(string uid, int changeKind, bool covert, bool tapped, int damage, int trauma)
            => new RenderEvent { Kind = RenderEvent.EventKind.CardStateChanged, Uid = uid, ChangeKind = changeKind, IsCovert = covert, IsTapped = tapped, Damage = damage, Trauma = trauma };

        public RenderEvent CreateCardDied(string uid) => new RenderEvent { Kind = RenderEvent.EventKind.CardDied, Uid = uid };

        public RenderEvent CreateCardPlayed(int playerId, string uid, string targetUid)
            => new RenderEvent { Kind = RenderEvent.EventKind.CardPlayed, PlayerId = playerId, Uid = uid, TargetUid = targetUid };

        public RenderEvent CreateCardDrawn(int playerId, string uid) => new RenderEvent { Kind = RenderEvent.EventKind.CardDrawn, PlayerId = playerId, Uid = uid };

        public RenderEvent CreatePhaseChanged(int phase) => new RenderEvent { Kind = RenderEvent.EventKind.PhaseChanged, Phase = phase };

        public RenderEvent CreatePriorityChanged(int priorityPlayerId) => new RenderEvent { Kind = RenderEvent.EventKind.PriorityChanged, PriorityPlayerId = priorityPlayerId };

        public RenderEvent CreateTurnStarted(int turnCount, int priorityPlayerId)
            => new RenderEvent { Kind = RenderEvent.EventKind.TurnStarted, TurnCount = turnCount, PriorityPlayerId = priorityPlayerId };

        public RenderEvent CreateTurnEnded(int turnCount) => new RenderEvent { Kind = RenderEvent.EventKind.TurnEnded, TurnCount = turnCount };

        public RenderEvent CreateGameOver(int winnerId) => new RenderEvent { Kind = RenderEvent.EventKind.GameOver, PlayerId = winnerId };

        public RenderEvent CreateAreaWon(int playerId, string areaUid)
            => new RenderEvent { Kind = RenderEvent.EventKind.AreaWon, PlayerId = playerId, Uid = areaUid };

        public RenderEvent CreateAssetsChanged() => new RenderEvent { Kind = RenderEvent.EventKind.AssetsChanged };

        public RenderEvent CreateStackChanged() => new RenderEvent { Kind = RenderEvent.EventKind.StackChanged };
    }
}
