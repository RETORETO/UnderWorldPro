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
            SelectTriggerOrder,// 触发排序窗口
            ForceChoice,       // 强制二选一/多选一
            Concede,           // 认输
            CombatInvestigation,// 调查对抗：胜者分配牌库顶 X 张（OptionUids=候选卡，Amount=X）
            CombatStrength,    // 战斗对抗：败者分配 Amount 点伤害（OptionUids=合法承伤目标）
            TieBreak,          // 先手破平确认（确认=回 Cancelled=false；取消=回 Cancelled=true）
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
    }
}
