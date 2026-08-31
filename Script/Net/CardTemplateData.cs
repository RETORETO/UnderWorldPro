using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【C/S 化 · M2 卡牌数据 JSON 协议】纯托管卡牌模板数据（POCO）。
    ///
    /// 背景：物理进程分离要求服务端是脱离 Godot 的纯托管进程，但卡牌静态数据在 `Script.Data.Cards`
    /// 中深度耦合 Godot 资源（`[Export]`/`: Resource`/`Godot.Collections.Array<>` + 728 个 `.tres`）。
    /// 本 POCO 定义一份**跨端 JSON 卡牌模板协议**：Godot 侧用 `.tres→JSON` 转换器把 `PlayerCardData`
    /// 序列化为本结构；纯托管 Server / 未来浏览器客户端读同一份 JSON，经 `TemplateUid` 反查。
    ///
    /// 本类纯数据、纯托管（不引用 Godot / Script.Data），可被 System.Text.Json 序列化。
    /// 字段名对齐现有 PlayerCardData 的核心静态属性（UID 为模板唯一键）。
    /// </summary>
    /// <summary>卡牌类型（Player=玩家卡 / Area=地区卡 / Council=秘社卡）。</summary>
    public enum TemplateKind
    {
        Player,
        Area,
        Council,
    }

    public class CardTemplateData
    {
        // ---- 类型标识 ----
        public TemplateKind Kind = TemplateKind.Player;

        // ---- BaseCardData ----
        public string UID;
        public string CardName;
        public int Score;

        // ---- PlayerCardData 核心静态字段 ----
        public string SubCardName;
        public string UniqueField;
        public int Cost = -1;
        public string CategoryFlag;              // 卡类别（字符串承载，如 "Character/Attachment/..."）

        public List<string> SubCategories = new List<string>();
        public List<string> Colors = new List<string>();        // CardColor 名
        public List<string> MagicDomains = new List<string>();  // MagicDomain 名
        public string Organization;

        public int Investigation;
        public int Strength;
        public int Power;
        public int FirstStrikeInvestigation;
        public int FirstStrikeStrength;
        public int FirstStrikePower;
        public int Defense = -1;

        public string ExtraCostDescription;
        public bool IsExtraCostMandatory;
        public List<string> Effects = new List<string>();

        // 忠诚度需求（名称 + 点数）
        public List<LoyaltyRequirementData> ColorLoyaltyRequirements = new List<LoyaltyRequirementData>();
        public List<LoyaltyRequirementData> DomainLoyaltyRequirements = new List<LoyaltyRequirementData>();

        // 目标规则（粗筛大类 + 限制 + 参数）
        public List<TargetRuleData> TargetRules = new List<TargetRuleData>();

        // ---- 结构化效果/能力（M3 深化：供 ServerRuleEngine 解析执行） ----
        public List<ServerEffect> EffectDataList = new List<ServerEffect>();

        // ---- Area（地区卡）----
        public int RequiredPower;
        public string WinTriggerEffect;
        public string CardImagePath;

        // ---- Council（秘社卡）----
        public int StartingHandSize = 6;
        public List<string> CouncilEffects = new List<string>();
        public string CardText;
        public string MainType = "秘社";
        public string SubType;
        public string ColorText;
        public string ColorIdentity = "无";
        public string Layout = "normal";
        public string Side = "front";
        public string PT;
        public string RelatedCardUID;
        public int TableRow;
    }

    /// <summary>忠诚度需求（纯数据）。</summary>
    public class LoyaltyRequirementData
    {
        public string Name;   // Color 名 / Domain 名
        public int Amount;
    }

    /// <summary>目标规则（纯数据，对应 Script.Data.Effects.TargetRule）。</summary>
    public class TargetRuleData
    {
        public string Kind;              // TargetKind 名
        public List<string> Restrictions = new List<string>(); // TargetRestriction 名
        public string Param;
    }

    /// <summary>
    /// 结构化效果/能力（纯托管，对应 Script.Data.Effects.EffectData 的核心调度字段）。
    /// 供 ServerRuleEngine 解析：能力发动找本效果、按 ActivateWindow/ActivateCondition 校验、执行 Actions。
    /// </summary>
    public class ServerEffect
    {
        public string EffectId;
        public string ActivateWindow;      // 快速行动/行动/触发（能力激活时机）
        public string ActivateCondition;   // 发动许可（横置本角色+快速行动2…）
        public int Cost;                   // 费用数字（快速行动 N 中的 N）
        public string Timing;              // EffectTiming 名（触发时机）
        public string Duration = "瞬时";    // 常驻/瞬时/直到回合结束
        public List<ServerEffectAction> Actions = new List<ServerEffectAction>();
    }

    /// <summary>单个动作原语（纯托管，对应 Script.Data.Effects.EffectAction：Verb + Args）。</summary>
    public class ServerEffectAction
    {
        public string Verb;                // ActionVerb 名（DealDamage/Move/Draw/Destroy/Sacrifice…）
        public List<string> Args = new List<string>();
    }
}
