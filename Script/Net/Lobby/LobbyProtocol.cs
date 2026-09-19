using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】协议常量与枚举（纯托管，不依赖 Godot，headless 服务端可用）。
    ///
    /// 归属说明：大厅协议与对局协议**分层但共用一条连接**——
    ///   - 大厅/房间消息用本文件的类型，经 `NetEnvelope.Types.LobbyRequest/LobbyResponse/RoomState/RoomList/ServerNotice` 信封；
    ///   - 对局消息（intent / choice_req / choice_res / SNAP:）沿用既有类型，不在此文件定义。
    /// </summary>
    public static class LobbyProtocol
    {
        /// <summary>
        /// 协议版本：`Hello` 携带、服务端比对；不匹配直接拒绝并断开（不做兼容矩阵，只做显式拒绝 + 日志）。
        /// 递增时机：大厅/房间协议出现**不兼容**变更时（纯加法字段不需要递增）。
        /// </summary>
        public const int ProtocolVersion = 1;
    }

    /// <summary>大厅/房间请求种类（`LobbyRequest.Kind`）。</summary>
    public enum LobbyRequestKind
    {
        /// <summary>登录大厅：携带协议版本与昵称。任何其它请求之前必须先 Hello。</summary>
        Hello,
        /// <summary>请求房间列表（进入大厅后 / 房间列表刷新）。</summary>
        ListRooms,
        /// <summary>创建房间（创建者入 seat1 并成为房主）。</summary>
        CreateRoom,
        /// <summary>加入指定房间。</summary>
        JoinRoom,
        /// <summary>离开当前房间（座位释放；房主离开则转移）。</summary>
        LeaveRoom,
        /// <summary>设置准备状态（服务端校验卡组后才生效）。</summary>
        SetReady,
        /// <summary>提交/更换卡组（服务端校验，**准备时与开局时各校验一次**；**已准备时拒绝更换** = 卡组锁定）。</summary>
        SelectDeck,
        /// <summary>开始对局（仅房主；服务端复检双方卡组）。</summary>
        StartMatch,
        /// <summary>踢出某座位玩家（仅房主）。</summary>
        Kick,
        /// <summary>请求再来一局（双方都点后：关闭本局、回到同一房间的未准备状态）。</summary>
        Rematch,
        /// <summary>心跳/延迟测量（仅回显，不做状态变更）。</summary>
        Ping,
    }

    /// <summary>大厅/房间请求的错误码（`LobbyResponse.ErrorCode` 与 `ServerNotice.Code` 共用）。</summary>
    public enum LobbyErrorCode
    {
        None = 0,
        /// <summary>协议版本不匹配（服务端会断开连接）。</summary>
        VersionMismatch,
        /// <summary>尚未 Hello 就发送了其它请求。</summary>
        NotHelloed,
        /// <summary>重复 Hello。</summary>
        AlreadyHelloed,
        /// <summary>昵称非法（空/超长/含非法字符）。</summary>
        NameInvalid,
        /// <summary>请求种类无法识别。</summary>
        UnknownRequest,
        /// <summary>房间不存在（或已销毁）。</summary>
        RoomNotFound,
        /// <summary>房间已满（两个座位都被占）。</summary>
        RoomFull,
        /// <summary>该会话已在某个房间中。</summary>
        AlreadyInRoom,
        /// <summary>该会话不在任何房间中。</summary>
        NotInRoom,
        /// <summary>不是房主，无权限执行该操作。</summary>
        NotHost,
        /// <summary>当前房间阶段不允许该操作。</summary>
        PhaseMismatch,
        /// <summary>卡组违规（卡牌不存在/结构非法等基础校验失败）。</summary>
        InvalidDeck,
        /// <summary>已准备：卡组已锁定（需先取消准备才能更换卡组）。</summary>
        DeckLocked,
        /// <summary>目标座位为空或非本房间座位。</summary>
        InvalidSeat,
        /// <summary>当前不处于对局中（对局通道消息被拒）。</summary>
        NotInMatch,
        /// <summary>被服务端断开（如被踢、服务器已满、协议不匹配）。</summary>
        Disconnected,
        /// <summary>服务端连接数已满。</summary>
        ServerFull,
        /// <summary>服务端内部错误。</summary>
        Internal,
    }

    /// <summary>房间阶段（服务端权威；客户端只读）。</summary>
    public enum RoomPhase
    {
        /// <summary>等待玩家 / 可自由准备。</summary>
        Waiting = 0,
        /// <summary>两名玩家都在座且都已准备（房主可开始）。</summary>
        Ready,
        /// <summary>正在建局/发初始快照（拒绝一切操作，避免开局竞态）。</summary>
        Starting,
        /// <summary>对局进行中（除 Ping 外，房间操作一律拒绝）。</summary>
        InMatch,
        /// <summary>本局已结束（可再来一局 / 离开）。</summary>
        Finished,
        /// <summary>房间已关闭（收到通知后客户端回大厅）。</summary>
        Closed,
    }
}
