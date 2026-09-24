using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Data.Effects;

/// <summary>
/// AreaManager - 地区管理器（纯逻辑层）
/// 职责：管理世界牌库、地区生命周期（创建/赢取/替换）、势力标记
/// 不直接持有或操作任何 UI 节点；所有 UI 刷新通过事件通知 Arena 订阅。
/// </summary>
public partial class AreaManager : Node
{
    // ============================================================
    // 事件（供 Arena UI 订阅，参数均为纯数据，不泄漏 UI 类型）
    // ============================================================
    /// <summary>地区被赢取：携带地区 UID 与是否为任务地区（UI 层处理得分区移动等）。
    /// 若是普通地区被替换，新地区数据通过 OnAreaReplaced 事件外抛。</summary>
    public event Action<string, bool> OnAreaWon;
    /// <summary>普通地区被替换为新地区：携带旧地区 UID 与新地区实例（UI 层负责刷新 AreaSlot 显示）</summary>
    public event Action<string, CardInstance> OnAreaReplaced;
    /// <summary>地区从战场移除（任务地区赢取或牌库空）：携带地区索引（1-based）</summary>
    public event Action<int> OnAreaRemoved;
    /// <summary>需要刷新世界牌库显示</summary>
    public event Action OnWorldDeckChanged;
    /// <summary>某地区势力标记变化：携带地区 UID 与双方标记数（UI 层刷新对应 AreaSlot）</summary>
    public event Action<string, int, int> OnMarksChanged;

    /// <summary>基础地区赢取完成（5步结算后）：携带 (areaUid, winnerId, scoreGained)，供 UI 刷新计分区与总分显示</summary>
    public event Action<string, int, int> OnAreaWonResolved;

    // ============================================================
    // 依赖
    // ============================================================
    private WorldDeckManager _worldDeckManager;
    private ZoneManager _zoneManager;
    private StackManager _stackManager;

    /// <summary>【P1】触发广播封装层（由 GameInitializer 注入）：用于 OnAreaWon / OnMarkerPlaced 发点。</summary>
    private Script.Manager.TriggerEmitPoints _triggerEmitPoints;
    public void SetTriggerEmitPoints(Script.Manager.TriggerEmitPoints emitPoints) => _triggerEmitPoints = emitPoints;
    private CardManager _cardManager; // 跨进程投影：ApplySnapshot 用 RebuildFromRef 重建地区卡实例

    // 势力标记数据：按地区 UID 记录双方标记数（不实现赢取，仅累记）
    private Dictionary<string, (int p1, int p2)> _marks = new Dictionary<string, (int p1, int p2)>();

    // 纯逻辑地区注册表：地区 UID → 槽位信息（与 UI 的 AreaSlot 同步，但本类不持有 UI 引用）
    private class AreaInfo
    {
        public int SlotIndex;            // 1-based 槽位编号（动态，任务地区销毁后前移）
        public int RequiredPower;        // 赢取所需势力值
        public CardCategory Category;    // Land=基础地区；Quest|Land=任务地区
        public int Score;                // 计入总分的分数
        public CardInstance Instance;    // 彻底方案B：地区卡实例（赢取时 MoveToScore 进分数区）
    }
    private Dictionary<string, AreaInfo> _areas = new Dictionary<string, AreaInfo>();

    /// <summary>注册/更新一个地区到逻辑注册表（Arena 在创建/替换/前移槽位时调用）。
    /// areaUid 即 CardInstance.UID（彻底方案B：用实例 UID 作为槽位身份 ID）。</summary>
    public void RegisterArea(string areaUid, int slotIndex, CardInstance instance)
    {
        if (string.IsNullOrEmpty(areaUid) || instance == null) return;
        var data = instance.AreaTemplate;
        if (data == null) return;
        _areas[areaUid] = new AreaInfo
        {
            SlotIndex = slotIndex,
            RequiredPower = data.RequiredPower,
            Category = data.Category,
            Score = data.Score,
            Instance = instance,
        };
    }

    /// <summary>移除一个地区（任务地区销毁时调用）</summary>
    public void UnregisterArea(string areaUid)
    {
        if (!string.IsNullOrEmpty(areaUid)) _areas.Remove(areaUid);
    }

