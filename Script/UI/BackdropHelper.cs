using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【区域背景 · 2026-10-20】把 `Cards/Image` 下的素材铺成界面/区域背景的**唯一入口**。
    ///
    /// 硬口径（用户要求）：**尺寸不合适时一律裁剪，不许拉伸**。
    /// 在 Godot 里对应 `TextureRect.stretch_mode = STRETCH_KEEP_ASPECT_COVERED(6)`：
    /// 等比放大到刚好铺满目标矩形，溢出的部分裁掉——画面比例永远不变形。
    /// 本类**禁止**使用默认的 `STRETCH_SCALE(0)`，任何调用点都不要自己 new TextureRect 再设 texture。
    ///
    /// 另外统一提供「压暗蒙版（scrim）」：背景图再好看也不能压过前景文字与卡牌，
    /// 故所有背景默认叠一层半透明黑，具体透明度由调用方按区域层级给定。
    /// </summary>
    public static class BackdropHelper
    {
        /// <summary>背景节点的固定名字（便于幂等：重复挂载时先删旧的）。</summary>
        public const string BgNodeName = "__Backdrop";

        /// <summary>蒙版节点名。</summary>
        public const string ScrimNodeName = "__Scrim";

        // ==================== 素材路径（Cards/Image 下的既有素材，集中在此便于换图） ====================

        /// <summary>宽幅场景图系列（1300×370，比例 3.514）：桌面/桌面1/2/3/4/6/7/8/9。</summary>
        public static readonly string[] SceneWide =
        {
            "res://Cards/Image/桌面.jpg",
            "res://Cards/Image/桌面1.jpg",
            "res://Cards/Image/桌面2.jpg",
            "res://Cards/Image/桌面3.jpg",
            "res://Cards/Image/桌面4.jpg",
            "res://Cards/Image/桌面6.jpg",
            "res://Cards/Image/桌面7.jpg",
            "res://Cards/Image/桌面8.jpg",
            "res://Cards/Image/桌面9.jpg",
        };

        /// <summary>窄竖条插画（90×365）：堆叠1 = 持十字的兜帽人；堆叠2 = 持镰刀的死神。</summary>
        public static readonly string PileSaint = "res://Cards/Image/堆叠1.jpg";
        public static readonly string PileReaper = "res://Cards/Image/堆叠2.jpg";

        /// <summary>极扁皮革噪点纹理（1350×100）：手牌区底纹。</summary>
        public static readonly string HandLeather = "res://Cards/Image/手牌.jpg";

        /// <summary>竖版皮革面板（300×900，带「隐秘世界」压印 LOGO）：菜单侧栏。</summary>
        public static readonly string LeatherPanel = "res://Cards/Image/牌手信息.jpg";

        // ==================== 挂载 ====================

        /// <summary>
        /// 在 <paramref name="host"/> 最底层铺一张**等比铺满裁切**的背景图。
        /// 返回新建的 `TextureRect`；素材不可用时返回 null（视为无背景，不影响功能）。
        /// </summary>
        /// <param name="scrimAlpha">压暗蒙版不透明度 0~1（0 = 不压暗）。文字密集处建议 0.45~0.65。</param>
        public static TextureRect AttachCover(Control host, string resPath, float scrimAlpha = 0f,
                                             TextureRect.StretchModeEnum mode = TextureRect.StretchModeEnum.KeepAspectCovered)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return null;

            // 幂等：同宿主重复调用时先清掉上一张，避免叠多层背景把画面越压越黑。
            var old = host.GetNodeOrNull<TextureRect>(BgNodeName);
            if (old != null && GodotObject.IsInstanceValid(old)) old.QueueFree();

            var tex = ResourceLoader.Load<Texture2D>(resPath);
            if (tex == null)
            {
                GD.PushWarning($"[Backdrop] 背景图加载失败（跳过，不拉伸兜底）：{resPath}");
                return null;
            }

            var rect = new TextureRect
            {
                Name = BgNodeName,
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = mode,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(rect);
            host.MoveChild(rect, 0);   // 最底层：必须在卡牌/文字之下

            if (scrimAlpha > 0f) AddScrim(rect, scrimAlpha);
            return rect;
        }

        /// <summary>铺一层纯色底 + 可选背景图（菜单类界面用：先底色，再图，再压暗）。</summary>
        public static TextureRect AttachCoverOver(Control host, Color baseColor, string resPath, float scrimAlpha = 0f)
        {
            var baseRect = UITheme.BackgroundRect();
            baseRect.Color = baseColor;
            host.AddChild(baseRect);
            host.MoveChild(baseRect, 0);
            return AttachCover(host, resPath, scrimAlpha);
        }

        /// <summary>压暗蒙版：叠在背景图之上的半透明黑（不拦截鼠标）。</summary>
        public static ColorRect AddScrim(Control parent, float alpha, Color? color = null)
        {
            if (parent == null || !GodotObject.IsInstanceValid(parent)) return null;
            var scrim = new ColorRect
            {
                Name = ScrimNodeName,
                Color = color ?? new Color(0f, 0f, 0f, alpha),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            if (color == null) scrim.Color = new Color(0f, 0f, 0f, alpha);
            scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            parent.AddChild(scrim);
            return scrim;
        }

        /// <summary>
        /// 给「已经是 TextureRect 的场景节点」套上等比裁切（例如 MainMenu 里预留的 Background 节点）。
        /// 会顺手清掉历史上误设的 `custom_maximum_size`——那个会把全屏背景压成 250×250。
        /// </summary>
        public static void MakeCover(TextureRect rect, string resPath, float scrimAlpha = 0f)
        {
            if (rect == null || !GodotObject.IsInstanceValid(rect)) return;
            ApplyCoverWindow(rect, resPath, new Rect2(), scrimAlpha);
        }

        /// <summary>
        /// 【2026-10-20 核心】给已存在的 `TextureRect` 套「图内裁切窗口 + 等比铺满裁切」。
        ///
        /// 只给一张 1300×370 的横版场景图，却要铺到 480×200 的横条上，且**同一张图要让上下两个半区
        /// 看起来是"不同的取景"**（否则上下两半一模一样，玩家分不清本方/对方）。
        /// 做法：用 `AtlasTexture.Region` 从原图上取两个 888×370 的窗口（288/370 = 2.4 = 480/200），
        /// 再由 `KeepAspectCovered` 铺满——全程零拉伸，纯裁切。
        /// </summary>
        /// <param name="region">原图上的取景窗口；传 `new Rect2()` 表示用整张图。</param>
        /// <returns>是否成功挂上背景图（false = 素材不可用，调用方应自行用纯色兜底）。</returns>
        public static bool ApplyCoverWindow(TextureRect rect, string resPath, Rect2 region, float scrimAlpha = 0f)
        {
            if (rect == null || !GodotObject.IsInstanceValid(rect)) return false;

            var tex = resPath == null ? null : ResourceLoader.Load<Texture2D>(resPath);
            if (tex == null) return false;   // 不清空原 texture，避免闪白；由调用方决定纯色兜底

            bool whole = region.Size.X <= 0f || region.Size.Y <= 0f;   // 空窗口 = 用整张图
            rect.Texture = whole
                ? tex
                : new AtlasTexture { Atlas = tex, Region = region };
            rect.CustomMinimumSize = Vector2.Zero;
            // 【2026-10-20 致命 bug 修复】这里原来把 custom_maximum_size 显式设成了 (0, 0)，
            // 但 Godot 4.7 里 (0,0) 不是"无上限"，而是**把尺寸钳成 0×0**
            // （实测：父 480×200、锚点 FullRect 的 TextureRect，设 (0,0) 后 size 恒为 0）。
            // 后果是凡走本方法的背景（争夺区半区、手牌区底纹）**一个像素都不渲染**，
            // 表现为"争夺区背景完全没生效"。用 (-1,-1) 才是"无上限"，同时仍能清掉历史上
            // MainMenu 那种误设的 250×250 上限。
            rect.CustomMaximumSize = new Vector2(-1f, -1f);
            rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;   // 等比铺满、溢出裁掉
            rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            rect.Visible = true;

            if (scrimAlpha > 0f)
            {
                var old = rect.GetNodeOrNull<ColorRect>(ScrimNodeName);
                if (old != null && GodotObject.IsInstanceValid(old))
                    old.Color = new Color(0f, 0f, 0f, scrimAlpha);
                else
                    AddScrim(rect, scrimAlpha);
            }
            return true;
        }

        /// <summary>给「纯色底」的区域补一层半透明色块（不拦截鼠标）；重复调用只更新颜色。</summary>
        public static ColorRect EnsureFill(Control host, Color color, string nodeName = "__Fill")
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return null;
            var old = host.GetNodeOrNull<ColorRect>(nodeName);
            if (old != null && GodotObject.IsInstanceValid(old))
            {
                old.Color = color;
                return old;
            }
            var rect = new ColorRect { Name = nodeName, Color = color, MouseFilter = Control.MouseFilterEnum.Ignore };
            rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(rect);
            host.MoveChild(rect, 0);
            return rect;
        }

        /// <summary>区域遮罩节点的固定名字（幂等）。</summary>
        public const string ZoneMaskNodeName = "__ZoneMask";

        /// <summary>
        /// 【2026-10-20 用户口径】整块玩家区铺**一张**背景图，各功能区域用「黑色半透明块」区分，
        /// 而不是每个区域各铺一张图（那样每块图的取景、亮度都不一样，拼起来像补丁）。
        ///
        /// 本方法给 `Control` 类区域（`HandContainer`/`AssetContainer`/`CouncilContainer`）挂遮罩：
        /// **黑色半透明 + 1px 极淡描边**，挂在区域**最底层**（index 0），
        /// 于是天然满足「区域色在卡片 UI 之下、在背景图之上」的层序要求。
        ///
        /// `Panel` 类区域（`DeckZone`/`GraveyardZone`/`ScoreZone`）不用本方法——
        /// 它们的底就是自己的 stylebox，再叠一层会把 stylebox 的边框盖掉，改走 `UITheme.ApplyZonePanel`。
        /// </summary>
        public static Panel EnsureZoneMask(Control host, float alpha = 0.42f)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return null;

            var mask = host.GetNodeOrNull<Panel>(ZoneMaskNodeName);
            if (mask != null && GodotObject.IsInstanceValid(mask))
            {
                mask.Visible = true;
                ApplyZoneMaskStyle(mask, alpha);
                return mask;
            }

            mask = new Panel { Name = ZoneMaskNodeName, MouseFilter = Control.MouseFilterEnum.Ignore };
            mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            ApplyZoneMaskStyle(mask, alpha);
            host.AddChild(mask);
            host.MoveChild(mask, 0);   // 区域最底层：卡片、标签一律压在它上面
            return mask;
        }

        /// <summary>遮罩外观：黑半透明 + 极淡描边（描边只为"分区"，不能抢卡面）。</summary>
        public static void ApplyZoneMaskStyle(Panel mask, float alpha = 0.42f)
        {
            if (mask == null || !GodotObject.IsInstanceValid(mask)) return;
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0f, 0f, 0f, alpha),
                BorderColor = new Color(1f, 1f, 1f, 0.07f),
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(8);
            sb.SetContentMarginAll(0f);
            mask.AddThemeStyleboxOverride("panel", sb);
        }
    }
}
