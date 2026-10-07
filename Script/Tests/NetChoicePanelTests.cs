using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;
using Script.Net;

namespace Script.Tests
{
    /// <summary>
    /// 【第 3 版·决策面板对齐单机】协议与服务端流程验证：
    ///  1) 新增协议字段（PayAllowed / OptionForced / OrderContextId / OptionLabels / OptionTexts / SelectedIndices）往返；
    ///  2) 触发排序：服务端**按客户端回包的下标顺序**入堆叠（此前顺序被忽略），且"放弃剩余"生效；
    ///  3) 触发排序：仍有强制触发时**忽略取消并重新请求**（等价单机 Done 按钮禁用）；
    ///  4) PayOrTap：服务端下发 PayAllowed（权威）且回包 Pay 正确落地到 ResolvePrepareUpkeep；
    ///  5) 蒙版拖起资格静态函数（DeployCovertEligibility）与 DeployCovert 的拒绝条件一致；
    ///  6) 联机专用决策窗口场景可加载、导出节点引用可解析。
    /// </summary>
    public class NetChoicePanelTests : TestRunner
    {
        // ==================== 桩网关：按脚本回答 ====================

        private sealed class ScriptedGateway : IInputGateway
        {
            /// <summary>(请求, 第几次调用) → 回包；返回 null 视为"取消"。</summary>
            public System.Func<ChoiceRequest, int, ChoiceResult> Respond;
            public int Calls;
            public readonly List<ChoiceRequest> Requests = new List<ChoiceRequest>();

            public Task<ChoiceResult> RequestChoice(int playerId, ChoiceRequest req)
            {
                int n = Calls++;
                Requests.Add(req);
                var r = Respond?.Invoke(req, n);
                r ??= new ChoiceResult { Cancelled = true };
                r.RequestId = req?.RequestId;
                return Task.FromResult(r);
            }
        }

        // ==================== 1) 协议字段往返 ====================

        public void TestChoiceRequestAndResultNewFieldsRoundTrip()
        {
            var req = new ChoiceRequest
            {
                RequestId = "req_1",
                PlayerId = 2,
                RequestKind = ChoiceRequest.Kind.SelectTriggerOrder,
                Prompt = "玩家2 排序触发（2 个）",
                SourceUid = "src_1",
                PayAmount = 3,
                PayAllowed = true,
                ForceChoice = true,
                OrderContextId = "trig_2_5_1",
            };
            req.OptionUids.Add("cardA");
            req.OptionUids.Add("cardB");
            req.OptionForced.Add(true);
            req.OptionForced.Add(false);
            req.OptionLabels.Add("触发A");
            req.OptionLabels.Add("触发B");
            req.OptionTexts.Add("效果文本A");
            req.OptionTexts.Add("效果B");

            // 走真实链路：信封 v2 包裹 + JSON 序列化/反序列化
            string wire = NetEnvelope.Wrap(NetEnvelope.Types.ChoiceRequest, ChoiceSerializer.Serialize(req));
            var back = ChoiceSerializer.DeserializeRequest(NetEnvelope.UnwrapPayload(wire));

            Assert.IsNotNull(back, "ChoiceRequest 应能往返");
            Assert.AreEqual(true, back.PayAllowed, "PayAllowed 应往返（服务端权威的「是否支付得起」标记）");
            Assert.AreEqual("trig_2_5_1", back.OrderContextId, "OrderContextId 应往返（客户端据此复用排序窗口）");
            Assert.AreEqual(2, back.OptionForced.Count, "OptionForced 数量应与 OptionUids 同序一致");
            Assert.IsTrue(back.OptionForced[0], "第 1 项应为强制");
            Assert.IsFalse(back.OptionForced[1], "第 2 项应为非强制");
            Assert.AreEqual("触发A", back.OptionLabels[0], "OptionLabels 应往返（条目名）");
            Assert.AreEqual("效果B", back.OptionTexts[1], "OptionTexts 应往返（效果文本）");

            var res = new ChoiceResult { RequestId = "req_1", Cancelled = false };
            res.SelectedIndices.Add(2);
            res.SelectedIndices.Add(0);
            string wire2 = NetEnvelope.Wrap(NetEnvelope.Types.ChoiceResult, ChoiceSerializer.SerializeResult(res));
            var back2 = ChoiceSerializer.DeserializeResult(NetEnvelope.UnwrapPayload(wire2));

            Assert.IsNotNull(back2, "ChoiceResult 应能往返");
            Assert.AreEqual(2, back2.SelectedIndices.Count, "SelectedIndices 应往返（本次入堆叠的下标顺序）");
            Assert.AreEqual(2, back2.SelectedIndices[0], "下标顺序应保持（0 号元素）");
            Assert.AreEqual(0, back2.SelectedIndices[1], "下标顺序应保持（1 号元素）");
        }

