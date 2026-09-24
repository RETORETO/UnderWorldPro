using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC101 直视深渊 效果（附属 / 结附于目标角色）。
    /// 卡文：结附于目标角色 持续：受此结附的角色，其**印刷文本框视作空白**，且**失去所有魔法领域图标**。
    ///      （不影响能力栏内的能力图标）
    ///
    /// 实现（**K20 收尾：文本空白 + 领域图标抑制**）：
    ///  · 打出目标＝角色（"结附于目标角色"）；
    ///  · `EffectData.DomainSuppression = AttachedHost`：宿主**失去全部魔法领域图标**
    ///    （`CardInstance.SuppressAllDomains`，属性重算也不会复活）→ 与 JC099 共用同一通道；
    ///  · `EffectData.TextBlank = true`：宿主**印刷文本框视作空白 ＝ 失去效果**——
    ///    其印刷效果不再注册持续效果、不可发动、不参与触发收集（`EffectEngine.IsTextBlank` 三处门禁），
    ///    已注册的持续效果会被注销；**已经在堆叠上的条目照常结算**（用户口径："不影响已经发动的效果"）。
    ///    结附解除 / 附属离场 → 统一重算即恢复（重新注册其效果）。
    ///  · 注意：**不影响能力栏内的能力图标**（＝能力图标/属性不动），本卡只做"文本失活 + 领域图标抑制"。
    /// </summary>
    public static class JC101_zhishishenyuan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC101_zhishishenyuan", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝角色（"结附于目标角色"）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC101_1_BlankTextAndSuppressDomains",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,          // 附属：结附于角色时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "受此结附的角色：印刷文本框视作空白（失去效果）且失去所有魔法领域图标",
                DomainSuppression = new DomainSuppressionSpec
                {
                    Victim = DomainSuppressionVictim.AttachedHost,
                },
                TextBlank = true,                        // 宿主"失去效果"（三处门禁 + 注销已注册的持续效果）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间：宿主失去全部魔法领域图标，且其印刷效果**全部失活**（不可发动、不触发、持续效果停止）；"
                        + "已经在堆叠上的效果照常结算；结附解除即恢复。能力栏内的能力图标不受影响。",
            });
        }
    }
}
