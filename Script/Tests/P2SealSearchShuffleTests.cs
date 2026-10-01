using System.Collections.Generic;
using System.Threading.Tasks;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【P2·封印 / 检索 / 洗牌】三个原语的回归：
    ///
    /// · **封印（Seal）**：被封印者来源（自身/目标/牌库顶/手牌）× 宿主（目标/自身）矩阵；
    ///   唯一出口 `TurnManager.SealCard`（双向引用 + 进 SealedZone + 广播 `OnSealed`）；"背面朝上"= 信息隔离。
    /// · **检索（Search）**：筛选语法、范围（整库 / 牌库顶 N）、四条去向（手牌/进场/暗藏进场/结附/封印）、
    ///   **找到 / 没找到 都洗牌**（用户确认）、联机 `SelectDeckCard` 候选携带展示引用。
    /// · **洗牌（Shuffle）**：走可复现随机源（同种子同结果），不乱动牌数。
    ///
    /// 规则口径（用户确认）：检索后必定洗牌；检索入牌库→手牌**不是抓牌**（不广播 OnDraw）。
    /// 【A53·2026-09 用户拍板】检索**一律强制、不可取消**（8 张 JC 检索卡卡文都没有"你可以寻找"）：
    /// 单机 `allowCancel: false`；联机回包取消/非法时**兜底取第一张匹配**（不能靠取消规避强制检索）。
    /// </summary>
    public class P2SealSearchShuffleTests : TestRunner
    {
        /// <summary>记录请求并按预设规则回答的网关。</summary>
        private sealed class RecGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public System.Func<ChoiceRequest, string> Pick;
            public bool CancelAll;
            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                if (CancelAll) return Task.FromResult(new ChoiceResult { RequestId = req?.RequestId, Cancelled = true });
                string uid = Pick?.Invoke(req);
                if (string.IsNullOrEmpty(uid) && req?.OptionUids != null && req.OptionUids.Count > 0)
                    uid = req.OptionUids[0];
                return Task.FromResult(new ChoiceResult
                {
                    RequestId = req?.RequestId,
                    SelectedUid = uid,
                    Cancelled = false,
                });
            }
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 41 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Probe(string uid, int cost = 1, EffectData effect = null,
            CardCategory category = CardCategory.Character, string subCategory = null)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = "探针_" + uid, Cost = cost, CategoryFlag = category };
            if (subCategory != null) tmpl.SubCategories.Add(subCategory);
            if (effect != null) tmpl.EffectDataList.Add(effect);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>构造一个只含一个动作的效果（事务卡结算口径，无需选目标）。</summary>
        private static EffectData ActionEffect(string id, ActionVerb verb, params string[] args)
        {
            var e = new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };
            var a = new EffectAction { Verb = verb, Args = new Array<string>() };
            foreach (var s in args) a.Args.Add(s);
            e.Actions.Add(a);
            return e;
        }

        /// <summary>检索效果（Args = [筛选, 去向, 附加, 范围]）。</summary>
        private static EffectData SearchEffect(string id, params string[] args)
            => ActionEffect(id, ActionVerb.Search, args);

        private static string FirstArea(GameInitializer game) => game.AreaManager.GetOrderedAreaUids()[0];

        private static bool WaitUntil(System.Func<bool> cond, int timeoutMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                System.Threading.Thread.Sleep(5);
            }
            return cond();
        }

        private static bool StackHas(GameInitializer game, string sourceUid)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.SourceCardUid == sourceUid) return true;
            return false;
        }

        /// <summary>把一张探针卡插到玩家1 牌库顶（index 0 = 牌库顶）。</summary>
        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            var deck = game.DeckManager.GetDeckPlayer1();
            deck.Insert(0, card);
            return card;
        }

        private static string DeckSignature(GameInitializer game)
            => string.Join(",", game.DeckManager.GetPlayer1DeckData().ConvertAll(c => c.UID));

        // ==================== ① 封印：方向矩阵 ====================

        /// <summary>自身 → 目标：把来源卡封到目标角色上（JC100/JC110 型），双向引用与区域归位都要成立。</summary>
        public void TestP2Seal_自身封目标_双向引用与封印区()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_seal_host"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src", effect: ActionEffect("TS_seal_e1", ActionVerb.Seal, "自身", "目标")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, host);

            Assert.AreEqual(CardZone.SealedZone, src.CurrentZone, "被封印者应进入封印子区域");
            Assert.IsTrue(ReferenceEquals(host, src.SealedBy), "被封印者应指向宿主");
            Assert.IsTrue(host.SealedCards.Contains(src), "宿主应记录被封的卡");
            Assert.IsTrue(src.IsSeal && host.IsSealed, "封印卡 / 被封印 两个标记都要置位");
            Assert.AreEqual(host.ZoneDetailId, src.ZoneDetailId, "封印卡地区 = 宿主所在地区");
        }

        /// <summary>目标 → 自身：把目标角色封在来源卡上（JC009 型，方向相反）。</summary>
        public void TestP2Seal_目标封自身_方向反转()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src2", effect: ActionEffect("TS_seal_e2", ActionVerb.Seal, "目标", "自身")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            var victim = game.CardManager.CreateCardInstance(Probe("TS_seal_victim"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, victim);

            Assert.AreEqual(CardZone.SealedZone, victim.CurrentZone, "目标应被封入封印区");
            Assert.IsTrue(ReferenceEquals(src, victim.SealedBy), "被封印者应指向来源卡");
            Assert.IsTrue(src.SealedCards.Contains(victim), "来源卡应记录被封的目标");
        }

        /// <summary>牌库顶 → 自身：先把牌从牌库列表移出，再封印（否则会"同时在牌库与封印区"）。</summary>
        public void TestP2Seal_牌库顶_从牌库移出后再封印()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src3", effect: ActionEffect("TS_seal_e3", ActionVerb.Seal, "牌库顶", "自身")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            var top = PutOnDeckTop(game, Probe("TS_seal_top"));
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(CardZone.SealedZone, top.CurrentZone, "牌库顶那张应被封入封印区");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "封印取走的牌应从牌库列表移除");
            Assert.IsFalse(game.DeckManager.GetPlayer1DeckData().Contains(top), "牌库中不应还留着它");
            Assert.IsTrue(ReferenceEquals(src, top.SealedBy), "宿主应为来源卡");
        }

        /// <summary>手牌 → 自身：无 UI（测试）时确定性兜底取第一张。</summary>
        public void TestP2Seal_手牌_无UI兜底取第一张()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src4", effect: ActionEffect("TS_seal_e4", ActionVerb.Seal, "手牌", "自身")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            int handBefore = game.TurnManager.GetHandOf(1).Count;
            Assert.IsTrue(handBefore > 0, "前置：玩家1 有手牌");

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(handBefore - 1, game.TurnManager.GetHandOf(1).Count, "应从手牌取走 1 张");
            Assert.AreEqual(1, src.SealedCards.Count, "来源卡上应有 1 张封印牌");
            Assert.AreEqual(CardZone.SealedZone, src.SealedCards[0].CurrentZone, "取自手牌的封印牌应进封印区");
        }

        /// <summary>【联机】封印取手牌经 `SelectHandCard` 网关（候选 = 控制者手牌，含展示引用）。</summary>
        public void TestP2Seal_联机_经网关选一张手牌()
        {
            var game = NewGame();
            var gw = new RecGateway
            {
                Pick = req =>
                {
                    Assert.AreEqual(1, req.PlayerId, "封印取手牌应问控制者本人");
                    Assert.AreEqual(ChoiceRequest.Kind.SelectHandCard, req.RequestKind, "请求种类应为 SelectHandCard");
                    return req.OptionUids.Count > 1 ? req.OptionUids[1] : req.OptionUids[0];
                },
            };
            game.SetInputGateway(gw);

            var area = FirstArea(game);
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src5", effect: ActionEffect("TS_seal_e5", ActionVerb.Seal, "手牌", "自身")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ResolveEffectAsync(src.PlayerTemplate.EffectDataList[0], src, null).Wait(5000);

            Assert.AreEqual(1, gw.Requests.Count, "应恰好发起一次选牌请求");
            Assert.AreEqual(handBefore, gw.Requests[0].OptionUids.Count, "候选 = 全部手牌");
            Assert.AreEqual(handBefore, gw.Requests[0].OptionCardRefs.Count, "候选需带展示引用");
            Assert.AreEqual(handBefore - 1, game.TurnManager.GetHandOf(1).Count, "选中的牌应离开手牌");
            Assert.AreEqual(1, src.SealedCards.Count, "来源卡上应有 1 张封印牌");
        }

        // ==================== ② 封印：背面朝上 = 信息隔离 ====================

        public void TestP2Seal_背面朝上_对非控制者隐藏牌面()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_seal_host2"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src6", effect: ActionEffect("TS_seal_e6", ActionVerb.Seal, "自身", "目标", "背面朝上")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, host);

            Assert.IsTrue(src.IsSealedFaceDown, "『背面朝上』应置位信息隔离标记");

            var refSealed = global::ZoneManager.ProjectCard(src, CardZone.SealedZone, src.ControllerId);
            Assert.IsTrue(refSealed.IsSealedFaceDown, "投影应携带背面标记（客户端据此画卡背）");
            // 本人（控制者=1）可见；对手（2）只见占位，且占位仍带"封印在谁身上"（公开信息），不泄露牌面
            Assert.IsFalse(SnapshotFilter.IsHiddenFor(refSealed, 1), "封印者本人应能检视该牌");
            Assert.IsTrue(SnapshotFilter.IsHiddenFor(refSealed, 2), "对手不应能检视背面朝上的封印牌");

            var hidden = SnapshotFilter.MakeHiddenRef(refSealed, 3);
            Assert.IsNull(hidden.TemplateUid, "占位不带牌面身份");
            Assert.AreEqual(host.UID, hidden.SealedByUid, "占位保留封印宿主（公开信息，供客户端挂载卡背）");
            Assert.AreEqual(refSealed.ZoneDetailId, hidden.ZoneDetailId, "占位保留地区归属");

            // 对照：正面封印（无"背面朝上"）对双方都可见
            var src2 = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src7", effect: ActionEffect("TS_seal_e7", ActionVerb.Seal, "自身", "目标")), 1);
            game.ZoneManager.MoveToArea(src2, area, 1);
            EffectEngine.Instance.ResolveEffect(src2.PlayerTemplate.EffectDataList[0], src2, host);
            var refOpen = global::ZoneManager.ProjectCard(src2, CardZone.SealedZone, src2.ControllerId);
            Assert.IsFalse(SnapshotFilter.IsHiddenFor(refOpen, 2), "未声明背面的封印牌对对手可见（封印关系本来公开）");
        }

        // ==================== ③ 封印：OnSealed 触发接线 ====================

        public void TestP2Seal_广播OnSealed触发()
        {
            var game = NewGame();
            // 触发排序窗口：单机由 Arena（_view）承载；测试无 UI → 注入网关走异步编排路径（与 P2DiscardTests 同法）。
            game.SetInputGateway(new RecGateway());
            var area = FirstArea(game);

            // 旁观卡（控制者=1）：每当一张卡被封印时抓 1 张
            var watcherTmpl = Probe("TS_seal_watch", effect: new EffectData
            {
                EffectId = "TS_seal_watch_e",
                Timing = EffectTiming.OnSealed,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
            });
            var watcher = game.CardManager.CreateCardInstance(watcherTmpl, 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            var host = game.CardManager.CreateCardInstance(Probe("TS_seal_host3"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src8", effect: ActionEffect("TS_seal_e8", ActionVerb.Seal, "自身", "目标")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, host);
            System.Threading.Thread.Sleep(150);

            Assert.IsTrue(WaitUntil(() => StackHas(game, watcher.UID)),
                "封印动作应广播 OnSealed → 收集到『每当一张卡被封印时』的触发");
        }

        /// <summary>宿主离场：封印卡连带回 OWNER 手牌（ZoneManager 既有连带规则）。</summary>
        public void TestP2Seal_宿主离场_封印卡回手牌()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_seal_host4"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_seal_src9", effect: ActionEffect("TS_seal_e9", ActionVerb.Seal, "自身", "目标")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);
            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, host);
            Assert.AreEqual(CardZone.SealedZone, src.CurrentZone, "前置：已封印");

            game.CardManager.DestroyInstance(host);   // 宿主被消灭 → 连带解封

            Assert.AreEqual(CardZone.Hand, src.CurrentZone, "宿主离场后封印卡应回 OWNER 手牌");
            Assert.IsNull(src.SealedBy, "封印关系应断开");
            Assert.IsFalse(src.IsSeal, "回手后不再是封印卡");
        }

        // ==================== ④ 检索：筛选 / 去向 / 洗牌 ====================

        public void TestP2Search_费用筛选_取牌加手牌并从牌库移出()
        {
            var game = NewGame();
            var cheap = PutOnDeckTop(game, Probe("TS_search_cheap", cost: 1));
            var pricey = PutOnDeckTop(game, Probe("TS_search_pricey", cost: 5));
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src1", effect: ActionEffect("TS_search_e1", ActionVerb.Search, "费用<=2", "手牌")), 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "检索到的牌应加入手牌");
            Assert.AreEqual(CardZone.Hand, cheap.CurrentZone, "符合条件的廉价牌应进手牌");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "牌库应少 1 张");
            Assert.IsFalse(game.DeckManager.GetPlayer1DeckData().Contains(cheap), "取走的牌应离开牌库列表");
            Assert.AreEqual(CardZone.Deck, pricey.CurrentZone, "不符合筛选的牌应留在牌库");
        }

        public void TestP2Search_多字段筛选_子类别与卡名()
        {
            var game = NewGame();
            var spell = PutOnDeckTop(game, Probe("TS_search_spell", cost: 3, subCategory: "法术"));
            var other = PutOnDeckTop(game, Probe("TS_search_other", cost: 3, subCategory: "人类"));
            var third = PutOnDeckTop(game, Probe("TS_search_third", cost: 6, subCategory: "法术"));   // 子类别对但费用不对

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src2", effect: ActionEffect("TS_search_e2", ActionVerb.Search, "费用<=3;子类别:法术", "手牌")), 1);
            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(CardZone.Hand, spell.CurrentZone, "两条筛选段（AND）都命中 → 应取走");
            Assert.AreEqual(CardZone.Deck, other.CurrentZone, "子类别不符 → 不取");
            Assert.AreEqual(CardZone.Deck, third.CurrentZone, "费用不符 → 不取");
        }

        public void TestP2Search_范围牌库顶N_只看顶部()
        {
            var game = NewGame();
            // 插入顺序：每次 PutOnDeckTop 都插到 index 0，故最后插入的在最上面。
            // 目标卡先插（沉到第 3 位），再插两张盖住它 → "牌库顶:2" 看不到它。
            var buried = PutOnDeckTop(game, Probe("TS_search_target", cost: 1));
            PutOnDeckTop(game, Probe("TS_search_a", cost: 1));
            PutOnDeckTop(game, Probe("TS_search_b", cost: 1));

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src3", effect: SearchEffect("TS_search_e3", "卡名:TS_search_target", "手牌", "", "牌库顶:2")), 1);
            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);
            Assert.AreEqual(CardZone.Deck, buried.CurrentZone, "目标卡在第 3 位（牌库顶:2 之外）→ 不应被找到");

            // 同样筛选、范围改为整库 → 应找到
            var src2 = game.CardManager.CreateCardInstance(
                Probe("TS_search_src4", effect: SearchEffect("TS_search_e4", "卡名:TS_search_target", "手牌", "", "牌库")), 1);
            EffectEngine.Instance.ResolveEffect(src2.PlayerTemplate.EffectDataList[0], src2, null);
            Assert.AreEqual(CardZone.Hand, buried.CurrentZone, "整库范围下应找到同一张卡");
        }

        public void TestP2Search_未找到_仍然洗牌且不改变牌数()
        {
            var game = NewGame();
            PutOnDeckTop(game, Probe("TS_search_none", cost: 1));
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            string orderBefore = DeckSignature(game);

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src5", effect: SearchEffect("TS_search_e5", "卡名:绝对不存在的卡", "手牌")), 1);
            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(deckBefore, game.DeckManager.GetPlayer1DeckCount(), "未找到不应改变牌数");
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "未找到不应改变手牌数");
            // 卡文"然后洗牌"对"没找到"同样成立：检索确实执行了 → 洗牌（固定种子下结果确定）
            Assert.IsFalse(orderBefore == DeckSignature(game), "未找到仍应洗牌（检索行为已发生）");
        }

        /// <summary>检索也必须洗牌：同种子下"检索+洗牌"的结果可复现（证明走了注入的随机源）。</summary>
        public void TestP2Search_洗牌_走可复现随机源()
        {
            string RunOnce()
            {
                var game = NewGame();
                for (int i = 0; i < 8; i++) PutOnDeckTop(game, Probe($"TS_search_r{i}", cost: 1));
                var src = game.CardManager.CreateCardInstance(
                    Probe("TS_search_src6", effect: SearchEffect("TS_search_e6", "费用<=1", "手牌")), 1);
                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);
                return DeckSignature(game);
            }

            string a = RunOnce();
            string b = RunOnce();
            Assert.AreEqual(a, b, "同种子（DebugSeed 固定）下检索+洗牌后的牌序应完全一致");
        }

        // ==================== ⑤ 检索：去向矩阵 ====================

        public void TestP2Search_去向进场_落在来源所在地区()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var found = PutOnDeckTop(game, Probe("TS_search_play", cost: 1));

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src7", effect: SearchEffect("TS_search_e7", "费用<=1", "进场")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(CardZone.AreaZone, found.CurrentZone, "『进场』应把牌放进争夺区");
            Assert.AreEqual(area, found.ZoneDetailId, "落点应为来源所在地区");
            Assert.IsFalse(found.IsCovert, "『进场』为正面进场");
        }

        public void TestP2Search_去向暗藏进场_背面入场()
        {
            var game = NewGame();
            var area = FirstArea(game);
            var found = PutOnDeckTop(game, Probe("TS_search_covert", cost: 1));

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src8", effect: SearchEffect("TS_search_e8", "费用<=1", "暗藏进场")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(CardZone.AreaZone, found.CurrentZone, "『暗藏进场』同样要落进争夺区");
            Assert.AreEqual(area, found.ZoneDetailId, "落点应为来源所在地区");
            Assert.IsTrue(found.IsCovert, "『暗藏进场』应以暗藏者（背面）状态进场");
        }

        public void TestP2Search_去向结附_绑定宿主并进附属区()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_search_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);

            var att = PutOnDeckTop(game, Probe("TS_search_att", cost: 2, category: CardCategory.Attachment));

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src9", effect: SearchEffect("TS_search_e9", "费用<=2", "结附")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, host);

            Assert.AreEqual(CardZone.AttachedZone, att.CurrentZone, "『结附』应把附属放进附属子区域");
            Assert.IsTrue(ReferenceEquals(host, att.AttachedTo), "应建立结附双向引用");
            Assert.IsTrue(host.AttachedCards.Contains(att), "宿主应记录该附属");
            Assert.AreEqual(host.ZoneDetailId, att.ZoneDetailId, "附属地区应跟随宿主");
        }

        public void TestP2Search_去向封印_封到目标身上()
        {
            var game = NewGame();
            var area = FirstArea(game);

            var host = game.CardManager.CreateCardInstance(Probe("TS_search_sealhost"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);

            var sealer = PutOnDeckTop(game, Probe("TS_search_sealcard", cost: 1));

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src10", effect: SearchEffect("TS_search_e10", "费用<=1", "封印")), 1);
            game.ZoneManager.MoveToArea(src, area, 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, host);

            Assert.AreEqual(CardZone.SealedZone, sealer.CurrentZone, "『封印』应把检索到的牌放进封印区");
            Assert.IsTrue(ReferenceEquals(host, sealer.SealedBy), "宿主应为检索效果的 target");
        }

        // ==================== ⑥ 检索：联机网关 ====================

        public void TestP2Search_联机_SelectDeckCard候选带展示引用()
        {
            var game = NewGame();
            var gw = new RecGateway();
            game.SetInputGateway(gw);

            var found = PutOnDeckTop(game, Probe("TS_search_net", cost: 1));
            var other = PutOnDeckTop(game, Probe("TS_search_bystander", cost: 9));   // 名字不匹配筛选（避免前缀误命中）

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src11", effect: SearchEffect("TS_search_e11", "卡名:TS_search_net", "手牌")), 1);
            int handBefore = game.TurnManager.GetHandOf(1).Count;

            EffectEngine.Instance.ResolveEffectAsync(src.PlayerTemplate.EffectDataList[0], src, null).Wait(5000);

            Assert.AreEqual(1, gw.Requests.Count, "应发起一次检索请求");
            var req = gw.Requests[0];
            Assert.AreEqual(ChoiceRequest.Kind.SelectDeckCard, req.RequestKind, "请求种类应为 SelectDeckCard");
            Assert.AreEqual(1, req.PlayerId, "检索请求应发给施术者本人");
            Assert.AreEqual(1, req.OptionUids.Count, "候选 = 通过筛选的牌（此处只有 1 张）");
            Assert.AreEqual(1, req.OptionCardRefs.Count, "牌库对双方不可见 → 候选必须带展示引用");
            Assert.AreEqual(CardZone.Hand, found.CurrentZone, "选中卡应进手牌");
            Assert.AreEqual(CardZone.Deck, other.CurrentZone, "未选中的卡应留在牌库");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1");
        }

        /// <summary>
        /// 【A53·2026-09 用户拍板】检索**强制**：即使回包是"取消"（异常客户端）也不能规避检索
        /// → 按"无有效回包"的确定性兜底口径**取第一张匹配** + 落地 + 洗牌。
        /// （原口径"玩家放弃 = 检索未发生 → 不洗牌"随 A53 作废。）
        /// </summary>
        public void TestP2Search_联机_强制检索_回包取消时兜底取第一张并洗牌()
        {
            var game = NewGame();
            var gw = new RecGateway { CancelAll = true };
            game.SetInputGateway(gw);

            var found = PutOnDeckTop(game, Probe("TS_search_give", cost: 1));
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_search_src12", effect: SearchEffect("TS_search_e12", "费用<=1", "手牌")), 1);
            EffectEngine.Instance.ResolveEffectAsync(src.PlayerTemplate.EffectDataList[0], src, null).Wait(5000);

            Assert.AreEqual(1, gw.Requests.Count, "应发起一次检索请求");
            Assert.IsTrue(gw.Requests[0].ForceChoice, "强制检索：请求应标记 ForceChoice = true（客户端不给取消）");
            Assert.AreEqual(CardZone.Hand, found.CurrentZone, "回包取消 → 兜底取第一张匹配 → 进手牌（强制检索不可规避）");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(1).Count, "手牌 +1");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "牌已从牌库取出（不再是『检索未发生』）");
        }

        // ==================== ⑦ 洗牌原语 ====================

        public void TestP2Shuffle_对手牌库_同种子可复现()
        {
            string RunOnce()
            {
                var game = NewGame();
                for (int i = 0; i < 6; i++) PutOnDeckTop(game, Probe($"TS_shuf{i}", cost: 1));
                var src = game.CardManager.CreateCardInstance(
                    Probe("TS_shuf_src", effect: ActionEffect("TS_shuf_e1", ActionVerb.Shuffle, "自己")), 1);
                int before = game.DeckManager.GetPlayer1DeckCount();
                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);
                Assert.AreEqual(before, game.DeckManager.GetPlayer1DeckCount(), "洗牌不改变牌数");
                return DeckSignature(game);
            }

            string a = RunOnce();
            string b = RunOnce();
            Assert.AreEqual(a, b, "洗牌走注入的可复现随机源 → 同种子下牌序一致");
        }

        public void TestP2Shuffle_对象解析_双方都洗()
        {
            var game = NewGame();
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_shuf_src2", effect: ActionEffect("TS_shuf_e2", ActionVerb.Shuffle, "双方")), 1);

            int d1 = game.DeckManager.GetPlayer1DeckCount();
            int d2 = game.DeckManager.GetPlayer2DeckCount();
            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(d1, game.DeckManager.GetPlayer1DeckCount(), "玩家1 牌库数不变");
            Assert.AreEqual(d2, game.DeckManager.GetPlayer2DeckCount(), "玩家2 牌库数不变");
        }

        // ==================== ⑧ 筛选器单测（语法边界） ====================

        public void TestP2SearchFilter_语法边界()
        {
            var game = NewGame();
            var cheapSpell = game.CardManager.CreateCardInstance(
                Probe("TS_flt_a", cost: 2, subCategory: "法术", category: CardCategory.Sorcery), 1);
            var priceyChar = game.CardManager.CreateCardInstance(Probe("TS_flt_b", cost: 4), 1);

            Assert.IsTrue(DeckSearchFilter.Matches("", cheapSpell), "空筛选 = 任意牌");
            Assert.IsTrue(DeckSearchFilter.Matches("费用<=2", cheapSpell), "费用<=2 命中");
            Assert.IsFalse(DeckSearchFilter.Matches("费用<=2", priceyChar), "费用<=2 不命中 4 费");
            Assert.IsTrue(DeckSearchFilter.Matches("费用>=3", priceyChar), "费用>=3 命中");
            Assert.IsTrue(DeckSearchFilter.Matches("类别:事务", cheapSpell), "类别筛选命中事务卡");
            Assert.IsFalse(DeckSearchFilter.Matches("类别:事务", priceyChar), "类别筛选不命中角色卡");
            Assert.IsTrue(DeckSearchFilter.Matches("子类别:法术,书籍", cheapSpell), "同段多值 = OR");
            Assert.IsTrue(DeckSearchFilter.Matches("费用<=2;子类别:法术", cheapSpell), "跨段 = AND");
            Assert.IsFalse(DeckSearchFilter.Matches("费用<=2;子类别:人类", cheapSpell), "任一 AND 段不成立即不通过");
            Assert.IsFalse(DeckSearchFilter.Matches("不存在的字段:xx", cheapSpell),
                "未知字段 fail-closed（避免写错字段名静默匹配全库）");
        }
    }
}
