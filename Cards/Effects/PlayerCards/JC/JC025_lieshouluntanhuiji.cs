using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC025 猎手论坛徽记 效果（附属 / 徽章，结附于本方秘社）。
    /// 卡文：结附于本方秘社
    ///      快速行动，横置：本回合中你下一张正面打出或翻面的【猎魔人】牌的所需费用减少1。
    ///      快速行动1，横置，牺牲猎手论坛徽记：从你的牌库中寻找一张猎手牌，展示该牌后置于你的手中，然后洗牌。
    /// 实现：与 JC012/JC039 同款双激活骨架；
    ///      ① 减费 filter `组织:猎魔人`（【猎魔人】= 卡牌 Organization）；
    ///      ② P2 `ActionVerb.Search`，筛选串 `子类别:猎手`（"猎手"是印刷子类别），`附加="展示"`。
    /// </summary>
    public static class JC025_lieshouluntanhuiji_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC025_lieshouluntanhuiji", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：仅本方秘社
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            // 激活①：快速行动，横置 → 下一张【猎魔人】牌费用 -1（一次性）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC025_1_CostReduce",
                Timing = EffectTiming.AttachmentActivated,
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
                        Args = new Array<string> { "费用减少", "1", "组织:猎魔人", "一次性" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "横置本徽记；下一张【猎魔人】牌费用 -1（一次性）。",
            });

            // 激活②：快速行动1，横置并牺牲本徽记 → 检索一张猎手牌入手（展示后洗牌）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC025_2_SearchHunter",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色,牺牲本角色",
                Cost = 1,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        Args = new Array<string> { "子类别:猎手", "手牌", "展示", "牌库" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "快速行动1、横置并牺牲本徽记；检索一张猎手牌，展示后入手并洗牌。",
            });
        }
    }
}
