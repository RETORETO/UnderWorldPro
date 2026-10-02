using System.Collections.Generic;
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
    /// 【A55·联机决策接线】回归：**服务端（无 UIHooks、有 InputGateway）**下，所有"需要玩家选择"的效果
    /// 都必须**真的问玩家**，而不是走"无 UI 确定性兜底"。
    ///
    /// 背景（2026-09 实测缺陷）：生产链路是 `TurnManager.ResolveStackTop`（同步）→ `ResolveEntry` →
    /// **同步** `ResolveEffect`，而网关分支原先只写在 `EffectEngine.ResolveEffectAsync`/`ExecuteVerbAsync`
    /// 那套"可挂起续算"实现里（全项目只有测试调用）⇒ 服务端把检索/弃牌/封印/可选支付等一律**自动代选**。
    /// 本套件统一用**同步入口** `ResolveEffect` + 网关注入来驱动（＝生产形状），逐项锁定：
    ///  · 请求确实下发（Kind / 候选 / ForceChoice 字段）；
    ///  · **挂起-续算**：等待回包期间原子锁保持、回包后剩余动作继续执行；
    ///  · 回包落地 + 权威复检（非法/取消按各效果既定语义兜底）；
    ///  · 单机（有 UIHooks）**不走网关**；两者皆无时仍走确定性兜底（既有行为不回退）。
    /// </summary>
    public class P4OnlineGatewayWiringTests : TestRunner
    {
        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 20260901 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static string FirstArea(GameInitializer game) => game.AreaManager.GetOrderedAreaUids()[0];

        private static PlayerCardData Probe(string uid, EffectData effect = null,
            CardCategory category = CardCategory.Sorcery)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = category == CardCategory.Character ? 3 : -1,
            };
            if (effect != null) tmpl.EffectDataList.Add(effect);
            tmpl._effectsInjected = true;   // 必须：否则 CardEffectFactory.EnsureEffects 会清空上面的效果
            return tmpl;
        }

        private static EffectData ActionEffect(string id, ActionVerb verb, params string[] args)
        {
            var e = new EffectData
            {
                EffectId = id,
                Timing = EffectTiming.OnSorceryCast,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };
            var a = new EffectAction { Verb = verb, Args = new Array<string>() };
            foreach (var s in args) a.Args.Add(s);
            e.Actions.Add(a);
            return e;
        }

        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_a55_asset_{playerId}_{i}", null, CardCategory.Attachment);
                tmpl.Colors.Add(CardColor.Blue);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static CardInstance PutOnDeckTop(GameInitializer game, PlayerCardData tmpl, int ownerId = 1)
        {
            var card = game.CardManager.CreateCardInstance(tmpl, ownerId);
            card.CurrentZone = CardZone.Deck;
            game.DeckManager.GetDeckPlayer1().Insert(0, card);
            return card;
        }

        /// <summary>
        /// 网关桩：`Defer=false` 时立即回包（默认取第一个候选 / `AnswerAmount`，可被 `AnswerUid` 覆盖）；
        /// `Defer=true` 时**挂起**（返回未完成 Task），由 `AnswerPending` 手动回包 —— 用于验证
        /// "服务端等待回包期间的原子锁保持 / 回包后续算"。
        /// </summary>
        private sealed class Gateway : IInputGateway
        {
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();
            private readonly List<System.Threading.Tasks.TaskCompletionSource<ChoiceResult>> _pending
                = new List<System.Threading.Tasks.TaskCompletionSource<ChoiceResult>>();
            public bool Defer;
            public string AnswerUid;        // null＝取第一个候选
            public int AnswerAmount = -1;   // <0＝取 NumberDefault
            public bool Cancel = false;     // true＝回包按"取消"处理（当前用例未使用，保留扩展位）

            public int PendingCount => _pending.Count;

            public System.Threading.Tasks.Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                Requests.Add(req);
                if (!Defer) return System.Threading.Tasks.Task.FromResult(Build(req));
                var tcs = new System.Threading.Tasks.TaskCompletionSource<ChoiceResult>();
                _pending.Add(tcs);
                return tcs.Task;
            }

            private ChoiceResult Build(ChoiceRequest req)
            {
                var r = new ChoiceResult { RequestId = req?.RequestId, Cancelled = Cancel };
                if (Cancel) return r;
                r.SelectedUid = !string.IsNullOrEmpty(AnswerUid)
                    ? AnswerUid
                    : (req?.OptionUids != null && req.OptionUids.Count > 0 ? req.OptionUids[0] : null);
                r.SelectedAmounts.Add(AnswerAmount >= 0 ? AnswerAmount : (req?.NumberDefault ?? 0));
                return r;
            }

            /// <summary>回包第 <paramref name="index"/> 个挂起请求（未完成 Task 的续算在同一线程同步执行）。</summary>
            public void AnswerPending(int index, string uid = null, int amount = -1, bool cancelled = false)
            {
                var tcs = _pending[index];
                var r = new ChoiceResult { Cancelled = cancelled };
                if (!cancelled)
                {
                    r.SelectedUid = uid;
                    r.SelectedAmounts.Add(amount);
                }
                tcs.SetResult(r);
            }
        }

        // ==================== ① 检索取牌（用户实测案例） ====================

        /// <summary>服务端检索必须下发 `SelectDeckCard` 并**等玩家回包**：等待期间原子锁保持，回包后按所选落地。</summary>
        public void TestA55检索_服务端经网关选牌并保持原子锁()
        {
            var game = NewGame();
            var gw = new Gateway { Defer = true };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;   // 服务端形状

            var first = PutOnDeckTop(game, Probe("TS_a55_search_a", null, CardCategory.Character));
            var second = PutOnDeckTop(game, Probe("TS_a55_search_b", null, CardCategory.Character));
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_a55_search_src", ActionEffect("TS_a55_search_e", ActionVerb.Search, "类别:角色", "手牌")), 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(1, gw.Requests.Count, "服务端应下发一次检索请求（而不是自动取第一张）");
            Assert.AreEqual(ChoiceRequest.Kind.SelectDeckCard, gw.Requests[0].RequestKind, "Kind＝SelectDeckCard");
            Assert.IsTrue(gw.Requests[0].ForceChoice, "检索为强制（A53）");
            Assert.IsTrue(gw.Requests[0].OptionUids.Contains(second.UID), "候选含牌库中通过筛选的牌");
            Assert.IsTrue(game.TurnManager.IsAtomicLocked, "等待玩家回包期间应保持原子锁（对手不能抢行动）");

            gw.AnswerPending(0, second.UID);

            Assert.AreEqual(CardZone.Hand, second.CurrentZone, "回包所选之牌进手牌");
            Assert.IsFalse(first.CurrentZone == CardZone.Hand, "未选中的候选不进手牌（证明不是自动取第一张）");
            Assert.IsFalse(game.TurnManager.IsAtomicLocked, "回包续算完成后应释放原子锁");
        }

        /// <summary>回包该候选之外的牌（越权）→ 命中权威复检 → 兜底取第一张（A53 强制检索不可被规避）。</summary>
        public void TestA55检索_越权回包按强制兜底()
        {
            var game = NewGame();
            var gw = new Gateway { AnswerUid = "不存在的牌" };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            PutOnDeckTop(game, Probe("TS_a55_search2_a", null, CardCategory.Character));
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_a55_search2_src", ActionEffect("TS_a55_search2_e", ActionVerb.Search, "类别:角色", "手牌")), 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            var expect = game.CardManager.GetInstance(gw.Requests[0].OptionUids[0]);
            Assert.AreEqual(CardZone.Hand, expect.CurrentZone, "非法回包 → 兜底取第一张合法候选");
        }

        // ==================== ② 弃牌 ====================

        public void TestA55弃牌_服务端经网关选牌()
        {
            var game = NewGame();
            var gw = new Gateway();
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var h1 = game.CardManager.CreateCardInstance(Probe("TS_a55_disc_h1"), 1);
            game.ZoneManager.MoveToHand(h1, 1);
            var h2 = game.CardManager.CreateCardInstance(Probe("TS_a55_disc_h2"), 1);
            game.ZoneManager.MoveToHand(h2, 1);
            gw.AnswerUid = h2.UID;

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_a55_disc_src", ActionEffect("TS_a55_disc_e", ActionVerb.Discard, "1", "自己", "自选")), 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(1, gw.Requests.Count, "应下发一次弃牌选择请求");
            Assert.AreEqual(ChoiceRequest.Kind.SelectHandCard, gw.Requests[0].RequestKind, "Kind＝SelectHandCard");
            Assert.AreEqual(CardZone.Graveyard, h2.CurrentZone, "弃掉的是玩家回包选中的那张");
            Assert.AreEqual(CardZone.Hand, h1.CurrentZone, "另一张留在手牌");
        }

        // ==================== ③ 封印手牌 ====================

        public void TestA55封印手牌_服务端经网关选牌()
        {
            var game = NewGame();
            var gw = new Gateway();
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            string area = FirstArea(game);
            var host = game.CardManager.CreateCardInstance(Probe("TS_a55_seal_host", null, CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(host, area, 1);
            var h1 = game.CardManager.CreateCardInstance(Probe("TS_a55_seal_h1"), 1);
            game.ZoneManager.MoveToHand(h1, 1);
            var h2 = game.CardManager.CreateCardInstance(Probe("TS_a55_seal_h2"), 1);
            game.ZoneManager.MoveToHand(h2, 1);
            gw.AnswerUid = h2.UID;

            var src = game.CardManager.CreateCardInstance(
                Probe("TS_a55_seal_src", ActionEffect("TS_a55_seal_e", ActionVerb.Seal, "手牌", "自身")), 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(1, gw.Requests.Count, "应下发一次封印取手牌请求");
            Assert.AreEqual(CardZone.SealedZone, h2.CurrentZone, "被封印的是玩家回包选中的那张");
            Assert.IsTrue(ReferenceEquals(src, h2.SealedBy), "宿主＝来源自身");
            Assert.AreEqual(CardZone.Hand, h1.CurrentZone, "另一张留在手牌");
        }

        // ==================== ④ 挂起-续算：回包后剩余动作必须继续执行 ====================

        /// <summary>K11 可选支付（联机）：回包"支付"→ 扣费 **且续算后续动作**；回包"不支付"→ 后续整段跳过。</summary>
        public void TestA55可选支付_回包后续算剩余动作()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new Gateway { AnswerAmount = 1 };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var e = ActionEffect("TS_a55_pay_e", ActionVerb.PayOptional, "1", "0", "1");
            e.Actions.Add(new EffectAction
            {
                Verb = ActionVerb.GrantTemporaryMana,
                Args = new Array<string> { "2" },
            });
            var src = game.CardManager.CreateCardInstance(Probe("TS_a55_pay_src", e), 1);

            EffectEngine.Instance.ResolveEffect(e, src, null);

            Assert.AreEqual(1, gw.Requests.Count, "应下发一次数值输入请求");
            Assert.AreEqual(ChoiceRequest.Kind.NumberInput, gw.Requests[0].RequestKind, "Kind＝NumberInput");
            Assert.AreEqual(1, gw.Requests[0].NumberMax, "范围下发（0..1）");
            Assert.AreEqual(2, game.PlayerManager.GetTemporaryMana(1),
                "**回包后续算**：第二条动作（获得 2 点临时费用）必须执行 —— 挂起-续算链路的核心断言");
            // 临时费用计入可用费用 ⇒ 支付 1 后又获得 2 ⇒ 可用＝before − 1 + 2
            Assert.AreEqual(manaBefore + 1, game.PlayerManager.GetAvailableMana(1),
                $"支付 1 且后续获得 2 点临时费用（before={manaBefore}）");
        }

        public void TestA55可选支付_回包不支付则后续跳过()
        {
            var game = NewGame();
            GiveAssets(game, 1, 3);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new Gateway { AnswerAmount = 0 };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var e = ActionEffect("TS_a55_pay2_e", ActionVerb.PayOptional, "1", "0", "1");
            e.Actions.Add(new EffectAction
            {
                Verb = ActionVerb.GrantTemporaryMana,
                Args = new Array<string> { "2" },
            });
            var src = game.CardManager.CreateCardInstance(Probe("TS_a55_pay2_src", e), 1);

            EffectEngine.Instance.ResolveEffect(e, src, null);

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不支付 → 不扣费");
            Assert.AreEqual(0, game.PlayerManager.GetTemporaryMana(1), "不支付 → 后续动作整段跳过（『若你如此做』）");
        }

        // ==================== ⑤ JC083「选择一个数字」（玩家选择型动态值） ====================

        public void TestA55数字选择_回包后重入结算()
        {
            var game = NewGame();
            var gw = new Gateway { AnswerAmount = 4 };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var e = new EffectData
            {
                EffectId = "TS_a55_value_e",
                Timing = EffectTiming.OnSorceryCast,
                ValueExpr = "玩家选择",
                ValueMin = 2,
                ValueMax = 5,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.GrantTemporaryMana, Args = new Array<string> { "$X" } },
                },
            };
            var src = game.CardManager.CreateCardInstance(Probe("TS_a55_value_src", e), 1);

            EffectEngine.Instance.ResolveEffect(e, src, null);

            Assert.AreEqual(1, gw.Requests.Count, "应下发一次数字选择请求（此前服务端直接『不发动』）");
            Assert.AreEqual(ChoiceRequest.Kind.NumberInput, gw.Requests[0].RequestKind, "Kind＝NumberInput");
            Assert.AreEqual(2, gw.Requests[0].NumberMin, "ValueMin 下发");
            Assert.AreEqual(5, gw.Requests[0].NumberMax, "ValueMax 下发");
            Assert.AreEqual(4, game.PlayerManager.GetTemporaryMana(1), "回包数字 4 → 重入结算后 $X＝4 生效");
        }

        // ==================== ⑥ 0/1 二选一三处（JC064 / JC114 / JC128） ====================

        public void TestA55支付或牺牲_回包支付与回包牺牲()
        {
            // 回包"支付"→ 扣费且不牺牲
            {
                var game = NewGame();
                GiveAssets(game, 1, 3);
                int manaBefore = game.PlayerManager.GetAvailableMana(1);
                var gw = new Gateway { AnswerAmount = 1 };
                game.SetInputGateway(gw);
                EffectEngine.Instance.UIHooks = null;
                string area = FirstArea(game);
                var src = game.CardManager.CreateCardInstance(
                    Probe("TS_a55_pos_pay", ActionEffect("TS_a55_pos_pay_e", ActionVerb.PayOrSacrificeSelf, "1"), CardCategory.Character), 1);
                game.ZoneManager.MoveToArea(src, area, 1);

                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

                Assert.AreEqual(1, gw.Requests.Count, "应询问一次『支付还是牺牲』");
                Assert.AreEqual(ChoiceRequest.Kind.NumberInput, gw.Requests[0].RequestKind, "Kind＝NumberInput（0/1 二选一）");
                Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "回包支付 → 扣费");
                Assert.AreEqual(CardZone.AreaZone, src.CurrentZone, "支付 → 不牺牲");
            }
            // 回包"牺牲"→ 牺牲
            {
                var game = NewGame();
                GiveAssets(game, 1, 3);
                var gw = new Gateway { AnswerAmount = 0 };
                game.SetInputGateway(gw);
                EffectEngine.Instance.UIHooks = null;
                string area = FirstArea(game);
                var src = game.CardManager.CreateCardInstance(
                    Probe("TS_a55_pos_sac", ActionEffect("TS_a55_pos_sac_e", ActionVerb.PayOrSacrificeSelf, "1"), CardCategory.Character), 1);
                game.ZoneManager.MoveToArea(src, area, 1);

                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

                Assert.AreEqual(CardZone.Graveyard, src.CurrentZone, "回包不支付 → 牺牲");
            }
        }

        public void TestA55可选牺牲自身_回包才决定后续动作()
        {
            // 回包"牺牲"→ 牺牲 + 后续动作继续
            {
                var game = NewGame();
                var gw = new Gateway { AnswerAmount = 1 };
                game.SetInputGateway(gw);
                EffectEngine.Instance.UIHooks = null;
                string area = FirstArea(game);

                var e = ActionEffect("TS_a55_sso_e", ActionVerb.SacrificeSelfOptional);
                e.Actions.Add(new EffectAction { Verb = ActionVerb.GrantTemporaryMana, Args = new Array<string> { "3" } });
                var src = game.CardManager.CreateCardInstance(Probe("TS_a55_sso_src", e, CardCategory.Character), 1);
                game.ZoneManager.MoveToArea(src, area, 1);

                EffectEngine.Instance.ResolveEffect(e, src, null);

                Assert.AreEqual(1, gw.Requests.Count, "应询问一次『是否牺牲自身』");
                Assert.AreEqual(CardZone.Graveyard, src.CurrentZone, "回包牺牲 → 牺牲");
                Assert.AreEqual(3, game.PlayerManager.GetTemporaryMana(1), "牺牲后后续动作继续（『若你如此做』）");
            }
            // 回包"不牺牲"→ 不牺牲 + 后续跳过
            {
                var game = NewGame();
                var gw = new Gateway { AnswerAmount = 0 };
                game.SetInputGateway(gw);
                EffectEngine.Instance.UIHooks = null;
                string area = FirstArea(game);

                var e = ActionEffect("TS_a55_sso2_e", ActionVerb.SacrificeSelfOptional);
                e.Actions.Add(new EffectAction { Verb = ActionVerb.GrantTemporaryMana, Args = new Array<string> { "3" } });
                var src = game.CardManager.CreateCardInstance(Probe("TS_a55_sso2_src", e, CardCategory.Character), 1);
                game.ZoneManager.MoveToArea(src, area, 1);

                EffectEngine.Instance.ResolveEffect(e, src, null);

                Assert.AreEqual(CardZone.AreaZone, src.CurrentZone, "回包不牺牲 → 不牺牲");
                Assert.AreEqual(0, game.PlayerManager.GetTemporaryMana(1), "不牺牲 → 后续动作跳过");
            }
        }

        public void TestA55贿赂或牺牲_回包走牺牲路线()
        {
            var game = NewGame();
            var gw = new Gateway { AnswerAmount = 0 };   // 0 ＝ 改为牺牲
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            string area = FirstArea(game);
            game.AreaManager.AddPowerMark(area, 2, 3);   // 目标玩家有 3 个标志（足够支付 → 会询问）
            var victim = game.CardManager.CreateCardInstance(Probe("TS_a55_bribe_victim", null, CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(victim, area, 2);

            var e = ActionEffect("TS_a55_bribe_e", ActionVerb.BribeOrSacrifice, "1", "角色");
            var src = game.CardManager.CreateCardInstance(Probe("TS_a55_bribe_src", e), 1);

            EffectEngine.Instance.ResolveEffect(e, src, null);

            Assert.AreEqual(2, gw.Requests.Count,
                "两次请求：①『移除标志还是牺牲』(NumberInput) ②牺牲路线的『选谁牺牲』(SelectTarget)");
            Assert.AreEqual(ChoiceRequest.Kind.NumberInput, gw.Requests[0].RequestKind, "①＝0/1 二选一");
            Assert.AreEqual(ChoiceRequest.Kind.SelectTarget, gw.Requests[1].RequestKind, "②＝牺牲对象选择");
            Assert.AreEqual(3, game.AreaManager.GetPowerMark(area, 2), "选择牺牲 → 标志不变");
            Assert.AreEqual(CardZone.Graveyard, victim.CurrentZone, "走牺牲路线 → 目标玩家牺牲一个角色");
        }

        // ==================== ⑦ 逐位牺牲选择（JC046/113/134/65） ====================

        public void TestA55逐位牺牲选择_两位玩家各回包一次()
        {
            var game = NewGame();
            var gw = new Gateway { Defer = true };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            string area = FirstArea(game);
            var p1Char = game.CardManager.CreateCardInstance(Probe("TS_a55_multi_c1", null, CardCategory.Character), 1);
            game.ZoneManager.MoveToArea(p1Char, area, 1);
            var p2Char = game.CardManager.CreateCardInstance(Probe("TS_a55_multi_c2", null, CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(p2Char, area, 2);

            var e = ActionEffect("TS_a55_multi_e", ActionVerb.SacrificeChoice, "角色", "每位玩家", "任意");
            var src = game.CardManager.CreateCardInstance(Probe("TS_a55_multi_src", e), 1);

            EffectEngine.Instance.ResolveEffect(e, src, null);

            Assert.AreEqual(1, gw.Requests.Count, "先手先选 → 先下发第一位玩家的选择请求");
            Assert.AreEqual(ChoiceRequest.Kind.SelectTarget, gw.Requests[0].RequestKind, "Kind＝SelectTarget");
            Assert.IsTrue(gw.Requests[0].ForceChoice, "牺牲选择不可取消");
            int first = gw.Requests[0].PlayerId;

            gw.AnswerPending(0, gw.Requests[0].OptionUids[0]);
            Assert.AreEqual(2, gw.Requests.Count, "第一位选完后应继续询问第二位玩家");
            Assert.IsFalse(first == gw.Requests[1].PlayerId, "第二位＝另一名玩家");

            gw.AnswerPending(1, gw.Requests[1].OptionUids[0]);

            Assert.AreEqual(CardZone.Graveyard, p1Char.CurrentZone, "玩家1 牺牲其回包所选角色");
            Assert.AreEqual(CardZone.Graveyard, p2Char.CurrentZone, "玩家2 牺牲其回包所选角色");
        }

        // ==================== ⑧ 优先级与兜底（不回退既有行为） ====================

        /// <summary>单机（有 UIHooks）**不走网关**：仍用本机选卡窗（两者同时存在时 UI 优先）。</summary>
        public void TestA55单机有UI时优先本机窗口()
        {
            var game = NewGame();
            var gw = new Gateway();
            game.SetInputGateway(gw);
            var stub = new PickStub();
            EffectEngine.Instance.UIHooks = stub;
            try
            {
                var found = PutOnDeckTop(game, Probe("TS_a55_ui_a", null, CardCategory.Character));
                var src = game.CardManager.CreateCardInstance(
                    Probe("TS_a55_ui_src", ActionEffect("TS_a55_ui_e", ActionVerb.Search, "类别:角色", "手牌")), 1);

                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

                Assert.AreEqual(1, stub.Calls, "单机应弹本机选卡窗");
                Assert.AreEqual(0, gw.Requests.Count, "有 UI 时不走网关（避免两端争抢决策）");
                Assert.AreEqual(CardZone.Hand, found.CurrentZone, "按窗口所选落地");
            }
            finally { EffectEngine.Instance.UIHooks = null; }
        }

        /// <summary>既无 UI 也无网关（纯 headless 规则内核/测试）→ 仍走确定性兜底（既有行为不回退）。</summary>
        public void TestA55无UI无网关_仍走确定性兜底()
        {
            var game = NewGame();
            game.EffectEngine.InputGateway = null;
            EffectEngine.Instance.UIHooks = null;

            var found = PutOnDeckTop(game, Probe("TS_a55_fb_a", null, CardCategory.Character));
            var src = game.CardManager.CreateCardInstance(
                Probe("TS_a55_fb_src", ActionEffect("TS_a55_fb_e", ActionVerb.Search, "类别:角色", "手牌")), 1);

            EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

            Assert.AreEqual(CardZone.Hand, found.CurrentZone, "无 UI/网关 → 确定性兜底取第一张匹配（行为不变）");
        }

        // ==================== ⑨ 续算落地 → 通知服务端下发快照（客户端 UI 即时刷新） ====================

        /// <summary>
        /// 服务端异步续算落地后必须通知一次"结算完成"（`MatchHost` 据此**立即下发快照**）。
        /// 缺陷实测（服务器日志）：JC007 检索经网关选牌后，牌是在客户端回包的**异步续算**里进的场，
        /// 而快照只在"意图流程结束"时推送 ⇒ 客户端要等到下一次上行（让过）才看得到新手牌
        /// （用户报的"卡片加入手牌，玩家让过后才能看到手牌"）。
        /// </summary>
        public void TestA55续算落地后通知服务端下发快照()
        {
            var game = NewGame();
            var gw = new Gateway { Defer = true };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            int settlements = 0;
            System.Action onSettled = () => settlements++;
            game.TurnManager.OnAsyncSettlementCompleted += onSettled;
            try
            {
                PutOnDeckTop(game, Probe("TS_a55_notify_a", null, CardCategory.Character));
                var src = game.CardManager.CreateCardInstance(
                    Probe("TS_a55_notify_src", ActionEffect("TS_a55_notify_e", ActionVerb.Search, "类别:角色", "手牌")), 1);

                EffectEngine.Instance.ResolveEffect(src.PlayerTemplate.EffectDataList[0], src, null);

                Assert.AreEqual(1, gw.Requests.Count, "已挂起等待回包");
                Assert.AreEqual(0, settlements, "等待回包期间不应上报『结算完成』（此时服务端无新状态可推）");

                gw.AnswerPending(0, gw.Requests[0].OptionUids[0]);

                var landed = game.CardManager.GetInstance(gw.Requests[0].OptionUids[0]);
                Assert.AreEqual(CardZone.Hand, landed.CurrentZone, "回包所选之牌已入手");
                Assert.AreEqual(1, settlements,
                    "续算落地后必须通知一次『结算完成』——MatchHost 据此立即下发快照（客户端 UI 即时可见）");
            }
            finally { game.TurnManager.OnAsyncSettlementCompleted -= onSettled; }
        }

        /// <summary>选卡窗口桩：记录调用次数并回第一个候选。</summary>
        private sealed class PickStub : Script.Interfaces.IGameUIHooks
        {
            public int Calls;
            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
            {
                Calls++;
                onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
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
    }
}
