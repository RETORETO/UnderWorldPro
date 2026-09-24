using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC077 庇佑圣灵 效果（角色 / 灵体）。
    /// 卡文（用户 2026-09 澄清：**这一条是「持续效果」**，卡库里的"触发："写法属效果数据错误）：
    ///      每当一个位于本地区、且非灵体的本方角色将受到伤害时，该伤害改为对庇佑圣灵造成。
    ///
    /// 实现（**K3·持续型伤害重定向**）：
    ///  · `EffectData.DamageRedirect = DamageRedirectVictim.FriendlyNonSpiritLocal` + `Timing = Continuous`（前提常驻）：
    ///    本卡在**争夺区**期间，本地区内由**同一控制者**控制的**非灵体角色**将受到的伤害**改由本卡承受**；
    ///  · 消费点＝`CardManager.DealDamageToInstance`（伤害唯一收口）→ `EffectEngine.ResolveDamageRedirect`；
    ///  · **一次伤害只重定向一次**（防乒乓），但本卡自身的"**灵体**防止"仍在其落地时照常判定
    ///    —— 这正是这张卡的强度来源（本卡是灵体：若对手魔法领域图标更少，本次伤害会被直接防止）。
    ///  · 前缀"灵体"由既有词条通道承担（`KeywordEffects.Spirit()`）；"不能移动"等其它前缀见进度表各行。
    /// </summary>
    public static class JC077_biyoushengling_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC077_biyoushengling", Build);

        private static void Build(PlayerCardData data)
        {
            // 灵体（关键词：与 JC111 同款"持续关键字授予"，引擎级 `DamageRules.IsSpirit` 消费）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC077_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC077_B_RedirectFriendlyDamageToSelf",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,              // 本卡在争夺区时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "本地区非灵体本方角色将受到的伤害改由本卡承受",
                DamageRedirect = new DamageRedirectSpec
                {
                    Victim = DamageRedirectVictim.FriendlyNonSpiritLocal,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只要庇佑圣灵在争夺区：本地区内（同一控制者）非灵体角色将受到的伤害改为对庇佑圣灵造成；"
                        + "伤害落到它身上后再判它的『灵体』防止。",
            });
        }
    }
}
