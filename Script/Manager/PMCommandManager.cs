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
    public void InjectModelRefs(CardManager cardManager, DeckManager deckManager, ZoneManager zoneManager)
    {
        _cardManager ??= cardManager;
        _deckManager ??= deckManager;
        _zoneManager ??= zoneManager;
    }

    /// <summary>显式注入的 EffectEngine 引用（静态单例治理：核心逻辑不再依赖 EffectEngine.Instance）。</summary>
    public Script.Manager.EffectEngine EffectEngineRef { get; set; }

    /// <summary>显式注入的 TurnManager 引用（`jumpto` 跳转步骤 / 定向取牌从牌库移出用）。</summary>
    public TurnManager TurnManagerRef { get; set; }

    /// <summary>定向取牌（`draw &lt;数量&gt; &lt;卡牌编号&gt;`）挑选"多张同名同编号"用的随机源。</summary>
    private readonly System.Random _rng = new System.Random();

    /// <summary>未显式注入时回退静态 Instance（向后兼容）。</summary>
    private Script.Manager.EffectEngine EE => EffectEngineRef ?? Script.Manager.EffectEngine.Instance;

    /// <summary>实时获取当前按槽位顺序的地区 UID 列表（方案②：每次命令执行时查询，不持有过期快照）。</summary>
    private List<string> CurrentAreaUids => _getAreaUids != null ? _getAreaUids() : new List<string>();

    /// <summary>
    /// 阻塞类命令：会打开对抗窗口并等待玩家响应。
    /// 批量执行时，遇到这类命令执行完即停止，后续命令全部丢弃。
    /// </summary>
    private static readonly HashSet<string> BlockingCommands = new HashSet<string>
    {
        "investigation", "battle", "power"
    };

    /// <summary>判断一行命令是否为阻塞类（对抗）命令</summary>
    public static bool IsBlockingCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        return BlockingCommands.Contains(parts[0].ToLower());
    }

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

            if (IsBlockingCommand(line))
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

    public void ExecuteCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return;
        var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        GD.Print($"🛠️ 执行PM命令: {cmd}");

        switch (parts[0].ToLower())
        {
            case "damage": ExecuteDamage(parts); break;
            case "trauma": ExecuteTrauma(parts); break;
            case "reset": ExecuteReset(); break;
            case "list": ExecuteList(); break;
            case "kill": ExecuteKill(parts); break;
            case "heal": ExecuteHeal(parts); break;
            case "draw": ExecuteDraw(parts); break;
            // 【2026-09 新增】直接跳转步骤（中间经过的步骤一律不结算）
            case "jumpto": case "jump": ExecuteJumpTo(parts); break;
            case "move": ExecuteMove(parts); break;
            case "tap": ExecuteTap(parts); break;
            case "untap": ExecuteUntap(parts); break;
            case "flip": ExecuteFlip(parts); break;
            case "covert": ExecuteCovert(parts); break;
            case "printdeck": case "deck": ExecuteDeck(parts); break;
            case "player1draw": ExecuteDrawAlias(parts, 1); break;
            case "player2draw": ExecuteDrawAlias(parts, 2); break;
            case "investigation": ExecuteInvestigation(parts); break;
            case "battle": ExecuteStrength(parts); break;
            case "power": ExecutePower(parts); break;
            case "setassetfromdeck": ExecuteSetAssetFromDeck(parts); break;
            case "addarea": ExecuteAddArea(parts); break;
            case "attach": ExecuteAttach(parts); break;
            case "attacharea": ExecuteAttachToArea(parts); break;
            case "detach": ExecuteDetach(parts); break;
            case "seal": ExecuteSeal(parts); break;
            case "sealarea": ExecuteSealToArea(parts); break;
            case "returnhandtodeck": ExecuteReturnHandToDeckBottom(parts); break;
            case "help": case "?": ExecuteHelp(); break;
            default: GD.Print($"❌ 未知命令: {parts[0]}，输入 help 查看帮助"); break;
        }
    }

    // ==================== 子命令实现 ====================

    private void ExecuteDamage(string[] parts)
    {
        if (parts.Length < 4) { GD.Print("❌ 用法: damage area|card|hand 目标 数值"); return; }
        if (!int.TryParse(parts[3], out int amount) || amount <= 0) { GD.Print("❌ 无效数值"); return; }

        if (parts[1] == "area")
        {
            if (!int.TryParse(parts[2], out int areaIdx) || areaIdx < 0 || areaIdx >= CurrentAreaUids.Count)
            { GD.Print("❌ 无效地区索引"); return; }
            var instances = _cardManager.GetAreaInstancesBySlot(CurrentAreaUids[areaIdx]);
            foreach (var inst in instances)
                _cardManager.DealDamageToInstance(inst, amount, false);
            GD.Print($"✅ 对地区{areaIdx}的{instances.Count}个角色造成{amount}点伤害");
        }
        else if (parts[1] == "card")
        {
            var instance = _cardManager.GetInstance(parts[2]);
            if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[2]}"); return; }
            _cardManager.DealDamageToInstance(instance, amount, false);
            GD.Print($"✅ 对{instance.PlayerTemplate?.CardName}造成{amount}点伤害");
        }
        else if (parts[1] == "hand")
        {
            var hand = _cardManager.GetHandCardsByOwner(1);
            if (!int.TryParse(parts[2], out int idx) || idx < 0 || idx >= hand.Count)
            { GD.Print($"❌ 无效手牌索引，当前手牌数量: {hand.Count}"); return; }
            var inst = hand[idx];
            if (inst == null) { GD.Print("❌ 卡牌实例为空"); return; }
            _cardManager.DealDamageToInstance(inst, amount, false);
            GD.Print($"✅ 对手牌第{idx}张 {inst.PlayerTemplate?.CardName} 造成{amount}点伤害");
        }
        else
        {
            GD.Print("❌ 目标必须是 area、card 或 hand");
        }
    }

    private void ExecuteTrauma(string[] parts)
    {
        if (parts.Length < 4) { GD.Print("❌ 用法: trauma area|card|hand 目标 数值"); return; }
        if (!int.TryParse(parts[3], out int amount) || amount <= 0) { GD.Print("❌ 无效数值"); return; }

        if (parts[1] == "area")
        {
            if (!int.TryParse(parts[2], out int areaIdx) || areaIdx < 0 || areaIdx >= CurrentAreaUids.Count)
            { GD.Print("❌ 无效地区索引"); return; }
            var instances = _cardManager.GetAreaInstancesBySlot(CurrentAreaUids[areaIdx]);
            foreach (var inst in instances)
                _cardManager.DealDamageToInstance(inst, amount, true);
            GD.Print($"✅ 对地区{areaIdx}的{instances.Count}个角色造成{amount}点创伤");
        }
        else if (parts[1] == "card")
        {
            var instance = _cardManager.GetInstance(parts[2]);
            if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[2]}"); return; }
            _cardManager.DealDamageToInstance(instance, amount, true);
            GD.Print($"✅ 对{instance.PlayerTemplate?.CardName}造成{amount}点创伤");
        }
        else if (parts[1] == "hand")
        {
            var hand = _cardManager.GetHandCardsByOwner(1);
            if (!int.TryParse(parts[2], out int idx) || idx < 0 || idx >= hand.Count)
            { GD.Print($"❌ 无效手牌索引，当前手牌数量: {hand.Count}"); return; }
            var inst = hand[idx];
            if (inst == null) { GD.Print("❌ 卡牌实例为空"); return; }
            _cardManager.DealDamageToInstance(inst, amount, true);
            GD.Print($"✅ 对手牌第{idx}张 {inst.PlayerTemplate?.CardName} 造成{amount}点创伤");
        }
        else
        {
            GD.Print("❌ 目标必须是 area、card 或 hand");
        }
    }

    private void ExecuteReset()
    {
        _cardManager.ResetAllDamage();
        GD.Print("✅ 所有伤害标记已重置");
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

    private void ExecuteKill(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: kill card <UID>"); return; }
        var instance = _cardManager.GetInstance(parts[2]);
        if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[2]}"); return; }
        if (!instance.HasDefense) { GD.Print($"⚠️ {instance.PlayerTemplate?.CardName} 无防御，不能被杀死"); return; }
        // 经 CardManager 统一结算（含死亡判定与墓地处理），不再直接调 Model.TakeDamage
        _cardManager.DealDamageToInstance(instance, instance.CurrentDefense);
        GD.Print($"💀 杀死 {instance.PlayerTemplate?.CardName}");
    }

    private void ExecuteHeal(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: heal card <UID> [数值]"); return; }
        var instance = _cardManager.GetInstance(parts[2]);
        if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[2]}"); return; }

        if (parts.Length >= 4 && int.TryParse(parts[3], out int amount) && amount > 0)
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
    }

    /// <summary>
    /// `draw` —— 两种用法（**旧语法完全兼容**）：
    ///  ① **旧**：`draw &lt;玩家 1|2&gt; [数量]`（两个参数都是数字）→ 该玩家从牌库顶抽牌（走 UI 注入的抽牌回调）。
    ///  ② **新**：`draw &lt;数量&gt; &lt;卡牌编号&gt; [玩家 1|2]` → **定向取牌**：从该玩家（缺省 P1）**卡组**里
    ///     找到**该编号**的牌加入其手牌。编号＝UID 里**第一个下划线之前**的部分、不区分大小写
    ///     （如 `JC001` 命中 `JC001_shenmibunan`）；卡组里没有该编号的牌 → **什么都不做**（只打日志）；
    ///     有多张 → **随机**挑一张。`数量` 表示最多取几张（取满/取不到即停）。
    ///     简写 `draw &lt;卡牌编号&gt;`（省略数量）＝取 1 张。
    /// </summary>
    private void ExecuteDraw(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: draw <玩家 1|2> [数量] / draw <数量> <卡牌编号> [玩家 1|2]"); return; }

        // 旧语法：`draw <玩家> [数量]`（第一个参数是玩家号；第二个参数缺省或是数字）
        bool oldForm = int.TryParse(parts[1], out _)
                       && (parts.Length < 3 || int.TryParse(parts[2], out _));
        if (oldForm)
        {
            if (!int.TryParse(parts[1], out int playerId) || (playerId != 1 && playerId != 2))
            { GD.Print("❌ 用法: draw <玩家 1|2> [数量]"); return; }
            int count = 1;
            if (parts.Length >= 3 && int.TryParse(parts[2], out int n) && n > 0) count = n;
            _onDraw?.Invoke(playerId, count);
            GD.Print($"✅ 玩家{playerId} 抽牌 {count} 张（已加入手牌区）");
            return;
        }

        // 新语法：`draw <数量> <卡牌编号> [玩家]`（或简写 `draw <卡牌编号>`）
        int take = 1;
        string code;
        int player = 1;
        if (parts.Length == 2)
        {
            code = parts[1];                                   // 简写：draw JC001
        }
        else
        {
            if (!int.TryParse(parts[1], out take) || take <= 0)
            { GD.Print("❌ 用法: draw <数量> <卡牌编号> [玩家 1|2]"); return; }
            code = parts[2];
            if (parts.Length >= 4 && int.TryParse(parts[3], out int p) && (p == 1 || p == 2)) player = p;
        }
        DrawSpecificCards(player, take, code);
    }

    /// <summary>卡牌编号归一化：取 UID 中**第一个下划线之前**的部分（不区分大小写比较）。</summary>
    private static string NormalizeCardCode(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return "";
        int idx = uid.IndexOf('_');
        return (idx > 0 ? uid.Substring(0, idx) : uid).Trim();
    }

    /// <summary>`draw &lt;数量&gt; &lt;卡牌编号&gt;` 的实体：从玩家卡组定向取牌加入其手牌。</summary>
    private void DrawSpecificCards(int playerId, int count, string code)
    {
        if (TurnManagerRef == null || _deckManager == null || _zoneManager == null)
        { GD.Print("❌ 定向取牌失败：TurnManager/DeckManager/ZoneManager 未注入"); return; }
        string want = NormalizeCardCode(code);
        if (string.IsNullOrEmpty(want)) { GD.Print("❌ 卡牌编号为空"); return; }

        int moved = 0;
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var deck = playerId == 1 ? _deckManager.GetDeckPlayer1() : _deckManager.GetDeckPlayer2();
            var matches = new List<CardInstance>();
            foreach (var c in deck)
                if (c != null && string.Equals(NormalizeCardCode(c.PlayerTemplate?.UID), want,
                        StringComparison.OrdinalIgnoreCase))
                    matches.Add(c);
            if (matches.Count == 0) break;
            var pick = matches.Count == 1 ? matches[0] : matches[_rng.Next(matches.Count)];
            TurnManagerRef.RemoveCardFromDeckOf(playerId, pick);   // 先从牌库数据层移出（牌库不在区域桶里）
            _zoneManager.MoveToHand(pick, playerId);               // 再加入手牌（照常广播移动事件）
            names.Add(pick.PlayerTemplate?.CardName ?? pick.UID);
            moved++;
        }

        if (moved == 0)
        {
            GD.Print($"ℹ️ 玩家{playerId} 卡组中没有编号「{want}」的牌 → 什么都不做");
            return;
        }
        GD.Print($"✅ 定向取牌：玩家{playerId} 卡组 → 手牌 {moved} 张（编号「{want}」）：{string.Join("、", names)}");
        _onZoneChanged?.Invoke();
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
    private void ExecuteMove(string[] parts)
    {
        if (parts.Length < 4)
        {
            GD.Print("❌ 用法: move <UID> <玩家1|2> <目标>");
            GD.Print("   目标: 1..N(地区编号) / hand / grave / deck [top|bottom] / score / asset");
            return;
        }

        var uid = parts[1];
        if (!int.TryParse(parts[2], out int playerId) || (playerId != 1 && playerId != 2))
        { GD.Print("❌ 玩家必须是 1 或 2"); return; }

        var target = parts[3].ToLower();
        var card = _cardManager.GetInstance(uid);
        if (card == null) { GD.Print($"❌ 未找到卡牌: {uid}"); return; }

        // 移动前实例归属
        int ownerBefore = card.OwnerId;
        int controllerBefore = card.ControllerId;

        // 目标为纯数字 -> 争夺区编号（1..N）
        if (int.TryParse(target, out int areaNo) && areaNo >= 1 && areaNo <= CurrentAreaUids.Count)
        {
            var areaUid = CurrentAreaUids[areaNo - 1];
            _zoneManager.MoveToArea(card, areaUid, playerId);
            GD.Print($"✅ {card.PlayerTemplate?.CardName} 移动到玩家{playerId}的争夺区{areaNo} ({areaUid})");
        }
        else
        {
            switch (target)
            {
                case "hand":
                    _zoneManager.MoveToHand(card, playerId);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName} 加入玩家{playerId}手牌");
                    break;
                case "grave":
                    _zoneManager.MoveToGraveyard(card, playerId);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName} 进入墓地");
                    break;
                case "deck":
                case "deckbottom":
                    _zoneManager.MoveToDeckBottom(card, playerId);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName} 返回玩家{playerId}牌库底部");
                    break;
                case "decktop":
                    _zoneManager.MoveToDeckTop(card, playerId);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName} 返回玩家{playerId}牌库顶部");
                    break;
                case "score":
                    _zoneManager.MoveToScore(card, playerId);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName} 进入玩家{playerId}得分区");
                    break;
                case "asset":
                    _zoneManager.MoveToAsset(card, playerId);
                    GD.Print($"✅ {card.PlayerTemplate?.CardName} 进入玩家{playerId}资产区（保留状态）");
                    break;
                default:
                    GD.Print($"❌ 未知目标区域: {target}（支持 1..N(地区)/hand/grave/deck[top|bottom]/score/asset）");
                    return;
            }
        }

        // 移动前后实例归属对比输出
        GD.Print($"📦 移动归属变化 [{card.PlayerTemplate?.CardName} | {uid}]：" +
                 $"OwnerId {ownerBefore}→{card.OwnerId}，ControllerId {controllerBefore}→{card.ControllerId}");

        _onZoneChanged?.Invoke();
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
    /// 翻面：对正面实例翻为背面，对背面实例翻为正面。
    /// 无论正反翻面，均清空卡上指示物/附属/封印：
    ///   指示物直接移除；附属进入弃牌堆；封印回到 OWNER 手牌。
    /// 翻到正面时恢复卡牌正常属性（与正常打出一致）。
    /// </summary>
    private void ExecuteFlip(string[] parts)
    {
        if (parts.Length < 2) { GD.Print("❌ 用法: flip <UID>"); return; }
        var instance = _cardManager.GetInstance(parts[1]);
        if (instance == null) { GD.Print($"❌ 未找到卡牌: {parts[1]}"); return; }

        bool wasFaceUp = _getFaceUp?.Invoke(parts[1]) ?? false;

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
        _setFaceUp?.Invoke(parts[1], toFaceUp);   // 仅通知 UI 翻面，不持有 UI 实例
        if (toFaceUp) _refreshCardUI?.Invoke(parts[1]);

        GD.Print($"🔃 翻面 {instance.PlayerTemplate?.CardName}：{(wasFaceUp ? "正面→背面(潜伏/暗藏)" : "背面→正面(揭示)")}（已清空指示物/附属/封印，IsCovert={instance.IsCovert}）");
        _onZoneChanged?.Invoke();
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

    // ==================== 对抗窗口（调查/战斗） ====================
    private void ExecuteInvestigation(string[] parts)
    {
        // 用法: investigation <玩家 1|2> <地区 areaNo> <X>
        if (parts.Length < 4) { GD.Print("❌ 用法: investigation <玩家 1|2> <地区 areaNo> <X>"); return; }
        if (!int.TryParse(parts[1], out int winner) || (winner != 1 && winner != 2)) { GD.Print("❌ 玩家必须是 1 或 2"); return; }
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1) { GD.Print("❌ 无效地区编号"); return; }
        if (!int.TryParse(parts[3], out int x) || x <= 0) { GD.Print("❌ X 必须是正整数"); return; }

        // 通过注入的回调打开窗口（不直接持有 Arena），保持 MVC 边界
        if (_openCombatWindow == null) { GD.Print("❌ 对抗窗口回调未初始化"); return; }
        _openCombatWindow.Invoke(CombatManager.CombatType.Investigation, winner, x, areaNo);
        GD.Print($"✅ 打开调查对抗窗口（玩家P{winner}，地区area{areaNo}，X={x}）");
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

    private void ExecuteStrength(string[] parts)
    {
        // 用法: battle <玩家 1|2> <地区 areaNo> <获胜点 X>
        if (parts.Length < 4) { GD.Print("❌ 用法: battle <玩家 1|2> <地区 areaNo> <获胜点 X>"); return; }
        if (!int.TryParse(parts[1], out int winner) || (winner != 1 && winner != 2)) { GD.Print("❌ 玩家必须是 1 或 2"); return; }
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1) { GD.Print("❌ 无效地区编号"); return; }
        if (!int.TryParse(parts[3], out int x) || x <= 0) { GD.Print("❌ 获胜点 X 必须是正整数"); return; }

        // 通过注入的回调打开窗口（不直接持有 Arena），保持 MVC 边界
        if (_openCombatWindow == null) { GD.Print("❌ 对抗窗口回调未初始化"); return; }
        _openCombatWindow.Invoke(CombatManager.CombatType.Strength, winner, x, areaNo);
        GD.Print($"✅ 打开战斗对抗窗口（玩家P{winner}，地区area{areaNo}，获胜{x}点），点击败方该地区的角色分配伤害");
    }

    /// <summary>势力对抗：在指定地区为指定玩家直接添加势力标记。用法: power <玩家 1|2> <地区 1|2|3> <点数></summary>
    private void ExecutePower(string[] parts)
    {
        if (parts.Length < 4) { GD.Print("❌ 用法: power <玩家 1|2> <地区 1|2|3> <点数>"); return; }
        if (!int.TryParse(parts[1], out int playerId) || (playerId != 1 && playerId != 2)) { GD.Print("❌ 玩家必须是 1 或 2"); return; }
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1 || areaNo > CurrentAreaUids.Count) { GD.Print($"❌ 无效地区编号，当前共 {CurrentAreaUids.Count} 个地区(1..{CurrentAreaUids.Count})"); return; }
        if (!int.TryParse(parts[3], out int points) || points <= 0) { GD.Print("❌ 点数必须是正整数"); return; }

        if (_areaManager == null) { GD.Print("❌ AreaManager 未初始化"); return; }
        var areaUid = CurrentAreaUids[areaNo - 1];
        _areaManager.AddPowerMark(areaUid, playerId, points);
        GD.Print($"✅ 势力对抗：玩家{playerId} 在地区{areaNo} 获胜{points}点（已添加势力标记）");
        // AddPowerMark 内部已不再触发赢取检查（避免嵌套入栈打乱阶段节拍）；此处作为调试命令的收尾点统一触发。
        // 规则不变：标记达标即结算赢取地区；仅时机改由命令结尾（非嵌套上下文）进行。
        if (_stackManager != null)
            _areaManager.TryStartAreaWin(_stackManager);
    }

    /// <summary>快速建立资产：从玩家卡组顶部取 num 张置入资产区。用法: setassetfromdeck <玩家 1|2> <num></summary>
    private void ExecuteSetAssetFromDeck(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: setassetfromdeck <玩家 1|2> <num>"); return; }
        if (!int.TryParse(parts[1], out int playerId) || (playerId != 1 && playerId != 2)) { GD.Print("❌ 玩家必须是 1 或 2"); return; }
        if (!int.TryParse(parts[2], out int num) || num <= 0) { GD.Print("❌ num 必须是正整数"); return; }

        if (_deckManager == null) { GD.Print("❌ DeckManager 未初始化"); return; }
        var deck = playerId == 1 ? _deckManager.GetPlayer1DeckData() : _deckManager.GetPlayer2DeckData();
        int actual = Math.Min(num, deck.Count);
        if (actual == 0) { GD.Print($"⚠️ 玩家{playerId}卡组为空，无法建立资产"); return; }

        for (int i = 0; i < actual; i++)
        {
            var card = deck[i];
            _zoneManager.MoveToAsset(card, playerId);
            GD.Print($"✅ {card.PlayerTemplate?.CardName} 移入玩家{playerId}资产区");
        }
        GD.Print($"✅ 玩家{playerId} 从卡组顶部移入 {actual} 张至资产区（请求 {num} 张）");
        _onZoneChanged?.Invoke();
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

    /// <summary>
    /// 把附属结附到场上（或任意区域）的宿主卡：建立双向引用、标记 IsAttached。
    /// 不移动区域，要求附属与宿主当前处于同一区域（通常都已在争夺区）。
    /// 用法: attach <附属UID> <宿主UID>
    /// </summary>
    private void ExecuteAttach(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: attach <附属UID> <宿主UID>"); return; }
        var att = _cardManager.GetInstance(parts[1]);
        var host = _cardManager.GetInstance(parts[2]);
        if (att == null) { GD.Print($"❌ 未找到附属卡: {parts[1]}"); return; }
        if (host == null) { GD.Print($"❌ 未找到宿主卡: {parts[2]}"); return; }
        if (att.AttachedTo != null) { GD.Print($"⚠️ {att.UID} 已结附于 {att.AttachedTo.UID}，先 detach"); return; }

        att.AttachedTo = host;
        att.IsAttached = true;
        if (!host.AttachedCards.Contains(att))
            host.AttachedCards.Add(att);
        // 移动到附属子区域（不进 _area），CurrentZone=AttachedZone，UI 挂宿主容器；ZoneDetailId=宿主所在地区
        _zoneManager.MoveToAttachedZone(att, host.ZoneDetailId, att.ControllerId == 0 ? 1 : att.ControllerId);
        GD.Print($"🔗 附属 {att.PlayerTemplate?.CardName}({att.UID}) 结附到宿主 {host.PlayerTemplate?.CardName}({host.UID})");
    }

    /// <summary>
    /// 把附属结附到指定地区（作为"结附于地区的附属"，独立存在于地区上、不依附角色）。
    /// 用法: attacharea <附属UID> <地区编号 1..N>
    /// </summary>
    private void ExecuteAttachToArea(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: attacharea <附属UID> <地区编号>"); return; }
        var att = _cardManager.GetInstance(parts[1]);
        if (att == null) { GD.Print($"❌ 未找到附属卡: {parts[1]}"); return; }
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1 || areaNo > CurrentAreaUids.Count)
        { GD.Print($"❌ 无效地区编号，当前共 {CurrentAreaUids.Count} 个(1..{CurrentAreaUids.Count})"); return; }

        // 结附地区：用 ZoneDetailId 标记为地区附属（与角色附属区分），并置于该争夺区
        var areaUid = CurrentAreaUids[areaNo - 1];
        att.AttachedTo = null;             // 地区附属不指向具体角色
        att.IsAttached = true;
        att.ZoneDetailId = areaUid;
        _zoneManager.MoveToAttachedZone(att, areaUid, att.ControllerId == 0 ? 1 : att.ControllerId);
        GD.Print($"🔗 附属 {att.PlayerTemplate?.CardName}({att.UID}) 结附到地区{areaNo}({areaUid})");
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
    /// 把封印卡封印到场上（或任意区域）的目标卡：建立双向引用、标记 IsSealed（标在目标卡上）。
    /// 封印卡一律移动到目标所在争夺区（与 attacharea 行为一致，统一由 ZoneManager 管理区域）。
    /// 守卫：目标卡必须处于场上（AreaZone），手牌/卡组的卡暂不可被封印（需求⑨）。
    /// 用法: seal <封印卡UID> <目标卡UID>
    /// </summary>
    private void ExecuteSeal(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: seal <封印卡UID> <目标卡UID>"); return; }
        var seal = _cardManager.GetInstance(parts[1]);
        var target = _cardManager.GetInstance(parts[2]);
        if (seal == null) { GD.Print($"❌ 未找到封印卡: {parts[1]}"); return; }
        if (target == null) { GD.Print($"❌ 未找到目标卡: {parts[2]}"); return; }
        if (target.CurrentZone != CardZone.AreaZone)
        { GD.Print($"❌ 目标卡 {target.UID} 不在场上（当前区域={target.CurrentZone}），暂不可被封印（手牌/卡组封印后续支持）"); return; }
        if (seal.SealedBy != null) { GD.Print($"⚠️ {seal.UID} 已被 {seal.SealedBy.UID} 封印，先 unseal"); return; }

        // 封印卡移动到目标所在地区（ZoneDetailId=目标所在地区），与目标同区
        seal.SealedBy = target;            // 封印卡记录被谁封印（对称 AttachedTo）
        seal.IsSeal = true;                // 标记本卡为封印卡（镜像 IsAttached），供地区赢取/UI 识别
        seal.IsSealed = false;             // IsSealed 标在【目标卡】上，封印卡自身不标
        target.SealedCards.Add(seal);      // 目标卡记录封印了哪些（对称 AttachedCards）
        target.IsSealed = true;            // 标记被封印（供封印关系/触发触点识别）
        // 封印卡移动到封印子区域（不进 _area），CurrentZone=SealedZone，UI 在 ArrangeAllSeals 做向上偏移
        _zoneManager.MoveToSealedZone(seal, target.ZoneDetailId, seal.ControllerId == 0 ? 1 : seal.ControllerId);
        GD.Print($"🔒 封印卡 {seal.PlayerTemplate?.CardName}({seal.UID}) 封印目标 {target.PlayerTemplate?.CardName}({target.UID})（已移入封印子区域 {target.ZoneDetailId}）");
    }

    /// <summary>
    /// 把封印卡封印到指定地区（作为"封印于地区的封印"，独立存在于地区上、不依附角色）。
    /// 封印卡移动到该争夺区，UI 挂 GetAreaContainer（无偏移，区别于封印角色时的向上偏移）。
    /// 用法: sealarea <封印卡UID> <地区编号 1..N>
    /// </summary>
    private void ExecuteSealToArea(string[] parts)
    {
        if (parts.Length < 3) { GD.Print("❌ 用法: sealarea <封印卡UID> <地区编号>"); return; }
        var seal = _cardManager.GetInstance(parts[1]);
        if (seal == null) { GD.Print($"❌ 未找到封印卡: {parts[1]}"); return; }
        if (!int.TryParse(parts[2], out int areaNo) || areaNo < 1 || areaNo > CurrentAreaUids.Count)
        { GD.Print($"❌ 无效地区编号，当前共 {CurrentAreaUids.Count} 个(1..{CurrentAreaUids.Count})"); return; }

        // 封印于地区：SealedBy=null + ZoneDetailId=地区UID 标记，并置于该争夺区（对称 attacharea）
        // 封印于地区不标 IsSealed（IsSealed 用于单卡压制守卫）；地区封印靠 SealedBy=null + ZoneDetailId 标识
        var areaUid = CurrentAreaUids[areaNo - 1];
        seal.SealedBy = null;              // 地区封印不指向具体角色
        seal.IsSeal = true;                // 标记本卡为封印卡（封印于地区），供地区赢取/UI 识别
        seal.IsSealed = false;
        seal.ZoneDetailId = areaUid;
        _zoneManager.MoveToSealedZone(seal, areaUid, seal.ControllerId == 0 ? 1 : seal.ControllerId);
        GD.Print($"🔒 封印卡 {seal.PlayerTemplate?.CardName}({seal.UID}) 封印到地区{areaNo}({areaUid})");
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
        GD.Print(@"
========== PM命令帮助 ==========
  damage area <索引> <数值>   - 对地区所有角色造成伤害
  damage card <UID> <数值>    - 对指定卡牌造成伤害
  damage hand <索引> <数值>   - 对手牌指定位置造成伤害
  trauma ...                  - 同 damage，但造成创伤
  reset                       - 重置所有伤害标记
  list                        - 列出所有卡牌状态
  kill card <UID>             - 强制杀死卡牌
  heal card <UID> [数值]      - 清除伤害（不填则完全清除）
  draw <玩家 1|2> [数量]      - 指定玩家抽牌（默认1张，从牌库顶）
  draw <数量> <卡牌编号> [玩家] - 【定向取牌】从该玩家（缺省P1）卡组取指定编号的牌加入其手牌
                               编号＝UID 下划线前的部分，不区分大小写（如 JC001）；
                               卡组里没有 → 什么都不做；有多张 → 随机一张；数量＝最多取几张
                               简写 draw JC001 ＝取 1 张
  jumpto <步骤名>[地区号]     - 【跳转步骤】直接跳到该步骤，**中间经过的步骤一律不结算**
                               步骤名用枚举名：StartReset/StartPrepare/StartDraw/ActionFirst/ActionSecond/
                               PreCombat/CombatInvestigation/CombatStrength/CombatPower/EndCleanup/
                               EndRecover/EndPassTurn；对抗类可带地区号：
                               jumpto CombatInvestigation1 ＝跳到 1 地区调查对抗（省略＝地区1）
  player1draw [数量]          - 玩家1抽牌（默认1张）
  player2draw [数量]          - 玩家2抽牌（默认1张）
  move <UID> <玩家> <目标>    - 移动卡牌: 1..N(地区编号)/hand/grave/deck[top|bottom]/score/asset
  tap <UID>                   - 横置指定卡牌（实例 IsTapped=true）
  untap <UID>                 - 解除横置（实例 IsTapped=false）
  flip <UID>                  - 翻面（正反切换，清空指示物/附属/封印，翻到正面恢复基础属性）
  covert <UID> [on|off]      - 测试: 强制设/解除暗藏者(IsCovert)，用于验证暗藏者免疫伤害
  deck [1|2] / printdeck      - 按卡组内真实顺序打印卡组内容（索引0=牌库顶，默认玩家1）
  investigation <玩家 1|2> <地区 areaNo> <X> - 打开调查对抗窗口（胜者从牌库顶查看X张分配放回）
  battle <玩家 1|2> <地区 areaNo> <X>        - 打开战斗对抗窗口（败方该地区角色分配X点伤害）
  power <玩家 1|2> <地区 areaNo> <点数>       - 势力对抗：直接为指定玩家在地区添加势力标记
  setassetfromdeck <玩家 1|2> <num> - 从牌库顶部移 num 张卡到资产区
  addarea [数量]              - 从世界牌库抽地区卡并新建地区槽位（默认1个，自动重排/重叠）
  attach <附属UID> <宿主UID>   - 结附：把附属卡结附到场上目标卡（不移动区域）
  attacharea <附属UID> <地区>  - 结附地区：把附属卡结附到指定地区（移入争夺区）
  detach <附属UID>            - 解除结附（仅断引用，不移动区域）
  seal <封印卡UID> <目标卡UID>  - 封印：把封印卡封印到场上目标卡（封印卡移入目标所在地区）
  sealarea <封印卡UID> <地区>   - 封印地区：把封印卡封印到指定地区（移入争夺区，不偏移）
  returnhandtodeck <玩家 1|2>   - 把指定玩家所有手牌放回其卡组底部（不洗牌）
  help                        - 显示帮助

【多行输入】
  Enter        换行（可输入多条命令，每行一条）
  Shift+Enter  执行全部命令（从上往下依次执行）
  以 # 开头的行视为注释，空行跳过
  注意：遇到 investigation / battle / power 这类对抗命令时，
        执行完该行即中止，后续命令全部舍弃（需等待玩家响应）


  move inst_13 1 2           - 玩家1的卡 inst_13 移动到争夺区2
  move inst_5 2 hand          - 玩家2的卡 inst_5 加入手牌
  move inst_8 1 grave         - 卡 inst_8 进入墓地
  move inst_3 1 deck          - 卡 inst_3 返回玩家1牌库底部

示例:
  damage area 0 3
  damage card inst_5 2
  damage hand 0 1
  player1draw
  player2draw 2
  draw 1 JC001               - 从P1卡组取一张 JC001 加入P1手牌（没有则不加）
  jumpto CombatInvestigation1 - 跳到1地区调查对抗
  jumpto EndRecover          - 跳到恢复步骤（中间步骤不结算）
=================================");
    }
}