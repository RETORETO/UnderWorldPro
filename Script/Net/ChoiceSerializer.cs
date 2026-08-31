using System.Text.Json;
using System.Text.Json.Serialization;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P2】ChoiceRequest/ChoiceResult 的 JSON 序列化器（与 SnapshotSerializer 对称）。
    /// 用于网络传输层在客户端/服务端间交换"决策请求-响应"。
    /// </summary>
    public static class ChoiceSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 关键：ChoiceRequest/ChoiceResult 均为 public 字段（RequestId/PlayerId/Pay/...），
            // System.Text.Json 默认忽略字段，必须显式 IncludeFields 才能让 RequestId 在请求-响应间往返，
            // 否则服务端 correlation 匹配失败（RequestId 为空）导致 await 永不完成。
            IncludeFields = true,
        };

        public static string Serialize(ChoiceRequest req)
            => JsonSerializer.Serialize(req, Options);

        public static ChoiceRequest DeserializeRequest(string json)
            => JsonSerializer.Deserialize<ChoiceRequest>(json, Options);

        public static string SerializeResult(ChoiceResult r)
            => JsonSerializer.Serialize(r, Options);

        public static ChoiceResult DeserializeResult(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<ChoiceResult>(json, Options);
        }
    }
}
