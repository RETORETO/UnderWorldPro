# PLAN.md · 联机计划总集

> **当前轮（第 3 版）计划见文末《十、第 3 版计划（当前轮）》。** 其下《一~九》为**上一轮（第 2 版）已实施并验证**的存档记录，保留作为参照与格式模板。

---

# 联机效果系统接入 · 四项遗留缺口修复计划（第 2 版 · 已确认，待批准实施）

> 状态：**已实施并验证**。四项裁决：G1 复刻单机「拖拽箭头激活」（**不做无目标增强**，严格 1:1）；G2 允许核心静态化；G3 允许快照协议扩展；G4 服务端校验 + 客户端可见提示。
> 改动文件与验证结果见文末《九、实施记录》。
> 约束：不修改单机模式现有行为；G2 涉及的核心共享文件仅做「行为等价」的纯函数静态化。

---

## 一、缺口与决策总览

| 编号 | 缺口 | 已确认方案 |
|---|---|---|
| G1 | 角色卡 CharacterActivated 联机无触发入口 | **复刻单机「拖拽箭头激活」**（箭头 + 合法目标高亮 + 落点发动） |
| G2 | 联机无「可发动」高亮 | **允许核心改造**：`EffectEngine.IsEffectActiveInZone / GetDefaultScope` 静态化（行为等价），客户端复用 `ActivateEligibility.CanActivate` |
| G3 | JC012① 费用修正在客户端不显示 | **允许扩展快照协议**：`GameSnapshot.CostMods` + `CardUI.SetExternalCostMod` |
| G4 | 服务端不复查激活目标合法性 | 联机桥校验 + **客户端可见提示** |

---

## 二、G1 角色卡激活：复刻单机「拖拽箭头激活」

### 2.1 单机实现（复刻基准，含源码位置）

- **起手**：`Arena._Input` 左键按下 → `CardDragController.TryBeginActivateDragAt(globalPos)`（约 L440-518）：
  - 原子锁校验（`TurnManager.IsAtomicLocked`）；
  - 在 `AreaCardUIs` 中**逆序**命中（= 最上层）且 `CanActivate == true` 的场上卡；附属区（`AttachedCardUIs`）也命中，但 `IsAttached` 时直接返回 false（附属走双击链路）；
  - 记录 `_activatingCard`，在 `ArrowLayer` 下实例化 `StackArrow`（`ZIndex = 4095`），并按 `Arena.IsLegalActivateTargetCached` 高亮合法目标。
- **拖动中**：`Arena._Process`（约 L830）每帧 `UpdateActivateDrag(鼠标位置)`：箭头 `from = 源卡视觉中心 → to = 鼠标`；按缓存增量刷新高亮；记录当前 hover 是否为合法目标。
- **放手**：`Arena._Input` 左键抬起 → `EndActivateDrag(globalPos)`：取 `GetActivatingAbility(source)` = 源卡**第一个** `CharacterActivated / AttachmentActivated` 效果；落点为合法目标 → `PlayerActionService.ActivateAbility(source, ability, target)`；否则取消并清高亮。
- **重要事实**：单机该路径**必须落到合法目标才发动**（`EndActivateDrag` 仅在 `target != null && legal` 时发动）→ 「无目标」的角色激活效果经拖拽在单机也无法发动。

### 2.2 联机实现方案（全部在 `ArenaOl.cs`，不动单机）

**状态字段**：
- `CardUI _activateDragSource`；`Script.Data.Effects.EffectData _activateDragAbility`；
- `StackArrow _activateArrow`；`Control _arrowLayer`；
- `List<CardUI> _activateHighlighted`；`HashSet<string> _activateLegalUids`。

**箭头层**：`ArenaOl._Ready` 动态创建（不改 `ArenaOl.tscn`）：`Control { MouseFilter = Ignore, ZIndex = 4095, ZAsRelative = false }` + `SetAnchorsAndOffsetsPreset(FullRect)`，命名 `ArrowLayer`；箭头场景复用 `res://Scenes/UI/StackArrow.tscn`（只读复用，不改其类）。

