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
        // ============ 常量【唯一真源】 ============
        // 全工程玩家卡尺寸只认这里：Arena.Layout.cs / CardUI.cs / StackEntryUI / DeckZone /
        // GraveyardZone / OnlineDeckZone 一律引用本常量，禁止再写 100/140 字面量。
        // 【P0·2026-10-18】100×140 → 140×196（14:10 → 10:14 比例不变，整体放大 40%）。
        public const float CARD_WIDTH = 140;
        public const float CARD_HEIGHT = 196;
        public const float HAND_SPACING = 20;
        // 场上/手牌/资产卡 ZIndex 基准：高于争夺区背景槽位(AreaSlot)，避免被背景遮挡。
        // 【2026-10】层号统一收口到 Script.UI.Layers，此处保留别名仅供既有调用点使用。
        public const int ZINDEX_CARD_BASE = Layers.CardBase;

        /// <summary>
        /// 取某张卡 UI 的**逻辑尺寸**。玩家卡走本类常量（P0 起 140×196），
        /// 秘社卡（`CouncilCardUI`，独立卡种、非 CardUI 子类）仍是 100×140。
        /// 排版函数一律用它，禁止直接写 CARD_WIDTH/CARD_HEIGHT —— 否则秘社卡会被撑大。
        /// </summary>
        public static Vector2 SizeOf(Control card)
        {
            // 三类卡尺寸**不同源**，不能一刀切：
            //  · 玩家卡/秘社卡 → `CardLayoutHelper` / `CouncilCardUI`（竖卡）
            //  · 地区卡 → `AreaCardUI`（横向卡，1.4:1，与玩家卡完全不同的比例）
            if (card is CouncilCardUI) return new Vector2(CouncilCardUI.CARD_WIDTH, CouncilCardUI.CARD_HEIGHT);
            if (card is AreaCardUI) return new Vector2(AreaCardUI.CARD_WIDTH, AreaCardUI.CARD_HEIGHT);
            return new Vector2(CARD_WIDTH, CARD_HEIGHT);
        }

        /// <summary>
        /// 【P4·2026-10-18】层叠平铺：资产区 / 分数区专用。每张只露 <paramref name="edge"/> 像素宽，
        /// 右卡压左卡（ZIndex 递增）；张数多到露边也放不下时**压缩露边**，绝不溢出容器。
        ///
        /// 为什么不再用 `LayoutCardsEvenly`：14 张资产按"并排 + 重叠"排会互相压到只剩一条缝且整体左偏，
        /// 玩家也读不出"有几张"；层叠让每张都能看见 36px 的边，数量一目了然（决策 4）。
        /// 悬停某张时由 `CardUI` 抬高其 ZIndex 即可看全卡面（不需要额外的浮层）。
        /// </summary>
        public static void LayoutStacked(Control container, System.Collections.Generic.List<Control> cards,
            float edge = 36f)
        {
            if (container == null || cards == null || cards.Count == 0) return;

            var valid = new System.Collections.Generic.List<Control>();
            foreach (var c in cards)
                if (c != null && GodotObject.IsInstanceValid(c)) valid.Add(c);
            if (valid.Count == 0) return;

            float containerWidth = container.Size.X;
            if (containerWidth <= 0) containerWidth = 400f;

            int n = valid.Count;
            Vector2 cardSize = SizeOf(valid[0]);
            float cardW = IsTapped(valid[0]) ? cardSize.Y : cardSize.X;

            if (n == 1)
            {
                PlaceCardInContainer(container, valid[0], (containerWidth - cardW) / 2f, 0);
                MarkStacked(valid[0]);
                return;
            }

            float usable = containerWidth - cardW;
            float step = usable > 0f ? Mathf.Min(edge, usable / (n - 1f)) : 0f;
            if (step < 0f) step = 0f;

            float total = cardW + step * (n - 1);
            float startX = Mathf.Max(0f, (containerWidth - total) / 2f);

            for (int i = 0; i < n; i++)
            {
                PlaceCardInContainer(container, valid[i], startX + i * step, i);
                MarkStacked(valid[i]);
            }
        }

        /// <summary>
        /// 【P4·决策 7·2026-10-18】对手手牌紧凑：把整把**牌背叠在容器中央的同一位置**（只露最上面一张）。
        ///
        /// 与 `LayoutStacked` 的唯一差别是 `step = 0`：紧凑态要的就是"一张牌背"的外观，
        /// 张数由 `CardCountBadge` 的「×N」角标交代；hover 展开时改调 `LayoutStacked(edge)`，
        /// 让每张露 36px 边，玩家能一眼数清张数（复用决策 4 的层叠机制）。
        ///
        /// **只给联机对手手牌用**：单机是同屏热座，对手手牌本来就正面可视，
        /// 压成一张牌背会直接废掉热座可玩性（用户 2026-10-18 明确口径：「只改联机端」）。
        /// </summary>
        public static void LayoutCompactBacks(Control container, System.Collections.Generic.List<Control> cards)
            => LayoutPile(container, cards);

        /// <summary>
        /// 【2026-10-19】把一组卡**叠成一摞**（全部落在容器中央的同一位置，只露最上面一张）。
        ///
        /// 与 `LayoutCompactBacks` 是同一份实现（后者保留为"对手手牌"的语义别名，避免既有调用点/测试改动）；
        /// 现在**资产区**也用它（用户口径：「资产区也可以支持折叠 + hover 展示卡」）——
        /// 张数由 `CardCountBadge` 的「×N」交代，悬停整块资产区弹出卡列表（`CardBadgePeek`）。
        /// </summary>
        public static void LayoutPile(Control container, System.Collections.Generic.List<Control> cards)
        {
            if (container == null || cards == null || cards.Count == 0) return;

            var valid = new System.Collections.Generic.List<Control>();
            foreach (var c in cards)
                if (c != null && GodotObject.IsInstanceValid(c)) valid.Add(c);
            if (valid.Count == 0) return;

            float containerWidth = container.Size.X;
            if (containerWidth <= 0) containerWidth = 400f;

            Vector2 cardSize = SizeOf(valid[0]);
            float startX = Mathf.Max(0f, (containerWidth - cardSize.X) / 2f);

            for (int i = 0; i < valid.Count; i++)
                PlaceCardInContainer(container, valid[i], startX, i);
        }

        private static void MarkStacked(Control card)
        {
            if (card is CardUI cui) cui.StackedMode = true;
        }

        /// <summary>
        /// 读取卡的横置状态（兼容 CardUI 与 CouncilCardUI）。
        /// </summary>
        public static bool IsTapped(Control card)
            => card is CardUI cui ? cui.IsTapped
             : card is CouncilCardUI cci ? cci.IsTapped
             : false;

        /// <summary>
        /// 在容器内均匀平铺一组卡（放得下居中，放不下压缩间距允许重叠，右卡压左卡）。
        /// 横置卡视觉宽=原高(CARD_HEIGHT)，竖置=原宽(CARD_WIDTH)，保证视觉一致。
        /// </summary>
        public static void LayoutCardsEvenly(Control container, System.Collections.Generic.List<Control> cards)
        {
            if (container == null || cards == null || cards.Count == 0) return;

            float containerWidth = container.Size.X;
            if (containerWidth <= 0) containerWidth = 1152;

            // 每张卡的视觉宽度（横置旋转后宽=卡高，竖置=卡宽；按卡种取尺寸）
            var visW = new float[cards.Count];
            for (int i = 0; i < cards.Count; i++)
            {
                var s = SizeOf(cards[i]);
                visW[i] = IsTapped(cards[i]) ? s.Y : s.X;
            }

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
        /// 横置模型：逻辑 Size 不变，整体旋转 90°（视觉 (卡高, 卡宽)）。
        /// </summary>
        public static void PlaceCardInContainer(Control container, Control card, float visualX, int z)
        {
            Vector2 cardSize = SizeOf(card);
            float cardW = cardSize.X, cardH = cardSize.Y;
            bool isTapped = IsTapped(card);
            float visW = isTapped ? cardH : cardW;
            float visH = isTapped ? cardW : cardH;

            // 视觉框中心（相对于容器）
            float visCenterX = visualX + visW / 2f;
            float visCenterY = container.Size.Y > visH
                ? container.Size.Y / 2f
                : visH / 2f;

            // 逻辑尺寸＝卡种尺寸，PivotOffset = 尺寸/2。
            // 【2026-09 修复】逻辑左上角 = 视觉中心 - PivotOffset（**不旋转**）：Godot 的旋转绕 PivotOffset 进行，
            // 旋转后视觉中心仍在 `Position + PivotOffset`（实测：Position=(0,0)/pivot=(50,70)/90° → 视觉中心 (50,70)，
            // 而 `pivot.Rotated(90°)` = (-70,50) ⇒ 原写法让**横置卡**整体偏移 (120,20)）。
            Vector2 pivot = cardSize / 2f;
            Vector2 localPos = new Vector2(visCenterX, visCenterY) - pivot;

            card.AnchorLeft = 0; card.AnchorRight = 0;
            card.AnchorTop = 0; card.AnchorBottom = 0;
            card.Scale = Vector2.One;
            // 【P3a】折叠成角标的卡重新摆到场上时必须恢复可见（联机同一套规则）
            card.Visible = true;
            // 【P4】默认非层叠；LayoutStacked 会在放置后再置回 true
            if (card is CardUI cuiStack) cuiStack.StackedMode = false;
            // 先调最小尺寸，再调 Size，防止被旧的最小尺寸限制
            card.CustomMinimumSize = cardSize;
            card.Size = cardSize;
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
