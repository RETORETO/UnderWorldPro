using System;
using System.Collections.Generic;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【批次 1 测试辅助】把"座位号 1/2"包装成会话寻址的 `LoopbackTransport`。
    ///
    /// 背景：传输层自批次 1 起只认 **SessionId**（会话）。既有测试以"座位（玩家编号 1/2）"表达身份，
    /// 本辅助类提供 `SessionOf(seat) = "p<seat>"` 的固定映射，使这些测试**只改调用点、不改断言语义**。
    ///
    /// 注意：这是**测试**辅助，不属于生产代码；生产侧由房间把 Seat↔Session 绑定（见大厅设计方案 §五）。
    /// </summary>
    public class PlayerIdLoopback
    {
        /// <summary>底层会话化回环传输（可注入 `StartLocalLoopback` / `NetworkInputGateway`）。</summary>
        public readonly LoopbackTransport Transport = new LoopbackTransport();

        /// <summary>座位 → 会话 id 的测试约定。</summary>
        public static string SessionOf(int seat) => "p" + seat;

        /// <summary>反向解析（非本约定格式返回 0）。</summary>
        public static int SeatOf(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId[0] != 'p') return 0;
            return int.TryParse(sessionId.Substring(1), out var seat) ? seat : 0;
        }

        /// <summary>注册某座位的"客户端"处理（等价旧的 RegisterClient(seat, handler)）。</summary>
        public void RegisterClient(int seat, Action<string> onServerMessage)
            => Transport.RegisterSession(SessionOf(seat), onServerMessage);

        /// <summary>服务端 → 指定座位（等价旧的 Send(seat, msg)）。</summary>
        public void Send(int seat, string message) => Transport.SendToSession(SessionOf(seat), message);

        /// <summary>指定座位 → 服务端（等价旧的 ClientReply(seat, msg)）。</summary>
        public void ClientReply(int seat, string message) => Transport.ClientReply(SessionOf(seat), message);

        /// <summary>订阅服务端上行消息，回调参数还原为"座位号"（等价旧的 OnMessageReceived((seat,msg)=>…)）。</summary>
        public void OnMessageReceived(Action<int, string> handler)
            => Transport.OnSessionMessage((sid, msg) => handler?.Invoke(SeatOf(sid), msg));

        public void Start() => Transport.Start();
        public void Stop() => Transport.Stop();

        /// <summary>某座位是否已连接（等价旧的 IsPlayerConnected(seat)）。</summary>
        public bool IsPlayerConnected(int seat) => Transport.IsSessionConnected(SessionOf(seat));

        /// <summary>已连接座位数。</summary>
        public int ConnectedCount
        {
            get
            {
                int n = 0;
                if (Transport.IsSessionConnected(SessionOf(1))) n++;
                if (Transport.IsSessionConnected(SessionOf(2))) n++;
                return n;
            }
        }
    }
}
