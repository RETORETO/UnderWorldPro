using Godot;
using System;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 泛用激活/选择分支项：弹窗中一个可点击选项。
    /// 不绑定具体卡类型（秘社/附属/触发皆可复用），由调用方提供显示文字与选中回调。
    /// </summary>
    public class ActivationChoiceItem
    {
        /// <summary>按钮显示文字（如"①费用-1：本回合下一张帷幕守望费用减少"）。</summary>
        public string Label;
        /// <summary>选中该选项后的回调（发动对应效果）。</summary>
        public Action OnChosen;
        /// <summary>是否置灰禁用（如场上无合法目标时）。置灰项点击不触发 OnChosen。</summary>
        public bool Disabled;
    }

    /// <summary>
    /// 泛用激活选择弹窗：列出若干互斥选项供玩家选择其一发动，或取消。
    /// 与 CouncilActivateWindow 行为一致（模态、点击窗外即取消），但不依赖 CouncilActivationOption，
    /// 未来所有"有选择分支的卡/效果"均复用此窗口。
    /// 由 Arena 实例化并调用 Init(items)。
    /// </summary>
    [GlobalClass]
    public partial class ActivationChoiceWindow : Window
    {
        private VBoxContainer _buttonBox;
        private List<ActivationChoiceItem> _items;

        // 窗口是否已通过"选项/取消"正常关闭，避免 FocusExited 重复释放
        private bool _dismissed = false;

        /// <summary>强制选择模式：为强制触发效果（如 JC068 阿格斯IV准备维护）使用。
        /// 开启后：不显示"取消"按钮，点击窗口外不关闭窗口——玩家必须二选一才能继续。
        /// 默认 false：保持原行为（可取消、点窗外即关闭），不影响其他非强制效果。</summary>
        public bool ForceChoice = false;

        /// <summary>【P2】自定义窗口标题（如"从牌库中寻找一张法术"）。空 = 用默认标题"选择激活效果"。</summary>
        public string CustomTitle;

        private void EnsureButtonBox()
        {
            if (_buttonBox != null) return;
            _buttonBox = new VBoxContainer();
            _buttonBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _buttonBox.OffsetTop = 30; // 给标题栏留空间
            _buttonBox.AddThemeConstantOverride("separation", 8);
            AddChild(_buttonBox);

            // 强制选择模式：不提供取消按钮（玩家必须选一项才能继续）
            if (ForceChoice) return;
            // 取消按钮（始终在最后）：等同点击窗口外，视为取消
            var cancel = new Button { Text = "取消" };
            cancel.Pressed += Dismiss;
            _buttonBox.AddChild(cancel);
        }

        public override void _Ready()
        {
            Title = string.IsNullOrEmpty(CustomTitle) ? "选择激活效果" : CustomTitle;
            EnsureButtonBox();
            Size = new Vector2I(420, 120);

            // 点击窗口外任意位置（焦点离开窗口）自动关闭，等价于取消。
            CallDeferred("ConnectFocusExited");
        }

        private void ConnectFocusExited()
        {
            FocusExited += OnFocusExited;
        }

        private void OnFocusExited()
        {
            if (_dismissed) return;
            // 强制选择模式：点击窗口外不关闭，玩家必须选一项（选项/取消之外无关闭途径）
            if (ForceChoice) return;
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
        /// 初始化弹窗。items：选项列表（Label+OnChosen）；取消不触发任何回调。
        /// </summary>
        public void Init(List<ActivationChoiceItem> items)
        {
            EnsureButtonBox();
            _items = items ?? new List<ActivationChoiceItem>();

            // 先移除旧选项按钮（保留最后的取消按钮）
            for (int i = _buttonBox.GetChildCount() - 2; i >= 0; i--)
            {
                var c = _buttonBox.GetChild(i);
                _buttonBox.RemoveChild(c);
                c.QueueFree();
            }

            int idx = 0;
            foreach (var item in _items)
            {
                if (item == null) continue;
                int captured = idx;
                var btn = new Button { Text = item.Label, Disabled = item.Disabled };
                btn.Pressed += () =>
                {
                    if (_items[captured].Disabled) return; // 置灰项不可发动
                    _items[captured].OnChosen?.Invoke();
                    Dismiss();
                };
                _buttonBox.AddChild(btn);
                idx++;
            }
        }
    }
}
