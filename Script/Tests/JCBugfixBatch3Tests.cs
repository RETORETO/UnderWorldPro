using System.Collections.Generic;
using Godot.Collections;
using Script.Data.Cards;
using Script.Data.Effects;
using Script.Enums;
using Script.Game.Entities;
using Script.Manager;

namespace Script.Tests
{
    /// <summary>
    /// 【2026-10 实测反馈·第五批（单机复现；联机同源的缺陷一并修）】4 条用户实测缺陷的回归单测：
    ///  ① **JC098 陷入疯狂的人群**：卡文"持续：本地区略过调查对抗步骤"＝**该步骤在流程里直接不存在**
    ///     （用户原文："需要跳过调查对抗步骤，不是调查对抗这件事，相当于流程里直接没有这个步骤"）——
    ///     例：1 地区有本卡 ⇒ 机动轮后直接进 1 地区战斗步骤；本卡在 2 地区 ⇒ 1 地区势力步骤后直接进 2 地区战斗步骤；
    ///  ② **JC100 尖牙幻象**：`-1 防御` 的费用必须在**发动能力时**（入堆叠之前）支付，不是效果处理时支付；
    ///     且减防致死后必须走死亡流程（UI 随区域移动事件归位刷新）；
    ///  ③ **JC107 崩塌的时空**：目标可以是**附属或资产**（资产卡此前"高亮却点不动"）；
    ///  ④ **JC108 乔治·帕佐，织梦人**：目标角色**含自身**（此前"无法选择自己为目标"）。
    ///
    /// 说明：纯 UI 交互（鼠标拖拽落点、点击命中）在 headless 下不可点，故对"数据/门禁/流程/落点"做行为断言，
    /// 对"UI 接线"另由 `JCEffectSelfAuditTests.TestAudit_承诺落地清单` 的源码级承诺钉住（判据㉖）。
    /// </summary>
    public class JCBugfixBatch3Tests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        // ==================== 基础工具 ====================

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 131 };
            game.InitializeAll();
            game.StartMatch();
            game.PlayerActionService.SetupInitialAreas(areas);
            return game;
        }

        private static PlayerCardData Load(string fileName)
        {
            Cards.Effects.AllEffects.RegisterAll();
            var tmpl = Godot.ResourceLoader.Load<PlayerCardData>(Dir + fileName + ".tres");
            if (tmpl != null) CardEffectFactory.EnsureEffects(tmpl);
            return tmpl;
        }

        private static List<string> Areas(GameInitializer game) => game.AreaManager.GetOrderedAreaUids();

        private static string FirstArea(GameInitializer game) => Areas(game)[0];

        /// <summary>探针（角色/附属/资产三类都由它扮演；数值按需指定）。</summary>
        private static PlayerCardData Probe(string uid, int defense = 2, int investigation = 0, int power = 0,
            int strength = 0, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = defense,
                Investigation = investigation, Power = power, Strength = strength,
            };
            tmpl.SubCategories.Add("人类");
            tmpl._effectsInjected = true;
            return tmpl;
        }

        private static CardInstance OnField(GameInitializer game, PlayerCardData tmpl, int playerId, string area)
        {
            var inst = game.CardManager.CreateCardInstance(tmpl, playerId);
            game.ZoneManager.MoveToArea(inst, area, playerId);
            return inst;
        }

        /// <summary>给某玩家若干资产（提供可用费用）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = new PlayerCardData
                {
                    UID = $"TS_b3_asset_{playerId}_{i}", CardName = "探针资产",
                    Cost = 0, CategoryFlag = CardCategory.Attachment, Defense = -1,
                };
                tmpl._effectsInjected = true;
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        /// <summary>连续让过直到堆叠清空。</summary>
        private static void Drain(GameInitializer game)
        {
            for (int i = 0; i < 60 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>连续让过直到抵达目标步骤（用于对抗阶段的流程断言）。</summary>
        private static void AdvanceTo(GameInitializer game, global::TurnManager.TurnPhase target, int maxPasses = 120)
        {
            for (int i = 0; i < maxPasses && game.TurnManager.CurrentPhase != target; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList) if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>触发排序视图桩：全部触发直接入堆叠（不排序、不弹窗）。</summary>
        private sealed class 直接入栈触发视图 : Script.Interfaces.ITriggerView
        {
            public readonly List<StackEffectInstance> Collected = new List<StackEffectInstance>();

            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<StackEffectInstance> triggers,
                System.Action<List<StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                if (triggers != null) Collected.AddRange(triggers);
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }

            public void RequestTargetSelection(StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked) => onPicked?.Invoke(null);

            public void RemoveOrderEntries(List<StackEffectInstance> entries) { }
        }

        /// <summary>UI 钩子桩：记录"是否支付 N"（二选一窗，`Pay` 决定回包）。</summary>
        private sealed class 支付钩子 : Script.Interfaces.IGameUIHooks
        {
            public bool Pay = true;
            public int OptionalPayCalls;
            public int LastAmount = -1;
            public int NumberCalls;      // 数值输入框调用次数（应为 0：本卡是"是/否"）

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
            public void PromptOptionalPay(int chooserPlayerId, string prompt, int amount, System.Action<bool> onChosen)
            {
                OptionalPayCalls++;
                LastAmount = amount;
                onChosen?.Invoke(Pay);
            }
            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                NumberCalls++;
                onChosen?.Invoke(defaultValue);
            }
            public void PromptDiscard(int discarderId, int chooserPlayerId, List<CardInstance> candidates,
                System.Action<CardInstance> onPicked) { }
            public void PromptPickCard(int chooserPlayerId, string prompt, List<CardInstance> candidates,
                bool allowCancel, System.Action<CardInstance> onPicked)
                => onPicked?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : null);
            public void PromptCurbMarks(int curbedPlayerId, int remaining,
                List<Script.Interfaces.CurbMarkOption> options, System.Action<string> onPickedAreaUid) { }
            public void PromptPrediction(int predictorId, int count, List<CardInstance> cards,
                System.Action<List<CardInstance>, List<CardInstance>> onConfirmed) { }
            public void PromptMultiTargets(CardInstance source, EffectData data,
                System.Func<int, List<TargetChoice>, SlotCandidates> candidatesForSlot,
                System.Action<List<TargetChoice>> onConfirmed) { }
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, List<int> candidates,
                System.Action<int> onChosen) => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);
            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(0);
        }

        /// <summary>订阅"对抗窗口请求"并计数（某地区某步骤真的结算了对抗 ⇒ 会开窗）。</summary>
        private sealed class 对抗窗口计数
        {
            public int Investigation;
            public int Strength;
            public int Power;
            private readonly GameInitializer _game;

            public 对抗窗口计数(GameInitializer game)
            {
                _game = game;
                _game.CombatManager.OnCombatWindowRequired += OnWindow;
            }

            private void OnWindow(CombatManager.CombatType type, int winner, int diff, int areaNo, System.Action onResolved)
            {
                if (type == CombatManager.CombatType.Investigation) Investigation++;
                else if (type == CombatManager.CombatType.Strength) Strength++;
                else Power++;
                onResolved?.Invoke();   // 无 UI：立即关窗，流程继续
            }

            public void Detach() => _game.CombatManager.OnCombatWindowRequired -= OnWindow;
        }

        // ==================== ① JC098：被略过的步骤在流程里直接不存在 ====================

        /// <summary>
        /// 【用户口径·① 场景一】本卡在 **1 地区**：机动轮（对抗前优先权窗口）双方让过后**直接进入
        /// 1 地区战斗步骤**——调查步骤完全不进入（不开对抗窗口、不分配行动权、UI 不停留）；
        /// 战斗/势力两步照常结算（"只略过调查"不能变成"三步全略过"）。
        /// </summary>
        public void TestB3_1A_JC098在1地区_机动轮后直接进1地区战斗步骤()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);
            OnField(game, Probe("TS_b3_098_p1", investigation: 3, power: 3, strength: 3), 1, areas[0]);
            OnField(game, Probe("TS_b3_098_p2", investigation: 0, power: 1, strength: 1), 2, areas[0]);
            var counter = new 对抗窗口计数(game);

            // 机动轮：双方让过 → 推进到"该地区第一个未被略过的对抗步骤"
            AdvanceTo(game, global::TurnManager.TurnPhase.PreCombat);
            Assert.AreEqual(global::TurnManager.TurnPhase.PreCombat, game.TurnManager.CurrentPhase,
                "前置：已到机动轮（对抗前优先权窗口）");
            game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
            game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(global::TurnManager.TurnPhase.CombatStrength, game.TurnManager.CurrentPhase,
                "JC098 在 1 地区 ⇒ 机动轮后**直接进入 1 地区战斗步骤**（调查步骤在流程里不存在）");
            Assert.AreEqual(1, game.TurnManager.CurrentCombatAreaNo, "仍在 1 地区");
            Assert.AreEqual(0, counter.Investigation, "调查步骤不进入 ⇒ 调查对抗窗口从未打开");
            Assert.AreEqual(1, counter.Strength, "战斗步骤照常结算（跳过的是『调查』这一步，不是整个对抗阶段）");

            // 1 地区战斗步骤之后：势力步骤照常（不是直接跳到下一地区）
            int markBefore = game.AreaManager.GetPowerMark(areas[0], 1);
            AdvanceTo(game, global::TurnManager.TurnPhase.CombatPower);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, game.TurnManager.CurrentPhase, "1 地区势力步骤照常进入");
            Assert.AreEqual(markBefore + 2, game.AreaManager.GetPowerMark(areas[0], 1),
                "势力对抗照常结算（3:1 ⇒ +2 势力标志；势力对抗为瞬时结算，不开交互窗口）");
            Assert.AreEqual(0, counter.Investigation, "全程调查对抗窗口数为 0（跳过的是调查步骤，不是整个对抗阶段）");
            counter.Detach();
        }

        /// <summary>
        /// 【用户口径·① 场景二】本卡在 **2 地区**：1 地区三步照常结算（1 地区调查照常开窗），
        /// 1 地区势力步骤后**直接进入 2 地区战斗步骤**（2 地区调查步骤在流程里不存在）。
        /// </summary>
        public void TestB3_1B_JC098在2地区_1地区势力后直接进2地区战斗步骤()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[1]);   // 本卡在 2 地区
            OnField(game, Probe("TS_b3_098b_p1", investigation: 3, power: 3, strength: 3), 1, areas[0]);
            OnField(game, Probe("TS_b3_098b_p2", investigation: 0, power: 1, strength: 1), 2, areas[0]);
            // 2 地区也放双方角色：调查/战斗都本会分出胜者 ⇒ 能真正验证"调查步骤被跳过、战斗步骤照常"
            OnField(game, Probe("TS_b3_098b_q1", investigation: 3, power: 3, strength: 3), 1, areas[1]);
            OnField(game, Probe("TS_b3_098b_q2", investigation: 0, power: 1, strength: 1), 2, areas[1]);
            var counter = new 对抗窗口计数(game);

            AdvanceTo(game, global::TurnManager.TurnPhase.CombatPower);
            Assert.AreEqual(1, game.TurnManager.CurrentCombatAreaNo, "前置：1 地区势力步骤");
            Assert.AreEqual(1, counter.Investigation, "1 地区调查步骤照常结算（略过按地区各自生效）");

            AdvanceTo(game, global::TurnManager.TurnPhase.CombatStrength);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatStrength, game.TurnManager.CurrentPhase,
                "JC098 在 2 地区 ⇒ 1 地区势力步骤后**直接进入 2 地区战斗步骤**");
            Assert.AreEqual(2, game.TurnManager.CurrentCombatAreaNo, "地区游标已推进到 2 地区");
            Assert.AreEqual(1, counter.Investigation, "2 地区调查步骤不进入 ⇒ 调查对抗窗口数不增加");
            Assert.AreEqual(2, counter.Strength, "2 地区战斗步骤照常开窗（1 地区 1 次 + 2 地区 1 次）");
            counter.Detach();
        }

        /// <summary>
        /// 【用户口径·① 补充】"略过"可只针对**中间那一步**（JC118 停战协议可指定清单）：
        /// 2 地区只略过"战斗" ⇒ 调查照常、势力直接接续。
        /// 这同时锁住 `NextCombatStepOfArea(fromStep)` 的**逐步骤筛选**（不是"整地区一刀切"）。
        /// </summary>
        public void TestB3_1C_只略过战斗步骤_调查照常势力直接接续()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Probe("TS_b3_098c_p1", investigation: 3, power: 3), 1, areas[0]);
            OnField(game, Probe("TS_b3_098c_p2", investigation: 0, power: 1), 2, areas[0]);
            EffectEngine.Instance.RegisterAreaCombatSkipThisTurn(areas[0], new List<string> { "战斗" });
            Assert.IsTrue(EffectEngine.Instance.AreaSkipsCombat(areas[0], "战斗"), "前置：本回合略过『战斗』");
            var counter = new 对抗窗口计数(game);

            AdvanceTo(game, global::TurnManager.TurnPhase.CombatInvestigation);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatInvestigation, game.TurnManager.CurrentPhase,
                "只略过战斗 ⇒ 调查步骤照常进入");
            Assert.AreEqual(1, counter.Investigation, "调查对抗照常结算");
            Assert.AreEqual(0, counter.Strength, "战斗步骤在流程里不存在（尚未开窗）");

            AdvanceTo(game, global::TurnManager.TurnPhase.CombatPower);
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatPower, game.TurnManager.CurrentPhase,
                "调查步骤后**直接进入势力步骤**（战斗步骤被跳过）");
            Assert.AreEqual(0, counter.Strength, "战斗对抗窗口从未打开");
            counter.Detach();
        }

        /// <summary>
        /// 【兜底】旁路入口（PM `jumpto` / 直接 `EnterPhase`）进到被略过的步骤时**不结算对抗**，
        /// 只给让过窗口（状态机不被撕开）——正常流程根本不进入该步骤（见上三条）。
        /// </summary>
        public void TestB3_1D_旁路进入被略过步骤仍不结算()
        {
            var game = NewGame();
            var areas = Areas(game);
            OnField(game, Load("JC098_xianrufengkuangderenqun"), 1, areas[0]);
            OnField(game, Probe("TS_b3_098d_p1", investigation: 3), 1, areas[0]);
            OnField(game, Probe("TS_b3_098d_p2", investigation: 0), 2, areas[0]);
            var counter = new 对抗窗口计数(game);

            Assert.IsTrue(game.TurnManager.JumpToPhase(global::TurnManager.TurnPhase.CombatInvestigation, 1, out string err),
                $"前置：jumpto 1 地区调查步骤成功（{err}）");
            Assert.AreEqual(global::TurnManager.TurnPhase.CombatInvestigation, game.TurnManager.CurrentPhase,
                "显式跳转照常进入该步骤（调试通道）");
            Assert.AreEqual(0, counter.Investigation, "被略过的地区**不结算调查对抗**（兜底门禁仍在）");
            counter.Detach();
        }

        // ==================== ② JC100：发动时付费 + 减防致死刷 UI ====================

        /// <summary>【结构】"支付1"必须是**发动时**的可选费用（`OptionalPayCost`），不再占结算动作位。</summary>
        public void TestB3_2A_JC100结构_发动时付费()
        {
            var tmpl = Load("JC100_jianyahuanxiang");
            var e = FindEffect(tmpl, "JC100_3_PayToWeakenSealingHost");
            Assert.IsNotNull(e, "JC100 应有『被封印时：可支付1 → 宿主 -1 防御力』效果");
            Assert.AreEqual(EffectTiming.OnSealed, e.Timing, "时机＝被封印时");
            Assert.AreEqual(1, e.OptionalPayCost, "费用＝**发动时的可选固定费用 1**（入堆叠前问『是否支付 1』）");
            Assert.AreEqual(1, e.Actions.Count, "动作只剩『-1 防御力』（支付不再占结算动作位）");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[0].Verb, "动作＝获得（负值＝削减上限）");
            Assert.AreEqual("防御", e.Actions[0].Args[0], "属性＝防御");
            Assert.AreEqual("-1", e.Actions[0].Args[1], "数量＝-1");
            Assert.AreEqual("封印宿主", e.Actions[0].Args[2], "主体＝封印宿主（卡文『该角色』）");
        }

        /// <summary>
        /// 【行为·BUG1】支付发生在**入堆叠之前**：`SealCard` 一趟走完（收集 → 问支付 → 入堆叠）后
        ///  · 已问过一次"是否支付 1"（二选一窗，不是数值输入框）；
        ///  · 费用**已扣**；
        ///  · 堆叠已有条目、而**效果尚未结算**（宿主防御还没降）——这正是"发动能力时就付费"。
        /// </summary>
        public void TestB3_2B_JC100支付在入堆叠前_效果尚未结算()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 2);
            var host = OnField(game, Probe("TS_b3_100_host", defense: 2), 1, area);
            var jc100 = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToHand(jc100, 1);

            var hooks = new 支付钩子 { Pay = true };
            EffectEngine.Instance.UIHooks = hooks;
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            Assert.IsTrue(game.TurnManager.SealCard(jc100, host), "前置：封印成功（广播 OnSealed）");

            Assert.AreEqual(1, hooks.OptionalPayCalls, "入堆叠前问过一次『是否支付 1』");
            Assert.AreEqual(1, hooks.LastAmount, "金额＝1");
            Assert.AreEqual(0, hooks.NumberCalls, "**不是数值输入框**（是/否二选一）");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "费用在**发动时**（入堆叠前）已扣清");
            Assert.IsFalse(game.StackManager.IsEmpty, "支付后该触发正常入堆叠");

            // 关键：此刻只是"已付费 + 已入堆叠"，效果还没结算 ⇒ 防御未变
            Assert.AreEqual(2, host.CurrentDefense, "付费发生在发动时；效果尚未结算 ⇒ 防御仍是 2");

            Drain(game);
            Assert.AreEqual(1, host.CurrentDefense, "结算后宿主 -1 防御力");
            Assert.AreEqual(1, hooks.OptionalPayCalls, "结算期间**不再**问一次支付（费用只在发动时问）");
        }

        /// <summary>【行为·BUG1 反面】不支付 ⇒ 该触发不入堆叠、不扣费、宿主防御不变（放弃整条效果）。</summary>
        public void TestB3_2C_JC100不支付_不入堆叠且不扣费()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 2);
            var host = OnField(game, Probe("TS_b3_100_host2", defense: 2), 1, area);
            var jc100 = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToHand(jc100, 1);

            var hooks = new 支付钩子 { Pay = false };
            EffectEngine.Instance.UIHooks = hooks;
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            game.TurnManager.SealCard(jc100, host);

            Assert.AreEqual(1, hooks.OptionalPayCalls, "仍然问过一次『是否支付 1』");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不支付 ⇒ 不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "不支付 ⇒ **该触发不入堆栈**（整条效果不结算）");
            Assert.AreEqual(2, host.CurrentDefense, "宿主防御不变");
        }

        /// <summary>
        /// 【行为·BUG2】减防导致死亡时**必须走死亡流程**（判据㉚：数值下降 ⇒ 复检派生状态）：
        /// 1 防御的封印宿主被 -1 防御后防御为 0 ⇒ 死亡 ⇒ 卡离场（进墓地）并广播**区域移动事件**
        /// —— 单机 UI 正是订阅该事件把卡面归位刷新（`Arena.SyncCardUIToZone`），
        /// 因此"死亡+移动事件"就是"UI 会刷新"的可执行代理断言。
        /// </summary>
        public void TestB3_2D_JC100减防致死_走死亡流程并广播移动事件()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 2);
            var host = OnField(game, Probe("TS_b3_100_die", defense: 1), 1, area);
            var jc100 = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToHand(jc100, 1);

            var moves = new List<(string uid, CardZone zone)>();
            game.ZoneManager.OnCardMoved += (card, zone, pid, detail) =>
            {
                if (card != null) moves.Add((card.UID, zone));
            };
            // 死亡瞬间的数值快照（`ResolveDeath` 先广播再离场；离场会复位实例数值 ⇒ 必须在此刻抓）
            int defenseAtDeath = -99, damageAtDeath = -99;
            game.CardManager.OnCardDied += c =>
            {
                if (c == host) { defenseAtDeath = c.CurrentDefense; damageAtDeath = c.Damage; }
            };

            EffectEngine.Instance.UIHooks = new 支付钩子 { Pay = true };
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());

            game.TurnManager.SealCard(jc100, host);
            Drain(game);

            Assert.AreEqual(CardZone.Graveyard, host.CurrentZone, "防御 1 未受伤 −1 上限 ⇒ 上限 0 ⇒ **死亡**（不再『留在场上不刷新』）");
            Assert.AreEqual(0, defenseAtDeath, "死亡瞬间上限＝0（减防确实把上限削到 0）");
            Assert.AreEqual(0, damageAtDeath, "上限削减**不产生伤害**（伤害仍为 0；这是『剩余生命＝上限−伤害』口径）");
            Assert.IsTrue(moves.Exists(m => m.uid == host.UID && m.zone == CardZone.Graveyard),
                "死亡产生一条『移动到墓地』事件（单机 UI 据此归位刷新；此前减防不判死 ⇒ 既无死亡也无该事件）");
        }

        /// <summary>【行为·BUG2 防误杀】3 防御未受伤的宿主：-1 防御后防御 2 ⇒ 存活、不广播死亡移动。</summary>
        public void TestB3_2E_JC100减防不误杀_防御3未受伤()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 2);
            var host = OnField(game, Probe("TS_b3_100_alive", defense: 3), 1, area);
            var jc100 = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToHand(jc100, 1);

            EffectEngine.Instance.UIHooks = new 支付钩子 { Pay = true };
            game.TurnManager.SetTriggerView(new 直接入栈触发视图());

            game.TurnManager.SealCard(jc100, host);
            Drain(game);

            Assert.AreEqual(CardZone.AreaZone, host.CurrentZone, "3 防御未受伤 −1 ⇒ 不死亡");
            Assert.AreEqual(2, host.CurrentDefense, "防御 3-1＝2");
        }

        // ==================== ③ JC107：目标含附属与资产 ====================

        /// <summary>
        /// 【行为·BUG3】JC107"消灭目标附属或资产"：**附属与资产都是合法目标**（OR 两大类），
        /// 而普通角色不是。资产卡此前在单机"命中检测"范围之外 ⇒ 高亮却点不动；
        /// 本用例锁规则侧（候选集合包含资产），UI 命中范围由㉖承诺钉住。
        /// </summary>
        public void TestB3_3A_JC107目标含附属与资产()
        {
            var game = NewGame();
            string area = FirstArea(game);

            var jc107 = game.CardManager.CreateCardInstance(Load("JC107_bengtadeshikong"), 1);
            game.ZoneManager.MoveToHand(jc107, 1);

            // 目标①：结附在角色上的附属
            var host = OnField(game, Probe("TS_b3_107_host", defense: 3), 2, area);
            var att = game.CardManager.CreateCardInstance(
                Probe("TS_b3_107_att", defense: -1, category: CardCategory.Attachment), 2);
            game.ZoneManager.AttachAndRegister(att, host);
            Assert.AreEqual(CardZone.AttachedZone, att.CurrentZone, "前置：附属在附属区");

            // 目标②：资产区的资产
            var asset = game.CardManager.CreateCardInstance(
                Probe("TS_b3_107_asset", defense: -1, category: CardCategory.Attachment), 1);
            game.ZoneManager.MoveToAsset(asset, 1);
            Assert.AreEqual(CardZone.AssetZone, asset.CurrentZone, "前置：资产在资产区");

            // 对照：普通角色（大类不符）
            var plain = OnField(game, Probe("TS_b3_107_plain", defense: 2), 1, area);

            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(jc107, att), "附属是合法目标");
            Assert.IsTrue(game.CardManager.IsLegalPlayTarget(jc107, asset), "**资产是合法目标**（此前点不动）");
            Assert.IsFalse(game.CardManager.IsLegalPlayTarget(jc107, plain), "普通角色不是合法目标（只消灭附属或资产）");

            // 结算侧同源：消灭资产真的生效
            var effect = FindEffect(jc107.PlayerTemplate, "JC107_1_DestroyAttachmentOrAsset");
            Assert.IsNotNull(effect, "JC107 应有『消灭目标附属或资产』效果");
            Assert.AreEqual(TargetKind.Asset, effect.Target.KindAlt, "效果目标第二大类＝资产（与附属 OR）");
            EffectEngine.Instance.ResolveEffect(effect, jc107, asset, null);
            Assert.AreEqual(CardZone.Graveyard, asset.CurrentZone, "以资产为目标结算 ⇒ 资产被消灭");
        }

        // ==================== ④ JC108：目标角色含自身 ====================

        /// <summary>
        /// 【行为·BUG4】JC108"将你牌库顶的一张牌封印在目标角色上"：卡文无"另一个/其他"排除措辞，
        /// 且被封印的是**牌库顶的牌**（不是本卡自己）⇒ **本卡自己是合法目标**：
        ///  · `Target.CanTargetSelf = true`；
        ///  · `IsLegalActivateTarget(自身→自身)` 成立；
        ///  · 本地区只有它自己时 `HasLegalTarget` 仍成立（候选不为空 ⇒ 能力可发动）。
        /// </summary>
        public void TestB3_4A_JC108自身是合法目标()
        {
            var game = NewGame();
            string area = FirstArea(game);
            var jc108 = OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, area);

            var ability = ActivateEligibility.GetFirstActivateAbility(jc108);
            Assert.IsNotNull(ability, "JC108 应有激活能力");
            Assert.IsTrue(ability.Target.CanTargetSelf, "目标角色**含自身**（卡文未排除自身；封印的是牌库顶的牌）");
            Assert.IsTrue(ActivateEligibility.IsLegalActivateTarget(jc108, ability, jc108,
                game.CardManager, game.PlayerManager), "以自身为目标合法");
            Assert.IsTrue(ActivateEligibility.HasLegalTarget(jc108, ability, game.CardManager, game.PlayerManager),
                "本地区只有本卡时仍存在合法目标（自身）⇒ 能力可发动/可高亮");
        }

        /// <summary>
        /// 【行为·BUG4 端到端】以**自身**为目标发动 JC108：费用/横置代价照付，
        /// 牌库顶的一张牌被封印在**本卡自己**身上（`SealedBy == 本卡`）。
        /// </summary>
        public void TestB3_4B_JC108以自身为目标发动_牌库顶牌封在自身()
        {
            var game = NewGame();
            string area = FirstArea(game);
            GiveAssets(game, 1, 2);
            game.TurnManager.PriorityPlayerId = 1;

            var jc108 = OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, area);
            var top = game.CardManager.CreateCardInstance(Probe("TS_b3_108_top", defense: 2), 1);
            game.ZoneManager.MoveToDeckTop(top, 1, toTop: true);

            var ability = ActivateEligibility.GetFirstActivateAbility(jc108);
            Assert.IsTrue(ActivateEligibility.CanActivate(jc108, game.TurnManager, game.PlayerManager, game.CardManager),
                "前置：能力可发动（自身即合法目标）");

            EffectEngine.Instance.ActivateAbility(jc108, ability, jc108, -1);
            Drain(game);

            Assert.IsTrue(jc108.IsTapped, "横置自身（发动代价）");
            Assert.IsTrue(jc108.SealedCards != null && jc108.SealedCards.Count == 1,
                "牌库顶的一张牌被封在**本卡自己**身上");
            Assert.AreEqual(jc108, jc108.SealedCards[0].SealedBy, "封印宿主＝本卡自身");
            Assert.AreEqual(top.UID, jc108.SealedCards[0].UID, "被封的正是牌库顶那张");
            Assert.AreEqual(CardZone.SealedZone, top.CurrentZone, "该牌进入封印区");
        }

        /// <summary>
        /// 【行为·BUG4·联机投影】服务端合法目标投影同样必须把**来源卡自身**下发给客户端
        /// （否则联机端"自己"没有被高亮、拖回自身被判取消）：
        /// `LegalityProjection` 的 `AbilityTargets` 中 JC108 那一条必须包含自身 UID。
        /// </summary>
        public void TestB3_4C_JC108联机投影含自身()
        {
            var game = NewGame();
            string area = FirstArea(game);
            game.TurnManager.PriorityPlayerId = 1;
            var jc108 = OnField(game, Load("JC108_qiaozhipazuozhimengren"), 1, area);

            var view = LegalityProjection.Build(game.CardManager, game.PlayerManager,
                game.TurnManager, game.ZoneManager, 1);
            Assert.IsNotNull(view, "应能构建客户端合法性投影");
            var entry = view.AbilityTargets.Find(x => x != null && x.SourceUid == jc108.UID);
            Assert.IsNotNull(entry, "投影应含 JC108 的激活目标条目");
            Assert.IsTrue(entry.TargetUids.Contains(jc108.UID),
                "投影中的合法目标**包含来源卡自身**（联机端因此可以点选自己）");
        }
    }
}
