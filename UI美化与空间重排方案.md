# UI 美化与空间重排方案（存档 · 待选卡找卡方案完成后执行）

> **状态**：已定稿待执行。**执行优先级排在 `选卡找卡UI优化方案.md` 之后**（见第八章「与选卡找卡方案的接口约定」）。
> **基底**：2026-10-16 工作区源码（`Script/UI/**`、`Scenes/**`、`Script/Tests/P5LayoutMathTests.cs`）。
> **范围**：单机 `Arena` 与联机 `ArenaOl` 的**表现层（View）**布局几何与交互反馈重构。**不改规则逻辑、不写 Model、不改联机协议。**

---

## 一、目标

| # | 目标 |
|---|------|
| ① | **卡牌放大**：基础卡 100×140 → **140×196**，提升卡面可读性 |
| ② | **争夺区空间重排**：压缩上下为封印/附属预留的空白，保证横向 3 地区 × 每区 ≥3 张卡 + 余量，纵向容纳 2 争夺区 + 地区 + 手牌区 |
| ③ | **封印/附属折叠**：宿主卡**角标**（"附×2""封×1"）+ **hover 自动展开**浮层显示完整卡图，展开面板**可点选** |
| ④ | **资产/分数区紧凑**：**层叠**（露边条）+ **hover 展开**浮层，**保留卡图**（不可完全退化为数字），分数区同处理 |
| ⑤ | **手牌 hover 上浮**：悬停手牌卡时该卡上浮 + 放大 |
| ⑥ | **信息密度优化**（非卡面部分）：牌库/墓地计数 Label 字号、位置优化。卡面文字后续移除，本次不处理 |
| ⑦ | **联机视角归一化**：`ArenaOl` 按 `_localPlayerId` 让"自己永远在屏幕下方、对手在上方"（路线 A：布局装配重定向） |
| ⑧ | **对手手牌紧凑**：对手手牌区退化为"单张牌背 + 数字角标"，hover 展开显示张数（复用 ④ 的层叠机制） |

**判定口径**：①②③④ 共同服务于"在有限像素内最大化卡牌可读性与操作流畅度"。

---

## 二、明确不做（延后）

| 项 | 原因 |
|---|---|
| 操作面板（P1让过/P2让过/回合结束/投降）重构 | 单机为测试模式，可接受现状 |
| D 弹窗统一外壳 | 延后阶段；但 `CardPickWindow`（选卡方案新建）应预留统一外壳接口 |
| E 拖拽可逆性高亮与缓动 | 延后阶段 |
| G 快捷键与事件日志面板 | 游戏逻辑未完成 |
| 卡面文字（instanceId/类别等）处理 | 测试用途，后续直接移除 |
| 全局 CJK 字体主题 | 可选小项，需自备字体文件；不阻塞主体 |

---

## 三、技术栈与真相源

- **引擎**：Godot 4.7 + C#（GL Compatibility），沿用 `UWPro.csproj` / `UWPro.sln`
- **分辨率**：1920×1080，`stretch/mode=viewport`，**`aspect` 待定**（现状 `ignore` 会拉伸变形，见 6.7 待决项）
- **布局唯一真相源**：`Script/UI/CardLayoutHelper.cs`（单机 `Arena` 与联机 `ArenaOl` 共用）
- **层级唯一真相源**：`Script/UI/Layers.cs`（0~1000 分带，**禁止魔数**）

---

## 四、关键技术决策

### 决策 1：卡牌尺寸 140×196

- 由 100×140 放大到 **140×196**（1.4x，宽高比 5:7 保持不变）。
- **依据**：横向 3 地区 × 3 张 = 3×(140×3 + 间距) ≈ 1440px + 槽间距 < 1920，满足"3 地区每区 3 张 + 余量"。
- **常量收口**：`CardLayoutHelper.CARD_WIDTH/CARD_HEIGHT` 为唯一来源，消除三处硬编码副本：
  - `CardUI.cs:472`（`CARD_WIDTH=100/CARD_HEIGHT=140`）
  - `Arena.Layout.cs:22-24`
  - `AreaSlot.cs:111`（`cardWidth=140, cardHeight=100`，注意此处是地区卡横条尺寸，**与卡牌尺寸语义不同**，需区分命名）

