# 联机附属/封印、横置与持续效果 BUG 修复计划

> 状态：分析完成，待确认后实施（本文件只做计划，不修改代码）。
> 范围：仅联机模式出现、单机正常的三个 BUG。
> 约束：尽可能不修改单机逻辑；以快照投影 / 联机客户端渲染层修复为主。

---

## 一、问题清单

1. 卡片结附到秘社后，UI 只显示卡背，而不是正确的结附卡面。
2. PM 命令 `attach inst_57 inst_30` 触发的结附 UI 出现位置偏移。
3. `TAP INST_57`（阿格斯IV）后：
   - 没有横置视觉表现；
   - 对手仍然不能暗藏（持续封锁未解除）。

---

## 二、根因分析

### 2.1 问题 1：结附卡显示为卡背

- 位置：`Script/UI/ArenaOl.cs` 的 `RebuildAttachmentsAndSeals`。
- 现状代码顺序：
  ```
  var ui = _cardUIScene.Instantiate<CardUI>();
  ui.Setup(inst);      // ← Setup 在 AddChild 之前调用
  ui.SetFace(true);
  ...
  hostContainer.AddChild(ui);   // ← AddChild 触发 _Ready
  ```
- `CardUI.Setup` 末尾会 `SetFace(true)`，但 `CardUI._Ready` 里执行 `SetFace(false)`（默认卡背）。
  因为 `Setup` 在 `AddChild` 之前，`_Ready` 晚于 `Setup` 执行，默认卡背覆盖了正面。
- 对比 `ArenaOl.RebuildInto` / `RebuildAreaZoneCards` 都是「先 `AddChild` 再 `Setup`」，所以它们正常。
- **结论**：这是联机迁移时附属渲染分支漏掉了「先入树再 Setup」的时序约束。

### 2.2 问题 2：结附 UI 位置偏移

- 位置：`ArenaOl.RebuildAttachmentsAndSeals`。
- 现状只做：
  ```
  ui.Position = new Vector2(0, offsetY);   // 固定 (0, 56)
  ui.TargetPosition = ui.GlobalPosition;
  ```
- 没有对齐宿主卡的视觉中心，也没有处理横置旋转后的视觉尺寸/中心，因此结附到角色/秘社时偏移。
- 单机 `Arena.Layout.ArrangeAllAttachments` 是完整算法：X 对齐宿主视觉中心、Y 从宿主视觉下缘向上压 2/5、多张依次下延、宿主 ZIndex 最高、横置用旋转后 Pivot 计算。
- **结论**：联机只做了「挂到同一容器 + 固定偏移」，漏掉了宿主对齐/偏移算法。

### 2.3 问题 3a：TAP 后无横置视觉

- 位置：`ArenaOl.RebuildAttachmentsAndSeals`（以及争夺区/资产区渲染）。
- 现状只调用 `ui.SetTapped(inst.IsTapped)`，它只维护 `IsTapped` 标记并 `QueueRedraw`，**不负责旋转**。
- 单机里旋转/定位由 `CardLayoutHelper.PlaceCardInContainer` 完成（`RotationDegrees = isTapped ? 90 : 0` + 按视觉宽高定位）。
- 联机附属/封印分支没有调用 `PlaceCardInContainer`，所以数据是横置的，但视觉不旋转。
- **结论**：横置状态已同步到客户端投影（`ZoneCardRef.IsTapped` 已往返），缺的是渲染层的旋转表现。

### 2.4 问题 3b：TAP 后对手仍不能暗藏

- 单机联动链：
  `CardInstance.SetTapped → OnStateChanged(TappedChanged) → CardManager.NotifyCardStateChanged → CardManager.OnCardStateChanged`
  → **`Arena.Events.OnCardStateChangedHandler` 订阅** → `EffectEngine.ReevaluatePrereqs(card.ControllerId)` → 解除 `BlockDeployCovert` 封锁。
- 联机服务端没有 `Arena`，即没有这个订阅；`TappedChanged` 被触发了，但没人调 `ReevaluatePrereqs`，所以封锁状态不变。
- 根因：**持续效果的前置重判（ReevaluatePrereqs）被耦合在单机 View 订阅里，未下沉到核心/装配层**。联机服务端因此漏掉这段规则副作用。
- 同类风险：`CovertChanged → ReevaluateAuras` 也依赖同一 View 订阅，联机同样会漏。

---

## 三、重点排查方向的结论

