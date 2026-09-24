using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC050 丧钟回响 效果（事务卡）。
    /// 卡文：行动：选择一个地区，消灭该地区上的所有角色和暗藏者。
    ///
    /// 实现（P3·K2 群体动作）：
    ///  · 打出目标＝**地区**（`TargetRules=[Area]`；"行动"限行动阶段）；
    ///  · 群体动作的作用地区＝打出时选定的目标地区（`TurnManager.ResolveEntry` 把 `TargetAreaId` 透传给
    ///    `EffectEngine.ResolveSorcery` → `ResolveEffect(targetAreaId)` → `MassTargetPool` 取集合）；
    ///  · 动作 `Destroy` 对该地区内所有角色与暗藏者各执行一次，**一次结算内全部完成**。
    /// </summary>
    public static class JC050_sangzhonghuixiang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC050_sangzhonghuixiang", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出时选择目标地区
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };
            data.LimitToActionPhaseOnly = true;   // "行动"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC050_1_DestroyAllInChosenArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    IncludeCovert = true,            // "…和暗藏者"
                    MassMode = MassMode.AllInArea,   // 【K2】"选定地区"内的所有卡
                    CanTargetSelf = true,            // 该地区内所有角色（不分敌我）都要被消灭
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "消灭选定地区上的所有角色与暗藏者（群体动作一次结算完成）。",
            });
        }
    }
}