        // ==================== 2/3) 触发排序：顺序应用 + 强制触发忽略取消 ====================

        /// <summary>构造一个"无目标、无 EffectData"的最简触发（EffectDataRef=null ⇒ 非强制）。</summary>
        private static StackEffectInstance NewTrigger(string uid, int controllerId, bool forced, string effectId)
        {
            var tmpl = new PlayerCardData { UID = "T_" + uid, CardName = "测试卡" + uid, CategoryFlag = CardCategory.Character };
            var src = new CardInstance(tmpl, "src_" + uid) { OwnerId = controllerId, ControllerId = controllerId, CurrentZone = CardZone.AreaZone };
            var e = new EffectData
            {
                EffectId = effectId,
                Forced = forced,
                FaqNote = "效果" + uid,
                // 显式声明"无需选目标"：EffectData.Target 缺省 NeedsSelection=true，
                // 否则会被 EnsureTargetThenPush 判为需目标触发（测试里没有合法目标 → 跳过入栈）。
                Target = new TargetSpec { NeedsSelection = false },
            };
            return new StackEffectInstance
            {
                UID = "trig_" + uid,
                Source = src,
                OwnerPlayerId = controllerId,
                ControllerId = controllerId,
                EffectDataRef = e,
            };
        }

        private static GameInitializer NewGame()
        {
            var game = new GameInitializer { DebugSeed = 1 };
            game.InitializeAll();
            return game;
        }

        /// <summary>轮询等待异步触发流程结束（RunTriggerPhaseDispatch 是 fire-and-forget）。</summary>
        private static bool WaitUntil(System.Func<bool> cond, int timeoutMs = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                System.Threading.Thread.Sleep(5);
            }
            return cond();
        }

        public void TestTriggerOrderAppliesClientChosenOrder()
        {
            var game = NewGame();
            var gateway = new ScriptedGateway();
            // 第 1 轮：本轮 3 个条目，客户端把"下标 1 先入堆栈，然后下标 0"（＝顺序 B,A），C 留在窗口
            // 第 2 轮：剩余 [C]，客户端"完成排序"（放弃剩余）
            gateway.Respond = (req, n) =>
            {
                if (n == 0)
                {
                    var r = new ChoiceResult { Cancelled = false };
                    r.SelectedIndices.Add(1);
                    r.SelectedIndices.Add(0);
                    return r;
                }
                return new ChoiceResult { Cancelled = true };
            };
            game.TurnManager.InputGateway = gateway;

            var bus = new GameEventBus();
            var registry = new TriggerRegistry(bus);
            game.TurnManager.SetTriggerSystem(registry, new TriggerEmitPoints(bus), bus);

            var triggers = new List<StackEffectInstance>
            {
                NewTrigger("A", 1, false, "effA"),
                NewTrigger("B", 1, false, "effB"),
                NewTrigger("C", 1, false, "effC"),
            };
            registry.RunTriggerPhase(triggers);

            // 轮数：第 1 轮入堆栈 2 个 → 剩余 [C] → 第 2 轮取消 → 结束（共 2 次请求）
            WaitUntil(() => gateway.Calls >= 2 && game.StackManager.Entries.Count >= 2);
            Assert.AreEqual(2, gateway.Calls, "应恰好两轮排序请求（先入堆栈 2 个，剩余 1 个再问一次）");

            var stack = game.StackManager.Entries;
            Assert.AreEqual(2, stack.Count, "入堆栈条目数应为 2（C 被放弃）");
            Assert.AreEqual("src_B", stack[0].SourceCardUid, "入堆栈顺序应按客户端回包的下标顺序：B 先");
            Assert.AreEqual("src_A", stack[1].SourceCardUid, "入堆栈顺序应按客户端回包的下标顺序：A 后");

            Assert.IsTrue((gateway.Requests[0].OrderContextId ?? "").StartsWith("trig_1_"),
                "OrderContextId 应形如 trig_<owner>_...");
            Assert.AreEqual(gateway.Requests[0].OrderContextId, gateway.Requests[1].OrderContextId,
                "同一 owner 的多轮请求应共用同一 OrderContextId（客户端据此复用同一窗口）");
            Assert.AreEqual(3, gateway.Requests[0].OptionUids.Count, "第 1 轮应下发 3 个候选");
            Assert.AreEqual(1, gateway.Requests[1].OptionUids.Count, "第 2 轮只应下发剩余 1 个候选");
        }

