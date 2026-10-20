using Godot;
using Script.Data.Cards;
using Script.Game.Entities;

namespace Script.UI
{
    /// <summary>
    /// 议会（秘社）卡 UI（CouncilCardUI）
    /// 区别于 CardUI（玩家角色卡，基于 PlayerTemplate）与 AreaCardUI（地区卡，基于 AreaCardData）：
    /// 秘社卡读取 CouncilTemplate（CouncilCardData），展示专属字段（卡图/名称/子类型/牌面文本/战力/激活费用）。
    /// 默认正面朝上；不可拖拽；双击发起激活（后续弹窗由 Arena 处理）。
    /// 横置视觉统一由 Arena 布局（PlaceCardInContainer 按 IsTapped 设 RotationDegrees）负责，本类不自行旋转。
    /// 继承 Control（与玩家卡 CardUI 解耦，避免误触 CardUI 的拖拽/字段逻辑），但把 CardUI 中
    /// 对横置旋转/平滑定位真正必要的逻辑原样搬入本类，保证定位与玩家卡完全对称：
    ///   - _Ready 中显式初始化 PivotOffset=(50,70)、Size=100x140、CustomMinimumSize、Anchor/Offset 归零、MouseFilter=Stop、子节点 Ignore
    ///   - IsTapped / SetTapped（UI 横置标记，由 Arena 在排版前从实例同步）
    ///   - TargetPosition + _Process 平滑移动（与 CardUI 一致，保证 PlaceCardInContainer 设置目标位置后无跳变）
    ///   - _HasPoint 基于本地未旋转 Size 矩形（命中范围与视觉严格一致）
    /// </summary>
    public partial class CouncilCardUI : Control
    {
        // ---------- 导出的子节点引用（对应 CouncilCardUI.tscn 场景结构） ----------
        [Export] private TextureRect _cardImage;       // 卡图
        [Export] private Label _nameLabel;             // 名称
        [Export] private Label _subTypeLabel;          // 子类型（如"秘社-法师结社"）
        [Export] private RichTextLabel _textLabel;     // 牌面完整文本（持续/行动效果说明）
        [Export] private TextureRect _frontface;       // 正面容器（卡图+文本所在层）
        [Export] private TextureRect _backface;        // 背面卡图

        // ---------- 信号 ----------
        [Signal] public delegate void CouncilActivatedEventHandler(CouncilCardUI cardUI);
        [Signal] public delegate void CouncilDoubleClickedEventHandler(CouncilCardUI cardUI);

        // ---------- 数据 ----------
        private CardInstance _instance;
        public CardInstance Instance => _instance;
        public CouncilCardData CouncilTemplate => _instance?.CouncilTemplate;

        public bool IsFaceUp { get; private set; } = true;

        /// <summary>
        /// 【2026-10-20·用户口径】文字底衬面板（`Display`）。它是 Godot 默认主题的 `Panel`＝
        /// 半透明深灰块，原本给卡名/子类型/效果全文当底衬；文字默认隐藏后只剩灰底 ⇒
        /// 整张秘社卡看着像蒙了一层灰。故与文字同开关：只在开 GM 输入框时显示。
        /// </summary>
        private Control _display;

        // ---------- 卡牌基础视觉状态（与 CardUI 对称搬入） ----------
        // 横置标记：仅维护 UI 表现，由 Arena 在排版前从实例 Instance.IsTapped 同步。
        public bool IsTapped { get; private set; } = false;

        // 平滑移动目标位置（与 CardUI.TargetPosition 一致；PlaceCardInContainer 设置后由 _Process 平滑归位）。
        private Vector2 _targetPosition;
        public Vector2 TargetPosition
        {
            get => _targetPosition;
            set => _targetPosition = value;
        }
        [Export] public float ReturnSpeed = 0.15f;

        // 卡牌基础尺寸。秘社卡是**独立卡种**（非 CardUI 子类），P0 只放大了玩家卡，
        // 秘社卡保持 100×140；但常量必须公开，供 Arena.PlaceCardInContainer 区分两种卡尺寸。
        public const float CARD_WIDTH = 100f;
        public const float CARD_HEIGHT = 140f;

