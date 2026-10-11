using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC127 终局 效果（事务卡；`Cost = 3`，子类别「灾难」）。
    /// 卡文：行动：选择目标地区，移除该地区上的所有势力标志。然后若该地区有三个或更多时间标志，
    ///      则消灭该地区上的所有角色。
    ///
    /// 实现（**判据㉑-a/㉑-b**：整段卡文只有 `行动：` **一个前缀段** ⇒ **一条效果、三个动作按序处理**）：
    ///  · 本体＝事务卡打出即结算（`OnSorceryCast`），**目标＝地区**（`TargetRules`/选择的目标地区）；
    ///  · ①「移除该地区上的**所有**势力标志」＝ 两条 `RemoveToken{"势力标志","全部",…,"目标地区"}`：
    ///    势力标志按玩家分桶存储（`AreaManager`），故"所有"＝**对手的全部 + 自己的全部**；
    ///  · ②「然后**若**该地区有三个或更多时间标志，**则**消灭该地区上的所有角色」＝ 同一条效果的**第三个动作**：
    ///    `Destroy` + `MassInArea = true`（对该地区内每个角色各执行一次）
    ///    + **动作级** `RequirePredicate = "AreaMarkerAtLeast"` / `RequireParam = "时间标志,3"`
    ///    （`EffectEngine.ActionConditionMet` ⇒ `EvaluateCondition(..., targetAreaId, ...)`，
    ///     `AreaMarkerAtLeast` 优先用**本次结算的目标地区**统计）—— 不满足则**跳过该动作**、不影响其它动作。
    ///  · `TargetSpec.MassSequential = true` 保证三个动作严格按声明顺序执行（与 JC048 诅咒沙漏同款原语）。
    ///
    /// 【2026-10 复核·修复】原实现把该卡文拆成**两条**同时机效果（A 清标志 / B 条件消灭）：
    ///  · 违反判据 **㉑-a**（一个前缀段 ⇒ 一条 `EffectData`）；
    ///  · 违反判据 **㉑-b**（段内"然后若…则…"次序敏感，却没有任何 `MassSequential`）。
    /// 现合并为一条效果（动作数组顺序＝结算顺序，动作级条件＝"然后若…则…"）。
    /// </summary>
    public static class JC127_zhongju_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC127_zhongju", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),        // 卡文"选择目标地区"
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC127_1_ClearPowerMarksThenDestroyAll",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,       // 【K2】"该地区上的所有角色"
                    MassSequential = true,               // 【㉑-b】三个动作按声明顺序执行（一条效果）
                    CanTargetSelf = true,                // 卡文"该地区上的**所有**角色"→ 含本方（与 JC050 同款写法）
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    // ① 移除该地区上的所有势力标志 —— 对手的全部（普通动作 ⇒ 只执行一次）
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "全部", "对手", "目标地区" },
                    },
                    // ② 自己的全部
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "全部", "自己", "目标地区" },
                    },
                    // ③ 然后若该地区时间标志 ≥ 3 → 消灭该地区上的所有角色（动作级条件；不满足则跳过本动作）
                    new EffectAction
                    {
                        Verb = ActionVerb.Destroy,
                        MassInArea = true,
                        RequirePredicate = "AreaMarkerAtLeast",
                        RequireParam = "时间标志,3",
                    },
                },
                Duration = "瞬时",
                FaqNote = "选择目标地区：移除该地区上的所有势力标志（双方的全部）；"
                        + "然后若该地区上有三个或更多时间标志，则消灭该地区上的所有角色。"
                        + "（一条效果内的三个动作按序处理。）",
            });
        }
    }
}
