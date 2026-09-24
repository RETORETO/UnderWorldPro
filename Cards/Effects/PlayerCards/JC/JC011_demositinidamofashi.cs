using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC011 德莫斯提尼，大魔法师 效果（角色）。
    /// 卡文：领袖 声望 现身触发：横置所有位于本地区的敌方角色。
    /// 实现（P3）：
    ///  · 领袖：持续关键字 + `LeaderRule` 唯一性规则；
    ///  · 声望：`RenownKeyword()`（规则本体由 `RenownRule` 引擎级消费，避免与卡级 `Renown()` 重复放置）；
    ///  · **现身触发·群体横置（K2）**：`MassMode=AllInArea` + `Kind=Character` + `Restrictions=[Enemy]` + `Tap`
    ///    → 作用地区＝现身事件所在地区（本地区），对其中所有敌方角色各横置一次。
    /// </summary>
    public static class JC011_demositinidamofashi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC011_demositinidamofashi", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC011_A_Leader";
            data.EffectDataList.Add(leader);

            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = "JC011_B_Renown";
            data.EffectDataList.Add(renown);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC011_1_TapAllEnemyInArea",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,   // 【K2】本地区内所有符合条件的卡
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                    CanTargetSelf = false,           // "敌方角色"（自身是己方，天然被 Restrictions 排除）
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                },
                Forced = true,
                Duration = "瞬时",
                FaqNote = "现身进场成功后，横置本地区所有敌方角色（群体动作一次结算完成）。",
            });
        }
    }
}
