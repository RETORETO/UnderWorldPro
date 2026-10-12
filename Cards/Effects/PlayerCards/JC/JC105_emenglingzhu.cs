using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC105 噩梦领主 效果（角色）。
    /// 卡文：灵体 持续：所有其他本方灵体角色获得［白星］。 触发：当另一个本方灵体角色死亡时，你可以支付2，
    ///       若如此做，则将该角色封印在目标角色上，而非置入墓地。
    ///
    /// 实现（效果级推进）：
    ///  · 「灵体」由引擎级词条提供（伤害防止，见 `DamageRules`/`CardManager.DamagePreventer`）；
    ///  · **持续：所有其他本方灵体角色获得［白星］**（P3·E）：光环 `IsAura` + **关键词筛选**
    ///    `AuraFilterKeyword="灵体"`（运行期授予 ∪ 卡面固有；范围＝来源控制者的全部争夺区角色，用户 2026-09 口径）；
    ///  · **死亡触发**（P3·K3 死亡类第一批，用户 A10 口径＝**触发路线**）：`OnCardDied` + 谓词
    ///    `DiedAllySpiritCharacter`（另一个本方灵体角色）+ **发动时的可选固定费用** `OptionalPayCost=2`
    ///    + `Seal{"触发卡","目标"}`（"该角色"＝触发上下文卡＝刚死亡的角色；宿主由玩家在入堆叠时选定）。
    ///    【2026-10 用户口径·与 JC061/JC100 同一份】"你可以支付2，若你如此做…"的**费用在发动能力时**
    ///    支付（取完目标 → **入堆叠之前**问一次"是否支付 2"，是/否两个选项），不在效果处理时支付 ⇒
    ///    不再用 `PayOptional`（那是"结算时弹窗问支付"）。不支付 ⇒ 该触发不入堆叠（整条效果不结算）。
    /// </summary>
    public static class JC105_emenglingzhu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC105_emenglingzhu", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 修复·灵体同类排查】卡文以**灵体**开头，但脚本此前没有授予【灵体】关键词
            // ⇒ `DamageRules.IsSpirit` 为假 ⇒ 灵体的"我方魔法领域图标更多则防止伤害"永不生效。
            // 现按 JC077/JC111 同款"持续关键字授予"补齐（消费点＝`CardManager.DamagePreventer`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC105_S_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC105_A_AuraWhiteStarToSpirits",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                IsAura = true,
                AuraFilterKeyword = "灵体",   // 【P3·E】按关键词筛选
                ContinuousEffect = "所有其他本方灵体角色获得［白星］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "白星", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "先手势力", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "同一控制者的全部灵体角色（除自身外）获得白星；非灵体不受影响。",
            });

            // ==================== ② 触发：另一个本方灵体角色死亡 → 可支付2，改为把该角色封印在目标角色上 ====================
            // 口径（用户 2026-09，§7.4b A10）：**走触发路线**——角色**先进入墓地**，再由本触发把它**取出并封印**
            // 到玩家选定的宿主角色上（`SealCard` → `MoveToSealedZone` 内部 `RemoveFromAllZones` 会把它移出墓地），
            // 而不是"替代死亡处置"的替代式效应。
            //  · 条件 `DiedAllySpiritCharacter`＝"另一个本方灵体角色死亡"（角色 + 同控制者 + 排除来源自身 + 灵体）；
            //  · "你可以支付2" ＝ `OptionalPayCost=2`（**发动时**的可选固定费用：取完目标 → 入堆叠前问一次
            //    "是否支付 2"（是/否两选项，不输入金额）；不支付 ⇒ 该触发不入堆叠；付不起 ⇒ 不弹窗）；
            //  · 目标＝**目标角色**（宿主，入堆叠时选定）；动作主体 `"触发卡"`＝**刚死亡的角色**（`TriggerContextCard`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC105_B_PayToSealDeadSpiritOnTarget",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAllySpiritCharacter" },
                Forced = false,                        // 卡文"你可以支付2"
                // 【2026-09 修复】"将该角色封印在**目标角色**上"未排除自身 ⇒ 可指定自身
                // （与 JC008/JC069 同款约定）；否则它是本地区唯一角色时该触发被静默跳过（JC004 同类）。
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true, CanTargetSelf = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Seal,
                        Args = new Array<string> { "触发卡", "目标" },
                    },
                },
                OptionalPayCost = 2,                   // "你可以支付2"：发动时（入堆叠前）问是/否
                Duration = "瞬时",
                FaqNote = "另一个本方灵体角色死亡时，可以在**发动时**支付2（入堆叠前询问『是否支付 2』，两个选项）；"
                        + "若如此做，把刚死亡的角色封印在目标角色上（从墓地取出、置入该角色的封印区），而非留在墓地。",
            });
        }
    }
}