**起手 `TryBeginOnlineActivateDragAt(globalPos)`**：
1. 前置：`!_activationSelectionBusy && !_activationTargetActive`；
2. 命中：复用/新增 `PickFieldCardAt(globalPos)`（按 ZIndex 取最上层），要求 `CurrentZone == AreaZone`、`!IsCovert`、`!IsAttached`、`ControllerId == _localPlayerId`；
3. 可发动判定：`ActivateEligibility.CanActivate(inst, launcher.ClientTurnManager, launcher.ClientPlayerManager, launcher.ClientCardManager)`（与单机同源，依赖 G2）；
4. 能力选取：`GetFirstOnlineActivateAbility(inst)` = 第一个 `CharacterActivated / AttachmentActivated`（**与单机一致**）；
5. 合法目标：`ComputeLegalCardTargetUids(inst, ability)`（已有方法）→ 缓存 `_activateLegalUids`；
6. 高亮：对合法 UID 调 `CardUI.SetTargetHighlight(true)` 并记入 `_activateHighlighted`；创建箭头并 `SetPoints(源卡中心, 鼠标)`；
7. 返回 true（调用方 `AcceptEvent()`，阻止同一次按下再触发手牌拖拽）。

**拖动中**：`ArenaOl._Process` 中若 drag active → `_activateArrow.SetPoints(源卡视觉中心, GetGlobalMousePosition())`；悬停卡变化时刷新高亮（仅 `_activateLegalUids` 内高亮）。

**放手 `EndOnlineActivateDrag(globalPos)`**：
1. 取落点卡（`PickFieldCardAt`）；若其 UID ∈ `_activateLegalUids` → 新增 `SendActivationWithTarget(inst, ability, targetUid)` 直接上行 `ActivateAbility(sourceUid, effectId, targetUid)`；
2. 否则 → 取消（清高亮、释放箭头），与单机一致；
3. 清理：`_activateHighlighted` 全部 `SetTargetHighlight(false)`、`QueueFree` 箭头、清空状态。

**输入路由（`ArenaOl._Input` 调整）**：
- 左键按下：若 `_activationTargetActive` → 保持现有「场上选目标」分支；否则先 `TryBeginOnlineActivateDragAt`，成功即 `AcceptEvent()` 返回；失败才让事件继续（手牌拖拽由 `CardUI` 自行处理）。
- 左键抬起：若 drag active → `EndOnlineActivateDrag` + `AcceptEvent()`。
- 拖拽期间同样纳入 `_activationSelectionBusy` 语义（禁止让过 / 结束回合 / 重复起手）。

### 2.3 交互冲突与边界

- 与**附属双击**（`AttachmentDoubleClicked`）不冲突：起手条件排除 `IsAttached`；
- 与**暗藏现身双击**不冲突：起手条件排除 `IsCovert`；
- 与**手牌拖拽**不冲突：`ArenaOl._Input` 先于 `CardUI.GuiInput` 收到左键按下，起手成功即 `AcceptEvent()`；
- 与**效果列表/场上选目标**不冲突：`_activationSelectionBusy` / `_activationTargetActive` 双重前置拦截；
- **快照重建**：按既定结论（我方行动权期间对手无法发起改变局面的意图），拖拽期间不应收到快照；仍加一条防御——每帧若 `_activateDragSource` 已 `!IsInstanceValid` 则立即取消拖拽（避免引用失效节点）。

### 2.4 无目标能力：不增强（已决）

- 单机「必须落到合法目标才发动」意味着**无目标**的角色激活效果经拖拽无法发动。
- **决策（用户）**：不做增强，保持与单机 1:1。（JC012 的激活方式为临时形态，后续会调整，本轮仅用于验证行为逻辑。）
- 实现后果：落点非合法目标 → 取消、不上行意图；无目标能力不会被拖拽发动。

### 2.5 验证

- 己方争夺区可发动角色卡：按下 → 出现箭头与合法目标高亮；拖到合法目标放手 → 服务端结算并入堆叠；拖到非法区域放手 → 取消、无意图上行。
- 需横置代价但角色已横置 / 费用不足 / 无优先权 → 起手即失败（不高亮、不画箭头）。
- 附属卡按下不进入激活拖拽（仍走双击弹效果列表）；暗藏者按下不进入（仍走双击现身）。

