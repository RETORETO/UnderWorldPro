# PM 命令说明（调试 / 裁判）

> 依据代码整理：实现全部集中在 `Script/Manager/PMCommandManager.cs`（`public partial class PMCommandManager : Node`）。
> 本文覆盖该文件中**全部可用命令**，未实现的命令一律不列入（文末单独标注"提示文案中提到但没有实现"的项）。

---

## 1. 入口与操作方式

| 场景 | 入口 |
| --- | --- |
| 单机 / 本地 | 在 `Arena` 场景中按 **反引号 `` ` ``** 开关命令输入框（`Script/UI/Arena.cs` → `_Input`，`Key` = 96）。输入框为一个 `TextEdit` 节点 `CommandInput`，初始隐藏。 |
| 联机 / 服务端 | `GodotIntentRouter.DebugCommand(int playerId, string commandLine, out string error)` → 内部调用 `PMCommandManager.TryExecuteCommand`。失败原因经 `error` 回填 `IntentResult.Error` 回传客户端。 |

输入框内的按键约定：

- **Enter** → 换行（可一次写多条，每行一条）
- **Shift + Enter** → 从上往下执行全部命令，执行后清空内容并保持聚焦
- 以 `#` 开头的行 = 注释，跳过；空行跳过

生命周期：`PMCommandManager` 由 `GameInitializer`（Composition Root）创建并注入，`Arena.InitializePMCommands()` 只取引用。因此**没有 View 的场景（headless / 纯服务端）也能执行 PM 命令**，只是部分依赖 UI 回调的能力会降级。

---

## 2. 通用语法规则

- 一行一条命令，按**空格**切分 token（连续空空格自动忽略）。
- **命令名不区分大小写**（`parts[0].ToLower()`）。
- 未知命令 → 打印并回执 `未知命令: xxx（输入 help 查看帮助）`。

### 2.1 玩家参数（`1` = P1，`2` = P2）

**统一口径**：凡带卡牌参数的命令，**玩家参数必须写在第一位**，且它**同时**决定：

1. 从哪个玩家的卡组 / 区域里找牌（搜索域）
2. 找到的卡加到哪个玩家

- 玩家参数**缺失或不是 `1`/`2` 一律报错**，不会静默按 P1 处理。
- 例外：`attach` / `attacharea` / `seal` / `sealarea` 的玩家参数**可省略**，省略时取**当前行动权玩家**（`TurnManager.PriorityPlayerId`，取不到则 P1），并打印一行提醒。

### 2.2 卡牌引用（`<卡牌编号|实例UID>`）

解析顺序固定为：**① 实例 UID → ② 卡牌编号**：

1. **实例 UID**：如 `inst_37`，在全局实例表中精确命中。
2. **卡牌编号**：取模板 UID 中**第一个下划线之前**的部分，不区分大小写。例如 `JC015`、`JC015_weimushouwanghuiji` 都按 `JC015` 匹配。
   - 搜索范围**严格限定在玩家参数指定的玩家内**，顺序为：卡组 → 其它区域（手牌 / 资产 / 争夺区 / 附属 / 封印 / 墓地 / 秘社 / 堆叠）。
   - 同一编号有多张时**随机挑一张**(仅定向取牌 `draw` / `setassetfromdeck` 除外)。
   - 找不到 → 明确报错，并附带"该编号在 P1/P2 卡组各几张、其它区域几张"的分布提示，**绝不静默降级**。

### 2.3 地区编号

- 除 `damage` / `trauma` 的 `area` 目标外，所有地区参数都是 **1..N**（1 = 第 1 个地区槽位），越界会明确报错并提示当前共有几个地区。
- 地区 UID 列表由 `Func<List<string>>` **每次执行时实时查询**，地区赢取 / 替换后不会落到失效槽位。

### 2.4 批量执行与中断规则

`ExecuteCommandBatch` 从上往下逐行执行。遇到**阻塞类命令**时，执行完该行即**中止**，后续所有命令被舍弃并打印提示。

