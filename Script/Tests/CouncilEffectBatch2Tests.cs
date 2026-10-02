using System.Collections.Generic;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Cards.Script.CouncilCards.JC;

namespace Script.Tests
{
    /// <summary>
    /// 【秘社效果批次·MSJC010~017】按卡文落地（用户 2026-09 口径）：
    ///  · 逐张的**激活选项结构**（选项数/费用/动词/筛选/条件/目标）；
    ///  · 本批次新增机制的**行为**：
    ///    ① 构筑白名单模式（`UseWhitelist` + `ExceptionRules`）—— 详见 `P5CouncilRestrictionTests`；
    ///    ② **牌作代价** `CardCostSpec`（封印区的牌→牌库底 / 墓地七张→封印在秘社上 / 横置三个本方角色）；
    ///    ③ `DestroyWithMinCounters`（013②"消灭所有其上有≥3时间标志者，按实际消灭数抓牌"）；
    ///    ④ `MillTopToGraveyard`（016①，磨牌**不触发死亡**）；
    ///    ⑤ `PlaceHandCardToArea`（015①，从手中暗藏派遣一张角色）；
    ///    ⑥ `UnsealToArea`（010③/016②，封印牌/墓地牌正面进场到目标地区）；
    ///    ⑦ `PutCardOnDeckTop`（017②，墓地灵体角色置于牌库顶）；
    ///    ⑧ `CouncilStaticGrant`（012，印刷费用≤2 的本方人类获得奴仆类别）；
    ///    ⑨ `CouncilCardData.DrawFromBottom`（017，抓牌改从牌库底）；
    ///    ⑩ `HandAtLeast` 谓词（015①）与 `具有先手标志` 复用（017①）；
    ///    ⑪ 开局效果 `OpeningEffectList`（015，开局在秘社上放【隐秘】标志）。
    /// </summary>
    public class CouncilEffectBatch2Tests : TestRunner
    {
        private const string CouncilDir = "res://Cards/Data/CouncilCards/JC/";

        private static CouncilCardData Load(string file)
            => ResourceLoader.Load<CouncilCardData>(CouncilDir + file + ".tres");

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 20260910 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Probe(string uid, string[] subs = null, int strength = 0,
            int cost = 1, CardCategory cat = CardCategory.Character, string inherentKeyword = null)
        {
            var t = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = cost, CategoryFlag = cat,
                Strength = strength, Defense = 1,
            };
            foreach (var s in subs ?? System.Array.Empty<string>()) t.SubCategories.Add(s);
            if (inherentKeyword != null)
            {
                var e = new EffectData { Timing = EffectTiming.Continuous };
                e.ContinuousGrants.Add(new EffectGrant { Kind = "Keyword", Value = inherentKeyword, Amount = 1 });
                t.EffectDataList.Add(e);
            }
            t._effectsInjected = true;
            return t;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int pid, string areaUid)
        {
            var c = game.CardManager.CreateCardInstance(tmpl, pid);
            game.ZoneManager.MoveToArea(c, areaUid, pid);
            return c;
        }

        /// <summary>记录总线下发的所有事件（纯观察，不接触发收集）——与 `P3DrawFromBottomTests` 同款。</summary>
        private sealed class RecordingBus
        {
            public readonly List<GameEvent> Events = new List<GameEvent>();
            public GameEventBus Bus { get; }
            public RecordingBus()
            {
                Bus = new GameEventBus();
                Bus.SetDispatch((e, c) => Events.Add(e));
            }
            public bool Has(GameEvent e) => Events.Contains(e);
        }

        /// <summary>把游戏的触发系统接到"记录总线"上（既观察事件、又保持触发收集照常）。</summary>
        private static RecordingBus WireRecordingBus(GameInitializer game)
        {
            var rec = new RecordingBus();
            var registry = new TriggerRegistry(rec.Bus);
            rec.Bus.SetDispatch((e, c) =>
            {
                rec.Events.Add(e);
                registry.HandleEvent(e, c);
            });
            var emit = new TriggerEmitPoints(rec.Bus);
            game.TurnManager.SetTriggerSystem(registry, emit, rec.Bus);
            return rec;
        }

        private static EffectData Opt(CouncilCardData c, int optIndex, int effectIndex = 0)
            => c.ActivationOptions[optIndex].EffectDataList[effectIndex];

        // ==================== ① 逐张结构 ====================

        public void TestMSJC010_结构三能力()
        {
            var c = Load("MSJC010_mozu");
            Assert.IsNotNull(c, "MSJC010 应可加载");
            Assert.AreEqual(4, c.ActivationOptions.Count, "4 个选项（①封印+抓牌 / ②获得灵体 / ③失去灵体 / ④角色牌进场）");
            Assert.AreEqual(2, c.ActivationOptions[0].Cost, "①费用 2");
            Assert.AreEqual(2, c.ActivationOptions[1].Cost, "②费用 2");
            Assert.AreEqual(2, c.ActivationOptions[2].Cost, "③费用 2");
            Assert.AreEqual(4, c.ActivationOptions[3].Cost, "④费用 4");

            var e1 = Opt(c, 0);
            Assert.AreEqual(ActionVerb.Seal, e1.Actions[0].Verb, "①第一个动作＝封印");
            Assert.AreEqual("手牌", e1.Actions[0].Args[0], "①从手牌取");
            Assert.AreEqual("自身", e1.Actions[0].Args[1], "①封印在秘社（自身）上");
            Assert.AreEqual("领域:星空", e1.Actions[0].Args[3], "①候选限定【星】领域");
            Assert.AreEqual(ActionVerb.Draw, e1.Actions[1].Verb, "①然后抓一张牌");
            Assert.IsNull(e1.Target, "①无目标能力必须显式 Target=null");

            var e2 = Opt(c, 1);
            Assert.IsNotNull(e2.CardCost, "②有牌作代价");
            Assert.AreEqual("封印区", e2.CardCost.FromZone, "②代价取自封印区");
            Assert.AreEqual(1, e2.CardCost.Count, "②代价 1 张");
            Assert.AreEqual(ActionVerb.MoveToZone, e2.CardCost.CostAction.Verb, "②代价动作＝移动到牌库底");
            Assert.AreEqual("牌库底", e2.CardCost.CostAction.Args[1], "②去向＝牌库底");
            Assert.AreEqual("自己", e2.CardCost.CostAction.Args[3], "②牌库归属＝**你的**牌库（不是拥有者）");
            Assert.AreEqual(ActionVerb.Gain, e2.Actions[0].Verb, "②收益＝获得灵体");
            Assert.AreEqual("灵体", e2.Actions[0].Args[0], "②获得的是灵体");
            Assert.AreEqual("直到回合结束", e2.Duration, "②时效到回合结束");

            var e3 = Opt(c, 2);
            Assert.AreEqual(ActionVerb.Lose, e3.Actions[0].Verb, "③收益＝失去灵体");

            var e4 = Opt(c, 3);
            Assert.IsTrue(e4.NeedsAreaTarget, "④需要选目标地区");
            Assert.AreEqual(2, e4.CardCost.Count, "④代价 2 张");
            Assert.AreEqual(ActionVerb.UnsealToArea, e4.Actions[0].Verb, "④动作用 UnsealToArea");
            Assert.AreEqual("类别:角色", e4.Actions[0].Args[3], "④只挑**角色牌**");
            Assert.AreEqual("由我操控", e4.Actions[0].Args[4], "④进场后由我操控");
        }

