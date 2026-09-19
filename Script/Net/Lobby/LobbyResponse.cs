using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】服务端 → 客户端的请求应答（信封类型 `lobby_res`）。
    /// 每个请求都有且只有一个应答（`RequestId` 回显）；状态变更本身另经 `room_state` 全量广播。
    /// </summary>
    public class LobbyResponse
    {
        /// <summary>对应请求的 RequestId（回显）。</summary>
        public string RequestId;

        /// <summary>是否成功（false 时看 `ErrorCode`/`ErrorText`）。</summary>
        public bool Ok;

        /// <summary>错误码。</summary>
        public LobbyErrorCode ErrorCode;

        /// <summary>错误/提示文案（面向玩家，可直接显示）。</summary>
        public string ErrorText;

        /// <summary>
        /// 本会话的 id（**仅 `Hello` 成功时填**）：客户端据此知道自己的会话 id（调试/显示/后续重连预留）。
        /// 会话 id 在连接建立时就由服务端分配，`Hello` 只是把它回给客户端。
        /// </summary>
        public string SessionId;

        /// <summary>本会话当前所在房间的状态（若有）。</summary>
        public RoomState RoomState;

        /// <summary>房间列表（`ListRooms` 应答）。</summary>
        public RoomListSnapshot RoomList;

        /// <summary>构造成功应答。</summary>
        public static LobbyResponse Success(string requestId) => new LobbyResponse { RequestId = requestId, Ok = true };

        /// <summary>构造失败应答。</summary>
        public static LobbyResponse Fail(string requestId, LobbyErrorCode code, string text = null)
            => new LobbyResponse { RequestId = requestId, Ok = false, ErrorCode = code, ErrorText = text ?? DefaultText(code) };

        /// <summary>错误码的默认文案（服务端未给具体原因时使用）。</summary>
        public static string DefaultText(LobbyErrorCode code) => code switch
        {
            LobbyErrorCode.VersionMismatch => "客户端与服务端协议版本不一致，请更新客户端。",
            LobbyErrorCode.NotHelloed => "请先登录大厅（Hello）。",
            LobbyErrorCode.AlreadyHelloed => "已经登录过了。",
            LobbyErrorCode.NameInvalid => "昵称不合法（不能为空、不得超过长度上限）。",
            LobbyErrorCode.UnknownRequest => "无法识别的请求。",
            LobbyErrorCode.RoomNotFound => "房间不存在或已被关闭。",
            LobbyErrorCode.RoomFull => "房间已满。",
            LobbyErrorCode.AlreadyInRoom => "你已经在某个房间中。",
            LobbyErrorCode.NotInRoom => "你当前不在任何房间中。",
            LobbyErrorCode.NotHost => "只有房主可以执行该操作。",
            LobbyErrorCode.PhaseMismatch => "当前房间状态不允许该操作。",
            LobbyErrorCode.InvalidDeck => "卡组违规。",
            LobbyErrorCode.InvalidSeat => "目标座位无效。",
            LobbyErrorCode.NotInMatch => "当前不在对局中。",
            LobbyErrorCode.Disconnected => "与服务端的连接已断开。",
            LobbyErrorCode.ServerFull => "服务器已满，请稍后再试。",
            _ => "服务端内部错误。",
        };
    }
}
