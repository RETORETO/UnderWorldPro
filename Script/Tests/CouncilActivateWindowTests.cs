using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Cards.Script.CouncilCards.JC;

namespace Script.Tests
{
    /// <summary>
    /// 秘社激活窗口弹出条件（ActivateEligibility.CanActivate → CanActivateCouncil）逻辑正确性测试。
    /// 覆盖维度：控制者玩家(P1/P2)、优先权归属、先后手(FirstPlayerId)、堆叠是否为空、阶段(12 个 TurnPhase)。
    ///
    /// 弹出规则（与 CanEnterBasicActionMask 严格一致）：
    ///   ① 玩家持有优先权（PriorityPlayerId == 控制者）
    ///   ② 堆叠为空（!StackManager.IsEmpty）
    ///   ③ 处于“自己的行动步骤”：先手(FirstPlayerId==控制者)→ActionFirst；后手→ActionSecond
    ///   ④ 非原子锁定、费用充足、未横置（作为前置默认值，另有专门反例）
    /// </summary>
    public partial class CouncilActivateWindowTests : TestRunner
    {
        private static readonly TurnManager.TurnPhase[] AllPhases =
        {
            TurnManager.TurnPhase.StartReset,
            TurnManager.TurnPhase.StartPrepare,
            TurnManager.TurnPhase.StartDraw,
            TurnManager.TurnPhase.ActionFirst,
            TurnManager.TurnPhase.ActionSecond,
            TurnManager.TurnPhase.PreCombat,
            TurnManager.TurnPhase.CombatInvestigation,
            TurnManager.TurnPhase.CombatStrength,
            TurnManager.TurnPhase.CombatPower,
            TurnManager.TurnPhase.EndCleanup,
            TurnManager.TurnPhase.EndRecover,
            TurnManager.TurnPhase.EndPassTurn,
        };

