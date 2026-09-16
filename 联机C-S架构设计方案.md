# UWPro 联机（C/S）架构设计方案

> 用途：把"当前已落地的联机实现"整理成**一份可执行的设计说明**：核心模块、整体架构、关键流程、职责划分、权威与安全边界、客户端延迟掩盖设计，以及"服务端形态"的取舍结论。
> 状态：设计说明（基于 2026-09-05 代码实况整理）；文中标注【已实现】/【待做】/【已决策】。
> 关联文档：`C-S迁移进度与批次计划.md`（迁移技术侧进度）、`联机对战完整实现最终缺口清单.md`（玩法完整度缺口）。
> **决策落实**：§12 的四项决策已确认并在本轮执行完毕，见 **§13 决策落实记录**。

---

## 0. 结论摘要（TL;DR）

1. **C/S 化核心已完成**：单机与联机**共用同一套规则**（Manager 层），联机只多了一层"网络输入网关 + 快照投影"，不存在第二套规则实现。
2. **服务端 = Godot headless 进程**（`NetEntry.tscn --server`）就已经是**真正的服务端**：规则、裁决、状态真相全在服务端进程，客户端只是投影 + 输入。**"不把 Godot 去掉"不等于"不是真服务端"**（详见 §8）。已确认**维持 headless，不推进去 Godot 化**。
3. **纯托管 `ServerCore/` 路线已整体移除**（2026-09-05，决策 (a)）：原骨架只编译 Enums+Net+随机源、意图路由是桩，无法承载规则；去 Godot 化的成本与"不写第二套规则"冲突（`ZoneManager`/`CardManager`/`TurnManager` 均为 `Node`；`EffectEngine` 97 处 `GD.*`、`TurnManager` 132 处；`EffectData` 依赖 `Godot.Collections`）。若将来出现 §1.2 的部署约束，按 §8.3 路径重启。
4. **客户端延迟掩盖"预留已落地"**：`SnapshotApplicationQueue`（同帧合并 + 可延后 + 与上一份快照配对）+ `ArenaOl.RouteRenderCommands`（表现层事件源）已实现；动画本体与乐观执行留待 Phase C（§9）。
5. **信息隔离已不再是缺口**：Phase B 已完成——客户端只看到自己的手牌/牌库内容，对手手牌/牌库与地区牌堆只发占位（数量保留、身份隐藏，见 §7.2）。

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
| `SnapshotDiff` / `RenderCommand` / `SnapshotApplicationQueue` | 前后快照差异 → 增量渲染指令；应用队列负责"同帧合并 + 延后应用 + 与上一份已应用快照配对"。**【已接入】**：ArenaOl 消费差异作为表现层（动画）事件源（见 §9.4） |
| `EventToMessageMapper` / `RenderEvent` | 渲染事件 → 网络消息（模块D）。**【已移除】**（2026-09-05：客户端本地 diff 即可获得变化，无需服务端下发事件消息） |
| `ReconnectManager` / `DeterministicRandom` / `RandomSourceHub` | 断线重连载荷与可复原随机源（**重连已决定延后**，库保留就绪） |

### 4.3 桥接层（`Script/Net/Godot`）
| 模块 | 职责 |
|---|---|
| `NetworkMatchLauncher` | 装配服务端侧（`StartServerOnly`）/客户端侧（`StartClientOnly`）/单进程回环（`StartLocalLoopback`）；消息分发；快照广播；暴露客户端投影只读访问器 |
| `NetMatchEntry` | 命令行进程入口（`--server [port]` / `--client <port> <pid> [host]` / `--host <ip>`）；服务端连接后推初始快照并打印本机候选 IPv4；客户端挂 `ArenaOl`。**局域网对战**：客户端 `host` 填服务端电脑 IP（默认回环）；服务端按连接先后分配玩家编号（先连＝玩家1） |
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

### 5.2 帧格式：类型信封 v2【✅ 已实现（Phase B）】
- **现状（v2）**：新增 `Script/Net/NetEnvelope`，报文格式 `V2:<type>|<payloadJson>`，`type ∈ {intent, intent_result, choice_req, choice_res}`；分隔符只取**第一个** `|`，故 JSON 载荷内可含 `|`。
  - 生产链路已全部改用信封：客户端上行意图/回包（`IntentClient.SendIntent`、`NetworkMatchLauncher.SendChoiceResult`）、服务端下行回执/请决策（`OnServerMessage` 回执、`NetworkInputGateway.RequestChoice`）。
  - 快照沿用既有 `SNAP:` 前缀（其本身已是类型标签），不重复包裹。
- **兼容策略（双读一个版本周期）**：接收方先 `NetEnvelope.TryUnwrap`；失败（旧客户端/旧格式）→ 回退原有"形状识别"分支，行为与之前完全一致。`NetworkInputGateway.TryHandleResult` 信封/裸 JSON 均可接受。
- **动机**：原先 `ChoiceRequest/ChoiceResult` 与 `IntentResult` 靠"是否有 `RequestId`"猜测类型，字段演进时易误判（例：`IntentResult` 将来若引入 `RequestId` 就会串台）。

### 5.3 关联与超时【✅ 已实现（Phase B：按 Kind 配置化）】
- 关联：`PlayerIntent.IntentId`（上行→回执）；`ChoiceRequest.RequestId`（下行→回包，`NetworkInputGateway` 用字典匹配）；超时回包**带 RequestId** 便于对账。
- 超时（`NetworkInputGateway.TimeoutSecondsFor(kind)`，可用 `TimeoutProvider` 覆盖，返回 ≤0 回退默认）：

  | Kind | 超时 | 说明 |
  |---|---|---|
  | `SelectTarget` / `SelectTriggerOrder` | 60s | 需玩家思考、可能要浏览牌面 |
  | `CombatInvestigation` / `CombatStrength` | 60s | 对抗分配（多点选/分配） |
  | `ForceChoice` / `PayOrTap` | 45s | 强制二选一 |
  | `TieBreak` / 其它 | 30s（默认） | 简单确认 |

