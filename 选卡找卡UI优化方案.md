# 选卡 / 找卡 UI 优化方案（定稿）

> **改动范围**：单机（单机热座）的 **View 层** 与 **View↔Controller 的调用参数**。
> 不改候选枚举、合法性判定、结算顺序、状态写入、联机协议。
> **不涉及**任何具体处理逻辑（第七章【乙】组若要放宽"能否取消"即属改逻辑，本批不做）。
>
> 基底：2026-10-15 工作区源码（`Script/UI/**`、`Script/Manager/**`、`Cards/**`、`Script/Interfaces/IGameUIHooks.cs`）。

---

## 一、目标

| # | 目标 |
|---|------|
| **①** | 目标落在〔争夺区角色 / 结附于地区的附属 / 封印于地区的封印 / 自己手牌 / 地区 / 秘社〕时：**场上高亮 + 鼠标左键点选**，并弹出**提示窗** |
| **②** | 其余一切目标（结附·封印于卡的附属封印、资产、牌库、墓地、对手手牌、放逐区…）：弹出**选择窗**（卡面网格 + 每张卡标注**明确到"哪个玩家 / 哪个地区"的所在区域** + 确认按钮） |
| **③** | 发动效果取目标**支持取消**；取消 ⇒ 整个效果**不进堆叠** |
| **④** | 结算取目标能否取消 ⇒ 按第七章清单固定 |
| **附加** | 修掉「以地区为目标时高亮不可见」的缺陷（属需求 ① 的一部分） |

**判定口径（一句话）**：
一次选择里，**只要候选含任意一张"不在 cat-1 范围内"的卡 ⇒ 走选择窗（连 cat-1 的卡一起进窗）；候选全部在 cat-1 内 ⇒ 走提示窗 + 场上高亮 + 左键。**

---

## 二、两个窗口：术语与程序组件

| 你的叫法 | 是什么 | 程序落点 |
|---|---|---|
| **选择窗** | "选择卡的窗口"：候选卡的**卡面网格**，每张卡下方标注**所在区域**，底部**确认**按钮（+ 取消） | **新建** `Script/UI/CardPickWindow.cs`（模板＝现有 `InspectWindow`）。它**取代**旧的 `ActivationChoiceWindow`（纯按钮列卡名） |
| **提示窗** | "提示玩家点选高亮的窗口"：**不列卡**，只提示"请点击高亮的卡/地区"，并承载本槽的**取消出口** | **已有**：`TargetSelectionWindow` 的单取消布局（`Open(..., allowCancel, cancelLabel)` / `ApplySingleCancelLayout`） |

两者是**两个不同的组件**，实现时不得混用。

三条既有通道与术语的对应：

| 通道 | 入口 | 形态 | 对应术语 |
|---|---|---|---|
| A | `TriggerInteractionCoordinator.RequestActivateTargetSelection`（`:281`） | 场上淡黄高亮 + 左键点选 | **提示窗** |
| B | `TriggerInteractionCoordinator.RequestFieldMultiPick`（`:429`） | 高亮 + 一个多选窗（左键加选 / 右键取消 / 确认） | **提示窗**的多选态 |
| C | `Arena.PromptPickCard`（`Arena.cs:841`）→ `ActivationChoiceWindow` | 一列按钮（卡名） | **选择窗**要替换的旧形态 |

---

## 三、判定口径（cat-1 / cat-2）

### 3.0 判定原则：逐卡实例、运行时、动态

分类**不按效果声明、也不按 `TargetKind` 静态映射**，而是**在"选择弹出的那一刻"，对本次候选列表里的每一张卡实例逐张判定**，再按规则甲/乙汇总。

- **同一张卡在不同状态下可落不同类**：附属卡**结附于地区**时是 cat-1，**结附于角色/秘社/附属**时是 cat-2。
  同一效果两次发动（场上构成不同）可能一次走提示窗、一次走选择窗。
- 判定只决定**"要不要多开一个选择窗"**，不改变候选集合：候选由 Controller 给定，View 不增不减、不改合法性。
- 判定数据全部来自 `CardInstance` 现有字段（`CurrentZone` / `IsAttached` / `AttachedTo` / `IsSeal` / `SealedBy` / `OwnerId` / `ZoneDetailId`），
  **不新增任何字段**。

### 3.1 成员表

| 类别 | 成员 |
|---|---|
| **cat-1**（提示窗：场上高亮 + 左键） | 争夺区角色 / 暗藏者｜**结附于地区**的附属｜**封印于地区**的封印｜**自己手牌**（＝做选择者自己的）｜**地区本身**（地区目标）｜**秘社** |
| **cat-2**（选择窗：卡面 + 区域标签 + 确认） | **结附/封印于卡**（角色 / 秘社 / 附属）的附属封印｜**资产（无条件）**｜牌库｜墓地｜**对手手牌**｜放逐区｜计分区｜堆叠｜任何拿不到可见场上 UI 的卡 |

> **资产是无条件 cat-2**：无论资产卡此刻在场上是否可被点中，都走选择窗（本方案不沿用任何"资产可点选"的既有行为）。

判定时机＝选择弹出的那一刻（决策持锁期间卡不会移动，一次判定即稳定）。

### 3.2 数据判据（逐卡实例）

| 形态 | 判据 | 归类 |
|---|---|---|
| 争夺区角色 / 暗藏者 | `CurrentZone == AreaZone` | cat-1 |
| 结附于地区 | `IsAttached && AttachedTo == null`（`ZoneDetailId` ＝ 地区 UID） | cat-1 |
| 结附于角色 / 秘社 / 附属 | `IsAttached && AttachedTo != null` | cat-2 |
| 封印于地区 | `IsSeal && SealedBy == null` | cat-1 |
| 封印于角色 / 秘社 / 附属 | `IsSeal && SealedBy != null` | cat-2 |
| 自己手牌 | `CurrentZone == Hand && OwnerId == 做选择的玩家` | cat-1 |
| 对手手牌 | `CurrentZone == Hand && OwnerId != 做选择的玩家` | cat-2 |
| 秘社 | `CurrentZone == CouncilZone` | cat-1 |
| **资产** | `CurrentZone == AssetZone` | **cat-2（无条件）** |
| 自己的地区 | 本槽是"地区目标"（`isArea`）时的 `AreaSlot` | cat-1 |

写入口径参考：`ZoneManager.AttachAndRegister`（`ZoneManager.cs:617-635`，结附于地区时 `host` 传 `null`）、
`TurnManager.SealCard`（`:557-579`，封印于地区时 `SealedBy == null`）。
渲染路由：`Arena.Layout.cs:641` 的 `hostCard = card.IsAttached ? card.AttachedTo : (card.IsSeal ? card.SealedBy : null)`
—— **渲染位置 ≠ 归类**："封印于角色"的封印虽然画在场上（宿主卡旁），仍归 cat-2。

> 宿主候选枚举 `EffectEngine.CollectOwnRuleHosts`（`:4148`）扫 `CouncilZone + AreaZone + AttachedZone`
> ⇒「结附于附属的附属」是规则支持的正式形态，必须覆盖。

### 3.3 规则甲 / 规则乙

- **规则甲（同一个候选列表内）**：列表里只要有一张 cat-2 的卡 ⇒ **整个列表（含 cat-1 的卡）全部走选择窗**；全部 cat-1 ⇒ 走提示窗 + 场上高亮 + 左键。
- **规则乙（同一效果的多目标）**：同一效果的多个目标（`TargetSlots` 多槽、或 `Target` ＋ `NeedsAreaTarget` 的组合）中，**只要有一个不是"全 cat-1"⇒ 该效果的全部目标槽都走选择窗**（逐槽各开一个选择窗），不再混用场上点选。
  - **实例（已定）**：`JC092 死灵召唤` / `JC133 操控死尸`（槽1＝目标地区、槽2＝你墓地角色牌）⇒ **开两次选择窗：先"选地区"、再"选墓地"**。
  - **边界**：规则乙只管**取目标**，不含**代价选择**。像"牺牲一个角色作代价 → 再取目标"这种顺序出现的两个窗（代价窗 + 目标窗）不合并。

