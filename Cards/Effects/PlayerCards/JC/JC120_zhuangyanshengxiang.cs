using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC120 庄严圣像 效果（附属卡）。
    /// 卡文：结附于目标地区 触发：当你赢取本地区时，将庄严圣像置入你的资产区，令其成为一个资产。
    ///
    /// 实现（P3·G 区域转换 → **P3·K3 去处替代第二批**）：
    ///  · 打出目标＝**地区**（`TargetRules = Area`，结附于目标地区，落在附属区）；
    ///  · 去处替代 `WinAreaDestination = "资产区"` + `Condition.Predicate = "WinnerIsSelfInSameArea"`
    ///    （① 本卡控制者＝赢取方 ② 本卡所在地区＝被赢取地区）＝卡文"当你赢取**本地区**时 … 置入你的资产区"；
    ///  · 消费点＝`AreaManager.ResolveAreaWon` 的"本地区卡归位"扫描（`TryApplyWinAreaReplacement`，
    ///    覆盖**结附地区的附属卡**那一轮）：条件成立 → 就地改判为**赢取者的资产区**并**正面**置入、跳过"送拥有者牌库底"。
    ///  · 为什么走**替代效果**而非入堆叠的触发（与 JC079 同一结论）：赢取结算里本地区卡归位是**同步**跑完的，
    ///    而入堆叠触发是**异步**结算的——若按触发实现，触发结算时庄严圣像已经被"送牌库底"扫描带回牌库，
    ///    `MoveToZone{"自身","资产区"}` 因"本卡已不在场"而被跳过，卡文不成立（本卡的原触发实现即因此失效，
    ///    已由 `TestZoneConvert_JC120赢取生产路径` 走生产路径锁定）。
    /// </summary>
    public static class JC120_zhuangyanshengxiang_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC120_zhuangyanshengxiang", Build);

        private static void Build(PlayerCardData data)
        {
            // 打出目标＝地区（"结附于目标地区"）
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC120_A_WinAreaToAsset",
                Timing = EffectTiming.Static,              // 静止替代规则（不入堆叠；见类注释）
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                WinAreaDestination = "资产区",              // 去处替代：送库底 → 赢取者资产区（正面，成为资产）
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本方赢取庄严圣像所结附的地区时，庄严圣像正面置入赢取者的资产区并成为一个资产，"
                        + "而非送回拥有者牌库底；敌方赢取该地区时照常回拥有者牌库底。",
            });
        }
    }
}
