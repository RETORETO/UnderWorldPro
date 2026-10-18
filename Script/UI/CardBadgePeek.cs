using Godot;
using Script.Game.Entities;
using System;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 【P3b·2026-10-18】角标悬浮展开面板：在宿主卡旁边横向列出被折叠的成员卡面。
    ///
    /// 定位＝`Layers.CardHoverPreview`（830），与整卡预览同层但**互斥**（视图在展开前先关掉整卡预览，
    /// 保证同一时刻只有一个浮层）。位置优先放在宿主**上方**（不遮住宿主与争夺区），空间不足则放下方，
    /// 最后统一钳进视口。
    ///
    /// 出口：点击成员 → 回调 `onPick(uid)`。调用方（战场视图）只把它转成
    /// `CardPickWindow.SelectByUid`（**只置选中、不提交**，仍需用户按「确认」）；没有选择窗时纯浏览。
    /// </summary>
    public partial class CardBadgePeek : Control
    {
        private const float CardScale = 0.55f;      // 展开态每张卡的缩放（约 77×108）
        private const int MaxCards = 6;
        private const float Gap = 6f;
        private const float Pad = 6f;

        private PanelContainer _panel;
        private HBoxContainer _row;
        private Action<string> _onPick;
        private bool _mouseInside;

        public override void _Ready()
        {
            ZAsRelative = false;
            ZIndex = Layers.CardHoverPreview;
            MouseFilter = MouseFilterEnum.Stop;
            Visible = false;

            _panel = new PanelContainer();
            _panel.AddThemeStyleboxOverride("panel", BuildStyle());
            AddChild(_panel);

            _row = new HBoxContainer();
            _row.AddThemeConstantOverride("separation", (int)Gap);
            _panel.AddChild(_row);

            MouseEntered += () => _mouseInside = true;
            MouseExited += () => { _mouseInside = false; };
        }

        /// <summary>面板范围是否包含该全局点（供视图判断"鼠标是否还在展开区内"）。</summary>
        public bool ContainsGlobalPoint(Vector2 globalPos)
            => Visible && GetGlobalRect().Grow(4f).HasPoint(globalPos);

        /// <summary>展开：在 host 旁边列出 members（超出 6 张只列前 6 张并加「…」提示）。</summary>
        public void ShowNear(Control host, IReadOnlyList<CardInstance> members, Action<string> onPick)
        {
            if (host == null || !GodotObject.IsInstanceValid(host) || members == null || members.Count == 0) return;

            _onPick = onPick;
            foreach (Node child in _row.GetChildren())
            {
                _row.RemoveChild(child);
                child.QueueFree();
            }

            var scene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
            int shown = Math.Min(members.Count, MaxCards);
            float cardW = CardLayoutHelper.CARD_WIDTH * CardScale;
            float cardH = CardLayoutHelper.CARD_HEIGHT * CardScale;

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
                ui.GuiInput += ev => { if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left) _onPick?.Invoke(uid); };
            }

            if (members.Count > shown)
                _row.AddChild(new Label { Text = "…", VerticalAlignment = VerticalAlignment.Center });

            // 先量尺寸再定位：PanelContainer 的合并最小尺寸此时已可用
            Vector2 want = new Vector2(_row.GetCombinedMinimumSize().X + Pad * 2f,
                                       cardH + Pad * 2f);

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
