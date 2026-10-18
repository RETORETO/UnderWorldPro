# UI 美化施工顺序（2026-10-18 核查版）

> **本文只做施工排期与冲突界定，规格口径仍以 `UI美化与空间重排方案.md`（2026-10-16 定稿）为准。**
> 前提：逻辑改动原则上由另一窗口负责；**P1 所需的一小部分输入路由/落点判定已获用户授权由本窗口一并修改**
> （其余 `Script/Manager/**` 仍不动）。

---

## 〇、现状核查结论

### 实施结果（2026-10-18 收工）

| 批次 | 状态 | 落地要点 |
|---|---|---|
| 选卡找卡 UI 批次 1/2/3 | ✅ 已完成 | `8bb48b6` / `caa8c23` / `2b5d6ab` |
| 选卡方案 **C4** | ⛔ 按用户要求暂缓 | 需改 `IGameUIHooks` + `TurnManager` |
| **P0** 尺寸收口 | ✅ 已完成 | `CardLayoutHelper.CARD_WIDTH/HEIGHT = 140/196` 为唯一真源（`Arena.Layout.cs`、`CardUI.cs` 改为别名；`.tscn` 手改）。**秘社卡例外**：`CardLayoutHelper.SizeOf()` 对 `CouncilCardUI` 仍返回 100×140；牌堆/墓地缩略图（`DeckZone`/`GraveyardZone`/`OnlineDeckZone`）保持 100×140 且 `CustomMinimumSize` 同设（只设 `Size` 会被钳回） |
| **P1** 联机视角归一 | ✅ 已完成（2026-10-19 补漏） | `ArenaOl.cs` 收口为 `ViewMirrored/ViewCtr/HandCtr/AssetCtr/ScoreCtr/CouncilCtr/DeckZoneOf/GraveZoneOf/PlayerAreaNode/SeatName`；**身份判断（`SendIntentAs`/`ControllerId`/`IsLocalPlayer`）不镜像**。补漏：① 对手手牌 hover 展开原先直取面板（`_localPlayerId==1 ? _p2Hand : _p1Hand`），镜像后取到的是**本方**手牌 ⇒ 玩家 2 视角对手牌背永不展开，改走 `HandCtr(对手座位)`；② `AreaSlot` 新增 `MirrorSeats`，地区「调查/战斗/势力」统计与势力标记文案随镜像换半边（否则自己的数字显示在对手那半边）；③ 新增 `OnlineViewMirrorTests` 源码契约防回归 |
| **P2** 争夺区重排 | ✅ 已完成 | 槽 480×540（`AreaSlot.tscn` + `AreaSlot.cs` 兜底值 + `ArrangeAreas`）；`Player1Area` 250→290、`CenterArea` 580→540、`HandContainer` 250→290、`ScoreZone` 随之下移，`StackPanel` 同步 |
| **P3a** 角标折叠 | ✅ 已完成 | 新增 `CardBadges.Apply()`；4 个实例级偏移函数改为"成员 `Visible=false` + 宿主角标"；**cat-1（结附/封印于地区）不折叠**，仍走 `ArrangeAreaAttachmentsAndSeals` 平铺 |
| **P3b** 角标 hover 展开 | ✅ 已完成 | 新增 `CardBadgePeek` + `Arena.BadgePeek.cs`；**封印/附属分家**（各自独立悬浮区，一次只列一种，面板有标题）；**双击成员＝发动激活能力**（复用既有双击链路，补回 JC020 这类被折叠附属的唯一入口）；单击成员只调 `CardPickWindow.SelectByUid`（置选中、不提交）；无选择窗时纯浏览 |
| **P4** 资产/分数层叠 | ✅ 已完成 | 新增 `CardLayoutHelper.LayoutStacked()`（露边 36px，放不下自动压缩）；单机 + 联机双侧接入 |
| **P4** 对手手牌紧凑 | ✅ 已完成（**仅联机端**） | 用户 2026-10-18 拍板「只改联机端」（单机同屏热座必须保持正面逐张可见）。`ArenaOl.ArrangeChildren` 对"整排都是隐藏牌背"的对手手牌改调 `CardLayoutHelper.LayoutCompactBacks()`：全部牌背叠在一处、只露一张 + `CardCountBadge` 的「×N」；鼠标停在手牌带内则改用 `LayoutStacked`（每张露 36px）便于数张数，收起/展开由 `_Process` 每帧矩形轮询（不依赖 `mouse_entered/exited` 的父子冒泡语义） |
| **P5** 手牌 hover 上浮 | ✅ 已完成 | `CardUI` 叠加 `HoverOffset()`（+28px / 1.18x），移动节点而非 shader/pivot ⇒ `GetVisualGlobalRect()` 自动一致；手牌上下文**抑制整卡预览** |
| **P6** 回归 | ✅ 已完成 | `dotnet build` 0 警告 0 错误；全量 headless **162 套件 0 失败**（`logs/ui_pile_pick_tests.log` 末行 `>>> 全部逻辑正确性测试通过`）；文档已更新（`制作指南/UI层级规范.md` 角标实施订正） |

