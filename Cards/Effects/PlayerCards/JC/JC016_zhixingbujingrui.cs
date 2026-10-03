using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC016 执行部精锐 效果（角色）。
    /// 卡文：撤回 杀伤1 持续：若所在的地区没有敌方角色，执行部精锐就获得［白眼］［黑星］。
    /// 实现（效果级推进）：
    ///      · 撤回 → `KeywordEffects.Withdraw()`，由 `AreaManager.ResolveAreaWon` 消费（回 OWNER 手牌而非牌库底）；
    ///      · 杀伤1 → `KeywordEffects.Kill(1)`（Attribute="杀伤"），由 `CombatManager` 在战斗对抗可分配伤害上累加；
    ///      · **持续：所在地区没有敌方角色 → 获得［白眼］［黑星］**（P3·B：新前提 `PrereqKind.NoEnemyCharacterInArea`
    ///        ＋`ContinuousGrants` 白眼/黑星；前提随"敌方角色进出该地区"实时重判——`ZoneManager.FireMoved` 对双方都重算）。
    /// </summary>
    public static class JC016_zhixingbujingrui_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC016_zhixingbujingrui", Build);

        private static void Build(PlayerCardData data)
        {
            var withdraw = KeywordEffects.Withdraw();
            withdraw.EffectId = "JC016_A_Withdraw";
            data.EffectDataList.Add(withdraw);

            var kill = KeywordEffects.Kill(1);
            kill.EffectId = "JC016_B_Kill1";
            data.EffectDataList.Add(kill);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC016_C_NoEnemyInAreaIcons",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "所在地区没有敌方角色",
                PrereqSpec = new ContinuousPrereqSpec { Kind = PrereqKind.NoEnemyCharacterInArea },
                ContinuousEffect = "所在地区没有敌方角色 → 获得［白眼］［黑星］",
                // 【2026-09 修复·用户实测"持续效果给自己增加能力图标（先手调查/势力）没有生效"】
                // 持续授予必须写**属性**通道（`Attribute` + 属性名：白眼=先手调查 / 黑星=先手势力），
                // 与 JC001（白眼）/JC085（白星）同款；此前只写 `Icon`（图标存在性）⇒ `HasIcon` 为真但
                // **数值完全没加**（先手调查/先手势力仍为 0）。现两条通道都写：
                //  · `Attribute` → 真正改数值（`AreaStatsCalculator` 读 `GetAttribute("先手调查"/"先手势力")`）；
                //  · `Icon` → 图标存在性（UI 展示 /"有图标"类判定）。
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "先手调查", Amount = 1 },   // 白眼＝先手调查
                    new EffectGrant { Kind = "Icon", Value = "白眼", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "先手势力", Amount = 1 },   // 黑星＝先手势力
                    new EffectGrant { Kind = "Icon", Value = "黑星", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只要其所在地区没有敌方（正面）角色，就获得白眼与黑星；敌方角色进入/离开该地区时实时开关。",
            });
        }
    }
}
