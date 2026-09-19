using System.Text.Json;
using System.Text.Json.Serialization;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P1-B】快照序列化工具：将 GameSnapshot 转为可网络下发的纯文本（JSON）。
    ///
    /// 用于：
    /// - 服务端向客户端下发"当前局面应长什么样"（客户端按此重建/更新 UI，不重算规则）
    /// - 断线重连：客户端重连后请求最新快照恢复
    /// - 历史回溯（D44 雏形）：周期性快照存档
    ///
    /// 纪律：仅依赖 System.Text.Json（纯托管），不引入 Godot 序列化器，保证 Headless 服务端可用。
    /// 所有类型（GameSnapshot/ZoneCardRef/...）均定义在 Net/ 下且为纯数据，无 Godot 类型字段。
    /// </summary>
    public static class SnapshotSerializer
    {
        /// <summary>
        /// 快照报文前缀（**唯一真相源**）：对局通道上"快照"与信封消息（`V2:<type>|…`）共用一条连接，
        /// 靠本前缀区分。服务端下发、客户端识别、以及对局宿主（`MatchHost`）都必须引用本常量。
        /// </summary>
        public const string SnapPrefix = "SNAP:";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 关键：GameSnapshot/ZoneCardRef/PlayerSnapshot 等均为 public 字段（field），
            // System.Text.Json 默认忽略字段，必须显式 IncludeFields 才能序列化往返不丢数据。
            IncludeFields = true,
        };

        public static string Serialize(GameSnapshot snap)
        {
            if (snap == null) return "null";
            return JsonSerializer.Serialize(snap, Options);
        }

        public static GameSnapshot Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            return JsonSerializer.Deserialize<GameSnapshot>(json, Options);
        }
    }
}
