using Godot;
using System;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using System.Collections.Generic;

public partial class CardManager : Node
{
    // ============================================================
    // 单例
    // ============================================================
    public static CardManager Instance { get; private set; }

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
    /// </summary>
    public static event Action<CardInstance, CardChangeKind> OnCardStateChanged;

    /// <summary>
    /// 触发 OnCardStateChanged（仅供 CardManager 及状态中枢方法调用，避免外部直接 Invoke 事件）。
    /// </summary>
    public static void NotifyCardStateChanged(CardInstance card, CardChangeKind kind)
        => OnCardStateChanged?.Invoke(card, kind);

    /// <summary>
    /// 选目标高光总线（2026-08-07 收口）：进入/退出"选目标"交互时广播合法目标卡 UID 集合。
    /// 参数为合法目标 UID 列表；传 null 或空列表表示清除所有高光（用于窗口打开前清残留、关闭后统一取消）。
    /// View（Arena）订阅后读此集合重绘可选目标黄色高光，Controller 不再直接操作 CardUI 节点（遵循 MVC 红线）。
    /// </summary>
    public static event Action<List<string>> OnTargetSelectionChanged;

    /// <summary>
    /// 触发 OnTargetSelectionChanged（仅供 Controller 协调器调用；View 只读集合重绘，不改 Model）。
    /// </summary>
    public static void NotifyTargetSelectionChanged(List<string> uids)
        => OnTargetSelectionChanged?.Invoke(uids);

    // ============================================================
    // 内部数据
    // ============================================================
    private Dictionary<string, CardInstance> _allInstances = new Dictionary<string, CardInstance>();
    private int _nextUID = 1;

    // 区域列表统一由 ZoneManager 管理（单一真相源），本类仅保留实例注册与伤害处理
    private ZoneManager _zoneManager;

    public void SetZoneManager(ZoneManager zoneManager) => _zoneManager = zoneManager;

    public override void _Ready()
    {
        Instance = this;
    }

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 在 EffectEngine.ResetAll 之后调用（先清空静态效果注册表，
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
    {
        GD.Print($"[CardManager] 卡牌死亡: {instance.PlayerTemplate?.CardName} (UID: {instance.UID})");
        MoveCardToGraveyard(instance);   // 委托 ZoneManager 处理区域移动（含 CurrentZone 修改）
        instance.OnDeath?.Invoke(instance);  // 转发 Model 的纯数据死亡事件（供其他订阅方）
        OnCardDied?.Invoke(instance);
    }

    // ============================================================
    // 计数器（Controller 层规则判定：被封印的卡不可加指示物）
    // ============================================================
    public void AddCounter(CardInstance instance, string type, int amount = 1)
    {
        if (instance == null) return;
        if (instance.IsSealed) return;   // 规则上移：封印守卫由 Controller 负责
        instance.AddCounter(type, amount);
    }

    public void RemoveCounter(CardInstance instance, string type, int amount = 1)
    {
        if (instance == null) return;
        instance.RemoveCounter(type, amount);
    }

    // ============================================================
    // 移动（委托 ZoneManager）
    // ============================================================
    public void MoveCardToHand(CardInstance instance)
        => _zoneManager?.MoveToHand(instance, instance.OwnerId);

    /// <summary>
    /// 退回手牌（MVC 收口）：先由 Controller 重置实例基础状态，再委托 ZoneManager 移动。
    /// 原实现中 View（Arena.ReturnCardToHand）直接调用 instance.ResetToBaseState() 修改 Model，
    /// 现已收口至此，确保 Model 状态变更只经 Controller 发生。
    /// </summary>
    public void ReturnCardToHand(CardInstance instance)
    {
        if (instance == null) return;
        Script.Manager.EffectEngine.UnregisterContinuous(instance); // P1：离场还原持续效果
        instance.ResetToBaseState();   // 规则/状态重置归属 Controller，不暴露给 View
        _zoneManager?.MoveToHand(instance, instance.OwnerId);
    }

    public void MoveCardToArea(CardInstance instance, string areaDetailId)
        // D32 修正：机动移动按控制者归属（ControllerId）作为地区归属玩家，
        // 确保被夺控的 JC014 移动后仍停留在控制者争夺区、不篡改原 Owner 语义。
        => _zoneManager?.MoveToArea(instance, areaDetailId, instance.ControllerId);

    /// <summary>
    /// 暗藏者现身入栈：将卡从当前区移入堆叠区（不重置状态，卡已在入栈前翻面为正面）。
    /// 按控制者(ControllerId)归属堆叠区玩家，与 MoveCardToArea 的归属语义一致（D32）。
    /// 入栈期间 CurrentZone=Stack，结算时 MoveToArea 会因 fromZone!=AreaZone 自然触发 EnterPlay。
    /// </summary>
    public void MoveCardToStack(CardInstance instance)
        => _zoneManager?.MoveToStack(instance, instance.ControllerId);

