using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Interfaces;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【暗藏者现身（快速能力 · AmbushReveal）集成测试】
    ///
    /// 验证的完整链路（B 层 · 真实组件，仅以 mock 视图绕过 UI 弹窗）：
    ///   构造暗藏者卡（IsCovert=true，在争夺区）→ 双击现身（RevealCovert）
    ///   → 门禁（有行动权/PriorityPlayerId）
    ///   → 忠诚/费用校验（按控制者 ControllerId）
    ///   → 进堆叠即翻面（IsCovert=false），Model 层卡移入 Stack 区
    ///   → 构造 AmbushReveal 结算条目，箭头 Targets 指向原暗藏地区
    ///   → 结算（ResolveStackTop）→ 卡回争夺区（MoveToArea 自动触发 EnterTrigger）+ 广播 UncoverTrigger
    ///
    /// 覆盖：门禁拒绝（无行动权）、非暗藏者拒绝、费用不足拒绝、成功现身全流程。
    /// </summary>
    public class RevealCovertTests : TestRunner
    {
        private TurnManager _tm;
        private CardManager _cm;
        private StackManager _sm;
        private ZoneManager _zm;
        private AreaManager _地区管理器;
        private PlayerManager _pm;

        private int _enterPlayCount;
        private int _uncoverCount;

        private void 搭建测试环境()
        {
            _cm = new CardManager();
            _cm._Ready();
            _sm = new StackManager();
            _sm._Ready();
            _zm = new ZoneManager();
            _zm.Initialize(_cm, null);
            _cm.SetZoneManager(_zm);          // 关键连通性（与 JC002 同）
            _pm = new PlayerManager();
            _tm = new TurnManager();
            _tm._Ready();
            _tm.Initialize(null, _pm, _cm);    // 注入 PlayerManager 以支持忠诚/费用校验
            _tm.SetStackManager(_sm);
            _tm.SetZoneManager(_zm);
            _地区管理器 = new AreaManager();
            _地区管理器.RegisterArea("areaA", 1, _cm.CreateCardInstance(new AreaCardData(), 0));
            _地区管理器.RegisterArea("areaB", 2, _cm.CreateCardInstance(new AreaCardData(), 0));
            _tm.SetAreaManager(_地区管理器);
            _tm.SetTriggerView(new 空视图());

            //【MVC 收口】触发系统三实例（原 static class → 实例）：测试自行装配，注入各 Manager，
            // 并覆盖总线 Dispatch 捕获 EnterTrigger/UncoverTrigger 广播（本测试卡无触发效果，不影响）。
            _enterPlayCount = 0;
            _uncoverCount = 0;
            var eventBus = new Script.Manager.GameEventBus();
            var emitPoints = new Script.Manager.TriggerEmitPoints(eventBus);
            var registry = new Script.Manager.TriggerRegistry(eventBus);
            _tm.SetTriggerSystem(registry, emitPoints, eventBus);
            _zm.SetTriggerEmitPoints(emitPoints);
            _cm.SetTriggerEmitPoints(emitPoints);
            eventBus.SetDispatch((evt, ctx) =>
            {
                if (evt == GameEvent.EnterTrigger) _enterPlayCount++;
                if (evt == GameEvent.UncoverTrigger) _uncoverCount++;
            });

            // 默认行动权归玩家1（先手），满足现身门禁
            设优先权(1);
        }

        /// <summary>反射设置 TurnManager.PriorityPlayerId（其 setter 为 private，测试需直接写 backing field）</summary>
        private void 设优先权(int p)
        {
            var f = typeof(TurnManager).GetField("<PriorityPlayerId>k__BackingField",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f?.SetValue(_tm, p);
        }

        /// <summary>空视图：不弹任何窗口（现身无目标选择/排序窗口需求）</summary>
        private class 空视图 : ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
                Action<List<StackEffectInstance>> onEnterStack, Action onDone)
                => onDone?.Invoke();
            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids, Action<string> onPicked)
                => onPicked?.Invoke("");
            public void RemoveOrderEntries(List<StackEffectInstance> pushed) { }
        }

        /// <summary>构造一张暗藏者卡：IsCovert=true，置于指定争夺区，控制者=拥有者。</summary>
        private CardInstance 创建暗藏者卡(int 拥有者Id, string 地区Uid, int cost = 0)
        {
            var 卡数据 = new PlayerCardData
            {
                CardName = "暗藏者_测试卡",
                CategoryFlag = CardCategory.Character,
                Cost = cost,   // 默认 0（无费用需求）；费用不足用例另设
            };
            var 实例 = _cm.CreateCardInstance(卡数据, 拥有者Id);
            实例.ControllerId = 拥有者Id;     // 控制者=拥有者（现身校验 controller==playerId）
            实例.CurrentZone = CardZone.AreaZone;
            实例.ZoneDetailId = 地区Uid;
            实例.IsCovert = true;            // 背面暗藏
            return 实例;
        }

        /// <summary>反射调用私有 ResolveStackTop，触发现身结算</summary>
        private void 结算堆叠顶()
        {
            var m = typeof(TurnManager).GetMethod("ResolveStackTop", BindingFlags.NonPublic | BindingFlags.Instance);
            m?.Invoke(_tm, null);
        }

        // —— 用例1：成功现身全流程 ——
        public void TestRevealCovert成功现身翻面并进堆叠且箭头指向原地区()
        {
            搭建测试环境();
            const string 地区A = "areaA";
            var 卡 = 创建暗藏者卡(1, 地区A, cost: 0);

            bool ok = _tm.RevealCovert(1, 卡.UID);
            Assert.IsTrue(ok, "玩家1 有行动权且费用满足，应成功发动现身");

            // 入栈断言
            Assert.AreEqual(1, _sm.Entries.Count, "期望入栈 1 条现身条目");
            var 条目 = _sm.Entries[0];
            Assert.AreEqual(ResolveKind.AmbushReveal, 条目.Kind, "条目 Kind 应为 AmbushReveal");
            Assert.AreEqual(地区A, 条目.TargetAreaId, "TargetAreaId 应为原暗藏地区");
            Assert.IsTrue(条目.Targets.Exists(t => t.Kind == StackTargetRef.RefKind.Area && t.AreaUid == 地区A),
                "条目箭头 Targets 应指向原暗藏地区 areaA");
            Assert.AreEqual(卡.UID, 条目.SourceCardUid, "SourceCard 应为该暗藏者");

            // 进堆叠即翻面 + Model 层进入 Stack 区
            Assert.IsFalse(卡.IsCovert, "进堆叠瞬间应已翻面（IsCovert=false）");
            Assert.AreEqual(CardZone.Stack, 卡.CurrentZone, "进堆叠期间 Model 层卡应处于 Stack 区（离开争夺区）");

            // 结算：回地区 + EnterTrigger + UncoverTrigger
            int beforeEnter = _enterPlayCount;
            int beforeUncover = _uncoverCount;
            结算堆叠顶();

            Assert.AreEqual(CardZone.AreaZone, 卡.CurrentZone, "结算后卡应回到争夺区");
            Assert.AreEqual(地区A, 卡.ZoneDetailId, "结算后卡应回到原暗藏地区");
            Assert.IsFalse(卡.IsCovert, "结算后应保持正面");
            Assert.IsTrue(_enterPlayCount > beforeEnter, "回地区应触发进场触发（EnterTrigger 广播）");
            Assert.IsTrue(_uncoverCount > beforeUncover, "现身应广播 UncoverTrigger 信号");
        }

        // —— 用例2：无行动权拒绝 ——
        public void TestRevealCovert无行动权拒绝()
        {
            搭建测试环境();
            设优先权(2);     // 当前行动权在玩家2
            var 卡 = 创建暗藏者卡(1, "areaA", cost: 0);

            bool ok = _tm.RevealCovert(1, 卡.UID);
            Assert.IsFalse(ok, "玩家1 无行动权，应拒绝现身");
            Assert.AreEqual(0, _sm.Entries.Count, "不应入栈");
            Assert.IsTrue(卡.IsCovert, "被拒后卡应保持暗藏状态");
            Assert.AreEqual(CardZone.AreaZone, 卡.CurrentZone, "被拒后卡应仍处争夺区");
        }

        // —— 用例3：非暗藏者（正面卡）拒绝 ——
        public void TestRevealCovert非暗藏者拒绝()
        {
            搭建测试环境();
            var 卡数据 = new PlayerCardData { CardName = "正面角色", CategoryFlag = CardCategory.Character, Cost = 0 };
            var 卡 = _cm.CreateCardInstance(卡数据, 1);
            卡.ControllerId = 1;
            卡.CurrentZone = CardZone.AreaZone;
            卡.ZoneDetailId = "areaA";
            卡.IsCovert = false;          // 已经是正面，非暗藏

            bool ok = _tm.RevealCovert(1, 卡.UID);
            Assert.IsFalse(ok, "正面卡（非暗藏）不应能现身");
            Assert.AreEqual(0, _sm.Entries.Count, "不应入栈");
        }

        // —— 用例4：费用不足拒绝（不扣费、不入栈）——
        public void TestRevealCovert费用不足拒绝()
        {
            搭建测试环境();
            // 玩家1 无未横置资产 → 可用费用为 0；暗藏者 Cost=3 → 不足
            var 卡 = 创建暗藏者卡(1, "areaA", cost: 3);
            Assert.AreEqual(0, _pm.GetAvailableMana(1), "前置：玩家1 可用费用应为 0");

            bool ok = _tm.RevealCovert(1, 卡.UID);
            Assert.IsFalse(ok, "费用不足应拒绝现身");
            Assert.AreEqual(0, _sm.Entries.Count, "不应入栈");
            Assert.IsTrue(卡.IsCovert, "被拒后卡应保持暗藏状态");
            Assert.AreEqual(CardZone.AreaZone, 卡.CurrentZone, "被拒后卡不应进入 Stack 区");
        }

        // —— 用例5：控制者≠发动者拒绝（防第三方误触发）——
        public void TestRevealCovert控制者非发动者拒绝()
        {
            搭建测试环境();
            // 卡控制者为玩家2，但由玩家1 发动（玩家1 有行动权）
            设优先权(1);
            var 卡 = 创建暗藏者卡(2, "areaA", cost: 0);
            卡.ControllerId = 2;          // 控制者=玩家2
            // 需把卡移到玩家1 视角下的地区？地区无关控制者校验；关键 controller(2)!=playerId(1)
            bool ok = _tm.RevealCovert(1, 卡.UID);
            Assert.IsFalse(ok, "发动者非控制者，应拒绝现身（防第三方误触发）");
            Assert.AreEqual(0, _sm.Entries.Count, "不应入栈");
        }
    }
}
