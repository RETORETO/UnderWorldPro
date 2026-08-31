using System;
using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2】同进程回环传输：无需真实 socket，用于端到端验证 NetworkInputGateway + *Async 链路。
    ///
    /// 模型：维护"每个玩家一个入站队列 + 一个 handler"。
    /// - Send(playerId, msg)：把消息推入该玩家的入站队列，并同步调用其客户端 handler（模拟网络延迟可加 Task.Delay）。
    /// - 客户端 handler 处理消息（如"收到 PayOrTap 请求→回 Pay=true"）后调用 ClientReply 把回包经传输发回服务端。
    ///
    /// 这等价于"两个玩家各是一个进程、通过 socket 互联"，但都在一个测试进程内，便于 CI/无网环境验证。
    /// </summary>
    public class LoopbackTransport : INetTransport
    {
        // 每个玩家的客户端侧消息处理（模拟真实客户端的 UI/AI 决策）
        private readonly Dictionary<int, Action<string>> _clientHandlers = new Dictionary<int, Action<string>>();
        // 服务端上行消息处理（由 NetworkInputGateway 注册）
        private Action<int, string> _serverHandler;
        private bool _running;

        /// <summary>注册"客户端"侧处理：模拟玩家收到服务器请求后如何回应。测试内设定。</summary>
        public void RegisterClient(int playerId, Action<string> onServerMessage)
        {
            _clientHandlers[playerId] = onServerMessage;
        }

        public void Send(int playerId, string message)
        {
            if (!_running) return;
            // 模拟网络：直接投递到该玩家的客户端 handler
            if (_clientHandlers.TryGetValue(playerId, out var h))
                h?.Invoke(message);
        }

        /// <summary>客户端回包：把响应发回服务端（服务端 OnMessageReceived 注册的 handler 会收到）。</summary>
        public void ClientReply(int playerId, string message)
        {
            if (!_running) return;
            _serverHandler?.Invoke(playerId, message);
        }

        public void OnMessageReceived(Action<int, string> handler)
        {
            _serverHandler = handler;
        }

        public void Start() => _running = true;
        public void Stop() => _running = false;
    }
}
