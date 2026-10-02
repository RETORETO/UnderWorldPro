using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Script.CouncilCards.JC
{
    /// <summary>
    /// MSJC013_jishizhe （计时者）—— 用户 2026-09 口径落地。
    ///
    /// 卡文（来源 Card.xml）：
    ///   持续：构筑时你的牌组中，非【红】且非【中立】派系的牌不能超过12张。
    ///   行动2，横置：在目标角色或附属上放置一个时间标志。
    ///   行动3，横置：消灭所有其上有三个或更多时间标志的角色和附属，然后每有一个角色或附属因此法被消灭，就抓一张牌。
    ///   初始手牌：6
    ///
    /// 实现要点：
    ///  · 构筑限制＝`.tres` 白名单（`AllowedColors = {红, 中立}` + `ExceptionMaxCount = 12`，**无例外条目**）；
    ///  · ① 目标＝"角色**或**附属" ⇒ `TargetSpec{Kind=Character, KindAlt=Attachment, AllAreas=true}` +
    ///    `PlaceCounter{"时间标志","1","目标"}`；
    ///  · ② "消灭**所有**其上有三个或更多时间标志的角色和附属"＝ 群体效果（`MassMode.AllInArea`）＋
    ///    **跨全部地区**（`AllAreas=true`，卡文不限定地区）＋ **卡上标志门槛**（`MinCounters="时间标志:3"`，
    ///    新增字段，解析实现在 `MassTargetPool.MeetsMinCounters`）＋ `Kind=None`（角色/附属都收）；
    ///  · "然后**每有一个**…因此法被消灭，就抓一张牌"（用户口径：按**实际**消灭数）＝ 动手前记下集合大小，
    ///    逐个 `Destroy`（走死亡统一出口），最后按**实际生效数**抓等量牌。
    /// </summary>
    [GlobalClass]
    public partial class MSJC013_jishizhe : CouncilCardData
    {
        public MSJC013_jishizhe()
        {
            // —— 选项1：行动2，横置；在目标角色或附属上放置一个时间标志 ——
            var opt1 = new CouncilActivationOption { OptionName = "横置·2费·目标角色或附属上放置一个时间标志" };
            opt1.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC013_1_PlaceTimeCounter",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    KindAlt = TargetKind.Attachment,     // "角色**或**附属"
                    AllAreas = true,                     // 卡文不限定地区
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PlaceCounter, Args = new Array<string> { "时间标志", "1", "目标" } },
                },
                Duration = "瞬时",
            });

            // —— 选项2：行动3，横置；消灭所有其上有 ≥3 个时间标志的角色和附属，每消灭一个抓一张牌 ——
            var opt2 = new CouncilActivationOption { OptionName = "横置·3费·消灭所有具≥3时间标志的角色与附属并等量抓牌" };
            opt2.EffectDataList.Add(new EffectData
            {
                EffectId = "MSJC013_2_DestroyTimeMarked",
                Timing = EffectTiming.CouncilActivated,
                ActivateWindow = "行动",
                ActivateCondition = "横置本角色",
                Cost = 3,
                Forced = false,
                Target = null,                       // 无卡目标（集合由动词自行按"其上有标志"收）
                Actions = new Array<EffectAction>
                {
                    // 集合＝全部争夺区内"其上时间标志 ≥3"的角色与附属；按**实际消灭数**抓等量牌（用户口径）
                    new EffectAction
                    {
                        Verb = ActionVerb.DestroyWithMinCounters,
                        Args = new Array<string> { "时间标志", "3" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "消灭＝走死亡统一出口（照常触发死亡时机）；随后按**实际被消灭**的张数抓等量牌。",
            });

            ActivationOptions.Add(opt1);
            ActivationOptions.Add(opt2);
        }
    }
}