        public void TestTriggerOrderForcedIgnoresCancelAndReasks()
        {
            var game = NewGame();
            var gateway = new ScriptedGateway();
            gateway.Respond = (req, n) =>
            {
                switch (n)
                {
                    case 0: return new ChoiceResult { Cancelled = true };            // 有强制触发 → 服务端应忽略并重问
                    case 1: { var r = new ChoiceResult { Cancelled = false }; r.SelectedIndices.Add(0); return r; }  // 入堆栈 A
                    default: return new ChoiceResult { Cancelled = true };           // 剩余 [B] 无强制 → 放弃
                }
            };
            game.TurnManager.InputGateway = gateway;

            var bus = new GameEventBus();
            var registry = new TriggerRegistry(bus);
            game.TurnManager.SetTriggerSystem(registry, new TriggerEmitPoints(bus), bus);

            var triggers = new List<StackEffectInstance>
            {
                NewTrigger("A", 1, true, "effForced"),
                NewTrigger("B", 1, false, "effB"),
            };
            registry.RunTriggerPhase(triggers);

            WaitUntil(() => gateway.Calls >= 3 && game.StackManager.Entries.Count >= 1);
            Assert.AreEqual(3, gateway.Calls, "强制触发存在时取消应被忽略并重问（共 3 轮：取消被拒→入堆栈A→放弃B)");
            Assert.IsTrue(gateway.Requests[0].ForceChoice, "含强制触发时请求应带 ForceChoice=true");
            Assert.IsTrue(gateway.Requests[0].OptionForced[0], "第 1 项应标记为强制（客户端据此禁止拖入放弃区）");
            Assert.AreEqual(2, gateway.Requests[1].OptionUids.Count, "取消被忽略后仍应下发全部剩余候选");

            var stack = game.StackManager.Entries;
            Assert.AreEqual(1, stack.Count, "仅强制的 A 入堆栈，B 被放弃");
            Assert.AreEqual("src_A", stack[0].SourceCardUid, "入堆栈的应是强制触发 A");
        }

        // ==================== 4) PayOrTap：PayAllowed 下发 + 回包落地 ====================

        public void TestPayOrTapSendsPayAllowedAndAppliesChoice()
        {
            var game = NewGame();
            var gateway = new ScriptedGateway { Respond = (req, n) => new ChoiceResult { Cancelled = false, Pay = false } };

            var ee = new Script.Manager.EffectEngine();
            ee.TurnManagerRef = game.TurnManager;   // 供 CanPayPrepareUpkeep 读取可用费用
            ee.InputGateway = gateway;

            int before = game.TurnManager.PlayerManagerRef.GetAvailableMana(1);
            var eff = new EffectData
            {
                EffectId = "TEST_PrepareUpkeep",
                Timing = EffectTiming.OnTurnStarted,
                Scope = ZoneScope.AttachTarget,
                Target = new TargetSpec { NeedsSelection = false },
                Actions = new Array<EffectAction>
                {
                    new EffectAction { Verb = ActionVerb.PrepareUpkeep, Args = new Array<string> { "2" } }
                },
            };
            var tmpl = new PlayerCardData { UID = "T_src", CardName = "阿格斯测试卡", CategoryFlag = CardCategory.Character };
            var src = new CardInstance(tmpl, "src_pay") { OwnerId = 1, ControllerId = 1, CurrentZone = CardZone.AreaZone };

            var task = ee.ResolveEffectAsync(eff, src, null);
            task.Wait(5000);

            Assert.AreEqual(1, gateway.Calls, "准备维护应恰好发起一次 PayOrTap 请求");
            var req = gateway.Requests[0];
            Assert.AreEqual(ChoiceRequest.Kind.PayOrTap, req.RequestKind, "请求种类应为 PayOrTap");
            Assert.AreEqual(2, req.PayAmount, "PayAmount 应为效果参数 2");
            Assert.AreEqual(before >= 2, req.PayAllowed, "PayAllowed 应等于服务端权威判定（可用费用 >= PayAmount）");
            Assert.IsTrue(req.ForceChoice, "单机 PromptPayOrTap 恒为强制二选一（联机应一致）");

            Assert.IsTrue(src.IsTapped, "回包 Pay=false 应落地为「横置来源卡」（ResolvePrepareUpkeep）");
            Assert.AreEqual(before, game.TurnManager.PlayerManagerRef.GetAvailableMana(1), "未支付则费用不变");
        }

