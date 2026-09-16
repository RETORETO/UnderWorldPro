# C/S 化迁移进度与批次计划

> 用途：防止上下文丢失。记录"分批迁移 + 每批三方验证"策略下，已完成/进行中/待做的迁移工作，以及关键设计决策。
> 最近更新：2026-09-05（本轮回填：P3 模块状态核实、第一批三方验证修复、模块B 下线记录、待清理项清理；详见 §十二）
>
> **📌 联机对战完整实现的最终缺口，见独立文档 → [`联机对战完整实现最终缺口清单.md`](./联机对战完整实现最终缺口清单.md)**（覆盖：四区域 UI 缺口、对抗整体、决策网关自动取消、剩余交互、规则完整度缺口与优先级）。
>
> **📐 联机整体架构（核心模块 / 关键流程 / 职责划分 / 权威与安全边界 / 客户端延迟掩盖 / 服务端形态取舍），见 → [`联机C-S架构设计方案.md`](./联机C-S架构设计方案.md)**（2026-09-05 新增；含"Godot headless 即真服务端"的判定标准与去 Godot 化的触发条件）。

## 一、总目标

把 `UWPro` 从"Godot 单机 View 直驱 Model"架构迁移到 **C/S（客户端/服务器）网络化架构**，使游戏能跨进程联机；
且**单机运行路径与联网对战路径共用同一套核心规则逻辑**（降低维护成本，不写第二套规则）。

### 已落地地基（P0–P3，已完成，非本次迁移内容）
- **P0 进程边界**：`IInputGateway`（Local/Auto/Network 三实现）+ `GameEventBus` + `CardInstance` 纯数据化。
- **P1 MVC 分层**：`Arena` 退化为纯 View；`ZoneManager/CardManager/TurnManager` 剥离 UI 硬依赖。
- **P2 快照可序列化**：`ToSnapshot()` 聚合 + JSON 序列化。
- **P3 五模块**（2026-09-05 逐模块核实实际状态）：
  - **A·GodotEnetTransport**：✅ 生产在用（`NetworkMatchLauncher` 真跨进程链路）。
  - **B·快照差异渲染**：🔄 **驱动层已移除、差异引擎保留并接入表现层**——`SnapshotRenderDriver.cs`、`Arena.SnapshotView.cs` 已删除，`Arena` 不再实现 `ISnapshotView`；联机客户端渲染由 **ArenaOl（快照全量重建）** 承担；`SnapshotDiff.cs`/`RenderCommand.cs` **保留**，并由新增的 `SnapshotApplicationQueue` + `ArenaOl.RouteRenderCommands` 接入为**表现层（动画）事件源**（2026-09-05，见 §十三/`联机C-S架构设计方案.md` §9）。
  - **C·IntentHub·PlayerIntent·GodotIntentRouter**：✅ 生产在用。
  - **D·EventToMessageMapper**：❌ **已下线删除（2026-09-05）**——`EventToMessageMapper.cs`/`RenderEvent.cs`（含 `EventSerializer`）与 `NetP3DTests` 均已删除；动画所需的"变化"由客户端**本地快照 diff** 得到，无需服务端下发事件消息。
  - **E·ReconnectManager**：⚠️ 仅库（`ReconnectManager.cs`），生产代码零引用 → **重连管线已决定延后**（见 §十一.3）。
- **关键纪律**（需求实现对照.md §1001-1009 用户决策）：**移除 `ServerRuleEngine` 重写版**，只保留 `IIntentRouter` 接口，迁移真实 Manager 规则。即"两条输入路径收敛到同一批 Manager 方法"，不存在第二套逻辑。
  - 2026-09-05 回填：纪律已执行——`ServerCore/ServerRuleEngine.cs`（空文件残留）先被删除；随后按决策**整个 `ServerCore/` 目录已移除（方案 a）**，`UWPro.csproj` 的两条 `Compile Remove="ServerCore/**"` 一并删除。纯托管服务端路线**正式搁置**（若将来出现 `联机C-S架构设计方案.md` §1.2 的部署约束，再按该文档 §8.3 路径重启）。

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

> **状态更新（2026-09-05）：3/3 全绿。** 此前 `TestLoopbackSnapshotRoundTrip` / `TestEnetRealTransportSnapshotRoundTrip` 长期失败于 `重建后玩家1手牌数应一致 ｜ 期望 2 实际 0`。
> - **根因**：`ZoneManager.PlaceIntoBucket` 的"秘社卡兜底"按**实例类型**（`inst.CouncilTemplate != null`）无条件改写快照区域 → 任何以 `CouncilCardData` 构造的实例，无论快照写的哪个区，一律被塞进秘社区桶，导致手牌桶为空；同时该兜底也违反"快照即真相"（会把快照明确写在墓地等合法区域的秘社卡搬错）。
> - **修复**：兜底收窄为**仅当快照 zone ∈ {Hand, Deck}**（秘社卡规则上不可能出现的区）时才归位秘社区，其余区域一律以快照为准；测试场景的通用卡改用 `PlayerCardData`（构造规则合法状态）并补 1 张秘社区卡覆盖 CouncilZone 桶。
> - 结果：全量 headless 测试 **33 个套件全绿、0 失败、0 ERR**。
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
- `Script/Net/Godot/NetMatchDemo.cs`：早期联机演示节点；**2026-09-05 已从 `Arena.tscn` 移除**（渲染由 ArenaOl 接管，节点仅余意图通道演示用途）。脚本文件保留，可供手动挂载调试；如需彻底删除请见 §十二.6。
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
3. **`NetMatchEntry.cs`**（正式）：命令行驱动进程入口节点，`--server`/`--client` 分侧启动；`_Process` 显式驱动 ENet Poll；服务端检测到客户端连接后自动 `PushSnapshot()` 推初始快照。
   - ⚠️ **2026-09-05 更正**：原文"客户端连接后每 3 秒自动上行抽牌意图"**已废弃**——该自动抽牌逻辑在代码中已被注释并明确标注"已废弃、禁止重新启用"（正式玩法必须由玩家显式操作）。客户端进程现挂载 `ArenaOl` 联机战场场景承担渲染与操作。
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

