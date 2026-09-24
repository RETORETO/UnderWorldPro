using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC097 阿斯旺 效果（角色）。
    /// 卡文：隐匿（该角色不能正面打出，只能以秘密派遣方式作为暗藏者进场。）
    ///      袭击X，X等于你墓地中角色牌的数量。
    /// 实现（P3）：
    ///  · 隐匿：`KeywordEffects.Stealth()`（手牌期生效：禁止"从手牌付费正面打出到争夺区"，秘密派遣等不受限）；
    ///  · **袭击X（K1 动态值）**：现身触发（`UncoverTrigger`）+ `DealDamage{$X}`，
    ///    `ValueExpr="计数:区域:墓地:角色:自己"` ＝ **本方墓地中角色牌的数量**；
    ///  · **X=0 时该效果不算发动**（用户 2026-09 口径）：墓地里没有角色牌 → 袭击不发动（收集阶段即被拦下）。
    /// </summary>
    public static class JC097_asiwang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC097_asiwang", Build);

        private static void Build(PlayerCardData data)
        {
            var stealth = KeywordEffects.Stealth();
            stealth.EffectId = "JC097_A_Stealth";
            data.EffectDataList.Add(stealth);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC097_1_StrikeByGraveyardCharacters",
                Timing = EffectTiming.UncoverTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                ValueExpr = "计数:区域:墓地:角色:自己",   // 【K1】X ＝ 你墓地中角色牌的数量
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    CanTargetSelf = true,
                    IsLocalZone = true,      // 袭击＝本地区（同 AREASLOT）的目标角色
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "$X" } },
                },
                Forced = false,
                Duration = "瞬时",
                FaqNote = "现身进场成功后，对本地区目标角色造成 X 点伤害，X＝你墓地中角色牌的数量；X=0 时不发动。",
            });
        }
    }
}
