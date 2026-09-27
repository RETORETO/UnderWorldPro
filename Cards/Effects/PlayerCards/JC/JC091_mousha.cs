using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC091 谋杀 效果（事务卡 / 快速行动）。
    /// 卡文：只能在行动阶段打出。快速行动：消灭目标人类角色。
    /// 实现：`LimitToActionPhaseOnly=true`（B 档门禁）+ 事务卡打出即结算 + **实例当前类别**"人类"目标 + `Destroy`。
    /// 注（2026-09 用户口径）：卡文写的是"**人类角色**"而非"**印刷类别**为人类"→ 判定读**实例当前类别**
    /// （`CurrentSubCategory`）：被 JC036 初拥改写为吸血鬼的角色**不再是人类**，故本卡**不能**指定它；
    /// 反之 JC036 自身的结附限制写的是"印刷类别为人类"，读模板数据，不受改写影响。
    /// </summary>
    public static class JC091_mousha_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC091_mousha", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;   // 只能在行动阶段打出（B 档）

            data.TargetRules = new Array<TargetRule>
            {
                new TargetRule
                {
                    Kind = TargetKind.Character,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.CurrentSubCategory },
                    Param = "人类",
                },
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC091_1_DestroyHuman",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,     // 非快速（受 LimitToActionPhaseOnly 约束）
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.CurrentSubCategory },
                    Param = "人类",
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "行动阶段打出；消灭目标**当前类别**含『人类』的角色（不分敌我）。"
                        + "注：写『印刷类别为人类』的目标规则才读模型数据，本卡读实例当前类别（用户 2026-09 口径）。",
            });
        }
    }
}
