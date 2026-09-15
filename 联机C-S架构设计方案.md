# UWPro 联机（C/S）架构设计方案

> 用途：把"当前已落地的联机实现"整理成**一份可执行的设计说明**：核心模块、整体架构、关键流程、职责划分、权威与安全边界、客户端延迟掩盖设计，以及"服务端形态"的取舍结论。
> 状态：设计说明（基于 2026-09-05 代码实况整理）；文中标注【已实现】/【待做】/【待决策】。
> 关联文档：`C-S迁移进度与批次计划.md`（迁移技术侧进度）、`联机对战完整实现最终缺口清单.md`（玩法完整度缺口）。

---

## 0. 结论摘要（TL;DR）

1. **C/S 化核心已完成**：单机与联机**共用同一套规则**（Manager 层），联机只多了一层"网络输入网关 + 快照投影"，不存在第二套规则实现。
2. **服务端 = Godot headless 进程**（`NetEntry.tscn --server`）就已经是**真正的服务端**：规则、裁决、状态真相全在服务端进程，客户端只是投影 + 输入。**"不把 Godot 去掉"不等于"不是真服务端"**（详见 §9）。
3. **纯托管 `ServerCore/` 路线不建议推进**：要变成真服务端需把整套规则/效果层脱离 Godot（`ZoneManager`/`CardManager`/`TurnManager` 均为 `Node`；`EffectEngine` 97 处 `GD.*`、`TurnManager` 132 处；`EffectData` 依赖 `Godot.Collections`），成本极高且与"不写第二套规则"冲突。
4. **客户端延迟掩盖应当"现在预留接口、后做动画本体"**：关键前置不是动画系统，而是**让客户端能从"前后快照差异"生成变化事件流**（`SnapshotDiff` 正好是这个底座），以及**乐观执行的输入层**。因此建议把 `SnapshotDiff` 从"待决策"改为【保留并接入为表现层事件源】（详见 §10）。
5. **当前最大的功能性缺口不是性能而是"信息隔离"**：快照把**双方手牌**都下发给了每个客户端（无按玩家过滤）→ 需要 `PerPlayerSnapshot`（详见 §8.3 与 §11 Phase B）。

---

## 1. 术语与判定标准

### 1.1 什么是"真服务端"
判定一个进程是不是"真服务端"，看 4 条硬标准，与"是否链接了渲染引擎"无关：

| 标准 | 含义 | 本项目（Godot headless 服务端） |
|---|---|---|
| ① 权威性 | 状态真相与结算只在服务端发生，客户端不得自行裁决 | ✅ `GameInitializer` 的真实 Manager 全在服务端进程 |
| ② 信任边界 | 客户端输入不可信，服务端必须校验 | ✅ 三层校验：`IntentHub` 身份 → `GodotIntentRouter` 参数/目标合法性 → `EffectEngine` 门禁 |
| ③ 独立进程/可独立部署 | 服务端可与客户端分离部署，客户端崩溃不影响对局 | ✅ `StartServerOnly()` 不建任何客户端投影；客户端崩溃服务端继续（可重连） |
| ④ 客户端可替换 | 换一个客户端实现（或伪造客户端）不能改变结算结果 | ✅ 客户端仅发 `PlayerIntent` / 回 `ChoiceResult`；非法输入被拒 |

**结论**：Godot `--headless`（dedicated server 模式：无窗口、无渲染、无音频）满足以上 4 条 —— **它就是真服务端**。把 Godot 换成纯 .NET 只是**部署形态**的选择，不改变"是不是服务端"。

### 1.2 什么时候才需要"去 Godot 化"
只有出现下列**部署/成本约束**之一时才值得付出去引擎化的代价（否则纯属自找复杂度）：

- 需要一台机器承载大量对局（每实例内存/启动开销成为瓶颈）；
- 容器镜像大小/依赖受限（不允许引擎运行时、要求 <100MB 级镜像）；
- 服务端要嵌入到已有的纯 .NET 微服务平台（统一运维/观测/扩缩容）；
- 需要在无引擎的 CI/压测环境做纯逻辑回归与负载测试；
- 跨语言/跨引擎团队希望服务端与客户端技术栈解耦。

出现上述任一条 → 走 §9.3 的**分步迁移路径**；否则保持 headless 服务端。

---

## 2. 目标 / 非目标 / 设计原则

