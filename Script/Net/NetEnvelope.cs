using System;

namespace Script.Net
{
    /// <summary>
    /// 【Phase B·协议加固】消息类型信封 v2：给每条消息一个**显式类型标签**，替代原先"靠 JSON 形状猜类型"
    /// 的脆弱分发（例如 `ChoiceRequest/ChoiceResult` 与 `IntentResult` 都靠"是否有 RequestId"区分）。
    ///
    /// 报文格式（文本，前缀 + 类型 + 分隔符 + 原始 JSON 载荷，保持既有各 Serializer 不变）：
    ///   <c>V2:&lt;type&gt;|&lt;payloadJson&gt;</c>
    /// 其中 type 取值见 <see cref="Types"/>；分隔符只取**第一个** '|'，故 JSON 载荷内可含 '|' 不受影响。
    ///
    /// 兼容策略（一个版本周期内双读）：
    ///   - 接收方先 `TryUnwrap`；成功按显式类型分发；
    ///   - 失败（无前缀 = 旧客户端/旧格式）→ 回退到原有"形状识别"分支，行为与之前完全一致；
    ///   - 快照沿用既有 `SNAP:` 前缀（其本身已是类型标签），不重复包裹。
    /// </summary>
    public static class NetEnvelope
    {
        /// <summary>信封前缀（v2）。</summary>
        public const string Prefix = "V2:";

        private const char Separator = '|';

        /// <summary>信封类型标签。</summary>
        public static class Types
        {
            public const string Intent = "intent";                 // C→S：PlayerIntent
            public const string IntentResult = "intent_result";    // S→C：IntentResult（裁决回执）
            public const string ChoiceRequest = "choice_req";      // S→C：ChoiceRequest（请决策）
            public const string ChoiceResult = "choice_res";       // C→S：ChoiceResult（回包）
        }

        /// <summary>包裹一条消息：返回 <c>V2:type|payload</c>。</summary>
        public static string Wrap(string type, string payloadJson)
            => $"{Prefix}{type}{Separator}{payloadJson ?? ""}";

        /// <summary>尝试解包：成功返回 true 并给出 type 与 payload；非信封格式返回 false（payload 原样返回）。</summary>
        public static bool TryUnwrap(string message, out string type, out string payload)
        {
            type = null;
            payload = message;
            if (string.IsNullOrEmpty(message) || !message.StartsWith(Prefix, StringComparison.Ordinal)) return false;

            int sep = message.IndexOf(Separator, Prefix.Length);
            if (sep < 0) return false;   // 格式不完整 → 视为非信封

            type = message.Substring(Prefix.Length, sep - Prefix.Length);
            payload = message.Substring(sep + 1);
            return !string.IsNullOrEmpty(type);
        }

        /// <summary>便捷：取出载荷（信封则解包，否则原样返回）。用于"不关心类型、只要载荷"的调用点。</summary>
        public static string UnwrapPayload(string message)
            => TryUnwrap(message, out _, out var payload) ? payload : message;

        /// <summary>便捷：取出类型标签（非信封返回 null）。</summary>
        public static string UnwrapType(string message)
            => TryUnwrap(message, out var type, out _) ? type : null;
    }
}
