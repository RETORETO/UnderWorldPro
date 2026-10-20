using Godot;
using System.Collections.Generic;
using Script.Data.Cards;
using Script.Enums;
using Script.IO;
using Script.UI;

public partial class DeckBuilder : Control
{
    private VBoxContainer _poolList;
    private LineEdit _nameEdit;
    private Label _countLabel;
    /// <summary>【2026-10-17】卡组构成统计条（颜色数量 + 费用数量），显示在底栏。</summary>
    private Label _statsLabel;
    // 【2026-10-17 · 卡组名 + 载入合并】原来"顶部下拉框选卡组"与"底栏输入卡组名"是两处控件；
    // 现在合并为一条「输入框 + 右侧 ▾ 下拉」：选卡组文件即打开并回填卡组名，保存时按卡组名覆盖同名文件。
    private Button _deckLoadButton;
    private PanelContainer _deckLoadPanel;
    private VBoxContainer _deckLoadList;
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

    // 【秘社批次】构筑限制展示与违规校验（"当前 X 张 / 需要 ≥N 张"）
    private VBoxContainer _restrictionBox;
    private Label _restrictionSummary;

    // ============================================================
    // 【2026-10-17 · 卡组框重做】卡组不再用 VBoxContainer 逐行排列，改成**固定尺寸的列式卡组板**：
    //   DeckPanel（固定锚点 = 卡组框尺寸固定）
    //     ├ InfoPanel   ← 秘社条 + 构筑限制（**框外**，需求 11 保证内容量不减）
    //     ├ SortBar     ← 排序优先级下拉（框外）
    //     ├ DeckScroll  ← 卡组框本体（ScrollContainer，竖向不滚、内建横条隐藏）
    //     │   └ DeckBoard ← 运行期绝对定位所有"同名组"节点
    //     └ DeckHBar    ← 框外底部的横向滑动条（仅内容横向溢出时显示）
    // 列高 = DeckScroll 的可视高度（运行期量取），故列高天然等于"卡组框可视高度"。
    // ============================================================
    private ScrollContainer _deckScroll;
    private Control _deckBoard;
    private HScrollBar _deckHBar;
    private Button _sortModeButton;
    private CardHoverPreview _hoverPreview;
    private DeckBoardLayout.DeckSortMode _sortMode = DeckBoardLayout.DeckSortMode.CostFirst;
    /// <summary>列高（= 卡组框可视高度），首帧后由 <see cref="RecomputeDeckLayout"/> 量取。</summary>
    private float _columnH;
    private bool _boardBuilt;
    /// <summary>外置横滑条 ↔ 内建横滑条 同步时的重入保护。</summary>
    private bool _syncingHBar;
    private int _layoutRetry;
    /// <summary>当前被悬浮的组的节点（null = 没有）。</summary>
    private Control _hoverGroupNode;
    /// <summary>组节点 ↔ 组数据（悬浮命中用；顺序 = 绘制顺序）。</summary>
    private readonly List<(DeckBoardLayout.DeckBoardGroup Group, Control Node)> _groupNodes
        = new List<(DeckBoardLayout.DeckBoardGroup, Control)>();
    /// <summary>模板路径 → 卡模板数据（供悬浮展示，避免每次悬浮都重新 Load）。</summary>
    private readonly Dictionary<string, PlayerCardData> _boardData = new Dictionary<string, PlayerCardData>();

    /// <summary>卡组板：列宽（卡宽 + 列间空隙）。</summary>
    private const float DeckColumnW = CardLayoutHelper.CARD_WIDTH + 10f;
    /// <summary>卡组板：初始堆叠错位步长 = 上方卡露出的高度（正好压住卡图顶部标题栏的卡名）。</summary>
    private const float DeckOffset0 = 28f;
    /// <summary>卡组板：异名组之间的纵向空隙（保证异名组不重叠）。</summary>
    private const float DeckGroupGap = 8f;
    /// <summary>卡组板：同名组张数上限（超出部分另起一组）。</summary>
    private const int DeckMaxPerGroup = 50;

    /// <summary>卡组合法所需的最少卡牌张数（不含秘社卡，按份数累计）。不足则视为违规卡组。</summary>
    private const int MinDeckSize = 50;

    // 分页
    /// <summary>
    /// 单页行数（默认值）。**可被命令行开关 `--wp-pagesize=N` 覆盖**（见 Script/Web/WebPerfParams.cs），
    /// 用于不重导出就做「行数 ↔ 耗时是否线性」的性能对照实验（E1）。
    /// 单条结果是带五行数据的信息面板（约 10 个控件 + 5 个 Label），比原先的一张卡图重得多。
    ///
    /// 【B1：40 → 12，2026-10-11】原来 40 行/页：一屏实际只看得到约 5 行（行高≈148px），
    /// 也就是说 35/40 的行是白建的 —— 而每行要建约 10 个控件并参与布局，
    /// 一页合计约 400 个控件。Web（wasm）上这份同步建节点的开销足以把界面卡住十几秒，
    /// 正是「点编辑卡组几乎无法下一步」的主因之一。改成 12（≈2 屏多）后同步成本降到约 30%。
    /// </summary>
    private const int DefaultPageSize = 12;
    private static int PageSize => WebPerfParams.PageSizeOverride > 0 ? WebPerfParams.PageSizeOverride : DefaultPageSize;
    private int _poolPage = 0;

    // 缓存：避免重复加载纹理（路径 -> Texture2D）
    private Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

    /// <summary>卡背占位图（渐进填充期间先贴它，避免行先空着）。懒加载，只取一次。</summary>
    private Texture2D _cardBackTex;
    private bool _cardBackTried;

    /// <summary>
    /// 卡图渐进填充的批次号：每次刷新卡池/卡组都自增，旧批次在下一个 await 处自行放弃，
    /// 避免「翻页后上一页的填充还在往已销毁的节点上贴图」。
    /// </summary>
    private int _fillToken;

    // 搜索结果面板的外观资源（所有行共用一份，避免每建一行就new一份主题资源）
    private StyleBoxFlat _poolRowStyle;

    // 编辑态
    private Dictionary<string, int> _workingCards = new Dictionary<string, int>();   // 模板路径 -> 份数
    private string _workingCouncil = "res://Cards/Data/CouncilCards/JC/MSJC009_mishe.tres";

    // 卡池（模板路径列表，用于渲染）
    private List<string> _poolPaths = new List<string>();          // 过滤后用于渲染
    private List<string> _allPoolPaths = new List<string>();        // 全量，供过滤（索引不可用时的回退源）
    private string _poolFilter = "";                                // 搜索框过滤关键字
    // 秘社候选
    private List<string> _councilPaths = new List<string>();

    // 【卡池结构化检索 · 2026-10-17】索引过滤引擎与可用性标志。
    // 索引可用时：`_filter` 里的结构化条件走索引过滤（见 ApplyPoolFilter）；
    // 索引缺失/损坏：回退到改造前的"按文件名子串"逻辑（`_allPoolPaths`）。
    private DeckFilterModel _filter = new DeckFilterModel();
    private bool _indexReady;
    /// <summary>是否已尝试加载索引（含失败）。失败后不再重试，避免每次搜索都付出一次失败开销。</summary>
    private bool _indexTried;

    private const string PlayerCardsDir = "res://Cards/Data/PlayerCards";
    private const string CouncilCardsDir = "res://Cards/Data/CouncilCards";
    private const string DecksDir = "res://Decks";

    public override void _Ready()
    {
        // 【2026-10-12 清理说明】这里原来插着几十行"逐步定位标记 m1..m19"和一批实验开关
        //（--wp-min / --wp-hide / --wp-solo / --wp-norules / --wp-gtest / --wp-addscene …）。
        // 崩点已彻底查清：**Web 上从托管代码连接 `RenderingServer.FramePostDraw` 会让 wasm abort**
        //（`--wp-hooks=post` 复现；只连 `FramePreDraw`/`SceneTree.ProcessFrame` 均正常），
        // 与 DeckBuilder 的内容毫无关系 —— 之前 `--wp-min`、`--wp-hide=all`、
        // 甚至只留 0 个子节点都照样崩，就是因为探针一直挂着。
        // 诊断标记与实验开关已全部删除，只保留 `[PERF]` 阶段计时（由 --wp-perf 控制）。
        ulong __tStart = Time.GetTicksMsec();

        _poolList = GetNode<VBoxContainer>("PoolPanel/PoolScroll/PoolList");
        _deckScroll = GetNode<ScrollContainer>("DeckPanel/DeckScroll");
        _deckBoard = GetNode<Control>("DeckPanel/DeckScroll/DeckBoard");
        _nameEdit = GetNode<LineEdit>("DeckPanel/DeckBar/NameEdit");
        _countLabel = GetNode<Label>("BottomBar/CountLabel");
        _statsLabel = GetNode<Label>("BottomBar/StatsLabel");
        _deckLoadButton = GetNode<Button>("DeckPanel/DeckBar/LoadBtn");
        _deckLoadPanel = GetNode<PanelContainer>("DeckPanel/DeckLoadPanel");
        _deckLoadList = GetNode<VBoxContainer>("DeckPanel/DeckLoadPanel/DeckLoadScroll/DeckLoadList");
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

        // 【卡池结构化检索 · 2026-10-17】高级筛选面板（同样运行期创建）
        BuildFilterPanel();
        // 量布局用：`--wp-filter=on` 时自动展开（等价于点一下「高级筛选」）
        if (WebPerfParams.AutoExpandFilter && _filterToggleButton != null)
            _filterToggleButton.ButtonPressed = true;

        // 【2026-10-17 卡组框重做】卡组板的滚动模式 / 框外横滑条 / 排序下拉 / 本场景悬浮浮层
        BuildDeckBoardChrome();

        // 搜索防抖计时器
        _searchTimer = new Timer();
        _searchTimer.WaitTime = 0.25f;
        _searchTimer.OneShot = true;
        AddChild(_searchTimer);
        _searchTimer.Timeout += () => { ApplyPoolFilter(); RefreshPoolView(); };

        // 【2026-10-17】保存按钮已挪到卡组名输入框右侧（DeckPanel/DeckBar）
        GetNode<Button>("DeckPanel/DeckBar/SaveButton").Pressed += OnSave;
        GetNode<Button>("BottomBar/BackButton").Pressed += OnBack;
        // 【2026-10-17】载入卡组改为"卡组名右侧的 ▾ 下拉"（与输入框合并）
        _deckLoadButton.Pressed += ToggleDeckLoadPanel;
        _deckLoadPanel.Visible = false;
        // 下拉外观与卡池卡片行统一（深底 + 细边框），"以搜索框为准"的一套视觉语言
        _deckLoadPanel.AddThemeStyleboxOverride("panel", GetPoolRowStyle());
        SetProcessInput(true);   // 用于"点下拉面板之外时自动收起"

        // 【2026-10-20 背景】卡组编辑界面本身是视觉基准，也补一层场景底图（压暗 0.80，几乎只做质感，
        // 保证复杂表格/卡池行的高对比可读性不被破坏）。
        ApplyBackdrop();

        ulong __tBind = Time.GetTicksMsec();
        // 整段套 try/catch：Web 上「托管异常跨原生边界」会直接 abort、看不到内容；
        // 捕获后打印 ex 就能拿到异常类型/消息/托管栈，便于以后定位。
        try
        {
            ScanPool();
            ulong __tScanPool = Time.GetTicksMsec();
            ScanCouncils();
            ulong __tScanCouncil = Time.GetTicksMsec();
            RefreshDeckLoadList();
            ulong __tLoadOpts = Time.GetTicksMsec();
            RefreshPoolView();
            ulong __tPool = Time.GetTicksMsec();

            WebPerfParams.Summary(
                $"deck._Ready scanPool={__tScanPool - __tBind}ms scanCouncil={__tScanCouncil - __tScanPool}ms "
                + $"loadOpts={__tLoadOpts - __tScanCouncil}ms refreshPool={__tPool - __tLoadOpts}ms "
                + $"(" + PageSize + $" 行/页) readyTotal={__tPool - __tStart}ms");

            // 进入编辑页显示哪个卡组：
            //   ① 显式带入的编辑目标（房间/主菜单）优先；
            //   ② 否则载入"上次使用的卡组" —— 与房间页的默认卡组**同一逻辑**
            //      （DeckSelection.ResolveDefault：偏好记录 > default_deck.json > 目录第一个）。
            // 注意：这里**只读不写**。"打开看一眼编辑页"不应该被记成一次选择，否则默认卡组会莫名漂移。
            string editPath = GameManager.Instance.EditingDeckPath;
            if (string.IsNullOrEmpty(editPath)) editPath = DeckSelection.ResolveDefault();

            if (!string.IsNullOrEmpty(editPath) && FileAccess.FileExists(editPath))
            {
                LoadDeckFromFile(editPath);
                GD.Print($"【DeckBuilder】进入编辑页：载入默认卡组 {editPath}");
            }
            else
            {
                RefreshDeckView();
            }

            WebPerfParams.Summary($"deck._Ready done total={Time.GetTicksMsec() - __tStart}ms");
            StartFirstFrameProbe();
            // 首帧后布局已就绪：按像素排布高级筛选区并把 PoolScroll 顶边钉到位
            Callable.From(ReflowPoolTop).CallDeferred();
            // 【2026-10-17 卡组框重做】列高 = 卡组框可视高度，必须等首帧布局完成才能量到真像素
            Callable.From(RecomputeDeckLayout).CallDeferred();
            if (GetViewport() != null) GetViewport().SizeChanged += OnViewportResized;
            // 【2026-10-17】索引加载实测约 170ms（System.Text.Json 首次反序列化偏慢），
            // 放在 _Ready 里会白白拖慢"进卡组编辑"。改为进屏 0.4s 后空闲预热：
            // 用户点开「高级筛选」再点「搜索」时索引早已就绪，首次结构化搜索无需等待。
            StartIndexWarmup();
        }
        catch (System.Exception __ex)
        {
            // 若这行能打出来，说明是「托管异常」而不是原生 abort —— 异常全文就是根因。
            GD.PrintErr("【DeckBuilder】_Ready 抛出托管异常");
            GD.PrintErr("  类型: " + __ex.GetType().FullName);
            GD.PrintErr("  消息: " + __ex.Message);
            GD.PrintErr("  托管栈: " + __ex.StackTrace);
        }
    }

