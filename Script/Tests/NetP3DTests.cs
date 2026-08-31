using System.Collections.Generic;
using Godot;
using Script.Enums;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【C/S 化 P3·模块D】渲染事件→网络消息映射验证（前置条件 ⑧ 收口）：
    ///  (1) EventSerializer 往返：RenderEvent 全字段（含枚举 Kind、CardZone int 承载）序列化不丢；
    ///  (2) EventToMessageMapper 工厂：各 CreateXxx 正确组装纯数据字段；
    ///  (3) Publish 触发输出回调（映射层出口）。
    /// 注：全部纯托管，不依赖 Godot 场景；Manager 事件实际接入（OnCardMoved 等回调抽取参数）由 UI 层
    /// 在联机装配时绑定，本测试验证映射层本身正确性。
    /// </summary>
    public class NetP3DTests : TestRunner
    {
        /// <summary>(1) RenderEvent 全字段 JSON 往返不丢。</summary>
        public void TestRenderEventSerializeRoundTrip()
        {
            var evt = new RenderEvent
            {
                Kind = RenderEvent.EventKind.CardMoved,
                Uid = "inst_5",
                PlayerId = 1,
                ToZone = (int)CardZone.AreaZone,
                ZoneOwnerId = 1,
                ZoneDetailId = "areaA",
                IsTapped = true,
                Damage = 2,
            };
            var json = EventSerializer.Serialize(evt);
            var back = EventSerializer.Deserialize(json);

            Assert.AreEqual(RenderEvent.EventKind.CardMoved, back.Kind, "Kind 枚举往返应一致（IncludeFields 关键）");
            Assert.AreEqual("inst_5", back.Uid, "Uid 应保留");
            Assert.AreEqual((int)CardZone.AreaZone, back.ToZone, "ToZone(int 承载) 应保留");
            Assert.AreEqual("areaA", back.ZoneDetailId, "ZoneDetailId 应保留");
            Assert.IsTrue(back.IsTapped, "IsTapped 应保留");
            Assert.AreEqual(2, back.Damage, "Damage 应保留");
        }

        /// <summary>(2) EventToMessageMapper 工厂正确组装字段。</summary>
        public void TestEventToMessageMapperFactories()
        {
            var published = new List<RenderEvent>();
            var mapper = new EventToMessageMapper(evt => published.Add(evt));

            mapper.Publish(mapper.CreateCardMoved("c1", CardZone.Graveyard, 0, null));
            mapper.Publish(mapper.CreateCardStateChanged("c2", (int)CardChangeKind.TappedChanged, false, true, 3, 1));
            mapper.Publish(mapper.CreateCardPlayed(2, "c3", "targetX"));
            mapper.Publish(mapper.CreatePhaseChanged((int)TurnManager.TurnPhase.ActionFirst));
            mapper.Publish(mapper.CreatePriorityChanged(2));
            mapper.Publish(mapper.CreateTurnStarted(5, 1));
            mapper.Publish(mapper.CreateGameOver(1));
            mapper.Publish(mapper.CreateAreaWon(1, "areaB"));
            mapper.Publish(mapper.CreateAssetsChanged());

            Assert.AreEqual(9, published.Count, "应发布 9 条渲染事件");
            Assert.AreEqual(RenderEvent.EventKind.CardMoved, published[0].Kind, "[0] CardMoved");
            Assert.AreEqual((int)CardZone.Graveyard, published[0].ToZone, "[0] ToZone=Graveyard");
            Assert.AreEqual(RenderEvent.EventKind.CardStateChanged, published[1].Kind, "[1] CardStateChanged");
            Assert.IsTrue(published[1].IsTapped && published[1].Damage == 3, "[1] 状态字段");
            Assert.AreEqual("targetX", published[2].TargetUid, "[2] CardPlayed 目标");
            Assert.AreEqual((int)TurnManager.TurnPhase.ActionFirst, published[3].Phase, "[3] Phase");
            Assert.AreEqual(2, published[4].PriorityPlayerId, "[4] 优先权玩家");
            Assert.AreEqual(5, published[5].TurnCount, "[5] 回合数");
            Assert.AreEqual(1, published[6].PlayerId, "[6] 获胜者");
            Assert.AreEqual("areaB", published[7].Uid, "[7] 赢取地区");
            Assert.AreEqual(RenderEvent.EventKind.AssetsChanged, published[8].Kind, "[8] AssetsChanged");
        }

        /// <summary>(3) 端到端：事件 → 消息 → 序列化 → 反序列化字段完整（模拟传输下发）。</summary>
        public void TestEventMessageEndToEnd()
        {
            // 模拟"事件回调 → mapper → 序列化 → 传输 → 反序列化 → 消费"
            string overWire = null;
            var mapper = new EventToMessageMapper(evt => overWire = EventSerializer.Serialize(evt));
            mapper.Publish(mapper.CreateCardDrawn(2, "inst_9"));

            Assert.IsFalse(string.IsNullOrEmpty(overWire), "应已序列化下发");
            var received = EventSerializer.Deserialize(overWire);
            Assert.AreEqual(RenderEvent.EventKind.CardDrawn, received.Kind, "下行事件 Kind 应一致");
            Assert.AreEqual(2, received.PlayerId, "抽牌玩家应一致");
            Assert.AreEqual("inst_9", received.Uid, "抽牌卡 UID 应一致");
        }
    }
}
