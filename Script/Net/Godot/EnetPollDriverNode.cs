using Godot;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【C/S 化 P3·模块A】ENet 轮询驱动节点。
    ///
    /// ENet 需要每帧主动 Poll 才能收发数据包与触发连接/断开信号。
    /// 将本节点挂到场景树（服务端或客户端主场景），并注入 GodotEnetTransport 实例，
    /// 即可在 _Process 中自动驱动 Poll。
    ///
    /// 用法（代码）：
    ///   var driver = new EnetPollDriverNode();
    ///   driver.Transport = myGodotEnetTransport;
    ///   GetTree().CurrentScene.AddChild(driver);
    /// 或在编辑器将该节点加入场景，通过 SetTransport 注入。
    /// </summary>
    [GlobalClass]
    public partial class EnetPollDriverNode : Node
    {
        /// <summary>待驱动的传输实例（需在 Start 之前注入）。</summary>
        public GodotEnetTransport Transport { get; set; }

        public override void _Process(double delta)
        {
            Transport?.Poll();
        }
    }
}
