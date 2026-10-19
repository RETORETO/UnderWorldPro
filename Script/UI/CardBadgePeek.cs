using Godot;
using Script.Game.Entities;
using System;
using System.Collections.Generic;

namespace Script.UI
{
    /// <summary>
    /// 【P3b·2026-10-18】角标悬浮展开面板：在宿主卡旁边横向列出被折叠的成员卡面。
    ///
    /// 【2026-10-18 用户口径】① 封印卡与附属卡来自两个不同区域 ⇒ 面板**一次只列一种**
    /// （标题写明"封印卡"或"附属卡"，由调用方按 `CardBadgeKind` 过滤后传入）；
    /// ② 面板里的卡**双击＝发动激活能力**（如 JC020 引匕的飞刀：横置自身横置目标暗藏者）——
    /// 这些卡被折叠成角标后，场上已经没有可双击的卡面了，双击入口必须在这里补回来。
    ///
    /// 定位＝`Layers.CardHoverPreview`（830），与整卡预览同层但**互斥**（视图在展开前先关掉整卡预览，
    /// 保证同一时刻只有一个浮层）。位置优先放在宿主**上方**（不遮住宿主与争夺区），空间不足则放下方，
    /// 最后统一钳进视口。
    ///
    /// 出口：
    ///  · 单击成员 → `onPick(uid)`：调用方只把它转成 `CardPickWindow.SelectByUid`
    ///    （**只置选中、不提交**，仍需用户按「确认」）；没有选择窗时纯浏览。**单击绝不关闭面板**。
    ///  · 双击成员 → `onActivate(cardUI)`：走既有双击激活链路（`Arena.OpenAttachmentActivateChoice`
    ///    / `ArenaOl.OnAttachmentDoubleClicked`），是否真的可发动、要不要取目标、横置与付费
    ///    全部由 Controller/服务端把关，本面板不写 Model。
    ///
    /// 【2026-10-19 修复·"hover 展开后点附属卡，窗口直接关、效果发不动"】根因是三处叠加：
    ///  ① 面板里的卡是**真 `CardUI`**，`MouseEntered` 会向 `CardPreviewBus` 申请整卡预览 ⇒ 预览浮层
    ///     与本面板同层(830)但后创建、直接盖住面板；鼠标落点随之变成"在预览上"，面板 0.25s 后被判
    ///     "鼠标离开"自动收起；
    ///  ② 联机端把**单击**出口写成了"收起面板"（`_ => HideNow()`），而双击的第一次按下就是单击 ⇒
    ///     第二次按下时面板已不在，`DoubleClick` 永远等不到；
    ///  ③ 两个视图的 `_Input`（先于 Control `_GuiInput`）会在命中场上"可发动卡/手牌"时 `AcceptEvent()`
    ///     把左键吞掉 ⇒ 面板里的卡收不到任何鼠标事件。
    /// 修法：**命中槽自己收鼠标**（内层 CardUI 只作画、`MouseFilter=Ignore`）＋ 自持双击时间窗 ＋
    /// 视图 `_Input` 对"面板内落点"放行。另外面板必须**照实表现横置**（横置卡旋转 90°）。
    /// </summary>
    public partial class CardBadgePeek : Control
    {
        private const float CardScale = 0.55f;      // 展开态每张卡的缩放（约 77×108）
        /// <summary>
        /// 【2026-10-19 用户口径】面板**一次最多显示 11.5 张**（第 12 张露半张＝"右边还有卡"的提示），
        /// 超出的部分靠**卡片外侧底部的横向滚动条**左右拖动查看——不再硬截断成"…（共 N 张）"。
        /// </summary>
        private const float VisibleCards = 11.5f;
        /// <summary>给横向滚动条预留的高度（滚动条在卡片下方、面板内部底端）。</summary>
        private const float ScrollBarH = 14f;
        private const float Gap = 6f;
        private const float Pad = 6f;

        /// <summary>双击时间窗（毫秒）：与 `Arena.TryHandleHandActivationDoubleClick` 同口径（Godot 默认 400ms）。</summary>
        private const ulong DoubleClickMs = 420UL;

