using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC081 亚洛尔 效果（角色）。
    /// 卡文：**灵体** 除装备与祝福外不受附属 **额外费用：展示任意数量的手牌，你每展示一张【圣贤】牌
    ///       或者具有【圣】图标的牌，亚洛尔的所需费用就减少1。**
    ///
    /// ①【前缀】灵体：`Continuous{Keyword=灵体}`（引擎级 `DamageRules.IsSpirit` 消费：对手在本地区魔法领域图标
    ///    更少时**防止该次伤害**）。与 JC077/JC111 同款"持续关键字授予"通道。
    ///
    /// ②【K10·额外费用】"展示任意数量手牌减费"（用户 2026-09 口径 **A22/A23/A33/A35**）：
    ///    · `PlayerCardData.ExtraCost = { Kind = RevealHandCards, Mandatory = **false**,
    ///      RevealFilter = "组织:圣贤|域:神圣" }` —— 这是**费用减免**：
    ///      **可以不展示**（＝没有减免，按全额费用照样能打出）；
    ///    · 【圣贤】牌 ＝ **组织:圣贤**；具有【圣】图标 ＝ **神圣魔法领域图标**（`MagicDomain.Divine`；
    ///      卡库单字写法：心=心灵 / 圣=神圣 / 星=星空 / 死=死亡 / 血=鲜血）；
    ///    · 每展示一张**合格**牌 → 本张费用 -1；减免**一次性**（只作用于这一张）；
    ///    · 交互（打出时）：手牌选择框（可多选）→ **确认**＝给对手展示所有高亮手牌 → 减免 → 入堆叠；
    ///      **取消**＝不打出、卡不入堆叠、**不扣任何费用**（A23）；
    ///    · 时机＝**付费阶段、卡入堆叠之前**（与其它额外费用一致，**不可被响应**）。
    ///
    /// ③【前缀】「除装备与祝福外不受附属」（用户 2026-09 口径：**与 JC001 类似实现即可**）：
    ///    `Timing = Static` + `StaticEffects{ Kind = OnlyAttachWithSubCategories, Param = "装备,祝福" }`
    ///    → **本卡只受【装备】或【祝福】类附属结附**；判定单一实现
    ///    `EffectEngine.GetAttachSubCategoryWhitelist` / `PassesAttachWhitelist`，
    ///    消费点＝打出阶段 `CardManager.IsLegalPlayTarget`（拦截）＋ 结算兜底（`TurnManager` 结附分支）。
    /// </summary>
    public static class JC081_yaluoer_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC081_yaluoer", Build);

        private static void Build(PlayerCardData data)
        {
            // ① 灵体（与 JC077/JC111 同款：持续授予关键字，引擎级伤害防止消费）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC081_A_Spirit",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AreaZone,
                ContinuousPrereq = "常驻",
                ContinuousEffect = "灵体",
                ContinuousGrants = new Array<EffectGrant>
                {
                    new EffectGrant { Kind = "Keyword", Value = "灵体", Amount = 1 },
                },
            });

            // ③【前缀】除装备与祝福外不受附属（静止规则：子类别白名单；与 JC001 同类实现）
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC081_C_OnlyEquipAndBlessingAttachments",
                Timing = EffectTiming.Static,
                Scope = ZoneScope.AreaZone,                // 在场上（争夺区）常驻生效
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.OnlyAttachWithSubCategories, Param = "装备,祝福" },
                },
                Duration = "常驻",
                FaqNote = "本卡只受【装备】与【祝福】类附属结附；其它附属不能以它为目标（打出与结算两处同解）。",
            });

            // ②【K10·额外费用】展示任意数量手牌 → 每张合格牌费用 -1（**可选**：不展示＝全额支付）
            data.ExtraCost = new ExtraCostSpec
            {
                Kind = ExtraCostKind.RevealHandCards,
                Mandatory = false,                          // 可以不展示（A22）
                RevealFilter = "组织:圣贤|域:神圣",          // 【圣贤】牌 或 具【圣】图标（＝神圣领域）之牌
            };
            data.ExtraCostDescription = "展示任意数量的手牌，每展示一张【圣贤】牌或具有【圣】图标的牌，所需费用减少1";
            data.IsExtraCostMandatory = false;
        }
    }
}
