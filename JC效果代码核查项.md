# JC 效果代码核查项（唯一口径来源）

> **本文件是 JC 扩展包效果代码自查的唯一口径来源。**
> 核查对象：`Cards/Effects/PlayerCards/JC/*.cs`（134 个效果脚本 + `JCEffects.cs` 注册入口）
> 与 `Cards/Data/PlayerCards/JC/*.tres`（135 个卡数据）。
>
> **纪律**：本轮核查**只读、只记录，不修改任何卡效果代码 / tres / 引擎代码**。
> 发现问题一律写入台账并标注【待裁定】，由用户裁定后另行进入修复阶段。
>
> **记录口径**：台账与问题清单**只写现状**（当前实现是什么、与卡文是否一致），
> **不列"曾经写错过什么"**（历史缺陷只在判据文档的"历史缺陷"栏作为判据由来参考）。
>
> 判据分两段：**A 段（①–⑨）＝用户给定的主干判据**；**B 段（⑩–㉔）＝结合本项目引擎实际字段与历史缺陷补齐的判据**，
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
| 次数·地区级一次（威名/声望） | ✅ 前缀区"威名/声望" | ✅ `KeywordEffects.WeiMing()`/`Renown()` + 引擎地区级去重 | 两侧比对（⑮）：**以威名为准**，声望去重口径存疑另记 |
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
- **次数限制·地区级一次**：一回合一次查 `MaxUsesPerTurn`；**威名/声望按"该地区每回合只触发一次"核对**，
  **以威名为准**；声望现存去重口径冲突，**只登记【待裁定】，不改引擎**（见判据⑮）。
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
- **历史缺陷**：JC121 曾漏装"公开"。

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
- **历史缺陷**：JC048 曾把"准备步骤强制触发"错落到拆分的多条同时机效果；JC124 的"行动3"曾漏声明窗口（见⑮）。

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
- **历史缺陷**：JC021 狭路相逢（三槽：地区 + 本方角色 + 敌方角色，`MinChosenSlots=1`）；JC055 混合 `Kind`+`KindAlt` 曾永远取不到角色一半。

### 判据⑥ 丢失目标是否导致效果无法结算
- **检查对象**：`TurnManager.IsTargetLost`、`TargetSpec.VersionSkipsSelf`、`TargetSlots` + `MinChosenSlots`。
- **判据描述**：
  - **单目标**：目标丢失 ⇒ **整条效果不结算**（fizzle）；
  - **仅 K15 多目标**：允许"部分目标失效后其余照常结算"（`MinChosenSlots` 自洽）。
- **字段/落点**：
  - 唯一判定点 `TurnManager.IsTargetLost`（版本号比对）；
  - 多目标部分结算：`TargetSlots` + `MinChosenSlots`，见 `Script/Tests/P3MultiTargetTests.cs:573-576`（JC021/D16 口径）；
  - `TargetSpec.VersionSkipsSelf`（`EffectData.cs:734`）：JC052"其他"版本敏感写法（翻面后"现存这张自己"也会被老触发消灭）。
- **检查方法**：自动化（单目标效果不得依赖部分结算；多目标效果必须声明 `MinChosenSlots`）+ 人工（卡文是否写"尽可能"）。
- **误判/豁免**：卡文明确写"对每个…都…"（群体）时不适用本判据，走判据⑯。

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

### 判据⑧ 询问窗口是否"合适"
- **检查对象**：`EffectAction.Verb` 与询问窗口的匹配。
- **判据描述**：必须用**语义匹配**的窗口——
  - **是/否**（"你可以支付N"固定金额）⇒ 走 `OptionalPayCost`(`EffectData.cs:1101`) → `PromptOptionalPay` 二选一窗；
    **不得**退回 `PromptNumberInput` 数值输入框；
  - **固定数量**（"弃一张""移除一个标志"）⇒ 不得弹"输入数量"；
  - **多选项文本**（"选择一项：·… ·…"）⇒ 走 `PromptEffectMode`（列表二选一），**不得**弹数值框；
  - **可变数量 X**（"移除X个"）⇒ 才允许数值/分配窗（`AmountByPlayerChoice`）。
