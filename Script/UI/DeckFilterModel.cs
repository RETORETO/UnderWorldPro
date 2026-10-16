using System.Collections.Generic;
using Script.IO;
using Script.Net;

namespace Script.UI
{
    /// <summary>
    /// 【卡组编辑器】卡池检索条件容器 + 匹配引擎。
    ///
    /// 语义（用户 2026-10-17 拍板）：
    ///   · 文本类（卡名 / 副卡名 / 子类别）= 子串包含，ASCII 折叠、序数比较；
    ///   · 类别 = `CardCategory` **位掩码按位包含**（选"角色"可命中 `角色|附属` 的复合卡）；
    ///   · 魔法领域 = 卡牌领域集合包含所选值；可选"无领域"；
    ///   · 忠诚 = **AND 组合**，且要求卡牌该忠诚点数 **≥** 输入值；
    ///   · 7 属性 = **≥** 语义（输入 N 表示"该属性 ≥ N"；留空不筛）；
    ///     防御为 -1（无防御）的卡天然不满足任何非负阈值，故无需特判；
    ///   · 独有卡 = 复选，勾选后只要 `UniqueField` 非空（ASCII 判空）。
    ///   所有条件之间以及（由 DeckBuilder 组合的）编号条件之间都是 **AND**。
    ///
    /// Web 安全：只用 <see cref="WebSafeText"/> 的 ASCII 折叠 / 判空，字符串只做序数 `Contains`，
    /// 绝不碰 ICU / 文化敏感 API。所有"需要折叠"的串都在 <see cref="PrepareForSearch"/> 里折一次。
    /// </summary>
    public class DeckFilterModel
    {
        // —— 域值哨兵 ——
        /// <summary>领域不限。</summary>
        public const int DomainAny = -2;
        /// <summary>仅"无领域"。</summary>
        public const int DomainNone = -1;

        // —— 属性下标（对应 MinAttr 数组）——
        public const int AttrInvestigation = 0;
        public const int AttrStrength = 1;
        public const int AttrPower = 2;
        public const int AttrDefense = 3;
        public const int AttrFsInvestigation = 4;
        public const int AttrFsStrength = 5;
        public const int AttrFsPower = 6;
        public const int AttrCount = 7;

        /// <summary>编号/文件名子串（沿用原 SearchBox）。匹配由 <see cref="MatchesUid"/> 负责。</summary>
        public string UidQuery = "";

        /// <summary>卡名子串（中文，子串匹配）。</summary>
        public string NameQuery = "";

        /// <summary>副卡名子串（⚠ 数据现状：全部卡此字段为空，搜不到属正常）。</summary>
        public string SubCardNameQuery = "";

        /// <summary>子类别子串（对数组任一元素命中即可）。</summary>
        public string SubCategoryQuery = "";

        /// <summary>类别位掩码；0 = 不筛。</summary>
        public int CategoryMask = 0;

        /// <summary>魔法领域过滤；<see cref="DomainAny"/> = 不限，<see cref="DomainNone"/> = 仅无领域，0..4 = 指定领域。</summary>
        public int DomainFilter = DomainAny;

        /// <summary>忠诚条件（AND 组合）。</summary>
        public List<LoyaltyCond> Loyalties = new List<LoyaltyCond>();

        /// <summary>7 个属性的下限；-1 = 不筛。顺序见 Attr* 常量。</summary>
        public int[] MinAttr = new int[AttrCount] { -1, -1, -1, -1, -1, -1, -1 };

        /// <summary>仅独有卡。</summary>
        public bool UniqueOnly = false;

        // —— 折叠后的检索词（PrepareForSearch 填充；null 表示该条件不启用）——
        private string _uid;
        private string _name;
        private string _subName;
        private string _subCat;

        /// <summary>是否存在任一条"结构化/文本"条件（编号除外）。用于判断要不要走索引过滤。</summary>
        public bool HasStructured =>
            (NameQuery != null && NameQuery.Length > 0)
            || (SubCardNameQuery != null && SubCardNameQuery.Length > 0)
            || (SubCategoryQuery != null && SubCategoryQuery.Length > 0)
            || CategoryMask != 0
            || DomainFilter != DomainAny
            || (Loyalties != null && Loyalties.Count > 0)
            || UniqueOnly
            || HasAnyMinAttr;

        /// <summary>是否有任一属性下限被设置。</summary>
        public bool HasAnyMinAttr
        {
            get
            {
                for (int i = 0; i < AttrCount; i++)
                    if (MinAttr != null && i < MinAttr.Length && MinAttr[i] >= 0) return true;
                return false;
            }
        }