        // ==================== 5) 蒙版技能资格静态函数 ====================

        /// <summary>
        /// 【2026-09 用户实测·JC064】"支付 N，否则牺牲本卡"在联机侧走 `AskZeroOrOneViaGatewayAsync`
        /// （`Kind.NumberInput`，值域 0..1 + 两个按钮文案）⇒ 客户端**应渲染两个按钮，而不是输入框**
        /// （用户口径："JC064 支付费用的触发弹窗**不需要输入框**，参考 JC061"）。判定收口在
        /// `Script.UI.ChoicePromptPanel.IsYesNoChoice`（单一入口，`BuildNumberInput` 消费）。
        /// </summary>
        public void TestYesNoChoice_0到1带按钮文案应渲染两个按钮()
        {
            var req = new ChoiceRequest
            {
                RequestKind = ChoiceRequest.Kind.NumberInput,
                NumberMin = 0, NumberMax = 1, NumberDefault = 1,
            };
            req.OptionLabels.Add("支付 1");
            req.OptionLabels.Add("牺牲");
            Assert.IsTrue(Script.UI.ChoicePromptPanel.IsYesNoChoice(req), "0..1 + 两个按钮文案 ⇒ **是/否二选一**（渲染按钮，不渲染输入框）");

            var numReq = new ChoiceRequest { NumberMin = 0, NumberMax = 5, NumberDefault = 0 };
            numReq.OptionLabels.Add("支付");
            numReq.OptionLabels.Add("取消");
            Assert.IsFalse(Script.UI.ChoicePromptPanel.IsYesNoChoice(numReq), "值域 0..5 ⇒ 真数值输入，保留输入框");

            var noLabels = new ChoiceRequest { NumberMin = 0, NumberMax = 1, NumberDefault = 0 };
            Assert.IsFalse(Script.UI.ChoicePromptPanel.IsYesNoChoice(noLabels), "没有按钮文案 ⇒ 不当作二选一");

            var singleLabel = new ChoiceRequest { NumberMin = 0, NumberMax = 1, NumberDefault = 0 };
            singleLabel.OptionLabels.Add("确认");
            Assert.IsFalse(Script.UI.ChoicePromptPanel.IsYesNoChoice(singleLabel), "只有一个按钮文案 ⇒ 不当作二选一");
            Assert.IsFalse(Script.UI.ChoicePromptPanel.IsYesNoChoice(null), "空请求安全返回 false");
        }

        public void TestDeployCovertEligibilityRule()
        {
            var charTmpl = new PlayerCardData { UID = "T_char", CardName = "角色", CategoryFlag = CardCategory.Character };
            var charInst = new CardInstance(charTmpl, "c1") { ControllerId = 1 };
            Assert.IsTrue(DeployCovertEligibility.IsEligible(charInst), "普通角色卡应可派遣暗藏");

            var publicTmpl = new PlayerCardData { UID = "T_public", CardName = "公开角色", CategoryFlag = CardCategory.Character };
            publicTmpl.EffectDataList.Add(KeywordEffects.Public());
            var publicInst = new CardInstance(publicTmpl, "c2") { ControllerId = 1 };
            Assert.IsFalse(DeployCovertEligibility.IsEligible(publicInst), "带【公开】关键字的卡不可派遣暗藏");

            var sorceryTmpl = new PlayerCardData { UID = "T_sor", CardName = "事务", CategoryFlag = CardCategory.Sorcery };
            var sorceryInst = new CardInstance(sorceryTmpl, "c3") { ControllerId = 1 };
            Assert.IsFalse(DeployCovertEligibility.IsEligible(sorceryInst), "非角色卡不可派遣暗藏");
            Assert.IsFalse(DeployCovertEligibility.IsEligible(null), "空实例不可派遣暗藏");

            // 与 TurnManager.CanDeployCovert（实例方法，已委托同一静态函数）结论一致
            var game = NewGame();
            Assert.AreEqual(game.TurnManager.CanDeployCovert(charInst), DeployCovertEligibility.IsEligible(charInst),
                "CanDeployCovert 应与静态函数结论一致（单机行为等价）");
            Assert.AreEqual(game.TurnManager.CanDeployCovert(publicInst), DeployCovertEligibility.IsEligible(publicInst),
                "公开卡两者的结论也应一致");
        }

