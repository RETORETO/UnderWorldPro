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
    /// 【P4·JC051】现身触发的检索『结附』去向（【A53·2026-09 用户拍板修订】）：
    /// 宿主**候选**由被检索卡自身的 TargetRules 决定（与"该牌从手牌打出时能否以它为目标"同一实现），
    /// 如检索到 JC012『结附于本方秘社』→ 候选为本方秘社；**候选 ≥2 时由玩家选**
    /// （单机 `PromptPickCard`／联机 `SelectTarget`；仅 1 个候选直接结附；无 UI 确定性兜底取第一个）；
    /// **无合法宿主 → 按"无合法目标"处理**（不结附、牌回拥有者牌库，随后照常洗牌）。
    /// </summary>
    public class JC051Tests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 13 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        /// <summary>把一张（已加载模板的）卡放进玩家1 的牌库顶。</summary>
        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            var deck = game.DeckManager.GetDeckPlayer1();
            deck.Insert(0, card);
            return card;
        }

        private static CardInstance CouncilOf(GameInitializer game, int playerId)
        {
            var councils = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, playerId);
            return (councils != null && councils.Count > 0) ? councils[0] : null;
        }

        // ==================== ① 结构 ====================

        public void TestJC051_结构_现身触发检索结附()
        {
            var tmpl = Load("JC051_nuoennagesita");
            Assert.IsNotNull(tmpl, "JC051 模板应可加载");

            EffectData e = null;
            foreach (var x in tmpl.EffectDataList)
                if (x != null && x.EffectId == "JC051_1_SearchEmblemAttach") e = x;
            Assert.IsNotNull(e, "JC051 应有『寻找徽章附属牌』效果");
            Assert.AreEqual(EffectTiming.UncoverTrigger, e.Timing, "为现身触发");
            Assert.AreEqual(1, e.Actions.Count, "单动作");
            Assert.AreEqual(ActionVerb.Search, e.Actions[0].Verb, "动作为检索");
            Assert.AreEqual("子类别:徽章", e.Actions[0].Args[0], "筛选=徽章附属");
            Assert.AreEqual("结附", e.Actions[0].Args[1], "去向=结附");
            Assert.AreEqual(TargetKind.None, e.Target.Kind, "检索不取目标（宿主由被检索卡自身规则决定）");
        }

        // ==================== ② 按被检索卡自身 TargetRules 选宿主 ====================

        /// <summary>检索到 JC012（徽章，结附于本方秘社）→ 结附**本方秘社**（而非地区/对手秘社）。</summary>
        public void TestJC051_检索到徽记结附本方秘社()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var emblem = PutOnDeckTop(game, Load("JC012_weimushouwanghuiji"), 1);
            var myCouncil = CouncilOf(game, 1);
            var enemyCouncil = CouncilOf(game, 2);
            Assert.IsNotNull(myCouncil, "前置：玩家1 应有秘社");
            Assert.IsNotNull(enemyCouncil, "前置：玩家2 应有秘社");

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(emblem.IsAttached, "检索到的徽记应被结附");
            Assert.IsTrue(ReferenceEquals(myCouncil, emblem.AttachedTo),
                "宿主应为**本方秘社**（JC012 自身 TargetRules=Friendly+Council）");
            Assert.AreEqual(CardZone.AttachedZone, emblem.CurrentZone, "结附后进入附属区");
            Assert.IsFalse(ReferenceEquals(emblem.AttachedTo, enemyCouncil), "不应结附到对手秘社");
        }

        /// <summary>检索到"结附于**敌方**秘社"的徽章 → 宿主应是对手秘社（宿主完全由被检索卡自身规则决定）。</summary>
        public void TestJC051_宿主由被检索卡自身规则决定_敌方秘社()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var enemyEmblem = PutOnDeckTop(game, EmblemProbe("T_jc051_enemy_emblem", TargetKind.Council, TargetRestriction.Enemy), 1);
            var myCouncil = CouncilOf(game, 1);
            var enemyCouncil = CouncilOf(game, 2);

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(ReferenceEquals(enemyCouncil, enemyEmblem.AttachedTo),
                "规则要求『敌方秘社』→ 宿主应为对手秘社");
            Assert.IsFalse(ReferenceEquals(enemyEmblem.AttachedTo, myCouncil), "不应结附到本方秘社（证明不是简单取第一个宿主）");
        }

        /// <summary>结附于角色的徽章 → 宿主为该角色（卡类目标同样走被检索卡自身规则）。</summary>
        public void TestJC051_检索到结附角色的徽章()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var myChar = game.CardManager.CreateCardInstance(Probe("T_jc051_host_char"), 1);
            game.ZoneManager.MoveToArea(myChar, area, 1);

            // 规则：本方 + 指定卡名（把宿主唯一钉在 myChar 上；且排除同为"本方角色"的 JC051 自己）
            var emblemTmpl = EmblemProbeWith("T_jc051_char_emblem", TargetKind.Character, "探针_T_jc051_host_char",
                TargetRestriction.Friendly, TargetRestriction.CardName);
            var emblem = PutOnDeckTop(game, emblemTmpl, 1);

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(ReferenceEquals(myChar, emblem.AttachedTo), "『结附于本方指定角色』→ 宿主应为该角色");
            Assert.AreEqual(CardZone.AttachedZone, emblem.CurrentZone, "进入附属区");
        }

        // ==================== ③ 无合法宿主 → 按"无合法目标"处理 ====================

        public void TestJC051_无合法宿主时按无合法目标处理()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            // 徽章要求结附于"敌方秘社上具有不存在类别的秘社"→ 场上无任何合法宿主
            var orphan = PutOnDeckTop(game, EmblemProbe("T_jc051_orphan",
                TargetKind.Council, TargetRestriction.StaticSubCategory, "不存在的类别"), 1);
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsFalse(orphan.IsAttached, "无合法宿主 → 不结附");
            Assert.IsNull(orphan.AttachedTo, "不应绑定任何宿主");
            Assert.AreEqual(CardZone.Deck, orphan.CurrentZone, "牌放回拥有者牌库（按『无合法目标』处理）");
            Assert.AreEqual(deckBefore, game.DeckManager.GetPlayer1DeckCount(),
                "检索把牌取出后无合法宿主 → 必须完整放回牌库（牌库不因此少牌）");
        }

        /// <summary>对照：有合法宿主时牌**不进**牌库（正常结附），证明上面的"回牌库"只发生在无合法宿主时。</summary>
        public void TestJC051_有合法宿主时不回牌库()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);
            var emblem = PutOnDeckTop(game, Load("JC053_mingzhongjiaopaihuiji"), 1);
            int deckBefore = game.DeckManager.GetPlayer1DeckCount();

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffect(e, jc051, null);

            Assert.IsTrue(emblem.IsAttached, "有合法宿主（本方秘社）→ 正常结附");
            Assert.AreEqual(deckBefore - 1, game.DeckManager.GetPlayer1DeckCount(), "结附成功后牌不再回牌库");
        }

        // ==================== ③b【A53】多合法宿主 → 由玩家选（单机开窗 / 联机 SelectTarget） ====================

        /// <summary>单机：宿主候选多个 → 弹窗让玩家选；玩家选第 2 个候选 → 结附该候选（而非引擎默认的第一个）。</summary>
        public void TestJC051_单机_多宿主时由玩家选择()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            // 两个本方角色（结附于"本方角色"的徽章 → 候选 ≥2，环境无关：不依赖秘社数量）
            var host1 = game.CardManager.CreateCardInstance(Probe("T_jc051_mh1"), 1);
            game.ZoneManager.MoveToArea(host1, area, 1);
            var host2 = game.CardManager.CreateCardInstance(Probe("T_jc051_mh2"), 1);
            game.ZoneManager.MoveToArea(host2, area, 1);
            var emblem = PutOnDeckTop(game, EmblemProbe("T_jc051_multi", TargetKind.Character, TargetRestriction.Friendly), 1);

            // 第 1 次询问＝检索取牌（选到徽章）；第 2 次询问＝选结附宿主（选第 2 个候选）
            var stub = new PickStub
            {
                Choose = (call, cands) => call == 1 ? cands.Find(c => c == emblem) : cands[1],
            };
            EffectEngine.Instance.UIHooks = stub;
            try
            {
                var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
                EffectEngine.Instance.ResolveEffect(e, jc051, null);
            }
            finally { EffectEngine.Instance.UIHooks = null; }

            Assert.AreEqual(2, stub.CandidateLists.Count, "应有两次询问：检索取牌 + 选择结附目标");
            Assert.IsFalse(stub.AllowCancels[0], "检索取牌为强制（A53：不再允许放弃）");
            Assert.IsFalse(stub.AllowCancels[1], "选宿主不可取消（卡文『结附于合理的目标』为强制）");
            Assert.IsTrue(stub.CandidateLists[1].Count >= 2, "宿主候选应有多个（本例＝本方角色）");
            Assert.IsTrue(ReferenceEquals(stub.CandidateLists[1][1], emblem.AttachedTo),
                "宿主应为玩家所选的**第 2 个候选**——证明不再是引擎代选第一个");
            Assert.IsFalse(ReferenceEquals(stub.CandidateLists[1][0], emblem.AttachedTo),
                "不应结附到候选中的第一个（引擎旧口径）");
        }

        /// <summary>单机：宿主候选只有 1 个 → **不弹窗**（无可决策空间），直接结附那一个。</summary>
        public void TestJC051_单机_唯一宿主时不弹窗()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            // 规则：本方 + 指定卡名 → 合法宿主被唯一钉在该角色上（排除自身与其它角色）
            var myChar = game.CardManager.CreateCardInstance(Probe("T_jc051_only_host"), 1);
            game.ZoneManager.MoveToArea(myChar, area, 1);
            var emblem = PutOnDeckTop(game, EmblemProbeWith("T_jc051_solo_emblem", TargetKind.Character,
                "探针_T_jc051_only_host", TargetRestriction.Friendly, TargetRestriction.CardName), 1);

            var stub = new PickStub { Choose = (call, cands) => cands.Find(c => c == emblem) };
            EffectEngine.Instance.UIHooks = stub;
            try
            {
                var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
                EffectEngine.Instance.ResolveEffect(e, jc051, null);
            }
            finally { EffectEngine.Instance.UIHooks = null; }

            Assert.AreEqual(1, stub.CandidateLists.Count, "只应有 1 次询问（检索取牌）；唯一宿主不弹窗");
            Assert.IsTrue(ReferenceEquals(myChar, emblem.AttachedTo), "唯一合法宿主 → 直接结附该角色");
        }

        /// <summary>联机：宿主候选多个 → 经网关 `SelectTarget` 让检索者选（候选＝合法宿主，强制）；客户端选第 2 个。</summary>
        public void TestJC051_联机_多宿主时经网关选择()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var host1 = game.CardManager.CreateCardInstance(Probe("T_jc051_nh1"), 1);
            game.ZoneManager.MoveToArea(host1, area, 1);
            var host2 = game.CardManager.CreateCardInstance(Probe("T_jc051_nh2"), 1);
            game.ZoneManager.MoveToArea(host2, area, 1);
            var emblem = PutOnDeckTop(game, EmblemProbe("T_jc051_net_multi", TargetKind.Character, TargetRestriction.Friendly), 1);

            string chosenHost = null;
            var gw = new TwoStepGateway
            {
                Pick = req =>
                {
                    if (req.RequestKind == ChoiceRequest.Kind.SelectDeckCard) return emblem.UID;
                    chosenHost = req.OptionUids[1];   // 客户端选第 2 个候选
                    return chosenHost;
                },
            };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffectAsync(e, jc051, null).Wait(5000);

            Assert.AreEqual(2, gw.Requests.Count, "应有两次网关请求：选牌库牌 + 选结附宿主");
            Assert.AreEqual(ChoiceRequest.Kind.SelectDeckCard, gw.Requests[0].RequestKind, "第 1 次＝检索取牌");
            Assert.IsTrue(gw.Requests[0].ForceChoice, "检索为强制（A53）：客户端不提供取消");
            Assert.AreEqual(ChoiceRequest.Kind.SelectTarget, gw.Requests[1].RequestKind, "第 2 次＝选结附宿主");
            Assert.IsTrue(gw.Requests[1].ForceChoice, "选宿主为强制");
            Assert.IsTrue(gw.Requests[1].OptionUids.Count >= 2, "宿主候选＝多个本方角色");
            Assert.AreEqual(gw.Requests[1].OptionUids.Count, gw.Requests[1].OptionLabels.Count, "候选显示名随请求下发");
            Assert.IsNotNull(chosenHost, "客户端确实回过一次宿主选择");
            Assert.AreEqual(chosenHost, emblem.AttachedTo?.UID, "结附到客户端回包选中的第 2 个候选");
            Assert.IsFalse(gw.Requests[1].OptionUids[0] == emblem.AttachedTo?.UID, "不应结附到候选第一个");
        }

        /// <summary>联机：宿主回包非法 → **确定性兜底取第一个合法宿主**（fail-safe：强制结附不可被规避）。</summary>
        public void TestJC051_联机_宿主回包非法时兜底第一个()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc051 = game.CardManager.CreateCardInstance(Load("JC051_nuoennagesita"), 1);
            game.ZoneManager.MoveToArea(jc051, area, 1);

            var host1 = game.CardManager.CreateCardInstance(Probe("T_jc051_bh1"), 1);
            game.ZoneManager.MoveToArea(host1, area, 1);
            var host2 = game.CardManager.CreateCardInstance(Probe("T_jc051_bh2"), 1);
            game.ZoneManager.MoveToArea(host2, area, 1);
            var emblem = PutOnDeckTop(game, EmblemProbe("T_jc051_net_bad", TargetKind.Character, TargetRestriction.Friendly), 1);

            var gw = new TwoStepGateway
            {
                Pick = req => req.RequestKind == ChoiceRequest.Kind.SelectDeckCard ? emblem.UID : "不存在的宿主UID",
            };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var e = FindEffect(jc051.PlayerTemplate, "JC051_1_SearchEmblemAttach");
            EffectEngine.Instance.ResolveEffectAsync(e, jc051, null).Wait(5000);

            Assert.AreEqual(2, gw.Requests.Count, "仍应发出选宿主请求");
            Assert.IsNotNull(emblem.AttachedTo, "回包非法 → 兜底结附仍在，强制结附不会被规避");
            Assert.AreEqual(gw.Requests[1].OptionUids[0], emblem.AttachedTo.UID,
                "兜底取的是**第一个合法候选**");
        }

        // ==================== 辅助 ====================

        /// <summary>选卡窗口桩：记录每次询问的候选与"是否可取消"，并按 `Choose` 决定选谁（缺省取第一个）。</summary>
        private sealed class PickStub : Script.Interfaces.IGameUIHooks
        {
            public readonly List<List<CardInstance>> CandidateLists = new List<List<CardInstance>>();
            public readonly List<bool> AllowCancels = new List<bool>();
            public System.Func<int, List<CardInstance>, CardInstance> Choose;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                CandidateLists.Add(candidates ?? new List<CardInstance>());
                AllowCancels.Add(allowCancel);
                int call = CandidateLists.Count;
                CardInstance picked = Choose != null
                    ? Choose(call, candidates)
                    : (candidates != null && candidates.Count > 0 ? candidates[0] : null);
                onPicked?.Invoke(picked);
            }
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen) { }
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen) { }
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen) { }
        }

        /// <summary>按预设规则回答的网关（可区分"选牌库牌"与"选宿主"两段请求）。</summary>
        private sealed class TwoStepGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public System.Func<ChoiceRequest, string> Pick;

            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                string uid = Pick?.Invoke(req);
                if (string.IsNullOrEmpty(uid)) uid = req?.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null;
                return Task.FromResult(new ChoiceResult { RequestId = req?.RequestId, SelectedUid = uid, Cancelled = false });
            }
        }

        /// <summary>徽章附属探针（可带多条目标规则——多条之间为 **OR**，用于构造"多个合法宿主"）。</summary>
        private static PlayerCardData EmblemProbeWithRules(string uid, params TargetRule[] rules)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Attachment,
            };
            tmpl.SubCategories.Add("徽章");
            var arr = new Array<TargetRule>();
            if (rules != null)
                foreach (var r in rules) if (r != null) arr.Add(r);
            tmpl.TargetRules = arr;
            tmpl._effectsInjected = true;
            return tmpl;
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

        /// <summary>徽章附属探针：自带一条目标规则（决定"合理的目标"是谁）。</summary>
        private static PlayerCardData EmblemProbe(string uid, TargetKind kind, TargetRestriction r, string param = "")
            => EmblemProbeWith(uid, kind, param, r);

        /// <summary>徽章附属探针（可带多条限制，全部满足才命中 = AND）。</summary>
        private static PlayerCardData EmblemProbeWith(string uid, TargetKind kind, string param,
            params TargetRestriction[] restrictions)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Attachment,
            };
            tmpl.SubCategories.Add("徽章");
            var rule = new TargetRule { Kind = kind, Param = param };
            if (restrictions != null)
                foreach (var r in restrictions) rule.Restrictions.Add(r);
            tmpl.TargetRules = new Godot.Collections.Array<TargetRule> { rule };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }
    }
}
