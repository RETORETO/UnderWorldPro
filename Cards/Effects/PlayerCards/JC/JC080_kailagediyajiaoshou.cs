using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC080 凯拉·歌迪亚教授 效果（角色）。
    /// 卡文：公开 声望 触发：每当你抓牌时，你可以在本地区上放置X个本方势力标记。X等于此次抓牌的数量。
    ///
    /// 实现（P3）：
    ///  · 【2026-09 整体复查·修复】**前缀「公开」「声望」必须由本脚本显式装载**：旧注释写的
    ///    "由引擎侧提供"是**错的**——`HasInherentKeyword` 只读 `EffectDataList`，没有"按卡面文本自动装载"的机制
    ///    ⇒ 旧实现下 `公开` 门禁与 `声望`（规则本体＝触发效果）全都缺失。现：
    ///    `KeywordEffects.Public()`（手牌不可派遣暗藏）＋ `KeywordEffects.RenownKeyword()`（声望关键字，供计数）
    ///    ＋ `KeywordEffects.Renown()`（声望规则本体：**每次势力对抗结束后**触发，声望角色较多方放置 1 个势力标志）。
    ///  · **触发时机 `OnDraw`**：**一次抓牌动作 = 一批**（用户 2026-09 口径），
    ///    `TurnManager.DrawCardsForPlayer/DrawFromBottomForPlayer` 在一批抓完后**只广播一次** `OnDraw`，
    ///    并把本批张数写进 `TriggerContext.ExtraInt`；
    ///  · 条件 `TriggerBySelf`：只有"**你**（来源控制者）抓牌"才触发，对手抓牌不触发；
    ///  · **动态值 X（K1）**：`ValueExpr="事件数量"`＝本批抓牌张数，动作参数写 `$X`（结算前替换）；
    ///  · 动作 `PlaceToken{势力标志, $X}`：落点取来源卡所在地区（＝凯拉的"本地区"），
    ///    放置归**来源控制者**（＝本方）——与卡文"本方势力标记"一致；
    ///  · **可选**（卡文"你可以"）：`Forced=false`，由触发窗口让玩家决定是否结算。
    /// </summary>
    public static class JC080_kailagediyajiaoshou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC080_kailagediyajiaoshou", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 整体复查·修复】前缀「公开」「声望」显式装载（见类注释）
            var publicKw = KeywordEffects.Public();
            publicKw.EffectId = "JC080_0_Public";
            data.EffectDataList.Add(publicKw);

            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = "JC080_0_Renown";
            data.EffectDataList.Add(renown);

            var renownTrigger = KeywordEffects.Renown();
            renownTrigger.EffectId = "JC080_0_RenownTrigger";
            data.EffectDataList.Add(renownTrigger);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC080_1_PlaceMarksOnDraw",
                Timing = EffectTiming.OnDraw,
                Condition = new EffectCondition { Predicate = "TriggerBySelf" },
                ValueExpr = "事件数量",              // 【K1】X ＝ 此次抓牌的数量（本批张数）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.PlaceToken,
                        // [指示物类型, 数量]：数量用占位符，结算时替换为本批抓牌张数
                        Args = new Array<string> { "势力标志", "$X" },
                    },
                },
                Forced = false,                       // 卡文"你可以"→ 可选触发
                Duration = "瞬时",
                FaqNote = "每当你抓牌时（一次抓牌动作＝一批，X＝该批张数）可在本地区放置 X 个本方势力标志；可选。",
            });
        }
    }
}
