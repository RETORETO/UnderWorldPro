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
    }
}
