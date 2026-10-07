using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC132 构筑静态障壁 效果（事务卡）。
    /// 卡文：行动：选择目标地区，横置所有位于该地区的角色。
    ///
    /// 实现（P3·K2 群体动作）：打出目标＝地区；群体动作作用地区＝打出时选定的目标地区；
    /// 动作 `Tap` 对该地区内所有角色各执行一次（一次结算内完成）。
    /// </summary>
    public static class JC132_gouzhujingtaizhangbi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC132_gouzhujingtaizhangbi", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC132_1_TapAllInChosenArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,
                    CanTargetSelf = true,
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                },
                Duration = "瞬时",
                FaqNote = "横置选定地区上的所有角色（不分敌我；群体动作一次结算完成）。",
            });
        }
    }
}
