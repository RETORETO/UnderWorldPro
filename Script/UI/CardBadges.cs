using System.Collections.Generic;
using Godot;
using Script.Game.Entities;

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
    /// 【2026-10-20·指示物（计数器）角标】新增"卡上指示物数量"行：
    ///  用户口径「指示物数量以类似封/附的方式显示，只显示数量没有 hover 逻辑，
    ///  三者同时存在时**指示物在最上方**，封/附保持现在的顺序（封在上、附在下）」。
    ///  故纵向行序恒为：**指示物(可多行，按传入顺序) → 封 → 附**，整列从卡面右下角**向上生长**。
    ///  指示物行 `MouseFilter=Ignore`（纯显示：不展开、不吞点击），与封/附的可 hover 行刻意区分。
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
        private const string SealName = "Seal";
        private const string AttachName = "Attach";
        private const string CounterNamePrefix = "Counter";

        /// <summary>悬浮某个角标（宿主卡 UI + 角标种类）。由战场视图注入（`Arena`/`ArenaOl`）。</summary>
        public static System.Action<Control, CardBadgeKind> OnBadgeHover;

        /// <summary>离开某个角标。视图通常延迟一小会儿再收起，避免鼠标在角标↔展开面板之间移动时闪断。</summary>
        public static System.Action<Control, CardBadgeKind> OnBadgeHoverEnd;

        /// <summary>
        /// 把卡上的指示物（`CardInstance.Counters`）转成角标文本，如 `"时间标志×2"`、`"锁定标志×1"`。
        /// 顺序＝字典枚举顺序（计数器的登记先后），数量 ≤ 0 的条目不显示。
        /// <para>抽成公共方法的原因：单机 `Arena.CollapseOneHost` 与联机 `ArenaOl.RefreshHostBadge`
        /// 必须用**同一份**文案生成，否则两端角标写法会漂。</para>
        /// </summary>
        public static List<string> BuildCounterTexts(CardInstance inst)
        {
            var list = new List<string>();
            if (inst?.Counters == null) return list;
            foreach (var kv in inst.Counters)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                list.Add($"{kv.Key}×{kv.Value}");
            }
            return list;
        }

        /// <summary>
        /// 更新宿主卡角标。所有行都为 0/空时移除角标节点（保持卡片干净，且不留空节点累积）。
        /// </summary>
        /// <param name="counterTexts">指示物角标文本（可空/可空列表）。按数组顺序从上往下排，全部排在封/附之上。</param>
        public static void Apply(Control host, int attached, int sealedCount,
            IReadOnlyList<string> counterTexts = null)
        {
            if (host == null || !GodotObject.IsInstanceValid(host)) return;

            // ---- 组装行（顺序即纵向顺序：指示物 → 封 → 附）----
            var names = new List<string>();
            var texts = new List<string>();
            var kinds = new List<CardBadgeKind?>();   // null = 指示物行（不可 hover）

            if (counterTexts != null)
            {
                int ci = 0;
                foreach (var t in counterTexts)
                {
                    if (string.IsNullOrEmpty(t)) continue;
                    names.Add(CounterNamePrefix + ci);
                    texts.Add(t);
                    kinds.Add(null);
                    ci++;
                }
            }
            if (sealedCount > 0) { names.Add(SealName); texts.Add($"封×{sealedCount}"); kinds.Add(CardBadgeKind.Seal); }
            if (attached > 0) { names.Add(AttachName); texts.Add($"附×{attached}"); kinds.Add(CardBadgeKind.Attach); }

            var root = host.GetNodeOrNull<Control>(RootName);
            if (names.Count == 0)
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
                    // 只做布局容器：hover 交给各标签自己处理（见类注释「角标分家」）
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ZAsRelative = true,
                    ZIndex = 1,                                   // 只压过宿主卡自身内容，不跨卡乱序
                    AnchorLeft = 1f, AnchorTop = 1f,
                    AnchorRight = 1f, AnchorBottom = 1f,
                    OffsetLeft = -BadgeW - Margin,
                    OffsetRight = -Margin,
                };
                host.AddChild(root);
            }

            // 行数变化 ⇒ 整列高度跟着变（自底向上生长）
            float rowsH = names.Count * BadgeH;
            root.OffsetTop = -rowsH - Margin;
            root.OffsetBottom = -Margin;

            // 清掉本轮不再需要的旧行（如指示物从 2 种减到 1 种、封印被清空）
            foreach (var child in root.GetChildren())
            {
                if (!(child is Label old)) continue;
                if (names.Contains(old.Name.ToString())) continue;
                root.RemoveChild(old);
                old.QueueFree();
            }

            for (int i = 0; i < names.Count; i++)
                SetRow(host, root, names[i], kinds[i], texts[i], i);
        }

        /// <summary>写一行角标。<paramref name="kind"/> = null 表示指示物行（纯显示，不接 hover）。</summary>
        private static void SetRow(Control host, Control root, string name, CardBadgeKind? kind, string text, int row)
        {
            var label = root.GetNodeOrNull<Label>(name);
            if (label == null)
            {
                label = new Label
                {
                    Name = name,
                    // 封印/附属：Pass 收 hover 但不吞点击（事件继续冒泡到宿主卡，拖拽/点选照常）；
                    // 指示物：Ignore —— 用户口径"只显示数量，没有 hover 逻辑"。
                    MouseFilter = kind.HasValue ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Ignore,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                label.AddThemeFontSizeOverride("font_size", FontSize);
                label.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.75f));
                label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
                label.AddThemeConstantOverride("outline_size", 4);

                if (kind.HasValue)
                {
                    // 每个标签是独立命中区 ⇒ 悬浮「封」只展开封印卡、悬浮「附」只展开附属卡
                    var capturedHost = host;
                    var capturedKind = kind.Value;
                    label.MouseEntered += () =>
                    {
                        if (GodotObject.IsInstanceValid(capturedHost)) OnBadgeHover?.Invoke(capturedHost, capturedKind);
                    };
                    label.MouseExited += () =>
                    {
                        if (GodotObject.IsInstanceValid(capturedHost)) OnBadgeHoverEnd?.Invoke(capturedHost, capturedKind);
                    };
                }
                root.AddChild(label);
            }

            label.Visible = true;
            label.Text = text;
            label.AnchorLeft = 0f; label.AnchorRight = 1f;
            label.OffsetLeft = 0f; label.OffsetRight = 0f;
            label.OffsetTop = row * BadgeH;
            label.OffsetBottom = label.OffsetTop + BadgeH;
        }
    }
}
