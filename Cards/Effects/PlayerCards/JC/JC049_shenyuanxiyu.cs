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
    /// 本批交付**第二条**（"非抓牌式取牌"）：
    ///  · 动词 `DrawFromBottom{2, "不计抓牌"}`：从**牌库底**取 2 张加入手牌；
    ///  · **关键口径**：卡文写"置于手中"、FAQ 明确"**并不算抓牌**" → 该取牌路径**不广播 `OnDraw`**
    ///    （"每当你抓牌时…"类效果不响应），且**空库不判负**（判负规则绑定"抓牌"这一行为）；
    ///    `OnCardDrawn` 仍照常通知（那是 View 把 CardUI 从牌库移出的界面通知，与触发时机无关）。
    ///  · 数据通路：`EffectData.Action` → `EffectEngine` 的 `ActionVerb.DrawFromBottom` 分支
    ///    （`HasArg(action, "不计抓牌")` → `TurnManager.DrawFromBottomForPlayer(p, n, countAsDraw:false)`）。
    ///
    /// **第一条（额外费用：牺牲一个角色）仍未实现**：属 `K10 额外费用`机制（打出前插入"额外费用"步骤），
    /// 该机制整组（JC049/JC081/JC135）用户尚未排期 → 本卡按"效果级推进"纪律只接入第二条。
    /// </summary>
    public static class JC049_shenyuanxiyu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC049_shenyuanxiyu", Build);

        private static void Build(PlayerCardData data)
        {
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
