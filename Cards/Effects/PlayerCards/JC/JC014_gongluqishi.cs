using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC014 公路骑士 效果（自原 JC014_gongluqishi 构造函数迁移，逻辑不变）。
    ///  1) 持续「公开」：手牌区常驻关键字"公开"，不可派遣为暗藏者（JC014-1）。
    ///  2) 机动触发：对抗阶段开始时选择另一地区移动过去（JC014-3）。
    /// </summary>
    public static class JC014_gongluqishi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC014_gongluqishi", Build);

        private static void Build(PlayerCardData data)
        {
            // —— JC014-1 持续：公开（手牌区常驻）——
            var publicEffect = KeywordEffects.Public();
            publicEffect.EffectId = "JC014_1_Public";
            data.EffectDataList.Add(publicEffect);

            // —— JC014-3 机动触发：移动至另一地区（对抗前优先权窗口 PreCombat）——
            var maneuverEffect = KeywordEffects.Maneuver();
            maneuverEffect.EffectId = "JC014_3_Maneuver";
            data.EffectDataList.Add(maneuverEffect);
        }
    }
}
