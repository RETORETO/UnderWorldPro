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
    /// **【2026-09 用户口径·A95】整条卡文是"一条效果里按顺序处理的三个动作"，不得拆成三条强制效果**：
    /// 原先拆成 3 条同时间效果时，①「放标志」与②「按地区标志数伤害」的收集/结算次序无法保证，
    /// 且②的 X 在**收集阶段**求值（那时标志还没放）会被 K1 门禁整条跳过 ⇒ 实测"只有牺牲那一回合才造成伤害"。
    /// 现改写为**一条** `OnTurnStarted` + `Forced` 效果，用新的**动作级**原语表达顺序：
    ///  · ① `PlaceCounter{"时间标志","1","自身"}`（普通动作 ⇒ 只执行一次）；
    ///  · ② `DealDamage{"$X"}` + `MassInArea = true`（**动作级群体** ⇒ 对该地区内每个角色各执行一次），
    ///    X ＝ `ValueExpr = "计数:地区内卡上标志:时间标志:本地区"`（**结算时**求值 ⇒ 含刚放下的这一个）；
    ///  · ③ `Sacrifice` + `RequirePredicate = "AreaMarkerAtLeast"` / `RequireParam = "时间标志,3"`
    ///    （**动作级条件** ⇒ 卡文"然后**若**…则…"，不满足则跳过本动作、不影响后续）；
    /// 动作顺序＝数组声明顺序，由 `EffectEngine.ExecuteSequentialMassActions` 保证（`TargetSpec.MassSequential`）。
    /// </summary>
    public static class JC048_zuzhoushalou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC048_zuzhoushalou", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 整体复查·修复】卡文"**结附于目标地区**"⇒ 声明打出目标大类（否则附属默认允许全部目标）。
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC048_1_PreparePlaceDamageSacrifice",
                Timing = EffectTiming.OnTurnStarted,     // 准备步骤
                Forced = true,                           // 卡文"强制触发"
                // X ＝ 该地区内所有卡上的时间标志之和；**结算时**求值（动作①刚放下的那个也算）
                ValueExpr = "计数:地区内卡上标志:时间标志:本地区",
                ValueAtResolve = true,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,       // "位于该地区的所有角色"
                    MassSequential = true,               // 【A95】动作按声明顺序执行（一条效果，三个动作）
                    CanTargetSelf = true,                // 不分敌我
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    // ① 在诅咒沙漏上放 1 个时间标志（只执行一次 —— 不声明 MassInArea）
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "时间标志", "1", "自身" },
                    },
                    // ② 该地区每有一个时间标志 → 对位于该地区的所有角色各造成 1 点伤害（逐个执行）
                    new EffectAction
                    {
                        Verb = ActionVerb.DealDamage,
                        Args = new Array<string> { "$X" },
                        MassInArea = true,
                    },
                    // ③ 然后若该地区时间标志 ≥ 3 → 牺牲自身（动作级条件，不满足则跳过）
                    new EffectAction
                    {
                        Verb = ActionVerb.Sacrifice,
                        RequirePredicate = "AreaMarkerAtLeast",
                        RequireParam = "时间标志,3",
                    },
                },
                Duration = "瞬时",
                FaqNote = "准备步骤强制触发：在诅咒沙漏上放置一个时间标志 → 该地区每有一个时间标志，"
                        + "就对位于该地区的所有角色造成 1 点伤害 → 然后若该地区有三个或更多时间标志，则牺牲诅咒沙漏。"
                        + "（一条效果内的三个动作按序处理。）",
            });
        }
    }
}
