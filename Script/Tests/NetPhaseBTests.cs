using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Manager;
using Script.Net;
using Script.Net.Godot;

namespace Script.Tests
{
    /// <summary>
    /// 【Phase B】联机协议与信息隔离专项验证：
    ///  (1) 快照按玩家过滤：对手手牌/牌库只下发占位（数量保留、身份隐藏），公开区原样；
    ///  (2) 占位引用可解析为"隐藏占位实例"（同进程共享 CardManager 与跨进程 NetClientCardManager 两条路径）；
    ///  (3) 消息类型信封 v2：包裹/解包往返、载荷含 '|' 不受影响、旧裸格式回退兼容；
    ///  (4) 决策超时按 Kind 配置化（含可覆盖 TimeoutProvider 与超时兜底行为）。
    /// </summary>
    public class NetPhaseBTests : TestRunner
    {
        private static ZoneCardRef Card(string uid, CardZone zone, int owner)
            => new ZoneCardRef { Uid = uid, TemplateUid = "T_" + uid, Zone = zone, ZoneOwnerId = owner, OwnerId = owner, ControllerId = owner };

        private static GameSnapshot BuildFullSnapshot()
        {
            return new GameSnapshot
            {
                TurnCount = 5,
                FirstPlayerId = 1,
                PriorityPlayerId = 1,
                CurrentPhase = (int)TurnManager.TurnPhase.ActionFirst,
                Zones = new List<ZoneCardRef>
                {
                    Card("h1a", CardZone.Hand, 1), Card("h1b", CardZone.Hand, 1),   // 自己手牌（可见）
                    Card("h2a", CardZone.Hand, 2),                                   // 对手手牌（隐藏）
                    Card("d1a", CardZone.Deck, 1),                                   // 自己牌库（可见）
                    Card("d2a", CardZone.Deck, 2),                                   // 对手牌库（隐藏）
                    Card("role1", CardZone.AreaZone, 1),                             // 公开区
                    Card("gy1", CardZone.Graveyard, 1),                              // 公开区
                    Card("asset1", CardZone.AssetZone, 1),                           // 公开区
                },
                WorldDeckCards = new List<ZoneCardRef>
                {
                    Card("w1", CardZone.Deck, -1), Card("w2", CardZone.Deck, -1),     // 地区牌堆（双方都隐藏）
                },
            };
        }

        // ==================== (1) 快照按玩家过滤 ====================

        /// <summary>viewer=1：自己手牌/牌库可见；对手手牌/牌库与地区牌堆只发占位（数量保留、身份隐藏）。</summary>
        public void TestSnapshotFilterHidesOpponentHiddenZones()
        {
            var full = BuildFullSnapshot();
            var view = SnapshotFilter.ForPlayer(full, 1);

            Assert.AreEqual(full.Zones.Count, view.Zones.Count, "过滤后卡数应一致（隐藏区改为占位，数量不变）");

            var byUid = new Dictionary<string, ZoneCardRef>();
            foreach (var r in view.Zones) byUid[r.Uid] = r;

            // 自己的手牌：原样可见
            Assert.IsTrue(byUid.ContainsKey("h1a") && byUid.ContainsKey("h1b"), "自己手牌应原样可见");
            Assert.IsFalse(byUid["h1a"].IsHidden, "自己手牌不应是占位");
            Assert.AreEqual("T_h1a", byUid["h1a"].TemplateUid, "自己手牌的模板信息应保留");
            // 牌库：对**双方**都不可见（只看卡背与数量，不暴露牌序）→ 自己的牌库同样是占位
            Assert.IsFalse(byUid.ContainsKey("d1a"), "自己牌库真实 UID 也不下发（不暴露牌序）");

            // 对手手牌/牌库：占位（不泄露 UID/TemplateUid）
            Assert.IsFalse(byUid.ContainsKey("h2a"), "对手手牌真实 UID 不得下发");
            Assert.IsFalse(byUid.ContainsKey("d2a"), "对手牌库真实 UID 不得下发");

            int hiddenHand2 = 0, hiddenDeck1 = 0, hiddenDeck2 = 0;
            foreach (var r in view.Zones)
            {
                if (!r.IsHidden) continue;
                Assert.IsTrue(string.IsNullOrEmpty(r.TemplateUid), $"占位不得携带 TemplateUid（UID={r.Uid}）");
                Assert.IsTrue(r.Uid.StartsWith(SnapshotFilter.HiddenUidPrefix), "占位 UID 应使用隐藏前缀");
                if (r.Zone == CardZone.Hand && r.ZoneOwnerId == 2) hiddenHand2++;
                if (r.Zone == CardZone.Deck && r.ZoneOwnerId == 1) hiddenDeck1++;
                if (r.Zone == CardZone.Deck && r.ZoneOwnerId == 2) hiddenDeck2++;
            }
            Assert.AreEqual(1, hiddenHand2, "对手手牌应产出 1 条占位");
            Assert.AreEqual(1, hiddenDeck1, "自己牌库应产出 1 条占位（牌序对双方都隐藏）");
            Assert.AreEqual(1, hiddenDeck2, "对手牌库应产出 1 条占位");

            // 公开区原样保留
            Assert.IsFalse(byUid["role1"].IsHidden, "争夺区（公开）不应被隐藏");
            Assert.IsFalse(byUid["gy1"].IsHidden, "墓地（公开）不应被隐藏");
            Assert.IsFalse(byUid["asset1"].IsHidden, "资产区（公开）不应被隐藏");

            // 地区牌堆：对双方都隐藏，数量保留
            Assert.AreEqual(full.WorldDeckCards.Count, view.WorldDeckCards.Count, "地区牌堆数量应保留");
            foreach (var r in view.WorldDeckCards)
                Assert.IsTrue(r.IsHidden && string.IsNullOrEmpty(r.TemplateUid), "地区牌堆应全部为占位");
        }