    /// <summary>返回当前所有地区 UID，按【真实槽位编号 SlotIndex 升序】排列。
    /// 这是"地区编号"的权威顺序（与 Arena._areaSlots / UI 显示一致），
    /// 不受卡牌插入顺序影响，避免对抗阶段结算串地区。</summary>
    public List<string> GetOrderedAreaUids()
    {
        var list = _areas.Keys.ToList();
        list.Sort((a, b) => _areas[a].SlotIndex.CompareTo(_areas[b].SlotIndex));
        return list;
    }

    /// <summary>返回当前所有地区卡实例，按【真实槽位编号 SlotIndex 升序】排列。
    /// 供 UI（含联机 ArenaOl 快照驱动）按场上顺序渲染地区槽位。</summary>
    public List<CardInstance> GetOrderedAreaInstances()
    {
        var ordered = GetOrderedAreaUids();
        var list = new List<CardInstance>(ordered.Count);
        foreach (var uid in ordered)
            if (_areas.TryGetValue(uid, out var info)) list.Add(info.Instance);
        return list;
    }

    /// <summary>查询某地区 UID 对应的真实槽位编号（1-based）；未注册返回 0。</summary>
    public int GetSlotIndex(string areaUid)
        => (!string.IsNullOrEmpty(areaUid) && _areas.TryGetValue(areaUid, out var info)) ? info.SlotIndex : 0;

    /// <summary>任务地区销毁后，其后的槽位编号前移（Arena 在前移 AreaSlot 时同步调用）</summary>
    public void ShiftSlotIndicesFrom(int removedSlotIndex)
    {
        foreach (var kv in _areas)
            if (kv.Value.SlotIndex > removedSlotIndex) kv.Value.SlotIndex--;
    }

    // ============================================================
    // 初始化
    // ============================================================
    public void Initialize(WorldDeckManager worldDeckManager, ZoneManager zoneManager, StackManager stackManager)
    {
        _worldDeckManager = worldDeckManager;
        _zoneManager = zoneManager;
        _stackManager = stackManager;
        GD.Print("✅ AreaManager 初始化完成");
    }

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 调用：清空地区注册表与势力标记（AreaInfo 持有的地区卡实例引用随之释放）。
    /// </summary>
    public void Reset()
    {
        _areas.Clear();
        _marks.Clear();
        GD.Print("[AreaManager] Reset: 地区注册表与势力标记已清空");
    }

    // ============================================================
    // 世界牌库查询
    // ============================================================
    public int WorldDeckCount => _worldDeckManager?.Count ?? 0;

    // ============================================================
    // 初始地区抽取（彻底方案B：返回地区卡实例列表）
    // ============================================================
    public List<CardInstance> DrawInitialAreas(int count = 3)
    {
        return _worldDeckManager?.DrawTop(count) ?? new List<CardInstance>();
    }

    // ============================================================
    // 新增地区：从世界牌库顶抽一张，返回实例（UI 层负责创建 AreaSlot）
    // ============================================================
    public CardInstance DrawNewArea()
    {
        CardInstance instance = _worldDeckManager?.DrawTop();
        if (instance == null)
        {
            GD.Print("⚠️ [AreaManager] 世界牌库已空，无法新增地区");
            return null;
        }
        GD.Print($"[AreaManager] 抽出新地区: {instance.AreaTemplate?.CardName}（牌库剩余 {WorldDeckCount} 张）");
        OnWorldDeckChanged?.Invoke();
        return instance;
    }

    // ============================================================
    // 打出任务地区：从牌库抽一张，返回实例（UI 层负责创建 AreaSlot）
    // ============================================================
    public CardInstance DrawTaskArea()
    {
        CardInstance instance = _worldDeckManager?.DrawTop();
        if (instance == null)
        {
            GD.Print("⚠️ [AreaManager] 世界牌库已空，无法打出任务地区");
            return null;
        }
        GD.Print($"[AreaManager] 抽出任务地区: {instance.AreaTemplate?.CardName}");
        OnWorldDeckChanged?.Invoke();
        return instance;
    }

