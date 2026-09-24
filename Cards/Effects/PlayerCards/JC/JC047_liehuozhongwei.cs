using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC047 烈火重围 效果（事务卡）。
    /// 卡文：行动：对目标地区上的所有角色造成1点伤害。
    ///
    /// 实现（P3·K2 群体动作）：打出目标＝地区；群体动作作用地区＝打出时选定的目标地区；
    /// 动作 `DealDamage{"1"}` 对该地区内所有角色各执行一次（一次结算内完成）。
    /// ⚠️ 引擎硬约束（§二·补 #1/#3）：**印刷无防御的卡不受伤害**、**暗藏者免疫伤害**——故本卡对这两类卡无实际效果（与卡面无冲突，只是"角色"的范围受限）。
    /// </summary>
    public static class JC047_liehuozhongwei_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC047_liehuozhongwei", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };
            data.LimitToActionPhaseOnly = true;   // "行动"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC047_1_DamageAllInChosenArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,     // "所有角色"（不含暗藏者）
                    MassMode = MassMode.AllInArea,
                    CanTargetSelf = true,
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "对选定地区上的所有角色各造成 1 点伤害；无印刷防御的卡与暗藏者按引擎约束不受伤害。",
            });
        }
    }
}
