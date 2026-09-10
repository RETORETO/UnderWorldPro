# 联机激活效果选择交互修复计划（第 3 版 · 已确认，待批准实施）

> 状态：交互规格已全部确认，代码未改动。待批准后实施。
> 范围：只修改联机侧（Script/UI/ArenaOl.cs + 新增联机专用 UI）；不改单机逻辑源码。
> 目标：双击【附属卡】/【秘社卡】先弹出效果列表（不可用项置灰、允许取消），玩家选择后再发动；需目标的效果进入联机"场上点击高亮"选目标（复用单机 TargetSelectionWindow）。

---

## 一、已确认的交互规格（含对六个疑问的裁决）

1. **效果列表允许取消**：显式"取消"按钮；点窗口外不关闭。
2. **不可用选项置灰**：保留原因文字（"已横置，无法横置自身"/"无合法目标"）。
3. **目标选择**：复用单机 TargetSelectionWindow（res://Scenes/UI/TargetSelectionWindow.tscn），采用场上点击高亮；无取消（必须选一个合法目标）。需求不明的规则若单机有完整实现可直接复用。
4. **地区目标 TargetKind.Area**：**暂缓**。当前没有激活效果需要"可选择地区"，本轮不做；候选计算仅覆盖卡目标。
5. **快照重入**：**不处理**。只有我方拥有行动权时才能选择激活，此时对手无法发起会改变局面的意图，故不会在本地选择期间收到导致重建的快照。
6. **秘社选项含多个 EffectData**：仍取该选项第一个效果，与单机一致。
7. **效果选择窗口新增联机专用类**：同意，不改单机 ActivationChoiceWindow。

---

## 二、现状与缺口

### 2.1 附属双击（JC012）

- 现状：ArenaOl.OnAttachmentDoubleClicked（约 L719-744）直接取第一个可激活效果上行，不弹列表。
- 单机对照：Arena.OpenAttachmentActivateChoice（约 L1347-1427）收集全部效果；>1 弹 ActivationChoiceWindow；选中需目标效果走 BeginAttachmentActivateWithTarget（约 L1437-1462）弹 TargetSelectionWindow 选目标。

### 2.2 秘社双击（MSJC001）

- 现状：ArenaOl.OnCouncilDoubleClicked（约 L746-769）直接取第一个选项的第一个效果上行。
- 单机对照：Arena.OnCouncilDoubleClicked（约 L1296-1340）弹 CouncilActivateWindow 列出 ActivationOptions，选中后发动 opt.EffectDataList[0]。

### 2.3 驱动层缺口

| 编号 | 缺口 |
|---|---|
| G1 | 联机没有"效果选择窗口"（单机窗口会点窗外关闭，不符合联机规则；且由单机 Arena 实例化）。 |
| G2 | 联机没有"激活目标选择"步骤；现有上行恒传 targetUid=""，选到需目标效果会被服务端 EffectEngine.ActivateAbility 以"缺少目标"拒绝（约 L1576-1580）。 |
| G3 | 联机没有本地可用性过滤/置灰；盲发非法选项时服务端拒绝但客户端无可见反馈。 |
| G4 | 联机没有"场上点击高亮选目标"的点击路由（卡高亮 + _Input 命中）。 |

---

## 三、修复方案（仅联机侧）

### 3.1 新增联机效果选择窗口（G1/G3）

- 新增联机专用 OnlineActivationChoiceWindow（新文件 Script/UI/OnlineActivationChoiceWindow.cs，代码构建 Window）：
  - 输入复用共享数据类 ActivationChoiceItem（Label / Disabled / OnChosen），不改该类。
  - 按钮列表；Disabled=true 时 Button.Disabled=true 置灰不可点；Label 已含原因后缀。
  - 底部显式"取消"按钮。
  - **不连接 FocusExited 关闭**（点窗外不关闭）。
- 显示文字：
  - 附属效果：优先 EffectData.FaqNote，回退 EffectId（对齐单机 GetActivationAbilityLabel）。
  - 秘社选项：优先 CouncilActivationOption.OptionName，回退首效果 EffectId。
- 不改 ActivationChoiceWindow / CouncilActivateWindow。

### 3.2 重写 OnAttachmentDoubleClicked

- 收集 inst.PlayerTemplate.EffectDataList 中 Timing ∈ {AttachmentActivated, CharacterActivated} 的效果。
- 0 个 → 忽略。
- 1 个 → 直接 SendActivation(inst, ability)（保持"单效果直接发动"）。
- ≥2 个 → 构造 ActivationChoiceItem：
  - 置灰判定（与单机 OpenAttachmentActivateChoice 对齐）：
    - ActivateCondition 含"横置本角色"且 inst.IsTapped → 置灰（标注"已横置，无法横置自身"）；
    - Target.NeedsSelection 且无合法卡目标 → 置灰（标注"无合法目标"）。
  - 弹 OnlineActivationChoiceWindow；选中 → SendActivation；取消 → 不发动。