        // ---------- 反射辅助：设置 private 属性/字段 ----------
        private static void SetMember(object obj, string name, object value)
        {
            var t = obj.GetType();
            // 1) 优先尝试可写属性（含 private setter 的自动属性）
            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                var setter = prop.GetSetMethod(true);   // true = 含非公开 setter
                if (setter != null)
                {
                    setter.Invoke(obj, new object[] { value });
                    return;
                }
            }
            // 2) 直接字段（含私有）
            var fld = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fld != null)
            {
                fld.SetValue(obj, value);
                return;
            }
            // 3) 只读属性回退到底层字段：_name / _首字母小写name / <name>k__BackingField
            string lowerFirst = "_" + char.ToLowerInvariant(name[0]) + name.Substring(1);
            foreach (var cand in new[] { "_" + name, lowerFirst, "<" + name + ">k__BackingField" })
            {
                var f2 = t.GetField(cand, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f2 != null)
                {
                    f2.SetValue(obj, value);
                    return;
                }
            }
            throw new Exception($"找不到可写成员 {name} @ {t.Name}");
        }

        // ---------- Mock 构造 ----------
        private StackManager NewStack(bool empty)
        {
            var sm = new StackManager();
            sm.Clear();
            if (!empty)
            {
                // 压入一条任意条目使堆叠非空
                var entry = new StackEntry(0, "测试效果") { SourceCardUid = "dummy" };
                sm.Push(entry);
            }
            return sm;
        }

        /// <summary>构造 PlayerManager，并向其注入一个含足够未横置资产卡的 ZoneManager，使 GetAvailableMana 返回大值（费用不构成瓶颈）。</summary>
        private PlayerManager NewPlayerManager(int manaPerPlayer)
        {
            var pm = new PlayerManager();
            // ZoneManager 用无参构造 + Initialize；引用仅用于内部存储，传 null 不影响 GetZoneCards。
            var zm = new ZoneManager();
            zm.Initialize(null, null);
            var assetDict = (System.Collections.Generic.Dictionary<int, List<CardInstance>>)GetField(zm, "_asset");
            for (int p = 0; p <= 1; p++)
            {
                var list = new List<CardInstance>();
                for (int i = 0; i < manaPerPlayer; i++)
                {
                    var inst = new CardInstance(new MSJC001_weimushouwang(), $"asset_{p}_{i}");
                    inst.OwnerId = p;
                    inst.ControllerId = p;
                    inst.IsTapped = false; // 未横置 → 计入可用费用
                    list.Add(inst);
                }
                assetDict[p] = list;
            }
            SetMember(pm, "_zoneManager", zm);
            return pm;
        }

        private static object GetField(object obj, string name)
        {
            var t = obj.GetType();
            var fld = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fld == null) throw new Exception($"找不到字段 {name} @ {t.Name}");
            return fld.GetValue(obj);
        }

        /// <summary>组装一个秘社实例（MSJC001，含两个行动窗口激活选项），可指定控制者与横置状态。</summary>
        private CardInstance NewCouncil(int controllerId, bool tapped)
        {
            var tmpl = new MSJC001_weimushouwang();
            var inst = new CardInstance(tmpl, "msjc_" + controllerId);
            inst.OwnerId = controllerId;
            inst.ControllerId = controllerId;
            inst.IsTapped = tapped;
            return inst;
        }

        /// <summary>预期计算：与 CanEnterBasicActionMask 严格对齐（未横置 / 费用充足 / 非原子锁前提下）。</summary>
        private static bool ExpectCanActivate(int controller, int firstPlayer, int priority, TurnManager.TurnPhase phase, bool stackEmpty)
        {
            if (priority != controller) return false;        // ① 必须持有优先权
            if (!stackEmpty) return false;                    // ② 堆叠必须空
            bool isFirst = (firstPlayer == controller);        // ③ 自己行动步骤
            bool inMyStep = isFirst
                ? phase == TurnManager.TurnPhase.ActionFirst
                : phase == TurnManager.TurnPhase.ActionSecond;
            return inMyStep;
        }

        // ---------- 主矩阵：穷举全部维度组合 ----------
        public void TestCouncilActivatePopupMatrix()
        {
            foreach (int controller in new[] { 0, 1 })        // P1 / P2
            foreach (int first in new[] { 0, 1 })              // 先后手
            foreach (int priority in new[] { 0, 1 })           // 优先权归属
            foreach (bool stackEmpty in new[] { true, false }) // 堆叠空 / 非空
            foreach (var phase in AllPhases)                   // 12 个阶段
            {
                string pName = controller == 0 ? "P1" : "P2";
                string label = $"控制者={pName},先手={(first == 0 ? "P1" : "P2")},优先权={(priority == 0 ? "P1" : "P2")},堆叠={(stackEmpty ? "空" : "非空")},阶段={phase}";

                var tm = new TurnManager();
                tm.SetStackManager(NewStack(stackEmpty));   // 公有方法，避免反射 StackManagerRef
                tm.Initialize(null, NewPlayerManager(8), null); // 充足费用
                SetMember(tm, "FirstPlayerId", first);
                SetMember(tm, "PriorityPlayerId", priority);
                SetMember(tm, "CurrentPhase", phase);

                var pm = tm.PlayerManagerRef;
                var cm = new CardManager();

                var inst = NewCouncil(controller, tapped: false);

                bool actual = ActivateEligibility.CanActivate(inst, tm, pm, cm);
                bool expect = ExpectCanActivate(controller, first, priority, phase, stackEmpty);
                Assert.AreEqual(expect, actual, $"[矩阵] {label} 期望={expect} 实际={actual}");
            }
        }

        // ---------- 显式反例（增强可读性） ----------

        /// <summary>堆叠非空时，即使其余条件全满足也不可弹出（用户规则：堆叠为空才能弹出）。</summary>
        public void TestStackNotEmptyBlocks()
        {
            var tm = new TurnManager();
            tm.SetStackManager(NewStack(empty: false));
            tm.Initialize(null, NewPlayerManager(8), null);
            SetMember(tm, "FirstPlayerId", 0);
            SetMember(tm, "PriorityPlayerId", 0);              // 控制权归 P1
            SetMember(tm, "CurrentPhase", TurnManager.TurnPhase.ActionFirst); // P1 先手→行动步骤
            var pm = tm.PlayerManagerRef;

            bool actual = ActivateEligibility.CanActivate(NewCouncil(0, false), tm, pm, new CardManager());
            Assert.IsFalse(actual, "堆叠非空时秘社激活窗口不应弹出");
        }

        /// <summary>玩家不持有优先权时不可弹出。</summary>
        public void TestPriorityMismatchBlocks()
        {
            var tm = new TurnManager();
            tm.SetStackManager(NewStack(empty: true));
            tm.Initialize(null, NewPlayerManager(8), null);
            SetMember(tm, "FirstPlayerId", 0);
            SetMember(tm, "PriorityPlayerId", 1);              // 优先权在 P2，但秘社属 P1
            SetMember(tm, "CurrentPhase", TurnManager.TurnPhase.ActionFirst);
            var pm = tm.PlayerManagerRef;

            bool actual = ActivateEligibility.CanActivate(NewCouncil(0, false), tm, pm, new CardManager());
            Assert.IsFalse(actual, "优先权不归控制者时秘社激活窗口不应弹出");
        }

        /// <summary>先手控制者仅在 ActionFirst 可弹出；ActionSecond 不可（后手步骤不是他的行动步骤）。</summary>
        public void TestFirstPlayerOnlyActionFirst()
        {
            foreach (var phase in AllPhases)
            {
                var tm = new TurnManager();
                tm.SetStackManager(NewStack(empty: true));
                tm.Initialize(null, NewPlayerManager(8), null);
                SetMember(tm, "FirstPlayerId", 0);             // P1 先手
                SetMember(tm, "PriorityPlayerId", 0);          // 优先权归 P1
                SetMember(tm, "CurrentPhase", phase);
                var pm = tm.PlayerManagerRef;

                bool actual = ActivateEligibility.CanActivate(NewCouncil(0, false), tm, pm, new CardManager());
                bool expect = phase == TurnManager.TurnPhase.ActionFirst;
                Assert.AreEqual(expect, actual, $"[先手] 阶段={phase} 期望={expect} 实际={actual}");
            }
        }

        /// <summary>后手控制者仅在 ActionSecond 可弹出；ActionFirst 不可。</summary>
        public void TestSecondPlayerOnlyActionSecond()
        {
            foreach (var phase in AllPhases)
            {
                var tm = new TurnManager();
                tm.SetStackManager(NewStack(empty: true));
                tm.Initialize(null, NewPlayerManager(8), null);
                SetMember(tm, "FirstPlayerId", 0);             // P1 先手 → P2 为后手
                SetMember(tm, "PriorityPlayerId", 1);          // 优先权归 P2（后手在 ActionSecond 优先）
                SetMember(tm, "CurrentPhase", phase);
                var pm = tm.PlayerManagerRef;

                bool actual = ActivateEligibility.CanActivate(NewCouncil(1, false), tm, pm, new CardManager());
                bool expect = phase == TurnManager.TurnPhase.ActionSecond;
                Assert.AreEqual(expect, actual, $"[后手] 阶段={phase} 期望={expect} 实际={actual}");
            }
        }

        /// <summary>秘社已横置时不可弹出（需横置本秘社作为代价，已横置则不满足）。</summary>
        public void TestTappedBlocks()
        {
            var tm = new TurnManager();
            tm.SetStackManager(NewStack(empty: true));
            tm.Initialize(null, NewPlayerManager(8), null);
            SetMember(tm, "FirstPlayerId", 0);
            SetMember(tm, "PriorityPlayerId", 0);
            SetMember(tm, "CurrentPhase", TurnManager.TurnPhase.ActionFirst);
            var pm = tm.PlayerManagerRef;

            bool actual = ActivateEligibility.CanActivate(NewCouncil(0, tapped: true), tm, pm, new CardManager());
            Assert.IsFalse(actual, "秘社已横置时激活窗口不应弹出");
        }

        /// <summary>费用不足时不可弹出。</summary>
        public void TestInsufficientManaBlocks()
        {
            var tm = new TurnManager();
            tm.SetStackManager(NewStack(empty: true));
            tm.Initialize(null, NewPlayerManager(0), null); // 0 张未横置资产 → 费用 0
            SetMember(tm, "FirstPlayerId", 0);
            SetMember(tm, "PriorityPlayerId", 0);
            SetMember(tm, "CurrentPhase", TurnManager.TurnPhase.ActionFirst);
            var pm = tm.PlayerManagerRef;

            bool actual = ActivateEligibility.CanActivate(NewCouncil(0, false), tm, pm, new CardManager());
            Assert.IsFalse(actual, "费用不足时激活窗口不应弹出");
        }

        /// <summary>原子锁定（如正在结算一个序列）期间不可弹出。</summary>
        public void TestAtomicLockBlocks()
        {
            var tm = new TurnManager();
            tm.SetStackManager(NewStack(empty: true));
            tm.Initialize(null, NewPlayerManager(8), null);
            SetMember(tm, "FirstPlayerId", 0);
            SetMember(tm, "PriorityPlayerId", 0);
            SetMember(tm, "CurrentPhase", TurnManager.TurnPhase.ActionFirst);
            tm.EnterAtomic(); // 原子操作进行中（模拟模态窗口已打开），真实置 _atomicLock=true
            var pm = tm.PlayerManagerRef;

            bool actual = ActivateEligibility.CanActivate(NewCouncil(0, false), tm, pm, new CardManager());
            Assert.IsFalse(actual, "原子锁定期间激活窗口不应弹出");
        }

        /// <summary>正常可弹出组合冒烟：P1 先手 + 优先权 P1 + 堆叠空 + ActionFirst。</summary>
        public void TestHappyPathPops()
        {
            var tm = new TurnManager();
            tm.SetStackManager(NewStack(empty: true));
            tm.Initialize(null, NewPlayerManager(8), null);
            SetMember(tm, "FirstPlayerId", 0);
            SetMember(tm, "PriorityPlayerId", 0);
            SetMember(tm, "CurrentPhase", TurnManager.TurnPhase.ActionFirst);
            var pm = tm.PlayerManagerRef;

            bool actual = ActivateEligibility.CanActivate(NewCouncil(0, false), tm, pm, new CardManager());
            Assert.IsTrue(actual, "正常条件（P1 先手/优先权/空堆叠/ActionFirst）下应弹出");
        }
    }
}
