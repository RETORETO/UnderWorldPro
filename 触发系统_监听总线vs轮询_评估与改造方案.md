# 触发系统改造评估：监听总线 vs 当前轮询

> 文档状态：方案评估（待评审）
> 适用项目：UWPro（Godot 4 + C#）
> 背景：当前触发系统为「主动轮询 + 集中收集」模式，新增触发时机需改动 `TurnManager` 多处，维护成本高；后续需大量新增触发时机，故评估引入「领域事件总线（监听总线）」。

---

## 1. 现状盘点（轮询模式）

### 1.1 当前架构

触发时机目前通过 `EffectTiming` 枚举声明，由 `TurnManager` 在**固定业务节点**手动调用收集方法：

```28:29:Script/Data/Effects/EffectData.cs
        OnCardPlayed,          // 反应触发：当任意卡被打出时，旁观者卡触发（如 XG01 打出事务牌时抓牌）
```

```338:389:Script/Manager/TurnManager.cs
private void CollectTriggeredEffects(Script.Data.Effects.EffectTiming timing, Script.Game.Entities.CardInstance contextCard)
{
    ... // 遍历所有卡实例 → 匹配 timing → 条件判断 → 构建 TriggerEffectInstance → RunTriggerPhase
}
```

```397:442:Script/Manager/TurnManager.cs
private void CollectManeuverTriggers()   // 仅 ManeuverTrigger 专用
{
    ... // 与 CollectTriggeredEffects 几乎重复，只是 timing 固定为 ManeuverTrigger + IsAreaTarget=true
}
```

调用点（仅 2 处）：

| 调用点 | 位置 | 时机 |
|--------|------|------|
| 打出卡牌后 | `TurnManager.cs:952` | `CollectTriggeredEffects(OnCardPlayed, instance)` |
| 对抗前优先权窗口 | `TurnManager.cs:1384` | `CollectManeuverTriggers()` |

### 1.2 现状痛点（维护难度高的根因）

1. **新增时机 = 改控制器**
   每新增一个 `EffectTiming`（如 `OnCardDied`、`OnAreaWon`、`OnTurnEnded`），必须：
   - 在枚举里加值；
   - 在对应业务代码里插入一行 `CollectTriggeredEffects(NewTiming, ctx)`；
   - 若该时机有特殊的"上下文卡 / 目标语义"（如机动要 `IsAreaTarget`），还要再写一个几乎重复的 `CollectXxx` 方法。
   控制器与卡牌数据的耦合随时机数量线性增长。

2. **收集逻辑重复**
   `CollectTriggeredEffects` 与 `CollectManeuverTriggers` 80% 代码相同（遍历实例、区域过滤、构建实例、RunTriggerPhase），只是 timing 与 `IsAreaTarget` 标记不同。新增特殊时机会继续复制粘贴。

3. **卡牌无法"自声明"监听**
   卡牌的 `EffectData` 已声明 `Timing + Condition + Actions`，但**何时被扫描、由谁驱动**完全由 `TurnManager` 决定。卡牌数据是被动的，没有"我订阅了某事件"的主动性。

4. **上下文传递脆弱**
   反应触发的"上下文卡"（`TargetCardUid`）、机动的"地区目标"（`IsAreaTarget`），靠各收集方法手工塞字段，新增时机若需新上下文，又要改 `TriggerEffectInstance` 和收集方法。

### 1.3 当前已具备的可用基础

- `EffectData` 已有完整声明式 schema：`Timing` / `Condition` / `Target` / `Actions` / `FaqNote`。
- `TriggerEffectInstance` 运行时载体已成型（Source / Owner / EffectDataRef / SelectedTargetUid / IsAreaTarget）。
- `RunTriggerPhase` / `PushTriggerToStack` / `ResolveStackTop` 已是**时机无关**的标准流程（入栈、排序窗口、让过、结算）。
- `EffectEngine.EvaluateCondition(...)` 条件判定已可复用。

=> 改造的重心是**"谁来触发收集"**，而不是重做结算流程。

---

## 2. 方案对比

### 方案 A：保持轮询（改进版：统一收集入口）

不引入总线，但把 `CollectTriggeredEffects` + `CollectManeuverTriggers` 合并为单个 `CollectTriggers(EffectTiming, ctx)`，由 `TurnManager` 在各业务点 emit 一个轻量 `RaiseTriggerTiming(timing, ctx)` 包装。

| 维度 | 评价 |
|------|------|
| 新增时机成本 | 中：仍需在 `TurnManager` 业务点插 `RaiseTriggerTiming` 一行 |
| 代码重复 | 低：收集逻辑合并 |
| 架构耦合 | 仍高：控制器必须知道所有时机 |
| 风险 | 低，纯重构 |
| 适用 | 时机数量 ≤ 10 且增长慢 |

