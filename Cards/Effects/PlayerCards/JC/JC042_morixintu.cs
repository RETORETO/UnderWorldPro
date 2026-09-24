using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC042 末日信徒 效果（角色）。
    /// 卡文：快速行动，牺牲末日信徒：本回合中你正面打出的下一张【鸣钟教派】牌或具有【血】图标的牌，
    ///      其所需费用减少2。
    /// 实现：`Gain{"费用减少","2","组织:鸣钟教派|域:血","一次性"}` —— filter 用 P1 的
    ///      "组织:/域:" 前缀 + `|` **或**语义（避免"既是鸣钟教派又带血图标"的牌被重复减 2 次）。
    /// </summary>
    public static class JC042_morixintu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC042_morixintu", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC042_1_CostReduce2",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "牺牲本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "费用减少", "2", "组织:鸣钟教派|域:血", "一次性" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "牺牲自身；下一张【鸣钟教派】牌或具【血】图标的牌费用 -2（两种条件取其一，不叠加）。",
            });
        }
    }
}
