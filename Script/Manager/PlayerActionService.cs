using System.Collections.Generic;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Game.Entities;
using Script.Manager;

/// <summary>
/// PlayerActionService —— 玩家意图统一通道（命令总线雏形）。
///
/// 职责：收口所有"玩家主动意图（写操作/命令）"，View 层不再直接点名具体 Manager 方法，
/// 而是经本服务表达意图。这样：
///   1) 所有玩家意图集中在一处，便于审计/重放；
///   2) 为将来 C/S 化提供单一序列化入口（只需在此层拦截意图并下发服务器）；
///   3) View 与具体 Controller 方法解耦，降低散落耦合。
///
/// 设计约束（与 MVC / C/S 预留一致）：
///   - 仅依赖 Manager 层，绝不引用任何 UI 类型（CardUI / Arena 等）；
///   - 仅承载"命令"，不承载"查询/事件订阅"（查询仍由 View 直连 Manager 只读，事件仍由 View 订阅）。
/// </summary>
public class PlayerActionService
{
    private readonly TurnManager _turnManager;
    private readonly CardManager _cardManager;
    private readonly ZoneManager _zoneManager;
    private readonly AreaManager _areaManager;
    private readonly PlayerManager _playerManager;

    public PlayerActionService(TurnManager turnManager, CardManager cardManager,
                               ZoneManager zoneManager, AreaManager areaManager,
                               PlayerManager playerManager)
    {
        _turnManager = turnManager;
        _cardManager = cardManager;
        _zoneManager = zoneManager;
        _areaManager = areaManager;
        _playerManager = playerManager;
    }

    /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
    public Script.Manager.EffectEngine EffectEngineRef { get; set; }

    /// <summary>未显式注入时回退静态 Instance（向后兼容）。</summary>
    private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;

    // ---------- 回合意图 ----------
    public void Pass(int playerId) => _turnManager.Pass(playerId);

    /// <summary>【第0回合·调度】客户端上报调度选择（要换掉的手牌 UID；可空）。</summary>
    public void MulliganReport(int playerId, System.Collections.Generic.IList<string> pickedUids)
        => _turnManager.ReportMulligan(playerId,
            pickedUids == null ? null : new System.Collections.Generic.List<string>(pickedUids));

    // ---------- 出牌意图 ----------
    public bool PlayCard(int playerId, string cardUid, string targetUid = null, string targetCardUid = null,
        string extraSacrificeUid = null, System.Collections.Generic.IList<string> extraRevealUids = null)
        => _turnManager.PlayCard(playerId, cardUid, targetUid, targetCardUid,
            null, -1, extraSacrificeUid, extraRevealUids);

    /// <summary>【K10·额外费用】该卡打出前是否需要**玩家做选择**（目前＝"牺牲一个角色"，JC049）。
    /// 候选/可支付性由 Controller 单一实现；View 据此决定是否先弹选择窗。</summary>
    public bool NeedsExtraCostPlayerChoice(Script.Data.Cards.PlayerCardData tmpl)
        => _turnManager.NeedsExtraCostPlayerChoice(tmpl);

    /// <summary>【K10·额外费用】"牺牲一个角色"作**打出费用**的候选（与权威侧同一实现，
    /// 见 `EffectEngine.GetSacrificeCandidates`）。</summary>
    public System.Collections.Generic.List<Script.Game.Entities.CardInstance> GetExtraCostSacrificeCandidates(
        Script.Game.Entities.CardInstance card)
        => _turnManager.GetExtraCostSacrificeCandidates(card);

    /// <summary>【K10·额外费用】该卡是否有"**展示手牌减费**"这一类可选额外费用（JC081）：
    /// View 据此弹"手牌多选 → 确认/取消"窗（取消＝不打出、不扣任何费用）。</summary>
    public bool NeedsExtraCostRevealChoice(Script.Data.Cards.PlayerCardData tmpl)
        => _turnManager.NeedsExtraCostRevealChoice(tmpl);

    /// <summary>【K10·额外费用】展示手牌的候选（＝打出者当前手牌中**除本卡自身以外**的牌）。</summary>
    public System.Collections.Generic.List<Script.Game.Entities.CardInstance> GetExtraCostRevealCandidates(
        int playerId, Script.Game.Entities.CardInstance source)
        => _turnManager.GetExtraCostRevealCandidates(playerId, source);

