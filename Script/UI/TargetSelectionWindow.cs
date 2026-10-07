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

        /// <summary>
        /// 【K15·多目标·场上点选】"备用出口"回调：本槽可留空 → 留空该槽；本槽必选 → 取消整次打出。
        /// 由调用方经 `Open(..., extraLabel)` 给出按钮文案后才会启用（见 `Open`）。
        /// </summary>
        public System.Action OnExtra;

        /// <summary>
        /// 【2026-09 用户口径 JC062·多选模式】"确认（加入堆叠）"回调：**已选清单由协调器持有**，
        /// 窗口只负责转发一次"玩家点了确认"（清单在 `TriggerInteractionCoordinator` 里收集）。
        /// 多选模式下窗口**不自动关闭**（玩家可反复在场上点选/取消），只有确认/取消两个出口。
        /// </summary>
        public System.Action OnMultiConfirmed;

        /// <summary>【2026-09 用户口径 JC062·多选模式】"取消（不发动）"回调。</summary>
        public System.Action OnCancelled;

        private string _pickedUid;
        private Button _cancelButton;
        private bool _multiPick;

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

        /// <summary>
        /// 【2026-09 用户口径 JC062/JC021·修复】"**场上高亮 + 一个窗（左键选择、右键取消）**"模式。
        /// 用户给出的窗面：
        /// <code>
        /// 请选择目标暗藏者：已选择（0/2）      ← 标题（由调用方按已选数量实时重算）
        /// 左键选择右键取消                      ← 提示
        /// 确认   取消
        /// </code>
        /// 与单选取目标的区别：窗口**保持打开**（不点选即关闭），玩家左键加选、右键取消，
        /// 最后按"确认"或"取消"；"确认"的可用性由调用方判定（JC021：必须已选地区且至少 1 个角色）。
        /// </summary>
        public void OpenMultiPick(string hint)
        {
            SetMultiPickLayout(true);
            _multiPick = true;
            _pickedUid = null;
            if (ConfirmButton != null) ConfirmButton.Text = "确认";
            if (_cancelButton != null) _cancelButton.Text = "取消";
            if (HintLabel != null) HintLabel.Text = string.IsNullOrEmpty(hint) ? "左键选择右键取消" : hint;
            var vp = GetViewport().GetVisibleRect().Size;
            Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)((vp.Y - Size.Y) / 2) + 120);
            Popup();
        }

        /// <summary>多选模式：每次选择变化后刷新标题（含计数）/"左键选择右键取消"提示/"确认"可用性。</summary>
        public void SetMultiPick(string hint, string title, bool confirmEnabled)
        {
            if (TitleLabel != null) TitleLabel.Text = string.IsNullOrEmpty(title) ? "请选择目标" : title;
            if (HintLabel != null) HintLabel.Text = string.IsNullOrEmpty(hint) ? "左键选择右键取消" : hint;
            if (ConfirmButton != null) ConfirmButton.Disabled = !confirmEnabled;
        }

        /// <summary>单选取目标的布局（把多选模式留下的布局/按钮还原，保证既有流程逐字不变）。</summary>
        private void SetMultiPickLayout(bool multi)
        {
            if (multi)
            {
                Size = new Vector2I(560, 240);
                if (TitleLabel != null)
                {
                    TitleLabel.OffsetLeft = 16; TitleLabel.OffsetTop = 12;
                    TitleLabel.OffsetRight = 544; TitleLabel.OffsetBottom = 44;
                }
                if (HintLabel != null)
                {
                    HintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    HintLabel.OffsetLeft = 16; HintLabel.OffsetTop = 50;
                    HintLabel.OffsetRight = 544; HintLabel.OffsetBottom = 164;
                }
                if (ConfirmButton != null)
                {
                    ConfirmButton.OffsetLeft = 16; ConfirmButton.OffsetTop = 176;
                    ConfirmButton.OffsetRight = 270; ConfirmButton.OffsetBottom = 212;
                }
                if (_cancelButton == null)
                {
                    _cancelButton = new Button { Text = "取消（不发动）" };
                    _cancelButton.Pressed += OnCancelPressed;
                    AddChildToPanel(_cancelButton);
                }
                _cancelButton.Visible = true;
                _cancelButton.OffsetLeft = 290; _cancelButton.OffsetTop = 176;
                _cancelButton.OffsetRight = 544; _cancelButton.OffsetBottom = 212;
                return;
            }
            // 单选取目标：还原场景原始布局/尺寸
            _multiPick = false;
            Size = new Vector2I(500, 150);
            if (TitleLabel != null)
            {
                TitleLabel.OffsetLeft = 16; TitleLabel.OffsetTop = 16;
                TitleLabel.OffsetRight = 486; TitleLabel.OffsetBottom = 48;
            }
            if (HintLabel != null)
            {
                HintLabel.OffsetLeft = 16; HintLabel.OffsetTop = 56;
                HintLabel.OffsetRight = 486; HintLabel.OffsetBottom = 100;
            }
            if (ConfirmButton != null)
            {
                ConfirmButton.OffsetLeft = 16; ConfirmButton.OffsetTop = 108;
                ConfirmButton.OffsetRight = 486; ConfirmButton.OffsetBottom = 144;
            }
            if (_cancelButton != null) _cancelButton.Visible = false;
        }

        private void AddChildToPanel(Control child)
        {
            var panel = ConfirmButton?.GetParent() ?? GetNodeOrNull<Control>("Panel");
            (panel ?? (Node)this).AddChild(child);
        }

        /// <summary>【2026-09 用户口径 JC062】"取消（不发动）"：关闭窗口并回调。</summary>
        private void OnCancelPressed()
        {
            _multiPick = false;
            Hide();
            var cb = OnCancelled;
            OnCancelled = null;
            OnMultiConfirmed = null;
            cb?.Invoke();
        }

        /// <summary>多选模式下：确认（已选清单在协调器里，窗口只通知"点了确认"；可空＝一个都不选）。</summary>
        public void ConfirmMultiPick()
        {
            _multiPick = false;
            Hide();
            var cb = OnMultiConfirmed;
            OnMultiConfirmed = null;
            OnCancelled = null;
            cb?.Invoke();
        }

        /// <summary>
        /// 打开提示窗。
        /// 【2026-09·K15 场上点选】`extraLabel` 非空时，把窗口自带的备用按钮改成**该槽的出口**：
        ///  · 可留空的槽 → "不选（留空该槽）"（回调 `OnExtra`）；
        ///  · 必选槽 → "取消（不打出）"（回调 `OnExtra`）。
        /// 取目标仍然发生在**场上高亮目标**上（点击即选中并关闭），本按钮只是退出通道 —— 用户口径：
        /// "选择查看的暗藏者应该选择场上的高亮暗藏者，而不是弹出选择框"。
        /// </summary>
        public void Open(string effectName, string hint, string extraLabel = null)
        {
            SetMultiPickLayout(false);   // 还原单选取目标布局（多选模式可能改过尺寸/按钮）
            // 水平居中、顶边位于实际视口垂直中点偏下 150px
            var vp = GetViewport().GetVisibleRect().Size;
            Position = new Vector2I((int)((vp.X - Size.X) / 2), (int)(vp.Y / 2) + 150);
            Popup();
            _pickedUid = null;
            if (TitleLabel != null) TitleLabel.Text = $"选择目标：{effectName}";
            if (HintLabel != null) HintLabel.Text = hint;
            if (ConfirmButton != null)
            {
                bool hasExtra = !string.IsNullOrEmpty(extraLabel);
                ConfirmButton.Disabled = !hasExtra;
                ConfirmButton.Text = hasExtra ? extraLabel : "（点场上卡后自动关闭）";
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

        /// <summary>保留确认按钮作为回车/备用确认路径；当前主流程已改为点选即确认。
        /// 【2026-09·K15】未点选任何目标时，按下本按钮＝执行该槽的**出口**（留空该槽 / 取消不打出）。
        /// 【2026-09·JC062 多选模式】按下＝"确认已选清单"（由 `TriggerInteractionCoordinator` 处理）。</summary>
        private void OnConfirmPressed()
        {
            if (_multiPick)
            {
                // 多选：已选清单由协调器持有 ⇒ 这里只转发"点了确认"
                if (OnMultiConfirmed == null) return;
                ConfirmMultiPick();
                return;
            }
            if (!string.IsNullOrEmpty(_pickedUid))
            {
                PickFromArena(_pickedUid);
                return;
            }
            var extra = OnExtra;
            OnExtra = null;
            if (extra == null) return;
            _pickedUid = null;
            Hide();
            extra();
        }

        public void Close()
        {
            Hide();
        }
    }
}