### 收尾批次（2026-10-19）· 用户口径落实

> 用户口径：① 地区卡可以加宽；② 资产**不**纳入场上点选；③ 联机卡选窗可做；④ 资产区与墓地区也支持折叠 + hover 展示卡。

| 项 | 状态 | 落地要点 |
|---|---|---|
| 角标面板「粘住」修复 | ✅ | 原来只在"离开角标"时排收起，鼠标从面板移到空白处没有任何触发 ⇒ 面板永久挂着。两视图改为 `TickBadgePeek` **每帧矩形轮询**（与对手手牌同一课） |
| 展开面板张数上限 | ✅ | 原写死 6 张（第 7 张起看不到、也无法双击发动）⇒ 改为**按视口宽度算**（`MaxCards=14` 封顶），超出显示「…（共 N 张）」 |
| 地区卡加宽 | ✅ | `AreaCardUI.CARD_WIDTH/HEIGHT = 196×140`（保持 1.4:1 卡图比例）；`AreaSlot` 改用该常量（6.6 语义分离），槽内上下各 200 仍容纳 196 高角色卡；世界牌库缩略仍 140×100（需先压 `CustomMinimumSize`） |
| 分数区几何 | ✅ | 卡面 196 高而容器仍 100 ⇒ 本方分数卡下半 96px 掉出屏幕。两场景：`CardContainer 196×196`、`Player1Area/ScoreZone` 上移到 94..290、`Player2Area/ScoreZone` 加高到 196；`ScoreLabel` 右移到 196（避开加宽后的卡） |
| 资产区折叠 | ✅ | `CardLayoutHelper.LayoutPile`（叠成一摞）+ `CardCountBadge`「×N」；悬停整块资产区弹卡列表。分数区**保持**每张露 36px（决策 4 原文） |
| 墓地区悬停展卡 | ✅ | 视觉不动（仍是 5px 递增缩略堆），悬停弹卡列表；命中判定覆盖铺开的子卡矩形（会超出面板） |
| 联机选卡窗 | ✅ | `SelectZoneCard` / `SelectHandCard` / `SelectDeckCard` → `OpenOnlineCardPickWindow`（真卡面 + 所在区域标签 + 确认/取消），候选由 `OptionCardRefs` 经 `RebuildFromRef` 重建；隐藏占位自动只画卡背；回包仍是 `ChoiceResult.SelectedUid`，**协议零改动**；缺卡面引用时退回通用按钮面板 |
| 注释/命名清理 | ✅ | `CardUI.cs`、`CardHoverPreview.cs` 的"计划·未实施"改为实施结论；`AreaSlot` 尺寸改用 `AreaCardUI` 常量 |

> 明确不做（本批口径）：资产卡**不**纳入"场上点选"候选 ⇒ 决策 4 的"层叠态候选/选中呈现"暂不需要
> （将来若纳入，再按方案二选一补）。

