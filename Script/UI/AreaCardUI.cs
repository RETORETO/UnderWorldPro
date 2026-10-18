using Godot;
using Script.Data.Cards;

namespace Script.UI
{
    public partial class AreaCardUI : Control
    {
        // ---------- 导出的子节点引用（对应 AreaCardUI 场景结构） ----------
        [Export] private TextureRect _cardImage;          // CardImage
        [Export] private Label _nameLabel;                // NameLabel
        [Export] private Label _powerLabel;               // PowerLabel
        [Export] private Label _scoreLabel;               // ScoreLabel
        [Export] private Label _marksLabel;               // MarksLabel

        [Export] private TextureRect _frontface;          // 正面卡图（当前未使用）
        [Export] private TextureRect _backface;           // 背面卡图（小尺寸备用）
        [Export] private TextureRect _background;         // 卡背底图（Background 节点）

        // ---------- 信号 ----------
        [Signal] public delegate void AreaCardClickedEventHandler(AreaCardUI cardUI);
        [Signal] public delegate void AreaCardDragStartedEventHandler(AreaCardUI cardUI);
        [Signal] public delegate void AreaCardDragEndedEventHandler(AreaCardUI cardUI, bool dropped);

        // ---------- 数据 ----------
        private AreaCardData _data;
        public AreaCardData Data => _data;

        // ---------- 交互状态 ----------
        private bool _isDragging;
        private Vector2 _dragOffset;
        private Vector2 _initialPosition;

        // ---------- 卡背缓存 ----------
        private static Texture2D _cachedBackTexture;

        // ============================================================
        // 生命周期
        // ============================================================
        public override void _Ready()
        {
            CheckExports();
            SetFace(false); // 默认显示背面（安全起见）

            // 地区卡拖拽已禁用，仅手牌可拖拽（如需恢复取消下一行注释）
            // GuiInput += OnGuiInput;

        }

        // ============================================================
        // 核心方法
        // ============================================================
        /// <summary>
        /// 设置地区卡数据并显示正面
        /// </summary>
        public void Setup(AreaCardData data)
        {
            if (data == null)
            {
                _data = null;
                ClearLabels();
                SetFace(false);
                return;
            }

            _data = data;

            // 名称
            if (_nameLabel != null)
                _nameLabel.Text = data.CardName ?? "未知地区";

            // 势力值（RequiredPower）
            if (_powerLabel != null)
                _powerLabel.Text = data.RequiredPower > 0 ? data.RequiredPower.ToString() : "—";

            // 分数（来自 BaseCardData.Score）
            if (_scoreLabel != null)
                _scoreLabel.Text = data.Score.ToString();

            // 标记/效果（WinTriggerEffect）
            if (_marksLabel != null)
                _marksLabel.Text = string.IsNullOrEmpty(data.WinTriggerEffect) ? "无效果" : data.WinTriggerEffect;

            // ---------- 加载正面卡图 ----------
            // 注意：地区卡资源（AreaCards/xxx.jpg）本身就是完整正面，只赋给 CardImage 即可
            if (_cardImage != null)
            {
                string imagePath = data.CardImagePath;
                if (string.IsNullOrEmpty(imagePath))
                {
                    GD.PrintErr($"[AreaCardUI] 地区卡 {data.CardName} 的 CardImagePath 为空，使用默认占位图");
                    _cardImage.Modulate = new Color(0.8f, 0.8f, 0.8f);
                }
                else
                {
                    var texture = ResourceLoader.Load<Texture2D>(imagePath);
                    if (texture != null)
                    {
                        _cardImage.Texture = texture;
                    }
                    else
                    {
                        GD.PrintErr($"[AreaCardUI] 卡图加载失败: {imagePath}");
                        _cardImage.Modulate = new Color(0.8f, 0.8f, 0.8f);
                    }
                }
            }

            // ---------- 加载背面卡图（缓存） ----------
            if (_backface != null)
            {
                if (_cachedBackTexture == null)
                {
                    string backPath = "res://Cards/Image/CardBack.jpg"; // 可替换为地区卡专用背
                    _cachedBackTexture = ResourceLoader.Load<Texture2D>(backPath);
                    if (_cachedBackTexture == null)
                        GD.PrintErr($"[AreaCardUI] 卡背加载失败: {backPath}");
                }
                _backface.Texture = _cachedBackTexture;
            }

            SetFace(true); // 默认显示正面
        }