### 局域网对战（2026-09-05 新增 `--host`：服务端在 A 机、客户端在 B 机）
```bash
# A 机（服务端，可加 --headless 无窗口运行；启动时会打印本机候选 IPv4）
godot --path <项目路径> res://Scenes/Net/NetEntry.tscn -- --server 18950
# B 机（两个客户端窗口，192.168.x.x 换成 A 机打印出的 IPv4）
godot --path <项目路径> res://Scenes/Net/NetEntry.tscn -- --client 18950 1 192.168.x.x
godot --path <项目路径> res://Scenes/Net/NetEntry.tscn -- --client 18950 2 192.168.x.x
```
- 服务端 ENet 监听绑定全部网卡（局域网可达）；**Windows 防火墙需允许 UDP 入站**（首次运行会弹授权，或手动加规则）。
- 服务端按**连接先后**分配玩家编号（第一个连入＝玩家1），`IntentHub` 强校验 `intent.PlayerId` 与连接映射一致 → **必须先启动玩家1 的客户端**，否则其意图会被判越权拒绝。
- 实测（2026-09-05，本机用真实网卡 IPv4 `--client <port> 1 <LAN IP>` 走非回环路径）：服务端 `Peer connected → assigned player 1/2` + 两次初始快照下行，两个客户端各自 `应用快照 手牌 0→6 总卡数=116 地区=3` 并完成战场重建，20s 内连接稳定、无 ERR。

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

> ❌ **2026-09-05 状态：本节所述接入已【整体下线】，仅作历史记录保留。**
> - `SnapshotRenderDriver.cs` / `Arena.SnapshotView.cs` 已删除；`Arena` 不再实现 `ISnapshotView`、无 `ApplySnapshot`；`NetMatchDemo` 中 `DriveArenaBySnapshot` 已移除（代码注释："联机客户端渲染统一由 ArenaOl（快照驱动·全量重建）负责"）。
> - **替代方案**：新增 `Script/UI/ArenaOl.cs` + `Scenes/Net/ArenaOl.tscn`（联机专用战场，快照**全量重建**渲染），由 `NetMatchEntry --client` 挂载；功能上覆盖了本节"用快照驱动客户端 UI"的目标，但**不走差异引擎**。
> - `SnapshotDiff.cs` 保留（`NetP3BTests` 仍在验证其 Diff 语义），但**生产无消费者**；是否保留/下线该库待定。

### 现状（P3·模块B/D 已就绪，本次接通最后一环）
快照差异渲染管线完整但之前未通电：`GameSnapshot → SnapshotDiff.Diff → List<RenderCommand> → SnapshotRenderDriver.ApplySnapshot → ISnapshotView → Arena 现有刷新方法`。缺的是"没有生产代码在收到快照后调用 `Arena.ApplySnapshot`"。

### 本次接入（正式代码）
- **`NetMatchDemo.cs`**：新增 `[Export] DriveArenaBySnapshot`（默认 true）。`StartDemo` 后查找场景中的 Arena，订阅 `NetworkMatchLauncher.OnClientSnapshot`，收到快照时回调 `Arena.ApplySnapshot(snap)`，经差异引擎增量驱动 Arena 刷新（首包走全量 `RebuildFrom`，后续走 `Diff` 增量）。
- **接入点**：`OnClientSnapshot`（`NetworkMatchLauncher.cs:210`）——客户端收到服务端快照时触发，是差异驱动最自然的挂接点。
- **单机兼容**：单机模式 Arena 仍走 ZoneManager 事件总线（`_snapshotDriver` 为 null 时不启用）；`EnableSnapshotRender`/`ApplySnapshot` 仅网络模式启用，不破坏单机渲染。

### 范围说明
- 当前 Arena 底层仍以 `CardInstance` 承载，`ISnapshotView` 各实现为"保守映射"（`ArrangeAllCards`/`RefreshStackUI` 等全量重排），非精确增量——但差异引擎本身（Diff/Route）已由 `NetP3BTests` 充分验证。
- 跨进程客户端进程（`NetEntry.tscn --client`）无 Arena 场景，差异驱动当前作用于**单进程联机演示**（Arena + NetMatchDemo）；跨进程的"纯数据化 UI"（CardUI 改由快照挂载）留待后续。

### 验证（历史记录）
- 单进程联机演示：Arena + NetMatchDemo，观察日志 `快照差异已驱动 Arena（第 N 回合，总卡数=…）`，且 Arena 界面随客户端抽牌意图刷新。
- ⚠️ 口令 "快照差异已驱动 Arena" 在当前代码中已无产生点（接线已移除），此验证步骤**作废**。

### 【TEMPTEST】持续让过+抽牌自动循环 —— ✅ 已于 2026-09-05 清理（保留原始说明作历史记录）
- **标记**：`NetMatchDemo.cs` 内 `AutoLoopPassDraw`/`LoopIntervalSec` 字段 + `_Process` 循环分支，统一用 `【TEMPTEST】` 注释标记；移除时 grep `TEMPTEST` 即可定位。
- **用途**：验证"过回合 + 抽牌"流程下手牌增长与快照投影一致（AutoRun 只抽 N 次，本循环持续驱动）。
- **启用**：`Arena.tscn` 的 NetMatchDemo 设 `AutoLoopPassDraw = true`（`LoopIntervalSec` 默认 2s）。
- **行为**：每 `LoopIntervalSec` 秒交替上行 Pass / DrawCards 意图，驱动服务端权威回合推进 + 快照下行。
- **移除范围**：`NetMatchDemo.cs` 中 `AutoLoopPassDraw`/`LoopIntervalSec`/`_loopTimer`/`_loopStep`/`_loopStarted` + `_Process` 循环分支。
- **清理结果（2026-09-05）**：上述字段与 `_Process` 分支已全部删除，`grep TEMPTEST` 在 `Script/` 下已无命中；`NetMatchDemo` 节点保留在 `Arena.tscn`（该场景已设 `AutoRun = false`，不影响单机）。
- **遗留建议（待确认）**：`NetMatchDemo` 现仅剩"意图通道演示"用途（渲染已由 ArenaOl 接管），如需彻底精简可整体从 `Arena.tscn` 移除该节点。

## 十一、后续可选项（非迁移必需，按需推进）

> **📌 从"当前可玩"到"完整双玩家对战"的最终缺口清单（四区域 UI / 对抗 / 决策网关 / 剩余交互 / 规则完整度），详见 [`联机对战完整实现最终缺口清单.md`](./联机对战完整实现最终缺口清单.md)。** 本节仅列迁移技术侧的可选项。

