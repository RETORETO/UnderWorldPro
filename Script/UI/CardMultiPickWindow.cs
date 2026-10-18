using Godot;
using System;
using System.Collections.Generic;
using Script.Game.Entities;

namespace Script.UI
{
    /// <summary>
    /// 【2026-10-19】**多选**选卡窗的请求参数（由 `ArenaOl` 构造；只承载"展示什么 + 选完交给谁"）。
    /// 与单选的 `CardPickRequest` 是一对：单选=点一张→确认，多选=点若干张（可来回切换）→确认。
    /// </summary>
    public sealed class CardMultiPickRequest
    {
        /// <summary>窗口标题（＝效果/流程提示语）。</summary>
        public string Title;
        /// <summary>标题下的一行提示（缺省时用"点卡切换选中，选好后按「确认」"）。</summary>
        public string Hint;
        /// <summary>候选卡（顺序＝调用方给定顺序，本窗不改动）。</summary>
        public System.Collections.Generic.List<CardInstance> Cards;
        /// <summary>本次做选择的玩家（"你"的判定基准）。</summary>
        public int ChooserId;
        /// <summary>玩家 id → 显示名（调用方注入；组件内不出现"玩家1/玩家2"字面量）。</summary>
        public Func<int, string> PlayerNameOf;
        /// <summary>地区 UID → 地区编号（调用方注入）。</summary>
        public Func<string, int> AreaIndexOf;
        /// <summary>至少选几张（0 ⇒ 可以不选，如开局调度）。</summary>
        public int MinCount;
        /// <summary>最多选几张（&lt;=0 ⇒ 不限制）。</summary>
        public int MaxCount;
        /// <summary>确认按钮文案。</summary>
        public string ConfirmLabel = "确认";
        /// <summary>是否允许取消（开局调度**不允许**：调度是固定流程，未选＝空确认）。</summary>
        public bool AllowCancel;
        /// <summary>取消按钮文案。</summary>
        public string CancelLabel = "取消";
        /// <summary>确认后回调一次（顺序＝用户勾选的先后顺序）。</summary>
        public Action<System.Collections.Generic.List<CardInstance>> OnConfirmed;
        /// <summary>取消时回调一次（与 OnConfirmed 互斥，保证恰好交付一次）。</summary>
        public Action OnCancelled;
    }

    /// <summary>
    /// 【2026-10-19·用户口径】"游戏开始的调度也要采用选卡类似的 UI"：
    /// 开局调度原来走 `ChoicePromptPanel` 的**勾选框列表**（只有卡名、没有卡面），
    /// 与墓地/手牌/牌库选卡用的 `CardPickWindow`（真卡面网格 + 所在区域标签）观感割裂。
    ///
    /// 本窗 = `CardPickWindow` 的**多选版本**，两者的三条既有坑完全一致（务必沿用）：
    ///  ① `Window` 默认 100×100 且不按内容撑开 ⇒ **必须显式设 `Size`**；
    ///  ② **必须先 `AddChild` 再 `Init`**（`CardUI._Ready()` 的 `SetFace(false)` 否则会盖掉翻好的正面）；
    ///  ③ 子窗口有**自己的视口与渲染层级** ⇒ 窗内悬浮预览要**自带** `CardHoverPreview` 子节点。
    ///
    /// 交互：点一张卡＝选中（`SetPickedHighlight(true)`），再点一次＝取消选中；底部实时显示"已选 N 张"。
    /// View 只负责"展示 + 收集选择"，**不写 Model**：调度上报由 `OnConfirmed` 的回调方执行。
    /// </summary>
    public partial class CardMultiPickWindow : Window
    {
        private CardMultiPickRequest _req;
        private readonly System.Collections.Generic.List<CardInstance> _cards
            = new System.Collections.Generic.List<CardInstance>();
        private readonly System.Collections.Generic.Dictionary<string, CardUI> _cardUis
            = new System.Collections.Generic.Dictionary<string, CardUI>();
        /// <summary>已选中的 uid（按点击先后顺序，确认时原样交付）。</summary>
        private readonly System.Collections.Generic.List<string> _picked
            = new System.Collections.Generic.List<string>();

        private ScrollContainer _scroll;
        private Button _confirmBtn;
        private Label _countLabel;
        private bool _done;

        /// <summary>当前已选张数（供外部查询）。</summary>
        public int PickedCount => _picked.Count;

