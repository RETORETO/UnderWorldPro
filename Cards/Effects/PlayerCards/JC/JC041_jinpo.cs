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
    /// ① 本批交付「**快速暗藏者行动2：令禁婆由暗藏者状态现身**」（用户口径 **A29** 追问答复）：
    ///  · 这是**暗藏者专用**的激活能力——"**区别于其他效果大多正面在场才能生效，这个效果只有作为暗藏者时可以激活**"；
    ///  · 「**水域**」是**地区子类别**（地区卡库已有，例 `Cards/Data/AreaCards/DQ001_chenmodefeixu.tres` → `["遗迹","水域"]`）；
    ///  · 数据：`CharacterActivated` + `ActivateWindow="快速"` + `Cost=2` ＋ **`RequireSourceCovert=true`**
    ///    ＋ **`RequireSourceAreaSubCategory="水域"`**；两个前提在候选侧（`ActivateEligibility`）与权威侧
    ///    （`EffectEngine.ActivateAbility`）**同一实现**判定（`MeetsSourcePrereqs`）；
    ///  · 动作 `Uncover{"自身"}`＝经 `CardManager.RevealCard` 收口**由暗藏者状态现身**（照常广播进场/现身时机）；
    ///  · **UI（A29）**：暗藏者位于水域时**双击不直接现身**，而弹选择框（"正常现身"/"激活暗藏者效果"，**可取消**）
    ///    ——见 `Arena._onCovertRevealForward`。
    ///
    /// ② 「现身强制触发：每位玩家各弃一张手牌」（2026-09 已交付，本批不改动）：
    ///  · 时机 ＝ `EffectTiming.UncoverTrigger`（暗藏→公开时由 `CardManager.RevealCard` 广播）；
    ///  · **强制** ＝ `Forced = true`；**每位玩家各弃一张** ＝ `Discard{1, "双方", "自选"}`
    ///    （`ResolvePlayerSide("双方")` → 双方**各自自选**；每批仍只广播一次 `OnDiscard`）。
    /// </summary>
    public static class JC041_jinpo_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC041_jinpo", Build);

        private static void Build(PlayerCardData data)
        {
            // ① 快速暗藏者行动2：由暗藏者状态现身（**仅暗藏者态 + 仅水域地区**可发动）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC041_A_RevealSelfFromCovertInWater",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",                      // 卡文"快速（暗藏者）行动2"
                Cost = 2,                                    // 费用 2
                Forced = false,
                RequireSourceCovert = true,                  // 只有作为暗藏者时才可以激活（A29）
                RequireSourceAreaSubCategory = "水域",         // 只能在其位于水域地区时发动
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Uncover, Args = new Array<string> { "自身" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动2：本卡**作为暗藏者且位于水域地区**时可发动 → 由暗藏者状态**现身**"
                        + "（照常触发进场/现身两类时机）。",
            });

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