1. **跨进程投影回归验证** —— ✅ **已完成（2026-09-05 回填）**：`NetP1Batch1MatchTests` 4/4 全绿（含 `TestLocalLoopbackWorldDeckAndAreaProjection`：世界牌堆 UID 序列与地区势力标记投影一致）；跨进程真实双进程的"世界牌堆/地区非 0"结论见 `联机对战完整实现最终缺口清单.md`（第九轮及以后）。
2. **UI 纯数据化** —— ✅ **已完成，但走了另一条路线（2026-09-05 回填）**：未采用"差异引擎 + ISnapshotView"，而是新增 `Script/UI/ArenaOl.cs` + `Scenes/Net/ArenaOl.tscn` 作为联机专用客户端战场，按快照**全量重建**渲染（含手牌/场地/秘社/附属/分数/堆叠/地区标记），由 `NetMatchEntry --client` 挂载。
3. **断线重连完善** —— ⏸️ **本轮决定：延后（不做）**。`ReconnectManager`（P3·模块E）+ `GameSnapshot.Random`（`RandomSnapshot`）已就绪，但生产零引用；**用户决定延后**，暂不接入 `NetworkMatchLauncher`/客户端入口。
   - 若将来要做，需补：① 连接断开检测（`GodotEnetTransport` 侧事件）；② 重连握手消息与身份确认；③ `GameSnapshot.PendingInput` 恢复（服务端挂起请求续接）；④ 客户端 UI 状态提示（重连中/已恢复）。参考实现入口：`ReconnectManager.BuildPayload/ApplyPayload` + `NetworkMatchLauncher.OnClientMessage` 分流。
4. **P3·模块D（EventToMessageMapper）** —— ✅ **已决定并执行：下线删除（2026-09-05）**：`EventToMessageMapper.cs` / `RenderEvent.cs`（含 `EventSerializer`）/ `NetP3DTests.cs` 已删除，`TestsRunner` 移除注册。定位与理由见 §十二.4(1)。
5. **`SnapshotDiff.cs`** —— ✅ **已决定并执行：保留并接入为表现层事件源（2026-09-05）**：新增 `Script/Net/SnapshotApplicationQueue.cs`（合并/延后/配对），`ArenaOl` 在 `_Process` 应用快照时用 `SnapshotDiff.Diff(prev,next)` → `RouteRenderCommands()`。见 §十二.4(2)。
6. **`ServerCore/` 纯托管路线** —— ✅ **已决定并执行：整体移除（方案 a，2026-09-05）**：目录与 `UWPro.csproj` 两条 `Compile Remove` 已删除；路线正式搁置（触发条件与重启路径见 `联机C-S架构设计方案.md` §1.2/§8.3）。见 §十二.4(3)。
7. **`NetMatchDemo` 演示节点** —— ✅ **已完成（2026-09-05）**：已从 `Arena.tscn` 移除节点与其 `ext_resource`（`Scenes/` 下已无引用）。
8. **延迟掩盖预留（表现层事件源）** —— ✅ **已完成预留（2026-09-05）**：快照同帧合并 + 延后窗口（上限 1.5s）+ 差异指令路由；动画本体与乐观执行属后续 Phase C（见 `联机C-S架构设计方案.md` §9/§10）。

---

## 十二、最新状态回填（2026-09-05 · 逐项核实结论）

> 背景：本文档此前停留在 2026-09-01，期间联机工作（ArenaOl、决策网关/选择协议、对抗与触发、效果系统接入等）大量推进但未回填；本节记录**逐项代码核实**的结果与处理。

### 12.1 核实结论一览

| 编号 | 项 | 原文档状态 | 核实结果 | 处理 |
|---|---|---|---|---|
| U1 | 第一批三方验证（`NetP1Batch1Tests`） | ✅ 已实现 | ⚠️ 3 用例中 2 个长期失败（手牌重建为 0） | ✅ **已修复**（见 §三状态更新）→ 3/3 通过 |
| U2 | P3·模块B 快照差异渲染 | ✅ 已接入正式代码 | ❌ 驱动层已删除、`Arena` 无 `ISnapshotView` | ✅ 回填为"驱动层下线"；**差异引擎已保留并接入表现层**（2026-09-05，见 §十三） |
| U3 | §十 【TEMPTEST】自动循环 | 待验收后移除 | ⚠️ 仍在代码中 | ✅ **已清理**（`NetMatchDemo.cs` 字段+`_Process` 分支；grep 无命中） |
| U4 | P3·模块E 断线重连 | 库就绪，可补管线 | ⚠️ 生产零引用 | ⏸️ **延后（用户决定 2026-09-05，§十一.3）** |
| U5 | §十一.2 UI 纯数据化 | 留待后续 | ✅ 已由 ArenaOl 实现（非差异引擎路线） | ✅ 已回填（§十一.2） |
| U6 | `ServerRuleEngine` / 纯托管路线 | 已移除重写版 | ⚠️ 空文件残留 + `ServerCore/` 骨架仍在 | ✅ **整个 `ServerCore/` 已移除（方案 a，2026-09-05）**；csproj 排除项同步删除 |
| U7 | §十一.1 跨进程投影回归验证 | 待做 | ✅ 实际已完成（`NetP1Batch1MatchTests` 4/4 绿） | ✅ 已回填为已完成（§十一.1） |
| U8 | P3·模块D EventToMessageMapper | 列为五模块之一 | ⚠️ 仅库+测试、生产零引用 | ✅ **已下线删除（2026-09-05）**（文件+测试+注册全清） |
| U9 | `NetMatchDemo` 节点挂在 `Arena.tscn` | 文档建议可移除 | ⚠️ 演示节点残留 | ✅ **已移除（2026-09-05，§十一.7）** |
| U10 | 延迟掩盖/动画的"预留" | 未规划 | — | ✅ **预留已实现**（`SnapshotApplicationQueue` + `ArenaOl.RouteRenderCommands`，§十一.8） |

### 12.2 第一批验证失败根因与修复（U1，已闭环）