### 3.4 分类收口在 View 的取目标入口

现有"走场上点选还是走列表窗"的分流做在 Controller 里、且按 `TargetSpec` **静态**判定
（`TurnManager.EnsureTargetThenPush`，`TurnManager.cs:2076`：`TargetZonePool.IsZonePileSpec(spec) && !IsFieldVisibleSpec(spec)`），
而 `IsFieldVisibleKind(Attachment/Seal)` 是**无条件 true** ⇒ spec 级判定无法表达"按宿主逐张看"。
本方案把分类**整体上移到 View**，Controller 的 spec 级分流一个字不改（仍满足"View 不写 Model、Controller 不引用具体 UI 类型"）。

**改法**：一份纯静态分类函数（`CardPickChannel.Classify(candidates) → { 全 cat-1 | 含 cat-2 }`），接在下列**全部**取目标入口上：

| 入口 | 位置 | 分类后行为 |
|---|---|---|
| 通道 A：激活取目标 | `TriggerInteractionCoordinator.RequestActivateTargetSelection`（`:281`） | 全 cat-1 ⇒ 提示窗 + 场上点选即确认；**含 cat-2 ⇒ 场上高亮 + 场上点击可选中 + 选择窗（确认提交）** |
| 通道 A：触发取目标 | `TriggerInteractionCoordinator.RequestTargetSelection`（`:155`） | 同上 |
| 通道 C：选牌 | `Arena.PromptPickCard`（`:841`） | 全 cat-1 ⇒ 改走场上高亮 + 场上点选即确认（提示窗）；含 cat-2 ⇒ 选择窗 |
| 场上取目标（含玩家自选牺牲） | `Arena.PromptPlayTarget`（`:780`） | 同上（资产 ⇒ 必然含 cat-2 ⇒ 选择窗） |
| 弃牌 | `Arena.PromptDiscard`（`:798`）→ 内部走选牌 | 候选＝弃牌者自己手牌 ⇒ 全 cat-1 ⇒ 场上点手牌 |
| 多槽 | `Arena.PromptMultiTargets`（`:972`） | 按规则乙：任一槽非全 cat-1 ⇒ 全部槽走选择窗；全 cat-1 ⇒ 维持现状 |

**两种模式的行为差（一句话）**：

| 模式 | 场上高亮 | 点场上卡 | 确认 |
|---|---|---|---|
| **提示窗模式**（全 cat-1） | 有 | **立即选中并提交** | 无需额外点击 |
| **选择窗模式**（含 cat-2） | 有（**候选 ∩ 有场上 UI** —— cat-2 中拿不到完整场上 UI 的不保证高亮，其选取一律走选择窗） | **只把选择窗内对应卡置为选中**（不提交、不关窗） | 需在选择窗点**确认** |

高亮范围与命中范围**同源**（都＝**候选 ∩ 有场上 UI**）；走提示窗时（全 cat-1）候选里每一张都有场上 UI ⇒ 不存在"高亮了却点不到"。

---

## 四、需求 ①：场上高亮 + 左键选择

### 4.1 现状缺口

1. **高亮总线已覆盖全场玩家卡**：`CardManager.NotifyTargetSelectionChanged` → `Arena.OnTargetSelectionChangedHandler`（`Arena.Events.cs:189`）遍历 `_areaCardUIs` ＋ `_cardUIMap` ⇒ 手牌、附属、封印、资产都会按候选集合高亮。
2. **命中测试只覆盖三类形态**：`CardDragController.PickAreaCardAt`（`:551`）只扫「争夺区角色 ＋ `IsAttached` 的附属 ＋ 资产」⇒
   **封印、自己手牌、秘社 ＝ 高亮却点不动**。若某次候选**只有**这些卡，玩家会被**卡死**
   （提示窗无 `extraLabel` 时确认键 `disabled`、提示"点场上卡后自动关闭"）。
3. **手牌被拖拽吞掉点击**：`Arena._Input` 里"手牌拖拽起手 `TryBeginDragAt`"（`Arena.cs:3076`）排在**选目标路由之前**，且不检查交互锁 ⇒ 点候选手牌会先被拖拽吃掉。
4. **秘社未参与选目标高亮**：`CouncilCardUI` 没有 `SetSelectableHighlight`；`_councilCardUIs` 只有"拖拽结附"会高亮（`Arena.Layout.cs:837 SetCouncilHighlight`）。
5. **地区高亮不可见**（见 4.3）。

### 4.2 改动清单

**核心设计**：命中**不按卡的类型枚举**，而是**按本次候选列表逐张取 UI 命中**——即"候选里凡是有场上 UI 的卡，点它就能选中它"。

| 文件 | 改动 |
|---|---|
| `Script/UI/Interaction/CardDragController.cs` | ① 新增 `PickCandidateCardUidAt(Vector2 globalPos, IReadOnlyList<string> candidateUids)`：**只服务选目标**的命中。遍历候选（按下方固定优先级排序），用**既有的** `Arena.FindCardUIByUID` / `FindCouncilUIByUID` 取该卡此刻的 UI，`Visible && GetVisualGlobalRect().HasPoint(pos)` 即命中并返回该 UID。**不改 `PickAreaCardAt`**（它同时服务拖拽落点 / 激活拖拽 / 战斗分配，属拖拽语义）。② `TryBeginDragAt` 开头加守卫：**选目标期间不允许起手拖拽** |
| `Script/UI/Interaction/TriggerInteractionCoordinator.cs` | `HandleTargetSelectionClick`（`:676`）的命中源换成新注入的 `_pickCandidateCardUidAt`，调用时传入**本次候选 UID 列表**；命中后按当前模式分流：**提示窗模式 ⇒ 直接提交**；**选择窗模式 ⇒ 把该 UID 转发给选择窗"置为选中"**（不提交、不关窗） |
| `Script/UI/Arena.cs` | 装配协调器时多注入一个委托（现装配点 `:470-477`） |
| `Script/UI/Arena.Events.cs` | `OnTargetSelectionChangedHandler`（`:189`）增补 `_councilCardUIs` 的高亮刷新 |
| `Script/UI/CouncilCardUI.cs` | 新增 `SetSelectableHighlight(bool)`（照抄 `CardUI.SetSelectableHighlight`：`_showSelectableHighlight` + `QueueRedraw()` + `_Draw` 画黄框 `Color(1, 0.85, 0.1)` 宽 6） |

**不需要新增任何 UI 集合**：`Arena.FindCardUIByUID`（`Arena.Layout.cs:1158`）已覆盖手牌容器、资产容器、计分区容器、`_areaCardUIs`、地区槽（含结附于地区的附属 / 封印）、秘社容器（含结附秘社的附属）、牌库、墓地；秘社卡本身走 `FindCouncilUIByUID`（`:1198`）。

**候选遍历优先级**（同一位置重叠时按"视觉在上"取胜）：**自己手牌 → 附属 / 封印 → 争夺区角色 → 资产 → 秘社**。

**命中范围 ＝ 候选列表 ∩ 有场上 UI**。两种模式共用同一命中，只是"点中之后做什么"不同（见 3.4 行为差表）；高亮范围与命中范围同源 ⇒ 不存在"高亮了却点不到"。

> 秘社进 cat-1 是必需的：共 **11 张**卡的**打出目标就是本方秘社**
> （`JC009/JC012/JC025/JC033/JC034/JC039/JC053/JC068/JC082/JC115/JC117`，`TargetRules = [Council, Friendly]`），
> 它们今天**没有可命中的秘社 UI** ⇒ 不补就点不动。

### 4.3 地区高亮修复（现状缺陷）

`AreaSlot.SetHighlight`（`AreaSlot.cs:138-142`）现在只有一句 `SelfModulate = new Color(1.2f,1.2f,0.4f)`，而：

