using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC062 调查档案 效果（事务卡）。
    /// 卡文：快速行动：检视至多两个目标暗藏者。抓一张牌。
    /// FAQ：即使场上没有可以检视的暗藏者，你仍然能打出调查档案，仅仅为了抓一张牌。
    ///
    /// 实现（P3·H 检视 + K15 多目标）：
    ///  · **快速行动** ＝ 可响应打出（C 档，与 JC008 等同一实现口径：`PlayTiming.Response`）；
    ///  · **检视至多两个目标暗藏者**：`TargetSlots` 两个**可留空**槽（都判 `Kind=Covert`，各槽最多 1 个目标）、
    ///    `MinChosenSlots=0` → 可以一个都不检视（FAQ：只为抓牌）；由 `TurnManager.BeginMultiTargetPlay` 的
    ///    "逐槽取目标 → 确认面板 → 入堆叠"流程收集；
    ///  · `Inspect` 动作**逐个目标**执行（多目标模式下动作序列对每个目标各跑一次）→ 每个被选中的暗藏者私下呈现给检视者；
    ///  · **抓一张牌**单独放在第二条 `OnSorceryCast` 效果里（无目标）——避免落进多目标循环被重复抓牌。
    /// </summary>
    public static class JC062_diaochadangan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC062_diaochadangan", Build);

        private static void Build(PlayerCardData data)
        {
            // "快速行动"：卡上含 PlayTiming.Response 的效果即可响应打出（C 档，见 PlayerCardData.CanPlayAsResponse）

            // ① 检视至多两个目标暗藏者（两个可留空槽；至少 0 个）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC062_1_InspectUpToTwoCoverts",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                TargetSlots = new Array<TargetSlot>
                {
                    new TargetSlot
                    {
                        Label = "目标暗藏者（第 1 个，可留空）",
                        Optional = true,
                        Spec = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true },
                    },
                    new TargetSlot
                    {
                        Label = "目标暗藏者（第 2 个，可留空）",
                        Optional = true,
                        Spec = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true },
                    },
                },
                MinChosenSlots = 0,   // 可以一个都不检视（FAQ：仅为抓一张牌也能打出）
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Inspect, Args = new Array<string> { "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "检视至多两个目标暗藏者（可 0 个）；检视是私密查看，不翻面、不改状态。",
            });

            // ② 抓一张牌（无目标，独立效果 → 一次结算只抓一张）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC062_2_DrawOne",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "然后抓一张牌（无论是否检视了暗藏者）。",
            });
        }
    }
}
