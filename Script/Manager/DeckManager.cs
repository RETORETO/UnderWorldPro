using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.IO;
using Script.Game;
using Script.Game.Entities;
using System.Collections.Generic;

public partial class DeckManager : Node
{
    /// <summary>抽卡/洗牌级诊断日志开关（降噪）。默认 false；调试时置 true 可见抽牌明细。不影响逻辑。</summary>
    public static bool VerboseMoves = false;

    // 两个玩家的卡组（存储 CardInstance）
    private List<CardInstance> _player1Deck = new List<CardInstance>();
    private List<CardInstance> _player2Deck = new List<CardInstance>();


    private CardManager _cardManager;

    /// <summary>随机源（由 GameInitializer 注入）。禁止在本类内 new Random()。</summary>
    private IRandomSource _rng;

    public void Initialize(CardManager cardManager, IRandomSource rng)
    {
        _cardManager = cardManager;
        _rng = rng;
    }


    // 测试卡组模板：(uid, 资源路径, 份数)
    // 当前测试卡组 = JC001~JC015 各 3 张 + XG01_阴爻探球者 3 张（共 48 张/副）。
    // 如需调整卡组构成，仅需修改此列表；份数之和即每副卡组的张数。
    private readonly (string uid, string path, int copies)[] _testDeckTemplate = new (string, string, int)[]
    {
        ("JC001_tonglingmaotouying",  "res://Cards/Data/PlayerCards/JC/JC001_tonglingmaotouying.tres",  3),
        ("JC002_jushizhenkanhuren",   "res://Cards/Data/PlayerCards/JC/JC002_jushizhenkanhuren.tres",   3),
        ("JC003_weimuhuwei",          "res://Cards/Data/PlayerCards/JC/JC003_weimuhuwei.tres",          3),
        ("JC004_lichangfashi",        "res://Cards/Data/PlayerCards/JC/JC004_lichangfashi.tres",        3),
        ("JC005_liejieshu",           "res://Cards/Data/PlayerCards/JC/JC005_liejieshu.tres",           3),
        ("JC006_nizhuanyijiezhimen",  "res://Cards/Data/PlayerCards/JC/JC006_nizhuanyijiezhimen.tres",  3),
        ("JC007_changchuntengpaixuezhe", "res://Cards/Data/PlayerCards/JC/JC007_changchuntengpaixuezhe.tres", 3),
        ("JC008_lingnengjifa",        "res://Cards/Data/PlayerCards/JC/JC008_lingnengjifa.tres",        3),
        ("JC009_yuandongliuhaokeyansuo", "res://Cards/Data/PlayerCards/JC/JC009_yuandongliuhaokeyansuo.tres", 3),
        ("JC010_qingshoufeinandier",  "res://Cards/Data/PlayerCards/JC/JC010_qingshoufeinandier.tres",  3),
        ("JC011_demositinidamofashi",          "res://Cards/Data/PlayerCards/JC/JC011_demositinidamofashi.tres",3),
        ("JC012_weimushouwanghuiji",  "res://Cards/Data/PlayerCards/JC/JC012_weimushouwanghuiji.tres",  3),
        ("JC013_mo",                  "res://Cards/Data/PlayerCards/JC/JC013_mo.tres",                  3),
        ("JC014_gongluqishi",         "res://Cards/Data/PlayerCards/JC/JC014_gongluqishi.tres",         3),
        ("BQ022_hejinzhihu",      "res://Cards/Data/PlayerCards/BQ/BQ022_hejinzhihu.tres",      3),
        ("JC030_juxingbianfu",      "res://Cards/Data/PlayerCards/JC/JC030_juxingbianfu.tres",      3),
        ("JC049_shenyuanxiyu",      "res://Cards/Data/PlayerCards/JC/JC049_shenyuanxiyu.tres",      3),
        ("JC047_liehuozhongwei",      "res://Cards/Data/PlayerCards/JC/JC047_liehuozhongwei.tres",      3),
        ("JC068_agesiIV",             "res://Cards/Data/PlayerCards/JC/JC068_agesIV.tres",              3),
        

    };

