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
    ///  · **被封印时·可选支付**（K11）：`OnSealed` + `PayOptional{1,0,1}`（弹**数值输入框**问是否支付1；不支付 → 后续跳过）
    ///    + `Gain{防御,-1,"封印宿主"}`（主体＝**封印宿主**，即"该角色"；`Duration=直到回合结束` 走时效修正栈自动还原）。
    /// </summary>
    public static class JC100_jianyahuanxiang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC100_jianyahuanxiang", Build);

        private static void Build(PlayerCardData data)
        {
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
                    // 可选支付 1（范围 0..1，默认 1）
                    new EffectAction
                    {
                        Verb = ActionVerb.PayOptional,
                        Args = new Array<string> { "1", "0", "1" },
                    },
                    // 令**封印宿主**（"该角色"）获得 -1 防御力，直到回合结束
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "防御", "-1", "封印宿主" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "被封印在角色上时可支付1（可选）→ 该角色 -1 防御力直到回合结束；不支付则无事发生。",
            });
        }
    }
}