    /// <summary>【K10·额外费用】某张手牌是否算"合格展示牌"（命中 `RevealFilter`；JC081＝组织:圣贤 或 神圣领域）。</summary>
    public bool IsQualifiedRevealCard(Script.Data.Cards.PlayerCardData tmpl, Script.Game.Entities.CardInstance handCard)
        => _turnManager.IsQualifiedRevealCard(tmpl, handCard);

    /// <summary>【K15·多目标】单机"多目标事务卡"打出意图：逐槽取目标 + 确认面板 → 确认后由 Controller 入堆叠。</summary>
    public void BeginMultiTargetPlay(int playerId, string cardUid)
        => _turnManager.BeginMultiTargetPlay(playerId, cardUid);

    /// <summary>【K16·模式分支】单机"选择一项"卡牌打出意图：先选定模式（付费前）→ 取该模式目标 → 入堆叠。</summary>
    public void BeginModeCardPlay(int playerId, string cardUid)
        => _turnManager.BeginModeCardPlay(playerId, cardUid);

    /// <summary>
    /// 【2026-09 用户口径 JC062/JC063】**正在进行"打出前选择流程"的卡**（模式/多目标/额外费用已由服务端接管，
    /// 正等玩家选择）：期间该卡**不能再被打出一次**（用户实测："打出后…还在手牌，**可以再次打出**"）。
    /// 由三个 `TryBegin*Online` 登记、异步流程结束后自动清除。
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _pendingPlayCardUids
        = new System.Collections.Generic.HashSet<string>();

    /// <summary>该卡是否已在一次"打出前选择流程"中（服务端接管中）。</summary>
    public bool IsPlayFlowPending(string cardUid)
        => !string.IsNullOrEmpty(cardUid) && _pendingPlayCardUids.Contains(cardUid);

    /// <summary>登记"打出前选择流程"，并在流程结束（成功入堆叠或取消）后自动解除登记。</summary>
    private async System.Threading.Tasks.Task RunPendingPlayFlow(string cardUid,
        System.Threading.Tasks.Task<bool> flow)
    {
        try { await flow; }
        finally { if (!string.IsNullOrEmpty(cardUid)) _pendingPlayCardUids.Remove(cardUid); }
    }

    /// <summary>
    /// 【K16·模式分支·联机】服务端权威路径：若该卡声明了 `Modes` 且已注入输入网关 → 由服务端驱动
    /// "选模式 → 选目标 → 入堆叠"（异步；期间持原子锁）。返回 true＝已接管本次打出。
    /// </summary>
    public bool TryBeginModeCardPlayOnline(int playerId, string cardUid)
    {
        var inst = _cardManager.GetInstance(cardUid);
        var effect = Script.Manager.MultiTargetSlots.FindModeEffect(
                                 inst?.PlayerTemplate, Script.Data.Effects.EffectTiming.OnSorceryCast)
                             ?? Script.Manager.MultiTargetSlots.PlayChoiceEffect(inst?.PlayerTemplate);   // 【JC082】打出时"选择一项"
        if (effect == null || _turnManager.InputGateway == null) return false;
        if (IsPlayFlowPending(cardUid))
        {
            Godot.GD.Print($"[K16 模式·联机] ⛔ {inst?.PlayerTemplate?.CardName} 已在一次打出前选择流程中 → 忽略重复打出");
            return true;
        }
        Godot.GD.Print($"[K16 模式·联机] 服务端接管 {inst?.PlayerTemplate?.CardName} 的打出流程（选模式 → 选目标 → 入堆叠）");
        _pendingPlayCardUids.Add(cardUid);
        _ = RunPendingPlayFlow(cardUid, _turnManager.PlayModeCardAsync(playerId, cardUid));
        return true;
    }

