using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【2026-10-20·用户口径】弹窗居中的**唯一实现**（需求："所有弹出的弹窗、提示窗、选择窗都需要居中显示"）。
    ///
    /// 背景（踩坑记录）：原先各窗口自己写
    /// <code>
    /// var vp = GetViewport().GetVisibleRect().Size;
    /// Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)((vp.Y - Size.Y) / 2));
    /// </code>
    /// 这段代码**写在 `Window` 子类内部时是错的**：`Window` 本身就是一个 `Viewport`，
    /// 因此 `GetViewport()` 拿到的是**窗口自己**（`GetVisibleRect().Size` ＝ 自身尺寸）
    /// ⇒ 中心点算成 `(0, 0)`，窗口被丢到屏幕左上角。
    /// 在 `Arena` / `ArenaOl` / `ModalWindowCoordinator`（Control/Node）里写同样的代码才是对的
    /// （那时 `GetViewport()` 是根视口）——所以过去只有"从窗口类内部弹出"的那几个窗口不居中
    /// （实测：`TargetSelectionWindow`＝选目标/调度提示窗、`TriggerOrderWindow`、`OnlineTriggerOrderWindow`）。
    ///
    /// 现在统一走本类：**根视口**（`GetTree().Root`）尺寸算中心，窗口内部/外部调用都正确。
    /// 纪律：新增窗口一律 `WindowCenter.PopupCentered(win)`，禁止再手写居中算式。
    /// </summary>
    public static class WindowCenter
    {
        /// <summary>把窗口摆到根视口的正中（不改可见性）。锚点用窗口**当前** `Size`，故须在设过尺寸之后调用。</summary>
        public static void CenterInRoot(Window window)
        {
            if (window == null || !GodotObject.IsInstanceValid(window)) return;

            // 根视口：`Window` 内部调用时唯一可靠的参照（自身 `GetViewport()` 会返回自己）。
            var root = window.GetTree()?.Root;
            Vector2 vp = root != null ? root.GetVisibleRect().Size : new Vector2(1920f, 1080f);

            // 窗口比视口还大时按 0 处理（贴左上角），避免出现负坐标导致标题栏跑出屏幕外
            int x = Mathf.Max(0, (int)((vp.X - window.Size.X) / 2f));
            int y = Mathf.Max(0, (int)((vp.Y - window.Size.Y) / 2f));
            window.Position = new Vector2I(x, y);
        }

        /// <summary>
        /// 弹出并居中。**先 `Popup()` 再定位**：嵌入式子窗口的最终尺寸在显示时才落定
        /// （`wrap_controls` / 内容最小尺寸都可能改写 `Size`），先弹后算才不会算到旧尺寸。
        /// </summary>
        public static void PopupCentered(Window window)
        {
            if (window == null || !GodotObject.IsInstanceValid(window)) return;
            window.Popup();
            CenterInRoot(window);
        }
    }
}
