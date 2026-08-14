using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>JC001 通灵猫头鹰 —— D19 打通验证：
    ///  - A：静止/禁止"不能被装备结附" → StaticFlags 置位/清除（D19-⑤/⑦）
    ///  - B：条件持续"白眼=先手调查" → 属性授予与先手调查值联动（D19-③）
    /// 前提（心灵忠诚≥2 开关）由 ContinuousRegistryTests / 集成链路覆盖，本文件验证数据层不变量。</summary>
    public partial class JC001Tests : TestRunner
    {
        private CardInstance NewJC001(int controllerId = 0)
        {
            Cards.Effects.AllEffects.RegisterAll();   // 幂等：确保 JC001 效果构建器已注册
            var tmpl = new PlayerCardData { UID = "JC001_tonglingmaotouying" };
            CardEffectFactory.EnsureEffects(tmpl);    // 经工厂构建 JC001 效果
            var inst = new CardInstance(tmpl, "jc001_" + controllerId);
            inst.OwnerId = controllerId;
            inst.ControllerId = controllerId;
            return inst;
        }

        public void TestCannotBeAttachedSetsStaticFlag()
        {
            var card = NewJC001();
            Assert.IsFalse(card.HasStaticFlag(StaticKind.CannotBeAttached), "注册前不应有静止标记");
            EffectEngine.RegisterPersistent(card);
            Assert.IsTrue(card.HasStaticFlag(StaticKind.CannotBeAttached),
                "注册后应有 CannotBeAttached 静止标记（争夺区生效）");
            Assert.IsTrue(EffectEngine.HasStaticFlag(card, StaticKind.CannotBeAttached),
                "HasStaticFlag 查询入口应返回 true");
        }

        public void TestCannotBeAttachedClearedOnUnregister()
        {
            var card = NewJC001();
            EffectEngine.RegisterPersistent(card);
            Assert.IsTrue(card.HasStaticFlag(StaticKind.CannotBeAttached));
            EffectEngine.UnregisterPersistent(card);
            Assert.IsFalse(card.HasStaticFlag(StaticKind.CannotBeAttached),
                "注销后应清除静止标记（D19-⑦ 防泄漏）");
        }

        public void TestWhiteEyeGrantsFirstStrikeInvestigation()
        {
            var card = NewJC001();
            // 直接模拟持续授予"白眼=先手调查+1"（同 EffectEngine.RegisterPersistent 满足前提时的 Grant）
            int before = card.CurrentFirstStrikeInvestigation;
            card.Grant("Attribute", "先手调查", 1);
            Assert.AreEqual(before + 1, card.CurrentFirstStrikeInvestigation,
                "白眼应使先手调查+1（卡文'白眼白眼'可叠加）");
            Assert.AreEqual(1, card.GetAttribute("先手调查"), "应先手调查属性计数=1");
            // 撤销还原
            card.Revoke("Attribute", "先手调查", 1);
            Assert.AreEqual(before, card.CurrentFirstStrikeInvestigation, "撤销后应还原");
            Assert.AreEqual(0, card.GetAttribute("先手调查"), "撤销后属性计数=0");
        }

        public void TestMultipleWhiteEyesStack()
        {
            var card = NewJC001();
            card.Grant("Attribute", "先手调查", 1);
            card.Grant("Attribute", "先手调查", 1); // 第二个白眼
            Assert.AreEqual(2, card.GetAttribute("先手调查"), "双白眼应叠加为2");
            Assert.AreEqual(2, card.CurrentFirstStrikeInvestigation, "双白眼先手调查+2");
        }

        public void TestResetKeepsGrantedWhiteEye()
        {
            var card = NewJC001();
            card.Grant("Attribute", "先手调查", 1);
            int withEye = card.CurrentFirstStrikeInvestigation;
            card.ResetToBaseValues(true);
            Assert.AreEqual(withEye, card.CurrentFirstStrikeInvestigation,
                "ResetToBaseValues 应保留已授予的白眼（重算不丢失）");
        }
    }
}
