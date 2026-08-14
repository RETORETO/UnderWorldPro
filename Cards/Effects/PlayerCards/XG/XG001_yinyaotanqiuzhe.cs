using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.XG
{
    /// <summary>
    /// XG001 银钥探求者 效果（自原 XG001_yinyaotanqiuzhe 构造函数迁移，逻辑不变）。
    /// 反应触发（XG001-1）：当该角色在场上时，玩家打出一张具有"法术"子类型的事务卡，控制者抓一张牌。
    /// </summary>
    public static class XG001_yinyaotanqiuzhe_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("XG001_yinyaotanqiuzhe", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "XG001_1_DrawOnSorcery",
                Timing = EffectTiming.OnCardPlayed,
                Forced = false,
                Condition = new EffectCondition { Predicate = "PlayedSorcery" },
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }
                },
                Duration = "瞬时",
                FaqNote = "打出一张具有'法术'子类型的事务时触发；玩家可选"
            });
        }
    }
}
