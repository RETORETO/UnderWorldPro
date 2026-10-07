using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC124 尼古拉斯·勒梅 效果（角色）。
    /// 卡文：**行动3：在目标角色上放置一个哲人石标志。**
    ///      **强制触发：当具有哲人石标志的角色死亡时，将其从墓地移回场上，并由其拥有者操控。
    ///      该能力在尼古拉斯·勒梅离场后仍然生效。**
    ///
    /// ①【行动3】`CharacterActivated`：费用 3 + 新动词 `PlaceCounter`（卡文只写"目标角色"故不含暗藏者）。
    ///
    /// ②【构筑级全局触发】**用户 2026-09 口径**："JC124 需要制作一个有效果的指示物，或者说**一个全局触发效果**，
    ///   **只要 JC124 一开始加入玩家卡组就能触发这个效果**"（"两种方法都可以只要能以合理形式满足 JC124 效果使用即可"），
    ///   并明确两点：**加入多个 JC124 也只能有一个效果生效**；**效果加入堆叠用 JC124 的卡图**。
    ///   落地＝`EffectData.DeckLevelRegistration = true`（新字段）＋ `Script/Manager/DeckLevelTriggerRule`：
    ///    · **登记**：`DeckManager` 装载卡组时扫描该玩家卡组，只要命中"带构筑级触发声明的牌"就**按玩家登记一次**
    ///      （先到先得 ⇒ **多张同名只生效一份**），登记记录**与该卡此后身处何处无关**
    ///      ⇒ 卡文"该能力在尼古拉斯·勒梅离场后仍然生效"天然成立（不依赖持续效果登记，不会被 `UnregisterPersistent` 撤销）；
    ///    · **收集**：事件发生时（`OnCardDied`）由 `DeckLevelTriggerRule.Collect` 按"已登记玩家"合成触发实例，
    ///      **来源卡＝该玩家卡组里的那张 JC124 实例** ⇒ 堆叠条目**显示 JC124 的卡图**；
    ///    · **不再走常规场上扫描**（`EffectData.DeckLevelRegistration` 在 `TriggerRegistry` 里被跳过），
    ///      否则 JC124 在场时会与构筑级登记**重复收集**（同一张死亡角色被回场两次）。
    ///
    /// ③「**具有哲人石标志的角色**死亡时，将其从墓地移回场上，并由其拥有者操控」：
    ///    · 条件谓词 **`DiedWithCounter`**（`Param="哲人石标志"`）——读**死亡瞬间的计数器快照**
    ///      （`CardInstance.CountersAtDeath`，由 `CardManager.ResolveDeath` 在广播死亡事件**之前**抓取）：
    ///      死亡触发在"卡仍在场上时"广播、但**结算发生在卡已进墓地之后**，而进墓地会 `ResetToBaseState()` 清空计数器，
    ///      故必须有快照，否则结算阶段条件复检必然不成立（该死亡角色的标志已消失）；
    ///    · 动作 `MoveToZone{"目标","争夺区","正面","拥有者"}`：把**触发上下文卡**（刚死亡的角色，＝条目目标）
    ///      从墓地移回**它死亡时所在的地区**（`StackEntry.TargetAreaId` ＝ 触发条目携带的"触发时所在地区"），
    ///      并把控制者交回**拥有者**（卡文"由其拥有者操控"；若该角色此前被夺控，则归还给拥有者）；
    ///    · **强制触发**（卡文"强制触发"）→ `Forced = true`（触发排序窗口不提供"放弃"）。
    /// </summary>
    public static class JC124_nigulasileimei_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC124_nigulasileimei", Build);

        /// <summary>构筑级全局触发的 EffectId（供规则/测试引用）。</summary>
        public const string DeckLevelEffectId = "JC124_2_ReturnPhilosopherStoneDeadToField";

        /// <summary>哲人石标志名（卡文用词）。</summary>
        public const string PhilosopherStoneCounter = "哲人石标志";

        private static void Build(PlayerCardData data)
        {
            // ==================== ① 行动3：在目标角色上放置一个哲人石标志 ====================
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC124_1_PlacePhilosopherStone",
                Timing = EffectTiming.CharacterActivated,
                // 【2026-09 整体复查·修复】卡文"**行动3**"⇒ 必须显式声明行动窗口。
                // 旧实现漏了 `ActivateWindow`（默认空串 ⇒ `ActivateEligibility` 的"行动"判定被跳过）
                // ⇒ 该能力被当成"只需优先权"（响应窗口也能发动），与卡文的"行动"不符。
                ActivateWindow = "行动",
                Cost = 3,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    // 【2026-09 整体复查·修复】卡文"目标角色"未排除自身 ⇒ 允许取自身（JC004 口径；在自身放哲人石标志是合法玩法）
                    CanTargetSelf = true,
                    IncludeCovert = false,   // 卡文只写"目标角色"
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceCounter,
                        Args = new Array<string> { PhilosopherStoneCounter, "1", "目标" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "行动3：在目标角色上放置一个哲人石标志。",
            });

            // ==================== ② 构筑级全局触发：具哲人石标志的角色死亡 → 移回场上（由拥有者操控） ====================
            data.EffectDataList.Add(new EffectData
            {
                EffectId = DeckLevelEffectId,
                Timing = EffectTiming.OnCardDied,
                Forced = true,                       // 卡文"强制触发"
                DeckLevelRegistration = true,        // 【构筑级】只要本卡在开局卡组内即登记（每玩家一次）
                // **无需玩家选目标**：被回场的"目标"＝触发上下文卡（刚死亡的那个角色，收集时已写入条目目标），
                // 故必须显式声明 `NeedsSelection=false`，否则触发会被判成"需要选目标"而在入堆叠前被跳过。
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Condition = new EffectCondition
                {
                    Predicate = "DiedWithCounter",
                    Param = PhilosopherStoneCounter,
                },
                Actions = new Array<EffectAction>
                {
                    // 回场：对象＝条目目标（＝触发上下文卡＝刚死亡的角色）；落点＝触发时所在地区（死亡地）；
                    // 第 4 参数 "拥有者" ＝ 控制者交回拥有者（卡文"由其拥有者操控"）。
                    new EffectAction
                    {
                        Verb = ActionVerb.MoveToZone,
                        Args = new Array<string> { "目标", "争夺区", "正面", "拥有者" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "只要尼古拉斯·勒梅在开局卡组内，本触发即登记（多张同名只生效一份），"
                        + "且在其离场后仍然生效：具哲人石标志的角色死亡时，从墓地移回其死亡时所在地区，"
                        + "并由其拥有者操控。",
            });
        }
    }
}
