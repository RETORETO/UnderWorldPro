using System.Collections.Generic;
using System.Threading.Tasks;

namespace Script.Net
{
    /// <summary>
    /// 【统一决策接口】核心规则层唯一的"需要玩家决策"抽象（单机/联机共用契约）。
    ///
    /// 规则结算引擎（EffectEngine / TurnManager）遇到"等待玩家选择"时，不应阻塞线程去等本地 UI，
    /// 而应挂起当前结算上下文、向目标玩家发起一个 ChoiceRequest、await 其 ChoiceResult 回来后续算。
    ///
    /// 决策语义一律以单机模式为准：
    /// - 单机：不注入本接口（InputGateway == null），核心走本地 IGameUIHooks / ITriggerView 的同步窗口路径
    ///   （现有行为不变，向后兼容）。
    /// - 联机：服务端注入 NetworkInputGateway，把同一份 ChoiceRequest 经网络下发客户端、await 回包续算。
    ///
    /// 实现：
    /// - NetworkInputGateway：联机服务端，经 INetTransport 下发/回收。
    /// - LocalInputGateway：单机/测试兼容桥（当前仅覆盖 PayOrTap 的占位语义，其余种类默认取消）。
    /// - AutoInputGateway：测试用预设答案。
    ///
    /// 纪律：规则层只依赖本接口，绝不在 Controller 内 new UI 类或 await Godot 节点。
    /// </summary>
    public interface IInputGateway
    {
        /// <summary>
        /// 向指定玩家请求一个选择，返回当该玩家回应后的结果。
        /// 实现可同步返回（Auto/Local）或真正异步等待网络（Network）。
        /// </summary>
        Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req);
    }

    public class ChoiceRequest
    {
        public enum Kind
        {
            PayOrTap,          // JC068 准备维护：支付 N / 横置二选一
            SelectTarget,      // 选目标（触发/效果）
            SelectHandCard,    // 【P2】从手牌中选一张（弃牌；JC114 由施术者从对手手牌中指定；封印取手牌）
            SelectDeckCard,    // 【P2】从牌库中选一张（检索取牌；候选随 OptionCardRefs 下发，因牌库对双方不可见）
            SelectTriggerOrder,// 触发排序窗口
            ForceChoice,       // 强制二选一/多选一
                               //   【2026-09 起亦用于】触发入堆叠前的"**是否支付 N**"（`EffectData.OptionalPayCost`，
                               //   见 `TurnManager.EnsureOptionalPayThenPushAsync`）：`OptionUids` 用哨兵
                               //   `__optpay_yes`/`__optpay_no`，`OptionLabels` 给出按钮文案（"支付 N"/"不支付"），
                               //   `ForceChoice=true` ⇒ 面板（`BuildOptions`）**不追加"取消"按钮**，
                               //   严格只有两个选项；回包 `SelectedUid`；取消/超时/非法 → 按"不支付"（fail-closed）。
            Concede,           // 认输
            CombatInvestigation,// 调查对抗：胜者分配牌库顶 X 张（OptionUids=候选卡，Amount=X）
            CombatStrength,    // 战斗对抗：败者分配 Amount 点伤害（OptionUids=合法承伤目标）
            TieBreak,          // 先手破平确认（确认=回 Cancelled=false；取消=回 Cancelled=true）
            CurbMarks,         // 【P4】遏制X：被遏制者按地区分配 X 次「移除本方势力标志」
                               //   （OptionUids=地区 UID，OptionMaxAmounts=每地区上限，Amount=X；
                               //    回包 SelectedUids/SelectedAmounts 同序表达"某地区移除几个"）
            Prediction,        // 【P4】预测X：检视牌库顶 X 张，任意数量置顶/底
                               //   （OptionUids=这 X 张牌，OptionCardRefs=展示引用，Amount=X；
                               //    回包 SelectedUids=置于牌库顶的牌（有序），未列出的按原序置底——与调查对抗同口径）
            SelectZoneCard,    // 【K14】选"区域牌堆里的牌"作为目标（墓地/手牌）：
                               //   OptionUids=候选卡，OptionCardRefs=展示引用（这些卡不在场上、没有 CardUI，
                               //   故客户端走**通用决策面板的按钮式选卡**，而非"场上点选"）；回包 SelectedUid
            MultiTargetSlot,   // 【K15】多目标槽位的**逐槽**选择（服务端驱动）：
                               //   SlotIndex/SlotCount/SlotLabel/SlotOptional 描述当前槽；
                               //   OptionUids=该槽当前合法候选（地区槽＝地区 UID；卡槽＝卡 UID），
                               //   OptionCardRefs=卡槽候选的展示引用，OptionLabels=候选显示名（与 OptionUids 同序）；
                               //   OrderContextId=本次多目标选择的上下文 id（客户端据此复用同一面板）；
                               //   回包：SelectedUid＝选中项；**空串＋Cancelled=false＝该槽留空**（可留空的槽）；
                               //   Cancelled=true＝取消整次打出
            ConfirmTargets,    // 【K15】多目标**确认面板**（用户 2026-09 口径：确认后才把效果加入堆叠）：
                               //   OptionLabels=逐槽摘要（如"目标地区=地区2"），OptionUids=已选卡目标（可空），
                               //   OptionCardRefs=已选卡的展示引用；
                               //   回包：Confirmed=true＝确认；false/Cancelled=true＝取消（不打出）
            FieldMultiPick,    // 【2026-09 用户口径 JC062/JC021】多目标**一次性整窗选择**（服务端聚合下发）：
                               //   与单机 `Arena.TryPromptFieldMultiPick` **同款交互与文案**——"一个窗 + 场上高亮 +
                               //   左键选择/右键取消 + 确认/取消"（`MultiTargetSlots.ClassifyShape == FieldMultiPick` 的形状才走本请求）：
                               //   · OptionUids/OptionCardRefs/OptionLabels ＝ 全部**卡槽**候选（各未占用槽候选的并集）；
                               //   · OptionAreaUids ＝ **地区槽**候选（地区 UID）；
                               //   · MultiCardLabel/MultiAreaLabel ＝ 两组标题文案（如"目标暗藏者"／"目标角色"＋"目标地区"）；
                               //   · MultiMaxCards/MultiMaxAreas/MultiMinCards/MultiAreaRequired ＝ 上限/下限/地区是否必选；
                               //   · MultiSameControllerOnce ＝"每个玩家场上的角色至多 1 个"（JC021 口径）；
                               //   回包：SelectedUids＝已选**卡**（有序）、SelectedAreaUids＝已选**地区**；
                               //        Cancelled=true＝取消整次打出。
                               //   口径：本请求的"确认"**就是最终确认** ⇒ 不再另发 `ConfirmTargets` 面板（与单机一致）。
            NumberInput,       // 【K11】数值输入框（"你可以支付N" / "选择一个数字" / "展示任意数量"）：
                               //   NumberMin/NumberMax/NumberDefault＝输入范围与初值；
                               //   OptionLabels[0]/OptionTexts[0] 可携带确认/取消按钮文案；
                               //   回包：SelectedAmounts[0]＝玩家输入的数字；Cancelled=true＝取消（如"不支付"）
            InspectCards,      // 【P3·H】检视（**私密**）：把被检视卡的面**只下发给检视者本人**，
                               //   OptionCardRefs=被检视卡的展示引用（服务端按座位寻址，天然只有该玩家能看到）；
                               //   回包：Cancelled=false＝已查看（客户端面板"确认"），Cancelled=true＝超时/放弃查看
            SelectEffectMode,  // 【K16】模式分支："选择一项"（卡文"选择一项：·… ·…"）：
                               //   OptionLabels=各模式名（如"将目标角色潜伏"），Amount=模式数；
                               //   回包：SelectedIndices[0]＝选定模式下标（0-based）；Cancelled=true＝取消整次打出。
                               //   口径：模式在**打出时选定、付费之前**，入堆叠后不可更改。
            SelectPlayer,      // 【K24】玩家目标（"目标玩家/目标对手"）：
                               //   OptionPlayerIds=候选玩家号（升序，如 [2]），Prompt=提示；
                               //   回包：SelectedPlayerId＝选中的玩家号；Cancelled=true＝取消（该触发不入堆叠）。
                               //   口径：玩家不是场上的卡节点 → 独立选择窗口；目标**入堆叠时锁定**。
            SelectActivationCost, // 【P2·联机】**激活代价选择**（与单机 `Arena.BeginActivateWithSacrifice` /
                               //   `BeginActivateWithCounterCostChoice` / `BeginActivateWithCounterCostAllocation`
                               //   三套窗 **1:1** 对应；客户端用 `OnlineActivationChoiceWindow`＝单机 `ActivationChoiceWindow` 的联机孪生）：
                               //   · OptionUids=候选卡 UID；OptionLabels=与候选同序的按钮文案（服务端组装，如"牺牲 X"/
                               //     "从 X 上移除 1 个标志（已选 0 / 共 2）"）；
                               //   · Amount = **固定数量模式**下需要移除的标志数（>0）；**逐卡分配模式**传 ≤0（总量由玩家自选、至少 1）；
                               //   · OptionMaxAmounts = 逐卡分配模式：与候选同序的"该卡当前标志数"（客户端据此显示 `已选/共`）；
                               //   · ForceChoice=true（必须选一个或取消整次激活）；
                               //   回包：固定/单张模式 → SelectedUid；逐卡分配模式 → SelectedUids + SelectedAmounts（同序、>0 项）；
                               //        Cancelled=true ＝取消（**不发动**，不付任何代价）。
            SelectHandCards,   // 【K10·额外费用·JC081】"展示任意数量的手牌"（多选）：
                               //   OptionUids=候选手牌 UID（＝打出者当前手牌），OptionCardRefs=展示引用，
                               //   OptionLabels=与候选同序的显示名（含"（合格 -1）/（不合格）"标记），
                               //   NumberMax=最多可选张数（＝候选数），ForceChoice=false（允许"一张都不展示"）；
                               //   回包：Confirmed=true ＋ SelectedUids=**已选（有序）**的牌（**可空**＝不展示、全额支付）；
                               //        Cancelled=true ＝取消整次打出（不扣任何费用）。
                               //   口径（A22/A23）：不展示＝没有减免但照样能打出；取消＝不打出、卡不入堆叠、不扣费。
            SelectMulligan,    // 【开局·调度步骤】发完起手手牌后、第一回合开始前，双方各选择**任意数量**手牌调度：
                               //   OptionUids=该玩家当前全部手牌 UID，OptionCardRefs=展示引用，
                               //   NumberMax=可选上限（＝候选手牌数），ForceChoice=false（允许"一张都不换"）；
                               //   回包：Confirmed=true ＋ SelectedUids=**已选**的牌（可空）；
                               //        Cancelled=true ＝视同未选（等价空选择，**不阻塞开局**）。
                               //   服务端等**双方**都回包后统一结算：所选牌置牌库底 → 从牌库顶抽等量 → 洗牌。
        }

        /// <summary>请求唯一标识，用于服务端将回包与挂起请求匹配（correlation id）。</summary>
        public string RequestId;
        public int PlayerId;
        public Kind RequestKind;
        public string Prompt;
        public string SourceUid;                 // 发起方卡 UID
        public List<string> OptionUids = new List<string>(); // 候选（目标/触发/对抗候选 UID 列表）
        public int PayAmount;                    // PayOrTap 用
        /// <summary>通用数值：CombatStrength=总伤害；CombatInvestigation=查看张数。</summary>
        public int Amount;
        /// <summary>是否强制选择（不可取消/跳过）。触发排序窗口内若存在 Forced 触发，客户端应隐藏"放弃"。</summary>
        public bool ForceChoice;

        // ---- 对抗类请求的补充上下文（客户端复用单机对抗窗口所需；2026-09-05）----
        /// <summary>对抗所属地区 UID（CombatStrength/CombatInvestigation/TieBreak 用）。</summary>
        public string AreaUid;
        /// <summary>对抗所属地区序号（1-based，窗口标题显示用）。</summary>
        public int AreaNo;
        /// <summary>败方玩家（CombatStrength：承伤方；窗口标题/提示用）。</summary>
        public int LoserPlayerId;
        /// <summary>对抗类型（CombatManager.CombatType 的整数值：0=Investigation/1=Strength/2=Power）。</summary>
        public int CombatTypeValue;
        /// <summary>
        /// 候选对象的**展示引用**（与 OptionUids 同序、一一对应）：供客户端在"服务端等待决策期间"直接重建
        /// 展示用卡实例（典型：调查对抗里要被分配放回牌库顶/底的 X 张卡）。
        /// 说明：决策等待期间服务端不会推快照，故这些卡必须随请求下发，否则客户端无法显示单机同款窗口。
        /// </summary>
        public List<ZoneCardRef> OptionCardRefs = new List<ZoneCardRef>();

        // ---- 决策面板对齐单机的补充字段（2026-09-05 · 第 3 版）----
        /// <summary>
        /// PayOrTap：服务端权威判定的"是否支付得起"（`EffectEngine.CanPayPrepareUpkeep`）。
        /// 客户端仅据此置灰"支付"选项（对齐单机 `Arena.PromptPayOrTap` 的费用不足置灰），**不重算规则**。
        /// </summary>
        public bool PayAllowed;
        /// <summary>
        /// 【2026-09 用户口径 JC063】`SelectEffectMode`：与 `OptionLabels` 同序的**置灰表**
        /// （true ＝ 该模式**当前没有合法目标**，客户端应置灰且点击无效）。
        /// 用户口径："JC063 两个效果如果场上完全没有合法目标，置灰（注意两个效果要求的合法目标不一致）"
        /// —— 由服务端按**与真正取目标同一份枚举**（`TurnManager.GetModeAvailability`）算好后下发；
        /// 未下发（旧服务端）＝全 false → 客户端不置灰（不静默丢选择）。
        /// </summary>
        public List<bool> OptionDisabled = new List<bool>();

        /// <summary>
        /// SelectTriggerOrder：与 <see cref="OptionUids"/> 同序的"是否强制"标记（真=不可放弃/不可拖入放弃区）。
        /// </summary>
        public List<bool> OptionForced = new List<bool>();
        /// <summary>
        /// SelectTriggerOrder：本次排序窗口的**上下文 id**（同一 owner、同一次触发阶段的排序窗口保持不变）。
        /// 客户端据此**复用同一窗口**：逐次入堆叠会产生多轮请求-响应，窗口不应反复关闭重开（对齐单机窗口保持打开）。
        /// </summary>
        public string OrderContextId;
        /// <summary>与 OptionUids 同序的条目名（SelectTriggerOrder：触发名 = `StackEffectInstance.DisplayName`）。</summary>
        public List<string> OptionLabels = new List<string>();
        /// <summary>与 OptionUids 同序的效果文本（SelectTriggerOrder：`StackEffectInstance.DisplayText`）。</summary>
        public List<string> OptionTexts = new List<string>();
        /// <summary>
        /// 与 OptionUids 同序的**每项上限**（CurbMarks：某地区当前可被移除的本方势力标志数）。
        /// 客户端据此钳制输入框上限（与单机逐次询问的"每地区上限＝该地区标记数"同解）；
        /// 服务端仍会按同一下限/上限**权威复检**回包，客户端只做展示。
        /// </summary>
        public List<int> OptionMaxAmounts = new List<int>();

        // ---- 【K15·多目标】逐槽选择 / 确认面板 ----
        /// <summary>MultiTargetSlot：当前槽序号（0-based）。</summary>
        public int SlotIndex;
        /// <summary>
        /// 【K15·多目标·2026-09 用户口径 A86】当前槽的候选**在场上看得见**吗——
        /// 服务端用**同一份判定**（`MultiTargetSlots.ShouldPickOnField`：地区槽 / 非区域牌堆的卡槽，且候选非空）
        /// 算好后下发，客户端据此把该槽渲染成"**场上高亮 + 点击**"（与单机
        /// `Arena.PromptMultiTargets` 的 `AskSlotByFieldClick` 同款），而不是候选按钮列表。
        /// 兜底：未下发（旧服务端）=false → 客户端继续用按钮列表（不静默丢选择）。
        /// </summary>
        public bool SlotFieldVisible;
        /// <summary>MultiTargetSlot：本效果的槽总数。</summary>
        public int SlotCount;
        /// <summary>MultiTargetSlot：该槽是否可留空（true 时客户端应提供"留空"入口）。</summary>
        public bool SlotOptional;
        /// <summary>MultiTargetSlot：槽位提示（如"目标地区""本方角色""敌方角色"）。</summary>
        public string SlotLabel;
        /// <summary>
        /// 【2026-10·JC092】MultiTargetSlot：该槽的**动作提示**（`TargetSlot.Hint` 原文，如
        /// "将你墓地中的角色牌以暗藏者状态置于该地区"）。客户端在场点选窗口里作为括号补充展示，
        /// **不再硬编码**"将移动至该地区"（那是移动类效果的说法，JC092 是"置于地区"）。
        /// 空＝客户端用中性兜底文案。
        /// </summary>
        public string SlotHint;

        // ---- 【2026-09·FieldMultiPick】"一个窗多选取目标"（JC062/JC021；服务端聚合下发）----
        /// <summary>FieldMultiPick：**地区槽**候选（地区 UID）。卡槽候选仍走 `OptionUids`。</summary>
        public List<string> OptionAreaUids = new List<string>();
        /// <summary>FieldMultiPick：卡槽组标题（JC062＝"目标暗藏者"；JC021＝"目标角色"）。</summary>
        public string MultiCardLabel;
        /// <summary>FieldMultiPick：地区槽组标题（如"目标地区"）；无地区槽时为空。</summary>
        public string MultiAreaLabel;
        /// <summary>FieldMultiPick：至多可选几张卡（＝卡槽数）。</summary>
        public int MultiMaxCards;
        /// <summary>FieldMultiPick：至多可选几个地区（0 或 1）。</summary>
        public int MultiMaxAreas;
        /// <summary>FieldMultiPick：至少需要选几张卡（JC062＝0「只为抓一张牌」；JC021＝1）。</summary>
        public int MultiMinCards;
        /// <summary>FieldMultiPick：地区槽是否**必选**（JC021=true ⇒ 没选地区时"确认"应置灰）。</summary>
        public bool MultiAreaRequired;
        /// <summary>FieldMultiPick：是否"**每个玩家场上的角色只能选择一个**"（JC021 口径；JC062=false）。</summary>
        public bool MultiSameControllerOnce;

        // ---- 【K11】数值输入框 ----
        /// <summary>NumberInput：允许的最小值。</summary>
        public int NumberMin;
        /// <summary>NumberInput：允许的最大值（如"可支付的最大费用"）。</summary>
        public int NumberMax;
        /// <summary>NumberInput：输入框初始值（已钳制到 [Min,Max]）。</summary>
        public int NumberDefault;

        // ---- 【K24】玩家目标 ----
        /// <summary>
        /// SelectPlayer：候选**玩家号**（升序）。玩家不是场上的卡节点、也没有 UID，故单列一字段
        /// （不用 `OptionUids` 硬塞；与 `OptionLabels` 可选同序给出显示名）。
        /// </summary>
        public List<int> OptionPlayerIds = new List<int>();
    }

    public class ChoiceResult
    {
        /// <summary>对应请求的 RequestId，用于服务端将回包与挂起请求匹配（correlation id）。</summary>
        public string RequestId;
        public string SelectedUid;   // 选中的目标/触发 UID（SelectTarget/Order 用）
        public bool Pay;             // PayOrTap：true=支付，false=横置
        public bool Cancelled;       // 玩家取消/超时
        /// <summary>多选结果（有序）：CombatInvestigation=放牌库顶的卡 UID 顺序；CombatStrength=分配伤害的目标 UID。</summary>
        public List<string> SelectedUids = new List<string>();
        /// <summary>与 SelectedUids 一一对应的数值：CombatStrength=各目标分配伤害点数。</summary>
        public List<int> SelectedAmounts = new List<int>();
        /// <summary>
        /// 【2026-10】CombatInvestigation / Prediction 回包：**置于牌库底的卡 UID（有序，顺序＝底组内上→下）**。
        /// 与 `SelectedUids`（＝置于牌库顶的卡，有序）一起表达两个框各自的内部顺序。
        /// 未回传（null/空）时服务端退化为"未选中的卡按原牌序置底"（旧行为；
        /// 兼容旧客户端 / 客户端兜底面板 / Bot，且保证不丢牌）。
        /// </summary>
        public List<string> BottomUids = new List<string>();
        /// <summary>
        /// SelectTriggerOrder：本次要入堆叠的条目**下标序列**（指向该次请求的 `OptionUids`，顺序即入栈先后）。
        /// 语义：本次请求里"玩家已放入入堆叠区"的触发；未列出的条目视为仍在排序窗（服务端据此续发下一轮请求）。
        /// 空列表 + `Cancelled=false` 表示"没有要入堆叠的条目"；`Cancelled=true` 表示放弃剩余。
        /// 兼容：旧客户端不回本字段时，服务端退化为"全部、默认顺序"（与旧行为一致）。
        /// 注：不能用 `SelectedUids` 表达顺序——同一来源卡可能有多个触发，UID 会重复。
        /// </summary>
        public List<int> SelectedIndices = new List<int>();

        /// <summary>
        /// 【K15】确认类请求的回包：`ConfirmTargets` 用 true＝确认；false/Cancelled=true＝取消（不打出）。
        /// 必须是 **public 字段**才能被 `ChoiceSerializer`（`IncludeFields`）往返。
        /// </summary>
        public bool Confirmed;

        /// <summary>
        /// 【K24】`SelectPlayer` 的回包：选中的**玩家号**；≤0 视同未选（配合 `Cancelled` 一起判）。
        /// </summary>
        public int SelectedPlayerId = -1;

        /// <summary>
        /// 【2026-09·FieldMultiPick】回包：已选**地区** UID（与 `SelectedUids`＝已选**卡**分开表达，
        /// 因为两者是不同类别的槽，且同一列表混装会丢失类别信息）。
        /// </summary>
        public List<string> SelectedAreaUids = new List<string>();
    }
}
