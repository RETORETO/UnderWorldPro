using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC084 街头混混 效果（角色）。
    /// 卡文：持续：若位于有本方势力标志的地区，街头混混获得［白星］。 持续：若位于有敌方势力标志的地区，街头混混获得［白眼］。
    ///
    /// 实现（P3·B）：两条**独立**的持续效果，各自声明新前提 `PrereqKind.AreaMarkOwner`
    /// （`Param`＝标志归属"自己"/"对手"，`Amount` 缺省 1＝该地区至少有 1 个该归属的势力标志）：
    ///  · 本方标志地区 → ［白星］；
    ///  · 敌方标志地区 → ［白眼］。
    /// 两者可同时成立（该地区双方都有标志时同时获得两个图标）；前提随势力标志增减实时重判。
    /// </summary>
    public static class JC084_jietouhunhun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC084_jietouhunhun", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC084_A_OwnMarkWhiteStar",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "所在地区有本方势力标志",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.AreaMarkOwner,
                    Param = "自己",
                    Amount = 1,
                },
                ContinuousEffect = "所在地区有本方势力标志 → 获得［白星］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "白星", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "所在地区有本方势力标志时获得白星；标志增减时实时开关。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC084_B_EnemyMarkWhiteEye",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "所在地区有敌方势力标志",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.AreaMarkOwner,
                    Param = "对手",
                    Amount = 1,
                },
                ContinuousEffect = "所在地区有敌方势力标志 → 获得［白眼］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "白眼", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "所在地区有敌方势力标志时获得白眼；标志增减时实时开关。",
            });
        }
    }
}