### 决策 2：争夺区纵向空间重排

- **现状**：`AreaSlot` 设计 400×580，内部 `Player1Area`/`Player2Area` 各约 240px 高（为封印/附属上下偏移预留），中间 `CardContainer`（地区卡）140×100。
- **改造**：③ 折叠封印/附属后，上下区域不再需要为大偏移预留 → 按"一张放大卡 + 小余量"重设（约 200px）。
- **`ArrangeAreas()`**（`Arena.Layout.cs:413`）的 `slotWidth=400/slotHeight=580/spacing=30` 按新预算重算；槽宽需 ≥ 3 张放大卡宽度（140×3 + 间距 ≈ 480px）。
- **地区卡（AreaCardUI）** 尺寸与 `AreaSlot.cs:111` 的 `cardWidth/cardHeight` 同步。

### 决策 3：封印/附属角标 + hover 展开

- **折叠态**：改造 `Arena.Layout.SyncCardUIToZone` 的 `AttachedZone`/`SealedZone` 分支——**不再**把附属/封印 UI 堆到宿主容器参与偏移布局（`ArrangeAllAttachments`/`ArrangeAllSeals`/`ArrangeCouncilAttachments`/`ArrangeCouncilSeals` 相应简化），改为宿主卡维护**角标节点**。
- **展开态**：角标 `MouseEntered` → 浮层原地展开，横向平铺完整卡图（复用 `CardHoverPreview` 的 `PanelContainer + Layers.CardHoverPreview` + 视口钳制）。
- **可点选**：展开面板中的卡响应点击并回传 UID，衔接选目标链路。

> ⚠️ **与选卡找卡方案的强冲突与分治口径** —— 见**第八章**，折叠**必须区分 cat-1 / cat-2**。

### 决策 4：资产/分数区层叠紧凑 + hover 展开

- **层叠算法**：新增层叠布局（固定露边宽度 step，如每张露 36px），卡图保留（露出部分可见卡色）。
- **hover 展开**：复用同一浮层机制显示完整卡图。
- **双端**：`ArenaOl.RebuildAssets/RebuildScoreZone/RebuildAssetsInto` 同步。

> ⚠️ **取目标语义修正**：美化原方案写"资产取目标时原地悬浮供点选"，**与选卡找卡方案冲突**（该方案规定**资产无条件 cat-2，走选择窗**）。**以选卡方案为准**——层叠 + hover 仅作**浏览**用途，取目标一律走 `CardPickWindow`。见第八章。

### 决策 5：手牌 hover 上浮

- 复用 `CardUI` 已有 `MouseEntered/Exited`（现用于 `CardPreviewBus`）。
- 悬浮时本体上浮（~40px）+ 放大（~1.3x），`ZIndex` 提升至 `Layers.CardDragging` 之下、`CardBase` 之上。
- 与 `_Process` 平滑归位协调：用独立"悬浮偏移"叠加在布局位置之上，不改 `_targetPosition`。

### 决策 6：联机视角归一化（新增）

- **现状**：`ArenaOl._Ready()` 把 P1 硬编码绑到 `Player1Area`（下方）、P2 绑到 `Player2Area`（上方），`_localPlayerId` **只用于输入隔离、不参与布局** ⇒ P2 玩家看到"自己在上面"。
- **方案（路线 A：装配重定向）**：按 `_localPlayerId` 交换绑定 —— **本地玩家始终绑 `Player1Area`（下）、对手绑 `Player2Area`（上）**。场景不改，改装配逻辑。
- **改造清单**（散落 10+ 处，需收口为 `GetLocalHand()` / `GetOpponentHand()` 等）：
  - `ArenaOl.cs:433/2512`：`_localPlayerId == 1 ? _p1Hand : _p2Hand`
  - `ArenaOl.cs:3452`：`_assetContainer1 : _assetContainer2`
  - `ArenaOl.cs:2552/3481`：`_councilContainer1/2`
  - `ArenaOl.cs:3576`：`slot.Player1Area : slot.Player2Area`
  - `ArenaOl.cs:3226`：`isLocalHand` 判定
