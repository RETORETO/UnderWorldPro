using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Manager;
using Script.UI;
using System;

/// <summary>
/// Arena 分部（M-09 结构拆分）：基础行动蒙版（派遣暗藏 / 建立资产）+ 回合状态栏 / 按钮刷新。
///
/// 本文件仅为物理分文件，未做任何类提取：所有成员仍属 Arena，访问级别、调用关系、
/// 运行时行为与拆分前完全一致。原位置为 Arena.cs 行 1086-1264。
///
/// 蒙版状态 _activeMask 被 CardDragController 经 Arena.ActiveMask 读取（拖拽时判定
/// 是否走蒙版投放分支），故蒙版逻辑不从 Arena 中提取为独立类，避免新增 internal 通道。
/// </summary>
public partial class Arena : Control
{
    // ============================================================
    // 基础行动蒙版：按钮点击为纯 UI 切换（不消耗次数）
    // 规则：
    //  - 点一下进入蒙版，再点一下退出为普通可出牌状态
    //  - 点派遣暗藏后又点建立资产（或反之）→ 直接切换到另一蒙版
    //  - 次数为 0 禁止点击进入对应蒙版
    //  - 仅玩家在自己行动阶段内、且堆叠为空时才能进入蒙版
    // ============================================================
    private void OnDeployCovertButtonPressed()
    {
        ToggleMask(MaskMode.DeployCovert, () => _playerManager.HasDeployCovertCharge(1), "派遣暗藏");
    }

    private void OnEstablishAssetButtonPressed()
    {
        ToggleMask(MaskMode.EstablishAsset, () => _playerManager.HasEstablishAssetCharge(1), "建立资产");
    }

    /// <summary>基础行动蒙版的进入/退出切换（View 内收敛两按钮的对称逻辑）。
    /// 规则门禁（阶段/堆叠空/行动权）已上收 TurnManager，此处仅做 UI 态切换与次数提示。</summary>
    private void ToggleMask(MaskMode mode, Func<bool> hasCharge, string actionName)
    {
        if (_activeMask == mode)
        {
            ExitMask();                       // 再点一下 → 退出蒙版
            return;
        }
        if (!CanEnterActionMask())
        {
            GD.Print($"❌ 仅在自己行动阶段且堆叠为空时才能{actionName}");
            return;
        }
        if (!hasCharge())
        {
            GD.Print($"❌ 本回合{actionName}次数已用尽，禁止进入蒙版");
            return;
        }
        EnterMask(mode); // 进入 / 从另一基础行动蒙版切换过来
    }

    /// <summary>是否允许进入基础行动蒙版：玩家1在自己行动阶段内且堆叠为空且拥有优先权。</summary>
    /// <summary>
    /// 基础行动蒙版准入判定（UI 仅转发，规则门禁已上收至 TurnManager.CanEnterBasicActionMask）。
    /// 本蒙版服务于本地玩家 P1，故固定传入 playerId=1。
    /// </summary>
    private bool CanEnterActionMask()
    {
        if (_turnManager == null) return false;
        return _turnManager.CanEnterBasicActionMask(1);
    }

    /// <summary>进入指定蒙版并更新 UI 提示、按钮高亮与手牌卡蒙版。</summary>
    private void EnterMask(MaskMode mode)
    {
        _activeMask = mode;
        GD.Print($"[Arena] 进入蒙版: {mode}（可拖拽态）");
        // 进入蒙版即清除残留的打牌目标高亮（黄色蒙版），避免建立资产/派遣暗藏拖拽时旧高亮残留
        // 【MVC 修复 M-08】高亮清除已迁移至 CardDragController。
        _cardDragController?.ClearDragTargetHighlights();
        RefreshMaskUI();
    }

    /// <summary>退出蒙版（回到普通可出牌状态）。</summary>
    private void ExitMask()
    {
        if (_activeMask == MaskMode.None) return;
        GD.Print($"[Arena] 退出蒙版: {_activeMask}（恢复普通可出牌状态）");
        _activeMask = MaskMode.None;
        RefreshMaskUI();
    }

    /// <summary>【View 内绑定层】蒙版相关 UI 的统一刷新：提示文案 + 按钮高亮 + 手牌蒙版。
    /// 收敛 EnterMask/ExitMask 中散落的 3 连调，单一声明点避免漏刷（与 SyncDerivedUI 同思路）。</summary>
    private void RefreshMaskUI()
    {
        UpdateMaskHint();
        UpdateActionButtons();
        RefreshHandMask();
    }

