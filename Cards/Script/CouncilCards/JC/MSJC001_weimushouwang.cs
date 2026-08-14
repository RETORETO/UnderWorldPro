using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC001_weimushouwang （帷幕守望）。
    /// 牌面数据来自同名 .tres；激活效果逻辑在构造函数中以 ActivationOptions 填充（RefCounted 不序列化进 .tres）。
    /// 两张激活效果均为【非快速行动效果】，由控制者支付费用 + 横置本秘社作为代价。
    ///   - 选项1：横置本秘社 + 付3费 → 控制者抓1张牌（牌库顶）。
    ///   - 选项2：横置本秘社 + 付4费 → 控制者从牌库底抓1张牌。
    /// </summary>
    [GlobalClass]
    public partial class MSJC001_weimushouwang : CouncilCardData
    {
        public MSJC001_weimushouwang()
        {
            // —— 选项1：3费，横置本秘社，抽1（牌库顶）——
            var opt1 = new CouncilActivationOption
            {
                OptionName = "横置·3费·抽1张",
                Cost = 3,
            };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC001_1_Draw",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }
                },
                Duration = "瞬时"
            });

            // —— 选项2：4费，横置本秘社，从牌库底抽1 ——
            var opt2 = new CouncilActivationOption
            {
                OptionName = "横置·4费·牌库底抽1张",
                Cost = 4,
            };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC001_2_DrawBottom",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 4,
                Forced = false,
                Target = null,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DrawFromBottom, Args = new Array<string> { "1" } }
                },
                Duration = "瞬时"
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);
        }
    }
}
