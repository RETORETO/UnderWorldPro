using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC003 帷幕护卫 效果（自原 JC003_weimuhuwei 构造函数迁移，逻辑不变）。
    /// 角色激活（JC003-1）：快速行动，横置本角色并支付 2 点费用，横置目标角色或暗藏者（无视地区）。
    /// 激活框架（D9）未接通前为声明态，玩家在游戏内尚无法发动。
    /// </summary>
    public static class JC003_weimuhuwei_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC003_weimuhuwei", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC003_1_Tap",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    Filter = "或暗藏者",
                    NeedsSelection = true,
                    IncludeCovert = true,
                    // 【2026-09 修复·A62】卡文"横置目标角色或暗藏者"**未排除自身** ⇒ 可指定自身
                    // （与 JC008/JC069 同款约定）；否则帷幕护卫是场上的唯一角色/暗藏者时该能力无法发动。
                    CanTargetSelf = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap }
                },
                Duration = "瞬时",
                FaqNote = "无视地区"
            });
        }
    }
}