---

## 三、G2 联机「可发动」高亮（已批准核心静态化）

### 3.1 关键调研结论（客户端已具备全部依赖）

- `ClientTurnManager`：`ApplySnapshot` 已恢复 `TurnCount / CurrentPhase / FirstPlayerId / PriorityPlayerId`，且已 `Initialize(ClientDeck, ClientPlayer, ClientCard)` + `SetZoneManager` + `SetStackManager` → `PriorityPlayerId`、`IsAtomicLocked`（客户端恒 false）、`CanEnterBasicActionMask` 可用；
- `ClientPlayerManager`：已 `SetZoneManager(ClientZone)` → `GetAvailableMana`（资产区未横置卡派生）可用；
- `ClientCardManager`：`GetAreaCards()` 走投影 ZoneManager，可用；
- **唯一阻塞**：`ActivateEligibility.CanActivate` 调 `EffectEngine.Instance.IsEffectActiveInZone(...)`，客户端 `Instance == null` → NRE。

### 3.2 改动

1. `Script/Manager/EffectEngine.cs`：`IsEffectActiveInZone(EffectData, CardInstance)` 与 `GetDefaultScope(CardInstance)` 改为 `static`（二者不使用实例字段），同步更新类内调用点；`ActivateEligibility` 改调静态版本。**逻辑零改动**。
2. `Script/Net/Godot/NetworkMatchLauncher.cs`：新增 `public CardManager ClientCardManager => _clientCard;`。
3. `Script/UI/ArenaOl.cs`：新增 `RefreshActivateHighlights()`——遍历 `ClientZoneManager.GetZoneCards(AreaZone, 0)` + `(AttachedZone, 0)`，对有 UI 的实例调 `ActivateEligibility.CanActivate(...)` → `ui.SetActivatableHighlight(can)`；在 `ApplySnapshot` 末尾调用。G1 的拖拽起手也复用同一判定（与单机 `CanActivate` 门禁一致）。

### 3.3 验证

- 有优先权 + 费用足够 + 有合法目标 → 高亮；让过 / 快照回显后高亮消失；
- 单机高亮行为不变（静态化不改逻辑，跑既有测试为准）。

---

## 四、G3 客户端费用修正显示（已批准协议扩展）

1. `EffectEngine` 新增只读导出 `public List<Script.Net.CostModSnap> GetActiveCostMods()`（导出 `_timedModStack` 中 `Kind == "CostMod"` 的 `PlayerId / Amount / FilterText / ConsumeOnce / ExpireTurn`）；现有 `GetCostModification` 语义不变。
2. `Script/Net/GameSnapshot.cs` 新增 DTO `CostModSnap` 与 `GameSnapshot.CostMods`（`List<CostModSnap>`）；`SnapshotSerializer` 按字段自动包含，向后兼容。
3. `GameInitializer.BuildSnapshot` 填 `snap.CostMods = EffectEngine?.GetActiveCostMods() ?? snap.CostMods;`。
4. `NetworkMatchLauncher.ApplyClientSnapshot` 保存并公开 `ClientCostMods`。
5. `CardUI` 新增 additive `public void SetExternalCostMod(int? mod)`（默认 `null` = 未接管）；`RefreshCostReduction` 在已接管时用外部值并跳过 `EffectEngine.Instance` 分支 → 单机不变。
6. `ArenaOl` 在各重建分支 `ui.Setup(inst)` 后调用 `ui.SetExternalCostMod(ComputeClientCostMod(inst))`；匹配规则与服务端一致（`FilterText` 空 = 全匹配；卡名含 `FilterText` 或 `Organization == FilterText` → 累加 `Amount`，下限 0）。

验证：发动 JC012① 后手牌相关牌显示减 1 且绿色 → 打出一张后快照回显恢复原价 → 回合结束未用消失；单机显示不变。

---

## 五、G4 服务端目标校验 + 客户端提示（已批准，需提示）

### 5.1 服务端校验（联机桥，不动核心）

