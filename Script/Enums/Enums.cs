using System;

namespace Script.Enums
{
    // 颜色：默认棕，共9种
    public enum CardColor
    {
        //棕，黄，白，绿，灰，紫，蓝，黑，红
        Brown, Yellow, White, Green, Gray, Purple, Blue, Black, Red
    }

    // 魔法领域：5种，可为空
    public enum MagicDomain
    {
        //心灵，神圣，星空，死亡，鲜血
        Mind, Divine, Star, Death, Blood
    }

    // 卡牌类别标志位（支持多身份）
    [Flags]
    public enum CardCategory
    {
        None = 0,

        // 基础类别（独立身份）
        Character = 1 << 0,    // 角色
        Sorcery = 1 << 1,    // 事务
        Attachment = 1 << 2,   // 附属
        Quest = 1 << 3,    // 任务（在手牌/卡组时为任务身份）
        Land = 1 << 4,    // 地区（在场时为地区身份）
        Token = 1 << 5,    // 指示物（基础指示物类型，可与其他组合）

        // 常用复合类别（便于使用）
        CharacterOrAttachment = Character | Attachment,  // 角色/附属
        QuestLand = Quest | Land,                        // 任务/地区
        TokenCharacter = Token | Character,              // 指示物角色
        TokenAttachment = Token | Attachment,            // 指示物附属
        // 未来可继续添加其他复合组合
    }

    // 卡牌区域（用于实例定位）
    public enum CardZone
    {
        Hand, Deck, Graveyard, CouncilZone, AssetZone, ScoreZone,
        AreaZone, Stack, AttachedZone, SealedZone, Exiled
    }

    /// <summary>
    /// 卡牌状态变更种类（事件总线 OnCardStateChanged 的 changeKind 参数）。
    /// 仅列出当前机制真正会触发的三类；封印/翻面归入区域移动(Moved)与暗藏变更(CovertChanged)，
    /// 不单列 SealedChanged/Flipped 事件（详见目标系统·丢失规则设计）。
    /// </summary>
    public enum CardChangeKind
    {
        /// <summary>区域移动（上场/进墓地/进手牌/换宿主/离场/进出封印区）；ZoneManager.FireMoved 发出。
        /// 覆盖丢失规则 ①②⑧⑨ 及 ④⑤ 的"离开原区域"。</summary>
        Moved,
        /// <summary>暗藏状态变化（IsCovert 布尔变化，含正面↔背面两方向）；CardInstance.SetCovert 发出。
        /// 覆盖丢失规则 ③。</summary>
        CovertChanged,
        /// <summary>屏障状态变化（HasGuard 布尔变化）；CardInstance.SetGuard 发出。
        /// 覆盖丢失规则 ④⑦。注：屏障机制当前未接入运行时赋值，此事件点预留，待屏障逻辑落地后自动生效。</summary>
        GuardChanged,
        /// <summary>横置/重置状态变化（IsTapped 布尔变化）；CardInstance.SetTapped 发出。
        /// JC068 阿格斯IV"未横置封锁"依赖此事件驱动 ReevaluatePrereqs 即时开关。</summary>
        TappedChanged,
    }

    /// <summary>
    /// 目标丢失原因（用于日志/提示区分"物理消失"还是"合法性丧失"，实现上均按丢失处理）。
    /// </summary>
    public enum TargetLossReason
    {
        None,
        /// <summary>目标实例已不存在（进墓地/移出游戏/离场）。</summary>
        Gone,
        /// <summary>目标离开合法区域（角色/暗藏者离场、附属离宿主、封印离封印区）。</summary>
        LeftZone,
        /// <summary>目标正面→背面（进入暗藏/covert），朝向变为丢失。对应 flip 动作的一个方向。</summary>
        Covert,
        /// <summary>目标背面→正面（解除暗藏/uncover），朝向变为丢失。对应 flip 动作的另一方向。
        /// 按规则③"翻面仍丢"，背面→正面同样使目标非法。</summary>
        Uncover,
        /// <summary>本地区限定下来源移动，目标不再"本地区"。</summary>
        OutOfLocalZone,
        /// <summary>目标获得屏障等使其非法（实现上等同丢失）。</summary>
        Illegal,
        /// <summary>目标控制者结算前变化（变为敌方/本方）。注（2026-08-11）：不再经方向性快照比对，
        /// 而由 D16 版本号方案统一覆盖——控制者变化触发 BumpVersion，结算比对 TargetVersionId 不一致即判丢失，
        /// 结果原因归为 ControllerChanged（具体方向由版本号变化本身隐含，效果仍可读取当前控制者判断敌友）。</summary>
        ControllerChanged,
        /// <summary>目标当前子类别结算前发生变化（获得/失去某 CurrentSubCategory）。注（2026-08-11）：同上，
        /// 由版本号方案统一覆盖（子类别变化触发 BumpVersion），结果原因归为 SubCategoryChanged。</summary>
        SubCategoryChanged,
    }

    /// <summary>
    /// 打出卡牌时可选择的目标种类（粗筛级别，未来由效果精确化）。
    /// 覆盖核心需求文档列举的所有对象：角色/附属/资产/秘社/地区/堆叠内卡/暗藏者/玩家/封印。
    /// </summary>
    public enum TargetKind
    {
        None,       // 无目标（多数事务）
        Character,  // 角色（场上正面角色）
        Attachment, // 附属
        Asset,      // 资产
        Council,    // 秘社（同 CouncilCardData / CouncilZone）
        Area,       // 地区（争夺区槽位）
        StackCard,  // 堆叠内的卡
        Covert,     // 暗藏者（场上背面角色）
        Player,     // 玩家
        Seal,       // 封印
        Deck,       // 牌库（搜牌等内部操作目标，不需玩家选择具体牌）
        Graveyard,  // 墓地内卡（规则⑧：以墓地内卡为目标，离墓地即丢失）
        Hand,       // 手牌内卡（规则⑨：以手牌卡为目标，离手牌即丢失）
    }

    /// <summary>打出时是否需要选择目标的覆盖标志（配合 PlayerCardData 使用）。</summary>
    public enum TargetRequirement
    {
        Default,    // 按卡类别默认推导
        Required,   // 强制需要目标
        None,       // 强制不需要目标
    }

}