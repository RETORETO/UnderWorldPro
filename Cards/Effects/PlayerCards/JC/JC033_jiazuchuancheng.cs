using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC033 家族传承 效果（附属 / 仪式·吸血鬼，结附于本方秘社）。
    /// 卡文：结附于本方秘社 持续：所有本方暗藏者，付费翻面时其所需费用减少1。
    ///      行动1，牺牲家族传承：从你的牌库中寻找一张吸血鬼角色牌，将该牌展示后以暗藏者状态置于目标地区，然后洗牌。
    ///
    /// 本轮交付**第一条**（持续·现身费用减免）：
    ///  · `EffectData.ContinuousCostMod`（`Amount=1`（正值＝减费）、`Filter=""`（全匹配）、
    ///    `PerTurn=false`（整回合、不限张数）、**`CostScope="现身"`**）：
    ///    只作用于**暗藏者付费翻面（现身）**那条付费通道（`TurnManager.RevealCovert`），
    ///    与"打出"通道**互不串台**（打通用的费用修正不会顺带减免现身费，反之亦然）。
    ///    语义上"所有本方暗藏者"由**通道本身**保证：现身通道只服务本方暗藏者，故过滤器留空即等价。
    /// **第二条（行动1，牺牲自身：检索吸血鬼角色牌 → 展示 → 以暗藏者状态置于目标地区 → 洗牌）仍未实现**：
    ///  检索/展示/暗藏进场/洗牌原语均已具备，唯一缺口是"**激活能力的地区目标**在联机路径不支持"
    ///  （见进度表该行说明）。
    /// </summary>
    public static class JC033_jiazuchuancheng_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC033_jiazuchuancheng", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标：仅本方秘社
            data.TargetRules = new Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            // 持续：所有本方暗藏者付费翻面时费用 -1（作用于"现身"付费通道）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC033_A_RevealCostMinusOne",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,     // 附属：结附于秘社时生效
                ContinuousPrereq = "常驻",
                ContinuousEffect = "本方暗藏者付费翻面费用 -1",
                ContinuousCostMod = new ContinuousCostModSpec
                {
                    Amount = 1,          // 正值＝减费（实际费用 = 印刷费用 - mod）
                    Filter = "",         // 全匹配（现身通道只服务本方暗藏者 → 等价"所有本方暗藏者"）
                    PerTurn = false,     // 整回合，不限张数
                    CostScope = "现身",   // **只**减"付费翻面"的费用，不影响打出
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本卡在场时，本方暗藏者付费翻面的费用 -1（不限张数；不影响正常打出）。",
            });

            // 行动1，牺牲家族传承：从你的牌库中寻找一张吸血鬼角色牌，将该牌展示后以暗藏者状态置于**目标地区**，然后洗牌
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC033_B_SacrificeSearchVampireToTargetArea",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "行动",                       // 卡文"行动1"
                Cost = 1,
                ActivateCondition = "牺牲本角色",               // "牺牲家族传承"（本附属自身）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                NeedsAreaTarget = true,                        // 【K15】落点＝"目标地区"（入堆叠时锁定）
                Actions = new Array<EffectAction>
                {
                    // 检索（`附加` 含"展示"→ 公开检视该牌）→ 以**暗藏者**状态进场 → 检索后必定洗牌
                    new EffectAction
                    {
                        Verb = ActionVerb.Search,
                        Args = new Array<string> { "子类别:吸血鬼;类别:角色", "暗藏进场", "展示" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "牺牲家族传承：从牌库找一张吸血鬼角色牌，展示后以暗藏者状态置于**选定地区**，然后洗牌。",
            });
        }
    }
}
