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
    /// 【C/S 化 P2 端到端内核验证】验证 NetworkInputGateway + LoopbackTransport 能真正代替
    /// AutoInputGateway 跑通 *Async 链路（服务端下发请求→客户端回包→服务端续算）。
    /// 仍非自动对战：用固定脚本构造一次 PrepareUpkeep 结算 + 模拟客户端自动回 Pay。
    /// </summary>
    public class NetP2Tests : TestRunner
    {
        private CardInstance NewAttached(string uid, int ctrl = 1, CardZone zone = CardZone.AttachedZone)
        {
            var tmpl = new CouncilCardData { CardName = "P2验证卡" };
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = zone;
            inst.IsAttached = zone == CardZone.AttachedZone;
            return inst;
        }

        public void TestNetworkGatewayEndToEnd()
        {
            var transport = new LoopbackTransport();
            transport.Start();

            // 模拟"玩家1客户端"：收到任何请求都回 Pay=true（等价于自动付费）
            transport.RegisterClient(1, serverMsg =>
            {
                var req = ChoiceSerializer.DeserializeRequest(serverMsg);
                if (req != null)
                {
                    var result = new ChoiceResult { RequestId = req.RequestId, Pay = true, Cancelled = false };
                    transport.ClientReply(1, ChoiceSerializer.SerializeResult(result));
                }
            });

            var gateway = new NetworkInputGateway(transport);
            // 统一分发方：服务端收到客户端上行 ChoiceResult 后，显式转发给网关完成挂起请求。
            // （NetworkInputGateway 设计为不自行订阅 OnMessageReceived，避免与传输其他订阅者互相覆盖。）
            transport.OnMessageReceived((pid, msg) => gateway.TryHandleResult(msg));

            var ee = new Script.Manager.EffectEngine();
            ee.InputGateway = gateway;

            var eff = new EffectData
            {
                EffectId = "P2_TEST_PrepareUpkeep",
                Timing = EffectTiming.OnTurnStarted,
                Scope = ZoneScope.AttachTarget,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PrepareUpkeep, Args = new Array<string> { "1" } }
                },
                Duration = "强制",
            };
            var src = NewAttached("src", 1);

            // 经网络网关的异步结算：服务端下发→客户端回包→续算
            var task = ee.ResolveEffectAsync(eff, src, null);
            var completed = task.Wait(5000);

            Assert.IsTrue(completed, "经网络网关的 ResolveEffectAsync 应在客户端回包后完成（无 UI 阻塞）");
            Assert.IsFalse(task.IsFaulted, "经网络网关不应抛异常");

            transport.Stop();
        }
    }
}