        /// <summary>viewer &lt;= 0：不过滤（服务端自省/测试取完整快照）。</summary>
        public void TestSnapshotFilterNoFilterForServerViewer()
        {
            var full = BuildFullSnapshot();
            var same = SnapshotFilter.ForPlayer(full, 0);
            Assert.IsTrue(ReferenceEquals(full, same), "viewer=0 应直接返回完整快照（不过滤）");

            var v2 = SnapshotFilter.ForPlayer(full, 2);
            var byUid = new Dictionary<string, ZoneCardRef>();
            foreach (var r in v2.Zones) byUid[r.Uid] = r;
            Assert.IsTrue(byUid.ContainsKey("h2a"), "玩家2 视角下自己的手牌应可见");
            Assert.IsFalse(byUid.ContainsKey("h1a"), "玩家2 视角下对手（玩家1）手牌不得下发");
        }

        // ==================== (2) 占位实例解析 ====================

        /// <summary>占位引用在同进程共享 CardManager 与跨进程 NetClientCardManager 两条路径都应解析为隐藏占位实例。</summary>
        public void TestHiddenPlaceholderResolvesToInstance()
        {
            var hiddenRef = new ZoneCardRef { Uid = "hidden_0_2_0", Zone = CardZone.Hand, ZoneOwnerId = 2, IsHidden = true };

            // 路径一：共享/单进程 CardManager（loopback 与单机测试路径）
            var cm = new CardManager();
            var inst = cm.RebuildFromRef(hiddenRef);
            Assert.IsNotNull(inst, "共享 CardManager 应能解析占位引用");
            Assert.IsTrue(inst.IsHidden, "占位实例应标记 IsHidden");
            Assert.IsTrue(inst.Template == null, "占位实例不应携带模板（不泄露身份）");
            var again = cm.RebuildFromRef(hiddenRef);
            Assert.IsTrue(ReferenceEquals(inst, again), "同一占位 UID 应复用同一实例（跨快照稳定）");

            // 路径二：跨进程客户端 CardManager（无模板库命中）
            var ncm = new NetClientCardManager();
            var inst2 = ncm.RebuildFromRef(hiddenRef);
            Assert.IsNotNull(inst2, "跨进程客户端 CardManager 应能解析占位引用");
            Assert.IsTrue(inst2.IsHidden && inst2.Template == null, "跨进程占位实例同样不携带模板");

            // 占位实例不携带任何运行时状态：避免通过 IsTapped/Damage 等侧信道泄露隐藏卡信息
            var hover = new ZoneCardRef { Uid = "hidden_0_2_1", Zone = CardZone.Hand, ZoneOwnerId = 2, IsHidden = true, IsTapped = true, Damage = 3 };
            var inst3 = cm.RebuildFromRef(hover);
            Assert.IsFalse(inst3.IsTapped, "占位实例不应接受运行时状态（防侧信道泄露）");
            Assert.AreEqual(0, inst3.Damage, "占位实例不应携带伤害等信息");
        }

        // ==================== (3) 消息类型信封 v2 ====================

