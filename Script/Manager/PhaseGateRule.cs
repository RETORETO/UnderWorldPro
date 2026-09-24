using Godot;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Script.Manager
{
    /// <summary>
    /// 【K26·发动窗口细化】**阶段排除**的唯一判定实现（用户 2026-09 口径，二次修订）。
    ///
    /// 规则文本形态（JC008 灵能激发 FAQ）："灵能激发的快速行动能力**不能在分配战斗伤害阶段使用**"。
    /// **口径（用户 2026-09 明确）**：这里说的"分配战斗伤害阶段"**只指"伤害分配窗口打开的那一小段"**，
    /// **不是**整个 `TurnPhase.CombatStrength` 阶段——即：
    ///  · 战斗对抗窗口打开期间（玩家正在分配伤害）→ 该效果不可发动 / 该卡不可打出；
    ///  · 窗口之外（哪怕仍处于战斗对抗阶段的前后）→ 不受限制。
    ///
    /// 因此数据侧只保留**阶段名清单** `EffectData.ExcludedPhases`（逗号分隔，空=无排除），
    /// 判定侧按当前状态求值；已登记的"阶段名 → 判据"只有一条（与用户规则文本一致）：
    ///  · `"分配战斗伤害阶段"` → `TurnManager.IsDamageAssignmentOpen`（由 `CombatManager.IsDamageAssignmentOpen` 提供，
    ///    单机在 `OpenCombatWindow` 置位/窗口关闭回调复位，联机在 `ResolveWinnerAsync` 的 await 前后置位/复位）。
    /// 未登记的阶段名按"不排除"处理并告警（防拼错静默失效 / 静默全禁）。
    ///
    /// 消费点（三处共用本实现，保证单机高亮/联机投影/服务端权威同解）：
    ///  · `ActivateEligibility.CanActivate` / `GetEligibleAbilities`（可发动判定与候选）
    ///  · `EffectEngine.ActivateAbility`（服务端权威门禁）
    ///  · `TurnManager.PlayCard`（事务卡：卡上任一 `OnSorceryCast` 效果排除当前状态 → 此时不可打出）
    /// </summary>
    public static class PhaseGateRule
    {
        /// <summary>"分配战斗伤害阶段"的登记名（与卡面/FAQ 用词一致）。</summary>
        public const string DamageAssignmentPhaseName = "分配战斗伤害阶段";

        /// <summary>该效果的 `ExcludedPhases` 里是否有**未登记**的阶段名（用于告警去重）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> _warned = new();

        /// <summary>某个"阶段名"当前是否成立（未登记返回 false 并告警一次）。</summary>
        public static bool IsTokenActive(string token, TurnManager tm)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (token == DamageAssignmentPhaseName) return tm != null && tm.IsDamageAssignmentOpen;
            if (_warned.Add(token))
            {
                GD.PushWarning($"[PhaseGateRule] 未登记的阶段名「{token}」→ 一律按『不排除』处理；" +
                               $"如需生效请在 `IsTokenActive` 登记（现有登记：{DamageAssignmentPhaseName}）");
            }
            return false;
        }

        /// <summary>该效果在当前状态下是否被**排除**（true = 不可发动 / 不可打出）。</summary>
        public static bool IsExcluded(EffectData e, TurnManager tm)
        {
            if (e == null || string.IsNullOrEmpty(e.ExcludedPhases)) return false;
            foreach (var raw in e.ExcludedPhases.Split(','))
                if (IsTokenActive(raw.Trim(), tm)) return true;
            return false;
        }

        /// <summary>
        /// 事务卡打出用的整卡判定：卡上任一效果（通常是 `OnSorceryCast`）在当前状态被排除 → 该卡不可打出。
        /// 只扫 `ExcludedPhases` 非空的效果，避免影响既有卡。
        /// </summary>
        public static bool IsCardPlayExcluded(PlayerCardData tmpl, TurnManager tm)
        {
            if (tmpl?.EffectDataList == null) return false;
            foreach (var e in tmpl.EffectDataList)
            {
                if (e == null || string.IsNullOrEmpty(e.ExcludedPhases)) continue;
                if (IsExcluded(e, tm)) return true;
            }
            return false;
        }

        /// <summary>取"被排除时的提示文案"（供门禁日志）；无排除返回 null。</summary>
        public static string DescribeExclusion(EffectData e)
            => (e == null || string.IsNullOrEmpty(e.ExcludedPhases)) ? null : e.ExcludedPhases;

        /// <summary>整卡判定用：返回**当前成立**的那个阶段名（无则空串），供打出/激活日志说明原因。</summary>
        public static string DescribeCardExclusion(PlayerCardData tmpl, TurnManager tm)
        {
            if (tmpl?.EffectDataList == null) return "";
            foreach (var e in tmpl.EffectDataList)
            {
                if (e == null || string.IsNullOrEmpty(e.ExcludedPhases)) continue;
                foreach (var raw in e.ExcludedPhases.Split(','))
                {
                    string token = raw.Trim();
                    if (IsTokenActive(token, tm)) return token;
                }
            }
            return "";
        }

        /// <summary>供日志：当前是否处于"分配战斗伤害窗口打开"状态。</summary>
        public static string DescribeState(TurnManager tm)
            => (tm != null && tm.IsDamageAssignmentOpen) ? $"正处于「{DamageAssignmentPhaseName}」（伤害分配窗口打开中）" : "不在伤害分配窗口内";
    }
}
