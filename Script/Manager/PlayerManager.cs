using Godot;
using Script.Game.Entities;
using Script.Data.Cards;
using Script.Enums;
using System;
using System.Collections.Generic;

public partial class PlayerManager : Node
{
    // ============================================================
    // 玩家数据类（仅存储玩家属性，卡牌列表由 CardManager 统一管理）
    // ============================================================
    public class PlayerData
    {
        public int PlayerId;
        public string Name;
        public bool IsFirstPlayer = false;
        public int DeployCovertCount = 1;
        public int EstablishAssetCount = 1;
        public int FirstPrivilegeCount = 1;
    }

    /// <summary>
    /// 胜利规则（2026-08-05 落地）：
    /// 命中任一即判该玩家获胜（对手失败）：
    ///  (1) 总分达标胜 —— 某玩家实时总分 ≥ VICTORY_SCORE_THRESHOLD；
    ///      若同回合双方均达标，取总分更高者；仍相等则本回合不判胜（继续）。
    ///  (2) 对方牌库耗尽负 —— 某玩家牌库（DeckZone）为空（无牌可抓）即判其对手获胜。
    /// 阈值设为常量，便于规则调整。
    /// </summary>
    public const int VICTORY_SCORE_THRESHOLD = 8;

    // ============================================================
    // 游戏结束状态与事件（2026-08-07 落地）
    // 胜利/失败判定唯一收口于 PlayerManager；UI 仅订阅 OnGameOver 展示，不写规则。
    // 触发胜利瞬间（分数达阈值/对方牌库耗尽/投降）立即结算：丢弃未结算堆叠效果、广播事件。
    // ============================================================
    private bool _isGameOver = false;
    /// <summary>游戏是否已结束（幂等保护，避免重复结算）</summary>
    public bool IsGameOver => _isGameOver;
    /// <summary>游戏结束事件，参数为获胜玩家 ID（1 或 2）；UI 订阅以弹胜负 LABEL 并切回主界面</summary>
    public event Action<int> OnGameOver;

    // ============================================================
    // 玩家数据
    // ============================================================
    public PlayerData Player1 { get; private set; }
    public PlayerData Player2 { get; private set; }

    /// <summary>
    /// 初始化玩家数据。抽成独立方法以便构造函数与 _Ready 复用：
    /// 测试中直接 new PlayerManager() 不经 Godot 场景树（_Ready 不触发），
    /// 故构造函数中预建 Player1/Player2，避免 StartGame→SetFirstPlayer 访问到 null。
    /// _Ready 中再调一次保持幂等（场景树晚绑定覆盖）。
    /// </summary>
    private void InitPlayers()
    {
        Player1 = new PlayerData { PlayerId = 1, Name = "玩家1" };
        Player2 = new PlayerData { PlayerId = 2, Name = "玩家2" };
    }

    public PlayerManager()
    {
        InitPlayers();
    }

    public override void _Ready()
    {
        InitPlayers();
        GD.Print("✅ PlayerManager 初始化完成");
    }

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 调用：复位游戏结束标记并重建玩家数据（计数/先手复位为默认）。
    /// 先手归属由后续对局流程（掷先手/首回合）重新决定。
    /// </summary>
    public void Reset()
    {
        _isGameOver = false;
        Player1 = new PlayerData { PlayerId = 1, Name = "玩家1" };
        Player2 = new PlayerData { PlayerId = 2, Name = "玩家2" };
        GD.Print("[PlayerManager] Reset: 玩家数据已重建, _isGameOver=false");
    }

    public PlayerData GetPlayer(int id) => id == 1 ? Player1 : Player2;
    public PlayerData GetCurrentPlayer() => Player1; // 本地玩家（玩家1）

    // ============================================================
    // 分数累计（核心需求 2.2 / 6.6）：分数区内所有卡（含角色/附属/赢取地区实例）的 Score 相加
    // 彻底方案B：地区卡实例赢取时 MoveToScore 进 ScoreZone，总分由 GetScoreTotal 实时计算，
    // 不再依赖独立的 WonAreaScore 字段。
    // ============================================================
    /// <summary>实时计算某玩家总分 = 分数区所有卡（角色/附属/地区实例）的 Score 之和。
    /// 地区卡实例通过 Template.Score 取分（BaseCardData 基类属性，玩家卡与地区卡通用）。</summary>
    public int GetScoreTotal(int playerId)
    {
        if (_zoneManager == null) return 0;
        int total = 0;
        foreach (var card in _zoneManager.GetZoneCards(CardZone.ScoreZone, playerId))
        {
            if (card?.Template != null) total += card.Template.Score;
        }
        return total;
    }

