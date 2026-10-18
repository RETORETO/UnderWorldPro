using Godot;
using Script.Game.Entities;

namespace Script.UI
{
    /// <summary>
    /// 【2026-09·JC062/JC057】检视结果窗口（**程序化构建**，不需要 .tscn）：
    /// 把本次结算中被私下检视到的**全部**牌面**列在同一个窗**里，框里**只有一个『确认』按钮**，
    /// 点击（或点窗外关闭窗口）即回调关闭。
    ///
    /// 用户口径（原文）："JC062 的查看结果方式是结算的时候弹出一个弹窗，里面列出所有看到的信息，
    /// 这个框的按钮只有一个确认，点击后关闭弹窗"＋"JC057 的查看方式和 JC062 一致"。
    ///
    /// 【2026-09 修复·用户实测三项】
    ///  ① **弹窗太小** ⇒ **显式设窗口尺寸**：Godot `Window` 默认 100×100、`wrap_controls=false`
    ///     不会按内容撑开，而 `PopupCentered()` 只按**当前** `Size` ⇒ 内容全被裁。
    ///  ② **卡面显示成卡背** ⇒ 调用方必须**先 `AddChild` 再 `Init`**：`CardUI._Ready()` 里是
    ///     `SetFace(false)`（默认背面），若窗口还没入树就先 `Setup()+SetFace(true)`，`_Ready` 会
    ///     后跑把正面盖掉（本仓库既有注释同款陷阱：`ArenaOl`/`InvestigationWindow`）。
    ///  ③ **窗内悬浮预览偏得很远** ⇒ 本窗**自带一个 `CardHoverPreview` 子节点**：子窗口有自己的视口
    ///     与渲染层级（`Window` 永远画在所有 Control 之上），主视口浮层既坐标不同也压不到窗上；
    ///     `CardPreviewBus` 会按"悬浮卡所在窗口"优先解析到它。
    ///
    /// View 只负责"展示 + 收集一次确认"，**不写 Model**：续算由回调（`EffectEngine.ContinuePendingChain`）驱动。
    /// 卡面渲染与 `InvestigationWindow` 同款（`res://Scenes/UI/CardUI.tscn` + `Setup` + `SetFace(true)`，
    /// **强制正面**——检视就是让检视者看到卡面）。
    /// </summary>
    public partial class InspectWindow : Window
    {
        /// <summary>玩家点『确认』（或关闭窗口）后回调一次。</summary>
        public System.Action OnConfirmed;

        private bool _done;
        private PackedScene _cardUIScene;
        private readonly System.Collections.Generic.List<CardInstance> _cards
            = new System.Collections.Generic.List<CardInstance>();

        /// <summary>构建窗口内容（**必须在 `AddChild` 之后**调用，见类注释②）。</summary>
        public void Init(string title, System.Collections.Generic.List<CardInstance> cards)
        {
            Title = string.IsNullOrEmpty(title) ? "检视" : title;
            _cardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
            _cards.Clear();
            if (cards != null)
                foreach (var c in cards)
                    if (c != null) _cards.Add(c);

            // 根面板铺满窗口内容区（窗口尺寸由本方法末尾显式设定，避免内容挤在左上角）
            var panel = new PanelContainer();
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(panel);
            var vb = new VBoxContainer();
            vb.AddThemeConstantOverride("separation", 10);
            panel.AddChild(vb);

            var hint = new Label { Text = string.IsNullOrEmpty(title) ? "检视" : title };
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            vb.AddChild(hint);

            // 卡面区：横向排列；张数多时用滚动容器（卡面真实尺寸＝CardLayoutHelper 常量，见 `CardUI._Ready`）
            int cardW = (int)CardLayoutHelper.CARD_WIDTH, cardH = (int)CardLayoutHelper.CARD_HEIGHT, gap = 8;
            int n = System.Math.Max(1, _cards.Count);
            int rowW = n * cardW + (n - 1) * gap;
            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(System.Math.Min(1160, rowW + 8), cardH + 8),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            vb.AddChild(scroll);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", gap);
            scroll.AddChild(row);

            foreach (var inst in _cards)
            {
                CardUI ui = null;
                if (_cardUIScene != null)
                {
                    ui = _cardUIScene.Instantiate<CardUI>();
                    ui.CanDrag = false;           // 检视窗只展示，不可拖动
                    ui.FreezeSmoothMove = true;   // 位置由容器统一排布
                    row.AddChild(ui);             // 先入树，再 Setup（否则 _Ready 的 SetFace(false) 会盖成卡背）
                    ui.Setup(inst);
                    ui.SetFace(true);             // 检视 = 强制显示卡面
                }
                if (ui == null)
                {
                    // 无卡面场景可用（编辑器缺场景/异常装配）→ 退化为卡名列示，绝不静默丢信息
                    var label = new Label { Text = inst.PlayerTemplate?.CardName ?? inst.UID };
                    row.AddChild(label);
                }
            }

            var ok = new Button { Text = "确认" };
            ok.CustomMinimumSize = new Vector2(120, 0);
            ok.Pressed += Confirm;
            var okRow = new HBoxContainer();
            okRow.Alignment = BoxContainer.AlignmentMode.Center;
            okRow.AddChild(ok);
            vb.AddChild(okRow);

            // 【修复③】窗内专用悬浮浮层：子窗口有自己的视口与渲染层级，判定＝"本窗列出的卡一律可看"
            // （这些卡已由效果授权给检视者：可能是对手手牌/对手暗藏者）。
            var hover = new CardHoverPreview
            {
                Name = CardPreviewBus.WindowOverlayName,
                PreviewScale = 0.72f,   // 缩小以适配窗口高度（原尺寸 260×364 会被窗口边界裁掉）
            };
            hover.CanPreviewOverride = c => c != null && _cards.Contains(c);
            AddChild(hover);

            // 【修复①】显式窗口尺寸：至少完整显示卡面本身（含标题栏、提示、卡行、确认按钮的留白）。
            Size = new Vector2I(
                System.Math.Clamp(rowW + 56, 340, 1184),
                36 + 28 + 10 + (cardH + 16) + 10 + 36 + 16);

            // 点窗外关闭 ＝ 已查看（同样是"只有确认"语义：不存在"取消检视"）
            CloseRequested += Confirm;
        }

        private void Confirm()
        {
            if (_done) return;   // 防重复回调（重复续算会丢掉动作链）
            _done = true;
            OnConfirmed?.Invoke();
            QueueFree();
        }
    }
}
