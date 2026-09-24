using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC010 青手费南迪尔 效果（角色）。
    /// 卡文：领袖 灵体 快速行动，横置：消灭目标已横置的角色。
    /// 实现：领袖/灵体以「持续关键字授予」表达（无前提=常驻），
    ///      激活效果用快速行动 + 横置本角色为代价，目标精筛 `TargetRestriction.Tapped`（P1 新增）。
    /// </summary>
    public static class JC010_qingshoufeinandier_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC010_qingshoufeinandier", Build);

        private static void Build(PlayerCardData data)
        {
            // 领袖 / 灵体（P1：Keyword 可授；两者的**完整规则语义**仍待规则文本补齐，见 JC实现清单 D 档）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC010_A_Keywords",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "领袖；灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "领袖", Amount = 1 },
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            // 快速行动，横置本角色：消灭目标已横置的角色
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC010_1_DestroyTapped",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Tapped },
                },
                Actions = new Array<EffectAction>
                {
                    // 注：横置自身是**代价**，由 ActivateCondition "横置本角色" 在引擎侧付出（EffectEngine.ActivateAbility 第 3 步），
                    // 不能写进 Actions —— ExecuteVerb 的 Tap 作用于**目标**而非来源。
                    new EffectAction { Verb = ActionVerb.Destroy },    // 消灭目标（已横置角色；按 P1 语义会广播死亡触发）
                },
                Duration = "瞬时",
                FaqNote = "横置自身为代价；只能指定已横置的角色。",
            });
        }
    }
}
