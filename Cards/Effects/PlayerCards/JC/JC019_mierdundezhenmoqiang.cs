using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC019 弥尔顿的镇魔枪 效果（附属 / 装备·武器，结附于目标角色）。
    /// 卡文：结附于目标角色 持续：受此结附的角色获得［黑刀］，并获得"快速行动，横置：此角色对位于本地区的
    ///      目标非人类角色造成1点伤害，该伤害不能被防止"的能力。
    /// FAQ：若要使用该能力，需要横置**其所结附的角色**，而非镇魔枪本身。
    ///
    /// 实现（**K13 运行期授予激活能力**）：
    ///  · 打出目标＝角色（"结附于目标角色"）；
    ///  · 持续（宿主获得黑刀）：`GrantToHost = true` + `Icon:黑刀 ×1`（既有通道）；
    ///  · **运行期授予激活能力**：同一效果的 `GrantedAbility` 声明那条"快速行动，横置：…"的能力
    ///    （`EffectEngine.ReapplyGrantedAbilities` 统一重算）——该能力以**宿主**为来源，故
    ///    `ActivateCondition = "横置本角色"` 横置的是**宿主**（与 FAQ 一致）；
    ///  · 目标＝**本地区**（宿主所在地区）的**非人类角色**；动作＝`DealDamage{"1","不可防止"}`；
    ///  · 用户 2026-09 口径：宿主原本就有激活效果时，在**既有的多激活效果选择窗口**里额外增列 1 项
    ///    （`ActivateEligibility.GetAllActivateAbilities` 已把运行期授予项并入候选）。
    /// </summary>
    public static class JC019_mierdundezhenmoqiang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC019_mierdundezhenmoqiang", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝角色（"结附于目标角色"）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC019_1_GrantBlackBladeAndAbility",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,          // 附属：结附于角色时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "受此结附的角色获得［黑刀］，并获得『快速行动，横置：对位于本地区的目标非人类角色造成1点伤害（不能被防止）』的能力",
                GrantToHost = true,                      // 授予落点＝宿主（结附的角色）
                ContinuousGrants = new Array<EffectGrant>
                {
                    // 两条通道都写：`Icon` 负责"图标存在性/展示"（`HasIcon`，持续授予自愈判定也用它），
                    // `Attribute:战斗` 负责**数值**（黑刀=+1 战力；`Icon` 通道本身不改战力）。
                    new EffectGrant { Kind = "Icon", Value = "黑刀", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                },
                // 【K13】运行期授予的**激活能力**（以宿主为来源；横置的是宿主，见 FAQ）
                GrantedAbility = new EffectData
                {
                    EffectId = "JC019_G_HostActivatedSmite",
                    Timing = EffectTiming.CharacterActivated,
                    ActivateWindow = "快速",              // 卡文"快速行动"
                    ActivateCondition = "横置本角色",      // 横置**宿主**（FAQ）
                    Forced = false,
                    Condition = new EffectCondition { Predicate = "Always" },
                    Target = new TargetSpec
                    {
                        Kind = TargetKind.Character,
                        NeedsSelection = true,
                        IsLocalZone = true,               // "位于本地区"
                        Restrictions = new Array<TargetRestriction> { TargetRestriction.NotCurrentSubCategory },
                        Param = "人类",                    // "非人类角色"（实例当前类别；用户 2026-09 口径）
                    },
                    Actions = new Array<EffectAction>
                    {
                        // Args[1] = "不可防止" → 跳过"防止"（灵体/护盾），直接落地
                        new EffectAction
                        {
                            Verb = ActionVerb.DealDamage,
                            Args = new Array<string> { "1", "不可防止" },
                        },
                    },
                    Duration = "瞬时",
                    FaqNote = "快速行动，横置受此结附的角色：对本地区一个非人类角色造成 1 点伤害（该伤害不能被防止）。",
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间：宿主获得［黑刀］，并获得一条『快速行动，横置：造成 1 点不可防止伤害』的能力；"
                        + "武器离场或解附即收回。",
            });
        }
    }
}