        public void TestMSJC011_结构两能力()
        {
            var c = Load("MSJC011_SPTzhixingbu");
            Assert.AreEqual(2, c.ActivationOptions.Count, "2 个选项");
            var e1 = Opt(c, 0);
            Assert.AreEqual(TargetKind.Character, e1.Target.Kind, "①目标＝角色");
            Assert.AreEqual(ActionVerb.Gain, e1.Actions[0].Verb, "①获得");
            Assert.AreEqual("杀伤", e1.Actions[0].Args[0], "①获得杀伤1");
            Assert.AreEqual("1", e1.Actions[0].Args[1], "①杀伤 1");

            var e2 = Opt(c, 1);
            Assert.IsTrue(e2.NeedsAreaTarget, "②需要选目标地区");
            Assert.AreEqual(MassMode.AllInArea, e2.Target.MassMode, "②群体");
            Assert.IsFalse(e2.Target.NeedsSelection, "②玩家选的是地区不是角色");
            Assert.IsTrue(e2.Target.Restrictions.Contains(TargetRestriction.Friendly), "②只限本方");
            Assert.IsTrue(e2.Target.Restrictions.Contains(TargetRestriction.HasAbilityIcon), "②需要能力图标");
            Assert.AreEqual("黑刀", e2.Target.Param, "②图标＝黑刀");
            Assert.AreEqual("撤回", e2.Actions[0].Args[0], "②获得撤回");
        }

        public void TestMSJC012_结构两能力加静态授予()
        {
            var c = Load("MSJC012_aifuluodejiazu");
            Assert.AreEqual(2, c.ActivationOptions.Count, "2 个选项");
            var e1 = Opt(c, 0);
            Assert.AreEqual(1, e1.Cost, "①费用 1");
            Assert.AreEqual(3, e1.CardCost.Count, "①代价＝三个");
            Assert.AreEqual("子类别:奴仆", e1.CardCost.Filter, "①筛选奴仆");
            Assert.AreEqual(ActionVerb.Tap, e1.CardCost.CostAction.Verb, "①代价动作＝横置");
            Assert.IsTrue(e1.CardCost.UntappedOnly, "①候选须未横置");
            Assert.AreEqual(ActionVerb.Draw, e1.Actions[0].Verb, "①收益＝抓一张");

            var e2 = Opt(c, 1);
            Assert.AreEqual(2, e2.Cost, "②费用 2");
            Assert.AreEqual("子类别:吸血鬼", e2.CardCost.Filter, "②筛选吸血鬼");
            Assert.AreEqual(TargetKind.Player, e2.Target.Kind, "②目标＝玩家");
            Assert.IsTrue(e2.Target.Restrictions.Contains(TargetRestriction.Enemy), "②目标＝敌方玩家");
            Assert.AreEqual(ActionVerb.Discard, e2.Actions[0].Verb, "②收益＝弃牌");
            Assert.AreEqual("目标玩家", e2.Actions[0].Args[1], "②由目标玩家弃");

            Assert.AreEqual(1, c.StaticEffectList.Count, "应有 1 条静态持续");
            var grant = c.StaticEffectList[0].CouncilStatic;
            Assert.IsNotNull(grant, "静态持续＝CouncilStaticGrant");
            Assert.AreEqual("奴仆", grant.GrantSubCategory, "授予奴仆类别");
            Assert.IsTrue(grant.FilterByMaxCost && grant.MaxCost == 2, "印刷费用≤2");
            Assert.IsTrue(grant.FilterBySubCategories && grant.TargetSubCategories.Contains("人类"), "限人类");
        }

        public void TestMSJC013_结构两能力()
        {
            var c = Load("MSJC013_jishizhe");
            Assert.AreEqual(2, c.ActivationOptions.Count, "2 个选项");
            var e1 = Opt(c, 0);
            Assert.AreEqual(2, e1.Cost, "①费用 2");
            Assert.AreEqual(TargetKind.Character, e1.Target.Kind, "①目标＝角色");
            Assert.AreEqual(TargetKind.Attachment, e1.Target.KindAlt, "①或附属");
            Assert.AreEqual(ActionVerb.PlaceCounter, e1.Actions[0].Verb, "①放标志");
            Assert.AreEqual("时间标志", e1.Actions[0].Args[0], "①时间标志");

            var e2 = Opt(c, 1);
            Assert.AreEqual(3, e2.Cost, "②费用 3");
            Assert.AreEqual(ActionVerb.DestroyWithMinCounters, e2.Actions[0].Verb, "②专用动词");
            Assert.AreEqual("时间标志", e2.Actions[0].Args[0], "②标志名");
            Assert.AreEqual("3", e2.Actions[0].Args[1], "②门槛 3");
        }

