using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC133 操控死尸 效果（事务卡）。
    /// 卡文：行动：选择目标地区，令你墓地中的目标角色牌正面进场并置于该地区，该角色额外具有不死生物类别。
    /// FAQ：当玩家使用操控死尸的能力选择一个带有混乱关键词的角色时，需要随机选择进入的地区，而非直接进入操控死尸选择的地区。
    ///（混乱关键词按用户口径**不在 JC 范围内**，此处不实现；已在进度表该行注明。）
    ///
    /// 实现（P3·C：**K15 多目标** + `PutToArea` 动词）：
    ///  · 多目标槽位：[目标地区（必选）] + [你墓地中的角色牌（必选）]；
    ///  · `PutToArea{"正面","不死生物"}`：正面进场到所选地区（照常广播进场触发），
    ///    并**额外授予**"不死生物"子类别（用户 2026-09 口径：**叠加**，不替换原类别）。
    /// </summary>
    public static class JC133_caokongsishi_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC133_caokongsishi", Build);

        private static void Build(PlayerCardData data)
        {
            data.LimitToActionPhaseOnly = true;   // "行动"

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC133_1_ReviveGraveyardCharacterFaceUp",
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
                            Filter = "类别:角色",
                            Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },
                        },
                    },
                },
                MinChosenSlots = 1,
                Actions = new Array<EffectAction>
                {
                    // 朝向＝正面；附加子类别＝不死生物（叠加，不替换）
                    new EffectAction
                    {
                        Verb = ActionVerb.PutToArea,
                        Args = new Array<string> { "正面", "不死生物" },
                    },
                },
                Duration = "瞬时",
                FaqNote = "选一个地区 + 你墓地中的一张角色牌：该牌正面进入该地区并额外获得「不死生物」类别。",
            });
        }
    }
}
