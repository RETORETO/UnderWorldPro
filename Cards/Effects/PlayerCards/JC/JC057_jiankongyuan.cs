using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC057 监控员 效果（角色，人类/工程师，国家机构）。
    /// 卡文：现身触发：检视目标玩家的手牌。 持续：只要监控员未横置，你可以随时检视本地区的任一暗藏者。
    ///
    /// 本轮交付**第一条**（K24 玩家目标 + P3·H 检视）：
    ///  · **现身触发** ＝ `EffectTiming.UncoverTrigger`（暗藏→公开时由 `CardManager.RevealCard` 广播）；
    ///  · **目标玩家** ＝ `Target{Kind=Player, Restrictions=[Enemy]}` → 触发入堆叠前经**独立选择窗口**就地选定
    ///    （`TurnManager.EnsurePlayerTargetThenPush`，与卡目标同口径：**入堆叠时锁定**）；
    ///  · **检视该玩家的手牌** ＝ `Inspect{"目标玩家手牌"}`（私密查看：只给来源控制者本人看，不改任何状态）。
    /// **第二条（持续·随时检视本地区任一暗藏者）仍未实现**：需要"持续可见性授权"（不经过效果结算的查看通道），
    /// 见 `JC卡牌效果进度表.md` 与 §7.9 K8。
    /// </summary>
    public static class JC057_jiankongyuan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC057_jiankongyuan", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC057_1_InspectTargetPlayerHand",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Inspect, Args = new Array<string> { "目标玩家手牌" } },
                },
                Duration = "瞬时",
                FaqNote = "现身时检视目标玩家（对手）的手牌；检视是私密查看，不给其他人看、也不改变任何状态。",
            });
        }
    }
}
