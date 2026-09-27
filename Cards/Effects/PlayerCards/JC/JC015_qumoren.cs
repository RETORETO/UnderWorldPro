using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC015 驱魔人 效果（自原 JC015_qumoren 构造函数迁移，逻辑不变）。
    /// 角色激活：行动，横置本角色，牺牲驱魔人 → 消灭目标非人类且印刷费用≥3的敌方角色。
    /// 目标合法性由 TargetRules 结构化声明（敌方 + 非人类 + 印刷费用≥3）。
    /// 注（2026-09 用户口径）：卡文只写"非人类"（"印刷"只修饰**费用**）→ 子类别判定走**实例当前类别**
    /// （`NotCurrentSubCategory`）：被 JC036 初拥改写为吸血鬼的角色 **合法**（它确实不再是人类）。
    /// </summary>
    public static class JC015_qumoren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC015_qumoren", Build);

        private static void Build(PlayerCardData data)
        {
            // —— 目标合法性（数据驱动，方案A主入口）——
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                new TargetRule
                {
                    Kind = TargetKind.Character,
                    Restrictions = new Godot.Collections.Array<TargetRestriction>
                    {
                        TargetRestriction.Enemy,
                        TargetRestriction.NotCurrentSubCategory,
                        TargetRestriction.CostAtLeast
                    },
                    Param = "人类|3"
                }
            };

            // —— JC015 角色激活：行动，横置+牺牲自身 → 消灭目标（敌方/非人类/费用≥3）角色 ——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC015_QuMoRen_Activate",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色,牺牲本角色",
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    Filter = "敌方非人类且印刷费用≥3的角色",
                    // ⚑ [2026-08-15] 激活目标精筛条件：与 data.TargetRules 同步（敌方 + 非人类 + 费≥3）。
                    // 供 EffectEngine.ValidateTargetAtResolve 结算复检兜底"夺控换边"：目标被夺控成己方后 Enemy 不匹配即判丢失。
                    // ⚑ [2026-09] 子类别判定改为**实例当前类别**（`NotCurrentSubCategory`，用户口径见类注释）。
                    Restrictions = new Godot.Collections.Array<TargetRestriction>
                    {
                        TargetRestriction.Enemy,
                        TargetRestriction.NotCurrentSubCategory,
                        TargetRestriction.CostAtLeast
                    },
                    Param = "人类|3"
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy }
                },
                Duration = "瞬时"
            });
        }
    }
}
