# 秘社卡 TRES 字段修正对照

> 日期：2026-10-02
> 范围：`Cards/Data/CouncilCards/**`（秘社卡 **17** 个 `.tres`） × `Cards/Data/PlayerCards/**`（玩家卡 **729** 个 `.tres`）
> 判据：**不要求字段集合一致，但语义相同的字段必须同名同型**。
> 结论：真正违反该判据的字段只有 **颜色** 与 **阵营** 两项，已修正；其余差异为双方各自独有语义，本次不动（见 §5 观察项）。

---

## 1. 字段级对比总览

| 语义 | 玩家卡字段（729 张实测） | 秘社卡原字段（17 张实测） | 是否合规 | 处置 |
|---|---|---|---|---|
| 颜色 | `Colors` : `Array[CardColor]`，`.tres` 写作 `Colors = Array[int]([1])` | `ColorText` : `String`，复合 `"黄·帷幕守望"` | ❌ **命名+类型+职责三重不一致** | 拆分：删 `ColorText`，新增 `Colors` |
| 阵营 | `Organization` : `String` | `Organization` : `String`（同名），但 **009~017 取值错误** | ❌ **值口径不一致** | 改值（9 张） |
| 基类三件套 | `UID` / `CardName` / `Score`（`BaseCardData`） | 同名同型 | ✅ | — |

### 颜色枚举对照（`Script/Enums/Enums.cs` → `CardColor`）

| 中文色名 | 枚举值 | `.tres` 写法 |
|---|---|---|
| 中立/棕 | `Brown = 0` | `Array[int]([0])` |
| 黄 | `Yellow = 1` | `Array[int]([1])` |
| 白 | `White = 2` | `Array[int]([2])` |
| 绿 | `Green = 3` | `Array[int]([3])` |
| 灰 | `Gray = 4` | `Array[int]([4])` |
| 紫 | `Purple = 5` | `Array[int]([5])` |
| 蓝 | `Blue = 6` | `Array[int]([6])` |
| 黑 | `Black = 7` | `Array[int]([7])` |
| 红 | `Red = 8` | `Array[int]([8])` |

> 该映射不是推测：按玩家卡 `Organization` 分组统计 `Colors` 的主流取值，得到自洽且唯一的结果（帷幕守望→1、猎魔人→3、王座会→6、鸣钟教派→8、国家机构→4、圣贤→2、方碑序列→7、梦境行者→5、中立/无组织→0），与秘社卡 `ColorText` 的色名段逐条吻合。

---

## 2. 必修项①：`ColorText` 拆分（17/17 全量）

| 项 | 内容 |
|---|---|
| **原字段名 / 类型** | `ColorText` / `String` |
| **原值样例** | `"黄·帷幕守望"`、`"中立"` |
| **问题描述** | ① 单一字段同时承载**颜色**与**阵营**两种语义；② 玩家卡的颜色字段叫 `Colors` 且为 `Array[CardColor]` 枚举数组，秘社卡叫 `ColorText` 且为字符串 —— 语义相同却**不同名、不同类型**，无法被共用代码（如 `TargetValidity`、构筑校验、UI 展示）统一处理；③ 字符串形式的颜色无法参与枚举比较，只能靠文本解析。 |
| **修正后字段名** | `Colors` : `Array[CardColor]`（与 `PlayerCardData.Colors` **同名同型**；`.tres` 写法沿用玩家卡的 `Array[int]([N])`） |
| **修正后值** | 取原 `ColorText` 中 `·` **之前**的色名，按上表映射为枚举整数 |
| **是否删除原字段** | **是**。`CouncilCardData.ColorText` 属性及其全部代码引用一次性删除，不做双写 |

---

## 3. 必修项②：`Organization` 纠错（9/17 变更）

