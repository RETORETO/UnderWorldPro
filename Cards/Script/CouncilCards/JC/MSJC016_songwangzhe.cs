using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC016_songwangzhe （颂亡者）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中只能包含【黑】和【中立】派系的牌，以及其他派系【死】领域的牌。
    ///   触发：在第一个回合的准备步骤开始时，将你牌库顶的三张牌置于你的墓地。
    ///   行动，3，横置：将你牌库顶的一张牌置于你的墓地。
    ///   行动，6，横置，将你墓地中的七张牌封印在颂亡者上：选择目标地区，
    ///                   令你墓地中的目标【死】领域的角色牌正面进场并置于该地区。
    ///   初始手牌：5（PT 也是 5）
    ///
    /// FAQ：二能力发动后需**先**将七张牌封印在颂亡者上；结算时若目标地区已被赢取而翻开新地区，
    ///      则因目标不合法而失效 ⇒ 由既有 D16 目标丢失判定承担。
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {黑, 中立}` + 例外 `领域=死亡`）；
    ///  · "触发：第一个回合的准备步骤开始时磨三张"＝ `TurnStartEffectList`
    ///    （由 `TurnManager.DoPrepare` 在 `TurnCount == 1` 时执行一次，**在起手发完之后**）；
    ///  · ①＝`MillTopToGraveyard{1}`（磨牌**不是**"消灭"，不触发死亡时机）；
    ///  · ②＝ 牌作代价 `CardCostSpec{FromZone="墓地", Count=7, CostAction=Seal{自身,自身}}`
    ///    ＋ `NeedsAreaTarget` ＋ 目标＝**你墓地中的目标【死】领域角色牌**
    ///    （`Kind=Graveyard` + `Restrictions=[Friendly, StaticSubCategory(角色)]` + `Filter="领域:死亡"`）
    ///    ＋ `PutToArea{"正面"}`（把该目标牌正面放进本次结算的目标地区）。
    /// </summary>
    [GlobalClass]
    public partial class MSJC016_songwangzhe : CouncilCardData
    {
        public MSJC016_songwangzhe()
        {
            // —— 选项1：行动3，横置；将你牌库顶的一张牌置于你的墓地 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·3费·磨1张（牌库顶→墓地）" };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC016_1_MillTopOne",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = null,                                  // 无目标（A57 防呆）
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.MillTopToGraveyard, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
            });

            // —— 选项2：行动6，横置，将墓地中的七张牌封印在颂亡者上；令墓地中目标【死】领域角色牌正面进场 ——
            var opt2 = new CouncilActivationOption { OptionName = "横置·6费·封印墓地七张·令墓地【死】领域角色牌正面进场" };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC016_2_SealSevenThenReanimate",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 6,
                Forced = false,
                NeedsAreaTarget = true,                          // "选择目标地区"
                // 目标＝你墓地中的【死】领域**角色牌**（放入所选地区的就是它）
                Target = new TargetSpec
                {
                    Kind = TargetKind.Graveyard,
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.Friendly,              // "**你**墓地中的"
                        TargetRestriction.StaticSubCategory,      // "…的角色牌"
                    },
                    Param = "角色",
                    Filter = "领域:死亡",                          // "【死】领域的"
                },
                // 牌作代价：把你墓地中的**七张**牌封印在颂亡者（来源秘社）上
                CardCost = new CardCostSpec
                {
                    FromZone = "墓地",
                    HostSelf = true,
                    Count = 7,
                    Mandatory = true,
                    CostAction = new EffectAction
                    {
                        Verb = ActionVerb.Seal,
                        Args = new Array<string> { "自身", "自身" },   // 被封印者＝选中的那张；宿主＝来源秘社
                    },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PutToArea, Args = new Array<string> { "正面" } },
                },
                Duration = "瞬时",
                FaqNote = "代价＝把墓地中七张牌封印在颂亡者上（墓地不足七张时不能发动）；" +
                          "收益＝令墓地中的目标【死】领域角色牌正面进场到所选地区。",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);

            // —— 触发：在**第一个回合的准备步骤开始时**，将你牌库顶的三张牌置于你的墓地 ——
            TurnStartEffectList.Add(new EffectData
            {
                EffectId = "MSJC016_T_FirstUpkeepMillThree",
                Timing = EffectTiming.OnTurnStarted,
                ContinuousEffect = "第一个回合的准备步骤开始时磨三张",
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.MillTopToGraveyard, Args = new Array<string> { "3" } },
                },
                Duration = "瞬时",
            });
        }
    }
}
