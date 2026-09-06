using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;
using Script.Data.Cards;
using Script.Data.Effects;

namespace Script.UI
{
    /// <summary>
    /// 【联机战场节点·快照驱动】单机用 Arena（事件驱动），跨进程联机用 ArenaOl（快照驱动）。
    ///
    /// 布局完全复用 res://Scenes/Net/ArenaOl.tscn（从 Arena.tscn 复制，区域容器/按钮位置与单机一致），
    /// 仅驱动方式分离：
    ///  - 纯快照驱动：收到快照 → 从客户端投影（launcher.ClientZoneManager 等）取卡实例 →
    ///    重建卡 UI → 用 CardLayoutHelper 排版（与单机视觉一致）。
    ///  - 操作输入：让过/回合结束按钮 → launcher.SendIntentAs 上行意图，服务端权威结算。
    ///
    /// 与单机 Arena 完全隔离，不触碰其事件驱动逻辑。
    /// </summary>
    [GlobalClass]
    public partial class ArenaOl : Control
    {
        private NetworkMatchLauncher _launcher;
        private int _localPlayerId = 1;

        private Control _p1Hand;
        private Control _p2Hand;
        private Control _areasContainer;

        // 专用区域组件（与单机 Arena 一致）：牌库/墓地/世界牌库用各自的组件渲染，
        // 手牌用 CardUI 渲染。联机快照驱动同样复用这些组件，确保视觉与单机一致。
        private DeckZone _p1DeckZone;
        private DeckZone _p2DeckZone;
        private GraveyardZone _p1GraveZone;
        private GraveyardZone _p2GraveZone;
        private WorldDeckZone _worldDeckZone;

        private Label _phaseLabel;
        private Label _p1ScoreLabel;
        private Label _p2ScoreLabel;

        private PackedScene _cardUIScene;
        private PackedScene _areaCardUIScene;
        private PackedScene _areaSlotScene;
        private PackedScene _councilUIScene;   // 秘社卡 UI（CouncilCardUI.tscn）
        private PackedScene _stackEntryScene;  // 堆叠条目 UI（StackEntryUI.tscn）
        private List<AreaSlot> _areaSlots = new List<AreaSlot>();
        private readonly Dictionary<string, CardUI> _cardUIMap = new Dictionary<string, CardUI>();
        private readonly Dictionary<string, CouncilCardUI> _councilCardUIMap = new Dictionary<string, CouncilCardUI>();
        private Control _assetContainer1, _assetContainer2;   // 资产区（P1/P2）
        private Control _scoreContainer1, _scoreContainer2;   // 分数区（P1/P2）
        private Control _councilContainer1, _councilContainer2; // 秘社区（P1/P2）
        private Control _stackContainer;      // 堆叠区容器
        private Label _resultLabel;           // 游戏结束结算文字
        private Button _concedeButton;        // 投降按钮
        private ChoicePromptPanel _choicePromptPanel;   // 联机决策面板（收到 ChoiceRequest 时弹出）
        private TextEdit _commandInput;       // PM 调试命令输入（Shift+Enter 上行 DebugCommand）

        // ==================== 联机拖拽（出牌/派遣暗藏/建立资产） ====================
        // 联机为快照驱动：拖拽结束不本地移动卡，只上行对应意图；服务端权威裁决后靠快照回显。
        // 蒙版模式：DeployCovert=派遣暗藏（需落到己方争夺区），EstablishAsset=建立资产（拖出即建立）。
        private enum MaskMode { None, DeployCovert, EstablishAsset }
        private MaskMode _maskMode = MaskMode.None;
        private Control _localHand;      // 本地玩家手牌容器（拖拽来源）
        private int _localHandOwner;     // 本地玩家号（与 _localPlayerId 一致，拖拽时校验）

        public override void _Ready()
        {
            _cardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");

            _p1Hand = GetNodeOrNull<Control>("Player1Area/HandContainer");
            _p2Hand = GetNodeOrNull<Control>("Player2Area/HandContainer");
            _areasContainer = GetNodeOrNull<Control>("CenterArea/AreasContainer");
            _p1DeckZone = GetNodeOrNull<DeckZone>("Player1Area/DeckZone");
            _p2DeckZone = GetNodeOrNull<DeckZone>("Player2Area/DeckZone");
            _p1GraveZone = GetNodeOrNull<GraveyardZone>("Player1Area/GraveyardZone");
            _p2GraveZone = GetNodeOrNull<GraveyardZone>("Player2Area/GraveyardZone");
            _worldDeckZone = GetNodeOrNull<WorldDeckZone>("CenterArea/WorldDeckZone");
            _assetContainer1 = GetNodeOrNull<Control>("Player1Area/AssetContainer");
            _assetContainer2 = GetNodeOrNull<Control>("Player2Area/AssetContainer");
            _phaseLabel = GetNodeOrNull<Label>("ActionPanel/PhaseLabel");
            _p1ScoreLabel = GetNodeOrNull<Label>("Player1Area/ScoreZone/ScoreLabel");
            _p2ScoreLabel = GetNodeOrNull<Label>("Player2Area/ScoreZone/ScoreLabel");
            _scoreContainer1 = GetNodeOrNull<Control>("Player1Area/ScoreZone/CardContainer");
            _scoreContainer2 = GetNodeOrNull<Control>("Player2Area/ScoreZone/CardContainer");
            _councilContainer1 = GetNodeOrNull<Control>("Player1Area/CouncilContainer");
            _councilContainer2 = GetNodeOrNull<Control>("Player2Area/CouncilContainer");
            _stackContainer = GetNodeOrNull<Control>("StackPanel/StackContainer");
            _resultLabel = GetNodeOrNull<Label>("CenterArea/ResultLabel");
            _concedeButton = GetNodeOrNull<Button>("ActionPanel/ConcedeButton");

            if (_areaCardUIScene == null)
                _areaCardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaCardUI.tscn");
            if (_areaSlotScene == null)
                _areaSlotScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaSlot.tscn");
            if (_councilUIScene == null)
                _councilUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CouncilCardUI.tscn");
            if (_stackEntryScene == null)
                _stackEntryScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/StackEntryUI.tscn");

            var passBtn = GetNodeOrNull<Button>("ActionPanel/PassButton");
            if (passBtn != null) passBtn.Pressed += () => DoPass(_localPlayerId);
            var endTurnBtn = GetNodeOrNull<Button>("ActionPanel/EndTurnButton");
            if (endTurnBtn != null) endTurnBtn.Pressed += () => DoEndTurn(_localPlayerId);

            // 基础行动：派遣暗藏 / 建立资产（联机→上行意图，服务端裁决）
            var deployBtn = GetNodeOrNull<Button>("Player1Area/DeployCovertButton");
            if (deployBtn != null) deployBtn.Pressed += () => ToggleMask(MaskMode.DeployCovert, deployBtn);
            var assetBtn = GetNodeOrNull<Button>("Player1Area/EstablishAssetButton");
            if (assetBtn != null) assetBtn.Pressed += () => ToggleMask(MaskMode.EstablishAsset, assetBtn);
            if (_concedeButton != null) _concedeButton.Pressed += () => DoConcede(_localPlayerId);

            // PM 调试命令输入框（联机客户端：上行 DebugCommand 意图，由服务端权威执行）
            _commandInput = GetNodeOrNull<TextEdit>("CommandInput");
            if (_commandInput != null)
            {
                _commandInput.Visible = false;
                _commandInput.GuiInput += OnCommandInputGuiInput;
            }
        }

