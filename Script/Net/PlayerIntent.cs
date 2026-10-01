using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块C】玩家意图指令通道 DTO（纯数据，不依赖 Godot / Manager）。
    ///
    /// 设计动机（收口前置条件 ⑥"意图 100% 收口可序列化"）：
    /// 原 PlayerActionService 仅是把"本地方法调用"集中收口，但调用方（View / 测试）直接在进程内
    /// new PlayerActionService 并同步调方法，仍属"单机命令总线雏形"——无法经网络下发服务端权威裁决。
    /// 本 DTO 把每个玩家写操作抽象为一条可序列化指令：
    ///   - 客户端构造 PlayerIntent（含玩家编号、意图种类、参数）
    ///   - 经 INetTransport 上行到服务端
    ///   - 服务端 IntentHub 反序列化、做身份/合法性校验（权威裁决），再路由到本地 PlayerActionService 执行
    ///   - 回 IntentResult（成功/失败原因 + 服务端新分配的 UID，解决 C/S 下双 Client 撞 UID 问题）
    ///
    /// 与 ChoiceRequest/ChoiceResult 的关系：
    ///   - Choice* 是"服务端→客户端"的"请做选择"请求（下行，阻塞服务端结算）；
    ///   - PlayerIntent/IntentResult 是"客户端→服务端"的"我要这么做"主动意图（上行，服务端裁决）。
    ///   两者共同构成 MTGA 类服务端权威架构的双向消息环。
    ///
    /// 纪律：本文件及 Net/ 目录下的类型不得 using Godot / Script.UI / Script.Game.Entities / Script.Manager，
    /// 以保证可被 System.Text.Json 等纯托管序列化器处理（Headless 服务端无 Godot 运行时）。
    /// </summary>
    public class PlayerIntent
    {
        /// <summary>意图唯一标识（correlation id），客户端生成，服务端回 IntentResult 时原样带回。</summary>
        public string IntentId;

        /// <summary>发起方玩家编号（游戏内 1/2）。服务端会校验该编号与连接映射一致，防止越权代操作。</summary>
        public int PlayerId;

        /// <summary>意图种类（对应 PlayerActionService 的各写操作入口）。</summary>
        public enum Kind
        {
            Pass,               // 让过（回合/响应窗口）
            PlayCard,           // 出牌（Args: cardUid[, targetUid][, targetCardUid]）
            DeployCovert,       // 暗藏（Args: cardUid, areaUid）
            RevealCovert,       // 暗藏者现身（Args: cardUid）
            EstablishAsset,     // 建立资产（Args: cardUid）
            ReturnCardToDeck,   // 退回牌库（Args: cardUid[, toTop=1/0]）
            ReturnCardToHand,   // 退回手牌（Args: cardUid）
            DrawCards,          // 抽牌（Args: count）
            DealDamage,         // 伤害（Args: targetUid, amount[, isTrauma=1/0]）
            DealAreaDamage,     // 地区群体伤害（Args: areaUid, amount[, isTrauma=1/0]）
            Concede,            // 投降（Args: 无，用 PlayerId）
            ActivateAbility,    // 发动能力（Args: sourceUid, effectId, targetUid[可空]）
            ResetActionCharges, // 重置行动次数（Args: 无）
            DebugCommand,       // PM 调试命令（Args: 整行命令字符串；仅调试/验证用）
            MulliganReport,     // 【第0回合·调度】客户端主动上报调度选择（Args: 要换掉的手牌 UID，任意数量、可空）
                                //   说明：第 0 回合**不走服务端下发决策请求**（会早于客户端订阅决策请求而丢失），
                                //   改由客户端弹调度框、玩家确认时上行本条意图；上报即代表客户端已就绪。
        }

        public Kind IntentKind;
        /// <summary>意图参数（按 Kind 约定顺序填充，字符串承载，服务端解析为对应类型）。</summary>
        public List<string> Args = new List<string>();

        /// <summary>便捷构造：生成意图 id（客户端侧调用）。</summary>
        public static PlayerIntent Create(int playerId, Kind kind, params string[] args)
        {
            var intent = new PlayerIntent
            {
                IntentId = $"int_{System.Guid.NewGuid():N}",
                PlayerId = playerId,
                IntentKind = kind,
            };
            if (args != null) intent.Args.AddRange(args);
            return intent;
        }
    }

    /// <summary>
    /// 【C/S 化 P3·模块C】服务端对 PlayerIntent 的裁决结果（纯数据）。
    /// 下行回客户端，用于反馈成功/失败，并携带"服务端权威分配的资源"（如新卡 UID）。
    /// </summary>
    public class IntentResult
    {
        /// <summary>对应请求的 IntentId，客户端据此匹配异步等待（correlation id）。</summary>
        public string IntentId;

        /// <summary>是否成功执行。</summary>
        public bool Success;

        /// <summary>失败原因（Success=false 时填充），如"非该玩家优先权""参数非法""越权"。</summary>
        public string Error;

        /// <summary>
        /// 服务端权威分配的新 UID 列表（如 CreateCardInstance 经意图创建卡时分配的 inst_xxx）。
        /// 客户端据此用服务端 UID 建本地投影，避免双 Client 各自自增撞 UID（前置条件 ⑥ 残余①）。
        /// 无新分配时为空。
        /// </summary>
        public List<string> AssignedUids = new List<string>();
    }
}
