using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【共享·纯几何排版工具】卡牌在容器内的均匀平铺 / 定位 / 横置旋转 / ZIndex。
    /// 由单机 Arena（事件驱动）与联机 ArenaOl（快照驱动）共用，确保两套战场视觉完全一致，
    /// 且驱动方式（事件 vs 快照）彼此隔离、互不污染。
    ///
    /// 本类只做"几何排版"，不依赖任何 Manager / ZoneManager / 数据源，入参为容器 + 卡 UI。
    /// </summary>
    public static class CardLayoutHelper
    {
        // ============ 常量（与 Arena.Layout.cs 一致，保持视觉统一） ============
        public const float CARD_WIDTH = 100;
        public const float CARD_HEIGHT = 140;
        public const float HAND_SPACING = 20;
        // 场上/手牌/资产卡 ZIndex 基准：高于争夺区背景槽位(AreaSlot, ZIndex 0~6)，避免被背景遮挡
        public const int ZINDEX_CARD_BASE = 100;

        /// <summary>读取卡的横置状态（兼容 CardUI 与 CouncilCardUI）。</summary>
        public static bool IsTapped(Control card)
            => card is CardUI cui ? cui.IsTapped
             : card is CouncilCardUI cci ? cci.IsTapped
             : false;

        /// <summary>
        /// 在容器内均匀平铺一组卡（放得下居中，放不下压缩间距允许重叠，右卡压左卡）。
        /// 横置卡视觉宽=原高(140)，竖置=原宽(100)，保证视觉一致。
        /// </summary>
        public static void LayoutCardsEvenly(Control container, System.Collections.Generic.List<Control> cards)
        {
            if (container == null || cards == null || cards.Count == 0) return;

            float containerWidth = container.Size.X;
            if (containerWidth <= 0) containerWidth = 1152;

            // 每张卡的视觉宽度（横置旋转后宽=原高=140, 竖=100）
            var visW = new float[cards.Count];
            for (int i = 0; i < cards.Count; i++)
                visW[i] = IsTapped(cards[i]) ? CARD_HEIGHT : CARD_WIDTH;

            int n = cards.Count;
            if (n == 1)
            {
                PlaceCardInContainer(container, cards[0], (containerWidth - visW[0]) / 2f, 0);
                return;
            }

            float idealTotal = 0;
            foreach (var w in visW) idealTotal += w;
            idealTotal += HAND_SPACING * (n - 1);

            float startX;
            float gap = HAND_SPACING;
            if (idealTotal <= containerWidth)
            {
                startX = (containerWidth - idealTotal) / 2f;   // 放得下：整体居中
            }
            else
            {
                // 放不下：压缩间距使总视觉宽恰好等于容器宽，绝不超出容器（允许重叠）
                startX = 0f;
                float sumVisW = 0;
                foreach (var w in visW) sumVisW += w;
                gap = (containerWidth - sumVisW) / (n - 1f);
            }

            float x = startX;
            for (int i = 0; i < n; i++)
            {
                // 每张卡视觉左缘 = x；横置卡视觉中心对齐到其视觉宽度中点
                PlaceCardInContainer(container, cards[i], x, i);
                x += visW[i] + gap;
            }
        }

        /// <summary>
        /// 单张卡放入容器：设置锚点/尺寸/枢轴/横置旋转/位置/ZIndex。
        /// 横置模型：逻辑 Size 保持 100x140，整体旋转 90°（视觉 140x100）。
        /// </summary>
        public static void PlaceCardInContainer(Control container, Control card, float visualX, int z)
        {
            bool isTapped = IsTapped(card);
            float visW = isTapped ? CARD_HEIGHT : CARD_WIDTH;
            float visH = isTapped ? CARD_WIDTH : CARD_HEIGHT;

            // 视觉框中心（相对于容器）
            float visCenterX = visualX + visW / 2f;
            float visCenterY = container.Size.Y > visH
                ? container.Size.Y / 2f
                : visH / 2f;

            // 逻辑尺寸固定 100x140，PivotOffset = (50,70)
            Vector2 pivot = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
            float angle = Mathf.DegToRad(isTapped ? 90f : 0f);
            Vector2 rotatedPivot = pivot.Rotated(angle);
            Vector2 localPos = new Vector2(visCenterX, visCenterY) - rotatedPivot;

            card.AnchorLeft = 0; card.AnchorRight = 0;
            card.AnchorTop = 0; card.AnchorBottom = 0;
            card.Scale = Vector2.One;
            // 先调最小尺寸，再调 Size，防止被旧的最小尺寸限制
            card.CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            card.Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            card.PivotOffset = pivot;
            card.RotationDegrees = isTapped ? 90f : 0f;
            card.Position = localPos;
            // TargetPosition 为平滑移动动画专用属性；玩家卡与秘社卡都实现了该属性，统一设置。
            if (card is CardUI cui)
                cui.TargetPosition = container.GlobalPosition + localPos;
            else if (card is CouncilCardUI cci)
                cci.TargetPosition = container.GlobalPosition + localPos;
            // 场上/手牌/资产卡统一抬高 ZIndex 基准，确保不被争夺区背景(AreaSlot, ZIndex 0~6)遮挡
            card.ZIndex = ZINDEX_CARD_BASE + z;   // 索引越大越靠右越在上层，实现右卡压左卡
        }
    }
}
