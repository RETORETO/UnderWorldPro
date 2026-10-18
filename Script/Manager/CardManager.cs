using Godot;
using System;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using System.Collections.Generic;

public partial class CardManager : Node
{
    // ============================================================
    // 单例
    // ============================================================
    public static CardManager Instance { get; internal set; }

    // ============================================================
    // 事件
    // ============================================================
    /// <summary>卡牌死亡事件，参数为死亡的 CardInstance</summary>
    public event Action<CardInstance> OnCardDied;

    /// <summary>
    /// 卡牌状态变更事件总线。所有影响目标有效性的状态变更（区域移动、暗藏变化、屏障变化）
    /// 统一经此发出，由 TurnManager 订阅并扫描堆叠置目标丢失标记，避免在多处散弹式通知。
    /// 参见 CardChangeKind 注释了解各变更种类的触发点与覆盖的丢失规则。
    /// 注意：事件是类内部资源，外部只能 +=/-=；发事件统一走 NotifyCardStateChanged。
    ///【MVC 收口】原为 static 事件，现改为实例事件：订阅/触发均经 CardManager 实例（Composition Root
    /// 创建的单例）进行，消除进程级全局状态，避免"再来一局"时旧 Arena 订阅残留叠加。
    /// </summary>
    public event Action<CardInstance, CardChangeKind> OnCardStateChanged;

    /// <summary>
    /// 触发 OnCardStateChanged（仅供 CardManager 及状态中枢方法调用，避免外部直接 Invoke 事件）。
    /// </summary>
    public void NotifyCardStateChanged(CardInstance card, CardChangeKind kind)
        => OnCardStateChanged?.Invoke(card, kind);

    /// <summary>
    /// 选目标高光总线（2026-08-07 收口）：进入/退出"选目标"交互时广播合法目标卡 UID 集合。
    /// 参数为合法目标 UID 列表；传 null 或空列表表示清除所有高光（用于窗口打开前清残留、关闭后统一取消）。
    /// View（Arena）订阅后读此集合重绘可选目标黄色高光，Controller 不再直接操作 CardUI 节点（遵循 MVC 红线）。
    ///【MVC 收口】原为 static 事件，现改为实例事件，订阅/触发统一经 CardManager 实例。
    /// </summary>
    public event Action<List<string>> OnTargetSelectionChanged;

    /// <summary>
    /// 触发 OnTargetSelectionChanged（仅供 Controller 协调器调用；View 只读集合重绘，不改 Model）。
    /// </summary>
    public void NotifyTargetSelectionChanged(List<string> uids)
        => OnTargetSelectionChanged?.Invoke(uids);

    // ============================================================
    // 内部数据
    // ============================================================
    private Dictionary<string, CardInstance> _allInstances = new Dictionary<string, CardInstance>();
    private int _nextUID = 1;

    // ---------- 【C/S 化 P3·模块C】UID 观察窗（意图上行收口 ⑥ 残余①）----------
    // 单机模式：本地 _nextUID++ 自增；C/S 模式：建卡经意图上行到服务端权威分配，
    // 客户端用服务端回传的 IntentResult.AssignedUids 建本地投影，避免双 Client 撞 UID。
    // _createWatch 标记"本次意图执行窗口内新建的实例"，供 IntentHub 收集回填。
    private string _lastCreatedUid;
    private bool _createWatch;

    // 区域列表统一由 ZoneManager 管理（单一真相源），本类仅保留实例注册与伤害处理
    private ZoneManager _zoneManager;
    //【MVC 收口】触发广播封装层实例（由 Composition Root 装配注入）
    private TriggerEmitPoints _triggerEmitPoints;

    public void SetZoneManager(ZoneManager zoneManager) => _zoneManager = zoneManager;

    /// <summary>本局 ZoneManager 引用（只读）。供"按区域枚举目标候选"（如 K14 墓地/手牌目标池）复用同一真相源。
    /// 未装配时为 null（调用方需判空）。</summary>
    public ZoneManager ZoneManagerRef => _zoneManager;

    /// <summary>【MVC 收口】注入触发广播封装层（由 GameInitializer 调用）。</summary>
    public void SetTriggerEmitPoints(TriggerEmitPoints emitPoints) => _triggerEmitPoints = emitPoints;

    /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
    public Script.Manager.EffectEngine EffectEngineRef { get; set; }

    /// <summary>
    /// 【批次 2·多对局隔离】显式注入的 PlayerManager 引用（由 `GameInitializer` 装配时注入）。
    /// 用于目标精筛里的"条件式限制"（如 `AttachedToCharacterIfNoMind` 需查【心】）；
    /// 传入 `TargetValidity.CheckRestrictions`，避免依赖进程级 `TargetValidity.PlayerManagerRef`
    /// （后者在一个服务端进程跑多场对局时只会指向最后一局）。未注入时该链路退回静态兜底（旧路径/单测）。
    /// </summary>
    public PlayerManager PlayerManagerRef { get; set; }

    /// <summary>未显式注入时回退静态 Instance（向后兼容，测试/旧装配路径不因治理而破坏）。</summary>
    private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;

    public override void _Ready()
    {
        Instance = this;
    }

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 在 EffectEngine.Instance.ResetAll 之后调用（先清空静态效果注册表，
    /// 避免本表清空时旧 UID 与新卡冲突）。
    /// </summary>
    public void Reset()
    {
        _allInstances.Clear();
        _nextUID = 1;
        GD.Print("[CardManager] Reset: 卡实例注册表已清空, _nextUID=1");
    }

    // ============================================================
    // 创建
    // ============================================================
    public CardInstance CreateCardInstance(BaseCardData template, int ownerId)
    {
        // 统一入口：为玩家卡模板经 CardEffectFactory 填充效果（幂等，仅首次实际注入）。
        // 覆盖所有创建路径（对局/测试/卡组加载），保证任何实例的 PlayerTemplate.EffectDataList 已就绪。
        if (template is Script.Data.Cards.PlayerCardData p)
            Script.Data.Effects.CardEffectFactory.EnsureEffects(p);

        string customUid = $"inst_{_nextUID++}";
        var instance = new CardInstance(template, customUid);
        instance.OwnerId = ownerId;
        instance.ControllerId = ownerId;
        _allInstances[instance.UID] = instance;
        _lastCreatedUid = instance.UID; // 记录最近新建 UID（供意图通道收集）

        // 【MVC 修复】订阅 Model 自身事件并转发到全局总线。
        // 原实现由 CardInstance 直接调用 CardManager.Notify*（Model→Controller 反向依赖）；
        // 现由工厂在实例创建时订阅其 OnStateChanged，转发到全局总线，解除 Model 对 Controller 的编译期耦合。
        // TurnManager/Arena 对 CardManager 总线事件的订阅保持不变，功能无影响。
        // 注（2026-08-11）：原 OnSubCategoriesChanged / OnControllerChanged 两条"目标丢失"总线已删除——
        // 控制者/子类别变化判丢失改由 D16 版本号方案统一覆盖（BumpVersion + 结算比对 TargetVersionId），
        // 不再需要目标相关事件总线。OnStateChanged 仍用于通用状态变更（含 UI 刷新）。
        instance.OnStateChanged += (c, k) => NotifyCardStateChanged(c, k);

        // 注意：不再订阅 instance.OnDeath 来驱动死亡流程。
        // 死亡判定统一由 DealDamageToInstance 在写入伤害后显式检测并调用 HandleInstanceDeath，
        // 若此处仍订阅 instance.OnDeath，会导致 HandleInstanceDeath 内部 Invoke OnDeath 时自递归。

        return instance;
    }

