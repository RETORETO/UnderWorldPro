using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC114 好奇的黑客 效果（角色）。
    /// 卡文：**行动1，横置：目标玩家展示其手牌。你可以从中选择一张，并牺牲好奇的黑客，
    ///       若你如此做，该玩家弃掉你所选择的牌。**
    ///
    /// 实现（全部复用既有原语，用户口径 **A13** ＋ 交互答复）：
    ///  · 激活：`CharacterActivated` + `ActivateWindow="行动"` + `Cost=1` + `ActivateCondition="横置本角色"`
    ///    （＝"行动1，横置"：代价在**入堆叠之前**支付，与 JC015/JC096 同款）；目标＝**玩家**（K24，
    ///    入堆叠时锁定 `StackEntry.TargetPlayerId`）；
    ///  · 动作链（顺序即卡文顺序）：
    ///     ① `Inspect{"目标玩家手牌"}`＝**展示/查看其手牌**——按用户口径**复用私密检视窗口**
    ///        （只有施术者本人看到，不移动、不翻面、不改状态）；
    ///     ② `SacrificeSelfOptional`＝"**你可以**…并**牺牲好奇的黑客**"——弹出 0/1 窗口（"牺牲/不牺牲"）；
    ///        **牺牲 → `ResolveDeath(Sacrifice)`（照常触发死亡时机）后继续**；
    ///        **不牺牲（或取消、或无 UI）→ 后续动作整段跳过**（卡文"**若你如此做**"＝链式中止语义）；
    ///     ③ `Discard{"1","目标玩家","由施术者指定"}`＝**该玩家弃掉你所指定的那张牌**：
    ///        由**施术者**从该玩家手牌里指定（`PromptDiscard(弃牌者, 选择者＝施术者, 候选＝其手牌)`；
    ///        联机侧只把候选项发给选择者本人）。
    ///  · 效果结算时来源卡可能已在墓地（②牺牲了自身）——后续动作仍以"已捕获的来源/控制者"结算，
    ///    与既有牺牲代价同款语义（JC105 的"触发卡"路径同理）。
    /// </summary>
    public static class JC114_haoqideheike_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC114_haoqideheike", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC114_1_RevealHandOptionalSacrificeDiscard",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",                 // 卡文"行动"
                Cost = 1,                                // 卡文"行动1"
                ActivateCondition = "横置本角色",          // 卡文"横置"（代价）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,            // 【K24】目标玩家（入堆叠时锁定）
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    // ① 展示/查看目标玩家手牌（私密检视：只给施术者本人）
                    new EffectAction { Verb = ActionVerb.Inspect, Args = new Array<string> { "目标玩家手牌" } },
                    // ② "你可以…并牺牲好奇的黑客"（可选；不牺牲 → 后续整段跳过）
                    new EffectAction { Verb = ActionVerb.SacrificeSelfOptional },
                    // ③ "若你如此做，该玩家弃掉你所选择的牌"（由施术者从其手牌中指定）
                    new EffectAction
                    {
                        Verb = ActionVerb.Discard,
                        Args = new Array<string> { "1", "目标玩家", "由施术者指定" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "行动1 + 横置自身：查看目标玩家手牌（仅施术者可见）→ 你可选择**牺牲好奇的黑客**，"
                        + "若如此做，则由你从该玩家手牌中指定一张，该玩家弃掉它；**不牺牲则什么都不发生**。",
            });
        }
    }
}
