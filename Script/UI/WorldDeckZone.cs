using Godot;
using Script.Data.Cards;
using Script.UI;
using System.Collections.Generic;

public partial class WorldDeckZone : Control
{
    [Export] private Control _cardContainer;
    [Export] private Label _countLabel;

    // 【MVC 修复 M-07】不再缓存 WorldDeckManager 引用：UI 组件只持派生数据（牌库计数），
    // 不直接耦合 Manager。计数由外部（Arena）在事件驱动刷新时注入，避免 View→Manager 反向依赖。
    private int _countCache;
    private PackedScene _areaCardUIScene;
    private List<AreaCardUI> _displayCards = new List<AreaCardUI>();

    private const float CARD_WIDTH = 140;
    private const float CARD_HEIGHT = 100;

    public override void _Ready()
    {
        _cardContainer = GetNode<Control>("CardContainer");
        if (_cardContainer == null) GD.PrintErr("WorldDeckZone: CardContainer not found!");
    }

    /// <summary>初始化。count 为初始世界牌库数量（由调用方从 WorldDeckManager.Count 读取后传入）。</summary>
    public void Initialize(int count, PackedScene cardUIScene)
    {
        _countCache = count;
        _areaCardUIScene = cardUIScene;
        RefreshDisplay(_countCache);
    }

    /// <summary>刷新显示。count 为世界牌库最新数量，由外部在事件驱动刷新时注入。</summary>
    public void RefreshDisplay(int count)
    {
        _countCache = count;
        // 清除旧卡背
        foreach (var child in _cardContainer.GetChildren())
            child.QueueFree();
        _displayCards.Clear();

        int displayCount = System.Math.Min(_countCache, 20);

        for (int i = 0; i < displayCount; i++)
        {
            var cardUI = _areaCardUIScene.Instantiate<AreaCardUI>();
            _cardContainer.AddChild(cardUI);
            cardUI.SetFace(false);
            // 世界牌库仍是 140×100 缩略（不进争夺区），必须**先**压掉场景最小尺寸再设 Size，
            // 否则 `.tscn` 里的 196×140 会把缩小请求钳回去（地区卡真身已加宽，缩略图不跟）。
            cardUI.CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            cardUI.Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            cardUI.Position = new Vector2(i * 0.5f, i * 0.5f);
            cardUI.MouseFilter = MouseFilterEnum.Ignore; // 让事件穿透
            _displayCards.Add(cardUI);
        }

        if (_countLabel != null)
            _countLabel.Text = _countCache.ToString();
    }

    public int Count => _countCache;
}
