using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>房间列表中的一行（简化信息，供大厅列表显示；不含任何卡组信息）。</summary>
    public class RoomBrief
    {
        public string RoomId;
        public string RoomName;
        public RoomPhase Phase;
        /// <summary>当前在座人数（0–2）。</summary>
        public int PlayerCount;
        /// <summary>最大人数（当前规则恒为 2；留字段以便后续扩展）。</summary>
        public int MaxPlayers = 2;
        /// <summary>房主显示名（`昵称#座位`）。</summary>
        public string HostDisplayName;
    }

    /// <summary>
    /// 【大厅/房间 · 批次 1】房间列表快照（信封类型 `room_list`；`ListRooms` 应答）。
    /// 只暴露"可加入/进行中"的房间摘要，不含任何卡组与座位细节。
    /// </summary>
    public class RoomListSnapshot
    {
        public List<RoomBrief> Rooms = new List<RoomBrief>();

        /// <summary>服务端最大房间数（客户端可据此提示"房间已满"）。</summary>
        public int MaxRooms;

        /// <summary>按查看者裁剪：目前房间摘要本身不含敏感信息，保留此入口以便后续扩展（接口稳定，避免以后改协议）。</summary>
        public RoomListSnapshot ForViewer(string sessionId) => this;
    }
}
