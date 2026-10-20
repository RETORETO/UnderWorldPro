using Script.Enums;
using Script.Game.Entities;

namespace Script.Data.Effects
{
    /// <summary>
    /// 入堆叠效果的运行时实例（新系统）——承载"效果型堆叠条目"的来源/目标/标记等运行时状态。
    /// 命名说明：本类同时承载【触发类】（事件被动触发，如 EnterTrigger/UncoverTrigger/OnCombatWon）
    /// 与【激活类】（玩家主动发动，如 CharacterActivated/AttachmentActivated/CouncilActivated）两类效果——
    /// 二者均"效果入堆叠"（StackEntry.IsCard=false、Kind=Trigger），故以"Stack（入堆叠）"而非"Trigger（触发）"命名，
    /// 避免以偏概全。持续/静止效果（Continuous/Static）走 EffectEngine 注册表、不生成本实例；
    /// 事务打出（OnSorceryCast）走 ResolveSorcery 直接结算、也不生成本实例。
    /// 由 TriggerRegistry.HandleEvent（触发）/ EffectEngine.PushActivationToStack（激活）生成，
    /// 经 RunTriggerPhase 排序窗口 + 让过后入堆叠，由 ResolveStackTop 走 EffectEngine.Instance.ResolveEffect 结算。
    /// 旧系统 TriggerEffectData / ICardTriggerBehavior 已迁移删除，本类不再持有旧静态模板。
    /// </summary>
    public class StackEffectInstance
    {
        /// <summary>运行时的唯一标识。</summary>
        public string UID { get; set; }

        /// <summary>触发来源卡实例。</summary>
        public CardInstance Source { get; set; }

        /// <summary>效果归属玩家（仅作元数据；分组弹窗以 ControllerId 为准）。</summary>
        public int OwnerPlayerId { get; set; }

        /// <summary>触发来源卡的控制者（D32 修正：窗口分组/弹窗归属以控制者为准，
        /// 不再按 OwnerPlayerId 分组，确保只有 Controller==P1 的 JC014 才推送给 P1）。</summary>
        public int ControllerId { get; set; }

        /// <summary>新系统（EffectEngine）触发数据引用：本触发由 EffectData 承载，
        /// PushTriggerToStack 据此写入 StackEntry.EffectDataEffect，由 ResolveStackTop 走新系统结算。</summary>
        public Script.Data.Effects.EffectData EffectDataRef { get; set; }

        /// <summary>全局触发序号，用于同时间触发的默认排序。</summary>
        public int TriggerTimestamp { get; set; }

        /// <summary>玩家已选择的场上目标卡 UID。</summary>
        public string SelectedTargetUid { get; set; }

        /// <summary>
        /// 【K16·模式分支·触发版】**触发**类"选择一项"（`EffectData.Modes`）所选模式下标（-1＝无模式/未选）。
        /// 与打出路径口径一致（**入堆叠时选定、入堆叠后不可更改**，见 §7.4b A5）：
        /// 由 `TurnManager.EnsureTargetThenPush*` 在入堆叠前经 `IGameUIHooks.PromptEffectMode`
        /// （联机＝`ChoiceRequest.Kind.SelectEffectMode`；无 UI 时确定性兜底第一个模式）选定，
        /// 再由 `PushTriggerToStack` 写入 `StackEntry.ModeIndex`，结算时只执行该模式的动作。
        /// </summary>
        public int ModeIndex { get; set; } = -1;

        /// <summary>
        /// 【K24·玩家目标】玩家已选择的**目标玩家号**（-1＝未选/无玩家目标）。
        /// 与 `SelectedTargetUid` 并列：触发类效果若以玩家为目标（`TargetSpec.Kind=Player`），
        /// 在触发入堆叠前经"独立选择窗口"选定后填入，`PushTriggerToStack` 写入 `StackEntry.TargetPlayerId`。
        /// </summary>
        public int SelectedPlayerId { get; set; } = -1;

        /// <summary>反应触发的固定上下文卡 UID（如"刚打出的卡"），区别于 SelectedTargetUid（玩家选择的目标）。
        /// 由 CollectTriggeredEffects 在收集时填入（contextCard.UID），结算时作为条件判定的上下文（如 PlayedSorcery）。</summary>
        public string TargetCardUid { get; set; }

        /// <summary>该触发效果当前是否还有合法目标（用于过滤是否显示在排序窗口）。</summary>
        public bool HasValidTarget { get; set; } = true;

