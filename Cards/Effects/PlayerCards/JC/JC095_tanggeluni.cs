using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC095 唐·格鲁尼 效果（角色）。
    /// 卡文：领袖 声望 进场触发：将本地区上的所有敌方势力标志移除，然后放置等量的本方势力标志。
    ///
    /// 实现：
    ///  · 前缀词条「领袖」「声望」由引擎级规则消费（`LeaderRule` / `RenownRule`，见 P4 交付）；
    ///  · **进场触发**（本轮补上）：`RemoveToken{势力标志, 全部, 对手, 本地区}` → 把**本地区**上该对手的**全部**势力标志
    ///    直接移除（用户 2026-09 口径：**不走遏制X的"按地区分配"窗口**，因为卡文限定"本地区"）；
    ///    随后 `PlaceToken{势力标志, $X}` 放置**等量**本方势力标志，X 由 `ValueExpr="计数:地区标志:对手:本地区"`
    ///    在**移除之前**求值（＝被移除的数量）。
    ///  · X=0（本地区没有敌方势力标志）→ 按 K1 口径**该效果不发动**（移除 0/放置 0 与不发动等价）。
    /// </summary>
    public static class JC095_tanggeluni_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC095_tanggeluni", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC095_A_Leader";
            data.EffectDataList.Add(leader);

            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = "JC095_B_Renown";
            data.EffectDataList.Add(renown);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC095_1_MoveEnemyMarksToSelf",
                Timing = EffectTiming.EnterTrigger,
                // 【K1】X ＝ 本地区上对手当前的势力标志数（在移除动作之前求值）
                ValueExpr = "计数:地区标志:对手:本地区",
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // 先移除本地区该对手的全部势力标志（直接移除，不由对手分配）
                    new EffectAction
                    {
                        Verb = ActionVerb.RemoveToken,
                        Args = new Array<string> { "势力标志", "全部", "对手", "本地区" },
                    },
                    // 再放置等量（$X）本方势力标志（落点＝来源所在地区＝本地区）
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceToken,
                        Args = new Array<string> { "势力标志", "$X" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "进场时把本地区敌方的全部势力标志移除，并在同地区放置等量本方势力标志；X=0 时该效果不发动。",
            });
        }
    }
}