        // ---------- 生命周期 ----------
        public override void _Ready()
        {
            // —— 与 CardUI._Ready 等价的视觉初始化（保证横置旋转定位对称）——
            // 子节点不接收鼠标，命中收敛到根节点自身，避免遮挡导致中间区域点不到。
            foreach (var child in GetChildren())
            {
                if (child is Control c)
                    c.MouseFilter = MouseFilterEnum.Ignore;
            }

            // 根节点显式 Stop，确保能接收 GuiInput。
            MouseFilter = MouseFilterEnum.Stop;

            // 显式设定尺寸与最小尺寸，避免依赖布局推断导致 Size 为 0 进而命中/定位异常。
            // 必须先 CustomMinimumSize 再 Size，否则 Size 被旧最小尺寸限制。
            CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            OffsetLeft = OffsetTop = OffsetRight = OffsetBottom = 0;
            AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
            PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;

            TargetPosition = Position;

            _display = GetNodeOrNull<Control>("Display");
            CardTextVisibility.Changed += OnCardTextVisibilityChanged;
            SetFace(true); // 默认正面朝上（需求①）
            GuiInput += OnGuiInput;

            // 【鼠标悬浮预览】秘社卡同样接入：鼠标进入/离开 → 经 CardPreviewBus 请求整张卡图浮层
            // （判定与 CardUI 一致，View 注入的 CovertInspectRule 已对秘社等公开信息放行）。
            MouseEntered += OnMouseEnteredPreview;
            MouseExited += OnMouseExitedPreview;
        }

        private void OnMouseEnteredPreview() => CardPreviewBus.RequestHover(this);

        private void OnMouseExitedPreview() => CardPreviewBus.EndHover(this);

        public override void _Process(double delta)
        {
            if (GlobalPosition.DistanceTo(_targetPosition) > 0.5f)
                GlobalPosition = GlobalPosition.Lerp(_targetPosition, ReturnSpeed);
            else
                GlobalPosition = _targetPosition;
        }

        /// <summary>由 Arena 在排版前调用，同步实例横置状态到 UI 标记（与资产卡 RefreshAssetTappedStates 对称）。
        /// 状态未变则跳过，避免重复触发。</summary>
        public void SetTapped(bool tapped)
        {
            if (IsTapped == tapped) return;
            IsTapped = tapped;
        }

        /// <summary>命中检测基于本地未旋转的 Size 矩形（与 CardUI._HasPoint 一致）。</summary>
        public override bool _HasPoint(Vector2 point)
        {
            return new Rect2(Vector2.Zero, Size).HasPoint(point);
        }

        /// <summary>获取真实视觉包围盒（旋转后的 AABB）——与 `CardUI.GetVisualGlobalRect` 同款实现。
        /// 【2026-09】联机"场上选目标"命中测试需要它：秘社在 `_councilCardUIMap`（不是 `_cardUIMap`），
        /// 但同样会作为合法目标出现（如 JC051「结附于本方秘社」的宿主候选）。</summary>
        public Rect2 GetVisualGlobalRect()
        {
            Transform2D gt = GetGlobalTransform();
            Vector2 p1 = gt * new Vector2(0, 0);
            Vector2 p2 = gt * new Vector2(Size.X, 0);
            Vector2 p3 = gt * new Vector2(0, Size.Y);
            Vector2 p4 = gt * new Vector2(Size.X, Size.Y);
            float minX = Mathf.Min(Mathf.Min(p1.X, p2.X), Mathf.Min(p3.X, p4.X));
            float minY = Mathf.Min(Mathf.Min(p1.Y, p2.Y), Mathf.Min(p3.Y, p4.Y));
            float maxX = Mathf.Max(Mathf.Max(p1.X, p2.X), Mathf.Max(p3.X, p4.X));
            float maxY = Mathf.Max(Mathf.Max(p1.Y, p2.Y), Mathf.Max(p3.Y, p4.Y));
            return new Rect2(new Vector2(minX, minY), new Vector2(maxX - minX, maxY - minY));
        }