        /// <summary>对抗获胜方玩家 ID（"X对抗获胜"类触发：Strength/Investigation/Power 获胜者）。
        /// 由 TriggerRegistry.HandleEvent 收集时从 ctx.TriggeringPlayerId 填入（如 OnCombatWon 威名触发），
        /// 然后 PushTriggerToStack 写入 StackEntry.WinnerId，最终经 ResolveEffect(winnerId) 传给条件 WinnerIsSelf 判定。
        /// 命名规范：对应原始能力名 Strength/Investigation/Power（势力）三种对抗，本字段对三者通用。</summary>
        public int WinnerId { get; set; } = -1;

        /// <summary>触发事件关联的地区 UID（战斗地区/赢取地区；null/空 表示无地区）。
        /// 由 TriggerRegistry.HandleEvent 收集时从 ctx.TargetAreaId 填入（如 OnCombatWon 威名触发），
        /// 然后 PushTriggerToStack 写入 StackEntry.TargetAreaId，最终供条件 WinnerInSameArea 判定
        /// “胜者获胜地区 == 本触发卡所在地区”，从而让跨地区卡在收集阶段即不入栈。</summary>
        public string TargetAreaId { get; set; } = null;

        /// <summary>
        /// 【触发语义】**触发条件满足时，来源卡所在的地区 UID**（null/空=未快照，回退实时 `Source.ZoneDetailId`）。
        ///
        /// 存在意义：触发只看条件是否满足过，不看来源此刻场景。死亡触发（卡已进墓地）与"进场即死"的
        /// 进场触发在窗口打开时，来源卡的 `ZoneDetailId` 已为 null —— 若拿它当"本地区"基准，
        /// 需要选目标的此类触发会因"取不到本地区候选"而整条被丢弃。
        /// 因此由 `TriggerRegistry` 收集时从 `TriggerContext.SourceAreaAtTrigger` 填入，供
        /// `TurnManager.GetLocalZoneTargets`（取候选）与 `CaptureTargetSnapshot`（结算复检基准）使用。
        /// </summary>
        public string SourceAreaAtTrigger { get; set; } = null;

        /// <summary>
        /// 【K1】本次触发事件携带的数量（`TriggerContext.ExtraInt`；-1 = 无数量语义）。
        /// 由 `TriggerRegistry.HandleEvent` 收集时填入，`PushTriggerToStack` 写入 `StackEntry.EventAmount`，
        /// 结算时供动态值表达式 `事件数量` 求值（JC040「遏制X，X＝此次弃牌的数量」）。
        /// </summary>
        public int EventAmount { get; set; } = -1;

        /// <summary>是否需要目标。</summary>
        /// <remarks>
        /// 新系统（EffectDataRef 承载）：
        ///   - 仅当显式 IsAreaTarget（机动/取地区目标）且 EffectData.Target.NeedsSelection==true 时才需要选目标；
        ///   - 普通反应触发（如 XG01 抽牌）即使 EffectData.Target 缺省 NeedsSelection=true，也不应被误判为需要目标而跳过入栈。
        /// </remarks>
        public bool RequiresTarget => IsAreaTarget && EffectDataRef?.Target?.NeedsSelection == true;

        /// <summary>新系统地区目标标记：当机动（ManeuverTrigger）取"目标地区"时置 true，
        /// 驱动 PushTriggerToStack 把 SelectedTargetUid 写入 entry.TargetAreaId，
        /// 并驱动 Arena.RequestTargetSelection 以"地区槽位高亮"模式开选目标窗口。</summary>
        public bool IsAreaTarget { get; set; }

        /// <summary>是否需要选地区目标（新系统地区目标语义），等价于 IsAreaTarget，供 Arena 判定。</summary>
        public bool NeedsAreaTarget => IsAreaTarget;

        public string DisplayName => EffectDataRef?.EffectId ?? "入堆叠效果";

        /// <summary>
        /// 【2026-10-20】可读文本：卡文 `FaqNote` → `"&lt;来源卡名&gt;·效果"`。
        /// 旧实现只取 `FaqNote`，而绝大多数 EffectData 没写 FaqNote ⇒ 触发排序窗/堆叠区**整行空白**
        /// （用户实测"对手发动的某些效果进入堆叠后没有正确显示"）。
        /// </summary>
        public string DisplayText
        {
            get
            {
                string faq = EffectDataRef?.FaqNote;
                if (!string.IsNullOrEmpty(faq)) return faq;
                string name = Source?.EffectiveCardName;
                if (string.IsNullOrEmpty(name)) name = Source?.CouncilTemplate?.CardName;
                if (string.IsNullOrEmpty(name)) name = Source?.PlayerTemplate?.CardName;
                return string.IsNullOrEmpty(name) ? "效果发动" : $"{name}·效果";
            }
        }

        public override string ToString()
            => $"StackEffect[{UID}] {DisplayName} owner={OwnerPlayerId} areaTarget={IsAreaTarget} target={SelectedTargetUid}";
    }
}