### 2.1 目标
1. 单机与联机**同一套规则**（一份 Manager 代码，两种驱动）。
2. 服务端权威：所有写操作经意图上行 → 服务端裁决 → 快照回执。
3. 客户端"只投影、不重算"：客户端不跑规则，只重建 UI 与本地交互态。
4. 交互体验优先：决策窗口、效果列表、选目标、拖拽手感尽量与单机对齐。

### 2.2 非目标（当前阶段）
- 不做服务器权威物理/帧同步（本项目是回合制卡牌，事件驱动即可）。
- 不做回滚式预测（rollback netcode）——卡牌游戏用"乐观表现 + 快照校正"已足够。
- 不做引擎替换（去 Godot 化）——除非触发 §1.2 的条件。

### 2.3 设计原则
| 原则 | 说明 |
|---|---|
| **快照即真相** | 客户端的一切模型状态以快照为准；不允许用"实例类型/本地推断"去改写快照内容（历史 BUG 已因此修复） |
| **单一规则实现** | 规则只在 Manager 层；联机不新增规则分支，只用 `IInputGateway` 把"等人决策"从本地弹窗改网络请求 |
| **依赖单向** | `Net/`（DTO/协议）不依赖 Godot；`Net/Godot/`（桥接）可依赖 Manager；Manager 不依赖 UI |
| **表现层只读** | 表现层消费"快照/差异"，绝不反向写 Model |
| **可替换的边界** | 传输（`INetTransport`）、输入（`IInputGateway`）、意图路由（`IIntentRouter`）、模板来源（`ResolveInstance`）均为可替换点 |

---

## 3. 整体架构

### 3.1 进程视图
```
┌─────────────────────────── 进程 1：服务端（Godot headless） ───────────────────────────┐
│ NetMatchEntry(--server) → NetworkMatchLauncher.StartServerOnly()                        │
│   ├─ 传输：GodotEnetTransport(Server) ←→ 客户端                                          │
│   ├─ 意图入口：IntentHub → IIntentRouter(GodotIntentRouter) → PlayerActionService        │
│   ├─ 规则层（真相）：GameInitializer 装配的 Manager 集合                                  │
│   │    ZoneManager / CardManager / TurnManager / StackManager / PlayerManager            │
│   │    AreaManager / WorldDeckManager / CombatManager / EffectEngine / TriggerRegistry…   │
│   ├─ 决策网关：NetworkInputGateway（ChoiceRequest 下行 / ChoiceResult 上行，await 续算） │
│   └─ 快照出口：BuildSnapshot() → SnapshotSerializer → "SNAP:"+JSON 广播给双方            │
│   ✗ 不建任何客户端投影；✗ 不加载 Arena/ArenaOl UI 场景                                    │
└──────────────────────────────────────────────────────────────────────────────────────────┘
                    ▲ PlayerIntent(JSON)                │ IntentResult / ChoiceRequest / SNAP
                    │                                    ▼
┌───────────────── 进程 2/3：客户端 A / B（Godot + 渲染） ─────────────────────────────────┐
│ NetMatchEntry(--client pid) → NetworkMatchLauncher.StartClientOnly()                     │
│   ├─ 模板库：NetClientCardManager（按 TemplateUid 从 .tres 重建 + CardEffectFactory 注入）│
│   ├─ 投影层（独立实例，不驱动规则）：ClientZone/Deck/Player/Stack/Turn/World/Area/CardMgr │
│   ├─ 输入：IntentClient（上行 PlayerIntent）+ 决策面板（回 ChoiceResult）                 │
│   └─ 表现：ArenaOl（快照全量重建）+ 交互（拖拽出牌/暗藏/资产、双击激活、场上高亮选目标、拖拽箭头激活）│
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

### 3.2 单机路径（同一套规则，另一种驱动）
```
Arena（单机 View，事件驱动）
  └─ GameInitializer（同一组合根）→ 同一批 Manager
       └─ 决策：IInputGateway 未注入 → 走本地 IGameUIHooks / ITriggerView（同步弹窗）
```
> 关键：**联机与单机的差异只有两处** —— ① 输入从"本地直调"改为"意图上行"；② 表现从"事件驱动"改为"快照驱动"。规则层完全一致。

### 3.3 分层与依赖方向
```
表现层        Arena / ArenaOl / CardUI / AreaSlot / 各 Window（ChoicePromptPanel…）
   │ 依赖（只读 + 意图）
桥接层        NetworkMatchLauncher / NetMatchEntry / GodotIntentRouter / NetClientCardManager
   │ 依赖
