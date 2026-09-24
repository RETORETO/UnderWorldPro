using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC129 操控心智 效果（事务卡，法术/心灵）。
    /// 卡文：行动：你获得目标敌方角色的控制权直到回合结束。
    ///
    /// 实现（P3：控制权转移原语 + 到期归还调度）：
    ///  · 目标 ＝ `Character` + `Enemy`（目标**敌方**角色）；
    ///  · 动作 ＝ 新动词 `GainControl{自身/目标, 直到回合结束}`：
    ///    只改**控制者**（`ControllerId`，`OwnerId` 不变，与引擎内"夺控"口径一致），
    ///    经 `ZoneManager.ChangeController` 统一出口（版本号 / 光环重算 / 前提取舍 / UI 刷新一并发生）；
    ///  · **直到回合结束** ＝ 登记时效项（`TimedMod{Kind="Control", PlayerId=原控制者}`），
    ///    回合结束由 `ExpireTurnEndEffects` **归还**给原控制者（卡已离场则安全跳过）。
    /// </summary>
    public static class JC129_caokongxinzhi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC129_caokongxinzhi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC129_1_GainControlUntilEndOfTurn",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,     // 卡文"行动："（行动阶段打出）
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.GainControl, Args = new Array<string> { "目标", "直到回合结束" } },
                },
                Duration = "直到回合结束",
                FaqNote = "获得目标敌方角色的控制权，直到回合结束（回合结束时归还给原控制者）。",
            });
        }
    }
}
