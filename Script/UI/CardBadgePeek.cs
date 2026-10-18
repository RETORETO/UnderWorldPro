using Godot;
using Script.Game.Entities;
using System;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 【P3b·2026-10-18】角标悬浮展开面板：在宿主卡旁边横向列出被折叠的成员卡面。
    ///
    /// 【2026-10-18 用户口径】① 封印卡与附属卡来自两个不同区域 ⇒ 面板**一次只列一种**
    /// （标题写明"封印卡"或"附属卡"，由调用方按 `CardBadgeKind` 过滤后传入）；
    /// ② 面板里的卡**双击＝发动激活能力**（如 JC020 引匕的飞刀：横置自身横置目标暗藏者）——
    /// 这些卡被折叠成角标后，场上已经没有可双击的卡面了，双击入口必须在这里补回来。
    ///
    /// 定位＝`Layers.CardHoverPreview`（830），与整卡预览同层但**互斥**（视图在展开前先关掉整卡预览，
    /// 保证同一时刻只有一个浮层）。位置优先放在宿主**上方**（不遮住宿主与争夺区），空间不足则放下方，
    /// 最后统一钳进视口。
    ///
    /// 出口：
    ///  · 单击成员 → `onPick(uid)`：调用方只把它转成 `CardPickWindow.SelectByUid`
    ///    （**只置选中、不提交**，仍需用户按「确认」）；没有选择窗时纯浏览。
    ///  · 双击成员 → `onActivate(cardUI)`：走既有双击激活链路（`Arena.OpenAttachmentActivateChoice`
    ///    / `ArenaOl.OnAttachmentDoubleClicked`），是否真的可发动、要不要取目标、横置与付费
    ///    全部由 Controller/服务端把关，本面板不写 Model。
    /// </summary>
    public partial class CardBadgePeek : Control
    {
        private const float CardScale = 0.55f;      // 展开态每张卡的缩放（约 77×108）
        /// <summary>无论视口多宽，一次最多列这么多张（再多就读不出是哪个了，宁可按视口宽度截断）。</summary>
        private const int MaxCards = 14;
        private const float Gap = 6f;
        private const float Pad = 6f;

        private PanelContainer _panel;
        private VBoxContainer _column;
        private Label _title;
        private HBoxContainer _row;
        private Action<string> _onPick;
        private Action<CardUI> _onActivate;
        private bool _mouseInside;
        private Control _host;

        /// <summary>当前面板依附的宿主卡（视图做"鼠标是否还在宿主上"判定用）。</summary>
        public Control Host => _host;

        public override void _Ready()
        {
            ZAsRelative = false;
            ZIndex = Layers.CardHoverPreview;
            MouseFilter = MouseFilterEnum.Stop;
            Visible = false;

            _panel = new PanelContainer();
            _panel.AddThemeStyleboxOverride("panel", BuildStyle());
            AddChild(_panel);

            _column = new VBoxContainer();
            _column.AddThemeConstantOverride("separation", 4);
            _panel.AddChild(_column);

            // 标题：明确"这一排是封印卡还是附属卡"（两者来自不同区域，不混排）
            _title = new Label
            {
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _title.AddThemeFontSizeOverride("font_size", 12);
            _title.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.75f));
            _column.AddChild(_title);

            _row = new HBoxContainer();
            _row.AddThemeConstantOverride("separation", (int)Gap);
            _column.AddChild(_row);

            MouseEntered += () => _mouseInside = true;
            MouseExited += () => { _mouseInside = false; };
        }

        /// <summary>面板范围是否包含该全局点（供视图判断"鼠标是否还在展开区内"）。</summary>
        public bool ContainsGlobalPoint(Vector2 globalPos)
            => Visible && GetGlobalRect().Grow(4f).HasPoint(globalPos);

        /// <summary>
        /// 展开：在 host 旁边列出 members。
        /// 一次能列多少张**按视口宽度算**（不再写死 6 张）：张数超出可视宽度才截断，并提示总张数。
        /// </summary>
        /// <param name="title">标题（"封印卡" / "附属卡" / "资产" / "墓地"），一次只列一种来源。</param>
        /// <param name="onPick">单击：置选中（不提交）。</param>
        /// <param name="onActivate">双击：发动激活能力（由调用方走既有双击链路）。</param>
        public void ShowNear(Control host, string title, IReadOnlyList<CardInstance> members,
                             Action<string> onPick, Action<CardUI> onActivate)
        {
            if (host == null || !GodotObject.IsInstanceValid(host) || members == null || members.Count == 0) return;

            _host = host;
            _onPick = onPick;
            _onActivate = onActivate;
            _title.Text = title ?? string.Empty;
            _title.Visible = !string.IsNullOrEmpty(title);

            foreach (Node child in _row.GetChildren())
            {
                _row.RemoveChild(child);
                child.QueueFree();
            }

            var scene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
            float cardW = CardLayoutHelper.CARD_WIDTH * CardScale;
            float cardH = CardLayoutHelper.CARD_HEIGHT * CardScale;
            // 张数按视口宽度算（视口越宽列越多），再受 MaxCards 封顶；超出部分用尾部提示交代总数。
            float viewportW = GetViewportRect().Size.X;
            int fit = Mathf.Max(1, (int)((viewportW - 24f + Gap) / (cardW + Gap)));
            int shown = Math.Min(members.Count, Math.Min(fit, MaxCards));

            for (int i = 0; i < shown; i++)
            {
                var inst = members[i];
                if (inst == null) continue;

                // 定尺寸卡面槽：CardUI 直接进容器会被横向拉满（见 CardPickWindow 注释）
                var slot = new Control { CustomMinimumSize = new Vector2(cardW, cardH), MouseFilter = MouseFilterEnum.Pass };
                _row.AddChild(slot);

                if (scene == null) continue;
                var ui = scene.Instantiate<CardUI>();
                ui.CanDrag = false;
                ui.FreezeSmoothMove = true;
                slot.AddChild(ui);
                ui.PivotOffset = Vector2.Zero;
                ui.Scale = new Vector2(CardScale, CardScale);
                ui.Position = Vector2.Zero;
                ui.Setup(inst);
                ui.SetFace(!inst.IsHidden);

                string uid = inst.UID;
                var capturedUi = ui;
                ui.GuiInput += ev =>
                {
                    if (ev is not InputEventMouseButton mb) return;
                    if (!mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;
                    // 双击＝发动激活能力（JC020 等被折叠的附属卡唯一入口）；单击＝置选中（不提交）
                    if (mb.DoubleClick) _onActivate?.Invoke(capturedUi);
                    else _onPick?.Invoke(uid);
                };
            }

            if (members.Count > shown)
                _row.AddChild(new Label
                {
                    Text = $"…（共 {members.Count} 张）",
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = MouseFilterEnum.Ignore,
                });

            // 先量尺寸再定位：标题 + 卡列的合并最小尺寸此时已可用
            Vector2 content = _column.GetCombinedMinimumSize();
            Vector2 want = new Vector2(content.X + Pad * 2f, content.Y + Pad * 2f);

            var viewport = GetViewportRect().Size;
            Vector2 hostPos = host.GlobalPosition;
            Vector2 hostSize = host.Size;
            float x = hostPos.X + hostSize.X / 2f - want.X / 2f;
            float y = hostPos.Y - want.Y - 8f;                 // 优先放上方
            if (y < 4f) y = hostPos.Y + hostSize.Y + 8f;       // 上方放不下 → 下方
            x = Mathf.Clamp(x, 4f, Math.Max(4f, viewport.X - want.X - 4f));
            y = Mathf.Clamp(y, 4f, Math.Max(4f, viewport.Y - want.Y - 4f));

            Position = new Vector2(x, y);
            Size = want;
            Visible = true;
        }

        public void HideNow()
        {
            if (!Visible) return;
            _mouseInside = false;
            Visible = false;
        }

        public bool IsOpen => Visible && _mouseInside;

        private static StyleBoxFlat BuildStyle()
        {
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.08f, 0.10f, 0.94f),
                BorderColor = new Color(0.85f, 0.75f, 0.45f, 0.9f),
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(6);
            sb.SetContentMarginAll(Pad);
            return sb;
        }
    }
}
