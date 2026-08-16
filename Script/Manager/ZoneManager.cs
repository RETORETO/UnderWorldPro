using Godot;
using Script.Data.Effects;
using Script.Game.Entities;
using Script.Enums;
using Script.Manager;
using System;
using System.Collections.Generic;

/// <summary>
/// ZoneManager — 统一的卡牌区域移动管理器（逻辑层，单一真相源）。
/// 负责所有 CardInstance 在 手牌/牌库/墓地/资产区/得分区/议会区/争夺区 之间的移动与归属列表维护。
/// 牌库的"卡组数据"(抽牌顺序)仍由 DeckManager 持有，本类在移动卡到牌库时委托 DeckManager。
///
/// 移动语义（自由移动阶段）：
/// - 每个 MoveTo* 都会把卡牌的 OwnerId / ControllerId 设为目标 playerId（正常流程传入的就是卡自身 owner，故无副作用）；
///   这保证了"玩家1的牌可进入玩家2的区域"等自由移动，后续完善规则时再在调用处加限制。
/// - 从牌库移走时同步从数据层牌库(DeckManager)移除，保证牌组计数(UI CountLabel 与逻辑)一致。
///
/// 移动完成后触发 OnCardMoved 事件，供 UI 层同步卡牌节点归属（逻辑→UI 自动驱动）。
/// </summary>
public partial class ZoneManager : Node
{
    // ============================================================
    // 依赖
    // ============================================================
    private CardManager _cardManager;
    private DeckManager _deckManager;

    /// <summary>卡牌移动完成后触发，供 UI 层同步卡牌节点归属。参数: (card, toZone, playerId, detailId)</summary>
    public event Action<CardInstance, CardZone, int, string> OnCardMoved;

    /// <summary>卡牌移动"全部后置刷新"完成后触发（ReevaluatePrereqs + 先手重算之后），
    /// 供 View 层集中刷新派生 UI（资产区领域忠诚标签、地区能力统计、分数、持续效果生效态等）。
    /// 与 OnCardMoved 同属 Model→View 单向事件总线，Model 不引用任何 UI 类型，符合 MVC 红线。</summary>
    public event Action<CardInstance, CardZone, int, string> OnPostMove;

    /// <summary>
    /// 牌库顺序被重新编排后触发（如调查对抗放回顶/底），参数为 (playerId, 编排后完整有序的牌库实例列表)。
    /// View 层据此【仅】按该有序列表重建 DeckZone 的 CardUI 顺序，不再由 View 自行计算谁去顶/底，
    /// 使牌库顺序的真相唯一归属于 ZoneManager，为 C/S 化（View 不在同进程）预留。
    /// </summary>
    public event Action<int, List<CardInstance>> OnDeckReordered;

    // ============================================================
    // 区域列表（单一真相源）
    // 手牌/墓地/资产/得分/议会 按玩家分桶；争夺区按 ZoneDetailId(地区UID) 混合双方
    // ============================================================
    private Dictionary<int, List<CardInstance>> _hand = new Dictionary<int, List<CardInstance>>();
    private Dictionary<int, List<CardInstance>> _graveyard = new Dictionary<int, List<CardInstance>>();
    private Dictionary<int, List<CardInstance>> _asset = new Dictionary<int, List<CardInstance>>();
    private Dictionary<int, List<CardInstance>> _score = new Dictionary<int, List<CardInstance>>();
    private Dictionary<int, List<CardInstance>> _council = new Dictionary<int, List<CardInstance>>();
    private List<CardInstance> _area = new List<CardInstance>();   // 争夺区
    private List<CardInstance> _attached = new List<CardInstance>(); // 附属子区域（实例独立附属区，不进 _area）
    private List<CardInstance> _sealed = new List<CardInstance>();   // 封印子区域（实例独立封印区，不进 _area）

    public void Initialize(CardManager cardManager, DeckManager deckManager)
    {
        _cardManager = cardManager;
        _deckManager = deckManager;
    }

    /// <summary>
    /// 对局级重置（B2：单场景常驻 + 显式 Reset）。
    /// 由 GameInitializer.ResetMatch 调用：清空全部区域桶并复位离场连带模式。
    /// 字典键无需预建——后续开局 Ensure 会自动重建；List 桶直接 Clear。
    /// </summary>
    public void Reset()
    {
        _hand.Clear();
        _graveyard.Clear();
        _asset.Clear();
        _score.Clear();
        _council.Clear();
        _area.Clear();
        _attached.Clear();
        _sealed.Clear();
        _detachMode = DetachMode.Normal;
        GD.Print("[ZoneManager] Reset: 全部区域桶已清空");
    }

    // ============================================================
    // 桶工具
    // ============================================================
    private List<CardInstance> Ensure(Dictionary<int, List<CardInstance>> dict, int playerId)
    {
        if (!dict.TryGetValue(playerId, out var list))
        {
            list = new List<CardInstance>();
            dict[playerId] = list;
        }
        return list;
    }

