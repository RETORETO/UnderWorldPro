using System.Collections.Generic;
using System.Linq;
using Godot;
using Script.Enums;
using Script.Data.Effects;
using Script.Game.Entities;

namespace Script.UI
{
    public partial class CardUI : Control
    {
        // ---------- 导出的子节点引用 ----------
        [Export] private Label _costLabel;
        [Export] private Label _nameLabel;
        [Export] private TextureRect _cardImage;      // 保留但实际使用 _frontface
        [Export] private Label _investigationLabel;
        [Export] private Label _subCategoriesLabel;   // 子类型显示（如"子类型：鸟/魔宠"，含运行时授予）
        [Export] private Label _magicDomainsLabel;     // 魔法领域显示（如"领域：心灵"，JC060 光环运行时授予"心"）
        [Export] private Label _instanceIdLabel;       // 实例ID显示（inst_xx）
        [Export] private Label _categoryLabel;         // 类别显示（角色/事务/附属/任务/地区/指示物，复合类别用"/"连接）
        [Export] private Label _combatLabel;
        [Export] private Label _powerLabel;
        [Export] private Label _defenseLabel;
        [Export] private Panel _selectionHighlight;

        [Export] private TextureRect _frontface;      // 正面卡图
        [Export] private TextureRect _backface;       // 背面卡图
        [Export] private Label _damageAllocationLabel;   // 战斗对抗分配的伤害数字（默认隐藏）

        // ---------- 信号 ----------
        [Signal] public delegate void CardDragStartedEventHandler(CardUI cardUI);
        [Signal] public delegate void CardDragEndedEventHandler(CardUI cardUI, bool dropped);
        [Signal] public delegate void CardClickedEventHandler(CardUI cardUI);
        [Signal] public delegate void CovertRevealRequestedEventHandler(CardUI cardUI);
        [Signal] public delegate void TappedChangedEventHandler(CardUI cardUI, bool tapped);
        // 附属卡双击激活信号：与秘社双击一致，单击不再弹出激活窗口（JC012 交互修正）。
        [Signal] public delegate void AttachmentDoubleClickedEventHandler(CardUI cardUI);
        // 【2026-10 用户口径·JC085】墓地中的"墓地行动"卡（`PlayerCardData.PlayableFromGraveyard`）
        // 双击请求"从墓地正面打出"：由 Arena/ArenaOl 弹**选择地区**窗（墓地牌没有拖拽通道）。
        [Signal] public delegate void GraveyardPlayRequestedEventHandler(CardUI cardUI);

        // ---------- 内部状态 ----------
        private CardInstance _instance;
        public CardInstance Instance => _instance;
        public bool IsSelected { get; private set; }

        // ---------- 拖拽状态 ----------
        private bool _isDragging;
        private Vector2 _dragOffset;
        private Vector2 _initialGlobalPosition;   // 拖拽开始时的全局位置（用于 CancelDrag 归位）

        // 是否允许拖拽：仅手牌区卡为 true，卡组卡默认 false
        public bool CanDrag { get; set; } = false;

        // 是否可发动角色激活能力：场上己方卡在玩家拥有优先权、费用足够、有合法目标时为 true（D9 激活框架）
        public bool CanActivate { get; set; } = false;

        // ---------- 平滑移动 ----------
        private Vector2 _targetPosition;
        public Vector2 TargetPosition
        {
            get => _targetPosition;
            set => _targetPosition = value;
        }
        [Export] public float ReturnSpeed = 0.15f;

        // ---------- 卡背缓存（避免重复加载） ----------
        private static Texture2D _cachedBackTexture;

        // ============================================================
        // 生命周期
        // ============================================================
        public override void _Ready()
        {
            CheckExports();

            if (_selectionHighlight != null)
                _selectionHighlight.Modulate = new Color(1, 1, 1, 0);

            // 纯视觉子节点不接收鼠标输入，命中区收敛到根节点自身(卡面 100x140)
            // 避免 full-rect 的子节点(TextureRect/Panel)命中矩形异常扩大导致"点空隙也抓起牌"
            foreach (var child in GetChildren())
            {
                if (child is Control c)
                    c.MouseFilter = MouseFilterEnum.Ignore;
            }

            // 根节点自身必须显式设为 Stop，确保能接收 GuiInput（防止序列化/继承导致被忽略）
            MouseFilter = MouseFilterEnum.Stop;

            // 显式设定尺寸与最小尺寸，避免依赖布局推断导致 Size 为 0 进而命中测试失败（卡牌点不到）
            // 同时保持 CustomMinimumSize 与 Size 一致，使 HBoxContainer 等自动布局容器能正确排布
            // 注意：必须先设置 CustomMinimumSize，再设置 Size，否则 Size 会被旧的最小尺寸强制限制
            CustomMinimumSize = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            Size = new Vector2(CARD_WIDTH, CARD_HEIGHT);
            OffsetLeft = OffsetTop = OffsetRight = OffsetBottom = 0;
            AnchorLeft = AnchorTop = AnchorRight = AnchorBottom = 0;
            PivotOffset = new Vector2(CARD_WIDTH, CARD_HEIGHT) / 2f;

            SetFace(false);
            _targetPosition = Position;

            GuiInput += OnGuiInput;

            // 【鼠标悬浮预览】鼠标进入/离开卡面 → 经 `CardPreviewBus` 请求"整张卡图"浮层。
            // 是否允许显示由视图注入的判定决定（`CovertInspectRule`：对手手牌/对手暗藏者默认不弹，
            // JC057 未横置且同地区时授权弹出）；未接管（无战场视图）→ 静默忽略。
            // 【2026-10-16 计划·未实施】按 `UI美化与空间重排方案.md` 决策 5，手牌卡的 hover 将改为
            // **本体上浮 + 放大**，并**抑制整卡预览浮层**（避免"上浮 + 浮层"同时发生）；
            // 且角标/层叠列表浮层优先于整卡预览（同宿主多触发源互斥，见方案第五章）。改造前请先读该方案。
            MouseEntered += OnMouseEnteredPreview;
            MouseExited += OnMouseExitedPreview;
        }

