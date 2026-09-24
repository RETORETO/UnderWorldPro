using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC004 力场法师 效果（角色）。
    /// 卡文：进场触发：选择本地区的目标角色。横置该角色。若该角色已横置，则改为将其移回其拥有者手上。
    ///
    /// 实现（P3·K17 条件式动作分支，零新机制）：
    ///  · 触发时机 `EnterTrigger`（进场触发）；
    ///  · 目标 `TargetSpec{Kind=Character, IsLocalZone=true}`＝**本地区**的目标角色（本地区由入栈快照基准判定）；
    ///  · 条件式二选一（用户 2026-09 口径的既有实现 `EffectCondition.Predicate` + `ElseBranch`）：
    ///    条件谓词 `已横置` —— **条件成立 → 走 `Actions`**，不成立 → 走 `ElseBranch`：
    ///      · 已横置 → `Actions = Bounce`（移回拥有者手牌）；
    ///      · 未横置 → `ElseBranch = Tap`（横置该角色）。
    /// </summary>
    public static class JC004_lichangfashi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC004_lichangfashi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC004_1_TapOrBounceLocalCharacter",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition
                {
                    Predicate = "已横置",   // 条件成立（目标已横置）→ 走下面的 Actions
                    ElseBranch = new Array<EffectAction>
                    {
                        // 未横置 → 横置该角色
                        new EffectAction { Verb = ActionVerb.Tap },
                    },
                },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    IsLocalZone = true,     // "本地区"
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    // 已横置 → 改为移回其拥有者手上
                    new EffectAction { Verb = ActionVerb.Bounce },
                },
                Duration = "瞬时",
                FaqNote = "目标角色未横置→横置之；已横置→改为移回其拥有者手牌（两动作二选一，走条件分支）。",
            });
        }
    }
}