    // ============================================================
    // 内部：实例死亡处理（Controller 层死亡流程入口）
    // 由 DealDamageToInstance 在写入伤害后做规则判定调用，不再由 Model 自触发。
    // ============================================================
    private void HandleInstanceDeath(CardInstance instance)
        => ResolveDeath(instance, DeathCause.Damage);

    /// <summary>
    /// 死亡原因（死亡是一个**行为**，不是一个"卡从场上进墓地"的动作）。
    /// 触发死亡触发的三种原因：伤害累积致死、被"消灭"、被"牺牲"。
    /// 回手/回牌库（Bounce/撤回/地区赢取送库底）**不是**死亡，不广播。
    /// </summary>
    public enum DeathCause
    {
        Damage,     // 伤害+创伤 ≥ 防御（战斗伤害/效果伤害）
        Destroy,    // 被"消灭"（ActionVerb.Destroy）
        Sacrifice,  // 被"牺牲"（ActionVerb.Sacrifice / 激活代价"牺牲本角色"）
    }

    /// <summary>
    /// 【死亡语义·死亡阻止挂接点】供"不会被 A 效果消灭""不会被牺牲"类替代效果实现：
    /// 返回 true = 本次**死亡行为被阻止**（卡留在场上）→ **不广播死亡触发**。
    /// 与 `DamagePreventer` 的区别：本钩子阻止的是"死亡"本身（伤害可能已经落地/消灭已经点名），
    /// `DamagePreventer` 阻止的是"伤害落地"（伤害没发生自然没有死亡）。由具体卡包/效果注册（默认 null = 不阻止）。
    /// 语义要点：**没死就没有死亡触发** —— "A 效果成功生效但卡没死"不属于死亡。
    /// </summary>
    public System.Func<CardInstance, DeathCause, bool> DeathPreventer { get; set; }

    /// <summary>
    /// 【触发语义·死亡唯一入口】按"死亡行为"处理一张场上卡：先广播死亡（此刻卡仍在场上），再按去向离场。
    ///
    /// 关键设计（三条规则）：
    ///  1. **死亡=行为**：伤害致死 / 消灭 / 牺牲 都走本方法 → 死亡触发必然成立；回手/回牌库不走本方法。
    ///  2. **触发条件"卡在场上"在广播时天然成立**：广播发生在离场之前，故死亡触发的生效区判定不需要
    ///     任何"墓地区"声明，也不依赖卡此刻的所在区。
    ///  3. **去向与死亡无关**：`destination` 只决定卡去哪儿（默认墓地）。将来"替代效果"（如"所有卡不进入墓地、
    ///     直接返回卡组"）只需改这里的去向，**死亡触发照常成立**——绝不能用"卡在墓地"当死亡判据。
    /// </summary>
    /// <param name="destination">离场去向（默认墓地；替代效果可改为牌库底/手牌等）。</param>
    public void ResolveDeath(CardInstance instance, DeathCause cause, CardZone destination = CardZone.Graveyard)
    {
        if (instance == null) return;
        CardZone zoneAtDeath = instance.CurrentZone;
        string areaAtDeath = instance.ZoneDetailId;
        bool onField = zoneAtDeath == CardZone.AreaZone || zoneAtDeath == CardZone.AttachedZone
                       || zoneAtDeath == CardZone.SealedZone;

        // 非场上来源（手牌/牌库/墓地/堆叠）→ 不是"死亡"行为：不广播，仅按去向兜底移动。
        if (!onField)
        {
            GD.Print($"[CardManager] {instance.PlayerTemplate?.CardName} 在 {zoneAtDeath} 被{cause}：非场上卡，不产生死亡事件");
            if (destination == CardZone.Graveyard && zoneAtDeath != CardZone.Graveyard)
                MoveCardToGraveyard(instance);
            return;
        }

        // 【死亡语义·唯一阻止点】替代/防止效果成立时：卡不死、留在场上、**不广播死亡触发**。
        // 典型：「此卡不会因为敌方效果被消灭」「不能被牺牲」；伤害类则由 `DamagePreventer` 在伤害收口处拦下
        // （伤害没落地 → 无死亡，同样不广播）。
        if (DeathPreventer?.Invoke(instance, cause) == true)
        {
            GD.Print($"[CardManager] {instance.PlayerTemplate?.CardName} 本次死亡({cause})被效果阻止 → 卡留在场上、不产生死亡触发");
            return;
        }

        GD.Print($"[CardManager] 卡牌死亡({cause}): {instance.PlayerTemplate?.CardName} (UID: {instance.UID})，死亡地={areaAtDeath}");

        // 1) 广播死亡（此刻卡仍在场上；候选收集会按 IsDying 把它排除在目标之外）
        instance.IsDying = true;
        try
        {
            // 【JC124】死亡瞬间的计数器快照（必须在广播**之前**抓）：死亡触发的结算晚于"进墓地"，
            // 而进墓地会 ResetToBaseState() 清空计数器 —— 没有快照则"具哲人石标志的角色死亡时…"类条件永不成立。
            instance.SnapshotCountersAtDeath();
            // 【2026-09 修复·JC030/JC031】同一原因：离场会清空**运行期授予的子类别**（如 JC030 由
            // "血领域忠诚≥2"授予的「吸血鬼」）⇒ 以子类别为条件的死亡触发在结算复检时恒不成立。
            // 故同样在广播前抓一份"死亡瞬间的当前子类别"快照，供谓词 `DiedVampireCharacter` 读取。
            instance.SnapshotSubCategoriesAtDeath();
            instance.OnDeath?.Invoke(instance);   // Model 纯数据死亡事件（供其他订阅方）
            OnCardDied?.Invoke(instance);         // Controller 层既有 C# 事件（UI 订阅）
            // 触发总线：带上"死亡那一刻"的区域/地区（此即规则要求的"触发时所在地区"，与去向无关）
            _triggerEmitPoints?.OnCardDied(instance, instance.ControllerId, zoneAtDeath, areaAtDeath);
        }
        finally
        {
            instance.IsDying = false;
        }

        // 2) 按去向离场（死亡触发已成立，去向不参与判定）
        switch (destination)
        {
            case CardZone.Deck:
                _zoneManager?.MoveToDeckBottom(instance, instance.OwnerId, false);
                break;
            case CardZone.Hand:
                ReturnCardToHand(instance);
                break;
            case CardZone.ScoreZone:
                _zoneManager?.MoveToScore(instance, instance.OwnerId);
                break;
            default:
                MoveCardToGraveyard(instance);
                break;
        }
    }

    /// <summary>
    /// 【P1】"消灭"（`ActionVerb.Destroy`）专用入口：等价于 `ResolveDeath(..., DeathCause.Destroy)`。
    /// 保留本方法名以兼容既有调用点；语义见 <see cref="ResolveDeath"/>。
    /// </summary>
    public void DestroyInstance(CardInstance instance)
        => ResolveDeath(instance, DeathCause.Destroy);