    // ============================================================
    // 地区赢取处理（核心需求 6.6 + 用户补充5步流程）
    // 规则简化版：堆叠为空时，扫描所有【基础地区】(Category==Land)，
    // 取编号最小、且某方标记 ≥ RequiredPower 者，将其"赢取地区"条目入栈（复制 UI），
    // 经响应窗口后执行步骤1~5结算。任务地区(Quest|Land)走独立规则（本期沿用移除逻辑）。
    // ============================================================

    /// <summary>检查当前可赢取的基础地区列表（按槽位编号升序），返回 (areaUid, winnerId)。</summary>
    public List<(string areaUid, int winnerId)> CheckWinableAreas()
    {
        var result = new List<(string areaUid, int winnerId)>();
        foreach (var kv in _areas)
        {
            var info = kv.Value;
            if (info.Category != CardCategory.Land) continue;   // 仅基础地区（任务地区走其他规则）
            if (!_marks.TryGetValue(kv.Key, out var m)) continue;
            int required = info.RequiredPower;
            if (required <= 0) continue;

            int winner = 0;
            if (m.p1 >= required && m.p1 > m.p2) winner = 1;
            else if (m.p2 >= required && m.p2 > m.p1) winner = 2;
            else if (m.p1 >= required && m.p1 == m.p2) winner = 0; // 平局标记不可能同时≥且相等除非 >0；安全默认
            if (winner == 0) continue;

            result.Add((kv.Key, winner));
        }
        // 按槽位编号升序
        result.Sort((a, b) =>
            _areas[a.areaUid].SlotIndex.CompareTo(_areas[b.areaUid].SlotIndex));
        return result;
    }

    /// <summary>
    /// 尝试启动赢取：仅当堆叠为空时，从可赢取基础地区中取编号最小者入栈。
    /// 由 TurnManager 在"势力对抗后"及"每次堆叠结算清空后"调用。
    /// </summary>
    public void TryStartAreaWin(StackManager stackManager)
    {
        if (stackManager == null || !stackManager.IsEmpty) return;
        var winable = CheckWinableAreas();
        if (winable.Count == 0) return;

        var (areaUid, winnerId) = winable[0];   // 编号最小者
        var entry = new StackEntry
        {
            OwnerId = winnerId,
            IsCard = false,
            Kind = ResolveKind.AreaWon,
            EffectText = $"赢取地区（槽位{_areas[areaUid].SlotIndex}）",
            WonAreaUid = areaUid,
            WonAreaSlotIndex = _areas[areaUid].SlotIndex,
            WinnerId = winnerId,
        };
        stackManager.Push(entry);
        GD.Print($"⚑ [AreaManager] 地区 {areaUid} (槽位{entry.WonAreaSlotIndex}) 满足赢取条件，入栈等待结算（赢取者 P{winnerId}）");
    }

    /// <summary>
    /// 结算赢取地区5步（由 TurnManager.ResolveEntry 的 AreaWon 分支调用）。
    /// 步骤1（响应窗口）已由堆叠机制在结算前处理；此处执行步骤2~5。
    /// </summary>
    /// <summary>
    /// 【P3·撤回】判定一张卡是否"拥有撤回能力"：运行期授予的 `撤回` 关键字 ∪ 卡面固有声明（`HasInherentKeyword`）。
    /// 与"公开"（`HasPublicKeyword`）同款双层判定——离场/效果失效时授予被移除，判定自然转 false。
    /// </summary>
    private static bool HasWithdraw(CardInstance card)
        => card != null && (card.HasKeyword("撤回") || (card.PlayerTemplate?.HasInherentKeyword("撤回") ?? false));

