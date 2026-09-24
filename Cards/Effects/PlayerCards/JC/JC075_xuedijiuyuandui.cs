using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC075 雪地救援队 效果（角色）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者） 快速行动2，横置：将另一个目标本方角色移回其拥有者手上。
    /// 实现：公开走 `KeywordEffects.Public()`（手牌期即生效，`HasPublicKeyword` 双层判定）；
    ///      激活效果 = 快速行动 + 费用 2 + 横置代价，目标为本方非自身角色，动作 `Bounce`。
    /// </summary>
    public static class JC075_xuedijiuyuandui_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC075_xuedijiuyuandui", Build);

        private static void Build(PlayerCardData data)
        {
            var publicEffect = KeywordEffects.Public();
            publicEffect.EffectId = "JC075_1_Public";
            data.EffectDataList.Add(publicEffect);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC075_2_ReturnAlly",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Bounce },
                },
                Duration = "瞬时",
                FaqNote = "快速行动2、横置自身；可把任意地区的本方角色移回其拥有者手上（无视地区）。",
            });
        }
    }
}