规则层        Manager 集合（Zone/Card/Turn/Stack/Player/Area/WorldDeck/Combat/EffectEngine…）
   │ 依赖（抽象）
协议层        Script/Net：PlayerIntent/IntentResult/ChoiceRequest/ChoiceResult/GameSnapshot/
              Serializer / INetTransport / IInputGateway / IIntentRouter / SnapshotDiff
   ▲ 纯托管，不依赖 Godot（可被 headless 服务端 / 测试 / 未来其它宿主复用）
```

---

## 4. 核心模块清单与职责

### 4.1 组合与业务层（服务端真相）
| 模块 | 职责 | 关键点 |
|---|---|---|
| `GameInitializer` | 组合根：创建并交叉注入全部 Manager；`InitializeAll()`（含 `AllEffects.RegisterAll()`）/`StartMatch()`/`BuildSnapshot()` | 单机与联机共用；`BuildSnapshot` 聚合 Turn/Zone/Stack/WorldDeck/Area/费用修正 |
| `PlayerActionService` | **玩家意图唯一收口**（命令总线）：Pass/PlayCard/DeployCovert/EstablishAsset/ActivateAbility/… | 联机时它的唯一网络入口是 `IntentHub`；单机可直接调用 |
| `TurnManager` | 回合/阶段/优先权/原子锁；触发阶段同步版与**异步版**（`RunTriggerPhaseAsync`）；`ToSnapshot/ApplySnapshot` | 异步版经 `IInputGateway` 请求"触发排序/选目标"，是联机决策阻塞的核心 |
| `ZoneManager` | 区域真相（手牌/牌库/墓地/资产/争夺区/附属/封印/分数/秘社/堆叠）；`ToSnapshot/ApplySnapshot` | 客户端投影靠 `ApplySnapshot` 重建；**快照即真相**（禁止按类型改写区域） |
| `CardManager` | 卡实例注册/创建/移动/伤害；`GetEffectData`（玩家卡+秘社选项）；`RebuildFromRef`/`ResolveInstance` | `ResolveInstance` 是**跨进程可替换点**（客户端重载为"从模板建实例"） |
| `EffectEngine` | 效果执行（激活/触发/持续）、费用修正栈、准备维护（PayOrTap） | 激活门禁（原子锁/优先权/窗口/条件/费用）+ 目标必填校验 |
| `StackManager` | 堆叠与结算；`ToSnapshot/ApplySnapshot` | 卡片型条目按 `SourceUid` 解析 |
| `PlayerManager` | 法力（派生自资产区）、先手、判负、分数 | `AvailableMana` 是**派生值**，客户端重建资产区后自动正确 |
| `AreaManager` / `WorldDeckManager` | 地区注册表/势力标记、世界牌堆 | 跨进程靠 `ZoneCardRef(含 TemplateUid)` 重建 |
| `CombatManager` | 调查/战斗/破平对抗（联机走 ChoiceRequest） | 窗口归属：**胜者**操作 |
| 轻量规则层 `TriggerRegistry`/`TriggerEmitPoints`/`ActivateEligibility`/`TargetValidity` | 触发收集、激活可发动判定、目标合法性 | `ActivateEligibility` 被**服务端校验**与**客户端高亮/拖拽起手**共用（生效区判定已静态化，客户端无 `EffectEngine` 实例也能用） |

### 4.2 协议层（`Script/Net`，纯托管）
| 模块 | 职责 |
|---|---|
| `PlayerIntent` / `IntentResult` | 上行意图 DTO（含 `Kind` 枚举与字符串参数）+ 服务端裁决回执（`Success/Error/AssignedUids`） |
| `ChoiceRequest` / `ChoiceResult` | 服务端→客户端的"请决策"请求（PayOrTap/SelectTarget/SelectTriggerOrder/ForceChoice/CombatInvestigation/CombatStrength/TieBreak）+ 客户端回包 |
| `IInputGateway` | **统一决策抽象**：单机不注入（本地窗口）；联机注入 `NetworkInputGateway`（网络 await） |
| `GameSnapshot` / `ZoneCardRef` / `PlayerSnapshot` / `StackEntrySnap` / `CostModSnap` / `RandomSnapshot` | 整局状态投影 DTO |
| `SnapshotSerializer` / `ChoiceSerializer` / `IntentSerializer` | System.Text.Json（`IncludeFields=true`，字段风格 DTO） |
| `INetTransport` / `GodotEnetTransport` / `LoopbackTransport` | 传输抽象；ENet（跨进程）/回环（单进程测试） |
| `IntentHub` / `IIntentRouter` | 服务端意图入口：身份校验（`intent.PlayerId == 连接映射`）+ 路由到 `GodotIntentRouter` |
| `NetworkInputGateway` | 服务端决策网关：`ChoiceRequest` 下行 + 按 `RequestId` 匹配回包 + 30s 超时兜底 |
| `SnapshotDiff` / `RenderCommand` | 前后快照差异 → 增量渲染指令（**当前无消费者，建议转为表现层事件源**，见 §10） |
| `EventToMessageMapper` / `RenderEvent` | 渲染事件 → 网络消息（**当前零引用，建议下线**） |
| `ReconnectManager` / `DeterministicRandom` / `RandomSourceHub` | 断线重连载荷与可复原随机源（**重连已决定延后**） |

### 4.3 桥接层（`Script/Net/Godot`）
| 模块 | 职责 |
|---|---|
| `NetworkMatchLauncher` | 装配服务端侧（`StartServerOnly`）/客户端侧（`StartClientOnly`）/单进程回环（`StartLocalLoopback`）；消息分发；快照广播；暴露客户端投影只读访问器 |
| `NetMatchEntry` | 命令行进程入口（`--server` / `--client <port> <pid>`）；服务端连接后推初始快照；客户端挂 `ArenaOl` |
| `GodotIntentRouter` | `IIntentRouter` 的 Godot 侧实现：把纯数据意图解析为实例/效果并调用 `PlayerActionService`；**含激活目标合法性复查** |
| `NetClientCardManager` | 跨进程客户端独立 `CardManager`：模板库 + `ResolveInstance` 重建 + **效果构建器注册与注入** |

### 4.4 表现层
| 模块 | 职责 |
|---|---|
| `Arena`（单机） | 事件驱动 View：订阅 Manager 事件做增量刷新；激活走"拖拽箭头 + 目标高亮"；附属/秘社走双击弹窗 |
| `ArenaOl`（联机） | 快照驱动 View：`ApplySnapshot` **全量重建**（手牌/场地/秘社/附属/分数/堆叠/地区标记）+ 交互（拖拽出牌、暗藏、资产、双击效果列表、场上高亮选目标、拖拽箭头激活、激活高亮、费用修正显示、拒绝提示窗） |
| 窗口/面板 | `ChoicePromptPanel`（服务端决策）、`OnlineActivationChoiceWindow`（联机效果选择，点窗外不关闭）、`OnlineNoticeWindow`（拒绝提示）、`TargetSelectionWindow`（复用单机，场上选目标提示） |

---

## 5. 协议设计

### 5.1 消息类型
| 方向 | 消息 | 载体 |
|---|---|---|
| C→S | `PlayerIntent`（Pass/PlayCard/DeployCovert/RevealCovert/EstablishAsset/ReturnCardToDeck/ReturnCardToHand/DrawCards/DealDamage/DealAreaDamage/Concede/ActivateAbility/ResetActionCharges/DebugCommand） | JSON |
| C→S | `ChoiceResult`（对决策请求的回复） | JSON |
| S→C | `IntentResult`（意图裁决回执） | JSON |
| S→C | `ChoiceRequest`（请决策） | JSON |
| S→C | 整局快照 | `"SNAP:" + JSON` |

### 5.2 帧格式现状与 **v2 信封建议**【待做】
- 现状：只有快照有前缀 `SNAP:`；`ChoiceRequest/ChoiceResult` 与 `IntentResult` **靠"形状"识别**（有 `RequestId` 视为选择消息，否则视为意图结果）。这在字段演进时是**脆弱点**（例如未来 `IntentResult` 增加 `RequestId` 语义就会误判）。
- 建议 v2：统一类型信封 `{"t":"intent|intent_result|choice_req|choice_res|snap","p":{…}}`，与旧格式**并存一个版本周期**（收到无 `t` 的旧格式走现状分支），避免一次性破坏性升级。

### 5.3 关联与超时
- 关联：`PlayerIntent.IntentId`（上行→回执）；`ChoiceRequest.RequestId`（下行→回包，`NetworkInputGateway` 用字典匹配）。
- 超时：`ChoiceRequest` 目前**硬编码 30s**，超时按"取消"续算。
  - 风险：玩家掉线时服务端该局会**挂起最长 30s**；且不同决策类型（选目标 vs 破平确认）应有不同超时。
  - 建议：超时按 `ChoiceRequest.Kind` 配置化 + 掉线立即取消（等 §11 Phase D 重连一并做）。

### 5.4 序列化与纯度纪律
- 统一 `System.Text.Json` + `IncludeFields = true`（DTO 用 public 字段）。
- `Script/Net`（除 `Godot/` 子目录）**不得** `using Godot / Script.UI / Script.Game.Entities / Script.Manager`（`GodotIntentRouter` 等桥接层例外，放在 `Net/Godot`）。

---

## 6. 关键流程

### 6.1 启动与开局
```
服务端：NetMatchEntry(--server)
  ├─ 找到/初始化 GameInitializer（InitializeAll：AllEffects.RegisterAll → 建 Manager → 交叉注入）
  ├─ StartMatch()（发初始手牌）+ SetupInitialAreas(3)
  └─ StartServerOnly()（挂 IntentHub/网关/传输；监听端口）
