using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC051 诺恩纳格斯塔 效果（角色）。
    /// 卡文：领袖 现身触发：自你的牌库中寻找一张徽章附属牌，将该牌放置进场并结附于合理的目标，然后洗牌。
    ///      持续：所有其他本方鸣钟教派角色获得［白星］。
    /// 实现（效果级推进）：
    ///  · **领袖**：持续关键字 + `LeaderRule` 唯一性规则；
    ///  · **现身触发·检索**：`UncoverTrigger` + `Search`（筛选 `子类别:徽章`、去向 `结附`）；
    ///    『结附于**合理的目标**』= 候选由**被检索卡自身的 TargetRules** 给出（如检索到 JC012『结附于本方秘社』
    ///    → 候选为本方秘社），**由玩家从候选中选择**（单机 `PromptPickCard`／联机 `SelectTarget`，A53 口径；
    ///    仅 1 个候选时直接结附），**无合法宿主按"无合法目标"处理**（牌回牌库）——
    ///    见 `EffectEngine.AttachSearchedCardByOwnRules` 与 `EffectAction` 的 `Search` 注释。
    ///  · **持续：所有其他本方鸣钟教派角色获得［白星］**（P3·E）：光环 `IsAura` + **组织筛选** `AuraFilterOrganization="鸣钟教派"`
    ///    （范围＝来源控制者的**全部**争夺区角色，用户 2026-09 口径）。
    /// </summary>
    public static class JC051_nuoennagesita_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC051_nuoennagesita", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC051_A_Leader";
            data.EffectDataList.Add(leader);

            // 持续光环：所有其他本方【鸣钟教派】角色获得白星
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC051_B_AuraWhiteStarToSect",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                IsAura = true,
                AuraFilterOrganization = "鸣钟教派",   // 【P3·E】按组织筛选
                ContinuousEffect = "所有其他本方鸣钟教派角色获得［白星］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "白星", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "同一控制者的全部鸣钟教派角色（除自身外）获得白星；非该组织的角色不受影响。",
            });

            // 现身触发：自牌库寻找一张徽章附属牌 → 结附于"合理的目标"（= 候选由被检索卡自身 TargetRules 给出，
            // 由**玩家**从中选一个）→ 洗牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC051_1_SearchEmblemAttach",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },   // 检索不取目标（宿主候选由被检索卡自身规则给出）
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        // [筛选, 去向, 附加, 范围]：附加 = "按目标规则" → 结附去向的宿主候选 = 被检索卡自身 TargetRules 的合法宿主，
                        // 由玩家选择（单机 PromptPickCard / 联机 SelectTarget）；仅 1 个候选时直接结附（A53）
                        Args = new Array<string> { "子类别:徽章", "结附", "按目标规则", "牌库" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "检索到的徽章附属牌按其自身目标规则给出结附候选（如『结附于本方秘社』的徽记 → 候选为本方秘社），" +
                           "由玩家从中选择结附目标；若场上没有该牌合法的宿主，则按『无合法目标』处理（不结附，牌回牌库），随后仍然洗牌。",
            });
        }
    }
}
