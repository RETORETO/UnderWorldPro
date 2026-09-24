using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC131 空投物资 效果（事务卡）。
    /// 卡文：行动：从你的牌库中寻找一张牌，将该牌置于你的手中，然后洗牌。
    /// 实现：`LimitToActionPhaseOnly=true`（行动阶段）+ P2 `ActionVerb.Search`，
    ///      筛选串为**空**（= 任意牌，见 `DeckSearchFilter`），去向 `手牌`，无"展示"（卡文未要求）；
    ///      洗牌口径由引擎统一处理（取到/没找到 → 洗牌；放弃 → 不洗牌）。
    /// </summary>
    public static class JC131_kongtouwuzi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC131_kongtouwuzi", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC131_1_SearchAny",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        Args = new Array<string> { "", "手牌", "", "牌库" },   // 空筛选 = 任意牌
                    },
                },
                Duration = "瞬时",
                FaqNote = "从牌库找任意一张牌入手并洗牌；找不到也洗牌，放弃则不洗。",
            });
        }
    }
}