`Script/Net/Godot/GodotIntentRouter.cs` 的 `ActivateAbility`：解析 `src / data / tgt` 后追加——
1. `data.Target != null && data.Target.NeedsSelection` 时 `tgt` 必须非空；
2. 必须 `ActivateEligibility.IsLegalActivateTarget(src, data, tgt, _cardManager)`；
3. 否则 `throw new InvalidOperationException("激活目标非法：...")` → `IntentHub` 回 `IntentResult { Success=false, Error=... }`，服务端状态不变。
（单机不经 `GodotIntentRouter`，行为不变。）

### 5.2 客户端可见提示（新增）

现状：`NetworkMatchLauncher.OnClientMessage` 对 `IntentResult` 只解析不处理 → 拒绝无任何反馈。

1. `NetworkMatchLauncher`：解析 `IntentResult` 后，若 `Success == false` → 触发新事件 `public event Action<IntentResult> OnClientIntentRejected;`（同时保留 `GD.PrintErr` 日志）。
2. 新增联机专用 `Script/UI/OnlineNoticeWindow.cs`（代码构建 `Window`：标题 + 文本 + 单个「确定」按钮；**不连接 `FocusExited`**，点窗外不关闭），与 `OnlineActivationChoiceWindow` 同风格。
3. `ArenaOl.AttachLauncher` 订阅 `OnClientIntentRejected` → 弹出提示窗，文案形如「操作被服务端拒绝：{Error}」；`DetachLauncher` 反订阅。
4. 交互：提示窗弹出期间计入本地模态（`_activationSelectionBusy` 语义），点「确定」关闭。

### 5.3 验证

- 正常激活（合法目标）不受影响；
- 构造非法目标（暗藏者 / 跨地区 / 错误类别）→ 服务端返回 `Success=false`、客户端弹出提示且日志含原因；
- 单机激活路径行为不变。

---

## 六、实施顺序与合并验证

顺序：G2（静态化 + 高亮，是 G1 起手门禁的基础）→ G1（拖拽箭头激活）→ G4（服务端校验 + 提示窗）→ G3（费用修正协议与显示）。

1. `dotnet build UWPro.csproj`：0 警告 0 错误。
2. headless 全量 `TestsRunner`：除既有 `NetP1Batch1Tests` 2 例失败外不得新增失败；建议新增用例：G3 快照 `CostMods` 往返一致、G4 非法目标被拒（`Success=false`）。
3. 三进程手动冒烟（`启动联机三进程测试.bat`）：
   - 可发动角色高亮出现 / 让过后消失；
   - 按下角色卡出现箭头与合法目标高亮，拖到合法目标放手发动成功；拖到空地放手则按 §2.4 分支处理；
   - 附属卡仍走双击弹效果列表，不受拖拽影响；
   - JC012① 发动后客户端手牌减费绿字、打出后恢复；
   - 非法目标被拒时客户端出现提示窗且点窗外不关闭。

---

## 七、待确认

- 无（四项决策已定；§2.4 采纳「不增强」）。

---

## 八、风险与注意

- G1 的拖拽期间若异常收到快照导致节点重建，按 §2.3 防御性取消；后续若要更强鲁棒性可改为「拖拽期间挂起快照应用」。
- G2 客户端 `IsAtomicLocked` 恒 false，高亮可能略宽松（服务端仍会拒绝，属可接受展示误差）。
- G3 仅展示、不参与结算；未来若做客户端预校验必须改读快照值。
- G4 的校验放在联机桥而非 `EffectEngine`，避免改变单机既有行为与测试预期。
- 除 G2 的静态化（行为等价）外，单机文件应保持零改动：`Arena`、`CardDragController`、`ActivationChoiceWindow`、`CouncilActivateWindow` 等。

---

## 九、实施记录（已完成）

### 9.1 G2 可发动高亮

