using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC098 陷入疯狂的人群 效果（角色 / 人类 / 梦境行者；前缀"公开"）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者）。持续：本地区略过调查对抗步骤。
    ///
    /// 本轮交付第二条（**K18 第一批：地区级"略过对抗"**）：
    ///  · `EffectData.StaticEffects = [StaticEffect{Kind = SkipAreaCombat, Param = "调查"}]` +
    ///    `Timing = Continuous` + `现提＝常驻`：卡在争夺区期间由 `RegisterPersistent` 登记，
    ///    离场/前提丧失时注销（`UnregisterPersistent` / `ReevaluatePrereqs`）。
    ///  · 消费点＝`TurnManager.NextCombatStepOfArea`（经 `AdvancePhase`）：该地区在本步骤**直接不存在**——
    ///    不进入该阶段、不分配行动权、不开让过窗口、不在 UI 上停留
    ///    （用户 2026-10 口径："跳过调查对抗步骤，不是调查对抗这件事，相当于流程里直接没有这个步骤"）；
    ///    例：本卡在 1 地区 ⇒ 机动轮后直接进 1 地区战斗步骤；在 2 地区 ⇒ 1 地区势力步骤后直接进 2 地区战斗步骤。
    ///  · 判定按卡**当前**所在地实时匹配地区（`EffectEngine.AreaSkipsCombat`），故卡在争夺区之间移动时自动跟随。
    ///
    /// 【2026-09 整体复查·修复】前缀"公开"**必须显式装载**（`KeywordEffects.Public()`）：旧注释写的
    /// "由既有词条通道承担（`HasPublicKeyword`），本脚本不再重复声明"是**错的**——`HasInherentKeyword`
    /// 只读 `EffectDataList`，没有按卡面文本自动装载关键字的机制 ⇒ 不声明则"不能被秘密派遣为暗藏者"门禁失效。
    /// </summary>
    public static class JC098_xianrufengkuangderenqun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC098_xianrufengkuangderenqun", Build);

        private static void Build(PlayerCardData data)
        {
            var publicKw = KeywordEffects.Public();
            publicKw.EffectId = "JC098_0_Public";
            data.EffectDataList.Add(publicKw);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC098_A_SkipInvestigationStep",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,          // 角色在争夺区时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "本地区略过调查对抗步骤",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.SkipAreaCombat, Param = "调查" },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "只要本卡在争夺区，其所在地区在对抗阶段**没有『调查步骤』这一步**（流程直接跳过，"
                        + "不进入该步骤、不给该步骤的优先权窗口）；例：1 地区有本卡 ⇒ 机动轮后直接进 1 地区战斗步骤。",
            });
        }
    }
}
