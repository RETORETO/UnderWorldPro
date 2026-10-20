using Godot;
using Script.Game.Entities;
using Script.Data.Cards;

namespace Script.UI
{
    public partial class StackEntryUI : Control
    {
        // ---------- 子节点引用 ----------
        private CardUI _cardUI;
        private AreaCardUI _areaCardUI;
        private Panel _effectPanel;
        private Label _effectBadge;
        private Label _effectText;
        private TextureRect _triggerCardArt;

        // ---------- 数据 ----------
        private StackEntry _entry;

        public StackEntry Entry => _entry;

        /// <summary>
        /// 本条目当前展示的卡面（卡片型恒非空；效果型为 null）。
        /// 供调用方在 `Setup` 后补注入依赖（如统计依赖仅供自建卡面使用）。
        /// </summary>
        public CardUI ShownCard => _cardUI;

        public override void _Ready()
        {
            // 注意：tscn 不再内嵌 CardUI 子节点（改为 Setup 时动态挂入复用卡），
            // 因此此处只获取 EffectPanel 相关节点，_cardUI 由 Setup 赋值。
            _effectPanel = GetNode<Panel>("EffectPanel");
            _effectBadge = GetNode<Label>("EffectPanel/EffectBadge");
            _effectText = GetNode<Label>("EffectPanel/EffectText");
            _triggerCardArt = GetNodeOrNull<TextureRect>("TriggerCardArt");
        }

        public override void _Process(double delta)
        {
            // 每帧把内部卡 UI 的全局位置硬锁定到本条目中心，与 GetGlobalCenter() 完全一致。
            // 这样彻底绕开 VBox 容器布局 + 复用卡反复 RemoveChild/AddChild 带来的不可预测锚点偏移，
            // 保证卡面中心永远与箭头起点（堆叠条目中心）重合，不再杂乱偏移。
            // 仅当卡仍是本节点子节点时才锁定；结算移出堆叠后卡被挂到别的区域，
            // GetParent() 不再是本节点，跳过以免把已移走的卡强行拉回堆叠。
            if (_cardUI != null && _cardUI.GetParent() == this)
            {
                _cardUI.GlobalPosition = GlobalPosition + (Size - _cardUI.Size) / 2f;
            }
            if (_areaCardUI != null && _areaCardUI.GetParent() == this)
            {
                _areaCardUI.GlobalPosition = GlobalPosition + (Size - _areaCardUI.Size) / 2f;
            }
        }

