using System.Collections.Generic;
using Godot;

namespace Script.Tests
{
    /// <summary>
    /// Godot 编辑器内一键运行测试：将该脚本挂到一个 Node 上，运行场景即可在输出面板看到结果。
    /// 按 P0→P5 优先级顺序执行各系统逻辑正确性测试。
    /// </summary>
    [GlobalClass]
    public partial class TestsRunner : Node
    {
        public override void _Ready()
        {
            var runners = new List<TestRunner>
            {
                new EffectSchemaTests(),        // P4
                new JCDataTests(),              // P5
                new TargetInterceptTests(),     // P0
                new EffectStackTests(),         // P1
                new ContinuousRegistryTests(),  // P2
                new ModStackTests(),            // P3
                new TargetLossTests(),          // 目标丢失规则（D16 丢失校验链）
                new TargetLossBusTests(),        // 目标丢失事件总线（发点收敛 + 过程记忆字段）
                new JC002TriggerChainTests(),    // JC002 完整进场触发链路 · 目标=自身时本地区丢失(规则⑥)
                new CouncilActivateWindowTests(), // 秘社激活窗口弹出条件矩阵（玩家/优先权/先后手/堆叠/阶段）
            };

            var redlines = new List<string>();
            var allOk = true;
            foreach (var r in runners)
            {
                r.RunAll();
                GD.Print(r.Report());
                if (!r.AllPassed) allOk = false;
            }

            if (redlines.Count > 0)
            {
                GD.Print("=== 已知红线（待后续系统支持，本次不计入失败） ===");
                foreach (var rl in redlines) GD.Print(rl);
            }

            GD.Print(allOk ? ">>> 全部逻辑正确性测试通过" : ">>> 存在失败用例，详见上方明细");
        }
    }
}
