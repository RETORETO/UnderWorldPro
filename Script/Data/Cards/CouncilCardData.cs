using Godot;
using Godot.Collections;
using System.Collections.Generic;
using System.Linq;
using Script.Enums;

namespace Script.Data.Cards
{
    /// <summary>
    /// 秘社卡牌数据（CouncilCardData）
    /// 代表一种特殊的“秘社”卡牌，用于定义套牌构筑限制、起始手牌数等。
    /// 继承自基础卡牌数据（BaseCardData）。
    /// </summary>
        [GlobalClass]
        public partial class CouncilCardData : BaseCardData
        {
            /// <summary>起始手牌数量（StartingHandSize），默认6张；玩家携带本秘社时开局抓牌数覆盖默认6张</summary>
            [Export] public int StartingHandSize { get; set; } = 6;

            /// <summary>套牌构筑限制列表（Restrictions）—— 每个限制定义了套牌必须满足的条件（构筑限制暂缓，方法保留）</summary>
            [Export] public Array<CouncilRestriction> Restrictions { get; set; } = new Array<CouncilRestriction>();

            /// <summary>效果列表（Effects）—— 秘社卡牌自身产生的效果ID或描述（牌面文本备用）</summary>
            [Export] public Array<string> Effects { get; set; } = new Array<string>();

            // ==================== 展示元数据（参考 AreaCardData / PlayerCardData，从卡牌 XML 解析填入） ====================

            /// <summary>牌面完整文本（CardText）—— 从 XML &lt;text&gt; 解析，UI 展示用。效果/限制的结构化实现后续制作。</summary>
            [Export] public string CardText { get; set; } = "";

            /// <summary>卡图路径（CardImagePath）—— 参考 AreaCardData，用字符串路径，加载时再 Load（不序列化为 ExtResource）</summary>
            [Export] public string CardImagePath { get; set; } = "";

            /// <summary>主类别（MainType），如"秘社"</summary>
            [Export] public string MainType { get; set; } = "秘社";

            /// <summary>子类别（SubType），如"秘社-法师结社"</summary>
            [Export] public string SubType { get; set; } = "";

            /// <summary>组织/阵营（Organization），如"帷幕守望"。务必与玩家卡 Organization 取值口径一致（取 XML &lt;colors&gt; 中"·"后的阵营名）</summary>
            [Export] public string Organization { get; set; } = "";

            /// <summary>颜色（Colors）—— 与玩家卡同名同类型；卡牌自身颜色标签，影响操控/忠诚门槛等判定。
            /// 取值来自 XML &lt;colors&gt; 中"·"之前的色名（黄/白/绿/灰/紫/蓝/黑/红/中立）。</summary>
            [Export] public Array<CardColor> Colors { get; set; } = new Array<CardColor> { CardColor.Brown };

            /// <summary>颜色身份（ColorIdentity），如"无"</summary>
            [Export] public string ColorIdentity { get; set; } = "无";

            /// <summary>版面（Layout），如"normal"</summary>
            [Export] public string Layout { get; set; } = "normal";

            /// <summary>正反面（Side），如"front"</summary>
            [Export] public string Side { get; set; } = "front";

            /// <summary>战力/防御展示值（PT），如"6"</summary>
            [Export] public string PT { get; set; } = "";

            /// <summary>关联卡牌唯一标识（RelatedCardUID），如"TK007 先手标志"</summary>
            [Export] public string RelatedCardUID { get; set; } = "";

            /// <summary>牌桌行（TableRow），如"2"</summary>
            [Export] public int TableRow { get; set; } = 0;

        // ==================== 激活效果框架（参考 PlayerCardData 行动效果） ====================
        // 秘社效果为"非快速行动效果"，需门槛 + 付费用 + 进堆叠 + TargetRules 选目标。
        // EffectDataList / ActivationOptions.EffectDataList 由秘社卡 .cs 在构造函数中填充（RefCounted 不序列化进 .tres）。

        /// <summary>通用激活费用（Cost），-1 表示无通用激活；若 ActivationOptions 非空则以各选项 Cost 为准</summary>
        [Export] public int Cost { get; set; } = -1;

