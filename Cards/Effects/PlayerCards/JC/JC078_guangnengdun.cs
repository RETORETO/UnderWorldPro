using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC078 光能盾 效果（事务卡 / 法术·能量 / 圣贤）。
    /// 卡文：快速行动：本回合中，为目标角色防止接下来将受到的所有伤害。
    ///
    /// 实现（P3·K3 第一批：**防止伤害**原语落地）：
    ///  · **快速行动** ＝ 可响应打出（与 JC008/JC062 同一口径：`PlayTiming.Response`）；
    ///  · 目标＝**任意角色**（不分敌我，卡文只写"目标角色"）；
    ///  · `ActionVerb.PreventDamage`（本轮首次实现，原先只是枚举占位/TODO）：
    ///    为该角色挂一条**伤害护盾**（`TimedMod{Kind="DamageShield"}`，期限＝**本回合**，回合结束自动出栈）；
    ///    护盾期间对该角色造成的**任何**伤害（战斗伤害 / 效果伤害 / 创伤）全部被防止——
    ///    不写伤害 → 不判死 → **不触发死亡**（死亡触发的前提是死亡真的发生）。
    ///  · 消费点＝`CardManager.DealDamageToInstance` 的 `DamagePreventer` 钩子（伤害唯一收口，暗藏者免疫也在同一处）。
    ///
    /// 口径说明（**不是猜测，是引擎既有约定**）：
    ///  · 阻止点唯一：伤害只经 `DealDamageToInstance` 落地，故护盾无法被任何来源绕过（`MassDamage`/战斗窗口/效果伤害同路）。
    ///  · 护盾**不消耗/不计数**：卡文是"所有伤害"而非"接下来 X 点伤害"，故同卡同回合只登记一条（幂等），不叠加。
    ///  · 目标不含暗藏者：既有 `TargetSpec` 约定——不写 `IncludeCovert` 即只选正面角色
    ///    （暗藏者本就免疫一切伤害，为它挂护盾无意义，故无需特意纳入）。
    /// </summary>
    public static class JC078_guangnengdun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC078_guangnengdun", Build);

        private static void Build(PlayerCardData data)
        {
            // "快速行动"：卡上含 PlayTiming.Response 的效果即可响应打出（C 档）
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character, TargetRestriction.None),   // 目标角色（不分敌我）
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC078_A_PreventAllDamage",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.Response,     // 快速行动
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PreventDamage, Args = new Array<string> { "目标" } },
                },
                Duration = "直到回合结束",   // 卡文"本回合中"＝引擎既有时效写法（`Duration` 含"回合结束"即时效项）
                FaqNote = "本回合中，目标角色接下来将受到的所有伤害被防止（不写伤害 → 不判死 → 不触发死亡）；"
                        + "护盾不消耗、不叠加，回合结束自动失效。",
            });
        }
    }
}
