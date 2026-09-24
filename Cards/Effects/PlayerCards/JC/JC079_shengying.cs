using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC079 圣婴 效果（角色 / 人类·圣者 / 圣贤；前缀"公开"、"1分值"）。
    /// 卡文：公开（该角色不能被秘密派遣为暗藏者）1分值 触发：当你赢取本地区时，将圣婴置入你的计分区，
    ///      而非置入其拥有者的牌库底。
    ///
    /// 本轮交付（**K3 去处替代第一批**）：
    ///  · `EffectData.WinAreaDestination = "计分区"` + `Condition.Predicate = "WinnerIsSelfInSameArea"`
    ///    （＝"**你**赢取**本地区**时"，与 `OnAreaWon` 触发路径**同一谓词实现**）；
    ///  · 消费点＝`AreaManager.ResolveAreaWon` 的"送牌库底"扫描（`MoveToScoreIfReplaced`）：条件成立 →
    ///    就地改判为**赢取者的计分区**、跳过"送拥有者牌库底"（与『撤回』同一实现位）。
    ///  · 为什么走**替代效果**而不是入堆叠的触发：新地区赢取结算里"本地区卡送库底"是同步跑完的，
    ///    而入堆叠触发是异步结算的（`AreaManager` 的既有注释已注明该类效果"仍属替代效果(K3)范畴"）——
    ///    若按触发实现，结算时圣婴已经躺在牌库里，改判去处无从谈起。
    ///  · 数据修正：该卡文写"**1分值**"，但卡数据与导出 JSON 里 `Score` 都是 0 → 已把
    ///    `JC079_shengying.tres` 的 `Score` 置为 1（否则"置入计分区"不产生任何分数，效果无意义）。
    ///
    /// 前缀"公开"由既有词条通道承担（`HasPublicKeyword`）。
    /// </summary>
    public static class JC079_shengying_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC079_shengying", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC079_A_WinAreaToScorePile",
                Timing = EffectTiming.Static,              // 静止替代规则（不入堆叠；见类注释）
                Scope = ZoneScope.AreaZone,                // 角色在争夺区时适用
                Condition = new EffectCondition { Predicate = "WinnerIsSelfInSameArea" },
                WinAreaDestination = "计分区",              // 去处替代：送库底 → 赢取者计分区
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                Duration = "常驻",
                FaqNote = "本方赢取圣婴所在地区时，圣婴置入赢取者的计分区（1 分值即时计入总分），而非送回拥有者牌库底；"
                        + "敌方赢取该地区时照常回拥有者牌库底。",
            });
        }
    }
}
