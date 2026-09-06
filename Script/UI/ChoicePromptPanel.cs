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

        private void AddCancelIfAllowed(ChoiceRequest request, string cancelLabel = "取消")
        {
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