    // ============================================================
    // 初始化
    // ============================================================

    /// <summary>
    /// 用外部卡组（来自 JSON 的 DeckList）初始化牌库。
    /// 逻辑与 InitTestDecks 一致：遍历 (模板路径, 份数) 实例化。
    /// playerId: 0=双方, 1=仅玩家1, 2=仅玩家2（用于房间选卡组时只替换 P1、P2 仍用硬编码）。
    /// </summary>
    public void InitFromDeckList(DeckList deck, int playerId = 0)
    {
        GD.Print("【DeckManager】从卡组 JSON 初始化牌库...");

        if (playerId == 0 || playerId == 1) _player1Deck.Clear();
        if (playerId == 0 || playerId == 2) _player2Deck.Clear();

        foreach (var entry in deck.Cards)
        {
            var cardData = ResourceLoader.Load<PlayerCardData>(entry.TemplatePath);
            if (cardData == null)
            {
                GD.PrintErr($"❌ 加载卡牌失败: {entry.TemplatePath}");
                continue;
            }

            for (int i = 0; i < entry.Copies; i++)
            {
                if (playerId == 0 || playerId == 1)
                {
                    var instance = _cardManager.CreateCardInstance(cardData, 1);
                    _player1Deck.Add(instance);
                }
                if (playerId == 0 || playerId == 2)
                {
                    var instance2 = _cardManager.CreateCardInstance(cardData, 2);
                    _player2Deck.Add(instance2);
                }
            }
        }

        if (playerId == 0 || playerId == 1) if (VerboseMoves) GD.Print($"✅ 玩家1卡组: {_player1Deck.Count} 张");
        if (playerId == 0 || playerId == 2) if (VerboseMoves) GD.Print($"✅ 玩家2卡组: {_player2Deck.Count} 张");

        if (playerId == 0 || playerId == 1) ShufflePlayer1();
        if (playerId == 0 || playerId == 2) ShufflePlayer2();
        if (VerboseMoves) GD.Print("✅ 卡组已洗牌");
    }

    /// <summary>
    /// 初始化测试卡组（硬编码 _testDeckTemplate）。playerId: 0=双方, 1=仅P1, 2=仅P2。
    /// </summary>
    public void InitTestDecks(int playerId = 0)
    {
        GD.Print("【DeckManager】初始化卡组（全部实例化）...");
        if (playerId == 0 || playerId == 1) _player1Deck.Clear();
        if (playerId == 0 || playerId == 2) _player2Deck.Clear();

        foreach (var entry in _testDeckTemplate)
        {
            var cardData = ResourceLoader.Load<PlayerCardData>(entry.path);
            if (cardData == null)
            {
                GD.PrintErr($"❌ 加载卡牌失败: {entry.path}");
                continue;
            }

            for (int i = 0; i < entry.copies; i++)
            {
                if (playerId == 0 || playerId == 1)
                {
                    var instance = _cardManager.CreateCardInstance(cardData, 1); // ownerId = 1
                    _player1Deck.Add(instance);
                }
                if (playerId == 0 || playerId == 2)
                {
                    var instance2 = _cardManager.CreateCardInstance(cardData, 2);
                    _player2Deck.Add(instance2);
                }
            }
        }

        if (playerId == 0 || playerId == 1) if (VerboseMoves) GD.Print($"✅ 玩家1卡组: {_player1Deck.Count} 张");
        if (playerId == 0 || playerId == 2) if (VerboseMoves) GD.Print($"✅ 玩家2卡组: {_player2Deck.Count} 张");

        if (playerId == 0 || playerId == 1) ShufflePlayer1();
        if (playerId == 0 || playerId == 2) ShufflePlayer2();
        if (VerboseMoves) GD.Print("✅ 卡组已洗牌");
    }

    // ============================================================
    // 洗牌
    // ============================================================
    public void ShufflePlayer1() => Shuffle(_player1Deck);
    public void ShufflePlayer2() => Shuffle(_player2Deck);

