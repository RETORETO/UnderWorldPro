using System.Collections.Generic;
using Godot;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10·触发窗口改造】单机触发排序窗口的 UI/交互契约验证：
    ///  1) 场景结构：单一效果列表 + 「全部入堆叠」「放弃剩余效果」两个按钮（旧的"第一张进入堆叠"与放弃区已下线）；
    ///  2) **未**实现批量能力的普通视图（全部测试桩）行为不受影响（照常入堆叠）；
    ///  3) 实现 <see cref="Script.Interfaces.ITriggerBatchView"/> 的视图会被注入「全部入堆叠」处理器，
    ///     且批量成功后按顺序全部入堆叠、`onFinished(true)`、已了结条目被回填移除；
    ///  4) 批量预选目标被取消 ⇒ **整体中止**：一条都不入堆叠、`onFinished(false)`、不移除任何条目。
    /// </summary>
    public class TriggerWindowBatchTests : TestRunner
    {
        // ==================== 视图桩 ====================

        /// <summary>只实现 ITriggerView（＝既有测试桩/headless 形态）：打开窗口即全部入堆叠，不等玩家。</summary>
        private sealed class PlainEnterAllView : Script.Interfaces.ITriggerView
        {
            public int Opened;
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                Opened++;
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }
            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            public void RemoveOrderEntries(List<StackEffectInstance> consumed) { }
        }

        /// <summary>实现 ITriggerView + ITriggerBatchView：**不**自动入堆叠，把两个回调留给测试驱动。</summary>
        private sealed class BatchCapableView : Script.Interfaces.ITriggerView, Script.Interfaces.ITriggerBatchView
        {
            public int Opened;
            public bool HandlerInjected;
            public System.Action<List<StackEffectInstance>, System.Action<bool>> Handler;
            public readonly List<List<StackEffectInstance>> Removed = new List<List<StackEffectInstance>>();
            /// <summary>true ＝ 取目标时"取消"（回包空 UID）；false ＝ 选第一个候选。</summary>
            public bool CancelTargetSelection;

            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
                => Opened++;   // 模拟"窗口已弹出，等玩家操作"

            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
            {
                if (CancelTargetSelection) { onPicked?.Invoke(null); return; }   // 取消 ⇒ 空 UID
                onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            }

            public void RemoveOrderEntries(List<StackEffectInstance> consumed) => Removed.Add(consumed);

            public void SetBatchEnterHandler(System.Action<List<StackEffectInstance>, System.Action<bool>> handler)
            {
                HandlerInjected = true;
                Handler = handler;
            }
        }

        // ==================== 辅助 ====================

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 1 };
            game.InitializeAll();
            return game;
        }

        /// <summary>最简触发：显式"无需选目标"（EffectData.Target 缺省 NeedsSelection=true，不显式关掉会被判为需目标）。</summary>
        private static StackEffectInstance NewTrigger(string uid, int controllerId, string effectId,
            bool needsTarget = false, bool isAreaTarget = false)
        {
            var tmpl = new PlayerCardData { UID = "T_" + uid, CardName = "测试卡" + uid, CategoryFlag = CardCategory.Character };
            var src = new CardInstance(tmpl, "src_" + uid)
            { OwnerId = controllerId, ControllerId = controllerId, CurrentZone = CardZone.AreaZone };
            var e = new EffectData
            {
                EffectId = effectId,
                FaqNote = "效果" + uid,
                Target = new TargetSpec { NeedsSelection = needsTarget },
            };
            return new StackEffectInstance
            {
                UID = "trig_" + uid,
                Source = src,
                OwnerPlayerId = controllerId,
                ControllerId = controllerId,
                EffectDataRef = e,
                IsAreaTarget = isAreaTarget,
            };
        }

        private static TriggerRegistry NewRegistry(GameInitializer game)
        {
            var bus = new GameEventBus();
            var registry = new TriggerRegistry(bus);
            game.TurnManager.SetTriggerSystem(registry, new TriggerEmitPoints(bus), bus);
            return registry;
        }

        // ==================== 1) 场景结构契约 ====================

        /// <summary>排序窗口场景：单一效果列表 + 「全部入堆叠」「放弃剩余效果」；放弃区与"第一张进入堆叠"已下线。</summary>
        public void TestTriggerOrderSceneNewLayoutContract()
        {
            var scene = ResourceLoader.Load<PackedScene>("res://Scenes/UI/TriggerOrderWindow.tscn");
            Assert.IsNotNull(scene, "TriggerOrderWindow.tscn 应可加载（解析无错误）");
            var win = scene.Instantiate<Script.UI.TriggerOrderWindow>();
            Assert.IsNotNull(win, "应能实例化为 TriggerOrderWindow");

            Assert.IsNotNull(win.OrderZoneContainer, "效果列表容器应可解析（OrderZoneContainer 指向单列表）");
            Assert.IsNull(win.DiscardZoneContainer, "单机新排序窗口不应再有放弃区（DiscardZoneContainer 应为 null）");
            Assert.IsNull(win.GetNodeOrNull("RootControl/Panel/FirstEnterButton"),
                "「第一张进入堆叠」按钮已下线（被双击入堆叠取代），场景中不应再有该节点");
            Assert.IsNull(win.GetNodeOrNull("RootControl/Panel/DiscardZone"),
                "放弃区域节点已下线（改为单列表）");

            Assert.IsNotNull(win.AllEnterButton, "「全部入堆叠」按钮应可解析");
            Assert.AreEqual("全部入堆叠", win.AllEnterButton.Text, "按钮文案应为「全部入堆叠」");
            Assert.IsNotNull(win.DoneButton, "「放弃剩余效果」按钮应可解析");
            Assert.AreEqual("放弃剩余效果", win.DoneButton.Text, "按钮文案应为「放弃剩余效果」");

            var root = win.GetNodeOrNull<Script.UI.TriggerOrderRoot>("RootControl");
            Assert.IsNotNull(root, "应存在 RootControl（TriggerOrderRoot 承载节点）");
            Assert.IsTrue(root.WrapEntries, "单机单列表应启用自动换行（多触发时不溢出）");
            win.Free();
        }

        // ==================== 2) 普通视图行为不变 ====================

        public void TestPlainViewStillEntersAllWhenNotBatchCapable()
        {
            var game = NewGame();
            var view = new PlainEnterAllView();
            game.TurnManager.SetTriggerView(view);
            var registry = NewRegistry(game);

            registry.RunTriggerPhase(new List<StackEffectInstance> { NewTrigger("A", 1, "effA") });

            Assert.AreEqual(1, view.Opened, "应对玩家1 打开一次排序窗口");
            Assert.AreEqual(1, game.StackManager.Entries.Count,
                "未实现 ITriggerBatchView 的视图应照常入堆栈（批量能力的引入不得改变既有行为）");
        }

        // ==================== 3) 批量成功：按序全部入堆叠 ====================

        public void TestBatchEnterAllPushesAllInOrder()
        {
            var game = NewGame();
            var view = new BatchCapableView();
            game.TurnManager.SetTriggerView(view);
            var registry = NewRegistry(game);

            var triggers = new List<StackEffectInstance>
            {
                NewTrigger("A", 1, "effA"),
                NewTrigger("B", 1, "effB"),
            };
            registry.RunTriggerPhase(triggers);

            Assert.AreEqual(1, view.Opened, "应打开一次排序窗口");
            Assert.IsTrue(view.HandlerInjected, "实现 ITriggerBatchView 的视图应被注入「全部入堆叠」处理器");
            Assert.IsNotNull(view.Handler, "处理器不应为 null");

            bool? finished = null;
            view.Handler(triggers, ok => finished = ok);

            Assert.IsTrue(finished.HasValue && finished.Value, "批量成功应回调 onFinished(true)");
            Assert.AreEqual(2, game.StackManager.Entries.Count, "两条触发都应入堆栈");
            Assert.AreEqual("src_A", game.StackManager.Entries[0].SourceCardUid, "应按窗口顺序（A 先）入堆栈");
            Assert.AreEqual("src_B", game.StackManager.Entries[1].SourceCardUid, "应按窗口顺序（B 后）入堆栈");
            Assert.AreEqual(1, view.Removed.Count, "批量结束后应回填一次已了结条目");
            Assert.AreEqual(2, view.Removed[0].Count, "已了结条目应为全部两条（窗口据此清空并自动关窗）");
        }

        // ==================== 4) 批量取消：整体中止、一条都不入堆叠 ====================

        public void TestBatchEnterAllAbortsAllWhenTargetSelectionCancelled()
        {
            var game = NewGame();
            var view = new BatchCapableView { CancelTargetSelection = true };
            game.TurnManager.SetTriggerView(view);
            var registry = NewRegistry(game);

            // 地区目标（机动类）的候选＝"其他争夺区"。默认对局场上无卡 ⇒ 地区表为空 ⇒ 会被判"无合法目标"而跳过，
            // 因此先放一张无效果的卡到某争夺区，保证候选非空、真正走到"预选取目标"这一步。
            var neighborTmpl = new PlayerCardData { UID = "T_neighbor", CardName = "邻居", CategoryFlag = CardCategory.Character };
            game.ZoneManager.MoveToArea(game.CardManager.CreateCardInstance(neighborTmpl, 1), "area_other", 1);

            var areaTrigger = NewTrigger("A", 1, "effArea", needsTarget: true, isAreaTarget: true);
            registry.RunTriggerPhase(new List<StackEffectInstance> { areaTrigger });

            Assert.IsTrue(view.HandlerInjected, "应被注入「全部入堆叠」处理器");

            int stackBefore = game.StackManager.Entries.Count;
            bool? finished = null;
            view.Handler(new List<StackEffectInstance> { areaTrigger }, ok => finished = ok);

            Assert.IsTrue(finished.HasValue && !finished.Value, "预选被取消应回调 onFinished(false)");
            Assert.AreEqual(stackBefore, game.StackManager.Entries.Count,
                "整体中止 ⇒ 一条触发都不得入堆栈（不产生部分入堆叠）");
            Assert.AreEqual(0, view.Removed.Count, "中止时不得回填移除条目（效果应保留在排序窗口内）");
            Assert.IsTrue(string.IsNullOrEmpty(areaTrigger.SelectedTargetUid), "取消后该触发不应留下已选目标");
        }
    }
}
