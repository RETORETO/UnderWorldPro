using Godot;
using Script.Data.Effects;
using Script.Enums;
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

        /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
        public Script.Manager.EffectEngine EffectEngineRef { get; set; }

        /// <summary>未显式注入时回退静态 Instance（向后兼容）。</summary>
        private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;

        public TriggerEmitPoints(GameEventBus bus)
        {
            _bus = bus;
        }

        /// <summary>
        /// 进场触发（EnterTrigger）：卡从非场地区进入争夺区、或暗藏者现身时广播。
        /// 对应 TriggerRegistry.EventToTiming 中 EnterTrigger → EnterTrigger 的映射。
        /// </summary>
        /// <param name="zoneAtTrigger">触发时来源卡所在区域：争夺区进场=AreaZone（默认），附属进场=AttachedZone。</param>
        public void EnterTrigger(CardInstance card, int controllerId, string areaDetailId, CardInstance attachedTo = null,
            CardZone zoneAtTrigger = CardZone.AreaZone)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.EnterTrigger, new TriggerContext
            {
                SourceCard = card,
                ContextCard = attachedTo,
                TargetAreaId = areaDetailId ?? "",
                TriggeringPlayerId = controllerId,
                // 【触发语义】进场＝"触发时来源在该区"（进入的地区/附属区）。即便该卡因持续效果**进场即死**、
                // 窗口打开时已在墓地，进场触发也必须照常收集与结算。
                SourceZoneAtTrigger = zoneAtTrigger,
                SourceAreaAtTrigger = areaDetailId ?? "",
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
                // 【触发语义】现身时来源在争夺区（同进场）。
                SourceZoneAtTrigger = CardZone.AreaZone,
                SourceAreaAtTrigger = areaDetailId ?? "",
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
            EE.ResetCombatWonDedup();

            _bus.Emit(GameEvent.OnCombatWon, new TriggerContext
            {
                SourceCard = null,
                ContextCard = null,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = winnerPlayerId,
            });
        }

        /// <summary>
        /// 【P3·调查对抗获胜】触发（OnInvestigationWon）：**调查对抗（Investigation）结算分出胜者后**广播。
        /// 对应 `TriggerRegistry.EventToTiming` 中 `OnInvestigationWon → OnInvestigationWon` 的映射。
        /// 与 `OnCombatWon`（只覆盖战斗对抗）并列；**平局 / 0:0 不 Broadcast**（无胜者语义，由调用方把关）。
        /// 按 D1 约定：此处不填 `SourceCard`（由 `TriggerRegistry` 收集时按"在场卡"各自注入 source）；
        /// 仅携带胜者玩家(TriggeringPlayerId)与对抗地区(TargetAreaId)，供 `WinnerIsSelfInSameArea` 判定。
        /// </summary>
        public void OnInvestigationWon(int winnerPlayerId, string areaUid)
        {
            _bus.Emit(GameEvent.OnInvestigationWon, new TriggerContext
            {
                SourceCard = null,
                ContextCard = null,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = winnerPlayerId,
            });
        }

        /// <summary>
        /// 【P3·"被潜伏/翻面时"】触发（OnLatent）：本卡**翻面为暗藏者**时广播（`CardInstance.SetCovert(true)`）。
        /// 反方向（暗藏→正面）走既有 `UncoverTrigger`，两者互不重叠；由装配层订阅
        /// `CardManager.OnCardStateChanged` 的 `CovertChanged` 后按 `IsCovert == true` 调用本方法。
        /// 触发语义＝**自身**：只有被潜伏的那张卡自己的 `OnLatent` 效果会被收集（与进场/现身同款）。
        /// 覆盖 JC054 蒙古死亡蠕虫「潜伏强制触发：对本地区其他角色各 1 点伤害」。
        /// </summary>
        public void OnLatent(CardInstance card, int controllerId, string areaDetailId)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.OnLatent, new TriggerContext
            {
                SourceCard = card,
                ContextCard = null,
                TargetAreaId = areaDetailId ?? "",
                TriggeringPlayerId = controllerId,
                SourceZoneAtTrigger = card.CurrentZone,
                SourceAreaAtTrigger = areaDetailId ?? "",
            });
        }

        /// <summary>
        /// 【P3】势力对抗/对抗阶段结束触发（OnPowerCombatEnded）：**一个地区的所有对抗结束后**广播（含平局）。
        /// 供【声望】响应：条件谓词 `RenownMajority` 会按该地区双方"声望角色"数量比较，
        /// 由较多的一方在该地区放置一个势力标记（多个声望角色也只放一个，去重与威名同款）。
        /// 与 `OnCombatWon` 的区别：本时机不要求"分出胜者"，`TriggeringPlayerId` 传 0（无胜者语义）。
        ///
        /// 【2026-09 用户口径·声望改触发】触发点＝`TurnManager` 在"该地区三种对抗全部结算完毕"
        /// （含 JC067 追加的那次势力对抗之后）时调用一次；并且**同一地区每回合只广播一次**
        /// （用户口径："不管势力对抗进行几次（JC067效果多次战斗对抗）声望都只触发一次"）——
        /// 本方法自带幂等守卫，任何重复调用都会被吞掉（不依赖调用方时序）。
        /// </summary>
        public void PowerCombatEnded(string areaUid)
        {
            if (string.IsNullOrEmpty(areaUid)) return;
            // 与威名同款：进入本次"势力对抗"维度前清空放置去重表
            // ⇒ **一次势力对抗内**同一方多个声望角色也只放一个标志（用户 2026-09 口径）。
            EE.ResetCombatWonDedup();

            _bus.Emit(GameEvent.OnPowerCombatEnded, new TriggerContext
            {
                SourceCard = null,
                ContextCard = null,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = 0,   // 无胜者语义：声望不依赖对抗胜负
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

        // ==================== P1：补齐"已登记但无发点"的时机 ====================
        // 以下 6 个事件在 TriggerRegistry.EventToTiming 里早已登记，但历史上没有任何业务点 Emit，
        // 导致"死亡触发/赢取触发/抓牌触发/标志放置/场上移动/离场触发"类卡写了也不会触发。
        // 触点选择原则：放在**状态已改变之后、后续连带处理之前**，使触发看到的世界与规则描述一致。

        /// <summary>
        /// 死亡触发（OnCardDied）：**死亡行为发生时**广播——由 `CardManager.ResolveDeath` 在卡**离场之前**调用
        /// （伤害致死 / 消灭 / 牺牲三种原因走同一入口）。
        ///
        /// 【触发语义·关键】
        ///  · 触发条件"卡在场上（争夺区/附属区）"在广播这一刻天然成立 → 死亡触发的生效区判定**不需要**
        ///    任何"墓地区"声明，也不看卡此刻的所在区；
        ///  · **死亡与去向无关**：`zoneAtDeath` / `areaAtDeath` 只把"死亡那一刻的区域/地区"钉住，供"本地区"基准使用。
        ///    即使将来有替代效果让卡**不进入墓地**（回牌库/回手牌等），死亡触发照样成立——
        ///    绝不能用"卡在墓地"当死亡判据（死亡是**行为**，不是"从场上进墓地"这个动作）；
        ///  · 入堆叠与结算阶段**没有地区要求**（触发只看条件是否满足过）。
        /// </summary>
        /// <param name="zoneAtDeath">死亡那一刻来源卡所在的区域（一般为争夺区/附属区）。</param>
        /// <param name="areaAtDeath">死亡那一刻来源卡所在的地区 UID（"本地区"基准）。</param>
        public void OnCardDied(CardInstance card, int controllerId, CardZone zoneAtDeath = CardZone.AreaZone,
            string areaAtDeath = null)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.OnCardDied, new TriggerContext
            {
                SourceCard = card,
                ContextCard = card,
                // TargetAreaId 承载"事件发生的地区"：死亡触发下＝死亡时所处地区，
                // 亦供 PlaceToken 等"落点取来源地区"的动词兜底（死亡卡自身的 ZoneDetailId 已为 null）。
                TargetAreaId = areaAtDeath ?? "",
                TriggeringPlayerId = controllerId,
                SourceZoneAtTrigger = zoneAtDeath,
                SourceAreaAtTrigger = areaAtDeath ?? "",
            });
        }

        /// <summary>
        /// 地区赢取触发（OnAreaWon）：地区结算"置入计分区"之后、把本地区卡送牌库底**之前**广播。
        /// 触点选在步骤2与步骤3之间的原因：赢取触发若需引用"本地区"（如"当你赢取本地区时…"），
        /// 此时地区仍注册在原 UID 下、卡也仍在区内，条件判定可正常取到；
        /// 注意触发是**入堆叠后异步结算**的，故"改变本地区卡去向"类效果仍需替代效果（K3）才能真正改变结果。
        /// </summary>
        public void OnAreaWon(string areaUid, int winnerId)
        {
            _bus.Emit(GameEvent.OnAreaWon, new TriggerContext
            {
                SourceCard = null,      // 学 OnCombatWon：来源由收集阶段按"在场卡"各自注入
                ContextCard = null,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = winnerId,
                // 【触发语义】此刻本地区的卡都还在该地区（赢取结算的"送牌库底"还没跑），
                // 但入堆叠/快照可能发生在其后 → 用本字段把"触发时所在地区"钉住，保住"本地区"基准。
                SourceAreaAtTrigger = areaUid ?? "",
            });
        }

        /// <summary>
        /// 抓牌触发（OnDraw）：**每"一批抓牌"只广播一次**（用户 2026-09 口径，替代原来的"逐张广播"）。
        /// ContextCard = 该批**第一张**抓到的牌（非空，供条件判定有个确定的对象）；TriggeringPlayerId = 抓牌玩家
        /// （供"当你抓牌时"条件 `TriggerBySelf` 判定）；ExtraInt = **本批抓到的张数**
        /// （供 JC080「X＝此次抓牌的数量」读取）。
        ///
        /// 批次边界＝**一次抽牌调用**（`TurnManager.DrawCardsForPlayer` / `DrawFromBottomForPlayer` 的一次调用，
        /// 例如"抓两张牌"＝一批 X=2，而不是两张各触发一次 X=1）。
        /// </summary>
        public void OnDraw(CardInstance drawnCard, int playerId, int batchSize = 1)
        {
            if (batchSize <= 0) return;   // 一张都没抓到（牌库耗尽判负）→ 不广播
            _bus.Emit(GameEvent.OnDraw, new TriggerContext
            {
                SourceCard = null,
                ContextCard = drawnCard,
                TargetAreaId = null,
                TriggeringPlayerId = playerId,
                ExtraInt = batchSize,
            });
        }

        /// <summary>
        /// 弃牌触发（OnDiscard）：**一次弃牌动作只广播一次**（用户 2026-09 口径，与 `OnDraw` 同口径；
        /// 唯一出口 `TurnManager.DiscardCards`，其调用方已把"一批"收集齐后一次性弃掉）。
        /// ContextCard = 本批**第一张**被弃的牌；TriggeringPlayerId = **弃牌者**（供"每当敌方玩家弃牌时"判定）；
        /// ExtraInt = 本次弃牌动作的**总张数**（供"遏制X（X = 此次弃牌的数量）"读取）。
        /// 语义边界：回手/回牌库/结算入墓**都不是**弃牌，不广播。
        /// </summary>
        public void OnDiscard(CardInstance card, int discarderId, int batchSize = 1)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.OnDiscard, new TriggerContext
            {
                SourceCard = card,
                ContextCard = card,
                TargetAreaId = "",
                TriggeringPlayerId = discarderId,
                ExtraInt = batchSize,
                // 弃牌时该牌在手牌；入堆叠后不再看区（触发语义规则 2）
                SourceZoneAtTrigger = CardZone.Hand,
                SourceAreaAtTrigger = "",
            });
        }

        /// <summary>
        /// 封印触发（OnSealed）：**封印动作发生时**广播（唯一出口 `TurnManager.SealCard`）。
        /// SourceCard/ContextCard = 封印卡自身；ExtraString = 宿主卡 UID（空=封印于地区）；TargetAreaId = 宿主所在地区。
        /// 语义边界：被封印卡"离场/地区赢取送库底"导致的封印解除**不是**本时机（那走 OnCardLeft/OnMove）。
        /// </summary>
        public void OnSealed(CardInstance sealingCard, CardInstance host, int sealerId, string areaUid)
        {
            if (sealingCard == null) return;
            _bus.Emit(GameEvent.OnSealed, new TriggerContext
            {
                SourceCard = sealingCard,
                ContextCard = sealingCard,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = sealerId,
                ExtraString = host?.UID ?? "",
                // 封印发生后封印卡已在封印区；触发条件在"封印动作成立"时满足（入堆叠后不再看区）
                SourceZoneAtTrigger = CardZone.SealedZone,
                SourceAreaAtTrigger = areaUid ?? "",
            });
        }

        /// <summary>
        /// 标志/指示物放置触发（OnMarkerPlaced）。
        /// ExtraString = 标志类型名（如"势力标志"/"时间标志"/"锁定标志"），ExtraInt = 本次数量，
        /// TriggeringPlayerId = 放置者（势力标志=获益方），TargetAreaId = 放置地区（卡上标志为空）。
        /// </summary>
        public void OnMarkerPlaced(string markerType, int amount, int playerId, string areaUid, CardInstance host = null)
        {
            _bus.Emit(GameEvent.OnMarkerPlaced, new TriggerContext
            {
                SourceCard = host,
                ContextCard = host,
                TargetAreaId = areaUid ?? "",
                TriggeringPlayerId = playerId,
                ExtraString = markerType ?? "",
                ExtraInt = amount,
            });
        }

        /// <summary>
        /// 场上移动触发（OnMove）：卡在场上移动（含**争夺区→争夺区**的机动/夺控）后广播。
        /// 与 `EnterTrigger` 的区别：EnterTrigger 只在"非场地区→争夺区"时发；OnMove 每次位置变更都发，
        /// 供"当此角色进入一个地区时"（机动也算）类效果使用。
        /// </summary>
        public void OnMove(CardInstance card, int controllerId, string toAreaDetailId, string fromAreaDetailId)
        {
            if (card == null) return;
            _bus.Emit(GameEvent.OnMove, new TriggerContext
            {
                SourceCard = card,
                ContextCard = card,
                TargetAreaId = toAreaDetailId ?? "",
                TriggeringPlayerId = controllerId,
                ExtraString = fromAreaDetailId ?? "",
                // 【触发语义】移动后来源在争夺区（新地区）——若移动过程中卡被效果消灭，触发仍应按"新地区"成立。
                SourceZoneAtTrigger = CardZone.AreaZone,
                SourceAreaAtTrigger = toAreaDetailId ?? "",
            });
        }

        /// <summary>
        /// 离场触发（OnCardLeft）：卡**非死亡**地离开场上（回手/回牌库底/进计分区等）后广播。
        /// ExtraString = 离开时所去的区域名（如 "Hand"/"Deck"/"ScoreZone"）。
        /// 【触发语义】发点在区域字段被改写**之前**：来源此刻仍在场上，"本地区"= 离开前所在地区。
        /// </summary>
        public void OnCardLeft(CardInstance card, int controllerId, string fromZoneName, string toZoneName)
        {
            if (card == null) return;
            var zoneAtTrigger = card.CurrentZone;
            _bus.Emit(GameEvent.OnCardLeft, new TriggerContext
            {
                SourceCard = card,
                ContextCard = card,
                TargetAreaId = card.ZoneDetailId ?? "",
                TriggeringPlayerId = controllerId,
                ExtraString = fromZoneName ?? "",
                SourceZoneAtTrigger = zoneAtTrigger,
                SourceAreaAtTrigger = card.ZoneDetailId ?? "",
            });
        }
    }
}
