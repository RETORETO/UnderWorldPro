using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC009 远东六号科研所 效果（附属 / 机构，结附于本方秘社）。
    /// 卡文：结附于本方秘社 进场触发：将目标角色封印在远东六号科研所上，然后遏制1。
    /// FAQ：◆进场时你可以选择一个角色，把该角色封印在远东六号科研所上（被封印的角色离场、牌面视作空白；
    ///      本卡离场时该角色牌移回其拥有者手上）。
    ///      ◆**如果未能成功将一个角色封印在其上，则之后的遏制1 能力无法结算**。
    ///
    /// 实现：
    ///  · 打出目标＝本方秘社（`TargetRule`），进场触发走 `EnterTrigger`（附属进场由 `MoveToAttachedZone` 广播）；
    ///  · 目标＝**任意角色**（FAQ 只要求"选择一个角色"）：`TargetSpec.AllAreas=true` 显式打开全场候选
    ///    ——本卡结附于秘社、**没有地区**，默认的"本地区"候选会是空的；
    ///  · 动作链＝`Seal{目标, 自身}` → `Curb{1, 对手}`；
    ///  · **"封印失败则不遏制"**（FAQ 第二条）由**动作成果**机制承担：`Seal` 未落地时置链式中止标志，
    ///    后续 `Curb` 整段跳过。
    /// </summary>
    public static class JC009_yuandongliuhaokeyansuo_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC009_yuandongliuhaokeyansuo", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：仅本方秘社
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC009_1_SealTargetThenCurb",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    AllAreas = true,          // 卡文未限定本地区（且本卡结附于秘社、没有地区）
                },
                Actions = new Array<EffectAction>
                {
                    // ① 把**目标角色**封印在**本卡**（远东六号科研所）上
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "目标", "自身" } },
                    // ② 然后遏制1（若①未成功生效 → 本动作整段跳过，见 FAQ 第二条）
                    new EffectAction { Verb = ActionVerb.Curb, Args = new Array<string> { "1", "对手" } },
                },
                Duration = "瞬时",
                FaqNote = "进场时把目标角色封印在本卡上，然后遏制1；未能成功封印则遏制不结算。",
            });
        }
    }
}
