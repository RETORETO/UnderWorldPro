using Script.Game.Entities;
using Script.Data.Effects;
using System.Collections.Generic;

namespace Script.Game.Entities
{
    /// <summary>
    /// 堆叠条目数据模型。卡片进堆叠与效果进堆叠统一用此结构表示。
    /// 规则：堆叠遵循 LIFO（后进先出），列表中末尾为堆叠顶。
    /// </summary>
    public class StackEntry
    {
        /// <summary>操控该条目的玩家（用于响应权）</summary>
        public int OwnerId { get; set; }

        /// <summary>控制该条目的玩家（用于箭头着色 / 控制者语义）。夺控场景与 OwnerId 不同，
        /// 堆叠箭头须按控制者颜色绘制（核心需求 / D31）。未显式设置时默认等于 OwnerId。</summary>
        public int ControllerId { get; set; }

        /// <summary>true=卡片进堆叠（显示 CardUI）；false=效果进堆叠（显示半透明遮罩 + 角标 + 文本）</summary>
        public bool IsCard { get; set; }

        /// <summary>卡片型条目对应的卡实例（效果型可为 null）</summary>
        public CardInstance SourceCard { get; set; }

        /// <summary>效果型条目的文本描述（卡片型可为卡名）</summary>
        public string EffectText { get; set; }

        /// <summary>目标引用列表（用于绘制箭头）。每项保存可定位目标节点的信息。</summary>
        public List<StackTargetRef> Targets { get; set; } = new List<StackTargetRef>();

        // ============================================================
        // 结构化结算数据（数据驱动，便于未来服务器同步）
        // 结算执行方（TurnManager.ResolveEntry）按 ResolveKind 走对应分支。
        // ============================================================

        /// <summary>结算类型。决定 ResolveEntry 如何执行本条目。</summary>
        public ResolveKind Kind { get; set; } = ResolveKind.PlayToArea;

        /// <summary>结算负载：目标地区槽位 id（PlayToArea / Attachment 结附地区时使用）</summary>
        public string TargetAreaId { get; set; }

        /// <summary>
        /// 【K16·模式分支】本条选定的模式序号（`EffectData.Modes` 的下标；-1＝无模式分支）。
        /// 口径（用户 2026-09）：**打出时选定、付费之前**，选定后入堆叠**不可更改**；结算只执行该模式。
        /// </summary>
        public int ModeIndex { get; set; } = -1;

        /// <summary>结算负载：目标卡 UID（Attachment 结附于卡 / 事务指向场上卡时使用，可为空）</summary>
        public string TargetCardUid { get; set; }

        /// <summary>
        /// 【K15·多目标】本条目选定的**全部目标卡 UID**（有序，与效果声明的槽位顺序一致）。
        /// 单目标条目可继续只用 <see cref="TargetCardUid"/>；多目标（`EffectData.TargetSlots`）时本列表为准，
        /// 且 `TargetCardUid` 取列表首项（兼容既有箭头绘制与联机快照）。
        /// </summary>
        public List<string> TargetCardUids { get; set; } = new List<string>();

        /// <summary>
        /// 【K15·多目标】与 <see cref="TargetCardUids"/> **同序**的入栈版本号（D16 丢失判定逐目标比对：
        /// 任一目标版本号变化即判该目标丢失）。
        /// </summary>
        public List<int> TargetVersionIds { get; set; } = new List<int>();

        /// <summary>结算负载：源卡 UID（卡片型固定等于 SourceCard.UID；效果型可空）</summary>
        public string SourceCardUid { get; set; }

        /// <summary>
        /// 【K24·玩家目标】本条目选定的**目标玩家号**（-1＝无玩家目标）。单目标效果用它；
        /// 多目标（含玩家槽）时以 <see cref="TargetPlayerIds"/> 为准，本字段取首个玩家槽的值。
        /// 与卡目标同口径：**入堆叠时锁定**，结算时不再重选。
        /// </summary>
        public int TargetPlayerId { get; set; } = -1;

