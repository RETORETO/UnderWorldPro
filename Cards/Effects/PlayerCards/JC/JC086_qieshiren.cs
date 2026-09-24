using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC086 窃尸人 效果（角色）。
    /// 卡文：进场触发：将你墓地中的目标角色牌置于你的手中。
    ///
    /// 实现（P3·K14 目标种类扩展）：
    ///  · 目标＝**墓地里的角色牌**：`TargetSpec{Kind=Graveyard, Filter="类别:角色", Restrictions=[Friendly]}`
    ///    ——卡文写"**你**墓地"，故用 `Friendly` 限定本方（用户口径：也允许指定对手的墓地，由卡面限制决定）；
    ///  · 候选池与合法性由 `TargetZonePool` 统一实现（`类别:角色` 复用 `DeckSearchFilter` 语法）；
    ///  · 丢失判定复用既有版本号逻辑（墓地↔其他区域移动会 `BumpVersion`，结算时比对入栈版本号即判丢失）；
    ///  · 选择窗口：这些牌没有场上 CardUI，故单机走 `IGameUIHooks.PromptPickCard`、联机走
    ///    `ChoiceRequest.Kind.SelectZoneCard`（见 `TurnManager.EnsureTargetThenPush`）。
    /// </summary>
    public static class JC086_qieshiren_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC086_qieshiren", Build);

        private static void Build(PlayerCardData data)
        {
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC086_1_GraveyardToHand",
                Timing = EffectTiming.EnterTrigger,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Graveyard,
                    Filter = "类别:角色",           // 文本筛选：只取"角色牌"（复用检索筛选语法）
                    NeedsSelection = true,
                    Restrictions = new Array<TargetRestriction> { TargetRestriction.Friendly },   // "你墓地"
                },
                Actions = new Array<EffectAction>
                {
                    // 墓地 → 拥有者手牌（Bounce = 移回拥有者手牌）
                    new EffectAction { Verb = ActionVerb.Bounce },
                },
                Duration = "瞬时",
                FaqNote = "进场触发：把本方墓地中的一张角色牌移回其拥有者手牌（目标在结算前离开墓地即判丢失）。",
            });
        }
    }
}
