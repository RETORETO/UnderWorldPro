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
            var card = ui.Instance;
            if (card == null) { Hide(); return; }
            if (CanPreview == null || Overlay == null) return;         // 未接管：不弹（无副作用）
            if (!CanPreview(card))
            {
                // 不允许查看（对手手牌 / 对手暗藏者且无 JC057 授权）→ 不弹
                Hide();
                return;
            }
            Overlay.ShowFor(card, ui.GlobalPosition);
        }

        /// <summary>`CardUI` 鼠标离开时调用。</summary>
        public static void EndHover(CardUI ui)
        {
            if (Overlay == null) return;
            if (ui != null && Overlay.Current != null && !ReferenceEquals(Overlay.Current, ui.Instance)) return;
            Hide();
        }

        private static void Hide() => Overlay?.HidePreview();
    }
}