| 项 | 内容 |
|---|---|
| **原字段名 / 类型** | `Organization` / `String`（字段名本来就合规，不动） |
| **错误来源（已定位）** | `秘社制作交接提示词.md` 第 31 行的生成规则 **`Organization = CardName`**。该规则对 001~008 无副作用（卡名恰好＝阵营名），但对 009~017 产生 9 处错误值。该行已同步改为反例警示。 |
| **问题描述** | 现存的 `Organization` 值（`貘组`／`S．P．T执行部`／`爱弗罗德家族`／`计时者`／`哨兵`／`隐士`／`颂亡者`／`梦魇族群`／`秘社`）**在 729 张玩家卡的 `Organization` 中 0 匹配**，既不是阵营口径，也无法被任何"组织"类检索/合法性判定命中。 |
| **修正依据** | 取原 `ColorText` 中 `·` **之后**的阵营名（即 XML `prop/colors` 的 `·` 后段）。反证：这 8 个阵营名**全部**出现在玩家卡 `Organization` 取值集合中（其中 `梦境行者` 有 65 张玩家卡），与 `TargetValidity.cs:295`（唯一运行时消费方）的比对口径天然兼容。另：010~017 的 `SubType` 均为"分支/部门/家族"类，语义上隶属于该母派系，与改后取值一致。 |
| **修正后字段名** | `Organization`（**不变**） |
| **修正后值** | 帷幕守望 / 猎魔人 / 王座会 / 鸣钟教派 / 国家机构 / 圣贤 / 方碑序列 / 梦境行者；009 因无 `·` 段，按确认口径取 `"中立"` |
| **特殊说明（009 秘社）** | 原 `ColorText = "中立"` 无 `·`，不存在阵营段；`Colors` 取 `Array[int]([0])`（Brown），`Organization` 取 `"中立"`，与玩家卡中立牌（`Organization` 为空串或"中立"）口径相容 |

---

## 4. 逐卡修正对照表（17 张）

`Colors` 列的 `[N]` 为 `CardColor` 枚举整数。

| # | 文件 | UID | CardName | 原 `ColorText` | 原 `Organization` | ✎ 修正后 `Colors` | ✎ 修正后 `Organization` |
|---|---|---|---|---|---|---|---|
| 1 | `MSJC001_weimushouwang.tres` | MSJC001_weimushouwang | 帷幕守望 | `黄·帷幕守望` | 帷幕守望 | `Array[int]([1])` | 帷幕守望 ✅不变 |
| 2 | `MSJC002_liemoren.tres` | MSJC002_liemoren | 猎魔人 | `绿·猎魔人` | 猎魔人 | `Array[int]([3])` | 猎魔人 ✅不变 |
| 3 | `MSJC003_wangzuohui.tres` | MSJC003_wangzuohui | 王座会 | `蓝·王座会` | 王座会 | `Array[int]([6])` | 王座会 ✅不变 |
| 4 | `MSJC004_mingzhongjiaopai.tres` | MSJC004_mingzhongjiaopai | 鸣钟教派 | `红·鸣钟教派` | 鸣钟教派 | `Array[int]([8])` | 鸣钟教派 ✅不变 |
| 5 | `MSJC005_guojiajigou.tres` | MSJC005_guojiajigou | 国家机构 | `灰·国家机构` | 国家机构 | `Array[int]([4])` | 国家机构 ✅不变 |
| 6 | `MSJC006_shengxian.tres` | MSJC006_shengxian | 圣贤 | `白·圣贤` | 圣贤 | `Array[int]([2])` | 圣贤 ✅不变 |
| 7 | `MSJC007_fangbeixulie.tres` | MSJC007_fangbeixulie | 方碑序列 | `黑·方碑序列` | 方碑序列 | `Array[int]([7])` | 方碑序列 ✅不变 |
| 8 | `MSJC008_mengjingxingzhe.tres` | MSJC008_mengjingxingzhe | 梦境行者 | `紫·梦境行者` | 梦境行者 | `Array[int]([5])` | 梦境行者 ✅不变 |
| 9 | `MSJC009_mishe.tres` | MSJC009_mishe | 秘社 | `中立` | **秘社** ❌ | `Array[int]([0])` | **中立** ✎ |
| 10 | `MSJC010_mozu.tres` | MSJC010_mozu | 貘组 | `黄·帷幕守望` | **貘组** ❌ | `Array[int]([1])` | **帷幕守望** ✎ |
| 11 | `MSJC011_SPTzhixingbu.tres` | MSJC011_SPTzhixingbu | S．P．T执行部 | `绿·猎魔人` | **S．P．T执行部** ❌ | `Array[int]([3])` | **猎魔人** ✎ |
| 12 | `MSJC012_aifuluodejiazu.tres` | MSJC012_aifuluodejiazu | 爱弗罗德家族 | `蓝·王座会` | **爱弗罗德家族** ❌ | `Array[int]([6])` | **王座会** ✎ |
| 13 | `MSJC013_jishizhe.tres` | MSJC013_jishizhe | 计时者 | `红·鸣钟教派` | **计时者** ❌ | `Array[int]([8])` | **鸣钟教派** ✎ |
| 14 | `MSJC014_shaobing.tres` | MSJC014_shaobing | 哨兵 | `灰·国家机构` | **哨兵** ❌ | `Array[int]([4])` | **国家机构** ✎ |
| 15 | `MSJC015_yinshi.tres` | MSJC015_yinshi | 隐士 | `白·圣贤` | **隐士** ❌ | `Array[int]([2])` | **圣贤** ✎ |
| 16 | `MSJC016_songwangzhe.tres` | MSJC016_songwangzhe | 颂亡者 | `黑·方碑序列` | **颂亡者** ❌ | `Array[int]([7])` | **方碑序列** ✎ |
| 17 | `MSJC017_mengyanzuqun.tres` | MSJC017_mengyanzuqun | 梦魇族群 | `紫·梦境行者` | **梦魇族群** ❌ | `Array[int]([5])` | **梦境行者** ✎ |

