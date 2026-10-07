using System.Collections.Generic;
using Script.Enums;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 P3·模块B】客户端渲染指令（纯数据，不依赖 Godot）。
    ///
    /// 模块 B 的目标是"客户端消费 GameSnapshot 而非本地 Model"。快照是全量状态，但客户端
    /// 不应每次全量重建 UI（昂贵且打断交互），而应"按前后快照的差异最小化刷新"。
    /// 本文件定义 RenderCommand——由 SnapshotDiff 对比两个 GameSnapshot 产出、供渲染层消费
    /// 的增量指令。它是"服务端权威下发快照 → 客户端增量刷新 UI"的契约层。
    ///
    /// 纪律：本文件及 Net/ 目录下的类型不得 using Godot / Script.UI，保证纯托管（可序列化、可单测、
    /// Headless 服务端 / 未来独立网络客户端进程均可复用本差异算法，不依赖具体 UI 框架）。
    /// </summary>
    public abstract class RenderCommand
    {
        /// <summary>指令类型（供渲染层按类型分发）。</summary>
        public enum CmdKind
        {
            AddCard,       // 快照新增一张卡
            RemoveCard,    // 快照不再含某 UID 的卡
            MoveCard,      // 卡从一区域移到另一区域
            UpdateCard,    // 卡在同区域但属性变化（Damage/Tap/Covert/防御等）
            StackPush,     // 堆叠新增一条
            StackPop,      // 堆叠弹出/清空
            PlayerUpdate,  // 玩家级状态变化（法力/故事书/胜负）
            PhaseChange,   // 回合/优先权/阶段变化
        }
        public CmdKind Kind;
    }

    /// <summary>单张卡的渲染指令负载（新增/移除/移动/属性更新通用）。</summary>
    public class CardRenderCommand : RenderCommand
    {
        public string Uid;
        public CardZone Zone;        // 目标区域
        public int ZoneOwnerId;      // 区域归属玩家（手牌/牌库/墓地/资产/分数/秘社按 playerId；争夺区等用 -1）
        public string ZoneDetailId;  // 所在地区 UID / 宿主 UID（争夺区/附属/封印用）
        public int ControllerId;
        // 属性（UpdateCard 时有效）
        public bool IsCovert;
        public bool IsTapped;
        public bool IsAttached;
        public bool IsSealed;
        public int Damage;
        public int Trauma;
        public int CurrentInvestigation;
        public int CurrentStrength;
        public int CurrentPower;
        /// <summary>【2026-09·JC051】卡面显示势力：服务端按与 AreaStatsCalculator 同一算式算好的标量
        /// （CurrentPower ＋（先手 ? 授予的先手势力 : 0））。联机客户端卡面的增量更新走本指令，故此值必须随指令下发；
        /// 否则角色卡的势永远不含先手势力（地区总显示读的是地区合计，故一直是对的——用户实测的差异根源）。</summary>
        
        public int CurrentDefense;
        public List<MagicDomain> MagicDomains;
    }

    /// <summary>堆叠指令负载。</summary>
    public class StackRenderCommand : RenderCommand
    {
        public int OwnerId;
        public string SourceUid;
        public string EffectText;
    }

    /// <summary>玩家/阶段指令负载。</summary>
    public class PlayerRenderCommand : RenderCommand
    {
        public int PlayerId;
        public int AvailableMana;
        public int StorybookCount;
        public bool IsGameOver;
    }

    /// <summary>回合/优先权/阶段指令负载。</summary>
    public class PhaseRenderCommand : RenderCommand
    {
        public int TurnCount;
        public int FirstPlayerId;
        public int PriorityPlayerId;
        public int CurrentPhase;
    }
}
