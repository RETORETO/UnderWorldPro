using System;
using System.Collections.Generic;
using Godot;
using Script.Net;

namespace Script.UI
{
    /// <summary>
    /// 【联机决策面板】客户端收到服务端下发的 ChoiceRequest 后，由 ArenaOl 实例化本窗口，
    /// 把"需要玩家决策"的请求翻译成按钮选项，并把玩家选择封装为 ChoiceResult 回传。
    ///
    /// 支持 ChoiceRequest.Kind 四种：
    ///  - PayOrTap：支付 N / 横置 二选一（ForceChoice 时隐藏取消）。
    ///  - SelectTarget：把 OptionUids 渲染为可点选项，点选回 SelectedUid。
    ///  - SelectTriggerOrder：显示触发条目，提供"按默认顺序全部入栈"与"放弃剩余"（非强制）。
    ///  - ForceChoice：多选一（无取消）。
    ///  - SelectHandCards【K10·JC081】：勾选任意数量手牌（可空）→ 确认 = 展示给对手；取消 = 不打出。
    ///
    /// 纪律：本类为纯 View，不写任何 Model；选项显示文字由外部注入 resolveLabel 解析，
    /// 选择结果经 onResult 回调交回 ArenaOl → NetworkMatchLauncher.SendChoiceResult 上行。
    /// </summary>
    public partial class ChoicePromptPanel : Window
    {
        private VBoxContainer _buttonBox;
        private ChoiceRequest _request;
        private Func<string, string> _resolveLabel;
        private Action<ChoiceResult> _onResult;

        private bool _dismissed = false;

        public override void _Ready()
        {
            Title = "请选择";
            Size = new Vector2I(460, 180);

            // 根容器：代码构建，避免依赖 .tscn 结构
            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(root);

            _buttonBox = new VBoxContainer();
            _buttonBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _buttonBox.OffsetTop = 30;
            _buttonBox.AddThemeConstantOverride("separation", 8);
            root.AddChild(_buttonBox);

            // 点窗外关闭（仅在可取消模式下生效；强制选择时忽略）
            CallDeferred("ConnectFocusExited");
        }

        private void ConnectFocusExited()
        {
            FocusExited += OnFocusExited;
        }

        private void OnFocusExited()
        {
            // 联机决策窗口统一不允许"点窗外直接关闭"（避免误触）。
            // 可取消/可放弃的窗口通过显式按钮（AddCancelIfAllowed 按 ForceChoice 决定是否显示）完成。
            // 这与单机 TargetSelectionWindow/TriggerOrderWindow/StrengthWindow 点窗外不关闭的行为一致。
        }

        /// <summary>
        /// 初始化面板。request：服务端下发的请求；resolveLabel：UID → 显示文字；
        /// onResult：玩家选择/取消后的回包（本窗口在回调后自毁）。
        /// </summary>
        public void Init(ChoiceRequest request, Func<string, string> resolveLabel, Action<ChoiceResult> onResult)
        {
            _request = request;
            _resolveLabel = resolveLabel ?? (uid => uid);
            _onResult = onResult;

            if (!string.IsNullOrEmpty(request.Prompt))
                Title = request.Prompt;

            ClearButtons();

            switch (request.RequestKind)
            {
                case ChoiceRequest.Kind.PayOrTap:
                    BuildPayOrTap(request);
                    break;
                case ChoiceRequest.Kind.SelectTarget:
                case ChoiceRequest.Kind.ForceChoice:
                    BuildOptions(request);
                    break;
                case ChoiceRequest.Kind.SelectTriggerOrder:
                    BuildTriggerOrder(request);
                    break;
                case ChoiceRequest.Kind.TieBreak:
                    BuildTieBreak(request);
                    break;
                case ChoiceRequest.Kind.CombatInvestigation:
                    BuildCombatInvestigation(request);
                    break;
                case ChoiceRequest.Kind.CombatStrength:
                    BuildCombatStrength(request);
                    break;
                case ChoiceRequest.Kind.CurbMarks:
                    BuildCurbMarks(request);
                    break;
                case ChoiceRequest.Kind.MultiTargetSlot:
                    BuildMultiTargetSlot(request);
                    break;
                case ChoiceRequest.Kind.ConfirmTargets:
                    BuildConfirmTargets(request);
                    break;
                case ChoiceRequest.Kind.FieldMultiPick:
                    BuildFieldMultiPick(request);
                    break;
                case ChoiceRequest.Kind.SelectEffectMode:
                    BuildEffectMode(request);
                    break;
                case ChoiceRequest.Kind.NumberInput:
                    BuildNumberInput(request);
                    break;
                case ChoiceRequest.Kind.InspectCards:
                    BuildInspectCards(request);
                    break;
                case ChoiceRequest.Kind.SelectPlayer:
                    BuildPlayerTarget(request);
                    break;
                case ChoiceRequest.Kind.SelectHandCards:
                    BuildHandCards(request);
                    break;
                case ChoiceRequest.Kind.SelectMulligan:
                    BuildMulligan(request);
                    break;
                default:
                    BuildOptions(request);
                    break;
            }

            // 根据选项数量自适应高度
            int count = _buttonBox.GetChildCount();
            Size = new Vector2I(460, Mathf.Clamp(90 + count * 46, 160, 620));
        }