    // ============================================================
    // 计数器
    // ============================================================
    public void AddCounter(CardInstance instance, string type, int amount = 1)
    {
        if (instance == null) return;
        if (instance.IsSealed) return;   // 规则上移：封印守卫由 Controller 负责
        instance.AddCounter(type, amount);
        // 【P3·F】卡上标志增减 → 重判"按计数缩放的持续授予"（如 JC045 每有一个时间标志 → 一个黑星）
        EE?.ReevaluatePrereqs(instance.ControllerId);
        // 【P3·K20】卡上标志增减 → 重算"持续失去图标"的受影响集合（JC123：具锁定标记者失去黑星/白星）
        EE?.ReapplyIconSuppressions();
        // 【P1·触发时机接线】卡上标志/指示物放置 → OnMarkerPlaced（此前无发点）。
        // 该时机高频，消费方需自行防自指递归（D-1 熔断兜底）。
        if (amount > 0) _triggerEmitPoints?.OnMarkerPlaced(type, amount, instance.ControllerId, instance.ZoneDetailId, instance);
    }

    public void RemoveCounter(CardInstance instance, string type, int amount = 1)
    {
        if (instance == null) return;
        instance.RemoveCounter(type, amount);
        // 【P3·F】卡上标志减少 → 同样重判"按计数缩放的持续授予"
        //（如 JC045 时间标志被移除 → 黑星数额随之减少；归零则整条授予被撤销）
        EE?.ReevaluatePrereqs(instance.ControllerId);
        // 【P3·K20】标志移除 → 重算"持续失去图标"（JC123：标志没了就该恢复黑星/白星）
        EE?.ReapplyIconSuppressions();
    }

    // ============================================================
    // 移动（委托 ZoneManager）
    // ============================================================
    public void MoveCardToHand(CardInstance instance)
        => _zoneManager?.MoveToHand(instance, instance.OwnerId);

    /// <summary>
    /// 【开局·调度步骤】把一张牌放回其所有者的牌库底（手牌 → 牌库底）。
    /// 收口到 Controller，保证手牌列表/区域桶/牌库数据层一致（与 `MoveCardToHand` 对称）。
    /// </summary>
    public void MoveCardToDeckBottom(CardInstance instance, int playerId)
    {
        if (instance == null) return;
        EE?.UnregisterContinuous(instance);
        _zoneManager?.MoveToDeckBottom(instance, playerId, false);
    }

    /// <summary>
    /// 退回手牌（MVC 收口）：先由 Controller 重置实例基础状态，再委托 ZoneManager 移动。
    /// 原实现中 View（Arena.ReturnCardToHand）直接调用 instance.ResetToBaseState() 修改 Model，
    /// 现已收口至此，确保 Model 状态变更只经 Controller 发生。
    /// </summary>
    public void ReturnCardToHand(CardInstance instance)
    {
        if (instance == null) return;
        EE?.UnregisterContinuous(instance); // P1：离场还原持续效果
        instance.ResetToBaseState();   // 规则/状态重置归属 Controller，不暴露给 View
        _zoneManager?.MoveToHand(instance, instance.OwnerId);
    }

    public void MoveCardToArea(CardInstance instance, string areaDetailId)
    {
        // 【P3·K4】"不能移动"静止规则（JC121 空鸣御守前缀；用户口径 A18/A19）：
        //  · 仅**未结附于角色**时生效（结附后本卡随宿主一起移动，本条不适用）；
        //  · 覆盖机动 / 效果移动 / 置换（它们全部经本方法这一个收口）。
        // 【2026-09 用户口径·通用】印刷规则**只在场上提供**：该卡必须**此刻在场**（争夺区/附属区且正面），
        // 否则不拦——例如"从墓地/手牌放置进场"（JC085 墓地行动、检索"进场"、`PutToArea`）不是"移动"，
        // 且卡不在场时其印刷规则本就不生效（此前无此门禁 ⇒ 在墓地/手牌里的 JC121 会被误拦）。
        if (instance != null
            && instance.IsExertingEffects
            && EffectEngine.Instance?.HasPrintedStaticKind(instance, Script.Data.Effects.StaticKind.CannotMoveWhileUnattached) == true
            && instance.AttachedTo == null)
        {
            GD.Print($"⛔ [CardManager] {instance.PlayerTemplate?.CardName} 未结附于角色 → 「不能移动」，拒绝移动到 {areaDetailId}");
            return;
        }
        // 【P3·K21·JC064 戒严】"**所有非【国家机构】的角色牌…也不能移动进入本地区**"（用户口径 A19：
        // "移动"**涵盖机动 / 效果移动 / 置换**）——本方法是三者**唯一收口**，故在此拦：
        //  · 只拦**角色牌**（卡文"角色牌"）；
        //  · 只拦"**移动进入**"＝该卡**此刻已在场上**（争夺区）的位置变更；从墓地/手牌/牌库的**放置进场**
        //    （如"操控死尸"）按 FAQ **放行**；**秘密派遣**走 `TurnManager.DeployCovert`→`ZoneManager.MoveToArea`，
        //    不经本方法，同样放行（FAQ 原文允许）。
        if ((instance.EffectiveCategoryFlag & CardCategory.Character) != 0
            && instance.CurrentZone == CardZone.AreaZone
            && EffectEngine.Instance?.IsEntryBlockedByOrgRule(instance, areaDetailId) == true)
        {
            GD.Print($"⛔ [CardManager] {instance.PlayerTemplate?.CardName} 受『戒严』类静止规则限制，拒绝移动进入 {areaDetailId}");
            return;
        }
        // 机动移动按控制者归属（ControllerId），维持控制者不变。
        _zoneManager?.MoveToArea(instance, areaDetailId, instance.ControllerId);
    }

    /// <summary>
    /// 暗藏者现身入栈：将卡从当前区移入堆叠区（不重置状态，卡已在入栈前翻面为正面）。
    /// 按控制者(ControllerId)归属堆叠区玩家，与 MoveCardToArea 的归属语义一致（D32）。
    /// 入栈期间 CurrentZone=Stack，结算时 MoveToArea 会因 fromZone!=AreaZone 自然触发 EnterTrigger。
    /// </summary>
    public void MoveCardToStack(CardInstance instance)
        => _zoneManager?.MoveToStack(instance, instance.ControllerId);

    /// <summary>
    /// 【事务卡打出】把卡本体移入**堆叠区**（响应窗口期间留栈，结算时才入墓）。
    /// 刻意复刻 `MoveCardToGraveyard` 的三项副作用，保证除"所在区域"外行为与旧路径一致：
    ///   1) `EE.UnregisterContinuous`（离场还原持续效果）；
    ///   2) 归属归一（`MoveToGraveyard` 传的是 `OwnerId`，此处同样用 `OwnerId`）；
    ///   3) `ResetToBaseState`（清伤害/横置/封印标记/动态授予/额外费用支付态）。
    /// </summary>
    public void MoveCardToStackForPlay(CardInstance instance)
    {
        if (instance == null) return;
        EE?.UnregisterContinuous(instance);
        _zoneManager?.MoveToStack(instance, instance.OwnerId, resetToBaseState: true);
    }