| 排查方向 | 结论 |
|---|---|
| 状态同步机制 | `IsTapped`、宿主关系、区域归属都能经快照往返；宿主关系此前已补 `AttachedToUid/SealedByUid`。数据同步本身 OK。 |
| UI 渲染逻辑 | 附属渲染分支存在「Setup/AddChild 时序」「固定偏移」「缺横置旋转」三处迁移遗漏。 |
| 持续效果更新 | `TappedChanged/CovertChanged → ReevaluatePrereqs/ReevaluateAuras` 的联动在 View 订阅里，联机服务端缺失。 |
| 秘社结附网络同步 | 快照已补宿主 UID，客户端可恢复 `AttachedTo`；但渲染挂载定位不对。 |
| 横置状态同步与表现 | 数据同步 OK，视觉旋转缺失。 |
| PM 命令路径差异 | 单机 `PMCommandManager` 直接改本地 Model + View 回调刷新；联机 DebugCommand 改服务端 Model，靠快照投影刷新，但服务端无 View 订阅，副作用（持续效果）缺失。 |

---

## 四、修复计划（尽可能不动单机）

### 4.1 修复问题 1（卡背）

- 文件：`Script/UI/ArenaOl.cs`，`RebuildAttachmentsAndSeals`。
- 改动：把 `ui.Setup(inst)`（及 `SetFace`）移到 `hostContainer.AddChild(ui)` 之后，与 `RebuildInto`/`RebuildAreaZoneCards` 保持同一「先入树再 Setup」时序。
- 单机影响：无（只改联机渲染）。

### 4.2 修复问题 2 + 3a（位置偏移 + 横置旋转）

- 文件：`Script/UI/ArenaOl.cs`，`RebuildAttachmentsAndSeals`。
- 改动：
  1. 对「结附于卡（角色/秘社）」的附属：移植单机 `ArrangeAllAttachments` 的核心定位算法（X 对齐宿主视觉中心、Y 从宿主下缘向上压 2/5、多张依次下延），并设置 `PivotOffset/Size/RotationDegrees(横置90°)/ZIndex` 与全局 `TargetPosition`。
  2. 对「结附于地区/封印于地区」的附属/封印：复用 `CardLayoutHelper.LayoutCardsEvenly`（会自动按 `IsTapped` 旋转 + 居中）。
  3. 确保 `ui.SetTapped(inst.IsTapped)` 后再统一排版，让旋转生效。
- 单机影响：无（只改联机渲染；算法可参考/移植，不改 `Arena.Layout`）。

### 4.3 修复问题 3b（持续效果不更新）

- 方案：把持续效果联动从「单机 View 订阅」下沉到「Composition Root 装配层」，使单机与联机服务端都执行。
- 文件：`Script/Manager/GameInitializer.cs`（在 `CardManager` 创建后订阅 `CardManager.OnCardStateChanged`）。
- 改动：
  - `TappedChanged` → `effectEngine.ReevaluatePrereqs(card.ControllerId)`
  - `CovertChanged` → `effectEngine.ReevaluateAuras(card.ControllerId)`
- 单机影响：`Arena.Events` 现有订阅可保留（`ReevaluatePrereqs/ReevaluateAuras` 幂等，重复触发无害），后续再清理；也可在确认后移除 Arena 订阅，但本轮为了「尽量不动单机」先保留。
- 备选（若想更彻底下沉）：在 `CardManager` 内部转发 `OnStateChanged` 时直接处理，但 `CardManager` 注入时机/客户端投影为 null 的风险更高，故优先选装配层订阅。

---

## 五、验证计划

1. 编译：`dotnet build UWPro.csproj` 0 警告 0 错误。
2. 跑现有逻辑测试，确认无新增失败（重点 JC012/JC068/JC020/JC005）。
3. 双进程手动冒烟（`启动联机三进程测试.bat`）：
   - 打出 JC012 结附本方秘社：卡面正面、位置对齐秘社、双击激活有响应。
   - `attach inst_57 inst_30`：结附卡位置对齐宿主。
   - `TAP INST_57`：卡面横置旋转；对手可暗藏（封锁解除）。
   - 竖置后对手再次不能暗藏（封锁恢复）。

---

## 六、风险与注意事项

- 4.2 的偏移算法若移植不完整，可能仍有视觉偏差；优先覆盖「结附角色/秘社」单附属的常见情况，多附属/横置宿主作为同算法一并处理。
- 4.3 会新增一次全局状态订阅，需注意 `_ExitTree` 反订阅，避免跨局残留（与现有 Arena 反订阅纪律一致）。
- 所有修复集中在联机客户端渲染与装配层，不触碰单机 `Arena`/`CardDragController`/`EffectEngine` 核心执行逻辑。
