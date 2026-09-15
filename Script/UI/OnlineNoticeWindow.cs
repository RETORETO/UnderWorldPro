using System;
using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【联机专用·提示窗】用于"需要明确告知玩家"的场景（如服务端拒绝意图 IntentResult.Success=false）。
    /// 窗口语义与 OnlineActivationChoiceWindow 一致：不连接 FocusExited（点窗口外不关闭），只经"确定"按钮关闭。
    /// </summary>
    [GlobalClass]
    public partial class OnlineNoticeWindow : Window
    {
        private Label _messageLabel;
        private bool _dismissed = false;

        /// <summary>关闭（点"确定"）时回调，供调用方复位模态标记。</summary>
        public Action OnClosed;

        public override void _Ready()
        {
            Title = "提示";
            Size = new Vector2I(480, 180);

            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            var box = new VBoxContainer();
            box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            box.OffsetLeft = 20;
            box.OffsetTop = 36;
            box.OffsetRight = -20;
            box.OffsetBottom = -20;
            box.AddThemeConstantOverride("separation", 12);
            root.AddChild(box);

            _messageLabel = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            _messageLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(_messageLabel);

            var ok = new Button { Text = "确定" };
            ok.Pressed += Close;
            box.AddChild(ok);
        }

        /// <summary>设置提示文案（须在 AddChild 之后调用，确保 _Ready 已建好控件）。</summary>
        public void Init(string message)
        {
            if (_messageLabel != null) _messageLabel.Text = message ?? "";
        }

        /// <summary>关闭窗口（仅此路径；点窗外不关闭，保持联机窗口纪律）。</summary>
        private void Close()
        {
            if (_dismissed) return;
            _dismissed = true;
            var cb = OnClosed;
            QueueFree();
            cb?.Invoke();
        }
    }
}