- **现象**：`TestLoopbackSnapshotRoundTrip` / `TestEnetRealTransportSnapshotRoundTrip` 断言 `重建后玩家1手牌数应一致 ｜ 期望 2 实际 0`；`TestStandaloneSnapshotExportIntegrity` 通过（该用例不调 `ApplySnapshot`，故未暴露）。
- **根因**：`ZoneManager.PlaceIntoBucket` 内的"秘社卡兜底"以**实例类型**判定（`inst.CouncilTemplate != null`）并无条件改写区域桶，**覆盖了快照里的 Zone**：
  - 测试场景用 `CouncilCardData` 作为通用模板构造手牌/资产等卡（仅为构造方便），这些实例 `CouncilTemplate != null` → 重建时全部被塞进 `_council`，`_hand` 因此为空；
  - 更深层问题：该兜底违反"快照即真相"——若快照明确把秘社卡写在墓地/争夺区等合法区域，客户端也会把它搬回秘社区（潜在显示/计数错误）。
- **修复**：兜底收窄为**仅当快照 `Zone ∈ {Hand, Deck}`**（秘社卡规则上不可能出现）时才归位秘社区；其余区域一律以快照为准。
- **测试侧同步**：`BuildScenario` 通用卡改用 `PlayerCardData`（构造规则合法状态），并补 1 张玩家1 秘社区卡以覆盖 `CouncilZone` 桶（新增断言 `重建后秘社区数应一致（秘社卡兜底不得误搬）`）。
- **验证**：`dotnet build` 0 警告 0 错误；headless 全量 `TestsRunner` **33 套件全绿、0 失败、0 ERR**（历史首次全绿）。

### 12.3 仍需跟进（未完成项）

1. **P3·模块E 断线重连管线** —— ⏸️ **本轮决定：延后**（不列入当前待办；完成路径见 §十一.3）。
2. **P3·模块D（EventToMessageMapper）/ `SnapshotDiff.cs` / `ServerCore/`** —— ⏳ **待决策**（三者的定位、当前状态、完成路径与成本评估见 §十二.4）。
3. （已完成项）`NetMatchDemo` 节点已从 `Arena.tscn` 移除，见 §十一.7。

### 12.4 三个待决策模块的定位与完成路径（2026-09-05 补充）

#### (1) P3·模块D：渲染事件 → 网络消息（`EventToMessageMapper.cs` / `RenderEvent.cs` / `EventSerializer`）

- **原本要做什么**：把 Manager 事件总线上的渲染事件（`OnCardMoved` / `OnCardStateChanged` / `OnPhaseChanged` / `OnPriorityChanged` / `OnCardDrawn` / `OnAreaWon` / `OnGameOver` / `AssetsChanged` / `StackChanged`）转成**可序列化的网络消息**，使"事件驱动渲染"可以跨进程：客户端把本地事件上报、或服务端把权威变更广播给客户端（用于表现层提示：动画/音效/飘字等）。
- **当前状态**：库 + `EventSerializer` + `NetP3DTests` 覆盖齐全，但**生产零引用**（`NetworkMatchLauncher` 只下发快照，不转发事件）。
- **要完成什么**（若要启用）：① 服务端在 Manager 事件处建 `EventToMessageMapper` 并挂到 `GodotEnetTransport` 下发（新增消息前缀如 `EVT:`）；② 客户端在 `NetworkMatchLauncher.OnClientMessage` 分流并暴露 `OnClientRenderEvent`；③ `ArenaOl` 订阅事件做增量表现（动画/高亮），**不再需要**本地 diff。
- **成本/收益**：接线成本中等（协议 + 两端分流 + UI 表现层），收益是"表现层提示"；但当前客户端是**快照全量重建**，动画提示也可由客户端**本地对前后快照做 diff** 得到，无需服务端下发事件消息。
- **建议**：**下线（删除模块D）**，避免长期死代码；若未来需要"服务端权威的表现提示"（例如只有服务端知道的信息，如对手暗藏翻面瞬间），再按上述路径重建。
- ✅ **决策与结果（2026-09-05）**：采纳下线 —— `EventToMessageMapper.cs` / `RenderEvent.cs`（含 `EventSerializer`）/ `NetP3DTests.cs` 已删除，`TestsRunner` 注册已移除。

#### (2) `SnapshotDiff.cs`（+ `RenderCommand.cs`）：快照增量差异引擎

- **原本要做什么**：对比前后两个 `GameSnapshot`，输出最小增量 `RenderCommand` 列表（AddCard/RemoveCard/MoveCard/UpdateCard/StackPush/StackPop/PlayerUpdate/PhaseChange），让客户端**增量刷新 UI** 而不是全量重建（模块B 的核心算法；配套的 `SnapshotRenderDriver`/`ISnapshotView` 已删除）。
- **当前状态**：纯函数、零依赖、`NetP3BTests` 覆盖；**生产零消费者**（ArenaOl 走全量重建）。
- **要完成什么**（若要启用）：① 重建"命令消费者"：在 `ArenaOl`（或新客户端场景）中按 `RenderCommand` 分发到已有的重建分支（增量版 `MoveCard/UpdateCard/StackPush` 等）；② 客户端保存 `_lastSnapshot` 并在 `ApplySnapshot` 时改为 `SnapshotDiff.Diff(_last, snap)` 驱动；③ 处理全量兜底（首包/差异异常时回退全量重建）。
- **成本/收益**：收益是**性能与"不打断交互"**（避免每次快照重建全部节点、丢失拖拽/高亮态）。当前对局规模下全量重建可用，故非必需。
- **建议**：**保留库（冻结）**，并在文档标注"未来客户端增量渲染/本地动画提示可复用"；若追求减债也可与模块D 一并删除。
- ✅ **决策与结果（2026-09-05）**：采纳保留，并**提前接入为表现层（动画）事件源**（不是继续冻结）——新增 `Script/Net/SnapshotApplicationQueue.cs`（同帧合并 + 可延后 + 与上一份已应用快照配对），`ArenaOl` 订阅改为"只入队"，在 `_Process` 中按安全时机应用快照并用 `SnapshotDiff.Diff(prev,next)` → `RouteRenderCommands()`（表现层唯一入口）。`NetP3BTests` 新增 3 例覆盖（合并/配对/延后），共 6 例全绿。

#### (3) `ServerCore/`：纯托管（脱离 Godot）服务端内核

