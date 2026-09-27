using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC135 余震 效果（事务卡 / 灾难）。
    /// 卡文：**额外费用：支付 X 费**。 快速行动：消灭目标**印刷费用为 X** 的附属。
    ///
    /// 【K10·额外费用】2026-09 交付，口径 **A24**（用户逐条确认）：
    ///  · 声明 `ExtraCost = { Kind = PayTargetCost, Mandatory = true }` ——
    ///    **先选目标、再定 X**：X ＝**所选定目标**的**印刷费用**（印刷＝卡片模型的 `Cost`）；
    ///  · **目标费用为 0**（如 0 费附属）→ **不支付额外费用**，直接生效后续效果（消灭该附属）；
    ///  · 若**合法目标的费用均大于打出者可用费用** → **该卡不能打出**（打出侧权威门禁；
    ///    可用费用不足时整次打出被拒、无副作用）；
    ///  · 额外费用与常规费用**合并**为本次打出的总费用（基础 2 + X），在**付费阶段、入堆叠之前**付清，
    ///    **不在堆叠里结算、不可被响应**（用户口径）。
    ///
    /// ② 快速行动：消灭目标附属 —— 目标已在付费前选定并锁定入堆叠（`StackEntry.TargetCardUid`），
    ///    结算时 `Destroy`（消灭＝死亡，照常广播死亡时机）；目标已离场则由 D16 目标丢失判据整条不结算。
    ///    注：卡文"印刷费用为 X 的附属"中的 X 由**额外费用**保证（X 就是该目标的印刷费用），
    ///    故目标声明只需"附属"，无需再写费用筛选（不会出现"付费 X 却打别人"的偏差）。
    /// </summary>
    public static class JC135_yuzhen_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC135_yuzhen", Build);

        private static void Build(PlayerCardData data)
        {
            // 【K10·额外费用】"支付 X 费"，X＝所选定目标的印刷费用
            data.ExtraCost = new ExtraCostSpec
            {
                Kind = ExtraCostKind.PayTargetCost,
                Mandatory = true,
            };
            data.ExtraCostDescription = "支付 X 费（X＝目标印刷费用）";
            data.IsExtraCostMandatory = true;

            // 打出目标＝附属（X 由该目标的印刷费用决定，见类注释）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Attachment),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC135_1_DestroyAttachmentCostX",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,     // 快速行动：有优先权即可打出（含响应窗口/堆叠非空）
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Attachment,
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                Duration = "瞬时",
                FaqNote = "先选目标 → X ＝该目标的**印刷费用**（0 费附属则无需支付额外费用）→ "
                        + "支付总费用（2 + X，付费阶段、不可被响应）→ 结算时消灭该附属。",
            });
        }
    }
}