        private void OnMouseEnteredPreview() => CardPreviewBus.RequestHover(this);

        private void OnMouseExitedPreview() => CardPreviewBus.EndHover(this);

        public override void _Process(double delta)
        {
            if (_freezeSmooth) return;   // 窗口内临时展示卡：禁用平滑归位，交由窗口直接控制位置
            if (_isDragging)
            {
                // 拖拽中：直接跟随鼠标（不平滑，保证跟手）
                GlobalPosition = GetGlobalMousePosition() - _dragOffset;
            }
            else
            {
                if (GlobalPosition.DistanceTo(_targetPosition) > 0.5f)
                    GlobalPosition = GlobalPosition.Lerp(_targetPosition, ReturnSpeed);
                else
                    GlobalPosition = _targetPosition;
            }
        }

        /// <summary>冻结平滑移动（调查窗口内临时卡使用，避免被 _Process 拉回）。</summary>
        public bool FreezeSmoothMove
        {
            get => _freezeSmooth;
            set => _freezeSmooth = value;
        }
        private bool _freezeSmooth = false;

        // 鼠标移动跟随已在 _Process 中处理（_isDragging 时直接跟随鼠标），此处不再处理移动。
        // 保留空实现以便后续需要时扩展；拖拽状态完全由 GuiInput 的按下/释放驱动。

        /// <summary>
        /// 【K27】该卡是否声明了"**手牌行动**"（`Scope = ZoneScope.HandZone` 的激活类效果）。
        /// 供双击分支判断"手牌中的卡是否可按激活处理"（普通手牌卡不受影响，仍走拖拽出牌）。
        /// 【2026-10·收口】判据统一到 `ActivateEligibility.HasHandScopedActivation`（原先 View/协调器
        /// 各内联一份，容易漂移），本方法只做转发。
        /// </summary>
        private static bool HasHandScopedActivation(CardInstance inst)
            => Script.Manager.ActivateEligibility.HasHandScopedActivation(inst);
        // GuiInput 处理按下/释放（Godot 命中分发正常时走这里；若命中失败则由 Arena 全局 _Input 驱动）
        private void OnGuiInput(InputEvent @event)
        {            // 【2026-09 诊断】"卡有高亮但双击完全没日志"（用户实测，单机）：需要区分
            // "事件压根没到卡 UI（被上层遮挡/吞掉）"与"到了但链路静默 return"。
            // 故对**可发动的场上卡**记一条左键日志（量极小：只在高亮激活卡上落笔）。
            if (@event is InputEventMouseButton diagMouse && diagMouse.ButtonIndex == MouseButton.Left
                && diagMouse.Pressed && CanActivate && _instance != null)
            {
                GD.Print($"[CardUI] 左键到达卡 UI：{_instance.PlayerTemplate?.CardName} 双击={diagMouse.DoubleClick} " +
                         $"区={_instance.CurrentZone} 附属={_instance.IsAttached} →{(diagMouse.DoubleClick ? "请求激活" : "（等待第二次点击）")}");
            }
            // 暗藏者现身：争夺区内背面朝上的卡，双击（任意拖拽态均可）即请求现身；
            // 先于 CanDrag 判断，因为暗藏者通常 CanDrag=false（背面不可拖拽出牌）。
            if (@event is InputEventMouseButton revealMouse
                && revealMouse.ButtonIndex == MouseButton.Left
                && revealMouse.Pressed
                && revealMouse.DoubleClick
                && _instance != null
                && _instance.IsCovert
                && _instance.CurrentZone == CardZone.AreaZone)
            {
                EmitSignal(SignalName.CovertRevealRequested, this);
                AcceptEvent();
                return;
            }

            // 【2026-10 用户口径·JC085】**墓地行动**：墓地中声明 `PlayableFromGraveyard` 的卡双击即请求
            // "从墓地正面打出"（先于 CanDrag 判断——墓地卡不可拖拽），由 Arena 弹**选择地区**窗。
            if (@event is InputEventMouseButton graveMouse
                && graveMouse.ButtonIndex == MouseButton.Left
                && graveMouse.Pressed
                && graveMouse.DoubleClick
                && _instance != null
                && _instance.CurrentZone == CardZone.Graveyard
                && _instance.PlayerTemplate?.PlayableFromGraveyard == true)
            {
                EmitSignal(SignalName.GraveyardPlayRequested, this);
                AcceptEvent();
                return;
            }

            // 附属卡双击激活：与秘社双击一致，单击不再弹窗（JC012 交互修正）。
            // 先于 CanDrag 判断，因为附属卡通常 CanDrag=false（不可拖拽出牌）。
            // 【K24】场上【正面角色】（争夺区、未暗藏）同样双击即请求激活：其激活能力可能"以玩家为目标"
            // （JC111 混沌蝴蝶），而玩家不是场上节点、无法用"拖到目标上"表达 → 必须由双击进选择窗口。
            // 争夺区卡 CanDrag=false（见 Arena.Layout），故不会与拖拽出牌冲突。
            // 【K27】**手牌**中的"手牌行动"卡（声明 `Scope=HandZone` 的激活能力，如 JC110）同样双击请求激活；
            // 该分支在 `CanDrag` 之前 `AcceptEvent`，因此不会触发拖拽出牌（其余手牌不受影响）。
            if (@event is InputEventMouseButton activateMouse
                && activateMouse.ButtonIndex == MouseButton.Left
                && activateMouse.Pressed
                && activateMouse.DoubleClick
                && _instance != null
                && (_instance.IsAttached
                    || (_instance.CurrentZone == CardZone.AreaZone && !_instance.IsCovert)
                    || (_instance.CurrentZone == CardZone.Hand && HasHandScopedActivation(_instance))))
            {
                EmitSignal(SignalName.AttachmentDoubleClicked, this);
                AcceptEvent();
                return;
            }

            if (!CanDrag) return;

            if (@event is InputEventMouseButton mouse)
            {
                if (mouse.ButtonIndex == MouseButton.Left)
                {
                    if (mouse.Pressed)
                    {
                        BeginDrag();
                        AcceptEvent();
                    }
                    else if (_isDragging)
                    {
                        EndDrag();
                        AcceptEvent();
                    }
                }
                else if (mouse.ButtonIndex == MouseButton.Right && mouse.Pressed)
                {
                    EmitSignal(SignalName.CardClicked, this);
                    AcceptEvent();
                }
            }
        }

