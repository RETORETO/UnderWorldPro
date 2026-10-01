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

        /// <summary>执行一条 PM 调试命令（仅调试/验证用；服务端权威执行，字符串为整行命令）。</summary>
        void DebugCommand(int playerId, string commandLine);

        /// <summary>
        /// 【第0回合·调度】客户端主动上报调度选择：`pickedUids` 为**要换掉**的手牌 UID（任意数量、可空）。
        /// 服务端按该玩家真实手牌权威复检；双方都上报后自动结算（置底 → 抽等量 → 洗牌）并开始第一回合。
        /// </summary>
        void MulliganReport(int playerId, System.Collections.Generic.IList<string> pickedUids);
    }

    /// <summary>
    /// 【窄接口】建卡 UID 观察窗（C/S 化 UID 下发辅助）。
    /// 从 IIntentRouter 拆出，避免把"实现细节"暴露在主路由契约上：
    /// 仅当路由实现同时需要收集"本次意图新建实例 UID"时才实现本接口，
    /// IntentHub 通过类型判断（is IUIDWatch）按需启用，不污染主路由抽象。
    /// </summary>
    public interface IUIDWatch
    {
        /// <summary>标记观察窗开始：其后第一次建卡产生的 UID 可被 ConsumeCreatedUid 取出。</summary>
        void MarkCreateWatch();

        /// <summary>取出观察窗内新建实例 UID（仅一次），无则返回 null。</summary>
        string ConsumeCreatedUid();
    }
}