- **检查方法**：自动化（`PayOptional` 值域 `min!=max && max>1` ⇒ 违规，参考既有用例）+ 人工（选项型是否误用数值框）。
- **历史缺陷**：JC031"是否要求随机弃牌"曾弹**输入框**，后改走 `PromptEffectMode` 列表二选一（A85）；JC044 计数器代价"固定模式"曾因误下发 `OptionMaxAmounts` 导致窗口样式不一致且无法发动（A91②）。

### 判据⑨ 不满足条件时按钮必须置灰
- **检查对象**：`ActivateEligibility`（候选侧）、`EffectEngine.ActivateAbility`（权威侧）、`TurnManager.GetModeAvailability`、`LegalityProjection`。
- **判据描述**：玩家不满足效果使用条件时，UI 按钮**提前置灰**，而不是"点了没反应"；
  候选侧与权威侧必须用**同一套判据**（避免"亮着但发不动"或"能发但没亮"）。
- **字段/落点**：
  - 候选侧 `Script/Manager/ActivateEligibility.cs`（`:22` `GetEligibleAbilities`，`:195` 生效区门禁）；含优先权 / 阶段 `PhaseGateRule` / 次数 / 动态值 / 生效区 / 合法目标 / 前置条件（`ConditionAllowsActivation`）；
  - 权威侧 `EffectEngine.ActivateAbility`；
  - 模式可用性 `TurnManager.GetModeAvailability`（View 只展示不重算）；
  - 联机投影 `LegalityProjection`（只投影当前确实可发动的能力）。
- **检查方法**：自动化（激活能力若有前置条件/代价/目标，须能被 `ActivateEligibility` 判定）+ 人工（找"UI 与服务端两套判据"的隐患）。
- **历史缺陷**：JC053 前置不满足曾**不置灰**（A98）⇒ `ConditionAllowsActivation` 提升为 public 后 UI 与服务端共用；A103 纯客户端无 `EffectEngine.Instance` 时置灰求值**崩溃**；A104 投影改为只投影可发动能力。

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
- **历史缺陷**：JC070 虔心修士曾**整卡无脚本**（前缀"公开/声望"全漏装）。

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
- **历史缺陷**：JC034"结附于本方秘社"的 `TargetRules` 曾完全没写 ⇒ 该卡**永远打不出**（`TargetRules.Count=0`、`IsLegalPlayTarget` 全 false）。

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
- **历史缺陷**：JC049 额外费用必须"无合法牺牲对象 ⇒ 整张不能打出"（A21）；JC055 可变标志代价 X≥1 且入堆叠前支付。

### 判据⑮ 次数与阶段门禁
- **判据描述**：`MaxUsesPerTurn` / `MaxUsesPerGame` / `OnlyPhases` / `ExcludedPhases` / `ActivateWindow` / `PlayTiming` / `LimitToActionPhaseOnly` 与卡文表述一致。
- **次数限制（本判据核心补充）**：逐卡核对卡文次数措辞与实现：
  - 卡文含"每回合一次 / 每回合N次" ⇒ 对应 `MaxUsesPerTurn=N`；
  - 卡文含"每局一次 / 整局N次" ⇒ 对应 `MaxUsesPerGame`；
  - **地区级一次**：卡文含 `威名` / `声望` ⇒ 该关键词按**"该地区每回合只触发一次"**核对。
    - **以威名为准**：`KeywordEffects.WeiMing()`（`Cards/Effects/PlayerCards/KeywordEffects.cs:202`）＝ `OnCombatWon` + 谓词 `WinnerIsSelfInSameArea` + 放势力标志，**单次战斗对抗内每控制者每地区只放一个**（`EffectEngine` 去重）；
    - **声望存疑**：`KeywordEffects.Renown()`（`:350`）＝ `OnPowerCombatEnded` + 谓词 `RenownMajority` + 放势力标志；
      引擎注释（`RenownRule.cs`）称"同一地区每回合只触发一次"，但既有测试 `P3KeywordsTests.TestP3_声望_每次势力对抗触发_同一次只加一个` 断言"JC067 追加的第二次势力对抗会**再触发一次**" ⇒ **去重口径冲突**。
      **本轮只登记现状与冲突，标注【待裁定】，不改引擎**（用户口径"以威名为准"）。
