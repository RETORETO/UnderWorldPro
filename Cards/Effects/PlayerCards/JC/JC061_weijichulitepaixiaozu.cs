using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC061 危机处理特派小组 效果（角色）。
    /// 卡文：机动 触发：当危机处理特派小组进入一个地区时，你可以支付1，若你如此做，消灭位于该地区的目标暗藏者。
    ///
    /// 实现（K11 可选支付 + 数值输入）：
    ///  · 「机动」＝`ManeuverTrigger`（争夺区↔争夺区移动后触发，`SourceAreaAtTrigger`＝进入的地区）；
    ///  · 目标 `Kind=Covert`：`IsLocalZone=true` → 候选＝**该地区**（进入的地区）的暗藏者；
    ///  · 动作序列：`PayOptional{1,0,1}` —— **弹数值输入框**问"是否支付 1"（用户 2026-09 口径：输入的数字即为 N；
    ///    这里范围锁定为 0..1，默认 1）→ 支付则扣 1 费并继续 → `Destroy`（消灭该目标）；
    ///    **不支付 → 不扣费且后续动作整段跳过**（"若你如此做…"的既有链式中止语义）。
    /// 注：目标在"取目标阶段"先行选定（触发管线的既有顺序），随后在结算时才询问是否支付；不支付则无事发生。
    /// </summary>
    public static class JC061_weijichulitepaixiaozu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC061_weijichulitepaixiaozu", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC061_1_PayToDestroyCovertInArea",
                Timing = EffectTiming.ManeuverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Covert,      // 目标暗藏者
                    NeedsSelection = true,
                    IsLocalZone = true,            // 位于"该地区"（进入的地区）
                },
                Actions = new Array<EffectAction>
                {
                    // 可选支付 1（范围 0..1，默认 1）：输入的数字即为 N
                    new EffectAction
                    {
                        Verb = ActionVerb.PayOptional,
                        Args = new Array<string> { "1", "0", "1" },
                    },
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "进入地区时可支付 1（可选，弹数值输入框）→ 支付则消灭该地区的目标暗藏者；不支付则无事发生。",
            });
        }
    }
}
