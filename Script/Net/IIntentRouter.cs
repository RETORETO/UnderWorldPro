using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·M1 纯托管 Server 内核】意图路由抽象（服务端权威结算入口）。
    ///
    /// 用途：把 <see cref="IntentHub"/> 对 Godot 业务层（PlayerActionService / CardManager）的硬引用
    /// 解耦为纯托管接口，使 Script.Net（非 Godot 子目录）可脱离 Godot 独立编译，作为纯托管 Server 内核的
    /// 通信/DTO 地基。
    ///
    /// 签名全部为纯数据（int/string/bool），不出现 CardInstance / EffectData / Godot 类型——具体的
    /// 实例查找、效果解析由实现方（Godot 桥接层或纯托管 Server 规则内核）内部完成，本接口只约定"能执行
    /// 一条权威意图"。
    /// </summary>
    public interface IIntentRouter
    {
        /// <summary>让过（回合/响应窗口）。</summary>
        void Pass(int playerId);

        /// <summary>出牌。</summary>
        void PlayCard(int playerId, string cardUid, string targetUid, string targetCardUid);

        /// <summary>暗藏。</summary>
        void DeployCovert(int playerId, string cardUid, string areaUid);

        /// <summary>暗藏者现身。</summary>
        void RevealCovert(int playerId, string cardUid);

        /// <summary>建立资产。</summary>
        void EstablishAsset(int playerId, string cardUid);

        /// <summary>退回牌库。</summary>
        void ReturnCardToDeck(int playerId, string cardUid, bool toTop);

        /// <summary>退回手牌。</summary>
        void ReturnCardToHand(int playerId, string cardUid);

        /// <summary>抽牌。</summary>
        void DrawCards(int playerId, int count);

        /// <summary>对目标实例造成伤害（targetUid 由实现方解析为实例）。</summary>
        void DealDamage(string targetUid, int amount, bool isTrauma);

        /// <summary>地区群体伤害。</summary>
        void DealAreaDamage(string areaUid, int amount, bool isTrauma);

        /// <summary>投降。</summary>
        void Concede(int playerId);

        /// <summary>发动能力（sourceUid/effectId/targetUid 由实现方解析）。</summary>
        void ActivateAbility(string sourceUid, string effectId, string targetUid);

        /// <summary>重置行动次数。</summary>
        void ResetActionCharges(int playerId);

        /// <summary>标记"建卡观察窗"开始（用于收集本次意图新建实例的 UID）。</summary>
        void MarkCreateWatch();

        /// <summary>取出观察窗内新建的实例 UID（仅一次），无则返回 null。</summary>
        string ConsumeCreatedUid();
    }
}