- **字段/落点**：
  - `MaxUsesPerTurn`（`:968`，计数基准＝每卡·每回合·每能力，按 `EffectId` 分别计数）、`MaxUsesPerGame`（`:981`）；
  - 威名/声望：`KeywordEffects.WeiMing()`（`KeywordEffects.cs:202`）/ `Renown()`（`:350`）、`RenownRule.cs`（地区级去重）；
  - `OnlyPhases`/`ExcludedPhases`（见⑬）；`ActivateWindow`（`:936`，"行动"/"快速"）；
  - `PlayTiming`（`:163`）：`MainPhase`/`Response`/`NonResponsive`；`LimitToActionPhaseOnly`；`ActivateCondition`（`:957`）。
- **检查方法**：自动化（激活能力必须声明 `ActivateWindow`；"行动："前缀不得为 C 档；"只能在行动阶段打出"须 `LimitToActionPhaseOnly`；**卡文次数词 ⇒ `MaxUsesPerTurn`/`MaxUsesPerGame` 映射；卡文含威名/声望 ⇒ 按地区级一次核对**）。
- **历史缺陷**：JC117"该能力只能在行动阶段发动"⇒ `OnlyPhases="行动阶段"`；JC124"行动3"曾漏 `ActivateWindow`；JC031"每回合只能发动一次"曾在**结算前**才检查 ⇒ 第二次死亡仍入堆叠（A76，后改为收集阶段拦下）；JC063/JC074"快速行动"曾误限行动阶段。

### 判据⑯ 目标筛选正确性（**只查"限制条件"；目标类型归⑤**）
- **判据描述**：`IsLocalZone`（本地区）/ `AllAreas`（所有地区）/ `IncludeCovert`（含暗藏者）/ `Filter` / `Restrictions` / `MassMode` / `MinCounters` / `ExcludeInChosenArea` 与卡文用词严格一致。
- **字段/落点**：`TargetSpec` 的 `IsLocalZone`、`AllAreas`、`IncludeCovert`、`Filter`、`Restrictions`、`MassMode`、`ExcludeInChosenArea`（多槽）。
- **检查方法（自动化，非人工）**：卡文限制词 ⇒ 期望字段的映射比对（"本地区"→`IsLocalZone`、"所有地区/任一地区"→`AllAreas`、
  "含暗藏者"→`IncludeCovert`、"所有角色/每个角色"→`MassMode.AllInArea`、"本方/敌方/对手"→`Restrictions`）；
  `Filter` 的检索串（如 `子类别:吸血鬼`）必须命中引擎白名单（⑬），且**子类别字符串必须能在 `tres.SubCategories` 找到**（`PlayerCardData.cs:209/215`）。
- **历史缺陷**：
  - `CanTargetSelf` 默认 **false** ⇒ 漏声明会让"本地区只有它自己"时**候选为空、触发静默跳过**（JC004 实测）；
  - JC017 卡文无"本地区"限定却漏加 `AllAreas=true`（原只能指定本地区角色）；
  - JC111/JC031/JC057"目标玩家"（＝任意玩家含自己）曾被收窄为 `Enemy`（2026-09 裁定修正）；
  - JC055 混合 `Kind`+`KindAlt` 曾因 `TargetZonePool.IsZonePileSpec` 判定导致"角色"一半永远取不到。

### 判据⑰ 光环与持续授予的**落点**
- **判据描述**：持续/光环授予的**作用对象**必须与卡文一致——
  - `GrantToHost`（授予**宿主**）vs 授予**自身**；
  - `AuraLocalZoneOnly`（**本地区**）vs 全部争夺区：**卡文写"所有"且未写"本地区" ⇒ 全场**（`AuraLocalZoneOnly=false`）；写"本地区"才限本地区；
  - `AuraFilterKeyword` / `AuraFilterOrganization` / `AuraFilterExcludeDomain` / `AuraRequiresNoDomain`（筛选范围）；
  - "所有**其他本方**角色"必须**排除自身**且限定本方。
- **字段/落点**：`GrantToHost`（`:1235`）、`AuraFilterKeyword`（`:1216`）、`AuraLocalZoneOnly`（`:1223`）、`IsAura`、`AuraRequiresNoDomain`、`AuraFilterExcludeDomain`、`ContinuousCostMod`（`:1243`）、`GrantedAbility`（`:1183`）。
- **检查方法**：人工为主 + 自动化（字段存在性/互斥性）。
- **历史缺陷**：
  - JC059 类"本地区"vs"全场"光环范围误写；
  - `AuraRequiresNoDomain` / `AuraFilterExcludeDomain` 曾因枚举默认值 `Mind`（=0）导致**每张光环卡都静默排除了心灵领域角色**（`EffectData.cs:1024-1029` 注释记录）；
  - JC051"所有其他本方鸣钟教派角色"须排除自身与非本组织者；
  - JC025/JC012 等徽记减费须限定 `Filter` 与一次性语义。

