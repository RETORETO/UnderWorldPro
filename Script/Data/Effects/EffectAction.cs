using Godot;
using Godot.Collections;

namespace Script.Data.Effects
{
    /// <summary>动作原语（EffectAction.Verb）。未实现的执行器后续补全，枚举值保留即可。</summary>
    public enum ActionVerb
    {
        // 基础姿态
        Tap,            // 横置
        Untap,          // 重置
        Latent,         // 【P1】潜伏：将目标（缺省来源）翻面为暗藏者（SetCovert(true)）
        Uncover,        // 【P1】现身：令目标（缺省来源）由暗藏者状态翻面揭示（CardManager.RevealCard，含进场/现身触发）
        // 存亡/位移
        Destroy,        // 消灭
        Bounce,         // 移回拥有者手牌/牌库
        Move,           // 移动至另一地区（机动）
        Sacrifice,      // 牺牲（自毁换效果）
        // 属性/图标增减
        Gain,           // 获得属性/图标（+X防御、［黑刀］等）
        Lose,           // 失去属性/图标
        // 结附/封印/遏制
        Attach,         // 结附
        Seal,           // 【P2】封印（Args: [被封印者来源, 宿主, 附加]，见 ActionVerb 注释）
        Curb,           // 【P4】遏制X（Args: [X（缺省1）, 被遏制者（缺省"对手"/"自己"）]，见 ActionVerb 注释）
        // 搜牌/展示/洗牌
        Search,         // 【P2】从牌库检索（Args: [筛选, 去向, 附加, 范围]，见 ActionVerb 注释）
        Reveal,         // 展示
        Shuffle,        // 【P2】洗牌（Args: [对象: 自己/对手]）
        // 资源/费用
        Pay,            // 支付费用（名誉点等）
        Discard,        // 【P2】弃牌：手牌 → 墓地（Args: [张数, 弃牌者, 选择方式]，见 ActionVerb 注释）
        PrepareUpkeep,  // 准备步骤维护：支付N费用，否则横置来源（JC068 阿格斯IV二选一）
        // 伤害
        DealDamage,     // 造成伤害
        PreventDamage,  // 防止伤害
        // 标志/指示物
        PlaceToken,     // 放置标志/指示物（地区势力标记）
        RemoveToken,    // 移除标志/指示物
        PlaceCounter,   // 【P3】在卡上放置标志/计数器（Args: [标志名, 数量, 对象]，见 ActionVerb 注释）
        GenerateToken,  // 生成 token/附属
        // 对抗（特殊原语，接入 CombatManager/InvestigationManager）
        Investigate,    // 调查对抗
        Strength,       // 战斗对抗（对齐 CombatType.Strength）
        Power,          // 势力对抗
        // 胜负
        Win,            // 胜负判定
        // 资源/抽牌
        Draw,           // 抓牌（从牌库顶加入手牌）
        DrawFromBottom, // 取牌（从牌库底加入手牌）。Args：[数量] + 可选 `"不计抓牌"`（＝**不广播 OnDraw**，
                        // 供 FAQ 明写"并不算抓牌"的效果用，如 JC049 深渊细语"将你牌库底的两张牌置于手中"）
        Predict,        // 【P4】预测X：检视牌库顶 X 张，任意数量置顶/底（Args: [X]，见 ActionVerb 注释）
        PutToArea,      // 【P3·C】把**目标牌**（如你墓地中的角色牌）放进"本次结算的目标地区"（Args: [朝向, 附加子类别]，见下）
        PayOptional,    // 【K11】可选支付：弹**数值输入框**问"是否支付 N"（Args: [默认金额, 最小, 最大]，见下）
        MoveToZone,     // 【P3·G】把某张牌移到**指定区域**（Args: [对象, 目的区, 朝向]，见下）
        Inspect,        // 【P3·H】检视（**私密**查看）某张牌：Args: [对象(目标/自身)]，见下
        GainControl,    // 【P3】获得控制权（JC129"你获得目标敌方角色的控制权直到回合结束"）：Args: [对象, 期限]，见下
        DestroyOnTarget,// 【P3·K2 扩展】消灭"目标卡上"的整组卡（JC013/JC017）：Args: [集合(附属/封印牌), 归属(任意/本方/对手)]，见下
        SkipAreaCombat, // 【P3·K18】令**目标地区本回合**略过指定对抗（JC118 停战协议）：Args: [对抗名清单]，见下
        ReplaceIcon,    // 【P3·K20】把某卡上的指定**能力图标**换成另一种（**图标替换＝属性替换**，JC106）：Args: [来源图标清单, 目标图标, 对象]，见下
    }

