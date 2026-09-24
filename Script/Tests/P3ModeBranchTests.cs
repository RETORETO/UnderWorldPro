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
    /// 【K16·模式分支】按用户 2026-09 口径实现并锁定（与 JC063 的 FAQ 一致）：
    ///  · 卡文"选择一项：·… ·…" → `EffectData.Modes`；**模式在打出时选定、付费之前**，
    ///    选定后写入 `StackEntry.ModeIndex`，**入堆叠即不可更改**；
    ///  · 结算**只执行所选模式**的目标与动作（另一模式完全不参与，含 D16 目标复检）；
    ///  · 未提供/非法模式 → 打出被拒（**不扣费、不入堆叠**），绝不静默代选；
    ///  · 单机：`IGameUIHooks.PromptEffectMode`（选完再取该模式目标）；联机：`ChoiceRequest.Kind.SelectEffectMode`。
    /// 覆盖卡：**JC063 警车追捕**（选项1 潜伏目标角色 / 选项2 暗藏者回手）。
    /// </summary>
    public class P3ModeBranchTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 101 };
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

        /// <summary>JC063 费用 2、颜色忠诚要求 颜色4:1。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_mb_asset_{playerId}_{i}", CardCategory.Attachment);
                tmpl.Colors.Add((CardColor)4);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData Jc063Effect(PlayerCardData tmpl)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == "JC063_1_TapOrBounceByMode") return e;
            return null;
        }

        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
            {
                int p = game.TurnManager.PriorityPlayerId;
                game.TurnManager.Pass(p);
            }
        }

        /// <summary>单机模式选择桩：按下标回包（-1＝取消）。</summary>
        private sealed class ModeStub : Script.Interfaces.IGameUIHooks
        {
            public int Answer = 0;
            public int Calls;
            public int LastModeCount;
            public string LastSourceName;

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

            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
            {
                Calls++;
                LastModeCount = data?.Modes?.Count ?? 0;
                LastSourceName = source?.PlayerTemplate?.CardName;
                onChosen?.Invoke(Answer);
            }

            /// <summary>【K11】本套件不涉及数值输入：视为取消。</summary>
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue, string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

        }

        // ==================== ① 结构 ====================

        public void TestK16_JC063结构()
        {
            var tmpl = Load("JC063_jingchezhuibu");
            Assert.IsNotNull(tmpl, "JC063 模板应可加载");
            var e = Jc063Effect(tmpl);
            Assert.IsNotNull(e, "JC063 应有模式分支效果");
            Assert.AreEqual(2, e.Modes.Count, "两个模式：潜伏目标角色 / 暗藏者回手");

            Assert.AreEqual(TargetKind.Character, e.ModeAt(0).Target.Kind, "模式1 目标＝角色");
            Assert.AreEqual(1, e.ModeAt(0).Actions.Count, "模式1 单动作");
            Assert.AreEqual(ActionVerb.Latent, e.ModeAt(0).Actions[0].Verb, "模式1 动作＝潜伏（翻面）");

            Assert.AreEqual(TargetKind.Covert, e.ModeAt(1).Target.Kind, "模式2 目标＝暗藏者");
            Assert.AreEqual(ActionVerb.Bounce, e.ModeAt(1).Actions[0].Verb, "模式2 动作＝移回拥有者手牌");

            // 卡级打出规则＝两模式目标大类的 OR（供打出阶段判定"需要目标"）
            Assert.IsTrue(tmpl.GetRequiresTarget(), "需要目标");
            var allowed = tmpl.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Character), "打出允许目标含角色");
            Assert.IsTrue(allowed.Contains(TargetKind.Covert), "打出允许目标含暗藏者");
        }

        // ==================== ② 未选模式/非法模式 → 拒绝打出且不扣费 ====================

        public void TestK16_未选模式则拒绝打出()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_mb_v0"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, victim.UID), "未提供模式 → 拒绝打出");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡留手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "未入堆叠");

            Assert.IsFalse(game.TurnManager.PlayCard(1, card.UID, null, victim.UID, null, 5), "模式下标越界 → 拒绝打出");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "仍不扣费");
        }

        // ==================== ③ 单机流程：选模式 → 取该模式目标 → 按所选模式结算 ====================

        /// <summary>选模式1（潜伏）→ 目标角色被翻面为暗藏者。</summary>
        public void TestK16_模式1_潜伏目标角色()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_mb_v1"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var stub = new ModeStub { Answer = 0 };
            EffectEngine.Instance.UIHooks = stub;
            // 选目标：单机走 View 通道；无 View 时用 hook 的选卡窗替代（模式目标为卡，PromptPickCard 可承载）
            game.TurnManager.SetTriggerView(new PickFirstView());

            game.TurnManager.BeginModeCardPlay(1, card.UID);

            Assert.AreEqual(1, stub.Calls, "应弹出一次模式选择");
            Assert.AreEqual(2, stub.LastModeCount, "模式数＝2");
            Assert.AreEqual("警车追捕", stub.LastSourceName, "来源＝警车追捕");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "选完模式+目标后入堆叠");
            Assert.AreEqual(0, game.StackManager.Entries[0].ModeIndex, "条目记录了所选模式（0）");

            ResolveStackOnce(game);
            Assert.IsTrue(victim.IsCovert, "模式1 生效：目标角色被翻面为暗藏者");
            Assert.AreEqual(CardZone.AreaZone, victim.CurrentZone, "翻面不移场（仍在争夺区）");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡结算后进墓地");
        }

        /// <summary>选模式2（暗藏者回手）→ 目标暗藏者回到拥有者手牌。</summary>
        public void TestK16_模式2_暗藏者回手()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_mb_c1"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int handBefore = game.TurnManager.GetHandOf(2).Count;
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            var stub = new ModeStub { Answer = 1 };
            EffectEngine.Instance.UIHooks = stub;
            game.TurnManager.SetTriggerView(new PickFirstView());

            game.TurnManager.BeginModeCardPlay(1, card.UID);

            Assert.AreEqual(1, game.StackManager.Entries.Count, "入堆叠");
            Assert.AreEqual(1, game.StackManager.Entries[0].ModeIndex, "条目记录了所选模式（1）");

            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.Hand, covert.CurrentZone, "模式2 生效：暗藏者回到拥有者手牌");
            Assert.AreEqual(handBefore + 1, game.TurnManager.GetHandOf(2).Count, "对手手牌 +1");
        }

        /// <summary>取消模式选择 → 不打出、不扣费、卡留手牌。</summary>
        public void TestK16_取消模式选择则不打出()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_mb_v2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            EffectEngine.Instance.UIHooks = new ModeStub { Answer = -1 };
            game.TurnManager.SetTriggerView(new PickFirstView());
            game.TurnManager.BeginModeCardPlay(1, card.UID);

            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "取消 → 卡留手牌");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "取消 → 不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "取消 → 不入堆叠");
            Assert.IsFalse(victim.IsCovert, "取消 → 目标未被翻面");
        }

        // ==================== ④ D16：模式2 的目标在堆叠期间翻面 → 整张不结算 ====================

        /// <summary>FAQ：对手响应把暗藏者付费翻面 → 警车追捕因目标不合法而失效（整张不结算，卡进墓地）。</summary>
        public void TestK16_模式2目标翻面则整张失效()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_mb_c2"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;

            EffectEngine.Instance.UIHooks = new ModeStub { Answer = 1 };
            game.TurnManager.SetTriggerView(new PickFirstView());
            game.TurnManager.BeginModeCardPlay(1, card.UID);
            Assert.AreEqual(1, game.StackManager.Entries.Count, "前置：已入堆叠");

            // 堆叠期间目标翻面（版本号变化 → 目标丢失）
            covert.SetCovert(false);
            int handBefore = game.TurnManager.GetHandOf(2).Count;

            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "目标未回手（模式不结算）");
            Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(2).Count, "对手手牌数不变");
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "事务卡仍进墓地");
        }

        // ==================== ⑤ 联机：服务端下发选模式 → 取目标 → 入堆叠 ====================

        private sealed class OnlineStubGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public int ModeAnswer;
            public bool CancelMode;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectEffectMode)
                {
                    var r = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = CancelMode };
                    if (!CancelMode) r.SelectedIndices.Add(ModeAnswer);
                    return System.Threading.Tasks.Task.FromResult(r);
                }
                var rr = new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    SelectedUid = (req.OptionUids != null && req.OptionUids.Count > 0) ? req.OptionUids[0] : null,
                    Cancelled = false,
                };
                return System.Threading.Tasks.Task.FromResult(rr);
            }
        }

        public void TestK16_联机_选模式后入堆叠()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_mb_on1"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new OnlineStubGateway { ModeAnswer = 0 };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayModeCardAsync(1, card.UID).GetAwaiter().GetResult();

            Assert.IsTrue(ok, "联机模式流程应成功");
            Assert.AreEqual(2, gw.Requests.Count, "请求序列＝选模式 + 选目标");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectEffectMode, gw.Requests[0].RequestKind, "第 1 个请求＝选模式");
            Assert.AreEqual(2, gw.Requests[0].OptionLabels.Count, "模式名随请求下发（2 项）");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "入堆叠");
            Assert.AreEqual(0, game.StackManager.Entries[0].ModeIndex, "记录所选模式");
            Assert.AreEqual(manaBefore - 2, game.PlayerManager.GetAvailableMana(1), "正常付费 2");

            ResolveStackOnce(game);
            Assert.IsTrue(victim.IsCovert, "所选模式（潜伏）生效");
        }

        public void TestK16_联机_取消选模式则不打出()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3);
            var victim = game.CardManager.CreateCardInstance(Probe("TS_mb_on2"), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);
            var card = game.CardManager.CreateCardInstance(Load("JC063_jingchezhuibu"), 1);
            game.ZoneManager.MoveToHand(card, 1);

            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.CurrentPhase = global::TurnManager.TurnPhase.ActionFirst;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new OnlineStubGateway { CancelMode = true };
            game.TurnManager.InputGateway = gw;

            bool ok = game.TurnManager.PlayModeCardAsync(1, card.UID).GetAwaiter().GetResult();

            Assert.IsFalse(ok, "取消选模式 → 中止");
            Assert.AreEqual(1, gw.Requests.Count, "只发了选模式请求就中止");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "未扣费");
            Assert.AreEqual(CardZone.Hand, card.CurrentZone, "卡留手牌");
            Assert.IsFalse(victim.IsCovert, "目标未被翻面");
        }

        /// <summary>协议往返：新 Kind 与模式选择回包字段</summary>
        public void TestK16_联机_协议字段往返()
        {
            var req = new Script.Net.ChoiceRequest
            {
                RequestId = "req_m", PlayerId = 1,
                RequestKind = Script.Net.ChoiceRequest.Kind.SelectEffectMode,
                Prompt = "警车追捕：选择一项",
                Amount = 2,
            };
            req.OptionLabels.Add("将目标角色潜伏（翻面为暗藏者）");
            req.OptionLabels.Add("将目标暗藏者移回其拥有者手上");

            var back = Script.Net.ChoiceSerializer.DeserializeRequest(Script.Net.ChoiceSerializer.Serialize(req));
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.SelectEffectMode, back.RequestKind, "Kind 往返");
            Assert.AreEqual(2, back.Amount, "模式数往返");
            Assert.AreEqual("将目标角色潜伏（翻面为暗藏者）", back.OptionLabels[0], "模式名往返");

            var res = new Script.Net.ChoiceResult { RequestId = "req_m" };
            res.SelectedIndices.Add(1);
            var rback = Script.Net.ChoiceSerializer.DeserializeResult(Script.Net.ChoiceSerializer.SerializeResult(res));
            Assert.AreEqual(1, rback.SelectedIndices[0], "所选模式下标往返");
        }

        /// <summary>单机选目标 View 桩：总是选第一个候选。</summary>
        private sealed class PickFirstView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone) { }
            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }
    }
}