- 阻塞类命令只有两条：`investigation` / `battle`（会打开对抗窗口等待玩家响应）。
- 判定是**模式感知**的：单机（`_openCombatWindow != null`）才算阻塞；**联机服务端**（窗口经决策协议异步下发）不阻塞，后续命令照常执行。
- `power` 是**瞬时结算**（直接加势力标记、不弹窗），**不阻塞**批量。

---

## 3. 命令总览

| 命令 | 用法 |
| --- | --- |
| `draw` | `draw <玩家 1\|2> [数量] [卡牌编号]` |
| `player1draw` / `player2draw` | `player1draw [数量]` / `player2draw [数量]` |
| `move` | `move <玩家 1\|2> <卡牌编号\|实例UID> <目标> [目标玩家 1\|2] [top\|bottom]` |
| `setassetfromdeck` | `setassetfromdeck <玩家 1\|2> <num>` 或 `<玩家 1\|2> <卡牌编号> [num]` |
| `returnhandtodeck` | `returnhandtodeck <玩家 1\|2>` |
| `investigation` | `investigation <玩家 1\|2> <地区 areaNo> <X>` |
| `battle` | `battle <玩家 1\|2> <地区 areaNo> <X>` |
| `power` | `power <玩家 1\|2> <地区 areaNo> <点数>` |
| `jumpto` / `jump` | `jumpto <步骤名>[地区号]` |
| `attach` | `attach [玩家 1\|2] <附属编号\|UID> <宿主编号\|UID>` |
| `attacharea` | `attacharea [玩家 1\|2] <附属编号\|UID> <地区编号>` |
| `detach` | `detach <附属UID>` |
| `seal` | `seal [玩家 1\|2] <封印卡编号\|UID> <目标编号\|UID>` |
| `sealarea` | `sealarea [玩家 1\|2] <封印卡编号\|UID> <地区编号>` |
| `damage` | `damage area\|card\|hand <目标> <数值>` |
| `trauma` | `trauma area\|card\|hand <目标> <数值>` |
| `heal` | `heal card <UID> [数值]` |
| `kill` | `kill card <UID>` |
| `reset` | `reset` |
| `list` | `list` |
| `tap` | `tap <UID>` |
| `untap` | `untap <UID>` |
| `flip` | `flip <UID>` |
| `covert` | `covert <UID> [on\|off]` |
| `deck` / `printdeck` | `deck [1\|2]` / `printdeck` |
| `addarea` | `addarea [数量]` |
| `help` / `?` | `help` / `?` |

---

## 4. 命令详解

### 4.1 抽牌

#### `draw <玩家 1|2> [数量] [卡牌编号]`

四种形态：

| 写法 | 行为 |
| --- | --- |
| `draw 2` | P2 从牌库顶抽 1 张 |
| `draw 1 5` | P1 从牌库顶抽 5 张 |
| `draw 2 JC015` | **定向取牌**：从 P2 卡组取 1 张 `JC015` 加入 **P2** 手牌 |
| `draw 2 1 JC015` | 同上（显式数量；**只支持 1**，写其它数字会报错） |

定向取牌的语义要点：

- 不算"抓牌"：不广播 `OnDraw`、不触发"每当你抓牌时"、不受"梦魇族群从牌库底抓牌"重定向、**空库不判负**（与 JC049「非抓牌式取牌」同口径）。
- 卡组里没有该编号 → 明确报错（含双方分布提示），**绝不静默降级为普通抽牌**。

#### `player1draw [数量]` / `player2draw [数量]`

固定玩家号从牌库顶抽牌，数量缺省 1。只是 `draw` 的便捷别名。

### 4.2 移动

#### `move <玩家 1|2> <卡牌编号|实例UID> <目标> [目标玩家 1|2] [top|bottom]`

- 第一个玩家参数是**搜索域**；目标是 `hand`/`grave`/`asset`/`score`/`deck` 时，缺省"加到搜索玩家"，可用第 4 个参数**显式覆盖目标玩家**。
- 目标取值：

