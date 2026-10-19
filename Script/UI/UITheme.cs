using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【UI 主题统一 · 2026-10-19】收拢卡组编辑界面（DeckBuilder）的视觉语言，作为**非对战界面**
    /// （主菜单 / 单机房间 / 联机大厅 / 联机房间）的单一配色真源。
    ///
    /// 风格（源自 `DeckBuilder.GetPoolRowStyle` 与各文字色）：**深灰蓝底 + 金色标题 + 灰蓝描边**的暗色卡牌风。
    /// 所有配色 token 与便捷方法集中在此，界面代码只调方法、不抄字面量；日后改主题只动本文件。
    ///
    /// Web 安全红线遵守：不使用 ToLower*/无参 StartsWith/EndsWith/Sort/OrdinalIgnoreCase/Char.IsDigit 等。
    /// </summary>
    public static class UITheme
    {
        // ==================== 配色 token（唯一真源） ====================
        /// <summary>全屏深色底（与联机大厅 Background 同色）。</summary>
        public static readonly Color Background = new Color(0.09f, 0.10f, 0.12f, 1f);

        /// <summary>面板背景（深灰蓝，微透明）。</summary>
        public static readonly Color Panel = new Color(0.12f, 0.13f, 0.16f, 0.95f);

        /// <summary>面板边框（灰蓝）。</summary>
        public static readonly Color Border = new Color(0.34f, 0.35f, 0.42f, 0.9f);

        /// <summary>金色主文字 / 标题。</summary>
        public static readonly Color Gold = new Color(1f, 0.92f, 0.72f, 1f);

        /// <summary>金色（角标 / 强调，略亮）。</summary>
        public static readonly Color GoldBright = new Color(1f, 0.96f, 0.78f, 1f);

        /// <summary>次级灰蓝文字。</summary>
        public static readonly Color Text = new Color(0.75f, 0.75f, 0.80f, 1f);

        /// <summary>成功 / 就绪 / 已选。</summary>
        public static readonly Color Success = new Color(0.55f, 0.95f, 0.55f, 1f);

        /// <summary>危险 / 错误 / 未就绪。</summary>
        public static readonly Color Danger = new Color(1f, 0.5f, 0.5f, 1f);

        /// <summary>禁用态。</summary>
        public static readonly Color Disabled = new Color(0.5f, 0.5f, 0.55f, 1f);

        /// <summary>按钮 hover 底（略亮）。</summary>
        private static readonly Color HoverBg = new Color(0.18f, 0.19f, 0.24f, 1f);

        /// <summary>按钮 pressed 底（略暗）。</summary>
        private static readonly Color PressedBg = new Color(0.15f, 0.16f, 0.20f, 1f);

        // ==================== 便捷方法 ====================

        /// <summary>给 Panel 上深色面板 + 灰蓝边框 + 圆角。</summary>
        public static void ApplyPanel(Panel panel)
        {
            if (panel != null) panel.AddThemeStyleboxOverride("panel", PanelBox(ContentMargin: 12f));
        }

        /// <summary>给 PanelContainer 上同款面板样式（紧凑内边距）。</summary>
        public static void ApplyPanel(PanelContainer panel)
        {
            if (panel != null) panel.AddThemeStyleboxOverride("panel", PanelBox(ContentMargin: 8f));
        }

        /// <summary>标题标签：金色 + 指定字号（默认 30）。</summary>
        public static void ApplyTitle(Label label, int fontSize = 30)
        {
            if (label == null) return;
            label.AddThemeColorOverride("font_color", Gold);
            label.AddThemeFontSizeOverride("font_size", fontSize);
        }

        /// <summary>正文 / 次级文字：灰蓝。</summary>
        public static void ApplyBody(Label label)
        {
            if (label != null) label.AddThemeColorOverride("font_color", Text);
        }

        /// <summary>主按钮：深底 + 金色文字 + 灰蓝边框（开始 / 确认 / 连接 等）。</summary>
        public static void ApplyPrimaryButton(Button button)
        {
            if (button == null) return;
            button.AddThemeColorOverride("font_color", Gold);
            button.AddThemeColorOverride("font_hover_color", GoldBright);
            button.AddThemeColorOverride("font_pressed_color", Gold);
            button.AddThemeColorOverride("font_disabled_color", Disabled);
            button.AddThemeStyleboxOverride("normal", ButtonBox(Panel, Border));
            button.AddThemeStyleboxOverride("hover", ButtonBox(HoverBg, Border));
            button.AddThemeStyleboxOverride("pressed", ButtonBox(PressedBg, Border));
            button.AddThemeStyleboxOverride("disabled", ButtonBox(new Color(0.12f, 0.13f, 0.16f, 0.6f), new Color(0.25f, 0.26f, 0.32f, 0.6f)));
        }

        /// <summary>次按钮：灰蓝文字 + 灰蓝边框（返回 / 退出 / 取消 等）。</summary>
        public static void ApplySecondaryButton(Button button)
        {
            if (button == null) return;
            button.AddThemeColorOverride("font_color", Text);
            button.AddThemeColorOverride("font_hover_color", Gold);
            button.AddThemeColorOverride("font_pressed_color", Gold);
            button.AddThemeColorOverride("font_disabled_color", Disabled);
            button.AddThemeStyleboxOverride("normal", ButtonBox(Panel, Border));
            button.AddThemeStyleboxOverride("hover", ButtonBox(HoverBg, Border));
            button.AddThemeStyleboxOverride("pressed", ButtonBox(PressedBg, Border));
            button.AddThemeStyleboxOverride("disabled", ButtonBox(new Color(0.12f, 0.13f, 0.16f, 0.6f), new Color(0.25f, 0.26f, 0.32f, 0.6f)));
        }

        /// <summary>输入框：深底 + 灰蓝边框 + 金色文字。</summary>
        public static void ApplyLineEdit(LineEdit edit)
        {
            if (edit == null) return;
            edit.AddThemeColorOverride("font_color", Gold);
            edit.AddThemeColorOverride("font_placeholder_color", Text);
            edit.AddThemeStyleboxOverride("normal", ButtonBox(Panel, Border));
            edit.AddThemeStyleboxOverride("focus", ButtonBox(HoverBg, Gold));
        }

        /// <summary>
        /// 【2026-10-20 对战界面】给战场区域面板上「深色半透明 + 细描边 + 圆角」，替换 Godot 默认的浅灰 Panel。
        /// 与菜单面板同源，但透明度更低（要让底下的区域背景透出来）、圆角更大。
        /// </summary>
        public static void ApplyZonePanel(Panel panel, Color? bg = null, Color? border = null, int cornerRadius = 10)
        {
            if (panel == null) return;
            var sb = new StyleBoxFlat
            {
                BgColor = bg ?? new Color(0.06f, 0.07f, 0.09f, 0.62f),
                BorderColor = border ?? new Color(0.30f, 0.32f, 0.40f, 0.55f),
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(cornerRadius);
            sb.SetContentMarginAll(0f);
            panel.AddThemeStyleboxOverride("panel", sb);
        }

        /// <summary>全屏深色背景（供各界面根节点第一层摆放）。</summary>
        public static ColorRect BackgroundRect()
        {
            var rect = new ColorRect
            {
                Color = Background,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            return rect;
        }

        // ==================== 内部 StyleBox 工厂 ====================

        private static StyleBoxFlat PanelBox(float ContentMargin)
        {
            var sb = new StyleBoxFlat { BgColor = Panel, BorderColor = Border };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(8);
            sb.SetContentMarginAll(ContentMargin);
            return sb;
        }

        private static StyleBoxFlat ButtonBox(Color bg, Color border)
        {
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(6);
            sb.SetContentMarginAll(8);
            return sb;
        }
    }
}
