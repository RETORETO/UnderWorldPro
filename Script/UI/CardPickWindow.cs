using Godot;
using System;
using System.Collections.Generic;
using Script.Game.Entities;

namespace Script.UI
{
    /// <summary>
    /// 【选卡/找卡 UI 优化·批次 2】选择窗的一次性请求参数（由 Arena 构造，协调器亦可复用）。
    /// 只承载"展示什么 + 选完交给谁"，**不含任何规则判定**（候选集合由 Controller 给定，本窗不增不减）。
    /// </summary>
    public sealed class CardPickRequest
    {
        /// <summary>窗口标题（＝效果提示语）。</summary>
        public string Title;
        /// <summary>标题下的一行提示（缺省时用"点选一张牌后按「确认」"）。</summary>
        public string Hint;
        /// <summary>候选卡（顺序＝Controller 给定顺序，本窗不改动）。</summary>
        public System.Collections.Generic.List<CardInstance> Cards;
        /// <summary>本次做选择的玩家（"你"的判定基准）。</summary>
        public int ChooserId;
        /// <summary>玩家 id → 显示名（Arena 注入；组件内不出现"玩家1/玩家2"字面量）。</summary>
        public Func<int, string> PlayerNameOf;
        /// <summary>地区 UID → 地区编号（Arena 注入）。</summary>
        public Func<string, int> AreaIndexOf;
        /// <summary>是否允许取消（false ⇒ 不显示取消按钮，且点窗外不关闭）。</summary>
        public bool AllowCancel;
        /// <summary>取消按钮文案（如"取消（不打出）"）。</summary>
        public string CancelLabel = "取消";
        /// <summary>选中并确认后回调一次。</summary>
        public Action<CardInstance> OnPicked;
        /// <summary>取消时回调一次（与 OnPicked 互斥，保证恰好交付一次）。</summary>
        public Action OnCancelled;
    }

    /// <summary>
    /// 【选卡/找卡 UI 优化·批次 2】选择窗：**卡面网格 + 每卡所在区域标签 + 确认/取消**。
    /// 取代旧的 `ActivationChoiceWindow`（一列纯按钮、只显示卡名）作为"选卡"的主形态。
    ///
    /// 对照方案 §5.1 的三条既有坑（`InspectWindow` 踩过，务必沿用）：
    ///  ① `Window` 默认 100×100 且不按内容撑开 ⇒ **必须显式设 `Size`**；
    ///  ② **必须先 `AddChild` 再 `Init`**（`CardUI._Ready()` 的 `SetFace(false)` 否则会盖掉翻好的正面）；
    ///  ③ 子窗口有**自己的视口与渲染层级** ⇒ 窗内悬浮预览要**自带** `CardHoverPreview` 子节点。
    ///
    /// 点击捕获走 `CardUI.GuiInput`（不叠透明遮挡层）：遮挡层会吃掉 `MouseEntered`，
    /// 导致窗内悬浮预览失效。卡片在本窗内 `CanDrag=false`，`CardUI.OnGuiInput` 会提前返回，
    /// 故本窗的处理器能稳定收到左键。
    ///
    /// View 只负责"展示 + 收集一次选择"，**不写 Model**：落地由 `OnPicked` 的回调方（Controller）执行。
    /// </summary>
    public partial class CardPickWindow : Window
    {
        private CardPickRequest _req;
        private readonly System.Collections.Generic.List<CardInstance> _cards
            = new System.Collections.Generic.List<CardInstance>();
        private readonly System.Collections.Generic.Dictionary<string, CardUI> _cardUis
            = new System.Collections.Generic.Dictionary<string, CardUI>();

        private ScrollContainer _scroll;
        private Button _confirmBtn;
        private string _selectedUid;
        private bool _done;

        /// <summary>当前是否已有选中项（供外部查询，勿直接读私有字段）。</summary>
        public bool HasSelection => !string.IsNullOrEmpty(_selectedUid);

