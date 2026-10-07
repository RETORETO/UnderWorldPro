using Godot;
using Script.Game.Entities;
using Script.Manager;

namespace Script.UI
{
    /// <summary>
    /// 【鼠标悬浮预览·总线】把"某个 `CardUI` 被鼠标悬浮"这件事，从 **CardUI**（散布在战场/手牌/牌库/
    /// 堆叠/调查窗等多处实例化，逐一订阅信号会碎成一地）**收口到一个注入点**：由当前视图
    /// （单机 `Arena` / 联机 `ArenaOl`）在就绪时 `Attach(判定, 浮层)` 一次即可全局生效。
    ///
    /// 分工（MVC 纪律）：
    ///  · **判定**＝由 View 传入，内部调用模型层单一真相源
    ///    `CovertInspectRule.CanViewerSeeCardFace(卡, 查看者)`（自己的卡/公开卡可看；对手手牌、对手暗藏者默认不可看；
    ///    **JC057 未横置且与被看暗藏者同地区**时授权可看）—— View 不重算规则；
    ///  · **浮层**＝`CardHoverPreview`（整张卡图的显示），只负责显示/隐藏。
    ///
    /// 未 `Attach`（例如没有战场视图的 headless/测试场景）→ 悬浮请求全部**静默忽略**（无副作用）。
    /// </summary>
    public static class CardPreviewBus
    {
        /// <summary>当前视图注入的"该卡能否预览"判定（查看者视角已由视图固定）；null＝未接管。</summary>
        public static System.Func<CardInstance, bool> CanPreview;

        /// <summary>当前视图注入的悬浮浮层（整张卡图）。</summary>
        public static CardHoverPreview Overlay;

        /// <summary>查看者玩家号（仅用于日志/诊断）。</summary>
        public static int ViewerPlayerId;

        /// <summary>
        /// 【2026-09·检视窗】"窗口内自带浮层"的节点名：**子窗口**（检视窗等）里挂一个同名 `CardHoverPreview`
        /// 子节点，悬浮该窗口内的卡时优先用它。原因（用户实测"检视窗内的卡悬浮UI偏离原本卡面有点远"）：
        ///  · 子窗口里的卡属于**窗口自己的视口**，`ui.GlobalPosition` 是窗口内容区坐标，
        ///    而主视口的浮层按主视口坐标定位 ⇒ 偏出整个窗口位置（几百像素）；
        ///  · 且 Godot 的 `Window` **永远渲染在所有 Control 之上** ⇒ 主视口浮层压在窗下，坐标修对也看不见。
        /// 找不到该子节点时**回退全局浮层**（战场/手牌等主视口场景行为逐字不变）。
        /// </summary>
        public const string WindowOverlayName = "CardHoverPreview";

        /// <summary>解析"该节点所属窗口自带的浮层"；没有则回退全局 `Overlay`。</summary>
        private static CardHoverPreview ResolveOverlay(Node node)
            => node?.GetWindow()?.GetNodeOrNull<CardHoverPreview>(WindowOverlayName) ?? Overlay;

        /// <summary>解析该浮层的"能否预览"判定：窗口自带浮层的专用判定优先，否则用总线全局判定。</summary>
        private static System.Func<CardInstance, bool> ResolvePredicate(CardHoverPreview overlay)
            => overlay?.CanPreviewOverride ?? CanPreview;

        /// <summary>由战场视图装配时调用（重复调用＝覆盖，后装配的视图生效）。</summary>
        public static void Attach(System.Func<CardInstance, bool> canPreview, CardHoverPreview overlay, int viewerPlayerId)
        {
            CanPreview = canPreview;
            Overlay = overlay;
            ViewerPlayerId = viewerPlayerId;
            GD.Print($"[CardPreviewBus] 悬浮预览已接管（查看者=玩家{viewerPlayerId}）");
        }

        /// <summary>视图离场时调用（`_ExitTree`），避免悬挂引用打到已释放节点。</summary>
        public static void Detach(CardHoverPreview overlay)
        {
            if (Overlay != overlay) return;
            CanPreview = null;
            Overlay = null;
            ViewerPlayerId = 0;
        }

        /// <summary>`CardUI` 鼠标进入时调用。</summary>
        public static void RequestHover(CardUI ui)
        {
            if (ui == null) return;
            RequestHoverCore(ui, ui.Instance, ui.GlobalPosition);
        }

        /// <summary>`CardUI` 鼠标离开时调用。</summary>
        public static void EndHover(CardUI ui)
        {
            var overlay = ResolveOverlay(ui);
            if (overlay == null) return;
            if (ui != null && overlay.Current != null && !ReferenceEquals(overlay.Current, ui.Instance)) return;
            overlay.HidePreview();
        }

        /// <summary>`CouncilCardUI`（秘社卡）鼠标进入时调用。判定与显示路径与 `CardUI` 完全一致。</summary>
        public static void RequestHover(CouncilCardUI ui)
        {
            if (ui == null) return;
            RequestHoverCore(ui, ui.Instance, ui.GlobalPosition);
        }

        /// <summary>`CouncilCardUI`（秘社卡）鼠标离开时调用。</summary>
        public static void EndHover(CouncilCardUI ui)
        {
            var overlay = ResolveOverlay(ui);
            if (overlay == null) return;
            if (ui != null && overlay.Current != null && !ReferenceEquals(overlay.Current, ui.Instance)) return;
            overlay.HidePreview();
        }

        /// <summary>公共悬浮请求：按注入判定决定是否弹出整张卡图浮层（玩家卡与秘社卡共用）。
        /// 【2026-09】浮层与判定都按"悬浮卡所在窗口"解析（子窗口自带浮层优先，否则回退全局）。</summary>
        private static void RequestHoverCore(Node sourceNode, CardInstance card, Vector2 anchorGlobalPos)
        {
            var overlay = ResolveOverlay(sourceNode);
            if (card == null) { overlay?.HidePreview(); return; }
            if (overlay == null) return;                                  // 未接管：不弹（无副作用）
            var predicate = ResolvePredicate(overlay);
            if (predicate == null) return;
            if (!predicate(card))
            {
                // 不允许查看（对手手牌 / 对手暗藏者且无 JC057 授权）→ 不弹
                overlay.HidePreview();
                return;
            }
            overlay.ShowFor(card, anchorGlobalPos);
        }
    }
}
