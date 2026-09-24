using System;
using Script.Data.Cards;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【P1】玩家级费用修正的"过滤串"匹配器（**服务端与客户端共用的唯一实现**）。
    ///
    /// 背景：`Effects` 里"下一张某类牌费用-N"的限定条件远超"卡名/组织"两种（如"具【血】图标的牌"
    /// "装备牌""灾难牌"）。若让服务端 `EffectEngine.GetCostModification` 与客户端减费显示各写一份匹配，
    /// 就会出现"客户端显示 -1、服务端按 -0 结算"的两套逻辑（本项目 P0~P2 治理的同一类问题）。
    /// 故匹配规则集中在本类，两端都调它：服务端传入本局权威实例，客户端传入快照投影实例。
    ///
    /// 过滤串语法（**向后兼容**：无前缀的普通串保持旧语义）：
    /// | 写法 | 语义 |
    /// |---|---|
    /// | 空串 | 全匹配（任意牌） |
    /// | `帷幕守望` | 旧语义：**卡名包含**该串 **或** Organization 等于该串 |
    /// | `组织:王座会` | 仅 Organization 相等 |
    /// | `子类别:装备` | 当前子类别含该值（含运行时授予） |
    /// | `域:Blood` / `域:血` | 具有指定魔法领域图标（枚举名或中文均可） |
    /// | `有域图标` | 具有任意魔法领域图标（对应 JC112「具有魔法领域图标的牌」） |
    /// </summary>
    public static class CostModFilter
    {
        /// <summary>过滤串前缀（带前缀的走新语义，其余走旧语义）。</summary>
        public const string OrgPrefix = "组织:";
        public const string SubCategoryPrefix = "子类别:";
        public const string DomainPrefix = "域:";
        public const string HasAnyDomainToken = "有域图标";

        /// <summary>
        /// 判断某条费用修正的过滤串是否命中一张牌。
        /// </summary>
        /// <param name="filterText">过滤串（见类注释语法表）。</param>
        /// <param name="inst">待判定的卡实例（可为 null：此时仅能按卡名/组织判定，新语义一律不命中）。</param>
        /// <param name="cardName">卡名（可空）。</param>
        /// <param name="organization">组织/阵营（可空）。</param>
        public static bool Matches(string filterText, CardInstance inst, string cardName, string organization)
        {
            if (string.IsNullOrEmpty(filterText)) return true;   // 空串=全匹配

            // 【P1】`|` 分段 = **或**语义（如卡文"【鸣钟教派】牌**或**具有【血】图标的牌"）。
            // 若不拆段而整体比对，同一张牌会被两条不同条件重复命中 → 减费叠加（规则上不应叠加）。
            if (filterText.IndexOf('|') >= 0)
            {
                foreach (var seg in filterText.Split('|'))
                    if (MatchesSingle(seg, inst, cardName, organization)) return true;
                return false;
            }
            return MatchesSingle(filterText, inst, cardName, organization);
        }

        /// <summary>单段过滤串匹配（不含 `|` 或语义）。</summary>
        private static bool MatchesSingle(string filterText, CardInstance inst, string cardName, string organization)
        {
            if (string.IsNullOrEmpty(filterText)) return true;
            string org = organization ?? inst?.PlayerTemplate?.Organization ?? "";

            if (filterText.StartsWith(OrgPrefix, StringComparison.Ordinal))
                return string.Equals(org, filterText.Substring(OrgPrefix.Length), StringComparison.Ordinal);

            if (filterText.StartsWith(SubCategoryPrefix, StringComparison.Ordinal))
            {
                if (inst == null) return false;
                string sub = filterText.Substring(SubCategoryPrefix.Length);
                return !string.IsNullOrEmpty(sub) && inst.HasSubCategory(sub);
            }

            if (filterText.StartsWith(DomainPrefix, StringComparison.Ordinal))
            {
                if (inst == null) return false;
                if (!TryParseDomain(filterText.Substring(DomainPrefix.Length), out var want)) return false;
                // 实例领域优先（含运行时授予），回退模板印刷领域
                if (inst.HasMagicDomain(want)) return true;
                var tmplDomains = inst.PlayerTemplate?.MagicDomains;
                if (tmplDomains == null) return false;
                foreach (var d in tmplDomains)
                    if (d == want) return true;
                return false;
            }

            if (string.Equals(filterText, HasAnyDomainToken, StringComparison.Ordinal))
                return inst != null && inst.HasAnyMagicDomainIcon;

            // 旧语义：卡名包含 **或** Organization 相等
            if (!string.IsNullOrEmpty(cardName) && cardName.Contains(filterText)) return true;
            return !string.IsNullOrEmpty(org) && string.Equals(org, filterText, StringComparison.Ordinal);
        }

        /// <summary>领域名解析：接受枚举名（Mind/Divine/Star/Death/Blood）与中文（心/圣/星/死/血）。</summary>
        public static bool TryParseDomain(string text, out MagicDomain domain)
        {
            domain = MagicDomain.Mind;
            if (string.IsNullOrEmpty(text)) return false;
            if (Enum.TryParse(text, ignoreCase: true, out MagicDomain parsed) && Enum.IsDefined(typeof(MagicDomain), parsed))
            {
                domain = parsed;
                return true;
            }
            switch (text)
            {
                case "心": case "心灵": domain = MagicDomain.Mind; return true;
                case "圣": case "神圣": domain = MagicDomain.Divine; return true;
                case "星": case "星空": domain = MagicDomain.Star; return true;
                case "死": case "死亡": domain = MagicDomain.Death; return true;
                case "血": case "鲜血": domain = MagicDomain.Blood; return true;
                default: return false;
            }
        }
    }
}
