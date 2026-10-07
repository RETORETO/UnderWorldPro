using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC002 巨石阵看护人 效果（自原 JC002_jushizhenkanhuren 构造函数迁移，逻辑不变）。
    /// 现身触发：横置本地区的目标角色。
    /// 【2026-09 整体复查·修复】**时机必须是 `UncoverTrigger`（现身触发）**：旧实现用 `EnterTrigger`
    /// （进场触发）⇒ 除了"现身"以外，**正面打进争夺区**时也会横置目标（卡文只写"现身触发"）。
    /// 引擎 `CardManager.RevealCard` 在现身时显式广播 `UncoverTrigger`（并复用进场触发链），
    /// 故改用 `UncoverTrigger` 后现身照常触发、正面进场不再误触发（与 JC011 现身触发同款）。
    /// </summary>
    public static class JC002_jushizhenkanhuren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC002_jushizhenkanhuren", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC002_EnterTap",
                Timing = EffectTiming.UncoverTrigger,   // 【2026-09 复查】现身触发（原 EnterTrigger 会正面进场误触发）
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = true,
                    IsLocalZone = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                },
            });
        }
    }
}
