using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC021 狭路相逢 效果（事务卡）。
    /// 卡文：只能在行动阶段打出。快速行动：选择目标本方角色和/或目标敌方角色，将这些角色移动到目标地区。
    /// FAQ：① 可用它把对方指定角色的位置要求破坏掉（本方实现天然满足——移动由本方在响应窗口打出时执行）；
    ///      ② **不能原地移动**：必须进入"另一个地区"，不能选择该角色正位于的那个地区。
    ///
    /// 实现（P3·K15 多目标 + 确认面板）：
    ///  · **多目标槽位**（`EffectData.TargetSlots`，用户 2026-09 口径）：至多 2 个**目标**
    ///    ＝「本方角色」槽 + 「敌方角色」槽，**各自可留空**；`MinChosenSlots = 1`（至少要选 1 个角色）；
    ///  · **目的地＝地区槽**（`Kind = Area`，必选）：其值成为本次结算的"目标地区"；
    ///  · **禁止原地移动**：两个角色槽都标记 `ExcludeInChosenArea` → **已经在目标地区内的角色不出现在候选里**
    ///    （取目标顺序上地区槽先选，卡槽候选按目的地过滤）；
    ///  · **确认面板**（用户 2026-09 口径）：逐槽选完后弹出确认面板，**确认通过才把效果加入堆叠**
    ///    （单机 `IGameUIHooks.PromptMultiTargets`／联机 `ChoiceRequest.Kind.ConfirmTargets`）；
    ///  · 动作 `Move{"目标"}`：把每个被选中的角色移动到地区槽指定的地区（一次结算内逐个执行完）。
    /// </summary>
    public static class JC021_xialuxiangfeng_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC021_xialuxiangfeng", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;   // "只能在行动阶段打出"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC021_1_MoveTargetsToChosenArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                // 【K15】默认单目标语义不参与；以 TargetSlots 为准（NeedsSelection=false 避免旧单目标路径介入）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                TargetSlots = new Array<TargetSlot>
                {
                    // 槽 0：目标地区（必选；它的值＝本次结算的目标地区）
                    new TargetSlot
                    {
                        Label = "目标地区",
                        Optional = false,
                        Spec = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                    },
                    // 槽 1：目标本方角色（可留空；排除已在目标地区内的角色）
                    new TargetSlot
                    {
                        Label = "本方角色",
                        Optional = true,
                        ExcludeInChosenArea = true,
                        Spec = new TargetSpec
                        {
                            Kind = TargetKind.Character,
                            NeedsSelection = true,
                            Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                        },
                    },
                    // 槽 2：目标敌方角色（可留空；排除已在目标地区内的角色）
                    new TargetSlot
                    {
                        Label = "敌方角色",
                        Optional = true,
                        ExcludeInChosenArea = true,
                        Spec = new TargetSpec
                        {
                            Kind = TargetKind.Character,
                            NeedsSelection = true,
                            Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                        },
                    },
                },
                MinChosenSlots = 1,   // 至少选 1 个角色（本方与/或敌方，各自可留空但不可都不选）
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Move, Args = new Array<string> { "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "选目标地区 + 目标本方角色和/或目标敌方角色（至少 1 个角色），把这些角色移动到该地区；不能原地移动（已在该地区的角色不在候选内）。",
            });
        }
    }
}
