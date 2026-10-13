# JC 效果代码核查项（唯一口径来源）

> **本文件是 JC 扩展包效果代码自查的唯一口径来源。**
> 核查对象：`Cards/Effects/PlayerCards/JC/*.cs`（134 个效果脚本 + `JCEffects.cs` 注册入口）
> 与 `Cards/Data/PlayerCards/JC/*.tres`（135 个卡数据）。
>
> **核查纪律（最新）**：
>  · **静态比对一律自动化**（`Script/Tests/JCEffectSelfAuditTests.cs`）；**必须先构建成功再真跑**——
>    构建失败或编译产物过期时，任何"通过"都不成立（自动化结论只在"真跑过"的前提下有效）；
>  · 扫描命中先判"是检查器口径问题还是实现问题"：**实现问题就修实现**（＋单测＋全量回归），
>    **检查器口径问题就修检查器并写明依据**；确属"卡文如此/引擎通道如此"的例外写入豁免表（**理由必写**）；
>  · 只有**卡文歧义**才停下来回问用户（㉔）。
>
> **记录口径**：只写**现状与结论**（当前实现是什么、与卡文是否一致、依据在哪），**不写"曾经写错过什么"、不写修改过程**。
>
> 判据分两段：**A 段（①–⑨）＝用户给定的主干判据**；**B 段（⑩–㉔）＝按本项目引擎实际字段补齐的判据**，
> 其中 **㉑（效果切分规则）为最高优先级**，它是判据②⑤⑥⑦⑧⑪⑱等的共同前置；
> **㉔（卡文歧义·必须回问）为强制流程判据**。
>
> **已合并的判据不重复体现**：凡已在别处完整表述的判据，**不再单独成条**，只在主判据处一并说明：
> 效果类型（含触发↔持续↔行动类别误用）全部归入 **②**；目标（目标类型 + 目标语义词 + 次数无关的自身约束）
> 全部归入 **⑤**；主体类别一致性归入 **⑫**。核查时**只需查主判据**，不存在"同一条判据查两遍"。
>
> **核查方式口径**：本核查的"静态比对"部分（读卡文 ↔ 读实现、结构/字段/白名单/一致性）
> **不属于"人工核查"**，一律通过**自动化扫描**（`JCEffectSelfAuditTests`）承担；文中"人工"仅指
> **自动化无法覆盖、需要人来确认的裁定类判断**（卡文歧义㉔、规则性裁定），与"人工逐卡核对"不同。
> **两侧统一口径**：每一项（除下表"tres 不体现"的运行期/UI 项）都必须核对"卡面 ↔ 实现"是否一致（见 0.4）。

---

## 0. 核查前置知识（读判据前必读）

### 0.1 效果不在 tres 里
`Script/Data/Effects/EffectData.cs:925-927` 明确：
> 所有具体效果均由卡片 C# 脚本在构造函数中构建并挂入 `EffectDataList`，不序列化进 `.tres`。

实测 135 个 JC tres 中 `EffectTiming` 命中数 = **0**。因此核查必然是
**tres（卡文 `Effects` 文本 + `CategoryFlag` 类别）↔ cs（`EffectDataList`）双向对照**，而非单向扫描。

### 0.2 注册链路
```
JCEffects.RegisterAll()
  → JC0xx_xxx_Effects.Register()
      → CardEffectFactory.Register("<UID>", Build)
          → data.EffectDataList.Add(new EffectData { ... })
```

### 0.3 术语
| 术语 | 含义 |
|---|---|
| **卡文** | `tres.Effects`（`Array[String]`）里的原文，含效果句与 FAQ 段 |
| **效果段** | 卡文中由前缀词切出的最小效果单位，对应**一条** `EffectData`（见㉑） |
| **前缀词** | `行动：`/`快速行动：`/`手牌行动N：`/`触发：`/`进场触发：`/`现身触发：`/`死亡触发：`/`持续：`/`准备步骤强制触发：`/`当…时：` 等（见㉑） |
| **场上** | 仅指 **争夺区（`AreaZone`）** 与 **附属区（`AttachTarget`）**；秘社区(`CouncilZone`)、资产区(`AssetZone`)、封印区(`SealedZone`) 等属扩展区域（见③） |
| **候选人** | 取目标时枚举出的可用目标集合 |
| **静默失败** | 写错/漏声明后引擎不报错、按缺省值处理，导致效果"表面正常但实际不生效" |

### 0.4 统一性口径（卡面 ↔ 实现必须一致；用户 2026-10-10）

**每一项判据都必须检查"卡面描述 ↔ 实际实现"是否统一**，但两侧**能承载的信息量不同**，
故按下表决定"哪一侧是权威依据（锚）"：

| 信息 | 卡面（tres：`Effects` 文本 / `CategoryFlag` / `SubCategories` / 数值） | 实现（cs：`EffectDataList`） | 统一性怎么查 |
|---|---|---|---|
| 效果句 / 前缀词 | ✅ 原文（权威） | ⚠️ 只在脚本注释/`EffectId` 里体现 | 卡面 → 实现：按㉑ 切段，逐段落到效果 |
| 效果类别（触发/持续/行动） | ✅ 前缀词 | ✅ `Timing` | 两侧直接比对（②） |
| 主体类别 | ✅ `CategoryFlag` | —（由数据卡决定） | 卡面 → 实现形态 |
| 子类别（人类/吸血鬼…） | ✅ `SubCategories`（印刷） | ⚠️ 以字符串写在 `TargetRule.Param`（`PlayerCardData.cs:209/215`） | **字符串必须能在 `SubCategories` 找到**（⑯ 已加） |
| 目标类型（角色/暗藏者/地区/玩家…） | ✅ 目标词 | ✅ `Target.Kind/KindAlt` | 两侧直接比对（⑤） |
| 目标语义限定词（其他/另一个/敌方/我方/友方/本地区/全部） | ✅ 措辞 | ✅ `CanTargetSelf`/`Restrictions`(Enemy/Friendly)/`IsLocalZone`/`AllAreas`/`MassMode`/`ExcludeSource` | 两侧直接比对（⑤/⑯） |
| 目标范围（本地区/所有地区/含自身/含暗藏者） | ✅ 措辞 | ✅ `IsLocalZone`/`AllAreas`/`CanTargetSelf`/`IncludeCovert` | 两侧直接比对（⑯） |
| 代价（横置/牺牲/支付 N/额外费用） | ✅ 措辞 | ✅ `Cost`/`SacrificeCost`/`OptionalPayCost`/`ExtraCost` | 两侧直接比对（⑭） |
| 次数 / 阶段门禁 | ✅ 措辞 | ✅ `MaxUsesPerTurn`/`MaxUsesPerGame`/`OnlyPhases`/`ActivateWindow`/… | 两侧直接比对（⑮） |
| 次数·地区级一次（威名/声望） | ✅ 前缀区"威名/声望" | ✅ `KeywordEffects.WeiMing()`/`Renown()` + 引擎**按标签分桶**的广播窗口去重 | 两侧比对（⑮）：时机↔标签**必须成对**（`OnCombatWon`⇒`威名`、`OnPowerCombatEnded`⇒`声望`），两词条同形且互不干扰 |
| 卡文卡名自指（卡文出现与本卡 `Name` 完全一致的卡名） | ✅ `Effects` 原文中出现的卡名 | ✅ 自指通道（动作对象"自身/触发卡/结附宿主"、`TriggerBySelf`） | 两侧比对（⑪） |
| 动态数值 X | ✅ "X"所在句 | ✅ `ValueExpr`/`ValueAtResolve` | 两侧比对 + 求值时机（⑱） |
| 期限 | ✅ "直到回合结束/常驻" | ✅ `Duration` | 两侧直接比对（⑲） |
| 代价的**"入堆叠前支付/不可被响应"** | ✅ 卡文措辞（"作为代价"） | ✅ 引擎在入堆叠前扣代价（`Cost`/`SacrificeCost`/`OptionalPayCost`/`ExtraCost`） | 两侧比对：卡文"代价"⇒ 实现必须为**入堆叠前支付**的代价字段，不得实现成"结算时再付"（⑭） |
| 关键词前缀（灵体/公开/声望…） | ✅ 前缀区 | ✅ `HasInherentKeyword`（由 `EffectDataList` 决定） | 卡面 → 实现（`JCCardTextAuditTests`） |
| 光环筛选 / 落点 | ✅ "本方/其他/组织/未印刷领域" | ✅ `AuraLocalZoneOnly`/`Filter`/`GrantToHost` | 两侧比对（⑰） |
| **目标丢失结算 / 回合结束撤销 / 询问窗口选择 / 按钮置灰 / 服务端权威判定** | ❌ **tres 不体现**（属引擎运行期/UI 规则） | ✅ 引擎侧实现 | **跳过"两侧统一"**，只查实现侧正确性 |

**结论**：除上表"tres 不体现"的运行期/UI 项外，**其余每一项都以本表方式核对"卡面 ↔ 实现"**；
**若某判据无法确定两侧如何对应（说不清该比什么字段），按判据㉔ 向用户询问，不得默认跳过。**

**用户裁定（2026-10-10）**：
- **光环范围**："有『所有』且未写『本地区』" ⇒ **全场**（`AuraLocalZoneOnly=false`）；写"本地区"才限本地区。
- **代价**："入堆叠前支付/不可被响应"**需要两侧统一**（卡文"作为代价"⇒ 实现必须是入堆叠前支付的代价字段）。
- **卡文↔实现的语义对比**：**自动化做"可规则化"的比对**（见判据⑪-b 的四种通道），
  且**即使自动对比通过，也不放弃人工/裁定复核**——两者并行，自动通过不等于语义正确。
- **次数限制·地区级一次**：一回合一次查 `MaxUsesPerTurn`；**威名/声望＝一次广播窗口内每控制者每地区 1 枚**
  （威名＝战斗对抗获胜每次；声望＝每次势力对抗结算完毕各触发一次，用户 2026-09 第二轮口径，见判据⑮）。
- **卡文卡名自指**：卡文出现与本卡 `Name` **完全一致**的卡名时，一律视作"本卡指代"，
  实现须走自指通道，**不得去别处找同名卡**（见判据⑪）。
- **文档不重复**：已合并的判据不单独体现、不重复罗列。

### 0.5 豁免机制（核查结果可用性的前提）
本项目存在大量「卡文出现该词，但按规则确实不要求该装载」的情形
（`Script/Tests/JCCardTextAuditTests.cs:37-63` 已有 13 条先例）。
**没有豁免机制，核查结果不可用**。故：
- 豁免键统一为 `卡UID前缀|判据编号|词`，值＝理由（须写明依据，如"用户 XXXX 口径"）；
- 豁免表登记在台账顶部"豁免登记区"，并在自动化测试内以同构字典落地；
- **新增豁免必须写明理由**，理由为空视为违规。

---

## A 段 · 用户给定 9 项（主干判据）

### 判据① 效果主体：角色 / 事务 / 附属
- **检查对象**：`tres.CategoryFlag`、卡文前缀、`EffectData` 形态三者是否自洽。
- **判据描述**：效果必须明确"卡文写的主体形态"与"脚本装载的效果形态"一致。
  角色卡的效果应围绕"该角色自身/其所在地区"；事务卡是"打出后一次性结算 + 离堆叠即失效"；
  附属卡的效果应围绕"宿主（结附目标）"。
- **字段/落点**：
  - `CategoryFlag` 位掩码（`Script/Enums/Enums.cs:21-39`）：`Character=1 / Sorcery=2 / Attachment=4 / Quest=8 / Land=16 / Token=32`
  - 实测：JC001=1（角色）、JC092=2（事务）、JC020=4（附属）、JC121=5（角色\|附属）、JC048=4（附属）
- **检查方法**：人工（读卡文前缀）+ 自动化（`CategoryFlag` 与主角色的统计）。
- **误判/豁免**：事务卡的"结附"类词条不适用；角色\|附属复合类别卡（如 JC121）需人工判定主形态。

### 判据② 效果类型：行动 / 持续 / 触发（**含类别误用**）
- **检查对象**：`EffectData.Timing`。
- **判据描述**：卡文前缀必须与 `EffectTiming` 分组一致——
  - **B 组·持续/静止**：`Continuous` / `Static`（对应卡文"持续："）
  - **C 组·激活能力**：`CharacterActivated` / `AttachmentActivated` / `CouncilActivated`（对应"行动N："等可发动能力）
  - **C 组·触发时机**：`EnterTrigger` / `UncoverTrigger` / `OnCardDied` / `OnTurnEnded` / `OnCombatWon` / `OnDraw` / `OnAreaWon` / `OnLatent` / `OnPowerCombatEnded` 等
  - **F 组·打出结算**：`OnSorceryCast` / `OnCardPlayed`（对应事务卡"行动："/"快速行动："）
- **类别误用（本判据核心）**：**先用㉑ 按前缀词切段、判定该段属于哪一类（行动/持续/触发），再检查实现是否归到同一类**。两类典型误用必须查出：
  1. **触发效果写成了持续效果**（`Continuous`/`Static`）：卡文是"当…时/XX触发："必须走时机总线，写成常驻注册表时既不会"触发一次"，还会**全天候持续生效**；
  2. **持续效果写成了触发效果**：卡文是"持续：…"（常驻规则/常驻授予），写成 `EnterTrigger`/`UncoverTrigger` 时只在某一瞬间结算一次、之后不再生效；
  3. 补充：**行动（激活能力）** 与**触发**互串同样违规（"行动N：" ⇒ `CharacterActivated`/`AttachmentActivated`；"触发：" ⇒ `*Trigger`/`OnXxx`）。
  - 特殊通道：`StaticEffects`/`StaticKind`（静止规则）、`WinAreaDestination`（去处替代）等"卡文像触发、实现属静止/替代"的合法通道，
    **必须由卡文用语支撑**（写入豁免并附依据），否则视为误用。
