using Script.IO;

namespace Script.Net.Lobby
{
    /// <summary>
    /// 【大厅/房间 · 增量 A】卡组校验抽象（**纯接口**，Godot 侧由 `GameDeckValidator` 实现）。
    ///
    /// 校验规则（D6：先只做**基础校验**，张数/同名上限留待后续）：
    ///  1. 卡组 JSON 结构合法、条目非空；
    ///  2. 每张卡模板路径都能解析到模板（存在性）；
    ///  3. 缺少秘社时由服务端补默认秘社（沿用 `DeckListLoader` 的兜底）。
    /// 触发时点（D6）：**点「准备」时**校验（不通过则准备不生效）；**点「开始对局」时复检**（不通过则不开始并取消该方准备）。
    /// </summary>
    public interface IDeckValidator
    {
        /// <summary>
        /// 校验一个卡组提交。
        /// </summary>
        /// <param name="deckJson">卡组 JSON 文本（客户端提交，优先）。</param>
        /// <param name="deckId">卡组标识（服务端本地路径，次选；BOT/测试用）。</param>
        /// <param name="deck">校验通过时的卡组对象。</param>
        /// <param name="deckName">卡组显示名（空则用默认名）。</param>
        /// <param name="cardCount">卡牌张数。</param>
        /// <param name="error">违规原因（面向玩家；通过时为 null）。</param>
        /// <returns>是否通过。</returns>
        bool Validate(string deckJson, string deckId, out DeckList deck, out string deckName, out int cardCount, out string error);
    }

    /// <summary>默认（不校验）实现：仅用于不以真实卡组驱动的测试替身场景；生产必须注入真实校验器。</summary>
    public sealed class PermissiveDeckValidator : IDeckValidator
    {
        public bool Validate(string deckJson, string deckId, out DeckList deck, out string deckName, out int cardCount, out string error)
        {
            deck = null;
            deckName = string.IsNullOrEmpty(deckId) ? "未指定卡组" : deckId;
            cardCount = 0;
            error = null;
            return true;
        }
    }
}