### 判据⑱ 动态数值 X
- **判据描述**：`ValueExpr` 必须合法（命中⑬白名单）；依赖的"玩家选择"必须有数值输入/分配 UI；
  `ValueAtResolve` 该开未开会导致**收集阶段 X=0 被整条跳过**。
- **字段/落点**：`ValueExpr`（`:989`）、`ValueAtResolve`（`:1001`）。
- **检查方法**：自动化（`ValueExpr` 非空 ⇒ 命中白名单）+ 人工（"X 由玩家选择"须有分配窗；自身会改变该计数 ⇒ 必须 `ValueAtResolve=true`）。
- **历史缺陷**：JC048 诅咒沙漏——X 在收集阶段求值，而"放标志"尚未发生 ⇒ 首个准备步骤 X=0 被 K1 门禁整条跳过，实测"只有牺牲那回合才造成伤害"（A90②，后置 `ValueAtResolve=true`）；JC040 遏制 X=事件数量，X=0 时不发动；JC055 `ValueExpr="代价数量"`。

### 判据⑲ 期限与撤销（**两面：⑲-a 期限可两侧比对；⑲-b 撤销属引擎运行期**）
- **⑲-a 期限（卡面 ↔ 实现）**：`Duration`（常驻 / 瞬时 / 直到回合结束）**必须与卡文的"直到回合结束/常驻/本回合"等措辞一致**。
  字段：`Duration`（`:1271`）。卡文期限词 ⇒ 期望 `Duration` 的映射比对（自动化）。
- **⑲-b 撤销（引擎运行期，tres 不体现）**：
  所有"授予/抑制/改写"类效果离场时必须被**统一重算撤销**（`GrantedAbility`/`IconSuppression`/`DomainSuppression`/`TextBlank`/`FormChange`/`ContinuousGrants` 的还原路径）。
- **字段/落点**：`Duration`（`:1271`）、`GrantedAbility`（`:1183`，`ReapplyGrantedAbilities`）、
  `IconSuppression`（`:1154`）、`DomainSuppression`（`:1161`）、`TextBlank`（`:1170`）、`FormChange`（`:1147`）、`ContinuousGrants`。
- **检查方法**：⑲-a 自动化（卡文期限词 ↔ `Duration`）；⑲-b 针对性核对实现侧还原路径（自动化可做"直到回合结束"须有还原路径的弱断言）。
- **历史缺陷**：
  - 离场清空运行期授予 ⇒ 以"运行期授予子类别"为条件的**死亡触发**恒不成立（JC030 血族长老：JC030 可触发 JC031 让对手随机弃牌，曾失效 ⇒ 改用死亡瞬间子类别快照）；
  - JC028 形态撤销须迁回附属区并还原类别；
  - 持续授予的自愈补授只挂在"卡牌移动"钩子 ⇒ **回合切换无移动时不补授**（JC051 A97，后订阅 `OnTurnStarted`）；
  - JC036 持续夺控（`ControlGain = AttachedHost`）注销时须归还拥有者。

### 判据⑳ 结构卫生
- **判据描述**：`EffectId` 唯一且符合命名规范；无空壳效果；无"声明了 `Target` 但动作未消费"；无同卡重复效果。
- **字段/落点**：`EffectId`（`:933`，计数基准，见⑮）、`Actions`（`:1270`）、`Modes`（`:1280`）。
- **检查方法**：自动化（同卡 `EffectId` 去重；`Actions` 与 `Modes` 皆空且非纯持续/静止 ⇒ 空壳告警）。
- **命名规范**：`<卡号>_<字母>_<语义>`，如 `JC048_1_PreparePlaceDamageSacrifice`、`JC022_S_Spirit`。

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
- **待裁定**：JC125 是否需要在某处（构筑校验层）落地，或长期保持"暂不做"。

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
- **落点**：台账"待裁定清单"的 **❓ 类目**；每条须写明**卡文原文 + 两种以上读法 + 各自影响的实装差异**，便于用户一次裁定。
  （**不列"曾经怎么写的"**，只写"现状哪一种读法 + 需要确认什么"。）