- **字段/落点**：`EffectData.cs:43`（`EffectTiming` 枚举，注释已分 B/C/F 组）。
- **检查方法**：自动化（卡文前缀 ↔ `Timing` 映射、类别 ↔ `Timing` 组别映射，二者为同一套用例）+ 人工（多段卡逐个核对、确认"这段是一次性事件还是常驻规则"）。
- **重要限定（与㉔ 联动）**：**卡文本身描述不明确**（例如只写"持续"但语义像一次性、"当…时"其实描述的是常驻规则）时，**不得自行择一实现** ⇒ 走判据㉔ 回问用户。

### 判据③ "场上"的定义只有争夺区 / 附属区
- **检查对象**：`EffectData.Scope`（`ZoneScope`）。
- **判据描述**：默认情况下的效果生效区只能是 **争夺区（`AreaZone`）** 或 **附属区（`AttachTarget`）**；
  任何其它区域（秘社区/资产区/墓地/手牌/牌库/封印区/计分区）必须有卡文明确依据才可声明。
- **字段/落点**：`EffectData.cs:174`（`ZoneScope` 枚举：`AreaZone` / `HandZone` / `AttachTarget` / `AssetZone` / `Graveyard` / `DeckZone` / `SealedZone` / `CouncilZone`）。
  默认推导 `GetDefaultScope`：秘社→`CouncilZone`、附属→`AttachTarget`、其余→`AreaZone`。
- **检查方法**：自动化（枚举 `Scope != AreaZone/AttachTarget` 的效果，逐条要卡文依据）。
- **误判/豁免**：
  - 秘社卡（`CategoryFlag=8`?→实为 `CouncilZone` 落点）本身就在秘社区，属合法；
  - 手牌行动卡（如 JC110"手牌行动4"）显式 `Scope=HandZone` 是**权威依据**；
  - 墓地/牌库等需卡文明确写"在墓地中/从牌库…"。

### 判据④ 默认只在场上**正面朝上**时触发
- **检查对象**：`EffectData.TriggerCovertMode`、`Scope`、`DeckLevelRegistration`。
- **判据描述**：除非卡文**特别描述**在墓地/手牌等非场上区域触发，
  否则效果一律**只在卡牌位于场上（争夺区/附属区）且正面朝上**时触发。
- **字段/落点**：
  - `TriggerCovertMode`（`EffectData.cs:1002`）：默认 `FaceUp`（仅正面可触发）/ `CovertOnly`（仅暗藏者）/ `Either`；
  - 朝向由 `CardInstance.IsCovert` 决定；
  - 判定收口：`EffectEngine.IsEffectActiveInZone`（静态纯函数）+ `ScopeMatchesZone`；
  - 接入点：触发 `TriggerRegistry.cs:220`、持续 `EffectEngine.RegisterPersistent`、激活 `ActivateEligibility.cs:195` 与 `EffectEngine.cs:8534/8547`；
  - 非场上区域触发**没有独立字段**，只能靠显式 `Scope`（`HandZone`/`Graveyard`/`DeckZone`/`SealedZone`）或 `DeckLevelRegistration=true`（`EffectData.cs:1308`，JC124 构筑级全局触发）表达。
- **检查方法**：自动化（`Scope` 非场上区域 或 `CovertOnly` 的效果 ⇒ 必须有卡文依据；`DeckLevelRegistration=true` 必须卡文写明"离场后仍生效/构筑级"）。
- **误判/豁免**：`CovertOnly` 仅限"暗藏者专用能力"（JC041 禁婆 `RequireSourceCovert`、JC054 蒙古死亡蠕虫潜伏触发）。
- **状态门禁（2026-10 用户口径）**：**横置的角色不提供其词条** ⇒ 威名/声望这类"由卡自身状态产出的词条"要求来源卡**未横置**
  （`EffectData.RequireSourceUntapped`，两处收集循环各一道；运行时授予的关键词模板**同一门禁**）；
  声望的"哪一方触发"计数同样**不含**横置者（`RenownRule.CountRenownCharacters`）。判据归口＝⑮，见该条。

### 判据⑤ 是否取目标 / 取什么目标 / 是否多目标（**含目标语义限定词与自身约束**）
- **检查对象**：`EffectData.Target`（`TargetSpec`）、`TargetSlots`、`MinChosenSlots`、`NeedsAreaTarget`。
- **判据描述**：
  1. 卡文写"目标X"⇒ 必须声明对应目标；写多个"目标X和/或目标Y"⇒ 多目标槽；不写目标⇒ 不得凭空取目标
     （群体效果用 `MassMode` 而非 `Target`）。
  2. **目标类型 × 效果说明一致性**：**卡文里的"目标X 类型"必须与 tres/cs 中声明的目标类型一致**。
     例：卡文写"以**角色或暗藏者**为目标" ⇒ cs 的 `Target.Kind/KindAlt`（角色/暗藏者）与 **tres** 的目标说明必须相互一致；
     卡文"目标地区" ⇒ `Kind=Area`；卡文"目标玩家" ⇒ `Kind=Player`。
  3. **目标语义限定词一致性**（本判据核心补充）：卡文里的目标限定语必须与实现侧约束字段一一对应——
     - `其他 / 另一个 / 其它` ⇒ 须**排除自身**（`CanTargetSelf=false` / `SacrificeCost.ExcludeSource` / 版本敏感 `VersionSkipsSelf`）；
     - `敌方 / 对手 / 敌方玩家` ⇒ 目标须限 `Restrictions=Enemy`；`我方 / 友方 / 本方` ⇒ 须限 `Restrictions=Friendly`；
     - `本地区` ⇒ `IsLocalZone=true`；`全部 / 所有地区 / 任一地区` ⇒ `AllAreas=true` 或 `MassMode`；
     - `全部 / 所有 / 每个` ⇒ 群体 `MassMode.AllInArea`（不是单个 `Target`）。
  4. **自身约束**：取目标的效果，**只要卡文没有写"其他/另一个/其它"等排除自身的措辞，就一律允许以自己为目标（`CanTargetSelf=true`）**。
     仅**规则上确实不满足**的卡才例外为 `false`，典型：
     - 自身不可能落入目标大类：来源是事务/附属（自身不是角色/暗藏者候选，如 JC020）、目标大类是暗藏者而本卡触发时已现身（如 JC058）；
     - 规则明确禁止自指：如 **JC066"不能把自己封印在自己身上"**（用户点名）；
     - 自身本就不符合卡文的筛选条件：如 JC088 目标"印刷费用 ≤2 的角色"，而自身费用为 3。
- **字段/落点**：
  - `Target`（`EffectData.cs:1229`，`TargetSpec`）：`Kind`（`TargetKind`，`Enums.cs:101`：`Character`/`Attachment`/`Asset`/`Council`/`Area`/`StackCard`/`Covert`/`Player`/`Seal`/`Deck`/`Graveyard`/`Hand`）、`KindAlt`、`SpecAlt`（OR 复合）
  - `CanTargetSelf`（`EffectData.cs:719`）、`VersionSkipsSelf`（`:734`）、`Restrictions`（`TargetRestriction.Enemy`/`Friendly`）
  - `TargetSlots`（`:1261`）+ `MinChosenSlots`（`:1267`）：多目标（K15）
  - `NeedsAreaTarget`（`:1192`）、`AreaTargetExcludesCardArea`（`:1195`）
  - `MassMode`（`:680`）：`Single` / `AllInArea`（群体，不需玩家选单个目标）
  - 牺牲代价侧：`SacrificeCostSpec.ExcludeSource`（`:397`）/ `LocalZone`（`:395`）
  - **tres 侧**：`tres.Effects` 的卡文原文（"目标角色/目标暗藏者/目标地区/目标玩家"及限定语）；附属的 `TargetRules`/`AllowedTargets`。
- **检查方法（自动化，非人工；与判据⑯ 共用映射表）**：
  - 多目标槽存在时 `MinChosenSlots` 必须 ≥1 且 ≤ 槽数；
  - **卡文目标词 ⇒ 期望 `TargetKind`** 的一致性映射（角色→`Character`、暗藏者→`Covert`、地区→`Area`、玩家→`Player`、
    附属→`Attachment`、资产→`Asset`、秘社→`Council`、墓地→`Graveyard`、手牌→`Hand`、牌库→`Deck`），逐条比对；
  - **卡文语义限定词 ⇒ 期望约束字段** 的映射："其他/另一个⇒排除自身"、"敌方/对手⇒`Enemy`"、"我方/本方⇒`Friendly`"、
    "本地区⇒`IsLocalZone`"、"全部/所有⇒`MassMode`"，逐条比对；
  - **自身约束**：卡文含"本地区 + 目标角色/暗藏者"且**不含**排除措辞 ⇒ `CanTargetSelf` 必须为 `true`；
    为 `false` 时必须落在豁免表（理由须写明规则依据）；逐卡确认每张 `CanTargetSelf=false` 确实属"规则上不满足"而非"漏写/想当然排除自身"。
- **检查方法（与既有测试的关系）**：`JCCardTextAuditTests.TestAudit_JC包_本地区目标需显式声明自身可否` 已按"本地区目标"口径实现（JC002/JC004）；
  本判据把口径**从"本地区目标"扩展到全部取目标效果**（含目标语义限定词）。
- **豁免**：`JC020 / JC058 / JC066 / JC088 / JC100`（各附规则依据，见台账豁免区）。

- **⑯ 2026-10 加固（限制条件的「适用范围」）**：否定/限制型静止规则只约束其**适用对象**——
  `OnlyAttachWithSubCategories` 只约束**附属卡**（非附属 fail-open：事务卡/角色卡以该卡为目标不受限，
  实测 JC078→JC081）；`CannotBeAttached` 只约束**装备**。检查＝数据层自洽（`TestAudit_承诺落地清单`）
  ＋行为级（`TestContract_白名单_只约束附属且仅限结附路径`）。
### 判据⑥ 丢失目标 → **按句号切分的"部分"级结算**（不整条 fizzle）
- **检查对象**：`TurnManager.IsTargetLost`、`EffectEngine.ValidateTargetAtResolve`、`TargetSpec.VersionSkipsSelf`、
  `TargetSlots` + `MinChosenSlots`、以及**效果的"部分"划分**（卡文句号边界）。
- **判据描述（最新口径）**：
  1. **一条效果内部按卡文句号（`。`）切成若干"独立部分"**；一条 `EffectData` 可以承载多个部分
     （部分之间用句内顺序原语表达，见判据㉑-b）——即"多个部分包装进一条效果"。
  2. **目标丢失的判定单位是"部分"，不是"整条效果"**：
     - 丢失的目标只影响**它所在的那个部分**（＝到该部分句号边界为止）；
     - **同一效果内其它部分照常结算**（"尽可能结算"）；
     - 只有当丢失的是**该次打出/发动的必需目标**（整卡目标声明：如事务卡唯一的卡目标/地区目标）时，
       才整体失效——**事务卡** ⇒ 不结算、卡进墓地；**触发/激活** ⇒ 仅跳过本条结算。
  3. **句号是"部分"的唯一边界**：逗号、"然后"、"若…则"是**句内**顺序（判据㉑-b），**不产生独立部分**。
  4. 多目标（K15）另按槽位粒度：失效槽位跳过、其余槽位照常结算（`MinChosenSlots` 自洽）。
- **理由与实测依据（为什么"丢失目标还能结算遏制1"）**：
  - **JC065 审问嫌疑人**：`快速行动：选择目标地区，然后目标对手牺牲一个位于该地区的暗藏者。遏制1。`
    ⇒ **遏制1 是独立句**（句号分隔）⇒ 前句因对手响应翻面而目标丢失时，**遏制1 照常结算**；
    卡文 FAQ 亦明确"即使场上没有暗藏者，你也可以打出审问嫌疑人，**仅仅为了遏制1**"。
  - **JC009 远东六号科研所**：`进场触发：将目标角色封印在远东六号科研所上，然后遏制1。`
    ⇒ 遏制1 与封印**在同一句内**（`，然后` 连接，非句号）⇒ 封印未落地则**该句整体不结算**，遏制1 不结算；
    卡文 FAQ 亦明确"如果未能成功将一个角色封印在其上，**则之后的遏制1 能力无法结算**"。
  - ⇒ 两卡的差异**只由句号边界决定**，与"效果条数"无关；这正是"部分级结算"而非"整条 fizzle"的口径。
- **字段/落点**：
  - 目标丢失判定：`TurnManager.IsTargetLost`（版本号比对）＋ `EffectEngine.ValidateTargetAtResolve`；
  - 现状缺口（按本法执行时的实现要求）：`EffectEngine` 现在按"整条效果"跳过（`EffectEngine.cs:308-322`），
    需改为**按部分判定**：动作序列按卡文句号分组，某组的目标丢失时只中止该组、其余组继续；
  - `TargetSpec.VersionSkipsSelf`（`EffectData.cs:734`）：JC052"其他"版本敏感写法；
  - 多目标部分结算：`TargetSlots` + `MinChosenSlots`（`P3MultiTargetTests`）。
- **检查方法（自动化）**：① 事务卡的"必需目标"缺失/丢失 ⇒ 整卡不结算；② 卡文含"…。…。"多句的效果，
  其动作序列须可按句号分组（部分级判定可落地）；③ 多目标效果必须声明 `MinChosenSlots`。
- **误判/豁免**：卡文写"对每个…都…"（群体）不适用本判据，走判据⑯；卡文只有一句且该句依赖唯一目标 ⇒ 整句不结算（＝本条效果不结算）。

### 判据⑦ 结算中需要询问玩家的部分必须有询问
- **检查对象**：脚本是否调用 `IGameUIHooks.Prompt*`（或等效询问通道）。
- **判据描述**：卡文含"你可以/选择一个/是否/检视/弃一张/展示/任意数量"等**需要玩家决策**的表述 ⇒
  必须有对应询问，**不得用确定性兜底简化**（如"自动选第一个""自动放弃"）。
