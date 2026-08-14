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
                        // 提升层级
                        ZIndex = 10;
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
        public void UpdateMarks(int p1, int p2)
        {
            if (_marksLabel != null)
                _marksLabel.Text = $"P1: {p1}  P2: {p2}";
        }

    }
}