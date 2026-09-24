using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC096 阿格里帕之犬 效果（角色 / 狗·恶魔）。
    /// 卡文：快速行动1，横置，牺牲一个角色：抓一张牌。
    ///
    /// 实现（**K22 牺牲代价**）：
    ///  · `Cost = 1`（"快速行动**1**"）+ `ActivateCondition = "横置本角色"`（"横置"）+ 快速窗口；
    ///  · 代价＝`SacrificeCostSpec`：卡文写"牺牲**一个**角色"（**未写"另一个"**）→ 按用户 2026-09 口径
    ///    **可以牺牲来源卡自身**（`ExcludeSource = false`）；不限地区；不可牺牲暗藏者；
    ///    由发动者自己选择（UI 选择窗；无 UI/未指定 → 确定性兜底第一个合法候选）；
    ///  · 主效果＝`Draw{"1"}`（抓一张牌）。
    ///  · 代价在**入堆叠之前**支付：即使牺牲的是来源自身，主效果（抓牌）仍以"已捕获的来源/控制者"结算
    ///    （与既有"牺牲本角色"代价同一语义）。
    /// </summary>
    public static class JC096_agelipazhiquan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC096_agelipazhiquan", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC096_1_SacrificeThenDraw",
                Timing = EffectTiming.CharacterActivated,
                ActivateWindow = "快速",
                Cost = 1,                                   // 快速行动1
                ActivateCondition = "横置本角色",             // 横置（代价）
                Forced = false,
                Condition = new EffectCondition { Predicate = "Always" },
                SacrificeCost = new SacrificeCostSpec
                {
                    ExcludeSource = false,                  // 口径：未写"另一个" → 允许牺牲来源自身
                    AllowCovert = false,
                },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Draw, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "快速行动1 + 横置自身 + 牺牲一个角色（发动者自选；未写『另一个』故可牺牲自身）→ 抓一张牌。",
            });
        }
    }
}