    /// <summary>单个动作原语及其参数。
    /// 继承 RefCounted（而非 GodotObject）：效果数据由卡片 C# 脚本构建并挂入 EffectDataList，
    /// 不序列化进 .tres（故无需 Resource）；但 RefCounted 提供引用计数，避免 GodotObject
    /// 在 GC 时被误回收导致 EffectDataList 元素失效。无 [Export] 即不进 .tres。</summary>
    /// <remarks>
    /// 【P2】`Discard` 的 Args 约定：
    ///  · `Args[0]` = 张数（缺省 1）
    ///  · `Args[1]` = 弃牌者：`"自己"`（缺省，= source 控制者）/ `"对手"`（= 其对手）/ `"双方"`（各弃）
    ///  · `Args[2]` = 选择方式：`"自选"`（缺省，由**弃牌者自己**选）/ `"随机"` / `"由施术者指定"`（JC114：施术者从弃牌者手牌中选）
    ///
    /// 【P2】`Seal` 的 Args 约定（被封印者 → 宿主）：
    ///  · `Args[0]` = 被封印者来源：`"自身"`（缺省，= source）/ `"目标"`（= target，如 JC009 把目标角色封在自己身上）
    ///    / `"牌库顶"`（= 控制者牌库顶，如 JC099/JC108）/ `"手牌"`（由控制者从自己手牌中选一张，如 JC119）
    ///  · `Args[1]` = 宿主：`"目标"`（缺省，= target，如 JC100/JC110 把自身封在目标角色上）/ `"自身"`（= source）
    ///  · `Args[2]` = 附加：可含 `"背面朝上"`（JC119：对手不能检视该封印牌）
    ///
    /// 【P2】`Search` 的 Args 约定：
    ///  · `Args[0]` = 筛选（`;` 段之间为 **AND**，段内 `,` 为 **OR**）：`费用&lt;=2` / `费用&gt;=3` / `子类别:法术,书籍`
    ///    / `类别:角色` / `关键字:灵体` / `组织:王座会` / `卡名:xxx`
    ///  · `Args[1]` = 去向：`"手牌"`（缺省）/ `"进场"`（正面置于来源所在地区）/ `"暗藏进场"`（以暗藏者状态置于来源所在地区）
    ///    / `"结附"`（宿主**按被检索卡自身的 TargetRules 自动选定**，见下）/ `"封印"`（宿主取 `Args[2]`：`自身`/`目标`）
    ///  · `Args[2]` = 附加（去向参数，见上）
    ///  · `Args[3]` = 范围：`"牌库"`（缺省，整库）/ `"牌库顶:N"`（如 JC032 的 6 张）
    ///  语义：**检索后必定洗牌**（卡文"然后洗牌"），找不到也洗（用户确认）。
    ///
    ///  其中 `结附` 去向的宿主解析（**2026-09 用户拍板修订**，JC051『结附于合理的目标』口径）：
    ///   · `Args[2]="地区"` → 显式结附「来源所在地区」（原语义保留）；
    ///   · 本次结算**已有显式 target**（效果声明了目标且已选中）→ 结附该 target（原语义保留）；
    ///   · 否则（检索类效果的常态：无目标）→ **按被检索卡自身的 `TargetRules` 选宿主**
    ///     （如检索到 JC012『结附于本方秘社』→ 结附本方秘社）；判定与打出阶段**同一实现**
    ///     （`CardManager.MatchesRule` = 粗筛大类 + `TargetValidity.CheckRestrictions`），
    ///     候选确定性顺序＝秘社（P1→P2）→ 争夺区卡 → 附属卡；该卡只声明 `Kind=Area` 规则时结附来源所在地区；
    ///   · **无合法宿主 → 按"无合法目标"处理**：不结附、牌**放回拥有者牌库**（牌库不因此少牌），随后照常洗牌。
    ///
    /// 【P2】`Shuffle` 的 Args 约定：`Args[0]` = `"自己"`（缺省）/`"对手"`。
    ///
    /// 【P3】`PlaceCounter` 的 Args 约定（**在"卡上"放标志/计数器**，区别于 `PlaceToken` 的地区势力标记）：
    ///  · `Args[0]` = 标志名（如 `"时间标志"` / `"锁定标志"` / `"哲人石标志"`）
    ///  · `Args[1]` = 数量（缺省 1）
    ///  · `Args[2]` = 对象：`"自身"`（缺省，= source）/ `"目标"`（= target）
    ///  实现经 `CardManager.AddCounter` 收口（会广播 `OnMarkerPlaced`，供"当指示物被放置时"的触发使用）。
    ///
    /// 【K11】`PayOptional` 的 Args 约定（**可选支付**＝玩家自己决定付不付/付多少）：
    ///  · `Args[0]` = 默认/建议金额（缺省 1）；`Args[1]` = 允许最小值（缺省 0）；`Args[2]` = 允许最大值（缺省＝默认金额）；
    ///  语义（用户 2026-09 口径）：弹出**数值输入框**（输入 N）+ "支付"/"不支付" → 支付则扣 N 点无色费用并继续后续动作；
    ///  **不支付/N=0/费用不足 → 不扣费并置链式中止**（"若你如此做…"的后续动作整段跳过）。
    ///  与 `Pay` 的区别：`Pay` 是"能付则付"（不询问，旧行为保留）；本动词**一定要问玩家**。
    ///  实现：单机 `IGameUIHooks.PromptNumberInput`；联机 `ChoiceRequest.Kind.NumberInput`（回包 `SelectedAmounts[0]`）。
    ///
    /// 【P3·G】`MoveToZone` 的 Args 约定（**把某张牌移到指定区域**，用于"成为一个资产/移回手上"等去处转换）：
    ///  · `Args[0]` = 对象：`"自身"`（缺省，= source）/ `"目标"`（= target）/ `"牌库顶"`（来源控制者牌库顶一张）；
    ///  · `Args[1]` = 目的区：`"资产区"`（缺省）/ `"争夺区"`（落点＝`targetAreaId`，缺省来源所在地区）/ `"手牌"` / `"牌库底"`；
    ///  · `Args[2]` = 朝向：`"正面"`（缺省）/ `"暗藏"`（仅对争夺区有意义）。
    ///  支持卡：JC126 产业扩张（牌库顶 → 你的资产区，成为资产）、JC120 庄严圣像（赢取本地区时自身 → 资产区）。
    ///  注：进入资产区后即按区域被视作"资产"（`CardManager.MapInstanceToTargetKind` 按区域映射；
    ///  费用来源 `PlayerManager.GetAvailableMana` 亦按"资产区未横置卡数"计）。
    ///
    /// 【P3·H】`Inspect` 的 Args 约定（**检视**＝私密查看某张牌，不改变任何状态）：
    ///  · `Args[0]` = 对象：`"目标"`（缺省，= target）/ `"自身"`（= source）；
    ///  · 检视者＝效果来源的控制者；**只有他本人可见**（区别于 `Reveal` 的公开展示）。
    ///  单机：`UiNotificationBus.NotifyCardInspected`（View 私下呈现）；联机：`ChoiceRequest.Kind.InspectCards`
    ///  （把该卡的面下发**仅给检视者**，客户端确认后回包）。
    ///  支持卡：JC062 调查档案（检视至多两个目标暗藏者）。
    ///
    /// 【P4】`Curb` 的 Args 约定（**遏制X**＝被遏制者移除他所拥有的 X 个势力标志）：    ///  · `Args[0]` = X（缺省 1）
    ///  · `Args[1]` = 被遏制者：`"对手"`（缺省，= source 控制者的对手）/ `"自己"`
    ///  语义（用户规则）：**X ≥ 该玩家标记总数 → 自动全清（不弹窗）**；
    ///  **X &lt; 总数 → 被遏制者按地区分配**，**每地区上限＝该地区该玩家的标记数**。
    ///  实现见 `CurbRule`（单机 `PromptCurbMarks` / 联机 `ChoiceRequest.Kind.CurbMarks` / 无 UI 确定性兜底），
    ///  底层减法唯一出口为 `AreaManager.RemovePowerMark`。
    ///
    /// 【P4】`Predict` 的 Args 约定（**预测X**）：
    ///  · `Args[0]` = X（缺省 1，即"检视牌库顶 X 张"）
    ///  语义：检视者＝source 的控制者；把任意数量的牌以任意顺序置于**牌库顶**，其余以任意顺序置于**牌库底**。
    ///  实现见 `PredictionRule`：复用调查对抗的取/放通道（`ZoneManager.TakeTopForInvestigation` /
    ///  `ReturnAfterInvestigation`）与分配窗口（`InvestigationWindow`），联机复用 `Kind.Prediction` +
    ///  `OptionUids` / `OptionCardRefs` 字段（回包 `SelectedUids` = 置顶顺序）。
    /// </remarks>
    [GlobalClass]
    public partial class EffectAction : RefCounted
    {
        /// <summary>动作类型。</summary>
        [Export] public ActionVerb Verb { get; set; }

        /// <summary>动作参数列表（数值/图标/属性类型/区域等，由具体 Verb 解释）。</summary>
        [Export] public Array<string> Args { get; set; } = new Array<string>();
    }
}