### 开工前的核查快照（保留作对照）

| 方案 | 当时状态 | 证据 |
|---|---|---|
| `选卡找卡UI优化方案.md` 批次 1/2/3 | ✅ 已完成 | `8bb48b6` / `caa8c23` / `2b5d6ab`；`CardPickWindow.cs`、`CardPickChannel.cs`、`CardZoneLabel.cs` 均在 |
| 选卡方案 **C4**（免费打出取目标可取消） | ⛔ 未做 | 需改 `IGameUIHooks` + `TurnManager`，属逻辑改动 ⇒ 不在本窗口 |
| `UI美化与空间重排方案.md` **P0** | ❌ 未实施 | `CardLayoutHelper.cs:15-16` = 100/140；`Arena.Layout.cs:22-23` = 100/140；`CardUI.cs:522-523` = 100/140；`Scenes/UI/CardUI.tscn:7/10/11` = 100/140 |
| **P1** 联机视角归一 | ❌ 未实施 | `ArenaOl.cs:458`、`:3071` 仍是 `_localPlayerId == 1 ? _p1Hand : _p2Hand`；无 `GetLocalHand()` |
| **P2** 争夺区重排 | ❌ 未实施 | `AreaSlot.tscn` 仍 400×580（`Player2Area` 0→240、`Player1Area` 340→580）；`ArrangeAreas` 仍 `slotWidth=400/slotHeight=580/spacing=30` |
| **P3** 封印/附属角标 | ❌ 未实施 | `Arena.Layout.cs:107/138/187/229/281` 只有"计划·未实施"注释，无角标节点 |
| **P4** 资产/分数区层叠 | ❌ 未实施 | `Arena.Layout.cs:372` 只有注释；资产区仍是平铺 |
| **P5** 手牌 hover 上浮 | ❌ 未实施 | `CardUI.cs:108-110` 只有注释，`MouseEntered` 仍只走 `CardPreviewBus.RequestHover` |
| **P6** 回归 | — | 待 P0–P5 |

---

## 一、开工前置（当前阻塞）

**另一个进程有 3 个 UI 文件处于未提交状态**（最后写入 15:53–15:54）：

| 文件 | 未提交改动 | 与美化方案的关系 |
|---|---|---|
| `Script/UI/Arena.Layout.cs` | +120/−?，改 `SyncCardUIToZone` 一带（≈497–678、880） | ⚠️ **P2/P3/P4 的主战场**（`ArrangeAreas`、4 个实例级偏移函数全在此文件） |
| `Script/UI/Arena.cs` | +51，改 156/333/701/737/743 一带 | 与 P3/P4 的调用侧重叠 |
| `Script/UI/StackEntryUI.cs` | +63/−?，堆叠条目卡面 | 与 P3 折叠无直接冲突，但同属 UI 目录 |

**结论**：P0 需要**同时改 4 处尺寸常量**（其中 `Arena.Layout.cs:22-23` 在其未提交文件里），现在动手会把两个改动搅在同一个文件里。**建议先让那个窗口把这 3 个文件提交**，再开工 P0。

**可以先做的零冲突准备**（不写文件）：量算参数、确认下文第四节待拍板项。

---

## 二、会牵动逻辑的点（本窗口不做，需报备后由逻辑窗口处理）

