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
        float spread = 5f;
        for (int i = 0; i < _graveyardCards.Count; i++)
        {
            var card = _graveyardCards[i];
            card.TargetPosition = basePos + new Vector2(i * spread, i * spread);
            card.AnchorLeft = 0;
            card.AnchorRight = 0;
            card.AnchorTop = 0;
            card.AnchorBottom = 0;
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