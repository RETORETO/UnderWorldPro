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
                new JC005Tests(),                 // JC005 裂解术（取目标阶段【心】判定/三种附属靶标/结算复检跳过/事务卡聚合）
                new NetCSTests(),                  // C/S 化 P0/P1 内核验证（异步输入链路 + 快照序列化往返）
                new NetP2Tests(),                  // C/S 化 P2 端到端验证（NetworkInputGateway + LoopbackTransport）
                new NetGodotTests(),               // C/S 化 P3·模块A 真实 ENet 联机回环验证
                new NetP3CTests(),                 // C/S 化 P3·模块C 意图指令通道（DTO序列化 + 服务端权威校验 + 回环闭环）
                new NetP3BTests(),                 // C/S 化 P3·模块B 客户端渲染切换（快照差异引擎 + 渲染驱动路由）
                new NetP3ETests(),                 // C/S 化 P3·模块E 断线重连（可复原PRNG + 随机源Hub + 重连管线）
                new NetP1Batch1Tests(),            // C/S 化·第一批迁移三方验证（Loopback端到端 / 单机回归 / Enet真实联机）
                new NetP1Batch1MatchTests(),        // C/S 化·第一批迁移端到端联机（NetworkMatchLauncher 回环 + 客户端投影一致）
                new NetCrossProcessSimTests(),      // 【临时】伪跨进程 + ResolveInstance 从模板重建验证（跨进程联机核心点，验证后移除）
                new OnlineEffectChainTests(),        // 联机效果系统接入：客户端可发动判定 / 费用修正快照 / 服务端目标校验
                new NetPhaseBTests(),                // Phase B：信息隔离（按玩家过滤快照 + 占位实例）/ 信封 v2 / 决策超时按 Kind
                new NetChoicePanelTests(),           // 第 3 版：触发排序顺序应用/强制忽略取消、PayOrTap PayAllowed、蒙版资格、决策窗口场景
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