    /// <summary>离场连带模式：决定卡离开争夺区时，其结附的附属去向。
    /// - Normal：卡因其他卡效果 / 伤害系统离开场上（返回手牌、卡组、被封印）→ 附属进入【弃牌堆】。
    /// - AreaWon：仅"地区被赢取"特例 → 整地区内所有卡（角色 + 递归附属）返回各自拥有者【牌库底】。
    /// 默认 Normal；地区赢取结算时由 ResolveAreaWon 经 EnterAreaWonMode 临时切换，结算后复原。</summary>
    private enum DetachMode { Normal, AreaWon }
    private DetachMode _detachMode = DetachMode.Normal;

    /// <summary>在地区赢取结算期间，让离场连带（含递归附属）一律回牌库底而非进墓地。try/finally 保证复原。</summary>
    public void EnterAreaWonMode(Action body)
    {
        var prev = _detachMode;
        _detachMode = DetachMode.AreaWon;
        try { body(); }
        finally { _detachMode = prev; }
    }

    /// <summary>从所有区域列表移除该卡（移动前的统一清理）；若来自牌库则同步从数据层牌库移除。
    /// 当卡【离开争夺区(AreaZone)】前往非争夺区时，其结附的附属（及附属的附属，递归）按当前连带模式处置：
    /// - Normal（卡因效果/伤害离场）→ 附属进入弃牌堆（用户规则）。
    /// - AreaWon（地区被赢取特例）→ 附属返回各自拥有者牌库底。
    /// 封印卡跟随逻辑预留钩子 DetachSealedCards（封印系统未实现，本期不调用）。</summary>
    private void RemoveFromAllZones(CardInstance card, CardZone targetZone)
    {
        bool leavingArea = card.CurrentZone == CardZone.AreaZone && targetZone != CardZone.AreaZone;
        if (card.CurrentZone == CardZone.Deck)
            _deckManager.RemoveFromDeck(card);   // 按旧 OwnerId 定位玩家牌库并移除
        foreach (var dict in new[] { _hand, _graveyard, _asset, _score, _council })
            foreach (var list in dict.Values) list.Remove(card);
        _area.Remove(card);
        _attached.Remove(card);
        _sealed.Remove(card);

        // D19-⑦：离开争夺区时注销该卡自身的持久效果（清除授予与静止标记，并从注册表移除），
        // 避免残留的 _continuousRegistry 在卡再次 MOVE 回场时触发 RegisterPersistent 的防重 return，
        // 导致"不能被装备结附"/"白眼=先手调查"重新生效失败（JC001 重复进场 Bug）。
        // 附属于秘社/角色的附属卡（AttachedZone）也在 MoveToAttachedZone 注册了持续效果（JC068 封锁），
        // 其离场/被消灭时同样须注销，否则阿格斯IV离场后仍封锁对手派遣。
        bool leavingAttach = card.CurrentZone == CardZone.AttachedZone && targetZone != CardZone.AttachedZone;
        if (leavingArea || leavingAttach)
        {
            GD.Print($"[JC001DEBUG] 离场注销持久效果: {card.PlayerTemplate?.CardName} (UID={card.UID})");
            Script.Manager.EffectEngine.UnregisterPersistent(card);
        }

        // D49：附属自身离场（AttachedZone → 其他区，如 JC020 牺牲进墓地）——清结附关系。
        // 此前 leavingArea 只处理"宿主离场"（DetachAttachments*），不处理"附属自己离场"，
        // 导致 ResetToBaseState 清了 IsAttached 但 AttachedTo 残留、宿主 AttachedCards 残留已离场的附属
        // → JC020 回手牌后无法再次结附。必须在离场入口统一清（不在 BumpVersion 清，避免争夺区移动误清）。
        if (leavingAttach)
        {
            var host = card.AttachedTo;
            if (host != null)
            {
                host.AttachedCards?.Remove(card);
                card.AttachedTo = null;
            }
        }

        // D49：封印卡自身离场（SealedZone → 其他区）——清封印关系（与附属对称）。
        // 封印系统未实装（DetachSealedCards 为空实现），此分支当前不影响运行；封印实装后需测试确认。
        if (card.CurrentZone == CardZone.SealedZone && targetZone != CardZone.SealedZone)
        {
            var sealedTarget = card.SealedBy;
            if (sealedTarget != null)
            {
                sealedTarget.SealedCards?.Remove(card);
                card.SealedBy = null;
            }
        }

        // D49：被封印的卡（IsSealed）离场——其封印卡（SealedCards）按规则返回拥有者手牌（预留钩子 DetachSealedCards）。
        // 封印系统未实装，此分支随封印实装后需测试确认（封印卡是否应回拥有者手牌）。
        if (card.IsSealed && targetZone != CardZone.SealedZone)
        {
            DetachSealedCards(card);
        }

        // 离开场上：附属按连带模式处置（递归），封印卡跟随预留钩子
        if (leavingArea)
        {
            if (_detachMode == DetachMode.AreaWon)
                DetachAttachmentsToDeckBottom(card);
            else
                DetachAttachmentsToGraveyard(card);
            DetachSealedCards(card);   // 预留：封印未实现，空实现（模式同理）
        }
    }

