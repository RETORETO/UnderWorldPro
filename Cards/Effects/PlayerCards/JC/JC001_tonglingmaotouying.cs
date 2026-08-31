using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC001 通灵猫头鹰 效果（自原 JC001_tonglingmaotouying 构造函数迁移，逻辑不变）。
    /// 不再继承 Resource：静态注册类，由 CardEffectFactory 按 UID 调用。
    /// 两个效果：
    ///  1) 静止/禁止：不能被装备结附（JC001-A）；
    ///  2) 条件持续：若资产区心灵领域忠诚≥2，获得［白眼］=先手调查能力+1（JC001-B）。
    /// </summary>
    public static class JC001_tonglingmaotouying_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC001_tonglingmaotouying", Build);

        private static void Build(PlayerCardData data)
        {
            // —— JC001-A 静止/禁止：不能被装备结附（争夺区常驻）——
            var cannotAttach = KeywordEffects.CannotBeAttached();
            cannotAttach.EffectId = "JC001_A_CannotBeAttached";
            data.EffectDataList.Add(cannotAttach);

            // —— JC001-B 条件持续：心心→白眼（先手调查+1）——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC001_B_WhiteEye",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "心灵领域忠诚≥2",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.DomainLoyalty,
                    Domain = MagicDomain.Mind,
                    Amount = 2,
                    Zone = CardZone.AssetZone
                },
                ContinuousEffect = "获得［白眼］（先手调查能力+1）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "先手调查", Amount = 1 }
                }
            });
        }
    }
}
