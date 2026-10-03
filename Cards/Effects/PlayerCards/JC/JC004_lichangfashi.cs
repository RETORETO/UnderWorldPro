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
    /// 实现（P3·K17 条件式动作分支）：
    ///  · 触发时机 `EnterTrigger`（进场触发）；
    ///  · 目标 `TargetSpec{Kind=Character, IsLocalZone=true}`＝**本地区**的目标角色（本地区由入栈快照基准判定）；
    ///  · 条件式二选一（`EffectCondition` + `ElseBranch`）：
    ///      · `Predicate = "Always"` —— **门禁恒真**（本效果没有门禁）；
    ///      · `BranchPredicate = "已横置"` —— **分支选择**（对"玩家已选定的目标"求值）：
    ///        成立 → `Actions`（`Bounce`：移回拥有者手牌）；不成立 → `ElseBranch`（`Tap`：横置该角色）。
    ///
    /// ⚠ **2026-09 修复（用户实测："JC004 进场后连进堆叠都没有"）**：
    ///   原实现把"已横置"写在 `Predicate` 上 ⇒ 被当成**收集阶段的门禁**——`TriggerRegistry` 收集时
    ///   还没有目标（传进来的上下文卡是 JC004 自己、未横置）⇒ 条件不满足 ⇒ **触发在收集阶段就被丢掉**，
    ///   永远进不了堆叠（手工 `ResolveEffect` 的既有用例因此掩盖了该缺陷）。
    ///   "已横置"是**对已选定目标**的分支判定，必须走 `BranchPredicate`（与 JC031 同款）。
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
                    Predicate = "Always",          // 门禁恒真（收集阶段不得按"目标状态"过滤）
                    BranchPredicate = "已横置",     // 分支：对**已选定目标**求值；成立 → Actions
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
                    // 【2026-09 修复·用户实测"进场后没有产生任何效果"】卡文只写"选择本地区的目标角色"，
                    // **没有排除自身** ⇒ 按本项目既有约定（卡文未写"另一个"即含自己；见 JC008/JC069 同款注释）
                    // 必须允许指定自身。此前 `CanTargetSelf=false`：当力场法师是本地区**唯一**的角色时
                    // （实测日志：EnterTrigger 扫描=1，只有它自己在场上）候选为空 ⇒ 该进场触发被当作
                    // "无合法目标"直接跳过，玩家看不到任何效果；允许自身后 ⇒ 可取自己为目标并横置自己。
                    CanTargetSelf = true,
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