        /// <summary>构建窗口内容（**必须在 `AddChild` 之后**调用，见类注释②）。</summary>
        public void Init(CardMultiPickRequest req)
        {
            _req = req ?? new CardMultiPickRequest();
            Title = string.IsNullOrEmpty(_req.Title) ? "选择卡牌" : _req.Title;

            _cards.Clear();
            _picked.Clear();
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
                Text = string.IsNullOrEmpty(_req.Hint)
                    ? "点卡切换选中（可来回切换），选好后按「确认」"
                    : _req.Hint,
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            vb.AddChild(hintRow);

            // ---- 卡面网格（与 CardPickWindow 同参数，保证两个窗看起来是同一套 UI）----
            float cardBaseW = CardLayoutHelper.CARD_WIDTH, cardBaseH = CardLayoutHelper.CARD_HEIGHT;
            const float cardScale = 0.61f;
            const int labelH = 44, gapX = 10, gapY = 10, colPad = 10, maxCols = 6;
            float dispW = cardBaseW * cardScale;
            float dispH = cardBaseH * cardScale;
            int n = System.Math.Max(1, _cards.Count);
            int cols = System.Math.Min(n, maxCols);
            int rows = (n + cols - 1) / cols;
            float colW = dispW + colPad;
            float gridW = cols * colW + (cols - 1) * gapX;
            float gridH = rows * (dispH + labelH) + (rows - 1) * gapY;

            var screen = GetTree()?.Root?.GetVisibleRect().Size ?? new Vector2(1280, 720);
            float maxWinW = System.Math.Min(1184f, screen.X * 0.92f);
            float maxWinH = System.Math.Max(260f, screen.Y * 0.86f);
            const float chromeH = 36f + 26f + 10f + 12f + 44f + 12f;
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
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
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

                if (cardScene != null)
                {
                    var ui = cardScene.Instantiate<CardUI>();
                    ui.CanDrag = false;           // 只展示/选择，不可拖动
                    ui.FreezeSmoothMove = true;
                    slot.AddChild(ui);            // 先入树（`_Ready` 会写 PivotOffset/SetFace(false)）
                    ui.PivotOffset = Vector2.Zero;
                    ui.Scale = new Vector2(cardScale, cardScale);
                    ui.Position = Vector2.Zero;
                    ui.Setup(inst);               // 再 Setup（否则 _Ready 的 SetFace(false) 盖成卡背）
                    ui.SetFace(!inst.IsHidden);   // 隐藏占位只画卡背
                    var captured = ui;
                    ui.GuiInput += ev => OnCardGuiInput(ev, captured);
                    if (!string.IsNullOrEmpty(inst.UID)) _cardUis[inst.UID] = ui;
                }
                else
                {
                    col.AddChild(new Label
                    {
                        Text = inst.PlayerTemplate?.CardName ?? inst.UID,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    });
                }

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

            // ---- 已选计数 / 确认 / 取消 ----
            var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            btnRow.AddThemeConstantOverride("separation", 16);

            _countLabel = new Label
            {
                Text = CountText(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btnRow.AddChild(_countLabel);

            _confirmBtn = new Button
            {
                Text = string.IsNullOrEmpty(_req.ConfirmLabel) ? "确认" : _req.ConfirmLabel,
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
            RefreshConfirm();

            // ---- 窗内专用悬浮预览（子窗口自有视口，主视口浮层压不到窗上）----
            var hover = new CardHoverPreview
            {
                Name = CardPreviewBus.WindowOverlayName,
                PreviewScale = 0.72f,
            };
            hover.CanPreviewOverride = c => c != null && _cards.Contains(c);
            AddChild(hover);

            float contentH = System.Math.Min(gridH, maxGridH) + (gridH > maxGridH ? 16f : 0f);
            Size = new Vector2I(
                (int)System.Math.Clamp(scrollW + 60f, 460f, maxWinW),
                (int)System.Math.Clamp(chromeH + contentH + 12f, 240f, maxWinH));

            // 关闭语义：允许取消 ⇒ 点窗外/关闭按钮＝取消；不允许 ⇒ 一律忽略（调度是固定流程）
            CloseRequested += OnCloseRequested;
            Callable.From(ConnectFocusExited).CallDeferred();
        }

        private void ConnectFocusExited() => FocusExited += OnFocusExited;

        private void OnFocusExited()
        {
            if (_done || _req == null || !_req.AllowCancel) return;
            Cancel();
        }

        private void OnCloseRequested()
        {
            if (_done || _req == null || !_req.AllowCancel) return;
            Cancel();
        }

        private void OnCardGuiInput(InputEvent ev, CardUI ui)
        {
            if (ev is not InputEventMouseButton mb) return;
            if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;
            if (ui?.Instance == null) return;
            Toggle(ui);
            ui.AcceptEvent();
        }

        /// <summary>切换某张卡的选中状态（点一次选中、再点一次取消）。</summary>
        private void Toggle(CardUI ui)
        {
            if (_done) return;
            string uid = ui.Instance?.UID;
            if (string.IsNullOrEmpty(uid)) return;

            if (_picked.Contains(uid))
            {
                _picked.Remove(uid);
                ui.SetPickedHighlight(false);
            }
            else
            {
                // 上限已满：忽略这次选中（不弹提示，边框闪一下即可——控件只做约束，不写规则）
                int max = _req?.MaxCount ?? 0;
                if (max > 0 && _picked.Count >= max) return;
                _picked.Add(uid);
                ui.SetPickedHighlight(true);
            }

            if (_countLabel != null) _countLabel.Text = CountText();
            RefreshConfirm();
        }

        private string CountText()
        {
            int min = _req?.MinCount ?? 0;
            int max = _req?.MaxCount ?? 0;
            if (max > 0) return $"已选 {_picked.Count} 张（最多 {max} 张）";
            if (min > 0) return $"已选 {_picked.Count} 张（至少 {min} 张）";
            return $"已选 {_picked.Count} 张";
        }

        private void RefreshConfirm()
        {
            if (_confirmBtn == null) return;
            int min = _req?.MinCount ?? 0;
            int max = _req?.MaxCount ?? 0;
            bool ok = _picked.Count >= min && (max <= 0 || _picked.Count <= max);
            _confirmBtn.Disabled = !ok;
        }

        private void Confirm()
        {
            if (_done) return;
            int min = _req?.MinCount ?? 0;
            if (_picked.Count < min) return;

            var picked = new System.Collections.Generic.List<CardInstance>();
            // 顺序＝用户勾选顺序（服务端按 UID 复检，顺序只影响展示/日志）
            foreach (var uid in _picked)
                if (_cardUis.TryGetValue(uid, out var ui) && ui?.Instance != null) picked.Add(ui.Instance);

            _done = true;
            // 先关窗、再回调：回调里常会立刻开下一个窗口，本窗仍是可见模态时会把新窗吞掉
            QueueFree();
            _req?.OnConfirmed?.Invoke(picked);
        }

        private void Cancel()
        {
            if (_done) return;
            _done = true;
            QueueFree();
            _req?.OnCancelled?.Invoke();
        }
    }
}