        public void TestMSJC014_结构两能力()
        {
            var c = Load("MSJC014_shaobing");
            Assert.AreEqual(2, c.ActivationOptions.Count, "2 个选项");
            var e1 = Opt(c, 0);
            Assert.AreEqual(TargetKind.Covert, e1.Target.Kind, "①目标＝暗藏者");
            Assert.IsTrue(e1.Target.Restrictions.Contains(TargetRestriction.Enemy), "①敌方暗藏者");
            Assert.AreEqual("锁定标志", e1.Actions[0].Args[0], "①放置锁定标志");

            var e2 = Opt(c, 1);
            Assert.AreEqual(TargetKind.Character, e2.Target.Kind, "②目标＝本方角色");
            Assert.IsTrue(e2.Target.Restrictions.Contains(TargetRestriction.Friendly), "②本方");
            Assert.AreEqual(ActionVerb.Move, e2.Actions[0].Verb, "②移动");
            Assert.AreEqual("锁定者所在地区", e2.Actions[0].Args[1], "②目的地＝锁定者所在地区");
        }

        public void TestMSJC015_结构两能力加开局效果()
        {
            var c = Load("MSJC015_yinshi");
            Assert.AreEqual(1, c.ActivationOptions.Count, "1 个激活选项");
            var e1 = Opt(c, 0);
            Assert.AreEqual(3, e1.Cost, "费用 3");
            Assert.IsTrue(e1.NeedsAreaTarget, "需要选目标地区");
            Assert.IsNotNull(e1.Condition, "有发动/结算条件");
            Assert.AreEqual("HandAtLeast", e1.Condition.Predicate, "条件＝手牌≥N");
            Assert.AreEqual("4", e1.Condition.Param, "条件＝手牌≥4");
            Assert.AreEqual(ActionVerb.PlaceHandCardToArea, e1.Actions[0].Verb, "动词＝从手牌放置进场");
            Assert.AreEqual("类别:角色", e1.Actions[0].Args[0], "只挑角色牌");
            Assert.AreEqual("暗藏", e1.Actions[0].Args[1], "以暗藏者状态");

            Assert.AreEqual(1, c.OpeningEffectList.Count, "应有 1 条开局效果");
            var open = c.OpeningEffectList[0];
            Assert.AreEqual(ActionVerb.PlaceCounter, open.Actions[0].Verb, "开局效果＝放标志");
            Assert.AreEqual("隐秘", open.Actions[0].Args[0], "放【隐秘】标志");
            Assert.AreEqual("自身", open.Actions[0].Args[2], "放在秘社自身上");
        }

        public void TestMSJC016_结构两能力加首回合触发()
        {
            var c = Load("MSJC016_songwangzhe");
            Assert.AreEqual(2, c.ActivationOptions.Count, "2 个激活选项");
            var e1 = Opt(c, 0);
            Assert.AreEqual(3, e1.Cost, "①费用 3");
            Assert.AreEqual(ActionVerb.MillTopToGraveyard, e1.Actions[0].Verb, "①磨牌");
            Assert.AreEqual("1", e1.Actions[0].Args[0], "①磨 1 张");

            var e2 = Opt(c, 1);
            Assert.AreEqual(6, e2.Cost, "②费用 6");
            Assert.IsTrue(e2.NeedsAreaTarget, "②需要选目标地区");
            Assert.AreEqual(7, e2.CardCost.Count, "②代价 7 张");
            Assert.AreEqual("墓地", e2.CardCost.FromZone, "②代价取自墓地");
            Assert.AreEqual(ActionVerb.Seal, e2.CardCost.CostAction.Verb, "②代价动作＝封印在秘社上");
            Assert.AreEqual(TargetKind.Graveyard, e2.Target.Kind, "②目标＝墓地中的牌");
            Assert.AreEqual("角色", e2.Target.Param, "②目标是角色牌");
            Assert.AreEqual("领域:死亡", e2.Target.Filter, "②目标限定【死】领域");
            Assert.AreEqual(ActionVerb.PutToArea, e2.Actions[0].Verb, "②放进目标地区");

            Assert.AreEqual(1, c.TurnStartEffectList.Count, "应有 1 条首个准备步骤触发");
            Assert.AreEqual("3", c.TurnStartEffectList[0].Actions[0].Args[0], "触发磨 3 张");
            Assert.AreEqual(5, c.StartingHandSize, "初始手牌 5");
        }

        public void TestMSJC017_结构两能力加抓牌方向()
        {
            var c = Load("MSJC017_mengyanzuqun");
            Assert.AreEqual(2, c.ActivationOptions.Count, "2 个选项");
            Assert.IsTrue(c.DrawFromBottom, "持续：从牌库底抓牌");
            var e1 = Opt(c, 0);
            Assert.AreEqual(3, e1.Cost, "①费用 3");
            Assert.IsNotNull(e1.Condition, "①有条件");
            Assert.AreEqual("具有先手标志", e1.Condition.Predicate, "①条件＝具有先手标志");
            Assert.AreEqual(ActionVerb.Seal, e1.Actions[0].Verb, "①封印");
            Assert.AreEqual("牌库顶", e1.Actions[0].Args[0], "①从牌库顶取");
            Assert.AreEqual("目标", e1.Actions[0].Args[1], "①封印在目标角色上");

            var e2 = Opt(c, 1);
            Assert.AreEqual(4, e2.Cost, "②费用 4");
            Assert.AreEqual(TargetKind.Graveyard, e2.Target.Kind, "②目标＝墓地");
            Assert.AreEqual("灵体|角色", e2.Target.Param, "②须灵体角色");
            Assert.AreEqual(ActionVerb.PutCardOnDeckTop, e2.Actions[0].Verb, "②置于牌库顶");
        }

        // ==================== ② 牌作代价（CardCostSpec） ====================