    /// <summary>
    /// 暗藏者"现身"（Reveal，背面→正面揭示）：规范化行为封装。
    /// 不移动地区（卡仍在原争夺区），但按规则需触发进场类效果：
    ///   1) 复位为基础状态并解除暗藏（IsCovert=false，与正常打出一致）；
    ///   2) 广播 EnterTrigger   → 触发"进场触发(EnterTrigger)"；
    ///   3) 广播 UncoverTrigger → 触发"现身触发(UncoverTrigger)"；
    /// 两类时机经 TriggerRegistry 分别收集（D37+：现身复用进场触发链 + UncoverTrigger 时机）。
    /// 注：争夺区→争夺区移动（机动/夺控）不触发本行为，详见 ZoneManager.MoveToArea。
    /// </summary>
    public void RevealCard(CardInstance instance)
    {
        if (instance == null) return;
        if (!instance.IsCovert) return;   // 本就正面/非暗藏，无需现身（幂等保护，避免重复触发）

        // 【需求3·翻面统一清理】翻正面 = 版本号变动 ⇒ 宿主关系脱离：附属进各自拥有者墓地、
        // 封印卡回各自拥有者手牌（与 GM `flip` 命令同口径；收敛 PM 里两份私有实现）。
        _zoneManager?.DetachHostRelations(instance);
        // 【需求1①】保留横置：角色横置翻面后仍横置、重置翻面后仍重置（preserveTapped:true）；
        // 指示物 / 运行期授予 / 伤害等其余属性照常重置。
        instance.ResetToBaseState(preserveTapped: true);
        // 【K13/P3·E】`ResetToBaseState` 会**清空运行期授予**（含光环授予：如 JC038 授予的"袭击"）——
        // 复位后必须立即按当前场上重算光环，否则"现身即丢失光环授予"（且光环登记表仍以为已授予，不会自动补回）。
        EE?.ReevaluateAuras(instance.ControllerId);
        string areaId = instance.ZoneDetailId ?? "";
        // 现身 = 复用进场触发链 + 广播现身触发（D39 薄封装）
        _triggerEmitPoints?.EnterTrigger(instance, instance.ControllerId, areaId);
        _triggerEmitPoints?.UncoverTrigger(instance, instance.ControllerId, areaId);
        // 【2026-09·领袖口径】在**原地翻为正面**（非"回地区"路径，故不会经过 `OnCardEnteredArea`）时，
        // 该卡的"正面在场"状态刚成立 ⇒ 由领袖唯一性规则重新收敛：
        // "至多 1 个领袖"只统计正面领袖（`LeadersOnField` 跳过暗藏），因此此刻才需要检查是否超限。
        if (instance.CurrentZone == CardZone.AreaZone)
            LeaderRule.EnforceOnEntry(instance, this, EE);
    }

    public void MoveCardToGraveyard(CardInstance instance)
    {
        if (instance == null) return;
        EE?.UnregisterContinuous(instance); // P1：离场还原持续效果
        _zoneManager?.MoveToGraveyard(instance, instance.OwnerId);
    }

    // ============================================================
    // 查询（委托 ZoneManager）
    // ============================================================
    public List<CardInstance> GetHandCards()
        => _zoneManager != null ? _zoneManager.GetZoneCards(CardZone.Hand, 0) : new List<CardInstance>();
    public List<CardInstance> GetAreaCards()
        => _zoneManager != null ? _zoneManager.GetZoneCards(CardZone.AreaZone, 0) : new List<CardInstance>();
    public List<CardInstance> GetGraveyardCards()
        => _zoneManager != null ? _zoneManager.GetZoneCards(CardZone.Graveyard, 0) : new List<CardInstance>();
    public CardInstance GetInstance(string uid) => _allInstances.GetValueOrDefault(uid);

    // ---------- 【C/S 化·第一批】快照重建：按权威态 ZoneCardRef 恢复实例运行时字段 ----------
    /// <summary>
    /// 与 ZoneManager.ApplySnapshot 配套：以 UID 为键取已存在实例，覆盖其运行时状态（不触发规则事件，
    /// 纯数据还原）。实例正常必已由牌库/卡组加载阶段建好（快照永远晚于建实例），故正常路径不新建；
    /// 极端竞态（如重连时服务端 UID 与本地自增 UID 对不齐、或快照先于卡组加载到达）走 <see cref="ResolveInstance"/> 自愈。
    /// 设计约束：对单机零影响（单机不调用本方法），复用既有实例避免 UID 漂移。
    /// 拆分 ResolveInstance 的目的：纯托管 Server 内核路线可重载该解析方法（改为从 CardTemplateData JSON 建实例），
    /// 而本方法的"运行时字段覆盖"逻辑对两条路线通用、不被推翻。
    /// </summary>
    public CardInstance RebuildFromRef(Script.Net.ZoneCardRef r)
    {
        if (r == null) return null;

        // 【Phase B 信息隔离·统一入口】隐藏占位（对手手牌/牌库、地区牌堆）不依赖模板库解析：
        // 在调用 ResolveInstance 之前统一处理，保证任何子类 override（跨进程客户端/测试替身/未来纯托管）
        // 都能正确得到占位实例，且不会因 TemplateUid 为空而走模板查找（历史上会抛 Dictionary key 为空）。
        // 占位 UID 稳定 → 跨快照复用同一实例。
        if (r.IsHidden)
        {
            var existed = GetInstance(r.Uid);
            var hidden = existed ?? new CardInstance(null, r.Uid);
            hidden.IsHidden = true;
            hidden.OwnerId = r.OwnerId;
            hidden.ControllerId = r.ControllerId;
            hidden.CurrentZone = r.Zone;
            hidden.ZoneDetailId = r.ZoneDetailId;
            if (existed == null) _allInstances[r.Uid] = hidden;
            return hidden;
        }

        var inst = ResolveInstance(r);
        if (inst == null)
        {
            GD.PrintErr($"[CardManager] RebuildFromRef: 无法解析实例 UID={r.Uid}（模板 {r.TemplateUid}），已跳过");
            return null;
        }
        // 同步运行时字段（纯覆盖，不触发 OnStateChanged，避免重建过程刷 UI/误判目标丢失）
        inst.OwnerId = r.OwnerId;
        inst.ControllerId = r.ControllerId;
        inst.IsCovert = r.IsCovert;
        inst.IsSealed = r.IsSealed;
        inst.IsAttached = r.IsAttached;
        inst.IsSeal = r.IsSeal;
        inst.IsSealedFaceDown = r.IsSealedFaceDown;   // 【P2】背面朝上封印：影响客户端可见性（非规则）
        inst.HasGuard = r.HasGuard;                   // 【P3】屏障：影响客户端本地目标合法性判定
        inst.IsTapped = r.IsTapped;
        inst.Damage = r.Damage;
        inst.Trauma = r.Trauma;
        inst.CurrentInvestigation = r.CurrentInvestigation;
        inst.CurrentStrength = r.CurrentStrength;
        inst.CurrentPower = r.CurrentPower;
        inst.CurrentDefense = r.CurrentDefense;
        inst.MagicDomains.Clear();
        if (r.MagicDomains != null) inst.MagicDomains.AddRange(r.MagicDomains);
        inst.EnteredThisTurn = r.EnteredThisTurn;
        inst.AbilityUsedThisTurn = r.AbilityUsedThisTurn;
        // 【K23】每回合每能力使用次数随快照下行（客户端 `CanActivate` 与服务端同解，否则"已达上限仍高亮"）
        {
            var pairs = new List<KeyValuePair<string, int>>();
            int n = Math.Min(r.UsedAbilityIds?.Count ?? 0, r.UsedAbilityCounts?.Count ?? 0);
            for (int i = 0; i < n; i++) pairs.Add(new KeyValuePair<string, int>(r.UsedAbilityIds[i], r.UsedAbilityCounts[i]));
            inst.SetAbilityUsesThisTurn(pairs);
        }
        // 【A57】每局游戏每能力使用次数随快照下行（同上：客户端"每局一次"的可用性判定要与服务端同解）
        {
            var pairsGame = new List<KeyValuePair<string, int>>();
            int nGame = Math.Min(r.UsedAbilityIdsGame?.Count ?? 0, r.UsedAbilityCountsGame?.Count ?? 0);
            for (int i = 0; i < nGame; i++)
                pairsGame.Add(new KeyValuePair<string, int>(r.UsedAbilityIdsGame[i], r.UsedAbilityCountsGame[i]));
            inst.SetAbilityUsesThisGame(pairsGame);
        }
        inst.IsHidden = r.IsHidden;   // Phase B 信息隔离：占位实例标记（只画卡背，不参与规则/交互）
        inst.SyncVersion(r.VersionId);

        // 【P1·运行时状态投影补全】静止标记 / 当前子类别 / 运行时授予 / 先手值：
        // 客户端不运行 EffectEngine，这些状态必须由快照带过来，客户端上的判定与展示才与服务端同解。
        // （单进程回环复用同一实例时等价于"照抄自己"，无副作用；真跨进程则把服务端真值搬过来。）
        var staticFlags = new List<StaticKind>();
        if (r.StaticFlags != null)
            foreach (var f in r.StaticFlags) staticFlags.Add((StaticKind)f);
        inst.RestoreProjectedState(staticFlags, r.SubCategories, r.RemovedSubCategories, r.GrantedKeywords, r.GrantedIcons,
            r.GrantedAttributes, r.CurrentFirstStrikeInvestigation, r.CurrentFirstStrikeStrength);
        // 【P3·K4】实例级形态覆盖（JC027/JC028）：类别位/卡名/印刷效果失活 随快照还原（客户端判定同解）
        if (r.CategoryFlagOverride != 0 || !string.IsNullOrEmpty(r.CardNameOverride) || r.PrintedEffectsSuppressed)
            inst.ApplyFormOverride((CardCategory)r.CategoryFlagOverride, r.CardNameOverride,
                r.PrintedEffectsSuppressed, zeroPrintedStatistics: false, formEffects: null);

        return inst;
    }

