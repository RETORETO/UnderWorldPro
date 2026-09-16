using Godot;

namespace Script.Tools
{
    /// <summary>
    /// 【C/S 化 · M2】卡牌 JSON 导出运行入口。
    /// 供 headless 命令行运行：把全部 .tres 卡牌模板转成纯数据 JSON（卡牌模板数据协议，
    /// 供离线工具/未来纯托管侧复用；原消费方 `ServerCore/` 已于 2026-09-05 移除，导出工具保留）。
    ///
    /// 运行方式（Godot 4.7 mono）：
    ///   Godot_v4.7.1-stable_mono_win64_console.exe --headless --path . --script res://Script/Tools/CardExportRunner.cs
    /// 输出：res://Cards/card_templates.json
    /// </summary>
    public partial class CardExportRunner : SceneTree
    {
        public override void _Initialize()
        {
            CardTresToJson.ConvertAll("res://Cards/card_templates.json");
            GD.Print("[CardExportRunner] 导出完成，退出。");
            Quit(0);
        }
    }
}
