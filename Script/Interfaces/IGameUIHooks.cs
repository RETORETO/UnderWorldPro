using System.Collections.Generic;
using Script.Game.Entities;

namespace Script.Interfaces
{
    /// <summary>
    /// 【MVC 收口】Controller 主动发起 UI 交互决策的依赖接口（依赖倒置）。
    /// 由 View（Arena）实现，在启动装配时注入 Composition Root / EffectEngine。
    /// Controller 依赖此接口，而非 View 往 Controller 塞回调——依赖方向 Controller → 接口 ← View。
    /// </summary>
    public interface IGameUIHooks
    {
        /// <summary>
        /// JC068 阿格斯IV「准备维护」：请求 UI 弹出"支付 N / 横置"二选一窗。
        /// UI 收集用户选择后，必须回传 pay=true/false 给 Controller（EffectEngine.Instance.ResolvePrepareUpkeep），
        /// 不得自行执行支付/横置动作（View 不写 Model）。
        /// </summary>
        void PromptPayOrTap(CardInstance source, int payAmount);

        /// <summary>
        /// 【2026-09 整体复查·修复·JC128】通用**二选一确认窗**（两个按钮、**不出现输入框**）：用于
        /// "是/否"型决策——如 JC128 替罪羊"是否移除 N 个势力标志（取消则牺牲一个角色）"。
        /// 用户口径（JC061/JC064 同类）："**不需要输入框**"。`onChosen(true)`＝选了 `yesLabel`；`false`＝`noLabel`。
        /// 默认实现：退回数值输入窗（值域 0..1、标签 `yesLabel`/`noLabel`）⇒ 旧 View / 测试桩不受影响。
        /// </summary>
        void PromptTwoChoice(int chooserPlayerId, string prompt, string yesLabel, string noLabel,
            System.Action<bool> onChosen)
            => PromptNumberInput(chooserPlayerId, prompt, 0, 1, 0, yesLabel, noLabel,
                chosen => onChosen?.Invoke(chosen > 0));

        /// <summary>
        /// 【2026-09 用户实测·JC064】"**支付 N，否则牺牲本卡**"（`PayOrSacrificeSelf`）的**二选一确认窗**——
        /// 用户口径："JC064 支付费用的触发弹窗**不需要输入框**，参考 JC061"（JC061 的可选支付＝两个按钮）。
        /// `onChosen(true)`＝支付 N；`false`＝牺牲本卡（费用不足时调用方根本不会问，直接牺牲）。
        /// 默认实现：退回数值输入窗（0/1，标签为 `"支付 N"` / `"牺牲"`）⇒ 旧 View / 测试桩不受影响。
        /// </summary>
        void PromptPayOrSacrifice(int chooserPlayerId, string sourceName, int amount, System.Action<bool> onChosen)
            => PromptNumberInput(chooserPlayerId, $"{sourceName}：支付 {amount} 还是牺牲它？", 0, 1, 1,
                $"支付 {amount}", "牺牲", chosen => onChosen?.Invoke(chosen > 0));

        /// <summary>
        /// 【P2】弃牌：请求 UI 让 **chooserPlayerId** 从候选手牌中选出一张（候选属于 discarderId）。
        ///  · 一般情形 chooser == discarder（自己弃自己的手牌，如 JC130）；
        ///  · JC114 情形 chooser == 施术者（从对手手牌中指定，候选需对 chooser 可见）。
        /// UI 选完后必须经 onPicked 回调交回 Controller（由 Controller 执行"手牌→墓地"并广播 OnDiscard），
        /// View 不得自行移动卡牌。
        /// 无 UI 时调用方走确定性兜底（弃第一张并打日志）。
        /// </summary>
        void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
            System.Action<CardInstance> onPicked);

