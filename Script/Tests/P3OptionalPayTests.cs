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
    /// 【K11·可选支付 + 数值输入框】按用户 2026-09 口径实现并锁定：
    ///  · 弹**数值输入框**：玩家输入一个数字 N，再选"支付"/"不支付"；**输入的数字即为 N**；
    ///  · 支付 → 扣 N 点无色费用并继续后续动作；**不支付 / N=0 / 费用不足 → 不扣费且后续动作整段跳过**
    ///    （复用既有 `_actionChainAborted`＝"若你如此做…"语义）；
    ///  · 单机：`IGameUIHooks.PromptNumberInput`（回调式，动作链**挂起续算**）；联机：`ChoiceRequest.Kind.NumberInput`
    ///    （回包 `SelectedAmounts[0]`）；无 UI → 确定性兜底＝**不支付**（绝不替玩家花钱）。
    /// 覆盖卡：**JC061 危机处理特派小组**（进入地区时可支付1 → 消灭该地区目标暗藏者）、
    ///        **JC100 尖牙幻象**（被封印时可支付1 → 该角色-1防御直到回合结束）。
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
            Assert.AreEqual(2, e.Actions.Count, "两个动作：可选支付 + 消灭");
            Assert.AreEqual(ActionVerb.PayOptional, e.Actions[0].Verb, "动作 1＝可选支付");
            Assert.AreEqual("1", e.Actions[0].Args[0], "默认/建议金额＝1");
            Assert.AreEqual("1", e.Actions[0].Args[2], "最大＝1（范围 0..1）");
            Assert.AreEqual(ActionVerb.Destroy, e.Actions[1].Verb, "动作 2＝消灭");
        }

        public void TestOptionalPay_JC100结构()
        {
            var tmpl = Load("JC100_jianyahuanxiang");
            Assert.IsNotNull(tmpl, "JC100 模板应可加载");
            var e = FindEffect(tmpl, "JC100_3_PayToWeakenSealingHost");
            Assert.IsNotNull(e, "JC100 应有『被封印时支付1 → 该角色-1防御』效果");
            Assert.AreEqual(EffectTiming.OnSealed, e.Timing, "时机＝被封印时");
            Assert.AreEqual("TriggerBySelf", e.Condition.Predicate, "条件＝本方封印动作");
            Assert.AreEqual("直到回合结束", e.Duration, "时效＝直到回合结束");
            Assert.AreEqual(ActionVerb.PayOptional, e.Actions[0].Verb, "动作 1＝可选支付");
            Assert.AreEqual(ActionVerb.Gain, e.Actions[1].Verb, "动作 2＝获得（-1防御）");
            Assert.AreEqual("-1", e.Actions[1].Args[1], "防御 -1");
            Assert.AreEqual("封印宿主", e.Actions[1].Args[2], "主体＝封印宿主（『该角色』）");
        }

        // ==================== ② JC061：支付 → 消灭；不支付 → 不消灭、不扣费 ====================

        /// <summary>支付 1 → 扣费且目标暗藏者被消灭。</summary>
        public void TestOptionalPay_JC061支付则消灭()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);   // JC061 颜色忠诚 4:2 → 3 张同色资产够

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_cov1"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var stub = new NumberStub { Answer = 1 };
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(unit.PlayerTemplate, "JC061_1_PayToDestroyCovertInArea");
            EffectEngine.Instance.ResolveEffect(e, unit, covert, area);

            Assert.AreEqual(1, stub.Calls, "应弹出一次数值输入框");
            Assert.AreEqual(0, stub.LastMin, "最小值＝0");
            Assert.AreEqual(1, stub.LastMax, "最大值＝1（默认金额）");
            Assert.AreEqual(1, stub.LastDefault, "默认值＝1");
            Assert.AreEqual("支付", stub.LastConfirm, "确认按钮＝支付");
            Assert.AreEqual("不支付", stub.LastCancel, "取消按钮＝不支付");

            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 → 可用费用 -1");
            Assert.AreEqual(CardZone.Graveyard, covert.CurrentZone, "支付后目标暗藏者被消灭（进墓地）");
        }

        /// <summary>选择不支付 → 不扣费、后续动作（消灭）整段跳过。</summary>
        public void TestOptionalPay_JC061不支付则跳过后续()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_cov2"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var stub = new NumberStub { Answer = -1 };   // 不支付
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(unit.PlayerTemplate, "JC061_1_PayToDestroyCovertInArea");
            EffectEngine.Instance.ResolveEffect(e, unit, covert, area);

            Assert.AreEqual(1, stub.Calls, "仍应询问一次");
            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "不支付 → 不扣费");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "**不支付 → 消灭动作整段跳过**（目标仍在场上）");
        }

        /// <summary>无 UI（headless）→ 确定性兜底：不支付（绝不替玩家花钱），后续跳过。</summary>
        public void TestOptionalPay_无UI兜底不支付()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_cov3"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            EffectEngine.Instance.UIHooks = null;
            var e = FindEffect(unit.PlayerTemplate, "JC061_1_PayToDestroyCovertInArea");
            EffectEngine.Instance.ResolveEffect(e, unit, covert, area);

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "兜底不扣费");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "兜底不消灭");
        }

        /// <summary>输入 0 等价于"不支付"（范围含 0，但 0 不构成"支付"）。</summary>
        public void TestOptionalPay_输入0视为不支付()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_cov4"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            EffectEngine.Instance.UIHooks = new NumberStub { Answer = 0 };
            var e = FindEffect(unit.PlayerTemplate, "JC061_1_PayToDestroyCovertInArea");
            EffectEngine.Instance.ResolveEffect(e, unit, covert, area);

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "N=0 → 视为未支付，不扣费");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "N=0 → 后续动作跳过");
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

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, areas[0], 1);   // 先进场（此时 OnMove 也会发，但旧/新地区都还没有暗藏者）

            // 旧地区（出发地）与新地区（目的地）各放一个暗藏者
            var oldCovert = game.CardManager.CreateCardInstance(Probe("TS_op_jc061_old", CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(oldCovert, areas[0], 2);
            oldCovert.SetCovert(true);
            var newCovert = game.CardManager.CreateCardInstance(Probe("TS_op_jc061_new", CardCategory.Character), 2);
            game.ZoneManager.MoveToArea(newCovert, areas[1], 2);
            newCovert.SetCovert(true);

            var stub = new NumberStub { Answer = 1 };      // 支付 1
            EffectEngine.Instance.UIHooks = stub;
            game.SetInputGateway(new EnterAllGateway());   // 触发阶段走网关（全部入堆叠 + 自动取首个候选）
            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            int callsBefore = stub.Calls;

            game.CardManager.MoveCardToArea(unit, areas[1]);   // 移动到新地区 → 广播 OnMove

            Assert.IsFalse(game.StackManager.IsEmpty, "『进入一个地区』应已收集触发并入堆叠");
            for (int i = 0; i < 8 && !game.StackManager.IsEmpty; i++)
                game.TurnManager.Pass(game.TurnManager.PriorityPlayerId);

            Assert.AreEqual(1, stub.Calls - callsBefore, "询问了一次是否支付 1");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 费");
            Assert.AreEqual(CardZone.Graveyard, newCovert.CurrentZone,
                "消灭的是**新进入地区**的暗藏者（回归：旧实现会打到出发地）");
            Assert.AreEqual(CardZone.AreaZone, oldCovert.CurrentZone,
                "出发地（移动前所在地区）的暗藏者不受影响");
        }

        // ==================== ③ JC100：被封印时支付 → 封印宿主 -1 防御（直到回合结束） ====================

        public void TestOptionalPay_JC100支付则宿主减防御()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)5);   // JC100 颜色忠诚 5:1

            var host = game.CardManager.CreateCardInstance(Probe("TS_op_host"), 2);
            game.ZoneManager.MoveToArea(host, area, 2);
            int defBefore = host.CurrentDefense;

            var fang = game.CardManager.CreateCardInstance(Load("JC100_jianyahuanxiang"), 1);
            game.ZoneManager.MoveToArea(fang, area, 1);

            // 封印：走唯一出口（会广播 OnSealed）
            game.TurnManager.SetTriggerView(new EnterAllTriggerView());
            game.SetInputGateway(new EnterAllGateway());
            game.TurnManager.SealCard(fang, host, false);

            int manaBefore = game.PlayerManager.GetAvailableMana(1);
            var stub = new NumberStub { Answer = 1 };
            EffectEngine.Instance.UIHooks = stub;

            var e = FindEffect(fang.PlayerTemplate, "JC100_3_PayToWeakenSealingHost");
            Assert.IsNotNull(e, "JC100 应有被封印时效果");
            EffectEngine.Instance.ResolveEffect(e, fang, null, area, fang.ControllerId);

            Assert.AreEqual(1, stub.Calls, "应询问是否支付 1");
            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 → 扣 1 费");
            Assert.AreEqual(defBefore - 1, host.CurrentDefense, "封印宿主（『该角色』）获得 -1 防御力");
        }

        // ==================== ④ 联机：NumberInput 请求 → 回包 SelectedAmounts[0] ====================

        private sealed class NumberGateway : Script.Net.IInputGateway
        {
            public readonly List<Script.Net.ChoiceRequest> Requests = new List<Script.Net.ChoiceRequest>();
            public int Answer;
            public bool Cancel;

            public System.Threading.Tasks.Task<Script.Net.ChoiceResult> RequestChoice(int playerId, Script.Net.ChoiceRequest req)
            {
                Requests.Add(req);
                var r = new Script.Net.ChoiceResult { RequestId = req.RequestId, Cancelled = Cancel };
                if (!Cancel) r.SelectedAmounts.Add(Answer);
                return System.Threading.Tasks.Task.FromResult(r);
            }
        }

        public void TestOptionalPay_联机_数值输入回包驱动()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_on1"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new NumberGateway { Answer = 1 };
            game.SetInputGateway(gw);   // 生产装配路径：同时注入引擎与回合管理器（引擎异步路径读 EE.InputGateway）
            EffectEngine.Instance.UIHooks = null;   // 联机走网关分支

            var e = FindEffect(unit.PlayerTemplate, "JC061_1_PayToDestroyCovertInArea");
            EffectEngine.Instance.ResolveEffectAsync(e, unit, covert, area).GetAwaiter().GetResult();

            Assert.AreEqual(1, gw.Requests.Count, "应下发一次 NumberInput 请求");
            Assert.AreEqual(Script.Net.ChoiceRequest.Kind.NumberInput, gw.Requests[0].RequestKind, "请求 Kind＝NumberInput");
            Assert.AreEqual(0, gw.Requests[0].NumberMin, "NumberMin 下发");
            Assert.AreEqual(1, gw.Requests[0].NumberMax, "NumberMax 下发");
            Assert.AreEqual(1, gw.Requests[0].NumberDefault, "NumberDefault 下发");
            Assert.AreEqual(2, gw.Requests[0].OptionLabels.Count, "按钮文案＝支付/不支付");

            Assert.AreEqual(manaBefore - 1, game.PlayerManager.GetAvailableMana(1), "支付 1 → 扣费");
            Assert.AreEqual(CardZone.Graveyard, covert.CurrentZone, "目标被消灭");
        }

        public void TestOptionalPay_联机_取消则不扣费且跳过()
        {
            var game = NewGame();
            string area = Areas(game)[0];
            GiveAssets(game, 1, 3, (CardColor)4);

            var unit = game.CardManager.CreateCardInstance(Load("JC061_weijichulitepaixiaozu"), 1);
            game.ZoneManager.MoveToArea(unit, area, 1);
            var covert = game.CardManager.CreateCardInstance(Probe("TS_op_on2"), 2);
            game.ZoneManager.MoveToArea(covert, area, 2);
            covert.SetCovert(true);
            int manaBefore = game.PlayerManager.GetAvailableMana(1);

            var gw = new NumberGateway { Cancel = true };
            game.SetInputGateway(gw);
            EffectEngine.Instance.UIHooks = null;

            var e = FindEffect(unit.PlayerTemplate, "JC061_1_PayToDestroyCovertInArea");
            EffectEngine.Instance.ResolveEffectAsync(e, unit, covert, area).GetAwaiter().GetResult();

            Assert.AreEqual(manaBefore, game.PlayerManager.GetAvailableMana(1), "取消 → 不扣费");
            Assert.AreEqual(CardZone.AreaZone, covert.CurrentZone, "取消 → 后续动作跳过");
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
    }
}
