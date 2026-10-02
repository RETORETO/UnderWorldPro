# JC 系列卡牌效果实现清单（全 135 张）

> **📊 进度账本 → `JC卡牌效果进度表.md`（每行一个效果 + 实现/单测/人工检查三列）**。
> **推进方式已变更（2026-09）**：不再"等所有批次做完再动卡"，改为**进度表驱动**——哪张卡的全部效果都能做就先接入哪张；
> 只差一个机制时，先补那个机制（用本清单的 K 编号定位缺口）。见 §五 与 §7.6。

> **当前批次状态（2026-09，P4 收尾实测；逐项证据见 §7.8 / §7.9，账本见 §7.10）**：
> - ✅ **P1 接线与补漏 / P2 交互原语（弃牌·封印·检索·洗牌·展示）** 全部交付；
> - ✅ **P3-词条第一批**：隐匿 / 屏障 / 撤回 / 非资产 / XX标志X（`PlaceCounter`）/ 杀伤X；
> - 🟡 **P3 目标与语义**：**已交付**——②（激活候选扫附属区/秘社群）、⑤ 条件分支（`ElseBranch`）、**K14 墓地/手牌/资产目标**、
  **K23 每回合每能力次数**、**K26 阶段排除（判据已收窄到"伤害分配窗口打开期间"）**、**K2 群体动作**（覆盖 JC011/047/050/052/056/132）、
  **K1 动态数值 X**（覆盖 JC040/080/097，含 **X=0 ⇒ 不发动** 口径）、
  **复合目标 `KindAlt`（OR 种类，覆盖 JC107）**、**目标候选池扩大到 附属区/秘社/封印区**、
  **OnDraw/OnDiscard 按批粒度**（覆盖 JC080：一批抓牌只广播一次并带本批张数；弃牌同口径）、
  **K15 多目标 + 确认面板（单机＋联机）**（覆盖 JC021）；
  **仍未做**——"附属/秘社作**激活**目标"的场上点选高亮（`Arena` 仍只扫争夺区；非争夺区目标走选卡窗不受影响）；
  （K16 模式分支 ✅ 已交付、K24 玩家目标 ✅ 已交付——口径均已落实，见 §7.9/§7.10）；
> - 🟡 **P4 机制级**：声望机制 ✅、遏制（K6）✅、领袖 ✅、预测X ✅；**K4 形态/类别转换 🟡（2026-09 已交付"持续夺控 `ControlGain` + 实例层子类别改写 `SubCategoryRemove`"；印刷类别改写经 A32 确认"不做"；实例级形态转换 JC027/028 待做）**；**K10 额外费用 🟡 大部分已交付（JC049①/JC135① ②；JC081 待做，见 §7.10 65）**；**K19 临时费用池 ✅（JC115）**；阶段钩子（K18·K29）已按需交付（略过/追加对抗、重置步骤静止规则）；构筑唯一性（K25）按 A38 **暂不做**；
> - 🟡 **P5 规则文本**：灵体/领袖/声望/杀伤/隐匿/撤回/屏障/公开/非资产/XX标志X 均已落地；本行原列的"计数器/伤害防止/检视"等**已陆续交付**（计数器放置与按计数缩放、`CounterCostSpec` 计数器作代价、`PreventDamage` 伤害护盾、`Inspect` 单次私密检视、**持续随时检视 ＋ 鼠标悬浮预览（JC057）**、`WinAreaDestination` 去处替代、持续型图标/领域抑制），**以 §7.9 / §7.10 为准**；**仍未做**：混乱规则文本（全项目无定义）、"持续可见性授权"式检视（JC057 第 2 行 / JC114）、JC124 的持久登记。
> 下文 §零~§六 为**立项时的完整体检**（保留原始档位判定，便于对比跟进）。

> 生成依据：`Cards/Data/PlayerCards/JC/*.tres`（135 张，逐张读取牌面 `Effects` 文本）+
> 引擎现状实测（`EffectAction.cs` / `EffectEngine.ExecuteVerb` / `EffectData.cs` / `TriggerRegistry.cs` /
> `TargetValidity.cs` / `ActivateEligibility.cs` / `ZoneManager` / `ArenaOl`），非文档转述。
> 结论口径：**"已实现"= 生产代码路径上真会跑**；"仅声明"= 枚举/字段存在但执行器是 TODO 或触点未接。

---

## 零、总览

| 档位 | 含义 | 张数 |
|---|---|---:|
| ✅ | 已完成并注册（`JCEffects.cs`） | **13** |
| **A** | 现有机制 + 现有范式可直接落地（只写卡脚本，零新机制） | **7** |
| **B** | 需补 ≤2 个"小前提"（一条限制 / 一个动词 / 一行 Emit / 一个前提种类 / 一个字段） | **26** |
| **C** | 需补**基础机制**（见 §二，涉及 20+ 张的共性缺口） | **89** |
| **D** | 卡文语义在项目内**无定义**（灵体/领袖/声望/杀伤/混乱/隐匿…）→ 需先补规则文本 | 与 A/B/C 叠加（不单独计数） |
| 合计 | | **135** |

**一句话结论**：
- **13 张已跑通**，另有 **7 张可立刻照现有范式写完**（JC029/058/071/075/091/102/103）+ **26 张只差一两个小前提**；
- 其余 89 张卡住的原因**高度集中**：**29 个基础机制缺口（K1–K29）+ 9 个触发时机未接线 + 6 个词条无规则定义**，
  补齐 §五 的 P1–P3 后，C 档会塌缩约 2/3；
- ⚠️ 另有 **20 条"现有引擎硬约束"**（§二·补）会静默改变效果结果（最典型：`DealDamage` 对无防御卡完全免伤、
  未识别谓词 fail-open 恒真、15 个动词仅声明未实现），**写卡前必须先读那一节**。

---

## 一、可直接复用的现有范式（写新卡时照抄这些）

| 范式 | 参考卡 | 覆盖能力 |
|---|---|---|
| 进场/现身触发 + 本地区单目标 | **JC002**、JC030-A | `Timing=EnterTrigger/UncoverTrigger` + `TargetSpec{Kind,IsLocalZone,CanTargetSelf}` + 单动词 |
| 角色/附属激活（快速/行动窗口 + 横置代价 + 费用） | **JC003**、JC012① | `ActivateWindow`/`ActivateCondition`/`Cost`；`ActivateEligibility` 预计算门禁 |
| 事务卡（打出即结算 + 快速打出 + 选目标） | **JC005** | `OnSorceryCast` + `PlayTiming.Response` + `IsLegalPlayTarget` 聚合 `EffectData.Target` |
| 条件式目标限制（同一效果两分支） | **JC005** | `TargetRestriction.AttachedToCharacterIfNoMind`（`TargetValidity.CheckRestrictions`） |
| 结算动作：横置/消灭/移回/伤害/抽牌 | JC002/JC005/JC020 | `ActionVerb.Tap/Destroy/Bounce/DealDamage/Draw/DrawFromBottom`（**均已实现**） |
| 持续关键字授予（公开/灵体类） | **JC014** | `Continuous` + `ContinuousGrants{Kind=Keyword}` + `HasInherentKeyword` 双层判定 |
| 静止/禁止标记（不能被装备结附） | **JC001-A**、JC030-B | `Static` + `StaticEffects{Kind=CannotBeAttached}` + 消费点 `IsLegalPlayTarget`/`ResolveEntry` |
| 条件持续（领域忠诚阈值） | **JC001-B** | `PrereqSpec{DomainLoyalty,Amount}` + `ReevaluatePrereqs` |
| 线性计量持续（每点忠诚 +N） | **JC018-A** | `PrereqSpec{DomainLoyalty,ScaleByLoyalty=true}` |
| 群体光环（影响他者） | **JC060** | `IsAura` + `AuraFilterExcludeDomain`/`AuraRequiresNoDomain` + `ContinuousGrants` |
| 准备步骤强制触发 + 二选一 | **JC068-A** | `OnTurnStarted` + `Forced` + `PrepareUpkeep` + `ForceChoice` 窗口 |
| 触发/激活"放置势力标志（本地区）" | **JC035**、JC018-B | `PlaceToken`（落点取 `source.ZoneDetailId`）+ 威名去重 |
| 结附秘社的减费徽记（一次性/整回合） | **JC012①** | `Gain` + `Args{费用减少,N,filter,一次性}` → `ApplyCostModification` |
| 抽牌/从牌库底抽牌 | 秘社 `MSJC001` | `Draw` / `DrawFromBottom`（唯一已实现的非基础动词） |
| 触发/效果选目标（单机窗口 + 联机 `OptionUids`） | 全触发类 | `TurnManager.EnsureTargetThenPush` + `ChoiceRequest.SelectTarget` |
| 决策窗（付费/选目标/排序/对抗分配） | JC068、对抗三类 | `IInputGateway` + `ChoiceRequest.Kind`（7 种）+ `ArenaOl` 复用单机窗口 |
| 出牌/激活的合法性投影（双端同解） | 全卡 | `LegalityProjection` + `GameViewSnap`（客户端不自己判规则） |

---

## 二、缺失的前提 / 依赖 / 基础机制（按影响面排序）

> ⚠️ **本表为立项时的原始体检，行内"影响/需要做什么"保留不改**；**每个 K 编号的当前落地状态见 §7.9（P4 收尾实测）**——
> 部分条目已交付（K6 遏制／K17 条件分支／K5 的"放置计数器"部分／K3 的"伤害防止钩子"部分等），请以 §7.9 为准。

> 编号 K1…K29；"影响"列为依赖该机制的卡数（含叠加）。

| # | 缺口 | 影响 | 需要做什么 | 建议落点 |
|---|---|---:|---|---|
| **K1** | **动态数值 X**（`Args` 只能是常量字符串，无取值器） | ~14 | 新增参数求值器：`CountZone(zone,filter)`/`CountCounter(type)`/`CountMarkers(player)`/`CardCost(target)` 等，`EffectAction.Args` 支持 `"$count:墓地:角色"` 语法或新增 `ArgsExpr` 字段 | `EffectAction`/`EffectEngine` |
| **K2** | **群体/批量动作**（`Actions` 只有"单目标 + 单动词"，无法"对某地区所有角色…"） | ~14 | 新增 `TargetSpec.MassMode`（AllInArea/AllEnemyInArea/AllOwn…）或批量动词（`DealDamageAll`/`DestroyAll`/`TapAll`） | `TargetSpec` + `EffectEngine` |
| **K3** | **替代效果（Replacement）**：伤害转移/防止、死亡替代、离场去处替代 | ~9 | 挂钩子到 `CardManager.DealDamageToInstance`（伤害）/`HandleInstanceDeath`（死亡）/`AreaManager` 赢取（离场），按注册表询问"是否替代" | 新增 `ReplacementRegistry`（需求对照已列为"完全缺失"） |
| **K4** | **形态/类别转换**（角色↔附属↔资产；子类别改写/移除） | 6 | `CardInstance` 增加运行时 `CategoryOverride`；子类别"改写"（现只支持追加）；`成为资产/角色` 需区域迁移 + 类别覆盖 | `CardInstance` + `ZoneManager` |
| **K5** | **计数器（标志）系统无消费**：`CardManager.AddCounter/RemoveCounter` 只有测试调用，无 `PlaceCounter` 动词、无按计数缩放、无"以计数器为代价" | 9 | 新增 `ActionVerb.PlaceCounter/RemoveCounter` + `PrereqKind`/代价类型"移除N个标志" + 持续按 `Counters[type]` 缩放 | `EffectEngine`/`ActivateEligibility` |
| **K6** | **势力标志只有 Add**（`AreaManager.AddPowerMark`，无 Remove） | 6 | 新增 `RemovePowerMark(area,player,amount)` + `Curb` 动词（遏制＝对手选择并移除X个本方标志）+ `RemoveToken` 动词 | `AreaManager`/`EffectEngine` |
| **K7** | ~~**牌库检索/洗牌**（`Search`/`Shuffle` 均为 TODO）~~ ✅**P2 已做** | 8 | 已交付：`Search`（`DeckSearchFilter` 筛选 + 4 条范围/5 条去向 + 必洗牌）、`Shuffle`（可复现随机源）、联机 `ChoiceRequest.Kind.SelectDeckCard`（候选带 `OptionCardRefs`） | `EffectEngine`/`DeckSearchFilter`/UI 网关 |
| **K8** | **检视/展示私有信息**（看对手手牌、看牌库顶、看暗藏者） | 6 | 需"临时可见授权"（对手手牌在客户端是占位实例，见 `SnapshotFilter`）+ 展示窗口 | `SnapshotFilter`/`ChoiceRequest`/UI |
| **K9** | ~~**弃牌（Discard）原语完全不存在**~~ ✅**P2 已做** | 7 | 已交付：`ActionVerb.Discard`（自选/随机/由施术者指定）+ `TurnManager.DiscardCards` 唯一出口 + `OnDiscard` Emit + `DiscardByEnemy` 谓词 | `EffectAction`/`TurnManager` |
| **K10** | **额外费用未实现**（`TurnManager.cs:1548` 明确"ExtraCost 暂未实现"） | 3 | 打出流程插入"额外费用"步骤（`ExtraCostDescription`/`IsExtraCostMandatory` 字段已在，但无执行器）+ 联机决策窗 | `TurnManager.PlayCard` |
| **K11** | **可选支付"若你如此做…"**（Pay 动词 TODO） | 4 | 实现 `ActionVerb.Pay` + "支付成功才执行后续动作"的链式条件 | `EffectEngine` |
| **K12** | ~~**9 个触发时机未接 Emit**~~ ✅**P1 接 6 个 + P2 接 `OnDiscard`/`OnSealed`**：仅剩 `OnCounter` 未接（反制/终止原语不存在） | 11 | 已交付（触点见《触发效果制作指南》§3.1）；`OnCounter` 待反制原语 | `CardManager`/`AreaManager`/`ZoneManager`/`TurnManager` |
| **K13** | **运行期授予"触发类关键词/激活能力"**（`ContinuousGrants` 只支持 Keyword/Icon/Attribute/MagicDomain/SubCategory，授出的关键字不参与 `TriggerRegistry` 扫描） | 7 | 授予的 Keyword 需能被触发收集识别（或新增 `GrantAbility(EffectData)` 把能力挂到宿主） | `EffectEngine`/`TriggerRegistry` |
| **K14** | **目标种类扩展**：`MapInstanceToTargetKind` 只认 角色/暗藏者/附属/秘社 → **资产/墓地/手牌/牌组/玩家/封印卡都不能作为卡目标**，候选枚举也不含资产区 | 11 | 扩 `MapInstanceToTargetKind` + 各处候选池（`LegalityProjection`/`GetLocalZoneTargets`/`ArenaOl`）+ 玩家目标选择窗 | `CardManager`/`TurnManager`/UI |
| **K15** | **多目标选择**（"选择目标本方角色和/或目标敌方角色"） | 2 | `TargetSpec` 支持 `Count`/多选组；联机 `ChoiceResult.SelectedUids` 已支持多选 | `TargetSpec`+UI |
| **K16** | **模式分支（选择一项）** | 3 | `EffectData.Modes`（子效果列表 + 结算时让玩家选一项） | `EffectData`+结算 |
| **K17** | **条件式动作分支（若…则…否则改为）** | 5 | `EffectData` 需要条件分支（JC005 用"条件式目标限制"绕过了选目标差异，但动作不同则无法表达） | `EffectData` |
| **K18** | **对抗流程改造**（略过调查对抗/略过某地区对抗/额外一次势力对抗） | 3 | `CombatManager` 对抗阶段编排增加"跳过/追加"钩子（按地区+类型） | `TurnManager`/`CombatManager` |
| **K19** | **临时费用池**（"你获得1"，且仅保留到步骤结束） | 1 | 现费用模型＝"未横置资产数"，需独立 mana pool + 步骤结束清空 | `PlayerManager` |
| **K20** | **图标系统不完整**：`黑刀/白眼/黑眼` 已有约定（=Attribute 战斗/先手调查/调查），但**黑星/白星/白刀无约定**；无"移除图标"、无"图标替换"、无"镜像他人图标" | 9 | 明确能力图标↔`Attribute` 映射表；`Gain` 支持负数与 Attribute 分支（现默认走 Keyword）；新增 `Lose`/`MirrorIcons` | `EffectEngine.ApplyGain`/`AreaStatsCalculator` |
| **K21** | **位置/区域条件**（"位于水域地区""本地区没有敌方角色""非国家机构角色不能进入本地区"） | 4 | 新 `PrereqKind`/`StaticKind`：按地区子类别、按地区内敌我分布、按 Organization 的移动/打出禁止 | `EffectData`+消费点 |
| **K22** | **"牺牲一个角色"类代价**（`Sacrifice` 动词只牺牲**来源自身**） | 8 | `ActivateCondition`/动作支持"牺牲一个角色（选择）"，含牺牲他人/对手的角色 | `ActivateEligibility`/`EffectEngine` |
| **K23** | **次数限制**（"每回合只能发动一次/两次"） | 6 | `AbilityUsedThisTurn` 字段已存在但**无人消费** → 需按 EffectId 计数 + 回合重置 | `ActivateEligibility`/`TurnManager` |
| **K24** | **玩家目标选择交互**（"目标玩家""目标对手"） | 0（**已交付**） | ✅ 候选枚举 `PlayerTargetPool` + 独立选择窗口（`Arena.PromptPlayerTarget` / 联机 `Kind.SelectPlayer`）+ 三条取目标通路（激活 / **触发** / 槽位）+ 入堆叠锁定 `StackEntry.TargetPlayerId`（见 §7.9 K24 / §7.10 ⑰）。**仍缺**打出路径 / 联机激活路径 | — |
| **K25** | **构筑与场上唯一性限制**（"牌组数量无限""只能操控至多一个道途"） | 2 | `GameDeckValidator` 扩规则 + 场上同名/同子类别数量校验 | `Net/Godot/GameDeckValidator.cs` |
| **K26** | **发动窗口细化**（"不能在分配战斗伤害阶段使用"） | 1 | `ActivateWindow` 增加阶段排除（现只有 快速/行动/触发） | `ActivateEligibility` |
| **K27** | **从手牌/墓地发动或打出**（"手牌行动4""墓地行动""从墓地正面打出"） | 3 | 触发/持续已可按 `ZoneScope` 扫描到（候选集＝全部实例）；但**激活候选**（`GetEligibleAbilities`/`ArenaOl`）只扫争夺区+附属区，且打出门禁不含墓地来源 | `ActivateEligibility`+`TurnManager.PlayCard`+UI |
| **K28** | **免费打出**（"不需支付其费用，但仍需忠诚"） | 1 | 打出流程支持 `cost=0` 覆盖（含忠诚照旧校验） | `TurnManager.PlayCard` |
| **K29** | **阶段钩子类效果**（结束步骤"潜伏"强制触发、重置步骤禁止重置） | 2 | 需要在 `DoCleanup`/`DoReset` 里挂钩子（`OnTurnEnded` 已可用，重置步骤无时机） | `TurnManager` |

### 触发时机接线清单（K12 明细）

| 时机 | 现状（2026-09 更新） | 应接位置 |
|---|---|---|
| `OnCardDied` | ✅ P1 已接 | `CardManager.ResolveDeath`（**死亡行为发生时、卡离场之前**广播；不再需要 `Scope=Graveyard`） |
| `OnAreaWon` | ✅ P1 已接 | `AreaManager.ResolveAreaWon` 第 5 步后（`SourceCard=null`，`TriggeringPlayerId=winnerId`，`TargetAreaId=areaUid`） |
| `OnDraw` | ✅ P1 已接 | `TurnManager.DrawCardsForPlayer` / `DrawFromBottomForPlayer` 循环内（效果抽牌也经此，收口唯一） |
| `OnDiscard` | ✅ P2 已接 | `TurnManager.DiscardCards`（弃牌唯一出口；`TriggeringPlayerId`=弃牌者，`ExtraInt`=本次弃牌总张数） |
| `OnMarkerPlaced` | ✅ P1 已接 | `AreaManager.AddPowerMark` + `CardManager.AddCounter` |
| `OnSealed` | ✅ P2 已接 | `TurnManager.SealCard`（封印唯一出口；`ExtraString`=宿主 UID） |
| `OnMove` | ✅ P1 已接 | `ZoneManager.MoveToArea`（**争夺区→争夺区也要发**）+ `MoveToAttachedZone` |
| `OnCounter` | ❌ 仍无 Emit | 项目无"反制/终止"机制，需先做原语 |
| `OnCardLeft` | ✅ P1 已接 | `ZoneManager.MoveToHand/MoveToDeck/MoveToScore`（按"非死亡离场"界定） |
| `OnCombatWon` | ✅ P1 已接 | `CombatManager.ResolveWinner/ResolveWinnerAsync` 战斗对抗分支（伤害分配窗口关闭后广播；`TriggeringPlayerId`=胜者、`TargetAreaId`=地区） |
| `OnLatent` | ✅ **本轮已接（2026-09）** | 装配层订阅 `CardManager.OnCardStateChanged` 的 `CovertChanged`，仅 `IsCovert == true` 时 Emit（本卡被潜伏/翻面为暗藏者）；自身语义；效果需声明 `TriggerCovertMode=CovertOnly`（此刻必然暗藏） |
| `OnInvestigationWon` | ✅ **本轮已接（2026-09）** | `CombatManager.ResolveWinner/ResolveWinnerAsync` **调查对抗分支**（调查窗口关闭/分配完成后广播；平局与 0:0 无胜者**不广播**）；映射 `GameEvent.OnInvestigationWon → EffectTiming.OnInvestigationWon`；覆盖 JC072 |
| `OnPowerCombatEnded` | ✅ P3 已接 | 势力对抗结束（含平局；`TriggeringPlayerId=0`） |

> ⚠️ 另外两个**结构性坑**（写卡时必须知道）：
> 1. **附属卡写 `EnterTrigger` 永不触发**——`EnterTrigger` 只在 `ZoneManager.MoveToArea` 发，附属走 `MoveToAttachedZone`。
>   JC009/JC066/JC121 这类"结附类附属的进场触发"必须**先补附属进场触点**（K12 附带项）。
> 2. **以暗藏方式进场的卡，自身进场触发被默认 `CovertTriggerMode.FaceUp` 拦掉**——需要"暗藏进场触发"的卡要显式写 `CovertOnly/Either`。

### 其他必须实现的系统（有卡片直接依赖）

| 系统 | 影响卡 | 现状 |
|---|---:|---|
| **封印（Seal）动词** | 9（JC009/066/099/100/105/108/109/110/119） | ✅ **P2 已交付**：`ActionVerb.Seal` 四来源×两宿主矩阵（自身/目标/牌库顶/手牌 → 目标/自身）+ `背面朝上` 信息隔离 + `OnSealed`；候选/UI 齐备（单机 `PromptPickCard`、联机 `SelectHandCard`）。**仍需逐卡核对**：JC066/JC099/JC109 需要"按封印数缩放/镜像能力"（K5 类）与目标筛选（K21） |
| **潜伏/现身动词** | 6（JC024/039/054/063/087/088） | 现身＝`CardManager.RevealCard`（已有）；**潜伏＝`SetCovert(true)` 无动词**（需注意 `TriggerCovertMode` 与版本号） |
| **屏障（Barrier）** | 2（JC017/073） | `CardInstance.HasGuard` 字段在、`TargetValidity.Check` 留 TODO **未判定**；无授予/移除动词 |
| **声望（Renown）机制** | 8（JC011/067/070/072/074/076/080/095） | 关键词可授（Keyword），但"地区对抗结束后比较未横置声望角色→额外放势力标志"这一规则**完全未实现**（需 K18 类阶段钩子 + 比较 + 放标） |
| **撤回（Withdraw）机制** | 3（JC016/087/116）+ JC079 | 需在"地区被赢取"结算里对具该关键词的卡改为**回拥有者手牌**（现统一进牌库底）→ 属 K3 替代效果的一个消费点 |
| **隐匿（Hidden）** | 1（JC097） | "不能正面打出、只能秘密派遣进场"——需打出门禁按关键词拦截（`StaticKind` 或 `LimitToPlay` 类字段） |
| **混乱（Chaos）** | 2（JC064/JC133 FAQ） | 无定义、无实现（"随机选择进入的地区"） |

---

## 二·补、写卡前必须知道的引擎硬约束（实测，全部带文件:行）

> 这些不是"缺失功能"，而是**现有功能本身的边界**，直接决定某些卡"看起来实现了却打不出效果"。

| # | 约束 | 证据 | 影响 |
|---|---|---|---|
| 1 | **`DealDamage` 对"无印刷防御"的卡完全免伤**（不是减伤，是整段 return） | `CardManager.DealDamageToInstance:418` + `CardInstance.HasDefense`（读模板 `PlayerTemplate.Defense>=0`，`CardInstance.cs:380`） | 大量"造成伤害"卡对 `Defense=-1` 的卡无效（JC050/JC047/JC132/JC102… 需按目标卡面逐一确认） |
| 2 | **`Gain("防御")` 救不回来**：加的是 `CurrentDefense`，而 `HasDefense` 读模板 | `EffectEngine.ApplyGain:637-643` vs `CardInstance.cs:380` | JC008/JC071/JC059/JC089 的加/减防御对"无防御卡"无实际意义 |
| 3 | **暗藏者免疫一切伤害**（设计如此） | `CardManager.cs:421` | JC047/JC050/JC132 的"消灭/伤害暗藏者"必须走 `Destroy` 而非伤害 |
| 4 | **只有 3 个 Attribute 白名单会真改数值**：`先手调查`/`调查`/`战斗` | `CardInstance.Grant:185-195`；`AreaStatsCalculator.cs:46-50` | **黑星/白星/白刀（势力/先手势力/先手战斗）目前无法产生实际数值**（K20）；`Icon` 授予**在生产代码中无任何规则消费者**（`HasIcon` 仅测试引用） |
| 5 | **未知 `EffectCondition.Predicate` → fail-open（返回 true）** | `EffectEngine.EvaluateCondition:899-901` | 谓词名写错 = 效果**无条件全量生效**且无报错；而 `TargetRestriction` 未实现是 **fail-closed**（判不通过，`TargetValidity.cs:224-226`）——两者方向相反，调试极易误判 |
| 6 | 两个谓词是**硬编码恒真**：`"成功封印"`、`"心灵领域忠诚≥2（资产区心心）"` | `EffectEngine.cs:831-833`、`:834-837` | ✅ **已绕过（2026-09）**：JC009 不再依赖 `"成功封印"` 谓词，改由**动作成果**判定（`Seal` 未落地 → 链式中止）；`"成功封印"` 谓词本身仍是恒真占位，等有卡真需要时再实现 |
| 7 | **`ContinuousPrereqSpec.Zone` 是死字段**（忠诚恒读资产区） | `EffectEngine.GetPrereqLoyaltyValue:1033` → `PlayerManager.GetDomainLoyalty:201` | 表达不了"某地区忠诚≥N" |
| 8 | **未知 `EffectGrant.Kind` 静默丢弃**（无 default 日志） | `CardInstance.Grant:181-210` | Kind 拼错 = 卡"无效"但无任何提示 |
| 9 | **Keyword/Icon 是 HashSet，多源不叠加** | `CardInstance.cs:183-184`、`:219-220` | 两个来源授予同一关键字，撤掉一个即整体消失 |
| 10 | ~~**费用修正只能改"打出费"且必须 `Duration` 含"回合结束"**~~ **已解除**（2026-09：`CostScope` 费用通道隔离 → "现身（付费翻面）"独立成通道，见 §7.10 ㉛；常驻减费走 `ContinuousCostMod`，见 §7.10 ㉚） | ~~`EffectEngine.cs:633`、`:656-669`；打出侧 `TurnManager.cs:1405`~~ → `EffectEngine.Apply/Get/ConsumeCostModification(..., costScope)`、`TurnManager.RevealCovert` | ~~激活费不可减（JC033 的"暗藏者翻面费用-1"无处挂）；"永久/常驻减费"表达不了~~ → 两者均已可表达（JC033 / JC026 已接入） |
| 11 | **光环只作用于"源卡控制者的争夺区"**，单卡只取第一个 `IsAura`，不按忠诚缩放 | `EffectEngine.cs:1264`、`:1308-1309`、`:1282-1288` | JC059（本地区+防御）、JC038（按 Organization）、JC105（按关键词）都超出现有光环能力 |
| 12 | **`RegisterPersistent` 附属卡不在 `AttachedZone` 直接 return；`UnregisterPersistent` 离场即撤销** | `EffectEngine.cs:942-947`、`:1204-1254` | ~~"离场后仍生效"（JC124 哲人石）当前**不可实现**~~ **已解决（2026-09）**：JC124「离场后仍生效」**不走持续效果登记**，改走**构筑级（卡组级）全局触发**（`DeckLevelTriggerRule`，登记只依赖"开局卡组里有没有"⇒ 与来源身处何处无关），见 §7.10 76 |
| 13 | **附属卡写 `EnterTrigger` 永不触发**；以暗藏进场者自身进场触发被默认 `FaceUp` 拦掉 | `ZoneManager.MoveToArea:444-453`（只此一处 Emit）vs `MoveToAttachedZone:459-476`；`TriggerRegistry.cs:183` | JC009/JC066/JC121 需先补"附属进场触点"；暗藏进场触发需显式 `CovertTriggerMode` |
| 14 | ~~**争夺区→争夺区移动不发任何事件**~~ **已解决** | `ZoneManager.MoveToArea` 末尾已 `_triggerEmitPoints?.OnMove(...)`（位置变更即广播，含争夺区→争夺区；同区重放不发） | ~~机动/夺控既无进场也无移动触发~~ ⇒ **「进入一个地区」现由 `OnMove` 承载**（JC061/JC087，见 §7.10 78 / §7.4b A48）；`OnMove` 已加入"只对事件主体卡成立"白名单 |
| 15 | **`OnCombatWon` 只覆盖战斗对抗**（调查/势力无广播） | `CombatManager.cs:215/338` | ✅ **已解决（2026-09）**：新增 `OnInvestigationWon`（单机＋联机调查分支，窗口关闭后广播；平局不广播）→ 覆盖 JC072；`OnPowerWon`（势力**获胜**）仍未做 |
| 16 | **效果取目标只有两条候选链路**：触发=本 AREASLOT 争夺区（且**只按大类粗筛、不应用 Restrictions**）；激活=**只有争夺区** | `TurnManager.GetLocalZoneTargets:888-919`（`:915` 无精筛）；`ActivateEligibility.HasLegalTarget:225-243`（`:235` 仅 `GetAreaCards`） | ① 候选过宽：玩家能选到结算必判丢失的目标；② 候选过窄：**`Kind=Attachment/Council` 的激活目标在单机与服务端永远不可发动**（现无卡使用，但 JC013/JC019 类需要） |
| 17 | **地区目标从不经 `CheckRestrictions` 精筛** | `TurnManager.cs:1459-1468`（只校验地区存在） | "目标城市地区"这类精筛无效 |
| 18 | **只有 `PrepareUpkeep` 一个动词带玩家交互** | `EffectEngine.cs:579-598`、`:380-406` | 任何"由某玩家选择…"的动作（Curb/弃牌/分配）目前只能强制结算 |
| 19 | **`Search`/`Reveal`/`Shuffle`/`Pay`/`RemoveToken`/~~`PreventDamage`~~/`Lose`/`Seal`/`Curb`/`Attach`/`GenerateToken`/`Investigate`/`Strength`/`Power`/`Win` 共 15 个动词仅声明**（打印 TODO 日志）—— **该表为交付前快照**：`Search`/`Reveal`/`Shuffle`/`Pay`/`RemoveToken`/`Seal`/`Curb`/`PreventDamage` 等已陆续落地（`PreventDamage` 见 §7.10 ㉜），**现存仍为 TODO 的**：`Lose`/`Attach`/`GenerateToken`/`Investigate`/`Strength`/`Power`/`Win` | `EffectEngine.cs:507/515/519/523/527/531` + `:563-573` | 见 §二 K 表 |
| 20 | **可执行动词仅 12 个** | Tap/Untap/Destroy/Bounce/Move/Sacrifice/Gain/PrepareUpkeep/DealDamage/PlaceToken/Draw/DrawFromBottom | `Move` 移动的是 **source**；`Sacrifice` 牺牲的是 **source**；`PlaceToken` 只能放势力标志到 source 所在地区 |

---

## 三、关键词/词条清单（含"定义缺失"）

| 词条 | 出现卡数 | 代码可表达性 | 缺口 |
|---|---:|---|---|
| 公开 | 12 | ✅ `Continuous{Keyword=公开}` + `HasPublicKeyword` 双层 | 无（JC014 已验证） |
| 威名 | 3 | ✅ `KeywordEffects.WeiMing()` + `OnCombatWon` | 运行期**授予**威名不可触发（K13） |
| 袭击N | 5 | ✅ `KeywordEffects.Strike(N)` + `UncoverTrigger` | 授出"袭击"不可触发（K13）；**袭击X** 需 K1 |
| 机动 | 4 | ✅ `KeywordEffects.Maneuver()` + `ManeuverTrigger` | 运行期授予不可触发（K13） |
| 灵体 | **13** | ❌ 无字段/枚举/消费点 | **JC 包已定义：灵体＝防止伤害**（受到超过防御上限的伤害也不死 → **不产生死亡触发**）。引擎侧挂接点已就位：`CardManager.DamagePreventer`（伤害防止）+ `DeathPreventer`（死亡阻止）；正式实现只需注册钩子（P2） |
| 领袖 | **10** | ❌ 无字段 | **规则语义未定义**（推测为构筑/唯一性限制 → 与 K25 相关） |
| 声望 | **8** | ❌ 无机制 | 规则文本见 JC070 括号；**需实现对抗结束钩子** |
| 杀伤N | 2（JC016/JC046） | ❌ 无定义 | **规则语义未定义**（推测：对地区所有角色造成N点伤害 → K2+K1） |
| 遏制N | 3 | 🟡 `ActionVerb.Curb` 存在但执行器是 TODO | 需 K6（RemovePowerMark）+ 对手选择 |
| 混乱 | 2 | ❌ 无定义 | 未定义（随机进入地区） |
| 隐匿 | 1 | ❌ | 需打出门禁拦截 |
| 撤回 | 3 | ❌ | 需 K3 消费点 |
| 潜伏 | 6 | ❌ 无动词 | `SetCovert(true)` + 动词封装 |
| 屏障 | 2 | 🟡 `HasGuard` 在、判定留 TODO | 需授予/移除 + `TargetValidity.Check` 判定 |
| 时间标志 | 5 | 🟡 `Counters` 字典在，**无动词、无消费** | K5 | **追加（2026-09，A17/A27）**：**计数器作发动代价**已交付——`EffectData.CounterCost`（`CounterCostSpec{Markers,Amount}`）+ `EffectEngine.GetCounterCostCandidates`/`PayCounterCost`（发动者选择、入堆叠前支付、fail-closed）+ `ActivateEligibility.GetCounterCostCandidates` 门禁 → **JC044②**（见 §7.10 57）
| 锁定标志 | 3 | 🟡 同上 | K5 + K21（按标记的静态限制） |
| 哲人石标志 | 1 | 🟡 同上 | K5 + K3（死亡替代，且"来源离场后仍生效"） |
| 分值 | 1（JC079） | 🟡 `BaseCardData.Score` 字段在 | 需地区赢取时的去处替代（K3） |

---

## 四、逐卡清单（135 张）

> 档位：**A**=现有机制+范式直接做；**B**=补 1 个小前提；**C**=需补基础机制；**D**=词条/规则语义未定义（与 A/B/C 叠加标注）。
> "路径"列给出可直接照抄的范式卡；"缺失"列引用 §二 的 K 编号。

### 4.1 已完成（13 张，仅列 ID）

`JC001 / JC002 / JC003 / JC005 / JC012 / JC014 / JC015 / JC018 / JC020 / JC030 / JC035 / JC060 / JC068`
（各含测试：JC001Tests、JC002TriggerChainTests、JC005Tests、JC012_015_030_035_060Tests、JC014_068Tests、JC020Tests 等）

### 4.2 可直接完成（A 档，7 张：无需任何新机制）

| 卡 | 名称 | 效果要点 | 路径（照抄范式） | 注意 |
|---|---|---|---|---|
| JC029 | 新生血族 | 袭击1（现身进场对同地区角色 1 伤害） | `KeywordEffects.Strike(1)` + `UncoverTrigger`（JC030-A 同款） | — |
| JC058 | 坚毅的刑警 | 现身触发：消灭本地区目标暗藏者 | JC002 骨架 + `TargetSpec{Kind=Covert,IsLocalZone}` + `Destroy` | 暗藏者在争夺区桶内，候选可达 |
| JC071 | 阿瓦隆隐士 | 快速行动横置：本地区另一个本方角色 +1 防御直到回合结束 | JC003 骨架 + `Gain{"防御","1"}` + `Duration=直到回合结束` | 目标须有印刷防御（见 §二 伤害限制） |
| JC075 | 雪地救援队 | 公开；快速行动2横置：另一个本方角色回手 | JC014(公开) + JC003(激活) + `Bounce` + `Friendly`+`CanTargetSelf=false` | — |
| JC091 | 谋杀 | 只能行动阶段打出；快速行动：消灭目标人类角色 | `LimitToActionPhaseOnly=true` + `TargetRule{Character,StaticSubCategory,"人类"}` + `Destroy` | 事务卡链路已通 |
| JC102 | 妖火 | 只能行动阶段打出；快速行动：对目标角色 1 伤害 | 同 JC091 + `DealDamage{"1"}` | 无防御角色免伤 |
| JC103 | 丧失理智的画家 | 快速行动横置：下张【梦境行者】牌费用-1 | **JC012① 同款**：`Gain{"费用减少","1","梦境行者","一次性"}` | filter 支持 Organization（"梦境行者"是 Organization） |

### 4.3 需补小前提（B 档，26 张：≤2 个小前提）

| 卡 | 名称 | 效果要点 | 缺失（小前提） |
|---|---|---|---|
| JC006 | 逆转异界之门 | 快速行动：将目标具魔法领域图标的角色移回手牌 | 新 restriction **`HasMagicDomainIcon`**（`Bounce` 已有） |
| JC008 | 灵能激发 | 目标角色 +1 防御与黑刀直到回合结束 | `Gain` 需识别 **Attribute"战斗"**（现"战斗属性"降级为 Keyword）+ **K26** 阶段窗口排除 |
| JC010 | 青手费南迪尔 | 领袖/灵体；快速行动横置：消灭**已横置**角色 | 新 restriction **`Tapped`**（`TargetRule.cs:39` 已列扩展位）；领袖/灵体 = D |
| JC022 | 引路仙子 | 灵体；触发：赢得**其参与**的战斗对抗时抓牌 | 新条件谓词 **"本卡参与了该次战斗"**（现只有 WinnerIsSelf/WinnerInSameArea）；灵体 D |
| JC023 | 范·弥尔顿 | 领袖/威名/机动/袭击2；持续：本地区敌方暗藏者不能参与势力对抗 | ~~新 **StaticKind** + `AreaStatsCalculator.Power` 消费点~~ **已交付**（`StaticKind.ExcludeCovertPower`，见 §7.10 ㉟） |
| JC039 | 王座会徽记 | 同 JC012 + 快速行动3横置：目标本方【王座会】角色潜伏 | **潜伏动词**（`SetCovert(true)` 封装）+ Organization 目标筛选 |
| JC042 | 末日信徒 | 牺牲：下张【鸣钟教派】或具【血】图标牌费用-2 | 费用修正 **filter 扩展**（现仅卡名/Organization，`EffectEngine.cs:764-766`） |
| JC043 | 纵火狂 | 进场触发：消灭目标"结附于秘社**或**本地区"的附属 | ✅ **已交付**（`TargetSpec.SpecAlt` 两条声明 OR + `HostIsCouncil`/`AttachedToArea` → `P3CompositeTargetTests`(4)） |
| JC053 | 鸣钟教派徽记 | 同 JC012 + 快速行动1横置：本回合若有敌方标志被移除则抓牌 | ✅ **已交付**（回合内事件记忆位 + 谓词 `EnemyPowerMarkRemovedThisTurn` + 激活前门禁 → `P3TurnMemoryTests`(7)） |
| JC072 | 博学的贤者 | 公开；触发：赢得**其参与**的调查对抗时获声望直到回合结束 | `Keyword=声望` 可授（规则本体未实现 D）+ **调查对抗无 OnCombatWon 广播** |
| JC074 | 天空的教诲 | 目标角色 +黑眼黑眼+声望直到回合结束 | `Gain{"调查","2"}` ✓ + `Keyword=声望`（D） |
| JC076 | 悟道僧人 | 声望；现身触发：抓一张牌 | `Draw` ✓ + 声望 D |
| JC079 | 圣婴 | 公开；1分值；触发：赢取本地区时置入计分区而非牌库底 | ~~`OnAreaWon` **接线**（K12）+ 赢取时**去处替代**（K3 消费点）~~ **已交付**（`WinAreaDestination`，见 §7.10 ㊲；并把卡文"1分值"补进数据 `Score=1`） |
| JC084 | 街头混混 | 持续：所在地区有本方标志→白星；有敌方标志→白眼 | 新 **PrereqKind：所在地区标志归属** + 白星/白眼（Attribute 白名单，K20） |
| JC085 | 复生行尸 | 持续：资产区有【死】→白星；**墓地行动：可从墓地正面打出** | ✅ **已全行交付**（`DomainLoyalty(Death)`→白星 + K27 来源门禁/墓地行动 → `P3GraveyardPlayTests`(6)） |
| JC088 | 职业杀手 | 现身触发：消灭本地区印刷费≤2 角色；快速行动2：潜伏 | **`CostAtMost`** 一条 restriction + 潜伏动词 |
| JC090 | 灵魂契约 | 结附地区；每当本方角色死亡→在结附地区放势力标志 | **`OnCardDied` 接线**（1 行 Emit；`PlaceToken` 落点已支持） |
| JC093 | 怨灵咒刃 | 快速行动**牺牲一个角色**：受结附角色 +黑刀黑星直到回合结束（每回合 2 次） | ~~**K22 牺牲他人角色作代价** + **K23 次数上限** + 黑星 K20~~ **已交付**（`SacrificeCostSpec` + `Gain{"…","…","结附宿主"}`，见 §7.10 ㊶） |
| JC094 | 威达的汀巴巴迪 | 行动，**牺牲本地区另一个角色**：消灭目标角色或暗藏者 | ~~**K22**（目标 `Kind=Character`+`IncludeCovert` ✓）~~ **已交付**（`SacrificeCostSpec`，见 §7.10 ㊵） |
| JC096 | 阿格里帕之犬 | 快速行动1横置，**牺牲一个角色**：抓一张牌 | ~~**K22**~~ **已交付**（未写"另一个"→ 可牺牲来源自身，见 §7.10 ㊶） |
| JC106 | 酒神秘仪 | 快速行动：目标角色黑白星→黑刀，并获得灵体 | ~~**K20 图标移除/替换**（`Lose` 动词缺失）；灵体 D~~ **已交付**（`ActionVerb.ReplaceIcon`，**图标替换＝属性替换**，见 §7.10 ㊹） |
| JC112 | 黑猫 | 公开；不能被装备结附；牺牲：下张"具领域图标"牌费用-1 | 费用修正 **filter 支持"具魔法领域图标"** |
| JC116 | 柯尔特巨蟒左轮 | 结附角色；**撤回**；受结附角色 +黑刀 | ~~**撤回消费点**（地区赢取时改为回手）+ `Attribute=战斗`（K20）~~ **已交付**；**2026-09 修正**：原先只授 `Icon`（有图标不加战力）→ 现同时授 `Attribute:战斗` |
| JC125 | 无知路人 | 构筑时牌组数量无限 | **K25 构筑校验**（`GameDeckValidator`） |
| JC129 | 操控心智 | 行动：获得目标敌方角色控制权直到回合结束 | ✅ **已交付**（`GainControl` + `ChangeController` + `TimedMod{Control}` 到期归还 → `P3ControlTransferTests`(6)） |
| JC130 | 深入研究 | 行动：抓两张牌，然后弃一张手牌 | `Draw` ✓ + **K9 弃牌原语** |

### 4.4 需补基础机制（C 档之一，80 张）

| 卡 | 名称 | 效果要点 | 主要缺失 |
|---|---|---|---|
| JC004 | 力场法师 | 进场触发：横置目标角色；**若已横置则改移回手牌** | **K17 条件式动作分支**（两个动作二选一） |
| JC007 | 常春藤派学者 | 进场：牌库找 ≤2 费法术/书籍牌→展示→入手→洗牌 | **K7 Search/Shuffle** + CostAtMost + 子类别筛选 |
| JC009 | 远东六号科研所 | 结附秘社；进场触发：封印目标角色，**然后遏制1（封印成功才结算）** | ✅ **已全行交付**（动作成果机制"封印未落地→后续整段跳过" + `TargetSpec.AllAreas` 全场候选 → `P3ActionOutcomeTests`(8)） |
| JC011 | 德莫斯提尼 | 领袖/声望；现身触发：横置**所有**本地区敌方角色 | **K2 群体动作** + 声望 D + 领袖 D |
| JC016 | 执行部精锐 | 撤回；杀伤1；持续：所在地区无敌方角色→白眼黑星 | 撤回消费点 + 杀伤 D + 新 PrereqKind（地区无敌方角色）+ 黑星 K20 |
| JC017 | 除咒师 | 进场：消灭目标角色上所有敌方附属；该角色获屏障直到回合结束 | **屏障机制**（`HasGuard` 判定 TODO）+ 批量消灭 + 阵营筛选 |
| JC019 | 弥尔顿的镇魔枪 | 结附角色；受结附角色 +黑刀 且获得"快速行动横置：对本地区非人类角色 1 伤害"的能力 | ~~**K13 运行期授予激活能力**（把 EffectData 挂到宿主）~~ **已交付**（`EffectData.GrantedAbility` + `ReapplyGrantedAbilities`，见 §7.10 ㊽） |
| JC021 | 狭路相逢 | 只能在行动阶段；快速行动：选目标本方角色和/或敌方角色，移动到目标地区 | **K15 多目标选择** + 禁原地移动校验 |
| JC024 | S.P.T董事局 | 领袖；进场：所有其他本方角色获机动直到回合结束；快速行动2：潜伏，下个准备步骤免费现身 | ✅ **已全行交付**（步骤级承诺 → `P3StepPledgeTests`(5)；群体 Gain 主体 + 运行期授予【机动】参与触发 → `P3RuntimeKeywordTests`(7)） |
| JC025 | 猎手论坛徽记 | 同 JC012 + 快速行动1横置牺牲：牌库找猎手牌→入手→洗牌 | **K7 Search/Shuffle** |
| JC026 | 洛特尼科夫 | 持续：每回合第一张装备牌费用-1；进场：牌库找装备牌 | ✅ **已全行交付**（`ContinuousCostMod`（每回合可再生的一次性，`子类别:装备`）+ `Search` → `P3RenewableCostModTests`(6)） |
| JC027 | 花园地精像 | 结附地区；快速行动2：**成为角色**并获白眼黑刀黑星+1防御直到回合结束 | **✅ 已全行交付（2026-09）**：`FormChange{CharacterOrAttachment}` ＋ 子类别「地精」＋ 四项 Gain（图标/数值双通道）＋ `TimedMod{Kind="Form"}` 到期还原；见 §7.10 69 |
| JC028 | 鲜血奴仆 | 快速行动牺牲：**移回场上成为附属**结附目标吸血鬼角色 | **✅ 已全行交付（2026-09）**：牺牲自身（代价）→ `TransformForm{"鲜血",附属,失活印刷效果,清图标与防御}` ＋ `Attach{"目标"}`；无合法目标不能发动、结算时目标离场不回场；见 §7.10 69 |
| JC031 | 唤灵师 | 触发：另一个本方角色死亡→令目标玩家弃一张手牌（吸血鬼则随机）；每回合一次 | ✅ **已全行交付**（K12 死亡接线 ✅ + K9 弃牌 ✅ + K24 触发路径 ✅ + K23 ✅ + **K17 门禁/分支双条件 ✅** → `P3DeathBranchTests`(8)） |
| JC032 | 血族长老 | 行动2：牌库顶 6 张找吸血鬼角色→展示→以暗藏者置于本地区→洗回；每回合一次 | **K7** + 暗藏进场封装 + K23 |
| JC033 | 家族传承 | 结附秘社；持续：本方暗藏者付费翻面费用-1；行动1牺牲：牌库找吸血鬼角色→以暗藏者置目标地区→洗牌 | **全行已交付**（费用通道 + 激活的地区目标，见 §7.10 ㊛/㊾）；~~**费用修正不作用于"现身/翻面费用"**（现只改打出费）~~ **已解除**（`CostScope="现身"`，2026-09 已接入）+ K7 |
| JC034 | 心兽之道 | 结附秘社；**只能操控至多一个道途**；持续：赢得战斗且有本方吸血鬼参与→令对手弃牌 | **K25 场上唯一性** + K9 + 条件谓词扩展 |
| JC036 | 初拥 | 结附人类角色；持续：**获得其控制权**，并将人类类别**改为**吸血鬼 | ~~**K4（子类别改写）+ 持续控制权及归还链**~~ **✅ 已全行交付（2026-09）**：`EffectData.ControlGain`（宿主→本卡控制者、物主与地区不变、不刷版本号；注销归还**拥有者**）+ `SubCategoryRemove`「人类」/`SubCategory`「吸血鬼」（实例当前类别，**印刷类别不变**）；FAQ 第二条＝通用规则 `AttachmentRule`（宿主翻面为暗藏者 ⇒ 附属被消灭）。见 §7.10 64 |
| JC037 | 克里斯蒂娜 | 领袖；持续：每操控一个暗藏者→一个黑眼 | **按场上计数缩放的持续授予**（现只支持领域忠诚缩放）+ 领袖 D |
| JC038 | 列恩 | 领袖；持续：其他本方【王座会】角色获袭击1；行动牺牲吸血鬼/奴仆：对手弃牌+你抓牌 | ~~**K13（授触发关键词）** + **K22** + K9 + 光环按 Organization 筛选~~ **全行已交付**（见 §7.10 ㊶） |
| JC040 | 安普莎 | 触发：敌方玩家弃牌时遏制X（X=弃牌数） | **K12 OnDiscard** + K9 + **K6 Curb** + **K1 动态X** |
| JC041 | 禁婆 | 快速暗藏者行动2：现身（**仅当位于水域地区**）；现身强制触发：每位玩家弃一张手牌 | **全行已交付（2026-09）**：第 1 条＝`RequireSourceCovert`（暗藏者专用激活）＋ `RequireSourceAreaSubCategory="水域"`（地区子类别前提）＋ `Uncover{"自身"}`；前提判定候选/权威同源；UI 按 A29 弹选择框；第 2 条＝`Discard{1,"双方","自选"}` → 见 §7.10 71 |
| JC044 | 灾难使者 | 时间标志1；行动，**移除一个时间标志**：遏制1 | **全行已交付**：时间标志1 ✅（词条）；第 2 条 ✅ **K5 计数器作发动代价**（`CounterCostSpec` + `PayCounterCost`）＋ 遏制走既有 `CurbRule`（见 §7.10 57） |
| JC045 | 钟摆祭司 | 触发：角色死亡→放时间标志；持续：每个标志→一个黑星 | **K12 死亡接线** + **K5** + 按计数缩放 + 黑星 K20 |
| JC046 | 无面屠戮者 | 杀伤2；现身强制触发：每位玩家各牺牲一个角色 | 杀伤 D + **多玩家各自选择牺牲**（K22 变体） |
| JC047 | 烈火重围 | 行动：对目标地区**所有角色**造成 1 伤害 | **K2 群体伤害**（+ 无防御角色免伤的限制见 §二） |
| JC048 | 诅咒沙漏 | 结附地区；准备强制触发：放时间标志→按标志数对地区所有角色造成等量伤害→≥3 则牺牲自身 | **全行已交付**（三条同时机效果；新取值写法 `计数:地区内卡上标志:…` 与谓词共用统计实现，见 §7.10 60） |
| JC049 | 深渊细语 | **额外费用：牺牲一个角色**；快速行动：牌库底两张入手 | **全行已交付（2026-09）**：① 额外费用＝**K10 框架**（`ExtraCostSpec.SacrificeCharacter`；必须支付、无合法牺牲对象则不能打出、由打出者自选、牺牲＝死亡；见 §7.10 65）；② `DrawFromBottom{2,"不计抓牌"}`（不广播 `OnDraw`、空库不判负，见 §7.10 53） |
| JC050 | 丧钟回响 | 行动：选地区，消灭该地区**所有**角色与暗藏者 | **K2 群体消灭** |
| JC051 | 诺恩纳格斯塔 | 领袖；现身：牌库找徽章附属→**放置进场并结附合理目标**→洗牌；持续：其他本方鸣钟教派角色获白星 | **✅ 全行已交付（2026-09）**：`Search{子类别:徽章,结附,按目标规则}`（宿主候选＝被检索卡自身 `TargetRules` 的合法宿主，**候选 ≥2 时由玩家选**——§7.4b **A53** 撤销了早先"引擎自动选宿主"口径）＋ 光环按 Organization ＋ 白星 K20；见 §7.10 81 |
| JC052 | 终焉的诗寇蒂 | 领袖；进场：消灭本地区**所有**其他角色与暗藏者 | **K2** |
| JC054 | 蒙古死亡蠕虫 | 潜伏强制触发：对本地区其他角色 1 伤害；结束步骤强制触发：潜伏 | ✅ **已全行交付**（新时机 `OnLatent` + 群体伤害 + 结束步骤 `Latent` → `P3LatentTests`(5)） |
| JC055 | 安妮斯朵拉 | 准备触发：目标角色/附属放时间标志；行动：移除 X 个标志→下张灾难牌费用-X | **全行已交付（2026-09）**：① 准备步骤 `PlaceCounter`；② `CounterCostSpec.AmountByPlayerChoice`（**X 由玩家逐卡分配**、X≥1、入堆叠前支付）＋ `ValueExpr="代价数量"` ＋ `Gain{"费用减少","$X","子类别:灾难","一次性","打出,现身"}`（**打出与现身双通道**）；见 §7.10 67 |
| JC056 | 地区警员 | 公开；进场触发：横置**所有**本地区暗藏者 | **K2 群体横置** |
| JC057 | 监控员 | 现身：**检视目标玩家手牌**；持续：未横置时可随时检视本地区任一暗藏者 | ✅ **全行已交付（2026-09）**：第 1 行＝K8 检视 + K24 玩家目标（`P3PlayerTargetTests`）；第 2 行＝**持续可见性授权**（`StaticKind.AllowInspectLocalCovert` + `SelfUntapped` 前提）＋ **鼠标悬浮预览 UI**（`CovertInspectRule` 判定单一真相源 + `CardHoverPreview` + `CardPreviewBus`），见 §7.10 77 / §7.4b A46 |
| JC059 | 安全保卫部门 | 公开；持续：本地区**所有其他**本方角色 +1 防御 | 光环需支持 **"本地区 + 影响防御"**（现光环=控制者争夺区+grants，`Defense` 无 ContinuousGrants 通道）→ K13 变体 |
| JC061 | 危机处理特派小组 | 机动；触发：进入一个地区时可支付 1→消灭该地区目标暗藏者 | **K12 OnMove**（进入地区含机动）+ **K11 Pay/可选支付** |
| JC062 | 调查档案 | 快速行动：检视至多两个目标暗藏者；抓一张牌 | **K8 检视**（多目标） |
| JC063 | 警车追捕 | 快速行动：**选择一项**：潜伏目标角色 / 目标暗藏者回手 | **K16 模式分支** + 潜伏动词（`Bounce` ✓） |
| JC064 | 戒严 | 结附地区；准备强制触发：支付 1 否则牺牲；持续：非【国家机构】角色不能正面打出/移动进入本地区 | **✅ 全行已交付（2026-09）**：`StaticKind.BlockNonOrgEntryInArea`（正面打出 + 移动进入两个消费点）＋ `PayOrSacrificeSelf{1}`（**由玩家选择**支付或牺牲；费用不足直接牺牲）；FAQ 的放行路径自然成立；见 §7.10 70。混乱 FAQ 按裁定暂不做 |
| JC065 | 审问嫌疑人 | 快速行动：选地区→**目标对手牺牲该地区一个暗藏者**；遏制1 | K24 ✅（地区槽+玩家槽组合）+ K6 ✅；仍缺对手决策（K22 变体） |
| JC066 | 封魔瓶瑟琳娜 | 进场：封印本地区目标角色/暗藏者；持续：**额外具有被封印角色的所有能力图标** | ~~Seal 动词 + **K13 图标镜像** + 附属进厂触点（K12 衍生）~~ **两行已交付**（① 封印方向修正为 `Seal{目标,自身}`；② `MirrorSealedIcons`，见 §7.10 ㉝） |
| JC067 | 约翰·F·纳特里 | 领袖/公开/声望；持续：本地区在对抗阶段**额外进行一次势力对抗** | ~~**K18 对抗流程改造** + 声望 D~~ **已交付**（`StaticKind.ExtraAreaPowerContest` + `AdvancePhase` 原地重入，见 §7.10 ㊴；声望已由 `RenownRule` 引擎级实现） |
| JC069 | 警犬 | 不能被装备结附；现身触发：放**锁定标志**；快速行动2：移动到具锁定标志者所在地区并获黑眼（每回合一次） | ~~**K5 标志** + 目标按标记筛选（K21）+ K23 + K12 OnMove~~ **已交付**（`TargetRestriction.HasCounter` + `Move{"自身","目标所在地区"}`，见 §7.10 ㊱） |
| JC070 | 虔心修士 | 公开；**声望**（含完整规则文本：地区对抗结束后比较未横置声望角色→额外放势力标志） | **声望机制本体未实现**（需 K18 阶段钩子 + 比较 + 放标） |
| JC073 | 苏族捕梦网 | 结附角色；持续：受结附角色获白眼 + **屏障** | **屏障机制**（授予/解除 + `TargetValidity` 判定） |
| JC077 | 庇佑圣灵 | 灵体；本地区非灵体本方角色将受伤时**改为由庇佑圣灵承受** | ~~**K3 伤害替代** + 灵体 D~~ **已交付**（**用户口径：此条实为持续效果**；`DamageRedirect = FriendlyNonSpiritLocal`，见 §7.10 ㊸） |
| JC078 | 光能盾 | 快速行动：本回合为目标角色**防止**接下来所有伤害 | ~~**K3/PreventDamage**~~ **已交付**（`PreventDamage`＝伤害护盾，见 §7.10 ㉜） |
| JC080 | 凯拉 | 公开/声望；触发：每当你抓牌→在本地区放 X 个势力标志（X=抓牌数） | **K12 OnDraw** + **K1 动态X** + 声望 D |
| JC081 | 亚洛尔 | 灵体；**除装备与祝福外不受附属**；额外费用：展示任意数量手牌，每张圣贤/圣图标牌费用-1 | ✅ **全行已交付（2026-09）**：额外费用（K10，见 §7.10 65；【圣贤】＝组织:圣贤、【圣】＝神圣领域）；[前缀]「除装备与祝福外不受附属」＝**子类别白名单静止规则** `StaticKind.OnlyAttachWithSubCategories{Param="装备,祝福"}`（**与 JC001 同款实现位的放宽版**，见 §7.10 73） |
| JC082 | 索亚之书 | **选择一项**：借阅（结附角色，+白眼）/ 收藏（结附秘社，快速行动2横置：本回合赢得下次调查对抗时抓牌） | ✅ **已全行交付**（宿主种类前提 `HostKind`/`HostIsCouncil` + 一次性承诺 `Pending` + 两条 TargetRule OR → `P3PendingEffectTests`(8)） |
| JC083 | 睿智的斯芬克司 | 准备触发：**选择一个数字**，展示牌库顶，相同则入手否则置底 | ~~需 **数值输入 UI**（全新交互类型）+ 牌库顶操作~~ **✅ 已全行交付（2026-09）**：`ValueExpr="玩家选择"`（K1 最后一类落地）＋ `ValueMin=0/ValueMax=20`（A31）＋ 复用数值输入窗（A36，结算前弹窗并挂起→重入）；动作 `RevealTopAndTakeIfCostIs`＝公开牌库顶 → 费用相同 **入手（不算抓牌）** / 不同 **置库底**。见 §7.10 66 |
| JC086 | 窃尸人 | 进场触发：将墓地中的目标角色牌置于手牌 | **K14 墓地目标** |
| JC087 | 格鲁尼的走私客 | 撤回；触发：进入地区时令本地区目标本方角色潜伏，若如此做放一个势力标志 | **K12 OnMove** + 潜伏动词 + K17 条件链 |
| JC089 | 毒血诅咒 | 结附角色；持续：-1 防御 + 白刀黑刀 + **获得威名** | ✅ **已全行交付**（负值 Attribute 授予 + 运行期授予【威名】可触发 → `P3RuntimeKeywordTests`(7)） |
| JC095 | 唐·格鲁尼 | 领袖/声望；进场：移除本地区**所有**敌方势力标志→放置等量本方 | **K6 标志移除** + K2 群体 + K1（等量） |
| JC098 | 陷入疯狂的人群 | 公开；持续：本地区**略过调查对抗步骤** | ~~**K18**~~ **已交付**（`StaticKind.SkipAreaCombat`，见 §7.10 ㉞） |
| JC099 | 徘徊的鬼魂 | 灵体；现身：牌库顶一张封印在本地区目标敌方角色上；持续：凡其上有封印的敌方角色失去魔法领域图标 | ~~Seal + **K21 目标筛选（敌+有封印）** + K13 领域屏蔽 + 灵体 D~~ **全行已交付**（`DomainSuppression`，见 §7.10 ㊻） |
| JC100 | 尖牙幻象 | 灵体；现身：可将**自身**封印在本地区目标角色上；触发：被封印时支付 1→该角色 -1 防御直到回合结束 | Seal 自身 + K11 Pay + 灵体 D |
| JC101 | 直视深渊 | 结附角色；持续：受结附角色**印刷文本框视作空白**且失去所有魔法领域图标 | ~~**K13 文本/领域屏蔽**（新状态 + 消费点）~~ **已交付**（`TextBlank` + `DomainSuppression`，见 §7.10 ㊼） |
| JC104 | 占卜师 | 进场：**预测3**（看牌库顶 3 张，任意数量排回顶/底） | 可复用 `CombatManager` 调查链路 + `InvestigationWindow`（**建议新增 `Prediction` 决策类型**）→ 接近 B |
| JC105 | 噩梦领主 | 灵体；持续：其他本方灵体获白星；触发：另一个本方灵体死亡时可支付 2→将之封印在目标角色上而非入墓 | **全行已交付**：光环按关键词筛选 ✅（P3AuraFilterTests）＋ **K3 死亡类第一批**（触发路线，见 §7.10 52）✅（P3DeathSealTests） |
| JC108 | 乔治·帕佐 | 快速行动1横置：牌库顶一张封印在目标角色上；持续：其上封印牌的敌方角色**重置步骤不能重置** | ~~Seal + **K29 重置步骤钩子** + K21 筛选~~ **已交付**（`StaticKind.NoUntapWithSealedCards` + `UntapAll` 消费点，见 §7.10 ㊳） |
| JC109 | 沉沦的巴德勒 | 结束步骤触发：牌库找灵体角色牌封印在自己上；持续：每有一张封印牌→一个黑刀 | **K7** + Seal + 按封印数缩放（K5 类） |
| JC110 | 闲人的绿地 | 灵体；**手牌行动4**：将自身封印在目标角色上；触发：被封印时横置或重置该角色 | **全行已交付**：第 1 条 ✅（手牌行动：`Scope=HandZone` 激活 + 来源生效区门禁 → `P3HandActivateTests`(5)）；第 2 条 ✅ **K16 触发版**（`Modes` 二选一、入堆叠时选定；见 §7.10 51） |
| JC111 | 混沌蝴蝶 | 灵体；不能被装备结附；快速行动1：检视目标玩家牌库顶；快速行动横置：将目标玩家牌库洗牌 | ✅ **已全行交付**（K8 ✅ + K7 ✅ + K24 ✅ → `P3PlayerTargetTests`(16)） |
| JC114 | 好奇的黑客 | 行动1横置：目标玩家展示手牌；可从中选一张并牺牲自身→该玩家弃掉该牌 | **✅ 已全行交付（2026-09）**：`Inspect{"目标玩家手牌"}`（私密查看）→ **新动词 `SacrificeSelfOptional`**（0/1 窗口；牺牲后继续、不牺牲则后续整段跳过＝"若你如此做"）→ `Discard{"1","目标玩家","由施术者指定"}`；见 §7.10 68 |
| JC117 | 红眼航班 | 结附秘社；快速行动1横置：将目标本方角色移动到目标地区（仅行动阶段） | ~~**K15 双目标（角色+地区）** + 禁原地~~ **已交付**（`NeedsAreaTarget` + `AreaTargetExcludesCardArea`，见 §7.10 ㊾） |
| JC120 | 庄严圣像 | 结附地区；触发：赢取本地区时**置入资产区成为资产** | **K12 OnAreaWon** + **K4 形态转换** |
| JC121 | 空鸣御守 | 附属+角色双类别/公开/灵体/不能移动；进场：结附本地区目标本方角色；受结附角色受伤改为对其造成 | **进场结附已交付（2026-09）**：`ActionVerb.Attach` 执行器（把来源卡自己结附到宿主）＋ `EnterTrigger` + 目标（角色+`Friendly`+`LocalZone`）+ `Attach{"目标"}`；**无合法宿主留在场上（A18）**（见 §7.10 61）；持续型伤害重定向 ✅（㊸）。**仍缺**：「不能移动」独立效果行（仅未结附于角色时生效，A18/A19） |
| JC122 | 哈克·亚当斯 | 快速行动2：放锁定标志；持续：凡具锁定标记者**不能翻面** | **K5 + K21 静态限制（按标记）**；翻面禁止需在 `SetCovert` 消费点 |
| JC123 | 饮魂者朱迪 | 快速行动2：放锁定标志；持续：凡具锁定标记者**失去黑星白星** | ~~**K5 + K20 图标移除 + K2 群体**~~ **全行已交付**（`IconSuppression` 持续型失去图标，见 §7.10 ㊺） |
| JC124 | 尼古拉斯·勒梅 | 行动3：放哲人石标志；强制触发：具哲人石标志的角色死亡时从墓地移回场上（**来源离场后仍生效**） | ✅ **已全行交付（2026-09）**：**构筑级（卡组级）全局触发**（`EffectData.DeckLevelRegistration` ＋ `DeckLevelTriggerRule`：卡组装载即按玩家登记一次、多张同名只生效一份、登记不因离场撤销）＋ **死亡瞬间计数器快照**（`CountersAtDeath` ＋ 谓词 `DiedWithCounter`）＋ `MoveToZone{"目标","争夺区","正面","拥有者"}`（回死亡时所在地区、控制者交回拥有者）；来源卡＝卡组里那张 JC124 实例 ⇒ **堆叠用 JC124 卡图**。见 §7.10 76 / §7.4b A44/A45 |
| JC126 | 产业扩张 | 行动：牌库顶一张正面置入资产区**成为资产** | **K4 形态转换** + 牌库顶→资产区 |
| JC127 | 终局 | 行动：选地区，移除该地区**所有**势力标志；若该地区≥3 时间标志则消灭所有角色 | **K6 + K2 + K5 查询 + K17** |
| JC128 | 替罪羊 | 行动：选目标敌方玩家，除非他移除三个本方势力标志，否则该玩家牺牲一个角色 | K24 ✅ + K6 ✅；仍缺对手二选一决策 + K22 牺牲他人角色 |
| JC131 | 空投物资 | 行动：牌库找一张牌→入手→洗牌 | **K7** |
| JC132 | 构筑静态障壁 | 行动：选地区，横置**所有**位于该地区的角色 | **K2 群体横置** |
| JC134 | 审判 | 行动：每位玩家牺牲一个资产 | **K14 资产目标 + K22 + 多玩家决策** |
| JC135 | 余震 | **额外费用：支付 X 费**；快速行动：消灭目标**印刷费用为 X** 的附属 | **全行已交付（2026-09）**：K10 框架（`ExtraCostSpec.PayTargetCost`）——**先选目标、再定 X**（X＝目标印刷费用；0 费附属则不需额外费用；合法目标费用均大于可用费用则不能打出）；效果＝快速行动 + 消灭该附属（目标已锁定入堆叠；见 §7.10 65） |

### 4.4b 需补基础机制（承接上表，9 张：前提超过 2 个小项）

| 卡 | 名称 | 效果要点 | 主要缺失 |
|---|---|---|---|
| JC013 | 貘 | 灵体；不能被装备结附；行动牺牲：横置目标角色或附属，然后消灭其上封印的所有牌 | **K14（附属不可作激活/触发目标）** + Seal 相关批量消灭；灵体 D |
| JC092 | 死灵召唤 | 行动：选地区，将墓地目标角色牌以暗藏者状态置入 | **K14 墓地目标** + 暗藏进场封装 |
| JC097 | 阿斯旺 | 隐匿（不能正面打出，只能秘密派遣进场）；袭击X（X=墓地角色牌数） | **隐匿打出门禁** + **K1 动态X** |
| JC107 | 崩塌的时空 | 行动：消灭目标附属或资产 | **K14 资产目标**（+ 附属目标同缺口） |
| JC113 | 无形捣蛋精 | 灵体；进场强制触发：每位玩家各牺牲一个附属 | **K22 变体（牺牲附属）+ 多玩家决策**；灵体 D |
| JC115 | 备用资金 | 结附秘社；快速行动横置：你获得 1 费（仅保留到步骤结束） | **K19 临时费用池**（费用模型改造） |
| JC118 | 停战协议 | 只能行动阶段；快速行动：选目标地区，本回合该地区略过对抗阶段 | ~~**K18 对抗流程改造**~~ **已交付**（本回合型 `ActionVerb.SkipAreaCombat`，见 §7.10 ㊷） |
| JC119 | 守秘人 | 现身：将手牌背面朝上封印在自己上；死亡触发：展示其上封印牌，可免费打出其中一张（视作快速，仍需忠诚） | **✅ 全行已交付（2026-09）**：封印回手标记 + `RevealSealedAndQueueFreePlay`（公开全部 → 选一张 → 登记）＋ **免费打出队列**（原子锁解除后 `PlayCard(freePlay, asIfQuick)`＝费用 0、视作快速、忠诚照常）；见 §7.10 72。⚠ 需选目标的牌本批不自动打出（待确认） |
| JC133 | 操控死尸 | 行动：选地区，令墓地目标角色牌正面进场置该地区，额外具不死生物类别 | **K14 墓地目标** + `SubCategory` 授予 ✓；混乱 FAQ D |

### 4.5 统计口径

| 档 | 张数 | 含义 |
|---|---:|---|
| ✅ 已完成 | 13 | 已注册 + 有测试 |
| **A** | **7** | 零新机制，照范式直接写卡脚本 |
| **B** | **26** | ≤2 个小前提（一条 restriction / 一个动词 / 一行 Emit / 一个前提种类 / 一个字段） |
| **C** | **89** | 需补至少一个**基础机制**（4.4 = 80 张，4.4b = 9 张） |
| 合计 | **135** | |

**D（词条/规则语义未定义，与 A/B/C 叠加，不单独计数）**：灵体 13、领袖 10、声望 8（含机制）、杀伤 2、
混乱 2、隐匿 1、撤回 3、屏障 2（含机制）、时间/锁定/哲人石标志 9（含 K5）。

**卡住原因的高度集中性**（C 档 89 张里）：
K1 动态 X ≈14、K2 群体动作 ≈14、K3 替代效果 ≈9、K4 形态转换 6、K5 计数器 9、K6 标志移除 6、
K7 检索洗牌 8、K8 检视 6、K9 弃牌 7、K10 额外费用 3、K12 时机接线 11、K13 授予能力 7、K14 目标种类 11、
K18 流程改造 3 → **只要做完 §五 的 P1+P2+P3，C 档会塌缩掉 2/3。**

---

## 五、实施顺序（**2026-09 计划变更：改为"进度表驱动"**）

> **本节的 P1…P5 批次已从"前置门槛"降级为"补机制时的参考清单"。**
> 真正的推进方式是 `JC卡牌效果进度表.md`（每行一个效果 + 实现/单测/人工检查三列）：
> **哪张卡的全部效果都能做，就先接入哪张卡**；若某张卡只差一个机制，就先补那个机制（用本节的 K 编号定位）。
> 这样每个小步都能落地可见成果，而不是等所有批次做完才动卡。

| 批次（参考，不再是门槛） | 内容 | 状态 |
|---|---|---|
| **P1 接线与补漏** | ① 时机补 Emit（K12）+ 附属进场触点；② 5 条 restriction；③ Attribute 白名单（K20）；④ `Gain` 负数；⑤ 潜伏动词；⑥ `Pay` 动词 | ✅ 已交付 |
| **P2 交互原语** | ① `Discard`（K9）+ `OnDiscard`；② `Search`/`Shuffle`（K7）；③ `Seal` 动词 + `OnSealed`；（④⑤⑥ 按用户调整延后到 P5） | ✅ 已交付（④`RemovePowerMark`+`Curb` 已于 **P4** 交付 / ⑤计数器 / ⑥伤害防止 仍延后） |
| **P3 目标与语义**（~45 卡） | ① `MapInstanceToTargetKind` 扩 资产/墓地/手牌/玩家（K14）+ 候选池与 UI；② 激活候选扫附属区/秘社群；③ 群体动作（K2）；④ 动态数值 X（K1）；⑤ 模式分支（K16）/条件分支（K17） | 🟡 **大部分交付**（累计交付 **K14 非争夺区目标（墓地/手牌/资产/附属区/秘社/封印区）+ 或种类 `KindAlt` · K23 次数上限 · K26 阶段排除 · K2 群体动作 · K1 动态数值 X · 条件分支 · K15 多目标（单机）**，逐项证据见 §7.10；用户 2026-09 已逐项确认口径）：**① K14 ✅**（`MapInstanceToTargetKind` 按区域映射 + `TargetZonePool` 统一候选/筛选/阵营/或种类；选择窗口＝单机 `PromptPickCard`／联机 `Kind.SelectZoneCard`；丢失复用版本号）——**"玩家目标（K24）"也已交付**（`PlayerTargetPool` + 独立窗口）；**② 已满足**（`Arena.RefreshActivateHighlights` 扫 争夺区+附属区、秘社走 `CouncilActivateWindow`；非争夺区目标候选已扩到 附属区/秘社/封印区）；**③ K2 群体动作 ✅**；**④ K1 动态数值 X ✅**（`ValueExpr` + `EffectValueResolver`；**X=0 ⇒ 不发动**；`玩家选择` 型仍未实现）；**⑤ 条件分支 ✅**（`Predicate` + `ElseBranch`）；**K15 多目标 ✅（单机＋联机）**；**K16 模式分支 ✅**（打出时选定、入堆叠后不可改、付费之前）；**K24 玩家目标 ✅**（任意对手含自己 + 独立窗口 + 与槽位组合） |
| **P4 机制级**（~30 卡） | ① 额外费用（K10）；② 形态/类别转换（K4）；③ 阶段钩子与对抗流程改造（K18/K29）；④ 声望机制；⑤ 构筑与唯一性（K25）；⑥ 临时费用池（K19） | ✅ **基本全部已交付**：**④ 声望机制已交付**（`RenownRule` + `KeywordEffects.RenownKeyword()` + `CombatManager` 势力对抗结束消费点；**不要**再用卡级 `Renown()`，会与引擎级规则各放一次标志）；**另已交付**：领袖（词条+规则钩子）、遏制/`RemovePowerMark`（K6）、预测X、**K10 额外费用（JC049①/JC081/JC135① ②）**、**K4 实例级形态转换（JC027/JC028）＋ 持续夺控＋子类别改写（JC036）**、**K19 临时费用池（JC115）**、阶段钩子（K18 略过/追加对抗、K29 重置步骤静止规则）；**仍未做**：**印刷类别改写 `CategoryOverride`（经 A32 确认"不做"）**、⑤ K25 构筑唯一性（`GameDeckValidator` 只做基础校验；A38 暂不做） |
| **P5 规则文本与延后项** | 补写 灵体/领袖/杀伤/混乱/隐匿/撤回 规则定义；并做 遏制（K6）/计数器（K5）/伤害防止（K3）及其 UI | 🟡 **大部分已交付**：**词条规则**＝灵体 ✅（`DamageRules` + `CardManager.DamagePreventer`）、领袖 ✅（本轮 `LeaderRule`）、声望 ✅（`RenownRule`）、杀伤 ✅（`CombatManager.KillBonus`）、隐匿 ✅（`TurnManager.PlayCard` 门禁）、撤回 ✅（`AreaManager.ResolveAreaWon`）、屏障 ✅（`TargetValidity.IsBarrierBlocking`）、公开/非资产 ✅、XX标志X ✅（`PlaceCounter`）；**遏制 ✅**（本轮：`RemovePowerMark` + `CurbRule` + 单机 `PromptCurbMarks`/联机 `Kind.CurbMarks`）；**仍缺**：**混乱规则文本（全项目无定义/无代码）**、计数器"移除作代价（`RemoveCounter` 动词）与按计数缩放"、伤害防止动词 `PreventDamage` 与替代效果（K3：伤害转移/死亡替代/去处替代）、检视私密信息（K8）及其 UI |

> 说明：张数为"该批次能解锁的卡数"估算（一张卡可能被多批共同解锁），不是互斥划分。
> 实际未做的机制缺口，见 §二 K 表中仍标 ⛔ 的行。

---

## 六、顺带发现的文档/代码不一致（建议一并修正）

| 位置 | 问题 |
|---|---|
| `制作指南/触发效果.md` §三、§四 | 声称"按 `TriggerPriority` 排序""由 `TriggerRegistry` 统一收集后一次性入栈"——**`TriggerPriority` 字段全项目不存在**；实际排序=按 `ControllerId` 分组 + 先手优先 + 玩家拖拽（`TurnManager.BuildTriggerOwnerGroups`） |
| `制作指南/触发效果.md` §三 | "`PushTriggersToStack` 已删除（D36）"与 `DirectPushEvents`（空集合、死分支）并存 ⇒ 新卡不要再往 `DirectPushEvents` 加时机 |
| `制作指南/事务卡制作流程.md` §二 | 列出 `TargetSpec.ForOpponent`/`PlayerFilter` 字段——**类里不存在**（实际字段：Kind/Filter/NeedsSelection/CanTargetSelf/IsLocalZone/IncludeCovert/Restrictions/Param） |
| `Script/Data/Effects/EffectData.cs:52-54` | 注释称 `OnCardPlayed` 无法响应事务打出；实测 `PlayCard` 末尾对所有卡类型无条件 Emit（事务也发） |
| `Script/Manager/EffectEngine.cs:17-18` | 类头注释"已落地 Tap/Untap/Destroy/Bounce/Move/Draw/Gain/Sacrifice"漏记 4 个（PrepareUpkeep/DealDamage/PlaceToken/DrawFromBottom） |
| `命名对照表.md` §九 | 与实测一致（✅/🟡/⚪ 标注准确），但需补 `DrawFromBottom`、`Move`(仅 source) 等语义备注 |
| `需求实现对照.md:984` | "触发收集完整环…当前只接了进场/现身"已过时（现已有 7 个真实时机） |

---

## 七、P1 交付与后续批次

### 7.1 本批已交付（全部有测试锁定）

**A. 触发时机接线（6 个新 Emit + 1 个结构性死角）**

| 时机 | 补点位置 | 备注 |
|---|---|---|
| `OnCardDied` | `CardManager.ResolveDeath`（**死亡=行为**：伤害致死 / 消灭 / 牺牲三原因统一入口） | 死亡在**卡离场之前**广播 → 死亡触发写**默认生效区**即可（**不需要** `Scope=Graveyard`）；去向可被替代效果改写（回牌库/手牌）而**死亡触发照常成立**；`DamagePreventer`（伤害被防止）/`DeathPreventer`（死亡被阻止）成立时**不广播**（没死就没有死亡触发） |
| `OnAreaWon` | `AreaManager.ResolveAreaWon`（计分之后、送牌库底之前） | 触发异步结算 → 改变去向类效果仍需 K3 替代效果 |
| `OnDraw` | `TurnManager.DrawCardsForPlayer` / `DrawFromBottomForPlayer` | 唯一抽牌收口（初始发牌/回合抓牌/效果抽牌都经此） |
| `OnMarkerPlaced` | `AreaManager.AddPowerMark` + `CardManager.AddCounter` | 卡上计数器放置也发（时间/锁定/哲人石标志的未来消费点） |
| `OnMove` | `ZoneManager.MoveToArea` / `MoveToAttachedZone` | **含争夺区→争夺区**（机动/夺控），同区重放不发 |
| `OnCardLeft` | `ZoneManager.MoveToHand` / `MoveToDeck` / `MoveToScore` | 仅当"原本在场上"时发 |
| 附属进场触发 | `ZoneManager.MoveToAttachedZone` | 原先只在 `MoveToArea` 发 → 附属的进场触发**永不触发**，本批修复 |
| `AreaManager` 注入 | `GameInitializer` 新增 `AreaManager.SetTriggerEmitPoints` | 地区层首次具备发点能力 |

仍**未接**：`OnCounter`（无反制/终止原语）。`OnDiscard`（P2 首轮）与 `OnSealed`（P2 本轮）均已接线，见 §7.3.1。

**B. 目标限制（5 条新增，均参与结算复检）**：`CostAtMost` / `Tapped` / `HasMagicDomainIcon` / `Organization` / `HasKeyword`。

**C. 图标 → 属性**：补齐 `白刀=先手战斗`、`黑星=势力`、`白星=先手势力`（原只有黑刀/白眼/黑眼），
`AreaStatsCalculator` 的**战力/势力**改读实例值并接入先手属性；顺带修掉"附属的先手值从不重算"（`RefreshFirstStrikeStats` 补扫附属区）。

**D. Gain 动词**：图标名 → Attribute（原先落到 Keyword 无任何数值效果）、支持负数（走 `Revoke`）、防御下限钳制。

**E. 新动词**：`Latent`（潜伏）、`Uncover`（现身）、`Pay`（支付，失败则**中止同序列后续动作**，实现"若你如此做则…"）。

**F. 费用修正过滤串**：新增 `CostModFilter`（服务端与客户端**共用同一实现**），支持
`组织:X` / `子类别:X` / `域:Blood|域:血` / `有域图标` 与 `|` 或语义（避免双条件重复减费）。

**G. 落地新卡 14 张**：JC006、JC010、JC029、JC039、JC042、JC058、JC071、JC075、JC088、JC090、JC091、JC102、JC103、JC112
（详见 `Cards/Effects/PlayerCards/JC/`，均已注册进 `JCEffects`；测试见 `P1NewCardsTests`）。

**H. 回归**：`P1EffectFoundationsTests`（13 条）+ `P1NewCardsTests`（4 条）+ 全量测试套件全绿。

### 7.2 与立项清单的差异（需回填档位）

- ✅ 已完成从 **13 → 27 张**：新增上述 14 张（原 A 档 7 张 + B 档 7 张）。
- 原 A 档 7 张里，JC029/058/071/075/091/102/103 **全部落地**；原 B 档已落地 JC006/010/039/042/088/090/112。
- JC116（撤回）、JC085/084（新 PrereqKind）、JC106（图标移除）、JC093/094/096（牺牲他人角色）、JC125/129/130 等仍待 P2/P3。

### 7.3 下一批（P2 交互原语）建议顺序

> **2026-09 范围调整（用户确认）**：原 P2 的七项拆成两批——
> **P2 = 弃牌 / 检索 / 洗牌 / 封印**（本轮做）；
> **P5 = 遏制（移除势力标志） / 计数器·标志 / 伤害防止**（连同这三项所需的 UI 支持与对应待确认项，一并延后到"规则文本实现"批次）。

**P2 顺序（每完成一个原语跑一次测试）**
1. ✅ `Discard` 动词 + `OnDiscard` 接点（解锁 JC031/034/038/040/041/114/130）
2. ✅ `Seal` 动词入口（底层 `MoveToSealedZone` 已具备）+ `OnSealed`（解锁 JC009/066/099/100/105/108/109/110/119）
3. ✅ `Search` + `Shuffle` + 选牌窗（解锁 JC007/025/026/032/033/051/109/131）

#### 7.3.1 P2 交付明细（2026-09 完成）

**① 弃牌（`Discard`）** —— 见 §7.3.2 首轮交付说明（`TurnManager.DiscardCards` / `DiscardRandom`、`OnDiscard`、`DiscardByEnemy`、`PromptDiscard`、`SelectHandCard`）。

**② 封印（`Seal`）**
- 唯一出口 `TurnManager.SealCard(sealing, host, faceDown)`：双向引用（`SealedBy` / `SealedCards`）+ 标记（`IsSeal` / `IsSealed`）+ 进 `SealedZone` + 广播 `OnSealed`；
  宿主离场时封印卡回 OWNER 手牌（沿用 `ZoneManager` 既有连带规则，已验证）。
- `ActionVerb.Seal` 执行器：来源矩阵 `自身` / `目标` / `牌库顶`（自动从牌库列表移出）/ `手牌`（控制者选一张）；
  宿主 `目标`（缺省）/ `自身`；附加 `背面朝上` = **信息隔离**（JC119）。
- 信息隔离全链路：`CardInstance.IsSealedFaceDown` → 投影 `ZoneCardRef.IsSealedFaceDown` → `SnapshotFilter.IsHiddenFor`
  （非控制者只收占位；占位**保留**宿主/地区引用，因为"封印在谁身上"是公开信息）→ 客户端重建。
- UI：取手牌走通用 `IGameUIHooks.PromptPickCard`（单机）/ `SelectHandCard`（联机）。

**③ 检索（`Search`）**
- `DeckSearchFilter`（新文件 `Script/Manager/DeckSearchFilter.cs`）：`;`=AND、`,`=OR；支持
  `费用<=N`/`费用>=N`/`子类别:`/`类别:`/`关键字:`/`组织:`/`卡名:`；**未知字段 fail-closed**（防"字段名写错 → 静默匹配全库"）。
- 去向矩阵：`手牌`（缺省）/ `进场`（来源所在地区正面）/ `暗藏进场`（**先翻面再进场**，与"派遣暗藏者"同序）/ `结附`（绑定 `target`；`附加="地区"` 则结附本地区）/ `封印`（宿主取 `附加`）。
- 范围：`牌库`（缺省）/ `牌库顶:N`（JC032 的 6 张）。
- 洗牌语义（**用户拍板，2026-09 修订**）：**取到牌 → 洗牌**；**没找到 → 洗牌**（"然后洗牌"对没找到同样成立）；
  **玩家放弃 → 检索行为根本没发生 → 不洗牌**（牌库不得因一次未发生的检索被打乱）。
  ⚠️ 与初版口径的差异：初版为"找到/没找到/放弃都洗牌"，已按用户复核改为"放弃不洗牌"（用例 `TestP2Search_联机_放弃取牌_不洗牌` 锁定逐位牌序不变）。
- **"检索≠抓牌"**：检索入牌库→手牌**不广播 `OnDraw`**（抓牌＝从牌库顶抽，检索是"加入手牌"）。
- 联机：`ChoiceRequest.Kind.SelectDeckCard`，候选**必须**带 `OptionCardRefs`（牌库对双方都不可见，客户端无法自行渲染候选）。

**④ 洗牌（`Shuffle`）**：`自己`/`对手`/`双方`，走 `DeckManager` 注入的**可复现随机源**（同种子同结果，已验证）。

**⑤ 回归**：`P2SealSearchShuffleTests`（22 条）+ `P2DiscardTests`（5 条）+ 全量套件全绿（0 fail / 0 err）。

**⑥ 仍未做（P2 外，2026-09 P4 收尾已核对）**：`Attach`（独立结附动词；检索的『结附』去向已可用且已按"被检索卡自身 TargetRules 选宿主"修订）、
`PreventDamage`（伤害防止动词）与替代效果（K3）。
**原列 3 项已交付**：`Reveal`（展示，P2 后续已做）、`PlaceCounter`（P3-标志）、`RemovePowerMark`+`Curb`（P4）。


**P5（原延后项）现状（见 §7.8）**：`RemovePowerMark`+`Curb` ✅ 已交付（P4）、`PlaceCounter` ✅（P3）、灵体伤害防止 ✅（`DamageRules`）；
**仍未做**：`RemoveCounter` 作代价/按计数器缩放、`PreventDamage` 动词与伤害/死亡/去处替代（K3）、检视私密信息（K8）、混乱规则文本。

### 7.4 P2 已确认口径（用户拍板，实现时以此为准）

| # | 问题 | 确认结果 |
|---|---|---|
| 1 | 弃牌的选择方式 | 四种分类一致：**自选 / 随机 / 被指定者自选 / 由施术者指定**；"令目标玩家弃一张" = **被指定者自己选** |
| 2 | 随机弃牌 | 复用项目的**可复现随机源**（`RandomSourceHub`），保证联机双端一致 + 重连可复原 |
| 3 | JC114「展示对手手牌后指定一张」 | **允许**把对手手牌的临时视图**只下发给施术者本人**（信息隔离下的授权机制） |
| 4 | 检索"没找到 / 放弃"时的洗牌 | **没找到 → 仍然洗牌**（卡文"然后洗牌"无条件执行）；**放弃 → 不洗牌**（放弃意味着检索行为根本没发生，牌库不得被打乱）——2026-09 用户复核修订 |
| 5 | JC051「结附于合理的目标」 | **⛔ 本行已被 A53 撤销（2026-09，用户裁定）**，现行口径见 A53：宿主**候选**仍＝被检索卡自身的 `TargetRules` 的合法宿主（与打出阶段同一实现 `CardManager.MatchesRule`；如检索到 JC012『结附于本方秘社』→ 候选＝本方秘社），但**候选 ≥2 时由玩家从中选**（单机 `PromptPickCard` / 联机 `SelectTarget`；候选 = 1 时不弹窗；无 UI 确定性兜底取第一个）；**无合法宿主 → 按"无合法目标"处理**（不结附、牌放回拥有者牌库，随后照常洗牌）。~~原"不再由施术者选、引擎取确定性顺序第一个（秘社 P1→P2→争夺区卡→附属卡）"的口径作废~~ |

### 7.4b 用户 2026-09 追加口径（**下一批实现的依据**）

> 与 §7.4 同级效力；后续轮次**必须**按本表实现，不得自行推断。

| # | 问题 | 确认结果（用户原话要点） |
|---|---|---|
| A1 | **K22 牺牲（作效果 / 作打出卡额外费用的一部分）** | ① 大多情况下牺牲由**效果发动者自己选择**牺牲谁；② **除非效果里明确写了"牺牲暗藏者"，否则不能牺牲暗藏者**；③ **牺牲可以触发死亡触发**（牺牲＝死亡）；④ **补充（2026-09 追加）**：卡文写"牺牲一个角色"（**没写"另一个"**）时，**可以牺牲来源卡自身** → 实现上 `SacrificeCostSpec.ExcludeSource` 仅当卡文含"另一个"时才置 true |
| A2 | **K3 替代效果** | **JC077、JC121 是「效果错误」，应为持续效果**（伤害改为由某卡承受＝持续）；**其他几个走触发**；卡文部分描述不清晰；**并非替代式效应**——"卡会先进入墓地后通过触发重新结附"。**补充（2026-09 追加）**：**不覆盖 JC079**（其"赢取时改判去处"的实现保持不变）。通用纪律：**对某张卡究竟是"触发"还是"替代式效应"有疑问的，一律先与用户确认后再写** |
| A3 | **K20 图标替换** | 图标替换＝**属性替换**；那四个效果（JC099/JC101/JC106/JC123）都是**影响所有图标**，**任意顺序**完成所有图标替换即可；整个替换过程**包装在效果内部**（原子锁会拦住在替换过程中生效的其他触发）。另：**"印刷文本框视为空白"（JC101）还会让受影响的卡失去效果**（但**不影响已经发动的效果**） |
| A4 | **JC118「本回合中该地区略过对抗阶段」** | **三步骤全部略过**（调查/战斗/势力） |
| A5 | **JC110「你可以横置或重置该角色」** | **发动时由发动者选择**横置或重置；**补充（2026-09 追加）**：选择时机＝**入堆叠时选定**（与 K16 卡牌模式"打出时选定、入堆叠不可更改"、K15 激活"目标地区入堆叠时锁定"同一口径；**不是结算时**再弹窗） |
| A6 | **JC057「随时检视本地区的暗藏者」** | **原为"延后"（2026-09 早期口径）；已由用户本轮明确"开始制作"并交付**（见 A41/A46 与 §7.10 77）：持续可见性授权 ＋ 鼠标悬浮预览 UI |
| A7 | **JC019 运行期授予激活能力（宿主获得快速行动能力）** | **若宿主原本已有激活效果 → 增加一个选择窗口**，与"其他有多个激活效果的选择窗口"一致（＝复用 `ActivationChoiceWindow`）；**宿主有多个激活效果就在原本的选择项窗口内额外增加 1 项** |
| A8 | 通用纪律 | **不要实现任何有疑问或规则不清晰的地方**；不清楚的规则**先提问确认**后才能编写 |
| A9 | **JC120 庄严圣像（卡文写"触发：赢取本地区时 → 置入资产区"）** | **确认沿用 JC079 口径走「替代效果」**（`Timing = Static` + `WinAreaDestination = "资产区"`）：赢取结算里"本地区卡归位"是**同步**跑完的、入堆叠触发是**异步**的，按触发实现在生产路径上必然失效（触发结算时卡已回牌库底）；**不做**"先结算触发再归位"的时序改造 |
| A10 | **K3 死亡类（JC105 / JC124）** | **确认走触发路线**（卡先进入墓地 → 死亡触发 → 重新结附/回手）；**JC105 已按此交付**（见 §7.10 52）。**JC124 亦已交付**（2026-09，见 §7.10 76 / A44）——其"离场后仍生效"由**构筑级（卡组级）全局触发**承担，且加一条**死亡瞬间计数器快照**（A45）以支撑"具哲人石标志"这类条件 |
| A11 | **多玩家牺牲编排（JC046 / JC113 / JC134）** | 结算时**阻塞进程**：**先手玩家先弹选择 UI**选牺牲 → **后手玩家能看到先手的选择（UI 高亮表示）**→ 后手再选 → **两张卡一起牺牲**（原子锁期间**不必区分死亡先后顺序**）；牺牲**可以触发死亡触发**。**补充（用户 2026-09 确认）**：若某玩家**没有可牺牲的卡**，该玩家**跳过、什么都不付出**（与 A12 的 JC128"没有角色则什么都不付出"同口径） |
| A12 | **JC128 替罪羊** | 势力标志**不足 3 个时必须牺牲**（不能"因为标志不够就免掉"）；**若玩家没有角色**，他仍可以选"牺牲一个角色"，此时**什么都不用付出**（即使标志足够，没有角色时也可以选牺牲路线） |
| A13 | **JC114 好奇的黑客：牺牲自身作代价的时机** | **在入堆叠前支付**（参考 **JC015 驱魔人**的既有写法：代价随激活/打出一起付、不可被响应） |
| A14 | **"参与了该次战斗"的判定（JC022 / JC034）** | 按**近似**判定＝**位于战斗地区且属于获胜方**（＝复用既有谓词 `WinnerIsSelfInSameArea`；事件 `OnCombatWon` 只带胜者玩家＋战斗地区，不另记参战者名单） |
| A15 | **JC027 花园地精像：附属→角色（形态）** | 效果发动后**实例的属性（`CategoryFlag`）变为「角色/附属」组合**——与 **JC121 的"属性"处理同款**；还原＝把属性**变回附属**。**注意与游戏既有的"转变（转变卡）"机制不同**，此处**不是**转变机制 |
| A16 | **JC036 初拥：控制权与子类别改写** | **回合结束不消失**——**卡文没有写"持续到回合结束"的效果都是"持续尽可能久"**；控制权在「JC036 在场 **且** 其版本号未变化」期间一直生效，直到 **JC036 离场或版本号变化**为止；**受控角色所在地区不变**，切换的是**争夺区归属与 controller**。**✅ 已交付（2026-09，见 §7.10 64）**：实现按"持续效果自身生命周期"落地——**不另设版本号判据**（用户补充口径：版本号变化一般伴随初拥离场，被初拥角色离场时作为附属的初拥也一同进墓地，两条路径都落在持续效果的注销分支）；注销时控制权**归还拥有者**（卡面 FAQ 原文）。另：其 FAQ 第二条（宿主翻面为暗藏者 ⇒ 初拥被消灭）**按用户口径落地为通用规则** `AttachmentRule`（任何「结附于角色」的附属，宿主翻面为暗藏者时被消灭） |
| A17 | **"自本方卡牌上移除一个时间标志"（JC044）** | 就是**发动代价**（不当作效果步骤） |
| A18 | **JC121 空鸣御守（未结附/结附两种形态）** | **没有宿主时留在场上**（无宿主状态）；「**不能移动**」是**独立效果行**，**只有未结附于角色时才生效**；**结附于角色时本卡随角色卡一起移动**（作为附属参与角色所在地区对抗，现有实现天然支持）。**JC122**：只限制**场上有锁定标记的暗藏者**、**只需限制"现身"行为**。**JC119 延后** |
| A19 | **JC064 戒严** | "不能移动进入本地区"**涵盖机动/效果移动/置换**；"支付1，**否则**牺牲戒严"＝**由玩家选择**支付或牺牲 |
| A20 | **K10 额外费用 / K25 唯一性构筑 / K19 临时费用池 / 联机侧路径** | **可以排期**（均进入可实施状态）；**但实现前对细节有疑问必须逐条向用户确认**，不得以猜测补逻辑 |
| A21 | **JC049 的「额外费用：牺牲一个角色」** | **必须支付**——它**是打出 JC049 的费用之一**；**如果没有办法牺牲（无合法牺牲对象）则不能打出 JC049** |
| A22 | **JC081 的「额外费用」（展示手牌减费）** | 这是**费用减免**：通过展示手牌降低费用；**可以不展示**（＝没有减免），**不展示也能按全额费用打出 JC081** |
| A23 | **JC081 的交互（展示手牌窗口）** | 打出这张牌时**弹出提示框选择手牌**，框内**确认/取消**两个按钮：**取消 → 不打出、卡不入堆叠**；提示框存在期间**可选中手牌、选中的手牌高亮**；**确认后给对手展示所有高亮手牌**→ 费用减免 → **JC081 进入堆叠** |
| A24 | **JC135 的「额外费用：支付X费」** | 需要**场上有印刷费用数值等于所支付额外费用的卡**（**印刷＝卡片模型的费用**）；**若场上有费用为 0 的附属，则可以不支付额外费用**。UI＝**输入框 + 确认 + 取消**：输入数字后若场上**没有**印刷费用为该数字的卡 → **确认按钮置灰不可点**；点确认即**支付费用并打出 JC135** |
| A25 | **JC034「你只能操控至多一个道途」（K25 场上唯一性）** | **与【领袖】类似**：存在多个道途时**由该玩家选择一个牺牲**（＝复用 `LeaderRule` 的同一套处理） |
| A26 | **JC115「快速行动，横置：你获得1」（K19 临时费用池）** | 本游戏**没有"有色费用"**这种说法，**所有费用都是"无色费用"**；这 1 点是**临时费用**，**在任意步骤结束时消失**（先手步骤 / 后手步骤 / 预对抗步骤 / 调查对抗步骤 / 战斗对抗步骤 / 势力对抗步骤**结束时清除**） |
| A27 | **JC044「自本方卡牌上移除一个时间标志」的选择者** | **由发动者自主选择**（确认） |### 7.5 伤害模型口径（P5 用，先记档）
| A28 | **JC028 鲜血奴仆（形态转换）** | 牺牲后回场成为附属时：**改卡名**（改为"鲜血"）、**类别改为附属（不保留角色类别）**、**可结附于任意地区的目标吸血鬼角色**、**原本的图标消失**（战斗图标消失、防御消失），**只保留"鲜血"的效果**（受此结附的角色**获得战斗图标 + 防御 +1**） |
| A29 | **JC041 禁婆（水域地区）** | "水域"是**地区子类别**（地区卡库已有，例：`Cards/Data/AreaCards/DQ001_chenmodefeixu.tres`）；**本卡需要 UI 支持**：暗藏者状态下若**位于水域**，**双击角色不直接现身**，而是弹出**选择框**（"正常现身" / "激活暗藏者效果"，**可取消**）；该效果的实现即为"这张卡现身" |
| A30 | **JC055② 的 X 交互** | 效果弹出**类似战斗伤害分配的选择框**：自己场上**有时间标志的卡高亮**，**点一次某张卡即记录用 1 点**，点**确认**后移除对应的时间标志；**未打出则该减费保留到回合结束消失** |
| A31 | **JC083「选择一个数字」** | 数字范围 **0～20**；所展示牌置入手牌**不算抓牌**（不触发"每当你抓牌时…"） |
| A32 | **"形态转换"的作用层面（JC027 / JC028，2026-09 追加）** | **JC027 和 JC028 也是只修改【实例】，不会有任何效果改变卡的【印刷类别】** —— 即 K4 的 `CategoryOverride`（印刷类别改写）**不需要做**；形态转换一律在**实例层**落地：`CategoryFlag` 实例覆盖（附属↔角色）、实例改名（JC028「鲜血」）、清空实例图标/防御、以及"自我重进场并结附"。配套含义：**`HasStaticSubCategory`（印刷类别＝模型数据）恒等于卡面**，任何卡文写"**印刷类别**为 X"的判定（如 JC036 的结附限制）永远读模型数据；写"X 角色"的判定读**实例当前类别**（`CurrentSubCategory`） |
| A33 | **K10 额外费用的时机与三条实现细节（2026-09 追加）** | 额外费用属于**打出卡牌支付费用阶段**（卡牌打出即需支付）——**不在堆叠里结算、不可被响应**（类比万智牌）。逐卡：**JC135＝先选目标、再定 X**（X＝目标印刷费用；若合法目标的费用均大于可用费用则该卡不能打出；选 **0 费附属为目标时不支付额外费用**、直接结算后续效果）；**JC049＝必须支付**、无合法牺牲对象则不能打出（联机侧需把"选谁牺牲"回环给该玩家，复用 `Kind.SelectTarget`，非新窗口类型）；**JC081＝不展示即全额支付**、取消不扣任何费用、减免一次性。**注**：A24 原文提到的"输入框+确认+取消"因"先选目标再定 X"而**不再需要** |
| A34 | **JC027「成为一个地精角色」（2026-09 追加）** | **是**：实例层**同时加上「地精」子类别**（与"类别覆盖为角色/附属组合"一起落地）。卡库中只有本卡提到地精，暂无其它卡按它筛选 |
| A35 | **JC081 的过滤器写法（2026-09 追加）** | 「**【圣贤】牌**」＝**组织:圣贤**（`CostModFilter` 的 `组织:` 前缀）；「**具有【圣】图标**」＝**神圣魔法领域图标**（`MagicDomain.Divine`，卡库单字写法：心=心灵 / 圣=神圣 / 星=星空 / 死=死亡 / 血=鲜血）。故筛选串＝`组织:圣贤|域:神圣`（`|` ＝或） |
| A36 | **交互复用（2026-09 追加）** | **JC083**「选择一个数字」（0～20）**复用既有数值输入窗**（`PromptNumberInput` / 联机 `Kind.NumberInput`），不需要新 UI；**JC114**「展示对手手牌 → 指定一张」**复用私密检视窗 + 选卡窗**，**允许取消**（取消＝不牺牲、什么都不发生）；**JC119** 死亡触发：**"视作具有【快速】"＝不受行动阶段限制**，**费用置 0 但忠诚照常校验**，顺序＝**先展示全部封印牌 → 你选一张打出 → 其余立即回拥有者手牌** |
| A37 | **JC041 的两条效果归属（2026-09 追加）** | ① 「快速暗藏者行动2：令禁婆由暗藏者状态现身，仅当位于水域地区」——这是**暗藏者专用**的激活能力（**只有作为暗藏者、且位于水域地区时才能激活**；区别于此游戏多数效果需正面在场生效）＝需要"现身效果化 + 地区子类别前提 + 双击弹选择框（A29）"；② 「现身强制触发：每位玩家各弃一张手牌」＝**多玩家决策部分**（**该条已交付**，见 `P3TwoPlayerDiscardTests`）。故 ⛔ 行里的"多玩家决策编排"备注属**误记**，实现①时不需要 |
| A38 | **本轮明确"暂不做"的两项（2026-09 追加）** | **混乱（Chaos）规则文本**：暂不做（JC064/JC133 的 FAQ 行保持规则备注）；**K25 构筑唯一性**（JC125「无知路人」）：暂不做（等整体构筑规则定下"同名卡上限"数值后再接） |
| A39 | **「玩家选择」型 X 的范围与 X=0 语义（JC083，2026-09 追加）** | JC083「选择一个数字」的范围＝**0～20**（A31）→ **X=0 是合法选择**。与 K1 早先口径"玩家主动选择的 X 不能为 0"的交界处理：**X=0 ⇒ 不发动**只适用于"**数量**"型 X（计数/事件数量）；**JC083 的 X 是"猜测值"而非"数量"**，故由 `EffectData.ValueMin`/`ValueMax` 声明范围，**`ValueMin=0` 时 X=0 合法且效果照常结算**（否则无法猜"印刷费用为 0 的牌"）；对照 **JC055②** 的 X 是"移除的标志数量"→ 至少 1（A30 的补充口径：**不允许为 0**）。另外：**取消选择 / 无 UI → 该效果不发动**（fail-closed，绝不替玩家猜一个数字）|
| A40 | **JC081 [前缀]「除装备与祝福外不受附属」的落地口径（2026-09 追加，本批起做）** | 用户口径：**"与 JC001 类似实现即可"**。即沿用 JC001「不能被装备结附」的**同一实现位**，只是从"**一律禁止**"放宽为"**子类别白名单**"：新静止规则 `StaticKind.OnlyAttachWithSubCategories{Param="装备,祝福"}`＝**只有【装备】/【祝福】类附属能结附本卡**；判定**单一真相源**（`EffectEngine.GetAttachSubCategoryWhitelist`/`PassesAttachWhitelist`，读**卡面印刷规则** → 两端同解）；**两处拦截**与 JC001 完全一致：**打出阶段** `IsLegalPlayTarget` 拦截 ＋ **结算兜底** `ResolveEntry` 附属分支送墓地；**白名单为空或拼错 → fail-open 放行**（不因参数错误静默封死规则） |
| A41 | **本轮起做的延后项与"暂不做"（2026-09 追加）** | 用户口径：**"之前延后的现在都可以开始制作"**——**JC057**（持续：随时检视本地区任一暗藏者 ＋ 新的**鼠标悬浮预览 UI**）、**JC124**（构筑级全局触发：具哲人石标志的角色死亡 → 移回场上）、**JC081 [前缀]**（见 A40）、**JC119**（免费打出的**目标选择窗**接入）、以及**需要玩家选择的联机缺口两项**（JC049 牺牲选择 / JC081 手牌多选）均在**本批起做**；**JC125 暂不做**（维持 A38 的"不排期"） |
| A42 | **JC081「展示任意数量的手牌」的候选范围（2026-09 追加，口径补充·非用户逐字确认）** | 展示候选＝打出者当前手牌中**除正在打出的本卡自身以外**的牌。依据：卡文"展示任意数量的**手牌**"指手牌中**其它**的牌；且 JC081 自身组织就是【圣贤】（`Organization="圣贤"`）——若允许"拿自己给自己减费"，则每打一张 JC081 都会白减 1。单机与联机**同一实现**（`GetExtraCostRevealCandidates(playerId, source)`；付费阶段校验对本卡自身 UID 亦忽略）。**如与设计原意不符请指出，可立即改回"含自身"** |
| A43 | **K10 额外费用的联机实现方式（2026-09 追加）** | 沿用 **A33** 的既定口径：**JC049 的"选谁牺牲"回环给该玩家、复用 `ChoiceRequest.Kind.SelectTarget`（非新窗口类型）**；**JC081 的"展示任意数量手牌"**因是**多选且可空**，新增请求类型 **`Kind.SelectHandCards`**（`OptionLabels` 带服务端给出的合格/不合格标记、`NumberMax`＝最多可选张数；回包 `Confirmed`＋`SelectedUids`，可空＝不展示；`Cancelled`＝不打出）。两者都**由服务端接管**（`TryBeginExtraCostPlayOnline` → `PlayWithExtraCostAsync`），与 K15/K16 的联机接管同一套机制；回包一律**服务端权威复检**（越权/失效 → 整次中止，fail-closed） |
| A44 | **JC124 哲人石的"构筑级全局触发"与三条口径（2026-09 追加）** | 用户口径：**"制作一个有效果的指示物，或者说一个全局触发效果，只要 JC124 一开始加入玩家卡组就能触发这个效果"**（两种实现均可）＋ **"加入多个 JC124 也只能有一个效果生效"** ＋ **"效果加入堆叠用 JC124 的卡图"** ＋ 本轮补充 **回死亡时所在地区** / **按玩家各自登记**。落地＝`EffectData.DeckLevelRegistration` ＋ `DeckLevelTriggerRule`（登记点＝卡组装载；来源卡＝卡组里那张实例 ⇒ 卡图）；**登记不因该牌离场而撤销**（卡文"该能力在尼古拉斯·勒梅离场后仍然生效"）。**另含一条我按卡文字面做的判断（请用户确认）**：卡文**没有**"本方/敌方"限定，且行动3 的目标不限本方、"由其拥有者操控"正是为跨玩家场合准备 ⇒ **任意玩家**的具哲人石标志角色死亡都回场、回场后归**其拥有者**；双方都登记时第二份登记为幂等空操作 |
| A45 | **死亡瞬间的"卡上标志"读取（2026-09 追加，技术口径）** | 死亡触发在"**卡仍在场上**"时广播，但**结算发生在卡已进墓地之后**，而进墓地会 `ResetToBaseState()` **清空计数器** ⇒ 一切以"死亡时具有某标志"为条件的死亡触发（JC124 哲人石）都必须在**广播前**抓快照。实现：`CardInstance.CountersAtDeath` ＋ `SnapshotCountersAtDeath()`（`CardManager.ResolveDeath` 调用），谓词 `DiedWithCounter` 读快照（无快照时回落当前计数器）。快照**每次死亡重写**，不残留上一次判定 |
| A46 | **JC057 持续检视 ＋ 鼠标悬浮预览 UI（2026-09 追加）** | 用户口径：本条需要**新的 UI 改动**——**鼠标悬浮在卡上弹出悬浮 UI 显示整张卡图**；可见性四条：**①所有可见卡悬浮都弹**、**②自己的暗藏者也按同样方式弹**、**③正常情况下对手的暗藏者与手牌不弹**、**④若 JC057 在场（未横置）则悬浮它所在地区的敌方暗藏者可弹**。落地＝持续可见性授权 `StaticKind.AllowInspectLocalCovert`（`SelfUntapped` 前提）＋ 判定单一真相源 `CovertInspectRule`（静态纯函数，两端同解）＋ 悬浮浮层 `CardHoverPreview` ＋ 收口总线 `CardPreviewBus`（`Arena`/`ArenaOl` 装配）。**单机为本地同桌视图**，故其"查看者"固定为本机人类座位（玩家1）。**已知联机缺口**：联机客户端只有隐藏占位、没有对手暗藏者的卡面，授权成立也显示不出，需按需请求卡面（未做） |
| A47 | **暗藏者（背面朝上）的持续/静止效果一并失效（2026-09，用户裁定）** | 用户裁定：**"改为暗藏时持续/静止也失效（与触发/激活/光环对齐）"**。落地：`EffectEngine.RegisterPersistent` 在 `card.IsCovert` 时**跳过注册**（不授予图标/属性/关键字、不登记地区级静止规则、不封锁秘密派遣），装配层 `CovertChanged` 钩子**注销/重新注册**（**潜伏即失效、现身即恢复**）；两个"读**印刷规则**而非注册表"的判定点各补一次 `IsCovert` 门禁：`EffectEngine.GetAttachSubCategoryWhitelist`（JC081 前缀）、`CovertInspectRule.HasPrintedLocalCovertInspect`（JC057 授权）。口径总结：**暗藏卡不提供任何持续/静止规则**（触发默认 FaceUp、激活硬门禁拒暗藏、光环要求源卡正面、持续/静止同款） |
| A48 | **「进入一个地区」= `OnMove`（位置变更之后），且 `OnMove` 为自身语义（2026-09）** | JC061/JC087 的卡文都是"当**此卡**进入一个地区时"⇒ 时机应为**位置变更之后**：`ZoneManager.MoveToArea` 在移动后广播 `OnMove`（带 `SourceAreaAtTrigger`＝进入的地区），**进场与"争夺区→争夺区"移动都覆盖**（原 `ManeuverTrigger` 实为"对抗前窗口打开时"、`EnterTrigger` 只覆盖进场，两者都是近似）。同时把 `OnMove` 加入 `TriggerRegistry` 的"**只对事件主体卡成立**"白名单（否则**任何卡移动/进场都会误触发**）。若将来需要"当**其他**角色进入本地区时…"这类**旁观**效果，需另加时机或谓词，不在本白名单放开 |
| A51 | **暗藏者现身必须校验额外费用（2026-09，用户裁定）** | 用户裁定：**"没有特殊说明的情况下需要校验额外费用，JC 卡包内是都需要校验"**。落地＝把 `PlayCard` 的额外费用解析抽成 `TurnManager.TryResolveExtraCost`（**单一实现**），`RevealCovert` 复用：JC081（`RevealHandCards`，可选）现身可展示手牌**减免本次现身费用**（候选须在手牌且不含本卡自身）；`SacrificeCharacter` 候选须合法、无候选 → 拒绝现身；`PayTargetCost` 需目标而现身路径无目标交互 ⇒ **拒绝现身**（fail-closed）。单机用与打出 1:1 的同款窗；联机服务端接管（`SelectHandCards`/`SelectTarget`） |
| A52 | **延后项裁定：JC 卡包未使用即延后（2026-09，用户裁定）** | 用户口径：**"我需要明确的 JC 卡包内是否用到这些效果，如果没有用到可以延后"**。已实测：`GenerateToken`/`Investigate`/`Strength`/`Power`/`Win`/`Lose` → **JC 包与全卡池均 0 处使用**；`TargetRestriction.CombatCapable` → **全卡池 0 处**；`ResolveKind.NewArea`（预留"任务/地区卡"）→ **全卡池无 任务/地区/指示物 类别**（CategoryFlag 8/16/32 零命中）⇒ **三项延后**（现状：六动词显式报错、CombatCapable 落 default 判不通过、NewArea 仅打印）。**何时需要补**：一旦有卡声明这些动词/类别/限制，立即按本表对应机制实现（不得再让它们静默降级） |
| A50 | **联机激活的"选择类入参"1:1 复刻单机窗（2026-09，用户裁定）** | 用户裁定：**"按单机那两套窗 1:1 复刻"**（地区目标窗 / 代价选择窗）。落地方式＝**服务端接管**（`TryBeginActivationChoiceOnline` → `TurnManager.ActivateAbilityWithChoicesAsync`）：顺序与单机 `Arena.BeginActivate` 一致（**目标地区 → 牺牲 → 标志 → 玩家目标**），候选由服务端用同源规则函数算出并随 `ChoiceRequest` 下发（**客户端不重算规则**），回包权威复检、失败即**整次不发动**；客户端用**与单机同款**的窗口渲染：地区＝"地区槽位高亮 + 点击"（`SelectTarget`）、代价＝`OnlineActivationChoiceWindow` 按钮列表（新 `Kind.SelectActivationCost`；逐卡分配模式 1:1 复刻 `ShowCounterAllocationStep`）、玩家目标＝`SelectPlayer`。**架构说明**：单机的候选来自本机 `EffectEngine`；联机客户端没有引擎/规则实例，故"同样的窗"由服务端喂数据（这是本设计的关键取舍） |
| A49 | **联机"玩家选择"的接线纪律（2026-09）** | **引擎侧与回合侧的网关都必须注入**：`MatchHost.StartServing` 现同时注入 `TurnManager`/`CombatManager`/**`EffectEngine`** 三处（否则联机时引擎侧决策退化为服务端自动代选）。另：**所有联机回包一律做权威复检**（必须命中服务端本次下发的候选），已覆盖检索/弃牌/封印/触发选目标/选玩家目标/额外费用/K15/遏制/预测/**激活代价**；**候选显示名由服务端随请求下发**（`ChoiceRequest.OptionLabels`），因为牌库/对手手牌/墓地在客户端是隐藏占位、无法本地解析卡名 |
| A53 | **检索一律强制（不可取消）＋ JC051 宿主改由玩家选（2026-09，用户裁定；撤销 §7.4b 第 5 条）** | 用户两项裁定：**①"收紧为强制：单机也去掉取消，与卡文和联机一致"**（8 张 JC 检索卡 JC007/025/026/032/033/051/109/131 的卡文**都没有"你可以寻找"**）⇒ 单机 `EffectEngine.ResolveSearch` 改 `allowCancel: false`；联机 `SearchViaGatewayAsync` 改 `ForceChoice = true`，且回包**取消/非法**不再等于"不取牌"（**原口径"玩家放弃 = 检索未发生 → 不洗牌"作废**）⇒ **fail-safe 兜底取第一张匹配**再落地 + 洗牌，否则异常客户端可用一次取消/越权回包**规避强制检索**；安全属性不变（候选只来自"检索者牌库 ∧ 通过筛选"）。**②"改成开窗选宿主"**（JC051『结附于合理的目标』）⇒ 宿主**候选来源不变**（＝被检索卡自身 `TargetRules` 的合法宿主，判定与打出阶段同一实现 `CardManager.MatchesRule`），但**候选 ≥2 时由玩家选**：单机 `IGameUIHooks.PromptPickCard`（不可取消）/ 联机 `Kind.SelectTarget`（服务端 `OptionLabels`，`ForceChoice=true`，回包权威复检）；回包非法/取消 → **确定性兜底取第一个合法宿主**（强制结附不可被规避）；**候选 = 1 时不弹窗**（无可决策空间）、无 UI 时确定性兜底取第一个。实现细节：去向落地改为**续算式** `ApplySearchDestination(..., onDone)`（选宿主窗挂起期间不洗牌，宿主落定后才"揭示 + 洗牌"）；卡数据 `Args[2]` 由 `"自动"` 改 `"按目标规则"`。**留档**：若将来出现"你可以寻找…"的卡，需先在 `Args` 加可选标记再恢复取消分支 |
| A54 | **秘社重放幂等 ＋ 联机"秘社目标"可点（2026-09，用户裁定"现在就修／补上"）** | 用户对两项"顺手发现"的裁定：**"现在就修"＋"补上"**。① **`SetupCouncils` 幂等化**（`ZoneManager.ClearCouncilZone` 新方法：旧秘社按 `CardZone.Exiled` 移出游戏 + 广播 `OnCardMoved` + 注销持续效果登记）——原先 `DealOpeningHands` 的"重放秘社"会累积 **2 张/玩家**，导致起手数读旧卡（回退 6）与"结附本方秘社"类效果重复候选；**秘社恒 1 张**后两者同时消失。② **秘社 UI 三态化**（`Arena.Layout.SyncCouncilCardUI`）：不在秘社区→摘除、在秘社区但无 UI→**补建**、已有→确保容器，配套秘社重放；`SyncCardUIToZone` 秘社判定放宽为"当前在秘社区 **或** 曾有 UI"。③ **联机选目标补秘社通道**：`OpenOnlineTargetSelectionWindow` 外加高亮 `_councilCardUIMap` 合法目标、`PickLegalActivationTargetAt` 纳入秘社同款 ZIndex 命中（新增 `CouncilCardUI.GetVisualGlobalRect`）、清理同款；动机＝JC051 宿主候选含秘社（否则联机只能等超时走兜底）。④ 顺手修：`PromptPickCard` 候选名兜底 `CouncilTemplate?.CardName`（原显示原始 UID）；`MakePlayableCard` 补 `_effectsInjected`（否则 `EnsureEffects` 清空效果表 ⇒ 探针 `PlayTiming.Response` 被抹掉、静默退化 A 档 = 既有失败真因） |
| A55 | **联机"需要玩家选择"的效果必须真问玩家（2026-09，用户裁定"这一批全补齐"）** | 用户实测反馈：服务端日志出现 `⚠ Search 无 UI：确定性兜底取第一张匹配` ⇒ 选卡只能自动选择。**根因（架构级）**：生产链路是 `TurnManager.ResolveStackTop`（同步）→ `ResolveEntry` → **同步** `EffectEngine.ResolveEffect`，而网关分支只写在 `ResolveEffectAsync`/`ExecuteVerbAsync`/`*ViaGatewayAsync` 那套"可挂起续算"实现里——**全项目调用 `ResolveEffectAsync` 的只有测试** ⇒ 服务端（无 `UIHooks`、有 `InputGateway`）下所有决策都静默走无 UI 兜底。**修法**：① 在**同步**执行体里接入网关：`ExecuteVerb` 对 `Inspect/PayOptional/PrepareUpkeep/Discard/Seal(手牌)/RevealSealedAndQueueFreePlay/Search` 改走既有异步执行体，并用 `SuspendChainForInput`/`ContinuePendingChain` 挂起-续算（服务端不阻塞线程；**有 UIHooks 时仍优先本机窗口**）；② 补齐原本**没有**网关分支的 5 处：JC083「选择一个数字」`ValueExpr=玩家选择`（原先服务端**整条不发动**）、JC064 `PayOrSacrificeSelf`、JC114 `SacrificeSelfOptional`、JC128 `BribeOrSacrifice`、`SacrificeChoice`（JC046/113/134/65 逐位牺牲）；③ 统一收口 `BeginGatewayFlow`（同步/异步回包都正确接管链）+ `AskZeroOrOneViaGatewayAsync`（0/1 二选一）+ `AskPlayerValueViaGatewayAsync`（数字）；④ 等待回包期间由引擎**显式 `EnterAtomic`**（服务端没有 View 帮忙 `NotifyModalOpened`），续算后经新 `TurnManager.OnEngineChainSettled` 补做"地区赢取检查 / 解原子锁 / JC119 免费打出队列"（挂起时 `ResolveStackTop` 已提前 return）；⑤ `SuspendChainForInput` 加"不覆盖已记录续算点"护栏（嵌套决策不再丢动作）。**顺带修复**：JC128 单机窗口原先**未挂起动作链**（窗口异步，代码同帧按"牺牲"分叉 ⇒ 玩家选择被丢弃；旧测试用同步桩掩盖）。 |
| A59 | **原子操作期间的玩家选择必须阻塞（2026-09，用户补充口径·框架级）** | 用户口径原文见 `需求实现对照.md` §7.3.1：**一个原子操作期间如果需要玩家选择，玩家选择完成之前阻塞进程；完成前只有持续效果可以影响游戏，不得有新的行动/触发效果进入堆叠**；"打出卡（含支付费用/额外费用/确认忠诚）"与"结算卡/结算效果"都属原子操作。**排查发现的 4 类不合规（本次全部修复）**：① 单机**所有通用选择窗**在等待期间不持锁（只有触发排序/选目标/对抗三类窗口联动 `LockModal`）⇒ 结算中途还能再出牌/再激活/让过；② 4 处"只开窗不挂起动作链"（`Discard`/`Seal{手牌}`/`Search`＋其回调内的"选结附宿主"）⇒ 效果后续动作**先于**玩家选择执行，且 `allowCancel:true` 取消时窗口不回调 ⇒ 链永久悬空；③ 联机 5 处等待回包不持锁（领袖/道途/遏制/预测/JC119 免费打出取目标）；④ `PlayCard` 的锁原先在"费用支付"处才置位、`ActivateAbility` 全链路无持锁（而"宣布使用效果"按规范是原子操作）。**修法**：`TurnManager.BeginDecisionHold/EndDecisionHold`（计数式、可嵌套；解锁条件＝"模态计数与持锁深度双 0"，修掉"模态窗关闭掀掉外层持锁"）＋ `HoldAtomicDuringDecisionAsync`；`Arena.LockChoiceWindow/UnlockChoiceWindow`（`ModalWindow.ChoiceBox`，按窗口实例成对；交付选择前先解锁再续算；取消由 `TreeExiting` 兜底）；`EffectEngine.SuspendChainForInput` 挂起即持锁、`ContinuePendingChain` 配对释放，并补齐 4 处挂起-续算；`PlayCard` 原子段上移到"确认忠诚"之前；`ActivateAbility` 全程持锁；恢复步骤弃牌整段持锁。**配套**：`ActivationChoiceWindow.OnCancelled` 保证"每个选择窗恰好一次回调"（持锁不泄漏、挂起链不悬空的前提）。**覆盖范围**：所有"强制玩家决策"（含恢复步骤弃牌；**调度步骤除外**——联机专用且 `PriorityPlayerId=0` 已封死行动）。详见 §7.10 91 与 `制作指南/原子性规则.md`。测试 `P3AtomicDecisionLockTests`(18)。 |
| A58 | **激活类能力"次数上限"的双重计数修复（2026-09，用户实测反馈）** | 用户实测：**"秘社效果第一次发动就被跳过了"**（`[Resolve] ⏹ 每局次数上限：MSJC001_2_SearchUniqueCard 本局已发动 1 次（上限 1）→ 跳过本次结算`）。**根因**：激活类能力的次数上限有**两处登记/检查**——① 发动阶段 `EffectEngine.ActivateAbility`（语义＝"发动被接受即计数，与结算成功与否无关"，`MarkAbilityUsedThisTurn/ThisGame`）；② 结算阶段 `TurnManager.ResolveEntry` 的 `ResolveKind.Trigger` 分支（本意只管**触发类**"同批次多条同源触发只结算到上限"）。而**激活条目也是 `Kind=ResolveKind.Trigger`**（`PushActivationToStack`）⇒ 首次结算就被自己刚登记的 1 次判成"已达上限"而整条跳过。**影响面**：不止秘社——`MaxUsesPerTurn=1` 的 **JC031/JC032/JC069** 与 `=2` 的 **JC093** 第二次，**一直是首发动即被跳过**（既有单测只驱动 `ActivateAbility` 断言入堆栈/计数，从未推进堆栈结算，故长期未暴露）。**修法**：`StackEntry` 新增 **`FromActivation`**（`PushActivationToStack` 置 true），`ResolveEntry` 的 K23/A57 检查+登记**整段对激活条目跳过**（触发条目照旧在结算前检查+登记）。**顺带修**：`P3SacrificeCost2Tests.TestSacrifice2_JC093每回合两次上限` 原先把"宿主"排在牺牲候选首位（牺牲代价确定性兜底取第一个 ⇒ 第 1 次就牺牲掉宿主、咒刃进墓地），旧实现靠"第 2 次被上限误拒"掩盖；现改为"牺牲品先上场、宿主最后"，并补断言"咒刃仍在场"。 |
| A57 | **秘社（MSJC）效果机制：每局游戏一次 ＋ 独有检索 ＋ 先手标志谓词（2026-09，用户裁定）** | 用户三项裁定/口径：**①"每局游戏只能发动一次"做成通用机制**＝新增 `EffectData.MaxUsesPerGame`（用户选定"按卡×能力"粒度，与 K23 `MaxUsesPerTurn` 平行，但**不随回合重置、离场/换区/版本号变动也不清零**，只有重开一局才恢复；客户端随 `ZoneCardRef.UsedAbilityIdsGame/CountsGame` 同解投影）；**②"独有牌"＝`PlayerCardData.UniqueField`（独有卡名）非空**（用户："UniqueField 不为空就为独有卡"）——`DeckSearchFilter` 新增无值标志段 **`独有`**；**③【先手标志】谓词**：新增 `EvaluateCondition("具有先手标志"/"先手标志")`，基准＝来源控制者是否为本回合先手（`PlayerManager.IsFirstPlayer`，由 `TurnManager.FirstPlayerId` 派生）。附：`UniqueField` 历史注释（"唯一键（如UID）"）是**错的**，已勘误为"独有卡名/独有标识"。**⚠ 勘误（后续实测纠正）**：早先记的"该字段全卡库 0 处赋值 / 数据待回填"**是错的**（首查漏检）——实测 **JC 卡包内已填 31 张**（JC010=`费南迪尔`、JC011=`德莫斯提尼`、JC012=`帷幕守望徽记`…；全库 698 张 .tres 中的 31 张），其余卡包/卡待继续回填。**⚠ 实现陷阱（已修）**：`EffectData.Target` 默认是**非 null 的 `TargetSpec`**，秘社"无目标能力"若忘了 `Target = null`，`CanActivateCouncil` 会误判"需要选目标"⇒ 该能力**永远不可激活**；已在门禁补 `Target.Kind != None` 前置（fail-open，不放过真需求）。 |
| A56 | **服务端"异步续算"落地后必须立即下发快照（2026-09，用户实测反馈）** | 用户反馈：**"效果能正常结算，但 UI 刷新有延迟，卡片加入手牌，玩家让过后才能看到手牌"**。服务器日志（18:25 实测）逐行定位：`🌐 联机决策请求已下发，动作链挂起等待回包` → `[MatchHost] 下行快照 手牌数=13` → （客户端回包）`检索：逆转异界之门 加入玩家1 手牌` → **直到 `玩家1 让过` 才又出现 `下行快照 手牌数=14`**。根因＝**快照只在"意图流程结束"时推送**（`MatchHost` 订阅 `TurnManager.OnAsyncSettlementCompleted`），而引擎侧"挂起-续算"（A55 新接的网关路径）与规则类异步流程（领袖/道途/遏制/预测）落地时**没人通知** ⇒ 客户端 UI 滞后到下一次上行。修法：新增公开通知口 `TurnManager.NotifyResolutionSettled()`，在 `OnEngineChainSettled()`（引擎续算收尾）与四个规则类的异步流程末尾调用 ⇒ `MatchHost` 立即 `PushSnapshot()`。单机不受影响（单机 UI 走 `OnCardMoved`/`OnPostMove` 事件总线，见 `事件总线使用说明.md` §三 收口原则）。 |
用户口径（2026-09）：
- **"不能被分配伤害"的卡，一开始就不能被选为伤害对象** → 这是**分配阶段的目标合法性**（不是结算期防止）；
- **"防止伤害"的卡可以被分配伤害，但即使分配了也不会发生任何事** → 防止发生在**伤害落地**时（不写伤害 → 不判死 → 不触发死亡触发）；
- **基本不涉及"重定向"**：不建通用 transfer/redirect 机制。（JC077 庇佑圣灵 / JC121 空鸣御守这类"该伤害改为对 X 造成"属个别卡文，留到 P5 与规则文本一起定。）

### 7.6 效果级推进纪律（用户拍板 2026-09，**已取代早先的"整卡不接入"**）

> **进度表建立后，"一张卡只要有任一效果做不了就整卡不接入"的旧纪律作废。**
> 现在按**效果**推进：**卡可以部分接入**——能做出来的效果立刻做、立刻接入、立刻在进度表里标 ✅；
> 做不出来的效果在该卡对应行标 `⛔ 未实现（缺什么）`，等机制补齐后回填。

配套要求：

1. **每个效果都要在 `JC卡牌效果进度表.md` 里有一行状态**（这就是该表存在的意义）；词条（灵体/领袖/声望/撤回/隐匿/混乱/杀伤）也算独立效果，各占一行。
2. **判定"不能做"的标准**（写进实现列）：缺动词执行器（`Curb`/`PlaceCounter`/`PreventDamage`…）、缺目标种类（资产/墓地/手牌/玩家）、
   缺动态数值 X、缺群体动作、缺条件/模式分支、缺关键词规则、缺时机接线。
3. **验收口径**：每个标 ✅ 的效果都要有行为用例（或明确说明"仅结构用例"）；无单测视为未验收。
4. **不做半条效果**：同一条效果内的多个子动作（如"抓2然后弃1"）要一起做完，不要只做前半截。
5. 早先为遵守旧纪律而撤回的 **JC010** 已恢复接入（其"领袖/灵体"两行在表中仍为 ⛔）。

5. 早先为遵守旧纪律而撤回的 **JC010** 已恢复接入（其"领袖"行已于 P4 落地，"灵体"行为引擎级）。

### 7.7 P4 交付明细（2026-09，按用户指定顺序四步）

**① 领袖（10 张：JC010/011/023/024/037/038/051/052/067/095）** —— `KeywordEffects.Leader()` + `Script/Manager/LeaderRule.cs`
- 规则：**同一控制者在场上至多 1 个拥有【领袖】的角色**；进场超限 → **立刻**由该控制者选一个（含刚进场的那个）**牺牲**，
  该牺牲是**规则性结算（不入堆叠、不可响应）**；**进场触发照常生效**；牺牲走 `CardManager.ResolveDeath(Sacrifice)`（照常广播死亡触发）。
- 触点：`ZoneManager.OnCardEnteredArea`（`MoveToArea` 的"非场地区→争夺区"分支，在 `EnterTrigger` 广播与 `RegisterPersistent` **之后**），
  由 `GameInitializer` 装配。
- 决策：单机复用 `IGameUIHooks.PromptPickCard`（不可取消）；联机 `ChoiceRequest.Kind.SelectTarget`（候选=该控制者场上全部领袖，强制选择，
  服务端对回包做权威复检，越权/取消/超时 → 确定性兜底）；无 UI/网关兜底＝牺牲最新进场者，并循环收敛到 1 个。

**② 遏制X** —— `AreaManager.RemovePowerMark` + `Script/Manager/CurbRule.cs` + `ActionVerb.Curb`
- `RemovePowerMark(areaUid, playerId, amount)`：标记系统的**减法侧**（返回实际移除数、按现有量钳制、**不影响对方**，
  与 `AddPowerMark` 的"净差抵扣"语义明确区分）。
- 语义：**X ≥ 被遏制者标记总数 → 自动全清（不弹窗）**；**X < 总数 → 被遏制者按地区分配**，**每地区上限＝该地区该玩家的标记数**；
  **被遏制者默认＝施术者的对手**（`Args[1]="自己"` 可改为自己）。
- 决策：单机 `IGameUIHooks.PromptCurbMarks` 逐次询问（每次 1 个，Controller 在回调内重算候选）；联机 `ChoiceRequest.Kind.CurbMarks`
  （`OptionUids`=地区、`OptionMaxAmounts`=每地区上限、`Amount`=X；回包 `SelectedUids/SelectedAmounts`）→ **服务端权威复检**（逐地区按上限钳制、
  总量不超过 X、非法/重复条目忽略）→ 不足部分按地区序号兜底补齐；无 UI/网关兜底＝按地区序号贪心。
- UI：单机 `Arena.PromptCurbMarks`（ActivationChoiceWindow，列出"地区N（可移除 k）"）；联机 `ChoicePromptPanel.BuildCurbMarks`（每地区一个 SpinBox，上限＝下发值）。

**③ 预测X** —— `ActionVerb.Predict` + `Script/Manager/PredictionRule.cs`（JC104 占卜师 进场触发：预测3）
- 语义：检视**牌库顶 X 张**，**任意数量**以任意顺序置**牌库顶**，其余以任意顺序置**牌库底**；**不抓牌**。
- 复用：数据层走调查对抗同一条取/放通道（`ZoneManager.TakeTopForInvestigation` / `ReturnAfterInvestigation`）；
  分配窗口复用 `InvestigationWindow`（新增可选参数 `titleOverride`，预测时标题为"预测X"且不抽牌）；
  联机复用调查同款字段 `Kind.Prediction` + `OptionUids` + `OptionCardRefs` + `Amount`（回包 `SelectedUids`＝置顶顺序，未列出的按原序置底）。
- 兜底：无 UI/网关（或联机取消/超时）＝**原序全部留在牌库顶**（牌库不变）；回包/窗口漏卡一律按原序补进牌库底（**不丢牌**）。

**④ JC051 检索『结附』去向修订** —— `EffectEngine.AttachSearchedCardByOwnRules` + `CollectOwnRuleHosts`
> ⚠ **本段口径已被 §7.4b A53（2026-09 用户裁定）撤销**：宿主不再由引擎自动选定，而是**候选 ≥2 时由玩家选**
> （单机 `PromptPickCard` / 联机 `SelectTarget`；候选 = 1 不弹窗；无 UI 确定性兜底取第一个）。下面是当时口径，仅作历史。
- 宿主**按被检索卡自身的 `TargetRules` 选定**（判定与打出阶段**同一实现** `CardManager.MatchesRule`＝粗筛大类 + `TargetValidity.CheckRestrictions`）；
  确定性候选顺序＝秘社(P1→P2)→争夺区卡→附属卡；卡只声明 `Kind=Area` 规则时结附来源所在地区。
- 显式 `Args[2]="地区"` 与"效果已选中的显式 target"仍按原语义（向后兼容 P2 口径）；
  **无合法宿主 → 按"无合法目标"处理**：不结附、牌回拥有者牌库（牌库不因此少牌），随后照常洗牌。

**测试与回归**：新增 `P4LeaderTests`（12 例）/ `P4CurbTests`（14 例）/ `P4PredictionTests`（8 例）/ `JC051Tests`（6 例），
已注册进 `TestsRunner`；`dotnet build` 0 警告 0 错误；headless 全量 `TestsRunner` **52 套件 369 例全绿 0 失败 0 错误**（既有套件零回退）。

**未排期（用户明确）**：额外费用（K10）、构筑唯一性（K25）、临时费用池（K19）。

### 7.8 P4 / P5 批次项现状（**P4 收尾实测**，逐项带代码证据）

> 本节回应"先前已完成的部分 P4/P5 内容"——P1/P2/P3-词条第一批与 P4 的若干项**早已落地**，
> 但 §五 批次表长期未回填。下表按 **P4/P5 原定范围**逐项核对，结论以生产代码路径为准（"仅声明未接线"不算已交付）。

**P4 批次项**

| 项 | 状态 | 证据（生产路径） | 备注 |
|---|---|---|---|
| ④ 声望机制 | ✅ 已交付 | `RenownRule.ApplyIfMajority`（消费点 `CombatManager` 势力对抗结束分支）+ `EffectEngine` 条件谓词 `RenownMajority` + 新时机 `OnPowerCombatEnded`（`TriggerEmitPoints.PowerCombatEnded`）| 卡面写"声望"时用 `KeywordEffects.RenownKeyword()`（纯关键字）——用卡级 `Renown()` 会与引擎级规则**各放一次**势力标志 |
| 遏制（K6，原排在 P5 却随 P2 一起延后） | ✅ 已交付（本轮 P4） | `AreaManager.RemovePowerMark` + `CurbRule` + `ActionVerb.Curb`；单机 `IGameUIHooks.PromptCurbMarks`／联机 `ChoiceRequest.Kind.CurbMarks` | X≥总数自动全清不弹窗；X<总数按地区分配且每地区上限＝该地区标记数；被遏制者默认＝施术者对手 |
| 领袖（词条规则） | ✅ 已交付（本轮 P4） | `KeywordEffects.Leader()` + `LeaderRule` + `ZoneManager.OnCardEnteredArea`（`GameInitializer` 装配） | 同控制者场上至多 1 个；进场超限立刻选一个牺牲、不入堆叠；进场触发照常 |
| 预测X（原 K8/交互延后项） | ✅ 已交付（本轮 P4） | `ActionVerb.Predict` + `PredictionRule`（复用 `TakeTopForInvestigation`/`ReturnAfterInvestigation` + `InvestigationWindow` + `Kind.Prediction`） | 检视牌库顶 X 张、任意数量置顶/底；不抓牌 |
| ① 额外费用（K10） | ✅ **已全行交付（2026-09）** | 卡级 `ExtraCostSpec` ＋ `TurnManager.PlayCard` 付费阶段：JC049①（牺牲一个角色）/ JC135① ②（支付 X 费 + 消灭目标）/ **JC081（展示任意数量手牌减费＝手牌多选交互）** 均已交付（见 §7.10 65）；**联机路径的两条缺口已闭合**（JC049 的牺牲选择＝`Kind.SelectTarget` 回环、JC081 的手牌多选＝新 `Kind.SelectHandCards`，见 §7.10 75 / §7.4b A43） |
| ② 形态/类别转换（K4） | ✅ **已全行交付（2026-09）** | **实例级形态转换已交付**（`EffectData.FormChange` ＋ `ActionVerb.TransformForm{对象,期限}`）：类别覆盖（`CategoryFlagOverride`，附属↔角色可组合）／卡名覆盖（`CardNameOverride`）／**印刷效果失活 + 形态效果表**（`PrintedEffectsSuppressed` ＋ `EffectEngine.EffectsOf` 统一收口）／印刷图标与防御清零（`ResetToBaseValues` 共同尊重）／追加子类别；到期由 `TimedMod{Kind="Form"}` 撤销 → **JC027 / JC028 全行 ✅**（见 §7.10 69）。**`CategoryOverride`（印刷类别改写）经 A32 确认"不做"**（用户口径：没有任何效果改变卡的印刷类别） |
| ③ 阶段钩子与对抗流程改造（K18/K29） | ❌ 未做 | `TurnManager.DoReset/DoCleanup` 存在但**无卡级钩子**；无"略过/追加对抗"入口 | 影响 JC067/098/118（K18）、JC108/122（重置步骤） |
| ⑤ 构筑与唯一性（K25） | ❌ 未做（用户明确不排期） | `GameDeckValidator.Validate` 仅校验"JSON 合法/条目非空/模板存在"（注释亦声明"不做张数上下限、同名上限、禁卡表"） | 影响 JC125（数量无限）、JC034（至多一个道途） |
| ⑥ 临时费用池（K19） | ❌ 未做（用户明确不排期） | 费用模型＝"未横置资产数"（`PlayerManager.GetAvailableMana`），无独立 mana pool | 影响 JC115；其 FAQ"能力不可被响应/仅保留到步骤结束"另需响应窗口口径 |

**P5 批次项**

| 项 | 状态 | 证据 / 缺口 |
|---|---|---|
| 灵体 | ✅ 已交付 | `DamageRules.IsSpiritPrevention` → `CardManager.DamagePreventer`（`GameInitializer` 注入）；语义＝对手在本地区魔法领域图标更少则**防止该次伤害**（不写伤害→不判死→无死亡触发） |
| 声望 / 杀伤 / 撤回 / 隐匿 / 屏障 / 公开 / 非资产 / XX标志X | ✅ 已交付 | `RenownRule` / `CombatManager.KillBonus` / `AreaManager.HasWithdraw` / `TurnManager.PlayCard` 门禁 / `TargetValidity.IsBarrierBlocking` / `PlayerCardData.HasPublicKeyword` / `TurnManager.EstablishAsset` 门禁 / `ActionVerb.PlaceCounter` |
| 领袖 | ✅ 已交付（本轮） | `LeaderRule` |
| 遏制（K6）+ 其分配 UI | ✅ 已交付（本轮） | `RemovePowerMark`/`CurbRule`；单机 `Arena.PromptCurbMarks`、联机 `ChoicePromptPanel.BuildCurbMarks`（`OptionMaxAmounts` 下发每地区上限） |
| 计数器 K5：**放置** | ✅ 已交付 | `ActionVerb.PlaceCounter` → `CardManager.AddCounter`（广播 `OnMarkerPlaced`） |
| 计数器 K5：**移除作代价 / 按计数器缩放** | ❌ 未做 | 无 `ActionVerb.RemoveCounter`；`PrereqSpec` 不支持"按 `Counters[type]` 缩放"（仅 `DomainLoyalty`/`SelfUntapped`）→ JC044/045/048/055/069/122/123/124/127、JC109 等 |
| 伤害防止 K3：**灵体式防止** | ✅ 已交付 | 见上（`DamagePreventer` 钩子 + `DamageRules`） |
| 伤害防止 K3：`PreventDamage` 动词 / 伤害替代 / 死亡替代 / 去处替代 | ❌ 未做 | `ActionVerb.PreventDamage` 仍打印 TODO；`CardManager.DeathPreventer` 钩子已就位但**无卡注册**；无 `ReplacementRegistry` → JC078、JC077/121（伤害改由它承担）、JC105（死亡改封印）、JC079（赢取改计入分区）、JC120/126（改形态）等 |
| 检视私密信息（K8） | ✅ **已交付（2026-09）** | `Reveal`＝**公开展示**（`UiNotificationBus.CardRevealed`）；**私密检视**＝`ActionVerb.Inspect`（单次：对手手牌/牌库顶/暗藏者，`P3InspectTests`）；**"仅自己可见"的授权**＝背面朝上封印（`SnapshotFilter.IsHiddenFor`）/ 预测X（`PromptPrediction`/`OptionCardRefs`）/ **持续随时检视（JC057：`CovertInspectRule` ＋ 鼠标悬浮预览 UI）**。**仍缺**：联机客户端按需拉取"隐藏卡的卡面"（对手暗藏者在授权后仍只有占位、无卡图） |
| 混乱 | ❌ 未做（**全项目无定义**） | 代码中无 混乱/Chaos 任何实现；缺规则文本（随机进入地区） → JC064/JC133 FAQ |

### 7.9 K 缺口现状（K1–K29 实测状态，供定位"缺什么"）

> 状态口径：**✅ 已交付**（生产路径可跑且有/可有行为用例）｜**🟡 部分**（判定层或钩子已有、枚举/消费点缺）｜**⛔ 未做**。
> "影响面"引用进度表 §"未实现原因分类汇总"的聚类（该表为交付前快照）。

| # | 缺口 | 状态 | 现状（证据）与**还缺什么** |
|---|---|---|---|
| K1 | 动态数值 X | ✅ 已交付（本轮补齐"玩家选择"） | `EffectData.ValueExpr`（表达式字符串，空＝不适用）＋ 新增 `ValueMin`/`ValueMax`（"玩家选择"型的数字范围）＋ `Script/Manager/EffectValueResolver.cs`（**唯一求值实现**）＋ `EffectEngine.EvaluateDynamicValue`/`SubstituteValueArgs`（占位符 `$X`）。口径（用户 2026-09）：**X=0 ⇒ 效果不发动**（适用于"数量"型 X）；已登记：`事件数量`（本次事件数量，JC040）、`计数:区域:…`（JC097）、`计数:卡上标志:…`、`计数:地区标志:…`、`计数:地区内卡上标志:…`（JC048）；**`玩家选择`（本轮交付）**＝玩家在数值输入窗给出数字（JC083；**声明 `ValueMin=0` 时 X=0 合法且不套用"不发动"**——该卡的 X 是"猜测值"而非"数量"），未取值/无 UI → fail-closed；**未登记写法一律 fail-closed（不发动 + 告警）**。测试 `P3DynamicValueTests`(7) / `P4ChooseNumberTests`(7) |
| K2 | 群体/批量动作 | ✅ 已交付（本轮） | 口径（用户 2026-09 二次确认）：**不做"批量期间挂起触发"**——群体效果包在一条效果/一段原子锁内（堆叠结算期间 `TurnManager` 原子段生效，`Pass`/`PlayCard` 被拒），触发按既有管线在效果结算后处理（多条由玩家排序）；实现只需"**完整执行**"。已交付：`TargetSpec.MassMode=AllInArea` + `MassTargetPool`（地区＝目标地区/来源所在地区；集合＝`Kind`+`IncludeCovert`+`Filter`+`Restrictions`，排除 IsDying 与来源自身）+ `EffectEngine` 同步/异步两条 mass 执行体 + `ResolveSorcery` 透传"打出时选定的目标地区"。**已交付（本轮）**：**"目标卡上"的整组消灭** `ActionVerb.DestroyOnTarget`（Args：`集合(附属/封印牌)` + `归属(任意/本方/对手)`，对象＝宿主卡）——"某卡上的附属（非地区）"的批量由此解决 → JC013（宿主上封印的所有牌）/ JC017（宿主上的所有敌方附属）；**仍缺**：与 K5/K1 组合的群体（JC048/127，如"移除某地区所有标志""按标志数缩放"） |
| K3 | 替代效果 | 🟡 部分（**伤害"防止" + 持续型"伤害重定向" + "赢取地区去处替代"已交付**） | 钩子已就位：`CardManager.DamagePreventer`（灵体 + **伤害护盾**）、`CardManager.DeathPreventer`（**无人注册**）、`ResolveDeath(destination)` 已支持改去向但**无卡驱动**。**已交付（2026-09）**：① `ActionVerb.PreventDamage` = **伤害护盾**（`RegisterDamageShield`/`IsDamagePrevented`，期限＝本回合，接在伤害唯一收口上与灵体并联）→ **JC078**；② **地区赢取时的去处替代**（`EffectData.WinAreaDestination` + `AreaManager.ResolveAreaWon` 归位扫描就地改判，条件走 `EvaluateCondition`；去处值已支持 `计分区` 与 **`资产区`**，消费点收口为 `TryApplyWinAreaReplacement`）→ **JC079**（见 §7.10 ㊲）/ **JC120**（见 §7.10 ㊿）；③ **持续型"伤害改由本卡承受"**（`EffectData.DamageRedirect`；**用户口径：JC077/JC121 是持续效果**，不是触发、也不是替代式框架；消费点＝`DealDamageToInstance`；一次伤害只重定向一次，承受方『灵体』防止照常）→ **JC077 / JC121**（见 §7.10 ㊸）；④ **死亡类第一批（走触发路线，用户 A10）**：`OnCardDied` + 新谓词 `DiedAllySpiritCharacter` + 动作主体 `"触发卡"`/`"死亡角色"`（`EffectEngine.TriggerContextCard` ← `TriggerContext.ContextCard`）→ **JC105**（支付2 → 把死亡角色封印到目标角色上，见 §7.10 52）。**仍缺**：**死亡类第二批**（JC124）——用户口径"其他几个走触发、并非替代式效应；卡会先进入墓地后通过触发重新结附"，**并已确认（§7.4b A10）：这两张走触发路线、JC124 落点＝手牌**（实现时按"先进墓地 → 死亡触发 → 重新结附/回手"的墓地来源口径处理）；其它去向/离场替代消费点 |
| K4 | 形态/类别转换 | 🟡 部分（**区域转换 + 控制权转移 + 子类别改写已交付**） | **已交付**：`ActionVerb.MoveToZone`（P3·G：对象 自身/目标/牌库顶 → 目的区 资产区/争夺区/手牌/牌库底，含朝向）；"成为一个资产"由**落区**满足（`MapInstanceToTargetKind` 按区域映射 + `GetAvailableMana` 按资产区未横置卡数）→ JC120 / JC126。**已交付**：**控制权转移 + 到期归还**（P3：`ActionVerb.GainControl` / `ZoneManager.ChangeController`（只改 `ControllerId`、走夺控统一出口）+ `TimedMod{Kind="Control"}` 回合结束交还原控制者）→ JC129 操控心智；`Grant("SubCategory")` 解决"额外具有不死生物"（JC133）。**已交付（2026-09）**：① **持续夺控 + 归还链**——`EffectData.ControlGain = AttachedHost`（`SetControlGain`/`ReleaseControlGainsFor`）：登记时把宿主交给本卡控制者（物主不变、**卡不移动地区**、**不刷版本号**），注销（来源离场/解附/卡失效）时**归还拥有者**（JC036 初拥；见 §7.10 64）；② **子类别"改写"＝移除侧**——`CardInstance.Grant/Revoke("SubCategoryRemove", …)` 可把**模板固有**子类别从**实例当前类别**抹掉（计数式、撤销归零还原；`ResetToBaseState` 清表、`ReapplyMissingContinuousGrants` 自愈、快照 `RemovedSubCategories` 投影保证双端同解）；③ 配套口径分工：`StaticSubCategory`＝**印刷类别（模型数据）**、`CurrentSubCategory`/新增 `NotCurrentSubCategory`＝**实例当前类别**。**仍缺**：**实例级形态转换**（`CategoryFlag` 实例覆盖＝附属↔角色、实例改名、清空实例图标/防御、"自我重进场并结附"）→ JC027/028；**`CategoryOverride`（印刷类别改写）按用户 2026-09 补充口径（A32）「不做」**——"JC027/JC028 也是只修改实例，不会有任何效果改变卡的印刷类别"，故 `Grant("SubCategory")` 的"追加"语义与 `SubCategoryRemove` 的"抹除"语义已经够用 |
| K5 | 计数器消费 | ✅ 已交付 | `PlaceCounter` ✅、`OnMarkerPlaced` ✅、**按 `Counters[type]` 缩放 ✅**（P3·F：`ContinuousPrereqSpec.ScaleExpr="计数:卡上标志:…:自身"`，且 `AddCounter/RemoveCounter` 落地后即时重判）；**"移除 N 个标志作代价" ✅**：`CounterCostSpec`（固定数量，JC044②）＋ **`AmountByPlayerChoice`＝"移除 X 个"由玩家逐卡分配**（JC055②，**X≥1**、上限＝本方总数、入堆叠前支付、fail-closed；支付数量经 `StackEntry.EventAmount` 下发，`ValueExpr="代价数量"` 读取；见 §7.10 67）；`AndCounter`/置顶·锁定标志的静态限制消费点按需扩展 |
| K6 | 势力标志移除 | ✅ 已交付 | `AreaManager.RemovePowerMark`（纯减法）+ `CurbRule`/`ActionVerb.Curb`；**仍缺**：只在"遏制"语义下可用——"移除某地区所有标志"（JC095/127）还需 K2 群体。**另**：每次真实移除会置位**回合内记忆位**（`MarkPowerMarkRemovedThisTurn`，回合开始清空）→ 支撑 JC053「本回合有敌方势力标志曾被移除」 |
| K7 | 检索/洗牌 | ✅ 已交付 | `Search`（`DeckSearchFilter` + 5 去向）、`Shuffle`（可复现随机源）、联机 `SelectDeckCard` |
| K8 | 检视/展示私密信息 | ✅ 已交付（**单次检视 + 持续随时检视**） | 「公开展示」✅（`Reveal`，双方可见）；**「检视」✅（P3·H）**：`ActionVerb.Inspect`（Args：`对象`）＝**私密查看**——不翻面、不移动、不改任何状态，只把该卡的面给**效果来源控制者**本人；对象写法：`自身` / `目标`（缺省；**无目标则跳过，不回退成来源卡**）/【K24】`目标玩家牌库顶`（只读不取出）/【K24】`目标玩家手牌`。单机 `UiNotificationBus.NotifyCardInspected(card, inspector)`（View 私下呈现），联机 `ChoiceRequest.Kind.InspectCards`（请求带 `OptionUids` + `OptionCardRefs`，**只下发给检视者**，超时不影响后续结算）。覆盖 JC062（检视至多两个暗藏者 + 抓一张牌）、JC111（检视目标玩家牌库顶）；**仍缺**：**持续可见授权**（"只要监控员未横置，你可以随时检视本地区的任一暗藏者" = 不经过效果结算的查看通道）→ JC057 第 2 行 / JC114 的"展示对手手牌后再指定一张" |
| K9 | 弃牌 | ✅ 已交付 | `ActionVerb.Discard`（自选/随机/被指定者自选/施术者指定）+ `TurnManager.DiscardCards` + `OnDiscard` |
| K10 | 额外费用 | ✅ 已交付（2026-09） | **已交付**：卡级 `PlayerCardData.ExtraCost`（`ExtraCostSpec`：`SacrificeCharacter` / `PayTargetCost` / `RevealHandCards` ＋ `Mandatory`/`RevealFilter`/`Sacrifice`）＋ 消费点 `TurnManager.PlayCard` 的**付费阶段**（卡入堆叠之前、不在堆叠结算、不可被响应）→ **JC049①（牺牲一个角色，必须支付、无对象则不能打出）** / **JC135① ②（先选目标、X＝目标印刷费用、0 费不支付、不足则不能打出）** / **JC081（展示任意数量手牌减费，可选、一次性；【圣贤】＝组织:圣贤、【圣】图标＝神圣领域）**（见 §7.10 65）。单机 View＝`Arena.BeginPlayWithExtraCostChoice`（牺牲选择窗）/ `BeginPlayWithRevealExtraCostChoice`（手牌多选 + 确认/取消）；**联机侧已补齐**（`TryBeginExtraCostPlayOnline` → `PlayWithExtraCostAsync`：`Kind.SelectTarget` 牺牲回环 ＋ 新 `Kind.SelectHandCards` 手牌多选，见 §7.10 75 / §7.4b A43）；**暗藏者现身同样校验额外费用**（`TryResolveExtraCost` 打出/现身共用；见 §7.10 80 / §7.4b A51） |
| K11 | 可选支付"若你如此做…" | ✅ 已交付（2026-09） | 新动词 **`PayOptional`**（Args：`[默认金额, 最小, 最大]`）：弹**数值输入框**（用户 2026-09 口径：**输入的数字即为 N**）+ "支付"/"不支付" → 支付则扣 N 点无色费用并**继续**后续动作；**不支付 / N=0 / 费用不足 → 不扣费且后续动作整段跳过**（复用既有 `_actionChainAborted`＝"若你如此做…"语义）。单机 `IGameUIHooks.PromptNumberInput`（回调式；`EffectEngine` 支持**动作链挂起续算** `ContinuePendingChain`）+ `Arena.PromptNumberInput` / `Script/UI/NumberInputWindow.cs`（程序化窗口）+ 联机 `ChoiceRequest.Kind.NumberInput`（`NumberMin/NumberMax/NumberDefault`，回包 `SelectedAmounts[0]`）+ `ChoicePromptPanel.BuildNumberInput`；无 UI → 确定性兜底＝**不支付**（绝不替玩家花钱）。覆盖 JC061/JC100；该窗口同时是通用"数值输入"原语（JC083"选择一个数字"、JC081"展示任意数量"可复用）。`Pay`（能付则付）旧行为保留 |
| K12 | 触发时机接线 | 🟡 部分（**已接 18 个**） | 已 Emit：`EnterTrigger`/`UncoverTrigger`/**`OnLatent`（2026-09 新接：本卡被潜伏/翻面为暗藏者，触点＝装配层 `CovertChanged` 且 `IsCovert==true`；反方向＝现身走 `UncoverTrigger`）**/`ManeuverTrigger`/**`OnMove`（2026-09 起被 JC061/JC087 用作「进入一个地区」；已加入"只对事件主体卡成立"白名单，见 §7.10 78 / §7.4b A48）**/`OnCardPlayed`/`OnTurnStarted`/`OnTurnEnded`/`OnCardDied`/`OnAreaWon`/`OnDraw`/`OnDiscard`/`OnMarkerPlaced`/`OnSealed`/`OnMove`/`OnCardLeft`/`OnCombatWon`/`OnInvestigationWon`/`OnPowerCombatEnded`（三个对抗时机均见 `CombatManager`）；**取证脚本**：`grep "_triggerEmitPoints?\\."`＋`TurnManager` 的 `_gameEventBus?.Emit`。**仍缺**：`OnCounter`（无反制/终止原语）、`OnPowerWon`（势力对抗**获胜**，与"对抗结束"不同） |
| K13 | 运行期授予触发类关键词/激活能力 | ✅ 已交付（袭击 / 机动·威名 / **运行期授予激活能力**） | **已交付**：`KeywordEffects.Strike(n)` 标记 `EffectData.IsStrike/StrikeAmount`；`TriggerRegistry` 在**现身触发**收集时把"卡面自带袭击 + 运行期授予的袭击（`Attribute:"袭击"`）"**合并为一个触发**，伤害＝总和（用户 2026-09 口径："已有袭击1 被授予袭击1 → 袭击2"），合成经 `KeywordEffects.StrikeWithAmount`。覆盖 **JC038**（光环按组织授予袭击1）。**已交付（本轮）**：**运行期授予的触发类关键词参与触发收集**——`KeywordEffects.RuntimeTriggerTemplates`（`机动`→`ManeuverTrigger`、`威名`→`OnCombatWon`）+ `TriggerRegistry` 收集阶段对"本卡运行期授予（`HasKeyword` 且非卡面固有 `HasInherentKeyword`）"者**合成**该关键词的触发形态（生效区/暗藏模式/条件判定同一条路径；卡面固有者跳过以免重复）→ JC024①（授予的【机动】真的能触发）/ JC089（授予的【威名】真的能触发）；`袭击` 走数值合并、`声望` 走引擎级 `RenownRule`，均不经此通道。**仍缺**：`GrantAbility(EffectData)`（把整条**激活能力**挂到宿主 → JC019/105）、文本/领域屏蔽（JC101）| 
| K14 | 目标种类扩展 | ✅ 已交付（**墓地 / 手牌 / 资产 / 附属区 / 秘社 / 封印区 + 或种类 `KindAlt`**） | 用户 2026-09 口径：**允许指定对手的手牌/墓地/资产**；**目标位置版本号变动即丢失**（复用 D16；已核对：**只有"争夺区→争夺区"不刷新版本号**，其余区域切换与翻面都刷新——与用户口径一致）；候选池**可以扩大**。已交付：`CardManager.MapInstanceToTargetKind` 按区域优先映射；候选与合法性 **单一实现 `TargetZonePool`**（`IsZonePileKind`＝墓地/手牌/资产/附属区/秘社/封印区；大类＋`KindAlt` 的 **OR** ＋文本筛选（`DeckSearchFilter` 语法，未知字段 fail-closed）＋`Restrictions`）；`KindAlt` 在**四处**一致生效（`TargetZonePool`／`TargetValidity.CheckZoneAndFace` 复检／`CardManager.MatchesRule` 打出粗筛／`EffectEngine` 激活与触发复检）；选择窗口＝单机 `PromptPickCard`／联机 `Kind.SelectZoneCard` + `OptionCardRefs`；挂载点＝`TurnManager.GetLocalZoneTargets`、`ActivateEligibility.HasLegalTarget`。**【P3·复合目标限制】**：`TargetSpec.SpecAlt`＝第二条**完整**目标声明（OR）——候选池＝两条并集（去重，本体优先），合法性/结算复检＝任一条**整条**通过即通过（`TargetZonePool` / `MassTargetPool` / `TargetValidity.Check` 三处同源）→ JC043。配套两个新限制：`HostIsCouncil`（目标＝结附于秘社的附属）与 `AttachedToArea`（目标＝**直接**结附于地区的附属；注：结附于**角色**的附属其 `ZoneDetailId` 也是宿主所在地区，单靠 `LocalZone` 无法区分）。**仍缺**：`Arena` 的**场上点选高亮**仍只扫争夺区/附属区（非争夺区目标走选卡窗，不受影响）。测试 `P3TargetZoneTests`(13) / `P3CompositeTargetTests`(4) |
| K15 | 多目标选择 | ✅ 已交付（单机 + 联机） | **已交付**：`EffectData.TargetSlots`（多槽位声明：每个槽＝`TargetSlot{Spec, Optional, Label, ExcludeInChosenArea}`）+ `MinChosenSlots`（至少选 N 个**目标**，按卡目标槽计数）+ 单一实现 `Script/Manager/MultiTargetSlots.cs`（逐槽候选 / 合法性 / 组合校验 / 入栈负载对齐 / 反推）+ `StackEntry.TargetCardUids`+`TargetVersionIds`（**按卡槽对齐**，留空槽保空串）+ D16 逐目标丢失（**只跳过丢失的那个**，其余照常结算）；打出侧 `TurnManager.PlayCard(..., multiChoices)`（**付费前**校验，非法整次拒绝）；**单机**：`BeginMultiTargetPlay`（逐槽取目标 → 确认面板 → 确认后才入堆叠）+ `IGameUIHooks.PromptMultiTargets` + `Arena.PromptMultiTargets`；**联机**：客户端发裸打出意图 → 服务端 `PlayMultiTargetCardAsync` 逐槽下发 `Kind.MultiTargetSlot` → `Kind.ConfirmTargets` 确认（`ChoiceResult.Confirmed`）→ 确认后 `PlayCard`（取消/超时→不扣费、卡留手牌；完成后经 `OnAsyncSettlementCompleted` 下发快照）+ `ChoicePromptPanel.BuildMultiTargetSlot/BuildConfirmTargets`。覆盖 JC021；JC117 类"双目标"同机制可复用 |
| K16 | 模式分支（选择一项） | ✅ 已交付（2026-09） | `EffectData.Modes`（每个模式＝`EffectMode{Label, Target, Actions}`）+ `StackEntry.ModeIndex`；口径（用户 2026-09，与 JC063 FAQ 一致）：**模式在打出时选定、付费之前**，入堆叠后**不可更改**；结算**只执行所选模式**（D16 目标复检也按该模式，见 `ValidateTargetAtResolve(..., modeIndex)`）。入口：单机 `TurnManager.BeginModeCardPlay` + `IGameUIHooks.PromptEffectMode`（选完再取该模式目标，复用 `GetTargetUidsForSpec(..., allAreasIfNotLocal:true)`）；联机 `TurnManager.PlayModeCardAsync` + `ChoiceRequest.Kind.SelectEffectMode`（选模式 → 选目标 → 入堆叠；取消/超时→不扣费）。覆盖 JC063；**触发类二选一也已交付（2026-09，K16 触发版）**：`StackEffectInstance.ModeIndex` + 触发入堆叠前的模式选择（单机 `TurnManager.EnsureTriggerModeThenPush`、联机 `EnsureTriggerModeAsync`＝复用 `Kind.SelectEffectMode`；无 UI 兜底第一个模式；取消→不入堆叠）→ **JC110 闲人的绿地②**（见 §7.10 51）；JC082/JC128 待接线 |
| K17 | 条件式动作分支 | ✅ 已交付（含**门禁 + 分支双条件**） | `EffectCondition.Predicate` + **`ElseBranch`**：谓词不满足 → 只跑 `ElseBranch`（`EffectEngine.ResolveEffect/Async`）；**【本轮新增】`EffectCondition.BranchPredicate`**：**两条独立条件**——`Predicate`＝触发/结算**门禁**（收集阶段过滤 + 结算复检），`BranchPredicate`（可空）＝门禁满足后的**分支选择**（不满足同样走 `ElseBranch`），用于卡文"每当 X 时…；**若 X 是 Y**，则**改为**…"（JC031 唤灵师）；缺省为空时行为与旧实现完全一致。已有谓词：`已横置/PlayedSorcery/OpponentHandLe3/WinnerIsSelf(WinnerInSameArea/WinnerIsSelfInSameArea)/DiedAnyCharacter/DiedAllyCharacter/**DiedVampireCharacter（死亡卡当前实际子类别含「吸血鬼」，含运行期授予）**/**HostIsCouncil·HostIsCharacter（本卡结附宿主的大类：秘社/角色）**/DiscardByEnemy/RenownMajority`。**注意**：未知谓词 **fail-open（恒真）**，写卡前必须确认谓词已登记 → 可立刻做 **JC004**（`已横置` + 两分支）。**【本轮新增】第三种"分支"＝动作成果**：动作序列中某动作**未真正生效**时置 `_actionChainAborted` → **后续动作整段跳过**（与既有"支付失败即中止"同一标志）＝卡文"…，**若你如此做**，…"；生效判据现覆盖 `Seal`（未落地）/`Latent`（无目标·不在场·已是暗藏）/`Uncover`（无目标·不在场·非暗藏）→ JC009/JC087 |
| K18 | 对抗流程改造 | 🟡 部分（**"略过对抗"（常驻+本回合）+ "追加对抗"已交付**） | **已交付（2026-09）**：① 地区级**常驻**"略过对抗"——`StaticKind.SkipAreaCombat`（`StaticEffect.Param` = `调查`/`战斗`/`势力`，可 `,` 组合）+ `EffectEngine.AreaSkipsCombat`（按卡**当前**所在地实时匹配地区）+ 消费点 `TurnManager.ResolveCurrentAreaCombat`（不结算对抗、**仍给让过窗口**）→ **JC098**（见 §7.10 ㉞）；② 地区级"**额外进行一次**势力对抗"——`StaticKind.ExtraAreaPowerContest` + 消费点 `TurnManager.AdvancePhase` 的 `CombatPower` 分支（原地重入、每地区每回合至多一次）→ **JC067**（见 §7.10 ㊴）；③ 地区级**本回合**"略过对抗"——新动词 `ActionVerb.SkipAreaCombat` + `RegisterAreaCombatSkipThisTurn`（`TimedMod{Kind="AreaCombatSkip", TargetUid=地区UID}`，回合结束出栈），查询与①**并联** → **JC118**（三步骤全略过，见 §7.10 ㊷）。**仍缺**："额外一次**调查/战斗**对抗"的写法（现仅登记势力对抗） |
| K19 | 临时费用池 | ⛔ 未做（**用户不排期**） | 费用＝未横置资产数，无独立池与"步骤结束清空" | **已交付（2026-09，用户口径 A26）**：**临时费用池**——`PlayerManager` 临时费用（`GrantTemporaryMana`/`GetTemporaryMana`/`ClearTemporaryMana`/`ClearAllTemporaryMana`）；`GetAvailableMana` ＝未横置资产 ＋ 临时费用；`SpendMana` **优先花临时费用**；**任意步骤结束时清除**（清空点 `TurnManager.EnterPhase` 每次阶段流转）；新动词 `GrantTemporaryMana{数量}` → **JC115**（见 §7.10 56）。**仍缺**：联机快照未含临时费用字段（待接）
| K20 | 图标系统不完整 | ✅ 已交付（镜像 / 替换 / 持续"失去图标" / 持续"失去领域图标" / "文本视作空白＝失去效果"） | 图标→属性映射 ✅（黑刀/白眼/黑眼/白刀/黑星/白星，`EffectEngine.IconToAttribute` + `AreaStatsCalculator` 消费）。**已交付（2026-09）**：① **图标镜像**（`ContinuousPrereqSpec.MirrorSealedIcons` → 把"被封印在本卡上的角色牌"的印刷能力图标汇总授予本卡，封印牌集合变化即撤销重算，见 §7.10 ㉝）→ **JC066**；② **图标替换＝属性替换**（新动词 `ActionVerb.ReplaceIcon`：按"当前有效贡献"整块换算 + `TimedMod{Kind="IconReplace"}` 到期精确还原，用户口径：**都影响全部同类图标、任意顺序、整包在效果内部完成**，见 §7.10 ㊹）→ **JC106**。**已交付（本轮）**：③ **持续型"失去图标"**（`EffectData.IconSuppression` + `ReapplyIconSuppressions` 统一重算 → **JC123**，见 §7.10 ㊺）；④ **持续型"失去魔法领域图标"**（`EffectData.DomainSuppression` + `CardInstance.SuppressAllDomains` + `ReapplySuppressions` → **JC099**，见 §7.10 ㊻）。**已交付（本轮）**：⑤ **"印刷文本框视作空白"＝失去效果**（`EffectData.TextBlank` + 三处门禁 + 注销已注册持续效果；**已经发动的效果不受影响**）→ **JC101**（见 §7.10 ㊼）。**K20 四张卡（JC066/JC099/JC101/JC106/JC123）全部交付**；`Lose` 动词仍是 TODO 占位（现由 `ReplaceIcon` / `IconSuppression` / `DomainSuppression` 通道取代，不再需要单独实现） |
| K21 | 位置/区域条件 | 🟡 部分（**地区级静止规则已交付，含按组织的进入限制**） | `PrereqKind` 现有 `None/DomainLoyalty/SelfUntapped/NoEnemyCharacterInArea/AreaMarkOwner/**HostKind（结附宿主大类）**`；`StaticKind` 现有 `CannotBeAttached/BlockDeployCovert/**SkipAreaCombat**/**ExcludeCovertPower**/**NoUntapWithSealedCards**/**UniquePathAtMostOne**/**CannotMoveWhileUnattached**/`BlockNonOrgEntryInArea``。**已交付（2026-09）**：① 地区级"略过对抗"（JC098，见 §7.10 ㉞）；② 地区级"敌方暗藏者不参与势力对抗"（JC023，见 §7.10 ㉟，消费点＝`AreaStatsCalculator` 单一真相源）；③ **目标须带卡上标志** `TargetRestriction.HasCounter`（JC069，见 §7.10 ㊱，判定挂 `TargetValidity.CheckRestrictions`;顺带修掉激活目标校验忽略 Restrictions 的缺陷）。**追加（2026-09，用户口径 A18）**：④ 新静止种类 **`StaticKind.NoRevealWithLockCounter`** —— 来源在场期间"**带锁定标记/锁定标志的暗藏者不能现身**"（JC122 哈克·亚当斯②；**只封现身方向**）→ 消费点 `TurnManager.RevealCovert` 经 `EffectEngine.IsRevealBlocked`（见 §7.10 54）。**追加（2026-09，用户口径 A19）**：⑤ 新静止种类 **`StaticKind.BlockNonOrgEntryInArea`** —— 「所有非【国家机构】的角色牌不可正面打出在本地区，也不能**移动进入**本地区」（JC064 戒严；两个消费点＝`TurnManager.PlayCard` 与 `CardManager.MoveCardToArea`；判定收口 `EffectEngine.IsEntryBlockedByOrgRule`；见 §7.10 70）。**追加（2026-09，用户口径 A29）**：⑥ **发动来源前提**——`EffectData.RequireSourceCovert`（**只有作为暗藏者时可以激活**，JC041）＋ `RequireSourceAreaSubCategory`（**地区子类别前提**，如"水域"，经 `AreaManager.AreaHasSubCategory` 读地区卡模板）；前提判定单一实现 `ActivateEligibility.MeetsSourcePrereqs`（候选侧＝权威侧，fail-closed）；见 §7.10 71。**仍缺**：JC069/JC122/JC123 的"标志数量门槛"写法（现只支持"至少 1 个"） |
| K22 | "牺牲一个角色"类代价 | ✅ 已交付（**作代价的部分**） | 激活代价 `牺牲本角色` ✅（`ActivateAbility` 第 3.5 步）；**已交付（2026-09，用户口径）**：`SacrificeCostSpec` + `PaySacrificeCost`（见 §7.10 ㊵/㊶）——由**发动者自选**被牺牲者、**不可暗藏者**（除非卡文写明）、**牺牲＝死亡并触发死亡时机**、代价在**入堆叠前**支付、无候选→拒绝且无副作用；子类别白名单（JC038「吸血鬼或奴仆」）；**卡文未写"另一个"→ 可牺牲来源自身** → **JC094 / JC093 / JC096 / JC038** 四行已交付。**口径已定（2026-09，A11/A12/A13）**：**仍缺实现**——多玩家牺牲编排＝**结算时阻塞**（先手先选 → 后手看到先手选择（UI 高亮）→ 后手再选 → **两张卡一起牺牲**，原子锁期间不分死亡先后；可触发死亡触发）→ JC046/JC113/JC134；JC128＝标志不足必须牺牲、**没有角色时选牺牲路线则什么都不付出**；JC114 的"牺牲自身"作代价＝**入堆叠前支付**（参考 JC015）、JC049 的"额外费用：牺牲一个角色"（受 K10 未排期阻塞；其**快速行动**条目已于 2026-09 交付，见 §7.10 53） |
| K23 | 次数限制 | ✅ 已交付（本轮） | 口径（用户 2026-09）：**每张卡 · 每回合 · 每能力**计数；**卡版本号变动 ⇒ 限制重新可用**（`CardInstance.BumpVersion` 清空计数）；回合重置在**重置步骤**（`TurnManager.DoReset`）。实现：`EffectData.MaxUsesPerTurn` + `CardInstance.GetAbilityUsesThisTurn/MarkAbilityUsedThisTurn/IsAbilityLimitReached`；消费点＝`ActivateEligibility`（高亮/候选）+ `EffectEngine.ActivateAbility`（权威门禁+登记）+ `TurnManager.ResolveEntry` 的 Trigger 分支（结算前检查+登记，含同批次多条触发）；快照随 `ZoneCardRef.UsedAbilityIds/UsedAbilityCounts` 下行保证双端同解。**已核对（用户 #4 口径）**：JC069 的"移动自身"属**争夺区→争夺区**移动 → **不刷新版本号** → 其"每回合一次"限额不会被自身移动重置 |
| K24 | 玩家目标 | ✅ 已交付（**激活路径 + 触发路径 + 槽位组合**，单机＋联机） | **已交付**（用户 2026-09 口径：允许指定任意对手、甚至自己；需要**独立选择窗口**）：候选与判定单一实现 `Script/Manager/PlayerTargetPool.cs`（`Kind=Player` + `Restrictions`：`Enemy`＝对手 / `Friendly`＝本方 / 空＝任意；`Filter` 文本写法亦支持；**未登记写法 → 空候选 + 告警（fail-closed）**）；`TargetChoice` 新增**玩家变体**（`IsPlayer`/`PlayerId`/`PlayerUid`）→ 与 K15 槽位**可组合**（`TargetSlot{Spec.Kind=Player}`：候选 / 合法性 / `SlotAlignedPlayerIds` 按玩家槽对齐 / `Reconstruct` 反推）；目标**入堆叠时锁定**（`StackEntry.TargetPlayerId`，多槽用 `TargetPlayerIds`）+ 箭头引用 `StackTargetRef.RefKind.Player`；结算侧动作参数写 `"目标玩家"`（`ResolvePlayerSide` 解析）→ 覆盖 `Shuffle`/`Discard` 等"对某玩家"的动作；`Inspect` 新增 `目标玩家牌库顶` / `目标玩家手牌`（只读不取出）；**三条取目标通路**：① 激活 `ActivateAbility(..., targetPlayerId)`；② 触发 `TurnManager.EnsurePlayerTargetThenPush`（单机经 `IGameUIHooks.PromptPlayerTarget`／联机经 `Kind.SelectPlayer`，**入堆叠前**就地选定）；③ 槽位（见上）；**目标校验收紧到"付费/横置/牺牲代价之前"**（原先目标不合法也会先扣费、先横置 — 顺带修掉的缺陷）；`ActivateEligibility.HasLegalTarget` 支持玩家目标。**View/协议**：`Arena.PromptPlayerTarget`（独立窗口）+ 场上**正面角色双击**即请求激活；联机 `ChoiceRequest.OptionPlayerIds` / `ChoiceResult.SelectedPlayerId` / `ChoicePromptPanel.BuildPlayerTarget` / 超时 45s。覆盖 **JC111 混沌蝴蝶**（两条快速行动）、**JC057 监控员**（现身触发检视目标玩家手牌）。测试 `P3PlayerTargetTests`(16)。**仍缺**：**打出路径**（事务卡以玩家为目标 → JC128/065 的 `PlayCard` 侧）、**联机的激活类效果**整体尚未接线（JC111 的能力在联机下无入口） |
| K25 | 构筑/场上唯一性 | 🟡 **场上唯一性已交付**（**2026-09 用户改口径：可排期**，见 A20/A25） | ① **场上唯一性**：已有 `LeaderRule`（领袖）＋**新建 `PathRule`（道途，与领袖同款：玩家选一个牺牲）**→ **JC034① 已交付**（见 §7.10 55）；② **构筑侧**：`GameDeckValidator` 明确"不做张数上下限/同名上限/禁卡表" → **同名卡上限本身尚未实现**，故 **JC125**「构筑时数量没有限制」的**豁免对象暂不存在**（待上限规则落地后再接；**上限数值需用户确认**） |
| K26 | 发动窗口细化 | ✅ 已交付（本轮，判据已按用户二次确认收窄） | `EffectData.ExcludedPhases`（状态名清单）＋**单一判定 `PhaseGateRule`**；口径＝**仅"伤害分配窗口打开的那一小段"**：`CombatManager.IsDamageAssignmentOpen`（单机 `OpenCombatWindow` 置位/窗口关闭回调复位；联机 `ResolveWinnerAsync` await 前后）→ `TurnManager.IsDamageAssignmentOpen`；消费点＝`PlayCard`（事务卡）/`ActivateEligibility`/`EffectEngine.ActivateAbility`。覆盖 **JC008**。未登记状态名按"不排除"处理并告警 |
| K27 | 手牌/墓地来源发动或打出 | ✅ 已交付（**来源门禁 + 墓地打出 + 手牌发动**） | **已交付（本轮）**：(1) `TurnManager.PlayCard` 新增**服务端权威来源门禁**——默认只允许**手牌**来源，其余（牌库/墓地/堆叠/场上/封印区/资产区）一律拒绝（补上了长期缺失的越权路径）；(2) 声明 `PlayerCardData.PlayableFromGraveyard`（卡文"**墓地行动**"）的卡额外放行**墓地**来源，费用/忠诚/行动步骤照常 → **JC085**；(3) `LegalityProjection` 的打出候选把墓地中这类卡列为可打出源。**已全部交付（本轮补齐）**：**"手牌行动"**——激活类效果声明 `Scope = ZoneScope.HandZone` 即可在**手牌**中发动（无需新标志）；`EffectEngine.ActivateAbility` 新增**来源生效区门禁**（默认角色=争夺区/附属=附属区；手牌行动=手牌），并堵住"来源不在生效区也能激活"的漏洞；`ActivateEligibility` 的 `CanActivate`/`GetEligibleAbilities` 把手牌来源纳入；View＝`CardUI` 双击手牌中的"手牌行动"卡请求激活（先于拖拽 `AcceptEvent`，普通手牌不受影响）→ **JC110①** |
| K28 | 免费打出 | ✅ 已交付（2026-09） | `TurnManager.PlayCard(..., freePlay:true, asIfQuick:true)`——`freePlay`＝**费用 0**（含跳过额外费用）而**忠诚照常校验**；`asIfQuick`＝**跳过行动阶段门禁**（"视作【快速】"）；配合**免费打出队列**（效果在结算期只登记，`ResolveStackTop` 在原子锁解除后执行）→ **JC119②**（见 §7.10 72）。**④目标交互已补齐**：单机 `IGameUIHooks.PromptPlayTarget`（复用激活效果的目标选择通道）；**联机**经 `SelectTarget` 回环（`TurnManager.RequestFreePlayTargetAsync`，见 §7.10 78）；无 UI → 确定性兜底取首候选；取消/无候选 → 留手牌 |
| K29 | 阶段钩子 | 🟡 部分（**重置步骤钩子已交付**） | `OnTurnEnded` ✅（JC054 结束步骤潜伏已用）；**已交付（2026-09）**：**重置步骤静态规则**——`StaticKind.NoUntapWithSealedCards` + 消费点 `CardManager.UntapAll`（重置步骤**唯一**解横置出口）→ 被锁角色不解除横置 → **JC108**（见 §7.10 ㊳）；"进入地区时支付1"的移动钩子语义已具备（`OnMove`）但可选支付（K11）已交付、尚未接线。**仍缺**：通用"重置步骤触发（非静止规则）"时机（如 JC122 的翻面限制属另一类静止规则，可按同款扩展） |

### 7.10 P3 交付明细（2026-09，累计：K14 / K23 / K26 / K2 / K1 / 或种类 `KindAlt` / K15 / K16 / K11 / K13 / K5 缩放 / K4 区域转换 / K8 检视 / K24 玩家目标 / `OnInvestigationWon` 时机 / K17 门禁+分支双条件 / 回合内事件记忆位 / 多玩家弃牌编排 / 宿主种类前提 + 一次性承诺 / 步骤级承诺 + 控制权转移与归还 / 动作成果（"若你如此做"）/ K2 宿主集合 / K13 运行期授予触发类关键词 / 费用通道隔离 `CostScope` / 防止伤害（伤害护盾）/ 图标镜像（持续授予来源改写）/ K18 地区级"略过对抗" / K21 地区级"敌方暗藏者不参与势力对抗" / K21 目标须带卡上标志 `HasCounter` + `Move` 目的地写法 / K3 地区赢取时的去处替代 `WinAreaDestination` / K29 重置步骤静态规则 `NoUntapWithSealedCards` / K18 地区级"额外进行一次势力对抗" `ExtraAreaPowerContest` + 本回合型 `SkipAreaCombat` / K22 "牺牲一个角色"作代价 `SacrificeCostSpec`（JC094/JC093/JC096/JC038；`Gain` 主体 `结附宿主`）/ K3 持续型伤害重定向 `DamageRedirect`（JC077/JC121）/ K20 图标替换＝属性替换 `ReplaceIcon`（JC106）+ 持续型"失去图标" `IconSuppression`（JC123）+ 持续型"失去魔法领域图标" `DomainSuppression`（JC099）+ "文本视作空白＝失去效果" `TextBlank`（JC101）/ K13 运行期授予激活能力 `GrantedAbility` + `DealDamage{"不可防止"}`（JC019）/ K15 激活能力的"目标地区" `NeedsAreaTarget`（JC117/JC033①）/ **K16 触发版「选择一项」（入堆叠时选定；JC110②）＋ `ZoneScope.SealedZone`（"被封印时"触发生效区，救活 JC100）/ K3 死亡类第一批（触发路线：`DiedAllySpiritCharacter` + 动作主体 `"触发卡"` + 触发上下文卡统一下发；JC105②）/ 非抓牌式取牌（`DrawFromBottom{"不计抓牌"}`：不广播 `OnDraw`、空库不判负；JC049②）/ 用户口径批（战斗获胜近似判定 A14 → JC022②；"锁定者禁现身" `NoRevealWithLockCounter` A18 → JC122②；JC074 补齐）/ K25「道途」唯一性 `UniquePathAtMostOne` + `PathRule`（与领袖同款：玩家选一个牺牲；A25 → JC034①）/ K19 临时费用池（优先花费 + 步骤结束清除；A26 → JC115）/ K5 计数器作发动代价（`CounterCostSpec` + 发动者选择 + 入堆叠前支付；A17/A27 → JC044②）/ 参数化条件 `EffectCondition.Param` + `AreaMarkerAtLeast`（地区内卡上标志合计 ≥ N）＋ 地区标志全清（JC127 终局）/ 准备步骤放标志（JC055①）/ A14 战斗获胜近似判定（`WinnerIsSelfWithAllySubCategoryInCombatArea` → JC034③）/ 地区内卡上标志计数取值 `计数:地区内卡上标志:…` ＋ 准备步骤三步触发（JC048）/ `ActionVerb.Attach` 附属进场自选结附目标（JC121①）/ 未结附态「不能移动」静止规则（`CannotMoveWhileUnattached`，模板印刷判定；JC121③）/ K22 多玩家/对手侧自选牺牲 `SacrificeChoice` ＋ JC128 二选一 `BribeOrSacrifice` / **K4 持续夺控 `ControlGain`（注销归还拥有者、不刷版本号）＋ 子类别改写 `SubCategoryRemove`（实例当前类别，印刷类别不变）＋ 通用规则「宿主翻面 ⇒ 其上附属被消灭」`AttachmentRule`（JC036）＋ `NotCurrentSubCategory` 实例口径（JC015/JC019/JC091 修正）** / **K10 额外费用 `ExtraCostSpec`（付费阶段/入堆叠前：牺牲一个角色 或 支付 X＝目标印刷费用；JC049①/JC135① ②）** / **K1「玩家选择」型动态值（`ValueMin`/`ValueMax` ＋ 数值输入窗，结算前挂起并重入）＋ 牌库顶费用比对动词 `RevealTopAndTakeIfCostIs`（JC083）** / **K5「移除 X 个标志」可变数量代价（`AmountByPlayerChoice` ＋ 逐卡分配 ＋ `代价数量` 取值）＋ 费用修正双通道 `打出,现身`（JC055②）** / **可选的「牺牲自身」动词 `SacrificeSelfOptional`（0/1 窗口 ＋ 链式中止）＋「查看目标玩家手牌 → 由施术者指定弃牌」条件链（JC114）** / **P3·K4 实例级形态转换 `FormChange` ＋ `TransformForm`（类别覆盖/改卡名/印刷效果失活＋形态效果表/清图标与防御/追加子类别）＋ `EffectsOf` 效果表收口 ＋ 激活目标精筛补齐（JC027/JC028）** / **K21 按组织的进入限制 `BlockNonOrgEntryInArea`（正面打出 + 移动进入两个消费点）＋ 「支付否则牺牲」动词 `PayOrSacrificeSelf`（JC064）** / **K21 暗藏者专用激活 `RequireSourceCovert` ＋ 地区子类别前提 `RequireSourceAreaSubCategory`（`MeetsSourcePrereqs` 候选/权威同源；JC041）** / **K28 免费打出 `PlayCard(freePlay, asIfQuick)` ＋ 免费打出队列（原子锁解除后执行）＋ 封印回手标记 `ReturnedFromSealHostUid`（JC119②）**）

> 三项均已**按用户逐条确认的口径**实现（未确认的一律未做），每项都有独立行为用例套件。

**① K26 阶段排除（覆盖 JC008 灵能激发）** —— `EffectData.ExcludedPhases` + `Script/Manager/PhaseGateRule.cs`
- 口径（用户二次确认）：FAQ"不能在分配战斗伤害阶段使用"＝**只指"伤害分配窗口打开的那一小段"**，**不是**整个 `TurnPhase.CombatStrength`。
- 实现：`CombatManager.IsDamageAssignmentOpen`（单机 `OpenCombatWindow` 置位、窗口关闭回调复位；联机 `ResolveWinnerAsync` await 前后）
  → `TurnManager.IsDamageAssignmentOpen` → `PhaseGateRule` 求值（单一判定；未登记状态名按"不排除"处理并告警，防拼错静默失效）；
  消费点三处：事务卡打出（`TurnManager.PlayCard`）、激活与秘社激活的可发动判定（`ActivateEligibility`）、服务端权威门禁（`EffectEngine.ActivateAbility`）。
- 测试：`P3PhaseGateTests`（6 例：窗口打开/关闭的判据、未登记 fail-open、JC008 结构、窗口内不可打出而窗口外可打出、激活门禁三层一致、效果落地与回合结束还原）。

**② K23 次数上限（覆盖 JC032 血族长老）** —— `EffectData.MaxUsesPerTurn` + `CardInstance` 计数
- 口径（用户）：**每张卡 · 每回合 · 每能力**；**卡版本号变动 ⇒ 限制重新可用**；回合重置发生在**重置步骤**。
- 实现：`CardInstance.GetAbilityUsesThisTurn/MarkAbilityUsedThisTurn/IsAbilityLimitReached`（按 `EffectId` 计数）+ `BumpVersion` 顺带清空 +
  `TurnManager.DoReset` → `CardManager.ResetAllAbilityUsesThisTurn()`；消费点：`ActivateEligibility`（高亮/候选）、
  `EffectEngine.ActivateAbility`（代价之前拒绝、接受后登记）、`TurnManager.ResolveEntry` 的 Trigger 分支（结算前检查+登记，**同批次多条触发也只结算到上限**）；
  快照随 `ZoneCardRef.UsedAbilityIds/UsedAbilityCounts` 下行，客户端 `CanActivate` 与服务端同解。
- 测试：`P3UseLimitTests`（8 例：每卡每能力独立计数、两卡互不影响、达上限后高亮/候选/权威三层拒绝、版本号变动后重新可用、
  重置步骤清零、触发类能力受限、JC032 结构与"牌库顶 6 张检索吸血鬼→暗藏进场"端到端）。

**③ K14 墓地 / 手牌目标（覆盖 JC086 窃尸人）** —— `CardManager.MapInstanceToTargetKind` + `Script/Manager/TargetZonePool.cs`
- 口径（用户）：**允许指定对手的手牌/墓地**；**结算时目标位置版本号变动即丢失**（复用既有 D16 版本号逻辑，不新增判据）。
- 实现：大类按**所在区域**优先映射（墓地/手牌优先于卡类别）；`TargetZonePool` 统一"候选枚举 + 文本筛选（`TargetSpec.Filter`，
  语法复用 `DeckSearchFilter`，未知字段 fail-closed）+ 精筛（`Restrictions` 的 Friendly/Enemy 等，复用 `TargetValidity.CheckRestrictions`）"；
  挂载点：`TurnManager.GetLocalZoneTargets`（触发/效果取目标）、`ActivateEligibility.HasLegalTarget`（激活取目标）；
  选择窗口：这些牌**没有场上 CardUI**，故单机走 `IGameUIHooks.PromptPickCard`、联机走 `ChoiceRequest.Kind.SelectZoneCard` + `OptionCardRefs`
  （`TurnManager.EnsureTargetThenPush/Async` 分流；无 UI 时确定性兜底取第一候选）。
- 测试：`P3TargetZoneTests`（8 例：区域映射、本方/对手墓地与手牌候选、未知字段 fail-closed、JC086 回手端到端、目标离场即丢失、
  单机选卡窗、无 UI 兜底且**不走场上点选**、联机 `SelectZoneCard` 请求字段）。

**顺带修复（测试中发现，属既有缺陷）**：`TurnManager.ResolveEntryTargetIsLost` 的 Trigger 分支此前**只要条目带 `TargetCardUid` 就做版本号比对**，
而触发条目的 `TargetCardUid` 是**触发上下文卡**（死亡触发＝刚死亡的卡）——死亡会 `BumpVersion`，于是**死亡类触发在结算时被整条丢弃**。
现已改为**仅当该效果声明需要指定目标（`Target.NeedsSelection == true`）时**才做丢失判定，与 `EffectEngine.ValidateTargetAtResolve`
对 `NeedsSelection==false` 返回 `NoTarget` 的口径一致。全套件回归通过（该次修复时快照：55 套件 391 例 0 失败；**当前全量见 §7.10**）。

**④ K2 群体动作（覆盖 JC011 / JC047 / JC050 / JC052 / JC056 / JC132）** —— `TargetSpec.MassMode` + `Script/Manager/MassTargetPool.cs`
- 口径（用户 2026-09，二次确认）：**不实现"批量期间挂起触发"**。群体效果一般包在**一条效果/一段原子锁操作**内，
  堆叠结算期间本就在原子段（`TurnManager.EnterAtomic`，`Pass`/`PlayCard` 被拒），因此中途不会有其它玩家行动；
  各目标死亡产生的死亡触发按既有触发管线收集，**在本次结算走完后**由触发窗口/堆叠处理（多条由玩家排序）。
  实现只需保证"**类似效果能完整执行**"。
- 实现：`TargetSpec.MassMode = AllInArea` 时本效果**不取单个目标**，而是作用于集合：
  地区＝本次结算的目标地区（打出时选的地区／触发事件所在地；空＝来源所在地区）；
  集合＝该地区内满足 `Kind`（`IncludeCovert` 放宽暗藏者）+ `Filter`（检索筛选语法）+ `Restrictions`（Friendly/Enemy…）的卡，
  排除正在死亡的卡与来源自身（除非 `CanTargetSelf`）；**同一动作序列对集合每张卡各执行一次，一次结算内全部完成**
  （同步/异步两条路径都接；`ResolveSorcery` 现会把"打出时选定的目标地区"透传给效果结算）。
- 测试：`P3MassTests`（10 例：集合按地区/阵营/暗藏者收敛、IsDying 不入集合、
  JC052 本地区其他角色与暗藏者全灭、**群体动作完整执行且死亡触发随后入堆叠**、
  JC050 结构与"所选地区全清"、JC011 敌方角色横置、JC056 暗藏者横置、JC132 横置所选地区角色、JC047 所选地区各 1 点伤害）。

**⑤ K14 资产目标（并入上条 K14 实现）** —— 用户确认"**资产也一样**：卡进入/离开资产区都更新版本号"。
- 实现：`MapInstanceToTargetKind` 增加"资产区 → `TargetKind.Asset`"的区域优先映射；`TargetZonePool` 的范围扩为
  **墓地 / 手牌 / 资产**（`IsZonePileKind`），候选/筛选/阵营判定不变；选择窗口沿用 `PromptPickCard` / `Kind.SelectZoneCard`。
- 版本号核对结论（用户要求确认）：**只有"争夺区→争夺区"不刷新版本号**，其余区域切换（手牌/牌库/墓地/资产/秘社/封印/堆叠/计分区）
  与**翻面**都刷新——与用户口径一致，无冲突（证据：`ZoneManager.FireMoved` 的唯一 `bumpVersion=false` 调用点在 `MoveToArea`
  的 `fromZone == AreaZone` 分支；`SetCovert` 亦 `BumpVersion`）。
- 测试：`P3TargetZoneTests` 增至 9 例（新增：资产区映射与"进入资产区刷新版本号"、本方/对手资产候选、资产目标离场即丢失）。

**⑥ K26 判据收窄（修正）** —— 用户 2026-09 二次确认：FAQ 的"分配战斗伤害阶段"**只指伤害分配窗口打开的那一小段**，
不是整个 `TurnPhase.CombatStrength`。实现改为 `CombatManager.IsDamageAssignmentOpen`（单机 `OpenCombatWindow` 置位、窗口关闭回调复位；
联机 `ResolveWinnerAsync` 在 await 前后置位/复位）→ `TurnManager.IsDamageAssignmentOpen` → `PhaseGateRule` 求值；
测试改为"窗口打开中拒绝 / 窗口关闭后（同一战斗对抗阶段内）放行"。

**⑦ K1 动态数值 X（覆盖 JC040 遏制X / JC097 袭击X）** —— `EffectData.ValueExpr` + `Script/Manager/EffectValueResolver.cs`
- 口径（用户 2026-09，逐条确认）：
  · **X=0 ⇒ 该效果不算发动**（整条不执行，不入堆叠）；
  · **玩家主动选择的 X 不能为 0**（选择窗口拒绝 0）；
  · **由计数/事件派生的 X 若为 0，同样不算发动**（不是"以 0 执行一次"）。
- 实现：卡文里的 X 由 `EffectData.ValueExpr` 声明（空＝不适用），动作参数里写占位符 `$X`；
  `EffectValueResolver.Evaluate` 是**唯一求值实现**，返回 `ValueEvalResult{NoExpr / Ok(value) / Fail(reason)}`，
  `EffectEngine.EvaluateDynamicValue` 映射为 `DynamicValueState{NotApplicable / Ok / Blocked}`，
  **`Blocked` ⇒ 整条效果不发动**（在 `ResolveEffect` 入口判定，早于取目标与代价）；
  数值在执行前用 `EffectEngine.SubstituteValueArgs` 写进动作/目标参数。
- 已登记表达式（**未登记的写法一律 fail-closed → 不发动 + 告警**，避免拼错字段名静默算成 0）：

  | 写法 | 含义 | 已接入 |
  |---|---|---|
  | `事件数量` | 本次触发事件携带的数量（`TriggerContext.ExtraInt`，如"此次弃牌的总张数"） | **JC040** |
  | `计数:区域:<墓地\|手牌\|资产\|争夺区\|附属区\|秘社\|封印区>:<角色\|附属\|法术\|任意>:<自己\|对手\|双方>` | 某区域内的卡数（按印刷类别筛选、隐藏占位不计） | **JC097** |
  | `计数:卡上标志:<标志名>:<自身\|目标>` | 某卡上的标志数 | 已实现，待 JC045 等接入 |
  | `计数:地区标志:<自己\|对手>:<本地区\|目标地区>` | 某地区的势力标志数（走 `AreaManager.GetPowerMark`） | 已实现，待 JC048/127 等接入（还缺 K5/K2） |
  | `玩家选择` | 玩家在**数值输入窗**里给出的数字（范围由 `EffectData.ValueMin/ValueMax` 声明） | **JC083**（2026-09 已交付；未取值/无 UI → fail-closed） |

- 事件数量透传：新增 `EffectEngine.ResolveEffect(..., int eventAmount)` 重载，触发管线把 `ExtraInt` 带进结算。
- 测试：`P3DynamicValueTests`（7 例：X>0 正常执行并替换 `$X`、**X=0 不发动**、未知表达式 fail-closed、三类计数表达式、
  事件数量缺失（无 `ExtraInt`）判失败、空 `ValueExpr` 不做任何判定（旧卡零影响）、JC097 结构与"墓地角色牌数"端到端）。

**⑧ 或种类 `KindAlt` + 目标候选池扩大（覆盖 JC107 崩塌的时空）**
- 口径（用户 2026-09）：① JC107 **只选一个目标**，该目标满足"附属 **OR** 资产"；② 目标候选池**"可以扩大"**。
- 实现：`TargetSpec.KindAlt`（`TargetKind.None`＝不使用）与 `Kind` 构成 OR，落地在**四处**（少任何一处都会"取到目标却打不出/结算被拒"）：
  `TargetZonePool.Collect/Matches`（候选枚举 + 合法性）、`TargetValidity.CheckZoneAndFace`（结算复检）、
  `CardManager.MatchesRule`（打出粗筛）、`EffectEngine`（激活/触发目标复检）。
- 候选池扩大：`TargetZonePool.IsZonePileKind` 由 **墓地/手牌/资产** 扩为 **墓地 / 手牌 / 资产 / 附属区 / 秘社 / 封印区**，
  至此"**附属或秘社也能作为效果/激活的目标**"不再是空白（此前 `GetAreaCards()` 只扫争夺区 → JC013 类"横置目标角色**或附属**"永远判无合法目标）；
  这些区域的卡没有场上 `CardUI`，统一走选卡窗（单机 `IGameUIHooks.PromptPickCard`／联机 `Kind.SelectZoneCard`）。
- 测试：`P3TargetZoneTests` 增至 13 例（新增：JC107 结构（两条 `TargetRule` OR + 单目标 `KindAlt`）、附属/资产候选合并、
  结算复检接受任一、**JC107 消灭附属 / 消灭资产端到端**、**激活目标候选含附属区与秘社**）。

**稳定性修复（本轮，测试基础设施）**：全量套件偶发原生崩溃
（`FATAL: Condition "gchandle.is_released()" is true` → `GodotObject.Finalize` 访问已释放句柄，定位在 `LobbyFlowTests` 附近），
根因是 GC 压力下 Godot `RefCounted` 终结器竞争。两处修复：① `SubstituteValueArgs` 改为复用 `_scratchAction`，
不再每次求值新建 `RefCounted`；② `TestsRunner` 在套件之间执行 `GC.Collect(); GC.WaitForPendingFinalizers();`。
修复后**连续两次全量跑全绿**。

**⑨ OnDraw 按批粒度 + JC080 凯拉（覆盖 JC080）** —— `TriggerEmitPoints.OnDraw` + `TurnManager.DrawCardsForPlayer/DrawFromBottomForPlayer`
- 口径（用户 2026-09）：**一批抓牌只广播一次，并带上本批张数**（替代原来的"逐张广播、不带张数"）。
- 实现：`TriggerEmitPoints.OnDraw(drawnCard, playerId, batchSize)` → `TriggerContext.ExtraInt = batchSize`
  （批次边界＝**一次抽牌调用**：如"抓两张"＝一批 X=2，而非两张各 X=1）；
  `TurnManager` 两个抽牌收口改为"本批全部抓完后只广播一次"（`ContextCard`＝本批第一张，供条件判定有确定对象）；
  **一张都没抓到（牌库耗尽判负）→ 不广播**；从牌库底抽牌同口径（秘社 MSJC001"从牌库底抓1张"本就是抓牌）。
- JC080 效果：`Timing=OnDraw` + 条件 `TriggerBySelf`（只有"你"抓牌才触发）+ `ValueExpr="事件数量"`（本批张数）
  + `PlaceToken{势力标志,$X}`（落点＝来源所在地区＝"本地区"，归来源控制者＝"本方"）+ `Forced=false`（"你可以"＝可选）。
- **OnDiscard 同口径**（用户 2026-09 二次确认）：`TurnManager.DiscardCards` 的调用方（单机 `EffectEngine.PromptOrFallbackDiscard`／
  联机 `DiscardViaGatewayAsync`）**先把本批要弃的牌收集齐，最后一次性落地**，因此 `OnDiscard` 也**每批只广播一次**，
  X＝该次弃牌动作的总张数——修掉了"对手一次弃 3 张 → JC040 触发 3 次、每次 X=3（共清 9 个标志）"的偏差
  （统一为 1 次、X=3）；本批内不会重复选中同一张（候选剔除本批已选但未落地的牌）。
- 测试：`P3DrawBatchTests`（6 例：一批只广播一次且事件数量＝本批张数、牌库底抓牌同口径、牌库耗尽不广播、
  JC080 结构与"抓 2 张→本地区 +2 本方标志"端到端、抓 1 张 X=1、**对手抓牌不触发**）；
  `P2DiscardTests` 增至 6 例（新增：**一次弃 3 张只收集 1 条触发且 ExtraInt=3**、**JC040 一次弃 3 张只遏制 3**）。

**⑩ K15 多目标 + 确认面板（单机已交付；覆盖 JC021 狭路相逢）** —— `EffectData.TargetSlots` + `Script/Manager/MultiTargetSlots.cs` + `TurnManager.BeginMultiTargetPlay` + `IGameUIHooks.PromptMultiTargets`
- 口径（用户 2026-09 逐条确认）：**至多 2 个目标＝「本方角色」1 个 + 「敌方角色」1 个**，**各自可留空，但至少要选 1 个**；
  **目标地区由玩家选择**；FAQ **禁止原地移动** → **已经在目标地区内的角色不出现在候选里**；
  **选完目标后弹确认面板，确认通过才把效果加入堆叠**（单机＋联机都要）。
- 数据模型：`TargetSlot{ Spec, Optional, Label, ExcludeInChosenArea }`（`ExcludeInChosenArea` 表达"排除位于已选『地区槽』内的卡"＝
  槽间依赖，因此取目标顺序上地区槽先于卡槽）；`EffectData.MinChosenSlots` 按**卡目标槽**计数（地区槽不算"目标"）。
- 单一实现 `MultiTargetSlots`：`CollectSlot`（按槽取候选，地区槽＝全部地区；卡槽＝`TargetZonePool` 的"区域牌堆"或争夺区扫描，
  复用 `MassTargetPool.Matches` 的 Friendly/Enemy + 文本筛选判定）、`Validate`（必选槽 / 至少 N 个目标 / 禁止原地移动 / 同卡不可占两槽）、
  `SlotAlignedCardUids`（**按卡槽对齐**写进 `StackEntry.TargetCardUids`，留空槽保空串——否则结算时无法还原"哪个目标属于哪个槽"）、
  `Reconstruct`（由入栈负载反推逐槽选择）。`StackManager.Push` 顺带登记 `TargetVersionIds`（**逐目标**版本号）。
- 打出/结算链路：`PlayCard(..., multiChoices)` 在**付费之前**校验多目标（非法→整次打出失败、不扣费、不入堆叠）→
  写入条目（`TargetAreaId`＝地区槽、`TargetCardUids`/`TargetVersionIds`）→ `ResolveEntry` 透传 → `EffectEngine.ResolveEffect(..., choices)`
  逐槽复检后**对每个被选中的卡目标各执行一次动作序列**（作用地区＝地区槽）；`Move` 动词新增 `Args[0]="目标"`（缺省 `"自身"` 保持机动语义）。
- D16：`TurnManager.PruneLostMultiTargets` 逐目标比对版本号，**只跳过丢失的那一个目标、其余照常结算**（用户 2026-09 口径；
  地区槽丢失仍按既有单目标 D16 口径：整条不结算、卡进墓地）。`IsMultiTargetLost` 保留为查询用。
- **联机（服务端驱动，用户 2026-09 口径"服务端逐槽下发选择请求 → 最后 ConfirmTargets → 入堆叠"）**：
  `ChoiceRequest.Kind.MultiTargetSlot`（`SlotIndex/SlotCount/SlotLabel/SlotOptional` + 候选/展示引用/`OrderContextId`；
  回包空 `SelectedUid`＋`Cancelled=false`＝该槽留空、`Cancelled=true`＝取消整次）→ `Kind.ConfirmTargets`
  （`OptionLabels`＝逐槽摘要；回包 `ChoiceResult.Confirmed`，**public 字段 + `IncludeFields` 自动往返**）→ 确认后 `PlayCard`。
  服务端 `TurnManager.PlayMultiTargetCardAsync`：选目标期间持原子锁，**调用 `PlayCard` 之前必须释放**（`PlayCard` 自身会 `EnterAtomic`，
  且开头拒"原子操作进行中"——首版漏了这点导致"确认通过却 PlayCard=False"）；完成后 `OnAsyncSettlementCompleted` → `MatchHost` 下发快照。
  客户端：`ArenaOl` 对多目标卡只发"裸打出意图"、`ChoicePromptPanel.BuildMultiTargetSlot/BuildConfirmTargets` 负责两个新面板。
- 测试：`P3MultiTargetTests`（16 例：结构、按槽候选与目的地排除、组合校验四种、两目标端到端、只选一个、非法组合被拒且不扣费、
  **部分丢失只跳过丢失的那个**、**全部丢失则无人移动**、未提供逐槽选择则拒绝、确认面板确认后打出（含候选随目的地动态刷新）、确认面板取消则不打出、
  无 UI 兜底不静默跳目标、**联机逐槽请求+确认后入堆叠**、**联机确认面板取消则不打出**、**联机逐槽取消则中止**、**协议字段往返**）。

**⑪ "成批推进"批次（2026-09，按进度表挑"只差一个机制"的卡；用户已逐项确认口径）**
覆盖 11 行效果 / 6 个新卡脚本（JC084/JC092/JC133/JC073/JC059/JC105）+ 3 张既有卡补齐（JC004/JC095/JC016），并修掉 1 个引擎缺陷。

- **A. `ActionVerb.RemoveToken`（移除**地区**势力标志）→ JC095 唐·格鲁尼**
  口径（用户）：**直接移除（不由对手分配）**、**只作用于指定地区**（区别于遏制X `Curb` 的"X<总数时按地区分配、范围全场"）。
  Args：`[指示物类型, 数量("全部" 缺省 / "N"), 归属("对手" 缺省 / "自己"), 地区("本地区" 缺省 / "目标地区")]`；
  X 由 `ValueExpr="计数:地区标志:对手:本地区"` 在**移除之前**求值 → `PlaceToken{势力标志,$X}` 放置等量；X=0 时不发动。
- **B. `PrereqKind` 扩展（持续前提）→ JC016 执行部精锐 / JC084 街头混混（2 行）**
  · `NoEnemyCharacterInArea`＝所在地区没有敌方**正面角色**（暗藏者不算角色）；
  · `AreaMarkOwner`＝所在地区有指定归属（`ContinuousPrereqSpec.Param`＝"自己"/"对手"）的势力标志 ≥ `Amount`（缺省 1）。
  重判时机：敌方卡进出该地区（`ZoneManager.FireMoved` **对双方控制者都重算**）、势力标志增减
  （装配层订阅 `AreaManager.OnMarksChanged` → 双方重判）。
- **C. `ActionVerb.PutToArea`（把目标牌放进本次结算的目标地区）→ JC092 死灵召唤 / JC133 操控死尸**
  复用刚交付的 **K15 双目标**（地区槽 + 你墓地角色牌槽）；Args `[朝向("正面"/"暗藏"), 附加子类别(可选)]`：
  **暗藏**＝先 `SetCovert(true)` 再进场（与"派遣暗藏者"同序）；移动经 `CardManager.MoveCardToArea` → **照常广播进场触发**；
  附加子类别经 `Grant("SubCategory", 名)`＝**叠加**（不替换）。
- **D. 持续授予落点＝结附宿主（`EffectData.GrantToHost`）→ JC116 柯尔特巨蟒左轮 / JC073 苏族捕梦网**
  "受此结附的角色获得X" → 授予 `card.AttachedTo`（宿主）；**无宿主不授予**；注销时按登记记录里的落点 UID 精确撤销。
  为此把持续登记表的值类型改为 `ContinuousGrantRecord{TargetUid, Grants}`（撤销不再依赖当时的结附关系）。
- **E. 光环筛选扩展 → JC051 诺恩纳格斯塔 / JC059 安全保卫部门 / JC105 噩梦领主**
  · `AuraFilterOrganization`（组织筛选，JC051 鸣钟教派）；· `AuraFilterKeyword`（关键词筛选，JC105 灵体）；
  · `AuraLocalZoneOnly`（仅本地区，JC059）＋ `ContinuousGrants{Attribute:"防御"}`（`CardInstance.Grant/Revoke` 新增"防御"→ 折进 `CurrentDefense`）。
  三处判定（首次注册 / 重算撤销 / 重算补授）统一走新helper `IsAuraTargetEligible`，既有领域筛选（JC060）行为不变。
  范围口径（用户）：**未写"本地区"的光环＝来源控制者的全场争夺区角色**；写了"本地区"的只影响同地区。
- **引擎缺陷修复（本轮顺带）**：持续前提登记表原按**卡 UID** 登记 → 同一张卡有多条"条件持续效果"时
  （JC084 白星/白眼两条），后一条"前提不满足"的还原会把前一条刚授予的图标一起撤掉。
  改为按 **卡 UID + 效果 Id** 登记（`PrereqKey`），并按 `TargetUid` 记录落点。
- **确认口径（用户 2026-09）**：① JC095 直接移除不走遏制分配；② JC092/JC133 置入地区**照常广播进场触发**、
  JC133 "额外具有不死生物"＝**叠加**；③ 授予对象＝宿主、无宿主不授予；④ 光环范围按上表。
- 测试：`P3ConditionalBranchTests`(3) / `P3RemoveTokenTests`(6) / `P3PrereqKindTests`(5) / `P3PutToAreaTests`(6) /
  `P3GrantToHostTests`(4) / `P3AuraFilterTests`(5)。
- **仍未做（本轮明确留下的）**：JC038 的"授予**袭击**后可参与触发收集"（K13 残余；组织筛选已具备）；
  JC105 的死亡触发（死亡替代＋可选支付询问）；JC059 的"公开"词条本身已具备。

**测试与回归（本轮累计）**：新增 `P3PhaseGateTests`(6) / `P3UseLimitTests`(8) / `P3TargetZoneTests`(13) / `P3MassTests`(10) /
`P3DynamicValueTests`(7) / `P3DrawBatchTests`(6) / `P3MultiTargetTests`(16) / `P3ConditionalBranchTests`(3) /
`P3RemoveTokenTests`(6) / `P3PrereqKindTests`(5) / `P3PutToAreaTests`(6) / `P3GrantToHostTests`(4) / `P3AuraFilterTests`(5) /
`P3ModeBranchTests`(9) / `P3OptionalPayTests`(10) / `P3StrikeStackingTests`(6) / `P3ScaledGrantTests`(5) / `P3ZoneConvertTests`(5)，
`P2DiscardTests` 4→6，已注册进 `TestsRunner`；`dotnet build` 0 警告 0 错误；
headless 全量 `TestsRunner` **70 套件 502 例 全绿 0 失败 0 错误**。

**㉑ 宿主种类前提 + 一次性待触发承诺（→ JC082 索亚之书）**
- **① 宿主种类前提**：`PrereqKind.HostKind`（`ContinuousPrereqSpec.Param` = `"角色"`/`"秘社"`）用于**持续效果**；
  配套**条件谓词** `HostIsCouncil` / `HostIsCharacter`（`EffectEngine.HostKindOf`，与前者同源：
  `CardInstance.AttachedTo` → `CardManager.MapInstanceToTargetKind`）。无宿主 → 一律不满足（fail-closed）。
  用途：JC082「选择一项：·借阅＝结附于目标角色…／·收藏＝结附于本方秘社…」——
  **"选择的一项"由结附目标种类体现**（两个选项本身就是"结附于角色/秘社"，打出侧用两条 `TargetRule` 的 OR）。
- **② 一次性待触发承诺**（`EffectData.Pending` + `PendingEffectSpec{Timing, Expire, Optional, Actions}`）：
  卡文形态"**本回合中**，当你…**下一次**…时，**你可以**…"：
  · 效果结算时**只登记承诺**（不立即执行动作）→ `EffectEngine.RegisterPending`（同卡同时机只留一条）；
  · 时机首次出现且**归属该玩家**（`ctx.TriggeringPlayerId`）时由 `TriggerRegistry` **兑现一次**：
    合成为一条普通触发（`Forced = !Optional`）走**同一管线**（排序窗 → 入堆叠 → 让过 → 结算），并**立即消费**；
  · `Expire="本回合结束"` → 回合结束时（`ExpireTurnEndEffects`）未兑现的承诺**自动作废**；
  · 归属玩家未知（`TriggeringPlayerId < 0`）→ 不兑现（fail-closed）。
- 覆盖 **JC082 索亚之书**：借阅（宿主＝角色 → 授予［白眼］）／收藏（宿主＝秘社 → 快速行动2横置：
  本回合下一次赢得调查对抗时抓一张）。测试 `P3PendingEffectTests`(8)。
- **步骤级承诺（本轮补齐）**：`OnTurnStarted`/`OnTurnEnded` 属**无归属玩家的时机**（双方共有步骤，`TriggeringPlayerId = -1`）→
  这类时机下"所有在该时机登记的承诺都在事件出现时兑现"，不再按玩家过滤 → JC024「潜伏…在**下一个**准备步骤开始时令其现身」
  （因该能力在准备步骤后发动时事件已过，兑现必然落在下一回合的准备步骤）。
- 其余"步骤级"需求（如"结束步骤触发"）走既有 `OnTurnEnded` 时机，无需再扩机制。

**91 原子操作期间的玩家选择：决策持锁（用户 2026-09 补充口径，框架级修复，无新增效果行）**
- **用户口径原文**：「一个原子操作期间如果需要玩家选择，**玩家选择完成之前会阻塞进程**，直到玩家选择完成。
  完成前**只有持续效果可以影响游戏**，**无法加入新的行动效果/触发效果进入堆叠**。而**卡的打出（包括支付费用、
  额外费用、确认忠诚等待）属于原子操作**，**卡的结算/效果的结算也属于原子操作**，都需要遵守上面的规则。」
  （权威落点：`需求实现对照.md` §7.3.1；实现者清单：`制作指南/原子性规则.md`）
- **排查结论（修复前的 4 类框架级不合规，均已修）**：
  ① **单机所有通用选择窗在等待期间不持锁**——`Arena` 的 `LockModal` 只用于"触发排序/选目标/对抗"三类窗口，
     选卡/数值/模式/多目标/玩家目标/额外费用/激活代价/准备维护/预测等窗口开窗不通知规则层 ⇒
     `ResolveStackTop` 段末 `ExitAtomicWhenIdle` 见 `_openModalCount==0` 立即解锁 ⇒ 等待期间
     `Pass`/`PlayCard`/`ActivateAbility` **全部放行**（可真的在结算中途再打一张牌）。
  ② **4 处"只开窗不挂起动作链"**（效果的后续动作先于玩家选择执行）：`Discard` 动词 / `Seal{手牌}` /
     `ResolveSearch`（含回调内"选结附宿主"再开一次窗）；且 `allowCancel:true` 取消时窗口**不回调** ⇒ 动作链永久悬空。
  ③ **联机 5 处决策等待不持锁**：领袖 / 道途 / 遏制 / 预测 / JC119 免费打出取目标（A55 只覆盖了引擎侧
     `BeginGatewayFlow` 与回合侧若干入口）。
  ④ **激活/出牌的前置步骤早于 `EnterAtomic`**：`PlayCard` 的锁原先在"费用支付"处才置位（忠诚/模式/多目标/
     额外费用/取目标都在锁外）；`EffectEngine.ActivateAbility` **全链路无持锁**（仅入站拒绝），
     而"宣布使用效果"按规范是原子操作。
- **修复实现**：
  · `TurnManager` 新增**计数式决策持锁** `BeginDecisionHold`/`EndDecisionHold`（`_decisionHoldDepth`，可嵌套；
    解锁条件统一为"模态计数与持锁深度**双 0**"——修掉"某个模态窗关闭把外层持锁掀掉"的隐患；`Reset()` 归零）
    ＋ 联机等待入口 `HoldAtomicDuringDecisionAsync`。
  · `Arena` 新增 `ModalWindow.ChoiceBox` 与 **按窗口实例成对**的 `LockChoiceWindow`/`UnlockChoiceWindow`
    （`_openModalCounts` 改为按类型计数，多窗重叠不再互相抵消），覆盖 10 类提示方法；
    **交付选择之前先解锁再续算**（否则回调里的 `PlayCard`/`ActivateAbility` 会被入站锁拒绝）；
    无选择被关闭（取消/点窗外）由 `TreeExiting` 兜底释放。
  · `EffectEngine`：`SuspendChainForInput()` 挂起＝**同时进入决策持锁**，`ContinuePendingChain()` 严格配对释放
    （含"续算中再次发起决策"的接替）；补齐 4 处挂起-续算；新增 `HoldDecisionAsync` 供规则类联机等待复用。
  · `PlayCard` 原子段**上移到"确认忠诚"之前**（入站门禁仍留在锁外，防自锁）；`ActivateAbility` 从"开始改状态"
    起到入堆叠止持锁（`try/finally` 收口）；恢复步骤手牌上限弃牌（`DiscardToHandLimitAsync`）整段持锁（含帧等待）。
  · **回调唯一性**：`ActivationChoiceWindow` 新增 `OnCancelled`（取消/点窗外/关闭窗口都补交付一次回调）、
    数值窗取消 → `-1`、预测窗未确认关闭 → 原序置顶。这是"持锁不泄漏 + 挂起链不悬空"的前提。
- **覆盖清单（联机等待持锁）**：`EffectEngine.BeginGatewayFlow`、`RunTriggerPhaseAsync`、
  `ResolveCurrentAreaCombatAsync`、`ActivateAbilityWithChoicesAsync`、`PlayMultiTargetCardAsync`、
  `PlayWithExtraCostAsync`、`PlayModeCardAsync`、`RevealCovertWithExtraCostAsync`、
  `LeaderRule` / `PathRule` / `CurbRule` / `PredictionRule` / `TurnManager.RequestFreePlayTargetAsync`。
- **不改的边界**：不修改 `核心需求.txt`（用户原始文本）；不改窗口默认取消 UX（不新设 `Exclusive=true`，
  冻结依靠规则层锁 + `Arena.IsInteractionLocked` 拖拽/Pass/激活高亮拦截）；地区赢取"归库顺序选择"仍是 TODO
  （偏离 D1），仅在指南中写明"实现该 UI 时必须持锁"；调度步骤为联机专用且 `PriorityPlayerId=0` 已封死行动，判定为已满足。
- 测试：新增 **`P3AtomicDecisionLockTests`（18 例）**——锁原语（嵌套/模态不误放/`Reset` 归零/持锁期拒绝让过·出牌）、
  引擎挂起即持锁与挂起-续算顺序（弃牌/封印/检索/可选支付/多玩家逐位牺牲）、联机等待持锁（领袖/道途/遏制/预测/
  JC119 免费打出取目标）、打出原子段（`OnCardPlayed` 时刻可观测 + 早退不泄漏）、激活原子段（入堆叠时刻可观测 +
  早退不泄漏）、恢复步骤弃牌持锁。
- 账本：全量 headless **140 套件 / 1070 用例 全绿 0 失败 0 错误**；构建 **0 警告 0 错误**。

**89 秘社构筑限制模型扩展：白名单 ＋ 例外 ＋ 额度（用户 2026-09 逐条口径）**
- **背景**：`CouncilRestriction` 原模型只有"正向 AND 筛选 ＋ 数量比较"，**表达不了** 010~017 的
  "只能包含…**以及**其他派系的…"与"非X且非中立 ≤12"。用户选"先给字段设计再定"→ 出 `秘社构筑限制模型扩展设计稿.md`，
  逐条确认后落地（**用户拍板 5 条口径**）：
  ① "只能包含"＝**只要含白名单颜色就合法**（不要求全部颜色都在白名单里）；
  ② "中立"＝ `CardColor.Brown`（`PlayerCardData.Colors` 注释原文即"默认棕色（中立）"，全库 154 张棕牌）；
  ③ 白名单模式下 `GetActualCount` 返回**违规张数**（UI 直接显示"当前 7/12"）；
  ④ "［黑刀］能力图标"＝**战力 > 0**（**不含先手战力**）——数据层只有 6 个数值、没有图标标签，卡面图标是桌面游戏术语的电子化；
  ⑤ "不具有魔法领域图标"＝`MagicDomains` 为空**或只含哨兵值 `-1`**（实测无领域卡在 `.tres` 里就是 `[-1]`）。
- **新增字段（`CouncilRestriction`）**：`UseWhitelist`（启用白名单模式，false 时**旧逻辑完全不变**，001~008 的 .tres 不动）／
  `AllowedColors: Array<CardColor>`／`ExceptionMaxCount`（0＝一个违规都不许；12＝013/015）／
  `ExceptionRules: Array<CouncilExceptionRule>`（**OR** 语义）。
- **新增类 `CouncilExceptionRule`**（`Script/Data/Cards/CouncilExceptionRule.cs`）：`FilterByCategory` /
  `FilterBySubCategories: Array<string>`（**OR**，表达"吸血鬼**或**奴仆"）/ `FilterByDomain: Array<MagicDomain>` /
  `RequireNoDomain`（"**不具有**魔法领域图标"）/ `FilterByKeyword` / `FilterByAbilityIcon`（当前只认 `黑刀`，
  未登记名 **fail-closed** 并打日志）。
- **单一实现收口**：`IsSatisfiedBy`（白名单分支＝`CountViolations ≤ ExceptionMaxCount`，**与 `Comparison/RequiredCount` 无关**——
  首版漏了这条分支，被新单测当场抓出）／`GetActualCount`（白名单＝违规张数）／`Describe`（两种句式：
  "只能包含 黄色/中立 派系的牌，以及其他派系的：领域为 星空 的牌"／"非 红色 且非 中立 派系的牌不能超过 12 张"，
  棕色在白名单语境显示为"中立"）。`CouncilCardData.ValidateDeckDetailed` 与大厅 `GameDeckValidator` **自动继承，无需改动**。
- **生成脚本**：`_gen_restrictions.py`（幂等：先清上一次生成块，再写入 `[ext_resource] CouncilExceptionRule`
  ＋ 各 `ExceptionRule` 子资源 ＋ `ResWhitelist`，最后在 `Cost = N` 后插入 `Restrictions`；可重复运行）。
- **`.tres` 落地**：010~017 各自写入白名单 + 例外（见上）。
- **构筑界面 `DeckBuilder`**：原先**完全没有**限制校验/展示 → 新增运行期创建的"秘社限制"面板（逐条 `Describe(实际张数)`，
  满足=绿 / 不满足=红）＋ `OnSave` **不满足即拦截保存**并弹明细；切换秘社/加减牌/载入卡组都会重算。
- 测试：`P5CouncilRestrictionTests` 16→**28 例**（白名单命中/只要含即合法/空卡组安全/每条例外单独通过与否/
  额度 12 的边界/未登记图标 fail-closed/**不含先手战力**/Describe 两种句式/明细联动/010~017 线上 .tres 结构回归）。

**90 秘社（MSJC）010~017 八张按卡文落地 ＋ 一批新机制（用户 2026-09 口径）**
- **新增机制**（全部走"先扩机制再写卡"，卡脚本零特例）：
  · **牌作代价 `CardCostSpec`**（`EffectData.CardCost`，与 K22 牺牲代价/K5 标志代价同款纪律：
    **候选枚举与权威门禁共用同一实现**、**入堆叠前支付**、不足 N 张 → **拒绝发动且无副作用**）：
    `FromZone`（封印区/墓地/手牌/场上争夺区）＋ `HostSelf`（封印区取"来源卡自己的封印牌"）＋ `Count` ＋
    `Filter`（复用 `DeckSearchFilter`）＋ `UntappedOnly` ＋ `Mandatory` ＋ `CostAction`（复用既有动词）。
    ⚠ 约定：**`CostAction` 的对象一律写 `"目标"`**——`PayCardCost` 把选中的牌作为 `target` 传入，
    写 `"自身"` 会解析成来源卡（＝"把秘社自己塞进牌库"），实测踩到。新增
    `ActivateAbility(..., cardCostUids)` 重载与 `ActivateEligibility` 同步门禁。
  · **`DestroyWithMinCounters{标志名, N}`**（MSJC013②）：集合＝**全部争夺区**内（角色∪附属、不限敌我）
    其上该标志 ≥N 的卡；先快照 → 逐张 `DestroyInstance`（走死亡统一出口）→ 按**实际消灭数** `Draw`
    （卡文"**每有一个**…因此法被消灭，就抓一张牌"，用户口径＝按实际数；拆成两条动作无法携带该中间量，故合为一个动词）。
  · **`MillTopToGraveyard{N}`**（MSJC016①）：牌库顶 N 张 → 墓地，**不是"消灭"、不触发死亡时机**。
  · **`PlaceHandCardToArea{筛选, 朝向, 目的地, 附加}`**（MSJC015①）：从手牌选一张合格牌 → 可公开**展示** →
    **以暗藏者状态**置于目标地区（FAQ：带【公开】关键词的角色同样可行）。
  · **`UnsealToArea{来源区, 朝向, 宿主, 筛选, 归属}`**（MSJC010③ / MSJC016②）：从**封印区/墓地**取一张，
    **先解除封印关系再进场**（否则卡会同时留在宿主 `SealedCards` 里形成双重归属），
    `归属="由我操控"` 时改写控制者；联机走 `Kind.SelectZoneCard`（新 `UnsealToAreaViaGatewayAsync`）。
  · **`PutCardOnDeckTop{对象}`**（MSJC017②）。
  · **`TargetSpec.MinCounters`**（"其上有 N 个或更多某标志"）＋ `TargetSpec.AllAreas` 支持群体集合跨全部地区
    （`MassTargetPool.Collect` 新增 `AllAreas` 分支、`Kind=None`＝不限大类；`锁定标志/锁定标记` 两种写法都认）。
  · **`TargetRestriction.HasAbilityIcon`**（`Param="黑刀"`）：按**战力>0** 判定（**不含先手战力**，用户口径），
    印刷值优先、再放宽到 `CurrentStrength`（含光环授予）。
  · **`CouncilStaticGrant`**（`CouncilCardData.StaticEffectList`，MSJC012：印刷费用≤2 的本方人类获得【奴仆】类别）：
    **秘社是 `CouncilCardData`（`PlayerTemplate == null`），光环机制结构上覆盖不到**，故按 `LeaderRule`/`PathRule`
    同款建独立规则类，装配层 `ZoneManager.OnPostMove` → `EffectEngine.ReevaluateCouncilStaticGrants`（**逐秘社**遍历、
    幂等差量补授/撤销、离场自动收回）。
  · **`CouncilCardData.DrawFromBottom`**（MSJC017"从牌库底抓牌而非牌库顶"）：消费点＝抓牌**唯一收口**
    `TurnManager.DrawCardsForPlayer`（初始发牌/阶段抓牌/效果抓牌统一改向），不是逐卡特例。
  · **`CouncilCardData.OpeningEffectList`**（"每局游戏开始时…"，MSJC015 在秘社上放【隐秘】标志）：
    由 `GameInitializer.StartMatch → ApplyCouncilOpeningEffects` 在秘社就位后、发牌前执行一次；
    **`TurnStartEffectList`**（"**第一个回合的准备步骤开始时**…"，MSJC016 磨三张）由 `TurnManager.DoPrepare`
    在 `TurnCount == 1` 时执行（**必须在起手发完之后**，否则磨掉的是未发完的牌库顶）。
  · **`Seal{手牌}` 的候选筛选**（`Args[3]`，MSJC010①"从手中选一张【星】领域的牌"）＋
    `DeckSearchFilter` 新增 `领域:星空` 段与 `费用=N`（等值）。
  · **`Lose` 动词接通**（原为 TODO）：与 `Gain` 同一条属性通道的负数方向（`Revoke`），实现"失去灵体"。
- **逐张**（`.tres` 构筑限制见 89）：
  010 貘组 4 选项（①封印【星】领域手牌+抓1 / ②③置1张封印牌入牌库底+目标**获得/失去**灵体 /
  ④置2张+封印角色牌正面进场由我操控）｜011 S．P．T执行部 2 选项（①目标角色获得杀伤1 / ②所选地区带［黑刀］的本方角色获得撤回，
  群体集合在结算那一刻确定＝FAQ 口径）｜012 爱弗罗德家族 2 选项（①横置三个奴仆→抓1 / ②横置三个吸血鬼→目标敌方玩家弃1）
  ＋静态授予（费用≤2 本方人类获得奴仆）｜013 计时者 2 选项（①放时间标志 / ②消灭所有具≥3时间标志者并按实际数抓牌）｜
  014 哨兵 2 选项（①敌方暗藏者上放锁定标志 / ②目标本方角色移动到"锁定者所在地区"——`Move` 新增目的地写法
  `锁定者所在地区`）｜015 隐士 1 选项（选地区，手牌≥4 时从手中暗藏派遣一张角色）＋开局放【隐秘】标志｜
  016 颂亡者 2 选项（①磨1 / ②封印墓地七张 → 墓地【死】领域角色牌正面进场）＋首回合准备磨3｜
  017 梦魇族群 2 选项（①若有先手标志则封印牌库顶一张到目标角色 / ②墓地灵体角色置于牌库顶）＋从牌库底抓牌。
- **⚠ 与卡文的已知偏差（如实记账）**：**MSJC014②** 卡文"将其移动到**目标**具有锁定标志的角色或暗藏者所在的地区"
  ——真正的口径可能要求玩家**同时**选"被移动的本方角色"与"那个锁定者"，而引擎当前没有"第二个参考目标"的目标槽。
  本实现落地为：**玩家选被移动的角色，引擎按确定性规则取第一个锁定者所在地区**（无 UI/联机两端同解）。
  若要玩家自选锁定者，需先扩『参考目标槽』机制。
- **本批实测修掉的既有缺陷（5 处，均已加回归用例）**：
  ① `CardInstance.MagicDomains` 原先只在**进场路径**从模板拷入、且**离场复位**会清空 ⇒ 卡离开争夺区后
     "按领域筛牌库/手牌/墓地的牌"全部失效（MSJC016② 目标＝墓地【死】领域角色牌零候选）→ 构造时与复位时都按模板重建；
  ② `_actionChainAborted` **跨 `ResolveEffect` 泄漏**（复位点只在效果入口，而代价动作/挂起续算/多目标路径直接调
     `ExecuteActions`）⇒ 上一条效果的中止会让**后续无关效果的动作整段静默跳过**（现象：磨牌动词连日志都没有）
     → 复位收口到 `ExecuteActionsFrom` 入口；
  ③ `TurnManager.ReturnCardToDeck(uid,…)` 经 `GetInstance(uid)` 反查、查不到就**静默 no-op**
     ⇒ 新增"以实例为准"的正式入口 `ReturnCardToDeck(CardInstance,…)`；
  ④ `ZoneManager.MoveToDeck` 只改 `ControllerId` 不改 `OwnerId` ⇒ "把**对手拥有**的牌置于你的牌库底"会让双方
     牌库计数错位（牌库是唯一必须两者一致的区域）→ 显式对齐拥有者；
  ⑤ `DeckSearchFilter` 的 `类别:` 段对"尚未进过争夺区的实例"（牌库/手牌/墓地）永远匹配不上
     ⇒ 补模板 `CategoryFlag` 回落（与"子类别"同款纪律）。
- 测试：新建 `CouncilEffectBatch2Tests`（**14 例**）：逐张结构（选项数/费用/动词/筛选/条件/目标）＋
  牌作代价（封印区候选与不足拒绝 / 置入**控制者**牌库底 / 横置三个角色的候选筛选含"人类变奴仆"联动）＋
  `DestroyWithMinCounters` 按实际消灭数抓牌（含附属、含边界）＋磨牌（不触发死亡 / 多张）＋
  `PlaceHandCardToArea`（暗藏进场 + 非角色留手牌）＋`UnsealToArea`（封印角色牌正面进场由我操控 / 墓地【死】领域筛选）＋
  `PutCardOnDeckTop`（牌库顶）＋`CouncilStaticGrant`（费用/人类/敌方/离场四向）＋`DrawFromBottom`（秘社在场改向）＋
  `HandAtLeast` 谓词边界＋开局效果＋`HasAbilityIcon`（**只算战力、不算先手战力**）＋Seal 手牌筛选；
  另含 **3 个缺陷回归用例**（动作链不跨效果泄漏 / 领域在牌库手牌墓地仍可读 / 封印牌真正进入指定玩家牌库底）。
- 账本：全量 headless **139 套件 1051 例 0 失败 0 错误**（上批 138/995）；Arena / ArenaOl 冒烟 **0 SCRIPT ERROR**。
  **秘社不占 JC 效果行** ⇒ `JC卡牌效果进度表.md` 统计行 `280 = 260 + 1 + 0 + 19` **不变**（已用脚本逐行重算确认）。

**88 一键联机三开脚本加固：杀旧进程 + 重编译 + 就绪校验 + 各进程独立用户目录（用户要求）**
- **用户诉求**："LOG 没有什么变化，还是第一次都无法结算；检查 `一键联机三开.bat` 是否有关闭老进程/重新编译，没有就加上。"
- **排查结论（跑的还是旧程序集）**：用户那次会话的进程 **9:47:37 启动**，而修复后的程序集
  `.godot/mono/temp/bin/Debug/UWPro.dll` 构建于 **9:48:26** ⇒ 进程加载的是**修复前**的代码；
  脚本原实现**只对端口占用告警、不杀旧进程**（`dotnet build` 本来就有，所以问题不在编译）。
  已核对：修复代码在源码（`TurnManager.cs:2528` / `StackEntry.cs:150` / `EffectEngine.cs:6741`）且
  DLL 时间戳晚于三个源文件 ⇒ 只要用新脚本重跑，跑到的就是修复版。
- **另外发现两个"旧实例还在"的连锁故障**（都实测复现）：
  ① 旧服务端占着 UDP 端口 ⇒ 新服务端起不来、客户端连**旧代码**服务端；
  ② 多实例共用 `user://logs/uwpro.log`（`project.godot` 固定日志路径）时，Godot 启动的日志轮转
     会 `Failed to open ... copy` → **signal 11 崩溃**（服务端/客户端都中过，崩溃栈里可见）。
- **脚本改造（`start_online_3.ps1` + `.bat` 注释）**：
  · **[1/5] 杀旧进程**：优先 `Get-CimInstance` 按**命令行含本工程目录**精确匹配（不误杀编辑器/其它工程）；
    权限不足时退路＝"可执行文件 == 本脚本的 Godot"且**无主窗口**（只清 headless 旧服务端，保留用户正在看的窗口）；
    杀完再查端口，占用者若是本工程 Godot 继续杀，若是**别的程序**则**报错退出**（不再"以为换新了其实没有"）；
  · **[2/5] 编译**并打印构建产物路径 + 时间戳（并提示"时间不是刚刚 = 编译没写进去"）；
  · **[3/5] 服务端独立用户目录**（`<proj>\.godot_userdata_runtime\server`，只在 Start-Process 瞬间改 APPDATA）
    ＋ **以服务端日志为准的就绪校验**（等待 `[LobbyServerHost]`；异常/超时则打印日志尾部并退出，
    不再让客户端连一个没起来的服务端）。注：早先把 `Get-NetUDPEndpoint` 当硬门禁会**误判**（部分权限/网表查不到属主），
    已降级为"仅提示"；
  · **[4/5][5/5] 两个客户端各自独立用户目录**（`clientA`/`clientB`）＋ 启动后存活校验（退出则打印警告）；
  · 新增开关 `-NoKill` / `-NoBuild` / `-SharedUserData`；`.bat` 注释同步说明。
  · **编码修复**：脚本存为 **UTF-8 with BOM**——原文件无 BOM，`powershell 5.1`（`.bat` 的退路分支）会把中文读成乱码
    并**直接语法报错**（实测 6 处 parse error），加 BOM 后两种 PowerShell 都能正确解析。
- 验证：以 `-Smoke -Headless` 完整跑通（杀进程 → 编译 → 服务端就绪校验 → 两客户端各自独立目录），
  服务端日志出现 `[LobbyServerHost] 大厅服务端就绪`、`[Lobby] s1（玩家A）创建房间 r1`、
  `卡组通过校验：默认对局卡组（57 张 / 秘社 1 张）`、`s2（玩家B）加入房间 r1`，三个进程无崩溃。
- **给用户的操作提示**：用 `一键联机三开.bat` 重跑即可（现在会自动清理旧实例）；
  若习惯用 **Godot 编辑器 F5** 跑，请先按【停止】(F8) 结束上一次运行——编辑器不会替你杀掉旧实例，
  那同样会跑在旧程序集上。

**87 激活类能力"次数上限"双重计数修复（用户实测：秘社效果第一次发动就被跳过）**
- **症状（用户实测）**：`[Resolve] 触发/激活效果（EffectData）结算: MSJC001_2_SearchUniqueCard` →
  `[Resolve] ⏹ 每局次数上限：MSJC001_2_SearchUniqueCard 本局已发动 1 次（上限 1）→ 跳过本次结算`
  ⇒ 秘社"行动4 检索独有牌"**首次发动就什么都没发生**。
- **根因（双重计数）**：激活类能力的次数上限有**两处**检查/登记——
  ① **发动阶段** `EffectEngine.ActivateAbility`（2.0 / 2.0b 门禁 + 入堆栈前 `MarkAbilityUsedThisTurn/ThisGame`，
     语义＝"发动被接受即计数，与结算成功与否无关"）；
  ② **结算阶段** `TurnManager.ResolveEntry` 的 `ResolveKind.Trigger` 分支（K23/A57 检查+登记，本意只管**触发类**
     "同一批次多条同源触发最多结算到上限"）。
  而**激活条目入堆栈的 `Kind` 也是 `ResolveKind.Trigger`**（`PushActivationToStack`）⇒ 结算时看到"已发动 1 次"
  正是**它自己刚登记的**，`1 ≥ MaxUses*` 成立 ⇒ 整条被跳过。
- **影响面（不止秘社）**：`MaxUsesPerTurn=1` 的 **JC031 唤灵师 / JC032 血族长老 / JC069 警犬** 与 `=2` 的
  **JC093 怨灵咒刃（第二次）**，**一直是"首次/第二次发动被跳过"**；既有 `P3UseLimitTests` 只驱动
  `ActivateAbility` 断言"入堆栈 + 计数"，**从未推进堆栈结算**，所以这个 bug 长期没被发现（A57 才把它照亮）。
- **修法**：`StackEntry` 新增 **`FromActivation`**（`PushActivationToStack` 置 `true`），
  `ResolveEntry` 的 K23/A57 检查+登记**整段对激活条目跳过**；触发类条目照旧（其没有更早的登记点）。
- **顺带修（测试侧真实暴露）**：`TestSacrifice2_JC093每回合两次上限` 原先把"宿主"排在牺牲候选首位——
  牺牲代价在"未指定候选"时**确定性兜底取第一个合法候选** ⇒ 第 1 次发动就牺牲了宿主，怨灵咒刃随宿主进墓地，
  第 2 次会因"来源不在生效区"被拒；旧实现靠"第 2 次先被（错误的）上限拒绝"掩盖了这一点。
  现改为"牺牲品先上场、宿主最后"，并补断言"两次发动后咒刃仍结附（宿主未被牺牲）"。
- 测试：`P3UseLimitTests` 新增 **`TestK23_激活类条目结算不得二次计数`**（激活 → 入堆栈 → 让过结算：
  既要**真的结算**（临时费用 +2），又要计数**仍为 1**）；`CouncilEffectTests` 新增
  **`TestCouncilMSJC001_首次激活即正常结算`**（激活 → 结算 → 检索落地：库顶那张「帷幕守望独有牌」进手牌；
  计数仍为 1；第二次发动被拒）。`CouncilEffectTests` 9→**10 例**、`P3UseLimitTests` 8→**9 例**。
- 账本：全量 headless **138 套件 997 例 全绿 0 失败 0 错误**；Arena / ArenaOl 冒烟 **0 SCRIPT ERROR**。
- **勘误**：A57 里"`UniqueField` 全卡库 0 处赋值、数据待回填"**不成立**（首次 grep 漏检）——实测
  **JC 卡包内已填 31 张**（如 JC010=`费南迪尔`、JC011=`德莫斯提尼`、JC012=`帷幕守望徽记`），
  故秘社"检索【本方】独有牌"在现有数据下**已有真实候选**（实测 `独有;组织:帷幕守望` → 牌库内 10 张命中）。

**86 秘社（MSJC）17 张 .tres 数据落地 ＋ 兜底秘社改为 MSJC009（用户指定）**
- **17 张 `.tres` 全部就位**（`Cards/Data/CouncilCards/JC/MSJC0NN_<拼音>.tres`）：
  命名一律 **3 位编号** + 与既有卡图文件名一致的拼音（`MSJC001_weimushouwang` … `MSJC011_SPTzhixingbu` … `MSJC017_mengyanzuqun`），
  内部 `UID = "MSJC0NN_<拼音>"`、`CardName` 去掉"MSJC0N "前缀。
  字段来源（Card.xml → .tres）：`text`(简体段)→`CardText`、`prop/type`→`SubType`、
  **`prop/colors`(复合串)→`Colors`(`Array[int]`,`·`前的色名映射 `CardColor` 枚举) ＋ `Organization`(`·`后的阵营名)**（原写单一字符串 `ColorText`，
  **2026-10-02 已废弃**：`ColorText` 同步颜色+阵营两个语义，与玩家卡不同名不同构），
  "初始手牌：N"→`StartingHandSize`。
- **起始手牌**：001~008 / 010~015 / 017 = **6**；**016 颂亡者 = 5**（XML 原文，PT 也是 5，保留未改）；
  **009 秘社 = 7**（用户指定："XML 里是 6，测试需要改成 7"）。
- **构筑限制**：001~008 = `FilterByColor + TargetColor=<派系色> + GreaterOrEqual + RequiredCount=25`（黄1/白2/绿3/灰4/紫5/蓝6/黑7/红8）；
  **009 秘社无限制**；**010~017 的 8 张当前无法表达**（模型只有"正向 AND 筛选 + 数量比较"，无法表达"只能包含…＋例外"与"非X非中立≤N"）——
  已在 `.tres` 留空并在《秘社制作交接提示词.md》逐张列出原文与建议模型，未伪造字段。
- **脚本配套（`Cards/Script/CouncilCards/JC/`）**：**001~009 已按卡文实现**（001~008＝同构两条：行动3 先手标志抽1 ＋
  行动4 检索本方独有牌·每局一次；**009＝单条**行动3 先手标志抽1）；**010~017 为占位脚本**（构造函数留空，
  卡文与 FAQ 原文写进 doc 注释，`ActivateWindow/Cost/每局一次/Target=null` 等实现要点写在 TODO 里）。
- **兜底秘社改为 MSJC009**（用户："玩家不带秘社时的兜底秘社改成 MSJC009"）：`GameInitializer.DefaultCouncilPath`、
  `DeckListLoader.FallbackCouncilPath`、`DeckBuilder._workingCouncil` 三处；实局验证
  `起始手牌：座位1 = 7 张 / 座位2 = 7 张`（＝MSJC009 的 7 起手生效）。
- 账本：全量 headless **138 套件 995 例 全绿 0 失败 0 错误**；Arena 冒烟 **0 SCRIPT ERROR**（秘社 UI 显示「秘社」）。
- **交接**：新窗口制作 010~017 的提示词与逐张数据见 **`UWPro/秘社制作交接提示词.md`**。

**85 秘社（MSJC）批次起步：MSJC001 按卡文落地 ＋ 两个新机制（每局游戏一次 / 独有检索）＋ 先手标志谓词（用户裁定）**
- **背景**：用户给出 MSJC001『帷幕守望』的完整卡表信息，要求核对"`.tres` + `.cs` 是否足以表达"，并确认三个已知缺口
  （① 构筑限制接入 ② 秘社检索为暂代 ③ 待做 17 张秘社）。
- **核对结论（本轮）**：`.tres` 静态字段**除 3 处外齐备**——`StartingHandSize` 原先靠默认值 6（已显式写入）、
  `CardText` 是占位文本（已按卡文改写）、`set num`（卡集/编号）**数据模型无对应字段**（不影响对局，暂记）；
  其余 `type/colors/organization` 均有字段且已填。
  `.cs` 结构够用（`ActivationOptions` + `EffectData`，激活窗/费用/横置链路已通），但**两条效果原先都没按卡文实现**。
- **MSJC001 落地（按卡文）**：
  · 行动3横置：**若你具有【先手标志】**则抓一张牌 ⇒ 新谓词 `具有先手标志`；
  · 行动4横置：`Search{独有;组织:帷幕守望 → 手牌, 展示}`（原先暂代＝`DrawFromBottom`）＋ **`MaxUsesPerGame = 1`**；
  · `.tres`：`CardText` 改为卡文原文、`StartingHandSize = 6` 显式声明（构筑限制/组织/PT/关联卡等原已齐备）。
- **新机制 ①：`EffectData.MaxUsesPerGame`（每局游戏一次）**——与 K23 平行：`CardInstance._abilityUsesThisGame`
  （`Get/Mark/Is…LimitReached/Reset/Set`）＋ 门禁四处（`ActivateEligibility.CanActivate` 的通用与秘社分支）＋
  两处权威点（`EffectEngine.ActivateAbility` 代价前、`TurnManager.ResolveEntry` 触发结算前，**均"先检查后登记"**）＋
  `CardManager.ResetAllAbilityUsesThisGame()`（**只在开局**调用：`GameInitializer.StartMatch`）＋
  快照投影往返（`ZoneCardRef.UsedAbilityIdsGame/CountsGame` → `ZoneManager.ProjectCard` → `CardManager` 回写）。
  **口径**：不随回合重置（`DoReset` 只清每回合计数）、离场/回手/换区/版本号变动**都不清零**，只有重开一局恢复。
- **新机制 ②：独有检索**——`DeckSearchFilter` 新增无值标志段 `独有`（＝`PlayerTemplate.UniqueField` 非空，
  用户口径"UniqueField 不为空就为独有卡"）；顺手勘误 `UniqueField` 的错误注释（原写"唯一键（如UID）"，
  实际语义＝**独有卡名/独有标识**，此前全卡库 0 处赋值）。
- **新机制 ③：先手标志谓词**——`EvaluateCondition("具有先手标志")`（别名 `先手标志`），
  基准＝来源控制者是否本回合先手（`PlayerManager.IsFirstPlayer` ← `TurnManager.FirstPlayerId` 派生）。
- **顺带修的实现陷阱**：`EffectData.Target` 默认非 null ⇒ 秘社"无目标能力"忘写 `Target = null` 时
  `CanActivateCouncil` 会走"需要选目标"门禁、该能力**永远不可激活**（本批实测踩到）；已补 `Kind != None` 前置。
- 测试：新增 **`CouncilEffectTests`（9 例）**——结构（含 `StartingHandSize`/构筑限制/两选项费用/筛选串/每局上限）、
  `独有` 与 `独有;组织:X` 的 AND 语义、先手标志谓词正反例、每局上限门禁、
  **不随回合与离场清零**（核心口径）、投影往返。
- **三处已知缺口现状（交接）**：**① 构筑限制**：数据层✓（`CouncilRestriction`/`ValidateDeckDetailed`）、
  大厅✓（`GameDeckValidator`）、单测✓；**缺构筑界面接入**（`Script/UI/DeckBuilder.cs` 内无任何限制校验/展示）。
  **② 秘社检索暂代**：MSJC001 已改真检索；**其余秘社待逐张检查**；且"独有"数据仍需回填（否则 `独有` 恒不命中）。
  **③ 17 张秘社**：`Card.xml` 内 17 张（MSJC01 帷幕守望 / 02 猎魔人 / 03 王座会 / 04 鸣钟教派 / 05 国家机构 /
  06 圣贤 / 07 方碑序列 / 08 梦境行者 / 09 秘社 / 10 貘组 / 11 S．P．T执行部 / 12 爱弗罗德家族 / 13 计时者 /
  14 哨兵 / 15 隐士 / 16 颂亡者 / 17 梦魇族群），每张＝一对 `.tres` + `.cs`，机制层本轮已补齐三项。
- 账本：全量 headless **138 套件 995 例 全绿 0 失败 0 错误**；Arena / ArenaOl 冒烟均 **0 SCRIPT ERROR**。

**84 服务端"异步续算"落地后立即下发快照（用户实测：检索入手后手牌 UI 延迟到"让过"才更新）**
- **症状（用户反馈）**："效果能正常结算，但 UI 刷新有延迟——卡片加入手牌，玩家让过后才能看到手牌。"
- **实测取证**（服务器日志 `logs/server_182545.log`，逐行）：
  `🔍 检索：玩家1 … 候选 5/38` → `🌐 联机决策请求已下发，动作链挂起等待回包（保持原子锁）`
  → `[MatchHost] 下行快照 … 手牌数=13` →（客户端回包后）`检索：逆转异界之门 加入玩家1 手牌` → `牌库已洗牌`
  → **`🟢 玩家1 让过`** → `[MatchHost] 下行快照 … 手牌数=14`。即"牌已入手"与"客户端可见"之间差了整整一次上行。
- **根因**：服务端**只在"意图流程结束"时推快照**（`MatchHost` 订阅 `TurnManager.OnAsyncSettlementCompleted`，
  各 `TurnManager.*Async` 流程末尾逐一 `Invoke`）；而**引擎侧"挂起-续算"**（A55 新接入的网关路径：
  检索/弃牌/封印/可选支付/JC119/逐位牺牲…）与**规则类异步流程**（`LeaderRule`/`PathRule`/`CurbRule`/
  `PredictionRule`）落地时**没有任何人通知** ⇒ 状态变化只能等下一次上行（让过/出牌）才被推送。
- **修法**：新增公开通知口 **`TurnManager.NotifyResolutionSettled()`**（内部 `OnAsyncSettlementCompleted?.Invoke()`），
  在 **`TurnManager.OnEngineChainSettled()`**（引擎续算收尾，见 §7.10 83 ④）末尾调用，并补到四个规则类的
  异步流程末尾（领袖/道途规则牺牲、遏制按地区分配、预测置顶置底）⇒ `MatchHost` 立即 `PushSnapshot()`，
  客户端 `ApplySnapshot` 全量重建 ⇒ 手牌/墓地/牌库即时可见。
- **单机不受影响**：单机 UI 走事件总线（`ZoneManager.FireMoved` → `OnCardMoved`（节点归属）+ `OnPostMove`
  → `SyncDerivedUI` → `ArrangeAllCards`），本次无需改动；`NotifyResolutionSettled` 在单机没有订阅者，空转无害。
  （口径见 `事件总线使用说明.md` §三 收口原则：move 类更新只走 `FireMoved`，调用方不再手写刷新。）
- 测试：`P4OnlineGatewayWiringTests` 新增一例——**挂起期间不上报、回包落地后恰好上报一次**"结算完成"
  （锁定的正是"MatchHost 据此立即下发快照"这一契约）；该套件 13→**14 例**。
- 账本：全量 headless **137 套件 986 例 全绿 0 失败 0 错误**；Arena / ArenaOl 冒烟均 **0 SCRIPT ERROR**。

**83 联机"需要玩家选择"的效果全部接上网关（用户裁定"这一批全补齐"，无新增效果行）**
- **症状（用户实测）**：服务端日志 `[EffectEngine] ⚠ Search 无 UI：确定性兜底取第一张匹配（深渊细语）`
  ⇒ 玩家没有收到任何选牌请求，牌是服务端**替他抽的**。
- **根因（架构级，一处断链）**：生产链路 = `TurnManager.ResolveStackTop`（同步）→ `ResolveEntry` →
  **同步** `EffectEngine.ResolveEffect`；而网关分支只写在 `ResolveEffectAsync` / `ExecuteVerbAsync` /
  `*ViaGatewayAsync` 那套"可挂起续算"实现里，**全项目调用 `ResolveEffectAsync` 的只有测试** ⇒
  服务端（`UIHooks == null` 且 `InputGateway != null`）下，所有"要玩家选择"的效果都落到"无 UI 确定性兜底"。
  MatchHost 此前已注入 `EffectEngine.InputGateway`（P0 修复），但同步核心从不读它 ⇒ 注入等于没注。
- **① 同步执行体接入网关**（`EffectEngine.ExecuteVerb`）：`Inspect`/`PayOptional`/`PrepareUpkeep`/`Discard`/
  `Seal(手牌)`/`RevealSealedAndQueueFreePlay`/`Search` 七个"需要玩家选择"的动词，在**无 UI 有网关**时改走既有
  异步执行体，并用 `SuspendChainForInput()`/`ContinuePendingChain()` 做**动作链挂起-续算**（不阻塞线程）。
  **有 `UIHooks` 时仍优先本机窗口**（单机行为零变化；两者同时存在的测试装配也不受影响）。
- **② 补齐原本没有网关分支的 5 处**：
  · **JC083「选择一个数字」（`ValueExpr=玩家选择`）**：原先服务端**整条效果不发动**（fail-closed）→ 新增
    `AskPlayerValueViaGatewayAsync`（`Kind.NumberInput`），回包写回选择值并**重入结算**；
  · **JC064 `PayOrSacrificeSelf`**、**JC114 `SacrificeSelfOptional`**、**JC128 `BribeOrSacrifice`**：
    统一走 `AskZeroOrOneViaGatewayAsync`（0/1 二选一，`OptionLabels` 为两个按钮文案；取消/超时归"否"）；
  · **`SacrificeChoice`（JC046/JC113/JC134/JC065 每位玩家各牺牲一个）**：新增
    `CollectSacrificePicksViaGatewayAsync`（逐位下发 `Kind.SelectTarget`、顺序＝先手优先、回包权威复检、
    非法/取消兜底第一候选），全部收齐后统一落地。
- **③ 挂起-续算的正确性**：新收口 `EffectEngine.BeginGatewayFlow(flow)` 兼容两种回包时机——
  **尚未回包** ⇒ 记录续算点、**显式 `TurnManager.EnterAtomic()`**（服务端没有 View 帮忙 `NotifyModalOpened`，
  否则等待窗口内对手可行动）、回包后 `ContinuePendingChain()`；**已同步回包**（测试桩/自动网关）⇒ 清掉挂起登记、
  就地继续剩余动作。续算跑完且链上不再等待输入时，经新 **`TurnManager.OnEngineChainSettled()`** 补做
  "地区赢取检查 / 解原子锁 / JC119 免费打出队列"（挂起时 `ResolveStackTop` 的同步段已提前 return）。
  `SuspendChainForInput()` 增护栏：**不覆盖已记录续算点**（回调里再发起决策时不再丢外层剩余动作）。
- **④ 顺带修复（单机真 bug）**：`BribeOrSacrifice`（JC128）原先**未挂起动作链**——窗口是回调式异步，代码却在
  同一帧按"走牺牲路线"分叉 ⇒ 玩家在窗口里的选择被丢弃（旧单测用同步桩，长期掩盖）。现改为"落地逻辑放进回调/
  回包"，并 `SuspendChainForInput()`。
- 测试：新增 **`P4OnlineGatewayWiringTests`（13 例）**——统一按**生产形状**（同步 `ResolveEffect` +
  网关注入 + 无 UI）驱动，逐项锁定：检索/弃牌/封印确实下发请求、**挂起期间原子锁保持且回包后续算后续动作**
  （K11 支付 → 第二条动作执行）、越权回包兜底、JC083 数字选择重入结算、三处 0/1 二选一、JC128 二段请求、
  逐位牺牲选择两次请求、**有 UI 时不走网关**、无 UI 无网关仍走确定性兜底。
- 账本：全量 headless **137 套件 985 例 全绿 0 失败 0 错误**；Arena / ArenaOl 冒烟均 **0 SCRIPT ERROR**。
- **留档（待用户裁定）**：那套"可挂起续算"的并行实现（`ResolveEffectAsync`，缺 K15 多目标 / K16 模式 /
  K1 玩家选择 / `_currentEffectData` 设置）现仅被测试调用，且已成事实上的"死代码孪生"——建议标注为
  **测试专用**并逐步把测试驱动切到同步入口，最终删除（避免以后又被误当生产入口）。

**82 秘社重放幂等（修既有 2 例失败）＋ 联机"秘社目标"可点（用户裁定"现在就修／补上"）**
- **① 根因**：`GameInitializer.DealOpeningHands()` 内 `ReloadDecks(); SetupCouncils();` 会**再建一次秘社**
  （目的是读到本局最终卡组携带的秘社），而 `ZoneManager.MoveToCouncil` 只"加入"不移除 ⇒ 每位玩家 CouncilZone
  里累积 **2 张秘社**（同模板）：`GetCouncilHandSize` 读到先建的旧卡 ⇒ 起手数回退默认 6（`TestOpeningHand_按秘社起手数发牌` 失败）；
  "结附于本方秘社"类效果出现重复候选（JC051 宿主选择凭空多一个）。
  **修法**：`SetupCouncils` 开头 `ZoneManager.ClearCouncilZone(pid)`（新方法）——旧实例按 `CardZone.Exiled`
  移出游戏 + 广播 `OnCardMoved`（View 据此摘除旧秘社 UI）+ 注销持续效果登记；再按本次卡组重建 ⇒ **恒 1 张**。
- **② 秘社 UI 生命周期（配套，否则"数据对了但看不见牌"）**：`Arena.Layout.SyncCardUIToZone` 秘社判定放宽为
  "当前在秘社区 **或** `_councilCardUIs` 里曾有它"；`SyncCouncilCardUI` 三态化：不在秘社区→摘除、在秘社区但无 UI→
  **补建**（重放后的新实例原先只打"未找到"日志）、已有 UI→确保容器正确；移除/补建后各重排一次。
- **③ 联机"场上选目标"补秘社通道**（用户裁定"补上"）：`OpenOnlineTargetSelectionWindow` 一并高亮
  `_councilCardUIMap` 中的合法目标；`PickLegalActivationTargetAt` 把秘社纳入同一 ZIndex 命中比较
  （新增 `CouncilCardUI.GetVisualGlobalRect()`，与 `CardUI` 同款"旋转后 AABB"）；
  `ClearActivationTargetSelection` 同款清理；`RebuildCouncils` 先清旧登记防残留；提示文案改为"点击场上卡**或秘社**"。
- **④ 两处顺手修**：`Arena.PromptPickCard` 候选文案兜底 `CouncilTemplate?.CardName`（原先秘社候选显示原始 UID）；
  `P5FlowOptimizationTests.MakePlayableCard` 补 `_effectsInjected = true` —— 否则 `CardEffectFactory.EnsureEffects`
  会清空 `EffectDataList` 重建、把探针的 `PlayTiming.Response` 抹掉（静默退化成 A 档），这正是既有失败
  `TestPriority_后手行动中先手出牌优先权归后手` 的**真因**（该用例同时改为与显式优先权状态机一致的"先 Pass 再出牌"）。
- 测试：新增 `TestCouncilSetup_开局后每位玩家恰一张秘社` / `TestCouncilSetup_重复重放秘社不累积`（`P5FlowOptimizationTests`）；
  上述 2 例既有失败**转绿**。
- 账本：全量 headless **136 套件 972 例 全绿 0 失败 0 错误**（**本工作树首次全绿**）；Arena / ArenaOl 冒烟均 0 SCRIPT ERROR。

**81 检索改强制（不可取消）＋ JC051 宿主改由玩家选（用户裁定 A53，无新增效果行）**
- **① 检索一律强制**（用户："**收紧为强制：单机也去掉取消，与卡文和联机一致**"）——已逐张核对 8 张 JC 检索卡
  （JC007/025/026/032/033/051/109/131）的卡文**都没有"你可以寻找"**：
  · 单机：`EffectEngine.ResolveSearch` → `PromptPickCard(..., allowCancel: false)`（窗口 `ForceChoice`，无"取消"）；
  · 联机：`SearchViaGatewayAsync` → `ChoiceRequest.ForceChoice = true`；回包**取消/非法**不再等于"不取牌"
    （**原口径"玩家放弃 = 检索未发生 → 不洗牌"作废**）⇒ **fail-safe 兜底取第一张匹配**再照常落地 + 洗牌；
    **安全属性不变**：候选永远只来自"检索者牌库 ∧ 通过筛选"（越权 UID 仍取不走）。
  · 留档：将来若出现"你可以寻找…"的卡，需先在 `Args` 加可选标记，再恢复取消分支。
- **② JC051『结附于合理的目标』的宿主改由玩家选择**（用户："**改成开窗选宿主（单机 PromptPickCard + 联机 SelectTarget）**"）
  ——**撤销 §7.4b 第 5 条**（2026-09 早期口径"不再由施术者选、按被检索卡自身 TargetRules 自动取第一个"）：
  · 候选来源**不变**：`EffectEngine.AttachSearchedCardByOwnRules` / `CollectOwnRuleHosts`
    （＝被检索卡自身 `TargetRules` 的合法宿主；判定复用打出阶段同一实现 `CardManager.MatchesRule`）；
  · **候选 ≥2 → 由玩家选**：单机 `IGameUIHooks.PromptPickCard`（`allowCancel:false`）；
    联机新增 `AttachSearchedCardByOwnRulesViaGatewayAsync`（`Kind.SelectTarget`，`OptionUids` + 服务端 `OptionLabels`，
    `ForceChoice=true`，**回包权威复检必须命中候选**）；回包非法/取消 → **确定性兜底取第一个合法宿主**（fail-safe）；
  · **候选 = 1 → 不弹窗**（直接结附，无可决策空间）；**无 UI（headless/测试）→ 确定性兜底取第一个**；
  · 去向落地改为**续算式**：`ApplySearchDestination(card, dest, extra, source, target, targetAreaId, onDone)`
    ——选宿主窗挂起期间**不洗牌**，宿主落定后才"公开展示 + 洗牌"（单机 `ResolveSearch` 的 `finish` 续算）；
  · 卡数据：`JC051_nuoennagesita.cs` 的 `Args[2]` 由 `"自动"` 改为 **`"按目标规则"`**（语义明确）。
- 测试：`JC051Tests` 6→**10 例**（新增：单机多宿主由玩家选／单机唯一宿主不弹窗／联机经网关 `SelectTarget` 选／
  联机非法回包兜底第一个）；`P2SealSearchShuffleTests`"联机放弃取牌不洗牌"改写为
  "**强制检索：回包取消 → 兜底取第一张并洗牌**"；`P4OnlineGuardTests` 越权回包用例改写为
  "越权牌取不走 ＋ **兜底取第一张合法候选**"。
- 账本：**效果行数不变**（280 = 260 / 1 / 0 / 19）；全量 headless **136 套件 970 例**：本批新增/改写用例**全绿**，
  余 **2 例为改动前既有失败**（`TestPriority_后手行动中先手出牌优先权归后手` / `TestOpeningHand_按秘社起手数发牌`；
  证据＝改动前日志 `test_out.txt`、`_run.log` 同为"失败 2"）→ **均已在下一条（82）定位并修好**；Arena 场景 headless 冒烟 **0 SCRIPT ERROR**。
- **顺带发现（本批未改，待用户裁定 → 已在 82 落地）**：`GameInitializer.DealOpeningHands()` 内 `ReloadDecks(); SetupCouncils();`
  会**再建一次秘社**，而 `ZoneManager.MoveToCouncil` 只"加入"不移除 ⇒ 每位玩家 CouncilZone 里**2 张秘社**（同模板）。
  这正好解释上面 2 例既有失败（`GetCouncilHandSize` 读到先建的旧卡 ⇒ 回退默认 6），并使"结附于本方秘社"类徽章
  在 JC051 里出现**2 个候选**（正常应为 1）。另：联机"场上选目标"窗 `OpenOnlineTargetSelectionWindow` 只高亮
  `_cardUIMap`，而**秘社在 `_councilCardUIMap`** ⇒ 候选含秘社时联机端点不到，只能等超时后走兜底
  （单机走 `PromptPickCard` 列表窗，不受影响）。

**80 现身校验额外费用（K10 扩展）＋ 延后项裁定 ＋ 清理（用户裁定，无新增效果行）**
- **① 现身必须校验额外费用**：用户裁定"**没有特殊说明的情况下需要校验额外费用，JC 卡包内是都需要校验**"。
  把 `PlayCard` 的额外费用解析抽成 **`TurnManager.TryResolveExtraCost`（单一实现，打出与现身共用）**，
  `RevealCovert(playerId, cardUid, extraSacrificeUid, extraRevealUids)` 复用同一份校验/兜底/减免：
  · `RevealHandCards`（JC081，可选）→ 现身时展示手牌**同样减免本次现身费用**；候选须在手牌且**不含本卡自身**（防自我减费）；
  · `SacrificeCharacter`（必须支付）→ 候选须合法，无候选 → 拒绝现身；
  · `PayTargetCost`（必须支付、需先选目标）→ 现身路径无目标交互 ⇒ **拒绝现身**（fail-closed）。
  单机交互＝与打出 **1:1** 同款窗口（`ShowRevealChoiceStepGeneric` 回调参数化＝展示多选窗共用；牺牲用同款候选按钮窗；
  统一入口 `Arena.RevealCovertWithExtraCost`）。联机＝服务端接管 `RevealCovertWithExtraCostAsync`
  （`SelectHandCards` 多选 / `SelectTarget` 单选，回包权威复检，取消即不现身）。
- **② 延后项裁定（附实测证据）**：`GenerateToken/Investigate/Strength/Power/Win/Lose` → **JC 卡包 0 处、全卡池 0 处**；
  `TargetRestriction.CombatCapable` → **全卡池 0 处**；`ResolveKind.NewArea` 预留的"任务/地区卡"→ **全卡池无该类别**
  （CategoryFlag 8/16/32 零命中）⇒ 三项**全部延后**（现状保留：六动词显式报错、CombatCapable 判不通过、NewArea 仅打印）。
- **③ 清理**：删除 6 个 0 字节 .cs（0 引用）；修正 6 处过期注释；给 `联机对战完整实现最终缺口清单.md` 加历史快照横幅。
- 测试 `P4RevealExtraCostTests`(9)：JC081 展示减免 / 不展示全额 / **非法展示候选拒绝** / `PayTargetCost` 拒绝现身 /
  牺牲按所选落地 / 无牺牲对象拒绝现身 / 无额外费用行为不变 / **联机接管（`SelectHandCards`，候选不含本卡自身）** /
  无额外费用不接管。全量 headless **134 套件 939 例 全绿 0 失败 0 错误**。

**79 联机"激活 + 地区目标"与"激活代价选择"——按单机那两套窗 1:1 复刻（用户裁定）**
- 单机形态：View 侧先弹窗收集"目标地区 / 牺牲角色 / 标志代价（固定数量 or 逐卡分配）/ 目标玩家"，
  再把结果作为参数交给 `PlayerActionService.ActivateAbility`（`Arena.BeginActivateAreaSelection` /
  `BeginActivateWithSacrifice` / `BeginActivateWithCounterCostChoice` / `BeginActivateWithCounterCostAllocation` /
  `BeginActivateWithPlayerTarget`）。
- 联机落地（**服务端接管**，与 K10/K15/K16 同款）：
  `GodotIntentRouter.ActivateAbility` → `PlayerActionService.TryBeginActivationChoiceOnline`
  → `TurnManager.ActivateAbilityWithChoicesAsync`；**顺序与单机 `BeginActivate` 一致**
  （目标地区 → 牺牲 → 标志 → 玩家目标）；候选由**服务端**用同源规则函数算出
  （`EffectEngine.GetLegalAreaTargets` / `GetSacrificeCandidates` / `GetCounterCostCandidates` /
  `PlayerTargetPool.Collect`），随 `ChoiceRequest` 下发；回包**权威复检**，取消/超时/非法 → **整次激活不发动**。
  - **目标地区（JC117）**：`Kind.SelectTarget`（候选＝合法地区；客户端走既有"地区槽位高亮 + 点击"通道）；
    入堆叠锁定 `StackEntry.TargetAreaId`。此前 `ArenaOl.SendActivation` 对 `TargetKind.Area` 直接 `return`
    （"本轮暂缓"）⇒ JC117 联机不可用。
  - **代价选择（牺牲 / 标志）**：新 `Kind.SelectActivationCost` ＋ 客户端
    **`OnlineActivationChoiceWindow`**（单机 `ActivationChoiceWindow` 的联机孪生，同一 `ActivationChoiceItem` 列表）：
    单张模式＝一张卡一个按钮；**逐卡分配模式**（JC055②）＝**1:1 复刻单机 `ShowCounterAllocationStep`**
    （点一次记录 1 点、显示"已选 X / 共 N"、满额置灰、总量 ≥1 才可确认、可重置/取消）；
    模式判据＝是否下发 `OptionMaxAmounts`；服务端按该卡现有标志数**权威钳制**。
  - **目标玩家（K24 联机激活路径）**：服务端下发 `Kind.SelectPlayer`（此前客户端对该类能力取不到卡目标而静默失败）。
  - 成功判据＝"**是否真的入堆叠**"（引擎门禁/代价校验失败 → 返回 false，不谎报成功）。
- 测试 `P4OnlineActivationTests`(7)。全量 headless **133 套件 930 例 全绿 0 失败 0 错误**；
  Arena 场景 headless 冒烟 **0 SCRIPT ERROR**。
- **仍未做**：联机 JC057 授权后对手暗藏者**仍无卡面**（客户端只有隐藏占位，需按需拉卡面）；`OnCounter` 触点未接。

**78 审计修复批次：P0/P1/P2 缺陷全清 ＋「暗藏者持续/静止失效」→ 跨卡/引擎级（无新增效果行）**
- 背景：用户要求"检查是否还有暂代逻辑、是否大多数效果只有角色在场上且正面才能触发"。三路只读审计 + 我方逐条复核后，
  用户裁定 **"P0 + P1 + P2 全清"** 且 **"暗藏者的持续/静止效果一并失效"**。本批只修缺陷、不新增效果行。
- **P0-A 联机决策接线**：`MatchHost.StartServing` 少注入 `EffectEngine.InputGateway`
  （唯一会注入引擎的 `GameInitializer.SetInputGateway` **在生产路径从未被调用**）⇒ 联机时引擎侧全部"玩家选择"
  退化为服务端确定性兜底（检索取第一张、弃牌弃第一张…）。修：补注入 + `StopServing` 对称清除；
  回归锁 `MatchIsolationTests.TestMatchHostInjectsGatewayIntoEffectEngine`。
- **P0-B 牺牲选择在单机也会代选**：`ResolveSacrificeChoice` 发起异步选卡窗后同帧 `chosen ??= candidates[0]` 并
  `ResolveDeath`（同类 5 处输入里唯一没挂起链的一处）⇒ 玩家点击无效。修：回调递归收集 + `SuspendChainForInput` /
  `ContinuePendingChain`；新增**异步窗口桩**回归（未点击不牺牲 / 按点击候选 / 全部收齐才落地 / 动作链续算）。
- **P1-A 秘社激活生效区**：新增 `ZoneScope.CouncilZone` ＋ `GetDefaultScope` 秘社分支 ＋ `ScopeMatchesZone` 分支
  （原 `Specified` ⇒ 恒 false ⇒ `ActivateAbility` 无条件拒绝秘社激活，而候选侧放行 ⇒ "能点、点完没反应"）；
  回归锁为生产路径端到端（已实测去掉分支即失败）。
- **P1-B「进入一个地区」时机**：JC061/JC087 由 `ManeuverTrigger`/`EnterTrigger` 统一改挂 **`OnMove`**，
  并把 `OnMove` 加入 `TriggerRegistry`"只对事件主体卡成立"的白名单（防误触发）；新增"移动后针对新地区"生产路径用例。
- **用户裁定·暗藏者持续/静止失效**：`RegisterPersistent` 暗藏时跳过 ＋ `GameInitializer` 的 `CovertChanged` 钩子
  注销/重新注册（潜伏即失效、现身即恢复）＋ 两个读印刷规则的判定点补 `IsCovert` 门禁；
  新增 `P4CovertContinuousTests`(5)。
- **P2**：联机回包权威复检（检索/弃牌/封印/触发选目标/选玩家目标）；选择窗显示名随请求下发
  （`OptionLabels`，解决"隐藏占位卡看不到卡名却必须选"）；动词 fall-through 拆开（`GenerateToken/Investigate/
  Strength/Power` 不再落 `Attach` 执行体）；两个恒真谓词改真实判定（`成功封印` / `心灵领域忠诚≥2`）；
  `RecheckTargetProperties` 经核对被第③段 `TargetValidity.Check` 全覆盖 ⇒ 改为显式说明（禁止第二套判定）；
  JC119 两处联机断链补网关分支（展示封印牌选一张 / 免费打出的目标请求）。
- **仍未做**：联机"激活 + 地区目标"（JC117）与"激活代价选择"（牺牲/标志/地区）需**联机侧新 UI**（待口径）；
  联机 JC057 授权后对手暗藏者仍无卡面（需按需拉卡面）；`OnCounter` 触点未接。
- 测试：新增 `P4CovertContinuousTests`(5) / `P4OnlineGuardTests`(3)，扩写 `MatchIsolationTests`(+1) /
  `P3MultiSacrificeTests`(+2 异步窗口) / `CouncilActivateWindowTests`(+1 生产路径) /
  `P3OptionalPayTests`(+1 移动后触发)；全量 headless **132 套件 923 例 全绿 0 失败 0 错误**。

**77 持续可见性授权 ＋ 鼠标悬浮预览 UI → JC057 监控员②（⛔ 未实现仅剩"用户明确暂不做"的 JC125）**
- 卡文：「持续：**只要监控员未横置，你可以随时检视本地区的任一暗藏者**。」
- 用户口径（本轮答复）：本条需要**一个新的 UI 改动**——**鼠标悬浮在卡上弹出悬浮 UI、显示整张卡图**；
  四条可见性：**①所有可见卡悬浮都弹** / **②自己的暗藏者也按同样方式弹** /
  **③正常情况下对手的暗藏者与手牌不弹** / **④若 JC057 在场，悬浮它所在地区的敌方暗藏者可弹**。
- 实现：
  - **模型层·持续可见性授权**：新静止规则 **`StaticKind.AllowInspectLocalCovert`**，
    JC057 声明 `Timing=Static` ＋ `Scope=AreaZone` ＋ `PrereqSpec{Kind=SelfUntapped}`（"只要监控员未横置"）
    ＋ `StaticEffects{Kind=AllowInspectLocalCovert}`；横置即失效、重置即恢复。
    "随时检视"是**不经过效果结算的查看通道**（一次性弹窗表达不了"随时"），故判定做成**静态纯函数**。
  - **判定单一真相源**：新 `Script/Manager/CovertInspectRule`：
    · `CanViewerSeeCardFace(卡, 查看者, CardManager)`：自己的卡（**含手牌与暗藏者**）→ 可见；
      公开卡（场上正面/附属/秘社/资产/墓地/地区）→ 可见；**对手手牌** → 不可见；
      **对手暗藏者** → 不可见，**仅当**查看者控制着**未横置**且**与被检视暗藏者同地区**的 JC057 类卡时授权；
    · `IsInspectAuthorized` / `HasPrintedLocalCovertInspect`：读**卡面印刷规则**＋实例态
      （与 JC081 的 `GetAttachSubCategoryWhitelist` 同款思路）⇒ 单机/联机客户端/服务端同解、不依赖引擎实例；
    · 边界 fail-closed：空卡 / 查看者≤0 / 无 CardManager / 来源无地区 → 不授权。
  - **UI 层**：新 `Script/UI/CardHoverPreview.cs`（悬浮浮层：整张卡图 260×364、鼠标穿透、
    贴边自动换侧并钳制视口内、底部显示卡名）＋ 新 `Script/UI/CardPreviewBus.cs`
    （把散布在战场/手牌/牌库/堆叠/调查窗等多处实例化的 `CardUI` 悬浮**统一收口到一个注入点**：
    `CardUI.MouseEntered/Exited` → 总线 → 按视图注入的判定显示/隐藏；未装配＝静默忽略，无副作用）；
    `Arena`（查看者＝本机人类座位玩家1，单机为本地同桌视图）与 `ArenaOl`（查看者＝`_localPlayerId`）
    均在 `_Ready` 装配、`_ExitTree` 反装配（`CardPreviewBus.Detach`）。
- **已知联机缺口（本批未做）**：联机客户端**不持有隐藏卡的模板/卡图**（服务端只下发占位 `IsHidden`）⇒
  即便 JC057 授权成立，**对手暗藏者仍无卡面可展示**。要真正支持需按需向服务端请求卡面
  （一次性 `ChoiceRequest.Kind.InspectCards` 回环，或快照按"授权玩家"额外下发该卡模板）。
- 测试 `P4CovertInspectTests`(10)：见进度表批次注记。全量 headless **130 套件 910 例 全绿 0 失败 0 错误**；
  Arena 场景 headless 冒烟（`--quit-after 200`）**0 SCRIPT ERROR**。
- ⚠ **人工检查重点**：悬浮预览是**纯 UI**，headless 覆盖不到"鼠标悬浮"的真实表现，请在游戏内验证四条可见性。

**76 构筑级（卡组级）全局触发 ＋ 死亡瞬间计数器快照 → JC124 尼古拉斯·勒梅（JC 卡全卡接入）**
- 卡文：「行动3：在目标角色上放置一个哲人石标志。**强制触发：当具有哲人石标志的角色死亡时，
  将其从墓地移回场上，并由其拥有者操控。该能力在尼古拉斯·勒梅离场后仍然生效。**」
- 用户口径（本轮答复）：**"JC124 需要制作一个有效果的指示物，或者说一个全局触发效果，
  只要 JC124 一开始加入玩家卡组就能触发这个效果。两种方法都可以只要能以合理形式满足 JC124 效果使用即可
  （注意加入多个 JC124 也只能有一个效果生效）效果加入堆叠用 JC124 的卡图"** ＋ **回死亡时所在地区**、**按玩家各自登记**。
- 实现（**全局触发路线**）：
  - 新字段 **`EffectData.DeckLevelRegistration`**（"只要在开局卡组内即生效"的效果声明）
    ＋ 新规则类 **`Script/Manager/DeckLevelTriggerRule`**：
    · **登记**：`DeckManager.InitFromDeckList` / `InitTestDecks` 装载卡组后调用 `RegisterFromDeck`，
      扫描该玩家卡组，命中带该声明的牌即**按玩家登记一次**（**先到先得 ⇒ 多张同名只生效一份**）；
    · **持久**：登记只依赖"开局卡组里有没有"，与那张牌此后身处何处无关 ⇒ 卡文"离场后仍然生效"天然成立
      （刻意**不**走持续效果登记：那条路会在来源离场时被 `UnregisterPersistent` 撤销——正是原 ⛔ 行记的缺口）；
    · **收集**：`TriggerRegistry.DeckLevelCollector`（装配层注入 `DeckLevelTriggerRule.Collect`）在**常规场上扫描之后**
      合成触发实例，**来源卡＝该玩家卡组里的那张实例** ⇒ **堆叠条目用 JC124 的卡图**；
      随后走与普通触发**完全相同**的管线（排序窗口 → 入堆叠 → 让过 → 结算）；
      带该声明的效果在 `TriggerRegistry` 的**场上扫描**里被**跳过**（否则那张牌在场时重复收集，同一死亡角色被回场两次）；
  - **条件**＝新谓词 **`DiedWithCounter`**（`Param`＝标志名）读**死亡瞬间的计数器快照**：
    新 `CardInstance.CountersAtDeath` ＋ `SnapshotCountersAtDeath()`，由 `CardManager.ResolveDeath`
    在**广播死亡事件之前**抓取——**死亡触发的结算晚于"进墓地"**，而进墓地 `ResetToBaseState()` 会清空计数器
    （没有快照则条件在结算复检时必然不成立；快照每次死亡重写，不会残留上一次的判定）；
  - **动作** `MoveToZone{"目标","争夺区","正面","拥有者"}`：把触发上下文卡（刚死亡的角色）移回**死亡时所在地区**——
    `PushTriggerToStack` 现把 **"触发时所在地区"** 写入 `StackEntry.TargetAreaId`（原先只有机动等显式取地区目标才有值），
    结算时作为 `ResolveEffect(targetAreaId)` 下发；新增 **`Args[3]="拥有者"`** ⇒ 回场后**控制者交回拥有者**
    （走 `ZoneManager.ChangeController`，卡文"并由其拥有者操控"）；
  - **「行动3」**：`CharacterActivated` 费用 3 ＋ `PlaceCounter{哲人石标志}`（卡文只写"目标角色" ⇒ 不含暗藏者）。
- **口径判断（已登记 A44，待用户确认）**：卡文**未限定"本方/敌方"**，行动3 的目标本身也不限本方
  （可把标志放在对手角色上），"由其拥有者操控"这句正是为跨玩家场合准备的 ⇒ 按**字面口径**实现：
  **任意玩家**的具标志角色死亡都回场、回场后归**其拥有者**；双方都登记时（镜像对局）第二份登记为**幂等空操作**。
- 测试 `P4PhilosopherStoneTests`(13)：结构 / 从卡组登记（三张同名只生效一份）/ 不在卡组不登记 /
  **端到端回场（死亡时所在地区 + 归拥有者）** / 无标志不回场 / 标志被移除后死亡不回场 /
  **不依赖本体在场（牌库中 / 本体被消灭后仍生效）** / 任意玩家的标志角色都回场 / 双方登记时单次回场 /
  **交回拥有者操控（控制者≠拥有者）** / **堆叠来源＝登记的 JC124 实例（卡图）** / 行动3 放置标志 / 死亡快照读取。
- 账本：**卡接入数 134→135（JC 全卡 135/135 接入完成）**、行 ✅258→**259**、⛔ 3→**2**（仅剩 JC057 / JC125）、
  FAQ 备注 18→**19**；全量 headless **129 套件 900 例 全绿 0 失败 0 错误**。

**75 K10 额外费用的「联机缺口」闭合 → JC049① 深渊细语 / JC081 亚洛尔（并新增 `Kind.SelectHandCards`）**
- 缺口：两类"必须由玩家本人做选择"的额外费用在**联机侧无路可走**——客户端只发裸 `PlayCard` 意图
  （`PlayerIntent.Args` 仅 cardUid/targetUid/targetCardUid），选择既不能在客户端产生、也不能随意图携带；
  服务端又无 View（`IGameUIHooks` 为 null）⇒ JC049 落"未指定候选"的确定性兜底、JC081 落"不展示"的全额支付。
- 实现（**复用既有联机接管机制**，与 K15 多目标 / K16 模式分支完全同款）：
  `GodotIntentRouter.PlayCard` → `PlayerActionService.TryBeginExtraCostPlayOnline` → `TurnManager.PlayWithExtraCostAsync`：
  - **JC049「牺牲一个角色」**：服务端权威枚举候选 → 下发 **`ChoiceRequest.Kind.SelectTarget`**
    （**A33 明确"复用该请求类型、非新窗口类型"**；客户端走既有"场上高亮 + 点选"，**零客户端改动**）→
    回包必须命中候选（**服务端权威复检**）→ 再 `PlayCard(..., extraSacrificeUid)`；**无合法牺牲对象 → 不发请求且不能打出**（A21）；
  - **JC081「展示任意数量的手牌」**：新增请求类型 **`ChoiceRequest.Kind.SelectHandCards`**（多选）＋
    客户端面板 `ChoicePromptPanel.BuildHandCards`（勾选 + 确认/取消）＋超时档 **60s**（`NetworkInputGateway.TimeoutSecondsFor`）；
    `OptionLabels`＝**服务端给出的**"（合格 -1）/（不合格）"标记（客户端不重算规则）；`NumberMax`＝最多可选张数；
    回包 `Confirmed=true` ＋ `SelectedUids`（**可空**＝不展示、全额支付）；**取消＝不打出、不扣任何费用**（A23）；
  - 两类流程都**持原子锁**收集选择、**在 `PlayCard` 之前释放**（`PlayCard` 自己会 `EnterAtomic`），
    完成后 `OnAsyncSettlementCompleted`（联机据此下发最新快照）；回包一律**服务端权威复检**（非法项丢弃/越权整体中止）。
- **口径补充（见 §7.4b A42）**：展示候选**排除正在打出的本卡自身**（卡文"手牌"指手牌中**其它**的牌；
  JC081 自身组织就是【圣贤】，否则每打一张都白减 1）——单机/联机**同一实现**
  `GetExtraCostRevealCandidates(playerId, source)`，付费阶段校验亦忽略本卡自身 UID。
- 测试 `P4ExtraCostTests`(14→**23**)：见进度表批次注记。全量 headless **128 套件 887 例 全绿 0 失败 0 错误**。

**74 JC119 免费打出的「目标选择窗」接入（K28 补齐）→ JC119 守秘人②（并新增 `IGameUIHooks.PromptPlayTarget`）**
- 背景：上一批把"免费打出"做成了走正常打出流程，但**需要选目标**的封印牌**被跳过**（效果内没有目标交互入口）——
  这类牌的打出**不是玩家拖拽发起的**，没有"拖到目标上"这一交互可用。
- 实现：
  - 新 UI 钩子 **`IGameUIHooks.PromptPlayTarget(chooserPlayerId, prompt, isArea, candidateUids, onPicked)`**；
    生产实现 **`Arena.PromptPlayTarget`** 复用激活效果的目标选择通道
    （`TriggerInteractionCoordinator.RequestActivateTargetSelection`：卡目标＝场上高亮 + 点击 / 地区目标＝地区槽高亮 + 点击，
    同一份 `TargetSelectionWindow` + 同一份卡高光总线）→ **不新增第二套选目标 UI**；
    View 只收集选择，实际打出仍由 `TurnManager.PlayCard` 执行（View 不写 Model）；窗口不可用 → 回调 null（不打出）。
  - **该方法带默认实现**（接口中唯一一个）：`IGameUIHooks` 既有 8 个方法皆为纯抽象，新增抽象成员会让 20+ 处测试桩
    全量编译失败；而"未实现"的语义是明确 fail-closed（回调 null＝不打这张牌），故默认体＝**打错误日志 + 回调 null**，
    既不动测试桩、也绝不静默代打。
  - `TurnManager.ProcessQueuedFreePlays` → 需选目标的牌改走 **`BeginFreePlayTargetChoice`**：
    · 候选＝**`TurnManager.GetLegalPlayTargetUids`**（候选池：争夺区 + 附属区 + 秘社 + 双方资产区 + 双方墓地；
      判定＝`CardManager.IsLegalPlayTarget`）——**与 `LegalityProjection` 同口径，不新增第二套判定**；
    · 地区目标模式＝卡声明可打出目标含 `Area`（`PlayerCardData.GetAllowedTargets()`），候选＝全部地区
      （与 `PlayCard` 对地区目标自身的权威校验"地区存在"同口径）；
    · 决策路径：单机 `UIHooks` → 弹窗；**联机 `InputGateway` → 本批不代打**（留在手牌并告警）；两者皆无 → 确定性兜底取首候选；
      无任何候选 → 留手牌并告警（fail-closed）；
    · **取消后继续处理队列**；**成功打出后不再递归处理**（新条目已入堆叠，其结算结束时 `ResolveStackTop` 会再次进入）。
  - 顺带：`ProcessQueuedFreePlays` 提为 **public**（仅供测试/调试驱动，队列本身只在结算期登记）。
- 测试 `P4KeeperTests`(5→**10**)：卡目标模式（弹窗候选＝唯一合法角色 → 选中 → 9 费免费 → 入堆叠 → 结算后结附所选宿主）/
  地区目标模式（`isArea=true` → 落点＝所选地区）/ 取消 → 留手牌且未入堆叠并出队 / 无合法目标 → 不弹窗留手牌 /
  无 UI → 确定性兜底取首候选并打出。全量 headless **128 套件 878 例 全绿 0 失败 0 错误**。
- **已补齐（2026-09，见 §7.10 78）**：**联机**路径的 JC119 免费打出目标请求（`SelectTarget` 回环）与
  「展示封印牌 → 免费打出一张」的网关分支（`SelectHandCard`）。**仍缺（需联机侧新 UI，待口径）**：
  JC117 的"激活 + 地区目标"、联机激活代价选择（牺牲角色/移除标志/目标地区）。

**73 静止规则"子类别白名单结附限制"（JC001 同款实现位的放宽版）→ JC081 亚洛尔 [前缀]「除装备与祝福外不受附属」**
- 卡文：JC081「**[前缀] 灵体 除装备与祝福外不受附属** 额外费用：展示任意数量的手牌…」。
  这是主表**最后一行 ⬜ 待人工归类**（用户 2026-09 曾明确暂缓；本轮答复：**"与 JC001 类似实现即可"**）。
- 关系与口径：JC001/JC030 的「**不能被装备结附**」＝`StaticKind.CannotBeAttached`（**一律禁止**，只拦【装备】类附属）；
  本卡是**同一实现位**上的放宽版＝**白名单**："**只有**命中白名单的附属能结附本卡"（白名单＝`装备,祝福`）。
- 实现：
  - 新静止规则 **`StaticKind.OnlyAttachWithSubCategories`**：`StaticEffect.Param` ＝ **逗号分隔的子类别白名单**
    （本卡 `"装备,祝福"`）；【祝福】是既有附属子类别（例 `Cards/Data/PlayerCards/BH/BH054_adaierdejiushu.tres`
    的 `SubCategories = ["状态","祝福"]`），【装备】同理（JC020/JC073/JC116/JC121…）。
  - **判定单一真相源**（`EffectEngine` 静态 helper，**读卡面印刷静止规则** → 客户端/服务端同解，
    **不依赖 EffectEngine 实例**、不随快照丢失）：
    · `EffectEngine.GetAttachSubCategoryWhitelist(target)`：**未声明该规则 → `null`**（调用方不施加任何限制）；
      声明了 → 返回白名单串；
    · `EffectEngine.PassesAttachWhitelist(attaching, whitelistParam)`：附属**当前**子类别
      （`CardInstance.CurrentSubCategories`，含运行时授予）命中任一白名单项 → 放行；
      **白名单为空串 / 整串只有分隔符或空白（如 `","`、`" , "`） → fail-open 放行**——
      宁可放宽，也不让拼错的 `Param` 把整张卡静默封死（与"Param 写错＝规则消失"的既有容错口径一致）。
  - **两处消费**（与 JC001 完全同款的两处拦截，避免"只拦 UI 不拦结算"的半吊子实现）：
    ① **打出阶段** `CardManager.IsLegalPlayTarget`：非法附属**不能以本卡为目标**（UI 不高亮、拖不放、投影不含）；
    ② **结算兜底** `TurnManager.ResolveEntry` 附属分支：绕过 UI 直接入栈（越权/历史快照/直接注入）→
    **附属进墓地**，不建立结附关系。
  - JC081 脚本新增效果 `JC081_C_OnlyEquipAndBlessingAttachments`（`Timing=Static` + `Scope=AreaZone` + 上述规则）；
    原有 `JC081_A_Spirit`（灵体）与卡级 `ExtraCost`（K10 展示手牌减费，见 65）不变。
- 测试：**`P4AttachWhitelistTests`(11)**——结构（Timing/Scope/规则种类/Param）/ **判定表**（【装备】【祝福】放行；
  【徽章】【诅咒】与空子类别拒绝；空白名单 fail-open；未声明规则返回 `null`）/ **打出阶段**（拦截非法、放行合法、
  **不误伤未声明规则的其它角色**）/ **权威路径** `PlayCard` 拒绝非法目标（返回 false、留手牌、堆叠空、未结附）/
  **结算兜底**（非法→墓地；合法【装备】→正常结附并成为宿主附属）；
  **双端一致性** `ClientServerParityTests` 新增一例：服务端判定 = 客户端本地判定 = 服务端合法性投影，
  且客户端能从**投影实例**读出同一份白名单（`装备,祝福`）。
- 账本：卡接入数 133→**134**、行 ✅257→**258**、⛔ 保持 3、**⬜ 待人工归类 1→0（清零）**、部分接入 1→**0**；
  全量 headless **128 套件 873 例 全绿 0 失败 0 错误**。

**72 K28 免费打出 ＋ "视作【快速】" ＋ 封印回手识别 → JC119 守秘人②**
- 卡文：「死亡触发：**展示封印在守秘人上的所有牌**，你可以**打出其中一张**，视作它具有【快速】且不需要支付其费用
  （但**仍需满足忠诚需求**）。将不打出的牌移回其拥有者手上。」
- 口径（用户 2026-09，本轮答复）：**走正常打出流程**——死亡触发在**原子锁之后**结算；此时封印牌**已按封印规则
  回到拥有者手牌** → 直接**从手牌**展示那批牌后打出；交互＝**复用既有选卡窗**，**允许取消＝不打**。
- 实现：
  - **识别"刚由封印回手"的牌**：`ZoneManager.DetachSealedCards`（宿主离场连带）为每张被放回的封印牌置
    `CardInstance.ReturnedFromSealHostUid = 宿主UID`；该卡**离开手牌或成功打出**即清除（`RemoveFromAllZones` / `PlayCard`）。
  - **展示 + 选择 + 登记**：新动词 `ActionVerb.RevealSealedAndQueueFreePlay`：候选＝控制者手牌中标记指向本卡的那批 →
    **先公开展示全部**（`RevealCardPublicly`）→ 弹选卡窗（`allowCancel:true`）→ **忠诚预检**
    （`CanPayLoyalty` 不满足 → 留在手牌并告警）→ `TurnManager.EnqueueFreePlay(owner, uid)`；
    **无 UI → 兜底不打出**（绝不替玩家出牌）。
  - **正常打出流程（关键）**：新增**免费打出队列**——效果在结算期**只能登记**（结算期处于原子锁内，直接 `PlayCard`
    会被"原子操作进行中"拒绝，也不应在结算内嵌套打出）；由 `TurnManager.ResolveStackTop` 在 **`ExitAtomicWhenIdle()`
    之后**调用 `ProcessQueuedFreePlays()` 逐条执行 `PlayCard(..., freePlay:true, asIfQuick:true)`；
  - **`freePlay` / `asIfQuick`**：`freePlay`＝**费用与额外费用一律 0**（仍走正常付费段但总额 0）而
    **忠诚照常校验**（卡文"但仍需满足忠诚需求"）；`asIfQuick`＝**跳过三档行动阶段门禁**（"视作具有【快速】"＝
    不受行动阶段限制）；两者都只是"由效果授予的打出许可"，其余打出流程（入堆叠/优先级/让过）完全照常。
  - **"将不打出的牌移回其拥有者手上"**＝**天然成立**（封印牌在守秘人离场时已按规则回手）。
- 测试 `P4KeeperTests`(5)：结构（时机/可选/动作）/ **端到端**（消灭守秘人 → 封印牌回手并带标记 → 弹窗候选＝那批牌 →
  已登记免费打出） / **免费打出不付费且入堆叠**（9 费牌 0 费打出；离手后标记清除） / **取消＝一张都不打** /
  **无封印牌则无事发生**。
- 账本：行 ✅256→**257**、⛔4→**3**、部分接入 2→**1**（只剩 JC057，按 A6 延后）；
  全量 headless **127 套件 861 例 全绿 0 失败 0 错误**。
- **已知限制（待用户确认）**：**需要选目标**的封印牌在"免费打出"时**没有目标交互**（效果内无交互入口），
  本批打日志并**留在手牌**；其余封印牌照常免费打出。

**71 K21 暗藏者专用激活 ＋ 地区子类别前提 → JC041 禁婆①**
- 卡文：「**快速暗藏者行动2：令禁婆由暗藏者状态现身。你只能在其位于水域地区时发动此能力。**」
- 口径（用户 2026-09，**A29** 追问答复）：这是**暗藏者专用**的激活能力——"**区别于其他效果大多正面在场才能生效，
  这个效果只有作为暗藏者时可以激活**"；「**水域**」是**地区子类别**（地区卡库已有，例
  `Cards/Data/AreaCards/DQ001_chenmodefeixu.tres` 的 `SubCategories = ["遗迹","水域"]`）；
  暗藏者位于水域时**双击不直接现身**，而弹**选择框**（"正常现身"／"激活暗藏者效果"，**可取消**）。
- 实现：
  - **声明**：`EffectData.RequireSourceCovert`（bool）＋ `EffectData.RequireSourceAreaSubCategory`（string，空＝不限）；
  - **前提判定单一实现** `ActivateEligibility.MeetsSourcePrereqs(source, ability, areaManager, out reason)`：
    候选侧（`GetEligibleAbilities` 与 `CanActivate` 两处）与**权威侧**（`EffectEngine.ActivateAbility` 1.5 步）**同源**；
    无法判定（来源不在地区／AreaManager 未注入）→ **不满足**（fail-closed）；
  - **地区子类别 API**：`AreaManager.AreaHasSubCategory(areaUid, 子类别)`（读**地区卡模板** `AreaCardData.SubCategories`；
    地区无实例级形态，故无运行期覆盖问题）＋ `AreaManager.GetAreaInstance(areaUid)`；
  - **效果**：`Uncover{"自身"}`＝经 `CardManager.RevealCard` 收口**由暗藏者状态现身**（照常广播进场/现身两类时机、
    **不改变所在地区**）；费用 2 + 快速窗口；
  - **UI（单机）**：`Arena._onCovertRevealForward` 在暗藏者双击时，先查候选侧列表中**当前可发动**且声明
    `RequireSourceCovert` 的能力 → 有则弹选择框（正常现身 / 激活该能力 / 取消），无则维持原"直接现身"。
- 测试 `P4WaterRevealTests`(6)：结构 / **水域暗藏者可发动**（付 2 费、入堆叠） / **非水域不可发动**（候选侧 false ＋
  权威侧拒绝且未入堆叠） / **正面角色不可发动**（"暗藏者行动"语义） /
  **端到端：发动 → 经 `RevealCard` 现身且不改地区** / `AreaHasSubCategory` 边界（命中/不命中/未知地区 fail-closed）。
- 账本：卡接入数保持 **133**（JC041 脚本此前已注册，本批补第一行）；行 ✅255→**256**、⛔5→**4**、部分接入 3→**2**；
  全量 headless **126 套件 856 例 全绿 0 失败 0 错误**。
- **注**：原 ⛔ 行里的"多玩家决策编排"属**误记**（那是同卡另一条"现身强制触发：每位玩家各弃一张手牌"，
  早已交付于 `P3TwoPlayerDiscardTests`）。
- **仍缺**：**联机侧**的暗藏者双击"选择框"（现单机已接；联机走 `RevealCovert` 裸意图，未接选择框）。

**70 K21 按组织的"进入限制" ＋ "支付否则牺牲"变体 → JC064 戒严**
- 卡文：「结附于目标地区 **准备步骤强制触发：支付1，否则牺牲戒严。** **持续：所有非【国家机构】的角色牌
  不可正面打出在本地区，也不能移动进入本地区。**」FAQ：◆秘密派遣仍可进入、也可用其他方式放置进场（如操控死尸）。
- 口径（用户 2026-09，**A19**）："不能**移动**进入本地区"**涵盖机动 / 效果移动 / 置换**；
  "支付1，**否则**牺牲戒严"＝**由玩家选择**支付或牺牲。
- 实现：
  - **进入限制**：新静止种类 `StaticKind.BlockNonOrgEntryInArea`（`Param`＝组织名，本卡＝`国家机构`），
    走既有"静止规则统一入口"（`SetStaticRuleRegistrations` / `HasAnyStaticRule` / `ClearAllStaticRulesFor` / `ResetAll`）；
    判定收口 **`EffectEngine.IsEntryBlockedByOrgRule(card, areaUid)`**：遍历在册来源，来源须**管辖该地区**
    （结附于该地区（`AttachedTo == null` 的附属）或位于该地区），该卡**组织名 ≠ 允许组织** → 拦下
    （未写组织名 → 不拦，防拼错静默生效）。两个消费点：
      · **正面打出在该地区**＝`TurnManager.PlayCard`（判据＝本次打出的目标地区；只对**角色牌**）；
      · **移动进入该地区**＝`CardManager.MoveCardToArea`（**机动/效果移动/置换唯一收口**；只对**角色牌**，
        且要求该卡**此刻已在场上**——从墓地/手牌/牌库进场属"**放置进场**"，按 FAQ **放行**）；
    **秘密派遣**走 `TurnManager.DeployCovert` → `ZoneManager.MoveToArea`，不经上述收口 → 自然放行（FAQ 第一条）。
  - **支付否则牺牲**：新动词 **`PayOrSacrificeSelf{金额}`**（与 `PayOptional`/`SacrificeSelfOptional` 同一交互原语）：
    可支付 → 弹 0/1 窗口（"支付 1" / "牺牲"）；选择支付 → `SpendMana` 且卡留场；选择牺牲 → `ResolveDeath(Sacrifice)`
    （照常触发死亡时机）；**费用不足 → 不询问、直接牺牲**（卡文"否则"）；**无 UI → 兜底"能付就付"**
    （不替玩家放弃已能支付的代价）；支付失败 → 兜底牺牲。
- 测试 `P4MartialLawTests`(6)：结构 / **正面打出被拒（国家机构角色放行）** / **移动进入被拒（国家机构放行）** /
  **FAQ：放置进场放行** / **准备步骤：支付（扣费留场）/ 牺牲（进墓地）/ 费用不足不询问直接牺牲** / **来源离场后限制消失**。
- 账本：卡接入数 **132→133**；行 ✅253→**255**、⛔7→**5**、部分接入 4→**3**；
  全量 headless **125 套件 850 例 全绿 0 失败 0 错误**。
- **仍缺**：FAQ 第二条（**带有混乱关键词的角色无法随机进入**）——**混乱规则文本全项目未定义**，按用户裁定**暂不做**。

**69 P3·K4 实例级形态转换 → JC027 花园地精像 / JC028 鲜血奴仆**
- 卡文：JC027「结附于目标地区 快速行动2：花园地精像**成为一个地精角色**，且具有［白眼］［黑刀］［黑星］和 1 防御力
  直到回合结束。（**它仍然是附属**）」；JC028「快速行动，**牺牲鲜血奴仆**：将鲜血奴仆**移回场上**，
  **成为一个名为「鲜血」的附属**并结附于目标**吸血鬼角色**。鲜血**不具有能力图标和防御力**，
  且具有『**受此结附的角色获得 1 防御力 和［黑刀］**』的能力。」
- 口径（用户 2026-09）：**A15**（效果发动后**实例属性（CategoryFlag）变为「角色/附属」组合**，还原＝变回附属）/
  **A28**（改卡名「鲜血」、类别改附属**不保留角色**、原图标消失、只保留「鲜血」的效果）/
  **A32**（**只改实例，没有任何效果改变卡的印刷类别** → 不做 `CategoryOverride`）；本轮追问答复：
  · JC027「参与角色统计与**目标角色合法性**」＋ 实例层加**「地精」子类别**；
  · JC028 是**同一张卡重新进场**、**以附属身份进场且已损失原有效果** → **与它自身的进场触发无关**、
    **只作为附属（不作为角色），不涉及地区选择**、**场上无符合条件角色则不能发动**、**结算时目标不在场则不回到场上**；
  · 卡名覆盖**目前不影响显示，但影响卡名判定**。
- 实现：
  - **`CardInstance` 形态覆盖**：`CategoryFlagOverride` / `CardNameOverride` / `PrintedEffectsSuppressed` ＋
    `FormEffects` / 印刷图标与防御清零（`_zeroPrintedStatistics`，`ResetToBaseValues` 共同尊重）；
    有效值访问器 `EffectiveCategoryFlag` / `EffectiveCardName` / `EffectiveEffects` / `HasFormOverride`；
    `ApplyFormOverride` / `ClearFormOverride`；**离场复位（`ResetToBaseState`）一并清形态**。
  - **声明与动词**：`EffectData.FormChange`（`FormChangeSpec{Category,Name,SuppressPrintedEffects,
    ZeroPrintedStatistics,AddSubCategories,Effects}`）＋ **`ActionVerb.TransformForm{对象, 期限}`**；
    期限含"回合结束"时登记 `TimedMod{Kind="Form"}` → `ExpireTurnEndEffects` 撤销形态（含追加子类别）并按印刷表重注册。
  - **效果表按形态取用**：新增 `EffectEngine.EffectsOf(card)`（＝`card.EffectiveEffects`），统一用于
    `RegisterPersistent` / `TriggerRegistry` 收集 / `ActivateEligibility.GetAllActivateAbilities` /
    `CardManager.GetCardAbilities` / 各项持续规格查询（夺控·伤害重定向·图标抑制·领域抑制·文本空白·授予能力）。
    **顺序关键**：失活时**先按印刷表 `UnregisterPersistent`** → 施加形态 → 按形态表 `RegisterPersistent`。
  - **类别改读有效类别**：`CardManager.MapInstanceToTargetKind`（JC027 能被指定为"目标角色"）/
    `AreaStatsCalculator.IsStatParticipant`（角色统计）/ `EffectValueResolver.MatchCategory`（`计数:区域:…:角色`）。
  - **JC028 的自我重进场**：代价＝`ActivateCondition="牺牲本角色"`（入堆叠前支付，牺牲＝死亡照常触发时机）→
    结算时 `TransformForm{"自身","常驻"}` ＋ **`Attach{"目标"}`**（把来源卡结附到目标；从墓地取出 → 进附属区）。
  - **顺带补齐（引擎缺陷）**：① 激活路径第 2.5 步原只校验"有没有给目标"，**不校验精筛条件** → 现按
    `TargetValidity.Check`（与打出/结算**同一实现**）权威复检；非法目标**拒绝发动且代价不白付**。
    ② `Gain{图标名}` 原**只写 `Attribute` 通道**（数值对、**图标名丢失** → `HasIcon`/UI 落空）→
    现**图标 + 属性双通道**都写并各登记一条时效项（与 JC116/JC019 既有修正口径一致）。
  - **双端同解**：快照新增 `CategoryFlagOverride` / `CardNameOverride` / `PrintedEffectsSuppressed`
    （`ZoneManager.ProjectCard` 下行 → `CardManager.RebuildFromRef` 还原为实例形态）。
- 测试 `P4FormChangeTests`(6)：两张卡结构 / **JC027 成为角色**（目标合法性→`TargetKind.Character`、子类别「地精」、
  图标与防御、**印刷子类别不变**）＋**回合结束还原** / **JC028 端到端**（牺牲自身 → 附属身份重新进场 →
  改名「鲜血」/类别附属/失活印刷效果/防御 -1 与战力 0 → 宿主获得 1 防御力与［黑刀］）/
  **JC028 无合法目标不能发动** / **结算时目标离场则不回场**。
- 账本：卡接入数 **130→132**；行 ✅251→**253**、⛔9→**7**、部分接入 6→**4**；
  全量 headless **124 套件 844 例 全绿 0 失败 0 错误**。
- **仍缺**：`CategoryOverride`（印刷类别改写）按 A32 **不做**。

**68 可选的"牺牲自身"＋查看手牌→指定弃牌 → JC114 好奇的黑客**
- 卡文：「**行动1，横置：目标玩家展示其手牌。你可以从中选择一张，并牺牲好奇的黑客，若你如此做，
  该玩家弃掉你所选择的牌。**」
- 口径（用户 2026-09）：**A13**（"牺牲自身"作代价在**入堆叠前支付**，参考 JC015 驱虫人写法）＋ 交互答复
  （**复用私密检视窗 + 选卡窗；允许取消**：取消＝不牺牲、什么都不发生）。
- 实现（全用既有原语接成条件链）：
  - 激活＝`CharacterActivated` + `ActivateWindow="行动"` + `Cost=1` + `ActivateCondition="横置本角色"`
    （代价入堆叠前支付，与 JC015/JC096 同款）；目标＝`Kind=Player`（**K24**，入堆叠锁定 `StackEntry.TargetPlayerId`）；
  - ① `Inspect{"目标玩家手牌"}`＝**私密**查看其手牌（只给施术者本人；不移动/不翻面/不改状态）；
  - ② **新动词 `SacrificeSelfOptional`**＝"**你可以**…并**牺牲好奇的黑客**"：复用 `PromptNumberInput`（0/1，
    按钮"牺牲/不牺牲"）＋既有挂起续算机制（`SuspendChainForInput`/`ContinuePendingChain`）：
    **选择牺牲 → `ResolveDeath(Sacrifice)`（照常广播死亡时机）→ 动作链继续**；
    **不牺牲 / 取消 → 置 `_actionChainAborted` → 后续动作整段跳过**（卡文"若你如此做"）；
    **无 UI → 兜底不牺牲**（绝不替玩家牺牲）；效果结算时来源可能已在墓地，后续动作仍以"已捕获的来源/控制者"结算；
  - ③ `Discard{"1","目标玩家","由施术者指定"}`＝由**施术者**从该玩家手牌中指定一张丢弃
    （既有 P2 弃牌原语本就支持"选择者≠弃牌者"：`PromptDiscard(弃牌者, 选择者＝施术者, 候选＝其手牌)`；
    联机侧候选只发给选择者本人）。
- 测试 `P4HackerTests`(4)：结构（时机/窗口/费用/横置/玩家目标/三动作与参数） /
  **牺牲后指定弃牌端到端**（黑客进墓地、弃牌者＝P2、选择者＝P1、被指定的那张进墓地、其手牌 -1） /
  **不牺牲则什么都不发生**（弃牌动作整段跳过、手牌不变） / **无 UI 则不牺牲**（fail-closed）。
- 账本：卡接入数 **129→130**（新增 `JC114_haoqideheike.cs`）；行统计 ✅250→**251**、⛔10→**9**；
  全量 headless **123 套件 838 例 全绿 0 失败 0 错误**。

**67 K5 可变数量标志代价 ＋ 双通道一次性减费 → JC055② 安妮斯朵拉（并补齐 JC044② 的单卡代价选择窗）**
- 卡文：「**行动：自本方卡牌上移除 X 个时间标志：本回合中，你所打出的下一张灾难牌的所需费用减少 X。**」
- 口径（用户 2026-09）：**A17**（"移除一个时间标志"＝**发动代价**，不是效果步骤）＋ **A30**（交互＝**类似伤害分配的
  选择框：点一次某张卡即记录用 1 点 → 确认后移除对应标志**；未打出则该减费保留到回合结束消失）＋
  本轮补充：**X 不允许为 0**（至少移除一个）；"**不仅限于正面打出，暗藏现身可以享受这个费用减免**"。
- 实现：
  - **代价（可变数量）**：`CounterCostSpec.AmountByPlayerChoice=true` → 候选＝本方场上带该标志 ≥1 的卡；
    `EffectEngine.PayCounterCostAllocation(source, data, allocation, out paidX)`：
    **先全部复检再无副作用落地**（每张卡须是合法候选、数量 ≤ 该卡实际持有量、**总和 ≥ 1**，否则 fail-closed 拒绝）；
    顺序＝按 `Markers` 白名单找"卡上实际持有的写法"逐点扣；
  - **X 的透传**：支付成功后把 X 记入 `PushActivationToStack(..., eventAmount)` → `StackEntry.EventAmount` →
    结算时下发 → 新增取值写法 **`代价数量`**（`EffectValueResolver`；与 `事件数量` 共用通路）→ 代入动作参数 `$X`；
    激活前置的动态值门禁对 `代价数量` **不做前置求值判定**（该值在支付后才产生）；
  - **减费（双通道 + 一次性 + 子类别）**：`Gain` 的 `Args[4]` 新增"**付费通道**"（`打出`（缺省）/ `现身` /
    组合 `"打出,现身"` / 通配 `全部`）＋ `EffectEngine.ScopeMatchesChannel`（`GetCostModification` 与
    `ConsumeCostModification` 同步改用）→ **打出与现身（暗藏者付费翻面）两条通道都能享受**；
    子类别筛选沿用 `CostModFilter` 的 `子类别:灾难`；`Duration="直到回合结束"` → 时效项，未用则回合结束流失；
  - **View（单机）**：`Arena.BeginActivateWithCounterCostAllocation`（A30 的"点卡 +1 / 重置 / 确认（≥1 才可确认）/
    取消"循环，复用通用选择窗）；**顺带补齐** `BeginActivateWithCounterCostChoice`（JC044② 的**固定数量**标志代价
    原本**单机无 UI**、引擎直接兜底取第一张 → 现补上"选一张卡"的选择窗），并在激活分派 `BeginActivate` 里分流。
- 测试 `P4CounterCostXTEsts`(6)：结构 / **逐卡分配移除标志并把 X 下发入堆叠（`StackEntry.EventAmount`）** /
  **不分配（总和 0）→ 拒绝发动** / **无候选 → 拒绝发动** / **分配超出持有量 → fail-closed（一个都不扣）** /
  **下一张灾难牌按 X 减费**（非灾难牌不减、**现身通道同样减免**、**一次性消费后失效**）。
- 账本：行统计 ✅249→**250**、⛔11→**10**（**JC055 两行全 ✅，移出"部分接入"**）；卡接入数保持 **129**
  （JC055 脚本此前已注册，本轮补第二行）；全量 headless **122 套件 834 例 全绿 0 失败 0 错误**。

**66 K1「玩家选择」型动态值 ＋ 牌库顶"费用比对" → JC083 睿智的斯芬克司**
- 卡文：准备步骤触发：**选择一个数字**，然后**展示你牌库顶的一张牌**；印刷费用与之**相同** → 置入**手牌**；
  **不同** → 置入**牌库底**。
- 口径（用户 2026-09）：**A31**（数字范围 **0～20**；所展示牌置入手牌**不算抓牌**）＋ **A36**（**复用既有数值输入窗**）
  ＋ **A39**（X=0 合法——此处的 X 是"猜测值"而非"数量"；取消/无 UI → 不发动）。
- 实现：
  - **取值**：`EffectData.ValueExpr = "玩家选择"` ＋ 新增 `ValueMin` / `ValueMax`（本卡 0/20）；
    `EffectValueResolver` 的 `玩家选择` 分支（原先"未实现 → fail-closed"）改为读
    `ValueContext.PlayerChosenValue` ＋ `HasPlayerChosenValue`（**struct 哨兵**：不能拿 0 当"未取值"，因为可合法选 0）；
  - **取值时机＝结算之前**：`ResolveEffect` 入口检测"玩家选择型且尚无值" → `BeginPlayerValuePrompt`
    存下"待续结算"上下文（`_pendingResolve`）并弹 `IGameUIHooks.PromptNumberInput`（范围＝`ValueMin..ValueMax`，
    按钮"确认/取消"）；回调写回 `_playerChosenValue` 并**重入** `ResolveEffect` 完成结算；
    **取消 → 不发动**；**无 UI → 不发动**（fail-closed）；取值**每次结算只消费一次**（不串到后续效果）；
  - **`EvaluateDynamicValue`** 对"玩家选择"短路：有值 → `Ok`（**不再套用"X=0 ⇒ Blocked"**）；无值 → `Blocked`；
  - **新动词 `RevealTopAndTakeIfCostIs`**（Args[0]＝期望印刷费用，卡里写 `$X`）：
    取来源控制者牌库**索引 0**（＝牌库顶）→ `RevealCardPublicly`（公开展示，不改变规则状态）→
    费用相同 → `CardManager.MoveCardToHand`（**不广播 `OnDraw`**：不是"抓牌"）/ 不同 → `TurnManager.ReturnCardToDeck(toTop:false)`；
    牌库为空 → 无事发生（不崩）。
- 测试 `P4ChooseNumberTests`(7)：结构 / **费用相同入手且不广播 `OnDraw`**（用记录总线观察）/
  **费用不同置入牌库底**（断言牌库末位） / **选择 0 合法且照常结算** / **取消则不发动** / **无 UI 则不发动** / 空牌库无事发生；
  并更新 `P3DynamicValueTests` 对"玩家选择"的断言（未取值 fail-closed / 有值 Ok 且可为 0）。
- 账本：卡接入数 **128→129**（新增 `JC083_ruizhidesifenkesi.cs`）；行统计 ✅248→**249**、⛔12→**11**；
  全量 headless **121 套件 828 例 全绿 0 失败 0 错误**。
- **仍缺**：联机侧"数值输入"走 `IInputGateway`（`ChoiceRequest.Kind.NumberInput` 已有协议类型，JC083 的触发路径尚未接线）。

**65 K10 额外费用框架（付费阶段、入堆叠前）→ JC049① 深渊细语 / JC135① ② 余震**
- 口径（**用户 2026-09，§7.4b A21–A24 + A33**）：额外费用＝**打出这张牌的"支付费用阶段"的一部分**；
  **不在堆叠里结算、不可被响应**；且：
  · **JC135＝先选目标、再定 X**（X＝该目标的**印刷费用**）；**0 费目标 → 不支付额外费用**、直接结算后续效果；
    **合法目标费用均大于可用费用 → 该卡不能打出**（A24 原文的"输入框+确认+取消"因此**不再需要**）；
  · **JC049＝必须支付**，**无合法牺牲对象则不能打出**；牺牲者由**打出者自己选择**（联机侧把该选择回环给该玩家，
    复用 `Kind.SelectTarget`，**不是**新增窗口类型）；
  · **JC081**（下一批）＝不展示即全额支付、取消不扣任何费用、减免一次性。
- 实现：
  - **数据**：卡级 `PlayerCardData.ExtraCost`（新 `ExtraCostSpec`：`Kind`＝`SacrificeCharacter` / `PayTargetCost` /
    `RevealHandCards`；`Mandatory`；`RevealFilter`；`Sacrifice` 细则）；旧文本字段 `ExtraCostDescription`/
    `IsExtraCostMandatory` 保留（读取兼容）。
  - **消费点＝`TurnManager.PlayCard` 的付费阶段**（在**目标/模式/多目标校验之后、EnterAtomic 之前做可支付性校验**，
    再在原子锁内、**入堆叠之前**支付）：
    · `totalCost = 基础费用 − 时效减免 − 展示减免 + 额外 X`（合并为**一次** `SpendMana`）；
    · `SacrificeCharacter`：候选＝`EffectEngine.GetSacrificeCandidates`（与 K22 牺牲代价**同一实现**）；
      无候选 → 直接拒绝（**不扣费、不入堆叠**）；指定非法候选 → fail-closed 拒绝；
      未指定 → 确定性兜底第一个合法候选（无 UI/headless）；支付＝`CardManager.ResolveDeath(Sacrifice)`（**照常广播死亡时机**）；
    · `PayTargetCost`：未给目标 → 拒绝；`extraMana = max(0, 目标模板 Cost)`；
    · `RevealHandCards`：校验候选确实在该玩家手牌（fail-closed），合格张数 → 一次性减免（本批未接卡，通道已就绪）。
  - **View（单机）**：`Arena.BeginPlayWithExtraCostChoice`（该卡声明了需要玩家选择的额外费用时由
    `CardDragController` 分流）→ `ActivationChoiceWindow` 选择窗（"牺牲 X（额外费用）" / "取消（不打出）"）→
    带选择 `PlayCard`；**取消＝不打出、不扣任何费用**；无合法牺牲对象 → 退回手牌
    （`PlayerActionService.NeedsExtraCostPlayerChoice` / `GetExtraCostSacrificeCandidates` 供 View 查询，与权威同源）。
  - **卡数据**：JC049 加 `ExtraCost`（牺牲，必须支付）；**JC135 新建脚本 + 注册**（`PayTargetCost` + 目标＝附属 +
    快速行动 `PlayTiming.Response` + `Destroy`）；**JC081 新建脚本 + 注册**（`RevealHandCards`（可选）+
    `RevealFilter="组织:圣贤|域:神圣"` + 灵体关键字）。
- 测试 `P4ExtraCostTests`(14)：结构 / "需要玩家选择"判定 / **JC049 无牺牲对象不能打出（不扣费不入堆叠）** /
  **JC049 牺牲先于入堆叠且由打出者选择**（被选者进墓地、未选者留在场上、照付 2 费）/
  **JC049 指定敌方角色 fail-closed** / **JC135 零费附属不付额外费用** / **JC135 支付 2+X** /
  **JC135 总额不足被拒且无副作用** / **JC135 未选目标被拒** / **JC135 端到端消灭目标附属** /
  **JC081 结构与过滤器** / **JC081 展示合格牌按张减免且不合格不减** / **JC081 不展示即全额** / **JC081 非法展示候选 fail-closed**。
- 账本：卡接入数 **126→128**（新增 `JC135_yuzhen.cs` / `JC081_yaluoer.cs`）；行统计 ✅244→**248**、⛔16→**12**
  （JC049/JC135 全行 ✅ 移出"部分接入"；JC081 仅剩 [前缀] 待归类）；全量 headless **120 套件 821 例 全绿 0 失败 0 错误**。
- **联机侧**"需要玩家选择的额外费用"回环**已补齐（2026-09，见 §7.10 75 / §7.4b A43）**：牺牲选择复用 `Kind.SelectTarget`；手牌多选新增 `Kind.SelectHandCards`；
  JC081 的 [前缀]「除装备与祝福外不受附属」按用户口径**归类暂缓**。

**64 K4 持续夺控 ＋ 子类别改写 ＋「宿主翻面 ⇒ 其上附属被消灭」通用规则 → JC036 初拥（并修正 3 处子类别口径）**
- 口径来源（**用户 2026-09 逐条确认**）：
  · **A16**：控制权"**持续尽可能久**"（卡文未写"持续到回合结束"）；**受控角色所在地区不变**，切换的只是争夺区归属与 controller；
  · **本轮追问答（"版本号"判据）**：「初拥版本号变化一般伴随初拥离场，控制权效果自然丢失，被初拥角色版本号变化一般也是角色离场，
    作为附属卡的初拥也会自然进入墓地」→ **不另设版本号判据**，一律由**持续效果自身的登记/注销生命周期**承担；
  · **FAQ 第二条落地方式**：「做成**通用规则**：任何『结附于角色』的附属，其宿主翻面为暗藏者时被消灭」；
  · **子类别口径**：「**印刷类别**为人类指**模型数据**为人类，这张卡的效果会让**卡片实例**失去人类类别，
    **JC091 这样的效果**对受这张卡影响的人类**无效**，但如果是『**消灭目标印刷类别为人类**』的效果**能生效**」。
- 实现：
  - **① 持续夺控** `EffectData.ControlGain`（`ControlGainSpec.Victim = AttachedHost`）＋ `EffectEngine.SetControlGain` /
    `ReleaseControlGainsFor`（登记表 `_controlGains` ＋ 取证口 `ControlGainCount` ＋ `_controlGainBusy` 防重入；
    接入**静止规则统一入口** `SetStaticRuleRegistrations` / `HasAnyStaticRule` / `ClearAllStaticRulesFor` / `ResetAll`）。
    交控经 `ZoneManager.ChangeController(host, 本卡控制者, **bumpVersion:false**)`——只改 controller、`OwnerId` 不变、
    **卡不移动地区**、**不刷版本号**（与既有口径"争夺区内的夺控换边不刷新版本号"一致，D16 丢失仍由结算时的
    `TargetValidity.CheckRestrictions` 按入栈来源控制者快照承担）；
    **归还**（来源离场·被消灭·解附·卡失效）：交还 `host.OwnerId`（卡面 FAQ 原文），并加防呆——
    只有当宿主当前控制者仍是"本效果写进去的那一份"时才归还（期间被别人再次夺控则不越权改写）。
  - **② 子类别改写**：`CardInstance` 新增 `_suppressedSubCategories`（按来源**计数**）→ 新授予类型
    `Grant/Revoke("SubCategoryRemove", 名)`：把（哪怕是**模板固有**的）子类别从**实例当前类别** `CurrentSubCategories`
    抹掉、撤销时计数归零还原；`ResetToBaseState`（离场复位）一并清零；`EffectEngine.IsGrantPresent` 增加该分支
    → 复位后由既有 `ReapplyMissingContinuousGrants` 自愈。**印刷类别（`HasStaticSubCategory`，读模型数据）不变**。
    卡数据＝`ContinuousGrants{ SubCategoryRemove:"人类" } + { SubCategory:"吸血鬼" }`（落点＝宿主，`GrantToHost`）。
  - **③ 通用规则** `Script/Manager/AttachmentRule.cs` → `EnforceOnCovertChange(host, cm)`：宿主翻面为暗藏者
    （`CovertChanged` 且 `IsCovert == true`）时，消灭其 `AttachedTo == host` 的全部附属（复制列表后 `DestroyInstance`
    ＝**消灭＝死亡**，照常进墓地并广播死亡触发）；**只按"宿主此刻是暗藏者"判**、**不重判结附目标规则本身**
    （否则初拥把宿主改成吸血鬼后会当场自毁）；结附于**地区**（无宿主）的附属不受影响；
    装配点＝`GameInitializer` 的 `CovertChanged` 分支末尾（与 `OnLatent` 广播同处）。
  - **④ 双端同解**：快照新增 `ZoneCardRef.RemovedSubCategories`（`ZoneManager.ProjectCard` 下行）→
    `CardInstance.RestoreProjectedState` 重建当前集合时按"（模板固有 ∪ 快照）**− 被改写掉的**"计算
    （原先只做并集，会把服务端已失去的类别加回来 → 两端 `HasSubCategory` 分叉）；
    卡面子类别标签与 `DeckSearchFilter` 的 `子类别:` 筛选同步尊重"实例当前类别"（实例已初始化时不再回落到模板）。
- **顺带修正（同一口径落地，3 处）**：`TargetRestriction` 新增 **`NotCurrentSubCategory`**（"非某类别"的**实例**口径；
  与 `CostAtLeast` 共用 `Param` 时按 `|` 分段）→ **JC015 驱魔人 / JC019 弥尔顿的镇魔枪**由 `NotStaticSubCategory` 改为它；
  **JC091 谋杀**由 `StaticSubCategory` 改为 `CurrentSubCategory`。依据：三张卡文都只写"人类/非人类"（"印刷"只修饰**费用**），
  只有 **JC036 自身**写"**印刷类别**为人类"→ 继续读模型数据。
- 覆盖卡行：**JC036**（三行全 ✅；**卡接入数 125→126**，新增 `JC036_chuyong.cs` 并注册进 `JCEffects`）；
  测试 `P3EmbraceTests`(8)：结构（印刷类别目标 ＋ `ControlGain` ＋ 两条授予）/ **夺控端到端**（宿主控制者 P2→P1、物主不变、
  地区不变、类别人类→吸血鬼、印刷类别仍为人类）/ **初拥离场 → 控制权归还拥有者 ＋ 类别还原** /
  **宿主翻面为暗藏者 → 初拥被消灭 ＋ 操控权归还**（真走 `SetCovert` 生产路径）/ **通用规则对其它附属同样生效、
  且不误伤结附地区的附属** / **"人类角色"读实例 vs "印刷类别为人类"读模型**（JC091 不再合法、JC036 结附限制仍成立、
  JC015/JC019 改为实例口径）/ **快照投影含被改写类别且客户端重建同解** / **无宿主不夺控**。
- 账本：行统计 ✅243→**244**、⛔17→**16**（⬜ 仍 1：JC081 的 [前缀]，用户明确**暂缓归类**）；
  **部分接入卡数改用脚本对表体逐行重算＝8**（原手工记"11 张"与表体已不一致，以重算为准）；
  全量 headless `TestsRunner` **119 套件 807 例 全绿 0 失败 0 错误**。

**63 K22 多玩家/对手侧自选牺牲 ＋ JC128 二选一 → JC046② / JC113 / JC134 / JC065 / JC128**
- 口径（用户 2026-09）：**A11**（多玩家牺牲编排）＋ **A12**（JC128：标志不足必须牺牲；没有角色时选牺牲路线＝什么都不付出）。
- 实现（两个新动词）：
  - **`ActionVerb.SacrificeChoice{ 种类, 谁, 地区 }`**：
    种类＝`角色`（场上正面角色）/`暗藏者`/`附属`/`资产`；谁＝`每位玩家`/`目标玩家`/`自己`；地区＝`任意`/`本地区`/`目标地区`；
    **每位玩家**＝按 **先手优先**（`TurnManager.FirstPlayerId`）逐个让该玩家**自己选**（先选者先记录，
    后选者可见先选者的选择——UI **高亮**由 View 负责）→ 全部选定后**一起牺牲**（`ResolveDeath(Sacrifice)`，
    原子锁期间**不分死亡先后**）；**无合法候选的玩家跳过、什么都不付出**；
    单机走 `IGameUIHooks.PromptPickCard(该玩家 id, …)`、无 UI 走确定性兜底第一个候选（联机侧留待后续批）。
  - **`ActionVerb.BribeOrSacrifice{ N, 种类 }`**（JC128）：目标玩家（K24 玩家目标）势力标志
    **不足 N → 必须牺牲**（不询问）；足够 → 询问（**确认＝移除 N 个标志**，复用 `CurbRule` 由他按地区分配；
    **取消＝牺牲路线**）；**没有角色时选牺牲路线 → 无候选即跳过（什么都不付出）**。
- 覆盖卡行：**JC046**（现身强制：每位玩家各牺牲一个角色）/ **JC113**（进场强制：各牺牲一个附属）/
  **JC134**（行动：各牺牲一个资产）/ **JC065**（快速行动：目标对手牺牲该地区一个暗藏者 ＋ 遏制1）/ **JC128**。
- 测试 `P3MultiSacrificeTests`(8)：结构（5 张卡）/ **每位玩家各牺牲一个角色**（先问先手 P1、再问 P2；被选者进墓地、
  未被选者留在场上）/ **无候选者跳过**（只有有候选的玩家被问）/ **每位玩家牺牲一个资产**（先手＝P2 时先问 P2）/
  **JC128 标志不足必须牺牲** / **标志足够时选择移除标志则不牺牲** / **没有角色时选牺牲路线什么都不付出（标志未动）** /
  **JC065 限地区**（只问对手、候选只有该地区的暗藏者，别的地区不受影响）。
- 账本：卡接入数 **121→125**（新增 JC113/JC134/JC065/JC128）；行统计 ✅238→**243**、⛔22→**17**。
**62 「不能移动」前缀（未结附态）→ JC121 空鸣御守③**
- 卡文前缀：「**不能移动**」（JC121 为"角色/附属"双类别）。
- 口径（**用户 2026-09，§7.4b A18/A19**）：这是**独立效果行**，**只有未结附于角色时才生效**；
  结附于角色时本卡随角色一起移动（该条不适用）；"移动"**涵盖机动 / 效果移动 / 置换**。
- 实现：
  - 新静止种类 **`StaticKind.CannotMoveWhileUnattached`**；
  - **消费点＝`CardManager.MoveCardToArea`**（机动 `Move` 动词、效果移动、置换的**唯一收口**）：
    条件＝该卡**模板印刷**了本规则 ∧ `AttachedTo == null`（未结附）→ 拒绝移动并打日志；
  - **为什么按模板印刷判定**（新增 `EffectEngine.HasPrintedStaticKind`）：附属卡在**争夺区**（＝未结附态）时
    `RegisterPersistent` 会因 **Z1 早退**（"附属不在附属区 → 跳过持久效果注册"）整体跳过，
    运行期旗标在该状态下**拿不到**；前缀属印刷规则，按模板判定才正确。
- 测试 `P3AttachSelfTests`(6，含本卡此前 4 例)：新增 **未结附 → 移动被拒（仍在原地区）** 与
  **结附于角色后 → 不再受该限制（移动照常生效）** 两例。
- 账本：行统计 ✅237→**238**、⛔23→**22**；**⬜ 待归类仅剩 1 行**（JC081 的 [前缀]「除装备与祝福外不受附属」，
  待用户归类/确认机制后处理）。
**61 附属进场自选结附目标（K12 死角）→ JC121 空鸣御守①**
- 卡文：JC121「进场触发：将空鸣御守结附于**位于本地区**的目标**本方**角色。」（本卡为"角色/附属"**双类别**，CategoryFlag=5）
- 实现：
  - **新执行器 `ActionVerb.Attach`**（原先只是 TODO 占位）：把**来源卡自己**结附到宿主上——
    `IsAttached = true` / `AttachedTo = 宿主` / 宿主 `AttachedCards` 收录 /
    `ZoneManager.MoveToAttachedZone(卡, 宿主所在地区, 卡控制者)`（宿主是秘社时地区写空串，与
    `AttachSearchedCard` 的秘社落点同款）；`Args[0]` 支持 `"目标"`（缺省）与 `"自身"`（反向：把目标结附到来源上）；
  - 卡数据：`Timing = EnterTrigger` + 目标 `{Kind=Character, Restrictions=[Friendly, LocalZone]}` + `Attach{"目标"}`；
  - **无合法宿主 → 留在场上（A18）**：触发取目标阶段无候选 → `EnsureTargetThenPush` 使该触发**不入堆叠**，
    本卡因此保持**未结附**地留在场上；结附后随宿主移动（`ZoneDetailId` 跟随宿主所在地区）。
- **踩坑（已记档）**：本卡是"角色/附属"双类别，而**附属的默认生效区＝结附区**（`AttachTarget`）→
  它的**进场触发**（发生在争夺区那一刻）在收集阶段会被"生效区不匹配"过滤掉；必须在该效果上显式
  `Scope = ZoneScope.AreaZone`。
- 测试 `P3AttachSelfTests`(4)：结构（时机/目标限制/动作）/
  **端到端：进场 → 结附到本地区本方角色（宿主收录、未选敌方）** /
  **本地区无合法宿主 → 留在场上且 `AttachedTo == null`** /
  **结附后随宿主移动（ZoneDetailId 跟随到新地区）**。
- 账本：行统计 ✅236→**237**、⛔24→**23**（JC121 仍有 ⛔ 行：「不能移动」独立效果行）。
- **仍缺**：JC121 的「**不能移动**」独立效果行（A18：仅**未结附于角色**时生效；A19：涵盖机动/效果移动/置换）。
**60 地区内"卡上标志数"取值 + 准备步骤三步触发 → JC048 诅咒沙漏**
- 卡文：「准备步骤**强制**触发：在诅咒沙漏上放置一个时间标志。然后该地区每有一个时间标志，就对位于该地区的所有角色
  造成1点伤害。然后若该地区上有三个或更多时间标志，则牺牲诅咒沙漏。」
- 实现（**同一时机的三条 `EffectData` 按声明顺序结算**，正好表达卡文的"然后…然后…"）：
  - **①a** `PlaceCounter{"时间标志","1","自身"}`（`Target = None`，无群体）；
  - **①b** 群体伤害：`Target{Kind=Character, MassMode=AllInArea, CanTargetSelf=true}` + `DealDamage{"$X"}`，
    `ValueExpr = "计数:地区内卡上标志:时间标志:本地区"`；
  - **②** `Condition{Predicate="AreaMarkerAtLeast", Param="时间标志,3"}` + `Sacrifice`（牺牲自身＝死亡，走死亡时机）。
- 新机制/口径：
  - **新取值写法 `计数:地区内卡上标志:标志名:地区`**（地区：`本地区`/`目标地区`）→ 统计 `ZoneDetailId == 该地区`
    的**全部卡**上该标志之和；实现收口为 `EffectValueResolver.CountMarkersInArea`，**与条件谓词 `AreaMarkerAtLeast`
    共用同一实现**（避免两处口径分叉）；
  - `AreaMarkerAtLeast` 的**地区回落**：无结算地区上下文（如准备步骤触发）时回落到**来源卡所在地区**
    （结附地区的附属卡天然成立，正是 JC048 的形态）。
- **踩坑（已记档）**：K2 群体动作的语义是"**对每个目标各执行一次整条动作序列**"→
  "放标志"与"群体伤害"**必须拆成两条效果**，否则标志会被放 N 次（本轮实测踩到，已修正并加断言）。
- 测试 `P3HourglassTests`(5)：结构（三条效果的时间/强制/群体/动态值/牺牲）/
  **新取值写法计数**（本地区 1+2+1＝4，别的地区 5 不计入）/
  **端到端：放标志（+1）→ 群体伤害（该地区共 2 个标志 → 双方各受 2 点，`Damage == 2`）** /
  **≥3 个标志 → 牺牲自身（进墓地）** / **只有 2 个 → 不牺牲**。
- 账本：卡接入数 **120→121**；行统计 ✅235→**236**、⛔25→**24**；部分接入 12→**11**。
**59 战斗获胜的近似判定（A14）→ JC034 心兽之道③**
- 卡文：JC034「持续：当你赢得一次战斗对抗时，**若有本方吸血鬼角色参与了这次战斗对抗**，你可以令目标对手弃一张手牌。」
- 口径（**用户 2026-09，§7.4b A14**）："参与了该次战斗对抗"按**近似**判定＝
  **"位于战斗地区且属于获胜方"**（引擎侧不额外记录参战者名单）。
- 实现：
  - 新谓词 **`WinnerIsSelfWithAllySubCategoryInCombatArea`**（`EffectCondition.Param` ＝ 子类别名，如 `"吸血鬼"`）：
    ① 来源卡控制者 ＝ 对抗获胜方（`triggeringPlayerId`）；② **战斗地区**（`triggeringAreaId`）内存在
    **同控制者**、带该子类别的**场上角色**（`CurrentZone == AreaZone && ZoneDetailId == 战斗地区`）；
  - 触发：`Timing = OnCombatWon` + `Forced=false`（卡文"你可以"）+ **目标＝玩家**（`Kind = Player` +
    `Restrictions = Enemy`，K24：**入堆叠时锁定**，无 UI 时确定性兜底第一个候选）+ `Discard{"1","目标玩家","自选"}`
    （由该对手**自己选择**弃哪张，与 JC031 同款）；
  - **顺带修**：`TriggerRegistry` 的**收集**路径原先把条件判定写成"无 `Param`"调用
    （`EvaluateCondition(..., ctx.TriggeringPlayerId, ctx.TargetAreaId)`）→ 参数化谓词在**收集阶段**会按
    "未给参数"判定、与结算侧口径分叉；现已把 `data.Condition?.Param` 一并在收集路径透传（同步/结算两处一致）。
- 测试 `P3CombatWonAllyTests`(3)：结构（时机/条件/参数/玩家目标限制/弃牌参数）/
  **谓词边界**（本方获胜+本地区有本方吸血鬼→满足；敌方获胜→不满足；打赢别的地区→不满足；
  吸血鬼移出战斗地区→不满足）/ **端到端：`OnCombatWon` 触发 → 入堆叠（玩家目标锁定）→ 目标对手弃 1 张**。
- 账本：行统计 ✅234→**235**、⛔26→**25**；JC034 三行全 ✅ → 移出"部分接入"集合（13→**12**）。
**58 参数化条件谓词（地区标志数）＋ 地区标志全清 → JC127 终局；准备步骤放标志 → JC055 安妮斯朵拉①**
- 卡文：
  · JC127「行动：选择目标地区，移除该地区上的所有势力标志。**然后若该地区有三个或更多时间标志，则消灭该地区上的所有角色**。」
  · JC055①「准备步骤触发：在目标角色或附属上放置一个时间标志。」
- 实现：
  - **JC055①（零新机制）**：`Timing = OnTurnStarted`（＝**准备步骤**，与 JC024"下一个准备步骤"同一时机常量）
    ＋ 或种类目标（`Kind = Character` + `KindAlt = Attachment`，K14 已交付）＋ `PlaceCounter{"时间标志","1","目标"}`。
  - **JC127①**：势力标志在 `AreaManager` 里**按玩家分桶**存储 → "该地区上的**所有**势力标志"＝
    **两条** `RemoveToken{"势力标志","全部","对手","目标地区"}` + `{"…","自己","目标地区"}`（既有原语，直接移除、不由对手分配）。
  - **JC127②（新谓词）**：`EffectCondition` 新增 **`Param`** 字段，并把它**透传**给 `EvaluateCondition(..., param)`；
    新谓词 **`AreaMarkerAtLeast`**：`Param = "标志名,数量"`（如 `"时间标志,3"`；只写标志名＝至少 1），
    统计口径＝**`ZoneDetailId == 目标地区` 的全部卡**上该标志之和（涵盖地区内角色与结附该地区的附属），
    地区取结算时的 `targetAreaId`（无地区 → 不满足）；
    条件满足 → 群体 `Target{Kind=Character, MassMode=AllInArea, CanTargetSelf=true}` + `Destroy`（消灭＝走死亡时机）。
  - **两条效果拆成两条同时机的 `EffectData`**：① 无条件执行（清标志）② 带条件（≥3 才消灭）——
    事务卡的全部 `OnSorceryCast` 效果按序结算，正好表达卡文的"然后若…则…"。
- 测试 `P3MarkerAreaTests`(6)：JC055 结构 / **在目标上放置时间标志** / JC127 结构（两条 RemoveToken 的归属与范围、
  条件谓词与参数、群体模式） / **新谓词边界**（合计 3 → ≥3、≥2 满足、≥4 不满足、无地区不满足，且**别的地区不计入**） /
  **端到端：双方势力标志被清空 + 条件满足 → 该地区双方角色都被消灭** / **只有 2 个标志 → 不消灭**。
- 账本：卡接入数 **118→120**（新增 `JC055_annisiduola.cs` / `JC127_zhongju.cs`）；行统计 ✅232→**234**、⛔28→**26**。
- **仍缺**：JC055②（"移除 **X** 个时间标志作代价 + 本回合下一张**灾难牌**减费 X"）——需要"X 由玩家选择"的
  标志代价（现 `CounterCostSpec` 为固定数量）＋ 子类别限定的**一次性**减费（K1 动态值 X 与 `CostScope` 的子类别筛选）。
**57 计数器作发动代价（K5）→ JC044 灾难使者②**
- 卡文：JC044「**行动，自本方卡牌上移除一个时间标志：遏制1**。（目标玩家选择并移除一个他所拥有的势力标志）」
- 口径（**用户 2026-09，§7.4b A17/A27**）："移除一个时间标志"**就是发动代价**（不是效果步骤）；
  由**发动者自主选择**移除**哪张卡**上的标志；代价在**入堆叠之前**支付。
- 实现（与既有"牺牲代价"同款结构，便于维护者对照）：
  - 新声明 **`CounterCostSpec{ Markers[], Amount }`** → `EffectData.CounterCost`；`Markers` 为**白名单写法**
    （卡库对同一概念有 `"时间标志"`/`"时间标记"` 两种写法，命中任一即算）；
  - `EffectEngine.GetCounterCostCandidates(source, spec)`：候选＝**该发动者控制**、**在场上**（争夺区 ∪ 附属区）、
    白名单标志合计 ≥ `Amount` 的卡；顺序＝`GetAllInstances()` 顺序（与其它候选枚举一致）；
  - `EffectEngine.PayCounterCost(source, data, chosenUid, out paid)`：无候选 → **拒绝发动**；
    指定候选非法 → **fail-closed 拒绝**；否则按白名单顺序找"卡上实际持有的写法"扣减，扣不足也拒绝（无副作用）；
  - 接入点＝`ActivateAbility` 的**代价阶段**（3.6 牺牲代价之后的新 3.7 步），新增激活重载
    `ActivateAbility(..., sacrificeUid, targetAreaId, counterCostUid)`（`counterCostUid`＝View 选择结果，空＝确定性兜底）；
  - 候选侧门禁：`ActivateEligibility` 两处"可发动能力"过滤 + 新增公开 `GetCounterCostCandidates`
    （与权威侧同源，不会分叉）；
  - 效果本身复用既有 **`Curb{"1"}`**（遏制1：**被遏制者**＝缺省＝施术者对手，由他选择并移除一个自己拥有的势力标志，
    `CurbRule` 三条决策路径早已交付）。
- 测试 `P3CounterCostTests`(6)：结构 / **候选只数本方场上带标志的卡**（本方无标志卡与敌方带标志卡都不算）/
  **无候选 → `CanActivate` 为 false 且发动被拒、不入堆叠** /
  **有候选 → 先扣标志（2→1）再入堆叠** / **指定非法候选 → fail-closed、不扣标志、不入堆叠** /
  **`"时间标记"` 另一种写法同样识别并扣除**。
- 账本：行统计 ✅231→**232**、⛔29→**28**（本卡两行全 ✅ → 移出"部分接入"集合）。
**56 临时费用池（K19）→ JC115 备用资金**
- 卡文：JC115「**快速行动，横置：你获得1**。」（附属卡，子类别「资源」）
- 口径（**用户 2026-09，§7.4b A26**）：本作**没有"有色费用"**——**所有费用都是无色费用**；这 1 点是**临时费用**，
  **在任意步骤结束时消失**（先手 / 后手 / 预对抗 / 调查对抗 / 战斗对抗 / 势力对抗 六处步骤末）；
  **支付时优先花费临时费用**。
- 实现：
  - `PlayerManager` 新增**临时费用池**：`GetTemporaryMana(playerId)` / `GrantTemporaryMana(playerId, n)`（可叠加）/
    `ClearTemporaryMana` / `ClearAllTemporaryMana`；
  - `GetAvailableMana` ＝ 未横置资产卡数 **＋ 临时费用**（所有"费用够不够"的判定自动含临时费用）；
  - `SpendMana`（**费用支付的唯一收口**）：**① 先花临时费用**（记日志、扣池）→ ② 不足部分横置资产卡；
  - **清除点**：`TurnManager.EnterPhase` 每次阶段流转时 `ClearAllTemporaryMana()` ——
    每次阶段流转恰好等于"上一个步骤结束"，覆盖用户点名的六个步骤（也覆盖回合内的其它步骤边界）；
  - 新动词 **`ActionVerb.GrantTemporaryMana`**（`Args[0]`＝数量，缺省 1）→ JC115 的激活能力
    （`AttachmentActivated` + `ActivateWindow="快速"` + `ActivateCondition="横置本角色"`）。
- 测试 `P3TemporaryManaTests`(5)：结构 / **结算后可用费用 +1** /
  **支付优先花临时费用（资产一张未横置）＋ 混合支付只横置不足部分** /
  **步骤结束（双方连续让过推进阶段）→ 双方临时费用一并清除、可用费用回到＝未横置资产数** /
  **临时费用不足仍拒绝支付**。
- 账本：卡接入数 **117→118**（新增 `JC115_beiyongzijin.cs`）；行统计 ✅230→**231**、⛔30→**29**、部分接入 15→**14**。
- **仍缺**：联机快照（`PlayerSnapshot`）尚未含临时费用字段（双端同解需补；本轮先落单机/headless 权威口径）。
**55 「道途」唯一性（K25 场上唯一性）→ JC034 心兽之道①**
- 卡文：JC034「结附于本方秘社。**你只能操控至多一个道途**。」（本卡 `SubCategories = ["道途","吸血鬼"]`，类别＝附属）
- 口径（**用户 2026-09，§7.4b A25**）：**与【领袖】类似**——场上存在多个道途时，**由该玩家选择一个牺牲**。
- 实现（刻意与既有 `LeaderRule` **逐条对齐**，便于维护者对照）：
  - 规则声明：静止标记 **`StaticKind.UniquePathAtMostOne`**（`Timing = Static` + `StaticEffects`），
    **只印在 JC034 上** → **未印该规则的「道途」卡互不限制**（`PathRule.RuleDeclared` 查该控制者场上是否存在带标记的道途）；
  - **`PathRule`**（新建，`Script/Manager/PathRule.cs`）：`IsPath`（按"当前实际子类别"判「道途」）、
    `PathsOnField`（该控制者场上＝`AttachedZone`（结附秘社）∪`AreaZone`，确定性顺序）、
    `EnforceOnEntry` → `EnforceInvariant`（循环让控制者选一个牺牲直到只剩 1 张，防呆上限 8 轮）；
  - 三条决策路径：**单机** `IGameUIHooks.PromptPickCard`（`allowCancel=false`，候选＝全部道途）／
    **联机** `ChoiceRequest.Kind.SelectTarget`（强制选择 + 服务端权威复检，回包非法/超时→确定性兜底）／
    **无 UI·无网关**（headless/测试）→ 确定性兜底＝**优先牺牲最新进场者**；
  - 落地＝`CardManager.ResolveDeath(card, DeathCause.Sacrifice)`（**规则性结算：不入堆叠、不可被响应**；
    **牺牲＝死亡，照常广播死亡触发**；与 A1 一致）；
  - **消费点**＝`ZoneManager.OnPostMove`（装配于 `GameInitializer`）——道途是"结附于本方秘社"的附属卡，
    落 `AttachedZone`，**不经过** `OnCardEnteredArea`（那只覆盖争夺区进场），故用通用移动钩子覆盖进场/回场/移动。
- 测试 `P3PathUniquenessTests`(5)：结构（本卡声明静止标记且自身是「道途」）/
  **两张道途（无 UI）→ 兜底牺牲最新进场者、先进场者留下** /
  **单机按玩家选择牺牲**（并断言询问次数、候选数、不可取消）/
  **场上无卡声明该规则时两张道途互不限制** / **不同玩家各 1 张互不影响**。
- 账本：卡接入数 **116→117**（新增 `JC034_xinshouzhidao.cs`）；行统计 ✅229→**230**、⛔31→**30**。
**54 用户口径批（三张小卡）→ JC022 引路仙子② / JC074 天空的教诲 / JC122 哈克·亚当斯②**
- 口径来源：用户 2026-09 一次性裁定（**A11–A20**，见 §7.4b），本批取其中"无需再澄清"的三条落地。
- **JC022 引路仙子②**（"参与了该次战斗"）：**A14** 按近似判定＝**位于战斗地区且属于获胜方** →
  直接复用既有谓词 **`WinnerIsSelfInSameArea`**（① 本卡控制者＝获胜方 ② 本卡所在地区＝获胜地区），
  时机 `OnCombatWon`（触点携带胜者玩家 `TriggeringPlayerId` + 战斗地区 `TargetAreaId`），动作 `Draw{1}`（真·抓牌）。
  **无需新谓词**（原 ⛔ 行记的"缺参战者名单"由用户口径消解）。
- **JC074 天空的教诲**：**补齐此前的不实 ✅**（该行原声称已实现，但全仓无脚本、无注册、无测试断言）——
  `LimitToActionPhaseOnly=true`（快速行动）＋ `Gain{"黑眼","2","目标"}`（图标名经 `IconToAttribute` 折成**调查 +2**）
  ＋ `Gain{"声望","1","目标"}`（关键词「声望」，规则本体由引擎级 `RenownRule` 承担），`Duration="直到回合结束"`
  → 两笔都登记 `TimedMod`，回合结束经 `ExpireTurnEndEffects` 自动还原。
- **JC122 哈克·亚当斯②**（"不能翻面"）：**A18** 只封**现身**方向、且只对**带锁定标记的暗藏者**生效 →
  新静止规则 **`StaticKind.NoRevealWithLockCounter`**（`Param="锁定标记,锁定标志"`，卡库两种写法都认）：
  · 登记：挂 `Timing=Continuous` 的 `ContinuousPrereq="常驻"` 效果上，经 `SetStaticRuleRegistrations`
    （**注意：新静止种类必须同时加进 `HasAnyStaticRule` 白名单，否则整条不会登记——本轮实测踩到**）；
  · 注销：`ClearAllStaticRulesFor` / 离场 / 前提丧失；
  · 消费点：`EffectEngine.IsRevealBlocked(card)`（要求 ①暗藏者 ②带锁定标记 ③来源在场未死亡）
    ← `TurnManager.RevealCovert`（现身的唯一玩家入口）；潜伏/翻面为暗藏者**不封禁**。
- 测试 `P3RulingBatchTests`(7)：JC022 结构 / **端到端赢下本地区战斗 → 抓一张且广播 `OnDraw`** /
  **敌方获胜与地区外获胜均不触发** / JC074 结构 / **黑眼×2＋声望生效且回合结束还原** / JC122 结构 /
  **锁定暗藏者被拒现身（生产路径）→ 来源离场后放行**。
- 账本：卡接入数 **114→116**（新增 `JC022_yinluxianzi.cs` / `JC074_tiankongdejiaohui.cs`）；
  行统计 ✅227→**229**、⬜3→**1**（JC113「每位玩家各牺牲一个附属」、JC121「不能移动」由用户口径**完成归类**，转为 ⛔ 待实现）。
**53 非抓牌式取牌（取牌 ≠ 抓牌）→ JC049 深渊细语②**
- 卡文/FAQ：JC049「快速行动：将你牌库底的两张牌**置于手中**」；FAQ「◆通过深渊细语从牌库底获得的手牌，
  **并不算抓牌**。」
- 问题：既有 `ActionVerb.DrawFromBottom` → `TurnManager.DrawFromBottomForPlayer` **一律广播 `OnDraw`**
  （注释里也写着"从牌库底抽牌同样属于抓牌"）→ 与 FAQ 冲突："每当你抓牌时…"类效果（JC080 等）会被误触发。
- 实现：
  - `DrawFromBottom` 增**可选标记** `"不计抓牌"`（`Args` 里出现即生效，沿用 `HasArg` 的既有写法，
    不占用数量位：数量仍是 `Args[0]` 的整数，缺省 1）；
  - `TurnManager.DrawFromBottomForPlayer(int playerId, int count, bool countAsDraw = true)`：
    `countAsDraw=false` → **不广播 `OnDraw`**；其余行为（取牌、入牌库底→手牌、界面通知）不变；
  - **空库语义（本批的显式定档，供用户复核）**：判负规则绑定"**抓牌**"这一行为，本路径只是把牌拿走 →
    `countAsDraw=false` 时**取到多少算多少、不判负**；`countAsDraw=true`（既有口径）仍空库判负；
  - **界面通知 `OnCardDrawn` 两条路径都照常**（它是 View"把 CardUI 从牌库取出"的通知，与触发时机无关；
    若日后要按"抓牌"驱动 UI，应改看 `OnDraw`）。
- 新增卡脚本 `JC049_shenyuanxiyu.cs`（注册入 `JCEffects` → 卡接入数 113 → **114**）；本卡第 1 条
  「额外费用：牺牲一个角色」仍属 **K10 额外费用**（用户未排期）→ 按"效果级推进"纪律只接入第 2 条。
- 测试 `P3DrawFromBottomTests`(4)：结构 / **端到端：效果结算 → 牌库底两张进手牌且 `OnDraw` 计数不变** /
  **对照：缺省口径仍广播一次 `OnDraw`（一次调用＝一批）** / **数量缺省＝1（标记不占数量位）**。
  测试装配注记：`TriggerRegistry` 构造时会 `SetDispatch(HandleEvent)` 占用总线分发，观察总线需**在构造之后**
  再包一层"先记录、再转发"的分发器（本轮踩到并修正）。
**52 K3 死亡类第一批（触发路线）＋ 条件判定上下文卡修复 → JC105 噩梦领主②**
- 卡文：JC105「触发：当**另一个本方灵体角色**死亡时，你可以支付2，若如此做，则**将该角色封印在目标角色上**，
  而非置入墓地。」
- 口径（**用户 2026-09，§7.4b A10**）：**走触发路线**（不是替代式死亡处置）——角色**先进入墓地**，
  再由该触发把它**取出**并封印到玩家选定的宿主上；卡文"而非置入墓地"＝最终结局，不是"绕过墓地"。
- 实现：
  - 新谓词 **`DiedAllySpiritCharacter`**：死亡卡是**角色** ＋ 控制者＝来源控制者（"本方"）＋ **排除来源自身**（"另一个"）
    ＋ 带关键词**灵体**（运行期关键字 ∪ 卡面固有，与光环筛选同一口径）；
  - **动作主体 `"触发卡"` / `"死亡角色"`**（新）：解析为**本次触发的上下文卡**。数据通路＝
    `TriggerEmitPoints.OnCardDied` 写入 `TriggerContext.ContextCard`（＝死亡的角色）→ `TriggerRegistry` 收集时
    落到 `StackEffectInstance.TargetCardUid` → `TurnManager` 结算 `ResolveKind.Trigger` 条目前取
    `entry.StackEffect?.TargetCardUid` 下发给新字段 **`EffectEngine.TriggerContextCard`**
    （激活条目无此值 → null；`ResolveStackTop` 入口先清空，避免上一次触发的残留串味）；
  - `ResolveSeal` 增分支：被封印者＝上下文卡（从**墓地**取出）→ 走唯一出口 `TurnManager.SealCard` →
    `ZoneManager.MoveToSealedZone`（内部 `RemoveFromAllZones` 会把它移出墓地桶）→ 进入宿主封印区
    （`SealedBy`＝宿主、宿主 `SealedCards` 收录）；
  - JC105② 卡数据：`OnCardDied` + `Forced=false`（"你可以"）+ `PayOptional{2,2,2}`（"支付2"：下限＝上限＝2，
    只有"支付/不支付"两种结果；无 UI → 兜底"不支付"，绝不替玩家花钱）+ 目标＝**目标角色**（宿主，入堆叠时选定）+
    `Seal{"触发卡","目标"}`。
- **顺带修的引擎级缺陷（关键：否则本卡在生产路径必然失效）**：结算时的**条件门禁**原先把 `target` 当上下文卡——
  `EvaluateCondition(pred, source, target, target, …)`。当该触发有**玩家选定的卡目标**时，`entry.TargetCardUid`
  已被所选目标覆盖，于是死亡类条件在结算时**对着宿主重判**（实测日志：
  `条件「DiedAllySpiritCharacter」：死亡卡=探针_host 是否灵体=False → 不满足`）→ 整条效果被判失败。
  修法：门禁的上下文卡**优先取触发上下文卡**，无则维持既有回落到 `target`（同步/异步两条路径同改）。
  说明：JC031 这类"以玩家为目标"的触发原不受影响（`entry.TargetCardUid` 仍是上下文卡）。
- 测试 `P3DeathSealTests`(6)：结构 / **端到端：支付2 → 死亡角色离开墓地、进入选定宿主的封印区**
  （并断言扣费 2、询问区间 2..2） / **不支付 → 照常留在墓地且不扣费** / **非灵体角色死亡不触发** /
  **敌方灵体死亡不触发（"本方"限定）** / **来源自身死亡不触发（"另一个"限定）**。
- **JC124 尼古拉斯·勒梅（死亡类第二批）已由用户裁定「延后制作」**（2026-09，A10）：本批不做；技术缺口备查——① 卡文写"将其从**墓地移回场上**，并由其拥有者操控"，
  与 A10 中"落点＝手牌"的答复**冲突**（按纪律不猜，需再确认）；② 卡文"该能力在尼古拉斯·勒梅**离场后仍然生效**"
  需要**持久登记**机制（`UnregisterPersistent` 目前离场即撤销，见 §7.9 #12），本批未做。

**51 触发类「选择一项」（K16 触发版）＋ 封印区生效区缺口修复 → JC110 闲人的绿地② / JC100 尖牙幻象（救活）**
- 卡文：JC110「触发：当闲人的绿地被封印在一个角色上时，**你可以横置或重置该角色**」。
- 口径（**用户 2026-09**，§7.4b A5）：由**发动者**选择；选择时机＝**入堆叠时选定**（与卡牌模式"打出时选定、
  入堆叠不可更改"、K15"目标地区入堆叠时锁定"同一口径）——**不是结算时**再弹窗。
- 实现：
  - `EffectData.Modes` 用于**触发**：`StackEffectInstance.ModeIndex`（-1＝无/未选）→ `PushTriggerToStack`
    写入 `StackEntry.ModeIndex` → 结算只执行该模式（**结算/目标复检路径原已按 `ModeIndex` 实现，无需改动**）；
  - **单机**：`TurnManager.EnsureTriggerModeThenPush(t, onReady, onSkip)`——入堆叠**之前**开
    `IGameUIHooks.PromptEffectMode` 选择窗（先模式、再取目标、最后入堆叠）；**无 UI（headless/测试）→
    确定性兜底第一个模式**并打警告；**取消/非法 → 该触发不入堆叠**（与"无合法目标"同一结果，绝不静默代选），
    且流程继续到下一个触发（`onSkip` 与 `onReady` 分离）；
  - **联机**：`TurnManager.EnsureTriggerModeAsync`（`EnsureTargetThenPushAsync` 的第一步）——
    下发 `ChoiceRequest.Kind.SelectEffectMode`（与卡牌模式**同一请求类型/客户端面板**），取消/超时/序号非法 → 不入堆叠；
  - **动作主体**：`Tap`/`Untap` 新增可选主体 `Args[0]`（缺省 `"目标"`），支持 `"结附宿主"`（`AttachedTo`）
    与 **`"封印宿主"`**（`SealedBy`，与 JC100 的 `Gain` 同一写法）——卡文"该角色"＝**被封印到的角色**；
  - JC110② 卡数据：`OnSealed` + `TriggerBySelf` + `Scope=SealedZone` + `Forced=false`（卡文"你可以"）+
    两模式（`横置该角色` = `Tap{"封印宿主"}` / `重置该角色` = `Untap{"封印宿主"}`）。
- **顺带修的引擎级缺口（重要）**：`ZoneScope`/`ScopeMatchesZone` **没有"封印区"分支**，而
  `TurnManager.SealCard` 是**先**把被封印卡移入封印区、**再**广播 `OnSealed`（`SourceZoneAtTrigger = SealedZone`）
  → "被封印时"自身触发的来源卡此刻在封印区、其默认生效区（角色=争夺区 / 附属=附属区）不匹配 →
  **该类触发在生产路径上永远收集不到**。证据：P3OptionalPayTests 的 JC100 用例是"手工 `ResolveEffect`"，
  从未走过收集路径 → **JC100 的既有 ✅ 行实际是死的**。修法：新增 `ZoneScope.SealedZone` +
  `ScopeMatchesZone` 分支；JC110/JC100 的该效果声明 `Scope = SealedZone`；回归锁＝
  `TestTriggerMode_JC100被封印时触发可收集`（真走 `SealCard` → 断言入堆叠）。
- 测试 `P3TriggerModeChoiceTests`(7)：结构（2 模式 + `Tap/Untap{"封印宿主"}`）/ **无 UI 兜底模式1 → 横置宿主** /
  **单机选模式2 → 重置宿主**（并断言询问次数/模式数/来源卡）/ **单机取消 → 不入堆叠** /
  **联机 `SelectEffectMode` 请求 + 入堆叠记录** / **联机取消 → 不入堆叠** / **JC100 封印区收集回归锁**。
- **仍缺**：JC082/JC128 的模式接线；触发模式"目标声明随模式变化"的取目标口径（JC110 两模式均无目标，未涉及）；
  联机触发模式的客户端面板交互（复用既有 `SelectEffectMode` 面板，未做新 UI）。

**㊿ K3 去处替代第二批：去处值扩展到「资产区」→ JC120 庄严圣像（并修正一条失效实现）**
- 卡文：JC120「结附于目标地区 触发：当你赢取本地区时，将庄严圣像置入你的**资产区**，令其成为一个资产。」
- 问题（**走生产路径才暴露**）：原实现＝`OnAreaWon` 触发 ＋ `MoveToZone{"自身","资产区","正面"}`，
  **手工结算该效果时通过、真走赢取结算时必然失效**：`AreaManager.ResolveAreaWon` 里"本地区卡归位"是**同步**
  跑完的（结附地区的附属卡那一轮直接 `MoveToDeckBottom`），而入堆叠的触发是**异步**结算的——触发结算时
  庄严圣像已经躺回牌库，`MoveToZone` 因"本卡已不在场"被跳过（同 JC079 的失效机理）。
- 实现（**沿用 JC079 已批准的替代效果口径**，见 ㊲）：
  - `EffectData.WinAreaDestination` 之处去值新增 **`"资产区"`**；
  - `AreaManager.MoveToScoreIfReplaced` → 更名 **`TryApplyWinAreaReplacement`**（去处分派：`计分区` → `MoveToScore`；
    `资产区` → `SetCovert(false)` ＋ `MoveToAsset(card, 赢取者)`，与 `MoveToZone{"自身","资产区","正面"}` 同口径；
    未识别的去处声明 → 按普通归位处理）。两处调用点（顶层卡 / 结附地区的附属卡）不变；
  - **JC120** 改为 `Timing = Static` ＋ `WinAreaDestination = "资产区"` ＋ `Condition.Predicate = "WinnerIsSelfInSameArea"`
    （`EffectId`：`JC120_A_WinAreaToAsset`）。
- 旁带硬化：`TurnManager.RunTriggerPhase` 在**触发排序视图缺失**时降级为"按收集顺序入堆叠"并打警告
  （原先直接 NRE；单机/联机之外的调用方——含测试直接调用该阶段——不再崩）。
- 测试 `P3ZoneConvertTests`(6)：结构（`Static` ＋ 去处值）/ **判定入口三条**（本方赢取本卡所结附地区 → `资产区`；
  敌方赢取 → 无替代；赢取**别的**地区 → 无替代；判定不移动卡）/ **生产路径 e2e**
  `TestZoneConvert_JC120赢取生产路径`（真调 `ResolveAreaWon` → 断言进**资产区**、按区域视作资产、归属赢取者、正面、
  已离开该地区附属区）。
- **用户裁定（2026-09，见 §7.4b A9）**：**确认沿用 JC079 口径走替代效果**；**不做**"先结算触发再归位"的时序改造。

**㊾ 激活能力的"目标地区"（K15 扩展）→ JC117 红眼航班 / JC033 家族传承①**
- 卡文：JC117「快速行动1，横置：将**目标本方角色**移动到**目标地区**。该能力只能在行动阶段发动。」
  FAQ：**必须进入另一个地区**（不能原地移动）；JC033①「行动1，牺牲家族传承：从牌库找一张吸血鬼角色牌，
  **展示**后以**暗藏者**状态置于**目标地区**，然后洗牌」。
- 问题：激活路径原先只能带**一个**目标（卡/玩家），**没有"地区目标"**；`targetAreaId` 只在打出/触发路径可用。
- 实现：
  - 新声明字段 **`EffectData.NeedsAreaTarget`**（激活额外需要一个地区）＋ **`AreaTargetExcludesCardArea`**
    （目标地区须不同于卡目标当前所在地区＝JC117 FAQ"不能原地移动"）；
  - `EffectEngine.ActivateAbility(..., sacrificeUid, targetAreaId)`（新重载）：**2.7 步前置门禁**（先于付费/横置）——
    无合法地区 → 拒绝；未指定 → 确定性兜底第一个合法地区；指定不合法 → 拒绝；
  - **入堆叠时锁定**：`PushActivationToStack(..., targetAreaId)` → `StackEntry.TargetAreaId` ＋ 地区箭头
    （`StackTargetRef.RefKind.Area`）→ 结算时作为 `ResolveEffect` 的 `targetAreaId` 下发（与机动/事务卡同一通路）；
  - **候选** `EffectEngine.GetLegalAreaTargets(source, data, cardTarget)`（地区来源＝`AreaManager.GetOrderedAreaUids()`，
    与对抗阶段同一份；选定卡目标后按"禁原地"过滤）；`ActivateEligibility.GetAreaTargetCandidates` 供 View 用；
    可发动性门禁：无可选地区 → 不可发动；声明"禁原地"时**至少要存在另一个地区**（卡目标未选定 → 必要条件）；
  - **View**：`Arena.BeginActivate` 分派新增 `BeginActivateAreaSelection`（地区选择窗 → 再走代价窗 → 最后
    `ActivateAbility(..., areaUid)`）；代价窗也带上已选地区（JC033① 同时有牺牲代价与地区目标）；
  - **检索落点**：`ResolveSearchArea` 改为**显式目标地区优先**（JC033①"置于**目标地区**"），未给定时回退来源地区（原语义不变）。
- 覆盖 **JC117**（行动阶段 ＋ 横置自身 ＋ 费用1 ＋ 移动到**另一个**地区）与 **JC033①**（牺牲自身 → 检索吸血鬼角色牌 →
  展示 → 暗藏进场到目标地区 → 洗牌）；测试 `P3ActivationAreaTargetTests`(5)：两张卡结构 / 候选地区排除原地 /
  **端到端 JC117**（移动到选定地区；指定"原地"被拒）/ **端到端 JC033①**（牺牲自身、目标牌落在选定地区且为暗藏状态）/
  无合法地区时拒绝且**未付出横置代价**。
- **已补齐（2026-09，见 §7.10 79 / §7.4b A50）**：~~联机路径的"激活 + 地区目标"（联机激活类效果整体尚未接线）~~
  ⇒ 联机激活现由**服务端接管**：目标地区（`SelectTarget`，地区槽位高亮）/ 牺牲代价 / 标志代价（含逐卡分配）/
  玩家目标（`SelectPlayer`）全部 1:1 复刻单机窗；无选择类入参的能力仍走原有同步路径。

**㊽ 运行期授予的**激活能力**（K13）→ JC019 弥尔顿的镇魔枪**
- 卡文：结附于目标角色 持续：受此结附的角色获得［黑刀］，**并获得"快速行动，横置：此角色对位于本地区的目标
  非人类角色造成1点伤害，该伤害不能被防止"的能力**。 FAQ：用该能力要横置**它所结附的角色**，而非武器本身。
- 口径（**用户 2026-09**）：宿主原本就有激活效果时，**在既有的"多激活效果选择窗口"里额外增列 1 项**。
- 实现：
  - 新数据结构 **`EffectData.GrantedAbility`**（内嵌一条 `EffectData`）：带它的**持续效果**会把这条激活能力授予
    **授予落点**（`GrantToHost` → 宿主；否则来源自身）。
  - **统一重算** `EffectEngine.ReapplyGrantedAbilities()`：清空后按局面重建（幂等）；扫描争夺区 + 附属区上、
    非死亡、非"文本视作空白"的卡，逐个检查其 `GrantedAbility` 效果（生效区 + `EvaluatePrereq` 都过）
    → 记到宿主名下（键＝来源卡 UID + 效果 Id，便于精确收回）。
    **触发点**：`GameInitializer` 的 `OnPostMove` 统一后置刷新（任何移动都会经过）+ `ReevaluatePrereqs` 尾部。
    注：只挂 `ReevaluatePrereqs` 不够——该方法是"本玩家有带前提的持续效果"时才继续，会漏掉重算（本轮踩到）。
  - **候选与门禁**：`ActivateEligibility.GetAllActivateAbilities(card)` ＝卡面固有激活能力 **∪** 运行期授予项，
    被 `GetEligibleAbilities` / `CanActivate` 共用 → **选择窗口自然"增列 1 项"**，且高亮/候选/权威门禁三处一致。
  - 能力以**宿主**为来源 → `ActivateCondition="横置本角色"` 横置的是**宿主**（与 FAQ 一致）。
  - **"该伤害不能被防止"**：`DealDamage` 新增可选参数 `Args[1] = "不可防止"` →
    `CardManager.DealDamageToInstance(..., unpreventable: true)` **跳过 `DamagePreventer`**（灵体 + 伤害护盾）
    直接落地；**重定向不受影响**（那是"改由谁承受"，不是"防止"）。
- 覆盖 **JC019**；测试 `P3GrantedAbilityTests`(5)：结构 / 宿主获得黑刀（**图标通道 + 属性通道都写**）且候选里
  增列 1 条 / 发动时**横置宿主**并造成**穿透护盾**的 1 点伤害 / 对照（普通伤害会被护盾防止）/ 武器离场后能力收回。
- **顺带修掉的真缺陷**：**JC116 柯尔特巨蟒左轮**原先只授 `Kind="Icon"`（只登记图标名、**不改数值**）→
  宿主"显示有黑刀却不多打 1 点"。现改为**两条通道都写**（`Icon` 供图标存在性/展示与持续授予自愈判定，
  `Attribute:战斗` 供数值），既有 `P3GrantToHostTests` 断言不变、新增数值断言由本轮套件覆盖。

**㊼ "印刷文本框视作空白"＝失去效果（K20 收尾）→ JC101 直视深渊**
- 卡文：结附于目标角色 持续：受此结附的角色，其**印刷文本框视作空白**，且**失去所有魔法领域图标**。
  （**不影响能力栏内的能力图标**）
- 口径（**用户 2026-09**）：文本空白 ＝ **该卡失去效果**，但**不影响已经发动的效果**。
- 实现：
  - `EffectData.TextBlank = true`（与 `DomainSuppression.Victim = AttachedHost` 写在同一效果里，共用同一套统一重算）；
  - **失去效果** ＝ 四处门禁 + 一次注销：
    ① `RegisterPersistent` 早退（不再注册任何持续/静止效果）；
    ② `ActivateEligibility.CanActivate` / `GetEligibleAbilities` 跳过（不可发动、不出现在候选/高亮里）；
    ③ `TriggerRegistry.HandleEvent` 的收集循环跳过（不参与触发收集）；
    ④ 施加瞬间 `UnregisterPersistent(宿主)` → **已注册的持续效果立刻停止**（其光环/授予一并还原）；
    ⑤ `EffectEngine.ActivateAbility` 权威门禁拒绝（防越权路径）。
  - **"不影响已经发动的效果"**：**结算路径不加任何检查** —— 已在堆叠上的条目照常结算（有专门用例锁定）。
  - 还原：解除结附/附属离场 → 统一重算：**先摘掉空白标记、再 `RegisterPersistent`** 重新注册其效果
    （顺序很关键：标记没摘就重注册会被 ① 的门禁挡回去，效果永远回不来——本轮踩到的坑）。
- 覆盖 **JC101**；测试 `P3TextBlankTests`(6)：结构 / 结附后宿主失去领域图标且被标记文本空白 /
  **已注册的持续效果停止**（宿主自身 +1 战斗消失）/ 不能发动（候选侧 + 高亮侧）/ **解除结附后恢复**
  （领域图标与持续效果一起回来）/ **已经在堆叠/已经发动的效果照常结算**（不回溯撤销）。

**㊻ 持续型"失去魔法领域图标"（K20）→ JC099 徘徊的鬼魂③**
- 卡文：持续：只要徘徊的幽灵在场且**未横置**，所有**其上有牌封印的敌方角色**失去**魔法领域图标**。
- 实现：
  - **领域图标抑制**（`CardInstance`）：`SuppressAllDomains()` / `RestoreDomains()` + `AreDomainsSuppressed`；
    **关键点**：`ResetToBaseValues` 重建领域集合时**跳过重填**（抽出 `RebuildDomains()`）——否则任何一次属性重算
    都会把图标"复活"（灵体防止、目标筛选等都读 `MagicDomains`，抑制一处即全链生效）。
  - 新数据结构 **`EffectData.DomainSuppression` → `DomainSuppressionSpec.Victim`**：
    · `EnemyCharacterWithSealedCards`（JC099）：**敌方**（与本卡控制者不同）+ `SealedCards` 非空 + **角色**；
    · `AttachedHost`（JC101，通道已就绪待用）：本卡**结附的宿主**。
  - **前提**：JC099 用既有 `PrereqKind.SelfUntapped`（"只要…未横置"）——横置时 `ReevaluatePrereqs` 注销该来源
    → 统一重算即恢复；竖置再抑制。
  - **统一重算** `EffectEngine.ReapplySuppressions()`：先全部还原、再按局面施加（幂等）；
    触发点：来源登记/注销（静止规则统一入口）、`ReevaluatePrereqs`（封印落地/解除、横置竖置、进出场都经此）。
- 覆盖 **JC099**；测试 `P3DomainSuppressionTests`(6)：结构（含前提＝自身未横置）/ 敌方有封印者失去领域 /
  本方与无封印者不受影响 / **来源横置即失效、竖置再抑制** / 封印解除与来源离场后恢复 /
  **抑制期间属性重算不会让图标复活**。

**㊺ 持续型"失去图标"（K20）→ JC123 饮魂者朱迪②**
- 卡文：持续：只要饮魂者朱迪在场，所有具有**锁定标记**的角色或暗藏者失去所有［黑星］和［白星］能力图标。
- 与一次性 `ReplaceIcon` 的区别：这是**在场期间持续抑制**，受影响集合随场上变化重算，来源离场/标志消失即恢复。
- 实现：
  - 新数据结构 **`EffectData.IconSuppression` → `IconSuppressionSpec{Icons, RequireCounters}`**：
    `Icons`＝要失去的能力图标清单（经 `IconToAttribute` 映射到属性）；`RequireCounters`＝对象须带**任一**卡上标志
    （空＝不限对象）。**卡库命名说明**：`锁定标记`（JC123 自己写的）与 `锁定标志`（JC069 写的）是**同一概念**的
    两种写法 → 两种都登记、二者等效（否则两张卡互相看不见对方的标志）。
  - **统一重算** `EffectEngine.ReapplyIconSuppressions()`：① 先按记录（`_iconSuppressionApplied`：卡 UID → 已施加的
    `Attribute` 记录）**精确还原**上一次抑制；② 再收集在册来源（须**在场**＝争夺区、非死亡）的规格；
    ③ 逐张场上卡命中任一规格 → 抹掉其图标对应的**当前有效贡献**（`势力` 取 `CurrentPower`；
    `先手势力` 取授予的『先手势力』）并记录施加量。**幂等**：可反复调用，属性值期间变化也能被覆盖。
  - **触点点**：来源登记/注销（走静止规则统一入口）、**受影响卡增减标志**（`CardManager.AddCounter`/`RemoveCounter`）、
    新卡进场（`RegisterPersistent` 尾部，若有在册来源）、`ResetAll` 清表。
- 覆盖 **JC123**；测试 `P3IconSuppressionTests`(6)：结构 / 具标志者失去黑星（无标记者不受影响）/
  白星（含印刷先手值 + 授予值）同样失去且**本地区势力合计 = 0** / **标志移除后恢复** / **来源离场后恢复** /
  **重算幂等**（反复重算不会越抹越多，还原也精确回到原值）。
- **实现踩坑（值得记）**：抑制用 `Grant(attr, -n)` 施加 → 还原必须 `Grant(attr, +n)`；我第一次写成 `-g.Amount`，
  结果"还原"变成再扣一次（出现 `-2 / -4` 负值），被测试当场抓到。
- **仍缺**：**"印刷文本框视作空白"**（JC101 → 该卡**失去效果**（不影响已经发动的效果）＋失去**领域图标**）；
  JC099（光环：按"其上有牌封印"筛选敌方角色 → 失去**领域图标**）。领域图标走的是 `MagicDomains` 另一条通道，留作下一批。

**㊹ 图标替换＝属性替换（K20 第一批）→ JC106 酒神秘仪**
- 口径（**用户 2026-09**）：**图标替换就是属性替换**；这几张卡**都影响全部同类图标**（不挑不拣）；
  **任意顺序**完成全部替换即可；整个替换过程**包装在效果内部**（原子锁期间不会被其它触发插进来）。
- 实现：新动词 **`ActionVerb.ReplaceIcon`**（`Args[0]`＝来源图标清单，`,` 分隔；`Args[1]`＝目标图标；
  `Args[2]`＝对象：`"目标"`（缺省）/`"自身"`/`"结附宿主"`）：
  - 逐条来源图标 → 经 `IconToAttribute` 取属性名 → 计数＝该属性的**当前有效贡献**
    （`势力` 取 `CurrentPower`（含印刷黑星 + 先手时的印刷白星 + 授予势力）；`先手势力` 取授予的『先手势力』）；
  - `Grant(来源属性, -n)` + `Grant(目标属性, +n)`：**整块替换**，替换后该卡不再提供任何来源图标的属性；
    n ≤ 0 的图标跳过（无图标时不产生"凭空换出"的收益）；
  - `Duration` 含"回合结束"时逐条登记 **`TimedMod{Kind="IconReplace", Value="来源属性|目标属性", Amount=n}`** →
    到期**精确还原**（收回换来的图标、还回原图标）；本批给它加了专属到期分支。
- 覆盖 **JC106**（`ReplaceIcon{"黑星,白星","黑刀","目标"}` + `Gain{"灵体","1","目标"}`，两条都限于本回合）；
  测试 `P3IconReplaceTests`(6)：结构 / 黑星整块换黑刀 / 黑星+白星（含授予的白星）一起换 / 无势力图标时只获得灵体 /
  **回合结束精确还原**（势力与换来的黑刀都还原，灵体同为本回合时效）/ 对象写法 `"自身"` 可用。
- **仍缺**：**持续型"失去图标"**（JC123 是"只要朱迪在场，具锁定标记者失去黑星/白星" —— 需要在场期间持续抑制、
  离场即恢复，属"持续抑制"而非一次性替换）；**印刷文本框视作空白**（JC101 → 该卡**失去效果**（不影响已发动的效果）
  ＋失去领域图标）；JC099（按"其上有牌封印"筛选 + 失去领域图标）。

**㊸ 持续型"伤害改由本卡承受"（K3）→ JC077 庇佑圣灵 / JC121 空鸣御守**
- 口径（**用户 2026-09**）：这两张卡的伤害条目**实为「持续效果」**（卡库里写成"触发："属效果数据错误）；
  既不走入堆叠的触发，也不走"替代式效应"框架。
- 实现：
  - 新数据结构 **`EffectData.DamageRedirect` → `DamageRedirectSpec`**，受保护对象用枚举 `DamageRedirectVictim`：
    · `FriendlyNonSpiritLocal`（JC077）：本卡**所在地区**内、**同控制者**、**非灵体**的**角色**；
    · `AttachedHost`（JC121）：本卡**结附的宿主**；
    （`None` ＝ 不启用；未登记写法 fail-closed 不生效）
  - 登记：`EffectEngine.SetDamageRedirect` 走既有**静止规则统一入口**（`SetStaticRuleRegistrations` /
    `HasAnyStaticRule` / `ClearAllStaticRulesFor` / `ResetAll` 各加一行）；
  - 查询 **`ResolveDamageRedirect(victim)`**：遍历在册来源卡（在场、非死亡、非自身），按上述条件匹配；
    **承受方＝来源卡自身**；
  - 消费点 **`CardManager.DealDamageToInstance`**（伤害唯一收口）：新增重载
    `DealDamageToInstance(instance, amount, isTrauma, allowRedirect)` → 命中重定向则**递归把伤害交给承受方**，
    递归时 `allowRedirect: false`（**一次伤害只重定向一次**，防"你来我往"乒乓）；承受方自身的
    "**防止**"（灵体/护盾）在其落地时照常判定。
- 覆盖 **JC077 / JC121**；测试 `P3DamageRedirectTests`(7)：两张卡结构 / JC077 本地区本方角色（伤害转移到本卡）/
  JC077 边界（敌方 ✗、其它地区 ✗、**灵体角色 ✗**）/ 来源离场后失效 / JC121 结附宿主受保护（未结附者不受影响）/
  不来回乒乓 / **承受方『灵体』防止照常生效**。
- **测试踩到的两条真实规则交互（已记档，供后续参考）**：
  ① 两张卡的**印刷防御都是 1** → 被转移过来的伤害**几乎必定致死**（测试里为观察数值临时提高其防御）；
  ② 承受方是**灵体**：伤害落到它身上后仍要过一遍"灵体防止"（对手在本地区魔法领域图标**更少**就整次防止）——
     这正是这两张卡的强度来源，故测试里需要**先压制对手领域图标**才能观察到"伤害真的落到承受方"。
- **仍缺**：JC105/JC124 的**死亡替代**（按 A2：走触发、"先入墓后由触发重新结附"，实现前逐张与用户确认）。

**㊷ 地区级"略过对抗"（**本回合型**，K18 第二批）→ JC118 停战协议**
- 口径（**用户 2026-09**）："本回合中该地区略过**对抗阶段**"＝**三步骤（调查/战斗/势力）全部略过**。
- 实现：
  - 新动词 **`ActionVerb.SkipAreaCombat`**（`Args[0]` ＝对抗名清单，`,` 分隔；**缺省/空 → 三步骤全部略过**）；
    落点地区＝本次结算的 `targetAreaId`（打出时选定的目标地区；缺省回退来源所在地区并告警）；
  - `EffectEngine.RegisterAreaCombatSkipThisTurn(areaUid, names)`：登记
    **`TimedMod{Kind="AreaCombatSkip", TargetUid=地区UID, Value="调查,战斗,势力"}`**——
    注意 **`TargetUid` 存的是地区 UID**（时效栈此前只存卡 UID），回合结束由 `ExpireTurnEndEffects`
    出栈（本批给它加了**专属到期分支**，与 `DamageShield` 同款：登记项即时效载体，地区无卡面状态需还原）；
    同地区同清单重复登记**幂等**；
  - 查询 **`AreaSkipsCombat(地区, 对抗名)`** 改为两源**并联**：① 常驻静止规则（JC098 类，来源卡在争夺区期间）；
    ② 本回合时效登记（JC118）；消费点不变＝`TurnManager.ResolveCurrentAreaCombat`
    （**不结算对抗、仍给让过窗口**，流程照常推进）。
- 覆盖 **JC118**（`LimitToActionPhaseOnly` + 打出目标＝地区 + `SkipAreaCombat{"调查,战斗,势力"}`）；
  测试 `P3TurnAreaCombatSkipTests`(5)：结构 / 登记后三步骤全略过（且不影响他区）/ 清单式只略过指定对抗 /
  **回合结束失效** / **端到端**（对照：调查步骤开对抗窗口 1 次；实验：0 次，且仍正常推进到战斗步骤并同样跳过）。
- **仍缺**："额外一次**调查/战斗**对抗"的写法（现仅登记势力对抗）。

**㊶ K22 牺牲代价续（JC093 / JC096 / JC038）＋ `Gain` 主体写法 `"结附宿主"`**
- 口径补充（**用户 2026-09 追加**）：卡文写"牺牲**一个**角色"而**没写"另一个"**时，**可以牺牲来源卡自身**。
- 新增实现：
  - **`Gain` 主体写法 `"结附宿主"`**（`Args[2]`）＝`source.AttachedTo`（"**受此结附的角色**"），与既有
    `"目标"/"自身"/"封印宿主"` 并列；JC093 用它把黑刀/黑星授予宿主（`EffectAction` 注释同步）。
  - `SacrificeCostSpec.ExcludeSource` 只在卡文含"另一个"时置 true（JC094）；JC096/JC038 置 false。
- 覆盖：
  - **JC093 怨灵咒刃**（附属 / 装备·武器）：`SacrificeCost`（来源是附属、非角色 → 天然不含自身）＋
    `Gain{黑刀,1,结附宿主}` / `Gain{黑星,1,结附宿主}` ＋ `Duration="直到回合结束"` ＋ `MaxUsesPerTurn=2`（快速窗口）；
  - **JC096 阿格里帕之犬**：`Cost=1` ＋ `ActivateCondition="横置本角色"` ＋ 牺牲一个角色（**可牺牲自身**）→ `Draw{1}`；
  - **JC038 列恩·爱弗罗德**：`SacrificeCost{ SubCategories=[吸血鬼,奴仆] }`（未写"另一个"→ 列恩自身是吸血鬼，
    场上无其它合法目标时**唯一候选就是它自己**）＋ 目标＝**敌方玩家**（K24）＋ `Discard{1,"目标玩家","自选"}`（由被指定者自己选）＋ `Draw{1}`。
- 测试 `P3SacrificeCost2Tests`(6)：三张卡结构 / JC093 宿主获得黑刀+黑星且**回合结束撤销** / JC093 每回合两次上限（第 3 次被拒）/
  JC096 **牺牲来源自身**后仍抓一张 / JC038 子类别筛选（人类✗、奴仆✓）＋令对手弃牌并抓牌 / JC038 来源自身为唯一合法候选。
- **仍缺**：**对手/多玩家侧**的牺牲编排（JC046/JC134/JC128 属"由对手做选择"的新原语）；JC049 的额外费用牺牲（K10 未排期）。

**㊵ "牺牲一个角色"作代价（K22 第一批）→ JC094 威达的汀巴巴迪**
- 口径（**用户 2026-09 拍板**）：① 牺牲大多作为"效果/打出卡额外费用"的一部分，**由效果发动者自己选择**被牺牲者；
  ② **除非效果里明确写了"暗藏者"，否则不能牺牲暗藏者**；③ **牺牲可以触发死亡触发**（牺牲＝死亡）。
- 实现：
  - 新数据结构 **`EffectData.SacrificeCost` → `SacrificeCostSpec`**：`SubCategories`（子类别白名单，空＝不限）、
    `LocalZone`（限来源卡所在地区）、`ExcludeSource`（"**另一个**角色"→排除来源自身）、
    `AllowCovert`（默认 false＝不可牺牲暗藏者）、`RequireTapped`（预留）；
  - **候选单一实现** `EffectEngine.GetSacrificeCandidates(source, spec)` ＋ **合法性单一判定** `IsLegalSacrifice(...)`
    （口径：本方控制、争夺区、非死亡流程、角色、子类别命中、本地区/排除自身/暗藏者按 spec）；
    候选侧（`ActivateEligibility.GetSacrificeCandidates`）与权威侧共用，不会分叉；
  - **代价支付** `PaySacrificeCost(source, data, chosenUid, out sacrificed)`：`chosenUid` ＝发动者选择结果，
    空 → **确定性兜底第一个合法候选**（与其它"无 UI 兜底"同款纪律）；随后
    `CM.ResolveDeath(pick, DeathCause.Sacrifice)` → **牺牲＝死亡，死亡时机照常触发**
    （复用既有唯一死亡入口，不另造路径）；
  - **前置门禁（2.6 步，先于付费/横置）**：无合法候选、或指定候选不合法 → **拒绝发动且无副作用**；
    `ActivateEligibility.CanActivate` / `GetEligibleAbilities` 同解（UI 据此置灰）；
  - **View**：`Arena.BeginActivate(...)` 成为激活分派的**统一入口**（双击/选框/拖拽落点全部改经此处），
    带牺牲代价的能力先弹 `BeginActivateWithSacrifice` 选择窗（列出候选 → 选完带 UID 调 Controller 复检/支付）；
    `PlayerActionService.ActivateAbility(..., sacrificeUid)` 透传。
- 覆盖 **JC094**；测试 `P3SacrificeCostTests`(7)：结构 / 候选过滤（本方同地区✓、自身✗、他区✗、暗藏者✗、敌方✗）/
  无候选不可发动且无副作用 / **端到端**（代价先付→被牺牲者立刻进墓地、主效果仍在堆叠上、结算后目标被消灭）/
  牺牲触发死亡时机（`OnCardDied` 广播）/ 指定非法候选被拒（fail-closed）且不改动其它候选 / 未指定候选时确定性兜底。
- **仍缺**：JC038/JC093/JC096 的"**一个**角色"是否**含来源卡自身**——卡文未写"另一个"，
  按用户"不清楚先问"的要求**待确认后**再写；对手/多玩家侧的牺牲编排（JC046/JC134/JC128）。

**㊴ 地区级"额外进行一次势力对抗"（对抗流程改造）→ JC067 约翰·F·纳特里②**
- 问题：对抗三步骤由 `TurnManager` 严格"每地区各跑一次"，没有"某地区某类对抗再跑一次"的口子（K18 另一半）。
- 实现：
  - 新静止种类 **`StaticKind.ExtraAreaPowerContest`**（与 `Timing = Continuous` 同一 `EffectData`，前提常驻；
    与 `SkipAreaCombat` 同款生命周期与"按卡**当前**所在地实时匹配地区"判定）；
  - 消费点 **`TurnManager.AdvancePhase` 的 `CombatPower` 分支**：离开本地区势力步骤时，若该地区声明了"额外一次"
    且本回合**还没追加过** → `nextPhase = CombatPower`（**原地重入**：`EnterPhase → DoCombatPower` 再结算一次
    同地区同类型的势力对抗），跑完（再过一次双方让过）才推进地区游标；
  - **每地区每回合至多追加一次**：`_extraPowerDoneAreas`（HashSet）去重，`Reset`/`StartGame`/`NextTurn` 清空；
  - 追加的那次是**正常势力对抗**：照常放势力标志、声望等既有规则同办（本批未引入任何特例分支）。
- **顺带做的内部收口**：静止规则已有 4 种（略过对抗 / 敌方暗藏者不参与势力对抗 / 封印角色不可重置 / 额外势力对抗），
  原先在 5 处站点各写一段分支 → 现统一为 **`SetStaticRuleRegistrations(card, e, active)`**（按效果声明登记/注销全部表）
  ＋ **`HasAnyStaticRule(e)`** ＋ **`ClearAllStaticRulesFor(uid)`**；新增静止种类只需在这三个方法里各加一行。
- 覆盖 **JC067**；测试 `P3ExtraPowerContestTests`(4)：结构 / 判定（本地区成立、他区不成立、换区跟随、离场失效）/
  **端到端**（对照组势力步骤 1 次→胜方 1 个标志；实验组原地重入再跑一次→2 个标志）/
  每回合只追加一次（第 2 次让过不再重入、标志数不再增长）。
  - 测试踩坑记录（值得留给后来者）：JC067 自带【声望】，声望标记会与对手新增标记**互相抵扣**（`AddPowerMark` 净差逻辑），
    读数会被抹成 0；实验里给对手也加一个【声望】角色"把声望多数打平"后，势力标志数才干净地反映对抗次数。
- **仍缺**：**本回合**生效的地区级追加/略过（JC118，见 K18 行）；"额外一次**调查/战斗**对抗"的写法（现仅登记势力对抗）。

**㊳ 重置步骤钩子（静态规则）→ JC108 乔治·帕佐，织梦人②**
- 问题：重置步骤（`TurnManager.DoReset` → `CardManager.UntapAll`）**只能无条件解除全部横置**，
  没有任何"某类卡本回合不解除横置"的口子（K29）。
- 实现：
  - 新静止种类 **`StaticKind.NoUntapWithSealedCards`**（与 `Timing = Continuous` 同一 `EffectData`，前提常驻）；
  - `EffectEngine`：`SetNoUntapWithSealedCards(card, active)` 登记/注销（与 K18/K21 的静止规则**同一套五处生命周期**：
    前提满足、离场、前提丧失、`RemoveAllContinuousFor`、`ResetAll`），查询 `IsUntapBlocked(card)`；
  - 判定三条件：① 本卡是**角色**（模板类别含 `Character`，"敌方**角色**"）② 其上**有牌封印**（`SealedCards` 非空）
    ③ 场上存在登记来源卡（**在争夺区**＝"在场"、非死亡流程）且其控制者 **≠** 本卡控制者（＝"敌方"）；
  - **卡文无"本地区"限定 → 全局生效**（不按地区匹配，与 K18/K21 的地区级规则区分）；
  - 消费点 **`CardManager.UntapAll`**：命中者跳过 `SetTapped(false)`，其余照常；日志区分"解除 N 张 / 锁定 M 张"。
- 覆盖 **JC108**；测试 `P3NoUntapSealedTests`(6)：结构 / 判定三条件（敌方+有封印=锁、无封印=不锁、本方=不锁）/
  附属卡不锁（只锁"角色"）/ 来源离场即失效 / **端到端**（重置步骤后有封印者仍横置、同地区无封印者照常重置）/
  封印被移除后下次重置步骤照常解除横置（实时判定，无需重登记）。
- **仍缺**：通用的"重置步骤**触发**时机"（非静止规则类效果）；JC122「具有锁定标记的角色不能翻面」属另一类静止规则
  （翻面限制需在 `SetCovert`/`RevealCard` 消费点，且"翻面"双向含义待用户确认）。

**㊲ 地区赢取时的"去处替代"（K3 第一批）→ JC079 圣婴**
- 问题：地区赢取结算的"本地区卡送拥有者牌库底"是**同步跑完**的，而入堆叠的 `OnAreaWon` 触发是**异步结算**的
  （`AreaManager.ResolveAreaWon` 的既有注释已写明"改变本地区卡去向"类效果仍属 K3 替代效果范畴）——
  按触发写会在"卡已回库"之后再改判，等于无效。此前只有『撤回』这一条硬编码的去向替代（关键词驱动）。
- 实现：
  - `EffectData.WinAreaDestination`（**去处替代声明**）：本卡所在地区被赢取时，把去处从"送拥有者牌库底"
    改判为该字段值；目前登记 `"计分区"`（JC079 → 赢取者计分区）。
  - 生效条件＝同一效果声明的 `Condition.Predicate`（缺省 `WinnerIsSelfInSameArea`＝"**你**赢取**本地区**时"），
    判定复用 `EffectEngine.EvaluateCondition`（与 `OnAreaWon` 触发路径**同一谓词实现**，不另造条件体系）。
  - 查询 `EffectEngine.ResolveWinAreaDestination(card, areaUid, winnerId)`；
    **消费点＝`AreaManager.ResolveAreaWon` 的送库底扫描**（顶层卡 + 结附地区的附属两处），
    与『撤回』**同一实现位**（`TryApplyWinAreaReplacement` → `ZoneManager.MoveToScore(卡, 赢取者)`；
    该位在 ㊿ 又扩了 `资产区` 去处值 → JC120）。
- **顺带补的卡数据**：JC079 卡文写"**1分值**"，但 `.tres` 与导出 JSON 的 `Score` 都是 0 →
  进计分区不产生任何分数、效果等于没做。已把 `JC079_shengying.tres` 的 `Score` 置为 **1**。
- 覆盖 **JC079**；测试 `P3WinAreaDestinationTests`(5)：结构（含 `Score==1` 断言）/ 条件仅本方赢取成立（敌方赢取不改判）/
  **端到端**（本方赢取 → 圣婴进计分区、总分＝地区卡分+1、对照普通角色照常回库底）/
  敌方赢取 → 圣婴回拥有者牌库底且计分区不得那 1 分 / 圣婴不在被赢取地区时不受影响。
- **仍缺**：伤害**替代**（改为由另一张卡承受：JC077/JC121）、**死亡替代**（JC105/JC124）、
  其它"离场/去处"替代消费点，以及把它们统一起来的 `ReplacementRegistry`。

**㊱ 目标须带"卡上标志"（`HasCounter`）+ `Move` 目的地写法 → JC069 警犬②**
- 问题：① 目标筛选只有"大类/子类别/组织/关键词/费用/横置/领域图标"等条件，**没有"目标须带某个卡上标志"**；
  ② `ActionVerb.Move` 只能移到"结算上下文里的 `targetAreaId`"，无法表达"移到**目标卡当前所在地区**"。
- 实现：
  - 新限制 **`TargetRestriction.HasCounter`**（`Param`＝标志名，判定 `实例.Counters[标志名] ≥ 1`；`Param` 空 → **fail-closed** 不通过）；
    判定加在 **`TargetValidity.CheckRestrictions`**（取出目标与结算复检的**单一真相源**）→ 三处自动同源：
    打出目标、激活目标、结算复检；
  - `Move` 新增 **`Args[1]` 目的地写法**：`"目标所在地区"` / `"来源所在地区"`（缺省省略＝`targetAreaId` 机动语义），
    按卡**当前**所在地实时解析；未登记的写法 → 跳过并告警（不静默乱移）。
- **顺带修掉的真缺陷**：`ActivateEligibility.IsLegalActivateTarget` 的"未配置 `TargetRules`"分支此前**只做大类粗筛**，
  激活能力自己声明的 `Target.Restrictions` **被完全忽略** → 任何带精筛条件的激活能力都会对同类目标放行
  （JC069 的表现就是"随便一个角色都能选"）。已改为同样走 `TargetValidity.CheckRestrictions`（全量套件零回归）。
- 覆盖 **JC069 警犬②**（`Cost=2` / 快速窗口 / `MaxUsesPerTurn=1` / `Move{"自身","目标所在地区"}` / `Gain{黑眼,1,自身}` + 直到回合结束）；
  测试 `P3CounterTargetTests`(7)：结构 / 只有带标志者（含暗藏者）是合法目标 / 无标志者时不可发动 / 端到端移动到目标所在地区并获黑眼 /
  黑眼回合结束撤销 / 标志移除后不再合法 / 缺标志名 fail-closed。
- **仍缺**：标志的**数量门槛**写法（"带 2 个以上锁定标志"）尚未登记；卡上标志增减**不** `BumpVersion`，
  "结算前标志被移除"由结算复检兜住（已由本批测试覆盖）。

**㉟ 地区级"敌方暗藏者不能参与势力对抗"（静止规则）→ JC023 范·弥尔顿**
- 问题：暗藏者在引擎里**本来参与势力对抗**（`AreaStatsCalculator.ValueOf`：暗藏者＝调查 0 / 战斗 0 / **势力 1＋先手势力**），
  但没有任何"把某个地区里的敌方暗藏者踢出势力统计"的口子。
- 实现：
  - 新静止种类 **`StaticKind.ExcludeCovertPower`**（与 `Timing = Continuous` 写在同一 `EffectData`，前提常驻）；
  - `EffectEngine`：`SetCovertPowerExclusion(card, active)` 登记/注销（与 K18 的 `SkipAreaCombat` **同一套生命周期**：
    前提满足、离场、前提丧失、`RemoveAllContinuousFor`、`ResetAll` 五处收口），查询
    `ExcludesCovertPower(areaUid, cardControllerId)` —— 地区内存在登记来源卡且其控制者 **≠** 该控制者
    （即"**敌方**暗藏者"）→ 排除；**不对称**：本方暗藏者照常参与；
  - **判定按卡当前所在地实时匹配地区**（不存地区快照）→ 换区自动跟随；
  - 消费点＝**`AreaStatsCalculator`**（既有注释已声明它是"对抗结算 `SumPlayerValue` 与 UI 展示 `GetAreaStats`
    共用的单一真相源"）→ 两处都调同一个 `IsExcludedByAreaStaticRule` 判定，显示值与结算值不会漂移。
    因暗藏者调查/战斗本为 0，实际效果＝其**势力贡献归零**；正面角色/附属完全不受影响。
- 覆盖 **JC023**；测试 `P3CovertPowerExclusionTests`(7)：结构 / 基线（暗藏者本来提供 1 点势力）/
  登记后敌方暗藏者归零且本方暗藏者照常 / 只影响势力（调查与战斗不受影响）/ 离场即失效 / 换区跟随 /
  **端到端**（对照组 2:2 平局无势力标志；实验组暗藏被排除后 2:1 → P1 赢得势力对抗并放置 1 个势力标志）。
- **仍缺**（K21 其余消费点）：按 **Organization** 的进入限制（JC064）、按**地区子类别**（水域）的前提（JC041）、
  按**卡上标志**筛选目标（JC069/JC122/JC123）。

**㉞ 地区级"略过对抗"（静止规则）→ JC098 陷入疯狂的人群②**
- 问题：对抗三步骤（调查/战斗/势力）由 `TurnManager` 逐地区硬跑，**没有任何"某地区略过某类对抗"的钩子**（K18）。
- 实现：
  - 新静止种类 **`StaticKind.SkipAreaCombat`**（`StaticEffect.Param` = 对抗名，可 `,` 组合：`调查`/`战斗`/`势力`），
    与 `Timing = Continuous` 写在**同一条** `EffectData` 上（与 JC068 `BlockDeployCovert` 同一范式）；
  - `EffectEngine`：`SetAreaCombatSkip(card, names, active)` 登记/注销（前提满足、离场、前提丧失、
    `RemoveAllContinuousFor`、`ResetAll` 五处收口），查询 `AreaSkipsCombat(areaUid, combatName)`；
  - **判定按卡"当前"所在争夺区实时匹配地区**（不存地区 UID 快照）→ 争夺区之间移动自动跟随，无需重登记；
  - 消费点 **`TurnManager.ResolveCurrentAreaCombat`**：单一判定入口 `IsAreaCombatSkipped(type, areaUid)`
    （对抗名映射 `CombatRuleName`）→ 该地区本步骤**不结算对抗**（无胜负、无势力标志、不开分配/伤害窗口），
    **但仍调 `GrantPriorityAfterCombatStep`**——与"无对抗管理器/无地区"的降级路径同款，
    保持"每个步骤都有优先权"的节奏与 `AdvancePhase` 的推进条件不变（略过 ≠ 卡死）。
- 覆盖 **JC098**；测试 `P3SkipAreaCombatTests`(5)：结构 / 登记后本地区略过（且只略过调查、不影响他区）/
  离场即失效 / 换区跟随 / **端到端**（对照组：调查步骤真的开对抗窗口；实验组：不开窗，且双方让过后照常推进到战斗步骤）。
- **仍缺**：**本回合**型地区略过（JC118「选择目标地区，本回合中该地区略过对抗阶段」——需打出时选地区 + 时效登记）、
  "**额外进行一次**对抗"（JC067）。JC118"略过**对抗阶段**"是否＝略过三步骤全部这一点**待用户确认**（本批未做）。

**㉝ 图标镜像（持续授予的"来源改写"）→ JC066 封魔瓶瑟琳娜②**
- 问题：持续授予一贯只支持"卡脚本里**静态声明**的 `ContinuousGrants`"（外加 `ScaleExpr`/`ScaleByLoyalty` 缩放**数量**），
  无法表达"把**别的卡**的能力图标复制到自己身上"——JC066「额外具有被封印在其上的角色牌能力栏内的所有能力图标」无处落。
- 实现：`ContinuousPrereqSpec.MirrorSealedIcons = true` —— 该效果**不看** `ContinuousGrants`，改为
  `EffectEngine.BuildMirroredIconGrants(card)` 现算一批授予：
  - 逐张读 `card.SealedCards`，**只取角色牌**（`CategoryFlag` 含 `Character`；非角色封印牌不提供能力图标）；
  - 取**印刷能力图标**（`Strength`/`Investigation`/`Power` + 三个先手值）→ 沿用 `IconToAttribute` 既有映射
    （黑刀=战斗 / 黑眼=调查 / 黑星=势力 / 白刀=先手战斗 / 白眼=先手调查 / 白星=先手势力）汇总；
  - **不含魔法领域图标**（领域 ≠ 能力图标，卡文只写"能力栏内的能力图标"）；同属性多来源**累加**为一条授予。
  - 前提判定：`EvaluatePrereq` 的镜像分支直接返回 true（前提＝常驻；授予量由封印牌集合决定），
    `HasPrereqSpec` 也随之认这条效果 → 它进入 `_pendingPrereqCards`/`_activePrereqCards` 的重判全集。
- **重算时机**：封印落地（`TurnManager.SealCard` → `ReevaluatePrereqs`）与任意换区（`ZoneManager.FireMoved` → 双方 `ReevaluatePrereqs`）
  都会**先按旧记录撤销、再按当前封印牌重算**（登记键＝卡 UID + 效果 Id，与同卡其它持续效果互不干扰）。
- **踩到的坑（已修）**：镜像在"当前 0 张封印牌"时 `applied.Count == 0` → 既不写 `_continuousRegistry`，
  也不进 `_activePrereqCards` → 之后封印落地时的 `ReevaluatePrereqs` **根本遍历不到这张卡**，镜像永远补不上。
  修法：镜像效果**即使 0 项授予也登记进重判全集**（`RegisterPersistent` / `ReevaluatePrereqs` 两处）。
- **顺带修正的真 bug（JC066①）**：原实现把 `Seal{自身, 目标}`（＝把**瑟琳娜**封印到目标角色身上），
  与卡文"将本地区的目标角色或暗藏者封印**于瑟琳娜上**"**方向相反**（且镜像永远读不到 `SealedCards`）。
  已改为 `Seal{目标, 自身}`＝被封印者＝目标、宿主＝瑟琳娜自身；`P2MoreCardsTests` 的断言与用例改按卡文口径。
- 覆盖 **JC066**（两行）；测试 `P3MirrorSealedIconsTests`(8)：结构 / 封印方向 / 封印后获得对应属性 / 多张累加 /
  封印牌离场即撤销 / 非角色封印牌不镜像 / 无图标封印牌不镜像 / 端到端（进场封印 → 镜像生效）。
- **已交付（本轮）**：**图标替换** `ActionVerb.ReplaceIcon`（＝属性替换，JC106，见 §7.10 ㊹）。**仍缺**：**持续型"失去图标"**（JC123 光环式移除黑星/白星）与 **"印刷文本框视作空白"**（JC101：该卡**失去效果**（不影响已发动的效果）＋失去领域图标）、JC099（光环按"其上有牌封印"筛选 + 失去领域图标）。

**㉜ 防止伤害（伤害护盾）→ JC078 光能盾**
- 问题：`ActionVerb.PreventDamage` 长期只是**枚举占位/TODO**（`EffectEngine` 的 TODO 组里），
  而伤害唯一收口 `CardManager.DealDamageToInstance` 只有"灵体"一条防止规则（装配层写死）→ "防止伤害"类卡无路可走。
- 实现：
  - 新动词 **`PreventDamage`**（Args[0]＝对象：`"目标"`（缺省）/`"自身"`；**对象＝目标而当前无目标 → 跳过**，
    与 `Inspect` 同口径，绝不回退成来源卡）；
  - `EffectEngine.RegisterDamageShield(card)` → `TimedMod{Kind="DamageShield", TargetUid, ExpireTurn=NowTurn}`：
    **期限＝本回合**，回合结束由 `ExpireTurnEndEffects` 出栈失效（护盾**就是**这条登记项，卡面无状态需还原）；
  - `EffectEngine.IsDamagePrevented(card)`（按卡 UID 判定）→ 装配层把它**并联**进既有的
    `CardManager.DamagePreventer`（`DamageRules.IsSpiritPrevention(...) || ee.IsDamagePrevented(target)`）；
  - **语义闭环**：被防止 → 不写伤害 → 不判死 → **不触发死亡**（死亡触发的前提是死亡真的发生）。
  - **幂等**：同卡同回合只登记一条（卡文是"所有伤害"而非"接下来 X 点"，护盾无层数/点数语义，重复结算不叠加）。
- 覆盖 **JC078 光能盾**（快速行动＝`PlayTiming.Response`；目标＝任意角色，不分敌我）；测试 `P3PreventDamageTests`(8)：
  结构 / 护盾只作用于目标卡 / 伤害被防止且不判死 / 对照（无护盾正常受伤）/ 回合结束失效 / 重复登记幂等 /
  端到端（引擎结算挂护盾）/ 无目标时跳过不护来源卡。
- **仍缺**（本批只做"防止"这一半）：**伤害替代**（JC077/JC121「该伤害改为由某卡承受」）、
  **死亡替代**（JC105/JC124「改为封印/移回场上」）、**去处替代**（JC079「改为置入计分区」）。

**㉛ 费用"通道隔离"：现身（付费翻面）费 ≠ 打出费（→ JC033 家族传承②）**
- 问题：`TimedMod`/`ApplyCostModification`/`GetCostModification`/`ConsumeCostModification` 原先**只有一条减费通道**
  （打出费用），《现身/翻面》的付费没有任何减费口子 → JC033②「所有本方暗藏者，**付费翻面**时其所需费用减少1」无法表达。
- 实现：新增 **`CostScope`（费用通道）**，缺省 `"打出"`：
  - `ContinuousCostModSpec.CostScope`（持续来源声明通道，JC033 写 `"现身"`）；
  - `TimedMod.CostScope` + `ApplyCostModification(..., costScope)` / `ConsumeCostModification(..., costScope)` /
    `GetCostModification(..., costScope)` 全链路带通道；**过滤条件＝过滤串 AND 通道**（`Filter` 为空即"该通道全部卡"）。
  - 消费点：`TurnManager.RevealCovert` 改用 `"现身"` 通道取修正/消费；打出路径（`PlayCard`）保持 `"打出"`。
  - 结果：JC033 的现身减费**不会**顺带把该玩家的打出费用也减掉，反之亦然（既有 `P3RenewableCostModTests` 的打出用例不受影响）。
- **顺带修掉的清理缺口（本轮实测踩到）**：`UnregisterPersistent` 里按 `SourceUid` 收口时原带 `!m.Renewable` 跳过 →
  **"整回合"型持续费用修正（`PerTurn=false`，JC033 这种）在来源卡离场后仍留在牌堆里继续减费**。
  已改为：**只要挂了 `SourceUid`（＝持续来源）就在来源离场时清理**（可再生 / 整回合两种都收），
  只有无 `SourceUid` 的一次性费用修正（如 JC012「本回合下一张」）才允许游离存在。
- 覆盖 **JC033 家族传承②**（`Scope=AttachTarget` 结附本方秘社 → 本方暗藏者付费翻面 -1）；测试 `P3RevealCostTests`(5)：
  结构 / 现身减费生效 / **打出通道不受影响（通道隔离）** / 来源离场即失效 / 端到端现身实付印刷费用-1。
- 仍未做：JC033① 的行动1（检索吸血鬼角色牌 → 展示 → 暗藏进场到**目标地区**）受阻于"激活能力的地区目标在联机路径不支持"（另见主表该行）。

**㉚ 持续性费用修正 + "每回合可再生的一次性"（→ JC026 洛特尼科夫①）**
- `EffectData.ContinuousCostMod`（`ContinuousCostModSpec{Amount, Filter, PerTurn}`）：持续效果在注册期间为
  **来源控制者**挂一条玩家级费用修正；`Filter` 走既有 `CostModFilter`（支持 `组织:`/`子类别:`/`域:`/`有域图标`），
  例：JC026 `Amount=1`（**正值＝减费**，与 `Gain{"费用减少",N}` 同一约定：`实际费用 = 印刷费用 - mod`）、
  `Filter="子类别:装备"`。
- **"每回合可再生的一次性"**（`TimedMod.Renewable`）：消费后**不删除**，只记 `ConsumedTurn` 本回合失效，
  **下回合自动恢复**（登记项即时效载体，无需回合开始重挂）；时效终结＝**来源卡离场**
  （`UnregisterPersistent` 按 `SourceUid` 清理，详见 §7.10 ㉛ 的清理缺口修订）。与普通"一次性"（`ConsumeOnce`，JC012 的"下一张"）互不干扰。
- **回合号基准**：可再生判定用 `NowTurn`（优先 `TurnManager.TurnCount`）而非 `EffectEngine.CurrentTurn`
  —— 后者只在若干结算入口同步、回合切换时会滞后（本轮实测踩到：消费后跨回合仍被判"本回合已用"）。
- 覆盖 **JC026**；测试 `P3RenewableCostModTests`(6)：结构 / 注册后仅**装备牌**减费（非装备不减；仅按卡名查询无法命中子类别过滤）/
  用掉后本回合失效·**下回合自动恢复**·再用又失效 / 对照（普通一次性不恢复）/ 来源离场即清理 / 端到端打出装备牌实付 3-1=2。
- 顺带加固：既有 `P2MoreCardsTests.TestJC026_检索装备牌并展示` 原按 `EffectDataList[0]` 取效果（新增效果会打乱下标）→ 改为按 `EffectId` 查找。
**㉙ "手牌行动"：在手牌中发动激活能力（→ JC110 闲人的绿地①）**
- **表达方式＝生效区声明**（不新增数据标志）：激活类效果声明 `Scope = ZoneScope.HandZone` 即为"手牌行动"。
- **来源生效区门禁**（新增，`EffectEngine.ActivateAbility` 第 0.8 步）：激活类效果的声明生效区必须与来源卡
  当前所在区一致（默认：角色=争夺区、附属=附属区、**手牌行动=手牌**）。这既让手牌来源合法，
  又**堵住**此前"来源不在生效区也能激活"的漏洞（例如角色已进墓地仍可发动其能力——此前只靠候选/UI 约束）。
- **候选扫描**：`ActivateEligibility.GetEligibleAbilities` 除场上卡外，把**该玩家的手牌**也纳入扫描
  （逐效果按生效区判定筛掉其余手牌效果）；`CanActivate`（单卡高亮）本就按效果粒度判生效区，故手牌卡同样适用。
- **View**：`CardUI` 双击"手牌中的手牌行动卡"即请求激活（该分支先于 `CanDrag` 且 `AcceptEvent()`，
  因此**不影响**普通手牌的拖拽出牌）。
- 覆盖 **JC110**（手牌行动4：支付 4 费 → `Seal{自身, 目标}` 把自身封印在目标角色上）。
- 测试 `P3HandActivateTests`(5)：结构 / 手牌中可发动·离手后被拒（生效区门禁）/ 端到端（付费 4 + 自身进封印区 +
  宿主绑定）/ 费用不足被拒 / 候选扫描含手牌来源而普通手牌卡不在候选中。
**㉘ K27 来源门禁 + "墓地行动"（从墓地正面打出）→ JC085 复生行尸②**
- **缺口**（清单 K27 早先记录）：`TurnManager.PlayCard` **没有任何来源区校验**——"只能从手牌打出"
  只靠候选投影（`LegalityProjection` 只枚举手牌）与 UI 约束，联机下可越权把牌库/墓地/场上的卡打出来。
- **交付**：
  · `TurnManager.PlayCard` 新增**服务端权威来源门禁**：默认仅允许 `CardZone.Hand` 来源，
    其余来源一律拒绝并打日志；
  · `PlayerCardData.PlayableFromGraveyard`（`[Export]`，卡文"**墓地行动**"）→ 额外放行**墓地**来源
    （"从墓地中**正面**打出，就如同其在你的手上一样"）；费用/忠诚/行动步骤/目标规则**全部照常**
    （"你仍需要支付其费用，且需满足其忠诚需求"）；
  · `LegalityProjection.Build` 的 `PlayTargets` 把墓地中声明该标志的卡一并列为**可打出源**（候选侧对齐）。
- 覆盖 **JC085**；测试 `P3GraveyardPlayTests`(6)：普通卡从墓地/牌库打出被拒 / JC085 可从墓地正面打出（结算后正面站场）/
  费用照常支付 / 费用不足被拒 / 打出候选含墓地行动卡而普通墓地卡不含 / 手牌打出不受影响（回归保护）。
- **回归确认**：新增门禁后全量 605 例 **0 失败** → 既有所有"打出"路径（单机拖拽、联机三种异步出牌、触发/激活入栈）都本就以手牌为来源，门禁未误伤。
- **仍缺**："从**手牌发动**激活能力"（JC110「手牌行动4」：激活门禁与候选仍只扫争夺区/附属区）。
**㉗ 新触发时机「被潜伏/翻面时」（`OnLatent`）+ 群体伤害（→ JC054 蒙古死亡蠕虫①）**
- **时机**：`GameEvent.OnLatent` → `EffectTiming.OnLatent`；触点＝**装配层**订阅 `CardManager.OnCardStateChanged`
  的 `CovertChanged`、且 `card.IsCovert == true` 时 `TriggerEmitPoints.OnLatent(card, controller, area)`。
  反方向（暗藏→正面）仍走既有 `UncoverTrigger`，两个方向**互不重叠**；
  语义＝**自身**时机（`TriggerRegistry` 的"自身语义"清单加入 `OnLatent`，只有被潜伏的那张卡自己的效果被收集）；
  上下文带 `SourceAreaAtTrigger`（供"本地区"类效果）。
- **⚠️ 写卡须知**：本时机**必然在暗藏态**下成立（"被潜伏"就是翻为暗藏者）→ 效果必须显式声明
  `TriggerCovertMode = CovertOnly`，否则会被默认的 `FaceUp`（仅正面可触发）静默拦掉（本轮实测踩到）。
- **群体伤害**：`TargetSpec.MassMode = AllInArea` + `DealDamage{1}`：一次结算内对本地区内每个符合条件者各结算一次
  （`MassTargetPool` 自动排除来源自身与濒死卡）；"无印刷防御的卡不受伤害"仍由引擎既有语义兜住。
- 覆盖 **JC054**：两条效果天然成链——结束步骤 `Latent`（自身翻面）→ `OnLatent` 触发 → 本地区其他角色各 1 点伤害。
- 测试 `P3LatentTests`(5)：结构 / 翻为暗藏者才广播（现身方向不触发）/ 群体伤害只打本地区其他角色（本方·敌方都受伤、
  别地区与自身不受影响）/ 结束步骤潜伏 → 伤害链 / 无印刷防御者不受伤害。
**㉖ 复合目标限制（`TargetSpec.SpecAlt`）→ JC043 纵火狂**
- 卡文形态："目标**……或……**"且**两侧条件不同**（JC043「消灭目标**结附于秘社**或**本地区**上的附属」——
  两者都是附属，一靠宿主种类、一靠"本地区"）。`KindAlt` 只能表达**大类** OR、`Restrictions` 是 AND，都不够。
- 交付 `TargetSpec.SpecAlt`（第二条**完整**目标声明）：
  · 候选池：两条各自收集的**并集**（去重；本体候选在前，顺序确定 → 双端同解）；
  · 合法性 / 结算复检：**任一条整条通过即通过**（`TargetZonePool.Matches`、`MassTargetPool.Matches`、
    `TargetValidity.Check` 三处同源；`Check` 改为 `CheckOne` ＋ 顶层 OR 包装，避免只在大类层做 OR 时被
    本体的 `IsLocalZone`/`Restrictions` 拦掉别名那条）。
- 配套两个新 `TargetRestriction`：**`HostIsCouncil`**（目标＝结附于秘社的附属）、
  **`AttachedToArea`**（目标＝**直接**结附于地区的附属）。⚠️ 坑：结附于**角色**的附属其 `ZoneDetailId`
  也是宿主所在地区，故"结附于本地区"必须 `LocalZone + AttachedToArea` 两条**同时**成立，
  单靠 `LocalZone` 会把"该地区的角色附属"误收进来。
- 另记：`TargetZonePool/MassTargetPool.Matches` **不含地区语义**（只做"大类＋属性"筛选）——
  "本地区"的最终判据在 `TargetZonePool.Collect`（带 `localAreaUid`）与 `TargetValidity.Check`（带来源地区快照）。
- 覆盖 **JC043**；测试 `P3CompositeTargetTests`(4)：结构 / 候选＝两条并集（角色附属与别地区附属被排除）/
  合法性（池筛选 + 结算复检两路）/ 端到端消灭两条各一次。
**㉕ 运行期授予的触发类关键词 + 群体 Gain 主体（→ JC024 S．P．T董事局① · JC089 毒血诅咒）**
- **① 运行期授予的触发类关键词参与触发收集**（K13 剩余项）：`KeywordEffects.RuntimeTriggerTemplates`
  （`机动`→`ManeuverTrigger`、`威名`→`OnCombatWon`）+ `TriggerRegistry` 在收集阶段对"本卡**运行期授予**
  （`HasKeyword` 且**非**卡面固有 `HasInherentKeyword`）"的关键词**合成**该关键词的触发形态
  （克隆模板并带 `EffectId = "Keyword_<名>"`；生效区 / 暗藏模式 / 条件判定与卡面效果同一条路径；
  卡面固有者跳过，避免重复结算）。`袭击` 走数值合并、`声望` 走引擎级 `RenownRule`，均不经此通道。
- **② `Gain` 的群体主体**：`Args[2] = "所有本方角色" / "所有其他本方角色"`（"其他"＝排除来源自身）→
  对**全场**本方角色逐个走同一条 `ApplyGain`（把主体参数改写为"目标"、把该卡作为 target 传入，
  因此"直到回合结束"的时效项**逐卡**登记）。
- **③ 负值授予的两处修复**（JC089「-1 防御」暴露）：
  · `EffectEngine.IsGrantPresent` 对 `Attribute` 改为**符号感知**（`Amount<0 → cur<0`），否则自愈路径每次重算都会再授一次；
  · `CardInstance.ResetToBaseValues` 的"防御"重算判据由 `> 0` 改为 `!= 0`，否则复位后**减益凭空消失**。
- **④ 持续授予自愈**：新增 `EffectEngine.ReapplyMissingContinuousGrants(playerId)`（按 `_continuousRegistry`
  逐条校验 `IsGrantPresent`，缺失即补授），并挂到装配层 `ZoneManager.OnPostMove`（与 `ReevaluateAuras` 同处）——
  复位（换区/现身）会清空运行期授予，数值型授予需按登记表补回。
- 测试 `P3RuntimeKeywordTests`(7)：结构×2 / 群体授予机动（含"其他"排除自身、对手不获得、回合结束逐张撤销）/
  授予的机动参与触发收集 / 未授予不收集 / JC089 宿主减益 + 授予的威名**真的触发**（战斗获胜 → 本地区放标志）/
  附属离场逐条撤销。
**㉔ K2 扩展："目标卡上"的整组消灭（→ JC013 貘 · JC017 除咒师）**
- K2 群体动作此前只支持"**地区内**群体"（`MassMode.AllInArea`），无法表达"**某张卡上**的集合"。
- 新动词 `ActionVerb.DestroyOnTarget`：对象＝宿主卡（`target`）；`Args[0]`＝集合来源（`附属`＝宿主的
  `AttachedCards` / `封印牌`＝宿主的 `SealedCards`）；`Args[1]`＝归属筛选（`任意`/`本方`/`对手`，
  基准＝**来源卡控制者**）。逐张走 `CardManager.DestroyInstance`（消灭＝死亡，照常广播死亡触发），
  宿主侧的 `AttachedCards`/`SealedCards` 由 `ZoneManager` 的离场 detach 逻辑自动清理
  （实现上先**快照**再逐张消灭，避免遍历中被改写）。
- **集合为空 → 什么都不做，且不置链式中止**：卡文"消灭…。该角色获得屏障"的后续动作不以消灭成功为前提
  （与 ㉓ 的"若你如此做"区分开）。
- 覆盖 **JC013**（牺牲自身为代价 → `Tap{目标}` → `DestroyOnTarget{封印牌}`）、
  **JC017**（`DestroyOnTarget{附属, 对手}` → `Gain{屏障,1,目标}` 直到回合结束）。
- 测试 `P3DestroyOnTargetTests`(7)：结构×2 / 只消灭敌方附属而本方留存（含 `HasGuard`+`IsBarrierBlocking` 断言与到期撤销）/
  JC013 横置 + 消灭封印牌 / 集合为空不影响后续动作 / 无宿主安全跳过 / JC013 激活路径（牺牲自身 + 结算）。
**㉓ 动作成果判定（"若你如此做"）（→ JC009 远东六号科研所 · JC087 格鲁尼的走私客）**
- 卡文第三种分支形态："…，**若你如此做**，…"——判据不是谓词，而是**前一个动作是否真正生效**。
- 实现：把既有"支付失败即中止"的 `_actionChainAborted`（链式中止标志）**泛化为动作成果**：
  某动作未生效 → 置标志（`MarkActionIneffective`）→ 同一动作序列的**后续动作整段跳过**
  （同步/异步两条动作循环都已在循环头检查该标志）。生效判据：
  · `Seal` —— 宿主为空 / 封印自己 / `SealCard` 返回 false（已被封印等）；
  · `Latent` —— 目标为空 / 不在场上 / 已经是暗藏者；
  · `Uncover` —— 目标为空 / 不在场上 / 非暗藏。
- 配套：`TargetSpec.AllAreas`（显式"不限定本地区"）——触发路径默认候选＝来源所在 SLOT，
  但**结附于秘社的附属没有地区**（`ZoneDetailId` 为空），JC009 这类"选择一个角色"必须显式打开全场候选。
- 覆盖 **JC009**（`Seal{目标,自身}` → `Curb{1,对手}`；FAQ：未能成功封印则遏制不结算）、
  **JC087**（`Latent{目标}` → `PlaceToken{势力标志,1}`；潜伏未生效则不放标志）。
- **口径记录**：JC087 的"当…**进入一个地区**时"按**进场触发**（`EnterTrigger`）实现——卡面**没有【机动】关键词**
  （对比 JC061 卡面写"机动 触发：当…进入一个地区时"→ 用 `ManeuverTrigger`）。若规则本意是"每次移动进地区都触发"，
  改一行 `Timing` 即可。
- 测试 `P3ActionOutcomeTests`(8)：结构×2 / JC009 封印成功则遏制 / **封印失败则不遏制** / JC087 潜伏成功则放标志 /
  潜伏未生效（已是暗藏）则不放 / 无目标则不放 / 进场触发端到端。
**㉒ 步骤级承诺 + 控制权转移与归还（→ JC024 S．P．T董事局② · JC129 操控心智）**
- **① 承诺与动作并存的语义修正**：`Pending` 的登记从"**替代**动作序列"改为"动作序列执行**之后**登记"
  （同步 / 异步 / 多目标 / 群体四条路径都补上）→ 既做一件事又留下承诺的能力（JC024：先 `Latent{自身}` 潜伏、
  再在下个准备步骤免费现身）两者都成立；只有承诺不做事的能力（JC082）Actions 为空，效果等价于纯登记。
- **② 步骤级兑现**：`OnTurnStarted`（准备步骤）/`OnTurnEnded`（结束步骤）是**双方共有步骤**、`TriggeringPlayerId = -1`
  → 这类时机下"所有在该时机登记的承诺都在事件出现时兑现"，不再按归属玩家过滤（其它时机仍要求归属匹配）。
  覆盖 **JC024**：`Pending{Timing=OnTurnStarted, Expire="", Optional=false, Actions={Uncover{自身}}}`——
  因该能力在准备步骤之后才能发动（事件已过），兑现必然落在**下一回合**的准备步骤＝卡文"**下一个**准备步骤"；
  `Uncover` 直连 `CardManager.RevealCard`，不经过任何付费通道＝卡文"且不需要支付其费用"。
- **③ 控制权转移 + 到期归还**：新动词 `ActionVerb.GainControl{对象, 期限}`：只改**控制者**（`ControllerId`，`OwnerId` 不变，
  与引擎内"夺控"口径一致），经**新增公开 API** `ZoneManager.ChangeController` 走内部夺控同一出口
  （版本号 / 光环重算 / 前提取舍 / UI 刷新）；期限含"回合结束"→ 登记 `TimedMod{Kind="Control", PlayerId=原控制者}`，
  由 `ExpireTurnEndEffects` **归还**给原控制者（卡已离场 → 安全跳过；已是本方控制 → 不夺取也不错记归还对象）。
- 测试：`P3StepPledgeTests`(5)（结构 / 激活则潜伏并登记承诺 / **下个准备步骤免费现身**（费用不足卡面值也照常现身）/
  兑现为强制触发 / 离场后兑现安全跳过）、`P3ControlTransferTests`(6)（结构 / 只改控制者不改拥有者 /
  获得控制权并在回合结束归还 / 常驻期限不归还 / 卡离场后归还安全跳过 / 已是本方控制则跳过）。

**⑳ 回合内事件记忆位 + 多玩家弃牌编排（→ JC053 鸣钟教派徽记② · JC041 禁婆②）**
- **① 回合内事件记忆位（"本回合"粒度）**：`AreaManager._powerMarkRemovedThisTurn`——
  · 置位点：`RemovePowerMark` 在**真实移除 &gt;0 个**时置位（请求但无可移除 → 不置位，与卡文"曾被移除"一致）；
  · 清空点：**回合开始的重置步骤**（`TurnManager.DoReset` → `ResetPowerMarkRemovedThisTurn`）→ 粒度＝"本回合"；
  · 谓词：`EnemyPowerMarkRemovedThisTurn`（敌我基准＝来源卡控制者，**任一对手**曾被移除即满足）。
  覆盖 **JC053**「本回合中，若有敌方势力标志曾被移除，你可以抓一张牌」。
- **② 激活类效果的"发动前提"前置门禁**：`EffectCondition.Predicate` 对激活类效果同样是**发动前提**——
  `EffectEngine.ActivateAbility` 在**付费/横置/牺牲代价之前**判定（不满足即拒绝发动、代价不付出；
  此前只有"目标合法性"被前置），`ActivateEligibility`（`CanActivate` / 候选列表）同步用于 UI 高亮/置灰。
  纪律与"激活前目标校验"同一条：**绝不出现"付了代价却什么都没发生"**。
- **③ 多玩家弃牌编排**：`Discard{1,"双方","自选"}` 全链路可用——`ResolvePlayerSide("双方")` 给出双方，
  **依次**对每位玩家调 `IGameUIHooks.PromptDiscard(discarderId, chooserPlayerId = 自己)`（各自自选），
  每人各一批落地（`OnDiscard` 每批只广播一次，与 P2 口径一致）；无 UI 走确定性兜底（各弃第一张）。
  覆盖 **JC041**「现身强制触发：每位玩家各弃一张手牌」（`UncoverTrigger` + `Forced=true`）。
- 测试：`P3TurnMemoryTests`(7)（结构 / 置位·移除 0 个不置位 / 回合开始清空 / 谓词只看敌方 / 条件不满足则拒发且不付代价 + UI 置灰 /
  条件满足则横置付费抓牌 / 新回合条件失效）、`P3TwoPlayerDiscardTests`(3)（结构 / 双方各一张 / 无 UI 兜底各一张）。

**⑲ 门禁 + 分支双条件（`EffectCondition.BranchPredicate`）→ JC031 唤灵师**
- 卡文形态："每当 **X** 时，…（动作 A）；**若 X 是 Y**，则**改为**…（动作 B）"——需要**两条独立条件**，
  而原先 `EffectCondition` 只有一条 `Predicate`（同时充当"收集门禁"与"ElseBranch 判据"），
  若把"是吸血鬼"写进去，则**非吸血鬼的本方角色死亡根本不会触发**（与卡文相反）。
- 交付：`EffectCondition.BranchPredicate`（可空）：
  · `Predicate` ＝ **门禁**（`TriggerRegistry` 收集阶段过滤 + 结算复检；不满足 → `ElseBranch`）；
  · `BranchPredicate` ＝ 门禁满足后的**分支选择**（不满足 → 同样 `ElseBranch`）；
  · 两条都满足 → `Actions`。**留空时行为与既有实现完全一致**（同步 `ResolveEffect` 与异步 `ResolveEffectAsync` 两条路径同办）。
- **新谓词 `DiedVampireCharacter`**：上下文卡（＝死亡的卡）是**角色** ∧ 其**当前实际子类别**含「吸血鬼」
  （`CardInstance.HasSubCategory`：模板固有 ∪ 运行期授予，与 `TargetRestriction.CurrentSubCategory` 同口径，
  如 JC030 授予的吸血鬼）。
- 覆盖 **JC031 唤灵师**：`OnCardDied`（P1 已接）+ 门禁 `DiedAllyCharacter`（"另一个本方角色"，已排除来源自身）
  + 分支 `DiedVampireCharacter` → 吸血鬼走 `Discard{1,"目标玩家","随机"}`、否则 `ElseBranch` 走 `"自选"`；
  玩家目标走 **K24 触发路径**（入堆叠前独立窗口就地选定）；`MaxUsesPerTurn=1`（K23）；`Forced=false`（"你可以"）。
- 测试 `P3DeathBranchTests`(8)：结构 / 吸血鬼判据（模板固有·运行期授予·非角色）/ 非吸血鬼走自选（询问弃牌者＝目标玩家）/
  吸血鬼走随机（**不**询问弃牌者）/ 来源自身死亡不触发 / 对手角色死亡不触发 / 每回合一次（第二次死亡不再弃牌）/
  取消目标玩家则不入堆叠且不弃牌。

**⑱ 调查对抗获胜时机 `OnInvestigationWon`（→ JC072 博学的贤者）**
- 该时机原为 `GameEvent`/`EffectTiming` 注释里的"**后续拓展·B 预留**"（`OnCombatWon` 只覆盖战斗对抗 Strength）。
  本轮补齐三件套：新枚举值 + `TriggerEmitPoints.OnInvestigationWon(winner, areaUid)` + `EventToTiming` 映射。
- **广播点（两条路径同源，均为"窗口关闭/分配完成之后"）**：
  · 单机 `CombatManager.ResolveWinner` 的调查分支 → `OpenCombatWindow(..., () => Emit)`；
  · 联机 `CombatManager.ResolveWinnerAsync` 的调查分支 → `ReturnAfterInvestigation` + `DrawCards` 之后 Emit。
  顺序纪律与战斗对抗一致（先完成窗口内事务 → 再结算获胜触发），避免触发排序窗与分配窗背靠背弹出。
- **不广播的情形**：平局（含先手破平被取消、先手不可破平）与 0:0 —— **无胜者即无事件**（不制造假胜者）。
- **上下文字段**：`TriggeringPlayerId`＝胜者、`TargetAreaId`＝对抗地区、`SourceCard=null`
  （沿用 D1：由收集阶段按"在场卡"各自注入 source）；因此**条件谓词可复用** `WinnerIsSelfInSameArea`
  ＝"本卡控制者获胜 ∧ 本卡就在该地区"＝卡文的"**其所参与**的对抗"。
- 覆盖 **JC072 博学的贤者**：`OnInvestigationWon` + `WinnerIsSelfInSameArea` + `Gain{声望,1,自身}` +
  `Duration="直到回合结束"`（时效由 `TimedMod` 到期还原；声望的规则本体由 `RenownRule` 承担）。
- 测试 `P3InvestigationWonTests`(7)：结构 / 获胜获得声望＋时效到期失去 / 输掉不触发 / 不在对抗地区不触发 /
  平局不触发 / **对照探针**证明"战斗对抗获胜只触发 OnCombatWon 类效果、不串线到调查时机" / 联机路径同样广播。
- **仍缺（供后续卡使用）**：`OnPowerWon`（势力对抗**获胜**，与"对抗结束"的 `OnPowerCombatEnded` 不同）、
  "被潜伏/翻面时"（JC054）、`OnCounter`。

**⑰ K24 玩家目标（→ JC111 混沌蝴蝶）**
- 口径（用户 2026-09）：允许指定**任意对手（甚至自己）**；玩家不是场上的卡节点 → 需要**独立的选择窗口**；
  目标**入堆叠时锁定**（与卡目标同口径，结算不重选）。
- **Model 单一实现** `Script/Manager/PlayerTargetPool.cs`：`Kind=Player`（`KindAlt` 亦认）+ `Restrictions`
  （`Enemy`＝对手 / `Friendly`＝本方 / 空＝任意），可选 `Filter` 文本（"对手/敌方玩家/自己/本方玩家/任意"）；
  **未登记的写法或"对玩家无意义"的限制条件 → 空候选 + 告警（fail-closed）**。
- **与 K15 槽位组合**：`TargetChoice` 新增玩家变体（`IsPlayer`/`PlayerId`，文本形式 `P{id}` 便于对齐）；
  `MultiTargetSlots`：`IsPlayerSlot` / `SlotCandidates.PlayerIds` / 合法性（同卡不可占两槽 → 同玩家不可占两槽）/
  `ChosenPlayerIds` / `SlotAlignedPlayerIds`（**按玩家槽对齐**，留空写 -1）/ `Reconstruct(…, playerIds)`。
- **入堆叠与结算**：`StackEntry.TargetPlayerId`（+ 多槽 `TargetPlayerIds`）、箭头 `StackTargetRef.RefKind.Player`；
  结算时动作参数写 `"目标玩家"`（`EffectEngine.ResolvePlayerSide` 解析为本次锁定的玩家号）→
  `Shuffle{"目标玩家"}`（洗该玩家牌库）、`Discard{…, "目标玩家"}` 等"对某玩家"的动作即刻可用；
  `Inspect` 新增 **`目标玩家牌库顶`**（`PeekDeckOf`，**只读不取出**）与 **`目标玩家手牌`**（JC057 类）。
  多目标模式的执行顺序：**先逐"目标玩家"各跑一遍动作序列，再逐"卡目标"**（两条通路共用同一序列）。
- **激活路径**：`EffectEngine.ActivateAbility(source, data, target, targetPlayerId)`（`PlayerActionService` 透传）；
  `ActivateEligibility.HasLegalTarget` 支持玩家目标（View 高亮/置灰正确）。
- **顺带修掉的缺陷**：激活时**目标校验原先排在付费/横置/牺牲之后** → "目标不合法"的发动会先扣费、先把自身横置/牺牲掉
  再被拒绝（代价白付）。现已上移到第 2.5 步（付费之前）；同时 `ValidateTargetAtResolve` 对玩家目标放行
  （玩家不会离场/翻面，否则会被误判"目标已丢失 → 不结算"）。
- **View**：`Arena.PromptPlayerTarget` = 独立选择窗口（列出候选玩家 + 取消）；场上**正面角色双击**即请求激活
  （"玩家目标"能力无法用"拖到目标上"表达；争夺区卡 `CanDrag=false`，不与拖拽冲突）。
- 覆盖 **JC111 混沌蝴蝶**：快速行动1 → 检视目标玩家牌库顶一张；快速行动+横置 → 洗目标玩家牌库。测试 `P3PlayerTargetTests`(16)。
- **触发路径（本轮补齐）**：触发类效果若以玩家为目标，在**入堆叠前**就地选定——
  `TurnManager.EnsurePlayerTargetThenPush`（候选＝`PlayerTargetPool.Collect`；单机经 `IGameUIHooks.PromptPlayerTarget`，
  **无 UI 兜底取第一个候选**（与卡目标既有口径一致）；联机经 `ChoiceRequest.Kind.SelectPlayer` +
  `OptionPlayerIds`/`ChoiceResult.SelectedPlayerId`，超时 45s；取消 → 该触发**不入堆叠**）；
  `StackEffectInstance.SelectedPlayerId` → `StackEntry.TargetPlayerId`（+ 玩家箭头）；
  `ResolveEntryTargetIsLost` 对玩家目标**不做卡丢失判定**（`TargetCardUid` 只是触发上下文卡）。
  覆盖 **JC057 监控员**（现身触发检视目标玩家手牌）。
- ~~**仍缺**：**打出路径**（事务卡以玩家为目标，JC128/JC065 的 `PlayCard` 侧）、**联机的激活类效果**整体未接线。~~
  **联机激活路径已交付（2026-09，见 §7.10 79 / §7.4b A50）**：以玩家为目标的激活能力由服务端下发
  `Kind.SelectPlayer`；**仍缺**：**打出路径**（事务卡以玩家为目标，JC128/JC065 的 `PlayCard` 侧）。

**⑯ P3·H 检视＝私密查看（→ JC062 调查档案）**
- 新动词 **`Inspect{对象}`**：对象＝`自身`/`目标`（缺省）。语义＝**只把该卡的面给效果来源控制者本人**看：
  **不翻面、不移动、不改任何状态、不给对手看**（与 `Reveal` 的公开展示相对）。
- 通道：单机 `UiNotificationBus.NotifyCardInspected(card, inspectorPlayerId)`（View 私下呈现）；
  联机 `ChoiceRequest.Kind.InspectCards`（`OptionUids` + `OptionCardRefs`，**只下发检视者**，`TimeoutSecondsFor` = 30s；
  超时/取消**不影响后续结算**）。协议往返与"只发给检视者"均有测试锁定（`P3InspectTests`）。
- **语义要点（易错）**：对象＝`目标` 而当前**无目标**时 → **跳过该次检视**（不回退成来源卡）。
  原因：多目标效果即便"一个卡槽都没选"，动作序列也会跑一遍（`target=null`），若回退成 `source` 就会把来源卡本身检视出去。
- 覆盖：**JC062 调查档案**（快速行动：检视**至多两个**目标暗藏者 → 抓一张牌）。两个槽都可留空、`MinChosenSlots=0`
  （FAQ：场上没有可检视的暗藏者也能打出，仅为抓一张牌）；"抓一张牌"做成**独立效果**（`JC062_2_DrawOne`），
  避免落进多目标循环导致抓牌数×目标数。
- 测试：`P3InspectTests`(6)（结构 / 单机私密呈现且不改状态 / 端到端选两个 / 无暗藏者只抓牌 / 联机只发给检视者 / 协议字段往返）。

**⑮ P3·G 区域/去向转换（→ JC126 产业扩张 / JC120 庄严圣像）**
- 新动词 **`MoveToZone{对象, 目的区, 朝向}`**：对象＝`自身`（缺省）/`目标`/`牌库顶`（来源控制者牌库顶一张，经
  `TurnManager.GetDeckOf` + `RemoveCardFromDeckOf` 取出）；目的区＝`资产区`（缺省）/`争夺区`（落点＝`targetAreaId`）/
  `手牌`/`牌库底`；朝向＝`正面`（缺省）/`暗藏`。
- 口径：**"成为一个资产"由落区满足**——`CardManager.MapInstanceToTargetKind` 按区域映射（资产区 → `TargetKind.Asset`），
  费用来源 `PlayerManager.GetAvailableMana` 也按"资产区未横置卡数"计，故置入资产区的牌即刻可作费用。
- 覆盖：**JC126**（`牌库顶 → 你的资产区`，牌库为空则无事发生）、**JC120**（`OnAreaWon` + `WinnerIsSelfInSameArea`
  → `自身 → 资产区`；敌方赢取不触发）。
- 测试：`P3ZoneConvertTests`(5)。
**⑭ P3·F 按计数缩放的持续授予（→ JC037 / JC045 / JC109）**
- `ContinuousPrereqSpec.ScaleExpr`（复用 `EffectValueResolver` 计数表达式，未登记写法 fail-closed → 不授予）：
  N＝表达式求值；**授予量＝`ContinuousGrant.Amount × N`**，且 **N&gt;0 才算前提成立**（N=0 时整条授予被撤销）。
  判定统一为 `EffectEngine.HasPrereqSpec` / `EvaluatePrereq` / `GetPrereqScale`（`ScaleExpr` 优先于 `ScaleByLoyalty`）。
- 表达式扩展：`计数:区域:…:<类别>:…` 新增类别 **`暗藏者`**（按"当前是否背面朝上"计，暗藏只存在于场上）；
  新增 **`计数:封印牌:自身`**（被封印在该卡上的牌数）。
- 重判时机（计数变化即时生效）：暗藏者进出/翻面（`ZoneManager.OnPostMove` / `CovertChanged` → 双方 `ReevaluatePrereqs`）、
  卡上标志增减（`CardManager.AddCounter` / `RemoveCounter`）、封印落地（`TurnManager.SealCard`）。
- 覆盖：**JC037**（每操控一个暗藏者 → 黑眼×N）、**JC045**（其上每有一个时间标志 → 黑星×N）、**JC109**（其上每有一张封印牌 → 黑刀×N）。
- 测试：`P3ScaledGrantTests`(5)。
**⑬ K11 可选支付 + 数值输入框（→ JC061 / JC100）与 K13 袭击叠加（→ JC038）**
- **K11**：新动词 `PayOptional{默认金额, 最小, 最大}` + 通用**数值输入窗口**（用户口径：输入的数字即为 N）：
  单机 `IGameUIHooks.PromptNumberInput` / `Arena.PromptNumberInput` / `NumberInputWindow`；联机 `Kind.NumberInput` + `ChoicePromptPanel`；
  无 UI 兜底＝不支付。`EffectEngine.ExecuteActions` 支持**挂起续算**（`_pendingChain` + `ContinuePendingChain`），
  以承载"回调式"的玩家输入而不阻塞同步结算。覆盖 JC061（进入地区→可选支付1→消灭该地区暗藏者）、
  JC100（被封印时→可选支付1→**封印宿主** -1 防御，`Gain` 主体新增 `"封印宿主"`）。
- **K13**：`IsStrike/StrikeAmount` 标记 + 收集阶段**合并叠加**（自带 ∪ 授予 → 一个触发，伤害＝总和），覆盖 JC038 的【王座会】光环。
- **本轮顺带修掉的引擎缺陷（都是被上面两个特性暴露出来的）**：
  ① **光环授予在"复位/换区"后丢失**：`CardInstance.ResetToBaseState()`（换区/现身复位）会清空运行期授予，但光环登记表仍以为"已授予"→ 永不补回。
     修法：新增**自愈判定** `IsGrantPresent` + 单一实现 `ApplyAuraGrantsTo`（首次授予 / 缺则补授），并在 `CardManager.RevealCard` 复位后立即重算光环。
  ② **光环重算只在 View 里做**（`Arena.OnPostMove`）→ 联机服务端与 headless 路径会漏；已下沉到装配层（`GameInitializer` 订阅 `ZoneManager.OnPostMove` → 双方 `ReevaluateAuras`）。
  ③ **重复撤销把派生数值扣穿**：`CardInstance.Revoke("Attribute", …)` 缺"确实登记过"的护栏 → 多次撤销会把 `CurrentDefense` 等扣到 0/负值。
  ④ **属性重算不回填授予**：`ResetToBaseValues`（`RefreshFirstStrikeStats` 调用）只重建"调查/战斗/先手调查"，未回填 **防御/先手战斗/势力** → 与光环撤销叠加后防御被多扣。
- 测试：`P3OptionalPayTests`(10) / `P3StrikeStackingTests`(6)。

**⑫ K16 模式分支（2026-09，"选择一项"）→ JC063 警车追捕**
- `EffectData.Modes`（`EffectMode{Label, Target, Actions}`）+ `StackEntry.ModeIndex`；口径（用户 2026-09，与 JC063 FAQ 一致）：
  **模式在打出时选定、付费之前**，入堆叠后**不可更改**；结算**只执行所选模式**（`ValidateTargetAtResolve` 的目标复检也按该模式）。
- 入口：单机 `TurnManager.BeginModeCardPlay`（`IGameUIHooks.PromptEffectMode` 选模式 → 取该模式目标 → `PlayCard(..., modeIndex)`）；
  联机 `TurnManager.PlayModeCardAsync`（`Kind.SelectEffectMode` → 选目标 → 入堆叠；取消/超时→不扣费、卡留手牌；
  完成后 `OnAsyncSettlementComplete d` 下发快照）；`Arena`/`ArenaOl`/`ChoicePromptPanel` 各自接入。
- 顺带把"取目标候选"抽成单一实现 `GetTargetUidsForSpec(spec, source, areaOverride, allAreasIfNotLocal)`：
  触发路径沿用"来源所在 SLOT"，**打出路径**（来源在手牌、无地区，且模式目标未声明 `IsLocalZone`）取**全场**争夺区。
- 测试：`P3ModeBranchTests`(9)。

**本轮之后仍未做（等用户确认后才动）**：
**打出规则**的复合目标（多条 `TargetRule` OR —— 效果侧 `KindAlt` 已解决，打出侧本就支持多规则）、
`玩家选择` 型 X（需数值输入窗口）、K29 重置步骤钩子、K20 图标移除、K22 牺牲他人、
K27 手牌外来源（**`PlayCard` 目前没有来源区门禁**）、K3 替代效果（**伤害"防止"已交付** → JC078；
仍缺伤害替代 / 死亡替代 / 去处替代）、K28 免费打出、
（K16/K24 已交付；K24 的触发/打出/联机三条支线见 §7.10 ⑰ 的"仍缺"；K11 的可选支付窗口亦可复用为"数值输入"原语）。

### 7.11 进度表（`JC卡牌效果进度表.md`）的使用方式

- **一行 = 一个效果**（同一张卡多个效果占多行）；三列状态：`效果是否实现` / `效果是否单测通过` / `人工检查效果`。
- 推进流程：① 从表里挑**只差一个机制**的卡 → ② 补机制（用本清单 K 编号定位）→ ③ 回填该批效果行 →
  ④ 接入 `JCEffects` + 补行为用例 → ⑤ 跑全量测试刷新"单测通过"列。
- 表中效果文本取自卡面 `.tres` 的 `Effects` 字段并按能力标签（持续/进场触发/快速行动/行动/触发/…）拆分；
  `[FAQ]` 行是官方规则备注，仅供人工核对（不是效果）。
- **当前进度（2026-09，以 `JC卡牌效果进度表.md` 为准）**：**85/135 卡**已接入（`JCEffects` 注册 85 张）；**279 行效果中 176 行行为 ✅**、81 行 ⛔（缺机制/待归类）、5 行 ⬜、17 行 `[FAQ]` 备注；部分接入 43 张；"其他（暂不排期）"列表 28 条。


