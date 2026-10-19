using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【对战界面区域背景 · 2026-10-20】把 `Cards/Image` 的素材铺到战场各区域。
    ///
    /// 设计口径（视觉规范）：
    ///  · **地区槽（争夺区）**：每个地区一张独立的宽幅场景图，上下半区取同一张图的**不同裁切窗口**
    ///    （否则上下两半一模一样，玩家分不清本方/对方）；对手半区压得更暗（0.52）以"后退"，
    ///    本方半区压得浅（0.30）以"突出"。半区再用**座位色边框**（座位1 红 / 座位2 蓝）标归属，
    ///    没有可用图时退回接近纯色的座位底色（见 `AreaSlot.ApplySeatFrame`）。
    ///  · **墓地**：`堆叠2`（持镰刀的死神）——语义直给，压暗 0.45 去抢戏。
    ///  · **堆叠区（后进先出结算栈）**：`堆叠2`，压暗 0.42。
    ///  · **秘社**：`堆叠1`（持十字的兜帽人），压暗 0.18，只做质感。
    ///  · **牌库 / 世界牌库**：`AreaCardBack`（卡背）当"一叠牌"的底，压暗 0.18。
    ///  · **手牌区**：`手牌`（1350×100 的皮革噪点）**铺满整块 960×290**（用户口径：这里可以放大），
    ///    但仍用等比铺满——噪点纹理放大不显形变，按矩形硬拉才会拉出方向性条纹。
    ///  · **资产区 / 分数区**：不铺图（避免和卡面抢），只给深色底 + 描边。
    ///
    /// 红线：**全部走 `BackdropHelper`（KeepAspectCovered = 等比铺满裁掉）**，任何区域都不许按矩形硬拉。
    /// 单机 `Arena` 与联机 `ArenaOl` 节点路径一致，故两端共用本类，避免各写一份漂移。
    /// </summary>
    public static class ArenaBackdrop
    {
        /// <summary>四个地区槽依次使用的场景图（氛围区分明显、且都不含强指向性主体）。</summary>
        private static readonly string[] AreaImages =
        {
            "res://Cards/Image/桌面3.jpg",   // 阴森庄园（暖黄窗光）
            "res://Cards/Image/桌面8.jpg",   // 月夜墓地（冷蓝、纵深强）
            "res://Cards/Image/桌面6.jpg",   // 工业厂区（逆光黄昏）
            "res://Cards/Image/桌面2.jpg",   // 废墟巨影（冷调 + 红光）
        };

        public static int AreaImageCount => AreaImages.Length;

        /// <summary>地区槽半区 480×200（比例 2.4）⇒ 原图上取 888×370 的窗口（888/370 = 2.4）。</summary>
        private static readonly Vector2 WindowSize = new Vector2(888f, 370f);

        /// <summary>对手半区取景窗口（靠左）。</summary>
        public static readonly Rect2 TopWindow = new Rect2(0f, 0f, WindowSize.X, WindowSize.Y);

        /// <summary>本方半区取景窗口（靠右）——与上半区取景不同，形成"两地"的空间感。</summary>
        public static readonly Rect2 BottomWindow = new Rect2(412f, 0f, WindowSize.X, WindowSize.Y);

        /// <summary>对手半区压暗值（更暗 = 后退）。</summary>
        public const float TopScrim = 0.52f;

        /// <summary>本方半区压暗值（更亮 = 前景）。</summary>
        public const float BottomScrim = 0.30f;

        /// <summary>取第 <paramref name="index"/> 个地区槽的场景图路径。</summary>
        public static string AreaImage(int index)
        {
            if (AreaImages.Length == 0) return null;
            int i = index % AreaImages.Length;
            if (i < 0) i += AreaImages.Length;
            return AreaImages[i];
        }

        /// <summary>
        /// 给整张战场铺区域背景。单机 `Arena` / 联机 `ArenaOl` 都在 `_Ready` 末尾调用一次即可。
        /// 所有节点都按路径软取（取不到就跳过），因此调用时机宽松、缺节点不会崩。
        /// </summary>
        public static void Apply(Node arena)
        {
            if (arena == null || !GodotObject.IsInstanceValid(arena)) return;

            // 战场里的 Panel 默认是 Godot 浅灰主题，与暗色卡牌风冲突 ⇒ 统一换成深色半透明 + 细描边。
            foreach (string path in new[]
            {
                "Player1Area", "Player2Area", "ActionPanel", "StackPanel",
                "Player1Area/ScoreZone", "Player2Area/ScoreZone",
                "Player1Area/DeckZone", "Player2Area/DeckZone",
                "Player1Area/GraveyardZone", "Player2Area/GraveyardZone",
                "CenterArea/WorldDeckZone",
            })
            {
                if (arena.GetNodeOrNull<Panel>(path) is Panel panel) UITheme.ApplyZonePanel(panel);
            }

            foreach (string side in new[] { "Player1Area", "Player2Area" })
            {
                ApplyHandStrip(arena, side);
                ApplyPile(arena, side, "DeckZone", null, 0.18f, useCardBack: true);
                ApplyPile(arena, side, "GraveyardZone", BackdropHelper.PileReaper, 0.45f, useCardBack: false);
                ApplyPile(arena, side, "CouncilContainer", BackdropHelper.PileSaint, 0.18f, useCardBack: false);
                ApplyFlatZone(arena, side, "AssetContainer", new Color(0.070f, 0.082f, 0.105f, 0.9f));
                ApplyFlatZone(arena, side, "ScoreZone", new Color(0.078f, 0.086f, 0.110f, 0.95f));
            }

            ApplyPile(arena, "CenterArea", "WorldDeckZone", null, 0.18f, useCardBack: true);
            ApplyStackPanel(arena);   // 堆叠区（后进先出）用「堆叠2」做底
        }

        /// <summary>
        /// 手牌区背景：铺满整个 960×290。
        ///
        /// 【2026-10-20 用户口径修正】上一版只把 `手牌.jpg` 取顶部 100px 做压边条，结果整片手牌区
        /// 大部分还是纯色、背景"偏小"。现改为铺满整块（素材 1350×100，等比放大到 290 高后横向溢出裁掉）：
        /// 用户明确允许这里拉伸，但**仍用等比铺满**而非按矩形硬拉——噪点/皮革纹理放大后不显形变，
        /// 而一旦按矩形硬拉，横向压缩 1.4 倍会把纹理拉出方向性条纹。
        /// </summary>
        private static void ApplyHandStrip(Node arena, string side)
        {
            var host = arena.GetNodeOrNull<Control>($"{side}/HandContainer");
            if (host == null || !GodotObject.IsInstanceValid(host)) return;

            BackdropHelper.EnsureFill(host, new Color(0.055f, 0.062f, 0.078f, 0.82f), "__HandFill");

            var strip = host.GetNodeOrNull<TextureRect>(BackdropHelper.BgNodeName);
            if (strip == null || !GodotObject.IsInstanceValid(strip))
            {
                strip = new TextureRect
                {
                    Name = BackdropHelper.BgNodeName,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                host.AddChild(strip);
                // 注意：**不能**再 MoveChild(strip, 0)——纯色托底 `__HandFill` 在 index 0，
                // 背景必须排在它之后（即画在它上面），否则会被托底盖住。
            }
            BackdropHelper.ApplyCoverWindow(strip, BackdropHelper.HandLeather, new Rect2(), 0.55f);
            strip.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            strip.OffsetLeft = 0f;
            strip.OffsetTop = 0f;
            strip.OffsetRight = 0f;
            strip.OffsetBottom = 0f;
        }

        /// <summary>堆叠区（`StackPanel`，「后进先出」结算栈）：`堆叠2`（持镰刀的死神）做底。</summary>
        private static void ApplyStackPanel(Node arena)
        {
            var host = arena.GetNodeOrNull<Control>("StackPanel");
            if (host == null || !GodotObject.IsInstanceValid(host)) return;

            var bg = host.GetNodeOrNull<TextureRect>(BackdropHelper.BgNodeName);
            if (bg == null || !GodotObject.IsInstanceValid(bg))
            {
                bg = new TextureRect
                {
                    Name = BackdropHelper.BgNodeName,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                host.AddChild(bg);
                host.MoveChild(bg, 0);   // 在 StackHeader / StackContainer 之下
            }
            BackdropHelper.ApplyCoverWindow(bg, BackdropHelper.PileReaper, new Rect2(), 0.42f);
        }

        /// <summary>牌库 / 墓地 / 秘社 / 世界牌库：小竖版或小横版底图，等比铺满裁切。</summary>
        private static void ApplyPile(Node arena, string side, string zoneNode, string resPath,
                                      float scrim, bool useCardBack)
        {
            var host = arena.GetNodeOrNull<Control>($"{side}/{zoneNode}") ?? arena.GetNodeOrNull<Control>(zoneNode);
            if (host == null || !GodotObject.IsInstanceValid(host)) return;

            string path = resPath;
            if (path == null && useCardBack) path = "res://Cards/Image/AreaCardBack.jpg";
            if (path == null) return;

            var bg = host.GetNodeOrNull<TextureRect>(BackdropHelper.BgNodeName);
            if (bg == null || !GodotObject.IsInstanceValid(bg))
            {
                bg = new TextureRect
                {
                    Name = BackdropHelper.BgNodeName,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                host.AddChild(bg);
                host.MoveChild(bg, 0);
            }
            BackdropHelper.ApplyCoverWindow(bg, path, new Rect2(), scrim);
        }

        /// <summary>资产区 / 分数区：只给深色底（不铺图，避免与卡面抢视觉）。</summary>
        private static void ApplyFlatZone(Node arena, string side, string zoneNode, Color color)
        {
            var host = arena.GetNodeOrNull<Control>($"{side}/{zoneNode}") ?? arena.GetNodeOrNull<Control>(zoneNode);
            if (host == null || !GodotObject.IsInstanceValid(host)) return;
            BackdropHelper.EnsureFill(host, color, "__ZoneFill");
        }
    }
}
