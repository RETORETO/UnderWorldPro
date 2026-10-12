using Godot;

namespace Script.IO
{
    /// <summary>
    /// 【大厅/房间 UI · 增量 C】**联机客户端本地偏好**（持久化到 `user://online_client.cfg`）：
    ///  · 上次使用的卡组（进入房间时自动选中并提交）；
    ///  · 上次连接的服务端地址/端口/昵称（省得每次重输）。
    ///
    /// 存储位置用 Godot 的 `user://`（每用户独立，不污染工程目录）；文件小、字段少，用 `ConfigFile` 语义最清晰。
    /// 读写都做容错：文件不存在/字段缺失/损坏时一律退回默认值，绝不因偏好损坏挡住联机。
    /// </summary>
    public static class OnlineClientPrefs
    {
        private const string Path = "user://online_client.cfg";
        private const string Section = "online";

        /// <summary>上次使用的卡组全路径（`res://Decks/xxx.json`；无记录时为空）。</summary>
        public static string LastDeckPath { get; private set; } = "";
        /// <summary>上次连接的服务端地址。</summary>
        public static string LastHost { get; private set; } = "127.0.0.1";
        /// <summary>上次连接的端口。</summary>
        public static ushort LastPort { get; private set; } = 18950;
        /// <summary>上次使用的昵称。</summary>
        public static string LastName { get; private set; } = "玩家";

        private static bool _loaded;

        /// <summary>读取偏好（幂等；首次访问自动加载）。</summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            var cfg = new ConfigFile();
            if (cfg.Load(Path) != Error.Ok)
            {
                GD.Print("[OnlineClientPrefs] 无本地偏好文件，使用默认值（首次运行属正常）");
                return;
            }
            LastDeckPath = cfg.GetValue(Section, "last_deck_path", "").AsString();
            LastHost = cfg.GetValue(Section, "last_host", LastHost).AsString();
            LastPort = (ushort)(int)cfg.GetValue(Section, "last_port", (int)LastPort);
            LastName = cfg.GetValue(Section, "last_nick", LastName).AsString();
            // 【Web 安全】空白判定改用 ASCII 版（Windows 上空实现会经 Unicode 类别表，Web 上不可靠）。
            if (WebSafeText.IsNullOrWhiteSpaceAscii(LastHost)) LastHost = "127.0.0.1";
            if (LastPort == 0) LastPort = 18950;
            if (WebSafeText.IsNullOrWhiteSpaceAscii(LastName)) LastName = "玩家";
            GD.Print($"[OnlineClientPrefs] 已载入偏好：卡组={LastDeckPath}，服务端={LastHost}:{LastPort}，昵称={LastName}");
        }

        /// <summary>记录本次使用的卡组并落盘。</summary>
        public static void SaveDeck(string deckPath) { EnsureLoaded(); LastDeckPath = deckPath ?? ""; Save(); }

        /// <summary>记录连接信息（地址/端口/昵称）并落盘。</summary>
        public static void SaveConnection(string host, ushort port, string nick)
        {
            EnsureLoaded();
            LastHost = WebSafeText.IsNullOrWhiteSpaceAscii(host) ? "127.0.0.1" : WebSafeText.TrimAscii(host);
            LastPort = port == 0 ? (ushort)18950 : port;
            LastName = WebSafeText.IsNullOrWhiteSpaceAscii(nick) ? "玩家" : WebSafeText.TrimAscii(nick);
            Save();
        }

        private static void Save()
        {
            EnsureLoaded();
            // 【联机排查】整段包 try/catch：托管异常会打印出来；原生 abort 则捕不到（靠调用方的 k5/k5b 夹住）。
            GD.Print("[OnlineClientPrefs] s1 即将写盘 " + Path);
            try
            {
                var cfg = new ConfigFile();
                cfg.SetValue(Section, "last_deck_path", LastDeckPath);
                cfg.SetValue(Section, "last_host", LastHost);
                cfg.SetValue(Section, "last_port", (int)LastPort);
                cfg.SetValue(Section, "last_nick", LastName);
                var err = cfg.Save(Path);
                if (err != Error.Ok) GD.PrintErr($"[OnlineClientPrefs] 偏好写入失败：{err}");
                else GD.Print($"[OnlineClientPrefs] 偏好已保存：卡组={LastDeckPath}，服务端={LastHost}:{LastPort}，昵称={LastName}");
            }
            catch (System.Exception e)
            {
                GD.PrintErr("[OnlineClientPrefs] 写盘抛托管异常（已忽略）: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
}
