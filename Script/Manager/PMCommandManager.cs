using Godot;
using Script.Data.Effects;
using Script.Game.Entities;
using Script.Enums;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// PM 调试命令管理器（Manager 层）。
/// 【MVC 边界修复】本管理器不再引用任何 Script.UI 类型（AreaSlot/CardUI），
/// 地区/手牌定位改为依赖 Model 侧的纯数据（AreaUID 列表、CardManager 的 Model 列表），
/// 仅通过 Arena 注入的回调刷新 UI，符合"Manager 不依赖 View 具体类型"的约束（问题 2 修复）。
/// </summary>
public partial class PMCommandManager : Node
{
    private CardManager _cardManager;
    private DeckManager _deckManager;
    // 地区 UID 列表（实时查询，由 Arena 注入 Func<>，每次命令执行时按槽位顺序取当前 UID）。
    // 方案②：不再持有初始化时的快照，避免地区赢取/替换后 UID 过期导致命令落到失效槽位。
    private Func<List<string>> _getAreaUids;
    private Action<int, int> _onDraw;
    private ZoneManager _zoneManager;
    private Action _onZoneChanged;
    // 仅通知 UI 刷新指定卡（不返回/持有 UI 实例），由 Arena 注入
    private Action<string> _refreshCardUI;
    // 读取/设置卡的翻面状态（由 Arena 注入，内部查 UI，PM 不持有 UI 类型）
    private Func<string, bool> _getFaceUp;
    private Action<string, bool> _setFaceUp;
    // 打开对抗窗口的回调（由 Arena 注入，内部路由到 CombatManager 事件 → UI）。
    // PM 命令管理器不直接持有/调用 Arena（UI）节点，符合 MVC 边界约束。
    private Action<CombatManager.CombatType, int, int, int> _openCombatWindow;
    private AreaManager _areaManager;
    private StackManager _stackManager;   // 供 AddPowerMark 后触发赢取地区检查（TryStartAreaWin 需堆叠引用）
    // 从世界牌库抽一张地区卡并新建 AreaSlot（由 Arena 注入，返回是否成功）
    private Func<bool> _drawAndAddNewArea;

    public void Initialize(
        CardManager cardManager,
        DeckManager deckManager,
        Func<List<string>> getAreaUids,
        Action<int, int> onDraw,
        ZoneManager zoneManager,
        Action onZoneChanged = null,
        Action<string> refreshCardUI = null,
        Func<string, bool> getFaceUp = null,
        Action<string, bool> setFaceUp = null,
        Action<CombatManager.CombatType, int, int, int> openCombatWindow = null,
        AreaManager areaManager = null,
        Func<bool> drawAndAddNewArea = null,
        StackManager stackManager = null)
    {
        _drawAndAddNewArea = drawAndAddNewArea;
        _cardManager = cardManager;
        _deckManager = deckManager;
        _getAreaUids = getAreaUids;
        _onDraw = onDraw;
        _zoneManager = zoneManager;
        _onZoneChanged = onZoneChanged;
        _refreshCardUI = refreshCardUI;
        _getFaceUp = getFaceUp;
        _setFaceUp = setFaceUp;
        _openCombatWindow = openCombatWindow;
        _areaManager = areaManager;
        _stackManager = stackManager;
    }

    /// <summary>
    /// 【2026-09】只注入**模型侧依赖**（卡/牌库/区域管理器）：由 Composition Root（`GameInitializer`）在创建时
    /// 就地接线，使 PM 命令在**没有 View** 的场景（headless 测试 / 纯服务端）同样可用；
    /// View 侧的 UI 回调（抽牌回调、刷新回调等）仍由 `Initialize` 注入，且 `Initialize` 会覆盖这里设的值。
    /// </summary>
    public void InjectModelRefs(CardManager cardManager, DeckManager deckManager, ZoneManager zoneManager,
                               AreaManager areaManager = null)
    {
        _cardManager ??= cardManager;
        _deckManager ??= deckManager;
        _zoneManager ??= zoneManager;
        // 【2026-10 修复】`power` 命令依赖 AreaManager 加势力标记；此前只注入前三个 ⇒
        // headless / 纯服务端下 `power` 恒报"AreaManager 未初始化"（什么都不做）。
        _areaManager ??= areaManager;
        AreaManagerRef ??= areaManager;
    }

    /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
    public Script.Manager.EffectEngine EffectEngineRef { get; set; }

    /// <summary>显式注入的 TurnManager 引用（`jumpto` 跳转步骤 / 定向取牌从牌库移出用）。</summary>
    public TurnManager TurnManagerRef { get; set; }

    /// <summary>
    /// 显式注入的 CombatManager 引用（2026-10）：联机服务端（无 Arena 本机窗口）下
    /// `investigation` / `battle` 经它把对抗窗口**经决策协议下发**给玩家，并按正规结算链结算。
    /// </summary>
    public CombatManager CombatManagerRef { get; set; }

    /// <summary>
    /// 显式注入的 AreaManager 引用（2026-10）：`_getAreaUids` 回调缺省时（headless / 纯服务端 /
    /// 测试装配）用它取"按槽位顺序的地区 UID 列表"，避免地区校验恒失败。
    /// </summary>
    public AreaManager AreaManagerRef { get; set; }

    /// <summary>定向取牌（`draw &lt;数量&gt; &lt;卡牌编号&gt;`）挑选"多张同名同编号"用的随机源。</summary>
    private readonly System.Random _rng = new System.Random();

    /// <summary>未显式注入时回退静态 Instance（向后兼容）。</summary>
    private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;

    /// <summary>实时获取当前按槽位顺序的地区 UID 列表（方案②：每次命令执行时查询，不持有过期快照）。
    /// 【2026-10 修复】View 侧回调（Arena/MatchHost 经 `Initialize` 注入）缺省时回退 `AreaManagerRef`：
    /// 否则 headless / 纯服务端场景下地区列表恒为空 ⇒ `investigation`/`battle`/`power` 的地区校验全部失败
    /// （表现为"命令什么都不做"）。</summary>
    private List<string> CurrentAreaUids =>
        _getAreaUids != null ? _getAreaUids()
        : (AreaManagerRef != null ? AreaManagerRef.GetOrderedAreaUids() : new List<string>());

    /// <summary>
    /// 阻塞类命令：会打开对抗窗口并等待玩家响应。
    /// 批量执行时，遇到这类命令执行完即停止，后续命令全部丢弃。
    /// 【2026-10 修正】只保留真正"打开窗口等玩家响应"的两条：`investigation` / `battle`。
    /// `power` 是**瞬时结算**（直接加势力标记、不弹窗），不应截断后续命令 —— 旧实现把它列入本表，
    /// 导致 `power 1 1 5` 之后的命令被静默丢弃。
    /// </summary>
    private static readonly HashSet<string> BlockingCommands = new HashSet<string>
    {
        "investigation", "battle"
    };

