using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC089 毒血诅咒 效果（附属 / 诅咒，结附于目标角色）。
    /// 卡文：结附于目标角色 持续：受此结附的角色获得-1防御力和［白刀］［黑刀］，并获得威名。
    ///
    /// 实现（一条持续效果，**授予落点＝结附宿主**）：
    ///  · 打出目标＝目标角色（`TargetRule{Kind=Character}`）；
    ///  · `GrantToHost = true` + `ContinuousGrants`：
    ///      - `Attribute 防御 -1`（**负值授予**：`CardInstance.Grant("Attribute","防御",-1)` 直接改 `CurrentDefense`，
    ///        下限钳到 -1 哨兵；配套修掉了"负值授予在自愈判定里被反复补授"的缺陷）；
    ///      - `Icon 白刀` / `Icon 黑刀`；
    ///      - `Keyword 威名` —— **运行期授予的【威名】现在会真的触发**（K13）：
    ///        该角色在战斗对抗获胜时，`TriggerRegistry` 合成【威名】的触发形态（`OnCombatWon` → 本地区放置一个势力标志）。
    ///  · 附属离场/被消灭 → 持续效果注销，上述授予逐条撤销（防御回退、威名与图标移除）。
    /// </summary>
    public static class JC089_duxuezuzhou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC089_duxuezuzhou", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC089_A_CursedHostGrants",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                GrantToHost = true,          // 授予**结附的角色**
                ContinuousEffect = "受此结附的角色 -1 防御并获得［白刀］［黑刀］与【威名】",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "防御", Amount = -1 },
                    new EffectGrant { Kind = "Icon", Value = "白刀", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "先手战斗", Amount = 1 },
                    new EffectGrant { Kind = "Icon", Value = "黑刀", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                    new EffectGrant { Kind = "Keyword", Value = "威名", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "宿主 -1 防御、获得白刀与黑刀与【威名】（战斗获胜时在本地区放置一个势力标志）。",
            });
        }
    }
}
