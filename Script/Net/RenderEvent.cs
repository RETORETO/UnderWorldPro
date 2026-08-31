using System.Collections.Generic;
using Script.Enums;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块D】渲染事件网络消息（纯数据，可序列化）。
    ///
    /// 前置条件 ⑧（事件总线→网络消息映射）：当前渲染事件（ZoneManager.OnCardMoved、CardManager
    /// .OnCardStateChanged、TurnManager.OnPhaseChanged 等）携带 CardInstance 等 Godot 实体参数，
    /// 无法直接序列化下发。本 DTO 把"渲染事件"统一为纯数据的可序列化消息，供 EventToMessageMapper
    /// 从事件回调参数中抽取组装；调用方再经传输层下发/消费。
    ///
    /// 纪律：本文件及 Net/ 目录下的类型不得 using Godot / Script.UI / Script.Game.Entities / Script.Manager，
    /// 保证可被 System.Text.Json 序列化（Headless 服务端 / 独立网络客户端均可复用）。
    /// </summary>
    public class RenderEvent
    {
        /// <summary>事件种类。</summary>
        public enum EventKind
        {
            CardMoved,      // 卡从一区域移到另一区域（ZoneManager.OnCardMoved / OnPostMove）
            CardStateChanged, // 卡状态变更（暗藏/横置/伤害等，CardManager.OnCardStateChanged）
            CardDied,       // 卡死亡（CardManager.OnCardDied）
            CardPlayed,     // 出牌（TurnManager.OnCardPlayed）
            CardDrawn,      // 抽牌（TurnManager.OnCardDrawn）
            PhaseChanged,   // 阶段切换（TurnManager.OnPhaseChanged）
            PriorityChanged,// 优先权变化（TurnManager.OnPriorityChanged）
            TurnStarted,    // 回合开始（TurnManager.OnTurnStarted）
            TurnEnded,      // 回合结束（TurnManager.OnTurnEnded）
            GameOver,       // 对局结束（PlayerManager.OnGameOver）
            AreaWon,        // 地区被赢取（AreaManager.OnAreaWon）
            AssetsChanged,  // 资产变化（TurnManager.OnAssetsChanged）
            StackChanged,   // 堆叠变化
        }

        public EventKind Kind;

        // ---- 通用：玩家编号 / 卡 UID ----
        public int PlayerId = -1;
        public string Uid;
        public string TargetUid;   // 出牌目标 / 移动目标

        // ---- CardMoved / CardStateChanged ----
        public int ToZone = -1;        // 目标区域（CardZone，int 承载避免 Net 依赖）
        public int FromZone = -1;      // 原区域（StateChanged 用）
        public int ZoneOwnerId = -1;   // 区域归属玩家
        public string ZoneDetailId;    // 地区/宿主 UID

        // ---- CardStateChanged ----
        public int ChangeKind = -1;    // CardChangeKind（Moved/Covert/Tapped 等）
        public bool IsCovert;
        public bool IsTapped;
        public int Damage;
        public int Trauma;

        // ---- PhaseChanged / TurnStarted ----
        public int Phase = -1;         // TurnPhase
        public int TurnCount;
        public int PriorityPlayerId = -1;

        // ---- CardPlayed / CardDrawn ----
        public string CardName;        // 展示用
    }

    /// <summary>
    /// 【C/S 化 P3·模块D】渲染事件 DTO 的 JSON 序列化器。
    /// 与 Snapshot/Choice/Intent 系列一致，DTO 用 public 字段，须 IncludeFields 才能往返不丢。
    /// </summary>
    public static class EventSerializer
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new System.Text.Json.JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
        };

        public static string Serialize(RenderEvent evt) => System.Text.Json.JsonSerializer.Serialize(evt, Options);
        public static RenderEvent Deserialize(string json) => System.Text.Json.JsonSerializer.Deserialize<RenderEvent>(json, Options);
    }
}
