using Godot;
using Godot.Collections;
using Script.Enums;

namespace Script.Data.Effects
{
    /// <summary>
    /// 单动作关键词效果库（KeywordEffects）。
    ///
    /// 将"机动""公开"等单动作关键词封装为可复用的工厂方法，供各卡脚本直接引用，
    /// 避免每张带相同关键词的卡都手写一遍重复结构，也便于统一语义与后续维护。
    ///
    /// 设计约定：
    ///  - 每个方法返回一个【新构造】的 EffectData（含其 TargetSpec / Actions 子对象），
    ///    调用方直接 EffectDataList.Add(KeywordEffects.Xxx()) 即可；
    ///  - 方法默认不带 EffectId（由调用方按"卡号_序号_关键词"规范补写），降低耦合；
    ///  - 后续若关键词语义演化（如"创伤袭击"作为整体动作），在此处新增对应方法，
    ///    而非在每张卡里解析两个关键词。
    /// </summary>
    public static class KeywordEffects
    {
        /// <summary>
        /// 机动（Maneuver）：对抗阶段开始时（ManeuverTrigger），选择另一地区并将角色移动过去。
        /// 对应卡文："机动（在对抗阶段开始时，你可以将该角色移动至另一个地区。）"
        /// 由 TurnManager 在对抗前 Emit(ManeuverTrigger) → TriggerRegistry 收为 ManeuverTrigger，
        /// 引擎推导目标地区（IsAreaTarget），由 EffectEngine.Instance.ExecuteVerb 的 Move 执行机动移动。
        /// </summary>
        /// <param name="forced">是否强制（默认 false，玩家可选）。</param>
        public static EffectData Maneuver(bool forced = false)
        {
            return new EffectData
            {
                Timing = EffectTiming.ManeuverTrigger,
                Forced = forced,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Area,
                    NeedsSelection = true
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Move }
                },
                Duration = "瞬时",
                FaqNote = "玩家可选",
                // 结算前校验来源卡版本号未变：离场/翻面/进手牌/进墓地则跳过结算（争夺区→争夺区移动不影响）。
                RequireSourceVersionUnchanged = true
            };
        }

        /// <summary>
        /// 公开（Public）：手牌区常驻关键字"公开"，约束该卡在手牌时不可被派遣为暗藏者。
        /// 对应卡文："公开（该角色不能被秘密派遣为暗藏者）"
        /// 由 RegisterContinuous 按 HandZone 生效；ContinuousGrants 授予"公开"关键字。
        /// 运行期可通过 PlayerCardData.HasInherentKeyword("公开") 实际判定，
        /// 而非从文本判断——效果失效（授予被移除）时判定自然为 false。
        /// </summary>
        public static EffectData Public()
        {
            return new EffectData
            {
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.HandZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：公开（不可用此卡进行派遣暗藏行动）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "公开", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 不能被装备结附（CannotBeAttached）：静止/禁止效果（D19-⑤），角色在争夺区常驻生效。
        /// 对应卡文："不能被装备结附"。
        /// 与公开不同——这是规则约束（非关键字授予），挂在 CardInstance.StaticFlags，
        /// 由 RegisterPersistent 置位、离场/封印清位；附属结附结算处读取 HasStaticFlag 拒绝结附。
        /// </summary>
        public static EffectData CannotBeAttached()
        {
            return new EffectData
            {
                Timing = EffectTiming.Static,
                Scope = ZoneScope.AreaZone,
                Duration = "常驻",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.CannotBeAttached, Param = "" }
                }
            };
        }

        /// <summary>
        /// 袭击（Strike）：现身触发（UncoverTrigger）关键词——角色从暗藏者状态现身进场并进场成功后触发，
        /// 对【同一 AREASLOT 内】的某一角色造成 amount 点伤害。
        /// 对应卡文："袭击N（现身进场成功后，对同一地区的目标角色造成 N 点伤害）"。
        /// 触发链路：CardManager.RevealCard → TriggerEmitPoints.UncoverTrigger → 引擎收该卡 UncoverTrigger 效果
        ///           → 入堆叠 → 双方让过 → ResolveEffect，执行 DealDamage。
        /// 目标约束（由引擎自动保证，无需在工厂里手写过滤）：
        ///  - 仅同 AREASLOT：GetLocalZoneTargets 取来源卡所在 SLOT 下全部争夺区，天然限定"同一地区"；
        ///  - 仅角色、排除暗藏者与附属：TargetKind.Character 映射时过滤掉 Covert（暗藏者）与 Attachment（附属）。
        /// 现身触发自身限制（D40）保证只收集"巨型蝙蝠自身"的现身效果，不波及他人。
        /// 注意：DealDamage 执行器已在 EffectEngine.Instance.ExecuteVerb 落地（EffectEngine.Instance.cs:276）。
        /// </summary>
        /// <param name="amount">伤害点数（默认 1，即"袭击1"）。</param>
        public static EffectData Strike(int amount = 1)
        {
            return new EffectData
            {
                EffectId = "",   // 调用方按"卡号_序号_关键词"规范补写，如 "JC030_1_strike"
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,   // 仅角色：自动排除暗藏者(Covert)与附属(Attachment)
                    NeedsSelection = true,         // 需玩家指明同 AREASLOT 内的目标角色
                    CanTargetSelf = true,          // 袭击可指定自身为目标（JC030 现身袭击可打自己）
                    IsLocalZone = true             // 规则⑥：限定本地区（同 AREASLOT），结算前目标/来源移动地区即丢失
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { amount.ToString() } }
                },
                Duration = "瞬时",
                FaqNote = "现身进场成功后触发；仅能选择同一地区的敌方/友方角色目标，不包括附属与暗藏者。"
            };
        }

        /// <summary>
        /// 威名（WeiMing）：战斗对抗获胜触发关键词——当本卡控制者赢得一场战斗对抗（Strength 类型）后，
        /// 在【本地区】（威名卡当前所在地区，= source.ZoneDetailId）放置一个本方势力标志。
        /// 对应卡文："威名（当你在一场战斗中获胜时，在该地区放置一个势力标记。）"
        /// 触发链路：CombatManager.ResolveWinner（战斗类型）→ TriggerEmitPoints.OnCombatWon 广播
        ///           → TriggerRegistry 收为 OnCombatWon（按"在场威名卡"各自注入 source）
        ///           → 条件 WinnerIsSelfInSameArea 判定：
        ///                 ① 本卡控制者 == 战斗获胜方（WinnerIsSelf）；
        ///                 ② 本卡所在地区 == 战斗获胜地区（WinnerInSameArea，Y 方案：跨地区卡收集阶段即不入栈）
        ///           → 入堆叠（排序窗口）→ 双方让过 → ResolveEffect，执行 PlaceToken(势力标志,1,威名)。
        /// 去重（D2）：同一控制者在同一地区、单次战斗对抗内只放置一个势力标志（由 EffectEngine 去重字典保证）。
        /// 非强制触发（Forced=false，对应卡文"可以"/"当…时"可选结算）；放置落点取 source.ZoneDetailId（与 JC035 同款）。
        /// </summary>
        public static EffectData WeiMing()
        {
            return new EffectData
            {
                EffectId = "",   // 调用方按"卡号_序号_关键词"规范补写，如 "JC018_2_WeiMing"
                Timing = EffectTiming.OnCombatWon,
                // 复合谓词：WinnerIsSelf(控制者==胜者) ∧ WinnerInSameArea(本地区==获胜地区)。
                // 仅 WinnerInSameArea 会让"敌方在本卡地区获胜"误触发，故需复合。
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                Target = new TargetSpec
                {
                    // 无显式目标：PlaceToken 落点取 source.ZoneDetailId（威名卡所在地区），
                    // 与 JC035 的附属结附地区落点同源，由引擎自动绑定来源卡所在地区。
                    Kind = TargetKind.None,
                    NeedsSelection = false
                },
                Actions = new Array<EffectAction>
                {
                    // Args[0]=势力标志类型；Args[1]=数量；Args[2]="威名" 触发 D2 单次战斗维度去重。
                    new EffectAction { Verb = ActionVerb.PlaceToken, Args = new Array<string> { "势力标志", "1", "威名" } }
                },
                Forced = false,  // 非强制触发（威名=可选结算）
                Duration = "瞬时",
                FaqNote = "战斗对抗获胜后在本地区放置一个势力标志；同一控制者同一地区单次战斗只放置一个（D2 去重）；非强制。"
            };
        }
    }
}