    // ============================================================
    // 【性能埋点】首帧探针 —— 字形栅格化 / 首帧 GPU 上传都发生在**渲染阶段**，
    // 纯 C# 计时器抓不到；用渲染帧信号打时间戳才能量出来。定位完成后可整段删除。
    // ============================================================
    private ulong _probeStart;
    private int _probeFrames;
    private bool _preDrawSeen;
    private bool _postConnected;

    private void StartFirstFrameProbe()
    {
        if (!WebPerfParams.FrameProbe) return;

        _probeStart = Time.GetTicksMsec();
        _probeFrames = 0;
        _probeStopped = false;
        WebPerfParams.Summary($"probe start (+{_probeStart}ms 引擎 uptime)");

        // 钩子 A：渲染帧信号（含绘制期开销，最能抓到字形栅格化/首次纹理上传）
        // 【2026-10-12 重大发现 —— Web 上禁止连接 FramePostDraw】
        //   A/B 实测（同一份构建，只改 index.html 的 args）：
        //     · `--wp-hooks=post`（只连接 FramePostDraw）→ **首帧立刻 Aborted，心跳 0 条**；
        //     · `--wp-hooks=pre` （只连接 FramePreDraw） → 存活 16s、240fps；
        //     · `--wp-hooks=proc`（只连接 ProcessFrame） → 存活 19s、240fps；
        //     · `--wp-noprobe`   （全不连接）            → 存活 39s、240fps。
        //   ⇒ 唯一致命的是 **RenderingServer.FramePostDraw**；且 `--wp-quietprobe`
        //     （回调体什么都不做）也崩 ⇒ 崩因是"连接该信号"本身，与回调内容无关。
        //   这条曾经让 Web 版一进 DeckBuilder 就 abort（看起来像"卡死、点不动"），
        //   因为 `DevFlags.FrameProbe` 默认 true —— 见 Script/DevFlags.cs。
        //   下面用 OS.HasFeature("web") 做**硬保险**，任何人以后误开开关都不会再崩 Web。
        bool isWeb = OS.HasFeature("web");
        _postConnected = !isWeb;
        if (_postConnected) RenderingServer.Singleton.FramePostDraw += OnProbeFramePostDraw;
        // 钩子 A2：draw 之前一发（Web 上安全）。用来把耗时钉在「逻辑流程」还是「绘制」。
        RenderingServer.Singleton.FramePreDraw += OnProbeFramePreDraw;
        // 钩子 B：逻辑帧信号（抓到布局 / 节点处理耗时）
        GetTree().ProcessFrame += OnProbeProcessFrame;
    }

    private void OnProbeFramePreDraw()
    {
        if (_preDrawSeen) return;
        _preDrawSeen = true;
        WebPerfParams.Summary($"frame_pre_draw +{Time.GetTicksMsec() - _probeStart}ms（首帧绘制已开始）");
        // Web 上没有 post 钩子来收尾（连接它必崩），所以这里自己摘钩子，
        // 否则 ProcessFrame/FramePreDraw 的钩子会一直挂着、每帧刷日志。
        if (!_postConnected) StopFirstFrameProbe();
    }

    private void OnProbeFramePostDraw()
    {
        WebPerfParams.Summary($"frame_post_draw#{_probeFrames} +{Time.GetTicksMsec() - _probeStart}ms "
                            + $"fps={Engine.GetFramesPerSecond()}");
        if (++_probeFrames < 4) return;
        StopFirstFrameProbe();
    }

    private void OnProbeProcessFrame()
    {
        WebPerfParams.Log($"process_frame +{Time.GetTicksMsec() - _probeStart}ms");
    }

    private void StopFirstFrameProbe()
    {
        _probeStopped = true;
        RenderingServer.Singleton.FramePostDraw -= OnProbeFramePostDraw;
        RenderingServer.Singleton.FramePreDraw -= OnProbeFramePreDraw;
        GetTree().ProcessFrame -= OnProbeProcessFrame;
        WebPerfParams.Summary("probe done");
    }

    // ============================================================
    // 【诊断埋点，默认关闭】只由运行时开关 `--wp-probe` 打开（见 DevFlags.FrameProbe 的说明）。
    // 之所以保留而没删：Godot 的日志没有时间戳、CDP 又会回放缓存日志，
    // "页面到底活到了第几秒"只能靠这条每秒心跳来证明。交付版（无 --wp-probe）完全静默。
    // ⚠️ 注意：绝不能在 Web 上连接 RenderingServer.FramePostDraw（会 wasm abort）。
    // ============================================================
    private int _processTicks;
    private ulong _lastBeatMs;
    private bool _probeStopped = true;

    /// <summary>
    /// 【2026-10-17】点在下拉面板与 ▾ 按钮之外时自动收起卡组载入列表（按钮自身的点击交给 Toggle 处理）。
    /// 用 `_Input` 而不是给整个界面挂"点击穿透"监听：本界面控件很多，逐个挂会碎成一地。
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (_deckLoadPanel == null || !_deckLoadPanel.Visible) return;
        if (!(@event is InputEventMouseButton mb) || !mb.Pressed) return;