        private void ClearButtons()
        {
            if (_buttonBox == null) return;
            foreach (Node child in _buttonBox.GetChildren())
                child.QueueFree();
        }

        private void BuildPayOrTap(ChoiceRequest request)
        {
            var pay = new Button { Text = $"支付 {request.PayAmount}（横置资产）" };
            pay.Pressed += () => Complete(new ChoiceResult { Pay = true, Cancelled = false });
            _buttonBox.AddChild(pay);

            var tap = new Button { Text = "横置" };
            tap.Pressed += () => Complete(new ChoiceResult { Pay = false, Cancelled = false });
            _buttonBox.AddChild(tap);

            AddCancelIfAllowed(request);
        }

        /// <summary>
        /// 【K24·玩家目标】`SelectPlayer`：把候选玩家号列成按钮。
        /// 显示名优先用随请求下发的 `OptionLabels`（与 `OptionPlayerIds` 同序），否则回退"玩家N"。
        /// 回包 `ChoiceResult.SelectedPlayerId`；取消由 `AddCancelIfAllowed` 按 `ForceChoice` 决定是否提供。
        /// </summary>
        private void BuildPlayerTarget(ChoiceRequest request)
        {
            var ids = request.OptionPlayerIds;
            if (ids != null)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    int pid = ids[i];
                    string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                                    && !string.IsNullOrEmpty(request.OptionLabels[i]))
                        ? request.OptionLabels[i]
                        : $"玩家{pid}";
                    var btn = new Button { Text = label };
                    btn.Pressed += () => Complete(new ChoiceResult
                    {
                        SelectedPlayerId = pid,
                        SelectedUid = Script.Manager.TargetChoice.PlayerUid(pid),
                        Cancelled = false,
                    });
                    _buttonBox.AddChild(btn);
                }
            }
            AddCancelIfAllowed(request, "放弃");
        }

        private void BuildOptions(ChoiceRequest request)
        {
            if (request.OptionUids != null)
            {
                for (int i = 0; i < request.OptionUids.Count; i++)
                {
                    string uid = request.OptionUids[i];
                    if (string.IsNullOrEmpty(uid)) continue;
                    // 【P2·联机可读性】优先用**服务端下发的显示名**（`OptionLabels`）：牌库/对手手牌/墓地等
                    // 区域牌堆在客户端是**隐藏占位**（无模板、无卡面），本地 `_resolveLabel` 只能回退成原始 UID
                    // ⇒ 玩家"看不到卡名却必须选"。服务端给出名字即单一真相源，客户端不重算。
                    string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                                    && !string.IsNullOrEmpty(request.OptionLabels[i]))
                        ? request.OptionLabels[i]
                        : _resolveLabel(uid);
                    string capturedUid = uid;
                    var btn = new Button { Text = label };
                    btn.Pressed += () => Complete(new ChoiceResult { SelectedUid = capturedUid, Cancelled = false });
                    _buttonBox.AddChild(btn);
                }
            }

            // SelectTarget 允许取消（放弃选择）；ForceChoice 不允许取消。
            if (request.RequestKind == ChoiceRequest.Kind.SelectTarget)
                AddCancelIfAllowed(request);
        }

        /// <summary>
        /// 【K10·额外费用·JC081】"**展示任意数量的手牌**"（多选）：
        /// 勾选要展示给对手的手牌（可一张都不勾＝不展示、按全额费用照样打出）→ **确认** ＝给对手展示所有已勾选牌；
        /// **取消**＝不打出、卡不入堆叠、不扣任何费用（对齐单机 `Arena.BeginPlayWithRevealExtraCostChoice`）。
        /// 候选显示名由服务端随 `OptionLabels` 下发（含"（合格 -1）/（不合格）"标记）——**客户端不重算规则**。
        /// 回包：`Confirmed=true` ＋ `SelectedUids`（已勾选，可空）；取消＝`Cancelled=true`。
        /// </summary>
        private void BuildHandCards(ChoiceRequest request)
        {
            string hintText = string.IsNullOrEmpty(request.Prompt)
                ? "额外费用：勾选要展示给对手的手牌（不选＝不展示，按全额费用打出）"
                : request.Prompt;
            var hint = new Label { Text = hintText };
            hint.MouseFilter = Control.MouseFilterEnum.Ignore;
            _buttonBox.AddChild(hint);

            // 数量约束：未显式设置 NumberMax（=0）时按"不限制"处理，避免破坏既有展示/调度流程
            int minSel = request.NumberMin;
            int maxSel = request.NumberMax > 0 ? request.NumberMax : int.MaxValue;

            var checks = new List<(string uid, CheckBox box)>();
            if (request.OptionUids != null)
            {
                for (int i = 0; i < request.OptionUids.Count; i++)
                {
                    string uid = request.OptionUids[i];
                    if (string.IsNullOrEmpty(uid)) continue;
                    string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                                    && !string.IsNullOrEmpty(request.OptionLabels[i]))
                        ? request.OptionLabels[i]
                        : _resolveLabel(uid);
                    var cb = new CheckBox { Text = label };
                    _buttonBox.AddChild(cb);
                    checks.Add((uid, cb));
                }
            }

            var confirm = new Button { Text = "确认" };
            // 按数量约束启用确认键：至少 minSel、至多 maxSel 张
            System.Action refreshConfirm = () =>
            {
                int n = 0;
                foreach (var (_, cb) in checks) if (cb.ButtonPressed) n++;
                confirm.Disabled = (n < minSel || n > maxSel);
            };
            foreach (var (_, cb) in checks)
            {
                cb.Toggled += (pressed) =>
                {
                    // 超过上限：自动取消本次勾选（防止多选）
                    if (pressed)
                    {
                        int n = 0;
                        foreach (var (_, b) in checks) if (b.ButtonPressed) n++;
                        if (n > maxSel) cb.ButtonPressed = false;
                    }
                    refreshConfirm();
                };
            }

            confirm.Pressed += () =>
            {
                var res = new ChoiceResult { Cancelled = false, Confirmed = true };
                foreach (var (uid, cb) in checks)
                    if (cb.ButtonPressed) res.SelectedUids.Add(uid);
                Complete(res);
            };
            _buttonBox.AddChild(confirm);
            refreshConfirm();

            AddCancelIfAllowed(request, "取消");
        }

        /// <summary>
        /// 【开局·调度步骤】发完起手手牌后的调度：勾选要换掉（放到牌库底）的手牌，可一张都不勾。
        /// 点"确认"锁定选择 → 回包 `Confirmed=true` ＋ `SelectedUids`（可空）；
        /// 服务端等**双方**都确认后才统一结算（置底 → 顶抽等量 → 洗牌），再进入第一回合。
        /// 说明：本步骤为开局固定流程，**不提供取消**（未选＝空确认，等价"不调度"）。
        /// </summary>
        private void BuildMulligan(ChoiceRequest request)
        {
            var hint = new Label
            {
                Text = "调度：勾选要换掉的手牌（可一张都不勾）。双方都确认后统一结算。",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            _buttonBox.AddChild(hint);

            var checks = new List<(string uid, CheckBox box)>();
            if (request.OptionUids != null)
            {
                for (int i = 0; i < request.OptionUids.Count; i++)
                {
                    string uid = request.OptionUids[i];
                    if (string.IsNullOrEmpty(uid)) continue;
                    string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                                    && !string.IsNullOrEmpty(request.OptionLabels[i]))
                        ? request.OptionLabels[i]
                        : _resolveLabel(uid);
                    var cb = new CheckBox { Text = label };
                    _buttonBox.AddChild(cb);
                    checks.Add((uid, cb));
                }
            }

            var confirm = new Button { Text = "确认调度" };
            confirm.Pressed += () =>
            {
                var res = new ChoiceResult { Cancelled = false, Confirmed = true };
                foreach (var (uid, cb) in checks)
                    if (cb.ButtonPressed) res.SelectedUids.Add(uid);
                Complete(res);
            };
            _buttonBox.AddChild(confirm);
        }

        private void BuildTriggerOrder(ChoiceRequest request)
        {
            // 显示待排序触发清单（只读说明）
            if (request.OptionUids != null)
            {
                foreach (var uid in request.OptionUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    var label = new Label { Text = "· " + _resolveLabel(uid) };
                    label.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _buttonBox.AddChild(label);
                }
            }

            // 简化协议：ChoiceResult 单 SelectedUid 无法表达逐条排序，故提供"全部按默认顺序入栈"。
            // 语义对齐单机 TriggerOrderWindow：非强制触发可"确认（放弃剩余）"，强制触发必须全部入栈。
            var confirm = new Button { Text = "按默认顺序全部入栈" };
            confirm.Pressed += () => Complete(new ChoiceResult { SelectedUid = null, Cancelled = false });
            _buttonBox.AddChild(confirm);

            AddCancelIfAllowed(request, "放弃剩余触发");
        }

        /// <summary>先手破平确认：确认=回 Cancelled=false；取消=回 Cancelled=true（平局无效果）。</summary>
        private void BuildTieBreak(ChoiceRequest request)
        {
            var confirm = new Button { Text = "确认破平" };
            confirm.Pressed += () => Complete(new ChoiceResult { Cancelled = false });
            _buttonBox.AddChild(confirm);

            AddCancelIfAllowed(request, "取消破平");
        }

        /// <summary>调查对抗：勾选要放回牌库顶的卡（未勾选放牌库底），结果回 SelectedUids（勾选顺序）。</summary>
        private void BuildCombatInvestigation(ChoiceRequest request)
        {
            var hint = new Label { Text = $"调查对抗：勾选放回牌库顶的卡（其余放牌库底），共 {request.Amount} 张" };
            hint.MouseFilter = Control.MouseFilterEnum.Ignore;
            _buttonBox.AddChild(hint);

            var checks = new List<(string uid, CheckBox box)>();
            if (request.OptionUids != null)
            {
                foreach (var uid in request.OptionUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    var cb = new CheckBox { Text = _resolveLabel(uid) };
                    _buttonBox.AddChild(cb);
                    checks.Add((uid, cb));
                }
            }

            var confirm = new Button { Text = "确认放回" };
            confirm.Pressed += () =>
            {
                var res = new ChoiceResult { Cancelled = false };
                foreach (var (uid, cb) in checks)
                    if (cb.ButtonPressed) res.SelectedUids.Add(uid);
                Complete(res);
            };
            _buttonBox.AddChild(confirm);

            AddCancelIfAllowed(request);
        }

        /// <summary>战斗对抗：为每个合法承伤目标分配伤害点数，结果回 SelectedUids/SelectedAmounts。</summary>
        private void BuildCombatStrength(ChoiceRequest request)
        {
            var hint = new Label { Text = $"战斗对抗：分配 {request.Amount} 点伤害" };
            hint.MouseFilter = Control.MouseFilterEnum.Ignore;
            _buttonBox.AddChild(hint);

            var spins = new List<(string uid, SpinBox spin)>();
            if (request.OptionUids != null)
            {
                foreach (var uid in request.OptionUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    var row = new HBoxContainer();
                    var name = new Label { Text = _resolveLabel(uid) };
                    name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    var spin = new SpinBox { MinValue = 0, MaxValue = request.Amount, Value = 0 };
                    spin.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
                    row.AddChild(name);
                    row.AddChild(spin);
                    _buttonBox.AddChild(row);
                    spins.Add((uid, spin));
                }
            }

            var confirm = new Button { Text = "确认分配" };
            confirm.Pressed += () =>
            {
                var res = new ChoiceResult { Cancelled = false };
                foreach (var (uid, spin) in spins)
                {
                    int v = (int)Math.Round(spin.Value);
                    if (v > 0)
                    {
                        res.SelectedUids.Add(uid);
                        res.SelectedAmounts.Add(v);
                    }
                }
                Complete(res);
            };
            _buttonBox.AddChild(confirm);

            AddCancelIfAllowed(request);
        }

        /// <summary>【P4】遏制X：被遏制者按地区分配要移除的势力标志（每地区上限服务端下发），结果回 SelectedUids/SelectedAmounts。</summary>
        private void BuildCurbMarks(ChoiceRequest request)
        {
            var hint = new Label { Text = $"遏制{request.Amount}：请分配要移除的势力标志（每地区上限＝该地区标记数）" };
            hint.MouseFilter = Control.MouseFilterEnum.Ignore;
            _buttonBox.AddChild(hint);

            var spins = new List<(string uid, SpinBox spin, int max)>();
            // 【2026-09 用户口径·遏制分配钳制】`request.Amount` ＝遏制值 X（本玩家总共要移除的势力标志数）。
            // 规则：① 多个框的分配之和达到 X 后 ⇒ **其余"增加分配"按钮/输入一律置灰**（不能再多分配）；
            //       ② 某个框**手输/步进**超过 `X − 其他地区已分配` 时 ⇒ **立即钳制**为该上限
            //          （同时仍不超过该地区自身上限 `cap`）。
            // 实现：每次任一 SpinBox 变化都重算所有框的**动态上限** `min(cap, X − 其他框之和)`，
            // 越界值当场回写，并把 `MaxValue` 收紧到该上限（SpinBox 的增减按钮与手输都会被 Godot 再钳一次），
            // 上限为 0（已达 X）时置灰不可编辑。服务端仍会权威复检（防回包越界）。
            bool reflowing = false;
            System.Action refillLimits = null;
            refillLimits = () =>
            {
                if (reflowing) return;                 // 防"回写 Value → ValueChanged → 再回写"的递归
                reflowing = true;
                int total = 0;
                foreach (var (_, s, _) in spins) total += (int)Math.Round(s.Value);
                foreach (var (_, s, cap) in spins)
                {
                    int mine = (int)Math.Round(s.Value);
                    int others = total - mine;
                    int allowedMax = Math.Max(0, Math.Min(cap, request.Amount - others));
                    if (mine > allowedMax) s.Value = allowedMax;   // ② 越界当场钳制
                    s.MinValue = 0;
                    s.MaxValue = allowedMax;
                    s.Editable = allowedMax > 0;                   // ① 该框已达上限 ⇒ 置灰（不可再增加/输入）
                    s.Modulate = allowedMax > 0 ? Colors.White : new Color(1f, 1f, 1f, 0.45f);
                }
                reflowing = false;
            };
            if (request.OptionUids != null)
            {
                for (int i = 0; i < request.OptionUids.Count; i++)
                {
                    string uid = request.OptionUids[i];
                    if (string.IsNullOrEmpty(uid)) continue;
                    // 每地区上限：由服务端随请求下发（`OptionMaxAmounts`，与 OptionUids 同序）；
                    // 缺失（旧服务端）时退化为总上限，服务端仍会权威复检。
                    int cap = (request.OptionMaxAmounts != null && i < request.OptionMaxAmounts.Count)
                        ? request.OptionMaxAmounts[i]
                        : request.Amount;
                    if (cap <= 0) continue;
                    // 条目名：优先服务端下发的 OptionLabels（"地区N"），否则按 UID 解析（地区名/卡名）
                    string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                                    && !string.IsNullOrEmpty(request.OptionLabels[i]))
                        ? request.OptionLabels[i]
                        : _resolveLabel(uid);

                    var row = new HBoxContainer();
                    var name = new Label { Text = $"{label}（可移除 {cap}）" };
                    name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                    var spin = new SpinBox { MinValue = 0, MaxValue = cap, Value = 0 };
                    spin.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
                    spin.ValueChanged += _ => refillLimits();      // 任一框变化 → 重算所有框的动态上限
                    row.AddChild(name);
                    row.AddChild(spin);
                    _buttonBox.AddChild(row);
                    spins.Add((uid, spin, cap));
                }
            }
            refillLimits();   // 初始收口（把每框上限按"X − 其他框"收紧）

            var confirm = new Button { Text = "确认分配" };
            confirm.Pressed += () =>
            {
                var res = new ChoiceResult { Cancelled = false };
                int left = request.Amount;
                foreach (var (uid, spin, cap) in spins)
                {
                    int v = (int)Math.Round(spin.Value);
                    if (v <= 0) continue;
                    if (v > cap) v = cap;
                    if (v > left) v = left;      // 客户端只做展示层钳制；服务端仍会权威复检
                    if (v <= 0) continue;
                    res.SelectedUids.Add(uid);
                    res.SelectedAmounts.Add(v);
                    left -= v;
                }
                Complete(res);
            };
            _buttonBox.AddChild(confirm);

            AddCancelIfAllowed(request);
        }

        /// <summary>
        /// 【K15·多目标】逐槽选择：把该槽的候选列成按钮（地区槽＝"地区N"；卡槽＝卡名，优先用 `OptionLabels` 下发名）；
        /// 可留空的槽额外给"留空"按钮（回包空 `SelectedUid`＋`Cancelled=false`）；任何时候都能"取消（不打出）"。
        /// </summary>
        private void BuildMultiTargetSlot(ChoiceRequest request)
        {
            var uids = request.OptionUids ?? new System.Collections.Generic.List<string>();
            for (int i = 0; i < uids.Count; i++)
            {
                string uid = uids[i];
                if (string.IsNullOrEmpty(uid)) continue;
                string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                    && !string.IsNullOrEmpty(request.OptionLabels[i]))
                    ? request.OptionLabels[i]
                    : _resolveLabel(uid);
                string capturedUid = uid;
                var btn = new Button { Text = label };
                btn.Pressed += () => Complete(new ChoiceResult { SelectedUid = capturedUid, Cancelled = false });
                _buttonBox.AddChild(btn);
            }

            if (request.SlotOptional)
            {
                var skip = new Button { Text = "不选（留空该槽）" };
                skip.Pressed += () => Complete(new ChoiceResult { SelectedUid = "", Cancelled = false });
                _buttonBox.AddChild(skip);
            }

            // 无论是否必选槽，都允许"取消整次打出"（回包 Cancelled=true → 服务端不扣费、不入堆叠）
            var cancel = new Button { Text = "取消（不打出）" };
            cancel.Pressed += () => Complete(new ChoiceResult { Cancelled = true });
            _buttonBox.AddChild(cancel);
        }

        /// <summary>
        /// 【K15·多目标】确认面板：列出逐槽摘要（`OptionLabels`）与已选卡目标，玩家点"确认"才入堆叠。
        /// 回包语义：`Confirmed=true`＝确认；`false`＋`Cancelled=true`＝取消（服务端不扣费、不入堆叠）。
        /// </summary>
        private void BuildConfirmTargets(ChoiceRequest request)
        {
            if (request.OptionLabels != null)
            {
                foreach (var line in request.OptionLabels)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    var label = new Label { Text = "· " + line };
                    label.MouseFilter = Control.MouseFilterEnum.Ignore;
                    label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    _buttonBox.AddChild(label);
                }
            }

            var confirm = new Button { Text = "确认（加入堆叠）" };
            confirm.Pressed += () => Complete(new ChoiceResult { Confirmed = true, Cancelled = false });
            _buttonBox.AddChild(confirm);

            var cancel = new Button { Text = "取消（不打出）" };
            cancel.Pressed += () => Complete(new ChoiceResult { Confirmed = false, Cancelled = true });
            _buttonBox.AddChild(cancel);
        }

        /// <summary>
        /// 【2026-09 用户口径 JC062/JC021·联机】整窗多选的**按钮式兜底**（通用决策面板用）：
        /// 卡候选与地区候选各列一组**可切换**按钮（再点一次取消该选择），并有"确认/取消"。
        /// 图形客户端由 `ArenaOl.OpenOnlineFieldMultiPickWindow` 走"场上高亮 + 左键/右键"的同款窗；
        /// 本分支只保证"没有场上 UI 的客户端（含机器人/无头端）也能回答"，避免请求无人应答而超时。
        /// </summary>
        private void BuildFieldMultiPick(ChoiceRequest request)
        {
            var pickedCards = new System.Collections.Generic.List<string>();
            var pickedAreas = new System.Collections.Generic.List<string>();
            var cardBtns = new System.Collections.Generic.List<(Button Btn, string Uid)>();
            var areaBtns = new System.Collections.Generic.List<(Button Btn, string Uid)>();

            System.Action refresh = null;
            bool CanConfirm()
                => pickedCards.Count >= request.MultiMinCards && pickedCards.Count <= request.MultiMaxCards
                   && pickedAreas.Count <= System.Math.Max(0, request.MultiMaxAreas)
                   && (!request.MultiAreaRequired || pickedAreas.Count >= 1);

            System.Action<(Button Btn, string Uid), System.Collections.Generic.List<string>> toggle =
                (entry, list) =>
                {
                    if (list.Contains(entry.Uid)) list.Remove(entry.Uid);
                    else if (list.Count < (list == pickedAreas ? System.Math.Max(0, request.MultiMaxAreas) : request.MultiMaxCards))
                    {
                        if (list == pickedAreas && request.MultiMaxAreas <= 1) list.Clear();   // 至多 1 个 ⇒ 换选
                        list.Add(entry.Uid);
                    }
                    refresh?.Invoke();
                };

            var cardUids = request.OptionUids ?? new System.Collections.Generic.List<string>();
            for (int i = 0; i < cardUids.Count; i++)
            {
                string uid = cardUids[i];
                if (string.IsNullOrEmpty(uid)) continue;
                string label = (request.OptionLabels != null && i < request.OptionLabels.Count
                    && !string.IsNullOrEmpty(request.OptionLabels[i])) ? request.OptionLabels[i] : _resolveLabel(uid);
                var btn = new Button { Text = label };
                var entry = (btn, uid);
                btn.Pressed += () => toggle(entry, pickedCards);
                cardBtns.Add(entry);
                _buttonBox.AddChild(btn);
            }
            foreach (var uid in request.OptionAreaUids ?? new System.Collections.Generic.List<string>())
            {
                if (string.IsNullOrEmpty(uid)) continue;
                var btn = new Button { Text = _resolveLabel(uid) };
                var entry = (btn, uid);
                btn.Pressed += () => toggle(entry, pickedAreas);
                areaBtns.Add(entry);
                _buttonBox.AddChild(btn);
            }

            var hint = new Label { Text = request.Prompt ?? "请选择目标" };
            hint.MouseFilter = Control.MouseFilterEnum.Ignore;
            _buttonBox.AddChild(hint);

            Button confirm = null;
            refresh = () =>
            {
                foreach (var (btn, uid) in cardBtns)
                    btn.Text = (pickedCards.Contains(uid) ? "✔ " : "") + _resolveLabel(uid);
                foreach (var (btn, uid) in areaBtns)
                    btn.Text = (pickedAreas.Contains(uid) ? "✔ " : "") + _resolveLabel(uid);
                if (confirm != null) confirm.Disabled = !CanConfirm();
            };

            confirm = new Button { Text = "确认" };
            confirm.Pressed += () =>
            {
                if (!CanConfirm()) return;
                var res = new ChoiceResult { Confirmed = true, Cancelled = false };
                res.SelectedUids.AddRange(pickedCards);
                res.SelectedAreaUids.AddRange(pickedAreas);
                Complete(res);
            };
            _buttonBox.AddChild(confirm);

            var cancel = new Button { Text = "取消" };
            cancel.Pressed += () => Complete(new ChoiceResult { Cancelled = true });
            _buttonBox.AddChild(cancel);
            refresh();
        }

        /// <summary>
        /// 【K16·模式分支】"选择一项"：把各模式列成按钮，点选即回包 `SelectedIndices[0]＝模式下标`；
        /// "取消（不打出）"回 `Cancelled=true`（服务端不扣费、不入堆叠）。
        /// 【2026-09 用户口径 JC063】`OptionDisabled[i] == true` ⇒ 该模式**当前没有合法目标** ⇒ 按钮**置灰**
        /// （点击无效；服务端仍会复检）。
        /// </summary>
        private void BuildEffectMode(ChoiceRequest request)
        {
            var labels = request.OptionLabels ?? new System.Collections.Generic.List<string>();
            for (int i = 0; i < labels.Count; i++)
            {
                int captured = i;
                bool disabled = request.OptionDisabled != null && captured < request.OptionDisabled.Count
                                && !request.OptionDisabled[captured];
                var btn = new Button { Text = labels[i] ?? $"选项{captured + 1}", Disabled = disabled };
                btn.Pressed += () =>
                {
                    if (disabled) return;   // 置灰项：点击不产生选择
                    var r = new ChoiceResult { Cancelled = false };
                    r.SelectedIndices.Add(captured);
                    Complete(r);
                };
                _buttonBox.AddChild(btn);
            }

            var cancel = new Button { Text = "取消（不打出）" };
            cancel.Pressed += () => Complete(new ChoiceResult { Cancelled = true });
            _buttonBox.AddChild(cancel);
        }

        /// <summary>
        /// 【K11·可选支付 / 数值输入】把"输入框 + 确认 / 取消"渲染到面板：
        /// 输入框初值＝`NumberDefault`（钳制到 [`NumberMin`,`NumberMax`]）；确认回 `SelectedAmounts[0]＝N`，
        /// 取消回 `Cancelled=true`（支付场景＝"不支付"）。
        /// </summary>
        /// <summary>
        /// 【2026-09 用户实测·JC064】该数值请求是否其实是"**是/否二选一**"（值域恒为 {0,1} 且带两个按钮文案）：
        /// 是 ⇒ 客户端应渲染**两个按钮**而不是输入框（用户口径："JC064 支付费用的触发弹窗不需要输入框，
        /// 参考 JC061"）。单一判定入口（`BuildNumberInput` 消费）。
        /// </summary>
        public static bool IsYesNoChoice(ChoiceRequest request)
            => request != null && request.NumberMin == 0 && request.NumberMax == 1
               && request.OptionLabels != null && request.OptionLabels.Count >= 2
               && !string.IsNullOrEmpty(request.OptionLabels[0]) && !string.IsNullOrEmpty(request.OptionLabels[1]);

        private void BuildNumberInput(ChoiceRequest request)
        {
            int min = request.NumberMin;
            int max = System.Math.Max(min, request.NumberMax);
            int def = System.Math.Clamp(request.NumberDefault, min, max);

            // 【2026-09 用户实测·JC064】"0/1 二选一"（如 `PayOrSacrificeSelf`＝"支付 N"/"牺牲"）**不要输入框**：
            // 直接给两个按钮（标签取 `OptionLabels[0]/[1]`）——用户口径："JC064 支付费用的触发弹窗不需要输入框，
            // 参考 JC061"。`ChoicePromptPanel.IsYesNoChoice` 为单一判定（单机 `NumberInputWindow` 之外的兜底面）。
            if (IsYesNoChoice(request))
            {
                var yes = new Button { Text = request.OptionLabels[0] };
                yes.Pressed += () =>
                {
                    var r = new ChoiceResult { Cancelled = false };
                    r.SelectedAmounts.Add(1);
                    Complete(r);
                };
                _buttonBox.AddChild(yes);

                var no = new Button { Text = request.OptionLabels[1] };
                no.Pressed += () =>
                {
                    var r = new ChoiceResult { Cancelled = false };
                    r.SelectedAmounts.Add(0);   // 显式回 0＝"否"（与 `AskZeroOrOneViaGatewayAsync` 的 `chosen > 0` 判定一致）
                    Complete(r);
                };
                _buttonBox.AddChild(no);
                return;
            }

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var input = new LineEdit
            {
                Text = def.ToString(),
                Alignment = HorizontalAlignment.Center,
                CustomMinimumSize = new Vector2(90, 0),
            };
            row.AddChild(input);
            _buttonBox.AddChild(row);

            var confirm = new Button
            {
                Text = (request.OptionLabels != null && request.OptionLabels.Count > 0 && !string.IsNullOrEmpty(request.OptionLabels[0]))
                    ? request.OptionLabels[0] : "确认",
            };
            confirm.Pressed += () =>
            {
                int value = def;
                if (int.TryParse(input.Text?.Trim(), out var parsed)) value = parsed;
                value = System.Math.Clamp(value, min, max);
                var r = new ChoiceResult { Cancelled = false };
                r.SelectedAmounts.Add(value);
                Complete(r);
            };
            _buttonBox.AddChild(confirm);

            var cancel = new Button
            {
                Text = (request.OptionLabels != null && request.OptionLabels.Count > 1 && !string.IsNullOrEmpty(request.OptionLabels[1]))
                    ? request.OptionLabels[1] : "取消",
            };
            cancel.Pressed += () => Complete(new ChoiceResult { Cancelled = true });
            _buttonBox.AddChild(cancel);
        }

        /// <summary>
        /// 【P3·H】检视（**私密**）：把被检视卡的面呈现给**检视者本人**（该请求只下发给检视者座位），
        /// 点"确认"回包 `Cancelled=false`；超时/关闭按"已查看"处理（检视不改变状态）。
        /// </summary>
        private void BuildInspectCards(ChoiceRequest request)
        {
            if (request.OptionUids != null)
            {
                foreach (var uid in request.OptionUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    var label = new Label
                    {
                        Text = "· " + _resolveLabel(uid),
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    };
                    _buttonBox.AddChild(label);
                }
            }

            var ok = new Button { Text = "确认（已查看）" };
            ok.Pressed += () => Complete(new ChoiceResult { Cancelled = false });
            _buttonBox.AddChild(ok);
        }

        private void AddCancelIfAllowed(ChoiceRequest request, string cancelLabel = "取消")        {
            if (request.ForceChoice) return;
            var cancel = new Button { Text = cancelLabel };
            cancel.Pressed += () => Complete(new ChoiceResult { Cancelled = true });
            _buttonBox.AddChild(cancel);
        }

        private void Complete(ChoiceResult result)
        {
            if (_dismissed) return;
            _dismissed = true;
            result.RequestId = _request?.RequestId;
            _onResult?.Invoke(result);
            QueueFree();
        }
    }
}
