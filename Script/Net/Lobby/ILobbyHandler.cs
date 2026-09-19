namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】大厅业务处理接口（**批次 3 提供真实实现 `LobbyServer`**）。
    ///
    /// 为什么先定接口：`ServerHost` 只负责"连接/会话/协议/幂等/校验"这类**与业务无关**的底座，
    /// 房间状态机与权限属于批次 3。接口 + 注入使两者可以独立演进，且最终形态不变（不是临时木桩）：
    /// 批次 3 只是把注入的实现从测试替身换成真实 `LobbyServer`，`ServerHost` 一行不改。
    ///
    /// 实现约定：
    ///  - 一切回包经 `ServerHost`（`SendTo`/`Respond`/`Notice`），实现不直接摸传输层；
    ///  - 实现收到的一定是**已通过 Hello/版本/幂等校验**的请求；
    ///  - 会话断开由 `OnSessionLeft` 通知实现（用于释放座位/房主转移）。
    /// </summary>
    public interface ILobbyHandler
    {
        /// <summary>处理一个大厅/房间请求（已通过底座校验）。</summary>
        void Handle(string sessionId, LobbyRequest request);

        /// <summary>会话断开（实现据此释放座位、转移房主、销毁空房）。</summary>
        void OnSessionLeft(string sessionId);
    }

    /// <summary>
    /// 【大厅/房间 · 批次 1】对局通道路由接口（**批次 5 提供真实实现 `MatchRouter`**）。
    ///
    /// 职责：判断某会话的对局类消息（`intent` / `choice_res`）该交给哪个 `MatchHost`，
    /// 并返回是否已处理。未处理时 `ServerHost` 会回一条"当前不在对局中"的通知（避免客户端静默丢包）。
    /// </summary>
    public interface IMatchRouter
    {
        /// <summary>尝试路由一条对局通道消息；返回 true 表示已处理。</summary>
        bool TryRoute(string sessionId, string envelopeType, string payload);
    }

    /// <summary>默认对局路由：任何对局消息都判为"当前不在对局中"（批次 5 之前的正式行为，非临时木桩）。</summary>
    public sealed class NoMatchRouter : IMatchRouter
    {
        public bool TryRoute(string sessionId, string envelopeType, string payload) => false;
    }
}