    /// <summary>
    /// 【K15·多目标·联机】服务端权威路径：若该卡声明了多目标槽位且已注入输入网关，
    /// 则**由服务端驱动**取目标 → 入堆叠（异步；期间持原子锁）。
    /// 【2026-09 用户口径 JC062/JC021】形状为 `FieldMultiPick`（全槽＝地区槽或"场上看得见的卡槽"）
    /// ⇒ **一次性聚合下发** `Kind.FieldMultiPick`（"一个窗 + 场上高亮 + 左键选择/右键取消 + 确认/取消"，
    /// 该请求的确认即最终确认，**不再另发逐槽请求与确认面板**）；其余形状（含区域牌堆槽，如 JC092/JC133）
    /// 仍走"逐槽 `MultiTargetSlot` → `ConfirmTargets`"。
    /// 返回 true＝已接管本次打出（调用方不要再走同步 `PlayCard`）；false＝按普通卡处理。
    /// </summary>
    public bool TryBeginMultiTargetPlayOnline(int playerId, string cardUid)
    {
        var inst = _cardManager.GetInstance(cardUid);
        var effect = Script.Manager.MultiTargetSlots.FindMultiTargetEffect(
            inst?.PlayerTemplate, Script.Data.Effects.EffectTiming.OnSorceryCast);
        if (effect == null || _turnManager.InputGateway == null) return false;
        bool fieldMultiPick = Script.Manager.MultiTargetSlots.ClassifyShape(effect)
                              == Script.Manager.MultiTargetSlots.MultiSlotShape.FieldMultiPick;
        if (fieldMultiPick)
        {
            if (IsPlayFlowPending(cardUid))
            {
                Godot.GD.Print($"[K15 多目标·联机] ⛔ {inst?.PlayerTemplate?.CardName} 已在一次打出前选择流程中 → 忽略重复打出");
                return true;
            }
            Godot.GD.Print($"[K15 多目标·联机] 服务端接管 {inst?.PlayerTemplate?.CardName} 的打出流程（**一个窗多选**聚合请求）");
        }
        else
        {
            Godot.GD.Print($"[K15 多目标·联机] 服务端接管 {inst?.PlayerTemplate?.CardName} 的打出流程（逐槽请求 + 确认面板）");
        }
        _pendingPlayCardUids.Add(cardUid);
        _ = RunPendingPlayFlow(cardUid, _turnManager.PlayMultiTargetCardAsync(playerId, cardUid));
        return true;
    }

    /// <summary>
    /// 【K10·额外费用·联机】服务端权威路径：若该卡声明了**需要玩家本人选择**的额外费用
    /// （JC049「牺牲一个角色」/ JC081「展示任意数量手牌」）且已注入输入网关 → 由服务端把选择
    /// **回环给该玩家**（`Kind.SelectTarget` / `Kind.SelectHandCards`）后再续算（异步；期间持原子锁）。
    /// 返回 true＝已接管本次打出（调用方不要再走同步 `PlayCard`）；false＝按普通卡处理。
    /// </summary>
    public bool TryBeginExtraCostPlayOnline(int playerId, string cardUid, string targetUid, string targetCardUid)
    {
        var inst = _cardManager.GetInstance(cardUid);
        var tmpl = inst?.PlayerTemplate;
        if (tmpl?.ExtraCost == null || _turnManager.InputGateway == null) return false;
        bool needsChoice = _turnManager.NeedsExtraCostPlayerChoice(tmpl) || _turnManager.NeedsExtraCostRevealChoice(tmpl);
        if (!needsChoice) return false;
        if (IsPlayFlowPending(cardUid))
        {
            Godot.GD.Print($"[K10 额外费用·联机] ⛔ {tmpl.CardName} 已在一次打出前选择流程中 → 忽略重复打出");
            return true;
        }
        Godot.GD.Print($"[K10 额外费用·联机] 服务端接管 {tmpl.CardName} 的打出流程（额外费用选择回环给玩家{playerId}）");
        _pendingPlayCardUids.Add(cardUid);
        _ = RunPendingPlayFlow(cardUid, _turnManager.PlayWithExtraCostAsync(playerId, cardUid, targetUid, targetCardUid));
        return true;
    }

    // ---------- 暗藏 / 资产意图 ----------
    public bool DeployCovert(int playerId, string cardUid, string areaUid)
        => _turnManager.DeployCovert(playerId, cardUid, areaUid);

    // ---------- 暗藏者现身意图（快速能力）----------
    public bool RevealCovert(int playerId, string cardUid,
        string extraSacrificeUid = null, System.Collections.Generic.IList<string> extraRevealUids = null)
        => _turnManager.RevealCovert(playerId, cardUid, extraSacrificeUid, extraRevealUids);

