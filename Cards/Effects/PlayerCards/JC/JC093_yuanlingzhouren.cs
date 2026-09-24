using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC093 怨灵咒刃 效果（附属 / 装备·武器 / 方碑序列）。
    /// 卡文：结附于目标角色 快速行动，牺牲一个角色：受此结附的角色获得［黑刀］［黑星］直到回合结束。
    ///      该能力每回合至多只能发动两次。
    ///
    /// 实现（**K22 牺牲代价** + K13 授予结附宿主 + K23 次数上限）：
    ///  · 打出目标＝**角色**（"结附于目标角色"）；来源是**附属卡**（装备·武器）——故"牺牲一个角色"
    ///    **天然不含来源自身**（它不是角色），无需 `ExcludeSource`；
    ///  · 代价＝`SacrificeCostSpec`（不限地区、不可牺牲暗藏者；由**发动者自己选择**被牺牲者，口径同 JC094）；
    ///  · 主效果：两条 `Gain` 主体写 **`"结附宿主"`**（＝`source.AttachedTo`，本卡结附的那个角色）
    ///    + `Duration="直到回合结束"` → 时效项，回合结束自动撤销（`ExpireTurnEndEffects`）；
    ///  · `ActivateWindow="快速"` + `MaxUsesPerTurn = 2`（"每回合至多只能发动两次"，K23）。
    /// </summary>
    public static class JC093_yuanlingzhouren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC093_yuanlingzhouren", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝角色（"结附于目标角色"）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC093_1_SacrificeGrantHostBladeStar",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",                     // 卡文"快速行动"（无费用数字 → Cost 0）
                MaxUsesPerTurn = 2,                          // "每回合至多只能发动两次"（K23）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                SacrificeCost = new SacrificeCostSpec
                {
                    AllowCovert = false,                     // 口径：卡文未写暗藏者 → 不可牺牲暗藏者
                    ExcludeSource = false,                   // 来源是附属卡、本就不是角色，无需排除
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑刀", "1", "结附宿主" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑星", "1", "结附宿主" } },
                },
                Duration = "直到回合结束",                    // 宿主获得的图标只到回合结束
                FaqNote = "快速行动：牺牲一个角色（发动者自选、不可暗藏者）→ 受此结附的角色获得［黑刀］［黑星］直到回合结束。"
                        + "每回合至多发动两次；场上没有可牺牲的角色时不可发动。",
            });
        }
    }
}
