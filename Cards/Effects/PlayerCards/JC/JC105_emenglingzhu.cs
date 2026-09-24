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
    ///    `DiedAllySpiritCharacter`（另一个本方灵体角色）+ `PayOptional{2,2,2}` + `Seal{"触发卡","目标"}`
    ///    （"该角色"＝触发上下文卡＝刚死亡的角色；宿主由玩家在入堆叠时选定）。
    /// </summary>
    public static class JC105_emenglingzhu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC105_emenglingzhu", Build);

        private static void Build(PlayerCardData data)
        {
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
            //  · "你可以支付2" ＝ `PayOptional{2,2,2}`（K11：支付/不支付二选一，支付才继续；无 UI → 兜底"不支付"）；
            //  · 目标＝**目标角色**（宿主，入堆叠时选定）；动作主体 `"触发卡"`＝**刚死亡的角色**（`TriggerContextCard`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC105_B_PayToSealDeadSpiritOnTarget",
                Timing = EffectTiming.OnCardDied,
                Condition = new EffectCondition { Predicate = "DiedAllySpiritCharacter" },
                Forced = false,                        // 卡文"你可以支付2"
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PayOptional,
                        Args = new Array<string> { "2", "2", "2" },
                    },
                    new EffectAction
                    {
                        Verb = ActionVerb.Seal,
                        Args = new Array<string> { "触发卡", "目标" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "另一个本方灵体角色死亡时，你可以支付2；若如此做，把刚死亡的角色封印在目标角色上"
                        + "（从墓地取出、置入该角色的封印区），而非留在墓地。",
            });
        }
    }
}
