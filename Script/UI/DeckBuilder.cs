using Godot;
using System.Collections.Generic;
using Script.Data.Cards;
using Script.Enums;
using Script.IO;
using Script.UI;

public partial class DeckBuilder : Control
{
    private VBoxContainer _poolList;
    private VBoxContainer _deckList;
    private LineEdit _nameEdit;
    private OptionButton _councilOption;
    private Label _countLabel;
    private OptionButton _loadOption;
    private LineEdit _searchBox;
    private Button _prevPage;
    private Button _nextPage;
    private Label _pageLabel;
    // 【筛选模式】卡池搜索对象的切换：卡牌 / 秘社
    private CheckBox _cardModeCheck;
    private CheckBox _councilModeCheck;
    private Control _poolPager;
    /// <summary>true＝秘社模式（卡池展示秘社候选）；false＝卡牌模式（原行为）。</summary>
    private bool _councilMode;
    /// <summary>互斥回调重入保护：程序化设置 ButtonPressed 会再次触发 Toggled。</summary>
    private bool _syncingModeChecks;
    /// <summary>秘社模式下过滤后的候选列表（用于渲染）。</summary>
    private List<string> _councilFilterPaths = new List<string>();

    // 【秘社批次】构筑限制展示与拦截（"当前 X 张 / 需要 ≥N 张"）
    private VBoxContainer _restrictionBox;
    private Label _restrictionSummary;

    // 分页
    private const int PageSize = 40;   // 单条结果现在是带五行数据的信息面板（约 26 个控件），比原来的一张卡图重得多
    private int _poolPage = 0;

    // 缓存：避免重复加载纹理（路径 -> Texture2D）
    private Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

    // 搜索结果面板的外观资源（所有行共用一份，避免每建一行就new一份主题资源）
    private StyleBoxFlat _poolRowStyle;

    // 编辑态
    private Dictionary<string, int> _workingCards = new Dictionary<string, int>();   // 模板路径 -> 份数
    private string _workingCouncil = "res://Cards/Data/CouncilCards/JC/MSJC009_mishe.tres";

    // 卡池（模板路径列表，用于渲染）
    private List<string> _poolPaths = new List<string>();          // 过滤后用于渲染
    private List<string> _allPoolPaths = new List<string>();        // 全量，供过滤
    private string _poolFilter = "";                                // 搜索框过滤关键字
    // 秘社候选
    private List<string> _councilPaths = new List<string>();

    private const string PlayerCardsDir = "res://Cards/Data/PlayerCards";
    private const string CouncilCardsDir = "res://Cards/Data/CouncilCards";
    private const string DecksDir = "res://Decks";

    public override void _Ready()
    {
        GD.Print("【DeckBuilder】场景加载完成");

        _poolList = GetNode<VBoxContainer>("PoolPanel/PoolScroll/PoolList");
        _deckList = GetNode<VBoxContainer>("DeckPanel/DeckScroll/DeckList");
        _nameEdit = GetNode<LineEdit>("BottomBar/NameEdit");
        _councilOption = GetNode<OptionButton>("BottomBar/CouncilOption");
        _countLabel = GetNode<Label>("BottomBar/CountLabel");
        _loadOption = GetNode<OptionButton>("LoadOption");
        _searchBox = GetNode<LineEdit>("PoolPanel/SearchBox");
        _searchBox.TextChanged += OnSearchTextChanged;
        _prevPage = GetNode<Button>("PoolPanel/PoolPager/PrevPage");
        _nextPage = GetNode<Button>("PoolPanel/PoolPager/NextPage");
        _pageLabel = GetNode<Label>("PoolPanel/PoolPager/PageLabel");
        _poolPager = GetNode<Control>("PoolPanel/PoolPager");
        _prevPage.Pressed += () => { if (!_councilMode && _poolPage > 0) { _poolPage--; RefreshPoolView(); } };
        _nextPage.Pressed += () => { if (!_councilMode && (_poolPage + 1) * PageSize < _poolPaths.Count) { _poolPage++; RefreshPoolView(); } };

        // 【筛选模式】"卡牌 / 秘社"互斥复选框：默认勾选卡牌。
        _cardModeCheck = GetNode<CheckBox>("PoolPanel/FilterModeBar/CardModeCheck");
        _councilModeCheck = GetNode<CheckBox>("PoolPanel/FilterModeBar/CouncilModeCheck");
        _syncingModeChecks = true;
        _cardModeCheck.ButtonPressed = true;
        _councilModeCheck.ButtonPressed = false;
        _syncingModeChecks = false;
        _councilMode = false;
        _cardModeCheck.Toggled += pressed => OnModeCheckToggled(true, pressed);
        _councilModeCheck.Toggled += pressed => OnModeCheckToggled(false, pressed);
        UpdateModeUi();

        // 【秘社批次】构筑限制面板：运行期动态创建（不依赖场景改动，避免 .tscn 与本类字段不同步）。
        BuildRestrictionPanel();

        // 搜索防抖计时器
        _searchTimer = new Timer();
        _searchTimer.WaitTime = 0.25f;
        _searchTimer.OneShot = true;
        AddChild(_searchTimer);
        _searchTimer.Timeout += () => { ApplyPoolFilter(); RefreshPoolView(); };

        GetNode<Button>("BottomBar/SaveButton").Pressed += OnSave;
        GetNode<Button>("BottomBar/BackButton").Pressed += OnBack;
        _councilOption.ItemSelected += OnCouncilSelected;
        _loadOption.ItemSelected += OnLoadSelected;

        ScanPool();
        ScanCouncils();
        BuildLoadOptions();
        BuildCouncilOptions();
        RefreshPoolView();

        // 若从房间/主菜单带入了要编辑的卡组，则载入
        var editPath = GameManager.Instance.EditingDeckPath;
        if (!string.IsNullOrEmpty(editPath))
        {
            _loadOption.Select(FindLoadIndex(editPath));
            LoadDeckFromFile(editPath);
        }
        else
        {
            RefreshDeckView();
        }
    }

