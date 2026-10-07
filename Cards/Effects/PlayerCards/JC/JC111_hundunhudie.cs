using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC111 混沌蝴蝶 效果（角色，灵体，梦境行者，昆虫）。
    /// 卡文：灵体 不能被装备结附 快速行动1：检视目标玩家牌库顶的一张牌。 快速行动，横置：将目标玩家的牌库洗牌。
    ///
    /// 实现（K24 玩家目标 + P3·H 检视 + P2 洗牌）：
    ///  · **灵体 / 不能被装备结附**：`KeywordEffects.Spirit()` / `KeywordEffects.CannotBeAttached()`（与 JC112 同款）；
    ///  · 两条**激活能力**（`CharacterActivated`，窗口=快速）——均以**玩家**为目标：
    ///    ① `Cost = 1`（卡文"快速行动**1**"）+ `Target{Kind=Player, Restrictions=[Enemy]}`（"目标玩家"＝对手）
    ///       → `Inspect{"目标玩家牌库顶"}`（只读不取出：检视不是移动）；
    ///    ② `ActivateCondition = "横置本角色"`（横置为代价）
    ///       → `Shuffle{"目标玩家"}`（洗**目标玩家**的牌库）。
    ///  · 玩家目标的**独立选择窗口**与目标锁定见 `PlayerTargetPool` / `Arena.PromptPlayerTarget`；
    ///    动作里的 `"目标玩家"` 由 `EffectEngine.ResolvePlayerSide` 解析为本次锁定的目标玩家号。
    /// </summary>
    public static class JC111_hundunhudie_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC111_hundunhudie", Build);

        private static void Build(PlayerCardData data)
        {
            // 灵体（关键词）：与 JC010/JC112 同款"持续关键字授予"（无前提=常驻）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC111_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            // 不能被装备结附（JC001-A 同款消费点）
            var cannotAttach = KeywordEffects.CannotBeAttached();
            cannotAttach.EffectId = "JC111_B_CannotBeAttached";
            data.EffectDataList.Add(cannotAttach);

            // ① 快速行动1：检视目标玩家牌库顶的一张牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC111_1_InspectTopOfTargetPlayer",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 1,                                  // 卡文"快速行动1"
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    NeedsSelection = true,
                    // 【2026-09 用户裁定】**"目标玩家"＝任意玩家（含自己）** ⇒ **不加** `Restrictions`
                    // （空＝任意；`Enemy`＝对手 / `Friendly`＝本方）。原先写 `Enemy` 属收窄，已按裁定移除。
                },
                Actions = new Array<EffectAction>
                {
                    // 只读牌库顶：`PeekDeckOf` 不取出，检视也不改变任何状态。
                    new EffectAction { Verb = ActionVerb.Inspect, Args = new Array<string> { "目标玩家牌库顶" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动1：检视目标玩家（对手）牌库顶的一张牌；私密查看，牌库不变。",
            });

            // ② 快速行动，横置：将目标玩家的牌库洗牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC111_2_ShuffleTargetPlayerDeck",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",             // 横置为代价（引擎侧在入堆叠前付出）
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    NeedsSelection = true,
                    // 【2026-09 用户裁定】**"目标玩家"＝任意玩家（含自己）** ⇒ **不加** `Restrictions`
                    // （空＝任意；`Enemy`＝对手 / `Friendly`＝本方）。原先写 `Enemy` 属收窄，已按裁定移除。
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Shuffle, Args = new Array<string> { "目标玩家" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动，横置自身：将目标玩家（对手）的牌库洗牌。",
            });
        }
    }
}
