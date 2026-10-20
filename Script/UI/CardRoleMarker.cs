using Godot;
using Script.Enums;
using Script.Game.Entities;

namespace Script.UI
{
    /// <summary>
    /// 【2026-10-20·用户口径】**结附/封印于地区**的卡，在自己卡面「左下角」标出身份：
    /// 「附属」/「封印」。
    ///
    /// 为什么需要：
    ///  · 这类卡是 **cat-1**（场上可见可点），不折叠、不平铺进角标，而是与角色卡**混在同一行**里平铺；
    ///  · 本轮又把卡面的「类别」等提示性文字全部隐藏了 ⇒ 玩家再也分不清"这一行里哪张是附属、哪张是角色"。
    ///
    /// 为什么不能复用 `CardBadges`（封×N / 附×N）：
    ///  · 那个是"**我身上挂着几张**"的**计数**（宿主视角）；这个是"**我自己是什么**"的**身份标签**（卡片视角），
    ///    语义完全不同；
    ///  · 用户明确要求「UI 和角色卡的封/附区别开来」⇒ 这里改用**实心胶囊 + 彩色细边框 + 青色/紫色文字**，
    ///    位置在**左下角**（封/附在右下角），一眼可分。
    ///
    /// Web 安全红线遵守：不使用 ToLower*/无参 StartsWith/EndsWith/Sort/OrdinalIgnoreCase/Char.IsDigit 等。
    /// </summary>
    public static class CardRoleMarker
    {
        private const string RootName = "__CardRoleMarker";
        private const string LabelName = "Text";
        private const int FontSize = 10;
        private const float PillW = 34f;
        private const float PillH = 16f;
        private const float Margin = 2f;

        /// <summary>附属身份色（青绿）——与封印色、与封/附角标的暖金都不同。</summary>
        public static readonly Color AttachColor = new Color(0.45f, 0.95f, 0.85f, 1f);

        /// <summary>封印身份色（淡紫）。</summary>
        public static readonly Color SealColor = new Color(0.82f, 0.68f, 1f, 1f);

        /// <summary>
        /// 按卡实例的**区域身份**自动打/清标记：
        ///  · `AttachedZone` 且 `AttachedTo == null` ⇒ 「附属」（结附于地区）；
        ///  · `SealedZone` 且 `SealedBy == null` ⇒ 「封印」（封印于地区）；
        ///  · 其余（角色、结附于卡/秘社的、已离场）⇒ 清除。
        /// </summary>
        public static void ApplyFor(CardInstance inst, Control host)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;
            if (inst == null) { Clear(host); return; }

            if (inst.CurrentZone == CardZone.AttachedZone && inst.AttachedTo == null)
            {
                Apply(host, "附属", AttachColor);
                return;
            }
            if (inst.CurrentZone == CardZone.SealedZone && inst.SealedBy == null)
            {
                Apply(host, "封印", SealColor);
                return;
            }
            Clear(host);
        }

        /// <summary>在左下角画一个身份胶囊；重复调用只更新文本/颜色（不重建节点）。</summary>
        public static void Apply(Control host, string text, Color color)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;
            if (string.IsNullOrEmpty(text)) { Clear(host); return; }

            var pill = host.GetNodeOrNull<Panel>(RootName);
            if (pill == null)
            {
                pill = new Panel
                {
                    Name = RootName,
                    // 纯显示：不抢 hover、不吞点击（卡的拖拽/点选/双击照常）
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ZAsRelative = true,
                    ZIndex = 1,
                    AnchorLeft = 0f, AnchorTop = 1f,
                    AnchorRight = 0f, AnchorBottom = 1f,
                    OffsetLeft = Margin,
                    OffsetRight = Margin + PillW,
                    OffsetTop = -PillH - Margin,
                    OffsetBottom = -Margin,
                };
                host.AddChild(pill);

                var label = new Label
                {
                    Name = LabelName,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                label.AddThemeFontSizeOverride("font_size", FontSize);
                label.AddThemeConstantOverride("outline_size", 3);
                label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
                pill.AddChild(label);
            }

            var lbl = pill.GetNodeOrNull<Label>(LabelName);
            // 幂等短路：文本与边框色都没变就不重建 StyleBox（本方法在每次全量排版时都会被调用）
            var current = pill.GetThemeStylebox("panel") as StyleBoxFlat;
            if (lbl != null && string.Equals(lbl.Text, text, System.StringComparison.Ordinal)
                && current != null && current.BorderColor == color)
            {
                pill.Visible = true;
                return;
            }

            // 实心深底 + 彩色细边框 + 圆角 ⇒ 与"纯文字描边"的封/附角标明显不同
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.06f, 0.08f, 0.72f),
                BorderColor = color,
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(8);
            sb.SetContentMarginAll(0f);
            pill.AddThemeStyleboxOverride("panel", sb);

            if (lbl != null)
            {
                lbl.Text = text;
                lbl.AddThemeColorOverride("font_color", color);
            }

            pill.Visible = true;
        }

        /// <summary>移除标记（宿主卡没有标记时为空操作）。</summary>
        public static void Clear(Control host)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;
            var pill = host.GetNodeOrNull<Panel>(RootName);
            if (pill == null) return;
            host.RemoveChild(pill);
            pill.QueueFree();
        }
    }
}
