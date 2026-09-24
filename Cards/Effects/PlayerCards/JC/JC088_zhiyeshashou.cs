using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC088 职业杀手 效果（角色）。
    /// 卡文：现身触发：消灭位于本地区，且印刷费用小于或等于2的目标角色。
    ///      快速行动2：潜伏。（将该角色翻面为暗藏者）
    /// 实现：① 目标精筛用 P1 新增的 `CostAtMost`（印刷费用 ≤ Param）；
    ///      ② "潜伏"用 P1 新增的 `ActionVerb.Latent`（经 SetCovert 收口，翻面刷版本号）。
    /// </summary>
    public static class JC088_zhiyeshashou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC088_zhiyeshashou", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC088_1_DestroyCheapCharacter",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IsLocalZone = true,
                    CanTargetSelf = false,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.CostAtMost },
                    Param = "2",                      // 印刷费用 ≤ 2
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "现身进场后，消灭本地区印刷费用≤2 的目标角色。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC088_2_Latent",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Latent },   // 潜伏自身
                },
                Duration = "瞬时",
                FaqNote = "快速行动2：潜伏（将该角色翻面为暗藏者）；无横置代价，可重复潜伏。",
            });
        }
    }
}