        /// <summary>由 Arena 全局输入（坐标点选）或 GuiInput 调用，开始拖拽。</summary>
        public void BeginDrag()
        {
            if (!CanDrag || _isDragging) return;
            _isDragging = true;
            _initialGlobalPosition = GlobalPosition;
            _dragOffset = GetGlobalMousePosition() - GlobalPosition;
            GetParent()?.MoveChild(this, -1);
            ZIndex = Layers.CardDragging;   // 拖拽态层号（压住所有卡，仍低于附属/封印与浮层）
            EmitSignal(SignalName.CardDragStarted, this);
            GD.Print($"[CardUI] 拖拽开始 {_instance?.PlayerTemplate?.CardName}");
        }

        /// <summary>结束拖拽（鼠标释放时调用）。</summary>
        public void EndDrag()
        {
            if (!_isDragging) return;
            _isDragging = false;
            ZIndex = 0;
            EmitSignal(SignalName.CardDragEnded, this, false);
            GD.Print($"[CardUI] 拖拽结束 {_instance?.PlayerTemplate?.CardName}");
        }

        /// <summary>当前是否正在拖拽（供 Arena 查询）。</summary>
        public bool IsDragging => _isDragging;

        // ============================================================
        // 核心方法
        // ============================================================
        public void Setup(CardInstance instance)
        {
            if (instance == null)
            {
                _instance = null;
                ClearLabels();
                SetFace(false);
                return;
            }

            _instance = instance;
            var template = instance.PlayerTemplate;
            if (template == null)
            {
                GD.PrintErr("❌ PlayerTemplate 为 null");
                SetFace(false);
                return;
            }

            // 费用（2026-08-11）：若当前控制者享有玩家级费用修正（如 JC012 下一张帷幕守望牌-1），
            // 实时显示减后费用，并以绿色提示"可减费"。减费由 EffectEngine 费用修正栈驱动，TurnManager 读取。
            RefreshCostReduction();

            // 能力值（使用动态值）——与 `RefreshStats` 同一函数来源，保证"调/战/势"三项对称
            if (_investigationLabel != null)
                _investigationLabel.Text = $"调 {ComputeDisplayed(CombatManager.CombatType.Investigation)}";

            // 实例ID（inst_xx，用于调试/规则定位，唯一标识一张卡实例）
            if (_instanceIdLabel != null)
                _instanceIdLabel.Text = instance.UID;

            // 类别（取实例有效类别 EffectiveCategoryFlag，含形态覆盖；复合类别用"/"连接）
            if (_categoryLabel != null)
                _categoryLabel.Text = CategoryText(instance.EffectiveCategoryFlag);

            // 子类型（静态模板 + 运行时授予），如"子类型：鸟/魔宠"
            RefreshSubCategoriesLabel();
            RefreshMagicDomainsLabel();
            if (_combatLabel != null)
                _combatLabel.Text = $"战 {ComputeDisplayed(CombatManager.CombatType.Strength)}";
            if (_powerLabel != null) _powerLabel.Text = $"势 {ComputeDisplayed(CombatManager.CombatType.Power)}";
            if (_defenseLabel != null)
                _defenseLabel.Text = instance.HasDefense ? $"防 {instance.CurrentDefense}" : "无防";

            // ---------- 加载正面卡图 ----------
            if (_frontface != null)
            {
                string imagePath = ResolveCardImagePath(template.UID);
                var texture = ResourceLoader.Load<Texture2D>(imagePath);
                if (texture != null)
                {
                    _frontface.Texture = texture;
                    // 同步赋值给 CardImage 节点，确保任意显示节点都有卡图（避免正面不显示）
                    if (_cardImage != null) _cardImage.Texture = texture;
                }
                else
                {
                    GD.PrintErr($"[CardUI] 卡图加载失败: {imagePath}");
                    // 可选：设置占位颜色
                    _frontface.Modulate = new Color(0.8f, 0.8f, 0.8f);
                }
            }

            // ---------- 加载背面卡图（仅首次加载，之后缓存） ----------
            if (_backface != null)
            {
                if (_cachedBackTexture == null)
                {
                    string backPath = "res://Cards/Image/CardBack.jpg";
                    _cachedBackTexture = ResourceLoader.Load<Texture2D>(backPath);
                    if (_cachedBackTexture == null)
                        GD.PrintErr($"[CardUI] 卡背加载失败: {backPath}");
                }
                _backface.Texture = _cachedBackTexture;
            }

            SetFace(true);
        }

