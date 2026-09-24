using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC090 灵魂契约 效果（附属，结附于地区）。
    /// 卡文：结附于目标地区 触发：每当一个本方角色死亡时，在受此结附的地区上放置一个本方势力标记。
    /// 实现：① 结附地区 = `TargetRule.Make(TargetKind.Area)`（与 JC035 同款）；
    ///      ② `Timing=OnCardDied`（P1 接线）+ 条件 `DiedAllyCharacter`（P1 新增谓词：死亡的是本方角色且非自身）；
    ///      ③ `PlaceToken` 落点取 source.ZoneDetailId（= 结附地区），复用 JC035 既有链路。
    /// </summary>
    public static class JC090_linghunqiyue_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC090_linghunqiyue", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：仅地区
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC090_1_MarkOnAllyDeath",
                Timing = EffectTiming.OnCardDied,
                Scope = ZoneScope.AttachTarget,     // 附属默认生效区（结附中）
                Forced = false,
                Condition = new EffectCondition { Predicate = "DiedAllyCharacter" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PlaceToken, Args = new Array<string> { "势力标志", "1" } },
                },
                Duration = "瞬时",
                FaqNote = "每当一个本方角色死亡时，在本附属结附的地区放置一个本方势力标记。",
            });
        }
    }
}
