using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC029 新生血族 效果（角色）。
    /// 卡文：袭击1（当该角色现身进场时，你可以令其对目标位于相同地区的角色造成1点伤害。）
    /// 实现：复用关键词工厂 `KeywordEffects.Strike(1)`（Timing=UncoverTrigger + 本地区目标 + DealDamage），
    ///      与 JC030 巨型蝙蝠「袭击1」同款，零新机制。
    /// </summary>
    public static class JC029_xinshengxuezu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC029_xinshengxuezu", Build);

        private static void Build(PlayerCardData data)
        {
            var strike = KeywordEffects.Strike(1);
            strike.EffectId = "JC029_1_Strike1";
            data.EffectDataList.Add(strike);
        }
    }
}