    public void RefreshStats()
    {
        if (!GodotObject.IsInstanceValid(this)) return;   // 节点已释放（QueueFree 后）则不刷新
        if (_instance == null || _instance.PlayerTemplate == null) return;
            // 【2026-09·JC051 用户口径】**三项数值完全对称**：调/战/势一律读**地区合计逐卡同一函数**
            // `AreaStatsCalculator.ValueOf(card, type, playerManager)`（其内部按 `IsFirstPlayer` 判先手）：
            //   先手回合 ＝ 基础值 ＋【先手值】＋ 临时授予（调查/战斗/势力）＋【先手值】临时先手授予（白眼/白刀/白星）
            //   后手回合 ＝ 基础值 ＋ 临时授予
            // 于是不再需要" (先手+N) / (调查+N)" 之类文本标签（数值里已含，标签会造成"数字＋标签"双计的错觉）。
            if (_investigationLabel != null) _investigationLabel.Text = $"调 {ComputeDisplayed(CombatManager.CombatType.Investigation)}";
            if (_combatLabel != null) _combatLabel.Text = $"战 {ComputeDisplayed(CombatManager.CombatType.Strength)}";
            // 子类型随持续效果授予/撤销刷新（如 JC030 在场获得"吸血鬼"）
            RefreshSubCategoriesLabel();
            // 魔法领域随光环授予/撤销刷新（如 JC060 给无领域角色加"心"=心灵领域）
            RefreshMagicDomainsLabel();
            if (_powerLabel != null) _powerLabel.Text = $"势 {ComputeDisplayed(CombatManager.CombatType.Power)}";
            if (_defenseLabel != null && _instance.HasDefense) _defenseLabel.Text = $"防 {_instance.CurrentDefense}";
        }

        /// <summary>外部注入的玩家级费用修正（联机客户端由 ArenaOl 从快照计算后注入）。
        /// 默认 null = 未接管 → 仍走下方单机 EffectEngine.Instance 逻辑，单机行为完全不变。</summary>
        public void SetExternalCostMod(int? mod)
        {
            _externalCostMod = mod;
            RefreshCostReduction();
        }

        // ============================================================
        // 【2026-09·JC051 用户口径】卡面势力显示所需依赖与取值（**复用既有实现，不新增机制**）
        // ------------------------------------------------------------
        // 取值单一来源＝`AreaStatsCalculator.ValueOf(card, Power, playerManager)`——与**地区合计逐卡**同一个函数、
        // 同一先手判定（`playerManager.IsFirstPlayer(card.ControllerId)`），故卡面数值 ≡ 该卡在地区合计中的贡献：
        //   先手回合：能力 ＋ 先手能力 ＋ 临时能力（“势力”）＋ 临时先手能力（“先手势力”）
        //   后手回合：能力 ＋ 临时能力
        // 注入范式照抄 `AreaSlot.SetStatsDependencies(ZoneManager, PlayerManager)`（本处只需 PlayerManager）。
        // 未注入（测试/未就绪）时 `ValueOf` 内部 isFirst=false，自然退化为“能力＋临时能力”，不崩不叠加。
        // ============================================================

        /// <summary>注入统计依赖（单机取 `Arena._playerManager`；联机取 `_matchClient.ClientPlayerManager`）。</summary>
        public void SetStatsDependencies(PlayerManager playerManager) => _playerManager = playerManager;

        private PlayerManager _playerManager;

        /// <summary>本卡当前应显示的**调查/战斗/势力**（三者对称）＝地区合计逐卡用的同一函数
        /// `AreaStatsCalculator.ValueOf(card, type, playerManager)`（内部按 `IsFirstPlayer` 判先手）。
        /// 未注入 `PlayerManager`（测试/未就绪）时其内部 isFirst=false，自然退化为"基础值＋临时授予"。</summary>
        private int ComputeDisplayed(CombatManager.CombatType type)
            => _instance == null
                ? 0
                : Script.Manager.AreaStatsCalculator.ValueOf(_instance, type, _playerManager);
        private int? _externalCostMod;

