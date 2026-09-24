using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC130 深入研究 效果（事务卡）。
    /// 卡文：行动：抓两张牌，然后弃一张手牌。
    /// 实现：`LimitToActionPhaseOnly=true`（B 档门禁：只能行动阶段打出，不分先后手）+
    ///      `Draw{"2"}`（唯一抽牌收口，会广播 `OnDraw`）+ P2 新增 `Discard{"1","自己","自选"}`（唯一弃牌出口，广播 `OnDiscard`）。
    /// 注：弃牌由**弃牌者自己**选（选项语义见 §7.4 第 1 条）；无 UI 时走确定性兜底（弃第一张）。
    /// </summary>
    public static class JC130_shenruyanjiu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC130_shenruyanjiu", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;   // 行动：只能行动阶段打出

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC130_1_DrawTwoDiscardOne",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "2" } },
                    new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "自己", "自选" } },
                },
                Duration = "瞬时",
                FaqNote = "抓两张后弃一张手牌（自己选；抓牌会触发抓牌时机，弃牌会触发弃牌时机）。",
            });
        }
    }
}