客户端：NetMatchEntry(--client p)
  ├─ StartClientOnly()（独立投影 Manager + NetClientCardManager + IntentClient）
  ├─ 挂载 ArenaOl 场景 + AttachLauncher（订阅 OnClientSnapshot / OnClientChoiceRequest / OnClientIntentRejected）
  └─ 等服务端快照 → ApplySnapshot（全量重建）
服务端：检测到玩家连接 → PushSnapshot()（后到的玩家2 再补推一次）
```

### 6.2 出牌 / 暗藏 / 建资产（主动意图）
```
ArenaOl 拖拽结束（判定落点：目标地区/目标卡）
  → IntentClient.SendIntent(PlayCard, cardUid, targetAreaUid, targetCardUid)
  → 服务端 OnServerMessage → IntentHub.HandleIntent（身份校验）
  → GodotIntentRouter → PlayerActionService.PlayCard → TurnManager/ZoneManager/CardManager/EffectEngine 结算
  → 回 IntentResult（失败带 Error → ArenaOl 弹 OnlineNoticeWindow）
  → PushSnapshot() 广播 → 各客户端 ApplyClientSnapshot → ArenaOl 重建 UI
```

### 6.3 决策阻塞（服务端 await 客户端）【已实现】
触发点与窗口归属：
| 场景 | 触发处 | 请求 Kind | 归属玩家 |
|---|---|---|---|
| 触发阶段排序 | `TurnManager.RunTriggerPhaseAsync` | `SelectTriggerOrder` | 该批触发的**来源卡控制者**（按控制者分组） |
| 触发需选目标 | `TurnManager.EnsureTargetThenPushAsync` | `SelectTarget`（强制） | 来源卡控制者 |
| 准备维护"支付/横置" | `EffectEngine.ExecuteVerbAsync` | `PayOrTap`（强制） | 来源卡控制者 |
| 调查对抗分配牌库顶 | `CombatManager.ResolveAreaCombatAsync` | `CombatInvestigation` | **胜者** |
| 战斗对抗分配伤害 | `CombatManager` | `CombatStrength` | **胜者** |
| 先手破平确认 | `CombatManager` | `TieBreak` | 先手玩家 |
流程：服务端 `await InputGateway.RequestChoice(playerId, req)` → 客户端 `OnClientChoiceRequest` → 面板（`ChoicePromptPanel`）→ `SendChoiceResult` → 服务端 `TryHandleResult` 完成 await → 续算 → 异步流程结束回调 `OnAsyncSettlementCompleted → PushSnapshot`。

### 6.4 效果激活（客户端选效果 / 服务端执行）【已实现】
```
双击附属/秘社/角色卡（或拖拽箭头激活）→ 客户端本地收集可激活效果
  ├─ 单效果 / 效果列表（OnlineActivationChoiceWindow：置灰不可用项 + 取消；点窗外不关闭）
  ├─ 需目标的效果 → 场上高亮合法目标（ActivateEligibility）→ 点击/落点选定
  └─ 上行 ActivateAbility(sourceUid, effectId, targetUid)
