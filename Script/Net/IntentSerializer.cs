using System.Text.Json;
using System.Text.Json.Serialization;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块C】PlayerIntent / IntentResult 的 JSON 序列化器。
    ///
    /// 关键纪律：DTO 使用 public 字段（field），而 System.Text.Json 默认忽略字段（见 P2 踩坑），
    /// 故 Options 必须 IncludeFields=true，否则网络往返后 IntentKind/PlayerId 等全部丢为默认值，
    /// 会导致服务端无法路由意图、玩家编号错乱。
    /// </summary>
    public static class IntentSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
        };

        public static string SerializeIntent(PlayerIntent intent)
            => JsonSerializer.Serialize(intent, Options);

        public static PlayerIntent DeserializeIntent(string json)
            => JsonSerializer.Deserialize<PlayerIntent>(json, Options);

        public static string SerializeResult(IntentResult result)
            => JsonSerializer.Serialize(result, Options);

        public static IntentResult DeserializeResult(string json)
            => JsonSerializer.Deserialize<IntentResult>(json, Options);
    }
}