        /// <summary>
        /// 切换正面/背面显示
        /// </summary>
        public void SetFace(bool isFaceUp)
        {
            if (_frontface != null) _frontface.Visible = false;       // 当前未使用，避免重复显示
            if (_backface != null) _backface.Visible = !isFaceUp;
            if (_cardImage != null) _cardImage.Visible = isFaceUp;      // 正面完整卡图
            if (_background != null) _background.Visible = !isFaceUp;   // 卡背底图，正面时隐藏

            // 控制文字标签（背面隐藏）
            if (_nameLabel != null) _nameLabel.Visible = isFaceUp;
            if (_powerLabel != null) _powerLabel.Visible = isFaceUp;
            if (_scoreLabel != null) _scoreLabel.Visible = isFaceUp;
            if (_marksLabel != null) _marksLabel.Visible = isFaceUp;
        }

        /// <summary>
        /// 刷新显示（数据可能外部改变时调用）
        /// </summary>
        public void RefreshDisplay()
        {
            if (_data != null)
                Setup(_data);
        }

        // ============================================================
        // 交互（点击 + 拖拽）
        // ============================================================
        private void OnGuiInput(InputEvent @event)
        {
            GD.Print("[AreaCardUI] OnGuiInput 触发");
            if (@event is InputEventMouseButton mouse)
            {
                if (mouse.ButtonIndex == MouseButton.Left)
                {
                    if (mouse.Pressed)
                    {
                        // 开始拖拽
                        _isDragging = true;
                        _initialPosition = Position;
                        _dragOffset = GetGlobalMousePosition() - GlobalPosition;
                        // 提升层级（区域内卡牌拖拽态；层号由 Script.UI.Layers 统一分配）
                        ZIndex = Layers.CardDragging;
                        EmitSignal(SignalName.AreaCardDragStarted, this);
                        AcceptEvent();
                        GD.Print($"[AreaCardUI] 拖拽开始: {_data?.CardName}");
                    }
                    else if (_isDragging)
                    {
                        // 结束拖拽（未释放到目标）
                        _isDragging = false;
                        ZIndex = 0;
                        EmitSignal(SignalName.AreaCardDragEnded, this, false);
                        AcceptEvent();
                        GD.Print($"[AreaCardUI] 拖拽结束 (未放置)");
                    }
                }
            }
            else if (@event is InputEventMouseButton click && click.ButtonIndex == MouseButton.Left && click.Pressed)
            {
                // 单击（不拖拽时触发）
                EmitSignal(SignalName.AreaCardClicked, this);
                AcceptEvent();
            }
        }

