using Godot;
using Script.Game.Entities;
using Script.Data.Cards;
using Script.Manager;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// CombatManager — 三种对抗（调查/战斗/势力）的计算与结算（纯逻辑层）。
///
/// 规则依据（核心需求 V3.0 + 补充）：
/// - 对抗阶段按地区编号从小到大，每个地区依次结算三种对抗（调查→战斗→势力），各自独立。
/// - 双方在本地区（己方争夺区）的【未横置】角色/附属/暗藏者对应能力分别求和。
/// - 高者胜，差值 = 获胜点数；相等(差值=0，含双方均0)为平局，本轮无效果。
/// - 横置(IsTapped)的角色/附属/暗藏者【不提供】任何能力值（含先手加成），但仍可被战斗对抗分配伤害。
/// - 暗藏者(IsCovert)：调查=0、战斗=0、势力=1（+先手势力加成）；被横置同样归零。
/// - 先手加成：若当前先手玩家是该卡操控者(ControllerId)，加算对应先手能力。
///
/// 产出形式（正确规则）：
/// - 调查对抗：胜者打开调查对抗窗口，从牌库顶查看 diff 张分配放回顶/底并抽 1 张（交互式）。
/// - 战斗对抗：败方打开战斗对抗窗口，分配 diff 点伤害给该地区角色（交互式）。
/// - 势力对抗：胜者每获胜 1 点 → 添加 1 势力标记（即差值本身），暂代逻辑保留。
/// 调查/战斗对抗为交互式窗口，需等待玩家逐地区结算完毕才回调 onComplete（由 TurnManager 推进阶段）。
///
/// 边界约束：CombatManager 不直接持有或调用任何 UI（Arena）节点。需要打开交互窗口时，
/// 通过 OnCombatWindowRequired 事件外抛（winner, diff, areaNo, onResolved），由 Arena 订阅并负责实例化窗口。
/// </summary>
public partial class CombatManager : Node
{
    private CardManager _cardManager;
    private PlayerManager _playerManager;
    private ZoneManager _zoneManager;
    private AreaManager _areaManager;

    public void Initialize(CardManager cardManager, PlayerManager playerManager,
                           ZoneManager zoneManager, AreaManager areaManager)
    {
        _cardManager = cardManager;
        _playerManager = playerManager;
        _zoneManager = zoneManager;
        _areaManager = areaManager;
        GD.Print("✅ CombatManager 依赖注入完成");
    }

    /// <summary>需要打开对抗窗口时外抛（纯数据，不依赖 UI）。
    /// 参数：(type, winnerPlayer, diff, areaNo(1-based), onResolved)。由 Arena 订阅并实例化对应窗口。</summary>
    public event Action<CombatType, int, int, int, System.Action> OnCombatWindowRequired;

    /// <summary>需要弹出"先手破平确认窗口"时外抛（纯数据，不依赖 UI）。
    /// 参数：(type, areaNo(1-based), areaUid, onResolved)。由 Arena 订阅并实例化破平窗口；
    /// 玩家点击后回调 onResolved(true)=确认破平 / onResolved(false)=取消（平局无效果）。
    /// 无订阅方时按"不可破平"处理（平局直接继续）。</summary>
    public event System.Action<CombatType, int, string, System.Action<bool>> OnTieBreakRequired;

    /// <summary>破平消耗费用（横置资产）后触发，供 UI 刷新资产横置表现。MVC：逻辑层只发事件，不触 UI。</summary>
    public event System.Action OnAssetsChanged;

    // ============================================================
    // 对外：单地区单对抗结算入口（TurnManager 调用）
    // 规则（核心需求 4.3）：按地区编号从小到大，每个地区内依次结算 调查→战斗→势力，
    // 且每一种对抗结算完毕后都要经过一次"双方连续让过"窗口才进入下一种对抗/下一地区。
    // 因此流程控制（地区游标 + 让过窗口）由 TurnManager 持有，CombatManager 只负责
    // "结算指定地区的指定对抗"这一原子操作，完毕后回调 onComplete。
    // ============================================================
    /// <summary>当前所有地区 UID，按【真实槽位编号升序】（由 AreaManager 权威确定，与 UI 显示一致）。
    /// 供 TurnManager 建立地区游标，避免按卡牌插入顺序导致的串地区错位。</summary>
    public System.Collections.Generic.List<string> GetOrderedAreaUids()
        => _areaManager?.GetOrderedAreaUids() ?? new System.Collections.Generic.List<string>();

