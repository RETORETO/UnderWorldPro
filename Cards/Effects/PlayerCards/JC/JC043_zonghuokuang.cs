using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC043 纵火狂 效果（角色，人类/罪犯/邪教徒）。
    /// 卡文：进场触发：消灭目标**结附于秘社或本地区**上的附属。
    ///
    /// 实现（**复合目标限制**：两条完整目标声明 OR）：
    ///  · 目标＝附属，两条替代条件**条件不同**，无法用 `KindAlt`（只表达大类 OR）或 `Restrictions`（AND）表达：
    ///      ① 结附于**本地区** → `Restrictions=[LocalZone]`（`ZoneDetailId == 来源地区`）；
    ///      ② 结附于**秘社**   → `Restrictions=[HostIsCouncil]`（`AttachedTo` 是秘社实例）；
    ///  · 用 `TargetSpec.SpecAlt` 挂第二条：候选池＝两条并集（去重），合法性/结算复检＝任一条通过即通过；
    ///  · 动作＝`Destroy`（消灭）。
    /// </summary>
    public static class JC043_zonghuokuang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC043_zonghuokuang", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC043_1_DestroyAttachmentOnCouncilOrArea",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Attachment,          // 目标＝附属
                    NeedsSelection = true,
                    IsLocalZone = true,                    // ① 结附于**本地区**
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.LocalZone,       // 在本地区…
                        TargetRestriction.AttachedToArea,  // …且**直接结附于地区**（无宿主）
                    },
                    SpecAlt = new TargetSpec               // ② 或：结附于**秘社**
                    {
                        Kind = TargetKind.Attachment,
                        NeedsSelection = true,
                        Restrictions = new Array<TargetRestriction> { TargetRestriction.HostIsCouncil },
                    },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "进场时消灭目标『结附于秘社』或『结附于本地区』的附属（二者任一）。",
            });
        }
    }
}