    /// <summary>
    /// 【K10·联机】现身时的"额外费用选择"接管：该暗藏者声明了 `ExtraCost` 时，由服务端经网关收集选择
    /// （`RevealHandCards`→`SelectHandCards` 多选可取消；`SacrificeCharacter`→`SelectTarget` 单选），
    /// 收集完再调 `RevealCovert`（其内部共用同一份 `TryResolveExtraCost` 校验）。
    /// 返回 true＝已接管（调用方不要再走同步 `RevealCovert`）；false＝无额外费用或未注入网关。
    /// </summary>
    public bool TryBeginRevealCovertExtraCostOnline(int playerId, string cardUid)
    {
        if (_turnManager.InputGateway == null) return false;
        var inst = _cardManager.GetInstance(cardUid);
        var tmpl = inst?.PlayerTemplate;
        if (tmpl?.ExtraCost == null || tmpl.ExtraCost.Kind == ExtraCostKind.None) return false;
        Godot.GD.Print($"[K10 额外费用·联机] 服务端接管 {tmpl.CardName} 的**现身**流程（额外费用选择回环给玩家{playerId}）");
        _ = _turnManager.RevealCovertWithExtraCostAsync(playerId, cardUid);
        return true;
    }

    public bool EstablishAsset(int playerId, string cardUid)
        => _turnManager.EstablishAsset(playerId, cardUid);

    // ---------- 牌库意图 ----------
    public void ReturnCardToDeck(int playerId, string cardUid, bool toTop = false)
        => _turnManager.ReturnCardToDeck(playerId, cardUid, toTop);

    // ---------- 手牌退回意图（拖拽取消/回退手牌）----------
    /// <summary>把指定卡退回手牌（重置基础状态 + 移动），由 Controller(CardManager) 统一收口。</summary>
    public void ReturnCardToHand(int playerId, string cardUid)
    {
        var inst = _cardManager.GetInstance(cardUid);
        if (inst == null) return;
        _cardManager.ReturnCardToHand(inst);
    }

    // ---------- 抽牌意图（如调查对抗补抽回调）----------
    public void DrawCards(int playerId, int count)
        => _turnManager.DrawCardsForPlayer(playerId, count);

    // ---------- 伤害意图（战斗窗口）----------
    public void DealDamage(CardInstance instance, int amount, bool isTrauma = false)
        => _cardManager.DealDamageToInstance(instance, amount, isTrauma);

    /// <summary>
    ///【MVC 修复 M-05】对某地区槽位内的全部角色造成伤害/创伤（群体伤害单一意图）。
    /// 原先由 View 自行 GetAreaInstancesBySlot 取出 Model 集合再逐个循环发伤害，
    /// 属"表现层遍历 Model 执行领域操作"；现收口为一个具备完整语义的意图，
    /// 调用方只表达"打这个地区"，遍历与结算全部留在 Controller 内。
    /// </summary>
    /// <returns>实际被结算的目标数量（供调用方输出日志/刷新）。</returns>
    public int DealAreaDamage(string areaUid, int amount, bool isTrauma = false)
    {
        if (string.IsNullOrEmpty(areaUid) || amount <= 0) return 0;
        var instances = _cardManager.GetAreaInstancesBySlot(areaUid);
        foreach (var inst in instances)
            _cardManager.DealDamageToInstance(inst, amount, isTrauma);
        return instances.Count;
    }

    // ---------- 对局生命周期意图 ----------
    /// <summary>开始对局（由 View 完成场景装配后触发）。</summary>
    public void StartGame() => _turnManager.StartGame();

    /// <summary>刷新先制统计（地区槽位变更后需重算）。</summary>
    public void RefreshFirstStrikeStats() => _turnManager.RefreshFirstStrikeStats();

    // ---------- 调查对抗意图 ----------
    /// <summary>取出牌库顶 count 张进入调查临时态。</summary>
    public List<CardInstance> TakeTopForInvestigation(int playerId, int count)
        => _zoneManager.TakeTopForInvestigation(playerId, count);

    /// <summary>调查确认后按玩家选择放回牌库顶/底。</summary>
    public void ReturnAfterInvestigation(int playerId, List<CardInstance> toTop, List<CardInstance> toBottom)
        => _zoneManager.ReturnAfterInvestigation(playerId, toTop, toBottom);

    // ---------- 地区槽位意图 ----------
    /// <summary>抽取初始地区（返回地区卡实例）。</summary>
    public List<CardInstance> DrawInitialAreas(int count = 3)
        => _areaManager.DrawInitialAreas(count);

    /// <summary>从世界牌库抽出一张新地区（返回实例）。</summary>
    public CardInstance DrawNewArea() => _areaManager.DrawNewArea();

