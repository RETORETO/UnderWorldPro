using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC055 安妮斯朵拉 效果（角色）。
    /// 卡文：准备步骤触发：在目标角色或附属上放置一个时间标志。
    ///      行动：自本方卡牌上移除X个时间标志：本回合中，你所打出的下一张灾难牌的所需费用减少X。
    ///
    /// 本批交付**第一条**（全部使用既有原语，无新机制）：
    ///  · 时机 `OnTurnStarted`（＝**准备步骤**，每回合开始广播；与 JC024 的"下一个准备步骤"同一时机常量）；
    ///  · 目标＝**角色或附属**（`KindAlt` 或种类，K14 已交付）；
    ///  · 动作 `PlaceCounter{时间标志, 1, 目标}`（卡上标志放置原语）。
    ///
    /// **第二条（2026-09 交付）**：「行动：自本方卡牌上移除 X 个时间标志：本回合中，你打出的下一张
    /// 灾难牌的所需费用减少 X」——用户口径 **A17/A30**：
    ///  · **移除 X 个时间标志＝发动代价**：`CounterCost = { Markers=["时间标志","时间标记"], AmountByPlayerChoice=true }`
    ///    → **X 由玩家逐卡分配**（"点一次某张卡即记录用 1 点 → 确认"，A30），**X ≥ 1（不允许为 0）**，
    ///    上限＝本方场上该标志总数；代价在**入堆叠之前**支付，无候选/和为 0/候选非法 → 拒绝发动且无副作用；
    ///  · **减费**：`Gain{"费用减少", "$X", "子类别:灾难", "一次性", "打出,现身"}` +
    ///    `ValueExpr="代价数量"`（X＝本次已支付的标志数量，经 `StackEntry.EventAmount` 下发）+
    ///    `Duration="直到回合结束"`（时效修正，回合结束自动流失）；
    ///    用户口径（本轮）：**不仅限正面打出**——**暗藏者付费翻面（现身）同样可享受该减免** → 付费通道写 `"打出,现身"`；
    ///  · 卡文"**灾难**"＝子类别 `灾难`（卡库 `JC135 余震` 即 `SubCategories=["灾难"]`）。
    /// </summary>
    public static class JC055_annisiduola_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC055_annisiduola", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC055_A_PreparePlaceTimeMarker",
                Timing = EffectTiming.OnTurnStarted,     // 准备步骤（每回合开始）
                Forced = false,                          // 卡文"触发"（无"可以"）→ 触发本身走可选窗口
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    KindAlt = TargetKind.Attachment,     // 【K14】"目标角色或附属"
                    NeedsSelection = true,
                    // 【2026-09 修复】"在目标角色或附属上"未排除自身 ⇒ 可指定自身（JC008/JC069 同款约定）；
                    // 否则它是本地区唯一角色时该准备步骤触发被静默跳过（JC004 同类缺陷）。
                    CanTargetSelf = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PlaceCounter, Args = new Array<string> { "时间标志", "1", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "准备步骤：在目标角色或附属上放置一个时间标志。",
            });

            // ② 行动：自本方卡牌上移除 X 个时间标志 → 本回合下一张灾难牌费用 -X（X 由玩家选择，至少 1）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC055_B_RemoveMarkersReduceDisasterCost",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",                  // 卡文"行动"（自己的行动步骤、堆叠为空）
                Forced = false,
                ValueExpr = "代价数量",                    // 【JC055②】X＝本次激活已支付（移除）的标志数量
                CounterCost = new CounterCostSpec
                {
                    Markers = new Array<string> { "时间标志", "时间标记" },   // 卡库两种写法都认
                    AmountByPlayerChoice = true,          // X 由玩家逐卡分配（A30）；至少 1
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        // Args：[费用减少], [X], [筛选＝灾难牌子类别], [一次性], [付费通道＝打出+现身]
                        // 主体缺省＝来源自身（`Gain` 的费用减少按来源控制者归属，无需写主体）
                        Args = new Array<string> { "费用减少", "$X", "子类别:灾难", "一次性", "打出,现身" },
                    },
                },
                Duration = "直到回合结束",                 // 时效费用修正（回合结束未用则流失）
                FaqNote = "行动：自本方卡牌上移除任意数量的时间标志（至少 1，由发动者逐卡选择）→ "
                        + "本回合中你打出的**下一张灾难牌**所需费用减少等量点数；"
                        + "该减免**打出与暗藏者付费翻面（现身）两个通道都可享受**，未用则回合结束失效。",
            });
        }
    }
}
