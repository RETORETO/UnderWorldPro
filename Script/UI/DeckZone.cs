using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.UI;
using System.Collections.Generic;

public partial class DeckZone : Control
{
    // ---------- 引用 ----------
    [Export] private PackedScene _cardUIScene;
    [Export] private Control _cardContainer;    // 用于定位卡组位置
    [Export] private Label _countLabel;         // 显示剩余牌数

    // ---------- 数据 ----------
    private List<CardUI> _deckCards = new List<CardUI>();
    private int _playerId = 1;
    public int Count => _deckCards.Count;
    // ---------- 常量 ----------
    // 【P0】牌库是"缩略牌堆"：面板本身只有 100×140，卡面按面板尺寸缩放显示（刻意小于战场卡 140×196），
    // 故这里**不引用** CardLayoutHelper，避免整堆卡面撑破面板。
    private const float CARD_WIDTH = 100;
    private const float CARD_HEIGHT = 140;

    // ---------- 初始化 ----------
    // 【MVC 修复 M-07】不再缓存 DeckManager 引用：初始化所需牌库数据由调用方（Arena，Composition Root）
    // 从 DeckManager 取出后注入，DeckZone 不持有任何 Manager 引用，彻底切断 View→Manager 反向依赖。
    public void Initialize(List<CardInstance> deckInstances, PackedScene cardUIScene, int playerId = 1)
    {
        _cardUIScene = cardUIScene;
        _playerId = playerId;
        _deckCards.Clear();

        if (deckInstances != null)
        {
            foreach (var instance in deckInstances)
            {
                var cardUI = _cardUIScene.Instantiate<CardUI>();
                AddChild(cardUI);
                cardUI.Setup(instance);
                cardUI.SetFace(false);
                _deckCards.Add(cardUI);
            }
            ArrangeCards();
            UpdateCount();
        }
    }

    // ---------- 抽牌（纯 UI 取卡：数据层抽牌已由 TurnManager.DrawCardsForPlayer 完成）----------
    // 仅从 UI 牌库显示列表头部取出对应 CardUI，不再直接调用 DeckManager.Draw（避免 UI 越权抽牌）。
    // 与 DeckManager.DrawPlayerX 取 _playerXDeck[0] 保持一致的"头部"顺序，保证 UI 与数据一一对应。
    public CardUI DrawCard()
    {
        if (_deckCards.Count == 0)
        {
            GD.PrintErr("【DeckZone】卡组已空，无法抽牌（数据层抽牌应已由 TurnManager 完成）");
            return null;
        }

        // 从头部取（数据层已由 TurnManager 移走对应 instance，这里仅移除 UI 节点）
        CardUI cardUI = _deckCards[0];
        _deckCards.RemoveAt(0);

        GD.Print($"【DeckZone】取出卡牌 UI: {cardUI.Instance?.PlayerTemplate?.CardName}");
        cardUI.SetFace(true);
        ArrangeCards();
        UpdateCount();
        return cardUI;
    }

    // ---------- 抽牌（事件驱动：数据层已由 TurnManager 抽好，这里只取对应 UI）----------
    // 用于响应 TurnManager.OnCardDrawn，避免数据层被重复抽取（双重抽牌 bug 的修复点）。
    public CardUI TakeCard(CardInstance instance)
    {
        if (instance == null) return null;

        int idx = _deckCards.FindIndex(c => c.Instance == instance);
        if (idx < 0)
        {
            GD.PrintErr($"【DeckZone】卡组中找不到实例对应的卡牌 UI: {instance.UID}");
            return null;
        }
        // 注意：当前抽牌 UI 归位已由 Arena.SyncCardUIToZone（OnCardMoved→Deck 分支）统一驱动，
        //       本方法目前无调用方，保留为纯 UI 取卡备用接口（与调查对抗 TakeTopCardsUI 对应）。

        CardUI cardUI = _deckCards[idx];
        _deckCards.RemoveAt(idx);
        GD.Print($"【DeckZone】取出卡牌 UI: {instance.PlayerTemplate?.CardName} (剩余 {_deckCards.Count})");
        ArrangeCards();
        UpdateCount();
        return cardUI;
    }

