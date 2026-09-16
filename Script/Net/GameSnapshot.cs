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

        /// <summary>玩家级费用修正（JC012① 等"下一张费用-X"）：仅用于客户端展示减费提示，结算仍以服务端为准。</summary>
        public List<CostModSnap> CostMods = new List<CostModSnap>();

        /// <summary>
        /// 【调查对抗·客户端 UI 对齐】当前待分配的调查卡（仅下发给决策者本人，其他人收到空表）。
        /// 客户端据此复刻单机 InvestigationWindow（显示这些卡面并分配放回牌库顶/底）。
        /// </summary>
        public List<ZoneCardRef> InvestigationCards = new List<ZoneCardRef>();
        /// <summary>上述调查卡的决策者玩家编号（0=无待决）。</summary>
        public int InvestigationOwnerPlayerId;
    }

    /// <summary>一条玩家级费用修正的快照投影（纯数据，对应 EffectEngine.TimedMod 中 Kind==CostMod 的条目）。</summary>
    public class CostModSnap
    {
        public int PlayerId;
        public int Amount;         // 修正量（负数为增费）
        public string FilterText;  // 匹配过滤：空=全匹配；否则卡名含该串 或 Organization 等于该串
        public bool ConsumeOnce;   // 一次性（"下一张"）
        public int ExpireTurn;     // 到期回合
    }

    public class PlayerSnapshot
    {
        public int PlayerId;
        public int AvailableMana;
        public int StorybookCount;   // 故事书/资源计数（按实际字段定）
        public bool IsGameOver;
        public int ScoreTotal;       // 胜利分数（分数区总分，联机客户端展示/结算用）
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
        /// <summary>
        /// 【Phase B 信息隔离】是否为隐藏占位引用：对手手牌/牌库（以及地区牌堆）只下发"占位"，
        /// 客户端据此建立只承载数量的占位实例（`CardInstance.IsHidden`），只画卡背、不泄露卡牌身份。
        /// </summary>
        public bool IsHidden;
        public bool IsCovert;
        public bool IsSealed;
        public bool IsAttached;
        public bool IsSeal;          // 本卡是封印卡（镜像 IsAttached）
        /// <summary>附属卡结附的宿主 UID（IsAttached=true 且结附于卡时有效；结附地区则为空）。</summary>
        public string AttachedToUid;
        /// <summary>封印卡封印的宿主 UID（IsSeal=true 且封印于卡时有效；封印地区则为空）。</summary>
        public string SealedByUid;
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
        /// <summary>true=卡片进堆叠（显示卡面）；false=效果进堆叠（显示文字）。</summary>
        public bool IsCard;
        /// <summary>ResolveKind 枚举值（PlayToArea / Trigger / AreaWon ...），客户端投影重建显示用。</summary>
        public int Kind;

        // ---- 客户端堆叠 UI 对齐单机所需的补充字段（2026-09-05）----
        /// <summary>控制者玩家（堆叠箭头按控制者着色；夺控场景与 OwnerId 不同）。</summary>
        public int ControllerId;
        /// <summary>Kind=AreaWon 时被赢取地区的 UID（客户端据此显示地区卡面）。</summary>
        public string WonAreaUid;
        /// <summary>效果型条目的来源卡模板 UID（客户端据此显示来源卡卡图；空则不显示）。</summary>
        public string SourceTemplateUid;
        /// <summary>堆叠条目的目标引用（客户端据此绘制"条目 → 目标"箭头）。</summary>
        public List<StackTargetSnap> Targets = new List<StackTargetSnap>();
    }

    /// <summary>堆叠条目目标引用的纯数据投影（对应 StackTargetRef）。</summary>
    public class StackTargetSnap
    {
        /// <summary>StackTargetRef.RefKind：0=Card / 1=Area / 2=Player。</summary>
        public int Kind;
        public string CardUid;
        public int AreaSlotId;
        public string AreaUid;
        public int PlayerId;
    }

    /// <summary>某地区的势力标记快照（第三批：p1=玩家1标记数，p2=玩家2标记数）。</summary>
    public class AreaMarkSnap
    {
        public string AreaUid;
        public int P1;
        public int P2;
    }
}
