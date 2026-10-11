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
        /// 【P3·K13】"**触发类**关键词"→ 其触发形态的模板（供**运行期授予**的关键字参与触发收集）。
        ///
        /// 背景：卡面固有的关键词由卡脚本写进 `EffectDataList`，触发收集时自然扫到；
        /// 而**运行期授予**的关键字（如 JC024 授予【机动】、JC089 授予【威名】）没有对应的 EffectData，
        /// 收集阶段会漏掉它。`TriggerRegistry` 用本表在收集时**合成**该关键词的触发效果，
        /// 与卡面效果走同一条收集路径（生效区 / 暗藏模式 / 条件判定都照常）。
        ///
        /// 只登记"关键词本身就等价于一条触发效果"的词：
        ///  · `机动` → `ManeuverTrigger`（对抗前窗口，选择另一地区移动过去）；
        ///  · `威名` → `OnCombatWon`（战斗对抗获胜，在本地区放置一个势力标志）；
        ///  · `声望` → `OnPowerCombatEnded`（**每次势力对抗结算完毕**，本地区声望角色较多的一方放置一个势力标志）。
        /// 不在表内的（`袭击` 由收集阶段的**数值合并**单独处理）一律不经本通道，避免重复结算。
        ///
        /// 【2026-09 用户口径·声望改触发】**声望从"引擎级立即结算"改为"触发效果"**（与威名同款，走本表）：
        /// 用户口径原文："声望是触发效果……应为势力对抗结算后**触发**声望，参考威名（但是不需要势力对抗获胜，
        /// 只需要计算声望角色数量）"。故本表新增 `声望`，同时**删除**原先在势力对抗出口直接调用
        /// `RenownRule.ApplyIfMajority` 的立即结算路径（否则会各放一次 = 重复）。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, EffectData> _triggerTemplates = new()
        {
            { "机动", Maneuver() },
            { "威名", WeiMing() },
            { "声望", Renown() },
        };

        /// <summary>见 <see cref="_triggerTemplates"/>：运行期授予的关键字 → 触发形态模板（只读遍历用）。</summary>
        public static System.Collections.Generic.Dictionary<string, EffectData> RuntimeTriggerTemplates()
            => _triggerTemplates;

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
                // 【2026-10·提示文字】选地区窗的提示由数据给出（不再硬编码在 UI 里）。
                Hint = "将移动至该地区",
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
                IsStrike = true,          // 【K13】标记为"袭击"：收集阶段会把多个袭击来源合并为一个触发（数值叠加）
                StrikeAmount = amount,
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
        /// 【K13】以既有"袭击"效果为模板，生成一个**伤害＝amount** 的袭击效果（用于把多个袭击来源合并为**一个**触发）。
        /// 模板只提供目标声明/条件/时效等结构（如卡面自带袭击的 `TargetSpec`），动作被替换为 `DealDamage{amount}`。
        /// </summary>
        public static EffectData StrikeWithAmount(EffectData template, int amount)
        {
            var prototype = template ?? Strike(1);
            return new EffectData
            {
                EffectId = (string.IsNullOrEmpty(prototype.EffectId) ? "袭击" : prototype.EffectId) + "_merged",
                Timing = EffectTiming.UncoverTrigger,
                IsStrike = true,
                StrikeAmount = amount,
                Condition = prototype.Condition ?? new EffectCondition { Predicate = "Always" },
                Target = prototype.Target,
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { amount.ToString() } },
                },
                Duration = string.IsNullOrEmpty(prototype.Duration) ? "瞬时" : prototype.Duration,
                Scope = prototype.Scope,
                TriggerCovertMode = prototype.TriggerCovertMode,
                RequireSourceVersionUnchanged = prototype.RequireSourceVersionUnchanged,
                FaqNote = $"袭击{amount}（由卡面自带袭击与运行期授予的袭击**叠加**合并而来）",
            };
        }

        /// <summary>
        /// 威名（WeiMing）：战斗对抗获胜触发关键词——当本卡控制者赢得一场战斗对抗（Strength 类型）后，
        /// 在【本地区】（威名卡当前所在地区，= source.ZoneDetailId）放置一个本方势力标志。
        /// 对应卡文："威名（当你在一场战斗中获胜时，在该地区放置一个势力标记。）"
        /// 【2026-10 用户口径】**横置的角色不提供其词条 ⇒ 本卡须未横置才触发**（`RequireSourceUntapped`，与声望同办）。
        /// 触发链路：CombatManager.ResolveWinner（战斗类型）→ TriggerEmitPoints.OnCombatWon 广播
        ///           → TriggerRegistry 收为 OnCombatWon（按"在场威名卡"各自注入 source）
        ///           → 条件 WinnerIsSelfInSameArea 判定：
        ///                 ① 本卡控制者 == 战斗获胜方（WinnerIsSelf）；
        ///                 ② 本卡所在地区 == 战斗获胜地区（WinnerInSameArea，Y 方案：跨地区卡收集阶段即不入栈）
        ///           → 入堆叠（排序窗口）→ 双方让过 → ResolveEffect，执行 PlaceToken(势力标志,1,威名)。
        /// 去重（广播窗口）：同一控制者在同一地区、**单次战斗对抗（一次 `OnCombatWon` 广播）内只放 1 枚**
        /// （`TriggerEmitPoints.OnCombatWon` 广播前 `BeginPlacementDedupWindow(PlacementDedupTags.WeiMing)`
        /// 清该标签的桶；`EffectEngine.PlaceToken` 按桶查重）。
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
                    // Args[0]=势力标志类型；Args[1]=数量；Args[2]=去重标签（`PlacementDedupTags.WeiMing`；
                    // 非空即启用去重 ⇒ 单次战斗对抗内每控制者每地区只放 1 枚）。
                    new EffectAction { Verb = ActionVerb.PlaceToken, Args = new Array<string> { "势力标志", "1", PlacementDedupTags.WeiMing } }
                },
                Forced = false,  // 非强制触发（威名=可选结算）
                Duration = "瞬时",
                // 【2026-10 用户口径】横置的角色不提供其词条 ⇒ 威名须**未横置**才触发（与声望同办）。
                RequireSourceUntapped = true,
                FaqNote = "战斗对抗获胜后在本地区放置一个势力标志；同一控制者同一地区单次战斗只放 1 枚（广播窗口去重）；未横置才触发；非强制。"
            };
        }

        // ==================== 【P3】词条规则（2026-09 用户给出完整规则文本） ====================

        /// <summary>
        /// 【P3】隐匿（Stealth）= 持续关键字：**该角色不能从手牌付费正面打出到争夺区**。
        /// 用户规则：仅限制"从手牌付费正面打出到争夺区"这一种方式；其他进入争夺区的方式（秘密派遣、效果放置…）不受限制。
        /// 实现：授予 `Keyword="隐匿"`；`TurnManager.PlayCard` 与 `PlayerCardData.HasInherentKeyword` 双向判定
        /// （印在卡面上的隐匿走"模板固有"，运行期授予的走 `HasKeyword`）。
        /// </summary>
        public static EffectData Stealth()
        {
            return new EffectData
            {
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.HandZone,   // 手牌期即需生效（打出门禁发生在手牌）
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：隐匿（不能从手牌付费正面打出到争夺区）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "隐匿", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 【P3】撤回（Withdraw）= 持续关键字：**一个地区被赢取时，拥有撤回能力的卡回到 OWNER 手牌，而不是牌库底**。
        /// 实现：授予 `Keyword="撤回"`；由 `AreaManager.ResolveAreaWon` 的"步骤3 区内卡归位"分支消费
        /// （带撤回的顶层卡、以及带撤回的附属卡，都改走 `返回手牌`）。
        /// </summary>
        public static EffectData Withdraw()
        {
            return new EffectData
            {
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：撤回（地区被赢取时回 OWNER 手牌而非牌库底）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "撤回", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 【P3】非资产（NonAsset）= 持续关键字：**拥有非资产效果的卡不能被用于【建立资产】基础行动**。
        /// 例外：可以通过其他效果变为资产（形态转换为"成为资产"不在本词条范围内）。
        /// 实现：授予 `Keyword="非资产"`；由 `TurnManager.EstablishAsset` 的门禁消费。
        /// </summary>
        public static EffectData NonAsset()
        {
            return new EffectData
            {
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.HandZone,   // 手牌期即需生效（建立资产是手牌 → 资产区的行动）
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：非资产（不能用于建立资产基础行动）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "非资产", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 【P3】屏障（Barrier）= 持续关键字：**不能被敌方卡牌或能力指定为目标**。
        /// 用户补充：一张卡若先被效果/卡牌指定为目标、之后才获得屏障，则结算时视为目标不合法
        /// （由 `TargetValidity.Check` 的结算复检天然覆盖）。
        /// 实现：授予 `Keyword="屏障"`（或实例标记 `HasGuard`）；判定收口在 `TargetValidity` 一处，
        /// 取目标阶段（`CardManager.MatchesRule/MatchesSpec`）与结算复检（`TargetValidity.Check`）共用同一实现。
        /// </summary>
        public static EffectData Barrier()
        {
            return new EffectData
            {
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：屏障（不能被敌方卡牌或能力指定为目标）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "屏障", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 【P3】杀伤 N（Kill）= 持续属性：**参与战斗对抗并获胜时，该次战斗对抗造成的可分配伤害 +N**；多个杀伤角色可累加。
        /// 实现：授予 `Attribute="杀伤"` 计 N（属性通道随快照投影，客户端/服务端同解）；
        /// `CombatManager` 在战斗对抗结算时把获胜方**本地区参与战斗的角色**的杀伤值求和，加到可分配伤害上。
        /// </summary>
        /// <param name="amount">杀伤点数（卡文"杀伤1"/"杀伤2"）。</param>
        public static EffectData Kill(int amount = 1)
        {
            return new EffectData
            {
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = $"关键字：杀伤{amount}（参与战斗对抗并获胜时，可分配伤害 +{amount}）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Attribute", Value = "杀伤", Amount = amount }
                }
            };
        }

        /// <summary>
        /// 【P3】声望（Renown）= 触发效果（用户 2026-09 规则文本，**第二轮口径**）：
        /// **每次势力对抗结算完毕**（含平局/败方；JC067「本地区额外进行一次势力对抗」追加的那次**同样触发**），
        /// 由该地区中**拥有较多声望角色的一方**触发，给该地区添加一个势力标志；
        /// **同一次势力对抗内多个声望角色也只添加 1 枚**（广播窗口去重，与威名同款）；
        /// **不依赖势力对抗获胜**（平局/败方只要声望角色更多就触发）。
        /// 实现：`Timing = OnPowerCombatEnded`（触发点＝`TurnManager` 每次势力对抗结算完毕广播）
        ///       ＋ 条件谓词 `RenownMajorityInSameArea`（该卡控制者在该地区的声望角色数 **严格多于** 对手，
        ///         且**来源卡就在该对抗地区内**——地区级规则）
        ///       ＋ `PlaceToken{势力标志,1,<see cref="PlacementDedupTags.Renown"/>}`。
        /// 口径（2026-10 用户口径）：**必须未横置**（横置的角色不提供其词条 ⇒ 既不计数也不触发；`RequireSourceUntapped`
        /// ＋ `RenownRule.CountRenownCharacters` 跳过横置者）、**不要求参与该次对抗**，只要在该地区即可；双方相等/均为 0 → 不触发。
        /// 注：卡面原文"一个地区的所有对抗结束后…未横置…"被本口径覆盖（用户 2026-09 第二轮口径优先）。
        /// </summary>
        public static EffectData Renown()
        {
            return new EffectData
            {
                EffectId = "",   // 调用方按"卡号_序号_词缀"规范补写
                Timing = EffectTiming.OnPowerCombatEnded,
                Condition = new EffectCondition { Predicate = "RenownMajorityInSameArea" },   // 【BUG 修复】必须"来源在该对抗地区内"（地区级规则）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    // Args[2]=去重标签（`PlacementDedupTags.Renown`；非空即启用去重 ⇒ 单次势力对抗内只放 1 枚）
                    new EffectAction { Verb = ActionVerb.PlaceToken, Args = new Array<string> { "势力标志", "1", PlacementDedupTags.Renown } }
                },
                Forced = false,   // "由拥有较多声望角色的一方触发"——非强制（可放弃）
                Duration = "瞬时",
                // 【2026-10 用户口径】横置的角色不提供其词条 ⇒ 声望须**未横置**才触发（计数同样不含横置者）。
                RequireSourceUntapped = true,
                FaqNote = "每次势力对抗结束后，本地区声望角色较多的一方放置一个势力标志（多个声望只放一个；只计未横置的声望角色）。"
            };
        }

        /// <summary>
        /// 【P4】声望的**关键字形式**（只授予 `Keyword="声望"`，不挂卡级触发）。
        ///
        /// 【2026-09 用户口径·声望改触发】**规则本体已改为触发效果**（与威名同款，见 `Renown()`）：
        /// 该触发由 `_triggerTemplates` 按关键字 `声望` **合成**（`TriggerRegistry` 收集时），
        /// 或由卡面显式声明的 `Renown()` 条目收集；**不再**有引擎级"立即结算"。
        /// 因此卡面印"声望"时，应同时：
        ///  · 用本方法宣告关键字（供计数 `RenownRule.CountRenownCharacters` 与其他"声望角色"规则使用）；
        ///  · 显式添加 `Renown()` 触发条目（卡面固有关键字不会被 `_triggerTemplates` 通道重复合成，
        ///    见 `TriggerRegistry` 的 `tmpl.HasInherentKeyword` 跳过）。
        /// </summary>
        public static EffectData RenownKeyword()
        {
            return new EffectData
            {
                EffectId = "",   // 调用方按"卡号_序号_关键词"规范补写
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：声望（势力对抗结束后，本地区声望角色较多的一方 +1 势力标志）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "声望", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 【P4】领袖（Leader）= 持续关键字 + **规则级唯一性约束**（用户 2026-09 规则文本）：
        /// **同一控制者在场上至多 1 个拥有【领袖】的角色**；一个领袖进场后若超限，**立刻**由该控制者
        /// 选一个（含刚进场的那个）**牺牲**——该牺牲是规则性结算，**不入堆叠**、不可响应；
        /// **进场触发照常生效**（进场触发在进场时已收集入堆叠，不因规则牺牲而撤销）。
        /// 实现：授予 `Keyword="领袖"`；由 `LeaderRule.EnforceOnEntry` 在进场触点
        /// （`ZoneManager.MoveToArea` → `OnCardEnteredArea`）消费；决策路径见 `LeaderRule`。
        /// </summary>
        public static EffectData Leader()
        {
            return new EffectData
            {
                EffectId = "",   // 调用方按"卡号_序号_关键词"规范补写
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "关键字：领袖（同一控制者在场上至多 1 个领袖；超限立刻选一个牺牲）",
                Duration = "常驻",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "领袖", Amount = 1 }
                }
            };
        }

        /// <summary>
        /// 【P3】XX标志X（如"时间标志1""锁定标志1""哲人石标志1"）= **强制进场触发**：
        /// 卡片进场后触发，给触发这个效果的卡**本身**添加 X 个 XX 标志。
        /// 实现：`EnterTrigger` + `Forced=true` + `PlaceCounter{标志名, X, "自身"}`；
        /// 经 `CardManager.AddCounter` 收口（广播 `OnMarkerPlaced`）。
        /// </summary>
        /// <param name="markerName">标志名（如"时间标志"）。</param>
        /// <param name="amount">数量 X。</param>
        public static EffectData Marker(string markerName, int amount = 1)
        {
            return new EffectData
            {
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { markerName, amount.ToString(), "自身" }
                    }
                },
                Forced = true,   // "强制进场触发"
                Duration = "瞬时",
                FaqNote = $"进场后强制给本卡放置 {amount} 个{markerName}。"
            };
        }
    }
}
