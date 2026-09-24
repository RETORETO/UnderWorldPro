using System.Collections.Generic;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Manager
{
    /// <summary>
    /// 【K24·玩家目标】"以**玩家**为目标"的候选枚举 + 合法性判定的**单一实现**（用户 2026-09 口径）。
    ///
    /// 口径（已与用户确认）：
    ///  · 允许指定**任意**对手（甚至自己）；卡文"目标对手/目标敌方玩家"＝`Restrictions` 含 `Enemy`；
    ///  · 需要**独立的选择窗口**（玩家不是场上的卡节点，无法用"点选/拖拽到目标"表达）；
    ///  · 目标在**入堆叠时锁定**（与卡目标同口径），结算时不再重选。
    ///
    /// 与卡目标的差异（因此不能复用 `TargetZonePool`/`MassTargetPool`）：
    ///  · 玩家没有 `CardInstance`，故判定直接吃 **玩家号**；
    ///  · "敌我"基准＝**来源控制者**（打出/激活者），与 `TargetValidity` 的 Friendly/Enemy 同语义；
    ///  · 玩家不因"离场/翻面"而丢失，故没有区域/版本号校验（`TargetValidity.CheckZoneAndFace` 对 Player 恒真）。
    ///
    /// 本类只做"候选与判定"，不落地选择窗口、不改对局状态（View/Controller 各司其职）。
    /// </summary>
    public static class PlayerTargetPool
    {
        /// <summary>该目标描述是否指向"玩家"大类（`Kind` 或 `KindAlt` 任一为 `Player`）。</summary>
        public static bool IsPlayerSpec(TargetSpec spec)
            => spec != null && (spec.Kind == TargetKind.Player || spec.KindAlt == TargetKind.Player);

        /// <summary>眼下对局中的全部玩家号（确定性顺序＝升序）。`PlayerManager` 为双人实现对局（Player1/Player2）。</summary>
        public static List<int> AllPlayers(PlayerManager pm)
        {
            var list = new List<int>();
            if (pm == null) return list;
            if (pm.Player1 != null) list.Add(1);
            if (pm.Player2 != null) list.Add(2);
            if (list.Count == 0) { list.Add(1); list.Add(2); }   // 未初始化兜底：仍是双人对局
            return list;
        }

        /// <summary>
        /// 取候选玩家（按玩家号升序）。
        /// `sourceController` ＝ 来源卡控制者（"自己/对手"的比较基准）；&lt;0 时视为未定 → **空候选**（fail-closed）。
        /// </summary>
        public static List<int> Collect(TargetSpec spec, int sourceController, PlayerManager pm)
        {
            var result = new List<int>();
            if (!IsPlayerSpec(spec)) return result;
            foreach (int pid in AllPlayers(pm))
                if (Matches(spec, pid, sourceController, pm, out _)) result.Add(pid);
            return result;
        }

        /// <summary>
        /// 玩家 `playerId` 是否满足该目标描述（取候选与结算复检**共用同一判定**）。
        /// 支持的限制条件只有 `None`（任意）/`Friendly`（本方）/`Enemy`（对手）；
        /// 其余限制条件（如 `CostAtLeast` 这类卡属性）对玩家**无意义** → **fail-closed**（不通过 + 告警）。
        /// </summary>
        public static bool Matches(TargetSpec spec, int playerId, int sourceController, PlayerManager pm,
            out string reason)
        {
            reason = null;
            if (!IsPlayerSpec(spec)) { reason = "该目标不是玩家目标"; return false; }
            if (pm == null) { reason = "PlayerManager 缺失（无法枚举玩家）"; return false; }
            if (playerId != 1 && playerId != 2) { reason = $"玩家{playerId} 不存在"; return false; }
            if (sourceController != 1 && sourceController != 2)
            { reason = "来源控制者未知，无法判定敌我"; return false; }

            if (spec.Restrictions != null)
            {
                foreach (var r in spec.Restrictions)
                {
                    switch (r)
                    {
                        case TargetRestriction.None:
                            break;
                        case TargetRestriction.Friendly:
                            if (playerId != sourceController) { reason = "该目标必须是本方玩家"; return false; }
                            break;
                        case TargetRestriction.Enemy:
                            if (playerId == sourceController) { reason = "该目标必须是对手玩家"; return false; }
                            break;
                        default:
                            reason = $"玩家目标不支持限制条件「{r}」→ 按不通过处理（fail-closed）";
                            Godot.GD.PushWarning($"[PlayerTargetPool] {reason}");
                            return false;
                    }
                }
            }

            // 可选的文本写法（`Filter`）：卡文里的"目标对手/目标敌方玩家/目标玩家"直接写进文本时也能表达。
            // 未知写法 → fail-closed（与 `DeckSearchFilter` 同一纪律）。
            string f = spec.Filter?.Trim() ?? "";
            if (f.Length > 0)
            {
                switch (f)
                {
                    case "任意":
                    case "任意玩家":
                        break;
                    case "对手":
                    case "敌方玩家":
                    case "目标对手":
                        if (playerId == sourceController) { reason = "该目标必须是对手玩家"; return false; }
                        break;
                    case "自己":
                    case "本方玩家":
                        if (playerId != sourceController) { reason = "该目标必须是本方玩家"; return false; }
                        break;
                    default:
                        reason = $"未登记的玩家目标写法「{f}」→ 按不通过处理（fail-closed）";
                        Godot.GD.PushWarning($"[PlayerTargetPool] {reason}");
                        return false;
                }
            }
            return true;
        }

        /// <summary>玩家显示名（日志/选择窗口用）。</summary>
        public static string Describe(PlayerManager pm, int playerId)
            => pm?.GetPlayer(playerId)?.Name ?? $"玩家{playerId}";

        /// <summary>目标描述的人类可读说明（选择窗口标题/日志用）。</summary>
        public static string DescribeSpec(TargetSpec spec)
        {
            if (spec == null) return "玩家";
            if (spec.Restrictions != null && spec.Restrictions.Contains(TargetRestriction.Enemy)) return "目标对手";
            if (spec.Restrictions != null && spec.Restrictions.Contains(TargetRestriction.Friendly)) return "目标本方玩家";
            return "目标玩家";
        }
    }
}
