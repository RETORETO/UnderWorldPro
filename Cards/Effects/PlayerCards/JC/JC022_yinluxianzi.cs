using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC022 引路仙子 效果（角色）。
    /// 卡文：灵体 触发：当你赢得引路仙子所参与的战斗对抗时，抓一张牌。
    ///
    /// 实现（**用户 2026-09 口径**："参与了该次战斗"按**近似**判定 ＝ **位于战斗地区且属于获胜方**）：
    ///  · 时机 `OnCombatWon`（战斗对抗分出胜者后广播，上下文＝胜者玩家 `TriggeringPlayerId` + 战斗地区 `TargetAreaId`）；
    ///  · 条件 `WinnerIsSelfInSameArea`（既有谓词，复用同一实现）：
    ///    ① 本卡控制者 ＝ 获胜方 ② 本卡所在地区 ＝ 获胜地区 → 卡的"参与该次战斗"由此**近似**成立；
    ///  · 动作 `Draw{1}`（这里是**真·抓牌** → 会广播 `OnDraw`）。
    ///
    /// 前缀「灵体」由引擎级伤害防止承担（`DamageRules` + `CardManager.DamagePreventer`），无需卡级效果。
    /// </summary>
    public static class JC022_yinluxianzi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC022_yinluxianzi", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 修复·用户实测"JC022 灵体没有生效"】卡文以**灵体**开头，但脚本此前只写了
            // OnCombatWon 触发、**没有授予【灵体】关键词** ⇒ `DamageRules.IsSpirit` 为假 ⇒
            // `IsSpiritPrevention`（我方魔法领域图标 > 对方时防止伤害）永不成立，战斗伤害照常落地。
            // 现按 JC077/JC111 同款"持续关键字授予"补齐（消费点＝`CardManager.DamagePreventer`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC022_S_Spirit",
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
                EffectId = "JC022_A_DrawOnCombatWonInSameArea",
                Timing = EffectTiming.OnCombatWon,
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                Forced = false,   // 卡文"当…时"（无"可以"，但触发本身仍走可选窗口，交由玩家在排序窗决定）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "本方赢下本卡所在地区的战斗对抗时抓一张牌（本卡参与该次战斗的判定口径＝"
                        + "『位于战斗地区且属于获胜方』，与地区内其他本方卡一致）。",
            });
        }
    }
}
