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

        public override void _Ready()
        {
            // 浮层自身不吃鼠标事件（否则会挡住底下的卡，导致悬浮抖动/点不到牌）
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            Visible = false;

            _frame = new PanelContainer();
            _frame.MouseFilter = MouseFilterEnum.Ignore;
            _frame.CustomMinimumSize = new Vector2(PREVIEW_WIDTH, PREVIEW_HEIGHT);
            _frame.Size = new Vector2(PREVIEW_WIDTH, PREVIEW_HEIGHT);
            AddChild(_frame);

            var box = new VBoxContainer();
            box.MouseFilter = MouseFilterEnum.Ignore;
            _frame.AddChild(box);

            _image = new TextureRect
            {
                MouseFilter = MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(PREVIEW_WIDTH - 8f, PREVIEW_HEIGHT - 30f),
            };
            box.AddChild(_image);

            _caption = new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
            box.AddChild(_caption);
        }

        /// <summary>显示某张卡的整张卡图（调用方需已确认"允许查看"）。</summary>
        public void ShowFor(CardInstance card, Vector2 anchorGlobalPos, string areaHint = null)
        {
            if (card?.PlayerTemplate == null) { HidePreview(); return; }
            if (_frame == null) return;   // 尚未 _Ready

            _current = card;
            string path = CardUI.ResolveCardImagePath(card.PlayerTemplate.UID);
            var tex = ResourceLoader.Load<Texture2D>(path);
            if (tex == null)
            {
                GD.PrintErr($"[CardHoverPreview] 卡图加载失败: {path}");
                HidePreview();
                return;
            }
            _image.Texture = tex;
            _caption.Text = string.IsNullOrEmpty(areaHint)
                ? card.PlayerTemplate.CardName
                : $"{card.PlayerTemplate.CardName}（{areaHint}）";

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

        /// <summary>把浮层放到被悬浮卡的一侧（优先右侧；贴边则改放左侧/上方），并钳制在视口内。</summary>
        private void PositionFrame(Vector2 anchorGlobalPos)
        {
            var vp = GetViewportRect().Size;
            float x = anchorGlobalPos.X + 110f;                     // 卡面宽 100 → 右侧留 10px 间隙
            if (x + PREVIEW_WIDTH > vp.X - 8f) x = anchorGlobalPos.X - PREVIEW_WIDTH - 10f;
            float y = anchorGlobalPos.Y - 40f;
            x = Mathf.Clamp(x, 8f, Mathf.Max(8f, vp.X - PREVIEW_WIDTH - 8f));
            y = Mathf.Clamp(y, 8f, Mathf.Max(8f, vp.Y - PREVIEW_HEIGHT - 8f));
            _frame.GlobalPosition = new Vector2(x, y);
        }
    }
}