    /// <summary>
    /// 【C/S 化·第一批·兜底自愈】把快照 ZoneCardRef 解析成本局 CardInstance。
    /// 解析策略（Godot 侧，按"尽量复用已建实例"原则，避免引入模板库依赖死链）：
    ///  1) 首选：_allInstances 中直接命中 r.Uid（正常路径，快照晚于加载）；
    ///  2) 兜底：遍历 _allInstances，找一个"同 TemplateUid 且仍处于 Deck 区域（尚未被本轮快照归位）"的空闲实例，
    ///     复用其身份并把 UID 重置为权威态 r.Uid（覆盖本地自增错位），使其后续被 ApplySnapshot 正确归位。
    ///  3) 仍失败：返回 null，由调用方跳过该卡（纯托管路线可重载本方法从 JSON 模板补建）。
    /// 注意：本方法不写运行时字段（那是 RebuildFromRef 的职责），只负责"拿到正确的实例对象"。
    /// </summary>
    protected virtual CardInstance ResolveInstance(Script.Net.ZoneCardRef r)
    {
        // 1) 直接命中
        var hit = GetInstance(r.Uid);
        if (hit != null) return hit;

        // 注：隐藏占位（r.IsHidden）已在 RebuildFromRef 中统一处理，不会进入本方法，
        // 故此处无需再判 IsHidden（避免多处重复实现，也不要求子类 override 自行处理）。

        // 2) 同模板空闲实例复用（覆盖重连 UID 错位竞态）
        foreach (var cand in _allInstances.Values)
        {
            if (cand == null) continue;
            if (cand.Template?.UID != r.TemplateUid) continue;
            // 仅复用仍躺在牌库、且 UID 尚未被本轮快照认领的卡（避免复用已归位的卡造成双归位）
            if (cand.CurrentZone == Script.Enums.CardZone.Deck && GetInstance(r.Uid) == null)
            {
                cand.SetUid(r.Uid);
                _allInstances[r.Uid] = cand; // 以权威 UID 重新登记（旧自增 UID 键可忽略，重连后整表将被 ApplySnapshot 重置）
                GD.Print($"[CardManager] ResolveInstance: 自愈复用同模板空闲实例（模板 {r.TemplateUid} → 权威UID {r.Uid}）");
                return cand;
            }
        }

        // 3) 仍失败
        return null;
    }

    /// <summary>
    /// 【C/S 化·跨进程】供子类（如客户端独立 CardManager）在 ResolveInstance 从模板新建实例后调用：
    /// 把实例的 UID 重置为服务端权威 UID，并按权威 UID 登记到注册表，保证 GetInstance 能按快照 UID 命中。
    /// </summary>
    protected void AdoptInstanceWithUid(CardInstance inst, string uid)
    {
        if (inst == null) return;
        if (!string.IsNullOrEmpty(uid))
        {
            inst.SetUid(uid);
            _allInstances[uid] = inst;
        }
    }

    // ---------- 【C/S 化 P3·模块C】意图通道辅助：UID 观察窗 + 效果查找 ----------
    /// <summary>标记"建卡观察窗"开始：其后第一次 CreateCardInstance 产生的 UID 会被 ConsumeCreatedUid 收集。</summary>
    public void MarkCreateWatch() => _createWatch = true;

    /// <summary>取出观察窗内新建的实例 UID（仅一次），并复位窗口。无新建则返回 null。</summary>
    public string ConsumeCreatedUid()
    {
        _createWatch = false;
        return _lastCreatedUid;
    }

    /// <summary>按卡 UID + 效果 Id 从其实例模板上查找对应 EffectData（供 ActivateAbility 意图路由）。
    /// 兼容玩家卡（PlayerTemplate.EffectDataList）与秘社卡（CouncilTemplate.ActivationOptions 的各 EffectDataList）。</summary>
    public EffectData GetEffectData(string cardUid, string effectId)
    {
        var inst = GetInstance(cardUid);
        if (inst == null) return null;

        if (inst.PlayerTemplate is PlayerCardData tmpl && tmpl.EffectDataList != null)
        {
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
        }

        if (inst.CouncilTemplate is CouncilCardData councilTmpl && councilTmpl.ActivationOptions != null)
        {
            foreach (var opt in councilTmpl.ActivationOptions)
            {
                if (opt?.EffectDataList == null) continue;
                foreach (var e in opt.EffectDataList)
                    if (e != null && e.EffectId == effectId) return e;
            }
        }

        // 【2026-09 修复·用户实测"JC019 结附后宿主发动能力被服务端拒绝：发动能力参数无效"】
        // **运行期授予的激活能力**（`EffectData.GrantedAbility`，如 JC019 给宿主的"快速行动，横置：造成 1 点伤害"）
        // 不在宿主模板的 `EffectDataList` 里 ⇒ 联机权威路径 `GodotIntentRouter.ActivateAbility` 经本方法取数得到 null
        // ⇒ 直接抛"发动能力参数无效"（客户端弹了窗口、点了能力，却被服务端拒绝）。
        // 现补一条：按运行期授予项查找（与候选枚举/门禁同一入口 `ActivateEligibility.GetAllActivateAbilities`）。
        foreach (var e in ActivateEligibility.GetAllActivateAbilities(inst))
            if (e != null && e.EffectId == effectId) return e;

        return null;
    }