        public void AttachLauncher(NetworkMatchLauncher launcher, int localPlayerId = 1)
        {
            _launcher = launcher;
            _localPlayerId = localPlayerId;
            if (_launcher != null)
            {
                _launcher.OnClientSnapshot += ApplySnapshot;
                _launcher.OnClientChoiceRequest += OnClientChoiceRequest;
            }
        }

        public void DetachLauncher()
        {
            if (_launcher != null)
            {
                _launcher.OnClientSnapshot -= ApplySnapshot;
                _launcher.OnClientChoiceRequest -= OnClientChoiceRequest;
            }
            _launcher = null;
        }

        /// <summary>服务端下发决策请求：弹出联机决策面板，玩家选择后回包。</summary>
        private void OnClientChoiceRequest(Script.Net.ChoiceRequest request)
        {
            if (request == null) return;
            if (_choicePromptPanel == null || !GodotObject.IsInstanceValid(_choicePromptPanel))
            {
                _choicePromptPanel = new ChoicePromptPanel();
                AddChild(_choicePromptPanel);
            }
            _choicePromptPanel.Init(request, ResolveChoiceLabel, result =>
            {
                if (_launcher != null)
                    _launcher.SendChoiceResult(result);
            });
            var vpSize = GetViewport().GetVisibleRect().Size;
            _choicePromptPanel.Position = new Vector2I((int)((vpSize.X - _choicePromptPanel.Size.X) / 2), (int)(vpSize.Y / 2) + 120);
            _choicePromptPanel.Popup();
        }

        /// <summary>把决策请求中的 UID 解析成可读文字（卡名/地区名/触发名）。</summary>
        private string ResolveChoiceLabel(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return "（未命名）";
            // 玩家卡（手牌/争夺区/附属等）
            var inst = _launcher?.ClientZoneManager != null ? FindInstanceInAllZones(uid) : null;
            if (inst != null)
            {
                if (inst.PlayerTemplate != null) return inst.PlayerTemplate.CardName ?? uid;
                if (inst.AreaTemplate != null) return inst.AreaTemplate.CardName ?? uid;
                if (inst.CouncilTemplate != null) return inst.CouncilTemplate.CardName ?? uid;
            }
            // 地区实例（独立 CardManager 中的地区卡）
            var area = _launcher?.ClientAreaManager?.GetOrderedAreaInstances()?.Find(a => a != null && a.UID == uid);
            if (area?.AreaTemplate != null) return area.AreaTemplate.CardName ?? uid;
            return uid;
        }

        /// <summary>在客户端投影 ZoneManager 的全部区域桶里按 UID 查卡实例。</summary>
        private Script.Game.Entities.CardInstance FindInstanceInAllZones(string uid)
        {
            var zone = _launcher?.ClientZoneManager;
            if (zone == null) return null;
            foreach (var z in new[] { CardZone.Hand, CardZone.Graveyard, CardZone.AssetZone, CardZone.AreaZone, CardZone.AttachedZone, CardZone.SealedZone, CardZone.CouncilZone, CardZone.ScoreZone, CardZone.Stack })
            {
                foreach (var inst in zone.GetZoneCards(z, 0))
                    if (inst != null && inst.UID == uid) return inst;
                for (int p = 1; p <= 2; p++)
                    foreach (var inst in zone.GetZoneCards(z, p))
                        if (inst != null && inst.UID == uid) return inst;
            }
            return null;
        }

        // ==================== 快照渲染（全量重建） ====================
        private void ApplySnapshot(GameSnapshot snap)
        {
            if (_launcher == null) return;
            ClearAllUI();

            var zone = _launcher.ClientZoneManager;
            if (zone != null)
            {
                // 手牌/墓地/资产区：ZoneManager 投影区域桶（玩家卡，PlayerTemplate != null）
                RebuildInto(_p1Hand, zone.GetZoneCards(CardZone.Hand, 1));
                RebuildInto(_p2Hand, zone.GetZoneCards(CardZone.Hand, 2));
                RebuildGraveyards(zone.GetZoneCards(CardZone.Graveyard, 1), zone.GetZoneCards(CardZone.Graveyard, 2));
                RebuildAssets(zone.GetZoneCards(CardZone.AssetZone, 1), zone.GetZoneCards(CardZone.AssetZone, 2));
            }

            // 牌库：数据在独立投影 DeckManager（ZoneManager.GetZoneCards(Deck) 恒空），从 ClientDeckManager 取
            RebuildDecks();

            // 场上地区：数据在独立投影 AreaManager（不在 ZoneManager 的 AreaZone 桶），从 ClientAreaManager 取
            RebuildAreas();

            // 争夺区角色/暗藏卡：ZoneManager 的 AreaZone 桶，按地区 + 控制者归属到对应 AreaSlot 的争夺区
            if (zone != null)
                RebuildAreaZoneCards(zone.GetZoneCards(CardZone.AreaZone, 0));

            // P2：秘社区 / 附属封印区 / 分数区 / 堆叠 / 势力标记（与单机 Arena 对齐）
            RebuildCouncils();
            RebuildAttachmentsAndSeals();
            RebuildScoreZone(snap);
            RefreshStackUI();
            RefreshAreaMarks();

            RebuildWorldDeck();

            var turn = _launcher.ClientTurnManager;
            if (_phaseLabel != null && turn != null)
                _phaseLabel.Text = $"回合{turn.TurnCount} 阶段={turn.CurrentPhase} 优先权={turn.PriorityPlayerId}";

            // 分数显示：优先用快照里的胜利分数；旧快照无该字段时回退到法力显示
            if (_p1ScoreLabel != null && snap.Players.Count > 0)
                _p1ScoreLabel.Text = snap.Players[0].ScoreTotal != 0 ? $"P1 分数 {snap.Players[0].ScoreTotal}" : $"P1 法力 {snap.Players[0].AvailableMana}";
            if (_p2ScoreLabel != null && snap.Players.Count > 1)
                _p2ScoreLabel.Text = snap.Players[1].ScoreTotal != 0 ? $"P2 分数 {snap.Players[1].ScoreTotal}" : $"P2 法力 {snap.Players[1].AvailableMana}";

            // 游戏结束展示（IsGameOver 由服务端 PlayerManager 权威广播）
            if (_resultLabel != null)
            {
                bool over = snap.Players.Exists(p => p.IsGameOver);
                _resultLabel.Visible = over;
                if (over)
                {
                    int winner = snap.Players.Find(p => p.IsGameOver)?.PlayerId ?? 0;
                    _resultLabel.Text = winner == 1 ? "玩家1 胜利！" : "玩家2 胜利！";
                }
            }

            ArrangeAll();
        }

