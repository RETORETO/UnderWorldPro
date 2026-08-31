using Godot;
using Script.Data.Effects;

namespace Script.Manager
{
    /// <summary>
    /// 触发领域事件总线（实例，由 Composition Root 装配）。
    /// 业务点只负责 Emit(GameEvent, TriggerContext)，不关心谁订阅；
    /// 卡牌的 EffectData 声明"响应哪个时机"，由 TriggerRegistry 在事件到达时统一匹配并接入既有触发流程。
    /// 取代原 TurnManager 中分散的 CollectXxx 调用，新增触发时机对核心逻辑零侵入。
    /// 参见 触发系统_监听总线vs轮询_评估与改造方案.md。
    /// 【MVC 收口】原 static class → 实例：消除进程级全局可变状态，Dispatch 由构造注入，
    /// 实例随 Composition Root（GameInitializer）创建/释放，跨局不残留。
    /// </summary>
    public class GameEventBus
    {
        /// <summary>触发分发器：由 TriggerRegistry 在装配时注入（实际收集 + 调用 TurnManager.RunTriggerPhase）。</summary>
        private System.Action<GameEvent, TriggerContext> _dispatch;

        public GameEventBus()
        {
        }

        /// <summary>注入触发分发器（通常指向 TriggerRegistry.HandleEvent）。</summary>
        public void SetDispatch(System.Action<GameEvent, TriggerContext> dispatch)
        {
            _dispatch = dispatch;
        }

        /// <summary>业务点广播一个触发事件。若未初始化 Dispatch，仅打日志不崩溃（便于未接入时机的安全扩展）。</summary>
        public void Emit(GameEvent evt, TriggerContext ctx = null)
        {
            if (_dispatch == null)
            {
                GD.Print($"[GameEventBus] ⚠ 未初始化 Dispatch，事件 {evt} 被忽略（安全降级）");
                return;
            }
            _dispatch(evt, ctx ?? new TriggerContext());
        }
    }
}
