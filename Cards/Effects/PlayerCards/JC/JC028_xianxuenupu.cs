using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC028 鲜血奴仆 效果（角色）。
    /// 卡文：**快速行动，牺牲鲜血奴仆：将鲜血奴仆移回场上，成为一个名为「鲜血」的附属并结附于目标吸血鬼角色。
    ///       鲜血不具有能力图标和防御力，且具有"受此结附的角色获得1防御力 和［黑刀］"的能力。**
    ///
    /// 实现（**P3·K4 实例级形态 + 自我重进场结附**，用户口径 **A28** ＋ 本轮追问答复）：
    ///  · 激活：`CharacterActivated` + `ActivateWindow="快速"`；**牺牲自己**＝代价（`SacrificeCost{ExcludeSource=false}`，
    ///    卡文未写"另一个"）→ **入堆叠前支付**；**场上没有符合条件的目标角色时不能发动**
    ///    （`ActivateEligibility.HasLegalTarget` 按目标规则判定为"无合法目标"）；
    ///  · 目标＝**吸血鬼角色**（`CurrentSubCategory="吸血鬼"`；**任意地区**、不限本地区）；
    ///  · **结算**：目标不在场 → 由 D16 目标丢失判据**整条不结算**（"结算时目标不在场则不回到场上"，卡留在墓地）；
    ///    目标在场 → 动作链：
    ///      ① `TransformForm{"自身","常驻"}`：实例级形态——**改卡名「鲜血」**、**类别改为附属（不保留角色类别）**、
    ///         **损失原有（印刷）效果**、**印刷图标与防御清零**，并挂上形态自己的效果表
    ///         （＝"受此结附的角色获得1防御力 和［黑刀］"的持续效果，落点＝宿主）；
    ///      ② `Attach{"目标"}`：**同一张卡重新进场**并以附属身份结附到该目标上
    ///         （它只作为附属、不作为角色，故不涉及"地区选择"）。
    ///  · 口径要点（用户答复）：这张卡以附属进场、且**已损失原有印刷效果**，故"与它自身的进场触发无关"；
    ///    回到场上**只作为附属**；牺牲＝死亡 → 照常广播死亡时机。
    /// </summary>
    public static class JC028_xianxuenupu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC028_xianxuenupu", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC028_A_ReturnAsBloodAttachment",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",                  // 卡文"快速行动"
                // 代价＝**牺牲本卡自身**（卡文点名"牺牲鲜血奴仆"）→ 走既有"牺牲本角色"代价通道，
                // 代价在**入堆叠之前**支付（牺牲＝死亡，照常触发死亡时机），随后效果从墓地把它取回场上。
                ActivateCondition = "牺牲本角色",
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                // 目标＝吸血鬼角色（任意地区）
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.CurrentSubCategory },
                    Param = "吸血鬼",
                },
                // 【P3·K4】实例级形态：改名为「鲜血」、类别改附属、印刷效果失活、印刷图标与防御清零
                FormChange = new FormChangeSpec
                {
                    Category = FormCategory.Attachment,
                    Name = "鲜血",
                    SuppressPrintedEffects = true,
                    ZeroPrintedStatistics = true,
                    // 形态自己的效果表：只有"鲜血"的效果（受此结附的角色获得 1 防御力 和［黑刀］）
                    Effects = new Array<EffectData>
                    {
                        new EffectData
                        {
                            EffectId = "JC028_FormA_GrantHostDefenseAndBlade",
                            Timing = EffectTiming.Continuous,
                            Scope = ZoneScope.AttachTarget,
                            ContinuousPrereq = "常驻",
                            ContinuousEffect = "受此结附的角色获得 1 防御力 和［黑刀］",
                            GrantToHost = true,
                            ContinuousGrants = new Array<EffectGrant>
                            {
                                new EffectGrant { Kind = "Attribute", Value = "防御", Amount = 1 },
                                new EffectGrant { Kind = "Icon", Value = "黑刀", Amount = 1 },
                                new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                            },
                            Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                            Duration = "常驻",
                            FaqNote = "宿主获得 1 防御力与［黑刀］（图标 + 数值两条通道都写）。",
                        },
                    },
                },
                Actions = new Array<EffectAction>
                {
                    // ① 变成「鲜血」形态（常驻）
                    new EffectAction
                    {
                        Verb = ActionVerb.TransformForm,
                        Args = new Array<string> { "自身", "常驻" },
                    },
                    // ② 同一张卡重新进场：以附属身份结附到目标吸血鬼角色上
                    new EffectAction { Verb = ActionVerb.Attach, Args = new Array<string> { "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动，牺牲自身：从墓地**以附属身份重新进场**结附到目标吸血鬼角色上——改名为「鲜血」、"
                        + "类别改为附属（不保留角色）、不具有能力图标与防御力，只保留『受此结附的角色获得 1 防御力与［黑刀］』；"
                        + "场上没有符合条件的角色时不能发动；结算时目标已不在场则不回到场上。",
            });
        }
    }
}
