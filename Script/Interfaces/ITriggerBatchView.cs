using System;
using System.Collections.Generic;
using Script.Data.Effects;

namespace Script.Interfaces
{
    /// <summary>
    /// 【触发排序·批量入堆叠】可选视图能力。
    ///
    /// 为什么单独开一个接口：<see cref="ITriggerView.OpenTriggerOrderForPlayer"/> 在全项目有数十个实现
    /// （含全部测试桩），直接加参数会波及面过大。本接口**只由真实视图（TriggerInteractionCoordinator）实现**，
    /// Controller 用 `_view is ITriggerBatchView` 判定：未实现者（测试桩 / headless / 异常装配）
    /// 自动回退到既有单条路径，行为完全不变。
    /// </summary>
    public interface ITriggerBatchView
    {
        /// <summary>
        /// 注入「全部入堆叠」处理器（由 Controller 提供；调用方 ＝ 排序窗口上的「全部入堆叠」按钮）。
        /// handler(ordered, onFinished)：
        ///  · ordered    ＝ 排序窗口内**当前顺序（左→右）**的待入堆叠效果；
        ///  · onFinished ＝ **必被调用一次**：
        ///      true  → 已按"预选好的目标 + 原顺序"统一入堆叠；
        ///      false → 整体中止（玩家在预选目标时取消，**一条都没有入堆叠**）。
        /// </summary>
        void SetBatchEnterHandler(Action<List<StackEffectInstance>, Action<bool>> handler);
    }
}