        /// <summary>信封包裹/解包往返；载荷含 '|' 不受影响；旧裸格式回退兼容。</summary>
        public void TestNetEnvelopeRoundTripAndLegacyCompat()
        {
            string payload = "{\"IntentId\":\"i1\",\"Note\":\"a|b\"}";
            var wrapped = NetEnvelope.Wrap(NetEnvelope.Types.Intent, payload);
            Assert.IsTrue(wrapped.StartsWith(NetEnvelope.Prefix), "包裹结果应带 v2 前缀");

            Assert.IsTrue(NetEnvelope.TryUnwrap(wrapped, out var type, out var got), "信封应可解包");
            Assert.AreEqual(NetEnvelope.Types.Intent, type, "类型标签应一致");
            Assert.AreEqual(payload, got, "载荷应逐字一致（含 '|' 不被截断）");
            Assert.AreEqual(NetEnvelope.Types.Intent, NetEnvelope.UnwrapType(wrapped), "UnwrapType 应取出类型");

            // 旧格式（裸 JSON）：TryUnwrap 返回 false，载荷原样返回 → 走形状识别回退分支
            Assert.IsFalse(NetEnvelope.TryUnwrap(payload, out var legacyType, out var legacyPayload), "裸 JSON 不应被识别为信封");
            Assert.IsTrue(legacyType == null, "裸 JSON 无类型标签");
            Assert.AreEqual(payload, legacyPayload, "裸 JSON 载荷应原样返回");
            Assert.AreEqual(payload, NetEnvelope.UnwrapPayload(payload), "UnwrapPayload 对旧格式应原样返回");

            // 格式不完整的"伪前缀"不应被误判为信封（避免把普通文本吃掉）
            Assert.IsFalse(NetEnvelope.TryUnwrap("V2:broken", out _, out _), "缺少分隔符不应视为信封");
        }

        // ==================== (4) 决策超时按 Kind 配置化 ====================

        /// <summary>按 Kind 的超时映射 + 未知 Kind 回退默认值。</summary>
        public void TestChoiceTimeoutPerKind()
        {
            Assert.AreEqual(60, NetworkInputGateway.TimeoutSecondsFor(ChoiceRequest.Kind.SelectTarget), "选目标 60s");
            Assert.AreEqual(60, NetworkInputGateway.TimeoutSecondsFor(ChoiceRequest.Kind.SelectTriggerOrder), "触发排序 60s");
            Assert.AreEqual(60, NetworkInputGateway.TimeoutSecondsFor(ChoiceRequest.Kind.CombatInvestigation), "调查对抗分配 60s");
            Assert.AreEqual(45, NetworkInputGateway.TimeoutSecondsFor(ChoiceRequest.Kind.PayOrTap), "支付/横置 45s");
            Assert.AreEqual(30, NetworkInputGateway.TimeoutSecondsFor(ChoiceRequest.Kind.TieBreak), "破平确认 30s");
            Assert.AreEqual(NetworkInputGateway.DefaultTimeoutSeconds,
                NetworkInputGateway.TimeoutSecondsFor((ChoiceRequest.Kind)999), "未知 Kind 应回退默认超时");
        }

        /// <summary>超时兜底：无可回包时按配置超时返回 Cancelled（且带 RequestId 便于对账）。</summary>
        public void TestChoiceTimeoutFallbackCompletesAsCancelled()
        {
            var transport = new LoopbackTransport();
            transport.Start();
            var gateway = new NetworkInputGateway(transport) { TimeoutProvider = _ => 1 };   // 1 秒超时

            var task = gateway.RequestChoice(1, new ChoiceRequest { RequestKind = ChoiceRequest.Kind.TieBreak, ForceChoice = true });
            bool done = task.Wait(6000);

            Assert.IsTrue(done, "无回包时应在配置超时后结束（不应永久挂起）");
            Assert.IsTrue(task.Result != null && task.Result.Cancelled, "超时应以 Cancelled 回包");
            Assert.IsFalse(string.IsNullOrEmpty(task.Result.RequestId), "超时回包应带 RequestId（便于对账）");

            transport.Stop();
        }

        // ==================== (5) 客户端 UI 对齐所需的新增快照/协议字段 ====================

