using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC082 索亚之书 效果（附属 / 书籍）。
    /// 卡文：选择一项： ·借阅：结附于目标角色 持续：受此结附的角色获得［白眼］。
    ///                 ·收藏：结附于本方秘社 快速行动2，横置：本回合中，当你赢得下一次调查对抗时，你可以抓一张牌。
    ///
    /// 实现口径（**"选择的一项"由结附目标种类体现**——两次选项分别就是"结附于角色"/"结附于秘社"，
    /// 打出时 `TargetRules` 的 OR 让玩家在"角色"与"本方秘社"之间选一个目标，选中的那类即所选的一项）：
    ///  · **打出目标** ＝ 两条 `TargetRule` 的 OR：`Character` 或 `Council(Friendly)`；
    ///  · **借阅（结附于角色）** ＝ 持续授予**宿主**［白眼］，前提 `PrereqKind.HostKind = "角色"`
    ///    （宿主是秘社时该条不生效——`GrantToHost` + `HostKind` 组合）；
    ///  · **收藏（结附于本方秘社）** ＝ 一条激活能力（快速行动2、横置为代价）：其效果声明
    ///    `EffectData.Pending`（**本回合**内首次出现"赢得调查对抗"时兑现一次 → 你可以抓一张牌），
    ///    并加 `HostKind = "秘社"` 前提——**只有结附在秘社上时才能发动**（结附在角色上时该能力不可用）。
    /// </summary>
    public static class JC082_suoyazhishu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC082_suoyazhishu", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：目标角色 **或** 本方秘社（玩家所选的一项即由此目标确定）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            // 借阅：受此结附的角色获得［白眼］（仅当宿主是**角色**时生效）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC082_A_LendGrantWhiteEye",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "结附于角色",
                PrereqSpec = new ContinuousPrereqSpec { Kind = PrereqKind.HostKind, Param = "角色" },
                GrantToHost = true,          // 授予**结附的宿主**
                ContinuousEffect = "受此结附的角色获得［白眼］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "白眼", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "先手调查", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "借阅：结附于角色 → 该角色获得［白眼］（宿主非角色时不授予）。",
            });

            // 收藏：快速行动2，横置：本回合中，当你赢得下一次调查对抗时，你可以抓一张牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC082_B_KeepPledgeDrawOnInvestigationWon",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",     // 附属的"横置本角色"＝横置本书
                Cost = 2,                            // 卡文"快速行动**2**"
                Forced = false,
                // 只有结附在**本方秘社**上时才可发动（结附于角色时为"借阅"形态，无此能力）
                Condition = new EffectCondition { Predicate = "HostIsCouncil" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                // 本条效果**不立即做事**：登记一份"本回合下一次赢得调查对抗时兑现"的承诺
                Pending = new PendingEffectSpec
                {
                    Timing = EffectTiming.OnInvestigationWon,
                    Expire = "本回合结束",
                    Optional = true,                 // 卡文"你可以"
                    Actions = new Array<EffectAction>
                    {
                        new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                    },
                },
                Duration = "瞬时",
                FaqNote = "收藏：结附于本方秘社；横置本书并支付2 → 本回合中当你赢得下一次调查对抗时，你可以抓一张牌。",
            });
        }
    }
}
