using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;
using Script.Data.Cards;

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
        private List<AreaSlot> _areaSlots = new List<AreaSlot>();
        private readonly Dictionary<string, CardUI> _cardUIMap = new Dictionary<string, CardUI>();
        private Control _assetContainer1, _assetContainer2;   // 资产区（P1/P2）

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

            if (_areaCardUIScene == null)
                _areaCardUIScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaCardUI.tscn");
            if (_areaSlotScene == null)
                _areaSlotScene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/AreaSlot.tscn");

            var passBtn = GetNodeOrNull<Button>("ActionPanel/PassButton");
            if (passBtn != null) passBtn.Pressed += () => DoPass(_localPlayerId);
            var endTurnBtn = GetNodeOrNull<Button>("ActionPanel/EndTurnButton");
            if (endTurnBtn != null) endTurnBtn.Pressed += () => DoEndTurn(_localPlayerId);

            // 基础行动：派遣暗藏 / 建立资产（联机→上行意图，服务端裁决）
            var deployBtn = GetNodeOrNull<Button>("Player1Area/DeployCovertButton");
            if (deployBtn != null) deployBtn.Pressed += () => ToggleMask(MaskMode.DeployCovert, deployBtn);
            var assetBtn = GetNodeOrNull<Button>("Player1Area/EstablishAssetButton");
            if (assetBtn != null) assetBtn.Pressed += () => ToggleMask(MaskMode.EstablishAsset, assetBtn);
        }

        public void AttachLauncher(NetworkMatchLauncher launcher, int localPlayerId = 1)
        {
            _launcher = launcher;
            _localPlayerId = localPlayerId;
            if (_launcher != null)
                _launcher.OnClientSnapshot += ApplySnapshot;
        }

        public void DetachLauncher()
        {
            if (_launcher != null)
                _launcher.OnClientSnapshot -= ApplySnapshot;
            _launcher = null;
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

            RebuildWorldDeck();

            var turn = _launcher.ClientTurnManager;
            if (_phaseLabel != null && turn != null)
                _phaseLabel.Text = $"回合{turn.TurnCount} 阶段={turn.CurrentPhase} 优先权={turn.PriorityPlayerId}";

            if (_p1ScoreLabel != null && snap.Players.Count > 0) _p1ScoreLabel.Text = $"P1 {snap.Players[0].AvailableMana}";
            if (_p2ScoreLabel != null && snap.Players.Count > 1) _p2ScoreLabel.Text = $"P2 {snap.Players[1].AvailableMana}";

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
            return null;
        }
    }
}
