using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC126 产业扩张 效果（事务卡）。
    /// 卡文：行动：将你牌库顶的一张牌正面置入你的资产区，令其成为一个资产。
    ///
    /// 实现（P3·G 区域转换）：`MoveToZone{"牌库顶","资产区","正面"}`
    ///  · 取**你（来源控制者）牌库顶**一张（牌库索引 0 = 顶），从牌库列表移出后置入你的资产区；
    ///  · 进入资产区即按区域被视作"资产"（`CardManager.MapInstanceToTargetKind` 按区域映射；
    ///    费用来源 `PlayerManager.GetAvailableMana` 亦按"资产区未横置卡数"计），故"令其成为一个资产"由落区满足；
    ///  · 牌库为空 → 无事发生（不崩）。
    /// </summary>
    public static class JC126_chanyekuozhang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC126_chanyekuozhang", Build);

        private static void Build(PlayerCardData data)
        {

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC126_1_DeckTopToAsset",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.MoveToZone,
                        Args = new Array<string> { "牌库顶", "资产区", "正面" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "牌库顶一张牌正面进入你的资产区，成为资产（可用于支付费用）。",
            });
        }
    }
}