| 目标 | 含义 |
| --- | --- |
| `1..N` | 争夺区编号（越界明确报错） |
| `hand` | 手牌 |
| `grave` | 墓地 |
| `deck` | 牌库（缺省**底部**） |
| `deck` + `top` / `bottom` | 牌库顶 / 底（`decktop` / `deckbottom` 也可直接作目标） |
| `score` | 得分区 |
| `asset` | 资产区（保留状态） |

- 执行后打印移动前后的 `OwnerId` / `ControllerId` 变化。

```
move 2 JC014 hand      # P2 卡组取 1 张 JC014 加入 P2 手牌
move 1 inst_37 2       # 实例 inst_37 移动到争夺区 2
move 1 JC003 deck top  # P1 的 JC003 放回 P1 牌库顶
move 1 JC003 hand 2    # P1 的 JC003 加入 P2 手牌
```

#### `setassetfromdeck <玩家 1|2> <num>` / `<玩家 1|2> <卡牌编号> [num]`

快速建立资产，从该玩家卡组取牌进**该玩家**资产区。

```
setassetfromdeck 1 12      # P1 卡组顶 12 张进 P1 资产区
setassetfromdeck 1 JC012   # P1 卡组中 JC012 取 1 张进资产区
setassetfromdeck 1 JC012 2 # 取 2 张（编号不重复取）
```

#### `returnhandtodeck <玩家 1|2>`

把该玩家**所有手牌**按原顺序放回其卡组**底部**（不洗牌）。

### 4.3 对抗

三条命令共用参数解析：`Cmd <玩家 1|2> <地区 areaNo> <X>`，其中 `<玩家>` 是**胜者**，地区号 **1..N**，`X` 必须是正整数。

| 命令 | 行为 | 结算方式 |
| --- | --- | --- |
| `investigation` | 调查对抗：胜者从牌库顶查看 X 张，分配放回顶/底后抽 1 张 | 单机 = 弹本机窗口；联机 = 经决策协议下发到胜者客户端 |
| `battle` | 战斗对抗：败方该地区角色分配 X 点伤害 | 同上 |
| `power` | 势力对抗：给该玩家在该地区加 X 个势力标记 | **瞬时结算，不弹窗**；结尾统一触发一次 `TryStartAreaWin`（标记达标即结算赢取地区） |

```
investigation 2 1 1   # P2 在地区1 调查对抗获胜 X=1
battle 1 1 2          # P1 在地区1 战斗对抗获胜 2 点
power 1 1 5           # P1 在地区1 加 5 个势力标记
```

> 联机服务端下 `investigation`/`battle` 走 `CombatManager.ResolveCombatFromDebug`（经决策协议下发），不再因 `_openCombatWindow == null` 而静默无效。

### 4.4 步骤跳转

#### `jumpto <步骤名>[地区号]`（别名 `jump`）

**直接跳到指定步骤，中间经过的步骤一律不结算**（其触发/效果都不结算），只进入目标步骤并执行它正常的进入逻辑。

步骤名 = `TurnManager.TurnPhase` 枚举名，**不区分大小写**：

```
StartReset  StartPrepare  StartDraw  ActionFirst  ActionSecond
PreCombat   CombatInvestigation  CombatStrength  CombatPower
EndCleanup  EndRecover  EndPassTurn  Mulligan
```

**对抗类步骤可带地区号**（紧贴在名字后，省略 = 地区 1；`PreCombat` 不带地区号）：

```
jumpto EndRecover            # 跳到恢复步骤
jumpto CombatInvestigation1  # 跳到 1 地区调查对抗
jumpto CombatStrength2       # 跳到 2 地区战斗对抗
jumpto CombatPower3          # 跳到 3 地区势力对抗
```

### 4.5 附属 / 封印

这四个命令的玩家参数**可省略**，省略时取当前行动权玩家并打印提醒。都接受编号或实例 UID。

