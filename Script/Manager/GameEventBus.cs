using Godot;
using Script.Data.Effects;

namespace Script.Manager
{
    /// <summary>
    /// 触发领域事件总线（单例）。
    /// 业务点只负责 Emit(GameEvent, TriggerContext)，不关心谁订阅；
    /// 卡牌的 EffectData 声明"响应哪个时机"，由 TriggerRegistry 在事件到达时统一匹配并接入既有触发流程。
    /// 取代原 TurnManager 中分散的 CollectXxx 调用，新增触发时机对核心逻辑零侵入。
    /// 参见 触发系统_监听总线vs轮询_评估与改造方案.md。
    /// </summary>
    public static class GameEventBus
    {
        /// <summary>触发分发器：由 TriggerRegistry 在初始化时注入（实际收集 + 调用 TurnManager.RunTriggerPhase）。</summary>
        public static System.Action<GameEvent, TriggerContext> Dispatch { get; set; }

        /// <summary>业务点广播一个触发事件。若未初始化 Dispatch，仅打日志不崩溃（便于未接入时机的安全扩展）。</summary>
        public static void Emit(GameEvent evt, TriggerContext ctx = null)
        {
            if (Dispatch == null)
            {
                GD.Print($"[GameEventBus] ⚠ 未初始化 Dispatch，事件 {evt} 被忽略（安全降级）");
                return;
            }
            Dispatch(evt, ctx ?? new TriggerContext());
        }
    }
}