        /// <summary>构建窗口内容（**必须在 `AddChild` 之后**调用，见类注释②）。</summary>
        public void Init(CardPickRequest req)
        {
            _req = req ?? new CardPickRequest();
            Title = string.IsNullOrEmpty(_req.Title) ? "选择卡牌" : _req.Title;

            _cards.Clear();
            if (_req.Cards != null)
                foreach (var c in _req.Cards)
                    if (c != null) _cards.Add(c);

            var panel = new PanelContainer();
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(panel);
            var vb = new VBoxContainer();
            vb.AddThemeConstantOverride("separation", 10);
            panel.AddChild(vb);

            var hintRow = new Label
            {
                Text = string.IsNullOrEmpty(_req.Hint) ? "点选一张牌后按「确认」" : _req.Hint,
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            vb.AddChild(hintRow);

            // ---- 卡面网格（多行网格；卡面按 CardScale 缩放，避免被容器横向拉伸）----
            // 【2026-10 用户实测】旧实现把 CardUI 直接放进 VBox（列宽 > 卡宽）⇒ 容器把卡面
            // **横向拉伸到列宽**（卡图变形、看着"偏大"），且每列下方的区域标签被挤出可视区（显示不全）。
            // 现改为：外层 Control 定尺寸 + CardUI 按 CardScale 缩放（PivotOffset 归零 ⇒ 缩放后仍与左上角对齐），
            // 卡片用 GridContainer 排成网格，整体放进可横竖滚动的 ScrollContainer，窗口尺寸按屏幕钳制。
            // 【P0】卡面基准尺寸改从 CardLayoutHelper 取（140×196）；缩放比随之重算，
            // 保证**屏幕上的实际显示尺寸不变**（140×196 基准下 0.61 ≈ 旧 100×140 基准下的 0.86）。
            float cardBaseW = CardLayoutHelper.CARD_WIDTH, cardBaseH = CardLayoutHelper.CARD_HEIGHT;
            const float cardScale = 0.61f;   // 略小于战场卡面，观感接近悬浮预览框
            const int labelH = 44, gapX = 10, gapY = 10, colPad = 10, maxCols = 6;
            float dispW = cardBaseW * cardScale;
            float dispH = cardBaseH * cardScale;
            int n = System.Math.Max(1, _cards.Count);
            int cols = System.Math.Min(n, maxCols);
            int rows = (n + cols - 1) / cols;
            float colW = dispW + colPad;
            float gridW = cols * colW + (cols - 1) * gapX;
            float gridH = rows * (dispH + labelH) + (rows - 1) * gapY;

            // 窗口尺寸：内容优先，但**必须留在屏幕内**（否则标题栏/按钮/最后一行会被裁掉）
            var screen = GetTree()?.Root?.GetVisibleRect().Size ?? new Vector2(1280, 720);
            float maxWinW = System.Math.Min(1184f, screen.X * 0.92f);
            float maxWinH = System.Math.Max(260f, screen.Y * 0.86f);
            const float chromeH = 36f + 26f + 10f + 12f + 44f + 12f;   // 标题栏/提示/间距/按钮/内边距
            float maxGridH = System.Math.Max(dispH + labelH, maxWinH - chromeH);
            float scrollW = System.Math.Min(gridW, maxWinW - 60f);
            float scrollH = System.Math.Min(gridH, maxGridH);

            _scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(scrollW + 8f, scrollH + (gridH > scrollH ? 16f : 0f)),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            vb.AddChild(_scroll);

            var grid = new GridContainer
            {
                Columns = cols,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,   // 不随滚动容器横向拉伸
            };
            grid.AddThemeConstantOverride("h_separation", gapX);
            grid.AddThemeConstantOverride("v_separation", gapY);
            _scroll.AddChild(grid);

            var cardScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
            foreach (var inst in _cards)
            {
                var col = new VBoxContainer { CustomMinimumSize = new Vector2(colW, 0) };
                col.AddThemeConstantOverride("separation", 4);
                grid.AddChild(col);

                // 卡面槽：显式尺寸 + 鼠标穿透（卡面本身仍是 Click 的接收者）
                var slot = new Control
                {
                    CustomMinimumSize = new Vector2(dispW, dispH),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                col.AddChild(slot);

                CardUI ui = null;
                if (cardScene != null)
                {
                    ui = cardScene.Instantiate<CardUI>();
                    ui.CanDrag = false;           // 选择窗只展示，不可拖动
                    ui.FreezeSmoothMove = true;   // 位置由容器统一排布
                    slot.AddChild(ui);            // 先入树（`_Ready` 会写 PivotOffset/SetFace(false)）
                    ui.PivotOffset = Vector2.Zero;      // 缩放锚点归零：否则缩放会把卡面推离左上角
                    ui.Scale = new Vector2(cardScale, cardScale);
                    ui.Position = Vector2.Zero;
                    ui.Setup(inst);               // 再 Setup（否则 _Ready 的 SetFace(false) 盖成卡背）
                    // 隐藏占位（对手手牌/牌库）只画卡背；其余一律显示真卡面（单机热座无信息隔离问题）
                    ui.SetFace(!inst.IsHidden);
                    ui.GuiInput += ev => OnCardGuiInput(ev, ui);
                    if (!string.IsNullOrEmpty(inst.UID)) _cardUis[inst.UID] = ui;
                }
                else
                {
                    // 场景缺失兜底：至少显示卡名，绝不静默丢信息
                    col.AddChild(new Label
                    {
                        Text = inst.PlayerTemplate?.CardName ?? inst.UID,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    });
                }

                // ---- 所在区域标签（唯一解析入口，明确到"哪个玩家 / 哪个地区"）----
                string zoneText = CardZoneLabel.Describe(inst, _req.ChooserId, _req.PlayerNameOf, _req.AreaIndexOf);
                col.AddChild(new Label
                {
                    Text = zoneText,
                    TooltipText = zoneText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    CustomMinimumSize = new Vector2(colW, labelH),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                });
            }

            // ---- 确认 / 取消 ----
            var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            btnRow.AddThemeConstantOverride("separation", 16);

            _confirmBtn = new Button
            {
                Text = "确认",
                Disabled = true,   // 未选中不可确认
                CustomMinimumSize = new Vector2(120, 0),
            };
            _confirmBtn.Pressed += Confirm;
            btnRow.AddChild(_confirmBtn);

            if (_req.AllowCancel)
            {
                var cancel = new Button
                {
                    Text = string.IsNullOrEmpty(_req.CancelLabel) ? "取消" : _req.CancelLabel,
                    CustomMinimumSize = new Vector2(120, 0),
                };
                cancel.Pressed += Cancel;
                btnRow.AddChild(cancel);
            }
            vb.AddChild(btnRow);

            // ---- 窗内专用悬浮预览（子窗口自有视口，主视口浮层压不到窗上）----
            var hover = new CardHoverPreview
            {
                Name = CardPreviewBus.WindowOverlayName,
                PreviewScale = 0.72f,
            };
            hover.CanPreviewOverride = c => c != null && _cards.Contains(c);
            AddChild(hover);

            // ---- 显式窗口尺寸（Window 默认 100×100，PopupCentered 只按当前 Size）----
            // 宽高都按"内容 + 留白"算，并钳制在屏幕内（见上方 maxWinW/maxWinH）。
            float contentH = System.Math.Min(gridH, maxGridH) + (gridH > maxGridH ? 16f : 0f);
            Size = new Vector2I(
                (int)System.Math.Clamp(scrollW + 60f, 420f, maxWinW),
                (int)System.Math.Clamp(chromeH + contentH + 12f, 240f, maxWinH));

            // 关闭语义：允许取消 ⇒ 点窗外/关闭按钮 ＝ 取消；不允许 ⇒ 一律忽略（强制选择）
            CloseRequested += OnCloseRequested;
            // 延迟连接：Popup 期间立即连接会在窗口刚获得焦点时误触发一次 FocusExited ⇒ 直接取消。
            Callable.From(ConnectFocusExited).CallDeferred();
        }

        private void ConnectFocusExited()
        {
            FocusExited += OnFocusExited;
        }

        private void OnFocusExited()
        {
            if (_done) return;
            // 强制选择：点窗外不关闭（唯一出口是「确认」）
            if (_req == null || !_req.AllowCancel) return;
            Cancel();
        }

        private void OnCloseRequested()
        {
            if (_done) return;
            if (_req == null || !_req.AllowCancel) return;   // 强制选择期间忽略关闭请求
            Cancel();
        }

        private void OnCardGuiInput(InputEvent ev, CardUI ui)
        {
            if (ev is not InputEventMouseButton mb) return;
            if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;
            if (ui?.Instance == null) return;
            Select(ui.Instance.UID);
            ui.AcceptEvent();   // 左键被本窗消费，避免继续冒泡给容器/滚动条
        }

        /// <summary>该 uid 是否属于本次候选（供 P3b 角标展开面板判断"点了有没有用"）。</summary>
        public bool HasCard(string uid) => !string.IsNullOrEmpty(uid) && _cardUis.ContainsKey(uid);

        /// <summary>
        /// 【跨方案接口】把指定 uid 置为选中（**只置选中，不提交、不关窗**）。
        /// 供协调器"选择窗模式下点场上高亮的卡"（方案 4.5）与《UI美化》的 hover 展开面板复用；
        /// 对"该卡当前没有场上 UI（折叠/层叠中）"同样有效——只要它在本次候选里即可。
        /// </summary>
        public void SelectByUid(string uid)
        {
            if (_done || string.IsNullOrEmpty(uid)) return;
            if (!_cardUis.ContainsKey(uid))
            {
                GD.Print($"[CardPickWindow] SelectByUid 忽略：{uid} 不在本次候选内");
                return;
            }
            Select(uid);
        }

        private void Select(string uid)
        {
            if (_done || string.IsNullOrEmpty(uid)) return;
            if (_selectedUid == uid) return;   // 重复点同一张：保持选中（不来回切换，避免误触取消）

            if (!string.IsNullOrEmpty(_selectedUid)
                && _cardUis.TryGetValue(_selectedUid, out var prev) && prev != null
                && GodotObject.IsInstanceValid(prev))
            {
                prev.SetPickedHighlight(false);
            }

            _selectedUid = uid;
            if (_cardUis.TryGetValue(uid, out var ui) && ui != null && GodotObject.IsInstanceValid(ui))
            {
                ui.SetPickedHighlight(true);
                _scroll?.EnsureControlVisible(ui);   // 长列表时滚到该项
            }
            if (_confirmBtn != null) _confirmBtn.Disabled = false;
        }

        private void Confirm()
        {
            if (_done) return;
            if (string.IsNullOrEmpty(_selectedUid)) return;
            if (!_cardUis.TryGetValue(_selectedUid, out var ui) || ui?.Instance == null) return;
            var picked = ui.Instance;

            _done = true;
            // 先关窗、再回调：回调里常会立刻打开下一个窗口，本窗仍是可见模态时会把新窗吞掉
            // （同 `ActivationChoiceWindow.Dismiss` 的既有教训）。
            QueueFree();
            _req?.OnPicked?.Invoke(picked);
        }

        private void Cancel()
        {
            if (_done) return;
            _done = true;
            QueueFree();
            _req?.OnCancelled?.Invoke();
        }

        /// <summary>
        /// 由外部收尾时**静默关闭**（不回调）：用于协调器已从别的出口结束本次选择
        /// （如场上右键取消）——避免 `OnCancelled` 被交付第二次。
        /// </summary>
        public void CloseSilently()
        {
            if (_done) return;
            _done = true;
            QueueFree();
        }
    }
}
