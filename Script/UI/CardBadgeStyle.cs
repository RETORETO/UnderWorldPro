using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【2026-10-20·卡面角标唯一风格源】卡面上所有"小标签"（封×N / 附×N / 指示物 / 费用 / 调战势 / 防 /
    /// 势力点 / 卡组·墓地张数 / 堆叠区标题 …）共用的字体样式：**暖金文字 + 4~5px 黑描边 + 居中**。
    ///
    /// 为什么单独抽一个类：`CardBadges`（封/附）与 `CardCountBadge`（×N）此前各自抄了一份字面量
    ///（`1,0.95,0.75` / 描边 4 / 描边 5），本轮又要给"数值标签"和"地区势力点"上同款样式，
    /// 若继续各抄一份，日后改配色就得改五处。
    ///
    /// 边距语义：本类只管**字体/配色/对齐**，不管位置（位置各有各的锚点，见各调用点）。
    /// Web 安全红线遵守：不使用 ToLower*/无参 StartsWith/EndsWith/Sort/OrdinalIgnoreCase/Char.IsDigit 等。
    /// </summary>
    public static class CardBadgeStyle
    {
        /// <summary>暖金主色（与 `CardBadges` 既有口径逐字一致，勿改）。</summary>
        public static readonly Color Gold = new Color(1f, 0.95f, 0.75f, 1f);

        /// <summary>描边色（黑，略透明 ⇒ 压在浅色卡面上也不发灰）。</summary>
        public static readonly Color Outline = new Color(0f, 0f, 0f, 0.9f);

        /// <summary>红方（座位 1）——与 `AreaSlot.Seat1Color` 同源色相，但为了让小字在任意卡图上可读而提亮。</summary>
        public static readonly Color RedSide = new Color(1f, 0.60f, 0.55f, 1f);

        /// <summary>蓝方（座位 2）——与 `AreaSlot.Seat2Color` 同源色相，同上提亮。</summary>
        public static readonly Color BlueSide = new Color(0.62f, 0.82f, 1f, 1f);

        /// <summary>角标基准字号（封/附、指示物、势力点、卡组张数共用）。</summary>
        public const int BadgeFontSize = 12;

        /// <summary>卡面数值字号（费用 / 调 / 战 / 势 / 防）：比角标略大，保证一眼读到。</summary>
        public const int ValueFontSize = 14;

        /// <summary>角标描边宽度。</summary>
        public const int BadgeOutline = 4;

        /// <summary>数值描边宽度（字号更大 ⇒ 描边同步加粗，否则细描边会被金字的亮部吃掉）。</summary>
        public const int ValueOutline = 5;

        /// <summary>
        /// 给一个 Label 套上"角标风格"。幂等（重复调用等价）。
        /// <paramref name="alignCenter"/> = false 时保留原水平对齐（如底部三连"调/战/势"需要左对齐）。
        /// </summary>
        public static void Apply(Label label, int fontSize = BadgeFontSize, int outline = BadgeOutline,
            Color? color = null, bool alignCenter = true)
        {
            if (label == null || !GodotObject.IsInstanceValid(label)) return;
            label.AddThemeFontSizeOverride("font_size", fontSize);
            label.AddThemeColorOverride("font_color", color ?? Gold);
            label.AddThemeColorOverride("font_outline_color", Outline);
            label.AddThemeConstantOverride("outline_size", outline);
            if (alignCenter) label.HorizontalAlignment = HorizontalAlignment.Center;
        }
        /// <summary>
        /// 牌库 / 墓地张数标签（"卡组×30" / "墓地×12"）：套封/附同款风格，并**抬到绝对层**
        /// `Layers.ZoneCountLabel`，确保不被容器里的卡背压住（用户实测"P2 侧数字被卡片挡住"）。
        /// 同时把标签左右偏移对称放宽（原占位只有 40px，12px 字号的"卡组×30"会被裁）。
        /// 幂等：已套过样式（有 font_size override）则直接返回。
        /// </summary>
        public static void ApplyZoneCountLabel(Label label)
        {
            if (label == null || !GodotObject.IsInstanceValid(label)) return;
            if (label.HasThemeFontSizeOverride("font_size")) return;

            Apply(label, BadgeFontSize, BadgeOutline);

            // 只在"水平居中锚点"的标签上放宽宽度（世界牌库等自有布局的不动）
            if (Mathf.IsEqualApprox(label.AnchorLeft, 0.5f) && Mathf.IsEqualApprox(label.AnchorRight, 0.5f))
            {
                label.OffsetLeft = -50f;
                label.OffsetRight = 50f;
            }

            label.ZAsRelative = false;
            label.ZIndex = Layers.ZoneCountLabel;
        }

        /// <summary>堆叠区标题（"堆叠区（后进先出）"）：与封/附同款金色+黑描边，字号略大。</summary>
        public static void ApplyZoneHeaderLabel(Label label)
        {
            if (label == null || !GodotObject.IsInstanceValid(label)) return;
            if (label.HasThemeFontSizeOverride("font_size")) return;
            Apply(label, 14, 5);
        }
    }
}
