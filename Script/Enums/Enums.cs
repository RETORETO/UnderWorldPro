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
    /// 目标丢失处置模式（目标系统·丢失规则，2026-08-07）。
    /// 用于区分"单目标丢失即反击"与"未来多目标可部分丢失"。
    /// 注意（2026-08-11）：当前结算处置实际由 TurnManager.ResolveEntryTargetIsLost 按 ResolveKind 分派，
    /// 而非读取本字段——等价语义为：
    ///   - 事务卡/附属（卡）打出丢失目标 → 取消打出、进墓地（EventListenerToGraveyard / Attachment 分支）；
    ///   - 反应触发/激活（效果）丢失目标 → 仅跳过结算、卡留场（Trigger 分支）。
    /// 该分派结果恰好等价于 LossMode=Counter 的本意（卡/效果丢失目标即不结算），故本字段目前为声明性标注，
    /// Optional 多目标模式预留未启用（单目标卡/效果保持默认 Counter 即原行为）。
    /// 若未来需按本字段精确分派（而非 ResolveKind），可在 ResolveEntryTargetIsLost 读取 entry.LossMode。
    /// </summary>
    public enum TargetLossMode
    {
        /// <summary>丢失即反击/整卡不结算（默认；单目标卡/效果的标准行为）。</summary>
        Counter,
        /// <summary>可选：丢失的目标跳过，其余照常结算（预留给未来多目标；单目标不会用到）。</summary>
        Optional,
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
        COVERT,
        /// <summary>目标背面→正面（解除暗藏/uncover），朝向变为丢失。对应 flip 动作的另一方向。
        /// 按规则③"翻面仍丢"，背面→正面同样使目标非法。</summary>
        UNCOVER,
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
        Council,    // 议会（同 CouncilCardData / CouncilZone）
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

    /// <summary>
    /// 【已废弃】旧触发系统的时机枚举（TriggerEffectData / ICardTriggerBehavior / TriggerEffectCollector 已删除）。
    /// 新系统统一使用 Script.Data.Effects.EffectTiming（EffectData.Timing）。
    /// 保留仅为兼容 Godot 元数据/历史序列化，任何新代码不应再引用本枚举。
    /// </summary>
    [Obsolete("旧系统枚举，使用 EffectTiming 替代")]
    public enum TriggerTiming
    {
        OnCardPlayed,      // 打出卡牌后
        OnCardResolved,    // 结算卡牌后
        OnEffectUsed,      // 使用效果后
        OnEffectResolved,  // 结算效果后
        OnInvestigation,   // 调查对抗后
        OnCombat,          // 战斗对抗后
        OnPower,           // 势力对抗后
        OnAreaWonStep2,    // 赢取地区流程步骤2后
        OnAreaWonStep3,    // 赢取地区流程步骤3后
        OnAreaWonStep4,    // 赢取地区流程步骤4后
        OnAreaWonStep5,    // 赢取地区流程步骤5后
        PreCombat,         // 对抗前（机动触发等）
    }

    /// <summary>【已废弃】旧系统触发目标类型枚举，新系统用 TargetKind（EffectData.Target.Kind）。保留仅为兼容元数据。</summary>
    [Obsolete("旧系统枚举，使用 TargetKind 替代")]
    public enum TriggerTargetType
    {
        None,           // 不需要目标
        Self,           // 触发来源自己
        AnyCard,        // 任意卡
        Character,      // 场上角色
        Attachment,     // 场上附属
        Asset,          // 资产
        Council,        // 秘社卡
        Area,           // 地区卡（作为场上卡对象的地区牌）
        AreaSlot,       // 地区槽位（争夺区位置，机动效果选择移动的目标地）
        Covert,         // 暗藏者
        AttachedHost,   // 所结附的宿主（角色/地区）
        Player,         // 玩家
    }

    /// <summary>【已废弃】旧系统触发条件分类枚举，新系统用 EffectCondition 类（EffectData.Condition）。保留仅为兼容元数据。</summary>
    [Obsolete("旧系统枚举，使用 EffectCondition 替代")]
    public enum TriggerCondition
    {
        Always,              // 无条件
        WhenEntersPlay,      // 当来源入战场
        WhenLeavesPlay,      // 当来源离场
        WhenTargetLeaves,    // 当某目标离场
        WhenAreaWon,         // 当地区被赢取
        WhenCombatResolved,  // 当对抗结算
        WhenEffectResolved,  // 当效果结算
        Custom,              // 自定义条件（由 ConditionParam 解释）
    }

}