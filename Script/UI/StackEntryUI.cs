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
                    var oldParent = reuseCardUI.GetParent();
                    oldParent?.RemoveChild(reuseCardUI);
                    AddChild(reuseCardUI);
                    // 清理从手牌/拖拽带来的残留变换状态（对齐 Arena.PlaceCardInContainer 的复位套路）：
                    // 否则残留的缩放/旋转/轴心/ZIndex 会导致卡在堆叠区内显示错乱（变形、偏移、盖在最上层）。
                    reuseCardUI.AnchorLeft = 0; reuseCardUI.AnchorRight = 0;
                    reuseCardUI.AnchorTop = 0; reuseCardUI.AnchorBottom = 0;
                    reuseCardUI.Scale = Vector2.One;
                    reuseCardUI.PivotOffset = new Vector2(100, 140) / 2f;
                    reuseCardUI.RotationDegrees = 0;        // 堆叠内一律立置，不保留横置旋转
                    reuseCardUI.ZIndex = 0;                  // 清掉拖拽遗留的 ZIndex=1000
                    reuseCardUI.CustomMinimumSize = new Vector2(100, 140);
                    reuseCardUI.Size = new Vector2(100, 140);
                    reuseCardUI.SetFace(true);
                    reuseCardUI.CanDrag = false;        // 堆叠中的卡不参与拖拽
                    // 冻结平滑移动：卡进入 StackEntryUI 后由 _Process 每帧锁定全局位置，
                    // 若保留平滑逻辑会基于旧 _targetPosition（手牌/拖拽时坐标）把它拉回原地，导致乱飞。
                    // 结算移出堆叠时由 SyncCardUIToZone 解冻。
                    reuseCardUI.FreezeSmoothMove = true;
                    // 停用卡自身 _Process：位置完全由本节点 _Process 每帧锁定，避免双重控制冲突。
                    reuseCardUI.ProcessMode = ProcessModeEnum.Disabled;
                    _cardUI = reuseCardUI;              // 后续 GetGlobalCenter / _Process 用真卡定位
                }
                else
                {
                    // 无复用卡：新建一个 CardUI 实例显示（理论上卡片型都应传入 reuseCardUI）
                    var scene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/CardUI.tscn");
                    var newCard = scene.Instantiate<CardUI>();
                    AddChild(newCard);
                    newCard.AnchorLeft = 0; newCard.AnchorRight = 0;
                    newCard.AnchorTop = 0; newCard.AnchorBottom = 0;
                    newCard.CustomMinimumSize = new Vector2(100, 140);
                    newCard.Size = new Vector2(100, 140);
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
                _effectText.Text = entry.EffectText ?? "效果发动";

                // 触发效果：尝试显示来源卡 UI（如银钥探求者的卡图）
                var sourceTemplate = entry.StackEffect?.Source?.PlayerTemplate;
                if (_triggerCardArt != null && sourceTemplate != null && !string.IsNullOrEmpty(sourceTemplate.UID))
                {
                    string path = ResolveCardImagePath(sourceTemplate.UID);
                    var tex = ResourceLoader.Load<Texture2D>(path);
                    if (tex != null)
                    {
                        _triggerCardArt.Texture = tex;
                        _triggerCardArt.Visible = true;
                    }
                    else
                    {
                        GD.PrintErr($"[StackEntryUI] 触发来源卡卡图加载失败: {path}");
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
