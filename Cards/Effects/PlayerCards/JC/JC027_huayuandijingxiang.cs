using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC027 花园地精像 效果（附属 / 结附于目标地区）。
    /// 卡文：结附于目标地区 **快速行动2：花园地精像成为一个地精角色**，且具有［白眼］［黑刀］［黑星］和 1防御力
    ///       直到回合结束。（**它仍然是附属**）
    ///
    /// 实现（**P3·K4 实例级形态转换**，用户口径 **A15/A32**）：
    ///  · 激活：`AttachmentActivated` + `ActivateWindow="快速"` + `Cost=2`（快速行动2）；
    ///  · **形态**：`FormChange{ Category = CharacterOrAttachment }` → **实例类别覆盖为"角色/附属"组合**
    ///    （"它仍然是附属"），因此**参与"目标角色"类判定与角色统计**；形态**只改实例**、**不改印刷类别**（A32）；
    ///    追加子类别 **「地精」**（用户口径：实例层加"地精"子类别）；
    ///  · 属性授予：`Gain{白眼/黑刀/黑星}` ＋ `Gain{防御,1}`，`Duration="直到回合结束"`（走时效修正自动还原）；
    ///  · **期限**：`TransformForm` 的第二参数＝`直到回合结束` → 登记 `TimedMod{Kind="Form"}`，
    ///    回合结束时**撤销类别覆盖/子类别/属性**（`ExpireTurnEndEffects` 的 Form 分支）。
    /// </summary>
    public static class JC027_huayuandijingxiang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC027_huayuandijingxiang", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 整体复查·修复】卡文"**结附于目标地区**"⇒ 必须声明打出目标大类，
            // 否则附属默认允许 角色/附属/秘社/地区/暗藏者 全部目标（可被结附到角色身上）。
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC027_A_BecomeGoblinCharacter",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",                  // 卡文"快速行动"
                Cost = 2,                                // 卡文"快速行动2"
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                // 【P3·K4】实例级形态：附属→"角色/附属"组合（它仍然是附属），并追加「地精」子类别
                FormChange = new FormChangeSpec
                {
                    Category = FormCategory.CharacterOrAttachment,
                    AddSubCategories = new Array<string> { "地精" },
                    // 【2026-09 用户口径】**形态自带属性＝状态、不叠加**：重复发动仍是 1 白眼/1 黑刀/1 势力/1 防御。
                    // （走 `FormChangeSpec.Grants` 由引擎幂等施加；若写成 `Actions` 里的 `Gain` 会每次累加。）
                    Grants = new Array<EffectGrant>
                    {
                        new EffectGrant { Kind = "Icon", Value = "白眼", Amount = 1 },   // 白眼＝先手调查 +1（Icon 名自动折成属性）
                        new EffectGrant { Kind = "Icon", Value = "黑刀", Amount = 1 },   // 黑刀＝战斗 +1
                        new EffectGrant { Kind = "Icon", Value = "黑星", Amount = 1 },   // 黑星＝势力 +1
                        new EffectGrant { Kind = "Attribute", Value = "防御", Amount = 1 },
                    },
                },
                Actions = new Array<EffectAction>
                {
                    // 形态（类别覆盖 ＋ 子类别 ＋ **形态自带授予** 全在 `FormChange` 里，由引擎幂等施加）
                    new EffectAction
                    {
                        Verb = ActionVerb.TransformForm,
                        Args = new Array<string> { "自身", "直到回合结束" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "快速行动2：本卡实例类别变为『角色/附属』（仍是附属）并追加『地精』子类别，"
                        + "同时获得［白眼］［黑刀］［黑星］与 1 防御力；**回合结束还原**（类别/子类别/图标/防御一并撤销）。"
                        + "注：只改**实例**，不改印刷类别。",
            });
        }
    }
}