        /// <summary>
        /// 【K24·玩家目标】本条目选定的**全部玩家槽目标**（按玩家槽对齐；留空槽写 -1）。
        /// 与 <see cref="TargetCardUids"/> 同一"按槽对齐"纪律（见 `MultiTargetSlots.SlotAlignedPlayerIds`）。
        /// </summary>
        public List<int> TargetPlayerIds { get; set; } = new List<int>();

        /// <summary>
        /// 过程记忆：目标在堆叠期间是否"曾丢失"。只置位、不清位（即使目标后来恢复合法也不撤销），
        /// 对应规则"只要发生过丢失即算丢失"。由 TurnManager 订阅 OnCardStateChanged 扫描置位，
        /// 结算时与瞬间兜底 IsTargetLost 一同判定。新建时为 false。
        /// </summary>
        public bool TargetWasLost { get; set; }

        /// <summary>
        /// 目标入栈时的状态版本号（D16 丢失目标新方案）。入栈锁定目标时记录目标卡的 VersionId；
        /// 结算时若目标卡仍存在但 VersionId 与此值不同，即判"目标状态已变 → 丢失"。
        /// 由 BumpVersion（移动/翻面/控制者变化/子类别授予撤销等中枢）驱动，取代逐属性广播。
        /// </summary>
        public int TargetVersionId { get; set; } = -1;

        /// <summary>是否已记录目标快照（目标存在且已锁定入栈时为 true；当前标记"已记录 TargetVersionId"）。</summary>
        /// <remarks>2026-08-11：原 TargetControllerSnapshot / TargetSubCategorySnapshot（目标侧属性快照）已删除——
        /// 目标自身的控制者/子类别变化由 D16 版本号方案覆盖（变化即 BumpVersion，结算比对 TargetVersionId 即可），
        /// 无需再维护方向性记忆快照。</remarks>
        public bool HasTargetSnapshot { get; set; } = false;

        /// <summary>入栈时【来源(自身)地区】快照，规则⑥"本地区"的唯一比较基准：
        /// 结算时来源或目标任一不在该地区即判丢失（争夺区内移动不刷版本号，必须靠本快照兜住）。</summary>
        public string SourceZoneDetailSnapshot { get; set; } = null;
        /// <summary>入栈时【来源控制者】快照，Friendly/Enemy 结算复检的比较基准（-1=未记录）。
        /// 用快照而非当前值：来源自身事后被夺控不改变入栈时已确定的敌我关系。</summary>
        public int SourceControllerSnapshot { get; set; } = -1;
        /// <summary>是否已记录来源快照（来源卡存在且已锁定入栈时为 true）。</summary>
        public bool HasSourceZoneSnapshot { get; set; } = false;

        /// <summary>结算负载（Kind=AreaWon）：被赢取地区的 UID</summary>
        public string WonAreaUid { get; set; }

        /// <summary>结算负载（Kind=AreaWon）：被赢取地区的槽位编号（1-based，用于升序与排序）</summary>
        public int WonAreaSlotIndex { get; set; }

        /// <summary>结算负载（Kind=AreaWon）：赢取该地区的玩家 ID</summary>
        public int WinnerId { get; set; }

        /// <summary>
        /// 【K1】本次触发事件携带的数量（`TriggerContext.ExtraInt`，如"本次弃牌的总张数"），-1 = 无。
        /// 由 `PushTriggerToStack` 从 `StackEffectInstance.EventAmount` 写入，结算时透传给
        /// `EffectEngine.ResolveEffect` 供动态值表达式 `事件数量` 求值。
        /// </summary>
        public int EventAmount { get; set; } = -1;

        /// <summary>结算负载（Kind=Trigger）：关联的入堆叠效果实例（含来源、目标、强制标记等运行时状态）。</summary>
        public StackEffectInstance StackEffect { get; set; }

        /// <summary>结算负载（Kind=Trigger）：新效果系统（EffectData）触发的直接承载。
        /// 非空时 ResolveEntry 的 Trigger 分支优先按其执行 EffectEngine.Instance.ResolveEffect（实现"效果入堆叠"）。
        /// 与 TriggerInstance 互斥，二者择一；新 JC 卡触发统一走本字段。</summary>
        public Script.Data.Effects.EffectData EffectDataEffect { get; set; }

