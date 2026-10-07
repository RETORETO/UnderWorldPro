using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC052 终焉的诗寇蒂 效果（角色）。
    /// 卡文：领袖 进场触发：消灭所有本地区的其他角色和暗藏者。
    ///
    /// 实现（P3）：
    ///  · 领袖：持续关键字 + `LeaderRule` 唯一性规则；
    ///  · **群体动作（K2）**：`TargetSpec{Kind=Character, IncludeCovert=true, MassMode=AllInArea, CanTargetSelf=false}`
    ///    → 作用地区＝本次结算的目标地区（进场触发＝进入的地区＝自身的本地区）；
    ///    动作 `Destroy` 对集合内每张卡各执行一次，**在一次结算内全部完成**
    ///    （期间处于堆叠结算的原子段，其它玩家不能行动；各目标死亡产生的死亡触发按既有触发管线收集，多条由玩家排序）。
    /// </summary>
    public static class JC052_zhongyandeshikoudi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC052_zhongyandeshikoudi", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC052_A_Leader";
            data.EffectDataList.Add(leader);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC052_1_DestroyAllOthersInArea",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,   // 角色
                    IncludeCovert = true,          // "…和暗藏者"
                    MassMode = MassMode.AllInArea, // 【K2】本地区所有符合条件的卡
                    CanTargetSelf = false,         // 卡文"**其他**角色和暗藏者" → 排除自身
                    NeedsSelection = false,        // 群体动作不选单目标
                    // 【2026-09 修复·用户口径】"…其他角色和暗藏者"里的"其他"＝**与收集该触发时同一张卡**：
                    // 若来源卡在此期间**版本号已变**（如进场触发 A 收集后本卡翻面/重新现身），
                    // 现存的这张"自己"对老触发而言已不是同一张卡 ⇒ **应当被消灭**。
                    // （用户举例：JC052 进场 → 进场A 收集 → 翻面 → 重新现身 → 进场B；进场A 结算时应消灭 JC052。）
                    VersionSkipsSelf = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
                // 【2026-09 修复·用户口径】卡文没有"你可以"⇒ **非强制**（用户实测："进场触发写成强制触发了"）。
                // 注意：`Forced` 只影响"是否可在触发排序窗放弃"，与"消灭范围"无关（范围见 `Target.VersionSkipsSelf`）。
                Forced = false,
                Duration = "瞬时",
                FaqNote = "进场时消灭本地区除自身外的所有角色与暗藏者（群体动作一次结算完成）。",
            });
        }
    }
}
