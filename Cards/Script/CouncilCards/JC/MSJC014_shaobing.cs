using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC014_shaobing （哨兵）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中只能包含【灰】和【中立】派系的牌，以及其他派系不具有魔法领域图标的牌。
    /// 行动，3，横置：在目标敌方暗藏者上放置一个锁定标志。
    /// 行动，3，横置：选择目标本方角色，将其移动到目标具有锁定标志的角色或暗藏者所在的地区。
    /// 初始手牌：6
    /// 
    /// FAQ：
    /// ◆哨兵秘社的一能力需先选择角色或暗藏者，在结算时，如果你指定的暗藏者已响应翻面为角色，则哨兵的一能力因目标不合法而失效，反之，指定的角色响应翻面成暗藏者，此能力也同样失效。
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC014_shaobing : CouncilCardData
    {
        public MSJC014_shaobing()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}