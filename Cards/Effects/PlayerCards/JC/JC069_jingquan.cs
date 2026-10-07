using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC069 警犬 效果（角色 / 狗 / 国家机构）。
    /// 卡文：不能被装备结附；现身触发：在目标角色或暗藏者上放置一个锁定标志；
    ///      快速行动2：将警犬移动到目标具有锁定标志的角色或暗藏者所在的地区，警犬获得［黑眼］直到回合结束。该能力每回合只能发动一次
    ///  · **不能被装备结附**：`KeywordEffects.CannotBeAttached()`（既有静止标记）；
    ///  · **现身触发**：`ActionVerb.PlaceCounter`（对象＝目标角色或暗藏者，`IncludeCovert=true`）；
    ///  · **快速行动2（本轮交付）**：
    ///    ① 目标筛选＝新限制 **`TargetRestriction.HasCounter`**（`Param="锁定标志"`，判定 `Counters ≥ 1`）
    ///       —— 只在**带锁定标志**的角色/暗藏者中选（不限地区：警犬要"移动过去"）；
    ///    ② `Move{"自身","目标所在地区"}`（本轮新增的**目的地写法**：按目标卡**当前**所在地实时解析）；
    ///    ③ `Gain{"黑眼","1"}` + `Duration="直到回合结束"` → 时效项到期自动撤销（`ExpireTurnEndEffects`）；
    ///    ④ `Cost = 2`（卡文"行动2"）+ `ActivateWindow = "快速"`（快速行动）+ `MaxUsesPerTurn = 1`（每回合一次，K23）。
    /// </summary>
    public static class JC069_jingquan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC069_jingquan", Build);

        private static void Build(PlayerCardData data)
        {
            var cannotAttach = KeywordEffects.CannotBeAttached();
            cannotAttach.EffectId = "JC069_A_CannotBeAttached";
            data.EffectDataList.Add(cannotAttach);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC069_1_UncoverPlaceLockMarker",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = false,
                    IncludeCovert = true,   // 卡文"目标角色或暗藏者"
                    // 【2026-09 用户口径·修复】卡文「在目标角色或暗藏者上放置一个锁定标志」**没有写"本地区"**
                    // ⇒ 用户实测报障并裁定："现在只能选本地区，我希望**放开到任意地区**"。
                    // 本引擎触发取目标默认限"同 AREASLOT"（`TurnManager.GetTargetUidsForSpec`），
                    // 故必须显式声明 `AllAreas=true`。
                    AllAreas = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { "锁定标志", "1", "目标" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "现身触发：在目标角色或暗藏者上放置一个锁定标志。",
            });

            // 快速行动2：移动到"具有锁定标志者"所在地区 + 获得黑眼直到回合结束（每回合一次）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC069_2_MoveToLockedThenGainDarkEye",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",                       // 卡文"快速行动2"
                Cost = 2,
                MaxUsesPerTurn = 1,                           // "该能力每回合只能发动一次"（K23）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IncludeCovert = true,                     // "具有锁定标志的角色**或暗藏者**"
                    CanTargetSelf = true,                     // 卡文未排除自身（自身无锁定标志时本就不会成为候选）
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.HasCounter },
                    Param = "锁定标志",
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Move,
                        Args = new Array<string> { "自身", "目标所在地区" },   // 警犬 → 目标所在地区
                    },
                    new EffectAction
                    {
                        Verb = ActionVerb.Gain,
                        Args = new Array<string> { "黑眼", "1", "自身" },     // 主体＝警犬自身（不是目标）；黑眼＝调查图标
                    },
                },
                Duration = "直到回合结束",                      // 黑眼只到回合结束
                FaqNote = "若场上没有任何带锁定标志的角色/暗藏者 → 无合法目标，本能力不可发动。",
            });
        }
    }
}