1. Godot 的 `SelfModulate` **只作用于节点自身绘制、不传播给子节点**（`Modulate` 才传播）；
2. `AreaSlot` 根节点是纯 `Control`，**自身不绘制**；
3. 场景里挂在根下的 `Background`（`AreaSlot.tscn:16-25`）`visible = false`；真正可见的是 `Player1Area/Player2Area` 里的 `Background`（**孙子节点**，同样不受根 `SelfModulate` 影响）。

⇒ **什么都看不见**。对照：同文件的 `SetSelected`（`:149`，多选"已选中"绿色）用的是 `Modulate`（会传播）⇒ 多选态能看到绿色，候选态什么都没有。

**修复**：

| 文件 | 改动 |
|---|---|
| `Script/UI/AreaCardUI.cs` | 新增 `SetSelectableHighlight(bool)` + `_showSelectableHighlight` + `_Draw()` 画黄框（与 `CardUI.cs:602-606` 逐字同款：`DrawRect(new Rect2(new Vector2(-2,-2), Size + new Vector2(4,4)), new Color(1f,0.85f,0.1f), false, 6f)`） |
| `Script/UI/AreaSlot.cs` | `SetHighlight(bool)`：保留原 `SelfModulate` 赋值（兼容既有调用方），**新增** 转发 `_areaCardUI?.SetSelectableHighlight(on)`，并给两侧 `Player1Area/Player2Area` 的 `Background` 上 `Modulate` 淡黄。**不用根节点 `Modulate` 上色**（会与 `SetSelected` 的绿色互相覆盖）。`SetSelected` 保持不动 |

**视觉形态（已定）**：地区卡画黄框 **＋** 两侧背景淡黄（两个都做）。
**地区目标保留提示窗**：它承载本槽的"取消（不发动）/（不打出）"出口与动作提示，删掉就没有取消通道。
**高亮清除**：全部走既有 `SetHighlight(false)`（协调器三个收尾函数已在清），不新增清理点。

### 4.4 需求 ① 不改的部分

- ❌ 不改 `AttachedCardUIs` / `AssetCardUIs` 的现有条件（它们只服务拖拽落点）；
- ❌ 不改拖拽落点、拖拽高亮；
- ❌ 不改候选枚举（`GetValidTargetUidsForTrigger` / `IsLegalActivateTarget` / `IsLegalPlayTarget` / `TargetZonePool` / `TargetValidity`）。

### 4.5 选择窗 ↔ 场上点击联动（**选择窗模式下**）

**要的行为**：走选择窗时，点击场上高亮的卡 ⇒ 选择窗内**对应条目变为选中**（与在窗内点它完全等价）；**仍然要点选择窗的「确认」**才提交。

| 项 | 设计 |
|---|---|
| 命中 | 复用 4.2 的 `PickCandidateCardUidAt(pos, 候选UID列表)`；命中结果若在候选内 ⇒ 转给选择窗 |
| 转交 | `CardPickWindow` 暴露 `SelectByUid(string uid)`：滚动到该条目 + 置选中态（不回调、不关窗、不提交） |
| 选中视觉 | 场上被选中的卡给**与候选黄框不同**的"已选中"样式（绿色边框，与选择窗内选中态同色）；换选/取消选中时清回候选黄框 |
| 清理 | 选择窗关闭/取消时清掉全部"已选中"样式；候选黄框仍由既有 `SetHighlight(false)` 收尾清理 |
| 边界 | ① 只对**有场上 UI 的候选卡**生效（牌库/墓地/对手手牌没有场上 UI ⇒ 只能点窗内条目）；② 提示窗模式**不做**此联动（那条路径点场上卡＝直接提交）；③ 多选场景（`PromptMultiTargets`）点场上卡＝切换该项选中态，与窗内点击同一入口 |

> 这条联动是"用选择窗取代按钮列表"后**不牺牲场上直观性**的关键：玩家仍可直接点场上的卡，只是提交动作统一落到不可误触的「确认」按钮上。

> **同一出口还服务《UI美化与空间重排方案》的 hover 展开面板**（角标展开＝结附 / 封印于卡、层叠展开＝资产 / 计分区、对手手牌展开）：这些面板的成员**全部属 cat-2**（见第十一章），点面板内的卡 ＝ 调用**同一个** `CardPickWindow.SelectByUid(uid)`。因此 `SelectByUid` 必须对「该 uid 当前没有场上 UI（卡正折叠 / 层叠中）、或尚未在窗内可见」也生效——这正是面板里的卡的情形。

---

## 五、需求 ②：选择窗（新组件 `CardPickWindow`）

### 5.1 形态

```
┌──────────────────────────────────────────────┐
│ 请选择墓地中的目标牌                            │ ← 标题（= prompt）
│ 点选一张牌后按「确认」                          │
│ ┌──────┐ ┌──────┐ ┌──────┐                    │
│ │ 卡面 │ │ 卡面 │ │ 卡面 │                    │
│ └──────┘ └──────┘ └──────┘                    │
│  你的墓地  玩家2墓地  封印于玩家1的秘社  你地区3  │ ← 每张卡下方的「所在区域」标签
│       [ 确认 ]        [ 取消 ]                 │ ← 取消按钮由 allowCancel 决定
└──────────────────────────────────────────────┘
```

- **点选不立即提交**，选中卡加绿色高亮边框；**确认**才回调 `onPicked(inst)`；**取消/关窗** → `onCancelled()`。
- **场上的卡也能点**：点场上高亮的候选卡 ＝ 在本窗内点它（置为选中，仍须确认）——见 4.5。`CardPickWindow` 需暴露 `SelectByUid(string uid)`。
- 只有 1 个候选也照常弹窗（不自动替玩家选）。
- `allowCancel == false` 时：不显示取消按钮，**且不允许点窗外关闭**（保证回调唯一性）。
- 卡多时横向滚动（照抄 `InspectWindow` 的 `ScrollContainer`）。
- 自带 `CardHoverPreview` 子节点（`CardPreviewBus.WindowOverlayName`）。
- **显示真卡面**（对手手牌/对手牌库也显示真卡面；单机热座无信息隔离问题）。
  唯一例外：`CardInstance.IsHidden`（隐藏占位）⇒ 只画卡背 + 区域标签。

> 照抄 `InspectWindow` 踩过的三个坑：① `Window` 默认 100×100 不会按内容撑开 ⇒ **必须显式设 `Size`**；
> ② **必须先 `AddChild` 再 `Init`**（否则 `CardUI._Ready()` 的 `SetFace(false)` 会把翻好的正面盖成卡背）；
> ③ 窗内浮层要**自带** `CardHoverPreview` 子节点（子窗口有自己的视口）。

### 5.2 「所在区域」标签（新增 `Script/UI/CardZoneLabel.cs`，纯静态展示）

**目标**：选择窗里每张候选卡下的区域文字，必须**明确到"哪一个玩家、哪一个地区"**，不允许出现"封印区""附属区"这类**分不清归属**的写法。

#### 5.2.1 唯一解析入口 + 玩家名扩展点

```
CardZoneLabel.Describe(CardInstance inst, int chooserId, Func<int,string> playerNameOf) → string
```

- `playerNameOf` 由 **Arena 注入**：`id => _playerManager.GetPlayer(id).Name`（`Arena._playerManager` 已存在，`Arena.cs:131`）。
- **组件内禁止出现 `"玩家1"` / `"玩家2"` 字面量**——所有玩家称呼一律经这一个委托。
- `PlayerData.Name`（`PlayerManager.cs:13-21`）当下由 `PlayerManager.InitPlayers` 写死为 `"玩家1"` / `"玩家2"`
  （`PlayerManager.cs:58-59`；注意该字段**目前 UI 侧从未读取**，是干净的单一数据源）。
- **将来替换为真实玩家名时**：只改 `PlayerData.Name` 的赋值来源，**本方案新增的任何标签代码一行都不用动**。
- 现状仍有零散硬编码（`Arena.Events.cs:346` 结算文案、`Arena.cs:803` 弃牌子提示、`Arena.cs:789` 日志）——
  **本次不纳入改动**，但新增组件不得再新增此类字面量。

