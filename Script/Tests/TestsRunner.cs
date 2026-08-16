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
                new JC001Tests(),               // JC001 通灵猫头鹰（静止不可结附/白眼先手调查授予/多源叠加/Reset保留）
                new RevealCovertTests(),        // 暗藏者现身（门禁/费用/翻面/进栈/结算回地区）
                new TargetLossTests(),          // 目标丢失规则（D16 丢失校验链）
                new TargetLossBusTests(),        // 目标丢失事件总线（发点收敛 + 过程记忆字段）
                new JC002TriggerChainTests(),    // JC002 完整进场触发链路 · 目标=自身时本地区丢失(规则⑥)
                new JC002003LostTargetTests(),   // JC002/JC003 丢失目标矩阵（离场/翻面/本地区/无视地区豁免）
                new CouncilActivateWindowTests(), // 秘社激活窗口弹出条件矩阵（玩家/优先权/先后手/堆叠/阶段）
                new JC012Tests(),                 // JC012 帷幕守望徽记（打出目标/激活目标/一次性减费）
                new JC015Tests(),                 // JC015 驱魔人（目标合法性矩阵/夺控换边判丢失）
                new JC030Tests(),                 // JC030 巨型蝙蝠（静止/条件持续/袭击1本地区与换地区丢失）
                new JC035Tests(),                 // JC035 川美魔咒（打出目标地区/触发条件/附属区生效）
                new JC060Tests(),                 // JC060 神秘学顾问（光环授予/撤销/源卡排除）
                new JC014Tests(),                 // JC014 公路骑士（持续公开手牌区/机动触发结构与目标）
                new JC068Tests(),                 // JC068 阿格斯IV（强制准备触发/持续封锁前提/横置解除/未结附不生效）
                new JC020Tests(),                 // JC020 隐蔽的飞刀（打出结附正面角色/两效果本地区限定/牺牲清结附关系）
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