    /// <summary>【P3·K3·去处替代】地区被赢取时，若该卡声明了"赢取后的去处替代"（`WinAreaDestination`）且条件成立，
    /// 就地把该卡按声明去处改判并返回 true（调用方据此跳过"送库底"）。判定/条件复用
    /// `EffectEngine.ResolveWinAreaDestination`（与触发路径同一谓词实现）。
    /// 当前支持的去处：`计分区`（JC079 圣婴「置入你的计分区，而非其拥有者牌库底」）、
    /// `资产区`（JC120 庄严圣像「置入你的资产区，令其成为一个资产」）。
    /// ⚠️ 为什么走替代而非触发：赢取结算里"本地区卡归位"是同步跑完的，入堆叠触发是异步的——
    /// 触发结算时该卡早已躺进牌库底，改判去处无从谈起（见 `ResolveAreaWon` 的既有注释）。</summary>
    private bool TryApplyWinAreaReplacement(CardInstance card, string areaUid, int winnerId)
    {
        var ee = Script.Manager.EffectEngine.Instance;
        string dest = ee?.ResolveWinAreaDestination(card, areaUid, winnerId);
        if (string.IsNullOrEmpty(dest) || _zoneManager == null) return false;
        string name = card.PlayerTemplate?.CardName;
        switch (dest)
        {
            case "计分区":
                _zoneManager.MoveToScore(card, winnerId);
                GD.Print($"[AreaManager] 去处替代：{name} → 玩家{winnerId} 计分区（而非牌库底）");
                return true;
            case "资产区":
                // 与 `MoveToZone{"自身","资产区","正面"}` 同口径：正面置入（暗藏只存在于场上）。
                card.SetCovert(false);
                _zoneManager.MoveToAsset(card, winnerId);
                GD.Print($"[AreaManager] 去处替代：{name} → 玩家{winnerId} 资产区（而非牌库底）");
                return true;
            default:
                return false;   // 未识别的去处声明：按普通归位处理
        }
    }

    /// <summary>【P3·撤回】把带撤回能力的卡移回其 OWNER 手牌（地区被赢取时的去向替代）。</summary>
    private void ReturnWithdrawToHand(CardInstance card)
    {
        if (card == null) return;
        // 走 CardManager 的"退回手牌"收口（解持续效果 + 复位状态 + 移动），语义与"回手"完全一致。
        if (_cardManager != null) _cardManager.ReturnCardToHand(card);
        else _zoneManager?.MoveToHand(card, card.OwnerId);
        GD.Print($"[AreaManager] 撤回：{card.PlayerTemplate?.CardName} 回 OWNER(P{card.OwnerId}) 手牌（而非牌库底）");
    }