- **不做路线 B（整场景旋转/镜像）**：文字会倒置、命中坐标需反向换算、`GetGlobalRect` 全错，风险不可接受。
- **单机 `Arena` 不动**（P1 固定在下，测试模式无影响）。

### 决策 7：对手手牌紧凑（新增）

- **现状已具备条件**：`ArenaOl.cs:3231-3239` 中 `IsHidden` 占位只画卡背、`CanDrag=false`。
- **改造**：把"逐张占位"改为"**一张牌背 + 数字角标**"，hover 展开显示张数（复用决策 4 的层叠 + hover 机制）。
- 联机对手手牌本就是隐藏占位（无模板/无卡图）⇒ 天然只显示牌背，无信息泄露风险。

---

## 五、性能与可靠性

- **布局复杂度**：层叠/平铺均 O(n)，n 通常 < 20，无瓶颈。
- **浮层复用**：扩展 `CardHoverPreview` 承载多卡平铺，**不新建第二个浮层**（避免层级/总线冲突）。
- **减少重排**：折叠态不再执行封印/附属偏移布局，反而**降低**开销。
- **双端一致性**：几何常量与算法集中 `CardLayoutHelper`，`Arena`/`ArenaOl` 只调用。

---

## 六、改动清单

### 6.1 目录与文件

```
d:/MYPROJECT/UWPro/
├── Script/UI/
│   ├── CardLayoutHelper.cs        # [MODIFY] 尺寸常量收口/层叠算法/折叠态判定
│   ├── Arena.Layout.cs            # [MODIFY] ArrangeAreas 重算；附属封印改角标；常量引用
│   ├── CardUI.cs                  # [MODIFY] 尺寸引用；角标节点；hover 上浮
│   ├── CardHoverPreview.cs        # [MODIFY] 扩展多卡平铺 + 点击回传
│   ├── CardPreviewBus.cs          # [MODIFY] 支持"卡列表 + 展开模式"
│   ├── AreaSlot.cs                # [MODIFY] UpdateLayout 按新尺寸重算（仅几何段，见第八章区域划分）
│   ├── AreaSlot.cs（高亮段）      # [不动] SetHighlight/SetSelected 归选卡方案
│   ├── Layers.cs                  # [MODIFY] 新增角标/展开面板层号常量
│   └── ArenaOl.cs                 # [MODIFY] 视角归一化 + 资产/分数区同步 + 对手手牌紧凑
├── Scenes/
│   ├── Arena/Arena.tscn           # [MODIFY] 玩家区高度/资产与分数容器坐标/槽位预留
│   └── UI/AreaSlot.tscn           # [MODIFY] Player1/2Area 高度压缩；CardContainer 对齐
└── Script/Tests/
    └── P5LayoutMathTests.cs       # [MODIFY] 更新几何/层级断言；新增折叠与层叠断言
```

### 6.2 回归风险

- `Script/Tests/P5LayoutMathTests.cs` 覆盖布局几何与层级基准（文档称 147 套件 1167 例）。改常量/算法后**必须同步断言**，跑全量 headless 测试保证 **0 失败**。
- 用 `.workbuddy/tools/build-and-test.sh <日志名>` 跑测。

### 6.3 层级约束

- 新角标/浮层**严格从 `Layers.cs` 取层号**并登记到 `制作指南/UI层级规范.md`。
- 保持相对关系：`卡牌(≤399) < 拖拽(400) < 附属(500) < 封印(550) < 箭头(800) < 预览(830) < 结果(850) < 统计(860) < 命令框(900)`。

### 6.4 红线

- **不写 Model**：浮层/角标/上浮严禁写回 Model（与 `后续优化事项.md` §5 一致）。
- **信息隔离**：对手手牌/牌库只显示卡背 + 角标，不得暴露身份。

### 6.5 待规划期确认的参数

- 卡牌目标尺寸（建议 140×196，可调）
- 手牌 hover 上浮量 / 放大倍率
- 层叠露边宽度（建议 36px）
- 地区卡（AreaCardUI）新尺寸