- 超时语义：以 `ChoiceResult{ Cancelled = true, RequestId = 已请求值 }` 续算（与"玩家放弃"一致）。
- 仍待做：**掉线立即取消**（当前仍需等超时），与 §11 Phase D 的重连一并处理。

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

### 7.2 缺口与状态（Phase B 后）
| 缺口 | 状态 | 说明 |
|---|---|---|
| 快照不按玩家过滤（隐藏信息泄露） | ✅ **已修复（Phase B）** | 新增 `SnapshotFilter.ForPlayer`：对手手牌/牌库与地区牌堆只下发**占位引用**（`ZoneCardRef.IsHidden`），客户端据此建"无模板占位实例"（`CardInstance.IsHidden`，只画卡背、数量与顺序保留、**不携带运行时状态**）；`PushSnapshot` 改为**逐玩家**过滤下发，`GameInitializer.BuildSnapshotFor(playerId)` 为入口；服务端侧仍保留完整真相（`OnServerSnapshot`） |
| `ChoiceResult` 无身份复核 | ⏳ 待做 | 依赖传输层映射（当前可接受）；建议回包携带并校验 `PlayerId`（低成本加固） |
| 无频率/重放校验 | ⏳ 待做 | 服务端按 `IntentId` 去重 + 每玩家最小间隔 |
| 固定 30s 超时 | ✅ **已修复（Phase B）** | 按 `ChoiceRequest.Kind` 配置化（见 §5.3）；仍待"掉线立即取消"（并入 Phase D） |
| 事务卡（需目标出牌）联机缺少选目标交互 | ✅ **已修复（2026-09-05）** | `ArenaOl` 拖出未命中目标时进入"场上高亮 + 复用单机 `TargetSelectionWindow`"选目标，选完上行 `PlayCard(cardUid, null, targetUid)`（对齐单机 `BeginSorceryTargetSelection`）；费用不足/无合法目标则不弹窗、不发意图 |
| 联机不显示地区"调查/战斗/势力"统计 | ✅ **已修复（2026-09-05）** | `ArenaOl.RebuildAreas` 注入 `SetStatsDependencies(ClientZoneManager, ClientPlayerManager)` → 复用 `AreaStatsCalculator`（与单机同源规则） |
| 联机堆叠区 UI 未对齐单机 | ✅ **已修复（2026-09-05）** | `ArenaOl.RefreshStackUI`：卡片型显示真实卡面；效果型显示文本 + 按 `SourceTemplateUid` 显示来源卡图；`AreaWon` 条目挂地区卡面；按条目 `Targets` 绘制"条目→目标"箭头（按控制者着色、每帧跟随）。快照补 `StackEntrySnap.{ControllerId, WonAreaUid, SourceTemplateUid, Targets}` |
| 联机调查/战斗对抗 UI 与单机不一致 | ✅ **已修复（2026-09-05）** | 联机决策**复用单机窗口**：`StrengthWindow`（点场上卡左键+1/右键-1；联机不本地结算 → 回包 `ChoiceResult`）、`InvestigationWindow`（拖拽分配放回牌堆顶/底）、`TieBreakWindow`（确认/取消）；`ChoiceRequest` 补 `AreaUid/AreaNo/LoserPlayerId/CombatTypeValue`；待分配调查卡经快照**仅下发给决策者**；服务端对战斗分配做**合法性复检** |

> 信息隔离的**副作用（有意为之）**：客户端只能看到"对手手牌数量"与"牌库剩余数"，因此**动画/表现层只能演可见信息**（这正是 Phase C 做动画的前提）。
> 兼容性：单机与既有本地快照链路不受影响（`IsHidden` 默认 false）；`WorldDeckManager`/`ZoneManager` 的投影导出会保留 `IsHidden` 标记，便于自省与断言。

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

### 9.3 落地方式（分阶段；**预留 1–3 已实施，动画本体后续做**）
**已实施（2026-09-05，见 §13）**：
1. ✅ **保留上一份快照 + 差异产出**：新增 `Script/Net/SnapshotApplicationQueue`（`Enqueue/TryDequeue(prev,next)/MarkApplied/Reset`），`ArenaOl` 在应用快照时用 `SnapshotDiff.Diff(prev, next)` 产出 `RenderCommand` 列表 —— 即**动画触发源**（AddCard=抽牌动画、MoveCard=飞卡、UpdateCard=数值跳动、StackPush/Pop=堆叠动画、PhaseChange=阶段提示、PlayerUpdate=法力变化）。
2. ✅ **表现事件接入点**：`ArenaOl.RouteRenderCommands(cmds)` 作为表现层唯一入口（当前为分类计数 + `LastPresentationSummary`，可开 `VerbosePresentation` 打日志）；后续动画系统只替换该方法内部实现，不动快照链路。
3. ✅ **快照合并/延后窗口**：同帧多份快照只应用最后一份（`CoalescedCount` 可观测）；本地交互/动画期间 `IsDeferred=true` 延后应用，**上限 1.5s**（`SnapshotMaxDeferSeconds`）后强制应用，避免长期不同步。
   > ⚠️ **该延后窗口是未经确认的占位假设**（默认"动画会阻塞快照应用"）。动画规格确定后需复核：若采用"不阻塞/可并发"，则应改为不阻塞或重新定阈值——见 §9.5。
4. ⏳ **乐观执行最小集**（Phase C）：仅对"本地可见且可回滚"的操作：出牌飞卡 + 卡位占位、暗藏落位、费用扣减预览、激活/拖拽高亮。**不做**完整 rollback 预测。
5. ⏳ **预加载**（Phase C）：牌组确定后预热本局卡图（`ResourceLoader` 已有缓存）；与"假加载"天然衔接。

**后续再做（明确不现在做）**：
- 完整动画系统（Tween/AnimationPlayer 编排、动画队列优先级、可跳过/加速）、
- 回滚式状态预测（rollback）、
- "假加载/匹配演出"（只有真的需要匹配阶段时才有意义）。

