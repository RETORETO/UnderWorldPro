using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC134 审判 效果（事务卡；`Cost = 3`，子类别「灾难」）。
    /// 卡文：行动：每位玩家牺牲一个资产。
    /// 实现（口径 A11）：`SacrificeChoice{"资产","每位玩家","任意"}` ——
    /// 按先手优先逐个自选（后选者可见先选者的选择）→ 全部选定后一起牺牲（不分死亡先后）；
    /// 无合法候选者跳过（没有资产就什么都不付出）；"牺牲"照常走死亡时机。
    /// </summary>
    public static class JC134_shenpan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC134_shenpan", Build);

        private static void Build(PlayerCardData data)
        {

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC134_A_EachPlayerSacrificesAsset",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.SacrificeChoice,
                        Args = new Array<string> { "资产", "每位玩家", "任意" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "每位玩家各自选择牺牲一个资产（先手先选、之后一起牺牲）；没有资产的玩家不受影响。",
            });
        }
    }
}
