using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
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
    /// 【P1·效果基础能力】回归测试：锁定本批新增/补齐的能力，防止回退。
    ///
    /// 覆盖：
    ///  ① 触发时机接线：`OnCardDied` / `OnAreaWon` / `OnDraw` / `OnMove` / `OnCardLeft` / `OnMarkerPlaced`
    ///     以及"附属卡进场触发"（原先只在 MoveToArea 发，附属走 MoveToAttachedZone → 永不触发）；
    ///  ② 目标限制：CostAtMost / Tapped / HasMagicDomainIcon / Organization / HasKeyword；
    ///  ③ 能力图标 → 属性（黑刀/白眼/黑眼/白刀/黑星/白星），并验证真的参与对抗数值（AreaStatsCalculator）；
    ///  ④ Gain 动词：图标名走 Attribute、支持负数（Revoke）、防御下限钳制；
    ///  ⑤ 新动词：Latent（潜伏）/ Uncover（现身）/ Pay（支付失败中止后续动作链）；
    ///  ⑥ 费用修正过滤串：组织:/子类别:/域:/有域图标（服务端与客户端共用 CostModFilter）。
    /// </summary>
    public class P1EffectFoundationsTests : TestRunner
    {
        // ==================== 桩：网关 + 事件记录 ====================

        /// <summary>自动"全部入堆栈"的网关：让触发排序窗口无需 UI 即可走完，便于断言"触发被收集并入栈"。</summary>
        private sealed class AllEnterGateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                return Task.FromResult(r);
            }
        }

        /// <summary>记录所有经总线下发的事件（不接触发收集，纯观察 Emit 是否发生）。</summary>
        private sealed class RecordingBus
        {
            public readonly List<(GameEvent evt, TriggerContext ctx)> Events = new List<(GameEvent, TriggerContext)>();
            public GameEventBus Bus { get; }
            public RecordingBus()
            {
                Bus = new GameEventBus();
                Bus.SetDispatch((e, c) => Events.Add((e, c)));
            }
            public int Count(GameEvent e)
            {
                int n = 0;
                foreach (var x in Events) if (x.evt == e) n++;
                return n;
            }
            public TriggerContext Last(GameEvent e)
            {
                for (int i = Events.Count - 1; i >= 0; i--) if (Events[i].evt == e) return Events[i].ctx;
                return null;
            }
        }

        // ==================== 装配辅助 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 7 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        /// <summary>造一张"带指定效果"的合成卡模板（效果不走 UID 注册表，直接挂模板）。</summary>
        private static PlayerCardData ProbeTemplate(string uid, EffectData effect,
            CardCategory category = CardCategory.Character, string organization = "")
        {
            var tmpl = new PlayerCardData
            {
                UID = uid,
                CardName = "探针_" + uid,
                CategoryFlag = category,
                Organization = organization,
            };
            if (effect != null) tmpl.EffectDataList.Add(effect);
            // 关键：标记效果已注入，否则 CreateCardInstance → CardEffectFactory.EnsureEffects 会
            // `EffectDataList.Clear()` 把手工挂上的探针效果抹掉（未注册 UID 不会重新填充）。
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>无目标、条件恒真的探针效果（指定时机/生效区）。</summary>
        private static EffectData ProbeEffect(string id, EffectTiming timing, ZoneScope scope = ZoneScope.Specified,
            ActionVerb verb = ActionVerb.Draw, string arg = "1")
            => new EffectData
            {
                EffectId = id,
                Timing = timing,
                Scope = scope,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = verb, Args = new Array<string> { arg } } },
            };

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

        /// <summary>取一个已注册的争夺地区 UID（地区卡不在 ZoneManager.AreaZone 桶，须经 AreaManager 查询）。</summary>
        private static string FirstAreaUid(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        /// <summary>把一张探针卡放到场上，并断言它触发的效果最终进入堆叠。</summary>
        private static bool TriggerReachedStack(GameInitializer game, CardInstance probe, string areaUid)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.SourceCardUid == probe.UID) return true;
            return false;
        }

        // ==================== ① 触发时机接线（端到端：Emit → 收集 → 入栈） ====================

        /// <summary>死亡触发：伤害致死与"消灭"都应广播 OnCardDied，且能收到"此卡自身死亡"的触发。
        /// 【触发语义】死亡触发不要求声明 `Scope=Graveyard`：触发只判条件是否满足过，
        /// 生效区按"实时区域（墓地）或触发时区域（死亡前的争夺区）"任一命中即可。</summary>
        public void TestP1_OnCardDied_消灭与伤害致死都触发()
        {
            var game = NewGame();
            var gateway = new AllEnterGateway();
            game.TurnManager.InputGateway = gateway;

            string area = FirstAreaUid(game);
            // 不声明 Scope（角色默认生效区=争夺区）——按触发语义应照常触发（卡此刻其实在墓地）
            var tmpl = ProbeTemplate("probe_died", ProbeEffect("PROBE_Died", EffectTiming.OnCardDied));
            var victim = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(victim, area, 1);

            game.CardManager.DestroyInstance(victim);

            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "前置：已被消灭入墓地");
            Assert.IsTrue(WaitUntil(() => TriggerReachedStack(game, victim, area)),
                "「消灭」应广播 OnCardDied，且未声明墓地生效区的角色卡也能收到自身死亡触发（触发不看当前场景）");
            Assert.IsTrue(gateway.Requests.Count > 0, "应发起过触发排序请求（证明走了标准触发流程）");
        }

        /// <summary>【JC 卡包约定】死亡触发应写**默认生效区**（角色=争夺区），而不是 `Scope=Graveyard`。
        /// 依据：JC 包内触发条件必须在"卡仍在场上"时满足；死亡触发在卡离场前广播，故声明"只在墓地生效"与约定冲突。
        /// ⚠️ 注意：这是**卡包约定**而非引擎不变量——引擎（`IsEffectActiveInZone` 的"实时区域 ∪ 触发时区域"）
        /// 对两种写法都支持，后续卡包若修改触发规则（例如允许"卡在墓地也能响应"），本用例需随包调整。</summary>
        public void TestP1_OnCardDied_JC约定用默认生效区()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();

            string area = FirstAreaUid(game);
            var tmpl = ProbeTemplate("probe_died_gv",
                ProbeEffect("PROBE_DiedGv", EffectTiming.OnCardDied, ZoneScope.Graveyard));
            var victim = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(victim, area, 1);

            game.CardManager.DestroyInstance(victim);

            System.Threading.Thread.Sleep(150);
            Assert.IsFalse(TriggerReachedStack(game, victim, area),
                "JC 包约定：死亡触发条件在'卡仍在场上'时成立，故应写默认生效区；声明 Scope=Graveyard 不会被收集");
        }

        /// <summary>赢取地区触发：OnAreaWon 在"计分后、卡送库底前"广播，来源卡能收到。</summary>
        public void TestP1_OnAreaWon_接线生效()
        {
            var game = NewGame();
            var gateway = new AllEnterGateway();
            game.TurnManager.InputGateway = gateway;

            string area = FirstAreaUid(game);
            var tmpl = ProbeTemplate("probe_areawon", ProbeEffect("PROBE_AreaWon", EffectTiming.OnAreaWon));
            var watcher = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            game.AreaManager.ResolveAreaWon(area, 1);

            Assert.IsTrue(WaitUntil(() => TriggerReachedStack(game, watcher, area)),
                "地区赢取应广播 OnAreaWon（触点：计分之后、送牌库底之前）");
        }

        /// <summary>抓牌触发：OnDraw 在唯一抽牌收口广播（含效果抽牌与从牌库底抽牌）。</summary>
        public void TestP1_OnDraw_接线生效()
        {
            var game = NewGame();
            var gateway = new AllEnterGateway();
            game.TurnManager.InputGateway = gateway;

            string area = FirstAreaUid(game);
            var tmpl = ProbeTemplate("probe_draw", ProbeEffect("PROBE_Draw", EffectTiming.OnDraw));
            var watcher = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(watcher, area, 1);

            game.TurnManager.DrawCardsForPlayer(1, 1);

            Assert.IsTrue(WaitUntil(() => TriggerReachedStack(game, watcher, area)),
                "抓牌应广播 OnDraw，且在场的来源卡应收到");
        }

        /// <summary>场上移动触发：机动（争夺区→争夺区）只发 OnMove、不发 EnterTrigger；
        /// 手牌→争夺区则两者都发（两条时机语义必须区分清楚）。</summary>
        public void TestP1_OnMove_机动不发进场但发移动()
        {
            var bus = new RecordingBus();
            var emit = new TriggerEmitPoints(bus.Bus);

            var cm = new CardManager(); cm._Ready();
            var zm = new ZoneManager(); zm._Ready();
            zm.Initialize(cm, null);
            cm.SetZoneManager(zm);
            zm.SetTriggerEmitPoints(emit);
            cm.SetTriggerEmitPoints(emit);

            var tmpl = ProbeTemplate("probe_move", null);
            var card = cm.CreateCardInstance(tmpl, 1);

            // 手牌 → 地区A：进场触发 + 移动触发都应发
            zm.MoveToArea(card, "AreaSlot_1", 1);
            Assert.AreEqual(1, bus.Count(GameEvent.EnterTrigger), "手牌→争夺区应发 EnterTrigger");
            Assert.AreEqual(1, bus.Count(GameEvent.OnMove), "手牌→争夺区也应发 OnMove");

            // 地区A → 地区B（机动）：只发 OnMove
            zm.MoveToArea(card, "AreaSlot_2", 1);
            Assert.AreEqual(1, bus.Count(GameEvent.EnterTrigger), "争夺区→争夺区移动【不应】再发 EnterTrigger（D37）");
            Assert.AreEqual(2, bus.Count(GameEvent.OnMove), "争夺区→争夺区移动【必须】发 OnMove（机动类效果依赖）");
            Assert.AreEqual("AreaSlot_2", bus.Last(GameEvent.OnMove).TargetAreaId, "OnMove 应携带新地区");
            Assert.AreEqual("AreaSlot_1", bus.Last(GameEvent.OnMove).ExtraString, "OnMove 应携带来源地区");

            // 同区重放：不发
            zm.MoveToArea(card, "AreaSlot_2", 1);
            Assert.AreEqual(2, bus.Count(GameEvent.OnMove), "同区重放不应产生无意义的 OnMove");
        }

        /// <summary>离场触发：回手/回牌库底等"非死亡离场"广播 OnCardLeft；牌库→手牌不算离场。</summary>
        public void TestP1_OnCardLeft_仅场上离开时触发()
        {
            var bus = new RecordingBus();
            var emit = new TriggerEmitPoints(bus.Bus);

            var cm = new CardManager(); cm._Ready();
            var zm = new ZoneManager(); zm._Ready();
            zm.Initialize(cm, null);
            cm.SetZoneManager(zm);
            zm.SetTriggerEmitPoints(emit);

            var card = cm.CreateCardInstance(ProbeTemplate("probe_left", null), 1);
            zm.MoveToArea(card, "AreaSlot_1", 1);
            Assert.AreEqual(0, bus.Count(GameEvent.OnCardLeft), "此前没有离场");

            zm.MoveToHand(card, 1);
            Assert.AreEqual(1, bus.Count(GameEvent.OnCardLeft), "场上→手牌应发 OnCardLeft");
            Assert.AreEqual("AreaZone", bus.Last(GameEvent.OnCardLeft).ExtraString, "应携带离开时所处的区域");

            zm.MoveToHand(card, 1);
            Assert.AreEqual(1, bus.Count(GameEvent.OnCardLeft), "手牌→手牌不算离场，不应重复广播");
        }

        /// <summary>标志放置触发：OnMarkerPlaced 携带类型/数量/放置者/地区。</summary>
        public void TestP1_OnMarkerPlaced_势力标志与卡上计数器()
        {
            var bus = new RecordingBus();
            var emit = new TriggerEmitPoints(bus.Bus);

            var cm = new CardManager(); cm._Ready();
            var am = new AreaManager();
            am.SetTriggerEmitPoints(emit);
            am.RegisterArea("AreaSlot_1", 1, null);

            am.AddPowerMark("AreaSlot_1", 1, 2);
            Assert.AreEqual(1, bus.Count(GameEvent.OnMarkerPlaced), "势力标志放置应广播 OnMarkerPlaced");
            var ctx = bus.Last(GameEvent.OnMarkerPlaced);
            Assert.AreEqual("势力标志", ctx.ExtraString, "应携带标志类型");
            Assert.AreEqual(2, ctx.ExtraInt, "应携带数量");
            Assert.AreEqual(1, ctx.TriggeringPlayerId, "应携带放置者");
            Assert.AreEqual("AreaSlot_1", ctx.TargetAreaId, "应携带放置地区");

            // 卡上计数器（时间/锁定/哲人石标志的未来消费点）
            cm.SetTriggerEmitPoints(emit);
            var card = cm.CreateCardInstance(ProbeTemplate("probe_counter", null), 1);
            cm.AddCounter(card, "时间标志", 1);
            Assert.AreEqual(2, bus.Count(GameEvent.OnMarkerPlaced), "卡上计数器放置同样应广播");
            Assert.AreEqual("时间标志", bus.Last(GameEvent.OnMarkerPlaced).ExtraString, "应携带计数器类型");
        }

        /// <summary>附属卡进场触发：原实现只在 MoveToArea 发 EnterTrigger，附属走 MoveToAttachedZone → 永不触发。</summary>
        public void TestP1_附属卡进场触发接线生效()
        {
            var game = NewGame();
            var gateway = new AllEnterGateway();
            game.TurnManager.InputGateway = gateway;

            string area = FirstAreaUid(game);
            var hostTmpl = ProbeTemplate("probe_host", null);
            var host = game.CardManager.CreateCardInstance(hostTmpl, 1);
            game.ZoneManager.MoveToArea(host, area, 1);

            var attTmpl = ProbeTemplate("probe_att", ProbeEffect("PROBE_AttEnter", EffectTiming.EnterTrigger),
                CardCategory.Attachment);
            var att = game.CardManager.CreateCardInstance(attTmpl, 1);
            att.AttachedTo = host;
            host.AttachedCards.Add(att);
            game.ZoneManager.MoveToAttachedZone(att, area, 1);

            Assert.IsTrue(WaitUntil(() => TriggerReachedStack(game, att, area)),
                "附属卡进入附属区应广播 EnterTrigger 并被自身收为触发");
        }

        // ==================== ② 新增目标限制 ====================

        public void TestP1_新增目标限制_五条()
        {
            var tmpl = new PlayerCardData
            {
                UID = "T_restr", CardName = "被检卡", CategoryFlag = CardCategory.Character,
                Cost = 2, Organization = "王座会",
            };
            tmpl.MagicDomains = new Array<MagicDomain> { MagicDomain.Blood };
            tmpl.SubCategories = new Array<string> { "吸血鬼" };
            var inst = new CardInstance(tmpl, "restr_1")
            {
                OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.AreaZone,
            };
            inst.ResetToBaseValues(false);
            var noRes = new List<TargetRestriction>();

            // CostAtMost：2 <= 2 通过；1 不通过
            Assert.IsTrue(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.CostAtMost }, "2", inst, 1, null),
                "CostAtMost(2)：费用 2 的卡应通过");
            Assert.IsFalse(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.CostAtMost }, "1", inst, 1, null),
                "CostAtMost(1)：费用 2 的卡不应通过");

            // Tapped
            Assert.IsFalse(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.Tapped }, "", inst, 1, null), "未横置不应通过 Tapped");
            inst.SetTapped(true);
            Assert.IsTrue(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.Tapped }, "", inst, 1, null), "已横置应通过 Tapped");
            inst.SetTapped(false);

            // HasMagicDomainIcon
            Assert.IsTrue(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.HasMagicDomainIcon }, "", inst, 1, null),
                "有【血】领域图标应通过");
            var noDomainTmpl = new PlayerCardData { UID = "T_nod", CardName = "无领域", CategoryFlag = CardCategory.Character };
            noDomainTmpl.MagicDomains = new Array<MagicDomain> { (MagicDomain)(-1) };   // -1 哨兵=印刷无领域
            var noDomain = new CardInstance(noDomainTmpl, "restr_2") { ControllerId = 1 };
            Assert.IsFalse(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.HasMagicDomainIcon }, "", noDomain, 1, null),
                "-1 哨兵（无领域）不应通过 HasMagicDomainIcon");

            // Organization
            Assert.IsTrue(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.Organization }, "王座会", inst, 1, null),
                "Organization 匹配应通过");
            Assert.IsFalse(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.Organization }, "鸣钟教派", inst, 1, null),
                "Organization 不匹配不应通过");

            // HasKeyword：运行时授予 与 模板固有 双层
            Assert.IsFalse(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.HasKeyword }, "公开", inst, 1, null),
                "未声明关键字不应通过");
            inst.Grant("Keyword", "灵体", 1);
            Assert.IsTrue(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.HasKeyword }, "灵体", inst, 1, null),
                "运行时授予的关键字应通过 HasKeyword");
            var inherentTmpl = new PlayerCardData { UID = "T_pub", CardName = "公开卡", CategoryFlag = CardCategory.Character };
            inherentTmpl.EffectDataList.Add(KeywordEffects.Public());
            var inherent = new CardInstance(inherentTmpl, "restr_3") { ControllerId = 1 };
            Assert.IsTrue(TargetValidity.CheckRestrictions(
                new List<TargetRestriction> { TargetRestriction.HasKeyword }, "公开", inherent, 1, null),
                "模板固有声明（如公开）也应通过 HasKeyword");

            Assert.IsTrue(TargetValidity.CheckRestrictions(noRes, "", inst, 1, null), "空限制集应恒通过（前置校验）");
        }

        // ==================== ③④ 图标 → 属性 / Gain ====================

        /// <summary>六个能力图标全部映射到实例属性，且真的参与对抗数值（含先手与非先手差异）。</summary>
        public void TestP1_图标属性映射与对抗数值()
        {
            var tmpl = new PlayerCardData
            {
                UID = "T_icon", CardName = "图标测试", CategoryFlag = CardCategory.Character,
                Investigation = 1, Strength = 2, Power = 1, Defense = 2,
            };
            var card = new CardInstance(tmpl, "icon_1") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.AreaZone };
            card.ResetToBaseValues(false);

            var pm = new PlayerManager();
            pm._Ready();
            pm.SetFirstPlayer(1);

            Assert.AreEqual(2, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Strength, pm), "基础战力=2");

            // 黑刀=战斗（折进实例，非先手也生效）
            card.Grant("Attribute", EffectEngine.IconToAttribute("黑刀"), 2);
            Assert.AreEqual(4, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Strength, pm),
                "黑刀×2 应让战力 2→4（先手与否都生效）");

            // 白刀=先手战斗（仅先手叠加）
            card.Grant("Attribute", EffectEngine.IconToAttribute("白刀"), 1);
            Assert.AreEqual(5, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Strength, pm),
                "先手玩家应再叠白刀 +1");
            pm.SetFirstPlayer(2);
            Assert.AreEqual(4, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Strength, pm),
                "非先手玩家不应获得白刀加成");
            pm.SetFirstPlayer(1);

            // 黑星=势力（折进实例）／白星=先手势力（实时）
            card.Grant("Attribute", EffectEngine.IconToAttribute("黑星"), 1);
            Assert.AreEqual(2, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Power, pm), "黑星 +1 → 势力 2");
            card.Grant("Attribute", EffectEngine.IconToAttribute("白星"), 1);
            Assert.AreEqual(3, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Power, pm), "先手再叠白星 +1");
            pm.SetFirstPlayer(2);
            Assert.AreEqual(2, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Power, pm), "非先手不叠白星");

            // 黑眼=调查（折进实例）／白眼=先手调查（实时）
            card.Grant("Attribute", EffectEngine.IconToAttribute("黑眼"), 1);
            Assert.AreEqual(2, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Investigation, pm), "黑眼 +1 → 调查 2");
            card.Grant("Attribute", EffectEngine.IconToAttribute("白眼"), 1);
            pm.SetFirstPlayer(1);
            Assert.AreEqual(3, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Investigation, pm), "先手再叠白眼 +1");

            // 撤销（负向）应精确还原
            card.Revoke("Attribute", EffectEngine.IconToAttribute("黑刀"), 2);
            pm.SetFirstPlayer(2);
            Assert.AreEqual(2, AreaStatsCalculator.ValueOf(card, CombatManager.CombatType.Strength, pm), "撤销黑刀后回到基础战力 2");
        }

        /// <summary>Gain 动词：图标名走 Attribute；负数走 Revoke；防御下限钳制在 -1。</summary>
        public void TestP1_Gain_图标映射与负数()
        {
            var ee = new EffectEngine();
            var tmpl = new PlayerCardData
            {
                UID = "T_gain", CardName = "增益测试", CategoryFlag = CardCategory.Character,
                Strength = 1, Defense = 2,
            };
            var card = new CardInstance(tmpl, "gain_1") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.AreaZone };
            card.ResetToBaseValues(false);

            ee.ResolveEffect(new EffectData
            {
                EffectId = "T_gain_icon", Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑刀", "2" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑刀", "-1" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "-1" } },
                },
            }, card, card);

            Assert.AreEqual(2, card.CurrentStrength, "黑刀+2 再 -1 应为 1+1=2（图标→Attribute 且支持负数）");
            Assert.AreEqual(1, card.GetAttribute("战斗"), "授予计数应为净额 1（+2 再 -1）");
            Assert.AreEqual(1, card.CurrentDefense, "防御 2-1=1");

            // 防御下限：反复减益不得低于"无防御"哨兵 -1
            ee.ResolveEffect(new EffectData
            {
                EffectId = "T_gain_def", Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "防御", "-5" } },
                },
            }, card, card);
            Assert.AreEqual(-1, card.CurrentDefense, "防御减益应钳制在 -1（无防御）");
        }

        // ==================== ⑤ 新动词 ====================

        /// <summary>Latent（潜伏）/ Uncover（现身）：经 SetCovert 收口，翻面即 BumpVersion。</summary>
        public void TestP1_动词_潜伏与现身()
        {
            var ee = new EffectEngine();
            var tmpl = ProbeTemplate("T_latent", null);
            var card = new CardInstance(tmpl, "latent_1") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.AreaZone };
            int v0 = card.VersionId;

            ee.ResolveEffect(new EffectData
            {
                EffectId = "T_latent", Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Latent } },
            }, card, card);
            Assert.IsTrue(card.IsCovert, "Latent 应把卡翻面为暗藏者");
            Assert.IsTrue(card.VersionId != v0, "翻面应刷新版本号（规则③：翻面即判目标丢失）");

            ee.ResolveEffect(new EffectData
            {
                EffectId = "T_uncover", Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Uncover } },
            }, card, card);
            Assert.IsFalse(card.IsCovert, "Uncover 应令暗藏者现身");
        }

        /// <summary>Pay 动词：费用不足时中止同一动作序列的后续动作（实现"若你如此做，则…"语义）。</summary>
        public void TestP1_动词_Pay失败中止动作链()
        {
            var game = NewGame();
            var ee = EffectEngine.Instance;
            var pm = game.TurnManager.PlayerManagerRef;
            int playerId = 1;

            var srcTmpl = ProbeTemplate("T_payer", null);
            var src = game.CardManager.CreateCardInstance(srcTmpl, playerId);
            string area = FirstAreaUid(game);
            game.ZoneManager.MoveToArea(src, area, playerId);

            var victimTmpl = ProbeTemplate("T_victim", null);
            var victim = game.CardManager.CreateCardInstance(victimTmpl, playerId);
            game.ZoneManager.MoveToArea(victim, area, playerId);

            // 无未横置资产 → 可用费用 0 → Pay(1) 失败 → 后续 Destroy 不应执行
            Assert.AreEqual(0, pm.GetAvailableMana(playerId), "前置：该玩家当前无可用费用");
            ee.ResolveEffect(new EffectData
            {
                EffectId = "T_pay_fail", Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Pay, Args = new Array<string> { "1" } },
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
            }, src, victim);
            Assert.IsTrue(victim.CurrentZone == CardZone.AreaZone, "Pay 失败后，后续 Destroy 不应执行");

            // 给一张未横置资产 → 可以支付 → 后续 Destroy 执行
            var asset = game.CardManager.CreateCardInstance(ProbeTemplate("T_asset", null), playerId);
            game.ZoneManager.MoveToAsset(asset, playerId);
            Assert.AreEqual(1, pm.GetAvailableMana(playerId), "前置：可支付 1");
            ee.ResolveEffect(new EffectData
            {
                EffectId = "T_pay_ok", Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Pay, Args = new Array<string> { "1" } },
                    new EffectAction { Verb = ActionVerb.Destroy },
                },
            }, src, victim);
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "Pay 成功后，后续 Destroy 应执行");
            Assert.AreEqual(0, pm.GetAvailableMana(playerId), "支付应横置资产（费用已花费）");
        }

        // ==================== ⑥ 费用修正过滤串（双端共用） ====================

        public void TestP1_费用修正过滤串()
        {
            var tmpl = new PlayerCardData
            {
                UID = "T_cost", CardName = "装备牌", CategoryFlag = CardCategory.Attachment,
                Organization = "王座会",
            };
            tmpl.MagicDomains = new Array<MagicDomain> { MagicDomain.Blood };
            tmpl.SubCategories = new Array<string> { "装备", "武器" };
            var inst = new CardInstance(tmpl, "cost_1") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.Hand };

            // 旧语义（无前缀）：卡名包含 / Organization 相等
            Assert.IsTrue(CostModFilter.Matches("装备", inst, "装备牌", "王座会"), "旧语义：卡名包含应命中");
            Assert.IsTrue(CostModFilter.Matches("王座会", inst, "装备牌", "王座会"), "旧语义：Organization 相等应命中");
            Assert.IsFalse(CostModFilter.Matches("鸣钟教派", inst, "装备牌", "王座会"), "旧语义：都不匹配则未命中");
            Assert.IsTrue(CostModFilter.Matches("", inst, "装备牌", "王座会"), "空串=全匹配");

            // 扩展语义
            Assert.IsTrue(CostModFilter.Matches("组织:王座会", inst, "装备牌", "王座会"), "组织: 前缀");
            Assert.IsFalse(CostModFilter.Matches("组织:猎魔人", inst, "装备牌", "王座会"), "组织: 前缀不匹配");
            Assert.IsTrue(CostModFilter.Matches("子类别:装备", inst, "装备牌", "王座会"), "子类别: 前缀");
            Assert.IsTrue(CostModFilter.Matches("域:Blood", inst, "装备牌", "王座会"), "域: 前缀（枚举名）");
            Assert.IsTrue(CostModFilter.Matches("域:血", inst, "装备牌", "王座会"), "域: 前缀（中文）");
            Assert.IsFalse(CostModFilter.Matches("域:心", inst, "装备牌", "王座会"), "域: 前缀（不匹配的领域）");
            Assert.IsTrue(CostModFilter.Matches("有域图标", inst, "装备牌", "王座会"), "有域图标（JC112 用）");

            // 服务端查询链路（与客户端 ArenaOl.ComputeClientCostMod 共用本匹配器）
            var ee = new EffectEngine();
            ee.ApplyCostModification(1, -1, 99, "子类别:装备", consumeOnce: true);
            Assert.AreEqual(-1, ee.GetCostModification(1, inst, "装备牌", "王座会"), "按子类别过滤应命中该卡");
            var otherTmpl = new PlayerCardData { UID = "T_other", CardName = "法术牌", CategoryFlag = CardCategory.Sorcery };
            var other = new CardInstance(otherTmpl, "cost_2") { OwnerId = 1, ControllerId = 1 };
            Assert.AreEqual(0, ee.GetCostModification(1, other, "法术牌", ""), "不满足子类别过滤的卡不应获得减费");
        }
    }
}
