using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC049 深渊细语 效果（事务卡）。
    /// 卡文：额外费用：牺牲一个角色。 快速行动：将你牌库底的两张牌置于手中。
    ///
    /// ① **额外费用：牺牲一个角色**（【K10】2026-09 交付，口径 **A21**）：
    ///  · 声明 `PlayerCardData.ExtraCost = { Kind = SacrificeCharacter, Mandatory = true }`
    ///    → `TurnManager.PlayCard` 在**付费阶段、入堆叠之前**支付（**不在堆叠里结算、不可被响应**，FAQ 原文）；
    ///  · **必须支付**：**无合法牺牲对象则整张不能打出**；牺牲者由**打出者自己选择**
    ///    （候选＝本方场上正面角色；口径见 `SacrificeCostSpec`，与 P3·K22 同一实现）；
    ///  · 牺牲＝**死亡**，照常广播死亡时机。
    ///
    /// ② 快速行动：将你牌库底的两张牌置于手中（本批之前已交付）：
    ///  · 动词 `DrawFromBottom{2, "不计抓牌"}`：从**牌库底**取 2 张加入手牌；
    ///  · **关键口径**：卡文写"置于手中"、FAQ 明确"**并不算抓牌**" → 该取牌路径**不广播 `OnDraw`**
    ///    （"每当你抓牌时…"类效果不响应），且**空库不判负**（判负规则绑定"抓牌"这一行为）；
    ///    `OnCardDrawn` 仍照常通知（那是 View 把 CardUI 从牌库移出的界面通知，与触发时机无关）。
    /// </summary>
    public static class JC049_shenyuanxiyu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC049_shenyuanxiyu", Build);

        private static void Build(PlayerCardData data)
        {
            // 【K10·额外费用】"牺牲一个角色"＝打出这张牌的费用之一（**必须支付**；无合法牺牲对象则不能打出）
            data.ExtraCost = new ExtraCostSpec
            {
                Kind = ExtraCostKind.SacrificeCharacter,
                Mandatory = true,
                Sacrifice = new SacrificeCostSpec(),   // 缺省：本方场上正面角色（不可暗藏者；未写"另一个"→ 不限自身）
            };
            data.ExtraCostDescription = "牺牲一个角色";
            data.IsExtraCostMandatory = true;

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC049_A_TakeBottomTwoToHand",
                Timing = EffectTiming.OnSorceryCast,     // 事务卡"打出即结算"
                PlayTiming = PlayTiming.MainPhase,       // 卡文"快速行动"
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.DrawFromBottom,
                        // Args[0]＝数量；附带 `"不计抓牌"` 标记 → 不广播 OnDraw（FAQ："并不算抓牌"）
                        Args = new Array<string> { "2", "不计抓牌" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "从你牌库底取两张牌加入手牌；**这是取牌、不是抓牌**——不触发任何『每当你抓牌时…』"
                        + "类效果；牌库不足两张时取到多少算多少（不因空库判负）。",
            });
        }
    }
}