        private PanelContainer _panel;
        private VBoxContainer _column;
        private Label _title;
        private ScrollContainer _scroll;
        private HBoxContainer _row;
        private Action<string> _onPick;
        private Action<CardUI> _onActivate;

        /// <summary>
        /// 【2026-10-19 用户口径】"效果可激活时高亮要正常显示"——**探针由视图注入**，面板只负责画。
        ///
        /// 为什么必须是探针：成员卡被折叠成角标后，场上那张原 `CardUI` 是 `Visible=false`，
        /// 视图算出来的绿框谁也看不见；而面板里是**新建的镜像 `CardUI`**，不会继承任何状态。
        /// 判定真源留在视图（单机 `Arena.IsCardActivatableForUi` / 联机 `ArenaOl.IsCardActivatableForUi`，
        /// 与场上高亮同一实现），面板不复制规则、不写 Model。
        /// null ⇒ 一律不高亮（建面板时忘记注入会表现为"面板里没有绿框"，而不是乱画）。
        /// </summary>
        public Func<CardInstance, bool> ActivatableProbe;

        private bool _mouseInside;
        private Control _host;
        /// <summary>上一次左键按下的成员 UID 与时刻（自持双击判定用，0＝无）。</summary>
        private string _lastClickUid;
        private ulong _lastClickMs;

        /// <summary>当前面板依附的宿主卡（视图做"鼠标是否还在宿主上"判定用）。</summary>
        public Control Host => _host;

        public override void _Ready()
        {
            ZAsRelative = false;
            ZIndex = Layers.CardHoverPreview;
            MouseFilter = MouseFilterEnum.Stop;
            Visible = false;

            _panel = new PanelContainer();
            _panel.AddThemeStyleboxOverride("panel", BuildStyle());
            AddChild(_panel);

            _column = new VBoxContainer();
            _column.AddThemeConstantOverride("separation", 4);
            _panel.AddChild(_column);

            // 标题：明确"这一排是封印卡还是附属卡"（两者来自不同区域，不混排）
            _title = new Label
            {
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _title.AddThemeFontSizeOverride("font_size", 12);
            _title.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.75f));
            _column.AddChild(_title);

            // 卡面横排区：**只开横向滚动**（竖向禁用）。滚动条位于卡片外侧底部，
            // 鼠标可拖动左右滑；面板的 `ContainsGlobalPoint` 用 `GetGlobalRect()`，
            // 已经覆盖滚动条区域 ⇒ 拖滚动条不会被 `TickBadgePeek` 判成"鼠标离开"而收起。
            _scroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
            };
            _column.AddChild(_scroll);

            _row = new HBoxContainer();
            _row.AddThemeConstantOverride("separation", (int)Gap);
            _scroll.AddChild(_row);