### 方案 B：领域事件总线（监听总线）★推荐

引入 `GameEventBus`（单例），业务点只 `bus.Emit(GameEvent.X, ctx)`；卡牌的 `EffectData` 声明"我响应哪个 GameEvent"，由统一 `TriggerRegistry` 在事件到达时匹配 `timing↔event` + `Condition`，自动生成 `TriggerEffectInstance` 并接入既有 `RunTriggerPhase`。

| 维度 | 评价 |
|------|------|
| 新增时机成本 | 低：业务点 `Emit` 一次即可，卡牌纯数据声明，控制器零改动 |
| 代码重复 | 无：单一匹配器 |
| 架构耦合 | 低：控制器只管发事件，不管谁订阅 |
| 风险 | 中：需新增总线 + Registry，并迁移现有 2 个调用点 |
| 适用 | 时机数量多、增长快（符合本项目后续规划） |

### 方案 C：Godot Signal 总线

用 Godot 的 `[Signal]` 做跨系统广播。但触发涉及 `CardInstance`（纯数据，非 Node）、异步排序窗口、入栈等，Signal 在纯逻辑层嵌入不自然，且难以携带结构化 `ctx`。不推荐。

---

## 3. 推荐方案 B 详细设计

### 3.1 核心组件

```
GameEventBus (单例)
  ├─ Emit(GameEvent evt, TriggerContext ctx)
  └─ Subscribe / 由 TriggerRegistry 内部消费

GameEvent (枚举，替代 EffectTiming 中"反应类"的硬编码)
  OnCardPlayed, OnCardDied, OnAreaWon, OnTurnStarted, OnTurnEnded,
  PreCombat(机动), OnPhaseChanged, ... (可无限扩展)

TriggerContext (事件上下文载体)
  SourceCard / ContextCard / TargetAreaId / 任意扩展字段

TriggerRegistry (触发匹配器，时机无关)
  ├─ 持有所有"已注册监听"：遍历场上 AreaZone 卡 → 读 EffectDataList
  ├─ On bus.Emit(evt, ctx): 匹配 evt↔EffectData.Timing 映射 + EvaluateCondition
  └─ 生成 TriggerEffectInstance → RunTriggerPhase(既有流程)
```

### 3.2 关键映射：GameEvent ↔ EffectTiming

> 实际代码维护于 `TriggerRegistry.EventToTiming`（`private static Dictionary<GameEvent, EffectTiming>`），**实现方法名为 `EventToTiming`**（下文 §3.4/§3.5 示例曾用 `MapEventToTiming` 为设计期命名，以代码为准）。下表为 2026-08-05 后的实际映射状态。

| GameEvent | 对应 EffectTiming | 备注 |
|-----------|-------------------|------|
| `OnCardPlayed` | `OnCardPlayed` | ✅ 已接（XG01） |
| `PreCombat` | `ManeuverTrigger` | ✅ 已接（JC014 机动） |
| `EnterPlay` | `EnterTrigger` | ✅ 已接（进场直接入栈） |
| `Uncover` | `UncoverTrigger` | ⏳ 映射就绪，触点待 D6 暗藏者翻面 |
| `OnTurnEnded` | `OnTurnEnded` | ✅ 已接（`DoCleanup`） |
| `OnCardDied` | `OnCardDied` | 🟡 映射就绪，触点待 `CardManager.HandleInstanceDeath` |
| `OnAreaWon` | `OnAreaWon` | 🟡 映射就绪，触点待 `AreaManager.ResolveAreaWon` |
| `OnTurnStarted` | `OnTurnStarted` | 🟡 映射就绪，触点待 `TurnManager.NextTurn` |
| `OnDraw` | `OnDraw` | 🟡 映射就绪，触点待 `EffectEngine.Draw` |
| `OnDiscard` | `OnDiscard` | 🟡 映射就绪，触点待 `HandManager`/`Discard` |
| `OnMarkerPlaced` | `OnMarkerPlaced` | 🟡 映射就绪，触点待 E4-#1（高频须配 D-1 熔断） |
| `OnSealed` | `OnSealed` | 🟡 映射就绪，触点待 D15 |
| `OnMove` | `OnMove` | 🟡 映射就绪，触点待移动原语 |
| `OnCounter` | `OnCounter` | 🟡 映射就绪，触点待 E6-TODO（关联 T9/D-2） |
| `OnCardLeft` | `OnCardLeft` | 🟡 映射就绪，触点待离场判定处 |

> `Continuous` / `SpellCast` / `CharacterActivated` / `AttachmentActivated` 等非"外部事件驱动"的时机，仍走各自既有路径（持续效果注册表、打出结算、激活窗口），**不强制进总线**，保持边界清晰。

### 3.3 业务点改造（只改 emit，不改收集）

