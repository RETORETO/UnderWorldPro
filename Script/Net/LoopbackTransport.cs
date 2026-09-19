using System;
using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2 / 大厅化·批次 1】同进程回环传输：无需真实 socket，用于端到端验证网关/会话/大厅链路。
    ///
    /// 模型（批次 1 起改为**会话寻址**）：
    /// - 每个会话一个入站 handler：`RegisterSession(sessionId, handler)`；
    /// - 服务端 → 客户端：`SendToSession(sessionId, msg)` 直接投递到该会话的 handler；
    /// - 客户端 → 服务端：`SendToServer(msg)`；回环场景下客户端**需要知道自己的会话 id**，故提供
    ///   `ClientReply(sessionId, msg)`（等价于"某客户端上行一条消息"）；
    /// - 服务端订阅 `OnSessionMessage/OnSessionConnected/OnSessionDisconnected`；
    /// - 会话建立：调用 `RegisterSession` 即视为"该会话已连接"（并对**后注册**的订阅者补发已连接事件，避免注册顺序敏感的测试）。
    ///
    /// 会话 id 约定：不强制；测试里常用 `"p1"/"p2"`（座位直连）或 `"s1"/"s2"`（服务端分配）。
    /// </summary>
    public class LoopbackTransport : INetTransport
    {
        // 会话 → 客户端侧消息处理（模拟真实客户端的 UI/AI 决策）
        private readonly Dictionary<string, Action<string>> _clientHandlers = new Dictionary<string, Action<string>>();

        // 服务端上行消息处理
        private Action<string, string> _serverHandler;
        private Action<string> _sessionConnectedHandler;
        private Action<string> _sessionDisconnectedHandler;

        // 服务端主动断开标记（回环下不真正移除 handler，只标记 + 通知，便于测试断言）
        private readonly HashSet<string> _disconnected = new HashSet<string>();

        private bool _running;

        /// <summary>该会话的发送次数（测试观测用）。</summary>
        public readonly Dictionary<string, int> SendCounts = new Dictionary<string, int>();

        /// <summary>注册一个"客户端"会话：模拟该会话收到服务端消息后如何处理。</summary>
        public void RegisterSession(string sessionId, Action<string> onServerMessage)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _clientHandlers[sessionId] = onServerMessage;
            _disconnected.Remove(sessionId);
            _sessionConnectedHandler?.Invoke(sessionId);   // 服务端会话建立事件
        }

        /// <summary>注销一个会话（模拟断开）。</summary>
        public void UnregisterSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _clientHandlers.Remove(sessionId);
            _disconnected.Add(sessionId);
            _sessionDisconnectedHandler?.Invoke(sessionId);
        }

        public void SendToSession(string sessionId, string message)
        {
            if (!_running) return;
            if (string.IsNullOrEmpty(sessionId)) return;
            if (_disconnected.Contains(sessionId)) return;
            if (!SendCounts.TryGetValue(sessionId, out var n)) n = 0;
            SendCounts[sessionId] = n + 1;
            if (_clientHandlers.TryGetValue(sessionId, out var h))
                h?.Invoke(message);
        }

        /// <summary>客户端 → 服务端（回环传输无法从"连接"推出来源，故由调用方显式给出会话 id）。</summary>
        public void ClientReply(string sessionId, string message)
        {
            if (!_running) return;
            if (_disconnected.Contains(sessionId)) return;
            _serverHandler?.Invoke(sessionId, message);
        }

        public void SendToServer(string message)
        {
            // 回环下"客户端"没有自身会话 id 的概念，必须用 ClientReply(sessionId, msg) 明确来源。
            // 保留本方法以满足接口契约（真实客户端传输用），此处仅在恰好只有一个会话时兜底投递。
            if (!_running) return;
            if (_clientHandlers.Count == 1)
            {
                foreach (var kv in _clientHandlers) { _serverHandler?.Invoke(kv.Key, message); return; }
            }
            GDLog("[LoopbackTransport] SendToServer 无法确定来源会话（会话数 != 1），已忽略。请用 ClientReply(sessionId, msg)。");
        }

        public void OnSessionMessage(Action<string, string> handler) => _serverHandler = handler;

        public void OnSessionConnected(Action<string> handler)
        {
            _sessionConnectedHandler = handler;
            // 补发已注册会话（避免"先注册客户端、后订阅连接事件"的测试因顺序而失败）
            foreach (var sid in _clientHandlers.Keys) handler?.Invoke(sid);
        }

        public void OnSessionDisconnected(Action<string> handler) => _sessionDisconnectedHandler = handler;

        public void Start() => _running = true;
        public void Stop() => _running = false;

        public bool IsSessionConnected(string sessionId)
            => !string.IsNullOrEmpty(sessionId) && _clientHandlers.ContainsKey(sessionId) && !_disconnected.Contains(sessionId);

        public int ConnectedSessionCount
        {
            get
            {
                int n = 0;
                foreach (var sid in _clientHandlers.Keys) if (!_disconnected.Contains(sid)) n++;
                return n;
            }
        }

        public void DisconnectSession(string sessionId, string reason)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _disconnected.Add(sessionId);
            _sessionDisconnectedHandler?.Invoke(sessionId);
        }

        private static void GDLog(string msg) => System.Console.WriteLine(msg);
    }
}
