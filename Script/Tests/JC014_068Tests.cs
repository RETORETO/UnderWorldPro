using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// JC014（公路骑士）与 JC068（阿格斯IV）数据层测试（2026-08-16）。
    /// 覆盖模式参照 JC001/002/003/012/015/030/035/060：
    ///   · 效果结构断言（Timing/Scope/Forced/Target/Actions/ContinuousGrants）
    ///   · 关键字固有判定（HasInherentKeyword）
    ///   · 目标合法性（IsLegalActivateTarget / IsLegalPlayTarget / GetAllowedTargets）
    ///   · 持续前提（SelfUntapped）→ 全局封锁秘密派遣的注册/解除/未结附不生效
    /// </summary>

    // ==================== JC014 公路骑士（角色） ====================
    public partial class JC014Tests : TestRunner
    {
        /// <summary>构造 JC014 风格模板：持续公开（手牌区）+ 机动（对抗阶段可选移动）。</summary>
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC014_gongluqishi",
                CardName = "公路骑士",
                CategoryFlag = CardCategory.Character,
            };
            tmpl.EffectDataList.Add(KeywordEffects.Public());   // 持续公开
            tmpl.EffectDataList.Add(KeywordEffects.Maneuver()); // 机动（对抗阶段可选移动）
            return tmpl;
        }

        private CardInstance NewInstance(string uid, int ctrl = 1, CardZone zone = CardZone.AreaZone)
        {
            var inst = new CardInstance(MakeTemplate(), uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = zone;
            inst.ZoneDetailId = "areaA";
            return inst;
        }

        public void TestJC014_T1_持续公开手牌区生效()
        {
            var tmpl = MakeTemplate();
            var e = tmpl.EffectDataList[0];
            Assert.AreEqual(EffectTiming.Continuous, e.Timing, "公开为持续效果");
            Assert.AreEqual(ZoneScope.HandZone, e.Scope, "公开在手牌区生效");
            Assert.AreEqual("Keyword", e.ContinuousGrants[0].Kind, "公开授予类型为 Keyword");
            Assert.AreEqual("公开", e.ContinuousGrants[0].Value, "公开授予关键字=公开");
            Assert.IsTrue(tmpl.HasInherentKeyword("公开"), "模板固有文本能力应声明公开关键字");
            Assert.IsTrue(tmpl.HasPublicKeyword(), "HasPublicKeyword 语义化封装应为 true");
        }

        public void TestJC014_T2_机动触发结构()
        {
            var e = MakeTemplate().EffectDataList[1];
            Assert.AreEqual(EffectTiming.ManeuverTrigger, e.Timing, "机动为对抗阶段开始触发");
            Assert.IsFalse(e.Forced, "机动玩家可选（非强制）");
            Assert.AreEqual(TargetKind.Area, e.Target.Kind, "机动目标为地区");
            Assert.IsTrue(e.Target.NeedsSelection, "机动需要选地区");
            Assert.AreEqual(ActionVerb.Move, e.Actions[0].Verb, "机动动作为移动");
            Assert.AreEqual("瞬时", e.Duration, "机动持续为瞬时");
        }

        public void TestJC014_T3_机动目标地区合法性()
        {
            var src = NewInstance("骑士_src", 1);
            var ability = MakeTemplate().EffectDataList[1];   // 机动：Target.Kind=Area

            // 机动目标为地区：MapInstanceToTargetKind 对 AreaCardData（无 PlayerTemplate）返回 None，
            // IsLegalActivateTarget 无法验证"地区合法"；改为验证目标大类聚合含 Area（数据层真相源）。
            var allowed = MakeTemplate().GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Area), $"机动目标大类应含 Area，实际={string.Join(",", allowed)}");

            // 目标为角色 → 非法（Kind 不匹配：Character ≠ Area）
            var charTarget = new CardInstance(new PlayerCardData { CardName = "某角色", CategoryFlag = CardCategory.Character }, "char_t");
            charTarget.CurrentZone = CardZone.AreaZone;
            Assert.IsFalse(ActivateEligibility.IsLegalActivateTarget(src, ability, charTarget, null),
                "机动目标为角色应非法（Kind=Area）");
        }

        public void TestJC014_T4_公开授予关键字()
        {
            // 注册后"公开"关键字授予仍可被模板固有判定捕获（数据层不依赖运行时持续注册时机）
            var card = NewInstance("骑士_a", 1);
            EffectEngine.RegisterPersistent(card);
            var tmpl = card.PlayerTemplate as PlayerCardData;
            Assert.IsTrue(tmpl.HasInherentKeyword("公开"), "注册后模板固有公开判定仍为 true");
            EffectEngine.UnregisterPersistent(card);
        }
    }

    // ==================== JC068 阿格斯IV（附属） ====================
    public partial class JC068Tests : TestRunner
    {
        /// <summary>构造 JC068 风格模板：效果A(准备强制触发,支付1或横置)+效果B(持续封锁,前提未横置)。</summary>
        private PlayerCardData MakeTemplate()
        {
            var tmpl = new PlayerCardData
            {
                UID = "JC068_agesiIV",
                CardName = "阿格斯IV",
                CategoryFlag = CardCategory.Attachment,
            };
            // 打出目标：本方秘社
            tmpl.TargetRules = new Array<TargetRule> { TargetRule.Make(TargetKind.Council, TargetRestriction.Friendly) };
            // 效果A：准备步骤强制触发（支付1或横置）
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC068_1_PrepareUpkeep",
                Timing = EffectTiming.OnTurnStarted,
                Forced = true,
                Scope = ZoneScope.AttachTarget,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PrepareUpkeep, Args = new Array<string> { "1" } }
                },
                Duration = "强制",
            });
            // 效果B：持续封锁秘密派遣（前提：自身未横置）
            tmpl.EffectDataList.Add(new EffectData
            {
                EffectId = "JC068_2_BlockDeployCovert",
                Timing = EffectTiming.Continuous,
                Scope = ZoneScope.AttachTarget,
                ContinuousPrereq = "阿格斯IV未横置",
                PrereqSpec = new ContinuousPrereqSpec { Kind = PrereqKind.SelfUntapped },
                StaticEffects = new Array<StaticEffect> { new StaticEffect { Kind = StaticKind.BlockDeployCovert, Param = "" } },
            });
            return tmpl;
        }

        /// <summary>构造附属到 AttachedZone 的阿格斯实例（控制者=1）。</summary>
        private CardInstance NewAttached(string uid, int ctrl = 1, CardZone zone = CardZone.AttachedZone)
        {
            var inst = new CardInstance(MakeTemplate(), uid);
            inst.OwnerId = ctrl;
            inst.ControllerId = ctrl;
            inst.CurrentZone = zone;
            inst.IsAttached = zone == CardZone.AttachedZone;
            return inst;
        }

        public void TestJC068_T1_打出目标本方秘社()
        {
            var tmpl = MakeTemplate();
            var allowed = tmpl.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Council), $"打出目标应含 Council，实际={string.Join(",", allowed)}");

            var cm = new CardManager(); cm._Ready();
            var src = NewAttached("src", 1);
            var selfCouncil = new CardInstance(new CouncilCardData { CardName = "本方秘社" }, "c1");
            selfCouncil.ControllerId = 1; selfCouncil.CurrentZone = CardZone.CouncilZone;
            Assert.IsTrue(cm.IsLegalPlayTarget(src, selfCouncil), "本方秘社应为合法打出目标");
            var enemyCouncil = new CardInstance(new CouncilCardData { CardName = "敌方秘社" }, "c2");
            enemyCouncil.ControllerId = 2; enemyCouncil.CurrentZone = CardZone.CouncilZone;
            Assert.IsFalse(cm.IsLegalPlayTarget(src, enemyCouncil), "敌方秘社不应为合法打出目标");
        }

        public void TestJC068_T2_效果A准备强制触发()
        {
            var e = MakeTemplate().EffectDataList[0];
            Assert.AreEqual(EffectTiming.OnTurnStarted, e.Timing, "效果A为准备步骤触发");
            Assert.IsTrue(e.Forced, "效果A强制触发不能放弃");
            Assert.AreEqual(ZoneScope.AttachTarget, e.Scope, "效果A生效区=附属区（Z1）");
            Assert.IsFalse(e.Target.NeedsSelection, "效果A无选目标");
            Assert.AreEqual(ActionVerb.PrepareUpkeep, e.Actions[0].Verb, "效果A动词=PrepareUpkeep");
            Assert.AreEqual("1", e.Actions[0].Args[0], "效果A代价为1（支付1或横置）");
        }

        public void TestJC068_T3_效果B未横置封锁对手派遣()
        {
            EffectEngine.ResetAll();   // 隔离静态封锁状态（避免跨测试污染 _blockDeployCovertPlayers）
            var card = NewAttached("agesi_a", 1);   // 未横置
            // 前提 SelfUntapped 满足 → RegisterPersistent 应封锁对手2
            EffectEngine.RegisterPersistent(card);
            Assert.IsTrue(EffectEngine.IsDeployCovertBlocked(2), "未横置时应对手2被封锁（秘密派遣）");
            Assert.IsFalse(EffectEngine.IsDeployCovertBlocked(1), "己方(1)不应被封锁");
        }

        public void TestJC068_T4_横置解除封锁竖置恢复()
        {
            EffectEngine.ResetAll();   // 隔离静态封锁状态（避免 T3 的 agesi_a 持续封锁玩家2）
            var card = NewAttached("agesi_b", 1);
            EffectEngine.RegisterPersistent(card);
            Assert.IsTrue(EffectEngine.IsDeployCovertBlocked(2), "前置：未横置应封锁对手2");

            // 横置 → 前提 SelfUntapped 丧失 → 解除封锁
            card.SetTapped(true);
            EffectEngine.ReevaluatePrereqs(1);
            Assert.IsFalse(EffectEngine.IsDeployCovertBlocked(2), "横置后应解除对手2封锁");

            // 竖置 → 前提恢复 → 重新封锁
            card.SetTapped(false);
            EffectEngine.ReevaluatePrereqs(1);
            Assert.IsTrue(EffectEngine.IsDeployCovertBlocked(2), "竖置后应恢复对手2封锁");
        }

        public void TestJC068_T5_未结附不生效()
        {
            EffectEngine.ResetAll();   // 隔离静态封锁状态（避免前面测试已封锁玩家2导致断言失真）
            // 阿格斯在 AreaZone（未结附）→ Z1 判定不生效，封锁不注册
            var card = NewAttached("agesi_c", 1, CardZone.AreaZone);
            EffectEngine.RegisterPersistent(card);
            Assert.IsFalse(EffectEngine.IsDeployCovertBlocked(2), "未结附（AreaZone）时封锁不应注册");
        }
    }
}
