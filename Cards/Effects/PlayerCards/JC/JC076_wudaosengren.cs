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
    /// 实现：**现身触发**（`Draw`，唯一抽牌收口，会广播 `OnDraw`）＋
    ///      【2026-09 用户口径·声望改触发】**声望词条**：关键字（供"声望角色"计数）＋ 触发效果
    ///      （该地区所有对抗结束后，声望角色较多的一方放置 1 个势力标志；不依赖对抗胜负）。
    ///      口径（用户已确认的默认值）：**不要求未横置**、不要求参与该次对抗，只数该地区的声望角色数量。
    /// </summary>
    public static class JC076_wudaosengren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC076_wudaosengren", Build);

        private static void Build(PlayerCardData data)
        {
            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = "JC076_A_Renown";
            data.EffectDataList.Add(renown);

            var renownTrigger = KeywordEffects.Renown();
            renownTrigger.EffectId = "JC076_A2_RenownTrigger";
            data.EffectDataList.Add(renownTrigger);
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
