using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC074 天空的教诲 效果（事务卡，费用 2）。
    /// 卡文：快速行动：目标角色获得［黑眼］［黑眼］和声望直到回合结束。
    ///
    /// 实现（与既有同类卡同一写法）：
    ///  · 【2026-09 整体复查·修复】**快速行动 ⇒ C 档**：卡文只印"快速行动"（**没有**"只能在行动阶段打出"），
    ///    故按 `PlayTiming.Response`（可响应打出、**不限行动阶段**）——与 JC062/JC065 同口径。
    ///    旧实现误写 `LimitToActionPhaseOnly = true` + `PlayTiming.MainPhase`，且注释把这个错误当成
    ///    "与 JC063 等快速行动卡一致的口径"（JC063 本身同样是缺陷，已在 A115① 修复）⇒ 非行动阶段打不出来。
    ///  · `Gain{"黑眼","2","目标"}` —— 图标名经 `EffectEngine.IconToAttribute` 折成**调查属性 +2**
    ///    （黑眼＝调查，见 `IconToAttribute`；与 JC069 警犬"Gain{黑眼}"同一写法）；
    ///  · `Gain{"声望","1","目标"}` —— 授予关键词「声望」（规则本体＝触发效果 `KeywordEffects.Renown()`，
    ///    授权后由 `RuntimeTriggerTemplates` 自动参与收集；见 JC011/JC067 同款）；
    ///  · `Duration = "直到回合结束"` → 两笔授予都登记 `TimedMod`，回合结束自动还原。
    /// </summary>
    public static class JC074_tiankongdejiaohui_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC074_tiankongdejiaohui", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 整体复查·修复】快速行动 = C 档（不限行动阶段）；不再设 LimitToActionPhaseOnly
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC074_A_BlackEye2AndRenown",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "黑眼", "2", "目标" } },
                    new EffectAction { Verb = ActionVerb.Gain, Args = new Array<string> { "声望", "1", "目标" } },
                },
                Duration = "直到回合结束",
                FaqNote = "目标角色获得［黑眼］［黑眼］（＝调查 +2）与「声望」直到回合结束；"
                        + "声望的规则本体由引擎级规则承担（势力对抗结束时该地区声望角色较多的一方额外放置一个势力标志）。",
            });
        }
    }
}
