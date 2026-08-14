using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC035 川美魔咒 效果（自原 JC035_chuanmeimozhou 构造函数迁移，逻辑不变）。
    /// 附属，结附于地区。准备步骤触发：若对手手牌 ≤ 3，可在本附属结附的地区放置一个本方势力标志。
    /// </summary>
    public static class JC035_chuanmeimozhou_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC035_chuanmeimozhou", Build);

        private static void Build(PlayerCardData data)
        {
            // ---- 打出目标：仅地区（附属通用目标系统，声明式） ----
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Area),
            };

            // ---- 准备步骤触发：放置势力标志 ----
            var e = new EffectData
            {
                EffectId = "JC035_Prepare_PlacePowerMark",
                Timing = EffectTiming.OnTurnStarted,
                Forced = false,
                Scope = ZoneScope.AttachTarget,
                Target = new TargetSpec { NeedsSelection = false },
            };

            e.Condition = new EffectCondition
            {
                Predicate = "OpponentHandLe3",
            };

            e.Actions.Add(new EffectAction
            {
                Verb = ActionVerb.PlaceToken,
                Args = new Godot.Collections.Array<string> { "势力标志", "1" },
            });

            data.EffectDataList.Add(e);
        }
    }
}
