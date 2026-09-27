using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC083 睿智的斯芬克司 效果（角色）。
    /// 卡文：准备步骤触发：**选择一个数字**，然后**展示你牌库顶的一张牌**。
    ///       若所展示的牌的**印刷费用**和你选择的数字**相同**，则将该牌置于你手中；
    ///       若不相同，将所展示的牌置于你的牌库底。
    ///
    /// 实现（**K1·"玩家选择"型动态值**首次落地，用户口径 **A31** ＋ **A36**）：
    ///  · `ValueExpr = "玩家选择"` ＋ `ValueMin = 0` / `ValueMax = 20`（**A31：数字范围 0～20**）；
    ///  · 取值交互＝**复用既有数值输入窗**（`IGameUIHooks.PromptNumberInput`；A36），
    ///    在效果结算**之前**弹出（`EffectEngine.BeginPlayerValuePrompt` 挂起结算 → 回调写回选择值 → 重入结算）；
    ///    **取消/无 UI → 该效果不发动**（fail-closed，绝不替玩家猜一个数字）；
    ///  · **X=0 合法**：此处的 X 是"**猜测值**"而非"数量"，故**不套用** K1 的"X=0 ⇒ 效果不发动"那条规则
    ///    （否则猜不到"印刷费用为 0 的牌"）；对照：JC055② 的 X 是**移除数量**，声明为"至少 1"；
    ///  · 动作 `RevealTopAndTakeIfCostIs{$X}`：公开展示来源控制者**牌库顶**一张 →
    ///    印刷费用相等 → **置入手牌**（口径 A31：**不算抓牌** → 不广播 `OnDraw`，走 `MoveCardToHand`）；
    ///    不等 → 置入**牌库底**（`ReturnCardToDeck(toTop:false)`）；牌库为空 → 无事发生。
    /// </summary>
    public static class JC083_ruizhidesifenkesi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC083_ruizhidesifenkesi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC083_A_GuessTopDeckCost",
                Timing = EffectTiming.OnTurnStarted,     // 准备步骤（与 JC024/JC048/JC055① 同一时机常量）
                ValueExpr = "玩家选择",                   // 【K1】数字由玩家给出
                ValueMin = 0,                            // A31：0～20（0 合法＝可猜"印刷费用为 0"的牌）
                ValueMax = 20,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.RevealTopAndTakeIfCostIs,
                        Args = new Array<string> { "$X" },   // 期望的印刷费用＝玩家所选数字
                    },
                },
                Duration = "瞬时",
                FaqNote = "准备步骤：先选一个数字（0～20），再公开展示你牌库顶的一张牌；"
                        + "印刷费用与之相同 → 置入手牌（**不算抓牌**，不触发『每当你抓牌时…』类效果）；"
                        + "不相同 → 置入你牌库底。",
            });
        }
    }
}