        /// <summary>刷新费用显示：若有玩家级费用修正（JC012 等"下一张帷幕守望牌-1"），实时显示减后费用并绿色提示；否则显示原始费用。</summary>
        public void RefreshCostReduction()
        {
            if (_costLabel == null || _instance == null || _instance.PlayerTemplate == null) return;
            var tmpl = _instance.PlayerTemplate;
            int mod = 0;
            if (_externalCostMod.HasValue)
            {
                // 联机客户端：ArenaOl 由快照投影算出减费额并注入（客户端无 EffectEngine 实例）。
                mod = _externalCostMod.Value;
            }
            else
            {
                // 【2026-10-14·Web 崩溃修复】单机/未接管：读 `EffectEngine.Instance` 的玩家级费用修正。
                //
                // 旧写法是「直接解引用 + 靠 catch 吞异常」：
                //     try { mod = Script.Manager.EffectEngine.Instance.GetCostModification(...); }
                //     catch (System.Exception) { mod = 0; }
                // 桌面端没问题——**联机客户端根本没有 EffectEngine 实例**（`Instance == null`，见
                // `EffectEngine` 由单机 Composition Root 创建），于是这里抛 NullReferenceException 被 catch 吞掉。
                // 但 **Web 运行时上任何托管异常都会 abort 整个 wasm 进程**
                //（浏览器控制台 `Aborted()` + `RuntimeError: unreachable`，wasm 栈固定；try/catch 拦不住，
                //  日志也拿不到异常文本）——表现为"联机进战场后一张牌都不显示"：
                // CardUI.Setup → RefreshCostReduction 崩在**第一张真卡**上，整页重建中断。
                //（已实测定位：CUI 打点显示崩点恰在 `EffectEngine.Instance` 解引用那一行。）
                //
                // 结论：**不能靠异常兜底**，必须显式判空。判空后行为与桌面端完全一致（mod=0）。
                var ee = Script.Manager.EffectEngine.Instance;
                if (ee != null)
                {
                    try { mod = ee.GetCostModification(_instance.ControllerId, tmpl.CardName, tmpl.Organization); }
                    catch (System.Exception) { mod = 0; }
                }
            }
            if (tmpl.Cost >= 0)
            {
                int shown = Mathf.Max(0, tmpl.Cost - mod);
                _costLabel.Text = shown.ToString();
                _costLabel.Modulate = mod > 0 ? new Color(0.4f, 1f, 0.5f) : Colors.White;   // 减费绿、原费白
            }
            else
            {
                _costLabel.Text = "—";
                _costLabel.Modulate = Colors.White;
            }
        }

        /// <summary>当前是否为正面朝上（持久状态，供 PM 命令 flip 判断）</summary>
        public bool IsFaceUp { get; private set; } = false;

        public void SetFace(bool isFaceUp)
        {
            IsFaceUp = isFaceUp;
            if (_frontface != null) _frontface.Visible = isFaceUp;
            if (_backface != null) _backface.Visible = !isFaceUp;

            // 控制文字标签可见性（卡名强制隐藏）
            if (_costLabel != null) _costLabel.Visible = isFaceUp;
            if (_investigationLabel != null) _investigationLabel.Visible = isFaceUp;
            if (_subCategoriesLabel != null) _subCategoriesLabel.Visible = isFaceUp;
            if (_magicDomainsLabel != null) _magicDomainsLabel.Visible = isFaceUp;
            if (_instanceIdLabel != null) _instanceIdLabel.Visible = isFaceUp;
            if (_categoryLabel != null) _categoryLabel.Visible = isFaceUp;
            if (_combatLabel != null) _combatLabel.Visible = isFaceUp;
            if (_powerLabel != null) _powerLabel.Visible = isFaceUp;
            if (_defenseLabel != null) _defenseLabel.Visible = isFaceUp;
            if (_cardImage != null) _cardImage.Visible = isFaceUp;
        }

        public void CancelDrag()
        {
            if (_isDragging)
            {
                _isDragging = false;
                ZIndex = 0;
                TargetPosition = _initialGlobalPosition;   // 归位到拖拽开始时的全局位置
                GD.Print($"[CardUI] 拖拽取消");
            }
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (_selectionHighlight != null)
                _selectionHighlight.Modulate = new Color(1, 1, 1, selected ? 0.4f : 0f);
        }

        /// <summary>横置表现：绕中心旋转 90°，逻辑尺寸保持 100x140 不变。
        /// 这样 Frontface 等子节点会随父节点一起旋转，视觉上从 100x140 变为 140x100。
        /// 同时保持视觉中心不变，使 GetGlobalRect() 直接等于真实视觉包围盒，
        /// 从而彻底解决命中范围、高亮边框与 UI 表现不一致的问题。</summary>
        public bool IsTapped { get; private set; } = false;

