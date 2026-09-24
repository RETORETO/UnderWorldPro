using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC108 乔治·帕佐，织梦人 效果（角色 / 人类·超能力者 / 梦境行者）。
    /// 卡文：快速行动1，横置：将你牌库顶的一张牌封印在目标角色上。
    ///      持续：只要乔治·帕佐，织梦人在场，所有其上有牌封印的敌方角色于重置步骤中不能被重置。
    ///  · **快速行动1**：激活（快速窗口，代价=横置自身，费用1）+ P2 `Seal` 的"牌库顶 → 目标"形态
    ///    （卡文没有"本地区"限定，故**不设** `IsLocalZone`）；
    ///  · **持续（本轮交付）**：`StaticKind.NoUntapWithSealedCards` 静止规则（`Timing = Continuous` + 前提常驻）——
    ///    本卡在争夺区（"在场"）期间，**其上有牌封印的敌方角色**在重置步骤中不被解除横置；
    ///    消费点＝`CardManager.UntapAll`（重置步骤的**唯一**解横置出口），判定 `EffectEngine.IsUntapBlocked`。
    ///    卡文无"本地区"限定 → **全局生效**，不按地区匹配。
    /// </summary>
    public static class JC108_qiaozhipazuozhimengren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC108_qiaozhipazuozhimengren", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC108_1_SealDeckTop",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 1,                      // 快速行动1
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    IncludeCovert = false,     // 卡文只写"目标角色"
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "牌库顶", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动1、横置自身；把你牌库顶的一张牌封印在目标角色上（无本地区限制）。",
            });

            // 持续：其上有牌封印的敌方角色，于重置步骤中不能被重置（全局静止规则）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC108_2_NoUntapSealedEnemyCharacters",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,          // 本卡在争夺区时生效（"在场"）
                ContinuousPrereq = "常驻",
                ContinuousEffect = "其上有牌封印的敌方角色于重置步骤中不能被重置",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.NoUntapWithSealedCards },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只锁『角色』且只锁对手的：本方角色、无封印的角色、附属/资产都不受影响；"
                        + "封印被移除后该角色于下次重置步骤即照常重置。",
            });
        }
    }
}
