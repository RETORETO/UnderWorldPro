using Godot;
using Script.Game.Entities;
using System.Collections.Generic;

public partial class StackManager : Node
{
    // 堆叠数据：列表末尾 = 堆叠顶（LIFO）
    private List<StackEntry> _stack = new List<StackEntry>();

    /// <summary>堆叠内容变化（增/删/清空）时触发，UI 订阅以刷新</summary>
    public event System.Action OnStackChanged;

    // ─────────────────────────────────────────────────────────────
    // 【D16 统一收口】目标版本号登记
    // 目的：任何取目标效果经 Push 入栈时，自动把目标卡当前 VersionId 记入 entry.TargetVersionId，
    // 供结算时 D16 版本号比对（目标翻面/移动/夺控/子类别变更均 BumpVersion → 版本号不一致即判丢失）。
    // 此前只有 TurnManager.CaptureTargetSnapshot（触发/出牌入栈）会登记，激活能力入栈
    // （EffectEngine.Instance.PushActivationToStack）绕过它导致漏记；统一收口到 Push 后所有入栈路径都不可能漏。
    // 依赖：CardManager 引用（运行时由 GameInitializer 注入；无注入的单测跳过登记，不影响行为）。
    // ─────────────────────────────────────────────────────────────
    private CardManager _cardManager;
    /// <summary>注入卡牌管理器，供 Push 时自动登记目标版本号（D16 统一收口）。</summary>
    public void SetCardManager(CardManager cm) => _cardManager = cm;

    // ─────────────────────────────────────────────────────────────
    // 【D-1 防御性熔断】堆叠累计计数器
    // 目的：防止效果互相触发导致无限递归（如 A 死亡触发 B、B 又致 A 死亡）。
    // 语义：统计"本次堆叠序列"累计压入的条目数，而非当前堆叠深度——
    //       递归爆炸时条目会边压边结算，深度可能始终很浅，只有累计值能反映失控。
    // 归零：堆叠清空时（ResolveTop 结算至空 / Clear），一个完整序列结束即重置。
    // 现状：正常对局远达不到 20，触发即视为逻辑缺陷，仅作防御，不做常规流程依赖。
    // ─────────────────────────────────────────────────────────────

    /// <summary>单次堆叠序列的累计压入上限，达到即熔断。</summary>
    public const int MaxStackSequence = 20;

    private int _sequenceCount = 0;

    /// <summary>本次堆叠序列累计压入的条目数（堆叠清空时归零）。</summary>
    public int SequenceCount => _sequenceCount;

    /// <summary>
    /// 是否已熔断。为 true 时禁止：任何效果触发、打出新卡、激活新效果。
    /// 调用方应在入栈/出牌/激活前检查此标志。
    /// </summary>
    public bool IsCircuitBroken => _sequenceCount >= MaxStackSequence;

    /// <summary>当前堆叠是否为空</summary>
    public bool IsEmpty => _stack.Count == 0;

    /// <summary>
    /// 清空所有未结算堆叠效果（游戏结束即时结算时调用，不经过堆叠）。
    /// 同时重置熔断序列计数（一个完整序列结束），并广播 OnStackChanged 让 UI 刷新。
    /// </summary>
    public void ClearAll()
    {
        _stack.Clear();
        _sequenceCount = 0;
        OnStackChanged?.Invoke();
    }

    /// <summary>堆叠顶条目（LIFO 顶部）</summary>
    public StackEntry Top => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>当前堆叠快照（只读遍历用）</summary>
    public IReadOnlyList<StackEntry> Entries => _stack;