> 数据 preservación：改写只对 `ColorText` 行与 `Organization` 行做行内替换，其余字段（含多行 `CardText`、FAQ 文本、全角句号 `S．P．T执行部`、`ext_resource`/`sub_resource` 块、`metadata`、行尾 CRLF/LF）保持原样 —— `git diff` 显示 17 个文件仅新增/删除目标行（除这些文件在开工前已有的用户未提交改动）。UTF-8 无 BOM、原换行风格（001~009 LF、010~017 CRLF）均已保留。

---

## 5. 观察项（判定为**不违反**判据，本次不改）

### 5.1 秘社卡独有语义（玩家卡无对应物，不存在"同名冲突"）
`StartingHandSize`、`Restrictions`(子资源)、`CardText`、`CardImagePath`、`DrawFromBottom`、`SubType`。

### 5.2 玩家卡独有语义（秘社不适用）
`SubCardName`、`UniqueField`、`CategoryFlag`、`SubCategories`、`MagicDomains`、`Investigation`、`Strength`、`Power`、`FirstStrike*`、`Defense`、`ExtraCost*`、`AllowedTargets`、`TargetRequirement`、`LimitToActionPhaseOnly`、`PlayableFromGraveyard`。

### 5.3 语义相近但建议**保持现状**（改动会波及大量既有代码）
| 秘社 | 玩家 | 结论 |
|---|---|---|
| `SubType` : `String`（`"秘社-法师结社/吸血鬼"`，单值复合串） | `SubCategories` : `Array[String]` | 名称与类型均不同，但秘社的子类型是一整串带斜杠的层级路径，拆成数组会丢失语义；且两者上层（`AreaCardData`）也用 `SubCategories`。**暂不改** |

### 5.4 格式瑕疵（低优先，非本次范围）
- 部分秘社 `.tres` 缺少 `gd_resource` 头部的 `uid=`（009/012 等），Godot 会在重新导入时自行补写。

---

## 6. 变更清单

### 6.1 数据类（`Script/Data/Cards/CouncilCardData.cs`）
- 删除：`[Export] public string ColorText { get; set; } = "";`
- 新增（与 `PlayerCardData` 同签名、同默认值）：
  ```csharp
  [Export] public Array<CardColor> Colors { get; set; } = new Array<CardColor> { CardColor.Brown };
  ```
- `Organization` 位置前移到颜色字段旁，注释补充说明取值口径（取 XML `prop/colors` 的 `·` 后段）。

### 6.2 数据文件（17 个 `.tres`）
按 §4 表，`ColorText` 行 → `Colors = Array[int]([N])`；9 张改 `Organization` 值。

### 6.3 导出链路
- `Script/Tools/CardTresToJson.cs`：秘社分支去掉 `ColorText = template.ColorText,`，改为
  `foreach (var c in template.Colors …) d.Colors.Add(c.ToString());` —— **复用 POCO 通用 `Colors`**，不再新增第二个同语义字段。
- `Script/Net/CardTemplateData.cs`：删除 Council 专有段 `public string ColorText;`；通用 `Colors` 注释补充"秘社卡共用"。

### 6.4 测试
- `Script/Tests/CouncilEffectTests.cs:61`：
  `Assert.AreEqual("黄·帷幕守望", tmpl.ColorText, …)` →
  `Assert.AreEqual(1, tmpl.Colors.Count, …)` + `Assert.AreEqual(CardColor.Yellow, tmpl.Colors[0], …)`。
  同测试第 64 行 `Organization == "帷幕守望"` **未改动且仍然成立**（001 本就是正确值）。