#### 5.2.2 两个显示口径

**玩家归属 `F(playerId)`**：

| 情形 | 显示 |
|---|---|
| `playerId == chooserId`（做选择的玩家本人） | `你` |
| 其余 | `playerNameOf(playerId)`（当下＝`玩家1` / `玩家2`） |

**地区 `A(areaUid, playerId)`** ＝ `{F(playerId)}地区{AreaManager.GetSlotIndex(areaUid)}`，例：`你地区3`、`玩家2地区1`。

> 地区槽的 P1 侧 / P2 侧由 **`card.ControllerId`** 决定——渲染即 `GetAreaContainer(slot, card.ControllerId)`
> （`Arena.Layout.cs:49-50`、`:598`），所以"哪个玩家的地区"＝该卡的 `ControllerId`。
> `N` ＝ `AreaManager.GetSlotIndex(ZoneDetailId)`（1-based，`AreaManager.cs:105-107`；
> 权威顺序见 `GetOrderedAreaUids`，`AreaManager.cs:84-92`）。

#### 5.2.3 完整标签表

| 情形 | 判据 | 标签 |
|---|---|---|
| 争夺区角色 / 暗藏者 | `CurrentZone==AreaZone` | `{F(ControllerId)}地区{N}`；`IsCovert` 时追加 `（暗藏）` |
| **结附于地区** | `IsAttached && AttachedTo==null` | `附属于{F(ControllerId)}地区{N}` |
| **封印于地区** | `IsSeal && SealedBy==null` | `封印于{F(ControllerId)}地区{N}` |
| **结附于秘社** | `IsAttached && AttachedTo.CurrentZone==CouncilZone` | `附属于{F(宿主.ControllerId)}的秘社` |
| **封印于秘社** | `IsSeal && SealedBy.CurrentZone==CouncilZone` | `封印于{F(宿主.ControllerId)}的秘社` |
| 结附于角色 / 暗藏者 / 附属 | `IsAttached && AttachedTo!=null`（非秘社） | `附属于「{宿主.EffectiveCardName}」` |
| 封印于角色 / 暗藏者 / 附属 | `IsSeal && SealedBy!=null`（非秘社） | `封印于「{宿主.EffectiveCardName}」` |
| 自己手牌 | `Hand && OwnerId==chooserId` | `你的手牌` |
| 对手手牌 | `Hand && OwnerId!=chooserId` | `{F(OwnerId)}的手牌` |
| 牌库 | `Deck` | `{F(ControllerId)}的牌库` |
| 墓地 | `Graveyard` | `{F(ControllerId)}的墓地` |
| 计分区 | `ScoreZone` | `{F(ControllerId)}的计分区` |
| 资产区 | `AssetZone` | `{F(ControllerId)}的资产区` |
| 秘社（卡本身） | `CouncilZone` | `{F(ControllerId)}的秘社` |
| 堆叠 | `Stack` | `堆叠`（需要归属时再加 `（{F(ControllerId)}）`） |
| 放逐区 | `Exiled` | `放逐区`（**无玩家维度**，见 5.2.4 第 7 条） |
| **地区目标（地区卡本身，非卡）** | 该槽是地区槽（`isArea`） | `地区{N}（{地区卡名}）` —— **不带玩家归属**，见 5.2.4 第 1 条 |
| 宿主 UI 缺失（异常兜底） | 上面任一"XX于{宿主}"取不到宿主 UI | 退回 `附属区` / `封印区`（仅此为兜底残留） |

> 宿主卡名走 `CardInstance.EffectiveCardName`（`CardInstance.cs:353-354`，含 JC028「鲜血」式名字覆盖）；
> 秘社名走 `CouncilTemplate.CardName`；地区名走 `AreaTemplate.CardName`。

#### 5.2.4 易混区域排查（本次必须逐条对齐）

1. **「地区目标（地区卡本身）」≠「地区内的卡」** —— 地区卡是**共享世界牌、两侧共用、无玩家归属**，
   标签写 `地区{N}（{地区名}）`；而**地区内**的卡（角色/附属/封印）才写 `{玩家名}地区{N}`。**这是最容易弄混的一对。**
2. **争夺区双方角色同处一个地区槽** —— 两张角色都在"地区1"，不带玩家名就无法分辨归属 ⇒ 必须带 `F(ControllerId)`。
3. **结附于地区的附属不在"附属区"数据桶里** —— 它的 `ZoneDetailId` ＝ 地区 UID，只是**渲染**进地区容器；
   标签必须写 `附属于{玩家名}地区{N}`，**不要**写"附属区"。
4. **封印于地区同理**（`SealedBy==null`），写 `封印于{玩家名}地区{N}`。
5. **暗藏者与角色同区（`AreaZone`）** —— 靠 `（暗藏）` 后缀区分，避免与普通角色混淆。
6. **秘社必须带玩家归属** —— 双方各有一个秘社（`_councilContainer1/2`），只写"秘社"无法分辨。
7. **放逐区无玩家维度可查** —— `ZoneManager.GetZoneCards` **没有 `Exiled` 分支**（落到 `default` 返回空列表，
   `ZoneManager.cs:705-753`）⇒ 只能写 `放逐区`。**若将来要区分双方放逐区，需要先补该分支（属改逻辑，本批不做）。**
8. **"对手手牌 / 对手牌库 / 对手墓地"一律带玩家名**（而非只有"对手"两字）—— 名字替换后自动变成真实玩家名。
9. **"你的/你"只对做选择者本人成立** —— 热座下同一块屏有两方手牌，`chooserId` 不同则同一个"手牌"文案会不同。

### 5.3 接线

| 入口 | 改动 |
|---|---|
| `Arena.PromptPickCard`（`:841`） | 先分类：全 cat-1 ⇒ 改走场上高亮 + 场上点选即提交（提示窗）；含 cat-2 ⇒ 开 `CardPickWindow`。`allowCancel` 如实映射到取消按钮 |
| `Arena.PromptPlayTarget`（`:780`） | 同上（玩家自选牺牲；候选＝资产 ⇒ 必然含 cat-2 ⇒ 选择窗） |
| `Arena.PromptDiscard`（`:798`） | 候选＝弃牌者自己手牌 ⇒ 全 cat-1 ⇒ 场上点手牌 |
| `TriggerInteractionCoordinator.RequestTargetSelection`（`:155`）／`RequestActivateTargetSelection`（`:281`） | 先分类；**两种模式都保持场上高亮**，含 cat-2 时**额外**开 `CardPickWindow`（场上点击转为"选中窗内条目"，见 4.5） |
| `Arena.PromptMultiTargets`（`:972`） | 按规则乙：任一槽非全 cat-1 ⇒ 所有槽走选择窗（地区槽条目＝地区卡面 +「地区N」标签）；全 cat-1 ⇒ 维持现状（`FieldMultiPick` 一个窗多选 / 逐槽提示窗） |

**不会卡住玩家的保证**：走提示窗时，候选里每一张都能被 `PickCandidateCardUidAt` 命中（4.2 —— 命中范围＝候选 ∩ 有场上 UI）；走选择窗时，候选里每一张都有卡面。不存在"高亮了却点不到"的第三态。

### 5.4 联机（本批不做）

联机侧 `ArenaOl` 对 `SelectZoneCard` / `SelectHandCard` / `SelectDeckCard` 一律返回 `false`（`ArenaOl.cs:506` 的 switch 无这几个 case），落到通用按钮面板。
改法（留待联机批次）：新增 `OpenOnlineCardPickWindow(req)`，用 `req.OptionCardRefs` + `ClientCardManager.RebuildFromRef` 重建卡实例（与 `OpenOnlineInspectWindow` 同一手法，`ArenaOl.cs:852`），交给同一个 `CardPickWindow`。
服务端已为 `zonePile` 请求下发 `OptionCardRefs` + `OptionLabels`（`TurnManager.cs:1883-1893`），**协议无需改动**。
联机的逐卡判定可**复用同一份规则**：`ZoneCardRef`（`Script/Net/GameSnapshot.cs:156`）已携带 `Zone` / `OwnerId` / `IsAttached` / `AttachedToUid` / `IsSeal` / `SealedByUid` / `ZoneDetailId`
⇒ 判定字段齐备，两端不会漂移。（`IsHidden` 占位实例只出现在对手手牌/牌库，本就是 cat-2，不影响判定。）
联机显示真卡面会与信息隔离（`IsHidden` / `IsSealedFaceDown`）冲突 ⇒ 留联机批次单独定。

