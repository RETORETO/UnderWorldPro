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
        /// <summary>对抗步骤的当前地区序号（1-based；0＝非对抗步骤）。客户端没有权威地区游标
        /// （`TurnManager._combatAreaUids` / `AreaManager` 均为服务端构造），故由服务端算出随快照下行，
        /// 供联机回合标签显示"1地区调查步骤"。</summary>
        public int CombatAreaNo;
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

        /// <summary>
        /// 【P2·合法性投影】服务端**为该查看者预先算好**的操作合法性视图（可发动 / 合法目标集合）。
        ///
        /// 存在的意义（消灭"两套判定"）：客户端原本自己调 `CardManager.IsLegalPlayTarget` /
        /// `ActivateEligibility.CanActivate` 做提前判断，而那要求客户端把服务端全部输入都投影齐
        /// （本局 PlayerManager、静止标记、运行时授予…）。只要漏一项就会两端不一致。
        /// 改为服务端用**同一份规则函数**算好候选集合下发后，客户端只做"渲染 + 成员判断"，
        /// 规则实现只剩服务端一份；且候选范围由服务端决定，客户端不再有自己的枚举范围（曾经的窄范围缺口）。
        ///
        /// null 表示服务端未生成（旧服务端 / 纯测试路径）→ 客户端**退回调用同一份规则函数**本地判定
        /// （该路径在 P0/P1 之后输入已补齐，同样一致）。
        /// </summary>
        public GameViewSnap View;
    }

    /// <summary>【P2】一条能力级合法目标投影（激活类效果：来源卡 + EffectId → 目标卡 UID）。</summary>
    public class AbilityTargetSnap
    {
        public string SourceUid;
        public string EffectId;
        public List<string> TargetUids = new List<string>();
    }

    /// <summary>【P2】一条打出级合法目标投影（手牌 → 目标卡 UID）。</summary>
    public class PlayTargetSnap
    {
        public string SourceUid;
        public List<string> TargetUids = new List<string>();
    }

    /// <summary>
    /// 【P2·合法性投影】服务端算给某个查看者的"操作合法性"纯数据视图（已按查看者过滤）。
    /// 全部由服务端 Manager 层用生产规则函数算出，客户端只读不算。
    /// </summary>
    public class GameViewSnap
    {
        /// <summary>本视图对应的查看者座位。</summary>
        public int ViewerPlayerId;

        /// <summary>当前"可发动"的场上卡 UID（服务端 `ActivateEligibility.CanActivate` 的结论）。
        /// 客户端据此做可发动高亮与激活拖拽起手门禁，不再自行判定。</summary>
        public List<string> ActivatableUids = new List<string>();

        /// <summary>激活类效果各自的可选目标（来源卡 UID + EffectId → 目标卡 UID 集合）。
        /// 只包含"至少有一个合法目标"的条目。</summary>
        public List<AbilityTargetSnap> AbilityTargets = new List<AbilityTargetSnap>();

        /// <summary>手牌打出时的合法目标卡 UID 集合（含场上卡与秘社；地区目标仍由落点位置表达）。</summary>
        public List<PlayTargetSnap> PlayTargets = new List<PlayTargetSnap>();
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

        /// <summary>【P1】该玩家当前是否被"未横置的阿格斯IV"封锁秘密派遣（JC068-B 全局封锁）。
        /// 权威来源是服务端 `EffectEngine.IsDeployCovertBlocked`（持续效果注册表），客户端无该引擎，
        /// 故必须随快照下行；否则客户端派遣门禁（DeployCovertEligibility）会放行、直到服务端才拒绝。</summary>
        public bool DeployCovertBlocked;
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
        /// <summary>【P2】封印卡是否"背面朝上"封印（JC119）：为 true 时对非控制者做信息隔离（只画卡背）。
        /// 与 IsCovert 无关：暗藏影响规则，本字段只影响可见性。</summary>
        public bool IsSealedFaceDown;
        /// <summary>【P3】屏障（`CardInstance.HasGuard`）：不能被敌方卡牌或能力指定为目标。
        /// 客户端本地目标合法性判定（`TargetValidity.IsBarrierBlocking`）要读它，不投影会导致两端判定分叉。</summary>
        public bool HasGuard;
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

        // ---- 【P1·运行时状态投影补全】----
        // 背景：客户端不运行 EffectEngine，因此凡"由持续/静止效果在服务端写入实例"的状态，
        // 若不随快照下行，客户端上的同一份规则函数（IsLegalPlayTarget / AreaStatsCalculator / CardUI …）
        // 必然算出与服务端不同的结果。以下字段即为该补齐集。
        /// <summary>静止/禁止标记（`Script.Data.Effects.StaticKind` 的整数值，如 CannotBeAttached=0）。
        /// 用 int 承载以免 Net/ 直接依赖 Manager 层枚举类型（对齐 CurrentPhase 的既有约定）。
        /// 消费点：`CardManager.IsLegalPlayTarget` 的"不能被装备结附"拦截。</summary>
        public List<int> StaticFlags = new List<int>();
        /// <summary>当前实际子类别（模板固有 + 运行时授予，如 JC030 授予"吸血鬼"）。
        /// 消费点：`CardInstance.HasSubCategory` / `TargetRestriction.CurrentSubCategory` / 卡面子类别显示。</summary>
        public List<string> SubCategories = new List<string>();
        /// <summary>【P3·K4】被"改写掉"的子类别（JC036 初拥把宿主的人类类别改为吸血鬼）。
        /// 客户端 `RestoreProjectedState` 重建当前集合时按"（模板固有 ∪ SubCategories）− 本表"计算——
        /// **不能**只靠"模板固有即成立"的并集，否则客户端会多出服务端已失去的类别，导致两端判定分叉。
        /// 注：**印刷类别**（`HasStaticSubCategory`，读模型数据）不受本表影响。</summary>
        public List<string> RemovedSubCategories = new List<string>();
        /// <summary>【P3·K4】**实例级形态覆盖**的类别位（`CardCategory` 整数值；0＝未覆盖；JC027"成为一个地精角色"）。
        /// 消费点：客户端 `CardInstance.EffectiveCategoryFlag` / 本地目标合法性判定，保证与服务端同解。</summary>
        public int CategoryFlagOverride;
        /// <summary>【P3·K4】实例级形态覆盖的**卡名**（JC028「鲜血」；空＝未覆盖）。
        /// 消费点：`CardInstance.EffectiveCardName` / `TargetRestriction.CardName` 等卡名判定。</summary>
        public string CardNameOverride = "";
        /// <summary>【P3·K4】形态是否**失活了印刷效果**（JC028"损失了原有的效果"）。</summary>
        public bool PrintedEffectsSuppressed;
        /// <summary>运行时授予的关键字（如持续效果授予的【公开】）。消费点：`HasKeyword` / `HasPublicKeyword`。</summary>
        public List<string> GrantedKeywords = new List<string>();
        /// <summary>运行时授予的图标。消费点：`HasIcon` 与卡面图标显示。</summary>
        public List<string> GrantedIcons = new List<string>();
        /// <summary>运行时授予的属性计数（"先手调查"/"调查"/"战斗"等）。
        /// 消费点：`CardInstance.GetAttribute`（如 `AreaStatsCalculator` 的白眼先手调查加成）。</summary>
        public Dictionary<string, int> GrantedAttributes = new Dictionary<string, int>();
        /// <summary>先手调查/先手战斗当前值（模板固有 + 运行时授予，Grant 已折入）。
        /// 消费点：CardUI 白眼标签等读取 `CurrentFirstStrike*` 的展示。</summary>
        public int CurrentFirstStrikeInvestigation;
        public int CurrentFirstStrikeStrength;

        // ---- 规则深化字段（M3）：用于门禁/结算判定 ----
        /// <summary>本回合是否登场过（登场同回合不可攻击/不可额外行动）。</summary>
        public bool EnteredThisTurn;

        /// <summary>版本号：卡每次进入不同区域自增，用于"目标丢失"判定（D16 等价）。</summary>
        public int VersionId;

        /// <summary>本卡是否已发动过能力（每回合重置，简化）。</summary>
        public bool AbilityUsedThisTurn;

        /// <summary>
        /// 【K23】本回合"每能力"使用次数的投影（与 <see cref="UsedAbilityCounts"/> **同序一一对应**）：
        /// EffectId 列表。客户端 `ActivateEligibility.CanActivate` 需要它来判断"每回合一次/两次"是否已用满，
        /// 否则客户端会给出与服务端相反的"可发动"高亮。
        /// </summary>
        public List<string> UsedAbilityIds = new List<string>();
        /// <summary>与 <see cref="UsedAbilityIds"/> 同序的已使用次数（&gt;0 才会下发）。</summary>
        public List<int> UsedAbilityCounts = new List<int>();

        /// <summary>
        /// 【A57】本局"每能力"使用次数的投影（与 <see cref="UsedAbilityCountsGame"/> **同序一一对应**）：
        /// "该能力每局游戏只能发动一次"（秘社 MSJC001 行动4）——**不随回合重置**，
        /// 客户端 `ActivateEligibility.CanActivate` 要读同一份真值，否则"本局已用过却仍高亮可发动"。
        /// </summary>
        public List<string> UsedAbilityIdsGame = new List<string>();
        /// <summary>与 <see cref="UsedAbilityIdsGame"/> 同序的本局已使用次数（&gt;0 才会下发）。</summary>
        public List<int> UsedAbilityCountsGame = new List<int>();
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
