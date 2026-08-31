using System.Collections.Generic;
using System.Threading.Tasks;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P0-B】输入网关接口：将"需要玩家决策"的同步弹窗语义解耦为异步请求-响应。
    ///
    /// 设计动机（对照 MTGA 类服务端权威架构）：
    /// 规则结算引擎（EffectEngine / TurnManager）遇到"等待玩家选择"时，不应阻塞线程去等本地 UI，
    /// 而应挂起当前结算上下文、向目标玩家发起一个 ChoiceRequest、await 其 ChoiceResult 回来后续算。
    ///
    /// 三个实现：
    /// - LocalInputGateway：包装现有 IGameUIHooks（本地弹窗），保持现有单机行为，向后兼容。
    /// - AutoInputGateway：测试用，直接返回预设答案，无需人工/UI 介入。
    /// - （未来）NetworkInputGateway：将请求经网络下发客户端、await 客户端回包（P1 网络层）。
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
            Concede            // 认输
        }

        /// <summary>请求唯一标识，用于服务端将回包与挂起请求匹配（correlation id）。</summary>
        public string RequestId;
        public int PlayerId;
        public Kind RequestKind;
        public string Prompt;
        public string SourceUid;                 // 发起方卡 UID
        public List<string> OptionUids = new List<string>(); // 候选（目标/触发 UID 列表）
        public int PayAmount;                    // PayOrTap 用
    }

    public class ChoiceResult
    {
        /// <summary>对应请求的 RequestId，用于服务端将回包与挂起请求匹配（correlation id）。</summary>
        public string RequestId;
        public string SelectedUid;   // 选中的目标/触发 UID（SelectTarget/Order 用）
        public bool Pay;             // PayOrTap：true=支付，false=横置
        public bool Cancelled;       // 玩家取消/超时
    }
}
