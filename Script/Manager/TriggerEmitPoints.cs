using Godot;
using Script.Data.Effects;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 触发广播薄封装层（D39）：集中所有"游戏行为 → 广播触发事件"的 Emit 点，
    /// 避免出现触发行为散落在各 Manager 内、难以检索与统一维护的问题。
    ///
    /// 分层约定：
    ///   - 触发动作（Emit）：统一走本文件，由对应游戏行为方调用；
    ///   - 触发收集/分发/时机映射/条件判定/入栈：仍集中在 TriggerRegistry.cs。
    ///
    /// 新增触发广播点时，优先在此追加方法，不要在业务代码里直接 new TriggerContext + Emit。
    /// 【MVC 收口】原 static class → 实例：构造注入 GameEventBus，由 Composition Root 装配，
    /// 各 Manager（ZoneManager/CardManager/CombatManager/TurnManager）经 SetTriggerEmitPoints 持有实例。
    /// </summary>
    public class TriggerEmitPoints
    {
        private readonly GameEventBus _bus;

        public TriggerEmitPoints(GameEventBus bus)
        {
            _bus = bus;
        }

        /// <summary>
        /// 进场触发（EnterTrigger）：卡从非场地区进入争夺区、或暗藏者现身时广播。
        /// 对应 TriggerRegistry.EventToTiming 中 EnterTrigger → EnterTrigger 的映射。
        /// </summary>
        public void EnterTrigger(CardInstance card, int controllerId, string areaDetailId, CardInstance attachedTo = null)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.EnterTrigger, new TriggerContext
            {
                SourceCard = card,
                ContextCard = attachedTo,
                TargetAreaId = areaDetailId ?? "",
                TriggeringPlayerId = controllerId,
            });
        }

        /// <summary>
        /// 现身触发（UncoverTrigger）：暗藏者翻面揭示（不移动地区）时广播。
        /// 对应 TriggerRegistry.EventToTiming 中 UncoverTrigger → UncoverTrigger 的映射。
        /// </summary>
        public void UncoverTrigger(CardInstance card, int controllerId, string areaDetailId)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.UncoverTrigger, new TriggerContext
            {
                SourceCard = card,
                ContextCard = null,
                TargetAreaId = areaDetailId ?? "",
                TriggeringPlayerId = controllerId,
            });
        }

        /// <summary>
        /// 战斗对抗获胜触发（OnCombatWon）：战斗(Strength 类型)对抗结算分出胜者后广播。
        /// 对应 TriggerRegistry.EventToTiming 中 OnCombatWon → OnCombatWon 的映射。
        /// 按 D1 约定：此处不填 SourceCard（由 TriggerRegistry 收集时按"在场威名卡"各自注入 source）；
        /// 仅携带胜者玩家(TriggeringPlayerId)与战斗地区(TargetAreaId)，供威名类效果条件 CombatWinnerIsSelf 判定。
        /// </summary>
        public void OnCombatWon(int winnerPlayerId, string areaUid)
        {
            // D2：单次战斗对抗维度去重——每次 Emit 前清空威名放置去重表，
            // 保证同一控制者多个威名在同一地区只放置一个势力标志。
            Script.Manager.EffectEngine.Instance.ResetCombatWonDedup();

            _bus.Emit(GameEvent.OnCombatWon, new TriggerContext
            {
                SourceCard = null,
                ContextCard = null,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = winnerPlayerId,
            });
        }

        /// <summary>
        /// 回合开始/准备步骤触发（OnTurnStarted）：准备阶段为双方共有步骤，
        /// 不归属单一回合玩家，故 TriggeringPlayerId 置 NoPlayer(-1)，
        /// 由每张持续/触发卡的 EffectData 按各自 source.ControllerId 自行判定"对手"。
        /// 对应 TriggerRegistry.EventToTiming 中 OnTurnStarted → OnTurnStarted 的映射。
        /// </summary>
        public void OnTurnStarted()
        {
            _bus.Emit(GameEvent.OnTurnStarted, new TriggerContext
            {
                SourceCard = null,
                ContextCard = null,
                TargetAreaId = null,
                TriggeringPlayerId = TriggerContext.NoPlayer,
            });
        }
    }
}