    /// <summary>
    /// 将条目压入堆叠顶部。卡片或效果均通过 StackEntry 构造。
    /// 已熔断（累计达 MaxStackSequence）时拒绝压入并返回 false。
    /// </summary>
    public bool Push(StackEntry entry)
    {
        if (entry == null) return false;

        // D-1 熔断：达到上限后拒绝一切新条目，避免递归失控
        if (IsCircuitBroken)
        {
            GD.PrintErr($"⛔ [Stack] 已熔断（累计 {_sequenceCount}/{MaxStackSequence}），拒绝压入: {entry.EffectText}");
            return false;
        }

        _stack.Add(entry);   // 末尾为顶
        _sequenceCount++;

        // D16 统一收口：入栈时自动登记目标状态版本号（供结算 D16 比对；无 CardManager 注入则跳过）
        if (_cardManager != null && !string.IsNullOrEmpty(entry.TargetCardUid))
        {
            var target = _cardManager.GetInstance(entry.TargetCardUid);
            if (target != null)
            {
                entry.TargetVersionId = target.VersionId;
            }
        }


        if (_sequenceCount >= MaxStackSequence)
            GD.PrintErr($"⛔ [Stack] 累计压入达上限 {MaxStackSequence}，触发熔断：后续禁止触发效果／打出卡牌／激活能力，直至堆叠清空");
        else if (_sequenceCount >= MaxStackSequence - 5)
            GD.PrintErr($"⚠️ [Stack] 累计压入 {_sequenceCount}/{MaxStackSequence}，接近熔断阈值（疑似效果递归）");

        OnStackChanged?.Invoke();
        return true;
    }

    /// <summary>弹出并结算堆叠顶条目（LIFO）。返回被弹出的条目。</summary>
    public StackEntry ResolveTop()
    {
        if (_stack.Count == 0) return null;
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        GD.Print($"[Stack] 结算顶部: {(top.IsCard ? "卡片 " + top.EffectText : "效果 " + top.EffectText)} (剩{_stack.Count})");

        // 堆叠清空 = 本次序列结束，计数归零
        if (_stack.Count == 0) ResetSequence("堆叠结算至空");

        OnStackChanged?.Invoke();
        return top;
    }

    /// <summary>清空堆叠（如阶段结束）</summary>
    public void Clear()
    {
        _stack.Clear();
        ResetSequence("堆叠清空");
        OnStackChanged?.Invoke();
    }

    /// <summary>累计计数归零（堆叠清空时调用）。</summary>
    private void ResetSequence(string reason)
    {
        if (_sequenceCount == 0) return;
        GD.Print($"[Stack] 累计计数归零（{reason}，原值 {_sequenceCount}）");
        _sequenceCount = 0;
    }

    /// <summary>占位兼容旧调用</summary>

    public void PassPriority(int playerId) => GD.Print($"玩家{playerId}让过");

    // ==================== C/S 化·第二批：客户端投影堆叠恢复 ====================
    /// <summary>
    /// 从服务端堆叠快照重建客户端投影的堆叠（仅联网投影/重连调用，对单机零影响）。
    /// 按 SourceUid 从 CardManager 解析卡片型条目的 SourceCard；效果型仅保留 EffectText。
    /// 重建不触发 OnStackChanged（纯数据覆盖，避免投影刷新反向驱动 UI）。
    /// </summary>
    public void ApplySnapshot(System.Collections.Generic.List<Script.Net.StackEntrySnap> entries)
    {
        _stack.Clear();
        _sequenceCount = 0;
        if (entries != null)
        {
            foreach (var s in entries)
            {
                if (s == null) continue;
                var entry = new Script.Game.Entities.StackEntry(s.OwnerId, s.EffectText ?? "");
                if (!string.IsNullOrEmpty(s.SourceUid) && _cardManager != null)
                    entry.SourceCard = _cardManager.GetInstance(s.SourceUid);
                entry.SourceCardUid = s.SourceUid;
                _stack.Add(entry);
            }
        }
    }


    // ==================== C/S 化 P1-A：堆叠快照导出 ====================
    /// <summary>导出当前堆叠为纯数据结构（避开不可序列化的 OnResolve 委托，仅取结构化 Kind+负载）。</summary>
    public List<Script.Net.StackEntrySnap> ToSnapshot()
    {
        var list = new System.Collections.Generic.List<Script.Net.StackEntrySnap>();
        foreach (var e in _stack)
        {
            if (e == null) continue;
            list.Add(new Script.Net.StackEntrySnap
            {
                OwnerId = e.OwnerId,
                SourceUid = e.SourceCardUid,
                EffectText = e.EffectText,
            });
        }
        return list;
    }
}