    // ============================================================
    // 扫描卡池 / 秘社
    // ============================================================
    private void ScanPool()
    {
        _allPoolPaths.Clear();
        ScanDirRecursive(PlayerCardsDir, _allPoolPaths);
        _allPoolPaths.Sort();
        GD.Print($"【DeckBuilder】扫描到卡池 {_allPoolPaths.Count} 张");
        ApplyPoolFilter();
    }

    private void ScanDirRecursive(string dirPath, List<string> sink)
    {
        using var dir = DirAccess.Open(dirPath);
        if (dir == null) return;
        dir.ListDirBegin();
        string name = dir.GetNext();
        while (!string.IsNullOrEmpty(name))
        {
            string full = dirPath + "/" + name;
            if (dir.CurrentIsDir())
                ScanDirRecursive(full, sink);
            else if (name.EndsWith(".tres", System.StringComparison.OrdinalIgnoreCase))
                sink.Add(full);
            name = dir.GetNext();
        }
        dir.ListDirEnd();
    }

    // 根据搜索关键字过滤当前模式的候选列表（按文件路径卡号片段 / 秘社名匹配，不区分大小写）
    private void ApplyPoolFilter()
    {
        _poolPage = 0;   // 过滤后回到第一页

        string f = string.IsNullOrWhiteSpace(_poolFilter) ? "" : _poolFilter.Trim().ToLowerInvariant();

        if (_councilMode)
        {
            _councilFilterPaths.Clear();
            foreach (var p in _councilPaths)
            {
                if (f.Length == 0) { _councilFilterPaths.Add(p); continue; }
                if (System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(f))
                {
                    _councilFilterPaths.Add(p);
                    continue;
                }
                // 秘社数量少（十余张），允许按卡名搜，代价可接受
                var c = ResourceLoader.Load<CouncilCardData>(p);
                if (c != null && !string.IsNullOrEmpty(c.CardName) && c.CardName.ToLowerInvariant().Contains(f))
                    _councilFilterPaths.Add(p);
            }
            return;
        }

        _poolPaths.Clear();
        if (f.Length == 0)
        {
            _poolPaths.AddRange(_allPoolPaths);
        }
        else
        {
            foreach (var p in _allPoolPaths)
            {
                string file = System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                if (file.Contains(f))
                    _poolPaths.Add(p);
            }
        }
    }

    private Timer _searchTimer;

    private void OnSearchTextChanged(string newText)
    {
        _poolFilter = newText;
        // 防抖：停止输入 250ms 后才真正过滤+刷新，避免每字符全量重载导致卡死/闪退
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    // ============================================================
    // 【筛选模式】卡牌 / 秘社 互斥切换
    // ============================================================
    /// <summary>
    /// 两个复选框互斥：勾选一个自动取消另一个；取消一个自动勾选另一个（始终恰好有一个处于勾选态）。
    /// 切换后立即重算过滤 + 重绘卡池，保证结果与展示同步。
    /// </summary>
    private void OnModeCheckToggled(bool isCardCheck, bool pressed)
    {
        if (_syncingModeChecks) return;
        _syncingModeChecks = true;
        _councilMode = isCardCheck ? !pressed : pressed;
        _cardModeCheck.ButtonPressed = !_councilMode;
        _councilModeCheck.ButtonPressed = _councilMode;
        _syncingModeChecks = false;

        ApplyPoolFilter();
        RefreshPoolView();
        UpdateModeUi();
        GD.Print($"【DeckBuilder】切换筛选模式 → {(_councilMode ? "秘社" : "卡牌")}");
    }

    /// <summary>按当前模式同步提示文案与控件显隐（秘社候选只有十余张，无需分页）。</summary>
    private void UpdateModeUi()
    {
        if (_searchBox != null)
            _searchBox.PlaceholderText = _councilMode ? "输入秘社名或编号过滤，如 MSJC" : "输入卡号过滤，如 JC";
        if (_poolPager != null)
            _poolPager.Visible = !_councilMode;
    }

    private void ScanCouncils()
    {
        _councilPaths.Clear();
        ScanDirRecursive(CouncilCardsDir, _councilPaths);   // 秘社按系列分子目录，需递归扫描
        _councilPaths.Sort();
        ApplyPoolFilter();
        GD.Print($"【DeckBuilder】扫描到秘社 {_councilPaths.Count} 张");
    }

    // ============================================================
    // 下拉选项构建
    // ============================================================
    private void BuildLoadOptions()
    {
        _loadOption.Clear();
        _loadOption.AddItem("（新建卡组）");
        using var dir = DirAccess.Open(DecksDir);
        if (dir != null)
        {
            dir.ListDirBegin();
            string name = dir.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (!dir.CurrentIsDir() && name.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                    _loadOption.AddItem(name);
                name = dir.GetNext();
            }
            dir.ListDirEnd();
        }
    }

    private int FindLoadIndex(string path)
    {
        for (int i = 1; i < _loadOption.ItemCount; i++)
            if (DecksDir + "/" + _loadOption.GetItemText(i) == path)
                return i;
        return 0;
    }

    private void BuildCouncilOptions()
    {
        _councilOption.Clear();
        int sel = 0;
        for (int i = 0; i < _councilPaths.Count; i++)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(_councilPaths[i]);
            _councilOption.AddItem(fileName);
            if (_councilPaths[i] == _workingCouncil) sel = i;
        }
        if (_councilPaths.Count > 0) _councilOption.Select(sel);
    }