### 9.4 与两个相关模块的关系（**已决策，2026-09-05**）
- **`SnapshotDiff` → 【保留并接入为表现层事件源】（已完成）**：不需要网络消息、不需要服务端参与，**纯客户端本地使用**；`NetP3BTests` 新增 3 例覆盖队列（合并/配对/延后）。
- **模块D（`EventToMessageMapper`/`RenderEvent`）→ 【已下线删除】（已完成）**：动画所需的"变化"客户端由本地 diff 得到，服务端不必为表现层额外发事件消息。（唯一例外：将来做"信息隔离"后要表达"对手做了一个内容未知的动作"——更适合放进快照的**事件摘要字段**，而不是恢复整条事件通道。）

### 9.5 动画设计规格（**待用户指定；未指定前不得实施 Phase C**）

> 状态说明：截至 2026-09-05，**动画的"内容与制作细节"尚未经用户确认**。§9.3 只完成了"预留接口"（差异指令可作为动画触发源），**不等于已具备开工条件**。下列问题需逐条给出结论后方可进入实现。

**A. 动画清单（做哪些）**：抽牌 / 出牌落场 / 暗藏落位与翻面 / 横置与重置 / 伤害与数值变化 / 消灭入墓 / 结附与封印 / 入栈与结算 / 地区夺取与势力标记 / 阶段与优先权提示 / 回合起止 / 胜负结算 / 费用与减费提示 / 激活效果命中提示。（可只勾选其中一部分，其余不做。）

**B. 与"全量重建"的关系（最关键的技术前提）**
- 方案 ①（建议先行）：**保留全量重建 + 只做"轻量出现类"动画**（淡入/缩放/数值跳动/翻面），不做跨容器位移 —— 实现简单、与重建天然兼容。
- 方案 ②：改为**增量应用**（按 `RenderCommand` 局部更新节点）以支持"飞卡/位移"动画 —— 需把 `ArenaOl` 的重建分支改造成增量版，工作量与风险显著更大。

**C. 时序与节奏**：单条动画时长区间（建议 0.15–0.4s）；串行队列 vs 允许并发；整批总时长上限（超过则压缩/合并）；是否支持"点击跳过单条/全部"与全局速度（1x/2x）。

**D. 播放期间的交互**：动画期间是否允许继续操作（建议允许，不阻塞输入）；新事件的插队规则；队列上限与溢出策略（丢弃最旧/合并同类）。

**E. 隐藏信息（信息隔离后必须遵守）**：对手手牌/牌库相关动画只能用**卡背/数量角标**（如"对手抽了 1 张"），不得暴露卡牌身份；对手暗藏/翻面如何表现。

**F. 乐观执行范围**：哪些操作先播本地动画（建议先做出牌飞卡+占位、激活命中、横置）；被服务端拒绝时的回滚表现（撤销动画 + 复用 `OnlineNoticeWindow` 提示）。

**G. 美术与音效资源**：是否有可用素材（特效图集/序列帧/音效）；若没有，是否接受"仅用现有卡图 + 缩放/位移/淡化"的程序化动画。

**H. 与延时掩盖的目标**：接受"多长延时用动画吸收"（例如目标：公网 150ms RTT 下点击有即时反馈）；是否需要"等待对手"的提示动画。

**I. 调试与验收**：是否需要动画总开关与速度设置（便于测试/低配）；验收清单（不重叠、不错位、不卡顿、可跳过）。

**J. 性能预算**：目标帧率与低配开关；手牌 10+ 张、场上多卡同帧变化时每帧最多播放几条动画。

> 结论：**Phase C 暂缓**；等上述规格明确（可只答关心的项，未答项按"不做/保守默认"处理）后再实施。

---

## 10. 分阶段路线图

| 阶段 | 目标 | 主要改动 | 验收 |
|---|---|---|---|
| **Phase A（现状收口）** ✅ 已完成 2026-09-05 | 把已实现能力与文档对齐，清理死代码 | 模块D 删除、`ServerCore/` 移除、`SnapshotDiff`+`SnapshotApplicationQueue` 接入表现层 | build 0/0 + 32 套件全绿 + 三进程冒烟 |
| **Phase B（信息隔离 + 协议加固）** ✅ **已完成（2026-09-05）** | 隐藏信息不泄露；协议可演进 | ①`SnapshotFilter` + 占位实例（对手手牌/牌库/牌堆）；②`NetEnvelope` 信封 v2（旧格式双读）；③超时按 Kind 配置化 | `NetPhaseBTests` 6 例全绿；客户端不再持有对手手牌/牌库的真实卡牌身份 |
| **Phase C（表现层与延迟掩盖）** ⏸ **暂缓**：待用户指定动画设计规格（§9.5），未指定前不实施 | 交互顺滑、延时被动画吸收 | 动画队列（消费 `RouteRenderCommands` 的差异指令）；乐观执行最小集；卡图预热 | 公网模拟 150ms 延迟下：出牌/激活"点击即有反馈"，无交互中断 |
| **Phase D（可选：重连 / 服务端形态）** ⏸ 已决定延后 | 掉线可恢复；如需再谈去 Godot 化 | `ReconnectManager` 接线（已决定延后）；§8.3 迁移路径（仅 §1.2 触发条件出现时） | 断网 30s 重连恢复一致快照 |

> Phase C / Phase D 的**待办清单、前置决策与触发条件**已收口到 `后续优化事项.md` **§五 / §六**（与本节路线图互为索引）。

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

## 12. 决策（✅ 已确认，2026-09-05）

1. **三个技术模块去向** —— ✅ 已按建议执行：
   - `EventToMessageMapper`（模块D）：**下线删除**（连同 `RenderEvent`/`EventSerializer` 与 `NetP3DTests`）。
   - `SnapshotDiff/RenderCommand`：**保留并接入为表现层事件源**（新增 `SnapshotApplicationQueue`；`ArenaOl` 消费差异）。
   - `ServerCore/`：**整体移除（方案 a）**（连同 `UWPro.csproj` 两条 `Compile Remove` 一并删除）。
