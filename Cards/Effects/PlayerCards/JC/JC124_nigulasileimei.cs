using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC124 尼古拉斯·勒梅 效果（角色）。
    /// 卡文：行动3：在目标角色上放置一个哲人石标志。
    ///      强制触发：当具有哲人石标志的角色死亡时，将其从墓地移回场上，并由其拥有者操控。该能力在尼古拉斯·勒梅离场后仍然生效。
    /// 实现（效果级推进）：**只做"放置哲人石标志"**（角色激活：行动 + 费用3 + 新动词 `PlaceCounter`，卡文只写"目标角色"故不含暗藏者）。
    /// ⛔ 未做「具有哲人石标志的角色死亡时移回场上」：缺死亡替代效果（K3）+ 按标志筛选 +"离场后仍生效"的持久登记
    ///    （现 `UnregisterPersistent` 会在源卡离场时撤销持续效果）。
    /// </summary>
    public static class JC124_nigulasileimei_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC124_nigulasileimei", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC124_1_PlacePhilosopherStone",
                Timing = EffectTiming.CharacterActivated,
                Cost = 3,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    IncludeCovert = false,   // 卡文只写"目标角色"
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "哲人石标志", "1", "目标" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "行动3：在目标角色上放置一个哲人石标志。",
            });
        }
    }
}
