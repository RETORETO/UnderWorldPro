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
        /// <summary>
        /// 【幽灵连接·2026-10-20】**对局中**会话的失联回收阈值（秒；≤0 = 关闭）。
        ///
        /// 桌游一回合想几分钟很正常，所以刻意比 `SessionIdleTimeoutSeconds` 宽得多。
        /// 判定依据是"多久没收到任何上行"——新版客户端每 15s 一条心跳（`ClientSession.HeartbeatIntervalMs`），
        /// 因此**正常思考永远不会触发**；只有进程被杀/断网导致心跳彻底停止才会超时。
        /// 旧版客户端不发心跳，故只对 `HeartbeatSeen` 的会话启用（服务端先升级也不会误踢旧客户端）。
        /// </summary>
        public int MatchIdleTimeoutSeconds = 900;
        /// <summary>
        /// 对局结束后、在**战场页**展示获胜者的时长（秒）。到点后服务端自动复用原房间回到等待阶段。
        /// 期间房间停留在 `Finished`（客户端据此停在战场页展示结果），无需任何玩家操作。
        /// </summary>
        public int MatchResultDisplaySeconds = 5;
        /// <summary>对局结束后房间保留时长（秒；兜底销毁，正常流程下房间早已自动复用回等待阶段）。</summary>
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
                    case "--result-seconds": if (Next(out var rs) && int.TryParse(rs, out var resultSecs)) { cfg.MatchResultDisplaySeconds = resultSecs; i++; } break;
                    case "--finished-ttl": if (Next(out var ft) && int.TryParse(ft, out var ttl)) { cfg.FinishedRoomTtlSeconds = ttl; i++; } break;
                    case "--idle-timeout": if (Next(out var it) && int.TryParse(it, out var idle)) { cfg.SessionIdleTimeoutSeconds = idle; i++; } break;
                    case "--match-idle-timeout": if (Next(out var mit) && int.TryParse(mit, out var midle)) { cfg.MatchIdleTimeoutSeconds = midle; i++; } break;
                    case "--verbose": cfg.Verbose = true; break;
                    // 说明：`--ws` 曾是"改用 WebSocket 监听"的开关；传输层统一为 WebSocket 后已无意义（默认即是），
                    // 保留为静默接受的参数以兼容既有脚本与文档，无需任何处理。
                }
            }
            return cfg;
        }
    }
}