        /// <summary>重建双方玩家牌库：用 DeckZone 组件显示卡背堆叠 + 数量（与单机一致）。
        /// 牌库数据在客户端投影 DeckManager（ZoneManager.GetZoneCards(Deck) 恒空）。</summary>
        private void RebuildDecks()
        {
            var deckMgr = _launcher?.ClientDeckManager;
            if (deckMgr == null) return;
            var d1 = deckMgr.GetDeckPlayer1();
            var d2 = deckMgr.GetDeckPlayer2();
            // DeckZone.Initialize 只清内部列表不清已有子节点，快照全量重建前先清掉旧卡 UI，避免跨快照堆积
            ClearDeckChildren(_p1DeckZone);
            ClearDeckChildren(_p2DeckZone);
            _p1DeckZone?.Initialize(d1, _cardUIScene, 1);
            _p2DeckZone?.Initialize(d2, _cardUIScene, 2);
        }

        /// <summary>清空 DeckZone 节点下的旧 CardUI 子节点（保留 CountLabel/CardContainer 等静态子节点）。</summary>
        private void ClearDeckChildren(DeckZone deckZone)
        {
            if (deckZone == null) return;
            foreach (Node child in deckZone.GetChildren())
                if (child is CardUI c && GodotObject.IsInstanceValid(c)) c.QueueFree();
        }

        /// <summary>重建双方墓地：用 GraveyardZone 组件显示正面堆叠 + 数量（与单机一致）。</summary>
        private void RebuildGraveyards(System.Collections.Generic.List<CardInstance> grave1, System.Collections.Generic.List<CardInstance> grave2)
        {
            // 先 Clear() 释放上一快照的旧卡节点，再 Initialize 设场景引用，最后逐张 AddCard
            if (_p1GraveZone != null) { _p1GraveZone.Clear(); _p1GraveZone.Initialize(_cardUIScene); }
            if (_p2GraveZone != null) { _p2GraveZone.Clear(); _p2GraveZone.Initialize(_cardUIScene); }
            RebuildGraveyardInto(_p1GraveZone, grave1);
            RebuildGraveyardInto(_p2GraveZone, grave2);
        }

        private void RebuildGraveyardInto(GraveyardZone graveZone, System.Collections.Generic.List<CardInstance> instances)
        {
            if (graveZone == null || instances == null) return;
            foreach (var inst in instances)
            {
                if (inst == null || inst.PlayerTemplate == null) continue;   // 墓地只放玩家卡
                var ui = _cardUIScene.Instantiate<CardUI>();
                ui.Setup(inst);
                graveZone.AddCard(ui);
            }
        }

        /// <summary>重建双方资产区：把资产卡显示到各自 AssetContainer（横排，费用左上、横置视觉）。
        /// 建立资产后，卡进 AssetZone 桶，由这里渲染，避免"手牌减了但资产区空"。</summary>
        private void RebuildAssets(System.Collections.Generic.List<CardInstance> asset1, System.Collections.Generic.List<CardInstance> asset2)
        {
            RebuildAssetsInto(_assetContainer1, asset1);
            RebuildAssetsInto(_assetContainer2, asset2);
        }

        private void RebuildAssetsInto(Control container, System.Collections.Generic.List<CardInstance> instances)
        {
            if (container == null) return;
            // 清空容器旧卡（立即移除+释放，避免延迟 QueueFree 残留）
            foreach (Node child in container.GetChildren())
                if (child is CardUI oc && GodotObject.IsInstanceValid(oc))
                {
                    container.RemoveChild(oc);
                    oc.QueueFree();
                }
            if (instances == null) return;

            float x = 0f;
            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                if (inst?.PlayerTemplate == null) continue;
                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                ui.Setup(inst);
                ui.SetTapped(inst.IsTapped);   // 使用费用后横置资产卡：同步服务端 IsTapped
                ui.CanDrag = false;   // 资产区只读展示（联机下后续可扩展"横置/消耗"意图，暂不拖拽）
                // 用共享排版定位：同时设置 Position 与 TargetPosition，避免 CardUI._Process 平滑移动
                // 把卡拉回默认 (0,0) 导致"飘到屏幕左上角"；PlaceCardInContainer 也会按 IsTapped 旋转。
                CardLayoutHelper.PlaceCardInContainer(container, ui, x, i);
                _cardUIMap[inst.UID] = ui;
                x += 105f;   // 横向排布，略重叠
            }
        }

