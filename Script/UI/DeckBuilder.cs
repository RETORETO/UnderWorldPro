using Godot;
using System.Collections.Generic;
using Script.Data.Cards;
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

    // 分页
    private const int PageSize = 80;
    private int _poolPage = 0;

    // 缓存：避免重复加载纹理（路径 -> Texture2D）
    private Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

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
        _prevPage.Pressed += () => { if (_poolPage > 0) { _poolPage--; RefreshPoolView(); } };
        _nextPage.Pressed += () => { if ((_poolPage + 1) * PageSize < _poolPaths.Count) { _poolPage++; RefreshPoolView(); } };

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
        ScanDirRecursive(PlayerCardsDir);
        _allPoolPaths.Sort();
        GD.Print($"【DeckBuilder】扫描到卡池 {_allPoolPaths.Count} 张");
        ApplyPoolFilter();
    }

    private void ScanDirRecursive(string dirPath)
    {
        using var dir = DirAccess.Open(dirPath);
        if (dir == null) return;
        dir.ListDirBegin();
        string name = dir.GetNext();
        while (!string.IsNullOrEmpty(name))
        {
            string full = dirPath + "/" + name;
            if (dir.CurrentIsDir())
                ScanDirRecursive(full);
            else if (name.EndsWith(".tres", System.StringComparison.OrdinalIgnoreCase))
                _allPoolPaths.Add(full);
            name = dir.GetNext();
        }
        dir.ListDirEnd();
    }

    // 根据搜索关键字过滤卡池（按文件路径中的卡号片段匹配，不区分大小写）
    private void ApplyPoolFilter()
    {
        _poolPaths.Clear();
        if (string.IsNullOrWhiteSpace(_poolFilter))
        {
            _poolPaths.AddRange(_allPoolPaths);
        }
        else
        {
            string f = _poolFilter.Trim().ToLowerInvariant();
            foreach (var p in _allPoolPaths)
            {
                string file = System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                if (file.Contains(f))
                    _poolPaths.Add(p);
            }
        }
        _poolPage = 0;   // 过滤后回到第一页
    }

    private Timer _searchTimer;

    private void OnSearchTextChanged(string newText)
    {
        _poolFilter = newText;
        // 防抖：停止输入 250ms 后才真正过滤+刷新，避免每字符全量重载导致卡死/闪退
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ScanCouncils()
    {
        _councilPaths.Clear();
        using var dir = DirAccess.Open(CouncilCardsDir);
        if (dir == null) return;
        dir.ListDirBegin();
        string name = dir.GetNext();
        while (!string.IsNullOrEmpty(name))
        {
            if (!dir.CurrentIsDir() && name.EndsWith(".tres", System.StringComparison.OrdinalIgnoreCase))
                _councilPaths.Add(CouncilCardsDir + "/" + name);
            name = dir.GetNext();
        }
        dir.ListDirEnd();
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
            var tr = new TextureRect();
            tr.Texture = tex;
            tr.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
            tr.ExpandMode = (TextureRect.ExpandModeEnum)1; // FitWidth：纹理按节点宽度缩放，否则按原始像素绘制导致过大
            tr.CustomMinimumSize = new Vector2(100, 140);
            tr.Size = new Vector2(100, 140);
            tr.SizeFlagsHorizontal = 0; // 不水平扩展填充，避免被容器拉伸
            if (tex != null) ok++; else { fail++; GD.PrintErr($"【DeckBuilder】卡图加载失败: {data?.UID} ({path})"); }
            // 整卡可点击加入
            tr.GuiInput += (InputEvent e) =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    AddCard(path);
            };
            _poolList.AddChild(tr);
        }
        GD.Print($"【DeckBuilder】刷新卡池（第 {_poolPage + 1} 页）完成：本页 {ok} 张 / 失败 {fail} 张，子节点数 {_poolList.GetChildCount()} / 总计 {total}");
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
    private void RefreshDeckView()
    {
        foreach (var child in _deckList.GetChildren())
            child.QueueFree();

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
        if (i >= 0 && i < _councilPaths.Count)
            _workingCouncil = _councilPaths[i];
    }

    private void OnSave()
    {
        string name = string.IsNullOrWhiteSpace(_nameEdit.Text) ? $"卡组{System.DateTime.Now:HHmmss}" : _nameEdit.Text.Trim();
        var deck = new DeckList
        {
            DeckName = name,
        };
        foreach (var kv in _workingCards)
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = kv.Key, Copies = kv.Value });
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

    private static string SanitizeFileName(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
