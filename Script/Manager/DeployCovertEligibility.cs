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
    /// 判定范围（与 `TurnManager.DeployCovert` 动作的第 2.5 / 5 步同判据）：
    ///  1) 具有【公开】关键字的卡不可用于派遣暗藏（模板固有 + 运行时授予两层，见 `CardInstance.HasPublicKeyword`）；
    ///  2) 仅角色卡可派遣。
    /// 动作内的**阶段 / 行动权 / 次数 / 全局封锁**等校验不在此处（它们属于"动作可否执行"，见 `TurnManager.DeployCovert`），
    /// 以免把"能否拖起"与"能否落地"混为一谈。
    /// </summary>
    public static class DeployCovertEligibility
    {
        /// <summary>该卡是否具备"被拖起以派遣暗藏"的资格（不含阶段/行动权/次数/封锁校验）。</summary>
        public static bool IsEligible(CardInstance instance)
        {
            if (instance == null) return false;
            // 【公开】关键字的卡不可进行暗藏行动（与 DeployCovert 第 2.5 步同判据）
            if (instance.HasPublicKeyword) return false;
            // 仅角色卡可派遣（与 DeployCovert 第 5 步同判据）
            return (instance.PlayerTemplate?.CategoryFlag & CardCategory.Character) != 0;
        }
    }
}