| 命令 | 用法 | 说明 |
| --- | --- | --- |
| `attach` | `attach [玩家 1\|2] <附属编号\|UID> <宿主编号\|UID>` | 宿主**自动限定为场上卡**（`AreaZone`/`AttachedZone`），不会挑错到卡组里的同编号 |
| `attacharea` | `attacharea [玩家 1\|2] <附属编号\|UID> <地区编号>` | 结附到地区（不指向具体角色） |
| `detach` | `detach <附属UID>` | 仅断开引用，**不移动区域** |
| `seal` | `seal [玩家 1\|2] <封印卡编号\|UID> <目标编号\|UID>` | 目标**必须是场上卡**（`AreaZone`）；封印卡移入封印子区域 |
| `sealarea` | `sealarea [玩家 1\|2] <封印卡编号\|UID> <地区编号>` | 封印到地区（`SealedBy=null` + `ZoneDetailId=地区UID` 标识） |

```
attacharea 1 JC012 1     # P1 的 JC012 结附到地区1
seal 1 JC020 inst_5      # P1 的 JC020 封印到场上目标实例
detach inst_12           # 解除结附
```

> `attach` 若附属已结附 → 报 `已结附于 xxx，先 detach`；`seal` 若封印卡已封印别人 → 报 `已被 xxx 封印，先 unseal`（**但 `unseal` 命令并未实现**，见第 7 节）。

### 4.6 状态修改（伤害 / 治疗 / 死亡 / 翻面）

> 这一组命令**只接受实例 UID**（不解析卡牌编号），且**不接受玩家参数**。

| 命令 | 用法 | 说明 |
| --- | --- | --- |
| `damage` | `damage area\|card\|hand <目标> <数值>` | 造成伤害 |
| `trauma` | `trauma area\|card\|hand <目标> <数值>` | 造成创伤（同 `damage` 参数） |
| `heal` | `heal card <UID> [数值]` | 有数值 = 移除该数量伤害；**无数值 = 完全清除伤害与创伤** |
| `kill` | `kill card <UID>` | 造成等于当前防御的伤害（无防御的卡会提示不能被杀死） |
| `reset` | `reset` | 重置**所有**卡的伤害标记 |
| `list` | `list` | 列出手牌 / 地区 / 墓地的卡与防御·伤害·创伤 |

`damage` / `trauma` 的目标三种：

| 目标 | 参数 | 索引基准 |
| --- | --- | --- |
| `area` | 地区索引 | **0-based**（`0 .. 地区数-1`） |
| `card` | 实例 UID | — |
| `hand` | 手牌索引 | **0-based**，且**固定取 P1 手牌** |

```
damage area 0 2        # 对第 1 个地区的所有角色造成 2 点伤害
damage card inst_5 3
trauma hand 0 1        # 对 P1 手牌第 1 张造成 1 点创伤
heal card inst_5       # 完全清除
kill card inst_5
```

#### 翻面 / 横置 / 暗藏

| 命令 | 用法 | 说明 |
| --- | --- | --- |
| `tap` | `tap <UID>` | 横置（与支付费用横置一致，发 `TappedChanged`，持续效果即时重判） |
| `untap` | `untap <UID>` | 解除横置 |
| `flip` | `flip <UID>` | 翻面（正面↔背面）。**无论正反翻面都清空指示物、附属（进弃牌堆）、封印（回 OWNER 手牌）**；翻到正面走 `CardManager.RevealCard`（暗藏者现身，触发进场/现身时机） |
| `covert` | `covert <UID> [on\|off]` | 强制设/清 `IsCovert`（缺省 `on`）。`on` = 背面潜伏，`off` = 正面并复位到基础状态。仅用于验证"暗藏者免疫伤害"，非正式玩法入口 |

### 4.7 查看 / 场地

| 命令 | 用法 | 说明 |
| --- | --- | --- |
| `deck` | `deck [1\|2]` | 按卡组内**真实顺序**打印卡组内容（**索引 0 = 牌库顶**），默认玩家 1，逐张输出名称 / UID / 模板 / 费用 / 调查 / 战力 / 势力 / 防御 |
| `printdeck` | `printdeck` | `deck` 的别名，**实现完全相同**（同样接受 `printdeck 2`） |
| `addarea` | `addarea [数量]` | 从世界牌库抽地区卡并新建地区槽位（默认 1 个）；世界牌库空则提示 |
| `help` | `help` / `?` | 打印完整帮助 |