    /// <summary>
    /// 暗藏者"现身"（Reveal，背面→正面揭示）：规范化行为封装。
    /// 不移动地区（卡仍在原争夺区），但按规则需触发进场类效果：
    ///   1) 复位为基础状态并解除暗藏（IsCovert=false，与正常打出一致）；
    ///   2) 广播 EnterPlay  → 触发"进场触发(EnterTrigger)"；
    ///   3) 广播 Uncover    → 触发"现身触发(UncoverTrigger)"；
    /// 两类时机经 TriggerRegistry 分别收集（D37+：现身复用进场触发链 + Uncover 时机）。
    /// 注：争夺区→争夺区移动（机动/夺控）不触发本行为，详见 ZoneManager.MoveToArea。
    /// </summary>
    public void RevealCard(CardInstance instance)
    {
        if (instance == null) return;
        if (!instance.IsCovert) return;   // 本就正面/非暗藏，无需现身（幂等保护，避免重复触发）

        instance.ResetToBaseState();       // 复位基础属性 + IsCovert=false（见 CardInstance.ResetToBaseState）
        string areaId = instance.ZoneDetailId ?? "";
        // 现身 = 复用进场触发链 + 广播现身触发（D39 薄封装）
        Script.Manager.TriggerEmitPoints.EnterPlay(instance, instance.ControllerId, areaId);
        Script.Manager.TriggerEmitPoints.Uncover(instance, instance.ControllerId, areaId);
    }

    public void MoveCardToGraveyard(CardInstance instance)
    {
        if (instance == null) return;
        Script.Manager.EffectEngine.UnregisterContinuous(instance); // P1：离场还原持续效果
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
    public void DealDamageToInstance(CardInstance instance, int amount, bool isTrauma = false)
    {
        if (instance == null || !instance.HasDefense) return;
        // 暗藏者(IsCovert)潜伏状态，免疫一切伤害（含后续效果系统造成的伤害）：
        // 统一在此收口，避免任何伤害来源绕过战斗窗口的承伤判定伤到暗藏者。
        if (instance.IsCovert) return;
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

    /// <summary>重置步骤：解除所有区域（争夺区/手牌/资产/结附卡）卡牌的横置。
    /// 结附卡（AttachedZone，含结附于角色与结附于秘社的附属、封印卡）此前漏重置，
    /// 现统一纳入，使已横置的附属卡（如 JC012 帷幕守望徽记）在回合开始随其他卡一起还原。</summary>
    public void UntapAll()
    {
        if (_zoneManager == null) return;
        int count = 0;
        foreach (var zone in new[] { CardZone.AreaZone, CardZone.Hand, CardZone.AssetZone, CardZone.AttachedZone })
            foreach (var c in _zoneManager.GetZoneCards(zone, 0))
                if (c.IsTapped) { c.SetTapped(false); count++; }   // SetTapped 发 TappedChanged（阿格斯IV竖置恢复封锁）
        GD.Print($"🔓 重置步骤：解除 {count} 张卡牌横置（含资产、结附卡，费用恢复）");
    }

    // ============================================================
    // 打出目标合法性（MVC 收口-P3）：原在 View(Arena) 内嵌的规则粗筛，上移至 Controller
    // ============================================================

    /// <summary>把场上卡实例映射为打出/激活目标种类（粗筛）。public 供激活框架复用。</summary>
    public static TargetKind MapInstanceToTargetKind(CardInstance inst)
    {
        if (inst == null) return TargetKind.None;
        if (inst.IsCovert) return TargetKind.Covert;          // 暗藏者
        if (inst.IsAttached) return TargetKind.Attachment;     // 附属
        // 秘社（CouncilCardData，承载于 CouncilZone）：即便 PlayerTemplate 为 null 也要识别
        if (inst.CurrentZone == CardZone.CouncilZone || inst.CouncilTemplate != null) return TargetKind.Council;
        if ((inst.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0) return TargetKind.Character;
        if ((inst.PlayerTemplate?.CategoryFlag & CardCategory.Attachment) != 0) return TargetKind.Attachment;
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
        if (dragging.CurrentSubCategories.Contains("装备")
            && Script.Manager.EffectEngine.HasStaticFlag(target, StaticKind.CannotBeAttached))
        {
            GD.Print($"⛔ [目标拦截] 装备 {tmpl.CardName} 不能以 {target.PlayerTemplate?.CardName} 为目标（不能被装备结附）");
            return false;
        }

        // 方案 A：逐条 TargetRule 裁决。满足【任一】规则即合法（OR）。
        int sourceController = dragging.ControllerId;   // 打出者/来源方
        if (tmpl.TargetRules != null)
        {
            foreach (var rule in tmpl.TargetRules)
                if (rule != null && MatchesRule(rule, target, sourceController))
                    return true;
        }
        return false;
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
    public static bool MatchesRule(Script.Data.Effects.TargetRule rule, CardInstance t, int sourceController)
    {
        if (rule == null || t == null) return false;
        // 1. 粗筛大类（取目标阶段专用：MapInstanceToTargetKind 为互斥映射）
        if (MapInstanceToTargetKind(t) != rule.Kind) return false;
        // 2. 精筛条件（全部满足）——与结算复检共用
        return Script.Manager.TargetValidity.CheckRestrictions(rule.Restrictions, rule.Param, t, sourceController);
    }
}