    // ============================================================
    // 基础行动次数（派遣暗藏 / 建立资产）：每回合各 1 次
    // 仅"成功完成行动"才扣减；切换蒙版等纯 UI 行为不扣减；次数为 0 禁止触发
    // ============================================================
    /// <summary>查询某玩家是否还有该基础行动的剩余次数（>0 才可点击对应按钮）</summary>
    public bool HasDeployCovertCharge(int playerId) => GetPlayer(playerId).DeployCovertCount > 0;
    public bool HasEstablishAssetCharge(int playerId) => GetPlayer(playerId).EstablishAssetCount > 0;

    /// <summary>成功完成派遣暗藏后扣减 1 次（返回是否扣减成功）</summary>
    public bool ConsumeDeployCovert(int playerId)
    {
        var p = GetPlayer(playerId);
        if (p.DeployCovertCount <= 0) return false;
        p.DeployCovertCount--;
        GD.Print($"⚑ 玩家{playerId} 派遣暗藏次数 -1，剩余 {p.DeployCovertCount}");
        return true;
    }

    /// <summary>成功完成建立资产后扣减 1 次（返回是否扣减成功）</summary>
    public bool ConsumeEstablishAsset(int playerId)
    {
        var p = GetPlayer(playerId);
        if (p.EstablishAssetCount <= 0) return false;
        p.EstablishAssetCount--;
        GD.Print($"⚑ 玩家{playerId} 建立资产次数 -1，剩余 {p.EstablishAssetCount}");
        return true;
    }

    /// <summary>回合开始重置：恢复基础行动次数 与 破平特权次数 为 1</summary>
    public void ResetActionCharges(int playerId)
    {
        var p = GetPlayer(playerId);
        p.DeployCovertCount = 1;
        p.EstablishAssetCount = 1;
        p.FirstPrivilegeCount = 1;   // 破平特权次数恢复（与派遣暗藏/建立资产一致）
        GD.Print($"🔄 玩家{playerId} 基础行动次数已重置（派遣暗藏/建立资产/先手破平 各 1）");
    }

    // ============================================================
    // 先手破平特权（2026-08-05 落地，MVC：纯逻辑层，不触 UI）
    // 规则：仅先手玩家可用；触发需同时满足「有可用费用」与「破平次数>0」；
    //       确认破平消耗 1 费用（横置 1 张未横置资产）+ 1 次破平次数。
    // ============================================================
    /// <summary>先手玩家当前是否可发起破平：有可用费用 且 破平次数>0。</summary>
    public bool CanTieBreak(int playerId)
    {
        var p = GetPlayer(playerId);
        if (p == null || p.FirstPrivilegeCount <= 0) return false;
        return GetAvailableMana(playerId) > 0;
    }

    /// <summary>消耗一次破平特权：横置 1 张未横置资产（费用）+ 破平次数-1。前置应已判 CanTieBreak。</summary>
    public void ConsumeTieBreak(int playerId)
    {
        var p = GetPlayer(playerId);
        if (p == null) return;
        // 消耗 1 费用（横置 1 张未横置资产），不足则跳过横置但不报错（理论上 CanTieBreak 已保证）
        if (GetAvailableMana(playerId) > 0) SpendMana(playerId, 1);
        p.FirstPrivilegeCount--;
        GD.Print($"⚡ 玩家{playerId} 使用先手破平特权：消耗 1 费用 + 1 次数，剩余破平次数 {p.FirstPrivilegeCount}");
    }

    // ============================================================
    // 区域依赖（忠诚/费用都来自资产区，需要 ZoneManager）
    // ============================================================
    private ZoneManager _zoneManager;
    public void SetZoneManager(ZoneManager zoneManager) => _zoneManager = zoneManager;

    private StackManager _stackManager;

    // ============================================================
    // 忠诚系统（6.1）：资产区每张卡按其颜色/领域提供忠诚值
    // ============================================================
    public Dictionary<CardColor, int> GetColorLoyalty(int playerId)
    {
        var result = new Dictionary<CardColor, int>();
        if (_zoneManager == null) return result;
        foreach (var card in _zoneManager.GetZoneCards(CardZone.AssetZone, playerId))
        {
            var t = card.PlayerTemplate;
            if (t == null) continue;
            foreach (var c in t.Colors)
            {
                int cur = result.TryGetValue(c, out int v) ? v : 0;
                result[c] = cur + 1;
            }
        }
        return result;
    }