```csharp
// TurnManager.PlayCard —— 改前
CollectTriggeredEffects(EffectTiming.OnCardPlayed, instance);

// 改后
GameEventBus.Instance.Emit(GameEvent.OnCardPlayed,
    new TriggerContext { ContextCard = instance });
```

```csharp
// TurnManager 对抗前窗口 —— 改前
CollectManeuverTriggers();

// 改后
GameEventBus.Instance.Emit(GameEvent.PreCombat, new TriggerContext { });
```

`TurnManager` 不再包含任何 `CollectXxx` 方法，这些方法整体迁移进 `TriggerRegistry`。

### 3.4 TriggerRegistry 统一匹配器（取代两个 Collect 方法）

```csharp
public class TriggerRegistry
{
    // 取代 CollectTriggeredEffects + CollectManeuverTriggers
    public void HandleEvent(GameEvent evt, TriggerContext ctx)
    {
        var timing = EventToTiming[evt];              // 枚举映射（实现方法名 EventToTiming）
        var collected = new List<TriggerEffectInstance>();
        foreach (var card in _cardManager.GetAllInstances())
        {
            if (card.CurrentZone != CardZone.AreaZone) continue;   // 规则1 保留
            foreach (var data in card.PlayerTemplate.EffectDataList)
            {
                if (data.Timing != timing) continue;
                if (!EffectEngine.EvaluateCondition(data.Condition?.Predicate,
                    card, ctx.ContextCard, ctx.ContextCard)) continue;
                collected.Add(new TriggerEffectInstance
                {
                    Source = card,
                    OwnerPlayerId = card.ControllerId,   // 规则2 保留
                    EffectDataRef = data,
                    TargetCardUid = ctx.ContextCard?.UID,
                    IsAreaTarget = (data.Timing == EffectTiming.ManeuverTrigger),
                });
            }
        }
        if (collected.Count > 0)
            _turnManager.RunTriggerPhase(collected, ...);   // 既有流程不变
    }
}
```

> 规则 1/2/3（区域过滤、控制者归属、标准排序窗口）全部保留，行为与现状一致，仅"谁调用"变了。

### 3.5 新增时机的标准步骤（改造后）

以"死亡触发"为例，开发者只需：

1. `GameEvent` 枚举加 `OnCardDied`；
2. `EffectTiming` 加 `OnCardDied`（追加在枚举**末尾**，遵守 `.tres` 序列化顺序约束）；
3. `EventToTiming` 增加映射一行（附触发语义注释）；
4. 在 `CardManager` 死亡逻辑里 `GameEventBus.Instance.Emit(GameEvent.OnCardDied, ctx)`（**此步为触发生效必要条件**；不做则映射就绪但不响应）；
5. 卡牌 `.tres` / `.cs` 里写 `EffectData{Timing=OnCardDied, Condition=..., Actions=[...]}`。

**`TurnManager` 和 `TriggerRegistry` 核心匹配逻辑完全不用动。**

---

## 4. 迁移路径（建议分阶段）

| 阶段 | 内容 | 风险 | 落地状态（2026-08-05） |
|------|------|------|------|
| P0 | 新增 `GameEvent` / `TriggerContext` / `GameEventBus` 单例骨架 | 低 | ✅ 已落地 |
| P1 | 把 `CollectTriggeredEffects` + `CollectManeuverTriggers` 合并迁入 `TriggerRegistry.HandleEvent`；`TurnManager` 两处调用改为 `Emit` | 中（需回归 XG01 / JC014 机动） | ✅ 已落地（XG01/JC014 已验证；旧 `CollectXxx` 已删除） |
| P2 | 补齐 `EventToTiming`（注：实现方法名为 `EventToTiming`，非下文示例的 `MapEventToTiming`），将 `EnterTrigger`/`UncoverTrigger` 等尚未接入收集的时机接上总线 | 中 | 🟡 映射**已全量登记**（含 10 个新时机），但多数新时机的业务点 `Emit` 触点**待接**（见下表） |
| P3 | 按需求新增 `OnCardDied`/`OnAreaWon`/`OnTurnEnded` 等并验证 | 低（增量） | 🟡 枚举已全部新增（`OnTurnEnded` 已接 `DoCleanup`；`OnCardDied`/`OnAreaWon`/`OnTurnStarted`/`OnDraw`/`OnDiscard`/`OnMarkerPlaced`/`OnSealed`/`OnMove`/`OnCounter`/`OnCardLeft` 枚举+映射就绪，触点待接） |

> 既有 `RunTriggerPhase` / `PushTriggerToStack` / `ResolveStackTop` / `EffectEngine.ResolveEffect` **完全不动**，最大化复用。

### 4.1 落地现状快照（2026-08-05 枚举补全批次）

