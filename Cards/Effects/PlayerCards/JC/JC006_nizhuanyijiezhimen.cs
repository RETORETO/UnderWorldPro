using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC006 逆转异界之门 效果（事务卡 / 快速行动）。
    /// 卡文：快速行动：将目标具有魔法领域图标的角色移回其拥有者手上。
    /// 实现：事务卡打出即结算（OnSorceryCast）+ 快速打出（PlayTiming.Response）；
    ///      目标精筛用 `TargetRestriction.HasMagicDomainIcon`（P1 新增：实例领域优先、回退模板印刷领域）。
    /// </summary>
    public static class JC006_nizhuanyijiezhimen_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC006_nizhuanyijiezhimen", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：具有魔法领域图标的角色（P0~P2 的取目标链路：投影优先 + 同一规则函数兜底）
            data.TargetRules = new Array<TargetRule>
            {
                new TargetRule
                {
                    Kind = TargetKind.Character,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.HasMagicDomainIcon },
                },
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC006_1_BounceDomainCharacter",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.HasMagicDomainIcon },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Bounce },
                },
                Duration = "瞬时",
                FaqNote = "快速行动：将目标具有魔法领域图标的角色移回其拥有者手上。",
            });
        }
    }
}
