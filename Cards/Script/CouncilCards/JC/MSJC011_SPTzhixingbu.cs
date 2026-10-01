using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC011_SPTzhixingbu （S．P．T执行部）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中只能包含【绿】和【中立】派系的牌，以及其他派系带有［黑刀］能力图标的人类角色牌。
    /// 行动，3，横置：目标角色获得杀伤1直到回合结束。
    /// 行动，3，横置：选择目标地区，所有位于该地区，且带有［黑刀］能力图标的本方角色获得撤回直到回合结束。
    /// 初始手牌：6
    /// 
    /// FAQ：
    /// ◆若 S．P．T执行部秘社的二能力已经发动且目标地区牌被赢取时，玩家通过其他能力使位于其他地区的角色移动进入被赢取地区，这些角色不会获得撤回。
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC011_SPTzhixingbu : CouncilCardData
    {
        public MSJC011_SPTzhixingbu()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}