using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC044 灾难使者 效果（角色）。
    /// 卡文：时间标志1 行动，自本方卡牌上移除一个时间标志：遏制1。（目标玩家选择并移除一个他所拥有的势力标志）
    ///
    /// 实现：
    ///  · ①「时间标志1」＝词条规则（强制进场触发，给本卡添加 1 个时间标志，`KeywordEffects.Marker`）；
    ///  · ②**「行动，自本方卡牌上移除一个时间标志：遏制1」**（**P3·K5 计数器作发动代价**，口径 §7.4b A17/A27）：
    ///    `CharacterActivated` + `ActivateWindow="行动"` + 代价
    ///    `CounterCost{Markers=["时间标志","时间标记"], Amount=1}`（**由发动者自主选择移除哪张卡上的标志**、
    ///    **入堆叠前支付**、无合法候选→能力不可发动）＋ 效果 `Curb{"1"}`（被遏制者＝缺省对手，经既有 `CurbRule`）。
    /// </summary>
    public static class JC044_zainanshizhe_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC044_zainanshizhe", Build);

        private static void Build(PlayerCardData data)
        {
            var marker = KeywordEffects.Marker("时间标志", 1);
            marker.EffectId = "JC044_A_TimeMarker1";
            data.EffectDataList.Add(marker);

            // ==================== ② 行动：移除一个时间标志（代价）→ 遏制1 ====================
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC044_B_RemoveTimeMarkerToCurb",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",          // 卡文"行动"＝自己的行动步骤
                Cost = 0,                        // "行动"后无数字 → 能力本身无额外费用
                Forced = false,
                // 发动代价：自本方卡牌上移除一个时间标志（由发动者选择哪张卡）
                CounterCost = new CounterCostSpec
                {
                    Markers = new Array<string> { "时间标志", "时间标记" },
                    Amount = 1,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Curb, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "行动：自本方一张卡上移除一个时间标志（发动者选择哪张卡）→ 遏制1"
                        + "（对手选择并移除一个他所拥有的势力标志）。",
            });
        }
    }
}
