using Script.Data.Cards;
using Script.Data.Effects;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC070 虔心修士 效果（角色 / 人类·僧侣 / 圣贤；前缀"公开""声望"）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者） 声望（当一个地区的所有对抗结束后，在该地区上若你比对手
    ///      操控更多未横置的声望角色，则你可以在该地区额外放置一个本方势力标志。）
    ///
    /// 【2026-09 整体复查·补实现】本卡此前**没有任何效果脚本**（`人工检查进度.md` 里该卡两行写着
    /// "✅ 引擎已支持该词条"，但"引擎支持该词条"≠"本卡已声明该词条"⇒ 实际两个前缀都没有装载，
    /// 与 JC059/JC079/JC080/JC098 的"公开漏装载"同一类缺陷）。本脚本按同款口径补齐：
    ///  · **公开** ＝ `KeywordEffects.Public()`（手牌区常驻关键字 ⇒ `HasPublicKeyword` 为真 ⇒
    ///    `DeployCovertEligibility` 拒绝把本卡秘密派遣为暗藏者）；
    ///  · **声望** ＝ `RenownKeyword()`（关键字，供 `RenownRule.CountRenownCharacters` 计数与其他
    ///    "声望角色"规则）+ `Renown()`（**规则本体＝触发效果**：**每次势力对抗结算完毕**触发，
    ///    该地区声望角色较多的一方在其地区放置 1 个本方势力标志；不依赖对抗胜负，多个声望角色也只放 1 个）。
    /// 卡数据（费用 1 / 颜色忠诚 2:2 / 先手调查 1 / 防御 1 等）与卡文一致，无需改动 `.tres`。
    /// </summary>
    public static class JC070_qianxinxiushi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC070_qianxinxiushi", Build);

        private static void Build(PlayerCardData data)
        {
            var publicKw = KeywordEffects.Public();
            publicKw.EffectId = "JC070_0_Public";
            data.EffectDataList.Add(publicKw);

            var renown = KeywordEffects.RenownKeyword();
            renown.EffectId = "JC070_0_Renown";
            data.EffectDataList.Add(renown);

            var renownTrigger = KeywordEffects.Renown();
            renownTrigger.EffectId = "JC070_0_RenownTrigger";
            data.EffectDataList.Add(renownTrigger);
        }
    }
}
