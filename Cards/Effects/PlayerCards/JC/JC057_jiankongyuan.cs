using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC057 监控员 效果（角色，人类/工程师，国家机构）。
    /// 卡文：现身触发：检视目标玩家的手牌。 持续：只要监控员未横置，你可以随时检视本地区的任一暗藏者。
    ///
    /// 本轮交付**第一条**（K24 玩家目标 + P3·H 检视）：
    ///  · **现身触发** ＝ `EffectTiming.UncoverTrigger`（暗藏→公开时由 `CardManager.RevealCard` 广播）；
    ///  · **目标玩家** ＝ `Target{Kind=Player, Restrictions=[Enemy]}` → 触发入堆叠前经**独立选择窗口**就地选定
    ///    （`TurnManager.EnsurePlayerTargetThenPush`，与卡目标同口径：**入堆叠时锁定**）；
    ///  · **检视该玩家的手牌** ＝ `Inspect{"目标玩家手牌"}`（私密查看：只给来源控制者本人看，不改任何状态）。
    /// ②【持续·随时检视本地区任一暗藏者】（本批交付，用户 2026-09 口径）：
    ///   `Timing = Static` ＋ `Scope = AreaZone` ＋ `PrereqSpec{Kind=SelfUntapped}` ＋
    ///   `StaticEffects{ Kind = AllowInspectLocalCovert }` —— **只要监控员未横置**，其控制者即可**随时**查看
    ///   **与监控员同一地区**的**暗藏者**（含对手的）卡面；横置即失效、重置即恢复（前提实时重判）。
    ///   消费点＝**UI 悬浮预览的可见性判定** `Script/Manager/CovertInspectRule.CanViewerSeeCardFace`
    ///   （静态纯函数：读卡面印刷规则 ＋ 实例态，单机/联机客户端同解；"随时"这种**不经过效果结算**的
    ///   查看通道无法用一次性弹窗表达，故走"鼠标悬浮 → 悬浮预览显示整张卡图"的 UI 通道）。
    /// </summary>
    public static class JC057_jiankongyuan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC057_jiankongyuan", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC057_1_InspectTargetPlayerHand",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    NeedsSelection = true,
                    // 【2026-09 用户裁定】**"目标玩家"＝任意玩家（含自己）** ⇒ **不加** `Restrictions`
                    // （空＝任意；`Enemy`＝对手 / `Friendly`＝本方）。原先写 `Enemy` 属收窄，已按裁定移除。
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Inspect, Args = new Array<string> { "目标玩家手牌" } },
                },
                Duration = "瞬时",
                FaqNote = "现身时检视目标玩家（对手）的手牌；检视是私密查看，不给其他人看、也不改变任何状态。",
            });

            // ==================== ② 持续：只要监控员未横置，可以随时检视本地区的任一暗藏者 ====================
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC057_2_InspectLocalCovertAnyTime",
                Timing = EffectTiming.Static,
                Scope = ZoneScope.AreaZone,                       // 在争夺区常驻生效
                PrereqSpec = new ContinuousPrereqSpec { Kind = PrereqKind.SelfUntapped },   // "只要监控员未横置"
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.AllowInspectLocalCovert, Param = "" },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "未横置时，其控制者可随时检视**与监控员同一地区**的任一暗藏者（含对手的）的卡面；"
                        + "横置即失去该授权，重置即恢复。",
            });
        }
    }
}
