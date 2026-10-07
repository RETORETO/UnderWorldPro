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
    /// 实现：
    ///  · **[前缀] 机动** ＝ `KeywordEffects.Maneuver()`（`EffectId="JC061_0_Maneuver"`）：对抗前优先权窗口
    ///    （`ManeuverTrigger`）选择另一地区并移动过去。**卡面关键词必须由卡脚本登记**（`.tres` 只放文本，
    ///    不参与装配）——2026-09 用户实测"机动效果没有触发"的根因就是它原先**完全没登记**。
    ///  · **触发时机＝`EffectTiming.OnMove`（进入/移动到某个地区之后）**："进入一个地区"的正确承载点：
    ///    `ZoneManager.MoveToArea` 在**位置变更之后**广播 `OnMove`（带 `SourceAreaAtTrigger`＝新地区），
    ///    **机动（争夺区→争夺区）与进场（手牌→争夺区）都覆盖**。
    ///    ⚠ 历史修复：原实现挂 `ManeuverTrigger`＝"对抗前窗口打开时"（`DoPreCombat` 广播，**移动尚未发生**）
    ///    且会被判成"取目标地区" ⇒ 实际变成"对抗前窗口支付1消灭**移动前**地区的暗藏者"，与卡文不符。
    ///  · **目标** `Kind=Covert` + `IsLocalZone=true` ⇒ 候选＝**该地区**（OnMove 之后＝**新进入的地区**）的暗藏者。
    ///  · **费用＝`OptionalPayCost=1`（可选固定费用，2026-09 用户口径）**：这是"**额外费用**"性质——
    ///    在**触发入堆叠之前**问一次"**是否支付 1**"（**只有是/否两个选项，不需要输入金额**，选"是"就付 1）；
    ///    **不支付 ⇒ 该触发不入堆叠**（整条效果不结算，也不扣费）；放弃整条效果请用**触发排序窗**
    ///    （该费用窗不提供"取消"）。收口＝`TurnManager.EnsureOptionalPayThenPush` / `...Async`，
    ///    位置在**取目标之后、`PushTriggerToStack` 之前**（选目标阶段取消不会白扣费）。
    ///    不再使用 `PayOptional`（那是"结算时弹数值输入框"，用户实测口径："用错了选择弹窗"）。
    /// </summary>
    public static class JC061_weijichulitepaixiaozu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC061_weijichulitepaixiaozu", Build);

        private static void Build(PlayerCardData data)
        {
            // ==================== 【2026-09 修复】[前缀] 机动 ====================
            // 用户实测："JC061 的机动效果没有触发"——根因是**卡面 [前缀] 的『机动』从未登记**：
            // `.tres` 不承载关键词（关键词一律由卡脚本写进 `EffectDataList`，见 JC014/JC023 同款），
            // 而本卡原先只写了下面那条 `OnMove` 触发 ⇒ 对抗前窗口（`ManeuverTrigger`）收集不到任何
            // 本卡效果，玩家根本没有"机动过去"这个动作可用，自然"机动不触发"。
            var maneuver = KeywordEffects.Maneuver();
            maneuver.EffectId = "JC061_0_Maneuver";
            data.EffectDataList.Add(maneuver);

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
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                // 【2026-09 用户口径】"支付1"＝**额外费用**：入堆叠前问"是否支付1"（是/否两选项、不输入金额）；
                // 不支付 ⇒ 该触发不入堆叠（整条效果不结算）。
                OptionalPayCost = 1,
                Duration = "瞬时",
                FaqNote = "进入地区时可以支付 1（入堆叠前询问『是否支付 1』，两个选项）→ 支付后消灭该地区的目标暗藏者；"
                        + "不支付则该触发不结算（放弃整条效果请用触发排序窗）。",
            });
        }
    }
}
