using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Cards.Effects.PlayerCards.JC
{
    /// <summary>
    /// JC068 阿格斯IV 效果（自原 JC068_agesi 构造函数迁移，逻辑不变）。
    /// 附属卡（机构），结附于本方秘社。
    ///  A）准备步骤强制触发：支付1（横置1张资产区牌），否则横置阿格斯IV；
    ///  B）持续：只要阿格斯IV未横置，对手就不能进行秘密派遣行动。
    /// </summary>
    public static class JC068_agesiIV_Effects
    {
        public static void Register()
            => CardEffectFactory.Register("JC068_agesiIV", Build);

        private static void Build(PlayerCardData data)
        {
            // ---- 打出目标：仅本方秘社 ----
            data.TargetRules = new Godot.Collections.Array<TargetRule>
            {
                TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly),
            };

            // ---- 效果A：准备步骤强制触发：支付1，否则横置阿格斯IV ----
            var upkeep = new EffectData
            {
                EffectId = "JC068_A_Upkeep",
                Timing = EffectTiming.OnTurnStarted,
                Forced = true,
                Scope = ZoneScope.AttachTarget,
                Target = new TargetSpec { NeedsSelection = false },
            };
            upkeep.Actions.Add(new EffectAction
            {
                Verb = ActionVerb.PrepareUpkeep,
                Args = new Array<string> { "1" },
            });
            data.EffectDataList.Add(upkeep);

            // ---- 效果B：持续：只要阿格斯IV未横置，对手就不能进行秘密派遣行动 ----
            var block = new EffectData
            {
                EffectId = "JC068_B_BlockDeployCovert",
                Timing = EffectTiming.Continuous,
                // 生效区：AttachTarget（结附于目标时）。阿格斯IV是结附于本方秘社的附属，在 AttachedZone 生效。
                // 【方案C】原误写 AreaZone（角色卡模板遗留），导致 IsEffectActiveInZone 判 AreaZone 为 false、
                // 附属于 AttachedZone 时封锁不注册。改为 AttachTarget 与附属身份/生效位置一致。
                Scope = ZoneScope.AttachTarget,
                Duration = "常驻",
                ContinuousPrereq = "阿格斯IV未横置",
                PrereqSpec = new ContinuousPrereqSpec
                {
                    Kind = PrereqKind.SelfUntapped,
                },
                ContinuousEffect = "只要阿格斯IV未横置，对手不能秘密派遣",
                StaticEffects = new Array<StaticEffect>
                {
                    new StaticEffect { Kind = StaticKind.BlockDeployCovert, Param = "" }
                },
            };
            data.EffectDataList.Add(block);
        }
    }
}
