using Godot;
using Script.Net;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【C/S 化 P3·M1 纯托管 Server 内核】Godot 侧意图路由桥接。
    ///
    /// 实现 <see cref="IIntentRouter"/>，把意图调用桥接到真实的 Godot 业务层
    /// （PlayerActionService / CardManager）。供 Godot 客户端进程 / 单机装配使用；
    /// 服务端同样以 Godot headless 进程承载本桥接（真实规则只在服务端执行）。
    /// 注：原"纯托管 Server 内核"骨架（`ServerCore/`）已于 2026-09-05 按决策整体移除；
    /// 若将来确需去 Godot 化，迁移路径见 `联机C-S架构设计方案.md` §8.3。
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

        /// <summary>
        /// 打出卡牌意图。**【K15·多目标】**先看该卡是否声明了多目标槽位：
        /// 是且已注入输入网关 → 服务端接管（逐槽下发 `MultiTargetSlot` → `ConfirmTargets` → 入堆叠，异步）；
        /// 否则走原有同步 `PlayCard`（单目标/无目标卡行为不变）。
        /// </summary>
        public void PlayCard(int playerId, string cardUid, string targetUid, string targetCardUid)
        {
            // 【K16·模式分支】"选择一项"类卡：服务端接管（先下发选模式 → 再取该模式目标 → 入堆叠）
            if (_service.TryBeginModeCardPlayOnline(playerId, cardUid)) return;
            if (_service.TryBeginMultiTargetPlayOnline(playerId, cardUid)) return;
            _service.PlayCard(playerId, cardUid, targetUid, targetCardUid);
        }
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

            // 服务端权威复查激活目标合法性：客户端的目标过滤只是展示层防呆，禁止非法 targetUid 直接进入结算。
            // 复用核心 ActivateEligibility.IsLegalActivateTarget（本地区/类别/目标规则/暗藏者豁免）。
            if (data.Target != null && data.Target.NeedsSelection)
            {
                if (tgt == null)
                    throw new System.InvalidOperationException($"激活 {effectId} 需要指定目标（targetUid 为空）");
                if (!Script.Manager.ActivateEligibility.IsLegalActivateTarget(src, data, tgt, _cardManager))
                    throw new System.InvalidOperationException(
                        $"激活目标非法：{tgt.PlayerTemplate?.CardName ?? tgt.UID} 不符合 {effectId} 的目标规则");
            }

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
