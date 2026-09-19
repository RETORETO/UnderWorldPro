using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 「派遣暗藏」可拖拽资格判定（单一真相源）。
    ///
    /// 背景：单机 `CardDragController.IsDraggableUnderMask` 在**拖起手牌**时调用
    /// `TurnManager.CanDeployCovert(instance)` 决定该牌能否被拿起；联机客户端（快照投影）也需要同一资格判定，
    /// 但其没有 TurnManager 实例。故把该纯函数抽取到此静态类，由两处共用，避免规则出现两份实现。
    ///
    /// 判定范围（与 `TurnManager.DeployCovert` 动作的第 2.3 / 2.5 / 5 步同判据）：
    ///  1) 玩家级全局封锁（JC068 阿格斯IV「未横置则对手不能秘密派遣」）→ 由 `deployCovertBlocked` 入参表达；
    ///  2) 具有【公开】关键字的卡不可用于派遣暗藏（模板固有 + 运行时授予两层，见 `CardInstance.HasPublicKeyword`）；
    ///  3) 仅角色卡可派遣。
    ///
    /// 【P1·同一判定】封锁项原先是"服务端动作里的旁路"（客户端完全不知道），导致客户端允许拖起、服务端才拒绝。
    /// 现在它是本函数的显式入参：服务端喂 `EffectEngine.IsDeployCovertBlocked`（权威），
    /// 客户端喂快照投影态（`PlayerManager.IsDeployCovertBlockedProjected`）——**一份逻辑，两个输入来源**。
    ///
    /// 仍不在本函数内：阶段 / 行动权 / 次数等"情境"校验（它们属于"动作可否执行"，
    /// 见 `TurnManager.DeployCovert`），以免把"能否拖起"与"能否落地"混为一谈。
    /// </summary>
    public static class DeployCovertEligibility
    {
        /// <summary>该卡是否具备"被拖起以派遣暗藏"的资格（不含阶段/行动权/次数校验）。</summary>
        /// <param name="deployCovertBlocked">该卡控制者当前是否被全局封锁秘密派遣（JC068）。
        /// 默认 false = 不封锁（旧调用点/单测语义不变）。</param>
        public static bool IsEligible(CardInstance instance, bool deployCovertBlocked = false)
        {
            if (instance == null) return false;
            // 全局封锁：玩家被"未横置阿格斯IV"封锁时，本次派遣资格不成立（与 DeployCovert 第 2.3 步同判据）
            if (deployCovertBlocked) return false;
            // 【公开】关键字的卡不可进行暗藏行动（与 DeployCovert 第 2.5 步同判据）
            if (instance.HasPublicKeyword) return false;
            // 仅角色卡可派遣（与 DeployCovert 第 5 步同判据）
            return (instance.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0;
        }
    }
}
