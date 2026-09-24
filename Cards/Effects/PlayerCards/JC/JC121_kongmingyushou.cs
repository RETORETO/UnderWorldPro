using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC121 空鸣御守 效果（附属 / 公开 / 灵体）。
    /// 卡文（用户 2026-09 澄清：伤害那一条是**持续效果**，不是触发）：
    ///      进场触发：将空鸣御守结附于位于本地区的目标本方角色。
    ///      每当受此结附的角色将受到伤害时，该伤害改为对空鸣御守造成。
    ///
    /// 本轮交付**第二条**（**K3·持续型伤害重定向**）：
    ///  · `EffectData.DamageRedirect = DamageRedirectVictim.AttachedHost` + `Timing = Continuous`（前提常驻）：
    ///    本卡**处于结附状态**期间，其**结附的宿主**将受到的伤害**改由本卡承受**；
    ///  · 消费点＝`CardManager.DealDamageToInstance` → `EffectEngine.ResolveDamageRedirect`（一次伤害只重定向一次）；
    ///  · 前缀"灵体"由既有词条通道承担；"不能移动"前缀见进度表该行（人工归类）。
    /// **第一条（进场触发：自行选结附目标）仍未实现**——需"附属进场时选结附目标"的路径（见进度表该行）。
    /// </summary>
    public static class JC121_kongmingyushou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC121_kongmingyushou", Build);

        private static void Build(PlayerCardData data)
        {
            // 灵体（关键词：与 JC111 同款"持续关键字授予"，引擎级 `DamageRules.IsSpirit` 消费）。
            // 附属卡：Scope 用 AttachTarget（与"受此结附的…"同区），保障结附期间生效。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC121_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC121_B_RedirectHostDamageToSelf",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,          // 附属：结附于角色时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "受此结附的角色将受到的伤害改由本卡承受",
                DamageRedirect = new DamageRedirectSpec
                {
                    Victim = DamageRedirectVictim.AttachedHost,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "结附期间：宿主将受到的伤害改为对空鸣御守造成；伤害落到它身上后再判它的『灵体』防止。",
            });
        }
    }
}
