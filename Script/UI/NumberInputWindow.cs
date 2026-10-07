using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【K11·可选支付 / 数值输入】通用**数值输入窗口**（程序化构建，不需要 .tscn）：
    /// 一个提示标签 + 一个数字输入框 + `确认` / `取消` 两个按钮。
    ///
    /// 语义（用户 2026-09 口径）：玩家**输入一个数字 N**（钳制到 `[Min,Max]`），再按确认（如"支付"）或取消（如"不支付"）；
    /// 取消 / 关闭窗口 → 回调 `-1`。
    /// View 只收集数字，**不写 Model**：扣费与后续动作由 Controller（EffectEngine）按回调结果执行。
    /// </summary>
    public partial class NumberInputWindow : Window
    {
        /// <summary>可选的最小值（含）。</summary>
        public int Min;

        /// <summary>可选的最大值（含）。</summary>
        public int Max = 99;

        /// <summary>确认回调：玩家输入的数字；-1＝取消/放弃。</summary>
        public System.Action<int> OnConfirmed;

        private LineEdit _input;
        private Label _hint;
        private bool _done;

        /// <summary>构建窗口内容（在 `AddChild` 之后、`Popup` 之前调用）。</summary>
        public void Init(string prompt, int min, int max, int defaultValue, string confirmLabel, string cancelLabel)
        {
            Title = prompt ?? "输入数值";
            Min = min;
            Max = System.Math.Max(min, max);

            var panel = new PanelContainer();
            AddChild(panel);
            var vb = new VBoxContainer();
            vb.AddThemeConstantOverride("separation", 8);
            panel.AddChild(vb);

            if (!string.IsNullOrEmpty(prompt))
            {
                _hint = new Label { Text = prompt, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                _hint.CustomMinimumSize = new Vector2(320, 0);
                vb.AddChild(_hint);
            }

            _input = new LineEdit
            {
                Text = System.Math.Clamp(defaultValue, Min, Max).ToString(),
                Alignment = HorizontalAlignment.Center,
                CustomMinimumSize = new Vector2(120, 0),
            };
            vb.AddChild(_input);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            vb.AddChild(row);

            var ok = new Button { Text = string.IsNullOrEmpty(confirmLabel) ? "确认" : confirmLabel };
            ok.Pressed += () => Confirm();
            row.AddChild(ok);

            var cancel = new Button { Text = string.IsNullOrEmpty(cancelLabel) ? "取消" : cancelLabel };
            cancel.Pressed += () => Cancel();
            row.AddChild(cancel);

            // 回车＝确认
            _input.TextSubmitted += _ => Confirm();
            CloseRequested += Cancel;

            // 【2026-09 修复·同 InspectWindow】**显式定窗口尺寸**：Godot `Window` 默认 100×100 且
            // `wrap_controls=false` 不会按内容撑开，而 `PopupCentered()` 只按当前 `Size` ⇒ 不设就会被裁。
            Size = new Vector2I(420, 200);
        }

        private void Confirm()
        {
            if (_done) return;
            _done = true;
            int value = Min;
            if (_input != null && int.TryParse(_input.Text?.Trim(), out var parsed)) value = parsed;
            value = System.Math.Clamp(value, Min, Max);
            OnConfirmed?.Invoke(value);
            QueueFree();
        }

        private void Cancel()
        {
            if (_done) return;
            _done = true;
            OnConfirmed?.Invoke(-1);
            QueueFree();
        }
    }
}
