using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC015_yinshi （隐士）——**占位脚本：激活效果待实现**（本轮只做 .tres 数据 + 机制）。
    ///
    /// 卡文（来源 Card.xml）：
    /// 持续：构筑时你的牌组中，非【白】且非【中立】派系的牌不能超过12张。
    /// 持续：在每局游戏开始时，在本方秘社上放置一个【隐秘】标志。
    /// 行动3，横置：选择目标地区，若你的手牌数量大于或等于四张，你可以从手中展示一张角色牌，然后将其以暗藏者状态置于该地区。
    /// 初始手牌：6
    /// 
    /// FAQ：
    /// ◆隐士秘社的二能力可以将带公开关键词的角色以暗藏者状态置于地区。
    ///
    /// TODO（新窗口实现）：把上面每条"行动…"写成一个 CouncilActivationOption（Cost/ActivateWindow="行动"/
    /// ActivateCondition="横置本角色"），EffectData.Timing = EffectTiming.CouncilActivated，
    /// **无目标能力必须显式 Target = null**（否则永远不可激活）；"每局游戏只能发动一次"用 MaxUsesPerGame = 1。
    /// 可用的筛选/谓词：DeckSearchFilter 的 独有（UniqueField 非空）/组织:X/子类别:X/类别:X/费用&lt;=N；
    /// EvaluateCondition 的 具有先手标志。
    /// </summary>
    [GlobalClass]
    public partial class MSJC015_yinshi : CouncilCardData
    {
        public MSJC015_yinshi()
        {
            // TODO：按卡文填 ActivationOptions（当前留空 = 该秘社暂时没有可发动的能力）
        }
    }
}