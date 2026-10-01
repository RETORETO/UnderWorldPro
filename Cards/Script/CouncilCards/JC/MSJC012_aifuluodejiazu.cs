using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC012_aifuluodejiazu （爱弗罗德家族）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中只能包含【蓝】和【中立】派系的牌，以及其他派系的吸血鬼或奴仆角色牌。
    /// 持续：所有印刷费用小于或等于2的本方人类角色额外具有奴仆类别。
    /// 行动，1，横置，横置三个本方奴仆角色：抓一张牌。
    /// 行动，2，横置，横置三个本方吸血鬼角色：目标敌方玩家弃一张牌。
    /// 初始手牌：6
    /// 
    /// FAQ：
    /// ◆当你的秘社是爱弗罗德家族时，只有在场上的 2 费及以下人类角色才额外具有奴仆类别。在牌库、 手牌以及墓地等其他区域的人类角色牌，都不具有奴仆类别。
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC012_aifuluodejiazu : CouncilCardData
    {
        public MSJC012_aifuluodejiazu()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}