        /// <summary>
        /// 【P2】通用"从候选卡中选一张"（封印取手牌 / 检索取牌 / 检索『结附』选宿主共用；弃牌见 <see cref="PromptDiscard"/>）。
        ///  · 封印（Seal `Args[0]="手牌"`）：候选 = 封印者手牌；
        ///  · 检索（Search）：候选 = 牌库中通过筛选的牌；**【A53·2026-09 用户拍板】检索一律强制、不可取消**
        ///    （8 张 JC 检索卡的卡文都没有"你可以寻找"），故以 `allowCancel: false` 调用，`onPicked(null)` 不再表示"不取牌"；
        ///  · 检索『结附』选宿主（JC051）：候选 = 被检索卡自身 TargetRules 给出的合法宿主（≥2 个才询问），同样不可取消。
        /// UI 选完后必须经 onPicked 回调交回 Controller（由 Controller 执行移动/封印/结附并广播触发），View 不得自行移动卡牌。
        /// 无 UI 时调用方走确定性兜底（取第一张并打日志）。
        /// </summary>
        /// <param name="chooserPlayerId">做选择的玩家。</param>
        /// <param name="prompt">窗口提示文本（如"从牌库中寻找一张法术"）。</param>
        /// <param name="allowCancel">是否允许"不选"（true 时 onPicked(null) 合法；检索类已统一为 false）。</param>
        void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates, bool allowCancel,
            System.Action<CardInstance> onPicked);

        /// <summary>
        /// 【P4·遏制】请求 UI 让**被遏制者**从"场上各地区"中选择一处移除 1 个本方势力标志。
        /// 语义（用户 2026-09 规则）：`X &lt; 该玩家势力标志总数` 时，由**被遏制者按地区分配** X 次移除，
        /// **每地区上限 = 该地区该玩家的标记数**；因此这里**逐次**询问（每次移除 1 个），
        /// 由 Controller 在每次回调后重算候选并再次询问，直到 X 个分配完。
        /// X ≥ 总数 时**不弹窗**（自动全清）——那种情形不会走到本方法。
        /// UI 只负责"展示候选 + 收集选择"，实际移除由 Controller 经回调执行（View 不写 Model）。
        /// 无 UI 时调用方走确定性兜底（按地区序号贪心填满 X）。
        /// </summary>
        /// <param name="curbedPlayerId">被遏制者（做选择的人）。</param>
        /// <param name="remaining">还需分配的移除数。</param>
        /// <param name="options">可选地区与各自当前可移除数（>0 才在列表内）。</param>
        /// <param name="onPickedAreaUid">选中的地区 UID（移除 1 个）；null = 玩家放弃（按确定性兜底补齐）。</param>
        void PromptCurbMarks(int curbedPlayerId, int remaining, List<CurbMarkOption> options,
            System.Action<string> onPickedAreaUid);

        /// <summary>
        /// 【P4·预测X】请求 UI 让预测者把"牌库顶的 X 张"**任意数量置于牌库顶、其余置于牌库底**
        /// （卡文："检视你牌库顶的 X 张牌，将其中任意数量的牌以任意顺序置于你的牌库顶，将其余的牌以任意顺序置于你的牌库底。"）。
        /// 复用口径（用户指定）：View 直接复刻**调查对抗分配窗口**（`InvestigationWindow`：拖到"牌库顶/牌库底"两区，
        /// 同一容器内左右顺序即置回顺序）；`cards` 已由 Controller 从牌库顶取出（脱离牌库列表），
        /// View 只负责展示与收集分配，置回由 Controller 经回调统一落地（View 不写 Model）。
        /// 无 UI 时调用方走确定性兜底（原序全部留在牌库顶，牌库不变）。
        /// </summary>
        /// <param name="predictorId">预测者（做选择的玩家）。</param>
        /// <param name="count">本次检视张数（= cards.Count）。</param>
        /// <param name="cards">从牌库顶取出的 X 张（待分配）。</param>
        /// <param name="onConfirmed">确认回调：置顶顺序 + 置底顺序（两列表的并集应等于 cards）。</param>
        void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
            System.Action<List<CardInstance>, List<CardInstance>> onConfirmed);

        /// <summary>
        /// 【K15·多目标】请求 UI 完成"逐槽取目标 + 确认面板"，然后把结果交回 Controller。
        ///  · `candidatesForSlot(slotIndex, currentChoices)`：返回该槽**当前合法候选**——
        ///    View 必须**每选完一槽后重新调用**（地区槽先选，卡槽候选随之排除"已在目标地区内的角色"，
        ///    即 FAQ 的"禁止原地移动"）；
        ///  · 每个槽按 `SlotCandidates.Optional` 决定是否允许"留空"；`SlotCandidates.Label` 供窗口标题；
        ///  · 全部槽处理完后**弹出确认面板**展示已选目标：确认 → `onConfirmed(choices)`；
        ///    取消 → `onConfirmed(null)`（该牌不打出，留在手牌）。
        /// UI 只负责展示与收集，**合法性复检/付费/入堆叠一律由 Controller 执行**（View 不写 Model）。
        /// 无 UI（测试/headless）时调用方走确定性兜底。
        /// </summary>
        /// <param name="source">发起本次多目标选择的效果来源卡（如刚打出的事务卡）。</param>
        /// <param name="data">声明了 `TargetSlots` 的效果。</param>
        /// <param name="candidatesForSlot">按槽取候选（每次基于"当前已选"重新计算）。</param>
        /// <param name="onConfirmed">确认回调：确认后的逐槽选择；null＝玩家取消。</param>
        void PromptMultiTargets(CardInstance source, Script.Data.Effects.EffectData data,
            System.Func<int, List<Script.Manager.TargetChoice>, Script.Manager.SlotCandidates> candidatesForSlot,
            System.Action<List<Script.Manager.TargetChoice>> onConfirmed);

        /// <summary>
        /// 【K16·模式分支】请求 UI 让玩家**选定"选择一项"中的一项**（卡文"选择一项：·… ·…"）。
        /// 口径（用户 2026-09）：模式**在打出时选定、付费之前**，选定后入堆叠**不可更改**。
        /// UI 只负责展示选项并回传下标：`onChosen(index)`；取消/放弃 → `onChosen(-1)`（该牌不打出，留手牌）。
        /// 实际合法性校验、付费、入堆叠由 Controller（`TurnManager.PlayCard`）执行（View 不写 Model）。
        /// 无 UI（测试/headless）时调用方走确定性兜底（不选模式直接尝试打出 → 被校验拒绝，绝不静默代选）。
        /// </summary>
        /// <param name="source">效果来源卡（刚打出的事务卡）。</param>
        /// <param name="data">声明了 `Modes` 的效果。</param>
        /// <param name="onChosen">选定模式的下标；-1＝取消。</param>
        void PromptEffectMode(CardInstance source, Script.Data.Effects.EffectData data, System.Action<int> onChosen);

        /// <summary>
        /// 【2026-09 用户口径 JC063】"选择一项" + **各模式可用性**（该模式当前是否有合法目标）：
        /// `availability` 与 `data.Modes` 同序，true ＝ 可用；false ＝ 该模式**当前没有合法目标**，View 应**置灰**。
        /// 用户口径："JC063 两个效果如果场上完全没有合法目标，置灰（注意两个效果要求的合法目标不一致）"
        /// —— 可用性由 Controller 用**与真正取目标同一份枚举**算好（`TurnManager.GetModeAvailability`），
        /// View 只展示不重算。
        /// 默认实现：忽略可用性、退回既有 <see cref="PromptEffectMode"/>（旧 View / 测试桩不受影响）。
        /// </summary>
        void PromptEffectModeWithAvailability(CardInstance source, Script.Data.Effects.EffectData data,
            System.Collections.Generic.List<bool> availability, System.Action<int> onChosen)
            => PromptEffectMode(source, data, onChosen);

        /// <summary>
        /// 【K11·可选支付 / 数值输入】请求 UI 弹出一个**数值输入框**：玩家输入一个数字 N（`min`..`max`，初始 `defaultValue`），
        /// 再按 `confirmLabel`（如"支付"）或 `cancelLabel`（如"不支付"）。
        /// 用户 2026-09 口径：**输入的数字即为 N**；取消/不支付 → `onChosen(-1)`。
        /// 覆盖：卡文"你可以支付 N，若你如此做…"（JC061/JC100/JC105）、"选择一个数字"（JC083）、"展示任意数量的手牌"（JC081）。
        /// UI 只负责收集数字，扣费与后续动作由 Controller 执行（View 不写 Model）。
        /// </summary>
        /// <param name="chooserPlayerId">做选择的玩家（一般＝来源卡控制者）。</param>
        /// <param name="prompt">窗口提示文本。</param>
        /// <param name="min">允许的最小值。</param>
        /// <param name="max">允许的最大值（如"可支付的最大费用"）。</param>
        /// <param name="defaultValue">输入框初始值（会被钳制到 [min,max]）。</param>
        /// <param name="confirmLabel">确认按钮文本（支付场景＝"支付"）。</param>
        /// <param name="cancelLabel">取消按钮文本（支付场景＝"不支付"）。</param>
        /// <param name="onChosen">玩家确认的数字；-1＝取消/放弃。</param>
        void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
            string confirmLabel, string cancelLabel, System.Action<int> onChosen);

        /// <summary>
        /// 【K24·玩家目标】请求 UI 弹出**独立的目标玩家选择窗口**：把候选玩家列成按钮（玩家不是场上的卡节点，
        /// 无法用"点选/拖拽到目标"表达 —— 用户 2026-09 口径）。
        /// 用于**触发路径**：触发类效果若以玩家为目标（`TargetSpec.Kind=Player`），在入堆叠前必须就地选定
        /// （候选由 `PlayerTargetPool.Collect` 给出；目标与卡目标同口径＝**入堆叠时锁定**）。
        /// UI 只负责收集选择：选中 → `onChosen(playerId)`；取消/放弃 → `onChosen(-1)`（该触发不入堆叠）。
        /// 实际合法性复检与入堆叠由 Controller（`TurnManager.EnsureTargetThenPush`）执行（View 不写 Model）。
        /// 无 UI（测试/headless）时调用方走确定性兜底（取第一个候选，与卡目标既有口径一致）。
        /// </summary>
        /// <param name="chooserPlayerId">做选择的玩家（一般＝触发来源卡的控制者）。</param>
        /// <param name="prompt">窗口提示文本（如"请为 监控员 选择目标玩家"）。</param>
        /// <param name="candidates">候选玩家号（升序；>0 个才会调用本方法）。</param>
        /// <param name="onChosen">选中的玩家号；-1＝取消。</param>
        void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
            System.Action<int> onChosen);

        /// <summary>
        /// 【JC119·K28】请求 UI 为"**由效果免费打出的牌**"选择目标（这类牌的打出**不是玩家拖拽发起的**，
        /// 因此没有"拖到目标上"这一交互可用，必须由 UI 弹一次**目标选择窗**）。
        /// 口径与"激活效果选目标"一致：**卡目标**＝场上合法目标卡高亮 + 点击；**地区目标**＝地区槽高亮 + 点击；
        /// 窗口只负责收集选择，**合法性复检/付费/入堆叠一律由 Controller（`TurnManager.PlayCard`）执行**（View 不写 Model）。
        /// 取消/关闭窗口 → `onPicked(null)`（该牌**留在手牌**，绝不静默代打）。
        ///
        /// 说明（为什么本方法带**默认实现**）：`IGameUIHooks` 的既有 8 个方法都是纯抽象，本方法增加时
        /// 已有 20+ 处测试桩（`TestRunner` 内的 `Stub` 类）会因"未实现接口成员"而全量编译失败；
        /// 而本方法的"无实现"语义是明确的**fail-closed**（回调 null＝不打这张牌），故给一个**显式兜底默认体**
        /// （打日志 + 回调 null），既不改动测试桩、也不会静默打出。生产实现见 `Arena.PromptPlayTarget`。
        /// </summary>
        /// <param name="chooserPlayerId">做选择的玩家（＝该免费打出牌的控制者）。</param>
        /// <param name="prompt">窗口提示文本（如"请为 火球术 选择目标"）。</param>
        /// <param name="isArea">true＝地区目标（高亮地区槽）；false＝卡目标（高亮场上卡）。</param>
        /// <param name="candidateUids">候选 UID（由 Controller 枚举，UI 不得自行扩缩）。</param>
        /// <param name="onPicked">选中的目标 UID；null＝取消（该牌留在手牌）。</param>
        void PromptPlayTarget(int chooserPlayerId, string prompt, bool isArea, List<string> candidateUids,
            System.Action<string> onPicked)
        {
            Godot.GD.PrintErr("[IGameUIHooks] ⚠ PromptPlayTarget 未实现（该 View 未提供目标选择窗）→ 按取消处理（不打出该牌）");
            onPicked?.Invoke(null);
        }

        /// <summary>
        /// 【K11·触发版】**触发入堆叠前的"可选固定费用"**：问玩家"**是否支付 N**"——
        /// **只有"是/否"两个选项，不需要输入金额**（选"是"就支付 N）。
        ///
        /// 语义（用户 2026-09 口径 JC061）：
        ///  · 否 → 调用方**不把该触发入堆叠**（整条效果不结算，也不扣费）；
        ///  · 是 → 调用方扣 N 点费用后再入堆叠。
        /// **放弃整条触发由更上层的触发排序窗负责**（`EffectData.Forced`），故本窗**不提供"取消"**。
        ///
        /// 说明（为什么本方法带**默认实现**）：与 `PromptPlayTarget` / `WaitForUIRefresh` 同款口径——
        /// `IGameUIHooks` 既有成员为纯抽象，本方法新增时已有 20+ 处测试桩（各测试文件内的 Stub 类）
        /// 会因"未实现接口成员"而全量编译失败；而本方法的"无实现"语义明确是 **fail-closed**
        /// （不支付 ⇒ 不入堆叠，**绝不替玩家花钱**），故给一个显式兜底默认体。
        /// 生产实现见 `Arena.PromptOptionalPay`。
        /// </summary>
        /// <param name="chooserPlayerId">做选择的玩家（＝该触发来源卡的控制者）。</param>
        /// <param name="prompt">窗口提示文本（如"危机处理特派小组：是否支付 1？"）。</param>
        /// <param name="amount">要支付的固定费用 N（调用方已确认玩家付得起才会调用本方法）。</param>
        /// <param name="onChosen">true＝支付；false＝不支付。</param>
        void PromptOptionalPay(int chooserPlayerId, string prompt, int amount, System.Action<bool> onChosen)
        {
            Godot.GD.PrintErr("[IGameUIHooks] ⚠ PromptOptionalPay 未实现 → 按『不支付』处理（该触发不入堆叠，绝不替玩家花钱）");
            onChosen?.Invoke(false);
        }

        /// <summary>
        /// 【2026-09·JC062/JC057 用户口径】**检视结果的合并呈现**：把本次结算中被私下检视到的**全部**牌面
        /// **列在同一个窗**里给检视者看，框里**只有一个『确认』按钮**，点击即关闭并继续结算。
        ///
        /// 用户口径（原文）："JC062 的查看结果方式是结算的时候弹出一个弹窗，里面**列出所有看到的信息**，
        /// 这个框的按钮**只有一个确认**，点击后关闭弹窗"＋"JC057 的查看方式和 JC062 一致"＋补问裁定
        /// "**等点确认后再继续结算**（JC062 的抓牌排在确认之后）；**两个目标合并为同一个窗**"。
        ///
        /// 由 `EffectEngine.FlushInspectBatch` 在**最外层结算收尾**调用（同批只调一次，已合并）；
        /// 调用前引擎已挂起（决策持锁），UI 只负责"展示 + 收集一次确认"，实际续算由回调驱动
        /// （`onConfirmed` → `ContinuePendingChain`），View 不写 Model。
        ///
        /// 说明（为什么本方法带**默认实现**）：与 `PromptPlayTarget` / `WaitForUIRefresh` / `PromptOptionalPay`
        /// 同款口径——新增抽象成员会让既有 20+ 处测试桩全量编译失败；而"无实现"的语义是明确的
        /// **fail-open**（检视只是呈现，不改变任何规则状态）⇒ 直接回调确认、绝不死锁。
        /// 生产实现见 `Arena.PromptInspectCards`（联机见 `ArenaOl.OpenOnlineInspectWindow`）。
        /// </summary>
        /// <param name="viewerPlayerId">检视者（＝效果来源卡控制者）。</param>
        /// <param name="title">窗口标题（如"检视 2 张牌"）。</param>
        /// <param name="cards">本批被检视的全部卡（**同一窗全部列出**；可为暗藏者/对手手牌）。</param>
        /// <param name="onConfirmed">玩家点『确认』（或关闭窗口）后回调，续算结算流程。</param>
        void PromptInspectCards(int viewerPlayerId, string title, System.Collections.Generic.List<CardInstance> cards,
            System.Action onConfirmed)
        {
            Godot.GD.PrintErr("[IGameUIHooks] ⚠ PromptInspectCards 未实现 → 检视结果不弹窗，直接继续结算");
            onConfirmed?.Invoke();
        }

        /// <summary>
        /// 【UI 立即刷新·帧同步点】请求 View **让出一帧、把当前已变更的 UI 真正渲染出来后再继续**。
        /// 用途：恢复步骤手牌上限"先手弃牌 → UI 立即刷新 → 后手可见 → 后手弃牌"的严格顺序——
        /// Controller 在一名玩家弃牌（已改 Model 且 View 已同步数据）后 <c>await</c> 本方法，
        /// 保证 View 至少完成一次重绘/布局，后手弹窗前就能**看到**先手弃入墓地的牌。
        ///
        /// 说明（为什么给**默认实现**）：`IGameUIHooks` 既有成员为纯抽象，本方法新增时
        /// 已有 20+ 处测试桩（`TestRunner`/各测试的 Stub）会因"未实现接口成员"而全量编译失败；
        /// 而 headless/测试环境本就**无渲染需要**，故默认实现直接返回已完成任务（不引入任何帧等待），
        /// 语义上等价于"无 UI 环境无需刷新"。生产实现见 `Arena.WaitForUIRefresh`（等待一帧 ProcessFrame）。
        /// </summary>
        System.Threading.Tasks.Task WaitForUIRefresh()
            => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>【P4·遏制】一个"可移除地区"选项：地区 UID + 序号（1-based，显示用）+ 当前可移除数。</summary>
    public sealed class CurbMarkOption
    {
        public string AreaUid;
        public int SlotIndex;
        /// <summary>该地区该玩家当前可被移除的势力标志数（每地区上限）。</summary>
        public int Removable;
    }
}