### 6.6 `AreaSlot` 内部尺寸语义澄清（易错点）

`AreaSlot.cs:111` 的 `cardWidth=140, cardHeight=100` 指的是**地区卡（AreaCardUI）横条尺寸**，与**玩家卡 100×140** 是**两套不同语义**。收口时**不得**混用同一常量，建议命名区分：
- `CardLayoutHelper.CARD_WIDTH/HEIGHT`（玩家卡）
- `AreaSlot` 内的 `AREA_CARD_WIDTH/HEIGHT`（地区卡）

### 6.7 待决：`aspect=ignore → keep`

现状 `ignore` 会让非 16:9 屏拉伸变形，抵销本次所有尺寸优化。建议改 `keep`（保比例 + 黑边）。**待用户拍板**（涉及产品决策）。

---

## 七、批次划分（建议）

| 批次 | 内容 | 依赖 |
|---|---|---|
| **P0** | 卡牌尺寸常量收口（`CardLayoutHelper` 唯一来源） | 无 |
| **P1** | 联机视角归一化（决策 6） | P0 无依赖，但建议先于几何改动 |
| **P2** | 争夺区重排 + `AreaSlot`/`Arena.tscn` 尺寸（决策 1/2） | P0 |
| **P3** | 封印/附属角标 + hover 展开（决策 3，含 cat-1/cat-2 分治） | P2 + 选卡方案完成 |
| **P4** | 资产/分数区层叠 + hover（决策 4）+ 对手手牌紧凑（决策 7） | P0 + P3 浮层机制 |
| **P5** | 手牌 hover 上浮（决策 5） | P3 浮层机制 |
| **P6** | 回归 `P5LayoutMathTests` + 双端验证 | P2–P5 |

---

## 八、与「选卡找卡方案」的接口约定 ★

> **本章为跨方案协调的核心，先执行选卡方案时**必须**参照本章，避免后续返工。**

### 8.1 执行顺序

**先执行 `选卡找卡UI优化方案.md`（3 批次），再执行本方案。** 理由：
1. 选卡方案修的是**阻塞性缺陷**（高亮却点不动、地区高亮不可见、手牌点击被拖拽吞掉），优先级高于体验提升；
2. 选卡方案建立"**候选卡有可见场上 UI 即可点选**"的契约（`PickCandidateCardUidAt`），本方案的**折叠/层叠**正要动这些卡的可见性 → **必须先建立契约再改造**；
3. 选卡方案 3.1 的 **cat-1/cat-2 分类规则**是本方案决策 3/4 的**前置知识**。

### 8.2 cat-1 / cat-2 分类（选卡方案定义，本方案必须遵守）

| 类别 | 成员 | 本方案的处理 |
|---|---|---|
| **cat-1**（提示窗：场上高亮 + 左键即选） | 争夺区角色/暗藏者｜**结附于地区**的附属（`IsAttached && AttachedTo==null`）｜**封印于地区**的封印（`IsSeal && SealedBy==null`）｜自己手牌｜地区本身｜秘社 | **必须保持场上可见可点** |
| **cat-2**（选择窗：卡面网格 + 区域标签 + 确认） | **结附/封印于卡**（`AttachedTo!=null` / `SealedBy!=null`）｜**资产（无条件）**｜牌库｜墓地｜对手手牌｜放逐区｜计分区｜堆叠 | **可折叠/层叠**（走选择窗，不依赖场上点选） |

### 8.3 【强冲突】封印/附属折叠必须分治

- **冲突**：选卡方案要求"候选有可见 UI 即可点选"；本方案折叠会隐藏 UI ⇒ `PickCandidateCardUidAt` 命中恒为假。
- **分治口径（本方案必须实现）**：
  - **结附/封印于地区**（cat-1）→ **保持场上可见可点**（不得折叠为角标，或折叠态在有候选时自动展开）
  - **结附/封印于卡**（cat-2）→ **可折叠为角标**（走选择窗，不需要场上点选）
