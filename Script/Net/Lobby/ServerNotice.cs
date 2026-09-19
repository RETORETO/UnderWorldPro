namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】服务端主动通知（信封类型 `server_notice`）。
    /// 用于**非应答类**提示：被踢、房主关闭房间、服务器即将停止、请求被拒等。
    /// </summary>
    public class ServerNotice
    {
        public LobbyErrorCode Code;
        public string Text;
        /// <summary>相关房间（可空）。</summary>
        public string RoomId;

        public static ServerNotice Of(LobbyErrorCode code, string text, string roomId = null)
            => new ServerNotice { Code = code, Text = string.IsNullOrEmpty(text) ? LobbyResponse.DefaultText(code) : text, RoomId = roomId };
    }
}
