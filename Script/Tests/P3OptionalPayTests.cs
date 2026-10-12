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
    /// 【K11】"可选支付"两条通道，按用户 2026-09 口径分别锁定：
    ///
    ///  **① 结算时的数值输入（既有 K11）**：`PayOptional{默认,最小,最大}` 作为**动作**在结算时执行——
    ///  弹**数值输入框**（输入的数字即为 N）→ 支付则扣 N 并继续后续动作；不支付/N=0/费用不足 →
    ///  不扣费且后续动作整段跳过（`_actionChainAborted`＝"若你如此做…"）。单机
    ///  `IGameUIHooks.PromptNumberInput`（动作链挂起续算）；联机 `ChoiceRequest.Kind.NumberInput`；
    ///  无 UI → 确定性兜底＝不支付。覆盖卡：**JC100 尖牙幻象**。
    ///
    ///  **② 触发入堆叠前的可选固定费用（2026-09 用户口径 JC061 修）**：`EffectData.OptionalPayCost=N` ——
    ///  这是"**额外费用**"性质：在**触发入堆叠之前**问一次"**是否支付 N**"（**只有是/否两个选项，
    ///  不需要输入金额**，选"是"就付 N）；**不支付 ⇒ 该触发不入堆叠**（整条效果不结算、也不扣费）；
    ///  **费用不足 → 不弹窗**，直接按"不支付"处理；放弃整条效果由**触发排序窗**负责（费用窗无"取消"）。
    ///  收口＝`TurnManager.EnsureOptionalPayThenPush` / `...Async`（位置在取目标之后、`PushTriggerToStack`
    ///  之前）。单机 `IGameUIHooks.PromptOptionalPay`；联机 `ChoiceRequest.Kind.ForceChoice` 两按钮列表；
    ///  无 UI/无网关 → 兜底＝不支付。覆盖卡：**JC061 危机处理特派小组**。
    ///  用户实测报障原文："JC061 的消灭暗藏者的支付①是**额外费用**，玩家把 JC061 加入堆叠时候就要
    ///  弹出选择是否支付。不支付的话结算时不结算。此外支付弹窗只需要**是否两个选项**，不需要输入支付的
    ///  费用，选是就支付①费。这里可能**用错了选择弹窗**。"
    /// </summary>
    public class P3OptionalPayTests : TestRunner
    {
        private const string Dir = "res://Cards/Data/PlayerCards/JC/";

        private static GameInitializer NewGame(int areas = 2)
        {
            var game = new GameInitializer { DebugSeed = 113 };
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

        private static PlayerCardData Probe(string uid, CardCategory category = CardCategory.Character)
        {
            var tmpl = new PlayerCardData
            {
                UID = uid, CardName = "探针_" + uid, Cost = 0,
                CategoryFlag = category, Defense = 1,
            };
            tmpl._effectsInjected = true;
            return tmpl;
        }

        /// <summary>给玩家放 n 张资产（提供无色费用与颜色忠诚）。</summary>
        private static void GiveAssets(GameInitializer game, int playerId, int n, CardColor color)
        {
            for (int i = 0; i < n; i++)
            {
                var tmpl = Probe($"TS_op_asset_{playerId}_{i}", CardCategory.Attachment);
                tmpl.Colors.Add(color);
                var asset = game.CardManager.CreateCardInstance(tmpl, playerId);
                game.ZoneManager.MoveToAsset(asset, playerId);
            }
        }

        private static EffectData FindEffect(PlayerCardData tmpl, string effectId)
        {
            if (tmpl?.EffectDataList == null) return null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.EffectId == effectId) return e;
            return null;
        }

        /// <summary>数值输入框桩：记录范围/默认值，并按脚本回包（-1＝不支付/取消）。</summary>
        private sealed class NumberStub : Script.Interfaces.IGameUIHooks
        {
            public int Answer = -1;
            public int Calls;
            public int LastMin, LastMax, LastDefault;
            public string LastConfirm, LastCancel, LastPrompt;

            public void PromptPayOrTap(CardInstance source, int payAmount) { }
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
            public void PromptPlayerTarget(int chooserPlayerId, string prompt, System.Collections.Generic.List<int> candidates, System.Action<int> onChosen)
                => onChosen?.Invoke(candidates != null && candidates.Count > 0 ? candidates[0] : -1);   // 【K24】桩：默认取第一个候选

            public void PromptEffectMode(CardInstance source, EffectData data, System.Action<int> onChosen)
                => onChosen?.Invoke(-1);

            public void PromptNumberInput(int chooserPlayerId, string prompt, int min, int max, int defaultValue,
                string confirmLabel, string cancelLabel, System.Action<int> onChosen)
            {
                Calls++;
                LastMin = min; LastMax = max; LastDefault = defaultValue;
                LastConfirm = confirmLabel; LastCancel = cancelLabel; LastPrompt = prompt;
                onChosen?.Invoke(Answer);
            }

            // ---- ② 触发入堆叠前的可选固定费用（JC061）：**是/否两选项，不输入金额** ----
            public bool PayAnswer;
            public int PayCalls;
            public int LastPayAmount;
            public string LastPayPrompt;

            public void PromptOptionalPay(int chooserPlayerId, string prompt, int amount, System.Action<bool> onChosen)
            {
                PayCalls++;
                LastPayAmount = amount;
                LastPayPrompt = prompt;
                onChosen?.Invoke(PayAnswer);
            }
        }

        // ==================== ① 结构 ====================

        public void TestOptionalPay_JC061结构()
        {
            var tmpl = Load("JC061_weijichulitepaixiaozu");
            Assert.IsNotNull(tmpl, "JC061 模板应可加载");
            var e = FindEffect(tmpl, "JC061_1_PayToDestroyCovertInArea");
            Assert.IsNotNull(e, "JC061 应有『支付1 → 消灭目标暗藏者』效果");
            Assert.AreEqual(EffectTiming.OnMove, e.Timing,
                "时机＝『进入一个地区』（OnMove：机动/跨地区移动**之后**；原挂 ManeuverTrigger 实为『对抗前窗口打开时』，移动尚未发生）");
            Assert.AreEqual(TargetKind.Covert, e.Target.Kind, "目标＝暗藏者");
            Assert.IsTrue(e.Target.IsLocalZone, "限该地区");
            // 【2026-09 用户口径·修】"支付1"＝**额外费用**（入堆叠前问是/否），不再是结算时的 `PayOptional` 动作
            Assert.AreEqual(1, e.OptionalPayCost, "费用＝可选固定 1（入堆叠前询问『是否支付 1』）");
            Assert.AreEqual(1, e.Actions.Count, "动作只剩『消灭』（支付改为入堆叠前的可选费用，不再占动作位）");
            Assert.AreEqual(ActionVerb.Destroy, e.Actions[0].Verb, "动作＝消灭");
        }

        // ==================== ② JC061：**入堆叠前**问『是否支付1』（是/否两选项） ====================
        // 口径（用户 2026-09）：支付＝额外费用，在**加入堆叠之前**问；不支付 ⇒ 该触发**不入堆叠**
        // （结算时自然什么都不发生）；放弃整条效果请用**触发排序窗**，费用窗不提供"取消"。

        /// <summary>把 JC061 摆到「进场地已有暗藏者」的场景，走**生产路径**（进场 → 收集 → 取目标 → 问支付）。</summary>
        private static CardInstance SetupJC061EnterWithCovert(GameInitializer game, out CardInstance unit,
            bool giveAssets = true, string covertUid = "TS_op_cov")
        {
            string area = Areas(game)[0];
            if (giveAssets) GiveAssets(game, 1, 3, (CardColor)4);   // JC061 颜色忠诚 4:2 → 3 张同色资产够

            var covert = game.CardManager.CreateCardInstance(Probe(covertUid), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            game.TurnManager.SetTriggerView(new PushAllTriggerView());
            unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            return covert;
        }

        /// <summary>① 支付：入堆叠前问一次"是否支付 1" → 扣费 → 入堆叠 → 结算消灭该地区暗藏者。</summary>
        public void TestOptionalPay_JC061入堆叠前问支付_支付则消灭()
        {
            var game = NewGame();
            var stub = new NumberStub { PayAnswer = true };
            EffectEngine.Instance.UIHooks = stub;

            var covert = SetupJC061EnterWithCovert(game, out var unit);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            game.ZoneManager.MoveToArea(unit, Areas(game)[0], 1);   // 进场 → OnMove 触发 → 取目标 → 问支付

            Assert.AreEqual(1, stub.PayCalls, "入堆叠前应问一次『是否支付 1』");
            Assert.AreEqual(1, stub.LastPayAmount, "金额＝1");
            Assert.IsTrue((stub.LastPayPrompt ?? "").Contains("是否支付"), "提示文本＝是否支付（不是数值输入框）");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 → 入堆叠前就扣清 1 费");
            Assert.IsFalse(game.StackManager.IsEmpty, "支付后该触发正常入堆叠");

            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.Graveyard, covert.CurrentZone, "结算时消灭该地区的目标暗藏者");
        }

        /// <summary>② 不支付：**该触发不入堆叠**（不扣费、结算时什么都不发生）。</summary>
        public void TestOptionalPay_JC061不支付则不入堆叠()
        {
            var game = NewGame();
            var stub = new NumberStub { PayAnswer = false };
            EffectEngine.Instance.UIHooks = stub;

            var covert = SetupJC061EnterWithCovert(game, out var unit, covertUid: "TS_op_cov2");
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            game.ZoneManager.MoveToArea(unit, Areas(game)[0], 1);

            Assert.AreEqual(1, stub.PayCalls, "仍应问一次『是否支付 1』");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不支付 → 不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "不支付 → **该触发不入堆叠**（整条效果不结算）");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "目标暗藏者仍在场上（消灭动作不会发生）");
        }

        /// <summary>③ 费用不足：**不弹窗**、直接按"不支付"处理（该触发不入堆叠；绝不替玩家花钱）。</summary>
        public void TestOptionalPay_JC061费用不足不弹窗按不支付()
        {
            var game = NewGame();
            var stub = new NumberStub { PayAnswer = true };   // 即使桩"愿意付"，也不该被问到
            EffectEngine.Instance.UIHooks = stub;

            var covert = SetupJC061EnterWithCovert(game, out var unit, giveAssets: false, covertUid: "TS_op_cov3");
            Assert.AreEqual(0, game.PlayerManager.GetAvailableMana(1), "前置：玩家当前可用费用＝0");
            game.ZoneManager.MoveToArea(unit, Areas(game)[0], 1);

            Assert.AreEqual(0, stub.PayCalls, "付不起 1 → **不弹窗**");
            Assert.IsTrue(game.StackManager.IsEmpty, "付不起 → 该触发不入堆叠");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "目标暗藏者仍在场上");
        }

        /// <summary>④ 无 UI（headless）→ 确定性兜底＝不支付（绝不替玩家花钱），该触发不入堆叠。</summary>
        public void TestOptionalPay_JC061无UI兜底不支付()
        {
            var game = NewGame();
            EffectEngine.Instance.UIHooks = null;

            var covert = SetupJC061EnterWithCovert(game, out var unit, covertUid: "TS_op_cov4");
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            game.ZoneManager.MoveToArea(unit, Areas(game)[0], 1);

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "兜底不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "兜底 → 不入堆叠");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "兜底不消灭");
        }

        // ==================== ②b JC061：**移动到另一地区**时触发，且目标是**新地区**的暗藏者 ====================
        // 回归（P1 缺陷）：原挂 `ManeuverTrigger`（＝对抗前窗口打开时、移动尚未发生）且被判成"取目标地区"，
        // 效果实际变成"消灭**移动前**地区（＝离开的地区）的暗藏者"，与卡文"进入一个地区…消灭位于**该地区**的
        // 目标暗藏者"不符。现挂 `OnMove`（位置变更**之后**广播，带 `SourceAreaAtTrigger`＝新地区）。

        public void TestOptionalPay_JC061移动后针对新地区触发()
        {
            var game = NewGame();
            var areas = Areas(game);
            GiveAssets(game, 1, 3, (CardColor)4);          // JC061 颜色忠诚 4:2

            var stub = new NumberStub { PayAnswer = true };                 // 支付 1
            EffectEngine.Instance.UIHooks = stub;
            game.TurnManager.SetTriggerView(new PushAllTriggerView());

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, areas[0], 1);   // 先进场（此时旧/新地区都还没有暗藏者）

            // 旧地区（出发地）与新地区（目的地）各放一个暗藏者
            var oldCovert = game.CardManager.CreateCardInstance(Probe("TS_op_jc061_old", CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(oldCovert, areas[0], 2);
            oldCovert.SetCovert(true);
            var newCovert = game.CardManager.CreateCardInstance(Probe("TS_op_jc061_new", CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(newCovert, areas[1], 2);
            newCovert.SetCovert(true);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            int callsBefore = stub.PayCalls;

            game.CardManager.MoveCardToArea(unit, areas[1]);   // 移动到新地区 → 广播 OnMove

            Assert.IsFalse(game.StackManager.IsEmpty, "『进入一个地区』应已收集触发并入堆叠");
            Assert.AreEqual(1, stub.PayCalls - callsBefore, "入堆叠前询问了一次是否支付 1");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 费");

            ResolveStackOnce(game);

            Assert.AreEqual(CardZone.Graveyard, newCovert.CurrentZone,
                "消灭的是**新进入地区**的暗藏者（回归：旧实现会打到出发地）");
            Assert.AreEqual(CardZone.AreaZone, oldCovert.CurrentZone,
                "出发地（移动前所在地区）的暗藏者不受影响");
        }

        /// <summary>
        /// 【2026-09 用户口径·修复】JC061 卡面 [前缀] 的**『机动』必须登记**（卡文"**机动** 触发：当危机处理特派小组
        /// 进入一个地区时…"）：`机动` 是对抗前窗口的动作（`ManeuverTrigger` + `Move` + 目标地区），
        /// 与后半段 `OnMove` 触发是**两条独立效果**。
        /// 用户实测报障："JC061 的机动效果没有触发"——根因＝只写了 `OnMove` 触发，**`机动` 关键词从未登记**
        /// （`.tres` 不承载关键词，关键词一律由卡脚本写进 `EffectDataList`，见 JC014/JC023 同款）
        /// ⇒ 对抗前窗口收集不到本卡任何效果，玩家根本没有"机动过去"这个动作可用。
        /// </summary>
        public void TestOptionalPay_JC061机动前缀已登记并在对抗前收集()
        {
            var tmpl = Load("JC061_weijichulitepaixiaozu");
            Assert.IsNotNull(tmpl, "JC061 模板应可加载");

            // ① 结构：存在『机动』（ManeuverTrigger + Move + 取地区目标）
            EffectData maneuver = null;
            foreach (var e in tmpl.EffectDataList)
                if (e != null && e.Timing == EffectTiming.ManeuverTrigger) { maneuver = e; break; }
            Assert.IsNotNull(maneuver, "JC061 应有『机动』效果（ManeuverTrigger）");
            Assert.AreEqual(TargetKind.Area, maneuver.Target.Kind, "机动目标＝地区");
            Assert.IsTrue(maneuver.Target.NeedsSelection, "机动需要选地区");
            Assert.AreEqual(ActionVerb.Move, maneuver.Actions[0].Verb, "机动动作＝移动");

            // ② 行为：生产路径 —— 对抗前优先权窗口真的把本卡的『机动』收集入堆叠
            var game = NewGame();
            string area = Areas(game)[0];
            game.TurnManager.SetTriggerView(new PushAllTriggerView());

            var unit = game.CardManager.CreateCardInstance(tmpl, 1);
            game.ZoneManager.MoveToArea(unit, area, 1);

            AdvanceToPhase(game, global::TurnManager.TurnPhase.PreCombat);

            Assert.IsNotNull(FindEntryPrefix(game, "JC061_0_Maneuver"),
                "对抗前窗口应收集到 JC061 的『机动』并入堆叠（修复前：一条也没有 ⇒ 机动不触发）");
        }

        public void TestOptionalPay_JC100结构()        {
            var tmpl = Load("JC100_jianyahuanxiang");
            Assert.IsNotNull(tmpl, "JC100 模板应可加载");
            var e = FindEffect(tmpl, "JC100_3_PayToWeakenSealingHost");
            Assert.IsNotNull(e, "JC100 应有『被封印时支付1 → 该角色-1防御』效果");
            Assert.AreEqual(EffectTiming.OnSealed, e.Timing, "时机＝被封印时");
            Assert.AreEqual("TriggerBySelf", e.Condition.Predicate, "条件＝本方封印动作");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝直到回合结束");
            // 【2026-10 用户口径】"支付1"＝**发动能力时**的额外费用（入堆叠前问是/否，两选项）
            Assert.AreEqual(1, e.OptionalPayCost, "费用＝发动时的可选固定 1（入堆叠前问『是否支付 1』）");
            Assert.AreEqual(1, e.Actions.Count, "动作只剩『-1 防御』（支付不再占结算动作位）");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[0].Verb, "动作＝获得（-1防御）");
            Assert.AreEqual("-1", e.Actions[0].Args[1], "防御 -1");
            Assert.AreEqual("封印宿主", e.Actions[0].Args[2], "主体＝封印宿主（『该角色』）");
        }

        // ==================== ③ JC100：被封印时支付 → 封印宿主 -1 防御（直到回合结束） ====================

        /// <summary>
        /// 【2026-10 用户口径】支付发生在**发动能力时**（封印动作 → 入堆叠之前问是/否）：
        /// 生产路径 `SealCard` 一趟走完后 ⇒ 已问一次"是否支付 1"、费用已扣、触发已入堆叠，
        /// 而**效果尚未结算**（宿主防御还没变）；随后结算才 -1 防御。
        /// </summary>
        public void TestOptionalPay_JC100支付则宿主减防御()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)5);   // JC100 颜色忠诚 5:1

            var hostTmpl = Probe("TS_op_host");
            hostTmpl.Defense = 3;   // 3 防御：-1 后可观察（1 防御会被减到 0 而死亡，死亡由行为批次用例覆盖）
            var host = game.CardManager.CreateCardInstance(hostTmpl, 2);
            game.ZoneManager.MoveToArea(host, area, 2);
            int defBefore = host.CurrentDefense;

            var fang = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToArea(fang, area, 1);

            var stub = new NumberStub { Answer = 1, PayAnswer = true };   // 【A117】固定金额 ⇒ 走二选一窗（PayAnswer）
            EffectEngine.Instance.UIHooks = stub;
            // 触发排序视图：把收集到的触发**真的入堆叠**（`EnterAllTriggerView` 是"只看不推"的空实现）
            game.TurnManager.SetTriggerView(new PushAllTriggerView());

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            // 封印：走唯一出口（会广播 OnSealed → 收集触发 → 问支付 → 入堆叠）
            Assert.IsTrue(game.TurnManager.SealCard(fang, host, false), "前置：封印成功");

            Assert.AreEqual(0, stub.Calls, "**不再走数值输入框**（卡文『你可以支付1』＝是/否 ⇒ 二选一窗；见 A117）");
            Assert.AreEqual(1, stub.PayCalls, "封印（发动）时应询问是否支付 1（二选一窗 `PromptOptionalPay`）");
            Assert.AreEqual(1, stub.LastPayAmount, "询问金额＝1");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 → **发动时**（入堆叠前）就扣 1 费");
            Assert.IsFalse(game.StackManager.IsEmpty, "支付后该触发入堆叠");
            Assert.AreEqual(defBefore, host.CurrentDefense, "尚未结算 ⇒ 宿主防御不变（费用在发动时付、效果在结算时做）");

            ResolveStackOnce(game);
            Assert.AreEqual(defBefore - 1, host.CurrentDefense, "结算后封印宿主（『该角色』）获得 -1 防御力");
        }

        // ==================== ④ 联机：触发入堆叠前 `Kind.ForceChoice` 两选项回包 ====================
        // 【2026-09 用户口径】联机复用既有**按钮列表**（`Kind.ForceChoice`）：面板 `BuildOptions` 对
        // ForceChoice **不追加"取消"按钮** ⇒ 严格只有"支付 N / 不支付"两个选项（用户："复用现有选择一项，
        // 不需要取消按钮，因为这是触发效果，玩家可以通过放弃框放弃效果，而不是取消"）。

        /// <summary>联机网关桩：自动通过触发排序/选目标，并按脚本回答"是否支付"（`Kind.ForceChoice`）。</summary>
        private sealed class OptionalPayGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public bool Pay = true;
            public bool CancelPayOnly;   // 只让"是否支付"这一问回取消/超时（模拟客户端不回包）

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req.RequestId };
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.SelectTriggerOrder)
                {
                    for (int i = 0; i < (req.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                    return System.Threading.Tasks.Task.FromResult(r);
                }
                if (req.RequestKind == Script.Net.ChoiceRequest.Kind.ForceChoice)
                {
                    if (CancelPayOnly) { r.Cancelled = true; return System.Threading.Tasks.Task.FromResult(r); }
                    // 两个按钮：0＝支付 / 1＝不支付（直接回协议下发的候选 UID，测试不硬编码哨兵）
                    r.SelectedUid = Pay ? req.OptionUids[0] : req.OptionUids[1];
                    return System.Threading.Tasks.Task.FromResult(r);
                }
                if (req.OptionUids != null && req.OptionUids.Count > 0) r.SelectedUid = req.OptionUids[0];
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        /// <summary>支付：服务端下发 `Kind.ForceChoice` 两按钮 → 回包"支付" → 扣费、入堆叠、结算消灭。</summary>
        public void TestOptionalPay_联机_两选项回包支付则消灭()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_on1"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            var gw = new OptionalPayGateway { Pay = true };
            game.SetInputGateway(gw);   // 联机：触发阶段走网关（异步路径，服务端权威）
            EffectEngine.Instance.UIHooks = null;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);   // 进场 → 收集触发 → 取目标 → 问支付

            Script.Net.ChoiceRequest payReq = null;
            foreach (var r in gw.Requests)
                if (r.RequestKind == Script.Net.ChoiceRequest.Kind.ForceChoice) payReq = r;
            Assert.IsNotNull(payReq, "应下发一次『是否支付1』请求（Kind.ForceChoice）");
            Assert.AreEqual(2, payReq.OptionUids.Count, "**只有两个选项**（面板对 ForceChoice 不追加『取消』）");
            Assert.AreEqual(2, payReq.OptionLabels.Count, "按钮文案＝两个");
            Assert.AreEqual("支付 1", payReq.OptionLabels[0], "按钮 1＝支付 1（不需要输入金额）");
            Assert.AreEqual("不支付", payReq.OptionLabels[1], "按钮 2＝不支付");

            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 → 入堆叠前扣清 1 费");
            Assert.IsFalse(game.StackManager.IsEmpty, "支付后该触发入堆叠");

            ResolveStackOnce(game);
            Assert.AreEqual(CardZone.Graveyard, covert.CurrentZone, "结算时消灭目标暗藏者");
        }

        /// <summary>不支付：该触发**不入堆叠**（不扣费、结算时什么都不发生）。</summary>
        public void TestOptionalPay_联机_回包不支付则不入堆叠()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_on2"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            var gw = new OptionalPayGateway { Pay = false };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不支付 → 不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "不支付 → **该触发不入堆叠**");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "目标暗藏者仍在场上");
        }

        /// <summary>回包取消/超时 → 按"不支付"处理（fail-closed，绝不替玩家花钱）。</summary>
        public void TestOptionalPay_联机_支付回包取消按不支付()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_on3"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);

            var gw = new OptionalPayGateway { CancelPayOnly = true };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "取消 → 不扣费");
            Assert.IsTrue(game.StackManager.IsEmpty, "取消 → 不入堆叠");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "目标暗藏者仍在场上");
        }

        /// <summary>协议往返：NumberInput 的 Kind / 范围字段 / 回包数值。</summary>
        public void TestOptionalPay_联机_协议字段往返()
        {
            var req = new Script.Net.ChoiceRequest
            {
                RequestId = "req_n", PlayerId = 1,
                RequestKind = Script.Net.ChoiceRequest.Kind.NumberInput,
                Prompt = "是否支付？可支付 0～3",
                NumberMin = 0, NumberMax = 3, NumberDefault = 1,
            };
            req.OptionLabels.Add("支付");
            req.OptionLabels.Add("不支付");

            var back = Script.Net.ChoiceSerializer.DeserializeRequest(Script.Net.ChoiceSerializer.Serialize(req));
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.NumberInput, back.RequestKind, "Kind 往返");
            Assert.AreEqual(0, back.NumberMin, "NumberMin 往返");
            Assert.AreEqual(3, back.NumberMax, "NumberMax 往返");
            Assert.AreEqual(1, back.NumberDefault, "NumberDefault 往返");
            Assert.AreEqual("支付", back.OptionLabels[0], "按钮文案往返");

            var res = new Script.Net.ChoiceResult { RequestId = "req_n" };
            res.SelectedAmounts.Add(2);
            var rback = Script.Net.ChoiceSerializer.DeserializeResult(Script.Net.ChoiceSerializer.SerializeResult(res));
            Assert.AreEqual(2, rback.SelectedAmounts[0], "回包数值 N 往返");
        }

        // ==================== 桩件 ====================

        private sealed class EnterAllGateway : Script.Net.IInputGateway
        {
            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                var r = new Script.Net.ChoiceResult { RequestId = req?.RequestId, Cancelled = false };
                for (int i = 0; i < (req?.OptionUids?.Count ?? 0); i++) r.SelectedIndices.Add(i);
                if (req?.OptionUids != null && req.OptionUids.Count > 0) r.SelectedUid = req.OptionUids[0];
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        private sealed class EnterAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone) { }
            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);
            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>触发视图：排序窗口**全部入堆叠**（观察"该时机是否真的收集到本卡效果"）。</summary>
        private sealed class PushAllTriggerView : Script.Interfaces.ITriggerView
        {
            public void OpenTriggerOrderForPlayer(int ownerPlayerId, List<Script.Data.Effects.StackEffectInstance> triggers,
                System.Action<List<Script.Data.Effects.StackEffectInstance>> onEnterStack, System.Action onDone)
            {
                onEnterStack?.Invoke(triggers);
                onDone?.Invoke();
            }

            public void RequestTargetSelection(Script.Data.Effects.StackEffectInstance trigger, List<string> validTargetUids,
                System.Action<string> onPicked)
                => onPicked?.Invoke(validTargetUids != null && validTargetUids.Count > 0 ? validTargetUids[0] : null);

            public void RemoveOrderEntries(List<Script.Data.Effects.StackEffectInstance> pushed) { }
        }

        /// <summary>连续让过推进到指定阶段（用于"对抗前优先权窗口"这类由阶段驱动的时机）。</summary>
        private static void AdvanceToPhase(GameInitializer game, global::TurnManager.TurnPhase phase)
        {
            for (int i = 0; i < 40 && game.TurnManager.CurrentPhase != phase; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        /// <summary>连续让过直到堆叠清空（触发/效果结算收尾）。</summary>
        private static void ResolveStackOnce(GameInitializer game)
        {
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);
        }

        private static StackEntry FindEntryPrefix(GameInitializer game, string prefix)
        {
            foreach (var e in game.StackManager.Entries)
                if (e.EffectDataEffect?.EffectId != null && e.EffectDataEffect.EffectId.StartsWith(prefix)) return e;
            return null;
        }
    }
}