            MouseEntered += () => _mouseInside = true;
            MouseExited += () => { _mouseInside = false; };
        }

        /// <summary>面板范围是否包含该全局点（供视图判断"鼠标是否还在展开区内"）。</summary>
        public bool ContainsGlobalPoint(Vector2 globalPos)
            => Visible && GetGlobalRect().Grow(4f).HasPoint(globalPos);

        /// <summary>
        /// 展开：在 host 旁边列出 members（**单行横向**，一次最多看 11.5 张；
        /// 更多时靠卡片外侧底部的横向滚动条左右滑，不再截断，也不再显示"…（共 N 张）"）。
        /// </summary>
        /// <param name="title">标题（"封印卡" / "附属卡" / "资产" / "墓地"），一次只列一种来源。</param>
        /// <param name="onPick">单击：置选中（不提交）。</param>
        /// <param name="onActivate">双击：发动激活能力（由调用方走既有双击链路）。</param>
        public void ShowNear(Control host, string title, IReadOnlyList<CardInstance> members,
                             Action<string> onPick, Action<CardUI> onActivate)
        {
            if (host == null || !GodotObject.IsInstanceValid(host) || members == null || members.Count == 0) return;

            _host = host;
            _onPick = onPick;
            _onActivate = onActivate;
            _title.Text = title ?? string.Empty;
            _title.Visible = !string.IsNullOrEmpty(title);

            foreach (Node child in _row.GetChildren())
            {
                _row.RemoveChild(child);
                child.QueueFree();
            }

            var scene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
            float cardW = CardLayoutHelper.CARD_WIDTH * CardScale;
            float cardH = CardLayoutHelper.CARD_HEIGHT * CardScale;

            // 【2026-10-19】不再按视口截断：**全部成员都造出来**，超出部分由横向滚动条查看。
            for (int i = 0; i < members.Count; i++)
            {
                var inst = members[i];
                if (inst == null) continue;

                // 【2026-10-19】横置卡必须照实画成横的：旋转 90° 后视觉框换成 (卡高, 卡宽)。
                // 玩家正是靠"这张是横着的"判断本回合还能不能横置自己（JC020 引匕的代价就是横置自身），
                // 一律画成正立会直接把判断带偏。
                bool tapped = inst.IsTapped;
                float slotW = tapped ? cardH : cardW;
                float slotH = tapped ? cardW : cardH;

                // 命中槽：**点击/双击由槽自己收回调**，里面的 CardUI 只负责画。
                // 不能让 CardUI 自己收（旧实现就是）：它的 `_Ready` 把 MouseFilter 强制成 Stop、
                // 还自带 `OnGuiInput`（附属卡双击会 emit 一个没人订阅的 `AttachmentDoubleClicked`），
                // 更要命的是 `MouseEntered` 会向 `CardPreviewBus` 申请**整卡预览**——那张预览与本面板
                // 同层(830)却后建，直接盖住面板；鼠标落点随之变成"在预览上"，面板 0.25s 后被判成
                // "鼠标离开"自动收起 ⇒ 用户看到的就是"点一下窗口就关了"。
                var slot = new Control
                {
                    CustomMinimumSize = new Vector2(slotW, slotH),
                    MouseFilter = MouseFilterEnum.Stop,
                };
                _row.AddChild(slot);

                if (scene == null) continue;
                var ui = scene.Instantiate<CardUI>();
                ui.CanDrag = false;
                ui.FreezeSmoothMove = true;
                slot.AddChild(ui);
                // 旋转/缩放都以左上角为基准，槽尺寸即"视觉包围盒"，命中区与画面天然一致
                ui.PivotOffset = Vector2.Zero;
                ui.Scale = new Vector2(CardScale, CardScale);
                ui.Setup(inst);
                ui.SetFace(!inst.IsHidden);
                // 【2026-10-19】可发动绿框：折叠成角标后，这是玩家唯一能看出"这张现在能发动"的地方
                ui.SetActivatableHighlight(ActivatableProbe?.Invoke(inst) == true);
                if (tapped)
                {
                    ui.SetTapped(true);
                    ui.RotationDegrees = 90f;
                    // pivot=(0,0) 时变换＝R·S·p + Position ⇒ 旋转后落在 x∈[-卡高·s, 0]，
                    // 必须右移一个"视觉宽度"才落进槽里。
                    ui.Position = new Vector2(cardH, 0f);
                }
                else ui.Position = Vector2.Zero;
                // **最后**再压 MouseFilter：`CardUI._Ready`（AddChild 时执行）会把它设回 Stop。
                ui.MouseFilter = MouseFilterEnum.Ignore;

                string uid = inst.UID;
                var capturedUi = ui;
                slot.GuiInput += ev => HandleMemberInput(ev, uid, capturedUi);
            }

            // ---- 尺寸：**按"11.5 张可视宽"算**，不能用 `_column.GetCombinedMinimumSize()` ----
            // 滚动模式下合并最小尺寸＝全部内容宽度（几十张会拉出屏幕），必须显式给可视宽度。
            var viewport = GetViewportRect().Size;
            float visibleW = Mathf.Min(viewport.X - 24f, VisibleCards * (cardW + Gap) - Gap);
            float rowH = cardH + ScrollBarH;            // 卡面 + 底部横向滚动条
            _scroll.CustomMinimumSize = new Vector2(visibleW, rowH);
            _scroll.ScrollHorizontal = 0;
            float titleH = _title.Visible ? _title.GetCombinedMinimumSize().Y : 0f;
            float sep = _title.Visible ? 4f : 0f;       // 与 `_column` 的 separation 一致
            Vector2 want = new Vector2(visibleW + Pad * 2f, titleH + sep + rowH + Pad * 2f);
            Vector2 hostPos = host.GlobalPosition;
            Vector2 hostSize = host.Size;
            float x = hostPos.X + hostSize.X / 2f - want.X / 2f;
            float y = hostPos.Y - want.Y - 8f;                 // 优先放上方
            if (y < 4f) y = hostPos.Y + hostSize.Y + 8f;       // 上方放不下 → 下方
            x = Mathf.Clamp(x, 4f, Math.Max(4f, viewport.X - want.X - 4f));
            y = Mathf.Clamp(y, 4f, Math.Max(4f, viewport.Y - want.Y - 4f));

            Position = new Vector2(x, y);
            Size = want;
            Visible = true;
        }

        /// <summary>
        /// 面板内成员的鼠标输入（**由命中槽转发**，不是 `CardUI` 自己）：
        ///  · 双击 ⇒ `onActivate`（发动激活能力）；单击 ⇒ `onPick`（只置选中、不提交）。**单击不关面板**。
        ///
        /// 双击判定不能只看事件上的 `DoubleClick`：该标记由 Godot 在 **GUI 分发**阶段置位，而单机
        /// `Arena._Input` / 联机 `ArenaOl._Input` 先于 Control `_GuiInput` 执行，命中场上卡时会
        /// `AcceptEvent()` 把左键整条吞掉 ⇒ 两次按下都到不了这里。故再叠一层**自持时间窗**
        /// （同一张卡、两次按下 ≤ `DoubleClickMs`），与 `Arena.TryHandleHandActivationDoubleClick` 同口径。
        /// </summary>
        private void HandleMemberInput(InputEvent ev, string uid, CardUI capturedUi)
        {
            if (ev is not InputEventMouseButton mb) return;
            if (!mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;

            ulong now = Time.GetTicksMsec();
            bool sameCard = _lastClickUid == uid && now - _lastClickMs <= DoubleClickMs;
            bool isDoubleClick = mb.DoubleClick || sameCard;

            // 【2026-10-19 诊断】"点了没反应"时必须能在日志上区分四种情形：
            // 面板没收到 → 收到了但判定成单击 → 判定成双击但出口为空 → 出口被门禁静默拒绝。
            Godot.GD.Print($"[CardBadgePeek] 成员左键：{uid} DoubleClick={mb.DoubleClick} sameCard={sameCard} " +
                           $"isDouble={isDoubleClick} onActivate={(_onActivate == null ? "null" : "set")} onPick={(_onPick == null ? "null" : "set")}");

            // 双击已消费 ⇒ 立刻清记账，避免"第三次点击"又被算成一次双击
            _lastClickUid = isDoubleClick ? null : uid;
            _lastClickMs = now;

            if (isDoubleClick)
            {
                // 双击＝发动激活能力（JC020 等被折叠的附属卡唯一入口）
                if (_onActivate == null)
                    Godot.GD.PrintErr($"[CardBadgePeek] 双击 {uid} 但面板未注入 OnActivate ⇒ 无任何动作（调用方装配问题）");
                _onActivate?.Invoke(capturedUi);
                return;
            }
            // 单击＝置选中（不提交）；没有选择窗时纯浏览
            _onPick?.Invoke(uid);
        }

        public void HideNow()
        {
            if (!Visible) return;
            _mouseInside = false;
            // 收起即清双击记账：否则"离开→立刻回来点同一张卡"会被误判成一次双击（假激活）
            _lastClickUid = null;
            Visible = false;
        }

        public bool IsOpen => Visible && _mouseInside;

        private static StyleBoxFlat BuildStyle()
        {
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.08f, 0.10f, 0.94f),
                BorderColor = new Color(0.85f, 0.75f, 0.45f, 0.9f),
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(6);
            sb.SetContentMarginAll(Pad);
            return sb;
        }
    }
}
