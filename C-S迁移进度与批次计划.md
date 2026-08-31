# C/S 化迁移进度与批次计划

> 用途：防止上下文丢失。记录"分批迁移 + 每批三方验证"策略下，已完成/进行中/待做的迁移工作，以及关键设计决策。
> 最近更新：2026-09-01
>
> **📌 联机对战完整实现的最终缺口，见独立文档 → [`联机对战完整实现最终缺口清单.md`](./联机对战完整实现最终缺口清单.md)**（覆盖：四区域 UI 缺口、对抗整体、决策网关自动取消、剩余交互、规则完整度缺口与优先级）。

## 一、总目标

把 `UWPro` 从"Godot 单机 View 直驱 Model"架构迁移到 **C/S（客户端/服务器）网络化架构**，使游戏能跨进程联机；
且**单机运行路径与联网对战路径共用同一套核心规则逻辑**（降低维护成本，不写第二套规则）。

### 已落地地基（P0–P3，已完成，非本次迁移内容）
- **P0 进程边界**：`IInputGateway`（Local/Auto/Network 三实现）+ `GameEventBus` + `CardInstance` 纯数据化。
- **P1 MVC 分层**：`Arena` 退化为纯 View；`ZoneManager/CardManager/TurnManager` 剥离 UI 硬依赖。
- **P2 快照可序列化**：`ToSnapshot()` 聚合 + JSON 序列化。
- **P3 五模块**：A·GodotEnetTransport / B·SnapshotRenderDriver / C·IntentHub·PlayerIntent·GodotIntentRouter / D·EventToMessageMapper / E·ReconnectManager。
- **关键纪律**（需求实现对照.md §1001-1009 用户决策）：**移除 `ServerRuleEngine` 重写版**，只保留 `IIntentRouter` 接口，迁移真实 Manager 规则。即"两条输入路径收敛到同一批 Manager 方法"，不存在第二套逻辑。

## 二、需要迁移的 Manager 批次清单

| 批次 | Manager | 状态 |
|------|---------|------|
| **第一批** | `ZoneManager` + `CardManager` | ✅ 代码改造完成 + 三方验证已写 |
| **第二批** | `TurnManager`（回合状态投影）+ `StackManager`（堆叠投影）+ `PlayerManager`+`DeckManager` | ✅ 代码改造完成 + 端到端用例已写 |
| **第三批** | `WorldDeckManager`（世界牌库投影）+ `AreaManager`（地区/势力标记投影） | ✅ 代码改造完成 + 端到端用例已写 |
| **第四批** | `GameManager` 聚合层（整局装配/状态机权威态 + `BuildSnapshot`） | ✅ 已评估：GameInitializer 已承担对局装配 + BuildSnapshot 聚合，GameManager 为应用级场景状态机，均已合规 |
| **收尾** | 轻量层无 View 直驱红线检查 | ✅ 审计完成，零违规 |

## 三、第一批迁移（ZoneManager + CardManager）明细

### 改动内容（已完成）
1. **`CardInstance.cs`**
   - 补 `EnteredThisTurn` / `AbilityUsedThisTurn` 两个运行时字段（快照前漏的，属合理卡状态：本回合登场 / 本回合已用能力）。
   - 加内部方法 `SyncVersion(int)`（快照重建恢复版本号）、`SetUid(string)`（极端路径复用权威 UID）。
2. **`ZoneManager.cs`**
   - `ToSnapshot()` 的 `ToCardRef` 补全之前漏导出的 `EnteredThisTurn` / `VersionId` / `AbilityUsedThisTurn` 三字段。
   - 新增 `ApplySnapshot(List<ZoneCardRef>)`：从快照重建区域真相（清空各桶→逐卡归位），支持手牌/牌库/墓地/资产/争夺区/附属/封印/分数/秘社。
   - 辅助：`ResetBucket` / `Ensure` / `PlaceIntoBucket`（桶分配与 `MoveToX` 保持一致，但不触发移动事件/规则）。
