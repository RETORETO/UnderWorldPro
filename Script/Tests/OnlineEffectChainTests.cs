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
    /// 【联机效果系统接入·四项遗留缺口】联机链路专项回归：
    ///   G2 客户端「可发动」判定可在无 EffectEngine 实例下运行（生效区判定已静态化）；
    ///   G3 玩家级费用修正（JC012①）可经快照下行并在客户端投影还原；
    ///   G4 服务端复查激活目标合法性（非法 targetUid 必须被拒）。
    /// </summary>
    public class OnlineEffectChainTests : TestRunner
    {
        private static PlayerCardData LoadTemplate(string path)
        {
            Cards.Effects.AllEffects.RegisterAll();   // 幂等：确保效果构建器已注册
            var tmpl = ResourceLoader.Load<PlayerCardData>(path);
            if (tmpl != null) Script.Data.Effects.CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        /// <summary>G2：客户端独立投影下调用 ActivateEligibility.CanActivate 不应抛异常。
        /// 关键点：该方法内部原先用 EffectEngine.Instance.IsEffectActiveInZone，客户端进程无该实例会 NRE；
        /// 现已改为静态调用，故客户端可安全复用核心判定做「可发动」高亮。</summary>
        public void TestClientActivateEligibilityRuns()
        {
            var server = new GameInitializer { DebugSeed = 23 };
            server.InitializeAll();
            server.StartMatch();

            var areas = server.PlayerActionService.SetupInitialAreas(1);
            Assert.IsTrue(areas.Count > 0, "应能摆放 1 个初始地区");
            string areaUid = areas[0].UID;

            var hand = server.ZoneManager.GetZoneCards(CardZone.Hand, 1);
            Assert.IsTrue(hand.Count > 0, "玩家1 起手应有手牌");
            server.CardManager.MoveCardToArea(hand[0], areaUid);

            // 客户端独立投影（模拟跨进程：独立 CardManager + 投影 Manager，与服务端不共享实例）
            var clientCard = new NetClientCardManager();
            var clientDeck = new DeckManager();
            clientDeck.Initialize(clientCard, new Script.Game.SeededRandomSource(1));
            var clientZone = new ZoneManager();
            clientZone.Initialize(clientCard, clientDeck);
            clientCard.SetZoneManager(clientZone);
            var clientPlayer = new PlayerManager();
            clientPlayer.SetZoneManager(clientZone);
            var clientStack = new StackManager();
            clientStack.SetCardManager(clientCard);
            var clientTurn = new TurnManager();
            clientTurn.Initialize(clientDeck, clientPlayer, clientCard);
            clientTurn.SetZoneManager(clientZone);
            clientTurn.SetStackManager(clientStack);

            var snap = server.BuildSnapshot();
            clientZone.ApplySnapshot(snap.Zones);
            clientTurn.ApplySnapshot(snap);

            var areaCards = clientZone.GetZoneCards(CardZone.AreaZone, 0);
            Assert.IsTrue(areaCards.Count > 0, "客户端投影争夺区应有卡（用于覆盖判定路径）");

            bool threw = false;
            try
            {
                foreach (var inst in areaCards)
                    ActivateEligibility.CanActivate(inst, clientTurn, clientPlayer, clientCard);
            }
            catch (System.Exception ex)
            {
                threw = true;
                GD.Print($"[Test] 客户端 CanActivate 抛异常：{ex.GetType().Name}: {ex.Message}");
            }
            Assert.IsFalse(threw, "客户端投影调用 ActivateEligibility.CanActivate 不应抛异常（生效区判定已静态化）");
        }

        /// <summary>G3：玩家级费用修正应经快照完整下行（导出 → 序列化 → 反序列化）。</summary>
        public void TestCostModsSnapshotRoundTrip()
        {
            var server = new GameInitializer { DebugSeed = 24 };
            server.InitializeAll();
            server.StartMatch();

            // 模拟 JC012①：玩家1 获得「下一张帷幕守望牌费用-1」（一次性）
            EffectEngine.Instance.ApplyCostModification(1, -1, server.TurnManager.TurnCount + 3, "帷幕守望", consumeOnce: true);

            var snap = server.BuildSnapshot();
            Assert.IsNotNull(snap, "服务端应能导出快照");
            Assert.AreEqual(1, snap.CostMods.Count, "快照应含 1 条玩家级费用修正（JC012①）");
            Assert.AreEqual(1, snap.CostMods[0].PlayerId, "费用修正应归属玩家1");
            Assert.AreEqual(-1, snap.CostMods[0].Amount, "费用修正量应为 -1");
            Assert.AreEqual("帷幕守望", snap.CostMods[0].FilterText, "费用修正过滤串应为『帷幕守望』");

            var json = SnapshotSerializer.Serialize(snap);
            var back = SnapshotSerializer.Deserialize(json);
            Assert.IsNotNull(back, "快照应能反序列化");
            Assert.AreEqual(1, back.CostMods.Count, "反序列化后费用修正条数应一致");
            Assert.AreEqual(-1, back.CostMods[0].Amount, "反序列化后修正量应一致");
            Assert.IsTrue(back.CostMods[0].ConsumeOnce, "反序列化后一次性标记应一致");
        }

        /// <summary>G4：联机桥必须复查激活目标合法性（客户端过滤只是展示层）。</summary>
        public void TestServerRejectsIllegalActivationTarget()
        {
            var server = new GameInitializer { DebugSeed = 25 };
            server.InitializeAll();
            server.StartMatch();
            var cm = server.CardManager;

            var jc012 = LoadTemplate("res://Cards/Data/PlayerCards/JC/JC012_weimushouwanghuiji.tres");
            Assert.IsNotNull(jc012, "应能加载 JC012 模板");
            var charTmpl = new PlayerCardData { UID = "TEST_Character", CardName = "测试角色", CategoryFlag = CardCategory.Character };

            var src = cm.CreateCardInstance(jc012, 1);
            var normal = cm.CreateCardInstance(charTmpl, 1);
            var covert = cm.CreateCardInstance(charTmpl, 1);
            covert.IsCovert = true;

            var router = new GodotIntentRouter(server.PlayerActionService, cm, null);

            bool threwOnCovert = false;
            try { router.ActivateAbility(src.UID, "JC012_2_Tap", covert.UID); }
            catch (System.InvalidOperationException) { threwOnCovert = true; }
            Assert.IsTrue(threwOnCovert, "以暗藏者为目标应被服务端拒绝（TargetSpec.IncludeCovert=false）");

            bool threwOnMissing = false;
            try { router.ActivateAbility(src.UID, "JC012_2_Tap", ""); }
            catch (System.InvalidOperationException) { threwOnMissing = true; }
            Assert.IsTrue(threwOnMissing, "需目标效果缺 targetUid 应被服务端拒绝");

            bool threwOnLegal = false;
            try { router.ActivateAbility(src.UID, "JC012_2_Tap", normal.UID); }
            catch (System.Exception ex) { threwOnLegal = true; GD.Print($"[Test] 合法目标激活异常：{ex.Message}"); }
            Assert.IsFalse(threwOnLegal, "合法角色目标不应被服务端目标校验拒绝");
        }
    }
}