        /// <summary>
        /// 重建争夺区角色/暗藏卡：把 AreaZone 桶的卡按所在地区(ZoneDetailId) + 控制者(ControllerId)
        /// 归属到对应 AreaSlot 的争夺区容器（P1→Player1Area，P2→Player2Area），
        /// 同步正背面(暗藏卡背面)与横置状态，并用共享排版均匀平铺。
        /// 解决"派遣暗藏/出牌到争夺区后 UI 无体现"。
        /// </summary>
        private void RebuildAreaZoneCards(System.Collections.Generic.List<CardInstance> areaZoneCards)
        {
            // 1) 清空每个 AreaSlot 争夺区容器的旧角色卡 UI
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                ClearContainerCards(slot.Player1Area);
                ClearContainerCards(slot.Player2Area);
            }

            if (areaZoneCards == null) return;

            // 2) 按 (地区UID, 容器) 分组，方便每容器均匀平铺
            var groups = new Dictionary<Control, System.Collections.Generic.List<Control>>();
            foreach (var inst in areaZoneCards)
            {
                if (inst?.PlayerTemplate == null) continue;   // 只渲染玩家卡（角色/暗藏者）
                if (string.IsNullOrEmpty(inst.ZoneDetailId)) continue;
                var slot = _areaSlots.Find(s => s != null && s.AreaUID == inst.ZoneDetailId);
                if (slot == null) continue;
                var container = inst.ControllerId == 1 ? slot.Player1Area : slot.Player2Area;
                if (container == null) continue;

                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                ui.Setup(inst);
                ui.SetFace(!inst.IsCovert);      // 暗藏卡(IsCovert)显示背面，普通角色正面
                ui.SetTapped(inst.IsTapped);     // 同步横置（使用费用/攻击后）
                ui.CanDrag = false;              // 争夺区只读展示（联机下后续再接入攻击/横置意图）
                _cardUIMap[inst.UID] = ui;
                // 剩余交互：暗藏双击现身 / 附属双击激活（上行意图，服务端权威裁决）
                ui.CovertRevealRequested += OnCovertRevealRequested;
                ui.AttachmentDoubleClicked += OnAttachmentDoubleClicked;

                if (!groups.TryGetValue(container, out var list)) { list = new System.Collections.Generic.List<Control>(); groups[container] = list; }
                list.Add(ui);
            }