    // ---------- 返回卡组（仅 UI 层：单张卡放回牌库显示列表）----------
    // 配套通道：数据层 CardZone.Deck 移动经 ZoneManager.FireMoved 触发 Arena.SyncCardUIToZone，
    //          其 Deck 分支（Arena.cs:721）即调用本方法 —— 属于"数据移动自动同步 UI"通道。
    // 职责：仅把 CardUI 挂回 DeckZone 节点、翻面、加入 _deckCards 列表，不做任何数据层操作
    //      （数据层回牌库已由 ZoneManager.MoveToDeckBottom 委托 DeckManager 完成，避免重复计数）。
    // 注意：本方法适用于【单张/零散】回牌库（如争地区被破坏回牌库、玩家主动回牌库）。
    //      对于【调查对抗】这种需精确保持顶/底顺序的批量回牌库，走 InsertAfterInvestigationUI 通道，
    //      因为该路径 ZoneManager.ReturnAfterInvestigation 刻意不 FireMoved，改由调用方手动同步（见下）。
    public void ReturnCardUI(CardUI cardUI, bool toTop = false)
    {
        if (cardUI == null) return;
        cardUI.GetParent()?.RemoveChild(cardUI);
        AddChild(cardUI);              // 挂回 DeckZone（与 Initialize 一致）
        cardUI.SetFace(false);
        cardUI.FreezeSmoothMove = false;   // 解除可能残留的窗口冻结
        if (!_deckCards.Contains(cardUI)) _deckCards.Add(cardUI);
        ArrangeCards();
        UpdateCount();
    }

    /// <summary>从牌组 UI 列表移除（供 Arena 逻辑↔UI 同步时清理旧归属）</summary>
    public void RemoveCardUI(CardUI cardUI)
    {
        _deckCards.Remove(cardUI);
        ArrangeCards();
        UpdateCount();
    }

    // ============================================================
    // 调查对抗专用：与数据层 TakeTop/PutOnTop/PutOnBottom 严格同步 UI 层
    // 关键点：从牌库顶取出的卡必须同时移出 _deckCards，否则数据层与 UI 层错位，
    // 后续抽牌会出现"同一张卡被抽两次"的假象。
    // ============================================================
    /// <summary>从牌库顶（UI 层）取出前 n 张对应的 CardUI（与 DeckManager.TakeTop 一一对应）。</summary>
    public List<CardUI> TakeTopCardsUI(int n)
    {
        int actual = System.Math.Min(n, _deckCards.Count);
        var taken = _deckCards.GetRange(0, actual);
        _deckCards.RemoveRange(0, actual);
        ArrangeCards();
        UpdateCount();
        return taken;
    }

    /// <summary>把卡牌 UI 放回牌库顶（顺序：列表头在前）。与 DeckManager.PutOnTop 逆序插入保持一致。</summary>
    /// <remarks>属于【调查对抗手动同步通道】：ZoneManager.ReturnAfterInvestigation 刻意不 FireMoved，
    /// 因此不走 SyncCardUIToZone→ReturnCardUI，而由调用方 Arena.OnInvestigationConfirmed 直接调用本批方法，
    /// 以保证顶/底精确顺序且不会因双重 Insert 导致同一 CardUI 在 _deckCards 中重复。</remarks>
    public void InsertOnTopUI(List<CardUI> cards)
    {
        if (cards == null || cards.Count == 0) return;
        for (int i = cards.Count - 1; i >= 0; i--)
        {
            var cardUI = cards[i];
            if (cardUI == null) continue;
            cardUI.GetParent()?.RemoveChild(cardUI);
            AddChild(cardUI);
            cardUI.SetFace(false);
            cardUI.FreezeSmoothMove = false;   // 窗口内冻结结束，恢复平滑归位
            _deckCards.Insert(0, cardUI);
        }
        ArrangeCards();
        // 立即强制归位：从调查窗口返回的卡可能停在窗口坐标，避免从远处飞回卡组
        foreach (var cardUI in cards)
            if (cardUI != null) cardUI.GlobalPosition = cardUI.TargetPosition;
        UpdateCount();
    }

    /// <summary>把卡牌 UI 放回牌库底（顺序保持传入顺序）。</summary>
    /// <remarks>同属【调查对抗手动同步通道】，与 InsertOnTopUI 配合使用，详见其注释。</remarks>
    public void InsertOnBottomUI(List<CardUI> cards)
    {
        if (cards == null || cards.Count == 0) return;
        foreach (var cardUI in cards)
        {
            if (cardUI == null) continue;
            cardUI.GetParent()?.RemoveChild(cardUI);
            AddChild(cardUI);
            cardUI.SetFace(false);
            cardUI.FreezeSmoothMove = false;   // 窗口内冻结结束，恢复平滑归位
            _deckCards.Add(cardUI);
        }
        ArrangeCards();
        // 立即强制归位
        foreach (var cardUI in cards)
            if (cardUI != null) cardUI.GlobalPosition = cardUI.TargetPosition;
        UpdateCount();
    }

