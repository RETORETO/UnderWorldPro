using System.Collections.Generic;
using System.Threading.Tasks;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Interfaces;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【P4·预测X】回归（用户 2026-09 规则文本）：
    /// **检视你牌库顶的 X 张牌，将其中任意数量的牌以任意顺序置于你的牌库顶，将其余的牌以任意顺序置于你的牌库底。**
    ///  · 数据层复用调查对抗的取/放通道（`TakeTopForInvestigation` / `ReturnAfterInvestigation`）；
    ///  · 分配窗口复用调查对抗窗口（单机 `PromptPrediction` / 联机 `Kind.Prediction` + `OptionCardRefs`）；
    ///  · 回包语义与调查对抗一致：`SelectedUids` = 置顶顺序，未列出的按原序置底；
    ///  · 无 UI/网关时确定性兜底 = 原序全部留在牌库顶（牌库不变）；**预测不抓牌**。
    /// </summary>
    public class P4PredictionTests : TestRunner
    {
        /// <summary>记录请求并按脚本回答的网关（Prediction = 按脚本回置顶列表）。</summary>
        private sealed class PredictGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public List<string> TopUids = new List<string>();
            public bool Cancel;

            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                if (req.RequestKind == ChoiceRequest.Kind.Prediction)
                {
                    if (Cancel) return Task.FromResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = true });
                    var r = new ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    r.SelectedUids.AddRange(TopUids);
                    return Task.FromResult(r);
                }
                if (req.RequestKind == ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    var r0 = new ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r0.SelectedIndices.Add(i);
                    return Task.FromResult(r0);
                }
                return Task.FromResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = false });
            }

            public ChoiceRequest PredictionRequest => Requests.Find(r => r.RequestKind == ChoiceRequest.Kind.Prediction);
        }

        /// <summary>单机 UI 桩：记录预测分配请求并按脚本回包。</summary>
        private sealed class PredictPickerStub : IGameUIHooks
        {
            public int Calls;
            public List<CardInstance> LastCards = new List<CardInstance>();
            public string LastTitleCount;
            public System.Func<List<CardInstance>, (List<CardInstance> top, List<CardInstance> bottom)> Split;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked) { }
            public void PromptCurbMarks(int curbedPlayerId, int remaining, List<CurbMarkOption> options,
                System.Action<string> onPickedAreaUid) { }

            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed)
            {
                Calls++;
                LastCards = new List<CardInstance>(cards);
                LastTitleCount = count.ToString();
                var (top, bottom) = Split != null ? Split(cards) : (null, cards);
                onConfirmed?.Invoke(top, bottom);
            }

            /// <summary>【K15】本套件不涉及多目标：视为玩家取消。</summary>
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed)
                => onConfirmed?.Invoke(null);

            /// <summary>【K16】本套件不涉及模式分支：视为取消。</summary>
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, Script.Data.Effects.EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            /// <summary>【K11】本套件不涉及数值输入：视为取消。</summary>
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue, string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);
        }

        // ==================== 装配辅助 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 29 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        /// <summary>把玩家1 的牌库替换成一份已知顺序的探针牌堆（索引 0 = 牌库顶）。</summary>
        private static List<CardInstance> KnownDeck(GameInitializer game, int n = 5)
        {
            var deck = new List<CardInstance>();
            for (int i = 0; i < n; i++)
            {
                var inst = game.CardManager.CreateCardInstance(Probe($"T_pre_deck{i + 1}"), 1);
                inst.CurrentZone = CardZone.Deck;
                deck.Add(inst);
            }
            game.DeckManager.SetDeckPlayer1(deck);
            return deck;
        }

        private static PlayerCardData Probe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static string DeckSignature(GameInitializer game)
            => string.Join(",", game.DeckManager.GetPlayer1DeckData().ConvertAll(c => c.UID));

        private static EffectData PredictEffect(string id, string x)
            => new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Predict, Args = new Array<string> { x } }
                },
            };

        private static CardInstance Predictor(GameInitializer game, int controllerId = 1)
            => game.CardManager.CreateCardInstance(Probe($"T_pre_src_{controllerId}"), controllerId);

        // ==================== ① 无 UI / 无网关：确定性兜底 ====================

        public void TestP4_预测_无UI兜底时牌库原序不变()
        {
            var game = NewGame();
            var deck = KnownDeck(game, 5);
            string before = DeckSignature(game);
            int handBefore = game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count;

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_fallback", "3"), Predictor(game), null);

            Assert.AreEqual(before, DeckSignature(game), "无 UI/网关：原序全部留在牌库顶 → 牌库完全不变");
            Assert.AreEqual(handBefore, game.ZoneManager.GetZoneCards(CardZone.Hand, 1).Count, "预测**不抓牌**（手牌不变）");
            Assert.AreEqual(CardZone.Deck, deck[0].CurrentZone, "取出的牌状态仍是牌库（临时态）");
        }

        // ==================== ② 联机：按回包置顶/置底 ====================

        public void TestP4_预测_联机按回包重排牌库顶底()
        {
            var game = NewGame();
            var gateway = new PredictGateway();
            game.SetInputGateway(gateway);
            var deck = KnownDeck(game, 5);
            var c1 = deck[0]; var c2 = deck[1]; var c3 = deck[2]; var c4 = deck[3]; var c5 = deck[4];

            // 被检视者（玩家1）选择：把第 3 张、第 1 张依次放到牌库顶（第 2 张留在牌库底）
            gateway.TopUids = new List<string> { c3.UID, c1.UID };

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_gw", "3"), Predictor(game), null);

            var req = gateway.PredictionRequest;
            Assert.IsNotNull(req, "联机应下发 Prediction 决策请求");
            Assert.AreEqual(1, req.PlayerId, "请求发给预测者（来源控制者）");
            Assert.AreEqual(3, req.Amount, "Amount = 检视张数 X");
            Assert.AreEqual(3, req.OptionUids.Count, "候选 = 取出的 X 张");
            Assert.AreEqual(3, req.OptionCardRefs.Count, "必须携带展示引用（OptionCardRefs），否则客户端无法显示卡面");
            Assert.IsTrue(req.ForceChoice, "预测分配为强制窗口（必须确认分配）");

            var after = game.DeckManager.GetPlayer1DeckData();
            Assert.AreEqual(c3.UID, after[0].UID, "牌库顶第 1 张 = 回包顺序第 1 张");
            Assert.AreEqual(c1.UID, after[1].UID, "牌库顶第 2 张 = 回包顺序第 2 张");
            Assert.AreEqual(c4.UID, after[2].UID, "未参与检视的牌保持原相对顺序");
            Assert.AreEqual(c5.UID, after[3].UID, "未参与检视的牌保持原相对顺序");
            Assert.AreEqual(c2.UID, after[4].UID, "未列入回包的牌置于牌库底");
            Assert.AreEqual(5, after.Count, "牌数不变（不得凭空少牌/多牌）");
        }

        /// <summary>回包里的非法 UID 被忽略；取消回包 → 全部按原序留在牌库顶（兜底，不丢牌）。</summary>
        public void TestP4_预测_回包非法被忽略与取消兜底()
        {
            var game = NewGame();
            var gateway = new PredictGateway();
            game.SetInputGateway(gateway);
            var deck = KnownDeck(game, 4);
            var c1 = deck[0]; var c2 = deck[1]; var c3 = deck[2];

            gateway.TopUids = new List<string> { "no_such_card", c2.UID };
            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_bad", "3"), Predictor(game), null);

            var after = game.DeckManager.GetPlayer1DeckData();
            Assert.AreEqual(c2.UID, after[0].UID, "非法 UID 被忽略，合法的那张仍按回包置顶");
            Assert.AreEqual(c1.UID, after[2].UID, "未列入回包的牌按原序置于牌库底");
            Assert.AreEqual(c3.UID, after[3].UID, "未列入回包的牌按原序置于牌库底");
            Assert.AreEqual(4, after.Count, "牌数不变");

            // 取消回包：全部回到原序（牌库不变）
            var game2 = NewGame();
            var gateway2 = new PredictGateway { Cancel = true };
            game2.SetInputGateway(gateway2);
            var deck2 = KnownDeck(game2, 4);
            string before2 = DeckSignature(game2);

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_cancel", "3"), Predictor(game2), null);

            Assert.AreEqual(before2, DeckSignature(game2), "取消回包 → 兜底为原序全部置顶（牌库不变）");
        }

        // ==================== ③ 单机：复用调查对抗分配窗口 ====================

        public void TestP4_预测_单机窗口分配顶底()
        {
            var game = NewGame();
            var picker = new PredictPickerStub();
            EffectEngine.Instance.UIHooks = picker;
            var deck = KnownDeck(game, 5);
            var c1 = deck[0]; var c2 = deck[1]; var c3 = deck[2]; var c5 = deck[4];

            // 窗口分配：把第 2 张放牌库顶；第 1、3 张放牌库底
            picker.Split = cards => (new List<CardInstance> { cards[1] },
                                     new List<CardInstance> { cards[0], cards[2] });

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_ui", "3"), Predictor(game), null);

            Assert.AreEqual(1, picker.Calls, "应打开一次预测分配窗口");
            Assert.AreEqual(3, picker.LastCards.Count, "窗口拿到牌库顶 3 张");
            Assert.AreEqual("3", picker.LastTitleCount, "窗口张数 = X = 3");

            var after = game.DeckManager.GetPlayer1DeckData();
            Assert.AreEqual(c2.UID, after[0].UID, "分配的置顶牌排在最前");
            Assert.AreEqual(c5.UID, after[2].UID, "未参与检视的牌随其后（保持原相对顺序）");
            Assert.AreEqual(c1.UID, after[3].UID, "置底的牌按窗口内左右顺序排在库底");
            Assert.AreEqual(c3.UID, after[4].UID, "置底的牌按窗口内左右顺序排在库底");
            Assert.AreEqual(5, after.Count, "牌数不变");
            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>窗口漏放（少回传一张）时，漏掉的牌按原序补进牌库底（绝不丢牌）。</summary>
        public void TestP4_预测_窗口漏卡不丢牌()
        {
            var game = NewGame();
            var picker = new PredictPickerStub();
            EffectEngine.Instance.UIHooks = picker;
            var deck = KnownDeck(game, 4);
            var c2 = deck[1];

            picker.Split = cards => (new List<CardInstance> { cards[1] }, new List<CardInstance>());   // 只回一张置顶

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_leak", "3"), Predictor(game), null);

            var after = game.DeckManager.GetPlayer1DeckData();
            Assert.AreEqual(4, after.Count, "漏放的牌被补回牌库（牌数不变）");
            Assert.AreEqual(c2.UID, after[0].UID, "置顶的牌在最前");
            EffectEngine.Instance.UIHooks = null;
        }

        // ==================== ④ 边界 ====================

        public void TestP4_预测_牌库张数少于X时取现有张数()
        {
            var game = NewGame();
            var gateway = new PredictGateway();
            game.SetInputGateway(gateway);
            KnownDeck(game, 2);

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_small", "3"), Predictor(game), null);

            var req = gateway.PredictionRequest;
            Assert.IsNotNull(req, "牌库有牌 → 仍应询问（张数取现有）");
            Assert.AreEqual(2, req.Amount, "牌库只有 2 张 → 实际检视 2 张");
            Assert.AreEqual(2, game.DeckManager.GetPlayer1DeckData().Count, "牌数不变");
        }

        public void TestP4_预测_空牌库时无操作()
        {
            var game = NewGame();
            var gateway = new PredictGateway();
            game.SetInputGateway(gateway);
            game.DeckManager.SetDeckPlayer1(new List<CardInstance>());

            EffectEngine.Instance.ResolveEffect(PredictEffect("T_pre_empty", "3"), Predictor(game), null);

            Assert.IsNull(gateway.PredictionRequest, "空牌库 → 不询问");
            Assert.AreEqual(0, game.DeckManager.GetPlayer1DeckData().Count, "牌库仍为空");
        }

        // ==================== ⑤ JC104 结构 ====================

        public void TestP4_预测_JC104占卜师结构()
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + "JC104_zhanbushi.tres");
            Assert.IsNotNull(tmpl, "JC104 模板应可加载");
            CardEffectFactory.EnsureEffects(tmpl);

            EffectData e = null;
            foreach (var x in tmpl.EffectDataList)
                if (x != null && x.EffectId == "JC104_1_Predict3") e = x;
            Assert.IsNotNull(e, "JC104 应有 Predict3 效果");
            Assert.AreEqual(EffectTiming.EnterTrigger, e.Timing, "预测3 为进场触发");
            Assert.IsTrue(e.Forced, "卡文『进场触发：预测3』无『可以』→ 强制");
            Assert.AreEqual(1, e.Actions.Count, "单动作");
            Assert.AreEqual(ActionVerb.Predict, e.Actions[0].Verb, "动作为预测");
            Assert.AreEqual("3", e.Actions[0].Args[0], "X=3");
            Assert.AreEqual(TargetKind.None, e.Target.Kind, "预测不取目标");
        }

        private const string Dir = "res://Cards/Data/PlayerCards/JC/";
    }
}
