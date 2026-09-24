using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC007 常春藤派学者 效果（角色）。
    /// 卡文：进场触发：从你的牌库中寻找一张印刷费用小于或等于2的法术或书籍牌，
    ///      展示该牌后置于你的手中，然后洗牌。
    /// 实现：P2 新增的 `ActionVerb.Search`：
    ///      · 筛选串 `费用&lt;=2;子类别:法术,书籍`（`;`=AND、`,`=OR，见 `DeckSearchFilter`）；
    ///      · 去向 `手牌`；`附加="展示"` → 取牌后公开展示（`UiNotificationBus.CardRevealed`）；
    ///      · 洗牌口径：取到牌/没找到 → 洗牌，玩家放弃 → 不洗牌（引擎统一处理）。
    /// </summary>
    public static class JC007_changchuntengpaixuezhe_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC007_changchuntengpaixuezhe", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC007_1_SearchSpellOrBook",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },   // 检索不需要选目标
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        // [筛选, 去向, 附加, 范围]
                        Args = new Array<string> { "费用<=2;子类别:法术,书籍", "手牌", "展示", "牌库" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "进场时检索牌库（≤2 费的法术或书籍牌），展示后入手并洗牌；找不到也洗牌，放弃则不洗。",
            });
        }
    }
}
