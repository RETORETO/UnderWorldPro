using System;
using System.Collections.Generic;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】房间注册表：创建/查询/移除/上限/列表摘要。
    /// 纯数据结构，不含协议与广播（那在 `LobbyServer`）。
    /// </summary>
    public class RoomRegistry
    {
        private readonly Dictionary<string, RoomSession> _rooms = new Dictionary<string, RoomSession>();
        private readonly int _maxRooms;
        private int _roomSeq;

        public RoomRegistry(int maxRooms = 8)
        {
            _maxRooms = maxRooms <= 0 ? 8 : maxRooms;
        }

        public int Count => _rooms.Count;
        public int MaxRooms => _maxRooms;
        public bool IsFull => _rooms.Count >= _maxRooms;
        public IEnumerable<RoomSession> All => _rooms.Values;

        /// <summary>创建房间（房号形如 `r1`）。已满返回 null。</summary>
        public RoomSession Create(string roomName, long nowTick, RoomSettings settings = null)
        {
            if (IsFull) return null;
            string id = "r" + (++_roomSeq);
            var room = new RoomSession
            {
                RoomId = id,
                RoomName = string.IsNullOrWhiteSpace(roomName) ? $"{id} 的房间" : roomName.Trim(),
                Settings = settings ?? new RoomSettings(),
                CreatedAtTick = nowTick,
                LastActiveTick = nowTick,
            };
            _rooms[id] = room;
            return room;
        }

        public RoomSession Get(string roomId)
            => !string.IsNullOrEmpty(roomId) && _rooms.TryGetValue(roomId, out var r) ? r : null;

        public void Remove(string roomId)
        {
            if (!string.IsNullOrEmpty(roomId)) _rooms.Remove(roomId);
        }

        /// <summary>房间列表摘要（大厅列表用）。</summary>
        public RoomListSnapshot List()
        {
            var snap = new RoomListSnapshot { MaxRooms = _maxRooms };
            foreach (var room in _rooms.Values) snap.Rooms.Add(room.ToBrief());
            return snap;
        }
    }
}
