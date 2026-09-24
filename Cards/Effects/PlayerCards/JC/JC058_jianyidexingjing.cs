using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC058 坚毅的刑警 效果（角色）。
    /// 卡文：现身触发：消灭本地区的目标暗藏者。
    /// 实现：与 JC002 巨石阵看护人同款触发骨架 + `Kind=Covert` 目标（暗藏者在争夺区桶内，候选可达）
    ///      + `Destroy`（P1 起"消灭"广播死亡触发）。
    /// </summary>
    public static class JC058_jianyidexingjing_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC058_jianyidexingjing", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC058_1_DestroyCovert",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Covert,      // 仅暗藏者（Kind=Character 会互斥排除暗藏者）
                    NeedsSelection = true,
                    IsLocalZone = true,            // 本地区（同 AREASLOT）
                    CanTargetSelf = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "现身进场后，消灭本地区的目标暗藏者。",
            });
        }
    }
}
