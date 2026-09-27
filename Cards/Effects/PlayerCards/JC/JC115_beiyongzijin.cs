using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC115 备用资金 效果（附属卡，`SubCategories = ["资源"]`）。
    /// 卡文：快速行动，横置：你获得1。
    ///
    /// 实现（**P3·K19 临时费用池**，口径见 §7.4b A26）：
    ///  · 激活能力：`Timing = AttachmentActivated` + `ActivateWindow = "快速"`（卡文"快速行动"）
    ///    + `ActivateCondition = "横置本角色"`（本作惯例写法＝横置本卡自身作代价，见 JC012/JC003）；
    ///  · 动作 `GrantTemporaryMana{1}`：给控制者 **+1 点临时费用**——
    ///    **所有费用都是"无色费用"**，临时费用**支付时优先花费**，且**任意步骤结束时清除**
    ///    （六个步骤：先手/后手/预对抗/调查对抗/战斗对抗/势力对抗；清空点＝`TurnManager.EnterPhase` 每次阶段流转）。
    /// </summary>
    public static class JC115_beiyongzijin_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC115_beiyongzijin", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC115_A_TapForTemporaryMana",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",                 // 卡文"快速行动"
                ActivateCondition = "横置本角色",         // 横置本卡自身作代价（既有惯例写法）
                Cost = 0,
                Forced = false,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.GrantTemporaryMana, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动，横置本卡：获得 1 点临时费用（无色费用）；临时费用在支付时优先花费，"
                        + "并在任意步骤结束时清除。",
            });
        }
    }
}
