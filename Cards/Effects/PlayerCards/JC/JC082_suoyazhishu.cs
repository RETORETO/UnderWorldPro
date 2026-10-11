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
    /// 实现口径：
    ///  · **"选择的一项"由结附目标种类体现**：两次选项分别就是"结附于角色"/"结附于本方秘社"，
    ///    打出时走"选择一项 → 再选该项目的目标"（`PlayerCardData.PlayChoice = true`；与 JC063 同款流程，
    ///    见 `MultiTargetSlots.PlayChoiceEffect` / `TurnManager.BeginModeCardPlay`）；
    ///  · **借阅（结附于角色）** ＝ 持续授予**宿主**［白眼］，前提 `PrereqKind.HostKind = "角色"`；
    ///  · **收藏（结附于本方秘社）** ＝ 一条激活能力（快速行动2、横置为代价）＋ 一条 `OnInvestigationWon` 触发：
    ///    激活只做代价（横置＋支付2，发动即由引擎登记"本回合已发动"）；抓牌由触发承担，
    ///    并在**入堆叠前**用 `ConfirmPrompt` 弹"是/否"选择窗（阻塞；选否 ⇒ 不入堆叠、不抓牌）。
    ///    【用户口径·2026-10】**不使用 `Pending` 一次性承诺**（那会在时机到达时再走一次触发/排序窗）。
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
            // 【用户口径·打出方式参考 JC063】"选择一项"必须在**打出时**选：先选一项（借阅／收藏），
            // 再选该项的结附目标 —— 与常规附属"拖到目标卡上"的流程不同。
            // 选项与本卡 `TargetRules` 一一对应（角色 → 结附于目标角色；本方秘社 → 结附于本方秘社）。
            data.PlayChoice = true;

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

            // 收藏（一）：快速行动2，横置 —— 只做代价（发动即登记"本回合已发动收藏能力"）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC082_B_KeepPledgeActivate",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",     // 附属的"横置本角色"＝横置本书
                Cost = 2,                            // 卡文"快速行动**2**"
                Forced = false,
                // 只有结附在**本方秘社**上时才可发动（结附于角色时为"借阅"形态，无此能力）
                Condition = new EffectCondition { Predicate = "HostIsCouncil" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                MaxUsesPerTurn = 1,                  // 每回合发动一次（横置本就是一次，双保险）
                Actions = new Array<EffectAction>(), // 本条只做代价（横置＋付费），效果由抓牌触发承担
                Duration = "瞬时",
                FaqNote = "收藏：结附于本方秘社；横置本书并支付2 ⇒ 本回合中你赢得下一次调查对抗时，"
                        + "会被询问是否抓一张牌（选否则不入堆叠、不抓牌）。",
            });

            // 收藏（二）：赢得调查对抗时**即时询问**是否抓一张牌（入堆叠前弹窗；选否不入堆叠）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC082_C_DrawOnInvestigationWon",
                Timing = EffectTiming.OnInvestigationWon,
                // 门禁：**本方赢得本次调查对抗** ∧ **本回合已发动过收藏能力**（复合谓词：收集期一次判定，
                // 否则"对手获胜"也会弹窗询问——实测）
                Condition = new EffectCondition
                {
                    Predicate = "WinnerIsSelfAndAbilityUsed",
                    Param = "JC082_B_KeepPledgeActivate",
                },
                // 【用户口径】入堆叠前弹出"是/否"选择窗（阻塞）；选否 ⇒ 不入堆叠、不结算
                ConfirmPrompt = "索亚之书（收藏）：是否抓一张牌？",
                MaxUsesPerTurn = 1,                  // "下一次"＝本回合只兑现一次
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Forced = false,                      // 卡文"你可以"
                Duration = "瞬时",
                FaqNote = "赢得调查对抗时询问是否抓一张牌（本回合内由收藏能力的发动解锁，只问一次）。",
            });
        }
    }
}
