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

            // 【2026-09 用户口径·声望改触发】"声望"的规则本体＝**触发效果**（该地区所有对抗结束后，
            // 本地区声望角色较多的一方放置一个势力标志；不依赖对抗胜负、只数声望角色）。
            // 卡面固有关键字不会被 `RuntimeTriggerTemplates` 通道重复合成，故这里必须**显式**声明触发条目。
            var renownTrigger = KeywordEffects.Renown();
            renownTrigger.EffectId = "JC011_B2_RenownTrigger";
            data.EffectDataList.Add(renownTrigger);

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
                // 【2026-09 修复（用户口径）】本效果**不是强制触发**：卡文只写"现身触发：横置…"，
                // 没有"必须"语义 ⇒ 玩家可在触发排序窗口把它拖入放弃区（`Forced=true` 会禁止放弃）。
                Forced = false,
                Duration = "瞬时",
                FaqNote = "现身进场成功后，横置本地区所有敌方角色（群体动作一次结算完成，**非强制**触发）。",
            });
        }
    }
}