### 3.3 重写 OnCouncilDoubleClicked

- ActivationOptions.Count == 0 → 忽略。
- 只有 1 个选项 → 取 opt.EffectDataList[0]，直接 SendActivation。
- ≥2 个选项 → 弹列表（每项映射到该选项首效果，标签用 OptionName；置灰判定同上）；选中 → SendActivation；取消 → 不发动。

### 3.4 统一上行入口 SendActivation(CardInstance inst, EffectData ability)

- 无需选目标：SendIntentAs(_localPlayerId, ActivateAbility, inst.UID, ability.EffectId, "")。
- 需选目标：进入 3.5；选中后 SendIntentAs(_localPlayerId, ActivateAbility, inst.UID, ability.EffectId, pickedUid)。

### 3.5 联机"场上点击高亮"选目标（G2/G4）

新增 ArenaOl 内部目标选择状态：

  bool _activationTargetActive;
  EffectData _activationTargetAbility;
  CardInstance _activationTargetSource;
  HashSet<string> _legalCardUids;

流程：

1. **候选计算（卡目标，仅此范围）**
   - 遍历 ClientZoneManager.GetZoneCards(CardZone.AreaZone, 0)；
   - 排除自身；
   - ability.Target.IsLocalZone 时要求 candidate.ZoneDetailId == inst.ZoneDetailId；
   - ActivateEligibility.IsLegalActivateTarget(inst, ability, candidate, null) 过滤（该函数第 4 参 cm 仅签名占位、实现未解引用，传 null 安全；内部用 CardManager.MapInstanceToTargetKind / MatchesRule 静态工具）。
   - 无合法目标 → 本效果在 3.2/3.3 已置灰，正常情况下不会进入此步。
2. **高亮**：对 UID ∈ _legalCardUids 的 CardUI 调 SetTargetHighlight(true)。
3. **提示窗**：只读复用 TargetSelectionWindow.tscn，Open(effectName, hint) + SetBlocking(false) 保证点击穿透到场上；无取消（与单机一致）。
4. **点击路由**：扩展 ArenaOl._Input：当 _activationTargetActive 且左键按下，hit-test 命中 _cardUIMap 的卡 UID ∈ _legalCardUids → 选中（AcceptEvent 阻止进一步传播）。
5. **选中后**：清空卡高亮 → 关闭提示窗 → 复位状态 → 上行 ActivateAbility(sourceUid, effectId, pickedUid)。
6. **本地模态锁**：效果选择或目标选择进行期间，拦截 DoPass / DoEndTurn / 手牌拖拽 / 重复双击。
7. **快照重入**：不处理（见 §一.5，行动权在我方时对手无法引发局面变更）。

### 3.6 目标选择取消

- 复用 TargetSelectionWindow：无取消、必须选一个合法目标（与单机一致），无需额外自建提示窗。

---

## 四、明确不改的部分（保证单机行为不变）

- Script/UI/Arena.cs（OpenAttachmentActivateChoice / OnCouncilDoubleClicked / BeginAttachmentActivateWithTarget 等）。
- ActivationChoiceWindow.cs、CouncilActivateWindow.cs、TargetSelectionWindow.cs（仅只读实例化 TargetSelectionWindow.tscn，不改源码）。
- Interaction/TriggerInteractionCoordinator.cs、Interaction/CardDragController.cs。
- EffectEngine.cs、Script/Net/ 协议、IntentHub。

---

## 五、验证计划

1. dotnet build UWPro.csproj：0 警告 0 错误。
2. 跑现有逻辑测试，确认无新增失败（重点 JC012 / JC015 / JC020 / JC060 / NetP 系列既有结果比对）。
3. 联机双进程手动冒烟（启动联机三进程测试.bat）：
   - 双击 JC012 附属 → 弹两个效果；文案正确；不可用项置灰并含原因；
   - 选① → 上行 JC012_1_CostReduce；
   - 再双击选② → 合法角色高亮 + 提示窗 → 点击高亮角色 → 上行 JC012_2_Tap + targetUid；
   - 点非高亮区域不选中；
   - 效果列表"取消" → 无意图上行；点窗口外不关闭；
   - 双击 MSJC001 秘社 → 两个选项；分别选 1/2 → 上行不同 effectId。

---

## 六、风险与注意

- 本地置灰/合法目标计算只用于展示与选目标，服务端仍做优先权/费用/条件/目标存在性权威校验。
- 服务端 EffectEngine.ActivateAbility 对传入 target 只判空、未做合法性复验；本轮不扩大服务端改动，此软点留待后续单独收口。
- 联机专用窗口与本机已有的 _choicePromptPanel（服务端决策单例）实例隔离。
- 本轮不做地区目标 TargetKind.Area（已暂缓，未来有需要时按单机规则复用实现）。
