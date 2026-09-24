using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC008 灵能激发 效果（事务卡 / 法术·心灵）。
    /// 卡文：快速行动：本回合中，目标角色获得+1防御力和［黑刀］。
    /// FAQ：◆灵能激发的快速行动能力**不能在分配战斗伤害阶段使用**。
    ///
    /// 实现：
    ///  · 打出窗口＝C 档（含 `PlayTiming.Response` 的快速行动：有优先权即可打出，见 `TurnManager.PlayCard`）；
    ///  · **K26 阶段排除**：效果声明 `ExcludedPhases = "分配战斗伤害阶段"` →
    ///    ① `TurnManager.PlayCard` 在该阶段拒绝打出本卡；② `ActivateEligibility`/`EffectEngine.ActivateAbility`
    ///    对激活类效果共用同一门禁（本卡是事务卡，主要走 ①）。阶段名映射见 `PhaseGateRule`；
    ///  · 效果本体：`Gain{"防御","1"}` + `Gain{"黑刀","1"}`，`Duration="直到回合结束"`（时效修正栈回合结束自动还原）。
    /// </summary>
    public static class JC008_lingnengjifa_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC008_lingnengjifa", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：任意一个角色（卡文"目标角色"，未限地区/阵营）
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC008_1_GainDefenseAndBlackBlade",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,   // 快速行动：有优先权即可打出（C 档）
                // 【K26】FAQ：不能在分配战斗伤害阶段使用
                ExcludedPhases = "分配战斗伤害阶段",
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = true,   // 卡文未排除自身；"目标角色"含自己
                },
                Actions = new Array<EffectAction>
                {
                    // 同一序列内两个动作（不做半条效果）：+1 防御 与 ［黑刀］都落地
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑刀", "1" } },
                },
                Duration = "直到回合结束",
                FaqNote = "本回合中目标角色 +1 防御力并获得［黑刀］；不能在分配战斗伤害阶段使用（K26 阶段排除）。",
            });
        }
    }
}
