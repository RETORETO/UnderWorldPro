using Godot;

namespace Script.Tools
{
    /// <summary>
    /// 【卡组编辑器检索索引】headless 生成入口。
    ///
    /// 运行方式（与 <see cref="CardExportRunner"/> 同款）：
    ///   Godot_v4.7.x-stable_mono_win64_console.exe --headless --path . --script res://Script/Tools/CardIndexRunner.cs
    /// 产物：res://Cards/card_index.json
    ///
    /// 打包脚本（导出Web版.ps1 / 打包发布.ps1）会在导出前调用本入口并断言产物新鲜度。
    /// </summary>
    public partial class CardIndexRunner : SceneTree
    {
        public override void _Initialize()
        {
            CardIndexBuilder.BuildAndWrite(CardIndexBuilder.DefaultOutputPath);
            GD.Print("[CardIndexRunner] 索引生成完成，退出。");
            Quit(0);
        }
    }
}