    public Dictionary<MagicDomain, int> GetDomainLoyalty(int playerId)
    {
        var result = new Dictionary<MagicDomain, int>();
        if (_zoneManager == null) return result;
        foreach (var card in _zoneManager.GetZoneCards(CardZone.AssetZone, playerId))
        {
            var t = card.PlayerTemplate;
            if (t == null) continue;
            foreach (var d in t.MagicDomains)
            {
                int cur = result.TryGetValue(d, out int v) ? v : 0;
                result[d] = cur + 1;
            }
        }
        return result;
    }

    /// <summary>检查玩家是否满足某张卡的忠诚需求（颜色+领域均≥需求，允许冗余）</summary>
    public bool CanPayLoyalty(int playerId, PlayerCardData card)
    {
        if (card == null) return true;
        return card.ValidateLoyalty(GetColorLoyalty(playerId), GetDomainLoyalty(playerId));
    }

    // ============================================================
    // 费用系统（6.2）：横置资产区卡牌产生无色费用，每张 1 点
    // 忠诚值不因资产横置而减少（统计时不区分横置状态）
    // ============================================================
    /// <summary>可用无色费用 = 未横置的资产卡数量 + **临时费用**（K19：JC115「你获得1」）</summary>
    public int GetAvailableMana(int playerId)
    {
        if (_zoneManager == null) return GetTemporaryMana(playerId);
        int count = GetTemporaryMana(playerId);
        foreach (var card in _zoneManager.GetZoneCards(CardZone.AssetZone, playerId))
            if (!card.IsTapped) count++;
        return count;
    }

    // ============================================================
    // 【P3·K19】临时费用池（JC115 备用资金「快速行动，横置：你获得1」）
    // 口径（用户 2026-09，§7.4b A26）：本作**没有"有色费用"**，所有费用都是**无色费用**；
    // 临时费用在**任意步骤结束时清除**（六个步骤：先手/后手/预对抗/调查对抗/战斗对抗/势力对抗，
    // 实现上＝`TurnManager.EnterPhase` 每次阶段流转时清空——每个步骤的结束都恰好是一次流转）。
    // 支付顺序：**优先花费临时费用**，不足的部分再横置资产卡（用户 2026-09 确认）。
    // ============================================================
    private readonly Dictionary<int, int> _temporaryMana = new Dictionary<int, int>();

    /// <summary>该玩家当前剩余临时费用。</summary>
    public int GetTemporaryMana(int playerId)
        => _temporaryMana.TryGetValue(playerId, out int v) ? v : 0;

    /// <summary>获得 N 点临时费用（可叠加；步骤结束时清除）。</summary>
    public void GrantTemporaryMana(int playerId, int amount)
    {
        if (playerId <= 0 || amount <= 0) return;
        _temporaryMana[playerId] = GetTemporaryMana(playerId) + amount;
        GD.Print($"⚡ [PlayerManager] 玩家{playerId} 获得 {amount} 点临时费用（当前 {_temporaryMana[playerId]}）");
    }

    /// <summary>清除某玩家的临时费用（步骤结束／重置时）。</summary>
    public void ClearTemporaryMana(int playerId)
    {
        if (_temporaryMana.TryGetValue(playerId, out int v) && v > 0)
            GD.Print($"⚡ [PlayerManager] 玩家{playerId} 步骤结束 → 清除临时费用 {v} 点");
        _temporaryMana.Remove(playerId);
    }

    /// <summary>清除双方的临时费用（`TurnManager.EnterPhase` 每次阶段流转时调用）。</summary>
    public void ClearAllTemporaryMana()
    {
        ClearTemporaryMana(1);
        ClearTemporaryMana(2);
    }