3. **`CardManager.cs`**
   - 新增 `RebuildFromRef(ZoneCardRef)`：以 UID 为键恢复实例运行时字段（伤害/暗藏/领域/版本号等）；纯数据覆盖，不触发 `OnStateChanged`。
   - 抽出 `ResolveInstance(ZoneCardRef)`（`protected virtual`）：实例解析策略——
     1. `_allInstances[r.Uid]` 直接命中（正常路径，快照晚于卡组加载）；
     2. 兜底：遍历 `_allInstances`，复用"同 TemplateUid 且仍在 Deck 区域"的空闲实例，重置其 UID 为权威 UID（覆盖重连时 UID 错位竞态）；
     3. 仍失败返回 null，由调用方跳过该卡。

### 设计红线（已遵守）
- **不新增第二套逻辑**：重建直接复用既有 `CardInstance` 实例（UID 一致），不破坏单机路径。
- **对单机零影响**：`ApplySnapshot`/`RebuildFromRef` 仅联网权威重建/重连时调用，单机路径无任何调用点注入。
- **不引入模板库依赖死链**：极端补建走"同模板空闲实例复用"，避免 Godot 资源系统与纯托管侧差异导致的编译/逻辑漂移。

### 三方验证（已写入 `Script/Tests/NetP1Batch1Tests.cs`，编排于 `TestsRunner`）
1. **LoopbackTransport 端到端**（`TestLoopbackSnapshotRoundTrip`）：`ToSnapshot` → 经 Loopback 序列化往返 → `ApplySnapshot` 重建 → 断言桶分布/UID/运行时字段一致 + 本次补全字段不丢。
2. **单机回归**（`TestStandaloneSnapshotExportIntegrity`）：直接驱动 `ZoneManager` 移动 API → `ToSnapshot` → 断言导出字段完整（含本次补全 3 字段）+ 不破坏单机。
3. **GodotEnetTransport 真实联机**（`TestEnetRealTransportSnapshotRoundTrip`）：整局快照经 127.0.0.1 真实 socket 序列化往返 → `ApplySnapshot` 重建 → 断言含补全字段零丢失。

> 注：三项验证统一使用**正式 `SnapshotSerializer`**（含 `IncludeFields=true`，与真实快照下发链路一致），不再手写 JSON options，避免字段丢失。`BuildScenario` 注入真实 `DeckManager`（含 `SeededRandomSource`）覆盖牌库分支。`ZoneManager.RemoveFromAllZones` 对 `_deckManager` 已做 null 防御。

### 联机对局启动器（第二批前置接线，已完成，正式代码非临时）
- 新增 `Script/Net/Godot/NetworkMatchLauncher.cs`：把已落地的正式组件串成 **Server 权威 + Client 投影** 闭环：
  - 服务端：`GodotEnetTransport(Server)` + `IntentHub(GodotIntentRouter)` + `NetworkInputGateway`；
  - 客户端：独立投影 `ZoneManager` + 共享 `CardManager`（同进程，ResolveInstance 直接命中）+ 独立 `DeckManager`；
  - 消息协议：`IntentResult` 回执 + `SNAP:` + `SnapshotSerializer` 整局快照下行 → 客户端 `ApplySnapshot` 重建投影；
  - 驱动：继承 `Node`，`_Process` 每帧 `Poll` 两端 ENet。
- **这是正式联机入口**，非验证桩；把第一批的 `ApplySnapshot` 真正接入了联网链路（之前仅测试调用）。
- `ZoneManager.ApplySnapshot` 因此改为用实例字段 `_cardManager`（而非全局 static `CardManager.Instance`），使 Server/Client 投影可共存于单进程，且风格与 ZoneManager 其他方法一致。

## 四、"最小侵入方案 vs 完整方案"结论（重要，防返工）

**第一批做的是"完整方案的必要组成部分"，不是临时过渡，不会被推倒重写。**

- `ZoneCardRef` 字段映射、`ToSnapshot` 导出、`ApplySnapshot` 桶归位、`RebuildFromRef` 字段覆盖——这些都是**纯数据层能力**，无论后续走"Godot 侧桥接联网"还是"纯托管 Server 内核"，都必需且复用。
- **唯一可能随路线分化的点**：`ResolveInstance` 的"实例创建"分支。
  - Godot 侧：当前自愈兜底已足够。
  - 纯托管 Server 内核：需**重载 `ResolveInstance`**（改为从 `CardTemplateData` JSON 建实例），而 `RebuildFromRef` 的字段覆盖逻辑完全通用、不被推翻。
