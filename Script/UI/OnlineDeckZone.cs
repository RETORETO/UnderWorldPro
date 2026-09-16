using Godot;
using Script.Game.Entities;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 【联机专用·牌库区】显示"卡背堆叠 + 剩余数"，与单机 DeckZone 视觉一致但不依赖卡模板：
    /// 用 `CardUI.Setup(null)` 只画卡背，因此可安全渲染**信息隔离**下的隐藏占位（对手牌库/地区牌堆）。
    ///
    /// 为什么不复用单机 `DeckZone`：它按 `CardInstance` 真实模板渲染卡面（`Setup(instance)`），
    /// 遇到无模板的隐藏占位会走"PlayerTemplate 为 null"的错误分支；为避免改动单机 UI 类，这里新建联机组件。
    /// </summary>
    [GlobalClass]
    public partial class OnlineDeckZone : Control
    {
        [Export] private Control _cardContainer;   // 卡背摆放容器（与 DeckZone 场景同名导出，便于复用节点路径）
        [Export] private Label _countLabel;        // 剩余牌数

        private readonly List<CardUI> _backs = new List<CardUI>();
        private PackedScene _cardUIScene;

        private const float CARD_WIDTH = 100f;
        private const float CARD_HEIGHT = 140f;
        private const float PILE_OFFSET = 2f;      // 堆叠错位（视觉上像一叠牌）

        public int Count => _backs.Count;

        /// <summary>清空卡背节点。</summary>
        public void Clear()
        {
            foreach (var b in _backs)
                if (b != null && GodotObject.IsInstanceValid(b)) b.QueueFree();
            _backs.Clear();
        }

        /// <summary>按数量渲染卡背（联机牌库/地区牌堆都只暴露数量）。</summary>
        public void Initialize(int count, PackedScene cardUIScene)
        {
            _cardUIScene = cardUIScene;
            Clear();
            if (_cardUIScene == null) { UpdateCount(); return; }

            var container = _cardContainer ?? this;
            for (int i = 0; i < count; i++)
            {
                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                ui.Setup(null);              // 无实例 → 只画卡背（不触碰模板/卡图加载）
                ui.SetFace(false);
                ui.CanDrag = false;
                ui.FreezeSmoothMove = true;  // 位置由本组件固定，禁用平滑归位
                ui.ProcessMode = ProcessModeEnum.Disabled;
                ui.AnchorLeft = 0; ui.AnchorRight = 0; ui.AnchorTop = 0; ui.AnchorBottom = 0;
                ui.Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
                ui.Position = new Vector2(i * PILE_OFFSET, -i * PILE_OFFSET);   // 轻微错位叠放
                ui.ZIndex = i;
                _backs.Add(ui);
            }
            UpdateCount();
        }

        /// <summary>兼容单机 DeckZone.Initialize 的签名：联机只关心数量，实例内容一律不显示。</summary>
        public void Initialize(List<CardInstance> deckInstances, PackedScene cardUIScene, int playerId = 1)
            => Initialize(deckInstances?.Count ?? 0, cardUIScene);

        private void UpdateCount()
        {
            if (_countLabel != null && GodotObject.IsInstanceValid(_countLabel))
                _countLabel.Text = Count.ToString();
        }
    }
}