    /// <summary>登记地区槽位到逻辑注册表（实例驱动）。</summary>
    public void RegisterArea(string areaUid, int slotIndex, CardInstance instance)
        => _areaManager.RegisterArea(areaUid, slotIndex, instance);

    /// <summary>抽取并登记初始地区（DrawInitialAreas + RegisterArea 收口为单一意图），返回地区实例列表。
    /// 供单机 Arena 与联机服务端 NetMatchEntry 共用，避免两处重复"抽地区→注册槽位"逻辑。</summary>
    public List<CardInstance> SetupInitialAreas(int count = 3)
    {
        var areas = _areaManager.DrawInitialAreas(count);
        for (int i = 0; i < areas.Count; i++)
            _areaManager.RegisterArea(areas[i].UID, i + 1, areas[i]);
        return areas;
    }

    /// <summary>注销地区槽位。</summary>
    public void UnregisterArea(string areaUid) => _areaManager.UnregisterArea(areaUid);

    /// <summary>地区移除后顺移其后槽位索引。</summary>
    public void ShiftSlotIndicesFrom(int removedSlotIndex)
        => _areaManager.ShiftSlotIndicesFrom(removedSlotIndex);

    // ---------- 玩家判负 / 能力发动 / 行动次数 意图收口 ----------
    // 【MVC 修复】以下三类原由 View 直连具体 Manager 调用（绕过命令总线），
    // 现统一收口到本服务，满足 C/S 化"玩家意图 100% 经单一入口"的硬性条件。

    /// <summary>玩家投降：判 loserId 失败、对手获胜。原由 Arena 直接调用 PlayerManager.Concede。</summary>
    public void Concede(int loserId) => _playerManager.Concede(loserId);

    /// <summary>
    /// 发动角色/附属/秘社激活能力（作为"行动"或"快速"窗口入堆叠）。
    /// 原由 Arena / CardDragController 直接调用 EffectEngine.Instance.ActivateAbility（静态执行体）。
    /// 此处透传参数，规则门禁（原子锁/优先权/窗口校验/堆叠熔断）仍由 EffectEngine 内部负责。
    /// </summary>
    public void ActivateAbility(CardInstance source, EffectData data, CardInstance target)
        => EE.ActivateAbility(source, data, target);

    /// <summary>
    /// 【P2·联机】**激活的"选择类"入参接管**（与单机那几套窗 **1:1** 对应）：
    /// 单机在 View 侧先弹窗收集"目标地区 / 牺牲角色 / 标志代价（固定数量或逐卡分配）/ 目标玩家"，
    /// 再把结果作为参数传给 `ActivateAbility`。联机侧服务端**没有 View**，故改为**服务端接管**：
    /// 由服务端用**同一批规则函数**算出候选，经 `ChoiceRequest` 下发（客户端用与单机同款的
    /// 地区高亮窗 / `OnlineActivationChoiceWindow` 按钮窗 / 玩家选择窗渲染），回包校验后再激活。
    /// 返回 true＝已接管（调用方不要再走同步 `ActivateAbility`）；false＝本能力无选择类入参或未注入网关。
    /// </summary>
    public bool TryBeginActivationChoiceOnline(int playerId, string sourceUid, string effectId,
        string targetUid, int targetPlayerId)
    {
        if (_turnManager.InputGateway == null) return false;
        var inst = _cardManager.GetInstance(sourceUid);
        var ability = FindActivationAbility(inst, effectId);
        if (inst == null || ability == null) return false;

        bool hasChoice = ability.NeedsAreaTarget
                         || ability.SacrificeCost != null
                         || ability.CounterCost != null
                         || Script.Manager.PlayerTargetPool.IsPlayerSpec(ability.Target);
        if (!hasChoice) return false;

        Godot.GD.Print($"[K15/K22/K5/K24·联机] 服务端接管 {inst.PlayerTemplate?.CardName ?? inst.CouncilTemplate?.CardName} 的激活流程（{ability.EffectId}：目标地区/代价/玩家目标）");
        _ = _turnManager.ActivateAbilityWithChoicesAsync(playerId, sourceUid, effectId, targetUid, targetPlayerId);
        return true;
    }

