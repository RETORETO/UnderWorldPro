using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC009_mishe （秘社）。
    /// 卡文（来源 Card.xml）：行动3，横置：若你具有【先手标志】，则抓一张牌。／初始手牌：**7**（用户口径：测试需要 7）。
    /// 实现：单一激活选项＝`Draw{1}` + 条件谓词 `具有先手标志`（【A57】：来源控制者是否为本回合先手）。
    /// 说明：本秘社**无构筑限制**（卡文只写"初始手牌"），也是"玩家未携带秘社"时的**兜底默认秘社**
    /// （`GameInitializer.DefaultCouncilPath` / `DeckListLoader.FallbackCouncilPath` / `DeckBuilder` 默认值）。
    /// </summary>
    [GlobalClass]
    public partial class MSJC009_mishe : CouncilCardData
    {
        public MSJC009_mishe()
        {
            var opt1 = new CouncilActivationOption
            {
                OptionName = "横置·3费·若有先手标志则抽1张",
                Cost = 3,
            };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC009_1_DrawIfFirstPlayer",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = null,                                                        // 无目标能力必须显式 null（见 A57 陷阱）
                Condition = new EffectCondition { Predicate = "具有先手标志" },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }
                },
                Duration = "瞬时"
            });

            ActivationOptions.Add(opt1);
        }
    }
}