---

## 六、需求 ③：发动效果取目标支持取消

### 6.1 机制已存在，只差把参数接到缺出口的路径上

`TriggerInteractionCoordinator.RequestActivateTargetSelection`（`:281`）已有 `extraLabel` / `onExtra`：
`extraLabel = "取消（不发动）"` ⇒ 提示窗多一个取消按钮；`hintWindow:false` 时 ⇒ 场上**右键**触发同一出口（`_cancelActivateTargetSelection`，`:405`）；收尾统一走 `FinishSelection`。
`TargetSelectionWindow` 侧已支持 `Open(..., allowCancel, cancelLabel)`（`:250`）、`ApplySingleCancelLayout`（`:185`）、`close_requested == 取消`（`:57`）。

### 6.2 需补出口的四条路径（全部用**既有**出口，不写新逻辑）

| # | 路径 | 位置 | 现状 | 改成 | 取消后落地 |
|---|---|---|---|---|---|
| **C1** | 事务卡拖拽取目标 | `Arena.BeginSorceryTargetSelection` → `Arena.cs:2834` | `extraLabel: null` | `extraLabel: "取消（不打出）"` | `_actionService.ReturnCardToHand(playerId, sorcery.UID)` + `ArrangeAllCards()`（＝同方法 `:2770` 费用不足分支的同一出口） |
| **C2** | 场上激活取**卡**目标 | `Arena.BeginAttachmentActivateWithTarget` → `Arena.cs:2720` | `allowCancel` 默认 `false`；且 `hintWindow:false` | `allowCancel: true`（`extraLabel:"取消（不发动）"`）＋ `hintWindow: true` | `onExtra` 里 `RefreshActivateHighlights()`（代码已在 `:2731-2737`） |
| **C3** | 场上激活取**地区**目标 | `Arena.BeginActivateAreaSelection` → `Arena.cs:2407` | 无 `extraLabel` | `extraLabel: "取消（不发动）"` | `onExtra` → `RefreshActivateHighlights()` |
| **C4** | 效果免费打出牌取目标 | `Arena.PromptPlayTarget`（`:780`） | 无取消出口 | 补取消出口 | 牌留在手牌 |

### 6.3 已支持取消、不需要动的路径

触发入堆叠取目标（`:155`，取消＝不入堆叠、留在触发排序窗）｜入堆叠取区域牌堆目标（`TurnManager.cs:2088`）｜触发/模式的玩家目标（`:2019`）｜墓地行动选地区（`Arena.cs:2094`，已有"取消（不打出）"）｜手牌行动双击（`Arena.cs:583`，`cancelable:true`）｜激活代价（牺牲/移除标志，`ActivationChoiceWindow` 自带取消）｜"选择一项"模式（回 `-1`）｜多目标槽（`PromptMultiTargets` 每槽可留空）。

### 6.4 一致性口径

- **取消 ＝ 整个效果不进堆叠**；已发生的代价支付不退还（现有流程代价都在取目标之后付，取消时还没付）。
- 触发器取目标取消的既有特例：**留在触发排序窗口**（不是丢弃）。此口径保持，不与"发动取目标取消"混。

---

## 七、需求 ④：结算取目标取消清单

### 7.0 判据

"这一处能不能取消"的真相源＝Controller 传进来的 **`allowCancel` 实参**：

- `allowCancel == false` 的地方，回调收到 `null` 会**直接崩/挂死**（代码写的是 `picked.PlayerTemplate.XXX` 或 `picked ?? 列表最后一个`）⇒ 只加 UI 取消按钮＝**改逻辑**（须同时加 null 分支）；
- `allowCancel == true` 的地方，回调里**已有** `if (picked == null) { ... }` 分支 ⇒ UI 加取消按钮是**纯 UI 改动**。

### 7.1 【甲】已有取消 / 天然可选（本次只保证 UI 上看得见）

| ID | 场景 | 位置 | 现状 |
|----|------|------|------|
| A1 | 触发入堆叠取场上目标 | `TriggerInteractionCoordinator.cs:155` | 可取消（取消＝不入堆叠、留在排序窗） |
| A2 | 触发入堆叠取区域牌堆目标（墓地/手牌） | `TurnManager.cs:2088` | 可取消 |
| A3 | 触发/模式取玩家目标 | `TurnManager.cs:2019` | 可取消（回 `-1`） |
| A4 | 触发入堆叠前"选择一项"模式 | `TurnManager.cs:1983` | 可取消（回 `-1`） |
| A5 | 触发入堆叠前"是否支付 N" | `TurnManager.cs:2178` | 可取消 |
| A6 | 触发 `ConfirmPrompt` 是/否 | `TurnManager.cs:2163` | 二选一 |
| A7 | 墓地行动选地区 | `Arena.cs:2094` | 已有"取消（不打出）" |
| A8 | 激活代价：牺牲附属/角色 | `Arena.BeginActivateWithSacrifice`（`:2245`） | 已有"取消（不发动）" |
| A9 | 激活代价：移除时间标志 | `Arena.BeginActivateWithCounterCostChoice`（`:2307`）/ `ShowCounterAllocationStep`（`:2343`） | 已有"取消（不发动）" |
| A10 | 事务卡"选择一项" | `TurnManager.cs:3572` | 取消 ⇒ 不打出 |
| A11 | 事务卡多目标逐槽 | `Arena.PromptMultiTargets`（`:972`） | 每槽有"留空 / 取消（不打出）" |
| A12 | 效果免费打出牌取目标 | `Arena.PromptPlayTarget`（`:780`） | 无取消出口 ⇒ **补**（＝ C4） |
| A13 | 结算期"选择一项" | `EffectEngine.cs:528` | 取消 ⇒ 视为"否"（**不**改成中止整条） |
| A14 | 结算期"选择一个数字" | `EffectEngine.cs:643` | 有取消（`chosen < 0` ⇒ 该效果不发动） |
| A15 | 结算期"可选支付 N"（固定额） | `EffectEngine.cs:2367` | 可取消（＝不支付） |
| A16 | 结算期"可选支付 N"（自选额） | `EffectEngine.cs:2369` | 可取消 |
| A17 | 结算期"可选牺牲自身" | `EffectEngine.cs:2447` | 可取消 |
| A18 | 结算期"支付 N 否则牺牲本卡" | `EffectEngine.cs:2564` | 二选一，无第三出口（见 B12） |
| A19 | 预测 X（置顶/置底） | `Arena.PromptPrediction`（`:937`） | 点窗外关闭 ⇒ 原序全留牌库顶 |
| A20 | 先手破平 | `TieBreakWindow` | 有确认/取消 |
| A21 | 封印回手"可免费打出其中一张" | `EffectEngine.cs:2611` | 可取消（＝不打） |
| A22 | 触发/结算"是否改为执行主分支" | `EffectEngine.cs:528` + `LandAskBranch` | 取消＝否 |

### 7.2 【乙】Controller 没有 null 分支 ⇒ 一律保持**不可取消**（本批不动）

