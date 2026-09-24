using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC102 妖火 效果（事务卡 / 快速行动）。
    /// 卡文：只能在行动阶段打出。快速行动：对目标角色造成1点伤害。
    /// 实现：行动阶段门禁 + 事务卡打出即结算 + `DealDamage{"1"}`（同一目标）。
    /// ⚠️ 引擎边界：伤害对"印刷无防御"的卡完全无效、对暗藏者无效（见实现清单 §二·补 约束 1/3）。
    /// </summary>
    public static class JC102_yaohuo_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC102_yaohuo", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;

            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC102_1_DealDamage",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "行动阶段打出；对目标角色造成 1 点伤害（不分敌我）。",
            });
        }
    }
}
