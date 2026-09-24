using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC031 唤灵师 效果（角色，吸血鬼/法师，王座会）。
    /// 卡文：触发：每当另一个本方角色死亡时，你可以令目标玩家弃一张手牌。
    ///      若死亡的是吸血鬼角色，则改为可以令目标玩家随机弃一张手牌。该能力每回合只能发动一次。
    ///
    /// 实现（全部复用既有原语 + 本轮补齐"门禁 + 分支"双条件）：
    ///  · **触发时机** ＝ `OnCardDied`（死亡行为时、卡离场之前广播；P1 已接）；
    ///  · **门禁条件** ＝ `DiedAllyCharacter`（"另一个本方角色"：是角色 ∧ 控制者＝来源控制者 ∧ **非来源自身**）；
    ///  · **分支条件** ＝ `DiedVampireCharacter`（**新增谓词**：死亡卡当前实际子类别含「吸血鬼」，
    ///    与 `TargetRestriction.CurrentSubCategory` 同口径，含运行期授予）→ 满足走 `Actions`（随机弃），
    ///    不满足走 `ElseBranch`（弃牌者自选）；
    ///  · **目标玩家** ＝ K24（`Kind=Player` + `Enemy`）：触发入堆叠前经独立选择窗口就地选定；
    ///  · **弃牌** ＝ `Discard{1, "目标玩家", 随机/自选}`（`ResolvePlayerSide` 支持"目标玩家"）；
    ///  · **每回合只能发动一次** ＝ `MaxUsesPerTurn = 1`（K23：结算前检查并登记）；
    ///  · **"你可以"** ＝ `Forced = false`（非强制触发，玩家可在触发排序窗放弃；也可在选择目标玩家时取消）。
    /// </summary>
    public static class JC031_huanlingshi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC031_huanlingshi", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC031_1_DiscardOnAllyDeath",
                Timing = EffectTiming.OnCardDied,
                MaxUsesPerTurn = 1,      // 该能力每回合只能发动一次
                Forced = false,          // "你可以" → 非强制
                Condition = new EffectCondition
                {
                    Predicate = "DiedAllyCharacter",          // 门禁：另一个本方角色死亡
                    BranchPredicate = "DiedVampireCharacter", // 分支：若死亡的是吸血鬼角色
                    ElseBranch = new Array<EffectAction>
                    {
                        // 非吸血鬼：由**目标玩家自己**选一张弃（自选）
                        new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "目标玩家", "自选" } },
                    },
                },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },
                },
                Actions = new Array<EffectAction>
                {
                    // 吸血鬼：改为**随机**弃一张（不由该玩家挑选）
                    new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "目标玩家", "随机" } },
                },
                Duration = "瞬时",
                FaqNote = "另一个本方角色死亡 → 令目标对手弃一张手牌；若死亡者本身是吸血鬼角色，则改为随机弃。每回合一次。",
            });
        }
    }
}
