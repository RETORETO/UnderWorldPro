using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC020 隐蔽的飞刀 效果（附属卡，结附于正面角色）。
    /// 首次制作"结附于角色"的附属卡，走规范结附逻辑（ResolveEntry 结附角色→MoveToAttachedZone，
    /// CurrentZone=AttachedZone，ZoneDetailId=宿主角色所在地区）。
    /// 两个激活效果均为快速行动、横置本附属为代价，且均为【本地区限定】（本地区=JC020 结附的角色所处地区）：
    ///   效果A：横置本飞刀 → 横置本地区的一个目标暗藏者；
    ///   效果B：横置并牺牲本飞刀 → 对本地区的一个目标角色造成 1 点伤害。
    /// </summary>
    public static class JC020_yinbidefeidao_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC020_yinbidefeidao", Build);

        private static void Build(PlayerCardData data)
        {
            // ---- 打出目标：结附于正面角色 ----
            // 最简 Make(Character)（无 Restrictions）：Kind=Character 自动排除暗藏者（暗藏者→Covert≠Character）。
            // 不用额外 Restrictions，避免 TargetRules 分支把打出规则精筛复用污染到激活目标（设计隐患见需求实现对照）。
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Character),
            };

            // —— 效果A：快速行动，横置本飞刀 → 横置本地区的一个目标暗藏者 ——
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC020_1_TapCovert",
                Timing = EffectTiming.AttachmentActivated,
                // 生效区：EffectData.Scope 默认 Specified → IsEffectActiveInZone 走 GetDefaultScope（附属→AttachTarget），
                // 附属卡未声明 Scope 默认在附属区生效（D46 根治），无需手动声明。
                ActivateWindow = "快速",
                ActivateCondition = "横置本角色",
                Cost = 0,
                Forced = false,
                // 仅暗藏者（Kind=Covert）+ 本地区限定（IsLocalZone，基准=宿主角色所在地区）
                Target = new TargetSpec
                {
                    Kind = TargetKind.Covert,
                    NeedsSelection = true,
                    IsLocalZone = true,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.Tap },
                },
                Duration = "瞬时",
                FaqNote = "横置本飞刀，横置本地区的一个目标暗藏者；本地区指飞刀所结附角色所处的地区"
            });

            // —— 效果B：快速行动，牺牲本飞刀 → 对本地区的一个目标角色造成 1 点伤害 ——
            // 前置条件仅为"牺牲本角色"（无横置前提）：tres 记载该效果只需牺牲，横置是效果A（横置暗藏者）的代价。
            // 因此已横置的飞刀也能发动本效果（牺牲即可，无需未横置），CanActivate 不因 IsTapped 拦截。
            data.EffectDataList.Add(new EffectData
            {
                EffectId = "JC020_2_DealDamage",
                Timing = EffectTiming.AttachmentActivated,
                ActivateWindow = "快速",
                ActivateCondition = "牺牲本角色",
                Cost = 0,
                Forced = false,
                // 仅正面角色（Kind=Character + IncludeCovert=false）+ 本地区限定
                Target = new TargetSpec
                {
                    Kind = TargetKind.Character,
                    NeedsSelection = true,
                    IsLocalZone = true,
                    IncludeCovert = false,
                },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.DealDamage, Args = new Array<string> { "1" } },
                },
                Duration = "瞬时",
                FaqNote = "牺牲本飞刀，对本地区的一个目标角色造成 1 点伤害；本地区指飞刀所结附角色所处的地区"
            });
        }
    }
}
