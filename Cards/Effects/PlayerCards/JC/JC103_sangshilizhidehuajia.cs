using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC103 丧失理智的画家 效果（角色）。
    /// 卡文：快速行动，横置：本回合中你下一张正面打出或翻面的【梦境行者】牌，所需费用减少1。
    /// 实现：与 JC012 帷幕守望徽记激活① 同款——`Gain{"费用减少","1",filter,"一次性"}`
    ///      + `Duration="直到回合结束"`；filter 用 P1 的 `组织:` 前缀（"梦境行者"是 Organization）。
    /// </summary>
    public static class JC103_sangshilizhidehuajia_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC103_sangshilizhidehuajia", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC103_1_CostReduce",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "费用减少", "1", "组织:梦境行者", "一次性" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "横置自身；下一张【梦境行者】牌费用 -1（一次性，回合结束未用则流失）。",
            });
        }
    }
}
