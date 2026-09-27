using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC036 初拥 效果（附属 / 状态，结附于目标**印刷类别为人类**的角色）。
    /// 卡文：结附于目标印刷类别为人类的角色 持续：**你获得受此结附角色的控制权，将该角色的人类类别改为吸血鬼类别**。
    /// FAQ： ◆当对手初拥结附在一个你的角色后，将该角色交给对手操控，在初拥被消灭后，该角色的操控权交还给其拥有者。
    ///      ◆当被初拥结附的角色因其他卡牌能力而翻面为暗藏者后，初拥会被消灭，该暗藏者的操控权交还给其拥有者。
    ///
    /// 实现（**K4：持续夺控 + 子类别改写**，用户 2026-09 §7.4b A16 口径）：
    ///  · 打出目标＝角色 + `StaticSubCategory`「人类」（**印刷类别**＝模型数据；卡文原文如此，
    ///    与 JC091「目标人类角色」读实例类别相反——见 `TargetRestriction` 注释）；
    ///  · **持续夺控**：`EffectData.ControlGain = AttachedHost` → 登记时经 `ZoneManager.ChangeController`
    ///    把宿主交给本卡控制者（只改 controller、物主不变、**卡不移动地区**），注销（离场/解附）时
    ///    **归还给拥有者**（FAQ 原文）；**不设"版本号"判据**——初拥离场或被初拥角色离场都会让持续效果自然注销；
    ///  · **子类别改写**：`ContinuousGrants` 落点＝宿主（`GrantToHost`）——
    ///    `SubCategoryRemove`「人类」把**模板固有**的"人类"从**实例当前类别**里抹掉，
    ///    `SubCategory`「吸血鬼」追加"吸血鬼"；撤销时按来源计数归零还原（模板固有者补回）。
    ///    注：**不改模型数据**（`HasStaticSubCategory` 仍为人类）——用户口径：被初拥改写的角色对
    ///    "人类角色"类效果（JC091）**不再合法**，但对"**印刷类别**为人类"类效果**仍然合法**；
    ///  · **宿主翻面为暗藏者 ⇒ 初拥被消灭**：走**通用规则** `AttachmentRule.EnforceOnCovertChange`
    ///    （装配层订阅翻面状态中枢），随后本卡的持续效果注销 → 控制权自动归还拥有者（即 FAQ 第二条）。
    /// </summary>
    public static class JC036_chuyong_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC036_chuyong", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝角色 + **印刷类别**为人类（读模型数据）
            data.TargetRules = new Array<TargetRule>
            {
                new TargetRule
                {
                    Kind = TargetKind.Character,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.StaticSubCategory },
                    Param = "人类",
                },
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC036_A_EmbraceHost",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,          // 附属：结附于角色时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "你获得受此结附角色的控制权，并将该角色的人类类别改为吸血鬼类别",
                // 【K4】持续夺控：宿主 → 本卡控制者；注销时归还拥有者
                ControlGain = new ControlGainSpec { Victim = ControlGainVictim.AttachedHost },
                // 授予落点＝宿主（"该角色"）
                GrantToHost = true,
                ContinuousGrants = new Array<EffectGrant>
                {
                    // "改为"＝**移除侧**（把实例当前类别里的"人类"抹掉；模板数据不变）+ 追加"吸血鬼"
                    new EffectGrant { Kind = "SubCategoryRemove", Value = "人类", Amount = 1 },
                    new EffectGrant { Kind = "SubCategory", Value = "吸血鬼", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间：宿主由本卡控制者操控（**不因回合结束消失**；初拥离场/被消灭即归还**拥有者**），"
                        + "且其类别由「人类」**改写**为「吸血鬼」（印刷类别＝模型数据不变，故"
                        + "『印刷类别为人类』类判定仍成立，而『人类角色』类判定不再成立）。"
                        + "宿主翻面为暗藏者时，初拥按通用规则被消灭，操控权归还拥有者（FAQ 第二条）。",
            });
        }
    }
}