    // ============================================================
    // 卡池渲染 & 交互
    // ============================================================
    private void RefreshPoolView()
    {
        int ok = 0, fail = 0;
        foreach (var child in _poolList.GetChildren())
            child.QueueFree();

        // 【筛选模式】秘社模式走独立的候选列表渲染（来源、加入规则都与卡牌不同）
        if (_councilMode)
        {
            RefreshCouncilPoolView();
            return;
        }

        int total = _poolPaths.Count;
        int pageCount = Mathf.Max(1, (total + PageSize - 1) / PageSize);
        if (_poolPage >= pageCount) _poolPage = pageCount - 1;
        if (_pageLabel != null) _pageLabel.Text = $"{_poolPage + 1} / {pageCount}（共 {total} 张）";

        // 只渲染当前页，避免一次性加载全部卡牌导致内存/显存爆掉闪退
        int start = _poolPage * PageSize;
        int end = Mathf.Min(start + PageSize, total);
        for (int i = start; i < end; i++)
        {
            string path = _poolPaths[i];
            var data = ResourceLoader.Load<PlayerCardData>(path);
            Texture2D tex = null;
            if (data != null)
            {
                string imgPath = CardUI.ResolveCardImagePath(data.UID);
                if (!_texCache.TryGetValue(imgPath, out tex))
                {
                    tex = ResourceLoader.Load<Texture2D>(imgPath);
                    if (tex != null) _texCache[imgPath] = tex;   // 缓存，避免重复加载
                }
            }
            if (tex != null) ok++; else { fail++; GD.PrintErr($"【DeckBuilder】卡图加载失败: {data?.UID} ({path})"); }

            // 每张结果＝一张信息面板：卡图 + 结构化数据列 + 加入按钮
            _poolList.AddChild(BuildCardPanel(path, data, tex));
        }
        GD.Print($"【DeckBuilder】刷新卡池（第 {_poolPage + 1} 页）完成：本页 {ok} 张 / 失败 {fail} 张，子节点数 {_poolList.GetChildCount()} / 总计 {total}");
    }

    /// <summary>
    /// 【筛选模式】秘社候选列表：卡图 + 秘社名 + 编号 + 「添加」按钮（点卡图同样生效）。
    /// 全秘社一次性展示（十余张，无需分页）；加入规则见 <see cref="SetCouncil"/>。
    /// </summary>
    private void RefreshCouncilPoolView()
    {
        int fail = 0;
        foreach (var path in _councilFilterPaths)
        {
            var data = ResourceLoader.Load<CouncilCardData>(path);
            string imgPath = ResolveCouncilImagePath(data);
            Texture2D tex = null;
            if (!string.IsNullOrEmpty(imgPath))
            {
                if (!_texCache.TryGetValue(imgPath, out tex))
                {
                    tex = ResourceLoader.Load<Texture2D>(imgPath);
                    if (tex != null) _texCache[imgPath] = tex;   // 与卡池共用缓存，避免重复加载
                }
            }
            if (tex == null) { fail++; GD.PrintErr($"【DeckBuilder】秘社卡图加载失败: {data?.UID} ({path})"); }

            bool isCurrent = path == _workingCouncil;
            string displayName = string.IsNullOrEmpty(data?.CardName)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : data.CardName;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var tr = MakeCardThumb(tex);
            tr.GuiInput += (InputEvent e) =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    SetCouncil(path);
            };
            row.AddChild(tr);

            var info = new VBoxContainer();
            info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            info.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            var nameLabel = new Label { Text = displayName, VerticalAlignment = VerticalAlignment.Center };
            nameLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            nameLabel.Modulate = isCurrent ? new Color(0.55f, 0.95f, 0.55f) : Colors.White;
            var subLabel = new Label
            {
                Text = System.IO.Path.GetFileNameWithoutExtension(path),
                VerticalAlignment = VerticalAlignment.Top,
                Modulate = new Color(0.75f, 0.75f, 0.75f),
            };
            info.AddChild(nameLabel);
            info.AddChild(subLabel);
            row.AddChild(info);

            var addBtn = new Button { Text = isCurrent ? "当前秘社" : "添加" };
            addBtn.Disabled = isCurrent;
            addBtn.CustomMinimumSize = new Vector2(64, 0);
            addBtn.Pressed += () => SetCouncil(path);
            row.AddChild(addBtn);

            _poolList.AddChild(row);
        }

