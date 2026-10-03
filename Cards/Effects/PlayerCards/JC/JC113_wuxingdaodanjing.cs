using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC113 无形捣蛋精 效果（角色；`Cost = 2`，幽灵；前缀「灵体」）。
    /// 卡文：灵体 进场强制触发：每位玩家各牺牲一个附属。
    /// 实现（口径 A11）：`SacrificeChoice{"附属","每位玩家","任意"}` ——
    /// 按先手优先逐个自选（后选者可见先选者的选择）→ 全部选定后一起牺牲（不分死亡先后）；
    /// 无合法候选者跳过；牺牲＝死亡 → 照常触发死亡时机。
    /// 前缀「灵体」由引擎级伤害防止承担。
    /// </summary>
    public static class JC113_wuxingdaodanjing_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC113_wuxingdaodanjing", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 修复·灵体同类排查】卡文以**灵体**开头，但脚本此前没有授予【灵体】关键词
            // ⇒ `DamageRules.IsSpirit` 为假 ⇒ 灵体的"我方魔法领域图标更多则防止伤害"永不生效。
            // 现按 JC077/JC111 同款"持续关键字授予"补齐（消费点＝`CardManager.DamagePreventer`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC113_B_Spirit",
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
                EffectId = "JC113_A_EnterEachPlayerSacrificesAttachment",
                Timing = EffectTiming.EnterTrigger,
                Forced = true,                       // 卡文"强制触发"
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.SacrificeChoice,
                        Args = new Array<string> { "附属", "每位玩家", "任意" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "进场时：每位玩家各自选择牺牲一个附属（先手先选、之后一起牺牲）；没有附属的玩家不受影响。",
            });
        }
    }
}