    private void Shuffle(List<CardInstance> deck)
    {
        // Fisher-Yates 洗牌：实例在卡组 List 内被真正打乱顺序，抽牌时固定取 [0]
        // 随机源统一走注入的 IRandomSource，保证同种子下牌序可复现。
        _rng.Shuffle(deck);
    }

    // ============================================================
    // 抽牌
    // ============================================================
    public CardInstance DrawPlayer1()
    {
        if (_player1Deck.Count == 0)
        {
            GD.PrintErr("❌ 玩家1卡组已空，无法抽牌");
            return null;
        }
        var card = _player1Deck[0];
        _player1Deck.RemoveAt(0);
        // 注意：CurrentZone 由 ZoneManager.MoveToHand 统一设置，本类只负责牌库列表增删
        if (VerboseMoves) GD.Print($"🃏 玩家1抽到卡牌: {card.PlayerTemplate?.CardName} (剩余 {_player1Deck.Count} 张)");
        return card;
    }

    public CardInstance DrawPlayer2()
    {
        if (_player2Deck.Count == 0)
        {
            GD.PrintErr("❌ 玩家2卡组已空，无法抽牌");
            return null;
        }
        var card = _player2Deck[0];
        _player2Deck.RemoveAt(0);
        // 注意：CurrentZone 由 ZoneManager.MoveToHand 统一设置，本类只负责牌库列表增删
        if (VerboseMoves) GD.Print($"🃏 玩家2抽到卡牌: {card.PlayerTemplate?.CardName} (剩余 {_player2Deck.Count} 张)");
        return card;
    }

    /// <summary>从牌库底抽牌（玩家1）。牌库为 List，底 = 末尾 [Count-1]。</summary>
    public CardInstance DrawFromBottomPlayer1()
    {
        if (_player1Deck.Count == 0)
        {
            GD.PrintErr("❌ 玩家1卡组已空，无法从底抽牌");
            return null;
        }
        var card = _player1Deck[_player1Deck.Count - 1];
        _player1Deck.RemoveAt(_player1Deck.Count - 1);
        if (VerboseMoves) GD.Print($"🃏 玩家1从牌库底抽到卡牌: {card.PlayerTemplate?.CardName} (剩余 {_player1Deck.Count} 张)");
        return card;
    }

    /// <summary>从牌库底抽牌（玩家2）。牌库为 List，底 = 末尾 [Count-1]。</summary>
    public CardInstance DrawFromBottomPlayer2()
    {
        if (_player2Deck.Count == 0)
        {
            GD.PrintErr("❌ 玩家2卡组已空，无法从底抽牌");
            return null;
        }
        var card = _player2Deck[_player2Deck.Count - 1];
        _player2Deck.RemoveAt(_player2Deck.Count - 1);
        if (VerboseMoves) GD.Print($"🃏 玩家2从牌库底抽到卡牌: {card.PlayerTemplate?.CardName} (剩余 {_player2Deck.Count} 张)");
        return card;
    }

    // ============================================================
    // 返回卡组
    // ============================================================
    public void ReturnToDeckPlayer1(CardInstance card, bool toTop = false)
    {
        if (card == null) return;
        card.ResetToBaseState();
        // 注意：CurrentZone 由 ZoneManager.MoveToDeck 统一设置，本类只负责牌库列表增删
        card.ZoneDetailId = null;
        if (toTop)
            _player1Deck.Insert(0, card);
        else
            _player1Deck.Add(card);
        if (VerboseMoves) GD.Print($"↩️ 卡牌 {card.PlayerTemplate?.CardName} 返回玩家1卡组{(toTop ? "（顶部）" : "（底部）")}");
    }

    public void ReturnToDeckPlayer2(CardInstance card, bool toTop = false)
    {
        if (card == null) return;
        card.ResetToBaseState();
        // 注意：CurrentZone 由 ZoneManager.MoveToDeck 统一设置，本类只负责牌库列表增删
        card.ZoneDetailId = null;
        if (toTop)
            _player2Deck.Insert(0, card);
        else
            _player2Deck.Add(card);
        if (VerboseMoves) GD.Print($"↩️ 卡牌 {card.PlayerTemplate?.CardName} 返回玩家2卡组{(toTop ? "（顶部）" : "（底部）")}");
    }

