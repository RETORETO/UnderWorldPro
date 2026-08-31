using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC030 巨型蝙蝠 效果（自原 JC030_juxingbianfu 构造函数迁移，逻辑不变）。
    ///  1) 袭击1（JC030-A）：现身触发，对同一地区角色造成 1 点伤害；
    ///  2) 静止/禁止：不能被装备结附（JC030-B）；
    ///  3) 条件持续：若资产区血领域忠诚≥2，获得［黑眼］=先手调查+1，并追加吸血鬼类别（JC030-C）。
    /// </summary>
    public static class JC030_juxingbianfu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC030_juxingbianfu", Build);

        private static void Build(PlayerCardData data)
        {
            // —— JC030-A 袭击1（关键词化）：现身进场成功后，对同一地区角色造成 1 点伤害 ——
            var strike = KeywordEffects.Strike(1);
            strike.EffectId = "JC030_A_Strike";
            data.EffectDataList.Add(strike);

            // —— JC030-B 静止/禁止：不能被装备结附（争夺区常驻）——
            var cannotAttach = KeywordEffects.CannotBeAttached();
            cannotAttach.EffectId = "JC030_B_CannotBeAttached";
            data.EffectDataList.Add(cannotAttach);

            // —— JC030-C 条件持续：血血→黑眼 + 吸血鬼类别 ——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC030_C_BloodEyes",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "血领域忠诚≥2",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.DomainLoyalty,
                    Domain = MagicDomain.Blood,
                    Amount = 2,
                    Zone = CardZone.AssetZone
                },
                ContinuousEffect = "获得［黑眼］（先手调查+1），并额外具有吸血鬼类别",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "调查", Amount = 1 },
                    new EffectGrant { Kind = "SubCategory", Value = "吸血鬼", Amount = 1 }
                }
            });
        }
    }
}