- **字段/落点**：`Script/Interfaces/IGameUIHooks.cs`：
  `PromptTwoChoice`(:26) / `PromptPayOrSacrifice`(:37) / `PromptDiscard`(:49) / `PromptPickCard`(:64) /
  `PromptCurbMarks`(:80) / `PromptPrediction`(:95) / `PromptMultiTargets`(:113) /
  `PromptEffectMode`(:127) / `PromptEffectModeWithAvailability`(:137) / `PromptNumberInput`(:156) /
  `PromptPlayerTarget`(:172) / `PromptPlayTarget`(:192) / `PromptOptionalPay`(:218) / `PromptInspectCards`(:245)。
- **检查方法**：人工为主（逐段对照卡文与动作原语），自动化辅助（`AskBranch`/`PayOptional`/`Discard` 等动词是否存在对应提示窗口路径）。
- **误判/豁免**：**无 UI/无网关的环境**（headless 测试）按既定确定性兜底是**允许的**（引擎既有设计），但线上路径必须有询问。

- **⑦/⑧ 2026-10 加固（非支付型"你可以…"）**：`EffectData.ConfirmPrompt` ⇒ 触发**入堆叠前**弹
  **两按钮是/否窗**（无输入框）；**选"否"⇒ 不入堆叠、不结算**；无 UI ⇒ 按"否"（fail-closed）。
  检查＝行为级（`TestContract_交互收尾_是窗与持锁`）。
### 判据⑧ 询问窗口是否"合适"
- **检查对象**：`EffectAction.Verb` 与询问窗口的匹配。
- **判据描述**：必须用**语义匹配**的窗口——
  - **是/否**（"你可以支付N"固定金额）⇒ 走 `OptionalPayCost`(`EffectData.cs:1101`) → `PromptOptionalPay` 二选一窗；
    **不得**退回 `PromptNumberInput` 数值输入框；
  - **固定数量**（"弃一张""移除一个标志"）⇒ 不得弹"输入数量"；
  - **多选项文本**（"选择一项：·… ·…"）⇒ 走 `PromptEffectMode`（列表二选一），**不得**弹数值框；
  - **可变数量 X**（"移除X个"）⇒ 才允许数值/分配窗（`AmountByPlayerChoice`）。
  - **文案必须按卡文/数据给（2026-10 用户口径·JC092）**：选择窗口（选地区/选卡/选目标）的提示语必须来自该效果的声明
    （`TargetSlot.Hint` / `EffectData.Hint`），**不得由 UI 硬编码另一类动作的说法**。实测：JC092 死灵召唤
    "选择一个地区，将你墓地中的目标角色牌以暗藏者状态置于该地区"在联机选地区时被显示成移动类效果的
    "请点击一个高亮的地区（将移动至该地区）"。
    检查＝自动（`TestAudit_承诺落地清单` 的『提示文字·联机按数据展示』承诺 ＋ **反向**断言"`ArenaOl.cs` 不得再出现该硬编码文案"）
    ＋行为（`TestB2_5_JC092选地区提示为卡文口径`）。
- **检查方法**：自动化（`PayOptional` 值域 `min!=max && max>1` ⇒ 违规，参考既有用例）+ 人工（选项型是否误用数值框）。

### 判据⑨ 不满足条件时按钮必须置灰
- **检查对象**：`ActivateEligibility`（候选侧）、`EffectEngine.ActivateAbility`（权威侧）、`TurnManager.GetModeAvailability`、`LegalityProjection`。
- **判据描述**：玩家不满足效果使用条件时，UI 按钮**提前置灰**，而不是"点了没反应"；
  候选侧与权威侧必须用**同一套判据**（避免"亮着但发不动"或"能发但没亮"）。
- **字段/落点**：
  - 候选侧 `Script/Manager/ActivateEligibility.cs`（`:22` `GetEligibleAbilities`，`:195` 生效区门禁）；含优先权 / 阶段 `PhaseGateRule` / 次数 / 动态值 / 生效区 / 合法目标 / 前置条件（`ConditionAllowsActivation`）；
  - 权威侧 `EffectEngine.ActivateAbility`；
  - 模式可用性 `TurnManager.GetModeAvailability`（View 只展示不重算）；
  - 联机投影 `LegalityProjection`（只投影当前确实可发动的能力）。
  - **次数用尽 ⇒ "无反馈"（2026-10 用户口径·JC093）**：激活能力本回合/本局次数**全部用尽**时双击
    **不开面板、不弹窗**（不是"开面板再置灰"）；**部分**用尽时仍开面板，把用尽项置灰并标注"（本回合次数已用尽）"。
    单一判定＝`ActivateEligibility.IsAbilityUsesExhausted` / `AllAbilitiesUsesExhausted`
    （单机 `Arena.OpenAttachmentActivateChoice` 与联机 `ArenaOl.OnAttachmentDoubleClicked` / `IsActivationEffectDisabled` **共用**）。
    实测：JC093 怨灵咒刃"每回合至多发动两次"用满两次后仍能双击唤起面板。
- **检查方法**：自动化（激活能力若有前置条件/代价/目标，须能被 `ActivateEligibility` 判定 ＋ ㉖ 承诺『次数用尽·单机/联机静默不开面板』）
  + 行为（`TestB2_6_JC093次数用尽后不可发动`）+ 人工（找"UI 与服务端两套判据"的隐患）。

---

## B 段 · 补充判据（⑩–㉔）

### 判据⑩ 注册完整性
- **判据描述**：`tres` 数 / `cs` 数 / `JCEffects.RegisterAll()` 中 `.Register()` 调用数三者对齐；
  每个 `cs` 都必须在 `RegisterAll` 里被调用（**漏注册 ⇒ 整个效果不生效**）；数量差异卡必须定性。
- **字段/落点**：`Cards/Effects/PlayerCards/JC/JCEffects.cs`（注册入口）。
- **实测事实**：
  - tres = **135**、cs = **134**、`.Register()` 调用 = **134**；
  - **差异卡已定位＝JC125 无知路人**（无 cs、无 Register）——但它是**有意为之**（见㉒）。
- **检查方法**：自动化（数量比对 + 每个 cs 文件名在 `JCEffects.cs` 中可检索到）。

### 判据⑪ 卡文 ↔ 实现一致性（以㉑切段数为准）
- **判据描述**：`tres.Effects` **按㉑切段后的段数** ↔ `EffectDataList` 条数 · 顺序 · 语义 必须对应；
  并与 `JC卡牌效果进度表.md` 的行状态（✅/⛔）一致。
- **字段/落点**：`PlayerCardData.Effects`（卡文）↔ `PlayerCardData.EffectDataList`；`JC卡牌效果进度表.md`。
- **检查方法**：
  - **⑪-a 结构（自动化）**：切段工具得到期望段数，与 `EffectDataList` 逐条比对（段数 + 顺序）。
  - **⑪-b 语义（自动化"可规则化"的比对，人工并行复核，自动通过≠语义正确）**——按**动作动词 ↔ 卡文语义片段**的可枚举映射逐段比对：
    | 卡文语义片段 | 期望动作（`ActionVerb`） |
    |---|---|
    | 造成 N 点伤害 | `DealDamage` |
    | 消灭 / 牺牲 | `Destroy` / `Sacrifice` |
    | 抓 N 张牌 / 弃 N 张牌 | `Draw` / `Discard` |
    | 放置 N 个XX标志 / 移除 N 个标志 | `PlaceCounter` / `RemoveCounter` |
    | 横置 / 重置 | `Tap` / `Untap` |
    | 封印 / 移回手上 | `Seal` / `ReturnToHand` |
    | 检索 / 洗牌 | `Search` / `Shuffle` |
    | 支付 N 费 / 获得 N 临时费 | `Pay` / `GrantTemporaryMana` |
    | 潜伏（翻面为暗藏者） | `Latent` |
    | 检视 / 预测 N | `Inspect` / `Predict` |
    | 消灭结附 / 结附 / 移动到地区 | `DestroyAttached` / `Attach` / `MoveToArea` |
    规则：**卡文命中某语义片段，则该段对应效果必须含该动词**；命中即报由裁定处理（卡文措辞可能对应多种实现）。
  - **⑪-c 关键词条**：前缀区关键词（灵体/公开/声望/…）必须被装载（`JCCardTextAuditTests` 覆盖）。
  - **⑪-d 卡文卡名自指（本判据核心补充）**：卡文（效果句，非 FAQ）中出现的卡名若与本卡 `Name` **完全一致**，
    一律视作**"本卡指代"**，实现必须走**自指通道**（动作对象 `自身`/`本卡`/`触发卡`/`结附宿主`/`封印宿主`；条件谓词 `TriggerBySelf`；`TargetSpec.CanTargetSelf` / `VersionSkipsSelf`），
    **不得去别处找同名卡**。命中即登记（例：JC110"当**闲人的绿地**被封印在一个角色上时"= 本卡自指；JC100"当**尖牙幻象**被封印…"；JC108"只要**乔治·帕佐，织梦人**在场"）。
    仅当名称出现在**效果句**时纳入核查，FAQ 叙述性提及不计。
  - **人工/裁定并行**：句级语义（含"然后/若…则"的因果、隐含条件）自动化无法穷尽，**自动比对不替代人工裁定**。
- **误判/豁免**：
  - **常驻关键词前缀**（灵体/公开/声望/领袖/机动/袭击N/不能被装备结附/XX标志N）**不单独成段**，计入同段的关键字授予；
  - 卡文一句可拆多条 `EffectData` 的**唯一合法情形**＝该句属于不同前缀段（见㉑-a）；
  - 进度表 ⛔ 行（当前仅 JC125）在台账"未实现卡登记区"登记。

### 判据⑫ 主体类别 × `Timing` 自洽（**同判据①：主体类别一致性**）
- **判据描述**：主体类别不允许出现**语义不可能**的 `Timing`——
  - 事务（`Sorcery=2`）不得有 `CharacterActivated` / `AttachmentActivated` / `CouncilActivated`（事务不在场，无"发动能力"）；
  - 非事务不得有 `OnSorceryCast`（那是事务打出结算时机）；
  - 附属（`Attachment=4`）默认 `Scope=AttachTarget`、角色（`Character=1`）默认 `Scope=AreaZone`（除非卡文另有说明）。
- **检查方法**：自动化（`CategoryFlag` × `Timing` 交叉表断言）。

### 判据⑬ 未登记字面量静默失败（fail-closed）
- **判据描述**：脚本写入的**非枚举字面量**（阶段名、表达式、检索语法、条件谓词、动词参数）若未在引擎登记，
  引擎按"不生效/不排除"**静默**处理 ⇒ 表面无报错、效果失效。必须命中引擎白名单。
- **字段/落点**：
  - `ExcludedPhases`（`:945`）/`OnlyPhases`（`:956`）：阶段名（白名单＝`PhaseGateRule` 登记名，如"行动阶段"/"分配战斗伤害阶段"）；
  - `ValueExpr`（`:989`）：表达式（白名单＝`EffectValueResolver`，如"事件数量"/"计数:地区内卡上标志:时间标志:本地区"/"代价数量"）；
  - `Filter`（检索/筛选语法，如"子类别:吸血鬼""组织:鸣钟教派""费用<=2"）；
  - `Condition.Predicate`（`EvaluateCondition` 谓词集，如"WinnerIsSelfInSameArea"/"DiedVampireCharacter"）；
  - `ActionVerb` 与动作参数（`EffectAction.cs`）。
- **检查方法**：自动化（**白名单比对**，白名单从引擎实现处提取，须注明"引擎新增登记后同步白名单"）。
- **误判/豁免**：新增自定义谓词/表达式需先在引擎登记；未登记即违规（除非在豁免表登记"引擎侧待补"）。

### 判据⑭ 代价与发动条件完备
- **判据描述**：卡文的代价（横置/牺牲/移除标志/付费/额外费用）与发动条件必须完整落地，
  且**代价在入堆叠前支付**、不可被响应（除非卡文另有说明）。
- **字段/落点**：`Cost`（`:958`）、`ActivateCondition`（`:957`，如"牺牲本角色"）、
  `SacrificeCost`（`ExcludeSource` 等）、`CounterCostSpec`（标志代价，`AmountByPlayerChoice`）、
  `OptionalPayCost`（`:1101`）、`ExtraCost`（`ExtraCostSpec`，`EffectData.cs:426-447`，如 JC049"额外费用：牺牲一个角色"）。
- **检查方法**：自动化（代价字段存在性）+ 人工（是否漏代价或代价错档）。

