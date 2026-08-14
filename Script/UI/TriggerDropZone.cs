using Godot;

namespace Script.UI
{
    /// <summary>
    /// 触发排序窗口内的放置区域（入堆叠区 / 放弃区）。
    /// 拖拽与 drop 判定已由 TriggerOrderWindow 在根 _GuiInput 层统一接管（与 InvestigationWindow 架构一致，
    /// 避免 Godot 内置 _GetDragData 在本窗口 transform 异常下的命中失败），此处仅作为区域标识容器。
    /// </summary>
    [GlobalClass]
    public partial class TriggerDropZone : Control
    {
        /// <summary>区域类型：true=放弃区（DiscardZone），false=入堆叠区（OrderZone）。</summary>
        [Export] public bool IsDiscardZone = false;

        /// <summary>drop 内容变化后通知（由 TriggerOrderWindow 绑定，用于重新手动排布）。</summary>
        public System.Action OnContentsChanged;
    }
}
