using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC017_mengyanzuqun （梦魇族群）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中只能包含【紫】和【中立】派系的牌，以及其他派系的灵体角色或梦境牌。
    ///   持续：你从牌库底抓牌，而非牌库顶。
    ///   行动，3，横置：若你具有【先手标志】，则将你牌库顶的一张牌封印在目标角色上。
    ///   行动，4，横置：将你墓地中的目标灵体角色置于你的牌库顶。
    ///   初始手牌：6
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {紫, 中立}` + **两条**例外：
    ///    ①`类别:角色 ∧ 关键字:灵体`、②`子类别:梦境`）；
    ///  · "从牌库底抓牌"＝ `CouncilCardData.DrawFromBottom = true` —— 消费点是抓牌**唯一收口**
    ///    `TurnManager.DrawCardsForPlayer`（初始发牌/阶段抓牌/效果抓牌统一改向），不是逐卡特例；
    ///  · ① 条件"若你具有【先手标志】"＝ 既有谓词 `具有先手标志`；收益＝`Seal{牌库顶, 目标}`
    ///    （把来源控制者牌库顶那张封印在**目标角色**上）；
    ///  · ② 目标＝**你墓地中的灵体角色**（`Kind=Graveyard` + `Friendly` + `HasKeyword("灵体")`
    ///    + `StaticSubCategory("角色")`）＋ `PutCardOnDeckTop`（置于**你的**牌库顶）。
    /// </summary>
    [GlobalClass]
    public partial class MSJC017_mengyanzuqun : CouncilCardData
    {
        public MSJC017_mengyanzuqun()
        {
            // —— 选项1：行动3，横置；若你具有【先手标志】，则将牌库顶一张牌封印在目标角色上 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·3费·若有先手标志则封印牌库顶一张到目标角色" };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC017_1_SealDeckTopOnTarget",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = new TargetSpec { Kind = TargetKind.Character, AllAreas = true },   // "目标角色"
                Condition = new EffectCondition { Predicate = "具有先手标志" },
                Actions = new Array<EffectAction>
                {
                    // [被封印者来源, 宿主]：牌库顶（来源控制者）→ 目标角色
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "牌库顶", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "条件『若你具有【先手标志】』不满足时该能力不产生效果（发动门禁与结算条件共用同一谓词）。",
            });

            // —— 选项2：行动4，横置；将你墓地中的目标灵体角色置于你的牌库顶 ——
            var opt2 = new CouncilActivationOption { OptionName = "横置·4费·将墓地中的目标灵体角色置于牌库顶" };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC017_2_SpiritGraveyardToDeckTop",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 4,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Graveyard,
                    Restrictions = new Array<TargetRestriction>
                    {
                        TargetRestriction.Friendly,           // "**你**墓地中的"
                        TargetRestriction.HasKeyword,         // "**灵体**角色"
                        TargetRestriction.StaticSubCategory,  // "灵体**角色**"
                    },
                    Param = "灵体|角色",                       // HasKeyword 取第 0 段；StaticSubCategory 取第 1 段
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PutCardOnDeckTop, Args = new Array<string> { "目标" } },
                },
                Duration = "瞬时",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);

            // —— 持续：你从牌库底抓牌，而非牌库顶 ——
            DrawFromBottom = true;
        }
    }
}
