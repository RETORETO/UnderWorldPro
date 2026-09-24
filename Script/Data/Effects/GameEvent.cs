namespace Script.Data.Effects
{
    /// <summary>
    /// 触发领域事件（监听总线事件类型）。
    /// 与 EffectTiming（数据声明用时机）解耦：一个 GameEvent 可映射到某一 EffectTiming，
    /// 由 TriggerRegistry.MapEventToTiming 维护映射。新增触发时机时，在此枚举追加值即可，
    /// 控制器与 TriggerRegistry 核心逻辑无需改动（参见 触发系统_监听总线vs轮询_评估与改造方案.md）。
    /// </summary>
    public enum GameEvent
    {
        /// <summary>任意卡被打出（反应触发上下文）。映射到 EffectTiming.OnCardPlayed。</summary>
        OnCardPlayed,

        /// <summary>对抗前优先权窗口（机动触发）。映射到 EffectTiming.ManeuverTrigger。</summary>
        ManeuverTrigger,

        // ===== 以下为后续可扩展时机（总线已就绪，待对应业务点 Emit 即可接入） =====

        /// <summary>卡牌进入争夺区/场上。映射到 EffectTiming.EnterTrigger。</summary>
        EnterTrigger,

        /// <summary>卡牌现身（暗藏→公开）。映射到 EffectTiming.UncoverTrigger。</summary>
        UncoverTrigger,

        /// <summary>卡牌死亡/离场。后续可新增 EffectTiming.OnCardDied 并映射。</summary>
        OnCardDied,

        /// <summary>某地区被赢取。后续可新增 EffectTiming.OnAreaWon 并映射。</summary>
        OnAreaWon,

        /// <summary>回合开始。后续可新增 EffectTiming.OnTurnStarted 并映射。</summary>
        OnTurnStarted,

        /// <summary>回合结束。映射到 EffectTiming.OnTurnEnded（已启用映射）。</summary>
        OnTurnEnded,

        // ============================================================
        // ⚠️ 下方为 2026-08-05 总线补全批次新增事件，与 EffectTiming 新增时机一一对应。
        // 命名原则：事件名（GameEvent）与时机名（EffectTiming）尽量同名，降低映射维护成本。
        // 每个事件需在 TriggerRegistry.EventToTiming 中登记映射，未登记的事件 Emit 时不会触发任何卡。
        // ============================================================

        /// <summary>抓牌（抽牌）事件发生。映射到 EffectTiming.OnDraw。
        /// 触发语义：本角色/玩家抓牌动作完成时广播，供"当你抓牌时"类旁观者卡响应。</summary>
        OnDraw,

        /// <summary>弃牌（手牌弃置）事件发生。映射到 EffectTiming.OnDiscard。
        /// 触发语义：卡牌从手牌区进入弃牌堆/墓地时广播，供"手牌触发/当你弃牌时"类卡响应。</summary>
        OnDiscard,

        /// <summary>指示物放置事件发生。映射到 EffectTiming.OnMarkerPlaced。
        /// 触发语义：任意指示物被放置到卡/区域时广播，供"当指示物被放置"类卡响应。
        /// 高频场景须配合 D-1 熔断与触发链死循环复核。</summary>
        OnMarkerPlaced,

        /// <summary>封印施加事件发生。映射到 EffectTiming.OnSealed。
        /// 触发语义：一张卡被施加封印状态时广播，供"当卡牌被封印"类卡响应（全卡封印语义 ~149 处）。</summary>
        OnSealed,

        /// <summary>场上移动事件发生。映射到 EffectTiming.OnMove。
        /// 触发语义：卡牌在场上区域/位置变更完成时广播，供"当移动时"类卡响应。</summary>
        OnMove,

        /// <summary>终止（反制/Counter）事件发生。映射到 EffectTiming.OnCounter。
        /// 触发语义：一张卡/效果被快速行动终止时广播，供"被终止时"类反应卡响应（关联 T9 反应箭头、D-2）。</summary>
        OnCounter,

        /// <summary>非死亡离场事件发生。映射到 EffectTiming.OnCardLeft。
        /// 触发语义：卡牌因回手/封印移除/移出游戏等非死亡原因离开战场时广播，供"当离开战场时"类卡响应。
        /// 与 OnCardDied 互斥：死亡走 OnCardDied，此处仅收非死亡离场。</summary>
        OnCardLeft,

        /// <summary>战斗对抗获胜事件发生。映射到 EffectTiming.OnCombatWon。
        /// 触发语义：CombatManager.ResolveWinner 结算战斗(Strength 类型)对抗分出胜者后广播，
        /// 供威名类效果（战斗对抗获胜触发）响应。ctx.TriggeringPlayerId=胜者玩家Id，ctx.TargetAreaId=战斗地区UID。
        /// 【后续拓展·B 预留】调查对抗获胜 → OnInvestigationWon；势力对抗(Power)获胜 → OnPowerWon；
        /// 同样携带 TriggeringPlayerId(胜者)+TargetAreaId(地区)，复用 WinnerIsSelf 条件链路。</summary>
        OnCombatWon,

        /// <summary>【P3】势力对抗结束事件发生。映射到 EffectTiming.OnPowerCombatEnded。
        /// 触发语义：**一次势力对抗结束时**广播（**无论胜负、含平局**；与 `OnCombatWon` 不同——后者只在"分出胜者的战斗对抗"后发）。
        /// 供【声望】响应："每个势力对抗结束后，由该地区拥有较多声望角色的一方给该地区添加一个势力标记"。
        /// ctx.TargetAreaId = 进行该次势力对抗的地区；ctx.TriggeringPlayerId = 0（无胜者语义；声望由条件自行比较双方声望角色数）。</summary>
        OnPowerCombatEnded,

        /// <summary>
        /// 【P3·调查对抗获胜】（2026-09 补齐：原为"后续拓展·B 预留"）映射到 `EffectTiming.OnInvestigationWon`。
        /// 触发语义：`CombatManager` 结算**调查对抗（Investigation）**分出胜者后广播（**平局/0:0 不广播**），
        /// 与 `OnCombatWon`（只覆盖战斗对抗 Strength）并列。ctx.TriggeringPlayerId=胜者玩家Id，
        /// ctx.TargetAreaId=该调查对抗所在地区；`SourceCard=null`（由收集阶段按"在场卡"各自注入 source）。
        /// 供 JC072「当你赢得**其所参与**的调查对抗时…」类效果响应（条件 `WinnerIsSelfInSameArea` 正好表达
        /// "本卡控制者获胜 ∧ 本卡就在该地区"）。
        /// </summary>
        OnInvestigationWon,

        /// <summary>
        /// 【P3·"被潜伏/翻面时"】卡牌**翻面为暗藏者**（`CardInstance.SetCovert(true)` 经状态中枢
        /// `CardChangeKind.CovertChanged`）时广播。映射到 `EffectTiming.OnLatent`。
        /// 触发语义：**自身**语义（只有"被潜伏的那张卡"自己的 `OnLatent` 效果会被收集，与进场/现身同款）；
        /// 反方向（暗藏→正面）由既有的 `UncoverTrigger` 承担，本事件**只在 IsCovert 变 true 时**发。
        /// ctx.SourceCard=被潜伏的卡，ctx.TargetAreaId/SourceAreaAtTrigger=其所在地区（供"本地区"类效果）。
        /// 覆盖 JC054「潜伏强制触发：对本地区其他角色各 1 点伤害」。
        /// </summary>
        OnLatent,
    }
}
