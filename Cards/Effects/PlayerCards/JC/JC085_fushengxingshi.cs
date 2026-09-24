using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC085 复生行尸 效果（角色）。
    /// 卡文：持续：若你的资产区具有【死】，则复生行尸获得［白星］。
    ///      墓地行动：你可以从墓地中正面打出复生行尸，就如同其在你的手上一样。（你仍需要支付其费用，且需满足其忠诚需求。）
    /// 实现：
    ///  · **条件持续**：`PrereqKind.DomainLoyalty` + 死领域 ≥1 + 授予 `先手势力`（白星），
    ///    与 JC001（心心→白眼）/ JC030（血血→黑眼）同款范式；
    ///  · **墓地行动**：`data.PlayableFromGraveyard = true`（K27）——`TurnManager.PlayCard` 的**来源门禁**
    ///    额外放行"从墓地正面打出"，`LegalityProjection` 也把墓地中该卡列为可打出源；
    ///    费用/忠诚/行动步骤等门禁照常（卡文"就如同其在你的手上一样"）。
    /// </summary>
    public static class JC085_fushengxingshi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC085_fushengxingshi", Build);

        private static void Build(PlayerCardData data)
        {
            // 【K27】"墓地行动：可以从墓地中正面打出"（来源门禁 + 打出候选）
            data.PlayableFromGraveyard = true;
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC085_1_WhiteStar",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "死领域忠诚≥1",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.DomainLoyalty,
                    Domain = MagicDomain.Death,
                    Amount = 1,
                    Zone = CardZone.AssetZone,
                },
                ContinuousEffect = "获得［白星］（先手势力+1）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "先手势力", Amount = 1 },
                },
            });
        }
    }
}
