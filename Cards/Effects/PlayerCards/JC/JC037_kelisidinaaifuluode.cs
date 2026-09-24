using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC037 克里斯蒂娜·爱弗罗德 效果（角色）。
    /// 卡文：领袖 持续：你每操控一个暗藏者，克里斯蒂娜·爱弗罗德就获得一个［黑眼］。
    /// 实现（效果级推进）：
    ///  · **领袖**：持续关键字 + `LeaderRule` 唯一性规则；
    ///  · **持续：每操控一个暗藏者 → 一个［黑眼］**（P3·F 按计数缩放的持续授予）：
    ///    `PrereqSpec.ScaleExpr = "计数:区域:争夺区:暗藏者:自己"` → N＝本方操控的暗藏者数，
    ///    授予量＝`Icon:黑眼 ×N`；N=0 时前提不成立（无黑眼）。暗藏者进出/翻面时实时重判。
    /// </summary>
    public static class JC037_kelisidinaaifuluode_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC037_kelisidinaaifuluode", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC037_A_Leader";
            data.EffectDataList.Add(leader);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC037_B_BlackEyePerCovert",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "每操控一个暗藏者获得一个黑眼",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    // 【P3·F】按计数缩放：本方操控的暗藏者数量（暗藏只存在于场上）
                    ScaleExpr = "计数:区域:争夺区:暗藏者:自己",
                },
                ContinuousEffect = "你每操控一个暗藏者，克里斯蒂娜·爱弗罗德就获得一个［黑眼］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Icon", Value = "黑眼", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "黑眼数量＝你操控的暗藏者数量（暗藏者在场/离场、翻面时实时重算）。",
            });
        }
    }
}