- `GodotIntentRouter` 已完整桥接 ZoneManager/CardManager（PlayCard/DrawCards/DeployCovert/... 落到 PlayerActionService→TurnManager/CardManager），无需修改。纯托管侧若要另写一套内核，也只需重写桥接层，不碰这两个 Manager 的真实逻辑。

## 五、第二批迁移（TurnManager/StackManager/PlayerManager/DeckManager）明细

### 设计决策
- 服务端 `TurnManager.ToSnapshot()` 早已聚合了全部回合字段（`TurnCount/FirstPlayerId/PriorityPlayerId/CurrentPhase`）+ `Players`（可用法力）+ `Stack`（堆叠）+ `Zones`。**第二批核心不是加导出，而是让客户端投影真正消费这些字段**——通过各 Manager 的 `ApplySnapshot` 状态恢复方法回写到客户端独立投影实例。
- **`AvailableMana` 是派生值**（`PlayerManager.GetAvailableMana` 从 `ZoneManager` 资产区实时计算），客户端重建资产区后自动正确，**无需**写回；`PlayerSnapshot.AvailableMana` 仅作校验/日志。这符合"客户端不重算规则、只按快照重建"的原则。
- **牌库剩余数**：牌库区卡已由 `ZoneCardRef(Deck)` → `ZoneManager.ApplySnapshot` → `PlaceIntoBucket` 填充 client 独立 `_clientDeck`，第一批已覆盖，无需额外写回。
- 客户端投影的 `PlayerManager/StackManager/TurnManager` 均为**独立实例**（不污染服务端权威），只做状态投影、不驱动规则。

### 改动内容（已完成，编译 0 错误 0 lint）
1. **`PlayerManager.cs`**：新增 `ApplySnapshot(List<PlayerSnapshot>)`——恢复 `_isGameOver` 游戏结束标记；先手标志由 launcher 在恢复 TurnManager 后统一 `SetFirstPlayer(firstPlayerId)` 派生，避免读不可靠字段。
2. **`StackManager.cs`**：新增 `ApplySnapshot(List<StackEntrySnap>)`——清空 `_stack` 后按 `SourceUid` 从共享 CardManager 解析卡片型条目 `SourceCard`，效果型保留 `EffectText` 重建；不触发 `OnStackChanged`（纯数据覆盖）。
3. **`TurnManager.cs`**：`TurnCount/CurrentPhase/FirstPlayerId/PriorityPlayerId` 四属性由 `private set` 改 `internal set`（同程序集投影恢复）；新增 `ApplySnapshot(GameSnapshot)` 恢复回合状态。
4. **`NetworkMatchLauncher.cs`**：
   - 客户端投影新增独立 `_clientPlayer/_clientStack/_clientTurn` 实例并装配依赖；
   - `OnClientMessage` 升级为 `ApplyClientSnapshot` 聚合调用：**区域 → 玩家 → 堆叠 → 回合**（顺序保证：先恢复区域，再恢复派生态状态），最后按 `_clientTurn.FirstPlayerId` 派生玩家先手标志；
   - 暴露 `ClientTurnManager/ClientStackManager/ClientPlayerManager` 只读访问器供断言。
5. **`NetP1Batch1MatchTests.cs`**：新增 `TestLocalLoopbackManagerStateProjection`——验证抽牌/让过多次快照后，客户端投影的阶段/先手/优先权/回合计数/堆叠条数/玩家先手标志均与服务端一致。

## 六、第三批迁移（WorldDeckManager/AreaManager）明细

### 设计决策
- 第三批目标是让"当前战场"（世界牌库 + 地区注册 + 势力标记）纳入投影闭环，供客户端 UI 渲染完整战场。
- **地区卡实例已在第一批 Zone 覆盖**（`ZoneCardRef(AreaZone)` → 客户端 `ZoneManager` 重建争夺区）。本批补的是**地区注册表 + 势力标记 + 世界牌堆**这三块 Zone 快照之外的持久状态。
- **CombatManager / EffectEngine 无需快照**：战斗结算为瞬时过程（`ResolveAreaCombat` 完成即结束）；效果/光环的运行时结果（Damage/Strength/Tapped）已落入 `ZoneCardRef` 卡片字段，客户端重建卡片后自动正确。持续修正栈等不进入跨快照持久状态。
- 世界牌堆：`WorldDeckManager._deck` 里"尚未抽出"的地区卡不在任何 Zone，客户端投影需单独重建。同进程共享 CardManager，按 UID 命中服务端已建地区实例（`GetInstance`）。