        // 处理鼠标移动（拖拽中）
        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventMouseMotion motion && _isDragging)
            {
                GlobalPosition = GetGlobalMousePosition() - _dragOffset;
                AcceptEvent();
            }
        }

        /// <summary>
        /// 取消拖拽（例如拖拽到非法区域时）
        /// </summary>
        public void CancelDrag()
        {
            if (!_isDragging) return;

            _isDragging = false;
            ZIndex = 0;
            // 不需要设置 Position，因为父容器会重新排列
            GD.Print("[AreaCardUI] 拖拽状态已取消");
        }
        // ============================================================
        // 辅助
        // ============================================================
        private void ClearLabels()
        {
            if (_nameLabel != null) _nameLabel.Text = "";
            if (_powerLabel != null) _powerLabel.Text = "";
            if (_scoreLabel != null) _scoreLabel.Text = "";
            if (_marksLabel != null) _marksLabel.Text = "";
        }

        private void CheckExports()
        {
            if (_cardImage == null) GD.PrintErr("[AreaCardUI] ❌ _cardImage 未绑定");
            if (_nameLabel == null) GD.PrintErr("[AreaCardUI] ❌ _nameLabel 未绑定");
            if (_powerLabel == null) GD.PrintErr("[AreaCardUI] ❌ _powerLabel 未绑定");
            if (_scoreLabel == null) GD.PrintErr("[AreaCardUI] ❌ _scoreLabel 未绑定");
            if (_marksLabel == null) GD.PrintErr("[AreaCardUI] ❌ _marksLabel 未绑定");
            if (_frontface == null) GD.PrintErr("[AreaCardUI] ❌ _frontface 未绑定");
            if (_backface == null) GD.PrintErr("[AreaCardUI] ❌ _backface 未绑定");
            if (_background == null) GD.PrintErr("[AreaCardUI] ❌ _background 未绑定");
        }
        /// <summary>
        /// 地区卡右下角的"势力标记"计数。
        /// <paramref name="localFirst"/> = true 时（联机镜像视图：本地座位 2）文案改用「本方/对手」，
        /// 否则仍是「P1/P2」——因为此时 P1 是**对手**，继续标 P1 会指错人。
        /// </summary>
        public void UpdateMarks(int p1, int p2, bool localFirst = false)
        {
            if (_marksLabel == null) return;
            _marksLabel.Text = localFirst ? $"本方 {p1} / 对手 {p2}" : $"P1: {p1}  P2: {p2}";
        }

        // ============================================================
        // 【选卡/找卡 UI 优化·批次 1】选目标"可选"黄色高光环
        // ------------------------------------------------------------
        // 与 `CardUI.SetSelectableHighlight` / `CouncilCardUI.SetSelectableHighlight` 逐字同款。
        // 用途：**以地区为目标**（机动类、12 张"打出目标＝地区"的卡等）时给地区卡画黄框，
        // 让"这一格地区可选"在视觉上可见（此前 `AreaSlot.SetHighlight` 的 `SelfModulate`
        // 画在**不自绘**的根节点上，玩家什么都看不到——本次一并修掉）。
        // ============================================================
        private bool _showSelectableHighlight = false;
        /// <summary>【2026-10-16】"**已选中**"态（JC021 多选：地区已被选中）——与"可选"黄框**不同的颜色**
        /// （青色粗框），用户口径："JC021 选中地区后地区高亮消失，我需要一个**区别于原有高亮的其他高亮**
        /// 代表地区被选中"。两个标志可同时为真，绘制时**已选中优先**（粗青框覆盖黄框）。</summary>
        private bool _showChosenHighlight = false;

        /// <summary>地区卡"可选目标"黄色高光环（由 AreaSlot.SetHighlight 转发）。</summary>
        public void SetSelectableHighlight(bool on)
        {
            if (_showSelectableHighlight == on) return;
            _showSelectableHighlight = on;
            QueueRedraw();
        }

        /// <summary>地区卡"已选中"青色粗框（由 AreaSlot.SetSelected 转发；与可选黄框区分）。</summary>
        public void SetChosenHighlight(bool on)
        {
            if (_showChosenHighlight == on) return;
            _showChosenHighlight = on;
            QueueRedraw();
        }

        public override void _Draw()
        {
            base._Draw();
            if (_showChosenHighlight)
            {
                // 已选中：**青色粗框**（宽 8、外扩 3），与"可选"黄框（宽 6、外扩 2）明显区分
                DrawRect(new Rect2(new Vector2(-3, -3), Size + new Vector2(6, 6)),
                    new Color(0.15f, 0.85f, 1f), false, 8f);
            }
            else if (_showSelectableHighlight)
            {
                // 外扩 2px、宽 6，与 CardUI / CouncilCardUI 同款视觉
                DrawRect(new Rect2(new Vector2(-2, -2), Size + new Vector2(4, 4)),
                    new Color(1f, 0.85f, 0.1f), false, 6f);
            }
        }

    }
}