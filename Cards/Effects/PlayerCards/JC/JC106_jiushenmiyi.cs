using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC106 酒神秘仪 效果（事务卡 / 法术·精魂 / 梦境行者）。
    /// 卡文：快速行动：本回合中，目标角色上的所有［黑星］和［白星］能力图标都替换为［黑刀］，并获得灵体。
    ///
    /// 实现（**K20 第一批：图标替换＝属性替换**，用户 2026-09 口径）：
    ///  · `ActionVerb.ReplaceIcon{"黑星,白星", "黑刀", "目标"}`：把目标卡上**当前有效的**势力相关贡献
    ///    （`CurrentPower` ＝ 印刷黑星 + 印刷白星(先手时) + 授予势力；以及授予的『先手势力』）
    ///    **整块换成『战斗』**（黑刀）——替换后该卡不再提供任何【势力】；
    ///    整套替换在**效果内部一次做完**（原子锁期间不会插入其它触发）；
    ///  · `Gain{"灵体","1","目标"}`：关键词也按 `Duration="直到回合结束"`（时效项，回合结束自动撤销）；
    ///  · 两条都基于 `Duration="直到回合结束"`（卡文"本回合中"）→ 时效栈到期精确还原。
    /// </summary>
    public static class JC106_jiushenmiyi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC106_jiushenmiyi", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),      // 目标角色（不分敌我）
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC106_1_ReplaceStarsToBladeAndGainSpirit",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,           // 快速行动
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    // 黑星/白星 → 黑刀（属性替换）
                    new EffectAction
                    {
                        Verb = ActionVerb.ReplaceIcon,
                        Args = new Array<string> { "黑星,白星", "黑刀", "目标" },
                    },
                    // 并获得【灵体】（同样只到回合结束）
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "灵体", "1", "目标" },
                    },
                },
                Duration = "直到回合结束",
                FaqNote = "本回合中：目标角色的所有［黑星］与［白星］图标整体换成［黑刀］（该卡不再提供任何势力），并获得【灵体】。",
            });
        }
    }
}