        if (_pageLabel != null) _pageLabel.Text = $"共 {_councilFilterPaths.Count} 个秘社";
        GD.Print($"【DeckBuilder】刷新秘社候选：{_councilFilterPaths.Count} 个 / 加载失败 {fail} 张");
    }

    // ============================================================
    // 搜索结果的结构化信息面板（五行数据）
    // ============================================================

    /// <summary>卡片行外观资源（全局复用一份，避免每行重复创建主题资源）。</summary>
    private StyleBoxFlat GetPoolRowStyle()
    {
        if (_poolRowStyle != null) return _poolRowStyle;
        _poolRowStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.13f, 0.16f, 0.85f),
            BorderColor = new Color(0.34f, 0.35f, 0.42f, 0.9f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 6, ContentMarginTop = 4, ContentMarginRight = 6, ContentMarginBottom = 4,
        };
        return _poolRowStyle;
    }

    /// <summary>
    /// 一张搜索结果面板：PanelContainer（边框+底色）内为「卡图 | 详细数据列 | 添加按钮」。
    /// 详细数据固定五行：<c>卡名/独有卡名</c>、<c>费用/颜色/魔法领域</c>、<c>类别/子类别</c>、<c>能力</c>、<c>编号</c>。
    /// </summary>
    private PanelContainer BuildCardPanel(string path, PlayerCardData data, Texture2D tex)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", GetPoolRowStyle());

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        var thumb = MakeCardThumb(tex);
        thumb.GuiInput += (InputEvent e) =>   // 卡图可点击加入，保持原有交互
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                AddCard(path);
        };
        row.AddChild(thumb);

        row.AddChild(BuildCardDetailColumn(path, data));

        var addBtn = new Button { Text = "添加", CustomMinimumSize = new Vector2(56, 0) };
        addBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        addBtn.Pressed += () => AddCard(path);
        row.AddChild(addBtn);

        return panel;
    }

    /// <summary>
    /// 详细数据列（固定五行，顺序写死在方法体内，保证所有卡牌格式一致）。
    /// 条件字段（独有卡名 / 魔法领域 / 子类别）取不到值时**整段不拼进文本**——不留占位符、不留空位；
    /// 每行内的字段各自带"键：值"，用分隔符隔开，既不合并也不错位。
    /// </summary>
    private static VBoxContainer BuildCardDetailColumn(string path, PlayerCardData data)
    {
        var col = new VBoxContainer();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        col.AddThemeConstantOverride("separation", 2);

        // ① 卡名（恒显示）｜独有卡名（仅当该卡确实拥有时显示）
        var f1 = new List<string> { "卡名：" + CardNameText(data, path) };
        if (HasUniqueField(data))
            f1.Add("独有卡名：" + data.UniqueField.Trim());
        col.AddChild(MakeInfoLine(f1, new Color(1f, 0.92f, 0.72f), 16));

        // ② 费用（恒显示）｜颜色（恒显示）｜魔法领域（仅当存在时显示）｜组织（仅当存在时显示，位于魔法领域右侧）
        var f2 = new List<string> { "费用：" + CostText(data), "颜色：" + ColorsText(data) };
        string domains = DomainsText(data);
        if (domains.Length > 0)
            f2.Add("魔法领域：" + domains);
        string org = OrganizationText(data);
        if (org.Length > 0)
            f2.Add("组织：" + org);
        col.AddChild(MakeInfoLine(f2));

        // ③ 类别（恒显示）｜子类别（仅当存在时显示）
        var f3 = new List<string> { "类别：" + CategoryText(data) };
        string subs = SubCategoriesText(data);
        if (subs.Length > 0)
            f3.Add("子类别：" + subs);
        col.AddChild(MakeInfoLine(f3));

        // ④ 能力：固定顺序罗列，数值为 0 的项整体略去，其余保持相对顺序
        var abilities = AbilityFields(data);
        col.AddChild(MakeTextLine(abilities.Count > 0 ? "能力图标：" + string.Join("，", abilities) : "能力图标："));

        // ⑤ 编号（恒显示）
        col.AddChild(MakeTextLine("编号：" + UidText(data, path), new Color(0.75f, 0.75f, 0.8f)));

        return col;
    }

    /// <summary>信息面板中的一行：字段用分隔符隔开，宽度不足时整行换行（绝不截断丢失数据）。</summary>
    private static Label MakeInfoLine(List<string> fields, Color? modulate = null, int fontSize = 0)
        => MakeTextLine(string.Join("　｜　", fields), modulate, fontSize);

    /// <summary>信息面板单行控件：横向填满剩余宽度、垂直居中，可按行指定颜色/字号。</summary>
    private static Label MakeTextLine(string text, Color? modulate = null, int fontSize = 0)
    {
        var lb = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        lb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        lb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        lb.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        if (modulate.HasValue) lb.Modulate = modulate.Value;
        if (fontSize > 0) lb.AddThemeFontSizeOverride("font_size", fontSize);
        return lb;
    }

    private static string CardNameText(PlayerCardData data, string path)
    {
        var name = data?.CardName;
        return string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name.Trim();
    }

    private static string UidText(PlayerCardData data, string path)
    {
        var uid = data?.UID;
        return string.IsNullOrWhiteSpace(uid) ? System.IO.Path.GetFileNameWithoutExtension(path) : uid.Trim();
    }

    private static bool HasUniqueField(PlayerCardData data)
        => data != null && !string.IsNullOrWhiteSpace(data.UniqueField);

    /// <summary>费用：-1 表示该卡不可直接打出。</summary>
    private static string CostText(PlayerCardData data)
        => data == null ? "—" : (data.Cost < 0 ? "—（不可打出）" : data.Cost.ToString());

    /// <summary>颜色（恒显示；无有效值按默认棕色/中立兜底）。</summary>
    private static string ColorsText(PlayerCardData data)
    {
        var parts = new List<string>();
        if (data?.Colors != null)
            foreach (var c in data.Colors)
            {
                if (!System.Enum.IsDefined(typeof(CardColor), c)) continue;   // 过滤脏数据（如哨兵值）
                var cn = ColorCn.TryGetValue(c, out var v) ? v : c.ToString();
                if (!parts.Contains(cn)) parts.Add(cn);
            }
        return parts.Count > 0 ? string.Join("、", parts) : "棕（中立）";
    }

    /// <summary>魔法领域；返回空串表示该卡无魔法领域，调用方据此整字段隐藏。</summary>
    private static string DomainsText(PlayerCardData data)
    {
        var parts = new List<string>();
        if (data?.MagicDomains != null)
            foreach (var d in data.MagicDomains)
            {
                // 注意：无领域的卡在 .tres 里可能被写成 -1 哨兵值，必须按合法枚举过滤，否则会显示成 "-1"
                if (!System.Enum.IsDefined(typeof(MagicDomain), d)) continue;
                var cn = DomainCn.TryGetValue(d, out var v) ? v : d.ToString();
                if (!parts.Contains(cn)) parts.Add(cn);
            }
        return string.Join("、", parts);
    }

    /// <summary>类别（按枚举定义顺序展开位掩码；跳过复合组合，避免同一身份重复列举）。</summary>
    private static string CategoryText(PlayerCardData data)
    {
        if (data == null) return "未知";
        var flag = data.CategoryFlag;
        if (flag == CardCategory.None) return "无";

        var parts = new List<string>();
        foreach (CardCategory v in System.Enum.GetValues(typeof(CardCategory)))
        {
            if (v == CardCategory.None) continue;
            if ((((int)v) & (((int)v) - 1)) != 0) continue;   // 单一位才能算"基础类别"，复合组合（如角色/附属）跳过
            if ((flag & v) == 0) continue;
            parts.Add(CategoryCn.TryGetValue(v, out var cn) ? cn : v.ToString());
        }
        return parts.Count > 0 ? string.Join("、", parts) : flag.ToString();
    }

    /// <summary>组织；返回空串表示该卡无组织，调用方据此整字段隐藏。</summary>
    private static string OrganizationText(PlayerCardData data)
    {
        var org = data?.Organization;
        return string.IsNullOrWhiteSpace(org) ? "" : org.Trim();
    }

    /// <summary>子类别；返回空串表示无子类别，调用方据此整字段隐藏。</summary>
    private static string SubCategoriesText(PlayerCardData data)
    {
        var parts = new List<string>();
        if (data?.SubCategories != null)
            foreach (var s in data.SubCategories)
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
        return string.Join("、", parts);
    }

    /// <summary>
    /// 能力项，固定顺序：调查 → 战斗 → 势力 → 先手调查 → 先手战斗 → 先手势力 → 防御。
    /// 数值为 0 的项不产出（防御 <c>&lt;=0</c> 视为无防御），其余项相对顺序不变。
    /// </summary>
    private static List<string> AbilityFields(PlayerCardData data)
    {
        var res = new List<string>();
        if (data == null) return res;

        AddAbility(res, "调查", data.Investigation);
        AddAbility(res, "战斗", data.Strength);
        AddAbility(res, "势力", data.Power);
        AddAbility(res, "先手调查", data.FirstStrikeInvestigation);
        AddAbility(res, "先手战斗", data.FirstStrikeStrength);
        AddAbility(res, "先手势力", data.FirstStrikePower);
        if (data.Defense > 0) res.Add($"防御 {data.Defense}");   // Defense<0＝无防御能力（卡表哨兵值）
        return res;
    }

    private static void AddAbility(List<string> sink, string name, int value)
    {
        if (value == 0) return;   // 零值能力项不显示
        sink.Add($"{name} {value}");
    }

    /// <summary>颜色枚举 → 中文名。</summary>
    private static readonly Dictionary<CardColor, string> ColorCn = new Dictionary<CardColor, string>
    {
        { CardColor.Brown, "棕" }, { CardColor.Yellow, "黄" }, { CardColor.White, "白" },
        { CardColor.Green, "绿" }, { CardColor.Gray, "灰" }, { CardColor.Purple, "紫" },
        { CardColor.Blue, "蓝" }, { CardColor.Black, "黑" }, { CardColor.Red, "红" },
    };

    /// <summary>领域枚举 → 中文名。</summary>
    private static readonly Dictionary<MagicDomain, string> DomainCn = new Dictionary<MagicDomain, string>
    {
        { MagicDomain.Mind, "心灵" }, { MagicDomain.Divine, "神圣" }, { MagicDomain.Star, "星空" },
        { MagicDomain.Death, "死亡" }, { MagicDomain.Blood, "鲜血" },
    };

    /// <summary>类别枚举（单一身份位）→ 中文名。</summary>
    private static readonly Dictionary<CardCategory, string> CategoryCn = new Dictionary<CardCategory, string>
    {
        { CardCategory.Character, "角色" }, { CardCategory.Sorcery, "事务" }, { CardCategory.Attachment, "附属" },
        { CardCategory.Quest, "任务" }, { CardCategory.Land, "地区" }, { CardCategory.Token, "指示物" },
    };

    /// <summary>统一卡图缩略控件（100x140，按宽度适配），卡池 / 秘社候选 / 卡组列表共用。</summary>
    private static TextureRect MakeCardThumb(Texture2D tex)
    {
        var tr = new TextureRect { Texture = tex };
        tr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        tr.ExpandMode = (TextureRect.ExpandModeEnum)1; // FitWidth：纹理按节点宽度缩放，否则按原始像素绘制导致过大
        tr.CustomMinimumSize = new Vector2(100, 140);
        tr.Size = new Vector2(100, 140);
        tr.SizeFlagsHorizontal = 0; // 不水平扩展填充，避免被容器拉伸
        return tr;
    }

    /// <summary>秘社卡图路径：优先 CouncilCardData.CardImagePath，缺失时回落到 UID 推断。</summary>
    private static string ResolveCouncilImagePath(CouncilCardData data)
    {
        if (data == null) return "";
        return string.IsNullOrEmpty(data.CardImagePath) ? CardUI.ResolveCardImagePath(data.UID) : data.CardImagePath;
    }

    /// <summary>
    /// 【秘社添加】把秘社放进卡组的**唯一入口**：卡组只保留一张秘社。
    /// 若已有秘社则直接覆盖（不叠加、不报错、不产生重复项），并同步底部下拉框与限制进度。
    /// </summary>
    private void SetCouncil(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        bool replaced = !string.IsNullOrEmpty(_workingCouncil) && _workingCouncil != path;
        _workingCouncil = path;

        int ci = _councilPaths.FindIndex(p => p == path);
        if (ci >= 0) _councilOption.Select(ci);   // 同步底部"秘社"下拉框（Select 不触发 ItemSelected，无重入风险）

        RefreshDeckView();                        // 卡组侧展示 + 重算构筑限制
        if (_councilMode) RefreshPoolView();      // 刷新"当前秘社"标记
        GD.Print($"【DeckBuilder】{(replaced ? "覆盖" : "添加")}秘社：{System.IO.Path.GetFileNameWithoutExtension(path)}");
    }

    /// <summary>移除卡组中的秘社（可再次从秘社候选里选择添加）。</summary>
    private void ClearCouncil()
    {
        if (string.IsNullOrEmpty(_workingCouncil)) return;
        _workingCouncil = "";
        RefreshDeckView();
        if (_councilMode) RefreshPoolView();
        GD.Print("【DeckBuilder】已移除卡组秘社");
    }

    private void AddCard(string path)
    {
        if (_workingCards.ContainsKey(path))
            _workingCards[path]++;
        else
            _workingCards[path] = 1;
        RefreshDeckView();
        GD.Print($"【DeckBuilder】加入卡 {System.IO.Path.GetFileNameWithoutExtension(path)}（共 {_workingCards[path]}）");
    }

    private void RemoveCard(string path)
    {
        if (!_workingCards.ContainsKey(path)) return;
        _workingCards[path]--;
        if (_workingCards[path] <= 0) _workingCards.Remove(path);
        RefreshDeckView();
    }

    // ============================================================
    // 当前卡组渲染（带份数 +/-）
    // ============================================================

    /// <summary>
    /// 卡组列表顶部的秘社行（独立于 <see cref="_workingCards"/>，不计入卡牌总数）。
    /// 只有一张，也无份数概念；"-" 用于移除以便重新选择。
    /// </summary>
    private void BuildDeckCouncilRow()
    {
        if (string.IsNullOrEmpty(_workingCouncil)) return;

        var data = ResourceLoader.Load<CouncilCardData>(_workingCouncil);
        string imgPath = ResolveCouncilImagePath(data);
        var tex = string.IsNullOrEmpty(imgPath) ? null : ResourceLoader.Load<Texture2D>(imgPath);
        if (tex == null) GD.PrintErr($"【DeckBuilder】卡组秘社卡图加载失败: {_workingCouncil}");

        var row = new HBoxContainer();
        row.AddChild(MakeCardThumb(tex));

        var name = new Label
        {
            Text = "【秘社】" + (string.IsNullOrEmpty(data?.CardName)
                ? System.IO.Path.GetFileNameWithoutExtension(_workingCouncil)
                : data.CardName),
            CustomMinimumSize = new Vector2(40, 140),
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.Modulate = new Color(0.55f, 0.95f, 0.55f);
        row.AddChild(name);

        var minus = new Button { Text = "-", CustomMinimumSize = new Vector2(28, 140) };
        minus.Pressed += ClearCouncil;
        row.AddChild(minus);

        _deckList.AddChild(row);
    }

    private void RefreshDeckView()
    {
        foreach (var child in _deckList.GetChildren())
            child.QueueFree();

        BuildDeckCouncilRow();

        int total = 0;
        int deckFail = 0;
        foreach (var kv in _workingCards)
        {
            total += kv.Value;
            var row = new HBoxContainer();

            // 轻量卡图（不实例化 CardUI）
            var data = ResourceLoader.Load<PlayerCardData>(kv.Key);
            var tex = data != null ? ResourceLoader.Load<Texture2D>(CardUI.ResolveCardImagePath(data.UID)) : null;
            var tr = new TextureRect();
            tr.Texture = tex;
            tr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
            tr.ExpandMode = (TextureRect.ExpandModeEnum)1; // FitWidth：纹理按节点宽度缩放，否则按原始像素绘制导致过大
            tr.CustomMinimumSize = new Vector2(100, 140);
            tr.Size = new Vector2(100, 140);
            tr.SizeFlagsHorizontal = 0;
            if (tex == null) { deckFail++; GD.PrintErr($"【DeckBuilder】卡组卡图加载失败: {kv.Key}"); }
            row.AddChild(tr);

            var count = new Label { Text = $"x{kv.Value}", CustomMinimumSize = new Vector2(40, 140), VerticalAlignment = VerticalAlignment.Center };
            var minus = new Button { Text = "-", CustomMinimumSize = new Vector2(28, 140) };
            var plus = new Button { Text = "+", CustomMinimumSize = new Vector2(28, 140) };
            minus.Pressed += () => RemoveCard(kv.Key);
            plus.Pressed += () => AddCard(kv.Key);
            row.AddChild(count);
            row.AddChild(minus);
            row.AddChild(plus);

            _deckList.AddChild(row);
        }

        _countLabel.Text = $"{total} 张";
        GD.Print($"【DeckBuilder】刷新卡组视图：共 {total} 张 / 加载失败 {deckFail} 张 / 行数 {_deckList.GetChildCount()}");
        if (string.IsNullOrEmpty(_nameEdit.Text))
            _nameEdit.PlaceholderText = $"卡组{System.DateTime.Now:HHmmss}";

        // 【秘社批次】每次卡组变动都重算构筑限制进度（展示 + 为保存拦截准备）
        RefreshRestrictionView();
    }

    // ============================================================
    // 加载 / 保存 / 返回
    // ============================================================
    private void OnLoadSelected(long index)
    {
        int i = (int)index;
        if (i <= 0)
        {
            _workingCards.Clear();
            _nameEdit.Text = "";
            RefreshDeckView();
            return;
        }
        string fileName = _loadOption.GetItemText(i);
        LoadDeckFromFile(DecksDir + "/" + fileName);
    }

    private void LoadDeckFromFile(string path)
    {
        var deck = DeckListLoader.Load(path);
        if (deck == null) return;
        _workingCards.Clear();
        foreach (var e in deck.Cards) _workingCards[e.TemplatePath] = e.Copies;
        _nameEdit.Text = deck.DeckName;
        if (deck.Councils.Count > 0) _workingCouncil = deck.Councils[0].TemplatePath;
        int ci = _councilPaths.FindIndex(p => p == _workingCouncil);
        if (ci >= 0) _councilOption.Select(ci);
        RefreshDeckView();
        GD.Print($"【DeckBuilder】已载入卡组：{path}");
    }

    private void OnCouncilSelected(long index)
    {
        int i = (int)index;
        // 无论从下拉框还是从秘社候选列表选择，都走同一个入口，保证只有一张秘社且状态一致
        if (i >= 0 && i < _councilPaths.Count)
            SetCouncil(_councilPaths[i]);
    }

    // ============================================================
    // 【秘社批次】构筑限制：展示 + 保存拦截
    // ============================================================
    /// <summary>运行期创建"秘社限制"面板（挂在 DeckPanel 顶部，不影响既有场景节点）。</summary>
    private void BuildRestrictionPanel()
    {
        var deckPanel = GetNodeOrNull<Control>("DeckPanel");
        if (deckPanel == null) return;

        _restrictionBox = new VBoxContainer { Name = "RestrictionBox" };
        _restrictionBox.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _restrictionBox.OffsetLeft = 8;
        _restrictionBox.OffsetRight = -8;
        _restrictionBox.OffsetTop = 4;
        _restrictionBox.OffsetBottom = 4;
        _restrictionBox.AddThemeConstantOverride("separation", 2);
        deckPanel.AddChild(_restrictionBox);

        _restrictionSummary = new Label { Name = "RestrictionSummary", Text = "" };
        _restrictionBox.AddChild(_restrictionSummary);

        // 给 DeckScroll 留出顶部空间，避免被限制面板压住。
        var scroll = deckPanel.GetNodeOrNull<Control>("DeckScroll");
        if (scroll != null) scroll.OffsetTop = 56;
    }

    /// <summary>
    /// 重算并渲染当前秘社的构筑限制进度。逐条显示 `Describe(actual)`；满足=绿色、不满足=红色。
    /// 返回未满足的明细列表（空＝全部满足）——`OnSave` 用它做保存拦截。
    /// </summary>
    private List<(string text, bool ok, int actual, int required)> RefreshRestrictionView()
    {
        var result = new List<(string, bool, int, int)>();
        if (_restrictionBox == null) return result;

        foreach (var child in _restrictionBox.GetChildren())
            if (child != _restrictionSummary) child.QueueFree();

        var council = string.IsNullOrEmpty(_workingCouncil)
            ? null
            : ResourceLoader.Load<CouncilCardData>(_workingCouncil);
        if (council == null || !council.HasRestrictions)
        {
            _restrictionSummary.Text = "秘社限制：无（该秘社未声明构筑限制）";
            _restrictionSummary.Modulate = Colors.White;
            return result;
        }

        // 展开当前编辑中的卡组（按份数展开；构筑限制按**实际张数**统计，必须展开）
        var expanded = new List<PlayerCardData>();
        foreach (var kv in _workingCards)
        {
            var data = ResourceLoader.Load<PlayerCardData>(kv.Key);
            if (data == null) continue;
            for (int c = 0; c < kv.Value; c++) expanded.Add(data);
        }

        bool allOk = true;
        foreach (var r in council.Restrictions)
        {
            if (r == null) continue;
            int actual = r.GetActualCount(expanded);
            bool ok = r.IsSatisfiedBy(expanded);
            if (!ok) allOk = false;
            result.Add((r.Describe(actual), ok, actual, r.RequiredCount));

            var line = new Label { Text = (ok ? "✔ " : "✘ ") + r.Describe(actual) };
            line.Modulate = ok ? new Color(0.55f, 0.95f, 0.55f) : new Color(1f, 0.5f, 0.5f);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _restrictionBox.AddChild(line);
        }

        _restrictionSummary.Text = $"秘社「{council.CardName}」限制：{(allOk ? "已达成" : "未达成（无法保存）")}";
        _restrictionSummary.Modulate = allOk ? new Color(0.55f, 0.95f, 0.55f) : new Color(1f, 0.6f, 0.4f);
        return result;
    }

    private void OnSave()
    {
        string name = string.IsNullOrWhiteSpace(_nameEdit.Text) ? $"卡组{System.DateTime.Now:HHmmss}" : _nameEdit.Text.Trim();

        // 【秘社批次·保存拦截】不满足当前秘社的构筑限制时**拒绝保存**，并把逐条明细打出来。
        var failures = RefreshRestrictionView();
        var bad = failures.FindAll(f => !f.ok);
        if (bad.Count > 0)
        {
            var detail = string.Join("；", bad.ConvertAll(f => f.text));
            GD.PrintErr($"【DeckBuilder】⛔ 卡组不符合秘社构筑限制，已拦截保存：{detail}");
            ShowMessage($"不符合秘社构筑限制，无法保存：\n{detail}");
            return;
        }

        var deck = new DeckList
        {
            DeckName = name,
        };
        foreach (var kv in _workingCards)
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = kv.Key, Copies = kv.Value });
        // 秘社单独成列（不在 Cards 里，避免被当作可抽取的卡牌）；未选择秘社则不写
        if (!string.IsNullOrEmpty(_workingCouncil))
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = _workingCouncil, Copies = 1 });

        string path = DecksDir + "/" + SanitizeFileName(name) + ".json";
        DeckListLoader.Save(deck, path);
        GameManager.Instance.SelectedDeckPath = path;
        GameManager.Instance.UseSelectedDeck = true;
        GD.Print($"【DeckBuilder】卡组已保存：{path}");
    }

    private void OnBack()
    {
        GameManager.Instance.GoToState(GameManager.GameState.MainMenu);
    }

    /// <summary>弹一个轻量提示窗（构筑限制拦截等场景用；不依赖场景节点，运行期创建）。</summary>
    private void ShowMessage(string message)
    {
        var dlg = new AcceptDialog
        {
            Title = "卡组校验",
            DialogText = message,
            Exclusive = true,
            Unresizable = false,
        };
        AddChild(dlg);
        dlg.PopupCentered(new Vector2I(560, 220));
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        dlg.CloseRequested += () => dlg.QueueFree();
    }

    private static string SanitizeFileName(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