    /// <summary>返回所有已注册的卡实例（场上/手牌/墓地/牌库/资产/秘社等）。用于触发效果收集器扫描静态触发。</summary>
    public List<CardInstance> GetAllInstances()
        => new List<CardInstance>(_allInstances.Values);

    /// <summary>按所有者查询手牌</summary>
    public List<CardInstance> GetHandCardsByOwner(int ownerId)
        => _zoneManager != null ? _zoneManager.GetZoneCards(CardZone.Hand, ownerId) : new List<CardInstance>();

    /// <summary>按所有者查询场上卡牌</summary>
    public List<CardInstance> GetAreaCardsByOwner(int ownerId)
        => _zoneManager != null ? _zoneManager.GetZoneCards(CardZone.AreaZone, ownerId) : new List<CardInstance>();

    /// <summary>按所有者查询墓地</summary>
    public List<CardInstance> GetGraveyardCardsByOwner(int ownerId)
        => _zoneManager != null ? _zoneManager.GetZoneCards(CardZone.Graveyard, ownerId) : new List<CardInstance>();

    public List<CardInstance> GetAreaInstancesBySlot(string slotUid)
        => _zoneManager != null ? _zoneManager.GetAreaCardsBySlot(slotUid) : new List<CardInstance>();

    // ============================================================
    // 伤害管理
    // ============================================================
    /// <summary>
    /// 【死亡语义·伤害防止挂接点】供"灵体（JC 包=防止伤害）""防止伤害""护盾"等实现：
    /// 返回 true = 本次伤害被**全部防止** → 不写伤害 → 不判死 → **不产生死亡触发**。
    /// 由具体卡包/效果注册（默认 null = 无防止，行为不变）。
    /// 语义要点：死亡触发的成立前提是"死亡真的发生了"；伤害被防止就没有死亡，故绝不能广播死亡触发。
    /// </summary>
    public System.Func<CardInstance, int, bool> DamagePreventer { get; set; }

    public void DealDamageToInstance(CardInstance instance, int amount, bool isTrauma = false)
        => DealDamageToInstance(instance, amount, isTrauma, allowRedirect: true, unpreventable: false);

    /// <summary>
    /// 【P3·K13】`unpreventable`：本次伤害**不能被防止**（JC019 镇魔枪授予的"该伤害不能被防止"）——
    /// 跳过 `DamagePreventer`（灵体 + 伤害护盾），直接落地；**重定向**不受影响（那是"改由谁承受"而非"防止"）。
    /// </summary>
    public void DealDamageToInstance(CardInstance instance, int amount, bool isTrauma, bool allowRedirect)
        => DealDamageToInstance(instance, amount, isTrauma, allowRedirect, unpreventable: false);

    /// <summary>
    /// 【P3·K3】`allowRedirect`：是否允许把本次伤害**改由其它卡承受**（JC077 庇佑圣灵 / JC121 空鸣御守）。
    /// 重定向后递归调用时传 false —— **一次伤害只重定向一次**（防"你来我往"乒乓），
    /// 但承受方自身的"防止"（灵体/护盾）仍在其落地时照常判定。
    /// </summary>
    public void DealDamageToInstance(CardInstance instance, int amount, bool isTrauma, bool allowRedirect,
        bool unpreventable)
    {
        if (instance == null || !instance.HasDefense) return;
        // 暗藏者(IsCovert)潜伏状态，免疫一切伤害（含后续效果系统造成的伤害）：
        // 统一在此收口，避免任何伤害来源绕过战斗窗口的承伤判定伤到暗藏者。
        if (instance.IsCovert) return;
        // 【P3·K3】持续型"伤害改由某卡承受"（不是触发、也不是防止）：先于"防止"判定，改判承伤对象。
        if (allowRedirect && amount > 0)
        {
            var bearer = EE?.ResolveDamageRedirect(instance);
            if (bearer != null && !ReferenceEquals(bearer, instance))
            {
                GD.Print($"[CardManager] ⇄ {instance.PlayerTemplate?.CardName} 本次 {amount} 点伤害改为由 " +
                         $"{bearer.PlayerTemplate?.CardName} 承受（持续重定向）");
                DealDamageToInstance(bearer, amount, isTrauma, allowRedirect: false, unpreventable: unpreventable);
                return;
            }
        }
        // 【死亡语义】伤害防止（灵体/防止伤害类）：防止成功 → 不写伤害 → 不判死 → 不触发死亡
        // 【P3·K13】"不能被防止"的伤害跳过本分支（JC019 授予的能力）
        if (amount > 0 && !unpreventable && DamagePreventer?.Invoke(instance, amount) == true)
        {
            GD.Print($"[CardManager] {instance.PlayerTemplate?.CardName} 本次 {amount} 点伤害被防止 → 不写伤害、不产生死亡");
            return;
        }
        if (amount > 0 && unpreventable)
            GD.Print($"[CardManager] ⚠ {instance.PlayerTemplate?.CardName} 本次 {amount} 点伤害**不能被防止** → 直接落地");
        if (isTrauma)
            instance.TakeTrauma(amount);
        else
            instance.TakeDamage(amount);
        // 死亡判定上移至 Controller：写入伤害后统一检查，避免 Model 内嵌流程逻辑
        if (instance.IsDead())
            HandleInstanceDeath(instance);
    }

    public void ResetAllDamage()
    {
        foreach (var instance in GetAreaCards())
            instance.ResetDamage();
        GD.Print("✅ 所有角色伤害标记已重置 (逻辑层)");
    }

