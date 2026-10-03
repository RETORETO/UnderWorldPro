using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC087 格鲁尼的走私客 效果（角色）。
    /// 卡文：撤回 触发：当格鲁尼的走私客进入一个地区时，你可以令本地区的目标本方角色潜伏，若你如此做，在该地区上放置一个本方势力标志。
    ///
    /// 实现：
    ///  · **撤回** ＝ `KeywordEffects.Withdraw()`（地区被赢取时回 OWNER 手牌）；
    ///  · **触发** ＝ **`OnMove`（进入/移动到某个地区之后）**——`ZoneManager.MoveToArea` 在位置变更后广播，
    ///    带 `SourceAreaAtTrigger`＝新地区；**进场（手牌→争夺区）与"争夺区→争夺区"移动（被 JC117 等效果移动）都覆盖**。
    ///    ⚠ 修复记录：原挂 `EnterTrigger`（只在"非场地区→争夺区"时发）⇒ 被效果移动时不触发，与卡文"进入一个地区"不符。
    ///  · 目标 ＝ **本地区**的目标**本方**角色（`IsLocalZone` + `Friendly`）；
    ///  · 动作链 ＝ `Latent{目标}` → `PlaceToken{势力标志,1}`（落点＝来源所在地区）；
    ///  · **"若你如此做"** 由**动作成果**机制承担：`Latent` 未真正生效（目标已是暗藏者/已离场/未选目标）
    ///    → 置链式中止标志 → 后续放标志整段跳过。
    /// </summary>
    public static class JC087_gelunidezousike_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC087_gelunidezousike", Build);

        private static void Build(PlayerCardData data)
        {
            var withdraw = KeywordEffects.Withdraw();
            withdraw.EffectId = "JC087_A_Withdraw";
            data.EffectDataList.Add(withdraw);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC087_1_LatentAllyThenPlaceMark",
                Timing = EffectTiming.OnMove,   // 「进入一个地区」＝位置变更后（覆盖进场 + 跨地区移动）
                Forced = false,                     // 卡文"你可以"
                // 自身语义由引擎保证（`TriggerRegistry` 的 OnMove/EnterTrigger 同款白名单），无需写条件。
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IsLocalZone = true,             // "本地区的目标本方角色"
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                    // 【2026-09 修复】"本地区的目标本方角色"未排除自身（走私客本身就是本地区的本方角色）
                    // ⇒ 可指定自身；否则它是本地区唯一角色时该触发会被当作"无合法目标"静默跳过（JC004 同类）。
                    CanTargetSelf = true,
                },
                Actions = new Array<EffectAction>
                {
                    // ① 令目标潜伏（未生效 → ② 整段跳过）
                    new EffectAction { Verb = ActionVerb.Latent, Args = new Array<string> { "目标" } },
                    // ② 若你如此做：在该地区放置一个本方势力标志（落点＝来源所在地区）
                    new EffectAction { Verb = ActionVerb.PlaceToken, Args = new Array<string> { "势力标志", "1" } },
                },
                Duration = "瞬时",
                FaqNote = "进入地区时可令本地区目标本方角色潜伏；若如此做，在该地区放置一个本方势力标志。",
            });
        }
    }
}
