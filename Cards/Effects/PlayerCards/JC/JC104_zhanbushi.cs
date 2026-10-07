using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC104 占卜师 效果（角色）。
    /// 卡文：进场触发：预测3。（检视你牌库顶的三张牌，将其中任意数量的牌以任意顺序至于你的牌库顶，
    ///      将其余的牌以任意顺序置于你的牌库底。）
    /// 实现：`EnterTrigger` + `ActionVerb.Predict`（Args[0]=3）；
    /// 复用调查对抗的取/放通道与分配窗口，联机走 `ChoiceRequest.Kind.Prediction`（见 `PredictionRule`）。
    /// 【2026-09 整体复查·修复】**非强制**：卡文只写"进场触发：预测3。"，**没有"强制触发"**字样，
    /// 故按项目既定口径 `Forced = false`（与 JC056「不是强制触发」同款：只有卡文印"强制触发"的才强制；
    /// 旧实现以"无『可以』"当作强制的理由与全局口径相反）⇒ 触发在排序窗口里可放弃。
    /// </summary>
    public static class JC104_zhanbushi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC104_zhanbushi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC104_1_Predict3",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Predict, Args = new Array<string> { "3" } }
                },
                Forced = false,   // 【2026-09 复查】卡文无"强制触发"⇒ 非强制（与 JC056 同口径）
                Duration = "瞬时",
                FaqNote = "检视牌库顶 3 张：任意数量（含 0）置于牌库顶（可排序），其余置于牌库底；不改变手牌。",
            });
        }
    }
}
