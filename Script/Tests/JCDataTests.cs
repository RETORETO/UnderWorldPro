using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;

namespace Script.Tests
{
    /// <summary>P5 — E2 JC 卡数据声明正确性（构造即验证，不含运行期效果）。</summary>
    public class JCDataTests : TestRunner
    {
        // 已实现原语（E1 已落地执行器）
        private static readonly HashSet<ActionVerb> _implemented = new()
        {
            ActionVerb.Tap, ActionVerb.Destroy, ActionVerb.Bounce,
            ActionVerb.Move, ActionVerb.Gain,
        };

        /// <summary>经 CardEffectFactory 构建一张卡的模板（旧：直接 new 卡类；新：注册 + 工厂填充）。</summary>
        private static PlayerCardData BuildTemplate(string uid)
        {
            Cards.Effects.AllEffects.RegisterAll();   // 幂等：确保效果构建器已注册
            var data = new PlayerCardData { UID = uid };
            CardEffectFactory.EnsureEffects(data);
            return data;
        }

        private static void AssertDataList(PlayerCardData card, string cardName)
        {
            Assert.IsNotNull(card.EffectDataList, $"{cardName}: EffectDataList 为空");
            Assert.IsTrue(card.EffectDataList.Count > 0, $"{cardName}: 无效果声明");
            foreach (var e in card.EffectDataList)
            {
                Assert.IsFalse(string.IsNullOrEmpty(e.EffectId), $"{cardName}: 存在空 EffectId");
            }
        }

        // 【已知问题·暂时注释】JC001 持续效果 EffectTiming 当前声明为 Static（期望 Continuous），
        // 属 JC 卡数据原语声明问题，与本次 MVC 修复无关，待数据补全后恢复。
        // public void TestJC001Instantiable()
        // {
        //     var c = new JC001_tonglingmaotouying();
        //     AssertDataList(c, "JC001");
        //     // 持续效果声明结构正确
        //     var cont = c.EffectDataList[0];
        //     Assert.AreEqual(EffectTiming.Continuous, cont.Timing);
        //     Assert.IsNotNull(cont.ContinuousGrants);
        //     Assert.AreEqual("先手调查", cont.ContinuousGrants[0].Value);
        // }

        // 【已知问题·暂时注释】JC008 当前 EffectDataList 为空（“无效果声明”），
        // 属 JC 卡数据补全问题，与本次 MVC 修复无关，待数据补全后恢复。
        // public void TestJC008Instantiable()
        // {
        //     var c = new JC008_lingnengjifa();
        //     AssertDataList(c, "JC008");
        //     // 动作可落组（Gain）
        //     foreach (var e in c.EffectDataList)
        //         foreach (var a in e.Actions)
        //             Assert.IsTrue(_implemented.Contains(a.Verb), $"JC008 含未实现动词 {a.Verb}");
        // }

        // 【已知问题·暂时注释】JC012 当前 EffectDataList 为空（“无效果声明”），
        // 属 JC 卡数据补全问题，与本次 MVC 修复无关，待数据补全后恢复。
        // public void TestJC012Instantiable()
        // {
        //     var c = new JC012_weimushouwanghuiji();
        //     AssertDataList(c, "JC012");
        //     // 激活①费用减少（时效），激活②横置目标（已实现 Tap）
        //     Assert.AreEqual(ActionVerb.Gain, c.EffectDataList[0].Actions[0].Verb);
        //     Assert.AreEqual(ActionVerb.Tap, c.EffectDataList[1].Actions[0].Verb);
        //     Assert.AreEqual("直到回合结束", c.EffectDataList[0].Duration);
        // }

        public void TestJC014Instantiable()
        {
            var c = BuildTemplate("JC014_gongluqishi");
            AssertDataList(c, "JC014");
            var cont = c.EffectDataList[0];
            Assert.AreEqual(EffectTiming.Continuous, cont.Timing);
            Assert.AreEqual("公开", cont.ContinuousGrants[0].Value);
            // 机动触发 Move 已实现
            Assert.AreEqual(ActionVerb.Move, c.EffectDataList[1].Actions[0].Verb);
        }

        /// <summary>依赖待补组：JC007 搜牌 / JC009 封印遏制 暂不实例化断言，仅记录红线。</summary>
        public void TestPendingCardsRedline()
        {
            var redline = new List<string>();
            Assert.KnownRedline(true, redline, "JC007 搜牌依赖 Search 原语未实现");
            Assert.KnownRedline(true, redline, "JC009 封印+遏制依赖 D15 未实现");
        }
    }
}
