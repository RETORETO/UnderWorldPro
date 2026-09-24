using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC024 S．P．T董事局 效果（角色，领袖）。
    /// 卡文：领袖 进场触发：所有其他本方角色获得机动直到回合结束。
    ///      快速行动2：将S．P．T董事局潜伏。在下一个准备步骤开始时，令其现身，且不需要支付其费用。
    ///
    /// 已接入：**领袖**（P4·`LeaderRule` 唯一性）+ **进场触发（所有其他本方角色获得机动直到回合结束）**
    /// + **快速行动2（潜伏 + 下个准备步骤免费用现身）**：
    ///  · 进场触发：`Gain{"机动","1","所有其他本方角色"}`（**群体主体**写法：全场本方角色逐个授予）
    ///    + `Duration="直到回合结束"`（逐卡登记时效项）；被授予的【机动】**运行期授予也能参与触发收集**
    ///    （K13：`TriggerRegistry` 会合成该关键词的触发形态，卡面固有者不重复）；
    ///  · 动作 1 `Latent{自身}`：把本卡翻面为暗藏者；
    ///  · 动作 2 以 `EffectData.Pending` 登记**一次性承诺**：`Timing = OnTurnStarted`（准备步骤＝每回合开始广播的时机）、
    ///    `Expire = ""`（不过期，等它自然兑现——"**下一个**准备步骤"；因本能力在准备步骤后发动时该事件已过，
    ///    故必然落在下一回合的准备步骤）、`Optional = false`（卡文无"你可以"→ 强制）；
    ///    兑现动作 `Uncover{自身}`（`CardManager.RevealCard`，**不经过打出/翻面付费通道** → 即"不需要支付其费用"）。
    /// **仍未实现**：无（两条效果均已接入）。
    /// </summary>
    public static class JC024_SPTdongshiju_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC024_SPTdongshiju", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC024_A_Leader";
            data.EffectDataList.Add(leader);

            // 进场触发：所有其他本方角色获得【机动】直到回合结束
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC024_1_ManeuverToOtherAllies",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // Args[2]＝群体主体："其他"＝排除本卡自身；Duration 含"回合结束" → 逐卡登记时效项
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "机动", "1", "所有其他本方角色" } },
                },
                Duration = "直到回合结束",
                FaqNote = "进场时所有其他本方角色获得【机动】直到回合结束（可放弃）。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC024_2_LatentThenFreeUncoverNextPrepare",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 2,                       // 卡文"快速行动**2**"
                Forced = false,
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // ① 将自身潜伏（翻面为暗藏者）
                    new EffectAction { Verb = ActionVerb.Latent, Args = new Array<string> { "自身" } },
                },
                // ② 登记"下一个准备步骤开始时兑现"的承诺（不立即现身）
                Pending = new PendingEffectSpec
                {
                    Timing = EffectTiming.OnTurnStarted,   // 准备步骤（每回合开始）
                    Expire = "",                           // 不过期：等到"下一个准备步骤"自然兑现
                    Optional = false,                      // 卡文无"你可以" → 强制兑现
                    Actions = new Array<EffectAction>
                    {
                        // 自身现身：`RevealCard` 直连，不支付任何费用（卡文"且不需要支付其费用"）
                        new EffectAction { Verb = ActionVerb.Uncover, Args = new Array<string> { "自身" } },
                    },
                },
                Duration = "瞬时",
                FaqNote = "快速行动2：潜伏本角色；在下一个准备步骤开始时令其免费现身（不支付其费用）。",
            });
        }
    }
}
