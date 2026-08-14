using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC018 魔刃传人 效果（自原 JC018_morenchuanren 构造函数迁移，逻辑不变）。
    ///  A) 条件持续（线性计量）：资产区每有 1 点心灵忠诚 → 1 个黑刀（战斗+N）（JC018-A）。
    ///  B) 威名（触发）：战斗获胜 → 在本地区放置一个势力标记（JC018-B）。
    /// </summary>
    public static class JC018_morenchuanren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC018_morenchuanren", Build);

        private static void Build(PlayerCardData data)
        {
            // —— A) 条件持续（线性计量）：每有 1 点心灵忠诚 → 1 个黑刀 ——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC018_A_XinLingHeiDao",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.ContestZone,
                ContinuousPrereq = "资产区心灵领域忠诚每点+1黑刀",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.DomainLoyalty,
                    Domain = MagicDomain.Mind,
                    Amount = 1,
                    Zone = CardZone.AssetZone,
                    ScaleByLoyalty = true
                },
                ContinuousEffect = "你的资产区内每有一个【心】，魔刃传人就获得一个［黑刀］(+N战斗)",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 }
                }
            });

            // —— B) 威名（触发，词条化）：战斗获胜 → 本地区放置一个势力标记 ——
            var weiMing = KeywordEffects.WeiMing();
            weiMing.EffectId = "JC018_B_WeiMing";
            data.EffectDataList.Add(weiMing);
        }
    }
}
