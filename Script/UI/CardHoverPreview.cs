using Godot;
using Script.Game.Entities;
using Script.Manager;

namespace Script.UI
{
    /// <summary>
    /// 【鼠标悬浮预览】整张卡图的悬浮浮层（View 层，用户 2026-09 口径）。
    ///
    /// 用途：单机/联机战场里，**鼠标悬浮到一张卡上时弹出一个浮层显示这张卡的整张卡图**
    /// （手牌卡面较小，卡图细节看不清）。浮层**不吃鼠标事件**（`MouseFilter=Ignore`）、
    /// 定位在被悬浮卡的一侧并自动钳制在视口内。
    ///
    /// 是否允许显示由 **View 注入的判定**决定（`CardPreviewBus.Attach` 时给定的委托），
    /// 判定本体＝模型层单一真相源 `CovertInspectRule.CanViewerSeeCardFace`
    /// （自己的卡一律可看；对手手牌/对手暗藏者默认不可看；**JC057 未横置且同地区**时授权可看）
    /// —— 本类只做"显示/隐藏"，不含任何规则判断（View 不写 Model、也不重算规则）。
    ///
    /// 【2026-10-16 计划·未实施】按 `UI美化与空间重排方案.md` 决策 3/4/5，本浮层将**扩展**为可承载
    /// **多卡平铺 + 点击回传**（宿主卡角标 hover 展开附属/封印列表、资产/分数区层叠列表展开），
    /// 并受"**任一时刻只允许一个浮层**，角标/层叠列表浮层优先于整卡预览"的互斥规则约束。
    /// **当前实现仍是单张整卡图**；改造前请先读该方案第五章（触发源互斥）与决策 3/4。
    /// </summary>
    public partial class CardHoverPreview : Control
    {
        private const float PREVIEW_WIDTH = 260f;      // 卡图放大显示（基础卡面为 100×140）
        private const float PREVIEW_HEIGHT = 364f;

        private PanelContainer _frame;
        private TextureRect _image;
        private Label _caption;
        private CardInstance _current;

        /// <summary>当前正在预览的卡（没有则为 null）。</summary>
        public CardInstance Current => _current;

        /// <summary>预览层是否可见（测试/诊断用）。</summary>
        public bool IsShowing => _frame != null && _frame.Visible;

        /// <summary>
        /// 【2026-09·检视窗】本浮层的**专用**"能否预览"判定（非空时优先于 `CardPreviewBus` 的全局判定）。
        /// 用途：检视窗里列出的卡**已经由效果授权**给检视者（可能属于"默认不可看"的区域——对手手牌、
        /// 对手暗藏者），故窗口自带的浮层用"本窗列出的卡一律可看"作为判定。
        /// </summary>
        public System.Func<CardInstance, bool> CanPreviewOverride;

        /// <summary>
        /// 【2026-09】浮层缩放（默认 1＝战场里的原尺寸 260×364）。子窗口（如检视窗）空间有限时按比例缩小，
        /// 避免浮层被窗口边界裁掉（浮层是窗口的子节点，超出窗口的部分会被裁）。
        /// </summary>
        public float PreviewScale = 1f;

        private float ScaledWidth => PREVIEW_WIDTH * (PreviewScale > 0f ? PreviewScale : 1f);
        private float ScaledHeight => PREVIEW_HEIGHT * (PreviewScale > 0f ? PreviewScale : 1f);

        public override void _Ready()
        {
            // 浮层自身不吃鼠标事件（否则会挡住底下的卡，导致悬浮抖动/点不到牌）
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            Visible = false;

            _frame = new PanelContainer();
            _frame.MouseFilter = MouseFilterEnum.Ignore;
            _frame.CustomMinimumSize = new Vector2(ScaledWidth, ScaledHeight);
            _frame.Size = new Vector2(ScaledWidth, ScaledHeight);
            AddChild(_frame);

            var box = new VBoxContainer();
            box.MouseFilter = MouseFilterEnum.Ignore;
            _frame.AddChild(box);

            _image = new TextureRect
            {
                MouseFilter = MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(ScaledWidth - 8f, ScaledHeight - 30f),
            };
            box.AddChild(_image);

            _caption = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
            box.AddChild(_caption);
        }

        /// <summary>显示某张卡的整张卡图（调用方需已确认"允许查看"）。
        /// 兼容玩家卡（PlayerTemplate）与秘社卡（CouncilTemplate）：秘社卡卡图路径用
        /// CouncilCardData.CardImagePath（非 PlayerCards 目录），卡名取 CouncilTemplate.CardName。</summary>
        public void ShowFor(CardInstance card, Vector2 anchorGlobalPos, string areaHint = null)
        {
            if (card == null) { HidePreview(); return; }
            if (_frame == null) return;   // 尚未 _Ready

            _current = card;
            string path;
            string cardName;
            if (card.PlayerTemplate != null)
            {
                path = CardUI.ResolveCardImagePath(card.PlayerTemplate.UID);
                cardName = card.PlayerTemplate.CardName;
            }
            else if (card.CouncilTemplate != null)
            {
                path = card.CouncilTemplate.CardImagePath;
                cardName = card.CouncilTemplate.CardName;
            }
            else
            {
                HidePreview();
                return;
            }

            var tex = ResourceLoader.Load<Texture2D>(path);
            if (tex == null)
            {
                GD.PrintErr($"[CardHoverPreview] 卡图加载失败: {path}");
                HidePreview();
                return;
            }
            _image.Texture = tex;
            _caption.Text = string.IsNullOrEmpty(areaHint)
                ? cardName
                : $"{cardName}（{areaHint}）";

            Visible = true;
            _frame.Visible = true;
            PositionFrame(anchorGlobalPos);
        }

        /// <summary>隐藏浮层。</summary>
        public void HidePreview()
        {
            _current = null;
            Visible = false;
            if (_frame != null) _frame.Visible = false;
        }

        /// <summary>把浮层放到被悬浮卡的一侧（优先右侧；贴边则改放左侧/上方），并钳制在视口内。
        /// 注：浮层若是**某个子窗口的子节点**（检视窗），`GetViewportRect()` 与 `anchorGlobalPos` 都是
        /// **该窗口内容区**的坐标，故本算式一字不改即正确（跨视口混用才会偏到很远，见 `CardPreviewBus`）。</summary>
        private void PositionFrame(Vector2 anchorGlobalPos)
        {
            var vp = GetViewportRect().Size;
            float w = ScaledWidth, h = ScaledHeight;
            float x = anchorGlobalPos.X + 110f;                     // 卡面宽 100 → 右侧留 10px 间隙
            if (x + w > vp.X - 8f) x = anchorGlobalPos.X - w - 10f;
            float y = anchorGlobalPos.Y - 40f;
            x = Mathf.Clamp(x, 8f, Mathf.Max(8f, vp.X - w - 8f));
            y = Mathf.Clamp(y, 8f, Mathf.Max(8f, vp.Y - h - 8f));
            _frame.GlobalPosition = new Vector2(x, y);
        }
    }
}
