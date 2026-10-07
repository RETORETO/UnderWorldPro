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
        // 场上/手牌/资产卡 ZIndex 基准：高于争夺区背景槽位(AreaSlot)，避免被背景遮挡。
        // 【2026-10】层号统一收口到 Script.UI.Layers，此处保留别名仅供既有调用点使用。
        public const int ZINDEX_CARD_BASE = Layers.CardBase;

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
        /// 【争夺区统一平铺·唯一真相源】单机 Arena 与联机 ArenaOl **必须**走这里，杜绝两侧序列口径漂移。
        ///
        /// 序列 = 争夺区角色卡（**全集，不做任何 IsAttached/IsSeal 过滤**）+ 地区附属/封印（AttachedTo/SealedBy 为 null）。
        /// 【2026-10 修复】联机曾用"遍历容器子节点 + !IsAttached && !IsSeal"取角色，会把
        /// 「形态角色」这类卡（EffectEngine.SyncFormZone → ZoneManager.MoveFormZone 迁入争夺区后
        /// **显式保持 IsAttached=true**，如 JC027"它仍然是附属"）从序列中剔除 ⇒ 该卡停留旧位、
        /// 且其它卡 index/视觉宽总和变化而整体位移（表现为横置卡错位、两张 JC027 完全重叠）。
        /// 单机口径为"争夺区 UI 全集"，此处以单机为准。
        /// </summary>
        /// <param name="container">争夺区容器（AreaSlot 的 Player1Area / Player2Area）。</param>
        /// <param name="areaZoneCards">争夺区桶内的卡 UI 全集（含形态角色）。</param>
        /// <param name="areaAttachSeals">结附/封印于该地区（无宿主卡）的附属与封印。</param>
        public static void LayoutAreaRow(Control container,
            System.Collections.Generic.List<Control> areaZoneCards,
            System.Collections.Generic.List<Control> areaAttachSeals)
        {
            if (container == null) return;

            var all = new System.Collections.Generic.List<Control>();
            if (areaZoneCards != null)
                foreach (var c in areaZoneCards)
                    if (c != null && GodotObject.IsInstanceValid(c)) all.Add(c);
            if (areaAttachSeals != null)
                foreach (var c in areaAttachSeals)
                    if (c != null && GodotObject.IsInstanceValid(c)) all.Add(c);

            LayoutCardsEvenly(container, all);
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

            // 逻辑尺寸固定 100x140，PivotOffset = (50,70)。
            // 【2026-09 修复】逻辑左上角 = 视觉中心 - PivotOffset（**不旋转**）：Godot 的旋转绕 PivotOffset 进行，
            // 旋转后视觉中心仍在 `Position + PivotOffset`（实测：Position=(0,0)/pivot=(50,70)/90° → 视觉中心 (50,70)，
            // 而 `pivot.Rotated(90°)` = (-70,50) ⇒ 原写法让**横置卡**整体偏移 (120,20)）。
            Vector2 pivot = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
            Vector2 localPos = new Vector2(visCenterX, visCenterY) - pivot;

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
            // 【2026-10 修复·横置卡偏移根因】平滑目标必须取"设置完 Size/Pivot/Rotation/Position 之后"的实时 GlobalPosition，
            // 不能再用 `container.GlobalPosition + localPos`：
            //   旋转 Control 的 transform origin = Position + pivot − R·pivot（scale=1），
            //   90° 时 pivot − R·pivot = (50,70) − (−70,50) = (120,20)
            //   ⇒ 横置卡 GlobalPosition = Position + (120,20)。旧算式少了该枢轴项，
            //   CardUI._Process 每帧 GlobalPosition.Lerp(_targetPosition) 会把横置卡稳定拉到
            //   `localPos − (120,20)` —— 这正是反复修不掉的"横置卡整体偏移"；
            //   而结附于它的附属用的是自锁写法 `TargetPosition = GlobalPosition`（不漂移）
            //   ⇒ 宿主漂移、附属不动 ⇒ 表现为"横置角色与它的附属性分离"。
            // 与 Arena.Layout 附属/封印定位（ui.TargetPosition = ui.GlobalPosition）完全同口径。
            SetSmoothTarget(card, container, localPos);
            // 场上/手牌/资产卡统一抬高 ZIndex 基准，确保不被争夺区背景(AreaSlot, ZIndex 0~6)遮挡
            card.ZIndex = ZINDEX_CARD_BASE + z;   // 索引越大越靠右越在上层，实现右卡压左卡
        }

        /// <summary>
        /// 设置卡的"平滑归位目标"（CardUI / CouncilCardUI 的 _Process 用 GlobalPosition.Lerp 逼近它）。
        /// 【2026-10】必须传刚排好版的 card，内部读其 **实时 GlobalPosition**（自动含旋转枢轴项），
        /// 从而保证横置/竖置两侧一致、不与自锁的附属/封印目标口径冲突。
        /// 尚未入树（无父节点）时 GlobalPosition 会退化为本地坐标，兜底回旧算式。
        /// </summary>
        public static void SetSmoothTarget(Control card, Control container, Vector2 localPosFallback)
        {
            if (card == null) return;
            Vector2 target = (card.GetParent() != null)
                ? card.GlobalPosition
                : (container != null ? container.GlobalPosition + localPosFallback : localPosFallback);
            if (card is CardUI cui) cui.TargetPosition = target;
            else if (card is CouncilCardUI cci) cci.TargetPosition = target;
        }
    }
}