- **原本要做什么**：把服务端做成**不依赖 Godot** 的纯 .NET 进程（服务器/容器里跑，无 Godot 运行时），复用 `Script.Enums` + `Script.Net`（DTO/快照/意图/随机源）作为协议与数据层地基。
- **当前状态 = 冒烟骨架，非可用服务端**：
  - `Program.cs`：纯托管冒烟（随机复原、`SnapshotDiff`、重连载荷、`IntentHub` + **桩路由 `EchoIntentRouter`**）；其中"真实规则结算"被显式标注为不实现（`ServerRuleEngine` 重写版已按纪律删除）。
  - `CardTemplateRepository.cs`：读 `Cards/card_templates.json`（Godot 侧 `CardExportRunner` 导出，约 1.2 MB / 700+ 张）按 UID 反查 —— **M2 卡牌 JSON 数据协议**，价值最高的一块。
  - `ServerCardInstance.cs`：纯托管卡实例（能力值、持续授予/注销、翻面+版本号、伤害/死亡）。
  - `ServerCore.csproj`：TFM `net10.0`（与主工程 `net8.0` 不一致），只编译 `Script/Enums` + `Script/Net`（排除 Godot 子目录与 `InputGateways.cs`）+ `DeterministicRandom`；**不含任何 Manager/规则层**。
- **要完成什么**（若要变成真正的纯托管服务端）：① 规则层去 Godot 化——`ZoneManager`/`CardManager`/`TurnManager` 目前都是 `Node`，`EffectEngine` 有 97 处 `GD.*`、`TurnManager` 132 处、`PMCommandManager` 128 处；效果数据模型 `EffectData` 还依赖 `Godot.Collections.Array<T>` 与 `[Export]`；② `ResolveInstance` 改为从 `CardTemplateData`(JSON) 建实例；③ 用真实 `IIntentRouter` 替换 `EchoIntentRouter`，并接上 Zone/Turn/Stack/Effect 全链路；④ 传输与主循环（ENet/其它 socket）+ 快照广播 + `ChoiceRequest` 决策回环；⑤ 与 Godot 客户端协议对齐验证。
- **成本/收益**：成本**很高**（等于把整套规则+效果系统脱离 Godot，且与"不写第二套规则"的纪律冲突）；当前服务端已可用 **Godot `--headless`** 方式运行（`NetMatchEntry --server`），功能上已满足联机需求。
- **建议**：**不推进该路线**。做法二选一：**(a) 整体移除 `ServerCore/`**（并删 `UWPro.csproj` 的两条 `Compile Remove`），把 `card_templates.json` 导出能力保留在 Godot 工具里；**(b) 降级保留为"协议/数据层验证工具"**：保留 `CardTemplateRepository`（JSON 数据协议有复用价值），删除 `ServerCardInstance` 与桩路由，避免被误当作第二套规则实现。
- ✅ **决策与结果（2026-09-05）**：采纳 **(a) 整体移除** —— `ServerCore/` 目录已删除，`UWPro.csproj` 两条 `Compile Remove="ServerCore/**"` 与注释已删除；卡牌 JSON 导出能力继续保留在 Godot 工具侧（`Script/Tools/CardTresToJson` / `CardExportRunner`）。服务端形态**维持 Godot headless**（判定标准与去 Godot 化触发条件见 `联机C-S架构设计方案.md` §1/§8）。

### 12.5 文档维护约定（避免再次过期）

- 涉及"接入/下线/替换"的决定，必须在**同一轮**写入本文档对应章节（参考本轮 §一/§十/§十一 的状态标注方式）。
- 每轮同步记录：`dotnet build` 结果 + headless 全量测试结论（套件数/失败数），失败用例须列名。
- 与 `联机对战完整实现最终缺口清单.md` 的分工：**本文档记迁移技术侧**（模块、批次、投影链路、下线决定），**缺口清单记玩法完整度**（UI/对抗/交互/规则）。

---

## 十三、执行记录（2026-09-05 · 三模块决策落地 + 延迟掩盖预留）

> 决策来源：`联机C-S架构设计方案.md` §12（已确认）。本节为**迁移技术侧**的执行记录。

### 13.1 代码改动
| 类别 | 内容 |
|---|---|
| 删除（模块D 下线） | `Script/Net/EventToMessageMapper.cs`、`Script/Net/RenderEvent.cs`（含 `EventSerializer`）、`Script/Tests/NetP3DTests.cs`；`Script/Tests/TestsRunner.cs` 移除 `new NetP3DTests()` |
| 删除（ServerCore 方案 a） | 整个 `ServerCore/` 目录；`UWPro.csproj` 移除两条 `Compile Remove="ServerCore/**"` + 注释 |
| 新增（表现层事件源底座） | `Script/Net/SnapshotApplicationQueue.cs`：同帧合并（`CoalescedCount`）、延后窗口（`IsDeferred`）、配对（`TryDequeue(out prev, out next)`）、`Reset()` |
| 修改（ArenaOl） | 快照订阅改为 `OnSnapshotReceived`（只入队）；`_Process` 中 `PumpSnapshotQueue(delta)` 决定应用时机（本地交互/动画期延后，**上限 1.5s**）；应用后用 `SnapshotDiff.Diff(prev,next)` → `RouteRenderCommands()`（当前为分类计数 + `LastPresentationSummary`，`VerbosePresentation` 可开日志）；`AttachLauncher` 时 `_snapshotQueue.Reset()` |
| 修改（测试） | `Script/Tests/NetP3BTests.cs` 新增 3 例（`TestSnapshotQueueCoalescing`/`TestSnapshotQueuePairing`/`TestSnapshotQueueDeferred`），并更正文件头注释（原提及的 `SnapshotRenderDriver` 已不存在） |

### 13.2 验证
- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**32 个套件全绿、0 失败、0 ERR**（套件数 33→32 因删除 `NetP3DTests`；`NetP3BTests` 由 3→6 例）。
- 备注：Godot headless 需写入 `user://` 日志目录（`%APPDATA%`），受限沙箱下会 SIGSEGV；该现象与工程无关，验证时以放宽权限运行。

### 13.3 下一步（建议顺序）
1. ~~Phase B 首位：信息隔离~~ → ✅ **已完成，见 §十四**。
2. **Phase C：表现层动画**（把 `RouteRenderCommands` 的实现换成动画队列）+ 乐观执行最小集 + 卡图预加载。
3. Phase D（可选）：断线重连接线；去 Godot 化仅在 §1.2 条件出现时重启。

