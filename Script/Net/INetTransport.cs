using System;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2】传输抽象层：将"消息如何发到客户端/从客户端收"与具体网络实现解耦。
    ///
    /// 服务端权威架构下，服务端持有本接口实例：
    /// - Send：向指定玩家下发一条消息（如序列化的 ChoiceRequest JSON）
    /// - Poll/订阅：接收客户端回包（如序列化的 ChoiceResult JSON）
    ///
    /// 实现可插拔：
    /// - LoopbackTransport：同进程回环（测试/单机双端验证用，无真实 socket）—— 已实现（Net/LoopbackTransport.cs）
    /// - （P3 里程碑）GodotEnetTransport：基于 Godot ENetMultiplayerPeer 的真实联机，置于 Net/Godot/ 子目录
    ///   （引入 Godot 依赖，不污染 Net/ 根目录的纯托管性，保证 Headless 服务端仍可用 Loopback/自定义 transport）
    /// - （P3 可选）TcpTransport：基于 System.Net.Sockets 的纯托管 TCP，同样不依赖 Godot
    ///
    /// 纪律：本接口不依赖 Godot，保持 Net/ 根目录纯托管（Headless 服务端可用）。
    /// 真实传输实现（依赖 Godot 或具体 socket 库）应放在 Net/ 的子目录，避免破坏根目录纯净性。
    /// </summary>
    public interface INetTransport
    {
        /// <summary>向指定玩家发送一条文本消息（JSON 等）。playerId 为游戏内玩家编号（1/2）。</summary>
        void Send(int playerId, string message);

        /// <summary>订阅客户端上行消息。回调参数为 (playerId, message)。</summary>
        void OnMessageReceived(Action<int, string> handler);

        /// <summary>启动传输（监听/连接）。</summary>
        void Start();

        /// <summary>停止传输并释放资源。</summary>
        void Stop();

        /// <summary>驱动传输轮询（如 ENet 需主动 Poll）。默认空实现：同步投递型传输（Loopback）无需轮询。</summary>
        void Poll() { }

        /// <summary>查询某游戏内玩家编号是否已连入。默认 true：同步回环型传输始终视为已连接；真实传输（ENet）按连接状态返回。</summary>
        bool IsPlayerConnected(int playerId) => true;
    }
}
