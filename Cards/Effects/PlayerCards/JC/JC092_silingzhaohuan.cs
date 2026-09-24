using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC092 死灵召唤 效果（事务卡）。
    /// 卡文：行动：选择一个地区，将你墓地中的目标角色牌以暗藏者状态置于该地区。
    /// FAQ：死灵召唤的行动能力可以指定墓地中带公开关键词的角色。
    ///
    /// 实现（P3·C：**K15 多目标** + `PutToArea` 动词）：
    ///  · 多目标槽位（用户 2026-09 口径）：[目标地区（必选）] + [你墓地中的角色牌（必选）]；
    ///  · `PutToArea{"暗藏"}`：把所选墓地角色牌**先翻面为暗藏者、再进场**到所选地区
    ///    （与"派遣暗藏者"、"检索→暗藏进场"同序），**照常广播进场触发**（用户 2026-09 口径）；
    ///  · FAQ「可指定带公开关键词的角色」：公开只限制"派遣暗藏/正面打出"，不影响把墓地牌置入地区。
    /// </summary>
    public static class JC092_silingzhaohuan_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC092_silingzhaohuan", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;   // "行动"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC092_1_ReviveGraveyardCharacterCovert",
                Timing = EffectTiming.OnSorceryCast,
                PlayTiming = PlayTiming.MainPhase,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false },
                TargetSlots = new Array<TargetSlot>
                {
                    new TargetSlot
                    {
                        Label = "目标地区",
                        Optional = false,
                        Spec = new TargetSpec { Kind = TargetKind.Area, NeedsSelection = true },
                    },
                    new TargetSlot
                    {
                        Label = "你墓地中的角色牌",
                        Optional = false,
                        Spec = new TargetSpec
                        {
                            Kind = TargetKind.Graveyard,
                            NeedsSelection = true,
                            Filter = "类别:角色",     // 检索筛选语法：只要角色牌
                            Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                        },
                    },
                },
                MinChosenSlots = 1,
                Actions = new Array<EffectAction>
                {
                    // 朝向＝暗藏（先翻面再进场）；无附加子类别
                    new EffectAction { Verb = ActionVerb.PutToArea, Args = new Array<string> { "暗藏" } },
                },
                Duration = "瞬时",
                FaqNote = "选一个地区 + 你墓地中的一张角色牌，该牌以暗藏者状态进入该地区（照常触发进场）。",
            });
        }
    }
}
