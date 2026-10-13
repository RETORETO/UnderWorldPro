using System;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2 / 大厅化改造·批次 1】传输抽象层：把"消息如何发到某个客户端/从客户端收"与具体网络实现解耦。
    ///
    /// **寻址模型（批次 1 起）**：传输层只认 **SessionId**（服务端为每条连接分配的会话标识），不再认"玩家编号"。
    ///   - 服务端：`SendToSession(sessionId, msg)` 定向下发；`OnSessionMessage/OnSessionConnected/OnSessionDisconnected` 订阅；
    ///   - 客户端：只有"我对服务端"这一条连接，用 `SendToServer(msg)`，不需要任何寻址；
    ///   - 对局层的"playerId（=座位 1/2）"由对局层把座位映射到 SessionId 后调用本接口（见 `NetworkInputGateway` 注入的解析器）。
    ///
    /// 为什么不在传输层保留"playerId 寻址"：大厅模型下同一条连接在不同阶段身份不同（先登录→再进房→座位才确定），
    /// 两套寻址并存会长期分叉（设计方案 §十四 明令禁止的临时做法之一）。
    ///
    /// 实现可插拔：
    /// - `LoopbackTransport`：同进程回环（测试用，无真实 socket）；
    /// - `Script.Net.Godot.GodotWebSocketTransport`：Godot WebSocket 真实联机（**2026-10-14 起桌面与 Web 都用它**）；
    /// - `Script.Net.Godot.GodotEnetTransport`：Godot ENet 实现（保留备用，已不在联机主链路上）。
    ///
    /// 纪律：本接口不依赖 Godot，保持 `Net/` 根目录纯托管（headless 服务端可用）。
    /// </summary>
    public interface INetTransport
    {
        // ==================== 发送 ====================

        /// <summary>服务端 → 指定会话发送一条文本消息。会话不存在时静默忽略（实现内可打日志）。</summary>
        void SendToSession(string sessionId, string message);

        /// <summary>客户端 → 服务端发送一条文本消息（客户端只有唯一对端，无需寻址）。</summary>
        void SendToServer(string message);

        // ==================== 订阅 ====================

        /// <summary>订阅客户端上行消息：回调参数为 (sessionId, message)。</summary>
        void OnSessionMessage(Action<string, string> handler);

        /// <summary>订阅"会话建立"（服务端角色）：参数为服务端分配的新会话 id。</summary>
        void OnSessionConnected(Action<string> handler);

        /// <summary>订阅"会话断开"（服务端角色）。</summary>
        void OnSessionDisconnected(Action<string> handler);

        // ==================== 生命周期 ====================

        /// <summary>启动传输（监听/连接）。</summary>
        void Start();

        /// <summary>停止传输并释放资源。</summary>
        void Stop();

        /// <summary>驱动传输轮询（如 ENet 需主动 Poll）。默认空实现：同步投递型传输（Loopback）无需轮询。</summary>
        void Poll() { }

        // ==================== 查询与服务端操作 ====================

        /// <summary>某会话是否仍连接着。</summary>
        bool IsSessionConnected(string sessionId);

        /// <summary>当前连接数（服务端角色；客户端角色按实现返回）。</summary>
        int ConnectedSessionCount { get; }

        /// <summary>服务端主动断开某会话（踢人 / 服务器满 / 协议不匹配）。默认空实现，便于测试替身。</summary>
        void DisconnectSession(string sessionId, string reason) { }
    }
}
