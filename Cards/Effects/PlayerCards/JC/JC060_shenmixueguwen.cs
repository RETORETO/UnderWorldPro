using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC060 神秘学顾问 效果（自原 JC060_shenmixueguwen 构造函数迁移，逻辑不变）。
    /// 持续光环（D19-③）：所有未印刷魔法领域图标的本方角色获得【心】图标和［黑刀］(+1战斗)。
    /// </summary>
    public static class JC060_shenmixueguwen_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC060_shenmixueguwen", Build);

        private static void Build(PlayerCardData data)
        {
            var aura = new EffectData
            {
                EffectId = "JC060_Aura",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                IsAura = true,
                ContinuousEffect = "所有未印刷魔法领域图标的本方角色获得【心】图标与［黑刀］(+1战斗)",
                AuraRequiresNoDomain = true,
                AuraFilterExcludeDomain = MagicDomain.Mind,
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "心", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                    new EffectGrant { Kind = "MagicDomain", Value = "Mind", Amount = 1 },
                }
            };
            data.EffectDataList.Add(aura);
        }
    }
}
