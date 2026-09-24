using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC038 列恩·爱弗罗德 效果（角色）。
    /// 卡文：领袖 持续：所有其他本方【王座会】角色获得袭击1。
    ///      行动，牺牲一个吸血鬼或奴仆角色：令目标对手弃一张手牌，然后你抓一张牌。
    ///
    /// 实现（效果级推进）：
    ///  · **领袖**：持续关键字 + `LeaderRule` 唯一性规则；
    ///  · **持续：所有其他本方【王座会】角色获得袭击1**（P3·E 光环组织筛选 + K13 袭击叠加）：
    ///    光环 `IsAura` + `AuraFilterOrganization="王座会"`（范围＝来源控制者的**全场**争夺区角色，用户 2026-09 口径），
    ///    授予 `Attribute:"袭击" ×1`。收集阶段把**卡面自带袭击**与该授予**合并为一个触发**，
    ///    伤害＝两者之和（用户口径："已有袭击1 被授予袭击1 → 袭击2"）——见 `TriggerRegistry` 的袭击合成。
    ///  · **行动，牺牲一个吸血鬼或奴仆角色：令目标对手弃一张手牌，然后你抓一张牌**（本轮交付）：
    ///    代价＝`SacrificeCostSpec`（子类别白名单＝`吸血鬼`/`奴仆`；卡文未写"另一个"→ 按口径**可牺牲来源自身**；
    ///    不可牺牲暗藏者）；目标＝**敌方玩家**（K24 玩家目标，入堆叠时锁定）；
    ///    动作＝`Discard{1, "目标玩家", "自选"}`（由**被指定者自己选**弃哪张，§7.4 口径①）+ `Draw{1}`。
    /// </summary>
    public static class JC038_lieenaifuluode_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC038_lieenaifuluode", Build);

        private static void Build(PlayerCardData data)
        {
            var leader = KeywordEffects.Leader();
            leader.EffectId = "JC038_A_Leader";
            data.EffectDataList.Add(leader);

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC038_B_AuraStrike1ToCourt",
                Timing = EffectTiming.Continuous,
                ContinuousPrereq = "常驻",
                IsAura = true,
                AuraFilterOrganization = "王座会",   // 【P3·E】按组织筛选
                ContinuousEffect = "所有其他本方【王座会】角色获得袭击1",
                ContinuousGrants = new Array<EffectGrant>
                {
                    // 【K13】"袭击"作为属性授予：收集阶段与卡面自带袭击**叠加合并**为一个触发
                    new EffectGrant { Kind = "Attribute", Value = "袭击", Amount = 1 },
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "同一控制者的全部【王座会】角色（除自身外）获得袭击1；若其本来就有袭击N，则合并为袭击N+1。",
            });

            // 行动，牺牲一个吸血鬼或奴仆角色：令目标对手弃一张手牌，然后你抓一张牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC038_C_SacrificeVampireOrServant",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "行动",                       // 卡文"行动"（无费用数字 → Cost 0）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                SacrificeCost = new SacrificeCostSpec
                {
                    SubCategories = new Array<string> { "吸血鬼", "奴仆" },   // "吸血鬼或奴仆角色"
                    ExcludeSource = false,                    // 卡文未写"另一个" → 可牺牲来源自身（用户口径）
                    AllowCovert = false,
                },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Player,
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Enemy },   // "目标对手"
                },
                Actions = new Array<EffectAction>
                {
                    // 由**被指定者（目标玩家）自己选**弃哪张（§7.4 口径①：令目标玩家弃牌＝被指定者自选）
                    new EffectAction { Verb = ActionVerb.Discard, Args = new Array<string> { "1", "目标玩家", "自选" } },
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "行动：牺牲一个吸血鬼或奴仆角色（发动者自选）→ 目标对手弃一张手牌（由他自己选弃哪张），然后你抓一张牌。",
            });
        }
    }
}
