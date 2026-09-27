using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC048 诅咒沙漏 效果（附属卡；`Cost = 2`，子类别「法器」，结附于目标地区）。
    /// 卡文：结附于目标地区。准备步骤强制触发：在诅咒沙漏上放置一个时间标志。然后该地区每有一个时间标志，
    ///      就对位于该地区的所有角色造成1点伤害。然后若该地区上有三个或更多时间标志，则牺牲诅咒沙漏。
    ///
    /// 实现（三步拆成两条同时机效果，按序结算——事务/触发类效果按声明顺序执行，正好表达卡文的"然后…然后…"）：
    ///  · **① 放标志 + 按地区标志数造成群体伤害**：时机 `OnTurnStarted`（＝**准备步骤**）+ `Forced = true`
    ///    （卡文"**强制**触发"）：
    ///    - `PlaceCounter{"时间标志","1","自身"}`（先放，故本次伤害计数**含刚放下的这一个**，符合卡文顺序）；
    ///    - 群体伤害：`Target{Kind=Character, MassMode=AllInArea, CanTargetSelf=true}` + `DealDamage{"$X"}`
    ///      其中 `ValueExpr = "计数:地区内卡上标志:时间标志:本地区"`（**本批新增的取值写法**：该地区内所有卡上的
    ///      时间标志之和；与条件谓词 `AreaMarkerAtLeast` 共用同一统计实现）；
    ///  · **② 若该地区时间标志 ≥ 3 → 牺牲自身**：时机 `OnTurnStarted` +
    ///    `Condition{Predicate="AreaMarkerAtLeast", Param="时间标志,3"}`（地区取自**来源卡所在地区**）
    ///    + `Sacrifice`（牺牲＝死亡，走死亡时机）。
    /// </summary>
    public static class JC048_zuzhoushalou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC048_zuzhoushalou", Build);

        private static void Build(PlayerCardData data)
        {
            // ①a 准备步骤强制触发：先在诅咒沙漏上放 1 个时间标志
            // ⚠ 必须与"群体伤害"**分成两条效果**：K2 群体动作的语义是"对**每个**目标各执行一次整条动作序列"，
            //   若放在同一条里，放标志会被重复执行（每个目标各放一次）——本轮实测踩到。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC048_A_PlaceTimeMarkerOnSelf",
                Timing = EffectTiming.OnTurnStarted,     // 准备步骤
                Forced = true,                           // 卡文"强制触发"
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "时间标志", "1", "自身" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "准备步骤：在诅咒沙漏上放置一个时间标志。",
            });

            // ①b 然后该地区每有 1 个时间标志 → 对位于该地区的所有角色各造成 1 点伤害
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC048_B_AreaDamagePerMarker",
                Timing = EffectTiming.OnTurnStarted,
                Forced = true,
                ValueExpr = "计数:地区内卡上标志:时间标志:本地区",   // X ＝ 该地区内所有卡上的时间标志之和（含刚放下的）
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,       // 位于该地区的所有角色
                    CanTargetSelf = true,                // 不分敌我
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.DealDamage,
                        Args = new Array<string> { "$X" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "该地区每有一个时间标志，就对位于该地区的所有角色造成 1 点伤害。",
            });

            // ② 若该地区时间标志 ≥ 3 → 牺牲自身
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC048_C_SacrificeSelfIfThreeMarkers",
                Timing = EffectTiming.OnTurnStarted,
                Forced = true,
                Condition = new EffectCondition
                {
                    Predicate = "AreaMarkerAtLeast",     // 【P3·K5】地区内卡上标志合计 ≥ N
                    Param = "时间标志,3",
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Sacrifice },
                },
                Duration = "瞬时",
                FaqNote = "随后若该地区上有三个或更多时间标志，则牺牲诅咒沙漏。",
            });
        }
    }
}