    /// <summary>结算赢取地区: 计分 + 触发 + 卡归位。</summary>
    public void ResolveAreaWon(string areaUid, int winnerId)
    {
        if (string.IsNullOrEmpty(areaUid) || !_areas.TryGetValue(areaUid, out var info)) return;
        bool isTaskArea = (info.Category & CardCategory.Quest) != 0;

        GD.Print($"[AreaManager] 结算赢取地区: {areaUid} (赢取者 P{winnerId}, 任务地区={isTaskArea})");

        // 步骤2：地区卡实例置入赢取者计分区（ScoreZone）。
        // 彻底方案B：通过 ZoneManager.MoveToScore 把真实实例移入分数区，
        // 触发 OnCardMoved → 胜利判定自动检测 ScoreZone 总分；分数由 GetScoreTotal 实时计算。
        OnAreaWon?.Invoke(areaUid, isTaskArea);   // 通知 UI 把 AreaCardUI 移入计分区容器（显示层）
        if (info.Instance != null && _zoneManager != null)
        {
            info.Instance.ControllerId = winnerId;
            _zoneManager.MoveToScore(info.Instance, winnerId);
            GD.Print($"[AreaManager] 地区卡实例 {info.Instance.UID} 已进入玩家P{winnerId} 分数区");
        }

        // 【P1·触发时机接线】地区赢取触发（OnAreaWon）：触点放在"步骤2 计分完成"之后、
        // "步骤3 本地区卡送牌库底"之前——此时地区仍注册在原 UID 下、区内卡也还在，
        // "当你赢取本地区时…"类效果的条件与目标都能正确取到。
        // ⚠️ 触发入堆叠后是异步结算的：需要"改变本地区卡去向"的效果（JC079/JC116/JC120）仍属替代效果(K3)范畴。
        _triggerEmitPoints?.OnAreaWon(areaUid, winnerId);

        // 清除本地区势力标记（赢取后标记归零，避免残留并随新地区/移除一并清理）
        if (_marks.ContainsKey(areaUid))
        {
            _marks[areaUid] = (0, 0);
            GD.Print($"[AreaManager.Mark] 地区 {areaUid} 被赢取，势力标记已清零");
            OnMarksChanged?.Invoke(areaUid, 0, 0);   // 通知 UI 刷新标记显示
        }

        // 步骤3：本地区内所有卡返回各自拥有者牌库底（顺序由场上顺序决定，本期简化自动归底）。
        // Q1 递归规则由 ZoneManager 自动处理：仅遍历【顶层卡】（角色 + 结附地区的附属），
        // 跳过"结附于角色的附属"——它们会在宿主 MoveToDeckBottom 时由 ZoneManager 自动连带回库，
        // 避免重复移动。层级顺序：角色 → 其附属 → 其附属的附属 → 结附地区的附属，均按序送入。
        // TODO UI：后续由用户制作的"归位顺序选择"UI 接管，本期按当前场上顺序直接归底。
        var cardsInArea = _zoneManager != null
            ? _zoneManager.GetZoneCards(CardZone.AreaZone, 0, areaUid)
            : new List<CardInstance>();
        // 地区赢取特例：整地区所有卡（角色 + 结附地区的附属 + 结附角色的附属 + 递归附属）返回各自拥有者牌库底。
        // 用 EnterAreaWonMode 包裹，使 MoveToDeckBottom 内部连带的附属也走"回库底"而非普通离场的"进墓地"。
        // 仅遍历【顶层卡】（角色 + 结附地区的附属），跳过"结附于角色的附属"——它们会在宿主 MoveToDeckBottom
        // 时由 ZoneManager 自动连带回库，避免重复移动。
        _zoneManager?.EnterAreaWonMode(() =>
        {
            // 【P3·撤回】规则文本："一个地区被赢取时，拥有撤回能力的卡回到 OWNER 手牌，而不是回到 OWNER 卡组"。
            // 处理顺序：先摘出"带撤回的附属卡"（否则它会被宿主的连带回库一起带走），再判定顶层卡本身是否带撤回。
            foreach (var card in cardsInArea)
            {
                if (card.AttachedCards != null)
                {
                    foreach (var att in new List<CardInstance>(card.AttachedCards))
                    {
                        if (HasWithdraw(att)) ReturnWithdrawToHand(att);
                    }
                }
            }
            // 1) 顶层卡（角色/暗藏者）：带撤回 → 回 OWNER 手牌；否则回库底。
            foreach (var card in cardsInArea)
            {
                if (card.IsAttached) continue;   // 附属卡不在 _area，此判断保留防御性
                // 【P3·K3·去处替代】JC079 圣婴「当你赢取本地区时，将圣婴置入你的计分区，而非其拥有者牌库底」：
                // 与『撤回』同一实现位——就地改判去处（该卡文属"改变本地区卡去向"的替代效果，不入堆叠）。
                if (TryApplyWinAreaReplacement(card, areaUid, winnerId)) continue;
                if (HasWithdraw(card)) { ReturnWithdrawToHand(card); continue; }
                _zoneManager.MoveToDeckBottom(card, card.OwnerId, false);
            }
            // 2) 附属于地区的附属卡（AttachedTo==null，挂地区容器，不在任何角色 SealedCards/AttachedCards 里）：回库底（带撤回者回手牌）
            foreach (var att in _zoneManager.GetZoneCards(CardZone.AttachedZone, 0, areaUid))
            {
                if (att.AttachedTo != null) continue;
                if (TryApplyWinAreaReplacement(att, areaUid, winnerId)) continue;   // 【P3·K3】去处替代同办（JC120 庄严圣像）
                if (HasWithdraw(att)) { ReturnWithdrawToHand(att); continue; }
                _zoneManager.MoveToDeckBottom(att, att.OwnerId, false);
            }
            // 3) 封印卡：封印于角色（角色的 SealedCards）与封印于地区（SealedZone 桶中 ZoneDetailId==地区）均回 OWNER 手牌（需求④）
            foreach (var role in cardsInArea)
            {
                if (role.SealedCards != null)
                    foreach (var seal in role.SealedCards)
                        if (seal.CurrentZone == CardZone.SealedZone) _zoneManager.MoveToHand(seal, seal.OwnerId);
            }
            foreach (var seal in _zoneManager.GetZoneCards(CardZone.SealedZone, 0, areaUid))
                _zoneManager.MoveToHand(seal, seal.OwnerId);
        });

        // 分数累计：计分区总分由 PlayerManager.GetScoreTotal 实时计算（分数区内所有卡 Score 之和），
        // 因此只需确保地区卡已进入 ScoreZone 即可，无需单独累计字段。
        int scoreGained = info.Score;

        // 步骤4：从世界牌库翻开新地区补充（基础地区不销毁槽位；任务地区走移除逻辑）
        if (isTaskArea)
        {
            UnregisterArea(areaUid);
            OnAreaRemoved?.Invoke(info.SlotIndex);
            ShiftSlotIndicesFrom(info.SlotIndex);
        }
        else
        {
            CardInstance newInstance = _worldDeckManager?.DrawTop();
            if (newInstance != null)
            {
                // 方案B修正：被赢走的旧地区实例已 MoveToScore 进分数区（旧 UID），
                // 新区必须用新实例 UID 作为槽位身份 ID，否则 AreaManager._areas 的 key 与
                // Arena.slot.AreaUID 错位 → 势力标记/对抗流程落到失效 UID（标记加了却赢不走、对抗不触发）。
                UnregisterArea(areaUid);                                    // 清理旧 UID 注册
                RegisterArea(newInstance.UID, info.SlotIndex, newInstance); // 用新实例 UID 重新注册
                OnAreaReplaced?.Invoke(areaUid, newInstance);               // 旧 UID 供 Arena 定位原槽位
            }
            else
            {
                // 牌库空：移除槽位
                UnregisterArea(areaUid);
                OnAreaRemoved?.Invoke(info.SlotIndex);
                ShiftSlotIndicesFrom(info.SlotIndex);
            }
        }
        OnWorldDeckChanged?.Invoke();

        // 步骤5：赢取触发能力（核心需求 2.2 标注延后，本期不实现，仅留钩子）
        // TODO：WinTriggerEffect 执行（延后）。

        // 通知 UI 刷新分数
        OnAreaWonResolved?.Invoke(areaUid, winnerId, scoreGained);
        GD.Print($"✅ [AreaManager] 地区 {areaUid} 赢取结算完成，P{winnerId} 获得 {scoreGained} 分");
    }

