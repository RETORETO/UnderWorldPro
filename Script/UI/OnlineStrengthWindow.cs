using Godot;
using Script.Game.Entities;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 【联机专用·战斗对抗窗口】与单机 `StrengthWindow` 的**视觉与操作完全一致**：
    /// 点击场上败方角色左键 +1 / 右键 -1、清空分配、剩余点数不足时禁用确认。
    ///
    /// 与单机版的差异（因此必须新建而不是复用）：
    ///  - **不做任何本地结算**：确认时把"预分配结果"交给 `OnConfirmed`（由 ArenaOl 封装成 ChoiceResult 上行），
    ///    真正的伤害由服务端在收到回包后统一结算；
    ///  - 合法承伤目标由 `IsLegalTarget` 注入判定（联机用服务端下发的合法目标集合，避免客户端复制规则）；
    ///  - 不持有 CardManager / CombatManager 等规则层依赖。
    /// </summary>
    [GlobalClass]
    public partial class OnlineStrengthWindow : Window
    {
        private Label _titleLabel;
        private Label _remainingLabel;
        private Label _hintLabel;
        private Button _clearButton;
        private Button _confirmButton;

        private int _winnerPlayer;
        private int _areaNo;
        private int _totalDamage;
        private int _remaining;
        private string _areaUid;
        private bool _confirmed;

        private readonly Dictionary<CardInstance, int> _allocations = new Dictionary<CardInstance, int>();

        /// <summary>合法承伤目标判定（由 ArenaOl 用服务端下发的合法目标集合注入）。</summary>
        public System.Func<CardInstance, bool> IsLegalTarget { get; set; }

        /// <summary>按实例取主战场 CardUI（用于显示/清除分配数字）。</summary>
        public System.Func<CardInstance, CardUI> FindCardUI { get; set; }

        /// <summary>确认回调：传入"实例 → 分配点数"（仅 >0 的条目），由调用方上行 ChoiceResult。</summary>
        public System.Action<Dictionary<CardInstance, int>> OnConfirmed { get; set; }

        public override void _Ready()
        {
            _titleLabel = GetNodeOrNull<Label>("RootControl/TitleLabel");
            _remainingLabel = GetNodeOrNull<Label>("RootControl/RemainingLabel");
            _hintLabel = GetNodeOrNull<Label>("RootControl/HintLabel");
            _clearButton = GetNodeOrNull<Button>("RootControl/ClearButton");
            _confirmButton = GetNodeOrNull<Button>("RootControl/ConfirmButton");
            if (_clearButton != null) _clearButton.Pressed += OnClearPressed;
            if (_confirmButton != null) _confirmButton.Pressed += OnConfirmPressed;
        }

        public void Configure(int winnerPlayer, int totalDamage, string areaUid, int areaNo)
        {
            _winnerPlayer = winnerPlayer;
            _totalDamage = totalDamage;
            _remaining = totalDamage;
            _areaUid = areaUid;
            _areaNo = areaNo;
            _allocations.Clear();
            _confirmed = false;
            if (_titleLabel != null) _titleLabel.Text = $"{areaNo}地区战斗对抗获胜";
            RefreshRemaining();
        }

        public int WinnerPlayer => _winnerPlayer;
        public string AreaUid => _areaUid;

        /// <summary>主战场卡被点击时由 ArenaOl 调用。buttonIndex: 1=左键(+1)，2=右键(-1)。</summary>
        public bool TryAllocate(CardUI cardUI, int buttonIndex)
        {
            if (cardUI?.Instance == null) return false;
            var inst = cardUI.Instance;
            if (IsLegalTarget == null || !IsLegalTarget(inst)) return false;

            int delta = buttonIndex == 2 ? -1 : 1;
            if (delta < 0)
            {
                int cur = _allocations.GetValueOrDefault(inst, 0);
                if (cur <= 0) return false;
            }
            else if (_remaining <= 0) return false;

            // 仅记录"预分配"，不写任何模型；确认时才交回调用方
            _allocations[inst] = _allocations.GetValueOrDefault(inst, 0) + delta;
            FindCardUI?.Invoke(inst)?.SetDamageAllocation(_allocations[inst]);
            _remaining -= delta;
            if (_remaining < 0) _remaining = 0;
            RefreshRemaining();
            return true;
        }

        private void RefreshRemaining()
        {
            if (_remainingLabel != null) _remainingLabel.Text = $"剩余可分配：{_remaining} 点";
            if (_confirmButton != null) _confirmButton.Disabled = _remaining != 0;
        }

        private void OnClearPressed()
        {
            foreach (var kv in _allocations)
                FindCardUI?.Invoke(kv.Key)?.ClearDamageDisplay();
            _allocations.Clear();
            _remaining = _totalDamage;
            RefreshRemaining();
        }

        private void OnConfirmPressed()
        {
            if (_confirmed) return;
            _confirmed = true;
            if (_confirmButton != null) _confirmButton.Disabled = true;

            var result = new Dictionary<CardInstance, int>();
            foreach (var kv in _allocations)
            {
                FindCardUI?.Invoke(kv.Key)?.ClearDamageDisplay();
                if (kv.Value > 0) result[kv.Key] = kv.Value;
            }
            _allocations.Clear();

            OnConfirmed?.Invoke(result);
            QueueFree();
        }
    }
}
