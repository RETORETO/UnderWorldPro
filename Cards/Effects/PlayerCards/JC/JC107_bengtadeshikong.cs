using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC107 崩塌的时空 效果（事务卡 / 灾难）。
    /// 卡文：行动：消灭目标附属或资产。
    ///
    /// 实现（P3·K14 或种类目标）——用户 2026-09 口径：**只能选择单个目标**，只要它是"附属 或 资产"其中之一即可：
    ///  · 打出目标：**两条 `TargetRule`**（`Attachment` / `Asset`）→ 打出规则本就是 OR（任一命中即合法）；
    ///  · 效果目标：`TargetSpec{Kind=Attachment, KindAlt=Asset}` → 取候选时两个大类合并（`TargetZonePool`），
    ///    结算复检接受任一（`KindAlt` OR）。
    /// </summary>
    public static class JC107_bengtadeshikong_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC107_bengtadeshikong", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：附属 或 资产（两条规则 = OR）
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Attachment),
                TargetRule.Make(TargetKind.Asset),
            };
            data.LimitToActionPhaseOnly = true;   // "行动"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC107_1_DestroyAttachmentOrAsset",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Attachment,     // 或…
                    KindAlt = TargetKind.Asset,       // …资产（单目标二选一）
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "消灭一个目标附属或资产（单目标；可以是结附于角色/秘社/地区的附属，也可以是资产区的资产）。",
            });
        }
    }
}