### 判据⑮ 次数与阶段门禁
- **判据描述**：`MaxUsesPerTurn` / `MaxUsesPerGame` / `OnlyPhases` / `ExcludedPhases` / `ActivateWindow` / `PlayTiming` / `LimitToActionPhaseOnly` 与卡文表述一致。
- **次数限制（本判据核心补充）**：逐卡核对卡文次数措辞与实现：
  - 卡文含"每回合一次 / 每回合N次" ⇒ 对应 `MaxUsesPerTurn=N`；
  - 卡文含"每局一次 / 整局N次" ⇒ 对应 `MaxUsesPerGame`；
  - **地区级一次（放置去重）**：卡文含 `威名` / `声望` ⇒ 按"**一次广播窗口内、每控制者每地区只放 1 枚势力标志**"核对。
    - **两词条同形（用户 2026-09 第二轮口径）**：
      · `威名`＝`KeywordEffects.WeiMing()`＝ `OnCombatWon` + 谓词 `WinnerIsSelfInSameArea` + `PlaceToken{势力标志,1,威名}`，
        **每次战斗对抗获胜**广播一次 ⇒ 单次战斗内每控制者每地区只放 1 枚；
      · `声望`＝`KeywordEffects.Renown()`＝ `OnPowerCombatEnded` + 谓词 `RenownMajorityInSameArea` + `PlaceToken{势力标志,1,声望}`，
        **每次势力对抗结算完毕**广播一次（JC067 追加的那次同样触发）⇒ 单次势力对抗内只放 1 枚。
      · 卡面原文"一个地区的所有对抗结束后…未横置…"（JC070 等）被上面的第二轮口径覆盖，**不按卡面字面实现**。
    - **机制**：`EffectEngine` 按**去重标签分桶**（`PlacementDedupTags`；桶＝标签，键＝`地区|控制者`），
      广播点 Emit 前调 `BeginPlacementDedupWindow(标签)` **只清本标签的桶** ⇒ ① 窗口内只放 1 枚；② 两个词条**互不干扰**。
      不变式：两次广播之间堆叠必已清空（`TurnManager.Pass`：堆叠非空只结算栈顶、不推进阶段）。
    - **检查**：自动＝时机↔标签成对断言（`OnCombatWon`⇒`威名`、`OnPowerCombatEnded`⇒`声望`）＋源码承诺；
      行为＝`TestContract_去重窗口_威名_多张一次战斗只放一枚`、`TestContract_去重窗口_威名与声望同窗不互吞`、
      `TestBug1_多个声望角色一次势力对抗只放一个标志`、`P3KeywordsTests.TestP3_声望_每次势力对抗触发_同一次只加一个`。
- **字段/落点**：
  - `MaxUsesPerTurn`（`:968`，计数基准＝每卡·每回合·每能力，按 `EffectId` 分别计数）、`MaxUsesPerGame`（`:981`）；
  - 威名/声望：`KeywordEffects.WeiMing()`（`KeywordEffects.cs:202`）/ `Renown()`（`:350`）、`RenownRule.cs`（只做计数）、
    `PlacementDedupTags`（标签单一真相源）、`EffectEngine.BeginPlacementDedupWindow`（按标签分桶）、
    `TriggerEmitPoints.OnCombatWon` / `PowerCombatEnded`（两处同形开启窗口）；
  - `OnlyPhases`/`ExcludedPhases`（见⑬）；`ActivateWindow`（`:936`，"行动"/"快速"）；
  - `PlayTiming`（`:163`）：`MainPhase`/`Response`/`NonResponsive`；`LimitToActionPhaseOnly`；`ActivateCondition`（`:957`）。
- **检查方法**：自动化（激活能力必须声明 `ActivateWindow`；"行动："前缀不得为 C 档；"只能在行动阶段打出"须 `LimitToActionPhaseOnly`；**卡文次数词 ⇒ `MaxUsesPerTurn`/`MaxUsesPerGame` 映射；威名/声望 ⇒ 时机↔去重标签成对**）。

- **⑮ 2026-10 加固（未横置才有词条 · 威名/声望）**（用户口径："威名和声望都只有未横置的角色可以触发"；
  声望"计算双方数量决定哪一方可以触发时不计入横置的声望角色"）：
  · 门禁声明＝`EffectData.RequireSourceUntapped`（`KeywordEffects.WeiMing()` / `Renown()` 各 `= true`）；
  · 消费点＝**两处收集循环**——卡面固有触发（`TriggerRegistry` 主循环）与**运行期授予关键词**的模板通道
    （`template.RequireSourceUntapped && card.IsTapped`）；`CloneKeywordTemplate` 必须**逐项拷贝门禁字段**
    （漏拷＝"模板声明了、运行期授予的那份却不过门禁"——本批实测坑）；
  · 计数＝`RenownRule.CountRenownCharacters` **跳过横置者**（卡文"更多**未横置**的声望角色"）。
  检查＝自动（㉖ 承诺『未横置门禁·声明与消费』『…运行期授予关键词同一门禁』『声望计数排除横置者』）
  ＋行为（`TestB2_1A_威名_横置不触发_未横置触发`、`TestB2_2_声望_横置不计入数量且不触发`、
  `TestB2_2B_JC089授予的威名_宿主横置不触发`）。

- **⑮ 2026-10 加固（声望＝地区级）**：声望触发条件必须同时要求"**来源卡就在该对抗地区内**"
  （复合谓词 `RenownMajorityInSameArea`），且 `PlaceToken` 必带**去重标签**（`威名`/`声望`）——
  否则别处（含跨地区授予）的声望角色会参与触发、并把标志放进错误地区。
  检查＝自动（`TestAudit_JC次数限制_与卡文一致`／`TestAudit_承诺落地清单`）＋行为（`TestBug3c_声望只由本地区角色触发`）。
### 判据⑯ 目标筛选正确性（**只查"限制条件"；目标类型归⑤**）
- **判据描述**：`IsLocalZone`（本地区）/ `AllAreas`（所有地区）/ `IncludeCovert`（含暗藏者）/ `Filter` / `Restrictions` / `MassMode` / `MinCounters` / `ExcludeInChosenArea` 与卡文用词严格一致。
- **字段/落点**：`TargetSpec` 的 `IsLocalZone`、`AllAreas`、`IncludeCovert`、`Filter`、`Restrictions`、`MassMode`、`ExcludeInChosenArea`（多槽）。
- **检查方法（自动化，非人工）**：卡文限制词 ⇒ 期望字段的映射比对（"本地区"→`IsLocalZone`、"所有地区/任一地区"→`AllAreas`、
  "含暗藏者"→`IncludeCovert`、"所有角色/每个角色"→`MassMode.AllInArea`、"本方/敌方/对手"→`Restrictions`）；
  `Filter` 的检索串（如 `子类别:吸血鬼`）必须命中引擎白名单（⑬），且**子类别字符串必须能在 `tres.SubCategories` 找到**（`PlayerCardData.cs:209/215`）。

### 判据⑰ 光环与持续授予的**落点**
- **判据描述**：持续/光环授予的**作用对象**必须与卡文一致——
  - `GrantToHost`（授予**宿主**）vs 授予**自身**；
  - `AuraLocalZoneOnly`（**本地区**）vs 全部争夺区：**卡文写"所有"且未写"本地区" ⇒ 全场**（`AuraLocalZoneOnly=false`）；写"本地区"才限本地区；
  - `AuraFilterKeyword` / `AuraFilterOrganization` / `AuraFilterExcludeDomain` / `AuraRequiresNoDomain`（筛选范围）；
  - "所有**其他本方**角色"必须**排除自身**且限定本方。
- **字段/落点**：`GrantToHost`（`:1235`）、`AuraFilterKeyword`（`:1216`）、`AuraLocalZoneOnly`（`:1223`）、`IsAura`、`AuraRequiresNoDomain`、`AuraFilterExcludeDomain`、`ContinuousCostMod`（`:1243`）、`GrantedAbility`（`:1183`）。
- **检查方法**：人工为主 + 自动化（字段存在性/互斥性）。

### 判据⑱ 动态数值 X
- **判据描述**：`ValueExpr` 必须合法（命中⑬白名单）；依赖的"玩家选择"必须有数值输入/分配 UI；
  `ValueAtResolve` 该开未开会导致**收集阶段 X=0 被整条跳过**。
- **字段/落点**：`ValueExpr`（`:989`）、`ValueAtResolve`（`:1001`）。
- **检查方法**：自动化（`ValueExpr` 非空 ⇒ 命中白名单）+ 人工（"X 由玩家选择"须有分配窗；自身会改变该计数 ⇒ 必须 `ValueAtResolve=true`）。

- **⑱ 2026-10 加固（求值阶段）**：`ValueExpr="玩家选择"` ⇒ 取值**只能发生在结算期**，
  收集阶段的动态值门禁**不得**丢掉该触发（实测 JC083"完全不触发"）；依赖"同效果更早动作"的 X ⇒ `ValueAtResolve=true`。
  另：**收集期只评估 `Condition.Predicate`**（`BranchPredicate` 供结算分支）⇒ 两条事实都要成立的门禁
  必须**合成单谓词**（实测 JC082 用 `WinnerIsSelfAndAbilityUsed`）。
  检查＝自动（`TestAudit_本批BUG回归防线`）＋行为（`TestBug4_JC083准备步骤触发并弹数值窗`）。
### 判据⑲ 期限与撤销（**两面：⑲-a 期限可两侧比对；⑲-b 撤销属引擎运行期**）
- **⑲-a 期限（卡面 ↔ 实现）**：`Duration`（常驻 / 瞬时 / 直到回合结束）**必须与卡文的"直到回合结束/常驻/本回合"等措辞一致**。
  字段：`Duration`（`:1271`）。卡文期限词 ⇒ 期望 `Duration` 的映射比对（自动化）。
- **⑲-b 撤销（引擎运行期，tres 不体现）**：
  所有"授予/抑制/改写"类效果离场时必须被**统一重算撤销**（`GrantedAbility`/`IconSuppression`/`DomainSuppression`/`TextBlank`/`FormChange`/`ContinuousGrants` 的还原路径）。
- **字段/落点**：`Duration`（`:1271`）、`GrantedAbility`（`:1183`，`ReapplyGrantedAbilities`）、
  `IconSuppression`（`:1154`）、`DomainSuppression`（`:1161`）、`TextBlank`（`:1170`）、`FormChange`（`:1147`）、`ContinuousGrants`。
- **检查方法**：⑲-a 自动化（卡文期限词 ↔ `Duration`）；⑲-b 针对性核对实现侧还原路径（自动化可做"直到回合结束"须有还原路径的弱断言）。

### 判据⑳ 结构卫生
- **判据描述**：`EffectId` 唯一且符合命名规范；无空壳效果；无"声明了 `Target` 但动作未消费"；无同卡重复效果。
- **字段/落点**：`EffectId`（`:933`，计数基准，见⑮）、`Actions`（`:1270`）、`Modes`（`:1280`）。
- **检查方法**：自动化（同卡 `EffectId` 去重；`Actions` 与 `Modes` 皆空且非纯持续/静止 ⇒ 空壳告警）。
- **命名规范**：`<卡号>_<字母>_<语义>`，如 `JC048_1_PreparePlaceDamageSacrifice`、`JC022_S_Spirit`。

- **⑳ 2026-10 加固（声明式分工）**：代价型激活效果（横置/支付）的动作由同卡另一条触发承担时，
  `Actions` 可为空——但**必须**登记豁免（键 `卡UID|struct|空壳:<EffectId>` + 理由），不得静默留空。
### 判据㉑ 效果切分规则（**最高优先级**）
> 由用户定义；它是②⑤⑥⑦⑧⑪⑱的共同前置。**必须先按㉑切段，再逐段核查其它判据。**

#### ㉑ 总则
卡文中，以下**前缀词**是效果的**唯一切分标记**：
`行动：` / `快速行动：` / `手牌行动N：` / `触发：` / `进场触发：` / `现身触发：` / `死亡触发：` /
`持续：` / `静止：` / `准备步骤强制触发：` / `结束步骤触发：` / `当…时：` / `每当…时：` / `该能力…`

**从某个前缀词起，直到下一个前缀词或卡文结束为止，其全部文字属于同一条效果；效果内部内容按文字顺序依次结算。**

> **新增子规则（用户 2026-10-10）：按前缀词切完段之后，必须再看一遍"这个前缀词代表的到底是不是效果"。**
> 出现"卡文写着触发，实现写成持续"（或反之）的情况时，**归判据② 处置**，并且在**卡文本身有歧义/描述不明确**时
> 强制走**判据㉔**（回问用户）而不是自行判断。**㉑ 负责"切几段"，② 负责"这段是行动/持续/触发"**。

#### ㉑-a 一段一效果
一个前缀段 ⇒ **一条 `EffectData`**。**不得**把一个前缀段拆成多条同时间的效果（否则收集/结算次序不保证）。

#### ㉑-b 段内顺序结算
段内多句按文字顺序依次结算；出现"然后…"、"若…则…"等**段内**次序敏感结构时，
须由 `TargetSpec.MassSequential = true`（`EffectData.cs:743`）+ **动作级**
`EffectAction.MassInArea`（`EffectAction.cs:211`）/ `RequirePredicate`（`:218`）/ `RequireParam`（`:221`）表达。

> **重要限定**（修正原"凡含'然后…'就用 `MassSequential`"的粗糙表述）：
> **必须先按㉑-a 确认这些结构落在同一段内**；跨段**不得**用 `MassSequential` 揉合。
> 反之：**同一段内**出现次序敏感多句却**没有** `MassSequential` ⇒ **违规**（JC048 类缺陷）。

引擎侧支撑：`EffectEngine.cs:413-418`（`MassSequential` 必须在 `MassTargetPool.IsMass` 分支**之前**判定）
→ `ExecuteSequentialMassActions`（`EffectEngine.cs:820`）→ `ActionConditionMet`（`:863`）。

#### ㉑-c 段内"选择一项"＝Modes
段内"选择一项：·… ·…"用 `EffectData.Modes`（`:1280`）表达，**不拆成多条效果**。

