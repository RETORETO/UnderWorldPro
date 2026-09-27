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
                },
                Actions = new Array<EffectAction>
                {
                    // ① 施加形态（直到回合结束）：实例类别覆盖 + 子类别「地精」
                    new EffectAction
                    {
                        Verb = ActionVerb.TransformForm,
                        Args = new Array<string> { "自身", "直到回合结束" },
                    },
                    // ② 图标与防御（同样到回合结束；图标名经 IconToAttribute 折成数值）
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "白眼", "1", "自身" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑刀", "1", "自身" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑星", "1", "自身" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "1", "自身" } },
                },
                Duration = "直到回合结束",
                FaqNote = "快速行动2：本卡实例类别变为『角色/附属』（仍是附属）并追加『地精』子类别，"
                        + "同时获得［白眼］［黑刀］［黑星］与 1 防御力；**回合结束还原**（类别/子类别/图标/防御一并撤销）。"
                        + "注：只改**实例**，不改印刷类别。",
            });
        }
    }
}