        /// <summary>声明式目标规则（TargetRules）—— 秘社默认激活（无 ActivationOptions 时）的选目标依据</summary>
        [Export]
        public Godot.Collections.Array<Script.Data.Effects.TargetRule> TargetRules { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.TargetRule>();

        /// <summary>结构化效果列表（EffectDataList）—— 秘社默认激活效果，由 .cs 构造时填充，不序列化进 .tres</summary>
        public Godot.Collections.Array<Script.Data.Effects.EffectData> EffectDataList { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.EffectData>();

        /// <summary>门槛：颜色忠诚度需求（如"棕色忠诚≥2"）。发动激活前需满足，不满足则不可发动</summary>
        [Export] public Array<ColorLoyaltyRequirement> ColorLoyaltyRequirements { get; set; }
            = new Array<ColorLoyaltyRequirement>();

        /// <summary>门槛：领域忠诚度需求（如"心灵领域忠诚≥1"）。发动激活前需满足，不满足则不可发动</summary>
        [Export] public Array<DomainLoyaltyRequirement> DomainLoyaltyRequirements { get; set; }
            = new Array<DomainLoyaltyRequirement>();

        /// <summary>多激活选项（二选一）。非空时玩家双击秘社弹出窗口选择其一发动（或取消）。
        /// 为空时走默认 EffectDataList + Cost + TargetRules 的单一激活。</summary>
        [Export] public Array<CouncilActivationOption> ActivationOptions { get; set; }
            = new Array<CouncilActivationOption>();

        /// <summary>
        /// 【秘社批次】**持续性 / 触发类效果列表**（非"激活选项"）。
        /// 秘社的"持续：…"与"触发：…"文本（如 MSJC012「费用≤2 的本方人类获得奴仆类别」、
        /// MSJC016「第一个回合的准备步骤开始时磨三张」、MSJC017「从牌库底抓牌」）不是可发动能力，
        /// 故与 `ActivationOptions` 分开承载：由秘社卡 `.cs` 构造函数填充，不序列化进 `.tres`。
        /// 消费点：
        ///  · `EffectData.CouncilStatic`（`CouncilStaticGrant`）→ `EffectEngine.ReevaluateCouncilStaticGrants`
        ///    （装配层 `ZoneManager.OnPostMove` 钩子驱动，与光环同款"随进出场重算"）；
        ///  · `Timing = OnTurnStarted` 等触发时机的条目 → `TriggerRegistry` 收集（与玩家卡触发同源）。
        /// </summary>
        public Godot.Collections.Array<Script.Data.Effects.EffectData> StaticEffectList { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.EffectData>();

        /// <summary>
        /// 【秘社批次】**抓牌方向**：`true` ＝ 从**牌库底**抓牌而非牌库顶（MSJC017 梦魇族群「持续：你从牌库底抓牌，而非牌库顶」）。
        /// 消费点：`TurnManager.DrawCardsForPlayer`（抓牌唯一收口）——该玩家所有"抓牌"行为统一改向；
        /// `DrawFromBottomForPlayer` 自身不受影响（它本来就是从底取）。
        /// </summary>
        [Export] public bool DrawFromBottom { get; set; }

        /// <summary>
        /// 【秘社批次】**开局效果**（"持续：在每局游戏开始时…"，MSJC015「在本方秘社上放置一个【隐秘】标志」）。
        /// 由 `GameInitializer.StartMatch` 在秘社就位后、发牌前调用 `ApplyCouncilOpeningEffects` 执行一次。
        /// 每局只执行一次（重开一局会重新走 `StartMatch`）。
        /// </summary>
        public Godot.Collections.Array<Script.Data.Effects.EffectData> OpeningEffectList { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.EffectData>();

        /// <summary>
        /// 【秘社批次】**首个准备步骤触发**（卡文"触发：**在第一个回合的准备步骤开始时**…"，MSJC016 颂亡者）。
        /// 由 `TurnManager.DoPrepare` 在 `TurnCount == 1` 时执行一次（**在起手发完之后**，见该处注释）。
        /// </summary>
        public Godot.Collections.Array<Script.Data.Effects.EffectData> TurnStartEffectList { get; set; }
            = new Godot.Collections.Array<Script.Data.Effects.EffectData>();


        /// <summary>防御性兜底：确保 EffectDataList 始终非空，避免运行时 NRE</summary>
        public CouncilCardData()
        {
            EffectDataList ??= new Godot.Collections.Array<Script.Data.Effects.EffectData>();
        }

        /// <summary>
        /// 验证给定的套牌是否满足所有限制条件（构筑限制暂缓，方法保留）
        /// </summary>
        public bool ValidateDeck(List<PlayerCardData> deck)
        {
            foreach (var restriction in Restrictions)
                if (!restriction.IsSatisfiedBy(deck))
                    return false;
            return true;
        }

        /// <summary>
        /// 【构筑校验·带明细】逐条检查构筑限制，返回**未满足**限制的可读文案列表（全部满足则空列表）。
        /// 供房间"卡组违规"提示透出**具体是哪条限制不满足**（每个限制经 `CouncilRestriction.Describe` 生成文案）。
        /// </summary>
        public List<string> ValidateDeckDetailed(List<PlayerCardData> deck)
        {
            var failures = new List<string>();
            foreach (var restriction in Restrictions)
            {
                if (restriction == null) continue;
                if (restriction.IsSatisfiedBy(deck)) continue;
                failures.Add(restriction.Describe(restriction.GetActualCount(deck)));
            }
            return failures;
        }

        /// <summary>本秘社是否声明了任何构筑限制（无限制则套牌"无限制"）。</summary>
        public bool HasRestrictions => Restrictions != null && Restrictions.Count > 0;
    }
}