using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC065 审问嫌疑人 效果（事务卡；`Cost = 1`，子类别「策略」）。
    /// 卡文：快速行动：选择目标地区，然后目标对手牺牲一个位于该地区的暗藏者。遏制1。
    ///      （目标玩家选择并移除一个他所拥有的势力标志）
    /// 实现（口径 A11/A12）：
    ///  · 打出目标＝**地区**（"选择目标地区"）；
    ///  · ① `SacrificeChoice{"暗藏者","目标玩家","目标地区"}` —— **由对手自己选择**牺牲哪个**位于该地区**的暗藏者
    ///    （无合法候选 → 该玩家跳过、什么都不付出）；牺牲＝死亡，走死亡时机；
    ///  · ② `Curb{"1"}`（遏制1：被遏制者＝缺省＝施术者对手，由他选择并移除一个自己拥有的势力标志，`CurbRule`）。
    /// </summary>
    public static class JC065_shenwenxianyiren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC065_shenwenxianyiren", Build);

        private static void Build(PlayerCardData data)
        {
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),        // "选择目标地区"
            };
            // 【2026-09 修复·用户口径】卡文**快速行动** ⇒ 取 **C 档**（`PlayTiming.Response`）：
            // 任意有优先权时均可打出（含响应窗口 / 堆叠非空 / 非行动阶段），**不限行动阶段**。
            // 用户实测："JC065 的门禁疑似错误，现在不能在非行动阶段发动"——
            // 根因是原先写成 `LimitToActionPhaseOnly=true`（B 档＝只允许行动阶段）＋ `PlayTiming.MainPhase`。
            // 与 JC049 的同类修复（"卡文『快速』⇒ 快速（响应）档：可插队响应打出"）以及
            // JC062/JC008/JC106/JC135 的既有口径完全一致。

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC065_A_OpponentSacrificesCovertInArea",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,        // 快速行动：有优先权即可打出（C 档，不限行动阶段）
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction
                    {
                        Verb = ActionVerb.SacrificeChoice,
                        Args = new Array<string> { "暗藏者", "目标玩家", "目标地区" },
                    },
                    new EffectAction { Verb = ActionVerb.Curb, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "选择目标地区：由对手自己选择并牺牲一个位于该地区的暗藏者（没有则不牺牲）；随后遏制1"
                        + "（对手选择并移除一个自己拥有的势力标志）。",
            });
        }
    }
}