- `Script/Manager/EffectEngine.cs`：`IsEffectActiveInZone` / `GetDefaultScope` 改为 **static**（纯函数，逻辑零改动）；新增只读导出 `GetActiveCostMods()`（G3 用）。
- `Script/Manager/ActivateEligibility.cs`：生效区判定改调静态版（客户端无 `EffectEngine` 实例也能复用）。
- `Script/Manager/TriggerRegistry.cs`：改调静态版，并**保留原有 `EE == null` 短路语义**（行为不变）。
- `Script/Net/Godot/NetworkMatchLauncher.cs`：新增 `ClientCardManager` 属性。
- `Script/UI/ArenaOl.cs`：新增 `RefreshActivateHighlights()`，在 `ApplySnapshot` 末尾对争夺区/附属区卡调 `ActivateEligibility.CanActivate(inst, ClientTurnManager, ClientPlayerManager, ClientCardManager)` → `CardUI.SetActivatableHighlight`。
- 测试文件语法同步：`Script/Tests/JC012_015_030_035_060Tests.cs`、`Script/Tests/JC020Tests.cs`（仅调用语法，断言不变）。

### 9.2 G1 角色卡拖拽箭头激活（复刻单机，不做无目标增强）

- `Script/UI/ArenaOl.cs`：新增拖拽状态字段、动态 `ArrowLayer`、`_Process` 每帧维护箭头与高亮、`_Input` 起手/放手路由；
  新增 `TryBeginOnlineActivateDragAt`（门禁 = `ActivateEligibility.CanActivate`，与单机同源）、`UpdateOnlineActivateDrag`、`EndOnlineActivateDrag`（**落点为合法目标才上行**，否则取消）、`CancelOnlineActivateDrag`、`GetFirstOnlineActivateAbility`（首能力，同单机）、`PickFieldCardAt`、`PickLegalTargetAt`。
- 冲突规避：排除 `IsCovert`（现身双击）/ `IsAttached`（附属双击）；起手成功即 `AcceptEvent()` 抢在 `CardUI` 手牌拖拽之前；拖拽期间置 `_activationSelectionBusy`。
- 箭头场景只读复用 `res://Scenes/UI/StackArrow.tscn`（未改其类）。

### 9.3 G3 客户端费用修正显示

- `Script/Net/GameSnapshot.cs`：新增 `CostModSnap` 与 `GameSnapshot.CostMods`。
- `Script/Manager/EffectEngine.cs`：`GetActiveCostMods()` 导出 `_timedModStack` 中 `Kind==CostMod` 条目。
- `Script/Manager/GameInitializer.cs`：`BuildSnapshot` 填 `snap.CostMods`。
- `Script/Net/Godot/NetworkMatchLauncher.cs`：`ApplyClientSnapshot` 保存并公开 `ClientCostMods`。
- `Script/UI/CardUI.cs`：新增 additive `SetExternalCostMod(int?)`；`RefreshCostReduction` 在「已接管」时用外部值，否则走原 `EffectEngine.Instance` 逻辑（**单机不变**）。
- `Script/UI/ArenaOl.cs`：`ComputeClientCostMod()` + 手牌/资产/争夺区/附属/分数区重建处注入。

### 9.4 G4 服务端目标校验 + 客户端提示

- `Script/Net/Godot/GodotIntentRouter.cs`：`ActivateAbility` 复查目标（需目标时空值拒绝；非法目标经 `ActivateEligibility.IsLegalActivateTarget` 拒绝），抛异常 → `IntentResult{Success=false,Error}`。
- `Script/Net/Godot/NetworkMatchLauncher.cs`：新增 `OnClientIntentRejected` 事件，`IntentResult.Success=false` 时打 `GD.PrintErr` 并触发事件。
- 新增 `Script/UI/OnlineNoticeWindow.cs`（点窗外不关闭，仅「确定」关闭）；`ArenaOl` 订阅事件弹提示，并以 `_noticeWindowOpen` 纳入本地模态守卫。

### 9.5 测试与验证

- 新增 `Script/Tests/OnlineEffectChainTests.cs`（已注册进 `TestsRunner`）：
  - `TestClientActivateEligibilityRuns`（G2：客户端投影调用 `CanActivate` 不抛异常）；
  - `TestCostModsSnapshotRoundTrip`（G3：费用修正导出→序列化→反序列化一致）；
  - `TestServerRejectsIllegalActivationTarget`（G4：暗藏者目标/缺目标被拒，合法角色目标通过）。