    /// <summary>
    /// 结算"指定地区的指定对抗"（原子操作）。
    /// 调查/战斗为交互式窗口，窗口关闭后回调 onComplete；势力为瞬时结算，立即回调；平局直接回调。
    /// </summary>
    /// <param name="type">对抗类型（调查/战斗/势力）</param>
    /// <param name="areaUid">地区 UID</param>
    /// <param name="areaNo">1-based 地区序号（与 Arena._areaSlots 顺序一致）</param>
    /// <param name="onComplete">本次对抗结算完毕回调（由 TurnManager 接管让过窗口）</param>
    public void ResolveAreaCombat(CombatType type, string areaUid, int areaNo, System.Action onComplete)
    {
        if (string.IsNullOrEmpty(areaUid)) { onComplete?.Invoke(); return; }

        // 先统一计算胜负（含平局），以便三种对抗都以一致格式输出"获胜方 / 获胜地区 / 获胜点数"
        var (winner, diff) = ComputeResult(areaUid, type);

        if (winner == 0)
        {
            // —— 平局分支：先手破平特权判定（2026-08-05）——
            int firstPlayer = (_playerManager != null && _playerManager.IsFirstPlayer(1)) ? 1 : 2;
            int firstValue = SumPlayerValue(areaUid, firstPlayer, type);

            // 规则：0:0 的平局不可用破平；只有 1:1 或更高平局才可破平。
            // 等价于"平局时先手方在该对抗的原始值不为 0"。
            if (firstValue == 0)
            {
                GD.Print($"  📍 地区{areaNo}({areaUid}) {TypeName(type)}：0:0 平局，不可使用先手破平");
                onComplete?.Invoke();
                return;
            }

            if (_playerManager != null && _playerManager.CanTieBreak(firstPlayer))
            {
                GD.Print($"  ⚖️ 地区{areaNo}({areaUid}) {TypeName(type)}：平局，先手玩家{firstPlayer} 可破平 → 请求确认窗口");
                if (OnTieBreakRequired == null)
                {
                    GD.Print($"  ⚠️ 无 UI 订阅破平窗口事件，按平局继续");
                    onComplete?.Invoke();
                    return;
                }
                // 暂停流程，等玩家在窗口中确认/取消
                OnTieBreakRequired?.Invoke(type, areaNo, areaUid, resolved =>
                {
                    if (resolved)
                    {
                        // 确认破平：消耗 1 费用 + 1 次数，按「先手方获胜 1 点」继续结算
                        _playerManager.ConsumeTieBreak(firstPlayer);
                        OnAssetsChanged?.Invoke();   // 资产横置变化，通知 UI 刷新
                        GD.Print($"  ⚡ 先手破平生效：玩家{firstPlayer} 以 +1 获胜");
                        ResolveWinner(type, areaUid, areaNo, firstPlayer, 1, onComplete);
                    }
                    else
                    {
                        // 取消：平局无效果，继续下一步
                        GD.Print($"  📍 地区{areaNo}({areaUid}) {TypeName(type)}：玩家取消破平，平局无效果");
                        onComplete?.Invoke();
                    }
                });
                return;
            }

            GD.Print($"  📍 地区{areaNo}({areaUid}) {TypeName(type)}：平局，先手玩家不可破平（无费用或次数耗尽），无获胜方");
            onComplete?.Invoke();
            return;
        }

        ResolveWinner(type, areaUid, areaNo, winner, diff, onComplete);
    }

    /// <summary>胜方结算分支（从 ResolveAreaCombat 抽出，供平局破平后复用）。</summary>
    private void ResolveWinner(CombatType type, string areaUid, int areaNo, int winner, int diff, System.Action onComplete)
    {
        // 统一的对抗结果输出：获胜方、获胜地区、获胜点数
        GD.Print($"  🏆 地区{areaNo}({areaUid}) {TypeName(type)} 结算结果 — 获胜方: 玩家{winner}，获胜地区: 地区{areaNo}，获胜点数: {diff}");

        // 势力对抗：瞬时结算（加势力标记），无交互窗口
        if (type == CombatType.Power)
        {
            _areaManager?.AddPowerMark(areaUid, winner, diff);
            onComplete?.Invoke();
            return;
        }

        // 调查/战斗对抗：打开交互窗口，窗口关闭后回调
        // 战斗对抗获胜广播（威名类触发）：在原子操作结算完毕、窗口关闭后（onComplete 由 TurnManager 原子释放后调度）
        // 广播 CombatWon 事件；胜者玩家写入 TriggeringPlayerId，战斗地区写入 TargetAreaId。
        // 按 D1 约定：此处不填 SourceCard（由 TriggerRegistry 收集时按"在场威名卡"各自注入 source）。
        if (type == CombatType.Battle)
        {
            TriggerEmitPoints.CombatWon(winner, areaUid);
        }
        OpenCombatWindow(type, areaUid, areaNo, winner, diff, onComplete);
    }

