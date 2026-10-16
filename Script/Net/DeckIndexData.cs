using System.Collections.Generic;

namespace Script.Net
{
    /// <summary>
    /// 【卡组编辑器检索索引 · 纯托管 POCO】由 `Cards/card_index.json` 反序列化而来。
    ///
    /// 为什么单独开一份精简 POCO、不复用 <see cref="CardTemplateData"/>：
    ///   `card_templates.json` 是 1.13MB 的 C/S 协议快照（含 Effects / EffectDataList / TargetRules），
    ///   而卡池检索只需要"能过滤的十几列 + 一份 res 路径"，精简后索引约 120~200KB，
    ///   Web 端反序列化显著更快、语义也更单一，且不会把 M2 协议拖成卡组编辑器的构建依赖。
    ///
    /// ⚠ 本类必须保持"纯数据、纯托管、无 Godot 引用"，才能被 System.Text.Json 直接反序列化。
    ///   （`System.Text.Json` + `IncludeFields=true` 在 Web 上已由 Script/Net 下的网络序列化器验证可用。）
    /// </summary>
    public class DeckIndexRoot
    {
        /// <summary>条目数。运行时与"实际扫描到的玩家卡数"比对，不一致即说明索引过期（打警告）。</summary>
        public int EntryCount;

        /// <summary>生成时间（由 Godot 的 Time API 产出的字符串，**不经 .NET 格式化**）。仅作排查用。</summary>
        public string GeneratedAt;

        /// <summary>索引条目（仅玩家卡；秘社/地区卡不进卡池，故不入索引）。</summary>
        public List<DeckSearchIndexEntry> Cards = new List<DeckSearchIndexEntry>();
    }

    /// <summary>
    /// 单张玩家卡的检索用索引条目。字段名即 `card_index.json` 的键名（PascalCase，不打 camelCase），
    /// 保证 C# 侧零映射直接反序列化。
    /// </summary>
    public class DeckSearchIndexEntry
    {
        /// <summary>资源路径（`res://Cards/Data/PlayerCards/.../JC001_xxx.tres`）。渲染与入库都用它，是索引 → 现实体的唯一钥匙。</summary>
        public string ResPath;

        /// <summary>文件名不含扩展名（如 `JC001_tonglingmaotouying`）。**编号搜索的匹配源**，保持与改造前"按文件名子串"行为一致。</summary>
        public string FileStem;

        public string UID;
        public string CardName;
        public string SubCardName;

        /// <summary>独有标识，**非空即"独有卡"**（用户 2026-09 口径）。</summary>
        public string UniqueField;

        public int Cost = -1;

        /// <summary>`(int)CardCategory` 位掩码。存整数而非枚举名，过滤时直接按位与，免去运行期解析。</summary>
        public int CategoryFlagValue;

        public List<string> SubCategories = new List<string>();

        /// <summary>颜色（`(int)CardColor`）。当前检索条件未用到，保留供后续扩展。</summary>
        public List<int> Colors = new List<int>();

        /// <summary>魔法领域（`(int)MagicDomain`）。**已剔除 -1 哨兵**：本列表为空即"无领域"。</summary>
        public List<int> MagicDomains = new List<int>();

        public string Organization;

        // —— 7 个能力属性（基础 4 + 先手 3）——
        public int Investigation;
        public int Strength;
        public int Power;
        public int FirstStrikeInvestigation;
        public int FirstStrikeStrength;
        public int FirstStrikePower;

        /// <summary>防御值，**-1 表示无防御**（筛选时该卡不计入防御条件）。</summary>
        public int Defense = -1;

        /// <summary>颜色忠诚需求（`Value = (int)CardColor`，`Amount` = 所需点数）。</summary>
        public List<LoyaltyEntry> ColorLoyalties = new List<LoyaltyEntry>();

        /// <summary>领域忠诚需求（`Value = (int)MagicDomain`，`Amount` = 所需点数）。</summary>
        public List<LoyaltyEntry> DomainLoyalties = new List<LoyaltyEntry>();

        // ============================================================
        // 【运行时派生字段】由 CardSearchIndex 在载入后填充（ASCII 折叠一次，供每次搜索复用）。
        // 不写入 JSON：构建器构造条目时这些字段恒为 null，序列化选项已设 WhenWritingNull。
        // ============================================================

        /// <summary>运行时：<see cref="FileStem"/> 的 ASCII 折叠（编号搜索复用）。</summary>
        public string FileStemFold;

        /// <summary>运行时：<see cref="CardName"/> 的 ASCII 折叠。</summary>
        public string NameFold;

        /// <summary>运行时：<see cref="SubCardName"/> 的 ASCII 折叠。</summary>
        public string SubCardNameFold;

        /// <summary>运行时：<see cref="SubCategories"/> 各元素的 ASCII 折叠。</summary>
        public List<string> SubCategoriesFold;
    }

    /// <summary>
    /// 一条忠诚需求。`Value` 用整数承载枚举值（颜色或领域，取决于所在列表），
    /// 过滤时是纯整数比较，不必做字符串/枚举名解析。
    /// </summary>
    public class LoyaltyEntry
    {
        /// <summary>颜色或领域的枚举整数值（颜色见 CardColor、领域见 MagicDomain）。</summary>
        public int Value;

        /// <summary>所需点数。</summary>
        public int Amount;
    }
}
