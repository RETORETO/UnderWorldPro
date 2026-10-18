using Godot;
using Script.Enums;
using Script.Game.Entities;
using Script.UI;
using System.Collections.Generic;

public partial class GraveyardZone : Control
{
    [Export] private PackedScene _cardUIScene;
    [Export] private Control _cardContainer;
    [Export] private Label _countLabel;

    private List<CardUI> _graveyardCards = new List<CardUI>();

    // 【P0】墓地是"缩略牌堆"：面板只有 100×140，卡面按面板尺寸缩放（刻意小于战场卡 140×196），
    // 故这里不引用 CardLayoutHelper；CustomMinimumSize 必须与 Size 同设，否则被 140×196 的最小尺寸钳回。
    private const float CARD_WIDTH = 100;
    private const float CARD_HEIGHT = 140;

    public void Initialize(PackedScene cardUIScene)
    {
        _cardUIScene = cardUIScene;
        _graveyardCards.Clear();
        ArrangeCards();
        UpdateCount();
    }

    /// <summary>
    /// 将 CardUI 添加到墓地 UI 容器（纯表现层操作）
    /// 注意：CurrentZone 由 CardManager.MoveCardToGraveyard 统一管理，此处不再修改
    /// </summary>
    public void AddCard(CardUI cardUI)
    {
        if (cardUI == null) return;
        // 从原父节点移除
        cardUI.GetParent()?.RemoveChild(cardUI);
        // 添加到墓地容器
        AddChild(cardUI);
        cardUI.SetFace(true); // 墓地显示正面
        if (!_graveyardCards.Contains(cardUI)) _graveyardCards.Add(cardUI);
        ArrangeCards();
        UpdateCount();
        GD.Print($"💀 卡牌 UI {cardUI.Instance?.PlayerTemplate?.CardName} 已加入墓地显示");
    }

    public void Clear()
    {
        foreach (var card in _graveyardCards)
            card.QueueFree();
        _graveyardCards.Clear();
        ArrangeCards();
        UpdateCount();
    }

    public void ArrangeCards()
    {
        if (_graveyardCards.Count == 0) return;

        Vector2 basePos = _cardContainer.GlobalPosition;
        // 【2026-10-19 用户口径】墓地错位**与牌库一致**：原写死 5px/张，几十张会斜出面板；
        // 现与 `DeckZone` 同源（`CardLayoutHelper.PileStep(count, 0.25f, 10f)`），张数多时自动压缩。
        float spread = CardLayoutHelper.PileStep(_graveyardCards.Count, 0.25f, 10f);
        for (int i = 0; i < _graveyardCards.Count; i++)
        {
            var card = _graveyardCards[i];
            card.TargetPosition = basePos + new Vector2(i * spread, i * spread);
            card.AnchorLeft = 0;
            card.AnchorRight = 0;
            card.AnchorTop = 0;
            card.AnchorBottom = 0;
            card.CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            card.Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            card.Scale = Vector2.One;
            card.RotationDegrees = 0f;   // 墓地卡强制立置，清除可能残留的横置旋转
            card.PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;
            card.ZIndex = i;
            card.SetFace(true);
        }
    }

    private void UpdateCount()
    {
        if (_countLabel != null)
            _countLabel.Text = $"墓地: {_graveyardCards.Count}";
    }

    public List<CardUI> GetCards() => _graveyardCards;
    public int Count => _graveyardCards.Count;
    public void RemoveCardUI(CardUI cardUI) => _graveyardCards.Remove(cardUI);
}