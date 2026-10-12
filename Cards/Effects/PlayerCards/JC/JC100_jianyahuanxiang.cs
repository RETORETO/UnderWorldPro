using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC100 尖牙幻象 效果（角色）。
    /// 卡文：灵体
    ///      现身触发：你可以将尖牙幻象封印在本地区的目标角色上。
    ///      触发：当尖牙幻象被封印在一个角色上时，你可以支付1，若你如此做，令该角色获得-1防御力直到回合结束。
    /// 实现（效果级推进）：
    ///  · **现身触发**：P2 `ActionVerb.Seal` 的"自身 → 目标"形态；卡文"你可以…"= 触发可选（不设 `Forced`）。
    ///  · **被封印时·可选支付**（K11 触发版）：`OnSealed` + `OptionalPayCost=1`（**入堆叠之前**问一次
    ///    "是否支付 1"，只有是/否两个选项；用户 2026-10 口径："**造成防御降低的效果，发动能力时就需要支付费用，
    ///    而不是效果处理时支付费用**"）⇒ 选"是"先扣费再入堆叠，选"否"该触发不入堆叠（整条效果不结算）；
    ///    不再用 `PayOptional`（那是"结算时弹窗问支付"，用户实测口径："用错了时机"）。
    ///  · **结算**：`Gain{防御,-1,"封印宿主"}`（主体＝**封印宿主**，即"该角色"；`Duration=直到回合结束`
    ///    走时效修正栈自动还原）；减防会让"当前防御力"下降 ⇒ `ApplyGain` 内部同口径补做死亡判定
    ///    （否则按规则已死的角色会留在场上、UI 不刷新）。
    /// </summary>
    public static class JC100_jianyahuanxiang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC100_jianyahuanxiang", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 修复·灵体同类排查】卡文以**灵体**开头，但脚本此前没有授予【灵体】关键词
            // ⇒ `DamageRules.IsSpirit` 为假 ⇒ 灵体的"我方魔法领域图标更多则防止伤害"永不生效。
            // 现按 JC077/JC111 同款"持续关键字授予"补齐（消费点＝`CardManager.DamagePreventer`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC100_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC100_2_SealSelfOnCharacter",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IsLocalZone = true,          // 本地区
                    CanTargetSelf = false,       // 自己不能封在自己身上
                    IncludeCovert = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "自身", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "现身时可将自身封印在本地区一个目标角色上。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC100_3_PayToWeakenSealingHost",
                Timing = EffectTiming.OnSealed,
                Scope = ZoneScope.SealedZone,   // 【修正】触发时来源区＝封印区（此前无此分支 → 该触发在生产路径上收集不到）
                Condition = new EffectCondition { Predicate = "TriggerBySelf" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },   // 主体＝封印宿主，无需取目标
                Actions = new Array<EffectAction>
                {
                    // 令**封印宿主**（"该角色"）获得 -1 防御力，直到回合结束
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "防御", "-1", "封印宿主" },
                    },
                },
                // 【2026-10 用户口径】"支付1"＝**发动能力时**支付的额外费用（入堆叠之前问是/否两个选项，
                // 不输入金额）；不支付 ⇒ 该触发不入堆叠（整条效果不结算，也不扣费）。
                OptionalPayCost = 1,
                Duration = "直到回合结束",
                FaqNote = "被封印在角色上时可在**发动时**支付1（入堆叠前询问『是否支付 1』，两个选项）→ "
                        + "该角色 -1 防御力直到回合结束；不支付则该触发不结算（放弃整条效果请用触发排序窗）。",
            });
        }
    }
}