        /// <summary>入栈时【来源卡版本号】快照（默认 -1=未记录）。仅当 EffectData.RequireSourceVersionUnchanged=true 时，
        /// 入栈记录来源卡 VersionId；结算时若来源卡 VersionId 已变（离场/翻面/进手牌等触发 BumpVersion），跳过结算。
        /// 争夺区→争夺区移动不 BumpVersion，故不影响此类效果。见 TurnManager.PushTriggerToStack / ResolveEntry。</summary>
        public int SourceVersionId { get; set; } = -1;

        /// <summary>
        /// 可选钩子：结构化结算之外的特殊逻辑（预留接口，如复杂触发效果）。
        /// TurnManager.ResolveEntry 会优先执行本委托（若非空），否则走结构化分支。
        /// 注意：委托不可序列化，仅用于单机原型；联机时以结构化 Kind+负载为准。
        /// </summary>
        public System.Action OnResolve;

        public StackEntry() { }

        public StackEntry(int ownerId, CardInstance card)
        {
            OwnerId = ownerId;
            ControllerId = ownerId;
            IsCard = true;
            SourceCard = card;
            SourceCardUid = card?.UID;
            EffectText = card?.PlayerTemplate?.CardName ?? "未知卡";
            Kind = ResolveKind.PlayToArea;   // 默认：打出角色卡 → 放地区
        }

        public StackEntry(int ownerId, string effectText)
        {
            OwnerId = ownerId;
            ControllerId = ownerId;
            IsCard = false;
            EffectText = effectText;
            Kind = ResolveKind.Trigger;       // 默认：效果型视为触发/快速效果
        }
    }

    /// <summary>
    /// 堆叠条目结算类型。对应核心需求第七章"打出卡牌结算"与效果类型。
    /// 新增卡牌/效果种类时在此枚举加值 + TurnManager.ResolveEntry 加对应分支即可。
    /// </summary>
    public enum ResolveKind
    {
        /// <summary>打出角色/角色附属类卡：结算时放置于目标地区己方争夺区</summary>
        PlayToArea,
        /// <summary>打出事务卡：结算后置入墓地</summary>
        EventToGraveyard,
        /// <summary>打出附属卡：结算时结附于目标（预留，当前未实现附属系统）</summary>
        Attachment,
        /// <summary>打出任务/地区卡：结算时成为新地区（预留）</summary>
        NewArea,
        /// <summary>快速/触发效果：结算时执行其效果（当前仅文本占位）</summary>
        Trigger,
        /// <summary>暗藏者现身：快速能力，翻转并视作角色（预留）</summary>
        AmbushReveal,
        /// <summary>赢取地区：堆叠为空时由 AreaManager.TryStartAreaWin 入栈，步骤1~5带响应窗口结算</summary>
        AreaWon,
    }

    /// <summary>
    /// 堆叠条目的目标引用。保存目标所属区域/槽位/卡UID，箭头层据此实时查询目标节点的全局中心点。
    /// </summary>
    public class StackTargetRef
    {
        /// <summary>堆叠目标引用种类：Card=指向卡实例 / Area=指向地区槽位 / Player=指向玩家。
        /// 注意与 Enums.TargetKind（打出/效果目标的"目标种类"，规则层 14 值）区分——本枚举是数据层的引用存储类型。</summary>
        public enum RefKind { Card, Area, Player }

        public RefKind Kind { get; set; }
        /// <summary>目标卡 UID（Kind=Card 时有效）</summary>
        public string CardUid { get; set; }
        /// <summary>目标地区槽位编号（Kind=Area 时有效，由 ParseAreaSlotId 解析，失败时回退查 AreaUid）</summary>
        public int AreaSlotId { get; set; }
        /// <summary>目标地区 UID（Kind=Area 时有效；编号解析失败时用它直接查 AreaSlot）</summary>
        public string AreaUid { get; set; }
        /// <summary>目标玩家 ID（Kind=Player 时有效）</summary>
        public int PlayerId { get; set; }
    }
}
