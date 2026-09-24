using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC041 禁婆 效果（角色，暗藏者系）。
    /// 卡文：快速暗藏者行动2：令禁婆由暗藏者状态现身。你只能在其位于水域地区时发动此能力。
    ///      现身强制触发：每位玩家各弃一张手牌。
    ///
    /// 本轮交付**第二条**（"现身强制触发：每位玩家各弃一张手牌"）：
    ///  · 时机 ＝ `EffectTiming.UncoverTrigger`（暗藏→公开时由 `CardManager.RevealCard` 广播）；
    ///  · **强制** ＝ `Forced = true`（触发排序窗不可放弃；也不需要目标选择）；
    ///  · **每位玩家各弃一张** ＝ `Discard{1, "双方", "自选"}`：`ResolvePlayerSide("双方")` 给出双方玩家，
    ///    每人**各自自选**弃一张（弃牌原语与 OnDiscard 批次口径已具备）。
    ///    注：由 `TurnManager`/`EffectEngine` 落地，**每批仍只广播一次 OnDiscard**（用户 2026-09 口径）。
    /// **第一条（快速暗藏者行动2：令其现身 + 仅限水域地区）仍未实现**：
    ///  需要"令来源由暗藏者状态现身"的**效果化动词**（现"现身"是玩家动作）与**地区子类别前提**（K21 位置/区域条件）。
    /// </summary>
    public static class JC041_jinpo_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC041_jinpo", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC041_1_EachPlayerDiscardsOne",
                Timing = EffectTiming.UncoverTrigger,
                Forced = true,                       // "现身**强制**触发"
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // 弃牌者＝双方；选择方式＝自选（**每位玩家自己**决定弃哪张）
                    new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "双方", "自选" } },
                },
                Duration = "瞬时",
                FaqNote = "现身时强制触发：双方各弃一张手牌（各自自选）。",
            });
        }
    }
}
