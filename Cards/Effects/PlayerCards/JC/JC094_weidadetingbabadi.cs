using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC094 威达的汀巴巴迪 效果（角色 / 人类·法师·罪犯 / 方碑序列）。
    /// 卡文：行动，牺牲本地区的另一个角色：消灭目标角色或暗藏者。
    ///
    /// 实现（**K22 牺牲代价第一批**，用户 2026-09 口径）：
    ///  · 代价＝`SacrificeCostSpec`：`LocalZone = true`（"本地区"）、`ExcludeSource = true`（"**另一个**角色"）、
    ///    `AllowCovert = false`（口径：除非卡文明确写"暗藏者"，否则不能牺牲暗藏者）、子类别不限；
    ///    由**发动者自己选择**被牺牲者（UI 选择窗；无 UI/未指定 → 确定性兜底第一个合法候选）；
    ///  · 代价在**入堆叠之前**支付：`EffectEngine.PaySacrificeCost` → `ResolveDeath(Sacrifice)`
    ///    → **牺牲＝死亡，死亡时机照常触发**；
    ///  · 无合法牺牲候选 / 指定候选不合法 → **拒绝发动且无副作用**（2.6 步前置门禁，先于付费与横置）；
    ///  · 主效果＝`Destroy` 目标"角色或暗藏者"（`IncludeCovert = true`；卡文未限地区 → 不限地区）。
    /// </summary>
    public static class JC094_weidadetingbabadi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC094_weidadetingbabadi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC094_1_SacrificeAllyDestroyTarget",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",                       // 卡文"行动"
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                SacrificeCost = new SacrificeCostSpec
                {
                    LocalZone = true,                          // "本地区的"
                    ExcludeSource = true,                      // "另一个角色"（不含自身）
                    AllowCovert = false,                       // 口径：不能牺牲暗藏者
                },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IncludeCovert = true,                      // "目标角色或暗藏者"
                    // 【2026-09 整体复查·修复】卡文"消灭目标角色或暗藏者"**未排除自身**（也不限地区）⇒
                    // 按 JC004 既定口径应允许取自身（`TargetSpec.CanTargetSelf` 默认 false，漏声明＝静默排除自身）；
                    // 代价侧"另一个角色"的排除由 `SacrificeCost.ExcludeSource` 单独承担，与此无关。
                    CanTargetSelf = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "行动：牺牲本地区另一个角色（自己选、不可选暗藏者，牺牲会触发死亡时机）→ 消灭目标角色或暗藏者。"
                        + "场上没有可牺牲的角色时不可发动。",
            });
        }
    }
}