    /// <summary>
    /// 宿主离开场上（地区赢取特例）时，将其结附的附属（按添加顺序，递归其附属层级）返回各自拥有者牌库底。
    /// 递归安全：附属自身 AttachedCards 为空时停止；MoveToDeckBottom 内部会再次经过本 detach 逻辑但无操作。
    /// 顺序要求（Q5）：逐个按序送入，供后续 UI 动画逐个播放。
    /// </summary>
    private void DetachAttachmentsToDeckBottom(CardInstance host)
    {
        if (host?.AttachedCards == null || host.AttachedCards.Count == 0) return;
        foreach (var att in host.AttachedCards)
        {
            att.AttachedTo = null;                       // 断开双向引用（宿主列表稍后 Clear）
            MoveToDeckBottom(att, att.OwnerId, false);   // 经统一移动：离开场上→若 att 有附属会递归（同 AreaWon 模式）
        }
        host.AttachedCards.Clear();
    }

    /// <summary>
    /// 宿主离开场上（卡因其他卡效果 / 伤害系统离场：返回手牌、卡组、被封印）时，
    /// 将其结附的附属（按添加顺序，递归其附属层级）送入各自拥有者【弃牌堆】。
    /// 递归安全：附属自身 AttachedCards 为空时停止；MoveToGraveyard 内部会再次经过本 detach 逻辑但无操作。
    /// 顺序要求：逐个按序送入，供后续 UI 动画逐个播放。
    /// </summary>
    private void DetachAttachmentsToGraveyard(CardInstance host)
    {
        if (host?.AttachedCards == null || host.AttachedCards.Count == 0) return;
        foreach (var att in host.AttachedCards)
        {
            att.AttachedTo = null;                       // 断开双向引用（宿主列表稍后 Clear）
            MoveToGraveyard(att, att.OwnerId);           // 经统一移动：离开场上→若 att 有附属会递归进墓地
        }
        host.AttachedCards.Clear();
    }

    /// <summary>
    /// 宿主（被封印的卡）离开场上时，将其封印的封印卡（按添加顺序）返回各自拥有者【手牌】。
    /// 规则（需求③）：被封印的卡从场上离开（入墓地/回手牌/回卡组/角色翻面）时，封印卡回 OWNER 手牌；
    /// 与附属（进墓地/回库底）去向不同，封印一律回 OWNER 手牌（无论 Normal 还是 AreaWon 模式，见需求④）。
    /// 递归安全：封印卡自身 SealedCards 通常为空（封印不嵌套）；若其带有附属，
    /// MoveToHand 内部会再次经过 RemoveFromAllZones → DetachAttachmentsToGraveyard 把附属送墓地（Q11 防御性）。
    /// </summary>
    private void DetachSealedCards(CardInstance host)
    {
        if (host?.SealedCards == null || host.SealedCards.Count == 0) return;
        foreach (var seal in host.SealedCards)
        {
            seal.SealedBy = null;                 // 断开双向引用（宿主列表稍后 Clear）
            seal.IsSeal = false;                  // 清除封印卡标记
            MoveToHand(seal, seal.OwnerId);        // 封印卡回 OWNER 手牌（不区分 AreaWon/Normal）
        }
        host.SealedCards.Clear();
    }

    /// <summary>将卡牌控制权转移到目标玩家（自由移动阶段只改 ControllerId，OwnerId 不变）。
    /// bumpVersion=false 时不刷新目标版本号（争夺区→争夺区移动的夺控换边场景，见 FireMoved 说明）。</summary>
    private void TransferOwnership(CardInstance card, int playerId, bool bumpVersion = true)
    {
        if (card.ControllerId == playerId) return;
        int oldController = card.ControllerId;
        int verBefore = card.VersionId;
        card.ControllerId = playerId;
        // 注（2026-08-11）：原 NotifyControllerChanged 目标总线调用已删除——控制者变化判丢失改由 D16 版本号方案覆盖，
        // 下方 FireMoved(card) 已触发 BumpVersion，结算时比对 TargetVersionId 不一致即判丢失，无需再发目标相关总线事件。
        // 注（2026-08-11·二次收口）：争夺区内的夺控换边【不刷版本号】——控制者变化对绝大多数卡的目标合法性无影响；
        // 要求"本方/敌方角色"的效果改由结算时 TargetValidity.CheckRestrictions 以入栈来源控制者快照复检（与"本地区"同构）。
        GD.Print($"[夺控LOG] TransferOwnership: {card.PlayerTemplate?.CardName}(UID={card.UID}) {oldController}→{playerId} 旧区={card.CurrentZone}，版本号 {verBefore}→{card.VersionId}(FireMoved后, bump={bumpVersion})");
        // 夺控影响持续光环（D19-③）：控制权变更后必须重算光环集合，否则
        // 旧控制者一侧的"对他者授予"不会被撤销，反复夺控会叠加攻击力。
        // 复用 FireMoved 统一出口：先 ReevaluatePrereqs（改写数据），再 OnPostMove→ReevaluateAuras(old)+ReevaluateAuras(new)，
        // 同时驱动 UI 刷新（含 MagicDomainsLabel / 战斗标签）。同区移动幂等，安全。
        CardZone zone = card.CurrentZone;
        FireMoved(card, zone, playerId, "夺控", bumpVersion);
        // 旧控制者侧也需重算，撤销其曾授予他者的光环效果
        if (oldController != playerId)
            EffectEngine.ReevaluateAuras(oldController);
    }

