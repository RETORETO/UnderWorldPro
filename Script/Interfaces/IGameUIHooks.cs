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
    }
}