- **检查方法**：人工为主。核查员遇上述情形即**登记而非实现**；
  自动化辅助：把"卡文短语 ↔ `Timing` 无唯一映射"的卡自动列成候选（本轮以人工清单为准）。
- **纪律**：**判据㉔ 优先于②**——先确认卡文要什么，再检查实现；卡文不清则一律回问。

---

## 判据 ↔ 检查方式 总览

| # | 判据 | 主要检查方式 |
|---|---|---|
| ① | 效果主体（**已并入 ⑫**） | 自动化（`CategoryFlag` × `Timing`/`Scope` 交叉表） |
| ② | 效果类型：行动/持续/触发（**含类别误用**） | 自动化（前缀 ↔ `Timing`；前缀类别 ↔ `Timing` 组别） |
| ③ | 场上区＝争夺区∪附属区 | 自动化（`Scope` 枚举） |
| ④ | 默认仅场上正面朝上 | 自动化（`TriggerCovertMode`/`Scope`） |
| **⑤** | **目标：类型 + 语义限定词（其他/敌方/我方/本地区/全部）+ 多目标 + 自身约束** | **自动化（槽位自洽 + 目标词↔`TargetKind` + 限定词↔约束字段 + 默认允许自身）** |
| ⑥ | 目标丢失结算（tres 不体现 ⇒ 跳过两侧统一） | 自动化（单/多目标分档） |
| ⑦ | 需要询问处有询问（tres 不体现） | 自动化辅助（动词↔窗口路径） |
| ⑧ | 询问窗口合适（tres 不体现） | 自动化 + 裁定 |
| ⑨ | 不满足条件置灰（tres 不体现） | 自动化（候选侧↔权威侧同判据） |
| ⑩ | 注册完整性 | 自动化（数量比对 + 每个 cs 被调用） |
| ⑪ | 卡文 ↔ 实现一致（条数/顺序/语义/**卡名自指**） | 自动化（㉑切段 + 动作动词映射 + 卡名自指）+ 人工/裁定 |
| ⑫ | 主体类别 × Timing 自洽（**含 ①**） | 自动化（交叉表） |
| ⑬ | 未登记字面量静默失败 | 自动化（白名单） |
| ⑭ | 代价与发动条件 | 自动化（卡文代价词 ↔ 代价字段） |
| ⑮ | 次数与阶段门禁（**含威名/声望地区级一次**） | 自动化（卡文次数/阶段词 ↔ 字段；威名/声望 ↔ 地区级一次） |
| ⑯ | 目标**限制条件**（类型已归⑤） | 自动化（本地区/含暗藏者/群体/Filter） |
| ⑰ | 光环/持续授予落点 | 自动化（卡文范围词 ↔ 光环字段）+ 裁定 |
| ⑱ | 动态数值 X | 自动化（`ValueExpr` 白名单 + 求值时机） |
| ⑲ | 期限与撤销（**⑲-a 卡面↔实现；⑲-b 引擎运行期**） | ⑲-a 自动化；⑲-b 实现侧核对 |
| ⑳ | 结构卫生（同卡结构，非卡面可比） | 自动化 |
| **㉑** | **效果切分规则** | **自动化（切段）** |
| ㉒ | 构筑级/非游戏内规则 | 自动化（登记对齐）+ 裁定 |
| **㉔** | **卡文歧义必须回问用户** | **强制流程（自动化列候选）** |

---

## 附：核查产出的问题分级
| 级别 | 含义 | 示例 |
|---|---|---|
| **P0 静默失效** | 效果完全/基本不生效，无任何报错 | 漏注册、前缀词条漏装、`CanTargetSelf` 漏声明致候选为空、未登记阶段名 |
| **P1 语义错误** | 效果能生效但结果与卡文不符 | 拆段错误、`MassSequential` 缺失、目标范围过宽/过窄、光环范围错 |
| **P2 交互不当** | 结果正确但询问窗口/置灰不合适 | 固定金额弹数值框、选项型弹输入框、不满足条件不置灰 |
| **P3 规范问题** | 不影响行为，属结构卫生 | `EffectId` 命名、空壳效果、注释缺失 |

> 全部问题仅记录，标注【待裁定】；用户裁定后才进入修复阶段。
