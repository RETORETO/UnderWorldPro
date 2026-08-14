using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC002 巨石阵看护人 效果（自原 JC002_jushizhenkanhuren 构造函数迁移，逻辑不变）。
    /// 现身触发：横置本地区的目标角色。
    /// </summary>
    public static class JC002_jushizhenkanhuren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC002_jushizhenkanhuren", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC002_EnterTap",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = true,
                    IsLocalZone = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                },
            });
        }
    }
}
