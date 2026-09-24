using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC099 徘徊的鬼魂 效果（角色 / 灵体 / 梦魇?）。
    /// 卡文：灵体
    ///      现身触发：将你牌库顶的一张牌封印在本地区的目标敌方角色上。
    ///      持续：只要徘徊的幽灵在场且未横置，所有其上有牌封印的敌方角色失去魔法领域图标。
    ///  · **灵体**：关键词持续授予（引擎级 `DamageRules` 消费）；
    ///  · **现身触发**：P2 `ActionVerb.Seal` 的"牌库顶 → 宿主"形态（宿主＝本地区敌方角色）；
    ///  · **持续（本轮交付）**：`EffectData.DomainSuppression`（**持续型"失去魔法领域图标"**）+
    ///    `PrereqSpec.Kind = SelfUntapped`（卡文"只要…**未横置**"，横置即自动失效、竖置恢复）；
    ///    受影响对象＝**敌方**、**其上有牌封印**的**角色**（全局，卡文无"本地区"限定）；
    ///    由 `EffectEngine.ReapplySuppressions` 统一重算（先精确还原、再按局面施加）——封印解除/来源横置或离场即恢复。
    /// </summary>
    public static class JC099_paihuaideguihun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC099_paihuaideguihun", Build);

        private static void Build(PlayerCardData data)
        {
            // 灵体（关键词：与 JC111 同款"持续关键字授予"）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC099_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC099_2_SealDeckTopOnEnemy",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IsLocalZone = true,          // 本地区
                    CanTargetSelf = false,
                    IncludeCovert = false,       // 卡文只写"敌方角色"
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Seal, Args = new Array<string> { "牌库顶", "目标" } },
                },
                Duration = "瞬时",
                FaqNote = "现身时把牌库顶那张牌封印在本地区一个目标敌方角色上。",
            });

            // 持续：其上有牌封印的敌方角色失去魔法领域图标（只要本卡在场且未横置）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC099_3_SealedEnemyLoseDomains",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                PrereqSpec = new ContinuousPrereqSpec { Kind = PrereqKind.SelfUntapped },   // "只要…未横置"
                ContinuousPrereq = "自身未横置",
                ContinuousEffect = "其上有牌封印的敌方角色失去魔法领域图标",
                DomainSuppression = new DomainSuppressionSpec
                {
                    Victim = DomainSuppressionVictim.EnemyCharacterWithSealedCards,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本卡在场且未横置时：所有其上有牌封印的敌方角色失去全部魔法领域图标；"
                        + "本卡横置、离场，或封印被解除 → 立即恢复。",
            });
        }
    }
}
