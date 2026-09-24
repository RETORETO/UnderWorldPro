using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC056 地区警员 效果（角色）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者） 进场触发：横置所有本地区的暗藏者。
    ///
    /// 实现（P3·K2 群体动作）：
    ///  · 公开：`KeywordEffects.Public()`（手牌期生效，禁止秘密派遣）；
    ///  · 进场触发：群体动作 `Kind=Covert`（＝暗藏者）＋`MassMode=AllInArea` ＋`Tap`
    ///    → 作用地区＝进场事件所在地区（本地区），对该地区内所有暗藏者各横置一次。
    /// </summary>
    public static class JC056_diqujingyuan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC056_diqujingyuan", Build);

        private static void Build(PlayerCardData data)
        {
            var publicKw = KeywordEffects.Public();
            publicKw.EffectId = "JC056_A_Public";
            data.EffectDataList.Add(publicKw);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC056_1_TapAllCovert",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Covert,        // 暗藏者
                    MassMode = MassMode.AllInArea,
                    CanTargetSelf = false,
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                },
                Forced = true,
                Duration = "瞬时",
                FaqNote = "进场时横置本地区所有暗藏者（群体动作一次结算完成）。",
            });
        }
    }
}