### 改动内容（已完成，编译 0 错误 0 lint）
1. **`GameSnapshot.cs`**：新增 `WorldDeckUids`（地区牌堆剩余卡 UID，自顶向下）+ `AreaMarks`（地区势力标记）+ `AreaMarkSnap` 类。
2. **`WorldDeckManager.cs`**：新增 `ToSnapshotUids()`（导出牌堆 UID）+ `ApplySnapshotUids()`（按 UID 命中共享实例重建牌堆）+ `SetCardManager()`（客户端投影注入）。
3. **`AreaManager.cs`**：新增 `ToSnapshotMarks()`（导出势力标记）+ `ApplySnapshot()`（从客户端 AreaZone 卡重建地区注册表 + 从快照恢复势力标记）。
4. **`GameInitializer.BuildSnapshot()`**：补世界牌堆 UID + 地区势力标记到快照。
5. **`NetworkMatchLauncher.cs`**：客户端投影新增独立 `_clientWorld`（WorldDeckManager，不加载本地牌库）+ `_clientArea`（AreaManager）；`ApplyClientSnapshot` 聚合顺序扩展为 **区域 → 世界牌库 → 玩家 → 堆叠 → 回合 → 地区**；暴露 `ClientWorldDeckManager/ClientAreaManager` 访问器。
6. **`NetP1Batch1MatchTests.cs`**：新增 `TestLocalLoopbackWorldDeckAndAreaProjection`——对服务端地区 `AddPowerMark` 后触发快照，断言客户端世界牌堆 UID 序列与地区势力标记均与服务端一致。

## 七、当前进度速查

- `CardInstance.cs` / `ZoneManager.cs` / `CardManager.cs`：lint 全绿，第一批改造完成。
- `PlayerManager.cs` / `StackManager.cs` / `TurnManager.cs`：lint 全绿，第二批投影回写完成。
- `WorldDeckManager.cs` / `AreaManager.cs` / `GameSnapshot.cs`：lint 全绿，第三批投影完成。
- `Script/Tests/NetP1Batch1Tests.cs`：三项验证（Loopback/单机/Enet，统一正式 `SnapshotSerializer`）已实现。
- `Script/Net/Godot/NetworkMatchLauncher.cs`：联机对局启动器（Server 权威 + Client 投影），第二批已聚合消费整局快照，正式代码非临时。
- `Script/Tests/NetP1Batch1MatchTests.cs`：联机端到端专项测试（手牌/Manager/世界牌库/地区投影一致），已编入 `TestsRunner`。
- `Script/Net/Godot/NetMatchDemo.cs`：即插即用演示节点（AutoRun 自动演示，挂 Arena 子节点运行即可观察闭环日志）。
- 详细场景设计见 `联机验证场景设计.md`。

## 八、收尾红线审计结论（C/S 迁移核心完成标志）

**结论：红线合规，零违规点。** Manager 层（含 `Script/Game/`）完全满足"C/S 迁移红线"——**单机与联机共用同一套规则、Model 不引用 View**：

- 无真实的 Godot UI 类型引用（`Control/Label/Button/Panel/Node2D/CanvasItem` 等均为注释文本，非代码）；
- 无 `GetNode<...>` / `new UI(...)` 创建或持有具体 UI 节点；
- 无 `using Script.UI` 依赖；
- 与 View 的所有交互均通过**抽象接口依赖注入**（`ITriggerView`→仅 TurnManager、`IGameUIHooks`→仅 EffectEngine）或**事件总线**（`UiNotificationBus`/`GameEventBus`），依赖方向为"UI → 接口 ← Controller"；
- 四个轻量层（`TriggerRegistry`/`ActivateEligibility`/`TargetValidity`/`AreaStatsCalculator`）均无 View 直驱；
- `GameManager` 为应用级场景状态机（`.tscn` 路径流转），`GameInitializer` 为对局组合根装配，均合规。

至此，**C/S 迁移四批核心 + 收尾红线全部完成**：Zone/Card/Player/Stack/Turn/WorldDeck/Area 状态投影闭环已打通，单机与联机共用同一套 Manager 规则。

## 九、跨进程联机（真双进程）—— ✅ 双进程实测通过

