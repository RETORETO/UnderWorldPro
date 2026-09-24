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

        // ---- 【K15】多目标：逐槽选择 / 确认面板 ----
        /// <summary>MultiTargetSlot：当前槽序号（0-based）。</summary>
        public int SlotIndex;
        /// <summary>MultiTargetSlot：本效果的槽总数。</summary>
        public int SlotCount;
        /// <summary>MultiTargetSlot：该槽是否可留空（true 时客户端应提供"留空"入口）。</summary>
        public bool SlotOptional;
        /// <summary>MultiTargetSlot：槽位提示（如"目标地区""本方角色""敌方角色"）。</summary>
        public string SlotLabel;

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
    }
}