    /// <summary>判断一行命令是否为阻塞类（对抗）命令（仅看命令名，供批量执行使用）。</summary>
    public static bool IsBlockingCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        return BlockingCommands.Contains(parts[0].ToLower());
    }

    /// <summary>
    /// **模式感知**的阻塞判定：只有"本地会真的弹窗等待玩家"时才算阻塞。
    /// 单机（`_openCombatWindow != null`）＝弹本机窗口 → 阻塞；
    /// 联机服务端（无本机窗口，命令经决策协议异步下发）＝不阻塞批量（否则整批命令会被误丢）。
    /// </summary>
    public bool IsBlockingCommandInCurrentMode(string cmd)
        => IsBlockingCommand(cmd) && _openCombatWindow != null;

    /// <summary>
    /// 批量执行多行 PM 命令：按从上到下的顺序逐行执行。
    /// 一旦某行是对抗类阻塞命令（investigation/battle/power），
    /// 执行完该行后立即中止，剩余命令全部舍弃。
    /// </summary>
    /// <returns>实际执行的行数</returns>
    public int ExecuteCommandBatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        int executed = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;          // 跳过空行
            if (line.StartsWith("#")) continue;      // 支持 # 注释行

            ExecuteCommand(line);
            executed++;

            if (IsBlockingCommandInCurrentMode(line))
            {
                int discarded = 0;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var rest = lines[j].Trim();
                    if (rest.Length == 0 || rest.StartsWith("#")) continue;
                    discarded++;
                }
                if (discarded > 0)
                    GD.Print($"⏸️ 遇到对抗命令「{line}」需等待玩家响应，已舍弃后续 {discarded} 条命令");
                break;
            }
        }

        GD.Print($"🛠️ 批量PM命令执行完毕：共执行 {executed} 条");
        return executed;
    }

    /// <summary>最近一次命令的失败原因（成功/无错误为 null）。供联机 `DebugCommand` 回执给客户端。</summary>
    public string LastError { get; private set; }

    /// <summary>执行单条 PM 命令（不返回错误；错误打到日志）。</summary>
    public void ExecuteCommand(string cmd) => TryExecuteCommand(cmd, out _);

    /// <summary>
    /// 执行单条 PM 命令并返回是否"无错误地执行"。
    /// 错误来源：命令未识别 / 参数非法（含**玩家参数缺失或不是 1|2**）/ 目标卡不存在 / 卡组无该编号。
    /// 联机侧（`GodotIntentRouter.DebugCommand`）用 `error` 回填 `IntentResult.Error`，
    /// 解决"联机上 PM 命令失败后玩家毫无反馈"的调试盲区（此前结果只打到服务端 headless 日志）。
    /// </summary>
    public bool TryExecuteCommand(string cmd, out string error)
    {
        LastError = null;
        error = null;
        if (string.IsNullOrWhiteSpace(cmd)) { error = "空命令"; LastError = error; return false; }

        var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { error = "空命令"; LastError = error; return false; }

        GD.Print($"🛠️ 执行PM命令: {cmd}");

        bool ok = true;
        switch (parts[0].ToLower())
        {
            case "damage": ok = ExecuteDamage(parts); break;
            case "trauma": ok = ExecuteTrauma(parts); break;
            case "reset": ok = ExecuteReset(); break;
            case "list": ExecuteList(); break;
            case "kill": ok = ExecuteKill(parts); break;
            case "heal": ok = ExecuteHeal(parts); break;
            case "draw": ok = ExecuteDraw(parts); break;
            // 【2026-09 新增】直接跳转步骤（中间经过的步骤一律不结算）
            case "jumpto": case "jump": ExecuteJumpTo(parts); break;
            case "move": ok = ExecuteMove(parts); break;
            case "tap": ExecuteTap(parts); break;
            case "untap": ExecuteUntap(parts); break;
            case "flip": ok = ExecuteFlip(parts); break;
            case "covert": ExecuteCovert(parts); break;
            case "printdeck": case "deck": ExecuteDeck(parts); break;
            case "player1draw": ExecuteDrawAlias(parts, 1); break;
            case "player2draw": ExecuteDrawAlias(parts, 2); break;
            case "investigation": ok = ExecuteInvestigation(parts); break;
            case "battle": ok = ExecuteStrength(parts); break;
            case "power": ok = ExecutePower(parts); break;
            case "setassetfromdeck": ok = ExecuteSetAssetFromDeck(parts); break;
            case "addarea": ExecuteAddArea(parts); break;
            case "attach": ok = ExecuteAttach(parts); break;
            case "attacharea": ok = ExecuteAttachToArea(parts); break;
            case "detach": ExecuteDetach(parts); break;
            case "seal": ok = ExecuteSeal(parts); break;
            case "sealarea": ok = ExecuteSealToArea(parts); break;
            case "returnhandtodeck": ExecuteReturnHandToDeckBottom(parts); break;
            case "help": case "?": ExecuteHelp(); break;
            default:
                error = $"未知命令: {parts[0]}（输入 help 查看帮助）";
                GD.Print($"❌ {error}");
                LastError = error;
                return false;
        }

        if (!ok && string.IsNullOrEmpty(error)) error = LastError ?? $"命令执行失败: {cmd}";
        LastError = error;
        return ok;
    }

    // ==================== 子命令实现 ====================
    // 【2026-10·用户实测"kill/damage 看起来没反应"】伤害/治疗类命令**改了模型但从不通知 View**：
    // 单机的伤害数字与地区统计只在"对抗窗口结算"和"测试按钮"里刷过（`Arena.RefreshDamageDisplay`），
    // 而 PM 命令直接调 `CardManager.DealDamageToInstance` ⇒ 模型变了、界面没变（表现为"命令没用"）。
    // 现统一在命令收尾调用 `_onZoneChanged`（Arena 已把它接到"整场重排 + 派生数值刷新"），
    // 与移动类命令保持一致；同时这些命令改为返回 bool，失败原因可经联机回执传到客户端。

    private bool ExecuteDamage(string[] parts)
    {
        // 【2026-10·用户实测对齐】`card` 关键字可省：`damage inst_64 5` ≡ `damage card inst_64 5`。
        // 因此"数值"所在下标不固定：带关键字时是 3，省略时是 2。
        // （area/hand 的数值语义与卡牌不同，必须显式写关键字，避免 `damage area 1 3` 被当成卡牌 UID。）
        const string damageUsage = "用法: damage <UID> <数值> 或 damage card <UID> <数值> / " +
                                   "damage area <地区索引> <数值> / damage hand <手牌索引> <数值>";
        if (parts.Length < 3) return Fail(damageUsage);

        string kind = parts[1].ToLower();
        bool knownKind = kind is "area" or "card" or "hand";
        int amountIdx = (knownKind && parts.Length >= 4) ? 3 : 2;
        if (amountIdx >= parts.Length) return Fail(damageUsage);
        if (!int.TryParse(parts[amountIdx], out int amount) || amount <= 0)
            return Fail("无效数值（必须是正整数）");

        // 省略写法下，数值在 parts[2]，卡牌 UID 在 parts[1]
        string target = knownKind ? parts[2] : parts[1];
        if (knownKind && kind == "card" && parts.Length < 3) return Fail(damageUsage);

        if (knownKind && kind == "area")
        {
            if (!int.TryParse(target, out int areaIdx) || areaIdx < 0 || areaIdx >= CurrentAreaUids.Count)
                return Fail($"无效地区索引（当前共 {CurrentAreaUids.Count} 个地区，索引 0..{CurrentAreaUids.Count - 1}）");
            var instances = _cardManager.GetAreaInstancesBySlot(CurrentAreaUids[areaIdx]);
            foreach (var inst in instances)
                _cardManager.DealDamageToInstance(inst, amount, false);
            GD.Print($"✅ 对地区{areaIdx}的{instances.Count}个角色造成{amount}点伤害");
        }
        else if (knownKind && kind == "hand")
        {
            var hand = _cardManager.GetHandCardsByOwner(1);
            if (!int.TryParse(target, out int idx) || idx < 0 || idx >= hand.Count)
                return Fail($"无效手牌索引，当前手牌数量: {hand.Count}");
            var inst = hand[idx];
            if (inst == null) return Fail("卡牌实例为空");
            _cardManager.DealDamageToInstance(inst, amount, false);
            GD.Print($"✅ 对手牌第{idx}张 {inst.PlayerTemplate?.CardName} 造成{amount}点伤害");
        }
        else
        {
            // `card <UID>` 或裸 `<UID>`
            var instance = _cardManager.GetInstance(target);
            if (instance == null)
                return Fail($"未找到卡牌: {target}（用法: damage <UID> <数值> 或 damage card <UID> <数值>；" +
                            "可用 list 查看当前 UID）");
            _cardManager.DealDamageToInstance(instance, amount, false);
            GD.Print($"✅ 对{instance.PlayerTemplate?.CardName}造成{amount}点伤害");
        }

        _onZoneChanged?.Invoke();   // 通知 View 刷新伤害数字/地区统计
        return true;
    }

    private bool ExecuteTrauma(string[] parts)
    {
        // 与 damage 同口径：`card` 关键字可省，数值下标随之变化。
        const string traumaUsage = "用法: trauma <UID> <数值> 或 trauma card <UID> <数值> / " +
                                   "trauma area <地区索引> <数值> / trauma hand <手牌索引> <数值>";
        if (parts.Length < 3) return Fail(traumaUsage);

        string kind = parts[1].ToLower();
        bool knownKind = kind is "area" or "card" or "hand";
        int amountIdx = (knownKind && parts.Length >= 4) ? 3 : 2;
        if (amountIdx >= parts.Length) return Fail(traumaUsage);
        if (!int.TryParse(parts[amountIdx], out int amount) || amount <= 0)
            return Fail("无效数值（必须是正整数）");

        string target = knownKind ? parts[2] : parts[1];
        if (knownKind && kind == "card" && parts.Length < 3) return Fail(traumaUsage);

        if (knownKind && kind == "area")
        {
            if (!int.TryParse(target, out int areaIdx) || areaIdx < 0 || areaIdx >= CurrentAreaUids.Count)
                return Fail($"无效地区索引（当前共 {CurrentAreaUids.Count} 个地区，索引 0..{CurrentAreaUids.Count - 1}）");
            var instances = _cardManager.GetAreaInstancesBySlot(CurrentAreaUids[areaIdx]);
            foreach (var inst in instances)
                _cardManager.DealDamageToInstance(inst, amount, true);
            GD.Print($"✅ 对地区{areaIdx}的{instances.Count}个角色造成{amount}点创伤");
        }
        else if (knownKind && kind == "hand")
        {
            var hand = _cardManager.GetHandCardsByOwner(1);
            if (!int.TryParse(target, out int idx) || idx < 0 || idx >= hand.Count)
                return Fail($"无效手牌索引，当前手牌数量: {hand.Count}");
            var inst = hand[idx];
            if (inst == null) return Fail("卡牌实例为空");
            _cardManager.DealDamageToInstance(inst, amount, true);
            GD.Print($"✅ 对手牌第{idx}张 {inst.PlayerTemplate?.CardName} 造成{amount}点创伤");
        }
        else
        {
            var instance = _cardManager.GetInstance(target);
            if (instance == null)
                return Fail($"未找到卡牌: {target}（用法: trauma <UID> <数值> 或 trauma card <UID> <数值>；" +
                            "可用 list 查看当前 UID）");
            _cardManager.DealDamageToInstance(instance, amount, true);
            GD.Print($"✅ 对{instance.PlayerTemplate?.CardName}造成{amount}点创伤");
        }

        _onZoneChanged?.Invoke();
        return true;
    }

    private bool ExecuteReset()
    {
        if (_cardManager == null) return Fail("CardManager 未初始化");
        _cardManager.ResetAllDamage();
        GD.Print("✅ 所有伤害标记已重置");
        _onZoneChanged?.Invoke();
        return true;
    }

    private void ExecuteList()
    {
        GD.Print("========== 卡牌状态 ==========");
        GD.Print($"手牌: {_cardManager.GetHandCards().Count} 张");
        foreach (var inst in _cardManager.GetHandCards())
            GD.Print($"  {inst.UID} | {inst.PlayerTemplate?.CardName} | 防御:{inst.CurrentDefense} 伤害:{inst.Damage} 创伤:{inst.Trauma}");

        GD.Print($"\n地区: {_cardManager.GetAreaCards().Count} 张");
        foreach (var inst in _cardManager.GetAreaCards())
            GD.Print($"  {inst.UID} | {inst.PlayerTemplate?.CardName} | 防御:{inst.CurrentDefense} 伤害:{inst.Damage} 创伤:{inst.Trauma}");

        GD.Print($"\n墓地: {_cardManager.GetGraveyardCards().Count} 张");
        GD.Print("=================================");
    }

    /// <summary>
    /// `kill &lt;UID&gt;` / `kill card &lt;UID&gt;` —— 强制杀死指定角色（打满防御走正常死亡结算）。
    /// 【2026-10·用户实测修正】原先**强制要求**第二个参数是字面量 `card`，于是最自然的写法
    /// `kill inst_64` 直接报"用法: kill card &lt;UID&gt;" —— 看起来就是"命令无法执行"。
    /// 现两种写法都接受：带 `card` 时 UID 取第 3 段，不带时 UID 取第 2 段。
    /// </summary>
    private bool ExecuteKill(string[] parts)
    {
        if (parts.Length < 2) return Fail("用法: kill <UID>（等价 kill card <UID>）");

        // 兼容两种写法：kill <UID> / kill card <UID>
        bool withKeyword = parts.Length >= 3 && string.Equals(parts[1], "card", StringComparison.OrdinalIgnoreCase);
        string uid = withKeyword ? parts[2] : parts[1];
        if (string.IsNullOrWhiteSpace(uid)) return Fail("用法: kill <UID>（等价 kill card <UID>）");

        var instance = _cardManager.GetInstance(uid);
        if (instance == null)
            return Fail($"未找到卡牌: {uid}（用法: kill <UID>，UID 形如 inst_37；可用 list 查看当前 UID）");
        if (!instance.HasDefense)
            return Fail($"{instance.PlayerTemplate?.CardName} 无防御，不能被杀死");
        // 经 CardManager 统一结算（含死亡判定与墓地处理），不再直接调 Model.TakeDamage
        _cardManager.DealDamageToInstance(instance, instance.CurrentDefense);
        GD.Print($"💀 杀死 {instance.PlayerTemplate?.CardName}");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// `heal &lt;UID&gt; [数值]` / `heal card &lt;UID&gt; [数值]` —— 清除伤害（不填数值＝清空伤害与创伤）。
    /// 与 `kill` 同理：两种写法都接受。
    /// </summary>
    private bool ExecuteHeal(string[] parts)
    {
        if (parts.Length < 2) return Fail("用法: heal <UID> [数值]（等价 heal card <UID> [数值]）");

        bool withKeyword = parts.Length >= 3 && string.Equals(parts[1], "card", StringComparison.OrdinalIgnoreCase);
        string uid = withKeyword ? parts[2] : parts[1];
        int amountIndex = withKeyword ? 3 : 2;

        var instance = _cardManager.GetInstance(uid);
        if (instance == null)
            return Fail($"未找到卡牌: {uid}（用法: heal <UID> [数值]；可用 list 查看当前 UID）");

        if (parts.Length > amountIndex && int.TryParse(parts[amountIndex], out int amount) && amount > 0)
        {
            // 经 CardManager 统一结算（支持负值减伤，死亡判定由 Controller 负责）
            _cardManager.DealDamageToInstance(instance, -amount);
            GD.Print($"💚 移除 {amount} 点伤害，剩余 {instance.Damage}");
        }
        else
        {
            instance.ResetDamage();
            instance.ResetTrauma();
            GD.Print($"💚 完全清除 {instance.PlayerTemplate?.CardName} 的伤害和创伤标记");
        }
        _onZoneChanged?.Invoke();
        return true;
    }

    // ==================== 玩家参数 + 卡牌选择器（唯一解析真相源） ====================

    /// <summary>
    /// 玩家参数统一口径（2026-10 用户裁定）：`1`＝P1、`2`＝P2。
    /// **同一个玩家参数同时表示"从哪个玩家卡组/区域找牌"与"卡加到哪个玩家"**，不接受其它取值。
    /// </summary>
    private static bool TryParsePlayerToken(string token, out int playerId)
    {
        playerId = 0;
        if (token == "1") { playerId = 1; return true; }
        if (token == "2") { playerId = 2; return true; }
        return false;
    }

    /// <summary>玩家参数用法错误提示（统一文案，避免各命令各写一套）。</summary>
    private static string PlayerUsageError(string cmd) =>
        $"❌ 玩家参数必须是 1 或 2（1=P1，2=P2），且必须在第一位。用法: {cmd} <玩家 1|2> ...";

    /// <summary>卡牌编号归一化：取模板 UID 中**第一个下划线之前**的部分（不区分大小写比较）。</summary>
    private static string NormalizeCardCode(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return "";
        int idx = uid.IndexOf('_');
        return (idx > 0 ? uid.Substring(0, idx) : uid).Trim();
    }

    /// <summary>该卡的模板编号（`JC012` / `inst_37` 这类实例 UID 不算编号）。</summary>
    private static string CardCodeOf(CardInstance card) => NormalizeCardCode(card?.PlayerTemplate?.UID);

    /// <summary>该玩家卡组里编号匹配的牌（保持卡组顺序）。</summary>
    private List<CardInstance> MatchInPlayerDeck(int playerId, string code)
    {
        var result = new List<CardInstance>();
        if (_deckManager == null || string.IsNullOrEmpty(code)) return result;
        var deck = playerId == 2 ? _deckManager.GetDeckPlayer2() : _deckManager.GetDeckPlayer1();
        if (deck == null) return result;
        foreach (var c in deck)
            if (c != null && string.Equals(CardCodeOf(c), code, StringComparison.OrdinalIgnoreCase))
                result.Add(c);
        return result;
    }

    /// <summary>该玩家**除卡组外**各区域里编号匹配的牌（手牌/资产/争夺区/附属/封印/墓地/秘社/堆叠）。
    /// 用于「卡组里没有该编号」时的可读错误提示与引用解析兜底。</summary>
    private List<CardInstance> MatchInPlayerZones(int playerId, string code)
    {
        var result = new List<CardInstance>();
        if (_zoneManager == null || string.IsNullOrEmpty(code)) return result;
        var zones = new[]
        {
            CardZone.Hand, CardZone.AssetZone, CardZone.AreaZone, CardZone.AttachedZone,
            CardZone.SealedZone, CardZone.Graveyard, CardZone.CouncilZone, CardZone.Stack,
        };
        foreach (var zone in zones)
        {
            var cards = _zoneManager.GetZoneCards(zone, playerId);
            if (cards == null) continue;
            foreach (var c in cards)
                if (c != null && string.Equals(CardCodeOf(c), code, StringComparison.OrdinalIgnoreCase)
                    && !result.Contains(c))
                    result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// **双方**范围内编号匹配的候选，按**使用优先级**排序，供不带玩家参数的
    /// `attach`/`seal` 系列命令按编号定位卡牌：
    ///   ① 场上（争夺区/附属/封印，P1→P2）→ ② 手牌 → ③ 资产/其它区域 → ④ 卡组（最后）。
    /// 这样"卡组里也有同编号"时不会把在场上/手里的同一张卡错挑成卡组副本（真实卡组与场上
    /// 常出现同编号多张，若随机挑会把 `attach` 的目标解析到卡组里 ⇒ 命令静默失效）。
    /// 同一实例只入一次（卡组列表与区域桶可能同时含它）。
    /// </summary>
    private List<KeyValuePair<CardInstance, string>> MatchByCodeAllPlayers(string code)
    {
        var result = new List<KeyValuePair<CardInstance, string>>();
        var seen = new HashSet<string>();

        void TakeZone(CardZone zone, string label)
        {
            for (int p = 1; p <= 2; p++)
            {
                var cards = _zoneManager?.GetZoneCards(zone, p);
                if (cards == null) continue;
                foreach (var c in cards)
                    if (c != null && string.Equals(CardCodeOf(c), code, StringComparison.OrdinalIgnoreCase)
                        && seen.Add(c.UID))
                        result.Add(new KeyValuePair<CardInstance, string>(c, $"P{p}·{label}"));
            }
        }

        // ① 场上（attach/seal 的目标优先取这些）
        TakeZone(CardZone.AreaZone, "场上");
        TakeZone(CardZone.AttachedZone, "附属区");
        TakeZone(CardZone.SealedZone, "封印区");
        // ② 手牌 → ③ 其它区域
        TakeZone(CardZone.Hand, "手牌");
        TakeZone(CardZone.AssetZone, "资产区");
        TakeZone(CardZone.Graveyard, "墓地");
        TakeZone(CardZone.CouncilZone, "秘社区");
        TakeZone(CardZone.Stack, "堆叠区");
        // ④ 卡组（最后）
        for (int p = 1; p <= 2; p++)
            foreach (var c in MatchInPlayerDeck(p, code))
                if (c != null && seen.Add(c.UID))
                    result.Add(new KeyValuePair<CardInstance, string>(c, $"P{p}·卡组"));
        return result;
    }

    /// <summary>不带玩家参数的命令（attach/seal 系列）解析失败时的错误：说明该编号在两边的分布。</summary>
    private string DescribeCardLookupFailureAnyPlayer(string token)
    {
        string code = NormalizeCardCode(token);
        if (string.IsNullOrEmpty(code)) code = token;
        int p1 = CountCodeInPlayer(1, code);
        int p2 = CountCodeInPlayer(2, code);
        return $"❌ 找不到卡牌「{token}」：既不是已有实例 UID，也不是任何区域里的卡牌编号" +
               $"（编号「{code}」在 P1 侧 {p1} 张、P2 侧 {p2} 张）";
    }

    /// <summary>该玩家编号匹配的牌总数（卡组 + 各区域），供"换一个玩家试试"类提示。用于日志分布统计。</summary>
    private int CountCodeInPlayer(int playerId, string code)
        => MatchInPlayerDeck(playerId, code).Count + MatchInPlayerZones(playerId, code).Count;

    /// <summary>卡牌引用解析失败时的统一错误（含该编号在双方卡组/区域的分布，便于排查）。</summary>
    private string DescribeCardLookupFailure(string token, int playerId)
    {
        string code = NormalizeCardCode(token);
        // 含下划线的完整写法（如 JC012_weimushouwanghuiji）也按编号段比对
        if (string.IsNullOrEmpty(code)) code = token;
        var sb = new System.Text.StringBuilder();
        sb.Append($"❌ P{playerId} 卡组/区域中没有编号「{code}」的牌，也不是已有实例 UID（token={token}）");
        int other = playerId == 1 ? 2 : 1;
        int inDeck = MatchInPlayerDeck(playerId, code).Count;
        int inZones = MatchInPlayerZones(playerId, code).Count;
        int inOther = CountCodeInPlayer(other, code);
        if (inDeck > 0 || inZones > 0)
            sb.Append($"（该编号在 P{playerId} 的卡组 {inDeck} 张、其它区域 {inZones} 张）");
        if (inOther > 0)
            sb.Append($"；提示：编号「{code}」在 P{other} 侧有 {inOther} 张");
        return sb.ToString();
    }

    /// <summary>
    /// **卡牌引用统一解析**（所有涉及卡牌参数的 PM 命令共用）：
    ///  ① 先按**实例 UID**（`inst_37`）在全局实例表命中；
    ///  ② 否则按**卡牌编号**（`JC012`）命中：卡组 → 争夺区/附属/封印 → 手牌 → 资产 → 墓地/秘社/堆叠；
    ///  ③ 同一编号有多张时**随机**挑一张（用 `_rng`）；`zoneFilter` 非空时逐张过滤（如 attach/seal 的宿主必须在场上）。
    /// 搜索范围：`playerId` 为 1/2 时**严格限定在该玩家**（`draw`/`move`/`setassetfromdeck` 的"参数玩家＝搜索域"口径）；
    /// 传 **0** 时在**双方**范围内按 P1→P2 / 卡组→区域定位（`attach`/`seal` 系列没有玩家参数时的口径）。
    /// </summary>
    private bool TryResolveCard(string token, int playerId, Func<CardInstance, bool> zoneFilter,
                                out CardInstance card, out string how, out string error)
    {
        card = null;
        how = null;
        error = null;
        if (string.IsNullOrWhiteSpace(token)) { error = "❌ 卡牌参数为空"; return false; }

        // ① 实例 UID
        var byUid = _cardManager?.GetInstance(token);
        if (byUid != null && (zoneFilter == null || zoneFilter(byUid)))
        {
            card = byUid;
            how = $"实例UID={token}";
            return true;
        }

        // ② 编号：卡组 → 区域（候选与所在区域标签同步收集，避免错位）
        string code = NormalizeCardCode(token);
        if (string.IsNullOrEmpty(code)) code = token;

        var candidates = new List<CardInstance>();
        var labels = new List<string>();
        void Take(List<CardInstance> src, string where)
        {
            foreach (var c in src)
            {
                if (c == null) continue;
                if (zoneFilter != null && !zoneFilter(c)) continue;
                candidates.Add(c);
                labels.Add(where);
            }
        }

        if (playerId == 1 || playerId == 2)
        {
            Take(MatchInPlayerDeck(playerId, code), $"P{playerId}·卡组");
            Take(MatchInPlayerZones(playerId, code), $"P{playerId}·区域");
        }
        else
        {
            // 无玩家参数（attach/seal 系列）：双方范围内定位
            foreach (var kv in MatchByCodeAllPlayers(code))
            {
                if (kv.Key == null || (zoneFilter != null && !zoneFilter(kv.Key))) continue;
                candidates.Add(kv.Key);
                labels.Add(kv.Value);
            }
        }

        if (candidates.Count == 0)
        {
            error = (playerId == 1 || playerId == 2)
                ? DescribeCardLookupFailure(token, playerId)
                : DescribeCardLookupFailureAnyPlayer(token);
            return false;
        }
        int pickIndex = candidates.Count == 1 ? 0 : _rng.Next(candidates.Count);
        card = candidates[pickIndex];
        how = $"编号「{code}」·{labels[pickIndex]}";
        return true;
    }

    /// <summary>
    /// `draw` —— **玩家参数在第一位，且同时决定"从谁的卡组找"与"加到谁手牌"**（2026-10 用户裁定）：
    ///  ① `draw &lt;玩家 1|2&gt;`                        → 该玩家从牌库顶抽 1 张；
    ///  ② `draw &lt;玩家 1|2&gt; &lt;数量&gt;`               → 该玩家从牌库顶抽 <数量> 张（旧 `draw 1 5` 形态，逐字兼容）；
    ///  ③ `draw &lt;玩家 1|2&gt; &lt;卡牌编号&gt;`            → **定向取牌**：从该玩家**卡组**取 1 张该编号的牌加入**该玩家**手牌；
    ///  ④ `draw &lt;玩家 1|2&gt; 1 &lt;卡牌编号&gt;`          → 同 ③（显式数量只允许 1；编号取牌不支持多张）。
    /// 语义要点：
    ///  · 玩家参数缺省不再"静默按 P1"——**缺失或非 1/2 一律报错**（这是"draw 2 JC015 加进 P1 手牌"的根因）；
    ///  · 编号取牌**不算"抓牌"**：不广播 `OnDraw`、不受"梦魇族群从牌库底抓牌"重定向、空库不判负
    ///    （与 JC049「非抓牌式取牌」同口径）；
    ///  · 卡组里没有该编号 → **明确报错**（并提示该编号在双方卡组/区域的分布），**绝不静默降级为普通抽牌**。
    /// </summary>
    private bool ExecuteDraw(string[] parts)
    {
        const string usage = "draw <玩家 1|2> [数量] [卡牌编号]";
        if (parts.Length < 2) return Fail($"用法: {usage}");

        // 玩家参数永远在第一位
        if (!TryParsePlayerToken(parts[1], out int player))
            return Fail(PlayerUsageError("draw") + $"（收到「{parts[1]}」）");

        // ① draw <玩家>  —— 抽 1 张
        if (parts.Length == 2)
        {
            _onDraw?.Invoke(player, 1);
            GD.Print($"✅ 玩家{player} 抽牌 1 张（已加入手牌区）");
            return true;
        }

        // ② draw <玩家> <数量>  —— 旧形态：牌库顶抽 <数量> 张
        if (parts.Length == 3 && int.TryParse(parts[2], out int count) && count > 0)
        {
            _onDraw?.Invoke(player, count);
            GD.Print($"✅ 玩家{player} 抽牌 {count} 张（已加入手牌区）");
            return true;
        }

        // ③ draw <玩家> <卡牌编号>  —— 定向取牌（1 张）
        if (parts.Length == 3)
            return DrawSpecificCard(player, parts[2]);

        // ④ draw <玩家> 1 <卡牌编号>  —— 显式数量；编号取牌不支持多张
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[2], out int explicitTake) || explicitTake <= 0)
                return Fail($"用法: {usage}");
            if (explicitTake != 1)
                return Fail($"按编号取牌不支持多张：一次只能从卡组取 1 张。请改用 `draw {player} 1 {parts[3]}`");
            return DrawSpecificCard(player, parts[3]);
        }

        return Fail($"用法: {usage}");
    }

    /// <summary>打印并返回 false（统一"失败"出口，供 TryExecuteCommand 回执联机错误）。</summary>
    private bool Fail(string error)
    {
        LastError = error;
        GD.Print(error.StartsWith("❌") ? error : "❌ " + error);
        return false;
    }

    /// <summary>
    /// `draw &lt;玩家&gt; &lt;卡牌编号&gt;` 的实体：从**该玩家卡组**定向取 1 张该编号的牌，加入**该玩家**手牌。
    /// 找不到 → 明确报错（含该编号在双方卡组/区域的分布），不静默降级。
    /// </summary>
    private bool DrawSpecificCard(int playerId, string codeToken)
    {
        if (TurnManagerRef == null || _deckManager == null || _zoneManager == null)
            return Fail("定向取牌失败：TurnManager/DeckManager/ZoneManager 未注入");

        string want = NormalizeCardCode(codeToken);
        if (string.IsNullOrEmpty(want)) want = codeToken;
        if (string.IsNullOrEmpty(want)) return Fail("卡牌编号为空");

        var matches = MatchInPlayerDeck(playerId, want);
        if (matches.Count == 0)
            return Fail(DescribeCardLookupFailure(codeToken, playerId));

        var pick = matches.Count == 1 ? matches[0] : matches[_rng.Next(matches.Count)];
        TurnManagerRef.RemoveCardFromDeckOf(playerId, pick);   // 先从牌库数据层移出（牌库不在区域桶里）
        _zoneManager.MoveToHand(pick, playerId);               // 再加入**该玩家**手牌（照常广播移动事件）
        GD.Print($"✅ 定向取牌：P{playerId} 卡组 → P{playerId} 手牌 1 张（编号「{want}」）：" +
                 $"{pick.PlayerTemplate?.CardName ?? pick.UID}");
        _onZoneChanged?.Invoke();
        return true;
    }

    // ==================== 步骤跳转（调试） ====================

    /// <summary>
    /// `jumpto &lt;步骤名&gt;[地区号]` —— 直接跳到指定步骤，**中间经过的步骤一律不结算**（其触发/效果都不结算），
    /// 只进入目标步骤并执行它正常的进入逻辑（该步骤照常可结算，后续流程照常推进）。
    ///  · 步骤名＝`TurnManager.TurnPhase` 枚举名（不区分大小写）：`StartReset`/`StartPrepare`/`StartDraw`/
    ///    `ActionFirst`/`ActionSecond`/`PreCombat`/`CombatInvestigation`/`CombatStrength`/`CombatPower`/
    ///    `EndCleanup`/`EndRecover`/`EndPassTurn`；
    ///  · **对抗类步骤带地区号**（紧贴在名字后）：`jumpto CombatInvestigation1` ＝跳到 **1 地区**调查对抗，
    ///    同理 `CombatStrength2` / `CombatPower3`（省略地区号＝地区 1；`PreCombat` 不带地区号）。
    /// </summary>
    private void ExecuteJumpTo(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: jumpto <步骤名>[地区号]，如 jumpto EndRecover / jumpto CombatInvestigation1"); return; }
        if (TurnManagerRef == null) { GD.Print("❌ jumpto 失败：TurnManager 未注入"); return; }

        string raw = parts[1].Trim();
        int areaNo = 0;
        string name = raw;
        // 拆"名字 + 结尾数字"（对抗类步骤的地区号）
        int cut = raw.Length;
        while (cut > 0 && char.IsDigit(raw[cut - 1])) cut--;
        if (cut < raw.Length && cut > 0)
        {
            name = raw.Substring(0, cut);
            int.TryParse(raw.Substring(cut), out areaNo);
        }

        if (!Enum.TryParse<TurnManager.TurnPhase>(name, ignoreCase: true, out var phase))
        {
            GD.Print($"❌ 未识别的步骤名「{raw}」。可用：StartReset/StartPrepare/StartDraw/ActionFirst/ActionSecond/" +
                     "PreCombat/CombatInvestigation[地区号]/CombatStrength[地区号]/CombatPower[地区号]/" +
                     "EndCleanup/EndRecover/EndPassTurn");
            return;
        }

        if (!TurnManagerRef.JumpToPhase(phase, areaNo, out string error))
        {
            GD.Print($"❌ jumpto {raw} 失败：{error}");
            return;
        }
        GD.Print($"✅ jumpto：已跳到 {TurnManagerRef.CurrentPhase}" +
                 (areaNo > 0 ? $"，地区 {areaNo}" : "") + $"（中间步骤未结算）");
        _onZoneChanged?.Invoke();
    }

    /// <summary>player1draw / player2draw 别名：固定玩家号，仅数量可变。</summary>
    private void ExecuteDrawAlias(string[] parts, int playerId)
    {
        int count = 1;
        if (parts.Length >= 2 && int.TryParse(parts[1], out int n) && n > 0)
            count = n;
        _onDraw?.Invoke(playerId, count);
        GD.Print($"✅ 玩家{playerId} 抽牌 {count} 张（已加入手牌区）");
    }

    // ==================== 卡牌区域移动 ====================
    /// <summary>
    /// `move &lt;玩家 1|2&gt; &lt;卡牌编号|实例UID&gt; &lt;目标&gt; [目标玩家 1|2] [top|bottom]`
    ///  · **玩家参数在第一位且同时是搜索域**（2026-10 用户裁定）：编号只从该玩家的卡组/区域里找；
    ///    目标是 `hand/grave/asset/score/deck` 时，缺省"卡加到该玩家"；`目标玩家` 可显式覆盖；
    ///  · 目标为纯数字 `1..N` ＝争夺区编号（越界显式报错，不再落到 default 分支）；
    ///  · `deck` 可用 `top|bottom` 指定牌库顶/底（缺省底，与旧行为一致）。
    /// </summary>
    private bool ExecuteMove(string[] parts)
    {
        const string usage = "move <玩家 1|2> <卡牌编号|实例UID> <目标> [目标玩家 1|2] [top|bottom]";
        if (parts.Length < 4)
        {
            GD.Print($"❌ 用法: {usage}");
            GD.Print("   目标: 1..N(地区编号) / hand / grave / deck [top|bottom] / score / asset");
            return false;
        }

        // ① 玩家参数（搜索域）
        if (!TryParsePlayerToken(parts[1], out int searchPlayer))
            return Fail(PlayerUsageError("move") + $"（收到「{parts[1]}」）");

        // ② 卡牌引用（编号只在 searchPlayer 范围内命中）
        if (!TryResolveCard(parts[2], searchPlayer, null, out var card, out string how, out string error))
            return Fail(error);

        // ③ 目标（纯数字＝地区编号，先校验越界；避免把越界数字误当成"目标玩家"吞掉）
        string target = parts[3].ToLower();
        if (int.TryParse(target, out int parsedArea) && (parsedArea < 1 || parsedArea > CurrentAreaUids.Count))
            return Fail($"无效地区编号 {parsedArea}，当前共 {CurrentAreaUids.Count} 个地区(1..{CurrentAreaUids.Count})");

        // ④ 可选参数：目标玩家 / 牌库顶底
        int destPlayer = searchPlayer;
        string deckSide = null;
        for (int i = 4; i < parts.Length; i++)
        {
            string tok = parts[i].ToLower();
            if (TryParsePlayerToken(tok, out int p)) { destPlayer = p; continue; }
            if (tok == "top" || tok == "bottom") { deckSide = tok; continue; }
            return Fail($"无法识别的参数「{parts[i]}」。用法: {usage}");
        }

        // 移动前实例归属
        int ownerBefore = card.OwnerId;
        int controllerBefore = card.ControllerId;
        bool deckTarget = target is "deck" or "decktop" or "deckbottom";
        bool toTop = target == "decktop" || deckSide == "top";

        // 目标为纯数字 -> 争夺区编号（1..N）
        if (int.TryParse(target, out int areaNo))
        {
            var areaUid = CurrentAreaUids[areaNo - 1];
            _zoneManager.MoveToArea(card, areaUid, destPlayer);
            GD.Print($"✅ {card.PlayerTemplate?.CardName}（{how}）移动到玩家{destPlayer}的争夺区{areaNo} ({areaUid})");
        }
        else if (deckTarget)
        {
            if (toTop) _zoneManager.MoveToDeckTop(card, destPlayer);
            else _zoneManager.MoveToDeckBottom(card, destPlayer);
            GD.Print($"✅ {card.PlayerTemplate?.CardName}（{how}）返回玩家{destPlayer}牌库{(toTop ? "顶部" : "底部")}");
        }
        else
        {
            switch (target)
            {
                case "hand":
                    _zoneManager.MoveToHand(card, destPlayer);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName}（{how}）加入玩家{destPlayer}手牌");
                    break;
                case "grave":
                    _zoneManager.MoveToGraveyard(card, destPlayer);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName}（{how}）进入玩家{destPlayer}墓地");
                    break;
                case "score":
                    _zoneManager.MoveToScore(card, destPlayer);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName}（{how}）进入玩家{destPlayer}得分区");
                    break;
                case "asset":
                    _zoneManager.MoveToAsset(card, destPlayer);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName}（{how}）进入玩家{destPlayer}资产区（保留状态）");
                    break;
                default:
                    return Fail($"未知目标区域: {target}（支持 1..N(地区)/hand/grave/deck[top|bottom]/score/asset）");
            }
        }

        // 移动前后实例归属对比输出
        GD.Print($"📦 移动归属变化 [{card.PlayerTemplate?.CardName} | {card.UID}]：" +
                 $"OwnerId {ownerBefore}→{card.OwnerId}，ControllerId {controllerBefore}→{card.ControllerId}");

        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>横置指定卡牌的实例（与支付费用横置一致）</summary>
    private void ExecuteTap(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: tap <UID>"); return; }
        var instance = _cardManager.GetInstance(parts[1]);
        if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[1]}"); return; }

        instance.SetTapped(true);           // SetTapped 发 TappedChanged → 持续效果重判（阿格斯封锁即时开关）
        _refreshCardUI?.Invoke(parts[1]);   // 仅通知 UI 刷新，不持有 UI 实例
        GD.Print($"🔒 横置 {instance.PlayerTemplate?.CardName}（实例 IsTapped 已置 true）");
    }

    /// <summary>解除指定卡牌的横置</summary>
    private void ExecuteUntap(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: untap <UID>"); return; }
        var instance = _cardManager.GetInstance(parts[1]);
        if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[1]}"); return; }

        instance.SetTapped(false);          // SetTapped 发 TappedChanged → 持续效果重判（阿格斯封锁即时开关）
        _refreshCardUI?.Invoke(parts[1]);   // 仅通知 UI 刷新，不持有 UI 实例
        GD.Print($"🔓 解除横置 {instance.PlayerTemplate?.CardName}（实例 IsTapped 已置 false）");
    }

    /// <summary>
    /// 翻面（**双向切换**）：背面 → 翻到正面（揭示），正面 → 翻到背面（潜伏/暗藏）。
    /// 无论正反翻面，均清空卡上指示物/附属/封印：指示物直接移除；附属进入弃牌堆；封印回到 OWNER 手牌。
    /// 翻到正面时恢复卡牌正常属性（与正常打出一致）。
    ///
    /// 【2026-10·用户实测修正】原先"当前是否正面"只读 **View 侧** 的 `CardUI.IsFaceUp`，有两个坑：
    ///  ① 该状态可能过期（卡进手牌时 View 无条件 `SetFace(true)`，与模型 `IsCovert` 不一致）；
    ///  ② View 缺该卡 UI 时缺省 false ⇒ 每次都被当成"背面"⇒ 表现为"只能翻成背面、翻不回来"。
    /// 现改为：**模型 `IsCovert` 为权威**（背面＝暗藏者），仅在没有 UI 时回退读 View，
    /// 再兜回 `IsCovert`。这样 `flip` 对任何状态的卡都是严格的双向切换。
    /// </summary>
    private bool ExecuteFlip(string[] parts)
    {
        if (parts.Length < 2) return Fail("用法: flip <UID>");
        var instance = _cardManager.GetInstance(parts[1]);
        if (instance == null)
            return Fail($"未找到卡牌: {parts[1]}（用法: flip <UID>；可用 list 查看当前 UID）");

        // ① 权威状态：模型 `IsCovert`（true＝背面/暗藏）；② 无 UI 时回退读 View；③ 再兜回 IsCovert
        bool wasFaceUp = !instance.IsCovert;
        if (_getFaceUp == null) wasFaceUp = instance.IsCovert ? false : !instance.IsCovert;

        // ---- 翻面伴随清理：指示物 / 附属 / 封印 ----
        ClearCounters(instance);          // 指示物直接移除
        ClearAttachments(instance);       // 附属进入弃牌堆（接口）
        ClearSeals(instance);             // 封印回到 OWNER 手牌（接口）

        bool toFaceUp = !wasFaceUp;
        if (toFaceUp)
        {
            // 翻到正面 = 暗藏者现身：规范化行为封装（复位 + 触发进场/现身两类时机），见 CardManager.RevealCard
            _cardManager.RevealCard(instance);
        }
        else
        {
            // 翻到背面 = 进入潜伏：标记为暗藏者（之前若被揭示过则重新潜伏）
            instance.SetCovert(true);   // 经状态中枢发 CovertChanged，供目标丢失总线捕获(规则③)
        }
        // 同步 UI 朝向：`flip` 是"纯翻面"（不移动区域），不经过 SyncCardUIToZone，
        // 必须显式刷一次，否则卡面停在旧朝向（手牌分支进区时会无条件置正面，更易错位）。
        _setFaceUp?.Invoke(parts[1], toFaceUp);
        _refreshCardUI?.Invoke(parts[1]);

        GD.Print($"🔃 翻面 {instance.PlayerTemplate?.CardName}({instance.UID})：" +
                 $"{(wasFaceUp ? "正面→背面(潜伏/暗藏)" : "背面→正面(揭示)")}" +
                 $"（已清空指示物/附属/封印，IsCovert={instance.IsCovert}）");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 测试辅助：强制将指定卡标记为暗藏者(IsCovert=true) / 解除(=false)。
    /// 仅用于验证"暗藏者免疫伤害"行为，非正式玩法入口（正式入口为 DeployCovert）。
    /// 用法: covert <UID> [on|off]   缺省 on
    /// </summary>
    private void ExecuteCovert(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: covert <UID> [on|off]"); return; }
        var instance = _cardManager.GetInstance(parts[1]);
        if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[1]}"); return; }
        bool on = parts.Length < 3 || parts[2].ToLower() != "off";
        // 暗藏(潜伏) = 背面朝上；解除暗藏 = 正面朝上（并恢复基础属性）
        instance.SetCovert(on);   // 走暗藏中枢（发 CovertChanged），供目标丢失版本号收口
        bool toFaceUp = !on;
        if (toFaceUp)
        {
            ClearCounters(instance);
            ClearAttachments(instance);
            instance.ResetToBaseState();
        }
        // 纯翻面（无区域移动）：显式触发 FireCovert 使目标版本号失效（D16 丢失目标新方案），
        // 复用 FireMoved 统一刷新出口保持 UI 一致。
        EE.ZoneManagerRef?.FireCovert(instance);
        // 同步 UI 表现：翻面 + 刷新数值，避免数据/UI 不一致
        _setFaceUp?.Invoke(instance.UID, toFaceUp);
        if (toFaceUp) _refreshCardUI?.Invoke(instance.UID);
        GD.Print($"🥷 covert {instance.PlayerTemplate?.CardName} (UID={instance.UID}) IsCovert={on}，UI翻面={(toFaceUp ? "正面" : "背面")}");
    }

    /// <summary>移除卡上所有指示物</summary>
    private void ClearCounters(CardInstance instance)
    {
        instance.ClearCounters();
    }

    /// <summary>移除附属：宿主翻面/离场时，其结附的附属（递归）进入各自 OWNER 的弃牌堆。
    /// 与 ZoneManager.DetachAttachmentsToGraveyard 同源规则（普通离场→进墓地）。</summary>
    private void ClearAttachments(CardInstance instance)
    {
        if (instance?.AttachedCards == null || instance.AttachedCards.Count == 0) return;
        // 复制一份遍历，避免 MoveToGraveyard 内部 RemoveFromAllZones 修改 AttachedCards 引发集合变更
        foreach (var att in instance.AttachedCards.ToList())
        {
            att.AttachedTo = null;
            _zoneManager.MoveToGraveyard(att, att.OwnerId);
        }
        instance.AttachedCards.Clear();
    }

    /// <summary>移除封印：宿主翻面/离场时，其封印的封印卡（按添加顺序）返回各自 OWNER 手牌。
    /// 与 ZoneManager.DetachSealedCards 同源规则（封印回 OWNER 手牌，区别于附属进墓地）。</summary>
    private void ClearSeals(CardInstance instance)
    {
        if (instance?.SealedCards == null || instance.SealedCards.Count == 0) return;
        // 复制一份遍历，避免 MoveToHand 内部 RemoveFromAllZones 修改 SealedCards 引发集合变更
        foreach (var seal in instance.SealedCards.ToList())
        {
            seal.SealedBy = null;
            seal.IsSeal = false;                  // 清除封印卡标记
            _zoneManager.MoveToHand(seal, seal.OwnerId);
        }
        instance.SealedCards.Clear();
    }

    // ==================== 对抗命令（调查/战斗/势力） ====================
    // 【2026-10 联机修复】三条命令统一经 `CombatManager.ResolveCombatFromDebug` 结算：
    //  · 单机（注入 _openCombatWindow）→ 由 Arena 侧转调 CombatManager → 走"正规结算"事件链弹本机窗口；
    //  · 联机服务端（无本机窗口）→ 直接经决策协议（IInputGateway）把对抗窗口下发/回收到对应玩家。
    // 旧实现在联机下 `_openCombatWindow == null` 就直接打印"回调未初始化"什么都不做（用户实测）。

    /// <summary>解析"胜者 + 地区号 + X"，并校验地区号在有效范围内；失败时经 `Fail` 记录可回执的错误。</summary>
    private bool TryParseCombatArgs(string[] parts, string cmd, out int winner, out int areaNo, out int x)
    {
        winner = 0; areaNo = 0; x = 0;
        if (parts.Length < 4) return Fail($"用法: {cmd} <玩家 1|2> <地区 areaNo> <X>");
        if (!TryParsePlayerToken(parts[1], out winner))
            return Fail(PlayerUsageError(cmd) + $"（收到「{parts[1]}」）");
        if (!int.TryParse(parts[2], out areaNo) || areaNo < 1)
            return Fail("无效地区编号（必须是正整数）");
        if (areaNo > CurrentAreaUids.Count)
            return Fail($"无效地区编号 {areaNo}，当前共 {CurrentAreaUids.Count} 个地区(1..{CurrentAreaUids.Count})");
        if (!int.TryParse(parts[3], out x) || x <= 0)
            return Fail("X 必须是正整数");
        return true;
    }

    /// <summary>
    /// `investigation &lt;玩家 1|2&gt; &lt;地区 areaNo&gt; &lt;X&gt;` —— 调查对抗：胜者从牌库顶查看 X 张，
    /// 分配放回顶/底后抽 1 张。单机弹本机窗口；联机经决策协议下发到胜者客户端。
    /// </summary>
    private bool ExecuteInvestigation(string[] parts)
    {
        if (!TryParseCombatArgs(parts, "investigation", out int winner, out int areaNo, out int x))
            return false;

        // 单机：经注入回调交给 Arena → CombatManager.ResolveCombatFromDebug（走正规结算链，含获胜触发）
        if (_openCombatWindow != null)
        {
            _openCombatWindow.Invoke(CombatManager.CombatType.Investigation, winner, x, areaNo);
            GD.Print($"✅ 调查对抗：P{winner} 在地区{areaNo} 获胜 X={x}（单机：走本机对抗窗口 + 正规结算链）");
            return true;
        }

        // 联机服务端（无本机窗口）：经决策协议把对抗窗口下发/回收到对应玩家
        if (CombatManagerRef == null)
            return Fail("调查对抗未生效：既无本机窗口回调，CombatManagerRef 也未注入");
        CombatManagerRef.ResolveCombatFromDebug(CombatManager.CombatType.Investigation, areaNo, winner, x);
        GD.Print($"✅ 调查对抗：P{winner} 在地区{areaNo} 获胜 X={x}（联机：已经决策协议下发窗口）");
        return true;
    }

    /// <summary>打印卡组内容物（按卡组内真实顺序，索引 0 = 牌库顶）。用法: deck [1|2]</summary>
    private void ExecuteDeck(string[] parts)
    {
        if (_deckManager == null) { GD.Print("❌ DeckManager 未初始化"); return; }

        int playerId = 1;
        if (parts.Length >= 2 && int.TryParse(parts[1], out int p) && (p == 1 || p == 2))
            playerId = p;

        var deck = playerId == 1 ? _deckManager.GetPlayer1DeckData() : _deckManager.GetPlayer2DeckData();
        GD.Print($"========== 玩家{playerId} 卡组内容（共 {deck.Count} 张，索引0=牌库顶） ==========");
        for (int i = 0; i < deck.Count; i++)
        {
            var inst = deck[i];
            if (inst == null) { GD.Print($"[{i,2}] <null>"); continue; }
            var t = inst.PlayerTemplate;
            GD.Print($"[{i,2}] {t?.CardName ?? "?"} | UID={inst.UID} | 模板={t?.UID ?? "?"} | 费{t?.Cost} 调{inst.CurrentInvestigation} 战{inst.CurrentStrength} 势{inst.CurrentPower} 防{(inst.HasDefense ? inst.CurrentDefense.ToString() : "无")}");
        }
        GD.Print($"========== 玩家{playerId} 卡组打印结束 ==========");
    }

    /// <summary>
    /// `battle &lt;玩家 1|2&gt; &lt;地区 areaNo&gt; &lt;获胜点 X&gt;` —— 战斗对抗：败方该地区角色分配 X 点伤害。
    /// 单机弹本机窗口；联机经决策协议下发到胜者客户端。
    /// </summary>
    private bool ExecuteStrength(string[] parts)
    {
        if (!TryParseCombatArgs(parts, "battle", out int winner, out int areaNo, out int x)) return false;

        if (_openCombatWindow != null)
        {
            _openCombatWindow.Invoke(CombatManager.CombatType.Strength, winner, x, areaNo);
            GD.Print($"✅ 战斗对抗：P{winner} 在地区{areaNo} 获胜 {x} 点（单机：走本机对抗窗口 + 正规结算链）");
            return true;
        }

        if (CombatManagerRef == null)
            return Fail("战斗对抗未生效：既无本机窗口回调，CombatManagerRef 也未注入");
        CombatManagerRef.ResolveCombatFromDebug(CombatManager.CombatType.Strength, areaNo, winner, x);
        GD.Print($"✅ 战斗对抗：P{winner} 在地区{areaNo} 获胜 {x} 点（联机：已经决策协议下发窗口）");
        return true;
    }

    /// <summary>
    /// 势力对抗：在指定地区为指定玩家直接添加势力标记（瞬时结算，**不弹窗**）。
    /// 用法: power &lt;玩家 1|2&gt; &lt;地区 1..N&gt; &lt;点数&gt;
    /// </summary>
    private bool ExecutePower(string[] parts)
    {
        if (!TryParseCombatArgs(parts, "power", out int playerId, out int areaNo, out int points)) return false;

        if (_areaManager == null) return Fail("AreaManager 未初始化");
        var areaUid = CurrentAreaUids[areaNo - 1];
        _areaManager.AddPowerMark(areaUid, playerId, points);
        GD.Print($"✅ 势力对抗：玩家{playerId} 在地区{areaNo} 获胜{points}点（已添加势力标记）");
        // AddPowerMark 内部已不再触发赢取检查（避免嵌套入栈打乱阶段节拍）；此处作为调试命令的收尾点统一触发。
        // 规则不变：标记达标即结算赢取地区；仅时机改由命令结尾（非嵌套上下文）进行。
        if (_stackManager != null)
            _areaManager.TryStartAreaWin(_stackManager);
        return true;
    }

    /// <summary>
    /// 快速建立资产：从**参数玩家**卡组取牌置入**该玩家**资产区（2026-10 用户裁定：编号只从该玩家卡组找）。
    /// 用法: setassetfromdeck &lt;玩家 1|2&gt; &lt;num&gt;          - 该玩家卡组顶 num 张
    ///       setassetfromdeck &lt;玩家 1|2&gt; &lt;卡牌编号&gt; [num]  - 该玩家卡组中该编号的牌（num 缺省 1，编号不重复取）
    /// </summary>
    private bool ExecuteSetAssetFromDeck(string[] parts)
    {
        const string usage = "setassetfromdeck <玩家 1|2> <num> | <玩家 1|2> <卡牌编号> [num]";
        if (parts.Length < 3) return Fail($"用法: {usage}");
        if (!TryParsePlayerToken(parts[1], out int playerId))
            return Fail(PlayerUsageError("setassetfromdeck") + $"（收到「{parts[1]}」）");

        if (_deckManager == null) return Fail("DeckManager 未初始化");

        // —— 形态①：数量（卡组顶 N 张）——
        if (int.TryParse(parts[2], out int num))
        {
            if (num <= 0) return Fail("num 必须是正整数");
            var deck = playerId == 2 ? _deckManager.GetPlayer2DeckData() : _deckManager.GetPlayer1DeckData();
            int actual = Math.Min(num, deck.Count);
            if (actual == 0) return Fail($"玩家{playerId}卡组为空，无法建立资产");

            for (int i = 0; i < actual; i++)
            {
                var card = deck[i];
                _zoneManager.MoveToAsset(card, playerId);
                GD.Print($"✅ {card.PlayerTemplate?.CardName} 移入玩家{playerId}资产区");
            }
            GD.Print($"✅ 玩家{playerId} 从卡组顶部移入 {actual} 张至资产区（请求 {num} 张）");
            _onZoneChanged?.Invoke();
            return true;
        }

        // —— 形态②：卡牌编号（从**该玩家**卡组按编号取）——
        string code = NormalizeCardCode(parts[2]);
        if (string.IsNullOrEmpty(code)) code = parts[2];
        int want = 1;
        if (parts.Length >= 4 && (!int.TryParse(parts[3], out want) || want <= 0))
            return Fail($"用法: {usage}");

        var matches = MatchInPlayerDeck(playerId, code);
        if (matches.Count == 0)
            return Fail(DescribeCardLookupFailure(parts[2], playerId));
        int moved = 0;
        for (int i = 0; i < matches.Count && moved < want; i++)
        {
            var card = matches[i];
            _zoneManager.MoveToAsset(card, playerId);
            GD.Print($"✅ {card.PlayerTemplate?.CardName}({card.UID}) 从 P{playerId} 卡组移入 P{playerId} 资产区");
            moved++;
        }
        GD.Print($"✅ 按编号建立资产：P{playerId} 卡组 → 资产区 {moved} 张（编号「{code}」，请求 {want} 张）");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 从世界牌库抽出新地区卡，生成新的 AreaSlot 并填入 CardContainer，然后重排所有地区。
    /// 用法: addarea [数量]（默认 1）
    /// </summary>
    private void ExecuteAddArea(string[] parts)
    {
        int count = 1;
        if (parts.Length >= 2)
        {
            if (!int.TryParse(parts[1], out count) || count <= 0)
            { GD.Print("❌ 用法: addarea [数量]，数量必须是正整数"); return; }
        }

        if (_drawAndAddNewArea == null) { GD.Print("❌ 新增地区回调未初始化"); return; }

        int success = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_drawAndAddNewArea.Invoke()) break;
            success++;
        }

        if (success == 0) GD.Print("⚠️ 未能新增地区（世界牌库可能已空）");
        else GD.Print($"✅ 新增 {success} 个地区（请求 {count} 个）");
    }

    // ==================== 附属系统（PM 调试用，配合离场连带/地区赢取验证） ====================
    // 【2026-10 用户裁定·回退】attach/attacharea/seal/sealarea **不带玩家参数**，恢复原签名：
    //   attach    <附属编号|UID> <宿主编号|UID>
    //   attacharea<附属编号|UID> <地区编号>
    //   seal      <封印卡编号|UID> <目标编号|UID>
    //   sealarea  <封印卡编号|UID> <地区编号>
    // 卡牌参数仍同时接受**实例 UID** 与**卡牌编号**（编号在双方范围内定位：实例表优先 → P1→P2 卡组/区域）。

    /// <summary>
    /// 把附属结附到场上（或任意区域）的宿主卡：建立双向引用、标记 IsAttached。
    /// 不移动区域，要求附属与宿主当前处于同一区域（通常都已在争夺区）。
    /// 用法: attach &lt;附属编号|UID&gt; &lt;宿主编号|UID&gt;
    ///  · 宿主必须是**场上的角色/附属**（zoneFilter 保证不会因为"卡组里也有同编号"而错挑）。
    /// </summary>
    private bool ExecuteAttach(string[] parts)
    {
        if (parts.Length < 3) return Fail("用法: attach <附属编号|UID> <宿主编号|UID>");

        if (!TryResolveCard(parts[1], 0, null, out var att, out string howAtt, out string e1))
            return Fail("未找到附属卡：" + e1);
        // 宿主必须是场上的卡（角色或地区附属/结附在角色上的附属均处于 AreaZone/AttachedZone）
        if (!TryResolveCard(parts[2], 0,
                c => c.CurrentZone == CardZone.AreaZone || c.CurrentZone == CardZone.AttachedZone,
                out var host, out string howHost, out string e2))
            return Fail("未找到场上宿主卡：" + e2);

        if (att.AttachedTo != null) return Fail($"{att.UID} 已结附于 {att.AttachedTo.UID}，先 detach");

        // 【顺序关键】先移动进附属子区域，**再**建立双向引用与标记：
        // 若先设 `att.AttachedTo = host` 再 `MoveToAttachedZone`，则内部的 `RemoveFromAllZones` →
        // `DetachAttachmentsToGraveyard` 会把「已结附的 att」当成宿主的附属送墓地，
        // 而 att 本身 `AttachedCards` 又非空 ⇒ `MoveToGraveyard ↔ DetachAttachmentsToGraveyard` **无限递归**（栈溢出）。
        _zoneManager.MoveToAttachedZone(att, host.ZoneDetailId, att.ControllerId == 0 ? 1 : att.ControllerId);
        att.AttachedTo = host;
        att.IsAttached = true;
        if (!host.AttachedCards.Contains(att))
            host.AttachedCards.Add(att);

        GD.Print($"🔗 附属 {att.PlayerTemplate?.CardName}({att.UID}·{howAtt}) 结附到宿主 " +
                 $"{host.PlayerTemplate?.CardName}({host.UID}·{howHost})");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 把附属结附到指定地区（作为"结附于地区的附属"，独立存在于地区上、不依附角色）。
    /// 用法: attacharea &lt;附属编号|UID&gt; &lt;地区编号 1..N&gt;
    /// </summary>
    private bool ExecuteAttachToArea(string[] parts)
    {
        if (parts.Length < 3) return Fail("用法: attacharea <附属编号|UID> <地区编号>");

        if (!TryResolveCard(parts[1], 0, null, out var att, out string howAtt, out string e1))
            return Fail("未找到附属卡：" + e1);
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1 || areaNo > CurrentAreaUids.Count)
            return Fail($"无效地区编号，当前共 {CurrentAreaUids.Count} 个(1..{CurrentAreaUids.Count})");

        // 结附地区：用 ZoneDetailId 标记为地区附属（与角色附属区分），并置于该争夺区
        // （与 attach 同样"先移动、后标记"，避免内部连带逻辑把本卡当宿主附属处理）
        var areaUid = CurrentAreaUids[areaNo - 1];
        _zoneManager.MoveToAttachedZone(att, areaUid, att.ControllerId == 0 ? 1 : att.ControllerId);
        att.AttachedTo = null;             // 地区附属不指向具体角色
        att.IsAttached = true;
        att.ZoneDetailId = areaUid;

        GD.Print($"🔗 附属 {att.PlayerTemplate?.CardName}({att.UID}·{howAtt}) 结附到地区{areaNo}({areaUid})");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 解除附属与宿主/地区的结附关系（仅断开引用，不移动卡牌区域）。
    /// 用法: detach <附属UID>
    /// </summary>
    private void ExecuteDetach(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: detach <附属UID>"); return; }
        var att = _cardManager.GetInstance(parts[1]);
        if (att == null) { GD.Print($"❌ 未找到附属卡: {parts[1]}"); return; }
        if (att.AttachedTo != null)
        {
            att.AttachedTo.AttachedCards.Remove(att);
            att.AttachedTo = null;
        }
        att.IsAttached = false;
        att.ZoneDetailId = null;
        GD.Print($"✂️ 附属 {att.PlayerTemplate?.CardName}({att.UID}) 已解除结附");
    }

    // ==================== 封印系统（PM 调试用，配合离场连带/地区赢取验证） ====================

    /// <summary>
    /// 把封印卡封印到场上的目标卡：建立双向引用、标记 IsSealed（标在目标卡上）。
    /// 封印卡移动到目标所在地区（统一由 ZoneManager 管理区域）。
    /// 守卫：目标卡必须处于场上（AreaZone），手牌/卡组的卡暂不可被封印（需求⑨）。
    /// 用法: seal &lt;封印卡编号|UID&gt; &lt;目标卡编号|UID&gt;
    ///  · 目标必须是场上卡（zoneFilter 保证不会错挑到卡组/手牌里的同编号卡）。
    ///  · 实际结算走既有唯一出口 `TurnManager.SealCard`（含 OnSealed 触发与持续前提重判）。
    /// </summary>
    private bool ExecuteSeal(string[] parts)
    {
        if (parts.Length < 3) return Fail("用法: seal <封印卡编号|UID> <目标卡编号|UID>");

        if (!TryResolveCard(parts[1], 0, null, out var seal, out string howSeal, out string e1))
            return Fail("未找到封印卡：" + e1);
        if (!TryResolveCard(parts[2], 0,
                c => c.CurrentZone == CardZone.AreaZone, out var target, out string howTarget, out string e2))
            return Fail("未找到场上目标卡：" + e2);

        if (seal.SealedBy != null) return Fail($"{seal.UID} 已被 {seal.SealedBy.UID} 封印，先 unseal");
        if (TurnManagerRef == null) return Fail("封印失败：TurnManager 未注入");

        // 【收口】走既有封印唯一出口（双向引用 + 标记 + 进封印子区域 + OnSealed 触发 + 持续重判），
        // 不再由调试命令手工拼装状态（避免与 ZoneManager 的封印连带逻辑打架）。
        if (!TurnManagerRef.SealCard(seal, target))
            return Fail($"封印失败：{seal.UID} → {target.UID}");

        GD.Print($"🔒 封印卡 {seal.PlayerTemplate?.CardName}({seal.UID}·{howSeal}) 封印目标 " +
                 $"{target.PlayerTemplate?.CardName}({target.UID}·{howTarget})（已移入封印子区域 {target.ZoneDetailId}）");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 把封印卡封印到指定地区（作为"封印于地区的封印"，独立存在于地区上，不依附角色）。
    /// 封印卡移动到该争夺区，UI 挂 GetAreaContainer（无偏移，区别于封印角色时的向上偏移）。
    /// 用法: sealarea &lt;封印卡编号|UID&gt; &lt;地区编号 1..N&gt;
    /// </summary>
    private bool ExecuteSealToArea(string[] parts)
    {
        if (parts.Length < 3) return Fail("用法: sealarea <封印卡编号|UID> <地区编号>");

        if (!TryResolveCard(parts[1], 0, null, out var seal, out string howSeal, out string e1))
            return Fail("未找到封印卡：" + e1);
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1 || areaNo > CurrentAreaUids.Count)
            return Fail($"无效地区编号，当前共 {CurrentAreaUids.Count} 个(1..{CurrentAreaUids.Count})");
        if (_zoneManager == null) return Fail("封印失败：ZoneManager 未注入");

        // 封印于地区：SealedBy=null + ZoneDetailId=地区UID 标记，并置于该争夺区（对称 attacharea）
        // 封印于地区不标 IsSealed（IsSealed 用于单卡压制守卫）；地区封印靠 SealedBy=null + ZoneDetailId 标识
        var areaUid = CurrentAreaUids[areaNo - 1];
        seal.SealedBy = null;              // 地区封印不指向具体角色
        seal.IsSeal = true;                // 标记本卡为封印卡（封印于地区），供地区赢取/UI 识别
        seal.IsSealed = false;
        seal.ZoneDetailId = areaUid;
        _zoneManager.MoveToSealedZone(seal, areaUid, seal.ControllerId == 0 ? 1 : seal.ControllerId);
        GD.Print($"🔒 封印卡 {seal.PlayerTemplate?.CardName}({seal.UID}·{howSeal}) 封印到地区{areaNo}({areaUid})");
        _onZoneChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 把指定玩家的所有手牌放回其卡组底部（不洗牌，保持原手牌顺序）。
    /// 手牌移动会修改集合，故先复制一份手牌列表再逐个 MoveToDeckBottom。
    /// 用法: returnhandtodeck <玩家 1|2>
    /// </summary>
    private void ExecuteReturnHandToDeckBottom(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int playerId) || (playerId != 1 && playerId != 2))
        { GD.Print("❌ 用法: returnhandtodeck <玩家 1|2>"); return; }

        var hand = _cardManager.GetHandCardsByOwner(playerId);
        if (hand == null || hand.Count == 0)
        { GD.Print($"⚠️ 玩家{playerId} 手牌为空，无需放回卡组"); return; }

        int moved = 0;
        foreach (var inst in hand.ToList())   // 复制快照，避免 MoveToDeckBottom 修改手牌集合引发遍历异常
        {
            if (inst == null) continue;
            _zoneManager.MoveToDeckBottom(inst, playerId);
            moved++;
        }
        _onZoneChanged?.Invoke();
        GD.Print($"✅ 玩家{playerId} 的 {moved} 张手牌已放回卡组底部（未洗牌）");
    }

    private void ExecuteHelp()
    {
        // 用**原始字符串字面量**（"""）承载帮助正文：正文里含有直角引号与双引号，@"" 会被提前截断（CS1010）。
        GD.Print("""
========== PM命令帮助（2026-10 修订：玩家参数第一位） ==========
  ★ 统一口径：凡是带卡牌参数的命令，**玩家参数（1=P1 / 2=P2）必须在第一位**，
    它同时决定【从哪个玩家的卡组/区域找牌】与【把卡加到哪个玩家】。
    玩家参数缺失或不是 1|2 一律报错（不会再静默按 P1 处理）。

  ---- 抽牌 ----
  draw <玩家 1|2>                  - 该玩家从牌库顶抽 1 张
  draw <玩家 1|2> <数量>           - 该玩家从牌库顶抽 <数量> 张（如 draw 1 5）
  draw <玩家 1|2> <卡牌编号>       - 【定向取牌·1张】从**该玩家卡组**取该编号的牌加入**该玩家**手牌
                                    编号＝模板 UID 下划线前的部分，不区分大小写（如 JC015）
                                    卡组里没有 → 明确报错（含该编号在双方卡组/区域的分布），
                                    绝不静默降级为普通抽牌
                                    例：draw 2 JC015 ＝ P2 卡组取 1 张 JC015 给 P2
  draw <玩家 1|2> 1 <卡牌编号>     - 同上（显式数量只允许 1；**编号取牌不支持多张**）
  注：按编号取牌**不算"抓牌"**（不触发"每当你抓牌时"、不受牌库底抓牌重定向、空库不判负）
  player1draw [数量] / player2draw [数量] - 固定玩家号抽牌（牌库顶）

  ---- 移动 / 建资产 ----
  move <玩家 1|2> <卡牌编号|实例UID> <目标> [目标玩家 1|2] [top|bottom]
                                   - 从**该玩家**卡组/区域找牌并移动；
                                     目标: 1..N(地区编号) / hand / grave / deck[top|bottom] / score / asset
                                     目标玩家缺省＝搜索玩家；地区号越界会明确报错
  setassetfromdeck <玩家 1|2> <num>        - 从**该玩家**卡组顶取 num 张进**该玩家**资产区
  setassetfromdeck <玩家 1|2> <卡牌编号> [num] - 从该玩家卡组按编号取（缺省 1 张）

  ---- 附属 / 封印（**不带玩家参数**，恢复原签名；两个卡牌参数都可用 实例UID 或 卡牌编号）----
  attach <附属编号|UID> <宿主编号|UID>       - 结附到**场上**宿主（宿主自动限定场上卡）
  attacharea <附属编号|UID> <地区编号>       - 结附到指定地区
  detach <附属UID>                           - 解除结附（仅断引用）
  seal <封印卡编号|UID> <目标编号|UID>       - 封印到**场上**目标卡
  sealarea <封印卡编号|UID> <地区编号>       - 封印到指定地区

  ---- 对抗 ----
  investigation <玩家 1|2> <地区 areaNo> <X> - 调查对抗：胜者查看牌库顶 X 张、分配放回后抽 1
                                               单机＝弹本机窗口；联机＝经决策协议下发给该玩家
  battle <玩家 1|2> <地区 areaNo> <X>        - 战斗对抗：败方该地区角色分配 X 点伤害（同上，单机/联机）
  power <玩家 1|2> <地区 areaNo> <点数>      - 势力对抗：**瞬时结算**（直接加势力标记，不弹窗、不阻塞批量）
  jumpto <步骤名>[地区号]     - 【跳转步骤】直接跳到该步骤，**中间经过的步骤一律不结算**
                                步骤名用枚举名：StartReset/StartPrepare/StartDraw/ActionFirst/ActionSecond/
                                PreCombat/CombatInvestigation/CombatStrength/CombatPower/EndCleanup/
                                EndRecover/EndPassTurn；对抗类可带地区号：
                                jumpto CombatInvestigation1 ＝跳到 1 地区调查对抗（省略＝地区1）

  ---- 其它 ----
  damage <UID> <数值> / damage card <UID> <数值>      - 对指定卡牌造成伤害（card 可省，两种写法等价）
  damage area <地区索引> <数值>                       - 对地区所有角色造成伤害（索引从 0 开始）
  damage hand <手牌索引> <数值>                       - 对 P1 手牌指定位置造成伤害（索引从 0 开始）
  trauma <UID> <数值> / trauma card|area|hand ...     - 同 damage，但造成创伤
  reset / list                - reset 重置伤害；list 列出全场卡牌状态（含 UID，配合 <UID> 参数用）
  kill <UID> / kill card <UID>  - 强制杀死卡牌（打满防御走正常死亡结算；两种写法等价）
  heal <UID> [数值] / heal card <UID> [数值] - 清除伤害（不填数值＝清空伤害与创伤）
  tap/untap/flip <UID>；covert <UID> [on|off]
  deck [1|2] / printdeck      - 按卡组内真实顺序打印卡组内容（索引0=牌库顶，默认玩家1）
  addarea [数量]              - 从世界牌库抽地区卡并新建地区槽位（默认1个）
  returnhandtodeck <玩家 1|2> - 把指定玩家所有手牌放回其卡组底部（不洗牌）
  help / ?                    - 显示帮助

【多行输入】
  Enter        换行（可输入多条命令，每行一条）
  Shift+Enter  执行全部命令（从上往下依次执行）
  以 # 开头的行视为注释，空行跳过
  注意：单机遇到 investigation / battle（需弹本机窗口等待玩家）时，
        执行完该行即中止，后续命令全部舍弃；
        联机（服务器侧不弹本机窗口）与 power（瞬时结算）不中止，后面命令照常执行。

示例:
  draw 2 JC015               - P2 卡组取 1 张 JC015 加入 P2 手牌（不是 P1！）
  draw 1 5                   - P1 从牌库顶抽 5 张
  draw 1 JC001               - P1 卡组取 1 张 JC001 加入 P1 手牌
  move 2 JC014 hand          - P2 卡组取 1 张 JC014 加入 P2 手牌
  move 1 inst_37 2           - P1 的实例 inst_37 移动到争夺区 2
  setassetfromdeck 1 12      - P1 卡组顶 12 张进 P1 资产区
  attacharea JC012 1         - JC012 结附到地区1（不带玩家参数）
  seal JC020 <目标编号|UID>   - JC020 封印到场上目标卡
  investigation 2 1 1        - P2 在地区1 调查对抗获胜 X=1
  battle 1 1 2               - P1 在地区1 战斗对抗获胜 2 点
  power 1 1 5                - P1 在地区1 加 5 个势力标记（瞬时）
  jumpto CombatInvestigation1 - 跳到1地区调查对抗
  jumpto EndRecover          - 跳到恢复步骤（中间步骤不结算）
=================================
""");
    }
}