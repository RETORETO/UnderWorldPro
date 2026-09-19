using System;
using Godot;
using Script.IO;
using Script.Net.Lobby;

namespace Script.Net.Godot
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】服务端卡组校验实现（D6：先只做**基础校验**）。
    ///
    /// 校验内容：
    ///  1. 卡组 JSON 结构合法（`DeckListLoader.LoadFromText`）或 `deckId` 指向服务端本地卡组文件；
    ///  2. 条目非空（至少 1 张玩家卡）；
    ///  3. 每个模板路径都能解析到模板（`ResourceLoader.Load`，存在性检查）；
    ///  4. 秘社缺失时 `DeckListLoader` 已兜底补默认秘社（与单机一致）。
    ///
    /// **不做**（本轮明确后置，协议字段已预留）：卡组张数上下限、同名卡上限、禁卡表。
    /// </summary>
    public class GameDeckValidator : IDeckValidator
    {
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

            // ③ 模板存在性（基础校验：坏路径/不存在的卡直接判违规）
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
            }
            foreach (var c in deck.Councils ?? new System.Collections.Generic.List<DeckList.DeckEntry>())
            {
                if (c == null || string.IsNullOrWhiteSpace(c.TemplatePath)) { error = "秘社条目缺少模板路径"; return false; }
                if (ResourceLoader.Load<Script.Data.Cards.CouncilCardData>(c.TemplatePath) == null)
                {
                    error = $"秘社模板不存在：{c.TemplatePath}";
                    return false;
                }
            }

            deckName = string.IsNullOrWhiteSpace(deck.DeckName) ? "未命名卡组" : deck.DeckName;
            cardCount = deck.TotalCardCount;
            GD.Print($"[GameDeckValidator] 卡组通过（{deckName}，卡 {cardCount} 张 / 秘社 {deck.TotalCouncilCount} 张）");
            return true;
        }
    }
}
