using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC016_songwangzhe （颂亡者）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中只能包含【黑】和【中立】派系的牌，以及其他派系【死】领域的牌。
    /// 触发：在第一个回合的准备步骤开始时，将你牌库顶的三张牌置于你的墓地。
    /// 行动，3，横置：将你牌库顶的一张牌置于你的墓地。
    /// 行动，6，横置，将你墓地中的七张牌封印在颂亡者上：选择目标地区，令你墓地中的目标【死】领域的角色牌正面进场并置于该地区。
    /// 初始手牌：5
    /// 
    /// FAQ：
    /// ◆颂亡者秘社的二能力发动后，需先将七张牌封印在颂亡者上，在结算时，如果此能力被响应导致目标地区被赢取（敌方还差一势力标记时赢取地区翻面夜总会看门人）翻开新的地区，则颂亡者的二能力因目标不合法而失效。
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC016_songwangzhe : CouncilCardData
    {
        public MSJC016_songwangzhe()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}