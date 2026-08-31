using Godot;
using Script.Game.Entities;
using Script.UI;
using System.Collections.Generic;

/// <summary>
/// 战斗对抗窗口（StrengthWindow）。
/// 战斗对抗胜利后由 Strength 调用 Configure(winnerPlayer, totalDamage, areaUid) 弹出。
/// 窗口只显示剩余可分配伤害；玩家点击【主战场真实 CardUI】分配伤害
/// （左键 +1，右键 -1），分配写入 CardInstance.Damage 并触发既有死亡流程。
/// 仅允许分配"败方在指定地区(areaUid)内的角色卡"。
/// 伤害结算统一经由 CardManager.DealDamageToInstance，避免 View 直接改 Model。
/// </summary>
public partial class StrengthWindow : Window
{
    private Label _titleLabel;
    private Label _remainingLabel;
    private Label _hintLabel;
    private Button _clearButton;
    private Button _confirmButton;

    private int _winnerPlayer;
    /// <summary>当前对抗所属的地区序号（1-based），用于窗口标题显示。</summary>
    private int _areaNo;
    private int _loserPlayer;
    private int _totalDamage;
    private int _remaining;
    private string _areaUid;

    /// <summary>本窗口对每个卡分配的伤害量（与 CardInstance.Damage 同步，便于 Clear 回退）</summary>
    private readonly Dictionary<CardInstance, int> _allocations = new Dictionary<CardInstance, int>();

    public override void _Ready()
    {
        _titleLabel = GetNode<Label>("RootControl/TitleLabel");
        _remainingLabel = GetNode<Label>("RootControl/RemainingLabel");
        _hintLabel = GetNode<Label>("RootControl/HintLabel");
        _clearButton = GetNode<Button>("RootControl/ClearButton");
        _confirmButton = GetNode<Button>("RootControl/ConfirmButton");

        _clearButton.Pressed += OnClearPressed;
        _confirmButton.Pressed += OnConfirmPressed;
    }

    public void Configure(int winnerPlayer, int totalDamage, string areaUid, int areaNo)
    {
        _winnerPlayer = winnerPlayer;
        _loserPlayer = winnerPlayer == 1 ? 2 : 1;
        _totalDamage = totalDamage;
        _remaining = totalDamage;
        _areaUid = areaUid;
        _areaNo = areaNo;
        _allocations.Clear();

        if (_titleLabel != null) _titleLabel.Text = $"{areaNo}地区战斗对抗获胜";
        RefreshRemaining();
        GD.Print($"【战斗窗口】已打开：胜者P{_winnerPlayer}，败方P{_loserPlayer}，总伤害 {_totalDamage}，地区 {areaUid}");
    }

    public int WinnerPlayer => _winnerPlayer;
    public string AreaUid => _areaUid;

    /// <summary>主战场卡被点击时由 Strength 调用。buttonIndex: 1=左键(+1)，2=右键(-1)。</summary>
    public bool TryAllocate(CardUI cardUI, int buttonIndex)
    {
        if (cardUI?.Instance == null) return false;
        var inst = cardUI.Instance;

        // 合法承伤目标判定交由 CombatManager（规则层），View 不内嵌游戏规则。
        // 兜底：未注入 CombatManager 时保留原判定逻辑，避免崩溃。
        bool legal = CombatManager != null
            ? CombatManager.IsLegalStrengthTarget(inst, _loserPlayer, _areaUid)
            : (inst.ControllerId == _loserPlayer
               && inst.CurrentZone == Script.Enums.CardZone.AreaZone
               && inst.ZoneDetailId == _areaUid
               && inst.HasDefense
               && !inst.IsCovert);
        if (!legal) return false;

        int delta = buttonIndex == 2 ? -1 : 1;

        // 右键减少：不能超过已分配量
        if (delta < 0)
        {
            int cur = _allocations.GetValueOrDefault(inst, 0);
            if (cur <= 0) return false;
        }
        // 左键增加：不能超过剩余点数
        else if (_remaining <= 0) return false;

        // 仅记录"预分配"，不写入 CardInstance；真正的伤害在点击【确认】时统一结算
        _allocations[inst] = _allocations.GetValueOrDefault(inst, 0) + delta;
        cardUI.SetDamageAllocation(_allocations[inst]);

        _remaining -= delta;
        if (_remaining < 0) _remaining = 0;
        RefreshRemaining();
        return true;
    }

    private void RefreshRemaining()
    {
        if (_remainingLabel != null)
            _remainingLabel.Text = $"剩余可分配：{_remaining} 点";
        if (_confirmButton != null)
            _confirmButton.Disabled = _remaining != 0;
    }

    private void OnClearPressed()
    {
        // 预分配尚未写入实例，直接清空标记即可
        foreach (var kv in _allocations)
            FindCardUI?.Invoke(kv.Key)?.ClearDamageDisplay();
        _allocations.Clear();
        _remaining = _totalDamage;
        RefreshRemaining();
        GD.Print("【战斗窗口】已清空所有分配");
    }

    private bool _confirmed = false;

    private void OnConfirmPressed()
    {
        if (_confirmed) return;
        _confirmed = true;
        if (_confirmButton != null) _confirmButton.Disabled = true;

        // 此刻才真正结算伤害：统一经由 CardManager 写入 Model 并触发既有死亡流程，
        // 不直接调用 CardInstance.TakeDamage，避免 View 越权修改数据层。
        foreach (var kv in _allocations)
        {
            var inst = kv.Key;
            int alloc = kv.Value;
            FindCardUI?.Invoke(inst)?.ClearDamageDisplay();
            if (alloc > 0)
            {
                // 统一经 CardManager 结算（含死亡判定与墓地处理），View 不直接写 Model。
                // CardManager 由 Arena 注入，正常路径必不为 null；若为 null 则跳过写入，
                // 避免"只改 Model 伤害却不走墓地处理"导致数据不一致（不再兜底直写 Model）。
                if (CardManager != null)
                {
                    CardManager.DealDamageToInstance(inst, alloc);
                    GD.Print($"  → {inst.PlayerTemplate?.CardName} 受到 {alloc} 点伤害");
                }
                else
                {
                    GD.PrintErr($"  ⚠️ [战斗窗口] CardManager 未注入，跳过 {inst.PlayerTemplate?.CardName} 的伤害结算");
                }
            }
        }

        GD.Print($"【战斗窗口】确认分配完成，共结算 {_allocations.Count} 个目标");
        OnResolved?.Invoke();
        QueueFree();
    }

    /// <summary>确认结算后回调（供 Strength 刷新显示 / 推进流程）。</summary>
    public System.Action OnResolved { get; set; }

    // Strength 注入：根据实例找主战场 CardUI（用于清显示）
    public System.Func<CardInstance, CardUI> FindCardUI { get; set; }

    /// <summary>伤害结算统一入口（由 Arena 注入）。View 不直接修改 CardInstance 的伤害字段。</summary>
    public CardManager CardManager { get; set; }

    /// <summary>合法承伤目标判定（由 Arena 注入）。View 不再内嵌游戏规则。</summary>
    public CombatManager CombatManager { get; set; }
}
