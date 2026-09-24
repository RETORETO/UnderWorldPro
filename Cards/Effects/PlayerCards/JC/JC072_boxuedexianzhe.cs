using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC072 博学的贤者 效果（角色，人类/学者，圣贤；调查 2）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者） 触发：当你赢得其所参与的调查对抗时，
    ///      博学的贤者获得声望直到回合结束。
    ///
    /// 实现（P3：**新增调查对抗获胜触发时机**）：
    ///  · **公开** ＝ `KeywordEffects.Public()`（手牌期约束"秘密派遣为暗藏者"）；
    ///  · **触发** ＝ `EffectTiming.OnInvestigationWon`（新时机，`CombatManager` 调查对抗分出胜者、
    ///    且**调查窗口关闭后**广播；平局/0:0 无胜者 → 不广播）；
    ///  · 条件 `WinnerIsSelfInSameArea` ＝ "本卡控制者获胜 ∧ 本卡就在该对抗所在地区" → 正好表达卡文
    ///    "**其所参与**的调查对抗"（不参与该地区对抗的贤者不会被误触发）；
    ///  · **获得声望直到回合结束** ＝ `Gain{关键词, 声望, 自身}` + `Duration="直到回合结束"`
    ///    （时效登记进 `TimedMod`，回合结束自动还原）；声望的**规则本体**由 `RenownRule` 承担（势力对抗结束时
    ///    该地区声望角色较多的一方 +1 势力标志）。
    /// </summary>
    public static class JC072_boxuedexianzhe_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC072_boxuedexianzhe", Build);

        private static void Build(PlayerCardData data)
        {
            var publicEffect = KeywordEffects.Public();
            publicEffect.EffectId = "JC072_A_Public";
            data.EffectDataList.Add(publicEffect);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC072_1_RenownOnInvestigationWon",
                Timing = EffectTiming.OnInvestigationWon,
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // Args[0]=授予物（未识别为图标/属性 → 走关键字通道）；Args[2]="自身"＝本角色
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "声望", "1", "自身" } },
                },
                Forced = false,                       // 卡文"当…时"触发（非强制），可由玩家在排序窗放弃
                Duration = "直到回合结束",
                FaqNote = "赢得其所参与的调查对抗 → 本角色获得【声望】直到回合结束（时效到期自动失去）。",
            });
        }
    }
}