| ID | 场景 | 位置 | 现状 `allowCancel` |
|----|------|------|--------------------|
| B1 | 弃牌（自己弃 / 由施术者从对手手牌指定） | `Arena.PromptDiscard`（`:798`）→ `EffectEngine.cs:3212` | `false` |
| B2 | 检索取牌 | `EffectEngine.cs:3843` | `false`（既定口径：检索强制、不可取消） |
| B3 | 检索『结附』选宿主（JC051） | `EffectEngine.cs:4036` | `false`（回调已有非法回包兜底取第 1 个宿主） |
| B4 | 封印取手牌 | `EffectEngine.cs:3419` | `false` |
| B5 | 选择一张牌放置进场（`UnsealToArea`） | `EffectEngine.cs:6405` | `false` |
| B6 | 从手中选一张牌（展示后置于目标地区） | `EffectEngine.cs:1675` | `false` |
| B7 | 领袖超限选择牺牲 | `LeaderRule.cs:110` | `false`（规则约束，不是玩家选择权） |
| B8 | 道途超限选择牺牲 | `PathRule.cs:109` | `false` |
| B9 | 遏制 X：逐次选地区移除标志 | `CurbRule.cs:152` + `Arena.PromptCurbMarks`（`:892`） | 强制；空回包＝按确定性兜底补齐 |
| B10 | 准备维护：支付 N / 横置（JC068） | `Arena.PromptPayOrTap`（`:727`） | 强制二选一 |
| B11 | 贿赂：移除标志 / 改为牺牲（JC128） | `EffectEngine.cs:1955` | 二选一 |
| B12 | 支付 N 否则牺牲本卡（JC064） | `EffectEngine.cs:2564` | 二选一 |
| B13 | 检视结果 | `Arena.PromptInspectCards`（`:1173`） | 只有一个"确认" |
| B14 | 调查对抗分配 | `InvestigationWindow` | 强制分配 |
| B15 | 战斗对抗分配 | `StrengthWindow` | 强制分配 |
| B16 | 玩家自选牺牲（角色 / 暗藏者 / 附属 / 资产） | `EffectEngine.CollectSacrificePicks`（`:5824`）→ `PromptPlayTarget` | `false`（回调无 null 分支，非法回包兜底 `candidates[0]`；且属**代价**支付，不可取消） |

### 7.3 【丙】需求 ③ 新增取消的四处

| ID | 场景 | 位置 |
|----|------|------|
| C1 | 事务卡拖拽取目标 | `Arena.cs:2834` |
| C2 | 场上激活取卡目标 | `Arena.cs:2720` |
| C3 | 场上激活取地区目标 | `Arena.cs:2407` |
| C4 | 效果免费打出牌取目标 | `Arena.cs:780` |

---

## 八、全量效果选择点排查（精确清单）

### 8.1 排查范围与方法

| 项 | 结论 |
|---|---|
| 已实现效果脚本 | **152 个**：JC **134**（`Cards/Effects/PlayerCards/JC/*.cs`）＋ XG **1**（`Cards/Effects/PlayerCards/XG/XG001_yinyaotanqiuzhe.cs`）＋ 秘社 **17**（`Cards/Script/CouncilCards/JC/*.cs`） |
| **涉及"玩家做选择"的** | **112 个** |
| 无选择的 | 40 个（纯持续/关键字/群体动作，`NeedsSelection=false`） |
| 未实现效果的系列 | **8 个系列 515 张卡**（BQ 117 / WM 98 / JZ 76 / BH 59 / XQ 52 / CQ 39 / DQ 31 / LC 30 / TK 13）**只有卡文 `.tres`、没有任何效果实现**（`AllEffects.RegisterAll()` 只注册 JC + XG）⇒ **不涉及选择窗** |
| 特例 | `JC125 无知路人`（135 张 JC 中唯一无效果脚本，卡文只有构筑规则）⇒ 无选择 |

排查口径：机械提取每个效果脚本的 `TargetSpec.Kind/KindAlt/SpecAlt`、`PlayerCardData.TargetRules`、`TargetSlots`、`NeedsAreaTarget`、`Modes`，以及全部"会问玩家选牌"的动作动词
（`Search` / `Discard` / `Seal{手牌}` / `UnsealToArea` / `PlaceHandCardToArea` / `RevealSealedAndQueueFreePlay` / `SacrificeChoice` / `PutToArea` / `Attach`），
再按第三章口径逐条归类。
Controller 侧的**全部决策入口**（已核对无遗漏）：`PromptPickCard`(×8)、`PromptDiscard`(×2)、`PromptPlayTarget`(×3)、`PromptPlayerTarget`、`PromptMultiTargets`、`PromptInspectCards`(×2)、`PromptPrediction`(×2)、`PromptEffectMode`(×2)、`PromptTwoChoice`(×2)、`PromptNumberInput`(×4)、`PromptOptionalPay`(×2)、`PromptPayOrTap`(×2)、`PromptPayOrSacrifice`、`PromptCurbMarks`。

### 8.2 分类结果总览

| 组 | 内容 | 张数 |
|---|---|---|
| **A** | 全部选择点都是 cat-1 ⇒ **提示窗** | **72** |
| **B** | 含 cat-2 选择点 ⇒ **选择窗** | **25** |
| **C** | 附属/封印的宿主**运行时**决定 ⇒ 混合（可能退化为选择窗） | **7** |
| **D** | 只有非卡选择（玩家目标 / 检视 / 预测） | **7** |
| — | 仅"预测"窗（`JC104`） | 1 |

### 8.3 A 组：提示窗（72 张）

**A-1 效果目标（`TargetSpec`，纯 cat-1）—— 51 张**

`JC002 JC003 JC004 JC006 JC008 JC009 JC010 JC012 JC015 JC017 JC019 JC020 JC021 JC028 JC039 JC058 JC061 JC062 JC063 JC065 JC066 JC069 JC071 JC074 JC075 JC078 JC087 JC088 JC091 JC092 JC094 JC097 JC099 JC100 JC102 JC105 JC106 JC108 JC110 JC117 JC118 JC121 JC122 JC123 JC124 JC129 JC133 MSJC010 MSJC011 MSJC014 MSJC017`
（`JC020/JC063/MSJC014` ＝ 角色**或**暗藏者；`JC021/JC065/JC092/JC133/JC118` 含地区槽）

**A-2 打出目标（`PlayerCardData.TargetRules`，纯 cat-1）—— 39 张**

| 大类 | 张数 | 卡 |
|---|---|---|
| 角色 | 17 | `JC006 JC008 JC015 JC019 JC020 JC036 JC063 JC073 JC078 JC082 JC089 JC091 JC093 JC101 JC102 JC106 JC116` |
| 秘社 | 11 | `JC009 JC012 JC025 JC033 JC034 JC039 JC053 JC068 JC082 JC115 JC117` |
| 地区 | 12 | `JC027 JC035 JC047 JC048 JC050 JC064 JC065 JC090 JC118 JC120 JC127 JC132` |
| 暗藏者 | 1 | `JC063` |

**A-3 地区目标（`NeedsAreaTarget = true`）—— 6 张**：`JC033 JC117 MSJC010 MSJC011 MSJC015 MSJC016`

**A-4 多目标槽（全 cat-1 ⇒ 走通道 B 的 `FieldMultiPick`，一个窗多选）**

| 卡 | 槽 | `MinChosenSlots` |
|---|---|---|
| `JC021 下落相逢` | 地区 + 本方角色 + 敌方角色（3 槽） | 1 |
| `JC062 调查档案` | 暗藏者 ×2（2 槽） | 0 |

**A-5 非目标型选择点（候选全是 cat-1）**

| 卡 | 选择点 |
|---|---|
| `JC041 锦鲤` | 弃牌（双方各弃 1，自选）⇒ 各自手牌 |
| `JC130 深入研究` | 弃牌（自己弃 1，自选） |
| `JC046 无面涂路者` | 玩家自选牺牲（角色） |
| `JC065 审问嫌疑人` | 玩家自选牺牲（暗藏者，限目标地区） |
| `JC119 守秘人` | 封印取手牌；封印回手后免费打出其中一张 |
| `MSJC015 隐士` | 从手牌选一张角色牌展示后暗藏进场（＋ 地区目标） |

