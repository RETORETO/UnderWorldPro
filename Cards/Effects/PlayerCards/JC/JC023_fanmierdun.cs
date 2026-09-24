using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC023 范·弥尔顿 效果（角色 / 人类·猎手 / 猎魔人）。
    /// 卡文：领袖 威名 机动 袭击2 持续：所有本地区的敌方暗藏者不能参与势力对抗。
    ///  · **领袖**（持续关键字 + `LeaderRule` 唯一性）、**威名**（战斗对抗获胜放势力标志）、
    ///    **机动**（对抗阶段开始可移动）、**袭击2**（现身进场对同地区角色 2 点伤害）——既有通道；
    ///  · **持续（本轮交付）**：`StaticKind.ExcludeCovertPower` 静止规则（`Timing = Continuous` + 前提常驻）——
    ///    本卡所在地区内**非本卡控制者**的暗藏者**不参与势力对抗**（其势力贡献 1+先手势力 归零）。
    ///    消费点＝`AreaStatsCalculator`（对抗结算与 UI 展示共用的**单一真相源**），故显示与结算同步一致。
    /// </summary>
    public static class JC023_fanmierdun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC023_fanmierdun", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC023_A_Leader";
            data.EffectDataList.Add(leader);

            var weiMing = KeywordEffects.WeiMing();
            weiMing.EffectId = "JC023_B_WeiMing";
            data.EffectDataList.Add(weiMing);

            var maneuver = KeywordEffects.Maneuver();
            maneuver.EffectId = "JC023_C_Maneuver";
            data.EffectDataList.Add(maneuver);

            var strike = KeywordEffects.Strike(2);
            strike.EffectId = "JC023_D_Strike2";
            data.EffectDataList.Add(strike);

            // 持续：所有本地区的**敌方**暗藏者不能参与势力对抗（地区级静止规则）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC023_E_ExcludeEnemyCovertPower",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,          // 角色在争夺区时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "本地区敌方暗藏者不能参与势力对抗",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.ExcludeCovertPower },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只要范·弥尔顿在争夺区，该地区内**对手**的暗藏者势力贡献归零（不影响本方暗藏者、不影响调查/战斗对抗）。",
            });
        }
    }
}