- 结果：`dotnet build` **0 警告 0 错误**；headless 全量 `TestsRunner` **仅剩既有 `NetP1Batch1Tests` 2 例失败**（与本轮无关），新增套件 3/3 通过。
- 待人工验证（headless 无法覆盖 UI）：三进程下高亮出现/消失、角色卡拖拽箭头与落点发动、非法目标提示窗、JC012① 减费绿字。

---

# 十、第 3 版计划（当前轮）：三处决策面板对齐单机 + 蒙版可拖拽预检

> 用户裁决（2026-09-05）：① 「**全部三处一起对齐**」决策面板；② 「**加客户端预检**」蒙版可拖拽资格。
> 约束不变：**不改单机 UI/逻辑**；共享的规则层只做**行为等价**的抽取；只动联机侧。
> 状态：**待用户确认 5 个开放问题后实施**（见 §10.6）。→ **已确认并实施，结论见本节开头的状态行。**

> 状态：**已实施并验证**（2026-09-05）。5 个开放问题的确认结论：① 静态纯函数落点＝**新建 `DeployCovertEligibility` + TurnManager 委托**；② 排序回包＝**`SelectedIndices` 有序下标**；③ 支付置灰＝**服务端下发 `PayAllowed`**；④ **接受**逐次入堆叠的多轮请求-响应；⑤ **纳入**地区目标选目标。
> 实施结果：`dotnet build` 0/0；headless **34 套件全绿、0 失败、0 ERR**；新增 `NetChoicePanelTests` 6 例全绿。设计侧记录见 `联机C-S架构设计方案.md` §17、`C-S迁移进度与批次计划.md` §十七。

## 10.1 现状认知（本轮改动的依据，均已核对源码）

1. `ArenaOl.OnClientChoiceRequest` 先试 `TryOpenOnlineCombatWindow`（`CombatStrength`/`CombatInvestigation`/`TieBreak` 已用单机同款窗口），其余一律落到 `ChoicePromptPanel`（简化按钮列表）。
2. **待对齐的三类请求**（服务端实际会发送的种类，已 grep 全部 `RequestKind =` 站点）：
   | 请求种类 | 发送点 | 单机对应 UI |
   |---|---|---|
   | `SelectTriggerOrder` | `TurnManager.RunTriggerPhaseAsync`（联机触发排序） | `TriggerOrderWindow`（拖拽入堆叠/放弃区 + 全部/第一张入堆叠 + 完成排序） |
   | `PayOrTap` | `EffectEngine.ExecuteVerbAsync`（JC068 阿格斯IV「准备维护」） | `Arena.PromptPayOrTap` → `ActivationChoiceWindow`（`ForceChoice=true`，两项，费用不足置灰"支付"） |
   | `SelectTarget` | `TurnManager.EnsureTargetThenPushAsync`（触发选目标，含**地区目标**机动触发） | `TriggerInteractionCoordinator.RequestTargetSelection` → `TargetSelectionWindow` + 场上合法目标高亮（卡目标经 `CardManager.NotifyTargetSelectionChanged`；地区目标经 `AreaSlot.SetHighlight/IsTargetSelectable/OnAreaClicked`），**必须点合法目标、无取消** |
3. `ChoiceRequest.Kind.ForceChoice` 在生产代码中**从未被发送**（grep 仅命中 `ChoicePromptPanel` 的渲染分支与超时表）→ 「ForceChoice 对齐」= 上述三类窗口在 `ForceChoice` 布尔下的**取消按钮/点窗外关闭**行为对齐单机（单机这三处均为强制、不可取消）。
4. **关键事实（决定了必须改服务端）**：`RunTriggerPhaseAsync` 收到 `SelectTriggerOrder` 回包后**完全不使用顺序** —— 只要 `Cancelled==false`，就把该 owner 的**全部**触发按默认顺序逐个入栈。所以"对齐单机排序窗口"必须同时改**协议**与**服务端异步流程**（单机同步流程 `RunTriggerPhase` 不动）。
5. **关键事实（决定了预检可行）**：`TurnManager.CanDeployCovert(instance)` 实际只判两件事 —— `!instance.HasPublicKeyword`（模板固有【公开】或运行时授予）与"是角色卡"；**不含**阶段/行动权/次数/封锁（那些在动作 `DeployCovert` 内判）。而 `CardInstance.HasPublicKeyword` 的模板回退分支（`PlayerTemplate.HasInherentKeyword("公开")`）在客户端可用 → 客户端可**复用同一条规则**做拖起预检，不是复制规则。

