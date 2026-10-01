using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC017_mengyanzuqun （梦魇族群）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中只能包含【紫】和【中立】派系的牌，以及其他派系的灵体角色或梦境牌。
    /// 持续：你从牌库底抓牌，而非牌库顶。
    /// 行动，3，横置：若你具有【先手标志】，则将你牌库顶的一张牌封印在目标角色上。
    /// 行动，4，横置：将你墓地中的目标灵体角色置于你的牌库顶。
    /// 初始手牌：6
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC017_mengyanzuqun : CouncilCardData
    {
        public MSJC017_mengyanzuqun()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}