| # | 事项 | 为什么会牵动逻辑 | 归属 |
|---|---|---|---|
| L1 | **尺寸常量 4 处不同步** | 拖拽落点、`PickCandidateCardUidAt`、`AreaSlot` 命中判定全部依赖几何矩形；改一半会出现"拖到卡上打不出/选不中"，表现为逻辑 BUG | 本窗口自洽，但必须一次性改完 4 处 |
| L2 | **P1 联机视角归一化** | `_localPlayerId` 参与**输入路由与落点判定**（`ArenaOl.cs:458/3071` 手牌、`:4061` 资产、`:4200` 命中、`:4235` 落点区域）。改错会把牌打到错误区域，服务端会拒绝或落到对手区 | 本窗口做，但**必须收口 helper**，且 `:1890/:2059` 按 `ControllerId` 取值处**不得改** |
| L3 | **P3 把 cat-1 也折叠** | 结附/封印于地区的卡若被折叠，`PickCandidateCardUidAt` 命中恒假 ⇒ 选卡"高亮却点不到"，直接违反选卡方案契约 | 红线，禁止 |
| L4 | **P3 展开面板"可点选"的出口** | 若点击直接调 `onPicked`，等于绕过 `CardPickWindow` 的「确认」⇒ 交互语义变了（点一下即生效） | 必须只走 `CardPickWindow.SelectByUid`（置选中、不提交） |
| L5 | **C4 免费打出取消** | `PromptPlayTarget` 两个调用方 null 语义相反（牺牲 vs 免费打出） | 逻辑窗口，本窗口不做 |
| L6 | **手牌上浮与 `GetVisualGlobalRect`** | 选卡方案的手牌命中依赖 `GetVisualGlobalRect()`（`CardUI.cs:564` 用 `GetGlobalTransform()`）⇒ 只要上浮通过移动节点实现即自动正确；**不得**用 shader/pivot 偏移伪装位置 | 本窗口注意即可 |

> 除 L2、L6 外，其余都属"不要做/必须遵守"，不产生跨窗口依赖。

---

## 三、完整施工顺序（P0 → P6）

### P0 ｜尺寸常量收口（先决条件，1 个提交）

- **目标**：`CARD_WIDTH/HEIGHT` 单一真相源 = `CardLayoutHelper`，值改 **140 / 196**。
- **改动**（4 处，原子提交，不可拆）：
  1. `Script/UI/CardLayoutHelper.cs:15-16` ← 唯一改值处
  2. `Script/UI/Arena.Layout.cs:22-23` → `= CardLayoutHelper.CARD_WIDTH/HEIGHT`
  3. `Script/UI/CardUI.cs:522-523` → 引用常量
  4. `Scenes/UI/CardUI.tscn:7/10/11` → 140 / 196（`.tscn` 是字面量，必须手改）
- **同时**：`Script/Tests/P5LayoutMathTests.cs` 的 100/140 字面量改为引用常量（约 `:19/30-32/40-41/73/94`）。
- **验收**：`P5LayoutMathTests` 全绿（含 A71"横置视觉中心 = Position + PivotOffset"）；全量 headless 0 失败；进游戏看手牌卡面是否放大且不裁切。
- **风险**：G2 里"卡图 ≤512px"是按 140 宽估的，放大后仍够用；但手牌 960px 宽从 9 张并排降到 6 张 ⇒ 多手牌会重叠（`LayoutCardsEvenly` 本来支持重叠，可接受）。

### P1 ｜联机视角归一化（不动 `Arena`）

- **目标**：无论 P1/P2 视角，**自己永远在下方**（`Player1Area`）、对手在上方。
- **改动**：只改 `Script/UI/ArenaOl.cs`。把散落的 `_localPlayerId == 1 ? _p1Hand : _p2Hand` 等收口成：
  `GetLocalHand()` / `GetOpponentHand()` / `GetLocalAsset()` / `GetLocalCouncil()` / `GetLocalArea(slot)`。
  已定位的散点：`:458`、`:3071`、`:4061`、`:3125`、`:4115`、`:4235`、`:4200`、`:3835`（另有 `:1948` 按玩家号 `p` 取值，语义不同）。
- **验收**：P2 席位进对局 → 自己手牌/资产/秘社/落点区都在下方；出牌落点正确；对手手牌仍是"牌背 + 数字"。
- **风险**：L2。**先写 helper 再逐点替换**，每替换一处跑一次本地对局；`ControllerId` 语义处一律不动。

### P2 ｜争夺区纵向重排 + 场景几何（双端）

