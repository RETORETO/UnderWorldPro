using System;
using System.Collections.Generic;
using Script.Enums;
using Script.Game.Entities;

namespace Script.Manager
{
    /// <summary>
    /// 【P2】牌库检索筛选器（Search 动词用）：把一段紧凑文本解析成"可匹配一张牌"的条件集合。
    ///
    /// 语法：`;` 分段之间是 **AND**，段内 `,` 是 **OR**（同一字段的多取值）。
    /// 支持字段（大小写/空格容忍）：
    /// | 写法 | 含义 |
    /// |---|---|
    /// | `费用&lt;=2` / `费用&gt;=3` | 印刷费用区间（`PlayerTemplate.Cost`） |
    /// | `子类别:法术,书籍` | 当前子类别（含运行时授予）任一中选 |
    /// | `类别:角色` / `类别:附属` / `类别:事务` | 卡类别（`CardCategory`） |
    /// | `关键字:灵体` | 运行时授予 ∪ 模板固有声明 |
    /// | `组织:王座会` | `Organization` 相等 |
    /// | `独有`（无值标志段） | `PlayerTemplate.UniqueField` **非空**＝独有牌（用户口径；秘社检索"【X】独有牌"＝`独有;组织:X`） |
    /// | `卡名:某卡` | 卡名包含 |
    /// 空串 = 全匹配（对应 JC131「寻找一张牌」）。
    ///
    /// 未知字段会让整条筛选**不通过并打日志**（fail-closed，避免"写错字段名 → 静默匹配全库"）。
    /// </summary>
    public static class DeckSearchFilter
    {
        /// <summary>该类筛选是否命中该卡。</summary>
        public static bool Matches(string filter, CardInstance card)
        {
            if (card == null) return false;
            if (string.IsNullOrWhiteSpace(filter)) return true;   // 空串=全匹配

            foreach (var rawSeg in filter.Split(';'))
            {
                var seg = rawSeg.Trim();
                if (seg.Length == 0) continue;
                if (!MatchSegment(seg, card))
                {
                    Godot.GD.Print($"[DeckSearchFilter] 未命中：卡={card.PlayerTemplate?.CardName} 段='{seg}'");
                    return false;
                }
            }
            return true;
        }

        private static bool MatchSegment(string seg, CardInstance card)
        {
            var tmpl = card.PlayerTemplate;

            // 费用区间：费用<=N / 费用>=N（也接受 <= / >= 与全角写法）
            if (TryParseCost(seg, "<=", out int maxCost)) return tmpl != null && tmpl.Cost <= maxCost;
            if (TryParseCost(seg, ">=", out int minCost)) return tmpl != null && tmpl.Cost >= minCost;

            // 【A57·独有牌】`独有`（无值标志段）→ `PlayerTemplate.UniqueField` 非空即为"独有牌"。
            // 用户口径（2026-09）：**UniqueField 不为空就是独有卡**（该字段由卡表数据回填）。
            // 用于秘社检索，如 MSJC001『帷幕守望』行动4「寻找一张【帷幕守望】独有牌」＝ `独有;组织:帷幕守望`。
            if (seg == "独有" || seg == "独有牌") return tmpl != null && !string.IsNullOrWhiteSpace(tmpl.UniqueField);

            int colon = seg.IndexOf(':');
            if (colon <= 0) { Godot.GD.Print($"[DeckSearchFilter] ⚠ 无法解析筛选段 '{seg}'（缺字段名或分隔符）→ 视为不通过"); return false; }
            string field = seg.Substring(0, colon).Trim();
            var values = seg.Substring(colon + 1).Split(',');

            switch (field)
            {
                case "子类别":
                    foreach (var v in values)
                    {
                        string sub = v.Trim();
                        // 牌库中的实例可能尚未 Initialize（CurrentSubCategories 为空），故回落到模板静态子类别。
                        // 【P3·K4】**实例已初始化**（当前集合非空）时**不再回落**：JC036 初拥会把"人类"从实例
                        // 当前类别里改写掉，若仍按模板匹配，`子类别:人类` 类筛选/目标筛选会与 `HasSubCategory` 分叉。
                        bool initialized = card.CurrentSubCategories != null && card.CurrentSubCategories.Count > 0;
                        if (card.HasSubCategory(sub) || (!initialized && card.HasStaticSubCategory(sub))) return true;
                    }
                    return false;
                case "类别":
                    foreach (var v in values)
                    {
                        var flag = ParseCategory(v.Trim());
                        if (flag == CardCategory.None) { Godot.GD.Print($"[DeckSearchFilter] ⚠ 未知类别 '{v}'"); continue; }
                        if (tmpl != null && (tmpl.CategoryFlag & flag) != 0) return true;
                    }
                    return false;
                case "关键字":
                    foreach (var v in values)
                    {
                        string kw = v.Trim();
                        if (card.HasKeyword(kw) || (tmpl?.HasInherentKeyword(kw) ?? false)) return true;
                    }
                    return false;
                case "组织":
                    foreach (var v in values)
                        if (string.Equals(tmpl?.Organization ?? "", v.Trim(), StringComparison.Ordinal)) return true;
                    return false;
                case "卡名":
                    foreach (var v in values)
                        if (!string.IsNullOrEmpty(tmpl?.CardName) && tmpl.CardName.Contains(v.Trim())) return true;
                    return false;
                default:
                    Godot.GD.Print($"[DeckSearchFilter] ⚠ 未知筛选字段 '{field}' → 视为不通过（避免静默全匹配）");
                    return false;
            }
        }

        /// <summary>解析形如 `费用&lt;=2`（容忍空格与 `费`/`印刷费用` 写法）。</summary>
        private static bool TryParseCost(string seg, string op, out int value)
        {
            value = 0;
            int opIdx = seg.IndexOf(op, StringComparison.Ordinal);
            if (opIdx <= 0) return false;
            string head = seg.Substring(0, opIdx).Trim();
            if (head != "费用" && head != "印刷费用") return false;
            return int.TryParse(seg.Substring(opIdx + op.Length).Trim(), out value);
        }

        private static CardCategory ParseCategory(string name)
        {
            switch (name)
            {
                case "角色": return CardCategory.Character;
                case "事务": return CardCategory.Sorcery;
                case "附属": return CardCategory.Attachment;
                case "任务": return CardCategory.Quest;
                case "地区": return CardCategory.Land;
                case "指示物": return CardCategory.Token;
                default: return CardCategory.None;
            }
        }
    }
}
