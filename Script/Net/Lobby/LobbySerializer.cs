using System.Text.Json;
using System.Text.Json.Serialization;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】大厅协议 JSON 序列化器（与 `ChoiceSerializer` 对称）。
    ///
    /// 约定：所有大厅类型均为 public 字段 → 必须 `IncludeFields = true`（否则字段不参与序列化）；
    /// 枚举按**数值**序列化（避免改枚举名导致旧客户端解析失败）；
    /// 未知字段容忍（System.Text.Json 默认忽略未知字段），便于后续纯加法扩展。
    /// </summary>
    public static class LobbySerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true,
            // 枚举用数值：协议稳定性优先（不因重命名/重排而破坏解析）
            Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
        };

        public static string Serialize(LobbyRequest req) => Serialize<LobbyRequest>(req);
        public static LobbyRequest DeserializeRequest(string json) => Deserialize<LobbyRequest>(json);

        public static string Serialize(LobbyResponse res) => Serialize<LobbyResponse>(res);
        public static LobbyResponse DeserializeResponse(string json) => Deserialize<LobbyResponse>(json);

        public static string Serialize(RoomState state) => Serialize<RoomState>(state);
        public static RoomState DeserializeRoomState(string json) => Deserialize<RoomState>(json);

        public static string Serialize(RoomListSnapshot list) => Serialize<RoomListSnapshot>(list);
        public static RoomListSnapshot DeserializeRoomList(string json) => Deserialize<RoomListSnapshot>(json);

        public static string Serialize(ServerNotice notice) => Serialize<ServerNotice>(notice);
        public static ServerNotice DeserializeNotice(string json) => Deserialize<ServerNotice>(json);

        private static string Serialize<T>(T value) => value == null ? null : JsonSerializer.Serialize(value, Options);

        private static T Deserialize<T>(string json) where T : class
            => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
    }
}
