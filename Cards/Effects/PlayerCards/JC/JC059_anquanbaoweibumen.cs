using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC059 安全保卫部门 效果（角色）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者）持续：位于本地区的所有其他本方角色获得+1防御力。
    ///
    /// 实现（P3·E）：
    ///  · 【2026-09 整体复查·修复】**「公开」必须由本脚本显式装载**（`KeywordEffects.Public()`）：
    ///    旧注释写的"由引擎级词条提供、本卡无需声明"是**错的**——`PlayerCardData.HasInherentKeyword`
    ///    只读 `EffectDataList` 的 `ContinuousGrants`，**没有任何"按卡面文本自动装载关键字"的机制**
    ///    ⇒ 不声明则 `HasPublicKeyword=false`，"不能被秘密派遣为暗藏者"门禁完全不生效（与 JC061/JC064 同类缺陷）。
    ///  · **持续光环**：`IsAura` + **`AuraLocalZoneOnly=true`**（只影响**本地区**＝与来源同一地区的角色）
    ///    + `ContinuousGrants{Attribute:"防御", 1}`（`CardInstance.Grant` 会把它折进 `CurrentDefense`，
    ///    语义与 `Gain{"防御"}` 一致）。范围＝"其他本方角色"（排除自身；非本方不受影响）。
    /// </summary>
    public static class JC059_anquanbaoweibumen_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC059_anquanbaoweibumen", Build);

        private static void Build(PlayerCardData data)
        {
            var publicKw = KeywordEffects.Public();
            publicKw.EffectId = "JC059_0_Public";
            data.EffectDataList.Add(publicKw);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC059_A_AuraDefenseLocalZone",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                IsAura = true,
                AuraLocalZoneOnly = true,     // 【P3·E】只影响本地区
                ContinuousEffect = "位于本地区的所有其他本方角色获得+1防御力",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "防御", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只有与安全保卫部门同一地区的本方角色（除自身外）获得+1防御力；其它地区不受影响。",
            });
        }
    }
}
