using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【P4·决策 7·2026-10-18】数量角标「×N」：**联机对手手牌紧凑**专用。
    ///
    /// 背景：联机对手手牌退化为「一张牌背」（`CardLayoutHelper.LayoutCompactBacks`），
    /// 张数无法再从排布里读出来 ⇒ 在代表牌背的**右上角**补一个「×N」角标；
    /// hover 整排时由 `ArenaOl` 改用 `LayoutStacked` 展开，每张露 36px 边可数。
    ///
    /// 与 `CardBadges`（封×N / 附×N）刻意分成两个类：
    ///  · 两者语义不同（封印/附属是**卡与卡的关系**，这里是**隐藏手牌的张数**）；
    ///  · 用户口径要求「封」「附」各自独立命中区、绝不混排，再往那张图上挂第三个可 hover 的
    ///    角标会让"到底展开了什么"变得含糊 —— 数量角标**只显示、不响应 hover**（`MouseFilter=Ignore`）。
    ///
    /// 层级：作为宿主牌的**子节点** + `ZAsRelative=true` + `ZIndex=2`（压过 `CardBadges` 的 1），
    /// 随宿主牌一起平移/旋转，宿主被别的卡压住时角标也一起被压住。
    /// </summary>
    public static class CardCountBadge
    {
        private const string RootName = "__CardCountBadge";
        private const int FontSize = 15;
        private const float BadgeW = 46f;
        private const float BadgeH = 22f;
        private const float Margin = 3f;

        /// <summary>
        /// 在 <paramref name="host"/> 上显示「×N」。<paramref name="count"/> ≤ 1 时直接移除
        /// （一张牌背不需要角标，避免遮挡牌面且不留空节点累积）。
        /// </summary>
        public static void Apply(Control host, int count)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;

            if (count <= 1)
            {
                Clear(host);
                return;
            }

            var label = host.GetNodeOrNull<Label>(RootName);
            if (label == null)
            {
                label = new Label
                {
                    Name = RootName,
                    MouseFilter = Control.MouseFilterEnum.Ignore,   // 只显示：不抢 hover、不吞点击
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    AnchorLeft = 1f, AnchorRight = 1f,             // 右上角贴齐宿主牌
                    AnchorTop = 0f, AnchorBottom = 0f,
                    OffsetLeft = -BadgeW - Margin,
                    OffsetRight = -Margin,
                    OffsetTop = Margin,
                    OffsetBottom = Margin + BadgeH,
                    ZAsRelative = true,
                    ZIndex = 2,
                };
                label.AddThemeFontSizeOverride("font_size", FontSize);
                label.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.75f));
                label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
                label.AddThemeConstantOverride("outline_size", 5);
                host.AddChild(label);
            }

            label.Text = $"×{count}";
        }

        /// <summary>移除数量角标（宿主卡没有角标时为空操作）。</summary>
        public static void Clear(Control host)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;
            var label = host.GetNodeOrNull<Label>(RootName);
            if (label == null) return;
            host.RemoveChild(label);
            label.QueueFree();
        }
    }
}
