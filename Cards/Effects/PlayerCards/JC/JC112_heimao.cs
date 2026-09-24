using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC112 黑猫 效果（角色）。
    /// 卡文：公开 不能被装备结附 快速行动，牺牲黑猫：本回合中，你下一张正面打出，且具有魔法领域图标的牌，
    ///      其所需费用减少1。
    /// 实现：公开 = `KeywordEffects.Public()`；"不能被装备结附" = `KeywordEffects.CannotBeAttached()`（JC001-A 同款消费点）；
    ///      减费用 P1 的 `有域图标` 过滤串（对应卡文"具有魔法领域图标的牌"）+ 一次性 + 牺牲本角色代价。
    /// </summary>
    public static class JC112_heimao_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC112_heimao", Build);

        private static void Build(PlayerCardData data)
        {
            var publicEffect = KeywordEffects.Public();
            publicEffect.EffectId = "JC112_A_Public";
            data.EffectDataList.Add(publicEffect);

            var cannotAttach = KeywordEffects.CannotBeAttached();
            cannotAttach.EffectId = "JC112_B_CannotBeAttached";
            data.EffectDataList.Add(cannotAttach);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC112_1_CostReduceDomainCard",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "牺牲本角色",       // 引擎侧代价：入堆叠前牺牲自身
                Cost = 0,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "费用减少", "1", "有域图标", "一次性" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "牺牲黑猫；下一张『具有魔法领域图标』的牌费用 -1（一次性）。",
            });
        }
    }
}