- **目标**：3 地区 × ≥3 张 140 宽卡不重叠；纵向容纳 2 争夺区 + 地区 + 手牌。
- **当前预算（实测）**：1080 = `Player1Area` 250 + `CenterArea` 580 + `Player2Area` 250；
  `CenterArea` 580 恰好等于 `AreaSlot` 580；`AreaSlot` 400 宽，3 槽 = 3×400+2×30 = 1260（居中留 330）。
- **必须改**：3 张 140 卡需 3×140+2×8 ≈ 436 > 400 ⇒ **槽宽提到 ≥460（建议 480）**；
  3×480+2×30 = 1500 ⇒ 居中 `startX=210`，仍避开左下的 `WorldDeckZone`（x 50–190）。
- **文件**：`Scenes/UI/AreaSlot.tscn`（400×580 → 480×H，内部 `Player1Area/Player2Area` 200 高）、
  `Script/UI/Arena.Layout.cs`（`ArrangeAreas` 三个常量 + `AreaSlot.cs:111-112` 地区卡尺寸）、
  `Scenes/Arena/Arena.tscn` 与 `Scenes/Net/ArenaOl.tscn`（玩家区高度）。
- **建议纵向**：`AreaSlot` 580 → 540（200 + 140 + 200），腾出的 40 给 `Player1Area`（250 → 290），
  手牌区才有空间做 P5 的上浮放大。**该数值需实机量算后定。**
- **验收**：3 地区各放 3 张放大卡不重叠、不出界；`P5LayoutMathTests` 全绿；单机 + 联机双端一致。

### P3a ｜封印/附属折叠为角标（先只做显示）

- **目标**：`结附于卡 / 封印于卡 / 结附于秘社`（cat-2）改为宿主卡角标"附×2""封×1"，层号取 `Layers` 的 **380–399**。
- **红线**：`结附/封印于地区`（`AttachedTo == null` / `SealedBy == null`）**一律不折叠**，继续走
  `ArrangeAreaAttachmentsAndSeals`（`Arena.Layout.cs:369` 一带）在地区容器内平铺。
- **改动**：`Arena.Layout.cs` 的 4 个实例级偏移函数（`ArrangeAllAttachments` / `ArrangeAllSeals` /
  `ArrangeCouncilAttachments` / `ArrangeCouncilSeals`）简化为"不参与布局 + 更新角标"；
  `CardUI.cs` 新增角标节点与 `SetBadges(attached, sealed)`；`Layers.cs` 增 `CardBadge = 380`。
- **验收**：角标数字随结附/封印数量变化；cat-1 的卡仍在地区内平铺可点；A71 用例在"未折叠态"仍成立。
- **收益**：这一步就已兑现 P2 的空间节省（上下 240 里的偏移预留不再需要）。

### P3b ｜角标 hover 展开 + 可点选（依赖 P3a）

- **目标**：hover 角标 → 原地展开横向卡列（复用 `CardHoverPreview` 的 `PanelContainer` + `Layers.CardHoverPreview=830` + 视口钳制）。
- **改动**：`CardHoverPreview.cs` 新增"多卡列表模式"（单卡路径**保持不变**，`CardPickWindow` 自带的实例不受影响）；
  `CardPreviewBus.cs` 增加触发源与互斥优先级（角标/列表 > 整卡预览）。
- **出口**：点击成员 → `CardPickWindow.SelectByUid(uid)`（只置选中，**不提交**）；无选择窗时为纯浏览。
- **验收**：选择窗打开时点展开面板的卡 = 窗内该卡变绿框，仍需点「确认」；同一时刻只出现一个浮层。

### P4 ｜资产/分数区层叠 + 对手手牌紧凑（依赖 P3b 浮层）

- **目标**：资产区（现 `400×140`，`AssetContainer` 1500→1900）改层叠，每张露 36px；hover 展开可看完整卡图并可按 `SelectByUid` 选中；
  分数区（现 140×100）同法；对手手牌退化为"一张牌背 + 数字角标"。
- **改动**：`CardLayoutHelper.cs` 新增层叠算法（O(n)）；`Arena.Layout.cs` 资产/分数排布改调用；
  `ArenaOl.cs` 的 `RebuildAssets` / `RebuildScoreZone` 同步。