---

## 十四、Phase B 执行记录（2026-09-05 · 信息隔离 / 信封 v2 / 超时配置化）

> 设计依据：`联机C-S架构设计方案.md` §10 路线图 Phase B（信息隔离提到首位）与 §14 执行记录。本节为迁移技术侧记录。

### 14.1 信息隔离（隐藏信息不再下发）
- **新增** `Script/Net/SnapshotFilter.cs`：按观看者过滤快照——隐藏判定 = `Zone ∈ {Hand, Deck}` 且 `ZoneOwnerId != viewer`；`WorldDeckCards`（地区牌堆）对双方隐藏；隐藏项替换为**占位引用**（`Uid = hidden_<zone>_<owner>_<index>`、`TemplateUid = null`、`IsHidden = true`），**数量与顺序保留**。
- **数据模型**：`ZoneCardRef.IsHidden`（随快照序列化）、`CardInstance.IsHidden`（运行时；单机恒 false）。
- **占位实例解析（统一入口）**：`CardManager.RebuildFromRef` 在 `ResolveInstance` **之前**统一处理隐藏引用（`new CardInstance(null, uid)`、稳定 UID 复用、只写区域归属、**不写运行时状态**以防侧信道泄露）→ 子类 override（`NetClientCardManager` 跨进程 / 测试替身 / 未来纯托管）无需各自实现；`NetClientCardManager` 移除重复分支并对空 `TemplateUid` 加保护。
- **服务端下发**：`GameInitializer.BuildSnapshotFor(playerId)`；`NetworkMatchLauncher.PushSnapshot()` 改为**逐玩家过滤下发**；`OnServerSnapshot` 仍为完整快照（服务端自省/测试）。
- **客户端渲染**：`ArenaOl.RebuildInto` 对占位卡 `Setup(null) + SetFace(false)`（不可拖拽、不减费、不参与高亮/交互）；`DeckZone` 对占位卡库/牌堆同样只画卡背并按数量显示；可发动判定/费减/墓地重建跳过占位。

### 14.2 协议加固（信封 v2）
- **新增** `Script/Net/NetEnvelope.cs`：`V2:<type>|<payloadJson>`，`type ∈ {intent, intent_result, choice_req, choice_res}`；只按**第一个** `|` 切分。
- **接线**：`IntentClient.SendIntent`、`NetworkMatchLauncher.SendChoiceResult`（上行打信封）；`OnServerMessage`（回执打信封 + 优先按类型分发）；`OnClientMessage`（按类型分发）；`NetworkInputGateway.RequestChoice`（打信封）与 `TryHandleResult`（**信封/裸 JSON 双读**）。
- **兼容**：快照沿用 `SNAP:` 前缀；非信封的旧格式回退"形状识别"分支，行为不变。

### 14.3 决策超时按 Kind 配置化
- `NetworkInputGateway.TimeoutSecondsFor(kind)`：选目标/触发排序/对抗分配 60s、强制二选一 45s、破平与其它 30s；`DefaultTimeoutSeconds` 兜底；可注入 `TimeoutProvider` 覆盖；超时回包补齐 `RequestId`。
- 仍待做：掉线**立即**取消（当前需等超时）→ 并入 Phase D 重连。

### 14.4 测试与验证
- 新增 `Script/Tests/NetPhaseBTests.cs`（6 例）：过滤（双视角）/占位解析（共享与跨进程两条路径 + 不写运行时状态）/信封往返与旧格式兼容/超时映射/超时兜底 Cancelled。
- 适配既有用例：`NetP2Tests`（解包请求+包裹回包）、`NetP3CTests`（服务端解包+回执包裹）、`NetP1Batch1MatchTests`（世界牌堆断言改为"数量一致 + 全占位"）、`NetCrossProcessSimTests`（测试替身空 `TemplateUid` 保护）。
- 结果：`dotnet build` **0 警告 0 错误**；headless 全量 `TestsRunner` **33 套件全绿、0 失败、0 ERR**。

### 14.5 下一步
- **Phase C（表现层与延迟掩盖）→ ⏸ 暂缓**：动画的**内容与制作细节尚未由用户指定**（动画清单／与全量重建的关系／时序节奏／播放期间可操作性／隐藏信息表现约束／乐观执行范围／美术音效资源／性能预算），问题清单见 `联机C-S架构设计方案.md` §9.5。**未指定前不实施**。
- 已就绪的前置：`SnapshotApplicationQueue` + `ArenaOl.RouteRenderCommands`（差异指令可作为动画触发源）+ 信息隔离（动画只能演可见信息）。
- 待复核的占位假设：`ArenaOl` 的"本地交互期间延后应用快照，上限 1.5s"（`SnapshotMaxDeferSeconds`）——若最终采用"动画不阻塞快照"，需改为不阻塞或重定阈值。

---

## 十五、UI 对齐与功能补齐执行记录（2026-09-05 · 四项）

> 用户指令：**甲1（事务卡选目标）+ 甲2（地区统计）+ 遗留1（堆叠区 UI）+ 遗留2（对抗 UI 对齐单机）**。全部为"对齐单机"的确定性改动；**不涉及动画**（Phase C 仍暂缓）。

### 15.1 甲1 — 事务卡（需目标出牌）联机选目标 ✅
- `ArenaOl`：拖出未命中目标且卡为 `Sorcery + GetRequiresTarget()` → 进入 `BeginOnlinePlayCardTargetSelection`（费用预检 → 合法目标 = 争夺区角色/附属/秘社，经 `ClientCardManager.IsLegalPlayTarget` 过滤 → 场上高亮 + 复用单机 `TargetSelectionWindow`）→ 选中后 `PlayCard(cardUid, null, targetUid)`。
- 复用既有"场上选目标"状态机，新增 `_pendingPlayCardUid` 区分"激活 vs 出牌"两条上行路径。

### 15.2 甲2 — 联机地区统计显示 ✅
- `ArenaOl.RebuildAreas` 注入 `slot.SetStatsDependencies(ClientZoneManager, ClientPlayerManager)` → 复用 `AreaStatsCalculator`（与单机同源规则），每次快照重建后刷新。

