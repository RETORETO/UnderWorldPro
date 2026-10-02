using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC011_SPTzhixingbu （S．P．T执行部）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中只能包含【绿】和【中立】派系的牌，以及其他派系带有［黑刀］能力图标的人类角色牌。
    ///   行动，3，横置：目标角色获得杀伤1直到回合结束。
    ///   行动，3，横置：选择目标地区，所有位于该地区，且带有［黑刀］能力图标的本方角色获得撤回直到回合结束。
    ///   初始手牌：6
    ///
    /// FAQ：若二能力已发动且目标地区牌被赢取时，之后才被移动进入该地区的角色**不会**获得撤回
    ///      ⇒ 群体集合在**结算那一刻**确定（`MassMode.AllInArea` 的一次性集合语义天然满足）。
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {绿, 中立}` + 例外 `类别:角色 ∧ 子类别:人类 ∧ 图标:黑刀`）；
    ///  · **［黑刀］能力图标**的判定口径（用户 2026-09 拍板）＝**战力 &gt; 0**（**不含先手战力**）：
    ///    构筑侧＝`CouncilExceptionRule.FilterByAbilityIcon / TargetAbilityIcon="黑刀"`；
    ///    效果侧＝`TargetRestriction.HasAbilityIcon`（`Param="黑刀"`）——两处共用同一套"战力"口径；
    ///  · ①＝`Gain{杀伤,1,目标}` + `Duration="直到回合结束"`（杀伤走属性通道，与 `KeywordEffects.Kill` 同源）；
    ///  · ②＝`NeedsAreaTarget`（选地区）＋群体 `Target{Kind=Character, MassMode=AllInArea, NeedsSelection=false,
    ///    Restrictions=[Friendly, HasAbilityIcon("黑刀")]}` ＋ `Gain{撤回,1,目标}`。
    /// </summary>
    [GlobalClass]
    public partial class MSJC011_SPTzhixingbu : CouncilCardData
    {
        public MSJC011_SPTzhixingbu()
        {
            // —— 选项1：行动3，横置；目标角色获得杀伤1直到回合结束 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·3费·目标角色获得杀伤1", Cost = 3 };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC011_1_GrantKill1",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                // "目标角色"：不限敌我、不限地区 ⇒ 全场争夺区角色
                Target = new TargetSpec { Kind = TargetKind.Character, AllAreas = true },
                Actions = new Array<EffectAction>
                {
                    // 杀伤N 走属性通道（与 KeywordEffects.Kill 同一实现：`Attribute="杀伤"`）
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "杀伤", "1", "目标" } },
                },
                Duration = "直到回合结束",
            });

            // —— 选项2：行动3，横置；选择目标地区，该地区带［黑刀］图标的本方角色获得撤回直到回合结束 ——
            var opt2 = new CouncilActivationOption { OptionName = "横置·3费·所选地区带［黑刀］的本方角色获得撤回", Cost = 3 };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC011_2_GrantWithdrawInArea",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                NeedsAreaTarget = true,          // "选择目标地区"
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    MassMode = MassMode.AllInArea,          // 该地区内**所有**符合条件的卡（群体）
                    NeedsSelection = false,                 // 玩家选的是地区，不是角色
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.Friendly,             // "本方角色"
                        TargetRestriction.HasAbilityIcon,        // "带有［黑刀］能力图标"（Param 见下）
                    },
                    Param = "黑刀",
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "撤回", "1", "目标" } },
                },
                Duration = "直到回合结束",
                FaqNote = "群体集合在结算那一刻确定：若目标地区随后被赢取、之后才被移动进入该地区的角色不会获得撤回。",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);
        }
    }
}