---

## 5. 完整示例

```
# 起手布置：P1/P2 各抓 5 张
draw 1 5
draw 2 5

# 定向取牌（不算抓牌，不触发抓牌时点）
draw 2 JC015

# 铺场
move 1 JC003 1
move 2 JC014 1
setassetfromdeck 1 3

# 附属 / 封印
attacharea 1 JC012 1
seal 1 JC020 inst_5

# 对抗（注意：单机下这行执行完即中止，后面的行会被舍弃）
battle 1 1 2
power 1 1 5

# 状态调整
damage card inst_5 2
heal card inst_5
jumpto EndRecover
```

---

## 6. 联机模式的差异

| 维度 | 单机 | 联机服务端 |
| --- | --- | --- |
| 入口 | Arena 输入框（反引号开关） | `GodotIntentRouter.DebugCommand(playerId, cmd, out error)` |
| 返回值 | 只看日志 | `bool` + `error`（回填 `IntentResult.Error` 给客户端提示） |
| `investigation` / `battle` | 弹本机窗口，**阻塞批量后续命令** | 经 `CombatManager.ResolveCombatFromDebug` + 决策协议下发，**不阻塞** |
| `power` | 瞬时结算，不阻塞 | 同左 |
| 地区列表来源 | Arena 注入的 `Func<List<string>>` | 缺省回退 `AreaManager.GetOrderedAreaUids()`，避免"命令什么都不做" |
| 依赖注入 | `Initialize(...)` | `InjectModelRefs(...)` + `AreaManagerRef` / `TurnManagerRef` / `CombatManagerRef` |

---

## 7. 已知限制与坑（按代码如实记录）

1. **`unseal` 命令不存在**：`seal` 的守卫报错文案是"…已被 xxx 封印，**先 unseal**"，但命令表里没有 `unseal`，也没有任何解除封印的 PM 命令。
2. **`detach` 只接受实例 UID**：不解析卡牌编号、不接受玩家参数（`_cardManager.GetInstance(parts[1])` 硬取）。
3. **状态修改组只认实例 UID**：`damage` / `trauma` / `heal` / `kill` / `tap` / `untap` / `flip` / `covert` 均直接 `GetInstance(token)`，传编号会报"未找到卡牌"。
4. **`damage hand` / `trauma hand` 固定 P1 手牌**（`GetHandCardsByOwner(1)` 硬编码），且索引 **0-based**；而其它命令的地区号是 **1-based**，两套基准不一致。
5. **`kill` / `heal` 必须写满 3 个 token**：`kill card <UID>`、`heal card <UID> [数值]` 中的第 2 个 token 内容不被校验，但**不能省**，否则触发"用法"提示。
6. **`deck` 与 `printdeck` 完全等价**（同一个 `ExecuteDeck`），帮助里并列只是为了兼容旧写法；两者都接受 `[1|2]`，无"世界牌库"打印能力。
7. **`damage area` 的区域索引 0-based**，与其它命令不一致，容易误伤到相邻地区。
8. **注释与实现不一致（仅文档层面）**：`ExecuteCommandBatch` 的 XML 注释仍写着"遇到 `investigation`/`battle`/`power` 立即中止"，但 `BlockingCommands` 已移除 `power`，实际 `power` 不中止。
9. **多张同名卡随机挑**：`move` / `attach` / `seal` 等在编号匹配到多张时用 `_rng.Next` 随机取一张；需要精确控制时请传实例 UID。
10. **UI 刷新依赖注入**：`tap`/`untap`/`flip`/`covert` 的 UI 表现依赖 Arena 注入的 `_refreshCardUI` / `_setFaceUp`；headless 下数据照改、UI 无表现。
