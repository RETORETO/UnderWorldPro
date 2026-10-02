using System.Collections.Generic;
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
    /// 【2026-09·原子操作期间的玩家选择】回归：用户口径——
    /// 「一个原子操作期间如果需要玩家选择，**玩家选择完成之前会阻塞进程**；完成前**只有持续效果**可以
    ///   影响游戏，**无法加入新的行动效果/触发效果进入堆叠**。而卡的打出（包括支付费用、额外费用、
    ///   确认忠诚等待）属于原子操作，卡的结算/效果的结算也属于原子操作。」
    ///
    /// 本套件锁定 6 组：
    ///  ① **锁原语**：计数式决策持锁（可嵌套；某个模态窗关闭**不得**把外层持锁掀掉；`Reset()` 归零）；
    ///     持锁期间 `Pass` / `PlayCard` / `ActivateAbility` 一律被拒。
    ///  ② **引擎挂起即持锁**：弃牌 / 封印取手牌 / 检索 / 可选支付 / 多玩家逐位牺牲——窗口未点击前
    ///     `IsAtomicLocked == true`，且**后续动作不得抢先执行**（挂起-续算）；选择交付后释放并续算一次。
    ///  ③ **联机决策等待持锁**：领袖 / 道途 / 遏制 / 预测 / JC119 免费打出取目标（服务端无 View）。
    ///  ④ **打出卡原子段**：从确认忠诚起持锁（`OnCardPlayed` 时刻可观测），结束后释放；早退不泄漏。
    ///  ⑤ **激活（宣布使用效果）原子段**：入堆叠时刻可观测持锁；早退不泄漏。
    ///  ⑥ **恢复步骤手牌上限弃牌**：等待玩家选择期间持锁，完成/异常后释放。
    /// </summary>
    public class P3AtomicDecisionLockTests : TestRunner
    {
        // ==================== 装配 / 探针 ====================

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 20260920 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 4 : -1,
            };
            tmpl._effectsInjected = true;   // 否则 CardEffectFactory.EnsureEffects 会清空注入的效果
            return tmpl;
        }

        /// <summary>【领袖】探针：模板固有授予【领袖】（走 `HasInherentKeyword`，不依赖持续效果注册时机）。</summary>
        private static PlayerCardData LeaderProbe(string uid) => KeywordProbe(uid, "领袖");

        /// <summary>【道途】探针：附属 + 子类别＝道途（`PathRule.IsPath` 读子类别）。</summary>
        private static PlayerCardData PathProbe(string uid)
        {
            var tmpl = Probe(uid, CardCategory.Attachment);
            tmpl.SubCategories.Add("道途");
            return tmpl;
        }

        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        /// <summary>把一张「道途」结附到本方秘社上（与 `EffectEngine.AttachSearchedCard` 的秘社落点同款）。</summary>
        private static CardInstance AttachToCouncil(GameInitializer game, PlayerCardData tmpl, int playerId)
        {
            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
            Assert.IsTrue(council != null && council.Count > 0, "前置：该玩家有秘社实例");
            var host = council[0];
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            card.IsAttached = true;
            card.AttachedTo = host;
            host.AttachedCards.Add(card);
            game.ZoneManager.MoveToAttachedZone(card, "", playerId);
            return card;
        }

        private static PlayerCardData KeywordProbe(string uid, string keyword)
        {
            var tmpl = Probe(uid);
            var cont = new EffectData
            {
                EffectId = uid + "_cont",
                Timing = EffectTiming.Continuous,
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = keyword, Amount = 1 },
                },
            };
            tmpl.EffectDataList.Add(cont);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(card, area, playerId);
            return card;
        }

        private static CardInstance ToHand(GameInitializer game, PlayerCardData tmpl, int playerId)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToHand(card, playerId);
            return card;
        }

        private static EffectData ActionEffect(string id, params (ActionVerb Verb, string[] Args)[] actions)
        {
            var e = new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };
            foreach (var (verb, args) in actions)
            {
                var a = new EffectAction { Verb = verb, Args = new Array<string>() };
                if (args != null) foreach (var s in args) a.Args.Add(s ?? "");
                e.Actions.Add(a);
            }
            return e;
        }

        /// <summary>0 费响应型事务卡（C 档：只要手牌/忠诚/费用满足即可打出入堆叠，不受行动步骤门禁）。</summary>
        private static CardInstance MakePlayableCard(GameInitializer game, int playerId, string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针事务_" + uid, Cost = 0, Defense = -1,
                CategoryFlag = CardCategory.Sorcery,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = uid + "_e",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Cost = 0,
                Duration = "瞬时",
            });
            tmpl._effectsInjected = true;
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToHand(inst, playerId);
            return inst;
        }

        private static void ProvideMana(GameInitializer game, int playerId)
        {
            var tmpl = Probe($"TS_lock_mana_{playerId}", CardCategory.Attachment);
            tmpl.Colors.Add(CardColor.Blue);
            var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToAsset(asset, playerId);
        }

        private static string FirstArea(GameInitializer game) => game.AreaManager.GetOrderedAreaUids()[0];

        // ==================== 桩：异步选择窗（真实窗口语义：点了才回调） ====================

        private sealed class AsyncHooks : Script.Interfaces.IGameUIHooks
        {
            public readonly List<(int PlayerId, List<CardInstance> Candidates, System.Action<CardInstance> Callback)> PickPending
                = new List<(int, List<CardInstance>, System.Action<CardInstance>)>();
            public readonly List<(int PlayerId, List<CardInstance> Candidates, System.Action<CardInstance> Callback)> DiscardPending
                = new List<(int, List<CardInstance>, System.Action<CardInstance>)>();
            public readonly List<System.Action<int>> NumberPending = new List<System.Action<int>>();
            public readonly List<System.Action<int>> PlayerTargetPending = new List<System.Action<int>>();
            /// <summary>数值输入"确认值"（-1＝取消）；被问到时立即回包（用于非挂起语义的用例）。</summary>
            public bool AutoAnswerNumber = false;
            public int AutoNumber = -1;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }

            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) => DiscardPending.Add((chooserPlayerId, candidates, onPicked));

            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => PickPending.Add((chooserPlayerId, candidates, onPicked));

            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid)
                => onPickedAreaUid?.Invoke(null);

            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }

            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }

            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen) => PlayerTargetPending.Add(onChosen);

            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(0);

            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                if (AutoAnswerNumber) { onChosen?.Invoke(AutoNumber); return; }
                NumberPending.Add(onChosen);
            }

            /// <summary>"玩家点击"最新打开的选卡窗（null＝第一候选）。</summary>
            public void ClickPick(string uid)
            {
                Assert.IsTrue(PickPending.Count > 0, "前置：应有一个等待玩家的选卡窗");
                var (_, candidates, cb) = PickPending[0];
                PickPending.RemoveAt(0);
                cb?.Invoke(Find(candidates, uid));
            }

            /// <summary>"玩家点击"最新打开的弃牌窗（null＝第一候选）。</summary>
            public void ClickDiscard(string uid)
            {
                Assert.IsTrue(DiscardPending.Count > 0, "前置：应有一个等待玩家的弃牌窗");
                var (_, candidates, cb) = DiscardPending[0];
                DiscardPending.RemoveAt(0);
                cb?.Invoke(Find(candidates, uid));
            }

            public void AnswerNumber(int value)
            {
                Assert.IsTrue(NumberPending.Count > 0, "前置：应有一个等待玩家的数值窗");
                var cb = NumberPending[0];
                NumberPending.RemoveAt(0);
                cb?.Invoke(value);
            }

            private static CardInstance Find(List<CardInstance> candidates, string uid)
            {
                if (!string.IsNullOrEmpty(uid) && candidates != null)
                    foreach (var c in candidates) if (c?.UID == uid) return c;
                return (candidates != null && candidates.Count > 0) ? candidates[0] : null;
            }
        }

        /// <summary>联机网关桩：请求**挂起**（返回未完成 Task），由 `Answer*` 手动回包。</summary>
        private sealed class DeferGateway : Script.Net.IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            private readonly List<System.Threading.Tasks.TaskCompletionSource<ChoiceResult>> _pending
                = new List<System.Threading.Tasks.TaskCompletionSource<ChoiceResult>>();

            public int PendingCount => _pending.Count;

            public System.Threading.Tasks.Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                var tcs = new System.Threading.Tasks.TaskCompletionSource<ChoiceResult>();
                _pending.Add(tcs);
                return tcs.Task;
            }

            public void AnswerUid(int index, string uid)
            {
                var r = new ChoiceResult { RequestId = Requests[index].RequestId, SelectedUid = uid };
                _pending[index].TrySetResult(r);
            }

            public void AnswerUids(int index, params string[] uids)
            {
                var r = new ChoiceResult { RequestId = Requests[index].RequestId };
                if (uids != null) foreach (var u in uids) r.SelectedUids.Add(u);
                _pending[index].TrySetResult(r);
            }
        }

        // ==================== ① 锁原语 ====================

        public void TestAtomicLock_计数持锁_嵌套与模态不误放()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            Assert.IsFalse(tm.IsAtomicLocked, "前置：非原子段");

            tm.BeginDecisionHold();
            tm.BeginDecisionHold();
            Assert.IsTrue(tm.IsAtomicLocked, "进入决策持锁后应锁定");
            Assert.AreEqual(2, tm.DecisionHoldDepth, "计数＝2（可嵌套）");

            tm.NotifyModalOpened();
            tm.NotifyModalClosed();
            Assert.IsTrue(tm.IsAtomicLocked,
                "某个模态窗关闭**不得**把外层的决策持锁一并掀掉（旧实现会直接解锁）");

            tm.EndDecisionHold();
            Assert.IsTrue(tm.IsAtomicLocked, "计数未归零 → 仍锁定");
            tm.EndDecisionHold();
            Assert.IsFalse(tm.IsAtomicLocked, "计数归零且无模态窗 → 解锁");
        }

        public void TestAtomicLock_Reset归零不泄漏()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            tm.BeginDecisionHold();
            Assert.IsTrue(tm.IsAtomicLocked, "前置：已锁定");
            tm.Reset();
            Assert.IsFalse(tm.IsAtomicLocked, "换局/复位必须把决策持锁归零（杜绝跨局锁死）");
            Assert.AreEqual(0, tm.DecisionHoldDepth, "持锁计数归零");
        }

        public void TestAtomicLock_持锁期间拒绝让过与出牌()
        {
            var game = NewGame();
            ProvideMana(game, 1);
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;
            var card = MakePlayableCard(game, 1, "TS_lock3_card");

            tm.EnterAtomic();
            tm.Pass(1);
            Assert.AreEqual(1, tm.PriorityPlayerId, "持锁期间让过被拒（行动权不得转移）");
            Assert.IsFalse(tm.PlayCard(1, card.UID), "持锁期间出牌被拒");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "被拒后卡仍在手牌");

            tm.ExitAtomicWhenIdle();
            Assert.IsTrue(tm.PlayCard(1, card.UID), "解锁后可正常打出（未留下副作用）");
        }

        // ==================== ② 引擎挂起即持锁 + 挂起-续算顺序 ====================

        public void TestAtomicLock_弃牌挂起期间持锁且后续动作不抢先()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var src = OnField(game, Probe("TS_lock4_src"), 1, area);
            var handCard = ToHand(game, Probe("TS_lock4_h", CardCategory.Character), 1);

            var hooks = new AsyncHooks();
            EffectEngine.Instance.UIHooks = hooks;
            var e = ActionEffect("TS_lock4_e",
                (ActionVerb.Discard, new[] { "1", "自己" }),
                (ActionVerb.PlaceCounter, new[] { "时间标志", "1", "自身" }));

            EffectEngine.Instance.ResolveEffect(e, src, null, "");

            Assert.AreEqual(1, hooks.DiscardPending.Count, "已打开弃牌窗等待玩家");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待玩家选择期间必须持原子锁");
            Assert.AreEqual(CardZone.Hand, handCard.CurrentZone, "未点击前不得弃牌");
            Assert.AreEqual(0, src.Counters.ContainsKey("时间标志") ? src.Counters["时间标志"] : 0,
                "未点击前**后续动作不得抢先执行**（挂起-续算）");

            hooks.ClickDiscard(handCard.UID);

            Assert.AreEqual(CardZone.Graveyard, handCard.CurrentZone, "玩家选择后弃牌落地");
            Assert.AreEqual(1, src.Counters.ContainsKey("时间标志") ? src.Counters["时间标志"] : 0,
                "随后从断点续算（放标志已执行）");
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "续算完成 → 释放原子锁");
        }

        public void TestAtomicLock_封印手牌挂起期间持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var host = OnField(game, Probe("TS_lock5_host"), 1, area);
            var handCard = ToHand(game, Probe("TS_lock5_h", CardCategory.Character), 1);

            var hooks = new AsyncHooks();
            EffectEngine.Instance.UIHooks = hooks;
            var e = ActionEffect("TS_lock5_e", (ActionVerb.Seal, new[] { "手牌", "自身", "" }));

            EffectEngine.Instance.ResolveEffect(e, host, null, "");

            Assert.AreEqual(1, hooks.PickPending.Count, "已打开封印选牌窗");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待玩家选择期间持锁");
            Assert.IsFalse(handCard.CurrentZone == CardZone.SealedZone, "未点击前未封印");

            hooks.ClickPick(handCard.UID);

            Assert.AreEqual(CardZone.SealedZone, handCard.CurrentZone, "点击后封印落地");
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "续算完成 → 释放");
        }

        public void TestAtomicLock_检索挂起期间持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var src = OnField(game, Probe("TS_lock6_src"), 1, area);
            var deckCard = Probe("TS_lock6_deck", CardCategory.Sorcery);
            var inst = game.CardManager.CreateCardInstance(deckCard, 1);
            inst.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, inst);

            var hooks = new AsyncHooks();
            EffectEngine.Instance.UIHooks = hooks;
            var e = ActionEffect("TS_lock6_e", (ActionVerb.Search, new[] { "", "手牌", "", "牌库" }));

            EffectEngine.Instance.ResolveEffect(e, src, null, "");

            Assert.AreEqual(1, hooks.PickPending.Count, "已打开检索选牌窗");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待玩家选择期间持锁");
            Assert.IsFalse(inst.CurrentZone == CardZone.Hand, "未点击前未入手");

            hooks.ClickPick(inst.UID);

            Assert.AreEqual(CardZone.Hand, inst.CurrentZone, "点击后加入手牌（检索不是抓牌）");
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "续算（含洗牌收尾）完成 → 释放");
        }

        public void TestAtomicLock_可选支付挂起期间持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var src = OnField(game, Probe("TS_lock7_src"), 1, area);
            ProvideMana(game, 1);

            var hooks = new AsyncHooks();
            EffectEngine.Instance.UIHooks = hooks;
            var e = ActionEffect("TS_lock7_e", (ActionVerb.PayOptional, new[] { "1", "0", "1" }));

            EffectEngine.Instance.ResolveEffect(e, src, null, "");

            Assert.AreEqual(1, hooks.NumberPending.Count, "已打开数值输入窗（是否支付）");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待玩家选择期间持锁");

            hooks.AnswerNumber(0);   // 不支付

            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包后释放");
        }

        public void TestAtomicLock_多玩家牺牲逐位窗口期间持续持锁()
        {
            var game = NewGame();
            game.TurnManager.FirstPlayerId = 1;
            string area = FirstArea(game);
            var p1a = OnField(game, Probe("TS_lock8_p1a"), 1, area);
            var p1b = OnField(game, Probe("TS_lock8_p1b"), 1, area);
            var p2a = OnField(game, Probe("TS_lock8_p2a"), 2, area);

            var hooks = new AsyncHooks();
            EffectEngine.Instance.UIHooks = hooks;
            var e = ActionEffect("TS_lock8_e", (ActionVerb.SacrificeChoice, new[] { "角色", "每位玩家", "任意" }));

            EffectEngine.Instance.ResolveEffect(e, p1a, null, "");

            Assert.AreEqual(1, hooks.PickPending.Count, "先问先手玩家");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "第一位玩家选择期间持锁");

            hooks.ClickPick(p1b.UID);
            Assert.AreEqual(1, hooks.PickPending.Count, "接着问第二位玩家");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "多玩家编排的**整段**期间持续持锁");

            hooks.ClickPick(p2a.UID);
            Assert.AreEqual(CardZone.Graveyard, p1b.CurrentZone, "全部选完 → 一起牺牲");
            Assert.AreEqual(CardZone.Graveyard, p2a.CurrentZone, "两位玩家的选择一起落地");
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "全部完成 → 释放");
        }

        // ==================== ③ 联机决策等待持锁 ====================

        public void TestAtomicLock_联机领袖回包期间持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var l1 = OnField(game, LeaderProbe("TS_lock10_a"), 1, area);   // 场上 1 个领袖：无冲突

            // 必须先注入网关：第 2 个领袖**进场瞬间**由 `ZoneManager.OnCardEnteredArea` 自动触发本规则
            var gw = new DeferGateway();
            EffectEngine.Instance.InputGateway = gw;

            var l2 = OnField(game, LeaderProbe("TS_lock10_b"), 1, area);
            Assert.IsTrue(LeaderRule.IsLeader(l2), "前置：探针被识别为领袖");

            Assert.AreEqual(1, gw.PendingCount, "进场超限 → 立即下发牺牲选择请求（等待回包）");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待领袖牺牲选择期间必须持原子锁");

            gw.AnswerUid(0, l1.UID);

            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包落地 → 释放");
            Assert.AreEqual(CardZone.Graveyard, l1.CurrentZone, "按回包牺牲（不变量恢复至 1 个领袖）");
        }

        public void TestAtomicLock_联机道途回包期间持锁()
        {
            var game = NewGame();
            // 先：印有「至多一个道途」规则的 JC034（道途唯一性只在该声明在场时生效）
            var first = AttachToCouncil(game, Load("JC034_xinshouzhidao"), 1);
            Assert.IsTrue(PathRule.IsPath(first), "前置：JC034 是道途");

            // 必须先注入网关：第 2 张道途**落场瞬间**由 `ZoneManager.OnPostMove` 自动触发本规则
            var gw = new DeferGateway();
            EffectEngine.Instance.InputGateway = gw;

            var second = AttachToCouncil(game, PathProbe("TS_lock11_b"), 1);
            Assert.IsTrue(PathRule.IsPath(second), "前置：探针被识别为道途（子类别）");

            Assert.AreEqual(1, gw.PendingCount, "超限 → 立即下发牺牲选择请求");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待道途牺牲选择期间持锁");

            gw.AnswerUid(0, first.UID);
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包后释放");
            Assert.AreEqual(CardZone.Graveyard, first.CurrentZone, "按回包牺牲");
        }

        public void TestAtomicLock_联机遏制回包期间持锁()
        {
            var game = NewGame();
            var areas = game.AreaManager.GetOrderedAreaUids();
            game.AreaManager.AddPowerMark(areas[0], 2, 1);
            game.AreaManager.AddPowerMark(areas[1], 2, 1);
            var src = OnField(game, Probe("TS_lock12_src"), 1, areas[0]);

            var gw = new DeferGateway();
            EffectEngine.Instance.InputGateway = gw;

            CurbRule.Resolve(EffectEngine.Instance, src, 1, "对手");   // X=1 < 总数 2 → 由被遏制者分配

            Assert.AreEqual(1, gw.PendingCount, "已下发按地区分配请求");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待遏制分配回包期间持锁");

            gw.AnswerUids(0, areas[0]);
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包后释放");
        }

        public void TestAtomicLock_联机预测回包期间持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var src = OnField(game, Probe("TS_lock13_src"), 1, area);

            var gw = new DeferGateway();
            EffectEngine.Instance.InputGateway = gw;

            PredictionRule.Resolve(EffectEngine.Instance, src, 2);

            Assert.IsTrue(gw.PendingCount >= 1, "已下发预测分配请求");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待预测分配回包期间持锁");

            gw.AnswerUids(0);   // 全部置底（合法回包）
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包后释放");
        }

        public void TestAtomicLock_联机JC119免费打出取目标期间持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);

            // 需要选目标（地区）的免费打出牌：声明 OnSorceryCast 目标＝地区且需要选择
            var tmpl = Probe("TS_lock14_card", CardCategory.Sorcery);
            tmpl.Cost = 0;
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "TS_lock14_e",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                Actions = new Array<EffectAction>(),
            });
            tmpl._effectsInjected = true;
            Assert.IsTrue(tmpl.GetRequiresTarget(), "前置：该牌需要选目标");

            var card = ToHand(game, tmpl, 1);

            var gw = new DeferGateway();
            EffectEngine.Instance.InputGateway = gw;

            game.TurnManager.EnqueueFreePlay(1, card.UID);
            game.TurnManager.ProcessQueuedFreePlays();

            Assert.AreEqual(1, gw.PendingCount, "免费打出取目标经网关回环");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待目标回包期间持锁");

            gw.AnswerUid(0, area);
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包后释放（随即走正常打出流程）");
        }

        // ==================== ④ 打出卡原子段 ====================

        public void TestAtomicLock_PlayCard打出期间持锁_结束后释放()
        {
            var game = NewGame();
            ProvideMana(game, 1);
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;
            var card = MakePlayableCard(game, 1, "TS_lock15_card");

            bool lockedAtPlayedEvent = false;
            tm.OnCardPlayed += (pid, uid, target) => { lockedAtPlayedEvent = tm.IsAtomicLocked; };

            Assert.IsTrue(tm.PlayCard(1, card.UID), "前置：可正常打出");
            Assert.IsTrue(lockedAtPlayedEvent, "打出卡的原子段覆盖「入堆叠 + 广播打出事件」全过程");
            Assert.IsFalse(tm.IsAtomicLocked, "打出结束后（无模态窗）释放原子锁");
        }

        public void TestAtomicLock_PlayCard早退不泄漏锁()
        {
            var game = NewGame();
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;

            // ① 未找到实例
            Assert.IsFalse(tm.PlayCard(1, "不存在的UID"), "找不到卡 → 拒绝");
            Assert.IsFalse(tm.IsAtomicLocked, "早退不得留下原子锁（①）");

            // ② 费用不足（1 费卡 + 无资产）
            var costly = Probe("TS_lock16_cost", CardCategory.Sorcery);
            costly.Cost = 2;
            var inst = ToHand(game, costly, 1);
            Assert.IsFalse(tm.PlayCard(1, inst.UID), "费用不足 → 拒绝");
            Assert.IsFalse(tm.IsAtomicLocked, "早退不得留下原子锁（费用不足）");
            Assert.AreEqual(CardZone.Hand, inst.CurrentZone, "拒绝后卡仍在手牌");

            // ③ 目标地区不存在（角色卡落点非法）
            var charTmpl = Probe("TS_lock16_char", CardCategory.Character);
            var ch = ToHand(game, charTmpl, 1);
            Assert.IsFalse(tm.PlayCard(1, ch.UID, "不存在的地区"), "目标地区非法 → 拒绝");
            Assert.IsFalse(tm.IsAtomicLocked, "早退不得留下原子锁（目标非法）");
        }

        // ==================== ⑤ 激活（宣布使用效果）原子段 ====================

        private static EffectData ActivationEffect(string id)
            => new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速行动",
                Cost = 0,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };

        public void TestAtomicLock_激活入堆叠期间持锁_结束后释放()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var tmpl = Probe("TS_lock17_unit");
            tmpl.EffectDataList.Add(ActivationEffect("TS_lock17_e"));
            tmpl._effectsInjected = true;
            var unit = OnField(game, tmpl, 1, area);
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;

            bool lockedAtStackPush = false;
            game.StackManager.OnStackChanged += () => { lockedAtStackPush = tm.IsAtomicLocked; };

            EffectEngine.Instance.ActivateAbility(unit, tmpl.EffectDataList[0], null, -1);

            Assert.AreEqual(1, game.StackManager.Entries.Count, "前置：激活应入堆叠");
            Assert.IsTrue(lockedAtStackPush, "「宣布使用效果」的原子段覆盖代价支付 → 入堆叠全程");
            Assert.IsFalse(tm.IsAtomicLocked, "入堆叠完成后释放");
        }

        public void TestAtomicLock_激活早退不泄漏锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var tmpl = Probe("TS_lock18_unit");
            tmpl.EffectDataList.Add(ActivationEffect("TS_lock18_e"));
            tmpl._effectsInjected = true;
            var unit = OnField(game, tmpl, 1, area);
            var tm = game.TurnManager;
            tm.FirstPlayerId = 1;
            tm.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            tm.PriorityPlayerId = 1;

            // ① 费用不足（费用 > 可用）→ 早退
            var costly = Probe("TS_lock18_cost");
            var costlyE = ActivationEffect("TS_lock18_cost_e");
            costlyE.Cost = 99;
            costly.EffectDataList.Add(costlyE);
            costly._effectsInjected = true;
            var unit2 = OnField(game, costly, 1, area);
            EffectEngine.Instance.ActivateAbility(unit2, costly.EffectDataList[0], null, -1);
            Assert.IsFalse(tm.IsAtomicLocked, "激活早退（费用不足）不得留下原子锁");

            // ② 缺少必需目标 → 早退
            var needTarget = Probe("TS_lock18_need");
            var needE = ActivationEffect("TS_lock18_need_e");
            needE.Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true };
            needTarget.EffectDataList.Add(needE);
            needTarget._effectsInjected = true;
            var unit3 = OnField(game, needTarget, 1, area);
            EffectEngine.Instance.ActivateAbility(unit3, needTarget.EffectDataList[0], null, -1);
            Assert.IsFalse(tm.IsAtomicLocked, "激活早退（缺目标）不得留下原子锁");
        }

        // ==================== ⑥ 恢复步骤手牌上限弃牌 ====================

        public void TestAtomicLock_恢复步骤弃牌等待期间持锁()
        {
            var game = NewGame();
            var tm = game.TurnManager;

            // 手牌超上限 2 张（HandLimit=7）
            while (tm.GetHandOf(1).Count <= tm.HandLimit + 1)
                ToHand(game, Probe($"TS_lock19_h{tm.GetHandOf(1).Count}", CardCategory.Character), 1);
            Assert.IsTrue(tm.GetHandOf(1).Count > tm.HandLimit, "前置：手牌超上限");

            var hooks = new AsyncHooks();
            EffectEngine.Instance.UIHooks = hooks;

            tm.EnterPhase(global::TurnManager.TurnPhase.EndRecover);

            Assert.IsTrue(hooks.DiscardPending.Count >= 1, "恢复步骤·手牌上限弃牌应弹窗询问玩家");
            Assert.IsTrue(tm.IsAtomicLocked, "恢复步骤弃牌等待玩家选择期间必须持原子锁");

            // 依次点掉全部待弃（每点一次可能再开一个窗）
            int guard = 0;
            while (hooks.DiscardPending.Count > 0 && guard++ < 10)
                hooks.ClickDiscard(null);

            Assert.IsFalse(tm.IsAtomicLocked, "弃牌完成（含帧同步等待）→ 释放原子锁");
            Assert.IsTrue(tm.GetHandOf(1).Count <= tm.HandLimit, "手牌已回到上限内");
        }
    }
}
