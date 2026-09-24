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
    /// 【K16·模式分支·触发版】**触发类"选择一项"**（用户 2026-09 口径，见清单 §7.4b A5）：
    ///  · 卡文"横置**或**重置"＝`EffectData.Modes` 两个模式；由**发动者**选择，且**入堆叠时选定**
    ///    （与卡牌模式"打出时选定、入堆叠不可更改"、K15"目标地区入堆叠时锁定"同一口径），
    ///    选定后写入 `StackEntry.ModeIndex`；结算**只执行所选模式**；
    ///  · 单机：`IGameUIHooks.PromptEffectMode`（`TurnManager.EnsureTriggerModeThenPush`，
    ///    **先选模式再取目标再入堆叠**）；无 UI（headless/测试）→ **确定性兜底第一个模式**；
    ///  · 联机：`ChoiceRequest.Kind.SelectEffectMode`（`TurnManager.EnsureTriggerModeAsync`，
    ///    复用卡牌模式分支的同一请求类型）；
    ///  · 取消/非法 → 该触发**不入堆叠**（与"无合法目标"同一结果，绝不静默代选）。
    /// 覆盖卡：**JC110 闲人的绿地**（触发："当本卡被封印在一个角色上时，你可以横置或重置该角色"）。
    /// </summary>
    public class P3TriggerModeChoiceTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 110 };
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

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static void ResolveStack(GameInitializer game)
        {
            for (int i = 0; i < 4 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>触发排序桩：全部触发直接入堆叠（模式选择由 IGameUIHooks 负责，见 ModeStub）。</summary>
        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId,
                List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
                => onEnterStack?.Invoke(triggers);

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>单机模式选择桩：按下标回包（-1＝取消）。</summary>
        private sealed class ModeStub : Script.Interfaces.IGameUIHooks
        {
            public int Answer;
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
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
            {
                Calls++;
                LastModeCount = data?.Modes?.Count ?? 0;
                LastSourceName = source?.PlayerTemplate?.CardName;
                onChosen?.Invoke(Answer);
            }
        }

        /// <summary>联机网关桩：`SelectEffectMode` 按 `ModeAnswer` 回包（其余请求取第一个候选项）。</summary>
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
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    // 排序窗：全部按默认顺序入栈（SelectedIndices 空＝按 remaining 顺序）。
                    return System.Threading.Tasks.Task.FromResult(new Script.Net.ChoiceResult { RequestId = req.RequestId });
                }
                var rr = new Script.Net.ChoiceResult
                {
                    RequestId = req.RequestId,
                    SelectedUid = (req.OptionUids != null && req.OptionUids.Count > 0) ? req.OptionUids[0] : null,
                };
                return System.Threading.Tasks.Task.FromResult(rr);
            }
        }

        /// <summary>把 JC110 封印到一张探针角色上（返回宿主）。</summary>
        private static CardInstance SealJc110OntoHost(GameInitializer game, string area, out CardInstance jc110)
        {
            var host = game.CardManager.CreateCardInstance(Probe("TS_tmc_host"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            jc110 = game.CardManager.CreateCardInstance(Load("JC110_xianrendelvdi"), 1);
            game.ZoneManager.MoveToArea(jc110, area, 1);
            return host;
        }

        // ==================== ① 结构 ====================

        public void TestTriggerMode_JC110结构()
        {
            var e = FindEffect(Load("JC110_xianrendelvdi"), "JC110_2_OnSealedTapOrUntapHost");
            Assert.IsNotNull(e, "JC110 应有『被封印时 → 横置或重置』触发效果");
            Assert.AreEqual(EffectTiming.OnSealed, e.Timing, "时机＝被封印");
            Assert.IsFalse(e.Forced, "卡文『你可以』→ 非强制");
            Assert.IsNotNull(e.Modes, "二选一以 Modes 表达");
            Assert.AreEqual(2, e.Modes.Count, "两个模式：横置 / 重置");
            Assert.AreEqual("横置该角色", e.Modes[0].Label, "模式1＝横置");
            Assert.AreEqual("重置该角色", e.Modes[1].Label, "模式2＝重置");
            Assert.AreEqual(ActionVerb.Tap, e.Modes[0].Actions[0].Verb, "模式1 动作＝横置");
            Assert.AreEqual("封印宿主", e.Modes[0].Actions[0].Args[0], "模式1 对象＝封印宿主（该角色＝被封印到的角色）");
            Assert.AreEqual(ActionVerb.Untap, e.Modes[1].Actions[0].Verb, "模式2 动作＝重置");
            Assert.AreEqual("封印宿主", e.Modes[1].Actions[0].Args[0], "模式2 对象＝封印宿主");
        }

        // ==================== ② 无 UI：确定性兜底第一个模式（headless/测试） ====================

        public void TestTriggerMode_无UI兜底取模式1_横置宿主()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            EffectEngine.Instance.UIHooks = null;                     // 无 UI → 兜底
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var host = SealJc110OntoHost(game, area, out var jc110);
            Assert.IsTrue(game.TurnManager.SealCard(jc110, host, false), "前置：封印成功");
            Assert.IsFalse(host.IsTapped, "前置：宿主未横置");

            Assert.AreEqual(1, game.StackManager.Entries.Count, "触发已入堆叠");
            Assert.AreEqual(0, game.StackManager.Entries[0].ModeIndex, "无 UI → 兜底取模式 1（下标 0）");

            ResolveStack(game);
            Assert.IsTrue(host.IsTapped, "模式1（横置该角色）生效");
        }

        // ==================== ③ 单机：玩家选模式2 → 重置宿主 ====================

        public void TestTriggerMode_单机选模式2_重置宿主()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var stub = new ModeStub { Answer = 1 };                   // 选"重置该角色"
            EffectEngine.Instance.UIHooks = stub;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var host = SealJc110OntoHost(game, area, out var jc110);
            host.SetTapped(true);                                     // 前置：宿主已横置（观察"重置"）
            Assert.IsTrue(game.TurnManager.SealCard(jc110, host, false), "前置：封印成功");

            Assert.AreEqual(1, stub.Calls, "模式选择窗口被询问一次");
            Assert.AreEqual(2, stub.LastModeCount, "下发 2 个模式");
            Assert.AreEqual("闲人的绿地", stub.LastSourceName, "询问来源＝JC110");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "触发入堆叠");
            Assert.AreEqual(1, game.StackManager.Entries[0].ModeIndex, "记录所选模式（1＝重置）");

            ResolveStack(game);
            Assert.IsFalse(host.IsTapped, "模式2（重置该角色）生效");
        }

        // ==================== ④ 单机：取消选择 → 该触发不入堆叠 ====================

        public void TestTriggerMode_取消模式选择_不入堆叠()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            var stub = new ModeStub { Answer = -1 };                  // 取消
            EffectEngine.Instance.UIHooks = stub;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var host = SealJc110OntoHost(game, area, out var jc110);
            Assert.IsTrue(game.TurnManager.SealCard(jc110, host, false), "前置：封印成功");

            Assert.AreEqual(1, stub.Calls, "问过一次");
            Assert.AreEqual(0, game.StackManager.Entries.Count, "取消 → 该触发不入堆叠");
            ResolveStack(game);
            Assert.IsFalse(host.IsTapped, "未选模式 → 宿主状态不变");
        }

        // ==================== ⑤ 联机：SelectEffectMode 请求 + 入堆叠记录 ====================

        public void TestTriggerMode_联机_选模式后入堆叠()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            EffectEngine.Instance.UIHooks = null;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var gw = new OnlineStubGateway { ModeAnswer = 0 };        // 选"横置该角色"
            game.TurnManager.InputGateway = gw;

            var host = SealJc110OntoHost(game, area, out var jc110);
            Assert.IsTrue(game.TurnManager.SealCard(jc110, host, false), "前置：封印成功");

            // 联机触发阶段是异步流程（SealCard→触发器→RunTriggerPhaseAsync，服务端驱动）；
            // 本桩的请求都是"已完成 Task"，故整条链在同一次调用内跑完；此处仅做短暂轮询兜底。
            for (int i = 0; i < 50 && game.StackManager.Entries.Count == 0; i++)
                System.Threading.Thread.Sleep(5);

            bool sawModeRequest = false;
            foreach (var r in gw.Requests)
                if (r.RequestKind == Script.Net.ChoiceRequest.Kind.SelectEffectMode) sawModeRequest = true;
            Assert.IsTrue(sawModeRequest, "联机下发过『选择一项』请求（复用卡牌模式的同一请求类型）");
            Assert.AreEqual(1, game.StackManager.Entries.Count, "触发入堆叠");
            Assert.AreEqual(0, game.StackManager.Entries[0].ModeIndex, "记录回包所选模式");

            ResolveStack(game);
            Assert.IsTrue(host.IsTapped, "模式1（横置该角色）生效");
        }

        // ==================== ⑥ 联机：取消选模式 → 该触发不入堆叠 ====================

        public void TestTriggerMode_联机取消模式_不入堆叠()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            EffectEngine.Instance.UIHooks = null;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            var gw = new OnlineStubGateway { CancelMode = true };      // 客户端取消/超时
            game.TurnManager.InputGateway = gw;

            var host = SealJc110OntoHost(game, area, out var jc110);
            Assert.IsTrue(game.TurnManager.SealCard(jc110, host, false), "前置：封印成功");
            for (int i = 0; i < 50 && gw.Requests.Count == 0; i++)
                System.Threading.Thread.Sleep(5);

            Assert.AreEqual(0, game.StackManager.Entries.Count, "取消选模式 → 该触发不入堆叠");
            ResolveStack(game);
            Assert.IsFalse(host.IsTapped, "未选模式 → 宿主状态不变");
        }

        // ==================== ⑦ 回归锁：`ZoneScope.SealedZone`（"被封印时"触发的生效区） ====================
        // 根因：`SealCard` 先把被封印卡移入封印区再广播 OnSealed（`SourceZoneAtTrigger = SealedZone`），
        // 而 `ScopeMatchesZone` 原先**没有封印区分支** → 角色类效果默认生效区（争夺区）不匹配，
        // "被封印时"自身触发在生产路径上**永远收集不到**（只有手工调用 ResolveEffect 的单测能过）。
        public void TestTriggerMode_JC100被封印时触发可收集()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            EffectEngine.Instance.UIHooks = null;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            var host = game.CardManager.CreateCardInstance(Probe("TS_tmc_host100"), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var fang = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToArea(fang, area, 1);
            Assert.IsTrue(game.TurnManager.SealCard(fang, host, false), "前置：封印成功");

            Assert.AreEqual(1, game.StackManager.Entries.Count, "JC100『被封印时』触发已入堆叠（封印区生效区）");
            Assert.AreEqual("JC100_3_PayToWeakenSealingHost",
                game.StackManager.Entries[0].EffectDataEffect?.EffectId, "入堆叠的正是该被封印时效果");
        }
    }
}