        Vector2 pos = mb.GlobalPosition;
        if (_deckLoadPanel.GetGlobalRect().HasPoint(pos)) return;
        if (_deckLoadButton != null && _deckLoadButton.GetGlobalRect().HasPoint(pos)) return;
        HideDeckLoadPanel();
    }

    public override void _Process(double delta)
    {
        // 【2026-10-17 卡组框重做】这两项与性能探针无关，必须每帧跑：
        //   ① 卡组板悬浮解析（含悬浮时浮出的 +/- 微按钮）；
        //   ② 框外横滑条跟随内建横滑条（滚轮滚动时同步）。
        UpdateDeckHover();
        SyncDeckHBarFromScroll();

        if (!WebPerfParams.FrameProbe) return;

        _processTicks++;
        ulong now = Time.GetTicksMsec();
        if (_processTicks <= 10)
            WebPerfParams.Log($"deck._Process 第 {_processTicks} 次 +{now - _probeStart}ms");

        if (now - _lastBeatMs >= 1000)
        {
            _lastBeatMs = now;
            GD.Print("[BEAT] deck 存活 ticks=" + Script.IO.WebSafeText.Int(_processTicks)
                   + " fps=" + Script.IO.WebSafeText.Int((int)Engine.GetFramesPerSecond())
                   + " nodes=" + Script.IO.WebSafeText.Int(GetTree().GetNodeCount()));
        }
    }

    public override void _ExitTree()
    {
        if (GetViewport() != null) GetViewport().SizeChanged -= OnViewportResized;
        if (_hoverPreview != null) _hoverPreview.HidePreview();
        // 场景切换时若探针还没摘干净，会在已释放的节点上调回调
        if (!_probeStopped)
        {
            try { StopFirstFrameProbe(); } catch { /* 已随场景释放 */ }
        }
    }

    // ============================================================
    // 扫描卡池 / 秘社
    // ============================================================
    private void ScanPool()
    {
        _allPoolPaths.Clear();
        // 导出安全的递归扫描（导出包内 .tres 会变成 "xxx.tres.remap"，见 Script/IO/ResourceDirScan.cs）
        Script.IO.ResourceDirScan.TresRecursiveInto(PlayerCardsDir, _allPoolPaths);
        // 【2026-10-12】显式 StringComparer.Ordinal：无参 List<T>.Sort() 用文化敏感比较器（走 ICU），
        // Web 版运行时没有 ICU，会 abort。卡池路径是 ASCII，序数排序与原先结果一致。
        _allPoolPaths.Sort(System.StringComparer.Ordinal);
        GD.Print($"【DeckBuilder】扫描到卡池 {_allPoolPaths.Count} 张");
        ApplyPoolFilter();
    }

    // 根据顶部搜索框的关键字过滤卡池（**按卡名**匹配，不区分大小写；秘社模式按秘社名/编号）
    private void ApplyPoolFilter()
    {
        _poolPage = 0;   // 过滤后回到第一页

        // 【2026-10-12 Web 修复】`.Trim()` 保留（待验证），但大小写折叠必须换成手写 ASCII 版：
        // 实测 `ToLowerInvariant()` 在 Web 上同样让 wasm abort（TextInfo 仍走原生）。
        // 卡池过滤只需要 ASCII 语义（卡号/英文名/中文名），手写折叠行为一致。
        // 【2026-10-17】顺带把判空/裁剪也换成 ASCII 版：`string.IsNullOrWhiteSpace` / `Trim()`
        // 的空白判定同样可能落到 Unicode 类别表（见 WebSafeText 类头），换掉更保险、对卡号搜索无行为差异。
        string f = WebSafeText.IsNullOrWhiteSpaceAscii(_poolFilter)
            ? ""
            : WebSafeText.ToLowerAscii(WebSafeText.TrimAscii(_poolFilter));

        if (_councilMode)
        {
            _councilFilterPaths.Clear();
            foreach (var p in _councilPaths)
            {
                if (f.Length == 0) { _councilFilterPaths.Add(p); continue; }
                if (WebSafeText.ToLowerAscii(System.IO.Path.GetFileNameWithoutExtension(p)).Contains(f))
                {
                    _councilFilterPaths.Add(p);
                    continue;
                }
                // 秘社数量少（十余张），允许按卡名搜，代价可接受
                var c = ResourceLoader.Load<CouncilCardData>(p);
                if (c != null && !string.IsNullOrEmpty(c.CardName) && WebSafeText.ToLowerAscii(c.CardName).Contains(f))
                    _councilFilterPaths.Add(p);
            }
            return;
        }

        _poolPaths.Clear();

        // 【2026-10-17 · 检索目标互换】
        //   顶部搜索框 → **卡名**（NameQuery，走索引的 NameFold 子串匹配）；
        //   高级筛选里的"卡号"框 → **编号**（UidQuery，见 OnFilterSearch）。
        //   两者都是"AND"关系，与其它结构化条件共同收窄结果。
        _filter.NameQuery = _poolFilter;
        _filter.PrepareForSearch();
        // 卡名属于"结构化条件"，走索引匹配；编号条件仍由 MatchesUid 单独组合
        if (_filter.HasStructured) EnsureIndexForStructured();
        if (_indexReady)
        {
            var cards = CardSearchIndex.Cards;
            bool needStruct = _filter.HasStructured;
            ulong __tF = Time.GetTicksMsec();
            for (int i = 0; i < cards.Count; i++)
            {
                var e = cards[i];
                if (!_filter.MatchesUid(e)) continue;
                if (needStruct && !_filter.Matches(e)) continue;
                _poolPaths.Add(e.ResPath);
            }
            WebPerfParams.Summary("deck.filter hits=" + WebSafeText.Int(_poolPaths.Count)
                + " ms=" + WebSafeText.Int((int)(Time.GetTicksMsec() - __tF)));
            return;
        }

        // 回退（索引不可用/失败）：中文卡名必须靠索引里的 `NameFold` 才能匹配，
        // 没有索引就只能退化为"按文件名（含卡号/拼音 UID）子串"——即此时输入中文卡名搜不到。
        // 索引由构建期 `CardIndexRunner` 生成并随仓库提交，正常打包必然存在；
        // 这里只在异常情况下兜底，并**只在第一次**打一条告警，避免每敲一个字就刷屏。
        if (f.Length > 0 && !_fallbackWarned)
        {
            _fallbackWarned = true;
            GD.PrintErr("【DeckBuilder】卡池索引不可用：顶部搜索框已退化为按卡号/文件名匹配（中文卡名搜不到）。请重跑 CardIndexRunner");
        }
        if (f.Length == 0)
        {
            _poolPaths.AddRange(_allPoolPaths);
        }
        else
        {
            foreach (var p in _allPoolPaths)
            {
                string file = WebSafeText.ToLowerAscii(System.IO.Path.GetFileNameWithoutExtension(p));
                if (file.Contains(f))
                    _poolPaths.Add(p);
            }
        }
    }

    /// <summary>回退告警是否已打过（只打一次）。</summary>
    private bool _fallbackWarned;

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
            _searchBox.PlaceholderText = _councilMode ? "输入秘社名或编号过滤，如 MSJC" : "输入卡名过滤，如 通灵猫头鹰";
        if (_poolPager != null)
            _poolPager.Visible = !_councilMode;
        // 【2026-10-17】秘社模式不做结构化筛选：收起并隐藏高级筛选入口，
        // 保证秘社模式完全走原逻辑（ApplyPoolFilter 的秘社分支未改动）。
        if (_filterToggleBar != null)
        {
            _filterToggleBar.Visible = !_councilMode;
            if (_councilMode && _filterExpanded)
            {
                _filterExpanded = false;
                if (_filterToggleButton != null)
                {
                    _filterToggleButton.ButtonPressed = false;
                    _filterToggleButton.Text = "▼ 高级筛选（类别 / 属性 / 忠诚）";
                }
                if (_filterBoxScroll != null) _filterBoxScroll.Visible = false;
                Callable.From(ReflowPoolTop).CallDeferred();
            }
        }
    }

    private void ScanCouncils()
    {
        _councilPaths.Clear();
        // 秘社按系列分子目录，需递归扫描；同样统一走导出安全的扫描工具
        Script.IO.ResourceDirScan.TresRecursiveInto(CouncilCardsDir, _councilPaths);
        _councilPaths.Sort(System.StringComparer.Ordinal);
        ApplyPoolFilter();
        GD.Print($"【DeckBuilder】扫描到秘社 {_councilPaths.Count} 张");
    }

    // ============================================================
    // 【2026-10-17】卡组名输入框 + 右侧 ▾ 下拉（合并"新建/载入"与"卡组名"）
    //   下拉用"场景内面板"而不是 PopupMenu：Popups 是 Window，在 viewport 拉伸下
    //   屏幕坐标与控件坐标会错位（Web 导出尤其明显）；面板方案定位确定、样式也好统一。
    // ============================================================
    private const int DeckLoadRowH = 34;
    private const int DeckLoadMaxH = 320;

    /// <summary>重建下拉里的卡组文件列表（"（新建卡组）" + `Decks/*.json`，按文件名序数升序）。</summary>
    private void RefreshDeckLoadList()
    {
        if (_deckLoadList == null) return;
        ClearChildren(_deckLoadList);

        // ① 新建（清空当前编辑内容）
        _deckLoadList.AddChild(MkDeckLoadRow("（新建卡组）", null));

        // ② 已保存的卡组
        // 【2026-10-20】改走 DeckCatalog：与房间页下拉框**同一份清单、同一套排序**
        //（ASCII 折叠忽略大小写，中文名也不会踩 ICU；原先这里另写了一份 DirAccess 扫描）。
        var paths = DeckCatalog.ListDeckPaths();
        foreach (var p in paths)
        {
            _deckLoadList.AddChild(MkDeckLoadRow(DeckCatalog.DisplayName(p), p));
        }

        // 面板高度按条目数收紧（不多占、也不超出）
        int h = Mathf.Clamp(6 + DeckLoadRowH * (paths.Count + 1), DeckLoadRowH + 12, DeckLoadMaxH);
        _deckLoadPanel.OffsetBottom = 42f + h;
    }

    /// <summary>下拉里的一行：整行可点（按钮铺满宽度，文字左对齐）。</summary>
    private Button MkDeckLoadRow(string text, string path)
    {
        var b = new Button
        {
            Text = text,
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, DeckLoadRowH),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
        };
        b.Pressed += () =>
        {
            HideDeckLoadPanel();
            if (path == null)
            {
                ClearWorkingDeck();
            }
            else
            {
                LoadDeckFromFile(path);
                // 【2026-10-20】在编辑页选中卡组 = 把该卡组设为新的"默认卡组"
                //（写盘 + 同步本场 GameManager，房间页/开局立刻生效）。
                DeckSelection.Remember(path);
                GD.Print($"【DeckBuilder】已将该卡组设为默认：{path}");
            }
        };
        return b;
    }

    /// <summary>"（新建卡组）"：清空编辑态（与原先选到第 0 项的语义一致）。</summary>
    private void ClearWorkingDeck()
    {
        _workingCards.Clear();
        _nameEdit.Text = "";
        RefreshDeckView();
        GD.Print("【DeckBuilder】已切换为新建卡组");
    }

    private void ToggleDeckLoadPanel()
    {
        if (_deckLoadPanel == null) return;
        if (_deckLoadPanel.Visible) { HideDeckLoadPanel(); return; }
        RefreshDeckLoadList();          // 每次展开都重扫一遍，保存/外部新增文件后立刻可见
        _deckLoadPanel.Visible = true;
        _deckLoadPanel.MoveToFront();
    }

    private void HideDeckLoadPanel()
    {
        if (_deckLoadPanel != null) _deckLoadPanel.Visible = false;
    }

    // ============================================================
    // 卡池渲染 & 交互
    // ============================================================
    private void RefreshPoolView()
    {
        ulong __t0 = Time.GetTicksMsec();
        int __before = _poolList.GetChildCount();
        int token = ++_fillToken;     // 作废上一批未完成的渐进填充
        int ok = 0, fail = 0;

        ClearChildren(_poolList);

        // 【筛选模式】秘社模式走独立的候选列表渲染（来源、加入规则都与卡牌不同）
        if (_councilMode)
        {
            RefreshCouncilPoolView();
            WebPerfParams.Summary($"deck.pool(council) total={Time.GetTicksMsec() - __t0}ms");
            return;
        }

        int total = _poolPaths.Count;
        int pageSize = PageSize;
        int pageCount = Mathf.Max(1, (total + pageSize - 1) / pageSize);
        if (_poolPage >= pageCount) _poolPage = pageCount - 1;
        if (_pageLabel != null) _pageLabel.Text = $"{_poolPage + 1} / {pageCount}（共 {total} 张）";

        // 【2026-10-12 Web 诊断开关 --wp-nocards】连卡牌面板都不建。
        // 用来判断首帧 abort 到底是不是卡牌面板（纹理 + 中文换行文本）引起的：
        // 加上这个开关后如果 DeckBuilder 能正常显示出来，就把范围锁死在卡牌面板内部。
        if (WebPerfParams.SkipPoolCards)
        {
            WebPerfParams.Summary("deck.pool 被 --wp-nocards 跳过（不建任何卡牌面板）");
            GD.Print("【DeckBuilder】--wp-nocards：已跳过卡牌面板构建");
            return;
        }

        // 只渲染当前页，避免一次性加载全部卡牌导致内存/显存爆掉闪退
        int start = _poolPage * pageSize;
        int end = Mathf.Min(start + pageSize, total);

        // 【性能埋点】把「加载模板数据 / 解析卡图路径 / 解码纹理 / 建节点」四件事分开计时，
        // 否则只知道"慢"，不知道慢在哪。见 Script/Web/WebPerfParams.cs。
        ulong dData = 0, dResolve = 0, dTex = 0, dNode = 0, texMax = 0;
        string texMaxPath = "";
        int texLoad = 0, texHit = 0, texPending = 0;
        // 【B2：渐进填充改成默认行为，2026-10-11】原先必须显式加 `--wp-prog` 才启用。
        // 现在默认就是「分帧填图」，只在显式 `--wp-sync`（或 E2 的 `--wp-notex`）时回到旧的同步加载。
        // 这样导出的包开箱即用，不必依赖实验开关。
        bool progressive = !WebPerfParams.ForceSync && !WebPerfParams.SkipTextures;
        var pending = new List<(string Path, TextureRect Target)>();

        for (int i = start; i < end; i++)
        {
            string path = _poolPaths[i];

            ulong s = Time.GetTicksMsec();
            var data = ResourceLoader.Load<PlayerCardData>(path);
            dData += Time.GetTicksMsec() - s;

            Texture2D tex = null;
            string imgPath = null;
            if (data != null)
            {
                s = Time.GetTicksMsec();
                imgPath = CardUI.ResolveCardImagePath(data.UID);
                dResolve += Time.GetTicksMsec() - s;

                if (!string.IsNullOrEmpty(imgPath))
                {
                    if (_texCache.TryGetValue(imgPath, out tex))
                    {
                        texHit++;
                    }
                    else if (WebPerfParams.SkipTextures)
                    {
                        // 【实验 E2】?notex=1：完全不加载纹理，用来量「摘掉纹理后还剩多少耗时」
                    }
                    else if (progressive)
                    {
                        texPending++;                                   // 交给 FillTexturesProgressive 分帧填
                    }
                    else
                    {
                        s = Time.GetTicksMsec();
                        tex = ResourceLoader.Load<Texture2D>(imgPath);
                        ulong dt = Time.GetTicksMsec() - s;
                        dTex += dt; texLoad++;
                        if (dt > texMax) { texMax = dt; texMaxPath = imgPath; }
                        if (dt > 50) WebPerfParams.Log($"slow-tex {dt}ms {imgPath}");
                        if (tex != null) _texCache[imgPath] = tex;      // 缓存，避免重复加载
                    }
                }
            }

            s = Time.GetTicksMsec();
            // 每张结果＝一张信息面板：卡图 + 结构化数据列 + 加入按钮
            var panel = BuildCardPanel(path, data, tex ?? CardBackPlaceholder(), out var thumb);
            _poolList.AddChild(panel);
            dNode += Time.GetTicksMsec() - s;

            if (tex == null && progressive && thumb != null && !string.IsNullOrEmpty(imgPath))
            {
                pending.Add((imgPath, thumb));
            }

            if (tex != null) ok++;
            else if (WebPerfParams.SkipTextures) { /* 故意不加载，不算失败 */ }
            else if (progressive) { /* 待分帧填充，不算失败 */ }
            else { fail++; GD.PrintErr($"【DeckBuilder】卡图加载失败: {data?.UID} ({path})"); }
        }

        WebPerfParams.Summary(
            $"deck.pool page={_poolPage + 1}/{pageCount} rows={end - start} "
            + $"data={dData}ms resolve={dResolve}ms tex={dTex}ms(load {texLoad}/hit {texHit}/pending {texPending}, 最慢 {texMax}ms) "
            + $"nodes={dNode}ms children {__before}->{_poolList.GetChildCount()} total={Time.GetTicksMsec() - __t0}ms");
        if (texMaxPath.Length > 0) WebPerfParams.Log($"最慢的一张：{texMaxPath}");

        if (pending.Count > 0) FillTexturesProgressive(token, pending);

        GD.Print($"【DeckBuilder】刷新卡池（第 {_poolPage + 1} 页）完成：本页 {ok} 张 / 失败 {fail} 张，子节点数 {_poolList.GetChildCount()} / 总计 {total}");
    }

    /// <summary>
    /// 立即把旧行摘出容器再排队释放。
    /// 为什么不只 `QueueFree()`：它要到**帧末**才真正删除，在此之前旧行仍在树上参与布局与绘制；
    /// 同一帧内刷新多次就会重复付这份成本。先 `RemoveChild` 即刻停止布局，`QueueFree` 保证安全回收。
    /// </summary>
    private static void ClearChildren(Node container)
    {
        foreach (var child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>卡背占位图（渐进填充期间先贴它，避免行先空着）。懒加载，只尝试一次。</summary>
    private Texture2D CardBackPlaceholder()
    {
        if (!_cardBackTried)
        {
            _cardBackTried = true;
            _cardBackTex = ResourceLoader.Load<Texture2D>("res://Cards/Image/CardBack.jpg");
        }
        return _cardBackTex;
    }

    /// <summary>
    /// 【性能修复 B2】卡图渐进填充：每帧只同步加载一张纹理并贴到对应缩略图上，
    /// 每张之后让出主线程一帧，于是界面**立刻可交互**，不再出现「进界面整屏冻结十几秒」。
    ///
    /// 两个安全点：
    ///   1. `token != _fillToken` → 这次刷新已被翻页/重刷取代，立刻放弃（否则会往旧节点上贴图）；
    ///   2. 每帧检查 `IsInstanceValid`，节点已随场景释放就直接停。
    /// </summary>
    private async void FillTexturesProgressive(int token, List<(string Path, TextureRect Target)> pending)
    {
        var tree = GetTree();
        if (tree == null) return;

        ulong t0 = Time.GetTicksMsec();
        int done = 0, failed = 0, skipped = 0;
        ulong maxOne = 0;

        foreach (var item in pending)
        {
            if (token != _fillToken)
            {
                WebPerfParams.Log($"prog fill 被新批次取代（已填 {done} 张）");
                return;
            }
            if (!GodotObject.IsInstanceValid(item.Target)) { skipped++; continue; }

            _texCache.TryGetValue(item.Path, out var tex);
            if (tex == null)
            {
                ulong s = Time.GetTicksMsec();
                tex = ResourceLoader.Load<Texture2D>(item.Path);
                ulong dt = Time.GetTicksMsec() - s;
                if (dt > maxOne) maxOne = dt;
                if (tex != null) _texCache[item.Path] = tex;
                else failed++;
            }
            if (GodotObject.IsInstanceValid(item.Target)) item.Target.Texture = tex;
            done++;

            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(this)) return;
        }

        WebPerfParams.Summary(
            $"deck.prog fill 完成 {done} 张 / 失败 {failed} / 跳过已销毁 {skipped} / "
            + $"共 {Time.GetTicksMsec() - t0}ms（单张最慢 {maxOne}ms）");
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
            _poolList.AddChild(BuildCouncilPanel(path, data, tex, isCurrent));
        }

        if (_pageLabel != null) _pageLabel.Text = $"共 {_councilFilterPaths.Count} 个秘社";
        GD.Print($"【DeckBuilder】刷新秘社候选：{_councilFilterPaths.Count} 个 / 加载失败 {fail} 张");
    }

    // ============================================================
    // 秘社展示面板（仅在勾选"秘社"时出现，UI 与玩家卡牌信息面板同一套）
    // ============================================================

    /// <summary>
    /// 秘社面板：与玩家卡牌面板完全同一风格（同一份 StyleBox 外框 + 100x140 卡图 + 数据列 + 操作按钮）。
    /// 三行布局：①秘社名 / 起始手牌数量　②颜色 / 组织　③编号。
    /// </summary>
    private PanelContainer BuildCouncilPanel(string path, CouncilCardData data, Texture2D tex, bool isCurrent)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", GetPoolRowStyle());

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        var thumb = MakeCardThumb(tex);
        thumb.GuiInput += (InputEvent e) =>   // 卡图可点击设为当前秘社
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                SetCouncil(path);
        };
        AttachCardHover(thumb, data);          // 需求 9：秘社候选卡图同样支持悬浮看整卡
        row.AddChild(thumb);

        row.AddChild(BuildCouncilDetailColumn(path, data, isCurrent));

        var actBtn = new Button { Text = isCurrent ? "当前秘社" : "添加" };
        actBtn.Disabled = isCurrent;
        actBtn.CustomMinimumSize = new Vector2(56, 0);
        actBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        actBtn.Pressed += () => SetCouncil(path);
        row.AddChild(actBtn);

        return panel;
    }

    /// <summary>
    /// 秘社数据列（固定三行）：
    /// ①秘社名（恒显示）｜起始手牌数量（恒显示）
    /// ②颜色（恒显示）｜组织（恒显示）
    /// ③编号（恒显示）
    /// 与玩家卡牌共用 <see cref="MakeInfoLine"/>，因此字号、配色、换行规则天然一致。
    /// </summary>
    private static VBoxContainer BuildCouncilDetailColumn(string path, CouncilCardData data, bool isCurrent)
    {
        var col = new VBoxContainer();
        col.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        col.AddThemeConstantOverride("separation", 2);

        // ① 秘社名｜起始手牌数量
        col.AddChild(MakeInfoLine(
            new List<string> { "秘社名：" + CouncilNameText(data, path), "起始手牌数量：" + StartingHandText(data) },
            isCurrent ? new Color(0.55f, 0.95f, 0.55f) : new Color(1f, 0.92f, 0.72f),
            16));

        // ② 颜色｜组织
        col.AddChild(MakeInfoLine(
            new List<string> { "颜色：" + CouncilColorText(data), "组织：" + CouncilOrganizationText(data) }));

        // ③ 编号
        col.AddChild(MakeTextLine("编号：" + CouncilUidText(data, path), new Color(0.75f, 0.75f, 0.8f)));

        return col;
    }

    private static string CouncilNameText(CouncilCardData data, string path)
    {
        var name = data?.CardName;
        return string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name.Trim();
    }

    private static string CouncilUidText(CouncilCardData data, string path)
    {
        var uid = data?.UID;
        return string.IsNullOrWhiteSpace(uid) ? System.IO.Path.GetFileNameWithoutExtension(path) : uid.Trim();
    }

    /// <summary>起始手牌数量：数据缺失时以卡表默认值 6 兜底。</summary>
    private static string StartingHandText(CouncilCardData data)
        => data == null ? "—" : data.StartingHandSize.ToString();

    /// <summary>秘社颜色：与玩家卡同样取自 Colors 枚举数组（无有效值时回落"棕（中立）"）。</summary>
    private static string CouncilColorText(CouncilCardData data)
        => data == null ? "—" : ColorsText(data.Colors);

    /// <summary>秘社组织（缺失时以"—"占位以保证三行结构完整）。</summary>
    private static string CouncilOrganizationText(CouncilCardData data)
        => string.IsNullOrWhiteSpace(data?.Organization) ? "—" : data.Organization.Trim();

    // ============================================================
    // 搜索结果的结构化信息面板（五行数据）
    // ============================================================

    /// <summary>
    /// 【2026-10-20 背景】卡组编辑界面的场景底图。
    /// 本界面是全工程的**视觉基准**，底色原本是纯色；补一层压暗到 0.80 的场景图只为质感，
    /// 卡池行/卡组板的对比度优先（所以压暗值给得很高）。
    /// 图片一律「等比铺满 + 溢出裁掉」，全部经 `BackdropHelper`，杜绝拉伸变形。
    /// </summary>
    private void ApplyBackdrop()
    {
        var baseRect = UITheme.BackgroundRect();
        AddChild(baseRect);
        MoveChild(baseRect, 0);

        var cover = BackdropHelper.AttachCover(this, "res://Cards/Image/桌面6.jpg", 0.80f);
        if (cover != null) MoveChild(cover, 1);
    }

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
    /// 详细数据固定五行：<c>卡名/独有卡名</c>、<c>费用/颜色/魔法领域/组织</c>、<c>类别/子类别</c>、<c>能力图标</c>、<c>编号</c>。
    /// <paramref name="thumb"/> 回传卡图控件，供 <see cref="FillTexturesProgressive"/> 稍后（分帧）把真图贴上去。
    /// </summary>
    private PanelContainer BuildCardPanel(string path, PlayerCardData data, Texture2D tex, out TextureRect thumb)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", GetPoolRowStyle());

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        thumb = MakeCardThumb(tex);
        thumb.GuiInput += (InputEvent e) =>   // 卡图可点击加入，保持原有交互
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                AddCard(path);
        };
        AttachCardHover(thumb, data);          // 需求 9：卡池的卡图也要能悬浮看整卡
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

        // ③ 类别（恒显示）｜子类别（仅当存在时显示）—— 短行，不参与 WordSmart 分词测量
        var f3 = new List<string> { "类别：" + CategoryText(data) };
        string subs = SubCategoriesText(data);
        if (subs.Length > 0)
            f3.Add("子类别：" + subs);
        col.AddChild(MakeInfoLine(f3, null, 0, wrap: false));

        // ④ 能力：固定顺序罗列，数值为 0 的项整体略去，其余保持相对顺序 —— 短行
        var abilities = AbilityFields(data);
        col.AddChild(MakeTextLine(abilities.Count > 0 ? "能力图标：" + string.Join("，", abilities) : "能力图标：", null, 0, wrap: false));

        // ⑤ 编号（恒显示）—— 短行
        col.AddChild(MakeTextLine("编号：" + UidText(data, path), new Color(0.75f, 0.75f, 0.8f), 0, wrap: false));

        return col;
    }

    /// <summary>信息面板中的一行：字段用分隔符隔开，宽度不足时整行换行（绝不截断丢失数据）。</summary>
    private static Label MakeInfoLine(List<string> fields, Color? modulate = null, int fontSize = 0, bool wrap = true)
        => MakeTextLine(string.Join("　｜　", fields), modulate, fontSize, wrap);

    /// <summary>
    /// 信息面板单行控件：横向填满剩余宽度、垂直居中，可按行指定颜色/字号。
    ///
    /// <paramref name="wrap"/> 为 false 时用 <see cref="TextServer.AutowrapMode.Arbitrary"/>（按字符换行）替代
    /// <see cref="TextServer.AutowrapMode.WordSmart"/>（按词智能换行）：
    /// WordSmart 每行都要做一次分词+断行测量，而卡池一页几百行、每行都是中文，
    /// 中文本来就可以任意字符处换行，用 Text 模式更便宜且视觉效果基本一致。
    /// 只对「几乎不会换行的短行」（类别/能力/编号）关掉，长行仍保留 WordSmart。
    /// </summary>
    private static Label MakeTextLine(string text, Color? modulate = null, int fontSize = 0, bool wrap = true)
    {
        var lb = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        // 【2026-10-12 Web 诊断开关 --wp-nowrap】强制改用 Arbitrary（按字符换行）。
        // 怀疑点：WordSmart（按词智能换行）需要分词/断行，中文断行要走到 ICU 那条路径，
        // 而 Web 运行时正是所有全球化路径都会 abort。用开关一测即可确认。
        lb.AutowrapMode = (wrap && !WebPerfParams.NoWordWrap)
            ? TextServer.AutowrapMode.WordSmart
            : TextServer.AutowrapMode.Arbitrary;
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
        => data == null ? "棕（中立）" : ColorsText(data.Colors);

    /// <summary>秘社颜色：与玩家卡共用同一份枚举→中文映射，保证展示口径一致。</summary>
    private static string ColorsText(CouncilCardData data)
        => data == null ? "棕（中立）" : ColorsText(data.Colors);

    private static string ColorsText(System.Collections.Generic.IEnumerable<CardColor> colors)
    {
        var parts = new List<string>();
        if (colors != null)
            foreach (var c in colors)
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
    /// 若已有秘社则直接覆盖（不叠加、不报错、不产生重复项），并重算构筑限制。
    /// </summary>
    private void SetCouncil(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        bool replaced = !string.IsNullOrEmpty(_workingCouncil) && _workingCouncil != path;
        _workingCouncil = path;

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
    // 【2026-10-17 卡组框重做】卡组板：秘社信息条 / 排序下拉 / 换列装箱 / 悬浮
    // ============================================================

    /// <summary>秘社缩略卡图高度（信息带 112px，留 6px 上下边距）。</summary>
    private const float CouncilThumbH = 100f;
    /// <summary>秘社缩略卡图宽度（卡面 5:7，100 → 71）。</summary>
    private const float CouncilThumbW = 71f;

    /// <summary>
    /// 秘社信息条（**卡组框之外**，挂在 `DeckPanel/InfoPanel/CouncilSlot`）。
    /// 独立于 <see cref="_workingCards"/>、不计入卡牌总数：秘社只有一张、无份数，
    /// 增删语义也与玩家卡不同（<see cref="SetCouncil"/> / <see cref="ClearCouncil"/>），
    /// 故不参与卡组板的排序与换列。内容：卡图 + 秘社名 + 移除按钮。
    ///
    /// 【2026-10-17 修复】原来这里还显示"编号"，编号一换行就把"✕ 移除秘社"顶到信息带之外、
    /// 压到下面的排序按钮上。现按用户口径处理：
    ///   · **去掉编号显示**；
    ///   · **移除按钮的下边缘与秘社卡图的下边缘对齐** —— 卡图固定 <see cref="CouncilThumbH"/> 高，
    ///     右侧竖列同样钉成这个高度（`SizeFlagsVertical=ShrinkBegin` + `CustomMinimumSize.Y`），
    ///     中间塞一个可扩展的空条把按钮压到底部，于是两者底边恒等，与名字几行无关。
    /// </summary>
    private void BuildCouncilInfoStrip()
    {
        var slot = GetNodeOrNull<Control>("DeckPanel/InfoPanel/CouncilSlot");
        if (slot == null) return;
        ClearChildren(slot);
        if (string.IsNullOrEmpty(_workingCouncil)) return;

        var data = ResourceLoader.Load<CouncilCardData>(_workingCouncil);
        string imgPath = ResolveCouncilImagePath(data);
        Texture2D tex = null;
        if (!string.IsNullOrEmpty(imgPath) && !_texCache.TryGetValue(imgPath, out tex))
        {
            tex = ResourceLoader.Load<Texture2D>(imgPath);
            if (tex != null) _texCache[imgPath] = tex;      // 与卡池/卡组共用缓存
        }
        if (tex == null) GD.PrintErr($"【DeckBuilder】卡组秘社卡图加载失败: {_workingCouncil}");

        // 缩略卡图（71×100，塞进信息带；悬浮看整卡）—— 它就是"下方边缘"的基准
        var thumb = new TextureRect
        {
            Texture = tex,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ExpandMode = (TextureRect.ExpandModeEnum)1,     // FitWidth
            CustomMinimumSize = new Vector2(CouncilThumbW, CouncilThumbH),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        AttachCardHover(thumb, data);
        slot.AddChild(thumb);

        // 右侧竖列：高度恒等于卡图高度（ShrinkBegin 保持最小高度并顶端对齐，不被 HBox 拉满 112）
        var col = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(0f, CouncilThumbH),
        };
        col.AddThemeConstantOverride("separation", 2);

        string councilName = string.IsNullOrEmpty(data?.CardName)
            ? System.IO.Path.GetFileNameWithoutExtension(_workingCouncil)
            : data.CardName;
        var name = new Label
        {
            Text = "【秘社】" + councilName,
            // 短标签用按字符换行，避开 WordSmart 的分词测量（见 MakeTextLine 注释）
            AutowrapMode = TextServer.AutowrapMode.Arbitrary,
            // 最多两行：名字再长也不会把下面的按钮挤出信息带（同 112px 高度内的硬保护）
            MaxLinesVisible = 2,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            Modulate = new Color(0.55f, 0.95f, 0.55f),
        };
        col.AddChild(name);

        // 可扩展空条：吃掉"卡图高度 − 名字 − 按钮"的余量，把按钮压到列底 = 卡图下边缘
        col.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });

        var remove = new Button
        {
            Text = "✕ 移除秘社",
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
        };
        remove.Pressed += ClearCouncil;
        col.AddChild(remove);

        slot.AddChild(col);
    }

    /// <summary>卡图控件的悬浮预览接线（卡池 / 秘社候选 / 秘社条共用）。</summary>
    private void AttachCardHover(Control control, BaseCardData data)
    {
        if (control == null || data == null) return;
        control.MouseFilter = Control.MouseFilterEnum.Stop;   // 必须吃鼠标事件才能拿到 entered/exited
        control.MouseEntered += () =>
        {
            if (_hoverPreview == null) return;
            // 锚点取卡图左上角：浮层按"卡右侧优先、贴边转左"定位（见 CardHoverPreview.PositionFrame）
            _hoverPreview.ShowFor(data, control.GlobalPosition);
        };
        control.MouseExited += () =>
        {
            if (_hoverPreview != null) _hoverPreview.HidePreview();
        };
    }

    /// <summary>
    /// 【2026-10-17】卡组板的外框装配（运行期创建，沿用 <see cref="BuildRestrictionPanel"/> 的惯例）：
    ///   · 卡组框：竖向不滚、内建横条隐藏（`ShowNever` 只隐藏显示、保留滚动能力）；
    ///   · 框外横滑条：与内建条双向同步，**仅内容横向溢出时显示**；
    ///   · 排序优先级下拉；
    ///   · 本场景专用的悬浮浮层（挂在根节点最后 ⇒ 画在最上层）。
    /// </summary>
    private void BuildDeckBoardChrome()
    {
        if (_deckScroll != null)
        {
            _deckScroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
            // ShowNever：横条不显示但仍可滚动；显示交给框外的 _deckHBar
            _deckScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
        }

        _deckHBar = GetNodeOrNull<HScrollBar>("DeckPanel/DeckHBar");
        if (_deckHBar != null)
        {
            _deckHBar.Visible = false;      // 内容没溢出前不占位、不显示
            _deckHBar.ValueChanged += OnDeckHBarValueChanged;
        }

        var sortBar = GetNodeOrNull<Control>("DeckPanel/SortBar");
        if (sortBar != null)
        {
            // 【2026-10-17】排序改为**一个按钮点一下就切换**（原来是 OptionButton 下拉）：
            // 按钮文字直接显示当前优先级，点一次在"费用优先 / 颜色优先"之间来回切。
            _sortModeButton = new Button
            {
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                CustomMinimumSize = new Vector2(150, 0),
                TooltipText = "点击切换排序优先级（费用优先 ⇄ 颜色优先）",
            };
            _sortModeButton.Pressed += OnSortModeButtonPressed;
            UpdateSortModeButtonText();
            sortBar.AddChild(_sortModeButton);

            var hint = new Label
            {
                Text = "（左键 +1 ｜ 右键 -1 ｜ 悬浮看整卡）",
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalAlignment = HorizontalAlignment.Right,
                Modulate = new Color(0.7f, 0.72f, 0.78f),
            };
            hint.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            sortBar.AddChild(hint);
        }

        // 本场景专用浮层：DeckBuilder 是独立场景且不是 Window，CardPreviewBus 的全局浮层此时为空
        // （战场视图进场时 Attach、离场时 Detach），故不能走总线，直接持有一个本地实例。
        _hoverPreview = new CardHoverPreview { Name = "CardHoverPreview" };
        _hoverPreview.CanPreviewOverride = _ => true;
        AddChild(_hoverPreview);
    }

    /// <summary>
    /// 【2026-10-17】排序优先级**按钮切换**：点一次在「费用优先」与「颜色优先」之间来回切，
    /// 只重排卡组板，不重扫卡池。按钮文字随当前模式更新，界面上直接看得到现在的优先级。
    /// </summary>
    private void OnSortModeButtonPressed()
    {
        _sortMode = _sortMode == DeckBoardLayout.DeckSortMode.CostFirst
            ? DeckBoardLayout.DeckSortMode.ColorFirst
            : DeckBoardLayout.DeckSortMode.CostFirst;
        UpdateSortModeButtonText();
        RebuildDeckBoard();
    }

    /// <summary>按钮文字＝当前优先级（费用优先 = 高费在前；颜色优先 = 按颜色枚举升序）。</summary>
    private void UpdateSortModeButtonText()
    {
        if (_sortModeButton == null) return;
        _sortModeButton.Text = _sortMode == DeckBoardLayout.DeckSortMode.ColorFirst
            ? "排序：颜色优先"
            : "排序：费用优先";
    }

    /// <summary>视口尺寸变化（窗口缩放 / Web 画布变化）→ 重新量列高并重排。</summary>
    private void OnViewportResized()
    {
        Callable.From(RecomputeDeckLayout).CallDeferred();
    }

    /// <summary>
    /// 量取"列高 = 卡组框可视高度"后重排。必须等首帧布局完成（此时 `Size` 才有真实像素），
    /// 早于此会拿到 0 —— 与 `ReflowPoolTop` 同一套首帧后处理惯例。
    /// </summary>
    private void RecomputeDeckLayout()
    {
        if (_deckScroll == null || _deckBoard == null) return;

        float h = _deckScroll.Size.Y;
        if (h <= 1f)
        {
            // 布局尚未完成：下一帧再试（限次，避免场景异常时无限重试）
            if (_layoutRetry++ < 20) Callable.From(RecomputeDeckLayout).CallDeferred();
            return;
        }
        _layoutRetry = 0;

        float newH = Mathf.Max(h, CardLayoutHelper.CARD_HEIGHT);   // 钳制：列高不得小于卡高
        if (_boardBuilt && Mathf.Abs(newH - _columnH) < 0.5f) return;   // 列高没变就不重建
        _columnH = newH;
        RebuildDeckBoard();
        GD.Print($"【DeckBuilder】卡组框重排：列高 {WebSafeText.Int((int)_columnH)}px / 组 {_groupNodes.Count} 个");
    }

    /// <summary>
    /// 重建卡组板：组装排序输入 → 算法层算出每个"同名组"的列号/Y/错位步长 → 建节点绝对定位。
    /// 取代原来"每个卡种一行 HBox（卡图 + xN + - + +）"的纵向列表。
    /// </summary>
    private void RebuildDeckBoard()
    {
        if (_deckBoard == null) return;
        ulong __t0 = Time.GetTicksMsec();
        if (_columnH <= 1f) _columnH = CardLayoutHelper.CARD_HEIGHT;   // 首帧前兜底

        // ① 组装排序/分组输入（同时缓存模板数据供悬浮使用）
        _boardData.Clear();
        var cards = new List<DeckBoardLayout.DeckBoardCard>();
        int total = 0, fail = 0;
        foreach (var kv in _workingCards)
        {
            total += kv.Value;
            var data = ResourceLoader.Load<PlayerCardData>(kv.Key);
            if (data == null) { fail++; continue; }
            _boardData[kv.Key] = data;
            cards.Add(new DeckBoardLayout.DeckBoardCard
            {
                TemplatePath = kv.Key,
                CardName = data.CardName ?? "",
                Uid = data.UID ?? "",
                Cost = data.Cost,
                ColorKey = ColorKeyOf(data),
                Count = kv.Value,
            });
        }

        // ② 排序 → 分组（50 张切分）→ 换列装箱（含压缩）
        var p = new DeckBoardLayout.DeckBoardParams
        {
            CardW = CardLayoutHelper.CARD_WIDTH,
            CardH = CardLayoutHelper.CARD_HEIGHT,
            ColumnW = DeckColumnW,
            ColumnH = _columnH,
            Offset0 = DeckOffset0,
            GroupGap = DeckGroupGap,
            MaxPerGroup = DeckMaxPerGroup,
            Mode = _sortMode,
        };
        var result = DeckBoardLayout.Compute(cards, p);

        // ③ 重建节点
        ClearChildren(_deckBoard);
        _groupNodes.Clear();
        _hoverGroupNode = null;
        if (_hoverPreview != null) _hoverPreview.HidePreview();

        foreach (var g in result.Groups)
        {
            var node = BuildDeckGroupNode(g);
            _deckBoard.AddChild(node);
            _groupNodes.Add((g, node));
        }

        // ④ 板内容尺寸（决定 ScrollContainer 的横向滚动范围）
        var contentSize = new Vector2(result.ContentWidth, _columnH);
        _deckBoard.CustomMinimumSize = contentSize;
        _deckBoard.Size = contentSize;
        _boardBuilt = true;

        WebPerfParams.Summary(
            $"deck.board groups={result.Groups.Count} cols={result.ColumnCount} "
            + $"cards={total} contentW={WebSafeText.Int((int)result.ContentWidth)} "
            + $"colH={WebSafeText.Int((int)_columnH)} total={Time.GetTicksMsec() - __t0}ms");
    }

    /// <summary>
    /// 一个"同名组"的节点：一个普通 Control（接收点击/悬浮），内部 N 张卡按 `offset` 纵向错位。
    ///
    /// 层级：子节点按 i 升序添加 ⇒ 越靠下越后画 ⇒ **Y 越靠下层级越高**；
    /// 于是最下方那张完整显示，上方每张只露出顶部 `offset` 像素（正好是卡图标题栏的卡名）。
    /// 命中的合理性：组内所有卡图与角标都是 `MouseFilter=Ignore`，指针落在组矩形任意位置都命中组节点；
    /// 同名组里 N 张是同一张卡，所以"点哪儿都是加/删一张同名卡"，语义一致。
    /// </summary>
    private Control BuildDeckGroupNode(DeckBoardLayout.DeckBoardGroup g)
    {
        float cardW = CardLayoutHelper.CARD_WIDTH;
        float groupH = Mathf.Max(g.GroupHeight, 1f);

        var node = new Control
        {
            Name = "G_" + (string.IsNullOrEmpty(g.Uid) ? "x" : g.Uid),
            Position = new Vector2(g.Column * DeckColumnW, g.StartY),
            CustomMinimumSize = new Vector2(cardW, groupH),
            Size = new Vector2(cardW, groupH),
            // Pass：本节点不处理的输入（滚轮等）继续冒泡给 ScrollContainer，
            // 否则鼠标停在卡上时卡组框滚不动（Stop 会把事件吞掉）。
            MouseFilter = Control.MouseFilterEnum.Pass,
        };

        // 卡图（与卡池共用 _texCache，避免点一次 + 就把整个卡组重载一遍）
        Texture2D tex = null;
        string imgPath = CardUI.ResolveCardImagePath(g.Uid);
        if (!string.IsNullOrEmpty(imgPath))
        {
            if (!_texCache.TryGetValue(imgPath, out tex))
            {
                tex = ResourceLoader.Load<Texture2D>(imgPath);
                if (tex != null) _texCache[imgPath] = tex;
            }
            if (tex == null) GD.PrintErr($"【DeckBuilder】卡组卡图加载失败: {g.TemplatePath}");
        }

        for (int i = 0; i < g.Count; i++)
        {
            var card = MakeCardThumb(tex);
            card.Position = new Vector2(0f, i * g.Offset);
            card.MouseFilter = Control.MouseFilterEnum.Ignore;   // 事件统一由组节点收
            node.AddChild(card);
        }

        // 常驻份数角标（叠在最下方那张完整显示的卡的左下角）
        if (g.Count > 1)
        {
            var badge = new Label
            {
                Text = "×" + WebSafeText.Int(g.Count),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Position = new Vector2(2f, groupH - 22f),
                Size = new Vector2(46f, 18f),
            };
            badge.AddThemeColorOverride("font_color", new Color(1f, 0.96f, 0.78f));
            badge.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
            badge.AddThemeConstantOverride("outline_size", 4);
            badge.AddThemeFontSizeOverride("font_size", 14);
            node.AddChild(badge);
        }

        // 悬浮时浮出的 +/- 微按钮（默认隐藏，由 UpdateDeckHover 显隐）
        var minus = new Button
        {
            Text = "－",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Position = new Vector2(cardW - 46f, groupH - 22f),
            CustomMinimumSize = new Vector2(20f, 20f),
            Size = new Vector2(20f, 20f),
        };
        minus.SetMeta("deck_micro", true);
        minus.Pressed += () => RemoveCard(g.TemplatePath);

        var plus = new Button
        {
            Text = "＋",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Position = new Vector2(cardW - 22f, groupH - 22f),
            CustomMinimumSize = new Vector2(20f, 20f),
            Size = new Vector2(20f, 20f),
        };
        plus.SetMeta("deck_micro", true);
        plus.Pressed += () => AddCard(g.TemplatePath);

        // 按钮比 100px 宽的卡窄，卡高 140 —— 这里只是把它们压在组矩形右下角，不做额外布局
        node.AddChild(minus);
        node.AddChild(plus);

        // 交互（需求 7）：左键 +1、右键 -1
        // 先 AcceptEvent 再改数据：改数据会立刻重建卡组板（本节点被移出树），
        // 之后再 AcceptEvent 就成了空操作，事件会继续冒泡给卡组框。
        node.GuiInput += (InputEvent e) =>
        {
            if (!(e is InputEventMouseButton mb) || !mb.Pressed) return;
            if (mb.ButtonIndex == MouseButton.Left)
            {
                node.AcceptEvent();
                AddCard(g.TemplatePath);
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                node.AcceptEvent();
                RemoveCard(g.TemplatePath);
            }
        };

        return node;
    }

    /// <summary>
    /// 卡图颜色排序键：取该卡颜色枚举值中的**最小者**（与 `CardColor` 定义顺序一致），
    /// 无合法颜色时回落棕（中立 0）。见 `DeckBoardLayout.ColorKeyOf`。
    /// </summary>
    private static int ColorKeyOf(PlayerCardData data)
    {
        if (data?.Colors == null) return 0;
        var vals = new List<int>();
        foreach (var c in data.Colors)
        {
            if (!System.Enum.IsDefined(typeof(CardColor), c)) continue;   // 过滤哨兵/脏值
            vals.Add((int)c);
        }
        return DeckBoardLayout.ColorKeyOf(vals);
    }

    // ============================================================
    // 【2026-10-17】框外横滑条 ↔ 内建横滑条 同步
    // ============================================================

    /// <summary>框外横滑条被拖动 → 驱动卡组框横向滚动。</summary>
    private void OnDeckHBarValueChanged(double value)
    {
        if (_syncingHBar || _deckScroll == null) return;
        _deckScroll.ScrollHorizontal = Mathf.RoundToInt((float)value);
    }

    /// <summary>
    /// 每帧把内建横滑条的范围/位置同步到框外条，并按"是否横向溢出"决定显隐。
    /// 每帧做（而不是只在重建后做一次）的原因：`MaxValue/Page` 要等 ScrollContainer 完成一次布局
    /// 才更新，`CallDeferred` 仍可能赶在布局之前；而这几行每帧成本可以忽略（需求 8：性能暂不优化）。
    /// </summary>
    private void SyncDeckHBarFromScroll()
    {
        if (_deckScroll == null || _deckHBar == null) return;
        var inner = _deckScroll.GetHScrollBar();
        if (inner == null) return;

        if (!Mathf.IsEqualApprox((float)_deckHBar.MaxValue, (float)inner.MaxValue)) _deckHBar.MaxValue = inner.MaxValue;
        if (!Mathf.IsEqualApprox((float)_deckHBar.Page, (float)inner.Page)) _deckHBar.Page = inner.Page;

        // 仅当内容横向溢出时显示（且不遮挡卡片：它在卡组框下沿之外）
        bool overflow = inner.MaxValue > inner.Page + 0.5f;
        if (_deckHBar.Visible != overflow) _deckHBar.Visible = overflow;

        // 滚轮在卡组框里滚动时，框外条跟随
        if (!_syncingHBar && Mathf.Abs((float)_deckHBar.Value - (float)inner.Value) > 0.6f)
        {
            _syncingHBar = true;
            _deckHBar.Value = inner.Value;
            _syncingHBar = false;
        }
    }

    // ============================================================
    // 【2026-10-17】卡组板悬浮解析
    // ============================================================

    /// <summary>
    /// 每帧解析"鼠标落在哪个同名组上"，然后显隐该组的 +/- 微按钮并弹整卡浮层。
    ///
    /// 为什么不用 `MouseEntered/Exited`：
    ///   · 悬浮时浮出的 +/- 微按钮是 `MouseFilter=Stop` 的子控件，指针移入子控件会让父节点收到
    ///     `mouse_exited` ⇒ 按钮会在鼠标按下去的瞬间消失；
    ///   · 同名组里卡片互相遮挡，逐卡绑定 entered/exited 还要处理"被遮挡的卡"的命中。
    /// 用组矩形的几何命中一次解决两件事（组矩形 = `(col*列宽, StartY, 卡宽, 组高)`，
    /// 与卡片实际占据区域完全一致）。
    /// </summary>
    private void UpdateDeckHover()
    {
        if (_hoverPreview == null || _deckBoard == null) return;

        Vector2 mouse = GetGlobalMousePosition();
        int hit = -1;
        for (int i = _groupNodes.Count - 1; i >= 0; i--)   // 后加的画在上层 ⇒ 倒序优先命中
        {
            var node = _groupNodes[i].Node;
            if (node == null || !GodotObject.IsInstanceValid(node) || !node.Visible) continue;
            if (node.GetGlobalRect().HasPoint(mouse)) { hit = i; break; }
        }

        if (hit < 0)
        {
            if (_hoverGroupNode != null)
            {
                SetGroupHoverChrome(_hoverGroupNode, false);
                _hoverGroupNode = null;
                _hoverPreview.HidePreview();
            }
            return;
        }

        var (group, hitNode) = _groupNodes[hit];
        if (!GodotObject.IsInstanceValid(hitNode)) { _hoverGroupNode = null; return; }
        if (ReferenceEquals(hitNode, _hoverGroupNode)) return;   // 没换组 ⇒ 什么都不用做

        if (_hoverGroupNode != null && GodotObject.IsInstanceValid(_hoverGroupNode))
            SetGroupHoverChrome(_hoverGroupNode, false);

        _hoverGroupNode = hitNode;
        SetGroupHoverChrome(hitNode, true);

        if (_boardData.TryGetValue(group.TemplatePath, out var data) && data != null)
            _hoverPreview.ShowFor((BaseCardData)data, hitNode.GlobalPosition);
    }

    /// <summary>切某个组的"悬浮态外观"：抬层 + 显隐 +/- 微按钮。</summary>
    private static void SetGroupHoverChrome(Control node, bool on)
    {
        if (node == null || !GodotObject.IsInstanceValid(node)) return;
        node.ZIndex = on ? 5 : 0;      // 抬到同列其它组之上（只在组间排序，不影响框外元素）
        foreach (var child in node.GetChildren())
            if (child is Button b && b.HasMeta("deck_micro")) b.Visible = on;
    }


    /// <summary>
    /// 【2026-10-17】卡组构成统计条：按颜色、费用分别统计**张数**（按份数累计）。
    ///
    ///   · 颜色：多色卡对它的每个颜色**各计一次** —— 与构筑限制（`CouncilRestriction.GetActualCount`）
    ///     的口径一致（那条规则也是"含该颜色即计入"），所以两处数字能对上；
    ///     代价是存在多色卡时，颜色合计可能大于总张数。
    ///   · 费用：按 `Cost` 分组、升序排列；`Cost &lt; 0`（不可直接打出）单独归为"不可打出"。
    ///
    /// 显示形如：<c>卡组构成： 黄25 蓝25 1费12 2费24 不可打出3</c>
    /// </summary>
    private void RefreshDeckStats()
    {
        if (_statsLabel == null) return;

        var colorCounts = new int[9];
        var costCounts = new Dictionary<int, int>();
        int unplayable = 0;
        bool any = false;

        foreach (var kv in _workingCards)
        {
            if (!_boardData.TryGetValue(kv.Key, out var data) || data == null) continue;
            any = true;
            int copies = kv.Value;

            bool anyColor = false;
            if (data.Colors != null)
            {
                foreach (var c in data.Colors)
                {
                    if (!System.Enum.IsDefined(typeof(CardColor), c)) continue;   // 过滤哨兵/脏值
                    int ci = (int)c;
                    if (ci < 0 || ci >= colorCounts.Length) continue;
                    colorCounts[ci] += copies;
                    anyColor = true;
                }
            }
            if (!anyColor) colorCounts[(int)CardColor.Brown] += copies;           // 无颜色按默认棕/中立

            if (data.Cost < 0) unplayable += copies;
            else costCounts[data.Cost] = (costCounts.TryGetValue(data.Cost, out int n) ? n : 0) + copies;
        }

        if (!any)
        {
            _statsLabel.Text = "卡组构成：—";
            return;
        }

        var sb = new System.Text.StringBuilder("卡组构成： ");
        for (int i = 0; i < colorCounts.Length; i++)
        {
            if (colorCounts[i] <= 0) continue;
            sb.Append(ColorCn[(CardColor)i]).Append(WebSafeText.Int(colorCounts[i])).Append(' ');
        }

        var costs = new List<int>(costCounts.Keys);
        costs.Sort();   // int 升序：Int32 默认比较器，不碰 ICU
        foreach (int c in costs)
            sb.Append(WebSafeText.Int(c)).Append('费').Append(WebSafeText.Int(costCounts[c])).Append(' ');

        if (unplayable > 0)
            sb.Append("不可打出").Append(WebSafeText.Int(unplayable)).Append(' ');

        _statsLabel.Text = sb.ToString().TrimEnd(' ');
    }

    private void RefreshDeckView()
    {
        ulong __t0 = Time.GetTicksMsec();
        _fillToken++;   // 作废可能在途的卡池渐进填充（它持有的是卡池行的节点，不能往卡组板上贴）

        // 【2026-10-17】卡组框改为"固定尺寸列式卡组板"：一次重建全部同名组节点（取代原来的逐行 HBox）
        RebuildDeckBoard();
        // 秘社信息条（卡组框之外；内容量保持原样：卡图 + 名称 + 移除）
        BuildCouncilInfoStrip();

        int total = GetDeckCardCount();
        _countLabel.Text = $"{total} 张";
        // 卡组构成统计条（颜色数量 + 费用数量），必须在 RebuildDeckBoard 之后调用：
        // 它复用那一趟已加载好的模板缓存 _boardData，不额外 Load 模板。
        RefreshDeckStats();

        GD.Print($"【DeckBuilder】刷新卡组视图：共 {total} 张 / 卡种 {_workingCards.Count} / 组 {_groupNodes.Count}");
        WebPerfParams.Summary(
            $"deck.view cards={total} kinds={_workingCards.Count} groups={_groupNodes.Count} "
            + $"total={Time.GetTicksMsec() - __t0}ms");

        // 【2026-10-12 Web 修复】原来是 `$"卡组{System.DateTime.Now:HHmmss}"`。
        // 自定义日期时间格式串会走 DateTimeFormatInfo → 全球化/ICU 路径，
        // 实测在魔改版 Web 运行时上让 wasm 直接 abort（本行正是 RefreshDeckView 的最后崩点）。
        // 改用 NowHHmmss()（引擎 Time 单例 + 纯手工取数字，见方法注释）。
        if (string.IsNullOrEmpty(_nameEdit.Text))
            _nameEdit.PlaceholderText = "卡组" + NowHHmmss();

        // 【秘社批次】每次卡组变动都重算构筑限制进度（展示 + 为保存拦截准备）
        RefreshRestrictionView();
    }

    /// <summary>
    /// 【2026-10-12 Web 修复】安全地取「当前时间 HHmmss」。
    ///
    /// 不能写 `System.DateTime.Now.ToString("HHmmss")`：带格式串的日期时间格式化会经过
    /// DateTimeFormatInfo（全球化/ICU），在魔改版 Web 运行时上会让 wasm 直接 abort。
    /// 这里改用 Godot 的 Time 单例取 "HH:MM:SS"（引擎返回 String，属已验证安全的那条封送路径），
    /// 再用纯 ASCII 数字筛选凑出 6 位数字——全程不碰任何全球化代码。
    /// 桌面端结果与原先一致（同为本地时间的 HHmmss）。
    /// </summary>
    private static string NowHHmmss()
    {
        try
        {
            string t = Godot.Time.GetTimeStringFromSystem();
            if (!string.IsNullOrEmpty(t))
            {
                string digits = "";
                for (int i = 0; i < t.Length && digits.Length < 6; i++)
                {
                    char c = t[i];
                    if (c >= '0' && c <= '9') digits += c;
                }
                if (digits.Length == 6) return digits;
            }
        }
        catch (System.Exception)
        {
            // 引擎接口不可用时退化为固定后缀（仅影响默认卡组名，不影响功能）
        }
        return "000000";
    }

    // ============================================================
    // 加载 / 保存 / 返回
    // ============================================================
    /// <summary>
    /// 载入卡组文件（唯一入口：下拉列表点选、以及从主菜单/房间带入的编辑目标）。
    /// 除了打开卡组，**同时把卡组名回填到输入框**（需求 1）；保存时按这个名字覆盖同名文件。
    /// </summary>
    private void LoadDeckFromFile(string path)
    {
        var deck = DeckListLoader.Load(path);
        if (deck == null) return;
        _workingCards.Clear();
        foreach (var e in deck.Cards) _workingCards[e.TemplatePath] = e.Copies;
        // 文件名兜底：老卡组 JSON 可能没有 deckName，此时用文件名当卡组名（否则输入框一片空白）
        _nameEdit.Text = WebSafeText.IsNullOrWhiteSpaceAscii(deck.DeckName)
            ? System.IO.Path.GetFileNameWithoutExtension(path)
            : deck.DeckName;
        if (deck.Councils.Count > 0) _workingCouncil = deck.Councils[0].TemplatePath;
        RefreshDeckView();
        GD.Print($"【DeckBuilder】已载入卡组：{path}");
    }

    // ============================================================
    // 【秘社批次】构筑限制：展示 + 保存拦截
    // ============================================================
    /// <summary>
    /// 【秘社批次】运行期创建"秘社限制"面板。
    /// 【2026-10-17 卡组框重做】改挂在 **卡组框之外** 的信息带里（`DeckPanel/InfoPanel/RestrictionScroll`）：
    /// 卡组板现在要独占整个卡组框（列高 = 框可视高度），不能再用 `DeckScroll.OffsetTop = 56` 给它让位。
    /// 明细过长时由外层 ScrollContainer 滚动，不挤压卡组框。
    /// </summary>
    private void BuildRestrictionPanel()
    {
        var host = GetNodeOrNull<ScrollContainer>("DeckPanel/InfoPanel/RestrictionScroll");
        if (host == null) return;

        _restrictionBox = new VBoxContainer { Name = "RestrictionBox", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _restrictionBox.AddThemeConstantOverride("separation", 2);
        host.AddChild(_restrictionBox);

        _restrictionSummary = new Label { Name = "RestrictionSummary", Text = "" };
        _restrictionSummary.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        _restrictionBox.AddChild(_restrictionSummary);
    }

    /// <summary>
    /// 重算并渲染当前秘社的构筑限制进度。逐条显示 `Describe(actual)`；满足=绿色、不满足=红色。
    /// 返回未满足的明细列表（空＝全部满足）——`CollectDeckViolations` 用它汇总违规原因。
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
            // 【2026-10-12】--wp-nowrap 时退回按字符换行（理由见 MakeTextLine 注释）
            line.AutowrapMode = WebPerfParams.NoWordWrap
                ? TextServer.AutowrapMode.Arbitrary
                : TextServer.AutowrapMode.WordSmart;
            _restrictionBox.AddChild(line);
        }

        _restrictionSummary.Text = $"秘社「{council.CardName}」限制：{(allOk ? "已达成" : "未达成")}";
        _restrictionSummary.Modulate = allOk ? new Color(0.55f, 0.95f, 0.55f) : new Color(1f, 0.6f, 0.4f);
        return result;
    }

    /// <summary>卡组内卡牌的总张数（按份数累计，不含秘社卡）。</summary>
    private int GetDeckCardCount()
    {
        int total = 0;
        foreach (var kv in _workingCards)
            total += kv.Value;
        return total;
    }

    /// <summary>
    /// 汇总当前卡组的违规原因（空列表＝完全合法）。
    /// 违规仅用于提示，**不再直接拦截保存**：用户在确认框里点"确认"后仍可落盘。
    /// </summary>
    private List<string> CollectDeckViolations()
    {
        var reasons = new List<string>();

        // ① 不符合秘社构筑规则：逐条列出未达成的限制明细
        var failures = RefreshRestrictionView();
        var bad = failures.FindAll(f => !f.ok);
        if (bad.Count > 0)
            reasons.Add("不符合秘社构筑规则：" + string.Join("；", bad.ConvertAll(f => f.text)));

        // ② 卡组数量不足
        int total = GetDeckCardCount();
        if (total < MinDeckSize)
            reasons.Add($"卡组数量不足：当前 {total} 张，至少需要 {MinDeckSize} 张");

        return reasons;
    }

    private void OnSave()
    {
        // 【2026-10-12 Web 修复】原来用 $"{DateTime.Now:HHmmss}"，格式化会走全球化路径 → Web 上 abort；
        // 统一改用 NowHHmmss()。（.Trim() 只裁 ASCII 空白，走共享的 Web 安全实现）
        string name = string.IsNullOrWhiteSpace(_nameEdit.Text) ? "卡组" + NowHHmmss() : WebSafeText.TrimAscii(_nameEdit.Text);

        // 【违规校验】非法卡组不再被直接拦截，而是先弹窗说明原因，由用户决定是否继续保存。
        var violations = CollectDeckViolations();
        if (violations.Count > 0)
        {
            GD.Print($"【DeckBuilder】⚠ 违规卡组待用户确认：{string.Join(" | ", violations)}");
            ConfirmSaveViolation(name, violations);
            return;
        }

        SaveDeck(name);
    }

    /// <summary>弹出"确认 / 取消"双按钮提示框，说明违规原因；确认才真正落盘。</summary>
    private void ConfirmSaveViolation(string name, List<string> violations)
    {
        var detail = string.Join("\n", violations.ConvertAll(v => "· " + v));
        var dlg = new ConfirmationDialog
        {
            Title = "卡组违规确认",
            DialogText = $"该卡组存在以下问题：\n{detail}\n\n是否仍然保存？",
            OkButtonText = "确认",
            CancelButtonText = "取消",
            Exclusive = true,
            Unresizable = false,
        };
        AddChild(dlg);
        dlg.Confirmed += () => { SaveDeck(name); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        dlg.CloseRequested += () => dlg.QueueFree();
        dlg.PopupCentered(new Vector2I(560, 240));
    }

    /// <summary>真正把编辑中的卡组写入 ../Decks。</summary>
    private void SaveDeck(string name)
    {
        string display = string.IsNullOrWhiteSpace(name) ? "卡组" + NowHHmmss() : WebSafeText.TrimAscii(name);

        var deck = new DeckList
        {
            DeckName = display,
        };
        foreach (var kv in _workingCards)
            deck.Cards.Add(new DeckList.DeckEntry { TemplatePath = kv.Key, Copies = kv.Value });
        // 秘社单独成列（不在 Cards 里，避免被当作可抽取的卡牌）；未选择秘社则不写
        if (!string.IsNullOrEmpty(_workingCouncil))
            deck.Councils.Add(new DeckList.DeckEntry { TemplatePath = _workingCouncil, Copies = 1 });

        string path = DecksDir + "/" + SanitizeFileName(display) + ".json";
        DeckListLoader.Save(deck, path);
        // 输入框回填"实际用于文件名的名字"：名字里若有非法文件名字符会被替换成下划线，
        // 回填后下次保存仍命中同一个文件（真正实现"按卡组名覆盖"）。
        _nameEdit.Text = display;
        // 【2026-10-20】刚保存的卡组即成为新的"默认卡组"（写盘 + 同步 GameManager）。
        // 原先是直接写 GameManager 两个字段，现在统一走 DeckSelection，保证与房间页口径一致。
        DeckSelection.Remember(path);
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

    // ============================================================
    // 【卡池结构化检索 · 2026-10-17】高级筛选面板
    //   运行期动态创建（沿用 BuildRestrictionPanel 的惯例，避免 .tscn 与字段漂移）。
    //   布局：FilterModeBar / SearchBox（既有，保持原位）→ FilterToggleBar → FilterBox → PoolScroll。
    //   展开时按像素把 PoolScroll 下推（而非改 .tscn 锚点），并给 PoolScroll 保底 ≥150px。
    // ============================================================
    private HBoxContainer _filterToggleBar;
    private Button _filterToggleButton;
    private ScrollContainer _filterBoxScroll;
    private VBoxContainer _filterBox;
    private LineEdit _fUidBox;
    private LineEdit _fSubNameBox;
    private LineEdit _fSubCatBox;
    private OptionButton _fCategoryOption;
    private OptionButton _fDomainOption;
    private OptionButton _fColorOption;
    private OptionButton _fOrgOption;
    /// <summary>组织下拉项与索引一一对应：下标 0 = 「不限」（空串），其余为索引里去重后的组织名。</summary>
    private readonly List<string> _orgOptions = new List<string>();
    private CheckBox _fUniqueCheck;
    private readonly LineEdit[] _fAttrEdits = new LineEdit[DeckFilterModel.AttrCount];
    private Label _fLoyaltyDisplay;
    private Button _fAddLoyaltyButton;
    private Button _fClearLoyaltyButton;
    private Button _fSearchButton;
    private ScrollContainer _poolScroll;
    private bool _filterExpanded;

    // 忠诚添加弹窗
    private PopupPanel _loyaltyPopup;
    private OptionButton _loyaltyTargetOption;
    private LineEdit _loyaltyAmountEdit;

    /// <summary>类别下拉：索引 → 位掩码（0 = 不限）。匹配是"按位包含"，故只需基础身份位。</summary>
    private static readonly int[] FilterCategoryMasks = { 0, 1, 2, 4, 8, 16, 32 };
    private static readonly string[] FilterCategoryNames = { "不限", "角色", "事务", "附属", "任务", "地区", "指示物" };

    /// <summary>领域下拉：索引 → 值（-2 = 不限，-1 = 无领域，0..4 = 具体领域）。</summary>
    private static readonly int[] FilterDomainValues = { -2, -1, 0, 1, 2, 3, 4 };
    private static readonly string[] FilterDomainNames = { "不限", "无领域", "心灵", "神圣", "星空", "死亡", "鲜血" };

    /// <summary>颜色下拉：下标 0 = 不限，下标 i&gt;0 对应枚举值 i-1（棕黄白绿灰紫蓝黑红）。</summary>
    private static readonly string[] FilterColorNames = { "不限", "棕", "黄", "白", "绿", "灰", "紫", "蓝", "黑", "红" };

    /// <summary>属性输入框标签（顺序必须与 DeckFilterModel.Attr* 常量一致）。</summary>
    private static readonly string[] FilterAttrNames =
        { "调查≥", "战斗≥", "势力≥", "防御≥", "先手调查≥", "先手战斗≥", "先手势力≥" };

    /// <summary>
    /// 7 个属性在 2 列网格里的**显示顺序**（用户 2026-10-17 口径）：
    ///   调查 / 先手调查 同一行；战斗 / 先手战斗 同一行；势力 / 先手势力 同一行；防御单独一行。
    /// 注意：这里只决定"摆在哪一格"，`_fAttrEdits` 仍按 `DeckFilterModel.Attr*` 下标存放，
    /// 故取值/回填逻辑不受影响。
    /// </summary>
    private static readonly int[] FilterAttrDisplayOrder =
    {
        DeckFilterModel.AttrInvestigation, DeckFilterModel.AttrFsInvestigation,
        DeckFilterModel.AttrStrength,      DeckFilterModel.AttrFsStrength,
        DeckFilterModel.AttrPower,         DeckFilterModel.AttrFsPower,
        DeckFilterModel.AttrDefense,
    };

    private void BuildFilterPanel()
    {
        var poolPanel = GetNodeOrNull<Control>("PoolPanel");
        if (poolPanel == null)
        {
            GD.PrintErr("【DeckBuilder】找不到 PoolPanel，跳过高级筛选面板构建");
            return;
        }
        _poolScroll = poolPanel.GetNodeOrNull<ScrollContainer>("PoolScroll");

        // ---- 折叠开关行 ----
        _filterToggleBar = new HBoxContainer { Name = "FilterToggleBar" };
        _filterToggleBar.AddThemeConstantOverride("separation", 8);
        poolPanel.AddChild(_filterToggleBar);

        _filterToggleButton = new Button { Text = "▼ 高级筛选（类别 / 属性 / 忠诚）", ToggleMode = true };
        _filterToggleButton.Toggled += OnFilterToggleToggled;
        _filterToggleBar.AddChild(_filterToggleButton);

        // ---- 筛选内容（放在 ScrollContainer 里：内容过高时可滚动，保证不把卡池挤没）----
        _filterBoxScroll = new ScrollContainer { Name = "FilterBoxScroll", Visible = false };
        poolPanel.AddChild(_filterBoxScroll);

        _filterBox = new VBoxContainer { Name = "FilterBox" };
        _filterBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _filterBox.AddThemeConstantOverride("separation", 4);
        _filterBoxScroll.AddChild(_filterBox);

        // 第 1 行：卡号 / 副卡名
        // 【2026-10-17 · 检索目标互换】这里原来是"卡名"（与顶部搜索框重复）；
        // 现在顶部搜索框改成按卡名过滤，本格改成**按卡号**过滤（文件名/UID 子串，如 JC001）。
        var row1 = MkRow();
        row1.AddChild(MkLabel("卡号", 52));
        _fUidBox = MkEdit("编号包含…如 JC001");
        _fUidBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row1.AddChild(_fUidBox);
        row1.AddChild(MkLabel("副卡名", 48));
        _fSubNameBox = MkEdit("暂无数据");
        _fSubNameBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row1.AddChild(_fSubNameBox);
        _filterBox.AddChild(row1);

        // 第 2 行：类别 / 领域 / 颜色 / 组织 / 仅独有卡
        // 一行 9 个控件，宽度紧：标签压到 30px，下拉给较小最小宽 + ExpandFill 平分剩余空间。
        var row2 = MkRow(4);
        row2.AddChild(MkLabel("类别", 30));
        _fCategoryOption = MkOption(56);
        _fCategoryOption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (int i = 0; i < FilterCategoryNames.Length; i++) _fCategoryOption.AddItem(FilterCategoryNames[i]);
        _fCategoryOption.Select(0);
        row2.AddChild(_fCategoryOption);

        row2.AddChild(MkLabel("领域", 30));
        _fDomainOption = MkOption(56);
        _fDomainOption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (int i = 0; i < FilterDomainNames.Length; i++) _fDomainOption.AddItem(FilterDomainNames[i]);
        _fDomainOption.Select(0);
        row2.AddChild(_fDomainOption);

        row2.AddChild(MkLabel("颜色", 30));
        _fColorOption = MkOption(56);
        _fColorOption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        for (int i = 0; i < FilterColorNames.Length; i++) _fColorOption.AddItem(FilterColorNames[i]);
        _fColorOption.Select(0);
        row2.AddChild(_fColorOption);

        row2.AddChild(MkLabel("组织", 30));
        _fOrgOption = MkOption(64);
        _fOrgOption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // 组织项依赖索引（构建期抽出的去重组织名），先只放「不限」，索引就绪后由 RefreshOrgOptions 重建
        _orgOptions.Add("");
        _fOrgOption.AddItem("不限");
        _fOrgOption.Select(0);
        row2.AddChild(_fOrgOption);

        _fUniqueCheck = new CheckBox { Text = "仅独有卡" };
        _fUniqueCheck.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        row2.AddChild(_fUniqueCheck);
        _filterBox.AddChild(row2);

        // 第 3 行：子类别
        var row3 = MkRow();
        row3.AddChild(MkLabel("子类别", 52));
        _fSubCatBox = MkEdit("如 人类 / 法师");
        _fSubCatBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row3.AddChild(_fSubCatBox);
        _filterBox.AddChild(row3);

        // 第 4 行区：7 个属性（≥ 语义）——按 FilterAttrDisplayOrder 摆位：
        //   调查/先手调查 · 战斗/先手战斗 · 势力/先手势力 各占一行，防御单独一行
        var attrGrid = new GridContainer { Columns = 2 };
        attrGrid.AddThemeConstantOverride("h_separation", 10);
        attrGrid.AddThemeConstantOverride("v_separation", 2);
        foreach (int ai in FilterAttrDisplayOrder)
        {
            var cell = MkRow(4);
            cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            cell.AddChild(MkLabel(FilterAttrNames[ai], 68));
            _fAttrEdits[ai] = MkNumEdit();
            cell.AddChild(_fAttrEdits[ai]);
            attrGrid.AddChild(cell);
        }
        _filterBox.AddChild(attrGrid);

        // 第 5 行：忠诚（显示框 + 添加 + 清空）
        var row5 = MkRow();
        row5.AddChild(MkLabel("忠诚", 52));
        _fLoyaltyDisplay = new Label { Text = "（未设置）", VerticalAlignment = VerticalAlignment.Center };
        _fLoyaltyDisplay.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _fLoyaltyDisplay.ClipText = true;
        row5.AddChild(_fLoyaltyDisplay);
        _fAddLoyaltyButton = new Button { Text = "添加忠诚" };
        _fAddLoyaltyButton.Pressed += OpenLoyaltyPopup;
        row5.AddChild(_fAddLoyaltyButton);
        _fClearLoyaltyButton = new Button { Text = "清空" };
        _fClearLoyaltyButton.Pressed += OnLoyaltyClear;
        row5.AddChild(_fClearLoyaltyButton);
        _filterBox.AddChild(row5);

        // 第 6 行：搜索
        var row6 = MkRow();
        _fSearchButton = new Button { Text = "搜索" };
        _fSearchButton.Pressed += OnFilterSearch;
        row6.AddChild(_fSearchButton);
        var hint = new Label
        {
            Text = "编号框实时过滤；其余条件点「搜索」生效",
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.Modulate = new Color(0.85f, 0.85f, 0.9f);
        row6.AddChild(hint);
        _filterBox.AddChild(row6);

        // 面板尺寸变化时重新排布
        poolPanel.Resized += ReflowPoolTop;
    }

    /// <summary>
    /// 幂等地确保卡池检索索引已加载（只在真正用到结构化条件时调用，编号搜索零额外成本）。
    /// 失败不致命：_indexReady=false 时 <see cref="ApplyPoolFilter"/> 回退到按文件名子串过滤。
    /// </summary>
    private void EnsureIndexForStructured()
    {
        if (_indexTried) return;
        _indexTried = true;
        _indexReady = CardSearchIndex.EnsureLoaded();

        // 【索引过期告警】索引条数应与实际扫到的卡池数一致；不一致说明卡表改了但没重跑生成
        //（打包脚本每次导出前会强制重生成，这里只做运行时兜底提示，不阻断功能）。
        if (_indexReady && CardSearchIndex.Count != _allPoolPaths.Count)
            GD.PrintErr($"【DeckBuilder】卡池索引疑似过期：索引 {CardSearchIndex.Count} 条 ≠ 卡池 {_allPoolPaths.Count} 张；请重跑 CardIndexRunner");

        // 索引就绪后重建组织下拉（项来自索引里去重后的组织名）
        if (_indexReady) RefreshOrgOptions();
    }

    /// <summary>
    /// 组织下拉的动态项：索引就绪后按去重组织名重建；索引未就绪时只有「不限」。
    /// 重建时尽量保留用户已选的组织。
    /// </summary>
    private void RefreshOrgOptions()
    {
        if (_fOrgOption == null) return;

        int sel = _fOrgOption.Selected;
        string cur = (sel > 0 && sel < _orgOptions.Count) ? _orgOptions[sel] : null;

        _orgOptions.Clear();
        _fOrgOption.Clear();
        _orgOptions.Add("");                 // 下标 0 = 不限（空串代表不过滤）
        _fOrgOption.AddItem("不限");

        if (CardSearchIndex.Available)
        {
            var set = new List<string>();
            var cards = CardSearchIndex.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                string o = cards[i].Organization;
                if (WebSafeText.IsNullOrWhiteSpaceAscii(o)) continue;
                if (IndexOfOrdinal(set, o) < 0) set.Add(o);
            }
            // 【Web 安全】显式序数比较器：无参 Sort() 走文化敏感路径（ICU），Web 上会 abort。
            set.Sort(System.StringComparer.Ordinal);
            for (int i = 0; i < set.Count; i++)
            {
                _orgOptions.Add(set[i]);
                _fOrgOption.AddItem(set[i]);
            }
        }

        int restore = 0;
        if (cur != null)
        {
            int k = IndexOfOrdinal(_orgOptions, cur);
            if (k > 0) restore = k;
        }
        _fOrgOption.Select(restore);

        WebPerfParams.Log("deck.filter orgOptions=" + WebSafeText.Int(_orgOptions.Count - 1));
    }

    private static int IndexOfOrdinal(List<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], v, System.StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>进屏后延迟预热索引：不阻塞 _Ready，也不让"首次结构化搜索"承担 ~170ms 的反序列化。</summary>
    private void StartIndexWarmup()
    {
        if (_indexTried) return;
        var t = new Timer { WaitTime = 0.4f, OneShot = true };
        AddChild(t);
        t.Timeout += () =>
        {
            t.QueueFree();
            EnsureIndexForStructured();
        };
        t.Start();
    }

    private static HBoxContainer MkRow(int sep = 6)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        return h;
    }

    private static Label MkLabel(string text, int minW = 0)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (minW > 0) l.CustomMinimumSize = new Vector2(minW, 0);
        return l;
    }

    private static LineEdit MkEdit(string placeholder)
    {
        return new LineEdit { PlaceholderText = placeholder };
    }

    private static OptionButton MkOption(int minW)
    {
        var o = new OptionButton();
        o.CustomMinimumSize = new Vector2(minW, 0);
        return o;
    }

    /// <summary>只允许数字的输入框（**禁用 SpinBox**：其数字格式化走 NumberFormatInfo，Web 上会 abort）。</summary>
    private static LineEdit MkNumEdit()
    {
        var e = new LineEdit { Alignment = HorizontalAlignment.Center };
        e.CustomMinimumSize = new Vector2(44, 0);
        e.TextChanged += s =>
        {
            string t = OnlyDigits(s);
            if (!string.Equals(t, s, System.StringComparison.Ordinal))
            {
                e.Text = t;
                e.CaretColumn = t.Length;
            }
        };
        return e;
    }

    /// <summary>剔除非 '0'..'9' 字符（不用 Char.IsDigit —— 会碰 Unicode 类别表）。</summary>
    private static string OnlyDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        bool ok = true;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') { ok = false; break; }
        }
        if (ok) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') sb.Append(c);
        }
        return sb.ToString();
    }

    private void OnFilterToggleToggled(bool pressed)
    {
        _filterExpanded = pressed;
        if (_filterToggleButton != null)
            _filterToggleButton.Text = pressed ? "▲ 收起筛选" : "▼ 高级筛选（类别 / 属性 / 忠诚）";
        if (_filterBoxScroll != null) _filterBoxScroll.Visible = pressed;
        // 组织项来自索引：展开时若索引已就绪但下拉还没填充过，补一次
        if (pressed) RefreshOrgOptions();
        // 等这一帧布局完成后再量内容高度
        Callable.From(ReflowPoolTop).CallDeferred();
    }

    /// <summary>
    /// 按像素重排顶部区域：FilterModeBar / SearchBox 保持原锚点，FilterToggleBar 与 FilterBox 紧随其后，
    /// PoolScroll 顶边下推（并保证可视高度 ≥ 150px，不够时让 FilterBox 内部滚动）。
    /// </summary>
    private void ReflowPoolTop()
    {
        var poolPanel = GetNodeOrNull<Control>("PoolPanel");
        if (poolPanel == null || _filterToggleBar == null || _searchBox == null) return;

        float w = poolPanel.Size.X;
        float h = poolPanel.Size.Y;
        if (w <= 1f || h <= 1f) return;   // 尚未完成布局

        float y = _searchBox.Position.Y + _searchBox.Size.Y + 4f;

        float toggleH = Mathf.Max(26f, _filterToggleBar.GetCombinedMinimumSize().Y);
        _filterToggleBar.Position = new Vector2(6f, y);
        _filterToggleBar.Size = new Vector2(w - 12f, toggleH);

        float yBoxTop = y + toggleH + 4f;

        // 给 PoolScroll 保底 150px（PoolPager 固定在底部 -42..-6，故卡池下界 = h - 46）
        float scrollTopLimit = (h - 46f) - 150f;
        float maxBoxH = Mathf.Max(60f, scrollTopLimit - yBoxTop - 6f);

        float contentH = 0f;
        if (_filterBox != null && _filterExpanded)
        {
            _filterBox.Size = new Vector2(w - 12f, 0f);           // 触发按宽度重算换行/最小高
            contentH = _filterBox.GetCombinedMinimumSize().Y;
        }
        float boxH = _filterExpanded ? Mathf.Min(contentH, maxBoxH) : 0f;

        if (_filterBoxScroll != null)
        {
            _filterBoxScroll.Visible = _filterExpanded && boxH > 0.5f;
            _filterBoxScroll.Position = new Vector2(6f, yBoxTop);
            _filterBoxScroll.Size = new Vector2(w - 12f, boxH);
        }

        if (_poolScroll != null)
        {
            _poolScroll.AnchorTop = 0f;                            // 改像素驱动，顶部锚点归零
            _poolScroll.OffsetTop = yBoxTop + boxH + 6f;
        }

        // 【G3 量化埋点】展开/收起后卡池可视高度是关键验收项（要求 ≥150px）
        WebPerfParams.Log("deck.layout panelH=" + WebSafeText.Int((int)h)
            + " toggleH=" + WebSafeText.Int((int)toggleH)
            + " filterBoxH=" + WebSafeText.Int((int)boxH)
            + " contentH=" + WebSafeText.Int((int)contentH)
            + " poolScrollH=" + WebSafeText.Int((int)((h - 46f) - (yBoxTop + boxH + 6f)))
            + " expanded=" + (_filterExpanded ? "1" : "0"));
    }

    // ---------------- 忠诚条件弹窗 ----------------
    private void OpenLoyaltyPopup()
    {
        if (_loyaltyPopup == null) BuildLoyaltyPopup();
        _loyaltyTargetOption.Select(0);
        _loyaltyAmountEdit.Text = "";
        _loyaltyPopup.PopupCentered(new Vector2I(300, 160));
    }

    private void BuildLoyaltyPopup()
    {
        _loyaltyPopup = new PopupPanel { Name = "AddLoyaltyPopup" };
        AddChild(_loyaltyPopup);

        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", 8);
        _loyaltyPopup.AddChild(vb);

        vb.AddChild(new Label { Text = "添加忠诚条件（颜色 / 魔法领域）" });

        _loyaltyTargetOption = new OptionButton();
        // 前 9 项 = CardColor（索引即枚举值 0..8），后 5 项 = MagicDomain（索引-9 = 枚举值 0..4）
        for (int i = 0; i < 9; i++)
            _loyaltyTargetOption.AddItem(ColorCn[(CardColor)i]);
        for (int i = 0; i < 5; i++)
            _loyaltyTargetOption.AddItem(DomainCn[(MagicDomain)i] + "（领域）");
        _loyaltyTargetOption.Select(0);
        vb.AddChild(_loyaltyTargetOption);

        _loyaltyAmountEdit = MkNumEdit();
        _loyaltyAmountEdit.Alignment = HorizontalAlignment.Left;
        _loyaltyAmountEdit.PlaceholderText = "点数（要求 ≥）";
        vb.AddChild(_loyaltyAmountEdit);

        var row = MkRow();
        var ok = new Button { Text = "确定" };
        ok.Pressed += OnLoyaltyConfirm;
        var cancel = new Button { Text = "取消" };
        cancel.Pressed += () => _loyaltyPopup.Hide();
        row.AddChild(ok);
        row.AddChild(cancel);
        vb.AddChild(row);
    }

    private void OnLoyaltyConfirm()
    {
        int idx = _loyaltyTargetOption.Selected;
        int amount;
        if (!WebSafeText.TryParseInt(_loyaltyAmountEdit.Text, out amount) || amount <= 0)
        {
            // 非法点数：不添加，清空输入让用户重填
            _loyaltyAmountEdit.Text = "";
            return;
        }

        bool isColor = idx <= 8;
        int value = isColor ? idx : idx - 9;
        _filter.Loyalties.Add(new LoyaltyCond { IsColor = isColor, Value = value, Amount = amount });
        RefreshLoyaltyDisplay();
        _loyaltyPopup.Hide();
    }

    private void OnLoyaltyClear()
    {
        _filter.Loyalties.Clear();
        RefreshLoyaltyDisplay();
    }

    private void RefreshLoyaltyDisplay()
    {
        if (_fLoyaltyDisplay == null) return;
        if (_filter.Loyalties.Count == 0)
        {
            _fLoyaltyDisplay.Text = "（未设置）";
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var c in _filter.Loyalties)
        {
            if (sb.Length > 0) sb.Append(" ; ");
            string name = c.IsColor ? ColorCn[(CardColor)c.Value] : DomainCn[(MagicDomain)c.Value];
            sb.Append(name).Append('≥').Append(WebSafeText.Int(c.Amount));
        }
        _fLoyaltyDisplay.Text = sb.ToString();
    }

    /// <summary>点「搜索」：把面板上的条件读进 _filter，然后重新过滤 + 重绘卡池。</summary>
    private void OnFilterSearch()
    {
        // 卡名由顶部搜索框实时提供（ApplyPoolFilter 里写 _filter.NameQuery），本面板只管卡号
        _filter.UidQuery = _fUidBox.Text ?? "";
        _filter.SubCardNameQuery = _fSubNameBox.Text ?? "";
        _filter.SubCategoryQuery = _fSubCatBox.Text ?? "";

        int ci = _fCategoryOption.Selected;
        _filter.CategoryMask = (ci >= 0 && ci < FilterCategoryMasks.Length) ? FilterCategoryMasks[ci] : 0;
        int di = _fDomainOption.Selected;
        _filter.DomainFilter = (di >= 0 && di < FilterDomainValues.Length) ? FilterDomainValues[di] : DeckFilterModel.DomainAny;

        int ci2 = _fColorOption.Selected;
        _filter.ColorFilter = (ci2 > 0 && ci2 <= 9) ? ci2 - 1 : -1;

        int oi = _fOrgOption.Selected;
        _filter.OrganizationQuery = (oi > 0 && oi < _orgOptions.Count) ? _orgOptions[oi] : "";

        _filter.UniqueOnly = _fUniqueCheck.ButtonPressed;

        for (int i = 0; i < DeckFilterModel.AttrCount; i++)
        {
            int v;
            bool ok = WebSafeText.TryParseInt(_fAttrEdits[i].Text, out v) && v >= 0;
            _filter.MinAttr[i] = ok ? v : -1;
        }

        ApplyPoolFilter();
        RefreshPoolView();
    }
}
