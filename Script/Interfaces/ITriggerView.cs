using System;
using System.Collections.Generic;
using Script.Data.Effects;

namespace Script.Interfaces
{
    /// <summary>
    /// 触发效果交互视图接口。用于将 TurnManager（Controller 层）与具体 UI（Arena）解耦：
    /// TurnManager 只依赖本接口，不直接引用 Arena 具体类型（MVC 解耦，呼应文档 M3 命令通道不引用 UI 的方向）。
    /// 接口方法签名与 Arena 的公开触发交互方法保持一致。
    /// 本接口定义在独立命名空间 Script.Interfaces（不依赖 Script.UI），避免 Controller 层反向引用 UI 程序集，
    /// 满足"Controller 无 UI 引用"的架构红线（C/S 预留）。依赖方向为：UI → 接口 ← Controller，二者均不直引 UI。
    /// </summary>
    public interface ITriggerView
    {
        /// <summary>打开触发排序窗口。ownerPlayerId 为排序归属玩家；triggers 为已过滤（含合法目标）的触发实例；
        /// onEnterStack 为入堆叠回调（已排定顺序），onDone 为本组排序完成回调。</summary>
        void OpenTriggerOrderForPlayer(int ownerPlayerId, System.Collections.Generic.List<Script.Data.Effects.TriggerEffectInstance> triggers, Action<System.Collections.Generic.List<Script.Data.Effects.TriggerEffectInstance>> onEnterStack, Action onDone);

        /// <summary>请求玩家为触发效果选择合法目标；onPicked(targetUid) 在选完后回调。</summary>
        void RequestTargetSelection(TriggerEffectInstance trigger, List<string> validTargetUids, Action<string> onPicked);

        /// <summary>触发实际入栈后调用：从排序窗口的 OrderZone 移除已入栈条目（取目标触发延后到选目标完成后调用）。</summary>
        void RemoveOrderEntries(List<TriggerEffectInstance> pushed);
    }
}
