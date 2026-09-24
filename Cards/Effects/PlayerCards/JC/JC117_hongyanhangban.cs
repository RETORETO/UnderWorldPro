using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC117 红眼航班 效果（附属 / 结附于本方秘社）。
    /// 卡文：结附于本方秘社 快速行动1，横置：将目标本方角色移动到目标地区。该能力只能在行动阶段发动。
    /// FAQ：◆若对方的卡牌或能力对指定目标角色有位置要求，你可以**响应**发动红眼航班把该角色移到其他地区使其目标不合法；
    ///      ◆移动时**必须进入另一个地区**，不能选择他正位于的那个地区（**不能原地移动**）。
    ///
    /// 实现（**K15 扩展：激活能力的"目标地区"**）：
    ///  · 打出目标＝本方秘社（`TargetRules = Council + Friendly`）；
    ///  · 激活能力：`ActivateWindow = "行动"`（卡文"只能在行动阶段发动"→ 复用既有行动窗口门禁：
    ///    自己行动步骤 + 堆叠为空 + 有行动权）、`Cost = 1`（"快速行动1"）、`ActivateCondition = "横置本角色"`
    ///    （横置此附属＝它的费用/代价）；
    ///  · **双目标表达**：`Target` ＝ 目标**本方角色**；`NeedsAreaTarget = true` ＝ 另需选定一个**目标地区**
    ///    （入堆叠时锁定，随 `StackEntry.TargetAreaId` 下发）；`AreaTargetExcludesCardArea = true` ＝
    ///    **不能原地移动**（FAQ：目标地区不能是该角色当前所在地区）；
  ///  · 动作＝`Move{"目标"}`：落点取本次结算的 `targetAreaId`（既有机动语义）。
    /// </summary>
    public static class JC117_hongyanhangban_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC117_hongyanhangban", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝本方秘社（"结附于本方秘社"）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC117_1_MoveFriendlyToTargetArea",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "行动",                       // 卡文"只能在行动阶段发动"
                Cost = 1,                                      // "快速行动1"
                ActivateCondition = "横置本角色",               // 横置此附属
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },   // "目标本方角色"
                },
                NeedsAreaTarget = true,                        // 另需选定"目标地区"
                AreaTargetExcludesCardArea = true,              // FAQ：不能原地移动
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Move, Args = new Array<string> { "目标" } },   // 落点＝targetAreaId
                },
                Duration = "瞬时",
                FaqNote = "行动阶段发动：横置本附属并支付 1 费 → 把本方一个角色移到**另一个**地区（不能选择他当前所在地区）。",
            });
        }
    }
}