2. **是否现在做 §9.3 的预留** —— ✅ 已执行**前 3 项**（保留上快照 + 表现事件接入点 + 快照合并/延后窗口）；乐观执行最小集与卡图预加载留待 Phase C。
3. **信息隔离提前** —— ✅ 已确认：`PerPlayerSnapshot` 作为 **Phase B 首位**。
4. **服务端形态** —— ✅ 确认**维持 Godot headless**，不推进去 Godot 化（仅当 §1.2 的部署约束出现时才按 §8.3 重启该路线）。

---

## 13. 决策落实记录（2026-09-05）

### 13.1 代码改动
| 类别 | 内容 |
|---|---|
| **删除（模块D 下线）** | `Script/Net/EventToMessageMapper.cs`、`Script/Net/RenderEvent.cs`（含 `EventSerializer`）、`Script/Tests/NetP3DTests.cs`；并从 `Script/Tests/TestsRunner.cs` 移除 `new NetP3DTests()` |
| **删除（ServerCore 方案 a）** | 整个 `ServerCore/` 目录（`Program.cs`/`CardTemplateRepository.cs`/`ServerCardInstance.cs`/`ServerCore.csproj` 等）；`UWPro.csproj` 移除两条 `Compile Remove="ServerCore/**"` 与注释 |
| **新增（表现层事件源底座）** | `Script/Net/SnapshotApplicationQueue.cs`：同帧合并（`CoalescedCount` 可观测）、延后窗口（`IsDeferred`）、与上一份已应用快照配对（`TryDequeue(out prev, out next)`）、`Reset()` 换局 |
| **修改（ArenaOl 接入）** | 订阅改为 `OnSnapshotReceived`（只入队）→ `_Process` 中 `PumpSnapshotQueue(delta)` 决定应用时机（本地交互/动画期延后，上限 1.5s）→ 应用后用 `SnapshotDiff.Diff(prev,next)` → `RouteRenderCommands()`（表现层唯一入口，当前为分类计数 + `LastPresentationSummary`，`VerbosePresentation` 可开日志）；`AttachLauncher` 时 `_snapshotQueue.Reset()` |
| **修改（测试）** | `Script/Tests/NetP3BTests.cs`：新增 3 例（`TestSnapshotQueueCoalescing` / `TestSnapshotQueuePairing` / `TestSnapshotQueueDeferred`），并更正文件头注释（原描述的 `SnapshotRenderDriver` 已不存在） |

### 13.2 验证结果
- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**32 个套件全绿、0 失败、0 ERR**，`>>> 全部逻辑正确性测试通过`（套件数由 33 → 32 降低 = 移除 `NetP3DTests`；`NetP3BTests` 由 3 → **6 例**）。
- 说明：Godot headless 运行需写入 `user://` 日志目录，受限沙箱下会 SIGSEGV；本次以放宽权限运行完成验证（与工程本身无关）。

### 13.3 后续（下一步优先级）
1. ~~Phase B 首位：信息隔离~~ → ✅ **已完成，见 §14**。
2. **Phase C（表现层动画）** → ⏸ **暂缓**：需先由用户指定动画设计规格（问题清单见 §9.5），未指定前不实施。Phase B 已完成，其前置条件（"动画只演可见信息"）已就绪。

---

## 14. Phase B 执行记录（2026-09-05 · 信息隔离 / 信封 v2 / 超时配置化）

### 14.1 代码改动
| 类别 | 内容 |
|---|---|
| **新增（信息隔离核心）** | `Script/Net/SnapshotFilter.cs`：`ForPlayer(full, viewer)` 按观看者过滤快照；隐藏判定 = `Hand`/`Deck` 且 `ZoneOwnerId != viewer`（`WorldDeckCards` 对双方隐藏）；`MakeHiddenRef` 生成稳定占位 UID（`hidden_<zone>_<owner>_<index>`，**不携带 TemplateUid/运行时状态**）；`viewer <= 0` 不过滤（服务端自省/测试） |
| **新增（协议加固）** | `Script/Net/NetEnvelope.cs`：类型信封 v2（`V2:<type>|payload`），`Wrap/TryUnwrap/UnwrapPayload/UnwrapType`；只按**第一个** `|` 切分（载荷含 `|` 安全） |
| **数据模型** | `ZoneCardRef.IsHidden`（新字段，参与序列化）；`CardInstance.IsHidden`（运行时标记，单机恒 false）；`ZoneManager.ToCardRef`、`WorldDeckManager.ToSnapshotUids` 保留 `IsHidden` 投影 |
| **占位实例解析（统一入口）** | `CardManager.RebuildFromRef`：`r.IsHidden` 时**在 ResolveInstance 之前**统一处理（`new CardInstance(null, uid)` + 稳定 UID 复用 + 只写区域归属，不写运行时状态）→ 任何子类 override（跨进程客户端/测试替身/未来纯托管）都无需各自处理；`NetClientCardManager` 去掉重复分支并对空 `TemplateUid` 加保护（不再以 null 作字典键） |
| **服务端下发** | `GameInitializer.BuildSnapshotFor(playerId)`；`NetworkMatchLauncher.PushSnapshot()` 改为**逐玩家过滤下发**（`SnapshotFilter.ForPlayer`），`OnServerSnapshot` 仍为完整快照（服务端自省/测试）；移除已无用的 `BroadcastSnapshot` |
| **客户端渲染** | `ArenaOl.RebuildInto`：`inst.IsHidden` → `Setup(null) + SetFace(false)`（只画卡背、不可拖拽、不减费、不参与高亮/交互）；`RefreshActivateHighlights`/`ComputeClientCostMod`/墓地重建等跳过占位；`DeckZone.Initialize`/重建分支对占位同样只画卡背（对手牌库/地区牌堆按数量显示） |
| **链路接线（信封）** | `IntentClient.SendIntent`、`NetworkMatchLauncher.SendChoiceResult`（上行打信封）；`NetworkMatchLauncher.OnServerMessage`（回执打信封 + 优先按类型分发）、`OnClientMessage`（按类型分发）；`NetworkInputGateway.RequestChoice`（打信封）、`TryHandleResult`（信封/裸 JSON 双读） |
| **超时配置化** | `NetworkInputGateway.TimeoutSecondsFor(kind)` + `DefaultTimeoutSeconds` + 可覆盖 `TimeoutProvider`；超时回包补齐 `RequestId` |
| **测试** | 新增 `Script/Tests/NetPhaseBTests.cs`（6 例：过滤/占位解析/不过滤视角/信封往返与旧格式兼容/超时映射/超时兜底）；适配既有用例：`NetP2Tests`（解包请求 + 包裹回包）、`NetP3CTests`（服务端解包/回执包裹）、`NetP1Batch1MatchTests.TestLocalLoopbackWorldDeckAndAreaProjection`（世界牌堆改为断言"数量一致 + 均为占位"）、`NetCrossProcessSimTests` 测试替身对空 `TemplateUid` 加保护 |