    // ============================================================
    // 势力标记管理（6.4-c）
    // 添加本方标记时，优先移除等量对方标记（净差逻辑），剩余才累加本方。
    // 当前阶段【不实现地区赢取】，仅用于测试标记产出与 UI 显示。
    // ============================================================
    public void AddPowerMark(string areaUid, int playerId, int amount)
    {
        if (amount <= 0) return;
        if (!_marks.TryGetValue(areaUid, out var m)) m = (0, 0);

        int self = playerId == 1 ? m.p1 : m.p2;
        int opp = playerId == 1 ? m.p2 : m.p1;

        // 优先用本方新增量抵扣对方已有标记
        int deducted = Math.Min(amount, opp);
        opp -= deducted;
        int remain = amount - deducted;
        self += remain;

        if (playerId == 1) m = (self, opp);
        else m = (opp, self);
        _marks[areaUid] = m;

        int slotIndex = _areas.TryGetValue(areaUid, out var areaInfo) ? areaInfo.SlotIndex : -1;
        GD.Print($"[AreaManager.Mark] 向 AreaSlot_{slotIndex}({areaUid}) 添加势力标记：玩家{playerId} +{amount} → " +
                 $"当前标记 P1={m.p1}, P2={m.p2}");

        // 通过事件通知 UI 层刷新对应地区标记（不直接操作 UI 节点）
        OnMarksChanged?.Invoke(areaUid, m.p1, m.p2);

        // 【P1·触发时机接线】标志放置触发（OnMarkerPlaced）：势力标志放置后广播。
        // 该时机高频（对抗结算/威名/效果放置都会走），消费方需自行防自指递归（D-1 熔断兜底）。
        _triggerEmitPoints?.OnMarkerPlaced("势力标志", amount, playerId, areaUid);

        // 注：赢取地区检查（TryStartAreaWin）不再在 AddPowerMark 内触发。
        // 原因：AddPowerMark 多从 EffectEngine 执行动作（如 JC035 的 PlaceToken）的嵌套调用中进入，
        // 若此时直接入栈"赢取地区"会嵌套进当前 ResolveStackTop 栈帧，打乱阶段优先权节拍。
        // 统一改由 TurnManager.ResolveStackTop 收尾点（堆叠清空后）调用，与势力对抗路径同一时机。
        // 纯调试命令 ExecuteAddMark 如需立即结算，请在其末尾自行调用 TryStartAreaWin(_stackManager)。
    }

