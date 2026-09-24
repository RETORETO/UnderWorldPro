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
    /// 【触发语义·场景无关】回归：触发只判"条件是否满足过"，**不看来源卡此刻身在何处**。
    ///
    /// 锁定三条规则：
    ///  ① **死亡触发**：死亡处理完（卡已进墓地）才有触发窗口 → 角色卡（默认生效区=争夺区）的死亡触发
    ///     不需要、也不应该要求 `Scope=Graveyard`；
    ///  ② **进场即死**：卡进场后被持续效果立刻消灭，进场触发窗口打开时它已在墓地 → 进场触发仍须照常触发；
    ///  ③ **"本地区"基准 = 触发条件满足时的地区**（死亡时所处地区 / 进入的地区），而不是墓地（无地区）或结算时的位置。
    ///
    /// 同时锁定配套护栏：触发期间来源已离场时，`Move`/`Latent`/`Uncover` 等"场上动作"不得把非场上的卡搬回场上。
    /// </summary>
    public class TriggerSceneSemanticsTests : TestRunner
    {
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

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 21 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(1);
            return game;
        }

        private static string FirstAreaUid(GameInitializer game)
        {
            var uids = game.AreaManager?.GetOrderedAreaUids();
            return (uids != null && uids.Count > 0) ? uids[0] : "AreaSlot_1";
        }

        private static PlayerCardData Probe(string uid, EffectData effect,
            CardCategory category = CardCategory.Character, int defense = -1)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, CategoryFlag = category, Defense = defense,
            };
            if (effect != null) tmpl.EffectDataList.Add(effect);
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static EffectData Effect(string id, EffectTiming timing, TargetSpec target = null,
            params EffectAction[] actions)
        {
            var e = new EffectData
            {
                EffectId = id,
                Timing = timing,
                Target = target ?? new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>(),
            };
            foreach (var a in actions) e.Actions.Add(a);
            return e;
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

        // ==================== ① 死亡触发不需要 Scope=Graveyard ====================

        public void TestTrigSem_角色卡死亡触发无需声明墓地生效区()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            // 注意：**故意不声明 Scope**（角色默认生效区=争夺区），按"触发不看场景"必须照常触发。
            var tmpl = Probe("TS_die_default",
                Effect("TS_die_default_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);

            game.CardManager.DestroyInstance(card);
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "前置：卡已被消灭入墓地");

            Assert.IsTrue(WaitUntil(() => StackHas(game, card.UID)),
                "死亡触发窗口打开时卡已在墓地，但【触发不看当前场景】→ 默认（争夺区）生效区的死亡触发仍须收集入栈");
        }

        /// <summary>反向对照：非事件主体的旁观者仍按实时区域判定（避免"尸体触发"污染反应类）。</summary>
        public void TestTrigSem_旁观者仍按实时区域判定()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            // 旁观卡：反应类（OnCardPlayed），但**自身在墓地** → 不应被收集
            var watcher = Probe("TS_watcher",
                Effect("TS_watcher_e", EffectTiming.OnCardPlayed, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var watcherInst = game.CardManager.CreateCardInstance(watcher, 1);
            game.ZoneManager.MoveToArea(watcherInst, area, 1);
            game.CardManager.DestroyInstance(watcherInst);       // 进墓地
            System.Threading.Thread.Sleep(80);

            // 让对手打出一张牌触发 OnCardPlayed
            var playedTmpl = Probe("TS_played", null);
            var played = game.CardManager.CreateCardInstance(playedTmpl, 2);
            game.ZoneManager.MoveToHand(played, 2);
            game.TurnManager.PriorityPlayerId = 2;
            game.TurnManager.PlayCard(2, played.UID);

            System.Threading.Thread.Sleep(120);
            Assert.IsFalse(StackHas(game, watcherInst.UID),
                "旁观者（非事件主体）仍按实时区域判定：已在墓地的旁观卡不应被反应类事件误触发");
        }

        // ==================== ② 进场即死：进场触发仍须成立 ====================

        public void TestTrigSem_进场即死仍触发进场触发()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            var tmpl = Probe("TS_enter_die",
                Effect("TS_enter_die_e", EffectTiming.EnterTrigger, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var card = game.CardManager.CreateCardInstance(tmpl, 1);

            // 模拟"进场后被持续效果立即消灭"：在 FireMoved（MoveToArea 内部、EnterTrigger 发点之前）
            // 的回调里把卡消灭 —— 这样进场触发窗口打开时卡已经在墓地。
            System.Action<CardInstance, CardZone, int, string> killer = null;
            killer = (c, zone, pid, detail) =>
            {
                if (c != card || zone != CardZone.AreaZone) return;
                game.ZoneManager.OnCardMoved -= killer;      // 只杀一次，避免递归
                game.CardManager.DestroyInstance(c);
            };
            game.ZoneManager.OnCardMoved += killer;

            game.ZoneManager.MoveToArea(card, area, 1);      // 进场 → 回调里立即死亡 → 随后才发 EnterTrigger

            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "前置：卡在进场过程中即被消灭（已在墓地）");
            Assert.IsTrue(WaitUntil(() => StackHas(game, card.UID)),
                "进场即死时，进场触发窗口打开时卡已在墓地 —— 但进场触发仍须照常收集入栈");
        }

        // ==================== ③ "本地区" = 触发条件满足时的地区 ====================

        public void TestTrigSem_死亡触发的本地区按死亡时所处地区()
        {
            var game = NewGame();
            var gateway = new AllEnterGateway();
            game.TurnManager.InputGateway = gateway;
            string area = FirstAreaUid(game);

            // 死亡者：死亡触发 = 消灭"本地区"目标角色（需要选目标 → 依赖"本地区"基准）
            var dying = Probe("TS_local_die",
                Effect("TS_local_die_e", EffectTiming.OnCardDied,
                    new TargetSpec
                    {
                        Kind = TargetKind.Character,
                        NeedsSelection = true,
                        IsLocalZone = true,
                        CanTargetSelf = false,
                    },
                    new EffectAction { Verb = ActionVerb.Destroy }));
            var dyingInst = game.CardManager.CreateCardInstance(dying, 1);

            // 同地区的一个候选目标
            var bystander = game.CardManager.CreateCardInstance(Probe("TS_local_bystander", null), 2);
            game.ZoneManager.MoveToArea(bystander, area, 2);

            // 另一个地区放一个对照候选（不同 AREASLOT，不应出现在候选里）
            var others = game.AreaManager.GetOrderedAreaUids();
            string otherArea = others.Count > 1 ? others[1] : area;
            var faraway = game.CardManager.CreateCardInstance(Probe("TS_local_far", null), 2);
            game.ZoneManager.MoveToArea(faraway, otherArea, 2);

            game.ZoneManager.MoveToArea(dyingInst, area, 1);
            game.CardManager.DestroyInstance(dyingInst);      // 死亡 → 触发（本地区=死亡时所处地区）

            // 该触发需选目标 → 服务端应下发 SelectTarget，候选应来自"死亡时所处地区"的同 SLOT 争夺区
            bool got = WaitUntil(() => FindTargetRequest(gateway, dyingInst.UID) != null);
            Assert.IsTrue(got, "死亡触发的『本地区』取目标应能开窗（若用墓地的空 ZoneDetailId 作基准，这里会因无候选而被丢弃）");
            var req = FindTargetRequest(gateway, dyingInst.UID);
            Assert.IsTrue(req.OptionUids.Contains(bystander.UID),
                "候选应包含死亡时所在地区的角色（本地区 = 死亡时所处地区）");
            Assert.IsFalse(req.OptionUids.Contains(dyingInst.UID),
                "死亡者自身已不在场，不应成为候选");
        }

        private static ChoiceRequest FindTargetRequest(AllEnterGateway gw, string sourceUid)
        {
            foreach (var r in gw.Requests)
                if (r.RequestKind == ChoiceRequest.Kind.SelectTarget && r.SourceUid == sourceUid) return r;
            return null;
        }

        // ==================== ④ 死亡=行为：牺牲也算死亡 ====================

        public void TestTrigSem_牺牲也是死亡()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            // 牺牲者：自身带死亡触发（默认生效区=争夺区，不声明墓地）
            var tmpl = Probe("TS_sac",
                Effect("TS_sac_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);

            // 用"牺牲本角色"作为激活代价（引擎代价路径）触发死亡
            var act = Effect("TS_sac_act", EffectTiming.CharacterActivated, null);
            act.ActivateCondition = "牺牲本角色";
            act.ActivateWindow = "快速";
            game.TurnManager.PriorityPlayerId = 1;
            EffectEngine.Instance.ActivateAbility(card, act, null);

            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "前置：牺牲后入墓地");
            Assert.IsTrue(WaitUntil(() => StackHas(game, card.UID)),
                "牺牲也是死亡（死亡=行为）→ 死亡触发必须成立");

            // 对照：ActionVerb.Sacrifice（效果动词路径）同样算死亡
            var tmpl2 = Probe("TS_sac2",
                Effect("TS_sac2_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var card2 = game.CardManager.CreateCardInstance(tmpl2, 1);
            game.ZoneManager.MoveToArea(card2, area, 1);
            EffectEngine.Instance.ResolveEffect(Effect("TS_sac2_kill", EffectTiming.OnSorceryCast, null,
                new EffectAction { Verb = ActionVerb.Sacrifice }), card2, card2);
            Assert.AreEqual(CardZone.Graveyard, card2.CurrentZone, "前置2：Sacrifice 动词后入墓地");
            Assert.IsTrue(WaitUntil(() => StackHas(game, card2.UID)),
                "ActionVerb.Sacrifice 也是死亡 → 死亡触发必须成立");
        }

        // ==================== ⑤ 死亡与去向无关（替代效果改去向也必须触发） ====================

        public void TestTrigSem_死亡去向不是墓地也照样触发()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            var tmpl = Probe("TS_dest",
                Effect("TS_dest_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);

            // 模拟未来的"替代效果"：所有卡不进入墓地，直接返回牌库底
            game.CardManager.ResolveDeath(card, CardManager.DeathCause.Destroy, CardZone.Deck);

            Assert.AreEqual(CardZone.Deck, card.CurrentZone, "前置：替代去向=牌库（未进墓地）");
            Assert.IsTrue(WaitUntil(() => StackHas(game, card.UID)),
                "死亡是行为、与去向无关：卡没进墓地（改回牌库）时，死亡触发依然必须成立");
        }

        // ==================== ⑥ 旁观者：条件满足时在场即可（其后离场仍可响应） ====================

        public void TestTrigSem_随宿主一同离场的旁观者仍能响应()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            // 宿主角色（无触发）
            var host = game.CardManager.CreateCardInstance(Probe("TS_host", null), 1);
            game.ZoneManager.MoveToArea(host, area, 1);

            // 旁观者：结附于宿主的附属，监听"有卡死亡"（默认生效区=附属区）
            var att = Probe("TS_att",
                Effect("TS_att_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }),
                CardCategory.Attachment);
            var attInst = game.CardManager.CreateCardInstance(att, 1);
            attInst.AttachedTo = host;
            host.AttachedCards.Add(attInst);
            game.ZoneManager.MoveToAttachedZone(attInst, area, 1);

            // 宿主死亡：连带把附属一起带走 → 附属在"入堆叠时"已不在附属区，
            // 但触发条件满足（宿主死亡）那一刻它仍在场上 → 必须能响应（入堆叠无地区要求）。
            game.CardManager.DestroyInstance(host);

            Assert.IsTrue(WaitUntil(() => StackHas(game, attInst.UID)),
                "旁观者只要在'条件满足那一刻'在场即可：随宿主一同离场后仍应能响应（入堆叠无地区要求）");
        }

        // ==================== ⑦ 没死就没有死亡触发（防止伤害 / 阻止死亡） ====================

        public void TestTrigSem_防止伤害则不产生死亡触发()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            var tmpl = Probe("TS_prevent_dmg",
                Effect("TS_prevent_dmg_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }),
                CardCategory.Character, defense: 1);      // 印刷防御 1：受 5 点伤害必死（若无防止）
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);
            Assert.AreEqual(1, card.CurrentDefense, "前置：印刷防御 1（注意：无印刷防御的卡完全免伤，测不出防止语义）");

            // 注册"伤害防止"（JC 包的灵体＝防止伤害，将来由此钩子实现）
            game.CardManager.DamagePreventer = (inst, amt) => inst == card;
            game.CardManager.DealDamageToInstance(card, 5);
            System.Threading.Thread.Sleep(120);

            Assert.AreEqual(0, card.Damage, "伤害被防止 → 不应写入伤害");
            Assert.AreEqual(CardZone.AreaZone, card.CurrentZone, "伤害被防止 → 不该死亡");
            Assert.IsFalse(StackHas(game, card.UID),
                "即使伤害值远超防御上限，被防止后没有死亡 → 绝不能触发死亡触发");

            // 撤掉防止后再打同样伤害：应死亡并触发
            game.CardManager.DamagePreventer = null;
            game.CardManager.DealDamageToInstance(card, 5);
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "对照：无防止时同样伤害致死");
            Assert.IsTrue(WaitUntil(() => StackHas(game, card.UID)), "对照：真死亡时应触发死亡触发");
        }

        public void TestTrigSem_死亡被阻止则不触发死亡触发()
        {
            var game = NewGame();
            game.TurnManager.InputGateway = new AllEnterGateway();
            string area = FirstAreaUid(game);

            var tmpl = Probe("TS_prevent_death",
                Effect("TS_prevent_death_e", EffectTiming.OnCardDied, null,
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } }));
            var card = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(card, area, 1);

            // "不会被 A 效果消灭"类替代效果（将来由此钩子实现）
            game.CardManager.DeathPreventer = (inst, cause) => inst == card && cause == CardManager.DeathCause.Destroy;
            game.CardManager.DestroyInstance(card);
            System.Threading.Thread.Sleep(120);

            Assert.AreEqual(CardZone.AreaZone, card.CurrentZone, "死亡被阻止 → 卡留在场上");
            Assert.IsFalse(StackHas(game, card.UID),
                "A 效果对这张卡成功生效但卡没死 → 不属于死亡，不能触发死亡触发");

            // 换一种死亡原因（牺牲）不受该阻止钩子影响 → 应正常死亡并触发
            game.CardManager.DeathPreventer = null;
            game.CardManager.ResolveDeath(card, CardManager.DeathCause.Sacrifice);
            Assert.AreEqual(CardZone.Graveyard, card.CurrentZone, "对照：阻止解除后牺牲致死");
            Assert.IsTrue(WaitUntil(() => StackHas(game, card.UID)), "对照：真死亡时应触发死亡触发");
        }

        // ==================== 配套护栏：来源已离场时的"场上动作"不得生效 ====================

        public void TestTrigSem_来源已离场时场上动作被护栏拦住()
        {
            var ee = new EffectEngine();
            var cm = new CardManager(); cm._Ready();
            ee.CardManagerRef = cm;
            var zm = new ZoneManager(); zm._Ready();
            zm.Initialize(cm, null);
            cm.SetZoneManager(zm);
            ee.ZoneManagerRef = zm;

            // 一张"已在墓地"的来源卡（模拟死亡触发的来源）
            var dead = cm.CreateCardInstance(Probe("TS_dead_src", null), 1);
            zm.MoveToArea(dead, "AreaSlot_1", 1);
            cm.DestroyInstance(dead);
            Assert.AreEqual(CardZone.Graveyard, dead.CurrentZone, "前置：来源已在墓地");

            // Latent：不应把墓地里的卡翻面为暗藏者
            ee.ResolveEffect(Effect("TS_guard_latent", EffectTiming.OnCardDied, null,
                new EffectAction { Verb = ActionVerb.Latent }), dead, dead);
            Assert.IsFalse(dead.IsCovert, "来源不在场上时 Latent 应被护栏拦住");

            // Move：不应把墓地里的卡搬回争夺区
            ee.ResolveEffect(Effect("TS_guard_move", EffectTiming.OnCardDied, null,
                new EffectAction { Verb = ActionVerb.Move }), dead, null);
            Assert.AreEqual(CardZone.Graveyard, dead.CurrentZone, "来源不在场上时 Move 应被护栏拦住（不得把墓地卡搬回场上）");
        }

        // ==================== 机制层：生效区判定的区域覆盖 ====================

        public void TestTrigSem_生效区覆盖参数()
        {
            var tmpl = Probe("TS_zone", null);
            var card = new CardInstance(tmpl, "ts_zone_1") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.Graveyard };
            var e = Effect("TS_zone_e", EffectTiming.OnCardDied);

            Assert.IsFalse(EffectEngine.IsEffectActiveInZone(e, card),
                "实时区域=墓地 → 角色默认生效区（争夺区）判定为不生效（这正是没有覆盖参数时会丢触发的原因）");
            Assert.IsTrue(EffectEngine.IsEffectActiveInZone(e, card, CardZone.AreaZone),
                "传入『触发时区域=争夺区』后应判定生效（触发语义的核心）");

            var attTmpl = Probe("TS_zone_att", null, CardCategory.Attachment);
            var att = new CardInstance(attTmpl, "ts_zone_2") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.Graveyard };
            Assert.IsTrue(EffectEngine.IsEffectActiveInZone(e, att, CardZone.AttachedZone),
                "附属卡同理：触发时在附属区即应生效");
        }
    }
}
