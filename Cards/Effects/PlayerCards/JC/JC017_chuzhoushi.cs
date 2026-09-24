using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC017 除咒师 效果（角色，人类/法师）。
    /// 卡文：进场触发：消灭目标角色上的所有敌方附属。本回合中，该角色获得屏障。
    ///      （屏障角色不能成为敌方卡牌或能力的目标。）
    ///
    /// 实现（一条效果、一次取目标，动作链按卡文顺序）：
    ///  · **进场触发** ＝ `EnterTrigger`；目标 ＝ 目标**角色**（本地区候选，与既有进场触发口径一致）；
    ///  · ① `DestroyOnTarget{附属, 对手}`：消灭**该角色上的所有敌方附属**（K2 扩展：集合＝宿主卡上的附属；
    ///    阵营筛选基准＝来源卡控制者）——**集合为空也不影响后续动作**（卡文无"若你如此做"）；
    ///  · ② `Gain{屏障,1,目标}` + `Duration="直到回合结束"`：该角色获得屏障直到回合结束（时效项到期自动撤销）。
    /// </summary>
    public static class JC017_chuzhoushi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC017_chuzhoushi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC017_1_DestroyEnemyAttachmentsThenBarrier",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    // ① 消灭目标角色上的所有**敌方**附属
                    new EffectAction { Verb = ActionVerb.DestroyOnTarget,
                        Args = new Array<string> { "附属", "对手" } },
                    // ② 本回合中该角色获得屏障
                    new EffectAction { Verb = ActionVerb.Gain,
                        Args = new Array<string> { "屏障", "1", "目标" } },
                },
                Duration = "直到回合结束",
                FaqNote = "进场时消灭目标角色上的所有敌方附属；该角色本回合获得屏障。",
            });
        }
    }
}