**A 组全量卡号（72）**：
`JC002 JC003 JC004 JC006 JC008 JC009 JC010 JC012 JC015 JC017 JC019 JC020 JC021 JC027 JC028 JC035 JC036 JC039 JC041 JC046 JC047 JC048 JC050 JC053 JC058 JC061 JC062 JC063 JC064 JC065 JC066 JC068 JC069 JC071 JC073 JC074 JC075 JC078 JC082 JC087 JC088 JC089 JC090 JC091 JC093 JC094 JC097 JC099 JC100 JC101 JC102 JC105 JC106 JC108 JC110 JC115 JC116 JC117 JC118 JC119 JC120 JC121 JC122 JC123 JC124 JC127 JC129 JC130 JC132 MSJC011 MSJC014 MSJC015`

### 8.4 B 组：选择窗（25 张）

| 卡 | 选择点 |
|---|---|
| `JC007 JC026 JC032 JC051 JC109 JC131` ＋ `MSJC001 MSJC002 MSJC003 MSJC004 MSJC005 MSJC006 MSJC007 MSJC008`（共 14 张） | 检索（牌库） |
| `JC025` | 打出目标（秘社，cat-1）＋ 检索（牌库） ⇒ 秘社也进窗（规则乙） |
| `JC033` | 打出目标（秘社）＋ 地区目标 ＋ 检索（牌库） ⇒ 全进窗 |
| `JC086 切齿人` | 目标＝你墓地角色牌 |
| `JC092 死灵召唤` / `JC133 操控死尸` | 地区槽 + 墓地槽 ⇒ **两个选择窗（先地区、后墓地）** |
| `JC107 本塔的时空` | 目标＝附属**或资产**（`KindAlt=Asset`）＋ 打出目标同上 ⇒ 必然含 cat-2 ⇒ 选择窗 |
| `JC114 好奇的海客` | 从**对手手牌**指定弃牌（玩家目标与检视不涉及选卡窗） |
| `JC134 审判` | 玩家自选牺牲（**资产**） ⇒ 资产无条件 cat-2 ⇒ **必走选择窗** |
| `MSJC010 貘祖` | 目标（角色）＋ 封印取手牌（都是 cat-1）＋ **从封印区取牌（候选＝封印在自身上的角色牌 ⇒ 封印于卡）** ⇒ 全进窗 |
| `MSJC016 颂亡者` | 目标＝你墓地【死】领域角色牌 ＋ 地区目标 ⇒ 同入窗 |
| `MSJC017 梦魇族群` | 选项①目标＝角色（cat-1）；**选项②目标＝你墓地灵体角色牌** ⇒ 选项②走窗 |

> **规则乙的连带效果**：`JC025 / JC033 / MSJC010 / MSJC016` 这类"既有 cat-2 选择点、又有目标槽 / 地区槽"的效果，
> 其目标槽（含地区槽）也一并走选择窗。
> `JC051` 的"结附宿主"候选由**被检索徽章卡自身的 `TargetRules`** 给出
> （徽章卡 `JC012/JC025/JC039/JC053` 全是 `Council + Friendly`）⇒ 宿主＝本方秘社（cat-1），通常只有 1 个候选 ⇒ 直接结附、不弹窗。

### 8.5 C 组：运行时混合（7 张）

| 卡 | 选择点 | 说明 |
|---|---|---|
| `JC005 烈界书` | 目标＝附属 | 结附于地区（cat-1）／结附于卡（cat-2）⇒ 场上有"结附于卡"的附属时整次走选择窗 |
| `JC013 貘` | 目标＝角色**或**附属 | 同上 |
| `JC043 纵火狂` | 目标＝附属（结附于本地区 / 结附于秘社） | 同上 |
| `JC055 安妮斯多拉` | 目标＝角色**或**附属 | 同上 |
| `JC135 愚真` | 目标＝附属（＋打出目标） | 同上 |
| `MSJC013 计时者` | 目标＝角色**或**附属 | 同上 |
| `JC113 五行祷弹镜` | 玩家自选牺牲（**附属**） | 同上 |

> C 组的归类**不按效果静态贴标签**，而是按 3.0 的逐卡实例判定：同一个 `JC005`，在"候选只有结附于地区的附属"时走提示窗 + 场上点选，
> 在"候选里存在结附于卡的附属"时整次走选择窗。**凡目标范围含〔资产〕或〔结附/封印于卡〕的效果都按此处理。**

### 8.6 多选择点效果（规则乙与"顺序两步"的适用清单）

| 卡 | 选择点 | 处置 |
|---|---|---|
| `JC092 / JC133` | 地区槽 + 墓地槽 | **两个选择窗**（先地区、后墓地） |
| `JC025 / JC033` | 打出目标（秘社）+ 检索（牌库） | 秘社也进窗（规则乙） |
| `MSJC010` | 目标（角色）+ 封印取手牌 + 地区目标 + 从封印区取牌 | 全进窗（规则乙） |
| `MSJC016` | 目标（墓地）+ 地区目标 | 全进窗（规则乙） |
| `JC117` | 目标（本方角色）+ 地区目标 | 全 cat-1 ⇒ 保持场上点选（两步都点场上） |
| `MSJC011` | 目标（角色）+ 地区目标 | 全 cat-1 ⇒ 保持场上点选 |
| `MSJC015` | 地区目标 + 从手牌放置进场 | 全 cat-1 ⇒ 保持场上点选 |
| `JC065` | 目标地区（＋打出目标地区）+ 该地区内玩家自选牺牲 | 全 cat-1 ⇒ 保持场上点选（顺序两步，不合并窗） |
| `JC020 / JC063 / JC069 / MSJC014` | 同一效果声明多个可选目标大类（同一时刻只取其一） | 各自按候选判定 |
| `JC021 / JC062` | 多目标槽 | 全 cat-1 ⇒ 通道 B |
| `JC119` | 封印取手牌 + 封印回手后免费打出 | 全 cat-1 ⇒ 场上点手牌 |
| `JC031 / JC034 / JC038 / MSJC012` | 玩家目标 + 弃牌（自选：由弃牌者自己从其手牌选） | 玩家目标＝非卡选择；弃牌＝cat-1 ⇒ 场上点手牌 |
| `JC043` | 本体 + `SpecAlt`（两条 OR 目标声明） | 全附属 ⇒ 按 C 组 |
| `JC114` | 玩家目标 + 检视 + 从对手手牌指定弃牌 | 只有弃牌进选择窗 |
| `JC111 / JC057` | 玩家目标 + 检视（展示窗） | 不涉及选卡窗 |

### 8.7 D 组：只有非卡选择（7 张）——不涉及本次改动

| 卡 | 选择点 |
|---|---|
| `JC031 JC034 JC038 JC057 JC111 JC128 MSJC012` | **玩家目标**（`TargetKind.Player` ⇒ `PromptPlayerTarget`）；其中 `JC031/JC034/JC038/MSJC012` 另有"弃牌（自选）"＝ cat-1 |
| `JC057 JC062 JC111 JC114` | **检视**（`ActionVerb.Inspect` ⇒ 展示窗，只有一个"确认"） |
| `JC104` | **预测 X**（`ActionVerb.Predict` ⇒ 置顶/置底窗） |

### 8.8 其余非选牌型决策（列出以证明无遗漏）

| 类型 | 卡 | 入口 |
|---|---|---|
| 是/否二选一（分支） | `JC031`（`AskBranch`） | `PromptNumberInput(0/1)` |
| 支付否则牺牲 | `JC064` | `PromptPayOrSacrifice` |
| 准备维护：支付 / 横置 | `JC068` | `PromptPayOrTap` |
| 贿赂：移除标志 / 牺牲 | `JC128` | `PromptTwoChoice` |
| 可选牺牲自身 | `JC114` | `PromptNumberInput(0/1)` |
| 模式选择（选择一项） | `JC063`、`JC110` | `PromptEffectMode` → `Modes` |
| 遏制分配 | 由 `CurbRule` 触发 | `PromptCurbMarks` |
| 群体动作 / 纯持续 / 关键字类 | 共 40 张（如 `JC011 JC052 JC098 JC103 JC112` 等；`NeedsSelection=false` 或只有持续效果） | 无任何选择点 |