    /// <summary>支付费用：**先花临时费用**，不足部分横置 playerId 的未横置资产卡；不足返回 false</summary>
    public bool SpendMana(int playerId, int amount)
    {
        if (amount <= 0) return true;
        if (GetAvailableMana(playerId) < amount)
        {
            GD.Print($"❌ [PlayerManager] 可用费用不足，无法支付 {amount}（当前 {GetAvailableMana(playerId)}）");
            return false;
        }
        int remaining = amount;
        // ① 优先花费临时费用（K19，用户 2026-09 口径）
        int temp = GetTemporaryMana(playerId);
        if (temp > 0)
        {
            int used = System.Math.Min(temp, remaining);
            _temporaryMana[playerId] = temp - used;
            remaining -= used;
            GD.Print($"⚡ 玩家{playerId} 支付：先花临时费用 {used} 点（剩余临时费用 {_temporaryMana[playerId]}）");
        }
        // ② 不足部分横置资产卡
        var tappedNames = new List<string>();
        if (remaining > 0 && _zoneManager != null)
        {
            foreach (var card in _zoneManager.GetZoneCards(CardZone.AssetZone, playerId))
            {
                if (remaining <= 0) break;
                if (!card.IsTapped)
                {
                    card.SetTapped(true);   // 实例层面完成横置（SetTapped 发 TappedChanged）
                    tappedNames.Add(card.PlayerTemplate?.CardName ?? card.UID);
                    remaining--;
                }
            }
        }
        GD.Print($"⚡ 玩家{playerId} 支付 {amount} 费用：横置资产卡 [{string.Join(", ", tappedNames)}]，实例 IsTapped 已置 true");
        return true;
    }

    /// <summary>重置步骤：解除所有资产卡横置（费用在回合开始恢复）</summary>
    public void UntapAssets(int playerId)
    {
        if (_zoneManager == null) return;
        foreach (var card in _zoneManager.GetZoneCards(CardZone.AssetZone, playerId))
            card.SetTapped(false);   // SetTapped 发 TappedChanged，统一横置/重置事件链
    }

    // ============================================================
    // 先手标志
    // ============================================================
    public void SetFirstPlayer(int playerId)
    {
        Player1.IsFirstPlayer = (playerId == 1);
        Player2.IsFirstPlayer = (playerId == 2);
        GD.Print($"🏁 先手玩家设置为玩家{playerId}");
    }

    public bool IsFirstPlayer(int playerId)
    {
        var player = GetPlayer(playerId);
        return player?.IsFirstPlayer ?? false;
    }

    // ============================================================
    // 胜利规则判定（2026-08-05 落地）：见 VICTORY_SCORE_THRESHOLD 注释
    // ============================================================
    /// <summary>判断某玩家牌库（Deck）是否已空（无牌可抓）。牌库耗尽即触发"对方牌库耗尽负"。</summary>
    public bool IsDeckEmpty(int playerId)
        => _zoneManager != null && _zoneManager.GetZoneCards(CardZone.Deck, playerId).Count == 0;

    /// <summary>
    /// 判定当前是否满足游戏结束条件，返回获胜玩家 ID（1 或 2）；无胜者返回 0。
    /// 规则优先级：先手牌库耗尽（条件2）直接判对手胜；否则比较总分（条件1）。
    /// 若同回合双方均达标，取总分更高者；仍相等不判胜（返回 0）。
    /// </summary>
    public int CheckGameOver()
    {
        bool deck1Empty = IsDeckEmpty(1);
        bool deck2Empty = IsDeckEmpty(2);
        if (deck1Empty && !deck2Empty) { GD.Print("🏁 [胜利] 玩家1 牌库耗尽 → 玩家2 获胜"); TriggerGameOver(2); return 2; }
        if (deck2Empty && !deck1Empty) { GD.Print("🏁 [胜利] 玩家2 牌库耗尽 → 玩家1 获胜"); TriggerGameOver(1); return 1; }
        if (deck1Empty && deck2Empty)  { GD.Print("🏁 [胜利] 双方牌库均耗尽 → 按总分高者胜"); }

        int s1 = GetScoreTotal(1);
        int s2 = GetScoreTotal(2);
        bool win1 = s1 >= VICTORY_SCORE_THRESHOLD;
        bool win2 = s2 >= VICTORY_SCORE_THRESHOLD;
        if (win1 && !win2) { GD.Print($"🏁 [胜利] 玩家1 总分达标 {s1}≥{VICTORY_SCORE_THRESHOLD} → 玩家1 获胜"); TriggerGameOver(1); return 1; }
        if (win2 && !win1) { GD.Print($"🏁 [胜利] 玩家2 总分达标 {s2}≥{VICTORY_SCORE_THRESHOLD} → 玩家2 获胜"); TriggerGameOver(2); return 2; }
        if (win1 && win2)
        {
            if (s1 > s2) { GD.Print($"🏁 [胜利] 双方达标，玩家1 总分更高 {s1}>{s2} → 玩家1 获胜"); TriggerGameOver(1); return 1; }
            if (s2 > s1) { GD.Print($"🏁 [胜利] 双方达标，玩家2 总分更高 {s2}>{s1} → 玩家2 获胜"); TriggerGameOver(2); return 2; }
            GD.Print($"⚖️ [胜利] 双方均达标且总分相等 {s1}={s2}，本回合不判胜（继续）");
            return 0;
        }
        return 0;
    }