#### 证据链（已核实）
| 案例 | 卡文 | 切分结论 | 实现 |
|---|---|---|---|
| **JC048 诅咒沙漏** | `结附于目标地区 准备步骤强制触发：在诅咒沙漏上放置一个时间标志。然后该地区每有一个时间标志，就对位于该地区的所有角色造成1点伤害。然后若该地区上有三个或更多时间标志，则牺牲诅咒沙漏。`（`JC048_zuzhoushalou.tres:31`） | **只有一个前缀段 ⇒ 只有一条效果** | `JC048_zuzhoushalou.cs:13-22` 注释："整条卡文是一条效果里按顺序处理的三个动作，不得拆成三条强制效果"；`:44 ValueAtResolve=true`、`:50 MassSequential=true`；动作①`PlaceCounter` ②`DealDamage{"$X"}`+`MassInArea` ③`Sacrifice`+`RequirePredicate="AreaMarkerAtLeast"`/`RequireParam="时间标志,3"` |
| **JC110 闲人的绿地** | `灵体 手牌行动4：将闲人的绿地封印在目标角色上。 触发：当闲人的绿地被封印在一个角色上时，你可以横置或重置该角色。`（`JC110_xianrendelvdi.tres:31`） | **两个前缀段 ⇒ 两条效果**（"灵体"＝常驻关键词前缀，不单独成段） | `JC110_A_Spirit`（Continuous 关键字）、`JC110_1_HandActionSealSelfOnCharacter`（CharacterActivated + `Scope=HandZone` + `Cost=4`）、`JC110_2_OnSealedTapOrUntapHost`（`OnSealed` + `Scope=SealedZone` + `Modes` 二选一） |
| **JC063 警车追捕** | `快速行动：选择一项： ·将目标角色潜伏。 ·将目标暗藏者移回其拥有者手上。`（`JC063_jingchezhuibu.tres:31`） | 一个前缀段 + 段内"选择一项" ⇒ **一条效果 + `Modes`** | `JC063_jingchezhuibu.cs:47-61` 一条效果 + 两 `EffectMode` |
| **JC005 裂解术** | `快速行动：消灭目标结附于角色的附属。若你的资产区具有【心】，则改为消灭目标附属。`（`JC005_liejieshu.tres:31`） | 一个前缀段 ⇒ **一条效果**；"若…则改为…"是**段内目标筛选**，落到 `Target.Restrictions`，**不**拆两条 | `JC005_liejieshu.cs:36-39` `AttachedToCharacterIfNoMind` |

#### 检查方法
- **自动化**：以正则按前缀词切分 `tres.Effects` 原文（先剥离 FAQ 段）得到"段列表"；段数作为㉑-a 的期望 `EffectDataList` 条数下界；段内含"然后…"却无 `MassSequential` ⇒ 告警；段内含"选择一项"却无 `Modes` ⇒ 告警。前缀词表以集中常量维护，并注明"卡库新增前缀写法后需同步"。
- **人工**：多段卡逐个核对"段 ↔ 效果"边界；确认"条件分支≠新效果"。
- **误判/豁免**：
  - 常驻关键词前缀（灵体/公开/声望/领袖/机动/袭击N/不能被装备结附/XX标志N）**不单独成段**，计入同段关键字授予 ⇒ 段数 = `EffectDataList` 条数可能是"段数 + 关键词条数"；
  - 卡文里"（…）"括号内的补充说明（如"（将该角色翻面为暗藏者）"）不构成新段。

### 判据㉒ 构筑级 / 非游戏内规则
- **判据描述**：卡文中的"构筑限制/牌组张数限制/规则性说明"**不属于场上效果**，
  不应简单用 `EffectTiming.Continuous` 表达；应落到构筑校验层或专用标记。
- **字段/落点**：参考先例 `DeckLevelRegistration`（`:1308`，JC124 构筑级全局触发）、`ExtraCostSpec`（`:426-447`）、`StaticKind`（如 `UniquePathAtMostOne`）。
- **实测事实**：
  - **JC125 无知路人**（`JC125_wuzhiluren.tres:27`）：`持续：名称为无知路人的牌，构筑时在牌组中的数量没有限制。`（`CategoryFlag=1`）——
    是**构筑规则**，**无 cs / 无 Register**；`JC卡牌效果进度表.md:299` 标 `⛔ 未实现（K25 构筑校验（GameDeckValidator））`，用户明确"暂不做"。
- **检查方法**：人工（识别构筑级文字）+ 自动化（`UnimplementedCards` 集合登记＋进度表 ⛔ 对齐）。

### 判据㉔ 卡文歧义必须回问用户（**强制流程判据**）
- **判据描述**：核查（以及据此进行的任何实现）过程中，凡遇**卡文描述不明确 / 存在多种合理读法**的情形，
  **必须停下来找用户确认**，不得按"最省事的读法"自行落地。桌面游戏原始描述不明确是**已知的误用主要来源**。
- **必须回问的典型情形**：
  1. **类别歧义（判据②）**：该段究竟是"触发"还是"持续"（例：只写"持续"但语义像一次性事件；写"当…时"但整段描述常驻规则）；
  2. **目标范围歧义**：是否含自身（"本地区的目标角色"）/ 是否含暗藏者 / 是否含本方；
  3. **数量/次数歧义**：X 的取值口径、每回合次数、是否可放弃；
  4. **区域歧义**：是否限"本地区"、是否含特定区域（秘社/资产/封印）；
  5. **归属歧义**：授予**宿主**还是**控制者**、代价由**哪一方**支付；
  6. **时机歧义**：卡文短语与引擎时机不完全对应（"当…时"对应哪个 `EffectTiming`）。
- **落点**：核查时**当场停下回问**；每次回问须写明**卡文原文 + 两种以上读法 + 各自影响的实装差异**，便于用户一次裁定；裁定后按裁定实现并只保留裁定口径。
  （**不列"曾经怎么写的"**，只写"现状哪一种读法 + 需要确认什么"。）
- **检查方法**：人工为主。核查员遇上述情形即**登记而非实现**；
  自动化辅助：把"卡文短语 ↔ `Timing` 无唯一映射"的卡自动列成候选（本轮以人工清单为准）。
- **纪律**：**判据㉔ 优先于②**——先确认卡文要什么，再检查实现；卡文不清则一律回问。

---

## 判据 ↔ 检查方式 总览

> **纪律**：本表由测试强制校验（`TestAudit_自检_判据覆盖矩阵`）——凡标"自动/行为"的判据，
> 其**检查方法必须真实存在**（反射校验）；标"人工"的必须写明落点；标"并入"的必须写明并入哪条判据。
> **判据不得只有文档、没有检查。**

| # | 判据 | 类型 | 检查方法（测试） |
|---|---|---|---|
| ① | 效果主体 | 并入 | 并入⑫（主体类别 × Timing 自洽） |
| ② | 效果类型：行动/持续/触发（含类别误用） | 自动 | `TestAudit_JC类别_触发与持续不得误用` |
| ③ | "场上"＝争夺区∪附属区 | 自动 | `TestAudit_JC区域_Scope仅场上区或需卡文依据` |
| ④ | 默认仅场上正面朝上 | 自动 | `TestAudit_JC朝向_CovertOnly与构筑级需卡文依据` |
| **⑤** | 目标：类型＋语义限定词＋多目标＋自身约束 | 自动 | `TestAudit_JC目标_声明自洽` |
| ⑥ | 丢失目标＝按句号部分级结算 | 自动 | `TestAudit_JC目标_声明自洽` |
| ⑦ | 需要询问处必须有询问 | 自动 | `TestAudit_JC实现_动作动词与卡文语义一致` |
| **⑧** | 询问窗口是否合适（含**文案按卡文/数据给**） | 行为 | `TestContract_交互收尾_是窗与持锁` |
| ⑨ | 不满足条件置灰（含**次数用尽 ⇒ 无反馈**） | 自动 | `TestAudit_JC目标筛选_限制条件与卡文一致` |
| ⑩ | 注册完整性 | 自动 | `TestAudit_JC注册_数量对齐` |
| **⑪** | 卡文 ↔ 实现一致（含卡名自指） | 自动 | `TestAudit_JC卡文_卡名自指登记` |
| ⑫ | 主体类别 × Timing 自洽 | 自动 | `TestAudit_JC类别_与Timing自洽` |
| ⑬ | 未登记字面量静默失败 | 自动 | `TestAudit_JC白名单_阶段名与取值与谓词已登记` |
| ⑭ | 代价与发动条件 | 自动 | `TestAudit_JC代价_关键代价字段存在性` |
| ⑮ | 次数与阶段门禁（含威名/声望的放置去重窗口） | 自动 | `TestAudit_JC次数限制_与卡文一致` |
| ⑯ | 目标限制条件（含**适用范围**） | 行为 | `TestContract_白名单_只约束附属且仅限结附路径` |
| ⑰ | 光环/持续授予落点与范围 | 自动 | `TestAudit_承诺落地清单` |
| ⑱ | 动态数值 X（含**求值阶段**） | 自动 | `TestAudit_本批BUG回归防线` |
| ⑲ | 期限与撤销 | 自动 | `TestAudit_JC期限_Duration与卡文一致` |
| ⑳ | 结构卫生（含声明式分工豁免） | 自动 | `TestAudit_JC结构_EffectId唯一与非空壳` |
| **㉑** | 效果切分规则 | 自动 | `TestAudit_JC切分_段数与效果条数一致` |
| ㉒ | 构筑级/非游戏内规则 | 自动 | `TestAudit_JC注册_数量对齐` |
| ㉓ | 类别误用 | 并入 | 并入② |
| **㉔** | 卡文歧义必须回问用户 | 人工 | 落点＝本文件判据㉔、《台账》卡文歧义登记区 |
| ㉕ | 取目标默认允许自身 | 并入 | 并入⑤ |
| **㉖** | 承诺必须落地（声明↔消费点↔行为用例） | 自动 | `TestAudit_承诺落地清单` |
| **㉗** | 多声明 OR 一致 | 行为 | `TestContract_多声明OR_两类别各自可结算` |
| **㉘** | 单一真相源的旁路同判 | 行为 | `TestContract_屏障_候选与合法性四路径一致` |
| **㉙** | 落地路径完备性与交互收尾 | 行为 | `TestContract_交互收尾_数值窗单机与联机` |
| **㉚** | 数值下降后复检派生状态（死亡判定） | 行为 | `TestB2_4A_JC089减防致死_防御1未受伤` |
## 附：核查产出的问题分级
| 级别 | 含义 | 示例 |
|---|---|---|
| **P0 静默失效** | 效果完全/基本不生效，无任何报错 | 漏注册、前缀词条漏装、`CanTargetSelf` 漏声明致候选为空、未登记阶段名 |
| **P1 语义错误** | 效果能生效但结果与卡文不符 | 拆段错误、`MassSequential` 缺失、目标范围过宽/过窄、光环范围错 |
| **P2 交互不当** | 结果正确但询问窗口/置灰不合适 | 固定金额弹数值框、选项型弹输入框、不满足条件不置灰 |
| **P3 规范问题** | 不影响行为，属结构卫生 | `EffectId` 命名、空壳效果、注释缺失 |

> 查出的实现问题直接修（＋单测＋全量回归）；只有卡文歧义按㉔回问，裁定后按裁定实现。

---

## 附二 · 判据最新口径（定稿）

> 本节是各判据在**当前引擎实现**下的权威口径，替代早期较粗的表述；只写"怎么查、依据在哪"，不含任何待办/建议。

### ②/㉓ 效果类别（触发 ↔ 持续 ↔ 行动）
- 前缀词 → 实现组别：`触发/进场触发/现身触发/死亡触发/准备步骤强制触发/结束步骤触发/当…时` ⇒ 触发类 `EffectTiming`（`EnterTrigger`/`UncoverTrigger`/`OnCardDied`/`OnTurnStarted`/`OnTurnEnded`/`OnCombatWon`…）；
  `持续/静止` ⇒ **必须**落在 `Continuous`/`Static`；`行动N/快速行动N/手牌行动N` ⇒ `CharacterActivated`/`AttachmentActivated`（＋`ActivateWindow`/`Cost`）。
- **事务卡**（`CategoryFlag` 含 `Sorcery`）的 `行动：`/`快速行动：` 段＝**打出即结算**（`OnSorceryCast`），**不按激活能力**判定。
- 认可的"非触发通道"：`DamageRedirect`/`TextBlank`/`IconSuppression`/`DomainSuppression`（替代与抑制）、
  `StaticKind`（地区级静止规则）、`WinAreaDestination`（K3 去处替代）、`StaticKind.AllowInspectLocalCovert`（随时检视）、
  `GrantedAbility`（K13 运行期授予能力）、`PlayableFromGraveyard`（`墓地行动：`＝从墓地正面打出的**许可**，不是效果段）。
- **前缀关键词本体条**（`KeywordEffects.*` 产出：领袖/威名/机动/袭击/杀伤/灵体/公开/隐匿/撤回/屏障/标志…）对应**前缀词条**，不计入"效果段"。
- **已知偏差（按本法执行时应改、优先级已裁定延后）**：**JC034 心兽之道**的"持续："段（"当你赢得一次战斗对抗时，你可以令目标对手弃一张手牌"）应按**持续效果**结算，现由 `OnCombatWon` 触发通道承载。

### ③ "场上"与区域 `Scope`
- 合法默认：`AreaZone`（争夺区）/`AttachTarget`（附属）。
- **关键词载体**（`ContinuousGrants` 含 `Kind="Keyword"`，如"公开/隐匿/灵体"）按 `KeywordEffects` 设计在**手牌区生效**（`Scope=HandZone`），不属越权。
- `SealedZone`：卡文写"**被封印**在…上时"即成立（JC100/JC110）。
- 其余非场上区（墓地/牌库/资产/秘社/手牌行动）必须卡文明确依据。

### ④ 朝向与构筑级
- `CovertOnly`（仅暗藏者触发）须卡文含"暗藏/潜伏"。
- `DeckLevelRegistration`（构筑级全局触发）须卡文写明"**离场后仍生效/仍然生效/构筑**"（JC124）。

### ⑤/㉕ 目标（类型 + 语义限定词 + 自身约束）
- 目标类型映射：角色→`Character`、暗藏者→`Covert`、地区→`Area`、玩家→`Player`、附属→`Attachment`、资产→`Asset`、秘社→`Council`、墓地→`Graveyard`、手牌→`Hand`、牌库→`Deck`。
- **同一目标也可由非 `Target.Kind` 通道承载**，均视为已声明：
  · "角色或暗藏者" ⇒ `Kind=Character` ＋ **`IncludeCovert=true`**（本工程既定写法）；
  · "目标角色牌（墓地/手牌/牌库）" ⇒ `Kind=Graveyard/Hand/Deck` ＋ `Filter="类别:角色"`；
  · "目标地区" ⇒ `TargetRules` 含 `Area`，或 `NeedsAreaTarget=true`；
  · "暗藏者"作为**牺牲/代价池** ⇒ 动作参数（如 `SacrificeChoice{"暗藏者",…}`）。
