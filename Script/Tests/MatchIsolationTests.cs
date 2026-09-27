using System.Collections.Generic;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;

namespace Script.Tests
{
    /// <summary>
    /// 【大厅/房间 · 批次 2】**多对局隔离**验证：一个服务端进程内同时存在两场对局时，规则执行、消息路由与快照下行
    /// 必须彼此独立——这是"一个服务端进程多房间并发"的前提。
    ///
    /// 覆盖：
    ///  1. 两场对局各自持有独立的 Manager 与 EffectEngine（不是同一实例）；
    ///  2. **核心回归**：即使进程级 `CardManager.Instance` 指向"最后构造的那一局"，A 局效果结算也只能写到 A 局的
    ///     CardManager（`EffectEngine.CM` 走注入引用），B 局世界完全不受影响；
    ///  3. 两个 `MatchHost` 共用一条传输时：**座位绑定互不串**（A 拒绝 B 的会话）、快照只发给本场座位且内容取自本场；
    ///  4. `StopServing()` 清除注入（避免旧局遗留 Events/网关造成跨局串扰）。
    /// </summary>
    public class MatchIsolationTests : TestRunner
    {
        private static GameInitializer NewGame(int seed)
        {
            var g = new GameInitializer { DebugSeed = seed };
            g.InitializeAll();
            g.StartMatch();
            return g;
        }

        private static CardInstance PlantCard(GameInitializer game, string uid, int playerId = 1)
        {
            var tmpl = new PlayerCardData { UID = uid, CardName = "隔离卡" + uid, CategoryFlag = CardCategory.Character };
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(inst, "iso_area", playerId);
            return inst;
        }