### 15.3 遗留1 — 堆叠区 UI 对齐单机 ✅
- 快照/投影：`StackEntrySnap` 增补 `ControllerId`/`WonAreaUid`/`SourceTemplateUid`/`Targets`（新 DTO `StackTargetSnap`）；`StackManager.ToSnapshot/ApplySnapshot` 双向映射；`NetworkMatchLauncher.ClientStackSnapshot` 暴露给 UI。
- `ArenaOl.RefreshStackUI`：卡片型真实卡面 / 效果型文本 + **来源卡图** / `AreaWon` 挂地区卡面（用单机 `StackEntryUI.AttachAreaCard` 这一**既有公开方法**）/ 按 `Targets` 画箭头（控制者着色、每帧跟随端点）。
- **单机 UI 零改动**：来源卡图由 ArenaOl 直接设置条目场景内既有节点 `TriggerCardArt` 的纹理，未给 `StackEntryUI` 增 API（`Script/UI/StackEntryUI.cs` 原样）。

### 15.4 遗留2 — 调查/战斗/破平对抗 UI 对齐单机 ✅（**新建联机专用 UI**）
- **根因修复（关键）**：服务端在"等待玩家决策"期间**不推快照**，故待分配调查卡必须**随请求下发** → 新增 `ChoiceRequest.OptionCardRefs`（与 `OptionUids` 同序的展示引用，`CombatManager.ToDisplayRef` 填充）；客户端用投影 CardManager `RebuildFromRef` 重建卡面。此前"靠快照携带"的方案在该时序下必然拿不到卡、只能退回简化面板（已修正）。
- 协议：`ChoiceRequest` 增补 `AreaUid`/`AreaNo`/`LoserPlayerId`/`CombatTypeValue`/`OptionCardRefs`。
- **新建** `Script/UI/OnlineStrengthWindow.cs` + `Scenes/Net/OnlineStrengthWindow.tscn`：与单机 `StrengthWindow` 视觉/操作一致（左键 +1、右键 -1、清空、剩余点数不足禁止确认），但**不做本地结算** → 确认时把预分配结果交 `OnConfirmed` 由 ArenaOl 回包；合法目标由 `IsLegalTarget`（服务端下发集合）判定。
- **新建** `Script/UI/OnlineDeckZone.cs` + `ArenaOl.tscn` 两个牌库节点改挂此脚本：`CardUI.Setup(null)` 只画卡背 + 数量（安全渲染无模板占位）。
- **直接复用**：`InvestigationWindow`/`TieBreakWindow` 是纯 View（回调注入、不调用数据层）→ 联机同一场景直接实例化，回调改为回 `ChoiceResult`。
- **单机零改动**：`StrengthWindow.cs`/`StackEntryUI.cs`/`DeckZone.cs` 三个共享类**已全部恢复原样**（本轮先试做的 additive 钩子按"不改单机 UI/逻辑，复用不了就新建"要求回退）。
- 快照兜底：`GameSnapshot.InvestigationCards` + `InvestigationOwnerPlayerId`（仅决策者可见）保留为**兜底来源**（若客户端缺 `OptionCardRefs` 时使用）。
- 信息隔离收紧：**牌库对双方都只发占位**（不暴露牌序），手牌仍"自己可见、对手占位"。
- 服务端加固：战斗分配按服务端计算的合法目标集合复检。

### 15.5 验证
- `dotnet build` **0 警告 0 错误**；headless 全量 `TestsRunner` **33 套件全绿、0 失败 0 ERR**；`NetPhaseBTests` 6 → **9 例**。
- `Scenes/Net/ArenaOl.tscn` 单独加载：**无解析/加载错误**（牌库节点已改挂 `OnlineDeckZone`）。
- 待人工冒烟：事务卡弹选目标、地区统计数字、堆叠区卡面/箭头、三类对抗窗口交互与回包。

### 15.6 仍未做（不受本轮影响）
- **Phase C（动画/延迟掩盖）**：⏸ 待用户指定动画设计规格（见 `联机C-S架构设计方案.md` §9.5）。
- **Phase D（断线重连）**：⏸ 已决定延后；另含"掉线立即取消挂起决策"。
- Phase B 收尾：`ChoiceResult` 的 `PlayerId` 复核、意图去重/频率限制。

---

## 十六、出牌拖拽语义统一执行记录（2026-09-05 · 联机对齐单机手势）

> 用户指令：**"联机使用事务牌的逻辑和单机不一致——单机是拖出手牌区后选择目标，联机是直接拖拽到目标；检查差异并统一成和单机一致"**。约束：**不改单机 UI/逻辑**，只动联机侧。设计侧完整对照见 `联机C-S架构设计方案.md` §16。

### 16.1 改动清单
| 类别 | 内容 |
|---|---|
| **重写** | `Script/UI/ArenaOl.cs` `OnLocalHandCardDragEnded`：按单机 `CardDragController.OnCardDragEnded` 的 9 步落点语义逐步对齐（模态冻结 → 蒙版分支 → 拖回手牌取消 → 合法目标卡 → 地区/秘社落点 → 角色卡必须落己方争夺区 → **事务卡弹窗选目标** → 其它需目标卡不发意图 → 上行 `PlayCard` 意图）。 |
| **命中范围统一** | `PickFieldCardAt` 收敛为"仅 `AreaZone`/`AttachedZone` + 跳过隐藏占位 + 取最上层"（对齐单机 `PickAreaCardAt`），并被"出牌落点 / 伤害分配点击 / 激活选目标"三处共用；删除重复实现 `PickFieldCardUiAt` 与已无引用的 `FindTargetCardUidAt`。 |
| **新增（拖拽高亮）** | `ArenaOl.UpdateHandDragTargetHighlights` / `ClearHandDragTargetHighlights`（`_Process` 驱动）：无蒙版时高亮争夺区/附属区中的合法目标卡 + 本方秘社，对齐单机 `UpdateDragTargetHighlights`。 |
| **蒙版落点** | 建立资产=**己方资产区或任一争夺区**，否则不发意图（对齐单机 `HandleMaskDrop`）；派遣暗藏=**己方**争夺区（联机按 `_localPlayerId` 判定，替代单机硬编码 `Player1Area`，P2 同样可用）。 |

### 16.2 原理说明（为何"单机退回手牌"在联机是"不发意图"）
单机 `OnCardDragEnded` 是"本地结算 + 自行归位（`ReturnCardToHand` + `PendingHandInsertIndex`）"；联机是"只上行意图，归位与呈现完全由快照重建驱动"，故单机所有"退回手牌"分支在联机统一表达为**不发意图并 return**。这保持了"客户端不写 Model / 不重算规则"的红线。