## 10.2 P1 — SelectTriggerOrder 对齐（协议 + 服务端流程 + 新窗口）

**协议**（`Script/Net/IInputGateway.cs`，纯加法，旧端可忽略）
- `ChoiceRequest` 新增：`OptionForced: List<bool>`（与 `OptionUids` 同序：强制触发不可放弃）、`OrderContextId: string`（同一 owner 的排序窗口上下文；客户端据此**复用同一窗口**而非新开）。
- `ChoiceResult` 新增：`SelectedIndices: List<int>`（**本次要入堆叠的条目下标序列**，指向本次请求的 `OptionUids`）。
- 兼容：老客户端不回 `SelectedIndices` → 服务端退化为「全部、默认顺序」（与今日行为一致）。

**服务端**（`Script/Manager/TurnManager.cs`，**仅** `RunTriggerPhaseAsync`；单机 `RunTriggerPhase` 零改动）
- 每个 owner 内改为**循环**：`remaining` 非空 → 发请求（`OptionUids`=来源 UID、`OptionForced`=`EffectDataRef.Forced`、`OrderContextId`=该 owner 本次窗口 id）→ 依回包：
  - `Cancelled==true` 且 `remaining` 含强制触发 → **忽略取消并重发**（等价单机「Done 按钮在入堆叠区非空时禁用」）；
  - `Cancelled==true` 且无强制触发 → 放弃剩余，该 owner 结束；
  - 否则按 `SelectedIndices` 顺序逐个 `await EnsureTargetThenPushAsync` 并入栈，随后从 `remaining` 移除；下标越界/重复 → 忽略该项并打日志。
- 循环直到 `remaining` 空 → 与今日一致地进入下一个 owner。

**客户端新增**（不改单机）
- `Script/UI/OnlineTriggerOrderWindow.cs` + `Scenes/Net/OnlineTriggerOrderWindow.tscn`：复制单机 `TriggerOrderWindow.tscn` 的节点结构（**独立 scene uid**、不带 `unique_id` 属性），脚本为联机专用：
  - **只读复用** `TriggerEntryUI`（`Setup(name, text, mandatory, data=null, template=null)` 对后两参可空）与 `TriggerOrderRoot`（全程不读 `TriggerData`）→ 单机这两个文件**零改动**；
  - 同款三按钮 + 入堆叠区/放弃区拖拽；强制条目不可拖入放弃区（`TriggerOrderRoot` 内建）；
  - 提交：把"入堆叠区当前顺序"映射为 `SelectedIndices`；`OrderContextId` 变化则重开窗口，相同则按新 `OptionUids` 刷新条目并**保留窗口**（实现"逐次入堆叠 + 窗口不关"的单机手感）；
  - 「完成排序」= 放弃剩余（`Cancelled=true`），仅在入堆叠区为空时可点（同单机）。
- `ArenaOl`：`OnClientChoiceRequest` 增 `SelectTriggerOrder` 分支 → 打开/更新窗口（纳入本地模态 `_activationSelectionBusy`），提交回包。

## 10.3 P2 — PayOrTap 对齐

- 服务端 `Script/Manager/EffectEngine.cs`：`ExecuteVerbAsync` 的请求补 `PayAllowed = CanPayPrepareUpkeep(source, payAmount)`（**服务端权威**，客户端不重算规则）。
- 客户端新增 `Script/UI/OnlinePayOrTapWindow.cs`（联机专用，不与单机 `ActivationChoiceWindow` 争用）：
  - 选项1「支付{PayAmount}（横置1张资产区牌）」，`PayAllowed==false` 时置灰；选项2「横置{来源卡名}」（卡名由 `SourceUid` 解析）。
  - 强制语义：无取消按钮、点窗外不关闭（对齐单机 `Arena.PromptPayOrTap` 恒 `ForceChoice=true`）。
  - 回包 `ChoiceResult.Pay`。