        /// <summary>
        /// 设置横置标记。横置的【视觉旋转与定位】由 Arena.ArrangeAllCards → PlaceCardInContainer 统一负责
        /// （它会按 card.IsTapped 设置 RotationDegrees 并按旋转后的视觉宽度排布），此处只维护 IsTapped 标记，
        /// 避免与布局逻辑"双重控制"同一张卡的位置导致横置卡飞出可见区。
        /// </summary>
        public void SetTapped(bool tapped)
        {
            if (IsTapped == tapped) return;   // 状态未变无需处理，避免重复偏移
            IsTapped = tapped;

            QueueRedraw();   // 横置状态改变后重绘，确保伤害高亮边框跟随

            EmitSignal(SignalName.TappedChanged, this, tapped);

            GD.Print($"[CardUI] {_instance?.PlayerTemplate?.CardName} 横置表现 = {tapped}");
        }

        /// <summary>拖拽打出时，对合法目标卡做半透明高亮（拖拽结束/离开时清除）。</summary>
        private bool _targetHighlight = false;
        public void SetTargetHighlight(bool on)
        {
            if (_targetHighlight == on) return;
            _targetHighlight = on;
            Modulate = on ? new Color(1f, 1f, 0.6f, 0.85f) : Colors.White;
        }

        // 卡牌基础尺寸（与 Arena 的 CARD_WIDTH/CARD_HEIGHT 对应）
        private const float CARD_WIDTH = 100f;
        private const float CARD_HEIGHT = 140f;

        // ============================================================
        // 蒙版（可拖拽态提示）：ColorRect 作为 CardUI 子节点，默认隐藏
        //  - 派遣暗藏蒙版：角色卡显示灰色蒙版
        //  - 建立资产蒙版：所有手牌显示绿色蒙版
        // ============================================================
        private ColorRect _maskRect;

        /// <summary>设置蒙版。maskColor 传 null 表示清除蒙版（隐藏 ColorRect）。</summary>
        public void SetMask(Color? maskColor)
        {
            if (_maskRect == null)
                _maskRect = GetNodeOrNull<ColorRect>("ColorRect");
            if (_maskRect == null) return;

            if (maskColor.HasValue)
            {
                _maskRect.Color = maskColor.Value;
                _maskRect.Visible = true;
            }
            else
            {
                _maskRect.Visible = false;
            }
        }

        /// <summary>当前蒙版是否可见（供 Arena 判断是否需清除）。</summary>
        public bool MaskVisible
        {
            get
            {
                if (_maskRect == null)
                    _maskRect = GetNodeOrNull<ColorRect>("ColorRect");
                return _maskRect != null && _maskRect.Visible;
            }
        }

        /// <summary>获取真实视觉包围盒（旋转后的 AABB）。
        /// Godot 的 GetGlobalRect() 返回的是 Rect2(transform.origin, scale*size)，
        /// 不会随旋转而真正包围视觉区域；因此这里手动计算四个顶点的全局 AABB。</summary>
        public Rect2 GetVisualGlobalRect()
        {
            Transform2D gt = GetGlobalTransform();
            Vector2 p1 = gt * new Vector2(0, 0);
            Vector2 p2 = gt * new Vector2(Size.X, 0);
            Vector2 p3 = gt * new Vector2(0, Size.Y);
            Vector2 p4 = gt * new Vector2(Size.X, Size.Y);
            float minX = Mathf.Min(Mathf.Min(p1.X, p2.X), Mathf.Min(p3.X, p4.X));
            float minY = Mathf.Min(Mathf.Min(p1.Y, p2.Y), Mathf.Min(p3.Y, p4.Y));
            float maxX = Mathf.Max(Mathf.Max(p1.X, p2.X), Mathf.Max(p3.X, p4.X));
            float maxY = Mathf.Max(Mathf.Max(p1.Y, p2.Y), Mathf.Max(p3.Y, p4.Y));
            return new Rect2(new Vector2(minX, minY), new Vector2(maxX - minX, maxY - minY));
        }

        /// <summary>命中检测基于本地未旋转的 Size 矩形。
        /// _HasPoint 传入的 point 是本地坐标，因此用本地 Rect 判断即可；
        /// 父节点/全局输入请使用 GetVisualGlobalRect()。</summary>
        public override bool _HasPoint(Vector2 point)
        {
            return new Rect2(Vector2.Zero, Size).HasPoint(point);
        }

        public override void _Draw()
        {
            base._Draw();

            // 伤害高亮边框：在本地坐标系中绘制，会随节点旋转而旋转，
            // 因此视觉上始终与卡牌真实区域重合。
            if (_showDamageHighlight)
            {
                DrawRect(new Rect2(Vector2.Zero, Size), new Color(1, 0.2f, 0.2f), false, 4f);
            }

            // 可发动激活高亮边框（D9）：绿色常亮边框，提示玩家此卡可拖拽发动能力。
            if (_showActivateHighlight)
            {
                DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.2f, 1f, 0.3f), false, 5f);
            }

