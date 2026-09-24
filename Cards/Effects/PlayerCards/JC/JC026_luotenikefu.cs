using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC026 洛特尼科夫 效果（角色）。
    /// 卡文：持续：每个回合中你所打出的第一张装备牌，其所需费用减少1。
    ///      进场触发：从你的牌库中寻找一张装备牌，展示该牌后置于你的手中，然后洗牌。
    /// 实现：
    ///  · **进场触发**（P2 检索）：`子类别:装备` + 去向手牌 + `展示` + 洗牌口径；
    ///  · **持续：每回合第一张装备牌费用 -1** ＝ `EffectData.ContinuousCostMod`
    ///    （`Amount=-1` / `Filter="子类别:装备"` / `PerTurn=true`）：为来源控制者挂玩家级费用修正；
    ///    限定条件走既有 `CostModFilter`（支持 `子类别:` 语法）；
    ///    **"每回合可再生的一次性"**：消费后本回合失效（`TimedMod.ConsumedTurn`），**下回合自动恢复**，
    ///    时效终结＝来源卡离场（`UnregisterContinuous` 按 `SourceUid` 清理）。
    /// </summary>
    public static class JC026_luotenikefu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC026_luotenikefu", Build);

        private static void Build(PlayerCardData data)
        {
            // 持续：每回合第一张装备牌费用 -1（每回合可再生的一次性）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC026_1_FirstEquipmentCostMinusOnePerTurn",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "每回合第一张装备牌费用 -1",
                ContinuousCostMod = new ContinuousCostModSpec
                {
                    Amount = 1,
                    Filter = "子类别:装备",
                    PerTurn = true,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本卡在场时，你每回合打出的第一张装备牌费用 -1（用掉后下回合自动恢复）。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC026_2_SearchEquipment",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        Args = new Array<string> { "子类别:装备", "手牌", "展示", "牌库" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "进场时检索一张装备牌，展示后入手并洗牌。",
            });
        }
    }
}
