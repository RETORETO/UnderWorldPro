using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC123 饮魂者朱迪 效果（角色 / 吸血鬼·法师）。
    /// 卡文：快速行动2：在目标角色或暗藏者上放置一个锁定标记。
    ///      持续：只要饮魂者朱迪在场，所有具有锁定标记的角色或暗藏者失去所有［黑星］和［白星］能力图标。
    ///  · **快速行动2 放锁定标记**：角色激活（快速 + 费用2）+ `PlaceCounter`（目标角色或暗藏者）；
    ///  · **持续（本轮交付）**：`EffectData.IconSuppression`（**持续型"失去图标"**）——
    ///    本卡在争夺区期间，凡带有**锁定标记/锁定标志**（卡库里同一概念的两种写法，二者等效）的角色/暗藏者，
    ///    其【黑星/白星】图标的**当前有效贡献被抹掉**；由 `EffectEngine.ReapplyIconSuppressions`
    ///    统一重算（先精确还原、再按当前局面施加），故**标志消失 / 来源离场即恢复**。
    /// </summary>
    public static class JC123_yinhunzhezhudi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC123_yinhunzhezhudi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC123_1_PlaceLockMarker",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 2,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    IncludeCovert = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "锁定标记", "1", "目标" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "快速行动2：在目标角色或暗藏者上放置一个锁定标记。",
            });

            // 持续：具锁定标记者失去所有［黑星］和［白星］能力图标
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC123_2_LockedLoseStarIcons",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,               // 本卡在争夺区时生效（"在场"）
                ContinuousPrereq = "常驻",
                ContinuousEffect = "具有锁定标记的角色或暗藏者失去所有［黑星］和［白星］能力图标",
                IconSuppression = new IconSuppressionSpec
                {
                    Icons = new Array<string> { "黑星", "白星" },
                    RequireCounters = new Array<string> { "锁定标记", "锁定标志" },   // 同一概念的两种卡库写法
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只要朱迪在场：带锁定标记/标志的角色与暗藏者不再提供任何势力（黑星/白星全部失效）；"
                        + "标志被移除或朱迪离场即恢复。",
            });
        }
    }
}
