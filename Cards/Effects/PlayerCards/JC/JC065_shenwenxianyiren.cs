using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC065 审问嫌疑人 效果（事务卡；`Cost = 1`，子类别「策略」）。
    /// 卡文：快速行动：选择目标地区，然后目标对手牺牲一个位于该地区的暗藏者。遏制1。
    ///      （目标玩家选择并移除一个他所拥有的势力标志）
    /// 实现（口径 A11/A12）：
    ///  · 打出目标＝**地区**（"选择目标地区"）；
    ///  · ① `SacrificeChoice{"暗藏者","目标玩家","目标地区"}` —— **由对手自己选择**牺牲哪个**位于该地区**的暗藏者
    ///    （无合法候选 → 该玩家跳过、什么都不付出）；牺牲＝死亡，走死亡时机；
    ///  · ② `Curb{"1"}`（遏制1：被遏制者＝缺省＝施术者对手，由他选择并移除一个自己拥有的势力标志，`CurbRule`）。
    /// </summary>
    public static class JC065_shenwenxianyiren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC065_shenwenxianyiren", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),        // "选择目标地区"
            };
            data.LimitToActionPhaseOnly = true;          // 卡文"快速行动"（与既有同类事务卡一致口径）

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC065_A_OpponentSacrificesCovertInArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.SacrificeChoice,
                        Args = new Array<string> { "暗藏者", "目标玩家", "目标地区" },
                    },
                    new EffectAction { Verb = ActionVerb.Curb, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "选择目标地区：由对手自己选择并牺牲一个位于该地区的暗藏者（没有则不牺牲）；随后遏制1"
                        + "（对手选择并移除一个自己拥有的势力标志）。",
            });
        }
    }
}
