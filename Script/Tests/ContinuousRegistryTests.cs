using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Game.Entities;

namespace Script.Tests
{
    /// <summary>P2 — E4 P1 持续注册表（逻辑正确性：增删与还原）。</summary>
    public partial class ContinuousRegistryTests : TestRunner
    {
        private partial class GrantCard : PlayerCardData
        {
            public GrantCard(string name, string keyword)
            {
                CardName = name;
                CategoryFlag = CardCategory.Character;
                EffectDataList = new Array<EffectData>
                {
                    new EffectData
                    {
                        EffectId = name + "_C",
                        Timing = EffectTiming.Continuous,
                        Scope = ZoneScope.ContestZone, // 角色卡在争夺区生效（方案C：Continuous 也按 Scope 判定）
                        ContinuousPrereq = "常驻",
                        ContinuousGrants = new Array<EffectGrant>
                        {
                            new EffectGrant { Kind = "Keyword", Value = keyword, Amount = 1 },
                        },
                        Duration = "常驻",
                    },
                };
            }
        }

        private CardInstance NewInstance(PlayerCardData tmpl, string uid)
        {
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = 0;
            inst.ControllerId = 0;
            inst.CurrentZone = CardZone.AreaZone; // 角色卡在争夺区生效（方案C：配合 Scope 判定）
            return inst;
        }

        public void TestRegisterGrantsKeyword()
        {
            var card = NewInstance(new GrantCard("JC001", "先手调查"), "inst_a");
            Assert.IsFalse(card.HasKeyword("先手调查"), "注册前不应有关键字");
            EffectEngine.RegisterContinuous(card);
            Assert.IsTrue(card.HasKeyword("先手调查"), "注册后应获关键字");
            Assert.IsTrue(EffectEngine.HasContinuous(card), "HasContinuous 应为 true");
        }

        public void TestUnregisterRevokesKeyword()
        {
            var card = NewInstance(new GrantCard("JC001", "先手调查"), "inst_b");
            EffectEngine.RegisterContinuous(card);
            Assert.IsTrue(card.HasKeyword("先手调查"));
            EffectEngine.UnregisterContinuous(card);
            Assert.IsFalse(card.HasKeyword("先手调查"), "注销后应还原关键字");
            Assert.IsFalse(EffectEngine.HasContinuous(card), "HasContinuous 应为 false");
        }

        public void TestSameKeywordMultiSource()
        {
            // 两个来源授予同一关键字，应精确计数
            var c1 = NewInstance(new GrantCard("A", "公开"), "m1");
            var c2 = NewInstance(new GrantCard("B", "公开"), "m2");
            EffectEngine.RegisterContinuous(c1);
            EffectEngine.RegisterContinuous(c2);
            Assert.IsTrue(c1.HasKeyword("公开"));
            EffectEngine.UnregisterContinuous(c1);   // 仅注销一个来源
            Assert.IsTrue(c2.HasKeyword("公开"), "另一来源仍应保有该关键字");
            EffectEngine.UnregisterContinuous(c2);
            Assert.IsFalse(c2.HasKeyword("公开"), "全部注销后还原");
        }

        /// <summary>持续效果系统红线（D19 部分已打通后的剩余缺口）。
        /// 已打通：条件持续前提（PrereqSpec + ReevaluatePrereqs）、静止/禁止标记（CannotBeAttached）、
        /// 白眼=先手调查接入 AreaStatsCalculator。剩余：ZoneScope 仅 Static 按 ContestZone 生效、
        /// 持续效果自身的区域联动（如手牌区数值类）仍有限、UI 图标展示未接。</summary>
        public void TestRedlines()
        {
            var redline = new List<string>();
            Assert.KnownRedline(true, redline, "ZoneScope 仅 Static 按 ContestZone 校验生效；持续效果(Continuous)仍不按 Scope 联动区域");
            Assert.KnownRedline(true, redline, "持续授予的属性/图标尚未接 UI 展示，仅数据层与对抗计算生效");
            Assert.KnownRedline(true, redline, "影响他者的光环/静止类（不能被锁定等）尚未实现，仅 CannotBeAttached 单例打通");
        }
    }
}