            // 3) 各容器均匀平铺（含横置旋转视觉宽度）
            foreach (var kv in groups)
                CardLayoutHelper.LayoutCardsEvenly(kv.Key, kv.Value);
        }

        /// <summary>清空容器下的 CardUI 子节点（立即移除+释放，避免延迟 QueueFree 残留）。</summary>
        private void ClearContainerCards(Control container)
        {
            if (container == null) return;
            foreach (Node child in container.GetChildren())
                if (child is CardUI oc && GodotObject.IsInstanceValid(oc))
                {
                    container.RemoveChild(oc);
                    oc.QueueFree();
                }
        }

        // ==================== P2：四区域 UI / 堆叠 / 势力标记 / 剩余交互 ====================

        /// <summary>重建双方秘社区（CouncilZone）：用 CouncilCardUI 显示秘社卡（正面、横置同步）。</summary>
        private void RebuildCouncils()
        {
            if (_councilUIScene == null) return;
            var zone = _launcher?.ClientZoneManager;
            if (zone == null) return;

            foreach (var ui in _councilCardUIMap.Values)
                if (ui != null && GodotObject.IsInstanceValid(ui)) ui.QueueFree();
            _councilCardUIMap.Clear();

            for (int p = 1; p <= 2; p++)
            {
                var container = p == 1 ? _councilContainer1 : _councilContainer2;
                if (container == null) continue;
                foreach (Node child in container.GetChildren())
                    if (child is CouncilCardUI ccu)
                    {
                        container.RemoveChild(ccu);
                        ccu.QueueFree();
                    }

                var cards = new System.Collections.Generic.List<Control>();
                foreach (var inst in zone.GetZoneCards(CardZone.CouncilZone, p))
                {
                    if (inst == null || inst.CouncilTemplate == null) continue;
                    var ui = _councilUIScene.Instantiate<CouncilCardUI>();
                    container.AddChild(ui);
                    ui.Setup(inst);
                    ui.SetTapped(inst.IsTapped);
                    _councilCardUIMap[inst.UID] = ui;
                    ui.CouncilDoubleClicked += OnCouncilDoubleClicked;
                    cards.Add(ui);
                }
                // 与单机 Arena 一致：经共享排版设置位置/尺寸/枢轴/TargetPosition（全局），
                // 否则 CouncilCardUI._Process 会把卡平滑拉向"本地坐标被当作全局坐标"的屏幕左上角。
                CardLayoutHelper.LayoutCardsEvenly(container, cards);
            }
        }

        /// <summary>重建附属/封印区（AttachedZone / SealedZone）：挂到宿主卡同容器并相对宿主对齐。
        /// 全量重建语义：宿主卡已由 RebuildAreaZoneCards / RebuildCouncils 先行渲染，此处只补附属/封印卡。
        /// 注意：必须"先 AddChild 再 Setup"（Setup 在 AddChild 前会被 _Ready 的默认 SetFace(false) 覆盖成卡背）。</summary>
        private void RebuildAttachmentsAndSeals()
        {
            var zone = _launcher?.ClientZoneManager;
            if (zone == null) return;

            var cards = new System.Collections.Generic.List<CardInstance>();
            var att = zone.GetZoneCards(CardZone.AttachedZone, 0);
            var seal = zone.GetZoneCards(CardZone.SealedZone, 0);
            if (att != null) cards.AddRange(att);
            if (seal != null) cards.AddRange(seal);

            // 附属于地区/封印于地区的卡：按容器分组，全部渲染后统一与容器内角色卡平铺（CardLayoutHelper 会按 IsTapped 旋转）。
            var areaGroup = new System.Collections.Generic.Dictionary<Control, System.Collections.Generic.List<CardUI>>();

            foreach (var inst in cards)
            {
                if (inst == null || inst.PlayerTemplate == null) continue;
                bool isSeal = inst.IsSeal;

                CardInstance host = inst.IsAttached ? inst.AttachedTo : inst.SealedBy;
                if (host != null)
                {
                    // —— 结附/封印于卡（角色或秘社）：挂宿主同容器，相对宿主对齐 ——
                    Control hostUi = null;
                    if (host.CurrentZone == CardZone.CouncilZone)
                    {
                        if (_councilCardUIMap.TryGetValue(host.UID, out var cui) && cui != null) hostUi = cui;
                    }
                    else if (_cardUIMap.TryGetValue(host.UID, out var hui) && hui != null) hostUi = hui;

                    if (hostUi == null) continue;   // 宿主 UI 未渲染（不应发生），跳过
                    var hostContainer = hostUi.GetParent() as Control;
                    if (hostContainer == null) continue;

                    // 先入树再 Setup（修复：Setup 在 AddChild 前会被 _Ready 覆盖成卡背）
                    var ui = _cardUIScene.Instantiate<CardUI>();
                    hostContainer.AddChild(ui);
                    ui.Setup(inst);
                    ui.SetTapped(inst.IsTapped);
                    ui.CanDrag = false;
                    ui.AttachmentDoubleClicked += OnAttachmentDoubleClicked;
                    _cardUIMap[inst.UID] = ui;

                    // 相对宿主定位（含宿主对齐、横置旋转、多张下延/上延、ZIndex）
                    int idx = 0;
                    var hostList = isSeal ? host.SealedCards : host.AttachedCards;
                    if (hostList != null) { int k = hostList.IndexOf(inst); if (k >= 0) idx = k; }
                    LayoutAttachmentRelative(ui, hostUi, isSeal, idx);
                }
                else
                {
                    // —— 附属于地区 / 封印于地区：挂到对应 AreaSlot 争夺区容器，随后统一平铺 ——
                    var slot = _areaSlots.Find(s => s != null && s.AreaUID == inst.ZoneDetailId);
                    var hostContainer = inst.ControllerId == 1 ? slot?.Player1Area : slot?.Player2Area;
                    if (hostContainer == null) continue;

                    var ui = _cardUIScene.Instantiate<CardUI>();
                    hostContainer.AddChild(ui);
                    ui.Setup(inst);
                    ui.SetTapped(inst.IsTapped);
                    ui.CanDrag = false;
                    ui.AttachmentDoubleClicked += OnAttachmentDoubleClicked;
                    _cardUIMap[inst.UID] = ui;

                    if (!areaGroup.TryGetValue(hostContainer, out var list)) { list = new System.Collections.Generic.List<CardUI>(); areaGroup[hostContainer] = list; }
                    list.Add(ui);
                }
            }

            // 结附/封印于地区的卡：与容器内角色卡一起均匀平铺（仅角色 + 地区附属，排除已相对宿主偏移的结附卡附属）。
            foreach (var kv in areaGroup)
            {
                var all = new System.Collections.Generic.List<Control>();
                foreach (Node child in kv.Key.GetChildren())
                {
                    if (child is not CardUI c || !GodotObject.IsInstanceValid(c) || c.Instance == null) continue;
                    bool isRole = c.Instance.CurrentZone == CardZone.AreaZone;
                    bool isAreaAttach = (c.Instance.IsAttached || c.Instance.IsSeal)
                                        && c.Instance.AttachedTo == null && c.Instance.SealedBy == null;
                    if (isRole || isAreaAttach) all.Add(c);
                }
                CardLayoutHelper.LayoutCardsEvenly(kv.Key, all);
            }
        }

        /// <summary>把附属/封印卡相对宿主（CardUI 或 CouncilCardUI）对齐定位（移植单机 ArrangeAllAttachments/Seals 算法）。
        /// seal=true 封印卡朝宿主上缘上延；false 附属朝宿主下缘下延。含横置旋转、Pivot、ZIndex 与平滑目标。</summary>
        private void LayoutAttachmentRelative(CardUI ui, Control hostUi, bool seal, int index)
        {
            // 附属/封印自身可能横置：同步旋转与 Pivot
            ui.PivotOffset = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT) / 2f;
            ui.RotationDegrees = ui.IsTapped ? 90f : 0f;

            // 宿主视觉中心与上/下缘（本地坐标，兼容宿主横置旋转）
            bool hostTapped = hostUi is CardUI hc ? hc.IsTapped : hostUi is CouncilCardUI cc ? cc.IsTapped : false;
            float angle = Mathf.DegToRad(hostTapped ? 90f : 0f);
            Vector2 rotatedPivot = hostUi.PivotOffset.Rotated(angle);
            float centerX = hostUi.Position.X + rotatedPivot.X;
            float centerY = hostUi.Position.Y + rotatedPivot.Y;
            float visHalfH = hostTapped ? CardLayoutHelper.CARD_WIDTH / 2f : CardLayoutHelper.CARD_HEIGHT / 2f;

            float topLeftX = centerX - CardLayoutHelper.CARD_WIDTH / 2f;
            float topLeftY;
            if (seal)
            {
                float topY = centerY - visHalfH;
                topLeftY = topY - CardLayoutHelper.CARD_HEIGHT + (index + 1) * (CardLayoutHelper.CARD_HEIGHT * 0.4f);
            }
            else
            {
                float bottomY = centerY + visHalfH;
                topLeftY = bottomY - CardLayoutHelper.CARD_HEIGHT + (index + 1) * (CardLayoutHelper.CARD_HEIGHT * 0.4f);
            }

            ui.Position = new Vector2(topLeftX, topLeftY);
            ui.TargetPosition = ui.GlobalPosition;
            // 宿主层最高，附属/封印依次递减（越后越远）
            ui.ZIndex = hostUi.ZIndex - (seal ? 1 : 0) - index;
        }

        /// <summary>重建分数区（ScoreZone）：渲染赢取地区/计分卡，并从快照刷新双方胜利分数。</summary>
        private void RebuildScoreZone(GameSnapshot snap)
        {
            var zone = _launcher?.ClientZoneManager;
            if (zone == null) return;

            for (int p = 1; p <= 2; p++)
            {
                var container = p == 1 ? _scoreContainer1 : _scoreContainer2;
                if (container == null) continue;
                foreach (Node child in container.GetChildren())
                    if (child is CardUI || child is AreaCardUI)
                    {
                        container.RemoveChild(child);
                        child.QueueFree();
                    }

                foreach (var inst in zone.GetZoneCards(CardZone.ScoreZone, p))
                {
                    if (inst == null) continue;
                    if (inst.PlayerTemplate != null)
                    {
                        var ui = _cardUIScene.Instantiate<CardUI>();
                        container.AddChild(ui);
                        ui.Setup(inst);
                        ui.SetFace(true);
                        ui.CanDrag = false;
                        _cardUIMap[inst.UID] = ui;
                    }
                    else if (inst.AreaTemplate != null && _areaCardUIScene != null)
                    {
                        var ui = _areaCardUIScene.Instantiate<AreaCardUI>();
                        container.AddChild(ui);
                        ui.Setup(inst.AreaTemplate);
                        ui.SetFace(true);
                        ui.MouseFilter = MouseFilterEnum.Ignore;
                    }
                }
                CardLayoutHelper.LayoutCardsEvenly(container, GatherCardUIs(container));
            }
        }

        /// <summary>收集容器内所有 CardUI（供均匀排版）。</summary>
        private System.Collections.Generic.List<Control> GatherCardUIs(Control container)
        {
            var list = new System.Collections.Generic.List<Control>();
            if (container == null) return list;
            foreach (Node child in container.GetChildren())
                if (child is CardUI c && GodotObject.IsInstanceValid(c)) list.Add(c);
            return list;
        }

        /// <summary>重建堆叠区（StackPanel/StackContainer）：条目以效果文字展示（客户端投影不重建卡片型 Kind）。</summary>
        private void RefreshStackUI()
        {
            if (_stackContainer == null) return;
            foreach (Node child in _stackContainer.GetChildren())
                if (child is StackEntryUI old)
                {
                    _stackContainer.RemoveChild(old);
                    old.QueueFree();
                }

            var stack = _launcher?.ClientStackManager;
            if (stack == null || _stackEntryScene == null) return;
            foreach (var entry in stack.Entries)
            {
                if (entry == null) continue;
                var ui = _stackEntryScene.Instantiate<StackEntryUI>();
                _stackContainer.AddChild(ui);
                ui.Setup(entry, null);
            }
        }

        /// <summary>刷新各地区势力标记（从客户端投影 AreaManager 读取）。</summary>
        private void RefreshAreaMarks()
        {
            var areaMgr = _launcher?.ClientAreaManager;
            if (areaMgr == null) return;
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                var m = areaMgr.GetMarks(slot.AreaUID);
                slot.UpdateMarks(m.p1, m.p2);
            }
        }

        // ==================== 剩余交互：投降 / 现身 / 激活（意图上行） ====================

        /// <summary>本地玩家投降。仅允许操作自己，越权忽略。</summary>
        public void DoConcede(int playerId)
        {
            if (_launcher == null || !IsLocalPlayer(playerId)) return;
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.Concede);
            GD.Print($"[ArenaOl] 上行 Concede: 玩家{_localPlayerId}");
        }

        // ==================== PM 调试命令（联机服务端权威执行） ====================
        /// <summary>Shift+Enter 执行输入框内的 PM 命令（逐行上行 DebugCommand 意图，服务端权威执行）。</summary>
        private void OnCommandInputGuiInput(InputEvent @event)
        {
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
            if (key.Keycode != Key.Enter && key.Keycode != Key.KpEnter) return;
            if (!key.ShiftPressed) return;   // 无 Shift 交给 TextEdit 换行
            if (_commandInput == null) return;
            string text = _commandInput.Text;
            _commandInput.Text = "";
            SendPMBatch(text);
            _commandInput?.AcceptEvent();
        }

        /// <summary>把多行 PM 命令逐行作为 DebugCommand 意图上行（空行/# 注释跳过）。</summary>
        private void SendPMBatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _launcher == null) return;
            foreach (var raw in text.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.DebugCommand, line);
                GD.Print($"[ArenaOl] 上行 DebugCommand: {line}");
            }
        }

        /// <summary>反引号 ` 切换 PM 命令输入框可见性（与单机 Arena 的按键一致，Key 96）。</summary>
        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == (Key)96)
            {
                if (_commandInput != null)
                {
                    bool show = !_commandInput.Visible;
                    _commandInput.Visible = show;
                    _commandInput.Text = "";
                    if (show) _commandInput.GrabFocus();
                    else _commandInput.ReleaseFocus();
                    AcceptEvent();
                }
            }
        }

        /// <summary>暗藏者双击现身：上行 RevealCovert 意图，服务端权威裁决。</summary>
        private void OnCovertRevealRequested(CardUI cardUI)
        {
            if (cardUI?.Instance == null || _launcher == null) return;
            if (cardUI.Instance.ControllerId != _localPlayerId) return;   // 输入隔离：只能现身自己的暗藏
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.RevealCovert, cardUI.Instance.UID);
            GD.Print($"[ArenaOl] 上行 RevealCovert: {cardUI.Instance.PlayerTemplate?.CardName}");
        }

        /// <summary>附属卡双击激活：上行 ActivateAbility（取模板首个激活能力，无目标），服务端权威裁决。</summary>
        private void OnAttachmentDoubleClicked(CardUI cardUI)
        {
            if (cardUI?.Instance == null || _launcher == null) return;
            if (cardUI.Instance.ControllerId != _localPlayerId) return;
            var inst = cardUI.Instance;
            string effectId = null;
            if (inst.PlayerTemplate is PlayerCardData tmpl)
            {
                foreach (var e in tmpl.EffectDataList)
                {
                    if (e.Timing is EffectTiming.AttachmentActivated or EffectTiming.CharacterActivated)
                    {
                        effectId = e.EffectId;
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(effectId))
            {
                GD.Print($"[ArenaOl] {inst.PlayerTemplate?.CardName} 无可激活能力，忽略双击");
                return;
            }
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, inst.UID, effectId, "");
            GD.Print($"[ArenaOl] 上行 ActivateAbility: {inst.PlayerTemplate?.CardName} effect={effectId}");
        }

        /// <summary>秘社双击激活：上行 ActivateAbility（取首个激活选项首个效果，无目标），服务端权威裁决。</summary>
        private void OnCouncilDoubleClicked(CouncilCardUI cardUI)
        {
            if (cardUI?.Instance == null || _launcher == null) return;
            if (cardUI.Instance.ControllerId != _localPlayerId) return;
            var inst = cardUI.Instance;
            string effectId = null;
            if (inst.CouncilTemplate != null && inst.CouncilTemplate.ActivationOptions.Count > 0)
            {
                foreach (var opt in inst.CouncilTemplate.ActivationOptions)
                {
                    if (opt?.EffectDataList == null || opt.EffectDataList.Count == 0) continue;
                    effectId = opt.EffectDataList[0].EffectId;
                    break;
                }
            }
            if (string.IsNullOrEmpty(effectId))
            {
                GD.Print($"[ArenaOl] 秘社 {inst.CouncilTemplate?.CardName} 无激活选项，忽略双击");
                return;
            }
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.ActivateAbility, inst.UID, effectId, "");
            GD.Print($"[ArenaOl] 上行秘社 ActivateAbility: {inst.CouncilTemplate?.CardName} effect={effectId}");
        }

        /// <summary>重建世界牌库（地区牌堆）：用 WorldDeckZone 组件显示卡背堆叠 + 数量。</summary>
        private void RebuildWorldDeck()
        {
            if (_worldDeckZone == null) return;
            var wm = _launcher?.ClientWorldDeckManager;
            if (wm == null) return;
            _worldDeckZone.Initialize(wm.Count, _areaCardUIScene);
        }

        /// <summary>重建场上地区：用 AreaSlot 槽位组件显示地区卡正面（与单机一致）。
        /// 地区数据在客户端投影 AreaManager（GetOrderedAreaInstances 按槽位排序），不在 ZoneManager 的 AreaZone 桶。</summary>
        private void RebuildAreas()
        {
            var areaMgr = _launcher?.ClientAreaManager;
            if (areaMgr == null) return;
            var areaInstances = areaMgr.GetOrderedAreaInstances();

            // 清空旧槽位
            foreach (var slot in _areaSlots)
                if (slot != null && GodotObject.IsInstanceValid(slot)) slot.QueueFree();
            _areaSlots.Clear();
            if (_areasContainer != null)
                foreach (Node child in _areasContainer.GetChildren())
                    child.QueueFree();

            if (areaInstances == null) return;

            for (int i = 0; i < areaInstances.Count; i++)
            {
                var inst = areaInstances[i];
                if (inst?.AreaTemplate == null) continue;
                var slot = _areaSlotScene.Instantiate<AreaSlot>();
                _areasContainer.AddChild(slot);
                slot.Name = $"AreaSlot_{i + 1}";
                // 联机投影下统计依赖为 null 时 AreaSlot 内部会自动跳过统计刷新，不影响卡面显示
                slot.Setup(inst, i + 1, false, _areaCardUIScene);
                _areaSlots.Add(slot);
            }
            ArrangeAreas();
        }

        /// <summary>地区槽位排版（复刻单机 Arena.ArrangeAreas：放得下居中，放不下重叠右压左）。</summary>
        private void ArrangeAreas()
        {
            if (_areaSlots.Count == 0 || _areasContainer == null) return;
            float containerWidth = _areasContainer.Size.X;
            if (containerWidth <= 0) containerWidth = 1920;

            int count = _areaSlots.Count;
            const float slotWidth = 400f;
            const float slotHeight = 580f;
            const float spacing = 30f;

            float startX, step;
            if (count == 1)
            {
                startX = System.Math.Max(0f, (containerWidth - slotWidth) / 2f);
                step = 0f;
            }
            else
            {
                float idealTotal = count * slotWidth + (count - 1) * spacing;
                if (idealTotal <= containerWidth)
                {
                    startX = (containerWidth - idealTotal) / 2f;
                    step = slotWidth + spacing;
                }
                else
                {
                    startX = 0f;
                    step = (containerWidth - slotWidth) / (count - 1f);
                    if (step < 0f) step = 0f;
                }
            }

            for (int i = 0; i < count; i++)
            {
                _areaSlots[i].Position = new Vector2(startX + i * step, 0);
                _areaSlots[i].Size = new Vector2(slotWidth, slotHeight);
                _areaSlots[i].ZIndex = i;
                _areaSlots[i].ZAsRelative = false;
            }
        }

        private void RebuildInto(Control container, System.Collections.Generic.IEnumerable<CardInstance> instances)
        {
            if (container == null || instances == null) return;
            // 重建前先立即清空容器旧 CardUI 子节点（移除并释放），
            // 避免 QueueFree 延迟释放导致旧卡（如建立资产后被移走的卡）仍残留在容器里、被 ArrangeAll 排布显示。
            foreach (Node child in container.GetChildren())
                if (child is CardUI oc && GodotObject.IsInstanceValid(oc))
                {
                    container.RemoveChild(oc);
                    oc.QueueFree();
                }

            bool isLocalHand = (container == _p1Hand && _localPlayerId == 1) || (container == _p2Hand && _localPlayerId == 2);
            if (isLocalHand) { _localHand = container; _localHandOwner = _localPlayerId; }
            foreach (var inst in instances)
            {
                if (inst == null || _cardUIMap.ContainsKey(inst.UID)) continue;
                // CardUI 只支持玩家卡（PlayerCardData）；地区卡/秘社卡 PlayerTemplate 为 null，跳过避免误报/背面
                if (inst.PlayerTemplate == null) continue;
                var ui = _cardUIScene.Instantiate<CardUI>();
                container.AddChild(ui);
                ui.Setup(inst);
                // 输入隔离：仅本地玩家的手牌可拖拽出牌/派遣/资产；对手手牌只读展示
                ui.CanDrag = isLocalHand;
                if (isLocalHand)
                    ui.CardDragEnded += OnLocalHandCardDragEnded;
                _cardUIMap[inst.UID] = ui;
            }
        }

        private void ClearAllUI()
        {
            // 清除手牌/墓地/地区动态节点（牌库/世界牌库由对应组件 Initialize 时自建/自清）
            foreach (var kv in _cardUIMap)
                if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
            _cardUIMap.Clear();
            foreach (var kv in _councilCardUIMap)
                if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
            _councilCardUIMap.Clear();
            foreach (var slot in _areaSlots)
                if (slot != null && GodotObject.IsInstanceValid(slot)) slot.QueueFree();
            _areaSlots.Clear();
        }

        private void ArrangeAll()
        {
            ArrangeChildren(_p1Hand);
            ArrangeChildren(_p2Hand);
            ArrangeAreas();
        }

        private void ArrangeChildren(Control container)
        {
            if (container == null) return;
            var cards = new List<Control>();
            foreach (var child in container.GetChildren())
                if (child is CardUI c && GodotObject.IsInstanceValid(c)) cards.Add(c);
            CardLayoutHelper.LayoutCardsEvenly(container, cards);
        }

        // ==================== 操作输入（意图上行，含输入隔离） ====================
        // 输入隔离：联机下每个客户端进程只代表一个玩家（_localPlayerId，由 AttachLauncher 传入）。
        //  1) 所有意图上行必须用本地玩家身份：DoPass/DoEndTurn 只允许 playerId == _localPlayerId。
        //  2) 权威隔离在服务端：IntentHub.HandleIntent 强校验 intent.PlayerId == 连接映射(reportedPlayerId)，
        //     本进程即使误传对方玩家号，服务端也会以"越权"拒绝（双重防线，客户端校验是防呆，服务端校验是权威）。
        /// <summary>是否为本地玩家（本进程可操作的对象）。</summary>
        public bool IsLocalPlayer(int playerId) => playerId == _localPlayerId;

        /// <summary>本地玩家让过。仅允许操作自己，越权忽略。</summary>
        public void DoPass(int playerId)
        {
            if (_launcher == null || !IsLocalPlayer(playerId)) return;   // 输入隔离：只操作本地玩家
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.Pass);
        }

        /// <summary>本地玩家结束回合/让过推进阶段。仅允许操作自己，越权忽略。</summary>
        public void DoEndTurn(int playerId)
        {
            if (_launcher == null || !IsLocalPlayer(playerId)) return;   // 输入隔离：只操作本地玩家
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.Pass);
        }

        // ==================== 联机拖拽落点判定 + 意图上行 ====================

        /// <summary>切换派遣/资产蒙版模式。再次点击同按钮退出蒙版。</summary>
        private void ToggleMask(MaskMode mode, Button btn)
        {
            _maskMode = (_maskMode == mode) ? MaskMode.None : mode;
            if (btn != null) btn.Modulate = (_maskMode == mode) ? new Color(1.2f, 1.2f, 0.4f) : Colors.White;
            GD.Print($"[ArenaOl] 蒙版模式 => {_maskMode}（{(_maskMode == MaskMode.DeployCovert ? "请拖手牌到己方争夺区" : _maskMode == MaskMode.EstablishAsset ? "请拖出手牌建立资产" : "无")}）");
        }

        /// <summary>本地玩家手牌拖拽结束：判定落点并上行对应意图（联机不本地移动，服务端裁决后快照回显）。
        /// 注：CardUI.EndDrag 发出的 CardDragEnded 第二个参数固定为 false（与单机 CardDragController 一致，
        /// 单机也不检查该参数），故这里不依据 dropped 过滤，改用卡当前位置判定落点。</summary>
        private void OnLocalHandCardDragEnded(CardUI cardUI, bool dropped)
        {
            if (cardUI?.Instance == null) return;
            if (_launcher == null || !IsLocalPlayer(_localHandOwner)) return;   // 输入隔离：只操作本地玩家
            var inst = cardUI.Instance;
            var cardUid = inst.UID;
            // 拖拽结束瞬间卡仍位于放手位置（CardLayoutHelper 平滑移动目标在下次快照重建后才会更新）
            Vector2 dropPos = cardUI.GetVisualGlobalRect().GetCenter();

            if (_maskMode == MaskMode.DeployCovert)
            {
                var areaUid = FindOwnAreaUidAt(dropPos);
                if (string.IsNullOrEmpty(areaUid))
                {
                    GD.Print("[ArenaOl] 派遣暗藏需拖到己方争夺区（服务端将拒绝非法落点）");
                    _maskMode = MaskMode.None;
                    return;
                }
                _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.DeployCovert, cardUid, areaUid);
                _maskMode = MaskMode.None;
                GD.Print($"[ArenaOl] 上行 DeployCovert: {inst.PlayerTemplate?.CardName} → 地区 {areaUid}");
                return;
            }

            if (_maskMode == MaskMode.EstablishAsset)
            {
                _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.EstablishAsset, cardUid);
                _maskMode = MaskMode.None;
                GD.Print($"[ArenaOl] 上行 EstablishAsset: {inst.PlayerTemplate?.CardName}");
                return;
            }

            // 普通出牌：判定落点目标
            string targetAreaUid = FindOwnAreaUidAt(dropPos);
            string targetCardUid = FindTargetCardUidAt(dropPos);
            _launcher.SendIntentAs(_localPlayerId, PlayerIntent.Kind.PlayCard, cardUid,
                targetAreaUid ?? "", targetCardUid ?? "");
            GD.Print($"[ArenaOl] 上行 PlayCard: {inst.PlayerTemplate?.CardName} targetArea={targetAreaUid ?? "无"} targetCard={targetCardUid ?? "无"}");
        }

        /// <summary>查找落点命中的"己方争夺区"对应地区 UID（本地玩家 1→Player1Area，2→Player2Area）。</summary>
        private string FindOwnAreaUidAt(Vector2 globalPos)
        {
            foreach (var slot in _areaSlots)
            {
                if (slot == null || !GodotObject.IsInstanceValid(slot)) continue;
                var ownArea = _localPlayerId == 1 ? slot.Player1Area : slot.Player2Area;
                if (ownArea != null && ownArea.GetGlobalRect().HasPoint(globalPos))
                    return slot.AreaUID;
            }
            return null;
        }

        /// <summary>查找落点命中的场上目标卡 UID（场上角色/附属/暗藏者等非手牌玩家卡）。</summary>
        private string FindTargetCardUidAt(Vector2 globalPos)
        {
            foreach (var kv in _cardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                if (ui.Instance == null) continue;
                // 目标只能是场上卡（非手牌/牌库/墓地）
                if (ui.Instance.CurrentZone != CardZone.AreaZone
                    && ui.Instance.CurrentZone != CardZone.AttachedZone)
                    continue;
                if (ui.GetVisualGlobalRect().HasPoint(globalPos))
                    return ui.Instance.UID;
            }
            // 秘社卡（CouncilCardUI）也是合法卡目标（如 JC012 结附本方秘社）。
            // 单机 CardDragController 有专设的秘社落点判定，联机快照驱动同样需要支持，
            // 否则附属拖到秘社时 targetCardUid 为空，服务端 Attachment 结算会落入"无合法目标→进墓地"。
            foreach (var kv in _councilCardUIMap)
            {
                var ui = kv.Value;
                if (ui == null || !GodotObject.IsInstanceValid(ui)) continue;
                if (ui.Instance == null) continue;
                if (ui.GetGlobalRect().HasPoint(globalPos))
                    return ui.Instance.UID;
            }
            return null;
        }
    }
}
