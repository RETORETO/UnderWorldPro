using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Game.Entities;

namespace Script.Tests
{
    /// <summary>P0 — E3 A+B 通用目标拦截 / D16 目标丢失（逻辑正确性）。</summary>
    public partial class TargetInterceptTests : TestRunner
    {
        // 测试用事务卡：效果声明需要选择"角色"目标
        private partial class SpellCard : PlayerCardData
        {
            public SpellCard()
            {
                CardName = "测试事务";
                CategoryFlag = CardCategory.Sorcery;
                EffectDataList = new Array<EffectData>
                {
                    new EffectData
                    {
                        EffectId = "SP1",
                        Timing = EffectTiming.SpellCast,
                        Target = new TargetSpec { Kind = TargetKind.Character, NeedsSelection = true },
                        Actions = new Array<EffectAction> { new EffectAction { Verb = ActionVerb.Destroy } },
                        Duration = "瞬时",
                    },
                };
            }
        }

        // 测试用角色卡：无目标
        private partial class CharCard : PlayerCardData
        {
            public CharCard()
            {
                CardName = "测试角色";
                CategoryFlag = CardCategory.Character;
            }
        }

        private CardInstance NewInstance(PlayerCardData tmpl, string uid = "u1")
        {
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = 0;
            inst.ControllerId = 0;
            return inst;
        }

        public void TestGetRequiresTargetFromEffectData()
        {
            var spell = new SpellCard();
            Assert.IsTrue(spell.GetRequiresTarget(), "事务卡含 NeedsSelection=true 效果应判定需要目标");
        }

        public void TestGetRequiresTargetCharacterDefault()
        {
            var ch = new CharCard();
            Assert.IsFalse(ch.GetRequiresTarget(), "纯角色卡默认不需要选择目标");
        }

        public void TestGetAllowedTargetsFromEffectData()
        {
            var spell = new SpellCard();
            var allowed = spell.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Character), "AllowedTargets 应聚合 EffectData.Target.Kind=Character");
        }

        public void TestAllowedTargetsSingleCardOverride()
        {
            var spell = new SpellCard();
            // 单卡显式覆盖优先
            spell.AllowedTargets = new Array<TargetKind> { TargetKind.Area };
            var allowed = spell.GetAllowedTargets();
            Assert.IsTrue(allowed.Contains(TargetKind.Area), "单卡 AllowedTargets 覆盖应生效");
            Assert.IsFalse(allowed.Contains(TargetKind.Character), "覆盖后不应出现 EffectData 推导的 Character");
        }

        public void TestValidateTargetAtResolveSpellLost()
        {
            var spell = new SpellCard();
            var src = NewInstance(spell);
            // 目标丢失（null）
            var res = EffectEngine.ValidateTargetAtResolve(spell.EffectDataList[0], src, null);
            Assert.AreEqual(EffectEngine.TargetValidation.Lost, res, "事务卡目标丢失应返回 Lost");
        }

        public void TestValidateTargetAtResolveSpellOk()
        {
            var spell = new SpellCard();
            var src = NewInstance(spell);
            var tgt = NewInstance(new CharCard(), "u2");
            tgt.CurrentZone = CardZone.AreaZone; // 角色目标需置于争夺区方为合法结算目标
            var res = EffectEngine.ValidateTargetAtResolve(spell.EffectDataList[0], src, tgt);
            Assert.AreEqual(EffectEngine.TargetValidation.Ok, res, "目标存在应返回 Ok");
        }

        public void TestValidateTargetAtResolveNoTarget()
        {
            var ch = new CharCard();
            var src = NewInstance(ch);
            var res = EffectEngine.ValidateTargetAtResolve(
                new EffectData { Timing = EffectTiming.Continuous, Target = new TargetSpec { Kind = TargetKind.None, NeedsSelection = false } },
                src, null);
            Assert.AreEqual(EffectEngine.TargetValidation.NoTarget, res, "无目标效果应返回 NoTarget");
        }

        /// <summary>ResolveEntryTargetIsLost 为 TurnManager 私有方法，需经集成（打出→结算链路）验证；此处仅记录红线。</summary>
        public void TestResolveEntryTargetIsLostIntegrationRedline()
        {
            var redline = new List<string>();
            Assert.KnownRedline(true, redline,
                "TurnManager.ResolveEntryTargetIsLost 为私有，需集成测试：角色落点地区丢失→进墓地 / 事务目标丢失→不结算进墓地 / 附属结附目标丢失→进墓地");
            Assert.KnownRedline(true, redline,
                "D16 反击规则已实现（2026-08-11 复核）：TurnManager.ResolveEntryTargetIsLost 按 ResolveKind 分派——" +
                "事务卡/附属(卡)丢失目标→取消打出、进墓地（EventListenerToGraveyard / Attachment 分支）；" +
                "反应触发/激活(效果)丢失目标→仅跳过结算、卡留场（Trigger 分支）。与 LossMode=Counter 语义一致，" +
                "补偿逻辑已由该私有方法覆盖，本红线仅标注其私有性需集成测试，不再标注'未实现'。");
        }
    }
}
