using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC005 裂解术 效果（事务卡/快速行动）。
    /// 快速行动：消灭目标结附于角色的附属。若你的资产区具有【心】，则改为消灭目标附属。
    ///
    /// 设计（单一效果 + 条件式目标限制）：
    /// 两个分支最终动作都是"消灭目标附属"，唯一差异在【选目标(打出)阶段】的候选范围：
    ///  · 资产区有【心】→ 可消灭【任意】附属；
    ///  · 资产区无【心】→ 仅可消灭【结附于角色】的附属。
    /// 因此不拆两个效果，而是给目标加一条条件式限制 TargetRestriction.AttachedToCharacterIfNoMind：
    /// 取目标阶段由 CheckRestrictions 实时评估【心】决定是否要求"结附于角色"；
    /// 结算阶段不复检（用户规则：结算时【心】是否仍在不影响，目标未丢即消灭）。
    /// </summary>
    public static class JC005_liejieshu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC005_liejieshu", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC005_1_DestroyAttachment",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,   // 快速行动：有优先权即可打出（含响应窗口/堆叠非空）
                Target = new TargetSpec
                {
                    Kind = TargetKind.Attachment,
                    NeedsSelection = true,
                    Restrictions = new Godot.Collections.Array<TargetRestriction>
                    {
                        TargetRestriction.AttachedToCharacterIfNoMind
                    },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy }
                },
                Duration = "瞬时",
            });
        }
    }
}