- 语义限定词**只在目标子句内**生效：出现在**代价/条件/归属/其它卡**语句里的"本方/敌方/另一个"不算目标限定（JC009/JC012/JC017/JC023/JC055/JC094/JC105/JC108 属此列）。
- **㉕ 默认允许取自身**：取目标的效果，卡文未写"其他/另一个/其它"⇒ `CanTargetSelf=true`（判定仅在**来源卡本身是角色**时适用）。
  例外只限"规则上确实不满足"：自身非该大类候选（附属/事务来源、目标为暗藏者而本卡正面）、发动代价＝牺牲自身（入堆栈前已离场）、
  规则禁止自指（不能把自身封印/结附在自身上）。
- 多目标（K15）：`TargetSlots` ＋ `MinChosenSlots`；卡文写"**至多/最多 N 个**"⇒ 允许 `MinChosenSlots=0`。

### ⑥ 丢失目标 ⇒ **按句号切分的部分级结算**
见判据⑥正文（句号＝部分边界；丢失目标只中止其所在部分；事务卡必需目标丢失⇒整卡不结算；触发/激活⇒仅跳过本条）。
理由示例：**JC065**（`…牺牲一个…暗藏者。遏制1。` ⇒ 遏制1 独立成句，照常结算）；**JC009**（`…封印在…上，然后遏制1。` ⇒ 同句，整体不结算）。

### ⑦/⑧/⑨ 询问与置灰
- 卡文含"你可以/选择一个/是否/任意数量/检视"等决策 ⇒ 必须有 `IGameUIHooks.Prompt*`（单机窗口）或网关请求（联机）。
- 窗口形态按语义选：是/否 ⇒ 两按钮（`PromptOptionalPay`/`PromptTwoChoice`/`PromptPayOrSacrifice`）；多个选项卡 ⇒ `Modes`；
  数值 ⇒ `PromptNumberInput`/`PromptPrediction`/分配窗；**不得**用"退化数值框"表达是/否。
- **询问时机必须对**：卡文"**你可以支付N，若你如此做…**"（触发类）的费用是**发动能力时**支付的额外费用 ⇒
  必须用 `EffectData.OptionalPayCost=N`（**取完目标 → 入堆叠之前**问一次是/否），**不得**写成结算动作
  `ActionVerb.PayOptional`（那是"效果处理时才问支付"）；不支付 ⇒ 该触发不入堆叠；付不起 ⇒ 不弹窗（fail-closed）。
  收口＝`TurnManager.EnsureOptionalPayThenPush` / `...Async`。已按此口径落地：JC061 / JC100 / **JC105**。
- 前置不满足（阶段/次数/代价/目标/代价对象）时 UI 与服务端**共用同一判定**并置灰。

### ⑩/⑪/⑪-b 注册、自指、语义↔动作
- 每个 `Cards/Effects/PlayerCards/JC/*.cs` 必须在 `JCEffects.cs` 注册；`tres`/`cs`/注册三者数量对齐（未实现卡登记在案）。
- **卡名自指**：卡文（非 FAQ）出现与本卡 `CardName` 完全一致的名字时，实现必须有**自指通道**之一：
  动作对象"自身/触发卡/结附宿主/封印宿主"、条件 `TriggerBySelf`、`Target.CanTargetSelf`、激活条件"横置/牺牲本角色"、
  非光环的持续授予（授予自身）、`StaticEffects`、`WinAreaDestination`、自指动词（`PayOrSacrificeSelf`/`SacrificeSelfOptional`/`PrepareUpkeep`/`TransformForm`/`RevealSealedAndQueueFreePlay`）。
  名字出现在**效果主体/触发条件主语**位置（来源即本卡）时无需额外字段。
- **⑪-b 语义↔动作**（高置信映射，动作集合含 `Modes[].Actions`，**先剥圆括号补充说明**）：
  `造成`→`DealDamage`（`DamageRedirect` 亦算）；`消灭`→`Destroy`/`DestroyOnTarget`/`DestroyWithMinCounters`；
  `封印`→`Seal`（`DestroyOnTarget{"封印牌"}`、`Search{…封印}` 亦算）；`检索`/`寻找`→`Search`；
  `洗牌`→`Shuffle`/`Search`（`Search` 内部自动洗牌）；`预测`→`Predict`；`检视`→`Inspect`；
  `抓一张/抓两张`→`Draw`/`DrawFromBottom`；`移回其拥有者手上`→`Bounce`/`MoveToZone`/`RevealSealedAndQueueFreePlay`；
  `潜伏`→`Latent`/`TransformForm`；`重置`→`Untap`；`放置`→`PlaceCounter`/`PlaceToken`/`PlaceHandCardToArea`/`UnsealToArea`。
  代价/条件词（横置/牺牲/支付/移除/伤害/抓/弃）不做动词要求（多由 `Cost`/`SacrificeCost`/`CounterCost`/触发条件/`Search` 承担）。

### ⑬ 白名单（未登记即静默失效）
阶段名（`分配战斗伤害阶段`/`行动阶段`/`对抗阶段`）、`ValueExpr`（`事件数量`/`代价数量`/`已付代价数量`/`玩家选择` 与 `计数:…` 前缀）、
`Condition.Predicate`/`BranchPredicate` 必须命中引擎登记；引擎新增登记后需同步白名单。

### ⑭ 代价完备
- 卡文写"**作为代价**"（或"支付N，否则…"）⇒ 必须是**入堆叠前支付**的代价字段（`Cost`/`SacrificeCost`/`CounterCost`/`OptionalPayCost`/`ExtraCost`）。
- 卡文只写"移除…标志"时按**效果**判定（`RemoveToken`）；无代价依据不得强行要求代价字段。

### ⑮ 次数与阶段门禁
- "每回合只能发动一次/限一次"⇒ `MaxUsesPerTurn`；"每局一次"⇒ `MaxUsesPerGame`；"只能在行动阶段发动"⇒ `OnlyPhases`（白名单）或行动窗口。
- **威名/声望**只在**前缀区**出现时才算本卡词条（效果句里的"获得声望"是**授予**），按"**一次广播窗口内每控制者每地区 1 枚**"核对：
  威名＝**每次战斗对抗获胜**广播一次（`OnCombatWon`）；声望＝**每次势力对抗结算完毕**广播一次（`OnPowerCombatEnded`，JC067 追加的那次同样触发）。
  两词条**同形**：广播点 `BeginPlacementDedupWindow(PlacementDedupTags.X)` **按标签分桶**清桶，`PlaceToken` 查本桶。

### ⑯ 目标筛选
- "本地区" ⇒ `IsLocalZone=true`，或 `Restrictions` 含 `LocalZone`，或**群体范围**（`MassMode.AllInArea` 的集合本身），或**牺牲代价** `SacrificeCost.LocalZone`。
- "所有/每个角色" ⇒ `MassMode.AllInArea`（不是单目标 `Target`）；"含/以及暗藏者" ⇒ `IncludeCovert=true`；`Filter` 检索串须命中引擎白名单且子类别能在 `tres.SubCategories` 找到。

### ⑰ 光环与授予落点
- `GrantToHost=true`＝授予**结附宿主**；无该标志的持续授予＝授予**来源卡自身**。
- 光环范围：卡文写"**本地区**"⇒ `AuraLocalZoneOnly=true`（JC059）；写"所有…"且未写本地区 ⇒ **全场**（`AuraLocalZoneOnly=false`，JC105）。
- 筛选：`AuraFilterKeyword`/`AuraFilterOrganization`/`AuraRequiresNoDomain`（"未印刷魔法领域图标"⇒`true`，JC060）/`AuraFilterExcludeDomain`；
  "所有**其他本方**角色" ⇒ 同控制者集合（`ReevaluateAuras` 只遍历来源控制者的卡）＋排除自身。
- **"获得［某能力图标］"必须双通道**：`Icon:<图标>`（存在性/展示/图标替换与抑制的判定基准）＋ `Attribute:<对应属性>`（数值）；
  `ApplyAuraGrantsTo` **不做** Icon→Attribute 折算（仅形态授予与 `Gain` 动词折算）⇒ 两通道不重复加成。

### ⑲ 期限与撤销
- ⑲-a：卡文"直到回合结束/本回合" ⇔ `Duration` 含"回合结束/本回合"；"常驻"⇔ `常驻`。
- ⑲-b：授予/抑制/改写类效果离场时由 **`EffectEngine.ReapplySuppressions`** 统一重算（先精确还原、再按当前局面施加）；
  `GrantedAbility`/`IconSuppression`/`DomainSuppression`/`TextBlank`/`FormChange`/`ContinuousGrants` 同入口。

### ㉑ 效果切分（最高优先级）
- **前缀命中（最新实现）**：核心前缀词（`准备步骤强制触发`/`结束步骤强制触发`/`潜伏强制触发`/`现身强制触发`/`进场触发`/`现身触发`/`死亡触发`/
  `快速暗藏者行动`/`快速暗藏行动`/`手牌行动`/`墓地行动`/`快速行动`/`行动`/`触发`/`持续`/`静止`）
  在 `：` 左侧 **40 字窗口**内命中、且窗口内无另一个 `：` 与句号 ⇒ 成立；**容忍修饰语**（数字、"2"、"1"、"N"、"，"、"横置"、"牺牲X"等）。
- **㉑-a**：一个前缀段 ⇒ 一条 `EffectData`；可自动判定部分＝"**有动作/模式的效果条数 ≤ 段数**"（关键词本体条、声望规则本体条、`墓地行动`段不计）。
- **㉑-b**：段内"然后/若…则"是**句内顺序**，由动作数组声明顺序保证；**仅当该段是群体效果**（`MassMode != Single`）时须 `MassSequential=true` ＋ 动作级 `MassInArea`/`RequirePredicate`/`RequireParam`。
- **㉑-c**：段内"选择一项"用 `EffectData.Modes`（一条效果），不拆多条效果。
- 卡文圆括号 `（…）` 内为规则解释，不产生动作要求、不构成新段。

### ㉒ 构筑级
JC125 无知路人（"构筑时牌组张数不限"）按用户口径**暂不落地**（无场上效果、无同名卡上限校验器）⇒ 无脚本是正确的。

### ㉔ 卡文歧义
遇歧义当场回问用户（给出卡文原文 + 两种以上读法 + 实装差异）；裁定后按裁定实现，文档只保留裁定口径。
**已确认口径**：JC036"印刷类别为人类"⇒读**模板**；JC091"人类角色"⇒读**实例当前**类别；JC047"所有角色"⇒**不含暗藏者**；
JC105 光环"所有其他本方灵体角色"⇒**全场**；JC059 光环写"本地区"⇒限本地区；JC060"未印刷魔法领域图标"⇒`AuraRequiresNoDomain=true`；JC034 见②的已知偏差。

### 验证纪律
1. **先构建成功再真跑**：`dotnet build UWPro.csproj` 0 错误 → headless 全量（`res://Scenes/TestsRunner.tscn`）0 失败；
2. 自动化套件必须含**防空转自检**：扫描规模（≥134 张卡且 `EffectDataList` 非空）、关键事实断言、
   **豁免理由非空**（`卡UID|判据|词` → 理由，理由为空视为违规）；
3. 实现侧改动必须补单测；文档只保留最新口径。
### 附二补遗 · 2026-10 实测 BUG 批反推出的口径

- **⑮ 声望去重**：`PlaceToken` 的去重标签**必须带**（`威名`／`声望`），去重维度＝同一控制者＋同一地区＋同一次广播
  （`OnCombatWon` / `PowerCombatEnded` 广播前**各清自己标签的桶**）⇒ **一个地区内多个声望角色只放 1 枚势力标志**（参考威名）。
- **⑤/⑯ 附属白名单的适用范围**：`OnlyAttachWithSubCategories`（JC081「除装备与祝福外不受附属」）只约束
  **附属卡**（`PassesAttachWhitelist` 对非附属 fail-open）⇒ 事务卡/角色卡以该卡为目标**不受限**。
- **⑤/⑯ 争夺区候选的判定链**：取目标阶段的候选枚举（触发/模式卡/激活）必须与打出、结算复检**共用**
  `TargetValidity.CheckRestrictions`（含**屏障**）——只按"大类映射"过滤会漏检屏障与 `Restrictions`
  ⇒ 被授予屏障的角色仍会被『袭击』等选为对象（用户实测 JC073）。
- **⑥ 多目标类别声明的 D16 复检**：卡上并列多条目标声明（"选择一项：角色／本方秘社"）时，结算复检必须按
  **任一命中即合法**（`PassesAnyDeclaredTargetDesc`，与 `IsLegalPlayTarget` 同款 OR）；只按首条复检会把合法目标
  误判为"丢失"⇒ 卡进弃牌堆（用户实测 JC082）。
- **⑱/⑬ 玩家选择型动态值**：`ValueExpr="玩家选择"` 的取值**只能发生在结算期**（`BeginPlayerValuePrompt`：单机
  `PromptNumberInput`／联机 `NumberInput`），因此**收集阶段不得**用动态值门禁丢掉该触发（否则连窗都不弹，JC083 实测）；
  必须显式声明 `ValueMin=0`（A31：0 合法）与上限。
- **㉑-c "选择一项"的两种载体**：
  · **效果内**选择一项（差异在动作）⇒ `EffectData.Modes`（JC063/JC110）；
  · **打出时**选择一项（差异在**结附/取目标类别**，效果本身按宿主各自生效，无法用 `Modes` 表达）⇒
    `PlayerCardData.PlayChoice = true`，选项＝该卡各 `TargetRules` 类别，流程＝`BeginModeCardPlay`
    （模式窗 → 该项目标窗 → `PlayCard(modeIndex)`，JC082）。