### 已落地（正式代码）
1. **`NetworkMatchLauncher.cs`**：拆出独立的 `StartServerOnly()`（服务端权威，不建客户端投影）与 `StartClientOnly()`（客户端投影，不建服务端规则）；`StartLocalLoopback` 复用二者（单进程回环向后兼容）；新增 `PushSnapshot()`（服务端主动广播当前快照）+ `ServerPlayerConnected()`/`ClientConnected()`（连接状态检测）。
2. **`NetClientCardManager.cs`**（正式）：客户端独立 CardManager，重载 `ResolveInstance` 从模板（.tres）按权威 UID 重建卡实例——跨进程最核心路径（不共享服务端实例，只靠快照）。
3. **`NetMatchEntry.cs`**（正式）：命令行驱动进程入口节点，`--server`/`--client` 分侧启动；`_Process` 显式驱动 ENet Poll；服务端检测到客户端连接后自动 `PushSnapshot()` 推初始快照；客户端连接后每 3 秒自动上行抽牌意图便于观察。
4. **`Scenes/Net/NetEntry.tscn`**：最小双进程入口场景（挂 NetMatchEntry + GameInitializer）。
5. **`CardManager.cs`**：新增 `AdoptInstanceWithUid`（子类重建实例后登记权威 UID）。
6. **`GodotEnetTransport.cs`**：新增 `IsClientConnected()`（客户端角色用底层连接状态判定，跨进程入口检测连接用）。

### 双进程验证方式
```bash
# 服务端进程（一个终端/窗口）
godot --path d:/MYPROJECT/UWPro res://Scenes/Net/NetEntry.tscn -- --server 18950
# 客户端进程（另一个终端/窗口）
godot --path d:/MYPROJECT/UWPro res://Scenes/Net/NetEntry.tscn -- --client 18950
```

### ✅ 实测结论（2026-08-26，Windows + Godot 4.7.1 mono 双实例）
- 服务端：`Peer connected → player 1` → 下行初始快照 → 随客户端每 3 秒抽牌意图持续下行新快照（手牌 12→13→14...→17）。
- 客户端：`模板库就绪：701 张卡` → 每个新卡 `从模板重建实例：JCxxx → 权威UID inst_N` → `应用快照 手牌 0→6→7→8...→11`。
- **跨进程投影同步成立**：客户端每次 `应用快照` 手牌数 == 服务端 `下行快照` 手牌数，且每张卡均为客户端**独立从模板重建**（非共享服务端实例）。
- **证明**：客户端不共享服务端实例、仅靠快照从模板重建的路径，在真实双进程 ENet 下完全可行。

### 伪跨进程验证（已通过，单进程可断言）
- `NetCrossProcessSimTests`（临时文件，验证后移除）：`TestCrossProcessClientRebuildFromTemplate` + `TestCrossProcessLauncherLoopback`——验证"客户端独立实例从模板重建、UID 对齐、投影手牌一致"，是跨进程核心逻辑的单进程证明。

### 已知边界（已修复，2026-08-26）
- `WorldDeckManager` / `AreaManager` 跨进程投影修复完成：
  - **根因**：跨进程客户端独立 CardManager，服务端地区卡不在 Zone 快照、客户端也未重建，故 `ApplySnapshotUids`/`AreaManager.ApplySnapshot` 无法命中实例 → 世界牌堆/地区注册表为空。
  - **WorldDeck**：`GameSnapshot.WorldDeckUids`（`List<string>`）升级为 `WorldDeckCards`（`List<ZoneCardRef>`，含 TemplateUid）；`WorldDeckManager.ToSnapshotUids/ApplySnapshotUids` 改用 `RebuildFromRef`（跨进程走 `NetClientCardManager.ResolveInstance` 从模板重建地区卡）。
  - **Area**：`GameSnapshot` 新增 `AreaCards`（`List<ZoneCardRef>`，含槽位）；`AreaManager` 新增 `ToSnapshot()` 导出地区卡 + `ApplySnapshot(areaCards, marks)` 从快照重建实例并 `RegisterArea`（不再依赖 AreaZone 桶）+ `SetCardManager`；`NetMatchEntry` 服务端 StartMatch 后摆 3 个初始地区（模拟 Arena 纯逻辑，供跨进程验证）。

