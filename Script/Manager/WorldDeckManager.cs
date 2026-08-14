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
}