### 14.2 验证
- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**33 个套件全绿、0 失败、0 ERR**，`>>> 全部逻辑正确性测试通过`（`NetPhaseBTests` 6/6；`NetCrossProcessSimTests` 3/3；`NetP1Batch1MatchTests` 4/4；`NetP1Batch1Tests` 3/3）。
- 兼容性：`IsHidden` 默认 false → 单机与既有本地快照链路行为不变；信封为"双读"，旧格式仍可解析。

### 14.3 仍待做（Phase B 收尾项 → 并入 Phase C/D）
- `ChoiceResult` 的 `PlayerId` 复核、意图去重/频率限制（§7.2 剩余缺口）。
- 掉线**立即**取消挂起决策（当前仍需等按 Kind 的超时）→ 与 Phase D 重连一并做。
- 占位卡的表现细节（卡背样式/数量角标）随 Phase C 的动画与表现层一起打磨。

### 14.4 Phase C 前置条件（**阻塞项**）
- Phase C（表现层动画 / 乐观执行 / 预加载）**需要的"动画设计规格"尚未由用户指定**：动画清单、与全量重建的关系（轻量出现类 vs 增量应用）、时序节奏、播放期间可操作性、隐藏信息的表现约束、乐观执行范围、美术与音效资源、性能预算等——完整问题清单见 **§9.5**。
- 因此：**Phase C 暂缓**；§9.3 已实现的"预留接口"（差异指令 + 节流/延后窗口）保持不变即可，不需要为等待期做任何额外改动；其中"延后窗口 1.5s"属占位假设，待规格确定后复核（§9.3 注）。

---

## 15. UI 对齐与功能补齐执行记录（2026-09-05 · 四项）

> 用户指令：**甲1（事务卡选目标）、甲2（地区统计）+ 两个遗留问题（堆叠区 UI、对抗 UI 对齐单机）**。均为"对齐单机"的确定性改动，不涉及动画（Phase C 仍暂缓）。

### 15.1 甲1 — 事务卡（需目标出牌）联机选目标
- `ArenaOl.OnLocalHandCardDragEnded`：拖出后若"`Sorcery` 类别 + `GetRequiresTarget()`"且落点未命中目标 → 进入 `BeginOnlinePlayCardTargetSelection`。
- 该入口：费用预检（用快照投影的费用修正，对齐单机"费用不足不弹窗"）→ 候选 = 争夺区角色 + 附属区附属 + 秘社（`ClientCardManager.IsLegalPlayTarget` 过滤）→ 场上高亮 + 复用单机 `TargetSelectionWindow` → 选中后 `PlayCard(cardUid, null, targetUid)`。
- 复用既有"场上选目标"状态机（`_activationTargetActive`/`_activationLegalCardUids`），新增 `_pendingPlayCardUid` 区分两条上行路径（激活 vs 出牌）。

### 15.2 甲2 — 地区统计显示
- `ArenaOl.RebuildAreas`：`slot.SetStatsDependencies(ClientZoneManager, ClientPlayerManager)` 后再 `Setup` → 复用 `AreaStatsCalculator`（与单机同源），随每次快照重建刷新。

### 15.3 遗留1 — 堆叠区 UI 对齐单机
- 快照：`StackEntrySnap` 新增 `ControllerId` / `WonAreaUid` / `SourceTemplateUid` / `Targets`（`StackTargetSnap`）；`StackManager.ToSnapshot/ApplySnapshot` 双向映射；`NetworkMatchLauncher.ClientStackSnapshot` 暴露给 UI。
- `ArenaOl.RefreshStackUI`：卡片型显示真实卡面 / 效果型文本 + **来源卡图** / `AreaWon` 挂地区卡面（调用单机 `StackEntryUI.AttachAreaCard` 这个**既有公开方法**）/ 按 `Targets` 绘制箭头（控制者着色 + 每帧跟随端点）。
- **单机 UI 零改动**：来源卡图不用 StackEntryUI 新 API，而是 ArenaOl 直接取该条目场景内既有节点 `TriggerCardArt` 设纹理（`GetNodeOrNull<TextureRect>("TriggerCardArt")`）→ `Script/UI/StackEntryUI.cs` 保持原样。