服务端：CardManager.GetEffectData（玩家卡 EffectDataList / 秘社 ActivationOptions）
  → GodotIntentRouter 目标合法性复查（G4）
  → EffectEngine.ActivateAbility（原子锁/优先权/窗口/激活条件/费用 → 入堆叠）
  → 快照回显
```
> 客户端"效果列表/置灰/合法目标"依赖模板上的 `EffectDataList/TargetRules`；跨进程客户端由 `NetClientCardManager` 在模板库构建时**注册并注入**（与单机同一份构建器 → EffectId 一致）。

### 6.5 快照下行与投影重建顺序【已实现】
`ApplyClientSnapshot` 严格按依赖顺序：
```
1) 区域桶（卡的位置真相）        _clientZone.ApplySnapshot(Zones)
2) 世界牌库（地区牌堆）          _clientWorld.ApplySnapshotUids(WorldDeckCards)
3) 玩家（胜负标记）             _clientPlayer.ApplySnapshot(Players)
4) 堆叠                        _clientStack.ApplySnapshot(Stack)
5) 回合（阶段/优先权/先手/回合数）_clientTurn.ApplySnapshot(snap) + SetFirstPlayer
6) 地区（注册表 + 势力标记）     _clientArea.ApplySnapshot(AreaCards, AreaMarks)
7) 费用修正（展示用）           _clientCostMods
→ OnClientSnapshot → ArenaOl.ApplySnapshot（全量重建 + 可发动高亮刷新）
```

### 6.6 本地交互态与模态纪律【已实现】
- 联机窗口**点窗外不关闭**；取消只经显式按钮（`OnlineActivationChoiceWindow`/`OnlineNoticeWindow`/`ChoicePromptPanel`）。
- `ArenaOl` 用 `_activationSelectionBusy` / `_activationTargetActive` / `_noticeWindowOpen` 做本地模态：期间拦截让过、结束回合、手牌拖拽、重复双击、重复起手。
- 选目标/激活拖拽期间 `ArenaOl._Input` 优先吞事件，避免与手牌拖拽争抢。

---

## 7. 权威与安全边界

### 7.1 三层校验【已实现】
| 层 | 位置 | 校验内容 |
|---|---|---|
| ① 身份 | `IntentHub.HandleIntent` | `intent.PlayerId` 必须等于连接映射（防越权代操作） |
| ② 参数/目标 | `GodotIntentRouter` | 实例/效果可解析；**激活需目标时**目标非空且 `ActivateEligibility.IsLegalActivateTarget` 通过 |
| ③ 规则门禁 | `EffectEngine` / `TurnManager` | 原子锁、优先权、行动窗口、激活条件（横置/牺牲）、费用、堆叠熔断 |

### 7.2 现有缺口
| 缺口 | 影响 | 建议 |
|---|---|---|
| **快照不按玩家过滤** | **双方手牌/牌库对客户端全可见**（隐藏信息泄露） | 【待做】`PerPlayerSnapshot(playerId)`：对手手牌只发"数量 + 卡背"，牌库只发剩余数；客户端投影相应改为"未知占位卡" |
| `ChoiceResult` 无身份复核 | 依赖传输层映射（当前可接受） | 回包时携带并校验 `PlayerId`（低成本加固） |
| 无频率/重放校验 | 客户端连点可能导致重复意图 | 服务端按 `IntentId` 去重 + 每玩家最小间隔 |
| 30s 固定超时 | 掉线时对局挂起 | 按 Kind 配置 + 掉线立即取消（与重连合并做） |

---

## 8. 服务端形态：Godot headless vs 纯托管（对应第 3 问）

### 8.1 Godot headless 的定位与代价
- **是**：官方支持的服务端运行模式（`--headless`，无窗口/无渲染/无音频；可用同一工程或 export 成 dedicated server 模板）。规则、裁决、真相都在该进程 → 满足 §1.1 四条标准。
- **代价**（务实评估）：进程启动与内存开销高于纯 .NET（引擎初始化 + Mono 运行时）；容器需带 Godot 运行时（镜像更大）；单机进程承载多对局需要多实例（当前是"一进程一局"）。
- **结论**：当前规模完全够用；**不要去 Godot 化**。

### 8.2 纯托管 `ServerCore/` 现状（为什么现在不可用）
- 只编译 `Script/Enums` + `Script/Net`（排除 Godot 子目录与 `InputGateways.cs`）+ `DeterministicRandom/IRandomSource`；**不含任何 Manager/规则层**。
- 意图路由是**桩** `EchoIntentRouter`（只验身份与分发，不结算）。
- 有价值的部分只有 `CardTemplateRepository`（读 Godot 导出的 `Cards/card_templates.json`，700+ 张，按 UID 反查）。
- TFM 为 `net10.0`，与主工程 `net8.0` 不一致（属实验性产物）。

### 8.3 若将来要去 Godot 化：分步迁移路径【仅在触发 §1.2 时执行】
1. **抽象日志/时间/集合**：把 `GD.Print` 等换成 `ILog`，把 `Godot.Collections.Array<T>` 换成普通集合（或保留但提供纯托管视图）；`GodotObject`/`Node` 依赖收敛到少数适配器。
2. **Manager 去 Node 化**：`ZoneManager`/`CardManager`/`TurnManager` 从 `Node` 派生改为普通类 + 显式生命周期（`Initialize/Dispose`），Godot 侧用薄壳节点承载。
3. **效果数据来源改造**：`EffectData` 由 C# 构建器生成 → 额外提供"导出为纯托管 JSON/二进制"的构建产物，纯托管侧只读数据不编译卡脚本。
4. **实例创建**：`ServerCore` 侧重载 `ResolveInstance`，从 `CardTemplateData` 建 `ServerCardInstance`（补齐与 `CardInstance` 的字段/事件对齐）。
5. **路由落地**：实现真实 `IIntentRouter` 替换 `EchoIntentRouter`，接上 Zone/Turn/Stack/Effect 全链路。
6. **传输与主循环**：选 socket（ENet 有纯 .NET 绑定或换 Kestrel/WebSocket）+ 帧调度 + 快照广播 + `ChoiceRequest` 回环。
7. **一致性验收**：同一份"意图脚本"在 Godot headless 与纯托管服务端跑出**逐字段一致**的快照（回归作为门禁）。

> 成本量级：中大型重构（牵动 Manager/效果/触发全链路），**不建议在没有明确部署约束时启动**。

---

## 9. 客户端表现层与"延迟掩盖"设计（对应第 2 问）

### 9.1 延迟从哪来（先量化）
| 来源 | 典型量级 | 玩家感知 |
|---|---|---|
| 意图 RTT（上行→裁决→快照下行） | 局域网 <10ms；公网 30–150ms | "我出的牌没动"的迟钝感 |
| 服务端结算耗时 | 通常 <20ms；复杂触发链/对抗可达数百 ms | 卡顿、界面"冻住" |
| 决策窗口等待（对手） | 秒级（人为） | 需要"对手正在思考"的反馈 |
| 快照广播与全量重建 | 重建 UI 数十 ms（节点重建 + 图片加载） | 交互被打断（拖拽/高亮丢失） |
| 资源加载（卡图/场景） | 首次命中较慢（701 张模板） | 首屏/抽牌闪白 |

### 9.2 三层模型（**核心设计**）
```
输入层（Local Intent Layer）  —— 乐观执行
   我的操作：立刻在"本地表现"上演（飞卡/占位/费用预览/高亮），并同时上行意图
        ↓（权威校正）