    /// <summary>
    /// 【2026-09·用户口径 JC059】**防御力下降后的补死亡判定**。
    ///
    /// 死亡判据是"伤害＋创伤 ≥ **当前**防御力"（`CardInstance.IsDead()`），因此除了"受伤"，
    /// 任何让**当前防御力变低**的路径（光环撤销、时效到期、形态还原、-N 防御效果…）都必须和伤害一样
    /// 跑一次死亡检查 —— 否则已经受伤的角色会在防御掉回去后**赖着不死**。
    /// 用户实测报障原文："JC059 移动地区后，原有地区的角色防御总值下降，如果已经受伤可能会导致死亡，
    /// 现在没有因为这个原因死亡，需要修正"。
    /// 【2026-10 用户口径·JC089 追加】"**最大防御只有 1 的宿主被 -1 防御后应死亡**"⇒ 判据放宽：
    /// 防御被降到 0（`CurrentDefense == 0`）**即使毫发无伤也死亡**；`CurrentDefense < 0`（无防御哨兵）不判死。
    ///
    /// 由 Controller（光环重算 / 时效还原等"减防"路径尾部）调用；**只在真的满足死亡判据时**才走既有
    /// 死亡流程 `HandleInstanceDeath`（含死亡时机广播与墓地处理），与伤害入口同口径地排除暗藏者。
    /// </summary>
    /// <returns>true ＝ 本次判定导致该卡死亡。</returns>
    public bool RecheckDeathAfterDefenseDrop(CardInstance instance)
    {
        if (instance == null) return false;
        // 只有在场卡参与死亡判定（争夺区/附属区；手牌/牌库/墓地等不在此列）
        if (instance.CurrentZone != CardZone.AreaZone && instance.CurrentZone != CardZone.AttachedZone) return false;
        if (instance.IsCovert) return false;          // 暗藏者免疫伤害/不参与死亡判定（与 `DealDamageToInstance` 同口径）
        // 【2026-10 用户口径·JC089】判据＝"**当前有防御力**（`CurrentDefense >= 0`）且 伤害＋创伤 ≥ 当前防御力"：
        //  · `CurrentDefense < 0`（＝-1 哨兵"当前无防御"，如附属/形态过渡态）⇒ 不判死
        //    （原"必须已经受伤"的写法会漏掉防御被降到 0 的卡）；
        //  · 防御被降到 **0**（如 1 防御的角色吃到 -1 防御）⇒ **即使毫发无伤也应死亡**（用户实测 JC089：不死亡）；
        //  · 已受伤的角色因减防而 `伤害＋创伤 ≥ 防御`（如 3 防御受 2 伤 → -1 防御后 2 ≥ 2）⇒ 死亡。
        if (instance.CurrentDefense < 0) return false;
        if (!instance.IsDead()) return false;
        GD.Print($"[CardManager] 💀 {instance.PlayerTemplate?.CardName} **防御力下降**后满足死亡判据" +
                 $"（伤害{instance.Damage}＋创伤{instance.Trauma} ≥ 防御{instance.CurrentDefense}）→ 死亡");
        HandleInstanceDeath(instance);
        return true;
    }

    /// <summary>重置步骤：解除所有区域（争夺区/手牌/资产/结附卡/**秘社**）卡牌的横置。
    /// 结附卡（AttachedZone，含结附于角色与结附于秘社的附属、封印卡）此前漏重置，
    /// 现统一纳入，使已横置的附属卡（如 JC012 帷幕守望徽记）在回合开始随其他卡一起还原。
    /// 【2026-09 修复·用户实测】**秘社区（CouncilZone）此前也漏了** ⇒ 秘社卡发动能力时自己横置后，
    /// 回合开始的重置步骤不会竖置它（表现为"秘社能力用不了第二次 / 秘社一直处于横置状态"）；
    /// 现补 `CardZone.CouncilZone`（`GetZoneCards(zone, 0)` 取双方）。</summary>
    public void UntapAll()
    {
        if (_zoneManager == null) return;
        int count = 0, blocked = 0;
        foreach (var zone in new[] { CardZone.AreaZone, CardZone.Hand, CardZone.AssetZone, CardZone.AttachedZone, CardZone.CouncilZone })
            foreach (var c in _zoneManager.GetZoneCards(zone, 0))
            {
                if (!c.IsTapped) continue;
                // 【P3·K29】静止规则：其上有牌封印的**敌方角色**在重置步骤中不能被重置（JC108 织梦人）。
                // 判定入口＝`EffectEngine.IsUntapBlocked`（单一实现；来源在场期间全局生效，卡文无"本地区"限定）。
                if (EE?.IsUntapBlocked(c) == true)
                {
                    blocked++;
                    GD.Print($"[CardManager] 🔒 {c.PlayerTemplate?.CardName} 其上有牌封印 → 重置步骤不解除横置（JC108）");
                    continue;
                }
                c.SetTapped(false); count++;   // SetTapped 发 TappedChanged（阿格斯IV竖置恢复封锁）
            }
        GD.Print($"🔓 重置步骤：解除 {count} 张卡牌横置（含资产、结附卡、秘社，费用恢复）" +
                 (blocked > 0 ? $"；{blocked} 张因『其上有牌封印』被锁定未重置" : ""));
    }

    /// <summary>
    /// 【K23】重置步骤：清空**所有卡**的本回合"每能力"使用次数（"该能力每回合只能发动一次/两次"）。
    /// 计数粒度＝每张卡·每回合·每能力（`CardInstance.GetAbilityUsesThisTurn`）。
    /// 另：按用户口径，卡版本号变动时该卡的计数也会单独清空（`CardInstance.BumpVersion`）。
    /// </summary>
    public void ResetAllAbilityUsesThisTurn()
    {
        int n = 0;
        foreach (var c in GetAllInstances())
        {
            if (c == null) continue;
            if (c.AbilityUsesThisTurn.Count == 0) continue;
            c.ResetAbilityUsesThisTurn();
            n++;
        }
        GD.Print($"♻️ 重置步骤：清空 {n} 张卡的『每回合每能力』使用次数");
    }

    /// <summary>
    /// 【A57】**重开一局**时清空所有卡的"每局游戏"能力使用次数（`EffectData.MaxUsesPerGame`）。
    /// 与 `ResetAllAbilityUsesThisTurn` 的区别：**只在开局/重开**调用，**不参与回合重置**——
    /// "每局一次"是整局维度的承诺（离场/换区/回手/版本号变动都不清）。
    /// 说明：实例本身按局重建，故本方法主要是"重开一局复用实例"时的兜底与显式口径。
    /// </summary>
    public void ResetAllAbilityUsesThisGame()
    {
        int n = 0;
        foreach (var c in GetAllInstances())
        {
            if (c == null) continue;
            if (c.AbilityUsesThisGame.Count == 0) continue;
            c.ResetAbilityUsesThisGame();
            n++;
        }
        GD.Print($"♻️ 开局：清空 {n} 张卡的『每局每能力』使用次数");
    }

    // ============================================================
    // 打出目标合法性（MVC 收口-P3）：原在 View(Arena) 内嵌的规则粗筛，上移至 Controller
    // ============================================================

    /// <summary>把场上卡实例映射为打出/激活目标种类（粗筛）。public 供激活框架复用。
    /// 【K14】墓地/手牌/**资产**目标**按所在区域优先映射**（与卡的类别无关：墓地里的角色牌是"墓地目标"、
    /// 资产区里的附属牌是"资产目标"）——用户 2026-09 口径：允许指定对手的手牌/墓地/资产，
    /// 结算时按版本号判丢失（**除"争夺区→争夺区"外的一切区域切换都刷新版本号**，见 `ZoneManager.FireMoved`）。</summary>
    public static TargetKind MapInstanceToTargetKind(CardInstance inst)
    {
        if (inst == null) return TargetKind.None;
        if (inst.CurrentZone == CardZone.Graveyard) return TargetKind.Graveyard;   // 【K14】墓地目标
        if (inst.CurrentZone == CardZone.Hand) return TargetKind.Hand;             // 【K14】手牌目标
        if (inst.CurrentZone == CardZone.AssetZone) return TargetKind.Asset;       // 【K14】资产目标
        if (inst.IsCovert) return TargetKind.Covert;          // 暗藏者
        // 【P3·K4】实例级形态（JC027"成为一个地精角色"）优先于"附属区映射"：
        // 类别覆盖里含角色 → 按**角色**参与目标合法性（用户口径：参与"目标角色"类判定）。
        if ((inst.CategoryFlagOverride & CardCategory.Character) != 0) return TargetKind.Character;
        if (inst.IsAttached) return TargetKind.Attachment;     // 附属
        // 秘社（CouncilCardData，承载于 CouncilZone）：即便 PlayerTemplate 为 null 也要识别
        if (inst.CurrentZone == CardZone.CouncilZone || inst.CouncilTemplate != null) return TargetKind.Council;
        if ((inst.EffectiveCategoryFlag & CardCategory.Character) != 0) return TargetKind.Character;
        if ((inst.EffectiveCategoryFlag & CardCategory.Attachment) != 0) return TargetKind.Attachment;
        // 资产/秘社等在场上以其他区域呈现，这里仅覆盖争夺区常见种类
        return TargetKind.None;
    }

