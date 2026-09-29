using System;
using System.Collections.Generic;
using Godot;
using Script.IO;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 卡组校验】服务端卡组校验实现。
    ///
    /// 校验内容：
    ///  1. 卡组 JSON 结构合法（`DeckListLoader.LoadFromText`）或 `deckId` 指向服务端本地卡组文件；
    ///  2. 条目非空（至少 1 张玩家卡）；
    ///  3. 每个模板路径都能解析到模板（`ResourceLoader.Load`，存在性检查）；
    ///  4. 秘社缺失时 `DeckListLoader` 已兜底补默认秘社（与单机一致）；
    ///  5. 【构筑规则 a】玩家卡组总张数 **≥ 50**；
    ///  6. 【构筑规则 b】若卡组携带的秘社声明了构筑限制（`CouncilCardData.Restrictions` 非空），
    ///     则展开后的玩家卡列表必须满足全部限制；不满足时把**具体限制文案**透出（fail 原因）。
    ///
    /// 触发时点：**点「准备」时**校验（不通过则准备不生效）；**点「开始对局」时复检**（不通过则不开始并取消该方准备）。
    /// </summary>
    public class GameDeckValidator : IDeckValidator
    {
        /// <summary>玩家卡组最小张数（构筑规则 a）。</summary>
        public const int MinDeckCardCount = 50;

        public bool Validate(string deckJson, string deckId,
            out DeckList deck, out string deckName, out int cardCount, out string error)
        {
            deck = null;
            deckName = null;
            cardCount = 0;
            error = null;

            // ① 取卡组（客户端内联 JSON 优先；否则服务端本地路径）
            if (!string.IsNullOrWhiteSpace(deckJson))
            {
                deck = DeckListLoader.LoadFromText(deckJson, "client-inline");
                if (deck == null) { error = "卡组 JSON 无法解析"; return false; }
            }
            else if (!string.IsNullOrWhiteSpace(deckId))
            {
                deck = DeckListLoader.Load(deckId.Trim());
                if (deck == null) { error = $"服务端找不到卡组：{deckId}"; return false; }
            }
            else
            {
                error = "未提交卡组";
                return false;
            }

            // ② 条目非空
            if (deck.Cards == null || deck.Cards.Count == 0) { error = "卡组没有任何卡牌"; return false; }

            // ③ 模板存在性（基础校验：坏路径/不存在的卡直接判违规）+ 收集展开后的玩家卡模板
            var expanded = new List<Script.Data.Cards.PlayerCardData>();
            for (int i = 0; i < deck.Cards.Count; i++)
            {
                var entry = deck.Cards[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.TemplatePath))
                {
                    error = $"第 {i + 1} 条卡牌缺少模板路径";
                    return false;
                }
                if (entry.Copies <= 0) { error = $"第 {i + 1} 条卡牌份数非法（{entry.Copies}）"; return false; }
                var tmpl = ResourceLoader.Load<Script.Data.Cards.PlayerCardData>(entry.TemplatePath);
                if (tmpl == null)
                {
                    error = $"卡牌模板不存在：{entry.TemplatePath}";
                    return false;
                }
                // 按份数展开（构筑限制按"实际张数"统计，必须展开）
                for (int c = 0; c < entry.Copies; c++) expanded.Add(tmpl);
            }

            // 秘社模板加载（同时做存在性检查并收集模板，供构筑限制校验）
            var councilTemplates = new List<Script.Data.Cards.CouncilCardData>();
            foreach (var c in deck.Councils ?? new List<DeckList.DeckEntry>())
            {
                if (c == null || string.IsNullOrWhiteSpace(c.TemplatePath)) { error = "秘社条目缺少模板路径"; return false; }
                var ct = ResourceLoader.Load<Script.Data.Cards.CouncilCardData>(c.TemplatePath);
                if (ct == null)
                {
                    error = $"秘社模板不存在：{c.TemplatePath}";
                    return false;
                }
                councilTemplates.Add(ct);
            }

            // ⑤ 构筑规则 a：卡组总张数 ≥ 50
            cardCount = deck.TotalCardCount;
            if (cardCount < MinDeckCardCount)
            {
                error = $"卡组张数不足：当前 {cardCount} 张，需 ≥ {MinDeckCardCount} 张";
                GD.Print($"[GameDeckValidator] 卡组违规（张数不足）：{cardCount} < {MinDeckCardCount}");
                return false;
            }

            // ⑥ 构筑规则 b：秘社构筑限制（没有秘社 / 秘社无限制 → 无限制）
            foreach (var council in councilTemplates)
            {
                if (!council.HasRestrictions) continue;
                var failures = council.ValidateDeckDetailed(expanded);
                if (failures.Count > 0)
                {
                    error = $"不符合秘社「{council.CardName}」的构筑限制：" + string.Join("；", failures);
                    GD.Print($"[GameDeckValidator] 卡组违规（秘社构筑限制）：{error}");
                    return false;
                }
            }

            deckName = string.IsNullOrWhiteSpace(deck.DeckName) ? "未命名卡组" : deck.DeckName;
            GD.Print($"[GameDeckValidator] 卡组通过（{deckName}，卡 {cardCount} 张 / 秘社 {deck.TotalCouncilCount} 张）");
            return true;
        }
    }
}
