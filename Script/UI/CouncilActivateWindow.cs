using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Script.UI
{
    /// <summary>
    /// 秘社双击激活弹窗：列出该秘社的多个激活选项（ActivationOptions），供玩家选择其一发动或取消。
    /// 由 Arena.OnCouncilDoubleClicked 实例化并调用 Init(options, onPick)。
    /// 各选项的费用展示沿用 opt.OptionName 中的"N费"文案（其数值来源为 EffectData.Cost）。
    /// </summary>
    [GlobalClass]
    public partial class CouncilActivateWindow : Window
    {
        private VBoxContainer _buttonBox;
        private Array<CouncilActivationOption> _options;
        private System.Action<CouncilActivationOption> _onPick;

        // 窗口是否已通过"选项/取消"正常关闭，避免 FocusExited 重复释放
        private bool _dismissed = false;

        public override void _Ready()
        {
            // 标题栏
            Title = "秘社激活";

            // 主体容器（垂直排列按钮）
            var root = GetChildOrNull<Control>(0);
            if (root == null)
            {
                root = new Control();
                AddChild(root);
            }
            _buttonBox = new VBoxContainer();
            _buttonBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _buttonBox.OffsetTop = 30; // 给标题栏留空间
            _buttonBox.AddThemeConstantOverride("separation", 8);
            AddChild(_buttonBox);

            // 取消按钮（始终在最后）：等同点击窗口外，视为取消
            var cancel = new Button { Text = "取消" };
            cancel.Pressed += Dismiss;
            _buttonBox.AddChild(cancel);

            // 窗口尺寸
            Size = new Vector2I(320, 200);

            // 点击窗口外任意位置（焦点离开窗口）自动关闭，等价于取消。
            // 用 CallDeferred 延迟到本帧布局/焦点稳定后再连接，避免弹出当帧因焦点状态不确定而误关。
            this.CallDeferred(MethodName.ConnectFocusExited);
        }

        private void ConnectFocusExited()
        {
            FocusExited += OnFocusExited;
        }

        private void OnFocusExited()
        {
            // 若已通过选项/取消正常关闭，则不再处理
            if (_dismissed) return;
            _dismissed = true;
            QueueFree();
        }

        /// <summary>正常关闭窗口（选项/取消触发），先置标志防止 FocusExited 重复释放。</summary>
        private void Dismiss()
        {
            if (_dismissed) return;
            _dismissed = true;
            QueueFree();
        }

        /// <summary>
        /// 初始化弹窗。
        /// options：秘社的激活选项列表；onPick：玩家点击某选项后的回调（传入被选选项），取消不触发。
        /// </summary>
        public void Init(Array<CouncilActivationOption> options, System.Action<CouncilActivationOption> onPick)
        {
            _options = options;
            _onPick = onPick;

            // 先移除旧的选项按钮（保留最后的取消按钮）
            for (int i = _buttonBox.GetChildCount() - 2; i >= 0; i--)
            {
                var c = _buttonBox.GetChild(i);
                _buttonBox.RemoveChild(c);
                c.QueueFree();
            }

            int idx = 0;
            foreach (var opt in options)
            {
                if (opt == null) continue;
                int captured = idx;
                var btn = new Button { Text = opt.OptionName };
                btn.Pressed += () =>
                {
                    _onPick?.Invoke(_options[captured]);
                    Dismiss();
                };
                _buttonBox.AddChild(btn);
                idx++;
            }
        }
    }
}