    /// <summary>调查对抗确认后统一放回：顶（逆序插头部，与数据层 PutOnTop 一致）+ 底（顺序插尾部）。</summary>
    public void InsertAfterInvestigationUI(List<CardUI> toTop, List<CardUI> toBottom)
    {
        InsertOnTopUI(toTop);
        InsertOnBottomUI(toBottom);
    }

    // ---------- 布局 ----------
    public void ArrangeCards()
    {
        if (_deckCards.Count == 0)
            return;

        // 先把场景树中子节点顺序对齐到逻辑列表 _deckCards 的顺序。
        // 否则 Insert(0,...)（放回牌库顶）只改了逻辑列表头部，但节点在树中仍是最后 AddChild 的位置，
        // 导致"牌库顶"的卡在渲染层次(ZIndex tie-break)上反而在最底层，与逻辑顺序/其他卡不统一。
        // 逆序 MoveChild(0) 可一次性把树序校正为正序，且不受下标偏移影响。
        for (int i = _deckCards.Count - 1; i >= 0; i--)
            MoveChild(_deckCards[i], 0);

        Vector2 basePos = _cardContainer.GlobalPosition;
        float spread = 0.5f;
        for (int i = 0; i < _deckCards.Count; i++)
        {
            var card = _deckCards[i];
            card.TargetPosition = basePos + new Vector2(i * spread, i * spread);
            card.AnchorLeft = 0;
            card.AnchorRight = 0;
            card.AnchorTop = 0;
            card.AnchorBottom = 0;
            card.CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);   // 必须与 Size 同设：CardUI._Ready 的最小尺寸是 140×196，只设 Size 会被钳回去
            card.Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            card.RotationDegrees = 0f;   // 牌库卡强制立置，清除可能残留的横置旋转
            card.PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
            // 牌库顶(索引0)应显示在最外层(最上层)，故 ZIndex 与索引反向：
            // 索引越大(越靠近牌库底) ZIndex 越小(越底层)，保证"放回牌库顶"的卡浮在最上面。
            card.ZIndex = _deckCards.Count - 1 - i;
            card.SetFace(false);
        }
    }

    private void UpdateCount()
    {
        if (_countLabel != null)
            _countLabel.Text = $"卡组: {_deckCards.Count}";
    }

    // ---------- 工具 ----------
    public List<CardUI> GetCards() => _deckCards;

    // ============================================================
    // 调查对抗后：按 ZoneManager 编排后的有序牌库序列重建 UI（纯重绘，不写数据层）
    // 牌库顺序真相唯一由 ZoneManager 决定，DeckZone 只据此重建 _deckCards 顺序并归位。
    // ============================================================
    public void RebuildFromOrdered(List<CardInstance> orderedDeck)
    {
        if (orderedDeck == null) return;

        // 1) 收集现有 _deckCards 中已建立的 instance->CardUI 映射，便于复用
        var existing = new Dictionary<string, CardUI>();
        foreach (var ui in _deckCards)
            if (ui?.Instance != null) existing[ui.Instance.UID] = ui;

        // 2) 按有序序列重建 _deckCards（复用已有 CardUI，缺失则新实例化）
        var rebuilt = new List<CardUI>();
        foreach (var inst in orderedDeck)
        {
            if (inst == null) continue;
            if (existing.TryGetValue(inst.UID, out var ui) && ui != null)
            {
                ui.GetParent()?.RemoveChild(ui);
                AddChild(ui);
                ui.SetFace(false);
                ui.FreezeSmoothMove = false;
                rebuilt.Add(ui);
            }
            else
            {
                var newUI = _cardUIScene.Instantiate<CardUI>();
                AddChild(newUI);
                newUI.Setup(inst);
                newUI.SetFace(false);
                rebuilt.Add(newUI);
            }
        }

        _deckCards = rebuilt;
        ArrangeCards();
        UpdateCount();
        GD.Print($"【DeckZone】已按有序牌库重建 UI，共 {_deckCards.Count} 张");
    }


}