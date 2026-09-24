using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC116 柯尔特巨蟒左轮 效果（附属卡）。
    /// 卡文：撤回（当所在的地区被赢取时，将该牌移回其拥有者手上，而非置于牌库底。）持续：受此结附的角色获得［黑刀］。
    /// 实现（效果级推进）：
    ///   · **撤回** → `KeywordEffects.Withdraw()`；`AreaManager.ResolveAreaWon` 会先把"带撤回的附属卡"
    ///     从宿主连带里摘出来回手，避免它被宿主一起送回牌库底（FAQ：撤回只属于左轮自己，不惠及结附的角色）；
    ///   · **持续：受此结附的角色获得［黑刀］** → `GrantToHost=true`（P3·D：授予落点＝**宿主**）；
    ///     未结附于卡（无宿主）时不授予。
    /// </summary>
    public static class JC116_keertejumangzuolun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC116_keertejumangzuolun", Build);

        private static void Build(PlayerCardData data)
        {
            var withdraw = KeywordEffects.Withdraw();
            withdraw.EffectId = "JC116_A_Withdraw";
            data.EffectDataList.Add(withdraw);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC116_B_GrantBlackBladeToHost",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                GrantToHost = true,          // 授予**结附的角色**
                ContinuousEffect = "受此结附的角色获得［黑刀］",
                ContinuousGrants = new Array<EffectGrant>
                {
                    // 【修正·2026-09】原先只授予 `Icon`（只有图标、**不改战力**）→ 宿主显示有黑刀却不多打 1 点。
                    // 现同时授予 `Attribute:战斗`（黑刀=+1 战力），两条通道保持一致。
                    new EffectGrant { Kind = "Icon", Value = "黑刀", Amount = 1 },
                    new EffectGrant { Kind = "Attribute", Value = "战斗", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间宿主获得黑刀；附属离场/被消灭时撤销。",
            });
        }
    }
}