    /// <summary>查询某地区当前双方势力标记（便于调试/结算）</summary>
    public (int p1, int p2) GetMarks(string areaUid)
        => _marks.TryGetValue(areaUid, out var m) ? m : (0, 0);

    /// <summary>查询某地区某玩家当前的势力标志数（0 = 无）。</summary>
    public int GetPowerMark(string areaUid, int playerId)
    {
        if (string.IsNullOrEmpty(areaUid) || playerId <= 0) return 0;
        if (!_marks.TryGetValue(areaUid, out var m)) return 0;
        return playerId == 1 ? m.p1 : m.p2;
    }

    /// <summary>
    /// 【P4·遏制】从某地区**移除**某玩家的势力标志（标记系统的减法侧；K6）。
    /// 语义与 `AddPowerMark` 明确区分：`AddPowerMark` 带"新增量先抵扣对方标记"的净差逻辑，
    /// 而**遏制是纯移除**——只减少被遏制者自己的标志，不影响对方。
    /// 返回**实际移除数**（不足时按现有数量钳制，不会变成负数）。
    /// 移除后同样经 `OnMarksChanged` 通知 UI（与放置对称）。
    /// </summary>
    public int RemovePowerMark(string areaUid, int playerId, int amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(areaUid) || playerId <= 0) return 0;
        if (!_marks.TryGetValue(areaUid, out var m)) return 0;

        int have = playerId == 1 ? m.p1 : m.p2;
        int removed = Math.Min(amount, have);
        if (removed <= 0) return 0;

        if (playerId == 1) m = (m.p1 - removed, m.p2);
        else m = (m.p1, m.p2 - removed);
        _marks[areaUid] = m;

        // 【P3·事件记忆位】"本回合中，玩家 P 的势力标志曾被移除"（JC053 鸣钟教派徽记的激活前提）：
        // 只在**真的移除了 >0 个**时置位（请求但无可移除 → 不置位，与卡文"曾被移除"一致）。
        MarkPowerMarkRemovedThisTurn(playerId);

