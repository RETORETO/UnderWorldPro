using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC064 戒严 效果（附属 / 结附于目标地区，子类别「局势」）。
    /// 卡文：结附于目标地区 **准备步骤强制触发：支付1，否则牺牲戒严。**
    ///       **持续：所有非【国家机构】的角色牌不可正面打出在本地区，也不能移动进入本地区。**
    /// FAQ： ◆结附有戒严的地区，非国家机构角色**仍然能够以秘密派遣方式进入**，也**可以用其他方式放置进场**
    ///        （比如操控死尸）。 ◆带有混乱关键词的角色无法随机进入受戒严结附的地区。
    ///
    /// 实现（**K21 按组织的进入限制 + "支付否则牺牲"变体**，用户口径 **A19**）：
    ///  · ① 持续（`StaticKind.BlockNonOrgEntryInArea`，`Param="国家机构"`）：来源结附地区的**进入限制**——
    ///    **非【国家机构】的角色牌**：**不可正面打出在该地区**（消费点 `TurnManager.PlayCard`：本次打出的目标地区）、
    ///    **也不能移动进入该地区**（消费点 `CardManager.MoveCardToArea`，机动/效果移动/置换的唯一收口）；
    ///    FAQ 两条**放行**路径自然成立：**秘密派遣**（走 `ZoneManager.MoveToArea`，不经上述收口）与
    ///    **其他方式放置进场**（从墓地/手牌/牌库进场不算"移动进入"，判定要求该卡**此刻已在场上**）。
    ///  · ② 准备步骤**强制触发**：动词 `PayOrSacrificeSelf{1}` —— **由玩家选择**支付或牺牲：
    ///    可支付 → 弹 0/1 窗口（"支付 1" / "牺牲"）；**费用不足 → 不询问、直接牺牲**（卡文"否则"）；
    ///    无 UI → 兜底"能付就付"。牺牲＝死亡，照常触发死亡时机。
    /// 注：FAQ 第二条（混乱关键词的角色无法随机进入）属**混乱规则文本未定义**（用户已明确暂不做）。
    /// </summary>
    public static class JC064_jieyan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC064_jieyan", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝地区（"结附于目标地区"）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            // ① 持续：非【国家机构】角色牌不可正面打出/移动进入本地区
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC064_A_BlockNonOrgEntry",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,          // 附属：结附于地区时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "所有非【国家机构】的角色牌不可正面打出在本地区，也不能移动进入本地区",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.BlockNonOrgEntryInArea, Param = "国家机构" },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本地区：非【国家机构】的角色牌不能正面打出、也不能移动进入（含机动/效果移动/置换）；"
                        + "秘密派遣与『其他方式放置进场』（如操控死尸）不受此限。",
            });

            // ② 准备步骤强制触发：支付1，否则牺牲戒严（由玩家选择）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC064_B_UpkeepPayOrSacrifice",
                Timing = EffectTiming.OnTurnStarted,     // 准备步骤
                Forced = true,                           // 卡文"强制触发"
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PayOrSacrificeSelf, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "准备步骤：支付 1，否则牺牲戒严（由控制者选择；费用不足时直接牺牲）。",
            });
        }
    }
}
