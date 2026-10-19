using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【对战界面区域背景 · 2026-10-20 第二版】把 `Cards/Image` 的素材铺到战场各区域。
    ///
    /// 本版按用户口径做了三处结构性调整：
    ///  1. **整块玩家区只有一张背景图**：`Player1Area` / `Player2Area`（1920×290 / 1920×250）各铺一张
    ///     场景图打底，手牌、牌库、墓地、秘社、资产、分数**不再各铺各的图**——上一版每块图取景与亮度
    ///     都不同，拼起来像补丁，且 `手牌.jpg`（1350×100 的极扁噪点）放大到 290 高后几乎只剩一片糊。
    ///  2. **各区域用「黑色半透明块」区分**：`Control` 类区域挂 `BackdropHelper.EnsureZoneMask`
    ///     （挂在区域 **index 0**，于是层序天然是 背景图 → 区域色块 → 卡片 UI / 其它游戏 UI），
    ///     `Panel` 类区域直接改自己的 stylebox（叠子节点会把 stylebox 的边框盖掉）。
    ///  3. **堆叠区改用 `牌手信息.jpg`**：300×900 的皮革面板（带缝线与压印 LOGO），目标是 200×540 的竖条，
    ///     比例 0.333 对 0.370，`KeepAspectCovered` 只裁掉上下各 3%，几乎是原图直出。
    ///
    /// 争夺区（`AreaSlot` 的上下半区）仍按地区取宽幅场景图 + 不同取景窗口，见 `AreaSlot.ApplyAreaBackdrop`。
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

        /// <summary>
        /// 整块玩家区的底图：`桌面.jpg`（1300×370 城市街景）。
        /// 铺到 1920×290（比例 6.62）时按宽度放大到 1920、高度变 546，上下各裁掉约 128px——
        /// 取的是画面中段（建筑立面 + 椰树），压暗 0.60 后只当"有质感的地毯"，不抢卡面。
        /// </summary>
        public const string PlayerZoneImage = "res://Cards/Image/桌面.jpg";

        /// <summary>玩家区底图的压暗值（区域遮罩之外再压一层，保证卡面可读）。</summary>
        public const float PlayerZoneScrim = 0.38f;

        /// <summary>区域遮罩色：纯黑半透明（用户口径「不同区域用黑色半透明颜色区分」）。</summary>
        public static readonly Color ZoneMaskBg = new Color(0f, 0f, 0f, 0.32f);

        /// <summary>区域遮罩描边：只为"能看出分区"，白 7% 足够，绝不抢卡面。</summary>
        public static readonly Color ZoneMaskBorder = new Color(1f, 1f, 1f, 0.07f);

        /// <summary>取第 <paramref name="index"/> 个地区槽的场景图路径。</summary>
        public static string AreaImage(int index)
        {
            if (AreaImages.Length == 0) return null;
            int i = index % AreaImages.Length;
            if (i < 0) i += AreaImages.Length;
            return AreaImages[i];
        }

        /// <summary>
        /// 给整张战场铺区域背景。单机 `Arena` / 联机 `ArenaOl` 都在 `_Ready` 里调用一次即可。
        /// 所有节点都按路径软取（取不到就跳过），因此调用时机宽松、缺节点不会崩。
        /// </summary>
        public static void Apply(Node arena)
        {
            if (arena == null || !GodotObject.IsInstanceValid(arena)) return;

            // ① 整块玩家区：只留一圈边框（底图随后铺在它上面，见 ③）。
            foreach (string side in new[] { "Player1Area", "Player2Area" })
            {
                if (arena.GetNodeOrNull<Panel>(side) is Panel playerZone)
                    UITheme.ApplyZonePanel(playerZone, new Color(0.04f, 0.05f, 0.07f, 0.55f),
                                           new Color(1f, 1f, 1f, 0.10f), 10);
            }

            // ② 区域内面板：统一「黑色半透明 + 淡描边」。这些是 `Panel`，底就是自己的 stylebox，
            //    不能再叠子节点（会把 stylebox 的 1px 边框盖掉）。
            foreach (string path in new[]
            {
                "ActionPanel", "StackPanel", "CenterArea/WorldDeckZone",
                "Player1Area/ScoreZone", "Player2Area/ScoreZone",
                "Player1Area/DeckZone", "Player2Area/DeckZone",
                "Player1Area/GraveyardZone", "Player2Area/GraveyardZone",
            })
            {
                if (arena.GetNodeOrNull<Panel>(path) is Panel panel)
                    UITheme.ApplyZonePanel(panel, ZoneMaskBg, ZoneMaskBorder, 8);
            }

            // ③ 玩家区整块底图 + 各 `Control` 类区域的黑色半透明遮罩。
            foreach (string side in new[] { "Player1Area", "Player2Area" })
            {
                ApplyPlayerZoneBackdrop(arena, side);

                foreach (string zone in new[] { "HandContainer", "AssetContainer", "CouncilContainer" })
                {
                    if (arena.GetNodeOrNull<Control>($"{side}/{zone}") is Control host)
                        BackdropHelper.EnsureZoneMask(host, ZoneMaskBg.A);   // 与 Panel 类区域同一档黑度
                }
            }

            // ④ 堆叠区（后进先出结算栈）：`牌手信息.jpg` 皮革面板。
            ApplyStackPanel(arena);

            // ⑤ 世界牌库：卡背当"一叠牌"的底（压暗 0.18，只做质感）。
            ApplyPile(arena, "CenterArea", "WorldDeckZone", null, 0.18f, useCardBack: true);
        }

        /// <summary>
        /// 给整块玩家区铺一张底图（`__Backdrop`，挂在区域 **index 0**，所以在所有子区域之下）。
        ///
        /// 内缩 1px：玩家区自身是 `Panel`，带 1px 描边；底图满矩形会把描边盖掉，
        /// 内缩 1px 后描边仍露在最外沿，区域边界不会"糊掉"。
        /// </summary>
        private static void ApplyPlayerZoneBackdrop(Node arena, string side)
        {
            var host = arena.GetNodeOrNull<Control>(side);
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
                host.AddChild(bg);
                host.MoveChild(bg, 0);   // 玩家区最底层：手牌/牌库/墓地/秘社/资产/分数全在它之上
            }

            BackdropHelper.ApplyCoverWindow(bg, PlayerZoneImage, new Rect2(), PlayerZoneScrim);

            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            bg.OffsetLeft = 1f;
            bg.OffsetTop = 1f;
            bg.OffsetRight = -1f;
            bg.OffsetBottom = -1f;
        }

        /// <summary>
        /// 堆叠区（`StackPanel`，「后进先出」结算栈）：`牌手信息.jpg` 皮革面板做底。
        ///
        /// 素材 300×900、目标 200×540：比例 0.333 vs 0.370，等比铺满只裁上下各约 3%，
        /// 缝线与底部「隐秘世界」压印 LOGO 基本完整保留。皮革本身够暗，压暗 0.25 即可。
        /// </summary>
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
                host.AddChild(bg);
                host.MoveChild(bg, 0);   // 在 StackHeader / StackContainer 之下
            }
            BackdropHelper.ApplyCoverWindow(bg, BackdropHelper.LeatherPanel, new Rect2(), 0.25f);
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            bg.OffsetLeft = 1f;
            bg.OffsetTop = 1f;
            bg.OffsetRight = -1f;
            bg.OffsetBottom = -1f;
        }

        /// <summary>牌库 / 世界牌库：卡背图当"一叠牌"的底，等比铺满裁切。</summary>
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
    }
}
