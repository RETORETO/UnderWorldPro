using Godot;
using Script.Net;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【C/S 化 P3·M1 纯托管 Server 内核】Godot 侧意图路由桥接。
    ///
    /// 实现 <see cref="IIntentRouter"/>，把意图调用桥接到真实的 Godot 业务层
    /// （PlayerActionService / CardManager）。供 Godot 客户端进程 / 单机装配使用；
    /// 纯托管 Server 进程则用另一套无 Godot 的实现（见 ServerCore 冒烟）。
    ///
    /// 桥接层处理 IIntentRouter 纯数据签名 → CardInstance 实例/EffectData 的解析。
    /// </summary>
    public class GodotIntentRouter : IIntentRouter, IUIDWatch
    {
        private readonly PlayerActionService _service;
        private readonly CardManager _cardManager;
        private readonly PMCommandManager _pm;

        public GodotIntentRouter(PlayerActionService service, CardManager cardManager, PMCommandManager pm = null)
        {
            _service = service;
            _cardManager = cardManager;
            _pm = pm;
        }

        public void Pass(int playerId) => _service.Pass(playerId);
        public void PlayCard(int playerId, string cardUid, string targetUid, string targetCardUid)
            => _service.PlayCard(playerId, cardUid, targetUid, targetCardUid);
        public void DeployCovert(int playerId, string cardUid, string areaUid)
            => _service.DeployCovert(playerId, cardUid, areaUid);
        public void RevealCovert(int playerId, string cardUid) => _service.RevealCovert(playerId, cardUid);
        public void EstablishAsset(int playerId, string cardUid) => _service.EstablishAsset(playerId, cardUid);
        public void ReturnCardToDeck(int playerId, string cardUid, bool toTop)
            => _service.ReturnCardToDeck(playerId, cardUid, toTop);
        public void ReturnCardToHand(int playerId, string cardUid) => _service.ReturnCardToHand(playerId, cardUid);
        public void DrawCards(int playerId, int count) => _service.DrawCards(playerId, count);

        public void DealDamage(string targetUid, int amount, bool isTrauma)
        {
            var tgt = _cardManager.GetInstance(targetUid);
            if (tgt == null) throw new System.InvalidOperationException($"伤害目标不存在: {targetUid}");
            _service.DealDamage(tgt, amount, isTrauma);
        }

        public void DealAreaDamage(string areaUid, int amount, bool isTrauma)
            => _service.DealAreaDamage(areaUid, amount, isTrauma);

        public void Concede(int playerId) => _service.Concede(playerId);

        public void ActivateAbility(string sourceUid, string effectId, string targetUid)
        {
            var src = _cardManager.GetInstance(sourceUid);
            var tgt = string.IsNullOrEmpty(targetUid) ? null : _cardManager.GetInstance(targetUid);
            var data = _cardManager.GetEffectData(sourceUid, effectId);
            if (src == null || data == null) throw new System.InvalidOperationException("发动能力参数无效");
            _service.ActivateAbility(src, data, tgt);
        }

        public void ResetActionCharges(int playerId) => _service.ResetActionCharges(playerId);

        public void DebugCommand(int playerId, string commandLine)
        {
            if (_pm == null)
            {
                GD.PrintErr("[GodotIntentRouter] PMCommandManager 未注入，DebugCommand 不可用");
                return;
            }
            _pm.ExecuteCommand(commandLine);
        }

        public void MarkCreateWatch() => _cardManager.MarkCreateWatch();
        public string ConsumeCreatedUid() => _cardManager.ConsumeCreatedUid();
    }
}
