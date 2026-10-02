using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·双端一致性回归】服务端权威 vs 真跨进程客户端投影：同一份规则函数在两端的结论必须相等。
    ///
    /// 背景（本组测试锁定的一类缺陷）：客户端原先自己提前判断（IsLegalPlayTarget / CanActivate /
    /// DeployCovertEligibility …），这些函数本身与服务端同源，但依赖的**输入**在客户端缺失时会静默降级
    /// （本局 PlayerManager 未注入、静止标记/运行时授予不入快照、全局封锁态不下发），于是两端得出不同结论：
    ///   · JC005 裂解术：有【心】却被判成"无【心】"，结附秘社/结附地区的附属选不出来；
    ///   · JC020 装备结附：客户端允许结附 JC001/JC030（服务端因 CannotBeAttached 拒绝）；
    ///   · JC068 阿格斯IV：客户端允许拖起手牌秘密派遣（服务端封锁拒绝）；
    ///   · JC001 白眼：客户端地区统计少算"先手调查+1"。
    ///
    /// 断言方式：**先用生产装配路径**（GameInitializer 服务端 + ClientMatchView 客户端投影 +
    /// BuildSnapshotFor 下行），再逐项比对"服务端结论 / 客户端本地结论 / 服务端下发的合法性投影"三者相等，
    /// 并对已知偏差点位加显式期望值（这样即使某天有人把 P0/P1 的注入删掉，测试也会红）。
    /// </summary>
    public partial class ClientServerParityTests : TestRunner
    {
        private const string P_Jc001 = "res://Cards/Data/PlayerCards/JC/JC001_tonglingmaotouying.tres"; // 领域=心(0) / 不能被装备结附 / 白眼前提
        private const string P_Jc005 = "res://Cards/Data/PlayerCards/JC/JC005_liejieshu.tres";        // 事务：条件式目标限制
        private const string P_Jc012 = "res://Cards/Data/PlayerCards/JC/JC012_weimushouwanghuiji.tres"; // 附属（结附秘社）
        private const string P_Jc020 = "res://Cards/Data/PlayerCards/JC/JC020_yinbidefeidao.tres";      // [装备,武器] 附属
        private const string P_Jc068 = "res://Cards/Data/PlayerCards/JC/JC068_agesiIV.tres";           // 全局封锁秘密派遣
        private const string P_Jc081 = "res://Cards/Data/PlayerCards/JC/JC081_yaluoer.tres";           // [前缀] 除装备与祝福外不受附属（子类别白名单）
        private const string P_Jc089 = "res://Cards/Data/PlayerCards/JC/JC089_duxuezuzhou.tres";       // [诅咒] 附属（结附角色，白名单外）
        private const string P_Council = "res://Cards/Data/CouncilCards/JC/MSJC001_weimushouwang.tres";

        private GameInitializer _server;
        private ClientMatchView _client;
        private int _viewerSeat = 1;

        // ==================== 装配辅助 ====================

        private static PlayerCardData LoadCard(string path)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var t = ResourceLoader.Load<PlayerCardData>(path);
            if (t != null) CardEffectFactory.EnsureEffects(t);
            return t;
        }

        /// <summary>服务端权威装配（生产组合根）+ 客户端投影装配（真跨进程：独立 NetClientCardManager 从模板重建）。</summary>
        private void SetupMatch(int mindAssets, int viewerSeat = 1)
        {
            _viewerSeat = viewerSeat;
            _server = new GameInitializer { DebugSeed = 42 };
            _server.InitializeAll();
            _server.StartMatch();
            _server.PlayerActionService.SetupInitialAreas(1);

            // 资产区放 N 张 JC001（MagicDomains=[Mind]）→ 提供【心】忠诚 N（同时可满足 JC001-B 白眼前提≥2）
            var jc001 = LoadCard(P_Jc001);
            for (int i = 0; i < mindAssets; i++)
                _server.ZoneManager.MoveToAsset(_server.CardManager.CreateCardInstance(jc001, 1), 1);

            // 客户端投影（含 ClientMatchView.Assemble：注入本局 PlayerManagerRef / 建投影 Manager）
            _client = new ClientMatchView(new NetClientCardManager(), viewerSeat);
            _client.ApplySnapshot(_server.BuildSnapshotFor(viewerSeat));
        }

        private CardInstance ServerCard(string templatePath, CardZone zone, string detailId = null, int ownerId = 1)
        {
            var inst = _server.CardManager.CreateCardInstance(LoadCard(templatePath), ownerId);
            switch (zone)
            {
                case CardZone.Hand: _server.ZoneManager.MoveToHand(inst, ownerId); break;
                case CardZone.AssetZone: _server.ZoneManager.MoveToAsset(inst, ownerId); break;
                case CardZone.AreaZone: _server.ZoneManager.MoveToArea(inst, detailId, ownerId); break;
                case CardZone.AttachedZone: _server.ZoneManager.MoveToAttachedZone(inst, detailId, ownerId); break;
                default: break;
            }
            return inst;
        }

        private CardInstance ServerCouncil()
        {
            var existing = _server.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1);
            if (existing != null && existing.Count > 0) return existing[0];
            var tmpl = ResourceLoader.Load<CouncilCardData>(P_Council);
            var inst = _server.CardManager.CreateCardInstance(tmpl, 1);
            _server.ZoneManager.MoveToCouncil(inst, 1);
            return inst;
        }

        /// <summary>结附于秘社的附属（JC005 的关键靶标：宿主 PlayerTemplate 为 null）。</summary>
        private CardInstance ServerAttachmentOnCouncil(string templatePath)
        {
            var council = ServerCouncil();
            var att = _server.CardManager.CreateCardInstance(LoadCard(templatePath), 1);
            att.IsAttached = true;
            att.AttachedTo = council;
            council.AttachedCards.Add(att);
            _server.ZoneManager.MoveToAttachedZone(att, "parity_area", 1);
            return att;
        }

        /// <summary>结附于"某张场上卡"的附属（JC005 的对照靶标：宿主为玩家卡）。</summary>
        private CardInstance ServerAttachmentOn(CardInstance host, string templatePath)
        {
            var att = _server.CardManager.CreateCardInstance(LoadCard(templatePath), 1);
            att.IsAttached = true;
            att.AttachedTo = host;
            host?.AttachedCards.Add(att);
            _server.ZoneManager.MoveToAttachedZone(att, "parity_area", 1);
            return att;
        }

        /// <summary>同步刷新客户端投影到服务端当前状态。</summary>
        private void SyncClient() => _client.ApplySnapshot(_server.BuildSnapshotFor(_viewerSeat));

        private CardInstance ClientCard(CardInstance serverInst)
            => serverInst == null ? null : _client.ClientCardManager.GetInstance(serverInst.UID);

        private GameViewSnap View => _client.LastSnapshot?.View;

        /// <summary>服务端权威判定（生产函数 + 本局 Manager）。</summary>
        private bool ServerLegalPlay(CardInstance hand, CardInstance target)
            => _server.CardManager.IsLegalPlayTarget(hand, target);

        /// <summary>客户端本地判定（客户端投影 Manager + 已注入的本局投影 PlayerManager）。</summary>
        private bool ClientLegalPlay(CardInstance clientHand, CardInstance clientTarget)
            => _client.ClientCardManager.IsLegalPlayTarget(clientHand, clientTarget);

        private bool ProjectionHasPlayTarget(CardInstance clientHand, CardInstance clientTarget)
        {
            var entry = View?.PlayTargets?.Find(x => x != null && x.SourceUid == clientHand.UID);
            return entry != null && entry.TargetUids.Contains(clientTarget.UID);
        }

        // ==================== 用例 ====================

        /// <summary>P0/P1/P2 核心回归：JC005 条件式目标限制（需读本局 PlayerManager 的【心】）两端同解。</summary>
        public void TestParity_JC005_条件式限制两端同解()
        {
            SetupMatch(mindAssets: 1);                         // 有【心】
            var hand = ServerCard(P_Jc005, CardZone.Hand);
            var onCouncil = ServerAttachmentOnCouncil(P_Jc012);
            var onChar = ServerAttachmentOn(ServerCard(P_Jc001, CardZone.AreaZone, "parity_area"), P_Jc012);
            SyncClient();

            var cHand = ClientCard(hand);
            var cOnCouncil = ClientCard(onCouncil);
            var cOnChar = ClientCard(onChar);
            Assert.IsNotNull(cHand, "客户端应重建手牌 JC005");
            Assert.IsNotNull(cOnCouncil, "客户端应重建结附秘社的附属");

            int mind = _client.ClientPlayerManager.GetDomainLoyalty(1).GetValueOrDefault(MagicDomain.Mind);
            Assert.IsTrue(mind >= 1, $"客户端投影应知道自己有【心】（实际 {mind}）");

            Assert.IsTrue(ServerLegalPlay(hand, onCouncil), "服务端：有【心】→ 结附秘社的附属合法");
            Assert.IsTrue(ClientLegalPlay(cHand, cOnCouncil),
                "客户端：有【心】→ 结附秘社的附属也必须合法（P0 注入本局 PlayerManager 的意义）");
            Assert.IsTrue(ServerLegalPlay(hand, onChar) && ClientLegalPlay(cHand, cOnChar), "结附角色的附属两端都应合法");
            Assert.IsTrue(ProjectionHasPlayTarget(cHand, cOnCouncil), "服务端投影应包含结附秘社的附属");
        }

        /// <summary>同一条件式限制在"无【心】"时要一致地收紧（两端都不能选结附秘社/结附地区的附属）。</summary>
        public void TestParity_JC005_无心时两端同样收紧()
        {
            SetupMatch(mindAssets: 0);                         // 无【心】
            var hand = ServerCard(P_Jc005, CardZone.Hand);
            var onCouncil = ServerAttachmentOnCouncil(P_Jc012);
            var onArea = ServerCard(P_Jc012, CardZone.AttachedZone, "parity_area");  // AttachedTo==null：结附地区的附属
            var onChar = ServerAttachmentOn(ServerCard(P_Jc001, CardZone.AreaZone, "parity_area"), P_Jc012);
            SyncClient();

            var cHand = ClientCard(hand);
            Assert.IsFalse(ServerLegalPlay(hand, onCouncil), "服务端：无【心】→ 结附秘社非法");
            Assert.IsFalse(ClientLegalPlay(cHand, ClientCard(onCouncil)), "客户端：无【心】→ 结附秘社同样非法");
            Assert.IsFalse(ServerLegalPlay(hand, onArea), "服务端：无【心】→ 结附地区非法");
            Assert.IsFalse(ClientLegalPlay(cHand, ClientCard(onArea)), "客户端：无【心】→ 结附地区同样非法");
            Assert.IsTrue(ServerLegalPlay(hand, onChar) && ClientLegalPlay(cHand, ClientCard(onChar)),
                "无【心】时结附角色的附属两端都应合法");
            Assert.IsFalse(ProjectionHasPlayTarget(cHand, ClientCard(onCouncil)), "投影不应包含非法的结附秘社附属");
        }

        /// <summary>P1 回归：静止标记（JC001-A/JC030-B「不能被装备结附」）两端同解。</summary>
        public void TestParity_装备结附_静止标记两端同解()
        {
            SetupMatch(mindAssets: 0);
            var jc001 = ServerCard(P_Jc001, CardZone.AreaZone, "parity_area");   // 争夺区 → 注册 CannotBeAttached
            var jc020 = ServerCard(P_Jc020, CardZone.Hand);                      // [装备] 附属
            SyncClient();

            Assert.IsTrue(jc001.HasStaticFlag(StaticKind.CannotBeAttached),
                "服务端 JC001 在争夺区应带 CannotBeAttached 静止标记（前置）");
            var c001 = ClientCard(jc001);
            var c020 = ClientCard(jc020);
            Assert.IsTrue(c001.HasStaticFlag(StaticKind.CannotBeAttached),
                "客户端投影也应带该静止标记（P1：StaticFlags 随快照下发）");

            Assert.IsFalse(ServerLegalPlay(jc020, jc001), "服务端应拒绝 JC020 结附 JC001");
            Assert.IsFalse(ClientLegalPlay(c020, c001), "客户端也必须拒绝（P0 去掉 EE 判空静默跳过 + P1 投影标记）");
            Assert.IsFalse(ProjectionHasPlayTarget(c020, c001), "服务端投影不应把 JC001 列为合法结附目标");
        }

        /// <summary>P1 回归：JC068-B「未横置封锁对手秘密派遣」两端同解。
        /// 注意受封锁的是"阿格斯IV控制者的对手"，故本用例以**被封锁方（座位2）**为观察端：
        /// 服务端门禁必须拒绝，客户端同一函数 + 快照投影封锁态也必须拒绝。</summary>
        public void TestParity_封锁秘密派遣两端同解()
        {
            SetupMatch(mindAssets: 0, viewerSeat: 2);
            ServerAttachmentOnCouncil(P_Jc068);                   // 玩家1 的阿格斯IV，未横置 → 封锁玩家2
            var p2Card = ServerCard(P_Jc001, CardZone.Hand, null, ownerId: 2);
            SyncClient();

            Assert.IsFalse(_server.TurnManager.CanPlayerDeployCovert(2),
                "服务端：未横置阿格斯IV → 玩家2 被封锁秘密派遣（前置）");
            Assert.IsTrue(_client.ClientPlayerManager.IsDeployCovertBlockedProjected(2),
                "客户端投影应收到封锁态（P1：PlayerSnapshot.DeployCovertBlocked）");

            var cP2Card = ClientCard(p2Card);
            Assert.IsNotNull(cP2Card, "客户端应重建玩家2 的手牌");
            // 服务端门禁（TurnManager.CanDeployCovert）与客户端门禁（DeployCovertEligibility + 投影封锁态）必须同解
            bool serverOk = _server.TurnManager.CanDeployCovert(p2Card);
            bool clientOk = DeployCovertEligibility.IsEligible(cP2Card,
                _client.ClientPlayerManager.IsDeployCovertBlockedProjected(2));
            Assert.IsFalse(serverOk, "服务端 CanDeployCovert 应因封锁而不通过");
            Assert.IsFalse(clientOk, "客户端同一函数 + 投影封锁态也必须不通过");
        }

        /// <summary>P1 回归：JC001-B 白眼（先手调查+1）在共用 AreaStatsCalculator 下两端同值。</summary>
        public void TestParity_白眼先手调查两端同值()
        {
            SetupMatch(mindAssets: 2);                            // 心≥2 → 白眼前提满足
            var charCard = ServerCard(P_Jc001, CardZone.AreaZone, "parity_area");
            _server.PlayerManager.SetFirstPlayer(1);
            SyncClient();
            _client.ClientPlayerManager.SetFirstPlayer(1);

            var cCard = ClientCard(charCard);
            int serverVal = AreaStatsCalculator.ValueOf(charCard, CombatManager.CombatType.Investigation, _server.PlayerManager);
            int clientVal = AreaStatsCalculator.ValueOf(cCard, CombatManager.CombatType.Investigation, _client.ClientPlayerManager);
            Assert.AreEqual(1, serverVal, "服务端：先手玩家 JC001 应得白眼（先手调查+1）");
            Assert.AreEqual(serverVal, clientVal,
                "客户端跑同一份 AreaStatsCalculator 必须同值（P1：授予属性 + 先手值随快照下发）");
        }

        /// <summary>全矩阵一致性：查看者手牌 × 候选目标，服务端 / 客户端本地 / 服务端投影三者逐一相等。
        /// 这是"任何新限制/新状态都不再引入两套判定"的总闸门。</summary>
        public void TestParity_打出目标全矩阵一致()
        {
            SetupMatch(mindAssets: 1);
            var jc005 = ServerCard(P_Jc005, CardZone.Hand);
            var jc020 = ServerCard(P_Jc020, CardZone.Hand);
            var jc012 = ServerCard(P_Jc012, CardZone.Hand);
            ServerAttachmentOnCouncil(P_Jc012);
            ServerCard(P_Jc001, CardZone.AreaZone, "parity_area");
            ServerCard(P_Jc001, CardZone.AttachedZone, "parity_area");
            SyncClient();

            // 候选池：争夺区 + 附属区 + 秘社（与服务端投影的候选范围一致）
            var candidates = new List<CardInstance>();
            candidates.AddRange(_server.ZoneManager.GetZoneCards(CardZone.AreaZone, 0));
            candidates.AddRange(_server.ZoneManager.GetZoneCards(CardZone.AttachedZone, 0));
            candidates.AddRange(_server.ZoneManager.GetZoneCards(CardZone.CouncilZone, 0));

            int compared = 0;
            foreach (var hand in new[] { jc005, jc020, jc012 })
            {
                var cHand = ClientCard(hand);
                Assert.IsNotNull(cHand, $"客户端应重建手牌 {hand.UID}");
                foreach (var t in candidates)
                {
                    if (t == null || t == hand) continue;
                    bool server = ServerLegalPlay(hand, t);
                    bool client = ClientLegalPlay(cHand, ClientCard(t));
                    bool proj = ProjectionHasPlayTarget(cHand, ClientCard(t));
                    Assert.AreEqual(server, client,
                        $"客户端本地判定与服务端不一致（来源 {hand.PlayerTemplate?.CardName} → 目标 {t.PlayerTemplate?.CardName ?? t.CouncilTemplate?.CardName}）");
                    Assert.AreEqual(server, proj,
                        $"服务端下发的合法性投影与服务端判定不一致（来源 {hand.PlayerTemplate?.CardName} → 目标 {t.PlayerTemplate?.CardName ?? t.CouncilTemplate?.CardName}）");
                    compared++;
                }
            }
            Assert.IsTrue(compared > 0, "应至少比较一组手牌×目标");
        }

        /// <summary>全矩阵一致性：可发动集合 + 激活合法目标集合，服务端 / 客户端 / 投影三者相等。</summary>
        public void TestParity_激活可发动与目标集合一致()
        {
            SetupMatch(mindAssets: 1);
            var jc012 = ServerAttachmentOnCouncil(P_Jc012);       // 两个快速激活效果（减费 / 横置目标角色）
            var jc020 = ServerAttachmentOn(ServerCard(P_Jc001, CardZone.AreaZone, "parity_area"), P_Jc020);
            _server.TurnManager.PriorityPlayerId = 1;              // 给查看者优先权（激活门禁要求）
            SyncClient();

            var fieldCards = new List<CardInstance>();
            fieldCards.AddRange(_server.ZoneManager.GetZoneCards(CardZone.AreaZone, 0));
            fieldCards.AddRange(_server.ZoneManager.GetZoneCards(CardZone.AttachedZone, 0));

            var candidates = new List<CardInstance>(fieldCards);
            candidates.AddRange(_server.ZoneManager.GetZoneCards(CardZone.CouncilZone, 0));

            // 客户端投影的优先权取自快照；此处服务端与投影同源，直接断言集合关系
            foreach (var card in fieldCards)
            {
                if (card == null) continue;
                bool serverActivatable = ActivateEligibility.CanActivate(card, _server.TurnManager,
                    _server.PlayerManager, _server.CardManager, ignoreAtomicLock: true);
                bool inProjection = View?.ActivatableUids?.Contains(card.UID) == true;
                Assert.AreEqual(serverActivatable, inProjection,
                    $"{card.PlayerTemplate?.CardName} 的『可发动』结论应与投影一致");

                foreach (var e in ServerActivationEffects(card))
                {
                    if (e?.Target == null || !e.Target.NeedsSelection) continue;
                    var proj = View?.AbilityTargets?.Find(x => x != null && x.SourceUid == card.UID && x.EffectId == e.EffectId);
                    int serverLegal = 0;
                    foreach (var t in candidates)
                    {
                        if (t == null || t == card) continue;
                        if (ActivateEligibility.IsLegalActivateTarget(card, e, t, _server.CardManager, _server.PlayerManager))
                            serverLegal++;
                    }
                    int projLegal = proj?.TargetUids?.Count ?? 0;
                    Assert.AreEqual(serverLegal, projLegal,
                        $"{card.PlayerTemplate?.CardName} 效果 {e.EffectId} 的合法目标数应与投影一致");
                    if (serverLegal > 0)
                        Assert.IsNotNull(proj, $"{card.PlayerTemplate?.CardName} 效果 {e.EffectId} 有合法目标时投影应含该条目");
                }
            }
            Assert.IsNotNull(jc012?.PlayerTemplate?.EffectDataList, "JC012 模板应有激活效果（前置）");
            Assert.IsNotNull(jc020?.PlayerTemplate?.EffectDataList, "JC020 模板应有激活效果（前置）");
        }

        /// <summary>快照往返保真：投影字段（静止标记/子类别/授予/先手值）经 ToSnapshot→ApplySnapshot 不丢。</summary>
        public void TestParity_投影字段往返保真()
        {
            SetupMatch(mindAssets: 2);
            var card = ServerCard(P_Jc001, CardZone.AreaZone, "parity_area");   // 带 CannotBeAttached + 白眼
            SyncClient();
            var cCard = ClientCard(card);

            Assert.AreEqual(card.StaticFlags.Count, cCard.StaticFlags.Count, "静止标记数量应往返一致");
            Assert.AreEqual(card.CurrentSubCategories.Count, cCard.CurrentSubCategories.Count, "当前子类别数量应往返一致");
            Assert.AreEqual(card.GetAttribute("先手调查"), cCard.GetAttribute("先手调查"), "授予属性（先手调查）应往返一致");
            Assert.AreEqual(card.CurrentFirstStrikeInvestigation, cCard.CurrentFirstStrikeInvestigation,
                "先手调查当前值应往返一致");
            Assert.AreEqual(card.CurrentInvestigation, cCard.CurrentInvestigation,
                "基础调查值不应因回填授予属性而二次叠加");
            Assert.AreEqual(card.CurrentStrength, cCard.CurrentStrength,
                "基础战力值不应因回填授予属性而二次叠加");
        }

        /// <summary>【JC081·[前缀]】"除装备与祝福外不受附属"（子类别白名单静止规则）两端同解。
        /// 规则判定是**静态 helper 读卡面印刷规则**（客户端也持有注入后的模板），
        /// 故除"客户端本地判定 = 服务端判定"外，还断言客户端能从投影实例读出同一份白名单。</summary>
        public void TestParity_JC081_只受装备与祝福附属两端同解()
        {
            SetupMatch(mindAssets: 0);
            var jc081 = ServerCard(P_Jc081, CardZone.AreaZone, "parity_area");   // 目标：只受【装备】【祝福】结附
            var jc020 = ServerCard(P_Jc020, CardZone.Hand);                      // [装备,武器] 附属 → 白名单内
            var jc089 = ServerCard(P_Jc089, CardZone.Hand);                      // [诅咒] 附属 → 白名单外
            SyncClient();

            var c081 = ClientCard(jc081);
            var c020 = ClientCard(jc020);
            var c089 = ClientCard(jc089);
            Assert.IsNotNull(c081, "客户端应重建 JC081");
            Assert.IsNotNull(c089, "客户端应重建 JC089");

            Assert.AreEqual("装备,祝福", EffectEngine.GetAttachSubCategoryWhitelist(jc081),
                "服务端：JC081 的白名单＝装备,祝福");
            Assert.AreEqual("装备,祝福", EffectEngine.GetAttachSubCategoryWhitelist(c081),
                "客户端：从投影实例必须读出同一份白名单（判定不依赖 EffectEngine 实例）");

            Assert.IsTrue(ServerLegalPlay(jc020, jc081), "服务端：【装备】类附属可结附 JC081");
            Assert.IsTrue(ClientLegalPlay(c020, c081), "客户端：同样放行");
            Assert.IsTrue(ProjectionHasPlayTarget(c020, c081), "服务端投影应把 JC081 列为该装备的合法目标");

            Assert.IsFalse(ServerLegalPlay(jc089, jc081), "服务端：【诅咒】类附属不能结附 JC081");
            Assert.IsFalse(ClientLegalPlay(c089, c081), "客户端必须同样拒绝（两端同一判定实现）");
            Assert.IsFalse(ProjectionHasPlayTarget(c089, c081), "服务端投影不应把 JC081 列为该诅咒的合法目标");
        }

        private static List<Script.Data.Effects.EffectData> ServerActivationEffects(CardInstance card)
        {
            var list = new List<Script.Data.Effects.EffectData>();
            if (card?.PlayerTemplate?.EffectDataList == null) return list;
            foreach (var e in card.PlayerTemplate.EffectDataList)
            {
                if (e == null) continue;
                if (e.Timing is Script.Data.Effects.EffectTiming.CharacterActivated
                    or Script.Data.Effects.EffectTiming.AttachmentActivated)
                    list.Add(e);
            }
            return list;
        }
    }
}