---

## 九、实施批次与验证

### 批次 1（需求 ① ＋ 地区高亮 ＋ 分类器）
1. 新增 `CardPickChannel`（**逐卡实例**判定 cat-1/cat-2；纯静态）
2. 新增 `CardZoneLabel`（区域标签：玩家归属 + 地区编号 + 宿主归属；玩家名走 `playerNameOf` 扩展点，见 5.2）
3. `CardDragController.PickCandidateCardUidAt(globalPos, 候选UID列表)` + `TryBeginDragAt` 的"选目标期间不起手"守卫
4. `CouncilCardUI.SetSelectableHighlight` + `Arena.Events.OnTargetSelectionChangedHandler` 补刷秘社高亮
5. 协调器注入新命中委托，并按当前模式分流（提示窗⇒提交 / 选择窗⇒转发选中）
6. `AreaCardUI.SetSelectableHighlight` + `AreaSlot.SetHighlight` 转发（4.3）
7. 验证：
   - 以"封印于地区"为目标 → 封印卡黄框 + 左键即选
   - 弃自己手牌 → 手牌黄框 + 左键即选，且**不触发拖拽**
   - 选地区目标 → 地区卡黄框 + 两侧淡黄（当前完全不可见）
   - 打出"目标＝本方秘社"的卡（`JC009 / JC082 / JC115`）→ 秘社黄框 + 左键即选

### 批次 2（需求 ②）
1. 新增 `CardPickWindow.cs`（卡面网格 + 每卡区域标签（用 `CardZoneLabel`）+ 确认/取消 + `SelectByUid`）
2. 分类接到全部取目标入口：`Arena.PromptPickCard`、`PromptPlayTarget`、`PromptDiscard`、
   `TriggerInteractionCoordinator.RequestTargetSelection` / `RequestActivateTargetSelection`、`Arena.PromptMultiTargets`（3.4 表）
3. 选择窗 ↔ 场上点击联动（4.5）
4. 验证：
   - 检索（全 cat-2）→ 选择窗，每张标 `你的牌库` / `玩家N的牌库`
   - 弃自己手牌（全 cat-1）→ 场上点手牌
   - `JC134`（牺牲资产）→ 选择窗，标签 `你的资产区` / `玩家N的资产区`（**资产无条件 cat-2**）
   - `JC092 / JC133` → 两个选择窗（先选地区 `地区{N}（{地区名}）`、后选墓地 `你的墓地`）
   - `JC005 / JC013 / JC055 / JC043 / JC135 / MSJC013 / JC113` → 场上有"结附于卡"的附属时走选择窗；只有"结附于地区"的附属时走场上点选
   - **标签专项**（5.2.4 逐条走一遍）：结附于地区 → `附属于玩家N地区N`；结附于角色 → `附属于「{宿主卡名}」`；
     结附/封印于秘社 → `附属于玩家N的秘社` / `封印于玩家N的秘社`；地区目标 → `地区{N}（{地区名}）`（**无玩家名**）；
     争夺区双方角色 → 各自的 `玩家N地区N` 不同；暗藏者 → 带 `（暗藏）` 后缀
   - 选择窗模式下点场上高亮的卡 ⇒ 窗内对应条目被选中，**仍需点确认**

### 批次 3（需求 ③ + ④）
1. C1–C4 四处补取消出口
2. `allowCancel` 如实映射到两种通道的取消按钮
3. 【乙】组（B1–B15）保持不可取消，不动

### 怎么验

- **编译**：`.workbuddy/tools/build-check.sh`（只编译）
- **跑测**：`.workbuddy/tools/build-and-test.sh <日志名>`（headless 跑 `Scenes/TestsRunner.tscn`；`SKIP_TESTS=1` 只编译）
  - agent shell 里 `dotnet build` 因 NuGet 机器级路径解析失败不可用，走 Roslyn 直编绕行（见项目记忆）；用户自己的会话里正常。
  - 大厅/会话类测试在受限环境必然失败（需真实端口 + 双客户端），与本改动无关——先备份 DLL 对比基线。
- **重点回归**：`P3CounterTargetTests`（激活取目标）、`P2SearchCardsTests`（检索）、`P2DiscardTests`（弃牌）、`P2SealSearchShuffleTests`（封印）、`EffectStackTests`（入堆叠）、`TriggerWindowBatchTests`（触发窗口）
- **UI 手测**：批次 1 / 批次 2 的验证行 + 第八章各组的抽样卡

### 实施纪律

第七章【乙】组与第八章的清单是**按声明与调用点机械清点**的结果，**可能不全**（JC 范围内仍有效果不走同一套方法）。
实施过程中若遇到清单里没有的"玩家做选择"场景（尤其不确定能否取消的）⇒ **先停下来确认，不自行拍板**。

---

## 十、明确不做的事

- ❌ 不改任何候选枚举 / 合法性判定（`GetValidTargetUidsForTrigger`、`IsLegalActivateTarget`、`IsLegalPlayTarget`、`TargetZonePool`、`TargetValidity`）
- ❌ 不改结算顺序 / 挂起-续算机制（`SuspendChainForInput` / `ContinuePendingChain` / `_pendingResolve`）
- ❌ 不改原子锁与"决策窗持锁"配对纪律（`LockChoiceWindow` / `UnlockChoiceWindow`，`Arena.cs:228/237`）
- ❌ 不改触发排序窗
- ❌ 不改 `PickAreaCardAt`（拖拽落点语义）
- ❌ 不改任何 `ChoiceRequest` / `ChoiceResult` 协议字段
- ❌ 不改联机侧（`ArenaOl` / `ChoicePromptPanel`）——留联机批次

---

## 十一、跨方案接口约定（与《UI美化与空间重排方案.md》）

> **执行顺序已定**：本方案先做（批次 1 → 2 → 3），再执行《美化》。下列接口必须在本方案实现时预留，否则《美化》落地后本方案的命中 / 联动要返工。

| # | 接口 | 约定 |
|---|---|---|
| 1 | **命中取视觉矩形** | 命中一律用 `CardUI.GetVisualGlobalRect()`（`CardUI.cs:564`）／`CouncilCardUI.GetVisualGlobalRect()`（`:120`），**不取布局位置**。《美化》决策 5 的手牌 hover 上浮会改变视觉位置 ⇒ 该上浮**必须**让 `GetVisualGlobalRect()` 反映最终视觉位置（最简＝改 `CardUI.Position`）；否则手牌命中会在上浮时点偏。 |
| 2 | **高亮 / 命中范围** | 一律 ＝ **候选 ∩ 有场上 UI**。**不得**写成"cat-2 候选（资产 / 结附·封印于卡）也会高亮"——《美化》折叠 / 层叠后它们可能没有完整场上 UI。cat-1 由《美化》红线保证"不折叠"，故"全 cat-1 ⇒ 场上必可点"始终成立。 |
| 3 | **`SelectByUid` 出口** | `CardPickWindow.SelectByUid(uid)` 必须**公开**，且对"该 uid 当前没有场上 UI（卡正折叠 / 层叠中）或尚未在窗内可见"也要生效（滚动到该项 + 置选中）。它是《美化》hover 展开面板点击的**唯一出口**（面板成员全部属 cat-2，见 4.5）。 |
| 4 | **文件段 / 组件划分** | `AreaSlot.cs`：高亮段（`SetHighlight` / `SetSelected`）归本方案，几何段（`UpdateLayout` / `_Ready` 尺寸）归《美化》（其 8.6）。`CardHoverPreview` 扩成"列表展开模式"时不得破坏 `CardPickWindow` 的既有用法（其 8.7）。 |

> 《美化》的 hover 展开面板（角标展开 / 资产·分数层叠展开 / 对手手牌展开）**成员全部属 cat-2**，故"面板可点"只需连到选择窗，不产生新的 cat-1 通道；其点击行为与 4.5 的场上点击联动**完全等价**（置选中、不提交）。
