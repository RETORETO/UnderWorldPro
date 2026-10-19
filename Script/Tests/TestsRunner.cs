using System.Collections.Generic;
using Godot;

namespace Script.Tests
{
    /// <summary>
    /// Godot 编辑器内一键运行测试：将该脚本挂到一个 Node 上，运行场景即可在输出面板看到结果。
    /// 按 P0→P5 优先级顺序执行各系统逻辑正确性测试。
    /// </summary>
    [GlobalClass]
    public partial class TestsRunner : Node
    {
        public override void _Ready()
        {
            var runners = new List<TestRunner>
            {
                new EffectSchemaTests(),        // P4
                new JCDataTests(),              // P5
                new TargetInterceptTests(),     // P0
                new EffectStackTests(),         // P1
                new ContinuousRegistryTests(),  // P2
                new ModStackTests(),            // P3
                new JC001Tests(),               // JC001 通灵猫头鹰（静止不可结附/白眼先手调查授予/多源叠加/Reset保留）
                new RevealCovertTests(),        // 暗藏者现身（门禁/费用/翻面/进栈/结算回地区）
                new TargetLossTests(),          // 目标丢失规则（D16 丢失校验链）
                new TargetLossBusTests(),        // 目标丢失事件总线（发点收敛 + 过程记忆字段）
                new JC002TriggerChainTests(),    // JC002 完整进场触发链路 · 目标=自身时本地区丢失(规则⑥)
                new JC002003LostTargetTests(),   // JC002/JC003 丢失目标矩阵（离场/翻面/本地区/无视地区豁免）
                new CouncilActivateWindowTests(), // 秘社激活窗口弹出条件矩阵（玩家/优先权/先后手/堆叠/阶段）
                new JC012Tests(),                 // JC012 帷幕守望徽记（打出目标/激活目标/一次性减费）
                new JC015Tests(),                 // JC015 驱魔人（目标合法性矩阵/夺控换边判丢失）
                new JC030Tests(),                 // JC030 巨型蝙蝠（静止/条件持续/袭击1本地区与换地区丢失）
                new JC035Tests(),                 // JC035 川美魔咒（打出目标地区/触发条件/附属区生效）
                new JC060Tests(),                 // JC060 神秘学顾问（光环授予/撤销/源卡排除）
                new JC014Tests(),                 // JC014 公路骑士（持续公开手牌区/机动触发结构与目标）
                new JC068Tests(),                 // JC068 阿格斯IV（强制准备触发/持续封锁前提/横置解除/未结附不生效）
                new JC020Tests(),                 // JC020 隐蔽的飞刀（打出结附正面角色/两效果本地区限定/牺牲清结附关系）
                new JC005Tests(),                 // JC005 裂解术（取目标阶段【心】判定/三种附属靶标/结算复检跳过/事务卡聚合）
                new NetCSTests(),                  // C/S 化 P0/P1 内核验证（异步输入链路 + 快照序列化往返）
                new NetP2Tests(),                  // C/S 化 P2 端到端验证（NetworkInputGateway + LoopbackTransport）
                new NetGodotTests(),               // C/S 化 P3·模块A 真实 ENet 联机回环验证
                new NetP3CTests(),                 // C/S 化 P3·模块C 意图指令通道（DTO序列化 + 服务端权威校验 + 回环闭环）
                new NetP3BTests(),                 // C/S 化 P3·模块B 客户端渲染切换（快照差异引擎 + 渲染驱动路由）
                new NetP3ETests(),                 // C/S 化 P3·模块E 断线重连（可复原PRNG + 随机源Hub + 重连管线）
                new NetP1Batch1Tests(),            // C/S 化·第一批迁移三方验证（Loopback端到端 / 单机回归 / Enet真实联机）
                new NetP1Batch1MatchTests(),        // C/S 化·第一批迁移端到端联机（NetworkMatchLauncher 回环 + 客户端投影一致）
                new NetCrossProcessSimTests(),      // 【临时】伪跨进程 + ResolveInstance 从模板重建验证（跨进程联机核心点，验证后移除）
                new OnlineEffectChainTests(),        // 联机效果系统接入：客户端可发动判定 / 费用修正快照 / 服务端目标校验
                new NetPhaseBTests(),                // Phase B：信息隔离（按玩家过滤快照 + 占位实例）/ 信封 v2 / 决策超时按 Kind
                new NetChoicePanelTests(),           // 第 3 版：触发排序顺序应用/强制忽略取消、PayOrTap PayAllowed、蒙版资格、决策窗口场景
                new TriggerWindowBatchTests(),        // 触发窗口改造：单列表场景契约 / 非批量视图回归 / 全部入堆叠批量预选与整体中止
                new LobbyProtocolTests(),            // 批次 1：大厅协议往返 / RoomState 按查看者裁剪（对手卡组不可见）/ 信封新类型 / 服务端配置
                new SessionRoutingTests(),           // 批次 1：会话生命周期 / SendToSession 定向投递 / 连接上限 / 真实 ENet 两会话不串
                new SessionGuardTests(),             // 批次 1：未 Hello 拒绝 / 版本不匹配断开 / 幂等（重复 RequestId）/ 脏数据 / 非对局通道拒绝
                new MatchIsolationTests(),           // 批次 2：一个进程两场对局互不串（世界/效果结算/MatchHost 路由与快照下行/停服清理）
                new LobbyFlowTests(),                // 增量 A：完整流程自检（真实 ENet 双客户端走完 建房→加入→卡组→准备→开局→结束→重开）+ 权限边界
                new ClientServerParityTests(),        // P0~P2：服务端权威 vs 真跨进程客户端投影的规则判定一致性（同解闸门）
                new P1EffectFoundationsTests(),       // P1：触发接线 / 新增目标限制 / 图标属性 / Gain负数 / 潜伏现身 Pay / 费用过滤串
                new P1NewCardsTests(),                // P1 新卡：JC006/010/029/039/042/058/071/075/088/090/091/102/103/112（结构 + 关键行为）
                new TriggerSceneSemanticsTests(),     // 触发语义：触发不看当前场景（死亡触发/进场即死）+ 本地区=触发时所处地区 + 离场护栏
                new P2DiscardTests(),                 // P2：弃牌原语（自选/随机/被指定者自选/施术者指定）+ OnDiscard 接线 + 弃牌非死亡
                new P2SealSearchShuffleTests(),        // P2：封印（方向矩阵/牌库顶/手牌/背面隔离/OnSealed）+ 检索（筛选/范围/去向/洗牌口径）+ 洗牌
                new P2SearchCardsTests(),              // P2 落地卡：JC007（检索≤2费法术/书籍+展示）/ JC025（牺牲检索猎手）/ JC130（抓2弃1）/ JC131（检索任意牌）
                new P2MoreCardsTests(),                // P2 落地卡（第3批）：JC026/053/066/076/085/099/100/108/109/119 的"前提已具备"效果行
                new P3KeywordsTests(),                 // P3 词条：隐匿 / 屏障 / 撤回 / 非资产 / XX标志X（PlaceCounter）/ 杀伤X
                new P4LeaderTests(),                   // P4 词条：领袖（同控制者场上至多 1 个；进场超限立刻选一个牺牲、不入堆叠；进场触发照常）
                new P4CurbTests(),                     // P4 原语：遏制X（RemovePowerMark / X≥总数自动全清 / 按地区分配且每地区上限=该地区标记数）
                new P4PredictionTests(),               // P4 原语：预测X（牌库顶 X 张任意数量置顶/底；复用调查对抗分配窗口与联机字段）
                new JC051Tests(),                      // P4：JC051 检索『结附』去向按被检索卡自身 TargetRules 选宿主；无合法宿主按"无合法目标"处理
                new P3PhaseGateTests(),                // P3·K26：阶段排除（ExcludedPhases／分配战斗伤害阶段；覆盖 JC008 灵能激发）
                new P3UseLimitTests(),                 // P3·K23：次数上限（每张卡每回合每能力；版本号变动重置；覆盖 JC032 血族长老）
                new P3TargetZoneTests(),               // P3·K14：墓地/手牌/资产目标（候选池/筛选/阵营/版本号丢失；覆盖 JC086 窃尸人）
                new P3MassTests(),                     // P3·K2：群体动作（AllInArea 集合 + 一次结算完整执行；覆盖 JC052 终焉的诗寇蒂 / JC050 丧钟回响）
                new P3DynamicValueTests(),             // P3·K1：动态数值 X（求值来源 / $X 替换 / X=0 不发动；覆盖 JC040 安普莎 / JC097 阿斯旺）
                new P3DrawBatchTests(),                // P3·OnDraw 按批粒度 + JC080 凯拉（一次抓牌＝一批，X＝该批张数；TriggerBySelf）
                new P3MultiTargetTests(),              // P3·K15：多目标槽位（至多 2 个目标/至少 1 个/禁止原地移动）+ JC021 狭路相逢
                new P3ConditionalBranchTests(),        // P3·K17：条件式动作分支（If/Else 二选一）+ JC004 力场法师
                new P3RemoveTokenTests(),              // P3·A：RemoveToken（地区势力标志移除，直接移除不走遏制分配）+ JC095 唐·格鲁尼
                new P3PrereqKindTests(),               // P3·B：持续前提扩展（所在地区无敌方角色 / 有本方·敌方势力标志）+ JC016 / JC084
                new P3PutToAreaTests(),                // P3·C：PutToArea（墓地角色牌置入地区）+ K15 双目标 + JC092 死灵召唤 / JC133 操控死尸
                new P3GrantToHostTests(),              // P3·D：持续授予落点＝结附宿主（GrantToHost）+ JC116 左轮 / JC073 捕梦网
                new P3AuraFilterTests(),               // P3·E：光环筛选扩展（组织/关键词/仅本地区/防御力授予）+ JC051 / JC059 / JC105
                new P3ModeBranchTests(),               // P3·K16：模式分支（打出付费前选定、入堆叠不可改）+ JC063 警车追捕
                new P3OptionalPayTests(),              // P3·K11：可选支付 + 数值输入框（支付N/不支付；挂起续算）+ JC061 / JC100
                new P3StrikeStackingTests(),           // P3·K13：袭击数值叠加 + 运行期授予的袭击参与触发收集 + JC038 列恩
                new P3ScaledGrantTests(),              // P3·F：按计数缩放的持续授予（ScaleExpr）+ JC037 / JC045 / JC109
                new P3ZoneConvertTests(),              // P3·G：MoveToZone 区域转换（牌库顶→资产区 / 自身→资产区）+ JC126 / JC120
                new P3InspectTests(),                  // P3·H：检视（私密查看，不改状态）+ JC062 调查档案（至多两个目标暗藏者）
                new P3PlayerTargetTests(),             // P3·K24：玩家目标（任意/本方/对手 + 独立窗口 + 与槽位组合）+ JC111 混沌蝴蝶
                new P3InvestigationWonTests(),         // P3：调查对抗获胜时机（OnInvestigationWon）+ JC072 博学的贤者
                new P3DeathBranchTests(),              // P3：门禁+分支双条件（BranchPredicate）+ JC031 唤灵师
                new P3TurnMemoryTests(),               // P3：回合内事件记忆位（势力标志被移除）+ 激活前门禁 + JC053 鸣钟教派徽记②
                new P3TwoPlayerDiscardTests(),         // P3：多玩家弃牌编排（双方各弃一张）+ JC041 禁婆②
                new P3PendingEffectTests(),            // P3：宿主种类前提 + 一次性待触发承诺 + JC082 索亚之书
                new P3StepPledgeTests(),               // P3：步骤级承诺（下个准备步骤免费现身）+ JC024 S.P.T董事局②
                new P3ControlTransferTests(),          // P3：控制权转移 + 到期归还 + JC129 操控心智
                new P3ActionOutcomeTests(),            // P3：动作成果判定（"若你如此做"）+ JC009 远东六号科研所 / JC087 格鲁尼的走私客
                new P3DestroyOnTargetTests(),          // P3·K2 扩展：消灭"目标卡上"的整组卡 + JC013 貘 / JC017 除咒师
                new P3SpiritKeywordTests(),            // 灵体前缀必须授予关键词：6 张卡的灵体防止伤害（JC022 实测 + 同类排查）
                new P5LayoutMathTests(),               // UI 几何：横置卡视觉中心＝Position+PivotOffset（附属/封印排版与卡位摆放基准）
                new BadgePeekTests(),                  // UI 契约：角标分家（封印/附属各自悬浮）+ 展开面板双击＝发动激活能力
                new OnlineViewMirrorTests(),           // UI 契约：联机镜像视图（本地座位 2）座位→槽位映射不得直取面板
                new UiPilePickTests(),                 // UI 契约：资产区折叠/资产+墓地悬停展卡/联机选卡窗
                new CardPickChannelTests(),            // UI 契约：cat-1/cat-2 分类（混合候选必须弹选择窗）+ 联机接线 + 快照跨局复位
                new DeckBoardLayoutTests(),            // 卡组编辑器：同名堆叠 / 50 张切分 / 换列 / 压缩 offset 布局算法
                new P3RuntimeKeywordTests(),           // P3·K13：运行期授予的触发类关键词参与收集 + 群体 Gain 主体 + JC024①/ JC089
                new P3CompositeTargetTests(),          // P3：复合目标限制（SpecAlt 两条声明 OR）+ HostIsCouncil + JC043 纵火狂
                new P3LatentTests(),                   // P3：新时机「被潜伏/翻面时」(OnLatent) + 群体伤害 + JC054 蒙古死亡蠕虫①
                new P3GraveyardPlayTests(),            // P3·K27：来源门禁 + 墓地行动（从墓地正面打出）+ JC085 复生行尸②
                new P3HandActivateTests(),             // P3·K27：手牌行动（Scope=HandZone 的激活）+ 来源生效区门禁 + JC110 闲人的绿地①
                new P3RenewableCostModTests(),         // P3：持续性玩家级费用修正（每回合可再生的一次性）+ JC026 洛特尼科夫①
                new P3RevealCostTests(),               // P3：付费通道隔离（打出/现身）+ JC033 家族传承①
                new P3PreventDamageTests(),            // P3·K3 第一批：防止伤害（伤害护盾）+ JC078 光能盾
                new P3MirrorSealedIconsTests(),        // P3：图标镜像（持续授予来源改写＝被封印角色牌的能力图标）+ JC066 封魔瓶瑟琳娜②
                new P3SkipAreaCombatTests(),           // P3·K18 第一批：地区级"略过对抗"（静态规则）+ JC098 陷入疯狂的人群②
                new P3CovertPowerExclusionTests(),     // P3·K21：地区级"敌方暗藏者不参与势力对抗" + JC023 范·弥尔顿
                new P3CounterTargetTests(),            // P3·K21：目标须带卡上标志（HasCounter）+ 移动到目标所在地区 + JC069 警犬②
                new P3WinAreaDestinationTests(),       // P3·K3 第一批：地区赢取时的去处替代 + JC079 圣婴②
                new P3NoUntapSealedTests(),            // P3·K29：重置步骤钩子 + "其上有封印的敌方角色不能被重置" + JC108 织梦人②
                new P3ExtraPowerContestTests(),        // P3·K18 第二批：地区级"额外进行一次势力对抗" + JC067 约翰·F·纳特里②
                new P3SacrificeCostTests(),            // P3·K22 第一批：牺牲一个角色作代价（发动者自选/不可暗藏者/触发死亡时机）+ JC094
                new P3SacrificeCost2Tests(),           // P3·K22 续：JC093 授予结附宿主 / JC096 可牺牲自身 / JC038 子类别筛选 + 玩家弃牌
                new P3TurnAreaCombatSkipTests(),       // P3·K18 第二批·本回合型：地区三步骤全略过 + JC118 停战协议
                new P3DamageRedirectTests(),           // P3·K3·持续型伤害重定向：JC077 庇佑圣灵 / JC121 空鸣御守
                new P3IconReplaceTests(),              // P3·K20 第一批·图标替换＝属性替换 + JC106 酒神秘仪
                new P3IconSuppressionTests(),          // P3·K20·持续型"失去图标" + JC123 饮魂者朱迪②
                new P3DomainSuppressionTests(),        // P3·K20·持续型"失去魔法领域图标" + JC099 徘徊的鬼魂③
                new P3TextBlankTests(),                // P3·K20 收尾：文本空白（失去效果）+ JC101 直视深渊
                new P3GrantedAbilityTests(),           // P3·K13·运行期授予激活能力 + JC019 弥尔顿的镇魔枪
                new P3ActivationAreaTargetTests(),     // P3·K15·激活能力的"目标地区"：JC117 红眼航班 / JC033 家族传承①
                new P3TriggerModeChoiceTests(),        // P3·K16·触发版「选择一项」（入堆叠时选定）：JC110 闲人的绿地②
                new P3DeathSealTests(),                // P3·K3·死亡类第一批（触发路线）：JC105 噩梦领主②
                new P3DrawFromBottomTests(),           // P3·非抓牌式取牌（不广播 OnDraw）：JC049 深渊细语②
                new P3RulingBatchTests(),              // 用户口径批：JC022 战斗获胜近似判定 / JC074 快速行动授予 / JC122 锁定者禁现身
                new JCCardTextAuditTests(),            // 【2026-09 整体复查】JC 包卡面自检（门禁/强制/关键字装载/时机/输入框；豁免表可审计）
                new JCEnginePathContractTests(),       // 【判据㉖–㉙】旁路同判 / 多声明 OR / 落地路径一致 / 交互收尾 / 联机孪生
                new JCBugfixBatchTests(),             // 【2026-10 实测 BUG 批】声望去重 / JC078 目标 / JC082 秘社结附 / JC083 触发 / 屏障拦袭击
                new JCBugfixBatch2Tests(),            // 【2026-10 实测反馈四批】未横置才有词条（威名/声望）/ JC085 墓地行动 / JC089 减防致死 / JC092 提示文字 / JC093 次数用尽
                new JCBugfixBatch3Tests(),            // 【2026-10 实测反馈五批】JC098 略过步骤＝流程里不存在 / JC100 发动时付费+减防致死刷 UI / JC107 资产目标 / JC108 可取自身
                new JCBugfixBatch4Tests(),            // 【2026-10 实测反馈六批】JC103 一次性减费消费 / JC104 预测窗文案 / JC105 光环覆盖运行期灵体 / JC106 灵体免伤 / JC108 双击激活 / JC110 手牌双击 / JC111 防御1
                new JCEffectSelfAuditTests(),          // 【2026-10 JC 效果代码自查】效果结构层面（㉑切分/③区域/④朝向/⑫类别/⑬白名单/⑮门禁/⑤目标/⑳结构/⑭代价/⑩注册；豁免表可审计）
                new JCContinuousGrantAuditTests(),     // 【2026-10 层系统前置审计】卡文(.tres)承诺的能力图标 ⇒ 脚本必须真的授予（查出 4 张漏授，[KnownDefect] 可见）
                new P3PathUniquenessTests(),           // P3·K25「道途」唯一性（与领袖同款：玩家选一个牺牲）：JC034①
                new P3TemporaryManaTests(),            // P3·K19 临时费用池（优先花费 + 步骤结束清除）：JC115
                new P3CounterCostTests(),              // P3·K5 计数器作发动代价（发动者选择）：JC044②
                new P3MarkerAreaTests(),               // 准备步骤放标志（JC055①）/ 地区标志数条件与清标志（JC127）
                new P3CombatWonAllyTests(),            // A14 近似：赢下战斗对抗 + 本方吸血鬼参与 → 目标对手弃牌：JC034③
                new P3HourglassTests(),                // 准备步骤放标志 + 按地区标志数群体伤害 + ≥3 牺牲自身：JC048
                new P3AttachSelfTests(),               // P3·K12 附属进场自选结附目标（Attach 执行器）：JC121①
                new P3MultiSacrificeTests(),           // K22 多玩家/对手侧自选牺牲（先手先选→一起牺牲）：JC046/113/134/065
                new P3AtomicDecisionLockTests(),       // 原子操作期间的玩家选择：决策持锁（单机窗口/引擎挂起/联机回包/打出/激活/恢复步骤）
                new P3FieldZoneGateTests(),            // 通用红线：效果只在场上（正面）提供（声望/撤回/灵体/屏障/关键字筛选/JC121不能移动/去处替代/激活G0）
                new PMCommandTests(),
                new P3EmbraceTests(),                  // P3·K4 持续夺控 + 子类别改写 + 「宿主翻面⇒附属被消灭」：JC036 初拥
                new P4ExtraCostTests(),                // P3·K10 额外费用（牺牲/支付X费）：JC049① / JC135① ②
                new P4ChooseNumberTests(),             // K1「玩家选择」型动态值 + JC083 睿智的斯芬克司
                new P4CounterCostXTEsts(),             // K5·JC055② 移除 X 个时间标志作代价 → 下一张灾难牌减费 X
                new P4HackerTests(),                   // JC114 查看目标玩家手牌 → 可选牺牲自身 → 由施术者指定弃牌
                new P4FormChangeTests(),               // P3·K4 实例级形态转换：JC027 成地精角色 / JC028 成「鲜血」附属
                new P4MartialLawTests(),               // P3·K21·JC064 戒严：非国家机构角色不可进入本地区 + 支付1否则牺牲
                new P4WaterRevealTests(),              // P3·K21·JC041 禁婆①：暗藏者专用激活 + 水域地区前提 + 现身
                new P4KeeperTests(),                   // JC119 守秘人②：展示封印牌 → 免费打出其中一张（视作快速、忠诚照常）
                new P4AttachWhitelistTests(),          // JC081 [前缀] 除装备与祝福外不受附属（子类别白名单静止规则）
                new P4PhilosopherStoneTests(),         // JC124 哲人石：构筑级（卡组级）全局触发（死亡 → 回场，由拥有者操控）
                new P4CovertInspectTests(),            // JC057 监控员②：持续"随时检视本地区任一暗藏者" + 悬浮预览可见性
                new P4CovertContinuousTests(),         // 暗藏者（背面朝上）的持续/静止效果失效（与触发/激活/光环对齐）
                new P4OnlineGuardTests(),              // 联机回包权威复检（检索/弃牌越权回包被拒）+ 候选显示名随请求下发
                new P4OnlineGatewayWiringTests(),      // A55：服务端"需要玩家选择"的效果全部经网关问玩家（检索/弃牌/封印/支付/数字/牺牲）+ 挂起-续算与原子锁
                new P4OnlineActivationTests(),         // 联机激活接管：目标地区（JC117）/ 牺牲代价 / 标志代价逐卡分配（1:1 单机窗）
                new P4RevealExtraCostTests(),          // K10 现身校验额外费用（JC081 展示减免 / 必须支付类 fail-closed）+ 联机接管
                new P4FlipTappedSealTests(),           // 【2026-10】翻面保留横置（RevealCard/Latent/GM flip/付费现身跨堆叠）+ 封印只在版本号变动时回手 + 翻面清附属封印
                new P5FlowOptimizationTests(),         // 流程优化：步骤中文名 / 免让过推进 / 优先权交换修复 / 秘社起手数 / 调度结算 / 卡组张数校验
                new P5CouncilRestrictionTests(),       // 秘社构筑限制：CouncilRestriction.Describe / ValidateDeckDetailed / MSJC001 需 ≥25 张黄色牌
                new CouncilEffectTests(),              // 秘社效果：MSJC001 按卡文落地（先手标志前提 / 独有检索 / 每局一次）＋【A57】每局上限 + 独有筛选
                new CouncilEffectBatch2Tests(),        // 秘社效果批次2：MSJC010~017 按卡文落地 + 牌作代价/卡上标志门槛/磨牌/抓牌方向/静态子类别授予/开局效果
                new CouncilAssignTests(),              // 秘社归属：双方各拿自己卡组的秘社 / 起手数各自取 / 无卡组方回退默认秘社 / 重开不串场 / 开局效果落最终实例
                new DeckInstanceBindingTests(),        // 开局发牌必须沿用 View 绑定的同一批卡实例（单人模式"看不到起始手牌/PM draw 无 UI"根因回归）
                new LocalHostTests(),                  // 增量 D：首页「建立主机」内嵌服务端（起/连自己/自动建房、端口顺延、Stop 同步释放端口）
                new DeckFilterTests(),                 // 卡池结构化检索：类别位掩码包含/属性≥/领域/忠诚AND且点数≥/文本与独有 + 真实索引一致性
            };

            var redlines = new List<string>();
            var allOk = true;
            foreach (var r in runners)
            {
                r.RunAll();
                GD.Print(r.Report());
                if (!r.AllPassed) allOk = false;
                // 【稳定性】每个套件之间主动做一次"完整 GC + 终结器排空"：
                // 测试会创建大量 RefCounted（PlayerCardData/EffectData/TargetSpec/EffectAction…），
                // 若交给 GC 在任意分配点触发终结，会命中 Godot 的 RefCounted 终结器竞态
                // （`gchandle.is_released()` → Fatal error 0xC000001D），表现为**偶发原生崩溃**（与业务逻辑无关）。
                // 在套件边界集中回收，可把终结时机钉在确定、串行的位置。
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
            }

            if (redlines.Count > 0)
            {
                GD.Print("=== 已知红线（待后续系统支持，本次不计入失败） ===");
                foreach (var rl in redlines) GD.Print(rl);
            }

            GD.Print(allOk ? ">>> 全部逻辑正确性测试通过" : ">>> 存在失败用例，详见上方明细");
        }
    }
}