表现层（Presentation Layer） —— 动画与插值
   消费"变化事件流"（来自 前后快照 diff），排队播放动画；动画期间可暂缓下一次快照应用
        ↓（真相）
权威层（Authority Layer）    —— 快照
   服务端快照到达 → 校正模型 → 若与乐观表现冲突则回滚表现 + 拒绝提示（已有 OnlineNoticeWindow）
```
**铁律**：乐观执行只允许改"表现"，**绝不写 Model**；一切以快照为准。

### 9.3 落地方式（分阶段；**建议现在只做"预留"，动画本体后续做**）
**现在就要做（低成本、否则后面返工）**：
1. **保留上一份快照**：`ArenaOl`/launcher 保存 `_lastSnapshot`，每次快照到达先 `SnapshotDiff.Diff(last, next)` 产出 `RenderCommand` 列表 —— 这就是**动画的触发源**（AddCard=抽牌动画、MoveCard=飞卡、UpdateCard=数值跳动、StackPush/Pop=堆叠动画、PhaseChange=阶段提示、PlayerUpdate=法力变化）。
2. **定义表现事件接口**：新增 `IPresentationSink`（或 `ArenaOl` 内的 `OnRenderCommand(cmd)` 分发），先把 `RenderCommand` 路由到"现有重建分支"，为后续替换成"动画分支"留缝。
3. **快照应用节流/合并窗口**：同一帧内多次快照只应用最后一次；动画进行中允许**延后应用**（有超时上限，避免长期不同步）。
4. **乐观执行最小集**：仅对"本地可见且可回滚"的操作先做：出牌飞卡 + 卡位占位、暗藏落位、费用扣减预览（本地数字）、激活/拖拽的高亮。**不做**完整 rollback 预测。
5. **预加载**：牌组确定后预热本局可能用到的卡图（`ResourceLoader` 已缓存，建议显式预热 + 进度反馈）；这一步与"假加载"天然衔接。

**后续再做（明确不现在做）**：
- 完整动画系统（Tween/AnimationPlayer 编排、动画队列优先级、可跳过/加速）、
- 回滚式状态预测（rollback）、
- "假加载/匹配演出"（只有真的需要匹配阶段时才有意义）。

### 9.4 与两个待决策模块的关系（**回答"是否可以提前考虑"：可以，而且应当**）
- **`SnapshotDiff` → 从"待决策（保留冻结）"改为【保留并接入为表现层事件源】**：它不需要网络消息、不需要服务端参与，**纯客户端本地使用**，且已有 `NetP3BTests` 覆盖。接入它是最小代价的"为动画留缝"。
- **模块D（`EventToMessageMapper`）→ 仍建议下线**：动画所需的"变化"客户端可由本地 diff 得到；服务端不必为表现层额外发事件消息。（唯一例外：将来做"信息隔离"后，需要让客户端知道"对手做了一个你不知道内容的动作"——那更适合放进快照的**事件摘要字段**，而不是恢复整条事件消息通道。）

---

## 10. 分阶段路线图

| 阶段 | 目标 | 主要改动 | 验收 |
|---|---|---|---|
| **Phase A（现状收口）** | 把已实现能力与文档对齐，清理死代码 | 三模块去留执行（见 §12）；`SnapshotDiff` 接入为事件源 | build 0/0 + 33 套件全绿 + 三进程冒烟 |
| **Phase B（信息隔离 + 协议加固）** | 隐藏信息不泄露；协议可演进 | `PerPlayerSnapshot` 过滤（对手手牌/牌库）；消息类型信封 v2；超时按 Kind 配置 | 单测：快照按玩家过滤断言；联机：无法看到对手手牌 |
| **Phase C（表现层与延迟掩盖）** | 交互顺滑、延时被动画吸收 | 动画队列（消费 `RenderCommand`）；乐观执行最小集；快照节流/合并；卡图预热 | 公网模拟 150ms 延迟下：出牌/激活"点击即有反馈"，无交互中断 |
| **Phase D（可选：重连 / 服务端形态）** | 掉线可恢复；如需再谈去 Godot 化 | `ReconnectManager` 接线（已决定延后）；§8.3 迁移路径（仅触发条件出现时） | 断网 30s 重连恢复一致快照 |

---

## 11. 设计红线（评审用 checklist）

1. 规则只能有一份：联机不得新增规则分支；差异只在"输入来源"与"表现驱动"。
2. 客户端不重算规则：投影只做状态恢复（`ApplySnapshot`），不做结算。
3. 快照即真相：客户端不得按类型/本地推断改写快照内容。
4. 表现层只读：动画/乐观执行不得写 Model；冲突以快照校正 + 提示。
5. 依赖单向：`Net/` 不依赖 Godot；Manager 不依赖 UI；桥接层在 `Net/Godot`。
6. 窗口纪律：联机窗口点窗外不关闭；取消必须显式；强制决策不可取消。
7. 每个新协议字段都要考虑"旧客户端兼容"（字段加法优先，语义变更走信封 v2）。

---

## 12. 待确认决策（请指定后我据此实施）

1. **三个技术模块去向**：
   - `EventToMessageMapper`（模块D）：建议 **下线删除**。
   - `SnapshotDiff/RenderCommand`：建议 **保留并接入为表现层事件源**（§9.4 的预留第 1–2 项）。
   - `ServerCore/`：建议 **(b) 降级保留**（仅留 `CardTemplateRepository`，删桩路由与 `ServerCardInstance`），或 (a) 整体移除。
2. **是否现在做 §9.3 的"预留 5 项"**（保留上快照 + 表现事件接口 + 快照节流 + 乐观执行最小集 + 卡图预热）？建议**现在做前 2 项 + 第 3 项**，乐观执行与预加载放到 Phase C。
3. **信息隔离（Phase B）是否提前**？它既是安全性问题也是"动画/信息可见性"的前提（对手手牌不可见 → 动画只能用"卡背/数量"表现）。建议**提前到 Phase B 首位**。
4. **服务端形态**：确认维持 **Godot headless**，`ServerCore/` 不推进（除非 §1.2 条件出现）。
