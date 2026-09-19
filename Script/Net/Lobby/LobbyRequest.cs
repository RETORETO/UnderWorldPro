namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】客户端 → 服务端的大厅/房间请求（信封类型 `lobby_req`）。
    ///
    /// 纪律：
    ///  - 本类为**纯数据**（无 Godot 依赖），字段为 public（`LobbySerializer` 用 IncludeFields 序列化）；
    ///  - 一切请求都只是"请求"：服务端权威校验后才改状态，客户端不得自行判定房主/准备/开局；
    ///  - `RequestId` 由客户端生成并在应答中回显，服务端据此做相关性匹配与**幂等去重**。
    /// </summary>
    public class LobbyRequest
    {
        /// <summary>请求唯一标识（客户端生成；服务端回显 + 幂等去重键）。</summary>
        public string RequestId;

        /// <summary>请求种类。</summary>
        public LobbyRequestKind Kind;

        /// <summary>客户端协议版本（仅 `Hello` 有意义；服务端比对 `LobbyProtocol.ProtocolVersion`）。</summary>
        public int ProtocolVersion;

        /// <summary>玩家昵称（仅 `Hello`；允许重名，房间内以 `昵称#座位` 显示）。</summary>
        public string PlayerName;

        /// <summary>目标房间（`JoinRoom` 必填；`CreateRoom` 可留空=服务端生成）。</summary>
        public string RoomId;

        /// <summary>房间设置（仅 `CreateRoom`）。</summary>
        public RoomSettings RoomSettings;

        /// <summary>准备状态（仅 `SetReady`）。</summary>
        public bool Ready;

        /// <summary>卡组标识（仅 `SelectDeck`：预置卡组名或路径；与 `DeckJson` 二选一）。</summary>
        public string DeckId;

        /// <summary>卡组内容 JSON（仅 `SelectDeck`；由服务端做基础校验：结构/卡牌 UID 存在/缺秘社补默认）。</summary>
        public string DeckJson;

        /// <summary>卡组显示名（可空，空则服务端用默认名）。</summary>
        public string DeckName;

        /// <summary>目标座位（仅 `Kick`；1 或 2）。</summary>
        public int TargetSeat;

        /// <summary>客户端时间戳（毫秒；仅 `Ping`，用于回显测 RTT）。</summary>
        public long ClientTimeMs;
    }
}