**已完成（结构性）**：
- `EffectTiming` 枚举补全 10 个监听时机（末尾追加，遵守 `.tres` 序列化约束）。
- `GameEvent` 配套新增 7 个事件（OnCardDied/OnAreaWon/OnTurnStarted 此前已存在）。
- `TriggerRegistry.EventToTiming` 映射表全部登记完毕，每个映射附触发语义注释。
- 每个新枚举的 XML 注释均写明"用于什么触发"与"触点【待接】位置"。

**各新时机触点接入状态（决定是否真正广播）**：
| 时机 | 映射 | 触点 Emit | 接入位置 |
|---|---|---|---|
| OnCardDied | ✅ | ⏳ | `CardManager.HandleInstanceDeath` |
| OnAreaWon | ✅ | ⏳ | `AreaManager.ResolveAreaWon`（可清核心需求 2.2 TODO） |
| OnTurnStarted | ✅ | ⏳ | `TurnManager.NextTurn` 后 |
| OnDraw | ✅ | ⏳ | `EffectEngine.Draw` |
| OnDiscard | ✅ | ⏳ | `HandManager` / `EffectEngine.Discard` |
| OnMarkerPlaced | ✅ | ⏳ | E4-#1 放置原语（高频，须配 D-1 熔断 + 死循环复核） |
| OnSealed | ✅ | ⏳ | D15 封印系统 |
| OnMove | ✅ | ⏳ | 移动原语 |
| OnCounter | ✅ | ⏳ | E6-TODO 反制原语（关联 T9/D-2） |
| OnCardLeft | ✅ | ⏳ | 离场判定处（区别于死亡） |

**验证边界（重要认知）**：因效果系统尚未规范（E6-TODO 动作原语多数占位）、全卡未实装，总线**端到端"触发结果正确性"暂不可验证**。本次工作属于"结构完整性"层，可验证项为：编译通过、枚举存在、映射无遗漏分支、未 Emit 时机不响应且不报错。触点接入须由各子系统实装时同步完成（届时仍须人工复核触发链死循环，见《触发总线_待解决问题清单.md》D-1 残留认知）。

> ⚠️ 文档 §3 示例中的 `MapEventToTiming` 为设计期命名，**实际实现方法名为 `EventToTiming`**，以代码为准。

---

## 5. 风险与注意

1. **事件顺序 / 重入**：`Emit` 后 `HandleEvent` 会同步开排序窗口（模态），需确保 `TurnManager` 的原子锁（`ExitAtomicWhenIdle`）在总线模式下仍正确解/加锁——现有 `OnCardPlayed` 已验证可行，新时机沿用同一机制即可。
2. **上下文完整性**：`TriggerContext` 需覆盖所有时机的上下文（死亡触发要带"死亡卡"、赢区触发要带"地区"），新增时机时扩展 `TriggerContext` 字段即可，不影响旧字段。
3. **性能**：每次 `Emit` 全量遍历实例（现状已是），卡牌规模不大，可接受；若后续膨胀可加"按 timing 建索引"。
4. **旧系统残留**：`Enums.cs` 中已 `[Obsolete]` 的 `TriggerTiming`/`TriggerTargetType`/`TriggerCondition` 旧枚举与总线方案无关，不必动。

---

## 6. 结论

当前为轮询模式，新增触发时机必须改动控制器且易复制代码，维护成本随时机数量线性上升。**推荐采用方案 B（领域事件总线）**：业务点只 `Emit`、卡牌纯数据声明、统一 `TriggerRegistry` 匹配，新增时机对核心逻辑零侵入。既有结算流程（RunTriggerPhase / EffectEngine）完整复用，改造风险可控，且天然支撑"后续大量新增时机"的规划。

### 6.1 关联：关键词封装选型（与总线正交，2026-08-05 选定方案3）

监听总线解决"**何时触发**"，但"**触发后做什么**"仍需对同类关键词（如公路骑士的 `公开`/`机动`）做复用封装，避免每张卡手写重复逻辑。关键词封装三方案对比后**选定方案3（组合式关键词链）**：

- 方案1（静态类手写调用）复用度最低，弃用；
- 方案2（单关键词自动展开表）无法表达复合关键词（如 `创伤袭击`），不满足；
- **方案3（原子关键词 + 组合规则 + `KeywordComboResolver`）**：既能表达 `袭击3` 带参单关键词，也能表达 `创伤袭击` 复合关键词，卡只声明原子关键词+参数、零逻辑代码。

> 方案3 仅做设计记录（详见《触发效果制作流程.md》§七），**本次不实现**，待效果系统规范（E6）后落地。与总线分工：总线管时机、关键词链管行为，关键词链只产出 `EffectData`，不得自行 `Emit`。

待评审确认后，可进入 P0/P1 实现，并同步更新《触发效果制作流程.md》等文档。
