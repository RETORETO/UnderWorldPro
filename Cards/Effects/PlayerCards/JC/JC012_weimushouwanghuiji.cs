using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC012 帷幕守望徽记 效果（自原 JC012_weimushouwanghuiji 构造函数迁移，逻辑不变）。
    /// 附属卡（徽章），结附于本方秘社；两个激活效果均为快速行动、横置本徽记为代价：
    ///   激活①：本回合下一张正面打出/翻面的【帷幕守望】牌费用-1（一次性）；
    ///   激活②：牺牲本徽记，横置一个目标角色或暗藏者（无视地区）。
    /// </summary>
    public static class JC012_weimushouwanghuiji_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC012_weimushouwanghuiji", Build);

        private static void Build(PlayerCardData data)
        {
            // ---- 打出目标：仅本方秘社 ----
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            // —— 激活①：快速行动，横置本徽记 → 本回合下一张正面打出/翻面的【帷幕守望】牌费用-1（一次性）——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC012_1_CostReduce",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 0,
                Forced = false,
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "费用减少", "1", "帷幕守望", "一次性" }
                    }
                },
                Duration = "直到回合结束",
                FaqNote = "下一张帷幕守望牌费用-1；打出或翻面后失效，整回合未用则流失"
            });

            // —— 激活②：快速行动，横置本徽记并牺牲本徽记 → 横置一个目标角色（无视地区）——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC012_2_Tap",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色,牺牲本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    Filter = "",
                    NeedsSelection = true,
                    IncludeCovert = false
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                    new EffectAction { Verb = ActionVerb.Sacrifice }
                },
                Duration = "瞬时",
                FaqNote = "无视地区；牺牲自身徽记"
            });
        }
    }
}
