using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC039 王座会徽记 效果（附属，结附于本方秘社）。
    /// 卡文：结附于本方秘社
    ///      快速行动，横置：本回合中你下一张正面打出或翻面的【王座会】牌的所需费用减少1。
    ///      快速行动3，横置：令目标本方【王座会】角色潜伏。
    /// 实现：与 JC012 同款双激活骨架；减费 filter 用 `组织:王座会`；
    ///      "潜伏"用 P1 新增 `ActionVerb.Latent`；目标精筛 = 角色 + 本方 + P1 新增 `Organization`。
    /// </summary>
    public static class JC039_wangzuohuihuiji_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC039_wangzuohuihuiji", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：仅本方秘社
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            // 激活①：快速行动，横置 → 下一张【王座会】牌费用 -1（一次性）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC039_1_CostReduce",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",     // 附属的"横置本角色"即横置本附属（引擎按 ActivateCondition 关键字解析）
                Cost = 0,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "费用减少", "1", "组织:王座会", "一次性" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "横置本徽记；下一张【王座会】牌费用 -1（一次性）。",
            });

            // 激活②：快速行动3，横置 → 令目标本方【王座会】角色潜伏
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC039_2_LatentAlly",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.Friendly,
                        TargetRestriction.Organization,   // P1 新增：按 Organization 筛选
                    },
                    Param = "王座会",
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Latent },
                },
                Duration = "瞬时",
                FaqNote = "快速行动3、横置本徽记；令目标本方【王座会】角色潜伏。",
            });
        }
    }
}
