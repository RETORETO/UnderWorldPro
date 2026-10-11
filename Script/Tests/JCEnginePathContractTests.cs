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
    /// 【判据㉖–㉙ 的行为级契约套件】把"最近三批实测 BUG 暴露的检查盲区"变成**可复跑的行为断言**：
    ///  · **㉘ 旁路同判**：同一判定（屏障 / 附属白名单 / 本地区）在**全部候选与合法性路径**上给出一致结论
    ///    （候选枚举①触发②模式③激活④打出合法性⑤结算复检）；
    ///  · **㉗ 多声明 OR**：卡上并列多条目标声明时，各声明的合法目标都能走通"打出 → 入堆 → 结算"；
    ///  · **㉙ 落地路径一致**：同一张结附卡经 **生产 `PlayCard` / PM `attach` / PM `attacharea`** 落地后语义一致；
    ///  · **㉙ 交互收尾**：回调型挂起（数值窗 / 是-否窗）回包后必须**做结算收尾并释放决策持锁**（否则联机不下发快照、
    ///    单机界面不刷新；实测 BUG：JC083 UI 不刷新）；
    ///  · **㉖/㉙ 联机孪生入口**：声明了 `PlayChoice` 的卡在**联机入口**（服务端驱动）同样可识别并完成落地。
    /// 所有场景都用同一套共享桩（`JCTestStubs.cs`），避免各套件口径漂移。
    /// </summary>
    public class JCEnginePathContractTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        // ==================== 基建 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 71 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(2);
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

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(inst, area, playerId);
            return inst;
        }

        /// <summary>探针角色（可带组织 / 自带袭击N / 指定调查值）。</summary>
        private static PlayerCardData Probe(string uid, int strike = 0, string org = null, int investigation = 0)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Character, Defense = 1,
                Investigation = investigation,
            };
            if (org != null) tmpl.Organization = org;
            if (strike > 0)
            {
                var s = KeywordEffects.Strike(strike);
                s.EffectId = uid + "_strike";
                tmpl.EffectDataList.Add(s);
            }
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>探针附属：可选"激活能力（目标＝角色）"、可选子类别。</summary>
        private static PlayerCardData AttachProbe(string uid, bool withActivation = false, params string[] subCategories)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针附属_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Attachment, Defense = -1,
            };
            // 打出目标声明（角色）：否则 `GetRequiresTarget()` 为 false ⇒ `IsLegalPlayTarget` 在
            // 白名单判定**之前**就返回 false，白名单路径的断言会变成"恒 false"的假通过。
            tmpl.TargetRules = new Array<TargetRule> { TargetRule.Make(TargetKind.Character) };
            foreach (var s in subCategories) tmpl.SubCategories.Add(s);
            if (withActivation)
            {
                tmpl.EffectDataList.Add(new EffectData
                {
                    EffectId = uid + "_act",
                    Timing = EffectTiming.AttachmentActivated,
                    ActivateWindow = "快速",
                    Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                    Duration = "瞬时",
                });
            }
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>探针事务卡：`OnSorceryCast` + 目标＝角色（用于"打出合法性"路径）。</summary>
        private static PlayerCardData SorceryProbe(string uid)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针事务_" + uid, Cost = 0,
                CategoryFlag = CardCategory.Sorcery, Defense = -1,
            };
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = uid + "_f",
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PreventDamage, Args = new Array<string> { "目标" } },
                },
                Duration = "瞬时",
            });
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>合成一份"选择一项"声明（模式目标＝角色），用于模式候选路径。</summary>
        private static EffectData MakeCharacterModeEffect()
        {
            var e = new EffectData { EffectId = "TS_mode_char", Timing = EffectTiming.OnSorceryCast };
            e.Modes.Add(new EffectMode
            {
                Label = "结附于目标角色",
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
            });
            return e;
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList) if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_ct_asset_{playerId}_{i}", CardName = "探针资产",
                    Cost = 0, CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl.Colors.Add(color);
                tmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        /// <summary>连续让过直到堆叠清空。</summary>
        private static void ResolveAll(GameInitializer game)
        {
            for (int i = 0; i < 60 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>推进到行动步骤并取得优先权（附属＝A 档打出的前置）。</summary>
        private static void AdvanceToActionStep(GameInitializer game, int playerId)
        {
            for (int i = 0; i < 80; i++)
            {
                bool ready = (game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionFirst
                              || game.TurnManager.CurrentPhase == global::TurnManager.TurnPhase.ActionSecond)
                             && game.TurnManager.PriorityPlayerId == playerId;
                if (ready) break;
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            }
        }

        private static bool FindEntry(GameInitializer game, string prefix)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId != null && e.EffectDataEffect.EffectId.StartsWith(prefix)) return true;
            return false;
        }

        /// <summary>造"屏障宿主"场面（宿主 + JC073 结附，走生产同款单点）。</summary>
        private static CardInstance MakeBarrierHost(GameInitializer game, string area, string uid = "TS_ct_host")
        {
            var host = OnField(game, Probe(uid), 1, area);
            var dream = game.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            game.ZoneManager.AttachAndRegister(dream, host);
            return host;
        }

        // ==================== ㉘ 旁路同判：屏障 ====================

        /// <summary>
        /// 【判据㉘】**屏障**必须在四条判定路径上一致：
        ///  ① 触发取目标候选（袭击）② 模式卡候选 ③ 激活目标判定 ④ 打出目标合法性。
        /// </summary>
        public void TestContract_屏障_候选与合法性四路径一致()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var host = MakeBarrierHost(game, area);
            var bystander = OnField(game, Probe("TS_ct_bystander"), 1, area);
            Assert.IsTrue(host.HasActiveKeyword("屏障"), "前置：宿主已获得【屏障】");

            // ① 触发取目标候选：P2 的"袭击1"现身
            RecordingTriggerView.LastGame = game;
            var view = new RecordingTriggerView();
            game.TurnManager.SetTriggerView(view);
            var striker = OnField(game, Probe("TS_ct_striker", strike: 1), 2, area);
            striker.SetCovert(true);
            game.CardManager.RevealCard(striker);
            Assert.IsTrue(view.Candidates.Contains(bystander.UID), "① 对照组（无屏障）应在袭击候选里");
            Assert.IsFalse(view.Candidates.Contains(host.UID), "① 带【屏障】的宿主不得出现在袭击候选里");

            // ② 模式卡候选：来源＝P2 的卡，候选应排掉屏障宿主
            var p2att = game.CardManager.CreateCardInstance(AttachProbe("TS_ct_mode_src"), 2);
            var availability = game.TurnManager.GetModeAvailability(MakeCharacterModeEffect(), p2att);
            Assert.IsTrue(availability.Count > 0, "② 模式可用性应可求值");
            Assert.IsTrue(availability[0], "② 场上仍有非屏障的合法目标（bystander/striker）⇒ 该模式可用");
            // 反向（隔离场景）：把 P1 对照角色与 P2 袭击者都移走 ⇒ 场上只剩屏障宿主
            // ⇒ 该模式对 P2 来源应**不可用**（候选侧已排除屏障）
            game.CardManager.MoveCardToGraveyard(bystander);
            game.CardManager.MoveCardToGraveyard(striker);
            var availability2 = game.TurnManager.GetModeAvailability(MakeCharacterModeEffect(), p2att);
            Assert.IsFalse(availability2[0], "② 仅剩带【屏障】的敌方目标 ⇒ 模式不可用（候选侧已排除屏障）");

            // ③ 激活目标判定：来源＝P2 附属，目标＝P1 屏障宿主
            var actAtt = game.CardManager.CreateCardInstance(AttachProbe("TS_ct_act", withActivation: true), 2);
            var ability = FindEffect(actAtt.PlayerTemplate, "TS_ct_act_act");
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(actAtt, ability, host,
                    game.CardManager, game.PlayerManager), "③ 带【屏障】的目标对敌方激活能力不合法");

            // ④ 打出目标合法性：来源＝P2 事务卡（目标角色）
            var spell = game.CardManager.CreateCardInstance(SorceryProbe("TS_ct_spell"), 2);
            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(spell, host),
                "④ 带【屏障】的敌方目标对事务卡打出不合法");

            // 对照：无屏障场景下三条路径都应放行（证明差异确实来自屏障，而不是"恒 false"）
            var game2 = NewGame();
            string area2 = FirstArea(game2);
            var host2 = OnField(game2, Probe("TS_ct_host2"), 1, area2);
            var actAtt2 = game2.CardManager.CreateCardInstance(AttachProbe("TS_ct_act2", withActivation: true), 2);
            var ability2 = FindEffect(actAtt2.PlayerTemplate, "TS_ct_act2_act");
            var spell2 = game2.CardManager.CreateCardInstance(SorceryProbe("TS_ct_spell2"), 2);
            var p2att2 = game2.CardManager.CreateCardInstance(AttachProbe("TS_ct_mode_src2"), 2);
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(actAtt2, ability2, host2,
                    game2.CardManager, game2.PlayerManager), "对照③：无屏障 ⇒ 激活目标合法");
            Assert.IsTrue(game2.CardManager.IsLegalPlayTarget(spell2, host2), "对照④：无屏障 ⇒ 打出目标合法");
            var availability3 = game2.TurnManager.GetModeAvailability(MakeCharacterModeEffect(), p2att2);
            Assert.IsTrue(availability3[0], "对照②：无屏障 ⇒ 模式可用");
        }

        /// <summary>
        /// 【判据㉘·第⑤路】结算复检："**先被指定为目标、之后才获得屏障**"必须在结算时被拦下
        /// （袭击伤害不落地）。
        /// </summary>
        public void TestContract_屏障_结算复检_先指定后获屏障()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var host = OnField(game, Probe("TS_ct_late_host"), 1, area);

            RecordingTriggerView.LastGame = game;
            var view = new RecordingTriggerView { PreferredUid = host.UID };
            game.TurnManager.SetTriggerView(view);
            var striker = OnField(game, Probe("TS_ct_late_striker", strike: 1), 2, area);
            striker.SetCovert(true);
            game.CardManager.RevealCard(striker);   // 此刻宿主**还没有**屏障 ⇒ 目标被成功指定
            Assert.IsTrue(view.Candidates.Contains(host.UID), "前置：指定时宿主还没有屏障 ⇒ 可被选中");
            Assert.IsTrue(FindEntry(game, "TS_ct_late_striker_strike"), "前置：袭击触发已入堆叠（目标＝宿主）");

            // 结算前授予宿主【屏障】⇒ 结算复检必须判目标不合法
            var dream = game.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            game.ZoneManager.AttachAndRegister(dream, host);
            Assert.IsTrue(host.HasActiveKeyword("屏障"), "前置：结算前宿主已获得【屏障】");

            ResolveAll(game);
            Assert.AreEqual(0, host.Damage, "⑤ 结算复检拦下 ⇒ 袭击伤害不落地（先指定、后获屏障）");
        }

        // ==================== ㉘ 旁路同判：附属白名单 ====================

        /// <summary>
        /// 【判据㉘/⑤/⑯】JC081「除装备与祝福外不受附属」的**适用范围**：
        ///  · 附属卡：非白名单子类别 ⇒ 不合法；命中白名单 ⇒ 合法；
        ///  · **事务卡**（非"结附"）：不受该静止规则限制。
        /// </summary>
        public void TestContract_白名单_只约束附属且仅限结附路径()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc081 = OnField(game, Load("JC081_yaluoer"), 1, area);

            var plainAtt = game.CardManager.CreateCardInstance(AttachProbe("TS_ct_wh_plain"), 1);
            var equipAtt = game.CardManager.CreateCardInstance(AttachProbe("TS_ct_wh_equip", false, "装备"), 1);
            var spell = game.CardManager.CreateCardInstance(Load("JC078_guangnengdun"), 1);

            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(plainAtt, jc081),
                "④ 非白名单附属（书籍/法术等）不得以 JC081 为目标");
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(equipAtt, jc081),
                "④ 白名单命中的附属（装备）合法");
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(spell, jc081),
                "④ **事务卡不受『不受附属』限制**（判据⑤/⑯ 适用范围；BUG2 回归）");
        }

        // ==================== ㉗ 多声明 OR ====================

        /// <summary>
        /// 【判据㉗】JC082 的两条目标声明（角色 / 本方秘社）都必须能走通
        /// "打出 → 入堆 → 结算"（D16 复检必须按任一命中即合法）。
        /// </summary>
        public void TestContract_多声明OR_两类别各自可结算()
        {
            // (a) 收藏：结附本方秘社（历史上被"首条规则＝角色"误判丢失 ⇒ 卡进墓地）
            var game = NewGame();
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1)[0];
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game.ZoneManager.MoveToHand(book, 1);
            AdvanceToActionStep(game, 1);
            Assert.IsTrue(game.PlayerActionService.PlayCard(1, book.UID, null, council.UID), "收藏：打出应成功");
            ResolveAll(game);
            Assert.AreEqual(CardZone.AttachedZone, book.CurrentZone, "收藏：应结附（不得进弃牌堆）");
            Assert.IsTrue(ReferenceEquals(book.AttachedTo, council), "收藏：绑定到本方秘社");

            // (b) 借阅：结附目标角色
            var game2 = NewGame();
            GiveAssets(game2, 1, 3, (CardColor)2);
            var host = OnField(game2, Probe("TS_ct_or_host"), 1, FirstArea(game2));
            var book2 = game2.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game2.ZoneManager.MoveToHand(book2, 1);
            AdvanceToActionStep(game2, 1);
            Assert.IsTrue(game2.PlayerActionService.PlayCard(1, book2.UID, null, host.UID), "借阅：打出应成功");
            ResolveAll(game2);
            Assert.AreEqual(CardZone.AttachedZone, book2.CurrentZone, "借阅：应结附于角色");
            Assert.IsTrue(ReferenceEquals(book2.AttachedTo, host), "借阅：绑定到目标角色");
            Assert.IsTrue(host.HasIcon("白眼"), "借阅：宿主获得［白眼］（授予宿主生效）");
        }

        // ==================== ㉙ 落地路径一致 ====================

        /// <summary>
        /// 【判据㉙】同一张结附卡的三种落地路径（生产 `PlayCard` / PM `attach` / PM `attacharea`）
        /// 必须语义一致：绑定正确 + **持续授予生效**（"授予宿主"类效果在任何路径都不能静默失效）。
        /// </summary>
        public void TestContract_落地路径一致_生产与PM命令()
        {
            // (a) 生产路径：打出 JC073 结附宿主
            var g1 = NewGame();
            string a1 = FirstArea(g1);
            GiveAssets(g1, 1, 4, (CardColor)2);
            var host1 = OnField(g1, Probe("TS_ct_path_prod"), 1, a1);
            var dream1 = g1.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            g1.ZoneManager.MoveToHand(dream1, 1);
            AdvanceToActionStep(g1, 1);
            Assert.IsTrue(g1.PlayerActionService.PlayCard(1, dream1.UID, null, host1.UID), "生产：打出结附成功");
            ResolveAll(g1);
            Assert.IsTrue(host1.HasActiveKeyword("屏障"), "生产路径：宿主获得【屏障】");

            // (b) PM attach
            var g2 = NewGame();
            string a2 = FirstArea(g2);
            var host2 = OnField(g2, Probe("TS_ct_path_pm"), 1, a2);
            var dream2 = g2.CardManager.CreateCardInstance(Load("JC073_suzubumengwang"), 1);
            g2.ZoneManager.MoveToArea(dream2, a2, 1);
            Assert.IsTrue(g2.PMCommandManager.TryExecuteCommand($"attach {dream2.UID} {host2.UID}", out string e2),
                "PM attach 应成功：" + e2);
            Assert.IsTrue(host2.HasActiveKeyword("屏障"), "PM attach 路径：宿主获得【屏障】（单点化后与生产一致）");

            // (c) PM attacharea（结附于地区：无宿主，但必须有 ZoneDetailId 且进附属区）
            var g3 = NewGame();
            string a3 = FirstArea(g3);
            var att3 = g3.CardManager.CreateCardInstance(AttachProbe("TS_ct_path_area"), 1);
            g3.ZoneManager.MoveToArea(att3, a3, 1);
            Assert.IsTrue(g3.PMCommandManager.TryExecuteCommand($"attacharea {att3.UID} 1", out string e3),
                "PM attacharea 应成功：" + e3);
            Assert.AreEqual(CardZone.AttachedZone, att3.CurrentZone, "结附地区：进附属区");
            Assert.IsNull(att3.AttachedTo, "结附地区：无宿主");
            Assert.AreEqual(a3, att3.ZoneDetailId, "结附地区：ZoneDetailId＝目标地区");
        }

        // ==================== ㉙ 交互收尾（数值窗 / 是-否窗） ====================

        /// <summary>
        /// 【判据㉙】数值窗是**回调型挂起**：回包后必须做结算收尾（`OnAsyncSettlementCompleted`）
        /// 并释放决策持锁。覆盖**单机 UI 桩**与**联机网关桩**两条路径（实测 BUG：联机漏了收尾 ⇒ UI 不刷新）。
        /// </summary>
        public void TestContract_交互收尾_数值窗单机与联机()
        {
            // (a) 单机：UI 桩回包
            RunNumberWindowCase(online: false);
            // (b) 联机：网关桩回包（引擎走 InputGateway 分支）
            RunNumberWindowCase(online: true);
        }

        private void RunNumberWindowCase(bool online)
        {
            var game = NewGame();
            string area = FirstArea(game);
            var sphinx = OnField(game, Load("JC083_ruizhidesifenkesi"), 1, area);
            var effect = FindEffect(sphinx.PlayerTemplate, "JC083_A_GuessTopDeckCost");
            Assert.IsNotNull(effect, "JC083 应有『选择一个数字』效果");

            var topTmpl = new PlayerCardData
            {
                UID = online ? "TS_ct_num_top_ol" : "TS_ct_num_top", CardName = "探针顶牌",
                Cost = 3, CategoryFlag = CardCategory.Character, Defense = 1,
            };
            topTmpl._effectsInjected = true;
            var top = game.CardManager.CreateCardInstance(topTmpl, 1);
            game.ZoneManager.MoveToDeckTop(top, 1);

            var prevHooks = EffectEngine.Instance.UIHooks;
            int settled = 0;
            System.Action onSettled = () => settled++;
            game.TurnManager.OnAsyncSettlementCompleted += onSettled;
            var gw = online ? new AutoAnswerGateway { NumberAnswer = 3 } : null;
            var stub = online ? null : new ConfirmStub { NumberAnswer = 3 };
            try
            {
                // 【测试卫生·必需】用例自带 UI 钩子状态，不继承别的套件残留：
                // 联机分支要求 `UIHooks == null`（否则引擎走单机弹窗分支，永远不发网关请求）。
                EffectEngine.Instance.UIHooks = stub;
                if (online)
                {
                    game.SetInputGateway(gw);
                    Assert.IsNull(EffectEngine.Instance.UIHooks, "前置：联机用例要求单机 UI 钩子为空");
                    Assert.IsTrue(ReferenceEquals(EffectEngine.Instance.InputGateway, gw),
                        "前置：网关必须注入到引擎侧（`SetInputGateway` 同时注入引擎与 TurnManager）");
                }

                // 直接结算该效果（同步入口内含"单机弹窗 / 联机网关"两条分支）：
                // 不经过整回合让过，避免"抓牌步骤把顶牌抽走 / 恢复步骤手牌上限弃牌"干扰断言。
                EffectEngine.Instance.ResolveEffect(effect, sphinx, null, area);

                if (online)
                    Assert.IsTrue(gw.Requests.Exists(r => r.RequestKind == Script.Net.ChoiceRequest.Kind.NumberInput),
                        "联机：应下发数值输入请求");
                Assert.AreEqual(CardZone.Hand, top.CurrentZone,
                    $"(online={online}) 印刷费用＝所选数字 ⇒ 置入手牌（说明回包后**续算已落地**）");
                Assert.IsTrue(settled >= 1,
                    $"(online={online}) 回包续算后必须做结算收尾（`OnAsyncSettlementCompleted`：联机靠它下发快照、单机靠它补做解锁/地区赢取）");
                Assert.IsFalse(game.TurnManager.IsAtomicLocked,
                    $"(online={online}) 结算收尾后必须释放决策持锁");
            }
            finally
            {
                game.TurnManager.OnAsyncSettlementCompleted -= onSettled;
                if (online) game.SetInputGateway(null);
                EffectEngine.Instance.UIHooks = prevHooks;
            }
        }

        /// <summary>
        /// 【判据㉙/⑦⑧】`ConfirmPrompt`（JC082 收藏"是否抓一张牌"）：
        /// 选"否"⇒ **不入堆叠、不抓牌**，且**仍然**完成结算收尾与解锁；选"是"⇒ 抓一张。
        /// </summary>
        public void TestContract_交互收尾_是窗与持锁()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1)[0];
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game.ZoneManager.AttachAndRegister(book, council, "");
            game.TurnManager.PriorityPlayerId = 1;
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            // P1 赢下调查对抗的场面（调查 2 vs 0）
            var strong = OnField(game, Probe("TS_ct_cf_strong", investigation: 2), 1, area);
            var weak = OnField(game, Probe("TS_ct_cf_weak"), 2, area);

            var prevHooks = EffectEngine.Instance.UIHooks;
            int settled = 0;
            System.Action onSettled = () => settled++;
            game.TurnManager.OnAsyncSettlementCompleted += onSettled;
            try
            {
                var stub = new ConfirmStub { Answer = false };
                EffectEngine.Instance.UIHooks = stub;

                var keep = FindEffect(book.PlayerTemplate, "JC082_B_KeepPledgeActivate");
                EffectEngine.Instance.ActivateAbility(book, keep, null);
                ResolveAll(game);
                int handBefore = game.TurnManager.GetHandOf(1).Count;

                var win = new InstantCombatWindow(game);
                try
                {
                    game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                    Assert.AreEqual(1, stub.Calls, "获胜时应弹一次『是/否』窗");
                    Assert.IsFalse(FindEntry(game, "JC082_C_"), "选『否』⇒ 该触发不入堆叠");
                    ResolveAll(game);
                    Assert.AreEqual(handBefore, game.TurnManager.GetHandOf(1).Count, "选『否』⇒ 不抓牌");
                    Assert.IsFalse(game.TurnManager.IsAtomicLocked, "选『否』后不得留下决策持锁");
                }
                finally { win.Detach(); }

                // 选『是』：抓一张（同一路径的另一分支）
                stub.Answer = true;
                int handBefore2 = game.TurnManager.GetHandOf(1).Count;
                var win2 = new InstantCombatWindow(game);
                try
                {
                    game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Investigation, area, 1, null);
                    Assert.IsTrue(FindEntry(game, "JC082_C_"), "选『是』⇒ 抓牌触发入堆叠");
                    ResolveAll(game);
                    Assert.AreEqual(handBefore2 + 1, game.TurnManager.GetHandOf(1).Count, "选『是』⇒ 抓一张");
                    Assert.IsFalse(game.TurnManager.IsAtomicLocked, "选『是』后不得留下决策持锁");
                }
                finally { win2.Detach(); }
            }
            finally
            {
                game.TurnManager.OnAsyncSettlementCompleted -= onSettled;
                EffectEngine.Instance.UIHooks = prevHooks;
            }
        }

        // ==================== ㉖/㉙ 联机孪生入口 ====================

        /// <summary>
        /// 【判据㉖/㉙】JC082（`PlayChoice`）在**联机入口**同样可识别并完成落地：
        ///  · `MultiTargetSlots.PlayChoiceEffect` 能合成"选择一项"；
        ///  · `PlayerActionService.TryBeginModeCardPlayOnline` 接管（返回 true）；
        ///  · 服务端驱动流程 `TurnManager.PlayModeCardAsync` 能"选模式 → 选目标 → 结附成功"。
        /// </summary>
        public void TestContract_联机孪生入口_PlayChoice()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3, (CardColor)2);
            var council = game.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1)[0];
            var book = game.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game.ZoneManager.MoveToHand(book, 1);
            AdvanceToActionStep(game, 1);

            var synthetic = MultiTargetSlots.PlayChoiceEffect(book.PlayerTemplate);
            Assert.IsNotNull(synthetic, "PlayChoiceEffect 应能合成『选择一项』声明（联机侧同款入口）");
            Assert.AreEqual(2, synthetic.Modes.Count, "两个选项（结附于目标角色／结附于本方秘社）");

            // (a) 入口接管：`TryBeginModeCardPlayOnline` 返回 true 并下发"选择一项"请求
            var gw = new AutoAnswerGateway { ModeChoice = 1 };   // 第 2 项＝收藏（本方秘社）
            game.SetInputGateway(gw);
            try
            {
                Assert.IsTrue(game.PlayerActionService.TryBeginModeCardPlayOnline(1, book.UID),
                    "联机入口应接管 PlayChoice 卡的打出流程");
                Assert.IsTrue(gw.Requests.Exists(r => r.RequestKind == Script.Net.ChoiceRequest.Kind.SelectEffectMode),
                    "联机入口应下发『选择一项』请求");
            }
            finally
            {
                game.SetInputGateway(null);
            }

            // (b) 服务端驱动流程（同一实现）端到端：另一张卡（避免与上面那次接管相互干扰）
            var game2 = NewGame();
            GiveAssets(game2, 1, 3, (CardColor)2);
            var council2 = game2.ZoneManager.GetZoneCards(CardZone.CouncilZone, 1)[0];
            var book2 = game2.CardManager.CreateCardInstance(Load("JC082_suoyazhishu"), 1);
            game2.ZoneManager.MoveToHand(book2, 1);
            AdvanceToActionStep(game2, 1);
            var gw2 = new AutoAnswerGateway { ModeChoice = 1 };
            game2.SetInputGateway(gw2);
            try
            {
                var task = game2.TurnManager.PlayModeCardAsync(1, book2.UID);
                Assert.IsTrue(task.Wait(5000), "服务端模式流程应能完成");
                Assert.IsTrue(task.Result, "服务端模式流程应成功打出");
                ResolveAll(game2);
                Assert.AreEqual(CardZone.AttachedZone, book2.CurrentZone, "联机流程：应结附（不得进弃牌堆）");
                Assert.IsTrue(ReferenceEquals(book2.AttachedTo, council2), "联机流程：应结附于本方秘社");
            }
            finally
            {
                game2.SetInputGateway(null);
            }
        }

        // ==================== 判据⑮ 放置去重窗口（威名／声望） ====================

        /// <summary>
        /// 【判据⑮·放置去重窗口】两个"放置势力标志"词条必须**形状一致且互不干扰**：
        ///  · 窗口＝一次广播（`OnCombatWon`＝威名／`PowerCombatEnded`＝声望）；
        ///  · 桶＝去重标签（`PlacementDedupTags`），键＝`地区|控制者`；开启窗口只清**本标签**的桶；
        ///  · 同一窗口内同一控制者同一地区只放 1 枚；下一次广播＝新窗口 ⇒ 再放 1 枚。
        /// 本用例锁【威名】：同区 2 张【威名】在一次战斗对抗内只放 1 枚，第二次战斗对抗再放 1 枚。
        /// </summary>
        public void TestContract_去重窗口_威名_多张一次战斗只放一枚()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            // 同区 2 张 JC018（威名，战力 1）⇒ P1 战力 2:0 赢下该地区的战斗对抗；P2 空场
            OnField(game, Load("JC018_morenchuanren"), 1, area);
            OnField(game, Load("JC018_morenchuanren"), 1, area);

            var win = new InstantCombatWindow(game);
            try
            {
                int before = game.AreaManager.GetPowerMark(area, 1);
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
                ResolveAll(game);
                Assert.AreEqual(before + 1, game.AreaManager.GetPowerMark(area, 1),
                    "同一次战斗对抗广播内：2 张【威名】只放 1 枚势力标志（与声望同款去重）");

                int afterFirst = game.AreaManager.GetPowerMark(area, 1);
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
                ResolveAll(game);
                Assert.AreEqual(afterFirst + 1, game.AreaManager.GetPowerMark(area, 1),
                    "第二次战斗对抗＝新的广播窗口 ⇒ 再放 1 枚（窗口按广播划分，不是整局/整回合一次）");
            }
            finally
            {
                win.Detach();
            }
        }

        /// <summary>
        /// 【判据⑮·放置去重窗口】**分桶独立**：威名与声望同处一个结算窗口时不互相吞标记、也不互相破防。
        /// 复现构造（旧实现＝共享一张表且键不含标签、任一方广播**清全表** ⇒ 期望 2 实际 3）：
        ///  ① `OnCombatWon` 广播（2 张威名入堆叠）；
        ///  ② 先结算**其中 1 张**威名（写入【威名】桶）——此时另一张仍在堆叠上、其去重记录还有效；
        ///  ③ `PowerCombatEnded` 广播：若清的是全表，威名的去重记录会被抹掉 ⇒ 第 2 张威名会**再放 1 枚**；
        ///  ④ 全部结算 ⇒ 期望 **威名共 1 枚 + 声望 1 枚**（＝2），而不是 3。
        /// 注："下一次广播＝新窗口再放 1 枚"由 `P3KeywordsTests.TestP3_声望_每次势力对抗触发_同一次只加一个`
        /// （声望）与本套件 `TestContract_去重窗口_威名_多张一次战斗只放一枚`（威名，含第二次战斗）分别覆盖；
        /// 本用例不再叠加第三次广播，避免势力标志达标触发**地区赢取**（赢取会清零势力标志，污染读数）。
        /// </summary>
        public void TestContract_去重窗口_威名与声望同窗不互吞()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());

            OnField(game, Load("JC018_morenchuanren"), 1, area);              // ① 威名 ×2（战力 1×2）
            OnField(game, Load("JC018_morenchuanren"), 1, area);
            OnField(game, Load("JC070_qianxinxiushi"), 1, area);             // ① 声望（P1 1 个 vs P2 0 个 ⇒ 多数）

            var win = new InstantCombatWindow(game);
            try
            {
                int before = game.AreaManager.GetPowerMark(area, 1);
                game.CombatManager.ResolveAreaCombat(CombatManager.CombatType.Strength, area, 1, null);
                Assert.IsFalse(game.StackManager.IsEmpty, "前置：威名触发已入堆叠");

                // ② 只结算 1 张威名（两次让过＝结算栈顶一项），另一张仍挂在堆叠上
                int queued = game.StackManager.Entries.Count;
                for (int i = 0; i < 2 && !game.StackManager.IsEmpty; i++)
                    game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
                Assert.IsTrue(game.StackManager.Entries.Count < queued,
                    "前置：已结算 1 张威名，仍有触发挂在堆叠上（同一窗口内交错）");

                // ③ 声望广播：只应清【声望】自己的桶，不得抹掉【威名】已放置的记录
                game.TurnManager.EmitPowerCombatEnded(area);
                ResolveAll(game);

                Assert.AreEqual(before + 2, game.AreaManager.GetPowerMark(area, 1),
                    "威名与声望**各自分桶** ⇒ 威名共 1 枚 + 声望 1 枚＝2（跨标签清桶不得破防本标签去重）");
            }
            finally
            {
                win.Detach();
            }
        }
    }
}