### 附二补遗二 · 2026-10 实测反馈二批

- **⑮ 声望＝地区级**：触发条件必须同时要求"**来源卡就在该对抗地区内**"（复合谓词 `RenownMajorityInSameArea`）。
  仅用"多数"会把**别处**（含 JC074 跨地区授予）的声望角色也算进来，而 `PlaceToken` 按来源地区落点 ⇒ 标志放进错误地区。
- **⑳ 声明式分工**：若某条激活效果只承担**代价**（横置/支付），动作由同卡的另一条触发承担，
  其 `Actions` 为空是设计（登记结构豁免，键 `卡UID|struct|空壳:<EffectId>`）。
- **⑦/⑧ "你可以…"（非支付型）**：时机到达时必须**在入堆叠前**弹"是/否"选择窗
  （`EffectData.ConfirmPrompt`；两按钮、无输入框）；**选否⇒不入堆叠、不结算**；无 UI ⇒ 按"否"（fail-closed）。
  禁止用 `Pending` 一次性承诺冒充（那会在时机到达时再走一次触发/排序窗——用户口径）。
- **条件求值的阶段差异**：触发收集期**只评估** `Condition.Predicate`（不评估 `BranchPredicate`）⇒
  凡"两条事实都必须成立"的门禁（如"本方获胜 ∧ 本回合已发动该能力"）必须合成**单谓词**。
- **单机/联机孪生纪律**：任何"打出/取目标/入堆叠前询问"的改动，必须**同时**检查四个入口——
  `CardDragController`（单机拖拽）、`Arena`/`TurnManager.BeginModeCardPlay`（单机流程）、
  `ArenaOl`（联机客户端）、`PlayerActionService.TryBegin*Online`＋`TurnManager.*Async`（服务端驱动）。
- **结附落点登记**：附属进入 `AttachedZone` 后必须 `RegisterContinuous`——**结附秘社的分支不得提前 `break`**
  （否则该附属的持续授予与激活能力全部不生效）。
- **交互后刷新**：凡"回调里同步重入结算"的 UI 窗（数值输入等），回调返回前必须刷新 View
  （`ArrangeAllCardsInternal`/`UpdateTurnUIInternal`），否则界面停留在结算前状态。
#### 补遗二之补 · 2026-10 实测第三批（PM 命令与联机刷新）

- **调试命令必须走"与生产同款"的登记纪律**：`ZoneManager.MoveToAttachedZone` 的自动登记发生在
  `AttachedTo` **尚未建立**的时刻（这是为了避免 `DetachAttachmentsToGraveyard` 递归而必须的次序），
  因此**任何**结附落地（生产结算 / `PMCommandManager.attach` / `attacharea`）都必须在**绑定建立之后**
  再 `RegisterPersistent` 一次（幂等），否则"授予宿主"（`GrantToHost`）类持续效果拿不到宿主，
  表现为【屏障】等词条**静默不生效**（用户实测：JC073 用 PM attach 后宿主仍能被袭击选中）。
- **凡"回调型挂起"的玩家交互（数值窗、确认窗等）**：回包续算后必须走 `ContinuePendingChain()`
  ⇒ `TurnManager.OnEngineChainSettled()`（含 `NotifyResolutionSettled()`）——
  **联机靠它立即下发快照**，否则客户端 UI 停留在结算前状态（用户实测：JC083 输入数字点确定后界面不刷新）。
  单机虽由 View 侧回调刷新，仍应走同一收尾以保证"地区赢取/解原子锁/免费打出队列"不被跳过。

---

## 漏检复盘与判据加固（2026-10）

> 三批实测 BUG（A124/A125/A126）在"25 判据 + 20 个审计用例全绿"的前提下仍然漏出。
> 原因不是"没跑"，而是**判据的检查维度与缺陷所在维度不同**。本表为整理后的结论（去重版），
> 每条盲区都已落到**新判据或加固条款**，并有可复跑的检查。

| 盲区 | 实测实例 | 原判据为什么不过问 | 已加固判据 / 检查 |
|---|---|---|---|
| ① 只核对"卡面 ↔ 卡脚本"，不核对"引擎是否真消费了该声明" | BUG1 声望（`Renown()` 声明去重、`PlaceToken` 只认 `威名`）；JC083 数值范围 | ⑮/⑱ 只问"卡有没有声明"，不问"引擎有没有消费" | **㉖ 承诺必须落地** → `TestAudit_承诺落地清单` |
| ② 只问"目标类型对不对"，不问"限制规则的**适用范围**" | BUG2 JC081「除装备与祝福外不受附属」被套到**任何**打出目标 ⇒ 事务卡 JC078 被拦 | ⑤/⑯ 是"关键词 ↔ 字段"存在性检查，无"作用对象"维度 | **加固⑤/⑯** → 数据层自洽 + `TestContract_白名单_只约束附属且仅限结附路径` |
| ③ 只做结构一致性，不做端到端流程 | BUG3 JC082（两条 `TargetRules` 都在，但复检只取首条 ⇒ 结附秘社进墓地） | ⑥ 只覆盖"单/多目标丢失语义"，未覆盖**多类别声明** | **㉗ 多声明 OR 一致** → `TestContract_多声明OR_两类别各自可结算` |
| ④ 门禁写在"另一个阶段"，核查只看"卡有没有声明" | BUG4 JC083（收集期把"尚未取值"当"X 不可用"⇒ 整条触发不收集） | ⑬/⑱ 无"**求值阶段**"维度（收集期 vs 结算期） | **加固⑱** → 行为 `TestBug4_JC083准备步骤触发并弹数值窗` + 自动 `TestAudit_本批BUG回归防线` |
| ⑤ 单一真相源的**扩散面**没覆盖（只验主入口） | BUG5 屏障（`CheckRestrictions` 正确，但候选枚举旁路只按大类过滤 ⇒ 宿主仍被袭击选中） | ⑦ 只验"屏障有实现"，没验"**所有候选枚举点**都过同一判定" | **㉘ 旁路同判** → `TestContract_屏障_候选与合法性四路径一致` / `..._结算复检_先指定后获屏障` |
| ⑥ 同一能力只改一条落地路径；交互挂起漏收尾 | JC082 联机侧漏改三入口；JC073 屏障在 **PM `attach`** 路径不生效；JC083 联机数值窗回包不收尾 ⇒ 不下发快照 | 无判据覆盖"**路径完备性**"（生产/调试命令/单机/联机/重连）与"**交互收尾**" | **㉙ 落地路径与交互收尾** → `TestContract_落地路径一致_生产与PM命令`、`TestContract_交互收尾_数值窗单机与联机`、`TestContract_交互收尾_是窗与持锁`、`TestContract_联机孪生入口_PlayChoice`、`TestBug5c_PM命令attach后宿主获得屏障` |

### 本轮加固附带查出的**真实缺陷**（不是检查器口径问题）

1. **换局残留挂起链吞掉结算收尾**：`EffectEngine._pendingChain` 未随换局清空；残留的"无续算点"挂起链
   会让后续回调型挂起被静默吞掉、`ContinuePendingChain` 直接返回 ⇒ **联机不下发快照（UI 不刷新）**。
   已修：`ResetAll` 清 `_pendingChain/_pendingResolve/_resolveDepth/_playerChosenValue`；
   `SuspendChainForCallback` 识别并替换"无续算点"残留链（告警）；新增 `SettleAfterValuePrompt` 单点收尾
   （未由我方登记也补一次 `OnEngineChainSettled`）。
2. **结附登记多路径分叉**：生产结算、PM `attach`、`attacharea` 各写一份 ⇒ 任一路径漏登记即"授予宿主"失效。
   已修：`ZoneManager.AttachAndRegister` 单点化，三条路径共用。

### 通用防范（写进后续核查动作）

1. **否定型规则**（不能被…／不受…／不能…）与**阶段/时机相关**规则 ⇒ 必须落**行为级用例**，不能只做字段比对；
2. **注释/参数名承诺了行为**的地方 ⇒ 一律加"消费点断言 + 行为用例"（判据㉖）；
3. **同一判定有多个调用点** ⇒ 核查必须追问"**旁路**是否也过这一判定"（判据㉘）；
4. **"结构有、流程无"只能靠端到端用例发现** ⇒ 每张受影响卡至少一条"真的打出／真的触发／真的结附"；
5. **任何改动都要问"另一条落地路径改了吗"**（单机/联机/PM 命令/快照重建）（判据㉙）。
6. **让"派生结果"的输入变坏的路径** ⇒ 尾部必须**重算派生结果**（判据㉚：减防/减血 ⇒ 复检死亡；改控制权/翻面/换区 ⇒ 复检目标合法性）。

---

### 判据㉚ 数值下降 ⇒ **必须复检派生状态**

- **判据描述**：任何让"派生结果"的输入变坏的路径，都必须在**同一路径尾部**复检派生结果，尤其是**死亡判定**
  （`伤害＋创伤 ≥ 当前防御力`）。实测（2026-10 用户口径·JC089 毒血诅咒"受此结附的角色获得 -1 防御力"）：
  持续授予/光环撤销/时效到期/形态还原等**所有减防路径**都必须在尾部补一次死亡判定。
- **口径（"剩余生命"语义·2026-10 用户裁定）**：**"获得 -N 防御力"是上限削减，不等于受到伤害**——
  削减时**优先吸收已受伤害**（`dmg -= min(N, dmg)`，再 `max -= N`），剩余生命＝当前上限 − 已受伤害：
  · 2 防御受 1 伤 → −1 上限 ⇒ 伤害被吸收为 0、上限 1 ⇒ **剩余 1 ⇒ 不死**（实测原判为死亡）；
  · 1 防御未受伤 → −1 上限 ⇒ 上限 0 ⇒ **死亡**；
  · 3 防御受 2 伤 → −1 上限 ⇒ 伤害 1、上限 2 ⇒ 剩余 1 ⇒ 不死（同款）；
  · 上限被削减后**生存力照常下降**（上述 1 上限的角色再吃 1 点伤害即死）。
  **边界（必须区分）**："**撤销正向授予**"（光环消失等，`CardInstance.Revoke`）**不吸收伤害** ⇒
  JC059 判例（3 防御受 2 伤、+1 光环撤销回 2 ⇒ **死亡**）保持不变 → 见 `P3AuraFilterTests.TestAuraFilter_减防导致死亡`。
- **死亡判定（单一实现）**：`CardManager.RecheckDeathAfterDefenseDrop`：
  · 只判**在场卡**（争夺区/附属区）且**非暗藏者**；
  · `CurrentDefense < 0`（-1 哨兵＝"当前无防御"，如附属/形态过渡态）⇒ **不判死**（防误杀）；
  · `CurrentDefense >= 0` 且 `IsDead()`（伤害＋创伤 ≥ 当前防御）⇒ 走既有死亡流程
    ⇒ **防御降到 0 时即使毫发无伤也死亡**。
- **字段/落点**：吸收实现＝`CardInstance.Grant("Attribute","防御",负数)` 分支（单一实现，`Gain{"防御",-N}` 与持续授予共用）；
  `CardManager.RecheckDeathAfterDefenseDrop`；收口扫描＝`EffectEngine.RecheckDeathsAfterDefenseDrop()`；
  调用点＝`ReevaluateAuras` 尾部（光环撤销）、`RegisterPersistent` 尾部（持续授予可能含**负值防御**）、其余减防路径。
- **检查方法**：自动（㉖ 承诺『上限削减吸收已受伤害（单一实现）』＋『减防后补死亡判定（含持续授予路径）』≥2 处消费点）
  ＋行为（`TestB2_4A_JC089减防致死_防御1未受伤`、`TestB2_4B_JC089减防吸收已受伤害_不误杀`、
  `TestB2_4C_JC089减防不误杀_防御3未受伤`、`TestAuraFilter_减防导致死亡`（JC059 边界））。

---

### 附二补遗三 · 2026-10 判据加固与复查

- **新增判据**：㉖ 承诺必须落地、㉗ 多声明 OR 一致、㉘ 单一真相源的旁路同判、㉙ 落地路径完备性与交互收尾。
- **加固既有**：⑤/⑯（限制条件适用范围）、⑦/⑧（非支付型"你可以…"＝入堆叠前是/否窗）、⑮（声望＝地区级）、
  ⑱（求值阶段 + 收集期只评估 `Condition.Predicate` ⇒ 复合门禁须合成单谓词）、⑳（声明式分工须登记豁免）。
- **新增检查**：
  · 自动层 `TestAudit_承诺落地清单`（14 条承诺的消费点源码断言 + 行为用例反射校验 + 数据层自洽：
    附属白名单自洽、光环范围字段 ↔ 卡文词）；
  · 自检层 `TestAudit_自检_判据覆盖矩阵`（①–㉚ 全覆盖、检查方法必须真实存在、文档总览不得缺号）；
  · 行为层 `JCEnginePathContractTests`（判定 × 路径矩阵 / 多声明 OR / 落地路径一致 / 交互收尾与持锁 / 联机孪生入口）。
- **测试基建**：`JCTestStubs.cs`（触发视图桩、UI 桩、网关桩、对抗窗口桩集中一处，避免各套件口径漂移）。

---

### 附二补遗四 · 2026-10 放置去重窗口统一（威名／声望）

**背景**：`声望` 的放置去重是**借用**`威名`的表，而不是与它同形的一等机制。核查后确认 4 处不一致，
其中 2 处是隐患、2 处是文档/检查问题：

