using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC061 危机处理特派小组 效果（角色）。
    /// 卡文：**机动** 触发：当危机处理特派小组**进入一个地区**时，你可以支付1，若你如此做，消灭**位于该地区**的目标暗藏者。
    ///
    /// 实现（K11 可选支付 + 数值输入）：
    ///  · 时机＝**`EffectTiming.OnMove`（进入/移动到某个地区之后）**——这是"进入一个地区"的正确承载点：
    ///    `ZoneManager.MoveToArea` 在**位置变更之后**广播 `OnMove`（`TriggerEmitPoints.OnMove`，
    ///    带 `SourceAreaAtTrigger`＝新地区），**机动（争夺区→争夺区）与进场（手牌→争夺区）都覆盖**。
    ///    ⚠ 修复记录：原实现挂 `ManeuverTrigger`，而该时机其实是"**对抗前窗口打开时**"（`TurnManager.DoPreCombat`
    ///    广播，**移动尚未发生**），并且它会被判成"**取目标地区**"（`IsAreaTarget=true`）⇒ 效果实际变成
    ///    "对抗前窗口支付1消灭**移动前**地区的暗藏者"，与卡文不符（旧单测手工 `ResolveEffect` 传死目标，掩盖了这一点）。
    ///  · 目标 `Kind=Covert`：`IsLocalZone=true` → 候选＝**该地区**（OnMove 之后＝**新进入的地区**）的暗藏者；
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
                Timing = EffectTiming.OnMove,   // 「进入一个地区」＝位置变更后（机动/进场都覆盖，见类注释）
                // 自身语义由引擎保证：`TriggerRegistry` 把 `OnMove` 列入"只对事件主体卡成立"的时机白名单
                // （与 EnterTrigger/UncoverTrigger/OnLatent 同款），故无需写"事件主体＝本卡"的条件。
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
