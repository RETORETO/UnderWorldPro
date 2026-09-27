using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC127 终局 效果（事务卡；`Cost = 3`，子类别「灾难」）。
    /// 卡文：行动：选择目标地区，移除该地区上的所有势力标志。然后若该地区有三个或更多时间标志，
    ///      则消灭该地区上的所有角色。
    ///
    /// 实现（全部使用既有原语 + 一个新谓词）：
    ///  · 本体＝事务卡打出即结算（`OnSorceryCast`），**目标＝地区**（`TargetRules`/`TargetSpec.Kind = Area`）；
    ///  · ①「移除该地区上的**所有**势力标志」＝ 两条 `RemoveToken{"势力标志","全部",…,"目标地区"}`：
    ///    势力标志按玩家分桶存储（`AreaManager`），故"所有"＝**对手的全部 + 自己的全部**；
    ///  · ②「然后**若**该地区有三个或更多时间标志，则消灭该地区上的所有角色」＝**第二条同时机效果**，
    ///    `Condition = { Predicate = "AreaMarkerAtLeast", Param = "时间标志,3" }`（**新谓词**：统计
    ///    `ZoneDetailId == 目标地区` 的所有卡上的该标志之和；`param` 形式 `"标志名,数量"`）
    ///    ＋ 群体动作 `Target.MassMode = MassMode.AllInArea` + `Destroy`（消灭＝走死亡时机）。
    /// </summary>
    public static class JC127_zhongju_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC127_zhongju", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),        // 卡文"选择目标地区"
            };

            // ① 移除该地区上的所有势力标志（对手的 + 自己的 = 全部）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC127_A_ClearPowerMarksInArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "全部", "对手", "目标地区" },
                    },
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "全部", "自己", "目标地区" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "选择目标地区：移除该地区上的所有势力标志（双方的全部）。",
            });

            // ② 若该地区有 ≥3 个时间标志 → 消灭该地区上的所有角色
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC127_B_DestroyAllIfMarkers",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition
                {
                    Predicate = "AreaMarkerAtLeast",     // 【P3·K5】新谓词：地区内卡上标志合计 ≥ N
                    Param = "时间标志,3",
                },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,       // 【K2】目标地区内全体
                    CanTargetSelf = true,                // 卡文"该地区上的**所有**角色"→ 含本方（与 JC050 同款写法）
                    NeedsSelection = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "随后若该地区上有三个或更多时间标志，则消灭该地区上的所有角色。",
            });
        }
    }
}