### 15.4 遗留2 — 调查/战斗/破平对抗 UI 对齐单机（**新建联机专用 UI，不改单机**）
- **根因修复（关键）**：原先设想"快照携带待分配调查卡"，但**服务端在等待玩家决策期间不会推快照** → 客户端拿不到卡、只能退回简化面板。现改为**随请求下发**：`ChoiceRequest.OptionCardRefs`（与 `OptionUids` 同序的展示引用，`CombatManager` 填充），客户端用投影 CardManager `RebuildFromRef` 重建卡面。
- **协议**：`ChoiceRequest` 补 `AreaUid` / `AreaNo` / `LoserPlayerId` / `CombatTypeValue` / `OptionCardRefs`。
- **新建联机专用 UI**（不改单机类）：
  - `Script/UI/OnlineStrengthWindow.cs` + `Scenes/Net/OnlineStrengthWindow.tscn`：与单机 `StrengthWindow` **视觉与操作一致**（点场上卡左键 +1 / 右键 -1、清空分配、剩余点数不足禁止确认），但**不做本地结算** —— 确认时把"预分配结果"交给 `OnConfirmed`，由 ArenaOl 封装 `ChoiceResult` 上行；合法承伤目标由 `IsLegalTarget`（服务端下发的合法目标集合）判定，客户端不复制规则。
  - `Script/UI/OnlineDeckZone.cs` + `ArenaOl.tscn` 两个牌库节点改挂此脚本：用 `CardUI.Setup(null)` 只画卡背 + 数量，安全渲染"无模板占位"，避开单机 `DeckZone` 的模板校验分支。
- **直接复用、无需修改**：`InvestigationWindow` / `TieBreakWindow` 本身是**纯 View（回调注入、不调用数据层）** → 联机直接实例化同一场景，把回调换成"回 `ChoiceResult`"即可。
- **单机零改动（试验性 additive 改动已全部回退）**：`Script/UI/StrengthWindow.cs`、`Script/UI/StackEntryUI.cs`、`Script/UI/DeckZone.cs` 均已恢复原样。
- **信息隔离收紧**：牌库对**双方**都只发占位（单机也只看卡背与数量、从不暴露牌序）；手牌仍为"自己可见、对手占位"。
- **服务端加固**：战斗伤害分配按服务端计算的合法目标集合**复检**，非法条目忽略。

### 15.5 验证
- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**33 套件全绿、0 失败、0 ERR**；`NetPhaseBTests` 由 6 → **9 例**（堆叠快照新增字段往返、调查卡仅决策者可见、对抗请求上下文+展示引用往返）。
- `Scenes/Net/ArenaOl.tscn` 单独加载验证：**无解析/加载错误**（牌库节点已改挂 `OnlineDeckZone`）。
- 待人工冒烟（headless 不覆盖 UI）：事务卡拖出弹选目标、地区统计数字、堆叠区卡面/箭头、三类对抗窗口的点击/拖拽/确认回包。

---

## 16. 出牌拖拽语义统一执行记录（2026-09-05 · 联机对齐单机手势）

> 用户指令：**"联机使用事务牌的逻辑和单机不一致——单机是拖出手牌区后选择目标，联机是直接拖拽到目标；检查联机模式逻辑差异的地方并且统一成和单机模式一致"**。
> 约束不变：**不改单机 UI/逻辑**；只动联机侧。

### 16.1 参考基准（单机唯一真相源）
`Script/UI/Interaction/CardDragController.OnCardDragEnded`（行 93–260）的**落点语义**，逐步复刻：

| 步 | 单机行为 | 联机（`ArenaOl.OnLocalHandCardDragEnded`）|
|---|---|---|
| 0 | `IsDragInteractionLocked`（模态窗口打开）→ 冻结出牌并归位 | `_activationSelectionBusy \|\| _noticeWindowOpen` → 直接 return（不发意图） |
| 1 | 非手牌卡的拖拽被忽略 | 只有本地玩家手牌订阅 `CardDragEnded`（`IsLocalPlayer` 输入隔离） |
| 2 | 蒙版激活 → `HandleMaskDrop` 专属分支 | `MaskMode.DeployCovert` / `EstablishAsset` 专属分支（见 §16.3） |
| 3 | 拖回自己手牌区 → 取消并归位 | 命中 `_localHand.GetGlobalRect()` → return（不发意图，卡由下次快照重建归位） |
| 4 | `PickAreaCardAt` 命中且 `IsLegalCardTarget` → `targetCardUid` | `PickFieldCardAt` 命中且 `ClientCardManager.IsLegalPlayTarget` → `targetCardUid` |
| 5 | 角色卡 → 己方争夺区容器；非角色卡 → 仅当 `GetAllowedTargets().Contains(Area)` 才取地区（D48）；秘社单独判 | 同左（`FindOwnAreaUidAt` / `FindTargetAreaUidAt` / 本方秘社容器 + `IsLegalPlayTarget`） |
| 6 | 角色卡未落己方争夺区 → 退回手牌 | 不发意图（return；归位交由快照重建） |
| 7 | `Sorcery + requiresTarget` 且两者皆空 → `BeginSorceryTargetSelection` | `BeginOnlinePlayCardTargetSelection`（同类判定 `IsSorceryRequiringTarget`） |
| 8 | 其它 `requiresTarget` 且两者皆空 → 退回手牌 | 不发意图（return） |
| 9 | `_actionService.PlayCard(...)` 本地结算 | `SendIntentAs(PlayCard, cardUid, targetAreaUid, targetCardUid)`（**服务端结算**） |

**差异根源**：单机"="本地直接结算 + UI 自行归位"；联机"=只上行意图 + 一切归位/呈现由快照重建驱动"。所以凡单机"退回手牌"的分支，联机一律**不发意图并 return**。

### 16.2 命中范围与拖拽高亮统一
- **`PickFieldCardAt`** 收敛为：**仅 `AreaZone` / `AttachedZone` 且跳过 `IsHidden`**，取 `ZIndex` 最上层 —— 与单机 `PickAreaCardAt`（遍历 `AreaCardUIs` 反向 + `AttachedCardUIs`）范围一致。此前它扫描全部 `_cardUIMap`（含手牌/牌库），存在把非场上卡误判为目标的隐患。
- 该函数被三处共用（出牌落点、伤害分配点击、激活选目标），因此**三处一并统一**；随之删除重复实现 `PickFieldCardUiAt` 与已无引用的 `FindTargetCardUidAt`（秘社落点改由拖拽分支内联判定，与单机结构一致）。
- **`UpdateHandDragTargetHighlights`**（新增，`_Process` 每帧调用）对齐单机 `UpdateDragTargetHighlights`：无蒙版/无模态时，对**争夺区/附属区**中 `IsLegalPlayTarget(dragging, target)` 为真的卡 `SetTargetHighlight(true)`，本方秘社单独高亮；拖拽结束或不满足条件时 `ClearHandDragTargetHighlights`。
  - 单机仅高亮 `AreaCardUIs`（不含附属）；联机把附属一起高亮，属**可见性增强的刻意差异**（不改规则，只影响提示）。