| # | 不一致 | 性质 |
|---|---|---|
| ① | 去重表与键**都不含标签**（表名 `_combatWonMarkPlaced`、方法 `ResetCombatWonDedup`、键 `地区\|控制者`）⇒ 跨标签**互相吞标记/互相破防** | 隐患（时序上被"两次广播之间堆叠必空"掩盖，直连 API/测试一次即现形） |
| ② | 启用条件是**手改白名单** `dedupTag == "威名" \|\| dedupTag == "声望"` ⇒ 新增同类词条必须记得改引擎（BUG1 根因） | 隐患（同类必再犯） |
| ③ | `RenownRule.cs`／`Renown()`／`PowerCombatEnded` 注释写"所有对抗结束后／每回合只触发一次／自带幂等守卫"，与代码（每次广播一次、无守卫）与测试相悖 ⇒ ⑮ 只能"以威名为准、声望存疑、只登记" | 文档口径冲突 |
| ④ | 威名侧**无数据层断言、无行为用例**；㉖ 的源码针只钉了 `dedupTag == "声望"` 一半 ⇒ 删掉威名那半无测试变红 | 检查不对称（不可反向验证） |

**统一后的机制（单一真相源）**：
- 标签常量 `Script/Data/Effects/PlacementDedupTags.cs`（`WeiMing` / `Renown`）；关键词模板与广播点**共用同一常量**；
- `EffectEngine`：`_placementDedupByTag`（**桶＝标签**，键＝`地区|控制者`）+ `BeginPlacementDedupWindow(标签)`
  （**只清本标签的桶**）；`PlaceToken` **非空标签即启用**（删掉白名单）；
- 广播点：`OnCombatWon` → `BeginPlacementDedupWindow(PlacementDedupTags.WeiMing)`；
  `PowerCombatEnded` → `(...Renown)`——两处**同形**；
- 不变式：**两次广播之间堆叠必已清空**（`TurnManager.Pass`：堆叠非空只结算栈顶、不推进阶段）。
  若将来新增"堆叠非空时也能广播"的触点，必须先改成"收集触发时打窗口戳"再广播。
- 口径（写进判据⑮）：威名＝**每次战斗对抗获胜**广播一次；声望＝**每次势力对抗结算完毕**广播一次
  （JC067 追加的那次同样触发）；每个窗口内**每控制者每地区只放 1 枚**，两词条互不干扰。

**检查（两侧对称）**：
- 自动：`TestAudit_JC次数限制_与卡文一致`＝时机↔标签成对（`OnCombatWon`⇒`威名`、`OnPowerCombatEnded`⇒`声望`）+ 两侧计数防空转；
  `TestAudit_承诺落地清单`＝两处窗口开启、按标签分桶、两条标签常量（针钉在 `Args` 字面量上，注释不能糊过去）；
- 行为：`TestContract_去重窗口_威名_多张一次战斗只放一枚`（同窗口 2 张威名只 1 枚；第二次战斗＝新窗口再 1 枚）、
  `TestContract_去重窗口_威名与声望同窗不互吞`（同一窗口交错：先结算 1 张威名 → 声望广播 → 全部结算 ⇒ 恰好 2 枚）、
  `TestBug1_多个声望角色一次势力对抗只放一个标志`、`P3KeywordsTests.TestP3_声望_每次势力对抗触发_同一次只加一个`；
- 反向验证：①清全表 ⇒ 同窗用例红；②删 `OnCombatWon` 的窗口 ⇒ 威名用例 + 承诺红；③`Renown()` 标签写成 `声_望` ⇒ ⑮红（6 条）+ 承诺红；还原⇒0 失败。

---

### 附二补遗五 · 2026-10 实测反馈四批（联机优先，单机同办）

- **⑮ 未横置才有词条（威名／声望）**：横置的角色不提供其词条 ⇒ 来源卡横置时**不收集**该类触发
  （`EffectData.RequireSourceUntapped`；**卡面固有**与**运行期授予关键词模板**两条收集路径**各一道**；
  `CloneKeywordTemplate` 必须逐项拷贝门禁字段）。声望"哪一方触发"的计数**不含**横置者
  （`RenownRule.CountRenownCharacters`）。卡文依据：声望"更多**未横置**的声望角色"＋项目既有口径
  "横置的卡不提供任何能力"（`AreaStatsCalculator`）。
- **㉚ 上限削减 ⇒ 吸收已受伤害 ＋ 复检死亡（JC089，二轮修正）**：『获得 -N 防御力』是**上限削减**、不是伤害：
  削减时**优先吸收已受伤害**（剩余生命＝当前上限 − 已受伤害）⇒『2 防御受 1 伤 + −1 上限』**不死亡**（剩余 1）、
  『1 防御未受伤 + −1 上限』**死亡**（上限 0）；削减后生存力照常下降（再受 1 点即死）。
  所有减防路径尾部补 `RecheckDeathsAfterDefenseDrop`；`-1` 哨兵（当前无防御）不判死。
  **边界**：撤销**正向**授予（光环消失）**不吸收**伤害 ⇒ JC059 判例（3 防御受 2 伤 + 光环撤销 ⇒ 死亡）保持。
- **⑧ 选择窗文案按卡文/数据给（JC092）**：选地区/选卡窗的提示必须来自 `TargetSlot.Hint`／`EffectData.Hint`
  （联机经 `ChoiceRequest.SlotHint` 下发），UI **不得硬编码**另一类动作的说法——
  实测 JC092 死灵召唤被显示成移动类效果的"（将移动至该地区）"；同款数据化覆盖 JC133（正面进场）与
  JC021／机动（真正的移动语义）。
- **⑨ 次数用尽 ⇒ 不问（JC093）**：激活能力本回合/本局次数**全部用尽**时双击**不开面板、无反馈**；
  部分用尽 ⇒ 该项置灰并标注"（本回合次数已用尽）"。单一判定＝`ActivateEligibility.IsAbilityUsesExhausted` /
  `AllAbilitiesUsesExhausted`（单机 `Arena` 与联机 `ArenaOl` 共用）。
- **㉙ 墓地行动的可行动入口（JC085，二轮修正）**：`PlayerCardData.PlayableFromGraveyard` 的卡在墓地中**要有可行动高亮**：
  门禁＝单一实现 `TurnManager.CanPlayFromGraveyardNow`（在墓地 ∧ 声明该标志 ∧ 有行动权 ∧ 行动步骤），
  单机 `Arena` 与联机 `ArenaOl` 的高亮各接一处（联机另要求服务端投影把它列为可打出源）；
  **双击 → 先弹『技能/激活选择窗』（只有一条也弹）→ 再弹『选择地区窗』→ 正面打出**（单机 `Arena.OnGraveyardPlayRequested`
  → `OpenGraveyardAreaChoice`；联机 `ArenaOl.OnGraveyardPlayRequested` → `OpenOnlineGraveyardAreaChoice` 后上行
  `PlayCard{targetAreaUid}`，服务端权威校验费用/忠诚/阶段）。信号＝`CardUI.GraveyardPlayRequested`
  （订阅收口在 `Arena.Layout` 的『区域同步唯一收口』与 `ArenaOl.RegisterOnlineCardUI`）。

### 附二补遗六 · 2026-10 实测反馈五批（单机复现；联机同源缺陷一并修）

- **㉘ 地区级"略过对抗"＝该步骤在流程里直接不存在（JC098/JC118，口径修正）**：卡文"本地区略过调查对抗**步骤**"
  ⇒ 不是"进入该步骤但不结算"，而是**流程里没有这一步**：不进入该阶段、不分配行动权、不开让过窗口、不开对抗窗口。
  单一消费点＝`TurnManager.NextCombatStepOfArea(fromStep)`（从给定序号起取"当前地区第一个未被跳过的对抗步骤"；
  该地区全被跳过 ⇒ 推进地区游标；所有地区都无可结算步骤 ⇒ 结束步骤），`AdvancePhase` 在**机动轮后 / 地区内推进 /
  换地区后**三处接入。`ResolveCurrentAreaCombat` 的旧门禁**保留为旁路兜底**（PM `jumpto` / 直接 `EnterPhase`
  进到被跳过的步骤时仍不结算对抗）。判定仍按卡**当前**所在地实时匹配地区（`EffectEngine.AreaSkipsCombat`）。
  同一条消费点让 JC118（`ActionVerb.SkipAreaCombat`，可只指定"战斗"等中间步骤）获得相同语义。
- **㉚ 减防的**结算动作**路径也要复检死亡（JC100 BUG2）**：㉚ 原文"数值下降 ⇒ 必须复检派生状态"覆盖**所有**减防路径——
  除持续授予（`RegisterPersistent` 尾）与光环撤销（`ReevaluateAuras` 尾）外，**动作型** `Gain{防御,-N}`
  （`EffectEngine.ApplyGain` 的"防御"分支，`amt < 0`）同样必须调用 `RecheckDeathsAfterDefenseDrop()`；
  否则"按规则已死"的角色留在场上、**UI 不刷新**（单机 UI 由 `ZoneManager.OnCardMoved` → `SyncCardUIToZone` 驱动）。
- **⑦ 询问时机：可选固定费用＝发动时支付（JC100/JC105，同 JC061）**：见 ⑦/⑧/⑨ 新增条目。
- **⑤/⑤-命中范围：高亮范围必须与"可点/可落点"范围同源（JC107）**：选目标的高亮走 `_cardUIMap`（在册 UI），
  而点击命中原先只扫"争夺区角色 + 附属区附属" ⇒ **资产卡高亮却点不动**。命中范围＝
  单机 `Arena.AssetCardUIs`（按实例所在区 `AssetZone` 从在册 UI 筛出）接入 `CardDragController.PickAreaCardAt`
  与拖拽落点高亮/清理；联机 `ArenaOl.PickFieldCardAt` 的区过滤补 `AssetZone`（拖拽高亮同补）。
  通用纪律：**凡"能高亮"的目标都必须"能点中"**（两处共用同一份集合）。
- **⑤/㉕ 自身目标的"能不能点"（JC108）**：`Target.CanTargetSelf=true` 只解决**候选/门禁**；交互侧还必须让"自身"**可被选中**：
  单机/联机激活拖拽的落点判定不得**无条件**拒绝"落点＝来源卡自身"，判据＝"**确实拖动过**（鼠标离开过来源卡）
  ∧ 自身是合法目标"（原地单击仍为取消）；联机另需 `LegalityProjection`（服务端投影）与
  `ArenaOl.ComputeLegalCardTargetUids`（客户端兜底）**不再无条件跳过来源卡**（原写法与
  `ActivateEligibility.IsLegalActivateTarget` 的 `CanTargetSelf` 放行分叉）。
  注：JC108 的被封印者是**牌库顶的牌**（不是本卡）⇒ 不适用 JC066/JC100/JC110"不能把自己封印在自己身上"。
  **A132 追加**：若"落点＝自身"在拖拽手势里仍难以表达（用户复测"依然无法以自己为目标"），
  改为**双击激活 → 选择目标**：`EffectData.ActivateByDoubleClick=true` ⇒ `CanDragActivate` 返回 false
  （左键不再被激活拖拽吞掉）⇒ `CardUI` 双击 → `OpenAttachmentActivateChoice`（单机）/`SendActivation`
  （联机）→ 目标选择窗（含自身）。（默认 false，既有"1 条能力 + 需选目标 ⇒ 拖拽"手势不变。）

### 附二补遗七 · 2026-10 实测反馈六批

- **⑦ 询问时机（费用修正的消费）**：卡文"你**下一张**X 牌费用减少 N（一次性）"的修正是被**下一张符合条件
  的牌**消费掉的——判据＝"本张**确实用到了时效减免**"（`costModApplied > 0`），**与"是否真的扣了费"无关**：
  减免可能把费用降到 0（1 费牌被 −1）⇒ 旧的"写在 `totalCost > 0` 分支里"会**不消费**，表现为
  ① 修正残留、下一张继续被减；② `CostModChanged` 不广播 ⇒ **手牌减费提示不刷新**。
  两个付费通道（`PlayCard` 的"打出" / `RevealCovert` 的"现身"）各自按同一判据消费；免费打出不受影响。
- **⑰ 光环的受影响集合必须跟随"运行期授予的关键字"（JC105/JC106）**：光环按关键字筛选目标
  （`AuraFilterKeyword`，判定读"运行期授予 ∪ 卡面固有"）时，**该关键字可能来自另一个效果的运行期授予**
  ⇒ 关键字授予/撤销处必须**立刻重算光环**（`EffectEngine.ReevaluateAurasAfterKeywordChange`，
  与"卡移动/回合开始"同一份 `ReevaluateAuras`），否则新晋关键字角色拿不到光环授予
  （实测："JC105 没有让 JC106 授予灵体的卡白星"）。
- **④ 印刷数值也是卡数据的核查项（JC111）**：`Defense = -1` 是"**无防御**"哨兵（`HasDefense=false` ⇒
  伤害被静默忽略、不参与判死），与"防御 1"是不同的规则状态；`.tres` 的印刷数值必须与卡面一致
  （实测：JC111 应为防御 1，原数据写成 -1 ⇒ 该卡整卡免伤）。
- **⑨ 输入链路：全局"拖拽起手"不得吞掉双击（JC110/JC108）**：`Arena._Input` 的全局拖拽起手
  （`TryBeginActivateDragAt` / `TryBeginDragAt`）会 `AcceptEvent()` ⇒ `CardUI.OnGuiInput` 收不到鼠标事件、
  **双击激活分支永不触发**。两条纪律：① 声明"双击激活"的激活能力不进拖拽（`ActivateByDoubleClick`）；
  ② 手牌行动卡的双击必须在全局输入里先判（`DoubleClick` 标记 + 落点），再决定是否起手拖拽。
- **⑧/显示：同一份 UI 机制被复用时，文案必须按动作给（JC104）**：窗口**标题栏**、标题、区域标题、提示行
  都要随动作语义走（`AllocationWindowTexts`：调查对抗 / 预测X 两套，单机与联机共用），
  不得"复用了别处的窗口，就显示别处的文案"。（复用**机制**是好事，复用**文案**是缺陷。）