        int slotIndex = _areas.TryGetValue(areaUid, out var areaInfo) ? areaInfo.SlotIndex : -1;
        GD.Print($"[AreaManager.Mark] 从 AreaSlot_{slotIndex}({areaUid}) 移除势力标记：玩家{playerId} -{removed}（请求 {amount}）→ " +
                 $"当前标记 P1={m.p1}, P2={m.p2}");
        OnMarksChanged?.Invoke(areaUid, m.p1, m.p2);
        return removed;
    }

    // ==================== 【P3】本回合"势力标志曾被移除"记忆位 ====================
    /// <summary>
    /// 本回合内"其势力标志曾被移除"的玩家集合（每次真实移除 &gt;0 个即置位；**每个回合开始时清空**，
    /// 由 `TurnManager` 的重置步骤调用 <see cref="ResetPowerMarkRemovedThisTurn"/>）。
    /// 用途：JC053 鸣钟教派徽记「本回合中，若有**敌方**势力标志曾被移除，你可以抓一张牌」——
    /// 记录的是"回合内的既成事实"，与"由谁造成"无关（卡文只要求"曾被移除"）。
    /// </summary>
    private readonly System.Collections.Generic.HashSet<int> _powerMarkRemovedThisTurn
        = new System.Collections.Generic.HashSet<int>();

    /// <summary>登记"玩家 playerId 的势力标志本回合曾被移除"。</summary>
    public void MarkPowerMarkRemovedThisTurn(int playerId)
    {
        if (playerId <= 0) return;
        _powerMarkRemovedThisTurn.Add(playerId);
    }

    /// <summary>玩家 playerId 的势力标志本回合是否曾被移除。</summary>
    public bool HasPowerMarkRemovedThisTurn(int playerId) => _powerMarkRemovedThisTurn.Contains(playerId);

    /// <summary>清空记忆位（回合开始的重置步骤调用）。</summary>
    public void ResetPowerMarkRemovedThisTurn()
    {
        if (_powerMarkRemovedThisTurn.Count == 0) return;
        _powerMarkRemovedThisTurn.Clear();
        GD.Print("[AreaManager.Mark] 重置：本回合『势力标志曾被移除』记忆位已清空");
    }

    /// <summary>清空所有地区标记（重置/测试用），通过事件通知 UI 层刷新</summary>
    public void ClearAllMarks()
    {
        foreach (var kv in _marks)
            OnMarksChanged?.Invoke(kv.Key, 0, 0);
        _marks.Clear();
    }

    // ==================== C/S 化·第三批：地区投影 ====================
    /// <summary>注入 CardManager 引用（跨进程投影：ApplySnapshot 用 RebuildFromRef 重建地区卡实例）。</summary>
    public void SetCardManager(CardManager cardManager) => _cardManager = cardManager;

    /// <summary>导出已注册地区卡快照（含槽位），供快照下行（客户端从模板重建地区卡并注册）。</summary>
    public System.Collections.Generic.List<Script.Net.ZoneCardRef> ToSnapshot()
    {
        var list = new System.Collections.Generic.List<Script.Net.ZoneCardRef>();
        foreach (var kv in _areas)
        {
            var info = kv.Value;
            if (info?.Instance == null) continue;
            list.Add(new Script.Net.ZoneCardRef
            {
                Uid = kv.Key,
                TemplateUid = info.Instance.Template?.UID ?? "",
                OwnerId = info.Instance.OwnerId,
                Zone = Script.Enums.CardZone.AreaZone,
                ZoneDetailId = info.SlotIndex.ToString(), // 槽位编号（1-based）
            });
        }
        return list;
    }

    /// <summary>导出各地区势力标记，供快照下行。</summary>
    public System.Collections.Generic.List<Script.Net.AreaMarkSnap> ToSnapshotMarks()
    {
        var list = new System.Collections.Generic.List<Script.Net.AreaMarkSnap>();
        foreach (var kv in _marks)
            list.Add(new Script.Net.AreaMarkSnap { AreaUid = kv.Key, P1 = kv.Value.p1, P2 = kv.Value.p2 });
        return list;
    }

    /// <summary>
    /// 从快照重建客户端投影的地区注册表与势力标记（仅联网投影/重连调用，对单机零影响）。
    /// 地区注册表：从快照 AreaCards（含模板+槽位）用 CardManager.RebuildFromRef 重建地区卡实例再 RegisterArea，
    ///   跨进程独立 CardManager（NetClientCardManager）走 ResolveInstance 从模板按权威 UID 重建；
    ///   不再依赖 ZoneManager 的 AreaZone（地区卡本就不进 Zone 桶）。
    /// 势力标记：从 AreaMarkSnap 快照直接恢复。
    /// </summary>
    public void ApplySnapshot(System.Collections.Generic.List<Script.Net.ZoneCardRef> areaCards,
        System.Collections.Generic.List<Script.Net.AreaMarkSnap> marks)
    {
        // 1) 重建地区注册表（从快照 AreaCards 重建实例 + 槽位）
        _areas.Clear();
        if (areaCards != null)
        {
            foreach (var r in areaCards)
            {
                var inst = _cardManager?.RebuildFromRef(r); // 命中共享实例或从模板重建
                if (inst == null || inst.AreaTemplate == null) continue;
                int slot = 0;
                if (r.ZoneDetailId != null) foreach (char c in r.ZoneDetailId) if (c >= '0' && c <= '9') slot = slot * 10 + (c - '0');
                RegisterArea(r.Uid, slot, inst);
            }
        }
        // 2) 恢复势力标记
        _marks.Clear();
        if (marks != null)
        {
            foreach (var m in marks)
                if (!string.IsNullOrEmpty(m.AreaUid)) _marks[m.AreaUid] = (m.P1, m.P2);
        }
    }
}