        private static EffectData DestroyEffect() => new EffectData
        {
            EffectId = "ISO_Destroy",
            Timing = EffectTiming.OnSorceryCast,
            Target = new TargetSpec { NeedsSelection = false },
            Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Destroy } },
        };

        // ==================== 1) 世界独立 ====================

        public void TestTwoMatchesHaveIndependentManagers()
        {
            var a = NewGame(11);
            var b = NewGame(22);

            Assert.IsTrue(!ReferenceEquals(a.CardManager, b.CardManager), "两局必须各有独立 CardManager");
            Assert.IsTrue(!ReferenceEquals(a.ZoneManager, b.ZoneManager), "两局必须各有独立 ZoneManager");
            Assert.IsTrue(!ReferenceEquals(a.PlayerManager, b.PlayerManager), "两局必须各有独立 PlayerManager");
            Assert.IsTrue(!ReferenceEquals(a.EffectEngine, b.EffectEngine), "两局必须各有独立 EffectEngine");

            // 复现生产环境的危险状态：进程级 `CardManager.Instance` 指向"最后构造的那一局"。
            // （生产里由 `CardManager._Ready` 赋值；测试中 GameInitializer 未入场景树，故此处显式构造该状态。）
            CardManager.Instance = b.CardManager;
            Assert.IsTrue(ReferenceEquals(CardManager.Instance, b.CardManager),
                "（前置条件）进程级 CardManager.Instance 指向 b 局——正是隔离要防的场景");

            // 一局的卡不会出现在另一局的世界里
            var cardA = PlantCard(a, "ISO_A");
            Assert.IsTrue(a.ZoneManager.GetZoneCards(CardZone.AreaZone, 1).Contains(cardA), "A 局的卡应在 A 局争夺区");
            Assert.IsFalse(b.ZoneManager.GetZoneCards(CardZone.AreaZone, 1).Contains(cardA), "A 局的卡不得出现在 B 局");
        }

        // ==================== 2) 效果结算不跨局（核心回归） ====================

        public void TestEffectResolutionDoesNotCrossMatches()
        {
            var a = NewGame(11);
            var b = NewGame(22);

            var cardA = PlantCard(a, "ISO_A");
            var cardB = PlantCard(b, "ISO_B");

            int bGraveBefore = b.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count;
            int bAreaBefore = b.ZoneManager.GetZoneCards(CardZone.AreaZone, 1).Count;

            // 用 A 局自己的效果引擎消灭 A 局的卡（此时 CardManager.Instance 指向 b）
            a.EffectEngine.ResolveEffect(DestroyEffect(), cardA, cardA);

            Assert.IsTrue(a.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Contains(cardA),
                "A 局的消灭必须落在 A 局墓地（走注入的 CardManagerRef，而非进程级 Instance）");
            Assert.IsFalse(a.ZoneManager.GetZoneCards(CardZone.AreaZone, 1).Contains(cardA),
                "A 局的卡应已离开 A 局争夺区");
            Assert.AreEqual(bGraveBefore, b.ZoneManager.GetZoneCards(CardZone.Graveyard, 1).Count,
                "B 局墓地数量必须不变（A 局效果不得写入 B 局）");
            Assert.AreEqual(bAreaBefore, b.ZoneManager.GetZoneCards(CardZone.AreaZone, 1).Count,
                "B 局争夺区数量必须不变");
            Assert.IsTrue(b.ZoneManager.GetZoneCards(CardZone.AreaZone, 1).Contains(cardB),
                "B 局自己的卡应仍在 B 局争夺区");
        }

        // ==================== 3) MatchHost：座位路由与快照下行互不串 ====================

        public void TestTwoMatchHostsRouteAndPushIndependently()
        {
            var loop = new LoopbackTransport();
            loop.Start();

            // 四个会话：p1/p2 属 A 房，p3/p4 属 B 房（共用同一条服务端传输，正是"多房间同进程"的真实形态）
            var inbox = new System.Collections.Generic.Dictionary<string, List<string>>
            {
                ["p1"] = new List<string>(), ["p2"] = new List<string>(),
                ["p3"] = new List<string>(), ["p4"] = new List<string>(),
            };
            foreach (var kv in inbox) { var key = kv.Key; loop.RegisterSession(key, m => inbox[key].Add(m)); }

            var gameA = NewGame(11);
            var gameB = NewGame(22);
            var hostA = new MatchHost("room_A", gameA, loop);
            var hostB = new MatchHost("room_B", gameB, loop);
            hostA.StartServing();
            hostB.StartServing();

            hostA.BindSeat(1, "p1"); hostA.BindSeat(2, "p2");
            hostB.BindSeat(1, "p3"); hostB.BindSeat(2, "p4");

            // 3.0 座位映射互不串
            Assert.AreEqual(1, hostA.SeatOfSession("p1"), "A 房应把 p1 视为座位1");
            Assert.AreEqual(0, hostA.SeatOfSession("p3"), "A 房不应认识 B 房的会话");
            Assert.AreEqual(1, hostB.SeatOfSession("p3"), "B 房应把 p3 视为座位1");
            Assert.AreEqual(0, hostB.SeatOfSession("p1"), "B 房不应认识 A 房的会话");

            // 3.1 越权路由：A 房收到属于 B 房的会话意图 → 拒绝并回执，且不进入 A 的规则层
            string intentJson = IntentSerializer.SerializeIntent(
                PlayerIntent.Create(1, PlayerIntent.Kind.DebugCommand, "iso_noop"));
            string envelopedIntent = NetEnvelope.Wrap(NetEnvelope.Types.Intent, intentJson);

            Assert.IsTrue(hostA.HandleMessage("p3", NetEnvelope.Types.Intent, intentJson),
                "A 房对未知会话的意图应判为已处理（拒绝路径）");
            Assert.AreEqual(1, inbox["p3"].Count, "越权意图应收到一条拒绝回执");
            // 中文经 JSON 序列化会被转义（\uXXXX），故按结构断言而非文本包含
            var denied = IntentSerializer.DeserializeResult(NetEnvelope.UnwrapPayload(inbox["p3"][0]));
            Assert.IsNotNull(denied, "越权回执应能解析为 IntentResult");
            Assert.IsFalse(denied.Success, "越权意图应判失败");
            Assert.IsFalse(string.IsNullOrEmpty(denied.Error), "越权回执应给出原因（未绑定本场座位）");

            // 3.2 正常路由：A 房接受 p1 的意图并回执；意图处理后本场快照广播给**本场两个座位**
            foreach (var kv in inbox) kv.Value.Clear();   // 清掉 3.1 的拒绝回执，便于隔离断言
            Assert.IsTrue(hostA.HandleMessage("p1", NetEnvelope.Types.Intent, intentJson), "A 房应处理 p1 的意图");
            Assert.IsTrue(inbox["p1"].Exists(m => m.Contains(NetEnvelope.Types.IntentResult)),
                "p1 应收到意图回执");
            Assert.IsTrue(inbox["p2"].Exists(m => m.StartsWith(SnapshotSerializer.SnapPrefix)),
                "p2 应收到本场快照（结算后广播）");
            Assert.IsFalse(inbox["p2"].Exists(m => m.Contains(NetEnvelope.Types.IntentResult)),
                "p2 不应收到发给 p1 的意图回执");
            Assert.AreEqual(0, inbox["p3"].Count, "B 房会话不得收到 A 房的任何消息");
            Assert.AreEqual(0, inbox["p4"].Count, "B 房会话不得收到 A 房的任何消息");

            // 3.3 快照下行隔离：给 A 局多种一张场地卡，B 局多两张 → 快照内容与投递对象都应各归各
            PlantCard(gameA, "ISO_PLANT_A");
            PlantCard(gameB, "ISO_PLANT_B1");
            PlantCard(gameB, "ISO_PLANT_B2");

            foreach (var kv in inbox) kv.Value.Clear();
            hostA.PushSnapshot();
            hostB.PushSnapshot();

            Assert.AreEqual(1, inbox["p1"].Count, "A 房快照应发给 p1");
            Assert.AreEqual(1, inbox["p2"].Count, "A 房快照应发给 p2");
            Assert.AreEqual(1, inbox["p3"].Count, "B 房快照应发给 p3");
            Assert.AreEqual(1, inbox["p4"].Count, "B 房快照应发给 p4");

            int areaA = CountAreaCards(inbox["p1"][0]);
            int areaB = CountAreaCards(inbox["p3"][0]);
            Assert.IsTrue(areaA > 0 && areaB > 0, "双方快照都应含争夺区卡（快照非空）");
            Assert.IsTrue(areaA != areaB,
                "两局争夺区卡数不同（A+1、B+2）→ 证明各自下发的是**本场**快照，未串场");

            // 3.4 停服后不得再注入/残留（避免旧局事件影响新局）
            hostA.StopServing();
            Assert.IsTrue(gameA.TurnManager.InputGateway == null, "StopServing 应清除本局网关注入");
            Assert.IsTrue(gameB.TurnManager.InputGateway != null, "B 房仍应保持注入（互不影响）");

            hostB.StopServing();
            Assert.IsTrue(gameB.TurnManager.InputGateway == null, "B 房停服后同样清除注入");
        }

        // ==================== 3b) 【P0 回归】联机必须把网关注入 **EffectEngine** ====================

        /// <summary>
        /// 效果层的大量"玩家选择"（检索牌库 / 弃牌 / 封印取手牌 / 检视 / 可选支付 / 预测 X …）走的是
        /// **引擎自己的** `EffectEngine.InputGateway` 分支。此前 `MatchHost` 只注入了
        /// `TurnManager`/`CombatManager`，引擎侧恒为 null ⇒ 联机时那些效果全部落到"服务端无 UI"的
        /// 确定性兜底（检索取第一张、弃牌弃第一张…），玩家根本没得选。本用例锁死"三处都要注入"。
        /// </summary>
        public void TestMatchHostInjectsGatewayIntoEffectEngine()
        {
            var loop = new LoopbackTransport();
            loop.Start();
            var game = NewGame(33);
            var host = new MatchHost("room_gw", game, loop);

            Assert.IsTrue(game.EffectEngine.InputGateway == null, "前置：开局未注入网关");

            host.StartServing();
            Assert.IsTrue(game.TurnManager.InputGateway != null, "TurnManager 应已注入网关");
            Assert.IsTrue(game.CombatManager.InputGateway != null, "CombatManager 应已注入网关");
            Assert.IsTrue(game.EffectEngine.InputGateway != null,
                "**EffectEngine 也必须注入**（否则联机检索/弃牌/封印/检视/支付全部退化为自动代选）");

            host.StopServing();
            Assert.IsTrue(game.EffectEngine.InputGateway == null, "停服应清除引擎侧网关注入（避免跨局串扰）");
        }

        /// <summary>从一条 SNAP 报文中数出争夺区卡数（用于区分两场快照）。</summary>
        private static int CountAreaCards(string message)
        {
            if (string.IsNullOrEmpty(message) || !message.StartsWith(SnapshotSerializer.SnapPrefix)) return -1;
            var snap = SnapshotSerializer.Deserialize(message.Substring(SnapshotSerializer.SnapPrefix.Length));
            if (snap == null) return -1;
            int n = 0;
            foreach (var z in snap.Zones) if (z.Zone == CardZone.AreaZone) n++;
            return n;
        }
    }
}
