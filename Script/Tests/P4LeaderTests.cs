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
    /// 【P4·领袖】词条规则回归（用户 2026-09 规则文本）：
    ///  · **同一控制者在场上至多 1 个领袖**（各控制者各算一个）；
    ///  · 领袖进场后若超限，**立刻**由该控制者选一个（含刚进场的那个）**牺牲** → **不入堆叠**、不可响应；
    ///  · **进场触发照常生效**（进场触发在进场时已入堆叠，不因规则性牺牲而撤销）；
    ///  · 牺牲是"死亡"（`DeathCause.Sacrifice`）→ 死亡触发照常；
    ///  · 决策路径：单机 `IGameUIHooks.PromptPickCard`（用可切换的桩替代）、联机 `ChoiceRequest.Kind.SelectTarget`、
    ///    无 UI/网关时确定性兜底（牺牲最新进场者）。
    /// </summary>
    public class P4LeaderTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        /// <summary>记录请求并按预设规则回答的网关：触发排序=全部入栈；SelectTarget=按脚本回 UID。</summary>
        private sealed class LeaderGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            /// <summary>SelectTarget 回包（返回 null = 不回包 UID，走取消/兜底路径）。</summary>
            public System.Func<ChoiceRequest, string> PickTarget;
            public bool CancelTarget;

            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                if (req.RequestKind == ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    var r = new ChoiceResult { RequestId = req.RequestId, Cancelled = false };
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                    return Task.FromResult(r);
                }
                if (req.RequestKind == ChoiceRequest.Kind.SelectTarget)
                {
                    if (CancelTarget)
                        return Task.FromResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = true });
                    return Task.FromResult(new ChoiceResult
                    {
                        RequestId = req.RequestId,
                        SelectedUid = PickTarget?.Invoke(req),
                        Cancelled = false,
                    });
                }
                return Task.FromResult(new ChoiceResult { RequestId = req.RequestId, Cancelled = false });
            }
        }

        /// <summary>可切换的单机 UI 桩：记录 PromptPickCard 调用，并按脚本回包。</summary>
        private sealed class PickerStub : Script.Interfaces.IGameUIHooks
        {
            public readonly List<List<CardInstance>> Prompts = new List<List<CardInstance>>();
            public System.Func<List<CardInstance>, CardInstance> Pick;
            public bool AllowUi = true;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }

            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }

            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                Prompts.Add(candidates);
                if (!AllowUi) return;
                onPicked?.Invoke(Pick != null ? Pick(candidates) : candidates[0]);
            }

            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid)
                => onPickedAreaUid?.Invoke(options != null && options.Count > 0 ? options[0].AreaUid : null);

            /// <summary>【K15】本套件不涉及多目标：视为玩家取消。</summary>
            public void PromptMultiTargets(CardInstance source, Script.Data.Effects.EffectData data,
                System.Func<int, List<Script.Manager.TargetChoice>, Script.Manager.SlotCandidates> candidatesForSlot,
                System.Action<List<Script.Manager.TargetChoice>> onConfirmed)
                => onConfirmed?.Invoke(null);

            /// <summary>【K16】本套件不涉及模式分支：视为取消。</summary>
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, Script.Data.Effects.EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            /// <summary>【K11】本套件不涉及数值输入：视为取消。</summary>
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue, string confirmLabel, string cancelLabel, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed)
                => onConfirmed?.Invoke(cards, new List<CardInstance>());
        }

        // ==================== 装配辅助 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 97 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static string FirstArea(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        /// <summary>【领袖】探针卡：持续授予"领袖"关键字（+可选附加效果）。</summary>
        private static PlayerCardData LeaderProbe(string uid, EffectData extra = null)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 1,
                CategoryFlag = CardCategory.Character, Defense = 1,
            };
            var leader = KeywordEffects.Leader();
            leader.EffectId = uid + "_leader";
            tmpl.EffectDataList.Add(leader);
            if (extra != null) tmpl.EffectDataList.Add(extra);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>无目标的探针效果（指定时机）。</summary>
        private static EffectData ProbeEffect(string id, EffectTiming timing, ActionVerb verb = ActionVerb.Draw)
            => new EffectData
            {
                EffectId = id,
                Timing = timing,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = verb, Args = new Array<string> { "1" } }
                },
            };

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int controllerId, string areaUid)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, controllerId);
            game.ZoneManager.MoveToArea(card, areaUid, controllerId);
            return card;
        }

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

        // ==================== 唯一性：超限立刻牺牲 ====================

        /// <summary>第一个领袖正常在场；第二个进场即超限 → 立刻牺牲（无 UI/网关时确定性兜底：牺牲最新进场者）。</summary>
        public void TestP4_领袖_进场超限立刻牺牲()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var first = OnField(game, LeaderProbe("T_ldr_first"), 1, area);
            Assert.IsTrue(LeaderRule.IsLeader(first), "持续授予『领袖』后应被识别为领袖");
            Assert.AreEqual(CardZone.AreaZone, first.CurrentZone, "单个领袖不超限，正常在场");

            var second = OnField(game, LeaderProbe("T_ldr_second"), 1, area);

            Assert.AreEqual(CardZone.Graveyard, second.CurrentZone, "超限 → 无 UI 兜底牺牲最新进场的领袖");
            Assert.AreEqual(CardZone.AreaZone, first.CurrentZone, "先在场的领袖保留");
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "同控制者场上至多 1 个领袖");
        }

        /// <summary>非领袖角色怎么进场都不受影响（对照：证明拦截只针对带【领袖】的卡）。</summary>
        public void TestP4_领袖_非领袖不受影响()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var leader = OnField(game, LeaderProbe("T_ldr_only"), 1, area);
            var plainA = game.CardManager.CreateCardInstance(Probe("T_plain_a"), 1);
            game.ZoneManager.MoveToArea(plainA, area, 1);
            var plainB = game.CardManager.CreateCardInstance(Probe("T_plain_b"), 1);
            game.ZoneManager.MoveToArea(plainB, area, 1);

            Assert.AreEqual(CardZone.AreaZone, leader.CurrentZone, "领袖仍在场");
            Assert.AreEqual(CardZone.AreaZone, plainA.CurrentZone, "普通角色不受领袖唯一性影响");
            Assert.AreEqual(CardZone.AreaZone, plainB.CurrentZone, "普通角色不受领袖唯一性影响");
        }

        /// <summary>唯一性是**按控制者**分别计的：双方各 1 个领袖互不影响。</summary>
        public void TestP4_领袖_双方各算一个()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var mine = OnField(game, LeaderProbe("T_ldr_p1"), 1, area);
            var theirs = OnField(game, LeaderProbe("T_ldr_p2"), 2, area);

            Assert.AreEqual(CardZone.AreaZone, mine.CurrentZone, "玩家1 的领袖在场");
            Assert.AreEqual(CardZone.AreaZone, theirs.CurrentZone, "玩家2 的领袖在场（各控制者各算一个）");
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "玩家1 场上领袖数=1");
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 2).Count, "玩家2 场上领袖数=1");
        }

        /// <summary>不变量：即便场上已存在多个领袖（异常构造），一次进场检查会把数量收敛到 1。</summary>
        public void TestP4_领袖_多个领袖收敛到1()
        {
            var game = NewGame();
            string area = FirstArea(game);

            // 暂时摘掉钩子，人为造出"3 个领袖同时在场"（正常规则下不可能出现）
            var hook = game.ZoneManager.OnCardEnteredArea;
            game.ZoneManager.OnCardEnteredArea = null;
            var a = OnField(game, LeaderProbe("T_ldr_c1"), 1, area);
            var b = OnField(game, LeaderProbe("T_ldr_c2"), 1, area);
            var c = OnField(game, LeaderProbe("T_ldr_c3"), 1, area);
            game.ZoneManager.OnCardEnteredArea = hook;
            Assert.AreEqual(3, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "前置：人为造出 3 个领袖");

            LeaderRule.EnforceOnEntry(c, game.CardManager, EffectEngine.Instance);

            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "收敛到恰好 1 个领袖");
            Assert.AreEqual(CardZone.AreaZone, a.CurrentZone, "最早进场的领袖保留");
            Assert.AreEqual(CardZone.Graveyard, b.CurrentZone, "其余领袖被牺牲");
            Assert.AreEqual(CardZone.Graveyard, c.CurrentZone, "其余领袖被牺牲");
        }

        // ==================== 进场触发照常生效 ====================

        /// <summary>超限被牺牲的领袖，其**进场触发**照常收集并进入堆叠（规则性牺牲不撤销触发）。</summary>
        public void TestP4_领袖_进场触发照常生效()
        {
            var game = NewGame();
            var gateway = new LeaderGateway();
            game.SetInputGateway(gateway);   // 触发排序窗口走网关（全部入栈），便于断言"确实进了堆叠"
            string area = FirstArea(game);

            OnField(game, LeaderProbe("T_ldr_et1"), 1, area);

            var tmpl = LeaderProbe("T_ldr_et2", ProbeEffect("T_ldr_et2_enter", EffectTiming.EnterTrigger));
            var entering = OnField(game, tmpl, 1, area);

            Assert.IsTrue(gateway.Requests.Exists(r => r.RequestKind == ChoiceRequest.Kind.SelectTriggerOrder),
                "进场触发照常走触发排序窗口（照常生效）");
            Assert.IsTrue(WaitUntil(() => StackHas(game, entering.UID)),
                "进场触发仍进入堆叠——规则性牺牲不撤销它");
            Assert.AreEqual(CardZone.Graveyard, entering.CurrentZone, "同时作为超限领袖被立刻牺牲");
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "场上仍只有 1 个领袖");
        }

        /// <summary>牺牲走死亡唯一入口：会广播 OnCardDied，被牺牲领袖自身的死亡触发照常收集。</summary>
        public void TestP4_领袖_牺牲算死亡并广播死亡触发()
        {
            var game = NewGame();
            var gateway = new LeaderGateway();
            game.SetInputGateway(gateway);
            string area = FirstArea(game);

            OnField(game, LeaderProbe("T_ldr_dt1"), 1, area);
            var tmpl = LeaderProbe("T_ldr_dt2", ProbeEffect("T_ldr_dt2_died", EffectTiming.OnCardDied));
            var second = OnField(game, tmpl, 1, area);

            Assert.AreEqual(CardZone.Graveyard, second.CurrentZone, "前置：已被规则牺牲");
            Assert.IsTrue(WaitUntil(() => StackHas(game, second.UID)),
                "牺牲=死亡（DeathCause.Sacrifice）→ 广播 OnCardDied 并收集其死亡触发");
        }

        // ==================== 决策路径：单机 / 联机 ====================

        /// <summary>单机：经 `IGameUIHooks.PromptPickCard` 让控制者选（不可取消），按玩家选择落地牺牲。</summary>
        public void TestP4_领袖_单机弹窗由控制者选择()
        {
            var game = NewGame();
            var picker = new PickerStub();
            EffectEngine.Instance.UIHooks = picker;
            string area = FirstArea(game);

            var first = OnField(game, LeaderProbe("T_ldr_ui1"), 1, area);
            picker.Pick = candidates => first;   // 玩家选择牺牲"先在场"的那个
            var second = OnField(game, LeaderProbe("T_ldr_ui2"), 1, area);

            Assert.AreEqual(1, picker.Prompts.Count, "应向该控制者弹一次选卡窗（候选=场上全部领袖）");
            Assert.AreEqual(2, picker.Prompts[0].Count, "候选应包含先在场者与刚进场者");
            Assert.AreEqual(CardZone.Graveyard, first.CurrentZone, "按玩家选择牺牲了先在场的领袖");
            Assert.AreEqual(CardZone.AreaZone, second.CurrentZone, "刚进场的领袖保留");
            EffectEngine.Instance.UIHooks = null;
        }

        /// <summary>联机：经网关下发 `SelectTarget` 决策请求（候选=全部领袖、强制选择），按回包落地牺牲。</summary>
        public void TestP4_领袖_联机决策请求按回包落地()
        {
            var game = NewGame();
            var gateway = new LeaderGateway();
            game.SetInputGateway(gateway);
            string area = FirstArea(game);

            var first = OnField(game, LeaderProbe("T_ldr_on1"), 1, area);
            gateway.PickTarget = _ => first.UID;
            var second = OnField(game, LeaderProbe("T_ldr_on2"), 1, area);

            var req = gateway.Requests.Find(r => r.RequestKind == ChoiceRequest.Kind.SelectTarget);
            Assert.IsNotNull(req, "联机应下发 SelectTarget 决策请求");
            Assert.AreEqual(2, req.OptionUids.Count, "候选=该控制者场上全部领袖");
            Assert.IsTrue(req.ForceChoice, "领袖唯一性是规则性结算：必须选一个（强制）");
            Assert.AreEqual(CardZone.Graveyard, first.CurrentZone, "按玩家回包牺牲指定领袖");
            Assert.AreEqual(CardZone.AreaZone, second.CurrentZone, "未被选中的领袖保留");
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "场上仍只有 1 个领袖");
        }

        /// <summary>联机：回包非法（不存在/越权 UID）或取消 → 服务端权威兜底（牺牲最新进场者），不卡死。</summary>
        public void TestP4_领袖_联机回包非法按兜底落地()
        {
            var game = NewGame();
            var gateway = new LeaderGateway();
            game.SetInputGateway(gateway);
            string area = FirstArea(game);

            var first = OnField(game, LeaderProbe("T_ldr_bad1"), 1, area);
            gateway.PickTarget = _ => "no_such_uid";
            var second = OnField(game, LeaderProbe("T_ldr_bad2"), 1, area);

            Assert.AreEqual(CardZone.AreaZone, first.CurrentZone, "非法回包不应误伤先在场的领袖");
            Assert.AreEqual(CardZone.Graveyard, second.CurrentZone, "非法回包 → 按确定性兜底牺牲最新进场者");

            // 取消回包同样兜底
            var game2 = NewGame();
            var gateway2 = new LeaderGateway { CancelTarget = true };
            game2.SetInputGateway(gateway2);
            string area2 = FirstArea(game2);
            var a2 = OnField(game2, LeaderProbe("T_ldr_can1"), 1, area2);
            var b2 = OnField(game2, LeaderProbe("T_ldr_can2"), 1, area2);
            Assert.AreEqual(CardZone.AreaZone, a2.CurrentZone, "取消回包不应误伤先在场的领袖");
            Assert.AreEqual(CardZone.Graveyard, b2.CurrentZone, "取消回包 → 按确定性兜底牺牲最新进场者");
        }

        // ==================== 结构：10 张领袖卡 ====================

        /// <summary>10 张领袖卡（JC010/011/023/024/037/038/051/052/067/095）都应声明【领袖】词条。</summary>
        public void TestP4_领袖_十张卡均声明领袖词条()
        {
            Cards.Effects.AllEffects.RegisterAll();
            string[] files =
            {
                "JC010_qingshoufeinandier", "JC011_demositinidamofashi", "JC023_fanmierdun",
                "JC024_SPTdongshiju", "JC037_kelisidinaaifuluode", "JC038_lieenaifuluode",
                "JC051_nuoennagesita", "JC052_zhongyandeshikoudi", "JC067_yuehanFnateli",
                "JC095_tanggeluni",
            };
            foreach (var f in files)
            {
                var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + f + ".tres");
                Assert.IsNotNull(tmpl, $"{f} 模板应可加载");
                CardEffectFactory.EnsureEffects(tmpl);
                Assert.IsTrue(tmpl.HasInherentKeyword("领袖"), $"{f} 应声明【领袖】词条（持续授予 Keyword=领袖）");
                Assert.IsTrue((tmpl.CategoryFlag & CardCategory.Character) != 0, $"{f} 应为角色（领袖只能是角色）");
            }
        }

        /// <summary>对照：普通角色（无【领袖】词条）不会被 `IsLeader` 误判。</summary>
        public void TestP4_领袖_探针对照()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var plain = game.CardManager.CreateCardInstance(Probe("T_ldr_plain"), 1);
            game.ZoneManager.MoveToArea(plain, area, 1);

            Assert.IsFalse(LeaderRule.IsLeader(plain), "无【领袖】词条的卡不是领袖");
            Assert.AreEqual(0, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "非领袖不计入领袖数");
        }

        /// <summary>端到端：真实领袖卡（JC010）进场后即被识别为领袖；第二张真实领袖卡（JC011）进场 → 立刻牺牲一个。</summary>
        public void TestP4_领袖_真实卡端到端()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var jc010 = OnField(game, Load("JC010_qingshoufeinandier"), 1, area);
            Assert.IsTrue(LeaderRule.IsLeader(jc010), "JC010 进场后应被识别为领袖（持续授予已注册）");
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count, "场上 1 个领袖");

            var jc011 = OnField(game, Load("JC011_demositinidamofashi"), 1, area);
            Assert.AreEqual(1, LeaderRule.LeadersOnField(game.CardManager, 1).Count,
                "第二张领袖卡进场后仍只剩 1 个（超限者被规则牺牲）");
            Assert.AreEqual(CardZone.Graveyard, jc011.CurrentZone, "无 UI 兜底：牺牲最新进场的领袖（JC011）");
            Assert.AreEqual(CardZone.AreaZone, jc010.CurrentZone, "先在场的领袖保留");
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
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
    }
}
