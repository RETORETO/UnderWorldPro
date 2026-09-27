using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC128 替罪羊 效果（事务卡；`Cost = ?`，子类别「策略」）。
    /// 卡文：行动：选择目标敌方玩家，除非他选择并移除三个他所拥有的势力标志，否则该玩家牺牲一个角色。
    /// 实现（**用户口径 A12**）：新动词 `BribeOrSacrifice{3, "角色"}`：
    ///  · **目标敌方玩家**＝K24 玩家目标（`Kind=Player` + `Restrictions=Enemy`，入堆叠时锁定）；
    ///  · 该玩家势力标志**不足 3 个 → 必须牺牲**（不询问）；
    ///  · 标志足够 → 询问他：**确认＝移除 3 个标志**（复用 `CurbRule`：由他按地区分配移除自己拥有的标记）；
    ///    **取消＝走牺牲路线**（他自选一个角色牺牲）；
    ///  · **没有角色时也可以选牺牲路线 → 什么都不付出**（无候选即跳过）。
    /// </summary>
    public static class JC128_tizuiyang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC128_tizuiyang", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;      // 卡文"行动"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC128_A_BribeOrSacrifice",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.BribeOrSacrifice,
                        Args = new Array<string> { "3", "角色" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "目标对手选择：移除自己 3 个势力标志，或牺牲一个自己的角色；标志不足 3 个时必须牺牲"
                        + "（没有角色时选牺牲路线＝什么都不付出）。",
            });
        }
    }
}
