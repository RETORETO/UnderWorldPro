using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC010_mozu （貘组）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中只能包含【黄】和【中立】派系的牌，以及其他派系【星】领域的牌。
    /// 行动，2，横置：从你的手中选择一张【星】领域的牌，将其封印在貘组上，然后抓一张牌。
    /// 行动，2，横置，将一张封印在貘组上的牌置于你的牌库底：目标角色获得或失去灵体能力直到回合结束。
    /// 行动，4，横置，将两张封印在貘组上的牌置于你的牌库底：选择目标地区，令一张封印在貘组上的角色牌正面进场并置于该地区，且由你操控。
    /// 初始手牌：6
    /// 
    /// FAQ：
    /// ◆貘组秘社的三能力发动后，需先将两张封印在貘组秘社牌上的卡置于牌库底，在结算时，如果此能力被响应导致目标地区被赢取（敌方还差一势力标记时赢取地区翻面夜总会看门人）翻开新的地区，则貘组的三能力因目标不合法而失效。
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC010_mozu : CouncilCardData
    {
        public MSJC010_mozu()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}