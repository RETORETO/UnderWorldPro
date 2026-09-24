using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC076 悟道僧人 效果（角色）。
    /// 卡文：声望（当一个地区的所有对抗结束后，在该地区上若你比对手操控更多未横置的声望角色，
    ///            则你可以在该地区额外放置一个本方势力标志。）
    ///      现身触发：抓一张牌。
    /// 实现（效果级推进）：**只做现身触发**（`Draw`，唯一抽牌收口，会广播 `OnDraw`）。
    /// ⛔ 未做「声望」词条：声望的完整规则（对抗结束后比较未横置声望角色 → 额外放势力标志）属未实现的机制，
    ///    且用户已说明将在 P5 给出完整词条规则 → 见进度表该行的 ⛔ 说明。
    /// </summary>
    public static class JC076_wudaosengren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC076_wudaosengren", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC076_2_UncoverDraw",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "现身进场后抓一张牌。",
            });
        }
    }
}
