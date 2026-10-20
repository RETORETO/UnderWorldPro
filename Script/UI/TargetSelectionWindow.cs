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

        /// <summary>【2026-10·触发窗口改造】单选取目标是否显示「取消」出口（由调用方经 Open 指定）。</summary>
        public bool AllowCancel { get; private set; }

        /// <summary>
        /// 【2026-10-20·强制多选】是否给了「取消」出口。为 false 时：
        ///  · 不显示取消按钮（如恢复步骤手牌上限弃牌："不能不丢" ⇒ 不能有"一张不丢"的按钮）；
        ///  · 标题栏关闭键被忽略（否则玩家一点 X 就让服务端永远等回包）。
        /// </summary>
        public bool HasCancelExit => _multiPick ? OnCancelled != null : AllowCancel;

        private string _pickedUid;
        private Button _cancelButton;
        private bool _multiPick;

        /// <summary>
        /// 【2026-10-20·用户口径】**本窗是否允许被关闭**（标题栏 X）。
        /// 强制流程（手牌上限弃牌、必须选一个目标的弃牌/取目标）**只能靠"完成选择"离开**：
        /// 关掉窗子既不能让流程推进、又会让玩家以为"选择已取消"，故直接忽略关闭请求。
        /// 判据：多选＝`showCancel`；单选＝有取消出口或该槽出口按钮（`extraLabel`）。
        /// </summary>
        private bool _closable = true;

        /// <summary>【2026-10 修复】本次 `Open` 发生的帧号：用于吞掉"开窗同帧的关闭请求"。</summary>
        private ulong _openedFrame = ulong.MaxValue;

        // 全屏遮罩层：选目标时需要点击穿透到下层（场上卡/地区槽位），故默认不阻塞。
        private ColorRect _blockLayer;

        public override void _Ready()
        {
            if (ConfirmButton != null) ConfirmButton.Pressed += OnConfirmPressed;
            _blockLayer = GetNodeOrNull<ColorRect>("BlockLayer");
            // 【2026-10·触发窗口改造】点标题栏关闭键 ＝ 「取消」：避免"窗被关掉但没有任何回调"
            // 导致调用方永久等待（触发入堆叠流程会因此被冻结）。
            CloseRequested += OnCloseRequested;
        }

        /// <summary>标题栏关闭键：优先按「取消」交付回调；没有取消回调时退回该槽的「出口」（K15 激活路径）。</summary>
        private void OnCloseRequested()
        {
            // 【2026-10 修复·JC049 实测】**开窗同帧的关闭请求一律忽略**：本窗常在"拖拽松手"的事件里被
            // 打开（`DragEnd → 额外费用窗`），松手/焦点切换会在同一帧触发一次 `close_requested`，
            // 于是窗口刚出现就自我取消 ⇒ 玩家看到"点了额外费用却没打出"。
            // 只吞**同帧**这一次，之后的真实关闭（点 X / 取消按钮）行为不变。
            if (Engine.GetProcessFrames() == _openedFrame)
            {
                GD.Print("[TargetSelectionWindow] 忽略开窗同帧的关闭请求（拖拽松手/焦点切换误触）");
                return;
            }
            // 【2026-10-20·用户口径】强制流程（没有任何取消/留空出口）⇒ 窗口关不掉。
            // 用户原文："丢弃提示窗不能通过丢牌以外方式关闭"。旧行为是"关窗就隐藏，但流程仍在等选择"，
            // 玩家会以为已经取消 ⇒ 这里保持窗口可见，直到玩家真正完成选择。
            if (!_closable)
            {
                GD.Print("[TargetSelectionWindow] 强制流程：忽略窗口关闭请求（必须完成本次选择）");
                return;
            }
            if (_multiPick)
            {
                // 【2026-10-20·强制多选】没有取消回调 ⇒ 这是"必须选够才能走"的流程（手牌上限弃牌）：
                // 窗口不提供任何退出路径，点标题栏 X 一律忽略，避免"点了关窗 → 服务端永远等回包 → 对局卡死"。
                if (OnCancelled != null) OnCancelPressed();
                else GD.Print("[TargetSelectionWindow] 强制多选流程：忽略窗口关闭请求（必须选够张数）");
                return;
            }
            if (OnCancelled != null) { OnCancelPressed(); return; }
            var extra = OnExtra;
            if (extra != null)
            {
                OnExtra = null;
                GD.Print("[TargetSelectionWindow] 窗口被关闭 → 视为该槽出口");
                Hide();
                extra();
                return;
            }
            Hide();
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
        /// <param name="cancelLabel">取消按钮文案；`showCancel=false` 时忽略。</param>
        /// <param name="showCancel">
        /// 【2026-10-20·手牌上限弃牌】是否给「取消」出口。**强制恰好 N 张**的流程（弃到上限）传 false：
        /// 不画取消按钮、点标题栏 X 也不生效 —— 用户口径"不能不丢，所以不需要一张不丢的按钮"。
        /// </param>
        /// <param name="confirmLabel">确认按钮文案（null ⇒ "确认"）。</param>
        public void OpenMultiPick(string hint, string cancelLabel = null, bool showCancel = true,
            string confirmLabel = null)
        {
            SetMultiPickLayout(true);
            _multiPick = true;
            _pickedUid = null;
            if (ConfirmButton != null)
            {
                ConfirmButton.Text = string.IsNullOrEmpty(confirmLabel) ? "确认" : confirmLabel;
                // 【2026-10-20】强制流程不画取消按钮 ⇒ 确认按钮铺满整行（否则右半边留一块空白）
                ConfirmButton.OffsetLeft = 16;
                ConfirmButton.OffsetRight = showCancel ? 270 : 544;
            }
            // 【2026-10-19】多选的"取消"语义由调用方决定：取目标场景＝"取消"，
            // 开局调度场景＝"不调度（一张不选）"——两者都只是回一个空清单，但文案必须说人话。
            if (_cancelButton != null)
            {
                _cancelButton.Visible = showCancel;
                _cancelButton.Text = string.IsNullOrEmpty(cancelLabel) ? "取消" : cancelLabel;
            }
            if (HintLabel != null) HintLabel.Text = string.IsNullOrEmpty(hint) ? "左键选择右键取消" : hint;
            // 【2026-10-20】没有取消出口 ⇒ 也关不掉（强制流程只能靠"选够并确认"离开）
            _closable = showCancel;
            _openedFrame = Engine.GetProcessFrames();   // 同上：吞掉开窗同帧的误触关闭
            WindowCenter.PopupCentered(this);           // 【2026-10-20】居中唯一实现（见 WindowCenter 注释）
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
                AllowCancel = false;   // 多选模式自带「取消（不发动）」，不叠加单选的取消出口
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
                EnsureCancelButton();
                _cancelButton.Text = "取消（不发动）";
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

        /// <summary>懒创建「取消」按钮（多选模式的「取消（不发动）」与单选取目标新增的取消出口共用同一实例）。</summary>
        private void EnsureCancelButton()
        {
            if (_cancelButton != null) return;
            _cancelButton = new Button { Text = "取消" };
            _cancelButton.Pressed += OnCancelPressed;
            AddChildToPanel(_cancelButton);
        }

        /// <summary>
        /// 【2026-10·触发窗口改造】单选取目标的「取消」出口布局（`allowCancel=false` 时还原原布局）。
        /// 口径：玩家在**入堆叠取目标**时可以取消 ⇒ 该效果不入堆叠，但**保留在触发排序窗口**（可重新处理）。
        /// 布局上把确认按钮让出右侧空间、窗口加宽到 560，保证两行文案都不被挤压。
        /// </summary>
        private void ApplySingleCancelLayout(bool allowCancel, string cancelLabel)
        {
            AllowCancel = allowCancel;
            if (!allowCancel)
            {
                if (_cancelButton != null) _cancelButton.Visible = false;
                Size = new Vector2I(500, 150);
                if (TitleLabel != null) TitleLabel.OffsetRight = 486;
                if (HintLabel != null) HintLabel.OffsetRight = 486;
                if (ConfirmButton != null)
                {
                    ConfirmButton.OffsetLeft = 16; ConfirmButton.OffsetTop = 108;
                    ConfirmButton.OffsetRight = 486; ConfirmButton.OffsetBottom = 144;
                }
                return;
            }
            EnsureCancelButton();
            Size = new Vector2I(560, 150);
            if (TitleLabel != null) TitleLabel.OffsetRight = 546;
            if (HintLabel != null) HintLabel.OffsetRight = 546;
            if (ConfirmButton != null)
            {
                ConfirmButton.OffsetLeft = 16; ConfirmButton.OffsetTop = 108;
                ConfirmButton.OffsetRight = 350; ConfirmButton.OffsetBottom = 144;
            }
            _cancelButton.Text = string.IsNullOrEmpty(cancelLabel) ? "取消" : cancelLabel;
            _cancelButton.Visible = true;
            _cancelButton.OffsetLeft = 366; _cancelButton.OffsetTop = 108;
            _cancelButton.OffsetRight = 546; _cancelButton.OffsetBottom = 144;
        }

        /// <summary>「取消」按钮：关闭窗口并回调 `OnCancelled`（**回调唯一性**：交付后置空）。
        /// 多选模式＝"取消（不发动）"；【2026-10】单选取目标＝"取消（不入堆叠）"出口。</summary>
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
        ///
        /// 【2026-10·触发窗口改造】新增 `allowCancel`/`cancelLabel`：为**触发入堆叠取目标**提供取消出口
        /// （玩家取消 ⇒ 该触发不入堆叠，但保留在触发排序窗口）。默认 false ＝ 既有所有调用点行为逐字不变。
        /// </summary>
        public void Open(string effectName, string hint, string extraLabel = null,
            bool allowCancel = false, string cancelLabel = null)
        {
            SetMultiPickLayout(false);                                    // 还原单选取目标布局（多选模式可能改过尺寸/按钮）
            ApplySingleCancelLayout(allowCancel, cancelLabel);            // 再叠加/还原「取消」出口
            // 【2026-10-20·用户口径】"没有取消出口 + 没有该槽出口按钮"＝强制取目标 ⇒ 窗口关不掉。
            _closable = allowCancel || !string.IsNullOrEmpty(extraLabel);
            _openedFrame = Engine.GetProcessFrames();   // 记录开窗帧（吞掉同帧误触的关闭请求，见 OnCloseRequested）
            WindowCenter.PopupCentered(this);           // 【2026-10-20】居中唯一实现（原先取自身视口 ⇒ 落在左上角）
            _pickedUid = null;
            // 标题：调用方给的文案已是**完整句子**（如"请选择要弃掉的手牌""额外费用：选择要牺牲的角色"）时
            // 不再叠加"选择目标："前缀——否则会显示成"选择目标：请选择要弃掉的手牌"（2026-10 用户实测语句不通）。
            // 只对"卡名/效果名"这类短语补前缀（`Contains(string)` 为序数比较，Web 安全）。
            if (TitleLabel != null)
            {
                bool fullSentence = effectName != null
                    && (effectName.Contains("请选择", System.StringComparison.Ordinal)
                        || effectName.Contains("选择要", System.StringComparison.Ordinal)
                        || effectName.Contains("额外费用", System.StringComparison.Ordinal)
                        || effectName.Contains("额外代价", System.StringComparison.Ordinal));
                TitleLabel.Text = fullSentence ? effectName : $"选择目标：{effectName}";
            }
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
            // 【2026-10 修复·JC049】开窗同帧的"确认/出口"按键一律忽略：本窗常在拖拽松手的同一帧被打开，
            // 松手/焦点切换会把按钮判成被按下 ⇒ 玩家还没看到窗就被当成"取消（不打出）"。
            if (Engine.GetProcessFrames() == _openedFrame)
            {
                GD.Print("[TargetSelectionWindow] 忽略开窗同帧的确认/出口按钮（拖拽松手误触）");
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
