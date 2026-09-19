using Godot;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 批次 6 收尾】**headless 专用服务端入口**（D1：服务端只有这一种形态）。
    ///
    /// 启动（示例）：
    ///   Godot_v4.7.1-stable_mono_win64_console.exe --headless --path &lt;工程&gt; res://Scenes/Net/NetServer.tscn \
    ///       -- --port 18950 --max-rooms 8 --max-conns 16 --verbose
    ///
    /// 本节点是**薄壳**：解析命令行 → 交 `LobbyServerHost` 装配（与 `NetEntry --server` 共用同一实现）。
    /// 也可直接双击工程目录下的 `启动大厅服务端.bat`（内含正确的 mono 版 Godot 路径与编译步骤）。
    /// </summary>
    [GlobalClass]
    public partial class NetServerEntry : Node
    {
        private LobbyServerHost _server;

        public override void _Ready()
        {
            var config = ServerConfig.FromArgs(OS.GetCmdlineUserArgs());
            _server = new LobbyServerHost { Name = "LobbyServerHost" };
            AddChild(_server);
            if (!_server.Start(config))
            {
                GD.PrintErr("[NetServer] 服务端启动失败（常见原因：端口被占用），进程即将退出");
                GetTree().Quit(1);
            }
        }
    }
}
