using System;
using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【联机专用·「支付/横置」二选一窗口】对齐单机 `Arena.PromptPayOrTap` 的弹窗（JC068 阿格斯IV「准备维护」）。
    ///
    /// 与单机 `ActivationChoiceWindow`(ForceChoice=true) 的**视觉与操作一致**：两个选项、费用不足时"支付"置灰、
    /// 强制选择（无取消按钮、点窗口外不关闭）。差异仅在**交互落点**：
    ///  - 单机选项回调直接调 `EffectEngine.ResolvePrepareUpkeep` 落地模型；
    ///  - 联机不做任何本地结算，选中后把 `ChoiceResult{Pay=...}` 交回 ArenaOl 上行，由服务端落地。
    ///
    /// 本类为纯 View：不引用任何 Manager，不写 Model；文案与置灰状态由调用方（ArenaOl）注入。
    /// </summary>
    [GlobalClass]
    public partial class OnlinePayOrTapWindow : Window
    {
        private VBoxContainer _buttonBox;
        private Action<bool> _onResult;
        private bool _payDisabled;
        private bool _dismissed;

        public override void _Ready()
        {
            Title = "准备维护";

            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            _buttonBox = new VBoxContainer();
            _buttonBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _buttonBox.OffsetTop = 30;   // 给标题栏留空间
            _buttonBox.AddThemeConstantOverride("separation", 8);
            root.AddChild(_buttonBox);

            // 不连接 FocusExited：点窗口外不关闭（强制选择，必须二选一）——与单机 ForceChoice=true 一致。
            Size = new Vector2I(460, 160);
        }

        /// <summary>
        /// 初始化：title=窗口标题；payLabel/tapLabel=两个选项文案；payDisabled=服务端判定的"支付得不起"（置灰）；
        /// onResult=玩家选择回包（true=支付，false=横置）。
        /// </summary>
        public void Init(string title, string payLabel, bool payDisabled, string tapLabel, Action<bool> onResult)
        {
            if (_buttonBox == null) return;   // _Ready 未跑（未入树）时忽略，调用方在 AddChild 之后即可正常 Init
            if (!string.IsNullOrEmpty(title)) Title = title;
            _payDisabled = payDisabled;
            _onResult = onResult;

            foreach (Node child in _buttonBox.GetChildren())
            {
                _buttonBox.RemoveChild(child);
                child.QueueFree();
            }

            var pay = new Button { Text = payLabel, Disabled = payDisabled };
            pay.Pressed += () => Complete(true);
            _buttonBox.AddChild(pay);

            var tap = new Button { Text = tapLabel };
            tap.Pressed += () => Complete(false);
            _buttonBox.AddChild(tap);

            Size = new Vector2I(460, Mathf.Clamp(70 + _buttonBox.GetChildCount() * 46, 150, 640));
        }

        /// <summary>支付选项是否处于置灰（供外部/测试查询）。</summary>
        public bool PayDisabled => _payDisabled;

        private void Complete(bool pay)
        {
            if (_dismissed) return;
            if (pay && _payDisabled) return;   // 置灰项不可选（双重防呆）
            _dismissed = true;
            var cb = _onResult;
            QueueFree();
            cb?.Invoke(pay);
        }
    }
}
