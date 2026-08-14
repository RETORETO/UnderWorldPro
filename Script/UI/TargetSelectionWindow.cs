using Godot;

namespace Script.UI
{
    /// <summary>
    /// 选择目标提示窗口（模态独占）。
    /// 规则（来自最新需求 第四节）：
    ///  - 触发效果需要指定目标时出现，模态独占、阻塞按钮。
    ///  - 玩家仅可选择场上的目标 UI（卡 UI 或高亮地区槽位），无合法目标时不显示本窗口（由调用方保证）。
    ///  - 选择目标后窗口立即关闭并回传 UID，触发效果随之加入堆叠，无需二次确认。
    /// </summary>
    public partial class TargetSelectionWindow : Window
    {
        [Export] public Label TitleLabel;
        [Export] public Label HintLabel;
        [Export] public Button ConfirmButton;

        /// <summary>玩家在场上点选目标卡后，由 Arena 转发触发；参数为目标卡 UID。</summary>
        public System.Action<string> OnTargetPicked;

        private string _pickedUid;

        // 全屏遮罩层：选目标时需要点击穿透到下层（场上卡/地区槽位），故默认不阻塞。
        private ColorRect _blockLayer;

        public override void _Ready()
        {
            if (ConfirmButton != null) ConfirmButton.Pressed += OnConfirmPressed;
            _blockLayer = GetNodeOrNull<ColorRect>("BlockLayer");
        }

        /// <summary>
        /// 控制全屏遮罩层是否拦截点击。
        /// 选目标流程需点击穿透到下层（场上卡/地区槽位），故调用方应设为 false（穿透）。
        /// </summary>
        public void SetBlocking(bool block)
        {
            if (_blockLayer != null)
                _blockLayer.MouseFilter = block ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        }

        public void Open(string effectName, string hint)
        {
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vp = GetViewport().GetVisibleRect().Size;
            Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)(vp.Y / 2) + 150);
            Popup();
            _pickedUid = null;
            if (TitleLabel != null) TitleLabel.Text = $"选择目标：{effectName}";
            if (HintLabel != null) HintLabel.Text = hint;
            if (ConfirmButton != null)
            {
                ConfirmButton.Disabled = true;
                ConfirmButton.Text = "（点场上卡后自动关闭）";
            }
        }

        /// <summary>
        /// 由 Arena 在玩家点选合法目标（卡或地区槽位）时调用。
        /// 选择后立即关闭窗口并回传目标 UID，触发效果随之加入堆叠，无需二次确认。
        /// </summary>
        public void PickFromArena(string targetCardUid)
        {
            _pickedUid = targetCardUid;
            if (ConfirmButton != null)
            {
                ConfirmButton.Disabled = true;
                ConfirmButton.Text = "目标已选择，自动入堆叠…";
            }
            Hide();
            OnTargetPicked?.Invoke(_pickedUid);
            _pickedUid = null;
        }

        /// <summary>保留确认按钮作为回车/备用确认路径；当前主流程已改为点选即确认。</summary>
        private void OnConfirmPressed()
        {
            if (string.IsNullOrEmpty(_pickedUid)) return;
            PickFromArena(_pickedUid);
        }

        public void Close()
        {
            Hide();
        }
    }
}
