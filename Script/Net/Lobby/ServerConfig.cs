namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 批次 1】服务端配置（D8 默认值；可用命令行覆盖）。
    ///
    /// 覆盖顺序：命令行参数 ＞ 本类默认值（暂无配置文件，避免引入额外格式与解析负担）。
    /// </summary>
    public class ServerConfig
    {
        /// <summary>监听端口。</summary>
        public ushort Port = 18950;
        /// <summary>最大同时连接数（大厅要容纳"多人等待 + 场内多人"，故远大于 2）。</summary>
        public int MaxConnections = 16;
        /// <summary>最大房间数（一个服务端进程可承载多场并发，见设计方案 §十一）。</summary>
        public int MaxRooms = 8;
        /// <summary>协议版本（与 `LobbyProtocol.ProtocolVersion` 比对）。</summary>
        public int ProtocolVersion = LobbyProtocol.ProtocolVersion;
        /// <summary>昵称长度上限（字符数）。</summary>
        public int NameMaxLen = 12;
        /// <summary>空房间（无人）立即销毁；此值用于回收"**不在任何房间**且长时间无消息"的会话（秒；≤0 = 关闭该机制）。</summary>
        public int SessionIdleTimeoutSeconds = 300;
        /// <summary>对局结束后房间保留时长（秒）。</summary>
        public int FinishedRoomTtlSeconds = 600;
        /// <summary>是否打印详细日志。</summary>
        public bool Verbose;

        /// <summary>从命令行参数解析（形如 `--port 18950 --max-rooms 8`）；无法识别的参数忽略（不影响 Godot 自带参数）。</summary>
        public static ServerConfig FromArgs(string[] args, ServerConfig baseline = null)
        {
            var cfg = baseline ?? new ServerConfig();
            if (args == null) return cfg;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                bool Next(out string v) { v = (i + 1 < args.Length) ? args[i + 1] : null; return v != null; }
                switch (a)
                {
                    case "--port": if (Next(out var p) && ushort.TryParse(p, out var port)) { cfg.Port = port; i++; } break;
                    case "--max-conns": if (Next(out var mc) && int.TryParse(mc, out var maxConns)) { cfg.MaxConnections = maxConns; i++; } break;
                    case "--max-rooms": if (Next(out var mr) && int.TryParse(mr, out var maxRooms)) { cfg.MaxRooms = maxRooms; i++; } break;
                    case "--name-max": if (Next(out var nm) && int.TryParse(nm, out var nameMax)) { cfg.NameMaxLen = nameMax; i++; } break;
                    case "--finished-ttl": if (Next(out var ft) && int.TryParse(ft, out var ttl)) { cfg.FinishedRoomTtlSeconds = ttl; i++; } break;
                    case "--idle-timeout": if (Next(out var it) && int.TryParse(it, out var idle)) { cfg.SessionIdleTimeoutSeconds = idle; i++; } break;
                    case "--verbose": cfg.Verbose = true; break;
                }
            }
            return cfg;
        }
    }
}
