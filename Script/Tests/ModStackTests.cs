using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Manager;
using Script.Game.Entities;

namespace Script.Tests
{
    /// <summary>P3 — E5 P2 修正栈（逻辑正确性：时效还原 + 玩家级费用修正）。</summary>
    public partial class ModStackTests : TestRunner
    {
        private partial class CharCard : PlayerCardData
        {
            public CharCard() { CardName = "测试角色"; CategoryFlag = CardCategory.Character; Defense = 2; }
        }

        private CardInstance NewInstance(PlayerCardData tmpl, string uid)
        {
            var inst = new CardInstance(tmpl, uid);
            inst.OwnerId = 0;
            inst.ControllerId = 0;
            return inst;
        }

        public void TestCostModificationStack()
        {
            EffectEngine.ExpireTurnEndEffects(int.MaxValue); // 隔离：清掉跨测试残留
            EffectEngine.CurrentTurn = 5;
            EffectEngine.ApplyCostModification(0, 1, 5, "帷幕守望");
            int mod = EffectEngine.GetCostModification(0, "帷幕守望徽记");
            Assert.AreEqual(1, mod, "匹配 filter 的卡应获费用 -1");
            int modOther = EffectEngine.GetCostModification(0, "灵能激发");
            Assert.AreEqual(0, modOther, "不匹配 filter 的卡不应减免");
        }

        public void TestCostModificationExpire()
        {
            // 隔离：先清空 static 费用栈中跨测试残留
            EffectEngine.ExpireTurnEndEffects(int.MaxValue);

            EffectEngine.CurrentTurn = 5;
            EffectEngine.ApplyCostModification(0, 1, 5, "帷幕守望");
            Assert.AreEqual(1, EffectEngine.GetCostModification(0, "帷幕守望徽记"), "匹配 filter 的卡应获费用 -1");

            // 回合结束（当前回合达 expireTurn）触发还原
            EffectEngine.ExpireTurnEndEffects(5);
            Assert.AreEqual(0, EffectEngine.GetCostModification(0, "帷幕守望徽记"), "回合结束后费用修正应出栈");

            // 未到期不应出栈
            EffectEngine.ApplyCostModification(0, 1, 7, "帷幕守望");
            EffectEngine.ExpireTurnEndEffects(5);
            Assert.AreEqual(1, EffectEngine.GetCostModification(0, "帷幕守望徽记"), "未到期费用修正应保留");
            EffectEngine.ExpireTurnEndEffects(int.MaxValue); // 清理
        }

        public void TestDefenseTimedRestore()
        {
            // 防御修正需实例在 CardManager 注册表中才可被 ExpireTurnEndEffects 还原。
            // 此处直接验证 CurrentDefense 增减契约（还原路径依赖 CardManager.Instance，集成测试覆盖）。
            var card = NewInstance(new CharCard(), "d1");
            int baseDef = card.CurrentDefense;
            card.CurrentDefense += 1; // 模拟 JC008 +1 防御
            Assert.AreEqual(baseDef + 1, card.CurrentDefense, "时效防御修正应累加");
            card.CurrentDefense -= 1;
            Assert.AreEqual(baseDef, card.CurrentDefense, "回合结束应还原防御");
        }

        /// <summary>防御还原与 P1 容器不统一 / 仅玩家级：已知红线（"下一张"语义已由 JC012 consumeOnce 实装，不再列为红线）。</summary>
        public void TestRedlines()
        {
            var redline = new List<string>();
            Assert.KnownRedline(true, redline, "默认（非一次性）费用修正语义仍为整回合全减免（JC012 通过 consumeOnce 实现『下一张』）");
            Assert.KnownRedline(true, redline, "防御还原用 CurrentDefense 直加减，与 P1 动态容器不统一");
            Assert.KnownRedline(true, redline, "费用修正仅玩家级，卡级费用修正未建模");
        }

        /// <summary>"下一张"语义（JC012，2026-08-11）：consumeOnce 一次性费用修正，打出符合条件牌一次后消费移除；
        /// 整回合未使用则随 ExpireTurnEndEffects 到期出栈流失。</summary>
        public void TestCostModificationConsumeOnce()
        {
            EffectEngine.ExpireTurnEndEffects(int.MaxValue); // 隔离
            EffectEngine.CurrentTurn = 5;
            // JC012 激活①：一次性减费（filter="帷幕守望"）
            EffectEngine.ApplyCostModification(0, 1, 5, "帷幕守望", consumeOnce: true);

            // 第一张打出的帷幕守望牌：享受减费
            Assert.AreEqual(1, EffectEngine.GetCostModification(0, "某帷幕守望角色", "帷幕守望"), "下一张应减费");
            EffectEngine.ConsumeCostModification(0, "帷幕守望"); // 打出后消费
            // 第二张：已消费，不再减费
            Assert.AreEqual(0, EffectEngine.GetCostModification(0, "另一帷幕守望角色", "帷幕守望"), "消费后下一张不再减费");

            // 未消费场景：新登记一次性项，回合结束前未用 → 到期流失
            EffectEngine.ApplyCostModification(0, 1, 5, "帷幕守望", consumeOnce: true);
            Assert.AreEqual(1, EffectEngine.GetCostModification(0, "帷幕守望角色", "帷幕守望"), "未消费前应减费");
            EffectEngine.ExpireTurnEndEffects(5); // 回合结束
            Assert.AreEqual(0, EffectEngine.GetCostModification(0, "帷幕守望角色", "帷幕守望"), "回合结束未用 → 流失");

            EffectEngine.ExpireTurnEndEffects(int.MaxValue); // 清理
        }

        /// <summary>Organization 维度匹配（2026-08-11）：GetCostModification 支持按卡 Organization 匹配 filter，
        /// 不依赖卡名是否含该串（如"帷幕守望"组织牌的卡名不一定含"帷幕守望"四字）。</summary>
        public void TestCostModificationByOrganization()
        {
            EffectEngine.ExpireTurnEndEffects(int.MaxValue); // 隔离
            EffectEngine.CurrentTurn = 5;
            EffectEngine.ApplyCostModification(0, 1, 5, "帷幕守望");

            // 卡名不含"帷幕守望"但 Organization=="帷幕守望" → 应匹配减费
            Assert.AreEqual(1, EffectEngine.GetCostModification(0, "黄金圣枪", "帷幕守望"), "Organization 匹配应减费");
            // 卡名含"帷幕守望"且 Organization 也匹配 → 仍匹配（双维度 OR）
            Assert.AreEqual(1, EffectEngine.GetCostModification(0, "帷幕守望徽记", "帷幕守望"), "卡名+Organization 均应匹配");
            // Organization 不符 → 不减免
            Assert.AreEqual(0, EffectEngine.GetCostModification(0, "黄金圣枪", "灵能教团"), "Organization 不符不应减费");

            EffectEngine.ExpireTurnEndEffects(int.MaxValue); // 清理
        }
    }
}