### 16.3 蒙版落点规则
- **建立资产**：落点需命中**己方资产区**或**任一争夺区**，否则取消（不发意图）——对齐单机 `HandleMaskDrop` 的 `hitAreaContainer == null && !droppedOnAsset` 判定。
- **派遣暗藏**：需落**己方**争夺区。单机硬编码 `Player1Area`（单机为本地人机/单侧视角）；联机按 `_localPlayerId` 取 `slot.Player1Area` / `slot.Player2Area` —— **语义等价且对 P2 同样成立**（属联机侧修正，不构成规则分歧）。

### 16.4 验证
- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**33 套件全绿、0 失败、0 ERR**，`>>> 全部逻辑正确性测试通过`。
- headless 不覆盖 UI 手感：需人工冒烟"事务卡拖出→弹窗选目标"、"拖到合法/非法落点的高亮与取消"、"蒙版拖拽落点"。

### 16.5 已知差异与待确认项（**未做，需用户拍板**）
1. **蒙版可拖拽资格未做客户端预检**：单机在**拖起**时用 `TurnManager.CanDeployCovert` 拦截（`IsDraggableUnderMask`）；联机客户端持有的是投影状态，本轮**不复制该规则**（守住"客户端不重算规则"红线），非法操作由服务端拒绝 + 回执提示。若要客户端预检，需先确认"允许客户端投影该资格判定"。
2. **其余决策面板仍为简化版**：`SelectTriggerOrder`（单机 `TriggerOrderWindow`）、`PayOrTap`（单机 `Arena.PromptPayOrTap`）、`ForceChoice` 在联机仍是 `ChoicePromptPanel`。**是否继续对齐待用户确认**（属 §9.5 之外的独立议题）。
3. **取消归位的插回顺序**：单机 `ReturnCardToHand` 按放手 X 计算 `PendingHandInsertIndex` 精确插回；联机不本地移动卡，手牌顺序由下一次快照重建决定，可能与放手位置不同。
4. **`DeployCovert` 落点判定**以 `_localPlayerId` 为准（见 §16.3），与单机硬编码 P1 的实现不同——已在文档记为联机修正项，不作为"与单机不一致"。

---

## 17. 决策面板三处对齐单机 + 蒙版拖起预检（2026-09-05 · 第 3 版）

> 用户裁决：① 「**全部三处一起对齐**」决策面板；② 「**加客户端预检**」蒙版可拖拽资格。
> 计划与开放问题全文见 `PLAN.md` 第十节（实施前已确认：静态函数落点＝新建 `DeployCovertEligibility`、排序回包＝`SelectedIndices` 有序下标、支付置灰＝服务端下发 `PayAllowed`、接受逐次入堆叠的多轮往返、纳入地区目标选目标）。
> 约束不变：**不改单机 UI/逻辑**；共享规则层只做**行为等价**的抽取。

### 17.1 决策面板对齐总览（改前 → 改后）

| 请求种类 | 发送点 | 改前（联机） | 改后（对齐单机） |
|---|---|---|---|
| `SelectTriggerOrder` | `TurnManager.RunTriggerPhaseAsync` | `ChoicePromptPanel` 简化列表；**服务端忽略玩家顺序**（一律默认顺序全部入栈） | `OnlineTriggerOrderWindow`（拖拽入堆叠/放弃区 + 全部/第一张入堆叠 + 完成排序）+ 服务端**按玩家下标顺序入堆栈** |
| `PayOrTap` | `EffectEngine.ExecuteVerbAsync`（JC068） | `ChoicePromptPanel` 两个按钮，无置灰 | `OnlinePayOrTapWindow`（对齐 `Arena.PromptPayOrTap`：支付/横置二选一、费用不足置灰、强制不可取消） |
| `SelectTarget` | `TurnManager.EnsureTargetThenPushAsync` | `ChoicePromptPanel` 按钮列表 | 复用 `TargetSelectionWindow` + 场上合法目标高亮（**卡目标**与**地区目标**均支持），点选即回包，无取消（对齐 `TriggerInteractionCoordinator.RequestTargetSelection`） |
| `CombatStrength`/`CombatInvestigation`/`TieBreak` | `CombatManager` | 上一轮已对齐（§15.4） | 不变 |

> 说明：`ChoiceRequest.Kind.ForceChoice` 在生产代码中**从未被发送**（仅存在渲染分支与超时表）；「ForceChoice 对齐」实质是各请求 `ForceChoice` 布尔下的**取消按钮/点窗外关闭**行为对齐单机（上述三处单机均为强制、不可取消）。

### 17.2 协议扩展（纯加法，旧端可忽略）

| 字段 | 位置 | 语义 |
|---|---|---|
| `PayAllowed` | `ChoiceRequest` | PayOrTap：服务端权威的"支付得起"（`EffectEngine.CanPayPrepareUpkeep`）→ 客户端仅据此置灰「支付」，**不重算规则** |
| `OptionForced` | `ChoiceRequest` | SelectTriggerOrder：与 `OptionUids` 同序的"强制"标记（强制项不可拖入放弃区） |
| `OrderContextId` | `ChoiceRequest` | SelectTriggerOrder：本次排序窗口上下文 id（`trig_<owner>_<turn>_<seq>`）。客户端据此**复用同一窗口**，不因逐次入堆叠的多轮请求而反复关闭重开 |
| `OptionLabels` / `OptionTexts` | `ChoiceRequest` | SelectTriggerOrder：条目名 / 效果文本（与单机 `DisplayName`/`DisplayText` 一致，客户端无需自行推导） |
| `SelectedIndices` | `ChoiceResult` | SelectTriggerOrder：本次要入堆叠的条目**下标序列**（指向该次请求的 `OptionUids`）。不能用 `SelectedUids` 表达顺序——同一来源卡可能有多个触发、UID 会重复 |