    /// <summary>从对应玩家的牌库移除该卡（按 OwnerId 定位），供 ZoneManager 移动时同步牌组计数</summary>
    public void RemoveFromDeck(CardInstance card)
    {
        if (card == null) return;
        if (card.OwnerId == 1) _player1Deck.Remove(card);
        else if (card.OwnerId == 2) _player2Deck.Remove(card);
    }

    // ============================================================
    // 查看顶部牌（不取出）
    // ============================================================
    public List<CardInstance> PeekPlayer1(int count)
    {
        return Peek(_player1Deck, count);
    }

    public List<CardInstance> PeekPlayer2(int count)
    {
        return Peek(_player2Deck, count);
    }

    private List<CardInstance> Peek(List<CardInstance> deck, int count)
    {
        if (count <= 0) return new List<CardInstance>();
        int actualCount = System.Math.Min(count, deck.Count);
        return deck.GetRange(0, actualCount);
    }

    // ============================================================
    // 将卡牌放回卡组顶部/底部（用于调查对抗的牌序调整）
    // ============================================================
    public void PutOnTopPlayer1(List<CardInstance> cards)
    {
        PutOnTop(_player1Deck, cards);
    }

    public void PutOnTopPlayer2(List<CardInstance> cards)
    {
        PutOnTop(_player2Deck, cards);
    }

    private void PutOnTop(List<CardInstance> deck, List<CardInstance> cards)
    {
        if (cards == null || cards.Count == 0) return;
        // 逆序插入，保持原顺序
        for (int i = cards.Count - 1; i >= 0; i--)
        {
            deck.Insert(0, cards[i]);
        }
    }

    public void PutOnBottomPlayer1(List<CardInstance> cards)
    {
        PutOnBottom(_player1Deck, cards);
    }

    public void PutOnBottomPlayer2(List<CardInstance> cards)
    {
        PutOnBottom(_player2Deck, cards);
    }

    /// <summary>从牌库顶临时取出 count 张（不设置 zone/手牌，不触发 UI），供调查对抗"查看"使用。数量不足时取尽。</summary>
    public List<CardInstance> TakeTopPlayer1(int count)
    {
        return TakeTop(_player1Deck, count);
    }

    public List<CardInstance> TakeTopPlayer2(int count)
    {
        return TakeTop(_player2Deck, count);
    }

    private List<CardInstance> TakeTop(List<CardInstance> deck, int count)
    {
        if (count <= 0) return new List<CardInstance>();
        int actual = System.Math.Min(count, deck.Count);
        var taken = deck.GetRange(0, actual);
        deck.RemoveRange(0, actual);
        return taken;
    }

    private void PutOnBottom(List<CardInstance> deck, List<CardInstance> cards)
    {
        if (cards == null || cards.Count == 0) return;
        deck.AddRange(cards);
    }

    // ============================================================
    // 获取卡组数据
    // ============================================================
    public List<CardInstance> GetPlayer1DeckData() => new List<CardInstance>(_player1Deck);
    public List<CardInstance> GetPlayer2DeckData() => new List<CardInstance>(_player2Deck);
    public int GetPlayer1DeckCount() => _player1Deck.Count;
    public int GetPlayer2DeckCount() => _player2Deck.Count;

    // ============================================================
    // 调查对抗：牌库顺序重组（新 MVC 收口）
    // ============================================================
    /// <summary>取当前牌库剩余部分（即已被 TakeTopForInvestigation 取出调查卡的"未参与"部分）。</summary>
    public List<CardInstance> GetDeckPlayer1() => _player1Deck;
    public List<CardInstance> GetDeckPlayer2() => _player2Deck;

    /// <summary>用整体有序序列覆盖牌库（顶→底）。用于调查对抗后重建牌库顺序，确保真相唯一。</summary>
    public void SetDeckPlayer1(List<CardInstance> ordered) => _player1Deck = ordered;
    public void SetDeckPlayer2(List<CardInstance> ordered) => _player2Deck = ordered;
}
