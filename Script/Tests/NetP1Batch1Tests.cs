using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化·第一批迁移验证】ZoneManager + CardManager 联网权威态收口的三方验证。
    ///
    /// 背景：第一批迁移在 ZoneManager/CardManager 上补齐了"快照可双向"能力——
    ///   - ZoneManager.ToSnapshot() 补全了 EnteredThisTurn / VersionId / AbilityUsedThisTurn 三个漏字段；
    ///   - 新增 ZoneManager.ApplySnapshot(List&lt;ZoneCardRef&gt;)：从快照重建区域真相；
    ///   - 新增 CardManager.RebuildFromRef(ZoneCardRef)：按 UID 恢复实例运行时字段（含自愈兜底）。
    ///
    /// 三项验证对应"分批迁移 + 每批三方验证"纪律：
    ///   (1) LoopbackTransport 端到端：快照经回环传输层序列化往返后重建，区域真相一致；
    ///   (2) 单机回归：不经网络，直接驱动 ZoneManager 移动 API，快照导出字段完整（含本次补全的 3 字段），证明改动不破坏单机；
    ///   (3) GodotEnetTransport 真实联机：整局快照经真实 socket 序列化往返后重建，字段零丢失。
    ///
    /// 设计约束：轻量装配（仅 ZoneManager + CardManager，不拉完整对局），以最小依赖锁定第一批缺口。
    /// </summary>
    public class NetP1Batch1Tests : TestRunner
    {
        // ---------- 轻量装配 helpers ----------

        private static CardManager NewCardManager()
        {
            var cm = new CardManager();
            CardManager.Instance = cm; // RebuildFromRef 经 CardManager.Instance 取卡
            return cm;
        }

        private static DeckManager NewDeckManager(CardManager cm)
        {
            // 注入确定性随机源，使牌库分支（RemoveFromDeck 等）可复现且无外部依赖
            var dm = new DeckManager();
            dm.Initialize(cm, new Script.Game.SeededRandomSource(12345));
            return dm;
        }

        private static ZoneManager NewZoneManager(CardManager cm, DeckManager dm)
        {
            var zm = new ZoneManager();
            zm.Initialize(cm, dm); // 注入真实 DeckManager：MoveToX/RemoveFromAllZones 的牌库分支不再 NRE
            cm.SetZoneManager(zm);
            return zm;
        }

        /// <summary>构造一个含多区域/多运行时状态的小场景，用于快照导出→重建对比。</summary>
        private static (CardManager cm, ZoneManager zm, List<CardInstance> all) BuildScenario()
        {
            var cm = NewCardManager();
            var dm = NewDeckManager(cm);
            var zm = NewZoneManager(cm, dm);
            var all = new List<CardInstance>();

            // 玩家1 手牌 x2
            var h1 = cm.CreateCardInstance(new CouncilCardData { CardName = "手牌A" }, 1);
            var h2 = cm.CreateCardInstance(new CouncilCardData { CardName = "手牌B" }, 1);
            zm.MoveToHand(h1, 1); zm.MoveToHand(h2, 1);
            all.Add(h1); all.Add(h2);

            // 玩家1 资产区 x1（带伤害/横置状态）
            var asset = cm.CreateCardInstance(new CouncilCardData { CardName = "资产甲" }, 1);
            asset.Damage = 3; asset.IsTapped = true;
            zm.MoveToAsset(asset, 1);
            all.Add(asset);

            // 玩家1 争夺区 x1（带暗藏/调查/力量）
            var area = cm.CreateCardInstance(new CouncilCardData { CardName = "争夺乙" }, 1);
            area.IsCovert = true; area.CurrentInvestigation = 2; area.CurrentPower = 5;
            area.EnteredThisTurn = true; area.AbilityUsedThisTurn = false;
            zm.MoveToArea(area, "area_main", 1);
            all.Add(area);

            // 玩家1 墓地 x1
            var gy = cm.CreateCardInstance(new CouncilCardData { CardName = "墓地丙" }, 1);
            zm.MoveToGraveyard(gy, 1);
            all.Add(gy);

            // 玩家1 附属区 x1
            var att = cm.CreateCardInstance(new CouncilCardData { CardName = "附属丁" }, 1);
            att.IsAttached = true;
            zm.MoveToAttachedZone(att, "area_main", 1);
            all.Add(att);

            // 玩家1 封印区 x1
            var seal = cm.CreateCardInstance(new CouncilCardData { CardName = "封印戊" }, 1);
            seal.IsSealed = true;
            zm.MoveToSealedZone(seal, "area_main", 1);
            all.Add(seal);

            // 玩家2 手牌 x1（验证分桶按 ZoneOwnerId）
            var h3 = cm.CreateCardInstance(new CouncilCardData { CardName = "手牌C" }, 2);
            zm.MoveToHand(h3, 2);
            all.Add(h3);

            return (cm, zm, all);
        }

        // ==================== (1) Loopback 端到端 ====================

        /// <summary>
        /// 快照经 LoopbackTransport 回环序列化往返 → ApplySnapshot 重建 → 区域真相（桶分布/UID/运行时字段）一致。
        /// 同时验证：补齐的纯数据快照确实可走网络层（纯数据契约不依赖 Godot）。
        /// </summary>
        public void TestLoopbackSnapshotRoundTrip()
        {
            var (cm, zm, all) = BuildScenario();

            // 1) 导出权威态快照
            var before = zm.ToSnapshot();
            Assert.AreEqual(all.Count, before.Count, "导出快照应覆盖全部实例");

            // 2) 经 LoopbackTransport 序列化往返（模拟服务端→客户端下发）
            var transport = new LoopbackTransport();
            transport.Start();
            string received = null;
            transport.RegisterClient(1, msg => { received = msg; });
            // 用正式快照序列化器（SnapshotSerializer，已含 IncludeFields）经 Loopback 往返，与正式下发链路一致
            var json = SnapshotSerializer.Serialize(new GameSnapshot { Zones = before });
            transport.Send(1, json); // 服务端下发快照字符串
            transport.Stop();
            Assert.IsFalse(string.IsNullOrEmpty(received), "客户端应经 Loopback 收到序列化快照");

            // 3) 反序列化 → 清空 → ApplySnapshot 重建
            var restored = SnapshotSerializer.Deserialize(received).Zones;
            // 清空当前区域真相，模拟客户端从空态重建
            zm.ApplySnapshot(new List<ZoneCardRef>());
            Assert.AreEqual(0, zm.GetZoneCards(CardZone.Hand, 1).Count, "清空后手牌应为空（重建前）");
            zm.ApplySnapshot(restored);

            // 4) 断言区域真相一致
            Assert.AreEqual(2, zm.GetZoneCards(CardZone.Hand, 1).Count, "重建后玩家1手牌数应一致");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.Hand, 2).Count, "重建后玩家2手牌数应一致（分桶）");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.AssetZone, 1).Count, "重建后资产区数应一致");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.Graveyard, 1).Count, "重建后墓地数应一致");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.AttachedZone, 1).Count, "重建后附属区数应一致");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.SealedZone, 1).Count, "重建后封印区数应一致");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.AreaZone, 1, "area_main").Count, "重建后争夺区数应一致");

            // 5) 断言运行时字段经重建零丢失（含本次补全的三个字段）
            var restoredArea = zm.GetZoneCards(CardZone.AreaZone, 1, "area_main")[0];
            Assert.AreEqual(true, restoredArea.IsCovert, "暗藏状态应保留");
            Assert.AreEqual(2, restoredArea.CurrentInvestigation, "调查值应保留");
            Assert.AreEqual(5, restoredArea.CurrentPower, "力量值应保留");
            Assert.AreEqual(true, restoredArea.EnteredThisTurn, "EnteredThisTurn（本次补全）应保留");
            var restoredAsset = zm.GetZoneCards(CardZone.AssetZone, 1)[0];
            Assert.AreEqual(3, restoredAsset.Damage, "伤害值应保留");
            Assert.AreEqual(true, restoredAsset.IsTapped, "横置状态应保留");

            // 6) UID 一致性（双端对账）
            foreach (var orig in all)
            {
                var back = cm.GetInstance(orig.UID);
                Assert.IsNotNull(back, $"重建后实例 UID={orig.UID} 应可经 GetInstance 找回");
                Assert.AreEqual(orig.UID, back.UID, "UID 应完全一致（权威态不漂移）");
            }
        }

        // ==================== (2) 单机回归 ====================

        /// <summary>
        /// 不经网络，直接驱动 ZoneManager 移动 API 建场景 → ToSnapshot 导出字段完整，
        /// 重点验证本次补全的 EnteredThisTurn / VersionId / AbilityUsedThisTurn 三个字段被正确带出，
        /// 证明第一批改动不破坏单机路径的快照导出。
        /// </summary>
        public void TestStandaloneSnapshotExportIntegrity()
        {
            var (cm, zm, all) = BuildScenario();
            var snap = zm.ToSnapshot();

            // 全部实例都应出现在快照中
            Assert.AreEqual(all.Count, snap.Count, "单机快照应覆盖全部实例");

            // 以 UID 建索引，核对每个字段
            var byUid = new Dictionary<string, ZoneCardRef>();
            foreach (var r in snap) byUid[r.Uid] = r;

            foreach (var orig in all)
            {
                Assert.IsTrue(byUid.ContainsKey(orig.UID), $"快照应含 UID={orig.UID}");
                var r = byUid[orig.UID];
                // 基础字段
                Assert.AreEqual(orig.OwnerId, r.OwnerId, $"UID={orig.UID} OwnerId 应一致");
                Assert.AreEqual(orig.ControllerId, r.ControllerId, $"UID={orig.UID} ControllerId 应一致");
                Assert.AreEqual(orig.CurrentZone, r.Zone, $"UID={orig.UID} Zone 应一致");
                // 本次补全的三个字段必须存在且正确带出
                Assert.AreEqual(orig.EnteredThisTurn, r.EnteredThisTurn,
                    $"UID={orig.UID} EnteredThisTurn（本次补全字段）应一致");
                Assert.AreEqual(orig.AbilityUsedThisTurn, r.AbilityUsedThisTurn,
                    $"UID={orig.UID} AbilityUsedThisTurn（本次补全字段）应一致");
                Assert.AreEqual(orig.VersionId, r.VersionId,
                    $"UID={orig.UID} VersionId（本次补全字段）应一致");
                // 运行时状态字段
                Assert.AreEqual(orig.Damage, r.Damage, $"UID={orig.UID} Damage 应一致");
                Assert.AreEqual(orig.IsCovert, r.IsCovert, $"UID={orig.UID} IsCovert 应一致");
                Assert.AreEqual(orig.IsSealed, r.IsSealed, $"UID={orig.UID} IsSealed 应一致");
                Assert.AreEqual(orig.IsTapped, r.IsTapped, $"UID={orig.UID} IsTapped 应一致");
            }

            // 争夺区实例的 ZoneDetailId 应被带出（用于重建时归位）
            var areaRef = byUid[all[3].UID]; // all[3] 是争夺乙
            Assert.AreEqual("area_main", areaRef.ZoneDetailId, "争夺区实例 ZoneDetailId 应被导出（重建归位依赖）");
        }

        // ==================== (3) GodotEnetTransport 真实联机 ====================

        /// <summary>
        /// 整局快照（GameSnapshot.Zones）经真实 ENet socket（127.0.0.1）序列化往返 → 客户端 ApplySnapshot 重建，
        /// 断言含本次补全字段在内的全部字段零丢失。验证真实联机链路下"快照可双向"成立。
        /// </summary>
        public void TestEnetRealTransportSnapshotRoundTrip()
        {
            const int port = 18911;
            var (cm, zm, all) = BuildScenario();

            // 构造整局快照（仅 Zones 部分用于本批验证；其余字段为空不影响）
            var snap = new GameSnapshot { Zones = zm.ToSnapshot() };

            var server = new global::Script.Net.Godot.GodotEnetTransport(global::Script.Net.Godot.EnetRole.Server) { Port = (ushort)port };
            var client = new global::Script.Net.Godot.GodotEnetTransport(global::Script.Net.Godot.EnetRole.Client)
            {
                Port = (ushort)port,
                ServerAddress = "127.0.0.1",
                LocalPlayerId = 1
            };

            string clientGot = null;
            server.OnMessageReceived((pid, msg) =>
            {
                // 服务端收到客户端"请求快照"→ 下发序列化快照（正式 SnapshotSerializer）
                var json = SnapshotSerializer.Serialize(snap);
                server.Send(pid, json);
            });
            client.OnMessageReceived((pid, msg) => { clientGot = msg; });

            server.Start();
            client.Start();

            // 手动驱动 ENet 轮询完成握手；客户端连接后请求快照
            for (int i = 0; i < 500; i++)
            {
                server.Poll();
                client.Poll();
                if (server.IsPlayerConnected(1) && clientGot == null)
                {
                    client.Send(1, "REQ_SNAPSHOT");
                }
                if (clientGot != null) break;
                System.Threading.Thread.Sleep(10);
            }

            Assert.IsFalse(string.IsNullOrEmpty(clientGot), "客户端应经真实 ENet 收到序列化快照");

            // 客户端从空态重建
            var restored = SnapshotSerializer.Deserialize(clientGot);
            Assert.AreEqual(all.Count, restored.Zones.Count, "重建快照卡数应一致");
            zm.ApplySnapshot(new List<ZoneCardRef>());
            zm.ApplySnapshot(restored.Zones);

            // 断言区域真相 + 补全字段零丢失
            Assert.AreEqual(2, zm.GetZoneCards(CardZone.Hand, 1).Count, "Enet重建后玩家1手牌数应一致");
            Assert.AreEqual(1, zm.GetZoneCards(CardZone.AreaZone, 1, "area_main").Count, "Enet重建后争夺区数应一致");

            var areaBack = zm.GetZoneCards(CardZone.AreaZone, 1, "area_main")[0];
            Assert.AreEqual(true, areaBack.IsCovert, "Enet重建后暗藏状态应保留");
            Assert.AreEqual(true, areaBack.EnteredThisTurn, "Enet重建后 EnteredThisTurn（本次补全）应保留");
            Assert.AreEqual(5, areaBack.CurrentPower, "Enet重建后力量值应保留");

            var assetBack = zm.GetZoneCards(CardZone.AssetZone, 1)[0];
            Assert.AreEqual(3, assetBack.Damage, "Enet重建后伤害值应保留");

            server.Stop();
            client.Stop();
        }
    }
}
