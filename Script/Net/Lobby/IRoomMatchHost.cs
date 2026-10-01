using System;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】房间内"一场对局"的抽象（**纯接口**，由 Godot 侧 `MatchHost` 实现）。
    ///
    /// 为什么用接口：`Script/Net/Lobby/` 保持纯托管（房间状态机与协议可独立演进/复用），
    /// 而对局宿主需要 `GameInitializer`（Godot Node）。房间只依赖本接口，工厂由服务端入口注入。
    /// </summary>
    public interface IRoomMatchHost
    {
        /// <summary>接管网络侧（装配 hub/网关/订阅）。房间在绑座之后调用。</summary>
        void StartServing();

        /// <summary>停止托管（房间销毁/重开时调用），清除事件订阅与网关注入。</summary>
        void StopServing();

        /// <summary>
        /// 【第0回合·调度步骤】进入开局调度（联机）。由房间层在**房间状态广播之后**调用：
        /// 客户端收到 `room.Phase=InMatch` 才挂载战场页并订阅决策请求，故调度请求必须晚于该广播下发。
        /// </summary>
        void StartMulligan();

        /// <summary>把座位绑定到会话。</summary>
        void BindSeat(int seat, string sessionId);

        /// <summary>按会话解绑。</summary>
        void UnbindSession(string sessionId);

        /// <summary>会话 → 座位（未绑定返回 0）。</summary>
        int SeatOfSession(string sessionId);

        /// <summary>座位 → 会话（未绑定返回 null）。</summary>
        string SessionOfSeat(int seat);

        /// <summary>处理一条对局通道消息（intent / choice_res）；返回是否已处理。</summary>
        bool HandleMessage(string sessionId, string envelopeType, string payload);

        /// <summary>快照下行（按座位过滤）。</summary>
        void PushSnapshot();

        /// <summary>
        /// 某座位判负（D5：对局中掉线/离桌，当前实现＝直接判该方认输；Phase D 重连后改为"宽限期超时判负"）。
        /// </summary>
        void ForfeitSeat(int seat, string reason);

        /// <summary>本场是否已结束。</summary>
        bool IsOver { get; }

        /// <summary>获胜座位（0=未结束/无胜者）。</summary>
        int WinnerSeat { get; }

        /// <summary>结束回调（座位号）。房间据此置 `Finished` 并广播结果。</summary>
        Action<int> OnGameOver { get; set; }
    }
}
