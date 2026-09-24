using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC071 阿瓦隆隐士 效果（角色）。
    /// 卡文：快速行动，横置：本地区的另一个目标本方角色获得+1防御力直到回合结束。
    /// 实现：与 JC003 帷幕护卫同款激活骨架；`Gain{"防御","1"}` + `Duration="直到回合结束"`
    ///      （时效修正栈在回合结束自动还原）。
    /// ⚠️ 引擎边界：加防只改实例 CurrentDefense，而"能否承伤"读模板印刷防御（HasDefense）——
    ///    对印刷无防御的卡，本效果加防不改变其承伤能力（见《JC卡牌效果实现清单》§二·补 约束 2）。
    /// </summary>
    public static class JC071_awalongyinshi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC071_awalongyinshi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC071_1_DefenseUp",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,          // 卡文"另一个"本方角色
                    IsLocalZone = true,             // 本地区
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1" } },
                },
                Duration = "直到回合结束",
                FaqNote = "横置自身为代价；仅本地区、本方、非自身角色。",
            });
        }
    }
}