        /// <summary>
        /// 把一张真卡 UI 挂进本条目并归一化它的视觉/交互状态（**幂等**，已挂在本节点时只做归一化）。
        ///
        /// 之所以独立成公开方法：卡进入 `CardZone.Stack` 之后，除了 `RefreshStackUI` 这一次挂载，
        /// 还可能被**其它同步路径**把手里的 CardUI 抢挂回区域容器（实测：`ZoneManager.MoveToStack`
        /// 里 `TransferOwnership` 会以**旧区域（Hand）**先 `FireMoved` 一次，此时 `CurrentZone`
        /// 还没改成 Stack，`Arena.SyncCardUIToZone` 走手牌分支 `container.AddChild` ⇒ 刚挂进堆叠的
        /// 卡又被搬回手牌容器，堆叠区只剩空白槽）。`Arena` 侧发现"逻辑在 Stack 但 UI 不在本条目"
        /// 时调本方法自愈，不必重跑整个 `Setup`。
        /// </summary>
        public void AttachCard(CardUI cardUI)
        {
            if (cardUI == null || !GodotObject.IsInstanceValid(cardUI)) return;

            if (cardUI.GetParent() != this)
                cardUI.GetParent()?.RemoveChild(cardUI);
            if (cardUI.GetParent() != this)
                AddChild(cardUI);

            // 清理从手牌/拖拽带来的残留变换状态（对齐 Arena.PlaceCardInContainer 的复位套路）：
            // 否则残留的缩放/旋转/轴心/ZIndex 会导致卡在堆叠区内显示错乱（变形、偏移、盖在最上层）。
            cardUI.AnchorLeft = 0; cardUI.AnchorRight = 0;
            cardUI.AnchorTop = 0; cardUI.AnchorBottom = 0;
            cardUI.Scale = Vector2.One;
            cardUI.Visible = true;   // 【P3a】曾被折叠成角标的卡进堆叠时必须恢复可见
            cardUI.PivotOffset = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT) / 2f;
            cardUI.RotationDegrees = 0;        // 堆叠内一律立置，不保留横置旋转
            cardUI.ZIndex = 0;                  // 清掉拖拽/手牌排版遗留的 ZIndex（手牌基准 300+）
            cardUI.CustomMinimumSize = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT);
            cardUI.Size = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT);
            cardUI.SetFace(true);
            cardUI.CanDrag = false;        // 堆叠中的卡不参与拖拽（ArrangeHandCards 会无脑设 true，故必须复位）
            // 冻结平滑移动：卡进入 StackEntryUI 后由 _Process 每帧锁定全局位置，
            // 若保留平滑逻辑会基于旧 _targetPosition（手牌/拖拽时坐标）把它拉回原地，导致乱飞。
            // 结算移出堆叠时由 SyncCardUIToZone 解冻。
            cardUI.FreezeSmoothMove = true;
            // 停用卡自身 _Process：位置完全由本节点 _Process 每帧锁定，避免双重控制冲突。
            cardUI.ProcessMode = ProcessModeEnum.Disabled;
            _cardUI = cardUI;              // 后续 GetGlobalCenter / _Process 用真卡定位
        }

        /// <summary>
        /// 用数据模型初始化显示。卡片型实例化 CardUI 并显示卡面；效果型显示半透明遮罩 + 角标 + 文本。
        /// reuseCardUI：卡片型复用的真卡 UI（入堆叠时手牌里的那张），传入后挂到本节点显示。
        /// 由于 tscn 不再内嵌 CardUI 子节点，不存在同名冲突，直接 AddChild 即可。
        /// </summary>
        public void Setup(StackEntry entry, CardUI reuseCardUI = null)
        {
            _entry = entry;
            if (entry == null) return;

            if (entry.IsCard && entry.SourceCard != null)
            {
                _effectPanel.Visible = false;

                if (reuseCardUI != null)
                {
                    // 复用真卡：从原父移除并挂到本节点。
                    // tscn 内已无内嵌 CardUI 子节点，无需重命名，无同名冲突风险。
                    AttachCard(reuseCardUI);
                }
                else
                {
                    // 无复用卡：新建一个 CardUI 实例显示（理论上卡片型都应传入 reuseCardUI）
                    var scene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
                    var newCard = scene.Instantiate<CardUI>();
                    AddChild(newCard);
                    newCard.AnchorLeft = 0; newCard.AnchorRight = 0;
                    newCard.AnchorTop = 0; newCard.AnchorBottom = 0;
                    newCard.CustomMinimumSize = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT);
                    newCard.Size = new Vector2(CardLayoutHelper.CARD_WIDTH, CardLayoutHelper.CARD_HEIGHT);
                    newCard.Setup(entry.SourceCard);
                    newCard.SetFace(true);
                    newCard.CanDrag = false;
                    newCard.FreezeSmoothMove = true;
                    newCard.ProcessMode = ProcessModeEnum.Disabled;
                    _cardUI = newCard;
                }
            }
            else
            {
                _effectPanel.Visible = true;
                _effectText.Text = ResolveEffectText(entry);

                // 触发/激活效果：显示来源卡卡图（玩家卡 **与秘社卡** 都支持）。
                // 【2026-10-20】旧实现只认 `Source.PlayerTemplate`，而秘社卡没有 PlayerTemplate
                // ⇒ 秘社发动效果时堆叠条目只剩一个空白的"效果发动"角标（用户实测）。
                string artPath = ResolveSourceArtPath(entry);
                if (_triggerCardArt != null && !string.IsNullOrEmpty(artPath))
                {
                    var tex = ResourceLoader.Load<Texture2D>(artPath);
                    if (tex != null)
                    {
                        _triggerCardArt.Texture = tex;
                        _triggerCardArt.Visible = true;
                    }
                    else
                    {
                        GD.PrintErr($"[StackEntryUI] 触发来源卡卡图加载失败: {artPath}");
                        _triggerCardArt.Visible = false;
                    }
                }
                else if (_triggerCardArt != null)
                {
                    _triggerCardArt.Visible = false;
                }
            }
        }

        /// <summary>
        /// 【2026-10-20】条目文案兜底：`EffectText` 为空（服务端没给 / 旧存档）时用来源卡名拼一句，
        /// 绝不把空白面板摆给玩家看。
        /// </summary>
        public static string ResolveEffectText(StackEntry entry)
        {
            if (entry == null) return "效果发动";
            if (!string.IsNullOrEmpty(entry.EffectText)) return entry.EffectText;
            var src = entry.StackEffect?.Source ?? entry.SourceCard;
            string name = src?.EffectiveCardName;
            if (string.IsNullOrEmpty(name)) name = src?.CouncilTemplate?.CardName;
            if (string.IsNullOrEmpty(name)) name = src?.PlayerTemplate?.CardName;
            return string.IsNullOrEmpty(name) ? "效果发动" : $"{name}·效果";
        }

        /// <summary>
        /// 【2026-10-20】来源卡卡图路径：秘社卡走 `CouncilCardData.CardImagePath`（它自己带完整路径），
        /// 玩家卡仍按"UID 前缀目录"约定拼（与 `CardUI.ResolveCardImagePath` 同款）。
        /// </summary>
        public static string ResolveSourceArtPath(StackEntry entry)
        {
            if (entry == null) return null;
            var src = entry.StackEffect?.Source ?? entry.SourceCard;
            if (src?.CouncilTemplate != null) return src.CouncilTemplate.CardImagePath;
            var pt = src?.PlayerTemplate;
            if (pt != null && !string.IsNullOrEmpty(pt.UID)) return ResolveCardImagePath(pt.UID);
            return null;
        }

        /// <summary>
        /// 根据 UID 推断卡图目录：取开头连续字母作为目录名。
        /// 与 CardUI / TriggerEntryUI 保持一致。
        /// </summary>
        private static string ResolveCardImagePath(string uid)
        {
            // 【2026-10-12 Web 修复】char.IsLetter 走 Unicode 类别表（全球化路径），Web 上会 abort；
            // 改用共享的 ASCII 前缀判定（卡号 UID 均为 ASCII，行为一致）。
            string folder = Script.IO.WebSafeText.LeadingAsciiLetters(uid, "JC");
            return $"res://Cards/Image/PlayerCards/{folder}/{uid}.jpg";
        }

        /// <summary>
        /// 为赢取地区（Kind=AreaWon）条目挂载一张地区卡复制 UI：
        /// 复用 AreaCardUI.tscn，Setup 对应地区数据并正面展示，作为堆叠区内可见的"卡面"。
        /// 调用前应确保本条目 Kind==AreaWon。
        /// </summary>
        public void AttachAreaCard(AreaCardData data, PackedScene areaCardScene)
        {
            if (data == null || areaCardScene == null) return;
            var ui = areaCardScene.Instantiate<AreaCardUI>();
            AddChild(ui);
            ui.AnchorLeft = 0; ui.AnchorRight = 0;
            ui.AnchorTop = 0; ui.AnchorBottom = 0;
            ui.CustomMinimumSize = new Vector2(140, 100);
            ui.Size = new Vector2(140, 100);
            ui.Setup(data);          // Setup 内部已 SetFace(true) 显示正面卡面
            ui.MouseFilter = MouseFilterEnum.Ignore;
            ui.ZIndex = Layers.StackPanelCard;   // 确保地区卡复制 UI 显示在 EffectPanel 文字层之上
            ui.ZAsRelative = true;
            ui.ProcessMode = ProcessModeEnum.Disabled;
            _effectPanel.Visible = false;   // 用卡图替代文字面板
            _areaCardUI = ui;
        }

        /// <summary>返回条目中心在全局坐标系下的点，供箭头起点使用。</summary>
        public Vector2 GetGlobalCenter()
        {
            return GlobalPosition + Size / 2f;
        }

        /// <summary>是否卡片型（供外部快速判断）。</summary>
        public bool IsCardEntry => _entry != null && _entry.IsCard;
    }
}