    /// <summary>
    /// 移动统一出口：UI 刷新 + 派生数据重算 + 目标版本号失效 + Moved 总线广播。
    /// bumpVersion=false 时【不刷新版本号】，用于"争夺区→争夺区"移动（含跨地区机动、含夺控换边）：
    /// 按规则该类移动不使以其为目标的效果丢失；离开争夺区（进堆叠/手牌/墓地等）仍照常刷新。
    /// </summary>
    private void FireMoved(CardInstance card, CardZone zone, int playerId, string detailId, bool bumpVersion = true)
    {
        OnCardMoved?.Invoke(card, zone, playerId, detailId);
        // D19-⑧：所有区域移动的"后置全局刷新"统一收口此处（MoveToArea/MoveToHand/MoveToAsset/
        // PM 命令的 MoveAreaToArea 等专用方法都经此出口，而 MoveCard 也走 MoveToX，故只需在此一处）。
        // 注意顺序：必须先 ReevaluatePrereqs（用新控制者/新忠诚重算并改写数据），再 RefreshFirstStrikeStats
        // （把最新数据刷到 UI）；反之 UI 会显示重算前的旧值，表现为"数据变了 UI 没刷"。
        Script.Manager.EffectEngine.ReevaluatePrereqs(playerId);
        TurnManager.Instance?.RefreshFirstStrikeStats();
        // 【MVC 收口】所有 move 路径（MoveToX / MoveCard / PM 命令专用移动）经此唯一出口，
        // 由 View 层订阅后统一刷新派生 UI（资产区领域忠诚、地区能力统计、分数、持续效果态），
        // 调用方无需再逐处手动调 SyncDerivedUI / RefreshStatsLabels。
        OnPostMove?.Invoke(card, zone, playerId, detailId);
        // 目标有效性版本号（D16 丢失目标新方案）：区域移动使目标状态版本失效，
        // 堆叠条目结算时比对入栈时记录的 VersionId 即可判丢失，无需逐属性广播。
        // 例外（2026-08-11）：争夺区→争夺区移动（含跨地区、含夺控换边）不刷新版本号，
        // 其"本地区/本方敌方"类限制改由结算时 TargetValidity.Check 按入栈快照复检。
        int fireMovedVerBefore = card.VersionId;
        if (bumpVersion)
        {
            card.BumpVersion();
            GD.Print($"🔢 [版本号] FireMoved BumpVersion: {card.PlayerTemplate?.CardName}(UID={card.UID}) 版本号 {fireMovedVerBefore}→{card.VersionId}（区域移动触发）");
        }
        else
        {
            GD.Print($"🔢 [版本号] 争夺区→争夺区移动，不刷新版本号（当前={card.VersionId}）：{card.PlayerTemplate?.CardName}(UID={card.UID})");
        }
        // 目标丢失总线：区域移动(进/出墓地、手牌、封印区、换宿主、离场)统一经此发 Moved，
        // 覆盖丢失规则 ①②④⑤⑧⑨ 的"离开原区域"。订阅方 TurnManager 扫描堆叠置目标丢失标记。
        CardManager.NotifyCardStateChanged(card, CardChangeKind.Moved);
    }

    /// <summary>
    /// 纯翻面出口（D16 丢失目标新方案）：翻面不改变区域，但会使目标状态版本失效。
    /// 正式玩法中翻面均伴随移动（现身→堆叠 / 派遣→争夺区 / 离场→手牌等）已自然经 FireMoved 收口；
    /// 本方法仅供"无区域移动的纯翻面"（如 GM 命令 covert）使用，复用 FireMoved 统一刷新出口，
    /// 由内部 BumpVersion 使版本号失效。zone/detailId 取卡当前值，确保 UI 派生刷新一致。
    /// </summary>
    public void FireCovert(CardInstance card)
    {
        if (card == null) return;
        FireMoved(card, card.CurrentZone, card.ControllerId, card.ZoneDetailId);
    }

    // ============================================================
    // 统一移动接口
    // ============================================================
    public void MoveCard(CardInstance card, CardZone targetZone, int playerId, string detailId = null)
    {
        if (card == null) return;
        switch (targetZone)
        {
            case CardZone.Hand: MoveToHand(card, playerId); break;
            case CardZone.Graveyard: MoveToGraveyard(card, playerId); break;
            case CardZone.AssetZone: MoveToAsset(card, playerId); break;
            case CardZone.AreaZone: MoveToArea(card, detailId ?? "", playerId); break;
            case CardZone.AttachedZone: MoveToAttachedZone(card, detailId ?? "", playerId); break;
            case CardZone.SealedZone: MoveToSealedZone(card, detailId ?? "", playerId); break;
            case CardZone.ScoreZone: MoveToScore(card, playerId); break;
            case CardZone.CouncilZone: MoveToCouncil(card, playerId); break;
            case CardZone.Deck: MoveToDeckBottom(card, playerId, false); break;
            case CardZone.Stack: MoveToStack(card, playerId); break;
            default:
                GD.PrintErr($"【ZoneManager】不支持的目标区域: {targetZone}");
                break;
        }
        // 注意：后置全局刷新（RefreshFirstStrikeStats + ReevaluatePrereqs）已统一收口到 FireMoved，
        // 所有 MoveToX 专用方法与 MoveCard 均经此出口，无需在此重复。
    }