- **兜底建议**：若某次候选**含 cat-1 的封印/附属**，折叠态应**临时展开**使其可见（与选卡方案 4.5「选择窗 ↔ 场上点击联动」兼容）。

### 8.4 【同源冲突】资产取目标改走选择窗

- **冲突**：本方案原写"资产取目标时原地悬浮供点选"；选卡方案规定"**资产无条件 cat-2，走 `CardPickWindow`**"。
- **修正（以选卡方案为准）**：
  - 资产区**层叠 + hover** 仅作**浏览**（看卡图）
  - **取目标一律走 `CardPickWindow`**，不做"原地悬浮供点选"
- 分数区同理（未来若成为目标，也走选择窗）。

### 8.5 【描边语义分离】

- 用户明确"**描边以后要留着做别的**"（故封印/附属只用角标）。
- 选卡方案大量使用描边：`CardUI.SetSelectableHighlight` 黄框 `Color(1, 0.85, 0.1)`、`CouncilCardUI.SetSelectableHighlight`、`AreaCardUI.SetSelectableHighlight`、`AreaSlot` 两侧淡黄。
- **约定**：
  - **选目标黄框**（`SetSelectableHighlight`）＝ 选卡方案专用，本方案**不得复用/改动**该接口
  - 本方案若需新的"附属/标记提示描边"，**另开接口**（不同颜色/不同方法名），与选目标黄框隔离

### 8.6 【同文件区域划分】`AreaSlot.cs`

| 代码区域 | 归属方案 | 内容 |
|---|---|---|
| `SetHighlight` / `SetSelected`（高亮段） | **选卡方案** | 修 `SelfModulate` 不传播 bug + 转发 `AreaCardUI.SetSelectableHighlight` |
| `UpdateLayout` / `_Ready` 尺寸（几何段） | **本方案** | `sideH`/卡位高度按新尺寸重算 |
| `AreaSlot.tscn` 尺寸 | 本方案（几何） | `Player1Area/Player2Area` 高度压缩 |

**约定**：同一文件不同方法段，改动互不覆盖；合并时按方法粒度 review。

### 8.7 【共享组件】`CardHoverPreview` / `CardPreviewBus`

- 本方案扩展 `CardHoverPreview` 支持"多卡平铺展开"（封印/附属、资产/分数区）。
- 选卡方案的 `CardPickWindow` **自带** `CardHoverPreview` 子节点（`CardPreviewBus.WindowOverlayName`）。
- **约定**：扩展时保持**单卡预览路径不变**；新增"列表展开模式"作为**并列分支**，不破坏 `CardPickWindow` 既有用法。

### 8.8 【无冲突但需注意】手牌点击

- 选卡方案 4.2 第 ③ 条：`TryBeginDragAt` 加守卫"选目标期间不起手拖拽"（`Arena.cs:3076` 路由顺序）。
- 本方案决策 5：手牌 hover 上浮（`CardUI` 视觉偏移）。
- **两者改同一区域（手牌）+ 同一文件（`CardUI`/`Arena`），但不冲突**（一改输入路由、一改视觉偏移）。建议选卡方案先落地输入守卫，本方案再加视觉偏移。

---

## 九、验收要点

- **单机**：3 地区各 ≥3 张放大卡不重叠；封印/附属折叠角标正确；资产/分数区层叠 + hover 展开；手牌 hover 上浮。
- **联机**：无论 P1/P2 视角，**自己在下、对手在上**；对手手牌为"牌背 + 数字"；资产/分数区与单机一致。
- **选卡兼容**：`结附/封印于地区`（cat-1）**保持场上可点选**（选卡方案验证项不回归）；资产取目标走选择窗。
- **回归**：`P5LayoutMathTests` 及全量 headless 测试 **0 失败**。

---

## 十、附：与其它文档的关系

- `选卡找卡UI优化方案.md`：**前置依赖**，第八章为其接口约定。
- `制作指南/UI层级规范.md`：层级唯一真相源说明。
- `后续优化事项.md` §5：表现层动画红线（不写 Model、信息隔离）。
- `事件总线使用说明.md`：UI 刷新收口原则。
