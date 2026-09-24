using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC053 鸣钟教派徽记 效果（附属 / 徽章，结附于本方秘社）。
    /// 卡文：结附于本方秘社
    ///      快速行动，横置：本回合中你下一张正面打出或翻面的【鸣钟教派】牌所需费用减少1。
    ///      快速行动1，横置：本回合中，若有敌方势力标志曾被移除，你可以抓一张牌。
    ///
    /// 两条激活均以"横置本徽记"为代价（`ActivateCondition = "横置本角色"`，附属的"本角色"即本徽记）。
    ///  · ① 减费（P2 已交付）：`Gain 费用减少` + 过滤串 `组织:鸣钟教派` + `一次性`；
    ///  · ② 【本轮交付】以"**本回合内敌方势力标志曾被移除**"为**发动前提**：
    ///      - 记忆位：`AreaManager.MarkPowerMarkRemovedThisTurn`（在 `RemovePowerMark` **真实移除&gt;0 个**时置位；
    ///        回合开始的重置步骤清空，故粒度＝"本回合"）；
    ///      - 谓词：`EffectCondition.Predicate = "EnemyPowerMarkRemovedThisTurn"`（敌我基准＝本徽记控制者）；
    ///      - 门禁：`EffectEngine.ActivateAbility` 在**付费/横置之前**判定该条件（不满足即拒绝发动、代价不付出），
    ///        `ActivateEligibility` 同步用于 UI 高亮/候选（置灰）；
    ///      - "你可以" ＝ 非强制（`Forced = false`），发动与否由玩家决定（不发动则什么都不发生）。
    /// </summary>
    public static class JC053_mingzhongjiaopaihuiji_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC053_mingzhongjiaopaihuiji", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：仅本方秘社
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC053_1_CostReduce",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",   // 附属的"横置本角色"= 横置本徽记
                Cost = 0,
                Forced = false,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "费用减少", "1", "组织:鸣钟教派", "一次性" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "横置本徽记；下一张【鸣钟教派】牌费用 -1（一次性）。",
            });

            // ② 快速行动1，横置：本回合中，若有敌方势力标志曾被移除，你可以抓一张牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC053_2_DrawIfEnemyMarkRemoved",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 1,                                  // 卡文"快速行动**1**"
                Forced = false,
                // 结算条件＝**发动前提**：本回合中敌方势力标志曾被移除（不满足 → 拒绝发动，见 ActivateAbility 2.2 步）
                Condition = new EffectCondition { Predicate = "EnemyPowerMarkRemovedThisTurn" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "本回合中若有敌方势力标志曾被移除，横置本徽记并支付1 → 抓一张牌（条件不满足时不可发动）。",
            });
        }
    }
}
