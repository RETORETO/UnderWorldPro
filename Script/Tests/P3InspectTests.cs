using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【P3·H·检视（私密查看）】按卡文实现并锁定：
    ///  · `ActionVerb.Inspect`：把被检视卡的面呈现给**检视者本人**（效果来源控制者），**不改变任何状态**
    ///    （不翻面、不展示给对手）；单机经 `UiNotificationBus.NotifyCardInspected`，联机经 `ChoiceRequest.Kind.InspectCards`；
    ///  · 与"展示"（`Reveal`，双方可见）语义相对。
    /// 覆盖卡：**JC062 调查档案**（快速行动：检视**至多两个**目标暗藏者，抓一张牌；FAQ：一个都不检视也能打出）。
    /// 复用：K15 多目标（两个可留空槽，至少 0 个）→ 逐槽取目标 + 确认面板。
    /// </summary>
    public class P3InspectTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 173 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_ins_asset_{playerId}_{i}", CardCategory.Attachment);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        // ==================== ① 结构 ====================

        public void TestInspect_JC062结构()
        {
            var tmpl = Load("JC062_diaochadangan");
            Assert.IsNotNull(tmpl, "JC062 模板应可加载");
            Assert.IsTrue(tmpl.CanPlayAsResponse(), "『快速行动』→ 可响应打出（含 PlayTiming.Response 效果）");

            var e = FindEffect(tmpl, "JC062_1_InspectUpToTwoCoverts");
            Assert.IsNotNull(e, "JC062 应有『检视至多两个目标暗藏者』效果");
            Assert.AreEqual(2, e.TargetSlots.Count, "两个可留空槽（至多两个目标）");
            Assert.IsTrue(e.TargetSlots[0].Optional && e.TargetSlots[1].Optional, "两槽都可留空");
            Assert.AreEqual(TargetKind.Covert, e.TargetSlots[0].Spec.Kind, "目标＝暗藏者");
            Assert.AreEqual(TargetKind.Covert, e.TargetSlots[1].Spec.Kind, "目标＝暗藏者");
            Assert.AreEqual(0, e.MinChosenSlots, "可以一个都不检视（FAQ：仅为抓一张牌）");
            Assert.AreEqual(ActionVerb.Inspect, e.Actions[0].Verb, "动作＝检视");

            var draw = FindEffect(tmpl, "JC062_2_DrawOne");
            Assert.IsNotNull(draw, "应有独立的『抓一张牌』效果（不落进多目标循环）");
            Assert.AreEqual(ActionVerb.Draw, draw.Actions[0].Verb, "动作＝抓牌");
            Assert.AreEqual("1", draw.Actions[0].Args[0], "抓 1 张");
            Assert.IsFalse(MultiTargetSlots.IsMultiTarget(draw), "抓牌效果本身不是多目标（一次结算只抓一张）");
        }

        // ==================== ② 单机：检视通知只给检视者（且不改变状态） ====================

        public void TestInspect_私密呈现且不改变状态()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var covert = game.CardManager.CreateCardInstance(Probe("TS_ins_cov"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            var sorcery = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            var inspector = sorcery.ControllerId;   // = 1

            var inspected = new List<CardInstance>();
            var receivers = new List<int>();
            System.Action<CardInstance, int> handler = (card, by) => { inspected.Add(card); receivers.Add(by); };
            UiNotificationBus.Instance.CardInspected += handler;
            try
            {
                var e = FindEffect(sorcery.PlayerTemplate, "JC062_1_InspectUpToTwoCoverts");
                // JC062 是多目标效果（K15）：目标由**逐槽选择**给出，`target` 参数在该路径下不参与解析。
                var choices = new List<TargetChoice> { TargetChoice.Card(covert.UID), TargetChoice.Empty };
                EffectEngine.Instance.ResolveEffect(e, sorcery, null, area, -1, -1, -1, choices);

                Assert.AreEqual(1, inspected.Count, "应产生一次检视通知");
                Assert.IsTrue(ReferenceEquals(inspected[0], covert), "被检视的正是目标卡");
                Assert.AreEqual(inspector, receivers[0], "检视者＝效果来源控制者（私密：只有他本人）");
                Assert.IsTrue(covert.IsCovert, "**检视不翻面**（仍是暗藏者）");
                Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "检视不移动卡");
            }
            finally
            {
                UiNotificationBus.Instance.CardInspected -= handler;
            }
        }

        // ==================== ③ 端到端：打出 JC062 → 检视最多两个暗藏者 + 抓一张牌 ====================

        /// <summary>选两个暗藏者：两者都被检视；抓牌恰好 1 张（多目标不会重复抓）。</summary>
        public void TestInspect_JC062打出检视两个并抓一张()
        {
            var game = NewGame();
            var areas = Areas(game);
            string a1 = areas[0], a2 = areas[1];
            GiveAssets(game, 1, 1, (CardColor)4);    // JC062 费用 1、颜色忠诚 4:1

            var c1 = game.CardManager.CreateCardInstance(Probe("TS_ins_c1"), 2);
            game.ZoneManager.MoveToArea(c1, a1, 2);
            c1.SetCovert(true);
            var c2 = game.CardManager.CreateCardInstance(Probe("TS_ins_c2"), 2);
            game.ZoneManager.MoveToArea(c2, a2, 2);
            c2.SetCovert(true);

            var card = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            int handBefore = game.TurnManager.GetHandOf(1).Count;   // 含这张事务卡

            var inspected = new List<CardInstance>();
            System.Action<CardInstance, int> handler = (c, by) => inspected.Add(c);
            UiNotificationBus.Instance.CardInspected += handler;
            try
            {
                var choices = new List<TargetChoice>
                {
                    TargetChoice.Card(c1.UID),
                    TargetChoice.Card(c2.UID),
                };
                Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "应能打出 JC062（检视两个暗藏者）");
                ResolveStackOnce(game);

                Assert.AreEqual(2, inspected.Count, "两个被选中的暗藏者各被检视一次");
                Assert.IsTrue(inspected.Exists(x => ReferenceEquals(x, c1)), "含第 1 个目标");
                Assert.IsTrue(inspected.Exists(x => ReferenceEquals(x, c2)), "含第 2 个目标");
                // 抓 1 张：手牌 = 原手牌 - 打出的事务卡 + 抓到的 1 张
                Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "打出 1 张 + 抓 1 张 → 手牌数不变");
                Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡进墓地");
                Assert.IsTrue(c1.IsCovert && c2.IsCovert, "检视不翻面");
            }
            finally
            {
                UiNotificationBus.Instance.CardInspected -= handler;
            }
        }

        /// <summary>FAQ：即使场上没有暗藏者也能打出（一个都不检视），仅仅为了抓一张牌。</summary>
        public void TestInspect_JC062无暗藏者也可只抓牌()
        {
            var game = NewGame();
            GiveAssets(game, 1, 1, (CardColor)4);

            var card = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            game.ZoneManager.MoveToHand(card, 1);
            game.TurnManager.PriorityPlayerId = 1;
            int handBefore = game.TurnManager.GetHandOf(1).Count;
            int deckBefore = game.TurnManager.GetDeckOf(1).Count;

            var inspected = new List<CardInstance>();
            System.Action<CardInstance, int> handler = (c, by) => inspected.Add(c);
            UiNotificationBus.Instance.CardInspected += handler;
            try
            {
                var empty = new List<TargetChoice> { TargetChoice.Empty, TargetChoice.Empty };
                Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, empty), "没有任何暗藏者也可打出（仅抓牌）");
                ResolveStackOnce(game);

                Assert.AreEqual(0, inspected.Count, "未检视任何卡");
                Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "打出 1 张 + 抓 1 张 → 手牌数不变");
                Assert.AreEqual(deckBefore - 1, game.TurnManager.GetDeckOf(1).Count, "牌库 -1（抓到 1 张）");
            }
            finally
            {
                UiNotificationBus.Instance.CardInspected -= handler;
            }
        }

        // ==================== ④ 联机：检视请求只下发给检视者（含卡面引用） ====================

        private sealed class InspectGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                return System.Threading.Tasks.Task.FromResult(new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    Cancelled = false,
                    SelectedUids = new List<string>(req.OptionUids),
                });
            }
        }

        public void TestInspect_联机请求只发给检视者()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var covert = game.CardManager.CreateCardInstance(Probe("TS_ins_on"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            var sorcery = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            var gw = new InspectGateway();
            game.SetInputGateway(gw);

            var e = FindEffect(sorcery.PlayerTemplate, "JC062_1_InspectUpToTwoCoverts");
            EffectEngine.Instance.ResolveEffectAsync(e, sorcery, covert, area).GetAwaiter().GetResult();

            Assert.AreEqual(1, gw.Requests.Count, "应下发一次检视请求");
            var req = gw.Requests[0];
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.InspectCards, req.RequestKind, "请求 Kind＝InspectCards");
            Assert.AreEqual(1, req.PlayerId, "**只发给检视者**（效果来源控制者 P1），对手不会收到");
            Assert.AreEqual(1, req.OptionUids.Count, "携带被检视卡 UID");
            Assert.AreEqual(covert.UID, req.OptionUids[0], "UID 正确");
            Assert.AreEqual(1, req.OptionCardRefs.Count, "携带卡面展示引用（检视者据此看到卡面）");
            Assert.AreEqual(covert.UID, req.OptionCardRefs[0].Uid, "展示引用指向被检视卡");
        }

        /// <summary>协议往返：新 Kind 与回包语义。</summary>
        public void TestInspect_联机协议字段往返()
        {
            var req = new Script.Net.ChoiceRequest
            {
                RequestId = "req_i", PlayerId = 1,
                RequestKind = Script.Net.ChoiceRequest.Kind.InspectCards,
                Prompt = "检视：探针",
            };
            req.OptionUids.Add("inst_x");
            var back = Script.Net.ChoiceSerializer.DeserializeRequest(Script.Net.ChoiceSerializer.Serialize(req));
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.InspectCards, back.RequestKind, "Kind 往返");
            Assert.AreEqual("inst_x", back.OptionUids[0], "OptionUids 往返");
            Assert.AreEqual("检视：探针", back.Prompt, "Prompt 往返");
        }

        // ==================== ⑤ 检视结果窗：合并成一个窗 + 只有一个确认 + 确认后才继续结算 ====================

        /// <summary>
        /// UI 桩：记录"检视结果窗"里列出的卡并**先不确认**（`Confirm()` 才模拟玩家点『确认』）；
        /// 同时给出玩家目标的自动答复（JC057 现身触发在入堆叠前要选目标玩家）。
        /// </summary>
        private sealed class InspectStub : Script.Interfaces.IGameUIHooks
        {
            public int Calls;
            public List<CardInstance> LastCards;
            public string LastTitle;
            public int LastViewer = -1;
            private System.Action _pending;

            public void PromptInspectCards(int viewerPlayerId, string title, List<CardInstance> cards,
                System.Action onConfirmed)
            {
                Calls++;
                LastViewer = viewerPlayerId;
                LastTitle = title;
                LastCards = new List<CardInstance>(cards ?? new List<CardInstance>());
                _pending = onConfirmed;   // 不立即确认：模拟"玩家还没点"
            }

            /// <summary>模拟玩家点『确认』。</summary>
            public void Confirm()
            {
                var cb = _pending;
                _pending = null;
                cb?.Invoke();
            }

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
            /// <summary>JC057 入堆叠前的目标玩家：桩自动取第一个候选。</summary>
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
        }

        /// <summary>
        /// 【2026-09 用户口径·修复】(1) **两个目标合并成同一个窗**（"JC062 的查看结果方式是结算的时候弹出一个
        /// 弹窗，里面列出所有看到的信息"）；(2) **确认后才继续结算**——"抓一张牌"排在确认之后（补问裁定）。
        /// </summary>
        public void TestInspect_JC062合并一个窗且确认后才抓牌()
        {
            var game = NewGame();
            var areas = Areas(game);
            GiveAssets(game, 1, 1, (CardColor)4);   // JC062 费用 1、颜色忠诚 4:1

            var c1 = game.CardManager.CreateCardInstance(Probe("TS_win_c1"), 2);
            game.ZoneManager.MoveToArea(c1, areas[0], 2);
            c1.SetCovert(true);
            var c2 = game.CardManager.CreateCardInstance(Probe("TS_win_c2"), 2);
            game.ZoneManager.MoveToArea(c2, areas[1], 2);
            c2.SetCovert(true);

            var card = game.CardManager.CreateCardInstance(Load("JC062_diaochadangan"), 1);
            game.ZoneManager.MoveToHand(card, 1);
            game.TurnManager.PriorityPlayerId = 1;

            var stub = new InspectStub();
            EffectEngine.Instance.UIHooks = stub;
            try
            {
                int handBefore = game.TurnManager.GetHandOf(1).Count;   // 含这张事务卡
                var choices = new List<TargetChoice> { TargetChoice.Card(c1.UID), TargetChoice.Card(c2.UID) };
                Assert.IsTrue(game.TurnManager.PlayCard(1, card.UID, null, null, choices), "应能打出 JC062");
                ResolveStackOnce(game);

                Assert.AreEqual(1, stub.Calls, "**整批只弹一个窗**（两个目标合并，不再一次目标一个窗）");
                Assert.AreEqual(2, stub.LastCards.Count, "窗里**列出全部**看到的牌（2 张）");
                Assert.IsTrue(stub.LastCards.Exists(x => ReferenceEquals(x, c1))
                              && stub.LastCards.Exists(x => ReferenceEquals(x, c2)), "两张目标都在同一个窗里");
                Assert.AreEqual(1, stub.LastViewer, "只有检视者本人看到（私密）");
                Assert.AreEqual(handBefore - 1, game.TurnManager.GetHandOf(1).Count,
                    "**确认之前**：抓一张牌尚未结算（只打出 1 张）");

                stub.Confirm();   // 玩家点『确认』（唯一的按钮）

                Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count,
                    "**确认之后**：抓一张牌才结算（打出 1 张 + 抓 1 张 → 手牌数不变）");
                Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡进墓地");
                Assert.IsTrue(c1.IsCovert && c2.IsCovert, "检视不翻面");
            }
            finally
            {
                EffectEngine.Instance.UIHooks = null;
            }
        }

        /// <summary>
        /// 【2026-09 用户口径·修复】JC057 的查看方式与 JC062 一致：**一个窗列出全部**看到的信息
        /// （＝目标玩家的**全部手牌**）、框里只有一个『确认』。
        /// </summary>
        public void TestInspect_JC057一个窗列出全部手牌()
        {
            var game = NewGame();
            string area = Areas(game)[0];

            var monitor = game.CardManager.CreateCardInstance(Load("JC057_jiankongyuan"), 1);
            game.ZoneManager.MoveToArea(monitor, area, 1);
            monitor.SetCovert(true);                       // 从暗藏者现身 → 现身触发
            game.TurnManager.SetTriggerView(new PushAllTriggerView());

            int hand2 = game.TurnManager.GetHandOf(2).Count;
            Assert.IsTrue(hand2 >= 2, $"前置：对手手牌 ≥2（实际 {hand2}）");

            var stub = new InspectStub();
            EffectEngine.Instance.UIHooks = stub;
            try
            {
                game.CardManager.RevealCard(monitor);          // 现身 → 现身触发（选目标玩家 → 检视其手牌）
                ResolveStackOnce(game);

                Assert.AreEqual(1, stub.Calls, "只弹一个窗");
                Assert.AreEqual(hand2, stub.LastCards.Count, "**一个窗列出目标玩家的全部手牌**（与 JC062 同款）");
                Assert.AreEqual(1, stub.LastViewer, "只有检视者本人看到");

                stub.Confirm();   // 收尾（唯一的按钮），释放决策持锁
            }
            finally
            {
                EffectEngine.Instance.UIHooks = null;
            }
        }

        /// <summary>触发视图桩：排序窗口**全部入堆叠**（JC057 现身触发需要真的入堆叠才会结算）。</summary>
        private sealed class PushAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }

            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<StackEffectInstance> pushed) { }
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }
    }
}