        // ==================== 6) 联机专用决策窗口场景可加载 ====================

        public void TestOnlineChoiceScenesLoadAndExportsResolve()
        {
            var triggerScene = ResourceLoader.Load<PackedScene>("res://Scenes/Net/OnlineTriggerOrderWindow.tscn");
            Assert.IsNotNull(triggerScene, "OnlineTriggerOrderWindow.tscn 应可加载（解析无错误）");
            var win = triggerScene.Instantiate<Script.UI.OnlineTriggerOrderWindow>();
            Assert.IsNotNull(win, "应能实例化为 OnlineTriggerOrderWindow");
            Assert.IsNotNull(win.OrderZoneContainer, "导出引用 OrderZoneContainer 应可解析");
            Assert.IsNotNull(win.DiscardZoneContainer, "导出引用 DiscardZoneContainer 应可解析");
            Assert.IsNull(win.GetNodeOrNull("RootControl/Panel/AllEnterButton"), "「全部进入堆叠」按钮已下线（场景中不应再有该节点）");
            Assert.IsNotNull(win.FirstEnterButton, "导出引用 FirstEnterButton 应可解析");
            Assert.IsNotNull(win.DoneButton, "导出引用 DoneButton 应可解析");
            Assert.AreEqual("完成排序并放弃下方效果", win.DoneButton.Text, "完成排序按钮文案应为「完成排序并放弃下方效果」");
            Assert.IsNotNull(win.GetNodeOrNull("RootControl"), "应存在 RootControl（TriggerOrderRoot 承载节点）");
            win.Free();

            var strengthScene = ResourceLoader.Load<PackedScene>("res://Scenes/Net/OnlineStrengthWindow.tscn");
            Assert.IsNotNull(strengthScene, "OnlineStrengthWindow.tscn 应可加载");

            var arenaScene = ResourceLoader.Load<PackedScene>("res://Scenes/Net/ArenaOl.tscn");
            Assert.IsNotNull(arenaScene, "ArenaOl.tscn 应可加载（解析无错误）");
        }

        // ==================== 7) 联机排序窗口自动关窗口径（双框全空） ====================

        /// <summary>
        /// 排序窗口自动关闭口径：**排序框与放弃框都为空**时静默关闭（仅 Hide、不回包）；
        /// 任一框还有内容则保持打开。拖拽/提交导致两框皆空时经 RefreshButtonStates 触发判定。
        /// </summary>
        public void TestOnlineTriggerOrderAutoCloseRequiresBothZonesEmpty()
        {
            var triggerScene = ResourceLoader.Load<PackedScene>("res://Scenes/Net/OnlineTriggerOrderWindow.tscn");
            Assert.IsNotNull(triggerScene, "OnlineTriggerOrderWindow.tscn 应可加载");
            var win = triggerScene.Instantiate<Script.UI.OnlineTriggerOrderWindow>();
            Assert.IsNotNull(win, "应能实例化为 OnlineTriggerOrderWindow");

            // 无条目（两框皆空）→ 拖拽变更刷新时即静默关闭，且不回调 OnDone（不回包）
            bool doneFired = false;
            win.OnDone = () => doneFired = true;
            win.RefreshButtonStates();
            Assert.IsFalse(win.Visible, "两框皆空时应自动关窗");
            Assert.IsFalse(doneFired, "静默自动关窗不应回包（不触发 OnDone）");

            // 排序框有内容 → 不关窗
            win.Show();
            var entry = new Godot.Control();
            win.OrderZoneContainer.AddChild(entry);
            win.RefreshButtonStates();
            Assert.IsTrue(win.Visible, "排序框非空时不应自动关窗");

            // 排序框清空、放弃框有内容 → 仍不应关窗
            win.OrderZoneContainer.RemoveChild(entry);
            entry.Free();
            var discarded = new Godot.Control();
            win.DiscardZoneContainer.AddChild(discarded);
            win.RefreshButtonStates();
            Assert.IsTrue(win.Visible, "放弃框非空时不应自动关窗");

            // 放弃框也清空 → 双框皆空 → 关窗
            win.DiscardZoneContainer.RemoveChild(discarded);
            discarded.Free();
            win.RefreshButtonStates();
            Assert.IsFalse(win.Visible, "双框皆空时应自动关窗");

            win.Free();
        }
    }
}
