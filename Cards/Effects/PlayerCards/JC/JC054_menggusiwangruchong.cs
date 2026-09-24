using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC054 蒙古死亡蠕虫 效果（角色）。
    /// 卡文：潜伏强制触发：蒙古死亡蠕虫对所有位于本地区的其他角色造成1点伤害。
    ///      结束步骤强制触发：潜伏。（将该角色翻面为暗藏者）
    /// 实现：
    ///  · **结束步骤强制触发：潜伏** ＝ `OnTurnEnded` + `Forced=true` + `Latent`（翻面为暗藏者）；
    /// </summary>
    public static class JC054_menggusiwangruchong_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC054_menggusiwangruchong", Build);

        private static void Build(PlayerCardData data)
        {
            // 潜伏强制触发：对本地区所有**其他**角色各 1 点伤害
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC054_1_DamageAllOthersInAreaOnLatent",
                Timing = EffectTiming.OnLatent,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = false,             // 群体：不需要玩家选单个目标
                    MassMode = MassMode.AllInArea,      // 集合＝本次触发所在地区（`SourceAreaAtTrigger`）
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "1" } },
                },
                Forced = true,        // "潜伏**强制**触发"
                // ⚠ 本时机**必然在暗藏态**下成立（"被潜伏"就是翻为暗藏者）→ 必须显式声明 CovertOnly，
                // 否则会被默认的 FaceUp（仅正面可触发）拦掉。
                TriggerCovertMode = CovertTriggerMode.CovertOnly,
                Duration = "瞬时",
                FaqNote = "被潜伏（翻面为暗藏者）时，对本地区所有其他角色各造成 1 点伤害；强制触发。",
            });

            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC054_2_EndStepLatent",
                Timing = EffectTiming.OnTurnEnded,
                Condition = new EffectCondition { Predicate = "Always" },
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Latent },   // 潜伏：翻面为暗藏者（目标缺省=自身）
                },
                Forced = true,        // "强制触发"：触发窗口不可放弃
                Duration = "瞬时",
                FaqNote = "结束步骤强制潜伏（翻面为暗藏者）；只有已在场时才生效（离场护栏）。",
            });
        }
    }
}
