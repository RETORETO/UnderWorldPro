using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC015 驱魔人 效果（自原 JC015_qumoren 构造函数迁移，逻辑不变）。
    /// 角色激活：行动，横置本角色，牺牲驱魔人 → 消灭目标非人类且印刷费用≥3的敌方角色。
    /// 目标合法性由**激活目标** `EffectData.Target` 结构化声明（敌方 + 非人类 + 印刷费用≥3）。
    ///
    /// ⚑ [2026-10-18 用户批准] **移除卡级 `data.TargetRules`**：
    ///   `TargetRules` 是「**打出目标**」语义（附属选宿主 / 事务选目标卡），由 `IsLegalPlayTarget` 消费；
    ///   而本卡唯一的效果是**激活能力**，目标是"双击激活时"选的，由 `EffectData.Target` 承担即可 ——
    ///   `ActivateEligibility.IsLegalActivateTarget` 的无 TargetRules 分支自 P3·K21 起同样走
    ///   `TargetValidity.CheckRestrictions(Target.Restrictions, Target.Param, …)` 精筛，
    ///   与 `MatchesRule`（TargetRules 分支）**同一函数**，故原 TargetRules 对激活判定 100% 冗余。
    ///   留着它只有一个净副作用：`GetRequiresTarget()` 判为 true ⇒ 角色卡被误判"打出需选目标" ⇒
    ///   效果免费打出（JC119 链路 `ProcessQueuedFreePlays`）会进入选目标窗，而角色卡既无合法卡目标、
    ///   `GetAllowedTargets()` 也不含 Area ⇒ "无任何合法目标 → 留在手牌"（JC099 无此配置 → 地区为空，同样错）。
    ///
    /// 注（2026-09 用户口径）：卡文只写"非人类"（"印刷"只修饰**费用**）→ 子类别判定走**实例当前类别**
    /// （`NotCurrentSubCategory`）：被 JC036 初拥改写为吸血鬼的角色 **合法**（它确实不再是人类）。
    /// </summary>
    public static class JC015_qumoren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC015_qumoren", Build);

        private static void Build(PlayerCardData data)
        {
            // —— JC015 角色激活：行动，横置+牺牲自身 → 消灭目标（敌方/非人类/费用≥3）角色 ——
            // 【2026-10-18】不再写 `data.TargetRules`（理由见类注释）：本卡无"打出目标"，
            // 激活目标的精筛只在下面 `EffectData.Target.Restrictions/Param` 声明一处（单一真相源）。
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
                    // 激活目标精筛条件（敌方 + 非人类 + 费≥3）：
                    // `IsLegalActivateTarget` 取目标阶段与结算复检共用 `TargetValidity.CheckRestrictions`，
                    // 兜底"夺控换边"（目标被夺控成己方后 Enemy 不匹配即判丢失）。
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