    /// <summary>
    /// 触发游戏结束并广播胜利事件（不经由堆叠，立即结算）。
    /// 幂等：已结束时直接 return，避免重复结算。
    /// 行为：丢弃未结算堆叠效果（StackManager.ClearAll），置 _isGameOver，Invoke OnGameOver(winnerId)。
    /// </summary>
    public void TriggerGameOver(int winnerId)
    {
        if (_isGameOver)
        {
            GD.Print($"⚠️ [胜利] 游戏已结束，忽略重复 TriggerGameOver({winnerId})");
            return;
        }
        _isGameOver = true;
        // 丢弃未结算堆叠效果（分数达标/牌库耗尽/投降均不经过堆叠即时结算）
        if (_stackManager != null) _stackManager.ClearAll();
        GD.Print($"🏁 [胜利] 游戏结束，获胜玩家 = 玩家{winnerId}（未结算堆叠效果已丢弃）");
        OnGameOver?.Invoke(winnerId);
    }

    /// <summary>玩家投降：直接判该玩家失败（对手获胜）。仅 UI 投降按钮调用。</summary>
    public void Concede(int loserId)
    {
        if (_isGameOver) return;
        int winner = loserId == 1 ? 2 : 1;
        GD.Print($"🏳️ [胜利] 玩家{loserId} 投降 → 玩家{winner} 获胜");
        TriggerGameOver(winner);
    }

    /// <summary>注入 StackManager 引用，供 TriggerGameOver 丢弃未结算效果（避免硬依赖构造顺序）</summary>
    public void SetStackManager(StackManager stackManager) => _stackManager = stackManager;

    // ============================================================
    // C/S 化·第二批：客户端投影恢复（仅联网重连/投影重建调用，对单机零影响）
    // AvailableMana 为派生值（由 ZoneManager 资产区实时计算），客户端重建资产区后自动正确，无需写回；
    // 这里只恢复无法派生的状态：先手标志、游戏是否结束、基础行动次数。
    // ============================================================
    /// <summary>从服务端玩家快照恢复客户端投影的玩家运行时状态（不触发 OnGameOver 事件，纯数据覆盖）。
    /// 注意：先手标志不在此恢复——它由 TurnManager.FirstPlayerId 派生，launcher 会在恢复 TurnManager 后
    /// 调用 SetFirstPlayer(firstPlayerId) 统一设置，避免此处读取不可靠的快照字段。</summary>
    public void ApplySnapshot(System.Collections.Generic.List<Script.Net.PlayerSnapshot> players)
    {
        if (players == null) return;
        _projectedDeployCovertBlocked.Clear();
        foreach (var p in players)
        {
            var data = GetPlayer(p.PlayerId);
            if (data == null) continue;
            if (p.IsGameOver) _isGameOver = true;
            // 【P1】全局封锁秘密派遣（JC068-B）：服务端权威态随快照投影到客户端，
            // 供客户端派遣门禁（DeployCovertEligibility）与服务端同解。
            if (p.DeployCovertBlocked) _projectedDeployCovertBlocked.Add(p.PlayerId);
        }
    }

    // ============================================================
    // 【P1】客户端投影的"全局封锁秘密派遣"态（JC068-B）
    // 服务端权威在 EffectEngine 的封锁表（持续效果注册表）；客户端无该引擎，只能读快照投影值。
    // 命名带 Projected 以免与服务端权威查询混淆。
    // ============================================================
    private readonly System.Collections.Generic.HashSet<int> _projectedDeployCovertBlocked
        = new System.Collections.Generic.HashSet<int>();

    /// <summary>该玩家（按快照投影）当前是否被封锁秘密派遣。快照未携带/未收到时为 false。</summary>
    public bool IsDeployCovertBlockedProjected(int playerId) => _projectedDeployCovertBlocked.Contains(playerId);
}