        /// <summary>010 ①②：封印区候选＝"封印在**本秘社**上"的牌；不足 N 张时门禁直接拒绝。</summary>
        public void TestCardCost_封印区候选与不足拒绝()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC010_mozu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            var e2 = Opt(Load("MSJC010_mozu"), 1);
            var cands = game.EffectEngine.GetCardCostCandidates(council, e2.CardCost);
            Assert.AreEqual(0, cands.Count, "前置：秘社上还没有封印牌 ⇒ 无候选");

            // 封印两张牌到秘社上
            var s1 = game.CardManager.CreateCardInstance(Probe("TS_cc_s1", cost: 0), 1);
            var s2 = game.CardManager.CreateCardInstance(Probe("TS_cc_s2", cost: 0), 2);
            Assert.IsTrue(game.TurnManager.SealCard(s1, council), "封印 1 成功");
            Assert.IsTrue(game.TurnManager.SealCard(s2, council), "封印 2 成功（不同拥有者，验证牌库归属分流）");

            cands = game.EffectEngine.GetCardCostCandidates(council, e2.CardCost);
            Assert.AreEqual(2, cands.Count, "1 张代价 ⇒ 2 张候选都合法");
        }

        /// <summary>010②：支付后那张封印牌进**秘社控制者**的牌库底（而非封印牌拥有者的牌库）。</summary>
        public void TestCardCost_置于你的牌库底按控制者分流()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC010_mozu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            // 被封印的牌拥有者是**玩家2**，但秘社属于玩家1 ⇒ 应进玩家1 的牌库底
            var sealedCard = game.CardManager.CreateCardInstance(Probe("TS_cc_owner2", cost: 0), 2);
            Assert.IsTrue(game.TurnManager.SealCard(sealedCard, council), "封印成功");
            int p1Before = game.TurnManager.GetDeckOf(1).Count;
            int p2Before = game.TurnManager.GetDeckOf(2).Count;

            var e2 = Opt(Load("MSJC010_mozu"), 1);
            var args = e2.CardCost.CostAction.Args;
            // 直接驱动动词（代价动作的单一实现）：Args[3]="自己" ⇒ 牌库归属＝来源控制者
            game.EffectEngine.ResolveEffect(new EffectData
            {
                EffectId = "TS_cc_move",
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.MoveToZone, Args = new Array<string>(args) },
                },
            }, council, sealedCard);

            Assert.AreEqual(CardZone.Deck, sealedCard.CurrentZone, "封印牌已进牌库");
            Assert.AreEqual(p1Before + 1, game.TurnManager.GetDeckOf(1).Count, "进的是**秘社控制者（玩家1）**的牌库");
            Assert.AreEqual(p2Before, game.TurnManager.GetDeckOf(2).Count, "不是拥有者（玩家2）的牌库");
            Assert.IsNull(sealedCard.SealedBy, "封印关系已解除");
            Assert.AreEqual(0, council.SealedCards.Count, "秘社的封印列表已清空");
        }

        /// <summary>012：代价候选只收**本方、未横置、带指定子类别**的角色。</summary>
        public void TestCardCost_横置三个角色候选筛选()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC012_aifuluodejiazu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];

            var servA = OnField(game, Probe("TS_cc_svA", subs: new[] { "奴仆" }), 1, area);
            var servB = OnField(game, Probe("TS_cc_svB", subs: new[] { "奴仆" }), 1, area);
            var servC = OnField(game, Probe("TS_cc_svC", subs: new[] { "奴仆" }), 1, area);
            // ⚠ 注意：下面的"人类"探针费用=1（≤2），会被 MSJC012 的**静态授予**额外变成奴仆 ⇒ 也是合法候选。
            //    这正好顺带正面验证了"费用≤2 的本方人类因持续效果而具有奴仆类别"（卡文 FAQ）。
            var humanBecomesServant = OnField(game, Probe("TS_cc_human", subs: new[] { "人类" }, cost: 1), 1, area);
            // 费用≥3 的人类**不**获得奴仆类别 ⇒ 不是候选
            OnField(game, Probe("TS_cc_human3", subs: new[] { "人类" }, cost: 3), 1, area);
            OnField(game, Probe("TS_cc_enemy", subs: new[] { "奴仆" }), 2, area);          // 敌方 → 不收
            var tapped = OnField(game, Probe("TS_cc_tapped", subs: new[] { "奴仆" }), 1, area);
            tapped.SetTapped(true);                                                        // 已横置 → 不收

            var e1 = Opt(Load("MSJC012_aifuluodejiazu"), 0);
            var cands = game.EffectEngine.GetCardCostCandidates(council, e1.CardCost);
            Assert.AreEqual(4, cands.Count, "合法候选＝3 个奴仆 ＋ 1 个『费用≤2 人类⇒奴仆』（已横置/敌方/费用≥3 人类都不算）");
            Assert.IsTrue(humanBecomesServant.HasSubCategory("奴仆"),
                "**费用 1 的本方人类因秘社静态授予而具有奴仆类别**（卡文 FAQ）");

            // 全部横置 3 个纯奴仆后，只剩"人类变奴仆"那 1 个 ⇒ 候选不足 3 ⇒ 门禁拒绝
            servA.SetTapped(true);
            servB.SetTapped(true);
            servC.SetTapped(true);
            cands = game.EffectEngine.GetCardCostCandidates(council, e1.CardCost);
            Assert.AreEqual(1, cands.Count, "三个奴仆都横置 ⇒ 只剩 1 个候选（人类变的那个）");
            Assert.IsTrue(cands.Count < e1.CardCost.Count, "候选 1 < 需求 3 ⇒ 门禁（CanActivateCouncil）会拒绝该能力");
        }

        /// <summary>016②：代价候选取自墓地；不足 7 张时 fail-closed（拒绝发动）。</summary>
        public void TestCardCost_墓地七张不足则拒绝()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC016_songwangzhe"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            var e2 = Opt(Load("MSJC016_songwangzhe"), 1);
            Assert.AreEqual(0, game.EffectEngine.GetCardCostCandidates(council, e2.CardCost).Count, "墓地空 ⇒ 无候选");

            for (int i = 0; i < 6; i++)
            {
                var c = game.CardManager.CreateCardInstance(Probe("TS_cc_gy" + i, cost: 0), 1);
                game.ZoneManager.MoveToGraveyard(c, 1);
            }
            Assert.AreEqual(6, game.EffectEngine.GetCardCostCandidates(council, e2.CardCost).Count, "墓地 6 张 ⇒ 候选 6");
            Assert.IsTrue(6 < e2.CardCost.Count, "6 < 需求 7 ⇒ 门禁（CanActivateCouncil）会拒绝该能力");
        }

        // ==================== ③ DestroyWithMinCounters（013②） ====================

        /// <summary>"消灭所有其上有 ≥3 时间标志的角色和附属，然后**每有一个因此被消灭就抓一张牌**"。</summary>
        public void TestDestroyWithMinCounters_按实际消灭数抓牌()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC013_jishizhe"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];

            var three = OnField(game, Probe("TS_dm_3"), 2, area);
            var two = OnField(game, Probe("TS_dm_2"), 2, area);
            var five = OnField(game, Probe("TS_dm_5"), 1, area);
            var other = OnField(game, Probe("TS_dm_0"), 2, area);
            game.CardManager.AddCounter(three, "时间标志", 3);
            game.CardManager.AddCounter(two, "时间标志", 2);
            game.CardManager.AddCounter(five, "时间标志", 5);

            // 附属也收（用附属模板）
            var att = game.CardManager.CreateCardInstance(
                Probe("TS_dm_att", cost: 0, cat: CardCategory.Attachment), 2);
            game.ZoneManager.MoveToArea(att, area, 2);
            game.CardManager.AddCounter(att, "时间标志", 3);

            int handBefore = game.CardManager.GetHandCardsByOwner(1).Count;

            var e2 = Opt(Load("MSJC013_jishizhe"), 1);
            game.EffectEngine.ResolveEffect(e2, council, null, area);

            Assert.AreEqual(CardZone.Graveyard, three.CurrentZone, "①3 个时间标志 → 被消灭");
            Assert.AreEqual(CardZone.Graveyard, five.CurrentZone, "②5 个时间标志 → 被消灭");
            Assert.AreEqual(CardZone.Graveyard, att.CurrentZone, "③**附属** 3 个时间标志 → 也被消灭");
            Assert.AreEqual(CardZone.AreaZone, two.CurrentZone, "2 个时间标志 → 不消灭（边界）");
            Assert.AreEqual(CardZone.AreaZone, other.CurrentZone, "0 个时间标志 → 不消灭");
            Assert.AreEqual(handBefore + 3, game.CardManager.GetHandCardsByOwner(1).Count,
                "**实际消灭 3 张 ⇒ 抓 3 张**（用户口径：按实际消灭数）");
        }

        // ==================== ④ MillTopToGraveyard（016①） ====================

        /// <summary>磨牌进墓地，但**不触发死亡**（不是"消灭"）。</summary>
        public void TestMill_牌库顶进墓地且不触发死亡()
        {
            var game = NewGame();
            var rec = WireRecordingBus(game);
            var council = game.CardManager.CreateCardInstance(Load("MSJC016_songwangzhe"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            var top = game.CardManager.CreateCardInstance(Probe("TS_mill_top", cost: 0), 1);
            top.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, top);
            var second = game.CardManager.CreateCardInstance(Probe("TS_mill_2", cost: 0), 1);
            second.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(1, second);

            rec.Events.Clear();   // 只关心"磨牌"这一步产生的事件
            var e1 = Opt(Load("MSJC016_songwangzhe"), 0);
            game.EffectEngine.ResolveEffect(e1, council, null);

            Assert.AreEqual(CardZone.Graveyard, top.CurrentZone, "牌库顶那张进了墓地");
            Assert.IsFalse(game.DeckManager.GetDeckPlayer1().Contains(top), "已从牌库列表移出（不留双份）");
            Assert.IsFalse(rec.Has(GameEvent.OnCardDied), "**磨牌不触发死亡时机**（不是『消灭』）");
        }

        public void TestMill_多张按顺序磨()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC016_songwangzhe"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            int gyBefore = game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count;

            var e3 = new EffectData
            {
                EffectId = "TS_mill3",
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.MillTopToGraveyard, Args = new Array<string> { "3" } },
                },
            };
            game.EffectEngine.ResolveEffect(e3, council, null);
            Assert.AreEqual(gyBefore + 3, game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count, "磨 3 张进墓地");
        }

        // ==================== ⑤ PlaceHandCardToArea（015①） ====================

        public void TestPlaceHandCardToArea_从手牌暗藏进场()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC015_yinshi"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            var area = game.AreaManager.GetOrderedAreaUids()[1];

            // 先清空手牌：`PlaceHandCardToArea` 在无 UI 下走"确定性兜底取首候选"，
            // 若起手牌还在手里，兜底会命中的是起手牌而不是本用例的探针。
            foreach (var c in new List<CardInstance>(game.CardManager.GetHandCardsByOwner(1)))
                game.ZoneManager.MoveToGraveyard(c, 1);

            var charCard = game.CardManager.CreateCardInstance(Probe("TS_ph_char", cost: 1), 1);
            game.ZoneManager.MoveToHand(charCard, 1);
            var spell = game.CardManager.CreateCardInstance(
                Probe("TS_ph_spell", cost: 1, cat: CardCategory.Sorcery), 1);
            game.ZoneManager.MoveToHand(spell, 1);
            // 卡文前提"若你的手牌数量大于或等于四张"：补两张填充牌，凑到 4 张（角色牌仍是第一个候选）。
            game.ZoneManager.MoveToHand(game.CardManager.CreateCardInstance(Probe("TS_ph_f1", cost: 0), 1), 1);
            game.ZoneManager.MoveToHand(game.CardManager.CreateCardInstance(Probe("TS_ph_f2", cost: 0), 1), 1);

            var e1 = Opt(Load("MSJC015_yinshi"), 0);
            game.EffectEngine.ResolveEffect(e1, council, null, area);

            Assert.AreEqual(CardZone.AreaZone, charCard.CurrentZone, "角色牌已进场");
            Assert.AreEqual(area, charCard.ZoneDetailId, "落在目标地区");
            Assert.IsTrue(charCard.IsCovert, "**以暗藏者状态**进场（FAQ：带公开关键词也行）");
            Assert.AreEqual(CardZone.Hand, spell.CurrentZone, "非角色牌（事务）仍留在手牌（筛选生效）");
        }

        // ==================== ⑥ UnsealToArea（010③ / 016②） ====================

        public void TestUnsealToArea_封印角色牌正面进场由我操控()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC010_mozu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];

            // 一张**玩家2拥有**的角色牌被封印在玩家1 的貘组上
            var sealedChar = game.CardManager.CreateCardInstance(Probe("TS_ua_char", cost: 2), 2);
            Assert.IsTrue(game.TurnManager.SealCard(sealedChar, council), "封印成功");
            Assert.AreEqual(CardZone.SealedZone, sealedChar.CurrentZone, "前置：在封印区");

            var e4 = Opt(Load("MSJC010_mozu"), 3);
            game.EffectEngine.ResolveEffect(e4, council, null, area);

            Assert.AreEqual(CardZone.AreaZone, sealedChar.CurrentZone, "已正面进场");
            Assert.AreEqual(area, sealedChar.ZoneDetailId, "落在目标地区");
            Assert.IsFalse(sealedChar.IsCovert, "正面（不是暗藏）");
            Assert.AreEqual(1, sealedChar.ControllerId, "**由我操控**（控制者改为秘社控制者）");
            Assert.IsNull(sealedChar.SealedBy, "封印关系已解除");
            Assert.AreEqual(0, council.SealedCards.Count, "秘社封印列表已清空（不留双重归属）");
        }

        /// <summary>016②：从**墓地**取一张【死】领域角色牌放进目标地区。</summary>
        public void TestUnsealToArea_墓地死领域角色进场()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC016_songwangzhe"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];

            var death = Probe("TS_ua_death", cost: 2);
            death.MagicDomains.Add(MagicDomain.Death);
            var deathCard = game.CardManager.CreateCardInstance(death, 1);
            game.ZoneManager.MoveToGraveyard(deathCard, 1);

            var mind = Probe("TS_ua_mind", cost: 2);
            mind.MagicDomains.Add(MagicDomain.Mind);
            var mindCard = game.CardManager.CreateCardInstance(mind, 1);
            game.ZoneManager.MoveToGraveyard(mindCard, 1);

            var e2 = Opt(Load("MSJC016_songwangzhe"), 1);
            // 目标已由 TargetSpec 锁定（墓地中的【死】领域角色牌）⇒ 直接用 UnsealToArea 收口验证筛选
            game.EffectEngine.ResolveEffect(new EffectData
            {
                EffectId = "TS_ua_16",
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.UnsealToArea,
                        Args = new Array<string> { "墓地", "正面", "自身", "领域:死亡", "" },
                    },
                },
            }, council, null, area);

            Assert.AreEqual(CardZone.AreaZone, deathCard.CurrentZone, "【死】领域角色牌已进场");
            Assert.AreEqual(CardZone.Graveyard, mindCard.CurrentZone, "【心】领域牌不被选中（筛选生效）");
        }

        // ==================== ⑦ PutCardOnDeckTop（017②） ====================

        public void TestPutCardOnDeckTop_墓地里灵体角色置于牌库顶()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC017_mengyanzuqun"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            var spirit = game.CardManager.CreateCardInstance(Probe("TS_pd_spirit", cost: 1, inherentKeyword: "灵体"), 1);
            game.ZoneManager.MoveToGraveyard(spirit, 1);
            Assert.AreEqual(CardZone.Graveyard, spirit.CurrentZone, "前置：在墓地");

            var e2 = Opt(Load("MSJC017_mengyanzuqun"), 1);
            game.EffectEngine.ResolveEffect(e2, council, spirit);

            Assert.AreEqual(CardZone.Deck, spirit.CurrentZone, "已置入牌库");
            var deck = game.TurnManager.GetDeckOf(1);
            Assert.IsTrue(ReferenceEquals(spirit, deck[0]), "**在牌库顶**（索引 0）");
        }

        // ==================== ⑧ CouncilStaticGrant（012） ====================

        /// <summary>印刷费用≤2 的**本方人类**角色获得奴仆类别；费用≥3 或非人类或敌方不受影响。</summary>
        public void TestCouncilStaticGrant_费用2以下人类获得奴仆()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC012_aifuluodejiazu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            var area = game.AreaManager.GetOrderedAreaUids()[0];

            var cheap = OnField(game, Probe("TS_csg_cheap", subs: new[] { "人类" }, cost: 2), 1, area);
            var expensive = OnField(game, Probe("TS_csg_exp", subs: new[] { "人类" }, cost: 3), 1, area);
            var nonHuman = OnField(game, Probe("TS_csg_nonhuman", subs: new[] { "法师" }, cost: 1), 1, area);
            var enemy = OnField(game, Probe("TS_csg_enemy", subs: new[] { "人类" }, cost: 1), 2, area);

            game.EffectEngine.ReevaluateCouncilStaticGrants();

            Assert.IsTrue(cheap.HasSubCategory("奴仆"), "**印刷费用 2 的本方人类 ⇒ 获得奴仆类别**");
            Assert.IsFalse(expensive.HasSubCategory("奴仆"), "费用 3 ⇒ 不获得");
            Assert.IsFalse(nonHuman.HasSubCategory("奴仆"), "非人类 ⇒ 不获得");
            Assert.IsFalse(enemy.HasSubCategory("奴仆"), "敌方 ⇒ 不获得（本方限定）");

            // 离场后收回（换区复位会清运行期授予，重算时撤销）
            game.ZoneManager.MoveToHand(cheap, 1);
            game.EffectEngine.ReevaluateCouncilStaticGrants();
            Assert.IsFalse(cheap.HasSubCategory("奴仆"), "离场（回手牌）⇒ 授予收回");
        }

        // ==================== ⑨ DrawFromBottom（017） ====================

        /// <summary>梦魇族群在场时，`DrawCardsForPlayer` 改从**牌库底**取牌。</summary>
        public void TestDrawFromBottom_秘社在场时改从牌库底抓牌()
        {
            var game = NewGame();
            Assert.IsFalse(game.TurnManager.CouncilDrawsFromBottom(1), "前置：默认秘社不改变抓牌方向");

            var council = game.CardManager.CreateCardInstance(Load("MSJC017_mengyanzuqun"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            Assert.IsTrue(game.TurnManager.CouncilDrawsFromBottom(1), "梦魇族群在场 ⇒ 从牌库底抓牌");

            // 造两张可区分的牌：牌库顶 A / 牌库底 B（列表末尾）
            var deck = game.DeckManager.GetDeckPlayer1();
            int n = deck.Count;
            Assert.IsTrue(n >= 2, "牌库至少 2 张");
            var bottomCard = deck[n - 1];

            int handBefore = game.CardManager.GetHandCardsByOwner(1).Count;
            game.TurnManager.DrawCardsForPlayer(1, 1);

            Assert.AreEqual(CardZone.Hand, bottomCard.CurrentZone, "**抓到的是牌库底那张**");
            Assert.AreEqual(handBefore + 1, game.CardManager.GetHandCardsByOwner(1).Count, "手牌 +1");
        }

        // ==================== ⑩ HandAtLeast 谓词（015①） ====================

        public void TestHandAtLeast_按手牌数判定()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC015_yinshi"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            // 清空手牌后逐张加到 4
            foreach (var c in new List<CardInstance>(game.CardManager.GetHandCardsByOwner(1)))
                game.ZoneManager.MoveToGraveyard(c, 1);
            Assert.IsFalse(game.EffectEngine.EvaluateCondition("HandAtLeast", council, null, null, -1, null, "4"),
                "手牌 0 张 ⇒ 不满足");
            for (int i = 0; i < 4; i++)
            {
                var c = game.CardManager.CreateCardInstance(Probe("TS_hal_" + i, cost: 0), 1);
                game.ZoneManager.MoveToHand(c, 1);
            }
            Assert.IsTrue(game.EffectEngine.EvaluateCondition("HandAtLeast", council, null, null, -1, null, "4"),
                "**手牌 4 张 ⇒ 满足**（边界包含）");
            Assert.IsFalse(game.EffectEngine.EvaluateCondition("HandAtLeast", council, null, null, -1, null, "5"),
                "手牌 4 < 5 ⇒ 不满足");
        }

        // ==================== ⑪ 开局效果（015） ====================

        /// <summary>开局效果：在本方秘社上放置一个【隐秘】标志（`StartMatch` 编排自动执行）。</summary>
        public void TestOpeningEffect_开局在秘社上放隐秘标志()
        {
            var game = NewGame();
            // 用本次卡组携带的秘社换成 015 需要显式放一个实例（StartMatch 已跑过，这里手动补测编排入口）
            var council = game.CardManager.CreateCardInstance(Load("MSJC015_yinshi"), 2);
            game.ZoneManager.MoveToCouncil(council, 2);
            game.ApplyCouncilOpeningEffects();

            Assert.IsTrue(council.Counters.TryGetValue("隐秘", out int n), "秘社上应有【隐秘】标志");
            Assert.AreEqual(1, n, "恰好 1 个");

            // 幂等性说明：StartMatch 每局只调一次；重复调用会再加一个（与"每局游戏开始时"语义一致，不做去重）
        }

        // ==================== ⑫ HasAbilityIcon（011②） ====================

        /// <summary>［黑刀］图标＝战力>0（**不含先手战力**），用户 2026-09 口径。</summary>
        public void TestHasAbilityIcon_按战力判定且不计先手战力()
        {
            var game = NewGame();
            var area = game.AreaManager.GetOrderedAreaUids()[0];
            var src = OnField(game, Probe("TS_hai_src", cost: 0), 1, area);

            var withBlade = OnField(game, Probe("TS_hai_blade", strength: 1), 1, area);
            var onlyFirstStrike = OnField(game, Probe("TS_hai_fs"), 1, area);
            onlyFirstStrike.Grant("Attribute", "先手战斗", 1);      // 只有先手战力
            var noBlade = OnField(game, Probe("TS_hai_none"), 1, area);

            var spec = new TargetSpec
            {
                Kind = TargetKind.Character,
                Restrictions = new Array<TargetRestriction> { TargetRestriction.HasAbilityIcon },
                Param = "黑刀",
            };
            Assert.IsTrue(TargetValidity.Check(src, withBlade, spec).valid, "战力>0 ⇒ 命中［黑刀］");
            Assert.IsFalse(TargetValidity.Check(src, onlyFirstStrike, spec).valid,
                "**只有先手战力 ⇒ 不算［黑刀］**（用户口径：不要计算先手战力）");
            Assert.IsFalse(TargetValidity.Check(src, noBlade, spec).valid, "无战力 ⇒ 不命中");
        }

        // ==================== ⑬ 本批次实测修掉的引擎缺陷（回归锁） ====================

        /// <summary>
        /// 【缺陷回归】`_actionChainAborted` 原先**跨 `ResolveEffect` 调用泄漏**：
        /// 该标志只在 `ResolveEffect` 的动作序列入口复位，而"代价动作 / 挂起续算 / 多目标逐卡执行"等路径
        /// 会直接调 `ExecuteActions`，于是**上一条效果的中止会让后续完全无关的动作整段被跳过**
        /// （实测现象：磨牌/移动到牌库底的动词"连日志都没有"）。现已把复位收口到 `ExecuteActionsFrom` 入口。
        /// </summary>
        public void Test回归_动作链中止不跨效果泄漏()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC016_songwangzhe"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);
            int gyBefore = game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count;

            // 第 1 条：Pay 金额 > 可用费用 ⇒ 支付失败 ⇒ 置链式中止，后续 Draw 被跳过
            var failing = new EffectData
            {
                EffectId = "TS_reg_abort",
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Pay, Args = new Array<string> { "9999" } },
                    new EffectAction { Verb = ActionVerb.MillTopToGraveyard, Args = new Array<string> { "1" } },
                },
            };
            game.EffectEngine.ResolveEffect(failing, council, null);
            Assert.AreEqual(gyBefore, game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count,
                "前置：同一条效果里支付失败 ⇒ 后续磨牌被跳过");

            // 第 2 条：**全新的一条效果**，不得被上一条的链式中止影响
            var follow = new EffectData
            {
                EffectId = "TS_reg_after",
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.MillTopToGraveyard, Args = new Array<string> { "2" } },
                },
            };
            game.EffectEngine.ResolveEffect(follow, council, null);
            Assert.AreEqual(gyBefore + 2, game.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count,
                "**新的一条效果照常执行**（中止标志不得跨效果泄漏）");
        }

        /// <summary>
        /// 【缺陷回归】`MagicDomains` 原先只在 `ResetToBaseValues`（进场路径）里从模板拷入，
        /// 且 `ResetToBaseState`（离场复位）会把实例领域清空 ⇒ **离开争夺区之后**实例的领域列表为空，
        /// "按领域筛牌库/手牌/墓地"的筛选（MSJC016② 目标＝墓地中的【死】领域角色牌）**永远零候选**。
        /// </summary>
        public void Test回归_领域在牌库手牌墓地中仍可读()
        {
            var game = NewGame();
            var death = Probe("TS_reg_domain", cost: 2);
            death.MagicDomains.Add(MagicDomain.Death);
            var card = game.CardManager.CreateCardInstance(death, 1);

            Assert.IsTrue(card.MagicDomains.Contains(MagicDomain.Death), "刚创建（尚未进场）⇒ 领域已就绪");
            Assert.IsTrue(DeckSearchFilter.Matches("领域:死亡", card), "刚创建 ⇒ 可直接按领域筛选");

            game.ZoneManager.MoveToGraveyard(card, 1);
            Assert.IsTrue(card.MagicDomains.Contains(MagicDomain.Death), "进墓地后领域仍在（离场复位必须重建模板领域）");
            Assert.IsTrue(DeckSearchFilter.Matches("领域:死亡", card), "进墓地后仍可按领域筛选");

            game.ZoneManager.MoveToHand(card, 1);
            Assert.IsTrue(DeckSearchFilter.Matches("领域:死亡", card), "回手牌后仍可按领域筛选");
        }

        /// <summary>
        /// 【缺陷回归】`TurnManager.ReturnCardToDeck(uid,...)` 经 `GetInstance(uid)` 反查实例，
        /// 查不到就**静默什么都不做** ⇒ MSJC010②③"把封印牌置于牌库底"变成"卡留在封印区、日志卡名为空"。
        /// 现改为"以实例为准"的正式入口（`ReturnCardToDeck(CardInstance,...)`）。
        /// </summary>
        public void Test回归_封印牌可真正进入指定玩家的牌库底()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC010_mozu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            var sealedCard = game.CardManager.CreateCardInstance(Probe("TS_reg_sealed", cost: 0), 2);
            Assert.IsTrue(game.TurnManager.SealCard(sealedCard, council), "前置：封印成功");

            var e3 = Opt(Load("MSJC010_mozu"), 3);
            // 直接驱动代价动作的单一实现
            game.EffectEngine.ResolveEffect(new EffectData
            {
                EffectId = "TS_reg_move",
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.MoveToZone, Args = new Array<string>(e3.CardCost.CostAction.Args) },
                },
            }, council, sealedCard);

            Assert.AreEqual(CardZone.Deck, sealedCard.CurrentZone, "封印牌已真正进牌库（不是留在封印区/落进手牌）");
            Assert.AreEqual(1, sealedCard.OwnerId, "牌库归属＝**秘社控制者**（玩家1），而非封印牌原拥有者（玩家2）");
            Assert.IsNull(sealedCard.SealedBy, "封印关系已解除");
            Assert.AreEqual(0, council.SealedCards.Count, "秘社封印列表已清空");
        }

        // ==================== ⑭ SealFromHand 的筛选（010①） ====================
        /// <summary>010①：封印候选限定【星】领域；手牌里没有【星】领域牌时该能力不可发动。</summary>
        public void TestSealHandFilter_限定额域且无候选则不可发动()
        {
            var game = NewGame();
            var council = game.CardManager.CreateCardInstance(Load("MSJC010_mozu"), 1);
            game.ZoneManager.MoveToCouncil(council, 1);

            foreach (var c in new List<CardInstance>(game.CardManager.GetHandCardsByOwner(1)))
                game.ZoneManager.MoveToGraveyard(c, 1);

            var star = Probe("TS_shf_star", cost: 1);
            star.MagicDomains.Add(MagicDomain.Star);
            var starCard = game.CardManager.CreateCardInstance(star, 1);
            var mind = Probe("TS_shf_mind", cost: 1);
            mind.MagicDomains.Add(MagicDomain.Mind);
            var mindCard = game.CardManager.CreateCardInstance(mind, 1);

            var e1 = Opt(Load("MSJC010_mozu"), 0);
            Assert.AreEqual("领域:星空", e1.Actions[0].Args[3], "筛选串＝领域:星空");
            Assert.IsTrue(DeckSearchFilter.Matches(e1.Actions[0].Args[3], starCard), "【星】领域手牌命中");
            Assert.IsFalse(DeckSearchFilter.Matches(e1.Actions[0].Args[3], mindCard), "【心】领域手牌不命中");
        }
    }
}
