using Godot;
using Script.Manager;
using System;

/// <summary>
/// 先手破平确认窗口（TieBreakWindow，2026-08-05）。
/// MVC：本类为纯 View，不写任何数据层（费用/次数消耗由 PlayerManager 在逻辑层完成）。
/// 仅通过 onResolved 回调把玩家意图（确认/取消）交回 Controller（CombatManager → ModalWindowCoordinator）。
/// 出现时机：三种对抗出现平局且先手玩家可破平时，由 ModalWindowCoordinator 订阅 OnTieBreakRequired 弹出。
/// </summary>
public partial class TieBreakWindow : Window
{
    // ---------- 场景节点 ----------
    private Label _titleLabel;
    private Label _promptLabel;
    private Button _confirmButton;
    private Button _cancelButton;

    // ---------- 状态 / 回调 ----------
    private System.Action<bool> _onResolved;

    public override void _Ready()
    {
        _titleLabel = GetNode<Label>("RootControl/TitleLabel");
        _promptLabel = GetNode<Label>("RootControl/PromptLabel");
        _confirmButton = GetNode<Button>("RootControl/ConfirmButton");
        _cancelButton = GetNode<Button>("RootControl/CancelButton");

        _confirmButton.Pressed += OnConfirmPressed;
        _cancelButton.Pressed += OnCancelPressed;
    }

    /// <summary>
    /// 配置窗口并显示文案。
    /// areaNo 为 1-based 地区序号；type 为对抗类型（用于文案如「调查/战斗/势力对抗」）；
    /// onResolved(true)=确认破平 / onResolved(false)=取消（平局无效果）。
    /// </summary>
    public void Configure(int areaNo, CombatManager.CombatType type, System.Action<bool> onResolved)
    {
        _onResolved = onResolved;
        string typeName = type switch
        {
            CombatManager.CombatType.Investigation => "调查",
            CombatManager.CombatType.Strength => "战斗",
            CombatManager.CombatType.Power => "势力",
            _ => "未知"
        };
        if (_promptLabel != null)
            _promptLabel.Text = $"地区{areaNo} {typeName}对抗打平。\n作为先手玩家，是否使用「先手破平特权」？\n（消耗 1 费用 + 1 次破平次数，以 +1 获胜）";
    }

    private void OnConfirmPressed()
    {
        GD.Print("[TieBreakWindow] 玩家确认破平");
        _onResolved?.Invoke(true);
        QueueFree();
    }

    private void OnCancelPressed()
    {
        GD.Print("[TieBreakWindow] 玩家取消破平");
        _onResolved?.Invoke(false);
        QueueFree();
    }
}
