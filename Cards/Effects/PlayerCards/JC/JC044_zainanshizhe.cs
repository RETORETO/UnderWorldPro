using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC044 灾难使者 效果（角色）。
    /// 卡文：时间标志1 行动，自本方卡牌上移除一个时间标志：遏制1。（目标玩家选择并移除一个他所拥有的势力标志）
    /// 实现（效果级推进）：**只做"时间标志1"**——按用户给出的词条规则"XX标志X = **强制进场触发**：
    ///      卡片进场后触发，给触发这个效果的卡本身添加 X 个 XX 标志"（`KeywordEffects.Marker`）。
    /// ⛔ 未做「自本方卡牌上移除一个时间标志 → 遏制1」：缺"移除卡上标志作代价"，且『遏制X』需要
    ///    "被遏制者选择并移除 X 个势力标志"的原语与分配 UI（本轮待确认口径后实现）。
    /// </summary>
    public static class JC044_zainanshizhe_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC044_zainanshizhe", Build);

        private static void Build(PlayerCardData data)
        {
            var marker = KeywordEffects.Marker("时间标志", 1);
            marker.EffectId = "JC044_A_TimeMarker1";
            data.EffectDataList.Add(marker);
        }
    }
}