    /// <summary>根据当前蒙版刷新所有手牌卡的蒙版覆盖：
    ///  - 派遣暗藏：仅角色卡显示灰色蒙版
    ///  - 建立资产：所有手牌显示绿色蒙版
    ///  - 普通状态：清除所有蒙版</summary>
    private void RefreshHandMask()
    {
        foreach (var card in _handCardUIs)
            if (card != null) ApplyMaskToOneCard(card);
    }

    /// <summary>按当前蒙版状态为单张手牌卡设置蒙版显示（离开手牌的卡不调用本方法，由 Sync 清除）。</summary>
    private void ApplyMaskToOneCard(CardUI card)
    {
        if (card == null) return;
        var grey = new Color(0.01f, 0.01f, 0.01f, 0.90f);   // 灰色蒙版（派遣暗藏·角色卡，加深）
        var green = new Color(0.2f, 0.8f, 0.3f, 0.55f); // 绿色蒙版（建立资产·所有手牌）

        switch (_activeMask)
        {
            case MaskMode.DeployCovert:
                // MVC 单一真相源：资格判定上收 TurnManager.CanDeployCovert，View 只调不问（避免双份规则漂移）。
                bool canCovert = _turnManager.CanDeployCovert(card.Instance);
                // 仅"可派遣暗藏"的卡打灰色蒙版（角色卡 且 无【公开】关键字）；
                // 事务卡/附属卡/公开卡均不打蒙版（不可暗藏）。
                card.SetMask(canCovert ? grey : (Color?)null);
                break;
            case MaskMode.EstablishAsset:
                card.SetMask(green);
                break;
            default:
                card.SetMask(null);
                break;
        }
    }

    /// <summary>刷新蒙版提示文本（规则1：蒙版持续显示直到该回合次数用尽）。</summary>
    private void UpdateMaskHint()
    {
        if (_maskHintLabel == null) return;
        switch (_activeMask)
        {
            case MaskMode.DeployCovert:
                _maskHintLabel.Text = "【派遣暗藏】仅可拖拽角色卡到己方争夺区";
                _maskHintLabel.Visible = true;
                break;
            case MaskMode.EstablishAsset:
                _maskHintLabel.Text = "【建立资产】拖拽手牌到资产区";
                _maskHintLabel.Visible = true;
                break;
            default:
                _maskHintLabel.Visible = false;
                break;
        }
    }

    /// <summary>刷新两个按钮的可用/高亮状态（次数用尽禁用；当前蒙版高亮）。</summary>
    private void UpdateActionButtons()
    {
        if (_deployCovertButton != null)
        {
            bool charge = _playerManager?.HasDeployCovertCharge(1) ?? false;
            _deployCovertButton.Disabled = !charge;
            _deployCovertButton.Modulate = (_activeMask == MaskMode.DeployCovert)
                ? new Color(0.7f, 1f, 0.7f) : Colors.White;
        }
        if (_establishAssetButton != null)
        {
            bool charge = _playerManager?.HasEstablishAssetCharge(1) ?? false;
            _establishAssetButton.Disabled = !charge;
            _establishAssetButton.Modulate = (_activeMask == MaskMode.EstablishAsset)
                ? new Color(0.7f, 1f, 0.7f) : Colors.White;
        }
    }

    // ============================================================
    // UI 更新
    // ============================================================
    private void UpdateTurnUI()
    {
        if (_phaseStatusLabel == null || _turnManager == null) return;

        // 四行显示：① 回合　② 优先权　③ 先手　④ 当前步骤中文名（中文步骤名统一由 TurnManager 提供）
        string turnText = $"回合: {_turnManager.TurnCount}";
        string priorityText = _turnManager.PriorityPlayerId > 0
            ? $"优先权: P{_turnManager.PriorityPlayerId}"
            : "优先权: 无";
        string firstText = $"先手: P{_turnManager.FirstPlayerId}";
        string phaseName = _turnManager.CurrentPhaseDisplayName;
        _phaseStatusLabel.Text = $"{turnText}\n{priorityText}\n{firstText}\n{phaseName}";
    }

    private void UpdateDeckCount() { }
}
