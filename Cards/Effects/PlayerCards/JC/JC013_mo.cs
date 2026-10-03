using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC013 貘 效果（角色，貘；灵体、不能被装备结附）。
    /// 卡文：灵体 不能被装备结附 行动，牺牲貘：横置目标角色或附属，然后消灭该角色或附属上封印的所有牌。
    ///
    /// 实现：
    ///  · **灵体** ＝ 引擎级（伤害防止：被分配伤害时比较该地区双方魔法领域图标数，对手更少则防止）；
    ///  · **不能被装备结附** ＝ `KeywordEffects.CannotBeAttached()`；
    ///  · **行动，牺牲貘** ＝ `ActivateCondition = "牺牲本角色"`（引擎在入堆叠前牺牲自身为代价）+ `ActivateWindow="行动"`；
    ///  · **目标角色或附属** ＝ `Kind=Character` + `KindAlt=Attachment`（K14 或种类）；
    ///  · 动作链 ＝ `Tap{目标}`（横置）→ `DestroyOnTarget{封印牌}`（消灭**该卡上封印的所有牌**，K2 扩展）。
    /// </summary>
    public static class JC013_mo_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC013_mo", Build);

        private static void Build(PlayerCardData data)
        {
            // 【2026-09 修复·灵体同类排查】卡文以**灵体**开头，但脚本此前没有授予【灵体】关键词
            // ⇒ `DamageRules.IsSpirit` 为假 ⇒ 灵体的"我方魔法领域图标更多则防止伤害"永不生效。
            // 现按 JC077/JC111 同款"持续关键字授予"补齐（消费点＝`CardManager.DamagePreventer`）。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC013_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });
            var cannotAttach = KeywordEffects.CannotBeAttached();
            cannotAttach.EffectId = "JC013_B_CannotBeAttached";
            data.EffectDataList.Add(cannotAttach);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC013_1_TapThenDestroySealed",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",              // 卡文"行动"（限自己的行动步骤）
                ActivateCondition = "牺牲本角色",      // "牺牲貘"＝发动代价（入堆叠前）
                Cost = 0,
                Forced = false,
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    KindAlt = TargetKind.Attachment,  // "目标角色**或附属**"（K14 或种类）
                    NeedsSelection = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },                                     // ① 横置目标
                    new EffectAction { Verb = ActionVerb.DestroyOnTarget,
                        Args = new Array<string> { "封印牌", "任意" } },                             // ② 消灭其上封印的所有牌
                },
                Duration = "瞬时",
                FaqNote = "牺牲貘：横置目标角色或附属，然后消灭该卡上封印的所有牌。",
            });
        }
    }
}
