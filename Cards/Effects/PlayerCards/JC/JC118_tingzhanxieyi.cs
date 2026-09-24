using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC118 停战协议 效果（事务卡 / 策略）。
    /// 卡文：只能在行动阶段打出。快速行动：选择目标地区，本回合中该地区略过对抗阶段。
    ///
    /// 实现（**K18 第二批·本回合型地区级"略过对抗"**，用户 2026-09 口径：**三步骤全部略过**）：
    ///  · 前缀"只能在行动阶段打出"→ `LimitToActionPhaseOnly = true`（既有打出门禁；与 JC091 同一写法）；
    ///  · 快速行动 → 本效果可直接结算（`OnSorceryCast`），打出目标＝**地区**（`TargetRules = Area`）；
    ///  · `ActionVerb.SkipAreaCombat{"调查,战斗,势力"}`：对**本次结算的目标地区**登记**本回合**时效
    ///    （`TimedMod{Kind="AreaCombatSkip", TargetUid=地区UID}`，回合结束由 `ExpireTurnEndEffects` 出栈）；
    ///  · 消费点＝`TurnManager.ResolveCurrentAreaCombat`（三步骤均不结算对抗，但仍给出让过窗口，流程照常推进）。
    /// </summary>
    public static class JC118_tingzhanxieyi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC118_tingzhanxieyi", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;              // "只能在行动阶段打出"

            // 打出目标＝地区（"选择目标地区"）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC118_1_SkipAllCombatsInAreaThisTurn",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,           // 受 LimitToActionPhaseOnly 约束（与 JC091 同款）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Area,
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    // 三步骤全部略过（用户口径）
                    new EffectAction { Verb = ActionVerb.SkipAreaCombat, Args = new Array<string> { "调查,战斗,势力" } },
                },
                Duration = "直到回合结束",
                FaqNote = "选择目标地区：本回合该地区的调查/战斗/势力三步骤**全部略过**（不结算对抗，但仍给优先权窗口）。",
            });
        }
    }
}
