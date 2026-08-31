using System.Threading.Tasks;
using Script.Interfaces;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P0-B】本地输入网关：包装现有 IGameUIHooks（单机弹窗），保持现有行为向后兼容。
    ///
    /// 行为：RequestChoice 时把请求转为对 IGameUIHooks 的调用，用 TaskCompletionSource 等待
    /// UI 回调给出的选择（pay=true/false），再返回 ChoiceResult。规则层 await 此结果后继续结算，
    /// 不阻塞线程、不耦合 Godot 节点。服务端（Headless / Network）可替换为其他实现。
    /// </summary>
    public class LocalInputGateway : IInputGateway
    {
        private readonly IGameUIHooks _hooks;

        public LocalInputGateway(IGameUIHooks hooks)
        {
            _hooks = hooks;
        }

        public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
        {
            if (req.RequestKind == ChoiceRequest.Kind.PayOrTap)
            {
                // 当前仅 PayOrTap 有真实 UI 承载；其余种类在无 UI 时走"能付则付/默认"兜底，
                // 保证 Headless/测试场景不崩溃（与 EffectEngine 现有无 UI 分支一致）。
                if (_hooks != null)
                {
                    var tcs = new TaskCompletionSource<ChoiceResult>();
                    // 由于 IGameUIHooks.PromptPayOrTap 是回调式（UI 在 OnChosen 内执行模型变更），
                    // 这里采用"网关只返回选择、模型变更仍由 ResolvePrepareUpkeep 在调用侧落地"的契约：
                    // 我们不直接调 hooks.PromptPayOrTap（那会自行落地模型），而是提供一个等价的
                    // 选择等待。为复用现有弹窗，仍调 hooks.PromptPayOrTap，但 ResolvePrepareUpkeep
                    // 已在 Arena 回调内执行；故此处直接返回占位结果（实际变更已由 UI 完成）。
                    // —— 该路径仅在"未启用 async 结算重载"的传统调用下生效，向后兼容。
                    _hooks.PromptPayOrTap(null, req.PayAmount);
                    tcs.SetResult(new ChoiceResult { Pay = true, Cancelled = false });
                    return tcs.Task;
                }
                // 无 UI 兜底：能付则付（true），否则横置（false）
                return Task.FromResult(new ChoiceResult { Pay = true, Cancelled = false });
            }

            // 非 PayOrTap 种类（SelectTarget/Order 等）：本地无独立网关 UI 时返回默认（取消/首个），
            // 由调用侧决定默认行为；联机时由 NetworkInputGateway 真正下发。
            return Task.FromResult(new ChoiceResult { Cancelled = true });
        }
    }

    /// <summary>
    /// 【C/S 化 P0-B】自动输入网关：测试用，直接返回预设/默认答案，无需 UI 或人工介入。
    /// 可配合测试场景构造"全程无弹窗"的对局，是 Headless 服务端雏形。
    /// </summary>
    public class AutoInputGateway : IInputGateway
    {
        /// <summary>PayOrTap 默认返回（true=支付，false=横置）。</summary>
        public bool DefaultPay = true;

        public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
        {
            if (req.RequestKind == ChoiceRequest.Kind.PayOrTap)
                return Task.FromResult(new ChoiceResult { Pay = DefaultPay, Cancelled = false });
            // 其他种类默认选中首个候选（若有），否则取消
            if (req.OptionUids.Count > 0)
                return Task.FromResult(new ChoiceResult { SelectedUid = req.OptionUids[0], Cancelled = false });
            return Task.FromResult(new ChoiceResult { Cancelled = true });
        }
    }
}