### 17.3 服务端流程改动（**仅联机异步路径**）

`TurnManager.RunTriggerPhaseAsync` 由"一次请求 + 全量默认顺序入栈"改为**逐轮循环**：
1. 剩余触发非空 → 发请求（带 `OptionForced`/`OrderContextId`/`OptionLabels`/`OptionTexts`）→ 依回包：
   - `Cancelled=true` 且剩余含强制触发 → **忽略取消并重发**（等价单机「Done 按钮在入堆叠区非空时禁用」）；
   - `Cancelled=true` 且无强制触发 → 放弃剩余，结束该 owner；
   - 否则按 `SelectedIndices` 顺序逐个 `EnsureTargetThenPushAsync`（需选目标的先经网关选目标）后从剩余移除；下标越界/重复 → 忽略并打日志；未回下标（旧客户端）或空列表 → **全部按默认顺序**（与旧行为一致，不会卡死）。
2. 每 owner 最多重问 32 轮（防呆：客户端持续回"不可推进"结果时按默认顺序全部入栈）。
3. 单机同步流程 `RunTriggerPhase`（`_view.OpenTriggerOrderForPlayer`）**零改动**。

### 17.4 客户端新增/改动

- **新增** `Script/UI/OnlineTriggerOrderWindow.cs` + `Scenes/Net/OnlineTriggerOrderWindow.tscn`（联机专用窗口，独立 scene uid）：
  - **只读复用**单机纯 View 组件 `TriggerEntryUI`（`Setup(..., triggerData=null, template=null)` 均安全）与 `TriggerOrderRoot`（全程不读 `TriggerData`）→ 单机这两个文件**零改动**；
  - 逐次入堆叠时按 `OrderContextId` **刷新同一窗口**，并借 `OptionKey` 一对一匹配把仍留在放弃区的条目**放回放弃区**（保留玩家落区）；
  - 提交后按钮冻结等服务端下一轮；3s 无下一轮则恢复按钮（防御服务端异常导致窗口永久冻结）。
- **新增** `Script/UI/OnlinePayOrTapWindow.cs`（纯 View，无场景文件）：两选项 + 置灰 + 无取消/点窗外不关闭；选中回 `ChoiceResult.Pay`。
- **`ArenaOl`**：
  - `OnClientChoiceRequest` → `TryOpenOnlineDecisionWindow`（原 `TryOpenOnlineCombatWindow` 更名，覆盖 6 种决策）；
  - `SelectTarget` → 复用既有「场上点击选目标」状态机（`_activationLegalCardUids` + `_onlineTargetWindow`），新增 `_pendingChoiceTargetRequest` 区分"回 `ChoiceResult`"与"上行意图"；
  - **新增地区目标支持**：`_activationLegalAreaUids` + `ApplyOnlineAreaHighlights`/`ClearOnlineAreaHighlights`（用单机同款公开 API `AreaSlot.SetHighlight/IsTargetSelectable/OnAreaClicked`）+ `PickLegalActivationAreaAt`；
  - **`_Input` 关键修正**：`Node._Input` 先于 `Control._GuiInput`，故选目标分支必须自行判定地区槽位（否则 `AreaSlot._GuiInput` 永远收不到点击）。
- **蒙版拖起预检（P4）**：新增 `Script/Manager/DeployCovertEligibility.cs`（静态纯函数：`!HasPublicKeyword && 是角色卡`），`TurnManager.CanDeployCovert` **改为委托调用**（表达式等价 ⇒ 单机行为不变）；`ArenaOl` 用同一函数在**拖起前**设 `CardUI.CanDrag`（进入/退出蒙版时 `ApplyOnlineMaskDragGate` 刷新），非法卡**拖不起来**（此前是"能拖起、等服务端拒绝"）。

### 17.5 验证

- `dotnet build UWPro.csproj`：**0 警告 0 错误**。
- headless 全量 `TestsRunner`：**34 套件全绿、0 失败、0 ERR**，`>>> 全部逻辑正确性测试通过`。
- 新增套件 `Script/Tests/NetChoicePanelTests.cs`（6 例）：协议新字段往返 / **排序按客户端下标顺序入堆栈 + 放弃剩余** / **强制触发忽略取消并重问** / `PayAllowed` 下发与 `Pay=false` 落地（横置来源卡）/ 蒙版资格静态函数与 `CanDeployCovert` 一致 / 联机决策窗口场景可加载且导出引用可解析。
- headless 不覆盖 UI 手感，待人工冒烟：排序窗口拖拽与逐次入堆叠、支付/横置窗口置灰、场上与地区点选目标、蒙版非法卡拖不起来。

### 17.6 遗留与差异（诚实记录）

1. **`Done` 点击可能命中"服务端已结束"**：全部入堆叠后若服务端已无剩余，其阶段即结束；此时玩家点「完成排序」发出的 `Cancelled=true` 无对应挂起请求，由网关丢弃（客户端仍在本地关窗并解锁）。行为与单机"必须点 Done 关窗"一致，无功能影响。
2. **`_activationSelectionBusy`（本地模态）依赖玩家点 Done 关闭排序窗**——与单机模态窗口纪律相同（窗口未关＝交互冻结）；快照应用仅被延后至上限 1.5s，不会长期不同步。
3. **逐次入堆叠的落区记忆按 `OptionKey` 一对一匹配**：若同一来源卡在同一轮有多个触发，匹配顺序为"先到先配"，极端情况下放弃区落区可能与玩家上一次摆放不完全一致（规则结果不受影响）。
4. **Phase C（动画/延迟掩盖）仍暂缓**（待用户指定动画规格，见 §9.5）；**Phase D（重连）** 仍延后（含"掉线立即取消挂起决策"）。


