using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC005_guojiajigou （国家机构）。
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中需包含 25 张或更多【黄】派系的牌。
    ///   行动3，横置：**若你具有【先手标志】**，则抓一张牌。
    ///   行动4，横置：从你的牌库中**寻找一张【国家机构】独有牌**，展示该牌后置于你的手中，然后洗牌。
    ///                **该能力每局游戏只能发动一次**。
    ///   初始手牌：6。
    ///
    /// 实现：
    ///  · 构筑限制＝`.tres` 的 `Restrictions`（`CouncilRestriction`：颜色=黄、≥25 张）；
    ///  · 初始手牌＝`StartingHandSize = 6`；
    ///  · 两个激活选项均为【行动·横置本秘社】+ 各自的费用（3 / 4）；
    ///  · 选项①的条件用谓词 **`具有先手标志`**（`EffectEngine.EvaluateCondition`：来源控制者是否为本回合先手）；
    ///  · 选项②＝`Search` 动词 + 筛选串 **`独有;组织:国家机构`**（`独有`＝`PlayerTemplate.UniqueField` 非空，
    ///    用户 2026-09 口径）+ 去向 `手牌` + 附加 `展示`（展示后入手再洗牌）；
    ///    次数限制用 **`MaxUsesPerGame = 1`**（【A57】每局游戏一次：不随回合重置、离场/换区也不清零）。
    /// </summary>
    [GlobalClass]
    public partial class MSJC005_guojiajigou : CouncilCardData
    {
        public MSJC005_guojiajigou()
        {
            // —— 选项1：行动3，横置本秘社；若你具有【先手标志】→ 抓一张牌 ——
            var opt1 = new CouncilActivationOption
            {
                OptionName = "横置·3费·若有先手标志则抽1张",
                Cost = 3,
            };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC005_1_DrawIfFirstPlayer",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = null,
                Condition = new EffectCondition { Predicate = "具有先手标志" },   // 【A57】"若你具有【先手标志】"
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }
                },
                Duration = "瞬时"
            });

            // —— 选项2：行动4，横置本秘社；从牌库寻找一张【国家机构】独有牌，展示后入手并洗牌；每局一次 ——
            var opt2 = new CouncilActivationOption
            {
                OptionName = "横置·4费·检索国家机构独有牌（每局一次）",
                Cost = 4,
            };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC005_2_SearchUniqueCard",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 4,
                Forced = false,
                Target = null,
                MaxUsesPerGame = 1,          // 【A57】"该能力每局游戏只能发动一次"
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        // [筛选, 去向, 附加, 范围]：`独有`＝UniqueField 非空；`组织:国家机构`＝【国家机构】牌
                        Args = new Array<string> { "独有;组织:国家机构", "手牌", "展示", "牌库" },
                    }
                },
                Duration = "瞬时",
                FaqNote = "检索牌库中一张【国家机构】独有牌（独有卡名非空），展示后置于手中并洗牌；该能力整局只能发动一次。"
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);
        }
    }
}
