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
    ///  · 消费点＝`TurnManager.ResolveCurrentAreaCombat`：该地区在本步骤**不结算调查对抗**
    ///    （不产生胜负、不加势力标志、不开牌库顶分配窗口），**但仍给出让过窗口**
    ///    （与"无对抗管理器"的降级路径同款，保持"每个步骤都有优先权"的节奏不变）。
    ///  · 判定按卡**当前**所在地实时匹配地区（`EffectEngine.AreaSkipsCombat`），故卡在争夺区之间移动时自动跟随。
    ///
    /// 前缀"公开"由既有词条通道承担（`HasPublicKeyword`），本脚本不再重复声明。
    /// </summary>
    public static class JC098_xianrufengkuangderenqun_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC098_xianrufengkuangderenqun", Build);

        private static void Build(PlayerCardData data)
        {
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
                FaqNote = "只要本卡在争夺区，其所在地区在对抗阶段的『调查步骤』不结算调查对抗（仍给优先权窗口，流程照常推进）。",
            });
        }
    }
}
