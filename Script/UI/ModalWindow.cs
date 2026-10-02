namespace Script.UI
{
    /// <summary>
    /// 模态窗口类型枚举（MVC 修复 M-08 提取为共享类型）。
    /// 原定义于 Arena 内部（private enum），但 ModalWindowCoordinator / TriggerInteractionCoordinator
    /// 均需引用以驱动模态锁；提取为 Script.UI 下的公共枚举，供 Arena 与各协调器共用单一真相。
    /// </summary>
    public enum ModalWindow
    {
        Investigation,
        Strength,
        TriggerOrder,
        TieBreak,
        TargetSelection,  // 选目标窗口（JC014机动/JC012激活②/JC005事务卡 等触发/激活选目标）
        /// <summary>【2026-09·原子操作期间的玩家选择】通用"玩家决策窗"（选卡/数值/模式/多目标/
        /// 玩家目标/额外费用/激活代价/准备维护 等）。语义（用户口径）：**一个原子操作期间若需要玩家选择，
        /// 玩家选择完成之前阻塞进程**——此期间只有持续效果可以影响游戏，不得有新的行动/触发效果进入堆叠。
        /// 由 `Arena.LockChoiceWindow/UnlockChoiceWindow` 按窗口实例成对维护（可与其它模态类型嵌套计数）。</summary>
        ChoiceBox
    }
}