### 16.3 验证
- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**33 套件全绿、0 失败、0 ERR**（`>>> 全部逻辑正确性测试通过`）。
- 待人工冒烟（headless 不覆盖 UI）：事务卡拖出→弹窗选目标；合法/非法落点的高亮与取消；蒙版拖拽落点。

### 16.4 已知差异（**未做，待用户拍板**）
1. **蒙版可拖拽资格无客户端预检**：单机拖起时用 `TurnManager.CanDeployCovert` 拦截；联机客户端不复制该规则，非法由服务端拒绝 + 回执提示（守"客户端不重算规则"红线）。若需预检，请确认是否允许客户端投影该判定。
2. **其余决策面板仍为简化版**：`SelectTriggerOrder` → 单机 `TriggerOrderWindow`、`PayOrTap` → 单机 `Arena.PromptPayOrTap`、`ForceChoice` 在联机仍走 `ChoicePromptPanel`。**是否继续对齐待确认。**
3. **取消归位的插回顺序**：单机按放手 X 精确插回（`PendingHandInsertIndex`）；联机顺序由下一次快照重建决定。
4. `DeployCovert` 的"己方"判定联机按本地玩家（更通用），与单机硬编码 P1 不同——已记为联机修正项。

---

## 十七、决策面板三处对齐单机 + 蒙版拖起预检（2026-09-05 · 第 3 版）

> 用户裁决：① 「**全部三处一起对齐**」决策面板；② 「**加客户端预检**」蒙版可拖拽资格。
> 实施前计划与 5 个开放问题的确认结论见 `PLAN.md` 第十节；设计侧完整说明见 `联机C-S架构设计方案.md` §17。

### 17.1 改动清单

| 类别 | 内容 |
|---|---|
| **协议（纯加法）** | `ChoiceRequest`：`PayAllowed`（服务端权威"支付得起"）、`OptionForced`（同序强制标记）、`OrderContextId`（排序窗口上下文）、`OptionLabels`/`OptionTexts`（条目名/效果文本）；`ChoiceResult`：`SelectedIndices`（有序下标＝本次入堆叠的顺序）。 |
| **服务端** | `TurnManager.RunTriggerPhaseAsync` 由"一次请求 + 全量默认顺序入栈"改为**逐轮循环**：按客户端 `SelectedIndices` 顺序入堆栈、从剩余移除后续发下一轮；有强制触发时**忽略取消并重问**（等价单机 Done 禁用）；越界/重复下标忽略；未回下标（旧端）退化为"全部默认顺序"。每 owner 最多 32 轮防呆。**单机同步流程零改动**。 |
| **服务端** | `EffectEngine.ExecuteVerbAsync`（PayOrTap）补 `PayAllowed = CanPayPrepareUpkeep(...)`。 |
| **新增（联机专用 UI）** | `Script/UI/OnlineTriggerOrderWindow.cs` + `Scenes/Net/OnlineTriggerOrderWindow.tscn`：对齐单机 `TriggerOrderWindow`（入堆叠区/放弃区拖拽 + 全部/第一张入堆叠 + 完成排序）；**只读复用** `TriggerEntryUI`/`TriggerOrderRoot`（单机两文件零改动）；按 `OrderContextId` 复用同一窗口以支持"逐次入堆叠"多轮往返，并按 `OptionKey` 恢复放弃区落区。 |
| **新增（联机专用 UI）** | `Script/UI/OnlinePayOrTapWindow.cs`（纯 View，无场景文件）：对齐单机 `Arena.PromptPayOrTap`——支付/横置二选一、费用不足置灰「支付」、强制（无取消、点窗外不关闭）；不做本地结算，回 `ChoiceResult.Pay`。 |
| **`ArenaOl`** | `SelectTarget` 改用**单机同款选目标流程**：复用 `TargetSelectionWindow` + 场上合法目标高亮，新增 `_pendingChoiceTargetRequest` 区分"回 ChoiceResult"与"上行意图"；**新增地区目标支持**（`_activationLegalAreaUids` + `AreaSlot.SetHighlight/IsTargetSelectable/OnAreaClicked` + `PickLegalActivationAreaAt`）；`_Input` 修正为在选目标分支内自行判定地区槽位（`Node._Input` 先于 `Control._GuiInput`）。`TryOpenOnlineCombatWindow` → `TryOpenOnlineDecisionWindow`（现覆盖 6 种决策）。 |
| **蒙版预检（P4）** | 新增 `Script/Manager/DeployCovertEligibility.cs` 静态纯函数；`TurnManager.CanDeployCovert` **改为委托调用**（行为等价，单机零变化）；`ArenaOl` 用同一函数在**拖起前**门禁（`CardUI.CanDrag` + 进入/退出蒙版时刷新），非法卡拖不起来。 |

### 17.2 验证

- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**34 套件全绿、0 失败、0 ERR**，`>>> 全部逻辑正确性测试通过`。
- 新增 `Script/Tests/NetChoicePanelTests.cs`（6 例）：协议字段往返 / 排序顺序应用 + 放弃剩余 / 强制触发忽略取消并重问 / PayOrTap `PayAllowed` 与落地 / 蒙版资格与 `CanDeployCovert` 一致 / 决策窗口场景加载与导出引用解析。
- 待人工冒烟：排序窗口拖拽与逐次入堆叠、支付/横置置灰、场上与地区点选目标、蒙版非法卡拖不起来。

### 17.3 遗留（不受本轮影响）

- **Phase C（动画/延迟掩盖）**：⏸ 待用户指定动画设计规格（`联机C-S架构设计方案.md` §9.5）；**待办清单与前置决策已收口到 `后续优化事项.md` §五**。
- **Phase D（断线重连）**：⏸ 延后；含"掉线立即取消挂起决策"。**接线清单与待拍板项见 `后续优化事项.md` §六**。
- Phase B 收尾：`ChoiceResult` 的 `PlayerId` 复核、意图去重/频率限制。
- 交互细节差异（已记录于设计方案 §17.6）：Done 点击可能命中"服务端已结束"（空回包被丢弃，无功能影响）；放弃区落区记忆按 `OptionKey` 一对一匹配。