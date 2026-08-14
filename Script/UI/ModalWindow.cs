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
        Battle,
        TriggerOrder,
        TieBreak
    }
}
