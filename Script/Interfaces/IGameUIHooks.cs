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
        /// 【P2】通用"从候选卡中选一张"（封印取手牌 / 检索取牌共用；弃牌见 <see cref="PromptDiscard"/>）。
        ///  · 封印（Seal `Args[0]="手牌"`）：候选 = 封印者手牌；
        ///  · 检索（Search）：候选 = 牌库中通过筛选的牌（**允许取消** → onPicked(null) 表示"不取牌"，
        ///    卡文"你可以寻找…"的"可以"由玩家自行决定；服务端随后仍会洗牌）。
        /// UI 选完后必须经 onPicked 回调交回 Controller（由 Controller 执行移动/封印并广播触发），View 不得自行移动卡牌。
        /// 无 UI 时调用方走确定性兜底（取第一张并打日志）。
        /// </summary>
        /// <param name="chooserPlayerId">做选择的玩家。</param>
        /// <param name="prompt">窗口提示文本（如"从牌库中寻找一张法术"）。</param>
        /// <param name="allowCancel">是否允许"不选"（true 时 onPicked(null) 合法）。</param>
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