## 十、快照差异渲染接入（客户端投影驱动 Arena UI）

### 现状（P3·模块B/D 已就绪，本次接通最后一环）
快照差异渲染管线完整但之前未通电：`GameSnapshot → SnapshotDiff.Diff → List<RenderCommand> → SnapshotRenderDriver.ApplySnapshot → ISnapshotView → Arena 现有刷新方法`。缺的是"没有生产代码在收到快照后调用 `Arena.ApplySnapshot`"。

### 本次接入（正式代码）
- **`NetMatchDemo.cs`**：新增 `[Export] DriveArenaBySnapshot`（默认 true）。`StartDemo` 后查找场景中的 Arena，订阅 `NetworkMatchLauncher.OnClientSnapshot`，收到快照时回调 `Arena.ApplySnapshot(snap)`，经差异引擎增量驱动 Arena 刷新（首包走全量 `RebuildFrom`，后续走 `Diff` 增量）。
- **接入点**：`OnClientSnapshot`（`NetworkMatchLauncher.cs:210`）——客户端收到服务端快照时触发，是差异驱动最自然的挂接点。
- **单机兼容**：单机模式 Arena 仍走 ZoneManager 事件总线（`_snapshotDriver` 为 null 时不启用）；`EnableSnapshotRender`/`ApplySnapshot` 仅网络模式启用，不破坏单机渲染。

### 范围说明
- 当前 Arena 底层仍以 `CardInstance` 承载，`ISnapshotView` 各实现为"保守映射"（`ArrangeAllCards`/`RefreshStackUI` 等全量重排），非精确增量——但差异引擎本身（Diff/Route）已由 `NetP3BTests` 充分验证。
- 跨进程客户端进程（`NetEntry.tscn --client`）无 Arena 场景，差异驱动当前作用于**单进程联机演示**（Arena + NetMatchDemo）；跨进程的"纯数据化 UI"（CardUI 改由快照挂载）留待后续。

### 验证
- 单进程联机演示：Arena + NetMatchDemo，观察日志 `快照差异已驱动 Arena（第 N 回合，总卡数=…）`，且 Arena 界面随客户端抽牌意图刷新。

### 【TEMPTEST】持续让过+抽牌自动循环（验收后移除）
- **标记**：`NetMatchDemo.cs` 内 `AutoLoopPassDraw`/`LoopIntervalSec` 字段 + `_Process` 循环分支，统一用 `【TEMPTEST】` 注释标记；移除时 grep `TEMPTEST` 即可定位。
- **用途**：验证"过回合 + 抽牌"流程下手牌增长与快照投影一致（AutoRun 只抽 N 次，本循环持续驱动）。
- **启用**：`Arena.tscn` 的 NetMatchDemo 设 `AutoLoopPassDraw = true`（`LoopIntervalSec` 默认 2s）。
- **行为**：每 `LoopIntervalSec` 秒交替上行 Pass / DrawCards 意图，驱动服务端权威回合推进 + 快照下行。
- **移除范围**：`NetMatchDemo.cs` 中 `AutoLoopPassDraw`/`LoopIntervalSec`/`_loopTimer`/`_loopPassTurn` + `_Process` 循环分支；若不再需要 `NetMatchDemo` 联机演示，可整体从 Arena.tscn 移除该节点。

## 十一、后续可选项（非迁移必需，按需推进）

> **📌 从"当前可玩"到"完整双玩家对战"的最终缺口清单（四区域 UI / 对抗 / 决策网关 / 剩余交互 / 规则完整度），详见 [`联机对战完整实现最终缺口清单.md`](./联机对战完整实现最终缺口清单.md)。** 本节仅列迁移技术侧的可选项。

1. **跨进程投影回归验证**：重新跑双进程（`NetEntry.tscn --server/--client`），确认客户端 `世界牌堆` 与 `地区` 不再为 0；跑 `TestsRunner` 确认 `NetP1Batch1MatchTests` 仍全绿。
2. **UI 纯数据化**：跨进程客户端进程若无 Arena 场景，需新建客户端渲染场景 + CardUI 改由快照挂载（不依赖 CardInstance/事件总线），才能让差异引擎在跨进程客户端真正驱动渲染。
3. **断线重连完善**：`ReconnectManager`（P3·模块E）+ `RandomSnapshot` 已就绪，可补全重连管线。