    public enum CombatType { Investigation, Battle, Power }

    /// <summary>计算某地区该对抗类型的胜负与差值。纯计算语义：平局返回 (0, 0)，不含破平决策。
    /// 破平（先手破平特权）由 ResolveAreaCombat 在平局分支中依据规则异步处理。</summary>
    private (int winner, int diff) ComputeResult(string areaUid, CombatType type)
    {
        int p1 = SumPlayerValue(areaUid, 1, type);
        int p2 = SumPlayerValue(areaUid, 2, type);
        if (p1 == p2) return (0, 0);
        int winner = p1 > p2 ? 1 : 2;
        return (winner, Math.Abs(p1 - p2));
    }

    /// <summary>通过事件外抛"打开对抗窗口"请求（不直接调用 UI）。无订阅方时按平局跳过。</summary>
    private void OpenCombatWindow(CombatType type, string areaUid, int areaNo, int winner, int diff, System.Action onResolved)
    {
        if (OnCombatWindowRequired == null)
        {
            GD.PrintErr($"❌ [CombatManager] 无 UI 订阅对抗窗口事件，无法打开{TypeName(type)}窗口，按平局跳过");
            onResolved?.Invoke();
            return;
        }

        GD.Print($"  🔲 地区{areaNo}({areaUid}) {TypeName(type)}：请求打开对抗窗口(areaNo={areaNo})");
        // 事件参数：(type, winnerPlayer, diff, areaNo, onResolved) —— 由 Arena 订阅并实例化对应窗口
        OnCombatWindowRequired?.Invoke(type, winner, diff, areaNo, onResolved);
    }

    // ============================================================
    // 能力求和：委托 AreaStatsCalculator（规则单一收口，与 UI 显示共用同一份逻辑）
    // ============================================================
    private int SumPlayerValue(string areaUid, int playerId, CombatType type)
        => AreaStatsCalculator.SumPlayerValue(_zoneManager, _playerManager, areaUid, playerId, type);

    private static string TypeName(CombatType type) => type switch
    {
        CombatType.Investigation => "调查对抗",
        CombatType.Battle => "战斗对抗",
        _ => "势力对抗"
    };

    // ============================================================
    // 战斗对抗合法承伤目标判定（规则层，原内嵌于 BattleWindow.TryAllocate）
    // 规则（核心需求 6.4-b）：仅"败方在指定地区(AreaZone)内、且拥有防御值"的角色卡可被分配伤害。
    // ============================================================
    /// <summary>校验某卡是否为合法承伤目标（供 View 转发判定，规则本身由 Controller 持有）。</summary>
    public bool IsLegalBattleTarget(CardInstance inst, int loserPlayer, string areaUid)
    {
        if (inst == null) return false;
        if (inst.ControllerId != loserPlayer) return false;
        // 仅场上角色与附属可承伤；封印卡(SealedZone)不参战（Q4）。附属卡(AttachedZone)可承伤但受 HasDefense 约束（Q5）
        if (inst.CurrentZone != Script.Enums.CardZone.AreaZone
            && inst.CurrentZone != Script.Enums.CardZone.AttachedZone) return false;
        if (inst.ZoneDetailId != areaUid) return false;
        if (!inst.HasDefense) return false;
        // 暗藏者(IsCovert)潜伏状态，不受战斗对抗伤害影响：不可作为承伤目标
        if (inst.IsCovert) return false;
        return true;
    }

    /// <summary>获取某地区内败方所有合法承伤目标（供 UI 预检是否有可分配对象）。</summary>
    public int CountLegalBattleTargets(int winnerPlayer, string areaUid)
    {
        int loser = winnerPlayer == 1 ? 2 : 1;
        if (_zoneManager == null) return 0;
        int count = 0;
        foreach (var c in _zoneManager.GetAllCardsInAreaIncludingAttached(areaUid))
            if (IsLegalBattleTarget(c, loser, areaUid)) count++;
        return count;
    }
}
