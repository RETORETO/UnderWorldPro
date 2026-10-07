using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC063 警车追捕 效果（事务卡）。
    /// 卡文：快速行动：选择一项： ·将目标角色潜伏。（将该角色翻面为暗藏者） ·将目标暗藏者移回其拥有者手上。
    /// FAQ：①「在打出警车追捕并为其付费时，你必须选择其中一项能力，然后警车追捕进入堆叠，你不能再更改选项。」
    ///      → 与用户 2026-09 口径一致：**模式在打出时选定、付费之前，入堆叠后不可更改**（`EffectData.Modes` + `StackEntry.ModeIndex`）。
    ///     ②「选项二指定正在结算中的翻面牌不合法」／「对手响应付费翻面 → 警车追捕因目标不合法而失效」
    ///      → 由既有 D16 机制覆盖：翻面会 `BumpVersion`，结算时版本号不一致 → 该模式目标丢失 → 整张不结算进墓地。
    ///
    /// 实现（K16 模式分支）：
    ///  · 模式1「将目标角色潜伏」：目标 `Kind=Character`，动作 `Latent`（翻面为暗藏者）；
    ///  · 模式2「将目标暗藏者移回其拥有者手上」：目标 `Kind=Covert`，动作 `Bounce`；
    ///  · 卡级 `TargetRules` 取两大类的 **OR**（Character/Covert）——只用于打出阶段的"需要目标/可高亮"判定，
    ///    真正的模式内筛选由各模式自己的 `Target` 声明负责。
    /// </summary>
    public static class JC063_jingchezhuibu_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC063_jingchezhuibu", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出阶段：允许 角色 或 暗藏者（两模式各自再精筛）
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
                TargetRule.Make(TargetKind.Covert),
            };
            // 【2026-09 修复·用户实测"JC063 门禁错误"】**快速行动 = C 档（可响应打出），不限行动阶段**：
            // 与 JC062/JC065 同一口径（卡文含"快速行动"的效果 `PlayTiming = Response`）。
            // 此前误写成 `LimitToActionPhaseOnly = true` + `PlayTiming = MainPhase` ⇒ 非行动阶段打不出来（门禁错误）。

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC063_1_TapOrBounceByMode",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Condition = new EffectCondition { Predicate = "Always" },
                // 无模式分支时不用（以 Modes 为准）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Modes = new Array<EffectMode>
                {
                    new EffectMode
                    {
                        Label = "将目标角色潜伏（翻面为暗藏者）",
                        Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                        Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Latent } },
                    },
                    new EffectMode
                    {
                        Label = "将目标暗藏者移回其拥有者手上",
                        Target = new TargetSpec { Kind = TargetKind.Covert, NeedsSelection = true },
                        Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Bounce } },
                    },
                },
                Duration = "瞬时",
                FaqNote = "打出并付费时选定模式，入堆叠后不可更改；模式2 指定的暗藏者在结算时若已翻面/离场，则目标不合法 → 整张不结算。",
            });
        }
    }
}