    /// <summary>判断 dragging 这张手牌是否能以 target 这张场上卡为合法目标（数据驱动 TargetRules 裁决）。</summary>
    public bool IsLegalPlayTarget(CardInstance dragging, CardInstance target)
    {
        if (dragging == null || target == null || dragging == target) return false;
        var tmpl = dragging.PlayerTemplate;
        if (tmpl == null || !tmpl.GetRequiresTarget()) return false;

        // D19-⑤：静止效果"不能被装备结附"——仅当打出的是【装备】类附属卡（子类型含"装备"，含运行时授予）
        // 且目标带 CannotBeAttached 标记时，禁止将其选为目标（打出阶段即拦截，而非结算后才进墓地）。
        // 【P0·同一判定不静默降级】原写法为 `EE != null && EE.HasStaticFlag(target, …)`：
        // EffectEngine 只是 CardInstance.StaticFlags 的查询转发（见 EffectEngine.HasStaticFlag），
        // 加 EE 判空会让"客户端没有 EffectEngine 实例"直接吞掉整条规则（静默放行非法目标）。
        // 改为直读实例标记：只要该标记随快照投影到位（见 ZoneCardRef.StaticFlags），两端判定天然一致。
        if (dragging.CurrentSubCategories.Contains("装备")
            && target.HasStaticFlag(StaticKind.CannotBeAttached))
        {
            GD.Print($"⛔ [目标拦截] 装备 {tmpl.CardName} 不能以 {target.PlayerTemplate?.CardName} 为目标（不能被装备结附）");
            return false;
        }

        // 【JC081】"**除装备与祝福外不受附属**"（与上条同类实现，把"一律禁止"放宽成"白名单允许"）：
        // 目标声明了子类别白名单（`OnlyAttachWithSubCategories.Param`，本卡＝`装备,祝福`）→
        // **打出阶段即拦截**：结附它的附属必须命中白名单之一，否则不能以它为目标。
        // 判定用静态 helper（读卡面印刷规则，客户端/服务端同解，不依赖 EffectEngine 实例）。
        {
            string whitelist = EffectEngine.GetAttachSubCategoryWhitelist(target);
            if (whitelist != null && !EffectEngine.PassesAttachWhitelist(dragging, whitelist))
            {
                GD.Print($"⛔ [目标拦截] {tmpl.CardName}（子类别 {string.Join("/", dragging.CurrentSubCategories)}）"
                       + $" 不能以 {target.PlayerTemplate?.CardName} 为目标（只受『{whitelist}』类附属结附）");
                return false;
            }
        }

        // 方案 A：逐条 TargetRule 裁决。满足【任一】规则即合法（OR）。
        int sourceController = dragging.ControllerId;   // 打出者/来源方
        if (tmpl.TargetRules != null)
        {
            foreach (var rule in tmpl.TargetRules)
                if (rule != null && MatchesRule(rule, target, sourceController, PlayerManagerRef))
                    return true;
        }

        // 事务卡：额外聚合 EffectData（OnSorceryCast）的 Target 规则裁决（目标真相源=效果系统）。
        // 满足【任一】效果的目标规则即合法（OR）。条件式限制（如 AttachedToCharacterIfNoMind）在
        // 取目标阶段由 CheckRestrictions 实时评估【心】，实现"有【心】选任意附属/无【心】仅选结附角色"。
        if ((tmpl.CategoryFlag & CardCategory.Sorcery) != 0 && tmpl.EffectDataList != null)
        {
            foreach (var e in tmpl.EffectDataList)
            {
                if (e == null || e.Timing != EffectTiming.OnSorceryCast) continue;
                var spec = e.Target;
                if (spec == null || spec.Kind == TargetKind.None) continue;
                if (MatchesSpec(spec, target, sourceController, PlayerManagerRef))
                    return true;
            }
        }
        return false;
    }

    /// <summary>单条 TargetSpec（效果系统目标）匹配：先粗筛大类，再要求所有 Restrictions 满足（AND）。
    /// 与 MatchesRule 对称，供事务卡打出时按 EffectData.Target 裁决候选（目标真相源=效果系统）。</summary>
    private static bool MatchesSpec(Script.Data.Effects.TargetSpec spec, CardInstance t, int sourceController,
        PlayerManager pm = null)
    {
        if (spec == null || t == null) return false;
        // 粗筛大类（与 MatchesRule 同一互斥映射）；【K14】`KindAlt` 非 None 时与 `Kind` 构成 OR
        var kind = MapInstanceToTargetKind(t);
        if (kind != spec.Kind && (spec.KindAlt == TargetKind.None || kind != spec.KindAlt)) return false;
        // 精筛条件（全部满足）——与结算复检共用 CheckRestrictions（单一真相源）
        return Script.Manager.TargetValidity.CheckRestrictions(
            spec.Restrictions, spec.Param, t, sourceController, pm);
    }

    /// <summary>
    /// 单条 TargetRule 匹配：先粗筛大类(Kind)，再要求所有 Restrictions 满足(AND)。
    /// sourceController = 打出者/激活者控制者，用于 Friendly/Enemy 判定。
    /// 注意：秘社(Council)实例的 PlayerTemplate 为 null，需用 CouncilTemplate / CurrentZone 判断。
    ///
    /// 【方案 B·单一真相源，2026-08-11】精筛条件已下沉到 TargetValidity.CheckRestrictions，
    /// 与结算复检（TargetValidity.Check）共用同一份实现：能选中的目标与结算时仍合法的目标判定一致，
    /// 新增限制只需改 CheckRestrictions 一处。本方法只保留"取目标阶段"特有的大类粗筛。
    /// 注：LocalZone 由 CheckRestrictions 跳过；取目标阶段的本地区筛选目前由
    /// TurnManager.GetLocalZoneTargets 按地区槽位遍历实现，不经本方法（tres 未配置 LocalZone）。
    /// </summary>
    /// <param name="pm">本对局的 PlayerManager（多对局隔离）；null 时该链路退回进程级静态兜底。</param>
    public static bool MatchesRule(Script.Data.Effects.TargetRule rule, CardInstance t, int sourceController,
        PlayerManager pm = null)
    {
        if (rule == null || t == null) return false;
        // 1. 粗筛大类（取目标阶段专用：MapInstanceToTargetKind 为互斥映射）
        if (MapInstanceToTargetKind(t) != rule.Kind) return false;
        // 2. 精筛条件（全部满足）——与结算复检共用
        return Script.Manager.TargetValidity.CheckRestrictions(rule.Restrictions, rule.Param, t, sourceController, pm);
    }
}
