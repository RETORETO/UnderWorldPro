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
