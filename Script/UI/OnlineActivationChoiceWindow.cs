using System;
using System.Collections.Generic;
using Godot;

namespace Script.UI
{
    /// <summary>
    /// 【联机专用·激活效果选择窗口】双击附属卡/秘社卡出现多个可激活效果/选项时，由 ArenaOl 实例化，
    /// 列出互斥选项供玩家选择其一发动，或点"取消"放弃。
    ///
    /// 与单机 ActivationChoiceWindow / CouncilActivateWindow 的差异（联机规则）：
    ///  - 不响应 FocusExited：点窗口外不关闭；
    ///  - 取消只经显式"取消"按钮，且把取消通知回调用方（ArenaOl 据此复位本地选择态）。
    /// 输入复用共享数据类 <see cref="ActivationChoiceItem"/>（Label/Disabled/OnChosen），不改该类。
    /// </summary>
    [GlobalClass]
    public partial class OnlineActivationChoiceWindow : Window
    {
        private VBoxContainer _buttonBox;
        private bool _dismissed = false;

        /// <summary>玩家点"取消"（放弃发动）时回调。选中某选项走对应 item.OnChosen，不触发本回调。</summary>
        public Action OnCancelled;

        public override void _Ready()
        {
            Title = "选择激活效果";

            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            _buttonBox = new VBoxContainer();
            _buttonBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _buttonBox.OffsetTop = 30;   // 给标题栏留空间
            _buttonBox.AddThemeConstantOverride("separation", 8);
            root.AddChild(_buttonBox);

            Size = new Vector2I(460, 160);
        }

        /// <summary>初始化选项列表。置灰项（Disabled=true）灰显且不可点；取消按钮始终在最后。</summary>
        public void Init(List<ActivationChoiceItem> items)
        {
            foreach (Node child in _buttonBox.GetChildren())
            {
                _buttonBox.RemoveChild(child);
                child.QueueFree();
            }

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null) continue;
                    int captured = i;
                    var btn = new Button { Text = it.Label, Disabled = it.Disabled };
                    btn.Pressed += () =>
                    {
                        if (items[captured]?.Disabled == true) return;   // 置灰项不可发动（双重防呆）
                        ChooseAndClose(items[captured]?.OnChosen);
                    };
                    _buttonBox.AddChild(btn);
                }
            }

            var cancel = new Button { Text = "取消" };
            cancel.Pressed += () =>
            {
                var cb = OnCancelled;
                CloseQuiet();
                cb?.Invoke();
            };
            _buttonBox.AddChild(cancel);

            int count = _buttonBox.GetChildCount();
            Size = new Vector2I(460, Mathf.Clamp(70 + count * 46, 150, 640));
        }

        private void ChooseAndClose(Action chosen)
        {
            if (_dismissed) return;
            _dismissed = true;
            chosen?.Invoke();
            QueueFree();
        }

        /// <summary>静默关闭（取消路径），先置标志防重复释放。</summary>
        private void CloseQuiet()
        {
            if (_dismissed) return;
            _dismissed = true;
            QueueFree();
        }
    }
}