    // ============================================================
    // 便捷方法
    // ============================================================
    /// <summary>进入堆叠区：暗藏者现身（快速能力）入栈时调用。
    /// 卡从原区（通常是争夺区）移入堆叠区，但不重置状态（卡已在入栈前翻面为正面）；
    /// 离开争夺区按连带模式处置附属/封印（与 Normal 离场一致，满足需求③/④）。
    /// 此移动使 CurrentZone 变为 Stack，结算时 MoveToArea 的 fromZone != AreaZone
    /// 自然触发 EnterPlay（“回地区触发进场触发是正常情况”）。</summary>
    public void MoveToStack(CardInstance card, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.Stack);
        card.CurrentZone = CardZone.Stack;
        card.ZoneDetailId = null;
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 进入堆叠区 (玩家{playerId})");
        FireMoved(card, CardZone.Stack, playerId, null);
    }

    /// <summary>进入手牌：转移归属 + 重置临时状态，清 ZoneDetailId</summary>
    public void MoveToHand(CardInstance card, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.Hand);
        TransferOwnership(card, playerId);
        card.ResetToBaseState();
        card.CurrentZone = CardZone.Hand;
        card.ZoneDetailId = null;
        Ensure(_hand, playerId).Add(card);
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 进入玩家{playerId}手牌");
        FireMoved(card, CardZone.Hand, playerId, null);
    }

    /// <summary>进入墓地：转移归属 + 重置临时状态</summary>
    public void MoveToGraveyard(CardInstance card, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.Graveyard);
        TransferOwnership(card, playerId);
        card.ResetToBaseState();
        card.CurrentZone = CardZone.Graveyard;
        card.ZoneDetailId = null;
        Ensure(_graveyard, playerId).Add(card);
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 进入墓地");
        FireMoved(card, CardZone.Graveyard, playerId, null);
    }

    /// <summary>进入资产区：转移归属；保留颜色/领域信息，不重置状态</summary>
    public void MoveToAsset(CardInstance card, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.AssetZone);
        TransferOwnership(card, playerId);
        // 资产区保留状态，不调用 ResetToBaseState
        card.CurrentZone = CardZone.AssetZone;
        card.ZoneDetailId = null;
        Ensure(_asset, playerId).Add(card);
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 进入玩家{playerId}资产区");
        FireMoved(card, CardZone.AssetZone, playerId, null);
    }

    /// <summary>进入争夺区：detailId 为地区UID；保留状态(进入场上保留打出时状态)</summary>
    public void MoveToArea(CardInstance card, string areaDetailId, int playerId)
    {
        if (card == null) return;
        // 进场判定依据：进入场地区前的来源区（此时 card.CurrentZone 仍为旧区，尚未被下方赋值覆盖）。
        // 仅当"非场地区 → 争夺区"时才视为进场（触发 EnterPlay）；
        // 争夺区→争夺区移动（机动/夺控转移）fromZone 也是 AreaZone，不触发——符合规则（D37）。
        CardZone fromZone = card.CurrentZone;
        // 目标版本号策略（2026-08-11）：争夺区→争夺区的移动【不刷新版本号】，
        // 含跨地区机动与夺控换边；以其为目标的效果不因此丢失（"本地区"限制另由结算快照复检）。
        // 注意必须同时透传给 TransferOwnership，否则其内部 FireMoved 会抢先 Bump 一次。
        bool bumpVersion = fromZone != CardZone.AreaZone;
        RemoveFromAllZones(card, CardZone.AreaZone);
        TransferOwnership(card, playerId, bumpVersion);
        card.CurrentZone = CardZone.AreaZone;
        card.ZoneDetailId = areaDetailId;
        // BUG3 修复：角色移动时同步其结附的附属（AttachedCards）的 ZoneDetailId（仅改地区，不改 control）。
        // 附属在 AttachedZone，ZoneDetailId 记宿主地区；宿主移动后若不跟随，附属"本地区(IsLocalZone)"基准
        // 与 UI 位置会停留在旧地区。夺控（TransferOwnership）只改控制者，不改附庸 control，故此处只同步地区。
        foreach (var att in card.AttachedCards)
        {
            if (att == null) continue;
            att.ZoneDetailId = areaDetailId;
        }
        _area.Add(card);
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName}(UID={card.UID}) 进入地区 {areaDetailId} (玩家{playerId})，当前版本号={card.VersionId}");
        // [DIAG 阿格斯IV] 追踪阿格斯进入争夺区（AreaZone）的实例，排查多实例问题
        if (card.PlayerTemplate?.CardName == "阿格斯IV")
            GD.Print($"[DIAG 阿格斯IV] 阿格斯进入AreaZone: UID={card.UID} ctrl={card.ControllerId} owner={card.OwnerId} fromZone={fromZone} 宿主AttachedTo={(card.AttachedTo?.UID ?? "∅")}");
        FireMoved(card, CardZone.AreaZone, playerId, areaDetailId, bumpVersion);
        if (fromZone != CardZone.AreaZone)
        {
            // 规范化进场触发封装点（D39）：手牌/封印区/墓地/资产区 → 争夺区 均经 TriggerEmitPoints 统一触发 EnterPlay。
            // 注释(后续)：JC012 等涉及"卡进入场地区"的时机可直接复用此路径。
            Script.Manager.TriggerEmitPoints.EnterPlay(card, playerId, areaDetailId, card.AttachedTo);
            // D19 统一注册：通过 MOVE 命令（非结算路径）进入争夺区的卡，同样需要注册持续/静止效果
            // （如 JC001 通灵猫头鹰的"不能被装备结附"与"白眼=先手调查"）。与 TurnManager.ResolveEntry
            // 的 PlayToArea 分支保持一致，避免"MOVE 进场不生效"的漏注册。
            GD.Print($"[JC001DEBUG] MOVE 进场注册持久效果: {card.PlayerTemplate?.CardName} (UID={card.UID}, 效果数={card.PlayerTemplate?.EffectDataList.Count ?? 0})");
            Script.Manager.EffectEngine.RegisterPersistent(card);
        }
    }

    /// <summary>进入附属子区域：不进 _area 桶，挂在宿主实例(AttachedTo)上，ZoneDetailId 记录所属地区。
    /// 与 MoveToArea 区分：附属卡不再被战力统计/伤害分配误当作普通地区卡（除非显式纳入）。
    /// 双向引用 AttachedTo 由调用方（ExecuteAttach）先建立。</summary>
    public void MoveToAttachedZone(CardInstance card, string areaDetailId, int playerId)
    {
        if (card == null) return;
        CardZone fromZone = card.CurrentZone;
        RemoveFromAllZones(card, CardZone.AttachedZone);
        TransferOwnership(card, playerId);
        card.CurrentZone = CardZone.AttachedZone;
        card.ZoneDetailId = areaDetailId;       // 记录所属地区（用于 UI 挂地区容器/统计）
        _attached.Add(card);
        // JC068 阿格斯IV 结附于秘社（AttachedZone）后需注册其持续效果（未横置→封锁对手派遣）。
        // 对无持续/静止效果的普通附属是无副作用空操作；防重复由 RegisterPersistent 内部去重。
        if (fromZone != CardZone.AttachedZone)
        {
            GD.Print($"[JC001DEBUG] 附属结附注册持久效果: {card.PlayerTemplate?.CardName} (UID={card.UID})");
            Script.Manager.EffectEngine.RegisterPersistent(card);
        }
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 进入附属子区域(地区 {areaDetailId})");
        // [DIAG 阿格斯IV] 追踪阿格斯进入附属区（AttachedZone）的实例，排查多实例问题
        if (card.PlayerTemplate?.CardName == "阿格斯IV")
            GD.Print($"[DIAG 阿格斯IV] 阿格斯进入AttachedZone: UID={card.UID} ctrl={card.ControllerId} owner={card.OwnerId} fromZone={fromZone} 宿主AttachedTo={(card.AttachedTo?.UID ?? "∅")}");
        FireMoved(card, CardZone.AttachedZone, playerId, areaDetailId);
    }

    /// <summary>进入封印子区域：不进 _area 桶，挂在宿主实例(SealedBy)或地区(SealedBy==null)上。
    /// 封印卡不参与对抗（战力/伤害），仅由 UI 偏移渲染、离场回 OWNER 手牌。
    /// 双向引用 SealedBy 由调用方（ExecuteSeal）先建立；ZoneDetailId 记录封印于哪个地区。</summary>
    public void MoveToSealedZone(CardInstance card, string areaDetailId, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.SealedZone);
        TransferOwnership(card, playerId);
        card.CurrentZone = CardZone.SealedZone;
        card.ZoneDetailId = areaDetailId;
        _sealed.Add(card);
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 进入封印子区域(地区 {areaDetailId})");
        FireMoved(card, CardZone.SealedZone, playerId, areaDetailId);
    }

    public void MoveToScore(CardInstance card, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.ScoreZone);
        TransferOwnership(card, playerId);
        card.ResetToBaseState();
        card.CurrentZone = CardZone.ScoreZone;
        card.ZoneDetailId = null;
        Ensure(_score, playerId).Add(card);
        FireMoved(card, CardZone.ScoreZone, playerId, null);
    }

    public void MoveToCouncil(CardInstance card, int playerId)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.CouncilZone);
        TransferOwnership(card, playerId);
        card.ResetToBaseState();
        card.CurrentZone = CardZone.CouncilZone;
        card.ZoneDetailId = null;
        Ensure(_council, playerId).Add(card);
        FireMoved(card, CardZone.CouncilZone, playerId, null);
    }

    public void MoveToDeckTop(CardInstance card, int playerId, bool toTop = true)
        => MoveToDeck(card, playerId, toTop);
    public void MoveToDeckBottom(CardInstance card, int playerId, bool toTop = false)
        => MoveToDeck(card, playerId, toTop);

    /// <summary>返回牌库：从其他区域列表移除(根治手牌列表残留) + 转移归属 + 重置临时状态 + 委托 DeckManager 回牌库</summary>
    private void MoveToDeck(CardInstance card, int playerId, bool toTop)
    {
        if (card == null) return;
        RemoveFromAllZones(card, CardZone.Deck);   // 若来自牌库，同步从数据层牌库移除
        TransferOwnership(card, playerId);
        card.ResetToBaseState();
        card.CurrentZone = CardZone.Deck;
        card.ZoneDetailId = null;
        if (playerId == 1) _deckManager.ReturnToDeckPlayer1(card, toTop);
        else _deckManager.ReturnToDeckPlayer2(card, toTop);
        GD.Print($"[ZoneManager] {card.PlayerTemplate?.CardName} 返回玩家{playerId}牌库");
        FireMoved(card, CardZone.Deck, playerId, null);
    }

    // ============================================================
    // 查询方法
    // ============================================================
    public List<CardInstance> GetZoneCards(CardZone zone, int playerId, string detailId = null)
    {
        switch (zone)
        {
            case CardZone.Hand: return Filter(_hand, playerId);
            case CardZone.Graveyard: return Filter(_graveyard, playerId);
            case CardZone.AssetZone: return Filter(_asset, playerId);
            case CardZone.ScoreZone: return Filter(_score, playerId);
            case CardZone.CouncilZone: return Filter(_council, playerId);
            case CardZone.AreaZone:
            {
                var result = new List<CardInstance>(_area);
                if (!string.IsNullOrEmpty(detailId)) result.RemoveAll(c => c.ZoneDetailId != detailId);
                if (playerId != 0) result.RemoveAll(c => c.ControllerId != playerId);
                return result;
            }
            case CardZone.AttachedZone:
            {
                var result = new List<CardInstance>(_attached);
                if (!string.IsNullOrEmpty(detailId)) result.RemoveAll(c => c.ZoneDetailId != detailId);
                if (playerId != 0) result.RemoveAll(c => c.ControllerId != playerId);
                return result;
            }
            case CardZone.SealedZone:
            {
                var result = new List<CardInstance>(_sealed);
                if (!string.IsNullOrEmpty(detailId)) result.RemoveAll(c => c.ZoneDetailId != detailId);
                if (playerId != 0) result.RemoveAll(c => c.ControllerId != playerId);
                return result;
            }
            default: return new List<CardInstance>();
        }
    }

    public List<CardInstance> GetAreaCardsBySlot(string slotUid)
        => GetZoneCards(CardZone.AreaZone, 0, slotUid);

    /// <summary>
    /// 返回某地区的"对抗统计全集"：地区内所有角色/暗藏者 + 附属于本地区内角色的附属
    /// + 附属于本地区的地区附属（AttachedTo==null 且 ZoneDetailId==地区）。不含封印卡（封印不参与对抗）。
    /// 供 AreaStatsCalculator / CombatManager 计算战力与伤害分配，确保封印/附属改造后附属属性仍计入地区。
    /// </summary>
    public List<CardInstance> GetAllCardsInAreaIncludingAttached(string slotUid)
    {
        var result = new List<CardInstance>(GetZoneCards(CardZone.AreaZone, 0, slotUid));
        // 先收集角色附件，再统一加入 result，避免在 foreach 中修改 result 触发枚举异常
        var attachments = new List<CardInstance>();
        foreach (var role in result)
        {
            if (role.AttachedCards != null)
                foreach (var att in role.AttachedCards)
                    if (att.CurrentZone == CardZone.AttachedZone) attachments.Add(att);
        }
        result.AddRange(attachments);
        // 附属于地区的附属（AttachedTo==null，挂地区容器）
        foreach (var att in _attached)
            if (att.AttachedTo == null && att.ZoneDetailId == slotUid && !result.Contains(att))
                result.Add(att);
        return result;
    }

    /// <summary>获取当前所有存在的争夺区（地区）UID 列表（去重，按出现顺序）</summary>
    public List<string> GetAllAreaSlotUids()
    {
        var result = new List<string>();
        foreach (var c in _area)
        {
            if (!string.IsNullOrEmpty(c.ZoneDetailId) && !result.Contains(c.ZoneDetailId))
                result.Add(c.ZoneDetailId);
        }
        return result;
    }

    public bool IsCardInZone(CardInstance card, CardZone zone, int playerId = 0)
    {
        if (card == null) return false;
        if (card.CurrentZone != zone) return false;
        if (playerId != 0 && card.ControllerId != playerId) return false;
        return true;
    }

    /// <summary>清空某玩家手牌列表（供 Arena.ClearHand 使用）</summary>
    public void ClearHand(int playerId)
    {
        if (_hand.TryGetValue(playerId, out var list)) list.Clear();
    }

    private List<CardInstance> Filter(Dictionary<int, List<CardInstance>> dict, int playerId)
    {
        var result = new List<CardInstance>();
        if (playerId == 0)
        {
            foreach (var list in dict.Values) result.AddRange(list);
        }
        else if (dict.TryGetValue(playerId, out var list))
        {
            result.AddRange(list);
        }
        return result;
    }

    // ============================================================
    // 文档"思考"问题的支撑能力
    // ============================================================
    /// <summary>把指定 UID 的卡（如来自牌库）送入墓地。返回是否成功。</summary>
    public bool SendCardToGraveyardByUID(string uid)
    {
        var card = _cardManager?.GetInstance(uid);
        if (card == null) { GD.PrintErr($"【ZoneManager】未找到卡牌: {uid}"); return false; }
        MoveToGraveyard(card, card.OwnerId);
        return true;
    }

    /// <summary>把指定 UID 的卡加入指定玩家手牌。</summary>
    public bool SendCardToHandByUID(string uid, int playerId)
    {
        var card = _cardManager?.GetInstance(uid);
        if (card == null) { GD.PrintErr($"【ZoneManager】未找到卡牌: {uid}"); return false; }
        MoveToHand(card, playerId);
        return true;
    }

    /// <summary>争夺区之间移动（如地区1 -> 地区2）。</summary>
    public bool MoveAreaToArea(CardInstance card, string toAreaDetailId, int playerId)
    {
        if (card == null || card.CurrentZone != CardZone.AreaZone) return false;
        MoveToArea(card, toAreaDetailId, playerId);
        return true;
    }

    /// <summary>争夺区 -> 资产区。</summary>
    public bool MoveAreaToAsset(CardInstance card, int playerId)
    {
        if (card == null || card.CurrentZone != CardZone.AreaZone) return false;
        MoveToAsset(card, playerId);
        return true;
    }

    /// <summary>资产区 -> 争夺区。</summary>
    public bool MoveAssetToArea(CardInstance card, string toAreaDetailId, int playerId)
    {
        if (card == null || card.CurrentZone != CardZone.AssetZone) return false;
        MoveToArea(card, toAreaDetailId, playerId);
        return true;
    }

    // ============================================================
    // 调查对抗专用（封装牌库取/放，确保 CurrentZone 只由本类修改）
    // ============================================================
    /// <summary>
    /// 调查对抗：从胜者牌库顶临时取出 count 张（数据层脱离牌库列表），
    /// 状态保持 CardZone.Deck（临时态，不进入任何区域列表），供窗口展示与分配。
    /// </summary>
    public List<CardInstance> TakeTopForInvestigation(int playerId, int count)
    {
        if (playerId == 1) return _deckManager.TakeTopPlayer1(count);
        return _deckManager.TakeTopPlayer2(count);
    }

    /// <summary>
    /// 调查对抗确认：把分配好的卡放回牌库顶/底（数据层），并重新编排胜者牌库的完整有序序列。
    /// 牌库顺序的真相（谁在顶/底、精确顺序）自此唯一由本方法决定，View 不再自行计算。
    /// 完成后触发 OnDeckReordered 供 UI 按序纯重绘（牌库 UI 重建由该事件专属驱动）。
    /// </summary>
    public void ReturnAfterInvestigation(int playerId, List<CardInstance> toTop, List<CardInstance> toBottom)
    {
        // 1) 先取回目前牌库里仍留在中段/底部、未参与本次调查的卡（剩余牌库）
        //    注意：GetDeckPlayerX 返回的是内部列表引用，务必拷贝后再拼装，避免别名污染。
        var remaining = playerId == 1 ? _deckManager.GetDeckPlayer1() : _deckManager.GetDeckPlayer2();

        // 2) 组装最终有序序列。牌库约定：索引 0 = 牌库顶，末尾 = 牌库底。
        //    因此顺序必须是：[放回顶部的 toTop] + [未参与调查的剩余牌] + [放回底部的 toBottom]
        //    （toTop 必须排在 remaining 之前，否则会被压到剩余牌库下方，表现为"放顶部也回到了牌底"）
        var ordered = new List<CardInstance>();
        if (toTop != null) ordered.AddRange(toTop);
        if (remaining != null) ordered.AddRange(remaining);
        if (toBottom != null) ordered.AddRange(toBottom);

        // 3) 用新序列整体覆盖数据层牌库（顶层写入，保证 source of truth 唯一）
        if (playerId == 1)
            _deckManager.SetDeckPlayer1(ordered);
        else
            _deckManager.SetDeckPlayer2(ordered);

        // 4) 通知 UI：牌库顺序已重组，View 仅按 ordered 重建 DeckZone 的 CardUI 顺序。
        // 注意：此处【不】调用 FireMoved(Deck)——牌库 UI 由 OnDeckReordered 专属重建，
        // 若再触发 OnCardMoved 会让 SyncCardUIToZone 把卡 ReturnCardUI 回 DeckZone，与 RebuildFromOrdered 重复，
        // 导致 DeckZone 卡列表出现重复/错位。
        OnDeckReordered?.Invoke(playerId, ordered);
    }
}