        /// <summary>堆叠快照新增字段（控制者/赢取地区/来源模板/目标引用）应能经序列化往返不丢。</summary>
        public void TestStackSnapCarriesClientUiFields()
        {
            var snap = new GameSnapshot
            {
                Stack = new List<StackEntrySnap>
                {
                    new StackEntrySnap
                    {
                        OwnerId = 1, ControllerId = 2, EffectText = "触发效果", IsCard = false,
                        Kind = (int)Script.Game.Entities.ResolveKind.AreaWon, WonAreaUid = "area_9", SourceTemplateUid = "JC068_agus",
                        Targets = new List<StackTargetSnap>
                        {
                            new StackTargetSnap { Kind = 0, CardUid = "inst_7" },
                            new StackTargetSnap { Kind = 1, AreaUid = "area_9", AreaSlotId = 2 },
                            new StackTargetSnap { Kind = 2, PlayerId = 2 },
                        },
                    },
                },
            };

            var back = SnapshotSerializer.Deserialize(SnapshotSerializer.Serialize(snap));
            Assert.IsNotNull(back?.Stack, "堆叠快照应可反序列化");
            Assert.AreEqual(1, back.Stack.Count, "堆叠条目数应一致");
            var e = back.Stack[0];
            Assert.AreEqual(2, e.ControllerId, "ControllerId（箭头着色）应往返保留");
            Assert.AreEqual("area_9", e.WonAreaUid, "WonAreaUid（地区卡面）应往返保留");
            Assert.AreEqual("JC068_agus", e.SourceTemplateUid, "SourceTemplateUid（来源卡图）应往返保留");
            Assert.AreEqual(3, e.Targets.Count, "目标引用应往返保留");
            Assert.AreEqual("inst_7", e.Targets[0].CardUid, "Card 目标 UID 应一致");
            Assert.AreEqual("area_9", e.Targets[1].AreaUid, "Area 目标 UID 应一致");
            Assert.AreEqual(2, e.Targets[2].PlayerId, "Player 目标编号应一致");
        }

        /// <summary>待分配调查卡只对决策者可见（信息隔离），其他人收到空表。</summary>
        public void TestInvestigationCardsFilteredToOwner()
        {
            var full = BuildFullSnapshot();
            full.InvestigationOwnerPlayerId = 1;
            full.InvestigationCards = new List<ZoneCardRef>
            {
                new ZoneCardRef { Uid = "invest_a", TemplateUid = "T_a", Zone = CardZone.Deck, ZoneOwnerId = 1 },
            };

            var v1 = SnapshotFilter.ForPlayer(full, 1);
            Assert.AreEqual(1, v1.InvestigationCards.Count, "决策者本人应收到待分配调查卡");
            Assert.AreEqual("invest_a", v1.InvestigationCards[0].Uid, "调查卡 UID 应原样下发");

            var v2 = SnapshotFilter.ForPlayer(full, 2);
            Assert.AreEqual(0, v2.InvestigationCards.Count, "对手不得收到调查卡内容（信息隔离）");
            Assert.AreEqual(0, v2.InvestigationOwnerPlayerId, "对手不应看到调查决策归属");
        }

        /// <summary>对抗类 ChoiceRequest 的补充上下文（地区/败方/类型）应能经序列化往返不丢。</summary>
        public void TestCombatChoiceRequestContextRoundTrip()
        {
            var req = new ChoiceRequest
            {
                RequestId = "req_77", PlayerId = 1, RequestKind = ChoiceRequest.Kind.CombatStrength,
                Amount = 3, AreaUid = "area_5", AreaNo = 2, LoserPlayerId = 2, CombatTypeValue = 1,
                OptionUids = new List<string> { "inst_1", "inst_2" }, ForceChoice = true,
                OptionCardRefs = new List<ZoneCardRef>
                {
                    new ZoneCardRef { Uid = "inst_1", TemplateUid = "T_1", Zone = CardZone.Deck, ZoneOwnerId = 1 },
                },
            };
            var back = ChoiceSerializer.DeserializeRequest(ChoiceSerializer.Serialize(req));
            Assert.IsNotNull(back, "ChoiceRequest 应可反序列化");
            Assert.AreEqual("area_5", back.AreaUid, "AreaUid 应往返保留");
            Assert.AreEqual(2, back.AreaNo, "AreaNo 应往返保留");
            Assert.AreEqual(2, back.LoserPlayerId, "LoserPlayerId 应往返保留");
            Assert.AreEqual(1, back.CombatTypeValue, "CombatTypeValue 应往返保留");
            Assert.AreEqual(1, back.OptionCardRefs?.Count ?? 0, "OptionCardRefs（决策期展示用卡引用）应往返保留");
            Assert.AreEqual("T_1", back.OptionCardRefs[0].TemplateUid, "OptionCardRefs 的模板 UID 应保留（客户端据此显示卡面）");
        }
    }
}