        /// <summary>
        /// 每次搜索前调用一次：把文本条件折叠成检索词。**必须先调用再逐条 <see cref="Matches"/>**，
        /// 否则文本条件不会生效。
        /// </summary>
        public void PrepareForSearch()
        {
            _uid = FoldOrNull(UidQuery);
            _name = FoldOrNull(NameQuery);
            _subName = FoldOrNull(SubCardNameQuery);
            _subCat = FoldOrNull(SubCategoryQuery);
        }

        private static string FoldOrNull(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = WebSafeText.TrimAscii(s);
            if (string.IsNullOrEmpty(t)) return null;
            return WebSafeText.ToLowerAscii(t);
        }

        /// <summary>编号/文件名子串是否命中（空查询 = 视为命中）。</summary>
        public bool MatchesUid(DeckSearchIndexEntry e)
        {
            if (e == null) return false;
            if (_uid == null) return true;
            string stem = e.FileStemFold ?? e.FileStem;
            return stem != null && stem.Contains(_uid);
        }

        /// <summary>结构化条件是否全部满足（不含编号条件；编号由 <see cref="MatchesUid"/> 单独组合）。</summary>
        public bool Matches(DeckSearchIndexEntry e)
        {
            if (e == null) return false;

            // 文本条件
            if (_name != null && !Contains(e.NameFold, _name)) return false;
            if (_subName != null && !Contains(e.SubCardNameFold, _subName)) return false;
            if (_subCat != null && !AnyContains(e.SubCategoriesFold, _subCat)) return false;

            // 类别：位掩码按位包含
            if (CategoryMask != 0 && (e.CategoryFlagValue & CategoryMask) == 0) return false;

            // 魔法领域
            if (DomainFilter != DomainAny)
            {
                bool none = e.MagicDomains == null || e.MagicDomains.Count == 0;
                if (DomainFilter == DomainNone)
                {
                    if (!none) return false;
                }
                else
                {
                    if (none || !e.MagicDomains.Contains(DomainFilter)) return false;
                }
            }

            // 忠诚：AND 组合 + 点数 ≥
            if (Loyalties != null && Loyalties.Count > 0)
            {
                foreach (var c in Loyalties)
                {
                    if (c == null) continue;
                    var list = c.IsColor ? e.ColorLoyalties : e.DomainLoyalties;
                    if (!LoyaltySatisfied(list, c.Value, c.Amount)) return false;
                }
            }

            // 7 属性：≥ 语义（留空跳过）
            if (MinAttr != null)
            {
                if (MinAttr[AttrInvestigation] >= 0 && e.Investigation < MinAttr[AttrInvestigation]) return false;
                if (MinAttr[AttrStrength] >= 0 && e.Strength < MinAttr[AttrStrength]) return false;
                if (MinAttr[AttrPower] >= 0 && e.Power < MinAttr[AttrPower]) return false;
                // 防御：-1（无防御）天然小于任何非负阈值 → 自动被排除，无需特判
                if (MinAttr[AttrDefense] >= 0 && e.Defense < MinAttr[AttrDefense]) return false;
                if (MinAttr[AttrFsInvestigation] >= 0 && e.FirstStrikeInvestigation < MinAttr[AttrFsInvestigation]) return false;
                if (MinAttr[AttrFsStrength] >= 0 && e.FirstStrikeStrength < MinAttr[AttrFsStrength]) return false;
                if (MinAttr[AttrFsPower] >= 0 && e.FirstStrikePower < MinAttr[AttrFsPower]) return false;
            }

            // 独有卡
            if (UniqueOnly && WebSafeText.IsNullOrWhiteSpaceAscii(e.UniqueField)) return false;

            return true;
        }

        // ============================================================
        // 小工具（全部序数，Web 安全）
        // ============================================================
        /// <summary>`haystack` 是否包含 `needle`（两者均已折叠；空 needle 视为不启用，调用方已排除）。</summary>
        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            // string.Contains(string) 默认序数比较，不碰 ICU
            return haystack.Contains(needle);
        }

        private static bool AnyContains(List<string> haystacks, string needle)
        {
            if (haystacks == null || haystacks.Count == 0) return false;
            for (int i = 0; i < haystacks.Count; i++)
                if (Contains(haystacks[i], needle)) return true;
            return false;
        }

        private static bool LoyaltySatisfied(List<LoyaltyEntry> list, int value, int amount)
        {
            if (list == null || list.Count == 0) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var le = list[i];
                if (le != null && le.Value == value && le.Amount >= amount) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 一条忠诚检索条件：`IsColor` 决定查颜色忠诚列表还是领域忠诚列表，
    /// `Value` 是对应枚举值，`Amount` 是要求达到的最小点数（≥）。
    /// </summary>
    public class LoyaltyCond
    {
        public bool IsColor;
        public int Value;
        public int Amount;
    }
}