### 6.5 产物与文档
- `Cards/card_templates.json`：删除 701 处已废弃的 `"ColorText"` 键行（逐行扫描保证不会删到对象末属性；删除后 JSON 仍能通过解析校验）。⚠️ 该文件本身是**过期快照**（只含 1 条秘社记录，且 `Colors` 为空），建议重跑 `Script/Tools/CardExportRunner.cs` 全量重导出以同步新的 `Colors`/`Organization`。
- 文档：`需求实现对照.md:940`、`秘社制作交接提示词.md:31/39`、`JC卡牌效果进度表.md:1009`、`JC卡牌效果实现清单.md:1074` 的旧口径已更正（含对 `Organization = CardName` 这一错误规则的显式标注）。

---

## 7. 风险评估

### 7.1 引用点全表（互斥扫描 `Script/`、`Cards/Script/`、`.tres`、`.tscn`、`json`、md）

| 引用点 | 说明 | 等级 | 处置 |
|---|---|---|---|
| `Script/Tests/CouncilEffectTests.cs:61` | 全仓**唯一**断言 `ColorText` 的测试，不同步改必挂 | 高 | 已同步改，见 §6.4 |
| Godot 资源导入一致性 | 类属性与 `.tres` 行必须同步；若残留 `ColorText` 行会被 Godot 以未知属性丢弃（不崩溃但静默丢数据） | 中 | 类、17 个 `.tres`、导出工具一次性改完；全仓扫描 `ColorText` 在 `.tres/.tscn/.cs/.json` 中已 **0 命中** |
| `Script/Manager/TargetValidity.cs:295` `t.PlayerTemplate?.Organization ?? t.CouncilTemplate?.Organization` | 秘社 `Organization` 的**唯一运行时消费方**；实测 `Cards/Script/**` 下**没有任何卡牌效果**用 `TargetRestriction.Organization` 指向秘社模板 ⇒ 当前**零行为变化**；改后反而能匹配真实阵营的玩家卡 | 低（且有正向收益） | 无需改代码 |
| `Script/Manager/DeckSearchFilter.cs:49/100`、`CostModFilter.cs:60`、`TurnManager`、`UI/DeckBuilder.cs:532/581`、`CardUI`、`Arena` | 全部限定 `PlayerCardData`（签名层隔离），读不到 `CouncilTemplate` | 无 | — |
| `Script/Tools/CardTresToJson.cs` + `Script/Net/CardTemplateData.cs` | POCO 字段增删 ⇒ JSON 协议形状变化；本仓**无 ServerCore 消费方**（`ServerCore/CardTemplateRepository.cs` 不存在） | 低 | 已同步 |
| `Cards/card_templates.json` | 过期快照，无运行时加载方 | 低 | 已剔除废弃键；建议整体重导出 |
| Godot 反射写法（`Set("X")` / `Get("X")` / `["X"]` / `PropertyName.X`） | **无命中**（仅 `DeckListLoader` 的 `deckName/templatePath/copies`、`CardTest` 的 `Counters["Damage"]` 等无关键），不存在因改名而静默失效的路径 | 无 | — |
| 假设"秘社组织名＝卡名"的字符串常量 | `Script/`、`Cards/Script/` 下**0 命中**（`貘组/哨兵/隐士/颂亡者/梦魇族群/S．P．T执行部/计时者/爱弗罗德家族`） | 无 | — |
| 4 份 md 文档 | 仅口径描述，但会导致后续批次照抄错误规则 | 低（流程风险） | 已更正 |
| `packages/godotsharp/**` | NuGet 包内 XML 文档中的同名 XRD 词素，**禁止改动** | 无 | 排除 |

### 7.2 总体风险等级：**低**

- 编译验证：`dotnet build UWPro.csproj` ➜ **0 警告 / 0 错误**。
- 运行时该改动**不改变任何现有判定结果颜色/组织筛选均新增字段消费方，旧的 17 张秘社在改前后唯一的组织消费点（TargetValidity）从未被秘社模板触发过。
- 唯一需要留意的是**后续批次**：必须按新口径（`prop/colors` 拆 `Colors` + `Organization`）落盘，交接文档已改写。

### 7.3 回归建议（本机无 Godot 可执行文件，未能跑 headless 回归）
1. Godot 编辑器打开工程，确认 17 张秘社 InspectoF 中 `Colors` 正常显示、`ColorText` 已消失且**无导入告警**。
2. 编辑器内运行 `Script/Tests/CouncilEffectTests` 与 `JCDataTests`（后者同样读这 17 个 `.tres`），预期全绿。
3. （可选）重跑 `CardExportRunner` 全量导出 `card_templates.json`，核对 17 条秘社记录均含非空 `Colors` 与新 `Organization`。
