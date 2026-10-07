using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC122 哈克·亚当斯 效果（角色）。
    /// 卡文：快速行动2：在目标角色或暗藏者上放置一个锁定标记。
    ///      持续：只要哈克·亚当斯在场，所有具有锁定标记的角色或暗藏者不能翻面。
    ///
    /// 实现：
    ///  · ①**放置锁定标记**：角色激活（快速 + 费用2 + `PlaceCounter`，`IncludeCovert=true`）；
    ///  · ②**「具有锁定标记的暗藏者不能翻面」**（**用户 2026-09 口径**：只需限制**现身**行为、
    ///    且只对**带锁定标记的暗藏者**生效）→ `StaticKind.NoRevealWithLockCounter` 静止规则：
    ///    前提满足时登记来源，消费点＝`TurnManager.RevealCovert`（现身唯一玩家入口）经
    ///    `EffectEngine.IsRevealBlocked(card)` 判定（卡带 `锁定标记`/`锁定标志` 且来源在场 → 拒绝现身）；
    ///    "潜伏/翻面为暗藏者"方向不封禁。
    /// </summary>
    public static class JC122_hakeyadangsi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC122_hakeyadangsi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC122_1_PlaceLockMarker",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    // 【2026-09 整体复查·修复】卡文"目标角色或暗藏者"**未排除自身** ⇒ 按 JC004 既定口径允许取自身
                    CanTargetSelf = true,
                    IncludeCovert = true,   // 卡文"目标角色或暗藏者"
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "锁定标记", "1", "目标" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "快速行动2：在目标角色或暗藏者上放置一个锁定标记。",
            });

            // ② 持续：只要本卡在场，带锁定标记的暗藏者不能【现身】（静止规则；离场自动注销）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC122_2_NoRevealForLockedCovert",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",              // 卡文"只要哈克·亚当斯在场"
                ContinuousEffect = "所有具有锁定标记的暗藏者不能现身",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.NoRevealWithLockCounter, Param = "锁定标记,锁定标志" },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本卡在场期间，场上带锁定标记/锁定标志的暗藏者不能现身（只封现身，不影响潜伏/翻面为暗藏者）。",
            });
        }
    }
}
