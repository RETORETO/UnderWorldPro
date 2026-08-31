using System.Collections.Generic;
using Script.Enums;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P0-A】整局状态快照（纯数据，不依赖 Godot / CardInstance）。
    ///
    /// 用途：服务端权威模式下，将 Manager 层的"唯一真相"抽取为可序列化结构，
    /// 经网络下发给客户端渲染（客户端不重算规则，只按快照重建/更新 UI）。
    /// 同时作为断线重连、历史回溯（D44）的状态恢复基础。
    ///
    /// 设计纪律：本文件及 Net/ 目录下的类型**不得** using Godot / Script.UI / Script.Game.Entities，
    /// 以保证可被 System.Text.Json 等纯托管序列化器处理（Headless 服务端无 Godot 运行时）。
    /// 注：TurnPhase 为全局命名空间枚举，此处用 int 承载以避免 Net/ 对 Manager 层类型的直接依赖；
    ///     序列化/反序列化时由调用方做 (int) / (TurnPhase) 显式转换。
    /// </summary>
    public class GameSnapshot
    {
        public int TurnCount;
        public int FirstPlayerId;
        public int PriorityPlayerId;
        public int CurrentPhase;   // 对应 TurnManager.TurnPhase（全局枚举），用 int 承载
        public List<PlayerSnapshot> Players = new List<PlayerSnapshot>();
        public List<ZoneCardRef> Zones = new List<ZoneCardRef>();
        public List<StackEntrySnap> Stack = new List<StackEntrySnap>();
        /// <summary>世界牌库（地区牌堆）剩余地区卡快照，自顶向下顺序（第三批：客户端投影重建地区牌堆）。
        /// 用 ZoneCardRef（含 TemplateUid）使客户端能跨进程从模板重建地区卡实例（仅 UID 在独立 CardManager 下无法解析）。</summary>
        public List<ZoneCardRef> WorldDeckCards = new List<ZoneCardRef>();
        /// <summary>各地区的势力标记（第三批：客户端投影重建势力标记）。</summary>
        public List<AreaMarkSnap> AreaMarks = new List<AreaMarkSnap>();
        /// <summary>已注册的地区卡快照（第三批·跨进程修复）：含地区卡模板与槽位，客户端从模板重建并注册，使 AreaManager 投影完整。</summary>
        public List<ZoneCardRef> AreaCards = new List<ZoneCardRef>();
        /// <summary>服务端当前"挂起等待某玩家输入"的请求（若有），用于断线重连恢复 pending 结算。</summary>
        public ChoiceRequest PendingInput;

        /// <summary>服务端当前随机源状态（MasterSeed + 各流步数），用于断线重连精确复原随机中间态（P3·模块E）。</summary>
        public RandomSnapshot Random;
    }

    public class PlayerSnapshot
    {
        public int PlayerId;
        public int AvailableMana;
        public int StorybookCount;   // 故事书/资源计数（按实际字段定）
        public bool IsGameOver;
    }

    /// <summary>一张卡在某一时刻的运行时状态投影（纯数据）。</summary>
    public class ZoneCardRef
    {
        public string Uid;
        public string TemplateUid;   // 指向 Card.xml / .tres 静态数据
        public int ControllerId;
        public int OwnerId;
        public CardZone Zone;
        public int ZoneOwnerId = -1; // 区域归属玩家（手牌/牌库/墓地/资产/分数/秘社按 playerId 分桶）
        public string ZoneDetailId;  // 所在地区 UID / 宿主 UID（争夺区/附属/封印用）
        public bool IsCovert;
        public bool IsSealed;
        public bool IsAttached;
        public bool IsSeal;          // 本卡是封印卡（镜像 IsAttached）
        public bool IsTapped;
        public int Damage;
        public int Trauma;
        public int CurrentInvestigation;
        public int CurrentStrength;
        public int CurrentPower;
        public int CurrentDefense;
        public List<MagicDomain> MagicDomains = new List<MagicDomain>();

        // ---- 规则深化字段（M3）：用于门禁/结算判定 ----
        /// <summary>本回合是否登场过（登场同回合不可攻击/不可额外行动）。</summary>
        public bool EnteredThisTurn;

        /// <summary>版本号：卡每次进入不同区域自增，用于"目标丢失"判定（D16 等价）。</summary>
        public int VersionId;

        /// <summary>本卡是否已发动过能力（每回合重置，简化）。</summary>
        public bool AbilityUsedThisTurn;
    }

    public class StackEntrySnap
    {
        public int OwnerId;
        public string EffectText;
        public string SourceUid;     // 来源卡 UID（若有）
    }

    /// <summary>某地区的势力标记快照（第三批：p1=玩家1标记数，p2=玩家2标记数）。</summary>
    public class AreaMarkSnap
    {
        public string AreaUid;
        public int P1;
        public int P2;
    }
}
