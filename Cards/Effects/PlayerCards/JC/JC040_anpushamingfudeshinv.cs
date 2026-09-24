using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC040 安普莎，冥府的侍女 效果（角色）。
    /// 卡文：触发：每当敌方玩家弃牌时，遏制X。X等于此次弃牌的数量。
    ///
    /// 实现（P3）：
    ///  · 触发：`Timing=OnDiscard` + 条件 `DiscardByEnemy`（弃牌者≠来源控制者）；
    ///  · **动态值 X（K1）**：`ValueExpr="事件数量"`＝`TriggerContext.ExtraInt`（`TurnManager.DiscardCards` 的本次弃牌总张数），
    ///    经 `StackEffectInstance.EventAmount → StackEntry.EventAmount → ResolveEffect(eventAmount)` 透传；
    ///    动作参数写 `$X`（由引擎替换为求值结果）；
    ///  · **X=0 时该效果不算发动**（用户口径）：收集阶段即求值，0 → 不收集（不发动）；
    ///  · 遏制：`ActionVerb.Curb`（被遏制者默认＝施术者的对手，即弃牌的那位对手）。
    /// </summary>
    public static class JC040_anpushamingfudeshinv_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC040_anpushamingfudeshinv", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC040_1_CurbByDiscardCount",
                Timing = EffectTiming.OnDiscard,
                Condition = new EffectCondition { Predicate = "DiscardByEnemy" },
                ValueExpr = "事件数量",              // 【K1】X ＝ 此次弃牌的数量
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Curb,
                        // [X, 被遏制者]：X 用占位符，结算时替换为本次弃牌数量；被遏制者缺省＝施术者对手（＝弃牌者）
                        Args = new Array<string> { "$X", "对手" },
                    },
                },
                Forced = false,
                Duration = "瞬时",
                FaqNote = "每当敌方玩家弃牌时，按其本次弃牌张数遏制（X 张）。X=0 时该效果不发动。",
            });
        }
    }
}
