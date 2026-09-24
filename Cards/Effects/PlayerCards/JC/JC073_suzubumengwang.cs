using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC073 苏族捕梦网 效果（附属卡）。
    /// 卡文：结附于目标角色 持续：受此结附的角色获得［白眼］和屏障（不能被敌方的卡牌或能力指定为目标）。
    ///
    /// 实现（P3·D）：持续授予落点＝**宿主**（`GrantToHost=true`）：
    ///  · ［白眼］图标（`Icon:白眼`）；
    ///  · **屏障**＝静止标记 `StaticKind.Barrier`（"不能被敌方的卡牌或能力指定为目标"，由 `TargetValidity`
    ///    的目标候选/结算复检消费）。屏障是"宿主获得"的，故与白眼一起授予宿主，而非授予捕梦网自身。
    /// 无宿主（未结附于卡）时不授予。
    /// </summary>
    public static class JC073_suzubumengwang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC073_suzubumengwang", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC073_A_GrantWhiteEyeAndBarrierToHost",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                GrantToHost = true,           // 授予**结附的角色**
                ContinuousEffect = "受此结附的角色获得［白眼］与屏障",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "白眼", Amount = 1 },
                    // 屏障＝运行期关键字（`TargetValidity.IsBarrierBlocking` 消费"授予的关键字 屏障"）：
                    // 授予**宿主**后，敌方来源不能把宿主指定为目标（"先被指定、后获屏障"在结算复检时判不合法）。
                    new EffectGrant { Kind = "Keyword", Value = "屏障", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间宿主获得白眼与屏障（不能被敌方卡牌/能力指定为目标）。",
            });
        }
    }
}