- **层叠态候选/选中呈现**：按方案决策 4 末条二选一（位移+抬层 或 角标指示），**不得复用 `SetSelectableHighlight` 黄框**。
- **风险（低）**：已核实单机与联机**都没有"点击某张资产"的交互**（付费是"支付X（横置1张资产）"按钮式，`Arena.cs:797`；
  联机只在刷新时 `SetTapped`），所以层叠不破坏付费链路。仍建议保留"露边条可点 = 展开"以防后续加交互。
- **实施结果（2026-10-18）·对手手牌紧凑只做联机端**：
  - `CardLayoutHelper.LayoutCompactBacks()`：`step = 0`，把牌背全叠在容器中央（`LayoutStacked` 的 `step = 0` 特例，单独成函数以免误用）；
  - `CardCountBadge`（新文件，与 `CardBadges` 刻意分离）：代表牌背右上角「×N」，`MouseFilter=Ignore` 只显示不抢 hover；
  - `ArenaOl.ArrangeChildren` 分支条件写死为"**整排都是 `Instance == null` 的隐藏牌背**"——
    若服务端把某张对手手牌明置（`IsHidden=false` 且有模板），说明那是玩家有权看见的信息，必须回退常规平铺；
  - 展开/收起用 `ArenaOl._Process` 每帧对 `HandContainer` 做矩形命中判定，**不用 `mouse_entered/exited`**：
    Godot 各版本对"进入子卡时父容器是否收到 exit"语义不一致，用事件会在玩家挪到牌背上时把整排塌回去；
  - 牌背占位置 `CardUI.SuppressHoverLift = true`，避免 P5 上浮动画与展开动画抢同一位置抖动（本方手牌不受影响）。

### P5 ｜手牌 hover 上浮（依赖 P2 手牌区高度）

- **目标**：悬停手牌 → 本体上浮 + 放大（建议 1.15–1.2x，先按 `Player1Area` 高度反算），并**抑制整卡预览浮层**。
- **改动**：`CardUI.cs` 增加"hover 偏移"叠加层（不改 `_targetPosition`，在 `_Process` 里叠加到目标点）；
  `CardPreviewBus` 对"手牌来源"直接不请求。
- **验收**：上浮不越界、不遮挡按钮；`GetVisualGlobalRect()` 与视觉位置一致（因为它走 `GetGlobalTransform()`，移动节点即正确）。

### P6 ｜回归与双端验证

- `dotnet build` 0 警告 0 错误；全量 headless `TestsRunner` 0 失败（重点：`P5LayoutMathTests`、`JCEffectSelfAuditTests`）。
- 单机手测清单 + 联机 P1/P2 双视角各一遍。
- 更新 `制作指南/UI层级规范.md`（角标 380–399、500–599 腾空）。

---

## 四、需要拍板的参数

| 参数 | 建议值 | 影响 |
|---|---|---|
| 卡牌尺寸 | 140×196 | P0 全局 |
| 争夺区槽宽/槽高 | 480 × 540 | P2；决定手牌区能否加高 |
| 手牌区高度 | 250 → 290（从 CenterArea 借 40） | P5 上浮空间 |
| 手牌上浮/放大 | +28px / 1.18x | P5，需实机微调 |
| 层叠露边宽度 | 36px | P4 |
| 角标尺寸/字号 | 28×18 / 12px | P3a |
| `project.godot` 的 `stretch/aspect` | `ignore` → `keep` | 非 16:9 屏不拉伸；改后 Web 卡图预算需重算（方案 6.7 待决） |

---

## 五、纪律

- 每批一个提交，`git add` 用**显式文件路径**，不用 `-a`。
- 每批必过：`dotnet build` + 全量 headless `TestsRunner`，日志落 `logs/`。
- 不写 Model、不改联机协议、不改 `IGameUIHooks` 签名。
- 碰到需要逻辑配合的（L2 以外的 L1/L3/L4/L5），**停下来报备**，不自己动 `Script/Manager/**`。
