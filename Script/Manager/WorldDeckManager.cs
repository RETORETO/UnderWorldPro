using Godot;
using Script.Data.Cards;
using Script.Game;
using Script.Game.Entities;
using Script.Interfaces;
using System.Collections.Generic;
using System.Linq;

public partial class WorldDeckManager : Node
{
    // 彻底方案B：世界牌库持有地区卡实例（CardInstance），开局实例化全部地区卡，
    // 抽地区/展示/赢取全部走实例，赢取时实例直接进入玩家分数区（ScoreZone）。
    // 牌堆（索引0为顶部）
    private List<CardInstance> _deck = new List<CardInstance>();

    /// <summary>随机源（由 GameInitializer 注入）。禁止在本类内 new Random()。</summary>
    private IRandomSource _rng;
    private IWorldDeckProvider _provider;
    private CardManager _cardManager;

    // 公开属性
    public int Count => _deck.Count;

    public void Initialize(IWorldDeckProvider provider, IRandomSource rng, CardManager cardManager)
    {
        _provider = provider;
        _rng = rng;
        _cardManager = cardManager;
        // 加载全部地区卡数据，逐张实例化为 CardInstance（ownerId=0 中立，赢取时改 ControllerId）。
        var areaDataList = _provider.LoadAllAreas();
        _deck = areaDataList
            .Select(data => _cardManager.CreateCardInstance(data, 0))
            .ToList();
        Shuffle();
        GD.Print($"【WorldDeckManager】已加载并实例化 {_deck.Count} 张地区卡，已洗牌");
    }

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 调用：复用已保存的依赖（_provider/_rng/_cardManager）
    /// 重新加载并洗牌全部地区卡，产生一套全新的地区卡实例。
    /// </summary>
    public void Reset()
    {
        _deck.Clear();
        // 复用已保存的依赖重建（依赖在首次 Initialize 时保存，常驻场景下无需重新注入）
        var areaDataList = _provider.LoadAllAreas();
        _deck = areaDataList
            .Select(data => _cardManager.CreateCardInstance(data, 0))
            .ToList();
        Shuffle();
        GD.Print($"[WorldDeckManager] Reset: 世界牌库已重建并洗牌, 共 {_deck.Count} 张");
    }

    public void Shuffle()
    {
        // 随机源统一走注入的 IRandomSource，保证同种子下地区牌序可复现。
        _rng.Shuffle(_deck);
    }

    /// <summary>
    /// 从牌堆顶部抽取一张（不放回，返回实例）
    /// </summary>
    public CardInstance DrawTop()
    {
        if (_deck.Count == 0) return null;
        var card = _deck[0];
        _deck.RemoveAt(0);
        return card;
    }

    /// <summary>
    /// 从牌堆顶部抽取 N 张（返回实例列表）
    /// </summary>
    public List<CardInstance> DrawTop(int count)
    {
        var result = new List<CardInstance>();
        for (int i = 0; i < count && _deck.Count > 0; i++)
        {
            result.Add(DrawTop());
        }
        return result;
    }

    public IReadOnlyList<CardInstance> GetAllAreas() => _deck.AsReadOnly();

    /// <summary>注入 CardManager 引用（客户端投影用：ApplySnapshotUids 需按 UID 命中实例）。</summary>
    public void SetCardManager(CardManager cardManager) => _cardManager = cardManager;

    // ==================== C/S 化·第三批：世界牌库投影 ====================
    /// <summary>导出地区牌堆剩余卡快照（自顶向下顺序，含 TemplateUid），供快照下行。
    /// 用 ZoneCardRef 而非纯 UID，使跨进程客户端可从模板重建地区卡实例。</summary>
    public System.Collections.Generic.List<Script.Net.ZoneCardRef> ToSnapshotUids()
    {
        var list = new System.Collections.Generic.List<Script.Net.ZoneCardRef>();
        foreach (var c in _deck)
        {
            if (c == null) continue;
            list.Add(new Script.Net.ZoneCardRef
            {
                Uid = c.UID,
                TemplateUid = c.Template?.UID ?? "",
                OwnerId = c.OwnerId,
                Zone = Script.Enums.CardZone.Deck,
                IsHidden = c.IsHidden,   // Phase B：隐藏占位（信息隔离）标记随投影保留
            });
        }
        return list;
    }

    /// <summary>
    /// 从快照重建客户端投影的地区牌堆（仅联网投影/重连调用，对单机零影响）。
    /// 用 CardManager.RebuildFromRef 解析实例：同进程共享 CardManager 直接命中；跨进程独立
    /// CardManager（NetClientCardManager）走 ResolveInstance 从模板按权威 UID 重建。
    /// </summary>
    public void ApplySnapshotUids(System.Collections.Generic.List<Script.Net.ZoneCardRef> refs)
    {
        _deck.Clear();
        if (refs != null)
        {
            foreach (var r in refs)
            {
                var inst = _cardManager?.RebuildFromRef(r); // 命中或从模板重建
                if (inst != null) _deck.Add(inst);
            }
        }
    }
}