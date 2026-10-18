using Godot;

namespace Script.UI
{
    /// <summary>角标种类：封印 / 附属（**两个不同区域，绝不混在一起展示**）。</summary>
    public enum CardBadgeKind
    {
        Seal = 0,     // 封×N —— 封印于本卡
        Attach = 1,   // 附×N —— 结附于本卡
    }

    /// <summary>
    /// 【P3a/P3b·2026-10-18】宿主卡上的「附×N / 封×N」角标 + 悬浮展开入口。
    ///
    /// 背景：`UI美化与空间重排方案.md` 决策 3 —— cat-2（结附/封印于**卡或秘社**）不再把附属/封印
    /// 逐张偏移铺在宿主周围，改为宿主卡角标；hover 角标可原地展开成员列表（决策 3 的 hover 展开）。
    /// **cat-1（结附/封印于地区）不折叠**，继续由 `ArrangeAreaAttachmentsAndSeals` 在地区内平铺。
    ///
    /// 【2026-10-18 用户口径·角标分家】封印卡与附属卡来自**两个不同区域**，不得混在一个列表里：
    /// 每个角标标签各自是**独立的 hover 命中区**（`CardBadgeKind`），悬浮「封×N」只展开封印卡，
    /// 悬浮「附×N」只展开附属卡。因此：
    ///  · 根节点 `__CardBadges` 改为 `MouseFilter=Ignore`（只负责布局，不再自己收 hover）；
    ///  · 两个 `Label` 各自 `MouseFilter=Pass` + 各自的 `mouse_entered/exited`。
    ///
    /// 层级：角标作为宿主卡的**子节点**、`ZAsRelative=true` + `ZIndex=1` ⇒ 永远压在自己的卡面之上、
    /// 又跟随宿主卡与其它卡的相对层序（宿主被别的卡压住时角标也一起被压住）。
    /// 若改用绝对层 380~399，两张卡的角标会同层按树序互压 ⇒ "下面那张卡的角标浮在上面那张卡上"。
    ///
    /// 命中：角标区**只覆盖右下角一小块**（不是整张卡），标签 `MouseFilter=Pass` ⇒
    /// 既能收到 `mouse_entered/exited`，又不吞掉卡片的点击（事件继续冒泡到宿主卡）。
    /// </summary>
    public static class CardBadges
    {
        private const string RootName = "__CardBadges";
        private const int FontSize = 12;
        private const float BadgeW = 36f;
        private const float BadgeH = 18f;
        private const float Margin = 2f;

        /// <summary>悬浮某个角标（宿主卡 UI + 角标种类）。由战场视图注入（`Arena`/`ArenaOl`）。</summary>
        public static System.Action<Control, CardBadgeKind> OnBadgeHover;

        /// <summary>离开某个角标。视图通常延迟一小会儿再收起，避免鼠标在角标↔展开面板之间移动时闪断。</summary>
        public static System.Action<Control, CardBadgeKind> OnBadgeHoverEnd;

        /// <summary>
        /// 更新宿主卡角标。两张数量都为 0 时移除角标节点（保持卡片干净，且不留空节点累积）。
        /// </summary>
        public static void Apply(Control host, int attached, int sealedCount)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;

            var root = host.GetNodeOrNull<Control>(RootName);
            if (attached <= 0 && sealedCount <= 0)
            {
                if (root != null)
                {
                    host.RemoveChild(root);
                    root.QueueFree();
                }
                return;
            }

            if (root == null)
            {
                root = new Control
                {
                    Name = RootName,
                    // 只做布局容器：hover 交给两个标签各自处理（见类注释「角标分家」）
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ZAsRelative = true,
                    ZIndex = 1,                                   // 只压过宿主卡自身内容，不跨卡乱序
                    AnchorLeft = 1f, AnchorTop = 1f,
                    AnchorRight = 1f, AnchorBottom = 1f,
                    OffsetLeft = -BadgeW - Margin,
                    OffsetRight = -Margin,
                    OffsetTop = -BadgeH * 2f - Margin,
                    OffsetBottom = -Margin,
                };
                host.AddChild(root);
            }

            // row 0 = 最上（封印），row 1 = 最下（附属）
            SetOne(host, root, CardBadgeKind.Seal, sealedCount > 0 ? $"封×{sealedCount}" : null, 0);
            SetOne(host, root, CardBadgeKind.Attach, attached > 0 ? $"附×{attached}" : null, 1);
        }

        private static void SetOne(Control host, Control root, CardBadgeKind kind, string text, int row)
        {
            string name = kind == CardBadgeKind.Seal ? "Seal" : "Attach";
            var label = root.GetNodeOrNull<Label>(name);
            if (text == null)
            {
                if (label != null) label.Visible = false;
                return;
            }
            if (label == null)
            {
                label = new Label
                {
                    Name = name,
                    // Pass：收 hover，但不吞点击（事件继续冒泡到宿主卡，拖拽/点选照常）
                    MouseFilter = Control.MouseFilterEnum.Pass,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.AddThemeFontSizeOverride("font_size", FontSize);
                label.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.75f));
                label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
                label.AddThemeConstantOverride("outline_size", 4);

                // 每个标签是独立命中区 ⇒ 悬浮「封」只展开封印卡、悬浮「附」只展开附属卡
                var capturedHost = host;
                var capturedKind = kind;
                label.MouseEntered += () =>
                {
                    if (GodotObject.IsInstanceValid(capturedHost)) OnBadgeHover?.Invoke(capturedHost, capturedKind);
                };
                label.MouseExited += () =>
                {
                    if (GodotObject.IsInstanceValid(capturedHost)) OnBadgeHoverEnd?.Invoke(capturedHost, capturedKind);
                };
                root.AddChild(label);
            }

            label.Visible = true;
            label.MouseFilter = Control.MouseFilterEnum.Pass;
            label.Text = text;
            label.AnchorLeft = 0f; label.AnchorRight = 1f;
            label.OffsetLeft = 0f; label.OffsetRight = 0f;
            label.OffsetTop = row * BadgeH;
            label.OffsetBottom = label.OffsetTop + BadgeH;
        }
    }
}
