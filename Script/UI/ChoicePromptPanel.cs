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
                foreach (var uid in request.OptionUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    string label = _resolveLabel(uid);
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
                    row.AddChild(name);
                    row.AddChild(spin);
                    _buttonBox.AddChild(row);
                    spins.Add((uid, spin, cap));
                }
            }

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
        /// 【K16·模式分支】"选择一项"：把各模式列成按钮，点选即回包 `SelectedIndices[0]＝模式下标`；
        /// "取消（不打出）"回 `Cancelled=true`（服务端不扣费、不入堆叠）。
        /// </summary>
        private void BuildEffectMode(ChoiceRequest request)
        {
            var labels = request.OptionLabels ?? new System.Collections.Generic.List<string>();
            for (int i = 0; i < labels.Count; i++)
            {
                int captured = i;
                var btn = new Button { Text = labels[i] ?? $"选项{captured + 1}" };
                btn.Pressed += () =>
                {
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
        private void BuildNumberInput(ChoiceRequest request)
        {
            int min = request.NumberMin;
            int max = System.Math.Max(min, request.NumberMax);
            int def = System.Math.Clamp(request.NumberDefault, min, max);

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
