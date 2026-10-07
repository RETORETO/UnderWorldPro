using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC067 约翰·F·纳特里 效果（角色 / 人类·政治家 / 国家机构）。
    /// 卡文：领袖 公开 声望 持续：本地区在对抗阶段时额外进行一次势力对抗。
    ///  · **领袖**（持续关键字 + `LeaderRule` 唯一性）、**公开**（不能被秘密派遣为暗藏者）、
    ///    **声望**（规则本体由 `RenownRule` 引擎级消费）；
    ///  · **持续（本轮交付）**：`StaticKind.ExtraAreaPowerContest` 静止规则——本卡所在地区在对抗阶段中
    ///    **额外进行一次势力对抗**。消费点＝`TurnManager.AdvancePhase` 的 `CombatPower` 分支：
    ///    当前地区本回合还没追加过 → **原地重入** `CombatPower`（同地区、同对抗类型再结算一次），
    ///    跑完再推进地区游标；**每地区每回合至多追加一次**（`_extraPowerDoneAreas`，重置步骤清空）。
    /// </summary>
    public static class JC067_yuehanFnateli_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC067_yuehanFnateli", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC067_A_Leader";
            data.EffectDataList.Add(leader);

            var publicKw = KeywordEffects.Public();
            publicKw.EffectId = "JC067_B_Public";
            data.EffectDataList.Add(publicKw);

            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = "JC067_C_Renown";
            data.EffectDataList.Add(renown);

            // 【2026-09 用户口径·声望改触发】"声望"的规则本体＝**触发效果**（该地区所有对抗结束后，
            // 本地区声望角色较多的一方放置一个势力标志；不依赖对抗胜负、只数声望角色）。
            // 卡面固有关键字不会被 `RuntimeTriggerTemplates` 通道重复合成，故这里必须**显式**声明触发条目。
            var renownTrigger = KeywordEffects.Renown();
            renownTrigger.EffectId = "JC067_C2_RenownTrigger";
            data.EffectDataList.Add(renownTrigger);

            // 持续：本地区在对抗阶段额外进行一次势力对抗
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC067_D_ExtraPowerContest",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,             // 本卡在争夺区时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "本地区在对抗阶段时额外进行一次势力对抗",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.ExtraAreaPowerContest },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只要本卡在争夺区，该地区的势力步骤完成后会**再结算一次**势力对抗（＝一次正常对抗：可再放势力标志、"
                        + "声望照常触发）；每回合每地区只追加一次。",
            });
        }
    }
}