            // 选目标高光：黄色常亮边框，提示玩家此卡是当前触发/行动效果的"可选目标"。
            // 与激活绿框、伤害红框区分；用独立边框标志绘制，不干扰 Modulate 蒙版与拖拽半透明高亮。
            if (_showSelectableHighlight)
            {
                // 外扩 2px 避免与激活/伤害边框重叠时相互遮挡
                DrawRect(new Rect2(new Vector2(-2, -2), Size + new Vector2(4, 4)), new Color(1f, 0.85f, 0.1f), false, 6f);
            }
        }

        /// <summary>选目标（触发/行动效果）可选目标黄色高光边框。由协调器在弹出选目标窗口时按合法集切换，选完后清除。</summary>
        public void SetSelectableHighlight(bool on)
        {
            if (_showSelectableHighlight == on) return;
            _showSelectableHighlight = on;
            QueueRedraw();
        }
        private bool _showSelectableHighlight = false;

        /// <summary>可发动激活高亮边框（D9）。由 Arena 在优先权变化时根据可发动性切换。</summary>
        public void SetActivatableHighlight(bool on)
        {
            CanActivate = on;
            if (_showActivateHighlight == on) return;
            _showActivateHighlight = on;
            QueueRedraw();
        }
        private bool _showActivateHighlight = false;

        // ---------- 战斗对抗：伤害分配显示 ----------
        private bool _showDamageHighlight;


        /// <summary>显示分配伤害数字（战斗对抗窗口调用）。amount<=0 时隐藏。文本为"分配伤害X点"。</summary>
        public void SetDamageAllocation(int amount)
        {
            if (_damageAllocationLabel == null) return;
            if (amount > 0)
            {
                _damageAllocationLabel.Text = $"分配伤害{amount}点";
                _damageAllocationLabel.Visible = true;
            }
            else
            {
                _damageAllocationLabel.Text = "";
                _damageAllocationLabel.Visible = false;
            }
            SetDamageHighlight(amount > 0);
        }

        /// <summary>已分配伤害的红色高亮边框（由 _Draw 绘制，确保与旋转后的视觉区域严格重合）。</summary>
        public void SetDamageHighlight(bool on)
        {
            _showDamageHighlight = on;
            QueueRedraw();
        }

        /// <summary>清除战斗对抗的伤害显示（数字 + 高亮边框），窗口关闭/清空时调用。</summary>
        public void ClearDamageDisplay()
        {
            SetDamageAllocation(0);
            _showDamageHighlight = false;
            QueueRedraw();
        }

        // ============================================================
        // 辅助
        // ============================================================
        /// <summary>刷新子类型标签，格式如"子类型：鸟/魔宠"。
        /// 静态模板子类型按原顺序在前，运行时授予的子类型（如 JC030 的"吸血鬼"）追加在末尾去重显示。
        /// 【P3·K4】被"改写掉"的子类别（JC036 初拥把人类改成吸血鬼）**不显示**——与 `HasSubCategory` 同解。</summary>
        private void RefreshSubCategoriesLabel()
        {
            if (_subCategoriesLabel == null || _instance == null) return;
            var template = _instance.PlayerTemplate;
            if (template == null) { _subCategoriesLabel.Text = ""; return; }
            // 静态顺序优先，运行时授予追加在末尾（去重），保证展示稳定且与模板顺序一致
            var ordered = new List<string>();
            foreach (var s in template.SubCategories)
                if (_instance.HasSubCategory(s)) ordered.Add(s);       // 跳过被"改写掉"的模板固有类别
            foreach (var s in _instance.CurrentSubCategories)
                if (!ordered.Contains(s)) ordered.Add(s);
            _subCategoriesLabel.Text = ordered.Count > 0 ? $"子类型：{string.Join("/", ordered)}" : "";
        }

        /// <summary>刷新魔法领域标签，格式如"领域：心灵"。
        /// 显示实例当前 MagicDomains（模板固有 + JC060 光环运行时授予，如"心"=心灵领域）。
        /// 复用 Arena.Layout.DomainCn 的中文映射保持一致。</summary>
        private void RefreshMagicDomainsLabel()
        {
            if (_magicDomainsLabel == null || _instance == null) return;
            var domains = _instance.MagicDomains;
            if (domains == null || domains.Count == 0) { _magicDomainsLabel.Text = ""; return; }
            var names = domains.Select(d => Arena.DomainCn.TryGetValue(d, out var cn) ? cn : d.ToString());
            _magicDomainsLabel.Text = $"领域：{string.Join("/", names)}";
        }

        /// <summary>类别枚举（单一身份位）→ 中文名（与 DeckBuilder.CategoryCn 同解，保持全项目口径一致）。</summary>
        private static readonly Dictionary<CardCategory, string> CategoryCn = new Dictionary<CardCategory, string>
        {
            { CardCategory.Character, "角色" }, { CardCategory.Sorcery, "事务" }, { CardCategory.Attachment, "附属" },
            { CardCategory.Quest, "任务" }, { CardCategory.Land, "地区" }, { CardCategory.Token, "指示物" },
        };
        /// <summary>类别位掩码 → 中文串（按枚举定义顺序展开；只取"单一位"基础类别，复合组合用"/"连接）。</summary>
        private static string CategoryText(CardCategory flag)
        {
            if (flag == CardCategory.None) return "无";
            var parts = new List<string>();
            foreach (CardCategory v in System.Enum.GetValues(typeof(CardCategory)))
            {
                if (v == CardCategory.None) continue;
                if ((((int)v) & (((int)v) - 1)) != 0) continue;   // 单一位才算基础类别，复合组合跳过
                if ((flag & v) == 0) continue;
                parts.Add(CategoryCn.TryGetValue(v, out var cn) ? cn : v.ToString());
            }
            return parts.Count > 0 ? string.Join("/", parts) : flag.ToString();
        }

        private void ClearLabels()
        {
            if (_nameLabel != null) _nameLabel.Text = "";
            if (_costLabel != null) _costLabel.Text = "";
            if (_investigationLabel != null) _investigationLabel.Text = "";
            if (_subCategoriesLabel != null) _subCategoriesLabel.Text = "";
            if (_magicDomainsLabel != null) _magicDomainsLabel.Text = "";
            if (_instanceIdLabel != null) _instanceIdLabel.Text = "";
            if (_categoryLabel != null) _categoryLabel.Text = "";
            if (_combatLabel != null) _combatLabel.Text = "";
            if (_powerLabel != null) _powerLabel.Text = "";
            if (_defenseLabel != null) _defenseLabel.Text = "";
        }

        private void CheckExports()
        {
            if (_costLabel == null) GD.PrintErr("[CardUI] ❌ _costLabel 未绑定");
            if (_cardImage == null) GD.PrintErr("[CardUI] ❌ _cardImage 未绑定");
            if (_investigationLabel == null) GD.PrintErr("[CardUI] ❌ _investigationLabel 未绑定");
            if (_subCategoriesLabel == null) GD.PrintErr("[CardUI] ❌ _subCategoriesLabel 未绑定");
            if (_magicDomainsLabel == null) GD.PrintErr("[CardUI] ❌ _magicDomainsLabel 未绑定");
            if (_instanceIdLabel == null) GD.PrintErr("[CardUI] ❌ _instanceIdLabel 未绑定");
            if (_categoryLabel == null) GD.PrintErr("[CardUI] ❌ _categoryLabel 未绑定");
            if (_combatLabel == null) GD.PrintErr("[CardUI] ❌ _combatLabel 未绑定");
            if (_powerLabel == null) GD.PrintErr("[CardUI] ❌ _powerLabel 未绑定");
            if (_defenseLabel == null) GD.PrintErr("[CardUI] ❌ _defenseLabel 未绑定");
            if (_selectionHighlight == null) GD.PrintErr("[CardUI] ❌ _selectionHighlight 未绑定");
            if (_frontface == null) GD.PrintErr("[CardUI] ❌ _frontface 未绑定");
            if (_backface == null) GD.PrintErr("[CardUI] ❌ _backface 未绑定");
        }

        public void RefreshDamageDisplay()
        {
            if (_instance == null) return;
            // 例如：在卡牌上显示伤害数字（需在 CardUI 中添加伤害标签）
            // _damageLabel.Text = _instance.Damage > 0 ? $"⚔️{_instance.Damage}" : "";
        }

        /// <summary>
        /// 根据 UID 推断卡图目录。项目约定：
        /// - 玩家卡图片位于 res://Cards/Image/PlayerCards/{字母前缀}/
        /// - UID 格式为 "字母前缀+数字_卡名"，如 XG01_yinyaotanqiuzhe、JC005_liejieshu
        ///   其中目录名只取开头连续字母部分（XG / JC），不包含数字。
        /// </summary>
        public static string ResolveCardImagePath(string uid)
        {
            if (string.IsNullOrEmpty(uid))
                return "res://Cards/Image/PlayerCards/JC/unknown.jpg";

            // 取 UID 开头的连续字母作为目录名
            // 【2026-10-12 Web 修复】原来用 System.Char.IsLetter——它属于 Unicode 类别判定，
            // 在魔改版 Web 运行时上会走全球化路径（同类事故：String.StartsWith(string) 直接让 wasm abort）。
            // 卡号前缀只可能是 A-Z/a-z，改用共享的 ASCII 序数判断：行为对全部真实卡号完全一致，
            // 且不触碰任何全球化代码。桌面端结果也完全相同（工程内 UID 无带音标/非拉丁字母者）。
            string folder = Script.IO.WebSafeText.LeadingAsciiLetters(uid, "JC");

            // 首选：{folder}/{uid}.jpg（与卡号同名）
            string exact = $"res://Cards/Image/PlayerCards/{folder}/{uid}.jpg";
            if (ResourceLoader.Exists(exact))
                return exact;

            // 兜底：实际文件名为 "{uid}_中文名.jpg"，用 DirAccess 模糊匹配
            string dir = $"res://Cards/Image/PlayerCards/{folder}";
            using (var da = DirAccess.Open(dir))
            {
                if (da != null)
                {
                    da.ListDirBegin();
                    string f = da.GetNext();
                    while (!string.IsNullOrEmpty(f))
                    {
                        // 导出后目录列表里的图片名是 "xxx.jpg.import"（编辑器下是 "xxx.jpg"），先归一化再匹配，
                        // 否则这条兜底在导出包里永远命不中（症状：目录名≠UID 的卡在编辑器有图、打包后没图）。
                        if (!da.CurrentIsDir())
                        {
                            string fileName = Script.IO.ResourceDirScan.NormalizePackedName(f);
                            if (fileName.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
                                && fileName.StartsWith(uid, System.StringComparison.OrdinalIgnoreCase))
                            {
                                da.ListDirEnd();
                                return $"{dir}/{fileName}";
                            }
                        }
                        f = da.GetNext();
                    }
                    da.ListDirEnd();
                }
            }
            // 都找不到，返回首选路径（交给上层打印加载失败日志）
            return exact;
        }
    }
}