    /// <summary>按 EffectId 找该实例（玩家卡或秘社卡）的激活类效果（找不到 → null）。</summary>
    private static EffectData FindActivationAbility(CardInstance inst, string effectId)
    {
        if (inst == null || string.IsNullOrEmpty(effectId)) return null;
        if (inst.PlayerTemplate?.EffectDataList != null)
        {
            foreach (var e in inst.PlayerTemplate.EffectDataList)
                if (e != null && e.EffectId == effectId
                    && (e.Timing == Script.Data.Effects.EffectTiming.CharacterActivated
                        || e.Timing == Script.Data.Effects.EffectTiming.AttachmentActivated)) return e;
        }
        if (inst.CouncilTemplate?.ActivationOptions != null)
        {
            foreach (var opt in inst.CouncilTemplate.ActivationOptions)
            {
                if (opt?.EffectDataList == null) continue;
                foreach (var e in opt.EffectDataList)
                    if (e != null && e.EffectId == effectId
                        && e.Timing == Script.Data.Effects.EffectTiming.CouncilActivated) return e;
            }
        }
        return null;
    }

    /// <summary>
    /// 【K24·玩家目标】发动"以玩家为目标"的激活能力（如 JC111 混沌蝴蝶的两条快速行动）：
    /// 目标由 View 的**独立选择窗口**收集后作为 `targetPlayerId` 传入，与卡目标参数互不冲突。
    /// </summary>
    public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId)
        => EE.ActivateAbility(source, data, target, targetPlayerId);

    /// <summary>
    /// 【K22】发动带"**牺牲一个角色**"代价的激活能力：`sacrificeUid` ＝**发动者自己选择**的被牺牲角色
    /// （由 View 的选择窗口收集；空 → 引擎走确定性兜底第一个合法候选）。
    /// 代价合法性与支付（含"牺牲＝死亡 → 触发死亡时机"）仍在 Controller（`EffectEngine`）收口。
    /// </summary>
    public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
        string sacrificeUid)
        => EE.ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, null);

    /// <summary>
    /// 【K15】发动需要"**目标地区**"的激活能力（JC117 移动到目标地区 / JC033① 置于目标地区）：
    /// 	argetAreaId 由 View 的地区选择窗收集，入堆叠时锁定。
    /// </summary>
    public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
        string sacrificeUid, string targetAreaId)
        => EE.ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, targetAreaId);

    /// <summary>【K5·JC044②】发动带"移除**固定数量**标志"代价的激活能力：`counterCostUid` ＝发动者选定的那张卡。</summary>
    public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
        string sacrificeUid, string targetAreaId, string counterCostUid)
        => EE.ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, targetAreaId, counterCostUid);

    /// <summary>
    /// 【K5·JC055②】发动带"**自本方卡牌上移除 X 个标志**"代价的激活能力：`allocation` ＝ 卡 UID → 移除数量
    /// （由 View 的逐卡分配窗收集，**总和 ≥ 1**）。代价合法性与支付（含"X 作为结算值下发"）在 Controller 收口。
    /// </summary>
    public void ActivateAbility(CardInstance source, EffectData data, CardInstance target, int targetPlayerId,
        string sacrificeUid, string targetAreaId, System.Collections.Generic.Dictionary<string, int> counterAllocation)
        => EE.ActivateAbility(source, data, target, targetPlayerId, sacrificeUid, targetAreaId, null, counterAllocation);

    /// <summary>【K5·JC055②】"移除标志"代价的**可分配候选**（本方场上带该标志 ≥1 的卡）。</summary>
    public System.Collections.Generic.List<CardInstance> GetCounterCostChoiceCandidates(CardInstance source, EffectData ability)
        => ability?.CounterCost == null
            ? new System.Collections.Generic.List<CardInstance>()
            : (EE.GetCounterCostCandidates(source, ability.CounterCost) ?? new System.Collections.Generic.List<CardInstance>());

    /// <summary>【K5·JC055②】某卡上"可作代价"的标志数（View 显示"共 n 个"用；与支付实现同源）。</summary>
    public int GetMarkerCountOnCard(CardInstance card, EffectData ability)
        => EE.GetMarkerCountOnCard(card, ability?.CounterCost);

    /// <summary>回合开始重置玩家基础行动次数（派遣暗藏/建立资产/先手破平 各 1）。原由 Arena 直接调用 PlayerManager.ResetActionCharges。</summary>
    public void ResetActionCharges(int playerId) => _playerManager.ResetActionCharges(playerId);
}