## 10.4 P3 — SelectTarget 对齐（卡目标 + **地区目标**）

- `ArenaOl`：`Kind.SelectTarget` → 复用既有「场上点击选目标」状态机（`_activationLegalCardUids` + `_onlineTargetWindow`/`TargetSelectionWindow` + `OnTargetPicked`），新增 `_pendingChoiceRequest` 把选中结果回成 `ChoiceResult{SelectedUid}`（区别于激活/出牌的本地目标选择）。
- **新增地区目标支持**（当前联机只有卡目标高亮）：新增 `_activationLegalAreaUids`；用单机同款公开 API `AreaSlot.SetHighlight/IsTargetSelectable/OnAreaClicked`（§只读复用）+ `ArenaOl._Input` 增地区槽位点击路由（对齐单机"点高亮地区"）。
- 取消语义：单机选目标不可取消 → 联机不显示取消按钮；仅保留服务端超时兜底的 `Cancelled` 既有路径。

## 10.5 P4 — 蒙版可拖拽预检

- **单一真相源**：把 `TurnManager.CanDeployCovert(CardInstance)` 的判定体抽为**静态纯函数**（推荐：新增 `Script/Manager/DeployCovertEligibility.cs`，与既有 `ActivateEligibility` 同风格），`TurnManager.CanDeployCovert` 改为**委托调用**（表达式等价 ⇒ 单机行为不变，`DeployCovert` 动作内的阶段/次数/封锁校验不动）。
- `ArenaOl`：蒙版模式（`DeployCovert`）下拖起手牌前的资格判定调用**同一静态函数**（对齐单机 `CardDragController.IsDraggableUnderMask` → `TurnManager.CanDeployCovert`）；不满足则**不被拖起**（与单机一致，无额外提示）。
- 结果：非法卡不再"能拖起、等服务端拒绝"。

## 10.6 待用户确认的 5 个开放问题

1. **静态纯函数落点**：新建 `DeployCovertEligibility` 静态类 + `TurnManager` 委托（推荐）／或直接在 `TurnManager` 上加 `public static`？
2. **排序回包语义**：`SelectedIndices`（有序下标，推荐）／其它表达？（`SelectedUids` 在"同一来源卡多触发"时 UID 重复，无法表达顺序）
3. **PayOrTap 置灰依据**：服务端下发 `PayAllowed`（推荐，客户端零规则）／客户端用投影 `ClientPlayerManager.GetAvailableMana` 比较？
4. **逐次入堆叠的多轮往返**是否接受（单机「第一张进入堆叠」的等价实现 = 每按一次一次请求-响应；窗口保持打开）？
5. **地区目标选目标**（§10.4 新增能力）确认纳入本轮？

## 10.7 P5 — 测试与文档（实施时同步）

- 新增 `Script/Tests/NetChoicePanelTests.cs`（注册进 `TestsRunner`）：
  1. `SelectTriggerOrder` 请求/回包新字段往返（`OptionForced`/`OrderContextId`/`SelectedIndices`）；
  2. 服务端**按 `SelectedIndices` 入栈顺序**断言 + 「无强制则放弃剩余」「有强制则忽略取消」两条分支；
  3. `PayOrTap`：`PayAllowed` 往返 + `Pay=true/false` 落地（`ResolvePrepareUpkeep` 效果断言）；
  4. `SelectTarget` 带地区 UID 的请求往返（客户端仅展示）；
  5. 蒙版资格静态函数：角色卡通过、含【公开】拒绝、非角色卡拒绝（与 `DeployCovert` 拒绝条件一致）。
- 全量回归：`dotnet build` 0/0 + headless `TestsRunner` 全绿 0 fail 0 ERR。
- 文档：`联机C-S架构设计方案.md` §17、`C-S迁移进度与批次计划.md` §十七、`联机对战完整实现最终缺口清单.md` 第二十轮、`需求实现对照.md` banner。
- 待人工冒烟（headless 不覆盖 UI）：三类窗口的交互与回包、排序多轮往返、地区高亮点选、蒙版非法卡拖不起。