        private void OnGuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.DoubleClick)
                {
                    EmitSignal(SignalName.CouncilDoubleClicked, this);
                }
            }
        }

        // ============================================================
        // 核心方法
        // ============================================================
        /// <summary>用秘社实例初始化 UI。读取 CouncilTemplate 展示字段，默认正面朝上。</summary>
        public void Setup(CardInstance council)
        {
            if (council == null)
            {
                _instance = null;
                ClearLabels();
                SetFace(false);
                return;
            }
            var data = council.CouncilTemplate;
            if (data == null)
            {
                GD.PrintErr("❌ CouncilTemplate 为 null");
                SetFace(false);
                return;
            }

            _instance = council;

            // 名称
            if (_nameLabel != null) _nameLabel.Text = data.CardName;

            // 子类型
            if (_subTypeLabel != null) _subTypeLabel.Text = data.SubType;

            // 牌面文本（持续 + 行动效果）
            if (_textLabel != null)
            {
                _textLabel.Clear();
                _textLabel.AppendText(string.IsNullOrEmpty(data.CardText) ? "（暂无效果文本）" : data.CardText);
            }

            // 卡图（用 CouncilCardData.CardImagePath 字符串路径，非 ResolveCardImagePath）
            if (_cardImage == null)
            {
                GD.PrintErr("[CouncilCardUI] _cardImage 节点未绑定（场景 NodePath 可能错误）");
            }
            else if (string.IsNullOrEmpty(data.CardImagePath))
            {
                GD.PrintErr("[CouncilCardUI] CardImagePath 为空（TRES 未写入或未被导入）");
            }
            else
            {
                var texture = ResourceLoader.Load<Texture2D>(data.CardImagePath);
                if (texture != null)
                {
                    _cardImage.Texture = texture;
                }
                else
                    GD.PrintErr($"[CouncilCardUI] 卡图加载失败: {data.CardImagePath}");
            }

            SetFace(true); // 默认正面朝上（需求①）
        }

        /// <summary>GM 命令框开关变化 ⇒ 按当前正/背面重跑一次显隐。</summary>
        private void OnCardTextVisibilityChanged()
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            SetFace(IsFaceUp);
        }

        public override void _ExitTree()
        {
            CardTextVisibility.Changed -= OnCardTextVisibilityChanged;
        }

        /// <summary>正面/背面切换：正面显示卡图+文本，背面显示卡背。</summary>
        public void SetFace(bool isFaceUp)
        {
            IsFaceUp = isFaceUp;
            if (_frontface != null) _frontface.Visible = isFaceUp;
            if (_backface != null) _backface.Visible = !isFaceUp;
            if (_cardImage != null) _cardImage.Visible = isFaceUp;

            // 【2026-10-20·用户口径】秘社卡的卡名/子类型/效果全文与印刷卡图重复 ⇒ 默认隐藏，
            // 只有打开 GM 命令框才显示（与 `CardUI` / `AreaCardUI` 同一开关、同一口径）。
            bool hint = isFaceUp && CardTextVisibility.ShowHintText;
            if (_display == null) _display = GetNodeOrNull<Control>("Display");   // 兜底：_Ready 之前调用
            if (_display != null) _display.Visible = hint;   // 灰底随文字一起消失（否则卡面像蒙了一层灰）
            if (_nameLabel != null) _nameLabel.Visible = hint;
            if (_subTypeLabel != null) _subTypeLabel.Visible = hint;
            if (_textLabel != null) _textLabel.Visible = hint;
        }

        private void ClearLabels()
        {
            if (_nameLabel != null) _nameLabel.Text = "";
            if (_subTypeLabel != null) _subTypeLabel.Text = "";
            if (_textLabel != null) _textLabel.Clear();
        }

        /// <summary>触发激活请求（由双击信号在 Arena 中订阅，弹出激活选项窗口）。</summary>
        public void RequestActivate() => EmitSignal(SignalName.CouncilActivated, this);

        // 拖拽结附本秘社时的合法目标高亮（与 CardUI.SetTargetHighlight 对称）。
        private bool _targetHighlight = false;
        public void SetTargetHighlight(bool on)
        {
            if (_targetHighlight == on) return;
            _targetHighlight = on;
            Modulate = on ? new Color(1f, 1f, 0.6f, 0.85f) : Colors.White;
        }

        // ============================================================
        // 【选卡/找卡 UI 优化·批次 1】选目标"可选"黄色高光环（与 CardUI.SetSelectableHighlight 逐字同款）
        // ------------------------------------------------------------
        // 秘社进 cat-1 是必需的：共 11 张卡的**打出目标就是本方秘社**
        // （JC009/JC012/JC025/JC033/JC034/JC039/JC053/JC068/JC082/JC115/JC117），
        // 它们此前**没有任何可命中的秘社高亮**。用独立 `_Draw` 边框，不干扰 `SetTargetHighlight`
        // 的 Modulate（拖拽半透明高亮）。
        // ============================================================
        private bool _showSelectableHighlight = false;

        /// <summary>选目标（触发/行动效果）可选目标黄色高光环。由 Arena 按合法集切换，选完后清除。</summary>
        public void SetSelectableHighlight(bool on)
        {
            if (_showSelectableHighlight == on) return;
            _showSelectableHighlight = on;
            QueueRedraw();
        }

        public override void _Draw()
        {
            base._Draw();
            if (_showSelectableHighlight)
            {
                // 外扩 2px，与 CardUI 同款（黄框 + 宽 6）
                DrawRect(new Rect2(new Vector2(-2, -2), Size + new Vector2(4, 4)),
                    new Color(1f, 0.85f, 0.1f), false, 6f);
            }
        }
    }
}
